# Wisland Architecture

This document is the implementation-facing map of the project. It is written for both human contributors and AI coding agents.

If you only need the quick entrypoint, start with `README.md`. If you need to change behavior, runtime flow, or module boundaries, read this file first.

## 1. Product Summary

Wisland is a Windows desktop recreation of the "Dynamic Island" interaction pattern:

- It renders as a compact always-on-top floating widget.
- It expands on hover and during notifications.
- It can be dragged and docked to the top edge of the screen.
- When docked and another app is maximized, it can collapse into a thin progress line.
- It integrates with Windows media sessions for track metadata, playback state, and progress.
- It can track multiple GSMTC sessions at once while exposing a single focused session to the shell.
- The expanded header uses a compact tab-strip metaphor with stacked source icons and a lightweight session picker overlay.
- It supports a custom task-progress override, tray controls, backdrop switching, persisted settings, and local logging.

## 2. Architectural Style

This is a pragmatic WinUI 3 desktop app, not a strict MVVM app.

The current architecture is intentionally split like this:

- `MainWindow` is the composition root and runtime orchestrator.
- `IslandController` is the pure state and animation target engine.
- `MediaService`, `SettingsService`, `ForegroundWindowMonitor`, `WindowAppearanceService`, and `ShellVisibilityService` are side-effect adapters.
- `Views/` and `Controls/` are lightweight presentation units.
- `Models/` now also contain semantic appearance tokens for theme-aware styling.
- `Helpers/` isolate Win32 and operational concerns.

That split matters. If you are changing code, keep logic placement consistent:

- Put pure state transitions and animation targets in `IslandController`.
- Put WinUI element synchronization in `MainWindow`.
- Put OS integration in `Helpers/`.
- Put media/session integration in `MediaService`.
- Put foreground-window polling in `ForegroundWindowMonitor`.
- Put backdrop and DWM appearance application in `WindowAppearanceService`.
- Put docked line-overlay ownership in `ShellVisibilityService`.
- Put persistence in `SettingsService`.

## 3. System Map

```text
App.xaml.cs
  -> creates MainWindow

MainWindow
  -> configures window behavior, tray, backdrop, timers, media hookup
  -> owns render loop and input events
  -> delegates state targeting to IslandController
  -> reacts to ForegroundWindowMonitor events
  -> listens for system theme and accent-color changes
  -> delegates backdrop/corner application to WindowAppearanceService
  -> delegates docked line-overlay ownership to ShellVisibilityService
  -> syncs logical state into XAML and AppWindow geometry
  -> owns focused-session selection, manual lock timing, auto-focus debounce, stable visual session ordering, and session cycling rules
  -> keeps the displayed source pinned during reconnect grace while the arbiter tracks the next auto winner

IslandController
  -> stores logical flags
  -> computes target width/height/Y/opacities
  -> advances current state with exponential decay

MediaService
  -> listens to Windows GSMTC session changes
  -> maps raw GSMTC sessions onto stable logical source keys
  -> keeps reconnecting sources alive in a short waiting state before pruning them
  -> exposes immutable media session snapshots and the system current logical source key
  -> runs short refresh bursts after session churn and transport skips to reduce late GSMTC updates

MediaSourceIconResolver
  -> resolves best-effort source app icons for the expanded header tab strip

SettingsService
  -> persists backdrop + position + dock state

ForegroundWindowMonitor
  -> polls the foreground window state when the island is docked
  -> raises maximized-state changes to the shell

WindowAppearanceService
  -> resolves theme-aware visual tokens from backdrop + system theme + accent
  -> applies shell surface, text/icon colors, progress palette, and DWM corner preferences

ShellVisibilityService
  -> owns NativeLineWindow
  -> shows and hides the docked line overlay
  -> translates logical shell line requests into DPI-aware Win32 overlay geometry
  -> applies palette-driven native line appearance without coupling line rendering to MainWindow

Views / Controls
  -> render compact content, expanded media content, liquid progress visuals,
  -> and reusable directional content transitions shared across island surfaces

Helpers
  -> logging, native line overlay window, Win32/DWM interop
```

