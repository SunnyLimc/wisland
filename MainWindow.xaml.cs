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
using wisland.Services.Media;
using Microsoft.UI.Input;

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
        private MediaVisualCache? _visualCache;
        private readonly SettingsService _settings = new();
        private readonly IslandController _controller = new();
        private readonly ForegroundWindowMonitor _foregroundWindowMonitor;
        private readonly UISettings _uiSettings = new();
        private AiSongResolverService _aiSongResolver;
        private SettingsWindow? _settingsWindow;

        private double _dpiScale = 1.0;

        // --- Dragging Context ---
        private POINT _dragStartScreenPos;
        private double _dragPhysicalOffsetX;
        private double _dragPhysicalOffsetY;

        // --- Touch Input State ---
        private PointerDeviceType _lastPointerDeviceType;
        private POINT _lastPointerScreenPos;
        private ulong _touchDownTimestamp;
        private Windows.Foundation.Point _touchDownPosition;
        private bool _isTouchTapCandidate;
        private bool _isTouchSwiping;
        private double _swipeCumulativeX;
        private bool _touchExpandedLatch;
        private readonly DispatcherTimer _touchLongPressTimer;
        private readonly DispatcherTimer _touchAutoCollapseTimer;
        // Touch drag: use immediate window moves to avoid render-loop feedback jitter
        private bool _isTouchDragging;

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
        private readonly DispatcherTimer _metadataSettleTimer;

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
        private IslandVisualTokens? _currentVisualTokens;

        // --- Content Clipping ---
        private readonly Microsoft.UI.Composition.RectangleClip _contentClip;
        private readonly Microsoft.UI.Composition.Visual _contentVisual;

        /// <summary>Whether the immersive media view is currently the active expanded view.</summary>
        private bool IsImmersiveActive => _settings.UseImmersiveMediaView;

        // --- OS Window Sync State ---
        private int _lastPhysX, _lastPhysY, _lastPhysW, _lastPhysH;
        private int _anchorPhysicalX;
        private int _anchorPhysicalY;
        private bool _hasAnchorPhysicalPoint;
        private bool _hasInitializedWindowBounds;
        private bool _isRenderLoopActive;
        private double _lastRenderedIslandWidth = -1;
        private double _lastRenderedIslandHeight = -1;
        private double _lastRenderedCornerRadius = -1;
        private bool? _lastRenderedDockPeekState;
        private float _lastClipTop = float.NaN;
        private float _lastClipRight = float.NaN;
        private float _lastClipBottom = float.NaN;
        private int _startupBoundsReconcileAttempts;
        private bool _hasCompletedStartupBoundsReconcile;

        public MainWindow()
        {
            try
            {
                this.InitializeComponent();
                this.ExtendsContentIntoTitleBar = true;
                this.Activated += MainWindow_Activated;
                RootGrid.ActualThemeChanged += RootGrid_ActualThemeChanged;
                RootGrid.Loaded += RootGrid_Loaded;
                RootGrid.SizeChanged += RootGrid_SizeChanged;
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
                if (_settings.LogLevel.HasValue)
                {
                    Logger.SetMinimumLevel(_settings.LogLevel.Value);
                }
                _aiSongResolver = new AiSongResolverService(_settings);

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
                _metadataSettleTimer = new DispatcherTimer();
                _metadataSettleTimer.Tick += MetadataSettleTimer_Tick;

                _touchLongPressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(IslandConfig.TouchLongPressMs) };
                _touchLongPressTimer.Tick += TouchLongPressTimer_Tick;
                _touchAutoCollapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(IslandConfig.TouchAutoCollapseMs) };
                _touchAutoCollapseTimer.Tick += TouchAutoCollapseTimer_Tick;

                _shellVisibilityService.LineTouchActivated += OnLineTouchActivated;
                _foregroundWindowMonitor.SetActive(_controller.IsDocked);
                InitializeSessionPickerOverlay();

                // Backdrop and media startup run after timer initialization because
                // initial media sync can hit selection/auto-focus timer paths.
                SetBackdrop(_settings.BackdropType, persist: false);
                UpdateState();
                ApplyInitialWindowState();
                InitializePresentationMachine();
                _ = InitializeMediaAsync();

                StartRenderLoop();
                this.Closed += OnWindowClosed;

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
            return GetDisplayedProgress(GetDisplayedMediaSessionSnapshot());
        }

        private bool IsTouch => _lastPointerDeviceType == PointerDeviceType.Touch;

        private POINT GetPointerScreenPosition(Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            var point = e.GetCurrentPoint(null).Position;
            var windowPos = this.AppWindow.Position;
            var frameInsets = GetMainWindowFrameInsets();
            return new POINT
            {
                X = windowPos.X + frameInsets.Left + (int)Math.Round(point.X * _dpiScale),
                Y = windowPos.Y + frameInsets.Top + (int)Math.Round(point.Y * _dpiScale)
            };
        }

        private POINT GetDragScreenPosition(Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (e.Pointer.PointerDeviceType != PointerDeviceType.Touch)
            {
                GetCursorPos(out var mousePos);
                return mousePos;
            }

            // Use _lastPhysX/_lastPhysY (our own tracked window position) instead of
            // AppWindow.Position which can be stale during rapid window moves.
            var local = e.GetCurrentPoint(null).Position;
            var frameInsets = GetMainWindowFrameInsets();

            double screenX = _lastPhysX + frameInsets.Left + local.X * _dpiScale;
            double screenY = _lastPhysY + frameInsets.Top + local.Y * _dpiScale;

            return new POINT
            {
                X = (int)Math.Round(screenX),
                Y = (int)Math.Round(screenY)
            };
        }

        /// <summary>
        /// During touch drag, move the window IMMEDIATELY from the pointer event handler
        /// instead of waiting for the render loop. This eliminates the one-frame delay
        /// that causes the feedback loop between local coordinates and window position.
        /// The render loop's ApplyWindowBounds will be a no-op since _lastPhysX/Y already
        /// matches the target bounds.
        /// </summary>
        private void ApplyTouchDragWindowMove(double targetPhysCenterX, double targetPhysCenterY, int physWidth)
        {
            int physX = (int)Math.Round(targetPhysCenterX - physWidth / 2.0);
            int physY = (int)Math.Round(targetPhysCenterY);

            if (physX != _lastPhysX || physY != _lastPhysY)
            {
                this.AppWindow.Move(new PointInt32(physX, physY));
                _lastPhysX = physX;
                _lastPhysY = physY;
            }
        }

        private void InitTouchDragTracking(Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _isTouchDragging = true;
        }

        private void RestartTouchAutoCollapseTimer()
        {
            _touchAutoCollapseTimer.Stop();
            if (_touchExpandedLatch)
            {
                _touchAutoCollapseTimer.Start();
            }
        }

        private void TouchAutoCollapseTimer_Tick(object? sender, object e)
        {
            _touchAutoCollapseTimer.Stop();
            if (_touchExpandedLatch)
            {
                _touchExpandedLatch = false;
                SetHoverMode(HoverMode.None);
            }
        }

        private void TouchLongPressTimer_Tick(object? sender, object e)
        {
            _touchLongPressTimer.Stop();
            if (_isTouchTapCandidate && !_controller.IsDragging)
            {
                _isTouchTapCandidate = false;
                IslandBorder.ContextFlyout?.ShowAt(RootGrid, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
                {
                    Position = _touchDownPosition
                });
            }
        }

        private void ToggleTouchExpansion()
        {
            if (_touchExpandedLatch)
            {
                _touchExpandedLatch = false;
                _touchAutoCollapseTimer.Stop();
                SetHoverMode(HoverMode.None);
            }
            else
            {
                _touchExpandedLatch = true;
                SetHoverMode(HoverMode.PointerActive);
                RestartTouchAutoCollapseTimer();
            }
        }

        private void OnLineTouchActivated()
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (_isClosed) return;
                _touchExpandedLatch = true;
                SetHoverMode(HoverMode.LineActive);
                UpdateShadowState();
                RestartTouchAutoCollapseTimer();
            });
        }

        private double GetDisplayedProgress(MediaSessionSnapshot? displayedSession)
        {
            return _taskProgress ?? (_isMediaProgressResetPending ? 0.0 : displayedSession?.Progress ?? 0.0);
        }

        private bool ShouldShowProgressEffect()
        {
            return ShouldShowProgressEffect(GetDisplayedMediaSessionSnapshot());
        }

        private bool ShouldShowProgressEffect(MediaSessionSnapshot? displayedSession)
        {
            bool hasDisplayedTimeline = displayedSession.HasValue && displayedSession.Value.HasTimeline;
            return _taskProgress.HasValue
                || _isMediaProgressResetPending
                || hasDisplayedTimeline
                || IslandProgressBar.IsEffectVisible;
        }

        private bool ShouldAnimateProgressShimmer()
        {
            return ShouldAnimateProgressShimmer(GetDisplayedMediaSessionSnapshot());
        }

        private bool ShouldAnimateProgressShimmer(MediaSessionSnapshot? displayedSession)
        {
            return _taskProgress.HasValue
                || (displayedSession.HasValue
                    && displayedSession.Value.HasTimeline
                    && displayedSession.Value.IsPlaying
                    && !displayedSession.Value.IsWaitingForReconnect
                    && !displayedSession.Value.MissingSinceUtc.HasValue
                    && !_isMediaProgressResetPending);
        }

        private void OpenSettingsWindow()
        {
            if (_settingsWindow != null)
            {
                _settingsWindow.Activate();
                return;
            }

            _settingsWindow = new SettingsWindow(
                _settings,
                _aiSongResolver,
                onBackdropChanged: type => SetBackdrop(type),
                onAiSettingsChanged: () => OnAiSettingsChanged(),
                onSetTaskProgress: p => SetTaskProgress(p),
                onClearTaskProgress: () => ClearTaskProgress(),
                onImmersiveMediaChanged: () => DispatcherQueue.TryEnqueue(SyncMediaUI));
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Activate();
        }
    }
}
