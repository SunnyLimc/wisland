using System;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using island.Helpers;
using island.Models;

namespace island
{
    public sealed partial class MainWindow
    {
        private void InitializeDisplayAnchorFromSettings()
        {
            if (TryGetSavedDisplayState(out var savedDisplay, out double savedCenterX, out double savedTopY))
            {
                _dpiScale = GetDisplayDpiScale(savedDisplay);
                _controller.InitializePosition(savedCenterX, _settings.IsDocked ? 0 : savedTopY, _settings.IsDocked);
                ClampControllerPositionToDisplay(savedDisplay.WorkArea, _controller.Current.Width, _controller.Current.Height, _dpiScale);
                UpdateAnchorPhysicalPoint(savedDisplay, _controller.Current, GetPhysicalPixels(_controller.Current.Width, _dpiScale), GetPhysicalPixels(_controller.Current.Height, _dpiScale));
                return;
            }

            var primaryDisplay = DisplayArea.Primary;
            _dpiScale = GetDisplayDpiScale(primaryDisplay);
            double defaultCenterX = primaryDisplay.WorkArea.Width / (2.0 * _dpiScale);
            double defaultTopY = IslandConfig.DefaultY;

            _controller.InitializePosition(defaultCenterX, _settings.IsDocked ? 0 : defaultTopY, _settings.IsDocked);
            ClampControllerPositionToDisplay(primaryDisplay.WorkArea, _controller.Current.Width, _controller.Current.Height, _dpiScale);
            UpdateAnchorPhysicalPoint(primaryDisplay, _controller.Current, GetPhysicalPixels(_controller.Current.Width, _dpiScale), GetPhysicalPixels(_controller.Current.Height, _dpiScale));
        }

        private bool TryGetSavedDisplayState(out DisplayArea display, out double relativeCenterX, out double relativeTopY)
        {
            if (_settings.AnchorPhysicalX.HasValue && _settings.AnchorPhysicalY.HasValue)
            {
                display = ResolveDisplayFromPhysicalPoint(_settings.AnchorPhysicalX.Value, _settings.AnchorPhysicalY.Value);
                double displayDpi = GetDisplayDpiScale(display);
                relativeCenterX = _settings.RelativeCenterX ?? _settings.CenterX;
                relativeTopY = _settings.RelativeTopY ?? _settings.LastY;

                if (!double.IsFinite(relativeCenterX) || relativeCenterX <= 0)
                {
                    relativeCenterX = (Math.Clamp(_settings.AnchorPhysicalX.Value, display.WorkArea.X, display.WorkArea.X + Math.Max(0, display.WorkArea.Width - 1)) - display.WorkArea.X) / displayDpi;
                }

                if (!double.IsFinite(relativeTopY) || relativeTopY < 0)
                {
                    relativeTopY = Math.Max(0, (_settings.AnchorPhysicalY.Value - display.WorkArea.Y) / displayDpi);
                }

                return true;
            }

            if (_settings.CenterX > 0)
            {
                display = DisplayArea.Primary;
                relativeCenterX = _settings.CenterX;
                relativeTopY = _settings.LastY;
                return true;
            }

            display = DisplayArea.Primary;
            relativeCenterX = 0;
            relativeTopY = 0;
            return false;
        }

        private DisplayArea GetCurrentDisplayArea()
        {
            if (_hasAnchorPhysicalPoint)
            {
                return ResolveDisplayFromPhysicalPoint(_anchorPhysicalX, _anchorPhysicalY);
            }

            return DisplayArea.Primary;
        }

        private DisplayArea ResolveDisplayFromPhysicalPoint(int x, int y)
            => DisplayArea.GetFromPoint(new PointInt32(x, y), DisplayAreaFallback.Nearest);

        private double GetDisplayDpiScale(DisplayArea display)
        {
            int sampleX = display.WorkArea.X + Math.Max(0, display.WorkArea.Width / 2);
            int sampleY = display.WorkArea.Y + Math.Max(0, Math.Min(display.WorkArea.Height - 1, 1));
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

        private void UpdateAnchorPhysicalPoint(DisplayArea display, IslandState state, int physWidth, int physHeight)
        {
            int centerXPhys = display.WorkArea.X + (int)Math.Round(state.CenterX * _dpiScale);
            centerXPhys = Math.Clamp(centerXPhys, display.WorkArea.X, display.WorkArea.X + Math.Max(0, display.WorkArea.Width - 1));

            int anchorYPhys;
            if (_controller.IsDocked)
            {
                int visiblePhys = GetDockPeekPhysicalPixels(_dpiScale);
                anchorYPhys = display.WorkArea.Y + Math.Max(0, visiblePhys - 1);
            }
            else
            {
                int topPhys = display.WorkArea.Y + (int)Math.Round(Math.Max(0, state.Y) * _dpiScale);
                anchorYPhys = topPhys + Math.Max(1, physHeight / 2);
                anchorYPhys = Math.Clamp(anchorYPhys, display.WorkArea.Y, display.WorkArea.Y + Math.Max(0, display.WorkArea.Height - 1));
            }

            _anchorPhysicalX = centerXPhys;
            _anchorPhysicalY = anchorYPhys;
            _hasAnchorPhysicalPoint = true;
        }

        private void SetActiveDisplayAnchorFromDrag(DisplayArea display, double physicalCenterX, double physicalTopY, int physHeight)
        {
            _anchorPhysicalX = Math.Clamp((int)Math.Round(physicalCenterX), display.WorkArea.X, display.WorkArea.X + Math.Max(0, display.WorkArea.Width - 1));
            int anchorY = (int)Math.Round(physicalTopY) + Math.Max(1, physHeight / 2);
            _anchorPhysicalY = Math.Clamp(anchorY, display.WorkArea.Y, display.WorkArea.Y + Math.Max(0, display.WorkArea.Height - 1));
            _hasAnchorPhysicalPoint = true;
        }
    }
}
