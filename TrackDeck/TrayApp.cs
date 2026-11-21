using System.Drawing;
using System.Drawing.Drawing2D;
using Microsoft.Win32;

namespace TrackDeck;

/// <summary>Tray app: watches Windows media and rewrites the card when the track (or play state) changes.</summary>
public sealed class TrayApp : ApplicationContext
{
    private static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrackDeck");
    private static readonly string SettingsPath = Path.Combine(Dir, "settings.txt");
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly MediaReader _media = new();
    private readonly ScreenController _screen = new();
    private readonly SemaphoreSlim _busy = new(1, 1);

    private string? _shownKey;        // what's on the keyboard now
    private string? _pendingKey;      // candidate waiting to settle
    private DateTime _pendingSince;
    private DateTime _lastWrite = DateTime.MinValue;
    private DateTime? _idleSince;
    private bool _restored = true;
    private string? _keyboardProblem; // e.g. "Keyboard is wireless…"; shown instead of the track until fixed

    private bool _showPaused = true;
    private bool _restoreWhenIdle = true;
    private int _idleSeconds = 60;    // paused or nothing playing for this long → put the user's screen back
    private const int MinIdleSeconds = 5, MaxIdleSeconds = 24 * 3600;

    private readonly ToolStripMenuItem _statusItem = new("Warming up…") { Enabled = false };
    private readonly ToolStripMenuItem _spotifyOnlyItem = new("Spotify only") { CheckOnClick = true };
    private readonly ToolStripMenuItem _showPausedItem = new("Show paused state") { CheckOnClick = true };
    private readonly ToolStripMenuItem _restoreIdleItem = new("Restore my screen when idle") { CheckOnClick = true };
    private readonly ToolStripMenuItem _idleTimeItem = new();
    private readonly ToolStripMenuItem _startupItem = new("Start with Windows") { CheckOnClick = true };

    public TrayApp()
    {
        Directory.CreateDirectory(Dir);
        LoadSettings();
        _screen.Log = Log;

        _spotifyOnlyItem.Checked = _media.SpotifyOnly;
        _showPausedItem.Checked = _showPaused;
        _restoreIdleItem.Checked = _restoreWhenIdle;
        _startupItem.Checked = IsStartupEnabled();

        _spotifyOnlyItem.CheckedChanged += (_, _) => { _media.SpotifyOnly = _spotifyOnlyItem.Checked; _shownKey = null; SaveSettings(); };
        _showPausedItem.CheckedChanged += (_, _) => { _showPaused = _showPausedItem.Checked; _shownKey = null; SaveSettings(); };
        _restoreIdleItem.CheckedChanged += (_, _) => { _restoreWhenIdle = _restoreIdleItem.Checked; UpdateIdleTimeItem(); SaveSettings(); };
        _idleTimeItem.Click += (_, _) => AskIdleSeconds();
        UpdateIdleTimeItem();
        _startupItem.CheckedChanged += (_, _) => SetStartup(_startupItem.Checked);

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Refresh card now", null, (_, _) => { _shownKey = null; _lastWrite = DateTime.MinValue; });
        menu.Items.Add("Restore my screen", null, async (_, _) => await RestoreFromMenuAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_spotifyOnlyItem);
        menu.Items.Add(_showPausedItem);
        menu.Items.Add(_restoreIdleItem);
        menu.Items.Add(_idleTimeItem);
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open log", null, (_, _) => { try { System.Diagnostics.Process.Start("notepad.exe", LogPath); } catch { } });
        menu.Items.Add("Exit (restore my screen)", null, async (_, _) => await ExitAsync());

        _tray = new NotifyIcon { Icon = MakeIcon(), Text = "TrackDeck", Visible = true, ContextMenuStrip = menu };
        _timer.Tick += async (_, _) => await TickAsync();
        _timer.Start();
        Log("started");
    }

