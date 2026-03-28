using System;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using wisland.Models;

namespace wisland.Controls
{
    /// <summary>
    /// Reusable two-slot horizontal content transition coordinator.
    /// Owns composition visuals, clip choreography, and slot swapping so views only supply content.
    /// </summary>
    public sealed class DirectionalContentTransitionCoordinator
    {
        private readonly FrameworkElement _viewport;
        private readonly FrameworkElement _primarySlot;
        private readonly FrameworkElement _secondarySlot;
        private readonly DirectionalTransitionProfile _profile;
        private readonly RectangleGeometry _viewportClip = new();

        private Compositor? _compositor;
        private Visual? _primaryVisual;
        private Visual? _secondaryVisual;
        private InsetClip? _primaryClip;
        private InsetClip? _secondaryClip;
        private CubicBezierEasingFunction? _enterEasing;
        private CubicBezierEasingFunction? _exitEasing;
        private int _activeSlotIndex;
        private long _transitionToken;

        public DirectionalContentTransitionCoordinator(
            FrameworkElement viewport,
            FrameworkElement primarySlot,
            FrameworkElement secondarySlot,
            DirectionalTransitionProfile profile)
        {
            _viewport = viewport;
            _primarySlot = primarySlot;
            _secondarySlot = secondarySlot;
            _profile = profile;
            _viewport.Clip = _viewportClip;
        }

        public int ActiveSlotIndex => _activeSlotIndex;

        public void Initialize()
        {
            if (_compositor != null)
            {
                return;
            }

            _primaryVisual = ElementCompositionPreview.GetElementVisual(_primarySlot);
            _secondaryVisual = ElementCompositionPreview.GetElementVisual(_secondarySlot);
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
            SetSlotZOrder(_activeSlotIndex, GetInactiveSlotIndex());
            UpdateViewportBounds();
            ShowActiveSlotOnly();
        }

        public void UpdateViewportBounds()
        {
            _viewportClip.Rect = new Rect(0, 0, _viewport.ActualWidth, _viewport.ActualHeight);
            UpdateSlotCenterPoints();
        }

        public void ApplyImmediately(Action<int> applyContentToSlot)
        {
            Stop();
            applyContentToSlot(_activeSlotIndex);
            ShowActiveSlotOnly();
        }

        public void Crossfade(Action<int> applyContentToIncomingSlot)
        {
            Initialize();
            Stop();

            int outgoingIndex = _activeSlotIndex;
            int incomingIndex = GetInactiveSlotIndex();

            applyContentToIncomingSlot(incomingIndex);
            SetSlotVisibility(incomingIndex, true);
            UpdateSlotCenterPoints();

            Visual outgoingVisual = GetVisual(outgoingIndex);
            Visual incomingVisual = GetVisual(incomingIndex);
            InsetClip outgoingClip = GetClip(outgoingIndex);
            InsetClip incomingClip = GetClip(incomingIndex);

            outgoingVisual.Opacity = 1.0f;
            outgoingVisual.Scale = Vector3.One;
            outgoingVisual.Offset = Vector3.Zero;
            ResetClip(outgoingClip);

            incomingVisual.Opacity = 0.0f;
            incomingVisual.Scale = new Vector3(_profile.IncomingScale, _profile.IncomingScale, 1.0f);
            incomingVisual.Offset = Vector3.Zero;
            ResetClip(incomingClip);
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

            outgoingVisual.StartAnimation("Opacity", CreateNeutralOutgoingOpacityAnimation());
            outgoingVisual.StartAnimation("Scale", CreateNeutralOutgoingScaleAnimation());
            incomingVisual.StartAnimation("Opacity", CreateNeutralIncomingOpacityAnimation());
            incomingVisual.StartAnimation("Scale", CreateNeutralIncomingScaleAnimation());

            _activeSlotIndex = incomingIndex;
            batch.End();
        }

        public void Transition(ContentTransitionDirection direction, Action<int> applyContentToIncomingSlot)
        {
            if (direction == ContentTransitionDirection.None)
            {
                ApplyImmediately(applyContentToIncomingSlot);
                return;
            }

            Initialize();
            Stop();

            int outgoingIndex = _activeSlotIndex;
            int incomingIndex = GetInactiveSlotIndex();
            int motionSign = GetMotionSign(direction);

            applyContentToIncomingSlot(incomingIndex);
            SetSlotVisibility(incomingIndex, true);
            UpdateSlotCenterPoints();

            Visual outgoingVisual = GetVisual(outgoingIndex);
            Visual incomingVisual = GetVisual(incomingIndex);
            InsetClip outgoingClip = GetClip(outgoingIndex);
            InsetClip incomingClip = GetClip(incomingIndex);

            float outgoingOffset = _profile.OutgoingOffset * motionSign;
            float incomingStartOffset = -(_profile.IncomingOffset * motionSign);
            float clipInset = GetClipInsetDistance();

            outgoingVisual.Opacity = 1.0f;
            outgoingVisual.Scale = Vector3.One;
            outgoingVisual.Offset = Vector3.Zero;
            ResetClip(outgoingClip);

            incomingVisual.Opacity = 0.0f;
            incomingVisual.Scale = new Vector3(_profile.IncomingScale, _profile.IncomingScale, 1.0f);
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

        public void Stop()
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
            ShowActiveSlotOnly();
        }

