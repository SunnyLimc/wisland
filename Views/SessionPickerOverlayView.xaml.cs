using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using wisland.Helpers;
using wisland.Models;
using wisland.Services;

namespace wisland.Views
{
    public sealed partial class SessionPickerOverlayView : UserControl
    {
        private static readonly MediaSourceIconResolver IconResolver = new();
        private const double ScrollFadeOpacity = 1.0;
        private const double ScrollFadeEpsilon = 0.5;
        private readonly ObservableCollection<SessionPickerRowVisualModel> _rows = new();
        private IReadOnlyList<SessionPickerRowModel> _currentModels = Array.Empty<SessionPickerRowModel>();
        private Color _primaryColor = Microsoft.UI.Colors.White;
        private Color _secondaryColor = Microsoft.UI.Colors.LightGray;
        private Color _surfaceColor = Color.FromArgb(236, 24, 28, 36);
        private long _iconLoadToken;
        private ScrollViewer? _sessionListScrollViewer;
        private SessionPickerOverlayLayoutMetrics? _lastPublishedLayoutMetrics;
        private Visual? _chromeVisual;
        private Visual? _panelVisual;
        private Compositor? _panelCompositor;
        private Visual? _sessionListVisual;
        private InsetClip? _sessionListClip;
        private CubicBezierEasingFunction? _panelShowEasing;
        private CubicBezierEasingFunction? _panelOpacityEasing;
        private CubicBezierEasingFunction? _listRevealEasing;
        private bool _isCorrectingSessionListOffset;
        private double _sessionListViewportCompensation;

        public SessionPickerOverlayView()
        {
            this.InitializeComponent();
            SessionList.ItemsSource = _rows;
            ScrollIndicator.Width = IslandConfig.SessionPickerOverlayScrollIndicatorWidth;
            ScrollIndicator.Margin = new Thickness(0, 0, IslandConfig.SessionPickerOverlayScrollIndicatorRightInset, 0);
            UpdateListViewport();
            ApplyPanelColors();
        }

        public event Action<string>? SessionSelected;

        public event EventHandler? DismissRequested;

        public event Action<SessionPickerOverlayLayoutMetrics>? LayoutMetricsChanged;

        public void SetColors(Color primary, Color secondary, Color surface)
        {
            _primaryColor = primary;
            _secondaryColor = secondary;
            _surfaceColor = surface;
            ApplyPanelColors();
            SyncRows();
        }

        public void SetRows(IReadOnlyList<SessionPickerRowModel> models)
        {
            _currentModels = models;
            _lastPublishedLayoutMetrics = null;
            UpdateListViewport();
            SyncRows();
            QueueLayoutMetricsUpdate();
            QueueScrollChromeUpdate();
        }

        public Size MeasureDesiredSize()
        {
            Size desiredSize = MeasurePanelDesiredSize();
            return new Size(
                Math.Max(IslandConfig.SessionPickerOverlayWidth, desiredSize.Width),
                Math.Max(1.0, desiredSize.Height));
        }

        public void PrepareShowAnimation()
        {
            EnsureChromeAnimationVisual();
            EnsurePanelAnimationVisual();
            EnsureSessionListAnimationVisual();
            StopChromeAnimations();
            StopPanelAnimations();
            StopSessionListAnimations();
            UpdatePanelAnimationCenterPoint();

            if (_chromeVisual != null)
            {
                _chromeVisual.Opacity = 1.0f;
                _chromeVisual.Offset = Vector3.Zero;
            }

            if (_panelVisual == null)
            {
                return;
            }

            _panelVisual.Opacity = (float)IslandConfig.SessionPickerOverlayPanelStartOpacity;
            _panelVisual.Offset = new Vector3(
                0.0f,
                (float)IslandConfig.SessionPickerOverlayPanelStartOffsetY,
                0.0f);

            PrepareSessionListShowAnimation();
        }

        public void StartShowAnimation()
        {
            EnsurePanelAnimationVisual();
            UpdatePanelAnimationCenterPoint();

            if (_panelCompositor == null || _panelVisual == null)
            {
                return;
            }

            CompositionScopedBatch batch = _panelCompositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            _panelVisual.StartAnimation("Opacity", CreatePanelOpacityAnimation(1.0f, IslandConfig.SessionPickerOverlayOpenDurationMs, _panelOpacityEasing));
            _panelVisual.StartAnimation("Offset", CreatePanelOffsetAnimation(
                Vector3.Zero,
                IslandConfig.SessionPickerOverlayOpenDurationMs,
                _panelShowEasing));
            batch.End();
            StartSessionListShowAnimation();
        }

        public void StartHideAnimation(SessionPickerOverlayDismissMotion dismissMotion)
        {
            EnsureChromeAnimationVisual();
            EnsurePanelAnimationVisual();
            EnsureSessionListAnimationVisual();
            StopChromeAnimations();
            StopPanelAnimations();
            StopSessionListAnimations();
            RestoreFullyVisibleAnimatedState();
            UpdatePanelAnimationCenterPoint();

            if (_chromeVisual == null || _panelCompositor == null)
            {
                return;
            }

            _chromeVisual.Opacity = 1.0f;
            _chromeVisual.Offset = Vector3.Zero;
            _chromeVisual.StartAnimation("Opacity", CreateChromeOpacityAnimation(dismissMotion.TargetOpacity, dismissMotion.DurationMs));
            _chromeVisual.StartAnimation("Offset", CreateChromeOffsetAnimation(
                new Vector3(0.0f, dismissMotion.OffsetY, 0.0f),
                dismissMotion.DurationMs));
        }

