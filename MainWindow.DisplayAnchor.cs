using System;
using Windows.Graphics;
using wisland.Helpers;
using wisland.Models;

namespace wisland
{
    public sealed partial class MainWindow
    {
        private void InitializeDisplayAnchorFromSettings()
        {
            if (TryGetSavedDisplayState(out var savedWorkArea, out double savedCenterX, out double savedTopY))
            {
                _dpiScale = GetDisplayDpiScale(savedWorkArea);
                _controller.InitializePosition(savedCenterX, _settings.IsDocked ? 0 : savedTopY, _settings.IsDocked);
                ClampControllerPositionToDisplay(savedWorkArea, _controller.Current.Width, _controller.Current.Height, _dpiScale);
                UpdateAnchorPhysicalPoint(savedWorkArea, _controller.Current, GetPhysicalPixels(_controller.Current.Width, _dpiScale), GetPhysicalPixels(_controller.Current.Height, _dpiScale));
                return;
            }

            RectInt32 primaryWorkArea = WindowInterop.GetPrimaryDisplayWorkArea();
            _dpiScale = GetDisplayDpiScale(primaryWorkArea);
            double defaultCenterX = primaryWorkArea.Width / (2.0 * _dpiScale);
            double defaultTopY = IslandConfig.DefaultY;

            _controller.InitializePosition(defaultCenterX, _settings.IsDocked ? 0 : defaultTopY, _settings.IsDocked);
            ClampControllerPositionToDisplay(primaryWorkArea, _controller.Current.Width, _controller.Current.Height, _dpiScale);
            UpdateAnchorPhysicalPoint(primaryWorkArea, _controller.Current, GetPhysicalPixels(_controller.Current.Width, _dpiScale), GetPhysicalPixels(_controller.Current.Height, _dpiScale));
        }

        private bool TryGetSavedDisplayState(out RectInt32 workArea, out double relativeCenterX, out double relativeTopY)
        {
            if (_settings.AnchorPhysicalX.HasValue && _settings.AnchorPhysicalY.HasValue)
            {
                workArea = ResolveDisplayWorkAreaFromPhysicalPoint(_settings.AnchorPhysicalX.Value, _settings.AnchorPhysicalY.Value);
                double displayDpi = GetDisplayDpiScale(workArea);
                relativeCenterX = _settings.RelativeCenterX ?? _settings.CenterX;
                relativeTopY = _settings.RelativeTopY ?? _settings.LastY;

                if (!double.IsFinite(relativeCenterX) || relativeCenterX <= 0)
                {
                    relativeCenterX = (Math.Clamp(_settings.AnchorPhysicalX.Value, workArea.X, workArea.X + Math.Max(0, workArea.Width - 1)) - workArea.X) / displayDpi;
                }

                if (!double.IsFinite(relativeTopY) || relativeTopY < 0)
                {
                    relativeTopY = Math.Max(0, (_settings.AnchorPhysicalY.Value - workArea.Y) / displayDpi);
                }

                return true;
            }

            if (_settings.CenterX > 0)
            {
                workArea = WindowInterop.GetPrimaryDisplayWorkArea();
                relativeCenterX = _settings.CenterX;
                relativeTopY = _settings.LastY;
                return true;
            }

            workArea = WindowInterop.GetPrimaryDisplayWorkArea();
            relativeCenterX = 0;
            relativeTopY = 0;
            return false;
        }

        private RectInt32 GetCurrentDisplayWorkArea()
        {
            if (_hasAnchorPhysicalPoint)
            {
                return ResolveDisplayWorkAreaFromPhysicalPoint(_anchorPhysicalX, _anchorPhysicalY);
            }

            return WindowInterop.GetPrimaryDisplayWorkArea();
        }

        private RectInt32 ResolveDisplayWorkAreaFromPhysicalPoint(int x, int y)
            => WindowInterop.GetDisplayWorkAreaForPoint(x, y);

