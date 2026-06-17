using DvbTv.Models;
using Microsoft.Extensions.Logging;
using RtlDvb;

namespace DvbTv.Services;

/// <summary>
/// WinUSB implementation of the tuner: drives the RTL2832U's built-in hardware DVB-T
/// demodulator directly (libusb/WinUSB register programming, ported from AndroidDvbDriver),
/// instead of DirectShow BDA. The chip emits MPEG-TS over bulk endpoint 0x81; a background
/// reader thread pumps it into the same TsPipeStream + EitCollector the BDA path uses, so the
/// VLC player, PSI scanner, EPG and UI are all unchanged.
///
/// Stick must be on the WinUSB (Zadig) driver, not the BDA driver — the two are mutually
/// exclusive. Verified end-to-end: lock @ 514 MHz, SNR 31 dB, 22.3 Mbps, valid 188-byte TS.
/// </summary>
public sealed class RtlSdrDvbTuner : IDvbTuner
{
    private readonly ILogger<RtlSdrDvbTuner> _log;
    private readonly RtlDevice _dev = new();
    private readonly EitCollector _eit = new();
    private readonly object _ctrlLock = new(); // serialize control-path ops (tune/stats) vs each other
    private readonly TsPipeStream _pipe = new(20 * 1024 * 1024);

    private Thread? _reader;
    private volatile bool _streaming;
    private bool _opened;

    public RtlSdrDvbTuner(ILogger<RtlSdrDvbTuner> log) => _log = log;

    public Stream? TransportStream => _pipe;

    /// <returns>true if the device was just opened (cold) on this call.</returns>
    private bool EnsureOpen()
    {
        if (_opened) return false;
        _log.LogInformation("Opening RTL2832U via WinUSB…");
        _dev.Open();
        var t0 = Environment.TickCount64;
        _dev.Initialize(); // power, detect R820T, attach demod+tuner, IMR calibration, EP init
        _opened = true;
        _log.LogInformation("RTL2832U up ({Ms} ms), tuner={Tuner}", Environment.TickCount64 - t0, _dev.TunerName);
        return true;
    }

    public async Task<bool> TuneAsync(TuneParameters p, CancellationToken ct = default)
    {
        bool coldStart = EnsureOpen();
        StopReader();         // pause streaming so control transfers don't race the bulk reader
        _pipe.Clear();

        // Cold start: right after IMR calibration the demod hasn't settled its PIDs yet, so VLC's
        // first probe locks onto a partial stream (audio-only, no video) → black "needs 2 clicks".
        // Do one full tune+stream cycle silently here so the demod stabilises BEFORE VLC sees it.
        if (coldStart)
        {
            _log.LogInformation("Cold start — warming up demod @ {Mhz:F0} MHz…", p.FrequencyHz / 1_000_000.0);
            lock (_ctrlLock) _dev.Tune(p.FrequencyHz, p.BandwidthMhz * 1_000_000L);
            for (int i = 0; i < 30 && !ct.IsCancellationRequested; i++)
            {
                await Task.Delay(100, ct);
                if (GetSignalStats().Locked) break;
            }
            lock (_ctrlLock) _dev.DisablePidFilter();
            StartReader();
            await Task.Delay(2000, ct); // let the multiplex stabilise (all PIDs flowing)
            StopReader();
            _pipe.Clear();
        }

        double mhz = p.FrequencyHz / 1_000_000.0;
        lock (_ctrlLock) _dev.Tune(p.FrequencyHz, p.BandwidthMhz * 1_000_000L);

        // Block on the real demod FSM lock (not an arbitrary sleep). Cold lock measured ~0.3-0.6s.
        int lockIters = Math.Max(1, p.LockTimeoutMs / 100);
        bool locked = false;
        for (int i = 0; i < lockIters && !ct.IsCancellationRequested && !locked; i++)
        {
            await Task.Delay(100, ct);
            locked = GetSignalStats().Locked;
        }

        if (!locked) { _log.LogWarning("No lock @ {Mhz:F0} MHz", mhz); return false; }

        var s = GetSignalStats();
        _log.LogInformation("Locked @ {Mhz:F0} MHz: strength={S}% SNR={Snr:F1} dB", mhz, s.StrengthPercent, s.SnrDb);

        lock (_ctrlLock) _dev.DisablePidFilter(); // full MPTS
        _pipe.Clear();
        StartReader();

        // Pre-buffer post-lock TS before play so VLC starts on a full ring (same rationale as BDA path).
        if (p.Prebuffer)
        {
            const int minBytes = 6 * 1024 * 1024, maxBytes = 12 * 1024 * 1024;
            int targetBytes = minBytes;
            long t0 = Environment.TickCount64, deadline = t0 + 6000, rate = 0;
            while (_pipe.Available < targetBytes && Environment.TickCount64 < deadline && !ct.IsCancellationRequested)
            {
                await Task.Delay(50, ct);
                long elapsed = Environment.TickCount64 - t0;
                if (elapsed >= 1000)
                {
                    rate = _pipe.Available * 1000L / elapsed;
                    targetBytes = (int)Math.Clamp(rate * 35 / 10, minBytes, maxBytes);
                }
            }
            double bufferedSec = rate > 0 ? (double)_pipe.Available / rate : 0;
            _log.LogInformation("Prebuffered {KB} KB (~{Sec:F1}s @ {Mbps:F1} Mbps) before play",
                _pipe.Available / 1024, bufferedSec, rate * 8 / 1e6);
        }
        return true;
    }

