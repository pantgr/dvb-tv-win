using System.Text;

namespace DvbTv.Services;

/// <summary>A DVB service (TV channel) discovered from the SDT/PAT.</summary>
public sealed class DvbService
{
    public int ServiceId { get; init; }
    public string Name { get; init; } = "";
    public string Provider { get; init; } = "";
}

/// <summary>
/// Minimal MPEG-TS PSI parser: service names from the SDT (PID 0x11, table 0x42)
/// and the program list from the PAT (PID 0x00, table 0x00). Enough to name
/// channels and drive VLC's :program=&lt;sid&gt; selection. Not a full SI decoder.
/// </summary>
public static class PsiParser
{
    private const int TsPacketSize = 188;
    private const int SdtPid = 0x11;
    private const int PatPid = 0x00;

    static PsiParser()
    {
        // Needed for ISO-8859-7 (Greek) service names on .NET Core.
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { /* best effort */ }
    }

    public static IReadOnlyList<DvbService> ParseServices(byte[] ts, int length)
    {
        var patSids = ParsePat(ts, length);          // service ids actually present in this mux
        var sdt = ParseSdt(ts, length);              // service id -> (name, provider)

        var list = new List<DvbService>();
        foreach (var kv in sdt)
        {
            if (patSids.Count > 0 && !patSids.Contains(kv.Key)) continue; // SDT may list other muxes
            list.Add(new DvbService { ServiceId = kv.Key, Name = kv.Value.name, Provider = kv.Value.provider });
        }
        foreach (var sid in patSids)
            if (!sdt.ContainsKey(sid))
                list.Add(new DvbService { ServiceId = sid, Name = $"Service {sid}" });

        return list.OrderBy(s => s.ServiceId).ToList();
    }

    private static HashSet<int> ParsePat(byte[] ts, int length)
    {
        var sids = new HashSet<int>();
        foreach (var s in ExtractSections(ts, length, PatPid))
        {
            if (s.Length < 8 || s[0] != 0x00) continue;
            int sectionLength = ((s[1] & 0x0F) << 8) | s[2];
            int end = 3 + sectionLength - 4; // minus CRC32
            for (int p = 8; p + 4 <= end; p += 4)
            {
                int programNumber = (s[p] << 8) | s[p + 1];
                if (programNumber != 0) sids.Add(programNumber); // 0 = NIT
            }
        }
        return sids;
    }

    private static Dictionary<int, (string name, string provider)> ParseSdt(byte[] ts, int length)
    {
        var map = new Dictionary<int, (string, string)>();
        foreach (var s in ExtractSections(ts, length, SdtPid))
        {
            if (s.Length < 12 || s[0] != 0x42) continue; // 0x42 = SDT actual TS
            int sectionLength = ((s[1] & 0x0F) << 8) | s[2];
            int end = 3 + sectionLength - 4; // minus CRC32
            int p = 11; // after table header + onid + reserved
            while (p + 5 <= end)
            {
                int serviceId = (s[p] << 8) | s[p + 1];
                int descLoopLen = ((s[p + 3] & 0x0F) << 8) | s[p + 4];
                int dp = p + 5;
                int dEnd = dp + descLoopLen;
                while (dp + 2 <= dEnd && dp + 2 <= end)
                {
                    int tag = s[dp];
                    int dlen = s[dp + 1];
                    if (tag == 0x48 && dp + 2 + dlen <= end) // service_descriptor
                    {
                        int q = dp + 2;
                        q += 1; // service_type
                        int provLen = s[q++];
                        string provider = DecodeDvbString(s, q, provLen);
                        q += provLen;
                        int nameLen = s[q++];
                        string name = DecodeDvbString(s, q, nameLen);
                        if (!string.IsNullOrWhiteSpace(name) && !map.ContainsKey(serviceId))
                            map[serviceId] = (name, provider);
                    }
                    dp += 2 + dlen;
                }
                p = dEnd;
            }
        }
        return map;
    }

    /// <summary>
    /// Find the byte offset where the TS sync byte (0x47) repeats every 188 bytes.
    /// The captured buffer is NOT guaranteed to start on a packet boundary (the sample
    /// grabber delivers shifted buffers after a re-tune), so we must lock onto the grid.
    /// </summary>
    private static int FindSyncOffset(byte[] ts, int length)
    {
        for (int off = 0; off < TsPacketSize && off + TsPacketSize * 5 <= length; off++)
            if (ts[off] == 0x47 && ts[off + 188] == 0x47 && ts[off + 376] == 0x47 &&
                ts[off + 564] == 0x47 && ts[off + 752] == 0x47)
                return off;
        return 0;
    }

    /// <summary>Reassemble complete PSI sections for a PID from a raw TS buffer.</summary>
    private static List<byte[]> ExtractSections(byte[] ts, int length, int wantPid)
    {
        var sections = new List<byte[]>();
        List<byte>? cur = null;
        int need = 0;

        for (int i = FindSyncOffset(ts, length); i + TsPacketSize <= length; i += TsPacketSize)
        {
            if (ts[i] != 0x47) continue;
            int pid = ((ts[i + 1] & 0x1F) << 8) | ts[i + 2];
            if (pid != wantPid) continue;

            bool pusi = (ts[i + 1] & 0x40) != 0;
            int afc = (ts[i + 3] >> 4) & 0x3;
            int p = i + 4;
            if (afc == 0 || afc == 2) continue;          // no payload
            if (afc == 3) p += 1 + ts[i + 4];            // skip adaptation field
            int packetEnd = i + TsPacketSize;
            if (p >= packetEnd) continue;

            if (pusi)
            {
                int ptr = ts[p]; p += 1 + ptr;           // pointer_field
                if (p > packetEnd) continue;
                cur = new List<byte>(); need = 0;
                for (int k = p; k < packetEnd; k++) cur.Add(ts[k]);
            }
            else
            {
                if (cur == null) continue;
                for (int k = p; k < packetEnd; k++) cur.Add(ts[k]);
            }

            if (need == 0 && cur.Count >= 3)
            {
                if (cur[0] == 0xFF) { cur = null; continue; } // stuffing
                need = (((cur[1] & 0x0F) << 8) | cur[2]) + 3;
            }
            if (need > 0 && cur.Count >= need)
            {
                sections.Add(cur.GetRange(0, need).ToArray());
                cur = null; need = 0;
            }
        }
        return sections;
    }

    /// <summary>Decode a DVB SI text string (handles the encoding-selection first byte).</summary>
    internal static string DecodeDvbString(byte[] data, int offset, int len)
    {
        if (len <= 0 || offset + len > data.Length) return "";
        int start = offset, count = len;
        Encoding enc;
        byte first = data[offset];
        if (first >= 0x20)
        {
            enc = Encoding.Latin1; // default DVB table (ISO-6937 approx)
        }
        else
        {
            enc = first switch
            {
                0x01 => GetEnc(28595), // ISO-8859-5 Cyrillic
                0x03 => GetEnc(28597), // ISO-8859-7 Greek
                0x05 => GetEnc(28599), // ISO-8859-9 Turkish
                0x15 => Encoding.UTF8,
                _ => Encoding.Latin1,
            };
            start = offset + 1; count = len - 1;
        }
        try { return enc.GetString(data, start, count).Trim(); }
        catch { return Encoding.Latin1.GetString(data, start, count).Trim(); }
    }

    private static Encoding GetEnc(int codepage)
    {
        try { return Encoding.GetEncoding(codepage); } catch { return Encoding.Latin1; }
    }
}
