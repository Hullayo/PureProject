using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PureProject.Core;
using PureProject.Infrastructure;

namespace PureProject.WinUI;

public sealed partial class MainWindow
{
    // Deliberately separate from the mutating smoke test. This audit reads a marked
    // synthetic fixture, changes only in-memory navigation/theme state, and exits.
    // Explicit "persistence" selection additionally saves into a NEW output copy;
    // the original fixture remains protected by before/after file hashes.
    // PNG work and diagnostic enumeration are excluded from interaction timings.
    private async Task RunScaleUiSmokeTestsAsync()
    {
        var started = DateTimeOffset.UtcNow;
        var scenes = new List<ScaleSceneResult>();
        string? output = null, data = null, failure = null, activeScene = null;
        Dictionary<string, string>? beforeFiles = null, afterFiles = null;
        object? fixture = null;
        object? persistence = null;
        var tasksPerStatus = 20;
        var tasksPerGroup = 200;
        var tasksPerProject = 2000;
        var totalTasks = 20000;
        var selection = (Environment.GetEnvironmentVariable("PUREPROJECT_UI_SCALE_SCENARIOS") ?? "all")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var options = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        async Task Progress(string phase)
        {
            if (output is null) return;
            await File.WriteAllTextAsync(Path.Combine(output, "scale-ui-progress.json"), JsonSerializer.Serialize(new
            {
                phase, activeScene, startedAt = started, updatedAt = DateTimeOffset.UtcNow,
                processId = Environment.ProcessId, dataDirectory = data, outputDirectory = output,
                selectedScenarios = selection, completedScenes = scenes.Count, fixture,
                memory = ScaleMemory(), scenes
            }, options));
            await File.AppendAllTextAsync(Path.Combine(output, "scale-ui-progress.log"),
                $"{DateTimeOffset.UtcNow:O} {phase} {activeScene ?? "startup"}{Environment.NewLine}");
        }

        async Task Scene(string id, string group, Func<Task<FrameworkElement?>> action, Func<object?>? observe = null)
        {
            if (!selection.Contains("all") && !selection.Contains(group) && !selection.Contains(id)) return;
            activeScene = id;
            // This must reach disk before any potentially expensive synchronous Render.
            await Progress("START");
            var entry = new ScaleSceneResult { Id = id, Group = group, StartedAt = DateTimeOffset.UtcNow, MemoryBefore = ScaleMemory() };
            var timer = Stopwatch.StartNew();
            FrameworkElement? target = null;
            Exception? fatalNativeFailure = null;
            try
            {
                target = await action();
                await ScaleRenderedLayoutAsync();
                timer.Stop();
                entry.RenderAndLayoutElapsedMilliseconds = timer.Elapsed.TotalMilliseconds;
                entry.MemoryAfterRender = ScaleMemory();
                entry.Surface = ScaleSurface(target ?? ContentHost);
                entry.Observation = observe?.Invoke();
                if (group == "list") ScaleAssertListVirtualized(SmokeFind<ListView>(ContentHost, l => l.Items.Count == tasksPerProject));
                entry.Success = true;
            }
            catch (Exception error)
            {
                timer.Stop();
                entry.RenderAndLayoutElapsedMilliseconds = timer.Elapsed.TotalMilliseconds;
                entry.MemoryAfterRender = ScaleMemory();
                entry.Exception = error.ToString();
                if (error is ScaleNativeTimeoutException) fatalNativeFailure = error;
                try { entry.Surface = ScaleSurface(target ?? (FrameworkElement?)_currentActiveDialog ?? ContentHost); }
                catch (Exception diagnosticError) { entry.DiagnosticError = diagnosticError.ToString(); }
            }
            var capture = Stopwatch.StartNew();
            try
            {
                if (fatalNativeFailure is null)
                {
                    entry.Screenshots.Add(await ScaleNativeDeadlineAsync(SmokeCaptureAsync(output!, id + "-root.png"), TimeSpan.FromSeconds(15), "Root PNG capture"));
                    if ((target ?? _currentActiveDialog) is FrameworkElement popup)
                        entry.Screenshots.Add(await ScaleNativeDeadlineAsync(SmokeCaptureAsync(output!, id + "-dialog.png", popup), TimeSpan.FromSeconds(15), "Popup PNG capture"));
                }
            }
            catch (Exception error)
            {
                entry.CaptureException = error.ToString(); entry.Success = false;
                if (error is ScaleNativeTimeoutException) fatalNativeFailure = error;
            }
            finally { capture.Stop(); entry.CaptureElapsedMilliseconds = capture.Elapsed.TotalMilliseconds; }
            entry.MemoryAfterCapture = ScaleMemory();
            entry.FinishedAt = DateTimeOffset.UtcNow;
            scenes.Add(entry);
            await Progress(entry.Success ? "PASS" : "FAIL");
            if (fatalNativeFailure is not null)
                throw new InvalidOperationException("Scale audit stopped after a native rendering deadline; no further native requests were queued.", fatalNativeFailure);
        }

        try
        {
            (data, output) = ScaleAuditPaths();
            Directory.CreateDirectory(output);
            await Progress("VALIDATING");
            beforeFiles = await ScaleFileHashesAsync(data);
            var markerText = await File.ReadAllTextAsync(Path.Combine(data, "scale-fixture.json"));
            using var marker = JsonDocument.Parse(markerText);
            ScaleValidateMarker(marker.RootElement, data);
            tasksPerStatus = marker.RootElement.GetProperty("dimensions").GetProperty("tasksPerStatus").GetInt32();
            tasksPerGroup = 10 * tasksPerStatus;
            tasksPerProject = 10 * tasksPerGroup;
            totalTasks = 10 * tasksPerProject;
            // Identity/path checks precede the session lock. Load through the real
            // repository once; a duplicate parse would inflate memory measurements.
            _repository = new JsonProjectRepository(data);
            _projects = await _repository.LoadAsync();
            fixture = ScaleValidateFixture(_projects, marker.RootElement, beforeFiles);
            _settingsRepository = new SettingsRepository(data);
            _settings = await _settingsRepository.LoadAsync();
            _settingsLoaded = true;
            _ready = true;
            _reminderTimer?.Stop();
            var project = _projects.OrderBy(p => p.SortOrder).First();
            var groups = project.TaskGroups.OrderBy(g => g.SortOrder).ToArray();
            var firstGroup = groups[0];
            var lastGroup = groups[^1];
            var lastStatus = firstGroup.Statuses.OrderBy(s => s.SortOrder).Last();
            var representative = project.Tasks.First(t => t.TaskGroupId == firstGroup.Id);
            var finalProject = _projects.OrderBy(p => p.SortOrder).Last();
            var finalTask = finalProject.Tasks.Last();
            _calendarDate = DateTimeOffset.Parse(marker.RootElement.GetProperty("referenceDate").GetString()! + "T12:00:00+08:00");
            await Progress("VALIDATED");

            foreach (var theme in new[] { "Dark", "Light" })
                await Scene("dashboard-" + theme.ToLowerInvariant(), "dashboard", () =>
                {
                    ScaleSelect(null, null, 0, theme);
                    SmokeAssert(SidebarExportButton.IsEnabled, "Dashboard must allow exporting all projects after data has loaded.");
                    return Task.FromResult<FrameworkElement?>(null);
                }, () => new { actualProjects = _projects.Count, actualTasks = _projects.Sum(p => p.Tasks.Count), theme = Root.ActualTheme.ToString(), exportEnabled = SidebarExportButton.IsEnabled });

            await Scene("kanban-first-group", "kanban", () =>
            {
                ScaleSelect(project.Id, firstGroup.Id, 0, "Dark");
                return Task.FromResult<FrameworkElement?>(null);
            }, () => new { projectId = project.Id, groupId = firstGroup.Id, actualTasks = project.Tasks.Count(t => t.TaskGroupId == firstGroup.Id), statuses = firstGroup.Statuses.Count });

            await Scene("kanban-status10-tail", "kanban", async () =>
            {
                await ScaleEnsureViewAsync(project.Id, firstGroup.Id, 0);
                var outline = SmokeFind<FrameworkElement>(ContentHost, e => AutomationProperties.GetAutomationId(e) == "TaskStatusOutline:" + lastStatus.Id);
                var horizontal = SmokeDescendants<ScrollViewer>(ContentHost).OrderByDescending(s => s.ScrollableWidth).First();
                horizontal.ChangeView(horizontal.ScrollableWidth, null, null, true);
                await ScaleRenderedLayoutAsync();
                var list = SmokeFind<ListView>(outline, _ => true);
                await ScaleListTailAsync(list);
                return null;
            }, () => new { groupId = firstGroup.Id, statusId = lastStatus.Id, actualTasksInColumn = tasksPerStatus, horizontal = ScaleHorizontalScroll(), tail = ScaleTaskListTail(lastStatus.Id) });

            await Scene("kanban-group10", "kanban", async () =>
            {
                await ScaleEnsureViewAsync(project.Id, firstGroup.Id, 0);
                var choice = SmokeFind<Button>(ContentHost, b => AutomationProperties.GetAutomationId(b) == "KanbanGroup:" + lastGroup.Id);
                if (Root.ActualWidth < 1200)
                    SmokeInvoke(SmokeFind<Button>(ContentHost, b => AutomationProperties.GetAutomationId(b) == "KanbanGroupDrawerToggle"));
                choice.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
                await SmokeLayoutAsync();
                SmokeInvoke(choice);
                await SmokeWaitAsync(() => _groupId == lastGroup.Id, "The tenth task group was not selected.");
                return null;
            }, () => new { selectedGroupId = _groupId, expectedGroupId = lastGroup.Id, actualTasks = project.Tasks.Count(t => t.TaskGroupId == lastGroup.Id) });

            Task? editing = null;
            double? editorOpenElapsedMilliseconds = null;
            await Scene($"editor-{tasksPerProject}-task-project", "editor", async () =>
            {
                await ScaleEnsureViewAsync(project.Id, firstGroup.Id, 0);
                var opened = Stopwatch.StartNew();
                editing = EditTaskAsync(representative.Id, project.Id);
                var dialog = await SmokeWaitForDialogAsync(editing);
                await ScaleRenderedLayoutAsync();
                editorOpenElapsedMilliseconds = opened.Elapsed.TotalMilliseconds;
                return dialog;
            }, () => new { editorOpenElapsedMilliseconds, editor = ScaleEditorObservation(project, representative) });
            await ScaleDismissDialogAsync(editing);

            Task? dependencyEditing = null;
            var dependencyObservation = new Dictionary<string, object?>();
            await Scene("editor-dependency-search", "editor", async () =>
            {
                await ScaleEnsureViewAsync(project.Id, firstGroup.Id, 0);
                dependencyEditing = EditTaskAsync(representative.Id, project.Id);
                var dialog = await SmokeWaitForDialogAsync(dependencyEditing);
                var query = SmokeFind<TextBox>(dialog, t => AutomationProperties.GetAutomationId(t) == "TaskDependencySearch");
                var picker = SmokeFind<ComboBox>(dialog, c => AutomationProperties.GetAutomationId(c) == "TaskDependencyPicker");
                var add = SmokeFind<Button>(dialog, b => AutomationProperties.GetAutomationId(b) == "TaskDependencyAdd");
                var candidate = project.Tasks.First(t => t.Id != representative.Id && representative.Dependencies.All(d => d.TaskId != t.Id));
                query.Text = "scale-no-match-dependency-query";
                await SmokeLayoutAsync();
                dependencyObservation["noResultCount"] = picker.Items.Count;
                dependencyObservation["noResultAddDisabled"] = !add.IsEnabled;
                SmokeAssert(picker.Items.Count == 0 && !add.IsEnabled, "An unmatched dependency query must leave an empty picker and disabled add button.");
                var candidateGroup = project.TaskGroups.Single(g => g.Id == candidate.TaskGroupId);
                query.Text = candidateGroup.Name;
                await SmokeLayoutAsync();
                var expectedGroupMatches = project.Tasks.Count(t => t.Id != representative.Id && representative.Dependencies.All(d => d.TaskId != t.Id)
                    && (t.Title.Contains(candidateGroup.Name, StringComparison.OrdinalIgnoreCase)
                        || project.TaskGroups.Single(g => g.Id == t.TaskGroupId).Name.Contains(candidateGroup.Name, StringComparison.OrdinalIgnoreCase)));
                dependencyObservation["groupQuery"] = candidateGroup.Name;
                dependencyObservation["groupQueryExpectedCount"] = expectedGroupMatches;
                dependencyObservation["groupQueryActualCount"] = picker.Items.Count;
                SmokeAssert(picker.Items.Count == expectedGroupMatches, "Dependency group-name filtering returned an unexpected candidate count.");
                query.Text = candidate.Title;
                await SmokeLayoutAsync();
                SmokeAssert(picker.Items.Count == 1 && picker.Items[0] is ProjectTask item && item.Id == candidate.Id, "Exact dependency title filtering did not retain its candidate.");
                picker.SelectedIndex = 0;
                await SmokeLayoutAsync();
                SmokeInvoke(add);
                await SmokeLayoutAsync();
                var offset = SmokeFind<NumberBox>(dialog, n => AutomationProperties.GetAutomationId(n) == "TaskDependencyOffset:" + candidate.Id);
                dependencyObservation["candidateId"] = candidate.Id;
                dependencyObservation["addedOffsetPresent"] = true;
                dependencyObservation["addedCandidateRemovedFromPicker"] = picker.Items.Count == 0;
                SmokeAssert(picker.Items.Count == 0, "An added dependency stayed in the picker.");
                query.Text = lastGroup.Name;
                await SmokeLayoutAsync();
                var preserved = ReferenceEquals(offset, SmokeFind<NumberBox>(dialog, n => AutomationProperties.GetAutomationId(n) == "TaskDependencyOffset:" + candidate.Id));
                dependencyObservation["selectedOffsetInstancePreservedAcrossQuery"] = preserved;
                SmokeAssert(preserved, "Changing dependency query replaced the existing offset input.");
                query.Text = candidate.Title;
                await SmokeLayoutAsync();
                SmokeInvoke(SmokeFind<Button>(dialog, b => AutomationProperties.GetAutomationId(b) == "TaskDependencyRemove:" + candidate.Id));
                await SmokeLayoutAsync();
                dependencyObservation["removedCandidateRestored"] = picker.Items.Count == 1 && picker.Items[0] is ProjectTask restored && restored.Id == candidate.Id;
                dependencyObservation["queryPreservedOnRemove"] = query.Text == candidate.Title;
                SmokeAssert((bool)dependencyObservation["removedCandidateRestored"]! && (bool)dependencyObservation["queryPreservedOnRemove"]!, "Removing the dependency did not restore the filtered candidate.");
                query.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
                return dialog;
            }, () => dependencyObservation);
            await ScaleDismissDialogAsync(dependencyEditing);

            Task? searching = null;
            await Scene($"search-across-{totalTasks}", "search", async () =>
            {
                searching = ShowGlobalSearchAsync();
                var dialog = await SmokeWaitForDialogAsync(searching);
                SmokeFind<TextBox>(dialog, t => AutomationProperties.GetAutomationId(t) == "GlobalSearchInput").Text = "任务";
                return dialog;
            }, () => ScaleSearchObservation("任务", null));
            await ScaleDismissDialogAsync(searching);
            searching = null;
            await Scene("search-final-project-task", "search", async () =>
            {
                searching = ShowGlobalSearchAsync();
                var dialog = await SmokeWaitForDialogAsync(searching);
                SmokeFind<TextBox>(dialog, t => AutomationProperties.GetAutomationId(t) == "GlobalSearchInput").Text = finalTask.Title;
                return dialog;
            }, () => ScaleSearchObservation(finalTask.Title, finalTask.Id));
            await ScaleDismissDialogAsync(searching);

            await Scene($"list-all-{tasksPerProject}", "list", () =>
            {
                ScaleSelect(project.Id, null, 1, "Dark");
                return Task.FromResult<FrameworkElement?>(null);
            }, () => new { projectId = project.Id, actualTasks = project.Tasks.Count, allTaskGroups = _groupId is null });
            await Scene($"list-all-{tasksPerProject}-tail", "list", async () =>
            {
                await ScaleEnsureViewAsync(project.Id, null, 1);
                await ScaleListTailAsync(SmokeFind<ListView>(ContentHost, l => l.Items.Count == tasksPerProject));
                return null;
            }, () => ScaleTaskListTail());

            MenuFlyout? tailMenu = null;
            var tailMenuObservation = new Dictionary<string, object?>();
            await Scene("list-menu-tail", "list", async () =>
            {
                await ScaleEnsureViewAsync(project.Id, null, 1);
                var list = SmokeFind<ListView>(ContentHost, l => l.Items.Count == tasksPerProject);
                await ScaleListTailAsync(list);
                var tailId = ((TaskRow)list.Items[^1]).Id;
                tailMenu = await SmokeOpenTaskMenuAsync(tailId);
                tailMenuObservation["taskId"] = tailId;
                tailMenuObservation["openedMenuItemsIncludingSubmenus"] = ScaleMenuItemCount(tailMenu.Items);
                tailMenuObservation["populatedMoreMenusInVisualTree"] = SmokeDescendants<Button>(ContentHost)
                    .Count(b => AutomationProperties.GetAutomationId(b).StartsWith("TaskMore:", StringComparison.Ordinal)
                        && b.Flyout is MenuFlyout menu && menu.Items.Count > 0);
                return ConsistencyPopupElements<MenuFlyoutPresenter>().First(p => p.ActualWidth > 0 && p.ActualHeight > 0);
            }, () => tailMenuObservation);
            if (tailMenu is not null)
            {
                tailMenu.Hide();
                await SmokeWaitAsync(() => !tailMenu.IsOpen, "The tail task menu did not close.");
                await SmokeLayoutAsync();
                tailMenuObservation["closedMenuItemsIncludingSubmenus"] = ScaleMenuItemCount(tailMenu.Items);
                tailMenuObservation["closedMenuIsEmpty"] = tailMenu.Items.Count == 0;
            }
            ProjectTask? tailEditingTask = null;
            await Scene("list-editor-tail", "list", async () =>
            {
                await ScaleEnsureViewAsync(project.Id, null, 1);
                var list = SmokeFind<ListView>(ContentHost, l => l.Items.Count == tasksPerProject);
                await ScaleListTailAsync(list);
                var tailId = ((TaskRow)list.Items[^1]).Id;
                tailEditingTask = project.Tasks.Single(t => t.Id == tailId);
                // Exercise the actual task menu's native invoke provider instead
                // of calling the editor directly for this recycled tail row.
                await SmokeTaskMenuActionAsync(tailId, "open");
                return await SmokeWaitForDialogAsync();
            }, () => new
            {
                editor = ScaleEditorObservation(project, tailEditingTask!),
                actualTitle = SmokeFind<TextBox>(_currentActiveDialog!, t => AutomationProperties.GetAutomationId(t) == "TaskEditorTitle").Text,
                expectedTitle = tailEditingTask!.Title
            });
            await ScaleDismissDialogAsync(null);

            foreach (var view in new[] { (Index: 2, Group: "calendar"), (Index: 3, Group: "timeline"), (Index: 4, Group: "dependencies") })
            {
                await Scene(view.Group + $"-all-{tasksPerProject}", view.Group, () =>
                {
                    ScaleSelect(project.Id, null, view.Index, "Dark");
                    return Task.FromResult<FrameworkElement?>(null);
                }, () => new
                {
                    actualSourceTasks = project.Tasks.Count,
                    actualDatedTasks = project.Tasks.Count(t => DateOnly.TryParse(t.DueDate, out _)),
                    limitMessages = SmokeDescendants<TextBlock>(ContentHost).Select(t => t.Text).Where(t => t.Contains("超过") || t.Contains("前 ") || t.Contains("缩小")).Distinct().ToArray(),
                    taskGroupFilterPresent = SmokeDescendants<ComboBox>(ContentHost).Any(c => AutomationProperties.GetAutomationId(c) == "TaskGroupFilter"),
                    calendarDateCells = SmokeDescendants<FrameworkElement>(ContentHost).Count(e => e.Name.StartsWith("CalendarDay", StringComparison.Ordinal)),
                    calendarOverflowLabels = SmokeDescendants<Button>(ContentHost).Where(b => AutomationProperties.GetAutomationId(b).StartsWith("CalendarSummary:", StringComparison.Ordinal)).Select(b => b.Content?.ToString()).ToArray(),
                    calendarDateButtons = SmokeDescendants<Button>(ContentHost).Count(b => AutomationProperties.GetAutomationId(b).StartsWith("CalendarDate:", StringComparison.Ordinal)),
                    calendarMaximumLinesPerCell = SmokeDescendants<FrameworkElement>(ContentHost).Where(e => e.Name.StartsWith("CalendarDay", StringComparison.Ordinal))
                        .Select(e => SmokeDescendants<Border>(e).Count(b => Math.Abs(b.Height - 3) < .01)).DefaultIfEmpty(0).Max()
                });
                if (view.Index is 3 or 4)
                    await Scene(view.Group + $"-first-group-{tasksPerGroup}", view.Group, () =>
                    {
                        // The baseline overflow view may omit the group selector.
                        // Use memory-only navigation so later scenes remain reviewable.
                        ScaleSelect(project.Id, firstGroup.Id, view.Index, "Dark");
                        return Task.FromResult<FrameworkElement?>(null);
                    }, () => new
                    {
                        actualSourceTasks = project.Tasks.Count(t => t.TaskGroupId == firstGroup.Id),
                        taskGroupFilterPresent = SmokeDescendants<ComboBox>(ContentHost).Any(c => AutomationProperties.GetAutomationId(c) == "TaskGroupFilter"),
                        renderedCanvasCount = SmokeDescendants<Canvas>(ContentHost).Count(),
                        renderedTaskActions = SmokeDescendants<FrameworkElement>(ContentHost).Count(e => AutomationProperties.GetAutomationId(e).StartsWith("TaskOpen:", StringComparison.Ordinal))
                    });
                if (view.Index is 3 or 4 && tasksPerGroup > 200)
                    await Scene(view.Group + $"-first-status-{tasksPerStatus}", view.Group, () =>
                    {
                        ScaleSelect(project.Id, firstGroup.Id, view.Index, "Dark", firstGroup.Statuses.OrderBy(s => s.SortOrder).First().Id);
                        return Task.FromResult<FrameworkElement?>(null);
                    }, () => new
                    {
                        statusId = _graphStatusId,
                        actualSourceTasks = GraphTasks(project).Count(),
                        statusFilterPresent = SmokeDescendants<ComboBox>(ContentHost).Any(c => AutomationProperties.GetAutomationId(c) == "GraphStatusFilter"),
                        renderedCanvasCount = SmokeDescendants<Canvas>(ContentHost).Count(),
                        renderedTaskActions = SmokeDescendants<FrameworkElement>(ContentHost).Count(e => AutomationProperties.GetAutomationId(e).StartsWith("TaskOpen:", StringComparison.Ordinal))
                    });
                if (view.Index == 2)
                    await Scene("calendar-day-details", "calendar", async () =>
                    {
                        await ScaleEnsureViewAsync(project.Id, null, 2);
                        var selected = DateOnly.FromDateTime(_calendarDate.DateTime);
                        SmokeInvoke(SmokeFind<Button>(ContentHost, b => AutomationProperties.GetAutomationId(b) == $"CalendarDate:{selected:yyyy-MM-dd}"));
                        await ScaleRenderedLayoutAsync();
                        var dailyList = SmokeFind<ListView>(ContentHost, l => l.Items.Count > 0);
                        await ScaleListTailAsync(dailyList);
                        return null;
                    }, () =>
                    {
                        var selected = DateOnly.FromDateTime(_calendarDate.DateTime);
                        var expected = project.Tasks.Where(t => DateOnly.TryParse(t.DueDate, out var due) && TaskStart(project, t) <= selected && selected <= due).ToArray();
                        var dailyList = SmokeFind<ListView>(ContentHost, l => l.Items.Count > 0);
                        return new { selectedDate = selected, expectedTaskCount = expected.Length, actualListItems = dailyList.Items.Count,
                            exactTaskIdsMatch = dailyList.Items.OfType<TaskRow>().Select(t => t.Id).SequenceEqual(expected.Select(t => t.Id)), tail = ScaleTaskListTail() };
                    });
            }

            await Scene("compact-dashboard-1024x768", "compact", async () =>
            {
                await ScaleResizeAsync(1024, 768);
                ScaleSelect(null, null, 0, "Dark");
                return null;
            });
            await Scene("compact-kanban-1024x768", "compact", async () =>
            {
                await ScaleResizeAsync(1024, 768);
                ScaleSelect(project.Id, firstGroup.Id, 0, "Dark");
                return null;
            });
            await Scene("compact-group10-drawer", "compact", async () =>
            {
                await ScaleResizeAsync(1024, 768);
                await ScaleEnsureViewAsync(project.Id, firstGroup.Id, 0);
                SmokeInvoke(SmokeFind<Button>(ContentHost, b => AutomationProperties.GetAutomationId(b) == "KanbanGroupDrawerToggle"));
                await SmokeLayoutAsync();
                var choice = SmokeFind<Button>(ContentHost, b => AutomationProperties.GetAutomationId(b) == "KanbanGroup:" + lastGroup.Id);
                choice.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
                return null;
            }, () => new { tenthGroup = ScaleElementPosition(SmokeFind<Button>(ContentHost, b => AutomationProperties.GetAutomationId(b) == "KanbanGroup:" + lastGroup.Id), Root) });
            await Scene($"compact-list-{tasksPerProject}", "compact", async () =>
            {
                await ScaleResizeAsync(1024, 768);
                ScaleSelect(project.Id, null, 1, "Dark");
                return null;
            });
            editing = null;
            editorOpenElapsedMilliseconds = null;
            await Scene($"compact-editor-{tasksPerProject}-task-project", "compact", async () =>
            {
                await ScaleResizeAsync(1024, 768);
                await ScaleEnsureViewAsync(project.Id, firstGroup.Id, 0);
                var opened = Stopwatch.StartNew();
                editing = EditTaskAsync(representative.Id, project.Id);
                var dialog = await SmokeWaitForDialogAsync(editing);
                await ScaleRenderedLayoutAsync();
                editorOpenElapsedMilliseconds = opened.Elapsed.TotalMilliseconds;
                return dialog;
            }, () => new { editorOpenElapsedMilliseconds, editor = ScaleEditorObservation(project, representative) });
            await ScaleDismissDialogAsync(editing);
            // Explicit soak selection runs 30 rounds / 120 native navigations.
            // Only the first and final scene are captured, keeping PNG allocation
            // outside the per-navigation timings and repeated memory samples.
            if (selection.Contains("soak"))
            {
                var samples = new List<object>();
                await Scene("soak-first-list", "soak", async () =>
                {
                    await ScaleResizeAsync(1440, 1000);
                    ScaleSelect(project.Id, null, 1, "Dark");
                    await ScaleRenderedLayoutAsync();
                    var list = SmokeFind<ListView>(ContentHost, l => l.Items.Count == tasksPerProject);
                    await ScaleListTailAsync(list);
                    return null;
                }, () => ScaleTaskListTail());
                SmokeAssert(scenes[^1].Success, "Soak did not acquire its initial virtualized list.");
                await Scene("soak-30-rounds-120-navigations", "soak", async () =>
                {
                    var orderedProjects = _projects.OrderBy(p => p.SortOrder).ToArray();
                    for (var round = 0; round < 30; round++)
                    {
                        var current = orderedProjects[round % orderedProjects.Length];
                        var group = current.TaskGroups.OrderBy(g => g.SortOrder).ElementAt((round / orderedProjects.Length) % current.TaskGroups.Count);
                        var status = group.Statuses.OrderBy(s => s.SortOrder).First();
                        foreach (var kind in new[] { "list-tail", "kanban", "calendar", "graph-status" })
                        {
                            var interaction = Stopwatch.StartNew();
                            var memoryBefore = ScaleMemory();
                            var view = kind switch { "list-tail" => 1, "kanban" => 0, "calendar" => 2, _ => round % 2 == 0 ? 3 : 4 };
                            ScaleSelect(current.Id, kind is "kanban" or "graph-status" ? group.Id : null, view, "Dark", kind == "graph-status" ? status.Id : null);
                            await ScaleRenderedLayoutAsync();
                            if (kind == "list-tail")
                            {
                                var list = SmokeFind<ListView>(ContentHost, l => l.Items.Count == tasksPerProject);
                                await ScaleListTailAsync(list);
                                ScaleAssertListVirtualized(list);
                            }
                            interaction.Stop();
                            var memoryAfterRender = ScaleMemory();
                            var lists = SmokeDescendants<ListView>(ContentHost).Select(list => new
                            {
                                items = list.Items.Count,
                                realized = SmokeDescendants<ListViewItem>(list).Count(),
                                taskContents = list.Items.OfType<TaskRow>().Count(row => row.Content is not null)
                            }).ToArray();
                            foreach (var list in lists)
                                SmokeAssert(list.realized < 200 && list.taskContents < 200, "Soak found unbounded task list realization.");
                            samples.Add(new { round = round + 1, interaction = samples.Count + 1, kind, projectId = current.Id, groupId = group.Id,
                                statusId = kind == "graph-status" ? status.Id : null, projectTasks = current.Tasks.Count,
                                elapsedMilliseconds = interaction.Elapsed.TotalMilliseconds, memoryBefore, memoryAfterRender, lists,
                                tail = kind == "list-tail" ? ScaleTaskListTail() : null });
                        }
                        await File.WriteAllTextAsync(Path.Combine(output, "soak-samples.json"), JsonSerializer.Serialize(new
                        {
                            completedRounds = round + 1, completedInteractions = samples.Count, processId = Environment.ProcessId,
                            measurement = "Each sample measures navigation plus native layout and list tail scrolling. Diagnostics and PNG work are excluded. No forced GC; first scene PNG was captured before the loop. Final PNG is captured after all samples.",
                            samples
                        }, options));
                        await Progress("SOAK_ROUND_" + (round + 1));
                    }
                    SmokeAssert(samples.Count == 120, "The soak must complete all 120 navigation interactions.");
                    return null;
                }, () => new { rounds = 30, interactions = samples.Count, samplesPath = Path.Combine(output, "soak-samples.json"), screenshotsDuringLoop = 0 });
            }
            // Writes require explicit selection: default "all" stays read-only.
            if (selection.Contains("persistence"))
            {
                var copy = Path.Combine(output, "mutation-data-" + Guid.NewGuid().ToString("N"));
                var copyTimer = Stopwatch.StartNew();
                _repository.Dispose();
                _repository = new JsonProjectRepository(copy);
                _ = await _repository.LoadAsync();
                await _repository.SaveAsync(_projects);
                _settingsRepository = new SettingsRepository(copy);
                await _settingsRepository.SaveAsync(_settings);
                _undo.Clear(); _redo.Clear();
                var savedTitle = finalTask.Title + $"｜{totalTasks} 任务库原生保存与历史验证";
                var originalTitle = finalTask.Title;
                var observations = new Dictionary<string, object?>
                {
                    ["mutationDirectory"] = copy, ["copyElapsedMilliseconds"] = copyTimer.Elapsed.TotalMilliseconds,
                    ["projectId"] = finalProject.Id, ["taskId"] = finalTask.Id,
                    ["originalTitle"] = originalTitle, ["savedTitle"] = savedTitle,
                    ["reloadMeaning"] = "Repository disposed and reopened in the same GUI process; external process restart is a separate validation."
                };
                persistence = observations;
                async Task AssertPersistedTitleAsync(string expected)
                {
                    var disk = await _repository.LoadAsync();
                    SmokeAssert(disk.Count == _projects.Count && disk.Sum(p => p.Tasks.Count) == totalTasks, "Persistence changed library counts.");
                    SmokeAssert(disk.Single(p => p.Id == finalProject.Id).Tasks.Single(t => t.Id == finalTask.Id).Title == expected, "Expected task title is absent from the saved repository.");
                    SmokeAssert(_projects.Single(p => p.Id == finalProject.Id).Tasks.Single(t => t.Id == finalTask.Id).Title == expected, "Memory and saved repository disagree.");
                }
                await Scene("persistence-native-editor-save", "persistence", async () =>
                {
                    await ScaleResizeAsync(1440, 1000);
                    ScaleSelect(finalProject.Id, finalTask.TaskGroupId, 0, "Dark");
                    var edit = EditTaskAsync(finalTask.Id, finalProject.Id);
                    var dialog = await SmokeWaitForDialogAsync(edit);
                    SmokeFind<TextBox>(dialog, t => AutomationProperties.GetAutomationId(t) == "TaskEditorTitle").Text = savedTitle;
                    SmokeInvoke(SmokeDialogButton(dialog, "PrimaryButton", "保存"));
                    await SmokeAwaitDialogAsync(edit, dialog);
                    await AssertPersistedTitleAsync(savedTitle);
                    SmokeAssert(_undo.Count == 1 && _redo.Count == 0, "Saving did not produce exactly one undo entry.");
                    observations["saveVerified"] = true;
                    return null;
                }, () => new { undo = _undo.Count, redo = _redo.Count, totalTasks });
                SmokeAssert(scenes[^1].Success, "Stopping persistence checks after save failure.");
                await Scene("persistence-undo", "persistence", async () =>
                {
                    await UndoAsync();
                    await AssertPersistedTitleAsync(originalTitle);
                    SmokeAssert(_undo.Count == 0 && _redo.Count == 1, "Undo did not transfer the history entry.");
                    observations["undoVerified"] = true;
                    return null;
                }, () => new { undo = _undo.Count, redo = _redo.Count });
                SmokeAssert(scenes[^1].Success, "Stopping persistence checks after undo failure.");
                await Scene("persistence-redo", "persistence", async () =>
                {
                    await RedoAsync();
                    await AssertPersistedTitleAsync(savedTitle);
                    SmokeAssert(_undo.Count == 1 && _redo.Count == 0, "Redo did not restore the history entry.");
                    observations["redoVerified"] = true;
                    return null;
                }, () => new { undo = _undo.Count, redo = _redo.Count });
                SmokeAssert(scenes[^1].Success, "Stopping persistence checks after redo failure.");
                var importId = _projects[0].Id;
                var originalName = _projects[0].Name;
                var importedName = originalName + "｜导入持久化验证";
                async Task AssertImportedNameAsync(string expected)
                {
                    var disk = await _repository.LoadAsync();
                    SmokeAssert(disk.Count == 10 && disk.Sum(p => p.Tasks.Count) == totalTasks, "Import changed library counts.");
                    SmokeAssert(disk.Single(p => p.Id == importId).Name == expected && _projects.Single(p => p.Id == importId).Name == expected,
                        "The import name did not match memory and the saved repository.");
                    SmokeAssert(disk.Single(p => p.Id == finalProject.Id).Tasks.Single(t => t.Id == finalTask.Id).Title == savedTitle,
                        "Import or its history changed an unrelated project's saved task.");
                }
                await Scene("persistence-import-commit", "persistence", async () =>
                {
                    var imported = await Task.Run(() => PmSerializer.Clone(_projects.Single(p => p.Id == importId)));
                    imported.Name = importedName;
                    await ApplyImportedProjectsAsync([imported]);
                    await AssertImportedNameAsync(importedName);
                    SmokeAssert(_undo.Count == 2 && _redo.Count == 0, "Import did not create one history step.");
                    observations["importCommitVerified"] = true;
                    observations["importProjectId"] = importId;
                    observations["importedProjectName"] = importedName;
                    return null;
                }, () => new { undo = _undo.Count, redo = _redo.Count, importId, importedName });
                SmokeAssert(scenes[^1].Success, "Stopping persistence checks after import failure.");
                await Scene("persistence-import-undo", "persistence", async () =>
                {
                    await UndoAsync();
                    await AssertImportedNameAsync(originalName);
                    SmokeAssert(_undo.Count == 1 && _redo.Count == 1, "Import undo did not transfer its history entry.");
                    observations["importUndoVerified"] = true;
                    return null;
                }, () => new { undo = _undo.Count, redo = _redo.Count });
                SmokeAssert(scenes[^1].Success, "Stopping persistence checks after import undo failure.");
                await Scene("persistence-import-redo", "persistence", async () =>
                {
                    await RedoAsync();
                    await AssertImportedNameAsync(importedName);
                    SmokeAssert(_undo.Count == 2 && _redo.Count == 0, "Import redo did not transfer its history entry.");
                    observations["importRedoVerified"] = true;
                    return null;
                }, () => new { undo = _undo.Count, redo = _redo.Count });
                SmokeAssert(scenes[^1].Success, "Stopping persistence checks after import redo failure.");
                await Scene("persistence-reopen-repository", "persistence", async () =>
                {
                    _repository.Dispose();
                    _repository = new JsonProjectRepository(copy);
                    _projects = await _repository.LoadAsync();
                    await AssertPersistedTitleAsync(savedTitle);
                    await AssertImportedNameAsync(importedName);
                    _undo.Clear(); _redo.Clear();
                    Render();
                    observations["repositoryReopenVerified"] = true;
                    return null;
                }, () => new { savedTaskTitle = _projects.Single(p => p.Id == finalProject.Id).Tasks.Single(t => t.Id == finalTask.Id).Title, totalTasks });
            }
            if (scenes.Count == 0) throw new InvalidOperationException("No scale audit scenes matched PUREPROJECT_UI_SCALE_SCENARIOS.");
        }
        catch (Exception error) { failure = error.ToString(); }
        finally
        {
            try
            {
                _currentActiveDialog?.Hide();
                _reminderTimer?.Stop();
                if (data is not null && beforeFiles is not null)
                {
                    afterFiles = await ScaleFileHashesAsync(data);
                    if (!beforeFiles.OrderBy(p => p.Key).SequenceEqual(afterFiles.OrderBy(p => p.Key)))
                        failure = (failure is null ? "" : failure + Environment.NewLine) + "Fixture files changed during a read-only UI scale audit.";
                }
                Environment.ExitCode = failure is null && scenes.Count > 0 && scenes.All(s => s.Success) ? 0 : 1;
                if (output is not null)
                {
                    await File.WriteAllTextAsync(Path.Combine(output, "scale-ui-result.json"), JsonSerializer.Serialize(new
                    {
                        success = Environment.ExitCode == 0, scope = persistence is null ? "read-only-native-ui-scale-audit" : "native-ui-scale-audit-with-isolated-persistence-copy", startedAt = started, finishedAt = DateTimeOffset.UtcNow,
                        processId = Environment.ProcessId, dataDirectory = data, outputDirectory = output, selectedScenarios = selection,
                        exception = failure, fixture, persistence, beforeFiles, afterFiles,
                        fixtureFilesUnchanged = beforeFiles is not null && afterFiles is not null && beforeFiles.OrderBy(p => p.Key).SequenceEqual(afterFiles.OrderBy(p => p.Key)),
                        successMeaning = "Selected scenes completed and fixture files stayed unchanged. Observation fields describe reachability, limits, and counts; they are evidence for review, not blanket UX acceptance.",
                        measurement = "In-process WinUI actions; render/layout timing includes the shared 100 ms layout settle, one 1-pixel native RenderTargetBitmap render request and a low-priority dispatcher barrier. It does not require continued CompositionTarget frames from an idle window. Diagnostic enumeration and full PNG capture are separate. No forced GC. Memory is point-in-time process/managed allocation, not attribution.",
                        interaction = "Application controls and native ButtonAutomationPeer; no desktop input or user-data writes.", scenes
                    }, options));
                    await Progress(Environment.ExitCode == 0 ? "COMPLETE" : "FAILED");
                }
                else Debug.WriteLine("Scale audit rejected before output initialization: " + failure);
            }
            finally { _ready = false; _repository?.Dispose(); Close(); Application.Current.Exit(); }
        }
    }

