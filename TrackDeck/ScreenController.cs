namespace TrackDeck;

/// <summary>
/// Puts a card on the keyboard screen using the same file layout as Corsair Web Hub.
/// Writes per update: the image file (~84 KB) and the 16-byte active-screen pointer.
/// Never touches lighting, macros, key assignments or profiles.
/// </summary>
public sealed class ScreenController : IDisposable
{
    private Bragi? _bragi;
    private byte[]? _originalActive;   // what the screen showed before we took over
    private bool _prepared;
    public Action<string>? Log { get; set; }

    private string StateDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrackDeck");

    private async Task<Bragi> ConnectAsync()
    {
        if (_bragi != null) return _bragi;
        _bragi = Bragi.Open() ?? throw new BragiException(NotFoundMessage());
        _bragi.Log = Log;
        await _bragi.StartSessionAsync();
        _prepared = false;
        return _bragi;
    }

    /// <summary>
    /// The screen can only be updated over the USB cable: the SLIPSTREAM receivers don't pass these
    /// commands on (Corsair's own Web Hub can't reach the screen wirelessly either).
    /// </summary>
    private static string NotFoundMessage()
    {
        bool receiver = Bragi.ReceiverIds.Any(pid => HidDevice.Describe(Bragi.Vid, pid).Any());
        return receiver
            ? "Keyboard is wireless. Plug in the USB cable to update the screen."
            : "Keyboard not found. Plug it in with the USB cable.";
    }

    /// <summary>One-time setup: remember the user's screen, make sure our config + map entry exist.</summary>
    private async Task PrepareAsync(Bragi b)
    {
        if (_prepared) return;
        Directory.CreateDirectory(StateDir);
        string origPath = Path.Combine(StateDir, "original-active-screen.bin");

        var active = await b.ReadFileAsync(ScreenFiles.ActiveScreen);
        var ours = ScreenFiles.ImageConfig(ScreenFiles.OurImage);
        if (active != null && !active.AsSpan().SequenceEqual(ours))
        {
            _originalActive = active;
            File.WriteAllBytes(origPath, active);
        }
        else if (File.Exists(origPath))
        {
            _originalActive = File.ReadAllBytes(origPath);
        }

        var cfg = await b.ReadFileAsync(ScreenFiles.OurConfig);
        if (cfg == null || !cfg.AsSpan().SequenceEqual(ours))
        {
            Log?.Invoke("writing screen config file");
            await b.WriteFileAsync(ScreenFiles.OurConfig, ours);
        }

        var map = await b.ReadFileAsync(ScreenFiles.ResourceMap) ?? [];
        var newMap = ScreenFiles.AddToResourceMap(map, ScreenFiles.OurImage);
        if (newMap != null)
        {
            File.WriteAllBytes(Path.Combine(StateDir, $"resource-map-before-{DateTime.Now:yyyyMMddHHmmss}.bin"), map);
            Log?.Invoke("adding image to resource map");
            await b.WriteFileAsync(ScreenFiles.ResourceMap, newMap);
        }
        _prepared = true;
    }

    public async Task ShowAsync(System.Drawing.Bitmap card)
    {
        var package = ScreenFiles.ImagePackage(CardRenderer.ToRgba(card), card.Width, card.Height);
        await WithReconnect(async b =>
        {
            await PrepareAsync(b);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await b.WriteFileAsync(ScreenFiles.OurImage, package);
            await b.WriteFileAsync(ScreenFiles.ActiveScreen, ScreenFiles.ImageConfig(ScreenFiles.OurImage));
            Log?.Invoke($"card written ({package.Length} bytes, {sw.ElapsedMilliseconds} ms)");
        });
    }

    /// <summary>
    /// Re-selects the screen that was selected when TrackDeck took over (an image or a GIF entry),
    /// the same way Web Hub selects a screen: copy its entry into the active-screen file.
    /// </summary>
    public async Task RestoreAsync()
    {
        string origPath = Path.Combine(StateDir, "original-active-screen.bin");
        var target = _originalActive ?? (File.Exists(origPath) ? File.ReadAllBytes(origPath) : null)
            ?? throw new BragiException("TrackDeck doesn't know which screen you had before.");
        await WithReconnect(async b =>
        {
            await b.WriteFileAsync(ScreenFiles.ActiveScreen, target);
            Log?.Invoke($"restored previous screen ({(target[0] == ScreenFiles.ConfigTypeGif ? "GIF" : "image")})");
        });
    }

    /// <summary>Runs an action; if an existing connection fails (e.g. cable replugged), reconnects once and retries.</summary>
    private async Task WithReconnect(Func<Bragi, Task> action)
    {
        bool hadConnection = _bragi != null;
        try { await action(await ConnectAsync()); }
        catch (Exception e) when (hadConnection && e is BragiException or IOException)
        {
            Log?.Invoke("reconnecting after: " + e.Message);
            _bragi?.Dispose();
            _bragi = null;
            await action(await ConnectAsync());
        }
    }

    /// <summary>Ends the keyboard session (best effort, never waits more than 2 s) and releases the device.</summary>
    public async Task CloseAsync()
    {
        if (_bragi == null) return;
        try { await Task.WhenAny(_bragi.StopSessionAsync(), Task.Delay(2000)); } catch { }
        Dispose();
    }

    /// <summary>Releases the device without talking to it. Never blocks.</summary>
    public void Dispose()
    {
        _bragi?.Dispose();
        _bragi = null;
    }
}
