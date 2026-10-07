using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using PureProject.Core;

namespace PureProject.WinUI;

public sealed partial class MainWindow
{
    private async Task EditProjectAsync(string? id = null)
    {
        if (!_ready) return;
        var existing = _projects.FirstOrDefault(p => p.Id == id);
        var name = new TextBox { Header = "项目名称", PlaceholderText = "项目名称", MinHeight = 34,
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 96, Text = existing?.Name ?? "" };
        var nameValue = PreserveInitialText(name, existing?.Name);
        var descriptionEditor = new LongTextEditor(this, existing is null ? "描述（可选）" : "项目描述", existing?.Description ?? "", existing is null ? 60 : 94);
        var description = descriptionEditor.Input;
        var selectedColor = existing?.Color ?? "#a33b32";
        var start = new CalendarDatePicker { Header = "开始日期", Date = ParseDate(existing?.StartDate), PlaceholderText = "年/月/日", MinWidth = 0, FontSize = 12, Padding = new Thickness(8, 4, 8, 4) };
        var end = new CalendarDatePicker { Header = "结束日期", Date = ParseDate(existing?.EndDate), PlaceholderText = "年/月/日", MinWidth = 0, FontSize = 12, Padding = new Thickness(8, 4, 8, 4) };
        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsOpen = false, Visibility = Visibility.Collapsed };
        error.Closed += (_, _) => error.Visibility = Visibility.Collapsed;
        var form = Column(12); form.MaxWidth = 378; form.HorizontalAlignment = HorizontalAlignment.Stretch;
        form.Children.Add(error); form.Children.Add(name); form.Children.Add(descriptionEditor.Panel);
        var nameHint = EditorText("", 11, "MutedTextBrush"); form.Children.Insert(2, nameHint);
        void UpdateNameHint()
        {
            var unchanged = string.Equals(nameValue(), existing?.Name, StringComparison.Ordinal);
            var count = TextRules.CountGraphemes(unchanged ? name.Text : name.Text.Trim());
            nameHint.Text = unchanged && count > TextRules.ProjectNameGraphemeLimit
                ? $"当前 {count} 字 · 原长名称可保留；修改后最多 {TextRules.ProjectNameGraphemeLimit} 个可见字符。"
                : $"{count} / {TextRules.ProjectNameGraphemeLimit} 个可见字符";
            nameHint.Foreground = ThemeBrush(!unchanged && count > TextRules.ProjectNameGraphemeLimit ? "DangerTextBrush" : "MutedTextBrush");
        }
        name.TextChanged += (_, _) => UpdateNameHint(); UpdateNameHint();
        var colors = new[] { ("朱砂", "#a33b32"), ("松绿", "#3e7562"), ("藤黄", "#ad7622"), ("绛红", "#b94a43"), ("烟紫", "#73576f"),
            ("黛青", "#477477"), ("胭脂", "#a85769"), ("竹青", "#6e7f48"), ("赭石", "#a85e37"), ("孔雀青", "#2e6f68") };
        var colorSection = Column(6); colorSection.Children.Add(EditorText(existing is null ? "颜色" : "项目颜色", 12, "SecondaryTextBrush"));
        var swatches = Column(6); var preview = new Grid { ColumnSpacing = 8 };
        preview.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); preview.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); preview.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var previewDot = new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(7), VerticalAlignment = VerticalAlignment.Center };
        var previewName = EditorText("", 12); var previewValue = EditorText("", 11, "MutedTextBrush"); previewValue.FontFamily = UiFont;
        Grid.SetColumn(previewName, 1); Grid.SetColumn(previewValue, 2); preview.Children.Add(previewDot); preview.Children.Add(previewName); preview.Children.Add(previewValue);
        void RenderProjectColors(string? focusColor = null, FocusState focusState = FocusState.Programmatic)
        {
            swatches.Children.Clear(); StackPanel? colorRow = null;
            for (var i = 0; i < colors.Length; i++)
            {
                if (i % 5 == 0) { colorRow = Row(6); swatches.Children.Add(colorRow); }
                var (colorName, colorValue) = colors[i]; var selected = string.Equals(selectedColor, colorValue, StringComparison.OrdinalIgnoreCase);
                var content = Row(6); content.Children.Add(new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(7), Background = ColorBrush(colorValue), VerticalAlignment = VerticalAlignment.Center });
                content.Children.Add(EditorText(colorName, 11, selected ? "AccentTextBrush" : "SecondaryTextBrush"));
                if (selected) content.Children.Add(new Border { Width = 14, Height = 14, CornerRadius = new CornerRadius(7), Background = ThemeBrush("AccentBrush"), Child = new FontIcon { Glyph = "\uE73E", FontSize = 9, Foreground = ThemeBrush("AccentContrastBrush") } });
                var swatch = new Button { Content = content, Padding = new Thickness(8, 4, 8, 4), MinHeight = 28, MinWidth = 0, CornerRadius = new CornerRadius(6), Background = ThemeBrush("SubtleBackgroundBrush"), BorderBrush = ThemeBrush(selected ? "AccentBrush" : "BorderBrush"), BorderThickness = new Thickness(1) };
                ToolTipService.SetToolTip(swatch, $"{colorName} ({colorValue})"); Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(swatch, "选择颜色 " + colorName);
                AutomationProperties.SetItemStatus(swatch, selected ? "已选中" : "未选中");
                swatch.Click += (_, _) => { var state = swatch.FocusState; selectedColor = colorValue; RenderProjectColors(colorValue, state); }; colorRow!.Children.Add(swatch);
                if (focusColor == colorValue) FocusEditorField(swatch, focusState);
            }
            previewDot.Background = ColorBrush(selectedColor); previewName.Text = colors.FirstOrDefault(c => string.Equals(c.Item2, selectedColor, StringComparison.OrdinalIgnoreCase)).Item1 ?? "自定义颜色"; previewValue.Text = selectedColor;
        }
        RenderProjectColors(); colorSection.Children.Add(swatches);
        colorSection.Children.Add(new Border { Child = preview, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, Background = ThemeBrush("SubtleBackgroundBrush"), BorderBrush = ThemeBrush("BorderBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6) }); form.Children.Add(colorSection);
        var dates = new Grid { ColumnSpacing = 8 }; dates.ColumnDefinitions.Add(new ColumnDefinition()); dates.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); dates.ColumnDefinitions.Add(new ColumnDefinition());
        dates.Children.Add(start); var arrow = EditorText("→", 12, "MutedTextBrush"); arrow.Margin = new Thickness(0, 18, 0, 0); Grid.SetColumn(arrow, 1); dates.Children.Add(arrow); Grid.SetColumn(end, 2); dates.Children.Add(end);
        var dateSection = Column(4); dateSection.Children.Add(EditorText("项目时间范围", 12, "SecondaryTextBrush")); dateSection.Children.Add(dates); form.Children.Add(dateSection);
        if (existing is null)
        {
            var storage = Column(6);
            var fact = EditorText("✓  应用内部存储始终开启", 12, "JadeBrush"); fact.Margin = new Thickness(6, 4, 6, 4);
            ToolTipService.SetToolTip(fact, _repository?.DataDirectory ?? "项目会自动保存到应用的数据目录。");
            storage.Children.Add(fact);
            var localCopy = new CheckBox { Content = "同时保存本地文件夹副本（尚未迁移）", IsChecked = false, IsEnabled = false, MinHeight = 28, FontSize = 12 };
            NativeControlPalette.SetIsEnabled(localCopy, true);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(localCopy, "当前会保存应用内部数据；额外文件夹副本功能尚未迁移。");
            storage.Children.Add(new Border { Child = localCopy, Background = ThemeBrush("SubtleBackgroundBrush"), Padding = new Thickness(6, 0, 6, 0), CornerRadius = new CornerRadius(4) });
            storage.Children.Add(EditorText("云同步地址和凭据在设置中管理。", 10, "MutedTextBrush"));
            var storageSection = Column(6); storageSection.Children.Add(EditorText("存储位置", 12, "SecondaryTextBrush"));
            storageSection.Children.Add(new Border { Child = storage, Padding = new Thickness(8), BorderThickness = new Thickness(1), BorderBrush = ThemeBrush("BorderBrush"), Background = ThemeBrush("CardBackgroundBrush"), CornerRadius = new CornerRadius(4) });
            form.Children.Add(storageSection);
        }
        var projectHeader = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><TextBlock Text="{Binding}" FontFamily="{ThemeResource AppFontFamily}" FontSize="12" Foreground="{ThemeResource SecondaryTextBrush}" Margin="0,0,0,4" /></DataTemplate>
            """);
        name.HeaderTemplate = projectHeader; description.HeaderTemplate = projectHeader;
        name.Resources["TextBoxTopHeaderMargin"] = description.Resources["TextBoxTopHeaderMargin"] = new Thickness(0, 0, 0, 4);
        var projectScroll = Scroll(form); projectScroll.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        projectScroll.Padding = new Thickness(20);
        var dialog = new ContentDialog { Title = existing is null ? "+ 新建项目" : "项目设置", Tag = "ProjectEditor", Content = projectScroll, PrimaryButtonText = existing is null ? "创建" : "保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
        dialog.Resources["ProjectEditorPreferredHeight"] = existing is null ? 664d : 620d;
        var validation = new EditorValidation(error);
        AutomationProperties.SetIsRequiredForForm(name, true);
        async void SaveProject(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            var deferral = args.GetDeferral();
            Control? invalidField = null;
            validation.Clear();
            try
            {
                invalidField = name;
                var savedName = TextRules.RequireProjectName(nameValue(), existing?.Name);
                invalidField = null;
                if (!System.Text.RegularExpressions.Regex.IsMatch(selectedColor.Trim(), "^#[0-9a-fA-F]{6}$")) throw new InvalidOperationException("主题色格式应为 # 加六位十六进制字符。");
                if (start.Date is not null && end.Date < start.Date) { invalidField = end; throw new InvalidOperationException("截止日期不能早于开始日期。"); }
                string? savedProjectId = null;
                await CommitAsync(projects =>
                {
                    var p = existing is null ? _service.CreateProject(savedName, descriptionEditor.Value, selectedColor.Trim()) : projects.Single(p => p.Id == existing.Id);
                    p.Name = savedName; p.Description = descriptionEditor.Value; p.Color = selectedColor.Trim();
                    p.StartDate = DateText(start.Date); p.EndDate = DateText(end.Date); p.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");
                    if (existing is null) { p.SortOrder = projects.Count * 1024; projects.Add(p); }
                    savedProjectId = p.Id;
                }, changedProjectId: existing?.Id, collectionOnly: existing is null);
                _selectedId = savedProjectId;
                Render();
            }
            catch (Exception ex) { args.Cancel = true; validation.Show(ex.Message, invalidField); }
            finally { deferral.Complete(); }
        }
        dialog.PrimaryButtonClick += SaveProject;
        try { await DialogAsync(dialog); }
        finally { dialog.PrimaryButtonClick -= SaveProject; dialog.Content = null; form.Children.Clear(); }
    }

    private async Task ManageProjectAsync()
    {
        if (Current is not { } p) return;
        var actions = new ComboBox { Header = "操作", SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var text in new[] { "编辑项目信息", "管理任务组", "导出当前项目", p.Archived ? "恢复项目" : "归档项目", "向上移动项目", "向下移动项目", "删除项目" }) actions.Items.Add(text);
        var result = await DialogAsync(new ContentDialog { Title = p.Name, Content = actions, PrimaryButtonText = "继续", CloseButtonText = "取消" });
        if (result != ContentDialogResult.Primary) return;
        switch (actions.SelectedIndex)
        {
            case 0: await EditProjectAsync(p.Id); break;
            case 1: await ManageGroupsAsync(); break;
            case 2: await SaveTextAsync(PmSerializer.ExportPm(p), p.Name, ".pm"); break;
            case 3: await ChangeAsync(p.Id, draft => draft.Archived = !draft.Archived); break;
            case 4: case 5:
                await CommitAsync(projects =>
                {
                    var sorted = projects.OrderBy(x => x.SortOrder).ToList(); var index = sorted.FindIndex(x => x.Id == p.Id);
                    var target = Math.Clamp(index + (actions.SelectedIndex == 4 ? -1 : 1), 0, sorted.Count - 1);
                    var item = sorted[index]; sorted.RemoveAt(index); sorted.Insert(target, item);
                    for (int i = 0; i < sorted.Count; i++) sorted[i].SortOrder = (i + 1) * 1024;
                }); break;
            case 6:
                if (await ConfirmAsync("删除项目", $"删除“{p.Name}”及其全部任务？可以使用撤销恢复，磁盘同时保留上一次有效备份。"))
                    await CommitAsync(projects => projects.RemoveAll(x => x.Id == p.Id), collectionOnly: true);
                break;
        }
    }

    private async Task ManageGroupsAsync()
    {
        if (Current is not { } p) return;
        var groups = new ComboBox { Header = "任务组", ItemsSource = p.TaskGroups.OrderBy(g => g.SortOrder).ToList(), DisplayMemberPath = "Name", HorizontalAlignment = HorizontalAlignment.Stretch };
        groups.SelectedItem = CurrentGroup ?? p.TaskGroups[0];
        var action = new ComboBox { Header = "操作", SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var text in new[] { "新增任务组", "重命名", "设为默认", "归档 / 恢复", "向上移动", "向下移动" }) action.Items.Add(text);
        var form = Column(); form.Children.Add(groups); form.Children.Add(action);
        var result = await DialogAsync(new ContentDialog { Title = "管理任务组", Content = form, PrimaryButtonText = "继续", CloseButtonText = "取消" });
        if (result != ContentDialogResult.Primary || groups.SelectedItem is not TaskGroup group) return;
        if (action.SelectedIndex == 0)
        {
            await CreateTaskGroupAsync(p.Id);
        }
        else if (action.SelectedIndex == 1)
        {
            var name = await PromptAsync("重命名任务组", group.Name);
            if (name is not null) await ChangeAsync(p.Id, draft => draft.TaskGroups.Single(g => g.Id == group.Id).Name = name);
        }
        else if (action.SelectedIndex == 2) await ChangeAsync(p.Id, draft => _service.SetDefaultTaskGroup(draft, group.Id));
        else if (action.SelectedIndex == 3) await ChangeAsync(p.Id, draft => { if (group.Archived) _service.RestoreTaskGroup(draft, group.Id); else _service.ArchiveTaskGroup(draft, group.Id); });
        else await ChangeAsync(p.Id, draft =>
        {
            var items = draft.TaskGroups.OrderBy(g => g.SortOrder).ToList();
            var index = items.FindIndex(g => g.Id == group.Id); var target = Math.Clamp(index + (action.SelectedIndex == 4 ? -1 : 1), 0, items.Count - 1);
            var item = items[index]; items.RemoveAt(index); items.Insert(target, item);
            for (int i = 0; i < items.Count; i++) items[i].SortOrder = (i + 1) * 1024;
        });
    }

    private async Task EditTaskAsync(string? taskId, string? projectId = null, string? initialStatusId = null)
    {
        if (!_ready) return;
        var project = _projects.FirstOrDefault(p => p.Id == (projectId ?? _selectedId));
        if (project is null) return;
        // Project metadata is read-only in this editor. Opening a single task must
        // not deep-copy every other task in a large project.
        var working = project;
        var isNew = taskId is null;
        var originalTask = isNew ? null : project.Tasks.Single(t => t.Id == taskId);
        var draft = isNew ? _service.CreateTask(new Project { Id = project.Id, Color = project.Color, DefaultTaskGroupId = project.DefaultTaskGroupId, TaskGroups = project.TaskGroups },
                "新任务", working.TaskGroups.Any(g => g.Id == _groupId && !g.Archived) ? _groupId : null, initialStatusId)
            : PmSerializer.CloneTask(originalTask!);
        var title = new TextBox { Header = "任务标题", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 72, MaxHeight = 144, Text = isNew ? "" : draft.Title };
        var titleValue = PreserveInitialText(title, originalTask?.Title);
        var descriptionEditor = new LongTextEditor(this, "说明", draft.Description, 120);
        var description = descriptionEditor.Input; description.PlaceholderText = "添加任务描述…";
        var titleHint = EditorText("", 11, "MutedTextBrush");
        void UpdateTitleHint()
        {
            var unchanged = string.Equals(titleValue(), originalTask?.Title, StringComparison.Ordinal);
            var count = TextRules.CountGraphemes(unchanged ? title.Text : title.Text.Trim());
            titleHint.Text = unchanged && count > TextRules.TaskTitleGraphemeLimit
                ? $"当前 {count} 字 · 原长标题可保留；修改后最多 {TextRules.TaskTitleGraphemeLimit} 个可见字符。"
                : $"{count} / {TextRules.TaskTitleGraphemeLimit} 个可见字符";
            titleHint.Foreground = ThemeBrush(!unchanged && count > TextRules.TaskTitleGraphemeLimit ? "DangerTextBrush" : "MutedTextBrush");
        }
        title.TextChanged += (_, _) => UpdateTitleHint(); UpdateTitleHint();
        var group = new ComboBox { Header = "任务组", ItemsSource = working.TaskGroups.Where(g => !g.Archived || g.Id == draft.TaskGroupId).OrderBy(g => g.SortOrder).ToList(), DisplayMemberPath = "Name", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(title, "TaskEditorTitle");
        AutomationProperties.SetAutomationId(description, "TaskEditorDescription");
        AutomationProperties.SetAutomationId(group, "TaskEditorGroup");
        group.SelectedItem = working.TaskGroups.First(g => g.Id == draft.TaskGroupId);
        var status = new ComboBox { Header = "状态", DisplayMemberPath = "Name", Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(status, "TaskEditorStatus");
        var statusByGroup = new Dictionary<string, string>(StringComparer.Ordinal) { [draft.TaskGroupId] = draft.StatusId };
        string? displayedStatusGroupId = null;
        var statusButtons = new Grid { ColumnSpacing = 6, RowSpacing = 6 };
        for (var i = 0; i < 3; i++) statusButtons.ColumnDefinitions.Add(new ColumnDefinition());
        void FillStatusButtons(string? focusId = null, FocusState focusState = FocusState.Programmatic)
        {
            statusButtons.Children.Clear(); statusButtons.RowDefinitions.Clear();
            if (status.ItemsSource is not IEnumerable<TaskStatusDefinition> definitions) return;
            var index = 0;
            foreach (var definition in definitions)
            {
                if (index % 3 == 0) statusButtons.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var active = status.SelectedItem == definition;
                var button = EditorButton(definition.Name, () => { status.SelectedItem = definition; return Task.CompletedTask; });
                button.Background = active ? ColorBrush(definition.Color) : ThemeBrush("CardBackgroundBrush");
                button.Foreground = active ? ContrastForeground(((SolidColorBrush)ColorBrush(definition.Color)).Color) : ThemeBrush("SecondaryTextBrush");
                button.BorderBrush = active ? ColorBrush(definition.Color) : ThemeBrush("BorderBrush");
                if (active) PreserveButtonPalette(button);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(button, active ? "当前状态" : "切换任务状态");
                AutomationProperties.SetItemStatus(button, active ? "已选中" : "未选中");
                AutomationProperties.SetAutomationId(button, "TaskEditorStatus_" + definition.Id);
                button.Tag = definition.Id;
                button.HorizontalAlignment = HorizontalAlignment.Stretch;
                button.Content = new TextBlock { Text = EditorPreview(definition.Name, 64), FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, TextAlignment = TextAlignment.Center };
                Grid.SetRow(button, index / 3); Grid.SetColumn(button, index % 3); statusButtons.Children.Add(button); index++;
                if (focusId == definition.Id) FocusEditorField(button, focusState);
            }
        }
        void SetStatuses()
        {
            if (group.SelectedItem is not TaskGroup selected) return;
            // Replacing ItemsSource raises SelectionChanged; only remember deliberate selections
            // after the new group's status list has been installed.
            displayedStatusGroupId = null;
            status.ItemsSource = selected.Statuses.OrderBy(s => s.SortOrder).ToList();
            var rememberedStatusId = statusByGroup.GetValueOrDefault(selected.Id);
            var selectedStatus = selected.Statuses.FirstOrDefault(s => s.Id == rememberedStatusId) ?? selected.Statuses.First(s => s.Id == selected.InitialStatusId);
            status.SelectedItem = selectedStatus;
            displayedStatusGroupId = selected.Id;
            statusByGroup[selected.Id] = selectedStatus.Id;
        }
        status.SelectionChanged += (_, _) =>
        {
            if (displayedStatusGroupId is not null && status.SelectedItem is TaskStatusDefinition selectedStatus)
                statusByGroup[displayedStatusGroupId] = selectedStatus.Id;
            var focused = statusButtons.Children.OfType<Button>().FirstOrDefault(button => button.FocusState != FocusState.Unfocused);
            FillStatusButtons(focused?.Tag as string, focused?.FocusState ?? FocusState.Programmatic);
        };
        SetStatuses(); group.SelectionChanged += (_, _) => SetStatuses();
        var priority = new ComboBox { Header = "优先级", ItemsSource = new[] { "高", "中", "低" }, SelectedIndex = draft.Priority switch { "high" => 0, "low" => 2, _ => 1 }, Visibility = Visibility.Collapsed };
        var priorityButtons = EditorChoiceButtons(priority, new[] { "高", "中", "低" }, new[] { "DangerBrush", "WarningBrush", "SuccessBrush" });
        var due = new CalendarDatePicker { Header = "截止日期", Date = ParseDate(draft.DueDate), PlaceholderText = "未设置", HorizontalAlignment = HorizontalAlignment.Stretch };
        var dueTime = new TextBox { Header = "截止时间", Text = draft.DueTime ?? "", PlaceholderText = "HH:mm（留空为 23:59）" };
        var offset = new NumberBox { Header = "开始偏移（自项目创建日起，天）", Value = draft.StartOffset ?? double.NaN, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var tags = new TextBox { Header = "标签", Text = string.Join(", ", draft.Tags), Visibility = Visibility.Collapsed };
        var reminder = new TextBox { Header = "提醒时间", Text = DateTimeOffset.TryParse(draft.Reminder, out var reminderDate) ? reminderDate.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : draft.Reminder ?? "", PlaceholderText = "yyyy-MM-dd HH:mm（应用运行时提醒）" };
        var colorChoices = new[] { "#a33b32", "#3e7562", "#ad7622", "#b94a43", "#73576f", "#477477", "#a85769", "#6e7f48", "#a85e37", "#2e6f68" };
        var colorRow = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, MaximumRowsOrColumns = 10, ItemWidth = 25, ItemHeight = 26 };
        var colorNames = new[] { "朱砂", "松绿", "藤黄", "绛红", "烟紫", "黛青", "胭脂", "竹青", "赭石", "孔雀青" };
        void FillColors(string? focusColor = null, FocusState focusState = FocusState.Programmatic)
        {
            colorRow.Children.Clear();
            foreach (var color in colorChoices)
            {
                var selected = string.Equals(draft.Color ?? "#a33b32", color, StringComparison.OrdinalIgnoreCase);
                var swatch = new Button { Width = 24, Height = 24, MinWidth = 0, MinHeight = 0, Padding = new Thickness(2), CornerRadius = new CornerRadius(2), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0), Content = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(2), Background = ColorBrush(color), BorderBrush = ThemeBrush("PrimaryTextBrush"), BorderThickness = new Thickness(selected ? 2 : 0) } };
                var colorName = colorNames[Array.IndexOf(colorChoices, color)];
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(swatch, $"任务颜色 {colorName} {color}");
                AutomationProperties.SetItemStatus(swatch, selected ? "已选中" : "未选中");
                ToolTipService.SetToolTip(swatch, $"{colorName} ({color})");
                swatch.Click += (_, _) => { var state = swatch.FocusState; draft.Color = color; FillColors(color, state); };
                colorRow.Children.Add(swatch);
                if (focusColor == color) FocusEditorField(swatch, focusState);
            }
        }
        FillColors();
        var tagChips = new VariableSizedWrapGrid { Orientation = Orientation.Horizontal, MaximumRowsOrColumns = 12, ItemWidth = 25, ItemHeight = 30 };
        var tagInput = new TextBox { PlaceholderText = "添加标签…", MaxLength = 500 };
        void FillTags()
        {
            tagChips.Children.Clear();
            foreach (var tag in tags.Text.Split([',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct())
            {
                var chip = EditorButton(EditorPreview(tag, 32) + " ×", () => { tags.Text = string.Join(", ", tags.Text.Split([',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(value => value != tag)); FillTags(); tagInput.Focus(FocusState.Programmatic); return Task.CompletedTask; });
                chip.CornerRadius = new CornerRadius(14); chip.Background = ThemeBrush("AccentLightBrush"); chip.Foreground = ThemeBrush("AccentTextBrush"); chip.BorderThickness = new Thickness(0); chip.MinHeight = 24; chip.Padding = new Thickness(8, 3, 8, 3); chip.Margin = new Thickness(0, 0, 6, 4);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(chip, "移除标签 " + tag); ToolTipService.SetToolTip(chip, tag);
                VariableSizedWrapGrid.SetColumnSpan(chip, Math.Clamp((int)Math.Ceiling((tag.Sum(character => character > 127 ? 12 : 6) + 32d) / 25), 2, 12));
                tagChips.Children.Add(chip);
            }
        }
        Task AddTag()
        {
            if (!string.IsNullOrWhiteSpace(tagInput.Text)) { tags.Text = string.Join(", ", tags.Text.Split([',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Append(tagInput.Text.Trim()).Distinct()); tagInput.Text = ""; FillTags(); }
            return Task.CompletedTask;
        }
        tagInput.KeyDown += async (_, args) => { if (args.Key == Windows.System.VirtualKey.Enter) { args.Handled = true; await AddTag(); } };
        FillTags();
        var tagPanel = Column(4); tagPanel.Children.Add(tagChips); tagPanel.Children.Add(EditorAddRow(tagInput, EditorButton("+", AddTag, true))); tagPanel.Children.Add(tags);
        var basic = Column(13);
        basic.Children.Add(title); basic.Children.Add(titleHint); basic.Children.Add(group); basic.Children.Add(status); basic.Children.Add(EditorSection("状态", statusButtons));
        basic.Children.Add(priority); basic.Children.Add(EditorSection("优先级", priorityButtons)); basic.Children.Add(descriptionEditor.Panel);
        var aiPolish = new Button
        {
            Content = "AI 润色 · 尚未接入", IsEnabled = false, HorizontalAlignment = HorizontalAlignment.Right,
            FontFamily = UiFont, FontSize = 12, MinWidth = 0, MinHeight = 28, Padding = new Thickness(12, 5, 12, 5),
            CornerRadius = new CornerRadius(2), Background = ThemeBrush("JadeBrush"), Foreground = ThemeBrush("AccentContrastBrush"),
            BorderBrush = ThemeBrush("JadeBrush"), BorderThickness = new Thickness(1), Opacity = .72
        };
        aiPolish.Resources["ButtonBackgroundDisabled"] = ThemeBrush("JadeBrush");
        aiPolish.Resources["ButtonForegroundDisabled"] = ThemeBrush("AccentContrastBrush");
        aiPolish.Resources["ButtonBorderBrushDisabled"] = ThemeBrush("JadeBrush");
        ToolTipService.SetToolTip(aiPolish, "AI 润色尚未接入，当前不可用。");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(aiPolish, "AI 润色尚未接入，当前不可用。");
        basic.Children.Add(aiPolish);
        basic.Children.Add(EditorSection("任务颜色", colorRow)); basic.Children.Add(due); basic.Children.Add(dueTime);
        var clearDue = EditorButton("清除截止日期与时间", () => { due.Date = null; dueTime.Text = ""; return Task.CompletedTask; }); clearDue.HorizontalAlignment = HorizontalAlignment.Left; clearDue.MinHeight = 24; clearDue.FontSize = 11;
        basic.Children.Add(clearDue); basic.Children.Add(reminder); basic.Children.Add(EditorSection("标签", tagPanel));

        var subtasks = Column(2);
        var subInputEditor = new LongTextEditor(this, "添加子任务", "", 28, compact: true);
        var subInput = subInputEditor.Input; subInput.PlaceholderText = "添加子任务…";
        var subtaskFields = new Dictionary<string, TextBox>();
        var subProgress = new ProgressBar { Height = 4, Minimum = 0, Maximum = 1, Foreground = ThemeBrush("AccentBrush"), Background = ThemeBrush("SubtleBackgroundBrush"), HorizontalAlignment = HorizontalAlignment.Stretch };
        var subProgressText = EditorText("", 11, "MutedTextBrush");
        void UpdateSubProgress() { subProgress.Maximum = Math.Max(1, draft.Subtasks.Count); subProgress.Value = draft.Subtasks.Count(item => item.Done); subProgressText.Text = $"{draft.Subtasks.Count(item => item.Done)}/{draft.Subtasks.Count}"; subProgress.Visibility = subProgressText.Visibility = draft.Subtasks.Count == 0 ? Visibility.Collapsed : Visibility.Visible; }
        void FillSubtasks()
        {
            subtasks.Children.Clear(); subtaskFields.Clear(); UpdateSubProgress();
            foreach (var sub in draft.Subtasks.ToList())
            {
                var row = new Grid { ColumnSpacing = 8 }; row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var done = new CheckBox { IsChecked = sub.Done, Width = 32, MinWidth = 32, MinHeight = 32, VerticalAlignment = VerticalAlignment.Center };
                NativeControlPalette.SetIsEnabled(done, true);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(done, "完成子任务 " + EditorPreview(sub.Title, 128));
                done.Checked += (_, _) => { sub.Done = true; UpdateSubProgress(); }; done.Unchecked += (_, _) => { sub.Done = false; UpdateSubProgress(); };
                FrameworkElement text;
                if (sub.Title.Length > PagedTextBuffer.DefaultPageSize)
                {
                    var host = Column(4);
                    var preview = EditorText(EditorPreview(sub.Title, 240), 12); preview.MaxLines = 3; preview.TextTrimming = TextTrimming.CharacterEllipsis;
                    host.Children.Add(preview);
                    LongTextEditor? editor = null;
                    var edit = EditorButton("展开 / 编辑子任务全文", () =>
                    {
                        if (editor is null)
                        {
                            editor = new LongTextEditor(this, "子任务标题", sub.Title, 56);
                            editor.Changed += () => { sub.Title = editor.Value; preview.Text = EditorPreview(sub.Title, 240); };
                            subtaskFields[sub.Id] = editor.Input; host.Children.Add(editor.Panel);
                        }
                        else editor.Panel.Visibility = editor.Panel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                        preview.Visibility = editor.Panel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                        if (editor.Panel.Visibility == Visibility.Visible) FocusEditorField(editor.Input);
                        return Task.CompletedTask;
                    });
                    host.Children.Add(edit); text = host;
                }
                else
                {
                    var editor = new LongTextEditor(this, "子任务标题", sub.Title, 28, compact: true);
                    var input = editor.Input; input.FontSize = 12; input.Padding = new Thickness(4);
                    editor.Changed += () => sub.Title = editor.Value;
                    AutomationProperties.SetName(input, "子任务标题"); subtaskFields[sub.Id] = input; text = editor.Panel;
                }
                var remove = EditorButton("×", () => { draft.Subtasks.Remove(sub); FillSubtasks(); subInput.Focus(FocusState.Programmatic); return Task.CompletedTask; }); remove.MinWidth = 24; remove.Padding = new Thickness(4); remove.BorderThickness = new Thickness(0);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, "移除子任务 " + EditorPreview(sub.Title, 128));
                row.Children.Add(done); Grid.SetColumn(text, 1); row.Children.Add(text); Grid.SetColumn(remove, 2); row.Children.Add(remove); subtasks.Children.Add(row);
            }
        }
        FillSubtasks();
        var subPanel = Column(6); subPanel.Children.Add(EditorAddRow(subProgress, subProgressText)); subPanel.Children.Add(subtasks);
        Task AddSubtask()
        { if (!string.IsNullOrWhiteSpace(subInputEditor.Value)) { draft.Subtasks.Add(new Subtask { Id = Guid.NewGuid().ToString(), Title = subInputEditor.Value.Trim() }); subInputEditor.Clear(); FillSubtasks(); subInput.Focus(FocusState.Programmatic); } return Task.CompletedTask; }
        subInput.KeyDown += async (_, args) => { if (args.Key == Windows.System.VirtualKey.Enter && !subInput.AcceptsReturn) { args.Handled = true; await AddSubtask(); } };
        subPanel.Children.Add(EditorAddRow(subInputEditor.Panel, EditorButton("+", AddSubtask, true)));

        var dependencies = Column(4);
        var dependencyCandidates = working.Tasks.Where(t => t.Id != draft.Id).ToArray();
        var dependencyOffsets = new Dictionary<string, double>();
        foreach (var dependency in draft.Dependencies) dependencyOffsets.TryAdd(dependency.TaskId, dependency.DayOffset);
        var selectedDependencyIds = dependencyOffsets.Keys.ToHashSet();
        // Candidates are data only. Create an input when a dependency is selected,
        // retaining its draft value if it is removed and then selected again.
        var dependencyInputs = new Dictionary<string, NumberBox>();
        var dependencyPicker = new ComboBox { PlaceholderText = "选择前置任务…", DisplayMemberPath = "Title", HorizontalAlignment = HorizontalAlignment.Stretch };
        var dependencySearch = new TextBox { Header = "搜索前置任务", PlaceholderText = "输入任务标题或任务组关键词…", MaxLength = 200 };
        var dependencyMatches = EditorText("", 11, "MutedTextBrush");
        var dependencyGroupNames = working.TaskGroups.ToDictionary(item => item.Id, item => item.Name);
        var dependencyAdd = EditorButton("+", () =>
        {
            if (dependencyPicker.SelectedItem is ProjectTask selected && selectedDependencyIds.Add(selected.Id))
            {
                FillDependencies();
                (dependencyPicker.IsEnabled ? (Control)dependencyPicker : dependencySearch).Focus(FocusState.Programmatic);
            }
            return Task.CompletedTask;
        }, true);
        dependencyAdd.IsEnabled = false;
        AutomationProperties.SetAutomationId(dependencies, "TaskDependencySelected");
        AutomationProperties.SetAutomationId(dependencySearch, "TaskDependencySearch");
        AutomationProperties.SetName(dependencySearch, "搜索前置任务");
        AutomationProperties.SetHelpText(dependencySearch, "按任务标题或任务组名称筛选；已添加的前置依赖不受搜索影响。");
        AutomationProperties.SetAutomationId(dependencyPicker, "TaskDependencyPicker");
        AutomationProperties.SetName(dependencyPicker, "选择前置任务");
        AutomationProperties.SetAutomationId(dependencyAdd, "TaskDependencyAdd");
        AutomationProperties.SetName(dependencyAdd, "添加选中的前置任务");
        AutomationProperties.SetAutomationId(dependencyMatches, "TaskDependencyStatus");
        AutomationProperties.SetLiveSetting(dependencyMatches, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        void RefreshDependencyCandidates()
        {
            var selectedId = (dependencyPicker.SelectedItem as ProjectTask)?.Id;
            var query = dependencySearch.Text.Trim();
            var available = dependencyCandidates.Where(item => !selectedDependencyIds.Contains(item.Id)).ToList();
            var matches = available.Where(item => query.Length == 0
                || item.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || dependencyGroupNames.GetValueOrDefault(item.TaskGroupId, "").Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
            dependencyPicker.SelectedIndex = -1;
            dependencyPicker.ItemsSource = matches;
            dependencyPicker.SelectedItem = matches.FirstOrDefault(item => item.Id == selectedId);
            dependencyPicker.IsEnabled = matches.Count > 0;
            dependencyMatches.Text = dependencyCandidates.Length == 0 ? "项目内还没有其他可依赖的任务。"
                : available.Count == 0 ? "所有可选任务均已添加为前置依赖。"
                : matches.Count == 0 ? $"匹配 0 / 可选 {available.Count} 个任务，请更换关键词。"
                : query.Length == 0 ? $"可选 {available.Count} 个任务，选择后点击添加。"
                : $"匹配 {matches.Count} / 可选 {available.Count} 个任务。";
            AutomationProperties.SetName(dependencyMatches, dependencyMatches.Text);
        }
        dependencySearch.TextChanged += (_, _) => RefreshDependencyCandidates();
        dependencyPicker.SelectionChanged += (_, _) => dependencyAdd.IsEnabled = dependencyPicker.SelectedItem is ProjectTask;
        void FillDependencies()
        {
            foreach (var input in dependencyInputs) dependencyOffsets[input.Key] = input.Value.Value;
            foreach (var child in dependencies.Children.OfType<Grid>()) child.Children.Clear();
            dependencies.Children.Clear();
            foreach (var id in dependencyInputs.Keys.Where(id => !selectedDependencyIds.Contains(id)).ToArray()) dependencyInputs.Remove(id);
            foreach (var candidate in dependencyCandidates.Where(item => selectedDependencyIds.Contains(item.Id)))
            {
                if (!dependencyInputs.TryGetValue(candidate.Id, out var days))
                {
                    days = new NumberBox { Value = dependencyOffsets.GetValueOrDefault(candidate.Id, 1), Width = 54, MinWidth = 0, FontSize = 12, Padding = new Thickness(4), SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Hidden };
                    AutomationProperties.SetAutomationId(days, "TaskDependencyOffset:" + candidate.Id);
                    AutomationProperties.SetName(days, $"前置任务 {candidate.Title} 的间隔天数");
                    AutomationProperties.SetHelpText(days, "相对前置任务的偏移天数，单位：天。");
                    ToolTipService.SetToolTip(days, "间隔天数");
                    dependencyInputs.Add(candidate.Id, days);
                }
                var name = EditorText(candidate.Title, 12); name.TextTrimming = TextTrimming.CharacterEllipsis; name.TextWrapping = TextWrapping.NoWrap;
                var remove = EditorButton("×", () => { selectedDependencyIds.Remove(candidate.Id); FillDependencies(); (dependencyPicker.IsEnabled ? (Control)dependencyPicker : dependencySearch).Focus(FocusState.Programmatic); return Task.CompletedTask; }); remove.MinWidth = 24; remove.Padding = new Thickness(4);
                AutomationProperties.SetAutomationId(remove, "TaskDependencyRemove:" + candidate.Id);
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, "移除前置依赖 " + candidate.Title);
                var value = new Grid { ColumnSpacing = 4, Background = ThemeBrush("SubtleBackgroundBrush"), Padding = new Thickness(6, 4, 6, 4) };
                value.ColumnDefinitions.Add(new ColumnDefinition()); value.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); value.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                value.Children.Add(name); Grid.SetColumn(days, 1); value.Children.Add(days); Grid.SetColumn(remove, 2); value.Children.Add(remove); dependencies.Children.Add(value);
            }
            RefreshDependencyCandidates();
        }
        FillDependencies();
        var dependencyPanel = Column(8); dependencyPanel.Children.Add(offset); dependencyPanel.Children.Add(EditorSection("前置依赖", dependencies));
        dependencyPanel.Children.Add(dependencySearch);
        dependencyPanel.Children.Add(EditorAddRow(dependencyPicker, dependencyAdd));
        dependencyPanel.Children.Add(dependencyMatches);

        var recurrence = new CheckBox { Content = "完成后创建下一次任务", IsChecked = draft.Recurrence is not null };
        NativeControlPalette.SetIsEnabled(recurrence, true);
        var freqValues = new[] { "daily", "weekly", "monthly", "yearly" };
        var frequency = new ComboBox { Header = "频率", ItemsSource = new[] { "每天", "每周", "每月", "每年" }, SelectedIndex = Math.Max(0, Array.IndexOf(freqValues, draft.Recurrence?.Freq ?? "daily")), HorizontalAlignment = HorizontalAlignment.Stretch };
        var interval = new NumberBox { Header = "间隔", Minimum = 1, Maximum = 366, Value = draft.Recurrence?.Interval ?? 1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
        var ends = new[] { "never", "count", "until" };
        var end = new ComboBox { Header = "结束条件", ItemsSource = new[] { "不设结束", "限定次数", "截止某日" }, SelectedIndex = Math.Max(0, Array.IndexOf(ends, draft.Recurrence?.End ?? "never")), HorizontalAlignment = HorizontalAlignment.Stretch };
        var count = new NumberBox { Header = "后续生成次数", Minimum = 0, Maximum = 100000, Value = draft.Recurrence?.Count ?? 10 };
        var until = new CalendarDatePicker { Header = "循环截止日期", Date = ParseDate(draft.Recurrence?.Until) };
        var weekdays = new Grid { ColumnSpacing = 2 }; var weekdayBoxes = new List<CheckBox>();
        var weekdayNames = new[] { "日", "一", "二", "三", "四", "五", "六" };
        for (int i = 0; i < 7; i++) { weekdays.ColumnDefinitions.Add(new ColumnDefinition()); var box = new CheckBox { Content = weekdayNames[i], FontSize = 11, MinWidth = 0, IsChecked = draft.Recurrence?.ByWeekday?.Contains(i) ?? false }; NativeControlPalette.SetIsEnabled(box, true); weekdayBoxes.Add(box); Grid.SetColumn(box, i); weekdays.Children.Add(box); }
        var recurrenceDetails = Column(8); recurrenceDetails.Children.Add(EditorSection("频率", EditorChoiceButtons(frequency, new[] { "每天", "每周", "每月", "每年" }))); recurrenceDetails.Children.Add(interval);
        frequency.Visibility = Visibility.Collapsed; recurrenceDetails.Children.Add(frequency);
        var weekdaySection = EditorSection("每周执行日", weekdays); recurrenceDetails.Children.Add(weekdaySection);
        recurrenceDetails.Children.Add(EditorSection("结束条件", EditorChoiceButtons(end, new[] { "不设结束", "限定次数", "截止某日" }))); end.Visibility = Visibility.Collapsed; recurrenceDetails.Children.Add(end); recurrenceDetails.Children.Add(count); recurrenceDetails.Children.Add(until);
        void UpdateRecurrenceFields() { recurrenceDetails.Visibility = recurrence.IsChecked == true ? Visibility.Visible : Visibility.Collapsed; weekdaySection.Visibility = frequency.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed; count.Visibility = end.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed; until.Visibility = end.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed; }
        recurrence.Checked += (_, _) => UpdateRecurrenceFields(); recurrence.Unchecked += (_, _) => UpdateRecurrenceFields(); frequency.SelectionChanged += (_, _) => UpdateRecurrenceFields(); end.SelectionChanged += (_, _) => UpdateRecurrenceFields(); UpdateRecurrenceFields();
        var recurrencePanel = Column(8); recurrencePanel.Children.Add(recurrence); recurrencePanel.Children.Add(recurrenceDetails);

        var commentsPanel = Column(8);
        var commentEditor = new LongTextEditor(this, "新增评论", "", 56);
        var commentInput = commentEditor.Input; commentInput.PlaceholderText = "添加评论…";
        AutomationProperties.SetAutomationId(commentInput, "TaskEditorCommentInput");
        var existingComments = Column(8);
        var commentHeading = EditorText("", 11, "SecondaryTextBrush"); commentHeading.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        void FillComments()
        {
            commentHeading.Text = $"评论 ({draft.Comments.Count})";
            existingComments.Children.Clear();
            if (draft.Comments.Count == 0) existingComments.Children.Add(EditorText("暂无评论。", 12, "MutedTextBrush"));
            foreach (var comment in draft.Comments.ToList())
            {
                var item = Column(6);
                var preview = EditorText(EditorPreview(comment.Content), 13); preview.MaxLines = 5; preview.TextTrimming = TextTrimming.CharacterEllipsis;
                LongTextEditor? editor = null;
                item.Children.Add(preview);
                var actions = Row(4);
                var edit = EditorButton("展开 / 编辑全文", () =>
                {
                    if (editor is null)
                    {
                        editor = new LongTextEditor(this, "评论内容", comment.Content, 56);
                        editor.Changed += () => { comment.Content = editor.Value; preview.Text = EditorPreview(comment.Content); };
                        item.Children.Insert(1, editor.Panel);
                        preview.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        var editing = editor.Panel.Visibility != Visibility.Visible;
                        editor.Panel.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
                        preview.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
                    }
                    if (editor.Panel.Visibility == Visibility.Visible) FocusEditorField(editor.Input);
                    return Task.CompletedTask;
                });
                var remove = EditorButton("删除", () => { draft.Comments.Remove(comment); FillComments(); commentInput.Focus(FocusState.Programmatic); return Task.CompletedTask; });
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(remove, $"删除 {comment.CreatedAt} 的评论");
                foreach (var button in new[] { edit, remove }) { button.FontSize = 11; button.MinHeight = 22; button.Padding = new Thickness(4, 1, 4, 1); button.BorderThickness = new Thickness(0); }
                remove.Foreground = ThemeBrush("DangerTextBrush"); actions.Children.Add(edit); actions.Children.Add(remove);
                var date = DateTimeOffset.TryParse(comment.CreatedAt, out var posted) ? posted.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : comment.CreatedAt;
                item.Children.Add(EditorAddRow(EditorText(date, 11, "MutedTextBrush"), actions));
                existingComments.Children.Add(new Border { Child = item, Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), BorderBrush = ThemeBrush("BorderBrush"), Background = ThemeBrush("SubtleBackgroundBrush") });
            }
        }
        FillComments();
        commentsPanel.Children.Add(existingComments);
        commentsPanel.Children.Add(commentEditor.Panel);
        var sendComment = EditorButton("发送", () => { if (!string.IsNullOrWhiteSpace(commentEditor.Value)) { draft.Comments.Add(new TaskComment { Id = Guid.NewGuid().ToString(), Content = commentEditor.Value.Trim(), CreatedAt = DateTimeOffset.UtcNow.ToString("O") }); commentEditor.Clear(); FillComments(); } return Task.CompletedTask; }, true);
        AutomationProperties.SetAutomationId(sendComment, "TaskEditorSendComment");
        sendComment.HorizontalAlignment = HorizontalAlignment.Right; sendComment.IsEnabled = false; commentEditor.Changed += () => sendComment.IsEnabled = !string.IsNullOrWhiteSpace(commentEditor.Value); commentsPanel.Children.Add(sendComment);
        var timerPanel = Column(6); var timerInfo = EditorText(TrackingText(draft), 12, "SecondaryTextBrush"); timerPanel.Children.Add(timerInfo);
        Button startTimer = null!;
        startTimer = EditorButton(draft.CompletedAt is not null ? "计时已结束" : draft.TrackedStart is not null ? "计时中" : "▷  开始计时", () =>
        {
            if (draft.TrackedStart is not null || draft.CompletedAt is not null) return Task.CompletedTask;
            draft.TrackedStart = DateTimeOffset.UtcNow.ToString("O");
            timerInfo.Text = TrackingText(draft) + "\n待保存：保存任务后计时生效，取消则不保留。";
            startTimer.Content = "已开始 · 待保存";
            startTimer.IsEnabled = false;
            AutomationProperties.SetItemStatus(startTimer, "待保存");
            return Task.CompletedTask;
        }, true);
        AutomationProperties.SetAutomationId(startTimer, "TaskEditorStartTimer");
        AutomationProperties.SetAutomationId(timerInfo, "TaskEditorTimerInfo");
        AutomationProperties.SetHelpText(startTimer, "开始计时后需保存任务，取消编辑则不保留本次计时。");
        startTimer.IsEnabled = draft.TrackedStart is null && draft.CompletedAt is null; timerPanel.Children.Add(startTimer);

        var content = Column(13); content.Width = 298;
        content.Children.Add(basic);
        content.Children.Add(EditorSection("子任务", subPanel));
        content.Children.Add(EditorSection("工时追踪", new Border { Child = timerPanel, Background = ThemeBrush("SubtleBackgroundBrush"), Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(8) }));
        content.Children.Add(EditorSection("循环规则", recurrencePanel)); content.Children.Add(dependencyPanel);
        var commentSection = Column(5); commentSection.Children.Add(commentHeading); commentSection.Children.Add(commentsPanel); content.Children.Add(commentSection);
        var metadata = Column(4);
        metadata.Children.Add(EditorText("创建于 " + EditorDate(draft.CreatedAt), 11, "MutedTextBrush")); metadata.Children.Add(EditorText("更新于 " + EditorDate(draft.UpdatedAt), 11, "MutedTextBrush"));
        content.Children.Add(new Border { Child = metadata, BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = ThemeBrush("BorderBrush"), Padding = new Thickness(0, 8, 0, 0) });
        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsOpen = false, Visibility = Visibility.Collapsed }; content.Children.Insert(0, error);
        error.Closed += (_, _) => error.Visibility = Visibility.Collapsed;
        title.HeaderTemplate = EditorHeaderTemplateFor("标题");
        description.HeaderTemplate = EditorHeaderTemplateFor("描述");
        var taskScroll = Scroll(content); taskScroll.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        taskScroll.Padding = new Thickness(16, 17, 24, 20);
        var dialog = new ContentDialog { Tag = "TaskDetail", Title = isNew ? "新建任务" : "任务详情", Content = taskScroll, PrimaryButtonText = "保存", SecondaryButtonText = isNew ? "" : "删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
        var validation = new EditorValidation(error);
        AutomationProperties.SetIsRequiredForForm(title, true);
        async void SaveTask(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            var deferral = args.GetDeferral();
            Control? invalidField = null;
            validation.Clear();
            try
            {
                invalidField = title;
                var savedTitle = TextRules.RequireTaskTitle(titleValue(), originalTask?.Title);
                invalidField = null;
                if (dueTime.Text.Length > 0 && !TimeOnly.TryParseExact(dueTime.Text.Trim(), "HH:mm", out _)) { invalidField = dueTime; throw new InvalidOperationException("截止时间格式应为 HH:mm。"); }
                if (reminder.Text.Length > 0 && !DateTimeOffset.TryParse(reminder.Text, out _)) { invalidField = reminder; throw new InvalidOperationException("提醒时间格式应为 yyyy-MM-dd HH:mm。"); }
                if (group.SelectedItem is not TaskGroup selectedGroup || status.SelectedItem is not TaskStatusDefinition selectedStatus) { invalidField = group; throw new InvalidOperationException("请选择任务组与状态。"); }
                draft.Title = savedTitle; draft.Description = descriptionEditor.Value; draft.TaskGroupId = selectedGroup.Id; draft.StatusId = selectedStatus.Id;
                draft.Priority = new[] { "high", "medium", "low" }[priority.SelectedIndex]; draft.DueDate = DateText(due.Date); draft.DueTime = string.IsNullOrWhiteSpace(dueTime.Text) ? null : dueTime.Text.Trim();
                draft.StartOffset = double.IsNaN(offset.Value) ? null : Math.Round(offset.Value);
                draft.Tags = tags.Text.Split([',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList();
                draft.Reminder = string.IsNullOrWhiteSpace(reminder.Text) ? null : DateTimeOffset.Parse(reminder.Text).ToUniversalTime().ToString("O");
                draft.Dependencies = dependencyCandidates.Where(candidate => selectedDependencyIds.Contains(candidate.Id)).Select(candidate =>
                {
                    var previous = draft.Dependencies.FirstOrDefault(old => old.TaskId == candidate.Id);
                    var days = dependencyInputs[candidate.Id].Value;
                    return new Dependency { TaskId = candidate.Id, DayOffset = double.IsNaN(days) ? 0 : Math.Round(days), Extra = previous?.Extra ?? [] };
                }).ToList();
                if (draft.Subtasks.FirstOrDefault(s => string.IsNullOrWhiteSpace(s.Title)) is { } emptySubtask) { invalidField = subtaskFields.GetValueOrDefault(emptySubtask.Id); throw new InvalidOperationException("子任务标题不能为空。"); }
                if (recurrence.IsChecked == true)
                {
                    if (end.SelectedIndex == 2 && until.Date is null) { invalidField = until; throw new InvalidOperationException("请选择循环截止日期。"); }
                    draft.Recurrence = new RecurrenceRule { Freq = freqValues[frequency.SelectedIndex], Interval = (int)(double.IsNaN(interval.Value) ? 1 : interval.Value), End = ends[end.SelectedIndex], Count = (int)(double.IsNaN(count.Value) ? 1 : count.Value), Until = DateText(until.Date), ByWeekday = weekdayBoxes.Select((box, i) => (box, i)).Where(x => x.box.IsChecked == true).Select(x => x.i).ToList(), SourceTaskId = draft.Recurrence?.SourceTaskId, Extra = draft.Recurrence?.Extra ?? [] };
                }
                else draft.Recurrence = null;
                // Append comments once, after successful validation; retain the draft on a failed write.
                var replacement = PmSerializer.CloneTask(draft);
                if (!string.IsNullOrWhiteSpace(commentEditor.Value)) replacement.Comments.Add(new TaskComment { Id = Guid.NewGuid().ToString(), Content = commentEditor.Value.Trim(), CreatedAt = DateTimeOffset.UtcNow.ToString("O") });
                await ChangeAsync(project.Id, p =>
                {
                    _service.SaveTask(p, replacement, isNew);
                    foreach (var tag in replacement.Tags.Where(tag => p.Tags.All(t => t.Name != tag))) p.Tags.Add(new Tag { Id = Guid.NewGuid().ToString(), Name = tag, Color = p.Color });
                });
            }
            catch (Exception ex) { args.Cancel = true; validation.Show(ex.Message, invalidField); }
            finally { deferral.Complete(); }
        }
        dialog.PrimaryButtonClick += SaveTask;
        try
        {
            while (await DialogAsync(dialog) == ContentDialogResult.Secondary)
            {
                if (!await ConfirmAsync("删除任务", $"删除“{EditorPreview(draft.Title, 128)}”？相关依赖会同步清理。")) continue;
                try
                {
                    await ChangeAsync(project.Id, p => _service.DeleteTask(p, draft.Id));
                    break;
                }
                catch (Exception ex) { validation.Show(ex.Message); }
                // Reuse this dialog and its controls after cancellation or a failed delete.
            }
        }
        finally { dialog.PrimaryButtonClick -= SaveTask; dialog.Content = null; content.Children.Clear(); }
    }

    private async Task EditMilestoneAsync(string projectId, string? milestoneId = null)
    {
        if (!_ready || _busy) return;
        var project = _projects.FirstOrDefault(p => p.Id == projectId);
        if (project is null) return;
        var existing = milestoneId is null ? null : project.Milestones.FirstOrDefault(m => m.Id == milestoneId)
            ?? throw new InvalidOperationException("里程碑已不存在，请刷新后重试。");
        var savedId = existing?.Id ?? Guid.NewGuid().ToString();
        var title = new TextBox { Header = "里程碑名称", Text = existing?.Title ?? "", MaxLength = 500 };
        var date = new CalendarDatePicker { Header = "目标日期", Date = existing is null ? DateTimeOffset.Now : ParseDate(existing.Date), HorizontalAlignment = HorizontalAlignment.Stretch };
        var descriptionEditor = new LongTextEditor(this, "说明", existing?.Description ?? "", 96);
        var description = descriptionEditor.Input;
        AutomationProperties.SetAutomationId(title, "MilestoneEditorTitle");
        AutomationProperties.SetAutomationId(description, "MilestoneEditorDescription");
        var color = new TextBox { Header = "标记颜色", Text = existing?.Color ?? project.Color, PlaceholderText = "#A33B32", MaxLength = 9 };
        var error = new InfoBar { IsOpen = false, Severity = InfoBarSeverity.Error };
        var body = Column(14); body.MinWidth = 320;
        foreach (var element in new UIElement[] { error, title, date, descriptionEditor.Panel, color }) body.Children.Add(element);
        var dialog = new ContentDialog
        {
            Title = existing is null ? "添加里程碑" : "编辑里程碑", Content = Scroll(body), PrimaryButtonText = "保存",
            SecondaryButtonText = existing is null ? "" : "删除", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary
        };
        var validation = new EditorValidation(error);
        void UpdateSaveEnabled() => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(title.Text) && date.Date is not null;
        title.TextChanged += (_, _) => UpdateSaveEnabled();
        date.DateChanged += (_, _) => UpdateSaveEnabled();
        UpdateSaveEnabled();
        async void SaveMilestone(ContentDialog sender, ContentDialogButtonClickEventArgs args)
        {
            var deferral = args.GetDeferral();
            Control? invalidField = null;
            validation.Clear();
            try
            {
                if (string.IsNullOrWhiteSpace(title.Text)) { invalidField = title; throw new InvalidOperationException("请填写里程碑名称。"); }
                if (date.Date is null) { invalidField = date; throw new InvalidOperationException("请选择目标日期。"); }
                if (!System.Text.RegularExpressions.Regex.IsMatch(color.Text.Trim(), "^#(?:[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$")) { invalidField = color; throw new InvalidOperationException("标记颜色应为 # 加六位或八位十六进制字符。"); }
                await ChangeAsync(projectId, p =>
                {
                    var milestone = existing is null ? new Milestone { Id = savedId }
                        : p.Milestones.FirstOrDefault(m => m.Id == savedId) ?? throw new InvalidOperationException("里程碑已不存在，请刷新后重试。");
                    milestone.Title = title.Text.Trim(); milestone.Date = DateText(date.Date)!;
                    milestone.Description = descriptionEditor.Value; milestone.Color = color.Text.Trim();
                    if (existing is null) p.Milestones.Add(milestone);
                });
            }
            catch (Exception ex) { args.Cancel = true; validation.Show(ex.Message, invalidField); }
            finally { deferral.Complete(); }
        }
        dialog.PrimaryButtonClick += SaveMilestone;
        try
        {
            while (await DialogAsync(dialog) == ContentDialogResult.Secondary && existing is not null)
            {
                if (!await ConfirmAsync("删除里程碑", $"删除“{EditorPreview(existing.Title, 128)}”？可以使用撤销恢复。")) continue;
                try
                {
                    await ChangeAsync(projectId, p => p.Milestones.RemoveAll(m => m.Id == existing.Id));
                    break;
                }
                catch (Exception ex) { validation.Show(ex.Message); }
            }
        }
        finally { dialog.PrimaryButtonClick -= SaveMilestone; dialog.Content = null; body.Children.Clear(); }
    }
    private static DateTimeOffset? ParseDate(string? date) => DateTimeOffset.TryParse(date, out var value) ? value : null;
    private static string? DateText(DateTimeOffset? date) => date?.ToString("yyyy-MM-dd");
    private static string TrackingText(ProjectTask task)
    {
        if (!DateTimeOffset.TryParse(task.TrackedStart, out var start)) return "尚未开始计时";
        var end = DateTimeOffset.TryParse(task.CompletedAt, out var completed) ? completed : DateTimeOffset.UtcNow;
        var duration = end - start; return $"开始于 {start.ToLocalTime():yyyy-MM-dd HH:mm} · 工时 {Math.Max(0, duration.TotalHours):F1} 小时";
    }
    private static string EditorDate(string? value) => DateTimeOffset.TryParse(value, out var date) ? date.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "—";
    private static TextBlock EditorText(string text, double size = 13, string token = "PrimaryTextBrush") => new()
    {
        Text = text, FontSize = size, FontFamily = UiFont, Foreground = ThemeBrush(token), TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center
    };
    private static StackPanel EditorSection(string title, UIElement content)
    {
        var section = Column(5);
        var label = EditorText(title, 11, "SecondaryTextBrush"); label.FontWeight = Microsoft.UI.Text.FontWeights.Normal;
        section.Children.Add(label); section.Children.Add(content); return section;
    }
    private static void FocusEditorField(Control field, FocusState state = FocusState.Programmatic, FrameworkElement? companion = null)
    {
        field.DispatcherQueue.TryEnqueue(() =>
        {
            field.UpdateLayout();
            field.Focus(state == FocusState.Unfocused ? FocusState.Programmatic : state);
            if (companion is null)
            {
                field.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = .25 });
                return;
            }
            // Native text inputs first reveal their caret on focus. Correct the scroll
            // after that request and layout, including the adjacent error explanation.
            field.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (!field.IsLoaded || !companion.IsLoaded) return;
                field.UpdateLayout();
                for (DependencyObject? parent = VisualTreeHelper.GetParent(field); parent is not null; parent = VisualTreeHelper.GetParent(parent))
                {
                    if (parent is not ScrollViewer scroll || scroll.Content is not FrameworkElement content
                        || scroll.ScrollableHeight <= 0 || scroll.ViewportHeight <= 0) continue;
                    var fieldBounds = field.TransformToVisual(content).TransformBounds(new Windows.Foundation.Rect(0, 0, field.ActualWidth, field.ActualHeight));
                    var errorBounds = companion.TransformToVisual(content).TransformBounds(new Windows.Foundation.Rect(0, 0, companion.ActualWidth, companion.ActualHeight));
                    var top = Math.Min(fieldBounds.Top, errorBounds.Top);
                    var bottom = Math.Max(fieldBounds.Bottom, errorBounds.Bottom);
                    const double gap = 12;
                    var targetOffset = scroll.VerticalOffset;
                    if (top < targetOffset + gap) targetOffset = top - gap;
                    else if (bottom > targetOffset + scroll.ViewportHeight - gap) targetOffset = bottom - scroll.ViewportHeight + gap;
                    targetOffset = Math.Clamp(targetOffset, 0, scroll.ScrollableHeight);
                    if (Math.Abs(targetOffset - scroll.VerticalOffset) > .5)
                        scroll.ChangeView(null, targetOffset, null, true);
                }
            });
        });
    }
    private sealed class EditorValidation(InfoBar summary)
    {
        private Control? _field;
        private TextBlock? _inline;
        private StackPanel? _host;
        private string _originalHelp = "";
        private string _originalStatus = "";

        public void Clear()
        {
            if (_field is not null)
            {
                AutomationProperties.SetHelpText(_field, _originalHelp);
                AutomationProperties.SetItemStatus(_field, _originalStatus);
                if (_inline is not null) AutomationProperties.GetDescribedBy(_field).Remove(_inline);
            }
            if (_inline is not null) _host?.Children.Remove(_inline);
            _field = null; _inline = null; _host = null;
            summary.IsOpen = false; summary.Visibility = Visibility.Collapsed;
        }

        public void Show(string message, Control? field = null)
        {
            Clear();
            summary.Severity = InfoBarSeverity.Error; summary.Message = message;
            summary.Visibility = Visibility.Visible; summary.IsOpen = true; summary.IsTabStop = true;
            AutomationProperties.SetAutomationId(summary, "EditorErrorSummary");
            AutomationProperties.SetName(summary, "保存未完成：" + message);
            if (field is not null)
            {
                _field = field;
                _originalHelp = AutomationProperties.GetHelpText(field);
                _originalStatus = AutomationProperties.GetItemStatus(field);
                AutomationProperties.SetHelpText(field, message);
                AutomationProperties.SetItemStatus(field, "输入有误");
                // Insert beside the closest form row, preserving native field headers.
                DependencyObject child = field;
                while (VisualTreeHelper.GetParent(child) is { } parent)
                {
                    if (parent is StackPanel panel && child is UIElement anchor)
                    {
                        _inline = EditorText(message, 11, "DangerTextBrush");
                        _inline.Margin = new Thickness(0, -Math.Max(0, panel.Spacing - 4), 0, 0);
                        AutomationProperties.SetAutomationId(_inline, "EditorFieldError");
                        panel.Children.Insert(panel.Children.IndexOf(anchor) + 1, _inline); _host = panel;
                        AutomationProperties.GetDescribedBy(field).Add(_inline);
                        break;
                    }
                    child = parent;
                }
            }
            FocusEditorField(field ?? summary, companion: _inline);
        }
    }
    private Button EditorButton(string text, Func<Task> action, bool accent = false)
    {
        var button = new Button
        {
            Style = (Style)Application.Current.Resources[accent ? "CompactAccentButtonStyle" : "CompactButtonStyle"],
            Content = text, FontFamily = UiFont, FontSize = 12, MinHeight = 32, MinWidth = 0,
            Padding = new Thickness(8, 5, 8, 5), CornerRadius = new CornerRadius(2), BorderThickness = new Thickness(1),
            Background = ThemeBrush(accent ? "AccentBrush" : "CardBackgroundBrush"),
            Foreground = ThemeBrush(accent ? "AccentContrastBrush" : "SecondaryTextBrush"),
            BorderBrush = ThemeBrush(accent ? "AccentBrush" : "BorderBrush")
        };
        button.Click += async (_, _) => await GuardAsync(action);
        return button;
    }
    private static Grid EditorAddRow(FrameworkElement field, FrameworkElement action)
    {
        var row = new Grid { ColumnSpacing = 4 };
        row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        if (action is Button button && Equals(button.Content, "+"))
        {
            var label = field switch { TextBox text => text.PlaceholderText, ComboBox picker => picker.PlaceholderText, _ => "添加" };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, label.Replace("选择", "添加").TrimEnd('…'));
        }
        row.Children.Add(field); Grid.SetColumn(action, 1); row.Children.Add(action); return row;
    }
    private Grid EditorChoiceButtons(ComboBox state, string[] labels, string[]? colors = null)
    {
        var row = new Grid { ColumnSpacing = 6 };
        var buttons = new List<Button>();
        for (var i = 0; i < labels.Length; i++)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition()); var index = i;
            var button = EditorButton(labels[index], () => { state.SelectedIndex = index; return Task.CompletedTask; });
            button.HorizontalAlignment = HorizontalAlignment.Stretch; button.Padding = new Thickness(4, 5, 4, 5);
            Grid.SetColumn(button, index); row.Children.Add(button); buttons.Add(button);
        }
        void Refresh()
        {
            for (var i = 0; i < buttons.Count; i++)
            {
                var active = state.SelectedIndex == i; var button = buttons[i];
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(button, active ? "已选中" : "选择此项");
                AutomationProperties.SetItemStatus(button, active ? "已选中" : "未选中");
                var color = ThemeBrush(colors is null ? "AccentBrush" : colors[i]);
                if (colors is not null)
                {
                    button.Foreground = ThemeBrush(colors[i].Replace("Brush", "TextBrush"));
                    button.Background = color is SolidColorBrush solid ? new SolidColorBrush(solid.Color) { Opacity = 0.13 } : ThemeBrush("AccentLightBrush");
                    button.BorderBrush = active ? color : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                    button.BorderThickness = active ? new Thickness(1, 1, 1, 2) : new Thickness(1);
                }
                else
                {
                    button.Foreground = ThemeBrush(active ? "AccentContrastBrush" : "SecondaryTextBrush");
                    button.Background = active ? color : ThemeBrush("SubtleBackgroundBrush");
                    button.BorderBrush = active ? color : ThemeBrush("BorderBrush");
                }
                PreserveButtonPalette(button);
            }
        }
        state.SelectionChanged += (_, _) => Refresh(); Refresh(); return row;
    }
    private static DataTemplate EditorHeaderTemplateFor(string label) => (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load($$"""
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
            <TextBlock Text="{{System.Security.SecurityElement.Escape(label)}}" FontFamily="{ThemeResource AppFontFamily}" FontSize="11" FontWeight="Normal"
                       Foreground="{ThemeResource SecondaryTextBrush}" Margin="0,0,0,4" />
        </DataTemplate>
        """);
    private static Grid FormGrid(params FrameworkElement[] children)
    {
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < children.Length; i++)
        { if (i % 2 == 0) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); Grid.SetColumn(children[i], i % 2); Grid.SetRow(children[i], i / 2); grid.Children.Add(children[i]); }
        return grid;
    }
}
