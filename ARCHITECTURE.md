# Island — Architecture & AI Planning Reference

> Windows 桌面端 macOS Dynamic Island 复刻，支持媒体控制、通知弹出、物理动画、拖拽停靠和系统托盘。

## Tech Stack

| Layer | Technology | Version |
|---|---|---|
| Framework | .NET 8 (`net8.0-windows10.0.19041.0`) | 8.0 |
| UI | WinUI 3 (WinAppSDK) | 1.8.250906003 |
| Window Extensions | WinUIEx | 2.9.0 |
| Deployment | Unpackaged + SelfContained | — |
| DPI | PerMonitorV2 DPI Aware | — |

## Project Structure

```
island/
├── island.slnx                  # Solution (XML slnx format)
├── island.csproj                # Project file
├── App.xaml / App.xaml.cs       # Application entry point + crash logging
├── MainWindow.xaml              # Single window XAML layout
├── MainWindow.xaml.cs           # Window logic (state, animation, drag, UI events)
├── Helpers/
│   └── Logger.cs                # Structured file logger → %LocalAppData%/Island/logs/
├── Models/
│   └── IslandConfig.cs          # Centralized constants (sizes, timings, thresholds)
├── Services/
│   ├── MediaService.cs          # GSMTC media session management + playback control
│   └── SettingsService.cs       # JSON settings persistence → %LocalAppData%/Island/
├── Package.appxmanifest         # App manifest (includes systemAI capability)
├── app.manifest                 # OS compatibility + DPI declaration
├── Assets/                      # 7 PNG icon assets
└── Properties/
    ├── launchSettings.json      # Debug profiles (Package / Unpackaged)
    └── PublishProfiles/
```

## Architecture Overview

```
App.xaml.cs (entry + Logger-based exception handler)
└── MainWindow.xaml.cs
    ├── State Machine        — UpdateState() with 4 boolean flags
    ├── Animation Loop       — CompositionTarget.Rendering + exponential decay
    ├── Drag & Dock System   — P/Invoke GetCursorPos, center-based positioning
    ├── Notification System  — ShowNotification() with async delay
    ├── Backdrop Manager     — Mica / Acrylic / None + auto-persist
    ├── Tray & Menu          — WinUIEx WindowManager
    ├── DWM Shadow/Corner    — DwmSetWindowAttribute P/Invoke
    ├── MediaService         — GSMTC session lifecycle, playback control
    └── SettingsService      — JSON load/save (backdrop, position, dock state)
```

## Core Systems

### State Machine (`UpdateState`)

| Flag | Meaning | Trigger |
|---|---|---|
| `_isNotifying` | Showing notification | `ShowNotification()` → async delay → reset |
| `_isHovered` | Mouse hovering | `PointerEntered/Exited` + debounce |
| `_isDragging` | Being dragged | `PointerPressed/Released` |
| `_isDocked` | Docked to screen top | Release when Y ≤ `DockThreshold` |

**Priority** (high → low): `_isNotifying` > `_isHovered` (+ !docked + !dragging) > default

### Animation System

Frame-synced exponential decay via `CompositionTarget.Rendering`:
```csharp
double t = 1.0 - Math.Exp(-IslandConfig.AnimationSpeed * dt);
_currentValue += (_targetValue - _currentValue) * t;
```

### MediaService

- Encapsulates `GlobalSystemMediaTransportControlsSessionManager`
- Exposes: `CurrentTitle`, `CurrentArtist`, `IsPlaying`, `HeaderStatus`
- Methods: `PlayPauseAsync()`, `SkipNextAsync()`, `SkipPreviousAsync()`
- Events: `MediaChanged`, `TrackChanged`

### SettingsService

- Persists to `%LocalAppData%/Island/settings.json`
- Properties: `BackdropType`, `CenterX`, `LastY`, `IsDocked`
- Auto-saves on backdrop change and drag release

### Window Configuration (WinUIEx)

| Property | Value | Effect |
|---|---|---|
| `IsTitleBarVisible` | false | No title bar |
| `IsAlwaysOnTop` | true | Always on top |
| `IsResizable/Minimizable/Maximizable` | false | Fixed widget |
| `IsShownInSwitchers` | false | Hidden from Alt+Tab |
| `IsVisibleInTray` | true | System tray icon |

## Constants Reference (`IslandConfig`)

| Constant | Value | Usage |
|---|---|---|
| `CompactWidth/Height` | 200 / 30 | Default pill size |
| `ExpandedWidth/Height` | 400 / 120 | Hover/notification size |
| `AnimationSpeed` | 25.0 | Exponential decay speed |
| `DockThreshold` | 15 | Y threshold to trigger dock |
| `DockPeekOffset` | 6 | Visible pixels when docked |
| `HoverDebounceMs` | 100 | Mouse exit debounce |
| `DefaultNotificationDurationMs` | 3000 | Standard notification |
| `TrackChangeNotificationDurationMs` | 4000 | Track change notification |

## Build & Run

```powershell
dotnet build island.csproj -c Debug -p:Platform=x64
dotnet run --project island.csproj -c Debug -p:Platform=x64
dotnet publish island.csproj -c Release -p:Platform=x64
```

## AI Quick Reference

| Task | Key Location | Notes |
|---|---|---|
| Modify UI layout | `MainWindow.xaml` | Border contains compact/expanded layers |
| Modify animations | `MainWindow.xaml.cs` #Animation region | Uses `IslandConfig.AnimationSpeed` |
| Add new state | `UpdateState()` in #State Machine region | Follow existing priority logic |
| Add menu item | `CreateTrayMenu()` + XAML ContextFlyout | Keep tray and context menu in sync |
| Window properties | Constructor | WinUIEx config + DWM shadow/corner tweaks |
| Media features | `Services/MediaService.cs` | GSMTC API, events fire on background thread |
| Notifications | `ShowNotification()` in #Notifications region | async void + `Task.Delay` timing |
| Backdrop | `SetBackdrop()` in #Backdrop region | Must sync text colors, auto-saves setting |
| Add constants | `Models/IslandConfig.cs` | All magic numbers centralized here |
| Logging | `Helpers/Logger.cs` | `Logger.Info/Warn/Error()`, writes to AppData |
| Settings | `Services/SettingsService.cs` | JSON to `%LocalAppData%/Island/settings.json` |
