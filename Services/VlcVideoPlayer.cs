using LibVLCSharp.Shared;
using LibVLCSharp.WinForms;
using Microsoft.Extensions.Logging;

namespace DvbTv.Services;

/// <summary>
/// LibVLCSharp player. Decode is offloaded to the GPU (NVDEC via Direct3D11)
/// so the CPU stays free for UI/zapping. VLC handles demux + H.264/MPEG-2.
/// </summary>
public sealed class VlcVideoPlayer : IVideoPlayer
{
    private readonly ILogger<VlcVideoPlayer> _log;
    private LibVLC? _libvlc;
    private MediaPlayer? _player;
    private int _desiredVolume = 100; // VLC resets volume per media; we re-apply this on every Playing event
    private System.Threading.Timer? _statsTimer;
    private StreamMediaInput? _streamInput; // kept alive while VLC reads through it; freed on next PlayStream/Dispose
    private int _lastLost;
    private bool _formatLogged;
    private bool _disposed;

    // Rate-limit for forwarded VLC errors. Some VLC errors fire PER FRAME once triggered
    // ("Could not convert timestamp X for FFmpeg" was 60k of 65k log lines on 2026-06-10 —
    // 93% of the file, drowning everything). First occurrence logs immediately; repeats of
    // the same message shape (digits stripped) are summarised at most once per window.
    private readonly object _vlcLogLock = new();
    private readonly Dictionary<string, (long LastEmitMs, int Suppressed)> _vlcThrottle = new();
    private const int VlcRepeatWindowMs = 10_000;

    public VlcVideoPlayer(ILogger<VlcVideoPlayer> log) => _log = log;

    public void Initialize()
    {
        Core.Initialize();
        // hw-decode disable + deinterlace are set PER-MEDIA in PlayStream (global LibVLC args like
        // --avcodec-hw=none / --codec=avcodec are IGNORED here — verified in the log: NVDEC kept
        // running. Media options DO apply, so the real knobs live there).
        _libvlc = new LibVLC("--no-video-title-show");
        // Forward ONLY VLC errors — Debug/Warning are thousands of discontinuity/late-frame
        // lines per minute that drown the app's own events and make the log unreadable.
        _libvlc.Log += (_, e) =>
        {
            if (e.Level != LibVLCSharp.Shared.LogLevel.Error) return;
            var msg = e.Message ?? "";
            // Known-benign VLC noise that fires on every HEALTHY live-TS play — filter so the
            // log stays readable (the whole point of this project's logging):
            //  • libdvbpsi "version_number differs … no discontinuity": the broadcaster bumps the
            //    EIT (EPG) table version constantly as programmes roll; libdvbpsi flags every bump.
            //    PID 0x12 carries EPG metadata only (no A/V), and we parse EPG ourselves (EitCollector),
            //    so VLC's EIT result is irrelevant. Confirmed harmless (forum.videolan.org/viewtopic.php?t=141970).
            //  • imem "Invalid get/release function pointers": harmless StreamMediaInput probe note;
            //    appears on every successful play too.
            if (msg.Contains("libdvbpsi") || msg.Contains("get/release function pointers")) return;
            ForwardVlcError(e.Module, msg);
        };
        _player = new MediaPlayer(_libvlc);
        // VLC applies volume per-playback and resets it on each new media (every zap). Re-apply the
        // user's chosen level once audio output exists, so the setting persists across channel changes.
        _player.Playing += (_, _) => { try { if (_player is not null) _player.Volume = _desiredVolume; } catch { /* audio not ready */ } };
        // Quantify frame loss (Pantelis' theory: fast motion → dropped frames → blur). VLC keeps
        // running counters per play; we log them every 5s and flag the DELTA of lost pictures so
        // a burst of drops during a fast scene is visible, not just the cumulative total.
        _statsTimer = new System.Threading.Timer(_ => LogStats(), null, 5000, 5000);
        _log.LogInformation("LibVLC initialized (d3d11va + direct3d11 vout)");
    }

    public void AttachView(VideoView view)
    {
        EnsureReady();
        view.MediaPlayer = _player;
        _log.LogDebug("VideoView attached to MediaPlayer");
    }

