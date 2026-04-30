# Architecture

> Implementation guide for contributors and AI coding agents.
> Read this before editing behavior across multiple modules.

## 1. What Wisland Is

Wisland is a Windows desktop widget that recreates the Dynamic Island interaction pattern. It renders as a compact always-on-top floating shell that expands on hover, integrates with system media sessions, and can dock to the top edge of the screen — collapsing into a native 1px progress strip when another app is maximized.

It is a **pragmatic WinUI 3 desktop app**, not a strict MVVM app. There is no DI container, no event aggregator, and no command bus. Services are instantiated directly with `new` and held as fields on the composition root.

## 2. Architectural Layers

```text
┌─────────────────────────────────────────────────────────────────┐
│  MainWindow  (composition root + shell orchestrator)            │
│  10 partial files by concern: Animation, Appearance, Display-   │
│  Anchor, Interaction, Lifetime, Media, Notifications,           │
│  SessionPicker, State, Tray                                     │
├─────────────────────────────────────────────────────────────────┤
│  Views / Controls       │  Services                             │
│  CompactView            │  IslandController (pure logic)        │
│  ExpandedMediaView      │  MediaService (6 partials)            │
│  SessionPickerOverlay   │  AiSongResolverService                │
│  LiquidProgressBar      │  AiSongPromptBuilder                  │
│  DirectionalContent-    │  SettingsService                      │
│  TransitionCoordinator  │  ForegroundWindowMonitor              │
│                         │  WindowAppearanceService              │
│  Settings pages (4)     │  ShellVisibilityService               │
├─────────────────────────┴───────────────────────────────────────┤
│  Helpers                                                        │
│  Logger, Loc, SafePaths, WindowInterop, NativeLineWindow,       │
│  MediaSource{App,Icon}Resolver, CompactSurfaceLayout,           │
│  SessionPickerPlacementResolver, SessionPickerOverlayLayout     │
├─────────────────────────────────────────────────────────────────┤
│  Models                                                         │
│  IslandConfig (constants), IslandState (render state),          │
│  IslandVisualTokens, MediaSessionSnapshot, palettes, AI types   │
└─────────────────────────────────────────────────────────────────┘
```

**Placement rules** — where code belongs:

| Concern | Where it goes |
| --- | --- |
| Pure state transitions and animation targets | `IslandController` |
| WinUI element sync and `AppWindow` geometry | `MainWindow` |
| GSMTC session discovery and source tracking | `Services/Media/` |
| Focused-session selection and lock/cycling policy | `MainWindow.Media.cs` |
| Backdrop, tokens, DWM corners | `WindowAppearanceService` |
| Docked line overlay | `ShellVisibilityService` → `NativeLineWindow` |
| Foreground window polling | `ForegroundWindowMonitor` |
| AI metadata resolution | `AiSongResolverService` |
| AI prompt construction | `AiSongPromptBuilder` |
| Persistence | `SettingsService` |
| Win32/DWM interop | `WindowInterop` |
| Multi-monitor math, display positioning | `WindowInterop` + `MainWindow.DisplayAnchor.cs` |
| Path-safe local file I/O | `SafePaths` |

## 3. Startup Sequence

```text
App.OnLaunched()
  1. Logger.Info(...)
  2. new SettingsService() → .Load()        // bootstrap instance for language
  3. Loc.Initialize(settings.Language)       // MRT Core + PrimaryLanguageOverride
  4. new MainWindow() → .Activate()
```

`MainWindow` constructor:

```text
  5. Configure WindowManager (WinUIEx): hidden title bar, always-on-top,
     not resizable/minimizable/maximizable, hidden from Alt-Tab
  6. new SettingsService() → .Load()        // runtime instance (separate from App's)
  7. new AiSongResolverService(settings)
  8. Initialize display anchor from saved physical coordinates (multi-monitor aware)
  9. Create system tray icon
 10. Create 5 DispatcherTimers (hover debounce, dock hover delay,
     cursor tracker, foreground monitor callback, session lock expiry)
 11. Initialize IslandController with starting position
 12. Restore backdrop
 13. Start MediaService, ForegroundWindowMonitor, ShellVisibilityService
 14. Wire CompositionTarget.Rendering → per-frame update loop
 15. Multi-pass startup bounds reconciliation (up to 4 passes)
```

