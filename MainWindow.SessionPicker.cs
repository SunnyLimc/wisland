using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Windows.Graphics;
using wisland.Helpers;
using wisland.Models;
using wisland.Services;

namespace wisland
{
    public sealed partial class MainWindow
    {
        private SessionPickerWindow? _sessionPickerWindow;
        private bool _isSessionPickerOpen;
        private SessionPickerOverlayLayoutMetrics? _sessionPickerLayoutMetrics;
        private WindowFrameInsets? _mainWindowFrameInsets;
        private SessionPickerOverlayAnimationPhase _sessionPickerOverlayAnimationPhase;
        private TimeSpan? _sessionPickerOverlayAnimationStartTime;
        private RectInt32 _sessionPickerOverlayAnimationFromBounds;
        private RectInt32 _sessionPickerOverlayAnimationToBounds;
        private RectInt32? _sessionPickerOverlayPresentedBounds;
        private bool _sessionPickerOverlayCloseReconcileHover = true;
        private int _sessionPickerOverlayCloseDurationMs = SessionPickerOverlayDismissMotion.FromKind(
            SessionPickerOverlayDismissKind.Passive).DurationMs;

        private bool HasBlockingSurfaceOpen
            => _isContextFlyoutOpen || _isSessionPickerOpen;

        private bool IsSessionPickerOverlayAnimating
            => _sessionPickerOverlayAnimationPhase != SessionPickerOverlayAnimationPhase.None;

        private void InitializeSessionPickerOverlay()
        {
            ExpandedContent.SessionPickerToggleRequested += ExpandedContent_SessionPickerToggleRequested;
        }

        private void ApplySessionPickerAppearance(IslandVisualTokens tokens)
            => _sessionPickerWindow?.View.SetColors(
                tokens.PrimaryTextColor,
                tokens.SecondaryTextColor,
                tokens.SurfaceColor);

        private SessionPickerWindow EnsureSessionPickerWindow()
        {
            if (_sessionPickerWindow != null)
            {
                return _sessionPickerWindow;
            }

            SessionPickerWindow sessionPickerWindow = new();
            sessionPickerWindow.DismissRequested += SessionPickerWindow_DismissRequested;
            sessionPickerWindow.SessionSelected += SessionPickerWindow_SessionSelected;
            sessionPickerWindow.Closed += SessionPickerWindow_Closed;
            sessionPickerWindow.View.LayoutMetricsChanged += SessionPickerView_LayoutMetricsChanged;
            _sessionPickerWindow = sessionPickerWindow;

            if (_currentVisualTokens.HasValue)
            {
                ApplySessionPickerAppearance(_currentVisualTokens.Value);
            }

            return sessionPickerWindow;
        }

        private void DisposeSessionPickerOverlay()
        {
            SessionPickerWindow? sessionPickerWindow = _sessionPickerWindow;
            _sessionPickerWindow = null;
            ClearSessionPickerOverlayState(reconcileHover: false);

            if (sessionPickerWindow == null)
            {
                return;
            }

            sessionPickerWindow.DismissRequested -= SessionPickerWindow_DismissRequested;
            sessionPickerWindow.SessionSelected -= SessionPickerWindow_SessionSelected;
            sessionPickerWindow.Closed -= SessionPickerWindow_Closed;
            sessionPickerWindow.View.LayoutMetricsChanged -= SessionPickerView_LayoutMetricsChanged;
            sessionPickerWindow.CloseOverlayWindow();
        }

        private void ClearSessionPickerOverlayState(bool reconcileHover)
        {
            bool wasOpen = _isSessionPickerOpen;
            _isSessionPickerOpen = false;
            _sessionPickerLayoutMetrics = null;
            _controller.IsTransientSurfaceOpen = false;
            ExpandedContent.SetSessionPickerExpanded(false, useTransitions: false);
            ResetSessionPickerOverlayAnimationState();

            if (_isClosed)
            {
                return;
            }

            UpdateState();

            if (reconcileHover && wasOpen)
            {
                ReconcileHoverStateAfterTransientSurfaceClosed();
            }
        }