    public void Play(string pathOrMrl)
    {
        EnsureReady();
        ResetPlayDiagnostics();
        _log.LogInformation("Play {Source}", pathOrMrl);
        if (pathOrMrl.Contains("://"))
        {
            // Network/live source. A whole DVB-T mux is ~20 Mbps and bursty, so give
            // VLC a real jitter buffer (1s) — too-low caching makes it drop "late"
            // packets => TS discontinuities.
            using var media = new Media(_libvlc!, pathOrMrl, FromType.FromLocation);
            media.AddOption(":network-caching=1000");
            media.AddOption(":live-caching=1000");
            _player!.Play(media);
        }
        else
        {
            using var media = new Media(_libvlc!, pathOrMrl, FromType.FromPath);
            _player!.Play(media);
        }
    }

    public void PlayStream(Stream ts, int program = 0)
    {
        EnsureReady();
        ResetPlayDiagnostics();
        _log.LogInformation("Play transport stream (canSeek={CanSeek}, program={Program})", ts.CanSeek, program);
        var input = new StreamMediaInput(ts);
        using var media = new Media(_libvlc!, input);
        // Bigger jitter buffer (3s) → video frames get enough lead time to present on time, so VLC
        // stops dropping them as "late" (the fast-motion blur) — and audio+video are delayed equally
        // so they stay in sync. 3s extra latency is fine for live TV (not interactive).
        media.AddOption(":network-caching=3000");
        media.AddOption(":live-caching=3000");
        // 🔵 2026-06-16: disable hw decode PER-MEDIA (global --avcodec-hw=none was ignored — log
        // still showed NVDEC "picture allocation failed"). This actually closes NVDEC.
        media.AddOption(":avcodec-hw=none");
        // Deinterlace OFF: the log showed decoded ≈ 2× displayed → double-rate deinterlace was
        // making 50 fps the display couldn't keep up with → stutter + A/V desync. Off → decoded
        // should drop to ~25 (= source). If combing appears on interlaced channels, switch to a
        // single-rate mode instead. (Was :deinterlace=-1 + yadif, which still ran double-rate.)
        media.AddOption(":deinterlace=0");
        media.AddOption(":file-caching=3000");
        if (program > 0) media.AddOption($":program={program}");
        _player!.Play(media);
        // The media wrapper can go right away (the player retains its own native ref —
        // same pattern as Play above), but the StreamMediaInput must outlive the playback:
        // VLC calls its read callbacks for as long as this media plays. Free the PREVIOUS
        // zap's input now that the player has switched media, and keep this one.
        var oldInput = _streamInput;
        _streamInput = input;
        oldInput?.Dispose();
    }

    /// <summary>Per-play reset of the 5s-stats diagnostics, so every zap logs its VIDEO/AUDIO
    /// format once and the lost-pictures delta doesn't go negative on VLC's fresh counters.</summary>
    private void ResetPlayDiagnostics()
    {
        _formatLogged = false;
        _lastLost = 0;
    }

    /// <summary>Forward a VLC error with per-message-shape rate limiting (see field docs).
    /// Nothing is hidden: the first occurrence logs at once, repeats are counted and a
    /// summary line with the count is emitted at most once per window.</summary>
    private void ForwardVlcError(string? module, string msg)
    {
        var sig = System.Text.RegularExpressions.Regex.Replace(msg, "[0-9]+", "#");
        lock (_vlcLogLock)
        {
            long now = Environment.TickCount64;
            _vlcThrottle.TryGetValue(sig, out var t);
            if (t.LastEmitMs != 0 && now - t.LastEmitMs < VlcRepeatWindowMs)
            {
                _vlcThrottle[sig] = (t.LastEmitMs, t.Suppressed + 1);
                return;
            }
            _vlcThrottle[sig] = (now, 0);
            if (t.Suppressed > 0)
                _log.LogWarning("[VLC] {Module}: {Message} (+{Suppressed} repeats suppressed in the last {Window}s)",
                    module, msg, t.Suppressed, VlcRepeatWindowMs / 1000);
            else
                _log.LogWarning("[VLC] {Module}: {Message}", module, msg);
        }
    }

