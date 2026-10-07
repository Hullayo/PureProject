using System.Text.Json;
using PureProject.Core;

internal static class TextRulesTests
{
    public static void Register(List<(string Name, Action Test)> tests)
    {
        foreach (var (kind, limit) in new[] { (TextFieldKind.Title, 64), (TextFieldKind.LongText, 1024), (TextFieldKind.Status, 20) })
        {
            tests.Add(($"{kind} input accepts {limit - 1}/{limit} and rejects {limit + 1} complete Unicode graphemes", () =>
            {
                Check(TextRules.GetLimit(kind) == limit, $"Wrong {kind} input limit");
                foreach (var element in new[] { "中", "𠀀", "👩‍💻", "e\u0301", "🇨🇳" })
                {
                    var below = Repeat(element, limit - 1); var exact = below + element; var over = exact + element;
                    Check(TextRules.CountGraphemes(exact) == limit, "Unicode text elements counted incorrectly");
                    Check(TextRules.Require(below, kind, "测试字段") == below, "Below-limit text changed");
                    Check(TextRules.Require(exact, kind, "测试字段") == exact, "Exact-limit text changed");
                    Reject(() => TextRules.Require(over, kind, "测试字段"));
                }
            }));
        }
        tests.Add(("Typing and paste accept complete replacements while blocking excess text before save", () =>
        {
            foreach (var (kind, limit) in new[] { (TextFieldKind.Title, 64), (TextFieldKind.LongText, 1024), (TextFieldKind.Status, 20) })
            {
                var current = Repeat("👩‍💻", limit);
                Check(TextRules.CanAcceptInput("", current, kind), "Paste at the grapheme boundary rejected");
                Check(!TextRules.CanAcceptInput(current, current + "中", kind), "Typing beyond the limit accepted");
                Check(!TextRules.CanAcceptInput(current, current + " ", kind), "Trailing whitespace bypassed the input limit");
                Check(TextRules.CanAcceptInput(current, Repeat("𠀀", limit), kind), "Selected text replacement at the limit rejected");
                Check(TextRules.CanAcceptInput(current, "", kind), "Clearing an input rejected");
                Check(!TextRules.CanAcceptInput("", new string(' ', limit + 1), kind), "Whitespace paste bypassed the input limit");
                var legacy = new string('旧', limit + 20); var shorter = legacy[..^1];
                Check(TextRules.CanAcceptInput(legacy, legacy, kind), "Unchanged legacy text rejected");
                Check(TextRules.CanAcceptInput(legacy, shorter, kind), "Gradual shortening of legacy text rejected");
                Check(!TextRules.CanAcceptInput(legacy, legacy + "长", kind), "Legacy text allowed to grow");
                Check(!TextRules.CanAcceptInput(legacy, new string('新', legacy.Length), kind), "Oversized replacement accepted without shortening");
                Reject(() => TextRules.Require(shorter, kind, "测试字段", legacy));
                Check(TextRules.Require(shorter[..limit], kind, "测试字段", legacy).Length == limit, "Legacy text could not be shortened to the new limit");
            }
            Check(!TextRules.CanAcceptInput("", "a" + new string('\u0301', 500), TextFieldKind.Title, 500), "Input bypassed its independent resource cap");
        }));
        tests.Add(("Input wrappers preserve whitespace rules and the independent UTF16 resource caps", () =>
        {
            Check(TextRules.CountGraphemes("中𠀀🌱👩‍💻e\u0301🇨🇳") == 6, "Mixed text elements counted incorrectly");
            Check(TextRules.RequireProjectName(Repeat("👩‍💻", 64)) == Repeat("👩‍💻", 64), "Project wrapper counts UTF16 units");
            Check(TextRules.RequireTaskTitle(Repeat("e\u0301", 64)) == Repeat("e\u0301", 64), "Task wrapper counts UTF16 units");
            Check(TextRules.RequireStatusName(Repeat("𠀀", 20)) == Repeat("𠀀", 20), "Status wrapper counts UTF16 units");
            Reject(() => TextRules.RequireProjectName(new string('中', 65)));
            Reject(() => TextRules.RequireTaskTitle(new string('题', 65)));
            Reject(() => TextRules.RequireStatusName(new string('态', 21)));
            Reject(() => TextRules.RequireProjectName("a" + new string('\u0301', 500)));
            Reject(() => TextRules.RequireTaskTitle("a" + new string('\u0301', 2000)));
            Reject(() => TextRules.RequireStatusName("a" + new string('\u0301', 500)));
            Reject(() => TextRules.RequireLongText("a" + new string('\u0301', 200000)));
            Reject(() => TextRules.RequireTaskTitle(" \t\r\n"));
            Reject(() => TextRules.RequireLongText(" \r\n", "评论", required: true));
            Check(TextRules.RequireLongText(null) == "", "Optional description cannot be cleared");
            var multiline = "  第一行\r\n第二行\t ";
            Check(TextRules.RequireLongText(multiline) == multiline, "Long text whitespace was normalized");
            Check(TextRules.RequireTaskTitle(" 新标题 ") == "新标题", "Title normalization changed");
        }));
        tests.Add(("Unchanged legacy values retain their exact text and remain subject to storage resource limits", () =>
        {
            var name = " " + new string('中', 498) + " ";
            var title = new string('题', 2000);
            var body = "\r\n" + new string('文', 199995) + "\t  ";
            var status = new string('态', 500);
            Check(TextRules.RequireProjectName(name, name) == name, "Unchanged name was trimmed");
            Check(TextRules.RequireTaskTitle(title, title) == title, "Unchanged legacy title changed");
            Check(TextRules.RequireLongText(body, original: body) == body, "Unchanged body lost whitespace");
            Check(TextRules.RequireStatusName(status, status) == status, "Unchanged legacy status changed");
            Reject(() => TextRules.RequireProjectName(name.Replace('中', '文'), name));
            Reject(() => TextRules.RequireTaskTitle(new string('文', 2000), title));
            Reject(() => TextRules.RequireLongText(body.Replace('文', '字'), original: body));
            Reject(() => TextRules.RequireStatusName(status.Replace('态', '状'), status));
            Reject(() => TextRules.RequireProjectName(name + "x", name + "x"));
            Reject(() => TextRules.RequireTaskTitle(title + "x", title + "x"));
            Reject(() => TextRules.RequireLongText(body + "x", original: body + "x"));
            Reject(() => TextRules.RequireStatusName(status + "x", status + "x"));
            Check(TextRules.RequireTaskTitle(" 新标题 ", title) == "新标题", "Explicit short edit not normalized");
        }));
        tests.Add(("Project create and update validate every field before changing live data", () =>
        {
            var service = new ProjectService();
            Reject(() => service.CreateProject(new string('中', 65)));
            Reject(() => service.CreateProject("project", new string('文', 1025)));
            var project = service.CreateProject(new string('中', 64), new string('文', 1024));
            RejectWithoutMutation(project, () => service.UpdateProject(project, "changed", new string('文', 1025), "#123456"));
            RejectWithoutMutation(project, () => service.UpdateProject(project, new string('中', 65), "changed", "#123456"));
            project.Name = " " + new string('旧', 100) + " "; project.Description = "\n" + new string('旧', 1500) + "\n";
            var name = project.Name; var body = project.Description;
            service.UpdateProject(project, name, body, "#123456");
            Check(project.Name == name && project.Description == body && project.Color == "#123456", "Unrelated project edit changed legacy text");
            RejectWithoutMutation(project, () => service.UpdateProject(project, name + "改", body, "#abcdef"));
            RejectWithoutMutation(project, () => service.UpdateProject(project, name, body + "改", "#abcdef"));
            service.UpdateProject(project, "短名称", "\n新说明\n", "#abcdef");
            Check(project.Name == "短名称" && project.Description == "\n新说明\n", "Valid project edit not saved exactly");
        }));
        tests.Add(("Task creation and full editor saves reject new or edited oversized fields atomically", () =>
        {
            var service = new ProjectService(); var project = service.CreateProject("project");
            RejectWithoutMutation(project, () => service.CreateTask(project, new string('题', 65)));
            var task = service.CreateTask(project, new string('题', 64));
            service.AddSubtask(project, task.Id, new string('子', 64));
            service.AddComment(project, task.Id, new string('评', 1024));
            var valid = PmSerializer.CloneTask(task); valid.Description = new string('文', 1024); valid.Tags.Add(new string('签', 64));
            service.SaveTask(project, valid, false);
            var mutations = new Action<ProjectTask>[]
            {
                draft => draft.Title += "多",
                draft => draft.Description += "多",
                draft => draft.Subtasks[0].Title += "多",
                draft => draft.Comments[0].Content += "多",
                draft => draft.Tags[0] += "多",
                draft => draft.Subtasks.Add(new() { Id = "new-subtask", Title = new string('子', 65) }),
                draft => draft.Comments.Add(new() { Id = "new-comment", Content = new string('评', 1025) }),
                draft => draft.Tags.Add(new string('新', 65))
            };
            foreach (var mutation in mutations)
            {
                var draft = PmSerializer.CloneTask(project.Tasks.Single()); mutation(draft);
                RejectWithoutMutation(project, () => service.SaveTask(project, draft, false));
                draft.Id = ProjectService.NewId();
                RejectWithoutMutation(project, () => service.SaveTask(project, draft, true));
            }
            RejectWithoutMutation(project, () => service.AddSubtask(project, task.Id, new string('子', 65)));
            RejectWithoutMutation(project, () => service.AddComment(project, task.Id, new string('评', 1025)));
        }));
        tests.Add(("Group, status and tag names enforce their own limits without partial mutations", () =>
        {
            var service = new ProjectService(); var project = service.CreateProject("project");
            var group = service.CreateTaskGroup(project, new string('组', 64));
            var status = service.CreateTaskStatus(project, group.Id, new string('态', 20));
            var tag = service.CreateTag(project, new string('签', 64));
            RejectWithoutMutation(project, () => service.CreateTaskGroup(project, new string('组', 65)));
            RejectWithoutMutation(project, () => service.RenameTaskGroup(project, group.Id, new string('组', 65)));
            RejectWithoutMutation(project, () => service.CreateTaskStatus(project, group.Id, new string('态', 21)));
            RejectWithoutMutation(project, () => service.UpdateTaskStatusDefinition(project, group.Id, status.Id, new string('态', 21), "#abcdef", "cancelled"));
            RejectWithoutMutation(project, () => service.CreateTag(project, new string('签', 65)));
            RejectWithoutMutation(project, () => service.RenameTag(project, tag.Id, new string('签', 65)));
            group.Name = " " + new string('旧', 100) + " "; status.Name = " " + new string('态', 30) + " "; tag.Name = " " + new string('签', 70) + " ";
            var groupName = group.Name; var statusName = status.Name; var tagName = tag.Name;
            service.RenameTaskGroup(project, group.Id, groupName);
            service.UpdateTaskStatusDefinition(project, group.Id, status.Id, statusName, "#abcdef", "cancelled");
            service.RenameTag(project, tag.Id, tagName);
            Check(group.Name == groupName && status.Name == statusName && tag.Name == tagName, "Unchanged names normalized or rejected");
            Check(status.Color == "#abcdef" && status.Category == "cancelled", "Unrelated status changes were lost");
            RejectWithoutMutation(project, () => service.RenameTaskGroup(project, group.Id, groupName + "改"));
            RejectWithoutMutation(project, () => service.UpdateTaskStatusDefinition(project, group.Id, status.Id, statusName + "改", "#123456", "active"));
            RejectWithoutMutation(project, () => service.RenameTag(project, tag.Id, tagName + "改"));
        }));
        tests.Add(("Legacy task fields survive unrelated edits and round trips but cannot bypass limits on new children", () =>
        {
            var service = new ProjectService(); var project = service.CreateProject("project");
            var task = service.CreateTask(project, "task");
            project.Name = " " + new string('名', 498) + " "; project.Description = new string('说', 2048); project.Readme = "\n" + new string('读', 2048) + "\n";
            project.TaskGroups[0].Name = new string('组', 500); project.TaskGroups[0].Statuses[0].Name = new string('态', 500);
            task.Title = new string('题', 2000); task.Description = "\n" + new string('文', 2048) + "\n";
            task.Subtasks.Add(new() { Id = "old-subtask", Title = new string('子', 1500) });
            task.Comments.Add(new() { Id = "old-comment", Content = " " + new string('评', 1500) + " " });
            task.Tags.Add(new string('签', 70));
            var draft = PmSerializer.CloneTask(task); draft.Priority = "high";
            service.SaveTask(project, draft, false);
            var saved = project.Tasks.Single();
            Check(saved.Title == task.Title && saved.Description == task.Description && saved.Subtasks[0].Title == task.Subtasks[0].Title && saved.Comments[0].Content == task.Comments[0].Content && saved.Tags[0] == task.Tags[0], "Unrelated edit changed legacy task text");
            foreach (var mutation in new Action<ProjectTask>[]
            {
                item => item.Description += "改",
                item => item.Subtasks[0].Title += "改",
                item => item.Comments[0].Content += "改",
                item => item.Tags[0] += "改",
                item => item.Subtasks[0].Id = "different-subtask",
                item => item.Comments[0].Id = "different-comment"
            })
            {
                var changed = PmSerializer.CloneTask(saved); mutation(changed);
                RejectWithoutMutation(project, () => service.SaveTask(project, changed, false));
            }
            var copies = service.DuplicateTasksInStatus(project, task.TaskGroupId, task.StatusId);
            Check(copies.Single().Title == task.Title && copies.Single().Comments[0].Content == task.Comments[0].Content, "Copy changed legacy text");
            var imported = PmSerializer.ParsePm(PmSerializer.ExportPm(project));
            Check(imported.Name == project.Name && imported.Description == project.Description && imported.Readme == project.Readme, "Legacy project import/export changed text");
            Check(imported.TaskGroups[0].Name == project.TaskGroups[0].Name && imported.TaskGroups[0].Statuses[0].Name == project.TaskGroups[0].Statuses[0].Name, "Legacy group/status import limits were reduced");
            Check(imported.Tasks.All(item => item.Title == task.Title && item.Description == task.Description && item.Subtasks[0].Title == task.Subtasks[0].Title && item.Comments[0].Content == task.Comments[0].Content && item.Tags[0] == task.Tags[0]), "Legacy task import/export changed text");
        }));
        tests.Add(("Legacy children with duplicate IDs preserve each distinct oversized value on unrelated edits", () =>
        {
            var service = new ProjectService(); var project = service.CreateProject("duplicate legacy child IDs");
            var task = service.CreateTask(project, "task");
            task.Subtasks.Add(new() { Id = "shared-subtask", Title = new string('甲', 80) });
            task.Subtasks.Add(new() { Id = "shared-subtask", Title = " " + new string('乙', 90) + " " });
            task.Comments.Add(new() { Id = "shared-comment", Content = new string('丙', 1100) });
            task.Comments.Add(new() { Id = "shared-comment", Content = "\n" + new string('丁', 1200) + "\n" });
            PmSerializer.EnsureValid(project);
            var originalTitles = task.Subtasks.Select(item => item.Title).ToArray();
            var originalComments = task.Comments.Select(item => item.Content).ToArray();
            var draft = PmSerializer.CloneTask(task); draft.Priority = "high";
            service.SaveTask(project, draft, false);
            var saved = project.Tasks.Single();
            Check(saved.Priority == "high" && saved.Subtasks.Select(item => item.Title).SequenceEqual(originalTitles)
                && saved.Comments.Select(item => item.Content).SequenceEqual(originalComments), "Duplicate IDs caused distinct unchanged legacy values to be rejected or normalized");
            foreach (var mutation in new Action<ProjectTask>[]
            {
                item => item.Subtasks[1].Title = new string('新', 90),
                item => item.Comments[1].Content = new string('新', 1200)
            })
            {
                var changed = PmSerializer.CloneTask(saved); mutation(changed);
                RejectWithoutMutation(project, () => service.SaveTask(project, changed, false));
            }
        }));
        tests.Add(("Unchanged legacy blank tags remain exact while new or modified blank tags are rejected atomically", () =>
        {
            var service = new ProjectService(); var project = service.CreateProject("legacy blank tags");
            var legacy = service.CreateTask(project, "legacy task");
            legacy.Tags = ["", " ", "\t", " \t "];
            PmSerializer.EnsureValid(project);
            var originalTags = legacy.Tags.ToArray();
            var draft = PmSerializer.CloneTask(legacy); draft.Priority = "high";
            service.SaveTask(project, draft, false);
            var saved = project.Tasks.Single();
            Check(saved.Priority == "high" && saved.Tags.SequenceEqual(originalTags), "Unchanged blank tags were rejected, removed or normalized");
            var changedLegacy = PmSerializer.CloneTask(saved); changedLegacy.Tags[0] = "\r\n";
            RejectWithoutMutation(project, () => service.SaveTask(project, changedLegacy, false));

            var normal = service.CreateTask(project, "normal task"); normal.Tags.Add("有效标签");
            foreach (var blank in new[] { "", " ", "\t", " \t ", "\r\n" })
            {
                var added = PmSerializer.CloneTask(normal); added.Tags.Add(blank);
                RejectWithoutMutation(project, () => service.SaveTask(project, added, false));
                var changed = PmSerializer.CloneTask(normal); changed.Tags[0] = blank;
                RejectWithoutMutation(project, () => service.SaveTask(project, changed, false));
                var fresh = PmSerializer.CloneTask(normal); fresh.Id = ProjectService.NewId(); fresh.Tags = [blank];
                RejectWithoutMutation(project, () => service.SaveTask(project, fresh, true));
            }
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

    private static string Repeat(string element, int count) => string.Concat(Enumerable.Repeat(element, count));
    private static void RejectWithoutMutation(Project project, Action action)
    {
        var before = JsonSerializer.Serialize(project);
        Reject(action);
        Check(JsonSerializer.Serialize(project) == before, "Rejected input mutated project data or timestamps");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action)
    {
        try { action(); } catch (Exception error) when (error is ArgumentException or InvalidOperationException or PmValidationException) { return; }
        throw new Exception("Expected a rejected invalid input");
    }
}
