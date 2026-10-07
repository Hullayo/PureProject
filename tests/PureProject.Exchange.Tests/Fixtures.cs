using System.Text.Json;
using PureProject.Core;

static class Fixtures
{
    public static Project Create(string id, int count, bool rich)
    {
        const string timestamp = "2026-10-06T09:30:00+08:00";
        var project = new Project { Id = id, Name = "中文项目 =SUM(A1:A2)", Description = "项目说明与中文/emoji 🌱", CreatedAt = timestamp, UpdatedAt = timestamp, DefaultTaskGroupId = "g-a", SyncEnabled = false, Color = "#445566", SortOrder = 2.5, StartDate = "2026-10-01", EndDate = "2027-01-01" };
        foreach (var gid in new[] { "g-a", "g-b" }) project.TaskGroups.Add(new TaskGroup
        {
            Id = gid, Name = gid == "g-a" ? "研发组" : "空任务组", InitialStatusId = gid + "-todo", CompletionStatusId = gid + "-done", SortOrder = gid == "g-a" ? 0.5 : 2,
            Statuses = [new() { Id = gid + "-todo", Name = "待办", Category = "todo" }, new() { Id = gid + "-active", Name = "进行中", Category = "active" }, new() { Id = gid + "-done", Name = "完成", Category = "done" }, new() { Id = gid + "-cancel", Name = "取消", Category = "cancelled" }]
        });
        for (var i = 0; i < count; i++) project.Tasks.Add(new ProjectTask
        {
            Id = $"t-{i:D5}", Title = i == 0 ? "=HYPERLINK(\"https://example.invalid\",\"标题\")" : $"任务 {i}", TaskGroupId = "g-a", StatusId = "g-a-todo", Priority = "medium", Description = rich && i == 0 ? new string('长', 90000) : "任务说明 _x0041_ / 中文 🌱",
            CreatedAt = timestamp, UpdatedAt = timestamp, DueDate = i == 0 ? "2026-11-01" : i == 1 ? null : "", DueTime = "09:30", StartOffset = 1.5, Color = "#abcdef", TrackedStart = timestamp, Reminder = "2026-11-01T09:00:00+08:00", GitRepoPath = "C:\\项目\\repo",
            Dependencies = i == 0 ? [] : [new() { TaskId = $"t-{i - 1:D5}", DayOffset = 2.5 }],
            Subtasks = rich ? [new() { Id = "sub-1", Title = "+子任务", Done = false }, new() { Id = "sub-2", Title = "已完成子任务", Done = true }] : [],
            Comments = rich ? [new() { Id = "comment-1", Content = "评论\n第二行", CreatedAt = timestamp }] : []
        });
        if (rich)
        {
            project.Readme = "# 项目说明\n完整 Markdown"; project.Template = "custom"; project.LegacyReadmeFile = "README.md";
            project.LegacyTemplate = new() { Dirs = ["源码"], Files = ["a.txt"], FileContents = new() { ["a.txt"] = "内容" } };
            project.FileTree = JsonSerializer.SerializeToElement(new { name = "root", future = new[] { 1, 2 } }); project.Flowchart = JsonSerializer.SerializeToElement(new { nodes = new[] { "a" }, edges = Array.Empty<string>() }); project.KanbanColumns = ["a", "b"];
            project.Tags = [new() { Id = "tag-1", Name = "标签", Color = "#123456" }]; project.Milestones = [new() { Id = "m1", Title = "里程碑", Date = "2026-12-01", Description = "版本发布" }]; project.Changelog = [new() { Version = "1.0", Date = "2026-10-01", Info = "完整历史", Auto = false }];
            project.Tasks[0].Tags = ["tag-1"]; project.Tasks[0].Recurrence = new() { Freq = "weekly", Interval = 2, ByWeekday = [1, 3], End = "count", Count = 9, Until = "2027-01-01", SourceTaskId = "source" };
            var extension = JsonSerializer.SerializeToElement(new { unicode = "扩展", array = new object?[] { 1, "二", null }, nested = new { enabled = true } });
            project.Extra["future_project"] = extension; project.Extra["storage"] = JsonSerializer.SerializeToElement("unknown-storage"); project.PmExtensions["future_envelope"] = extension;
            foreach (var group in project.TaskGroups) { group.Extra["future_group"] = extension; foreach (var status in group.Statuses) status.Extra["future_status"] = extension; }
            foreach (var task in project.Tasks) { task.Extra["future_task"] = extension; foreach (var d in task.Dependencies) d.Extra["future_dependency"] = extension; foreach (var s in task.Subtasks) s.Extra["future_subtask"] = extension; foreach (var c in task.Comments) c.Extra["future_comment"] = extension; }
            project.Tags[0].Extra["future_tag"] = extension; project.Milestones[0].Extra["future_milestone"] = extension; project.Changelog[0].Extra["future_history"] = extension; project.LegacyTemplate.Extra["future_template"] = extension; project.Tasks[0].Recurrence!.Extra["future_recurrence"] = extension;
        }
        return project;
    }
}
