using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PureProject.Core;

namespace PureProject.WinUI;

public sealed partial class MainWindow
{
    private MenuFlyout ViewMenu()
    {
        return new MenuFlyout
        {
            MenuFlyoutPresenterStyle = (Style)Application.Current.Resources["PaperMenuFlyoutPresenterStyle"]
        };
    }

    private MenuFlyoutItem ViewMenuItem(string text, Func<Task> action, string? id = null, bool enabled = true)
    {
        var item = new MenuFlyoutItem { Text = text, FontFamily = UiFont, FontSize = 12, IsEnabled = enabled };
        item.Style = (Style)Application.Current.Resources["PaperMenuFlyoutItemStyle"];
        ApplyMenuPalette(item);
        if (id is not null) ViewIdentity(item, id, text);
        item.Click += async (_, _) => await RunMenuActionAsync(item, action);
        return item;
    }

    private MenuFlyoutSubItem ViewSubMenu(string text, string id)
    {
        var submenu = new MenuFlyoutSubItem { Text = text, FontFamily = UiFont, FontSize = 12 };
        submenu.Style = (Style)Application.Current.Resources["PaperMenuFlyoutSubItemStyle"];
        ApplyMenuPalette(submenu);
        ViewIdentity(submenu, id, text); return submenu;
    }

    private void ApplyMenuPalette(Control item, string textBrush = "PrimaryTextBrush")
    {
        // Keep the SDK's native parts and keyboard states. Each submenu item
        // receives the same local palette before its template is loaded.
        var transparent = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        foreach (var prefix in new[] { "MenuFlyoutItem", "MenuFlyoutSubItem" })
        {
            foreach (var state in new[] { "", "PointerOver", "Pressed", "Disabled", "SubMenuOpened" })
            {
                item.Resources[prefix + "Background" + state] = state switch
                {
                    "PointerOver" => ThemeBrush("SubtleBackgroundBrush"),
                    "Pressed" or "SubMenuOpened" => ThemeBrush("AccentLightBrush"),
                    _ => transparent
                };
                item.Resources[prefix + "Foreground" + state] = ThemeBrush(state == "Disabled" ? "MutedTextBrush" : textBrush);
            }
            item.Resources[prefix + "BackgroundBrush"] = transparent;
        }
        foreach (var state in new[] { "", "PointerOver", "Pressed", "Disabled", "SubMenuOpened" })
        {
            item.Resources["MenuFlyoutSubItemChevron" + state] = ThemeBrush(state == "Disabled" ? "MutedTextBrush" : "SecondaryTextBrush");
            item.Resources["MenuFlyoutItemKeyboardAcceleratorTextForeground" + state] = ThemeBrush("MutedTextBrush");
        }
        item.Resources["MenuFlyoutPresenterBackground"] = ThemeBrush("CardBackgroundBrush");
        item.Resources["MenuFlyoutPresenterBorderBrush"] = ThemeBrush("BorderStrongBrush");
    }

