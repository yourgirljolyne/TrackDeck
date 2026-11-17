# TrackDeck (source)

C# / .NET 10 WinForms tray app. No NuGet packages, no iCUE, no drama.

| File | What it does |
|---|---|
| `Hid.cs` | Finds and talks to HID interfaces via SetupAPI and hid.dll |
| `Bragi.cs` | Corsair Bragi v1.5 client: session, get/set property, read/write files |
| `ScreenFiles.cs` | File ids and formats (image package, screen config, resource map) |
| `ScreenController.cs` | Puts a card on the screen; remembers and restores the previous screen |
| `Media.cs` | Reads the current track from Windows (SMTC) |
| `CardRenderer.cs` | Draws the 248×170 card |
| `TrayApp.cs` | Tray icon, polling loop, debounce and rate limit, settings |
| `Program.cs` | Entry point and command-line tools |

Files written on the keyboard: `32700` (screen config), `32701` (image), `62` (active screen pointer), and once, `61` (resource map). The app never sends lighting, key assignment, profile or factory reset commands.

App data (settings, log, saved original screen): `%LOCALAPPDATA%\TrackDeck\`.