        private void ShowActiveSlotOnly()
        {
            int inactiveIndex = GetInactiveSlotIndex();
            SetSlotVisibility(_activeSlotIndex, true);
            SetSlotVisibility(inactiveIndex, false);
        }

        private void StartOutgoingAnimations(Visual visual, InsetClip clip, ContentTransitionDirection direction, float targetOffset, float clipInset)
        {
            visual.StartAnimation("Offset", CreateOutgoingOffsetAnimation(targetOffset));
            visual.StartAnimation("Opacity", CreateOutgoingOpacityAnimation());
            visual.StartAnimation("Scale", CreateOutgoingScaleAnimation());
            StartDirectionalClipAnimation(clip, direction, clipInset, isIncoming: false);
        }

        private void StartIncomingAnimations(Visual visual, InsetClip clip, ContentTransitionDirection direction, float clipInset)
        {
            visual.StartAnimation("Offset", CreateIncomingOffsetAnimation(visual.Offset));
            visual.StartAnimation("Opacity", CreateIncomingOpacityAnimation());
            visual.StartAnimation("Scale", CreateIncomingScaleAnimation());
            StartDirectionalClipAnimation(clip, direction, clipInset, isIncoming: true);
        }

        private Vector3KeyFrameAnimation CreateOutgoingOffsetAnimation(float targetOffset)
        {
            Vector3KeyFrameAnimation animation = _compositor!.CreateVector3KeyFrameAnimation();
            animation.InsertKeyFrame(
                _profile.OutgoingTravelProgress,
                new Vector3(targetOffset * 0.92f, 0.0f, 0.0f),
                _exitEasing!);
            animation.InsertKeyFrame(1.0f, new Vector3(targetOffset, 0.0f, 0.0f), _exitEasing!);
            animation.Duration = GetTransitionDuration();
            return animation;
        }

        private ScalarKeyFrameAnimation CreateOutgoingOpacityAnimation()
        {
            ScalarKeyFrameAnimation animation = _compositor!.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(0.14f, 0.94f);
            animation.InsertKeyFrame(_profile.OutgoingFadeEndProgress, 0.0f, _exitEasing!);
            animation.InsertKeyFrame(1.0f, 0.0f);
            animation.Duration = GetTransitionDuration();
            return animation;
        }

        private Vector3KeyFrameAnimation CreateOutgoingScaleAnimation()
        {
            Vector3KeyFrameAnimation animation = _compositor!.CreateVector3KeyFrameAnimation();
            Vector3 compactScale = new(_profile.OutgoingScale, _profile.OutgoingScale, 1.0f);
            animation.InsertKeyFrame(_profile.OutgoingTravelProgress, compactScale, _exitEasing!);
            animation.InsertKeyFrame(1.0f, compactScale, _exitEasing!);
            animation.Duration = GetTransitionDuration();
            return animation;
        }

        private Vector3KeyFrameAnimation CreateIncomingOffsetAnimation(Vector3 startOffset)
        {
            Vector3KeyFrameAnimation animation = _compositor!.CreateVector3KeyFrameAnimation();
            animation.InsertKeyFrame(_profile.IncomingDelayProgress, startOffset);
            animation.InsertKeyFrame(1.0f, Vector3.Zero, _enterEasing!);
            animation.Duration = GetTransitionDuration();
            return animation;
        }

        private ScalarKeyFrameAnimation CreateIncomingOpacityAnimation()
        {
            ScalarKeyFrameAnimation animation = _compositor!.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(_profile.IncomingDelayProgress, 0.0f);
            animation.InsertKeyFrame(0.76f, 0.74f, _enterEasing!);
            animation.InsertKeyFrame(1.0f, 1.0f, _enterEasing!);
            animation.Duration = GetTransitionDuration();
            return animation;
        }

        private Vector3KeyFrameAnimation CreateIncomingScaleAnimation()
        {
            Vector3KeyFrameAnimation animation = _compositor!.CreateVector3KeyFrameAnimation();
            Vector3 startScale = new(_profile.IncomingScale, _profile.IncomingScale, 1.0f);
            animation.InsertKeyFrame(_profile.IncomingDelayProgress, startScale);
            animation.InsertKeyFrame(1.0f, Vector3.One, _enterEasing!);
            animation.Duration = GetTransitionDuration();
            return animation;
        }

        private ScalarKeyFrameAnimation CreateNeutralOutgoingOpacityAnimation()
        {
            ScalarKeyFrameAnimation animation = _compositor!.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(0.0f, 1.0f);
            animation.InsertKeyFrame(1.0f, 0.0f, _exitEasing!);
            animation.Duration = GetTransitionDuration();
            return animation;
        }

        private Vector3KeyFrameAnimation CreateNeutralOutgoingScaleAnimation()
        {
            Vector3KeyFrameAnimation animation = _compositor!.CreateVector3KeyFrameAnimation();
            Vector3 compactScale = new(_profile.OutgoingScale, _profile.OutgoingScale, 1.0f);
            animation.InsertKeyFrame(1.0f, compactScale, _exitEasing!);
            animation.Duration = GetTransitionDuration();
            return animation;
        }

