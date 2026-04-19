using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics.Effects;
using Windows.Storage.Streams;
using Windows.UI;
using wisland.Controls;
using wisland.Helpers;
using wisland.Models;

namespace wisland.Views
{
    public sealed partial class ImmersiveMediaView : UserControl
    {
        private static MediaSourceIconResolver IconResolver => MediaSourceIconResolver.Shared;

        private const int ArtCrossfadeDurationMs = 400;
        private const int GradientCrossfadeDurationMs = 600;

        private readonly DirectionalContentTransitionCoordinator _metadataTransition;
        private readonly MetadataSnapshot[] _metadataSnapshots = new MetadataSnapshot[2];

        private Color _subColor = Color.FromArgb(255, 180, 180, 190);
        private bool _canOpenSessionPicker;

        private IRandomAccessStreamReference? _lastThumbnailRef;
        private string? _lastAlbumArtIdentity; // Content-based key for album art change detection
        private ImageSource? _previousAlbumArtSource; // Holds the old art for crossfade outgoing slot
        private string? _lastSourceIconIdentity;
        private bool _isBusyTransport; // True during transport switching grace period
        private bool _hasAlbumArt;     // True when album art is currently displayed
        private double _lastAnimatedProgress;
        private bool _isSeeking;

        // Auto-advancing progress state: anchor the last known position/duration to
        // wall-clock time so the progress bar can tick forward between GSMTC updates.
        private double _progressAnchorPositionSeconds;
        private DateTimeOffset _progressAnchorTime;
        private double _progressDurationSeconds;
        private bool _progressIsPlaying;
        private bool _progressHasTimeline;
        private string? _progressSessionKey; // Track session identity for reset-on-switch.
        private Microsoft.UI.Xaml.DispatcherTimer? _progressTimer;
        private Microsoft.UI.Xaml.Media.Animation.Storyboard? _progressScaleStoryboard;
        private Microsoft.UI.Xaml.Media.Animation.Storyboard? _sessionSwitchStoryboard;
        private bool _isSessionSwitching; // While true, suppress ticker so drain-then-grow plays out.

        // Composition blur for album art background
        private Compositor? _compositor;
        private SpriteVisual? _blurVisual;
        private CompositionSurfaceBrush? _blurSurfaceBrush;
        private CompositionEffectBrush? _blurEffectBrush;

        public event EventHandler? BackClick;
        public event EventHandler? PlayPauseClick;
        public event EventHandler? ForwardClick;
        public event EventHandler? SessionPickerToggleRequested;
        public event EventHandler<double>? SeekRequested; // double = ratio 0..1

        public ImmersiveMediaView()
        {
            this.InitializeComponent();

            _metadataTransition = new DirectionalContentTransitionCoordinator(
                MetadataViewport,
                MetadataSlotPrimary,
                MetadataSlotSecondary,
                IslandConfig.ExpandedMediaTransitionProfile);

            _metadataSnapshots[0] = ReadMetadataSnapshotFromSlot(0);
            _metadataSnapshots[1] = ReadMetadataSnapshotFromSlot(1);

            InitializeHeaderChipArrays();

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            InitializeBlurVisual();
            ApplyTextColors();
            ApplyTransportColors();
            InitializeHeaderChipAtLoad();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            StopProgressTicker();
            StopSessionSwitchAnimation();
            StopScaleAnimation();
            if (_progressTimer != null)
            {
                _progressTimer.Tick -= OnProgressTick;
                _progressTimer = null;
            }

            // Detach composition child visual and dispose resources so GPU memory
            // is released when the view is torn down.
            try
            {
                ElementCompositionPreview.SetElementChildVisual(BlurHost, null);
            }
            catch { /* already detached */ }

            _blurVisual?.Dispose();
            _blurVisual = null;
            _blurEffectBrush?.Dispose();
            _blurEffectBrush = null;
            _blurSurfaceBrush?.Dispose();
            _blurSurfaceBrush = null;
            _compositor = null;
        }

        /// <summary>
        /// P2b: frame-driven overload. Thin wrapper delegating to the legacy call;
        /// P4 will internalize fingerprint/kind and delete the legacy overload.
        /// </summary>
        public bool UpdateMedia(MediaPresentationFrame frame)
        {
            if (frame == null) return false;
            ContentTransitionDirection direction = frame.Transition switch
            {
                FrameTransitionKind.SlideForward => ContentTransitionDirection.Forward,
                FrameTransitionKind.SlideBackward => ContentTransitionDirection.Backward,
                _ => ContentTransitionDirection.None
            };
            bool switchingHint = frame.Kind == PresentationKind.Switching;
            return UpdateMedia(
                frame.Session,
                frame.DisplayIndex,
                frame.OrderedSessions.Count,
                frame.OrderedSessions,
                direction,
                switchingHint);
        }

