using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using System;
using System.Runtime.InteropServices;
using Windows.Graphics;
using Windows.Media.Control;
using System.Threading.Tasks;
using WinUIEx;

namespace island
{
    public sealed partial class MainWindow : Window
    {
        private GlobalSystemMediaTransportControlsSessionManager? _mediaManager;
        private GlobalSystemMediaTransportControlsSession? _currentSession;
        private double _dpiScale = 1.0;
        
        // --- Logical Targets & Current Values ---
        private double _targetWidth = 200;
        private double _targetHeight = 30;
        private double _currentWidth = 200;
        private double _currentHeight = 30;

        private double _targetCompactOpacity = 1;
        private double _currentCompactOpacity = 1;
        private double _targetExpandedOpacity = 0;
        private double _currentExpandedOpacity = 0;

        private double _targetY = 10;
        private double _currentY = 10;
        private double _centerX = 0; // Use centerX to ensure symmetric expansion
        
        // --- Logical States ---
        private bool _isHovered = false;
        private bool _isDragging = false;
        private bool _isDocked = false;
        private bool _isNotifying = false;

        private DispatcherTimer _hoverDebounceTimer;
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

                _dpiScale = this.GetDpiForWindow() / 96.0;

                // Initialize Position (Center Top)
                var display = DisplayArea.Primary.WorkArea;
                _centerX = (display.Width / _dpiScale) / 2.0;

                // Tray Icon
                manager.IsVisibleInTray = true;
                manager.TrayIconContextMenu += (s, e) => e.Flyout = CreateTrayMenu();

                SetBackdrop(BackdropType.Mica); 
                _ = InitializeMediaManagerAsync();

                _hoverDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                _hoverDebounceTimer.Tick += (s, e) => {
                    _hoverDebounceTimer.Stop();
                    _isHovered = false;
                    UpdateState();
                };

                CompositionTarget.Rendering += OnCompositionTargetRendering;
            }
            catch (Exception ex)
            {
                System.IO.File.WriteAllText("crash_log.txt", ex.ToString());
                throw;
            }
        }

        private void UpdateState()
        {
            if (_isNotifying)
            {
                _targetWidth = 400;
                _targetHeight = 120;
                _targetCompactOpacity = 0;
                _targetExpandedOpacity = 1;
                _targetY = 10;
            }
            else if (_isHovered && !_isDocked && !_isDragging)
            {
                _targetWidth = 400;
                _targetHeight = 120;
                _targetCompactOpacity = 0;
                _targetExpandedOpacity = 1;
            }
            else
            {
                _targetWidth = 200;
                _targetHeight = 30;
                _targetCompactOpacity = 1;
                _targetExpandedOpacity = 0;

                if (_isDocked && !_isDragging)
                {
                    _targetY = -_targetHeight + 6; 
                }
                else if (!_isDragging)
                {
                    _targetY = Math.Max(10, _currentY); 
                }
            }
        }

        private void OnCompositionTargetRendering(object? sender, object e)
        {
            if (e is RenderingEventArgs args)
                UpdateAnimation(args.RenderingTime);
        }

        private void UpdateAnimation(TimeSpan renderingTime)
        {
            if (_lastRenderTime == TimeSpan.Zero) { _lastRenderTime = renderingTime; return; }
            double dt = (renderingTime - _lastRenderTime).TotalSeconds;
            _lastRenderTime = renderingTime;
            if (dt <= 0 || dt > 0.1) dt = 0.016;

            // FIX: Continuously update DPI scale to fix cross-monitor scaling cuts
            _dpiScale = this.GetDpiForWindow() / 96.0;

            double speed = 25.0; 
            double t = 1.0 - Math.Exp(-speed * dt);

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
            CompactContent.IsHitTestVisible = _currentCompactOpacity > 0.5;
            ExpandedContent.IsHitTestVisible = _currentExpandedOpacity > 0.5;

            // OS Window Sync (Calculate physX from centerX to ensure symmetric expansion)
            int physW = (int)Math.Ceiling(_currentWidth * _dpiScale);
            int physH = (int)Math.Ceiling(_currentHeight * _dpiScale);
            int physX = (int)Math.Round((_centerX - _currentWidth / 2.0) * _dpiScale);
            int physY = (int)Math.Round(_currentY * _dpiScale);

            this.AppWindow.MoveAndResize(new RectInt32(physX, physY, physW, physH));
        }

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
                
                if (_currentY <= 15) {
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
                _isDocked = _currentY <= 15;
                UpdateState();
            }
        }

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

        public async void ShowNotification(string title, string message, int durationMs = 3000)
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
                UpdateCurrentSession();
            });
        }

        public enum BackdropType { Mica, Acrylic, None }

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
        }

        private void UpdateTextColors(Windows.UI.Color main, Windows.UI.Color sub)
        {
            CompactText.Foreground = new SolidColorBrush(main);
            MusicTitleText.Foreground = new SolidColorBrush(main);
            ArtistNameText.Foreground = new SolidColorBrush(sub);
            HeaderStatusText.Foreground = new SolidColorBrush(sub);
            IconBack.Foreground = new SolidColorBrush(main);
            IconPause.Foreground = new SolidColorBrush(main);
            IconForward.Foreground = new SolidColorBrush(main);
        }

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

        private async Task InitializeMediaManagerAsync()
        {
            try
            {
                _mediaManager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                if (_mediaManager != null)
                {
                    _mediaManager.CurrentSessionChanged += (s, e) => UpdateCurrentSession();
                    UpdateCurrentSession();
                }
            }
            catch { }
        }

        private void UpdateCurrentSession()
        {
            var newSession = _mediaManager?.GetCurrentSession();
            
            if (_currentSession != null)
                _currentSession.MediaPropertiesChanged -= OnMediaPropertiesChanged;

            _currentSession = newSession;

            if (_currentSession != null)
            {
                _currentSession.MediaPropertiesChanged += OnMediaPropertiesChanged;
                UpdateUIFromSession(_currentSession);
            }
            else
            {
                this.DispatcherQueue.TryEnqueue(() =>
                {
                    MusicTitleText.Text = "No Media";
                    ArtistNameText.Text = "Waiting for music...";
                    HeaderStatusText.Text = "Dynamic Island";
                });
            }
        }

        private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) 
            => UpdateUIFromSession(sender);

        private async void UpdateUIFromSession(GlobalSystemMediaTransportControlsSession session)
        {
            try
            {
                var props = await session.TryGetMediaPropertiesAsync();
                if (props != null)
                {
                    this.DispatcherQueue.TryEnqueue(() =>
                    {
                        MusicTitleText.Text = string.IsNullOrEmpty(props.Title) ? "Unknown Track" : props.Title;
                        ArtistNameText.Text = string.IsNullOrEmpty(props.Artist) ? "Unknown Artist" : props.Artist;
                        HeaderStatusText.Text = "Now Playing";
                        
                        if (_isDocked && !_isHovered && !_isDragging)
                            ShowNotification("New Track", props.Title, 4000);
                    });
                }
            }
            catch { }
        }
    }
}
