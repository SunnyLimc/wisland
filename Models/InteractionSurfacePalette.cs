using Windows.UI;

namespace wisland.Models
{
    internal readonly record struct InteractionSurfacePalette(
        Color BackgroundColor,
        Color BorderColor,
        Color HoverBackgroundColor,
        Color HoverBorderColor,
        Color PressBackgroundColor,
        Color PressBorderColor,
        Color FocusRingColor)
    {
        public static InteractionSurfacePalette Create(
            Color primaryColor,
            Color secondaryColor,
            byte backgroundAlpha,
            byte borderAlpha,
            byte hoverBackgroundAlpha,
            byte hoverBorderAlpha,
            byte pressBackgroundAlpha,
            byte pressBorderAlpha,
            byte focusRingAlpha)
            => new(
                WithAlpha(primaryColor, backgroundAlpha),
                WithAlpha(secondaryColor, borderAlpha),
                WithAlpha(primaryColor, hoverBackgroundAlpha),
                WithAlpha(secondaryColor, hoverBorderAlpha),
                WithAlpha(primaryColor, pressBackgroundAlpha),
                WithAlpha(secondaryColor, pressBorderAlpha),
                WithAlpha(primaryColor, focusRingAlpha));

        private static Color WithAlpha(Color color, byte alpha)
            => Color.FromArgb(alpha, color.R, color.G, color.B);
    }
}