        public bool UpdateMedia(
            MediaSessionSnapshot? session,
            int displayIndex,
            int sessionCount,
            IReadOnlyList<MediaSessionSnapshot> availableSessions,
            ContentTransitionDirection direction = ContentTransitionDirection.None,
            bool showTransportSwitchingHint = false)
        {
            bool showBusyTransportState = showTransportSwitchingHint
                && session.HasValue
                && session.Value.MissingSinceUtc.HasValue;

            UpdatePlayPauseSymbol(session.HasValue
                && session.Value.IsPlaying
                && !session.Value.IsWaitingForReconnect
                && !showBusyTransportState);

            _canOpenSessionPicker = availableSessions.Count > 1;

            // Header chip: text + avatar strip
            HeaderTextSnapshot headerSnapshot = CreateMediaHeaderTextSnapshot(session, sessionCount, showBusyTransportState);
            AvatarStripSnapshot avatarSnapshot = CreateMediaAvatarStripSnapshot(session, displayIndex, availableSessions);
            ApplyHeaderTextSnapshot(headerSnapshot, avatarSnapshot, direction);
            ApplyAvatarStripSnapshot(avatarSnapshot, direction);
            UpdateHeaderChipActionability(useTransitions: IsLoaded);

            // Source badge icon (bottom-right of album art)
            if (session.HasValue)
            {
                string sourceIconIdentity = session.Value.SourceAppId;
                if (!string.Equals(_lastSourceIconIdentity, sourceIconIdentity, StringComparison.Ordinal))
                {
                    _lastSourceIconIdentity = sourceIconIdentity;
                    LoadSourceIcon(sourceIconIdentity);
                }
            }

            // Metadata (title + artist) with transitions
            MetadataSnapshot nextMetadata = session.HasValue
                ? new MetadataSnapshot(session.Value.Title, GetMetadataSubtitle(session.Value, showBusyTransportState))
                : new MetadataSnapshot(Loc.GetString("Media/NoMedia"), Loc.GetString("Media/WaitingForMusic"));

            bool metadataChanged = ApplyMetadataSnapshot(nextMetadata, direction);

            // Track switching state — hold old album art/background during grace period
            _isBusyTransport = showBusyTransportState;

            // Album art — reload when the track identity changes (content-based),
            // not when the thumbnail reference is reissued by GSMTC.
            if (session.HasValue && !showBusyTransportState)
            {
                var thumbnailRef = session.Value.Thumbnail;
                string newIdentity = BuildAlbumArtIdentity(session.Value);
                if (!string.Equals(_lastAlbumArtIdentity, newIdentity, StringComparison.Ordinal))
                {
                    _lastAlbumArtIdentity = newIdentity;
                    _lastThumbnailRef = thumbnailRef;
                    if (thumbnailRef != null)
                    {
                        LoadAlbumArt(session.Value);
                    }
                    // If thumbnail is null but we had art before, keep the old art
                    // (GSMTC may send null briefly during track transitions)
                }
            }
            else if (!session.HasValue)
            {
                ClearAlbumArt();
            }
            // When showBusyTransportState is true: don't touch album art at all — keep old visuals

            // Progress
            UpdateProgress(session);

            return metadataChanged;
        }

        public void ShowNotification(string title, string message, string header)
        {
            _canOpenSessionPicker = false;
            var headerSnapshot = new HeaderTextSnapshot(header, ShowExpandHint: false);
            ApplyHeaderTextSnapshot(headerSnapshot, AvatarStripSnapshot.Empty, ContentTransitionDirection.None);
            ApplyAvatarStripSnapshot(AvatarStripSnapshot.Empty, ContentTransitionDirection.None);
            UpdateHeaderChipActionability(useTransitions: IsLoaded);
            ApplyMetadataSnapshot(new MetadataSnapshot(title, message), ContentTransitionDirection.None);
        }

        public void SetColors(Color main, Color sub, Color icon)
        {
            // Immersive view ignores theme tokens — text is always light on dark background.
            // Colors are driven by album art palette via ApplyBackgroundPalette instead.
        }

        public void SetSessionPickerExpanded(
            bool isExpanded,
            bool useTransitions = true,
            int? durationOverrideMs = null)
        {
            _isSessionPickerExpanded = isExpanded;
            ApplyHeaderExpandGlyphStateToAllSlots(
                useTransitions && IsLoaded,
                durationOverrideMs ?? IslandConfig.HeaderExpandGlyphToggleDurationMs);
        }

        public Rect GetSessionPickerAnchorBounds(UIElement relativeTo)
        {
            GeneralTransform transform = SessionHeaderButton.TransformToVisual(relativeTo);
            return transform.TransformBounds(new Rect(0, 0, SessionHeaderButton.ActualWidth, SessionHeaderButton.ActualHeight));
        }

        // --- Private helpers ---

        private void UpdatePlayPauseSymbol(bool isPlaying)
        {
            PlayPauseIcon.Glyph = isPlaying ? "\uE769" : "\uE768"; // Pause : Play
        }