    private async Task TickAsync()
    {
        if (!await _busy.WaitAsync(0)) return;
        try
        {
            NowPlaying? np = null;
            try { np = await _media.ReadAsync(); } catch (Exception e) { Log("media: " + e.Message); }

            // Idle = paused, or nothing playing at all. After the idle time, the user's own screen comes back
            // and stays until music plays again.
            bool idle = np == null || !np.IsPlaying;
            if (idle) _idleSince ??= DateTime.Now; else _idleSince = null;
            bool idleTimeUp = idle && _restoreWhenIdle && DateTime.Now - _idleSince >= TimeSpan.FromSeconds(_idleSeconds);

            if (idleTimeUp)
            {
                _pendingKey = null;
                if (!_restored && DateTime.Now - _lastWrite >= TimeSpan.FromSeconds(3))
                {
                    await RestoreCoreAsync();
                    _lastWrite = DateTime.Now;
                    Log($"idle for {_idleSeconds} s, restored your screen");
                }
                SetStatus(_keyboardProblem ?? (np == null
                    ? "Nothing playing. Showing your screen."
                    : $"Paused: {np.Title} (showing your screen)"));
                return;
            }

            if (np == null)
            {
                SetStatus(_media.SpotifyOnly ? "Spotify is quiet. Press play!" : "Nothing playing. Suspiciously quiet.");
                _pendingKey = null;
                return;
            }

            if (!_showPaused) np = np with { IsPlaying = true };
            string key = np.Key;
            SetStatus(_keyboardProblem ?? $"{(np.IsPlaying ? "Playing" : "Paused")}: {np.Title} — {np.Artist}");
            if (key == _shownKey) { _pendingKey = null; return; }

            // Wait for the metadata to settle (title, then cover, often arrive separately),
            // and never write more often than every 3 seconds.
            if (key != _pendingKey) { _pendingKey = key; _pendingSince = DateTime.Now; return; }
            if (DateTime.Now - _pendingSince < TimeSpan.FromSeconds(1.5)) return;
            if (DateTime.Now - _lastWrite < TimeSpan.FromSeconds(3)) return;

            using var card = CardRenderer.Render(np);
            await _screen.ShowAsync(card);
            _shownKey = key;
            _pendingKey = null;
            _lastWrite = DateTime.Now;
            _restored = false;
            if (_keyboardProblem != null) { _keyboardProblem = null; Log("keyboard connected again"); }
            SetStatus($"{(np.IsPlaying ? "Playing" : "Paused")}: {np.Title} — {np.Artist}");
            Log($"shown: {np.Title} — {np.Artist} ({(np.IsPlaying ? "playing" : "paused")})");
        }
        catch (Exception e)
        {
            string msg = e is BragiException ? e.Message : "Keyboard error: " + e.Message;
            if (msg != _keyboardProblem)
            {
                // Say it once (log + notification), not on every retry.
                Log(e is BragiException ? msg : "error: " + e);
                _tray.ShowBalloonTip(5000, "TrackDeck", msg, ToolTipIcon.Info);
            }
            _keyboardProblem = msg;
            SetStatus(msg);
            _lastWrite = DateTime.Now.AddSeconds(7); // retry about every 10 seconds
        }
        finally { _busy.Release(); }
    }

    /// <summary>Puts the previous screen back. Returns null on success, or why it couldn't.</summary>
    private async Task<string?> RestoreAsync()
    {
        await _busy.WaitAsync();
        try { await RestoreCoreAsync(); return null; }
        catch (Exception e) { Log("restore error: " + e.Message); return e.Message; }
        finally { _busy.Release(); }
    }

    private async Task RestoreFromMenuAsync()
    {
        var error = await RestoreAsync();
        _tray.ShowBalloonTip(4000, "TrackDeck", error == null ? "Your previous screen is back." : "Couldn't restore your screen: " + error, ToolTipIcon.Info);
    }

    private async Task RestoreCoreAsync()
    {
        await _screen.RestoreAsync();
        _restored = true;
        _shownKey = null;
    }

    private bool _exiting;

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _timer.Stop();
        Log("exiting");

        // Put the previous screen back, but never let a stuck keyboard keep TrackDeck alive.
        var restore = RestoreAsync();
        string? error = await Task.WhenAny(restore, Task.Delay(15000)) == restore
            ? await restore
            : "the keyboard didn't answer in time.";
        await _screen.CloseAsync();

