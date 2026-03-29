using System;
using System.Collections.Generic;
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

        private bool HasBlockingSurfaceOpen
            => _isContextFlyoutOpen || _isSessionPickerOpen;

        private void InitializeSessionPickerOverlay()
        {
            _sessionPickerWindow = new SessionPickerWindow();
            _sessionPickerWindow.DismissRequested += SessionPickerWindow_DismissRequested;
            _sessionPickerWindow.SessionSelected += SessionPickerWindow_SessionSelected;
            ExpandedContent.SessionPickerToggleRequested += ExpandedContent_SessionPickerToggleRequested;
        }

        private void ApplySessionPickerAppearance(IslandVisualTokens tokens)
            => _sessionPickerWindow?.View.SetColors(
                tokens.PrimaryTextColor,
                tokens.SecondaryTextColor,
                tokens.SurfaceColor);

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
            if (_sessionPickerWindow == null)
            {
                return;
            }

            IReadOnlyList<SessionPickerRowModel> rows = SessionPickerRowProjector.Project(
                context.OrderedSessions,
                context.DisplayedSession?.SessionKey);

            _sessionPickerWindow.View.SetRows(rows);
            if (!TryGetSessionPickerWindowBounds(out RectInt32 bounds))
            {
                return;
            }

            _isSessionPickerOpen = true;
            _controller.IsTransientSurfaceOpen = true;
            _sessionPickerWindow.ShowOverlay(bounds);
            UpdateState();
        }

        private void HideSessionPickerOverlay(bool reconcileHover = true)
        {
            if (!_isSessionPickerOpen)
            {
                return;
            }

            _isSessionPickerOpen = false;
            _controller.IsTransientSurfaceOpen = false;
            _sessionPickerWindow?.HideOverlay();
            UpdateState();

            if (reconcileHover)
            {
                ReconcileHoverStateAfterTransientSurfaceClosed();
            }
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
            int overlayWidth = GetPhysicalPixels(Math.Max(IslandConfig.SessionPickerOverlayWidth, desiredSize.Width), _dpiScale);
            int overlayHeight = GetPhysicalPixels(Math.Max(1.0, desiredSize.Height), _dpiScale);
            int gap = GetPhysicalPixels(IslandConfig.SessionPickerOverlayWindowGap, _dpiScale);
            int margin = GetPhysicalPixels(IslandConfig.SessionPickerOverlayScreenMargin, _dpiScale);

            bounds = SessionPickerPlacementResolver.Resolve(
                anchorBounds,
                GetCurrentDisplayWorkArea(),
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

            int left = _lastPhysX + (int)Math.Round(anchorLogicalBounds.X * _dpiScale);
            int top = _lastPhysY + (int)Math.Round(anchorLogicalBounds.Y * _dpiScale);
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
    }
}
