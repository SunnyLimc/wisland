using Windows.UI;
using wisland.Helpers;

namespace wisland.Models
{
    internal readonly record struct ImmersiveSurfaceTokens(
        Color OpaqueBackfillColor,
        Color HostSurfaceColor,
        Color LeftEdgeBackdropColor,
        Color GradientStartColor,
        Color GradientMidColor,
        Color GradientEndColor,
        Color ProgressAccentColor)
    {
        private const double SettledBlurOpacity = 0.60;
        private const double NoBlurOpacity = 0.0;
        private const double DarkScrimOpacity = 0x59 / 255.0;

        public static readonly ImmersiveSurfaceTokens Default = FromPalette(
            AlbumArtPalette.Default,
            NoBlurOpacity);

        public static ImmersiveSurfaceTokens FromPalette(AlbumArtPalette palette)
            => FromPalette(palette, SettledBlurOpacity);

        private static ImmersiveSurfaceTokens FromPalette(AlbumArtPalette palette, double blurOpacity)
        {
            Color gradientStart = ClampLuminance(palette.Dominant, 80);
            Color gradientMid = ClampLuminance(palette.Secondary, 55);
            Color gradientEnd = ClampLuminance(palette.Average, 65);

            Color baseColor = Blend(palette.Average, palette.Secondary, 0.45);
            baseColor = Blend(baseColor, Color.FromArgb(255, 16, 18, 24), 0.38);
            Color backfill = ClampLuminance(baseColor, 48);

            Color leftEdge = ResolveVisibleBackgroundColor(
                SampleGradient(gradientStart, gradientMid, gradientEnd, 0.25),
                palette.LeftEdge,
                blurOpacity);

            return new ImmersiveSurfaceTokens(
                backfill,
                backfill,
                leftEdge,
                gradientStart,
                gradientMid,
                gradientEnd,
                EnsureBright(palette.Dominant));
        }

        private static Color ResolveVisibleBackgroundColor(
            Color gradientColor,
            Color blurColor,
            double blurOpacity)
        {
            Color afterBlur = Composite(blurColor, blurOpacity, gradientColor);
            return Composite(Color.FromArgb(255, 0, 0, 0), DarkScrimOpacity, afterBlur);
        }

        private static Color SampleGradient(Color start, Color mid, Color end, double offset)
        {
            offset = System.Math.Clamp(offset, 0.0, 1.0);
            if (offset <= 0.60)
            {
                return Blend(start, mid, offset / 0.60);
            }

            return Blend(mid, end, (offset - 0.60) / 0.40);
        }

        private static Color Composite(Color foreground, double foregroundOpacity, Color background)
        {
            foregroundOpacity = System.Math.Clamp(foregroundOpacity * (foreground.A / 255.0), 0.0, 1.0);
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
            => (byte)System.Math.Clamp(
                (int)System.Math.Round((foreground * foregroundOpacity) + (background * backgroundOpacity)),
                0,
                255);

        private static Color ClampLuminance(Color color, double maxLuminance)
        {
            double luminance = GetLuminance(color);
            if (luminance <= maxLuminance)
            {
                return Color.FromArgb(255, color.R, color.G, color.B);
            }

            double scale = maxLuminance / luminance;
            return Color.FromArgb(
                255,
                (byte)(color.R * scale),
                (byte)(color.G * scale),
                (byte)(color.B * scale));
        }

        private static Color EnsureBright(Color color)
        {
            double luminance = GetLuminance(color);
            if (luminance >= 140)
            {
                return Color.FromArgb(255, color.R, color.G, color.B);
            }

            if (luminance < 10)
            {
                return Color.FromArgb(255, 200, 200, 220);
            }

            double scale = System.Math.Min(4.0, 180.0 / luminance);
            return Color.FromArgb(
                255,
                (byte)System.Math.Min(255, (int)(color.R * scale + 30)),
                (byte)System.Math.Min(255, (int)(color.G * scale + 30)),
                (byte)System.Math.Min(255, (int)(color.B * scale + 30)));
        }

        private static Color Blend(Color from, Color to, double amount)
        {
            amount = System.Math.Clamp(amount, 0.0, 1.0);
            byte BlendChannel(byte start, byte end)
                => (byte)System.Math.Round(start + ((end - start) * amount));

            return Color.FromArgb(
                255,
                BlendChannel(from.R, to.R),
                BlendChannel(from.G, to.G),
                BlendChannel(from.B, to.B));
        }

        private static double GetLuminance(Color color)
            => (0.299 * color.R) + (0.587 * color.G) + (0.114 * color.B);
    }
}
