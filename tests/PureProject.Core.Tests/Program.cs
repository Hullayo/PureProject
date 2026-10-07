using System.Text.Json;
using System.Text.Json.Nodes;
using PureProject.Core;

var tests = new List<(string Name, Action Test)>();
var service = new ProjectService(new FixedClock());
// The default suite is self-contained after build/publish. An optional first
// argument may still point at another fixture root or an older source checkout.
var fixtureRoot = args.FirstOrDefault() ?? Path.Combine(AppContext.BaseDirectory, "Fixtures");
void Test(string name, Action action) => tests.Add((name, action));
void Assert(bool condition, string reason) { if (!condition) throw new Exception(reason); }
void Reject(Action action, string reason)
{
    try { action(); } catch (Exception e) when (e is PmValidationException or InvalidOperationException or ArgumentException) { return; }
    throw new Exception(reason);
}
Project Fresh() => service.CreateProject("测试项目", "原生重构兼容测试");

Test("All six existing demo .pm files import and round trip", () =>
{
    foreach (var folder in new[] { "static", "v1.2/static" })
        foreach (var name in new[] { "demo.pm", "demo2.pm", "demo3.pm" })
        {
            var project = PmSerializer.ParsePm(File.ReadAllText(Path.Combine(fixtureRoot, folder, name)));
            var again = PmSerializer.ParsePm(PmSerializer.ExportPm(project));
            Assert(project.Tasks.Count > 0 && project.Tasks.Count == again.Tasks.Count, name + " tasks lost");
            Assert(project.Readme == again.Readme, name + " README lost");
            Assert(JsonSerializer.Serialize(project.LegacyTemplate) == JsonSerializer.Serialize(again.LegacyTemplate), name + " legacy template lost");
        }
});
Test("Schema 1–3 migration is deterministic and uses original JS FNV IDs", () =>
{
    var legacy = """
    {"version":"1.0","project":{"name":"Legacy","created_at":"2020-01-01","kanban_columns":["待办","评审中","done"]},"tasks":[{"id":"t1","title":"完成项","status":"done","updated_at":"2020-01-02"},{"id":"t2","title":"评审项","status":"评审中"}]}
    """;
    foreach (var schema in new[] { 1, 2, 3 })
    {
        var json = JsonNode.Parse(legacy)!.AsObject(); json["schema_version"] = schema;
        var project = PmSerializer.ParsePm(json.ToJsonString());
        var again = PmSerializer.ParsePm(json.ToJsonString());
        Assert(project.DefaultTaskGroupId == again.DefaultTaskGroupId, "migration IDs changed");
        Assert(service.IsCompleted(project, project.Tasks[0]), "done not migrated");
        Assert(service.GetTaskStatus(project, project.Tasks[1])!.Category == "active", "active not migrated");
        Assert(project.Tasks[0].CompletedAt == "2020-01-02", "history timestamp changed");
    }
    Assert(PmSerializer.StableId("x", "hello") == "x-4f9f2cab", "FNV mismatch");
});
Test("Unknown metadata, task, group, envelope and legacy template fields survive", () =>
{
    var source = JsonNode.Parse(PmSerializer.ExportPm(Fresh()))!.AsObject();
    source["plugin_payload"] = new JsonObject { ["a"] = 42 };
    source["project"]!["custom_project"] = "kept";
    source["project"]!["storage"] = new JsonObject { ["path"] = "C:/private" };
    source["storage"] = new JsonObject { ["ownerDeviceId"] = "private" };
    source["task_groups"]![0]!["plugin_group"] = true;
    source["template"] = new JsonObject { ["dirs"] = new JsonArray("Firmware"), ["files"] = new JsonArray("README.md"), ["file_contents"] = new JsonObject { ["README.md"] = "# 旧文档", ["binary.txt"] = "unchanged" }, ["unknown"] = 9 };
    var project = PmSerializer.ParsePm(source.ToJsonString());
    var task = service.CreateTask(project, "task"); task.Extra["task_extension"] = JsonSerializer.SerializeToElement(new[] { 1, 2, 3 });
    project = PmSerializer.ParseProjects(PmSerializer.ExportInternal(new[] { project }))[0];
    var exported = JsonNode.Parse(PmSerializer.ExportPm(project))!;
    Assert(exported["plugin_payload"]!["a"]!.GetValue<int>() == 42, "envelope extension lost");
    Assert(exported["project"]!["custom_project"]!.GetValue<string>() == "kept", "metadata extension lost");
    Assert(exported["task_groups"]![0]!["plugin_group"]!.GetValue<bool>(), "group extension lost");
    Assert(exported["tasks"]![0]!["task_extension"]!.AsArray().Count == 3, "task extension lost");
    Assert(exported["template"]!["unknown"]!.GetValue<int>() == 9, "template extension lost");
    Assert(exported["storage"] is null && exported["project"]!["storage"] is null, "device storage leaked");
});
Test("Internal arrays/bundles and both backup names import; future versions reject", () =>
{
    var p = Fresh(); service.CreateTask(p, "task");
    var internalJson = PmSerializer.ExportInternal(new[] { p });
    Assert(PmSerializer.ParseProjects(internalJson).Single().Id == p.Id, "internal bundle failed");
    Assert(PmSerializer.ParseProjects(JsonNode.Parse(internalJson)!["projects"]!.ToJsonString()).Single().Id == p.Id, "internal array failed");
    var backup = PmSerializer.ExportBackup(new[] { p });
    Assert(PmSerializer.ParseProjects(backup).Single().Id == p.Id, "backup failed");
    Assert(PmSerializer.ParseProjects(backup.Replace("pureproject-backup", "projectmanager-backup")).Single().Id == p.Id, "legacy backup failed");
    Reject(() => PmSerializer.ParseProjects(backup.Replace("\"schema_version\": 4", "\"schema_version\": 99")), "future backup accepted");
    Reject(() => PmSerializer.ParsePm(PmSerializer.ExportPm(p).Replace("\"schema_version\": 4", "\"schema_version\": 99")), "future project accepted");
});
Test("Malformed arrays, foreign statuses, duplicate IDs and dependency cycles reject", () =>
{
    var p = Fresh(); var a = service.CreateTask(p, "a"); var b = service.CreateTask(p, "b");
    var valid = PmSerializer.ExportPm(p);
    void MutateAndReject(Action<JsonNode> mutation)
    { var node = JsonNode.Parse(valid)!; mutation(node); Reject(() => PmSerializer.ParsePm(node.ToJsonString()), "malformed file accepted"); }
    MutateAndReject(n => n["tasks"] = new JsonObject());
    MutateAndReject(n => n["tasks"]![0]!["dependencies"] = new JsonObject());
    MutateAndReject(n => n["tasks"]![0]!["status_id"] = "foreign");
    MutateAndReject(n => n["tasks"]![0]!["id"] = null);
    MutateAndReject(n => n["task_groups"]![0]!["statuses"]![0]!["category"] = null);
    MutateAndReject(n => n["tasks"]![0]!["dependencies"] = new JsonArray(new JsonObject { ["dayOffset"] = 0 }));
    MutateAndReject(n => n["tasks"]![1]!["id"] = a.Id);
    MutateAndReject(n => n["tasks"]![0]!["recurrence"] = new JsonObject());
    MutateAndReject(n => n["tasks"]![0]!["dependencies"] = new JsonArray(new JsonObject { ["taskId"] = "missing", ["dayOffset"] = 0 }));
    MutateAndReject(n => { n["tasks"]![0]!["dependencies"] = new JsonArray(new JsonObject { ["taskId"] = b.Id }); n["tasks"]![1]!["dependencies"] = new JsonArray(new JsonObject { ["taskId"] = a.Id }); });
    Reject(() => PmSerializer.ParseProjects("{bad"), "invalid JSON accepted");
    Reject(() => PmSerializer.ParseProjects("[] trailing"), "invalid trailing text accepted");
});
Test("Backup restore rejects all changes when one entry is invalid", () =>
{
    var first = Fresh(); var second = Fresh(); second.Id = "second";
    var node = JsonNode.Parse(PmSerializer.ExportBackup(new[] { first, second }))!;
    node["projects"]![1]!["project"]!["name"] = "";
    Reject(() => PmSerializer.ParseProjects(node.ToJsonString()), "partial backup accepted silently");
});
Test("Completion gates dependencies; cancelled tasks do not satisfy prerequisites", () =>
{
    var p = Fresh(); var a = service.CreateTask(p, "prerequisite"); var b = service.CreateTask(p, "dependent");
    service.AddDependency(p, b.Id, a.Id);
    Reject(() => service.CompleteTask(p, b.Id), "blocked task completed");
    Reject(() => service.AddDependency(p, a.Id, b.Id), "dependency cycle added");
    var cancelled = service.CreateTaskStatus(p, a.TaskGroupId, "已取消", "cancelled");
    service.ChangeTaskStatus(p, a.Id, cancelled.Id);
    Reject(() => service.CompleteTask(p, b.Id), "cancelled prerequisite accepted");
    service.CompleteTask(p, a.Id); service.CompleteTask(p, b.Id);
    Assert(service.GetStatistics(p).Completed == 2, "completed statistics wrong");
    service.ReopenTask(p, b.Id); Assert(!service.IsClosed(p, b), "reopen failed");
    service.DeleteTask(p, a.Id); Assert(b.Dependencies.Count == 0, "delete left dangling dependency");
});
Test("Recurring completion emits one fresh instance and consumes count", () =>
{
    var p = Fresh(); var task = service.CreateTask(p, "周期任务");
    task.DueDate = "2026-01-31"; task.Recurrence = new() { Freq = "monthly", End = "count", Count = 1, Interval = 1 };
    service.AddSubtask(p, task.Id, "步骤").Done = true; service.AddComment(p, task.Id, "旧评论");
    var generated = service.CompleteTask(p, task.Id)!;
    Assert(generated.DueDate == "2026-02-28", "month clamp failed");
    Assert(task.Recurrence is null && generated.Recurrence!.Count == 0 && generated.Recurrence.SourceTaskId == task.Id, "consumption failed");
    Assert(generated.Comments.Count == 0 && !generated.Subtasks[0].Done && generated.Subtasks[0].Id != task.Subtasks[0].Id, "fresh instance state failed");
    service.CompleteTask(p, task.Id); Assert(p.Tasks.Count == 2, "completion duplicated recurrence");
    service.CompleteTask(p, generated.Id); Assert(p.Tasks.Count == 2, "exhausted count generated task");
    PmSerializer.EnsureValid(p);
});
Test("Recurrence week intervals, leap years and inclusive until dates", () =>
{
    var today = new DateOnly(2026, 1, 1);
    Assert(RecurrenceCalculator.NextOccurrence(new() { Freq = "yearly" }, "2024-02-29", today) == "2025-02-28", "leap clamp failed");
    var weekly = new RecurrenceRule { Freq = "weekly", Interval = 2, ByWeekday = [1, 3] };
    Assert(RecurrenceCalculator.NextOccurrence(weekly, "2026-10-05", today) == "2026-10-07", "same week failed");
    Assert(RecurrenceCalculator.NextOccurrence(weekly, "2026-10-07", today) == "2026-10-19", "two week interval failed");
    Assert(RecurrenceCalculator.NextOccurrence(new() { Freq = "daily", End = "until", Until = "2026-01-03" }, "2026-01-02", today) == "2026-01-03", "until inclusive failed");
    Assert(RecurrenceCalculator.NextOccurrence(new() { Freq = "daily", End = "until", Until = "2026-01-03" }, "2026-01-03", today) is null, "until overflow allowed");
});
Test("Task groups preserve archive/default and status completion invariants", () =>
{
    var p = Fresh(); var old = p.TaskGroups.Single();
    Reject(() => service.ArchiveTaskGroup(p, old.Id), "last group archived");
    var group = service.CreateTaskGroup(p, "研发组");
    var task = service.CreateTask(p, "Move me");
    service.MoveTask(p, task.Id, group.Id);
    Assert(task.TaskGroupId == group.Id && task.StatusId == group.InitialStatusId, "cross group references failed");
    service.SetDefaultTaskGroup(p, group.Id); service.ArchiveTaskGroup(p, group.Id);
    Assert(p.DefaultTaskGroupId == old.Id, "archive default fallback failed");
    Reject(() => service.CreateTask(p, "bad", group.Id), "archived group allowed create");
    service.RestoreTaskGroup(p, group.Id);
    var review = service.CreateTaskStatus(p, group.Id, "验收", "active");
    service.ChangeTaskStatus(p, task.Id, review.Id);
    service.SetGroupCompletionStatus(p, group.Id, review.Id);
    Assert(service.IsCompleted(p, task) && task.CompletedAt is not null, "completion column not reflected in tasks");
    Assert(group.Statuses.Count(s => s.Category == "done") == 1 && review.SortOrder == group.Statuses.Max(s => s.SortOrder), "completion ordering/category failed");
    PmSerializer.EnsureValid(p);
});
Test("Batch copies remap internal dependencies without changing external ones", () =>
{
    var p = Fresh(); var a = service.CreateTask(p, "a"); var b = service.CreateTask(p, "b");
    var external = service.CreateTask(p, "external", statusId: p.TaskGroups[0].CompletionStatusId);
    service.AddDependency(p, b.Id, a.Id); service.AddDependency(p, b.Id, external.Id);
    var copies = service.CopyStatusTasks(p, a.TaskGroupId, a.StatusId);
    Assert(copies.Count == 2, "wrong copied count");
    Assert(copies[1].Dependencies[0].TaskId == copies[0].Id && copies[1].Dependencies[1].TaskId == external.Id, "copy dependency mapping failed");
    Reject(() => service.ReorderTaskStatuses(p, a.TaskGroupId, new[] { a.StatusId, a.StatusId }), "bad ordering accepted");
    PmSerializer.EnsureValid(p);
});
Test("Task edit is atomic on invalid dependencies and supports valid content updates", () =>
{
    var p = Fresh(); var a = service.CreateTask(p, "original"); var edited = PmSerializer.CloneTask(a);
    edited.Title = "changed"; edited.Dependencies.Add(new() { TaskId = "missing" });
    Reject(() => service.UpdateTask(p, edited), "bad edit accepted");
    Assert(p.Tasks[0].Title == "original", "failed edit mutated project");
    edited.Dependencies.Clear(); service.UpdateTask(p, edited);
    Assert(p.Tasks[0].Title == "changed", "valid edit failed");
});
Test("Editor save supports initial done status and rejects new tasks in archived groups", () =>
{
    var p = Fresh(); var group = p.TaskGroups[0]; group.InitialStatusId = group.CompletionStatusId;
    var draft = service.CreateTask(PmSerializer.Clone(p), "Already finished");
    service.SaveTask(p, draft, true);
    Assert(p.Tasks.Single().CompletedAt is not null && service.IsCompleted(p, p.Tasks.Single()), "initial done task could not be saved");
    var archived = service.CreateTaskGroup(p, "Archive me");
    var blocked = service.CreateTask(PmSerializer.Clone(p), "blocked", archived.Id);
    service.ArchiveTaskGroup(p, archived.Id);
    var before = PmSerializer.ExportPm(p);
    Reject(() => service.SaveTask(p, blocked, true), "new task entered archived group");
    Assert(PmSerializer.ExportPm(p) == before, "rejected new task mutated project");
});
Test("Cross-group editor save evaluates final status and final dependencies atomically", () =>
{
    var p = Fresh(); var prerequisite = service.CreateTask(p, "not completed"); var task = service.CreateTask(p, "move and edit");
    service.AddDependency(p, task.Id, prerequisite.Id);
    var target = service.CreateTaskGroup(p, "Target"); target.InitialStatusId = target.CompletionStatusId;
    var active = target.Statuses.Single(s => s.Category == "active");
    var draft = PmSerializer.CloneTask(task); draft.TaskGroupId = target.Id; draft.StatusId = active.Id; draft.Dependencies.Clear();
    service.SaveTask(p, draft, false);
    var saved = p.Tasks.Single(t => t.Id == task.Id);
    Assert(saved.TaskGroupId == target.Id && saved.StatusId == active.Id && saved.Dependencies.Count == 0, "target initial done blocked legitimate open save");
    var blocked = PmSerializer.CloneTask(saved); blocked.StatusId = target.CompletionStatusId;
    blocked.Dependencies.Add(new() { TaskId = prerequisite.Id });
    var before = PmSerializer.ExportPm(p);
    Reject(() => service.SaveTask(p, blocked, false), "new incomplete dependency bypassed gate");
    Assert(PmSerializer.ExportPm(p) == before, "failed cross-group completion mutated data");
});
Test("Completed cross-group edits preserve completion history and never consume recurrence twice", () =>
{
    var p = Fresh(); var task = service.CreateTask(p, "completed"); service.CompleteTask(p, task.Id);
    task.CompletedAt = "2020-01-01T08:00:00Z";
    task.Recurrence = new() { Freq = "daily", End = "count", Count = 2 };
    var target = service.CreateTaskGroup(p, "Target");
    var draft = PmSerializer.CloneTask(task); draft.TaskGroupId = target.Id; draft.StatusId = target.CompletionStatusId; draft.Title = "Edited after completion";
    var generated = service.SaveTask(p, draft, false);
    var saved = p.Tasks.Single();
    Assert(generated is null && saved.CompletedAt == "2020-01-01T08:00:00Z" && saved.Recurrence!.Count == 2, "done-to-done edit reset history or generated recurrence");
    Assert(saved.StatusBeforeClosedId is null, "cross-group edit retained a foreign status history");
    PmSerializer.EnsureValid(p);
});
Test("New and edited recurring tasks consume final draft once and preserve v1.2 start offset", () =>
{
    var p = Fresh(); var task = service.CreateTask(PmSerializer.Clone(p), "new recurring");
    task.StatusId = p.TaskGroups[0].CompletionStatusId; task.DueDate = "2026-01-31"; task.StartOffset = 5;
    task.Recurrence = new() { Freq = "monthly", End = "count", Count = 1 };
    var next = service.SaveTask(p, task, true)!;
    Assert(next.DueDate == "2026-02-28" && next.StartOffset == 5 && next.Recurrence!.Count == 0, "new draft recurrence or v1.2 offset changed");
    Assert(p.Tasks.Single(t => t.Id == task.Id).Recurrence is null, "new completion did not consume original");
    var target = service.CreateTaskGroup(p, "Next group");
    var edited = PmSerializer.CloneTask(next); edited.TaskGroupId = target.Id; edited.StatusId = target.CompletionStatusId;
    edited.DueDate = "2026-03-31"; edited.StartOffset = 10; edited.Title = "Final editor title"; edited.Recurrence!.Count = 1;
    var further = service.SaveTask(p, edited, false)!;
    Assert(further.DueDate == "2026-04-30" && further.Title == edited.Title && further.StartOffset == 10 && further.TaskGroupId == target.Id, "cross-group recurring save ignored final editor values");
    service.SaveTask(p, PmSerializer.CloneTask(p.Tasks.Single(t => t.Id == edited.Id)), false);
    Assert(p.Tasks.Count == 3, "repeat editor save generated twice");
});
Test("C# recurrence matches v1.2 daily/weekly/monthly/yearly regression vectors", () =>
{
    var today = new DateOnly(2026, 1, 1);
    var cases = new (RecurrenceRule Rule, string Date, string Expected)[]
    {
        (new() { Freq = "daily" }, "2026-09-15", "2026-09-16"),
        (new() { Freq = "daily", Interval = 3 }, "2026-09-15", "2026-09-18"),
        (new() { Freq = "daily", Interval = 0 }, "2026-09-15", "2026-09-16"),
        (new() { Freq = "daily" }, "2026-12-31", "2027-01-01"),
        (new() { Freq = "weekly" }, "2026-09-15", "2026-09-22"),
        (new() { Freq = "weekly", Interval = 2 }, "2026-09-15", "2026-09-29"),
        (new() { Freq = "weekly", ByWeekday = [1, 3] }, "2026-09-15", "2026-09-16"),
        (new() { Freq = "weekly", ByWeekday = [1, 3] }, "2026-09-16", "2026-09-21"),
        (new() { Freq = "weekly", ByWeekday = [2] }, "2026-09-15", "2026-09-22"),
        (new() { Freq = "weekly", ByWeekday = [1, 3], Interval = 2 }, "2026-09-16", "2026-09-28"),
        (new() { Freq = "weekly", ByWeekday = [0] }, "2026-09-15", "2026-09-20"),
        (new() { Freq = "monthly" }, "2026-09-15", "2026-10-15"),
        (new() { Freq = "monthly" }, "2026-01-31", "2026-02-28"),
        (new() { Freq = "monthly" }, "2026-03-31", "2026-04-30"),
        (new() { Freq = "monthly" }, "2026-05-31", "2026-06-30"),
        (new() { Freq = "monthly" }, "2026-12-15", "2027-01-15"),
        (new() { Freq = "monthly", Interval = 3 }, "2026-01-31", "2026-04-30"),
        (new() { Freq = "monthly" }, "2026-01-30", "2026-02-28"),
        (new() { Freq = "yearly" }, "2026-09-15", "2027-09-15"),
        (new() { Freq = "yearly" }, "2024-02-29", "2025-02-28"),
        (new() { Freq = "yearly" }, "2027-02-28", "2028-02-28"),
        (new() { Freq = "yearly", Interval = 4 }, "2024-02-29", "2028-02-29"),
        (new() { Freq = "daily", End = "count", Count = 1 }, "2026-09-15", "2026-09-16"),
        (new() { Freq = "daily" }, "2026-09-15T23:00:00.000Z", "2026-09-16")
    };
    foreach (var item in cases) Assert(RecurrenceCalculator.NextOccurrence(item.Rule, item.Date, today) == item.Expected, $"v1.2 mismatch for {item.Rule.Freq} from {item.Date}");
    var rule = new RecurrenceRule { Freq = "weekly", ByWeekday = [1], End = "count", Count = 2 };
    var chain = new List<string>(); var date = "2026-09-15";
    while (RecurrenceCalculator.NextOccurrence(rule, date, today) is { } next)
    { chain.Add(next); date = next; rule = RecurrenceCalculator.Consume(rule); }
    Assert(chain.SequenceEqual(new[] { "2026-09-21", "2026-09-28" }), "count=2 chain differs from v1.2");
});

