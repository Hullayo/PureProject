using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Runtime.CompilerServices;

namespace PureProject.WinUI;

// Keep the SDK control templates and peers. Publish the current palette at the
// control's direct resource scope, where native template states can resolve it.
public static class NativeControlPalette
{
    private sealed class PaletteState
    {
        public bool Initialized;
        public string[] Keys = [];
        public readonly Dictionary<string, SolidColorBrush> Brushes = new(StringComparer.Ordinal);
        public readonly HashSet<string> CustomKeys = new(StringComparer.Ordinal);
    }

    private static readonly ConditionalWeakTable<FrameworkElement, PaletteState> States = new();

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(NativeControlPalette), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject element, bool value)
    {
        element.SetValue(IsEnabledProperty, value);
        if (value && element is FrameworkElement control) EnsureConfigured(control);
    }

    private static void OnIsEnabledChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is FrameworkElement element && args.NewValue is true) EnsureConfigured(element);
    }

    private static void EnsureConfigured(FrameworkElement element)
    {
        var state = States.GetValue(element, _ => new PaletteState());
        if (state.Initialized) return;
        state.Initialized = true;
        state.Keys = ResourceKeys(element).ToArray();
        ApplyPalette(element, state);
        element.ActualThemeChanged += (_, _) => ApplyPalette(element, state);
        if (element is InfoBar info)
        {
            info.Loaded += (_, _) => ApplyInfoBarTypography(info);
            info.ActualThemeChanged += (_, _) => ApplyInfoBarTypography(info);
            info.RegisterPropertyChangedCallback(InfoBar.SeverityProperty, (_, _) => ApplyInfoBarSurface(info));
            ApplyInfoBarSurface(info);
        }
    }

    private static void ApplyPalette(FrameworkElement element, PaletteState state)
    {
        var palette = (ResourceDictionary)Application.Current.Resources.ThemeDictionaries[element.ActualTheme == ElementTheme.Dark ? "Dark" : "Light"];
        // Resolve known keys explicitly; compiled XAML resources may be deferred.
        foreach (var key in state.Keys)
            if (!state.CustomKeys.Contains(key) && palette.TryGetValue(key, out var value) && value is SolidColorBrush brush)
                SetPrivateBrush(element, state, key, brush);
    }

    private static IEnumerable<string> ResourceKeys(FrameworkElement element)
    {
        var interactions = new[] { "", "PointerOver", "Pressed", "Disabled" };
        return element switch
        {
            Button => new[] { "Button", "AccentButton" }.SelectMany(prefix =>
                new[] { "Background", "Foreground", "BorderBrush" }.SelectMany(part => interactions.Select(state => prefix + part + state))),
            CheckBox => new[] { "Unchecked", "Checked", "Indeterminate" }.SelectMany(selection =>
                new[] { "CheckBoxForeground", "CheckBoxCheckBackgroundFill", "CheckBoxCheckBackgroundStroke", "CheckBoxCheckGlyphForeground" }
                    .SelectMany(part => interactions.Select(state => part + selection + state))),
            ListViewItem => new[] { "ListViewItemForeground", "ListViewItemForegroundPointerOver", "ListViewItemForegroundSelected",
                "ListViewItemBackground", "ListViewItemBackgroundPointerOver", "ListViewItemBackgroundPressed", "ListViewItemBackgroundSelected",
                "ListViewItemBackgroundSelectedPointerOver", "ListViewItemBackgroundSelectedPressed", "ListViewItemBackgroundSelectedDisabled",
                "ListViewItemDragBackground", "ListViewItemDragForeground", "ListViewItemFocusBorderBrush", "ListViewItemFocusSecondaryBorderBrush",
                "ListViewItemFocusVisualPrimaryBrush", "ListViewItemFocusVisualSecondaryBrush", "ListViewItemSelectionIndicatorBrush",
                "ListViewItemSelectionIndicatorPointerOverBrush", "ListViewItemSelectionIndicatorPressedBrush", "ListViewItemSelectionIndicatorDisabledBrush" },
            ToolTip => new[] { "ToolTipBackgroundBrush", "ToolTipForegroundBrush", "ToolTipBorderBrush" },
            InfoBar => new[] { "Error", "Warning", "Success", "Informational" }.SelectMany(severity =>
                new[] { "BackgroundBrush", "IconBackground", "IconForeground" }.Select(part => "InfoBar" + severity + "Severity" + part))
                    .Concat(new[] { "InfoBarTitleForeground", "InfoBarMessageForeground", "InfoBarBorderBrush", "InfoBarHyperlinkButtonForeground" }),
            _ => Array.Empty<string>()
        };
    }

    internal static void SetCustomBrush(FrameworkElement element, string key, Brush brush)
    {
        EnsureConfigured(element);
        var state = States.GetValue(element, _ => new PaletteState());
        state.CustomKeys.Add(key);
        SetPrivateBrush(element, state, key, brush);
    }

    internal static void RestoreBrush(FrameworkElement element, string key)
    {
        EnsureConfigured(element);
        var state = States.GetValue(element, _ => new PaletteState());
        state.CustomKeys.Remove(key);
        var palette = (ResourceDictionary)Application.Current.Resources.ThemeDictionaries[element.ActualTheme == ElementTheme.Dark ? "Dark" : "Light"];
        SetPrivateBrush(element, state, key, (Brush)palette[key]);
    }

    private static void SetPrivateBrush(FrameworkElement element, PaletteState state, string key, Brush source)
    {
        if (source is not SolidColorBrush solid) return;
        if (state.Brushes.TryGetValue(key, out var owned))
        {
            owned.Color = solid.Color;
            owned.Opacity = solid.Opacity;
            return;
        }
        owned = new SolidColorBrush(solid.Color) { Opacity = solid.Opacity };
        // A mutable resource belongs to one dictionary and is inserted only once.
        // Later theme/selection changes mutate this retained, privately created brush.
        element.Resources.Remove(key);
        element.Resources[key] = owned;
        state.Brushes.Add(key, owned);
    }

    private static void ApplyInfoBarSurface(InfoBar info)
    {
        var palette = (ResourceDictionary)Application.Current.Resources.ThemeDictionaries[info.ActualTheme == ElementTheme.Dark ? "Dark" : "Light"];
        info.Background = (Brush)palette[$"InfoBar{info.Severity}SeverityBackgroundBrush"];
        info.Foreground = (Brush)palette["PrimaryTextBrush"];
    }

    private static void ApplyInfoBarTypography(InfoBar info)
    {
        ApplyInfoBarSurface(info);
        // The SDK uses static font-size resources for these two named parts.
        // Change only typography; retain severity icons, automation and close behavior.
        void Visit(DependencyObject parent)
        {
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is TextBlock text && text.Name is "Title" or "Message")
                {
                    text.FontFamily = info.FontFamily;
                    text.FontSize = 12;
                }
                else if (child is Button close && close.Name == "CloseButton")
                {
                    // The native InfoBar supplies its own AppBar brush aliases here.
                    SetIsEnabled(close, true);
                    close.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                    close.Foreground = info.Foreground;
                    close.BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                }
                Visit(child);
            }
        }
        Visit(info);
    }
}
