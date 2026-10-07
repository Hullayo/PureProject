namespace PureProject.Core;

public sealed record ActiveProjectStatistics(int TaskCount, int CompletedCount, double CompletionRate);
public sealed record CalendarDaySummary(DateOnly Date, IReadOnlyList<ProjectTask> Tasks,
    int StartingCount, int DueCount, int CompletedCount);

/// <summary>Bounded view projections. These never modify the persisted model.</summary>
public static class ProjectPresentation
{
    public static ActiveProjectStatistics ActiveStatistics(Project project)
    {
        var groups = project.TaskGroups.Where(g => !g.Archived).ToDictionary(g => g.Id);
        var doneStatuses = groups.Values.SelectMany(g => g.Statuses.Where(s => s.Category == "done")
            .Select(s => (g.Id, s.Id))).ToHashSet();
        var count = 0; var done = 0;
        foreach (var task in project.Tasks)
        {
            if (!groups.ContainsKey(task.TaskGroupId)) continue;
            count++;
            if (doneStatuses.Contains((task.TaskGroupId, task.StatusId))) done++;
        }
        return new(count, done, count == 0 ? 0 : 100.0 * done / count);
    }

    public static DateOnly TaskStart(Project project, ProjectTask task)
    {
        var start = DateTimeOffset.TryParse(project.CreatedAt, out var created)
            ? DateOnly.FromDateTime(created.LocalDateTime) : DateOnly.FromDateTime(DateTime.Today);
        var offset = task.StartOffset ?? 0;
        if (double.IsFinite(offset) && offset > 0)
            start = DateOnly.FromDayNumber((int)Math.Clamp(start.DayNumber + Math.Truncate(offset), 0, DateOnly.MaxValue.DayNumber));
        return DateOnly.TryParse(task.DueDate, out var due) && due < start ? due : start;
    }

    public static IReadOnlyList<CalendarDaySummary> CalendarWindow(Project project,
        IEnumerable<ProjectTask> tasks, DateOnly start, int dayCount = 42)
    {
        if (dayCount is < 1 or > 42 || start.DayNumber > DateOnly.MaxValue.DayNumber - dayCount + 1)
            throw new ArgumentOutOfRangeException(nameof(dayCount));
        var daily = Enumerable.Range(0, dayCount).Select(_ => new List<ProjectTask>()).ToArray();
        var starts = new int[dayCount]; var dues = new int[dayCount]; var done = new int[dayCount];
        var doneStatuses = project.TaskGroups.SelectMany(g => g.Statuses.Where(s => s.Category == "done")
            .Select(s => (g.Id, s.Id))).ToHashSet();
        foreach (var task in tasks)
        {
            if (!DateOnly.TryParse(task.DueDate, out var due)) continue;
            var first = TaskStart(project, task).DayNumber - start.DayNumber;
            var last = due.DayNumber - start.DayNumber;
            var completed = doneStatuses.Contains((task.TaskGroupId, task.StatusId));
            // At most 42 references per task; one shared projection supplies both count and details.
            for (var day = Math.Max(0, first); day <= Math.Min(dayCount - 1, last); day++)
            {
                daily[day].Add(task);
                if (day == first) starts[day]++;
                if (day == last) dues[day]++;
                if (completed) done[day]++;
            }
        }
        return Enumerable.Range(0, dayCount).Select(i => new CalendarDaySummary(start.AddDays(i),
            daily[i].AsReadOnly(), starts[i], dues[i], done[i])).ToArray();
    }
}
