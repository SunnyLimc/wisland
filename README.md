# Wisland

Wisland is a WinUI 3 desktop widget that recreates the Dynamic Island interaction pattern on Windows.

It supports:

- a compact always-on-top shell
- hover-based expansion
- system media metadata and playback controls
- direction-aware track switch animation in the expanded media panel
- drag-to-reposition and top-edge docking
- a thin hidden progress line when docked over maximized apps
- tray actions, backdrop switching, saved settings, and local logging

## Tech Stack

- .NET 8
- WinUI 3 / Windows App SDK
- WinUIEx
- Windows GSMTC for media integration
- Win32 / DWM interop for advanced window behavior

## Run Locally

```powershell
dotnet build wisland.csproj -c Debug -p:Platform=x64
dotnet run --project wisland.csproj -c Debug -p:Platform=x64
```

## Documentation Map

- `README.md`
  Quick project entrypoint, setup, and doc navigation.
- `ARCHITECTURE.md`
  Source-of-truth for module boundaries, runtime flow, state model, and change guidance.

## Repository Shape

```text
App.xaml(.cs)            Application startup
MainWindow.xaml + MainWindow.*.cs
                         Shell composition and orchestration split by concern
Models/                  Shared constants, typed config values, and render state
Services/                Controller, media integration, settings, window monitoring, appearance
Views/                   Compact and expanded content views
Controls/                Custom liquid progress bar and reusable content transitions
Helpers/                 Logging and native window interop
```

## Documentation Strategy

The repo now follows a simple documentation model that works better for fast human + AI collaboration:

- keep `README.md` short and navigational
- keep `ARCHITECTURE.md` implementation-specific and current
- avoid aspirational docs that drift away from code
- update docs when behavior or ownership changes

If you are making code changes, read `ARCHITECTURE.md` before editing behavior across multiple modules.
