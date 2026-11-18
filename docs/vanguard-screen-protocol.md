# VANGUARD AIR 99 screen protocol notes

Source: Corsair Web Hub (`https://www.corsair.com/web-hub/`, bundle `assets/B1aWXCAu.js` + `assets/Dde8HGGV.js`), read 2026-11-17.
Nothing here has been tested against the keyboard yet.

## Device

| | |
|---|---|
| VID / PID | `0x1B1C` / `0x2B21` (wired). Web Hub product id `11041`. |
| Dongles | SLIPSTREAM `0x2B00`, SLIPSTREAM v2 `0x2B1B` (sub-device addressing, not covered yet) |
| HID interface | the collection with usage page `0xFF42`, usage `0x01` |
| Report ID | `0` |
| Report length | from the descriptor's output report (fallback 64 bytes); pad every packet to this length |
| Protocol | "Bragi" v1.5 (4-byte header). Web Hub hard-codes v1.5 for this PID. |
| Screen area usable in hardware mode | **248 × 170** (Web Hub hard-codes this for the Air 99; panel is 320 × 170) |

## Packet header (v1.5)

```
byte 0  sub-device address (0 for the keyboard itself over USB)
byte 1  1  (= expect an answer)
byte 2  session id (from START SESSION; 0 for the session command itself)
byte 3  command id
byte 4… payload
```

Response: byte 3 echoes the command id, byte 4 is status. Byte 1 == 15 → session expired; byte 4 == 3 → invalid state. On either, start a new session and retry.

## Commands

| id | name | payload |
|---|---|---|
| 1  | SET property | prop id (u16 LE), value (4 bytes) |
| 2  | GET property | prop id (u16 LE) → value in response bytes 5..8 |
| 3  | GET multiple | count, prop ids (u16 LE each) |
| 5  | UNBIND buffers | count, buffer indexes… |
| 6  | WRITE begin | buffer idx, total length (u32 LE), first data chunk |
| 7  | WRITE continue | buffer idx, data chunk |
| 8  | READ buffer | buffer idx |
| 9  | DESCRIBE buffer | buffer idx → size u32 at response data[2..6] |
| 11 | CREATE file | file id (u16 LE) |
| 12 | DELETE file | file id (u16 LE) |
| 13 | OPEN file | buffer idx, file id (u16 LE) |
| 15 | FACTORY RESET | ⚠️ never send |
| 18 | PING / pairing | |
| 27 | SESSION control | start: `1, 4 random bytes, 0` → session id at response byte 9. stop: `2, 0,0,0,0, sessionId` |

Chunk sizes: WRITE begin carries `reportLen - (4 + 5)` bytes; each WRITE continue carries 4 more than that.
Buffer index 0 = "common file index".

## Properties used by the screen

| id | meaning |
|---|---|
| 3   | operating mode: 1 SELF_OPERATED, 2 HOST_CONTROLLED, 3/5 bootloader ⚠️, 4 sleep |
| 62  | free storage bytes (u32) |
| 230 | screen module present |
| 242 | screen width |
| 243 | screen height |
| 263 | current screen index in the profile's screen list |

## How Web Hub puts an image on the screen

1. SET property 3 = 2 (HOST_CONTROLLED).
2. Write the image as a **file** (OPEN → if that fails UNBIND + CREATE + OPEN → WRITE begin/continue → UNBIND). File ids are picked from 28000–32767.
   - Image file format for this model: `0x48 0x00` + 16-bit RGB565 BMP (BI_BITFIELDS, masks F800/07E0/001F, rows top-down as written) + 4-byte unix timestamp.
   - GIFs: each frame is its own BMP file, plus a "GIF config" file: `u16 102, u16 frameDelayMs, u16 count, u16 fileId…`.
3. Write a small "screen config" file: `56, 0, resourceId (u16 LE)`.
4. Add the resource to the resource map (file 61) and the config id to the profile's screen-mode layout file.
5. To **show** it: read the config file and copy its bytes into **file 62** (the active-screen file), then set property 263 to its index.
6. SET property 3 = 1 (SELF_OPERATED).

## What this means for a live widget

- Web Hub has **no live streaming**. Everything goes to the keyboard's flash storage.
- Rewriting flash several times a second would wear it out, so a 30 fps widget can't use this path.
- What *is* safe with this path: write one 248×170 image **when the track changes** (reusing the same file id) and point file 62 at it. That's a "now playing card": cover, title, artist, play/pause. No animation or scrolling.
- iCUE's live widgets must use another mechanism (probably a frame-streaming command not present in Web Hub). Finding it needs a USB capture of iCUE while a widget runs (Wireshark + USBPcap).

## Also seen

Web Hub talks to a running iCUE over a local WebSocket (`icueWebSocketWorker`) with `releaseDevice` / `restoreDevice` messages so the two don't fight over the keyboard.

## Wireless (tested 2026-11-18)

- The keyboard pairs to a **SLIPSTREAM v2** receiver (`0x2B1B`, Bragi v1.5 per Web Hub). Its vendor channel (`0xFF42` usage 1, 64-byte reports) never answers: interrupt writes hang, and control-transfer writes (`HidD_SetOutputReport`) are accepted but get no reply, with or without a session. This happened with iCUE, SignalRGB and Stream Deck all closed and after replugging.
- Corsair Web Hub can't reach the keyboard through this receiver either.
- The older **SLIPSTREAM** receiver (`0x2B00`) does answer, in Bragi v1.0 (2-byte header `[0x08 | sub, cmd]`, status at byte 2, data from byte 3). Property 54 returns a bitmask of paired devices; the device behind it here had no screen (likely a mouse).
- Over Bluetooth LE the keyboard shows up as VID `0x0A82` / PID `0x2B21`; no vendor channel was available while it was on the receiver.

Conclusion: screen updates need the USB cable.
