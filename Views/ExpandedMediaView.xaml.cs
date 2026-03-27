using System;
using System.Collections.Generic;
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using wisland.Controls;
using wisland.Helpers;
using wisland.Models;

namespace wisland.Views
{
    public sealed partial class ExpandedMediaView : UserControl
    {
        private static readonly MediaSourceIconResolver IconResolver = new();
        private static readonly float[] AvatarSlotOffsets = { 0.0f, 10.0f, 18.0f };
        private static readonly float[] AvatarSlotScales = { 1.0f, 13.0f / 16.0f, 10.0f / 16.0f };

        private readonly DirectionalContentTransitionCoordinator _metadataTransition;
        private readonly DirectionalContentTransitionCoordinator _headerLabelTransition;
        private readonly MetadataSnapshot[] _metadataSnapshots = new MetadataSnapshot[2];
        private readonly HeaderTextSnapshot[] _headerTextSnapshots = new HeaderTextSnapshot[2];
        private AvatarStripSnapshot _avatarStripSnapshot = AvatarStripSnapshot.Empty;
        private readonly Border[] _headerAvatarBorders;
        private readonly Image[] _headerAvatarImages;
        private readonly TextBlock[] _headerAvatarFallbacks;
        private readonly TextBlock[] _headerLabels;
        private readonly FontIcon[] _headerExpandGlyphs;
        private readonly long[] _avatarLoadTokens = new long[4];
        private readonly RectangleGeometry _headerAvatarViewportClip = new();

        private Color _mainColor = Microsoft.UI.Colors.White;
        private Color _subColor = Microsoft.UI.Colors.LightGray;
        private Color _iconColor = Microsoft.UI.Colors.White;
        private string? _selectedSessionKey;
        private IReadOnlyList<MediaSessionSnapshot> _pickerSessions = Array.Empty<MediaSessionSnapshot>();

        private Compositor? _avatarCompositor;
        private readonly Visual[] _headerAvatarVisuals = new Visual[4];
        private CubicBezierEasingFunction? _avatarMoveEasing;
        private CubicBezierEasingFunction? _avatarEnterEasing;
        private CubicBezierEasingFunction? _avatarExitEasing;
        private long _avatarTransitionToken;
        private Storyboard? _headerChipWidthStoryboard;
        private double _headerChipWidth = double.NaN;

        public event EventHandler? BackClick;
        public event EventHandler? PlayPauseClick;
        public event EventHandler? ForwardClick;
        public event Action<string>? SessionSelected;

        public ExpandedMediaView()
        {
            this.InitializeComponent();

            _metadataTransition = new DirectionalContentTransitionCoordinator(
                MetadataViewport,
                MetadataSlotPrimary,
                MetadataSlotSecondary,
                IslandConfig.ExpandedMediaTransitionProfile);

            _headerLabelTransition = new DirectionalContentTransitionCoordinator(
                HeaderLabelViewport,
                HeaderLabelSlotPrimary,
                HeaderLabelSlotSecondary,
                IslandConfig.HeaderChipTransitionProfile);

            _headerAvatarBorders = new[]
            {
                HeaderAvatarPresenter0,
                HeaderAvatarPresenter1,
                HeaderAvatarPresenter2,
                HeaderAvatarPresenter3
            };

            _headerAvatarImages = new[]
            {
                HeaderAvatarPresenter0Image,
                HeaderAvatarPresenter1Image,
                HeaderAvatarPresenter2Image,
                HeaderAvatarPresenter3Image
            };

            _headerAvatarFallbacks = new[]
            {
                HeaderAvatarPresenter0Fallback,
                HeaderAvatarPresenter1Fallback,
                HeaderAvatarPresenter2Fallback,
                HeaderAvatarPresenter3Fallback
            };

            _headerLabels = new[]
            {
                HeaderLabelPrimary,
                HeaderLabelSecondary
            };

            _headerExpandGlyphs = new[]
            {
                HeaderExpandGlyphPrimary,
                HeaderExpandGlyphSecondary
            };

            HeaderAvatarViewport.Clip = _headerAvatarViewportClip;
            _metadataSnapshots[0] = ReadMetadataSnapshotFromSlot(0);
            _metadataSnapshots[1] = ReadMetadataSnapshotFromSlot(1);
            _headerTextSnapshots[0] = ReadHeaderTextSnapshotFromSlot(0);
            _headerTextSnapshots[1] = ReadHeaderTextSnapshotFromSlot(1);

            SessionPickerList.MaxHeight = IslandConfig.SessionPickerMaxVisibleItems * IslandConfig.SessionPickerEstimatedRowHeight;
            Loaded += OnLoaded;
        }

        public bool IsSessionPickerOpen { get; private set; }

        public bool UpdateMedia(
            MediaSessionSnapshot? session,
            int displayIndex,
            int sessionCount,
            IReadOnlyList<MediaSessionSnapshot> availableSessions,
            ContentTransitionDirection direction = ContentTransitionDirection.None)
        {
            UpdatePlayPauseSymbol(session.HasValue && session.Value.IsPlaying);
            UpdateSessionPickerItems(availableSessions, session?.SessionKey);

            HeaderTextSnapshot nextHeaderTextSnapshot = CreateMediaHeaderTextSnapshot(session, availableSessions.Count);
            AvatarStripSnapshot nextAvatarStripSnapshot = CreateMediaAvatarStripSnapshot(session, displayIndex, availableSessions);
            MetadataSnapshot nextMetadataSnapshot = session.HasValue
                ? new MetadataSnapshot(session.Value.Title, session.Value.Artist)
                : new MetadataSnapshot("No Media", "Waiting for music...");

            bool headerTextChanged = ApplyHeaderTextSnapshot(nextHeaderTextSnapshot, direction);
            bool avatarStripChanged = ApplyAvatarStripSnapshot(nextAvatarStripSnapshot, direction);
            bool metadataChanged = ApplyMetadataSnapshot(nextMetadataSnapshot, direction);
            return headerTextChanged || avatarStripChanged || metadataChanged;
        }

