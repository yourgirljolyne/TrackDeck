# Now Playing Card for the VANGUARD AIR 99

Show what Spotify is playing on the screen of a Corsair **VANGUARD AIR 99** keyboard, **without iCUE**.

![Card preview](docs/images/card.png)

A tiny Windows tray app reads the current track from Windows, draws a 248×170 card (cover, title, artist, album, play/pause) and sends it to the keyboard over USB whenever the song changes. Your lighting, macros, key assignments and profiles are never touched.

> Not affiliated with or endorsed by Corsair. The screen protocol was worked out from Corsair's public Web Hub app. Use at your own risk.

## Download and run

1. Get `NowPlayingCard.exe` from the [Releases](../../releases) page, or build it (below).
2. Install the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) if you don't have it.
3. Connect the keyboard with the **USB cable** and **close iCUE**.
4. Run `NowPlayingCard.exe`. A green icon appears in the tray. Play something in Spotify.

Right-click the tray icon for options: Spotify only or any media app, show the paused state, restore your previous screen when nothing plays, start with Windows. **Exit** puts your previous screen back.

## How it works

- **Media:** Windows' System Media Transport Controls (the same info as the volume flyout), preferring Spotify.
- **Keyboard:** Corsair's "Bragi" HID protocol on the vendor interface (usage page `0xFF42`). The card is stored as an image file on the keyboard, and the screen is pointed at it, the same way Web Hub uploads images.
- **Flash wear:** the screen lives in flash storage, so the card is rewritten only when the track or play state changes (about one 84 KB write per song, at most one write every 3 seconds). No animations or progress bar for that reason.

Full protocol notes: [docs/vanguard-screen-protocol.md](docs/vanguard-screen-protocol.md).

## Build

Requires the .NET 10 SDK on Windows.

```
cd NowPlayingCard
dotnet publish -c Release -r win-x64 -o dist -p:PublishSingleFile=true -p:SelfContained=false
```

## Command line

```
NowPlayingCard list            list the keyboard's HID interfaces (sends nothing)
NowPlayingCard probe           read screen properties (changes nothing)
NowPlayingCard backup          save the screen files it uses to backups\
NowPlayingCard render x.png    draw the current card to a PNG (add --sample for demo data)
NowPlayingCard show            put the current card on the screen once
NowPlayingCard restore         put your previous screen back
```

Running `backup` once before first use is a good idea.

## Also in this repo

[`icue-widget/`](icue-widget) is an HTML "Now Playing" widget for **iCUE**'s widget system (320×170, live updates, blurred album-art background), for people who do use iCUE.

## Limitations

- Tested on the VANGUARD AIR 99 over USB only. The wireless receiver and other Vanguard models are not supported yet (they likely use the same protocol with different IDs).
- iCUE and this app will fight over the keyboard; run one or the other.

## License

[MIT](LICENSE)
