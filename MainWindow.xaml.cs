using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using System;
using System.Runtime.InteropServices;
using Windows.Graphics;
using System.Threading.Tasks;
using System.Numerics;
using System.Threading;
using Windows.UI.ViewManagement;
using WinUIEx;
using island.Helpers;
using island.Models;
using island.Services;

namespace island
{
    /// <summary>
    /// Main window implementing a macOS-style Dynamic Island widget for Windows.
    /// Uses exponential-decay animation, drag-to-dock, and media session integration.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        private readonly WindowAppearanceService _appearanceService = new();
        private readonly MediaService _mediaService = new();
        private readonly SettingsService _settings = new();
        private readonly IslandController _controller = new();
        private readonly ForegroundWindowMonitor _foregroundWindowMonitor;
        private readonly UISettings _uiSettings = new();

        private double _dpiScale = 1.0;

        // --- Dragging Context ---
        private POINT _dragStartScreenPos;
        private double _dragPhysicalOffsetX;
        private double _dragPhysicalOffsetY;

        // --- Timers & Progress ---
        private readonly DispatcherTimer _hoverDebounceTimer;
        private TimeSpan _lastRenderTime = TimeSpan.Zero;
        private double? _taskProgress = null;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        // --- Context-Aware UX State ---
        private readonly DispatcherTimer _dockedHoverDelayTimer;

        // --- Line Mode State ---
        private readonly ShellVisibilityService _shellVisibilityService = new();
        private readonly DispatcherTimer _cursorTrackerTimer;
        private HoverMode _hoverMode = HoverMode.None;
        private int _lineHoverElapsedMs = 0;
        private int _lineExitElapsedMs = 0;
        private CancellationTokenSource? _notificationCts;
        private bool _isClosed;
        private BackdropType _currentBackdropType = BackdropType.Mica;

        // --- Content Clipping ---
        private readonly Microsoft.UI.Composition.RectangleClip _contentClip;
        private readonly Microsoft.UI.Composition.Visual _contentVisual;

        // --- OS Window Sync State ---
        private int _lastPhysX, _lastPhysY, _lastPhysW, _lastPhysH;
        private double _lastProgressBottomBleed = -1;
        private int _anchorPhysicalX;
        private int _anchorPhysicalY;
        private bool _hasAnchorPhysicalPoint;
        private bool _isRenderLoopActive;
        private double _lastRenderedIslandWidth = -1;
        private double _lastRenderedIslandHeight = -1;
        private double _lastRenderedCornerRadius = -1;
        private bool? _lastRenderedDockPeekState;
        private float _lastClipTop = float.NaN;
        private float _lastClipRight = float.NaN;
        private float _lastClipBottom = float.NaN;

        public MainWindow()
        {
            try
            {
                this.InitializeComponent();
                this.ExtendsContentIntoTitleBar = true;
                RootGrid.ActualThemeChanged += RootGrid_ActualThemeChanged;
                _uiSettings.ColorValuesChanged += UiSettings_ColorValuesChanged;
                _foregroundWindowMonitor = new ForegroundWindowMonitor(
                    () => WinRT.Interop.WindowNative.GetWindowHandle(this),
                    TimeSpan.FromMilliseconds(IslandConfig.ForegroundCheckIntervalMs));
                _foregroundWindowMonitor.ForegroundMaximizedChanged += OnForegroundMaximizedChanged;

                _contentVisual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(ContentContainer);
                _contentClip = _contentVisual.Compositor.CreateRectangleClip();
                _contentVisual.Clip = _contentClip;

                var manager = WindowManager.Get(this);
                manager.IsTitleBarVisible = false;
                manager.IsAlwaysOnTop = true;
                manager.IsResizable = false;
                manager.IsMinimizable = false;
                manager.IsMaximizable = false;
                this.AppWindow.IsShownInSwitchers = false;

                // Load saved settings
                _settings.Load();

                InitializeDisplayAnchorFromSettings();

                manager.IsVisibleInTray = true;
                manager.TrayIconContextMenu += (s, e) => e.Flyout = CreateTrayMenu();

                // Backdrop (from saved settings)
                SetBackdrop(_settings.BackdropType, persist: false);
                _ = InitializeMediaAsync();

                _hoverDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(IslandConfig.HoverDebounceMs) };
                _hoverDebounceTimer.Tick += HoverDebounceTimer_Tick;

                // Hover delay timer
                _dockedHoverDelayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(IslandConfig.DockedHoverDelayMs) };
                _dockedHoverDelayTimer.Tick += DockedHoverDelayTimer_Tick;

                _cursorTrackerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(IslandConfig.CursorTrackerIntervalMs) };
                _cursorTrackerTimer.Tick += CursorTrackerTimer_Tick;
                _foregroundWindowMonitor.SetActive(_controller.IsDocked);

                StartRenderLoop();
                this.Closed += OnWindowClosed;
                UpdateState();

                Logger.Info("MainWindow initialized successfully");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Fatal error during MainWindow initialization");
                throw;
            }
        }

        private double GetDisplayedProgress() => _taskProgress ?? _mediaService.Progress;
    }
}
