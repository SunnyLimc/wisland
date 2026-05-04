using Microsoft.UI.Xaml.Media;
using System;
using Windows.UI;
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
                ResolveCompatProgressStartBackfillColor(
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
        }

        private WindowSurfaceState ResolveWindowSurfaceState()
        {
            if (!ShouldUseImmersiveWindowSurface())
            {
                Color resizeBackfillColor = ResolveCompatResizeBackfillColor();
                return WindowSurfaceState.CreateCompat(
                    _compatWindowSurfaceColor,
                    resizeBackfillColor,
                    $"compat:{PackColor(_compatWindowSurfaceColor):X8}:{PackColor(resizeBackfillColor):X8}");
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

        private Color ResolveCompatResizeBackfillColor()
        {
            bool isProgressVisible = IslandProgressBar?.IsEffectVisible == true;
            return ResolveCompatProgressStartBackfillColor(
                _compatWindowSurfaceColor,
                _compatProgressBaseColor,
                isProgressVisible);
        }

        private static Color ResolveCompatProgressStartBackfillColor(
            Color surfaceColor,
            Color progressBaseColor,
            bool isProgressVisible)
        {
            Color opaqueSurface = CreateOpaqueBackfillColor(surfaceColor);
            if (!isProgressVisible || progressBaseColor.A == 0)
            {
                return opaqueSurface;
            }

            double progressAlpha = progressBaseColor.A / 255.0;
            double surfaceAlpha = surfaceColor.A / 255.0;
            double uncoveredBySurfaces = Math.Pow(1.0 - surfaceAlpha, 2.0);
            double backdropWeight = (1.0 - progressAlpha) * uncoveredBySurfaces;
            double surfaceWeight = (1.0 - progressAlpha) * (1.0 - uncoveredBySurfaces);

            return Color.FromArgb(
                255,
                SolveSelfConsistentChannel(progressBaseColor.R, progressAlpha, opaqueSurface.R, surfaceWeight, backdropWeight),
                SolveSelfConsistentChannel(progressBaseColor.G, progressAlpha, opaqueSurface.G, surfaceWeight, backdropWeight),
                SolveSelfConsistentChannel(progressBaseColor.B, progressAlpha, opaqueSurface.B, surfaceWeight, backdropWeight));
        }

        private static byte SolveSelfConsistentChannel(
            byte progressChannel,
            double progressAlpha,
            byte surfaceChannel,
            double surfaceWeight,
            double backdropWeight)
        {
            double fixedPart = (progressChannel * progressAlpha) + (surfaceChannel * surfaceWeight);
            double channel = backdropWeight >= 0.999
                ? fixedPart
                : fixedPart / (1.0 - backdropWeight);
            return (byte)Math.Clamp((int)Math.Round(channel), 0, 255);
        }
    }
}
