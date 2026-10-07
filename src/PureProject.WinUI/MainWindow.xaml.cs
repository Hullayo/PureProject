using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Automation;
using PureProject.Core;
using PureProject.Infrastructure;
using Windows.System;
using Windows.Storage.Pickers;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace PureProject.WinUI;

public sealed partial class MainWindow : Window
{
    private readonly ProjectService _service = new();
    private JsonProjectRepository? _repository;
    private SettingsRepository? _settingsRepository;
    private AppSettings _settings = new();
    private List<Project> _projects = [];
    // Published project versions are immutable. A single-project edit clones only that
    // project; snapshots share all unchanged versions and are trimmed after every save.
    private readonly Stack<List<Project>> _undo = new();
    private readonly Stack<List<Project>> _redo = new();
    private const int MaximumHistorySteps = 12;
    private const int MaximumRetainedTaskVersions = 200_000;
    private const long MaximumRetainedHistoryBytes = 256L * 1024 * 1024;
    private sealed record HistoryWeight(long Bytes);
    private readonly ConditionalWeakTable<Project, HistoryWeight> _historyWeights = new();
    private readonly HashSet<string> _shownReminders = [];
    private string? _selectedId;
    private string? _groupId;
    private int _view;
    private bool _rendering;
    private bool _ready;
    private bool _busy;
    private bool _dialogOpen;
    private bool _settingsLoaded;
    private ContentDialog? _currentActiveDialog;
    private DateTimeOffset _calendarDate = DateTimeOffset.Now;
    private DispatcherTimer? _reminderTimer;
    private new Project? Current => _projects.FirstOrDefault(p => p.Id == _selectedId);
    private TaskGroup? CurrentGroup => Current?.TaskGroups.FirstOrDefault(g => g.Id == _groupId);

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 920));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "icon.ico"));
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = Microsoft.UI.Windowing.TitleBarHeightOption.Collapsed;
        SetTitleBar(TitleBarDragRegion);
        Root.ActualThemeChanged += (_, _) => { _activeDark = Root.ActualTheme == ElementTheme.Dark; if (_ready) Render(); };
        AppWindow.Closing += (_, e) => { if (_busy) { e.Cancel = true; ShowMessage("正在处理数据，请稍后再关闭窗口。"); } };
        Closed += (_, _) => { _ready = false; _reminderTimer?.Stop(); _repository?.Dispose(); };
        AddShortcut(VirtualKey.N, async () => { if (Current is null) await EditProjectAsync(); else await EditTaskAsync(null); });
        AddShortcut(VirtualKey.F, FocusTaskSearchAsync);
        AddShortcut(VirtualKey.F, ShowGlobalSearchAsync, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift);
        AddShortcut((VirtualKey)188, ShowSettingsAsync);
        AddShortcut(VirtualKey.Z, UndoAsync);
        AddShortcut(VirtualKey.Y, RedoAsync);
        AddShortcut(VirtualKey.Z, RedoAsync, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift);
    }

    private void AddShortcut(VirtualKey key, Func<Task> action, VirtualKeyModifiers modifiers = VirtualKeyModifiers.Control)
    {
        var shortcut = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        shortcut.Invoked += async (_, e) => { if (!_dialogOpen && _ready && !_busy) { e.Handled = true; await GuardAsync(action); } };
        Root.KeyboardAccelerators.Add(shortcut);
    }

    private async void Root_Loaded(object sender, RoutedEventArgs e)
    {
        if (_ready || _repository is not null) return;
        if (Environment.GetEnvironmentVariable("PUREPROJECT_UI_SCALE_TEST") == "1")
        {
            await RunScaleUiSmokeTestsAsync();
            return;
        }
        try
        {
            _busy = true;
            SetSaveStatus("正在读取本地数据…");
            _repository = new JsonProjectRepository(Environment.GetEnvironmentVariable("PUREPROJECT_DATA_DIR"));
            _projects = await Task.Run(() => _repository.LoadAsync());
            _settingsRepository = new SettingsRepository(_repository.DataDirectory);
            try { _settings = await _settingsRepository.LoadAsync(); _settingsLoaded = true; }
            catch (Exception settingsError) { ShowMessage("项目已读取，但设置无法加载。设置文件已保留：" + settingsError.Message, InfoBarSeverity.Warning); }
            ApplyTheme();
            _busy = false;
            _ready = true;
            Render();
            SetSaveStatus("本地数据已就绪");
            ShowTemporaryRecoveryNotice();
            StartReminderTimer();
            if (Environment.GetEnvironmentVariable("PUREPROJECT_UI_SMOKE_TEST") == "1" &&
                !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PUREPROJECT_DATA_DIR")))
                await RunUiSmokeTestsAsync();
        }
        catch (Exception ex)
        {
            _busy = false;
            if (Environment.GetEnvironmentVariable("PUREPROJECT_UI_SMOKE_TEST") == "1" &&
                Environment.GetEnvironmentVariable("PUREPROJECT_DATA_DIR") is { Length: > 0 } smokeDirectory)
            {
                Directory.CreateDirectory(smokeDirectory);
                await File.WriteAllTextAsync(Path.Combine(smokeDirectory, "startup-failure.txt"), ex.ToString());
                Environment.ExitCode = 1;
                Application.Current.Exit();
                return;
            }
            SetSaveStatus("数据未加载，已暂停编辑");
            ShowMessage(ex is RepositoryDataException
                ? "项目文件无法读取或校验失败，原文件已保留。请按下方说明检查本地文件。"
                : ex.Message, InfoBarSeverity.Error);
            var panel = Column(16);
            panel.Children.Add(Heading("无法打开本地数据"));
            panel.Children.Add(Label(_repository is null
                ? "请关闭使用同一数据目录的另一个简项窗口后重启，并检查目录的读写权限。"
                : $"数据目录：{_repository.DataDirectory}\n请先复制整个数据目录留存，再检查项目文件与可用的备份。", true));
            if (_repository?.TemporaryRecovery.HasUncommittedFiles == true)
                panel.Children.Add(Label(TemporaryRecoveryInstructions(loaded: false), true));
            if (_repository is not null && ex is RepositoryDataException && File.Exists(_repository.BackupFilePath))
                panel.Children.Add(ActionButton("从上一份本地备份恢复", async () =>
                {
                    if (!await ConfirmAsync("恢复本地备份", "将损坏的数据恢复为最后一次有效备份。恢复前请保留原始文件。")) return;
                    _busy = true;
                    SetSaveStatus("正在恢复本地备份…");
                    try
                    {
                        _projects = await Task.Run(async () => { await _repository.RestoreBackupAsync(); return await _repository.LoadAsync(); });
                        _settingsRepository ??= new SettingsRepository(_repository.DataDirectory);
                        try { _settings = await _settingsRepository.LoadAsync(); _settingsLoaded = true; ApplyTheme(); }
                        catch (Exception settingsError) { ShowMessage("项目已恢复，但设置无法加载：" + settingsError.Message, InfoBarSeverity.Warning); }
                        _ready = true;
                        Render();
                        StartReminderTimer();
                        SetSaveStatus("本地备份已恢复");
                    }
                    finally { _busy = false; OperationStatus.Visibility = Visibility.Collapsed; }
                }));
            ContentHost.Children.Add(panel);
        }
    }

    private void StartReminderTimer()
    {
        if (_reminderTimer is null)
        {
            _reminderTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _reminderTimer.Tick += (_, _) => CheckReminders();
        }
        _reminderTimer.Start();
        CheckReminders();
    }

    private void ShowTemporaryRecoveryNotice()
    {
        if (_repository?.TemporaryRecovery is not { } report
            || (!report.HasUncommittedFiles && !report.ScanLimitReached && string.IsNullOrWhiteSpace(report.ScanWarning))) return;
        var message = report.HasUncommittedFiles
            ? "发现未提交的暂存文件，已保留恢复候选。当前仍使用原主数据。\n" + TemporaryRecoveryInstructions(loaded: true)
            : "部分暂存文件尚未完成检查，已保留原文件。";
        if (report.ScanLimitReached) message += " 本次暂存扫描已达上限。";
        if (!string.IsNullOrWhiteSpace(report.ScanWarning)) message += " " + EditorPreview(report.ScanWarning, 240);
        ShowMessage(message, InfoBarSeverity.Warning);
    }

    private string TemporaryRecoveryInstructions(bool loaded)
    {
        if (_repository is null) return "";
        var candidates = _repository.TemporaryRecovery.Files
            .Where(file => file.Action is not "RemovedEmpty" and not "RemovedRedundant")
            .Select(file => file.FilePath).Take(4).ToArray();
        var locations = candidates.Length > 0
            ? "候选文件：\n" + string.Join("\n", candidates)
            : "待检查目录：" + _repository.DataDirectory;
        var next = loaded
            ? "先复制整个数据目录留存，人工核对候选内容；需要合并时，把确认过的候选复制为新的 .json 文件，再使用导入。"
            : "当前主库未加载。请先退出简项并复制整个数据目录留存，人工核对候选内容；确认要恢复后，将现有 projects.json 另名保留，再把确认过的候选复制为此目录中的 projects.json，重启后检查数据。";
        return locations + "\n" + next + " 未核实的候选可能不完整，请保留原候选和备份。";
    }

    private void ApplyTheme()
    {
        Root.RequestedTheme = _settings.Theme switch { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        _activeDark = Root.ActualTheme == ElementTheme.Dark;
    }

    private async Task GuardAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { ShowMessage(ex.Message, InfoBarSeverity.Error); }
    }

    private async Task CommitAsync(Action<List<Project>> change, string message = "已保存", string? changedProjectId = null, bool collectionOnly = false)
    {
        if (!_ready || _repository is null) throw new InvalidOperationException("数据尚未就绪，请等待加载完成。");
        if (_busy) throw new InvalidOperationException("正在处理另一项操作，请稍后保存。");
        _busy = true;
        SetSaveStatus("正在保存…");
        try
        {
            var before = _projects;
            var next = await Task.Run(() =>
            {
                // Weigh a published version once, away from the dispatcher. Weak
                // cache keys must not themselves retain discarded history.
                foreach (var project in before)
                    _historyWeights.GetValue(project, p => new HistoryWeight(ModelMemoryEstimate.ForProject(p)));
                return before.Select(p => collectionOnly || (changedProjectId is not null && p.Id != changedProjectId) ? p : PmSerializer.Clone(p)).ToList();
            });
            // Form callbacks may read WinUI controls, so apply them on the dispatcher.
            change(next);
            await Task.Run(() => _repository.SaveAsync(next));
            _undo.Push(before);
            _redo.Clear();
            _projects = next;
            TrimHistory();
            Render();
            SetSaveStatus($"{message} · {DateTime.Now:HH:mm:ss}");
        }
        catch (IOException error)
        {
            SetSaveStatus("保存失败 · 原数据保持完整");
            var reason = (error.HResult & 0xffff) is 32 or 33
                ? "数据文件正被其他程序占用。请关闭占用该文件的程序后重试。"
                : "暂时无法写入数据目录。请检查目录是否可用及磁盘空间后重试。";
            throw new InvalidOperationException(reason + "原数据未改变。", error);
        }
        catch (UnauthorizedAccessException error)
        {
            SetSaveStatus("保存失败 · 原数据保持完整");
            throw new InvalidOperationException("当前数据目录没有写入权限。请检查目录权限后重试，原数据未改变。", error);
        }
        catch
        {
            SetSaveStatus("保存失败 · 原数据保持完整");
            throw;
        }
        finally { _busy = false; OperationStatus.Visibility = Visibility.Collapsed; }
    }

    private Task ChangeAsync(string projectId, Action<Project> change, string message = "已保存") =>
        CommitAsync(projects =>
        {
            var p = projects.Single(x => x.Id == projectId);
            change(p);
            p.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");
        }, message, changedProjectId: projectId);

    private void TrimHistory()
    {
        // Limit steps, task versions and estimated model bytes beyond live data.
        // This retention weight includes long text and is not a process-memory cap.
        // Reference identity matters: records compare values but versions must not.
        var retained = new HashSet<Project>(ReferenceEqualityComparer.Instance);
        foreach (var project in _projects) retained.Add(project);
        var taskVersions = 0;
        long retainedBytes = 0;
        foreach (var stack in new[] { _undo, _redo })
        {
            var keep = new List<List<Project>>();
            foreach (var snapshot in stack)
            {
                var newVersions = snapshot.Where(p => !retained.Contains(p)).ToArray();
                var added = newVersions.Sum(p => p.Tasks.Count);
                var addedBytes = newVersions.Sum(p => _historyWeights.GetValue(p, item => new HistoryWeight(ModelMemoryEstimate.ForProject(item))).Bytes);
                if (keep.Count >= MaximumHistorySteps || taskVersions + added > MaximumRetainedTaskVersions
                    || retainedBytes + addedBytes > MaximumRetainedHistoryBytes) break;
                keep.Add(snapshot); taskVersions += added; retainedBytes += addedBytes;
                foreach (var project in snapshot) retained.Add(project);
            }
            stack.Clear();
            foreach (var snapshot in keep.AsEnumerable().Reverse()) stack.Push(snapshot);
        }
    }

    private async Task RestoreHistoryAsync(Stack<List<Project>> from, Stack<List<Project>> to)
    {
        if (from.Count == 0 || _busy || _repository is null) return;
        _busy = true;
        try
        {
            SetSaveStatus("正在恢复历史并保存…");
            var next = from.Peek();
            await Task.Run(async () =>
            {
                foreach (var project in _projects)
                    _historyWeights.GetValue(project, p => new HistoryWeight(ModelMemoryEstimate.ForProject(p)));
                await _repository.SaveAsync(next);
            });
            from.Pop();
            to.Push(_projects);
            _projects = next;
            TrimHistory();
            Render();
            SetSaveStatus(ReferenceEquals(from, _undo) ? "已撤销 · 已保存" : "已重做 · 已保存");
        }
        catch { SetSaveStatus("历史恢复未完成 · 当前数据已保留"); throw; }
        finally { _busy = false; OperationStatus.Visibility = Visibility.Collapsed; }
    }
    private Task UndoAsync() => RestoreHistoryAsync(_undo, _redo);
    private Task RedoAsync() => RestoreHistoryAsync(_redo, _undo);
    private async void Undo_Click(object sender, RoutedEventArgs e) => await GuardAsync(UndoAsync);
    private async void Redo_Click(object sender, RoutedEventArgs e) => await GuardAsync(RedoAsync);

    private void Render()
    {
        if (!_ready) return;
        var focus = CaptureUiFocus();
        _rendering = true;
        try
        {
            if (Current is null) _selectedId = null;
            RenderShell();
            ProjectToolbar.Visibility = Current is null ? Visibility.Collapsed : Visibility.Visible;
            if (Current is { } current)
            {
                PageTitle.Text = SingleLinePreview(current.Name, 128); PageTitle.MaxLines = 1;
                PageSubtitle.Text = string.IsNullOrWhiteSpace(current.Description) ? "把计划化为清晰的下一步。" : EditorPreview(current.Description, 360);
                var groups = current.TaskGroups.OrderBy(g => g.Archived).ThenBy(g => g.SortOrder).ToList();
                if (_view > 0) groups.Insert(0, new TaskGroup { Id = "", Name = "全部任务组" });
                if ((_view == 0 && string.IsNullOrEmpty(_groupId)) ||
                    (!string.IsNullOrEmpty(_groupId) && !groups.Any(g => g.Id == _groupId)))
                    _groupId = current.DefaultTaskGroupId;
                GroupSelector.ItemsSource = groups;
                GroupSelector.SelectedItem = groups.FirstOrDefault(g => g.Id == (_groupId ?? ""));
                ViewSelector.SelectedIndex = _view;
            }
            else { PageTitle.Text = "项目总览"; PageSubtitle.Text = "专注当下，让每件事稳步向前。"; }
            UndoButton.IsEnabled = _undo.Count > 0;
            RedoButton.IsEnabled = _redo.Count > 0;
        }
        finally { _rendering = false; }
        RenderContent();
        RestoreUiFocus(focus);
    }

    private void Group_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (!_rendering && GroupSelector.SelectedItem is TaskGroup g) { _groupId = string.IsNullOrEmpty(g.Id) ? null : g.Id; RenderContent(); } }
    private void View_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (!_rendering && _ready) { _view = ViewSelector.SelectedIndex; Render(); } }
    private void Filter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    { if (!_rendering && _ready) RenderContent(); }
    private void Search_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    { if (_ready && args.Reason == AutoSuggestionBoxTextChangeReason.UserInput) RenderContent(); }
    private async void NewProject_Click(object sender, RoutedEventArgs e) => await GuardAsync(() => EditProjectAsync());
    private async void NewTask_Click(object sender, RoutedEventArgs e) => await GuardAsync(() => EditTaskAsync(null));
    private async void ManageProject_Click(object sender, RoutedEventArgs e) => await GuardAsync(ManageProjectAsync);
    private async void Import_Click(object sender, RoutedEventArgs e) => await GuardAsync(ImportAsync);

    private IEnumerable<ProjectTask> VisibleTasks(Project p)
    {
        var query = SearchBox.Text?.Trim() ?? "";
        var priority = PrioritySelector.SelectedIndex switch { 1 => "high", 2 => "medium", 3 => "low", _ => null };
        return p.Tasks.Where(t => (string.IsNullOrEmpty(_groupId) ? p.TaskGroups.Any(g => g.Id == t.TaskGroupId && !g.Archived) : t.TaskGroupId == _groupId)
            && (priority is null || t.Priority == priority)
            && (query.Length == 0 || (t.Title + " " + t.Description + " " + string.Join(" ", t.Tags) + " " +
                p.TaskGroups.FirstOrDefault(g => g.Id == t.TaskGroupId)?.Name).Contains(query, StringComparison.OrdinalIgnoreCase)));
    }
    private static TaskStatusDefinition? Status(Project p, ProjectTask t) =>
        p.TaskGroups.FirstOrDefault(g => g.Id == t.TaskGroupId)?.Statuses.FirstOrDefault(s => s.Id == t.StatusId);
    private static bool IsTaskClosed(Project p, ProjectTask t) => Status(p, t)?.Category is "done" or "cancelled";
    private static IEnumerable<ProjectTask> ActiveTasks(Project p) =>
        p.Tasks.Where(t => p.TaskGroups.Any(g => g.Id == t.TaskGroupId && !g.Archived));
    private static bool Overdue(Project p, ProjectTask t) => !IsTaskClosed(p, t) && !string.IsNullOrWhiteSpace(t.DueDate) &&
        DateTime.TryParse($"{t.DueDate} {t.DueTime ?? "23:59"}", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) && date < DateTime.Now;
    private void CheckReminders()
    {
        var due = _projects.Where(p => !p.Archived).SelectMany(p => ActiveTasks(p).Where(t => !IsTaskClosed(p, t)))
            .Where(t => DateTimeOffset.TryParse(t.Reminder, out var date) && date <= DateTimeOffset.Now && _shownReminders.Add(t.Id + t.Reminder)).ToList();
        if (due.Count > 0) ShowMessage("任务提醒：" + string.Join("、", due.Select(t => t.Title)), InfoBarSeverity.Warning);
    }
    private void ShowMessage(string message, InfoBarSeverity severity = InfoBarSeverity.Informational)
    { Notice.Message = message; Notice.Severity = severity; Notice.IsOpen = true; }

    private static StackPanel Column(double spacing = 12) => new() { Spacing = spacing };
    private static StackPanel Row(double spacing = 8) => new() { Orientation = Orientation.Horizontal, Spacing = spacing };
    private static TextBlock Label(string text, bool wrap = false) => new() { Text = text, FontFamily = UiFont, FontSize = 13, Foreground = ThemeBrush("PrimaryTextBrush"), TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap };
    private static TextBlock Heading(string text, double size = 20) => new() { Text = text, FontFamily = SerifFont, Foreground = ThemeBrush("PrimaryTextBrush"), FontSize = size, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private static Brush ColorBrush(string? value)
    {
        try
        {
            var hex = value?.TrimStart('#');
            if (hex?.Length == 6) return new SolidColorBrush(Windows.UI.Color.FromArgb(255,
                Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16)));
        }
        catch (FormatException) { }
        return ThemeBrush("TextFillColorSecondaryBrush");
    }
    private static Border Card(UIElement child, Thickness? padding = null)
    {
        var border = (Border)Microsoft.UI.Xaml.Markup.XamlReader.Load("""
            <Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                CornerRadius="2" BorderThickness="1" Background="{ThemeResource CardBackgroundBrush}"
                BorderBrush="{ThemeResource BorderBrush}" />
            """);
        border.Child = child; border.Padding = padding ?? new Thickness(20); return border;
    }
    private Button ActionButton(string text, Func<Task> action, bool accent = false)
    {
        var button = new Button { Content = text };
        if (accent) button.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        button.Click += async (_, _) => await GuardAsync(action);
        return button;
    }
    private static ScrollViewer Scroll(UIElement content, bool horizontal = false) => new()
    {
        Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = horizontal ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
        HorizontalScrollMode = horizontal ? ScrollMode.Enabled : ScrollMode.Disabled
    };
    private async Task<ContentDialogResult> DialogAsync(ContentDialog dialog)
    {
        if (_dialogOpen || _busy) return ContentDialogResult.None;
        var focus = CaptureUiFocus();
        dialog.XamlRoot = Root.XamlRoot;
        dialog.RequestedTheme = Root.RequestedTheme;
        ConfigureDialogStyle(dialog);
        var drawer = Equals(dialog.Tag, "TaskDetail");
        if (drawer) MainPane.Margin = new Thickness(0, 0, Math.Min(340, Root.ActualWidth - 264), 0);
        _dialogOpen = true;
        _currentActiveDialog = dialog;
        try { return await dialog.ShowAsync(); }
        finally
        {
            _dialogOpen = false; _currentActiveDialog = null;
            if (drawer) MainPane.Margin = new Thickness(0);
            RestoreUiFocus(focus, Current is null ? "DashboardNavigation" : "ProjectView:" + _view);
        }
    }
    private async Task<bool> ConfirmAsync(string title, string message) => await DialogAsync(new ContentDialog
    { Title = title, Content = Label(message, true), PrimaryButtonText = "确认", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close }) == ContentDialogResult.Primary;
    private async Task<string?> PromptAsync(string title, string value = "", Func<string, string>? validate = null, int maximumLength = 500)
    {
        var input = new TextBox { Header = "名称", MinWidth = 360, MaxLength = maximumLength,
            AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 144, Text = value };
        var inputValue = PreserveInitialText(input, value);
        var error = new InfoBar { Severity = InfoBarSeverity.Error, IsOpen = false, Visibility = Visibility.Collapsed };
        var content = Column(8); content.Children.Add(input); content.Children.Add(error);
        var validation = new EditorValidation(error);
        string? validated = null;
        var dialog = new ContentDialog { Title = title, Content = content, PrimaryButtonText = "保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary, IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(value) };
        input.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(input.Text);
        dialog.Opened += (_, _) => input.Focus(FocusState.Programmatic);
        dialog.PrimaryButtonClick += (_, args) =>
        {
            try { validated = validate is null ? input.Text.Trim() : validate(inputValue()); validation.Clear(); }
            catch (Exception ex) { args.Cancel = true; validation.Show(ex.Message, input); }
        };
        return await DialogAsync(dialog) == ContentDialogResult.Primary ? validated : null;
    }
}
