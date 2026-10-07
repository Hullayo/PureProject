using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Runtime.CompilerServices;

namespace PureProject.WinUI;

public sealed partial class MainWindow
{
    private static bool _activeDark;
    private const string AppFontSource = "ms-appx:///Assets/Fonts/HarmonyOS_Sans_SC.ttf#HarmonyOS Sans SC";
    private static readonly FontFamily UiFont = new(AppFontSource);
    private static readonly FontFamily SerifFont = UiFont;
    private readonly ConditionalWeakTable<ContentDialog, Action> _configuredDialogStyles = new();

    private static Brush ContrastForeground(Windows.UI.Color background)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255d;
            return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
        }
        var luminance = .2126 * Linear(background.R) + .7152 * Linear(background.G) + .0722 * Linear(background.B);
        var blackContrast = (luminance + .05) / .05;
        var whiteContrast = 1.05 / (luminance + .05);
        return new SolidColorBrush(blackContrast >= whiteContrast ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White);
    }

    private static Brush ThemeBrush(string key)
    {
        var resources = Application.Current.Resources;
        var palette = (ResourceDictionary)resources.ThemeDictionaries[_activeDark ? "Dark" : "Light"];
        if (palette.ContainsKey(key) && palette[key] is Brush brush) return brush;
        if (resources.ContainsKey(key) && resources[key] is Brush fallback) return fallback;
        throw new InvalidOperationException($"The color resource '{key}' is not defined.");
    }

    // Keep the native ContentDialog button peers, deferrals, validation, and result semantics.
    private bool ConfigureDialogStyle(ContentDialog dialog)
    {
        var drawer = Equals(dialog.Tag, "TaskDetail");
        var settings = Equals(dialog.Tag, "Settings");
        var projectEditor = Equals(dialog.Tag, "ProjectEditor");
        var groupEditor = Equals(dialog.Tag, "TaskGroupEditor");
        var height = Math.Max(300, Root.ActualHeight - 48);
        var settingsHeight = Math.Min(613, Math.Max(320, Root.ActualHeight * .8));
        double ProjectHeight() => Math.Min(dialog.Resources.ContainsKey("ProjectEditorPreferredHeight") ? (double)dialog.Resources["ProjectEditorPreferredHeight"] : 586, Math.Max(320, Root.ActualHeight * .86));
        dialog.RequestedTheme = _activeDark ? ElementTheme.Dark : ElementTheme.Light;
        dialog.FontFamily = UiFont;
        dialog.FontSize = 13;
        dialog.Background = ThemeBrush(drawer ? "SidebarBackgroundBrush" : projectEditor || groupEditor ? "CardBackgroundBrush" : "AppBackgroundBrush");
        dialog.Foreground = ThemeBrush("PrimaryTextBrush");
        dialog.BorderBrush = ThemeBrush(drawer ? "BorderStrongBrush" : "BorderBrush");
        dialog.BorderThickness = drawer ? new Thickness(1, 0, 0, 0) : new Thickness(1);
        dialog.CornerRadius = new CornerRadius(drawer ? 0 : settings ? 12 : projectEditor || groupEditor ? 6 : 2);
        // WinUI may host its named smoke layer outside the dialog visual tree. Keep it
        // transparent and render the modal scrim in our own template layer instead.
        dialog.Resources["ContentDialogSmokeFill"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        dialog.Resources["ContentDialogMinWidth"] = drawer ? 340d : settings ? 560d : projectEditor ? 420d : groupEditor ? 360d : 320d;
        dialog.Resources["ContentDialogMaxWidth"] = drawer ? 340d : settings ? 560d : projectEditor ? 420d : groupEditor ? 360d : Math.Min(860, Math.Max(320, Root.ActualWidth - 64));
        dialog.Resources["ContentDialogMinHeight"] = drawer ? height : settings ? Math.Min(560, settingsHeight) : 184d;
        dialog.Resources["ContentDialogMaxHeight"] = drawer ? height : settings ? settingsHeight : Math.Max(320, Root.ActualHeight * .86);
        dialog.Resources["ContentDialogButtonSpacing"] = 8d;
        dialog.Style = (Style)Application.Current.Resources["PureProjectContentDialogStyle"];
        dialog.PrimaryButtonStyle = (Style)Application.Current.Resources["CompactAccentButtonStyle"];
        dialog.SecondaryButtonStyle = (Style)Application.Current.Resources["CompactButtonStyle"];
        dialog.CloseButtonStyle = (Style)Application.Current.Resources["CompactButtonStyle"];

        // Refresh the palette and size resources on every show, but retain one event
        // registration per dialog instance. Weak keys do not keep closed editors alive.
        if (_configuredDialogStyles.TryGetValue(dialog, out var refreshHeader))
        {
            refreshHeader();
            return drawer;
        }
        Button? headerCloseButton = null;
        var resizeSubscribed = false;
        var headerLayoutSubscribed = false;
        var opened = false;
        var closeRequested = false;
        void CloseDialog(object sender, RoutedEventArgs args)
        {
            // The template can accept input before ContentDialog raises Opened.
            // Retain that request until the native opening transition has finished.
            if (opened) { closeRequested = false; dialog.Hide(); }
            else closeRequested = true;
        }
        bool BindHeaderClose()
        {
            var close = FindDialogPart<Button>(dialog, "HeaderCloseButton");
            if (ReferenceEquals(headerCloseButton, close)) return close is not null;
            if (headerCloseButton is not null) headerCloseButton.Click -= CloseDialog;
            headerCloseButton = close;
            if (headerCloseButton is not null) headerCloseButton.Click += CloseDialog;
            return headerCloseButton is not null;
        }
        void StopWatchingHeaderLayout()
        {
            if (!headerLayoutSubscribed) return;
            dialog.LayoutUpdated -= HeaderLayoutUpdated;
            headerLayoutSubscribed = false;
        }
        void WatchHeaderTemplate()
        {
            if (BindHeaderClose()) { StopWatchingHeaderLayout(); return; }
            if (headerLayoutSubscribed) return;
            dialog.LayoutUpdated += HeaderLayoutUpdated;
            headerLayoutSubscribed = true;
        }
        void HeaderLayoutUpdated(object? sender, object args) => WatchHeaderTemplate();
        _configuredDialogStyles.Add(dialog, WatchHeaderTemplate);

        void ResizePanel(object sender, SizeChangedEventArgs args)
        {
            if (FindDialogPart<Border>(dialog, "BackgroundElement") is not { } background) return;
            if (drawer) background.Height = background.MinHeight = background.MaxHeight = Math.Max(300, Root.ActualHeight - 48);
            else if (settings)
            {
                var available = Math.Min(613, Math.Max(320, Root.ActualHeight * .8));
                background.MinHeight = Math.Min(560, available);
                background.Height = background.MaxHeight = available;
            }
            else if (projectEditor)
            {
                background.MaxHeight = Math.Max(320, Root.ActualHeight * .86);
                background.Height = ProjectHeight();
            }
        }

        dialog.Loaded += (_, _) =>
        {
            WatchHeaderTemplate();
            // Named command buttons can receive input during the opening transition.
            // Give them their final columns/visibility as soon as the template loads.
            ConfigureDialogButtons(dialog);
        };
        dialog.Opened += (_, _) =>
        {
            opened = true;
            dialog.Resources["PureProjectDialogOpened"] = true;
            // A theme/template refresh can replace the header button between shows.
            // Detach its old instance before attaching the current template part.
            WatchHeaderTemplate();
            // Apply layout after template creation: merged styles can resolve resources before
            // the dialog-local dictionary is available, so layout does not use custom theme keys.
            if (FindDialogPart<ScrollViewer>(dialog, "ContentScrollViewer") is { } body)
                body.Padding = drawer || settings || projectEditor ? new Thickness(0) : groupEditor ? new Thickness(16) : new Thickness(20, 16, 20, 20);
            if (FindDialogPart<ContentControl>(dialog, "Title") is { } title && (drawer || settings || projectEditor || groupEditor))
                title.FontSize = 16;
            if (settings && FindDialogPart<Grid>(dialog, "TitleBar") is { } header)
            {
                header.Height = 54;
                header.Padding = new Thickness(20, 14, 20, 14);
                header.Background = ThemeBrush("AppBackgroundBrush");
            }
            else if (projectEditor && FindDialogPart<Grid>(dialog, "TitleBar") is { } projectHeader)
            {
                projectHeader.Height = 58;
                projectHeader.Padding = new Thickness(20, 16, 20, 16);
            }
            else if (groupEditor && FindDialogPart<Grid>(dialog, "TitleBar") is { } groupHeader)
            {
                groupHeader.Height = 48;
                groupHeader.Padding = new Thickness(16, 0, 16, 0);
            }
            if (FindDialogPart<Border>(dialog, "TitleAccentBorder") is { } accent)
                accent.Visibility = drawer ? Visibility.Visible : Visibility.Collapsed;
            if (FindDialogPart<Border>(dialog, "TitleSecondRule") is { } secondRule)
                secondRule.Visibility = drawer ? Visibility.Visible : Visibility.Collapsed;
            if (FindDialogPart<Microsoft.UI.Xaml.Shapes.Rectangle>(dialog, "SmokeLayerBackground") is { } smoke)
                smoke.Fill = (Brush)dialog.Resources["ContentDialogSmokeFill"];
            if (FindDialogPart<Microsoft.UI.Xaml.Shapes.Rectangle>(dialog, "ApplicationDialogScrim") is { } scrim)
            {
                scrim.Visibility = drawer ? Visibility.Collapsed : Visibility.Visible;
                scrim.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(projectEditor ? (byte)115 : (byte)128, 0, 0, 0));
            }
            if (FindDialogPart<Border>(dialog, "BackgroundElement") is { } panel)
            {
                panel.HorizontalAlignment = drawer ? HorizontalAlignment.Right : HorizontalAlignment.Center;
                panel.VerticalAlignment = drawer ? VerticalAlignment.Top : VerticalAlignment.Center;
                panel.Margin = drawer ? new Thickness(0, 48, 0, 0) : new Thickness(0);
                panel.MinWidth = (double)dialog.Resources["ContentDialogMinWidth"];
                panel.MaxWidth = (double)dialog.Resources["ContentDialogMaxWidth"];
                panel.MinHeight = (double)dialog.Resources["ContentDialogMinHeight"];
                panel.MaxHeight = (double)dialog.Resources["ContentDialogMaxHeight"];
                if (drawer || settings || projectEditor || groupEditor) panel.Width = (double)dialog.Resources["ContentDialogMinWidth"];
                if (drawer) panel.Height = (double)dialog.Resources["ContentDialogMinHeight"];
                else if (settings) panel.Height = (double)dialog.Resources["ContentDialogMaxHeight"];
                else if (projectEditor) panel.Height = ProjectHeight();
            }
            ConfigureDialogButtons(dialog);
            if ((drawer || settings || projectEditor) && !resizeSubscribed)
            {
                Root.SizeChanged += ResizePanel;
                resizeSubscribed = true;
            }
            if (closeRequested)
            {
                dialog.DispatcherQueue.TryEnqueue(() =>
                {
                    if (!opened || !closeRequested) return;
                    closeRequested = false;
                    dialog.Hide();
                });
            }
        };
        dialog.Closed += (_, _) =>
        {
            opened = false;
            dialog.Resources["PureProjectDialogOpened"] = false;
            closeRequested = false;
            StopWatchingHeaderLayout();
            if (resizeSubscribed)
            {
                Root.SizeChanged -= ResizePanel;
                resizeSubscribed = false;
            }
            if (headerCloseButton is not null)
            {
                headerCloseButton.Click -= CloseDialog;
                headerCloseButton = null;
            }
        };
        WatchHeaderTemplate();
        return drawer;
    }

    private static void ConfigureDialogButtons(ContentDialog dialog)
    {
        if (FindDialogPart<Grid>(dialog, "CommandSpace") is not { } commands
            || FindDialogPart<Button>(dialog, "PrimaryButton") is not { } primary
            || FindDialogPart<Button>(dialog, "SecondaryButton") is not { } secondary
            || FindDialogPart<Button>(dialog, "CloseButton") is not { } close) return;
        var drawer = Equals(dialog.Tag, "TaskDetail");
        var project = Equals(dialog.Tag, "ProjectEditor");
        var group = Equals(dialog.Tag, "TaskGroupEditor");
        var settings = Equals(dialog.Tag, "Settings");
        var shown = new List<Button>();
        void Include(Button button, string label) { if (!string.IsNullOrEmpty(label)) shown.Add(button); }
        if (drawer)
        {
            if (!string.IsNullOrEmpty(dialog.SecondaryButtonText)) Include(secondary, dialog.SecondaryButtonText);
            else Include(close, dialog.CloseButtonText);
            Include(primary, dialog.PrimaryButtonText);
            secondary.Foreground = ThemeBrush("DangerTextBrush"); secondary.Background = ThemeBrush("AccentLightBrush");
        }
        else if (project || group)
        {
            Include(close, dialog.CloseButtonText); Include(primary, dialog.PrimaryButtonText);
            close.Background = ThemeBrush("SubtleBackgroundBrush");
        }
        else if (!settings)
        {
            Include(primary, dialog.PrimaryButtonText); Include(secondary, dialog.SecondaryButtonText); Include(close, dialog.CloseButtonText);
        }
        foreach (var button in new[] { primary, secondary, close })
        {
            button.Visibility = shown.Contains(button) ? Visibility.Visible : Visibility.Collapsed;
            button.Height = project ? 34 : drawer ? 35 : group ? 30 : double.NaN;
        }
        // ContentDialog owns these template parts. Reparenting them during Opened
        // can race native focus setup; keep them attached and declare traversal order.
        foreach (var button in new[] { primary, secondary, close })
        {
            var index = shown.IndexOf(button);
            button.TabIndex = index >= 0 ? index : int.MaxValue;
            button.IsTabStop = index >= 0;
        }
        commands.Visibility = shown.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        commands.Padding = project ? new Thickness(20, 14, 20, 14) : drawer ? new Thickness(16, 15, 16, 13) : group ? new Thickness(16, 12, 16, 12) : new Thickness(16, 13, 16, 13);
        if (FindDialogPart<Border>(dialog, "FooterSecondRule") is { } footerRule) footerRule.Visibility = drawer ? Visibility.Visible : Visibility.Collapsed;
        if (FindDialogPart<Border>(dialog, "FooterPrimaryRule") is { } footerPrimary) footerPrimary.Visibility = shown.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        commands.Height = project ? 64 : double.NaN;
        commands.BorderBrush = ThemeBrush("BorderBrush"); commands.BorderThickness = new Thickness(0);
        foreach (var column in commands.ColumnDefinitions) column.Width = new GridLength(0);
        if (project || group)
        {
            commands.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            commands.ColumnDefinitions[2].Width = new GridLength(group ? 72 : 90);
            commands.ColumnDefinitions[3].Width = new GridLength(8);
            commands.ColumnDefinitions[4].Width = new GridLength(group ? 72 : 90);
            Grid.SetColumn(close, 2); Grid.SetColumn(primary, 4);
        }
        else
        {
            var positions = shown.Count switch { 3 => new[] { 0, 2, 4 }, 2 => new[] { 0, 4 }, _ => new[] { 4 } };
            for (var index = 0; index < shown.Count; index++)
            {
                Grid.SetColumn(shown[index], positions[index]);
                commands.ColumnDefinitions[positions[index]].Width = new GridLength(1, GridUnitType.Star);
            }
            if (shown.Count >= 2) commands.ColumnDefinitions[3].Width = new GridLength(8);
            if (shown.Count == 3) commands.ColumnDefinitions[1].Width = new GridLength(8);
        }
    }

    private static T? FindDialogPart<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        if (parent is T element && element.Name == name) return element;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var result = FindDialogPart<T>(VisualTreeHelper.GetChild(parent, i), name);
            if (result is not null) return result;
        }
        return null;
    }
}

