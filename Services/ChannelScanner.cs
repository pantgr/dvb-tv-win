using DvbTv.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DvbTv.Services;

/// <summary>
/// Sweeps the UHF DVB-T raster (channels 21–48, 8 MHz, Greece). For each mux that
/// locks, captures a few MB of TS and parses the SI/PSI (SDT+PAT) to enumerate the
/// individual services (channels) with their real names, one Channel per service.
/// </summary>
public sealed class ChannelScanner : IChannelScanner
{
    private const int FirstUhfChannel = 21;   // 474 MHz
    private const int LastUhfChannel = 48;    // 690 MHz
    private const int CaptureBytes = 6 * 1024 * 1024; // ~6 MB ≈ 2.7s @18Mbps → SDT (≈2s period) repeats at least once
    private const int CaptureTimeoutMs = 7000;
    // Scan tuning: 3s lock window keeps dead frequencies cheap (a real mux locks well within it),
    // and NO prebuffer — CaptureTsAsync clears the pipe and does its own capture, so the playback
    // prebuffer would be ~2.5-5s of pure waste per locked mux.
    private const int ScanLockTimeoutMs = 3000;

    private readonly IDvbTuner _tuner;
    private readonly ILogger<ChannelScanner> _log;
    private readonly int _bandwidthMhz;

    public ChannelScanner(IDvbTuner tuner, IConfiguration config, ILogger<ChannelScanner> log)
    {
        _tuner = tuner;
        _log = log;
        _bandwidthMhz = config.GetValue("Tv:DefaultBandwidthMhz", 8);
    }

    public async Task<IReadOnlyList<Channel>> ScanAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var found = new List<Channel>();
        int lcn = 1;
        _log.LogInformation("UHF sweep CH{First}–CH{Last} starting", FirstUhfChannel, LastUhfChannel);

        for (int ch = FirstUhfChannel; ch <= LastUhfChannel; ch++)
        {
            ct.ThrowIfCancellationRequested();
            long freqHz = (474L + (ch - FirstUhfChannel) * 8L) * 1_000_000L;
            double mhz = freqHz / 1_000_000.0;
            progress?.Report($"Σάρωση CH{ch} ({mhz:F0} MHz)…  [{found.Count} κανάλια]");

            if (!await _tuner.TuneAsync(new TuneParameters(freqHz, _bandwidthMhz, ScanLockTimeoutMs, Prebuffer: false), ct)) continue;

            var s = _tuner.GetSignalStats();
            progress?.Report($"CH{ch} ({mhz:F0} MHz) lock {s.StrengthPercent}% — διαβάζω κανάλια…");

            var ts = await _tuner.CaptureTsAsync(CaptureBytes, CaptureTimeoutMs, ct);

            // Diagnostic: is the captured TS 188-aligned, and does it carry SDT/PAT?
            int aligned = 0, sdtPk = 0, patPk = 0, total = ts.Length / 188;
            for (int i = 0; i + 188 <= ts.Length; i += 188)
            {
                if (ts[i] != 0x47) continue;
                aligned++;
                int pid = ((ts[i + 1] & 0x1F) << 8) | ts[i + 2];
                if (pid == 0x11) sdtPk++;
                else if (pid == 0x00) patPk++;
            }
            _log.LogInformation("  diag CH{Ch}: aligned={A}/{T}, sdtPkts={S}, patPkts={P}", ch, aligned, total, sdtPk, patPk);

            var services = PsiParser.ParseServices(ts, ts.Length);
            _log.LogInformation("MUX CH{Ch} @ {Mhz:F0} MHz: {Count} services ({Bytes} KB TS)", ch, mhz, services.Count, ts.Length / 1024);

            foreach (var svc in services)
            {
                found.Add(new Channel
                {
                    Name = svc.Name,
                    LogicalChannelNumber = lcn++,
                    FrequencyHz = freqHz,
                    BandwidthMhz = _bandwidthMhz,
                    ServiceId = svc.ServiceId,
                });
                _log.LogInformation("  service {Sid}: {Name}", svc.ServiceId, svc.Name);
            }

            // No services parsed but signal locked → keep the mux as a fallback entry.
            if (services.Count == 0)
                found.Add(new Channel { Name = $"UHF CH{ch} ({mhz:F0} MHz)", LogicalChannelNumber = lcn++, FrequencyHz = freqHz, BandwidthMhz = _bandwidthMhz });
        }

        _log.LogInformation("UHF sweep done: {Count} channels", found.Count);
        progress?.Report($"Σάρωση τέλος · {found.Count} κανάλια");
        return found;
    }
}