        private bool ActiveDisplayHasScreenAbove(RectInt32 workArea)
        {
            int displayLeft = workArea.X;
            int displayRight = workArea.X + workArea.Width;

            foreach (RectInt32 other in WindowInterop.GetDisplayWorkAreas())
            {
                bool isSameDisplay = other.X == workArea.X
                    && other.Y == workArea.Y
                    && other.Width == workArea.Width
                    && other.Height == workArea.Height;
                if (isSameDisplay)
                {
                    continue;
                }

                bool overlapsHorizontally = other.X < displayRight && other.X + other.Width > displayLeft;
                bool isAbove = other.Y + other.Height <= workArea.Y;

                if (overlapsHorizontally && isAbove)
                {
                    return true;
                }
            }

            return false;
        }

        private bool SupportsDockedLinePresentation(RectInt32 workArea)
        {
            if (!_controller.IsDocked
                || _controller.IsNotifying
                || _controller.IsDragging
                || _controller.IsTransientSurfaceOpen)
            {
                return false;
            }

            return _controller.IsForegroundMaximized || ActiveDisplayHasScreenAbove(workArea);
        }

        private bool ShouldUseDockedLinePresentation(RectInt32 workArea)
            => SupportsDockedLinePresentation(workArea) && !_controller.IsHovered;

        private double GetDisplayDpiScale(RectInt32 workArea)
        {
            int sampleX = workArea.X + Math.Max(0, workArea.Width / 2);
            int sampleY = workArea.Y + Math.Max(0, Math.Min(workArea.Height - 1, 1));
            return WindowInterop.GetDpiScaleForPoint(sampleX, sampleY);
        }

        private void ClampControllerPositionToDisplay(RectInt32 workArea, double widthLogical, double heightLogical, double dpiScale)
        {
            double displayWidthLogical = workArea.Width / dpiScale;
            double displayHeightLogical = workArea.Height / dpiScale;
            double halfWidth = widthLogical / 2.0;
            double minCenterX = halfWidth;
            double maxCenterX = Math.Max(minCenterX, displayWidthLogical - halfWidth);

            _controller.Current.CenterX = Math.Clamp(_controller.Current.CenterX, minCenterX, maxCenterX);

            if (_controller.IsDocked)
            {
                _controller.Current.Y = Math.Min(_controller.Current.Y, 0);
                return;
            }

            double maxTopY = Math.Max(0, displayHeightLogical - (10.0 / dpiScale));
            _controller.Current.Y = Math.Clamp(_controller.Current.Y, 0, maxTopY);
        }

        private void UpdateAnchorPhysicalPoint(RectInt32 workArea, IslandState state, int physWidth, int physHeight)
        {
            int centerXPhys = workArea.X + (int)Math.Round(state.CenterX * _dpiScale);
            centerXPhys = Math.Clamp(centerXPhys, workArea.X, workArea.X + Math.Max(0, workArea.Width - 1));

            int anchorYPhys;
            if (_controller.IsDocked)
            {
                int visiblePhys = GetDockPeekPhysicalPixels(_dpiScale);
                anchorYPhys = workArea.Y + Math.Max(0, visiblePhys - 1);
            }
            else
            {
                int topPhys = workArea.Y + (int)Math.Round(Math.Max(0, state.Y) * _dpiScale);
                anchorYPhys = topPhys + Math.Max(1, physHeight / 2);
                anchorYPhys = Math.Clamp(anchorYPhys, workArea.Y, workArea.Y + Math.Max(0, workArea.Height - 1));
            }

            _anchorPhysicalX = centerXPhys;
            _anchorPhysicalY = anchorYPhys;
            _hasAnchorPhysicalPoint = true;
        }

        private void SetActiveDisplayAnchorFromDrag(RectInt32 workArea, double physicalCenterX, double physicalTopY, int physHeight)
        {
            _anchorPhysicalX = Math.Clamp((int)Math.Round(physicalCenterX), workArea.X, workArea.X + Math.Max(0, workArea.Width - 1));
            int anchorY = (int)Math.Round(physicalTopY) + Math.Max(1, physHeight / 2);
            _anchorPhysicalY = Math.Clamp(anchorY, workArea.Y, workArea.Y + Math.Max(0, workArea.Height - 1));
            _hasAnchorPhysicalPoint = true;
        }
    }
}
