using Microsoft.UI.Xaml.Media;
using Windows.UI;
using wisland.Models;

namespace wisland
{
    public sealed partial class MainWindow
    {
        private static readonly Color DefaultCompatSurfaceColor = Color.FromArgb(255, 0x3F, 0x3C, 0x42);

        private Color _compatWindowSurfaceColor = DefaultCompatSurfaceColor;
        private WindowSurfaceState _currentWindowSurfaceState =
            WindowSurfaceState.CreateCompat(DefaultCompatSurfaceColor, "initial");
        private bool _hasAppliedWindowSurfaceState;
        private WindowSurfaceState? _lastStableImmersiveWindowSurfaceState;

        private void SetCompatWindowSurfaceColor(Color surfaceColor)
        {
            _compatWindowSurfaceColor = surfaceColor;
            RefreshWindowSurfaceState();
        }

        private void RefreshWindowSurfaceState()
        {
            if (_isClosed)
            {
                return;
            }

            ApplyWindowSurfaceState(ResolveWindowSurfaceState());
        }

        private WindowSurfaceState ResolveWindowSurfaceState()
        {
            if (!ShouldUseImmersiveWindowSurface())
            {
                return WindowSurfaceState.CreateCompat(
                    _compatWindowSurfaceColor,
                    $"compat:{PackColor(_compatWindowSurfaceColor):X8}");
            }

            MediaSessionSnapshot? session = GetDisplayedMediaSessionSnapshot();
            if (session.HasValue
                && !session.Value.MissingSinceUtc.HasValue
                && !string.IsNullOrEmpty(session.Value.ThumbnailHash)
                && _visualCache != null
                && _visualCache.TryGet(session.Value.ThumbnailHash, out MediaVisualAssets? assets)
                && assets != null)
            {
                WindowSurfaceState settled = WindowSurfaceState.CreateImmersive(
                    WindowSurfaceMode.ImmersiveSettled,
                    assets.ImmersiveSurfaceTokens,
                    $"immersive:{session.Value.SessionKey}:{assets.Hash}");
                _lastStableImmersiveWindowSurfaceState = settled;
                return settled;
            }

            if (_lastStableImmersiveWindowSurfaceState.HasValue)
            {
                WindowSurfaceState stable = _lastStableImmersiveWindowSurfaceState.Value;
                return new WindowSurfaceState(
                    WindowSurfaceMode.ImmersivePending,
                    stable.HostSurfaceColor,
                    stable.ResizeBackfillColor,
                    $"immersive-pending:{stable.VersionKey}");
            }

            return WindowSurfaceState.CreateImmersive(
                WindowSurfaceMode.ImmersivePending,
                ImmersiveSurfaceTokens.Default,
                "immersive-pending:default");
        }

        private bool ShouldUseImmersiveWindowSurface()
        {
            if (!IsImmersiveActive || _controller.IsForcedExpanded)
            {
                return false;
            }

            return _controller.IsHovered
                || _controller.IsTransientSurfaceOpen;
        }

        private void ApplyWindowSurfaceState(WindowSurfaceState state)
        {
            if (_hasAppliedWindowSurfaceState && _currentWindowSurfaceState == state)
            {
                return;
            }

            _currentWindowSurfaceState = state;
            _hasAppliedWindowSurfaceState = true;

            HostSurface.Background = new SolidColorBrush(state.HostSurfaceColor);
            IslandBorder.Background = new SolidColorBrush(state.HostSurfaceColor);
            UpdateResizeBackfillSurfaceColor(state.ResizeBackfillColor);
        }
    }
}
