using System;
using System.Collections.Generic;
using System.Numerics;
using System.Threading;
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
using wisland.Services.Media;

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

        // Album-art cache integration. The cache owns all decoded BitmapImage /
        // LoadedImageSurface / AlbumArtPalette triples keyed by ThumbnailHash.
        // Switching sessions becomes a synchronous hash lookup; no stream opens,
        // no BitmapImage decode, no per-switch LoadedImageSurface allocation.
        private MediaVisualCache? _visualCache;
        private string _currentArtHash = string.Empty;   // "" while no art or cache empty.
        private string _pendingArtHash = string.Empty;   // non-empty while we're awaiting AssetsReady for this hash.
        private ContentTransitionDirection _pendingArtDirection = ContentTransitionDirection.None;
        private ImageSource? _previousAlbumArtSource; // Holds the old art for crossfade outgoing slot
        private string? _lastSourceIconIdentity;
        private CancellationTokenSource? _sourceIconCts;
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

        // Composition blur for album art background.
        // We host TWO blur sprite visuals (active + outgoing) inside a single
        // container visual so a directional session-tab switch can render the
        // old and new blur as parallax layers crossfading in opposite
        // directions, instead of an instantaneous surface swap.
        private Compositor? _compositor;
        private ContainerVisual? _blurContainerVisual;
        private SpriteVisual? _blurVisual;            // Active layer (current art).
        private CompositionSurfaceBrush? _blurSurfaceBrush;
        private CompositionEffectBrush? _blurEffectBrush;
        private SpriteVisual? _outgoingBlurVisual;    // Outgoing layer (previous art) during directional swap.
        private CompositionSurfaceBrush? _outgoingBlurSurfaceBrush;
        private CompositionEffectBrush? _outgoingBlurEffectBrush;
        private LoadedImageSurface? _previousBlurSurface; // The surface currently displayed on _blurVisual (cached for transfer to outgoing on swap).
        // Tunable depth animation parameters. Drift is intentionally larger than
        // the album art slide (96px) felt at the foreground because the blur
        // panel covers the entire chrome — large drift on a heavily-blurred
        // visual still reads as subtle ambient motion, not a window-shake.
        private const int BlurSwapDurationMs = 520;
        private const float BlurDriftDistance = 32f;
        private const float BlurIncomingScaleStart = 1.06f;
        private const float BlurOutgoingScaleEnd = 1.08f;

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
            InitializeGradientParallax();
            ApplyTextColors();
            ApplyTransportColors();
            InitializeHeaderChipAtLoad();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            StopProgressTicker();
            StopSessionSwitchAnimation();
            StopScaleAnimation();

            // Cancel any in-flight async work so it doesn't touch disposed UI state.
            _sourceIconCts?.Cancel();
            _sourceIconCts?.Dispose();
            _sourceIconCts = null;

            if (_visualCache != null)
            {
                _visualCache.AssetsReady -= OnVisualCacheAssetsReady;
                _visualCache = null;
            }

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
            _outgoingBlurVisual?.Dispose();
            _outgoingBlurVisual = null;
            _outgoingBlurEffectBrush?.Dispose();
            _outgoingBlurEffectBrush = null;
            _outgoingBlurSurfaceBrush?.Dispose();
            _outgoingBlurSurfaceBrush = null;
            _blurContainerVisual?.Dispose();
            _blurContainerVisual = null;
            _previousBlurSurface = null;
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

            // Album art — route through the visual cache. Cache hit = synchronous
            // swap with no stream open / no decode; cache miss = retain current
            // visuals and wait for AssetsReady to fire for this hash. A switch
            // from one cached session to another is therefore a single frame-
            // atomic update with zero reload flicker.
            if (session.HasValue && !showBusyTransportState)
            {
                ApplyAlbumArtFromCache(session.Value, direction);
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

        private static MediaTrackFingerprint BuildAlbumArtFingerprint(MediaSessionSnapshot session)
        {
            // Kept only because MediaTrackFingerprint is still referenced by the
            // outer SyncMediaUI frame-diff path. Album-art routing no longer uses
            // a fingerprint — the cache keys on session.ThumbnailHash directly.
            string hash = session.Thumbnail != null
                ? (session.ThumbnailHash ?? string.Empty)
                : string.Empty;
            return new MediaTrackFingerprint(
                session.SessionKey ?? string.Empty,
                session.Title ?? string.Empty,
                session.Artist ?? string.Empty,
                hash);
        }

        /// <summary>
        /// Attaches the shared <see cref="MediaVisualCache"/>. Must be called once
        /// by the host (MainWindow) before any session updates flow through
        /// <see cref="UpdateMedia(MediaPresentationFrame)"/>. Safe to re-attach
        /// (detaches the previous subscription first).
        /// </summary>
        internal void SetVisualCache(MediaVisualCache? cache)
        {
            if (ReferenceEquals(_visualCache, cache)) return;
            if (_visualCache != null)
            {
                _visualCache.AssetsReady -= OnVisualCacheAssetsReady;
            }
            _visualCache = cache;
            if (_visualCache != null)
            {
                _visualCache.AssetsReady += OnVisualCacheAssetsReady;
            }
        }

        private void OnVisualCacheAssetsReady(string hash)
        {
            // Cache hands this back on the UI dispatcher already; still check we
            // actually wanted this hash (user may have scrolled past it).
            if (!string.Equals(_pendingArtHash, hash, StringComparison.Ordinal)) return;
            if (_visualCache is null) return;
            if (!_visualCache.TryGet(hash, out MediaVisualAssets? assets) || assets is null) return;
            _pendingArtHash = string.Empty;
            ApplyAlbumArtAssets(assets, _pendingArtDirection);
        }

        /// <summary>
        /// Routes a session's album art through the cache. Synchronously applies
        /// on cache hit; otherwise registers the hash as pending and returns —
        /// the current visuals remain in place until <see cref="OnVisualCacheAssetsReady"/>
        /// fires for this hash.
        /// <para>
        /// The <paramref name="direction"/> parameter is plumbed through to the
        /// art-swap pipeline so a future dual-slot directional coordinator can
        /// slide the whole art+blur+gradient stack left/right. The current
        /// implementation uses the existing crossfade; no behavior change for now.
        /// </para>
        /// </summary>
        private void ApplyAlbumArtFromCache(MediaSessionSnapshot session, ContentTransitionDirection direction)
        {
            string hash = session.ThumbnailHash ?? string.Empty;

            if (session.Thumbnail is null || string.IsNullOrEmpty(hash))
            {
                // No resolvable art reference yet. Keep current visuals unless we
                // had none — in which case fall back to the default palette.
                if (!_hasAlbumArt) ClearAlbumArt();
                _pendingArtHash = string.Empty;
                return;
            }

            // Already displaying this exact art payload — no-op.
            if (string.Equals(_currentArtHash, hash, StringComparison.Ordinal))
            {
                _pendingArtHash = string.Empty;
                return;
            }

            if (_visualCache != null
                && _visualCache.TryGet(hash, out MediaVisualAssets? assets)
                && assets is not null)
            {
                _pendingArtHash = string.Empty;
                ApplyAlbumArtAssets(assets, direction);
                return;
            }

            // Miss: retain current visuals; remember the hash we're waiting for.
            // A subsequent AssetsReady(hash) will apply it.
            _pendingArtHash = hash;
            _pendingArtDirection = direction;
        }

        private void ApplyAlbumArtAssets(MediaVisualAssets assets, ContentTransitionDirection direction)
        {
            if (_isBusyTransport) return;

            // Capture the currently-displayed image source for the crossfade's
            // outgoing slot. Do this before swapping the primary source.
            _previousAlbumArtSource = AlbumArtImage.Source;

            // Synchronous swap — bitmap is already decoded, surface already on GPU.
            AlbumArtImage.Source = assets.Bitmap;

            _currentArtHash = assets.Hash;
            _hasAlbumArt = true;
            AlbumArtFallback.Visibility = Visibility.Collapsed;

            // Wrap all three layers' StartAnimation calls in a single scoped
            // batch so the compositor commits them as one unit. The UI thread
            // is single-threaded so they would already commit on the same tick
            // in practice, but the explicit batch (a) documents the atomic
            // intent and (b) gives us one Completed event for the entire
            // session-tab transition if we need it later (cleanup, "settled"
            // notifications, etc.).
            //
            // Layer order in code matches paint order from front (album art) to
            // back (gradient) so a debugger paused mid-call shows the visual
            // stack top-down.
            CompositionScopedBatch? batch = null;
            if (_compositor != null && direction != ContentTransitionDirection.None)
            {
                batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            }

            try
            {
                if (direction == ContentTransitionDirection.None)
                {
                    CrossfadeAlbumArtLayers();
                }
                else
                {
                    SlideAlbumArtLayers(direction);
                }
                SwapBlurSurface(assets.BlurSurface, direction);
                CrossfadeBackgroundPalette(assets.Palette, direction);
                EnsureBlurLayerVisible();
            }
            finally
            {
                batch?.End();
            }
        }

        private void SlideAlbumArtLayers(ContentTransitionDirection direction)
        {
            if (_compositor == null)
            {
                // Non-composition fallback: just snap.
                AlbumArtImageOutgoing.Source = _previousAlbumArtSource;
                AlbumArtImage.Opacity = 1;
                AlbumArtImageOutgoing.Opacity = 0;
                return;
            }

            // Sign: Forward (wheel-down / next tab) → new comes from right, old exits left.
            float sign = direction == ContentTransitionDirection.Forward ? 1f : -1f;
            const float slideDistance = 96f; // Slightly > art width (80) so it fully clears the rounded-corner clip.
            const int durationMs = 360;

            Visual outgoingVisual = ElementCompositionPreview.GetElementVisual(AlbumArtImageOutgoing);
            Visual incomingVisual = ElementCompositionPreview.GetElementVisual(AlbumArtImage);

            AlbumArtImageOutgoing.Source = _previousAlbumArtSource;
            outgoingVisual.Opacity = _previousAlbumArtSource != null ? 1f : 0f;
            outgoingVisual.Offset = Vector3.Zero;
            // Anchor scale to the image center so the zoom breathes around the
            // art's middle rather than collapsing toward the top-left.
            outgoingVisual.CenterPoint = new Vector3(
                (float)(AlbumArtImageOutgoing.ActualWidth * 0.5),
                (float)(AlbumArtImageOutgoing.ActualHeight * 0.5),
                0f);
            outgoingVisual.Scale = Vector3.One;

            incomingVisual.Opacity = 1f;
            incomingVisual.Offset = new Vector3(slideDistance * sign, 0f, 0f);
            incomingVisual.CenterPoint = new Vector3(
                (float)(AlbumArtImage.ActualWidth * 0.5),
                (float)(AlbumArtImage.ActualHeight * 0.5),
                0f);
            incomingVisual.Scale = new Vector3(0.96f, 0.96f, 1f);

            var enterEasing = _compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.18f, 1.0f), new Vector2(0.32f, 1.0f));
            var exitEasing = _compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.42f, 0.0f), new Vector2(0.92f, 0.52f));

            var outOffset = _compositor.CreateVector3KeyFrameAnimation();
            outOffset.InsertKeyFrame(1f, new Vector3(-slideDistance * sign, 0f, 0f), exitEasing);
            outOffset.Duration = TimeSpan.FromMilliseconds(durationMs);

            var outOpacity = _compositor.CreateScalarKeyFrameAnimation();
            outOpacity.InsertKeyFrame(0.55f, 1f);
            outOpacity.InsertKeyFrame(1f, 0f, exitEasing);
            outOpacity.Duration = TimeSpan.FromMilliseconds(durationMs);

            // Outgoing shrinks slightly as it leaves — reads as "stepping back".
            var outScale = _compositor.CreateVector3KeyFrameAnimation();
            outScale.InsertKeyFrame(1f, new Vector3(0.94f, 0.94f, 1f), exitEasing);
            outScale.Duration = TimeSpan.FromMilliseconds(durationMs);

            var inOffset = _compositor.CreateVector3KeyFrameAnimation();
            inOffset.InsertKeyFrame(1f, Vector3.Zero, enterEasing);
            inOffset.Duration = TimeSpan.FromMilliseconds(durationMs);

            // Incoming eases out from 0.96 to 1.0 — subtle "settle" bounce.
            var inScale = _compositor.CreateVector3KeyFrameAnimation();
            inScale.InsertKeyFrame(1f, Vector3.One, enterEasing);
            inScale.Duration = TimeSpan.FromMilliseconds(durationMs);

            outgoingVisual.StartAnimation("Offset", outOffset);
            outgoingVisual.StartAnimation("Opacity", outOpacity);
            outgoingVisual.StartAnimation("Scale", outScale);
            incomingVisual.StartAnimation("Offset", inOffset);
            incomingVisual.StartAnimation("Scale", inScale);

            // Reset incoming offset storage to zero after animation completes so
            // the next apply starts from a clean Vector3.Zero snapshot. The Offset
            // animation lands on (0,0,0) anyway; explicit reset is defensive.
        }

        /// <summary>
        /// Swaps the blurred-album-art background surface. For directional
        /// (session-tab) switches, runs a dual-layer parallax crossfade where
        /// the previous blur drifts off (opposite to slide direction) while
        /// fading + slowly zooming, and the new blur drifts in from the slide
        /// direction with a 1.06→1.0 settle. For non-directional updates
        /// (first load / refresh), performs an instant surface swap on the
        /// active visual.
        /// </summary>
        private void SwapBlurSurface(LoadedImageSurface? newSurface, ContentTransitionDirection direction)
        {
            if (_blurSurfaceBrush is null) return;

            // Non-directional path: instant swap on the active layer. Caller
            // will fade BlurHost in if it was hidden via EnsureBlurLayerVisible.
            if (direction == ContentTransitionDirection.None
                || _compositor is null
                || _outgoingBlurVisual is null
                || _outgoingBlurSurfaceBrush is null
                || _blurVisual is null)
            {
                _blurSurfaceBrush.Surface = newSurface;
                _previousBlurSurface = newSurface;
                return;
            }

            // Directional path: hand the current surface to the outgoing visual
            // and load the new one onto the active visual, then animate both.
            // If we have no prior surface, the outgoing layer simply stays at 0
            // opacity — no spurious cross-fade from black.
            bool hasOutgoing = _previousBlurSurface != null;
            if (hasOutgoing)
            {
                _outgoingBlurSurfaceBrush.Surface = _previousBlurSurface;
            }
            _blurSurfaceBrush.Surface = newSurface;
            _previousBlurSurface = newSurface;

            float sign = direction == ContentTransitionDirection.Forward ? 1f : -1f;
            int duration = BlurSwapDurationMs;

            // Easings: incoming uses a "settling" curve (fast then ease into rest);
            // outgoing uses a "depart" curve (slow start, accelerating into nothing)
            // so the rear layer feels like it falls behind into space.
            var enterEasing = _compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.16f, 0.84f), new Vector2(0.30f, 1.0f));
            var exitEasing = _compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.40f, 0.0f), new Vector2(0.72f, 0.40f));

            // Center-anchor the scale so it breathes around the viewport mid-axis
            // rather than collapsing toward a corner.
            Vector3 anchor = new(
                (float)(BlurHost.ActualWidth * 0.5),
                (float)(BlurHost.ActualHeight * 0.5),
                0f);
            _outgoingBlurVisual.CenterPoint = anchor;
            _blurVisual.CenterPoint = anchor;

            // Outgoing: starts at rest, drifts AGAINST the slide direction (so it
            // feels like falling backward), zooms slowly outward, fades to 0.
            _outgoingBlurVisual.Offset = Vector3.Zero;
            _outgoingBlurVisual.Scale = Vector3.One;
            _outgoingBlurVisual.Opacity = hasOutgoing ? 1f : 0f;

            if (hasOutgoing)
            {
                var outOffset = _compositor.CreateVector3KeyFrameAnimation();
                outOffset.InsertKeyFrame(1f, new Vector3(-BlurDriftDistance * sign, 0f, 0f), exitEasing);
                outOffset.Duration = TimeSpan.FromMilliseconds(duration);

                var outScale = _compositor.CreateVector3KeyFrameAnimation();
                outScale.InsertKeyFrame(1f, new Vector3(BlurOutgoingScaleEnd, BlurOutgoingScaleEnd, 1f), exitEasing);
                outScale.Duration = TimeSpan.FromMilliseconds(duration);

                var outOpacity = _compositor.CreateScalarKeyFrameAnimation();
                outOpacity.InsertKeyFrame(0f, 1f);
                outOpacity.InsertKeyFrame(1f, 0f, exitEasing);
                outOpacity.Duration = TimeSpan.FromMilliseconds(duration);

                _outgoingBlurVisual.StartAnimation("Offset", outOffset);
                _outgoingBlurVisual.StartAnimation("Scale", outScale);
                _outgoingBlurVisual.StartAnimation("Opacity", outOpacity);
            }

            // Incoming: starts offset in the slide direction with a 1.06 zoom
            // and full opacity (it's already painted with the new surface, so it
            // can be visible from frame 0 to mask the outgoing's fade if any).
            // We still ramp opacity from 0→1 so the *combined* layer brightness
            // stays balanced during the swap (avoids momentary 2× luminance).
            _blurVisual.Offset = new Vector3(BlurDriftDistance * sign, 0f, 0f);
            _blurVisual.Scale = new Vector3(BlurIncomingScaleStart, BlurIncomingScaleStart, 1f);
            _blurVisual.Opacity = hasOutgoing ? 0f : 1f;

            var inOffset = _compositor.CreateVector3KeyFrameAnimation();
            inOffset.InsertKeyFrame(1f, Vector3.Zero, enterEasing);
            inOffset.Duration = TimeSpan.FromMilliseconds(duration);

            var inScale = _compositor.CreateVector3KeyFrameAnimation();
            inScale.InsertKeyFrame(1f, Vector3.One, enterEasing);
            inScale.Duration = TimeSpan.FromMilliseconds(duration);

            _blurVisual.StartAnimation("Offset", inOffset);
            _blurVisual.StartAnimation("Scale", inScale);

            if (hasOutgoing)
            {
                var inOpacity = _compositor.CreateScalarKeyFrameAnimation();
                inOpacity.InsertKeyFrame(0f, 0f);
                inOpacity.InsertKeyFrame(1f, 1f, enterEasing);
                inOpacity.Duration = TimeSpan.FromMilliseconds(duration);
                _blurVisual.StartAnimation("Opacity", inOpacity);
            }
        }

        private void CrossfadeAlbumArtLayers()
        {
            if (_compositor != null)
            {
                var easing = _compositor.CreateCubicBezierEasingFunction(
                    new Vector2(0.25f, 0.1f), new Vector2(0.25f, 1.0f));

                Visual outgoingVisual = ElementCompositionPreview.GetElementVisual(AlbumArtImageOutgoing);
                Visual incomingVisual = ElementCompositionPreview.GetElementVisual(AlbumArtImage);

                AlbumArtImageOutgoing.Source = _previousAlbumArtSource;
                outgoingVisual.Opacity = _previousAlbumArtSource != null ? 1f : 0f;
                incomingVisual.Opacity = 0f;

                var fadeIn = _compositor.CreateScalarKeyFrameAnimation();
                fadeIn.InsertKeyFrame(0f, 0f);
                fadeIn.InsertKeyFrame(1f, 1f, easing);
                fadeIn.Duration = TimeSpan.FromMilliseconds(ArtCrossfadeDurationMs);

                var fadeOut = _compositor.CreateScalarKeyFrameAnimation();
                fadeOut.InsertKeyFrame(0f, outgoingVisual.Opacity);
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

        private void EnsureBlurLayerVisible()
        {
            if (_compositor == null) return;
            Visual blurVisual = ElementCompositionPreview.GetElementVisual(BlurHost);
            if (blurVisual.Opacity >= 0.1f) return; // Already visible — instant surface swap suffices.

            var easing = _compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.25f, 0.1f), new Vector2(0.25f, 1.0f));
            var fadeIn = _compositor.CreateScalarKeyFrameAnimation();
            fadeIn.InsertKeyFrame(1f, 0.6f, easing);
            fadeIn.Duration = TimeSpan.FromMilliseconds(500);
            blurVisual.StartAnimation("Opacity", fadeIn);
        }

        /// <summary>
        /// Crossfades the gradient background from current colors to new palette colors.
        /// Uses composition-level opacity to avoid single-frame flashes.
        /// </summary>
        private void CrossfadeBackgroundPalette(AlbumArtPalette palette, ContentTransitionDirection direction = ContentTransitionDirection.None)
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

                // Directional parallax on the gradient layer. We pre-inflate the
                // visuals by a baseline scale (set on Loaded) so the small
                // horizontal drift never exposes uncovered strips at the edges.
                if (direction != ContentTransitionDirection.None)
                {
                    float sign = direction == ContentTransitionDirection.Forward ? 1f : -1f;
                    const float gradientDrift = 24f; // Subtler than blur (32px) — gradient is a softer plane behind the blur layer.
                    int duration = GradientCrossfadeDurationMs;

                    var enterEasing = _compositor.CreateCubicBezierEasingFunction(
                        new Vector2(0.16f, 0.84f), new Vector2(0.30f, 1.0f));
                    var exitEasing = _compositor.CreateCubicBezierEasingFunction(
                        new Vector2(0.40f, 0.0f), new Vector2(0.72f, 0.40f));

                    Vector3 anchor = new(
                        (float)(GradientBackground.ActualWidth * 0.5),
                        (float)(GradientBackground.ActualHeight * 0.5),
                        0f);
                    activeVisual.CenterPoint = anchor;
                    outgoingVisual.CenterPoint = anchor;

                    activeVisual.Offset = new Vector3(gradientDrift * sign, 0f, 0f);
                    var inOffset = _compositor.CreateVector3KeyFrameAnimation();
                    inOffset.InsertKeyFrame(1f, Vector3.Zero, enterEasing);
                    inOffset.Duration = TimeSpan.FromMilliseconds(duration);
                    activeVisual.StartAnimation("Offset", inOffset);

                    outgoingVisual.Offset = Vector3.Zero;
                    var outOffset = _compositor.CreateVector3KeyFrameAnimation();
                    outOffset.InsertKeyFrame(1f, new Vector3(-gradientDrift * sign, 0f, 0f), exitEasing);
                    outOffset.Duration = TimeSpan.FromMilliseconds(duration);
                    outgoingVisual.StartAnimation("Offset", outOffset);
                }
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
            _currentArtHash = string.Empty;
            _pendingArtHash = string.Empty;
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

            // Container that owns both the active and outgoing blur sprite visuals.
            // SetElementChildVisual only allows ONE child visual per host element,
            // so the dual-layer crossfade lives inside this container.
            _blurContainerVisual = _compositor.CreateContainerVisual();
            _blurContainerVisual.RelativeSizeAdjustment = System.Numerics.Vector2.One;

            // === Outgoing (rear) layer ===
            // Sits BEHIND the active layer so the active fade-in masks any
            // sub-pixel seam at the moment of swap.
            _outgoingBlurSurfaceBrush = _compositor.CreateSurfaceBrush();
            _outgoingBlurSurfaceBrush.Stretch = CompositionStretch.UniformToFill;
            IGraphicsEffect outBlur = new GaussianBlurEffect
            {
                Name = "BlurOut",
                BlurAmount = 40f,
                BorderMode = EffectBorderMode.Hard,
                Source = new CompositionEffectSourceParameter("source")
            };
            CompositionEffectFactory outFactory = _compositor.CreateEffectFactory(outBlur);
            _outgoingBlurEffectBrush = outFactory.CreateBrush();
            _outgoingBlurEffectBrush.SetSourceParameter("source", _outgoingBlurSurfaceBrush);

            _outgoingBlurVisual = _compositor.CreateSpriteVisual();
            _outgoingBlurVisual.Brush = _outgoingBlurEffectBrush;
            _outgoingBlurVisual.RelativeSizeAdjustment = System.Numerics.Vector2.One;
            _outgoingBlurVisual.Opacity = 0f; // Hidden until a directional swap arms it.

            // === Active (front) layer ===
            _blurSurfaceBrush = _compositor.CreateSurfaceBrush();
            _blurSurfaceBrush.Stretch = CompositionStretch.UniformToFill;
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

            _blurVisual = _compositor.CreateSpriteVisual();
            _blurVisual.Brush = _blurEffectBrush;
            _blurVisual.RelativeSizeAdjustment = System.Numerics.Vector2.One;

            _blurContainerVisual.Children.InsertAtBottom(_outgoingBlurVisual);
            _blurContainerVisual.Children.InsertAtTop(_blurVisual);

            ElementCompositionPreview.SetElementChildVisual(BlurHost, _blurContainerVisual);

            // Start invisible — EnsureBlurLayerVisible fades it in on first art.
            ElementCompositionPreview.GetElementVisual(BlurHost).Opacity = 0f;
        }

        /// <summary>
        /// Inflates the gradient background visuals slightly so the directional
        /// parallax drift during a session-tab switch never exposes uncovered
        /// strips at the edge. The borders fully cover the parent grid; we
        /// scale them up at composition level (no XAML layout impact) so the
        /// pre-blur color plane stays edge-to-edge even at peak ±24px drift.
        /// </summary>
        private void InitializeGradientParallax()
        {
            if (_compositor is null) return;

            const float gradientBaselineScale = 1.10f;

            void Apply(Visual v, FrameworkElement el)
            {
                v.CenterPoint = new Vector3(
                    (float)(el.ActualWidth * 0.5),
                    (float)(el.ActualHeight * 0.5),
                    0f);
                v.Scale = new Vector3(gradientBaselineScale, gradientBaselineScale, 1f);
            }

            Visual activeVisual = ElementCompositionPreview.GetElementVisual(GradientBackground);
            Visual outgoingVisual = ElementCompositionPreview.GetElementVisual(GradientBackgroundOutgoing);
            Apply(activeVisual, GradientBackground);
            Apply(outgoingVisual, GradientBackgroundOutgoing);

            GradientBackground.SizeChanged += (_, _) =>
                Apply(ElementCompositionPreview.GetElementVisual(GradientBackground), GradientBackground);
            GradientBackgroundOutgoing.SizeChanged += (_, _) =>
                Apply(ElementCompositionPreview.GetElementVisual(GradientBackgroundOutgoing), GradientBackgroundOutgoing);
        }

        private void ClearBlurSurface()
        {
            if (_blurSurfaceBrush != null)
                _blurSurfaceBrush.Surface = null;
            if (_outgoingBlurSurfaceBrush != null)
                _outgoingBlurSurfaceBrush.Surface = null;
            if (_outgoingBlurVisual != null)
                _outgoingBlurVisual.Opacity = 0f;
            _previousBlurSurface = null;
            if (_compositor != null)
                ElementCompositionPreview.GetElementVisual(BlurHost).Opacity = 0f;
        }

        private async void LoadSourceIcon(string sourceAppId)
        {
            // Cancel any prior in-flight icon resolve so a rapid session switch
            // cannot land an older icon after a newer one.
            _sourceIconCts?.Cancel();
            _sourceIconCts?.Dispose();
            var cts = new CancellationTokenSource();
            _sourceIconCts = cts;
            CancellationToken ct = cts.Token;

            try
            {
                var icon = await IconResolver.ResolveAsync(sourceAppId);
                if (ct.IsCancellationRequested) return;
                SourceBadgeIcon.Source = icon;
                SourceBadge.Visibility = icon != null ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (OperationCanceledException)
            {
                // Superseded by a newer resolve — expected.
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