        private void UpdateProgress(MediaSessionSnapshot? session)
        {
            string? newKey = session?.SessionKey;
            bool sessionChanged = !string.Equals(_progressSessionKey, newKey, StringComparison.Ordinal);

            if (!session.HasValue || !session.Value.HasTimeline || session.Value.DurationSeconds <= 0)
            {
                _progressSessionKey = newKey;
                _progressHasTimeline = false;
                _progressIsPlaying = false;
                _progressDurationSeconds = 0;
                StopProgressTicker();
                StopSessionSwitchAnimation();

                ElapsedTimeText.Text = "";
                TotalTimeText.Text = "";
                AnimateScaleTo(0.0, 260);
                ElapsedTimeText.Visibility = Visibility.Collapsed;
                TotalTimeText.Visibility = Visibility.Collapsed;
                ProgressTrack.Visibility = Visibility.Collapsed;
                ProgressFill.Visibility = Visibility.Collapsed;
                return;
            }

            ElapsedTimeText.Visibility = Visibility.Visible;
            TotalTimeText.Visibility = Visibility.Visible;
            ProgressTrack.Visibility = Visibility.Visible;
            ProgressFill.Visibility = Visibility.Visible;

            double progress = Math.Clamp(session.Value.Progress, 0.0, 1.0);
            double duration = session.Value.DurationSeconds;
            double positionSeconds = progress * duration;

            // Always re-anchor so the ticker never carries over stale offsets from a
            // previous session (prevents the "started from 3s" bug on session switch).
            _progressAnchorPositionSeconds = positionSeconds;
            _progressAnchorTime = DateTimeOffset.UtcNow;
            _progressDurationSeconds = duration;
            _progressIsPlaying = session.Value.IsPlaying
                && !session.Value.IsWaitingForReconnect;
            _progressHasTimeline = true;

            TotalTimeText.Text = FormatTime(duration);

            if (sessionChanged && _progressSessionKey != null)
            {
                // Drain the old bar to zero, then grow to the new position.
                _progressSessionKey = newKey;
                if (!_isSeeking)
                {
                    StartSessionSwitchAnimation(progress);
                }
            }
            else
            {
                _progressSessionKey = newKey;
                if (!_isSeeking && !_isSessionSwitching)
                {
                    ElapsedTimeText.Text = FormatTime(positionSeconds);
                    AnimateScaleTo(progress, 260);
                }
            }

            if (_progressIsPlaying && !_isSessionSwitching)
            {
                StartProgressTicker();
            }
            else
            {
                StopProgressTicker();
            }
        }

        private void StartSessionSwitchAnimation(double targetRatio)
        {
            StopSessionSwitchAnimation();
            StopProgressTicker();
            _isSessionSwitching = true;

            // Phase A: animate scale to 0 and elapsed text to "0:00" over 260ms.
            StopScaleAnimation();
            double current = _lastAnimatedProgress;
            var drain = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            var drainAnim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                From = current,
                To = 0.0,
                Duration = TimeSpan.FromMilliseconds(260),
                EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
                {
                    EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut
                }
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(drainAnim, ProgressFillScale);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(drainAnim, "ScaleX");
            drain.Children.Add(drainAnim);

            ElapsedTimeText.Text = FormatTime(0);

            drain.Completed += (_, _) =>
            {
                if (!ReferenceEquals(_sessionSwitchStoryboard, drain))
                {
                    return;
                }

                _lastAnimatedProgress = 0.0;
                // Phase B: re-anchor to wall-clock now and animate up to the current position.
                _progressAnchorPositionSeconds = targetRatio * _progressDurationSeconds;
                _progressAnchorTime = DateTimeOffset.UtcNow;

                var grow = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
                var growAnim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
                {
                    From = 0.0,
                    To = targetRatio,
                    Duration = TimeSpan.FromMilliseconds(320),
                    EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
                    {
                        EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut
                    }
                };
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(growAnim, ProgressFillScale);
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(growAnim, "ScaleX");
                grow.Children.Add(growAnim);

                ElapsedTimeText.Text = FormatTime(_progressAnchorPositionSeconds);

                grow.Completed += (_, _) =>
                {
                    if (!ReferenceEquals(_sessionSwitchStoryboard, grow))
                    {
                        return;
                    }

                    _lastAnimatedProgress = targetRatio;
                    _isSessionSwitching = false;
                    _sessionSwitchStoryboard = null;
                    if (_progressIsPlaying)
                    {
                        StartProgressTicker();
                    }
                };

                _sessionSwitchStoryboard = grow;
                grow.Begin();
            };

            _sessionSwitchStoryboard = drain;
            drain.Begin();
        }

        private void StopSessionSwitchAnimation()
        {
            _sessionSwitchStoryboard?.Stop();
            _sessionSwitchStoryboard = null;
            _isSessionSwitching = false;
        }

        private void StartProgressTicker()
        {
            if (_progressTimer == null)
            {
                _progressTimer = new Microsoft.UI.Xaml.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(250)
                };
                _progressTimer.Tick += OnProgressTick;
            }

            if (!_progressTimer.IsEnabled)
            {
                _progressTimer.Start();
            }
        }

        private void StopProgressTicker()
        {
            _progressTimer?.Stop();
        }

