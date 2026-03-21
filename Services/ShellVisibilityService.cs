using System;
using island.Helpers;
using island.Models;

namespace island.Services
{
    /// <summary>
    /// Higher-level shell visibility facade for docked line mode.
    /// Keeps NativeLineWindow out of MainWindow orchestration code.
    /// </summary>
    public sealed class ShellVisibilityService : IDisposable
    {
        private readonly NativeLineWindow _lineWindow = new();

        public void ShowDockedLine(double centerX, double dpiScale, double progress)
        {
            int width = (int)Math.Ceiling(IslandConfig.CompactWidth * dpiScale);
            int height = Math.Max(1, (int)Math.Ceiling(dpiScale));
            int x = (int)Math.Round((centerX - IslandConfig.CompactWidth / 2.0) * dpiScale);

            _lineWindow.SetProgress(progress);
            _lineWindow.Show(x, 0, width, height);
        }

        public void HideDockedLine()
        {
            _lineWindow.Hide();
        }

        public void Dispose()
        {
            _lineWindow.Hide();
            _lineWindow.Dispose();
        }
    }
}