    public void Stop()
    {
        _player?.Stop();
        _log.LogInformation("Playback stopped");
    }

    public int Volume
    {
        get => _desiredVolume;
        set
        {
            _desiredVolume = Math.Clamp(value, 0, 150);
            if (_player is not null) _player.Volume = _desiredVolume;
        }
    }

    public bool IsPlaying => _player?.IsPlaying ?? false;

    public IReadOnlyList<(int Id, string Name)> GetSubtitleTracks()
    {
        var list = new List<(int, string)>();
        var desc = _player?.SpuDescription;
        if (desc != null)
            foreach (var t in desc)
                list.Add((t.Id, string.IsNullOrWhiteSpace(t.Name) ? $"Track {t.Id}" : t.Name));
        return list;
    }

    public int CurrentSubtitle => _player?.Spu ?? -1;

    public bool SetSubtitle(int id)
    {
        if (_player is null) return false;
        var ok = _player.SetSpu(id);
        _log.LogInformation("Subtitle (SPU) track -> {Id} (ok={Ok})", id, ok);
        return ok;
    }

    private void LogVideoFormat()
    {
        try
        {
            // MediaPlayer.Media returns a NEW wrapper (with a retained native ref) on every
            // access — dispose it, or each call leaks a native media reference.
            using var m = _player?.Media;
            if (m is null) return;
            foreach (var t in m.Tracks)
            {
                // The MPTS carries the elementary streams of EVERY programme in the mux; the
                // non-selected ones show up unprobed (0x0 / 0ch 0Hz). Log only the real ones.
                if (t.TrackType == TrackType.Video)
                {
                    var v = t.Data.Video;
                    if (v.Width == 0 && v.Height == 0) continue;
                    double fps = v.FrameRateDen > 0 ? (double)v.FrameRateNum / v.FrameRateDen : 0;
                    _log.LogInformation("VIDEO format: {W}x{H} @ {Fps:F3} fps (fourcc={Codec}) | player.Fps reported={PlayerFps}",
                        v.Width, v.Height, fps, t.Codec, _player?.Fps);
                }
                else if (t.TrackType == TrackType.Audio)
                {
                    var a = t.Data.Audio;
                    if (a.Channels == 0 && a.Rate == 0) continue;
                    _log.LogInformation("AUDIO format: {Ch}ch {Rate}Hz (fourcc={Codec})", a.Channels, a.Rate, t.Codec);
                }
            }
        }
        catch (Exception ex) { _log.LogDebug(ex, "LogVideoFormat failed"); }
    }

    private void LogStats()
    {
        try
        {
            using var m = _player?.Media; // new wrapper per access — see LogVideoFormat
            if (m is null || _player?.IsPlaying != true) return;
            var s = m.Statistics;
            int lost = s.LostPictures;
            int deltaLost = lost - _lastLost;
            _lastLost = lost;
            if (!_formatLogged) { _formatLogged = true; LogVideoFormat(); } // tracks are populated by now
            _log.LogInformation(
                "VLC stats: decoded={Dec} displayed={Disp} lost={Lost} (+{Delta} since last) | player.Fps={Fps}",
                s.DecodedVideo, s.DisplayedPictures, lost, deltaLost, _player?.Fps ?? 0);
        }
        catch { /* stats are best-effort */ }
    }

    private void EnsureReady()
    {
        if (_libvlc is null || _player is null)
            throw new InvalidOperationException("Call Initialize() before using the player.");
    }

    public void Dispose()
    {
        // 🔴 Idempotent. MainForm disposes us in FormClosing (STA thread = correct for LibVLC),
        // then the DI host disposes the singleton AGAIN. A 2nd LibVLC.Dispose() → LibVLCLogUnset()
        // on an already-freed handle = native 0xc0000005 "Exception Processing Message / Unexpected
        // parameters" on exit (confirmed via .NET Runtime crash stack). The 2nd call must no-op.
        if (_disposed) return;
        _disposed = true;
        _statsTimer?.Dispose();
        _player?.Dispose();
        _streamInput?.Dispose(); // after the player — VLC must not call read callbacks on a freed input
        _libvlc?.Dispose();
        _log.LogDebug("VlcVideoPlayer disposed");
    }
}