        private void OnProgressTick(object? sender, object e)
        {
            if (!_progressHasTimeline || !_progressIsPlaying || _isSeeking || _isSessionSwitching)
            {
                return;
            }

            double elapsedSinceAnchor = (DateTimeOffset.UtcNow - _progressAnchorTime).TotalSeconds;
            double position = _progressAnchorPositionSeconds + elapsedSinceAnchor;
            if (position >= _progressDurationSeconds)
            {
                position = _progressDurationSeconds;
                _progressIsPlaying = false;
                StopProgressTicker();
            }

            double ratio = _progressDurationSeconds > 0
                ? Math.Clamp(position / _progressDurationSeconds, 0.0, 1.0)
                : 0.0;

            ElapsedTimeText.Text = FormatTime(position);
            AnimateScaleTo(ratio, 260);
        }

        /// <summary>
        /// Smoothly animates the progress fill scale to the target ratio. Never snaps —
        /// session switches are handled separately by StartSessionSwitchAnimation and
        /// seeks by the pointer-capture flow.
        /// </summary>
        private void AnimateScaleTo(double target, int durationMs)
        {
            double current = _lastAnimatedProgress;
            if (Math.Abs(current - target) < 0.0005)
            {
                return;
            }

            StopScaleAnimation();

            var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            var anim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                From = current,
                To = target,
                Duration = TimeSpan.FromMilliseconds(durationMs),
                EasingFunction = null // linear
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(anim, ProgressFillScale);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(anim, "ScaleX");
            storyboard.Children.Add(anim);
            _progressScaleStoryboard = storyboard;
            storyboard.Begin();
            _lastAnimatedProgress = target;
        }

        private void StopScaleAnimation()
        {
            _progressScaleStoryboard?.Stop();
            _progressScaleStoryboard = null;
        }

        private static string FormatTime(double totalSeconds)
        {
            if (double.IsNaN(totalSeconds) || totalSeconds < 0) totalSeconds = 0;
            int minutes = (int)(totalSeconds / 60);
            int seconds = (int)(totalSeconds % 60);
            return $"{minutes}:{seconds:D2}";
        }

        private static string BuildAlbumArtIdentity(MediaSessionSnapshot session)
        {
            // Content-based key so reissued IRandomAccessStreamReference objects with
            // the same underlying art don't cause reloads. A track change (different
            // title/artist) or a switch between a present/absent thumbnail will flip
            // the key and trigger a reload.
            bool hasThumb = session.Thumbnail != null;
            return $"{session.SessionKey}|{session.Title}|{session.Artist}|{(hasThumb ? '1' : '0')}";
        }

        private async void LoadAlbumArt(MediaSessionSnapshot session)
        {
            var targetRef = session.Thumbnail;
            string targetIdentity = BuildAlbumArtIdentity(session);

            // Save the previous art source SYNCHRONOUSLY before any await so rapid
            // switches don't corrupt the outgoing slot. This captures whatever is
            // currently visible before the new load begins.
            ImageSource? previousForThisLoad = AlbumArtImage.Source;

            try
            {
                // --- Phase 1: Load everything in the background ---
                BitmapImage? albumArt = await AlbumArtColorExtractor.LoadThumbnailAsync(targetRef);
                if (!string.Equals(_lastAlbumArtIdentity, targetIdentity, StringComparison.Ordinal)) return;
                if (_isBusyTransport) { _lastAlbumArtIdentity = null; return; }

                if (albumArt == null)
                {
                    if (!_hasAlbumArt) ClearAlbumArt();
                    return;
                }

                // Start color extraction in parallel while we wait for image realization
                string colorKey = $"{session.SessionKey}:{session.Title}:{session.Artist}";
                Task<AlbumArtPalette?> paletteTask = AlbumArtColorExtractor.ExtractAsync(targetRef, colorKey);

                // --- Phase 2: Pre-realize the image before crossfading ---
                _previousAlbumArtSource = previousForThisLoad;

                bool imageReady = await PreRealizeAlbumArtAsync(albumArt);
                if (!string.Equals(_lastAlbumArtIdentity, targetIdentity, StringComparison.Ordinal)) return;
                if (_isBusyTransport) { _lastAlbumArtIdentity = null; return; }
                if (!imageReady) return;

                AlbumArtPalette? palette = await paletteTask;
                if (!string.Equals(_lastAlbumArtIdentity, targetIdentity, StringComparison.Ordinal)) return;
                if (_isBusyTransport) { _lastAlbumArtIdentity = null; return; }

                // --- Phase 3: Perform all crossfades atomically ---
                CrossfadeAlbumArtFromPreRealized();
                _hasAlbumArt = true;
                AlbumArtFallback.Visibility = Visibility.Collapsed;

                if (palette.HasValue)
                {
                    CrossfadeBackgroundPalette(palette.Value);
                }

                await LoadBlurredBackgroundAsync(targetRef);
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to load album art: {ex.Message}");
                // Don't clear on error if we already have art — degrade gracefully
            }
        }

