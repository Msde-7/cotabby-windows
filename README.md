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
  (Notepad, VS Code, Slack, Discord, Outlook, browser inputs, …).
- **Word-by-word acceptance** — press `Tab` to take the next word (or whole phrase, in
  phrase mode) and keep pressing to take more, exactly like the macOS app; press
  `` ` `` (backtick) to take the entire suggestion at once.
- **The same models as upstream** — the four `tabby-2-*` *base* checkpoints
  (Qwen3.5 0.8B/2B Base, Gemma 4 E2B/E4B from mradermacher). Base models continue
  your text instead of chatting at it, which is what makes the suggestions read
  like *your* next words.
- **The upstream prompt pipeline** — persona ("Written by <you>."), language
  conditioning, budgeted context sections, greedy constrained decode with a
  no-repeat-3-gram guard, sentence-boundary stops, and a one-shot normalizer
  (prompt-echo strip, trailing-duplication filter, insertion safety gate).
- **GPU inference** — llama.cpp's Vulkan backend offloads every layer to your GPU
  (NVIDIA/AMD/Intel); falls back to CPU automatically. A persistent context keeps
  time-to-suggestion in the low hundreds of milliseconds.
- **Inline `:emoji:` autocomplete** — type `:smile`, `:tada`, `:+1` and a small popup
  shows matching glyphs; close the trigger with `:` to commit.
- **First-run welcome wizard** — mirrors the macOS onboarding: template cards
  (Quick / Everyday / Powerful), profile, keybinds, model download.
- **Settings window with parity panes** — sidebar navigation like the macOS app:
  General, Engine & Model, Writing, Shortcuts, Apps, Advanced, About.
- **Launch at login** — opt-in via the General pane; written to `HKCU\…\Run` so no
  admin is required.
- **Customizable ghost text** — the upstream color preset palette, opacity slider,
  optional `Tab` hint chip; automatic light/dark theme follow.
- **All inference local** — llama.cpp via LLamaSharp; no network calls except the
  one-time model download from Hugging Face. No telemetry.

## Models

| Model          | File                             | Size    | Notes                        |
| -------------- | -------------------------------- | ------- | ---------------------------- |
| `tabby-2-nano` | `Qwen3.5-0.8B-Base.i1-Q6_K.gguf` | ~0.6 GB | fastest, low-memory          |
| `tabby-2-mini` | `Qwen3.5-2B-Base.i1-Q4_K_M.gguf` | ~1.3 GB | recommended default          |
| `tabby-2-base` | `gemma-4-E2B.i1-Q6_K.gguf`       | ~3.9 GB | higher quality               |
| `tabby-2-pro`  | `gemma-4-E4B.i1-Q4_K_M.gguf`     | ~5.3 GB | best quality, needs 16 GB+   |

Same Hugging Face artifacts as the macOS app, so the two ports can share a cache.
Qwen3.5 models also support fill-in-middle (used when the caret is mid-line).

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
| Base-model catalog (`tabby-2-nano/mini/base/pro`) | ✅ same artifacts |
| Base-completion prompt pipeline (persona / language / budgeted sections) | ✅ |
| Constrained greedy decode (top-20 argmax, no-repeat-3-gram, sentence stops) | ✅ |
| One-shot output normalizer (echo strip, dup filter, safety gate) | ✅ |
| Fill-in-middle for mid-line carets | ✅ (Qwen3.5 models)       |
| Word-by-word / phrase acceptance   | ✅ (`Tab`, mode in Shortcuts) |
| Accept-whole key                   | ✅ (`` ` `` backtick default) |
| Ghost text overlay (color/opacity, hint chip, theme-aware) | ✅ |
| Settings window (sidebar: General / Engine & Model / Writing / Shortcuts / Apps / Advanced / About) | ✅ |
| First-run welcome wizard (template cards) | ✅                 |
| Inline `:emoji:` autocomplete      | ✅ (built-in catalog)     |
| Per-app blocklist                  | ✅                        |
| Launch at login                    | ✅ (HKCU Run key)         |
| Completion length presets (2–4 / 4–7 / 7–12 / 12–20 words) | ✅ |
| GPU offload                        | ✅ Vulkan (vs Metal)      |
| Apple Intelligence engine          | n/a (llama.cpp only)      |
| Visual-context / screenshot OCR    | not yet                   |
| Custom rules editor                | not yet                   |

## License

GNU AGPL v3.0 — same as upstream Cotabby.
