using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using PureProject.Infrastructure;

namespace PureProject.WinUI;

public sealed partial class MainWindow
{
    private Task ShowSettingsAsync() => ShowSettingsPageAsync();

    private async Task ShowSettingsPageAsync()
    {
        if (!_ready || _busy) return;
        var selectedPage = 0;
        Dictionary<string, string>? shortcutDraft = null;
        var rebuild = true;
        while (rebuild)
        {
            rebuild = false;
            var saving = false;
            var restoringControls = false;
            StackPanel? shortcutsPage = null;
            Func<Task>? transferAction = null;
            var error = new InfoBar { Severity = InfoBarSeverity.Error, IsOpen = false, Visibility = Visibility.Collapsed };
            error.Closed += (_, _) => error.Visibility = Visibility.Collapsed;
            var validation = new EditorValidation(error);
            var panel = new Grid { Background = ThemeBrush("AppBackgroundBrush") };
            var interactionHost = new ContentControl { Content = panel, HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
            var container = new Grid { Width = 558, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
            var dialog = new ContentDialog { Tag = "Settings", Title = "设置", Content = container, CloseButtonText = "关闭" };
            void RebuildSettings()
            {
                if (shortcutsPage is not null)
                    shortcutDraft = shortcutsPage.Children.OfType<TextBox>()
                        .Where(input => AutomationProperties.GetAutomationId(input).StartsWith("ShortcutInput_", StringComparison.Ordinal))
                        .ToDictionary(input => AutomationProperties.GetAutomationId(input)["ShortcutInput_".Length..],
                            input => ShortcutSettings.Parse(input.Text).ToString(), StringComparer.Ordinal);
                rebuild = true;
                dialog.Hide();
            }
            async Task<bool> SaveSettingsAsync(AppSettings updated)
            {
                if (saving) return false;
                validation.Clear();
                saving = _busy = true;
                // Finish the native selection/toggle event before disabling its host.
                // Disabling inside SelectionChanged invalidates ComboBox's active UIA item.
                await Task.Yield();
                interactionHost.IsEnabled = false;
                try
                {
                    if (_settingsRepository is null || !_settingsLoaded)
                        throw new InvalidOperationException("原设置文件无法读取，已阻止覆盖。请先保留并检查数据目录中的 settings.json，再重启应用。");
                    var backupChanged = updated.AutoBackupEnabled != _settings.AutoBackupEnabled
                        || updated.AutoBackupIntervalMinutes != _settings.AutoBackupIntervalMinutes;
                    if (updated.AutoBackupEnabled && backupChanged)
                        await Task.Run(_autoBackupService.EnsureBackupDirectoryWritable);
                    await _settingsRepository.SaveAsync(updated);
                    _settings = updated;
                    ConfigureShortcuts();
                    if (backupChanged) ConfigureAutoBackup();
                    return true;
                }
                catch (Exception ex) { validation.Show(ex.Message); return false; }
                finally { saving = _busy = false; interactionHost.IsEnabled = true; }
            }

            var theme = new ComboBox { ItemsSource = new[] { "跟随系统", "浅色", "深色" }, SelectedIndex = _settings.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 }, Visibility = Visibility.Collapsed };
            var appearance = Column(20); appearance.Children.Add(theme);
            var themeButtons = EditorChoiceButtons(theme, new[] { "跟随系统", "浅色", "深色" });
            foreach (var button in themeButtons.Children.OfType<Button>()) button.CornerRadius = new CornerRadius(6);
            appearance.Children.Add(SettingsSection("主题", themeButtons));
            appearance.Children.Add(EditorText("跟随系统会自动使用 Windows 的浅色或深色外观。", 12, "SecondaryTextBrush"));
            theme.SelectionChanged += async (_, _) =>
            {
                if (restoringControls) return;
                if (await SaveSettingsAsync(_settings with { Theme = new[] { "System", "Light", "Dark" }[theme.SelectedIndex] }))
                {
                    ApplyTheme(); Render(); RebuildSettings();
                }
                else
                {
                    restoringControls = true;
                    theme.SelectedIndex = _settings.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
                    restoringControls = false;
                }
            };

            var backup = Column(20);
            var enabled = new ToggleSwitch { Header = "自动备份", OnContent = "开启", OffContent = "关闭", IsOn = _settings.AutoBackupEnabled };
            AutomationProperties.SetAutomationId(enabled, "SettingsAutoBackupEnabled");
            AutomationProperties.SetName(enabled, "自动备份");
            var minutes = new[] { 1, 5, 10, 15, 30, 60, 120 };
            var interval = new ComboBox
            {
                Header = "自动备份时间", ItemsSource = new[] { "1m", "5m", "10m", "15m", "30m", "1h", "2h" },
                SelectedIndex = Array.IndexOf(minutes, _settings.AutoBackupIntervalMinutes),
                IsEnabled = enabled.IsOn, HorizontalAlignment = HorizontalAlignment.Stretch
            };
            AutomationProperties.SetAutomationId(interval, "SettingsAutoBackupInterval");
            AutomationProperties.SetName(interval, "自动备份时间");
            var backupHint = EditorText("", 12, "SecondaryTextBrush");
            void RefreshBackupControls()
            {
                restoringControls = true;
                enabled.IsOn = _settings.AutoBackupEnabled;
                interval.SelectedIndex = Array.IndexOf(minutes, _settings.AutoBackupIntervalMinutes);
                interval.IsEnabled = enabled.IsOn;
                backupHint.Text = enabled.IsOn ? "应用运行期间，按所选间隔保存备份。" : "自动备份已关闭，备份时间不生效。";
                restoringControls = false;
            }
            RefreshBackupControls();
            enabled.Toggled += async (_, _) =>
            {
                if (restoringControls) return;
                await SaveSettingsAsync(_settings with { AutoBackupEnabled = enabled.IsOn });
                RefreshBackupControls();
            };
            interval.SelectionChanged += async (_, _) =>
            {
                if (restoringControls || interval.SelectedIndex < 0 || !_settings.AutoBackupEnabled) return;
                await SaveSettingsAsync(_settings with { AutoBackupIntervalMinutes = minutes[interval.SelectedIndex] });
                RefreshBackupControls();
            };
            backup.Children.Add(enabled); backup.Children.Add(interval); backup.Children.Add(backupHint);
            var location = Column(8);
            var backupPath = EditorText(_autoBackupService.BackupDirectory, 12, "SecondaryTextBrush");
            backupPath.IsTextSelectionEnabled = true;
            AutomationProperties.SetAutomationId(backupPath, "SettingsBackupPath");
            location.Children.Add(backupPath);
            location.Children.Add(EditorText("在软件安装目录的 backup 文件夹中，按项目文件分别建文件夹，保存带时间戳的完整备份。", 12, "SecondaryTextBrush"));
            backup.Children.Add(SettingsSection("自动备份位置", location));

            Task QueueTransferAsync(Func<Task> action)
            {
                transferAction = action; dialog.Hide(); return Task.CompletedTask;
            }
            var data = Column(24);
            var export = EditorButton("导出数据", () => QueueTransferAsync(ShowExportAsync), true);
            var import = EditorButton("选择备份文件", () => QueueTransferAsync(ImportAsync));
            AutomationProperties.SetAutomationId(export, "SettingsExport");
            AutomationProperties.SetAutomationId(import, "SettingsImport");
            var exportSection = SettingsSection("数据备份", export);
            exportSection.Children.Add(EditorText("选择备份格式和保存位置，导出全部项目或当前项目。", 12, "SecondaryTextBrush"));
            var importSection = SettingsSection("数据恢复", import);
            importSection.Children.Add(EditorText("选择备份文件并导入数据；导入前会显示项目数量和替换范围。", 12, "SecondaryTextBrush"));
            data.Children.Add(exportSection); data.Children.Add(importSection);
            var shortcuts = BuildShortcutSettingsPage(SaveSettingsAsync, shortcutDraft);
            shortcutsPage = shortcuts;

            var about = Column(24);
            about.Children.Add(SettingsSection("软件名称", EditorText("简项 PureProject", 20)));
            var version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "未知版本";
            about.Children.Add(SettingsSection("版本号", EditorText(version, 15)));
            about.Children.Add(SettingsSection("仓库地址", SettingsLink("https://github.com/Hullayo/PureProject", "SettingsRepositoryLink")));
            about.Children.Add(SettingsSection("问题反馈", SettingsLink("https://github.com/Hullayo/PureProject/issues", "SettingsIssuesLink")));

            var pages = new[] { appearance, backup, data, shortcuts, about };
            var navigation = Column(2); navigation.Padding = new Thickness(8, 12, 8, 12);
            var contentHost = new Grid { Padding = new Thickness(20, 16, 20, 20) };
            var navButtons = new List<Button>();
            void SelectPage(int selected)
            {
                selectedPage = selected;
                contentHost.Children.Clear(); contentHost.Children.Add(pages[selected]);
                for (var i = 0; i < navButtons.Count; i++)
                {
                    navButtons[i].Foreground = ThemeBrush(i == selected ? "AccentTextBrush" : "SecondaryTextBrush");
                    navButtons[i].Background = ThemeBrush(i == selected ? "AccentLightBrush" : "AppBackgroundBrush");
                    if (i == selected) PreserveButtonPalette(navButtons[i]); else RestoreButtonPalette(navButtons[i]);
                    AutomationProperties.SetHelpText(navButtons[i], i == selected ? "当前设置页" : "打开设置页");
                    AutomationProperties.SetItemStatus(navButtons[i], i == selected ? "已选中" : "未选中");
                }
            }
            var tabNames = new[] { "主题", "自动备份", "数据管理", "快捷键", "关于" };
            var tabIds = new[] { "appearance", "backup", "data", "shortcuts", "about" };
            for (var i = 0; i < tabNames.Length; i++)
            {
                var index = i;
                var button = EditorButton(tabNames[index], () => { SelectPage(index); return Task.CompletedTask; });
                AutomationProperties.SetAutomationId(button, "SettingsPage_" + tabIds[index]);
                button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Left;
                button.Padding = new Thickness(12, 8, 12, 8); button.CornerRadius = new CornerRadius(6); button.BorderThickness = new Thickness(0); button.MinHeight = 36;
                navButtons.Add(button); navigation.Children.Add(button);
            }
            SelectPage(selectedPage);
            panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) }); panel.ColumnDefinitions.Add(new ColumnDefinition());
            panel.Children.Add(new Border { Child = navigation, BorderThickness = new Thickness(0, 0, 1, 0), BorderBrush = ThemeBrush("BorderBrush") });
            var contentScroll = Scroll(contentHost); contentScroll.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            Grid.SetColumn(contentScroll, 1); panel.Children.Add(contentScroll);
            container.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); container.RowDefinitions.Add(new RowDefinition());
            container.Children.Add(error); Grid.SetRow(interactionHost, 1); container.Children.Add(interactionHost);
            dialog.Closing += (_, args) => { if (saving) args.Cancel = true; };
            void RefreshSystemTheme(FrameworkElement sender, object args)
            {
                if (_settings.Theme == "System" && !saving && ReferenceEquals(_currentActiveDialog, dialog)) RebuildSettings();
            }
            Root.ActualThemeChanged += RefreshSystemTheme;
            try
            {
                do
                {
                    await DialogAsync(dialog);
                    if (transferAction is not { } action) break;
                    transferAction = null;
                    try { await action(); }
                    catch (Exception ex) { validation.Show(ex.Message); }
                    SelectPage(2);
                } while (true);
            }
            finally { Root.ActualThemeChanged -= RefreshSystemTheme; }
        }
    }

    private static HyperlinkButton SettingsLink(string address, string id)
    {
        var link = new HyperlinkButton
        {
            Content = EditorText(address, 12, "AccentTextBrush"), NavigateUri = new Uri(address),
            Padding = new Thickness(0, 4, 0, 4), HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left, FontFamily = UiFont
        };
        AutomationProperties.SetAutomationId(link, id); AutomationProperties.SetName(link, address);
        return link;
    }

    private static StackPanel SettingsSection(string title, UIElement content)
    {
        var section = Column(8); section.Children.Add(EditorText(title, 12, "MutedTextBrush")); section.Children.Add(content); return section;
    }
}
