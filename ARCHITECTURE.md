# Island Architecture

This document is the implementation-facing map of the project. It is written for both human contributors and AI coding agents.

If you only need the quick entrypoint, start with `README.md`. If you need to change behavior, runtime flow, or module boundaries, read this file first.

## 1. Product Summary

Island is a Windows desktop recreation of the "Dynamic Island" interaction pattern:

- It renders as a compact always-on-top floating widget.
- It expands on hover and during notifications.
- It can be dragged and docked to the top edge of the screen.
- When docked and another app is maximized, it can collapse into a thin progress line.
- It integrates with Windows media sessions for track metadata, playback state, and progress.
- It supports a custom task-progress override, tray controls, backdrop switching, persisted settings, and local logging.

## 2. Architectural Style

This is a pragmatic WinUI 3 desktop app, not a strict MVVM app.

The current architecture is intentionally split like this:

- `MainWindow` is the composition root and runtime orchestrator.
- `IslandController` is the pure state and animation target engine.
- `MediaService` and `SettingsService` are side-effect adapters.
- `Views/` and `Controls/` are lightweight presentation units.
- `Helpers/` isolate Win32 and operational concerns.

That split matters. If you are changing code, keep logic placement consistent:

- Put pure state transitions and animation targets in `IslandController`.
- Put WinUI element synchronization in `MainWindow`.
- Put OS integration in `Helpers/`.
- Put media/session integration in `MediaService`.
- Put persistence in `SettingsService`.

## 3. System Map

```text
App.xaml.cs
  -> creates MainWindow

MainWindow
  -> configures window behavior, tray, backdrop, timers, media hookup
  -> owns render loop and input events
  -> delegates state targeting to IslandController
  -> syncs logical state into XAML and AppWindow geometry
  -> coordinates NativeLineWindow for docked/maximized mode

IslandController
  -> stores logical flags
  -> computes target width/height/Y/opacities
  -> advances current state with exponential decay

MediaService
  -> listens to Windows GSMTC session changes
  -> exposes track metadata, playback state, and progress

SettingsService
  -> persists backdrop + position + dock state

Views / Controls
  -> render compact content, expanded media content, and liquid progress visuals

Helpers
  -> logging, native line overlay window, Win32/DWM interop
```

## 4. Repository Layout

```text
island/
├── App.xaml / App.xaml.cs
│   Startup and unhandled exception logging.
├── MainWindow.xaml / MainWindow.xaml.cs
│   Top-level shell, event routing, render loop, OS window sync, tray menu.
├── Models/
│   ├── IslandConfig.cs
│   └── IslandState.cs
├── Services/
│   ├── IslandController.cs
│   ├── MediaService.cs
│   └── SettingsService.cs
├── Views/
│   ├── CompactView.xaml(.cs)
│   └── ExpandedMediaView.xaml(.cs)
├── Controls/
│   └── LiquidProgressBar.xaml(.cs)
├── Helpers/
│   ├── Logger.cs
│   ├── NativeLineWindow.cs
│   └── WindowInterop.cs
└── Assets/ + Properties/
    Packaging, publish profiles, and app assets.
```

## 5. Runtime Flow

### 5.1 Launch

1. `App.OnLaunched()` creates `MainWindow` and activates it.
2. `MainWindow` configures the WinUIEx window manager:
   - hidden title bar
   - always on top
   - not resizable/minimizable/maximizable
   - hidden from task switchers
   - visible in system tray
3. Saved settings are loaded from `%LocalAppData%/Island/settings.json`.
4. The controller is initialized with starting position and docked state.
5. Backdrop is restored.
6. Media integration, timers, composition clipping, and the line window are initialized.
7. `CompositionTarget.Rendering` becomes the per-frame update loop.

### 5.2 Input -> State -> Frame

The central loop is:

```text
Pointer / timer / media event
  -> MainWindow updates controller flags or service state
  -> MainWindow calls UpdateState()
  -> IslandController recalculates targets
  -> per-frame UpdateAnimation() advances current state
  -> MainWindow syncs state into XAML + AppWindow + NativeLineWindow
```

### 5.3 Per-Frame Animation