        public void ShowNotification(string title, string message, string header)
        {
            ApplyHeaderTextSnapshot(new HeaderTextSnapshot(header, ShowExpandHint: false), ContentTransitionDirection.None);
            ApplyAvatarStripSnapshot(AvatarStripSnapshot.Empty, ContentTransitionDirection.None);
            ApplyMetadataSnapshot(new MetadataSnapshot(title, message), ContentTransitionDirection.None);
        }

        public void SetColors(Color main, Color sub, Color icon)
        {
            _mainColor = main;
            _subColor = sub;
            _iconColor = icon;

            ApplyMetadataColors();
            ApplyTransportIconColors();
            ApplyHeaderColors();
            RefreshSessionPickerItemColors();
        }

        private bool ApplyHeaderTextSnapshot(HeaderTextSnapshot snapshot, ContentTransitionDirection direction)
        {
            double targetWidth = MeasureHeaderChipWidth(snapshot);
            if (_headerTextSnapshots[_headerLabelTransition.ActiveSlotIndex].Equals(snapshot))
            {
                EnsureHeaderChipWidthInitialized(targetWidth);
                return false;
            }

            if (direction == ContentTransitionDirection.None)
            {
                _headerLabelTransition.ApplyImmediately(slotIndex => ApplyHeaderTextSnapshotToSlot(slotIndex, snapshot));
                AnimateHeaderChipWidth(targetWidth);
                return true;
            }

            AnimateHeaderChipWidth(targetWidth);
            _headerLabelTransition.Transition(direction, slotIndex => ApplyHeaderTextSnapshotToSlot(slotIndex, snapshot));
            return true;
        }

        private bool ApplyAvatarStripSnapshot(AvatarStripSnapshot snapshot, ContentTransitionDirection direction)
        {
            AvatarStripSnapshot currentSnapshot = _avatarStripSnapshot;
            if (currentSnapshot.Equals(snapshot))
            {
                return false;
            }

            if (direction != ContentTransitionDirection.None
                && TryStartAvatarStripTransition(currentSnapshot, snapshot, direction))
            {
                _avatarStripSnapshot = snapshot;
                return true;
            }

            _avatarStripSnapshot = snapshot;
            ApplyAvatarStripSnapshotImmediately(snapshot);
            return true;
        }

        private bool ApplyMetadataSnapshot(MetadataSnapshot snapshot, ContentTransitionDirection direction)
        {
            if (_metadataSnapshots[_metadataTransition.ActiveSlotIndex].Equals(snapshot))
            {
                return false;
            }

            if (direction == ContentTransitionDirection.None)
            {
                _metadataTransition.ApplyImmediately(slotIndex => ApplyMetadataSnapshotToSlot(slotIndex, snapshot));
                return true;
            }

            _metadataTransition.Transition(direction, slotIndex => ApplyMetadataSnapshotToSlot(slotIndex, snapshot));
            return true;
        }

        private void OnBack_Click(object sender, RoutedEventArgs e) => BackClick?.Invoke(this, EventArgs.Empty);
        private void OnPlayPause_Click(object sender, RoutedEventArgs e) => PlayPauseClick?.Invoke(this, EventArgs.Empty);
        private void OnForward_Click(object sender, RoutedEventArgs e) => ForwardClick?.Invoke(this, EventArgs.Empty);

        private void OnSessionHeader_Click(object sender, RoutedEventArgs e)
        {
            if (SessionPickerList.Items.Count > 1)
            {
                SessionPickerFlyout.ShowAt(SessionHeaderButton);
            }
        }

        private void SessionPickerFlyout_Opening(object sender, object e)
            => IsSessionPickerOpen = true;

        private void SessionPickerFlyout_Closed(object sender, object e)
            => IsSessionPickerOpen = false;

