using System.Globalization;

namespace PureProject.Core;

/// <summary>Domain mutations. Persist a project only after a successful operation.</summary>
public sealed class ProjectService(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private string Now => _clock.GetUtcNow().ToString("O");
    private DateOnly Today => DateOnly.FromDateTime(_clock.GetLocalNow().DateTime);
    public static string NewId() => Guid.NewGuid().ToString();

    public Project CreateProject(string name, string description = "", string color = "#4f46e5")
    {
        var project = new Project { Id = NewId(), Name = TextRules.RequireProjectName(name), Description = description, Color = color, CreatedAt = Now, UpdatedAt = Now };
        var group = PmSerializer.CreateLegacyGroup(project.Id);
        project.TaskGroups.Add(group); project.DefaultTaskGroupId = group.Id;
        return project;
    }

    public ProjectTask CreateTask(Project project, string title, string? groupId = null, string? statusId = null)
    {
        var group = Group(project, groupId ?? project.DefaultTaskGroupId);
        if (group.Archived) throw new InvalidOperationException("无法向已归档任务组添加任务");
        var status = Status(group, statusId ?? group.InitialStatusId);
        var task = new ProjectTask
        {
            Id = NewId(), Title = TextRules.RequireTaskTitle(title), TaskGroupId = group.Id, StatusId = status.Id,
            CompletedAt = status.Category == "done" ? Now : null, Color = project.Color, CreatedAt = Now, UpdatedAt = Now
        };
        project.Tasks.Add(task); Touch(project); return task;
    }

    public void UpdateTask(Project project, ProjectTask replacement)
    {
        var existing = Task(project, replacement.Id);
        if (existing.TaskGroupId != replacement.TaskGroupId) throw new InvalidOperationException("跨组请使用 MoveTask");
        SaveTask(project, replacement, false);
    }

    /// <summary>
    /// Saves an editor draft atomically, including cross-group changes. Dependency checks use
    /// the final draft, and only a newly completed task consumes its recurrence rule.
    /// </summary>
    public ProjectTask? SaveTask(Project project, ProjectTask replacement, bool isNew)
    {
        var existing = project.Tasks.FirstOrDefault(t => t.Id == replacement.Id);
        if (isNew && existing is not null) throw new InvalidOperationException("任务 ID 已存在");
        if (!isNew && existing is null) throw new InvalidOperationException("任务不存在");
        var targetGroup = Group(project, replacement.TaskGroupId);
        if (targetGroup.Archived && (isNew || existing!.TaskGroupId != targetGroup.Id)) throw new InvalidOperationException("无法向已归档任务组添加任务");
        var targetStatus = Status(targetGroup, replacement.StatusId);
        var previousStatus = existing is null ? null : GetTaskStatus(project, existing);
        var entersDone = targetStatus.Category == "done" && (isNew || previousStatus?.Category != "done");
        var dependenciesChanged = existing is not null && !existing.Dependencies.Select(d => d.TaskId).ToHashSet().SetEquals(replacement.Dependencies.Select(d => d.TaskId));
        var clone = project.CopyForTaskEdit();
        var edited = PmSerializer.CloneTask(replacement);
        edited.Title = TextRules.RequireTaskTitle(edited.Title, existing?.Title);
        edited.CompletedAt = targetStatus.Category == "done" ? (previousStatus?.Category == "done" ? existing!.CompletedAt ?? Now : Now) : null;
        edited.StatusBeforeClosedId = existing?.TaskGroupId == targetGroup.Id ? existing.StatusBeforeClosedId : null;
        if (existing?.TaskGroupId == targetGroup.Id && previousStatus?.Category is not "done" and not "cancelled" && targetStatus.Category is "done" or "cancelled") edited.StatusBeforeClosedId = existing.StatusId;
        edited.CreatedAt = existing?.CreatedAt ?? (string.IsNullOrWhiteSpace(edited.CreatedAt) ? Now : edited.CreatedAt);
        edited.UpdatedAt = Now;
        if (isNew) clone.Tasks.Add(edited);
        else clone.Tasks[clone.Tasks.FindIndex(t => t.Id == edited.Id)] = edited;
        // Validate first so malformed/cyclic drafts cannot generate another instance or alter live data.
        PmSerializer.EnsureValid(clone);
        if (targetStatus.Category == "done" && (entersDone || dependenciesChanged)) RequireDependencies(clone, edited);
        var generated = entersDone ? GenerateRecurringSuccessor(edited, Group(clone, edited.TaskGroupId)) : null;
        if (generated is not null) clone.Tasks.Add(generated);
        PmSerializer.EnsureValid(clone);
        project.Tasks = clone.Tasks;
        Touch(project);
        return generated;
    }

    public ProjectTask? ChangeTaskStatus(Project project, string taskId, string statusId)
    {
        var task = Task(project, taskId);
        var group = Group(project, task.TaskGroupId);
        var old = Status(group, task.StatusId);
        var next = Status(group, statusId);
        if (old.Id == next.Id) return null;
        if (next.Category == "done" && old.Category != "done") RequireDependencies(project, task);
        var generated = next.Category == "done" && old.Category != "done" ? GenerateRecurringSuccessor(task, group) : null;
        if (next.Category is "done" or "cancelled" && old.Category is not "done" and not "cancelled") task.StatusBeforeClosedId = task.StatusId;
        task.StatusId = next.Id;
        task.CompletedAt = next.Category == "done" ? task.CompletedAt ?? Now : null;
        task.UpdatedAt = Now;
        if (generated is not null) project.Tasks.Add(generated);
        Touch(project); return generated;
    }

    private ProjectTask? GenerateRecurringSuccessor(ProjectTask task, TaskGroup group)
    {
        if (task.Recurrence is not { } rule) return null;
        ProjectTask? generated = null;
        var date = RecurrenceCalculator.NextOccurrence(rule, task.DueDate, Today);
        if (date is not null)
        {
            generated = PmSerializer.CloneTask(task);
            generated.Id = NewId(); generated.StatusId = group.InitialStatusId;
            generated.CompletedAt = Status(group, generated.StatusId).Category == "done" ? Now : null;
            generated.StatusBeforeClosedId = null; generated.DueDate = date;
            generated.Recurrence = RecurrenceCalculator.Consume(rule); generated.Recurrence.SourceTaskId = task.Id;
            foreach (var subtask in generated.Subtasks) { subtask.Id = NewId(); subtask.Done = false; }
            generated.Comments = []; generated.TrackedStart = null; generated.Reminder = null;
            generated.CreatedAt = Now; generated.UpdatedAt = Now;
        }
        task.Recurrence = null;
        return generated;
    }

    public ProjectTask? CompleteTask(Project project, string taskId)
    {
        var task = Task(project, taskId);
        return ChangeTaskStatus(project, taskId, Group(project, task.TaskGroupId).CompletionStatusId);
    }
    public void ReopenTask(Project project, string taskId)
    {
        var task = Task(project, taskId); var group = Group(project, task.TaskGroupId);
        var target = group.Statuses.FirstOrDefault(s => s.Id == task.StatusBeforeClosedId && s.Category is not "done" and not "cancelled")
            ?? group.Statuses.FirstOrDefault(s => s.Id == group.InitialStatusId && s.Category is not "done" and not "cancelled")
            ?? group.Statuses.FirstOrDefault(s => s.Category is not "done" and not "cancelled")
            ?? throw new InvalidOperationException("任务组缺少可重新打开的状态");
        ChangeTaskStatus(project, taskId, target.Id);
    }
    public void ToggleTaskStatus(Project project, string taskId)
    { if (IsClosed(project, Task(project, taskId))) ReopenTask(project, taskId); else CompleteTask(project, taskId); }
    public void CancelTask(Project project, string taskId)
    {
        var task = Task(project, taskId); var group = Group(project, task.TaskGroupId);
        var status = group.Statuses.FirstOrDefault(s => s.Category == "cancelled") ?? throw new InvalidOperationException("请先为任务组添加取消状态");
        ChangeTaskStatus(project, taskId, status.Id);
    }

    public void MoveTask(Project project, string taskId, string targetGroupId, string? targetStatusId = null)
    {
        var task = Task(project, taskId); var group = Group(project, targetGroupId);
        if (group.Archived) throw new InvalidOperationException("目标任务组已归档");
        var target = Status(group, targetStatusId ?? group.InitialStatusId);
        if (target.Category == "done") RequireDependencies(project, task);
        task.TaskGroupId = group.Id; task.StatusId = target.Id; task.StatusBeforeClosedId = null;
        task.CompletedAt = target.Category == "done" ? Now : null; task.UpdatedAt = Now;
        Touch(project);
    }

    public void DeleteTask(Project project, string taskId)
    {
        Task(project, taskId);
        project.Tasks.RemoveAll(t => t.Id == taskId);
        foreach (var task in project.Tasks)
        {
            if (task.Dependencies.RemoveAll(d => d.TaskId == taskId) > 0) task.UpdatedAt = Now;
        }
        Touch(project);
    }

    public void ReorderTask(Project project, string taskId, int newIndex)
    {
        var task = Task(project, taskId);
        project.Tasks.Remove(task); project.Tasks.Insert(Math.Clamp(newIndex, 0, project.Tasks.Count), task); Touch(project);
    }

    public IReadOnlyList<ProjectTask> DuplicateTasksInStatus(Project project, string groupId, string statusId)
    {
        var status = Status(Group(project, groupId), statusId);
        var originals = project.Tasks.Where(t => t.TaskGroupId == groupId && t.StatusId == statusId).ToArray();
        var mapping = originals.ToDictionary(t => t.Id, _ => NewId());
        var copies = originals.Select(PmSerializer.CloneTask).ToArray();
        foreach (var task in copies)
        {
            task.Id = mapping[task.Id]; task.CreatedAt = Now; task.UpdatedAt = Now;
            task.TrackedStart = null; task.CompletedAt = status.Category == "done" ? Now : null;
            foreach (var dependency in task.Dependencies) dependency.TaskId = mapping.GetValueOrDefault(dependency.TaskId, dependency.TaskId);
            foreach (var subtask in task.Subtasks) subtask.Id = NewId();
            foreach (var comment in task.Comments) comment.Id = NewId();
            if (task.Recurrence?.SourceTaskId is { } source) task.Recurrence.SourceTaskId = mapping.GetValueOrDefault(source, source);
        }
        project.Tasks.AddRange(copies); if (copies.Length > 0) Touch(project); return copies;
    }

    public IReadOnlyList<ProjectTask> CopyStatusTasks(Project project, string groupId, string statusId) => DuplicateTasksInStatus(project, groupId, statusId);

    public TaskGroup CreateTaskGroup(Project project, string name)
    {
        var group = PmSerializer.CreateLegacyGroup(NewId());
        group.Name = Name(name); group.SortOrder = project.TaskGroups.Select(g => g.SortOrder).DefaultIfEmpty().Max() + 1024;
        project.TaskGroups.Add(group); Touch(project); return group;
    }
    public void RenameTaskGroup(Project project, string groupId, string name) { Group(project, groupId).Name = Name(name); Touch(project); }
    public void SetDefaultTaskGroup(Project project, string groupId)
    {
        if (Group(project, groupId).Archived) throw new InvalidOperationException("默认任务组不能已归档");
        project.DefaultTaskGroupId = groupId; Touch(project);
    }
    public void ArchiveTaskGroup(Project project, string groupId, string? replacementDefaultId = null)
    {
        var group = Group(project, groupId);
        if (group.Archived) return;
        var alternatives = project.TaskGroups.Where(g => g.Id != groupId && !g.Archived).ToArray();
        if (alternatives.Length == 0) throw new InvalidOperationException("项目至少保留一个未归档任务组");
        var replacement = replacementDefaultId is null ? alternatives[0] : alternatives.FirstOrDefault(g => g.Id == replacementDefaultId) ?? throw new InvalidOperationException("替代默认组不存在或已归档");
        if (project.DefaultTaskGroupId == groupId) project.DefaultTaskGroupId = replacement.Id;
        group.Archived = true; Touch(project);
    }
    public void RestoreTaskGroup(Project project, string groupId) { Group(project, groupId).Archived = false; Touch(project); }
    public void DeleteTaskGroup(Project project, string groupId)
    {
        var group = Group(project, groupId);
        if (project.Tasks.Any(t => t.TaskGroupId == groupId)) throw new InvalidOperationException("请先迁移或删除组内任务");
        var fallback = project.TaskGroups.FirstOrDefault(g => !g.Archived && g.Id != groupId) ?? throw new InvalidOperationException("项目至少保留一个未归档任务组");
        project.TaskGroups.Remove(group);
        if (project.DefaultTaskGroupId == groupId) project.DefaultTaskGroupId = fallback.Id;
        Touch(project);
    }
    public void ReorderTaskGroups(Project project, IReadOnlyList<string> orderedIds)
    {
        CheckOrder(project.TaskGroups.Select(g => g.Id), orderedIds);
        for (var i = 0; i < orderedIds.Count; i++) Group(project, orderedIds[i]).SortOrder = (i + 1) * 1024;
        Touch(project);
    }

    public TaskStatusDefinition CreateTaskStatus(Project project, string groupId, string name, string category = "active")
    {
        CheckCategory(category); var group = Group(project, groupId);
        var status = new TaskStatusDefinition { Id = NewId(), Name = Name(name), Category = category, Color = PmSerializer.CategoryColor(category), SortOrder = group.Statuses.Select(s => s.SortOrder).DefaultIfEmpty().Max() + 1024 };
        group.Statuses.Add(status); Touch(project); return status;
    }
    public void UpdateTaskStatusDefinition(Project project, string groupId, string statusId, string name, string color, string category)
    {
        CheckCategory(category); name = Name(name);
        var group = Group(project, groupId); var status = Status(group, statusId);
        if (group.CompletionStatusId == statusId && category != "done") throw new InvalidOperationException("完成列必须保留 done 类别");
        status.Name = name; status.Color = color;
        if (status.Category != category)
        {
            status.Category = category;
            foreach (var task in project.Tasks.Where(t => t.TaskGroupId == groupId && t.StatusId == statusId)) { task.CompletedAt = category == "done" ? task.CompletedAt ?? Now : null; task.UpdatedAt = Now; }
        }
        Touch(project);
    }
    public void SetGroupInitialStatus(Project project, string groupId, string statusId)
    { var group = Group(project, groupId); Status(group, statusId); group.InitialStatusId = statusId; Touch(project); }
    public void SetGroupCompletionStatus(Project project, string groupId, string statusId)
    {
        var group = Group(project, groupId); Status(group, statusId);
        var ordered = group.Statuses.OrderBy(s => s.SortOrder).Where(s => s.Id != statusId).Select(s => s.Id).Append(statusId).ToArray();
        if (group.InitialStatusId == statusId) group.InitialStatusId = ordered.FirstOrDefault(id => id != statusId) ?? statusId;
        for (var i = 0; i < ordered.Length; i++)
        {
            var status = Status(group, ordered[i]);
            var category = status.Id == statusId ? "done" : status.Category == "done" ? "active" : status.Category;
            if (category != status.Category)
                foreach (var task in project.Tasks.Where(t => t.TaskGroupId == groupId && t.StatusId == status.Id)) { task.CompletedAt = category == "done" ? task.CompletedAt ?? Now : null; task.UpdatedAt = Now; }
            status.Category = category; status.SortOrder = (i + 1) * 1024;
        }
        group.CompletionStatusId = statusId; Touch(project);
    }
    public void ReorderTaskStatuses(Project project, string groupId, IReadOnlyList<string> orderedIds)
    {
        var group = Group(project, groupId); CheckOrder(group.Statuses.Select(s => s.Id), orderedIds);
        for (var i = 0; i < orderedIds.Count; i++) Status(group, orderedIds[i]).SortOrder = (i + 1) * 1024;
        Touch(project);
    }
    public void DeleteTaskStatus(Project project, string groupId, string statusId, string? migrateToStatusId = null)
    {
        var group = Group(project, groupId); var status = Status(group, statusId);
        if (statusId == group.InitialStatusId || statusId == group.CompletionStatusId) throw new InvalidOperationException("初始列和完成列不能直接删除");
        var tasks = project.Tasks.Where(t => t.TaskGroupId == groupId && t.StatusId == statusId).ToArray();
        TaskStatusDefinition? replacement = null;
        if (migrateToStatusId is not null) replacement = Status(group, migrateToStatusId);
        if (replacement?.Id == statusId || tasks.Length > 0 && replacement is null) throw new InvalidOperationException("请选择其它状态承接任务");
        if (replacement?.Category == "done") foreach (var task in tasks) RequireDependencies(project, task);
        foreach (var task in tasks)
        { task.StatusId = replacement!.Id; task.CompletedAt = replacement.Category == "done" ? Now : null; task.UpdatedAt = Now; }
        foreach (var task in project.Tasks.Where(t => t.StatusBeforeClosedId == statusId)) task.StatusBeforeClosedId = null;
        group.Statuses.Remove(status); Touch(project);
    }

    public void AddDependency(Project project, string taskId, string dependencyTaskId, double dayOffset = 0)
    {
        var task = Task(project, taskId); Task(project, dependencyTaskId);
        if (task.Dependencies.Any(d => d.TaskId == dependencyTaskId)) return;
        if (WouldCreateDependencyCycle(project, taskId, dependencyTaskId)) throw new InvalidOperationException("任务依赖不能形成循环");
        task.Dependencies.Add(new Dependency { TaskId = dependencyTaskId, DayOffset = dayOffset }); task.UpdatedAt = Now; Touch(project);
    }
    public void RemoveDependency(Project project, string taskId, string dependencyTaskId)
    { var task = Task(project, taskId); task.Dependencies.RemoveAll(d => d.TaskId == dependencyTaskId); task.UpdatedAt = Now; Touch(project); }
    public bool WouldCreateDependencyCycle(Project project, string taskId, string dependencyTaskId)
    {
        var lookup = project.Tasks.ToDictionary(t => t.Id);
        var pending = new Stack<string>(); pending.Push(dependencyTaskId); var visited = new HashSet<string>();
        while (pending.TryPop(out var id))
        {
            if (id == taskId) return true;
            if (!visited.Add(id) || !lookup.TryGetValue(id, out var task)) continue;
            foreach (var dependency in task.Dependencies) pending.Push(dependency.TaskId);
        }
        return false;
    }
    public IReadOnlyList<ProjectTask> GetBlockingDependencies(Project project, ProjectTask task) => task.Dependencies.Select(d => project.Tasks.FirstOrDefault(t => t.Id == d.TaskId)).Where(t => t is not null && !IsCompleted(project, t)).Cast<ProjectTask>().ToArray();
    public bool CanCompleteTask(Project project, string taskId)
    { var task = Task(project, taskId); return task.Dependencies.All(d => project.Tasks.FirstOrDefault(t => t.Id == d.TaskId) is { } dependency && IsCompleted(project, dependency)); }
    public bool IsCompleted(Project project, ProjectTask task) => GetTaskStatus(project, task)?.Category == "done";
    public bool IsClosed(Project project, ProjectTask task) => GetTaskStatus(project, task)?.Category is "done" or "cancelled";
    public TaskStatusDefinition? GetTaskStatus(Project project, ProjectTask task) => project.TaskGroups.FirstOrDefault(g => g.Id == task.TaskGroupId)?.Statuses.FirstOrDefault(s => s.Id == task.StatusId);
    public TaskGroup? GetTaskGroup(Project project, ProjectTask task) => project.TaskGroups.FirstOrDefault(g => g.Id == task.TaskGroupId);

    public Subtask AddSubtask(Project project, string taskId, string title)
    { var task = Task(project, taskId); var item = new Subtask { Id = NewId(), Title = Name(title) }; task.Subtasks.Add(item); task.UpdatedAt = Now; Touch(project); return item; }
    public void ToggleSubtask(Project project, string taskId, string subtaskId)
    { var task = Task(project, taskId); var item = task.Subtasks.FirstOrDefault(s => s.Id == subtaskId) ?? throw new InvalidOperationException("子任务不存在"); item.Done = !item.Done; task.UpdatedAt = Now; Touch(project); }
    public TaskComment AddComment(Project project, string taskId, string content)
    { var task = Task(project, taskId); var item = new TaskComment { Id = NewId(), Content = Name(content), CreatedAt = Now }; task.Comments.Add(item); task.UpdatedAt = Now; Touch(project); return item; }
    public void StartTracking(Project project, string taskId)
    { var task = Task(project, taskId); if (IsClosed(project, task)) throw new InvalidOperationException("已关闭任务不能开始计时"); task.TrackedStart ??= Now; task.UpdatedAt = Now; Touch(project); }
    public TimeSpan GetTrackedDuration(Project project, ProjectTask task)
    {
        if (!DateTimeOffset.TryParse(task.TrackedStart, out var start)) return TimeSpan.Zero;
        var end = IsCompleted(project, task) && DateTimeOffset.TryParse(task.CompletedAt, out var completed) ? completed : _clock.GetUtcNow();
        return end > start ? end - start : TimeSpan.Zero;
    }

    public ProjectStatistics GetStatistics(Project project, DateOnly? today = null)
    {
        var day = today ?? Today; var tasks = project.Tasks;
        var todo = tasks.Count(t => GetTaskStatus(project, t)?.Category == "todo");
        var active = tasks.Count(t => GetTaskStatus(project, t)?.Category == "active");
        var done = tasks.Count(t => IsCompleted(project, t)); var cancelled = tasks.Count(t => GetTaskStatus(project, t)?.Category == "cancelled");
        var open = tasks.Where(t => !IsClosed(project, t)).ToArray();
        bool DateMatches(ProjectTask task, Func<DateOnly, bool> predicate) => DateOnly.TryParseExact(task.DueDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var due) && predicate(due);
        return new(tasks.Count, todo, active, done, cancelled, open.Count(t => DateMatches(t, d => d < day)), open.Count(t => DateMatches(t, d => d == day)), open.Count(t => t.Priority == "high"), tasks.Count == 0 ? 0 : 100.0 * done / tasks.Count);
    }

    private void RequireDependencies(Project project, ProjectTask task)
    { if (!CanCompleteTask(project, task.Id)) throw new InvalidOperationException("前置任务尚未全部完成，无法标记完成"); }
    private static TaskGroup Group(Project project, string id) => project.TaskGroups.FirstOrDefault(g => g.Id == id) ?? throw new InvalidOperationException("任务组不存在");
    private static ProjectTask Task(Project project, string id) => project.Tasks.FirstOrDefault(t => t.Id == id) ?? throw new InvalidOperationException("任务不存在");
    private static TaskStatusDefinition Status(TaskGroup group, string id) => group.Statuses.FirstOrDefault(s => s.Id == id) ?? throw new InvalidOperationException("状态不属于任务组");
    private static string Name(string text) => string.IsNullOrWhiteSpace(text) ? throw new ArgumentException("名称或内容不能为空") : text.Trim();
    private void Touch(Project project) => project.UpdatedAt = Now;
    private static void CheckCategory(string category) { if (category is not "todo" and not "active" and not "done" and not "cancelled") throw new ArgumentException("无效的状态类别"); }
    private static void CheckOrder(IEnumerable<string> knownIds, IReadOnlyList<string> ids)
    { var known = knownIds.ToHashSet(); if (known.Count != ids.Count || ids.Distinct().Count() != ids.Count || ids.Any(id => !known.Contains(id))) throw new ArgumentException("排序列表必须恰好包含每个现有 ID 一次"); }
}