## 4. Repository Layout

```text
wisland/
├── App.xaml / App.xaml.cs
│   Startup and unhandled exception logging.
├── MainWindow.xaml / MainWindow.*.cs
│   Top-level shell split into partial files for state, animation, interaction,
│   media, tray, appearance, and lifetime concerns.
├── Models/
│   ├── BackdropType.cs
│   ├── ContentTransitionDirection.cs
│   ├── DirectionalTransitionProfile.cs
│   ├── HoverMode.cs
│   ├── IslandConfig.cs
│   ├── MediaSessionSnapshot.cs
│   ├── IslandState.cs
│   ├── IslandThemeKind.cs
│   ├── IslandVisualTokens.cs
│   └── ProgressBarPalette.cs
├── Services/
│   ├── ForegroundWindowMonitor.cs
│   ├── IslandController.cs
│   ├── Media/
│   │   ├── MediaFocusArbiter.cs
│   │   ├── MediaService*.cs
│   │   └── MediaSourceNameFormatter.cs
│   ├── ShellVisibilityService.cs
│   ├── SettingsService.cs
│   └── WindowAppearanceService.cs
├── Views/
│   ├── CompactView.xaml(.cs)
│   └── ExpandedMediaView.xaml(.cs)
├── Controls/
│   ├── DirectionalContentTransitionCoordinator.cs
│   └── LiquidProgressBar.xaml(.cs)
├── Helpers/
│   ├── Logger.cs
│   ├── MediaSourceIconResolver.cs
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
3. Saved settings are loaded from `%LocalAppData%/Wisland/settings.json`.
4. The controller is initialized with starting position and docked state.
5. Backdrop is restored.
6. Media integration, timers, composition clipping, shell visibility services, and theme listeners are initialized.
7. `CompositionTarget.Rendering` becomes the per-frame update loop.

### 5.2 Input -> State -> Frame

The central loop is:

```text
Pointer / timer / media event
  -> MainWindow updates controller flags or service state
  -> MainWindow refreshes appearance when system theme or accent changes
  -> MainWindow calls UpdateState()
  -> IslandController recalculates targets
  -> per-frame UpdateAnimation() advances current state
  -> MainWindow syncs state into XAML + AppWindow and delegates line overlay visibility to ShellVisibilityService
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

the main island can be moved off-screen and represented by a `NativeLineWindow`, owned through `ShellVisibilityService`, that paints a 1px progress strip.

This is one of the most sensitive parts of the app because it depends on:

- DPI-aware physical pixel math
- monitor work area anchoring
- raw Win32 window painting
- hover re-entry timing

## 6. Feature Map

### Core user-visible features

- Compact island shell
- Expanded media panel
- Focused multi-session media shell
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
- shell visibility coordination
- render loop
- media UI synchronization
- focused-session selection, lock expiry, stable visual session ordering, and non-loop wheel cycling
- auto-focus debounce and reconnect waiting-state handling
- tray menu wiring
- backdrop application
- persistence save points

Important implication: `MainWindow` is still the shell boundary, but it is now split across partial files by runtime concern. Do not move pure state math here if it belongs in `IslandController`.

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

- requests the session manager and listens for manager/session changes
- maps raw GSMTC sessions onto stable logical sources instead of one-off raw session identities
- keeps reconnecting sources in `WaitingForReconnect` for a short grace window before pruning
- exposes immutable `MediaSessionSnapshot` values and `SystemCurrentSessionKey`
- provides transport controls against an explicit session key
- extrapolates progress locally between timeline updates for playing sessions
- runs a short refresh burst after transport skips and manager session churn
- raises track notifications only for the system current session

Failure mode is intentionally soft: errors are logged and the app keeps running.

### `SettingsService`

Persists only stable user preferences and placement data:

- backdrop type
- horizontal center position
- last Y position
- docked flag

It is not a general state snapshot system.

### `ForegroundWindowMonitor`

Owns the polling loop for foreground maximized-window detection.

This keeps shell state transitions separate from:

- Win32 foreground window lookup
- maximized-state inspection
- timer ownership

