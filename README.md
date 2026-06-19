# Cotabby for Windows

Local AI autocomplete for Windows. A system-wide ghost-text suggestion engine that
runs entirely on-device via [llama.cpp](https://github.com/ggerganov/llama.cpp), with
no cloud calls and no telemetry.

This is a native Windows port of [FuJacob/cotabby](https://github.com/FuJacob/cotabby)
(macOS, Swift). The macOS app uses AppKit, SwiftUI, and the Accessibility API to read
the focused text field, render ghost text near the caret, and insert accepted
suggestions on `Tab`. This port replaces that stack with WPF, UI Automation (UIA),
`SetWindowsHookEx`, and `SendInput` to provide the same experience on Windows 10/11.

## What you get

- **System-wide ghost text** — works in any focused text field exposed via UI Automation
  (Notepad, VS Code, Slack, Discord, Outlook, browser inputs, …). Press `Tab` to accept.
- **Inline `:emoji:` autocomplete** — type `:smile`, `:tada`, `:+1` and a small popup
  shows matching glyphs; close the trigger with `:` to commit.
- **First-run welcome wizard** — five quick steps that capture name, languages,
  completion length preset, model choice, and a few defaults (skip to keep defaults).
- **Settings window with parity panes** — General, Engine & model, Writing, Shortcuts,
  Apps (per-app blocklist), Advanced (debounce, paths, open log/models), About.
- **Launch at login** — opt-in via the General pane; written to `HKCU\…\Run` so no
  admin is required.
- **Customizable ghost text** — color preset palette, opacity slider, optional `Tab`
  hint chip; automatic light/dark theme follow.
- **Configurable acceptance** — `Tab` is always bound; you can add an alternate key
  (e.g. backtick) when `Tab` clashes with the host app.
- **All inference local** — llama.cpp via LLamaSharp; no network calls except the
  one-time model download from Hugging Face. No telemetry.

## Stack

| Concern                    | macOS (Swift)                  | Windows (this port, C# / .NET 9)        |
|----------------------------|--------------------------------|------------------------------------------|
| UI                         | SwiftUI + AppKit               | WPF                                      |
| Focus / caret detection    | Accessibility (AX) API         | UI Automation (UIA)                      |
| Global keyboard hook       | `CGEventTap`                   | `SetWindowsHookEx` (WH_KEYBOARD_LL)      |
| Text insertion             | `CGEvent` + AX                 | `SendInput` + UIA `TextPattern`          |
| Inference                  | llama.cpp (Swift bindings)     | [LLamaSharp](https://github.com/SciSharp/LLamaSharp) |
| Apple Intelligence engine  | `FoundationModels`             | n/a — llama.cpp only                     |
| Menu bar                   | NSStatusItem                   | `H.NotifyIcon` system tray               |
| Overlay window             | NSPanel (borderless, topmost)  | WPF window (transparent, topmost, click-through) |

## Project layout

```
src/
  Cotabby.Core/        Pure logic: state machines, value types, contracts.
  Cotabby.Inference/   LLamaSharp runtime + suggestion engine.
  Cotabby.Win32/       UIA focus tracking, keyboard hook, caret geometry, SendInput.
  Cotabby.App/         WPF host: tray icon, settings, overlay window, DI composition.
```

`Cotabby.Core` and `Cotabby.Inference` target plain `net9.0` so the testable logic
stays decoupled from the Windows desktop runtime. `Cotabby.Win32` and `Cotabby.App`
target `net9.0-windows`.

## Requirements

- Windows 10 1809+ / Windows 11
- .NET 9 SDK
- x64 (ARM64 support is on the roadmap once LLamaSharp ARM64 native packages stabilize)

## Build

```powershell
dotnet restore
dotnet build -c Release
```

## Run

```powershell
dotnet run --project src\Cotabby.App
```

On first run, Cotabby will register a global keyboard hook and place an icon in the
system tray. There is no UAC elevation; UI Automation and `WH_KEYBOARD_LL` both work
under standard user rights, though some elevated processes (e.g. Task Manager) will
not surface AX trees.

The first launch also opens the **welcome wizard** to capture a couple of preferences
and let you pick a model — every step is skippable. You can re-run the wizard any time
from `Tray → Run welcome again…`.

## Configuration

- Settings file: `%APPDATA%\Cotabby\settings.json`
- Models cache:  `%LOCALAPPDATA%\Cotabby\models`
- Log file:      `C:\tmp\cotabby-live.log`

Open them from `Tray → Settings… → Advanced` (each has a one-click "Open" button).

## Parity status vs the macOS port

| Area                              | Windows port              |
|-----------------------------------|---------------------------|
| Ghost text overlay (color/opacity, hint chip, theme-aware) | ✅ |
| Settings window (General / Engine & model / Writing / Shortcuts / Apps / Advanced / About) | ✅ |
| First-run welcome wizard           | ✅                        |
| Inline `:emoji:` autocomplete      | ✅ (built-in catalog)     |
| Per-app blocklist                  | ✅                        |
| Launch at login                    | ✅ (HKCU Run key)         |
| Completion length presets          | ✅                        |
| Allow multi-line / single-line     | ✅                        |
| Alternate accept key (e.g. backtick) | ✅                      |
| Tab accept                         | ✅                        |
| Word-by-word vs whole acceptance   | Whole only on Windows     |
| Apple Intelligence engine          | n/a (llama.cpp only)      |
| Foundation Models routing          | n/a                       |
| Visual-context / screenshot OCR    | not yet                   |
| Custom rules editor                | not yet                   |

## License

GNU AGPL v3.0 — same as upstream Cotabby.