        public void FocusList()
        {
            DispatcherQueue?.TryEnqueue(() =>
            {
                bool hasOverflow = SessionPickerOverlayLayout.HasScrollableOverflow(_currentModels.Count);

                if (SessionList.SelectedItem != null && hasOverflow)
                {
                    SessionList.ScrollIntoView(SessionList.SelectedItem, ScrollIntoViewAlignment.Leading);
                }
                else if (!hasOverflow)
                {
                    ResetSessionListVerticalOffset();
                }

                SessionList.Focus(FocusState.Programmatic);
            });
        }

        private void SyncRows()
        {
            _iconLoadToken++;
            long token = _iconLoadToken;
            string? preferredSelectionKey = (SessionList.SelectedItem as SessionPickerRowVisualModel)?.SessionKey;
            Dictionary<string, SessionPickerRowVisualModel> existingRows = new(StringComparer.Ordinal);
            HashSet<string> activeKeys = new(StringComparer.Ordinal);

            foreach (SessionPickerRowVisualModel row in _rows)
            {
                existingRows[row.SessionKey] = row;
            }

            SessionPickerRowVisualModel? selectedItem = null;

            for (int i = 0; i < _currentModels.Count; i++)
            {
                SessionPickerRowModel model = _currentModels[i];
                activeKeys.Add(model.SessionKey);

                SessionPickerRowVisualModel row;
                if (existingRows.TryGetValue(model.SessionKey, out SessionPickerRowVisualModel? existingRow))
                {
                    row = existingRow;
                    row.Update(model, _primaryColor, _secondaryColor);

                    int currentIndex = _rows.IndexOf(row);
                    if (currentIndex != i)
                    {
                        _rows.Move(currentIndex, i);
                    }
                }
                else
                {
                    row = CreateVisualModel(model);
                    _rows.Insert(i, row);
                }

                row.OuterMargin = GetRowOuterMargin(i, _currentModels.Count);

                if (!string.IsNullOrWhiteSpace(preferredSelectionKey)
                    && string.Equals(row.SessionKey, preferredSelectionKey, StringComparison.Ordinal))
                {
                    selectedItem = row;
                }
                else if (selectedItem == null && row.IsSelected)
                {
                    selectedItem = row;
                }

                if (row.NeedsIconLoad)
                {
                    _ = LoadIconAsync(row, token);
                }
            }

            for (int i = _rows.Count - 1; i >= 0; i--)
            {
                if (!activeKeys.Contains(_rows[i].SessionKey))
                {
                    _rows.RemoveAt(i);
                }
            }

            SessionList.SelectedItem = selectedItem;
            UpdateRowSelectionVisuals(selectedItem);
            QueueScrollChromeUpdate();
        }

        private void QueueLayoutMetricsUpdate()
            => DispatcherQueue?.TryEnqueue(PublishLayoutMetrics);

        private void PublishLayoutMetrics()
        {
            if (TryBuildLayoutMetrics(out SessionPickerOverlayLayoutMetrics metrics))
            {
                if (_lastPublishedLayoutMetrics.HasValue
                    && _lastPublishedLayoutMetrics.Value.Equals(metrics))
                {
                    return;
                }

                _lastPublishedLayoutMetrics = metrics;
                LayoutMetricsChanged?.Invoke(metrics);
            }
        }

        private bool TryBuildLayoutMetrics(out SessionPickerOverlayLayoutMetrics metrics)
        {
            metrics = default;
            if (_currentModels.Count == 0)
            {
                return false;
            }

            Size desiredSize = MeasurePanelDesiredSize();
            metrics = new SessionPickerOverlayLayoutMetrics(
                Width: Math.Max(IslandConfig.SessionPickerOverlayWidth, desiredSize.Width),
                Height: Math.Max(1.0, desiredSize.Height));
            return true;
        }

        private SessionPickerRowVisualModel CreateVisualModel(SessionPickerRowModel model)
        {
            SessionPickerRowVisualModel row = new(
                model.SessionKey,
                model.SourceAppId,
                model.SourceName,
                model.Title,
                model.Subtitle,
                model.StatusText,
                SessionPickerRowProjector.ResolveMonogram(model.SourceName),
                new SolidColorBrush(_primaryColor),
                new SolidColorBrush(_secondaryColor));
            row.ApplySelectionState(model.IsSelected, _primaryColor, _secondaryColor);
            return row;
        }

        private async Task LoadIconAsync(SessionPickerRowVisualModel row, long token)
        {
            try
            {
                string sourceAppId = row.SourceAppId;
                ImageSource? icon = await IconResolver.ResolveAsync(sourceAppId);
                if (token != _iconLoadToken
                    || icon == null
                    || !string.Equals(row.SourceAppId, sourceAppId, StringComparison.Ordinal))
                {
                    return;
                }

                row.IconSource = icon;
            }
            catch
            {
                // Keep the monogram fallback on icon resolution failure.
            }
        }

