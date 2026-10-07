using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using PureProject.Core;
using Windows.System;

namespace PureProject.WinUI;

public sealed partial class MainWindow
{
    private void RenderShell()
    {
        BrandLogo.Source = SourceIcon("logo", 25, true).Source;
        ProjectsFolderIcon.Source = SourceIcon("folder", 15, true).Source;
        TopSearchButton.Content = SourceIcon("search", 15);
        TopSettingsButton.Content = SourceIcon("settings", 15);
        SidebarSearchButton.Content = SourceIcon("search", 15);
        NavigationNewProjectButton.Content = SourceIcon("plus", 16);
        TopContext.Text = SingleLinePreview(Current?.Name); TopContext.MaxLines = 1;
        var dashboardContent = Row(9);
        dashboardContent.Children.Add(SourceIcon("dashboard", 15, true));
        dashboardContent.Children.Add(new TextBlock { Text = "仪表盘", FontFamily = SerifFont, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        DashboardButton.Content = dashboardContent;
        AutomationProperties.SetAutomationId(DashboardButton, "DashboardNavigation");
        DashboardButton.Background = Current is null ? ThemeBrush("CardBackgroundBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        DashboardButton.Foreground = ThemeBrush(Current is null ? "AccentBrush" : "PrimaryTextBrush");
        DashboardButton.BorderBrush = ThemeBrush("AccentBrush");
        DashboardButton.BorderThickness = new Thickness(Current is null ? 3 : 0, 0, 0, 0);
        if (Current is null) PreserveButtonPalette(DashboardButton);
        else RestoreButtonPalette(DashboardButton);
        SidebarExportButton.IsEnabled = _ready;
        RenderSidebarProjects();
        ViewTabsHost.Children.Clear();
        var names = new[] { "看板", "列表", "日历", "时间线", "依赖图" };
        for (var i = 0; i < names.Length; i++)
        {
            var index = i;
            var button = new Button { Content = names[i], Tag = i, Height = 44, MinHeight = 44, MinWidth = 0,
                Padding = new Thickness(13, 0, 13, 0), FontSize = 13, FontFamily = SerifFont, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                CornerRadius = new CornerRadius(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Foreground = ThemeBrush(i == _view ? "AccentBrush" : "SecondaryTextBrush"),
                BorderBrush = ThemeBrush("AccentBrush"), BorderThickness = new Thickness(0, 0, 0, i == _view ? 3 : 0) };
            AutomationProperties.SetName(button, names[i]);
            AutomationProperties.SetAutomationId(button, "ProjectView:" + i);
            AutomationProperties.SetItemStatus(button, i == _view ? "当前视图" : "");
            AutomationProperties.SetHelpText(button, i == _view ? "当前视图" : "切换项目视图");
            if (i == _view) PreserveButtonPalette(button);
            button.Click += (_, _) =>
            {
                var state = button.FocusState;
                if (_view != index) { _view = index; Render(); FocusUiElement("ProjectView:" + index, state == FocusState.Keyboard ? state : FocusState.Programmatic); }
            };
            button.KeyDown += (_, args) =>
            {
                var next = args.Key switch { VirtualKey.Left => (index + 4) % 5, VirtualKey.Right => (index + 1) % 5, VirtualKey.Home => 0, VirtualKey.End => 4, _ => -1 };
                if (next < 0) return;
                args.Handled = true; _view = next; Render(); FocusUiElement("ProjectView:" + next);
            };
            ViewTabsHost.Children.Add(button);
        }
    }

    private void RenderSidebarProjects()
    {
        SidebarProjectList.Children.Clear();
        var filter = SidebarSearchBox.Text.Trim();
        foreach (var project in _projects.OrderBy(p => p.Archived).ThenBy(p => p.SortOrder).Where(p => p.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
        {
            var selected = project.Id == _selectedId;
            var row = new Grid { MinHeight = 38 };
            var content = new Grid { ColumnSpacing = 10 };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            content.ColumnDefinitions.Add(new ColumnDefinition());
            content.Children.Add(new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = ColorBrush(project.Color), VerticalAlignment = VerticalAlignment.Center });
            var title = new TextBlock { Text = SingleLinePreview(project.Name) + (project.Archived ? " · 已归档" : ""), FontFamily = UiFont, FontSize = 13, MaxLines = 1,
                MaxWidth = 130, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left };
            Grid.SetColumn(title, 1); content.Children.Add(title);
            var open = new Button { Content = content, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                MinHeight = 38, Padding = new Thickness(33, 8, 11, 8), BorderThickness = new Thickness(selected ? 3 : 1, 1, 1, 1), CornerRadius = new CornerRadius(2),
                Background = selected ? ThemeBrush("CardBackgroundBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent), Foreground = ThemeBrush(selected ? "AccentBrush" : "PrimaryTextBrush"),
                BorderBrush = selected ? ThemeBrush("BorderBrush") : null };
            if (selected) PreserveButtonPalette(open);
            open.Click += (_, _) => { if (_selectedId == project.Id) return; _selectedId = project.Id; _groupId = null; Render(); };
            ToolTipService.SetToolTip(open, project.Name); AutomationProperties.SetName(open, project.Name); row.Children.Add(open);
            AutomationProperties.SetAutomationId(open, "SidebarProject:" + project.Id);
            AutomationProperties.SetItemStatus(open, selected ? "当前项目" : project.Archived ? "已归档" : "");
            if (selected) row.Children.Add(new Border { Width = 3, HorizontalAlignment = HorizontalAlignment.Left, Background = ThemeBrush("AccentBrush"), IsHitTestVisible = false });
            var actions = Row(4); actions.HorizontalAlignment = HorizontalAlignment.Right; actions.VerticalAlignment = VerticalAlignment.Center; actions.Margin = new Thickness(0, 0, 7, 0); actions.Opacity = 0;
            var edit = ShellIconButton("pencil", "编辑项目", async () => await EditProjectAsync(project.Id));
            var delete = ShellIconButton("trash", "删除项目", async () =>
            {
                if (await ConfirmAsync("删除项目", $"确定删除“{project.Name}”及全部任务？可使用撤销恢复。"))
                    await CommitAsync(projects => projects.RemoveAll(p => p.Id == project.Id), collectionOnly: true);
            });
            actions.Children.Add(edit); actions.Children.Add(delete); row.Children.Add(actions);
            AutomationProperties.SetAutomationId(edit, "ProjectEdit:" + project.Id);
            AutomationProperties.SetName(edit, "编辑项目：" + project.Name);
            AutomationProperties.SetAutomationId(delete, "ProjectDelete:" + project.Id);
            AutomationProperties.SetName(delete, "删除项目：" + project.Name);
            row.PointerEntered += (_, _) => { actions.Opacity = 1; title.MaxWidth = 118; };
            row.PointerExited += (_, _) => { if (!edit.FocusState.Equals(FocusState.Keyboard) && !delete.FocusState.Equals(FocusState.Keyboard)) actions.Opacity = 0; title.MaxWidth = 130; };
            row.GotFocus += (_, _) => { actions.Opacity = 1; title.MaxWidth = 118; };
            row.LostFocus += (_, _) => DispatcherQueue.TryEnqueue(() =>
            {
                var focused = Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject;
                if (!ShellDescendants<DependencyObject>(row).Contains(focused!)) { actions.Opacity = 0; title.MaxWidth = 130; }
            });
            var menu = ViewMenu();
            menu.Items.Add(ViewMenuItem("管理项目", async () => { _selectedId = project.Id; Render(); await ManageProjectAsync(); }));
            open.ContextFlyout = menu;
            SidebarProjectList.Children.Add(row);
        }
        if (SidebarProjectList.Children.Count == 0)
            SidebarProjectList.Children.Add(new TextBlock { Text = filter.Length > 0 ? "未找到匹配项目" : "暂无项目", FontFamily = UiFont, FontSize = 12, Foreground = ThemeBrush("MutedTextBrush"), Margin = new Thickness(12), HorizontalAlignment = HorizontalAlignment.Center });
    }

    private Button ShellIconButton(string icon, string label, Func<Task> action)
    {
        var button = new Button { Content = SourceIcon(icon, 13), Width = 24, Height = 28, MinHeight = 0, MinWidth = 0,
            Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        button.Click += async (_, _) => await GuardAsync(action); AutomationProperties.SetName(button, label); ToolTipService.SetToolTip(button, label); return button;
    }
    private void Dashboard_Click(object sender, RoutedEventArgs e) { _selectedId = null; _groupId = null; Render(); }
    private void SidebarSearch_Click(object sender, RoutedEventArgs e)
    {
        SidebarSearchBox.Visibility = SidebarSearchBox.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (SidebarSearchBox.Visibility == Visibility.Visible) SidebarSearchBox.Focus(FocusState.Programmatic);
        else SidebarSearchBox.Text = "";
    }
    private void SidebarSearch_TextChanged(object sender, TextChangedEventArgs e) { if (_ready) RenderSidebarProjects(); }
    private async void Settings_Click(object sender, RoutedEventArgs e) => await GuardAsync(ShowSettingsAsync);
    private async void Export_Click(object sender, RoutedEventArgs e) => await GuardAsync(ShowExportAsync);
    private async void TopSearch_Click(object sender, RoutedEventArgs e) => await GuardAsync(ShowGlobalSearchAsync);
    private async void Flowchart_Click(object sender, RoutedEventArgs e) => await GuardAsync(async () => await DialogAsync(new ContentDialog
    { Title = "流程图", Content = Label("旧版流程图数据会随项目导入和导出保留。原生流程图编辑器尚未迁移，可在 v1.2 中继续编辑。", true), CloseButtonText = "关闭" }));
    private void Minimize_Click(object sender, RoutedEventArgs e) { if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.Minimize(); }
    private void Maximize_Click(object sender, RoutedEventArgs e)
    { if (AppWindow.Presenter is OverlappedPresenter presenter) { if (presenter.State == OverlappedPresenterState.Maximized) presenter.Restore(); else presenter.Maximize(); } }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private Task FocusTaskSearchAsync()
    {
        if (Current is null) return ShowGlobalSearchAsync();
        _view = 1; Render();
        FocusUiElement("TaskSearch");
        return Task.CompletedTask;
    }
    private sealed record SearchHit(Project Project, ProjectTask? Task, TaskGroup? Group, string Kind, string Title, string Context);

    private IEnumerable<SearchHit> SearchAll(string query)
    {
        bool Matches(string? value) => value?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
        foreach (var project in _projects.OrderBy(p => p.Archived).ThenBy(p => p.SortOrder))
        {
            if (Matches(project.Name)) yield return new(project, null, null, "项目", project.Name, project.Archived ? "已归档项目" : "项目");
            foreach (var group in project.TaskGroups.Where(g => Matches(g.Name)))
                yield return new(project, null, group, "任务组", group.Name, project.Name + (group.Archived ? " · 已归档任务组" : " · 任务组"));
            foreach (var task in project.Tasks.Where(t => Matches(t.Title) || Matches(t.Description) || t.Subtasks.Any(s => Matches(s.Title))))
            {
                var group = project.TaskGroups.FirstOrDefault(g => g.Id == task.TaskGroupId);
                yield return new(project, task, group, "任务", task.Title, project.Name + " · " + group?.Name);
            }
        }
    }

    private async Task ShowGlobalSearchAsync()
    {
        var input = new TextBox { PlaceholderText = "搜索项目、任务组、任务、子任务…", FontSize = 14, MinHeight = 36, MaxLength = 200 };
        AutomationProperties.SetName(input, "全局搜索关键词"); AutomationProperties.SetAutomationId(input, "GlobalSearchInput");
        AutomationProperties.SetHelpText(input, "输入关键词；按向下键进入结果，按 Enter 打开第一项。");
        var status = new TextBlock { FontSize = 12, Foreground = ThemeBrush("MutedTextBrush"), TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(status, "GlobalSearchStatus"); AutomationProperties.SetLiveSetting(status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        var results = Column(4); var resultsScroll = Scroll(results); resultsScroll.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        AutomationProperties.SetAutomationId(resultsScroll, "GlobalSearchResults");
        var form = new Grid { Width = Math.Max(280, Math.Min(560, Root.ActualWidth - 96)), Height = Math.Min(460, Math.Max(220, Root.ActualHeight - 230)), RowSpacing = 12 };
        form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); form.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); form.RowDefinitions.Add(new RowDefinition());
        form.Children.Add(input); Grid.SetRow(status, 1); form.Children.Add(status); Grid.SetRow(resultsScroll, 2); form.Children.Add(resultsScroll);
        var dialog = new ContentDialog { Tag = "GlobalSearch", Title = "全局搜索", Content = form, CloseButtonText = "关闭" };
        SearchHit? chosen = null;
        void Choose(SearchHit hit) { chosen = hit; dialog.Hide(); }
        void AnnounceResults(string message)
        {
            status.Text = message;
            if (Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(status) is { } peer)
                peer.RaiseAutomationEvent(Microsoft.UI.Xaml.Automation.Peers.AutomationEvents.LiveRegionChanged);
        }
        void Refresh()
        {
            results.Children.Clear(); var query = input.Text.Trim();
            if (query.Length == 0) { AnnounceResults("搜索所有项目；用 ↑ ↓ 浏览结果，Enter 打开。"); results.Children.Add(Label("输入名称、描述或子任务关键词。", true)); return; }
            var matches = SearchAll(query).Take(61).ToList();
            AnnounceResults(matches.Count == 0 ? "没有找到匹配结果，试试更短的关键词。" : matches.Count > 60 ? "显示前 60 项结果，请补充关键词缩小范围。" : $"找到 {matches.Count} 项结果");
            foreach (var hit in matches.Take(60))
            {
                var text = Column(4);
                text.Children.Add(new TextBlock { Text = hit.Title, FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis });
                text.Children.Add(new TextBlock { Text = hit.Context, FontSize = 12, Foreground = ThemeBrush("MutedTextBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
                var row = new Grid { ColumnSpacing = 10 }; row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) }); row.ColumnDefinitions.Add(new ColumnDefinition());
                row.Children.Add(SourceIcon(hit.Kind == "任务" ? "clipboard" : "folder", 16)); Grid.SetColumn(text, 1); row.Children.Add(text);
                var button = new Button { Content = row, Tag = hit, Padding = new Thickness(10), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                AutomationProperties.SetName(button, $"打开{hit.Kind}：{hit.Title}，{hit.Context}");
                AutomationProperties.SetAutomationId(button, "GlobalResult:" + hit.Kind + ":" + (hit.Task?.Id ?? hit.Group?.Id ?? hit.Project.Id));
                ToolTipService.SetToolTip(button, hit.Title + "\n" + hit.Context);
                button.Click += (_, _) => Choose(hit);
                button.KeyDown += (_, args) =>
                {
                    if (args.Key is not (VirtualKey.Up or VirtualKey.Down)) return;
                    args.Handled = true; var index = results.Children.IndexOf(button) + (args.Key == VirtualKey.Up ? -1 : 1);
                    if (index < 0) input.Focus(FocusState.Keyboard);
                    else if (index < results.Children.Count && results.Children[index] is Button next) { next.Focus(FocusState.Keyboard); next.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false }); }
                };
                results.Children.Add(button);
            }
            resultsScroll.ChangeView(null, 0, null);
        }
        input.TextChanged += (_, _) => Refresh(); Refresh();
        input.KeyDown += (_, args) =>
        {
            if (results.Children.OfType<Button>().FirstOrDefault() is not { } first) return;
            if (args.Key == VirtualKey.Down) { args.Handled = true; first.Focus(FocusState.Keyboard); }
            else if (args.Key == VirtualKey.Enter && first.Tag is SearchHit hit) { args.Handled = true; Choose(hit); }
        };
        dialog.Opened += (_, _) => input.Focus(FocusState.Programmatic);
        await DialogAsync(dialog);
        if (chosen is not null)
        {
            _selectedId = chosen.Project.Id; _groupId = chosen.Group?.Id;
            if (chosen.Task is null) _view = 0;
            Render();
            if (chosen.Task is not null) await EditTaskAsync(chosen.Task.Id, chosen.Project.Id);
            else FocusUiElement(chosen.Group is not null
                ? Root.ActualWidth < 1200 ? "KanbanGroupDrawerToggle" : "KanbanGroup:" + chosen.Group.Id
                : "SidebarProject:" + chosen.Project.Id);
        }
    }

    private static IEnumerable<T> ShellDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T item) yield return item;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in ShellDescendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