        private ScalarKeyFrameAnimation CreateNeutralIncomingOpacityAnimation()
        {
            ScalarKeyFrameAnimation animation = _compositor!.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(_profile.IncomingDelayProgress, 0.0f);
            animation.InsertKeyFrame(1.0f, 1.0f, _enterEasing!);
            animation.Duration = GetTransitionDuration();
            return animation;
        }

        private Vector3KeyFrameAnimation CreateNeutralIncomingScaleAnimation()
        {
            Vector3KeyFrameAnimation animation = _compositor!.CreateVector3KeyFrameAnimation();
            Vector3 startScale = new(_profile.IncomingScale, _profile.IncomingScale, 1.0f);
            animation.InsertKeyFrame(_profile.IncomingDelayProgress, startScale);
            animation.InsertKeyFrame(1.0f, Vector3.One, _enterEasing!);
            animation.Duration = GetTransitionDuration();
            return animation;
        }

        private ScalarKeyFrameAnimation CreateOutgoingClipAnimation(float clipInset)
        {
            ScalarKeyFrameAnimation animation = _compositor!.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(0.10f, 0.0f);
            animation.InsertKeyFrame(_profile.OutgoingTravelProgress, clipInset, _exitEasing!);
            animation.InsertKeyFrame(1.0f, clipInset, _exitEasing!);
            animation.Duration = GetTransitionDuration();
            return animation;
        }

        private ScalarKeyFrameAnimation CreateIncomingClipAnimation(float clipInset)
        {
            ScalarKeyFrameAnimation animation = _compositor!.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(_profile.IncomingDelayProgress, clipInset);
            animation.InsertKeyFrame(1.0f, 0.0f, _enterEasing!);
            animation.Duration = GetTransitionDuration();
            return animation;
        }

        private TimeSpan GetTransitionDuration()
            => TimeSpan.FromMilliseconds(_profile.DurationMs);

        private void StartDirectionalClipAnimation(InsetClip clip, ContentTransitionDirection direction, float clipInset, bool isIncoming)
        {
            string property = GetClipPropertyName(direction, isIncoming);
            ScalarKeyFrameAnimation animation = isIncoming
                ? CreateIncomingClipAnimation(clipInset)
                : CreateOutgoingClipAnimation(clipInset);
            clip.StartAnimation(property, animation);
        }

        private float GetClipInsetDistance()
        {
            double width = _viewport.ActualWidth > 0 ? _viewport.ActualWidth : _primarySlot.ActualWidth;
            if (width <= 0)
            {
                width = 1;
            }

            return (float)Math.Clamp(
                width * _profile.ClipInsetRatio,
                _profile.ClipInsetMin,
                _profile.ClipInsetMax);
        }

        private void UpdateSlotCenterPoints()
        {
            if (_primaryVisual != null)
            {
                _primaryVisual.CenterPoint = GetCenterPoint(_primarySlot);
            }

            if (_secondaryVisual != null)
            {
                _secondaryVisual.CenterPoint = GetCenterPoint(_secondarySlot);
            }
        }

        private Vector3 GetCenterPoint(FrameworkElement element)
        {
            float width = (float)(element.ActualWidth > 0 ? element.ActualWidth : _viewport.ActualWidth);
            float height = (float)(element.ActualHeight > 0 ? element.ActualHeight : _viewport.ActualHeight);
            return new Vector3(width * 0.5f, height * 0.5f, 0.0f);
        }

        private static void PrepareIncomingClip(InsetClip clip, ContentTransitionDirection direction, float clipInset)
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

        private static string GetClipPropertyName(ContentTransitionDirection direction, bool isIncoming)
        {
            bool useLeftInset = direction == ContentTransitionDirection.Forward
                ? !isIncoming
                : isIncoming;
            return useLeftInset ? "LeftInset" : "RightInset";
        }

        private static int GetMotionSign(ContentTransitionDirection direction)
            => direction == ContentTransitionDirection.Backward ? 1 : -1;

        private void SetSlotVisibility(int slotIndex, bool isVisible)
        {
            FrameworkElement slot = GetSlot(slotIndex);
            slot.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SetSlotZOrder(int frontSlotIndex, int backSlotIndex)
        {
            Canvas.SetZIndex(GetSlot(frontSlotIndex), 1);
            Canvas.SetZIndex(GetSlot(backSlotIndex), 0);
        }

        private int GetInactiveSlotIndex() => _activeSlotIndex == 0 ? 1 : 0;

        private FrameworkElement GetSlot(int slotIndex)
            => slotIndex == 0 ? _primarySlot : _secondarySlot;

        private Visual GetVisual(int slotIndex)
            => slotIndex == 0 ? _primaryVisual! : _secondaryVisual!;

        private InsetClip GetClip(int slotIndex)
            => slotIndex == 0 ? _primaryClip! : _secondaryClip!;

        private static void StopVisualAnimations(Visual visual)
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

        private static void ResetVisual(Visual visual, bool visible)
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
    }
}
