using DvbTv.Models;

namespace DvbTv.Services;

/// <summary>Orchestrates tuner + player: the high-level "watch this channel" logic.</summary>
public interface ITvController
{
    IReadOnlyList<Channel> Channels { get; }
    /// <summary>Replace the channel list (after a scan) so consumers see it without a restart.</summary>
    void SetChannels(IReadOnlyList<Channel> channels);
    /// <summary>The channel that is actually playing — set only after a successful tune+play.</summary>
    Channel? Current { get; }
    Task<bool> ChangeChannelAsync(Channel channel, CancellationToken ct = default);
    IReadOnlyList<DvbEvent> GetSchedule(int serviceId);
    void Stop();
}
