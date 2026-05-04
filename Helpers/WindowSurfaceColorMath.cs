using System;
using Windows.UI;

namespace wisland.Helpers
{
    internal static class WindowSurfaceColorMath
    {
        public static Color CreateOpaque(Color color)
            => Color.FromArgb(255, color.R, color.G, color.B);

        public static uint Pack(Color color)
            => ((uint)color.A << 24)
                | ((uint)color.R << 16)
                | ((uint)color.G << 8)
                | color.B;

        public static Color ResolveCompatProgressStartBackdropColor(
            Color surfaceColor,
            Color progressBaseColor,
            bool isProgressVisible)
        {
            Color opaqueSurface = CreateOpaque(surfaceColor);
            if (!isProgressVisible || progressBaseColor.A == 0)
            {
                return opaqueSurface;
            }

            double progressAlpha = progressBaseColor.A / 255.0;
            double surfaceAlpha = surfaceColor.A / 255.0;
            // The progress bar sits over IslandBorder and HostSurface. Solve for
            // the backdrop color that makes the leading progress pixel look like
            // the palette base after those translucent surfaces are composited.
            double uncoveredBySurfaces = Math.Pow(1.0 - surfaceAlpha, 2.0);
            double backdropWeight = (1.0 - progressAlpha) * uncoveredBySurfaces;
            double surfaceWeight = (1.0 - progressAlpha) * (1.0 - uncoveredBySurfaces);

            return Color.FromArgb(
                255,
                SolveSelfConsistentChannel(progressBaseColor.R, progressAlpha, opaqueSurface.R, surfaceWeight, backdropWeight),
                SolveSelfConsistentChannel(progressBaseColor.G, progressAlpha, opaqueSurface.G, surfaceWeight, backdropWeight),
                SolveSelfConsistentChannel(progressBaseColor.B, progressAlpha, opaqueSurface.B, surfaceWeight, backdropWeight));
        }

        public static Color ResolveVisibleBackgroundColor(
            Color gradientColor,
            Color blurColor,
            double blurOpacity,
            double darkScrimOpacity)
        {
            Color afterBlur = Composite(blurColor, blurOpacity, gradientColor);
            return Composite(Color.FromArgb(255, 0, 0, 0), darkScrimOpacity, afterBlur);
        }

        public static Color ResolveEdgeSurfaceColor(Color rightEdge, Color bottomEdge)
            => Blend(rightEdge, bottomEdge, 0.50);

        public static Color SampleGradient(Color start, Color mid, Color end, double offset)
        {
            offset = Math.Clamp(offset, 0.0, 1.0);
            if (offset <= 0.60)
            {
                return Blend(start, mid, offset / 0.60);
            }

            return Blend(mid, end, (offset - 0.60) / 0.40);
        }

        public static Color ClampLuminance(Color color, double maxLuminance)
        {
            double luminance = GetLuminance(color);
            if (luminance <= maxLuminance)
            {
                return CreateOpaque(color);
            }

            double scale = maxLuminance / luminance;
            return Color.FromArgb(
                255,
                (byte)(color.R * scale),
                (byte)(color.G * scale),
                (byte)(color.B * scale));
        }

        public static Color EnsureBright(Color color)
        {
            double luminance = GetLuminance(color);
            if (luminance >= 140)
            {
                return CreateOpaque(color);
            }

            if (luminance < 10)
            {
                return Color.FromArgb(255, 200, 200, 220);
            }

            double scale = Math.Min(4.0, 180.0 / luminance);
            return Color.FromArgb(
                255,
                (byte)Math.Min(255, (int)(color.R * scale + 30)),
                (byte)Math.Min(255, (int)(color.G * scale + 30)),
                (byte)Math.Min(255, (int)(color.B * scale + 30)));
        }

        public static Color Blend(Color from, Color to, double amount)
        {
            amount = Math.Clamp(amount, 0.0, 1.0);
            byte BlendChannel(byte start, byte end)
                => (byte)Math.Round(start + ((end - start) * amount));

            return Color.FromArgb(
                255,
                BlendChannel(from.R, to.R),
                BlendChannel(from.G, to.G),
                BlendChannel(from.B, to.B));
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

        private static Color Composite(Color foreground, double foregroundOpacity, Color background)
        {
            foregroundOpacity = Math.Clamp(foregroundOpacity * (foreground.A / 255.0), 0.0, 1.0);
            double backgroundOpacity = 1.0 - foregroundOpacity;

            return Color.FromArgb(
                255,
                CompositeChannel(foreground.R, foregroundOpacity, background.R, backgroundOpacity),
                CompositeChannel(foreground.G, foregroundOpacity, background.G, backgroundOpacity),
                CompositeChannel(foreground.B, foregroundOpacity, background.B, backgroundOpacity));
        }

        private static byte CompositeChannel(
            byte foreground,
            double foregroundOpacity,
            byte background,
            double backgroundOpacity)
            => (byte)Math.Clamp(
                (int)Math.Round((foreground * foregroundOpacity) + (background * backgroundOpacity)),
                0,
                255);

        private static double GetLuminance(Color color)
            => (0.299 * color.R) + (0.587 * color.G) + (0.114 * color.B);
    }
}
