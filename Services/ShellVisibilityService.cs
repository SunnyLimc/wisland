using System;
using island.Helpers;

namespace island.Services
{
    /// <summary>
    /// Higher-level shell visibility facade for docked line mode.
    /// Keeps NativeLineWindow out of MainWindow orchestration code.
    /// </summary>
    public sealed class ShellVisibilityService : IDisposable
    {
        private NativeLineWindow? _lineWindow;

        public void ShowDockedLine(int physicalX, int monitorTopPhysical, int physicalWidth, double progress)
        {
            NativeLineWindow lineWindow = _lineWindow ??= new NativeLineWindow();
            lineWindow.SetProgress(progress);
            lineWindow.Show(physicalX, monitorTopPhysical, physicalWidth, 1);
        }

        public void HideDockedLine()
        {
            if (_lineWindow == null)
            {
                return;
            }

            _lineWindow.Hide();
            _lineWindow.Dispose();
            _lineWindow = null;
        }

        public void Dispose()
        {
            HideDockedLine();
        }
    }
}
