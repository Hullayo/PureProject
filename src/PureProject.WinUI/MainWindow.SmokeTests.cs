using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PureProject.Core;
using PureProject.Infrastructure;
using Windows.Graphics.Imaging;
using Windows.Security.Cryptography;
using Windows.Storage;

namespace PureProject.WinUI;

public sealed partial class MainWindow
{
    /// <summary>
    /// Opt-in integration checks running on this window's UI thread. This uses only the
    /// application's own controls and render tree, never the desktop or another process.
    /// </summary>
    private async Task RunUiSmokeTestsAsync()
    {
        var started = DateTimeOffset.UtcNow;
        var steps = new List<UiSmokeStep>();
        var screenshots = new List<UiSmokeScreenshot>();
        string? directory = null;
        string? failure = null;
        string? failureScreenshotError = null;
        var currentStep = "Verify isolated test directory";
        try
        {
            SmokeAssert(Environment.GetEnvironmentVariable("PUREPROJECT_UI_SMOKE_TEST") == "1", "UI smoke-test opt-in is missing.");
            var configured = Environment.GetEnvironmentVariable("PUREPROJECT_DATA_DIR");
            SmokeAssert(!string.IsNullOrWhiteSpace(configured), "An explicit isolated data directory is required.");
            var candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(configured!));
            SmokeAssert(_repository is not null && _ready, "The repository is not ready.");
            var repository = _repository ?? throw new InvalidOperationException("The repository is unavailable.");
            SmokeAssert(string.Equals(candidate, Path.TrimEndingDirectorySeparator(repository.DataDirectory), StringComparison.OrdinalIgnoreCase), "The test and repository directories differ.");
            SmokeAssert(!string.Equals(candidate, Path.TrimEndingDirectorySeparator(Path.GetFullPath(JsonProjectRepository.DefaultDataDirectory)), StringComparison.OrdinalIgnoreCase), "Smoke tests must never run in the normal user data directory.");
            SmokeAssert(_projects.Count == 0 && !File.Exists(repository.DataFilePath) && !File.Exists(repository.BackupFilePath), "The smoke-test directory must have no existing project data.");
            SmokeAssert(Directory.EnumerateFileSystemEntries(candidate).All(path => Path.GetFileName(path) == "session.lock"), "The smoke-test directory must be fresh and contain only its session lock.");
            directory = candidate;
            await File.WriteAllTextAsync(Path.Combine(directory, "ui-smoke-owned.txt"), "PureProject isolated in-process UI smoke test\n" + started.ToString("O"));
            _reminderTimer?.Stop();
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 890));
            await SmokeWaitAsync(() => AppWindow.Size.Width == 1440 && AppWindow.Size.Height == 890, "The test window did not resize to the standard screenshot dimensions.");
            await SmokeLayoutAsync();

            async Task Step(string name, Func<Task> action)
            {
                currentStep = name;
                // Flush fixed scenario names at each boundary so a native crash can be
                // located without recording control values, drafts, or credentials.
                var progressPath = Path.Combine(directory, "ui-smoke-progress.log");
                File.AppendAllText(progressPath, $"{DateTimeOffset.UtcNow:O}\tSTART\t{name}{Environment.NewLine}");
                var elapsed = Stopwatch.StartNew();
                await action();
                steps.Add(new(name, elapsed.Elapsed.TotalMilliseconds));
                File.AppendAllText(progressPath, $"{DateTimeOffset.UtcNow:O}\tPASS\t{name}\t{elapsed.Elapsed.TotalMilliseconds:F1} ms{Environment.NewLine}");
            }

            var projectId = "";
            var primaryGroupId = "";
            var secondaryGroupId = "";
            var milestoneId = "";
            var today = DateOnly.FromDateTime(DateTime.Today);
            await Step("Create project, three task states, dependencies and a milestone through CommitAsync", async () =>
            {
                await CommitAsync(projects =>
                {
                    var project = _service.CreateProject("简项 · 原生重构验收", "用 C# 与 WinUI 3 管理计划、任务与交付。", "#A33B32");
                    projectId = project.Id;
                    primaryGroupId = project.DefaultTaskGroupId;
                    var group = project.TaskGroups.Single(g => g.Id == primaryGroupId);
                    var planning = _service.CreateTask(project, "定义功能范围与交付清单", primaryGroupId, group.CompletionStatusId);
                    planning.Description = "完成需求确认、功能分组和验收标准。"; planning.Priority = "high";
                    planning.StartOffset = 0; planning.DueDate = today.AddDays(2).ToString("yyyy-MM-dd");
                    var implementation = _service.CreateTask(project, "实现 WinUI 3 原生任务编辑器", primaryGroupId, group.Statuses.Single(s => s.Category == "active").Id);
                    implementation.Description = "打通任务详情、评论、依赖、循环规则与本地持久化。";
                    implementation.Priority = "high"; implementation.StartOffset = 2; implementation.DueDate = today.AddDays(5).ToString("yyyy-MM-dd");
                    _service.AddDependency(project, implementation.Id, planning.Id);
                    _service.AddSubtask(project, implementation.Id, "完成表单与输入校验").Done = true;
                    _service.AddSubtask(project, implementation.Id, "完成原生界面集成验证");
                    _service.AddComment(project, implementation.Id, "本条评论用于验证编辑器保留已有内容。");
                    var release = _service.CreateTask(project, "完成数据兼容与上线验收", primaryGroupId);
                    release.StartOffset = 5; release.DueDate = today.AddDays(10).ToString("yyyy-MM-dd");
                    _service.AddDependency(project, release.Id, implementation.Id);
                    milestoneId = Guid.NewGuid().ToString();
                    project.Milestones.Add(new Milestone { Id = milestoneId, Title = "原生客户端验收", Date = today.AddDays(10).ToString("yyyy-MM-dd"), Color = "#A33B32", Description = "全部检查通过后完成交付。" });
                    projects.Add(project); _selectedId = projectId; _groupId = primaryGroupId;
                });
                await SmokeLayoutAsync();
                SmokeAssert(_projects.Single().Tasks.Count == 3 && File.Exists(repository.DataFilePath), "The first commit was not persisted.");
            });

            await Step("Load the bundled Harmony font and bind visible application text to it", async () =>
            {
                var fontPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", "HarmonyOS_Sans_SC.ttf");
                SmokeAssert(File.Exists(fontPath), "The published application is missing its bundled HarmonyOS font.");
                var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(fontPath)));
                SmokeAssert(digest == "8978E05044E7089AD6A9DE38C505C8148305607983487435A916D2610700A7CA", "The published HarmonyOS font differs from the verified local asset.");
                SmokeAssert(Application.Current.Resources["AppFontFamily"] is FontFamily family && family.Source == AppFontSource,
                    "The application font resource does not resolve to the bundled HarmonyOS family.");
                SmokeHarmonyText(Root);
            });

            await Step("Create a task group through the visible button, validate empty input and cancel without changing JSON", async () =>
            {
                _view = 0; _groupId = primaryGroupId; Render(); await SmokeLayoutAsync();
                var before = await File.ReadAllTextAsync(repository.DataFilePath);
                var create = SmokeFind<Button>(ContentHost, button => AutomationProperties.GetAutomationId(button) == "TaskGroupCreate");
                SmokeControlWithinRoot(create, "New task group");
                SmokeInvoke(create);
                var dialog = await SmokeWaitForDialogAsync();
                SmokeAssert(Equals(dialog.Tag, "TaskGroupEditor"), "The visible task-group create button did not open its editor.");
                var input = SmokeFind<TextBox>(dialog, field => AutomationProperties.GetAutomationId(field) == "TaskGroupName");
                SmokeInvoke(SmokeDialogButton(dialog, "PrimaryButton", "创建"));
                await SmokeInvalidFieldAsync(dialog, input);
                SmokeAssert(ReferenceEquals(_currentActiveDialog, dialog) && _projects.Single().TaskGroups.Count == 1,
                    "Empty task-group input closed the editor or changed the project.");
                screenshots.Add(await SmokeCaptureAsync(directory, "task-group-validation.png", dialog));
                input.Text = "本次任务组草稿不得保存";
                SmokeInvoke(SmokeDialogButton(dialog, "CloseButton", "取消"));
                await SmokeWaitAsync(() => !_dialogOpen && !_busy, "Cancelling task-group creation did not close the editor.");
                SmokeAssert(_projects.Single().TaskGroups.Count == 1 && await File.ReadAllTextAsync(repository.DataFilePath) == before,
                    "Cancelling task-group creation changed the persisted project.");

                SmokeInvoke(SmokeFind<Button>(ContentHost, button => AutomationProperties.GetAutomationId(button) == "TaskGroupCreate"));
                dialog = await SmokeWaitForDialogAsync();
                input = SmokeFind<TextBox>(dialog, field => AutomationProperties.GetAutomationId(field) == "TaskGroupName");
                input.Text = "设计评审";
                SmokeInvoke(SmokeDialogButton(dialog, "PrimaryButton", "创建"));
                await SmokeWaitAsync(() => !_dialogOpen && !_busy && _projects.Single().TaskGroups.Count == 2, "The visible task-group create action did not commit its new group.");
                await SmokeLayoutAsync();
                secondaryGroupId = _projects.Single().TaskGroups.Single(group => group.Name == "设计评审").Id;
                var stored = PmSerializer.ParseProjects(await File.ReadAllTextAsync(repository.DataFilePath));
                SmokeAssert(_groupId == secondaryGroupId && stored.Single().TaskGroups.Any(group => group.Id == secondaryGroupId && group.Name == "设计评审"),
                    "The new task group was not selected and persisted.");
                SmokeControlWithinRoot(SmokeFind<Button>(ContentHost, button => AutomationProperties.GetAutomationId(button) == "KanbanGroup:" + secondaryGroupId), "Created task-group choice");
                screenshots.Add(await SmokeCaptureAsync(directory, "task-group-created.png"));
                _groupId = primaryGroupId; Render(); await SmokeLayoutAsync();
            });

            if (Environment.GetEnvironmentVariable("PUREPROJECT_UI_CONSISTENCY_ONLY") == "1")
            {
                foreach (var theme in new[] { "Light", "Dark" })
                {
                    _settings = _settings with { Theme = theme }; ApplyTheme(); Render(); await SmokeLayoutAsync();
                    await Step("Inspect native calendar, number spin and checkbox states in " + theme,
                        () => SmokeConsistencyTaskControlsAsync(directory, theme, projectId, primaryGroupId, screenshots));
                    await Step("Inspect nested task menus and the existing action tooltip in " + theme,
                        () => SmokeConsistencyMenusAsync(directory, theme, projectId, primaryGroupId, screenshots));
                    await Step("Inspect all notification severities in " + theme,
                        () => SmokeConsistencyNoticesAsync(directory, theme, screenshots));
                }
                return;
            }

            await Step("Open native task drawer, invoke validation, then save using its own automation peer", async () =>
            {
                var editing = EditTaskAsync(null, projectId);
                var dialog = await SmokeWaitForDialogAsync(editing);
                var title = SmokeFind<TextBox>(dialog, field => Equals(field.Header, "任务标题"));
                title.Text = "";
                SmokeInvoke(SmokeDialogButton(dialog, "PrimaryButton", "保存"));
                await SmokeWaitAsync(() => SmokeDescendants<InfoBar>(dialog).Any(bar => bar.IsOpen && bar.Severity == InfoBarSeverity.Error), "The empty-title validation did not appear.");
                await SmokeInvalidFieldAsync(dialog, title);
                SmokeAssert(_projects.Single().Tasks.Count == 3, "Invalid form input changed project data.");
                title.Text = "通过原生对话框保存的验收任务";
                SmokeFind<TextBox>(dialog, field => Equals(field.Header, "说明")).Text = "此任务由应用内 ContentDialog 和保存按钮自动化提供程序创建。";
                SmokeFind<CalendarDatePicker>(dialog, field => Equals(field.Header, "截止日期")).Date = DateTimeOffset.Now.AddDays(7);
                screenshots.Add(await SmokeCaptureAsync(directory, "task-dialog.png", dialog));
                SmokeInvoke(SmokeDialogButton(dialog, "PrimaryButton", "保存"));
                await SmokeAwaitDialogAsync(editing, dialog);
                SmokeAssert(_projects.Single().Tasks.Any(task => task.Title == "通过原生对话框保存的验收任务"), "The native Save button did not commit the task.");
            });

            await Step("Undo and redo using the native button automation providers", async () =>
            {
                SmokeInvoke(UndoButton);
                await SmokeWaitAsync(() => !_busy && _projects.Single().Tasks.Count == 3, "Undo did not restore the three-task snapshot.");
                SmokeAssert(RedoButton.IsEnabled, "Redo button remained disabled.");
                SmokeInvoke(RedoButton);
                await SmokeWaitAsync(() => !_busy && _projects.Single().Tasks.Count == 4, "Redo did not restore the saved task.");
            });

            await Step("Cancel a task edit without persisting its draft", async () =>
            {
                var task = _projects.Single().Tasks.Single(t => t.Title == "通过原生对话框保存的验收任务");
                var editing = EditTaskAsync(task.Id, projectId);
                var dialog = await SmokeWaitForDialogAsync(editing);
                SmokeFind<TextBox>(dialog, field => Equals(field.Header, "任务标题")).Text = "此草稿必须被取消";
                SmokeInvoke(SmokeHeaderCloseButton(dialog));
                await SmokeAwaitDialogAsync(editing, dialog);
                SmokeAssert(_projects.Single().Tasks.Single(t => t.Id == task.Id).Title == task.Title, "Cancel persisted an unsaved edit.");
            });

            await Step("Save a milestone and cancel its timeline delete action with focus restored", async () =>
            {
                var editing = EditMilestoneAsync(projectId, milestoneId);
                var dialog = await SmokeWaitForDialogAsync(editing);
                SmokeFind<TextBox>(dialog, field => Equals(field.Header, "里程碑名称")).Text = "原生客户端验收 · 已检查";
                SmokeInvoke(SmokeDialogButton(dialog, "PrimaryButton", "保存"));
                await SmokeAwaitDialogAsync(editing, dialog);
                SmokeAssert(_projects.Single().Milestones.Single(m => m.Id == milestoneId).Title.EndsWith("已检查", StringComparison.Ordinal), "Milestone dialog did not save.");
                var originalView = _view; var originalGroup = _groupId;
                var beforeDelete = await File.ReadAllTextAsync(repository.DataFilePath);
                var undoBeforeDelete = _undo.Count; var redoBeforeDelete = _redo.Count;
                _view = 3; Render(); await SmokeLayoutAsync();
                var remove = SmokeFind<Button>(ContentHost, button => AutomationProperties.GetAutomationId(button) == "MilestoneDelete:" + milestoneId);
                SmokeControlWithinRoot(remove, "Timeline milestone delete action");
                SmokeInvoke(remove);
                var confirmation = await SmokeWaitForDialogAsync();
                SmokeAssert(confirmation.Title?.ToString()?.Contains("删除里程碑", StringComparison.Ordinal) == true, "Timeline milestone delete skipped confirmation.");
                SmokeInvoke(SmokeDialogButton(confirmation, "CloseButton", "取消"));
                await SmokeWaitAsync(() => !_dialogOpen && !_busy, "Cancelling timeline milestone deletion did not close the confirmation.");
                await SmokeLayoutAsync();
                var returnedOrigin = SmokeFind<Button>(ContentHost, button => AutomationProperties.GetAutomationId(button) == "MilestoneDelete:" + milestoneId);
                SmokeAssert(SmokeContainsFocus(returnedOrigin), "Cancelling timeline milestone deletion did not restore its originating button focus.");
                SmokeAssert(_undo.Count == undoBeforeDelete && _redo.Count == redoBeforeDelete && await File.ReadAllTextAsync(repository.DataFilePath) == beforeDelete,
                    "Cancelling timeline milestone deletion changed data or history.");
                _view = originalView; _groupId = originalGroup; Render(); await SmokeLayoutAsync();
            });

            await Step("Cancel task and milestone deletion without losing their open editor drafts", async () =>
            {
                var before = await File.ReadAllTextAsync(repository.DataFilePath);
                var undoCount = _undo.Count;
                var task = _projects.Single().Tasks.Single(item => item.Title == "通过原生对话框保存的验收任务");
                var editing = EditTaskAsync(task.Id, projectId);
                var original = await SmokeWaitForDialogAsync(editing);
                SmokeFind<TextBox>(original, field => Equals(field.Header, "任务标题")).Text = "取消删除后仍保留的任务草稿";
                SmokeFind<TextBox>(original, field => Equals(field.Header, "新增评论")).Text = "取消删除不得丢弃这条待保存评论";
                SmokeInvoke(SmokeDialogButton(original, "SecondaryButton", "删除"));
                var confirmation = await SmokeWaitForDialogAsync(editing, original);
                SmokeAssert(confirmation.Title?.ToString()?.Contains("删除任务", StringComparison.Ordinal) == true, "Task deletion did not open its confirmation.");
                SmokeInvoke(SmokeDialogButton(confirmation, "CloseButton", "取消"));
                var restored = await SmokeWaitForDialogAsync(editing, confirmation);
                SmokeAssert(ReferenceEquals(restored, original)
                    && SmokeFind<TextBox>(restored, field => Equals(field.Header, "任务标题")).Text == "取消删除后仍保留的任务草稿"
                    && SmokeFind<TextBox>(restored, field => Equals(field.Header, "新增评论")).Text == "取消删除不得丢弃这条待保存评论",
                    "Cancelling task deletion discarded or rebuilt the open draft.");
                SmokeAssert(_undo.Count == undoCount && await File.ReadAllTextAsync(repository.DataFilePath) == before, "Cancelling task deletion changed persisted data or history.");
                SmokeFind<TextBox>(restored, field => Equals(field.Header, "任务标题")).Focus(FocusState.Keyboard);
                screenshots.Add(await SmokeCaptureAsync(directory, "task-delete-cancel-draft.png", restored));
                SmokeInvoke(SmokeHeaderCloseButton(restored)); await SmokeAwaitDialogAsync(editing, restored);

                editing = EditMilestoneAsync(projectId, milestoneId);
                original = await SmokeWaitForDialogAsync(editing);
                SmokeFind<TextBox>(original, field => Equals(field.Header, "里程碑名称")).Text = "取消删除后仍保留的里程碑草稿";
                SmokeFind<TextBox>(original, field => Equals(field.Header, "说明")).Text = "取消删除必须返回本条说明草稿。";
                SmokeInvoke(SmokeDialogButton(original, "SecondaryButton", "删除"));
                confirmation = await SmokeWaitForDialogAsync(editing, original);
                SmokeAssert(confirmation.Title?.ToString()?.Contains("删除里程碑", StringComparison.Ordinal) == true, "Milestone deletion did not open its confirmation.");
                SmokeInvoke(SmokeDialogButton(confirmation, "CloseButton", "取消"));
                restored = await SmokeWaitForDialogAsync(editing, confirmation);
                SmokeAssert(ReferenceEquals(restored, original)
                    && SmokeFind<TextBox>(restored, field => Equals(field.Header, "里程碑名称")).Text == "取消删除后仍保留的里程碑草稿"
                    && SmokeFind<TextBox>(restored, field => Equals(field.Header, "说明")).Text == "取消删除必须返回本条说明草稿。",
                    "Cancelling milestone deletion discarded or rebuilt the open draft.");
                SmokeAssert(_undo.Count == undoCount && await File.ReadAllTextAsync(repository.DataFilePath) == before, "Cancelling milestone deletion changed persisted data or history.");
                screenshots.Add(await SmokeCaptureAsync(directory, "milestone-delete-cancel-draft.png", restored));
                SmokeInvoke(SmokeHeaderCloseButton(restored)); await SmokeAwaitDialogAsync(editing, restored);
            });

            await Step("Use the visible task-more menu to edit, reorder, move, copy and delete with persisted results", async () =>
            {
                _view = 1; _groupId = null; Render(); await SmokeLayoutAsync();
                var original = PmSerializer.CloneTask(_projects.Single().Tasks.Single(task => task.Title == "通过原生对话框保存的验收任务"));
                var taskId = original.Id;
                var originalOrder = _projects.Single().Tasks.Select(task => task.Id).ToArray();
                var unchangedJson = await File.ReadAllTextAsync(repository.DataFilePath);
                var unchangedUndo = _undo.Count; var unchangedRedo = _redo.Count;
                foreach (var (boundaryTaskId, action) in new[]
                {
                    (originalOrder[0], "up"), (originalOrder[^1], "down"),
                    (taskId, "status:" + original.StatusId), (taskId, "priority:" + original.Priority)
                })
                {
                    var (menu, item) = await SmokeTaskMenuItemAsync(boundaryTaskId, action);
                    var peer = FrameworkElementAutomationPeer.CreatePeerForElement(item);
                    SmokeAssert(!item.IsEnabled && peer is not null && !peer.IsEnabled() && !item.Focus(FocusState.Keyboard),
                        $"Current or boundary menu action '{action}' is not natively disabled.");
                    if (peer!.GetPattern(PatternInterface.Invoke) is not IInvokeProvider disabledInvoke)
                        throw new InvalidOperationException("The menu item lost its native invoke contract.");
                    try { disabledInvoke.Invoke(); }
                    catch (Exception error) when (unchecked((uint)error.HResult) == 0x80040200) { /* UIA_E_ELEMENTNOTENABLED */ }
                    await SmokeLayoutAsync();
                    SmokeAssert(!_busy && !_dialogOpen && _undo.Count == unchangedUndo && _redo.Count == unchangedRedo
                        && await File.ReadAllTextAsync(repository.DataFilePath) == unchangedJson,
                        $"Disabled menu action '{action}' changed project data or history.");
                    menu.Hide(); await SmokeLayoutAsync();
                }
                await SmokeTaskMenuActionAsync(taskId, "delete");
                var cancelledDelete = await SmokeWaitForDialogAsync();
                SmokeInvoke(SmokeDialogButton(cancelledDelete, "CloseButton", "取消"));
                await SmokeWaitAsync(() => !_dialogOpen && !_busy, "Cancelling task-more Delete did not close its confirmation.");
                await SmokeLayoutAsync();
                var menuOrigin = SmokeFind<Button>(ContentHost, button => AutomationProperties.GetAutomationId(button) == "TaskMore:" + taskId);
                SmokeAssert(SmokeContainsFocus(menuOrigin), "Cancelling a task-more action did not return focus to the originating More button.");
                SmokeAssert(_undo.Count == unchangedUndo && _redo.Count == unchangedRedo && await File.ReadAllTextAsync(repository.DataFilePath) == unchangedJson,
                    "Cancelling task-more Delete changed data or history.");
                ProjectTask Current(Project project) => project.Tasks.Single(task => task.Id == taskId);
                async Task Changed(string action, Func<Project, bool> expected)
                {
                    await SmokeTaskMenuActionAsync(taskId, action);
                    await SmokeWaitAsync(() => !_busy && !_dialogOpen && expected(_projects.Single()), $"Task menu action '{action}' did not update project state.");
                    var stored = PmSerializer.ParseProjects(await File.ReadAllTextAsync(repository.DataFilePath)).Single();
                    SmokeAssert(expected(stored), $"Task menu action '{action}' did not reach the persisted JSON.");
                    await SmokeLayoutAsync();
                }
                async Task Rename(string title)
                {
                    await SmokeTaskMenuActionAsync(taskId, "rename");
                    var prompt = await SmokeWaitForDialogAsync();
                    SmokeFind<TextBox>(prompt, field => Equals(field.Header, "名称")).Text = title;
                    SmokeInvoke(SmokeDialogButton(prompt, "PrimaryButton", "保存"));
                    await SmokeWaitAsync(() => !_busy && !_dialogOpen && Current(_projects.Single()).Title == title, "Task-more Rename did not save its prompt.");
                    SmokeAssert(Current(PmSerializer.ParseProjects(await File.ReadAllTextAsync(repository.DataFilePath)).Single()).Title == title, "Task-more Rename was not persisted.");
                    await SmokeLayoutAsync();
                }

                await SmokeTaskMenuActionAsync(taskId, "open");
                var dialog = await SmokeWaitForDialogAsync();
                SmokeAssert(Equals(dialog.Tag, "TaskDetail") && SmokeFind<TextBox>(dialog, field => Equals(field.Header, "任务标题")).Text == original.Title,
                    "Task-more Open did not open the selected task.");
                SmokeInvoke(SmokeHeaderCloseButton(dialog));
                await SmokeWaitAsync(() => !_dialogOpen && !_busy, "Closing the task-more editor did not return to the task list.");
                await SmokeLayoutAsync();
                await Rename("任务更多菜单重命名验收");
                await Rename(original.Title);
                await Changed("toggle", project => IsDone(project, Current(project)));
                await Changed("toggle", project => !IsDone(project, Current(project)) && Current(project).StatusId == original.StatusId);
                await Changed("priority:high", project => Current(project).Priority == "high");
                await Changed("priority:" + original.Priority, project => Current(project).Priority == original.Priority);
                var activeStatusId = _projects.Single().TaskGroups.Single(group => group.Id == primaryGroupId).Statuses.Single(status => status.Category == "active").Id;
                await Changed("status:" + activeStatusId, project => Current(project).StatusId == activeStatusId);
                await Changed("status:" + original.StatusId, project => Current(project).StatusId == original.StatusId);
                var secondaryStatusId = _projects.Single().TaskGroups.Single(group => group.Id == secondaryGroupId).InitialStatusId;
                await Changed($"move:{secondaryGroupId}:{secondaryStatusId}", project => Current(project).TaskGroupId == secondaryGroupId && Current(project).StatusId == secondaryStatusId);
                await Changed($"move:{primaryGroupId}:{original.StatusId}", project => Current(project).TaskGroupId == primaryGroupId && Current(project).StatusId == original.StatusId);
                await Changed("up", project => project.Tasks.FindIndex(task => task.Id == taskId) < Array.IndexOf(originalOrder, taskId));
                await Changed("down", project => project.Tasks.Select(task => task.Id).SequenceEqual(originalOrder));
                await Changed("copy", project => project.Tasks.Count == 5);
                var copy = _projects.Single().Tasks.Single(task => !originalOrder.Contains(task.Id));
                SmokeAssert(copy.Id != taskId && copy.Description == original.Description && copy.TaskGroupId == primaryGroupId && copy.Priority == original.Priority,
                    "Task-more Copy did not produce an independent task with the source details.");
                await SmokeTaskMenuActionAsync(copy.Id, "delete");
                dialog = await SmokeWaitForDialogAsync();
                SmokeAssert(dialog.Title?.ToString()?.Contains("删除", StringComparison.Ordinal) == true, "Task-more Delete did not request confirmation.");
                SmokeInvoke(SmokeDialogButton(dialog, "PrimaryButton", dialog.PrimaryButtonText));
                await SmokeWaitAsync(() => !_busy && !_dialogOpen && _projects.Single().Tasks.Count == 4, "Task-more Delete did not remove the copied task.");
                var final = PmSerializer.ParseProjects(await File.ReadAllTextAsync(repository.DataFilePath)).Single();
                SmokeAssert(!final.Tasks.Any(task => task.Id == copy.Id) && final.Tasks.Select(task => task.Id).SequenceEqual(originalOrder),
                    "Task-more Delete was not persisted or changed unrelated task order.");
                await SmokeLayoutAsync();
            });

            await Step("Reorder adjacent visible list tasks across different status columns", async () =>
            {
                _view = 1; _groupId = null; Render(); await SmokeLayoutAsync();
                var task = _projects.Single().Tasks.Single(item => item.Title == "实现 WinUI 3 原生任务编辑器");
                string[] VisibleOrder() => SmokeFind<ListView>(ContentHost, list => list.Items.OfType<TaskRow>().Any(row => row.Id == task.Id))
                    .Items.OfType<TaskRow>().Select(row => row.Id).ToArray();
                var before = VisibleOrder();
                var index = Array.IndexOf(before, task.Id);
                SmokeAssert(index > 0 && _projects.Single().Tasks.Single(item => item.Id == before[index - 1]).StatusId != task.StatusId,
                    "The visible reorder fixture needs a preceding task from a different status column.");
                var expected = before.ToArray(); (expected[index - 1], expected[index]) = (expected[index], expected[index - 1]);
                await SmokeTaskMenuActionAsync(task.Id, "up");
                await SmokeWaitAsync(() => !_busy && _projects.Single().Tasks.FindIndex(item => item.Id == task.Id) < _projects.Single().Tasks.FindIndex(item => item.Id == before[index - 1]),
                    "List Move Up did not cross the adjacent visible task from another status.");
                await SmokeLayoutAsync();
                SmokeAssert(VisibleOrder().SequenceEqual(expected), "List Move Up used hidden same-status peers instead of the adjacent visible row.");
                var saved = PmSerializer.ParseProjects(await File.ReadAllTextAsync(repository.DataFilePath)).Single();
                SmokeAssert(saved.Tasks.Select(item => item.Id).SequenceEqual(expected) && saved.Tasks.Single(item => item.Id == task.Id).StatusId == task.StatusId,
                    "Visible list ordering was not persisted or unexpectedly changed task status.");
                await SmokeTaskMenuActionAsync(task.Id, "down");
                await SmokeWaitAsync(() => !_busy && _projects.Single().Tasks.Select(item => item.Id).SequenceEqual(before), "List Move Down did not restore the visible task order.");
                await SmokeLayoutAsync();
                SmokeAssert(VisibleOrder().SequenceEqual(before), "The restored task order differs from the visible list.");
            });

            await Step("Recover blocked saves while preserving task drafts, one timer start, one comment and project selection", async () =>
            {
                var original = PmSerializer.CloneTask(_projects.Single().Tasks.Single(item => item.Title == "通过原生对话框保存的验收任务"));
                var before = await File.ReadAllTextAsync(repository.DataFilePath);
                var undoCount = _undo.Count;
                var redoCount = _redo.Count;
                var editing = EditTaskAsync(original.Id, projectId);
                var dialog = await SmokeWaitForDialogAsync(editing);
                var title = SmokeFind<TextBox>(dialog, field => Equals(field.Header, "任务标题"));
                var comment = SmokeFind<TextBox>(dialog, field => Equals(field.Header, "新增评论"));
                title.Text = "保存失败后可继续重试的任务草稿";
                comment.Text = "保存失败重试后只新增一次的评论";
                var timer = SmokeFind<Button>(dialog, button => AutomationProperties.GetAutomationId(button) == "TaskEditorStartTimer");
                SmokeAssert(timer.Focus(FocusState.Keyboard), "The editor timer cannot receive focus.");
                await SmokeLayoutAsync();
                SmokeInvoke(timer);
                await SmokeLayoutAsync();
                var timerInfo = SmokeFind<TextBlock>(dialog, label => AutomationProperties.GetAutomationId(label) == "TaskEditorTimerInfo");
                var pendingTimer = timerInfo.Text;
                SmokeAssert(!timer.IsEnabled && !new ButtonAutomationPeer(timer).IsEnabled() && pendingTimer.Contains("保存", StringComparison.Ordinal),
                    "Starting the draft timer did not disable repeat activation or explain pending persistence.");
                SmokeAssert(_undo.Count == undoCount && await File.ReadAllTextAsync(repository.DataFilePath) == before, "Starting the draft timer persisted an unsaved editor change.");
                var save = SmokeDialogButton(dialog, "PrimaryButton", "保存");
                using (var blocker = new FileStream(repository.DataFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    SmokeInvoke(save);
                    await SmokeWaitAsync(() => !_busy && SmokeDescendants<InfoBar>(dialog).Any(bar => bar.IsOpen && bar.Severity == InfoBarSeverity.Error),
                        "A locked project file did not surface a recoverable save error.");
                    await SmokeLayoutAsync();
                    SmokeAssert(ReferenceEquals(_currentActiveDialog, dialog) && !editing.IsCompleted && title.Text == "保存失败后可继续重试的任务草稿"
                        && comment.Text == "保存失败重试后只新增一次的评论", "A failed write lost the open editor or its draft.");
                    SmokeAssert(SmokeDialogButton(dialog, "PrimaryButton", "保存").IsEnabled && !_busy && !timer.IsEnabled && timerInfo.Text == pendingTimer,
                        "A failed write did not restore Save or reset the draft timer unexpectedly.");
                    SmokeAssert(_undo.Count == undoCount && _redo.Count == redoCount && await File.ReadAllTextAsync(repository.DataFilePath) == before
                        && _projects.Single().Tasks.Single(item => item.Id == original.Id).Title == original.Title,
                        "A failed task save changed data or history.");
                    title.Focus(FocusState.Keyboard);
                    screenshots.Add(await SmokeCaptureAsync(directory, "task-save-failure-retry.png", dialog));
                }
                SmokeInvoke(SmokeDialogButton(dialog, "PrimaryButton", "保存"));
                await SmokeAwaitDialogAsync(editing, dialog);
                var stored = PmSerializer.ParseProjects(await File.ReadAllTextAsync(repository.DataFilePath)).Single().Tasks.Single(item => item.Id == original.Id);
                SmokeAssert(stored.Title == "保存失败后可继续重试的任务草稿" && stored.Comments.Count == original.Comments.Count + 1
                    && stored.Comments.Count(item => item.Content == "保存失败重试后只新增一次的评论") == 1
                    && DateTimeOffset.TryParse(stored.TrackedStart, out _) && _undo.Count == Math.Min(MaximumHistorySteps, undoCount + 1)
                    && _undo.Peek().Single().Tasks.Single(task => task.Id == original.Id).Title == original.Title,
                    "Retry did not save exactly one comment, one timer start and one history entry.");
                SmokeInvoke(UndoButton);
                await SmokeWaitAsync(() => !_busy && _undo.Count == Math.Min(MaximumHistorySteps, undoCount + 1) - 1 && _projects.Single().Tasks.Single(item => item.Id == original.Id).Title == original.Title,
                    "The saved failure-regression fixture could not be restored through Undo.");
                var restored = _projects.Single().Tasks.Single(item => item.Id == original.Id);
                SmokeAssert(restored.Comments.Count == original.Comments.Count && restored.TrackedStart == original.TrackedStart,
                    "Undo did not restore the original comment and timer fixture.");
                await SmokeLayoutAsync();

                var selectedBeforeCreate = _selectedId;
                var currentBeforeCreate = Current?.Id;
                var beforeCreate = await File.ReadAllTextAsync(repository.DataFilePath);
                var undoBeforeCreate = _undo.Count; var redoBeforeCreate = _redo.Count;
                SmokeAssert(selectedBeforeCreate == projectId && currentBeforeCreate == projectId, "The failed-project-create fixture needs an active existing project.");
                var creating = EditProjectAsync();
                var projectEditor = await SmokeWaitForDialogAsync(creating);
                var projectName = SmokeFind<TextBox>(projectEditor, field => Equals(field.Header, "项目名称"));
                projectName.Text = "保存失败不得切换当前项目";
                using (var blocker = new FileStream(repository.DataFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    SmokeInvoke(SmokeDialogButton(projectEditor, "PrimaryButton", "创建"));
                    await SmokeWaitAsync(() => !_busy && SmokeDescendants<InfoBar>(projectEditor).Any(bar => bar.IsOpen && bar.Severity == InfoBarSeverity.Error),
                        "The blocked new-project write did not expose a recoverable editor error.");
                    await SmokeLayoutAsync();
                    SmokeAssert(ReferenceEquals(_currentActiveDialog, projectEditor) && !creating.IsCompleted
                        && projectName.Text == "保存失败不得切换当前项目" && SmokeDialogButton(projectEditor, "PrimaryButton", "创建").IsEnabled,
                        "A failed new-project save lost the editable draft or left Create disabled.");
                    SmokeAssert(_selectedId == selectedBeforeCreate && Current?.Id == currentBeforeCreate && _projects.Count == 1
                        && _undo.Count == undoBeforeCreate && _redo.Count == redoBeforeCreate
                        && await File.ReadAllTextAsync(repository.DataFilePath) == beforeCreate,
                        "A failed new-project write changed the selected project, project data or history before persistence succeeded.");
                }
                SmokeInvoke(SmokeHeaderCloseButton(projectEditor)); await SmokeAwaitDialogAsync(creating, projectEditor);
                SmokeAssert(_selectedId == selectedBeforeCreate && Current?.Id == currentBeforeCreate && ProjectToolbar.Visibility == Visibility.Visible
                    && _undo.Count == undoBeforeCreate && _redo.Count == redoBeforeCreate && await File.ReadAllTextAsync(repository.DataFilePath) == beforeCreate,
                    "Closing a failed new-project draft did not return to the original project unchanged.");
            });

            foreach (var theme in new[] { "Light", "Dark" })
            {
                await Step($"Apply {theme} theme and render dashboard", async () =>
                {
                    _settings = _settings with { Theme = theme }; ApplyTheme();
                    _selectedId = null; Render(); await SmokeLayoutAsync();
                    SmokeAssert(Root.ActualTheme == (theme == "Light" ? ElementTheme.Light : ElementTheme.Dark), "Requested theme was not applied.");
                    SmokeAssert(ContentHost.Children.Count > 0 && SmokeDescendants<FrameworkElement>(ContentHost).Count() > 3, "Dashboard did not render.");
                    SmokeNavigationActions();
                    screenshots.Add(await SmokeCaptureAsync(directory, $"dashboard-{theme.ToLowerInvariant()}.png"));
                    _selectedId = projectId; _groupId = primaryGroupId; Render(); await SmokeLayoutAsync();
                });
                await Step($"Verify the shared AutoSuggestBox style in {theme}", async () =>
                {
                    // The production search state uses a hidden AutoSuggestBox. Render a
                    // separate instance to exercise that shared native style without
                    // changing the live search model or presenting a fictitious workflow.
                    var suggestions = new AutoSuggestBox { PlaceholderText = "搜索验收", Width = 340 };
                    var fixture = new ContentDialog { Title = "搜索输入样式验收", Content = suggestions, CloseButtonText = "关闭" };
                    var showing = DialogAsync(fixture);
                    var dialog = await SmokeWaitForDialogAsync(showing);
                    await SmokeInputChromeAsync(suggestions, SmokeHeaderCloseButton(dialog));
                    var input = SmokeFind<TextBox>(suggestions, field => field.ActualWidth > 0 && field.ActualHeight > 0);
                    SmokeSingleLineInputGeometry(input);
                    suggestions.Text = "实际输入验收"; await SmokeLayoutAsync();
                    SmokeAssert(input.Text == suggestions.Text, "The styled suggestion box does not display its entered text.");
                    SmokeSingleLineInputGeometry(input);
                    SmokeInvoke(SmokeHeaderCloseButton(dialog)); await SmokeAwaitDialogAsync(showing, dialog);
                });
                var names = new[] { "kanban", "list", "calendar", "timeline", "dependencies" };
                for (var view = 0; view < names.Length; view++)
                {
                    var selectedView = view;
                    await Step($"Render {names[view]} in {theme} with both task-group selections", async () =>
                    {
                        var tabs = SmokeViewTabButtons();
                        foreach (var tab in tabs) SmokeControlWithinRoot(tab, $"Project view tab {tab.Content}");
                        SmokeInvoke(tabs[selectedView]);
                        await SmokeLayoutAsync();
                        SmokeAssert(_view == selectedView, "The visible project tab did not update application state.");
                        foreach (var groupId in new[] { secondaryGroupId, primaryGroupId })
                        {
                            GroupSelector.SelectedItem = ((IEnumerable<TaskGroup>)GroupSelector.ItemsSource).Single(g => g.Id == groupId);
                            await SmokeLayoutAsync();
                            SmokeAssert(_groupId == groupId, "The group selection event did not update application state.");
                            SmokeAssert(ContentHost.Children.Count > 0 && SmokeDescendants<FrameworkElement>(ContentHost).Any(), "Selected view has no rendered child controls.");
                        }
                        if (selectedView != 0)
                        {
                            var groupFilter = SmokeFind<ComboBox>(ContentHost, combo => Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(combo) == "任务组筛选");
                            groupFilter.SelectedIndex = 0;
                            await SmokeLayoutAsync();
                            Render(); await SmokeLayoutAsync();
                            SmokeAssert(_groupId is null, "Selecting all task groups was reset to the default group.");
                        }
                        SmokeAssert(ContentHost.ActualWidth > 0 && ContentHost.ActualHeight > 0, "The view has a zero-size layout.");
                        SmokeViewStructure(selectedView);
                        SmokeHarmonyText(Root);
                        screenshots.Add(await SmokeCaptureAsync(directory, $"{selectedView + 1:00}-{names[selectedView]}-{theme.ToLowerInvariant()}.png"));
                        if (selectedView is 0 or 1)
                        {
                            var task = _projects.Single().Tasks.Single(item => item.Title == "实现 WinUI 3 原生任务编辑器");
                            var menu = await SmokeOpenTaskMenuAsync(task.Id);
                            var presenter = VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot)
                                .SelectMany(popup => SmokeDescendants<MenuFlyoutPresenter>(popup.Child))
                                .First(element => element.ActualWidth > 0 && element.ActualHeight > 0);
                            SmokeHarmonyText(presenter);
                            screenshots.Add(await SmokeCaptureAsync(directory, $"task-more-{names[selectedView]}-{theme.ToLowerInvariant()}.png", presenter));
                            menu.Hide(); await SmokeLayoutAsync();
                        }
                    });
                }
                await Step($"Clear a no-match task filter through its visible recovery action in {theme}", async () =>
                {
                    _view = 1; _groupId = null; Render(); await SmokeLayoutAsync();
                    var search = SmokeFind<TextBox>(ContentHost, control => AutomationProperties.GetAutomationId(control) == "TaskSearch");
                    search.Text = "不存在的验收筛选结果";
                    await SmokeLayoutAsync();
                    SmokeAssert(!SmokeDescendants<ListView>(ContentHost).Any(list => list.Items.Count > 0), "The non-matching search still displays tasks.");
                    SmokeAssert(SmokeDescendants<TextBlock>(ContentHost).Any(label => label.Text.Contains("匹配", StringComparison.Ordinal)), "The filtered empty state does not distinguish no matches from an empty project.");
                    var clear = SmokeFind<Button>(ContentHost, control => AutomationProperties.GetAutomationId(control) == "ClearTaskFilters");
                    SmokeControlWithinRoot(clear, "Clear task filters");
                    screenshots.Add(await SmokeCaptureAsync(directory, $"list-empty-{theme.ToLowerInvariant()}.png"));
                    SmokeInvoke(clear); await SmokeLayoutAsync();
                    SmokeAssert(string.IsNullOrEmpty(SearchBox.Text) && _listStatusFilter is null && _listTagFilter is null && PrioritySelector.SelectedIndex == 0,
                        "Clear filters did not reset the active search and task filters.");
                    SmokeAssert(SmokeDescendants<ListView>(ContentHost).Sum(list => list.Items.Count) == 4, "Clear filters did not restore the four original tasks.");
                });
                await Step($"Open and capture the task drawer in {theme}", async () =>
                {
                    _view = 0; _groupId = primaryGroupId; Render(); await SmokeLayoutAsync();
                    var task = _projects.Single().Tasks.Single(t => t.Title == "实现 WinUI 3 原生任务编辑器");
                    var editing = EditTaskAsync(task.Id, projectId);
                    var dialog = await SmokeWaitForDialogAsync(editing);
                    SmokeAssert(Equals(dialog.Tag, "TaskDetail"), "The task editor did not use the task-detail drawer template.");
                    SmokeAssert(SmokeFind<TextBox>(dialog, field => Equals(field.Header, "任务标题")).Text == task.Title, "Task drawer did not load the selected task.");
                    SmokeHarmonyText(dialog);
                    SmokeDialogFrameRules(dialog);
                    foreach (var field in new Control[]
                    {
                        SmokeFind<TextBox>(dialog, control => Equals(control.Header, "任务标题")),
                        SmokeFind<TextBox>(dialog, control => Equals(control.Header, "说明")),
                        SmokeFind<ComboBox>(dialog, control => Equals(control.Header, "任务组")),
                        SmokeFind<CalendarDatePicker>(dialog, control => Equals(control.Header, "截止日期")),
                        SmokeFind<NumberBox>(dialog, control => Equals(control.Header, "开始偏移（自项目创建日起，天）"))
                    }) await SmokeInputChromeAsync(field, SmokeHeaderCloseButton(dialog));
                    var groupChoice = SmokeFind<ComboBox>(dialog, control => Equals(control.Header, "任务组"));
                    var originalGroupChoice = groupChoice.SelectedIndex;
                    await SmokeChooseComboItemAsync(groupChoice, originalGroupChoice == 0 ? 1 : 0);
                    await SmokeChooseComboItemAsync(groupChoice, originalGroupChoice);
                    var restoredStatus = SmokeFind<Button>(dialog, button => Equals(button.Tag, task.StatusId));
                    SmokeAssert(AutomationProperties.GetItemStatus(restoredStatus) == "已选中",
                        "Switching to another task group and back discarded the draft's original status.");
                    SmokeFind<TextBox>(dialog, field => Equals(field.Header, "任务标题")).Focus(FocusState.Keyboard);
                    await SmokeLayoutAsync();
                    await SmokeTaskDrawerActionsAsync(dialog);
                    screenshots.Add(await SmokeCaptureAsync(directory, $"task-drawer-{theme.ToLowerInvariant()}.png", dialog));
                    var dueTime = SmokeFind<TextBox>(dialog, field => Equals(field.Header, "截止时间"));
                    dueTime.Text = "25:90";
                    SmokeInvoke(SmokeDialogButton(dialog, "PrimaryButton", "保存"));
                    await SmokeWaitAsync(() => SmokeDescendants<InfoBar>(dialog).Any(bar => bar.IsOpen && bar.Severity == InfoBarSeverity.Error), "The invalid due-time validation did not appear.");
                    await SmokeInvalidFieldAsync(dialog, dueTime);
                    screenshots.Add(await SmokeCaptureAsync(directory, $"task-validation-{theme.ToLowerInvariant()}.png", dialog));
                    SmokeInvoke(SmokeHeaderCloseButton(dialog));
                    await SmokeAwaitDialogAsync(editing, dialog);
                });
                await Step($"Open and capture Settings in {theme}", async () =>
                {
                    var showing = ShowSettingsAsync();
                    var dialog = await SmokeWaitForDialogAsync(showing);
                    SmokeAssert(Equals(dialog.Tag, "Settings"), "Settings did not open its dedicated dialog template.");
                    SmokeAssert(SmokeDescendants<Grid>(dialog).Any(grid => grid.ColumnDefinitions.Count == 2 && Math.Abs(grid.ColumnDefinitions[0].Width.Value - 140) < .5), "Settings is missing the 140-DIP navigation column.");
                    screenshots.Add(await SmokeCaptureAsync(directory, $"settings-{theme.ToLowerInvariant()}.png", dialog));
                    SmokeInvoke(SmokeFind<Button>(dialog, button => Equals(button.Content, "数据管理")));
                    await SmokeLayoutAsync();
                    await SmokeInputChromeAsync(SmokeFind<TextBox>(dialog, field => Equals(field.Header, "同步服务器地址")), SmokeHeaderCloseButton(dialog));
                    await SmokeInputChromeAsync(SmokeFind<PasswordBox>(dialog, field => Equals(field.Header, "访问令牌")), SmokeHeaderCloseButton(dialog));
                    screenshots.Add(await SmokeCaptureAsync(directory, $"settings-inputs-{theme.ToLowerInvariant()}.png", dialog));
                    SmokeInvoke(SmokeHeaderCloseButton(dialog));
                    await SmokeAwaitDialogAsync(showing, dialog);
                });
                await Step($"Capture new project editor, validate its required name and close without saving in {theme}", async () =>
                {
                    var countBefore = _projects.Count;
                    var editing = EditProjectAsync();
                    var dialog = await SmokeWaitForDialogAsync(editing);
                    SmokeAssert(Equals(dialog.Tag, "ProjectEditor"), "New project did not use the project editor template.");
                    SmokeDialogFrameRules(dialog);
                    var name = SmokeFind<TextBox>(dialog, field => Equals(field.Header, "项目名称"));
                    SmokeAssert(string.IsNullOrEmpty(name.Text), "New project form unexpectedly contains an existing name.");
                    screenshots.Add(await SmokeCaptureAsync(directory, $"project-create-{theme.ToLowerInvariant()}.png", dialog));
                    SmokeInvoke(SmokeDialogButton(dialog, "PrimaryButton", "创建"));
                    await SmokeWaitAsync(() => SmokeDescendants<InfoBar>(dialog).Any(bar => bar.IsOpen && bar.Severity == InfoBarSeverity.Error), "New project accepted an empty name without validation.");
                    await SmokeInvalidFieldAsync(dialog, name);
                    SmokeAssert(_projects.Count == countBefore, "Invalid project input changed project data.");
                    name.Text = "本次新建草稿不得保存";
                    SmokeInvoke(SmokeHeaderCloseButton(dialog));
                    await SmokeAwaitDialogAsync(editing, dialog);
                    SmokeAssert(_projects.Count == countBefore, "Closing a new project draft unexpectedly created a project.");
                });
                await Step($"Capture existing project editor and discard a changed draft in {theme}", async () =>
                {
                    var original = _projects.Single(project => project.Id == projectId);
                    var originalName = original.Name;
                    var originalDescription = original.Description;
                    var editing = EditProjectAsync(projectId);
                    var dialog = await SmokeWaitForDialogAsync(editing);
                    SmokeAssert(Equals(dialog.Tag, "ProjectEditor"), "Edit project did not use the project editor template.");
                    var name = SmokeFind<TextBox>(dialog, field => Equals(field.Header, "项目名称"));
                    var description = SmokeFind<TextBox>(dialog, field => Equals(field.Header, "项目描述"));
                    SmokeAssert(name.Text == originalName && description.Text == originalDescription, "Project editor did not load the selected project's fields.");
                    SmokeProjectDatesVisible(dialog);
                    screenshots.Add(await SmokeCaptureAsync(directory, $"project-edit-{theme.ToLowerInvariant()}.png", dialog));
                    name.Text = "本次编辑草稿不得保存";
                    description.Text = "关闭项目设置必须保留此前的数据。";
                    SmokeInvoke(SmokeHeaderCloseButton(dialog));
                    await SmokeAwaitDialogAsync(editing, dialog);
                    var unchanged = _projects.Single(project => project.Id == projectId);
                    SmokeAssert(unchanged.Name == originalName && unchanged.Description == originalDescription, "Closing project settings persisted an unsaved draft.");
                });
                if (Environment.GetEnvironmentVariable("PUREPROJECT_UI_CONSISTENCY_TEST") == "1")
                {
                    await Step($"Inspect native calendar, number spin and checkbox states in {theme}",
                        () => SmokeConsistencyTaskControlsAsync(directory, theme, projectId, primaryGroupId, screenshots));
                    await Step($"Inspect nested task menus and the existing action tooltip in {theme}",
                        () => SmokeConsistencyMenusAsync(directory, theme, projectId, primaryGroupId, screenshots));
                    await Step($"Inspect and dismiss all four native notification severities in {theme}",
                        () => SmokeConsistencyNoticesAsync(directory, theme, screenshots));
                }
            }

            await Step("Preserve quick-add drafts on repeated selection and create once from an enabled nonempty action", async () =>
            {
                _selectedId = projectId; _view = 0; _groupId = primaryGroupId; Render(); await SmokeLayoutAsync();
                var kanbanInput = SmokeFind<TextBox>(ContentHost, control => AutomationProperties.GetAutomationId(control).StartsWith("TaskQuickAdd:", StringComparison.Ordinal));
                var kanbanInputId = AutomationProperties.GetAutomationId(kanbanInput);
                kanbanInput.Text = "重复选择当前项目与任务组不得丢失的草稿";
                kanbanInput.Select(2, 4);
                SmokeInvoke(SmokeFind<Button>(Root, button => AutomationProperties.GetAutomationId(button) == "SidebarProject:" + projectId));
                await SmokeLayoutAsync();
                SmokeAssert(_groupId == primaryGroupId && _view == 0
                    && ReferenceEquals(kanbanInput, SmokeFind<TextBox>(ContentHost, field => AutomationProperties.GetAutomationId(field) == kanbanInputId))
                    && kanbanInput.Text == "重复选择当前项目与任务组不得丢失的草稿" && kanbanInput.SelectionStart == 2 && kanbanInput.SelectionLength == 4,
                    "Selecting the current project rebuilt the view, reset its task group or discarded a quick-add draft.");
                var selectedGroup = SmokeFind<Button>(ContentHost, button => AutomationProperties.GetAutomationId(button) == "KanbanGroup:" + primaryGroupId);
                SmokeAssert(AutomationProperties.GetItemStatus(selectedGroup) == "已选中", "The current task group does not expose its selected state.");
                SmokeInvoke(selectedGroup); await SmokeLayoutAsync();
                SmokeAssert(ReferenceEquals(kanbanInput, SmokeFind<TextBox>(ContentHost, field => AutomationProperties.GetAutomationId(field) == kanbanInputId))
                    && kanbanInput.Text == "重复选择当前项目与任务组不得丢失的草稿", "Selecting the current task group discarded the quick-add draft.");
                kanbanInput.Text = "";
                var targetTab = SmokeViewTabButtons()[1];
                SmokeAssert(targetTab.Focus(FocusState.Keyboard), "The List view tab cannot receive keyboard focus.");
                SmokeInvoke(targetTab); await SmokeLayoutAsync();
                var currentTab = SmokeViewTabButtons()[1];
                SmokeAssert(ReferenceEquals(FocusManager.GetFocusedElement(Root.XamlRoot), currentTab), "Switching views discarded keyboard focus instead of restoring it to the selected tab.");
                var input = SmokeFind<TextBox>(ContentHost, control => AutomationProperties.GetAutomationId(control).StartsWith("TaskQuickAdd:", StringComparison.Ordinal));
                var inputId = AutomationProperties.GetAutomationId(input);
                var add = SmokeFind<Button>(ContentHost, button => AutomationProperties.GetAutomationId(button) == inputId + ":Submit");
                var before = await File.ReadAllTextAsync(repository.DataFilePath);
                var undoCount = _undo.Count;
                SmokeAssert(string.IsNullOrEmpty(input.Text) && !add.IsEnabled && !new ButtonAutomationPeer(add).IsEnabled(), "An empty quick-add field leaves its action enabled.");
                input.Text = "   "; await SmokeLayoutAsync();
                SmokeAssert(!add.IsEnabled && !new ButtonAutomationPeer(add).IsEnabled() && _undo.Count == undoCount
                    && await File.ReadAllTextAsync(repository.DataFilePath) == before, "A whitespace-only quick-add enables or commits its action.");
                SmokeAssert(input.Focus(FocusState.Keyboard), "Quick-add input cannot receive keyboard focus.");
                input.Text = "键盘连续新增验收任务";
                await SmokeLayoutAsync();
                SmokeSingleLineInputGeometry(input);
                SmokeAssert(add.IsEnabled && new ButtonAutomationPeer(add).IsEnabled(), "A valid quick-add title did not enable the native action.");
                SmokeInvoke(add);
                // Native Invoke may dispatch asynchronously. A second activation is
                // attempted only while the same action remains enabled; disabled
                // controls are never invoked by the test's helper.
                if (add.IsEnabled) SmokeInvoke(add);
                await SmokeWaitAsync(() => !_busy && _projects.Single().Tasks.Count == 5, "Keyboard quick-add did not commit the task.");
                await SmokeLayoutAsync();
                SmokeAssert(_projects.Single().Tasks.Count(task => task.Title == "键盘连续新增验收任务") == 1 && _undo.Count == Math.Min(MaximumHistorySteps, undoCount + 1),
                    "Rapid quick-add activation produced duplicate tasks or history entries.");
                var replacement = SmokeFind<TextBox>(ContentHost, control => AutomationProperties.GetAutomationId(control) == inputId);
                SmokeAssert(ReferenceEquals(FocusManager.GetFocusedElement(Root.XamlRoot), replacement), "Quick-add lost focus after rebuilding the task view.");
                SmokeElementWithinRoot(replacement, "Focused replacement quick-add input");
                SmokeAssert(!SmokeRenderedVisible(SaveState, Root)
                    && !SmokeDescendants<TextBlock>(TopBar).Any(element => ReferenceEquals(element, SaveState)),
                    "The operation log is still visible or occupying the brand header.");
                SmokeAssert(AutomationProperties.GetLiveSetting(SaveState) == AutomationLiveSetting.Polite
                    && SaveState.Text.Contains("已保存", StringComparison.Ordinal)
                    && AutomationProperties.GetName(SaveState).Contains(SaveState.Text, StringComparison.Ordinal),
                    "The save status does not announce the current saved message through its live accessible name.");
                var created = _projects.Single().Tasks.Single(task => task.Title == "键盘连续新增验收任务");
                await ChangeAsync(projectId, project => _service.DeleteTask(project, created.Id));
                await SmokeLayoutAsync();
                SmokeAssert(_projects.Single().Tasks.Count == 4, "The temporary quick-add regression fixture was not removed.");
            });

            await Step("Cancel the application export dialog and retain the Settings data-page drafts", async () =>
            {
                var settingsRepository = _settingsRepository ?? throw new InvalidOperationException("The settings repository is unavailable.");
                var existed = File.Exists(settingsRepository.FilePath);
                var before = existed ? await File.ReadAllTextAsync(settingsRepository.FilePath) : null;
                var storedUrl = _settings.SyncServerUrl; var storedToken = _settings.SyncToken;
                var showing = ShowSettingsAsync();
                var original = await SmokeWaitForDialogAsync(showing);
                SmokeInvoke(SmokeFind<Button>(original, button => AutomationProperties.GetAutomationId(button) == "SettingsPage_data"));
                await SmokeLayoutAsync();
                var url = SmokeFind<TextBox>(original, field => AutomationProperties.GetAutomationId(field) == "SettingsSyncUrl");
                var token = SmokeFind<PasswordBox>(original, field => AutomationProperties.GetAutomationId(field) == "SettingsSyncToken");
                url.Text = "https://smoke-draft.invalid"; token.Password = "smoke-unsaved-token";
                SmokeInvoke(SmokeFind<Button>(original, button => AutomationProperties.GetAutomationId(button) == "SettingsExport"));
                var export = await SmokeWaitForDialogAsync(showing, original);
                SmokeAssert(Equals(export.Title, "备份与导出"), "Settings Export did not open the application's export-options dialog.");
                var format = SmokeFind<ComboBox>(export, control => AutomationProperties.GetAutomationId(control) == "ExportFormat");
                SmokeAssert(format.Items.Count == 3 && format.Items[0].ToString()!.Contains(".pureproject")
                    && format.Items[1].ToString()!.Contains(".mm") && format.Items[2].ToString()!.Contains(".xlsx"), "Three lossless exchange formats are not available.");
                format.SelectedIndex = 2; await SmokeLayoutAsync();
                SmokeAssert(SmokeDescendants<TextBlock>(export).Any(text => text.Text.Contains("分组标题合并")), "Excel editing/restore guidance is missing.");
                // Stop at the application's cancel action. This scenario never opens
                // or claims coverage of the operating-system import/save pickers.
                SmokeInvoke(SmokeDialogButton(export, "CloseButton", "取消"));
                var returned = await SmokeWaitForDialogAsync(showing, export);
                SmokeAssert(ReferenceEquals(returned, original) && url.Text == "https://smoke-draft.invalid" && token.Password == "smoke-unsaved-token",
                    "Cancelling export recreated Settings or discarded its connection drafts.");
                SmokeAssert(AutomationProperties.GetItemStatus(SmokeFind<Button>(returned, button => AutomationProperties.GetAutomationId(button) == "SettingsPage_data")) == "已选中"
                    && SmokeRenderedVisible(url, returned) && SmokeRenderedVisible(token, returned), "Cancelling export did not return to the Settings data page.");
                SmokeInvoke(SmokeHeaderCloseButton(returned)); await SmokeAwaitDialogAsync(showing, returned);
                SmokeAssert(_settings.SyncServerUrl == storedUrl && _settings.SyncToken == storedToken && File.Exists(settingsRepository.FilePath) == existed
                    && (!existed || await File.ReadAllTextAsync(settingsRepository.FilePath) == before), "Closing the returned Settings draft persisted unsaved connection values.");
            });

            await Step("Cancel background transfers through both footer and header close buttons", async () =>
            {
                foreach (var header in new[] { false, true })
                {
                    var transfer = RunTransferAsync("正在验证可取消的数据操作", token =>
                    {
                        token.WaitHandle.WaitOne(TimeSpan.FromSeconds(15));
                        token.ThrowIfCancellationRequested();
                        return "unexpected completion";
                    }, discardResultOnCancellation: true);
                    var progress = await SmokeWaitForDialogAsync(transfer);
                    SmokeAssert(_busy, "Transfer is missing its busy guard.");
                    SmokeInvoke(header ? SmokeHeaderCloseButton(progress) : SmokeDialogButton(progress, "CloseButton", "取消"));
                    await SmokeAwaitDialogAsync(transfer, progress);
                    SmokeAssert(await transfer is null && !_busy && !_dialogOpen, "Cancellation did not stop the worker and release dialog/busy state.");
                }
                var fast = await RunTransferAsync("正在完成数据操作", _ => "completed");
                SmokeAssert(fast == "completed" && !_busy && !_dialogOpen, "A quickly completed transfer left a dialog or busy state behind.");
            });

            await Step("Persist and apply Light and Dark themes through visible Settings buttons", async () =>
            {
                var settingsRepository = _settingsRepository ?? throw new InvalidOperationException("The settings repository is unavailable.");
                var showing = ShowSettingsAsync();
                var dialog = await SmokeWaitForDialogAsync(showing);
                foreach (var (label, theme, expected) in new[] { ("浅色", "Light", ElementTheme.Light), ("深色", "Dark", ElementTheme.Dark) })
                {
                    var button = SmokeFind<Button>(dialog, item => item.IsEnabled && Equals(item.Content, label));
                    SmokeInvoke(button);
                    dialog = await SmokeWaitForDialogAsync(showing, dialog);
                    SmokeAssert(Equals(dialog.Tag, "Settings"), "Theme switch did not reopen Settings.");
                    SmokeAssert(_settings.Theme == theme && Root.ActualTheme == expected, "The visible Settings theme button did not apply its theme.");
                    var saved = await settingsRepository.LoadAsync();
                    SmokeAssert(saved.Theme == theme, "The Settings theme button did not persist its selection immediately.");
                }
                SmokeInvoke(SmokeHeaderCloseButton(dialog));
                await SmokeAwaitDialogAsync(showing, dialog);
            });

            await Step("Persist Dark theme through SettingsRepository and reload it", async () =>
            {
                var settingsRepository = _settingsRepository ?? throw new InvalidOperationException("The settings repository is unavailable.");
                await settingsRepository.SaveAsync(_settings with { Theme = "Dark" });
                var restored = await settingsRepository.LoadAsync();
                SmokeAssert(restored.Theme == "Dark" && File.Exists(settingsRepository.FilePath), "Dark theme was not persisted and reloaded.");
                _settings = restored; ApplyTheme(); await SmokeLayoutAsync();
                SmokeAssert(Root.ActualTheme == ElementTheme.Dark, "Reloaded Dark setting was not applied to the window.");
            });

            await Step("Search real task groups and navigate a bounded list of global results", async () =>
            {
                var showing = ShowGlobalSearchAsync();
                var dialog = await SmokeWaitForDialogAsync(showing);
                var query = SmokeFind<TextBox>(dialog, control => AutomationProperties.GetAutomationId(control) == "GlobalSearchInput");
                query.Text = "设计评审"; await SmokeLayoutAsync();
                var groupResult = SmokeFind<Button>(dialog, button => AutomationProperties.GetAutomationId(button) == "GlobalResult:任务组:" + secondaryGroupId);
                SmokeAssert(!string.IsNullOrWhiteSpace(new ButtonAutomationPeer(groupResult).GetName()), "The task-group search result has no accessible name.");
                screenshots.Add(await SmokeCaptureAsync(directory, "global-search-group-dark.png", dialog));
                SmokeInvoke(groupResult); await SmokeAwaitDialogAsync(showing, dialog);
                SmokeAssert(_selectedId == projectId && _groupId == secondaryGroupId && _view == 0, "Opening a task-group search result did not select its actual group.");

                var fixture = _service.CreateProject("临时搜索滚动验收", "仅用于隔离目录内搜索回归，测试后移除。", "#A33B32");
                for (var index = 0; index < 70; index++) _service.CreateTask(fixture, $"搜索容量回归 {index + 1:00}", fixture.DefaultTaskGroupId);
                _projects.Add(fixture);
                try
                {
                    showing = ShowGlobalSearchAsync(); dialog = await SmokeWaitForDialogAsync(showing);
                    query = SmokeFind<TextBox>(dialog, control => AutomationProperties.GetAutomationId(control) == "GlobalSearchInput");
                    query.Text = "搜索容量回归"; await SmokeLayoutAsync();
                    var results = SmokeFind<ScrollViewer>(dialog, control => AutomationProperties.GetAutomationId(control) == "GlobalSearchResults");
                    var items = SmokeDescendants<Button>(results).Where(button => AutomationProperties.GetAutomationId(button).StartsWith("GlobalResult:", StringComparison.Ordinal)).ToArray();
                    SmokeAssert(items.Length == 60 && results.ScrollableHeight > 0 && results.ViewportHeight > 0,
                        "The many-result search is not capped at 60 inside a scrollable viewport.");
                    var panel = SmokeFind<Border>(dialog, element => element.Name == "BackgroundElement");
                    SmokeFullyWithin(query, panel, "Global search query"); SmokeFullyWithin(results, panel, "Global search result viewport");
                    var close = SmokeDialogButton(dialog, "CloseButton", "关闭");
                    var closeBounds = SmokeBounds(close, panel); var resultBounds = SmokeBounds(results, panel);
                    SmokeAssert(resultBounds.Bottom <= closeBounds.Top + 1, "Global search results overlap the fixed close action.");
                    SmokeAssert(items[^1].Focus(FocusState.Keyboard), "The last search result cannot receive keyboard focus.");
                    await SmokeLayoutAsync();
                    SmokeFullyWithin(items[^1], results, "Focused final global search result");
                    SmokeAssert(results.VerticalOffset > 0, "Focusing the last global search result did not scroll it into view.");
                    SmokeFullyWithin(query, panel, "Global search query after scrolling results");
                    screenshots.Add(await SmokeCaptureAsync(directory, "global-search-many-dark.png", dialog));
                    SmokeInvoke(SmokeHeaderCloseButton(dialog)); await SmokeAwaitDialogAsync(showing, dialog);
                }
                finally { _projects.RemoveAll(project => project.Id == fixture.Id); }

                var selected = _projects.Single().Tasks.Single(task => task.Title == "实现 WinUI 3 原生任务编辑器");
                showing = ShowGlobalSearchAsync(); dialog = await SmokeWaitForDialogAsync(showing);
                query = SmokeFind<TextBox>(dialog, control => AutomationProperties.GetAutomationId(control) == "GlobalSearchInput");
                query.Text = "完成表单与输入校验"; await SmokeLayoutAsync();
                var taskResult = SmokeFind<Button>(dialog, button => AutomationProperties.GetAutomationId(button) == "GlobalResult:任务:" + selected.Id);
                SmokeInvoke(taskResult);
                var drawer = await SmokeWaitForDialogAsync(showing, dialog);
                SmokeAssert(Equals(drawer.Tag, "TaskDetail") && SmokeFind<TextBox>(drawer, field => Equals(field.Header, "任务标题")).Text == selected.Title,
                    "Searching a subtask did not open its parent task editor.");
                SmokeInvoke(SmokeHeaderCloseButton(drawer)); await SmokeAwaitDialogAsync(showing, drawer);
            });

            await Step("Render dashboard and kanban at 1024×768 with reachable controls", async () =>
            {
                try
                {
                    AppWindow.Resize(new Windows.Graphics.SizeInt32(1024, 768));
                    await SmokeWaitAsync(() => AppWindow.Size.Width == 1024 && AppWindow.Size.Height == 768, "The test window did not resize to 1024×768.");
                    _selectedId = null; Render(); await SmokeLayoutAsync();
                    screenshots.Add(await SmokeCaptureAsync(directory, "dashboard-1024x768-dark.png"));
                    SmokeNavigationActions();
                    SmokeAssert(ProjectToolbar.Visibility == Visibility.Collapsed, "Dashboard unexpectedly shows the project toolbar.");

                    _selectedId = projectId; _groupId = primaryGroupId; _view = 0; Render(); await SmokeLayoutAsync();
                    screenshots.Add(await SmokeCaptureAsync(directory, "kanban-1024x768-dark.png"));
                    SmokeElementWithinRoot(ProjectToolbar, "Project view bar");
                    foreach (var tab in SmokeViewTabButtons()) SmokeControlWithinRoot(tab, $"Project view tab {tab.Content}");
                    SmokeViewStructure(0);
                    SmokeNavigationActions();
                    var groupToggle = SmokeFind<Button>(ContentHost, button => AutomationProperties.GetName(button) == "选择任务组");
                    SmokeControlWithinRoot(groupToggle, "Compact task-group selector");
                    SmokeInvoke(groupToggle); await SmokeLayoutAsync();
                    var groupDrawer = SmokeFind<Border>(ContentHost, element => AutomationProperties.GetAutomationId(element) == "CompactGroupDrawer");
                    SmokeAssert(SmokeContainsFocus(groupDrawer), "Opening the compact task-group drawer did not move focus into its choices.");
                    SmokeElementWithinRoot(groupDrawer, "Compact task-group drawer");
                    SmokeAssert(SmokeDescendants<Grid>(groupDrawer).Any(panel => panel.TabFocusNavigation == KeyboardNavigationMode.Cycle),
                        "The open compact task-group panel has no native cyclic Tab scope.");
                    foreach (var action in SmokeDescendants<Button>(groupDrawer).Where(button => button.IsEnabled && button.IsTabStop && button.Visibility == Visibility.Visible))
                    {
                        SmokeElementWithinRoot(action, "Compact task-group action");
                        SmokeAssert(action.Focus(FocusState.Keyboard) && SmokeContainsFocus(groupDrawer), "A compact task-group action cannot receive focus inside the drawer.");
                    }
                    screenshots.Add(await SmokeCaptureAsync(directory, "kanban-groups-open-1024x768-dark.png"));
                    SmokeInvoke(SmokeFind<Button>(groupDrawer, button => AutomationProperties.GetAutomationId(button) == "KanbanGroup:" + secondaryGroupId));
                    await SmokeLayoutAsync();
                    groupToggle = SmokeFind<Button>(ContentHost, button => AutomationProperties.GetAutomationId(button) == "KanbanGroupDrawerToggle");
                    groupDrawer = SmokeFind<Border>(ContentHost, element => AutomationProperties.GetAutomationId(element) == "CompactGroupDrawer");
                    SmokeAssert(_groupId == secondaryGroupId && groupDrawer.Visibility == Visibility.Collapsed && SmokeContainsFocus(groupToggle),
                        "Choosing a compact task group did not close its drawer and restore focus to the selector.");
                    SmokeInvoke(groupToggle); await SmokeLayoutAsync();
                    SmokeInvoke(SmokeFind<Button>(ContentHost, button => AutomationProperties.GetAutomationId(button) == "KanbanGroupDrawerClose"));
                    await SmokeLayoutAsync();
                    SmokeAssert(groupDrawer.Visibility == Visibility.Collapsed && SmokeContainsFocus(groupToggle), "Closing the compact task-group drawer did not restore selector focus.");
                    _groupId = primaryGroupId; Render(); await SmokeLayoutAsync();

                    foreach (var compactView in new[] { 1, 2, 3, 4 })
                    {
                        SmokeInvoke(SmokeViewTabButtons()[compactView]); await SmokeLayoutAsync();
                        SmokeViewStructure(compactView);
                        foreach (var filter in SmokeDescendants<ComboBox>(ContentHost).Where(control => SmokeEffectivelyVisible(control, Root)))
                            SmokeElementWithinRoot(filter, $"Compact view filter {AutomationProperties.GetName(filter)}");
                        if (compactView == 1)
                        {
                            SmokeElementWithinRoot(SmokeFind<TextBox>(ContentHost, control => AutomationProperties.GetAutomationId(control) == "TaskSearch"), "Compact task search");
                            screenshots.Add(await SmokeCaptureAsync(directory, "list-1024x768-dark.png"));
                        }
                        if (compactView is 3 or 4)
                        {
                            var canvas = SmokeFind<Canvas>(ContentHost, element => element.ActualWidth > 0 && element.ActualHeight > 0);
                            foreach (var button in SmokeDescendants<Button>(canvas)) SmokeFullyWithin(button, canvas, "Compact chart task action");
                            if (compactView == 4) screenshots.Add(await SmokeCaptureAsync(directory, "dependencies-1024x768-dark.png"));
                        }
                    }
                    var task = _projects.Single().Tasks.Single(item => item.Title == "实现 WinUI 3 原生任务编辑器");
                    var editing = EditTaskAsync(task.Id, projectId);
                    var drawer = await SmokeWaitForDialogAsync(editing);
                    await SmokeTaskDrawerActionsAsync(drawer);
                    screenshots.Add(await SmokeCaptureAsync(directory, "task-drawer-1024x768-dark.png", drawer));
                    foreach (var header in new[] { "任务标题", "任务组", "说明", "截止日期", "截止时间", "提醒时间", "开始偏移（自项目创建日起，天）" })
                    {
                        var field = SmokeFind<Control>(drawer, control => control switch
                        {
                            TextBox text => Equals(text.Header, header), ComboBox combo => Equals(combo.Header, header),
                            CalendarDatePicker date => Equals(date.Header, header), NumberBox number => Equals(number.Header, header), _ => false
                        });
                        SmokeAssert(field.Focus(FocusState.Keyboard), $"Compact task field {header} cannot receive keyboard focus.");
                        await SmokeLayoutAsync();
                        SmokeEditorFieldVisible(drawer, field, $"Compact task field {header}");
                    }
                    SmokeInvoke(SmokeHeaderCloseButton(drawer)); await SmokeAwaitDialogAsync(editing, drawer);
                }
                finally
                {
                    AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 890));
                    await SmokeWaitAsync(() => AppWindow.Size.Width == 1440 && AppWindow.Size.Height == 890, "The test window did not restore its standard screenshot dimensions.");
                    await SmokeLayoutAsync();
                }
            });

            await Step("Reload persisted JSON through the shared import validator", async () =>
            {
                var stored = PmSerializer.ParseProjects(await File.ReadAllTextAsync(repository.DataFilePath));
                SmokeAssert(stored.Count == 1 && stored[0].Tasks.Count == 4, "Persisted project data differs from UI state.");
                SmokeAssert(stored[0].Milestones.Single().Title.EndsWith("已检查", StringComparison.Ordinal), "Milestone edit did not reach the data file.");
            });
        }
        catch (Exception exception)
        {
            failure = exception.ToString();
            Environment.ExitCode = 1;
            if (directory is not null)
            {
                try { screenshots.Add(await SmokeCaptureAsync(directory, "failure.png", _currentActiveDialog)); }
                catch (Exception captureException) { failureScreenshotError = captureException.ToString(); }
            }
        }
        finally
        {
            try
            {
                _currentActiveDialog?.Hide();
                if (directory is not null)
                {
                    var result = new
                    {
                        success = failure is null, scope = Environment.GetEnvironmentVariable("PUREPROJECT_UI_CONSISTENCY_ONLY") == "1" ? "component-diagnostic" : "full-regression",
                        startedAt = started, finishedAt = DateTimeOffset.UtcNow,
                        failedStep = failure is null ? null : currentStep, exception = failure, failureScreenshotError,
                        dataDirectory = directory, interaction = "In-process WinUI controls and native ButtonAutomationPeer/IInvokeProvider",
                        viewport = new { windowWidth = AppWindow.Size.Width, windowHeight = AppWindow.Size.Height, clientWidthDip = Root.ActualWidth, clientHeightDip = Root.ActualHeight, rasterizationScale = Root.XamlRoot.RasterizationScale },
                        steps, screenshots
                    };
                    await File.WriteAllTextAsync(Path.Combine(directory, "ui-smoke-result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                }
                else Debug.WriteLine("UI smoke isolation check failed: " + failure);
            }
            finally { Close(); }
        }
    }

    private async Task SmokeLayoutAsync()
    {
        Root.UpdateLayout();
        await Task.Delay(100);
        Root.UpdateLayout();
    }

    private async Task<ContentDialog> SmokeWaitForDialogAsync(Task? opening = null, ContentDialog? previous = null)
    {
        var elapsed = Stopwatch.StartNew();
        while (_currentActiveDialog is null || ReferenceEquals(_currentActiveDialog, previous)
            || !_currentActiveDialog.Resources.TryGetValue("PureProjectDialogOpened", out var opened) || opened is not true)
        {
            if (opening is { IsCompleted: true })
            {
                await opening;
                throw new InvalidOperationException("The native dialog opening operation completed without showing a dialog.");
            }
            if (elapsed.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("The native dialog did not open.");
            await Task.Delay(50);
            Root.UpdateLayout();
        }
        await SmokeLayoutAsync();
        if (opening is { IsFaulted: true } or { IsCanceled: true }) await opening;
        var dialog = _currentActiveDialog ?? throw new InvalidOperationException("The native dialog closed before its initial layout completed.");
        dialog.UpdateLayout();
        SmokeAssert(dialog.XamlRoot is not null, "Dialog has no XamlRoot.");
        return dialog;
    }

    private async Task SmokeAwaitDialogAsync(Task editing, ContentDialog dialog)
    {
        if (await Task.WhenAny(editing, Task.Delay(10000)) != editing)
        {
            var errors = string.Join(" | ", SmokeDescendants<InfoBar>(dialog).Where(bar => bar.IsOpen).Select(bar => bar.Message));
            throw new TimeoutException("The dialog did not finish its save/cancel operation. " + errors);
        }
        await editing;
        await SmokeLayoutAsync();
    }

    private async Task SmokeWaitAsync(Func<bool> condition, string failureMessage)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException(failureMessage);
            await Task.Delay(50);
            Root.UpdateLayout();
        }
    }

    private static void SmokeInvoke(Button button)
    {
        SmokeAssert(button.IsEnabled, $"Button '{button.Content}' is disabled.");
        var peer = new ButtonAutomationPeer(button);
        if (peer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke) throw new InvalidOperationException("Button does not expose its native invoke provider.");
        invoke.Invoke();
    }

    private async Task<MenuFlyout> SmokeOpenTaskMenuAsync(string taskId)
    {
        var more = SmokeFind<Button>(ContentHost, button => AutomationProperties.GetAutomationId(button) == "TaskMore:" + taskId);
        SmokeControlWithinRoot(more, "Task more action");
        // Verify the live native hit-test result for the button's center and
        // padding. This catches transparent icon holes without asserting how
        // the template is implemented; routed mouse activation is still a
        // separate physical-input acceptance check.
        foreach (var x in new[] { 3d, more.ActualWidth / 2, more.ActualWidth - 3 })
        {
            var point = more.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(x, more.ActualHeight / 2));
            var hit = VisualTreeHelper.FindElementsInHostCoordinates(point, Root, false).FirstOrDefault();
            var belongsToButton = false;
            for (DependencyObject? element = hit; element is not null; element = VisualTreeHelper.GetParent(element))
                if (ReferenceEquals(element, more)) { belongsToButton = true; break; }
            SmokeAssert(belongsToButton, "Task-more padding or icon center lets native hit testing pass through to the task card.");
        }
        SmokeAssert(more.Flyout is MenuFlyout, "Task more has no native menu flyout.");
        var menu = (MenuFlyout)more.Flyout;
        SmokeInvoke(more);
        await SmokeWaitAsync(() => menu.IsOpen && menu.Items.OfType<MenuFlyoutItem>().Any(item => item.ActualWidth > 0 && item.ActualHeight > 0),
            "Invoking the visible task-more button did not open a rendered native menu.");
        await SmokeLayoutAsync();
        SmokeAssert(!_dialogOpen, "Opening task-more also opened the task editor.");
        return menu;
    }

    private async Task SmokeTaskMenuActionAsync(string taskId, string action)
    {
        var (_, item) = await SmokeTaskMenuItemAsync(taskId, action);
        var identity = AutomationProperties.GetAutomationId(item);
        SmokeAssert(item.IsEnabled, $"Task menu action '{identity}' is disabled.");
        var itemPeer = FrameworkElementAutomationPeer.CreatePeerForElement(item);
        if (itemPeer?.GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
            throw new InvalidOperationException($"Task menu action '{identity}' does not expose its native invoke provider.");
        invoke.Invoke();
    }

    private async Task<(MenuFlyout Menu, MenuFlyoutItem Item)> SmokeTaskMenuItemAsync(string taskId, string action)
    {
        var menu = await SmokeOpenTaskMenuAsync(taskId);
        var identity = $"TaskMenu:{action}:{taskId}";
        var path = SmokeMenuItemPath(menu.Items, identity) ?? throw new InvalidOperationException($"Task menu action '{identity}' is missing.");
        foreach (var parent in path.Take(path.Count - 1))
        {
            SmokeAssert(parent is MenuFlyoutSubItem && parent.IsEnabled && parent.ActualWidth > 0 && parent.ActualHeight > 0,
                $"Task-menu submenu '{AutomationProperties.GetAutomationId(parent)}' is not visible and enabled.");
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(parent);
            if (peer?.GetPattern(PatternInterface.ExpandCollapse) is not IExpandCollapseProvider expansion)
                throw new InvalidOperationException("The task-menu submenu does not expose its native expand provider.");
            expansion.Expand();
            await SmokeLayoutAsync();
        }
        var leaf = path[^1];
        SmokeAssert(leaf is MenuFlyoutItem && leaf.Visibility == Visibility.Visible && leaf.ActualWidth > 0 && leaf.ActualHeight > 0,
            $"Task menu action '{identity}' has no visible layout.");
        return (menu, (MenuFlyoutItem)leaf);
    }

    private static List<MenuFlyoutItemBase>? SmokeMenuItemPath(IEnumerable<MenuFlyoutItemBase> items, string identity)
    {
        foreach (var item in items)
        {
            if (AutomationProperties.GetAutomationId(item) == identity) return [item];
            if (item is MenuFlyoutSubItem submenu && SmokeMenuItemPath(submenu.Items, identity) is { } children)
            {
                children.Insert(0, item);
                return children;
            }
        }
        return null;
    }

    private static void SmokeHarmonyText(DependencyObject root)
    {
        var labels = SmokeDescendants<TextBlock>(root).Where(label => label.ActualWidth > 0 && label.ActualHeight > 0
            && SmokeRenderedVisible(label, root) && label.Text.Any(character => character is >= '\u4E00' and <= '\u9FFF')).ToArray();
        SmokeAssert(labels.Length > 0, "There are no visible Chinese text labels to verify the application font.");
        foreach (var label in labels)
            SmokeAssert(label.FontFamily.Source == AppFontSource, $"Visible text '{label.Text}' uses '{label.FontFamily.Source}' instead of the bundled HarmonyOS font.");
    }

    private async Task SmokeInputChromeAsync(Control field, Control focusSink)
    {
        SmokeAssert(field.FontFamily.Source == AppFontSource, $"{field.GetType().Name} does not use the bundled HarmonyOS font.");
        SmokeAssert(focusSink.Focus(FocusState.Keyboard), "The input-style test cannot move focus away from the field.");
        await SmokeLayoutAsync();
        var backgroundName = field is ComboBox or CalendarDatePicker ? "Background" : "BorderElement";
        var border = SmokeFind<Border>(field, element => element.Name == backgroundName);
        void FullBorder(Border current, string state)
        {
            var thickness = current.BorderThickness;
            SmokeAssert(current.ActualWidth > 0 && current.ActualHeight > 0 && current.BorderBrush is SolidColorBrush { Color.A: > 0 }
                && Math.Abs(thickness.Left - 1) < .01 && Math.Abs(thickness.Top - 1) < .01
                && Math.Abs(thickness.Right - 1) < .01 && Math.Abs(thickness.Bottom - 1) < .01,
                $"{field.GetType().Name} {state} must have one solid 1-DIP border on all four sides; actual {thickness}, brush {current.BorderBrush?.GetType().Name}.");
        }
        FullBorder(border, "normal");
        SmokeAssert(field.Focus(FocusState.Keyboard), $"{field.GetType().Name} cannot receive focus for the styled-field test.");
        await SmokeLayoutAsync();
        SmokeAssert(SmokeContainsFocus(field), $"{field.GetType().Name} did not retain focus.");
        FullBorder(border, "focused");
        var accent = (SolidColorBrush)ThemeBrush("AccentBrush");
        if (field is ComboBox or CalendarDatePicker)
        {
            var focus = SmokeFind<Border>(field, element => element.Name == "FocusBorder");
            FullBorder(focus, "focus overlay");
            SmokeAssert(focus.Opacity > .9 && focus.BorderBrush is SolidColorBrush brush && brush.Color == accent.Color,
                $"{field.GetType().Name} has no visible full accent focus border.");
        }
        else SmokeAssert(border.BorderBrush is SolidColorBrush brush && brush.Color == accent.Color,
            $"{field.GetType().Name} retained the system focus underline brush.");
        SmokeAssert(!SmokeDescendants<FrameworkElement>(field).Any(element => element.Name == "Pill" && element.Visibility == Visibility.Visible && element.Opacity > 0),
            $"{field.GetType().Name} still displays the system focus pill.");
        SmokeAssert(focusSink.Focus(FocusState.Keyboard), "The disabled-style test cannot move focus away from the field.");
        field.IsEnabled = false;
        try
        {
            await SmokeLayoutAsync();
            FullBorder(border, "disabled");
            SmokeAssert(!field.Focus(FocusState.Keyboard), $"Disabled {field.GetType().Name} still accepts keyboard focus.");
        }
        finally { field.IsEnabled = true; Root.UpdateLayout(); }
    }

    private static void SmokeSingleLineInputGeometry(TextBox field)
    {
        var identity = AutomationProperties.GetAutomationId(field);
        var probe = new TextBlock { Text = string.IsNullOrEmpty(field.Text) ? field.PlaceholderText : field.Text,
            FontFamily = field.FontFamily, FontSize = field.FontSize, FontWeight = field.FontWeight,
            FontStyle = field.FontStyle, FontStretch = field.FontStretch, TextWrapping = TextWrapping.NoWrap,
            // ActualHeight reports fractional text metrics. Compare to the same
            // metrics, rather than a DesiredSize rounded up to a whole DIP.
            UseLayoutRounding = false };
        probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var lineHeight = probe.DesiredSize.Height;
        SmokeAssert(lineHeight > 0, $"Input '{identity}' has no measurable text line.");
        var content = SmokeFind<ScrollViewer>(field, element => element.Name == "ContentElement");
        var textHeight = content.ActualHeight - content.Padding.Top - content.Padding.Bottom;
        SmokeAssert(textHeight + .5 >= lineHeight,
            $"Input '{identity}' clips its text line: {textHeight:0.##} DIP content height for a {lineHeight:0.##}-DIP font line.");
        SmokeFullyWithin(content, field, $"Input '{identity}' content viewport");
        if (string.IsNullOrEmpty(field.Text))
        {
            var placeholder = SmokeFind<FrameworkElement>(field, element => element.Name == "PlaceholderTextContentPresenter");
            // The SDK TextBox template uses TextBlock; AutoSuggestBox's editing
            // template uses ContentControl for the same native placeholder part.
            var padding = placeholder switch { TextBlock text => text.Padding, Control control => control.Padding, _ => new Thickness(0) };
            var placeholderHeight = placeholder.ActualHeight - padding.Top - padding.Bottom;
            SmokeAssert(SmokeRenderedVisible(placeholder, field) && placeholderHeight + .5 >= lineHeight,
                $"Input '{identity}' clips its placeholder: {placeholderHeight:0.##} DIP available for a {lineHeight:0.##}-DIP font line.");
            SmokeFullyWithin(placeholder, field, $"Input '{identity}' placeholder");
        }
        if (field.Header is null)
            SmokeAssert(!SmokeDescendants<FrameworkElement>(field).Any(element => element.Name == "HeaderContentPresenter"
                && SmokeRenderedVisible(element, field) && element.ActualHeight > .5),
                $"Headerless input '{identity}' reserves visible space for an empty header.");
    }

    private async Task SmokeChooseComboItemAsync(ComboBox combo, int selectedIndex)
    {
        SmokeAssert(selectedIndex >= 0 && selectedIndex < combo.Items.Count && selectedIndex != combo.SelectedIndex,
            "The dropdown interaction needs an existing alternative option.");
        SmokeAssert(combo.Focus(FocusState.Keyboard), "The native dropdown cannot receive focus.");
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(combo);
        if (peer?.GetPattern(PatternInterface.ExpandCollapse) is not IExpandCollapseProvider expand)
            throw new InvalidOperationException("The dropdown has no native expand/collapse provider.");
        expand.Expand();
        await SmokeWaitAsync(() => combo.IsDropDownOpen && combo.ContainerFromIndex(selectedIndex) is ComboBoxItem { ActualWidth: > 0, ActualHeight: > 0 },
            "The native dropdown did not render its selectable alternatives.");
        var item = (ComboBoxItem)combo.ContainerFromIndex(selectedIndex);
        SmokeAssert(item.IsEnabled && item.Visibility == Visibility.Visible, "The dropdown alternative is hidden or disabled.");
        // WinUI exposes SelectionItem through the data peer, not the visual
        // ComboBoxItem container's peer.
        var itemPeer = new ComboBoxItemDataAutomationPeer(combo.Items[selectedIndex], (ComboBoxAutomationPeer)peer);
        if (itemPeer?.GetPattern(PatternInterface.SelectionItem) is not ISelectionItemProvider selection)
            throw new InvalidOperationException("The dropdown option has no native selection provider.");
        selection.Select();
        await SmokeWaitAsync(() => combo.SelectedIndex == selectedIndex, "Selecting the rendered dropdown option did not update the field.");
        expand.Collapse();
        await SmokeLayoutAsync();
        SmokeAssert(!combo.IsDropDownOpen && ReferenceEquals(combo.SelectedItem, combo.Items[selectedIndex]),
            "The native dropdown did not close with its chosen item selected.");
    }

    private static void SmokeDialogFrameRules(ContentDialog dialog)
    {
        var panel = SmokeFind<Border>(dialog, element => element.Name == "BackgroundElement");
        var drawer = Equals(dialog.Tag, "TaskDetail");
        foreach (var name in drawer ? new[] { "TitleBottomRule", "TitleSecondRule", "FooterPrimaryRule", "FooterSecondRule" }
            : new[] { "TitleBottomRule", "FooterPrimaryRule" })
        {
            var rule = SmokeFind<Border>(dialog, element => element.Name == name);
            SmokeAssert(SmokeRenderedVisible(rule, dialog) && Math.Abs(rule.ActualHeight - 1) < .1 && rule.Background is SolidColorBrush { Color.A: > 0 },
                $"Dialog boundary '{name}' is hidden or does not render its 1-DIP line.");
            SmokeAssert(Math.Abs(rule.ActualWidth - (panel.ActualWidth - panel.BorderThickness.Left - panel.BorderThickness.Right)) < 1,
                $"Dialog boundary '{name}' stops at padded content instead of spanning its panel.");
        }
        if (drawer)
        {
            foreach (var pair in new[] { ("TitleSecondRule", "TitleBottomRule"), ("FooterPrimaryRule", "FooterSecondRule") })
            {
                var upper = SmokeFind<Border>(dialog, element => element.Name == pair.Item1);
                var lower = SmokeFind<Border>(dialog, element => element.Name == pair.Item2);
                SmokeAssert(Math.Abs(SmokeBounds(lower, panel).Top - SmokeBounds(upper, panel).Bottom - 1) < .1,
                    "The drawer double rule must contain two distinct lines separated by one DIP.");
            }
        }
    }

    private static Button SmokeDialogButton(ContentDialog dialog, string templateName, string text)
    {
        var button = SmokeDescendants<Button>(dialog).FirstOrDefault(candidate => candidate.Name == templateName)
            ?? SmokeDescendants<Button>(dialog).FirstOrDefault(candidate => string.Equals(candidate.Content?.ToString(), text, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"Native dialog button '{text}' was not found in the rendered template.");
        SmokeAssert(string.Equals(button.Content?.ToString(), text, StringComparison.Ordinal), $"Native dialog button '{templateName}' has an unexpected label.");
        SmokeAssert(SmokeEffectivelyVisible(button, dialog) && button.IsEnabled && button.ActualWidth > 0 && button.ActualHeight > 0,
            $"Native dialog button '{text}' is hidden, disabled, or has no interactive layout.");
        var panel = SmokeFind<Border>(dialog, element => element.Name == "BackgroundElement");
        var commands = SmokeFind<Grid>(dialog, element => element.Name == "CommandSpace");
        SmokeFullyWithin(button, panel, $"Native dialog button '{text}'");
        SmokeFullyWithin(button, commands, $"Native dialog button '{text}'");
        var bounds = SmokeBounds(button, commands);
        foreach (var other in SmokeDescendants<Button>(commands).Where(candidate => candidate != button && SmokeEffectivelyVisible(candidate, dialog)))
        {
            var otherBounds = SmokeBounds(other, commands);
            SmokeAssert(bounds.Right <= otherBounds.Left + .5 || otherBounds.Right <= bounds.Left + .5 || bounds.Bottom <= otherBounds.Top + .5 || otherBounds.Bottom <= bounds.Top + .5,
                $"Native dialog button '{text}' overlaps visible '{other.Content}': {bounds} / {otherBounds}.");
        }
        return button;
    }

    private static async Task SmokeTaskDrawerActionsAsync(ContentDialog dialog)
    {
        var save = SmokeDialogButton(dialog, "PrimaryButton", "保存");
        var commands = SmokeFind<Grid>(dialog, element => element.Name == "CommandSpace");
        var column = Grid.GetColumn(save);
        var span = Grid.GetColumnSpan(save);
        SmokeAssert(column >= 0 && column + span <= commands.ColumnDefinitions.Count, "Task Save button uses an invalid footer column.");
        var available = commands.ColumnDefinitions.Skip(column).Take(span).Sum(definition => definition.ActualWidth);
        SmokeAssert(available > 0 && save.ActualWidth <= available + 1, $"Task Save button is clipped by its footer column: {save.ActualWidth} / {available} DIP.");
        var left = string.IsNullOrEmpty(dialog.SecondaryButtonText)
            ? SmokeDialogButton(dialog, "CloseButton", "取消")
            : SmokeDialogButton(dialog, "SecondaryButton", "删除");
        var saveBounds = SmokeBounds(save, commands);
        var leftBounds = SmokeBounds(left, commands);
        SmokeAssert(leftBounds.Right <= saveBounds.Left + .5, "Task Save button must be fully to the right of the delete/cancel action.");
        if (!string.IsNullOrEmpty(dialog.SecondaryButtonText))
        {
            var close = SmokeFind<Button>(dialog, candidate => candidate.Name == "CloseButton");
            SmokeAssert(!SmokeEffectivelyVisible(close, dialog), "The existing-task drawer exposes Cancel over its delete/save footer.");
        }
        SmokeAssert(left.Focus(FocusState.Keyboard), "The left task-footer action cannot receive keyboard focus.");
        // Desktop WinUI 1.8 rejects Next/Previous with SearchRoot (and requires
        // SearchRoot without it). Verify tree order and real directional focus;
        // physical Tab traversal remains explicitly outside this smoke's scope.
        var footerOrder = commands.Children.OfType<Button>().Where(button => SmokeEffectivelyVisible(button, commands))
            .OrderBy(button => button.TabIndex).ToArray();
        SmokeAssert(footerOrder.SequenceEqual(new[] { left, save }) && left.IsTabStop && save.IsTabStop && left.TabIndex < save.TabIndex,
            "The task-footer declared tab order differs from its left-to-right layout.");
        var movement = await FocusManager.TryMoveFocusAsync(FocusNavigationDirection.Right, new FindNextElementOptions { SearchRoot = commands });
        SmokeAssert(movement.Succeeded && ReferenceEquals(FocusManager.GetFocusedElement(dialog.XamlRoot), save),
            "The task-footer rightward focus does not reach Save from delete/cancel.");
    }

    private async Task SmokeInvalidFieldAsync(ContentDialog dialog, Control field)
    {
        await SmokeWaitAsync(() => SmokeContainsFocus(field), "Failed validation did not focus the invalid field.");
        await SmokeLayoutAsync();
        SmokeAssert(!string.IsNullOrWhiteSpace(AutomationProperties.GetHelpText(field)) && !string.IsNullOrWhiteSpace(AutomationProperties.GetItemStatus(field)),
            "The invalid field does not expose its validation message and invalid state to accessibility tools.");
        var inline = AutomationProperties.GetDescribedBy(field).OfType<TextBlock>()
            .SingleOrDefault(label => AutomationProperties.GetAutomationId(label) == "EditorFieldError");
        SmokeAssert(inline is not null && !string.IsNullOrWhiteSpace(inline.Text) && inline.ActualWidth > 0 && inline.ActualHeight > 0,
            "Validation has no rendered inline error associated with the invalid field.");
        SmokeEditorFieldVisible(dialog, field, "Focused invalid field");
        SmokeEditorFieldVisible(dialog, inline!, "Inline validation message");
    }

    private static bool SmokeContainsFocus(DependencyObject control)
    {
        if (control is not FrameworkElement element || element.XamlRoot is null) return false;
        for (var focused = FocusManager.GetFocusedElement(element.XamlRoot) as DependencyObject; focused is not null; focused = VisualTreeHelper.GetParent(focused))
            if (ReferenceEquals(focused, control)) return true;
        return false;
    }

    private static void SmokeEditorFieldVisible(ContentDialog dialog, FrameworkElement field, string label)
    {
        var panel = SmokeFind<Border>(dialog, element => element.Name == "BackgroundElement");
        var commands = SmokeFind<Grid>(dialog, element => element.Name == "CommandSpace");
        SmokeAssert(SmokeEffectivelyVisible(field, dialog), $"{label} is not effectively visible.");
        SmokeFullyWithin(field, panel, label);
        var bounds = SmokeBounds(field, panel); var footer = SmokeBounds(commands, panel);
        SmokeAssert(bounds.Bottom <= footer.Top + 1, $"{label} is covered by the fixed footer: {bounds}; footer {footer}.");
        for (var ancestor = VisualTreeHelper.GetParent(field); ancestor is not null && ancestor != dialog; ancestor = VisualTreeHelper.GetParent(ancestor))
        {
            if (ancestor is ScrollViewer scroll) SmokeFullyWithin(field, scroll, label);
            if (ancestor is ScrollContentPresenter viewport) SmokeFullyWithin(field, viewport, label + " in the content viewport");
        }
    }

    private static void SmokeProjectDatesVisible(ContentDialog dialog)
    {
        var panel = SmokeFind<Border>(dialog, element => element.Name == "BackgroundElement");
        var commands = SmokeFind<Grid>(dialog, element => element.Name == "CommandSpace");
        var footerBounds = SmokeBounds(commands, panel);
        foreach (var header in new[] { "开始日期", "结束日期" })
        {
            var date = SmokeFind<CalendarDatePicker>(dialog, field => Equals(field.Header, header));
            SmokeAssert(SmokeEffectivelyVisible(date, dialog) && date.IsEnabled && date.ActualWidth > 0 && date.ActualHeight > 0,
                $"Project {header} has no visible interactive layout.");
            SmokeFullyWithin(date, panel, $"Project {header}");
            var bounds = SmokeBounds(date, panel);
            SmokeAssert(bounds.Bottom <= footerBounds.Top + .5, $"Project {header} is covered by the footer: {bounds}; footer {footerBounds}.");
            var viewports = 0;
            for (var ancestor = VisualTreeHelper.GetParent(date); ancestor is not null && ancestor != dialog; ancestor = VisualTreeHelper.GetParent(ancestor))
            {
                if (ancestor is not ScrollViewer scroll) continue;
                viewports++;
                var viewportBounds = SmokeBounds(date, scroll);
                var padding = scroll.Padding;
                SmokeAssert(viewportBounds.Left >= padding.Left - 1 && viewportBounds.Top >= padding.Top - 1
                    && viewportBounds.Right <= scroll.ActualWidth - padding.Right + 1 && viewportBounds.Bottom <= scroll.ActualHeight - padding.Bottom + 1,
                    $"Project {header} is clipped in its initial scroll viewport: {viewportBounds}; viewer {scroll.ActualWidth}×{scroll.ActualHeight}, padding {padding}.");
            }
            SmokeAssert(viewports > 0, $"Project {header} has no body scroll viewport.");
        }
    }

    private static bool SmokeEffectivelyVisible(FrameworkElement element, DependencyObject ancestor)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement visual && (visual.Visibility != Visibility.Visible || !visual.IsHitTestVisible || visual.Opacity <= 0)) return false;
            if (ReferenceEquals(current, ancestor)) return true;
        }
        return false;
    }

    private static bool SmokeRenderedVisible(FrameworkElement element, DependencyObject ancestor)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement visual && (visual.Visibility != Visibility.Visible || visual.Opacity <= 0)) return false;
            if (ReferenceEquals(current, ancestor)) return true;
        }
        return false;
    }

    private static Windows.Foundation.Rect SmokeBounds(FrameworkElement element, UIElement relativeTo) =>
        element.TransformToVisual(relativeTo).TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));

    private static void SmokeFullyWithin(FrameworkElement element, FrameworkElement container, string label)
    {
        var bounds = SmokeBounds(element, container);
        SmokeAssert(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= container.ActualWidth + 1 && bounds.Bottom <= container.ActualHeight + 1,
            $"{label} is clipped by {container.Name}: {bounds}; container {container.ActualWidth}×{container.ActualHeight}.");
    }

    private static Button SmokeHeaderCloseButton(ContentDialog dialog)
    {
        var close = SmokeFind<Button>(dialog, button => button.Name == "HeaderCloseButton");
        SmokeAssert(close.Visibility == Visibility.Visible && close.IsHitTestVisible && close.ActualWidth > 0 && close.ActualHeight > 0,
            "The dialog's header close button is not visibly reachable.");
        SmokeAssert(close.IsEnabled && close.Focus(FocusState.Programmatic), "The dialog's header close button cannot receive keyboard focus.");
        return close;
    }

    private static T SmokeFind<T>(DependencyObject root, Func<T, bool> predicate) where T : DependencyObject =>
        SmokeDescendants<T>(root).FirstOrDefault(predicate) ?? throw new InvalidOperationException($"Expected {typeof(T).Name} was not found in the dialog.");

    private static IEnumerable<T> SmokeDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        var queue = new Queue<DependencyObject>(); queue.Enqueue(root);
        while (queue.TryDequeue(out var current))
        {
            if (current is T match) yield return match;
            var count = VisualTreeHelper.GetChildrenCount(current);
            for (var index = 0; index < count; index++) queue.Enqueue(VisualTreeHelper.GetChild(current, index));
        }
    }

    private void SmokeElementWithinRoot(FrameworkElement element, string label)
    {
        SmokeAssert(element.Visibility == Visibility.Visible && element.IsHitTestVisible && element.ActualWidth > 0 && element.ActualHeight > 0, $"{label} is hidden or has no interactive layout.");
        var bounds = element.TransformToVisual(Root).TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
        SmokeAssert(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= Root.ActualWidth + 1 && bounds.Bottom <= Root.ActualHeight + 1,
            $"{label} falls outside the client area: {bounds}; root {Root.ActualWidth}×{Root.ActualHeight}.");
    }

    private void SmokeNavigationActions()
    {
        foreach (var rule in new[] { TopBarDoubleRule, DashboardDoubleRule, ProjectsDoubleRule, SidebarActionsDoubleRule }
            .Concat(ProjectToolbar.Visibility == Visibility.Visible ? new[] { ViewTabsDoubleRule } : []))
        {
            var lines = rule.Children.OfType<Border>().OrderBy(line => SmokeBounds(line, rule).Top).ToArray();
            SmokeAssert(rule.ActualWidth > 0 && Math.Abs(rule.ActualHeight - 3) < .1 && lines.Length == 2
                && lines.All(line => Math.Abs(line.ActualWidth - rule.ActualWidth) < .1 && Math.Abs(line.ActualHeight - 1) < .1 && line.Background is SolidColorBrush { Color.A: > 0 })
                && Math.Abs(SmokeBounds(lines[1], rule).Top - SmokeBounds(lines[0], rule).Bottom - 1) < .1,
                $"Shell boundary '{rule.Name}' does not render two full-width one-DIP lines with a one-DIP gap.");
        }
        var sidebar = SmokeFind<FrameworkElement>(Root, element => element.Name == "SidebarPanel");
        var topbar = SmokeFind<FrameworkElement>(Root, element => element.Name == "TopBar");
        SmokeElementWithinRoot(sidebar, "Project sidebar");
        SmokeElementWithinRoot(topbar, "Application top bar");
        SmokeAssert(Math.Abs(sidebar.ActualWidth - 264) < 1, $"Sidebar width differs from V1.2: {sidebar.ActualWidth} DIP, expected 264.");
        SmokeAssert(Math.Abs(topbar.ActualHeight - 48) < 1, $"Top bar height differs from V1.2: {topbar.ActualHeight} DIP, expected 48.");
        SmokeElementWithinRoot(SmokeFind<FrameworkElement>(Root, element => element.Name == "SidebarProjectList"), "Sidebar project list");
        foreach (var name in new[] { "DashboardButton", "NavigationNewProjectButton", "NavigationImportButton", "TopSearchButton", "TopSettingsButton" })
        {
            var button = SmokeFind<Button>(Root, element => element.Name == name);
            SmokeControlWithinRoot(button, name);
            SmokeAssert(!string.IsNullOrWhiteSpace(Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(button)), $"Shell action {name} has no accessible name.");
        }
    }

    private Button[] SmokeViewTabButtons()
    {
        SmokeAssert(ProjectToolbar.Visibility == Visibility.Visible && Math.Abs(ProjectToolbar.ActualHeight - 47) < 1,
            $"The project view bar should be visible and 47 DIP high; actual {ProjectToolbar.ActualHeight}.");
        var host = SmokeFind<FrameworkElement>(Root, element => element.Name == "ViewTabsHost");
        var buttons = SmokeDescendants<Button>(host).Where(button => button.Visibility == Visibility.Visible).ToArray();
        SmokeAssert(buttons.Length == 5, $"Expected five visible project view tabs, found {buttons.Length}.");
        return buttons;
    }

    private void SmokeViewStructure(int view)
    {
        SmokeElementWithinRoot(ContentHost, "Project view content");
        foreach (var field in SmokeDescendants<TextBox>(ContentHost).Where(field => AutomationProperties.GetAutomationId(field).StartsWith("TaskQuickAdd:", StringComparison.Ordinal)
            && field.ActualWidth > 0 && field.ActualHeight > 0 && SmokeRenderedVisible(field, ContentHost)))
            SmokeSingleLineInputGeometry(field);
        var grids = SmokeDescendants<Grid>(ContentHost).ToArray();
        if (view == 0)
        {
            SmokeAssert(grids.Any(grid => grid.ColumnDefinitions.Count > 0 && grid.ColumnDefinitions.All(column => column.Width.IsAbsolute && Math.Abs(column.Width.Value - 272) < .5)), "Kanban is missing the fixed 272-DIP status columns.");
            if (Root.ActualWidth >= 1200)
                SmokeAssert(grids.Any(grid => grid.ColumnDefinitions.Any(column => column.Width.IsAbsolute && Math.Abs(column.Width.Value - 256) < .5)), "Wide kanban is missing its 256-DIP task-group sidebar.");
            SmokeAssert(SmokeDescendants<ListView>(ContentHost).Any(list => list.Items.Count > 0), "Kanban has no task cards for the populated task group.");
        }
        else if (view == 1)
        {
            SmokeAssert(SmokeDescendants<ListView>(ContentHost).Any(list => list.Items.Count > 0), "Task list has no rows for the populated task group.");
            SmokeAssert(grids.Any(grid => Math.Abs(grid.MinHeight - 58) < .5 && grid.ColumnDefinitions.Count == 7), "Task list is missing the compact 58-DIP row layout.");
        }
        else if (view == 2)
        {
            var calendar = SmokeFind<Grid>(ContentHost, grid => grid.Name == "CalendarMonthGrid");
            var days = calendar.Children.OfType<FrameworkElement>().Where(element => element.Name.StartsWith("CalendarDay", StringComparison.Ordinal)).ToArray();
            SmokeAssert(calendar.ColumnDefinitions.Count == 7 && calendar.RowDefinitions.Count == 7 && days.Length == 42, "Calendar must contain seven weekday columns and exactly 42 date cells.");
            SmokeAssert(days.All(day => day.ActualWidth > 0 && day.ActualHeight > 0), "A calendar date cell has no visible size.");
            foreach (var day in days)
            {
                var bounds = day.TransformToVisual(calendar).TransformBounds(new Windows.Foundation.Rect(0, 0, day.ActualWidth, day.ActualHeight));
                SmokeAssert(bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= calendar.ActualWidth + 1 && bounds.Bottom <= calendar.ActualHeight + 1,
                    $"Calendar date cell {day.Name} overflows its month grid.");
            }
        }
        else if (view == 3)
        {
            var canvas = SmokeFind<Canvas>(ContentHost, element => element.ActualWidth > 0 && element.ActualHeight > 0);
            SmokeAssert(SmokeDescendants<Button>(canvas).Any(button => Math.Abs(button.Height - 24) < .5), "Timeline is missing its 24-DIP task bars.");
            SmokeAssert(SmokeDescendants<Button>(canvas).Any(button => Math.Abs(button.Height - 36) < .5), "Timeline is missing its 36-DIP task rows.");
        }
        else if (view == 4)
        {
            var canvas = SmokeFind<Canvas>(ContentHost, element => element.ActualWidth > 0 && element.ActualHeight > 0);
            SmokeAssert(SmokeDescendants<Grid>(canvas).Any(grid => Math.Abs(grid.Width - 160) < .5 && Math.Abs(grid.Height - 76) < .5), "Dependency graph is missing the 160×76-DIP task nodes.");
            SmokeAssert(SmokeDescendants<Microsoft.UI.Xaml.Shapes.Path>(canvas).Any(path => path.Data is PathGeometry), "Dependency graph is missing its curved dependency connectors.");
        }
    }

    private void SmokeControlWithinRoot(Control control, string label)
    {
        SmokeElementWithinRoot(control, label);
        SmokeAssert(control.IsEnabled && control.Focus(FocusState.Programmatic), $"{label} cannot receive keyboard focus.");
    }

    private async Task<UiSmokeScreenshot> SmokeCaptureAsync(string directory, string fileName, FrameworkElement? target = null)
    {
        await SmokeLayoutAsync();
        var element = target ?? Root;
        element.UpdateLayout();
        RenderTargetBitmap? underlay = null;
        byte[]? underlayPixels = null;
        var composition = "root";
        string? compositionNote = null;
        if (target is not null)
        {
            underlay = new RenderTargetBitmap();
            await underlay.RenderAsync(Root);
            SmokeAssert(underlay.PixelWidth > 0 && underlay.PixelHeight > 0, "Window underlay rendering produced no pixels.");
            CryptographicBuffer.CopyToByteArray(await underlay.GetPixelsAsync(), out underlayPixels);
            SmokeAssert(underlayPixels.Length == checked(underlay.PixelWidth * underlay.PixelHeight * 4), "Window underlay pixel buffer has an unexpected length.");
        }
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element);
        SmokeAssert(bitmap.PixelWidth > 0 && bitmap.PixelHeight > 0, "RenderTargetBitmap produced no pixels.");
        var buffer = await bitmap.GetPixelsAsync();
        CryptographicBuffer.CopyToByteArray(buffer, out var pixels);
        SmokeAssert(pixels.Length == checked(bitmap.PixelWidth * bitmap.PixelHeight * 4), "Rendered pixel buffer has an unexpected length.");
        var first = BitConverter.ToUInt32(pixels, 0);
        var hasDetail = false;
        for (var offset = 4; offset + 4 <= pixels.Length; offset += 4)
            if (BitConverter.ToUInt32(pixels, offset) != first) { hasDetail = true; break; }
        SmokeAssert(hasDetail, "Rendered screenshot is a single flat color.");
        if (underlay is not null && underlayPixels is not null)
        {
            if (underlay.PixelWidth == bitmap.PixelWidth && underlay.PixelHeight == bitmap.PixelHeight)
            {
                // Both RenderTargetBitmap buffers are premultiplied BGRA. Composite the
                // application's own popup over its own root, including the popup scrim.
                for (var offset = 0; offset < pixels.Length; offset += 4)
                {
                    var inverseAlpha = 255 - pixels[offset + 3];
                    for (var channel = 0; channel < 4; channel++)
                        pixels[offset + channel] = (byte)Math.Min(255, pixels[offset + channel] + (underlayPixels[offset + channel] * inverseAlpha + 127) / 255);
                }
                composition = "popup-over-root";
            }
            else
            {
                composition = "popup-uncomposited-size-mismatch";
                compositionNote = $"Original popup pixels retained: popup {bitmap.PixelWidth}×{bitmap.PixelHeight}, root {underlay.PixelWidth}×{underlay.PixelHeight}.";
            }
        }
        var folder = await StorageFolder.GetFolderFromPathAsync(directory);
        var file = await folder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
        using (var stream = await file.OpenAsync(FileAccessMode.ReadWrite))
        {
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
            await encoder.FlushAsync();
            SmokeAssert(stream.Size > 100, "PNG encoder produced an empty file.");
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            SmokeAssert(decoder.PixelWidth == bitmap.PixelWidth && decoder.PixelHeight == bitmap.PixelHeight, "PNG dimensions differ from the rendered view.");
        }
        return new(file.Path, bitmap.PixelWidth, bitmap.PixelHeight, new FileInfo(file.Path).Length,
            element.ActualWidth, element.ActualHeight, Root.ActualWidth, Root.ActualHeight, Root.XamlRoot.RasterizationScale,
            composition, compositionNote, underlay?.PixelWidth, underlay?.PixelHeight);
    }

    private static void SmokeAssert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private sealed record UiSmokeStep(string Name, double ElapsedMilliseconds);
    private sealed record UiSmokeScreenshot(string Path, int Width, int Height, long Bytes,
        double TargetWidthDip, double TargetHeightDip, double ClientWidthDip, double ClientHeightDip, double RasterizationScale,
        string Composition, string? CompositionNote, int? UnderlayPixelWidth, int? UnderlayPixelHeight);
}
