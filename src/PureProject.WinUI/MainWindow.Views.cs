using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Shapes;
using PureProject.Core;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.UI.Text;

namespace PureProject.WinUI;

public sealed partial class MainWindow
{
    private int _dashboardSort;
    private bool _dashboardDescending = true;
    private string? _listStatusFilter;
    private string? _listTagFilter;
    private DateOnly? _calendarSelectedDate;
    private string? _graphStatusId;
    private sealed class TaskListLifetime(Action retire, Action release)
    {
        public Action Retire { get; set; } = retire;
        public Action Release { get; } = release;
    }
    private readonly Dictionary<ListView, TaskListLifetime> _taskListCleanup = new();
    private DataTemplate? _kanbanTaskTemplate;
    private DataTemplate? _tableTaskTemplate;
    private int _contentGeneration;
    private bool _viewRefreshQueued;
    private Action<bool>? _pendingViewRefresh;
    private bool _pendingViewRefreshShell;
    private int _pendingViewRefreshGeneration;
    private string? _pendingViewRefreshProject;

    private void ReleaseTaskLists(Action detach, IEnumerable<ListView>? lists = null)
    {
        var retiring = new List<TaskListLifetime>();
        foreach (var list in (lists ?? _taskListCleanup.Keys).ToArray())
            if (_taskListCleanup.Remove(list, out var lifetime)) { lifetime.Retire(); retiring.Add(lifetime); }
        try { detach(); }
        finally { foreach (var lifetime in retiring) lifetime.Release(); }
    }

    private bool IsCurrentViewEvent(Control source, int generation, string? projectId)
        => _ready && generation == _contentGeneration && string.Equals(_selectedId, projectId, StringComparison.Ordinal)
            && source.IsLoaded && BelongsToRoot(source);

    private void QueueViewRefresh(Control source, int generation, string? projectId, bool renderShell, string focusId)
    {
        var samePendingView = _pendingViewRefresh is not null && generation == _pendingViewRefreshGeneration
            && string.Equals(projectId, _pendingViewRefreshProject, StringComparison.Ordinal);
        _pendingViewRefreshShell = renderShell || (samePendingView && _pendingViewRefreshShell);
        _pendingViewRefreshGeneration = generation; _pendingViewRefreshProject = projectId;
        _pendingViewRefresh = refreshShell =>
        {
            if (!IsCurrentViewEvent(source, generation, projectId)) return;
            if (refreshShell) Render(); else RenderContent();
            FocusUiElement(focusId);
        };
        if (_viewRefreshQueued) return;
        _viewRefreshQueued = true;
        if (!DispatcherQueue.TryEnqueue(() =>
        {
            _viewRefreshQueued = false;
            var refresh = _pendingViewRefresh; _pendingViewRefresh = null;
            var refreshShell = _pendingViewRefreshShell; _pendingViewRefreshShell = false;
            refresh?.Invoke(refreshShell);
        })) { _viewRefreshQueued = false; _pendingViewRefresh = null; _pendingViewRefreshShell = false; }
    }

    private void ParkFocusBeforeDetach(DependencyObject retiringRoot)
    {
        if (_dialogOpen || Root.XamlRoot is null) return;
        for (var element = FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject;
             element is not null; element = VisualTreeHelper.GetParent(element))
        {
            if (!ReferenceEquals(element, retiringRoot)) continue;
            ++_focusRequestVersion; _pendingFocusAction = null;
            var anchor = FindUiControl(Current is null ? "DashboardNavigation" : "ProjectView:" + _view, null);
            if (anchor?.Focus(FocusState.Programmatic) != true) DashboardButton.Focus(FocusState.Programmatic);
            break;
        }
    }

    private void RenderContent()
    {
        var focus = CaptureUiFocus();
        try
        {
            _contentGeneration++;
            ParkFocusBeforeDetach(ContentHost);
            ReleaseTaskLists(() => ContentHost.Children.Clear());
            if (Current is not { } p) { RenderDashboard(); return; }
            ContentHost.Children.Add(_view switch { 1 => BuildTaskList(p), 2 => BuildCalendar(p), 3 => BuildTimeline(p), 4 => BuildDependencies(p), _ => BuildKanban(p) });
        }
        finally { RestoreUiFocus(focus, Current is null ? "DashboardNavigation" : "ProjectView:" + _view); }
    }

