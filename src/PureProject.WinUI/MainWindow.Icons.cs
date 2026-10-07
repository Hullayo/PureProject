using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;

namespace PureProject.WinUI;

public sealed partial class MainWindow
{
    private static readonly HashSet<string> SourceIconNames = new(StringComparer.Ordinal)
    {
        "logo", "dashboard", "folder", "folder-open", "file", "clipboard", "check", "close",
        "chevron-right", "chevron-up", "chevron-down", "sun", "moon", "help", "play",
        "download", "printer", "upload", "chart", "sparkles", "ban", "alert-triangle",
        "repeat", "plus", "menu", "more-horizontal", "settings", "archive", "trash", "search", "pencil"
    };

    /// <summary>Uses the exact SVG geometry extracted from v1.2's Icon.svelte and Sidebar.svelte.</summary>
    private static Image SourceIcon(string name, double size = 16, bool accent = false)
    {
        var mirrored = name == "chevron-left";
        var assetName = mirrored ? "chevron-right" : name;
        if (!SourceIconNames.Contains(assetName)) throw new ArgumentOutOfRangeException(nameof(name), name, "This icon is not present in the v1.2 icon set.");
        if (!double.IsFinite(size) || size <= 0) throw new ArgumentOutOfRangeException(nameof(size));

        var variant = (_activeDark ? "dark" : "light") + (accent ? "-accent" : "");
        var image = new Image
        {
            Source = new SvgImageSource(new Uri($"ms-appx:///Assets/Icons/{assetName}-{variant}.svg")),
            Width = size,
            Height = size,
            Stretch = Stretch.Uniform,
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center
        };
        // The action's containing button supplies its accessible name.
        AutomationProperties.SetAccessibilityView(image, AccessibilityView.Raw);
        if (mirrored)
        {
            image.RenderTransformOrigin = new Point(.5, .5);
            image.RenderTransform = new ScaleTransform { ScaleX = -1 };
        }
        return image;
    }
}
