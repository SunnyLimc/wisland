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

        private bool HasBlockingSurfaceOpen
            => _isContextFlyoutOpen || _isSessionPickerOpen;

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
                HideSessionPickerOverlay();
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
            => HideSessionPickerOverlay();

        private void SessionPickerWindow_SessionSelected(string sessionKey)
        {
            HideSessionPickerOverlay(reconcileHover: false);
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
            if (!TryGetSessionPickerWindowBounds(out RectInt32 bounds))
            {
                return;
            }

            _isSessionPickerOpen = true;
            _controller.IsTransientSurfaceOpen = true;
            sessionPickerWindow.ShowOverlay(bounds);
            UpdateState();
        }

        private void HideSessionPickerOverlay(bool reconcileHover = true)
        {
            if (!_isSessionPickerOpen)
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
                _sessionPickerWindow.MoveOverlay(bounds);
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
    }
}
