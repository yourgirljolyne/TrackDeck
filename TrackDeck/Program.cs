namespace TrackDeck;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        MigrateFromOldName();
        string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "";
        if (cmd is "probe" or "list" or "backup" or "render" or "show" or "restore")
        {
            AttachConsole();
            return cmd switch
            {
                "show" => Diagnostics.Show().GetAwaiter().GetResult(),
                "restore" => Diagnostics.Restore().GetAwaiter().GetResult(),
                "list" => Diagnostics.List(),
                "backup" => Diagnostics.Backup().GetAwaiter().GetResult(),
                "render" => Diagnostics.Render(args.Skip(1).FirstOrDefault(a => !a.StartsWith("--")) ?? "card.png").GetAwaiter().GetResult(),
                _ => Diagnostics.Probe().GetAwaiter().GetResult(),
            };
        }

        // Default: tray app, one instance only.
        using var mutex = new Mutex(true, "TrackDeck.SingleInstance", out bool first);
        if (!first) return 0;
        ApplicationConfiguration.Initialize();
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
