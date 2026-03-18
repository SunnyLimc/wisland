# Island — Architecture & AI Planning Reference

> Windows 桌面端 macOS Dynamic Island 复刻。基于组件化架构，支持液态物理动画、媒体控制、拖拽停靠和系统托盘。

## Tech Stack

| Layer | Technology | Version |
|---|---|---|
| Framework | .NET 8 (`net8.0-windows10.0.19041.0`) | 8.0 |
| UI | WinUI 3 (WinAppSDK) | 1.8.250907003 |
| Window Extensions | WinUIEx | 2.9.0 |
| Deployment | Unpackaged + SelfContained | — |
| DPI | PerMonitorV2 DPI Aware | — |

## Project Structure

```
island/
├── Controls/
│   └── LiquidProgressBar.xaml   # "Liquid" physics progress bar component
├── Views/
│   ├── CompactView.xaml         # UI for the collapsed state
│   └── ExpandedMediaView.xaml   # UI for the expanded media controller
├── Models/
│   ├── IslandConfig.cs          # Centralized constants (magic numbers)
│   └── IslandState.cs           # Physical state data model (Width, Height, Opacity, etc.)
├── Services/
│   ├── IslandController.cs      # Core Logic: State machine + Physics (Exponential Decay)
│   ├── MediaService.cs          # GSMTC media session management
│   └── SettingsService.cs       # JSON settings persistence
├── Helpers/
│   ├── Logger.cs                # Structured file logger
│   ├── NativeLineWindow.cs      # Transparent overlay for "docked line" mode
│   └── WindowInterop.cs         # P/Invoke Win32 API wrappers
├── MainWindow.xaml / .cs        # Execution Layer: OS Window sync + Event routing
└── App.xaml / .cs               # Application entry point
```

## Architecture Overview

项目采用 **Controller-View-Component** 模式，实现逻辑与表现的彻底分离：

```
Input (Mouse/Media/System)
└── MainWindow.xaml.cs (Event Distributor)
    └── IslandController.cs (The "Brain")
        ├── State Machine (Hover/Dock/Notify/Maximized)
        └── Physics Engine (Exponential Decay Calculation)
            └── IslandState (Current Physics Values)
                └── MainWindow.xaml.cs (Sync Layer)
                    ├── LiquidProgressBar (Sub-component Physics)
                    ├── CompactView / ExpandedMediaView (UI Sync)
                    └── OS Window (MoveAndResize P/Invoke)
```

## Core Systems

### 1. IslandController (Logic & Physics)

负责所有的“思考”过程。不包含任何 UI 引用，纯数学驱动。

| Property | Meaning |
|---|---|
| `IsHovered` | Mouse is over the island |
| `IsDragging` | Island is being moved by user |
| `IsDocked` | Island is snapped to the screen top |
| `IsNotifying` | Active notification is overriding the state |
| `IsForegroundMaximized` | Focused window is maximized (triggers "line" mode) |

**Physics Engine**: 使用 `Tick(dt)` 驱动指数衰减（Exponential Decay）算法，实时更新 `IslandState`。

### 2. LiquidProgressBar (Component)

一个高度封装的 UI 组件，模拟液态流体质感。
- **Velocity Mapping**: 进度条头部的宽度和亮度与位移速度（Velocity）成正比。
- **Smoothed Feedback**: 对速度进行二次平滑处理，消除突发跳变导致的闪烁。
- **Shimmer Effect**: 持续的流光背景，提供“生命感”。

### 3. Media Integration (Service)

- 封装 `GlobalSystemMediaTransportControlsSessionManager`。
- 监听系统媒体状态，自动更新 `ExpandedMediaView`。
- 提供媒体控制接口（播放/暂停/切歌）。

### 4. Windowing Helper (Native)

- **NativeLineWindow**: 当灵动岛在全屏应用下隐藏时，在屏幕顶部边缘渲染的 1px 像素线，负责捕捉鼠标悬停以唤醒岛屿。
- **WindowInterop**: 处理 DWM 窗口圆角、阴影以及窗口置顶等低级 API 调用。

## Build & Run

```powershell
# Build
dotnet build island.csproj -c Debug -p:Platform=x64

# Run
dotnet run --project island.csproj -c Debug -p:Platform=x64
```

## AI & Developer Quick Reference

| Task | Key Location | Architecture Role |
|---|---|---|
| 修改动画速度 | `IslandConfig.AnimationSpeed` | Constant |
| 增加新的岛屿状态 | `IslandController.UpdateTargetState()` | Controller Logic |
| 修改进度条视觉 | `Controls/LiquidProgressBar.xaml` | Component View |
| 增加通知类型 | `MainWindow.ShowNotification()` | Action Entry |
| 修改媒体布局 | `Views/ExpandedMediaView.xaml` | View Content |
| 处理 Win32 消息 | `Helpers/WindowInterop.cs` | Native Helper |
| 调整物理参数 | `Services/IslandController.cs` | Physics Engine |

## Design Decisions

- **Exponential Decay over Storyboards**: 使用每帧计算而非 XAML Storyboard，以支持在动画过程中动态改变目标（Target Switching），实现无缝的物理过渡。
- **Component-Based UI**: 将 `Compact` 和 `Expanded` 拆分为独立 View，降低了 `MainWindow` 的 XAML 复杂度，支持未来通过插件化方式增加更多视图模式。
- **Smoothed Velocity**: 进度条反馈使用平滑后的速度，而非瞬时速度，以适配离散的（Step-based）进度更新。