`MainWindow` now subscribes to state changes instead of owning the polling logic directly.

### `WindowAppearanceService`

Owns shell appearance application:

- backdrop selection
- semantic token resolution for light/dark + accent-aware styling
- text color application
- border background updates
- progress palette application
- DWM corner preference changes

This keeps `MainWindow` from directly coordinating WinUI backdrop objects, accent-aware color decisions, and native corner preferences.

### `ShellVisibilityService`

Owns the docked line overlay abstraction:

- lifetime of `NativeLineWindow`
- DPI-aware geometry for the 1px line overlay
- progress updates for the line surface
- theme-aware line palette updates
- line hide/show calls from the shell

This keeps `MainWindow` from directly managing the Win32 overlay window instance.

### `CompactView`

Minimal compact content surface. It now keeps its own two-slot text surface wired to the shared directional transition coordinator and exposes a lightweight session-count hint for multi-source media states.

### `ExpandedMediaView`

Expanded content surface for:

- compact tab-strip header with stacked source avatars
- stable stacked-avatar deck order with neutral focus/reorder animation for non-direction changes
- best-effort source app icons with monogram fallback
- chip-triggered session picker overlay request
- title
- artist
- direction-aware metadata transition animation for previous / next track changes
- previous / play-pause / next controls

It raises button/toggle events and leaves command behavior to the parent window. It no longer owns low-level composition choreography directly; instead it supplies header and metadata snapshots to shared directional transition coordinators.

### `SessionPickerWindow` + `SessionPickerOverlayView`

Dedicated secondary shell surface for the multi-session drop list.

- `MainWindow` owns its lifetime, visibility, placement, and dismissal rules
- `SessionPickerOverlayView` owns row layout, keyboard handling, async icon fill, and theme refresh
- `Helpers/SessionPickerPlacementResolver` keeps anchor-to-overlay placement pure and testable
- `Services/IslandController.IsTransientSurfaceOpen` keeps the island expanded while the list is open
- `ExpandedMediaView` only raises the chip toggle event and exposes the chip anchor bounds
- row projection is centralized in `SessionPickerRowProjector`, so playback state / subtitle / selected state stay out of the view layer
- the overlay list uses a compact `ListView` template with a reserved accessory slot, async source icon fill, and monogram fallback
- top and bottom scroll boundaries are softened with fade chrome instead of extra spacer rows
- runtime `SessionPickerOverlayLayoutMetrics` publish the realized panel height, while horizontal anchoring stays tied to the chip's geometric center
- `SessionPickerWindow` converts desired client bounds into an explicit outer `AppWindow` rect, because the secondary WinUI overlay window did not size reliably via `ResizeClient(...)`

### `DirectionalContentTransitionCoordinator`

Reusable WinUI composition coordinator for two-slot horizontal content transitions.

It owns:

- outgoing / incoming slot swapping
- z-order handoff
- offset, opacity, and scale timing
- directional clip choreography
- viewport clipping and center-point updates

This is the preferred place for future collapsed / compact directional content animations, rather than duplicating composition code inside each view.

### `LiquidProgressBar`

Custom visual component that gives the island its moving progress feel.

It layers:

- a frosted base
- shimmer flow
- a dynamic tail
- a bright leading edge core

Its update model is velocity-aware and includes dirty checks to reduce unnecessary layout work.

Its colors are now palette-driven rather than hardcoded, so theme changes do not require motion logic changes.

### `NativeLineWindow`

Raw Win32 overlay window used only for the fully tucked docked state. It now renders a small layered edge rail with per-pixel alpha, rather than a flat two-color `WM_PAINT` strip, and is owned by `ShellVisibilityService`.

### `WindowInterop`

Thin Win32/DWM interop wrapper used for:

- querying the foreground window
- checking whether that window is maximized
- switching corner rounding and shadow behavior

### `Logger`

Writes daily log files under `%LocalAppData%/Wisland/logs/`. Logging must never crash the app.

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

