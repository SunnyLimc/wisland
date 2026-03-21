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

        public void ShowDockedLine(int physicalX, int monitorTopPhysical, int physicalWidth, double progress)
        {
            _lineWindow.SetProgress(progress);
            _lineWindow.Show(physicalX, monitorTopPhysical, physicalWidth, 1);
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