        _tray.Visible = false;
        _tray.Dispose();
        if (error != null)
            MessageBox.Show(
                $"TrackDeck closed, but couldn't put your previous screen back:\n{error}\n\n" +
                "Plug in the USB cable, start TrackDeck again and choose \"Restore my screen\" (or Exit).",
                "TrackDeck", MessageBoxButtons.OK, MessageBoxIcon.Information);
        Log("exited");
        ExitThread();
    }

    private void UpdateIdleTimeItem()
    {
        _idleTimeItem.Text = $"    after {FormatSeconds(_idleSeconds)}…";
        _idleTimeItem.Enabled = _restoreWhenIdle;
    }

    private static string FormatSeconds(int s) => s == 1 ? "1 second" : $"{s} seconds";

    private void AskIdleSeconds()
    {
        using var form = new Form
        {
            Text = "TrackDeck", FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterScreen,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, TopMost = true,
            AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(12),
        };
        var label = new Label
        {
            Text = "Put my screen back after music has been paused\nor stopped for this many seconds:",
            AutoSize = true, Margin = new Padding(0, 0, 0, 8),
        };
        var number = new NumericUpDown { Minimum = MinIdleSeconds, Maximum = MaxIdleSeconds, Value = _idleSeconds, Width = 100 };
        var unit = new Label { Text = "seconds", AutoSize = true, Margin = new Padding(6, 6, 0, 0) };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel };

        var row = new FlowLayoutPanel { AutoSize = true, Margin = new Padding(0, 0, 0, 12) };
        row.Controls.AddRange([number, unit]);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill };
        buttons.Controls.AddRange([cancel, ok]);
        var layout = new TableLayoutPanel { AutoSize = true, ColumnCount = 1 };
        layout.Controls.AddRange([label, row, buttons]);
        form.Controls.Add(layout);
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        form.Shown += (_, _) => { number.Select(); number.Select(0, number.Text.Length); };

        if (form.ShowDialog() != DialogResult.OK) return;
        _idleSeconds = (int)number.Value;
        UpdateIdleTimeItem();
        SaveSettings();
        Log($"idle time set to {_idleSeconds} s");
    }

    private void SetStatus(string s)
    {
        if (s.Length > 60) s = s[..57] + "…";
        _statusItem.Text = s;
        _tray.Text = s.Length > 63 ? s[..63] : s;
    }

    // ---------------- settings, startup, log ----------------

    private void LoadSettings()
    {
        try
        {
            foreach (var line in File.ReadAllLines(SettingsPath))
            {
                var parts = line.Split('=', 2);
                if (parts.Length != 2) continue;
                bool v = parts[1].Trim() == "true";
                switch (parts[0].Trim())
                {
                    case "idleSeconds" when int.TryParse(parts[1].Trim(), out int secs):
                        _idleSeconds = Math.Clamp(secs, MinIdleSeconds, MaxIdleSeconds); break;
                    case "spotifyOnly": _media.SpotifyOnly = v; break;
                    case "showPaused": _showPaused = v; break;
                    case "restoreWhenIdle": _restoreWhenIdle = v; break;
                }
            }
        }
        catch { /* defaults */ }
    }

    private void SaveSettings()
    {
        try
        {
            File.WriteAllLines(SettingsPath, [
                $"spotifyOnly={(_media.SpotifyOnly ? "true" : "false")}",
                $"showPaused={(_showPaused ? "true" : "false")}",
                $"restoreWhenIdle={(_restoreWhenIdle ? "true" : "false")}",
                $"idleSeconds={_idleSeconds}"]);
        }
        catch { }
    }

    private static bool IsStartupEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue("TrackDeck") != null;
    }

    private static void SetStartup(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue("TrackDeck", $"\"{Environment.ProcessPath}\"");
        else k.DeleteValue("TrackDeck", false);
    }

    private static string LogPath => Path.Combine(Dir, "log.txt");

    private static void Log(string s)
    {
        try
        {
            var fi = new FileInfo(LogPath);
            if (fi.Exists && fi.Length > 512 * 1024) fi.Delete();
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {s}{Environment.NewLine}");
        }
        catch { }
    }

    private static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var green = new SolidBrush(CardRenderer.Accent);
            g.FillEllipse(green, 1, 1, 30, 30);
            using var black = new SolidBrush(Color.FromArgb(18, 18, 18));
            g.FillRectangle(black, 9, 15, 4, 9);
            g.FillRectangle(black, 15, 9, 4, 15);
            g.FillRectangle(black, 21, 12, 4, 12);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