    private void StartReader()
    {
        _streaming = true;
        _reader = new Thread(ReaderLoop) { IsBackground = true, Name = "rtl-ts-reader" };
        _reader.Start();
    }

    private void StopReader()
    {
        _streaming = false;
        _reader?.Join(2000);
        _reader = null;
    }

    private void ReaderLoop()
    {
        var buf = new byte[256 * 1024];
        long bytes = 0;
        int buffers = 0, zeroReads = 0;
        // per-window instrumentation to pin down stutter cause: USB read time, EIT-parse time, ring drain
        long readMs = 0, feedMs = 0, readMaxMs = 0;
        int ringMin = int.MaxValue, ringMax = 0;
        var sw = new System.Diagnostics.Stopwatch();
        _log.LogInformation("TS reader started");
        while (_streaming)
        {
            int n;
            sw.Restart();
            try { n = _dev.ReadBulk(buf, 1000); }
            catch (Exception ex) { _log.LogWarning(ex, "bulk read failed; stopping reader"); break; }
            long rMs = sw.ElapsedMilliseconds;
            readMs += rMs; if (rMs > readMaxMs) readMaxMs = rMs;
            if (n <= 0) { zeroReads++; continue; }

            var span = new ReadOnlySpan<byte>(buf, 0, n);
            _pipe.Append(span);
            sw.Restart();
            _eit.FeedBuffer(span);
            feedMs += sw.ElapsedMilliseconds;
            bytes += n;

            int fill = _pipe.Available * 100 / _pipe.Capacity;
            if (fill < ringMin) ringMin = fill;
            if (fill > ringMax) ringMax = fill;

            if (++buffers % 50 == 0)
            {
                _log.LogInformation(
                    "TS reader: {MB:F1} MB | ring {Min}-{Max}% | bulk avg {RAvg}ms max {RMax}ms | eit-parse avg {FAvg}ms | zeroReads {Zero}",
                    bytes / 1e6, ringMin, ringMax, readMs / 50, readMaxMs, feedMs / 50, zeroReads);
                readMs = feedMs = readMaxMs = 0; zeroReads = 0; ringMin = int.MaxValue; ringMax = 0;
            }
        }
        _log.LogInformation("TS reader stopped ({MB:F1} MB)", bytes / 1e6);
    }

    public SignalStats GetSignalStats()
    {
        try
        {
            lock (_ctrlLock)
            {
                bool locked = _dev.GetStatus().Contains(RtlDvb.DvbStatus.FE_HAS_LOCK);
                if (!locked) return SignalStats.NoLock;
                double snrDb = _dev.ReadSnr() / 10.0;
                int strength = _dev.ReadRfStrength();
                int quality = Math.Clamp((int)(snrDb * 100 / 35), 0, 100);
                int ber = _dev.ReadBer();
                return new SignalStats(true, strength, quality, snrDb, ber);
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "GetSignalStats failed");
            return SignalStats.NoLock;
        }
    }

    public IReadOnlyList<DvbEvent> GetSchedule(int serviceId) => _eit.GetSchedule(serviceId);

    public async Task<byte[]> CaptureTsAsync(int maxBytes, int timeoutMs, CancellationToken ct = default)
    {
        // Drop transition TS and let the mux settle so we capture fresh SDT/PAT (same as BDA path).
        _pipe.Clear();
        await Task.Delay(700, ct);
        _pipe.Clear();

        var buf = new byte[maxBytes];
        int total = 0;
        long deadline = Environment.TickCount64 + timeoutMs;
        while (total < maxBytes && Environment.TickCount64 < deadline && !ct.IsCancellationRequested)
        {
            int n = _pipe.ReadAvailable(buf, total, maxBytes - total);
            if (n > 0) total += n;
            else await Task.Delay(30, ct);
        }
        if (total == maxBytes) return buf;
        var trimmed = new byte[total];
        Array.Copy(buf, trimmed, total);
        return trimmed;
    }

    public void Stop() => StopReader();

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopReader();
        try { _dev.Dispose(); } catch { }
        try { _pipe.Dispose(); } catch { }
        _log.LogDebug("RtlSdrDvbTuner disposed");
    }
}
