using System.Text.Json;
using PureProject.Core;

internal static class TextRulesTests
{
    public static void Register(List<(string Name, Action Test)> tests)
    {
        tests.Add(("Text input counts graphemes and keeps the independent UTF16 resource cap", () =>
        {
            Check(TextRules.CountGraphemes("中🌱👩‍💻e\u0301🇨🇳") == 5, "Unicode text elements counted incorrectly");
            var project = string.Concat(Enumerable.Repeat("👩‍💻", 64));
            Check(TextRules.RequireProjectName(project) == project, "64 complex graphemes rejected");
            Reject(() => TextRules.RequireProjectName(project + "a"));
            var title = string.Concat(Enumerable.Repeat("e\u0301", 128));
            Check(TextRules.RequireTaskTitle(title) == title, "128 combining graphemes rejected");
            Reject(() => TextRules.RequireTaskTitle(title + "a"));
            Reject(() => TextRules.RequireProjectName("a" + new string('\u0301', 500)));
            Reject(() => TextRules.RequireTaskTitle("a" + new string('\u0301', 2000)));
            Reject(() => TextRules.RequireTaskTitle(" \t\r\n"));
        }));
        tests.Add(("Legacy values remain exact when unchanged, including surrounding whitespace", () =>
        {
            var name = " " + new string('中', 498) + " ";
            var title = new string('题', 2000);
            Check(TextRules.RequireProjectName(name, name) == name, "Unchanged name was trimmed");
            Check(TextRules.RequireTaskTitle(title, title) == title, "Unchanged legacy title changed");
            Reject(() => TextRules.RequireProjectName(name.Replace('中', '文'), name));
            Reject(() => TextRules.RequireTaskTitle(new string('文', 2000), title));
            Reject(() => TextRules.RequireTaskTitle(title + "x", title + "x"));
            Check(TextRules.RequireTaskTitle(" 新标题 ", title) == "新标题", "Explicit short edit not normalized");
            var service = new ProjectService(); var project = service.CreateProject("project");
            var task = service.CreateTask(project, "task"); project.Name = name; task.Title = title;
            var draft = PmSerializer.CloneTask(task); draft.Priority = "high";
            service.SaveTask(project, draft, false);
            Check(project.Tasks.Single().Title == title, "Unrelated edit rejected or changed legacy title");
            var copies = service.DuplicateTasksInStatus(project, task.TaskGroupId, task.StatusId);
            Check(copies.Single().Title == title, "Copy changed legacy title");
            var imported = PmSerializer.ParsePm(PmSerializer.ExportPm(project));
            Check(imported.Name == name && imported.Tasks.All(t => t.Title == title), "Legacy import/export changed text");
        }));
        tests.Add(("New business mutations enforce text rules without partially creating tasks", () =>
        {
            var service = new ProjectService();
            Reject(() => service.CreateProject(new string('中', 65)));
            var project = service.CreateProject(new string('中', 64));
            var before = JsonSerializer.Serialize(project);
            Reject(() => service.CreateTask(project, new string('文', 129)));
            Check(JsonSerializer.Serialize(project) == before, "Rejected create mutated project");
            var task = service.CreateTask(project, new string('文', 128));
            var edited = PmSerializer.CloneTask(task); edited.Title += "x";
            before = JsonSerializer.Serialize(project);
            Reject(() => service.SaveTask(project, edited, false));
            Check(JsonSerializer.Serialize(project) == before, "Rejected title edit mutated project");
        }));
        tests.Add(("Task save publishes only a validated target and leaves prior shared objects unchanged", () =>
        {
            var service = new ProjectService(); var project = service.CreateProject("transaction");
            var first = service.CreateTask(project, "first"); var second = service.CreateTask(project, "second");
            first.Subtasks.Add(new() { Id = "sub", Title = "old" });
            first.Extra["payload"] = JsonSerializer.SerializeToElement(new { nested = new[] { "old" } });
            var oldList = project.Tasks; var oldGroups = project.TaskGroups;
            var draft = PmSerializer.CloneTask(first); draft.Subtasks[0].Title = "new";
            draft.Dependencies.Add(new() { TaskId = draft.Id });
            var before = JsonSerializer.Serialize(project);
            Reject(() => service.SaveTask(project, draft, false));
            Check(JsonSerializer.Serialize(project) == before && ReferenceEquals(project.Tasks, oldList), "Failed transaction changed data or list");
            draft.Dependencies.Clear(); draft.Title = "edited";
            service.SaveTask(project, draft, false);
            Check(!ReferenceEquals(project.Tasks, oldList), "Task list not atomically replaced");
            Check(ReferenceEquals(project.Tasks[1], second) && ReferenceEquals(project.TaskGroups, oldGroups), "Unchanged objects unnecessarily cloned");
            Check(first.Title == "first" && first.Subtasks[0].Title == "old", "Prior target snapshot mutated");
            Check(project.Tasks[0].Subtasks[0].Title == "new" && !ReferenceEquals(project.Tasks[0], draft), "Published target aliases mutable editor draft");
            draft.Subtasks[0].Title = "after-save";
            Check(project.Tasks[0].Subtasks[0].Title == "new", "Draft edit after commit leaked into published data");
        }));
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception error) when (error is ArgumentException or InvalidOperationException or PmValidationException) { return; }
        throw new Exception("Expected a rejected invalid input");
    }
}