        /// <summary>
        /// Sets the new art on AlbumArtImage (which is currently hidden at composition level)
        /// and waits for the ImageOpened event so the GPU texture is ready.
        /// Returns true if the image was successfully realized.
        /// </summary>
        private async Task<bool> PreRealizeAlbumArtAsync(BitmapImage newArt)
        {
            var tcs = new TaskCompletionSource<bool>();

            void OnOpened(object s, RoutedEventArgs e)
            {
                AlbumArtImage.ImageOpened -= OnOpened;
                AlbumArtImage.ImageFailed -= OnFailed;
                tcs.TrySetResult(true);
            }

            void OnFailed(object s, ExceptionRoutedEventArgs e)
            {
                AlbumArtImage.ImageOpened -= OnOpened;
                AlbumArtImage.ImageFailed -= OnFailed;
                tcs.TrySetResult(false);
            }

            AlbumArtImage.ImageOpened += OnOpened;
            AlbumArtImage.ImageFailed += OnFailed;

            // Keep AlbumArtImage invisible at composition level while loading
            if (_compositor != null)
                ElementCompositionPreview.GetElementVisual(AlbumArtImage).Opacity = 0f;

            AlbumArtImage.Source = newArt;

            // Timeout after 2 seconds — don't block forever
            var timeoutTask = Task.Delay(2000);
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

            if (completedTask == timeoutTask)
            {
                AlbumArtImage.ImageOpened -= OnOpened;
                AlbumArtImage.ImageFailed -= OnFailed;
                // Realization took longer than expected. The BitmapImage was already
                // decoded by SetSourceAsync upstream, so the pixel data exists; the
                // ImageOpened event may simply be delayed because the Image element
                // starts at composition-opacity 0. Proceed with the crossfade — the
                // animation takes long enough that the rendered texture will catch up.
                return true;
            }

            return tcs.Task.Result;
        }

        /// <summary>
        /// Performs the crossfade animation after the new art has been pre-realized on AlbumArtImage.
        /// AlbumArtImage already has the new Source set (hidden at composition opacity=0).
        /// </summary>
        private void CrossfadeAlbumArtFromPreRealized()
        {
            if (_compositor != null)
            {
                var easing = _compositor.CreateCubicBezierEasingFunction(
                    new Vector2(0.25f, 0.1f), new Vector2(0.25f, 1.0f));

                Visual outgoingVisual = ElementCompositionPreview.GetElementVisual(AlbumArtImageOutgoing);
                Visual incomingVisual = ElementCompositionPreview.GetElementVisual(AlbumArtImage);

                // Move old art to outgoing
                AlbumArtImageOutgoing.Source = _previousAlbumArtSource;
                outgoingVisual.Opacity = 1f;
                // incomingVisual already at 0f from PreRealize

                var fadeIn = _compositor.CreateScalarKeyFrameAnimation();
                fadeIn.InsertKeyFrame(0f, 0f);
                fadeIn.InsertKeyFrame(1f, 1f, easing);
                fadeIn.Duration = TimeSpan.FromMilliseconds(ArtCrossfadeDurationMs);

                var fadeOut = _compositor.CreateScalarKeyFrameAnimation();
                fadeOut.InsertKeyFrame(0f, 1f);
                fadeOut.InsertKeyFrame(1f, 0f, easing);
                fadeOut.Duration = TimeSpan.FromMilliseconds(ArtCrossfadeDurationMs);

                incomingVisual.StartAnimation("Opacity", fadeIn);
                outgoingVisual.StartAnimation("Opacity", fadeOut);
            }
            else
            {
                AlbumArtImageOutgoing.Source = _previousAlbumArtSource;
                AlbumArtImage.Opacity = 1;
                AlbumArtImageOutgoing.Opacity = 0;
            }
        }

        /// <summary>
        /// Crossfades the gradient background from current colors to new palette colors.
        /// Uses composition-level opacity to avoid single-frame flashes.
        /// </summary>
        private void CrossfadeBackgroundPalette(AlbumArtPalette palette)
        {
            // Copy current active gradient to outgoing
            OutgoingGradientStop0.Color = GradientStop0.Color;
            OutgoingGradientStop1.Color = GradientStop1.Color;
            OutgoingGradientStop2.Color = GradientStop2.Color;

            // Set new colors on active gradient
            GradientStop0.Color = ClampBrightness(palette.Dominant, maxLuminance: 80);
            GradientStop1.Color = ClampBrightness(palette.Secondary, maxLuminance: 55);
            GradientStop2.Color = ClampBrightness(palette.Average, maxLuminance: 65);

            // Update progress fill accent
            ProgressFillBrush.Color = EnsureBright(palette.Dominant);

            if (_compositor != null)
            {
                var easing = _compositor.CreateCubicBezierEasingFunction(
                    new Vector2(0.25f, 0.1f), new Vector2(0.25f, 1.0f));

                Visual activeVisual = ElementCompositionPreview.GetElementVisual(GradientBackground);
                Visual outgoingVisual = ElementCompositionPreview.GetElementVisual(GradientBackgroundOutgoing);

                // Snap at composition level — no XAML property changes
                outgoingVisual.Opacity = 1f;
                activeVisual.Opacity = 0f;

                var fadeIn = _compositor.CreateScalarKeyFrameAnimation();
                fadeIn.InsertKeyFrame(0f, 0f);
                fadeIn.InsertKeyFrame(1f, 1f, easing);
                fadeIn.Duration = TimeSpan.FromMilliseconds(GradientCrossfadeDurationMs);

                var fadeOut = _compositor.CreateScalarKeyFrameAnimation();
                fadeOut.InsertKeyFrame(0f, 1f);
                fadeOut.InsertKeyFrame(1f, 0f, easing);
                fadeOut.Duration = TimeSpan.FromMilliseconds(GradientCrossfadeDurationMs);

                activeVisual.StartAnimation("Opacity", fadeIn);
                outgoingVisual.StartAnimation("Opacity", fadeOut);
            }
            else
            {
                GradientBackground.Opacity = 1;
                GradientBackgroundOutgoing.Opacity = 0;
            }
        }

