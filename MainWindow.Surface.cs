using Microsoft.UI.Xaml.Media;
using Windows.UI;
using wisland.Helpers;
using wisland.Models;

namespace wisland
{
    public sealed partial class MainWindow
    {
        private static readonly Color DefaultCompatSurfaceColor = Color.FromArgb(255, 0x3F, 0x3C, 0x42);
        private static readonly Color DefaultCompatProgressBaseColor = Color.FromArgb(58, 0x6E, 0x7E, 0x96);

        private Color _compatWindowSurfaceColor = DefaultCompatSurfaceColor;
        private Color _compatProgressBaseColor = DefaultCompatProgressBaseColor;
        private WindowSurfaceState _currentWindowSurfaceState =
            WindowSurfaceState.CreateCompat(
                DefaultCompatSurfaceColor,
                WindowSurfaceColorMath.CreateOpaque(DefaultCompatSurfaceColor),
                WindowSurfaceColorMath.ResolveCompatProgressStartBackdropColor(
                    DefaultCompatSurfaceColor,
                    DefaultCompatProgressBaseColor,
                    isProgressVisible: false),
                "initial");
        private bool _hasAppliedWindowSurfaceState;
        private WindowSurfaceState? _lastStableImmersiveWindowSurfaceState;

        private void SetCompatWindowSurfaceColors(Color surfaceColor, Color progressBaseColor)
        {
            _compatWindowSurfaceColor = surfaceColor;
            _compatProgressBaseColor = progressBaseColor;
            RefreshWindowSurfaceState();
        }

        private void RefreshWindowSurfaceState()
        {
            if (_isClosed)
            {
                return;
            }

            ApplyWindowSurfaceState(ResolveWindowSurfaceState());
            UpdateResizeBackdropForCurrentState();
        }

        private WindowSurfaceState ResolveWindowSurfaceState()
        {
            if (!ShouldUseImmersiveWindowSurface())
            {
                Color resizeBackfillColor = WindowSurfaceColorMath.CreateOpaque(_compatWindowSurfaceColor);
                // In compat mode the content/backfill stay on the normal island
                // surface. Only the window backdrop is solved to match the first
                // visible pixel of the liquid progress bar during resize.
                Color resizeBackdropColor = ResolveCompatResizeBackdropColor();
                return WindowSurfaceState.CreateCompat(
                    _compatWindowSurfaceColor,
                    resizeBackfillColor,
                    resizeBackdropColor,
                    $"compat:{WindowSurfaceColorMath.Pack(_compatWindowSurfaceColor):X8}:{WindowSurfaceColorMath.Pack(resizeBackfillColor):X8}:{WindowSurfaceColorMath.Pack(resizeBackdropColor):X8}");
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
                    stable.ResizeBackdropColor,
                    $"immersive-pending:{stable.VersionKey}");
            }

            return WindowSurfaceState.CreateImmersive(
                WindowSurfaceMode.ImmersivePending,
                ImmersiveSurfaceTokenFactory.Default,
                "immersive-pending:default");
        }

        private bool ShouldUseImmersiveWindowSurface()
        {
            // Dragging forces compact targets and should not keep the album-art
            // surface/backdrop active; otherwise translucent compat surfaces can
            // pick up stale immersive colors while the user moves the island.
            if (!IsImmersiveActive || _controller.IsForcedExpanded || _controller.IsDragging)
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
            UpdateResizeBackdropColor(state.ResizeBackdropColor);
        }

        private Color ResolveCompatResizeBackdropColor()
        {
            bool isProgressVisible = IslandProgressBar?.IsEffectVisible == true;
            return WindowSurfaceColorMath.ResolveCompatProgressStartBackdropColor(
                _compatWindowSurfaceColor,
                _compatProgressBaseColor,
                isProgressVisible);
        }
    }
}