`MainWindow` now also owns an explicit `HoverMode` runtime state for shell orchestration. This is especially important for docked line mode, where pointer-driven hover and global cursor-tracked hover do not share the same event source.

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
4. Docked hidden mode is a two-window system: the main island window plus a `NativeLineWindow` managed by `ShellVisibilityService`. When the session picker is open, a temporary secondary WinUI window is also active.
5. Settings persistence should remain small, explicit, and tolerant of corruption.
6. Service failures should log and degrade gracefully rather than crash the process.
7. Transport controls and progress always follow the focused displayed session, while docked new-track notifications follow the system current session.
8. Local development uses the standard `bin/` and `obj/` output trees; do not add repo-local alternate build-output directories to dodge file locks.
9. Multi-session visual ordering should remain stable for the user; background priority changes may change focus, but should not constantly reshuffle the header strip.

## 10. Fast Change Guide

Use this table before editing:

| Goal | Primary file(s) | Notes |
| --- | --- | --- |
| Change compact / expanded sizing | `Models/IslandConfig.cs`, `Services/IslandController.cs` | Config holds constants, controller holds target logic. |
| Change animation feel | `Models/IslandConfig.cs`, `Services/IslandController.cs`, `Controls/LiquidProgressBar.xaml.cs` | Shell motion and progress motion are separate concerns. |
| Change docking behavior | `Services/IslandController.cs`, `Services/ForegroundWindowMonitor.cs`, `MainWindow.State.cs`, `MainWindow.Animation.cs` | Controller handles logical targets, monitor handles foreground polling, MainWindow handles physical anchoring. |
| Change hidden line mode | `MainWindow.State.cs`, `MainWindow.Animation.cs`, `Services/ShellVisibilityService.cs`, `Helpers/NativeLineWindow.cs`, `Services/WindowAppearanceService.cs` | Be careful with DPI, monitor math, and corner state transitions. |
| Change compact UI | `Views/CompactView.xaml`, `Views/CompactView.xaml.cs`, `Controls/DirectionalContentTransitionCoordinator.cs` | Compact content owns text/count-hint decisions; shared directional motion lives in the coordinator. |
| Change expanded media UI | `Views/ExpandedMediaView.xaml`, `Views/ExpandedMediaView.xaml.cs`, `Controls/DirectionalContentTransitionCoordinator.cs`, `Helpers/MediaSourceIconResolver.cs` | View owns header/metadata/control structure; shared directional motion lives in the coordinator. |
| Change tray actions | `MainWindow.Tray.cs` | `CreateTrayMenu()` is the entrypoint. |
| Add a persisted setting | `Services/SettingsService.cs`, `Models/BackdropType.cs`, `MainWindow.Lifetime.cs`, `MainWindow.Appearance.cs` | Keep persisted values typed at the shell boundary whenever possible. |
| Change media behavior | `Services/Media/*.cs`, `MainWindow.Media.cs`, `MainWindow.Notifications.cs` | Service owns GSMTC session discovery; window decides focused-session policy and UI response. |
| Change diagnostics | `Helpers/Logger.cs` | Logging is local-file based. |

## 11. Best Practices For Vibe Coding In This Repo

If you are working quickly with an AI assistant, use this order:

1. Read `README.md` for intent and navigation.
2. Read this file for boundaries and runtime flow.
3. Read the exact module you want to change.
4. Preserve the current placement of responsibilities before introducing a new pattern.
5. Prefer small, local edits over broad architectural rewrites.
6. Update docs when you change behavior, module ownership, or a contributor-facing invariant.
7. Prefer standard `dotnet build` outputs; if you only need a compile check while the app is running, use `dotnet msbuild /t:Compile` instead of creating ad-hoc output folders inside the repo.

The best "vibe coding" approach for this codebase is not "rewrite everything into patterns". It is:

- understand the runtime loop,
- keep pure logic and side effects separated,
- keep native window polling and native appearance application behind services,
- document real behavior instead of idealized architecture,
- leave obvious extension seams for later.

## 12. Recommended Next Refactors

These are documentation-backed refactor seams, not mandatory changes:

- Introduce a typed settings snapshot model if persisted preferences expand beyond a few fields.
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
