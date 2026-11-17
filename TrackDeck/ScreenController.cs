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
        _bragi = Bragi.Open() ?? throw new BragiException("Keyboard not found. Is it connected by USB cable?");
        _bragi.Log = Log;
        await _bragi.StartSessionAsync();
        _prepared = false;
        return _bragi;
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

    /// <summary>Puts back whatever the screen showed before we started.</summary>
    public async Task RestoreAsync()
    {
        if (_originalActive == null)
        {
            string origPath = Path.Combine(StateDir, "original-active-screen.bin");
            if (File.Exists(origPath)) _originalActive = File.ReadAllBytes(origPath);
        }
        if (_originalActive == null) return;
        await WithReconnect(async b =>
        {
            await b.WriteFileAsync(ScreenFiles.ActiveScreen, _originalActive);
            Log?.Invoke("restored original screen");
        });
    }

    private async Task WithReconnect(Func<Bragi, Task> action)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { await action(await ConnectAsync()); return; }
            catch (Exception e) when (attempt == 0 && e is BragiException or IOException)
            {
                Log?.Invoke("retrying after: " + e.Message);
                _bragi?.Dispose();
                _bragi = null;
            }
        }
    }

    public void Dispose()
    {
        if (_bragi != null)
        {
            try { _bragi.StopSessionAsync().GetAwaiter().GetResult(); } catch { }
            _bragi.Dispose();
        }
    }
}