`MainWindow.UpdateAnimation()` runs on every render tick and does five jobs:

1. Normalize `dt` and refresh DPI scale.
2. Advance extrapolated media progress via `MediaService.Tick(dt)`.
3. Advance island physics via `IslandController.Tick(dt)`.
4. Update visual children:
   - `LiquidProgressBar`
   - border size and corner radius
   - compact and expanded view opacity
   - hit testing
   - composition clip
5. Sync the actual OS window rectangle with `AppWindow.MoveAndResize(...)`.

This project does not use XAML storyboards for shell motion. Motion is driven by explicit math each frame.

### 5.4 Docked / Maximized "Line Mode"

When all of these are true:

- the island is docked,
- the foreground app is maximized,
- the island is not hovered,
- the island is not notifying,
- the island is not being dragged,

the main island can be moved off-screen and represented by `NativeLineWindow`, a top-most transparent Win32 overlay that paints a 1px progress strip.

This is one of the most sensitive parts of the app because it depends on:

- DPI-aware physical pixel math
- monitor work area anchoring
- raw Win32 window painting
- hover re-entry timing

## 6. Feature Map

### Core user-visible features

- Compact island shell
- Expanded media panel
- Hover expansion
- Temporary notification expansion
- Drag and drop repositioning
- Top-edge docking
- Docked hidden line mode for maximized foreground apps
- Media playback controls
- Media timeline progress
- Custom task progress override
- Tray menu actions
- Backdrop switching between `Mica`, `Acrylic`, and `None`
- Persisted position and visual preference
- File logging for diagnostics

### Non-goals of the current implementation

- Full MVVM abstraction
- Cross-platform support
- Plugin system
- Formal command bus or event aggregator
- Test-heavy architecture

Those are possible future directions, but they are not how the current code is organized.

## 7. Module Responsibilities

### `MainWindow`

Owns orchestration and side effects:

- input event handlers
- drag lifecycle
- hover timers
- foreground maximized detection
- line-window coordination
- render loop
- media UI synchronization
- tray menu wiring
- backdrop application
- persistence save points

Important implication: `MainWindow` is large because it is the shell boundary. Do not move pure state math here if it belongs in `IslandController`.

### `IslandController`

Owns logical flags and animation targets:

- `IsHovered`
- `IsDragging`
- `IsDocked`
- `IsNotifying`
- `IsForegroundMaximized`
- `IsHoverPending`

It also owns the `Current` `IslandState` and computes target width, height, Y, and content opacities.

Key behavior:

- expanded targets when hovered or notifying
- compact targets when idle
- dock peek target when docked
- drag-time live dock release
- exponential decay state advancement

This class has no WinUI dependency and should stay that way.

### `MediaService`

Wraps Windows GSMTC:

- obtains the current media session
- listens for session, media, playback, and timeline changes
- exposes title, artist, header, play state, and progress
- provides `PlayPauseAsync`, `SkipNextAsync`, and `SkipPreviousAsync`
- extrapolates progress locally between timeline updates

Failure mode is intentionally soft: errors are logged and the app keeps running.

### `SettingsService`

Persists only stable user preferences and placement data:

- backdrop type
- horizontal center position
- last Y position
- docked flag

It is not a general state snapshot system.

### `CompactView`

Minimal compact content surface. Right now it only shows text and text color.

### `ExpandedMediaView`

Expanded content surface for:

- header text
- title
- artist
- previous / play-pause / next controls

It raises button events and leaves command behavior to the parent window.

### `LiquidProgressBar`

Custom visual component that gives the island its moving progress feel.

It layers:

- a frosted base
- shimmer flow
- a dynamic tail
- a bright leading edge core

Its update model is velocity-aware and includes dirty checks to reduce unnecessary layout work.

### `NativeLineWindow`

Raw Win32 overlay window used only for the fully tucked docked state. It paints progress directly via `WM_PAINT`.

### `WindowInterop`

Thin Win32/DWM interop wrapper used for:

- querying the foreground window
- checking whether that window is maximized
- switching corner rounding and shadow behavior

### `Logger`

