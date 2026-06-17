using System.Text;
using DvbTv.Services;

namespace DvbTv.UI;

/// <summary>
/// Standalone EPG / channel-info window (separate top-level window — NEVER touches the
/// video window). Shows the schedule for the currently playing channel with ◄ ► buttons
/// to step through programmes (each with its description), and a "Τώρα" button to jump
/// back to what's on now. Fed live from the EIT tap. Closing (X) just hides it.
/// </summary>
public sealed class EpgForm : Form
{
    private readonly ITvController _tv;

    private readonly Label _info = new()
    {
        Dock = DockStyle.Fill,
        ForeColor = Color.Gainsboro,
        Font = new Font("Segoe UI", 10.5f),
        Padding = new Padding(14),
    };
    private readonly Button _prev = new() { Text = "◄ Προηγ.", Dock = DockStyle.Left, Width = 95, FlatStyle = FlatStyle.Flat };
    private readonly Button _next = new() { Text = "Επόμ. ►", Dock = DockStyle.Right, Width = 95, FlatStyle = FlatStyle.Flat };
    private readonly Button _nowBtn = new() { Text = "● Τώρα", Dock = DockStyle.Fill, FlatStyle = FlatStyle.Flat };
    private readonly Panel _buttons = new() { Dock = DockStyle.Bottom, Height = 40 };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 5000 };

    private int _serviceId = -1;
    private int _index;
    private bool _followNow = true; // auto-track the live programme; paused while the user browses with ◄►

    public EpgForm(ITvController tv)
    {
        _tv = tv;
        Text = "DvbTv — EPG / Πληροφορίες";
        Width = 400;
        Height = 500;
        BackColor = Color.FromArgb(28, 28, 30);
        ForeColor = Color.Gainsboro;
        StartPosition = FormStartPosition.Manual;

        foreach (var b in new[] { _prev, _next, _nowBtn })
        { b.ForeColor = Color.White; b.BackColor = Color.FromArgb(50, 50, 54); b.FlatAppearance.BorderSize = 0; }

        Controls.Add(_info);
        _buttons.Controls.Add(_nowBtn); // fill (added first)
        _buttons.Controls.Add(_prev);
        _buttons.Controls.Add(_next);
        Controls.Add(_buttons);

        _prev.Click += (_, _) => { _followNow = false; _index--; ShowEvent(); };
        _next.Click += (_, _) => { _followNow = false; _index++; ShowEvent(); };
        _nowBtn.Click += (_, _) => { _followNow = true; JumpToNow(); ShowEvent(); };

        _timer.Tick += (_, _) => Tick();
        Load += (_, _) => { _timer.Start(); Tick(); };
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
    }

    private void Tick()
    {
        var ch = _tv.Current;
        if (ch is null) { _info.Text = "Διάλεξε κανάλι για να δεις EPG…"; return; }
        if (ch.ServiceId != _serviceId) { _serviceId = ch.ServiceId; _followNow = true; JumpToNow(); } // channel changed → reset to now
        else if (_followNow) JumpToNow(); // auto-advance to the live programme as time passes
        ShowEvent();
    }

    private void JumpToNow()
    {
        var sch = _tv.GetSchedule(_serviceId);
        var utc = DateTime.UtcNow;
        for (int i = 0; i < sch.Count; i++)
            if (utc < sch[i].StartUtc + sch[i].Duration) { _index = i; return; } // first not-yet-ended
        _index = Math.Max(0, sch.Count - 1);
    }

    private void ShowEvent()
    {
        var ch = _tv.Current;
        var sch = _serviceId >= 0 ? _tv.GetSchedule(_serviceId) : Array.Empty<Models.DvbEvent>();

        var sb = new StringBuilder();
        sb.AppendLine($"📺  {ch?.Name ?? "—"}");
        if (ch != null) sb.AppendLine($"#{ch.LogicalChannelNumber} · {ch.FrequencyHz / 1_000_000.0:F0} MHz · sid {ch.ServiceId}");
        sb.AppendLine();

        if (sch.Count == 0)
        {
            sb.AppendLine("(αναμονή EPG…)");
            _info.Text = sb.ToString();
            return;
        }

        _index = Math.Clamp(_index, 0, sch.Count - 1);
        var e = sch[_index];
        var utc = DateTime.UtcNow;
        string marker = (e.StartUtc <= utc && utc < e.StartUtc + e.Duration) ? "● ΤΩΡΑ"
                        : e.StartUtc > utc ? "▸ προσεχώς" : "◂ έληξε";

        sb.AppendLine($"[{_index + 1}/{sch.Count}]  {marker}");
        sb.AppendLine();
        sb.AppendLine($"🕒 {e.StartLocal:ddd HH:mm}–{e.EndLocal:HH:mm}  ({(int)e.Duration.TotalMinutes}′)");
        sb.AppendLine();
        sb.AppendLine($"▶ {e.Name}");
        if (!string.IsNullOrWhiteSpace(e.Description))
        {
            sb.AppendLine();
            sb.AppendLine(e.Description);
        }
        _info.Text = sb.ToString();
    }
}
