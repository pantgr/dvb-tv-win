using System.Text.Json;
using DvbTv.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DvbTv.Services;

public sealed class JsonChannelStore : IChannelStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private readonly ILogger<JsonChannelStore> _log;
    private readonly string _path;

    public JsonChannelStore(IConfiguration config, ILogger<JsonChannelStore> log)
    {
        _log = log;
        var file = config["Tv:ChannelsFile"] ?? "channels.json"; // appsettings.json "Tv" section
        _path = Path.IsPathRooted(file) ? file : Path.Combine(AppContext.BaseDirectory, file);
    }

    public IReadOnlyList<Channel> Load()
    {
        if (!File.Exists(_path))
        {
            _log.LogInformation("No channel file at {Path} — starting empty", _path);
            return [];
        }

        try
        {
            var json = File.ReadAllText(_path);
            var list = JsonSerializer.Deserialize<List<Channel>>(json, Options) ?? [];
            _log.LogInformation("Loaded {Count} channels from {Path}", list.Count, _path);
            return list;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Failed to read channels from {Path}", _path);
            return [];
        }
    }

    public void Save(IReadOnlyList<Channel> channels)
    {
        File.WriteAllText(_path, JsonSerializer.Serialize(channels, Options));
        _log.LogInformation("Saved {Count} channels to {Path}", channels.Count, _path);
    }
}
