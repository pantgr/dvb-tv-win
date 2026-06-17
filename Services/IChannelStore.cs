using DvbTv.Models;

namespace DvbTv.Services;

/// <summary>Persists the discovered channel list (JSON on disk).</summary>
public interface IChannelStore
{
    IReadOnlyList<Channel> Load();
    void Save(IReadOnlyList<Channel> channels);
}