        private void ApplyPanelColors()
        {
            byte alpha = (byte)Math.Clamp(Math.Max((int)_surfaceColor.A, 228), 0, 255);
            Color backgroundColor = Color.FromArgb(alpha, _surfaceColor.R, _surfaceColor.G, _surfaceColor.B);
            Color borderColor = Color.FromArgb(72, _secondaryColor.R, _secondaryColor.G, _secondaryColor.B);
            RootGrid.Background = new SolidColorBrush(backgroundColor);
            ChromeRoot.Background = new SolidColorBrush(backgroundColor);
            HostSurface.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            HostSurface.BorderBrush = new SolidColorBrush(borderColor);
            HostSurface.BorderThickness = new Thickness(1);
            PanelBorder.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            PanelBorder.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            TopEdgeFade.Background = CreateEdgeFadeBrush(backgroundColor, isTopEdge: true);
            BottomEdgeFade.Background = CreateEdgeFadeBrush(backgroundColor, isTopEdge: false);
            ScrollIndicator.Background = new SolidColorBrush(Color.FromArgb(
                148,
                _secondaryColor.R,
                _secondaryColor.G,
                _secondaryColor.B));
        }

        private void UpdateListViewport()
        {
            if (SessionPickerOverlayLayout.HasScrollableOverflow(_currentModels.Count)
                || _currentModels.Count <= 0)
            {
                _sessionListViewportCompensation = 0.0;
            }

            SessionPickerOverlayViewportMetrics metrics = SessionPickerOverlayLayout.GetViewportMetrics(
                _currentModels.Count,
                _sessionListViewportCompensation);
            ApplyScrollAffordanceLayout(metrics);
            TopEdgeFade.Height = IslandConfig.SessionPickerOverlayEdgeFadeHeight;
            BottomEdgeFade.Height = IslandConfig.SessionPickerOverlayEdgeFadeHeight;
            SessionList.Height = metrics.Height;
            SessionList.MaxHeight = metrics.Height;
        }

        private void ApplyScrollAffordanceLayout(SessionPickerOverlayViewportMetrics metrics)
        {
            PanelBorder.Padding = new Thickness(
                IslandConfig.SessionPickerOverlayPanelPadding,
                IslandConfig.SessionPickerOverlayPanelPadding,
                metrics.PanelRightPadding,
                IslandConfig.SessionPickerOverlayPanelPadding);
            SessionList.Padding = new Thickness(0, metrics.EdgeInset, 0, metrics.EdgeInset);
            SessionList.Margin = new Thickness(0, 0, metrics.ListRightMargin, 0);
            ApplySessionListScrollMode(metrics.ShowsScrollAffordance);
            Visibility affordanceVisibility = metrics.ShowsScrollAffordance ? Visibility.Visible : Visibility.Collapsed;
            ScrollIndicator.Visibility = affordanceVisibility;
            TopEdgeFade.Visibility = affordanceVisibility;
            BottomEdgeFade.Visibility = affordanceVisibility;
        }

        private void ApplySessionListScrollMode(bool hasOverflow)
        {
            ScrollMode verticalScrollMode = hasOverflow ? ScrollMode.Enabled : ScrollMode.Disabled;
            ScrollViewer.SetVerticalScrollMode(SessionList, verticalScrollMode);
            ScrollViewer.SetIsVerticalRailEnabled(SessionList, hasOverflow);

            if (_sessionListScrollViewer == null)
            {
                return;
            }

            _sessionListScrollViewer.VerticalScrollMode = verticalScrollMode;
            _sessionListScrollViewer.IsVerticalRailEnabled = hasOverflow;

            if (!hasOverflow)
            {
                ResetSessionListVerticalOffset();
            }
        }

        private Size MeasurePanelDesiredSize()
        {
            PanelBorder.Measure(new Size(IslandConfig.SessionPickerOverlayWidth, double.PositiveInfinity));
            return PanelBorder.DesiredSize;
        }

