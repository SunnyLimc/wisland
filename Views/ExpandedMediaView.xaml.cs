using Microsoft.UI.Composition;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI;
using System;
using System.Numerics;
using Windows.Foundation;
using wisland.Models;

namespace wisland.Views
{
    public sealed partial class ExpandedMediaView : UserControl
    {
        private readonly RectangleGeometry _metadataClip = new();
        private readonly MetadataSnapshot[] _slotSnapshots = new MetadataSnapshot[2];
        private Compositor? _compositor;
        private Visual? _primaryVisual;
        private Visual? _secondaryVisual;
        private InsetClip? _primaryClip;
        private InsetClip? _secondaryClip;
        private CubicBezierEasingFunction? _enterEasing;
        private CubicBezierEasingFunction? _exitEasing;
        private int _activeSlotIndex;
        private long _transitionToken;
        private Color _mainColor = Microsoft.UI.Colors.White;
        private Color _subColor = Microsoft.UI.Colors.LightGray;
        public event EventHandler? BackClick;
        public event EventHandler? PlayPauseClick;
        public event EventHandler? ForwardClick;

        public ExpandedMediaView()
        {
            this.InitializeComponent();
            MetadataViewport.Clip = _metadataClip;

            _slotSnapshots[0] = ReadSnapshotFromSlot(0);
            _slotSnapshots[1] = ReadSnapshotFromSlot(1);

            Loaded += OnLoaded;
        }

        public bool Update(string title, string artist, string header, bool isPlaying, TrackSwitchDirection direction = TrackSwitchDirection.None)
        {
            Symbol playPauseSymbol = isPlaying ? Symbol.Pause : Symbol.Play;
            if (IconPlayPause.Symbol != playPauseSymbol)
            {
                IconPlayPause.Symbol = playPauseSymbol;
            }

            MetadataSnapshot nextSnapshot = new(title, artist, header);
            if (_slotSnapshots[_activeSlotIndex].Equals(nextSnapshot))
            {
                return false;
            }

            if (direction == TrackSwitchDirection.None)
            {
                StopMetadataTransition();
                ApplySnapshotToSlot(_activeSlotIndex, nextSnapshot);
                HideInactiveSlot();
                return true;
            }

            EnsureComposition();
            if (_compositor == null || _primaryVisual == null || _secondaryVisual == null)
            {
                StopMetadataTransition();
                ApplySnapshotToSlot(_activeSlotIndex, nextSnapshot);
                HideInactiveSlot();
                return true;
            }

            StartMetadataTransition(nextSnapshot, direction);
            return true;
        }

        public void SetColors(Color main, Color sub, Color icon)
        {
            _mainColor = main;
            _subColor = sub;

            ApplyColorsToSlot(0);
            ApplyColorsToSlot(1);

            var iconBrush = new SolidColorBrush(icon);
            IconBack.Foreground = iconBrush;
            IconPlayPause.Foreground = iconBrush;
            IconForward.Foreground = iconBrush;
        }

        private void OnBack_Click(object sender, RoutedEventArgs e) => BackClick?.Invoke(this, EventArgs.Empty);
        private void OnPlayPause_Click(object sender, RoutedEventArgs e) => PlayPauseClick?.Invoke(this, EventArgs.Empty);
        private void OnForward_Click(object sender, RoutedEventArgs e) => ForwardClick?.Invoke(this, EventArgs.Empty);

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            EnsureComposition();
            UpdateMetadataClip();
            HideInactiveSlot();
        }