        private void ExpandedContent_SessionPickerToggleRequested(object? sender, EventArgs e)
        {
            if (_isSessionPickerOpen)
            {
                HideSessionPickerOverlay(dismissKind: SessionPickerOverlayDismissKind.Toggle);
                return;
            }

            DisplayedMediaContext context = ResolveDisplayedMediaContext();
            if (!CanShowSessionPicker(context))
            {
                return;
            }

            ShowSessionPickerOverlay(context);
        }

        private void SessionPickerWindow_DismissRequested(object? sender, EventArgs e)
            => HideSessionPickerOverlay(dismissKind: SessionPickerOverlayDismissKind.Passive);

        private void SessionPickerWindow_SessionSelected(string sessionKey)
        {
            HideSessionPickerOverlay(
                reconcileHover: false,
                dismissKind: SessionPickerOverlayDismissKind.Selection);
            SelectSession(sessionKey, GetDirectionToSession(sessionKey));
        }

        private bool CanShowSessionPicker(DisplayedMediaContext context)
            => !_isClosed
                && !_controller.IsNotifying
                && !_controller.IsDragging
                && _controller.Current.ExpandedOpacity > IslandConfig.HitTestOpacityThreshold
                && context.DisplayedSession.HasValue
                && context.OrderedSessions.Count > 1;

        private void ShowSessionPickerOverlay(DisplayedMediaContext context)
        {
            SessionPickerWindow sessionPickerWindow = EnsureSessionPickerWindow();

            _sessionPickerLayoutMetrics = null;
            IReadOnlyList<SessionPickerRowModel> rows = SessionPickerRowProjector.Project(
                context.OrderedSessions,
                context.DisplayedSession?.SessionKey);

            sessionPickerWindow.View.SetRows(rows);
            if (!TryGetSessionPickerAnimatedBounds(out RectInt32 startBounds, out RectInt32 endBounds))
            {
                return;
            }

            Logger.Debug($"Session picker overlay opening with {rows.Count} row(s)");
            _isSessionPickerOpen = true;
            _controller.IsTransientSurfaceOpen = true;
            ExpandedContent.SetSessionPickerExpanded(
                true,
                useTransitions: true,
                durationOverrideMs: IslandConfig.SessionPickerOverlayOpenDurationMs);
            BeginSessionPickerOpenAnimation(startBounds, endBounds);
            UpdateState();
        }

        private void HideSessionPickerOverlay(
            bool reconcileHover = true,
            SessionPickerOverlayDismissKind dismissKind = SessionPickerOverlayDismissKind.Passive)
        {
            if (!_isSessionPickerOpen)
            {
                return;
            }

            Logger.Debug($"Session picker overlay closing (dismiss={dismissKind})");
            SessionPickerOverlayDismissMotion dismissMotion = SessionPickerOverlayDismissMotion.FromKind(dismissKind);

            ExpandedContent.SetSessionPickerExpanded(
                false,
                useTransitions: true,
                durationOverrideMs: dismissMotion.DurationMs);

            if (TryBeginSessionPickerCloseAnimation(reconcileHover, dismissMotion))
            {
                return;
            }

            _sessionPickerWindow?.HideOverlay();
            ClearSessionPickerOverlayState(reconcileHover);
        }

        private void SyncSessionPickerOverlay(DisplayedMediaContext context)
        {
            if (!_isSessionPickerOpen)
            {
                return;
            }

            if (!CanShowSessionPicker(context))
            {
                HideSessionPickerOverlay(reconcileHover: false);
                return;
            }

            _sessionPickerWindow?.View.SetRows(SessionPickerRowProjector.Project(
                context.OrderedSessions,
                context.DisplayedSession?.SessionKey));
            UpdateSessionPickerOverlayPlacement();
        }

        private void UpdateSessionPickerOverlayPlacement()
        {
            if (!_isSessionPickerOpen || _sessionPickerWindow == null)
            {
                return;
            }

            if (TryGetSessionPickerWindowBounds(out RectInt32 bounds))
            {
                if (_sessionPickerOverlayAnimationPhase == SessionPickerOverlayAnimationPhase.Opening)
                {
                    _sessionPickerOverlayAnimationToBounds = bounds;
                    return;
                }

                if (_sessionPickerOverlayAnimationPhase == SessionPickerOverlayAnimationPhase.Closing)
                {
                    return;
                }

                _sessionPickerWindow.MoveOverlay(bounds);
                _sessionPickerOverlayPresentedBounds = bounds;
            }
        }