    private Button ViewTaskMore(Project project, ProjectTask task, IReadOnlyList<string>? visibleTaskIds = null)
    {
        var button = ViewIcon("\uE712", "更多任务操作：" + task.Title);
        ViewIdentity(button, "TaskMore:" + task.Id, "更多任务操作：" + task.Title, "打开任务、状态、复制、移动、优先级和删除操作");
        // Keep native Button.Flyout behavior, but allocate commands only while open.
        var menu = ViewMenu();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            PopulateTaskMenu(menu, project, task.Id, visibleTaskIds);
        };
        CaptureMenuOrigin(menu, "TaskMore:" + task.Id);
        menu.Closed += (_, _) => menu.Items.Clear();
        button.Flyout = menu;
        return button;
    }

    // All entry points share the same actions, including the visible button, right-click and Shift+F10.
    private MenuFlyout BuildTaskMenu(Project project, string taskId, IReadOnlyList<string>? visibleTaskIds = null)
    {
        var menu = ViewMenu();
        PopulateTaskMenu(menu, project, taskId, visibleTaskIds);
        CaptureMenuOrigin(menu, "TaskMore:" + taskId);
        return menu;
    }

    private void PopulateTaskMenu(MenuFlyout menu, Project project, string taskId, IReadOnlyList<string>? visibleTaskIds)
    {
        project = _projects.FirstOrDefault(p => p.Id == project.Id) ?? project;
        var task = project.Tasks.Single(t => t.Id == taskId);
        var group = project.TaskGroups.Single(g => g.Id == task.TaskGroupId);
        string Id(string action) => $"TaskMenu:{action}:{task.Id}";
        AddMenu(menu, "打开任务", () => EditTaskAsync(task.Id, project.Id), Id("open"));
        AddMenu(menu, "重命名", () => RenameTaskAsync(project.Id, task.Id), Id("rename"));
        AddMenu(menu, IsTaskClosed(project, task) ? "重新打开任务" : "完成任务", () => ChangeAsync(project.Id, draft => _service.ToggleTaskStatus(draft, task.Id)), Id("toggle"));

        var statuses = ViewSubMenu("更改状态", Id("status"));
        foreach (var status in group.Statuses.OrderBy(s => s.SortOrder))
            statuses.Items.Add(ViewMenuItem(status.Name, () => ChangeAsync(project.Id, draft => _service.ChangeTaskStatus(draft, task.Id, status.Id)), Id("status:" + status.Id), task.StatusId != status.Id));
        menu.Items.Add(statuses);
        menu.Items.Add(new MenuFlyoutSeparator());
        AddMenu(menu, "复制任务", () => DuplicateTaskAsync(project.Id, task.Id), Id("copy"), !group.Archived);

        var move = ViewSubMenu("移动到任务组", Id("move"));
        foreach (var targetGroup in project.TaskGroups.Where(g => !g.Archived && g.Id != group.Id).OrderBy(g => g.SortOrder))
        {
            var targetMenu = ViewSubMenu(targetGroup.Name, Id("move:" + targetGroup.Id));
            foreach (var targetStatus in targetGroup.Statuses.OrderBy(s => s.SortOrder))
                targetMenu.Items.Add(ViewMenuItem(targetStatus.Name, () => ChangeAsync(project.Id, draft =>
                {
                    var edited = PmSerializer.CloneTask(draft.Tasks.Single(t => t.Id == task.Id));
                    edited.TaskGroupId = targetGroup.Id; edited.StatusId = targetStatus.Id;
                    _service.SaveTask(draft, edited, false);
                }, "任务已移动"), Id($"move:{targetGroup.Id}:{targetStatus.Id}")));
            move.Items.Add(targetMenu);
        }
        move.IsEnabled = move.Items.Count > 0; menu.Items.Add(move);

        var priorities = ViewSubMenu("设置优先级", Id("priority"));
        foreach (var priority in new[] { "high", "medium", "low" })
            priorities.Items.Add(ViewMenuItem(PriorityText(priority), () => ChangeAsync(project.Id, draft =>
            {
                var edited = PmSerializer.CloneTask(draft.Tasks.Single(t => t.Id == task.Id));
                edited.Priority = priority; _service.UpdateTask(draft, edited);
            }), Id("priority:" + priority), task.Priority != priority));
        menu.Items.Add(priorities);

        var peers = TaskReorderIds(project, task, visibleTaskIds);
        var index = peers.IndexOf(task.Id);
        menu.Items.Add(new MenuFlyoutSeparator());
        AddMenu(menu, "向上移动", () => MoveTaskInViewAsync(project.Id, task.Id, -1, visibleTaskIds), Id("up"), index > 0);
        AddMenu(menu, "向下移动", () => MoveTaskInViewAsync(project.Id, task.Id, 1, visibleTaskIds), Id("down"), index >= 0 && index < peers.Count - 1);
        menu.Items.Add(new MenuFlyoutSeparator());
        var delete = AddMenu(menu, "删除任务", () => DeleteTaskWithConfirmationAsync(project.Id, task.Id), Id("delete"));
        delete.Foreground = ThemeBrush("DangerTextBrush");
        ApplyMenuPalette(delete, "DangerTextBrush");
    }

    private async Task RenameTaskAsync(string projectId, string taskId)
    {
        var task = _projects.Single(p => p.Id == projectId).Tasks.Single(t => t.Id == taskId);
        var title = await PromptAsync("重命名任务", task.Title, value => TextRules.RequireTaskTitle(value, task.Title), 0);
        if (title is null) return;
        await ChangeAsync(projectId, draft =>
        {
            var edited = PmSerializer.CloneTask(draft.Tasks.Single(t => t.Id == taskId));
            edited.Title = title; _service.UpdateTask(draft, edited);
        }, "任务已重命名");
    }

    private Task DuplicateTaskAsync(string projectId, string taskId) => ChangeAsync(projectId, draft =>
    {
        var source = draft.Tasks.Single(t => t.Id == taskId);
        if (draft.TaskGroups.Single(g => g.Id == source.TaskGroupId).Archived) throw new InvalidOperationException("请先恢复任务组，再复制任务。");
        var copy = PmSerializer.CloneTask(source);
        copy.Id = ProjectService.NewId();
        if (copy.Title.Length <= TextRules.TaskTitleUtf16Limit - 4
            && TextRules.CountGraphemes(copy.Title) <= TextRules.TaskTitleGraphemeLimit - 4) copy.Title += "（副本）";
        copy.CreatedAt = copy.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");
        copy.TrackedStart = null;
        foreach (var subtask in copy.Subtasks) subtask.Id = ProjectService.NewId();
        foreach (var comment in copy.Comments) comment.Id = ProjectService.NewId();
        // Copying a completed task preserves its state; it must not complete it again or consume recurrence.
        draft.Tasks.Insert(draft.Tasks.IndexOf(source) + 1, copy);
        PmSerializer.EnsureValid(draft);
    }, "已复制任务");

    private static List<string> TaskReorderIds(Project project, ProjectTask task, IReadOnlyList<string>? visibleTaskIds)
    {
        if (visibleTaskIds is null)
            return project.Tasks.Where(t => t.TaskGroupId == task.TaskGroupId && t.StatusId == task.StatusId).Select(t => t.Id).ToList();
        var existing = project.Tasks.Select(t => t.Id).ToHashSet();
        return visibleTaskIds.Where(existing.Contains).ToList();
    }

    private Task MoveTaskInViewAsync(string projectId, string taskId, int offset, IReadOnlyList<string>? visibleTaskIds) => ChangeAsync(projectId, draft =>
    {
        var task = draft.Tasks.Single(t => t.Id == taskId);
        var peers = TaskReorderIds(draft, task, visibleTaskIds);
        var index = peers.IndexOf(task.Id); var target = index + offset;
        if (index < 0 || target < 0 || target >= peers.Count) return;
        _service.ReorderTask(draft, task.Id, draft.Tasks.FindIndex(t => t.Id == peers[target]));
    }, "任务顺序已保存");

    private async Task DeleteTaskWithConfirmationAsync(string projectId, string taskId)
    {
        var task = _projects.Single(p => p.Id == projectId).Tasks.Single(t => t.Id == taskId);
        if (await ConfirmAsync("删除任务", $"删除「{task.Title}」？相关依赖会同步清理，可以使用撤销恢复。"))
            await ChangeAsync(projectId, draft => _service.DeleteTask(draft, taskId), "任务已删除");
    }
}
