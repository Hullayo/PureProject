using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace PureProject.WinUI;

public sealed partial class MainWindow
{
    // Call after assigning a data-driven or selected palette. Native Button
    // still handles pointer capture, keyboard activation and automation.
    private static void PreserveButtonPalette(Button button)
    {
        NativeControlPalette.SetIsEnabled(button, true);
        var background = button.Background;
        var foreground = button.Foreground;
        var border = button.BorderBrush;
        foreach (var prefix in new[] { "Button", "AccentButton" })
        {
            NativeControlPalette.SetCustomBrush(button, prefix + "BackgroundPointerOver", InteractionBrush(background, foreground, .08));
            NativeControlPalette.SetCustomBrush(button, prefix + "BackgroundPressed", InteractionBrush(background, foreground, .16));
            NativeControlPalette.SetCustomBrush(button, prefix + "ForegroundPointerOver", foreground);
            NativeControlPalette.SetCustomBrush(button, prefix + "ForegroundPressed", foreground);
            NativeControlPalette.SetCustomBrush(button, prefix + "BorderBrushPointerOver", border);
            NativeControlPalette.SetCustomBrush(button, prefix + "BorderBrushPressed", border);
        }
    }

    private static void RestoreButtonPalette(Button button)
    {
        NativeControlPalette.SetIsEnabled(button, true);
        foreach (var prefix in new[] { "Button", "AccentButton" })
            foreach (var suffix in new[] { "BackgroundPointerOver", "BackgroundPressed", "ForegroundPointerOver", "ForegroundPressed", "BorderBrushPointerOver", "BorderBrushPressed" })
                NativeControlPalette.RestoreBrush(button, prefix + suffix);
    }

    private static Brush InteractionBrush(Brush background, Brush foreground, double amount)
    {
        if (background is not SolidColorBrush fill) return background;
        if (fill.Opacity < 1 || fill.Color.A < 255)
            return new SolidColorBrush(Windows.UI.Color.FromArgb((byte)Math.Round(Math.Min(1, fill.Color.A / 255d * fill.Opacity + amount) * 255), fill.Color.R, fill.Color.G, fill.Color.B));
        var text = foreground as SolidColorBrush;
        var darkText = text is not null && .2126 * text.Color.R + .7152 * text.Color.G + .0722 * text.Color.B < 128;
        var target = darkText ? 255 : 0;
        byte Blend(byte value) => (byte)Math.Round(value + (target - value) * amount);
        return new SolidColorBrush(Windows.UI.Color.FromArgb(fill.Color.A, Blend(fill.Color.R), Blend(fill.Color.G), Blend(fill.Color.B)));
    }
}