        private bool TryGetSessionPickerWindowBounds(out RectInt32 bounds)
        {
            bounds = default;
            if (_sessionPickerWindow == null
                || !TryGetSessionPickerAnchorPhysicalBounds(out RectInt32 anchorBounds))
            {
                return false;
            }

            Size desiredSize = _sessionPickerWindow.View.MeasureDesiredSize();
            double widthLogical = _sessionPickerLayoutMetrics?.Width
                ?? Math.Max(IslandConfig.SessionPickerOverlayWidth, desiredSize.Width);
            double heightLogical = _sessionPickerLayoutMetrics?.Height
                ?? Math.Max(1.0, desiredSize.Height);
            int overlayWidth = GetPhysicalPixels(widthLogical, _dpiScale);
            int overlayHeight = GetPhysicalPixels(heightLogical, _dpiScale);
            int gap = GetPhysicalPixels(IslandConfig.SessionPickerOverlayWindowGap, _dpiScale);
            int margin = GetPhysicalPixels(IslandConfig.SessionPickerOverlayScreenMargin, _dpiScale);
            RectInt32 workArea = GetCurrentDisplayWorkArea();

            bounds = SessionPickerPlacementResolver.Resolve(
                anchorBounds,
                workArea,
                overlayWidth,
                overlayHeight,
                gap,
                margin);
            return true;
        }

        private bool TryGetSessionPickerAnchorPhysicalBounds(out RectInt32 anchorBounds)
        {
            anchorBounds = default;
            Rect anchorLogicalBounds = ExpandedContent.GetSessionPickerAnchorBounds(RootGrid);
            if (anchorLogicalBounds.Width <= 0 || anchorLogicalBounds.Height <= 0)
            {
                return false;
            }

            WindowFrameInsets frameInsets = GetMainWindowFrameInsets();
            int clientOriginX = _lastPhysX + frameInsets.Left;
            int clientOriginY = _lastPhysY + frameInsets.Top;
            int left = clientOriginX + (int)Math.Round(anchorLogicalBounds.X * _dpiScale);
            int top = clientOriginY + (int)Math.Round(anchorLogicalBounds.Y * _dpiScale);
            int width = Math.Max(1, (int)Math.Ceiling(anchorLogicalBounds.Width * _dpiScale));
            int height = Math.Max(1, (int)Math.Ceiling(anchorLogicalBounds.Height * _dpiScale));

            anchorBounds = new RectInt32(left, top, width, height);
            return true;
        }

        private void ReconcileHoverStateAfterTransientSurfaceClosed()
        {
            if (_isClosed || HasBlockingSurfaceOpen)
            {
                return;
            }

            GetCursorPos(out var cursorPoint);
            if (IsCursorWithinIslandBounds(cursorPoint, 0))
            {
                SetHoverMode(HoverMode.PointerActive);
                return;
            }

            _hoverDebounceTimer.Stop();
            _dockedHoverDelayTimer.Stop();
            SetHoverMode(HoverMode.None);
        }

        private void SessionPickerView_LayoutMetricsChanged(SessionPickerOverlayLayoutMetrics metrics)
        {
            _sessionPickerLayoutMetrics = metrics;

            if (_isSessionPickerOpen)
            {
                UpdateSessionPickerOverlayPlacement();
            }
        }

        private void SessionPickerWindow_Closed(object sender, WindowEventArgs args)
        {
            if (!ReferenceEquals(sender, _sessionPickerWindow))
            {
                return;
            }

            if (_sessionPickerWindow == null)
            {
                return;
            }

            SessionPickerWindow sessionPickerWindow = _sessionPickerWindow;
            _sessionPickerWindow = null;
            sessionPickerWindow.DismissRequested -= SessionPickerWindow_DismissRequested;
            sessionPickerWindow.SessionSelected -= SessionPickerWindow_SessionSelected;
            sessionPickerWindow.Closed -= SessionPickerWindow_Closed;
            sessionPickerWindow.View.LayoutMetricsChanged -= SessionPickerView_LayoutMetricsChanged;
            ClearSessionPickerOverlayState(reconcileHover: true);
        }