        private void SessionList_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is SessionPickerRowVisualModel row)
            {
                SessionSelected?.Invoke(row.SessionKey);
            }
        }

        private void SessionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
            => UpdateRowSelectionVisuals(SessionList.SelectedItem as SessionPickerRowVisualModel);

        private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
            => HandleListKeyDown(e);

        private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            QueueLayoutMetricsUpdate();
            QueueScrollChromeUpdate();
        }

        private void PanelBorder_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdatePanelAnimationCenterPoint();
            QueueLayoutMetricsUpdate();
        }

        private void SessionList_KeyDown(object sender, KeyRoutedEventArgs e)
            => HandleListKeyDown(e);

        private void SessionList_Loaded(object sender, RoutedEventArgs e)
        {
            EnsurePanelAnimationVisual();
            EnsureSessionListAnimationVisual();
            EnsureSessionListScrollViewer();
            NormalizeNonScrollableSessionListState();
            QueueLayoutMetricsUpdate();
            QueueScrollChromeUpdate();
        }

        private void SessionList_Unloaded(object sender, RoutedEventArgs e)
            => DetachSessionListScrollViewer();

        private void HandleListKeyDown(KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                DismissRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (e.Key == VirtualKey.Enter
                && SessionList.SelectedItem is SessionPickerRowVisualModel row)
            {
                e.Handled = true;
                SessionSelected?.Invoke(row.SessionKey);
            }
        }

        private void UpdateRowSelectionVisuals(SessionPickerRowVisualModel? selectedRow)
        {
            foreach (SessionPickerRowVisualModel row in _rows)
            {
                row.ApplySelectionState(
                    ReferenceEquals(row, selectedRow),
                    _primaryColor,
                    _secondaryColor);
            }
        }

        private static Thickness GetRowOuterMargin(int index, int totalCount)
            => index >= 0 && index < totalCount - 1
                ? new Thickness(0, 0, 0, IslandConfig.SessionPickerOverlayItemSpacing)
                : new Thickness(0);

        private void EnsureSessionListScrollViewer()
        {
            if (_sessionListScrollViewer != null)
            {
                return;
            }

            _sessionListScrollViewer = FindScrollViewer(SessionList);
            if (_sessionListScrollViewer != null)
            {
                _sessionListScrollViewer.ViewChanged += SessionListScrollViewer_ViewChanged;
                ApplySessionListScrollMode(SessionPickerOverlayLayout.HasScrollableOverflow(_currentModels.Count));
            }
        }

        private void DetachSessionListScrollViewer()
        {
            if (_sessionListScrollViewer == null)
            {
                return;
            }

            _sessionListScrollViewer.ViewChanged -= SessionListScrollViewer_ViewChanged;
            _sessionListScrollViewer = null;
        }

        private void SessionListScrollViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
            => UpdateScrollEdgeChrome();

        private void QueueScrollChromeUpdate()
            => DispatcherQueue?.TryEnqueue(UpdateScrollEdgeChrome);

        private void UpdateScrollEdgeChrome()
        {
            EnsureSessionListScrollViewer();

            if (_currentModels.Count == 0
                || _sessionListScrollViewer == null
                || !SessionPickerOverlayLayout.HasScrollableOverflow(_currentModels.Count))
            {
                NormalizeNonScrollableSessionListState();
                SetEdgeFadeState(0.0, 0.0);
                SetScrollIndicatorState(0.0, 0.0, 0.0);
                return;
            }

            double scrollableHeight = _sessionListScrollViewer.ScrollableHeight;
            if (scrollableHeight <= ScrollFadeEpsilon)
            {
                SetEdgeFadeState(0.0, 0.0);
                SetScrollIndicatorState(0.0, 0.0, 0.0);
                return;
            }

            UpdateScrollIndicator();

            double verticalOffset = _sessionListScrollViewer.VerticalOffset;
            double topOpacity = verticalOffset > ScrollFadeEpsilon ? ScrollFadeOpacity : 0.0;
            double bottomOpacity = verticalOffset < scrollableHeight - ScrollFadeEpsilon
                ? ScrollFadeOpacity
                : 0.0;

            SetEdgeFadeState(topOpacity, bottomOpacity);
        }

        private void SetEdgeFadeState(double topOpacity, double bottomOpacity)
        {
            TopEdgeFade.Opacity = topOpacity;
            BottomEdgeFade.Opacity = bottomOpacity;
        }

        private void NormalizeNonScrollableSessionListState()
        {
            if (_sessionListScrollViewer == null
                || _isCorrectingSessionListOffset
                || SessionPickerOverlayLayout.HasScrollableOverflow(_currentModels.Count))
            {
                return;
            }

            double verticalOffset = _sessionListScrollViewer.VerticalOffset;
            double scrollableHeight = _sessionListScrollViewer.ScrollableHeight;
            if (verticalOffset <= ScrollFadeEpsilon && scrollableHeight <= ScrollFadeEpsilon)
            {
                return;
            }

            if (TryApplyNonScrollableViewportCompensation(scrollableHeight))
            {
                return;
            }

            ResetSessionListVerticalOffset(scheduleAsync: true);
        }

        private bool TryApplyNonScrollableViewportCompensation(double scrollableHeight)
        {
            if (scrollableHeight <= ScrollFadeEpsilon
                || scrollableHeight > IslandConfig.SessionPickerOverlayNonScrollableViewportCompensationLimit)
            {
                return false;
            }

            double targetCompensation = Math.Max(_sessionListViewportCompensation, scrollableHeight);
            if (Math.Abs(targetCompensation - _sessionListViewportCompensation) <= 0.01)
            {
                return false;
            }

            _sessionListViewportCompensation = targetCompensation;
            UpdateListViewport();
            QueueLayoutMetricsUpdate();
            QueueScrollChromeUpdate();
            return true;
        }

        private void ResetSessionListVerticalOffset(bool scheduleAsync = false)
        {
            if (_sessionListScrollViewer == null)
            {
                return;
            }

            if (!scheduleAsync)
            {
                _sessionListScrollViewer.ChangeView(null, 0.0, null, true);
                return;
            }

            if (_isCorrectingSessionListOffset)
            {
                return;
            }

            _isCorrectingSessionListOffset = true;
            if (DispatcherQueue?.TryEnqueue(() =>
                {
                    try
                    {
                        _sessionListScrollViewer?.ChangeView(null, 0.0, null, true);
                    }
                    finally
                    {
                        _isCorrectingSessionListOffset = false;
                    }
                }) == true)
            {
                return;
            }

            _isCorrectingSessionListOffset = false;
            _sessionListScrollViewer.ChangeView(null, 0.0, null, true);
        }

        private void UpdateScrollIndicator()
        {
            if (_sessionListScrollViewer == null)
            {
                SetScrollIndicatorState(0.0, 0.0, 0.0);
                return;
            }

            if (!SessionPickerOverlayLayout.TryGetScrollIndicatorMetrics(
                viewportHeight: Math.Max(
                    0.0,
                    _sessionListScrollViewer.ViewportHeight > 0.0
                        ? _sessionListScrollViewer.ViewportHeight
                        : SessionList.ActualHeight),
                scrollableHeight: _sessionListScrollViewer.ScrollableHeight,
                verticalOffset: _sessionListScrollViewer.VerticalOffset,
                minThumbHeight: IslandConfig.SessionPickerOverlayScrollIndicatorMinHeight,
                out SessionPickerOverlayScrollIndicatorMetrics metrics))
            {
                SetScrollIndicatorState(0.0, 0.0, 0.0);
                return;
            }

            SetScrollIndicatorState(1.0, metrics.Height, metrics.OffsetY);
        }

        private void SetScrollIndicatorState(double opacity, double height, double offsetY)
        {
            ScrollIndicator.Opacity = opacity;
            ScrollIndicator.Height = height;
            ScrollIndicatorTransform.Y = offsetY;
        }

        private static ScrollViewer? FindScrollViewer(DependencyObject root)
        {
            if (root is ScrollViewer scrollViewer)
            {
                return scrollViewer;
            }

            int childCount = VisualTreeHelper.GetChildrenCount(root);
            for (int childIndex = 0; childIndex < childCount; childIndex++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, childIndex);
                ScrollViewer? match = FindScrollViewer(child);
                if (match != null)
                {
                    return match;
                }
            }

            return null;
        }

        private void EnsureChromeAnimationVisual()
        {
            if (_chromeVisual != null)
            {
                return;
            }

            _chromeVisual = ElementCompositionPreview.GetElementVisual(ChromeRoot);
            _panelCompositor = _chromeVisual.Compositor;
        }

        private void EnsurePanelAnimationVisual()
        {
            if (_panelVisual != null)
            {
                return;
            }

            EnsureChromeAnimationVisual();
            Compositor? compositor = _panelCompositor;
            if (compositor == null)
            {
                return;
            }

            _panelVisual = ElementCompositionPreview.GetElementVisual(PanelBorder);
            _panelShowEasing = compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.18f, 0.9f),
                new Vector2(0.2f, 1.0f));
            _panelOpacityEasing = compositor.CreateCubicBezierEasingFunction(
                new Vector2(0.2f, 0.0f),
                new Vector2(0.0f, 1.0f));
            UpdatePanelAnimationCenterPoint();
        }

        private void EnsureSessionListAnimationVisual()
        {
            if (_sessionListVisual != null && _sessionListClip != null)
            {
                return;
            }

            EnsurePanelAnimationVisual();
            if (_panelCompositor == null)
            {
                return;
            }

            _sessionListVisual = ElementCompositionPreview.GetElementVisual(SessionList);
            _sessionListClip = _panelCompositor.CreateInsetClip();
            _sessionListVisual.Clip = _sessionListClip;
            _listRevealEasing = _panelCompositor.CreateCubicBezierEasingFunction(
                new Vector2(0.22f, 0.84f),
                new Vector2(0.18f, 1.0f));
        }

        private void StopChromeAnimations()
        {
            if (_chromeVisual == null)
            {
                return;
            }

            _chromeVisual.StopAnimation("Opacity");
            _chromeVisual.StopAnimation("Offset");
        }

        private void UpdatePanelAnimationCenterPoint()
        {
            if (_panelVisual == null)
            {
                return;
            }

            float width = (float)Math.Max(0.0, PanelBorder.ActualWidth);
            _panelVisual.CenterPoint = new Vector3(width * 0.5f, 0.0f, 0.0f);
        }

        private void StopPanelAnimations()
        {
            if (_panelVisual == null)
            {
                return;
            }

            _panelVisual.StopAnimation("Opacity");
            _panelVisual.StopAnimation("Offset");
        }

        private void PrepareSessionListShowAnimation()
        {
            EnsureSessionListAnimationVisual();
            if (_sessionListVisual == null || _sessionListClip == null)
            {
                return;
            }

            ApplySessionListRevealStartState();
        }

        private void StartSessionListShowAnimation()
        {
            EnsureSessionListAnimationVisual();
            if (_sessionListVisual == null || _sessionListClip == null || _panelCompositor == null)
            {
                return;
            }

            ApplySessionListRevealStartState();

            ScalarKeyFrameAnimation listOpacityAnimation = _panelCompositor.CreateScalarKeyFrameAnimation();
            listOpacityAnimation.Duration = TimeSpan.FromMilliseconds(IslandConfig.SessionPickerOverlayListRevealDurationMs);
            listOpacityAnimation.DelayTime = TimeSpan.FromMilliseconds(IslandConfig.SessionPickerOverlayListRevealDelayMs);
            listOpacityAnimation.InsertKeyFrame(1.0f, 1.0f, _panelOpacityEasing);

            ScalarKeyFrameAnimation listClipAnimation = _panelCompositor.CreateScalarKeyFrameAnimation();
            listClipAnimation.Duration = TimeSpan.FromMilliseconds(IslandConfig.SessionPickerOverlayListRevealDurationMs);
            listClipAnimation.DelayTime = TimeSpan.FromMilliseconds(IslandConfig.SessionPickerOverlayListRevealDelayMs);
            listClipAnimation.InsertKeyFrame(1.0f, 0.0f, _listRevealEasing);

            Vector3KeyFrameAnimation listOffsetAnimation = _panelCompositor.CreateVector3KeyFrameAnimation();
            listOffsetAnimation.Duration = TimeSpan.FromMilliseconds(IslandConfig.SessionPickerOverlayListRevealDurationMs);
            listOffsetAnimation.DelayTime = TimeSpan.FromMilliseconds(IslandConfig.SessionPickerOverlayListRevealDelayMs);
            listOffsetAnimation.InsertKeyFrame(1.0f, Vector3.Zero, _listRevealEasing);

            _sessionListVisual.StartAnimation("Opacity", listOpacityAnimation);
            _sessionListVisual.StartAnimation("Offset", listOffsetAnimation);
            _sessionListClip.StartAnimation("BottomInset", listClipAnimation);
        }

        private void StopSessionListAnimations()
        {
            _sessionListVisual?.StopAnimation("Opacity");
            _sessionListVisual?.StopAnimation("Offset");
            _sessionListClip?.StopAnimation("BottomInset");
        }

        private void RestoreFullyVisibleAnimatedState()
        {
            if (_panelVisual != null)
            {
                _panelVisual.Opacity = 1.0f;
                _panelVisual.Offset = Vector3.Zero;
            }

            if (_sessionListVisual != null)
            {
                _sessionListVisual.Opacity = 1.0f;
                _sessionListVisual.Offset = Vector3.Zero;
            }

            if (_sessionListClip != null)
            {
                SetSessionListClipInsets(0.0f);
            }
        }

        private void ApplySessionListRevealStartState()
        {
            if (_sessionListVisual == null || _sessionListClip == null)
            {
                return;
            }

            UpdateLayout();

            _sessionListVisual.Opacity = (float)IslandConfig.SessionPickerOverlayListStartOpacity;
            _sessionListVisual.Offset = new Vector3(
                0.0f,
                (float)IslandConfig.SessionPickerOverlayListStartOffsetY,
                0.0f);
            SetSessionListClipInsets(GetListRevealStartInset(GetSessionListActualHeight()));
        }

        private float GetSessionListActualHeight()
            => (float)Math.Max(0.0, SessionList.ActualHeight);

        private void SetSessionListClipInsets(float bottomInset)
        {
            if (_sessionListClip == null)
            {
                return;
            }

            _sessionListClip.LeftInset = 0.0f;
            _sessionListClip.TopInset = 0.0f;
            _sessionListClip.RightInset = 0.0f;
            _sessionListClip.BottomInset = bottomInset;
        }

        private static float GetListRevealStartInset(float listHeight)
        {
            if (listHeight <= 0.0f)
            {
                return 0.0f;
            }

            float inset = (float)Math.Clamp(
                listHeight * IslandConfig.SessionPickerOverlayListStartInsetRatio,
                IslandConfig.SessionPickerOverlayListStartInsetMin,
                IslandConfig.SessionPickerOverlayListStartInsetMax);
            return Math.Clamp(inset, 0.0f, listHeight);
        }

        private ScalarKeyFrameAnimation CreatePanelOpacityAnimation(float targetOpacity, int durationMs, CompositionEasingFunction? easing)
        {
            ScalarKeyFrameAnimation animation = _panelCompositor!.CreateScalarKeyFrameAnimation();
            animation.Duration = TimeSpan.FromMilliseconds(durationMs);
            animation.InsertKeyFrame(1.0f, targetOpacity, easing);
            return animation;
        }

        private Vector3KeyFrameAnimation CreatePanelOffsetAnimation(Vector3 targetOffset, int durationMs, CompositionEasingFunction? easing)
        {
            Vector3KeyFrameAnimation animation = _panelCompositor!.CreateVector3KeyFrameAnimation();
            animation.Duration = TimeSpan.FromMilliseconds(durationMs);
            animation.InsertKeyFrame(1.0f, targetOffset, easing);
            return animation;
        }

        private ScalarKeyFrameAnimation CreateChromeOpacityAnimation(float targetOpacity, int durationMs)
        {
            ScalarKeyFrameAnimation animation = _panelCompositor!.CreateScalarKeyFrameAnimation();
            animation.Duration = TimeSpan.FromMilliseconds(durationMs);
            animation.InsertKeyFrame(1.0f, targetOpacity, _panelOpacityEasing);
            return animation;
        }

        private Vector3KeyFrameAnimation CreateChromeOffsetAnimation(Vector3 targetOffset, int durationMs)
        {
            Vector3KeyFrameAnimation animation = _panelCompositor!.CreateVector3KeyFrameAnimation();
            animation.Duration = TimeSpan.FromMilliseconds(durationMs);
            animation.InsertKeyFrame(1.0f, targetOffset, _panelShowEasing);
            return animation;
        }

        private static Brush CreateEdgeFadeBrush(Color backgroundColor, bool isTopEdge)
        {
            LinearGradientBrush brush = new()
            {
                StartPoint = new Point(0.5, 0),
                EndPoint = new Point(0.5, 1)
            };

            if (isTopEdge)
            {
                brush.GradientStops.Add(new GradientStop
                {
                    Color = backgroundColor,
                    Offset = 0
                });
                brush.GradientStops.Add(new GradientStop
                {
                    Color = backgroundColor,
                    Offset = 0.35
                });
                brush.GradientStops.Add(new GradientStop
                {
                    Color = Color.FromArgb(0, backgroundColor.R, backgroundColor.G, backgroundColor.B),
                    Offset = 1
                });
            }
            else
            {
                brush.GradientStops.Add(new GradientStop
                {
                    Color = Color.FromArgb(0, backgroundColor.R, backgroundColor.G, backgroundColor.B),
                    Offset = 0
                });
                brush.GradientStops.Add(new GradientStop
                {
                    Color = backgroundColor,
                    Offset = 0.65
                });
                brush.GradientStops.Add(new GradientStop
                {
                    Color = backgroundColor,
                    Offset = 1
                });
            }

            return brush;
        }

        private sealed class SessionPickerRowVisualModel : INotifyPropertyChanged
        {
            private ImageSource? _iconSource;
            private bool _isSelected;
            private string _sourceAppId = string.Empty;
            private string _sourceName = string.Empty;
            private string _title = string.Empty;
            private string _subtitle = string.Empty;
            private string _statusText = string.Empty;
            private string _monogram = string.Empty;
            private Brush _rowBackground = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            private Brush _rowBorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            private Brush _rowHoverBackground = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            private Brush _rowHoverBorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            private Brush _rowPressBackground = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            private Brush _rowPressBorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            private Brush _rowFocusBorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            private Brush _badgeBackground = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            private Brush _badgeBorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            private Brush _statusBackground = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            private Brush _statusBorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            private Thickness _outerMargin;

            public SessionPickerRowVisualModel(
                string sessionKey,
                string sourceAppId,
                string sourceName,
                string title,
                string subtitle,
                string statusText,
                string monogram,
                Brush primaryForeground,
                Brush secondaryForeground)
            {
                SessionKey = sessionKey;
                SourceAppId = sourceAppId;
                SourceName = sourceName;
                Title = title;
                Subtitle = subtitle;
                StatusText = statusText;
                Monogram = monogram;
                PrimaryForeground = primaryForeground;
                SecondaryForeground = secondaryForeground;
            }

            public event PropertyChangedEventHandler? PropertyChanged;

            public string SessionKey { get; }

            public string SourceAppId
            {
                get => _sourceAppId;
                private set => SetString(ref _sourceAppId, value);
            }

            public string SourceName
            {
                get => _sourceName;
                private set => SetString(ref _sourceName, value);
            }

            public string Title
            {
                get => _title;
                private set
                {
                    if (!SetString(ref _title, value))
                    {
                        return;
                    }

                    OnPropertyChanged(nameof(SecondaryLine));
                }
            }

            public string Subtitle
            {
                get => _subtitle;
                private set
                {
                    if (!SetString(ref _subtitle, value))
                    {
                        return;
                    }

                    OnPropertyChanged(nameof(SecondaryLine));
                }
            }

            public string StatusText
            {
                get => _statusText;
                private set
                {
                    if (!SetString(ref _statusText, value))
                    {
                        return;
                    }

                    OnPropertyChanged(nameof(StatusVisibility));
                }
            }

            public bool IsSelected
            {
                get => _isSelected;
                private set
                {
                    if (_isSelected == value)
                    {
                        return;
                    }

                    _isSelected = value;
                    OnPropertyChanged();
                }
            }

            public string Monogram
            {
                get => _monogram;
                private set => SetString(ref _monogram, value);
            }

            public Brush RowBackground
            {
                get => _rowBackground;
                private set => SetBrush(ref _rowBackground, value);
            }

            public Brush RowBorderBrush
            {
                get => _rowBorderBrush;
                private set => SetBrush(ref _rowBorderBrush, value);
            }

            public Brush RowHoverBackground
            {
                get => _rowHoverBackground;
                private set => SetBrush(ref _rowHoverBackground, value);
            }

            public Brush RowHoverBorderBrush
            {
                get => _rowHoverBorderBrush;
                private set => SetBrush(ref _rowHoverBorderBrush, value);
            }

            public Brush RowPressBackground
            {
                get => _rowPressBackground;
                private set => SetBrush(ref _rowPressBackground, value);
            }

            public Brush RowPressBorderBrush
            {
                get => _rowPressBorderBrush;
                private set => SetBrush(ref _rowPressBorderBrush, value);
            }

            public Brush RowFocusBorderBrush
            {
                get => _rowFocusBorderBrush;
                private set => SetBrush(ref _rowFocusBorderBrush, value);
            }

            public Brush BadgeBackground
            {
                get => _badgeBackground;
                private set => SetBrush(ref _badgeBackground, value);
            }

            public Brush BadgeBorderBrush
            {
                get => _badgeBorderBrush;
                private set => SetBrush(ref _badgeBorderBrush, value);
            }

            public Brush PrimaryForeground { get; }

            public Brush SecondaryForeground { get; }

            public Brush StatusBackground
            {
                get => _statusBackground;
                private set => SetBrush(ref _statusBackground, value);
            }

            public Brush StatusBorderBrush
            {
                get => _statusBorderBrush;
                private set => SetBrush(ref _statusBorderBrush, value);
            }

            public Brush StatusForeground => SecondaryForeground;

            public Thickness OuterMargin
            {
                get => _outerMargin;
                set => SetThickness(ref _outerMargin, value);
            }

            public string SecondaryLine
                => string.IsNullOrWhiteSpace(Subtitle)
                    ? Title
                    : $"{Title} · {Subtitle}";

            public Visibility StatusVisibility
                => string.IsNullOrWhiteSpace(StatusText) ? Visibility.Collapsed : Visibility.Visible;

            public Visibility IconVisibility
                => _iconSource == null ? Visibility.Collapsed : Visibility.Visible;

            public Visibility MonogramVisibility
                => _iconSource == null ? Visibility.Visible : Visibility.Collapsed;

            public ImageSource? IconSource
            {
                get => _iconSource;
                set
                {
                    if (ReferenceEquals(_iconSource, value))
                    {
                        return;
                    }

                    _iconSource = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IconVisibility));
                    OnPropertyChanged(nameof(MonogramVisibility));
                }
            }

            public bool NeedsIconLoad
                => !string.IsNullOrWhiteSpace(SourceAppId)
                    && IconSource == null;

            public void Update(SessionPickerRowModel model, Color primaryColor, Color secondaryColor)
            {
                bool sourceChanged = !string.Equals(SourceAppId, model.SourceAppId, StringComparison.Ordinal);
                if (sourceChanged)
                {
                    SourceAppId = model.SourceAppId;
                    IconSource = null;
                }

                SourceName = model.SourceName;
                Title = model.Title;
                Subtitle = model.Subtitle;
                StatusText = model.StatusText;
                Monogram = SessionPickerRowProjector.ResolveMonogram(model.SourceName);
                ApplySelectionState(model.IsSelected, primaryColor, secondaryColor);
            }

            public void ApplySelectionState(bool isSelected, Color primaryColor, Color secondaryColor)
            {
                InteractionSurfacePalette rowPalette = InteractionSurfacePalette.Create(
                    primaryColor,
                    secondaryColor,
                    isSelected ? (byte)34 : (byte)12,
                    isSelected ? (byte)90 : (byte)32,
                    isSelected ? (byte)46 : (byte)18,
                    isSelected ? (byte)108 : (byte)62,
                    isSelected ? (byte)60 : (byte)28,
                    isSelected ? (byte)120 : (byte)76,
                    140);

                IsSelected = isSelected;
                RowBackground = new SolidColorBrush(rowPalette.BackgroundColor);
                RowBorderBrush = new SolidColorBrush(rowPalette.BorderColor);
                RowHoverBackground = new SolidColorBrush(rowPalette.HoverBackgroundColor);
                RowHoverBorderBrush = new SolidColorBrush(rowPalette.HoverBorderColor);
                RowPressBackground = new SolidColorBrush(rowPalette.PressBackgroundColor);
                RowPressBorderBrush = new SolidColorBrush(rowPalette.PressBorderColor);
                RowFocusBorderBrush = new SolidColorBrush(rowPalette.FocusRingColor);
                BadgeBackground = new SolidColorBrush(WithAlpha(primaryColor, isSelected ? (byte)52 : (byte)28));
                BadgeBorderBrush = new SolidColorBrush(WithAlpha(secondaryColor, 54));
                StatusBackground = new SolidColorBrush(WithAlpha(primaryColor, isSelected ? (byte)34 : (byte)18));
                StatusBorderBrush = new SolidColorBrush(WithAlpha(secondaryColor, 42));
            }

            private static Color WithAlpha(Color color, byte alpha)
                => Color.FromArgb(alpha, color.R, color.G, color.B);

            private bool SetString(ref string field, string value, [CallerMemberName] string? propertyName = null)
            {
                if (string.Equals(field, value, StringComparison.Ordinal))
                {
                    return false;
                }

                field = value;
                OnPropertyChanged(propertyName);
                return true;
            }

            private void SetBrush(ref Brush field, Brush value, [CallerMemberName] string? propertyName = null)
            {
                if (ReferenceEquals(field, value))
                {
                    return;
                }

                field = value;
                OnPropertyChanged(propertyName);
            }

            private void SetThickness(ref Thickness field, Thickness value, [CallerMemberName] string? propertyName = null)
            {
                if (field.Equals(value))
                {
                    return;
                }

                field = value;
                OnPropertyChanged(propertyName);
            }

            private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
                => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
