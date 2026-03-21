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
        private readonly MediaService _mediaService = new();
        private readonly SettingsService _settings = new();
        private readonly IslandController _controller = new();

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
        private readonly DispatcherTimer _foregroundCheckTimer;
        private readonly DispatcherTimer _dockedHoverDelayTimer;

        // --- Line Mode State ---
        private NativeLineWindow? _lineWindow;
        private readonly DispatcherTimer _cursorTrackerTimer;
        private int _hoverTicks = 0;

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
                SetBackdrop(ParseBackdropType(_settings.BackdropType));
                _ = InitializeMediaAsync();

                _hoverDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(IslandConfig.HoverDebounceMs) };
                _hoverDebounceTimer.Tick += (s, e) =>
                {
                    _hoverDebounceTimer.Stop();
                    _controller.IsHovered = false;
                    _controller.IsHoverPending = false; // Note: need to add this to controller if missing
                    UpdateState();
                };

                // Foreground window check loop
                _foregroundCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                _foregroundCheckTimer.Tick += (s, e) => CheckForegroundWindow();
                _foregroundCheckTimer.Start();

                // Hover delay timer
                _dockedHoverDelayTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(IslandConfig.DockedHoverDelayMs) };
                _dockedHoverDelayTimer.Tick += (s, e) =>
                {
                    _dockedHoverDelayTimer.Stop();
                    _controller.IsHoverPending = false;
                    _controller.IsHovered = true;
                    UpdateState();
                };

                _lineWindow = new NativeLineWindow();
                _cursorTrackerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                _cursorTrackerTimer.Tick += CursorTrackerTimer_Tick;

                CompositionTarget.Rendering += OnCompositionTargetRendering;

                this.Closed += (s, e) => _lineWindow?.Dispose();

                Logger.Info("MainWindow initialized successfully");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Fatal error during MainWindow initialization");
                throw;
            }
        }

        #region State Machine

        private void UpdateShadowState()
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            // We ONLY want to disable rounding/shadows when the window is fully tucked away and acting as a line.
            bool isCompletelyHiddenLine = _controller.IsOffscreen();
            
            int preference = isCompletelyHiddenLine ? WindowInterop.DWMWCP_DONOTROUND : WindowInterop.DWMWCP_ROUND;
            WindowInterop.DwmSetWindowAttribute(hwnd, WindowInterop.DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
        }

        private void CheckForegroundWindow()
        {
            if (!_controller.IsDocked) return;

            IntPtr hwnd = WindowInterop.GetForegroundWindow();
            if (hwnd == IntPtr.Zero || hwnd == WinRT.Interop.WindowNative.GetWindowHandle(this))
            {
                return;
            }

            bool isMaximized = WindowInterop.IsWindowMaximized(hwnd);
            if (isMaximized != _controller.IsForegroundMaximized)
            {
                _controller.IsForegroundMaximized = isMaximized;
                UpdateState();
            }
        }

        private void CursorTrackerTimer_Tick(object? sender, object e)
        {
            if (!_controller.IsDocked || !_controller.IsForegroundMaximized || _controller.IsHovered)
            {
                _cursorTrackerTimer.Stop();
                return;
            }

            GetCursorPos(out var pt);
            
            double xRadius = IslandConfig.CompactWidth / 2.0;
            if (pt.Y <= 1 && pt.X >= (_controller.Current.CenterX - xRadius) * _dpiScale && pt.X <= (_controller.Current.CenterX + xRadius) * _dpiScale)
            {
                _hoverTicks++;
                if (_hoverTicks * 50 >= IslandConfig.DockedHoverDelayMs)
                {
                    _hoverTicks = 0;
                    _cursorTrackerTimer.Stop();

                    // Soft-reset height to 1px so the physics engine can "grow" it from the line.
                    // We DON'T touch Current.Y here to avoid breaking the animation target logic.
                    _controller.Current.Height = 1;
                    
                    _controller.IsHovered = true;
                    UpdateState();
                    UpdateShadowState();
                }
            }
            else
            {
                _hoverTicks = 0;
            }
        }

        private void ShowLineWindow()
        {
            if (_lineWindow != null)
            {
                int w = (int)Math.Ceiling(IslandConfig.CompactWidth * _dpiScale);
                int h = (int)Math.Max(1, 1 * _dpiScale); // 1px line
                int x = (int)Math.Round((_controller.Current.CenterX - IslandConfig.CompactWidth / 2.0) * _dpiScale);
                int y = 0;

                double progress = _taskProgress ?? _mediaService.Progress;
                _lineWindow.SetProgress(progress);
                _lineWindow.Show(x, y, w, h);
            }
        }

        private void HideLineWindow()
        {
            _hoverTicks = 0;
            _cursorTrackerTimer.Stop();
            _lineWindow?.Hide();
        }

        private void UpdateState()
        {
            _controller.UpdateTargetState();

            // Handle side effects like showing/hiding native line
            if (_controller.IsOffscreen() || (_controller.IsDocked && _controller.IsForegroundMaximized && !_controller.IsHovered && !_controller.IsNotifying))
            {
                // This will be handled in UpdateAnimation OS sync
            }
            else
            {
                HideLineWindow();
            }
            
            UpdateShadowState();
        }

        #endregion

        #region Animation

        private void OnCompositionTargetRendering(object? sender, object e)
        {
            if (e is RenderingEventArgs args)
                UpdateAnimation(args.RenderingTime);
        }

        /// <summary>
        /// Per-frame animation using exponential decay interpolation.
        /// Syncs XAML elements and OS window position/size every frame.
        /// </summary>
        private void UpdateAnimation(TimeSpan renderingTime)
        {
            if (_lastRenderTime == TimeSpan.Zero) { _lastRenderTime = renderingTime; return; }
            double dt = (renderingTime - _lastRenderTime).TotalSeconds;
            _lastRenderTime = renderingTime;
            if (dt <= 0 || dt > IslandConfig.MaxDeltaTime) dt = IslandConfig.FallbackDeltaTime;

            // Update DPI scale for cross-monitor dragging support
            _dpiScale = this.GetDpiForWindow() / 96.0;

            // Update Media Extrapolation
            _mediaService.Tick(dt);

            // Physics/Animation Tick
            double t = 1.0 - Math.Exp(-IslandConfig.AnimationSpeed * dt);
            _controller.Tick(dt);

            var state = _controller.Current;

            // Update Progress Bar Component
            double targetProgress = _taskProgress ?? _mediaService.Progress;
            IslandProgressBar.Update(dt, t, targetProgress, state.Width, state.Height);

            // XAML Sync
            IslandBorder.Width = state.Width;
            IslandBorder.Height = state.Height;

            // Dynamic radius: half-height for compact, but capped at 20px for expanded state
            double radius = Math.Min(state.Height / 2.0, 20.0); 
            IslandBorder.CornerRadius = new CornerRadius(radius);

            // Update Composition Clip for ContentContainer (Layer 3: Composition-Level Clipping)
            if (_contentClip != null)
            {
                var vecRadius = new Vector2((float)radius);
                _contentClip.TopLeftRadius = vecRadius;
                _contentClip.TopRightRadius = vecRadius;
                _contentClip.BottomLeftRadius = vecRadius;
                _contentClip.BottomRightRadius = vecRadius;
                _contentClip.Right = (float)state.Width;
                _contentClip.Bottom = (float)state.Height;

                // Explicitly clip out the top part when docked and settled to prevent any text bleed
                if (_controller.IsDocked && !_controller.IsHovered && !_controller.IsNotifying && !_controller.IsDragging && state.Height <= IslandConfig.CompactHeight + 1)
                {
                    double peek = _controller.IsForegroundMaximized ? IslandConfig.MaximizedDockPeekOffset : IslandConfig.DockPeekOffset;
                    _contentClip.Top = (float)(state.Height - peek);
                }
                else
                {
                    _contentClip.Top = 0;
                }
            }

            if (CompactContent.Opacity != state.CompactOpacity) CompactContent.Opacity = state.CompactOpacity;
            if (ExpandedContent.Opacity != state.ExpandedOpacity) ExpandedContent.Opacity = state.ExpandedOpacity;
            
            // Mutually exclusive hit testing (Layer 4: Interactive Isolation)
            bool isExpandedActive = state.ExpandedOpacity > IslandConfig.HitTestOpacityThreshold;
            if (ExpandedContent.IsHitTestVisible != isExpandedActive)
            {
                ExpandedContent.IsHitTestVisible = isExpandedActive;
                CompactContent.IsHitTestVisible = !isExpandedActive && state.IsHitTestVisible;
            }

            // OS Window Sync (Layer 1: Window Sync Guarding)
            int physW = (int)Math.Ceiling(state.Width * _dpiScale);
            int physH = (int)Math.Ceiling(state.Height * _dpiScale);
            int physX = (int)Math.Round((state.CenterX - state.Width / 2.0) * _dpiScale);
            int physY;

            // Get the display area where the window is currently located (Best Practice for Multi-Monitor)
            var display = DisplayArea.GetFromWindowId(this.AppWindow.Id, DisplayAreaFallback.Primary);
            int monitorTopPhys = display.WorkArea.Y;

            // DPI-Precise Visible Height (Layer 2: Physical Pixel Anchoring)
            // CRITICAL: Only anchor when the animation has nearly settled to avoid snapping!
            bool isSettled = Math.Abs(state.Height - IslandConfig.CompactHeight) < 1.0 && Math.Abs(state.Y - _controller.TargetY) < 1.0;

            if (isSettled && _controller.IsDocked && !_controller.IsHovered && !_controller.IsNotifying && !_controller.IsDragging)
            {
                // We want EXACTLY 'DockPeekOffset' logical pixels visible.
                double peek = _controller.IsForegroundMaximized ? IslandConfig.MaximizedDockPeekOffset : IslandConfig.DockPeekOffset;
                int visiblePhys = (int)Math.Round(peek * _dpiScale);
                
                // Absolute coordinate relative to the current monitor's top
                physY = monitorTopPhys + visiblePhys - physH;
            }
            else
            {
                physY = (int)Math.Round(state.Y * _dpiScale);
            }

            // If it's animating off-screen (hiding), snap it
            if (_controller.IsOffscreen())
            {
                physY = -1000;
                double progress = _taskProgress ?? _mediaService.Progress;
                _lineWindow?.SetProgress(progress);
                ShowLineWindow();
                UpdateShadowState();
            }

            // ONLY update if physical coordinates changed (Best Practice)
            if (physX != _lastPhysX || physY != _lastPhysY || physW != _lastPhysW || physH != _lastPhysH)
            {
                this.AppWindow.MoveAndResize(new RectInt32(physX, physY, physW, physH));
                _lastPhysX = physX;
                _lastPhysY = physY;
                _lastPhysW = physW;
                _lastPhysH = physH;
            }
        }

        #endregion

        #region Drag & Dock

        private void RootGrid_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            // Only start dragging if the click wasn't on a button or interactive element
            if (e.OriginalSource is FrameworkElement fe && (fe is Button || fe.Parent is Button))
            {
                return;
            }

            var props = e.GetCurrentPoint(RootGrid).Properties;
            if (props.IsLeftButtonPressed)
            {
                _controller.IsDragging = true;
                RootGrid.CapturePointer(e.Pointer);
                
                GetCursorPos(out _dragStartScreenPos);
                
                // Get current physical position
                double physCenterX = (_controller.Current.CenterX) * _dpiScale;
                double physCenterY = (_controller.Current.Y) * _dpiScale;

                _dragPhysicalOffsetX = _dragStartScreenPos.X - physCenterX;
                _dragPhysicalOffsetY = _dragStartScreenPos.Y - physCenterY;

                UpdateState();
            }
        }

        private void RootGrid_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (_controller.IsDragging)
            {
                GetCursorPos(out var currentPos);
                
                // Calculate target physical center based on original cursor-to-center offset
                double targetPhysCenterX = currentPos.X - _dragPhysicalOffsetX;
                double targetPhysCenterY = currentPos.Y - _dragPhysicalOffsetY;

                // Get current display area based on cursor position for boundary checking
                var display = DisplayArea.GetFromPoint(new PointInt32(currentPos.X, currentPos.Y), DisplayAreaFallback.Primary);
                var bounds = display.WorkArea;

                // Boundary Guard: Keep window partially on screen so it can't be lost
                double halfWidthPhys = (IslandConfig.CompactWidth / 2.0) * _dpiScale;
                targetPhysCenterX = Math.Clamp(targetPhysCenterX, bounds.X + halfWidthPhys, bounds.X + bounds.Width - halfWidthPhys);
                targetPhysCenterY = Math.Clamp(targetPhysCenterY, bounds.Y, bounds.Y + bounds.Height - 10);

                // Update DPI Scale for the monitor where the window is being dragged
                _dpiScale = this.GetDpiForWindow() / 96.0;

                // Delegate coordinates to controller (handles real-time dock release and target calculation)
                _controller.HandleDrag(targetPhysCenterX / _dpiScale, targetPhysCenterY / _dpiScale);
            }
        }

        private void RootGrid_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (_controller.IsDragging)
            {
                _controller.IsDragging = false;
                RootGrid.ReleasePointerCapture(e.Pointer);
                _controller.FinalizeDrag();
                UpdateState();
                UpdateShadowState();
                SavePositionSettings();
            }
        }

        #endregion

        #region Hover

        private void RootGrid_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _hoverDebounceTimer.Stop();
            
            if (_controller.IsDocked && !_controller.IsDragging && _controller.IsForegroundMaximized)
            {
                _controller.IsHoverPending = true;
                _dockedHoverDelayTimer.Start();
            }
            else
            {
                _controller.IsHovered = true;
                UpdateState();
            }
        }

        private void RootGrid_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (_controller.IsHoverPending)
            {
                _controller.IsHoverPending = false;
                _dockedHoverDelayTimer.Stop();
            }
            else
            {
                _hoverDebounceTimer.Start();
            }
        }

        #endregion

        #region Notifications

        /// <summary>
        /// Show an expanded notification for the specified duration.
        /// </summary>
        public async void ShowNotification(string title, string message, int durationMs = IslandConfig.DefaultNotificationDurationMs)
        {
            try
            {
                this.DispatcherQueue.TryEnqueue(() =>
                {
                    ExpandedContent.Update(title, message, "Notification", false);
                    _controller.IsNotifying = true;
                    UpdateState();
                });
                await Task.Delay(durationMs);
                this.DispatcherQueue.TryEnqueue(() =>
                {
                    _controller.IsNotifying = false;
                    UpdateState();
                    SyncMediaUI();
                });
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "ShowNotification failed");
            }
        }

        #endregion

        #region Task Progress

        /// <summary>
        /// Set a custom task progress (0.0 to 1.0) which overrides media progress.
        /// </summary>
        public void SetTaskProgress(double progress)
        {
            _taskProgress = Math.Clamp(progress, 0.0, 1.0);
        }

        /// <summary>
        /// Clear the custom task progress and revert to tracking media progress.
        /// </summary>
        public void ClearTaskProgress()
        {
            _taskProgress = null;
        }

        #endregion

        #region Backdrop

        /// <summary>Supported backdrop effect types.</summary>
        public enum BackdropType { Mica, Acrylic, None }

        /// <summary>
        /// Apply a system backdrop effect and update text colors to match.
        /// </summary>
        public void SetBackdrop(BackdropType type)
        {
            Windows.UI.Color textColor;
            Windows.UI.Color subTextColor;

            switch (type)
            {
                case BackdropType.Mica:
                    this.SystemBackdrop = new MicaBackdrop();
                    IslandBorder.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                    textColor = Microsoft.UI.Colors.Black;
                    subTextColor = Microsoft.UI.Colors.DimGray;
                    break;
                case BackdropType.Acrylic:
                    this.SystemBackdrop = new DesktopAcrylicBackdrop();
                    IslandBorder.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                    textColor = Microsoft.UI.Colors.Black;
                    subTextColor = Microsoft.UI.Colors.DimGray;
                    break;
                case BackdropType.None:
                default:
                    this.SystemBackdrop = null;
                    IslandBorder.Background = new SolidColorBrush(Microsoft.UI.Colors.Black);
                    textColor = Microsoft.UI.Colors.White;
                    subTextColor = Microsoft.UI.Colors.LightGray;
                    break;
            }
            UpdateTextColors(textColor, subTextColor);

            // Persist backdrop preference
            _settings.BackdropType = type.ToString();
            _settings.Save();
        }

        private void UpdateTextColors(Windows.UI.Color main, Windows.UI.Color sub)
        {
            CompactContent.SetTextColor(main);
            ExpandedContent.SetColors(main, sub);
        }

        private static BackdropType ParseBackdropType(string name) => name switch
        {
            "Acrylic" => BackdropType.Acrylic,
            "None" => BackdropType.None,
            _ => BackdropType.Mica,
        };

        #endregion

        #region Media Integration

        private async Task InitializeMediaAsync()
        {
            _mediaService.MediaChanged += OnMediaServiceChanged;
            _mediaService.TrackChanged += OnTrackChanged;
            await _mediaService.InitializeAsync();
        }

        private void OnMediaServiceChanged()
        {
            this.DispatcherQueue?.TryEnqueue(() => SyncMediaUI());
        }

        private void OnTrackChanged(string title, string artist)
        {
            this.DispatcherQueue?.TryEnqueue(() =>
            {
                if (_controller.IsDocked && !_controller.IsHovered && !_controller.IsDragging)
                    ShowNotification("New Track", title, IslandConfig.TrackChangeNotificationDurationMs);
            });
        }

        /// <summary>Sync UI elements with current MediaService state.</summary>
        private void SyncMediaUI()
        {
            ExpandedContent.Update(
                _mediaService.CurrentTitle,
                _mediaService.CurrentArtist,
                _mediaService.HeaderStatus,
                _mediaService.IsPlaying);
        }

        private async void PlayPause_Click(object? sender, EventArgs e) => await _mediaService.PlayPauseAsync();
        private async void SkipNext_Click(object? sender, EventArgs e) => await _mediaService.SkipNextAsync();
        private async void SkipPrevious_Click(object? sender, EventArgs e) => await _mediaService.SkipPreviousAsync();

        #endregion

        #region Tray & Menu

        private MenuFlyout CreateTrayMenu()
        {
            var menu = new MenuFlyout();
            var showItem = new MenuFlyoutItem { Text = "Show Island" };
            showItem.Click += ShowIsland_Click;
            menu.Items.Add(showItem);
            var testItem = new MenuFlyoutItem { Text = "Test Notification" };
            testItem.Click += TestNotification_Click;
            menu.Items.Add(testItem);
            var testProgressItem = new MenuFlyoutItem { Text = "Test Task Progress" };
            testProgressItem.Click += TestProgress_Click;
            menu.Items.Add(testProgressItem);
            menu.Items.Add(new MenuFlyoutSeparator());
            var backdropSub = new MenuFlyoutSubItem { Text = "Backdrop Style" };
            var micaItem = new MenuFlyoutItem { Text = "Mica" };
            micaItem.Click += Mica_Click;
            backdropSub.Items.Add(micaItem);
            var acrylicItem = new MenuFlyoutItem { Text = "Acrylic" };
            acrylicItem.Click += Acrylic_Click;
            backdropSub.Items.Add(acrylicItem);
            var noneItem = new MenuFlyoutItem { Text = "None" };
            noneItem.Click += None_Click;
            backdropSub.Items.Add(noneItem);
            menu.Items.Add(backdropSub);
            menu.Items.Add(new MenuFlyoutSeparator());
            var exitItem = new MenuFlyoutItem { Text = "Exit" };
            exitItem.Click += Exit_Click;
            menu.Items.Add(exitItem);
            return menu;
        }

        private void ShowIsland_Click(object sender, RoutedEventArgs e) { this.Activate(); this.SetForegroundWindow(); }
        private void TestNotification_Click(object sender, RoutedEventArgs e) => ShowNotification("Dynamic Island", "Flawless Physics!");
        private async void TestProgress_Click(object sender, RoutedEventArgs e)
        {
            for (int i = 0; i <= 100; i += 5)
            {
                SetTaskProgress(i / 100.0);
                await Task.Delay(200);
            }
            ClearTaskProgress();
        }
        private void Mica_Click(object sender, RoutedEventArgs e) => SetBackdrop(BackdropType.Mica);
        private void Acrylic_Click(object sender, RoutedEventArgs e) => SetBackdrop(BackdropType.Acrylic);
        private void None_Click(object sender, RoutedEventArgs e) => SetBackdrop(BackdropType.None);
        private void Exit_Click(object sender, RoutedEventArgs e) => Application.Current.Exit();

        #endregion

        #region Settings Persistence

        /// <summary>Save current position and dock state to settings.</summary>
        private void SavePositionSettings()
        {
            _settings.CenterX = _controller.Current.CenterX;
            _settings.LastY = _controller.Current.Y;
            _settings.IsDocked = _controller.IsDocked;
            _settings.Save();
        }

        #endregion
    }
}