        private void ClearAlbumArt()
        {
            AlbumArtImage.Source = null;
            AlbumArtImageOutgoing.Source = null;
            _previousAlbumArtSource = null;
            AlbumArtFallback.Visibility = Visibility.Visible;
            ClearBlurSurface();
            _lastThumbnailRef = null;
            _lastAlbumArtIdentity = null;
            _hasAlbumArt = false;
            ApplyDefaultBackgroundPalette();

            // Reset composition opacities
            if (_compositor != null)
            {
                ElementCompositionPreview.GetElementVisual(AlbumArtImage).Opacity = 1f;
                ElementCompositionPreview.GetElementVisual(AlbumArtImageOutgoing).Opacity = 0f;
            }
            else
            {
                AlbumArtImage.Opacity = 1;
                AlbumArtImageOutgoing.Opacity = 0;
            }
        }

        /// <summary>
        /// Applies default (dark) gradient without crossfade — used for initial state and clear.
        /// </summary>
        private void ApplyDefaultBackgroundPalette()
        {
            var palette = AlbumArtPalette.Default;
            GradientStop0.Color = palette.Dominant;
            GradientStop1.Color = palette.Secondary;
            GradientStop2.Color = palette.Average;

            // Also reset outgoing stops so a later crossfade doesn't briefly flash
            // stale colors if the outgoing gradient is made visible again.
            OutgoingGradientStop0.Color = palette.Dominant;
            OutgoingGradientStop1.Color = palette.Secondary;
            OutgoingGradientStop2.Color = palette.Average;

            // Use composition-level opacity to avoid flashes
            if (_compositor != null)
            {
                ElementCompositionPreview.GetElementVisual(GradientBackground).Opacity = 1f;
                ElementCompositionPreview.GetElementVisual(GradientBackgroundOutgoing).Opacity = 0f;
            }
            else
            {
                GradientBackground.Opacity = 1;
                GradientBackgroundOutgoing.Opacity = 0;
            }

            _subColor = Color.FromArgb(210, 200, 200, 215);
            ProgressFillBrush.Color = Microsoft.UI.Colors.White;

            if (IsLoaded)
            {
                ApplyTextColors();
                ApplyTransportColors();
            }
        }

        /// <summary>
        /// Clamps a color's perceived luminance to a maximum, preserving hue.
        /// Ensures gradient background is always dark enough for white text.
        /// </summary>
        private static Color ClampBrightness(Color c, double maxLuminance)
        {
            double lum = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
            if (lum <= maxLuminance)
                return c;

            double scale = maxLuminance / lum;
            return Color.FromArgb(c.A,
                (byte)(c.R * scale),
                (byte)(c.G * scale),
                (byte)(c.B * scale));
        }

        /// <summary>
        /// Ensures a color is bright enough to be visible as an accent on dark backgrounds.
        /// Used only for progress fill — not text (text is always white).
        /// </summary>
        private static Color EnsureBright(Color c)
        {
            double lum = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
            if (lum >= 140)
                return c;
            if (lum < 10)
                return Color.FromArgb(255, 200, 200, 220); // Near-black → default light accent

            double scale = Math.Min(4.0, 180.0 / lum);
            return Color.FromArgb(255,
                (byte)Math.Min(255, (int)(c.R * scale + 30)),
                (byte)Math.Min(255, (int)(c.G * scale + 30)),
                (byte)Math.Min(255, (int)(c.B * scale + 30)));
        }

        // --- Composition blur pipeline ---

