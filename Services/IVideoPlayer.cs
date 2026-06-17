using LibVLCSharp.WinForms;

namespace DvbTv.Services;

/// <summary>Video playback abstraction. Current impl = LibVLCSharp with GPU (d3d11va) decode.</summary>
public interface IVideoPlayer : IDisposable
{
    void Initialize();
    void AttachView(VideoView view);
    void Play(string pathOrMrl);
    void PlayStream(Stream transportStream, int program = 0);
    void Stop();
    int Volume { get; set; }
    bool IsPlaying { get; }

    // Subtitles / closed captions (DVB bitmap subs + teletext) — only present when the
    // channel actually broadcasts them, so the track list is queried live per channel.
    /// <summary>Available subtitle tracks of the playing program: (id, name). id = -1 means "off".</summary>
    IReadOnlyList<(int Id, string Name)> GetSubtitleTracks();
    /// <summary>Currently selected subtitle track id (-1 = disabled).</summary>
    int CurrentSubtitle { get; }
    /// <summary>Enable a subtitle track by id (-1 disables). Returns false if it couldn't be set.</summary>
    bool SetSubtitle(int id);
}
