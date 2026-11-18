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

    private readonly ToolStripMenuItem _statusItem = new("Warming up…") { Enabled = false };
    private readonly ToolStripMenuItem _spotifyOnlyItem = new("Spotify only") { CheckOnClick = true };
    private readonly ToolStripMenuItem _showPausedItem = new("Show paused state") { CheckOnClick = true };
    private readonly ToolStripMenuItem _restoreIdleItem = new("Restore my screen when nothing plays") { CheckOnClick = true };
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
        _restoreIdleItem.CheckedChanged += (_, _) => { _restoreWhenIdle = _restoreIdleItem.Checked; SaveSettings(); };
        _startupItem.CheckedChanged += (_, _) => SetStartup(_startupItem.Checked);

        var menu = new ContextMenuStrip();
        menu.Items.Add(_statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Refresh card now", null, (_, _) => { _shownKey = null; _lastWrite = DateTime.MinValue; });
        menu.Items.Add("Restore my screen", null, async (_, _) => await RestoreAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_spotifyOnlyItem);
        menu.Items.Add(_showPausedItem);
        menu.Items.Add(_restoreIdleItem);
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

            if (np == null)
            {
                SetStatus(_media.SpotifyOnly ? "Spotify is quiet. Press play!" : "Nothing playing. Suspiciously quiet.");
                _pendingKey = null;
                _idleSince ??= DateTime.Now;
                if (_restoreWhenIdle && !_restored && DateTime.Now - _idleSince > TimeSpan.FromSeconds(60))
                    await RestoreCoreAsync();
                return;
            }
            _idleSince = null;

            if (!_showPaused) np = np with { IsPlaying = true };
            string key = np.Key;
            SetStatus(_keyboardProblem ?? $"{(np.IsPlaying ? "▶" : "⏸")} {np.Title} — {np.Artist}");
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
            SetStatus($"{(np.IsPlaying ? "▶" : "⏸")} {np.Title} — {np.Artist}");
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

    private async Task RestoreAsync()
    {
        await _busy.WaitAsync();
        try { await RestoreCoreAsync(); }
        catch (Exception e) { Log("restore error: " + e.Message); }
        finally { _busy.Release(); }
    }

    private async Task RestoreCoreAsync()
    {
        await _screen.RestoreAsync();
        _restored = true;
        _shownKey = null;
    }

    private async Task ExitAsync()
    {
        _timer.Stop();
        await RestoreAsync();
        _tray.Visible = false;
        _screen.Dispose();
        ExitThread();
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
                $"restoreWhenIdle={(_restoreWhenIdle ? "true" : "false")}"]);
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