    private static string BoundedViewText(string? text, int utf16Limit = 256)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (text.Length <= utf16Limit) return text;
        var length = utf16Limit;
        if (char.IsHighSurrogate(text[length - 1]) && char.IsLowSurrogate(text[length])) length--;
        return text[..length] + "…";
    }

    private static string SingleLinePreview(string? text, int utf16Limit = 256)
        => BoundedViewText(text?.Replace("\r\n", " ", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' '), utf16Limit);

    private TextBlock ViewText(string text, double size = 12, string color = "PrimaryTextBrush", bool bold = false, bool serif = false)
        => new() { Text = BoundedViewText(text), FontSize = size, Foreground = ThemeBrush(color switch { "AccentBrush" => "AccentTextBrush", "DangerBrush" => "DangerTextBrush", "WarningBrush" => "WarningTextBrush", "SuccessBrush" => "SuccessTextBrush", _ => color }), FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
            FontFamily = serif ? SerifFont : UiFont, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };

    private static void ViewIdentity(DependencyObject element, string id, string? name = null, string? help = null)
    {
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(element, id);
        if (name is not null) Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(element, BoundedViewText(name, 384));
        if (help is not null) Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(element, BoundedViewText(help, 512));
    }

    private void ViewTaskIdentity(FrameworkElement element, Project p, ProjectTask task)
    {
        var context = $"{p.TaskGroups.FirstOrDefault(g => g.Id == task.TaskGroupId)?.Name}，{Status(p, task)?.Name}，{PriorityText(task.Priority)}";
        if (!string.IsNullOrEmpty(task.DueDate)) context += $"，截止 {task.DueDate}";
        var preview = BoundedViewText(task.Title);
        ViewIdentity(element, "TaskOpen:" + task.Id, "打开任务：" + preview, context);
        ToolTipService.SetToolTip(element, preview + "\n" + BoundedViewText(context) + (task.Title.Length > 256 ? "\n打开详情查看完整标题" : ""));
    }

    private Border ViewBorder(UIElement child, string background = "CardBackgroundBrush", Thickness? padding = null, Thickness? border = null, double radius = 0)
        => new() { Child = child, Background = ThemeBrush(background), BorderBrush = ThemeBrush("BorderBrush"), BorderThickness = border ?? new Thickness(1),
            Padding = padding ?? new Thickness(0), CornerRadius = new CornerRadius(radius) };

    private Button ViewButton(string text, Func<Task>? action, bool accent = false, double height = 30)
    {
        var button = new Button { Style = (Style)Application.Current.Resources[accent ? "CompactAccentButtonStyle" : "CompactButtonStyle"], Content = BoundedViewText(text), FontSize = 11, FontFamily = UiFont, MinHeight = height,
            Padding = new Thickness(9, 0, 9, 0), CornerRadius = new CornerRadius(2), Background = ThemeBrush(accent ? "AccentBrush" : "CardBackgroundBrush"),
            Foreground = ThemeBrush(accent ? "AccentContrastBrush" : "SecondaryTextBrush"), BorderBrush = ThemeBrush(accent ? "AccentBrush" : "BorderBrush"), BorderThickness = new Thickness(1) };
        if (action is not null) button.Click += async (_, _) => await GuardAsync(action);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, BoundedViewText(text, 384)); return button;
    }

    private Button ViewIcon(string glyph, string name, Func<Task>? action = null, double size = 28)
    {
        var button = ViewButton("", action, height: size);
        var sourceName = glyph switch
        {
            "\uE70D" => "chevron-down", "\uE70E" => "chevron-up", "\uE710" => "plus", "\uE712" => "more-horizontal",
            "\uE700" => "menu", "\uE711" => "close", "\uE76B" => "chevron-left", "\uE76C" => "chevron-right",
            "\uE768" => "play", "\uE74D" => "trash", _ => null
        };
        button.Content = sourceName is not null ? SourceIcon(sourceName, 15) : new FontIcon { Glyph = glyph, FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 13 };
        button.Width = size; button.Height = size; button.MinWidth = size; button.Padding = new Thickness(0); button.BorderThickness = new Thickness(0);
        // SVG content is decorative and cannot receive input; retain a transparent hit surface for the button.
        button.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        ToolTipService.SetToolTip(button, BoundedViewText(name)); Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, BoundedViewText(name, 384)); return button;
    }

    private Border ViewDot(Brush color, double size = 8, bool round = false)
        => new() { Width = size, Height = size, Background = color, CornerRadius = new CornerRadius(round ? size / 2 : 0), VerticalAlignment = VerticalAlignment.Center };
    private static string PriorityText(string priority) => priority switch { "high" => "高优先级", "low" => "低优先级", _ => "中优先级" };
    private static string PriorityColor(string priority) => priority switch { "high" => "#ef4444", "low" => "#10b981", _ => "#f59e0b" };
    private static int PriorityOrder(string priority) => priority switch { "high" => 0, "low" => 2, _ => 1 };
    private static string ShortDate(string? date) => DateOnly.TryParse(date, out var parsed) ? parsed.ToString("MM-dd") : "";
    private static bool IsDone(Project p, ProjectTask t) => Status(p, t)?.Category == "done";
    private IEnumerable<ProjectTask> ViewGroupTasks(Project p) => p.Tasks.Where(t => string.IsNullOrEmpty(_groupId)
        ? p.TaskGroups.Any(g => g.Id == t.TaskGroupId && !g.Archived) : t.TaskGroupId == _groupId);

    private ComboBox ViewCombo(IEnumerable<string> items, int selected = 0, double width = 120, string name = "筛选")
    {
        var combo = new ComboBox { ItemsSource = items.ToList(), SelectedIndex = selected, Width = width, Height = 30, MinHeight = 30, FontFamily = UiFont,
            FontSize = 11, Padding = new Thickness(8, 2, 0, 2), CornerRadius = new CornerRadius(3), Background = ThemeBrush("CardBackgroundBrush"),
            Foreground = ThemeBrush("SecondaryTextBrush"), BorderBrush = ThemeBrush("BorderBrush") };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(combo, name); return combo;
    }

    private ComboBox ViewGroupFilter(Project p)
    {
        var generation = _contentGeneration;
        var groups = p.TaskGroups.Where(g => !g.Archived || g.Id == _groupId).OrderBy(g => g.SortOrder).ToList();
        var combo = ViewCombo(new[] { "全部任务组" }.Concat(groups.Select(g => g.Name)), groups.FindIndex(g => g.Id == _groupId) + 1, 120, "任务组筛选");
        ViewIdentity(combo, "TaskGroupFilter"); ToolTipService.SetToolTip(combo, combo.SelectedItem?.ToString()); combo.SelectionChanged += (_, _) => ToolTipService.SetToolTip(combo, combo.SelectedItem?.ToString());
        combo.SelectionChanged += (_, _) =>
        {
            if (!IsCurrentViewEvent(combo, generation, p.Id)) return;
            _groupId = combo.SelectedIndex <= 0 ? null : groups[combo.SelectedIndex - 1].Id; _listStatusFilter = null;
            QueueViewRefresh(combo, generation, p.Id, renderShell: true, "TaskGroupFilter");
        };
        return combo;
    }

    private ComboBox ViewPriorityFilter(Action refresh)
    {
        var combo = ViewCombo(new[] { "全部优先级", "高优先级", "中优先级", "低优先级" }, Math.Max(0, PrioritySelector.SelectedIndex), 100, "优先级筛选");
        ViewIdentity(combo, "TaskPriorityFilter");
        combo.SelectionChanged += (_, _) => { var rendering = _rendering; _rendering = true; try { PrioritySelector.SelectedIndex = combo.SelectedIndex; } finally { _rendering = rendering; } refresh(); FocusUiElement("TaskPriorityFilter"); }; return combo;
    }

    private TextBox ViewSearch(Action refresh)
    {
        var search = new TextBox { Text = SearchBox.Text ?? "", PlaceholderText = "搜索任务", FontFamily = UiFont, FontSize = 12, Height = 34, MinHeight = 34,
            Padding = new Thickness(9, 5, 9, 5), CornerRadius = new CornerRadius(3), Background = ThemeBrush("CardBackgroundBrush"), BorderBrush = ThemeBrush("BorderBrush"), Foreground = ThemeBrush("PrimaryTextBrush") };
        ViewIdentity(search, "TaskSearch", "搜索任务", "按任务标题、描述、标签或任务组筛选"); search.TextChanged += (_, _) => { SearchBox.Text = search.Text; refresh(); }; return search;
    }

    private FrameworkElement ViewHeader(Project p, string? meta = null, bool calendar = false)
    {
        var grid = new Grid { ColumnSpacing = 10, MinHeight = 63 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(ViewDot(ColorBrush(p.Color), 12, true)); var title = new Grid { ColumnSpacing = 10 };
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); title.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); var name = ViewText(SingleLinePreview(p.Name), 16, bold: true, serif: true); name.MaxWidth = 320; name.MaxLines = 1; name.HorizontalAlignment = HorizontalAlignment.Left; ToolTipService.SetToolTip(name, BoundedViewText(p.Name)); title.Children.Add(name); title.HorizontalAlignment = HorizontalAlignment.Left;
        if (!calendar && !string.IsNullOrEmpty(meta)) { var detail = ViewText(meta, 12, "MutedTextBrush"); Grid.SetColumn(detail, 1); title.Children.Add(detail); }
        Grid.SetColumn(title, 1); grid.Children.Add(title);
        var filter = ViewGroupFilter(p); filter.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(filter, 2); grid.Children.Add(filter);
        if (calendar && !string.IsNullOrEmpty(meta)) { grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); var badge = ViewBorder(ViewText(meta, 11, "MutedTextBrush"), "SubtleBackgroundBrush", new Thickness(8, 2, 8, 2), new Thickness(0), 4); badge.VerticalAlignment = VerticalAlignment.Center; Grid.SetColumn(badge, 3); grid.Children.Add(badge); }
        return ViewBorder(grid, "AppBackgroundBrush", new Thickness(24, 0, 24, 0), new Thickness(0, 0, 0, 1));
    }

    private FrameworkElement ViewDoubleRule(UIElement child, string color = "BorderStrongBrush")
    {
        var grid = new Grid(); grid.Children.Add(child);
        var rule = new Grid { Height = 3, VerticalAlignment = VerticalAlignment.Bottom, IsHitTestVisible = false, UseLayoutRounding = true };
        rule.Children.Add(new Border { Height = 1, Background = ThemeBrush(color), VerticalAlignment = VerticalAlignment.Top });
        rule.Children.Add(new Border { Height = 1, Background = ThemeBrush(color), VerticalAlignment = VerticalAlignment.Bottom });
        ViewIdentity(rule, "DoubleRule", "双线分隔"); grid.Children.Add(rule); return grid;
    }

    private FrameworkElement ViewSectionHeading(string text, FrameworkElement? controls = null)
    {
        var grid = new Grid { MinHeight = 38, Padding = new Thickness(0, 3, 0, 12), ColumnSpacing = 9 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) }); grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new Border { Width = 3, Height = 16, Background = ThemeBrush("AccentBrush"), VerticalAlignment = VerticalAlignment.Center });
        var title = ViewText(text, 16, bold: true, serif: true); Grid.SetColumn(title, 1); grid.Children.Add(title); if (controls is not null) { Grid.SetColumn(controls, 2); grid.Children.Add(controls); } return ViewDoubleRule(grid, "BorderBrush");
    }

    private void RenderDashboard()
    {
        var generation = _contentGeneration; var projectId = _selectedId;
        var body = Column(22); body.Padding = new Thickness(30, 25, 30, 30); var query = "";
        var projects = _projects.Where(p => !p.Archived).ToList(); var tasks = projects.SelectMany(p => ActiveTasks(p).Select(t => (Project: p, Task: t))).ToList(); var done = tasks.Count(x => IsDone(x.Project, x.Task));
        var banner = new Grid { MinHeight = 86, Padding = new Thickness(0, 6, 0, 20) }; banner.ColumnDefinitions.Add(new ColumnDefinition()); banner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        banner.Children.Add(new Border { Child = ViewText("项目总览", 25, bold: true, serif: true), BorderThickness = new Thickness(3, 0, 0, 0), BorderBrush = ThemeBrush("AccentBrush"), Padding = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var metrics = new Grid(); var values = new[] { ("总任务", tasks.Count.ToString("N0"), "PrimaryTextBrush"), ("进行中", tasks.Count(x => Status(x.Project, x.Task)?.Category == "active").ToString("N0"), "AccentBrush"),
            ("已完成", done.ToString("N0"), "SuccessBrush"), ("进度", tasks.Count == 0 ? "0%" : $"{Math.Round(done * 100.0 / tasks.Count)}%", "SuccessBrush") };
        for (var i = 0; i < values.Length; i++) { metrics.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) }); var stack = Column(2); var label = ViewText(values[i].Item1, 10, "MutedTextBrush"); label.HorizontalAlignment = HorizontalAlignment.Center; var value = ViewText(values[i].Item2, 16, values[i].Item3, true, true); value.HorizontalAlignment = HorizontalAlignment.Center; stack.Children.Add(label); stack.Children.Add(value); var segment = ViewBorder(stack, padding: new Thickness(12, 7, 12, 7), border: new Thickness(i == 0 ? 0 : 1, 0, 0, 0)); Grid.SetColumn(segment, i); metrics.Children.Add(segment); }
        var metricBorder = ViewBorder(metrics); metricBorder.Height = 60; Grid.SetColumn(metricBorder, 1); banner.Children.Add(metricBorder); body.Children.Add(ViewDoubleRule(banner));
        var columns = new Grid(); columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) }); columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var projectPanel = Column(12); projectPanel.Margin = new Thickness(0, 0, 24, 0); var sortControls = Row(5);
        var sort = ViewCombo(new[] { "更新时间", "任务数量", "任务进度" }, _dashboardSort, 104, "项目排序依据"); ViewIdentity(sort, "DashboardSort"); sort.Height = sort.MinHeight = 28;
        sort.SelectionChanged += (_, _) =>
        {
            if (!IsCurrentViewEvent(sort, generation, projectId)) return;
            _dashboardSort = sort.SelectedIndex;
            QueueViewRefresh(sort, generation, projectId, renderShell: false, "DashboardSort");
        }; sortControls.Children.Add(sort);
        var direction = ViewIcon(_dashboardDescending ? "\uE70D" : "\uE70E", _dashboardDescending ? "倒序" : "正序", () => { _dashboardDescending = !_dashboardDescending; RenderContent(); FocusUiElement("DashboardSortDirection"); return Task.CompletedTask; }); ViewIdentity(direction, "DashboardSortDirection"); direction.BorderThickness = new Thickness(1); direction.Background = ThemeBrush("CardBackgroundBrush"); direction.CornerRadius = new CornerRadius(3); sortControls.Children.Add(direction); projectPanel.Children.Add(ViewSectionHeading("所有项目", sortControls));
        var matching = projects.Where(p => query.Length == 0 || (p.Name + p.Description).Contains(query, StringComparison.OrdinalIgnoreCase) || p.Tasks.Any(t => (t.Title + t.Description).Contains(query, StringComparison.OrdinalIgnoreCase))).ToList();
        var projectStatistics = projects.ToDictionary(p => p.Id, ProjectPresentation.ActiveStatistics);
        double SortValue(Project p) => _dashboardSort switch { 1 => projectStatistics[p.Id].TaskCount, 2 => projectStatistics[p.Id].CompletionRate, _ => DateTimeOffset.TryParse(p.UpdatedAt, out var date) ? date.ToUnixTimeSeconds() : 0 };
        var ordered = (_dashboardDescending ? matching.OrderByDescending(SortValue) : matching.OrderBy(SortValue)).ThenBy(p => p.Name);
        var cards = new Grid { ColumnSpacing = 12, RowSpacing = 12, Padding = new Thickness(0, 2, 0, 0) }; var cardItems = new List<Button>();
        foreach (var p in ordered)
        {
            var stack = Column(8); var cardTitle = new Grid { ColumnSpacing = 8 }; cardTitle.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); cardTitle.ColumnDefinitions.Add(new ColumnDefinition()); cardTitle.Children.Add(ViewDot(ColorBrush(p.Color), 9, true)); var name = ViewText(SingleLinePreview(p.Name), 14, bold: true, serif: true); name.MaxLines = 1; Grid.SetColumn(name, 1); cardTitle.Children.Add(name); stack.Children.Add(cardTitle);
            if (p.Description.Length > 0) stack.Children.Add(ViewText(p.Description, 11, "MutedTextBrush"));
            var activeStats = projectStatistics[p.Id]; var pd = activeStats.CompletedCount; var percent = Math.Round(activeStats.CompletionRate);
            var stats = new Grid(); stats.ColumnDefinitions.Add(new ColumnDefinition()); stats.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); stats.Children.Add(ViewText($"{activeStats.TaskCount:N0} 任务    {pd:N0} 完成", 11, "SecondaryTextBrush"));
            if (!string.IsNullOrEmpty(p.EndDate)) { var due = ViewText("截止 " + p.EndDate, 10, "MutedTextBrush"); Grid.SetColumn(due, 1); stats.Children.Add(due); } stack.Children.Add(stats);
            var progress = new Grid { Height = 4, Background = ThemeBrush("PaperDeepBrush") }; progress.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(percent, GridUnitType.Star) }); progress.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100 - percent, GridUnitType.Star) }); progress.Children.Add(new Border { Background = ColorBrush(p.Color) }); stack.Children.Add(progress);
            var percentLabel = ViewText($"{percent}%", 11, "MutedTextBrush"); percentLabel.HorizontalAlignment = HorizontalAlignment.Right;
            ToolTipService.SetToolTip(percentLabel, $"未归档任务组：{pd:N0} / {activeStats.TaskCount:N0} 个任务已完成；按此进度排序"); stack.Children.Add(percentLabel);
            var card = ViewButton("", () => { _selectedId = p.Id; _groupId = null; Render(); return Task.CompletedTask; }); card.Content = stack; card.MinHeight = p.Description.Length > 0 ? 128 : 106; card.Padding = new Thickness(16, 15, 16, 13); card.HorizontalContentAlignment = HorizontalAlignment.Stretch; card.HorizontalAlignment = HorizontalAlignment.Stretch; card.CornerRadius = new CornerRadius(3);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(card, $"打开项目 {BoundedViewText(p.Name)}"); cards.Children.Add(card); cardItems.Add(card);
        }
        int cardColumns = 0;
        void ArrangeCards(double width) { var count = Math.Max(1, (int)Math.Floor((width + 12) / 252)); if (count == cardColumns) return; cardColumns = count; cards.ColumnDefinitions.Clear(); cards.RowDefinitions.Clear(); for (var i = 0; i < count; i++) cards.ColumnDefinitions.Add(new ColumnDefinition()); for (var i = 0; i < cardItems.Count; i++) { if (i % count == 0) cards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); Grid.SetRow(cardItems[i], i / count); Grid.SetColumn(cardItems[i], i % count); } }
        ArrangeCards(Math.Max(240, (ContentHost.ActualWidth - 84) * 2 / 3)); cards.SizeChanged += (_, e) => ArrangeCards(e.NewSize.Width); projectPanel.Children.Add(cards);
        if (projects.Count == 0) { var empty = Column(14); empty.Padding = new Thickness(20, 42, 20, 42); empty.HorizontalAlignment = HorizontalAlignment.Center; empty.Children.Add(ViewText("暂无项目", 12, "MutedTextBrush")); empty.Children.Add(ViewButton("创建第一个项目", () => EditProjectAsync(), true)); projectPanel.Children.Add(empty); } columns.Children.Add(projectPanel);
        var upcoming = Column(12); upcoming.Children.Add(ViewSectionHeading(query.Length > 0 ? "搜索结果" : "未来 7 天到期")); var upcomingList = Column(6); upcoming.Children.Add(upcomingList); var today = DateOnly.FromDateTime(DateTime.Today);
        var matches = tasks.Where(x => query.Length > 0 ? (x.Task.Title + x.Task.Description + string.Join(" ", x.Task.Tags)).Contains(query, StringComparison.OrdinalIgnoreCase) : !IsTaskClosed(x.Project, x.Task) && DateOnly.TryParse(x.Task.DueDate, out var date) && date >= today && date <= today.AddDays(7)).OrderBy(x => PriorityOrder(x.Task.Priority)).ThenBy(x => x.Task.DueDate).ToList();
        if (matches.Count > 100)
        {
            var summary = ViewText($"共 {matches.Count:N0} 项，按优先级显示前 100 项", 11, "MutedTextBrush");
            summary.TextWrapping = TextWrapping.Wrap;
            ViewIdentity(summary, "DashboardResultLimit", summary.Text);
            upcoming.Children.Insert(1, summary);
        }
        foreach (var (p, task) in matches.Take(100))
        {
            var row = new Grid { ColumnSpacing = 8 }; row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) }); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); row.Children.Add(ViewDot(ColorBrush(PriorityColor(task.Priority)), 8, true));
            var text = Column(0); text.Children.Add(ViewText(task.Title, 13)); text.Children.Add(ViewText($"{p.Name} · {p.TaskGroups.FirstOrDefault(g => g.Id == task.TaskGroupId)?.Name}", 11, "MutedTextBrush")); Grid.SetColumn(text, 1); row.Children.Add(text);
            var right = Column(0); right.HorizontalAlignment = HorizontalAlignment.Right; var days = DateOnly.TryParse(task.DueDate, out var due) ? due.DayNumber - today.DayNumber : -1;
            var dueLabel = ViewText(days switch { 0 => "今天", 1 => "明天", 2 => "后天", _ => task.DueDate ?? "" }, 12, days == 0 ? "DangerBrush" : "SecondaryTextBrush"); dueLabel.HorizontalAlignment = HorizontalAlignment.Right; right.Children.Add(dueLabel); var priorityLabel = ViewText(PriorityText(task.Priority)[..1], 10, "MutedTextBrush"); priorityLabel.HorizontalAlignment = HorizontalAlignment.Right; right.Children.Add(priorityLabel); Grid.SetColumn(right, 2); row.Children.Add(right);
            var button = ViewButton("", () => EditTaskAsync(task.Id, p.Id)); button.Content = row; button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Stretch; button.Padding = new Thickness(12, 10, 12, 10); button.BorderThickness = new Thickness(0); ViewTaskIdentity(button, p, task);
            var holder = new Grid(); holder.Children.Add(ViewBorder(button, radius: 2)); holder.Children.Add(new Border { Width = 3, Background = ColorBrush(p.Color), HorizontalAlignment = HorizontalAlignment.Left }); upcomingList.Children.Add(holder);
        }
        if (matches.Count == 0) { var empty = ViewText(query.Length > 0 ? "没有匹配的任务" : "未来 7 天没有到期的任务", 12, "MutedTextBrush"); empty.Margin = new Thickness(0, 32, 0, 32); empty.HorizontalAlignment = HorizontalAlignment.Center; upcomingList.Children.Add(empty); }
        var upcomingBorder = ViewBorder(upcoming, "AppBackgroundBrush", new Thickness(24, 0, 0, 0), new Thickness(1, 0, 0, 0)); upcomingBorder.BorderBrush = ThemeBrush("BorderStrongBrush"); Grid.SetColumn(upcomingBorder, 1); columns.Children.Add(upcomingBorder); body.Children.Add(columns);
        var dashboardMode = -1;
        void ArrangeDashboard(double width)
        {
            var compact = width < 690; var mode = compact ? 1 : 0; if (mode == dashboardMode) return; dashboardMode = mode;
            columns.ColumnDefinitions.Clear(); columns.RowDefinitions.Clear(); banner.ColumnDefinitions.Clear(); banner.RowDefinitions.Clear();
            if (compact)
            {
                columns.ColumnDefinitions.Add(new ColumnDefinition()); columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); columns.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                Grid.SetColumn(upcomingBorder, 0); Grid.SetRow(upcomingBorder, 1); upcomingBorder.Padding = new Thickness(0, 22, 0, 0); upcomingBorder.BorderThickness = new Thickness(0); projectPanel.Margin = new Thickness(0);
                banner.ColumnDefinitions.Add(new ColumnDefinition()); banner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); banner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); banner.RowSpacing = 16;
                Grid.SetColumn(metricBorder, 0); Grid.SetRow(metricBorder, 1); foreach (var column in metrics.ColumnDefinitions) column.Width = new GridLength(1, GridUnitType.Star);
            }
            else
            {
                columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) }); columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                Grid.SetColumn(upcomingBorder, 1); Grid.SetRow(upcomingBorder, 0); upcomingBorder.Padding = new Thickness(24, 0, 0, 0); upcomingBorder.BorderThickness = new Thickness(1, 0, 0, 0); projectPanel.Margin = new Thickness(0, 0, 24, 0);
                banner.ColumnDefinitions.Add(new ColumnDefinition()); banner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); Grid.SetColumn(metricBorder, 1); Grid.SetRow(metricBorder, 0); foreach (var column in metrics.ColumnDefinitions) column.Width = new GridLength(96);
            }
        }
        ArrangeDashboard(Math.Max(300, ContentHost.ActualWidth - 60)); columns.SizeChanged += (_, e) => ArrangeDashboard(e.NewSize.Width);
        ContentHost.Children.Add(new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
    }

    private UIElement BuildKanban(Project p)
    {
        var group = CurrentGroup ?? p.TaskGroups.FirstOrDefault(g => g.Id == p.DefaultTaskGroupId); if (group is null) return ViewText("请先创建任务组。");
        var layout = new Grid(); layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(256) }); layout.ColumnDefinitions.Add(new ColumnDefinition());
        var groupPanel = new Grid(); groupPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(49) }); groupPanel.RowDefinitions.Add(new RowDefinition());
        var groupBorder = ViewBorder(groupPanel, "SidebarBackgroundBrush", border: new Thickness(0, 0, 1, 0));
        var drawerOpen = false; var scrim = ViewButton("关闭任务组", null);
        void CloseGroupDrawer()
        {
            drawerOpen = false; groupPanel.TabFocusNavigation = Microsoft.UI.Xaml.Input.KeyboardNavigationMode.Local;
            groupBorder.Visibility = Visibility.Collapsed; scrim.Visibility = Visibility.Collapsed;
            FocusUiElement("KanbanGroupDrawerToggle");
        }
        scrim.Click += (_, _) => CloseGroupDrawer();
        var groupHeader = new Grid { Padding = new Thickness(14, 0, 8, 0) }; groupHeader.Children.Add(ViewText("任务组", 12, "SecondaryTextBrush", true));
        var createGroup = ViewIcon("\uE710", "新建任务组", () => CreateTaskGroupAsync(p.Id), 30); ViewIdentity(createGroup, "TaskGroupCreate", "新建任务组");
        createGroup.HorizontalAlignment = HorizontalAlignment.Right; groupHeader.Children.Add(createGroup); groupPanel.Children.Add(ViewBorder(groupHeader, "SidebarBackgroundBrush", border: new Thickness(0, 0, 0, 1)));
        var groups = Column(2); groups.Padding = new Thickness(8);
        foreach (var g in p.TaskGroups.OrderBy(g => g.Archived).ThenBy(g => g.SortOrder))
        {
            var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            var button = ViewButton(g.Name, () =>
            {
                if (g.Id == group.Id) { if (Root.ActualWidth < 1200) CloseGroupDrawer(); return Task.CompletedTask; }
                _groupId = g.Id; Render(); if (Root.ActualWidth < 1200) FocusUiElement("KanbanGroupDrawerToggle"); return Task.CompletedTask;
            }, height: 38); button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Left; button.Padding = new Thickness(9, 7, 8, 7);
            button.BorderThickness = new Thickness(3, 0, 0, 0); button.BorderBrush = g.Id == p.DefaultTaskGroupId ? ThemeBrush("SuccessBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent); button.Background = g.Id == group.Id ? ThemeBrush("AccentLightBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            button.Foreground = ThemeBrush(g.Id == group.Id ? "AccentTextBrush" : "SecondaryTextBrush"); if (g.Id == group.Id) PreserveButtonPalette(button); button.FontSize = 12; button.IsEnabled = !g.Archived; ViewIdentity(button, "KanbanGroup:" + g.Id, g.Name + (g.Archived ? "，已归档" : "")); Microsoft.UI.Xaml.Automation.AutomationProperties.SetItemStatus(button, g.Id == group.Id ? "已选中" : "未选中"); ToolTipService.SetToolTip(button, g.Archived ? g.Name + " · 请先恢复任务组" : g.Name); row.Children.Add(button);
            var more = ViewIcon("\uE712", $"管理任务组 {g.Name}"); ViewIdentity(more, "TaskGroupMore:" + g.Id); var menu = ViewMenu();
            AddMenu(menu, "重命名", async () => { var name = await PromptAsync("重命名任务组", g.Name); if (name is not null) await ChangeAsync(p.Id, draft => _service.RenameTaskGroup(draft, g.Id, name)); }, "TaskGroupMenu:rename:" + g.Id);
            if (!g.Archived)
            {
                AddMenu(menu, g.Id == p.DefaultTaskGroupId ? "当前默认任务组" : "设为默认任务组", () => ChangeAsync(p.Id, draft => _service.SetDefaultTaskGroup(draft, g.Id)), "TaskGroupMenu:default:" + g.Id, g.Id != p.DefaultTaskGroupId);
                var archive = AddMenu(menu, "归档任务组", async () =>
                {
                    await ChangeAsync(p.Id, draft => _service.ArchiveTaskGroup(draft, g.Id), "任务组已归档");
                    if (_groupId == g.Id) { _groupId = Current?.DefaultTaskGroupId; Render(); }
                }, "TaskGroupMenu:archive:" + g.Id, p.TaskGroups.Count(item => !item.Archived) > 1);
                if (!archive.IsEnabled) ToolTipService.SetToolTip(archive, "项目至少保留一个未归档任务组");
            }
            else AddMenu(menu, "恢复任务组", () => ChangeAsync(p.Id, draft => _service.RestoreTaskGroup(draft, g.Id), "任务组已恢复"), "TaskGroupMenu:restore:" + g.Id);
            menu.Items.Add(new MenuFlyoutSeparator()); AddMenu(menu, "管理全部任务组", ManageGroupsAsync, "TaskGroupMenu:manage:" + g.Id); CaptureMenuOrigin(menu, "TaskGroupMore:" + g.Id); more.Flyout = menu; Grid.SetColumn(more, 1); row.Children.Add(more); groups.Children.Add(row);
        }
        var groupScroll = new ScrollViewer { Content = groups }; Grid.SetRow(groupScroll, 1); groupPanel.Children.Add(groupScroll); ViewIdentity(groupBorder, "CompactGroupDrawer", "任务组面板"); layout.Children.Add(groupBorder);
        var boardArea = new Grid(); boardArea.RowDefinitions.Add(new RowDefinition { Height = new GridLength(49) }); boardArea.RowDefinitions.Add(new RowDefinition()); Grid.SetColumn(boardArea, 1); layout.Children.Add(boardArea);
        var header = new Grid { ColumnSpacing = 9, Padding = new Thickness(14, 7, 14, 7) }; header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var leading = Row(9);
        scrim.Content = null; scrim.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(90, 0, 0, 0)); scrim.BorderThickness = new Thickness(0); scrim.CornerRadius = new CornerRadius(0);
        scrim.HorizontalAlignment = HorizontalAlignment.Stretch; scrim.VerticalAlignment = VerticalAlignment.Stretch; scrim.Visibility = Visibility.Collapsed;
        Grid.SetColumnSpan(scrim, 2); Canvas.SetZIndex(scrim, 10); layout.Children.Add(scrim); Canvas.SetZIndex(groupBorder, 20);
        var groupToggle = ViewIcon("\uE700", "选择任务组", () => { drawerOpen = true; groupPanel.TabFocusNavigation = Microsoft.UI.Xaml.Input.KeyboardNavigationMode.Cycle; groupBorder.Visibility = Visibility.Visible; scrim.Visibility = Visibility.Visible; FocusUiElement("KanbanGroup:" + group.Id); return Task.CompletedTask; }, 32); ViewIdentity(groupToggle, "KanbanGroupDrawerToggle", "选择任务组", "展开任务组列表；按 Escape 关闭");
        ViewIdentity(scrim, "KanbanGroupDrawerClose", "关闭任务组", "按 Escape 关闭任务组面板");
        void HandleDrawerKeys(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs args)
        {
            if (!drawerOpen) return;
            if (args.Key == Windows.System.VirtualKey.Escape)
            {
                CloseGroupDrawer(); args.Handled = true; return;
            }
            if (args.Key != Windows.System.VirtualKey.Tab) return;
            var targets = ShellDescendants<Button>(groupBorder).Where(button => button.IsEnabled && button.IsTabStop && button.Visibility == Visibility.Visible).ToList(); targets.Add(scrim);
            var currentFocus = FocusManager.GetFocusedElement(Root.XamlRoot); var currentIndex = targets.FindIndex(button => ReferenceEquals(button, currentFocus));
            var backwards = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            var nextIndex = currentIndex < 0 ? backwards ? targets.Count - 1 : 0 : (currentIndex + (backwards ? -1 : 1) + targets.Count) % targets.Count;
            targets[nextIndex].Focus(FocusState.Keyboard); targets[nextIndex].StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false }); args.Handled = true;
        }
        groupPanel.KeyDown += HandleDrawerKeys; scrim.KeyDown += HandleDrawerKeys;
        leading.Children.Add(groupToggle); leading.Children.Add(ViewDot(ColorBrush(p.Color))); header.Children.Add(leading);
        var heading = ViewText(group.Name + (group.Archived ? " · 已归档" : ""), 14, bold: true); Grid.SetColumn(heading, 1); header.Children.Add(heading);
        var newStatus = ViewButton("新建状态", async () => { var name = await PromptAsync("新增状态"); if (name is not null) await ChangeAsync(p.Id, draft => _service.CreateTaskStatus(draft, group.Id, name)); }); ViewIdentity(newStatus, "TaskStatusCreate", "新建状态"); newStatus.IsEnabled = !group.Archived; Grid.SetColumn(newStatus, 2); header.Children.Add(newStatus); boardArea.Children.Add(ViewBorder(header, border: new Thickness(0, 0, 0, 1)));
        var board = new Grid { ColumnSpacing = 12, Padding = new Thickness(14) }; var statuses = group.Statuses.OrderBy(s => s.SortOrder).ToList();
        for (var i = 0; i < statuses.Count; i++)
        {
            var status = statuses[i]; board.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(272) }); var column = new Grid { Background = ThemeBrush("SubtleBackgroundBrush") };
            column.RowDefinitions.Add(new RowDefinition { Height = new GridLength(40) }); column.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); column.RowDefinitions.Add(new RowDefinition());
            var colHeader = new Grid { ColumnSpacing = 8, Padding = new Thickness(10, 0, 6, 0) }; colHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); colHeader.ColumnDefinitions.Add(new ColumnDefinition()); colHeader.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            colHeader.Children.Add(ViewDot(ColorBrush(status.Color))); var name = ViewText(status.Name, 12, bold: true); Grid.SetColumn(name, 1); colHeader.Children.Add(name);
            async Task RenameStatus() { var renamed = await PromptAsync("重命名状态", status.Name); if (renamed is not null) await ChangeAsync(p.Id, draft => _service.UpdateTaskStatusDefinition(draft, group.Id, status.Id, renamed, status.Color, status.Category)); }
            name.DoubleTapped += async (_, _) => await GuardAsync(RenameStatus); var more = ViewIcon("\uE712", $"管理状态 {status.Name}"); ViewIdentity(more, "TaskStatusMore:" + status.Id); var menu = ViewMenu(); AddMenu(menu, "重命名", RenameStatus, "TaskStatusMenu:rename:" + status.Id);
            AddMenu(menu, status.Id == group.CompletionStatusId ? "当前完成列" : "设为完成列", () => ChangeAsync(p.Id, draft => _service.SetGroupCompletionStatus(draft, group.Id, status.Id)), "TaskStatusMenu:complete:" + status.Id, status.Id != group.CompletionStatusId);
            AddMenu(menu, "复制状态任务", async () =>
            {
                var count = p.Tasks.Count(task => task.TaskGroupId == group.Id && task.StatusId == status.Id);
                if (count == 0) { ShowMessage("当前状态没有可复制的任务。", InfoBarSeverity.Informational); return; }
                await ChangeAsync(p.Id, draft => _service.CopyStatusTasks(draft, group.Id, status.Id), $"已复制 {count} 个任务");
            }, "TaskStatusMenu:copy:" + status.Id);
            menu.Items.Add(new MenuFlyoutSeparator());
            AddMenu(menu, "向左移动", () => ChangeAsync(p.Id, draft => ReorderStatus(draft, group.Id, status.Id, -1)), "TaskStatusMenu:left:" + status.Id, i > 0);
            AddMenu(menu, "向右移动", () => ChangeAsync(p.Id, draft => ReorderStatus(draft, group.Id, status.Id, 1)), "TaskStatusMenu:right:" + status.Id, i < statuses.Count - 1);
            CaptureMenuOrigin(menu, "TaskStatusMore:" + status.Id); more.Flyout = menu; Grid.SetColumn(more, 2); colHeader.Children.Add(more); column.Children.Add(ViewBorder(colHeader, "SubtleBackgroundBrush", border: new Thickness(0, 0, 0, 1)));
            var add = ViewQuickAdd(p, group.Id, status.Id, false); add.Margin = new Thickness(8); Grid.SetRow(add, 1); column.Children.Add(add);
            var list = MakeTaskList(p, p.Tasks.Where(t => t.TaskGroupId == group.Id && t.StatusId == status.Id), true); list.Padding = new Thickness(8, 0, 8, 8); EnableTaskDrag(list, p, status.Id); Grid.SetRow(list, 2); column.Children.Add(list);
            var outline = ViewBorder(column, "SubtleBackgroundBrush", border: new Thickness(0, 3, 0, 0)); outline.BorderBrush = ThemeBrush("BorderStrongBrush"); ViewIdentity(outline, "TaskStatusOutline:" + status.Id); Grid.SetColumn(outline, i); board.Children.Add(outline);
        }
        var scroll = new ScrollViewer { Content = board, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollMode = ScrollMode.Disabled };
        Grid.SetRow(scroll, 1); boardArea.Children.Add(scroll);
        void ArrangeGroups()
        {
            var narrow = Root.ActualWidth > 0 && Root.ActualWidth < 1200;
            groupPanel.TabFocusNavigation = narrow && drawerOpen ? Microsoft.UI.Xaml.Input.KeyboardNavigationMode.Cycle : Microsoft.UI.Xaml.Input.KeyboardNavigationMode.Local;
            layout.ColumnDefinitions[0].Width = new GridLength(narrow ? 0 : 256); groupToggle.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
            groupBorder.Width = narrow ? 256 : double.NaN; groupBorder.HorizontalAlignment = narrow ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
            Grid.SetColumnSpan(groupBorder, narrow ? 2 : 1); groupBorder.Visibility = !narrow || drawerOpen ? Visibility.Visible : Visibility.Collapsed;
            scrim.Visibility = narrow && drawerOpen ? Visibility.Visible : Visibility.Collapsed;
        }
        ArrangeGroups(); layout.SizeChanged += (_, _) => ArrangeGroups(); return layout;
    }

    private Grid ViewQuickAdd(Project p, string? groupId, string? statusId, bool labeled)
    {
        var row = new Grid { ColumnSpacing = labeled ? 7 : 6 }; row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var creationGroupId = groupId; var actionColumn = 1; ComboBox? creationGroupSelector = null;
        if (labeled && string.IsNullOrEmpty(groupId))
        {
            var groups = p.TaskGroups.Where(g => !g.Archived).OrderBy(g => g.SortOrder).ToList();
            var initial = Math.Max(0, groups.FindIndex(g => g.Id == p.DefaultTaskGroupId)); creationGroupId = groups.ElementAtOrDefault(initial)?.Id;
            var selector = ViewCombo(groups.Select(g => g.Name), initial, 100, "新建任务所在分组"); selector.Height = selector.MinHeight = 34; creationGroupSelector = selector;
            selector.SelectionChanged += (_, _) => creationGroupId = groups.ElementAtOrDefault(selector.SelectedIndex)?.Id;
            Grid.SetColumn(selector, 1); row.Children.Add(selector); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); actionColumn = 2;
        }
        var input = new TextBox { PlaceholderText = labeled ? "添加任务" : "添加新任务...", FontFamily = UiFont, FontSize = 12, MinHeight = labeled ? 34 : 31, Height = labeled ? 34 : 31, Padding = new Thickness(8, 4, 8, 4), CornerRadius = new CornerRadius(0), Background = ThemeBrush("CardBackgroundBrush"), BorderBrush = ThemeBrush("BorderBrush") };
        var inputId = $"TaskQuickAdd:{groupId ?? "all"}:{statusId ?? "default"}"; ViewIdentity(input, inputId, "添加任务标题", "最多 128 个可见字符；输入后按 Enter 添加任务");
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var feedback = ViewText("", 10, "MutedTextBrush"); feedback.TextWrapping = TextWrapping.Wrap; feedback.Margin = new Thickness(1, 3, 0, 0);
        feedback.Visibility = Visibility.Collapsed; ViewIdentity(feedback, inputId + ":Feedback", "标题长度与校验提示"); Grid.SetRow(feedback, 1); Grid.SetColumnSpan(feedback, actionColumn + 1); row.Children.Add(feedback);
        var saving = false; var composing = false; Button? add = null;
        bool CanCreate() => !saving && !string.IsNullOrWhiteSpace(input.Text) && p.TaskGroups.Any(g => g.Id == (creationGroupId ?? p.DefaultTaskGroupId) && !g.Archived);
        void RefreshAddState()
        {
            input.IsEnabled = !saving && p.TaskGroups.Any(g => g.Id == (creationGroupId ?? p.DefaultTaskGroupId) && !g.Archived);
            if (creationGroupSelector is not null) creationGroupSelector.IsEnabled = !saving;
            if (add is not null) add.IsEnabled = CanCreate();
            if (composing) return;
            var text = input.Text ?? ""; var count = TextRules.CountGraphemes(text.Trim());
            feedback.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            try
            {
                if (text.Length > 0) TextRules.RequireTaskTitle(text);
                feedback.Text = $"{count} / {TextRules.TaskTitleGraphemeLimit} 个可见字符";
                feedback.Foreground = ThemeBrush("MutedTextBrush");
            }
            catch (ArgumentException error)
            {
                feedback.Text = error.Message; feedback.Foreground = ThemeBrush("DangerTextBrush");
                if (add is not null) add.IsEnabled = false;
            }
        }
        async Task Create()
        {
            if (_busy || composing || !CanCreate()) return;
            string title;
            try { title = TextRules.RequireTaskTitle(input.Text); }
            catch (ArgumentException) { RefreshAddState(); input.Focus(FocusState.Keyboard); return; }
            var saved = false;
            saving = true; RefreshAddState();
            try
            {
                var creationStatusId = labeled && !string.IsNullOrEmpty(groupId) && p.TaskGroups.Single(g => g.Id == groupId).Statuses.Any(s => s.Id == _listStatusFilter)
                    ? _listStatusFilter : statusId;
                await ChangeAsync(p.Id, draft => _service.CreateTask(draft, title, creationGroupId, creationStatusId)); saved = true;
            }
            finally
            {
                saving = false; RefreshAddState();
                if (saved) FocusUiElement(inputId);
            }
        }
        input.TextCompositionStarted += (_, _) => composing = true;
        input.TextCompositionEnded += (_, _) => { composing = false; RefreshAddState(); };
        input.KeyDown += async (_, args) => { if (args.Key == Windows.System.VirtualKey.Enter && !composing) { args.Handled = true; await GuardAsync(Create); } }; row.Children.Add(input);
        add = labeled ? ViewButton("添加", Create, true, 34) : ViewIcon("\uE710", "添加任务", Create, 30); ViewIdentity(add, inputId + ":Submit"); Grid.SetColumn(add, actionColumn); row.Children.Add(add);
        input.TextChanged += (_, _) => RefreshAddState(); if (creationGroupSelector is not null) creationGroupSelector.SelectionChanged += (_, _) => RefreshAddState(); RefreshAddState(); return row;
    }

    private MenuFlyoutItem AddMenu(MenuFlyout menu, string text, Func<Task> action, string? id = null, bool enabled = true)
    { var item = ViewMenuItem(text, action, id, enabled); menu.Items.Add(item); return item; }
    private void ReorderStatus(Project p, string groupId, string statusId, int offset)
    {
        var group = p.TaskGroups.Single(g => g.Id == groupId); var items = group.Statuses.OrderBy(s => s.SortOrder).ToList(); var index = items.FindIndex(s => s.Id == statusId); var target = Math.Clamp(index + offset, 0, items.Count - 1);
        var value = items[index]; items.RemoveAt(index); items.Insert(target, value); _service.ReorderTaskStatuses(p, groupId, items.Select(s => s.Id).ToArray());
    }

    private UIElement BuildTaskList(Project p)
    {
        var layout = new Grid { Padding = new Thickness(22, 18, 22, 18), RowSpacing = 10 }; for (var i = 0; i < 3; i++) layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition());
        var title = Row(10); title.Margin = new Thickness(0, 0, 0, 4); title.Children.Add(ViewDot(ColorBrush(p.Color))); var titleText = Column(0); var heading = ViewText(SingleLinePreview(p.Name), 17, bold: true, serif: true); heading.MaxLines = 1; heading.LineHeight = 27.2; heading.LineStackingStrategy = LineStackingStrategy.BlockLineHeight; titleText.Children.Add(heading); var count = ViewText("", 11, "MutedTextBrush"); count.LineHeight = 22.4; count.LineStackingStrategy = LineStackingStrategy.BlockLineHeight; titleText.Children.Add(count); title.Children.Add(titleText); layout.Children.Add(title);
        var create = ViewQuickAdd(p, _groupId, null, true); Grid.SetRow(create, 1); layout.Children.Add(create);
        var host = new Grid { BorderBrush = ThemeBrush("BorderBrush"), BorderThickness = new Thickness(0, 1, 0, 0) }; Grid.SetRow(host, 3); layout.Children.Add(host);
        void Refresh()
        {
            var visible = VisibleTasks(p).Where(t => _listStatusFilter is null || (string.IsNullOrEmpty(_groupId) ? Status(p, t)?.Category : t.StatusId) == _listStatusFilter).Where(t => _listTagFilter is null || t.Tags.Contains(_listTagFilter)).ToList();
            var filtered = !string.IsNullOrEmpty(_groupId) || !string.IsNullOrWhiteSpace(SearchBox.Text) || PrioritySelector.SelectedIndex > 0 || _listStatusFilter is not null || _listTagFilter is not null;
            var total = p.Tasks.Count(t => p.TaskGroups.Any(g => g.Id == t.TaskGroupId && !g.Archived)); count.Text = filtered ? $"显示 {visible.Count} / 全部 {total} 个任务" : $"{visible.Count} 个任务"; Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(count, count.Text);
            ParkFocusBeforeDetach(host);
            ReleaseTaskLists(() => host.Children.Clear(), host.Children.OfType<ListView>());
            if (visible.Count == 0)
            {
                var empty = Column(10); empty.HorizontalAlignment = HorizontalAlignment.Center; empty.VerticalAlignment = VerticalAlignment.Top; empty.Margin = new Thickness(12, 44, 12, 0);
                var message = ViewText(filtered ? "没有匹配的任务" : "还没有任务", 14, "SecondaryTextBrush", true); message.HorizontalAlignment = HorizontalAlignment.Center; empty.Children.Add(message);
                var hint = ViewText(filtered ? "尝试其他关键词，或清除筛选查看全部任务。" : "在上方输入任务标题，按 Enter 开始添加。", 12, "MutedTextBrush"); hint.TextWrapping = TextWrapping.Wrap; hint.TextAlignment = TextAlignment.Center; empty.Children.Add(hint);
                if (filtered)
                {
                    var clear = ViewButton("清除筛选", () => { _groupId = null; _listStatusFilter = null; _listTagFilter = null; SearchBox.Text = ""; var rendering = _rendering; _rendering = true; try { PrioritySelector.SelectedIndex = 0; } finally { _rendering = rendering; } Render(); FocusUiElement("TaskSearch"); return Task.CompletedTask; }); clear.HorizontalAlignment = HorizontalAlignment.Center; ViewIdentity(clear, "ClearTaskFilters"); empty.Children.Add(clear);
                }
                host.Children.Add(empty);
            }
            else { var list = MakeTaskList(p, visible, false); EnableTaskDrag(list, p, null); host.Children.Add(list); }
        }
        var filters = new Grid { ColumnSpacing = 7, RowSpacing = 7 }; for (var i = 0; i < 4; i++) filters.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); filters.ColumnDefinitions.Add(new ColumnDefinition()); filters.Children.Add(ViewGroupFilter(p));
        var statusValues = string.IsNullOrEmpty(_groupId) ? new[] { "todo", "active", "done", "cancelled" }.ToList() : p.TaskGroups.FirstOrDefault(g => g.Id == _groupId)?.Statuses.OrderBy(s => s.SortOrder).Select(s => s.Id).ToList() ?? [];
        var statusNames = string.IsNullOrEmpty(_groupId) ? new[] { "待办", "进行中", "已完成", "已取消" }.ToList() : p.TaskGroups.FirstOrDefault(g => g.Id == _groupId)?.Statuses.OrderBy(s => s.SortOrder).Select(s => s.Name).ToList() ?? [];
        if (_listStatusFilter is not null && !statusValues.Contains(_listStatusFilter)) _listStatusFilter = null;
        var statusCombo = ViewCombo(new[] { "全部状态" }.Concat(statusNames), statusValues.IndexOf(_listStatusFilter ?? "") + 1, 88, "状态筛选"); statusCombo.SelectionChanged += (_, _) => { _listStatusFilter = statusCombo.SelectedIndex <= 0 ? null : statusValues[statusCombo.SelectedIndex - 1]; Refresh(); FocusUiElement("TaskStatusFilter"); }; Grid.SetColumn(statusCombo, 1); filters.Children.Add(statusCombo);
        ViewIdentity(statusCombo, "TaskStatusFilter");
        var priority = ViewPriorityFilter(Refresh); Grid.SetColumn(priority, 2); filters.Children.Add(priority); var tags = p.Tasks.SelectMany(t => t.Tags).Distinct().ToList(); var tagCombo = ViewCombo(new[] { "全部标签" }.Concat(tags), tags.IndexOf(_listTagFilter ?? "") + 1, 88, "标签筛选");
        if (_listTagFilter is not null && !tags.Contains(_listTagFilter)) _listTagFilter = null;
        ViewIdentity(tagCombo, "TaskTagFilter");
        tagCombo.SelectionChanged += (_, _) => { _listTagFilter = tagCombo.SelectedIndex <= 0 ? null : tags[tagCombo.SelectedIndex - 1]; Refresh(); FocusUiElement("TaskTagFilter"); }; Grid.SetColumn(tagCombo, 3); filters.Children.Add(tagCombo); var search = ViewSearch(Refresh); Grid.SetColumn(search, 4); filters.Children.Add(search); Grid.SetRow(filters, 2); layout.Children.Add(filters);
        foreach (var combo in filters.Children.OfType<ComboBox>()) { combo.Height = combo.MinHeight = 34; combo.FontSize = 12; }
        var filterMode = -1;
        void ArrangeFilters(double width)
        {
            var mode = width < 550 ? 2 : width < 740 ? 1 : 0; if (mode == filterMode) return; filterMode = mode;
            filters.ColumnDefinitions.Clear(); filters.RowDefinitions.Clear(); var cols = mode == 2 ? 2 : mode == 1 ? 4 : 5;
            for (var i = 0; i < cols; i++) filters.ColumnDefinitions.Add(new ColumnDefinition { Width = mode == 0 && i < 4 ? GridLength.Auto : new GridLength(1, GridUnitType.Star) });
            for (var i = 0; i < (mode == 2 ? 3 : mode == 1 ? 2 : 1); i++) filters.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var widths = new[] { 120d, 88d, 100d, 88d };
            for (var i = 0; i < 4; i++) { var item = (FrameworkElement)filters.Children[i]; item.Width = mode == 0 ? widths[i] : double.NaN; item.HorizontalAlignment = HorizontalAlignment.Stretch; Grid.SetColumn(item, mode == 2 ? i % 2 : i); Grid.SetRow(item, mode == 2 ? i / 2 : 0); }
            Grid.SetColumn(search, mode == 0 ? 4 : 0); Grid.SetRow(search, mode == 2 ? 2 : mode == 1 ? 1 : 0); Grid.SetColumnSpan(search, mode == 0 ? 1 : cols);
        }
        ArrangeFilters(Math.Max(300, ContentHost.ActualWidth - 44)); filters.SizeChanged += (_, e) => ArrangeFilters(e.NewSize.Width); Refresh(); return layout;
    }

    private ListView MakeTaskList(Project p, IEnumerable<ProjectTask> tasks, bool kanban = true, Func<ProjectTask, UIElement>? renderTask = null)
    {
        var list = new ListView { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(0), Background = null, BorderThickness = new Thickness(0) };
        list.ItemContainerStyle = (Style)Application.Current.Resources["CompactTaskListItemStyle"];
        DataTemplate CreateTemplate() => (DataTemplate)XamlReader.Load($"""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><ContentPresenter MinHeight="{(kanban ? 72 : 58)}" HorizontalAlignment="Stretch" /></DataTemplate>
            """);
        list.ItemTemplate = kanban ? _kanbanTaskTemplate ??= CreateTemplate() : _tableTaskTemplate ??= CreateTemplate();
        var orderedTasks = tasks.ToList(); IReadOnlyList<string>? visibleTaskIds = kanban ? null : orderedTasks.Select(t => t.Id).ToArray();
        var taskById = orderedTasks.ToDictionary(t => t.Id);
        var realizedRows = new Dictionary<Microsoft.UI.Xaml.Controls.Primitives.SelectorItem, TaskRow>();
        var retiring = false;
        void UpdateContainer(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (retiring) { args.Handled = true; return; }
            if (args.InRecycleQueue)
            {
                if (args.ItemContainer.ContentTemplateRoot is ContentPresenter recycled) recycled.Content = null;
                // WinUI may supply Item=null while returning a container to the pool.
                if (realizedRows.Remove(args.ItemContainer, out var previousRow)) previousRow.Content = null;
                if (args.Item is TaskRow recycledRow) recycledRow.Content = null;
                ViewIdentity(args.ItemContainer, "", "", "");
                return;
            }
            if (args.Item is not TaskRow row) return;
            if (realizedRows.TryGetValue(args.ItemContainer, out var oldRow) && !ReferenceEquals(oldRow, row)) oldRow.Content = null;
            realizedRows[args.ItemContainer] = row;
            // Let the virtualizing panel decide which task bodies need controls.
            var task = taskById[row.Id];
            row.Content ??= renderTask is not null ? renderTask(task) : kanban ? ViewKanbanCard(p, task) : ViewTaskListRow(p, task, visibleTaskIds);
            // Populate before measurement; a deferred binding initially measures
            // zero-height rows and makes the panel realize the entire collection.
            if (args.ItemContainer.ContentTemplateRoot is ContentPresenter presenter) presenter.Content = row.Content;
            ViewIdentity(args.ItemContainer, "TaskOpen:" + row.Id, "打开任务：" + row.Title, "按 Enter 打开任务；按 Shift+F10 查看任务操作");
            args.Handled = true;
        }
        list.ContainerContentChanging += UpdateContainer;
        var rows = orderedTasks.Select(t => new TaskRow { Id = t.Id, Title = BoundedViewText(t.Title) }).ToList();
        list.ItemsSource = rows;
        async void ItemClick(object sender, ItemClickEventArgs args)
        {
            if (!retiring && args.ClickedItem is TaskRow row) await GuardAsync(() => EditTaskAsync(row.Id, p.Id));
        }
        void RightTapped(object sender, RightTappedRoutedEventArgs args)
        {
            if (retiring) return;
            var element = args.OriginalSource as DependencyObject; while (element is not null && element is not ListViewItem) element = VisualTreeHelper.GetParent(element); if (element is not ListViewItem { Content: TaskRow row }) return;
            BuildTaskMenu(p, row.Id, visibleTaskIds).ShowAt(list, args.GetPosition(list)); args.Handled = true;
        }
        void KeyDown(object sender, KeyRoutedEventArgs args)
        {
            if (retiring) return;
            var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
            if (args.Key != Windows.System.VirtualKey.Application && !(args.Key == Windows.System.VirtualKey.F10 && shift)) return;
            var element = FocusManager.GetFocusedElement(list.XamlRoot) as DependencyObject;
            while (element is not null && element is not ListViewItem && element != list) element = VisualTreeHelper.GetParent(element);
            if (element is ListViewItem { Content: TaskRow row } item) { BuildTaskMenu(p, row.Id, visibleTaskIds).ShowAt(item); args.Handled = true; }
        }
        list.ItemClick += ItemClick; list.RightTapped += RightTapped; list.KeyDown += KeyDown;
        var cleanupQueued = false; var cleanupComplete = false;
        void ClearManagedReferences()
        {
            foreach (var row in rows) row.Content = null;
            realizedRows.Clear(); taskById.Clear(); orderedTasks.Clear();
            // Do not mutate the collection that was supplied as ItemsSource.
            // WinUI owns native container teardown; no presenter is dismantled here.
        }
        void QueueDetachedCleanup()
        {
            if (cleanupQueued || cleanupComplete) return;
            cleanupQueued = true;
            if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                cleanupQueued = false;
                if (cleanupComplete || list.IsLoaded || BelongsToRoot(list)) return;
                cleanupComplete = true; list.Unloaded -= OnUnloaded;
                try { list.ItemsSource = null; }
                finally { ClearManagedReferences(); }
            }))
            {
                cleanupQueued = false; cleanupComplete = true;
                // A shutting-down dispatcher cannot safely execute native cleanup.
                ClearManagedReferences();
            }
        }
        void OnUnloaded(object sender, RoutedEventArgs args) => QueueDetachedCleanup();
        _taskListCleanup.Add(list, new TaskListLifetime(() =>
        {
            retiring = true;
            list.ContainerContentChanging -= UpdateContainer;
            list.ItemClick -= ItemClick; list.RightTapped -= RightTapped; list.KeyDown -= KeyDown;
            // Subscribe before removal: Unloaded can occur synchronously in Clear.
            list.Unloaded += OnUnloaded;
        }, () =>
        {
            if (!list.IsLoaded && !BelongsToRoot(list)) QueueDetachedCleanup();
        }));
        return list;
    }

    private UIElement ViewKanbanCard(Project p, ProjectTask task)
    {
        var stack = Column(7); var heading = new Grid { ColumnSpacing = 4 }; heading.ColumnDefinitions.Add(new ColumnDefinition()); heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        var title = ViewText(task.Title, 12); title.TextWrapping = TextWrapping.Wrap; title.MaxLines = 3; title.LineHeight = 17.4; if (IsDone(p, task)) title.TextDecorations = TextDecorations.Strikethrough; heading.Children.Add(title);
        var more = ViewTaskMore(p, task); more.VerticalAlignment = VerticalAlignment.Top; Grid.SetColumn(more, 1); heading.Children.Add(more); stack.Children.Add(heading);
        if (!string.IsNullOrEmpty(task.DueDate)) { var date = ViewText(task.DueDate, 11, Overdue(p, task) ? "DangerBrush" : "MutedTextBrush"); date.LineHeight = 16; date.LineStackingStrategy = LineStackingStrategy.BlockLineHeight; stack.Children.Add(date); }
        if (task.Tags.Count > 0) { var tags = Row(4); foreach (var tag in task.Tags.Take(3)) tags.Children.Add(ViewBorder(ViewText(tag, 9, "MutedTextBrush"), padding: new Thickness(4, 1, 4, 1))); stack.Children.Add(tags); }
        var outer = new Grid { Margin = new Thickness(0, 0, 0, 8), MinHeight = 72 }; if (IsTaskClosed(p, task)) title.Foreground = ThemeBrush("MutedTextBrush");
        var outline = ViewBorder(stack, padding: new Thickness(12, 10, 10, 10)); ViewIdentity(outline, "TaskCardOutline:" + task.Id); outer.Children.Add(outline);
        outer.Children.Add(new Border { Width = 3, Background = ColorBrush(PriorityColor(task.Priority)), HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false }); ViewTaskIdentity(outer, p, task); return outer;
    }

    private UIElement ViewTaskListRow(Project p, ProjectTask task, IReadOnlyList<string>? visibleTaskIds = null)
    {
        var row = new Grid { ColumnSpacing = 9, MinHeight = 58, Padding = new Thickness(8, 7, 8, 7) }; foreach (var width in new[] { 18d, 28d, 8d, -1d, 0d, 0d, 60d }) row.ColumnDefinitions.Add(new ColumnDefinition { Width = width < 0 ? new GridLength(1, GridUnitType.Star) : width == 0 ? GridLength.Auto : new GridLength(width) }); row.Children.Add(ViewText("⠿", 15, "MutedTextBrush"));
        var toggle = ViewButton("", () => ChangeAsync(p.Id, draft => _service.ToggleTaskStatus(draft, task.Id)), height: 22); toggle.Width = toggle.Height = toggle.MinWidth = 22; toggle.Padding = new Thickness(0); toggle.CornerRadius = new CornerRadius(0); toggle.BorderBrush = ThemeBrush(IsDone(p, task) ? "SuccessBrush" : "BorderStrongBrush"); toggle.Background = IsDone(p, task) ? ThemeBrush("SuccessBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        if (IsDone(p, task)) toggle.Content = new FontIcon { Glyph = "\uE73E", FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 12, Foreground = ContrastForeground(((SolidColorBrush)ThemeBrush("SuccessBrush")).Color) }; ViewIdentity(toggle, "TaskToggle:" + task.Id, (IsTaskClosed(p, task) ? "重新打开任务：" : "完成任务：") + task.Title); ToolTipService.SetToolTip(toggle, IsTaskClosed(p, task) ? "重新打开任务" : "完成任务"); Grid.SetColumn(toggle, 1); row.Children.Add(toggle);
        var priority = ViewDot(ColorBrush(PriorityColor(task.Priority))); Grid.SetColumn(priority, 2); row.Children.Add(priority); var info = Column(4); var title = ViewText(task.Title, 13, IsDone(p, task) ? "MutedTextBrush" : "PrimaryTextBrush", true); if (IsDone(p, task)) title.TextDecorations = TextDecorations.Strikethrough;
        title.DoubleTapped += async (_, args) => { args.Handled = true; await GuardAsync(() => RenameTaskAsync(p.Id, task.Id)); }; info.Children.Add(title);
        var context = Row(7); var group = ViewText(p.TaskGroups.FirstOrDefault(g => g.Id == task.TaskGroupId)?.Name ?? "", 11, "SecondaryTextBrush"); group.MaxWidth = 150; context.Children.Add(ViewBorder(group, "SubtleBackgroundBrush", new Thickness(5, 1, 5, 1)));
        var status = ViewText(Status(p, task)?.Name ?? "", 11, "SecondaryTextBrush"); context.Children.Add(ViewDot(ColorBrush(Status(p, task)?.Color), 6, true)); context.Children.Add(status); info.Children.Add(context); Grid.SetColumn(info, 3); row.Children.Add(info);
        if (!string.IsNullOrEmpty(task.TrackedStart)) { var duration = _service.GetTrackedDuration(p, task); var timer = ViewText($"{(int)duration.TotalHours:D2}:{duration.Minutes:D2}", 10, "MutedTextBrush"); timer.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetColumn(timer, 4); row.Children.Add(timer); }
        else { var start = ViewIcon("\uE768", "开始计时", () => ChangeAsync(p.Id, draft => _service.StartTracking(draft, task.Id))); start.Content = SourceIcon("play", 11); start.IsEnabled = !IsTaskClosed(p, task); ViewIdentity(start, "TaskTimer:" + task.Id, "开始计时：" + task.Title); start.HorizontalAlignment = HorizontalAlignment.Right; Grid.SetColumn(start, 4); row.Children.Add(start); }
        var due = ViewText(ShortDate(task.DueDate), 11, Overdue(p, task) ? "DangerBrush" : "MutedTextBrush"); ToolTipService.SetToolTip(due, string.IsNullOrEmpty(task.DueDate) ? "未设截止日期" : "截止 " + task.DueDate); Grid.SetColumn(due, 5); row.Children.Add(due);
        var actions = Row(2); actions.Children.Add(ViewTaskMore(p, task, visibleTaskIds));
        var delete = ViewIcon("\uE74D", "删除任务", () => DeleteTaskWithConfirmationAsync(p.Id, task.Id)); delete.Content = SourceIcon("trash", 13); ViewIdentity(delete, "TaskDelete:" + task.Id, "删除任务：" + task.Title); actions.Children.Add(delete); Grid.SetColumn(actions, 6); row.Children.Add(actions);
        var holder = ViewBorder(row, "AppBackgroundBrush", border: new Thickness(0, 0, 0, 1)); ViewTaskIdentity(holder, p, task); return holder;
    }

    private void EnableTaskDrag(ListView list, Project p, string? statusId)
    {
        void DragStarting(object sender, DragItemsStartingEventArgs args)
        {
            if (!_taskListCleanup.ContainsKey(list)) return;
            if (args.Items.FirstOrDefault() is TaskRow task) { args.Data.SetText($"pureproject-task:{p.Id}:{task.Id}"); args.Data.RequestedOperation = DataPackageOperation.Move; }
        }
        void DragOver(object sender, DragEventArgs args)
        { if (_taskListCleanup.ContainsKey(list) && args.DataView.Contains(StandardDataFormats.Text)) args.AcceptedOperation = DataPackageOperation.Move; }
        async void Drop(object sender, DragEventArgs args)
        {
            if (!_taskListCleanup.ContainsKey(list) || !args.DataView.Contains(StandardDataFormats.Text)) return; var deferral = args.GetDeferral();
            try
            {
                var text = await args.DataView.GetTextAsync(); if (!_taskListCleanup.ContainsKey(list)) return;
                var prefix = $"pureproject-task:{p.Id}:"; if (!text.StartsWith(prefix, StringComparison.Ordinal)) return; var taskId = text[prefix.Length..]; var dropY = args.GetPosition(list).Y; string? beforeId = null;
                for (var i = 0; i < list.Items.Count; i++) { if (list.ContainerFromIndex(i) is not ListViewItem container || list.Items[i] is not TaskRow item || item.Id == taskId) continue; var point = container.TransformToVisual(list).TransformPoint(new Point(0, 0)); if (dropY < point.Y + container.ActualHeight / 2) { beforeId = item.Id; break; } }
                await GuardAsync(() => ChangeAsync(p.Id, draft => { if (statusId is not null) _service.ChangeTaskStatus(draft, taskId, statusId); var moving = draft.Tasks.Single(t => t.Id == taskId); draft.Tasks.Remove(moving); var insertAt = beforeId is not null ? draft.Tasks.FindIndex(t => t.Id == beforeId) : statusId is null ? draft.Tasks.Count : draft.Tasks.FindLastIndex(t => t.StatusId == statusId) + 1; draft.Tasks.Insert(Math.Clamp(insertAt, 0, draft.Tasks.Count), moving); }));
            }
            finally { deferral.Complete(); }
        }
        list.CanDragItems = true; list.AllowDrop = true;
        list.DragItemsStarting += DragStarting; list.DragOver += DragOver; list.Drop += Drop;
        _taskListCleanup[list].Retire += () => { list.DragItemsStarting -= DragStarting; list.DragOver -= DragOver; list.Drop -= Drop; };
    }

    private UIElement BuildCalendar(Project p)
    {
        var tasks = ViewGroupTasks(p).Where(t => DateOnly.TryParse(t.DueDate, out _)).ToList(); var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition()); layout.Children.Add(ViewHeader(p, tasks.Count > 0 ? $"{tasks.Count:N0} 个任务有时间线" : null, true));
        var navigation = new Grid { Padding = new Thickness(24, 12, 24, 12) }; var controls = Row(12);
        var previous = ViewIcon("\uE76B", "上个月", () => { _calendarDate = _calendarDate.AddMonths(-1); RenderContent(); return Task.CompletedTask; }, 26); ViewIdentity(previous, "CalendarPreviousMonth"); previous.Width = 34; controls.Children.Add(previous); var month = ViewText(_calendarDate.ToString("yyyy年M月"), 15, bold: true); month.MinWidth = 100; month.TextAlignment = TextAlignment.Center; controls.Children.Add(month);
        var next = ViewIcon("\uE76C", "下个月", () => { _calendarDate = _calendarDate.AddMonths(1); RenderContent(); return Task.CompletedTask; }, 26); ViewIdentity(next, "CalendarNextMonth"); next.Width = 34; controls.Children.Add(next); navigation.Children.Add(controls);
        var todayButton = ViewButton("今天", () => { _calendarDate = DateTimeOffset.Now; _calendarSelectedDate = null; RenderContent(); return Task.CompletedTask; }, height: 26); ViewIdentity(todayButton, "CalendarToday"); todayButton.Background = ThemeBrush("SubtleBackgroundBrush"); todayButton.Padding = new Thickness(12, 0, 12, 0); todayButton.FontSize = 12; todayButton.CornerRadius = new CornerRadius(6); todayButton.HorizontalAlignment = HorizontalAlignment.Right; navigation.Children.Add(todayButton);
        var navBorder = ViewBorder(navigation, "AppBackgroundBrush", border: new Thickness(0, 0, 0, 1)); Grid.SetRow(navBorder, 1); layout.Children.Add(navBorder);
        var body = new Grid(); body.ColumnDefinitions.Add(new ColumnDefinition()); body.ColumnDefinitions.Add(new ColumnDefinition { Width = _calendarSelectedDate.HasValue ? new GridLength(300) : new GridLength(0) }); Grid.SetRow(body, 2); layout.Children.Add(body);
        var calendar = new Grid { Name = "CalendarMonthGrid", Margin = new Thickness(24, 0, 24, 16) }; for (var i = 0; i < 7; i++) calendar.ColumnDefinitions.Add(new ColumnDefinition()); calendar.RowDefinitions.Add(new RowDefinition { Height = new GridLength(28) }); for (var i = 0; i < 6; i++) calendar.RowDefinitions.Add(new RowDefinition());
        var weekNames = new[] { "一", "二", "三", "四", "五", "六", "日" };
        for (var i = 0; i < 7; i++) { var label = ViewText(weekNames[i], 11, "MutedTextBrush", true); label.HorizontalAlignment = HorizontalAlignment.Center; var holder = ViewBorder(label, "AppBackgroundBrush", border: new Thickness(0, 0, 0, 1)); Grid.SetColumn(holder, i); calendar.Children.Add(holder); }
        var first = new DateOnly(_calendarDate.Year, _calendarDate.Month, 1); var start = first.AddDays(-(((int)first.DayOfWeek + 6) % 7)); var today = DateOnly.FromDateTime(DateTime.Today);
        var days = ProjectPresentation.CalendarWindow(p, tasks, start);
        for (var i = 0; i < 42; i++)
        {
            var date = start.AddDays(i); var current = date.Month == first.Month; var stack = Column(2); stack.Padding = new Thickness(6, 4, 6, 4); stack.VerticalAlignment = VerticalAlignment.Top; var cellContent = new Grid();
            var dayNumber = ViewButton("", () => { _calendarSelectedDate = _calendarSelectedDate == date ? null : date; RenderContent(); return Task.CompletedTask; }, height: 18);
            dayNumber.Content = ViewText(date.Day.ToString(), 12, date == today ? "AccentBrush" : "MutedTextBrush", date == today);
            dayNumber.Padding = new Thickness(0); dayNumber.BorderThickness = new Thickness(0); dayNumber.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent); dayNumber.HorizontalAlignment = HorizontalAlignment.Left;
            ViewIdentity(dayNumber, $"CalendarDate:{date:yyyy-MM-dd}", date.ToString("yyyy年M月d日"), "按 Enter 查看当天任务");
            var day = days[i]; var active = day.Tasks;
            var dense = active.Count > 2;
            stack.Children.Add(dayNumber);
            var accessibleSummary = $"{date:yyyy年M月d日}，{active.Count} 个排期任务，其中 {day.StartingCount} 个开始、{day.DueCount} 个截止、{day.CompletedCount} 个已完成";
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(dayNumber, accessibleSummary);
            ToolTipService.SetToolTip(dayNumber, accessibleSummary);
            if (dense)
            {
                var summary = ViewButton($"{active.Count:N0} 项", () => { _calendarSelectedDate = date; RenderContent(); FocusUiElement("CalendarCloseDetails"); return Task.CompletedTask; }, height: 24);
                summary.Padding = new Thickness(4, 0, 4, 0);
                summary.HorizontalAlignment = HorizontalAlignment.Stretch;
                summary.HorizontalContentAlignment = HorizontalAlignment.Left;
                ViewIdentity(summary, $"CalendarSummary:{date:yyyy-MM-dd}", accessibleSummary, "按 Enter 查看全部任务；开始、截止和完成数量可重叠");
                stack.Children.Add(summary);
                var indicators = ViewText($"始 {day.StartingCount:N0} · 截 {day.DueCount:N0}", 10, "MutedTextBrush");
                ToolTipService.SetToolTip(indicators, $"当天开始 {day.StartingCount:N0} 个，截止 {day.DueCount:N0} 个；共 {day.CompletedCount:N0} 个已完成"); stack.Children.Add(indicators);
            }
            if (current)
            {
                foreach (var task in active.Take(dense ? 0 : 2))
                {
                    var due = task.DueDate == date.ToString("yyyy-MM-dd"); var begins = TaskStart(p, task) == date;
                    var label = ViewText((task.Recurrence is null ? "" : "↻ ") + task.Title, 11, due && IsDone(p, task) ? "MutedTextBrush" : due && Overdue(p, task) ? "DangerTextBrush" : "PrimaryTextBrush"); if (due && IsDone(p, task)) label.TextDecorations = TextDecorations.Strikethrough;
                    var button = ViewButton("", () => EditTaskAsync(task.Id, p.Id), height: 22); button.Content = label; button.Padding = new Thickness(4, 2, 4, 2); button.BorderThickness = new Thickness(3, 0, 0, 0); button.BorderBrush = ColorBrush(PriorityColor(task.Priority)); button.CornerRadius = new CornerRadius(3); button.Background = ThemeBrush(due ? "AppBackgroundBrush" : "AccentLightBrush"); PreserveButtonPalette(button); button.HorizontalAlignment = HorizontalAlignment.Stretch; button.HorizontalContentAlignment = HorizontalAlignment.Left; ViewTaskIdentity(button, p, task); ViewIdentity(button, $"CalendarTask:{date:yyyy-MM-dd}:{(due ? "due" : "start")}:{task.Id}", (begins && due ? "开始并截止：" : due ? "截止任务：" : begins ? "开始任务：" : "跨日排期：") + BoundedViewText(task.Title)); stack.Children.Add(button);
                }
            }
            var cellScroll = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            cellContent.Children.Add(cellScroll); var cell = ViewBorder(cellContent, date == today ? "AccentLightBrush" : current ? "AppBackgroundBrush" : "SubtleBackgroundBrush", border: new Thickness(0, 0, i % 7 == 6 ? 0 : 1, 1)); cell.Name = $"CalendarDay{i}"; cell.Tag = date;
            if (_calendarSelectedDate == date) { cell.BorderThickness = new Thickness(2); cell.BorderBrush = ThemeBrush("AccentBrush"); }
            cell.Tapped += (_, args) => { var source = args.OriginalSource as DependencyObject; while (source is not null && source != cell) { if (source is Button) return; source = VisualTreeHelper.GetParent(source); } _calendarSelectedDate = _calendarSelectedDate == date ? null : date; RenderContent(); };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(cell, accessibleSummary); Grid.SetRow(cell, i / 7 + 1); Grid.SetColumn(cell, i % 7); calendar.Children.Add(cell);
        }
        body.Children.Add(calendar);
        if (_calendarSelectedDate is { } selected)
        {
            var panel = new Grid(); panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); panel.RowDefinitions.Add(new RowDefinition());
            var daily = days.FirstOrDefault(d => d.Date == selected)?.Tasks ?? ProjectPresentation.CalendarWindow(p, tasks, selected, 1)[0].Tasks; var header = new Grid { Padding = new Thickness(16, 12, 12, 12) }; var text = Column(4); text.Children.Add(ViewText(selected.ToString("M月d日 dddd"), 14, bold: true)); text.Children.Add(ViewText($"{daily.Count:N0} 个任务", 11, "MutedTextBrush")); header.Children.Add(text);
            var close = ViewIcon("\uE711", "关闭日期详情", () => { _calendarSelectedDate = null; RenderContent(); FocusUiElement($"CalendarDate:{selected:yyyy-MM-dd}"); return Task.CompletedTask; }); ViewIdentity(close, "CalendarCloseDetails"); close.HorizontalAlignment = HorizontalAlignment.Right; close.VerticalAlignment = VerticalAlignment.Top; header.Children.Add(close); panel.Children.Add(ViewBorder(header, "AppBackgroundBrush", border: new Thickness(0, 0, 0, 1)));
            UIElement DayEntry(ProjectTask task)
            {
                var isStart = selected == TaskStart(p, task); var isDue = selected.ToString("yyyy-MM-dd") == task.DueDate;
                var kind = isStart ? isDue ? "开始·截止" : "开始" : isDue ? "截止" : "进行中";
                var kindColor = isStart && isDue ? "SuccessBrush" : isDue ? "DangerBrush" : "AccentBrush";
                var stack = Column(6); var top = new Grid { ColumnSpacing = 6 }; top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); top.ColumnDefinitions.Add(new ColumnDefinition()); top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                top.Children.Add(ViewDot(ColorBrush(PriorityColor(task.Priority)), 6, true)); var title = ViewText(task.Title, 13, bold: true); title.TextWrapping = TextWrapping.Wrap; title.MaxLines = 3; Grid.SetColumn(title, 1); top.Children.Add(title);
                var badge = ViewBorder(ViewText(kind, 9, kindColor), "AccentLightBrush", new Thickness(4, 2, 4, 2), new Thickness(0), 3); Grid.SetColumn(badge, 2); top.Children.Add(badge); stack.Children.Add(top);
                if (!string.IsNullOrEmpty(task.Description)) { var desc = ViewText(task.Description, 11, "MutedTextBrush"); desc.TextWrapping = TextWrapping.Wrap; desc.MaxLines = 2; stack.Children.Add(desc); }
                var meta = Row(6); meta.Children.Add(ViewText(Status(p, task)?.Name ?? "未知状态", 10, IsDone(p, task) ? "SuccessBrush" : "MutedTextBrush")); meta.Children.Add(ViewText(PriorityText(task.Priority), 10, "MutedTextBrush")); stack.Children.Add(meta);
                var dates = ViewText("截止 " + task.DueDate + (task.Subtasks.Count > 0 ? $"   {task.Subtasks.Count(s => s.Done)}/{task.Subtasks.Count}" : ""), 10, Overdue(p, task) ? "DangerBrush" : "MutedTextBrush"); stack.Children.Add(dates);
                var card = ViewBorder(stack, "SubtleBackgroundBrush", new Thickness(12, 10, 12, 10), radius: 8); ViewTaskIdentity(card, p, task);
                var holder = new Grid { Margin = new Thickness(0, 0, 0, 6) }; holder.Children.Add(card); holder.Children.Add(new Border { Width = 3, Background = ThemeBrush(kindColor), HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(3), IsHitTestVisible = false }); return holder;
            }
            FrameworkElement entries;
            if (daily.Count == 0) { entries = ViewText("这一天没有排期任务", 12, "MutedTextBrush"); entries.Margin = new Thickness(8, 24, 8, 24); }
            else { var list = MakeTaskList(p, daily, renderTask: DayEntry); list.Padding = new Thickness(8); ViewIdentity(list, "CalendarDayTasks", $"{selected:M月d日} 的 {daily.Count} 个任务"); entries = list; }
            Grid.SetRow(entries, 1); panel.Children.Add(entries); var panelBorder = ViewBorder(panel, "AppBackgroundBrush", border: new Thickness(1, 0, 0, 0)); Grid.SetColumn(panelBorder, 1); body.Children.Add(panelBorder);
        }
        return layout;
    }

    private static DateOnly TaskStart(Project p, ProjectTask t) => ProjectPresentation.TaskStart(p, t);

    private FrameworkElement ViewLegend(Project p, IEnumerable<ProjectTask> tasks, bool arrows)
    {
        var row = Row(24); row.Padding = new Thickness(24, 12, 24, 12); row.MinHeight = 42; var priorities = Row(12); priorities.Children.Add(ViewText("优先级：", 11, "MutedTextBrush"));
        foreach (var priority in new[] { "high", "medium", "low" }) { var item = Row(5); item.Children.Add(ViewDot(ColorBrush(PriorityColor(priority)), 10)); item.Children.Add(ViewText(PriorityText(priority)[..1], 11, "SecondaryTextBrush")); priorities.Children.Add(item); } row.Children.Add(priorities);
        var statuses = Row(12); statuses.Children.Add(ViewText("状态：", 11, "MutedTextBrush"));
        var scopeStatuses = arrows ? tasks.Select(t => Status(p, t)).OfType<TaskStatusDefinition>() : p.TaskGroups.Where(g => !g.Archived && (string.IsNullOrEmpty(_groupId) || g.Id == _groupId)).OrderBy(g => g.SortOrder).SelectMany(g => g.Statuses.OrderBy(s => s.SortOrder));
        foreach (var status in scopeStatuses.DistinctBy(s => s.Id)) { var item = Row(5); item.Children.Add(ViewDot(ColorBrush(status.Color), arrows ? 8 : 10, arrows)); item.Children.Add(ViewText(status.Name, 11, "SecondaryTextBrush")); statuses.Children.Add(item); } row.Children.Add(statuses);
        if (arrows) row.Children.Add(ViewText("箭头： A → B 表示 B 依赖 A", 11, "MutedTextBrush"));
        return ViewBorder(new ScrollViewer { Content = row, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled }, "SidebarBackgroundBrush", border: new Thickness(0, 1, 0, 0));
    }

    private UIElement BuildTimeline(Project p)
    {
        var tasks = GraphTasks(p).ToList();
        if (tasks.Count > 500) return ViewScopeLimit(p, "时间线", tasks.Count, 500);
        var layout = new Grid(); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition()); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.Children.Add(GraphHeader(p, $"时间线视图 · {tasks.Count:N0} 个任务"));
        var milestoneBar = Row(7); milestoneBar.Padding = new Thickness(24, 6, 24, 6); milestoneBar.MinHeight = 42; milestoneBar.Children.Add(ViewText("项目里程碑", 11, "SecondaryTextBrush", true));
        foreach (var milestone in p.Milestones.OrderBy(m => m.Date))
        {
            var chip = Row(2); var edit = ViewButton($"{milestone.Title} · {milestone.Date}", () => EditMilestoneAsync(p.Id, milestone.Id), height: 26); edit.BorderThickness = new Thickness(0); edit.FontSize = 10; edit.Padding = new Thickness(6, 0, 2, 0); chip.Children.Add(edit);
            var remove = ViewIcon("\uE711", "删除里程碑：" + milestone.Title, async () =>
            {
                if (await ConfirmAsync("删除里程碑", $"删除「{milestone.Title}」？可以使用撤销恢复。"))
                    await ChangeAsync(p.Id, draft => draft.Milestones.RemoveAll(m => m.Id == milestone.Id), "里程碑已删除");
            }, 22); ViewIdentity(remove, "MilestoneDelete:" + milestone.Id, "删除里程碑：" + milestone.Title); chip.Children.Add(remove);
            var outline = ViewBorder(chip); outline.BorderBrush = ColorBrush(milestone.Color); milestoneBar.Children.Add(outline);
        }
        var milestoneTitle = new TextBox { PlaceholderText = "里程碑名称", MaxLength = 500, FontFamily = UiFont, Width = 146, Height = 28, MinHeight = 28, FontSize = 11, Padding = new Thickness(7, 3, 7, 3), CornerRadius = new CornerRadius(2), Background = ThemeBrush("CardBackgroundBrush") };
        var milestoneDate = new CalendarDatePicker { PlaceholderText = "年/月/日", FontFamily = UiFont, Width = 110, Height = 28, MinHeight = 28, FontSize = 11, Padding = new Thickness(7, 0, 7, 0), CornerRadius = new CornerRadius(2), Background = ThemeBrush("CardBackgroundBrush") };
        ViewIdentity(milestoneTitle, "MilestoneQuickAddTitle", "里程碑名称"); ViewIdentity(milestoneDate, "MilestoneQuickAddDate", "里程碑日期");
        milestoneBar.Children.Add(milestoneTitle); milestoneBar.Children.Add(milestoneDate);
        var savingMilestone = false; Button? addMilestone = null;
        bool CanCreateMilestone() => !savingMilestone && !string.IsNullOrWhiteSpace(milestoneTitle.Text) && milestoneDate.Date.HasValue;
        void RefreshMilestoneState()
        {
            milestoneTitle.IsEnabled = milestoneDate.IsEnabled = !savingMilestone;
            if (addMilestone is not null) addMilestone.IsEnabled = CanCreateMilestone();
        }
        async Task CreateMilestone()
        {
            if (_busy || !CanCreateMilestone() || milestoneDate.Date is not { } date) return;
            var title = milestoneTitle.Text.Trim(); var saved = false; savingMilestone = true; RefreshMilestoneState();
            try { await ChangeAsync(p.Id, draft => draft.Milestones.Add(new Milestone { Id = Guid.NewGuid().ToString(), Title = title, Date = date.ToString("yyyy-MM-dd"), Color = "#9f352f" })); saved = true; }
            finally { savingMilestone = false; RefreshMilestoneState(); if (saved) FocusUiElement("MilestoneQuickAddTitle"); }
        }
        addMilestone = ViewButton("添加", CreateMilestone, true, 28); ViewIdentity(addMilestone, "MilestoneQuickAddSubmit", "添加里程碑"); milestoneBar.Children.Add(addMilestone);
        milestoneTitle.TextChanged += (_, _) => RefreshMilestoneState(); milestoneDate.DateChanged += (_, _) => RefreshMilestoneState();
        milestoneTitle.KeyDown += async (_, args) => { if (args.Key == Windows.System.VirtualKey.Enter) { args.Handled = true; await GuardAsync(CreateMilestone); } }; RefreshMilestoneState();
        var milestoneScroll = new ScrollViewer { Content = milestoneBar, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled }; var milestoneBorder = ViewBorder(milestoneScroll, "AppBackgroundBrush", border: new Thickness(0, 0, 0, 1)); Grid.SetRow(milestoneBorder, 1); layout.Children.Add(milestoneBorder);
        var today = DateOnly.FromDateTime(DateTime.Today); DateOnly Created(ProjectTask t) => DateTimeOffset.TryParse(t.CreatedAt, out var date) ? DateOnly.FromDateTime(date.LocalDateTime) : today;
        var origin = tasks.Count > 0 ? tasks.Min(Created) : today; var computed = new Dictionary<string, (double Start, double Duration)>();
        var taskIndex = p.Tasks.ToDictionary(t => t.Id);
        // Post-order traversal avoids recursion/stack overflow on long dependency chains.
        foreach (var visible in tasks)
        {
            var pending = new Stack<(ProjectTask Task, bool Expanded)>(); pending.Push((visible, false));
            while (pending.TryPop(out var entry))
            {
                var task = entry.Task;
                if (computed.ContainsKey(task.Id)) continue;
                if (!entry.Expanded)
                {
                    pending.Push((task, true));
                    foreach (var dependency in task.Dependencies)
                        if (!computed.ContainsKey(dependency.TaskId) && taskIndex.TryGetValue(dependency.TaskId, out var parent)) pending.Push((parent, false));
                    continue;
                }
                var duration = DateOnly.TryParse(task.DueDate, out var due) ? Math.Max(1, due.DayNumber - Created(task).DayNumber) : 7;
                double start = task.StartOffset is { } offset && double.IsFinite(offset) ? offset : Math.Max(0, Created(task).DayNumber - origin.DayNumber);
                if (task.Dependencies.Count > 0) start = task.Dependencies.Select(d => computed.TryGetValue(d.TaskId, out var parent) ? parent.Start + (double.IsFinite(d.DayOffset) ? d.DayOffset : 0) : 0).DefaultIfEmpty(0).Max();
                computed[task.Id] = (Math.Clamp(start, -origin.DayNumber, DateOnly.MaxValue.DayNumber - origin.DayNumber - duration), duration);
            }
        }
        var milestones = p.Milestones.Where(m => DateOnly.TryParse(m.Date, out _)).ToList(); var days = tasks.SelectMany(t => new[] { computed[t.Id].Start, computed[t.Id].Start + computed[t.Id].Duration }).Concat(milestones.Select(m => (double)(DateOnly.Parse(m.Date).DayNumber - origin.DayNumber))).ToList();
        var min = Math.Max(-origin.DayNumber, Math.Min(-7, days.DefaultIfEmpty(-7).Min())); var max = Math.Min(DateOnly.MaxValue.DayNumber - origin.DayNumber, Math.Max(37, days.DefaultIfEmpty(37).Max() + 7)); var total = Math.Max(1, max - min);
        var canvas = new Canvas { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top }; var scroll = new ScrollViewer { Content = canvas, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        void Draw(double viewport)
        {
            const double axisHeight = 61;
            canvas.Children.Clear(); var chartWidth = Math.Max(360, viewport - 200); var width = chartWidth + 200; var height = Math.Max(180, axisHeight + tasks.Count * 36); canvas.Width = width; canvas.Height = height; double X(double day) => 200 + (day - min) / total * chartWidth;
            canvas.Children.Add(new Border { Width = width, Height = axisHeight, Background = ThemeBrush("SidebarBackgroundBrush") }); var taskLabel = ViewText("任务", 12, "SecondaryTextBrush", true); Canvas.SetLeft(taskLabel, 16); Canvas.SetTop(taskLabel, 8); canvas.Children.Add(taskLabel);
            canvas.Children.Add(new Line { X1 = 200, X2 = 200, Y1 = 0, Y2 = height, Stroke = ThemeBrush("BorderBrush"), StrokeThickness = 1 }); canvas.Children.Add(new Line { X1 = 0, X2 = width, Y1 = 36, Y2 = 36, Stroke = ThemeBrush("BorderBrush"), StrokeThickness = 1 }); canvas.Children.Add(new Line { X1 = 0, X2 = width, Y1 = axisHeight, Y2 = axisHeight, Stroke = ThemeBrush("BorderBrush"), StrokeThickness = 1 });
            var from = DateOnly.FromDayNumber((int)(origin.DayNumber + min)); var weekStep = Math.Max(7, Math.Ceiling(total / 100 / 7) * 7); var firstMonday = ((8 - (int)from.DayOfWeek) % 7);
            for (double d = 0; d <= total; d += weekStep)
            {
                var day = d + firstMonday; if (day > total) break; var date = DateOnly.FromDayNumber((int)Math.Clamp(origin.DayNumber + min + day, 0, DateOnly.MaxValue.DayNumber)); var x = X(min + day);
                canvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = axisHeight, Y2 = height, Stroke = ThemeBrush("BorderBrush"), StrokeThickness = 1, Opacity = .5 }); var week = ViewText(date.ToString("M/d"), 10, "MutedTextBrush"); week.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); Canvas.SetLeft(week, x - week.DesiredSize.Width / 2); Canvas.SetTop(week, 40); canvas.Children.Add(week);
            }
            var monthDate = new DateOnly(from.Year, from.Month, 1); if (monthDate < from && monthDate < new DateOnly(9999, 12, 1)) monthDate = monthDate.AddMonths(1);
            for (var monthIndex = 0; monthIndex < 1200 && monthDate.DayNumber - from.DayNumber <= total; monthIndex++)
            {
                var label = ViewText(monthDate.ToString("M月"), 12, "SecondaryTextBrush", true); label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); Canvas.SetLeft(label, X(monthDate.DayNumber - origin.DayNumber) - label.DesiredSize.Width / 2); Canvas.SetTop(label, 6); canvas.Children.Add(label);
                if (monthDate.Year == 9999 && monthDate.Month == 12) break; monthDate = monthDate.AddMonths(1);
            }
            for (var i = 0; i < tasks.Count; i++)
            {
                var task = tasks[i]; var timing = computed[task.Id]; var y = axisHeight + i * 36; canvas.Children.Add(new Line { X1 = 0, X2 = width, Y1 = y + 36, Y2 = y + 36, Stroke = ThemeBrush("BorderBrush"), StrokeThickness = 1 });
                var labelRow = Row(8); labelRow.Children.Add(ViewDot(ColorBrush(Status(p, task)?.Color), 8, true)); var title = ViewText(task.Title, 12, IsDone(p, task) ? "MutedTextBrush" : "PrimaryTextBrush"); title.MaxWidth = 156; if (IsDone(p, task)) title.TextDecorations = TextDecorations.Strikethrough; labelRow.Children.Add(title);
                var taskButton = ViewButton("", () => EditTaskAsync(task.Id, p.Id), height: 36); taskButton.Content = labelRow; taskButton.Width = 199; taskButton.Height = 36; taskButton.Padding = new Thickness(16, 0, 8, 0); taskButton.BorderThickness = new Thickness(0); taskButton.Background = ThemeBrush("AppBackgroundBrush"); taskButton.HorizontalContentAlignment = HorizontalAlignment.Left; ViewTaskIdentity(taskButton, p, task); Canvas.SetTop(taskButton, y); canvas.Children.Add(taskButton);
                var fill = (SolidColorBrush)ColorBrush(PriorityColor(task.Priority));
                if (IsDone(p, task)) { var background = ((SolidColorBrush)ThemeBrush("AppBackgroundBrush")).Color; var color = fill.Color; fill = new SolidColorBrush(Windows.UI.Color.FromArgb(255, (byte)((color.R + background.R) / 2), (byte)((color.G + background.G) / 2), (byte)((color.B + background.B) / 2))); }
                var barText = ViewText($"{task.Title} ({timing.Duration:0}天)", 11); barText.Foreground = ContrastForeground(fill.Color); if (IsDone(p, task)) barText.TextDecorations = TextDecorations.Strikethrough; var bar = ViewButton("", () => EditTaskAsync(task.Id, p.Id), height: 24); bar.Content = barText; bar.Height = 24; bar.Width = Math.Max(6, timing.Duration / total * chartWidth); bar.Padding = new Thickness(8, 0, 8, 0); bar.Background = fill; bar.CornerRadius = new CornerRadius(4);
                bar.BorderThickness = new Thickness(Overdue(p, task) ? 2 : 0); bar.BorderBrush = ThemeBrush("DangerBrush"); bar.Foreground = barText.Foreground; PreserveButtonPalette(bar); bar.HorizontalContentAlignment = HorizontalAlignment.Left; ViewTaskIdentity(bar, p, task); ViewIdentity(bar, "TimelineBar:" + task.Id); ToolTipService.SetToolTip(bar, $"{BoundedViewText(task.Title)}\n{timing.Duration:0} 天 · {Status(p, task)?.Name}"); Canvas.SetLeft(bar, X(timing.Start)); Canvas.SetTop(bar, y + 6); canvas.Children.Add(bar);
            }
            var todayX = X(today.DayNumber - origin.DayNumber); if (todayX >= 200 && todayX <= width) canvas.Children.Add(new Line { X1 = todayX, X2 = todayX, Y1 = axisHeight, Y2 = height, Stroke = ThemeBrush("DangerBrush"), StrokeThickness = 2, Opacity = .7 });
            foreach (var milestone in milestones) { var x = X(DateOnly.Parse(milestone.Date).DayNumber - origin.DayNumber); canvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = axisHeight, Y2 = height, Stroke = ColorBrush(milestone.Color), StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 3, 3 }, Opacity = .8 }); }
        }
        Draw(Math.Max(600, ContentHost.ActualWidth)); scroll.SizeChanged += (_, args) => Draw(args.NewSize.Width); ViewEnablePanning(scroll); Grid.SetRow(scroll, 2); layout.Children.Add(scroll); var legend = ViewLegend(p, tasks, false); Grid.SetRow(legend, 3); layout.Children.Add(legend); return layout;
    }

    private UIElement BuildDependencies(Project p)
    {
        var tasks = GraphTasks(p).ToList();
        if (tasks.Count > 300) return ViewScopeLimit(p, "依赖关系图", tasks.Count, 300);
        var ids = tasks.Select(t => t.Id).ToHashSet();
        var byId = p.Tasks.ToDictionary(t => t.Id);
        foreach (var task in p.Tasks) if (task.Dependencies.Any(d => ids.Contains(d.TaskId)) && ids.Add(task.Id)) tasks.Add(task);
        for (var i = 0; i < tasks.Count && tasks.Count <= 300; i++) foreach (var dependency in tasks[i].Dependencies) if (ids.Add(dependency.TaskId) && byId.TryGetValue(dependency.TaskId, out var ancestor)) tasks.Add(ancestor);
        if (tasks.Count > 300) return ViewScopeLimit(p, "依赖关系图", tasks.Count, 300);
        var layout = new Grid(); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.RowDefinitions.Add(new RowDefinition()); layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); layout.Children.Add(GraphHeader(p, $"依赖关系图 · {tasks.Count:N0} 个节点（含关联任务）"));
        var levels = new Dictionary<string, int>(); var degree = tasks.ToDictionary(t => t.Id, t => t.Dependencies.Count(d => tasks.Any(x => x.Id == d.TaskId))); var queue = new Queue<ProjectTask>(tasks.Where(t => degree[t.Id] == 0)); foreach (var task in queue) levels[task.Id] = 0;
        while (queue.TryDequeue(out var parent)) foreach (var child in tasks.Where(t => t.Dependencies.Any(d => d.TaskId == parent.Id))) { levels[child.Id] = Math.Max(levels.GetValueOrDefault(child.Id), levels[parent.Id] + 1); if (--degree[child.Id] == 0) queue.Enqueue(child); }
        var last = levels.Values.DefaultIfEmpty(0).Max(); foreach (var task in tasks) if (!levels.ContainsKey(task.Id)) levels[task.Id] = ++last;
        var positions = new Dictionary<string, (double X, double Y)>(); foreach (var layer in tasks.GroupBy(t => levels[t.Id])) { var row = 0; foreach (var task in layer) positions[task.Id] = (layer.Key * 220, row++ * 106); }
        var canvas = new Canvas { Width = positions.Count == 0 ? 360 : positions.Values.Max(v => v.X) + 160, Height = positions.Count == 0 ? 180 : positions.Values.Max(v => v.Y) + 76, Margin = new Thickness(24), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
        foreach (var task in tasks)
        {
            var target = positions[task.Id];
            foreach (var dependency in task.Dependencies)
            {
                if (!positions.TryGetValue(dependency.TaskId, out var source)) continue; var from = new Point(source.X + 160, source.Y + 38); var to = new Point(target.X, target.Y + 38); var mid = (from.X + to.X) / 2;
                var figure = new PathFigure { StartPoint = from }; figure.Segments.Add(new BezierSegment { Point1 = new Point(mid, from.Y), Point2 = new Point(mid, to.Y), Point3 = to }); var geometry = new PathGeometry(); geometry.Figures.Add(figure);
                canvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Path { Data = geometry, Stroke = ThemeBrush("AccentBrush"), StrokeThickness = 2, Opacity = .5 }); canvas.Children.Add(new Polygon { Points = new PointCollection { new(to.X, to.Y), new(to.X - 16, to.Y - 6), new(to.X - 16, to.Y + 6) }, Fill = ThemeBrush("AccentBrush"), Opacity = .3 });
                if (dependency.DayOffset > 0) { var offset = ViewText($"+{dependency.DayOffset:0.#}天", 10, "MutedTextBrush"); Canvas.SetLeft(offset, mid - 10); Canvas.SetTop(offset, (from.Y + to.Y) / 2 - 14); canvas.Children.Add(offset); }
            }
        }
        foreach (var task in tasks)
        {
            var node = new Canvas { Width = 158, Height = 74 }; var status = Status(p, task); var dot = ViewDot(ColorBrush(status?.Color), 8, true); Canvas.SetLeft(dot, 12); Canvas.SetTop(dot, 14); node.Children.Add(dot);
            var title = ViewText(task.Title, 12, IsDone(p, task) ? "MutedTextBrush" : "PrimaryTextBrush", true); title.MaxWidth = 105; if (IsDone(p, task)) title.TextDecorations = TextDecorations.Strikethrough; Canvas.SetLeft(title, 26); Canvas.SetTop(title, 8); node.Children.Add(title);
            var subtitle = ViewText($"{p.TaskGroups.FirstOrDefault(g => g.Id == task.TaskGroupId)?.Name} · {status?.Name}", 11, "MutedTextBrush"); subtitle.MaxWidth = 133; Canvas.SetLeft(subtitle, 16); Canvas.SetTop(subtitle, 31); node.Children.Add(subtitle);
            var due = ViewText(DateOnly.TryParse(task.DueDate, out var dueDate) ? "截止 " + dueDate.ToString("M/d") : "", 11, Overdue(p, task) ? "DangerBrush" : "MutedTextBrush"); Canvas.SetLeft(due, 16); Canvas.SetTop(due, 51); node.Children.Add(due); var priority = ViewText(PriorityText(task.Priority)[..1], 11, "SecondaryTextBrush"); Canvas.SetLeft(priority, 141); Canvas.SetTop(priority, 7); node.Children.Add(priority);
            var button = ViewButton("", () => EditTaskAsync(task.Id, p.Id)); button.Content = node; button.Width = 160; button.Height = 76; button.Padding = new Thickness(0); button.CornerRadius = new CornerRadius(8); button.Background = ThemeBrush("SubtleBackgroundBrush"); ViewTaskIdentity(button, p, task);
            var holder = new Grid { Width = 160, Height = 76 }; holder.Children.Add(button);
            var outline = new Border { BorderThickness = new Thickness(1), BorderBrush = ThemeBrush("BorderBrush"), CornerRadius = new CornerRadius(8), IsHitTestVisible = false }; ViewIdentity(outline, "TaskNodeOutline:" + task.Id); holder.Children.Add(outline);
            button.PointerEntered += (_, _) => outline.BorderBrush = ThemeBrush("AccentBrush"); button.PointerExited += (_, _) => outline.BorderBrush = ThemeBrush("BorderBrush");
            holder.Children.Add(new Border { Width = 4, Background = ColorBrush(PriorityColor(task.Priority)), CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false }); var point = positions[task.Id]; Canvas.SetLeft(holder, point.X); Canvas.SetTop(holder, point.Y); canvas.Children.Add(holder);
        }
        if (tasks.Count == 0) canvas.Children.Add(ViewText("添加任务及前置依赖后，可以在这里查看关系。", 12, "MutedTextBrush"));
        var scroll = new ScrollViewer { Content = canvas, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; ViewEnablePanning(scroll); Grid.SetRow(scroll, 1); layout.Children.Add(scroll); var legend = ViewLegend(p, tasks, true); Grid.SetRow(legend, 2); layout.Children.Add(legend); return layout;
    }

    private IEnumerable<ProjectTask> GraphTasks(Project p)
    {
        var groups = p.TaskGroups.Where(g => string.IsNullOrEmpty(_groupId) ? !g.Archived : g.Id == _groupId).ToList();
        if (_graphStatusId is not null && !groups.Any(g => g.Statuses.Any(s => s.Id == _graphStatusId))) _graphStatusId = null;
        return ViewGroupTasks(p).Where(t => _graphStatusId is null || t.StatusId == _graphStatusId);
    }

    private FrameworkElement GraphHeader(Project p, string meta)
    {
        var generation = _contentGeneration;
        var panel = Column(0); panel.Children.Add(ViewHeader(p, meta));
        var statuses = p.TaskGroups.Where(g => string.IsNullOrEmpty(_groupId) ? !g.Archived : g.Id == _groupId)
            .OrderBy(g => g.SortOrder).SelectMany(g => g.Statuses.OrderBy(s => s.SortOrder).Select(s => (Group: g, Status: s))).ToList();
        var row = Row(10); row.Padding = new Thickness(24, 0, 24, 10);
        row.Children.Add(ViewText("显示状态", 12, "SecondaryTextBrush"));
        var filter = ViewCombo(new[] { "全部状态" }.Concat(statuses.Select(x => string.IsNullOrEmpty(_groupId) ? x.Group.Name + " · " + x.Status.Name : x.Status.Name)),
            statuses.FindIndex(x => x.Status.Id == _graphStatusId) + 1, 210, "图表状态筛选");
        ViewIdentity(filter, "GraphStatusFilter", "图表状态筛选", "选择单个状态可缩小大任务组的显示范围");
        filter.SelectionChanged += (_, _) =>
        {
            if (!IsCurrentViewEvent(filter, generation, p.Id)) return;
            _graphStatusId = filter.SelectedIndex <= 0 ? null : statuses[filter.SelectedIndex - 1].Status.Id;
            QueueViewRefresh(filter, generation, p.Id, renderShell: false, "GraphStatusFilter");
        };
        row.Children.Add(filter);
        panel.Children.Add(row); return panel;
    }

    private UIElement ViewScopeLimit(Project p, string label, int count, int limit)
    {
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        layout.Children.Add(GraphHeader(p, $"{label} · {count:N0} 个任务"));
        var message = Column(12);
        message.Margin = new Thickness(24, 48, 24, 24);
        message.MaxWidth = 560;
        message.HorizontalAlignment = HorizontalAlignment.Center;
        message.VerticalAlignment = VerticalAlignment.Top;
        var title = ViewText("缩小范围后查看" + label, 17, bold: true);
        title.TextWrapping = TextWrapping.Wrap;
        message.Children.Add(title);
        var detail = ViewText($"当前范围包含 {count:N0} 个任务，超过此视图的 {limit} 项显示上限。请使用任务组和显示状态筛选缩小范围；任务数据会完整保留。"
            + (label == "依赖关系图" ? "关联的前置和后续任务也会计入节点数。" : ""), 13, "SecondaryTextBrush");
        detail.TextWrapping = TextWrapping.Wrap;
        message.Children.Add(detail);
        if (p.TaskGroups.Where(g => string.IsNullOrEmpty(_groupId) ? !g.Archived : g.Id == _groupId).OrderBy(g => g.SortOrder).FirstOrDefault() is { } group)
        {
            var useStatus = p.Tasks.Count(t => t.TaskGroupId == group.Id) > limit;
            var firstStatus = group.Statuses.OrderBy(s => s.SortOrder).FirstOrDefault();
            var choose = ViewButton(useStatus ? "查看此任务组的第一个状态" : "查看第一个任务组", () =>
            {
                _groupId = group.Id; _graphStatusId = useStatus ? firstStatus?.Id : null; Render(); FocusUiElement(useStatus ? "GraphStatusFilter" : "TaskGroupFilter");
                return Task.CompletedTask;
            }, true, 34);
            ToolTipService.SetToolTip(choose, group.Name);
            ViewIdentity(choose, "ReduceTaskScope", "查看任务组：" + group.Name);
            message.Children.Add(choose);
        }
        ViewIdentity(message, "TaskScopeLimit");
        Grid.SetRow(message, 1); layout.Children.Add(message);
        return layout;
    }

    private static void ViewEnablePanning(ScrollViewer scroll)
    {
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetHelpText(scroll, "拖动空白区域平移，或使用滚动条；按 Tab 选择任务，按 Enter 打开。");
        Point origin = default; double horizontal = 0, vertical = 0; uint? pointer = null;
        scroll.PointerPressed += (_, args) =>
        {
            var point = args.GetCurrentPoint(scroll); if (!point.Properties.IsLeftButtonPressed && !point.Properties.IsMiddleButtonPressed) return;
            var source = args.OriginalSource as DependencyObject;
            while (source is not null && source != scroll) { if (source is Button || source is Microsoft.UI.Xaml.Controls.Primitives.ScrollBar) return; source = VisualTreeHelper.GetParent(source); }
            origin = point.Position; horizontal = scroll.HorizontalOffset; vertical = scroll.VerticalOffset;
            if (scroll.CapturePointer(args.Pointer)) { pointer = args.Pointer.PointerId; args.Handled = true; }
        };
        scroll.PointerMoved += (_, args) => { if (pointer != args.Pointer.PointerId) return; var point = args.GetCurrentPoint(scroll).Position; scroll.ChangeView(horizontal - point.X + origin.X, vertical - point.Y + origin.Y, null, true); args.Handled = true; };
        scroll.PointerReleased += (_, args) => { if (pointer != args.Pointer.PointerId) return; pointer = null; scroll.ReleasePointerCapture(args.Pointer); };
        scroll.PointerCaptureLost += (_, _) => pointer = null;
    }
}

public sealed class TaskRow
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public UIElement? Content { get; set; }
}
