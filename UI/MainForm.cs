using DvbTv.Models;
using DvbTv.Services;
using LibVLCSharp.WinForms;
using Microsoft.Extensions.Logging;

namespace DvbTv.UI;

public sealed class MainForm : Form
{
    private readonly ITvController _tv;
    private readonly IVideoPlayer _player;
    private readonly IDvbTuner _tuner;
    private readonly IChannelScanner _scanner;
    private readonly IChannelStore _store;
    private readonly ILogger<MainForm> _log;

    private readonly VideoView _videoView = new() { Dock = DockStyle.Fill, BackColor = Color.Black };
    private readonly ListBox _channels = new() { Dock = DockStyle.Left, Width = 230, IntegralHeight = false };
    private readonly StatusStrip _status = new() { Dock = DockStyle.Bottom };
    private readonly ToolStripStatusLabel _statusLabel = new() { Text = "Ready", Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _signalLabel = new() { Text = "📡 —", Alignment = ToolStripItemAlignment.Right };
    private readonly System.Windows.Forms.Timer _signalTimer = new() { Interval = 1000 };
    private readonly ToolStrip _controlBar = new() { Dock = DockStyle.Bottom, GripStyle = ToolStripGripStyle.Hidden, RenderMode = ToolStripRenderMode.System };
    private ToolStripLabel _volLabel = null!;
    private int _mutedFrom; // remembers the level before mute so unmute restores it
    private bool _fullscreen;
    private FormBorderStyle _savedBorder;
    private FormWindowState _savedState;

    // 🔴 Zap serialization. A cold tune can take ~3s lock + ~2.5s prebuffer; without a guard a
    // second double-click in that window runs TWO ChangeChannelAsync calls interleaved on the
    // SAME tuner — the first poll loop can see the second tune's lock and play the wrong
    // ServiceId on the wrong mux. Rule: a new zap CANCELS the in-flight one, then waits until
    // it has fully exited the tuner before submitting its own tune request.
    private CancellationTokenSource? _zapCts;
    private Task _zapTask = Task.CompletedTask;
    private bool _scanning; // scan and zap are mutually exclusive (the scanner retunes + clears the pipe)

    private EpgForm? _epgForm;          // separate now/next window
    private WeeklyEpgForm? _weeklyForm; // separate weekly-grid window

    public MainForm(ITvController tv, IVideoPlayer player, IDvbTuner tuner, IChannelScanner scanner, IChannelStore store, ILogger<MainForm> log)
    {
        _tv = tv;
        _player = player;
        _tuner = tuner;
        _scanner = scanner;
        _store = store;
        _log = log;

        Text = "DvbTv — RTL2832U DVB-T";
        Width = 1280;
        Height = 720;
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { /* icon optional */ }

        Controls.Add(_videoView);   // fill (added first)
        _videoView.DoubleClick += (_, _) => ToggleFullscreen();
        _channels.DoubleClick += OnChannelActivate;
        Controls.Add(_channels);    // left
        _status.Items.Add(_statusLabel);
        _status.Items.Add(_signalLabel);
        Controls.Add(_status);      // bottom (lowest)
        _signalTimer.Tick += UpdateSignal;
        BuildControlBar();          // bottom (above status) — UNDER the video, not over it
        BuildMenu();                // top

        KeyPreview = true;
        KeyDown += OnKeyDown;
        Load += OnLoad;
        // Tear down player AND the BDA COM graph HERE, while the STA message pump is still alive.
        // Releasing the DirectShow graph later (DI host dispose, after Application.Run returns)
        // crashes with a native "system error / unexpected parameters" on app exit.
        FormClosing += (_, _) => { _signalTimer.Stop(); _zapCts?.Cancel(); _tv.Stop(); _player.Dispose(); _tuner.Dispose(); _epgForm?.Dispose(); _weeklyForm?.Dispose(); };
    }

    private void BuildMenu()
    {
        var menu = new MenuStrip();
        var tv = new ToolStripMenuItem("&TV");
        tv.DropDownItems.Add("&Scan channels", null, async (_, _) => await ScanAsync());
        tv.DropDownItems.Add("&EPG (now/next)", null, (_, _) => ShowEpg());
        tv.DropDownItems.Add("&Weekly EPG", null, (_, _) => ShowWeekly());
        tv.DropDownItems.Add("Open &file (test player)…", null, (_, _) => OpenFile());
        tv.DropDownItems.Add("S&top", null, (_, _) => StopAll());
        menu.Items.Add(tv);
        MainMenuStrip = menu;
        Controls.Add(menu);
    }

    private void BuildControlBar()
    {
        ToolStripButton Btn(string text, EventHandler onClick)
        {
            var b = new ToolStripButton(text) { DisplayStyle = ToolStripItemDisplayStyle.Text, AutoToolTip = false };
            b.Click += onClick;
            return b;
        }
        _controlBar.Items.Add(Btn("⏹ Stop", (_, _) => StopAll()));
        _controlBar.Items.Add(Btn("▶ Play", async (_, _) => await PlayCurrent()));

        var cc = new ToolStripDropDownButton("💬 Subtitles") { DisplayStyle = ToolStripItemDisplayStyle.Text, AutoToolTip = false };
        cc.DropDownOpening += (_, _) => PopulateSubtitleMenu(cc);
        _controlBar.Items.Add(cc);

        _controlBar.Items.Add(Btn("⛶ Full screen", (_, _) => ToggleFullscreen()));

        _controlBar.Items.Add(new ToolStripSeparator());
        _controlBar.Items.Add(Btn("🔇 Mute", (_, _) => ToggleMute()));
        _controlBar.Items.Add(Btn("🔉 −", (_, _) => ChangeVolume(-5)));
        _controlBar.Items.Add(Btn("🔊 +", (_, _) => ChangeVolume(+5)));
        _volLabel = new ToolStripLabel($"{_player.Volume}%");
        _controlBar.Items.Add(_volLabel);

        Controls.Add(_controlBar);
    }

    /// <summary>Fill the CC dropdown live — subtitle tracks exist only if the channel broadcasts them
    /// (DVB bitmap subs or teletext), and only after VLC has detected the ES, so we query on open.</summary>
    private void PopulateSubtitleMenu(ToolStripDropDownButton cc)
    {
        cc.DropDownItems.Clear();
        var tracks = _player.GetSubtitleTracks();
        if (tracks.Count == 0)
        {
            cc.DropDownItems.Add(new ToolStripMenuItem("(no subtitles broadcast)") { Enabled = false });
            return;
        }

        int current = _player.CurrentSubtitle;
        bool hasOff = false;
        void AddItem(int id, string name)
        {
            var item = new ToolStripMenuItem(name) { Checked = id == current };
            item.Click += (_, _) =>
            {
                _player.SetSubtitle(id);
                SetStatus(id < 0 ? "Subtitles: off" : $"Subtitles: {name}");
            };
            cc.DropDownItems.Add(item);
        }
        foreach (var (id, name) in tracks)
        {
            if (id < 0) { hasOff = true; AddItem(id, "Off"); }
            else AddItem(id, name);
        }
        if (!hasOff) AddItem(-1, "Off"); // VLC usually includes a -1 entry; add one if it didn't
    }

    private async Task PlayCurrent()
    {
        // Prefer what's SELECTED in the list (so Play plays your selection); fall back to the
        // currently-playing channel only if nothing is selected (e.g. Play after Stop).
        var ch = _channels.SelectedItem as Channel ?? _tv.Current;
        if (ch is null) { SetStatus("Select a channel first"); return; }
        await ZapTo(ch);
    }

    /// <summary>Stop playback + tuner graph, cancelling any zap still in flight.</summary>
    private void StopAll()
    {
        _zapCts?.Cancel();
        _tv.Stop(); // stops player AND tuner graph
        SetStatus("Stopped");
    }

    /// <summary>The single entry point for channel changes — supersedes any in-flight zap.</summary>
    private async Task ZapTo(Channel ch)
    {
        if (_scanning) { SetStatus("Scan in progress — please wait…"); return; }

        // Cancel the in-flight zap and WAIT until it has fully exited the tuner before we
        // touch it — two TuneAsync calls interleaved on one BDA graph mis-tune (see field docs).
        _zapCts?.Cancel();
        var cts = new CancellationTokenSource();
        _zapCts = cts;
        try { await _zapTask; } catch { /* the superseded zap's outcome is irrelevant here */ }
        if (cts.IsCancellationRequested) return; // an even newer zap took over while we waited

        SetStatus($"Tuning {ch}…");
        var task = ChangeAndReport(ch, cts.Token);
        _zapTask = task;
        await task;
    }

    private async Task ChangeAndReport(Channel ch, CancellationToken ct)
    {
        try
        {
            var ok = await _tv.ChangeChannelAsync(ch, ct);
            if (!ct.IsCancellationRequested)
                SetStatus(ok ? $"▶ {ch}" : $"Lock failed: {ch} — see logs");
        }
        catch (OperationCanceledException) { /* superseded by a newer zap or Stop — say nothing */ }
        catch (Exception ex)
        {
            _log.LogError(ex, "Tune failed for {Channel}", ch);
            SetStatus($"Tune FAILED: {ex.Message}");
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F11) ToggleFullscreen();
        else if (e.KeyCode == Keys.Escape && _fullscreen) ToggleFullscreen();
    }

    /// <summary>Fullscreen the video by hiding the chrome (list/bar/status/menu) — the video itself is untouched.</summary>
    private void ToggleFullscreen()
    {
        _fullscreen = !_fullscreen;
        if (_fullscreen)
        {
            _savedBorder = FormBorderStyle;
            _savedState = WindowState;
            if (MainMenuStrip != null) MainMenuStrip.Visible = false;
            _channels.Visible = false;
            _status.Visible = false;
            _controlBar.Visible = false;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Normal;   // toggle needed to go borderless-maximized
            WindowState = FormWindowState.Maximized;
        }
        else
        {
            if (MainMenuStrip != null) MainMenuStrip.Visible = true;
            _channels.Visible = true;
            _status.Visible = true;
            _controlBar.Visible = true;
            FormBorderStyle = _savedBorder;
            WindowState = _savedState;
        }
    }

    private void OnLoad(object? sender, EventArgs e)
    {
        try
        {
            _player.Initialize();
            _player.AttachView(_videoView);
            LoadChannelList(_tv.Channels);
            ShowEpg();
            _signalTimer.Start();
            SetStatus($"Player ready · {_tv.Channels.Count} channels — double-click a channel to play");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Player init failed");
            SetStatus("Player init FAILED — see logs");
        }
    }

    private void ShowEpg()
    {
        if (_epgForm is null || _epgForm.IsDisposed)
        {
            _epgForm = new EpgForm(_tv) { Location = new Point(Right, Top) };
            _epgForm.Show(this);
        }
        else
        {
            _epgForm.Show();
            _epgForm.BringToFront();
        }
    }

    private void ShowWeekly()
    {
        if (_weeklyForm is null || _weeklyForm.IsDisposed)
        {
            _weeklyForm = new WeeklyEpgForm(_tv) { Location = new Point(Math.Max(0, Left - 200), Top + 40) };
            _weeklyForm.Show(this);
        }
        else
        {
            _weeklyForm.RefreshChannels(); // pick up a list refreshed by a scan since last shown
            _weeklyForm.Show();
            _weeklyForm.BringToFront();
        }
    }

    private void LoadChannelList(IReadOnlyList<Channel> channels)
    {
        _channels.BeginUpdate();
        _channels.Items.Clear();
        foreach (var ch in channels) _channels.Items.Add(ch);
        _channels.EndUpdate();
    }

    private async void OnChannelActivate(object? sender, EventArgs e)
    {
        if (_channels.SelectedItem is not Channel ch) return;
        await ZapTo(ch);
    }

    private async Task ScanAsync()
    {
        if (_scanning) { SetStatus("Scan already in progress…"); return; }
        _scanning = true;
        try
        {
            // The scanner retunes constantly and clears the pipe — stop any zap/playback first.
            _zapCts?.Cancel();
            try { await _zapTask; } catch { /* superseded zap — irrelevant */ }
            _tv.Stop();

            SetStatus("Scanning…");
            var progress = new Progress<string>(SetStatus);
            var found = await _scanner.ScanAsync(progress);
            if (found.Count > 0)
            {
                _store.Save(found);
                _tv.SetChannels(found);     // so EPG windows / Play fallback see the fresh list
                LoadChannelList(found);
                if (_weeklyForm is { IsDisposed: false }) _weeklyForm.RefreshChannels();
            }
            SetStatus($"Scan done · {found.Count} channels — double-click to play");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Scan failed");
            SetStatus($"Scan FAILED: {ex.Message} — see logs");
        }
        finally { _scanning = false; }
    }

    private void OpenFile()
    {
        using var dlg = new OpenFileDialog { Filter = "Media|*.ts;*.mp4;*.mkv;*.avi;*.m2ts|All|*.*" };
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _log.LogInformation("Test playback of {File}", dlg.FileName);
            _player.Play(dlg.FileName);
            SetStatus($"Playing {Path.GetFileName(dlg.FileName)}");
        }
    }

