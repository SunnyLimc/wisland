using island.Models;

namespace island
{
    public sealed partial class MainWindow
    {
        /// <summary>
        /// Apply a system backdrop effect and update text colors to match.
        /// </summary>
        public void SetBackdrop(BackdropType type, bool persist = true)
        {
            _currentBackdropType = type;

            _appearanceService.ApplyBackdrop(this, IslandBorder, CompactContent, ExpandedContent, type);

            if (persist && _settings.BackdropType != type)
            {
                _settings.BackdropType = type;
                _settings.Save();
            }
        }
    }
}
