using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PureProject.Core;
using PureProject.Infrastructure;
using Windows.System;

namespace PureProject.WinUI;

public sealed partial class MainWindow
{
    private async Task SmokeSettingsPagesAsync(string directory, string theme, List<UiSmokeScreenshot> screenshots)
    {
        var showing = ShowSettingsAsync();
        var dialog = await SmokeWaitForDialogAsync(showing);
        SmokeAssert(Equals(dialog.Tag, "Settings"), "Settings did not open its dedicated dialog template.");
        SmokeSettingsFrameRules(dialog);
        var pages = new[] { "appearance", "backup", "data", "shortcuts", "about" };
        var labels = new[] { "主题", "自动备份", "数据管理", "快捷键", "关于" };
        var navigation = SmokeDescendants<Button>(dialog)
            .Where(button => AutomationProperties.GetAutomationId(button).StartsWith("SettingsPage_", StringComparison.Ordinal)).ToArray();
        SmokeAssert(navigation.Select(AutomationProperties.GetAutomationId).SequenceEqual(pages.Select(page => "SettingsPage_" + page))
            && navigation.Select(button => button.Content?.ToString()).SequenceEqual(labels),
            "Settings must expose only Theme, Automatic backup, Data management, Shortcuts and About as its first-level pages.");
        foreach (var page in pages)
        {
            await SmokeSettingsNavigateAsync(dialog, page);
            SmokeAssert(navigation.Count(button => AutomationProperties.GetItemStatus(button) == "已选中") == 1,
                "Settings navigation does not expose exactly one selected page.");
            SmokeHarmonyText(dialog);
            foreach (var button in navigation) SmokeControlWithinRoot(button, "Settings navigation " + button.Content);
            switch (page)
            {
                case "appearance":
                    foreach (var label in new[] { "跟随系统", "浅色", "深色" })
                        SmokeControlWithinRoot(SmokeFind<Button>(dialog, button => Equals(button.Content, label)), "Theme choice " + label);
                    SmokeAssert(!SmokeDescendants<TextBlock>(dialog).Any(text => text.Text.Contains("颜色方案", StringComparison.Ordinal)),
                        "The removed color-scheme setting remains on the Theme page.");
                    break;
                case "backup":
                    var enabled = SmokeFind<ToggleSwitch>(dialog, control => AutomationProperties.GetAutomationId(control) == "SettingsAutoBackupEnabled");
                    var interval = SmokeFind<ComboBox>(dialog, control => AutomationProperties.GetAutomationId(control) == "SettingsAutoBackupInterval");
                    SmokeControlWithinRoot(enabled, "Automatic backup toggle");
                    SmokeAssert(interval.IsEnabled == enabled.IsOn && interval.Items.Cast<string>().SequenceEqual(new[] { "1m", "5m", "10m", "15m", "30m", "1h", "2h" }),
                        "Automatic backup has an incorrect interval list or enabled state.");
                    var path = SmokeFind<TextBlock>(dialog, control => AutomationProperties.GetAutomationId(control) == "SettingsBackupPath");
                    SmokeAssert(string.Equals(Path.GetFullPath(path.Text), Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "backup")), StringComparison.OrdinalIgnoreCase),
                        "Automatic backup does not display the installation's backup directory.");
                    SmokeElementWithinRoot(path, "Automatic backup directory");
                    break;
                case "data":
                    SmokeControlWithinRoot(SmokeFind<Button>(dialog, button => AutomationProperties.GetAutomationId(button) == "SettingsExport"), "Export data");
                    SmokeControlWithinRoot(SmokeFind<Button>(dialog, button => AutomationProperties.GetAutomationId(button) == "SettingsImport"), "Import backup");
                    SmokeAssert(!SmokeDescendants<TextBox>(dialog).Any() && !SmokeDescendants<PasswordBox>(dialog).Any(),
                        "Data management still exposes removed connection or credential settings.");
                    break;
                case "shortcuts":
                    SmokeAssert(SmokeDescendants<TextBox>(dialog).Count(input => AutomationProperties.GetAutomationId(input).StartsWith("ShortcutInput_", StringComparison.Ordinal))
                        == ShortcutSettings.Definitions.Count, "The first-level Shortcuts page is missing editable shortcut records.");
                    break;
                case "about":
                    var links = SmokeDescendants<HyperlinkButton>(dialog).ToArray();
                    SmokeAssert(links.Length == 2
                        && links.Single(link => AutomationProperties.GetAutomationId(link) == "SettingsRepositoryLink").NavigateUri.AbsoluteUri == "https://github.com/Hullayo/PureProject"
                        && links.Single(link => AutomationProperties.GetAutomationId(link) == "SettingsIssuesLink").NavigateUri.AbsoluteUri == "https://github.com/Hullayo/PureProject/issues",
                        "About must provide the repository and issues links.");
                    foreach (var text in new[] { "软件名称", "简项 PureProject", "版本号", typeof(MainWindow).Assembly.GetName().Version!.ToString(3), "仓库地址", "问题反馈" })
                        SmokeElementWithinRoot(SmokeFind<TextBlock>(dialog, label => label.Text == text), "About " + text);
                    SmokeAssert(!SmokeDescendants<TextBox>(dialog).Any(), "Shortcut editors remain in About.");
                    break;
            }
            screenshots.Add(await SmokeCaptureAsync(directory, $"settings-{page}-{theme.ToLowerInvariant()}.png", dialog));
            if (page == "shortcuts")
            {
                var save = SmokeFind<Button>(dialog, button => AutomationProperties.GetAutomationId(button) == "ShortcutsSave");
                save.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
                await SmokeLayoutAsync();
                SmokeControlWithinRoot(save, "Save shortcuts");
                screenshots.Add(await SmokeCaptureAsync(directory, $"settings-shortcuts-actions-{theme.ToLowerInvariant()}.png", dialog));
            }
        }
        SmokeInvoke(SmokeHeaderCloseButton(dialog));
        await SmokeAwaitDialogAsync(showing, dialog);
    }

    private void SmokeSettingsFrameRules(ContentDialog dialog)
    {
        var panel = SmokeFind<Border>(dialog, element => element.Name == "BackgroundElement");
        var header = SmokeFind<Grid>(dialog, element => element.Name == "TitleBar");
        var body = SmokeFind<ScrollViewer>(dialog, element => element.Name == "ContentScrollViewer");
        var close = SmokeHeaderCloseButton(dialog);
        SmokeElementWithinRoot(panel, "Settings panel");
        SmokeControlWithinRoot(close, "Settings header close action");
        SmokeFullyWithin(header, panel, "Settings header");
        SmokeFullyWithin(body, panel, "Settings body");
        SmokeAssert(Math.Abs(panel.ActualWidth - 560) < 1 && Math.Abs(header.ActualHeight - 54) < 1
            && body.Padding == new Thickness(0), "Settings lost its dedicated panel width, header height or unpadded body.");
        SmokeAssert(SmokeDescendants<Grid>(dialog).Any(grid => grid.ColumnDefinitions.Count == 2
            && grid.ColumnDefinitions[0].Width.IsAbsolute && Math.Abs(grid.ColumnDefinitions[0].Width.Value - 140) < .5),
            "Settings is missing its 140-DIP navigation column.");
        var rule = SmokeFind<Border>(dialog, element => element.Name == "TitleBottomRule");
        // Fractional display scaling can round the nominal 1-DIP rule to the
        // nearest physical pixel; Settings has no footer command boundary.
        var pixelDip = 1 / Root.XamlRoot.RasterizationScale;
        SmokeAssert(SmokeRenderedVisible(rule, dialog) && rule.ActualHeight > 0
            && Math.Abs(rule.ActualHeight - 1) <= pixelDip && rule.Background is SolidColorBrush { Color.A: > 0 }
            && Math.Abs(rule.ActualWidth - (panel.ActualWidth - panel.BorderThickness.Left - panel.BorderThickness.Right)) <= pixelDip + .5,
            $"Settings header divider is hidden or fails to span the panel: {rule.ActualWidth} × {rule.ActualHeight} DIP.");
        SmokeAssert(SmokeFind<Grid>(dialog, element => element.Name == "CommandSpace").Visibility == Visibility.Collapsed
            && SmokeFind<Border>(dialog, element => element.Name == "FooterPrimaryRule").Visibility == Visibility.Collapsed,
            "Settings reserves a footer for hidden dialog commands.");
    }

    private async Task SmokeSettingsNavigateAsync(ContentDialog dialog, string page)
    {
        var button = SmokeFind<Button>(dialog, candidate => AutomationProperties.GetAutomationId(candidate) == "SettingsPage_" + page);
        SmokeInvoke(button);
        await SmokeLayoutAsync();
        SmokeAssert(AutomationProperties.GetItemStatus(button) == "已选中", "Settings failed to select " + page + ".");
    }

    private async Task SmokeSettingsBackupAsync()
    {
        var repository = _settingsRepository ?? throw new InvalidOperationException("The settings repository is unavailable.");
        var showing = ShowSettingsAsync();
        var dialog = await SmokeWaitForDialogAsync(showing);
        await SmokeSettingsNavigateAsync(dialog, "backup");
        var enabled = SmokeFind<ToggleSwitch>(dialog, control => AutomationProperties.GetAutomationId(control) == "SettingsAutoBackupEnabled");
        var interval = SmokeFind<ComboBox>(dialog, control => AutomationProperties.GetAutomationId(control) == "SettingsAutoBackupInterval");
        async Task Toggle(bool value)
        {
            if (enabled.IsOn != value)
            {
                var peer = FrameworkElementAutomationPeer.CreatePeerForElement(enabled);
                if (peer?.GetPattern(PatternInterface.Toggle) is not IToggleProvider toggle)
                    throw new InvalidOperationException("Automatic backup has no native toggle provider.");
                toggle.Toggle();
            }
            await SmokeWaitAsync(() => !_busy && enabled.IsOn == value && _settings.AutoBackupEnabled == value && interval.IsEnabled == value,
                "The automatic-backup toggle did not apply or update its interval control.");
            SmokeAssert((await repository.LoadAsync()).AutoBackupEnabled == value, "The automatic-backup toggle was not persisted immediately.");
            SmokeAssert((_autoBackupTimer?.IsEnabled == true) == value, "The automatic-backup timer disagrees with the saved toggle.");
        }
        await Toggle(true);
        SmokeAssert(interval.Focus(FocusState.Keyboard), "The backup interval cannot receive keyboard focus.");
        var intervalPeer = FrameworkElementAutomationPeer.CreatePeerForElement(interval);
        if (intervalPeer?.GetPattern(PatternInterface.ExpandCollapse) is not IExpandCollapseProvider intervalPopup)
            throw new InvalidOperationException("The backup interval has no native expand/collapse provider.");
        intervalPopup.Expand();
        await SmokeWaitAsync(() => interval.IsDropDownOpen && interval.ContainerFromIndex(0) is ComboBoxItem { ActualHeight: > 0 },
            "The backup interval popup did not open.");
        SmokeAssert(HasOpenShortcutPopup(dialog, Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(dialog.XamlRoot) as DependencyObject),
            "The close-shortcut guard failed to defer to the open backup interval popup.");
        intervalPopup.Collapse();
        await SmokeLayoutAsync();
        SmokeAssert(!interval.IsDropDownOpen
            && !HasOpenShortcutPopup(dialog, Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(dialog.XamlRoot) as DependencyObject),
            "The close-shortcut guard still treats the closed interval popup as active.");
        foreach (var (index, minutes) in new[] { (0, 1), (1, 5), (2, 10), (3, 15), (4, 30), (5, 60), (6, 120) })
        {
            if (interval.SelectedIndex != index) await SmokeChooseComboItemAsync(interval, index);
            await SmokeWaitAsync(() => !_busy && _settings.AutoBackupIntervalMinutes == minutes,
                "The backup interval selection was not applied.");
            SmokeAssert((await repository.LoadAsync()).AutoBackupIntervalMinutes == minutes
                && _autoBackupTimer?.Interval == TimeSpan.FromMinutes(minutes), "The saved interval and running backup timer differ.");
        }

        var backupDirectory = _autoBackupService.BackupDirectory;
        var beforeFiles = Directory.GetFiles(backupDirectory, "*.pureproject", SearchOption.AllDirectories).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expected = _projects.ToDictionary(project => project.Id, PmSerializer.ExportPm, StringComparer.Ordinal);
        await RunAutoBackupAsync();
        var createdFiles = Directory.GetFiles(backupDirectory, "*.pureproject", SearchOption.AllDirectories).Where(path => !beforeFiles.Contains(path)).ToArray();
        SmokeAssert(createdFiles.Length == expected.Count && _lastAutoBackupError is null,
            "Running the configured automatic backup did not create one recoverable file per project.");
        var restoredIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in createdFiles)
        {
            var restored = new ProjectExchangeService().ImportFile(file);
            SmokeAssert(restored.Count == 1 && expected.TryGetValue(restored[0].Id, out var original)
                && PmSerializer.ExportPm(restored[0]) == original && restoredIds.Add(restored[0].Id),
                "An automatic backup failed to restore the complete original project.");
            SmokeAssert(string.Equals(Path.GetDirectoryName(Path.GetDirectoryName(file)), Path.TrimEndingDirectorySeparator(backupDirectory), StringComparison.OrdinalIgnoreCase),
                "An automatic backup was not written under its own project folder.");
        }
        await Toggle(false);
        SmokeAssert(!interval.Focus(FocusState.Keyboard), "The disabled backup interval can still receive keyboard focus.");
        var filesWhileDisabled = Directory.GetFiles(backupDirectory, "*.pureproject", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        await RunAutoBackupAsync();
        SmokeAssert(Directory.GetFiles(backupDirectory, "*.pureproject", SearchOption.AllDirectories).Order(StringComparer.Ordinal).SequenceEqual(filesWhileDisabled),
            "Automatic backup wrote another file after being disabled.");
        SmokeInvoke(SmokeHeaderCloseButton(dialog)); await SmokeAwaitDialogAsync(showing, dialog);
        showing = ShowSettingsAsync(); dialog = await SmokeWaitForDialogAsync(showing);
        await SmokeSettingsNavigateAsync(dialog, "backup");
        SmokeAssert(!SmokeFind<ToggleSwitch>(dialog, control => AutomationProperties.GetAutomationId(control) == "SettingsAutoBackupEnabled").IsOn
            && SmokeFind<ComboBox>(dialog, control => AutomationProperties.GetAutomationId(control) == "SettingsAutoBackupInterval") is { IsEnabled: false, SelectedIndex: 6 },
            "Reopening Settings lost the disabled backup state or its selected interval.");
        SmokeInvoke(SmokeHeaderCloseButton(dialog)); await SmokeAwaitDialogAsync(showing, dialog);
    }

    private async Task SmokeSettingsShortcutsAsync()
    {
        var repository = _settingsRepository ?? throw new InvalidOperationException("The settings repository is unavailable.");
        var showing = ShowSettingsAsync();
        var dialog = await SmokeWaitForDialogAsync(showing);
        await SmokeSettingsNavigateAsync(dialog, "shortcuts");
        var input = SmokeFind<TextBox>(dialog, field => AutomationProperties.GetAutomationId(field) == "ShortcutInput_New");
        var save = SmokeFind<Button>(dialog, button => AutomationProperties.GetAutomationId(button) == "ShortcutsSave");
        var before = await File.ReadAllTextAsync(repository.FilePath);
        SmokeAssert(input.Focus(FocusState.Keyboard) && input.IsReadOnly, "The shortcut recorder is not focusable or accepts raw text edits.");
        // Feed the same recorder used by PreviewKeyDown. This checks recording,
        // validation and persistence without claiming physical-key or IME coverage.
        RecordShortcut(input, VirtualKey.F, VirtualKeyModifiers.Control);
        save.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false }); await SmokeLayoutAsync();
        SmokeInvoke(save); await SmokeLayoutAsync();
        SmokeAssert(SmokeFind<InfoBar>(dialog, bar => AutomationProperties.GetAutomationId(bar) == "ShortcutsValidation") is { IsOpen: true, Severity: InfoBarSeverity.Error }
            && await File.ReadAllTextAsync(repository.FilePath) == before && ShortcutSettings.GetEffective(_settings.Shortcuts)["New"] == "Ctrl+N",
            "A duplicate shortcut was accepted or changed persisted settings.");

        input.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false }); await SmokeLayoutAsync();
        RecordShortcut(input, VirtualKey.N, VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu);
        var draft = input.Text;
        RecordShortcut(input, VirtualKey.N, VirtualKeyModifiers.None);
        SmokeAssert(input.Text == draft && await File.ReadAllTextAsync(repository.FilePath) == before,
            "An unmodified text key replaced a valid shortcut draft or saved it prematurely.");
        RecordShortcut(input, VirtualKey.N, VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu);
        var closeInput = SmokeFind<TextBox>(dialog, field => AutomationProperties.GetAutomationId(field) == "ShortcutInput_ClosePanel");
        RecordShortcut(closeInput, VirtualKey.F8, VirtualKeyModifiers.None);
        await SmokeSettingsNavigateAsync(dialog, "data");
        await SmokeSettingsNavigateAsync(dialog, "shortcuts");
        SmokeAssert(input.Text == draft, "Navigating between Settings pages lost the shortcut draft.");
        await SmokeSettingsNavigateAsync(dialog, "appearance");
        var changedTheme = _settings.Theme == "Light" ? "Dark" : "Light";
        SmokeInvoke(SmokeFind<Button>(dialog, button => Equals(button.Content, changedTheme == "Light" ? "浅色" : "深色")));
        dialog = await SmokeWaitForDialogAsync(showing, dialog);
        await SmokeSettingsNavigateAsync(dialog, "shortcuts");
        input = SmokeFind<TextBox>(dialog, field => AutomationProperties.GetAutomationId(field) == "ShortcutInput_New");
        closeInput = SmokeFind<TextBox>(dialog, field => AutomationProperties.GetAutomationId(field) == "ShortcutInput_ClosePanel");
        var savedAfterThemeChange = await repository.LoadAsync();
        SmokeAssert(_settings.Theme == changedTheme && savedAfterThemeChange.Theme == changedTheme
            && input.Text == draft && closeInput.Text == "F8"
            && ShortcutSettings.GetEffective(savedAfterThemeChange.Shortcuts)["New"] == "Ctrl+N"
            && ShortcutSettings.GetEffective(savedAfterThemeChange.Shortcuts)["ClosePanel"] == "Esc",
            "Changing the theme lost an unsaved shortcut draft or saved it before the shortcut Save action.");
        save = SmokeFind<Button>(dialog, button => AutomationProperties.GetAutomationId(button) == "ShortcutsSave");
        save.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false }); await SmokeLayoutAsync();
        SmokeInvoke(save);
        await SmokeWaitAsync(() => !_busy && ShortcutSettings.GetEffective(_settings.Shortcuts)["New"] == "Ctrl+Alt+N", "The custom shortcut did not save.");
        SmokeAssert(ShortcutSettings.GetEffective((await repository.LoadAsync()).Shortcuts)["New"] == "Ctrl+Alt+N"
            && _configuredShortcuts.Any(shortcut => shortcut.Key == VirtualKey.N && shortcut.Modifiers == (VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu))
            && !_configuredShortcuts.Any(shortcut => shortcut.Key == VirtualKey.N && shortcut.Modifiers == VirtualKeyModifiers.Control),
            "The custom shortcut did not replace its previous live binding and persisted value.");
        SmokeAssert(ShortcutSettings.GetEffective((await repository.LoadAsync()).Shortcuts)["ClosePanel"] == "F8"
            && _configuredShortcuts.Any(shortcut => shortcut.Key == VirtualKey.F8 && shortcut.Modifiers == VirtualKeyModifiers.None)
            && !_configuredShortcuts.Any(shortcut => shortcut.Key == VirtualKey.Escape),
            "The customized Close panel shortcut retained its previous Escape binding.");
        SmokeInvoke(SmokeHeaderCloseButton(dialog)); await SmokeAwaitDialogAsync(showing, dialog);

        var transfer = RunTransferAsync("正在验证自定义关闭快捷键的取消流程", token =>
        {
            token.WaitHandle.WaitOne(TimeSpan.FromSeconds(15));
            token.ThrowIfCancellationRequested();
            return "unexpected completion";
        }, discardResultOnCancellation: true);
        var progress = await SmokeWaitForDialogAsync(transfer);
        SmokeAssert(_busy && CanCloseShortcutDialog(progress), "The close-shortcut guard blocks cancelling an active transfer.");
        // Exercise the dialog-close route used by the custom accelerator. Native
        // key routing itself remains outside this in-process smoke's scope.
        progress.Hide();
        await SmokeAwaitDialogAsync(transfer, progress);
        SmokeAssert(await transfer is null && !_busy && !_dialogOpen, "Closing a transfer from the shortcut route did not cancel its worker.");

        showing = ShowSettingsAsync(); dialog = await SmokeWaitForDialogAsync(showing);
        await SmokeSettingsNavigateAsync(dialog, "shortcuts");
        input = SmokeFind<TextBox>(dialog, field => AutomationProperties.GetAutomationId(field) == "ShortcutInput_New");
        SmokeAssert(input.Text.Replace(" ", "", StringComparison.Ordinal) == "Ctrl+Alt+N", "Reopening Settings lost the saved custom shortcut.");
        before = await File.ReadAllTextAsync(repository.FilePath);
        var reset = SmokeFind<Button>(dialog, button => AutomationProperties.GetAutomationId(button) == "ShortcutsReset");
        reset.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false }); await SmokeLayoutAsync();
        SmokeInvoke(reset); await SmokeLayoutAsync();
        SmokeAssert(input.Text.Replace(" ", "", StringComparison.Ordinal) == "Ctrl+N" && await File.ReadAllTextAsync(repository.FilePath) == before,
            "Reset failed to restore default drafts or persisted them before Save.");
        save = SmokeFind<Button>(dialog, button => AutomationProperties.GetAutomationId(button) == "ShortcutsSave");
        SmokeInvoke(save);
        await SmokeWaitAsync(() => !_busy && ShortcutSettings.GetEffective(_settings.Shortcuts)["New"] == "Ctrl+N", "Saving the default shortcuts did not restore them.");
        var restored = ShortcutSettings.GetEffective((await repository.LoadAsync()).Shortcuts);
        SmokeAssert(ShortcutSettings.Definitions.All(definition => restored[definition.Id] == definition.DefaultGesture)
            && _configuredShortcuts.Count == ShortcutSettings.Definitions.Count, "Resetting shortcuts left a custom or duplicate live binding.");
        SmokeInvoke(SmokeHeaderCloseButton(dialog)); await SmokeAwaitDialogAsync(showing, dialog);
    }

    private async Task SmokeSettingsExportReturnAsync()
    {
        var repository = _settingsRepository ?? throw new InvalidOperationException("The settings repository is unavailable.");
        var existed = File.Exists(repository.FilePath);
        var before = existed ? await File.ReadAllTextAsync(repository.FilePath) : null;
        var showing = ShowSettingsAsync();
        var original = await SmokeWaitForDialogAsync(showing);
        await SmokeSettingsNavigateAsync(original, "data");
        var exportButton = SmokeFind<Button>(original, button => AutomationProperties.GetAutomationId(button) == "SettingsExport");
        SmokeInvoke(exportButton);
        var export = await SmokeWaitForDialogAsync(showing, original);
        SmokeAssert(Equals(export.Title, "备份与导出"), "Settings Export did not open the export-options dialog.");
        var format = SmokeFind<ComboBox>(export, control => AutomationProperties.GetAutomationId(control) == "ExportFormat");
        SmokeAssert(format.Items.Count == 3 && format.Items[0].ToString()!.Contains(".pureproject")
            && format.Items[1].ToString()!.Contains(".mm") && format.Items[2].ToString()!.Contains(".xlsx"), "Three lossless exchange formats are not available.");
        await SmokeChooseComboItemAsync(format, 2);
        SmokeAssert(SmokeDescendants<TextBlock>(export).Any(text => text.Text.Contains("分组标题合并")), "Excel editing/restore guidance is missing.");
        // Stop at the application cancel action; operating-system file pickers are
        // intentionally not exercised by this in-process native-control smoke.
        SmokeInvoke(SmokeDialogButton(export, "CloseButton", "取消"));
        var returned = await SmokeWaitForDialogAsync(showing, export);
        SmokeAssert(ReferenceEquals(returned, original) && ReferenceEquals(exportButton,
            SmokeFind<Button>(returned, button => AutomationProperties.GetAutomationId(button) == "SettingsExport")),
            "Cancelling export recreated Settings or its Data management controls.");
        SmokeAssert(AutomationProperties.GetItemStatus(SmokeFind<Button>(returned, button => AutomationProperties.GetAutomationId(button) == "SettingsPage_data")) == "已选中"
            && SmokeRenderedVisible(exportButton, returned), "Cancelling export did not return to the Settings data page.");
        SmokeInvoke(SmokeHeaderCloseButton(returned)); await SmokeAwaitDialogAsync(showing, returned);
        SmokeAssert(File.Exists(repository.FilePath) == existed && (!existed || await File.ReadAllTextAsync(repository.FilePath) == before),
            "Cancelling export unexpectedly changed persisted settings.");
    }

    private async Task SmokeSettingsThemesAsync()
    {
        var repository = _settingsRepository ?? throw new InvalidOperationException("The settings repository is unavailable.");
        var showing = ShowSettingsAsync();
        var dialog = await SmokeWaitForDialogAsync(showing);
        foreach (var (label, theme, expected) in new[]
            { ("跟随系统", "System", ElementTheme.Default), ("浅色", "Light", ElementTheme.Light), ("深色", "Dark", ElementTheme.Dark) })
        {
            var button = SmokeFind<Button>(dialog, item => item.IsEnabled && Equals(item.Content, label));
            if (_settings.Theme != theme)
            {
                SmokeInvoke(button);
                dialog = await SmokeWaitForDialogAsync(showing, dialog);
            }
            SmokeAssert(Equals(dialog.Tag, "Settings") && _settings.Theme == theme && Root.RequestedTheme == expected
                && (expected == ElementTheme.Default || Root.ActualTheme == expected), "A visible theme choice did not apply its requested appearance.");
            SmokeAssert((await repository.LoadAsync()).Theme == theme, "The theme choice was not persisted immediately.");
        }
        SmokeInvoke(SmokeHeaderCloseButton(dialog)); await SmokeAwaitDialogAsync(showing, dialog);
    }
}