        private void SessionPickerList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is ListViewItem item && item.Tag is string sessionKey)
            {
                SessionSelected?.Invoke(sessionKey);
                SessionPickerFlyout.Hide();
            }
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            _metadataTransition.Initialize();
            _metadataTransition.UpdateViewportBounds();
            _headerLabelTransition.Initialize();
            _headerLabelTransition.UpdateViewportBounds();
            InitializeHeaderAvatarStrip();
            UpdateHeaderAvatarViewportBounds();
            ApplyAvatarStripSnapshotImmediately(_avatarStripSnapshot);
            EnsureHeaderChipWidthInitialized(MeasureHeaderChipWidth(
                _headerTextSnapshots[_headerLabelTransition.ActiveSlotIndex]));
            ApplyMetadataColors();
            ApplyTransportIconColors();
            ApplyHeaderColors();
            RefreshSessionPickerItemColors();
        }

        private void MetadataViewport_SizeChanged(object sender, SizeChangedEventArgs e)
            => _metadataTransition.UpdateViewportBounds();

        private void HeaderLabelViewport_SizeChanged(object sender, SizeChangedEventArgs e)
            => _headerLabelTransition.UpdateViewportBounds();

        private void HeaderAvatarViewport_SizeChanged(object sender, SizeChangedEventArgs e)
            => UpdateHeaderAvatarViewportBounds();

        private void EnsureHeaderChipWidthInitialized(double targetWidth)
        {
            if (!double.IsNaN(_headerChipWidth))
            {
                return;
            }

            double width = targetWidth > 0
                ? targetWidth
                : HeaderChipBorder.ActualWidth;

            if (width > 0)
            {
                _headerChipWidth = width;
                HeaderChipBorder.Width = width;
            }
        }

        private void SetHeaderChipWidthImmediately(double targetWidth)
        {
            StopHeaderChipWidthAnimation();
            _headerChipWidth = targetWidth;
            HeaderChipBorder.Width = targetWidth;
        }

        private void AnimateHeaderChipWidth(double targetWidth)
        {
            if (targetWidth <= 0)
            {
                return;
            }

            if (!IsLoaded)
            {
                SetHeaderChipWidthImmediately(targetWidth);
                return;
            }

            double currentWidth = !double.IsNaN(_headerChipWidth)
                ? _headerChipWidth
                : HeaderChipBorder.ActualWidth;

            if (currentWidth <= 0 || Math.Abs(currentWidth - targetWidth) < 0.5)
            {
                SetHeaderChipWidthImmediately(targetWidth);
                return;
            }

            StopHeaderChipWidthAnimation();
            HeaderChipBorder.Width = currentWidth;

            Storyboard storyboard = new();
            DoubleAnimationUsingKeyFrames animation = CreateHeaderChipWidthAnimation(currentWidth, targetWidth);
            Storyboard.SetTarget(animation, HeaderChipBorder);
            Storyboard.SetTargetProperty(animation, nameof(Width));
            storyboard.Children.Add(animation);
            storyboard.Completed += (_, _) =>
            {
                HeaderChipBorder.Width = targetWidth;
                _headerChipWidth = targetWidth;
                if (ReferenceEquals(_headerChipWidthStoryboard, storyboard))
                {
                    _headerChipWidthStoryboard = null;
                }
            };

            _headerChipWidth = targetWidth;
            _headerChipWidthStoryboard = storyboard;
            storyboard.Begin();
        }

        private void StopHeaderChipWidthAnimation()
        {
            if (_headerChipWidthStoryboard == null)
            {
                return;
            }

            _headerChipWidthStoryboard.Stop();
            _headerChipWidthStoryboard = null;
        }

        private DoubleAnimationUsingKeyFrames CreateHeaderChipWidthAnimation(double currentWidth, double targetWidth)
        {
            bool isGrowing = targetWidth > currentWidth;
            double delta = targetWidth - currentWidth;
            TimeSpan duration = TimeSpan.FromMilliseconds(IslandConfig.HeaderChipSizeTransitionDurationMs);
            DoubleAnimationUsingKeyFrames animation = new()
            {
                Duration = duration,
                EnableDependentAnimation = true
            };

            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero),
                Value = currentWidth
            });

            if (isGrowing)
            {
                animation.KeyFrames.Add(new SplineDoubleKeyFrame
                {
                    KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(
                        IslandConfig.HeaderChipSizeTransitionDurationMs * IslandConfig.HeaderChipGrowSettleProgress)),
                    Value = currentWidth + (delta * IslandConfig.HeaderChipGrowSettleRatio),
                    KeySpline = CreateKeySpline(0.18, 1.0, 0.28, 1.0)
                });
            }
            else
            {
                animation.KeyFrames.Add(new DiscreteDoubleKeyFrame
                {
                    KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(
                        IslandConfig.HeaderChipSizeTransitionDurationMs * IslandConfig.HeaderChipShrinkDelayProgress)),
                    Value = currentWidth
                });
            }

            animation.KeyFrames.Add(new SplineDoubleKeyFrame
            {
                KeyTime = KeyTime.FromTimeSpan(duration),
                Value = targetWidth,
                KeySpline = isGrowing
                    ? CreateKeySpline(0.18, 1.0, 0.28, 1.0)
                    : CreateKeySpline(0.32, 0.0, 0.18, 1.0)
            });

            return animation;
        }

        private void InitializeHeaderAvatarStrip()
        {
            if (_avatarCompositor != null)
            {
                return;
            }

            for (int i = 0; i < _headerAvatarBorders.Length; i++)
            {
                Visual visual = ElementCompositionPreview.GetElementVisual(_headerAvatarBorders[i]);
                visual.CenterPoint = new Vector3(8.0f, 8.0f, 0.0f);
                _headerAvatarVisuals[i] = visual;
            }

            _avatarCompositor = _headerAvatarVisuals[0].Compositor;
            _avatarMoveEasing = _avatarCompositor.CreateCubicBezierEasingFunction(
                new Vector2(0.18f, 1.0f),
                new Vector2(0.32f, 1.0f));
            _avatarEnterEasing = _avatarCompositor.CreateCubicBezierEasingFunction(
                new Vector2(0.18f, 1.0f),
                new Vector2(0.28f, 1.0f));
            _avatarExitEasing = _avatarCompositor.CreateCubicBezierEasingFunction(
                new Vector2(0.42f, 0.0f),
                new Vector2(0.92f, 0.52f));
        }

        private void UpdateHeaderAvatarViewportBounds()
        {
            _headerAvatarViewportClip.Rect = new Rect(
                0,
                0,
                HeaderAvatarViewport.ActualWidth,
                HeaderAvatarViewport.ActualHeight);

            if (_avatarCompositor == null)
            {
                return;
            }

            for (int i = 0; i < _headerAvatarVisuals.Length; i++)
            {
                _headerAvatarVisuals[i].CenterPoint = new Vector3(8.0f, 8.0f, 0.0f);
            }
        }

        private bool TryStartAvatarStripTransition(
            AvatarStripSnapshot currentSnapshot,
            AvatarStripSnapshot nextSnapshot,
            ContentTransitionDirection direction)
        {
            InitializeHeaderAvatarStrip();
            StopAvatarStripTransition();

            List<HeaderAvatarSnapshot> currentAvatars = GetVisibleHeaderAvatars(currentSnapshot);
            List<HeaderAvatarSnapshot> nextAvatars = GetVisibleHeaderAvatars(nextSnapshot);
            if (currentAvatars.Count == 0 || nextAvatars.Count == 0)
            {
                return false;
            }

            Dictionary<string, int> presenterBySessionKey = new(StringComparer.Ordinal);
            HashSet<string> nextSessionKeys = new(StringComparer.Ordinal);
            for (int i = 0; i < currentAvatars.Count; i++)
            {
                presenterBySessionKey[currentAvatars[i].SessionKey] = i;
            }

            for (int i = 0; i < nextAvatars.Count; i++)
            {
                nextSessionKeys.Add(nextAvatars[i].SessionKey);
            }

            int enteringCount = 0;
            int exitingCount = 0;
            for (int i = 0; i < nextAvatars.Count; i++)
            {
                if (!presenterBySessionKey.ContainsKey(nextAvatars[i].SessionKey))
                {
                    enteringCount++;
                }
            }

            for (int i = 0; i < currentAvatars.Count; i++)
            {
                if (!nextSessionKeys.Contains(currentAvatars[i].SessionKey))
                {
                    exitingCount++;
                }
            }

            if (enteringCount > 1 || exitingCount > 1)
            {
                return false;
            }

            List<AvatarPresenterAnimation> animations = new(currentAvatars.Count + nextAvatars.Count);
            for (int oldSlot = 0; oldSlot < currentAvatars.Count; oldSlot++)
            {
                if (!nextSessionKeys.Contains(currentAvatars[oldSlot].SessionKey))
                {
                    animations.Add(CreateExitingAvatarAnimation(oldSlot, direction));
                }
            }

            bool usesOverlayPresenter = false;
            for (int newSlot = 0; newSlot < nextAvatars.Count; newSlot++)
            {
                HeaderAvatarSnapshot avatar = nextAvatars[newSlot];
                if (presenterBySessionKey.TryGetValue(avatar.SessionKey, out int presenterIndex))
                {
                    animations.Add(CreateMovingAvatarAnimation(
                        presenterIndex,
                        presenterIndex,
                        newSlot));
                }
                else
                {
                    if (usesOverlayPresenter)
                    {
                        return false;
                    }

                    usesOverlayPresenter = true;
                    ApplyAvatarToPresenter(3, avatar);
                    animations.Add(CreateEnteringAvatarAnimation(3, newSlot, direction));
                }
            }

            if (animations.Count == 0)
            {
                return false;
            }

            HeaderAvatarOverflowFade.Visibility = nextSnapshot.ShowOverflowFade
                ? Visibility.Visible
                : Visibility.Collapsed;

            long transitionToken = ++_avatarTransitionToken;
            CompositionScopedBatch batch = _avatarCompositor!.CreateScopedBatch(CompositionBatchTypes.Animation);
            batch.Completed += (_, _) =>
            {
                if (transitionToken != _avatarTransitionToken)
                {
                    return;
                }

                ApplyAvatarStripSnapshotImmediately(nextSnapshot);
            };

            for (int i = 0; i < animations.Count; i++)
            {
                StartAvatarPresenterAnimation(animations[i]);
            }

            batch.End();
            return true;
        }

        private void StopAvatarStripTransition()
        {
            _avatarTransitionToken++;
            if (_avatarCompositor == null)
            {
                return;
            }

            for (int i = 0; i < _headerAvatarVisuals.Length; i++)
            {
                StopAvatarPresenterAnimations(i);
            }

            ApplyAvatarStripSnapshotImmediately(_avatarStripSnapshot);
        }

        private void ApplyAvatarStripSnapshotImmediately(AvatarStripSnapshot snapshot)
        {
            InitializeHeaderAvatarStrip();

            StopAvatarPresenterAnimations(3);
            ApplyAvatarToPresenter(3, HeaderAvatarSnapshot.Hidden);
            ResetAvatarPresenterVisual(3, 2, visible: false);

            List<HeaderAvatarSnapshot> visibleAvatars = GetVisibleHeaderAvatars(snapshot);
            for (int slotIndex = 0; slotIndex < 3; slotIndex++)
            {
                StopAvatarPresenterAnimations(slotIndex);
                HeaderAvatarSnapshot avatar = slotIndex < visibleAvatars.Count
                    ? visibleAvatars[slotIndex]
                    : HeaderAvatarSnapshot.Hidden;
                ApplyAvatarToPresenter(slotIndex, avatar);
                ResetAvatarPresenterVisual(slotIndex, slotIndex, avatar.IsVisible);
            }

            HeaderAvatarOverflowFade.Visibility = snapshot.ShowOverflowFade
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void StartAvatarPresenterAnimation(AvatarPresenterAnimation animation)
        {
            Border border = _headerAvatarBorders[animation.PresenterIndex];
            Visual visual = _headerAvatarVisuals[animation.PresenterIndex];

            border.Visibility = Visibility.Visible;
            Canvas.SetZIndex(border, GetAvatarAnimationZIndex(animation.StartSlot, animation.EndSlot));

            visual.Offset = new Vector3(animation.StartOffset, 0.0f, 0.0f);
            visual.Scale = new Vector3(animation.StartScale, animation.StartScale, 1.0f);
            visual.Opacity = animation.StartOpacity;

            visual.StartAnimation("Offset", CreateAvatarOffsetAnimation(animation));
            visual.StartAnimation("Scale", CreateAvatarScaleAnimation(animation));

            ScalarKeyFrameAnimation? opacityAnimation = CreateAvatarOpacityAnimation(animation);
            if (opacityAnimation != null)
            {
                visual.StartAnimation("Opacity", opacityAnimation);
            }
            else
            {
                visual.StopAnimation("Opacity");
                visual.Opacity = animation.EndOpacity;
            }
        }

        private Vector3KeyFrameAnimation CreateAvatarOffsetAnimation(AvatarPresenterAnimation animation)
        {
            Vector3KeyFrameAnimation transition = _avatarCompositor!.CreateVector3KeyFrameAnimation();
            Vector3 start = new(animation.StartOffset, 0.0f, 0.0f);
            Vector3 end = new(animation.EndOffset, 0.0f, 0.0f);

            switch (animation.Kind)
            {
                case AvatarAnimationKind.Entering:
                    transition.InsertKeyFrame(IslandConfig.HeaderAvatarEntryDelayProgress, start);
                    transition.InsertKeyFrame(1.0f, end, _avatarEnterEasing!);
                    break;

                case AvatarAnimationKind.Exiting:
                    transition.InsertKeyFrame(0.74f, Vector3.Lerp(start, end, 0.88f), _avatarExitEasing!);
                    transition.InsertKeyFrame(1.0f, end, _avatarExitEasing!);
                    break;

                default:
                    transition.InsertKeyFrame(1.0f, end, _avatarMoveEasing!);
                    break;
            }

            transition.Duration = TimeSpan.FromMilliseconds(IslandConfig.HeaderAvatarTransitionDurationMs);
            return transition;
        }

        private Vector3KeyFrameAnimation CreateAvatarScaleAnimation(AvatarPresenterAnimation animation)
        {
            Vector3KeyFrameAnimation transition = _avatarCompositor!.CreateVector3KeyFrameAnimation();
            Vector3 start = new(animation.StartScale, animation.StartScale, 1.0f);
            Vector3 end = new(animation.EndScale, animation.EndScale, 1.0f);

            switch (animation.Kind)
            {
                case AvatarAnimationKind.Entering:
                    transition.InsertKeyFrame(IslandConfig.HeaderAvatarEntryDelayProgress, start);
                    transition.InsertKeyFrame(1.0f, end, _avatarEnterEasing!);
                    break;

                case AvatarAnimationKind.Exiting:
                    transition.InsertKeyFrame(1.0f, end, _avatarExitEasing!);
                    break;

                default:
                    transition.InsertKeyFrame(1.0f, end, _avatarMoveEasing!);
                    break;
            }

            transition.Duration = TimeSpan.FromMilliseconds(IslandConfig.HeaderAvatarTransitionDurationMs);
            return transition;
        }

        private ScalarKeyFrameAnimation? CreateAvatarOpacityAnimation(AvatarPresenterAnimation animation)
        {
            if (animation.Kind == AvatarAnimationKind.Moving)
            {
                return null;
            }

            ScalarKeyFrameAnimation transition = _avatarCompositor!.CreateScalarKeyFrameAnimation();

            if (animation.Kind == AvatarAnimationKind.Entering)
            {
                transition.InsertKeyFrame(IslandConfig.HeaderAvatarEntryDelayProgress, 0.0f);
                transition.InsertKeyFrame(0.78f, 0.82f, _avatarEnterEasing!);
                transition.InsertKeyFrame(1.0f, 1.0f, _avatarEnterEasing!);
            }
            else
            {
                transition.InsertKeyFrame(0.12f, 1.0f);
                transition.InsertKeyFrame(IslandConfig.HeaderAvatarExitFadeEndProgress, 0.0f, _avatarExitEasing!);
                transition.InsertKeyFrame(1.0f, 0.0f);
            }

            transition.Duration = TimeSpan.FromMilliseconds(IslandConfig.HeaderAvatarTransitionDurationMs);
            return transition;
        }

        private void ResetAvatarPresenterVisual(int presenterIndex, int slotIndex, bool visible)
        {
            Visual visual = _headerAvatarVisuals[presenterIndex];
            visual.Offset = new Vector3(AvatarSlotOffsets[slotIndex], 0.0f, 0.0f);
            visual.Scale = new Vector3(AvatarSlotScales[slotIndex], AvatarSlotScales[slotIndex], 1.0f);
            visual.Opacity = visible ? 1.0f : 0.0f;
            _headerAvatarBorders[presenterIndex].Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            Canvas.SetZIndex(_headerAvatarBorders[presenterIndex], visible ? GetAvatarSlotZIndex(slotIndex) : 0);
        }

        private void StopAvatarPresenterAnimations(int presenterIndex)
        {
            if (_avatarCompositor == null)
            {
                return;
            }

            Visual visual = _headerAvatarVisuals[presenterIndex];
            visual.StopAnimation("Offset");
            visual.StopAnimation("Scale");
            visual.StopAnimation("Opacity");
        }

        private void UpdateSessionPickerItems(IReadOnlyList<MediaSessionSnapshot> sessions, string? selectedSessionKey)
        {
            _pickerSessions = sessions;
            _selectedSessionKey = selectedSessionKey;
            SessionPickerList.Items.Clear();
            ListViewItem? selectedItem = null;

            for (int i = 0; i < sessions.Count; i++)
            {
                MediaSessionSnapshot session = sessions[i];
                bool isSelected = string.Equals(session.SessionKey, selectedSessionKey, StringComparison.Ordinal);
                ListViewItem item = BuildSessionPickerItem(session, isSelected);
                SessionPickerList.Items.Add(item);

                if (isSelected)
                {
                    selectedItem = item;
                }
            }

            SessionPickerList.SelectedItem = selectedItem;
        }

        private ListViewItem BuildSessionPickerItem(MediaSessionSnapshot session, bool isSelected)
        {
            Border monogramBadge = new()
            {
                Width = 24,
                Height = 24,
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.FromArgb(26, _mainColor.R, _mainColor.G, _mainColor.B)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(56, _subColor.R, _subColor.G, _subColor.B)),
                Child = new TextBlock
                {
                    Text = GetSourceMonogram(session.SourceName),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontSize = 10,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = new SolidColorBrush(_mainColor)
                }
            };

            StackPanel textStack = new()
            {
                Spacing = 2
            };

            textStack.Children.Add(new TextBlock
            {
                Text = session.SourceName,
                FontSize = 11,
                Foreground = new SolidColorBrush(_subColor),
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            textStack.Children.Add(new TextBlock
            {
                Text = session.Title,
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(_mainColor),
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            textStack.Children.Add(new TextBlock
            {
                Text = session.Artist,
                FontSize = 11,
                Foreground = new SolidColorBrush(_subColor),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Visibility = string.IsNullOrWhiteSpace(session.Artist) ? Visibility.Collapsed : Visibility.Visible
            });

            TextBlock statusText = new()
            {
                Text = GetPlaybackStatusLabel(session),
                FontSize = 11,
                Foreground = new SolidColorBrush(_subColor),
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(12, 0, 0, 0)
            };

            Grid content = new()
            {
                Padding = new Thickness(12, 10, 12, 10)
            };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.Children.Add(monogramBadge);
            Grid.SetColumn(monogramBadge, 0);

            Border textHost = new()
            {
                Child = textStack,
                Margin = new Thickness(10, 0, 0, 0)
            };
            content.Children.Add(textHost);
            Grid.SetColumn(textHost, 1);

            content.Children.Add(statusText);
            Grid.SetColumn(statusText, 2);

            return new ListViewItem
            {
                Tag = session.SessionKey,
                Content = content,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                IsSelected = isSelected
            };
        }

        private void RefreshSessionPickerItemColors()
            => UpdateSessionPickerItems(_pickerSessions, _selectedSessionKey);

        private double MeasureHeaderChipWidth(HeaderTextSnapshot snapshot)
        {
            StackPanel labelStack = new()
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4
            };

            TextBlock label = new()
            {
                Text = snapshot.Label,
                FontSize = HeaderLabelPrimary.FontSize,
                FontWeight = HeaderLabelPrimary.FontWeight,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = HeaderLabelPrimary.MaxWidth
            };
            labelStack.Children.Add(label);

            if (snapshot.ShowExpandHint)
            {
                labelStack.Children.Add(new FontIcon
                {
                    Glyph = HeaderExpandGlyphPrimary.Glyph,
                    FontSize = HeaderExpandGlyphPrimary.FontSize
                });
            }

            labelStack.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

            Thickness padding = HeaderChipBorder.Padding;
            Thickness borderThickness = HeaderChipBorder.BorderThickness;
            double contentWidth = 34.0 + 4.0 + labelStack.DesiredSize.Width;
            double chromeWidth = padding.Left + padding.Right + borderThickness.Left + borderThickness.Right;
            return Math.Ceiling(contentWidth + chromeWidth);
        }

        private HeaderTextSnapshot CreateMediaHeaderTextSnapshot(MediaSessionSnapshot? session, int sessionCount)
        {
            if (!session.HasValue)
            {
                return new HeaderTextSnapshot("Wisland", ShowExpandHint: false);
            }

            return new HeaderTextSnapshot(
                GetHeaderLabel(session.Value),
                ShowExpandHint: sessionCount > 1);
        }

        private AvatarStripSnapshot CreateMediaAvatarStripSnapshot(
            MediaSessionSnapshot? session,
            int displayIndex,
            IReadOnlyList<MediaSessionSnapshot> availableSessions)
        {
            if (!session.HasValue)
            {
                return AvatarStripSnapshot.Empty;
            }

            HeaderAvatarSnapshot[] avatars = GetVisibleHeaderAvatars(availableSessions, displayIndex);
            return new AvatarStripSnapshot(
                ShowOverflowFade: availableSessions.Count > 3,
                First: avatars[0],
                Second: avatars[1],
                Third: avatars[2]);
        }

        private HeaderAvatarSnapshot[] GetVisibleHeaderAvatars(
            IReadOnlyList<MediaSessionSnapshot> sessions,
            int displayIndex)
        {
            HeaderAvatarSnapshot[] avatars =
            {
                HeaderAvatarSnapshot.Hidden,
                HeaderAvatarSnapshot.Hidden,
                HeaderAvatarSnapshot.Hidden
            };

            if (sessions.Count == 0)
            {
                return avatars;
            }

            int startIndex = displayIndex >= 0 && displayIndex < sessions.Count ? displayIndex : 0;
            int visibleCount = Math.Min(3, sessions.Count);
            for (int i = 0; i < visibleCount; i++)
            {
                MediaSessionSnapshot session = sessions[(startIndex + i) % sessions.Count];
                avatars[i] = new HeaderAvatarSnapshot(
                    session.SessionKey,
                    session.SourceAppId,
                    session.SourceName,
                    true);
            }

            return avatars;
        }

        private static List<HeaderAvatarSnapshot> GetVisibleHeaderAvatars(AvatarStripSnapshot snapshot)
        {
            List<HeaderAvatarSnapshot> avatars = new(3);
            if (snapshot.First.IsVisible)
            {
                avatars.Add(snapshot.First);
            }

            if (snapshot.Second.IsVisible)
            {
                avatars.Add(snapshot.Second);
            }

            if (snapshot.Third.IsVisible)
            {
                avatars.Add(snapshot.Third);
            }

            return avatars;
        }

        private void ApplyHeaderTextSnapshotToSlot(int slotIndex, HeaderTextSnapshot snapshot)
        {
            _headerTextSnapshots[slotIndex] = snapshot;
            _headerLabels[slotIndex].Text = snapshot.Label;
            _headerExpandGlyphs[slotIndex].Visibility = snapshot.ShowExpandHint
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void ApplyMetadataSnapshotToSlot(int slotIndex, MetadataSnapshot snapshot)
        {
            _metadataSnapshots[slotIndex] = snapshot;
            TextBlock title = GetTitleText(slotIndex);
            TextBlock artist = GetArtistText(slotIndex);

            title.Text = snapshot.Title;
            artist.Text = snapshot.Artist;
            artist.Visibility = string.IsNullOrWhiteSpace(snapshot.Artist) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void ApplyAvatarToPresenter(int presenterIndex, HeaderAvatarSnapshot avatar)
        {
            Border border = _headerAvatarBorders[presenterIndex];
            Image image = _headerAvatarImages[presenterIndex];
            TextBlock fallback = _headerAvatarFallbacks[presenterIndex];

            _avatarLoadTokens[presenterIndex]++;
            image.Source = null;
            image.Visibility = Visibility.Collapsed;

            if (!avatar.IsVisible)
            {
                border.Visibility = Visibility.Collapsed;
                fallback.Visibility = Visibility.Collapsed;
                fallback.Text = string.Empty;
                return;
            }

            border.Visibility = Visibility.Visible;
            fallback.Visibility = Visibility.Visible;
            fallback.Text = GetSourceMonogram(avatar.SourceName);

            if (!string.IsNullOrWhiteSpace(avatar.SourceAppId))
            {
                long token = _avatarLoadTokens[presenterIndex];
                _ = LoadAvatarIconAsync(presenterIndex, avatar.SourceAppId, token);
            }
        }

        private async System.Threading.Tasks.Task LoadAvatarIconAsync(
            int presenterIndex,
            string sourceAppId,
            long token)
        {
            ImageSource? icon = await IconResolver.ResolveAsync(sourceAppId);
            if (_avatarLoadTokens[presenterIndex] != token || icon == null)
            {
                return;
            }

            Image image = _headerAvatarImages[presenterIndex];
            TextBlock fallback = _headerAvatarFallbacks[presenterIndex];
            image.Source = icon;
            image.Visibility = Visibility.Visible;
            fallback.Visibility = Visibility.Collapsed;
        }

        private void ApplyMetadataColors()
        {
            SolidColorBrush mainBrush = new(_mainColor);
            SolidColorBrush subBrush = new(_subColor);
            MusicTitleTextPrimary.Foreground = mainBrush;
            MusicTitleTextSecondary.Foreground = mainBrush;
            ArtistNameTextPrimary.Foreground = subBrush;
            ArtistNameTextSecondary.Foreground = subBrush;
        }

        private void ApplyTransportIconColors()
        {
            SolidColorBrush iconBrush = new(_iconColor);
            IconBack.Foreground = iconBrush;
            IconPlayPause.Foreground = iconBrush;
            IconForward.Foreground = iconBrush;
        }

        private void ApplyHeaderColors()
        {
            Color borderColor = Color.FromArgb(54, _subColor.R, _subColor.G, _subColor.B);
            Color backgroundColor = Color.FromArgb(20, _mainColor.R, _mainColor.G, _mainColor.B);
            Color avatarFillColor = Color.FromArgb(34, _mainColor.R, _mainColor.G, _mainColor.B);
            Color avatarBorderColor = Color.FromArgb(68, _subColor.R, _subColor.G, _subColor.B);

            HeaderChipBorder.BorderBrush = new SolidColorBrush(borderColor);
            HeaderChipBorder.Background = new SolidColorBrush(backgroundColor);
            SessionPickerBorder.BorderBrush = new SolidColorBrush(borderColor);
            SessionPickerBorder.Background = new SolidColorBrush(backgroundColor);
            HeaderLabelPrimary.Foreground = new SolidColorBrush(_subColor);
            HeaderLabelSecondary.Foreground = new SolidColorBrush(_subColor);
            SolidColorBrush expandGlyphBrush = new(new Color { A = 204, R = _subColor.R, G = _subColor.G, B = _subColor.B });
            HeaderExpandGlyphPrimary.Foreground = expandGlyphBrush;
            HeaderExpandGlyphSecondary.Foreground = expandGlyphBrush;

            for (int presenterIndex = 0; presenterIndex < _headerAvatarBorders.Length; presenterIndex++)
            {
                _headerAvatarBorders[presenterIndex].Background = new SolidColorBrush(avatarFillColor);
                _headerAvatarBorders[presenterIndex].BorderBrush = new SolidColorBrush(avatarBorderColor);
                _headerAvatarBorders[presenterIndex].BorderThickness = new Thickness(1);
                _headerAvatarFallbacks[presenterIndex].Foreground = new SolidColorBrush(_mainColor);
            }

            HeaderAvatarOverflowFade.Fill = CreateOverflowFadeBrush(backgroundColor);
        }

        private void UpdatePlayPauseSymbol(bool isPlaying)
        {
            Symbol playPauseSymbol = isPlaying ? Symbol.Pause : Symbol.Play;
            if (IconPlayPause.Symbol != playPauseSymbol)
            {
                IconPlayPause.Symbol = playPauseSymbol;
            }
        }

        private MetadataSnapshot ReadMetadataSnapshotFromSlot(int slotIndex)
            => new(GetTitleText(slotIndex).Text, GetArtistText(slotIndex).Text);

        private HeaderTextSnapshot ReadHeaderTextSnapshotFromSlot(int slotIndex)
            => new(
                _headerLabels[slotIndex].Text,
                ShowExpandHint: _headerExpandGlyphs[slotIndex].Visibility == Visibility.Visible);

        private TextBlock GetTitleText(int slotIndex)
            => slotIndex == 0 ? MusicTitleTextPrimary : MusicTitleTextSecondary;

        private TextBlock GetArtistText(int slotIndex)
            => slotIndex == 0 ? ArtistNameTextPrimary : ArtistNameTextSecondary;

        private static string GetHeaderLabel(MediaSessionSnapshot session)
            => session.PlaybackStatus switch
            {
                Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => "Now Playing",
                Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => "Paused",
                _ => "Media"
            };

        private static string GetSourceMonogram(string sourceName)
        {
            foreach (char c in sourceName)
            {
                if (char.IsLetterOrDigit(c))
                {
                    return char.ToUpperInvariant(c).ToString();
                }
            }

            return "M";
        }

        private static string GetPlaybackStatusLabel(MediaSessionSnapshot session)
            => session.PlaybackStatus switch
            {
                Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => "Playing",
                Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => "Paused",
                _ => "Idle"
            };

        private static Brush CreateOverflowFadeBrush(Color backgroundColor)
        {
            LinearGradientBrush brush = new()
            {
                StartPoint = new Point(0, 0.5),
                EndPoint = new Point(1, 0.5)
            };
            brush.GradientStops.Add(new GradientStop
            {
                Color = Color.FromArgb(0, backgroundColor.R, backgroundColor.G, backgroundColor.B),
                Offset = 0
            });
            brush.GradientStops.Add(new GradientStop
            {
                Color = backgroundColor,
                Offset = 1
            });
            return brush;
        }

        private static KeySpline CreateKeySpline(
            double controlPoint1X,
            double controlPoint1Y,
            double controlPoint2X,
            double controlPoint2Y)
            => new()
            {
                ControlPoint1 = new Point(controlPoint1X, controlPoint1Y),
                ControlPoint2 = new Point(controlPoint2X, controlPoint2Y)
            };

        private static int GetAvatarSlotZIndex(int slotIndex)
            => 30 - (slotIndex * 10);

        private static int GetAvatarAnimationZIndex(int startSlot, int endSlot)
        {
            int dominantSlot = Math.Min(
                startSlot >= 0 ? startSlot : 3,
                endSlot >= 0 ? endSlot : 3);
            return dominantSlot >= 0 && dominantSlot < 3
                ? GetAvatarSlotZIndex(dominantSlot)
                : 0;
        }

        private static AvatarPresenterAnimation CreateMovingAvatarAnimation(
            int presenterIndex,
            int startSlot,
            int endSlot)
            => new(
                PresenterIndex: presenterIndex,
                StartSlot: startSlot,
                EndSlot: endSlot,
                StartOffset: AvatarSlotOffsets[startSlot],
                EndOffset: AvatarSlotOffsets[endSlot],
                StartScale: AvatarSlotScales[startSlot],
                EndScale: AvatarSlotScales[endSlot],
                StartOpacity: 1.0f,
                EndOpacity: 1.0f,
                Kind: AvatarAnimationKind.Moving);

        private static AvatarPresenterAnimation CreateEnteringAvatarAnimation(
            int presenterIndex,
            int endSlot,
            ContentTransitionDirection direction)
        {
            float endOffset = AvatarSlotOffsets[endSlot];
            float offsetDelta = direction == ContentTransitionDirection.Forward
                ? IslandConfig.HeaderAvatarEnterTravel
                : -IslandConfig.HeaderAvatarEnterTravel;
            float endScale = AvatarSlotScales[endSlot];

            return new AvatarPresenterAnimation(
                PresenterIndex: presenterIndex,
                StartSlot: endSlot,
                EndSlot: endSlot,
                StartOffset: endOffset + offsetDelta,
                EndOffset: endOffset,
                StartScale: endScale * IslandConfig.HeaderAvatarEnterScaleMultiplier,
                EndScale: endScale,
                StartOpacity: 0.0f,
                EndOpacity: 1.0f,
                Kind: AvatarAnimationKind.Entering);
        }

        private static AvatarPresenterAnimation CreateExitingAvatarAnimation(
            int startSlot,
            ContentTransitionDirection direction)
        {
            float startOffset = AvatarSlotOffsets[startSlot];
            float offsetDelta = direction == ContentTransitionDirection.Forward
                ? -IslandConfig.HeaderAvatarExitTravel
                : IslandConfig.HeaderAvatarExitTravel;
            float startScale = AvatarSlotScales[startSlot];

            return new AvatarPresenterAnimation(
                PresenterIndex: startSlot,
                StartSlot: startSlot,
                EndSlot: -1,
                StartOffset: startOffset,
                EndOffset: startOffset + offsetDelta,
                StartScale: startScale,
                EndScale: startScale * IslandConfig.HeaderAvatarExitScaleMultiplier,
                StartOpacity: 1.0f,
                EndOpacity: 0.0f,
                Kind: AvatarAnimationKind.Exiting);
        }

        private readonly record struct MetadataSnapshot(string Title, string Artist);

        private readonly record struct HeaderTextSnapshot(
            string Label,
            bool ShowExpandHint);

        private readonly record struct AvatarStripSnapshot(
            bool ShowOverflowFade,
            HeaderAvatarSnapshot First,
            HeaderAvatarSnapshot Second,
            HeaderAvatarSnapshot Third)
        {
            public static AvatarStripSnapshot Empty { get; } = new(
                ShowOverflowFade: false,
                First: HeaderAvatarSnapshot.Hidden,
                Second: HeaderAvatarSnapshot.Hidden,
                Third: HeaderAvatarSnapshot.Hidden);
        }

        private readonly record struct HeaderAvatarSnapshot(
            string SessionKey,
            string SourceAppId,
            string SourceName,
            bool IsVisible)
        {
            public static HeaderAvatarSnapshot Hidden { get; } = new(string.Empty, string.Empty, string.Empty, false);
        }

        private readonly record struct AvatarPresenterAnimation(
            int PresenterIndex,
            int StartSlot,
            int EndSlot,
            float StartOffset,
            float EndOffset,
            float StartScale,
            float EndScale,
            float StartOpacity,
            float EndOpacity,
            AvatarAnimationKind Kind);

        private enum AvatarAnimationKind
        {
            Moving,
            Entering,
            Exiting
        }
    }
}
