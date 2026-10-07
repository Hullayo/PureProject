using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using PureProject.Core;
using PureProject.Infrastructure;
using Windows.Storage.Pickers;
using System.Text;

namespace PureProject.WinUI;

public sealed partial class MainWindow
{
    private List<SyncConflict> _syncConflicts = [];

    private async Task ImportAsync()
    {
        if (!_ready || _busy) return;
        var picker = new FileOpenPicker();
        foreach (var extension in new[] { ".pureproject", ".mm", ".xlsx", ".pm", ".json" }) picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        var imported = await RunTransferAsync("正在读取并校验导入文件", token =>
        {
            if (Path.GetExtension(file.Path).ToLowerInvariant() is ".pm" or ".json")
            {
                using var stream = File.OpenRead(file.Path);
                return PmSerializer.ReadInternalAsync(stream, token).GetAwaiter().GetResult().ToList();
            }
            return new ProjectExchangeService().ImportFile(file.Path, token).ToList();
        }, discardResultOnCancellation: true);
        if (imported is null) return;
        var existingIds = _projects.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var collisions = imported.Where(p => existingIds.Contains(p.Id)).ToList();
        var content = Column(12);
        content.Children.Add(Label($"已校验 {imported.Count:N0} 个项目、{imported.Sum(p => p.TaskGroups.Count):N0} 个任务组、{imported.Sum(p => p.Tasks.Count):N0} 条任务。", true));
        content.Children.Add(Label($"新增 {imported.Count - collisions.Count:N0} 个项目；替换 {collisions.Count:N0} 个相同 ID 的项目。导入将一次性保存，可在本次运行中撤销。", true));
        if (collisions.Count > 0) content.Children.Add(Label("将替换：" + string.Join("、", collisions.Take(5).Select(p => p.Name)) + (collisions.Count > 5 ? $" 等 {collisions.Count} 个项目" : ""), true));
        var preview = new ContentDialog { Title = "确认导入", Content = content, PrimaryButtonText = "导入并保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        AutomationProperties.SetAutomationId(preview, "ImportPreview");
        if (await DialogAsync(preview) != ContentDialogResult.Primary) return;
        await ApplyImportedProjectsAsync(imported);
        ShowMessage($"导入完成：{imported.Count:N0} 个项目、{imported.Sum(p => p.Tasks.Count):N0} 条任务，已保存至本地。", InfoBarSeverity.Success);
    }

    private Task ApplyImportedProjectsAsync(IReadOnlyList<Project> imported) => CommitAsync(projects =>
    {
        foreach (var project in imported)
        {
            var index = projects.FindIndex(old => old.Id == project.Id);
            if (index >= 0) projects[index] = project; else projects.Add(project);
        }
    }, $"已导入 {imported.Count:N0} 个项目", collectionOnly: true);

    private async Task<T?> RunTransferAsync<T>(string title, Func<CancellationToken, T> work, bool discardResultOnCancellation = false) where T : class
    {
        if (_busy || _dialogOpen) return null;
        using var cancellation = new CancellationTokenSource();
        var content = Column(12);
        content.Children.Add(Label("数据较多时可能需要一些时间。完成校验前可取消。", true));
        content.Children.Add(new ProgressBar { IsIndeterminate = true, MinWidth = 320, Foreground = ThemeBrush("AccentBrush"), Background = ThemeBrush("SubtleBackgroundBrush") });
        var dialog = new ContentDialog { Title = title, Content = content, CloseButtonText = "取消" };
        AutomationProperties.SetAutomationId(dialog, "TransferProgress");
        var finished = false;
        dialog.Closing += (_, args) =>
        {
            if (finished) return;
            args.Cancel = true; cancellation.Cancel(); dialog.CloseButtonText = "正在取消…";
        };
        var showing = DialogAsync(dialog);
        _busy = true;
        try
        {
            var result = await Task.Run(() => work(cancellation.Token));
            // A canceled read can be discarded even at the final dispatcher handoff.
            // An export that already atomically committed must report success.
            if (discardResultOnCancellation) cancellation.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            ShowMessage("操作已取消。原有项目和目标文件保持完整。"); return null;
        }
        finally { finished = true; _busy = false; dialog.Hide(); await showing; }
    }

    private async Task SaveTextAsync(string text, string name, string extension)
    {
        var picker = new FileSavePicker();
        picker.FileTypeChoices.Add(extension switch { ".pm" => "简项项目", ".csv" => "CSV 表格", ".md" => "Markdown 报告", _ => "JSON 备份" }, new List<string> { extension });
        picker.SuggestedFileName = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        await AtomicFile.WriteAsync(file.Path, text);
        ShowMessage($"已导出至 {file.Path}", InfoBarSeverity.Success);
    }

    private async Task ShowExportAsync()
    {
        if (!_ready || _busy) return;
        var format = new ComboBox { Header = "文件格式", SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var label in new[] { "简项完整数据包（.pureproject）", "开源思维导图（FreeMind / Freeplane .mm）", "Excel 工作簿（.xlsx · 合并单元格）" }) format.Items.Add(label);
        var scope = new ComboBox { Header = "导出范围", SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        scope.Items.Add($"全部项目（{_projects.Count:N0} 个 / {_projects.Sum(p => p.Tasks.Count):N0} 条任务）");
        var current = Current;
        if (current is not null) scope.Items.Add($"当前项目（{current.Tasks.Count:N0} 条任务）");
        var hint = Label("完整数据包适合备份和迁移，包含所有任务属性及扩展字段。", true);
        format.SelectionChanged += (_, _) => hint.Text = format.SelectedIndex switch
        {
            1 => "可用开源 Freeplane / FreeMind 打开。支持编辑层级名称、任务标题与归属后还原；请保留节点标识和数据属性。",
            2 => "每个项目独立工作表，分组标题合并、表头冻结。支持修改可见字段后还原；请保留 ID、结构和元数据工作表。",
            _ => "完整数据包适合备份和迁移，包含所有任务属性及扩展字段。"
        };
        AutomationProperties.SetAutomationId(format, "ExportFormat"); AutomationProperties.SetAutomationId(scope, "ExportScope");
        var panel = Column(12); panel.Children.Add(format); panel.Children.Add(scope); panel.Children.Add(hint);
        panel.Children.Add(Label("三种格式均支持通过本软件导入还原，包含归档项目与任务组；同步地址和凭据不导出。", true));
        var choice = await DialogAsync(new ContentDialog { Title = "备份与导出", Content = panel, PrimaryButtonText = "选择保存位置", SecondaryButtonText = "其他导出", CloseButtonText = "取消" });
        if (choice == ContentDialogResult.Secondary) { await ShowOtherExportsAsync(current); return; }
        if (choice != ContentDialogResult.Primary) return;
        var extension = format.SelectedIndex switch { 1 => ".mm", 2 => ".xlsx", _ => ".pureproject" };
        var selected = scope.SelectedIndex == 1 && current is not null ? new List<Project> { current } : _projects.ToList();
        var picker = new FileSavePicker { SuggestedFileName = $"PureProject-{DateTime.Now:yyyyMMdd-HHmm}" };
        picker.FileTypeChoices.Add(format.SelectedItem.ToString()!, new List<string> { extension });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        var result = await RunTransferAsync("正在生成导出文件", token =>
        {
            new ProjectExchangeService().ExportFile(file.Path, selected, token); return file.Path;
        });
        if (result is not null) ShowMessage($"已导出 {selected.Count:N0} 个项目、{selected.Sum(p => p.Tasks.Count):N0} 条任务至 {result}", InfoBarSeverity.Success);
    }

    private async Task ShowOtherExportsAsync(Project? current)
    {
        var options = new ComboBox { Header = "导出内容", SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        options.Items.Add("全部项目原生备份（JSON）");
        if (current is not null) foreach (var name in new[] { "当前项目（.pm）", "当前项目任务表（CSV）", "当前项目报告（Markdown）" }) options.Items.Add(name);
        var panel = Column(12); panel.Children.Add(options);
        panel.Children.Add(Label("原生 JSON 与 .pm 可重新导入。CSV 和 Markdown 用于阅读与报告，不包含完整还原数据。", true));
        if (await DialogAsync(new ContentDialog { Title = "其他导出", Content = panel, PrimaryButtonText = "选择保存位置", CloseButtonText = "取消" }) != ContentDialogResult.Primary) return;
        var index = options.SelectedIndex;
        var extension = index switch { 1 => ".pm", 2 => ".csv", 3 => ".md", _ => ".json" };
        var picker = new FileSavePicker { SuggestedFileName = $"PureProject-{DateTime.Now:yyyyMMdd-HHmm}" };
        picker.FileTypeChoices.Add(options.SelectedItem.ToString()!, new List<string> { extension });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync(); if (file is null) return;
        var path = file.Path; var snapshot = _projects.ToList();
        var saved = await RunTransferAsync("正在生成导出文件", token =>
        {
            if (index == 0) JsonProjectRepository.ExportBackupAsync(path, snapshot, token).GetAwaiter().GetResult();
            else if (current is not null)
            {
                var text = index switch { 1 => PmSerializer.ExportPm(current), 2 => ExportCsv(current), _ => ExportMarkdown(current) };
                AtomicFile.WriteAsync(path, text, cancellationToken: token).GetAwaiter().GetResult();
            }
            return path;
        });
        if (saved is not null) ShowMessage("已导出至 " + saved, InfoBarSeverity.Success);
    }

    private static string ExportCsv(Project p)
    {
        static string Cell(string? value)
        {
            var text = value ?? "";
            if (text.TrimStart().FirstOrDefault() is '=' or '+' or '-' or '@' || text.StartsWith('\t') || text.StartsWith('\r')) text = "'" + text;
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }
        var output = new StringBuilder("\uFEFF标题,任务组,状态,优先级,截止日期,标签,说明\r\n");
        foreach (var task in p.Tasks)
            output.AppendLine(string.Join(",", new[] { task.Title, p.TaskGroups.FirstOrDefault(g => g.Id == task.TaskGroupId)?.Name,
                Status(p, task)?.Name, PriorityText(task.Priority), task.DueDate, string.Join("; ", task.Tags), task.Description }.Select(Cell)));
        return output.ToString();
    }
    private static string ExportMarkdown(Project p)
    {
        static string Escape(string text) => text.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
        var output = new StringBuilder($"# {Escape(p.Name)}\n\n{p.Description}\n\n导出时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm}\n\n");
        foreach (var group in p.TaskGroups.OrderBy(g => g.SortOrder))
        {
            output.AppendLine($"## {Escape(group.Name)}{(group.Archived ? "（归档）" : "")}\n\n| 任务 | 状态 | 优先级 | 截止日期 |\n|---|---|---|---|");
            foreach (var task in p.Tasks.Where(t => t.TaskGroupId == group.Id)) output.AppendLine($"| {Escape(task.Title)} | {Escape(Status(p, task)?.Name ?? "")} | {PriorityText(task.Priority)} | {task.DueDate ?? "—"} |");
            output.AppendLine();
        }
        return output.ToString();
    }

    private Task ShowSettingsAsync() => ShowSettingsWithDraftAsync(null, null);

    private async Task ShowSettingsWithDraftAsync(string? draftSyncUrl, string? draftSyncToken)
    {
        if (!_ready || _busy) return;
        var theme = new ComboBox { Header = "外观", ItemsSource = new[] { "跟随系统", "浅色", "深色" }, SelectedIndex = _settings.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 }, Visibility = Visibility.Collapsed };
        var url = new TextBox { Header = "同步服务器地址", Text = draftSyncUrl ?? _settings.SyncServerUrl, PlaceholderText = "https://your-server.example" };
        var token = new PasswordBox { Header = "访问令牌", Password = draftSyncToken ?? _settings.SyncToken };
        AutomationProperties.SetAutomationId(url, "SettingsSyncUrl");
        AutomationProperties.SetAutomationId(token, "SettingsSyncToken");
        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsOpen = false, Visibility = Visibility.Collapsed };
        error.Closed += (_, _) => error.Visibility = Visibility.Collapsed;
        var validation = new EditorValidation(error);
        var appearance = Column(20); appearance.Children.Add(theme);
        var themeButtons = EditorChoiceButtons(theme, new[] { "跟随系统", "浅色", "深色" });
        foreach (var button in themeButtons.Children.OfType<Button>()) button.CornerRadius = new CornerRadius(6);
        var themeSection = SettingsSection("主题", themeButtons);
        var themeHint = EditorText("跟随系统会自动使用 Windows 的浅色或深色外观。", 11, "MutedTextBrush");
        themeSection.Children.Add(themeHint); appearance.Children.Add(themeSection);
        theme.SelectionChanged += (_, _) => themeHint.Visibility = theme.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        themeHint.Visibility = theme.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        appearance.Children.Add(SettingsSection("语言", SettingsReadOnlyChoices(new[] { "中文", "English" }, 0, "当前使用中文；语言切换尚未迁移。")));
        appearance.Children.Add(SettingsSection("动画 · 尚未迁移", SettingsReadOnlyChoices(new[] { "无动画", "适中", "丰富" }, -1, "动画级别设置尚未迁移。")));
        var editorTheme = Column(8);
        editorTheme.Children.Add(SettingsReadOnlyChoices(new[] { "跟随系统", "浅色", "深色" }, -1, "README 编辑器及主题设置尚未迁移。"));
        var presets = SettingsReadOnlyChoices(new[] { "清除", "GitHub 风格", "学术风格" }, -1, "README 编辑器样式预设尚未迁移。");
        presets.HorizontalAlignment = HorizontalAlignment.Left;
        foreach (var button in presets.Children.OfType<Button>()) { button.FontSize = 11; button.MinHeight = 26; button.Padding = new Thickness(8, 4, 8, 4); }
        editorTheme.Children.Add(presets);
        var cssUnavailable = EditorText("▸ 自定义 CSS（覆盖编辑器样式）", 12, "MutedTextBrush");
        cssUnavailable.Margin = new Thickness(0, 8, 0, 0); ToolTipService.SetToolTip(cssUnavailable, "自定义 CSS 尚未迁移。");
        editorTheme.Children.Add(cssUnavailable);
        appearance.Children.Add(SettingsSection("编辑器主题（README）· 尚未迁移", editorTheme));
        var tutorial = SettingsReadOnlyChoices(new[] { "显示教程" }, -1, "新手教程尚未迁移。");
        appearance.Children.Add(tutorial);
        var taskFields = SettingsSection("任务字段", SettingsReadOnlyChoices(new[] { "完整模式", "简洁模式" }, 0, "当前使用完整模式；简洁模式尚未迁移。"));
        taskFields.Children.Add(EditorText("当前仅支持完整模式；简洁模式和教程尚未迁移。", 11, "MutedTextBrush"));
        appearance.Children.Add(taskFields);

        var colors = Column(20);
        var palette = new Grid { ColumnSpacing = 6 };
        var paletteKeys = new[] { "AccentBrush", "JadeBrush", "WarningBrush", "DangerBrush", "BlueGrayBrush" };
        for (var i = 0; i < paletteKeys.Length; i++)
        {
            palette.ColumnDefinitions.Add(new ColumnDefinition());
            var swatch = new Border { Height = 32, Background = ThemeBrush(paletteKeys[i]), CornerRadius = new CornerRadius(2) };
            Grid.SetColumn(swatch, i); palette.Children.Add(swatch);
        }
        colors.Children.Add(SettingsSection("当前颜色方案", palette));
        colors.Children.Add(EditorText("使用简项默认配色，随浅色和深色主题切换。", 13, "SecondaryTextBrush"));
        colors.Children.Add(EditorText("自定义颜色方案尚未开放。任务颜色可以在任务详情中单独设置。", 11, "MutedTextBrush"));

        var data = Column(20);
        var server = Column(8);
        var backendLabel = EditorText("自建服务器", 12); backendLabel.Foreground = ThemeBrush("AccentContrastBrush"); backendLabel.TextAlignment = TextAlignment.Center;
        server.Children.Add(new Border { Child = backendLabel, Padding = new Thickness(7), CornerRadius = new CornerRadius(6), Background = ThemeBrush("AccentBrush") });
        server.Children.Add(url); server.Children.Add(EditorText("连接简项同步服务器，在多台设备间同步项目。", 11, "MutedTextBrush")); server.Children.Add(token);
        var syncMode = EditorText("手动同步", 12, "AccentTextBrush");
        server.Children.Add(SettingsSection("同步模式", syncMode));
        server.Children.Add(EditorText("保存并同步后会核对本地与服务器版本。发生冲突时，由你选择保留的版本。", 11, "MutedTextBrush"));
        data.Children.Add(SettingsSection("云同步", server));
        var storagePath = EditorText(_repository?.DataDirectory ?? "", 12, "SecondaryTextBrush"); storagePath.IsTextSelectionEnabled = true;
        var storage = Column(8); storage.Children.Add(storagePath); storage.Children.Add(EditorText("项目修改自动保存；上一次有效数据会保留为备份。", 11, "MutedTextBrush"));
        data.Children.Add(SettingsSection("本地数据", storage));
        var transfer = new Grid { ColumnSpacing = 6 }; transfer.ColumnDefinitions.Add(new ColumnDefinition()); transfer.ColumnDefinitions.Add(new ColumnDefinition());
        Func<Task>? afterSettings = null;
        var returnAfterTransfer = false;
        Task QueueTransferAsync(Func<Task> action)
        {
            afterSettings = action;
            returnAfterTransfer = true;
            _currentActiveDialog?.Hide();
            return Task.CompletedTask;
        }
        var export = EditorButton("备份与导出", () => QueueTransferAsync(ShowExportAsync));
        var import = EditorButton("导入与还原", () => QueueTransferAsync(ImportAsync));
        AutomationProperties.SetAutomationId(export, "SettingsExport");
        AutomationProperties.SetAutomationId(import, "SettingsImport");
        export.HorizontalAlignment = HorizontalAlignment.Stretch; import.HorizontalAlignment = HorizontalAlignment.Stretch; transfer.Children.Add(export); Grid.SetColumn(import, 1); transfer.Children.Add(import);
        data.Children.Add(SettingsSection("导出 / 导入", transfer));

        var notifications = Column(20);
        notifications.Children.Add(SettingsSection("任务提醒", EditorText("在任务详情中设置提醒时间，到时会在应用内显示提醒。", 13, "SecondaryTextBrush")));
        notifications.Children.Add(EditorText("请保持简项运行，以便按时收到提醒。", 11, "MutedTextBrush"));

        var ai = Column(20);
        ai.Children.Add(SettingsSection("AI", EditorText("尚未接入", 13, "SecondaryTextBrush")));
        ai.Children.Add(EditorText("当前版本尚不支持 AI 服务配置、任务生成或文字润色。", 12, "MutedTextBrush"));

        var plugins = Column(20);
        plugins.Children.Add(SettingsSection("插件", EditorText("尚未开放", 13, "SecondaryTextBrush")));
        plugins.Children.Add(EditorText("当前版本尚不支持安装和运行插件。", 12, "MutedTextBrush"));

        var about = Column(20);
        var applicationVersion = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "未知版本";
        var version = EditorText($"v{applicationVersion}", 24); version.FontFamily = SerifFont;
        about.Children.Add(SettingsSection("版本", version));
        about.Children.Add(EditorText("简项 PureProject", 15));
        about.Children.Add(EditorText("本应用使用 HarmonyOS Sans 字体。字体版权归 Huawei Device Co., Ltd. 等原权利人所有，按 HarmonyOS Sans Fonts License Agreement 随软件提供；许可正文见程序目录 Assets/Fonts/LICENSE.txt。", 12, "MutedTextBrush"));
        var shortcuts = Column(8);
        foreach (var (keys, description) in new[] { ("Ctrl + N", "新建任务"), ("Ctrl + F", "搜索任务"), ("Ctrl + Z", "撤销"), ("Ctrl + Y", "重做"), ("Esc", "关闭面板") })
        {
            var key = new Border { Child = EditorText(keys, 11, "SecondaryTextBrush"), Padding = new Thickness(6, 3, 6, 3), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), BorderBrush = ThemeBrush("BorderBrush"), Background = ThemeBrush("SubtleBackgroundBrush") };
            shortcuts.Children.Add(EditorAddRow(EditorText(description, 12, "SecondaryTextBrush"), key));
        }
        about.Children.Add(SettingsSection("快捷键", shortcuts));

        var pages = new[] { appearance, colors, data, notifications, ai, plugins, about };
        var navigation = Column(2); navigation.Padding = new Thickness(8, 12, 8, 12);
        var contentHost = new Grid { Padding = new Thickness(20, 16, 20, 20) };
        var navButtons = new List<Button>();
        void SelectPage(int selected)
        {
            contentHost.Children.Clear(); contentHost.Children.Add(pages[selected]);
            for (var i = 0; i < navButtons.Count; i++)
            {
                navButtons[i].Foreground = ThemeBrush(i == selected ? "AccentTextBrush" : "SecondaryTextBrush");
                navButtons[i].Background = ThemeBrush(i == selected ? "AccentLightBrush" : "AppBackgroundBrush");
                if (i == selected) PreserveButtonPalette(navButtons[i]); else RestoreButtonPalette(navButtons[i]);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(navButtons[i], i == selected ? "当前设置页" : "打开设置页");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(navButtons[i], i == selected ? "已选中" : "未选中");
            }
        }
        var tabNames = new[] { "外观", "颜色方案", "数据管理", "通知", "AI", "插件", "关于" };
        var tabIds = new[] { "appearance", "colors", "data", "notifications", "ai", "plugins", "about" };
        for (var i = 0; i < tabNames.Length; i++)
        {
            var index = i;
            var button = EditorButton(tabNames[index], () => { SelectPage(index); return Task.CompletedTask; });
            AutomationProperties.SetAutomationId(button, "SettingsPage_" + tabIds[index]);
            button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Left;
            button.Padding = new Thickness(12, 8, 12, 8); button.CornerRadius = new CornerRadius(6); button.BorderThickness = new Thickness(0); button.MinHeight = 32;
            navButtons.Add(button); navigation.Children.Add(button);
        }
        SelectPage(0);
        var panel = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch, Background = ThemeBrush("AppBackgroundBrush") };
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(140) }); panel.ColumnDefinitions.Add(new ColumnDefinition());
        panel.Children.Add(new Border { Child = navigation, BorderThickness = new Thickness(0, 0, 1, 0), BorderBrush = ThemeBrush("BorderBrush") });
        var contentScroll = Scroll(contentHost); contentScroll.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        Grid.SetColumn(contentScroll, 1); panel.Children.Add(contentScroll);
        var container = new Grid { Width = 558, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
        container.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); container.RowDefinitions.Add(new RowDefinition());
        container.Children.Add(error); Grid.SetRow(panel, 1); container.Children.Add(panel);
        var dialog = new ContentDialog { Tag = "Settings", Title = "设置", Content = container, CloseButtonText = "取消" };
        var saving = false;
        var restoringTheme = false;
        var closeAfterSave = false;
        void EnsureSettingsCanSave()
        {
            if (_settingsRepository is null || !_settingsLoaded) throw new InvalidOperationException("原设置文件无法读取，已阻止覆盖。请先保留并检查数据目录中的 settings.json，再重启应用。");
        }
        var syncActions = new Grid { ColumnSpacing = 6 };
        syncActions.ColumnDefinitions.Add(new ColumnDefinition()); syncActions.ColumnDefinitions.Add(new ColumnDefinition());
        void SetSaving(bool value)
        {
            saving = _busy = value;
            url.IsEnabled = token.IsEnabled = export.IsEnabled = import.IsEnabled = !value;
            foreach (var button in themeButtons.Children.OfType<Button>().Concat(syncActions.Children.OfType<Button>())) button.IsEnabled = !value;
        }
        async Task SaveConnectionAsync(bool sync)
        {
            if (saving) return;
            Control? invalidField = null;
            validation.Clear();
            SetSaving(true);
            try
            {
                EnsureSettingsCanSave();
                var address = url.Text.Trim();
                if (address.Length > 0 || sync) { invalidField = url; RestSyncClient.ValidateServerUri(address); invalidField = null; }
                var updated = _settings with { SyncServerUrl = address, SyncToken = token.Password };
                await _settingsRepository!.SaveAsync(updated); _settings = updated;
                error.Severity = InfoBarSeverity.Success; error.Message = "同步设置已保存。"; error.Visibility = Visibility.Visible; error.IsOpen = true;
                if (sync) { afterSettings = SyncAsync; closeAfterSave = true; SetSaving(false); dialog.Hide(); }
            }
            catch (Exception ex) { validation.Show(ex.Message, invalidField); }
            finally { if (saving) SetSaving(false); }
        }
        var saveConnection = EditorButton("保存设置", () => SaveConnectionAsync(false));
        var syncConnection = EditorButton("保存并同步", () => SaveConnectionAsync(true), true);
        saveConnection.HorizontalAlignment = syncConnection.HorizontalAlignment = HorizontalAlignment.Stretch;
        syncActions.Children.Add(saveConnection); Grid.SetColumn(syncConnection, 1); syncActions.Children.Add(syncConnection);
        server.Children.Add(syncActions);
        theme.SelectionChanged += async (_, _) =>
        {
            if (restoringTheme) return;
            var previousIndex = _settings.Theme switch { "Light" => 1, "Dark" => 2, _ => 0 };
            if (saving) { restoringTheme = true; theme.SelectedIndex = previousIndex; restoringTheme = false; return; }
            SetSaving(true);
            try
            {
                EnsureSettingsCanSave();
                var updated = _settings with { Theme = new[] { "System", "Light", "Dark" }[theme.SelectedIndex] };
                await _settingsRepository!.SaveAsync(updated); _settings = updated;
                ApplyTheme(); Render();
                // Rebuild the open settings surface with the newly applied palette.
                // Data-page drafts remain intact when changing the appearance tab.
                afterSettings = () => ShowSettingsWithDraftAsync(url.Text, token.Password);
                closeAfterSave = true; SetSaving(false); dialog.Hide();
            }
            catch (Exception ex)
            {
                restoringTheme = true; theme.SelectedIndex = previousIndex; restoringTheme = false;
                validation.Show(ex.Message);
            }
            finally { if (saving) SetSaving(false); }
        };
        dialog.Closing += (_, args) => { if (saving && !closeAfterSave) args.Cancel = true; };
        while (true)
        {
            await DialogAsync(dialog);
            if (afterSettings is not { } action) break;
            afterSettings = null;
            if (!returnAfterTransfer)
            {
                await action();
                break;
            }
            returnAfterTransfer = false;
            try { await action(); }
            catch (Exception ex) { validation.Show(ex.Message); }
            // Preserve the same URL/token controls and return to the originating data page,
            // including when a picker or the export dialog is canceled.
            SelectPage(2);
        }
    }

    private static StackPanel SettingsSection(string title, UIElement content)
    {
        var section = Column(8); section.Children.Add(EditorText(title, 11, "MutedTextBrush")); section.Children.Add(content); return section;
    }

    private static Grid SettingsReadOnlyChoices(string[] labels, int selected, string reason)
    {
        var row = new Grid { ColumnSpacing = 6 };
        ToolTipService.SetToolTip(row, reason);
        for (var index = 0; index < labels.Length; index++)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var active = index == selected;
            var background = ThemeBrush(active ? "AccentBrush" : "SubtleBackgroundBrush");
            var foreground = ThemeBrush(active ? "AccentContrastBrush" : "SecondaryTextBrush");
            var button = new Button
            {
                Content = labels[index], IsEnabled = false, FontFamily = UiFont, FontSize = 12,
                MinHeight = 32, MinWidth = 0, Padding = new Thickness(7), CornerRadius = new CornerRadius(6),
                HorizontalAlignment = HorizontalAlignment.Stretch, Background = background, Foreground = foreground,
                BorderBrush = ThemeBrush(active ? "AccentBrush" : "BorderBrush"), BorderThickness = new Thickness(1), Opacity = .72
            };
            button.Resources["ButtonBackgroundDisabled"] = background;
            button.Resources["ButtonForegroundDisabled"] = foreground;
            button.Resources["ButtonBorderBrushDisabled"] = button.BorderBrush;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(button, reason);
            ToolTipService.SetToolTip(button, reason);
            Grid.SetColumn(button, index); row.Children.Add(button);
        }
        return row;
    }

    private async Task SyncAsync()
    {
        if (_busy || _repository is null || _settingsRepository is null) return;
        if (string.IsNullOrWhiteSpace(_settings.SyncServerUrl)) throw new InvalidOperationException("请先填写同步服务器地址。");
        _busy = true;
        SetSaveStatus("正在同步…");
        try
        {
            using var client = new RestSyncClient();
            var result = await Task.Run(() => new ManualSyncService(client).SyncAsync(_projects, _settings));
            var before = _projects;
            await Task.Run(() => _repository.SaveAsync(result.Projects));
            _undo.Push(before); _redo.Clear(); _projects = result.Projects; TrimHistory();
            _syncConflicts = result.Conflicts;
            Render();
            try { await _settingsRepository.SaveAsync(result.Settings); _settings = result.Settings; }
            catch (Exception ex) { throw new IOException("项目数据已保存，但同步版本记录未能保存。下次同步将重新核对，请勿重复覆盖：" + ex.Message, ex); }
            ShowMessage($"同步完成：上传 {result.Uploaded} 个、下载 {result.Downloaded} 个" + (_syncConflicts.Count > 0 ? $"，有 {_syncConflicts.Count} 个冲突待处理。" : "。"), _syncConflicts.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
            SetSaveStatus("同步完成 · " + DateTime.Now.ToString("HH:mm:ss"));
        }
        catch { SetSaveStatus("同步未完成 · 本地数据已保留"); throw; }
        finally { _busy = false; }
        if (_syncConflicts.Count > 0) await ResolveConflictsAsync();
    }

    private async Task ResolveConflictsAsync()
    {
        foreach (var conflict in _syncConflicts.ToList())
        {
            var local = _projects.FirstOrDefault(p => p.Id == conflict.Id);
            if (local is null || conflict.Deleted || conflict.ServerData is null)
            {
                ShowMessage("存在删除或缺失项目的同步冲突。请先导出备份，并核对服务器与本地项目：" + (local?.Name ?? conflict.Id), InfoBarSeverity.Warning);
                continue;
            }
            var remote = PmSerializer.ParsePm(conflict.ServerData);
            if (remote.Id != conflict.Id) throw new InvalidDataException("服务器冲突数据的项目 ID 不一致，已拒绝覆盖。");
            var panel = Column(); panel.Children.Add(Label($"项目：{local.Name}\n本地：{local.Tasks.Count} 项任务，更新于 {local.UpdatedAt}\n服务器：{remote.Tasks.Count} 项任务，更新于 {remote.UpdatedAt}\n\n选择要保留的版本。覆盖前会保存本地历史。", true));
            var result = await DialogAsync(new ContentDialog { Title = "处理同步冲突", Content = panel, PrimaryButtonText = "保留本地并上传", SecondaryButtonText = "采用服务器版本", CloseButtonText = "稍后处理", DefaultButton = ContentDialogButton.Close });
            if (result == ContentDialogResult.None) continue;
            if (result == ContentDialogResult.Primary)
            {
                _busy = true;
                try
                {
                    using var client = new RestSyncClient();
                    var push = await client.PushAsync(_settings, local, conflict.ServerRev);
                    if (push.Conflict is not null) { ShowMessage("服务器版本再次变化，请重新同步后处理。", InfoBarSeverity.Warning); continue; }
                    await RecordSyncBaselineAsync(local, push.Rev!.Value);
                }
                finally { _busy = false; }
            }
            else
            {
                await CommitAsync(projects => projects[projects.FindIndex(p => p.Id == local.Id)] = remote, "已应用服务器版本", collectionOnly: true);
                await RecordSyncBaselineAsync(remote, conflict.ServerRev);
            }
            _syncConflicts.Remove(conflict);
        }
        // Establish revisions and hashes from the server after the explicit resolution.
        if (_syncConflicts.Count == 0) ShowMessage("冲突已处理；下次同步将重新核对版本。", InfoBarSeverity.Success);
    }

    private async Task RecordSyncBaselineAsync(Project project, long revision)
    {
        var settings = _settings with
        {
            SyncStateServerUrl = RestSyncClient.ValidateServerUri(_settings.SyncServerUrl).AbsoluteUri,
            SyncRevisions = new Dictionary<string, long>(_settings.SyncRevisions) { [project.Id] = revision },
            SyncedProjectHashes = new Dictionary<string, string>(_settings.SyncedProjectHashes) { [project.Id] = ManualSyncService.Hash(project) }
        };
        if (_settingsRepository is null) throw new InvalidOperationException("设置存储未就绪。");
        await _settingsRepository.SaveAsync(settings);
        _settings = settings;
    }
}
