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

        private Color _mainColor = Microsoft.UI.Colors.White;
        private Color _subColor = Color.FromArgb(255, 180, 180, 190);
        private Color _iconColor = Microsoft.UI.Colors.White;
        private bool _canOpenSessionPicker;
        private bool _isSessionPickerExpanded;

        private IRandomAccessStreamReference? _lastThumbnailRef;
        private ImageSource? _previousAlbumArtSource; // Holds the old art for crossfade outgoing slot
        private string? _lastSourceIconIdentity;
        private string? _lastHeaderLabel;
        private double _lastProgress;
        private double _lastDurationSeconds;
        private double _progressTrackWidth;
        private bool _isBusyTransport; // True during transport switching grace period
        private bool _hasAlbumArt;     // True when album art is currently displayed

        // Composition blur for album art background
        private Compositor? _compositor;
        private SpriteVisual? _blurVisual;
        private CompositionSurfaceBrush? _blurSurfaceBrush;
        private CompositionEffectBrush? _blurEffectBrush;

        public event EventHandler? BackClick;
        public event EventHandler? PlayPauseClick;
        public event EventHandler? ForwardClick;
        public event EventHandler? SessionPickerToggleRequested;

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

            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            InitializeBlurVisual();
            ApplyTextColors();
            ApplyTransportColors();
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
            HeaderExpandGlyph.Visibility = _canOpenSessionPicker ? Visibility.Visible : Visibility.Collapsed;

            // Header label
            string headerLabel = session.HasValue
                ? MediaSourceAppResolver.TryResolveDisplayName(session.Value.SourceAppId) ?? session.Value.SourceName
                : Loc.GetString("AppName");
            if (!string.Equals(_lastHeaderLabel, headerLabel, StringComparison.Ordinal))
            {
                HeaderLabel.Text = headerLabel;
                _lastHeaderLabel = headerLabel;
            }

            // Source icon in header + badge
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

            // Album art — reload when the thumbnail reference changes,
            // but skip updates while switching to keep old visuals stable
            if (session.HasValue && !showBusyTransportState)
            {
                var thumbnailRef = session.Value.Thumbnail;
                if (!ReferenceEquals(_lastThumbnailRef, thumbnailRef))
                {
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
            HeaderExpandGlyph.Visibility = Visibility.Collapsed;
            HeaderLabel.Text = header;
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

            // Rotate the expand glyph
            float targetAngle = isExpanded ? 180f : 0f;
            if (HeaderExpandGlyph.RenderTransform is RotateTransform rotate)
            {
                rotate.Angle = targetAngle;
            }
            else
            {
                HeaderExpandGlyph.RenderTransform = new RotateTransform { Angle = targetAngle };
                HeaderExpandGlyph.RenderTransformOrigin = new Point(0.5, 0.5);
            }
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
            if (!session.HasValue || !session.Value.HasTimeline || session.Value.DurationSeconds <= 0)
            {
                ElapsedTimeText.Text = "";
                TotalTimeText.Text = "";
                ProgressFill.Width = 0;
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

            double progress = session.Value.Progress;
            double duration = session.Value.DurationSeconds;
            _lastProgress = progress;
            _lastDurationSeconds = duration;

            double elapsed = progress * duration;
            ElapsedTimeText.Text = FormatTime(elapsed);
            TotalTimeText.Text = FormatTime(duration);

            // Update progress fill width
            _progressTrackWidth = ProgressTrack.ActualWidth;
            if (_progressTrackWidth > 0)
            {
                ProgressFill.Width = Math.Clamp(progress * _progressTrackWidth, 0, _progressTrackWidth);
            }
        }

        private static string FormatTime(double totalSeconds)
        {
            if (double.IsNaN(totalSeconds) || totalSeconds < 0) totalSeconds = 0;
            int minutes = (int)(totalSeconds / 60);
            int seconds = (int)(totalSeconds % 60);
            return $"{minutes}:{seconds:D2}";
        }

        private async void LoadAlbumArt(MediaSessionSnapshot session)
        {
            // Capture the reference we're loading for — if it changes mid-await, discard results
            var targetRef = session.Thumbnail;

            try
            {
                // --- Phase 1: Load everything in the background ---
                BitmapImage? albumArt = await AlbumArtColorExtractor.LoadThumbnailAsync(targetRef);
                if (!ReferenceEquals(_lastThumbnailRef, targetRef)) return; // stale

                if (albumArt == null)
                {
                    if (!_hasAlbumArt) ClearAlbumArt();
                    return;
                }

                // Start color extraction in parallel while we wait for image realization
                string colorKey = $"{session.SessionKey}:{session.Title}:{session.Artist}";
                Task<AlbumArtPalette?> paletteTask = AlbumArtColorExtractor.ExtractAsync(targetRef, colorKey);

                // --- Phase 2: Pre-realize the image before crossfading ---
                // Save the current art source before it gets replaced
                _previousAlbumArtSource = AlbumArtImage.Source;

                // Set the new art on the incoming (hidden) image and wait for ImageOpened
                // so the GPU texture is ready before we start the crossfade animation.
                bool imageReady = await PreRealizeAlbumArtAsync(albumArt);
                if (!ReferenceEquals(_lastThumbnailRef, targetRef)) return; // stale
                if (!imageReady) return; // failed to realize, keep old art

                // Wait for palette (likely already done)
                AlbumArtPalette? palette = await paletteTask;
                if (!ReferenceEquals(_lastThumbnailRef, targetRef)) return; // stale

                // --- Phase 3: Perform all crossfades atomically ---
                // The incoming image already has the art set from PreRealize.
                // Now swap outgoing ← old primary, and animate.
                CrossfadeAlbumArtFromPreRealized();
                _hasAlbumArt = true;
                AlbumArtFallback.Visibility = Visibility.Collapsed;

                if (palette.HasValue)
                {
                    CrossfadeBackgroundPalette(palette.Value);
                }

                // Load blurred background via composition
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
                // Timed out but image might still render — proceed anyway
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

            _mainColor = Microsoft.UI.Colors.White;
            _subColor = Color.FromArgb(210, 200, 200, 215);
            _iconColor = Microsoft.UI.Colors.White;
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
            return Color.FromArgb(255,
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
        }

        private async void LoadSourceIcon(string sourceAppId)
        {
            try
            {
                var icon = await IconResolver.ResolveAsync(sourceAppId);
                HeaderIcon.Source = icon;
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

            HeaderLabel.Foreground = whiteBrush;
            HeaderExpandGlyph.Foreground = subBrush;

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

        private void OnSessionHeader_Click(object sender, RoutedEventArgs e)
        {
            if (_canOpenSessionPicker)
                SessionPickerToggleRequested?.Invoke(this, EventArgs.Empty);
        }

        // --- Snapshot types (shared with ExpandedMediaView) ---

        private readonly record struct MetadataSnapshot(string Title, string Artist);
    }
}