> **Note**: Two independent `SettingsService` instances exist at runtime — one in `App` (used only to bootstrap `Loc`) and one owned by `MainWindow` for all runtime use. They are not shared.

## 4. The Render Loop

All shell motion is **frame-driven**, not storyboard-driven. `CompositionTarget.Rendering` fires every frame and drives:

```text
Per frame:
  1. Compute delta time (capped at IslandConfig.MaxDeltaTime)
  2. Refresh DPI scale and work area (deduped logging)
  3. MediaService.Tick(dt)          — extrapolate media progress
  4. IslandController.Tick(dt)      — advance render state via exponential decay
  5. Update visual children:
     - LiquidProgressBar progress/palette
     - Border size and corner radius
     - Compact and expanded view opacity
     - Hit-test visibility
     - Composition clip bounds
  6. AppWindow.MoveAndResize(...)   — sync OS window rect
  7. Session picker overlay position interpolation (if open)
```

The universal interpolation method is **exponential decay**:

```
current += (target - current) × (1 - e^(-speed × dt))
```

where `speed` is `IslandConfig.AnimationSpeed` (25.0). This produces smooth, framerate-independent motion without springs or keyframes.

## 5. State Model

### Logical state

Boolean mode flags on `IslandController`:

| Flag | Drives |
| --- | --- |
| `IsHovered` | Expanded targets |
| `IsDragging` | Live position tracking |
| `IsDocked` | Top-edge auto-hide |
| `IsNotifying` | Temporary expansion |
| `IsForegroundMaximized` | Docked line mode |
| `IsHoverPending` | Debounced hover entry |
| `IsTransientSurfaceOpen` | Keep expanded while picker is open |

`MainWindow` owns an additional runtime `HoverMode` enum for shell orchestration — important because docked-line hover and island-surface hover use different event sources.

### Render state

`IslandState` is a 7-property mutable POCO interpolated each frame:

- `Width`, `Height`, `Y`, `CenterX`
- `CompactOpacity`, `ExpandedOpacity`
- `IsHitTestVisible`

**Logical state decides where the island wants to go. Render state decides what is on screen.**

## 6. Docked Line Mode

When the island is docked, not hovered, not notifying, not dragged, and the foreground app is maximized — the main island window moves off-screen and a `NativeLineWindow` (raw Win32 layered window) renders a 1–2px progress strip.

This is one of the most sensitive subsystems because it depends on:

- DPI-aware physical pixel math via `WindowInterop`
- Monitor work area anchoring
- Raw Win32 `CreateWindowEx` / GDI bitmap painting (bypasses WinUI entirely)
- Hover re-entry timing from cursor tracker polling
- `ShellVisibilityService` as the ownership facade

The line window uses a pixel buffer rendered into a GDI bitmap with per-pixel alpha, dirty-flag redraws, and palette-driven colors. It is completely decoupled from WinUI/XAML.

## 7. Media Integration

### MediaService (6 partial files)

```text
MediaService.cs                 Core: GSMTC manager, events, transport controls
MediaService.State.cs           Snapshot and progress extrapolation
MediaService.SourceTracking.cs  Stable logical source keys with reconnect grace
MediaService.Refresh.cs         Burst refresh after session churn or transport skips
MediaService.InternalTypes.cs   Internal structs (TrackedSource, etc.)
```

Key behaviors:

- Maps raw GSMTC sessions onto **stable logical source keys** so re-connecting apps don't shuffle the UI
- Keeps sources in `WaitingForReconnect` for a grace window before pruning
- Extrapolates progress locally between timeline updates for playing sessions
- Runs short refresh bursts after transport skips and manager session churn
- Thread-safe via `lock (_gate)` and `SemaphoreSlim`

### Focus arbitration

`MediaFocusArbiter` decides which session gets visual focus with debounce and grace windows. `MainWindow.Media.cs` tracks the "displayed session key" independently from the "selected session key" to support manual lock, auto-focus debounce, reconnect waiting-state pinning, and non-loop wheel cycling.

