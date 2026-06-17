namespace DvbTv.Models;

/// <summary>DVB-T tuning request: centre frequency + channel bandwidth (Greece = 8 MHz).</summary>
/// <param name="LockTimeoutMs">How long to poll for signal lock. A cold tuner (PLL+AGC settle)
/// has been measured to need up to ~3.4s, so the zap path uses a 5s default; the scanner passes
/// 3000 so dead frequencies don't slow the sweep (the poll exits early on lock either way).</param>
/// <param name="Prebuffer">Fill the ring with ~2.5s of post-lock TS before returning, so VLC
/// starts playback on a full buffer. The scanner turns this off — it clears the pipe and does its
/// own capture (CaptureTsAsync), so prebuffering there is pure wasted scan time.</param>
public readonly record struct TuneParameters(long FrequencyHz, int BandwidthMhz, int LockTimeoutMs = 5000, bool Prebuffer = true);