        private bool TryGetSessionPickerAnimatedBounds(out RectInt32 startBounds, out RectInt32 endBounds)
        {
            startBounds = default;
            endBounds = default;

            if (!TryGetSessionPickerWindowBounds(out endBounds)
                || !TryGetSessionPickerAnchorPhysicalBounds(out RectInt32 anchorBounds))
            {
                return false;
            }

            startBounds = BuildSessionPickerAnimationStartBounds(anchorBounds, endBounds);
            return true;
        }

        private RectInt32 BuildSessionPickerAnimationStartBounds(RectInt32 anchorBounds, RectInt32 endBounds)
        {
            int minStartHeight = GetPhysicalPixels(IslandConfig.SessionPickerOverlayAnimationStartMinHeight, _dpiScale);
            int maxStartHeight = GetPhysicalPixels(IslandConfig.SessionPickerOverlayAnimationStartMaxHeight, _dpiScale);
            int screenMargin = GetPhysicalPixels(IslandConfig.SessionPickerOverlayScreenMargin, _dpiScale);
            RectInt32 workArea = GetCurrentDisplayWorkArea();
            int startWidth = Math.Max(1, (int)Math.Round(
                endBounds.Width * IslandConfig.SessionPickerOverlayAnimationStartWidthScale));
            int scaledStartHeight = Math.Max(1, (int)Math.Round(
                endBounds.Height * IslandConfig.SessionPickerOverlayAnimationStartHeightScale));
            int startHeight = Math.Clamp(scaledStartHeight, minStartHeight, maxStartHeight);
            int anchorCenterX = anchorBounds.X + (anchorBounds.Width / 2);
            int anchorBottom = anchorBounds.Y + anchorBounds.Height;
            int rawStartX = anchorCenterX - (startWidth / 2);
            int rawStartY = anchorBottom - (int)Math.Round(
                startHeight * IslandConfig.SessionPickerOverlayAnimationAnchorOverlapRatio);
            int minX = workArea.X + screenMargin;
            int maxX = workArea.X + Math.Max(screenMargin, workArea.Width - startWidth - screenMargin);
            int minY = workArea.Y + screenMargin;
            int maxY = workArea.Y + Math.Max(screenMargin, workArea.Height - startHeight - screenMargin);
            int startX = Math.Clamp(rawStartX, minX, maxX);
            int startY = Math.Clamp(rawStartY, minY, maxY);

            return new RectInt32(startX, startY, startWidth, startHeight);
        }

        private void BeginSessionPickerOpenAnimation(RectInt32 startBounds, RectInt32 endBounds)
        {
            if (_sessionPickerWindow == null)
            {
                return;
            }

            _sessionPickerOverlayAnimationPhase = SessionPickerOverlayAnimationPhase.Opening;
            _sessionPickerOverlayAnimationStartTime = null;
            _sessionPickerOverlayAnimationFromBounds = startBounds;
            _sessionPickerOverlayAnimationToBounds = endBounds;
            _sessionPickerOverlayPresentedBounds = startBounds;
            _sessionPickerOverlayCloseReconcileHover = true;

            _sessionPickerWindow.View.PrepareShowAnimation();
            _sessionPickerWindow.ShowOverlay(startBounds);
            _sessionPickerWindow.View.StartShowAnimation();
            StartRenderLoop();
        }

        private bool TryBeginSessionPickerCloseAnimation(
            bool reconcileHover,
            SessionPickerOverlayDismissMotion dismissMotion)
        {
            if (_sessionPickerWindow == null)
            {
                return false;
            }

            _sessionPickerOverlayCloseReconcileHover &= reconcileHover;
            if (_sessionPickerOverlayAnimationPhase == SessionPickerOverlayAnimationPhase.Closing)
            {
                return true;
            }

            RectInt32 fromBounds = _sessionPickerOverlayPresentedBounds
                ?? (TryGetSessionPickerWindowBounds(out RectInt32 currentBounds)
                    ? currentBounds
                    : default);
            if (fromBounds.Width <= 0 || fromBounds.Height <= 0)
            {
                return false;
            }

            if (_sessionPickerOverlayAnimationPhase == SessionPickerOverlayAnimationPhase.Opening)
            {
                fromBounds = _sessionPickerOverlayAnimationToBounds;
                _sessionPickerWindow.MoveOverlay(fromBounds);
            }

            _sessionPickerOverlayAnimationPhase = SessionPickerOverlayAnimationPhase.Closing;
            _sessionPickerOverlayAnimationStartTime = null;
            _sessionPickerOverlayAnimationFromBounds = fromBounds;
            _sessionPickerOverlayAnimationToBounds = fromBounds;
            _sessionPickerOverlayPresentedBounds = fromBounds;
            _sessionPickerOverlayCloseDurationMs = dismissMotion.DurationMs;
            _sessionPickerWindow.View.StartHideAnimation(dismissMotion);
            StartRenderLoop();
            return true;
        }