### Source resolution

- `MediaSourceAppResolver` — three-tier display name lookup: Start Menu shortcuts → AppsFolder COM enumeration → `AppInfo` for packaged apps. Results cached via `ConcurrentDictionary<string, Lazy<>>`.
- `MediaSourceIconResolver` — async icon resolver with deduplicated in-flight cache. Packaged logos → executable thumbnails → shell items. Failed resolutions are evicted to allow retries.

## 8. AI Song Resolution

Dual-path architecture for optional clean-metadata resolution:

| Path | SDK | Output format |
| --- | --- | --- |
| Google GenAI | `Google.GenAI` 1.6 | Structured output with `PropertyOrdering`, optional grounding + thinking |
| OpenAI-compatible | `OpenAI` 2.10 | Strict JSON schema |

Both paths use a 6-field schema: `title`, `title-alt`, `title-alt2`, `artist`, `artist-alt`, `artist-alt2`.

**`AiSongPromptBuilder`** constructs locale-aware prompts:

- Native-language templates for `zh-Hans`, `zh-Hant`, `ja` — full natural-language prompts, not just variable substitution
- Generic English template with language/market substitution
- Returns `(Message, TemplateName)` for logging which template was selected

**`AiSongResolverService`** manages:

- Per-profile config: temperature (0–2), thinking depth, Google grounding toggle
- In-memory LRU cache + file persistence (`ai-song-cache.json` via `SafePaths`)
- Multi-level logging: Info for key events, Debug for config/prompts, Trace for full API payloads
- `TestModelAsync` for settings UI validation

## 9. Visual System

### Animation

All motion uses the **Composition API** — not XAML Storyboards:

- `DirectionalContentTransitionCoordinator` — reusable two-slot content switcher with `InsetClip`, offset/opacity/scale timing, and `CubicBezierEasingFunction`. Used by both `CompactView` and `ExpandedMediaView`.
- `ExpandedMediaView` — header avatar strip with composition-level scale/offset animations for reordering, separate easing for hover/press/focus states.
- `SessionPickerOverlayView` — panel/list reveal via composition visuals, scroll fade via composition clips.
- `LiquidProgressBar` — layered frosted base, shimmer flow, dynamic tail, bright leading edge. Velocity-aware with dirty checks. Palette-driven colors.
- `ImmersiveMediaView` owns a separate palette-aware media progress row. It
  animates the fill by `Width`, not a `ScaleTransform`, so skip/re-anchor
  animations cannot leave a faint retained-animation head ahead of the real
  fill.

When immersive media is the expanded surface, `MainWindow.Animation` hides the
shell-level `LiquidProgressBar` for the full expansion lifetime (including
hover/requested expansion, not only after opacity is high). The shell bar is
still used for compact/docked/non-immersive surfaces; it must not be visible
behind immersive content.

### Theming

`WindowAppearanceService` resolves `IslandVisualTokens` from (backdrop type, theme kind, accent color) and applies them to all surfaces. It manages `MicaBackdrop` / `DesktopAcrylicBackdrop` lifecycle with change-guarding and controls DWM corner preferences.

### Window system

| Window | Type | Purpose |
| --- | --- | --- |
| `MainWindow` | WinUI 3 (WinUIEx) | Primary island shell |
| `SessionPickerWindow` | WinUI 3 (WinUIEx) | Borderless secondary overlay, show/hide lifecycle |
| `SettingsWindow` | WinUI 3 (WinUIEx) | NavigationView + Frame, fused title bar |
| `NativeLineWindow` | Raw Win32 | 1px docked progress strip (GDI + layered window) |

## 10. Persistence

`SettingsService` writes JSON to `%LocalAppData%/Wisland/settings.json` via `SafePaths` (path-traversal guard). All file I/O uses atomic writes (`.tmp` → rename).

Persisted data:

