using DvbTv.Models;
using DvbTv.Services;

namespace DvbTv.UI;

/// <summary>
/// Weekly EPG grid — a separate window (next to the TV, never on the video) showing the
/// full schedule per channel, grouped by day, with description. Fed from the live EIT
/// tap (actual + other TS). A channel fills in once its mux's EIT has been seen.
/// </summary>
public sealed class WeeklyEpgForm : Form
{
    private readonly ITvController _tv;

    private readonly ListBox _channels = new() { Dock = DockStyle.Left, Width = 210, IntegralHeight = false };
    private readonly ListView _events = new()
    {
        Dock = DockStyle.Fill,
        View = View.Details,
        FullRowSelect = true,
        HideSelection = false,
        BackColor = Color.FromArgb(24, 24, 26),
        ForeColor = Color.Gainsboro,
    };
    private readonly TextBox _desc = new()
    {
        Dock = DockStyle.Bottom,
        Height = 110,
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        BorderStyle = BorderStyle.None,
        BackColor = Color.FromArgb(20, 20, 22),
        ForeColor = Color.Gainsboro,
        Font = new Font("Segoe UI", 9.5f),
    };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15000 };

    public WeeklyEpgForm(ITvController tv)
    {
        _tv = tv;
        Text = "DvbTv — Weekly schedule";
        Width = 780;
        Height = 660;
        BackColor = Color.FromArgb(28, 28, 30);
        ForeColor = Color.Gainsboro;
        StartPosition = FormStartPosition.Manual;

        _events.Columns.Add("When", 130);
        _events.Columns.Add("Duration", 70);
        _events.Columns.Add("Programme", 520);

        Controls.Add(_events);   // fill (added first)
        Controls.Add(_desc);     // bottom
        Controls.Add(_channels); // left

        RefreshChannels();
        _channels.SelectedIndexChanged += (_, _) => LoadEvents(force: true);
        _events.SelectedIndexChanged += (_, _) => ShowDesc();
        _timer.Tick += (_, _) => LoadEvents();
        Load += (_, _) => { _timer.Start(); if (_channels.Items.Count > 0) _channels.SelectedIndex = 0; };
        FormClosing += (_, e) => { if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); } };
    }

    /// <summary>(Re)fill the channel list from the controller — called after a scan so the
    /// grid doesn't keep showing the pre-scan (or empty, on first run) list until a restart.</summary>
    public void RefreshChannels()
    {
        var selected = _channels.SelectedItem as Channel;
        _channels.BeginUpdate();
        _channels.Items.Clear();
        foreach (var ch in _tv.Channels) _channels.Items.Add(ch);
        _channels.EndUpdate();
        if (selected is not null)
            for (int i = 0; i < _channels.Items.Count; i++)
                if (_channels.Items[i] is Channel c && c.ServiceId == selected.ServiceId && c.FrequencyHz == selected.FrequencyHz)
                { _channels.SelectedIndex = i; break; }
    }

    private int _loadedSid = -1;
    private int _loadedCount = -1;

    private void LoadEvents(bool force = false)
    {
        if (_channels.SelectedItem is not Channel ch) return;
        var sch = _tv.GetSchedule(ch.ServiceId);

        // The 15s refresh used to rebuild the ListView unconditionally, wiping the user's
        // selection/scroll mid-browse. Rebuild only when the data actually changed.
        if (!force && ch.ServiceId == _loadedSid && sch.Count == _loadedCount) return;
        _loadedSid = ch.ServiceId;
        _loadedCount = sch.Count;

        _events.BeginUpdate();
        _events.Items.Clear();
        _events.Groups.Clear();
        var groups = new Dictionary<string, ListViewGroup>();
        foreach (var e in sch)
        {
            string dayKey = e.StartLocal.ToString("dddd dd/MM");
            if (!groups.TryGetValue(dayKey, out var g))
            {
                g = new ListViewGroup(dayKey);
                groups[dayKey] = g;
                _events.Groups.Add(g);
            }
            _events.Items.Add(new ListViewItem(new[]
            {
                e.StartLocal.ToString("HH:mm"),
                $"{(int)e.Duration.TotalMinutes}′",
                e.Name,
            })
            { Tag = e, Group = g });
        }
        _events.EndUpdate();

        if (sch.Count == 0)
            _desc.Text = "(waiting for this channel's EPG — play it for a bit so the EIT is collected)";
    }

    private void ShowDesc()
    {
        if (_events.SelectedItems.Count > 0 && _events.SelectedItems[0].Tag is DvbEvent e)
            _desc.Text = $"{e.Name}\r\n{e.StartLocal:dddd dd/MM HH:mm}–{e.EndLocal:HH:mm}\r\n\r\n{e.Description}";
    }
}
