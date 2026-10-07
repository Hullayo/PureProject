using System.Text.Json;
using PureProject.Core;

internal static class ProjectPresentationTests
{
    public static void Register(List<(string Name, Action Test)> tests)
    {
        tests.Add(("Active statistics and sorting use the same archive-aware 30/35 percent scope", () =>
        {
            var service = new ProjectService();
            Project Build(int activeDone, int archivedDone)
            {
                var p = service.CreateProject("统计"); var active = p.TaskGroups.Single();
                var archived = service.CreateTaskGroup(p, "归档");
                foreach (var (group, completed) in new[] { (active, activeDone), (archived, archivedDone) })
                    for (var i = 0; i < 100; i++) service.CreateTask(p, "任务" + i, group.Id, i < completed ? group.CompletionStatusId : group.InitialStatusId);
                archived.Archived = true; return p;
            }
            var p30 = Build(30, 44); var p35 = Build(35, 28);
            var a = ProjectPresentation.ActiveStatistics(p30); var b = ProjectPresentation.ActiveStatistics(p35);
            Check(a == new ActiveProjectStatistics(100, 30, 30) && b == new ActiveProjectStatistics(100, 35, 35), "Displayed active percentages differ");
            Check(service.GetStatistics(p30).CompletionRate == 37 && service.GetStatistics(p35).CompletionRate == 31.5, "Regression fixture no longer triggers reverse physical ranking");
            var ordered = new[] { p30, p35 }.OrderByDescending(p => ProjectPresentation.ActiveStatistics(p).CompletionRate).ToArray();
            Check(ReferenceEquals(ordered[0], p35), "Archived groups reversed displayed 30/35 ordering");
            p30.TaskGroups[1].Archived = false;
            Check(ProjectPresentation.ActiveStatistics(p30) == new ActiveProjectStatistics(200, 74, 37), "Restoring group did not update the shared scope");
        }));
        tests.Add(("Calendar projection deduplicates same-day endpoints and shares filtered cross-month details", () =>
        {
            var service = new ProjectService(); var p = service.CreateProject("日期");
            p.CreatedAt = new DateTimeOffset(new DateTime(2026, 1, 30, 12, 0, 0, DateTimeKind.Local)).ToString("O");
            var group = p.TaskGroups.Single(); var other = service.CreateTaskGroup(p, "其他");
            var same = service.CreateTask(p, "同日起止", group.Id, group.CompletionStatusId); same.StartOffset = 2; same.DueDate = "2026-02-01";
            var cross = service.CreateTask(p, "跨月", group.Id); cross.StartOffset = 0; cross.DueDate = "2026-02-02";
            var filtered = service.CreateTask(p, "已筛掉", other.Id); filtered.DueDate = "2026-02-01";
            var undated = service.CreateTask(p, "无日期", group.Id);
            var before = service.CreateTask(p, "窗口之前", group.Id); before.DueDate = "2026-01-29";
            var modelBefore = JsonSerializer.Serialize(p);
            var days = ProjectPresentation.CalendarWindow(p, p.Tasks.Where(t => t.TaskGroupId == group.Id), new DateOnly(2026, 1, 31), 4);
            Check(days.Select(d => d.Tasks.Count).SequenceEqual(new[] { 1, 2, 1, 0 }), "Cross-month counts are wrong");
            Check(days[1].Tasks.Select(t => t.Id).SequenceEqual(new[] { same.Id, cross.Id }), "Counter and detail IDs differ or endpoint duplicated");
            Check(days[1].StartingCount == 1 && days[1].DueCount == 1 && days[1].CompletedCount == 1, "Same-day start/due/completion counters wrong");
            Check(days.SelectMany(d => d.Tasks).All(t => t.Id != filtered.Id && t.Id != undated.Id && t.Id != before.Id), "Filtered or out-of-window tasks leaked into details");
            Check(JsonSerializer.Serialize(p) == modelBefore, "Calendar projection mutated source model");
        }));
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
