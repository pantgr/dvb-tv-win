using System.Diagnostics;
using DvbTv.Models;
using Microsoft.Extensions.Logging;

namespace DvbTv.Services;

public sealed class TvController : ITvController
{
    private readonly IDvbTuner _tuner;
    private readonly IVideoPlayer _player;
    private readonly ILogger<TvController> _log;

    public TvController(IDvbTuner tuner, IVideoPlayer player, IChannelStore store, ILogger<TvController> log)
    {
        _tuner = tuner;
        _player = player;
        _log = log;
        Channels = store.Load();
    }

    public IReadOnlyList<Channel> Channels { get; private set; }
    public Channel? Current { get; private set; }

    /// <summary>Replace the channel list after a scan, so EPG windows etc. see the fresh list without a restart.</summary>
    public void SetChannels(IReadOnlyList<Channel> channels) => Channels = channels;

    public IReadOnlyList<DvbEvent> GetSchedule(int serviceId) => _tuner.GetSchedule(serviceId);

    public async Task<bool> ChangeChannelAsync(Channel channel, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        _log.LogInformation("Zapping to {Channel} @ {Freq} Hz", channel, channel.FrequencyHz);

        // Stop the OLD channel's playback before tuning: both playbacks read the same live
        // pipe, so a still-running VLC reader drains everything the post-lock prebuffer tries
        // to accumulate — the "Prebuffered 0 KB before play" in the 2026-06-10 logs — wasting
        // the full prebuffer deadline and starting the new channel on an empty buffer.
        _player.Stop();

        var locked = await _tuner.TuneAsync(new TuneParameters(channel.FrequencyHz, channel.BandwidthMhz), ct);
        if (!locked)
        {
            _log.LogWarning("LOCK FAILED for {Channel} after {Ms} ms", channel, sw.ElapsedMilliseconds);
            return false;
        }

        var s = _tuner.GetSignalStats();
        _log.LogInformation("Locked {Channel}: strength={Strength}% quality={Quality}% in {Ms} ms",
            channel, s.StrengthPercent, s.QualityPercent, sw.ElapsedMilliseconds);

        // The tuner writes the locked mux's raw TS to its live pipe; play that.
        var ts = _tuner.TransportStream;
        if (ts is null)
        {
            _log.LogWarning("Tuner has no transport stream for {Channel}", channel);
            return false;
        }
        _player.PlayStream(ts, channel.ServiceId);
        // Only now does this become the "current" channel — a failed zap must not steal
        // the EPG window / Play-after-Stop fallback from the channel that actually plays.
        Current = channel;
        _log.LogInformation("Now playing {Channel} (sid={Sid}, total {Ms} ms)", channel, channel.ServiceId, sw.ElapsedMilliseconds);
        return true;
    }

    public void Stop()
    {
        _player.Stop();
        _tuner.Stop();
    }
}