    private static (string Data, string Output) ScaleAuditPaths()
    {
        if (Environment.GetEnvironmentVariable("PUREPROJECT_UI_SCALE_TEST") != "1") throw new InvalidOperationException("The scale audit requires explicit opt-in.");
        var suppliedData = Environment.GetEnvironmentVariable("PUREPROJECT_DATA_DIR");
        var suppliedOutput = Environment.GetEnvironmentVariable("PUREPROJECT_UI_SCALE_OUTPUT");
        if (string.IsNullOrWhiteSpace(suppliedData) || string.IsNullOrWhiteSpace(suppliedOutput)
            || !Path.IsPathFullyQualified(suppliedData) || !Path.IsPathFullyQualified(suppliedOutput))
            throw new InvalidOperationException("Set explicit absolute PUREPROJECT_DATA_DIR and PUREPROJECT_UI_SCALE_OUTPUT paths.");
        var data = Path.GetFullPath(suppliedData).TrimEnd(Path.DirectorySeparatorChar);
        var output = Path.GetFullPath(suppliedOutput).TrimEnd(Path.DirectorySeparatorChar);
        var defaultData = Path.GetFullPath(JsonProjectRepository.DefaultDataDirectory).TrimEnd(Path.DirectorySeparatorChar);
        static bool Within(string value, string parent) => value.Equals(parent, StringComparison.OrdinalIgnoreCase)
            || value.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        if (Within(data, defaultData) || Within(output, defaultData) || Within(output, data) || Within(data, output))
            throw new InvalidOperationException("Scale input/output must be separate isolated directories outside the default application data directory.");
        if (!File.Exists(Path.Combine(data, "scale-fixture.json")) || !File.Exists(Path.Combine(data, "projects.json")))
            throw new InvalidOperationException("A completed scale fixture marker and projects.json are required.");
        // Reject junction/symlink aliases which could undermine the lexical isolation check.
        foreach (var path in new[] { data, output })
            for (var folder = new DirectoryInfo(path); folder is not null; folder = folder.Parent)
                if (folder.Exists && folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidOperationException("Scale audit directories must not pass through a reparse point.");
        return (data, output);
    }

    private static void ScaleValidateMarker(JsonElement marker, string data)
    {
        SmokeAssert(marker.GetProperty("schemaVersion").GetInt32() == 1
            && marker.GetProperty("fixtureKind").GetString() == "pureproject-scale-audit"
            && marker.GetProperty("synthetic").GetBoolean()
            && marker.GetProperty("idPrefix").GetString() == "scale-", "The scale fixture identity is invalid.");
        SmokeAssert(string.Equals(Path.GetFullPath(marker.GetProperty("dataDirectory").GetString()!).TrimEnd(Path.DirectorySeparatorChar), data, StringComparison.OrdinalIgnoreCase), "The scale fixture marker belongs to a different directory.");
        var dimensions = marker.GetProperty("dimensions");
        foreach (var (key, count) in new[] { ("projects", 10), ("groupsPerProject", 10), ("statusesPerGroup", 10) })
            SmokeAssert(dimensions.GetProperty(key).GetInt32() == count, "Unexpected fixture dimension: " + key);
        var tasksPerStatus = dimensions.GetProperty("tasksPerStatus").GetInt32();
        SmokeAssert(tasksPerStatus is 20 or 100, "Only the reviewed 20k and 100k profiles are supported.");
        if (marker.TryGetProperty("profile", out var profile))
            SmokeAssert(profile.GetString() == (tasksPerStatus == 20 ? "20k" : "100k"), "Fixture profile and dimensions disagree.");
        foreach (var kind in new[] { "expectedCounts", "verifiedCounts" })
            foreach (var (key, count) in new[] { ("projects", 10), ("taskGroups", 100), ("statuses", 1000), ("tasks", 1000 * tasksPerStatus) })
                SmokeAssert(marker.GetProperty(kind).GetProperty(key).GetInt32() == count, "Unexpected fixture count: " + kind + "." + key);
        SmokeAssert(DateOnly.TryParse(marker.GetProperty("referenceDate").GetString(), out _), "The scale fixture reference date is invalid.");
    }

    private static object ScaleValidateFixture(List<Project> projects, JsonElement marker, Dictionary<string, string> files)
    {
        var tasksPerStatus = marker.GetProperty("dimensions").GetProperty("tasksPerStatus").GetInt32();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        void Id(string id) => SmokeAssert(id.StartsWith("scale-", StringComparison.Ordinal) && ids.Add(id), "Every fixture entity must have a unique scale- ID: " + id);
        SmokeAssert(projects.Count == 10, "The fixture must contain ten projects.");
        var groupCounts = new List<object>();
        foreach (var project in projects)
        {
            Id(project.Id);
            SmokeAssert(!project.Archived && project.TaskGroups.Count == 10 && project.Tasks.Count == 100 * tasksPerStatus, "Each scale project must match its marker dimensions.");
            foreach (var group in project.TaskGroups)
            {
                Id(group.Id);
                SmokeAssert(!group.Archived && group.Statuses.Count == 10, "Each scale task group must contain ten statuses.");
                var tasks = project.Tasks.Where(t => t.TaskGroupId == group.Id).ToArray();
                SmokeAssert(tasks.Length == 10 * tasksPerStatus, "Each scale task group must match its marker dimensions.");
                var states = new List<object>();
                foreach (var status in group.Statuses)
                {
                    Id(status.Id);
                    var count = tasks.Count(t => t.StatusId == status.Id);
                    SmokeAssert(count == tasksPerStatus, "Each scale status must match its marker dimensions.");
                    states.Add(new { statusId = status.Id, tasks = count });
                }
                groupCounts.Add(new { projectId = project.Id, groupId = group.Id, tasks = tasks.Length, statuses = states });
            }
            foreach (var task in project.Tasks)
            {
                Id(task.Id);
                SmokeAssert(string.IsNullOrEmpty(task.TrackedStart) && string.IsNullOrEmpty(task.Reminder) && task.Recurrence is null, "Scale tasks must not start timers, reminders or recurrence.");
                foreach (var subtask in task.Subtasks) Id(subtask.Id);
                foreach (var comment in task.Comments) Id(comment.Id);
            }
            foreach (var tag in project.Tags) Id(tag.Id);
            foreach (var milestone in project.Milestones) Id(milestone.Id);
        }
        return new
        {
            projects = projects.Count, taskGroups = projects.Sum(p => p.TaskGroups.Count), statuses = projects.Sum(p => p.TaskGroups.Sum(g => g.Statuses.Count)),
            tasks = projects.Sum(p => p.Tasks.Count), groupCounts,
            initialProjectsSha256 = marker.GetProperty("initialProjectsSha256").GetString(), actualProjectsSha256 = files["projects.json"],
            initialHashMatches = string.Equals(marker.GetProperty("initialProjectsSha256").GetString(), files["projects.json"], StringComparison.OrdinalIgnoreCase)
        };
    }

    private static async Task<Dictionary<string, string>> ScaleFileHashesAsync(string directory)
    {
        var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(directory).Where(f => !Path.GetFileName(f).Equals("session.lock", StringComparison.OrdinalIgnoreCase)).Order())
        {
            await using var stream = File.OpenRead(file);
            hashes[Path.GetFileName(file)] = Convert.ToHexString(await SHA256.HashDataAsync(stream));
        }
        return hashes;
    }

