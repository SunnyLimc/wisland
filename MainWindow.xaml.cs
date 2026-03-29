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
using wisland.Helpers;
using wisland.Models;
using wisland.Services;

namespace wisland
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
        private readonly DispatcherTimer _selectionLockTimer;
        private readonly DispatcherTimer _autoFocusTimer;

        // --- Line Mode State ---
        private readonly ShellVisibilityService _shellVisibilityService = new();
        private readonly DispatcherTimer _cursorTrackerTimer;
        private HoverMode _hoverMode = HoverMode.None;
        private HoverMode _hoverModeBeforeContextFlyout = HoverMode.None;
        private int _lineHoverElapsedMs = 0;
        private int _lineExitElapsedMs = 0;
        private bool _isMediaProgressResetPending;
        private bool _hideMediaProgressWhenResetCompletes;
        private CancellationTokenSource? _notificationCts;
        private bool _isContextFlyoutOpen;
        private bool _isClosed;
        private BackdropType _currentBackdropType = BackdropType.Mica;

        // --- Content Clipping ---
        private readonly Microsoft.UI.Composition.RectangleClip _contentClip;
        private readonly Microsoft.UI.Composition.Visual _contentVisual;

        // --- OS Window Sync State ---
        private int _lastPhysX, _lastPhysY, _lastPhysW, _lastPhysH;
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

                _hoverDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(IslandConfig.HoverDebounceMs) };
                _hoverDebounceTimer.Tick += HoverDebounceTimer_Tick;

                // Hover delay timer
                _dockedHoverDelayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(IslandConfig.DockedHoverDelayMs) };
                _dockedHoverDelayTimer.Tick += DockedHoverDelayTimer_Tick;

                _cursorTrackerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(IslandConfig.CursorTrackerIntervalMs) };
                _cursorTrackerTimer.Tick += CursorTrackerTimer_Tick;
                _selectionLockTimer = new DispatcherTimer();
                _selectionLockTimer.Tick += SelectionLockTimer_Tick;
                _autoFocusTimer = new DispatcherTimer();
                _autoFocusTimer.Tick += AutoFocusTimer_Tick;
                _foregroundWindowMonitor.SetActive(_controller.IsDocked);
                InitializeSessionPickerOverlay();

                // Backdrop and media startup run after timer initialization because
                // initial media sync can hit selection/auto-focus timer paths.
                SetBackdrop(_settings.BackdropType, persist: false);
                _ = InitializeMediaAsync();

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

        private double GetDisplayedProgress()
        {
            MediaSessionSnapshot? displayedSession = GetDisplayedMediaSessionSnapshot();
            return _taskProgress ?? (_isMediaProgressResetPending ? 0.0 : displayedSession?.Progress ?? 0.0);
        }

        private bool ShouldShowProgressEffect()
        {
            MediaSessionSnapshot? displayedSession = GetDisplayedMediaSessionSnapshot();
            bool hasDisplayedTimeline = displayedSession.HasValue && displayedSession.Value.HasTimeline;
            return _taskProgress.HasValue
                || _isMediaProgressResetPending
                || hasDisplayedTimeline
                || IslandProgressBar.IsEffectVisible;
        }

        private bool ShouldAnimateProgressShimmer()
        {
            MediaSessionSnapshot? displayedSession = GetDisplayedMediaSessionSnapshot();
            return _taskProgress.HasValue
                || (displayedSession.HasValue
                    && displayedSession.Value.HasTimeline
                    && displayedSession.Value.IsPlaying
                    && !displayedSession.Value.IsWaitingForReconnect
                    && !displayedSession.Value.MissingSinceUtc.HasValue
                    && !_isMediaProgressResetPending);
        }
    }
}
