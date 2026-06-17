using DvbTv.Models;

namespace DvbTv.Services;

/// <summary>
/// Abstraction over the RTL2832U DVB-T tuner (DirectShow BDA graph).
/// Tunes a frequency, reports lock/signal, and exposes the MPEG-TS output.
/// </summary>
public interface IDvbTuner : IDisposable
{
    Task<bool> TuneAsync(TuneParameters parameters, CancellationToken ct = default);
    SignalStats GetSignalStats();
    /// <summary>Live TS pipe the player reads (pull); null until the graph is built.</summary>
    Stream? TransportStream { get; }
    /// <summary>Drain up to maxBytes of raw TS from the live pipe (for SI/PSI scan), or until timeout.</summary>
    Task<byte[]> CaptureTsAsync(int maxBytes, int timeoutMs, CancellationToken ct = default);
    /// <summary>All EPG events for a service (from the live EIT tap), sorted by start time.</summary>
    IReadOnlyList<DvbEvent> GetSchedule(int serviceId);
    void Stop();
}
