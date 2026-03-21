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
        private int _hoverTicks = 0;
        private CancellationTokenSource? _notificationCts;
        private bool _isClosed;
        private BackdropType _currentBackdropType = BackdropType.Mica;

        // --- Content Clipping ---
        private readonly Microsoft.UI.Composition.RectangleClip _contentClip;
        private readonly Microsoft.UI.Composition.Visual _contentVisual;

        // --- OS Window Sync State ---
        private int _lastPhysX, _lastPhysY, _lastPhysW, _lastPhysH;

        public MainWindow()
        {
            try
            {
                this.InitializeComponent();
                this.ExtendsContentIntoTitleBar = true;
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

                _dpiScale = this.GetDpiForWindow() / 96.0;

                // Load saved settings
                _settings.Load();

                // Initialize Controller Position
                double initialCenterX = _settings.CenterX > 0 ? _settings.CenterX : (DisplayArea.Primary.WorkArea.Width / _dpiScale) / 2.0;
                double initialY = _settings.IsDocked ? 0 : Math.Max(IslandConfig.DefaultY, _settings.LastY);
                _controller.InitializePosition(initialCenterX, initialY, _settings.IsDocked);

                // Tray Icon
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

                CompositionTarget.Rendering += OnCompositionTargetRendering;
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
