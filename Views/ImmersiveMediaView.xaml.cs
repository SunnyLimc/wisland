using System;
using System.Collections.Generic;
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
using Windows.UI;
using wisland.Controls;
using wisland.Helpers;
using wisland.Models;

namespace wisland.Views
{
    public sealed partial class ImmersiveMediaView : UserControl
    {
        private static MediaSourceIconResolver IconResolver => MediaSourceIconResolver.Shared;

        private readonly DirectionalContentTransitionCoordinator _metadataTransition;
        private readonly MetadataSnapshot[] _metadataSnapshots = new MetadataSnapshot[2];

        private Color _mainColor = Microsoft.UI.Colors.White;
        private Color _subColor = Color.FromArgb(255, 180, 180, 190);
        private Color _iconColor = Microsoft.UI.Colors.White;
        private bool _canOpenSessionPicker;
        private bool _isSessionPickerExpanded;

        private string? _lastAlbumArtIdentity;
        private string? _lastSourceIconIdentity;
        private string? _lastHeaderLabel;
        private double _lastProgress;
        private double _lastDurationSeconds;
        private double _progressTrackWidth;

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

            // Album art
            if (session.HasValue)
            {
                string albumArtIdentity = $"{session.Value.SessionKey}:{session.Value.Title}:{session.Value.Artist}";
                if (!string.Equals(_lastAlbumArtIdentity, albumArtIdentity, StringComparison.Ordinal))
                {
                    _lastAlbumArtIdentity = albumArtIdentity;
                    LoadAlbumArt(session.Value);
                }
            }
            else
            {
                ClearAlbumArt();
            }

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
            try
            {
                BitmapImage? albumArt = await AlbumArtColorExtractor.LoadThumbnailAsync(session.Thumbnail);
                if (albumArt != null)
                {
                    AlbumArtImage.Source = albumArt;
                    AlbumArtFallback.Visibility = Visibility.Collapsed;

                    // Extract colors for gradient background
                    string colorKey = $"{session.SessionKey}:{session.Title}:{session.Artist}";
                    AlbumArtPalette? palette = await AlbumArtColorExtractor.ExtractAsync(session.Thumbnail, colorKey);
                    if (palette.HasValue)
                    {
                        ApplyBackgroundPalette(palette.Value);
                    }

                    // Load blurred background via composition
                    await LoadBlurredBackgroundAsync(session.Thumbnail);
                }
                else
                {
                    ClearAlbumArt();
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Failed to load album art: {ex.Message}");
                ClearAlbumArt();
            }
        }

        private void ClearAlbumArt()
        {
            AlbumArtImage.Source = null;
            AlbumArtFallback.Visibility = Visibility.Visible;
            ClearBlurSurface();
            _lastAlbumArtIdentity = null;
            ApplyBackgroundPalette(AlbumArtPalette.Default);
        }

        private void ApplyBackgroundPalette(AlbumArtPalette palette)
        {
            // Always darken gradient colors aggressively so background is reliably dark
            GradientStop0.Color = ClampBrightness(palette.Dominant, maxLuminance: 60);
            GradientStop1.Color = ClampBrightness(palette.Secondary, maxLuminance: 40);
            GradientStop2.Color = ClampBrightness(palette.Average, maxLuminance: 50);

            // Text/icons are always white/light — no accent derivation.
            // Only the progress fill gets a subtle accent tint from the album art.
            _mainColor = Microsoft.UI.Colors.White;
            _subColor = Color.FromArgb(210, 200, 200, 215);
            _iconColor = Microsoft.UI.Colors.White;
            ProgressFillBrush.Color = EnsureBright(palette.Dominant);

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

        private async Task LoadBlurredBackgroundAsync(Windows.Storage.Streams.IRandomAccessStreamReference? thumbnailRef)
        {
            if (thumbnailRef == null || _blurSurfaceBrush == null) return;

            try
            {
                using var stream = await thumbnailRef.OpenReadAsync();
                var surface = LoadedImageSurface.StartLoadFromStream(stream);
                _blurSurfaceBrush.Surface = surface;
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
