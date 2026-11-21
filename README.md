<div align="center">

# Ostrov — a Dynamic-Island-style launcher for Windows 11

A small black pill at the top of your screen that expands into music, weather, timers, projects, clipboard, phone notifications, Claude Code requests, quick AI answers and more.

**[Русская версия](README.ru.md)** · [Design playground](docs/island-demo.html) · [Architecture](docs/ARCHITECTURE.md) · [Contributing](CONTRIBUTING.md)

<img src="docs/images/tabs.gif" alt="Ostrov tabs" width="560">

</div>

> **Status: personal project, rough edges included.** Built by one person (with a lot of help from an AI coding assistant, Claude Code, which wrote most of the code under the author's direction and review) and tested on a single machine: Windows 11, one monitor, Apple Music, VS Code, an iPhone. Things may break elsewhere. Bug reports and ideas are very welcome.
>
> The screenshots above are a design preview rendered from the HTML playground in `docs/` with fake data, not captures of real data.

## What it does

The island lives at the top edge of the screen. Collapsed, it is a tiny pill that can show passive activities (timer, render progress, music, downloads, Claude working…). Click it to expand into a header with tabs.

| Tab | What you get |
| --- | --- |
| **Home** | Bluetooth, VPN status, timer, sleep, power; phone battery and last notification; CPU / GPU / RAM rings; mini player; weather tile |
| **Player** | Cover, controls and synced lyrics (word-by-word when available) for **Apple Music** via Windows media controls; coloured background from the cover; music wave from the real audio spectrum |
| **Weather** | Open-Meteo forecast, hour-by-hour chart, a one-line summary ("rain until 8 pm"), quiet rain and snow in the background |
| **Time** | iPhone-style timer wheels, stopwatch with laps; the running timer shows in the collapsed island |
| **Projects** | One-click open of a folder in VS Code, pinning, search, "open now" dot |
| **Buffer** | Clipboard history (text and images) and a drag-and-drop file shelf |
| **Phone** | Battery and notifications of a paired **iPhone** (through Windows *Phone Link*), a QR-code bridge to send files and text between phone and PC over your home network |
| **Settings** | Glass level, blur, night mode, hotkeys, dictation, apps that shrink the island, optional SSH tunnel |

Plus things that work without opening a tab:

- **Claude Code integration** — permission requests and questions appear as cards on the island ("Allow / Deny"); "Claude is working / finished" with the current step and a list of sessions. Uses Claude Code *hooks*.
- **Quick answers (Alt+Q)** — a calculator (`15% of 200`, `sqrt(2)*3`), commands (`timer 25`, `volume 30`, `open project`), currency conversion, day summaries, and AI answers streamed straight onto the island (OpenAI API, your own key).
- **Push-to-talk dictation** — hold the right Alt key, speak (Russian and English), release: the text is typed into the active window.
- **After Effects render notifications** — a small script inside AE reports render progress and completion.
- **Notch mode** — in browsers and fullscreen video the island shrinks into a thin notch with a progress bar; in fullscreen games it hides.
- **Short hints** — Caps Lock, input language, volume and microphone flashes; browser download progress; low disk space warning; reminders and "do X after render" set in plain language.

## Requirements

- Windows 11 (Windows 10 may partly work; untested)
- To run a release build: the **.NET 10 Desktop Runtime**
- To build: **.NET 10 SDK**

## Build and run

```powershell
git clone https://github.com/ment4l-immo/ostrov.git
cd ostrov
dotnet run --project src/Ostrov
```

or publish a local copy and start it (closes a running instance first):

```powershell
.\Ostrov-update.bat      # publishes to dist\ and launches dist\Ostrov.exe
```

Run the logic tests with `dotnet test tests/Ostrov.Tests`.

Release builds are **not code-signed**, so Windows SmartScreen will warn on first launch. The phone bridge asks Windows for firewall access the first time you open it.

## Optional setup

**OpenAI features** (Alt+Q answers, dictation, news summaries): paste your own API key in *Settings → AI & search*. It is stored encrypted with Windows DPAPI under `%AppData%\Ostrov` and never leaves your machine except in requests to OpenAI. You can also set the `OPENAI_API_KEY` environment variable.

**Claude Code hooks** (permission cards, "working / finished" status): add the hook to your Claude Code `settings.json` (global or per project), with the real path to this repository:

```json
{
  "hooks": {
    "PermissionRequest": [{ "hooks": [{ "type": "command", "timeout": 3600,
      "command": "powershell -NoProfile -ExecutionPolicy Bypass -File \"C:\\path\\to\\ostrov\\tools\\ostrov-hook.ps1\"" }] }],
    "UserPromptSubmit": [{ "hooks": [{ "type": "command", "timeout": 10,
      "command": "powershell -NoProfile -ExecutionPolicy Bypass -File \"C:\\path\\to\\ostrov\\tools\\ostrov-hook.ps1\"" }] }],
    "Stop": [{ "hooks": [{ "type": "command", "timeout": 10,
      "command": "powershell -NoProfile -ExecutionPolicy Bypass -File \"C:\\path\\to\\ostrov\\tools\\ostrov-hook.ps1\"" }] }],
    "PreToolUse": [{ "matcher": "*", "hooks": [{ "type": "command", "timeout": 10,
      "command": "powershell -NoProfile -ExecutionPolicy Bypass -File \"C:\\path\\to\\ostrov\\tools\\ostrov-hook.ps1\"" }] }],
    "PostToolUse": [{ "matcher": "*", "hooks": [{ "type": "command", "timeout": 10,
      "command": "powershell -NoProfile -ExecutionPolicy Bypass -File \"C:\\path\\to\\ostrov\\tools\\ostrov-hook.ps1\"" }] }]
  }
}
```

The hook talks to the island over a local named pipe only (no network). If the island is not running the hook exits instantly and Claude Code behaves as usual. The island reads only tool names, short file names, project names and session ids — never prompts, answers or command contents.

**After Effects**: copy `tools/ae/ostrov-render-notify.jsx` into `Support Files\Scripts\Startup` of your After Effects folder (needs admin rights once) and enable *Allow Scripts to Write Files and Access Network*.

**Phone tab**: pair your iPhone with Windows *Phone Link* (Bluetooth). Notifications from apps you turn off in Windows are invisible to Ostrov.

## Privacy and network

Nothing is collected and there is no telemetry. The app talks to the network only for the features you use:

| Feature | Service | What is sent |
| --- | --- | --- |
| Weather | Open-Meteo | City coordinates |
| Lyrics | LRCLIB | Track title, artist, album, duration |
| Track info, phone app icons | Apple iTunes Search API | Title / artist / album, or app name |
| Currency | open.er-api.com | Currency code |
| AI answers, dictation, summaries | OpenAI API | Your question or audio, with your own key, only when you use it |
| Optional tunnel | Your own server over SSH | Only OpenAI requests |

Local data (clipboard history, settings, day journal, reminders…) lives in `%AppData%\Ostrov`. The day journal stores only program names and durations, never window titles, and can be switched off and cleared in Settings.

## Known limitations

- Apple Music sound for the wave is taken from its helper process; a different music app will show cover and controls but not the wave.
- The glass uses the Windows acrylic backdrop in a separate window under the island, so the blur strength is set by Windows; Liquid-Glass-style refraction at the edges is not possible this way.
- Phone notifications and battery need the phone connected over Bluetooth through Phone Link.
- No tests for the UI; only the pure logic (calculator, parsers, QR encoder, ranking) is covered.
- Code comments are mostly in Russian.

## Trademarks

Ostrov is an independent hobby project. It is **not affiliated with or endorsed by** Anthropic, Apple, OpenAI, Microsoft, Adobe or any other company mentioned. Claude, Apple Music, iPhone, OpenAI, After Effects, VS Code and other names are trademarks of their owners and are used only to describe what the app integrates with. The project contains no third-party logos or assets; every icon is drawn in code or comes from fonts that ship with Windows.

## License

[MIT](LICENSE)