Test("Undated recurrence follows the user's local calendar at a UTC day boundary", () =>
{
    var localService = new ProjectService(new LocalBoundaryClock());
    var project = localService.CreateProject("Local calendar");
    var task = localService.CreateTask(project, "Daily without a due date");
    task.Recurrence = new RecurrenceRule { Freq = "daily" };
    var next = localService.CompleteTask(project, task.Id);
    Assert(next?.DueDate == "2026-10-06", "recurrence used the previous UTC date instead of the local date");
});

TextRulesTests.Register(tests);
ProjectPresentationTests.Register(tests);
PagedTextTests.Register(tests);
var failures = 0;
foreach (var (name, action) in tests)
{
    try { action(); Console.WriteLine($"PASS {name}"); }
    catch (Exception exception) { failures++; Console.Error.WriteLine($"FAIL {name}\n{exception}"); }
}
Console.WriteLine($"Core: {tests.Count - failures}/{tests.Count} tests passed.");
return failures == 0 ? 0 : 1;

sealed class FixedClock : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(2026, 1, 1, 8, 0, 0, TimeSpan.Zero);
}
sealed class LocalBoundaryClock : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => new(2026, 10, 4, 20, 0, 0, TimeSpan.Zero);
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.CreateCustomTimeZone("Test UTC+8", TimeSpan.FromHours(8), "UTC+8", "UTC+8");
}