    private void ScaleSelect(string? projectId, string? groupId, int view, string theme, string? graphStatusId = null)
    {
        _selectedId = projectId; _groupId = groupId; _view = view;
        _graphStatusId = graphStatusId;
        _listStatusFilter = null; _listTagFilter = null; _calendarSelectedDate = null;
        var wasRendering = _rendering; _rendering = true;
        try { SearchBox.Text = ""; PrioritySelector.SelectedIndex = 0; }
        finally { _rendering = wasRendering; }
        _settings = _settings with { Theme = theme };
        ApplyTheme();
        Render();
    }

    private async Task ScaleEnsureViewAsync(string projectId, string? groupId, int view)
    {
        if (_selectedId != projectId || _groupId != groupId || _view != view || Root.ActualTheme != ElementTheme.Dark)
            ScaleSelect(projectId, groupId, view, "Dark");
        await ScaleRenderedLayoutAsync();
    }

    private async Task ScaleRenderedLayoutAsync()
    {
        await SmokeLayoutAsync();
        SmokeAssert(Root.IsLoaded && Root.XamlRoot is not null && Root.ActualWidth > 0 && Root.ActualHeight > 0,
            "The scale audit root has not completed its native layout.");
        // A static, fully rendered WinUI window need not emit a second Rendering
        // event. Request one real render explicitly without encoding a screenshot.
        var rendered = new RenderTargetBitmap();
        async Task<bool> RenderOnceAsync() { await rendered.RenderAsync(Root, 1, 1); return true; }
        await ScaleNativeDeadlineAsync(RenderOnceAsync(), TimeSpan.FromSeconds(10), "Native layout render request");
        SmokeAssert(rendered.PixelWidth > 0 && rendered.PixelHeight > 0, "The native render request produced no pixels.");
        var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => idle.TrySetResult()))
            throw new InvalidOperationException("The scale audit dispatcher is shutting down.");
        if (await Task.WhenAny(idle.Task, Task.Delay(10000)) != idle.Task)
            throw new TimeoutException("The scale audit dispatcher did not reach its layout barrier.");
        await idle.Task;
        Root.UpdateLayout();
    }

    private async Task ScaleResizeAsync(int width, int height)
    {
        if (AppWindow.Size.Width == width && AppWindow.Size.Height == height) return;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
        await SmokeWaitAsync(() => AppWindow.Size.Width == width && AppWindow.Size.Height == height, "The audit window did not resize.");
        await SmokeLayoutAsync();
    }

    private async Task ScaleListTailAsync(ListView list)
    {
        SmokeAssert(list.Items.Count > 0, "The scale task list is empty.");
        list.ScrollIntoView(list.Items[^1], ScrollIntoViewAlignment.Leading);
        await SmokeWaitAsync(() => list.ContainerFromIndex(list.Items.Count - 1) is ListViewItem { ActualHeight: > 0 }, "The final task did not acquire a rendered container.");
        await ScaleRenderedLayoutAsync();
        var final = (ListViewItem)list.ContainerFromIndex(list.Items.Count - 1);
        final.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
        await ScaleRenderedLayoutAsync();
        SmokeAssert(ScaleIntersects(final, list), "The final task container is outside the list viewport after scrolling.");
        ScaleAssertListVirtualized(list);
    }

    private static void ScaleAssertListVirtualized(ListView list)
    {
        if (list.Items.Count < 200) return;
        var contents = list.Items.OfType<TaskRow>().Count(row => row.Content is not null);
        var containers = Enumerable.Range(0, list.Items.Count).Count(index => list.ContainerFromIndex(index) is ListViewItem);
        SmokeAssert(contents < 200 && containers < 200,
            $"Large task list lost bounded realization: {list.Items.Count} items, {contents} row contents, {containers} realized containers (expected fewer than 200 each).");
    }

    private static async Task<T> ScaleNativeDeadlineAsync<T>(Task<T> operation, TimeSpan timeout, string label)
    {
        if (await Task.WhenAny(operation, Task.Delay(timeout)) != operation)
            throw new ScaleNativeTimeoutException($"{label} exceeded {timeout.TotalSeconds:0} seconds.");
        return await operation;
    }

    private sealed class ScaleNativeTimeoutException(string message) : TimeoutException(message) { }

    private async Task ScaleDismissDialogAsync(Task? showing)
    {
        if (_currentActiveDialog is { } dialog)
        {
            dialog.Hide();
            if (showing is not null) await SmokeAwaitDialogAsync(showing, dialog);
            else await SmokeWaitAsync(() => !_dialogOpen, "The scale audit dialog did not close.");
        }
        else if (showing is not null) await showing;
    }

    private static object ScaleMemory()
    {
        using var process = Process.GetCurrentProcess(); process.Refresh();
        return new { managedBytes = GC.GetTotalMemory(false), totalAllocatedBytes = GC.GetTotalAllocatedBytes(false), workingSetBytes = process.WorkingSet64,
            privateBytes = process.PrivateMemorySize64, peakWorkingSetBytes = process.PeakWorkingSet64, handles = process.HandleCount };
    }

    private static int ScaleMenuItemCount(IEnumerable<MenuFlyoutItemBase> items) =>
        items.Sum(item => 1 + (item is MenuFlyoutSubItem submenu ? ScaleMenuItemCount(submenu.Items) : 0));

    private object ScaleSurface(FrameworkElement surface)
    {
        var lists = SmokeDescendants<ListView>(surface).ToArray();
        return new
        {
            windowWidth = AppWindow.Size.Width, windowHeight = AppWindow.Size.Height, clientWidthDip = Root.ActualWidth, clientHeightDip = Root.ActualHeight,
            rasterizationScale = Root.XamlRoot.RasterizationScale, theme = Root.ActualTheme.ToString(), selectedProjectId = _selectedId, selectedGroupId = _groupId, view = _view,
            visualElements = SmokeDescendants<FrameworkElement>(surface).Count(),
            listViews = lists.Select((list, index) =>
            {
                var realized = Enumerable.Range(0, list.Items.Count).Select(i => (Index: i, Container: list.ContainerFromIndex(i) as ListViewItem)).Where(x => x.Container is not null).ToArray();
                var scroll = SmokeDescendants<ScrollViewer>(list).FirstOrDefault();
                return new { index, automationId = AutomationProperties.GetAutomationId(list), items = list.Items.Count,
                    taskRowContentNonNull = list.Items.OfType<TaskRow>().Count(row => row.Content is not null),
                    realizedContainers = realized.Length,
                    visibleContainers = realized.Count(x => ScaleIntersects(x.Container!, list)),
                    firstRealizedIndex = realized.FirstOrDefault().Container is null ? (int?)null : realized[0].Index,
                    lastRealizedIndex = realized.LastOrDefault().Container is null ? (int?)null : realized[^1].Index,
                    viewportWidth = scroll?.ViewportWidth, viewportHeight = scroll?.ViewportHeight,
                    scrollableHeight = scroll?.ScrollableHeight, verticalOffset = scroll?.VerticalOffset };
            }).ToArray(),
            taskOpenElements = SmokeDescendants<FrameworkElement>(surface).Count(e => AutomationProperties.GetAutomationId(e).StartsWith("TaskOpen:", StringComparison.Ordinal)),
            statusColumns = SmokeDescendants<FrameworkElement>(surface).Count(e => AutomationProperties.GetAutomationId(e).StartsWith("TaskStatusOutline:", StringComparison.Ordinal))
        };
    }

    private object ScaleTaskListTail(string? statusId = null)
    {
        var root = statusId is null ? ContentHost : SmokeFind<FrameworkElement>(ContentHost, e => AutomationProperties.GetAutomationId(e) == "TaskStatusOutline:" + statusId);
        var list = SmokeFind<ListView>(root, l => l.Items.Count > 0);
        var container = list.ContainerFromIndex(list.Items.Count - 1) as ListViewItem;
        return new { totalItems = list.Items.Count, finalTaskId = (list.Items[^1] as TaskRow)?.Id,
            finalContainerRealized = container is not null, finalContainerIntersectsViewport = container is not null && ScaleIntersects(container, list),
            finalContainerPosition = container is null ? null : ScaleElementPosition(container, list) };
    }

    private object ScaleHorizontalScroll()
    {
        var scroll = SmokeDescendants<ScrollViewer>(ContentHost).OrderByDescending(s => s.ScrollableWidth).First();
        return new { scroll.HorizontalOffset, scroll.ScrollableWidth, scroll.ViewportWidth, reachedRightEdge = Math.Abs(scroll.HorizontalOffset - scroll.ScrollableWidth) < 2 };
    }

    private object ScaleEditorObservation(Project project, ProjectTask task)
    {
        var dialog = _currentActiveDialog ?? throw new InvalidOperationException("The task editor is not open.");
        var picker = SmokeDescendants<ComboBox>(dialog).FirstOrDefault(c => c.PlaceholderText == "选择前置任务…");
        return new { taskId = task.Id, projectTaskCount = project.Tasks.Count, potentialDependencyCandidates = project.Tasks.Count - 1,
            existingDependencies = task.Dependencies.Count, dependencyPickerItems = picker?.Items.Count,
            statusButtons = SmokeDescendants<Button>(dialog).Count(b => AutomationProperties.GetAutomationId(b).StartsWith("TaskEditorStatus_", StringComparison.Ordinal)),
            visualTreeCheckBoxes = SmokeDescendants<CheckBox>(dialog).Count(), visualTreeNumberBoxes = SmokeDescendants<NumberBox>(dialog).Count(),
            note = "Visual-tree counts exclude detached controls; potential candidates are data counts, not a claim about instantiated controls." };
    }

    private object ScaleSearchObservation(string query, string? expectedTaskId)
    {
        var dialog = _currentActiveDialog ?? throw new InvalidOperationException("The search dialog is not open.");
        var resultIds = SmokeDescendants<Button>(dialog).Select(b => AutomationProperties.GetAutomationId(b)).Where(id => id.StartsWith("GlobalResult:", StringComparison.Ordinal)).ToArray();
        return new { query, searchSourceTasks = _projects.Sum(p => p.Tasks.Count), actualMatchingDataItems = SearchAll(query).Count(),
            renderedResults = resultIds.Length, expectedTaskId, expectedTaskFound = expectedTaskId is null ? (bool?)null : resultIds.Contains("GlobalResult:任务:" + expectedTaskId),
            resultIds, status = SmokeFind<TextBlock>(dialog, t => AutomationProperties.GetAutomationId(t) == "GlobalSearchStatus").Text };
    }

    private static bool ScaleIntersects(FrameworkElement element, FrameworkElement viewport)
    {
        if (element.Visibility != Visibility.Visible || element.ActualWidth <= 0 || element.ActualHeight <= 0) return false;
        var rect = SmokeBounds(element, viewport);
        return rect.Right > 0 && rect.Bottom > 0 && rect.Left < viewport.ActualWidth && rect.Top < viewport.ActualHeight;
    }

    private static object ScaleElementPosition(FrameworkElement element, FrameworkElement viewport)
    {
        var rect = SmokeBounds(element, viewport);
        return new { rect.X, rect.Y, rect.Width, rect.Height, intersectsViewport = ScaleIntersects(element, viewport),
            fullyWithinViewport = rect.Left >= -1 && rect.Top >= -1 && rect.Right <= viewport.ActualWidth + 1 && rect.Bottom <= viewport.ActualHeight + 1 };
    }

    private sealed class ScaleSceneResult
    {
        public string Id { get; init; } = "";
        public string Group { get; init; } = "";
        public DateTimeOffset StartedAt { get; init; }
        public DateTimeOffset FinishedAt { get; set; }
        public bool Success { get; set; }
        public double RenderAndLayoutElapsedMilliseconds { get; set; }
        public double CaptureElapsedMilliseconds { get; set; }
        public object? MemoryBefore { get; init; }
        public object? MemoryAfterRender { get; set; }
        public object? MemoryAfterCapture { get; set; }
        public object? Surface { get; set; }
        public object? Observation { get; set; }
        public string? Exception { get; set; }
        public string? DiagnosticError { get; set; }
        public string? CaptureException { get; set; }
        public List<UiSmokeScreenshot> Screenshots { get; } = [];
    }
}
