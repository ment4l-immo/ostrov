# Contributing / Как участвовать

Thanks for looking at Ostrov! It is a personal project, so the best contributions are small, focused and easy to review.
Спасибо, что заглянули в Остров! Это личный проект, поэтому лучше всего подходят небольшие, понятные изменения.

## Getting started

```powershell
git clone https://github.com/ment4l-immo/ostrov.git
cd ostrov
dotnet build src/Ostrov
dotnet test tests/Ostrov.Tests
```

Requires Windows 11 and the .NET 10 SDK. Run the island with `dotnet run --project src/Ostrov` (close a running copy first: only one instance can run).

The visual language can be tried without building anything: open `docs/island-demo.html` in a browser (sliders for glass, edge light, tints; every tab clickable).

## Guidelines

- **One thing per pull request.** A bug fix, a tab, a setting.
- **No secrets, no personal data.** Never commit API keys, tunnel keys, `settings.json`, logs or screenshots with private content. Keys are stored with DPAPI under `%AppData%\Ostrov` and must stay out of the repo.
- **Keep the look consistent.** Colours live in `src/Ostrov/Services/Hues.cs` (ink `#E9ECEF`, graphite `#212529`, midnight `#191970`, alert `#FB5607`). Orange `#FF9F0A` is reserved for the timer/stopwatch. No third-party logos or assets: icons are drawn in code or come from Windows fonts.
- **Mind the cost.** The island is drawn in a transparent (layered) window, so a redraw is expensive. Avoid permanent animations and per-frame work; stop timers and frame callbacks when something is not visible.
- **Pure logic gets a test** in `tests/Ostrov.Tests` (calculator, parsers, rankers…). UI is checked by eye; please describe how you checked it.
- Code comments are mostly Russian; English comments in new code are welcome.

## Ideas that need help

- Real Liquid-Glass-style edge refraction (needs custom rendering instead of the system blur).
- A game volume mixer reachable from fullscreen games.
- Checking and fixing the VPN tile for more clients than Amnezia and Happ.
- Download tracking for more launchers (Steam, Epic…).
- More languages in the UI (strings are currently Russian).
- A proper installer and code signing.

## Reporting bugs

Open an issue with Windows version, what you did and what happened. `%AppData%\Ostrov\crash.log` helps if the app crashed (check it for personal data before attaching).