    private void SetStatus(string text)
    {
        if (InvokeRequired) { BeginInvoke(() => SetStatus(text)); return; }
        _statusLabel.Text = text;
        _log.LogDebug("STATUS: {Text}", text);
    }

    private void ChangeVolume(int delta)
    {
        _player.Volume += delta;       // setter clamps 0..150
        _mutedFrom = 0;                // a manual change exits the mute state
        UpdateVolumeLabel();
    }

    private void ToggleMute()
    {
        if (_player.Volume > 0) { _mutedFrom = _player.Volume; _player.Volume = 0; }
        else { _player.Volume = _mutedFrom > 0 ? _mutedFrom : 100; _mutedFrom = 0; }
        UpdateVolumeLabel();
    }

    private void UpdateVolumeLabel() => _volLabel.Text = _player.Volume == 0 ? "🔇 muted" : $"{_player.Volume}%";

    /// <summary>Live signal readout in the status bar — frequency, strength, quality, SNR, lock.</summary>
    private void UpdateSignal(object? sender, EventArgs e)
    {
        try
        {
            var s = _tuner.GetSignalStats();
            var ch = _tv.Current;
            string freq = ch is not null ? $"{ch.FrequencyHz / 1_000_000.0:0.#} MHz" : "—";
            string lockTxt = s.Locked ? "🔒 Lock" : "🔓 No lock";
            _signalLabel.Text = $"📡 {freq}  ·  Signal {s.StrengthPercent}%  ·  Quality {s.QualityPercent}%  ·  SNR {s.SnrDb:0.0} dB  ·  BER {s.Ber}  ·  {lockTxt}";
        }
        catch { /* tuner not ready / mid-zap — leave previous reading */ }
    }
}
