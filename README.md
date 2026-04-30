# Wisland

A WinUI 3 desktop widget that recreates the Dynamic Island interaction pattern on Windows.

## Features

- **Compact floating shell** — always-on-top, rounded island widget with frame-driven motion
- **Hover expansion** — expands into a rich media panel on hover with exponential-decay animation
- **Immersive media panel** — full-bleed album-art view with extracted palette and its own smooth media progress row
- **Multi-session media** — integrates with Windows GSMTC for track metadata, playback controls, and progress across multiple apps simultaneously
- **Session management** — focused-session model with manual lock, stable visual tab ordering, wheel cycling, and an independent session picker overlay
- **Unified presentation machine** — a single event-driven state machine (`MediaPresentationMachine`) owns focus arbitration, manual-lock, skip/natural-ending stabilization, AI override, and notification overlay — emits `MediaPresentationFrame` as the single source of truth for media UI
- **Direction-aware transitions** — Composition API–driven content animations for track switching and header changes
- **Drag and dock** — drag-to-reposition anywhere, snap-to-dock at the top edge
- **Docked line mode** — collapses into a native Win32 1px progress strip when docked over maximized apps
- **AI song metadata** — optional clean metadata resolution via Google GenAI or OpenAI-compatible models, with structured output, localized prompts, and alternative candidates
- **Theming** — Mica, Acrylic, or None backdrop with system theme and accent-color awareness
- **Multi-language UI** — English, Japanese, Chinese Simplified with runtime language switching
- **System tray** — tray icon with show/settings/exit actions
- **Local diagnostics** — rolling daily log files with configurable verbosity

## Tech Stack

| Component | Version |
| --- | --- |
| .NET | 10 |
| WinUI 3 / Windows App SDK | 1.8 |
| WinUIEx | 2.9 |
| Google.GenAI | 1.6 |
| OpenAI | 2.10 |
| Target | `net10.0-windows10.0.19041.0` (min `10.0.17763.0`) |

The app ships as an **unpackaged** self-contained executable (`WindowsPackageType=None`).

## Quick Start

```powershell
# restore, build, and test
dotnet build wisland.slnx -c Debug -p:Platform=x64
dotnet test wisland.Tests\wisland.Tests.csproj -c Debug

# run
dotnet run --project wisland.csproj -c Debug -p:Platform=x64 -p:SelfContained=false -p:UseAppHost=false
```

If the DLL is locked by a running instance, use a compile-only check:

```powershell
dotnet msbuild wisland.csproj /t:Compile /p:Configuration=Debug /p:Platform=x64 /p:SelfContained=false /p:UseAppHost=false
```

## Repository Layout

```text
App.xaml(.cs)                 Startup, global exception handler, language bootstrap
MainWindow.xaml               Shell XAML root
MainWindow.*.cs               Shell orchestration split into 10 partial files by concern
SessionPickerWindow.cs        Borderless secondary window for the session picker overlay
SettingsWindow.cs             Settings window with NavigationView and fused title bar

Models/                       Constants (IslandConfig), render state (IslandState),
                              visual tokens, media snapshots, AI types, palettes

Services/
├── IslandController.cs       Pure state engine — targets + exponential-decay animation (no WinUI deps)
├── Media/                    GSMTC session discovery, source tracking, focus arbitration,
│   │                         reconnect grace, burst refresh, row projection, stabilization
│   ├── MediaService.*.cs     Raw GSMTC layer (cs / State / SourceTracking / Refresh / Stabilization / InternalTypes)
│   ├── MediaFocusArbiter.cs  Winner selection across multiple sessions (debounced)
│   ├── StabilizationReleaseGuards.cs  Fresh-track shape + position-restart guard (see docs/)
│   ├── SessionPickerRowProjector.cs   Pure projection from snapshots → picker rows
│   └── Presentation/         The unified media presentation state machine (P2+ refactor)
│       ├── MediaPresentationMachine.cs       Event-driven core, emits MediaPresentationFrame
│       ├── MediaPresentationMachineContext.cs
│       ├── MediaPresentationState.cs
│       ├── PresentationEvent.cs              Input event ADT
│       ├── SwitchIntent.cs                   Direction + deadline for user-initiated switches
│       ├── IPresentationPolicy.cs / IAiOverrideResolver.cs
│       └── Policies/         FocusArbitration, ManualSelectionLock, Stabilization,
│                              NotificationOverlay, AiOverride
├── AiSongResolverService.cs  Dual-path AI resolver (Google GenAI native + OpenAI-compatible)
├── AiSongPromptBuilder.cs    Locale-aware prompt templates (zh-Hans, zh-Hant, ja, en)
├── SettingsService.cs        JSON persistence to %LocalAppData%/Wisland/ with DPAPI key encryption
├── ForegroundWindowMonitor   Foreground-window polling for docked auto-hide
├── WindowAppearanceService   Theme tokens, backdrop lifecycle, DWM corner control
└── ShellVisibilityService    NativeLineWindow facade for docked line overlay

Views/
├── CompactView               Single-line compact content with directional text transitions
├── ExpandedMediaView         Header avatar strip, metadata transitions, playback controls
├── ImmersiveMediaView        Full-bleed album-art panel with palette extraction + header chip
├── SessionPickerOverlayView  Scrollable session list with composition-driven reveal
└── Settings/                 Appearance, AI Models, AI Song Override, Diagnostics pages

Controls/
├── DirectionalContentTransitionCoordinator   Reusable Composition two-slot content switcher
└── LiquidProgressBar                         Layered velocity-aware progress visualization

Helpers/
├── Logger.cs                 Rolling daily logs, 7-day retention, caller-context tagging
├── Loc.cs                    MRT Core localization wrapper with unpackaged fallback
├── NativeLineWindow.cs       Raw Win32 layered window for the docked progress strip
├── WindowInterop.cs          P/Invoke layer — multi-monitor, DPI, window state queries
├── SafePaths.cs              Path-traversal guard for all local file I/O
├── MediaSourceAppResolver    Three-tier app name resolution (shortcuts → AppsFolder → AppInfo)
├── MediaSourceIconResolver   Async icon resolver with dedup in-flight cache
├── CompactSurfaceLayout      Pure layout math for compact state decisions
├── SessionPickerPlacementResolver  Pure overlay placement calculator
└── SessionPickerOverlayLayout      Pure row/viewport metric calculator

Strings/{en-US,ja,zh-Hans}/Resources.resw   Localized string resources
Tools/                        Excluded-from-build diagnostic utilities
wisland.Tests/                xUnit tests via source-file linking (no project reference)
```

## CI

| Workflow | Trigger | Purpose |
| --- | --- | --- |
| **CodeQL** | push / PR to `main` | Security analysis with `security-extended` query pack |
| **Release** | push to `main`, `v*` tags, manual | Multi-platform publish (x64, x86, ARM64) |

## Docs

| File | Purpose |
| --- | --- |
| **README.md** | Project entrypoint — what it is, how to build, where things live |
| **ARCHITECTURE.md** | Implementation guide — runtime flow, module responsibilities, state model, invariants, change guide |
| **docs/MediaPresentationMachine.design.md** | Design of the unified media presentation state machine — legacy chain inventory, conflict matrix, state set, event set, transitions, invariants, rollout log |

Read `ARCHITECTURE.md` before editing behavior across multiple modules.
When touching anything under `Services/Media/`, also read
`docs/MediaPresentationMachine.design.md` — it documents the invariants
the frame pipeline relies on (e.g. "fingerprint change ⇒ exactly one
non-None transition", rapid-skip stabilization rules).
