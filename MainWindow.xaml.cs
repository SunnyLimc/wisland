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

        private double _dpiScale = 1.0;

        // --- Logical Targets & Current Values ---
        private double _targetWidth = IslandConfig.CompactWidth;
        private double _targetHeight = IslandConfig.CompactHeight;
        private double _currentWidth = IslandConfig.CompactWidth;
        private double _currentHeight = IslandConfig.CompactHeight;

        private double _targetCompactOpacity = 1;
        private double _currentCompactOpacity = 1;
        private double _targetExpandedOpacity = 0;
        private double _currentExpandedOpacity = 0;

        private double _targetY = IslandConfig.DefaultY;
        private double _currentY = IslandConfig.DefaultY;
        private double _centerX = 0;

        // --- Logical States ---
        private bool _isHovered = false;
        private bool _isDragging = false;
        private bool _isDocked = false;
        private bool _isNotifying = false;

        private readonly DispatcherTimer _hoverDebounceTimer;
        private TimeSpan _lastRenderTime = TimeSpan.Zero;

        // --- Dragging Context ---
        private POINT _dragStartScreenPos;
        private double _dragStartCenterX;
        private double _dragStartY;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X; public int Y; }

        // --- DWM API for Shadow Control ---
        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        
        // Values for DWMWA_WINDOW_CORNER_PREFERENCE
        private const int DWMWCP_DEFAULT = 0;       // Let the system decide
        private const int DWMWCP_DONOTROUND = 1;    // No rounding, no shadow
        private const int DWMWCP_ROUND = 2;         // Full rounded corners + large shadow
        private const int DWMWCP_ROUNDSMALL = 3;    // Small rounded corners + small/light shadow

        public MainWindow()
        {
            try
            {
                this.InitializeComponent();
                this.ExtendsContentIntoTitleBar = true;

                var manager = WindowManager.Get(this);
                manager.IsTitleBarVisible = false;
                manager.IsAlwaysOnTop = true;
                manager.IsResizable = false;
                manager.IsMinimizable = false;
                manager.IsMaximizable = false;

                this.AppWindow.IsShownInSwitchers = false;

                // Set DWM Drop Shadow & Corner Preference
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                // DWMWCP_ROUND gives the maximum standard system corner radius
                int preference = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));

                _dpiScale = this.GetDpiForWindow() / 96.0;

                // Load saved settings
                _settings.Load();

                // Initialize Position
                var display = DisplayArea.Primary.WorkArea;
                _centerX = _settings.CenterX > 0 ? _settings.CenterX : (display.Width / _dpiScale) / 2.0;
                _isDocked = _settings.IsDocked;
                _currentY = _settings.IsDocked ? 0 : Math.Max(IslandConfig.DefaultY, _settings.LastY);
                _targetY = _currentY;

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
                    _isHovered = false;
                    UpdateState();
                };

                CompositionTarget.Rendering += OnCompositionTargetRendering;

                Logger.Info("MainWindow initialized successfully");
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Fatal error during MainWindow initialization");
                throw;
            }
        }

        #region State Machine

        /// <summary>
        /// Recalculate animation targets based on current logical state.
        /// Priority: Notifying > Hovered (non-docked, non-dragging) > Default.
        /// </summary>
        private void UpdateState()
        {
            if (_isNotifying)
            {
                _targetWidth = IslandConfig.ExpandedWidth;
                _targetHeight = IslandConfig.ExpandedHeight;
                _targetCompactOpacity = 0;
                _targetExpandedOpacity = 1;
                _targetY = IslandConfig.DefaultY;
            }
            else if (_isHovered && !_isDocked && !_isDragging)
            {
                _targetWidth = IslandConfig.ExpandedWidth;
                _targetHeight = IslandConfig.ExpandedHeight;
                _targetCompactOpacity = 0;
                _targetExpandedOpacity = 1;
            }
            else
            {
                _targetWidth = IslandConfig.CompactWidth;
                _targetHeight = IslandConfig.CompactHeight;
                _targetCompactOpacity = 1;
                _targetExpandedOpacity = 0;

                if (_isDocked && !_isDragging)
                {
                    _targetY = -_targetHeight + IslandConfig.DockPeekOffset;
                }
                else if (!_isDragging)
                {
                    _targetY = Math.Max(IslandConfig.DefaultY, _currentY);
                }
            }
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

            double t = 1.0 - Math.Exp(-IslandConfig.AnimationSpeed * dt);

            _currentWidth += (_targetWidth - _currentWidth) * t;
            _currentHeight += (_targetHeight - _currentHeight) * t;
            _currentCompactOpacity += (_targetCompactOpacity - _currentCompactOpacity) * t;
            _currentExpandedOpacity += (_targetExpandedOpacity - _currentExpandedOpacity) * t;

            if (!_isDragging)
                _currentY += (_targetY - _currentY) * t;

            // XAML Sync
            IslandBorder.Width = _currentWidth;
            IslandBorder.Height = _currentHeight;
            IslandBorder.CornerRadius = new CornerRadius(_currentHeight / 2.0);

            CompactContent.Opacity = _currentCompactOpacity;
            ExpandedContent.Opacity = _currentExpandedOpacity;
            CompactContent.IsHitTestVisible = _currentCompactOpacity > IslandConfig.HitTestOpacityThreshold;
            ExpandedContent.IsHitTestVisible = _currentExpandedOpacity > IslandConfig.HitTestOpacityThreshold;

            // OS Window Sync (centerX-based for symmetric expansion)
            int physW = (int)Math.Ceiling(_currentWidth * _dpiScale);
            int physH = (int)Math.Ceiling(_currentHeight * _dpiScale);
            int physX = (int)Math.Round((_centerX - _currentWidth / 2.0) * _dpiScale);
            int physY = (int)Math.Round(_currentY * _dpiScale);

            this.AppWindow.MoveAndResize(new RectInt32(physX, physY, physW, physH));
        }

        #endregion

        #region Drag & Dock

        private void RootGrid_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            var props = e.GetCurrentPoint(RootGrid).Properties;
            if (props.IsLeftButtonPressed)
            {
                _isDragging = true;
                RootGrid.CapturePointer(e.Pointer);
                GetCursorPos(out _dragStartScreenPos);
                _dragStartCenterX = _centerX;
                _dragStartY = _currentY;
                UpdateState();
            }
        }

        private void RootGrid_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (_isDragging)
            {
                GetCursorPos(out var currentScreenPos);
                _centerX = _dragStartCenterX + (currentScreenPos.X - _dragStartScreenPos.X) / _dpiScale;
                _currentY = _dragStartY + (currentScreenPos.Y - _dragStartScreenPos.Y) / _dpiScale;

                if (_currentY <= IslandConfig.DockThreshold)
                {
                    _currentY = 0;
                }
                _targetY = _currentY;
            }
        }

        private void RootGrid_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (_isDragging)
            {
                _isDragging = false;
                RootGrid.ReleasePointerCapture(e.Pointer);
                _isDocked = _currentY <= IslandConfig.DockThreshold;
                UpdateState();
                SavePositionSettings();
            }
        }

        #endregion

        #region Hover

        private void RootGrid_PointerEntered(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _hoverDebounceTimer.Stop();
            _isHovered = true;
            UpdateState();
        }

        private void RootGrid_PointerExited(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _hoverDebounceTimer.Start();
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
                    HeaderStatusText.Text = "Notification";
                    MusicTitleText.Text = title;
                    ArtistNameText.Text = message;
                    _isNotifying = true;
                    UpdateState();
                });
                await Task.Delay(durationMs);
                this.DispatcherQueue.TryEnqueue(() =>
                {
                    _isNotifying = false;
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
            CompactText.Foreground = new SolidColorBrush(main);
            MusicTitleText.Foreground = new SolidColorBrush(main);
            ArtistNameText.Foreground = new SolidColorBrush(sub);
            HeaderStatusText.Foreground = new SolidColorBrush(sub);
            IconBack.Foreground = new SolidColorBrush(main);
            IconPlayPause.Foreground = new SolidColorBrush(main);
            IconForward.Foreground = new SolidColorBrush(main);
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
                if (_isDocked && !_isHovered && !_isDragging)
                    ShowNotification("New Track", title, IslandConfig.TrackChangeNotificationDurationMs);
            });
        }

        /// <summary>Sync UI elements with current MediaService state.</summary>
        private void SyncMediaUI()
        {
            MusicTitleText.Text = _mediaService.CurrentTitle;
            ArtistNameText.Text = _mediaService.CurrentArtist;
            HeaderStatusText.Text = _mediaService.HeaderStatus;
            IconPlayPause.Symbol = _mediaService.IsPlaying ? Symbol.Pause : Symbol.Play;
        }

        private async void PlayPause_Click(object sender, RoutedEventArgs e) => await _mediaService.PlayPauseAsync();
        private async void SkipNext_Click(object sender, RoutedEventArgs e) => await _mediaService.SkipNextAsync();
        private async void SkipPrevious_Click(object sender, RoutedEventArgs e) => await _mediaService.SkipPreviousAsync();

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
        private void Mica_Click(object sender, RoutedEventArgs e) => SetBackdrop(BackdropType.Mica);
        private void Acrylic_Click(object sender, RoutedEventArgs e) => SetBackdrop(BackdropType.Acrylic);
        private void None_Click(object sender, RoutedEventArgs e) => SetBackdrop(BackdropType.None);
        private void Exit_Click(object sender, RoutedEventArgs e) => Application.Current.Exit();

        #endregion

        #region Settings Persistence

        /// <summary>Save current position and dock state to settings.</summary>
        private void SavePositionSettings()
        {
            _settings.CenterX = _centerX;
            _settings.LastY = _currentY;
            _settings.IsDocked = _isDocked;
            _settings.Save();
        }

        #endregion
    }
}
