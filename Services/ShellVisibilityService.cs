using System;
using wisland.Helpers;
using wisland.Models;

namespace wisland.Services
{
    /// <summary>
    /// Higher-level shell visibility facade for docked line mode.
    /// Keeps NativeLineWindow out of MainWindow orchestration code.
    /// </summary>
    public sealed class ShellVisibilityService : IDisposable
    {
        private NativeLineWindow? _lineWindow;
        private LinePalette _linePalette = new(
            Windows.UI.Color.FromArgb(96, 255, 255, 255),
            Windows.UI.Color.FromArgb(78, 104, 118, 138),
            Windows.UI.Color.FromArgb(224, 116, 176, 255),
            Windows.UI.Color.FromArgb(248, 214, 236, 255),
            Windows.UI.Color.FromArgb(96, 0, 0, 0),
            Windows.UI.Color.FromArgb(92, 72, 84, 100));
        private int _lineHeightPhysical = IslandConfig.NativeLinePhysicalHeight;

        public void ApplyAppearance(LinePalette palette, int lineHeightPhysical)
        {
            _linePalette = palette;
            _lineHeightPhysical = Math.Max(1, lineHeightPhysical);

            if (_lineWindow != null)
            {
                _lineWindow.ApplyPalette(_linePalette);
            }
        }

        public void ShowDockedLine(int physicalX, int monitorTopPhysical, int physicalWidth, double progress)
        {
            bool firstShow = _lineWindow == null;
            NativeLineWindow lineWindow = _lineWindow ??= new NativeLineWindow();
            lineWindow.ApplyPalette(_linePalette);
            lineWindow.SetProgress(progress);
            lineWindow.Show(physicalX, monitorTopPhysical, physicalWidth, _lineHeightPhysical);
            if (firstShow)
            {
                Logger.Debug($"Docked line window created and shown: X={physicalX}, W={physicalWidth}");
            }
        }

        public void HideDockedLine()
        {
            if (_lineWindow == null)
            {
                return;
            }

            Logger.Debug("Docked line window hidden");
            _lineWindow.Hide();
        }

        public void Dispose()
        {
            if (_lineWindow == null)
            {
                return;
            }

            _lineWindow.Dispose();
            _lineWindow = null;
        }
    }
}