Writes daily log files under `%LocalAppData%/Island/logs/`. Logging must never crash the app.

## 8. State Model

There are two levels of state:

### Logical state

Boolean mode flags on `IslandController`:

- hover
- drag
- dock
- notification
- foreground maximized
- hover pending

### Render state

Continuous values in `IslandState`:

- width
- height
- Y
- center X
- compact opacity
- expanded opacity
- hit-test visibility

This split is deliberate:

- logical state decides where the island wants to go
- render state decides what is currently on screen

## 9. Invariants

These are the rules that contributors should preserve:

1. `IslandController` stays UI-framework-free.
2. Motion is frame-driven, not storyboard-driven.
3. `MainWindow` is the only place that should directly sync shell state into WinUI elements and `AppWindow`.
4. Docked hidden mode is a two-window system: the main island window plus `NativeLineWindow`.
5. Settings persistence should remain small, explicit, and tolerant of corruption.
6. Service failures should log and degrade gracefully rather than crash the process.

## 10. Fast Change Guide

Use this table before editing:

| Goal | Primary file(s) | Notes |
| --- | --- | --- |
| Change compact / expanded sizing | `Models/IslandConfig.cs`, `Services/IslandController.cs` | Config holds constants, controller holds target logic. |
| Change animation feel | `Models/IslandConfig.cs`, `Services/IslandController.cs`, `Controls/LiquidProgressBar.xaml.cs` | Shell motion and progress motion are separate concerns. |
| Change docking behavior | `Services/IslandController.cs`, `MainWindow.xaml.cs` | Controller handles logical targets, MainWindow handles physical anchoring. |
| Change hidden line mode | `MainWindow.xaml.cs`, `Helpers/NativeLineWindow.cs` | Be careful with DPI and monitor math. |
| Change compact UI | `Views/CompactView.xaml`, `Views/CompactView.xaml.cs` | Keep the view lightweight. |
| Change expanded media UI | `Views/ExpandedMediaView.xaml`, `Views/ExpandedMediaView.xaml.cs` | Event surface lives here, command behavior lives in MainWindow. |
| Change tray actions | `MainWindow.xaml.cs` | `CreateTrayMenu()` is the entrypoint. |
| Add a persisted setting | `Services/SettingsService.cs`, `MainWindow.xaml.cs` | Load during startup, save at the relevant mutation point. |
| Change media behavior | `Services/MediaService.cs`, `MainWindow.xaml.cs` | Service owns GSMTC; window decides how UI responds. |
| Change diagnostics | `Helpers/Logger.cs` | Logging is local-file based. |

## 11. Best Practices For Vibe Coding In This Repo

If you are working quickly with an AI assistant, use this order:

1. Read `README.md` for intent and navigation.
2. Read this file for boundaries and runtime flow.
3. Read the exact module you want to change.
4. Preserve the current placement of responsibilities before introducing a new pattern.
5. Prefer small, local edits over broad architectural rewrites.
6. Update docs when you change behavior, module ownership, or a contributor-facing invariant.

The best "vibe coding" approach for this codebase is not "rewrite everything into patterns". It is:

- understand the runtime loop,
- keep pure logic and side effects separated,
- document real behavior instead of idealized architecture,
- leave obvious extension seams for later.

## 12. Recommended Next Refactors

These are documentation-backed refactor seams, not mandatory changes:

- Split `MainWindow.xaml.cs` into partial files by concern such as animation, drag, media, and tray.
- Extract a small `BackdropService` or `WindowAppearanceService` if backdrop logic grows.
- Extract a `ForegroundWindowMonitor` if maximized-window detection gets more complex.
- Introduce manual smoke-test notes for docking, DPI, and line-mode behavior if UI changes become frequent.

## 13. Documentation Contract

The project now uses a two-level documentation model:

- `README.md` is the onboarding and navigation entrypoint.
- `ARCHITECTURE.md` is the implementation and change guide.

Update `README.md` when:

- a user-visible feature changes
- setup or run instructions change
- the doc map changes

Update `ARCHITECTURE.md` when:

- module ownership changes
- runtime flow changes
- state/invariant rules change
- new extension seams or risks become important