| Category | Fields |
| --- | --- |
| Appearance | Backdrop type |
| Position | CenterX, LastY, physical anchor X/Y, relative position, docked flag |
| AI models | Provider, endpoint, model ID, temperature, thinking depth, grounding toggle, API key (DPAPI-encrypted) |
| AI prompts | Language, market, native prompt toggle |
| Display | Language override, log level |
| Window | Settings window size |

`Load()` gracefully falls back to defaults on any exception. All numeric fields are sanitized with range clamping.

## 11. Internationalization

### Resource files

`Strings/{en-US,ja,zh-Hans}/Resources.resw` — all three must contain identical key sets.

### Key conventions

| Context | Format | Example |
| --- | --- | --- |
| XAML `x:Uid` | `ElementName.Property` | `DiagnosticsTitle.Text` |
| Code-behind `Loc.GetString()` | `Category/Key` | `Media/NoMedia` |

The dot/slash distinction matters: MRT Core treats dots as property-path separators, so dotted keys cannot be looked up via `GetValue()`.

### Language override

- **Packaged mode** (MSIX / Sparse Package): `PrimaryLanguageOverride` drives both `x:Uid` and `GetString()`.
- **Unpackaged mode** (`WindowsPackageType=None`, current default): `PrimaryLanguageOverride` throws `InvalidOperationException`. The app follows the OS display language and the Settings language selector is disabled with an `InfoBar` hint.

### Adding a new language

1. Create `Strings/{tag}/Resources.resw` with the same keys as existing files.
2. Add the language to the `LanguageSelector` in `Views/Settings/AppearancePage.xaml`.

## 12. Testing

The test project (`wisland.Tests/`) uses **source-file linking** instead of a project reference to avoid WinUI hosting dependencies:

```xml
<Compile Include="..\..\Helpers\CompactSurfaceLayout.cs" Link="Helpers\CompactSurfaceLayout.cs" />
```

This means: **new testable files must be manually linked** in `wisland.Tests.csproj`.

Currently tested modules:

- `IslandController` (transient surface)
- `MediaFocusArbiter`
- `MediaSourceAppResolver`
- `MediaSourceNameFormatter`
- `SessionPickerRowProjector`
- `SessionPickerOverlayLayout`
- `SessionPickerPlacementResolver`
- `SessionPickerOverlayDismissMotion`
- `CompactSurfaceLayout`

```powershell
dotnet test wisland.Tests\wisland.Tests.csproj -c Debug
```

## 13. CI/CD

### CodeQL (`codeql.yml`)

- Triggers on push/PR to `main`
- .NET 10, `security-extended` query pack
- Custom config excludes `cs/path-injection` and `cs/command-line-injection` (false positives — all paths derive from `SafePaths`, process targets are hardcoded)

### Release (`release.yml`)

- Triggers on push to `main`, `v*` tags, manual dispatch
- Matrix: x64, x86, ARM64
- `dotnet publish` with platform-specific publish profiles → zip → upload artifact

## 14. Security Boundaries

- **`SafePaths`** — all local file I/O (settings, cache, logs) goes through a path-traversal guard that validates results stay under `%LocalAppData%/Wisland/`
- **DPAPI** — AI API keys are encrypted at rest via `System.Security.Cryptography.ProtectedData`
- **No network inputs** — this is a local desktop app. The only outbound calls are optional AI API requests initiated by user configuration
- **CodeQL** runs on every push with `security-extended` queries

## 15. Invariants

Rules that contributors must preserve:

1. `IslandController` stays UI-framework-free. No `using Microsoft.UI.*`.
2. Motion is frame-driven, not storyboard-driven.
3. `MainWindow` is the only place that syncs shell state into WinUI elements and `AppWindow`.
4. Docked hidden mode is a multi-window system: main island + `NativeLineWindow` (via `ShellVisibilityService`). Session picker adds a temporary secondary WinUI window.
5. Settings persistence is small, explicit, and tolerant of corruption.
6. Service failures log and degrade gracefully — never crash the process.
7. Transport controls and progress follow the focused displayed session; docked new-track notifications follow the system current session.
8. Use standard `bin/`/`obj/` output trees. No ad-hoc build-output directories.
9. Multi-session visual ordering remains stable for the user.
10. All file paths go through `SafePaths`. No raw `Path.Combine` with user-influenced segments.
11. `PresentationKind.Switching` frames must never leak raw transient metadata,
    but they may carry display-safe stabilization fields such as `Progress=0`
    for `SkipTransition` so expanded views reset progress immediately.