        private void InitializeBlurVisual()
        {
            if (_compositor != null) return;

            Visual hostVisual = ElementCompositionPreview.GetElementVisual(BlurHost);
            _compositor = hostVisual.Compositor;

            // Surface brush: receives the album art image
            _blurSurfaceBrush = _compositor.CreateSurfaceBrush();
            _blurSurfaceBrush.Stretch = CompositionStretch.UniformToFill;

            // Effect graph: GaussianBlur wrapping the surface
            IGraphicsEffect blurEffect = new GaussianBlurEffect
            {
                Name = "Blur",
                BlurAmount = 40f,
                BorderMode = EffectBorderMode.Hard,
                Source = new CompositionEffectSourceParameter("source")
            };

            CompositionEffectFactory factory = _compositor.CreateEffectFactory(blurEffect);
            _blurEffectBrush = factory.CreateBrush();
            _blurEffectBrush.SetSourceParameter("source", _blurSurfaceBrush);

            // Sprite visual that paints the blurred result
            _blurVisual = _compositor.CreateSpriteVisual();
            _blurVisual.Brush = _blurEffectBrush;
            _blurVisual.RelativeSizeAdjustment = System.Numerics.Vector2.One;

            ElementCompositionPreview.SetElementChildVisual(BlurHost, _blurVisual);

            // Start invisible — LoadBlurredBackgroundAsync fades in on first load.
            ElementCompositionPreview.GetElementVisual(BlurHost).Opacity = 0f;
        }

