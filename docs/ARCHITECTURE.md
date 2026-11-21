# Architecture

C# / WPF on .NET 10 (`net10.0-windows10.0.19041.0`). No NuGet packages in the app itself: everything (QR encoder, FFT, RSS/LRC/YAML parsers, Core Audio and WASAPI interop) is written in the project.

## The window

`MainWindow` is one transparent, borderless, topmost window (`AllowsTransparency`) 600 px wide, glued to the top edge of the primary monitor. The visible island is the `Island` border inside it; its size, corner radius and the cross-fade of the content are driven by one animated value, `Progress` (0 = collapsed, 1 = expanded).

The window is split into partial classes by concern:

| File | Concern |
| --- | --- |
| `MainWindow.xaml.cs` | expand/collapse animation, sizes, auto-collapse, overlays (Claude / Alt+Q cards), drag and drop |
| `MainWindow.Tabs.cs` | the tab header (plates, labels, clock) and tab layout |
| `MainWindow.Activities.cs` | passive activities: main pill, up to two circles, swap animation |
| `MainWindow.Blur.cs` | glass layers and the blur window (see below) |
| `MainWindow.Glow.cs`, `Backdrop/` | coloured background (cover, weather, tab palettes) |
| `MainWindow.Outline.cs` | adaptive light outline on dark backgrounds |
| `MainWindow.Night.cs` | night dimming |

## Modules (tabs)

A tab is an `IIslandModule` (`Modules/IIslandModule.cs`): id, icon, expanded size, a view factory and optional background palette. Modules are listed in `ModuleCatalog`. Every module is created at startup, because many keep passive activities alive without an open tab (the music wave, the timer, the phone link).

## Passive activities and flashes

`Services/ActivityCenter` keeps a list of `ActivityEntry` (pill view, size, priority, bubble, wide view, notch view…). The highest priority is the main pill; the next two become circles; clicking a circle swaps it with the main pill. Short one-off hints (Caps Lock, volume, phone messages…) use `ShowFlash` and replace the main pill for a moment.

`Services/IslandBus` is the small event bus between modules and the window (request height, hold open, show overlay, settings changed…).

## Glass

The island is drawn in a per-pixel-alpha window, where system blur is not available. So:

1. `Backdrop/BlurBehind` is a separate borderless window placed *directly under* the island. It uses the **acrylic** accent policy and a rounded window region that follows the island's shape, so the blur reaches the edges.
2. The island paints its own translucent fill (`Backdrop/GlassBrush`, denser towards the side edges), a tint layer per tab, a thin edge light and a soft highlight on top.
3. Resizing a blurred window is expensive, so the blur window is repositioned only when the island settles (see `UpdateBlurBounds`), not on every animation frame.

## Services worth knowing

- `AppSettings` — one JSON file in `%AppData%\Ostrov`; always `Load()` → change → `Save()`.
- `OpenAiKeyStore` — API key encrypted with DPAPI.
- `ServerTunnel` — optional `ssh.exe` port forward to the user's own server, only for OpenAI traffic.
- `FullscreenWatcher` — foreground app polling: fullscreen video / game, browsers (notch mode).
- `Claude/` — named-pipe bridge to the Claude Code hook script (`tools/ostrov-hook.ps1`).
- `Search/` — Alt+Q card: calculator, commands, local answers, OpenAI streaming client, AI actions (reminders, currency).
- `Dictation/` — low-level keyboard hook (push-to-talk), WASAPI microphone capture, realtime transcription over WebSocket, text typing.
- `Modules/Phone/` — Phone Link notifications through `UserNotificationListener`, battery through Bluetooth device properties, and the QR bridge (`Bridge/`: own HTTP server, own QR encoder).

## Tests

`tests/Ostrov.Tests` (xUnit) covers the pure logic: calculator, Alt+Q command parser, lyrics parsers (LRC and word-synced), news ranking and the QR encoder.