        private void TickSessionPickerOverlayAnimation(TimeSpan renderingTime)
        {
            if (_sessionPickerWindow == null || _sessionPickerOverlayAnimationPhase == SessionPickerOverlayAnimationPhase.None)
            {
                return;
            }

            _sessionPickerOverlayAnimationStartTime ??= renderingTime;
            double elapsedMs = (renderingTime - _sessionPickerOverlayAnimationStartTime.Value).TotalMilliseconds;
            double durationMs = _sessionPickerOverlayAnimationPhase == SessionPickerOverlayAnimationPhase.Opening
                ? IslandConfig.SessionPickerOverlayOpenDurationMs
                : _sessionPickerOverlayCloseDurationMs;
            double progress = Math.Clamp(elapsedMs / durationMs, 0.0, 1.0);

            if (_sessionPickerOverlayAnimationPhase == SessionPickerOverlayAnimationPhase.Opening)
            {
                double eased = EaseOutCubic(progress);
                RectInt32 bounds = LerpRect(
                    _sessionPickerOverlayAnimationFromBounds,
                    _sessionPickerOverlayAnimationToBounds,
                    eased);
                _sessionPickerWindow.MoveOverlay(bounds);
                _sessionPickerOverlayPresentedBounds = bounds;
            }

            if (progress < 1.0)
            {
                return;
            }

            if (_sessionPickerOverlayAnimationPhase == SessionPickerOverlayAnimationPhase.Opening)
            {
                _sessionPickerWindow.MoveOverlay(_sessionPickerOverlayAnimationToBounds);
                _sessionPickerOverlayPresentedBounds = _sessionPickerOverlayAnimationToBounds;
                _sessionPickerOverlayAnimationPhase = SessionPickerOverlayAnimationPhase.None;
                _sessionPickerOverlayAnimationStartTime = null;
                return;
            }

            _sessionPickerWindow.HideOverlay();
            ClearSessionPickerOverlayState(_sessionPickerOverlayCloseReconcileHover);
        }

        private void ResetSessionPickerOverlayAnimationState()
        {
            _sessionPickerOverlayAnimationPhase = SessionPickerOverlayAnimationPhase.None;
            _sessionPickerOverlayAnimationStartTime = null;
            _sessionPickerOverlayAnimationFromBounds = default;
            _sessionPickerOverlayAnimationToBounds = default;
            _sessionPickerOverlayPresentedBounds = null;
            _sessionPickerOverlayCloseReconcileHover = true;
            _sessionPickerOverlayCloseDurationMs = SessionPickerOverlayDismissMotion.FromKind(
                SessionPickerOverlayDismissKind.Passive).DurationMs;
        }

        private static RectInt32 LerpRect(RectInt32 from, RectInt32 to, double progress)
            => new(
                LerpInt(from.X, to.X, progress),
                LerpInt(from.Y, to.Y, progress),
                Math.Max(1, LerpInt(from.Width, to.Width, progress)),
                Math.Max(1, LerpInt(from.Height, to.Height, progress)));

        private static int LerpInt(int from, int to, double progress)
            => (int)Math.Round(from + ((to - from) * progress));

        private static double EaseOutCubic(double t)
            => 1.0 - Math.Pow(1.0 - t, 3.0);

        private WindowFrameInsets GetMainWindowFrameInsets()
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (WindowInterop.TryGetWindowFrameInsets(hwnd, out WindowFrameInsets insets))
            {
                if (!_mainWindowFrameInsets.HasValue || !_mainWindowFrameInsets.Value.Equals(insets))
                {
                    _mainWindowFrameInsets = insets;
                }

                return insets;
            }

            return _mainWindowFrameInsets ?? default;
        }

        private enum SessionPickerOverlayAnimationPhase
        {
            None,
            Opening,
            Closing
        }
    }
}
