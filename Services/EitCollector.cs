using DvbTv.Models;

namespace DvbTv.Services;

/// <summary>
/// Continuously collects EPG from the DVB EIT (PID 0x12): present/following
/// (table 0x4E) AND schedule (tables 0x50–0x5F), actual TS. Fed packet buffers by
/// the sample-grabber thread (taps the live TS without disturbing the VLC pipe);
/// read from the UI thread.
///
/// Section-assembly state (_cur/_need) is touched ONLY on the grabber thread
/// (single producer); the per-service event maps are guarded by _lock.
/// </summary>
public sealed class EitCollector
{
    private const int EitPid = 0x12;
    private const int TsPacketSize = 188;

    private readonly object _lock = new();
    // service id -> (event id -> event), deduplicated across p/f + schedule sections.
    private readonly Dictionary<int, Dictionary<int, DvbEvent>> _events = new();

    private readonly List<byte> _cur = new(4096);
    private int _need;

    public void FeedBuffer(ReadOnlySpan<byte> ts)
    {
        for (int i = FindSync(ts); i + TsPacketSize <= ts.Length; i += TsPacketSize)
        {
            if (ts[i] != 0x47) continue;
            int pid = ((ts[i + 1] & 0x1F) << 8) | ts[i + 2];
            if (pid != EitPid) continue;

            bool pusi = (ts[i + 1] & 0x40) != 0;
            int afc = (ts[i + 3] >> 4) & 0x3;
            int p = i + 4;
            if (afc == 0 || afc == 2) continue;
            if (afc == 3) p += 1 + ts[i + 4];
            int end = i + TsPacketSize;
            if (p >= end) continue;

            if (pusi)
            {
                int ptr = ts[p]; p += 1 + ptr;
                if (p > end) continue;
                _cur.Clear(); _need = 0;
                for (int k = p; k < end; k++) _cur.Add(ts[k]);
            }
            else
            {
                if (_cur.Count == 0) continue;
                for (int k = p; k < end; k++) _cur.Add(ts[k]);
            }

            if (_need == 0 && _cur.Count >= 3)
            {
                if (_cur[0] == 0xFF) { _cur.Clear(); continue; }
                _need = (((_cur[1] & 0x0F) << 8) | _cur[2]) + 3;
            }
            if (_need > 0 && _cur.Count >= _need)
            {
                try { ParseSection(_cur.GetRange(0, _need).ToArray()); } catch { /* skip bad section */ }
                _cur.Clear(); _need = 0;
            }
        }
    }

    private void ParseSection(byte[] s)
    {
        int tableId = s[0];
        // 0x4E/0x4F = present-following (actual/other TS); 0x50–0x6F = schedule (actual/other TS).
        bool isEit = tableId == 0x4E || tableId == 0x4F || (tableId >= 0x50 && tableId <= 0x6F);
        if (!isEit || s.Length < 14) return;

        int serviceId = (s[3] << 8) | s[4];
        int sectionLength = ((s[1] & 0x0F) << 8) | s[2];
        int end = 3 + sectionLength - 4; // minus CRC32
        int p = 14;

        while (p + 12 <= end)
        {
            int eventId = (s[p] << 8) | s[p + 1];
            DateTime start = DecodeStart(s, p + 2);
            TimeSpan dur = DecodeDuration(s, p + 9);
            int descLen = ((s[p + 10] & 0x0F) << 8) | s[p + 11];
            int dp = p + 12, dEnd = Math.Min(dp + descLen, end);

            string name = "", text = "";
            while (dp + 2 <= dEnd)
            {
                int tag = s[dp], dl = s[dp + 1];
                if (tag == 0x4D && dp + 2 + dl <= dEnd) // short_event_descriptor
                {
                    int q = dp + 2 + 3; // skip ISO-639 language
                    int nameLen = s[q++];
                    name = PsiParser.DecodeDvbString(s, q, nameLen); q += nameLen;
                    int textLen = s[q++];
                    text = PsiParser.DecodeDvbString(s, q, textLen);
                }
                dp += 2 + dl;
            }

            if (start != default && dur > TimeSpan.Zero)
            {
                var ev = new DvbEvent { ServiceId = serviceId, EventId = eventId, StartUtc = start, Duration = dur, Name = name, Description = text };
                lock (_lock)
                {
                    if (!_events.TryGetValue(serviceId, out var map))
                        _events[serviceId] = map = new Dictionary<int, DvbEvent>();
                    map[eventId] = ev;
                }
            }
            p += 12 + descLen;
        }
    }

    /// <summary>All known events for a service, sorted by start time.</summary>
    public IReadOnlyList<DvbEvent> GetSchedule(int serviceId)
    {
        lock (_lock)
        {
            if (!_events.TryGetValue(serviceId, out var map) || map.Count == 0)
                return Array.Empty<DvbEvent>();
            var list = new List<DvbEvent>(map.Values);
            list.Sort((a, b) => a.StartUtc.CompareTo(b.StartUtc));
            return list;
        }
    }

    public void Clear()
    {
        lock (_lock) _events.Clear();
    }

    private static int FindSync(ReadOnlySpan<byte> ts)
    {
        for (int off = 0; off < TsPacketSize && off + TsPacketSize * 5 <= ts.Length; off++)
            if (ts[off] == 0x47 && ts[off + 188] == 0x47 && ts[off + 376] == 0x47 &&
                ts[off + 564] == 0x47 && ts[off + 752] == 0x47)
                return off;
        return 0;
    }

    private static int Bcd(byte b) => ((b >> 4) * 10) + (b & 0x0F);

    private static DateTime DecodeStart(byte[] d, int o)
    {
        int mjd = (d[o] << 8) | d[o + 1];
        if (mjd == 0 || mjd == 0xFFFF) return default;
        int hh = Bcd(d[o + 2]), mm = Bcd(d[o + 3]), ss = Bcd(d[o + 4]);
        int yp = (int)((mjd - 15078.2) / 365.25);
        int mp = (int)((mjd - 14956.1 - (int)(yp * 365.25)) / 30.6001);
        int day = mjd - 14956 - (int)(yp * 365.25) - (int)(mp * 30.6001);
        int k = (mp == 14 || mp == 15) ? 1 : 0;
        int year = yp + k + 1900;
        int month = mp - 1 - k * 12;
        try { return new DateTime(year, month, day, hh, mm, ss, DateTimeKind.Utc); }
        catch { return default; }
    }

    private static TimeSpan DecodeDuration(byte[] d, int o)
    {
        try { return new TimeSpan(Bcd(d[o]), Bcd(d[o + 1]), Bcd(d[o + 2])); }
        catch { return TimeSpan.Zero; }
    }
}