        private async Task LoadBlurredBackgroundAsync(IRandomAccessStreamReference? thumbnailRef)
        {
            if (thumbnailRef == null || _blurSurfaceBrush == null || _compositor == null) return;

            try
            {
                // Load the surface while old blur is still visible — no fade-out first
                using var stream = await thumbnailRef.OpenReadAsync();
                var surface = LoadedImageSurface.StartLoadFromStream(stream);

                // Wait for the surface to be loaded before swapping
                var tcs = new TaskCompletionSource<bool>();
                void OnLoaded(LoadedImageSurface s, LoadedImageSourceLoadCompletedEventArgs a)
                {
                    surface.LoadCompleted -= OnLoaded;
                    tcs.TrySetResult(a.Status == LoadedImageSourceLoadStatus.Success);
                }
                surface.LoadCompleted += OnLoaded;

                var timeoutTask = Task.Delay(2000);
                var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);

                // Swap surface and crossfade
                _blurSurfaceBrush.Surface = surface;

                var easing = _compositor.CreateCubicBezierEasingFunction(
                    new Vector2(0.25f, 0.1f), new Vector2(0.25f, 1.0f));

                Visual blurVisual = ElementCompositionPreview.GetElementVisual(BlurHost);

                // If blur was already visible, just hold it; if first time, fade in
                float currentOpacity = blurVisual.Opacity;
                if (currentOpacity < 0.1f)
                {
                    var fadeIn = _compositor.CreateScalarKeyFrameAnimation();
                    fadeIn.InsertKeyFrame(1f, 0.6f, easing);
                    fadeIn.Duration = TimeSpan.FromMilliseconds(500);
                    blurVisual.StartAnimation("Opacity", fadeIn);
                }
                // If already visible (≥0.1), the surface swap is seamless under the blur
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to load blur background surface: {ex.Message}");
            }
        }

        private void ClearBlurSurface()
        {
            if (_blurSurfaceBrush != null)
                _blurSurfaceBrush.Surface = null;
            if (_compositor != null)
                ElementCompositionPreview.GetElementVisual(BlurHost).Opacity = 0f;
        }

        private async void LoadSourceIcon(string sourceAppId)
        {
            try
            {
                var icon = await IconResolver.ResolveAsync(sourceAppId);
                SourceBadgeIcon.Source = icon;
                SourceBadge.Visibility = icon != null ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to load source icon: {ex.Message}");
            }
        }

        private void ApplyTextColors()
        {
            if (!IsLoaded) return;

            var whiteBrush = new SolidColorBrush(Microsoft.UI.Colors.White);
            var subBrush = new SolidColorBrush(_subColor);

            TitlePrimary.MarqueeForeground = whiteBrush;
            TitleSecondary.MarqueeForeground = whiteBrush;
            ArtistPrimary.MarqueeForeground = subBrush;
            ArtistSecondary.MarqueeForeground = subBrush;

            ElapsedTimeText.Foreground = subBrush;
            TotalTimeText.Foreground = subBrush;

            AlbumArtFallback.Foreground = subBrush;
        }

        private void ApplyTransportColors()
        {
            if (!IsLoaded) return;

            var whiteBrush = new SolidColorBrush(Microsoft.UI.Colors.White);
            var playPauseBg = new SolidColorBrush(Color.FromArgb(35, 255, 255, 255));

            BtnBack.Foreground = whiteBrush;
            BtnForward.Foreground = whiteBrush;
            BtnPlayPause.Foreground = whiteBrush;

            if (BtnBack.Content is FontIcon backIcon)
                backIcon.Foreground = whiteBrush;
            if (BtnForward.Content is FontIcon fwdIcon)
                fwdIcon.Foreground = whiteBrush;

            PlayPauseIcon.Foreground = whiteBrush;
            BtnPlayPause.Background = playPauseBg;
        }

        // --- Metadata transition system (2-slot, same pattern as ExpandedMediaView) ---

        private bool ApplyMetadataSnapshot(MetadataSnapshot snapshot, ContentTransitionDirection direction)
        {
            MetadataSnapshot current = _metadataSnapshots[_metadataTransition.ActiveSlotIndex];
            if (current == snapshot)
                return false;

            void ApplyToSlot(int incomingSlot)
            {
                WriteMetadataToSlot(incomingSlot, snapshot);
                _metadataSnapshots[incomingSlot] = snapshot;
            }

            if (direction == ContentTransitionDirection.None)
                _metadataTransition.Crossfade(ApplyToSlot);
            else
                _metadataTransition.Transition(direction, ApplyToSlot);

            return true;
        }

        private void WriteMetadataToSlot(int slot, MetadataSnapshot snapshot)
        {
            if (slot == 0)
            {
                TitlePrimary.Text = snapshot.Title;
                ArtistPrimary.Text = snapshot.Artist;
            }
            else
            {
                TitleSecondary.Text = snapshot.Title;
                ArtistSecondary.Text = snapshot.Artist;
            }
        }

        private MetadataSnapshot ReadMetadataSnapshotFromSlot(int slot)
        {
            return slot == 0
                ? new MetadataSnapshot(TitlePrimary.Text ?? string.Empty, ArtistPrimary.Text ?? string.Empty)
                : new MetadataSnapshot(TitleSecondary.Text ?? string.Empty, ArtistSecondary.Text ?? string.Empty);
        }

        private static string GetMetadataSubtitle(MediaSessionSnapshot session, bool showBusyTransportState)
        {
            if (showBusyTransportState)
                return Loc.GetString("Media/Switching");
            return session.Artist;
        }

        // --- Events ---

        private void BtnBack_Click(object sender, RoutedEventArgs e) => BackClick?.Invoke(this, EventArgs.Empty);
        private void BtnPlayPause_Click(object sender, RoutedEventArgs e) => PlayPauseClick?.Invoke(this, EventArgs.Empty);
        private void BtnForward_Click(object sender, RoutedEventArgs e) => ForwardClick?.Invoke(this, EventArgs.Empty);

        // --- Transport button press feedback (#19) ---

        private static void AnimateButtonScale(Button btn, double to, int durationMs)
        {
            if (btn.RenderTransform is not ScaleTransform st) return;
            var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            var animX = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                To = to,
                Duration = TimeSpan.FromMilliseconds(durationMs),
                EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
                {
                    EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut
                }
            };
            var animY = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                To = to,
                Duration = TimeSpan.FromMilliseconds(durationMs),
                EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
                {
                    EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut
                }
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animX, st);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animX, "ScaleX");
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animY, st);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animY, "ScaleY");
            storyboard.Children.Add(animX);
            storyboard.Children.Add(animY);
            storyboard.Begin();
        }

        private void TransportButton_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is Button btn) AnimateButtonScale(btn, 0.9, 90);
        }

        private void TransportButton_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is Button btn) AnimateButtonScale(btn, 1.0, 150);
        }

        private void TransportButton_PointerCanceled(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is Button btn) AnimateButtonScale(btn, 1.0, 150);
        }

        // --- Progress seek (#20) ---

        private void ProgressInteractionGrid_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is not Grid grid) return;
            grid.CapturePointer(e.Pointer);
            _isSeeking = true;
            UpdateSeekFromPointer(grid, e);
        }

        private void ProgressInteractionGrid_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (!_isSeeking) return;
            if (sender is not Grid grid) return;
            UpdateSeekFromPointer(grid, e);
        }

        private void ProgressInteractionGrid_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is not Grid grid) return;
            if (_isSeeking)
            {
                double ratio = ComputeSeekRatio(grid, e);
                _isSeeking = false;
                grid.ReleasePointerCapture(e.Pointer);

                // Re-anchor auto-advance at the seek target so the ticker resumes
                // from here instead of snapping back to the previous position on
                // the next GSMTC update.
                if (_progressHasTimeline && _progressDurationSeconds > 0)
                {
                    _progressAnchorPositionSeconds = ratio * _progressDurationSeconds;
                    _progressAnchorTime = DateTimeOffset.UtcNow;
                    ElapsedTimeText.Text = FormatTime(_progressAnchorPositionSeconds);
                    AnimateScaleTo(ratio, 160);
                }

                SeekRequested?.Invoke(this, ratio);
            }
            else
            {
                grid.ReleasePointerCapture(e.Pointer);
            }
        }

        private void ProgressInteractionGrid_PointerCanceled(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _isSeeking = false;
            if (sender is Grid grid) grid.ReleasePointerCapture(e.Pointer);
        }

        private void ProgressInteractionGrid_PointerCaptureLost(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _isSeeking = false;
        }

        private void UpdateSeekFromPointer(Grid grid, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            double ratio = ComputeSeekRatio(grid, e);
            // During active drag, animate quickly so the bar tracks the finger/cursor
            // without jumping jerkily when the pointer moves between polling events.
            AnimateScaleTo(ratio, 80);
        }

        private static double ComputeSeekRatio(Grid grid, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            double w = grid.ActualWidth;
            if (w <= 0) return 0;
            double x = e.GetCurrentPoint(grid).Position.X;
            return Math.Clamp(x / w, 0, 1);
        }

        // --- Snapshot types (shared with ExpandedMediaView) ---

        private readonly record struct MetadataSnapshot(string Title, string Artist);
    }
}