## 16. Change Guide

| Goal | Primary file(s) | Notes |
| --- | --- | --- |
| Change compact/expanded sizing | `Models/IslandConfig.cs`, `Services/IslandController.cs` | Config holds constants, controller holds target logic |
| Change animation feel | `Models/IslandConfig.cs`, `Services/IslandController.cs`, `Controls/LiquidProgressBar.xaml.cs` | Shell motion and progress motion are separate |
| Change docking behavior | `IslandController`, `ForegroundWindowMonitor`, `MainWindow.State.cs`, `MainWindow.Animation.cs` | Controller targets, monitor polls, MainWindow anchors |
| Change hidden line mode | `MainWindow.State/Animation.cs`, `ShellVisibilityService`, `NativeLineWindow`, `WindowAppearanceService` | DPI, monitor math, corner state — tread carefully |
| Change compact UI | `Views/CompactView.*`, `DirectionalContentTransitionCoordinator` | View owns text; coordinator owns motion |
| Change expanded media UI | `Views/ExpandedMediaView.*`, `DirectionalContentTransitionCoordinator`, `MediaSourceIconResolver` | View owns header/metadata/controls; coordinator owns motion |
| Change tray actions | `MainWindow.Tray.cs` | `CreateTrayMenu()` entrypoint |
| Add a persisted setting | `SettingsService`, relevant model, `MainWindow.Lifetime.cs` | Keep values typed at the shell boundary |
| Change media behavior | `Services/Media/*.cs`, `MainWindow.Media.cs`, `MainWindow.Notifications.cs` | Service owns GSMTC; window owns focus policy |
| Change media presentation / transition rules | `Services/Media/Presentation/*.cs`, `docs/MediaPresentationMachine.design.md` | Single-threaded state machine emits `MediaPresentationFrame`; see design doc §4–§6 for Confirming, `SwitchIntent.Source`, and `AiOverrideLookup` |
| Change diagnostics | `Helpers/Logger.cs` | Local-file based, 7-day retention |
| Add/change localized string | `Strings/*/Resources.resw` + XAML or code-behind caller | Add key to **all three** `.resw` files |
| Add a new language | `Strings/{tag}/Resources.resw`, `Views/Settings/AppearancePage.xaml` | Copy, translate, add ComboBox entry |
| Change AI resolution | `AiSongResolverService`, `AiSongPromptBuilder` | Resolver owns API + cache; builder owns prompts |
| Add AI model setting | `AiModelProfile`, `SettingsService`, `Views/Settings/AiModelsPage.*` | Add property, DTO field, serialization, UI, then wire into resolver |
| Change AI prompt templates | `AiSongPromptBuilder` | Native templates use `{0}`–`{3}`; generic uses `{0}`–`{5}` |
| Change settings UI | `SettingsWindow.cs`, `Views/Settings/*` | Pages hosted in Frame via NavigationView |
| Change display anchor logic | `MainWindow.DisplayAnchor.cs`, `WindowInterop` | Physical anchor persistence, multi-monitor restore |
| Add a test | `wisland.Tests/` | Link source files in `.csproj`, write xUnit test |

## 17. Coding With AI In This Repo

1. Read **README.md** for intent and navigation.
2. Read **this file** for boundaries and runtime flow.
3. Read the **exact module** you want to change.
4. Preserve the current placement of responsibilities before introducing a new pattern.
5. Prefer small, local edits over broad architectural rewrites.
6. Update docs when you change behavior, module ownership, or an invariant.
7. Use `dotnet build wisland.slnx -c Debug -p:Platform=x64` for standard builds. Use `dotnet msbuild /t:Compile` if the DLL is locked.

The best approach for this codebase is not "rewrite into patterns" — it is:

- Understand the render loop
- Keep pure logic and side effects separated
- Keep native window concerns behind services
- Document real behavior, not idealized architecture
