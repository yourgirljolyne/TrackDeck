namespace TrackDeck;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        MigrateFromOldName();
        string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        if (cmd is "probe" or "list" or "backup" or "render" or "show" or "restore" or "screens" or "select")
        {
            AttachConsole();
            return cmd switch
            {
                "screens" => Diagnostics.Screens().GetAwaiter().GetResult(),
                "select" => Diagnostics.Select(ushort.Parse(args.ElementAtOrDefault(1) ?? "0")).GetAwaiter().GetResult(),
                "show" => Diagnostics.Show().GetAwaiter().GetResult(),
                "restore" => Diagnostics.Restore().GetAwaiter().GetResult(),
                "list" => Diagnostics.List(),
                "backup" => Diagnostics.Backup().GetAwaiter().GetResult(),
                "render" => Diagnostics.Render(args.Skip(1).FirstOrDefault(a => !a.StartsWith("--")) ?? "card.png").GetAwaiter().GetResult(),
                _ => Diagnostics.Probe().GetAwaiter().GetResult(),
            };
        }

        // Default: tray app, one instance only.
        ApplicationConfiguration.Initialize();
        // Admiral (the background program for all the Deck apps) shows the card now; two copies would fight over the screen.
        if (System.Threading.Mutex.TryOpenExisting("Admiral.SingleInstance", out var admiral))
        {
            admiral.Dispose();
            MessageBox.Show("Admiral is running and already shows the now-playing card, so TrackDeck isn't needed. " +
                "Its card settings are in Admiral's tray menu.", "TrackDeck", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        using var mutex = new Mutex(true, "TrackDeck.SingleInstance", out bool first);
        if (!first)
        {
            MessageBox.Show("TrackDeck is already running. Look for the green icon in the tray (it may be tucked under the ^ arrow).",
                "TrackDeck", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        Application.Run(new TrayApp());
        return 0;
    }

    /// <summary>The app was called NowPlayingCard before; carry over its data and startup entry once.</summary>
    private static void MigrateFromOldName()
    {
        try
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string oldDir = Path.Combine(local, "NowPlayingCard"), newDir = Path.Combine(local, "TrackDeck");
            if (Directory.Exists(oldDir) && !Directory.Exists(newDir))
            {
                Directory.CreateDirectory(newDir);
                foreach (var f in Directory.GetFiles(oldDir))
                    File.Copy(f, Path.Combine(newDir, Path.GetFileName(f)));
            }

            using var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (run?.GetValue("NowPlayingCard") != null)
            {
                run.DeleteValue("NowPlayingCard", false);
                run.SetValue("TrackDeck", $"\"{Environment.ProcessPath}\"");
            }
        }
        catch { /* best effort */ }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int pid);
    private static void AttachConsole()
    {
        AttachConsole(-1);
        var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
        Console.SetOut(stdout);
    }
}

internal static class Diagnostics
{
    /// <summary>Lists the keyboard's HID interfaces. Sends nothing.</summary>
    public static int List()
    {
        foreach (var (path, page, usage, inLen, outLen) in HidDevice.Describe(Bragi.Vid, Bragi.PidAir99Wired))
            Console.WriteLine($"page=0x{page:X4} usage=0x{usage:X2} in={inLen} out={outLen}  {path}");
        return 0;
    }

    /// <summary>Starts a session and only READS properties. Changes nothing on the keyboard.</summary>
    public static async Task<int> Probe()
    {
        using var b = Bragi.Open();
        if (b == null) { Console.WriteLine("Keyboard control interface not found (is it plugged in by USB?)."); return 1; }
        b.Log = s => Console.WriteLine("  · " + s);
        Console.WriteLine($"Opened {b.DevicePath}  report length {b.ReportLength}");

        await b.StartSessionAsync();
        foreach (var (id, name) in new (ushort, string)[] {
            (Bragi.PropOperatingMode, "operating mode"),
            (Bragi.PropScreenPresent, "screen present"),
            (Bragi.PropScreenWidth, "screen width"),
            (Bragi.PropScreenHeight, "screen height"),
            (Bragi.PropScreenIndex, "screen index"),
            (Bragi.PropFreeStorage, "free storage")})
        {
            try { Console.WriteLine($"{name,-15} ({id,3}) = {await b.GetPropertyAsync(id)}"); }
            catch (Exception e) { Console.WriteLine($"{name,-15} ({id,3}) ! {e.Message}"); }
        }
        await b.StopSessionAsync();
        return 0;
    }

    /// <summary>Renders the card for what's playing now (or sample data) to a PNG. Doesn't touch the keyboard.</summary>
    public static async Task<int> Render(string outPath)
    {
        NowPlaying? np = null;
        bool sample = Environment.GetCommandLineArgs().Contains("--sample");
        if (!sample)
            try { np = await new MediaReader { SpotifyOnly = false }.ReadAsync(); } catch (Exception e) { Console.WriteLine("media: " + e.Message); }
        np ??= new NowPlaying("Midnight Drive Through the Neon City Lights", "The Night Owls", "Afterglow (Deluxe Edition)", true, SampleCover(), "sample");
        Console.WriteLine($"{np.Source}: {np.Title} — {np.Artist} [{np.Album}] playing={np.IsPlaying} cover={np.Cover?.Length ?? 0}B");
        using var card = CardRenderer.Render(np);
        card.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
        using var big = new System.Drawing.Bitmap(card.Width * 3, card.Height * 3);
        using (var g = System.Drawing.Graphics.FromImage(big))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.DrawImage(card, 0, 0, big.Width, big.Height);
        }
        big.Save(Path.ChangeExtension(outPath, ".x3.png"), System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine("Saved " + Path.GetFullPath(outPath));
        return 0;
    }

    /// <summary>Renders what's playing (or sample data) and puts it on the keyboard screen once.</summary>
    public static async Task<int> Show()
    {
        NowPlaying? np = null;
        try { np = await new MediaReader { SpotifyOnly = false }.ReadAsync(); } catch { }
        np ??= new NowPlaying("Midnight Drive Through the Neon City Lights", "The Night Owls", "Afterglow (Deluxe Edition)", true, null, "sample");
        using var card = CardRenderer.Render(np);
        using var sc = new ScreenController { Log = s => Console.WriteLine("  · " + s) };
        await sc.ShowAsync(card);
        await sc.CloseAsync();
        Console.WriteLine($"Shown: {np.Title} — {np.Artist}");
        return 0;
    }

    /// <summary>A made-up gradient "album cover" for screenshots.</summary>
    private static byte[] SampleCover()
    {
        using var bmp = new System.Drawing.Bitmap(300, 300);
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var grad = new System.Drawing.Drawing2D.LinearGradientBrush(new System.Drawing.Point(0, 0), new System.Drawing.Point(300, 300),
                System.Drawing.Color.FromArgb(255, 95, 109), System.Drawing.Color.FromArgb(91, 60, 196));
            g.FillRectangle(grad, 0, 0, 300, 300);
            using var sun = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(90, 255, 220, 120));
            g.FillEllipse(sun, 80, 60, 140, 140);
            using var ground = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(120, 20, 10, 40));
            g.FillRectangle(ground, 0, 190, 300, 110);
        }
        using var ms = new MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return ms.ToArray();
    }

    public static async Task<int> Restore()
    {
        using var sc = new ScreenController { Log = s => Console.WriteLine("  · " + s) };
        await sc.RestoreAsync();
        await sc.CloseAsync();
        return 0;
    }

    /// <summary>Shows a stored screen entry (copies it into the active-screen file, like Web Hub does).</summary>
    public static async Task<int> Select(ushort entryId)
    {
        using var b = Bragi.Open();
        if (b == null) { Console.WriteLine("Keyboard not found. Plug in the USB cable."); return 1; }
        await b.StartSessionAsync();
        var entry = await b.ReadFileAsync(entryId);
        if (entry is not { Length: > 0 } || entry[0] is not (56 or 102)) { Console.WriteLine($"{entryId} is not an image or GIF entry."); return 1; }
        await b.WriteFileAsync(ScreenFiles.ActiveScreen, entry);
        await b.StopSessionAsync();
        Console.WriteLine($"Selected {entryId} ({(entry[0] == 102 ? "GIF" : "image")}).");
        return 0;
    }

    /// <summary>Lists screen entries stored on the keyboard (file ids 28000-29000). Read-only.</summary>
    public static async Task<int> Screens()
    {
        using var b = Bragi.Open();
        if (b == null) { Console.WriteLine("Keyboard not found. Plug in the USB cable."); return 1; }
        await b.StartSessionAsync();
        for (ushort id = 28000; id <= 29000; id++)
        {
            byte[]? d;
            try { d = await b.ReadFileAsync(id); } catch (BragiException) { continue; }
            if (d is not { Length: > 1 }) continue;
            string kind = d[0] switch { 56 => "image entry", 102 => "GIF entry", 72 => "image data", 69 => "GIF data", _ => $"type {d[0]}" };
            string detail = d[0] switch
            {
                56 => $"-> image {BitConverter.ToUInt16(d, 2)}",
                102 when d.Length >= 6 => $"delay {BitConverter.ToUInt16(d, 2)} ms, {BitConverter.ToUInt16(d, 4)} frames: " +
                    string.Join(",", Enumerable.Range(0, (d.Length - 6) / 2).Select(i => BitConverter.ToUInt16(d, 6 + i * 2)).Take(12)) + "…",
                _ => Convert.ToHexString(d.AsSpan(0, Math.Min(16, d.Length)))
            };
            if (d[0] != 72) Console.WriteLine($"{id}: {kind,-12} {d.Length,6} bytes  {detail}");
        }
        await b.StopSessionAsync();
        return 0;
    }

    /// <summary>Reads the screen files we will touch and saves them to backups\. Changes nothing.</summary>
    public static async Task<int> Backup()
    {
        using var b = Bragi.Open();
        if (b == null) { Console.WriteLine("Keyboard control interface not found."); return 1; }
        await b.StartSessionAsync();
        var dir = Path.Combine(AppContext.BaseDirectory, "backups", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(dir);
        foreach (ushort id in new ushort[] { ScreenFiles.ResourceMap, ScreenFiles.ActiveScreen, ScreenFiles.OurConfig, ScreenFiles.OurImage })
        {
            var data = await b.ReadFileAsync(id);
            if (data == null) { Console.WriteLine($"file {id}: (does not exist)"); continue; }
            File.WriteAllBytes(Path.Combine(dir, $"file-{id}.bin"), data);
            Console.WriteLine($"file {id}: {data.Length} bytes  {Convert.ToHexString(data.AsSpan(0, Math.Min(32, data.Length)))}");
        }
        // Follow the active screen config to its resource, to learn the current format.
        var active = await b.ReadFileAsync(ScreenFiles.ActiveScreen);
        if (active is { Length: >= 4 } && active[0] == ScreenFiles.ConfigTypeImage)
        {
            ushort res = BitConverter.ToUInt16(active, 2);
            var img = await b.ReadFileAsync(res);
            Console.WriteLine($"active image resource {res}: {img?.Length ?? 0} bytes  {Convert.ToHexString((img ?? []).AsSpan(0, Math.Min(70, img?.Length ?? 0)))}");
            if (img != null) File.WriteAllBytes(Path.Combine(dir, $"file-{res}.bin"), img);
        }
        await b.StopSessionAsync();
        Console.WriteLine($"Saved to {dir}");
        return 0;
    }
}