        private void MetadataViewport_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateMetadataClip();
            UpdateSlotCenterPoints();
        }

        private void EnsureComposition()
        {
            if (_compositor != null)
            {
                return;
            }

            _primaryVisual = ElementCompositionPreview.GetElementVisual(MetadataSlotPrimary);
            _secondaryVisual = ElementCompositionPreview.GetElementVisual(MetadataSlotSecondary);
            _compositor = _primaryVisual.Compositor;
            _primaryClip = _compositor.CreateInsetClip();
            _secondaryClip = _compositor.CreateInsetClip();
            _primaryVisual.Clip = _primaryClip;
            _secondaryVisual.Clip = _secondaryClip;
            _enterEasing = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.18f, 1.0f), new Vector2(0.32f, 1.0f));
            _exitEasing = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.42f, 0.0f), new Vector2(0.92f, 0.52f));

            ResetVisual(_primaryVisual, visible: true);
            ResetVisual(_secondaryVisual, visible: false);
            ResetClip(_primaryClip);
            ResetClip(_secondaryClip);
            UpdateSlotCenterPoints();
        }

        private void StartMetadataTransition(MetadataSnapshot nextSnapshot, TrackSwitchDirection direction)
        {
            StopMetadataTransition();

            int outgoingIndex = _activeSlotIndex;
            int incomingIndex = GetInactiveSlotIndex();
            int motionSign = GetMotionSign(direction);

            ApplySnapshotToSlot(incomingIndex, nextSnapshot);
            SetSlotVisibility(incomingIndex, true);
            UpdateSlotCenterPoints();

            Visual outgoingVisual = GetVisual(outgoingIndex);
            Visual incomingVisual = GetVisual(incomingIndex);
            InsetClip outgoingClip = GetClip(outgoingIndex);
            InsetClip incomingClip = GetClip(incomingIndex);

            float outgoingOffset = (float)(IslandConfig.TrackSwitchOutgoingOffset * motionSign);
            float incomingStartOffset = (float)(-IslandConfig.TrackSwitchIncomingOffset * motionSign);
            float clipInset = GetClipInsetDistance();

            outgoingVisual.Opacity = 1.0f;
            outgoingVisual.Scale = Vector3.One;
            outgoingVisual.Offset = Vector3.Zero;
            ResetClip(outgoingClip);

            incomingVisual.Opacity = 0.0f;
            incomingVisual.Scale = new Vector3(IslandConfig.TrackSwitchIncomingScale, IslandConfig.TrackSwitchIncomingScale, 1.0f);
            incomingVisual.Offset = new Vector3(incomingStartOffset, 0.0f, 0.0f);
            PrepareIncomingClip(incomingClip, direction, clipInset);
            SetSlotZOrder(outgoingIndex, incomingIndex);

            CompositionScopedBatch batch = _compositor!.CreateScopedBatch(CompositionBatchTypes.Animation);
            long transitionToken = ++_transitionToken;
            batch.Completed += (_, _) =>
            {
                if (transitionToken != _transitionToken)
                {
                    return;
                }

                ResetVisual(outgoingVisual, visible: false);
                ResetVisual(incomingVisual, visible: true);
                ResetClip(outgoingClip);
                ResetClip(incomingClip);
                SetSlotVisibility(outgoingIndex, false);
                SetSlotVisibility(incomingIndex, true);
                SetSlotZOrder(incomingIndex, outgoingIndex);
            };

            StartOutgoingAnimations(outgoingVisual, outgoingClip, direction, outgoingOffset, clipInset);
            StartIncomingAnimations(incomingVisual, incomingClip, direction, clipInset);
            _activeSlotIndex = incomingIndex;
            batch.End();
        }

        private void StopMetadataTransition()
        {
            _transitionToken++;

            if (_primaryVisual != null)
            {
                StopVisualAnimations(_primaryVisual);
                ResetVisual(_primaryVisual, _activeSlotIndex == 0);
            }

            if (_secondaryVisual != null)
            {
                StopVisualAnimations(_secondaryVisual);
                ResetVisual(_secondaryVisual, _activeSlotIndex == 1);
            }

            if (_primaryClip != null)
            {
                StopClipAnimations(_primaryClip);
                ResetClip(_primaryClip);
            }

            if (_secondaryClip != null)
            {
                StopClipAnimations(_secondaryClip);
                ResetClip(_secondaryClip);
            }

            SetSlotZOrder(_activeSlotIndex, GetInactiveSlotIndex());
            HideInactiveSlot();
        }

        private void HideInactiveSlot()
        {
            int inactiveIndex = GetInactiveSlotIndex();
            SetSlotVisibility(_activeSlotIndex, true);
            SetSlotVisibility(inactiveIndex, false);
        }

        private void StartOutgoingAnimations(Visual visual, InsetClip clip, TrackSwitchDirection direction, float targetOffset, float clipInset)
        {
            visual.StartAnimation("Offset", CreateOutgoingOffsetAnimation(targetOffset));
            visual.StartAnimation("Opacity", CreateOutgoingOpacityAnimation());
            visual.StartAnimation("Scale", CreateOutgoingScaleAnimation());
            StartDirectionalClipAnimation(clip, direction, clipInset, isIncoming: false);
        }

        private void StartIncomingAnimations(Visual visual, InsetClip clip, TrackSwitchDirection direction, float clipInset)
        {
            // Delay the reveal slightly so the next track feels handed off, not overpainted on top.
            visual.StartAnimation("Offset", CreateIncomingOffsetAnimation(visual.Offset));
            visual.StartAnimation("Opacity", CreateIncomingOpacityAnimation());
            visual.StartAnimation("Scale", CreateIncomingScaleAnimation());
            StartDirectionalClipAnimation(clip, direction, clipInset, isIncoming: true);
        }

        private Vector3KeyFrameAnimation CreateOutgoingOffsetAnimation(float targetOffset)
        {
            Vector3KeyFrameAnimation animation = _compositor!.CreateVector3KeyFrameAnimation();
            animation.InsertKeyFrame(
                IslandConfig.TrackSwitchOutgoingTravelProgress,
                new Vector3(targetOffset * 0.92f, 0.0f, 0.0f),
                _exitEasing!);
            animation.InsertKeyFrame(1.0f, new Vector3(targetOffset, 0.0f, 0.0f), _exitEasing!);
            animation.Duration = GetTrackSwitchDuration();
            return animation;
        }

        private ScalarKeyFrameAnimation CreateOutgoingOpacityAnimation()
        {
            ScalarKeyFrameAnimation animation = _compositor!.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(0.14f, 0.94f);
            animation.InsertKeyFrame(IslandConfig.TrackSwitchOutgoingFadeEndProgress, 0.0f, _exitEasing!);
            animation.InsertKeyFrame(1.0f, 0.0f);
            animation.Duration = GetTrackSwitchDuration();
            return animation;
        }

        private Vector3KeyFrameAnimation CreateOutgoingScaleAnimation()
        {
            Vector3KeyFrameAnimation animation = _compositor!.CreateVector3KeyFrameAnimation();
            Vector3 compactScale = new(IslandConfig.TrackSwitchOutgoingScale, IslandConfig.TrackSwitchOutgoingScale, 1.0f);
            animation.InsertKeyFrame(IslandConfig.TrackSwitchOutgoingTravelProgress, compactScale, _exitEasing!);
            animation.InsertKeyFrame(1.0f, compactScale, _exitEasing!);
            animation.Duration = GetTrackSwitchDuration();
            return animation;
        }

        private Vector3KeyFrameAnimation CreateIncomingOffsetAnimation(Vector3 startOffset)
        {
            Vector3KeyFrameAnimation animation = _compositor!.CreateVector3KeyFrameAnimation();
            animation.InsertKeyFrame(IslandConfig.TrackSwitchIncomingDelayProgress, startOffset);
            animation.InsertKeyFrame(1.0f, Vector3.Zero, _enterEasing!);
            animation.Duration = GetTrackSwitchDuration();
            return animation;
        }

        private ScalarKeyFrameAnimation CreateIncomingOpacityAnimation()
        {
            ScalarKeyFrameAnimation animation = _compositor!.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(IslandConfig.TrackSwitchIncomingDelayProgress, 0.0f);
            animation.InsertKeyFrame(0.76f, 0.74f, _enterEasing!);
            animation.InsertKeyFrame(1.0f, 1.0f, _enterEasing!);
            animation.Duration = GetTrackSwitchDuration();
            return animation;
        }

        private Vector3KeyFrameAnimation CreateIncomingScaleAnimation()
        {
            Vector3KeyFrameAnimation animation = _compositor!.CreateVector3KeyFrameAnimation();
            Vector3 startScale = new(IslandConfig.TrackSwitchIncomingScale, IslandConfig.TrackSwitchIncomingScale, 1.0f);
            animation.InsertKeyFrame(IslandConfig.TrackSwitchIncomingDelayProgress, startScale);
            animation.InsertKeyFrame(1.0f, Vector3.One, _enterEasing!);
            animation.Duration = GetTrackSwitchDuration();
            return animation;
        }

        private static TimeSpan GetTrackSwitchDuration()
            => TimeSpan.FromMilliseconds(IslandConfig.TrackSwitchAnimationDurationMs);

        private void StopVisualAnimations(Visual visual)
        {
            visual.StopAnimation("Offset");
            visual.StopAnimation("Opacity");
            visual.StopAnimation("Scale");
        }

        private static void StopClipAnimations(InsetClip clip)
        {
            clip.StopAnimation("LeftInset");
            clip.StopAnimation("RightInset");
        }

        private void ResetVisual(Visual visual, bool visible)
        {
            visual.Offset = Vector3.Zero;
            visual.Scale = Vector3.One;
            visual.Opacity = visible ? 1.0f : 0.0f;
        }

        private static void ResetClip(InsetClip clip)
        {
            clip.LeftInset = 0.0f;
            clip.RightInset = 0.0f;
            clip.TopInset = 0.0f;
            clip.BottomInset = 0.0f;
        }

        private void SetSlotZOrder(int frontSlotIndex, int backSlotIndex)
        {
            Canvas.SetZIndex(frontSlotIndex == 0 ? MetadataSlotPrimary : MetadataSlotSecondary, 1);
            Canvas.SetZIndex(backSlotIndex == 0 ? MetadataSlotPrimary : MetadataSlotSecondary, 0);
        }

        private void SetSlotVisibility(int slotIndex, bool isVisible)
        {
            Grid slot = GetSlot(slotIndex);
            slot.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ApplySnapshotToSlot(int slotIndex, MetadataSnapshot snapshot)
        {
            _slotSnapshots[slotIndex] = snapshot;

            TextBlock header = GetHeaderText(slotIndex);
            TextBlock title = GetTitleText(slotIndex);
            TextBlock artist = GetArtistText(slotIndex);

            header.Text = snapshot.Header;
            title.Text = snapshot.Title;
            artist.Text = snapshot.Artist;
            artist.Visibility = string.IsNullOrWhiteSpace(snapshot.Artist) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ApplyColorsToSlot(int slotIndex)
        {
            GetTitleText(slotIndex).Foreground = new SolidColorBrush(_mainColor);
            SolidColorBrush subBrush = new(_subColor);
            GetArtistText(slotIndex).Foreground = subBrush;
            GetHeaderText(slotIndex).Foreground = subBrush;
        }

        private MetadataSnapshot ReadSnapshotFromSlot(int slotIndex)
        {
            return new MetadataSnapshot(
                GetTitleText(slotIndex).Text,
                GetArtistText(slotIndex).Text,
                GetHeaderText(slotIndex).Text);
        }

        private void UpdateSlotCenterPoints()
        {
            if (_primaryVisual != null)
            {
                _primaryVisual.CenterPoint = GetCenterPoint(MetadataSlotPrimary);
            }

            if (_secondaryVisual != null)
            {
                _secondaryVisual.CenterPoint = GetCenterPoint(MetadataSlotSecondary);
            }
        }

        private Vector3 GetCenterPoint(FrameworkElement element)
        {
            float width = (float)(element.ActualWidth > 0 ? element.ActualWidth : MetadataViewport.ActualWidth);
            float height = (float)(element.ActualHeight > 0 ? element.ActualHeight : MetadataViewport.ActualHeight);
            return new Vector3(width * 0.5f, height * 0.5f, 0.0f);
        }

        private void UpdateMetadataClip()
        {
            _metadataClip.Rect = new Rect(0, 0, MetadataViewport.ActualWidth, MetadataViewport.ActualHeight);
        }

        private int GetInactiveSlotIndex() => _activeSlotIndex == 0 ? 1 : 0;

        private static int GetMotionSign(TrackSwitchDirection direction)
            => direction == TrackSwitchDirection.Previous ? 1 : -1;

        private float GetClipInsetDistance()
        {
            double width = MetadataViewport.ActualWidth > 0 ? MetadataViewport.ActualWidth : IslandConfig.ExpandedWidth;
            return (float)Math.Clamp(
                width * IslandConfig.TrackSwitchClipInsetRatio,
                IslandConfig.TrackSwitchClipInsetMin,
                IslandConfig.TrackSwitchClipInsetMax);
        }

        private static void PrepareIncomingClip(InsetClip clip, TrackSwitchDirection direction, float clipInset)
        {
            ResetClip(clip);
            if (GetClipPropertyName(direction, isIncoming: true) == "RightInset")
            {
                clip.RightInset = clipInset;
            }
            else
            {
                clip.LeftInset = clipInset;
            }
        }

        private void StartDirectionalClipAnimation(InsetClip clip, TrackSwitchDirection direction, float clipInset, bool isIncoming)
        {
            string property = GetClipPropertyName(direction, isIncoming);
            ScalarKeyFrameAnimation animation = isIncoming
                ? CreateIncomingClipAnimation(clipInset)
                : CreateOutgoingClipAnimation(clipInset);
            clip.StartAnimation(property, animation);
        }

        private static string GetClipPropertyName(TrackSwitchDirection direction, bool isIncoming)
        {
            bool useLeftInset = direction == TrackSwitchDirection.Next
                ? !isIncoming
                : isIncoming;
            return useLeftInset ? "LeftInset" : "RightInset";
        }

        private ScalarKeyFrameAnimation CreateOutgoingClipAnimation(float clipInset)
        {
            ScalarKeyFrameAnimation animation = _compositor!.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(0.10f, 0.0f);
            animation.InsertKeyFrame(IslandConfig.TrackSwitchOutgoingTravelProgress, clipInset, _exitEasing!);
            animation.InsertKeyFrame(1.0f, clipInset, _exitEasing!);
            animation.Duration = GetTrackSwitchDuration();
            return animation;
        }

        private ScalarKeyFrameAnimation CreateIncomingClipAnimation(float clipInset)
        {
            ScalarKeyFrameAnimation animation = _compositor!.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(IslandConfig.TrackSwitchIncomingDelayProgress, clipInset);
            animation.InsertKeyFrame(1.0f, 0.0f, _enterEasing!);
            animation.Duration = GetTrackSwitchDuration();
            return animation;
        }

        private Grid GetSlot(int slotIndex)
            => slotIndex == 0 ? MetadataSlotPrimary : MetadataSlotSecondary;

        private Visual GetVisual(int slotIndex)
            => slotIndex == 0 ? _primaryVisual! : _secondaryVisual!;

        private InsetClip GetClip(int slotIndex)
            => slotIndex == 0 ? _primaryClip! : _secondaryClip!;

        private TextBlock GetHeaderText(int slotIndex)
            => slotIndex == 0 ? HeaderStatusTextPrimary : HeaderStatusTextSecondary;

        private TextBlock GetTitleText(int slotIndex)
            => slotIndex == 0 ? MusicTitleTextPrimary : MusicTitleTextSecondary;

        private TextBlock GetArtistText(int slotIndex)
            => slotIndex == 0 ? ArtistNameTextPrimary : ArtistNameTextSecondary;

        private readonly record struct MetadataSnapshot(string Title, string Artist, string Header);
    }
}
