# Cotabby for Windows

Local AI autocomplete for Windows. A system-wide ghost-text suggestion engine that
runs entirely on-device via [llama.cpp](https://github.com/ggerganov/llama.cpp), with
no cloud calls and no telemetry.

This is a native Windows port of [FuJacob/cotabby](https://github.com/FuJacob/cotabby)
(macOS, Swift). The macOS app uses AppKit, SwiftUI, and the Accessibility API to read
the focused text field, render ghost text near the caret, and insert accepted
suggestions on `Tab`. This port replaces that stack with WPF, UI Automation (UIA),
`SetWindowsHookEx`, and `SendInput` to provide the same experience on Windows 10/11.

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

## License

GNU AGPL v3.0 — same as upstream Cotabby.
