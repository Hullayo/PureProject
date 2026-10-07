using System.Text.Json;
using System.Text.Json.Serialization;

namespace PureProject.Core;

/// <summary>Unknown properties survive import/export, including plugin data.</summary>
public abstract class ExtensibleModel
{
    [JsonExtensionData] public Dictionary<string, JsonElement> Extra { get; set; } = [];
}

public sealed class Project : ExtensibleModel
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("color")] public string Color { get; set; } = "#4f46e5";
    [JsonPropertyName("template")] public string Template { get; set; } = "default";
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = "";
    [JsonPropertyName("updated_at")] public string UpdatedAt { get; set; } = "";
    [JsonPropertyName("archived")] public bool Archived { get; set; }
    [JsonPropertyName("start_date")] public string? StartDate { get; set; }
    [JsonPropertyName("end_date")] public string? EndDate { get; set; }
    [JsonPropertyName("tasks")] public List<ProjectTask> Tasks { get; set; } = [];
    [JsonPropertyName("task_groups")] public List<TaskGroup> TaskGroups { get; set; } = [];
    [JsonPropertyName("default_task_group_id")] public string DefaultTaskGroupId { get; set; } = "";
    [JsonPropertyName("tags")] public List<Tag> Tags { get; set; } = [];
    [JsonPropertyName("changelog")] public List<ChangelogEntry> Changelog { get; set; } = [];
    [JsonPropertyName("milestones")] public List<Milestone> Milestones { get; set; } = [];
    [JsonPropertyName("readme")] public string Readme { get; set; } = "";
    [JsonPropertyName("legacy_template")] public LegacyTemplate? LegacyTemplate { get; set; }
    [JsonPropertyName("legacy_readme_file")] public string? LegacyReadmeFile { get; set; }
    [JsonPropertyName("fileTree")] public JsonElement? FileTree { get; set; }
    [JsonPropertyName("kanban_columns")] public List<string>? KanbanColumns { get; set; }
    [JsonPropertyName("sync_enabled")] public bool SyncEnabled { get; set; } = true;
    [JsonPropertyName("sort_order")] public double SortOrder { get; set; }
    [JsonPropertyName("flowchart")] public JsonElement? Flowchart { get; set; }
    // The .pm envelope and its project metadata have separate extension namespaces.
    [JsonPropertyName("_pm_extensions")] public Dictionary<string, JsonElement> PmExtensions { get; set; } = [];

    // Transaction validation is read-only for shared metadata and untouched tasks.
    // The caller must deep-clone its edited task before placing it in this list.
    internal Project CopyForTaskEdit()
    {
        var copy = (Project)MemberwiseClone();
        copy.Tasks = Tasks.ToList();
        return copy;
    }
}

public sealed class TaskGroup : ExtensibleModel
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("sort_order")] public double SortOrder { get; set; }
    [JsonPropertyName("archived")] public bool Archived { get; set; }
    [JsonPropertyName("initial_status_id")] public string InitialStatusId { get; set; } = "";
    [JsonPropertyName("completion_status_id")] public string CompletionStatusId { get; set; } = "";
    [JsonPropertyName("statuses")] public List<TaskStatusDefinition> Statuses { get; set; } = [];
}

public sealed class TaskStatusDefinition : ExtensibleModel
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("color")] public string Color { get; set; } = "#6b7280";
    [JsonPropertyName("category")] public string Category { get; set; } = "todo";
    [JsonPropertyName("sort_order")] public double SortOrder { get; set; }
}

public sealed class ProjectTask : ExtensibleModel
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("color")] public string? Color { get; set; }
    [JsonPropertyName("task_group_id")] public string TaskGroupId { get; set; } = "";
    [JsonPropertyName("status_id")] public string StatusId { get; set; } = "";
    [JsonPropertyName("completed_at")] public string? CompletedAt { get; set; }
    [JsonPropertyName("status_before_closed_id")] public string? StatusBeforeClosedId { get; set; }
    [JsonPropertyName("priority")] public string Priority { get; set; } = "medium";
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = [];
    [JsonPropertyName("due_date")] public string? DueDate { get; set; }
    [JsonPropertyName("due_time")] public string? DueTime { get; set; }
    [JsonPropertyName("start_offset")] public double? StartOffset { get; set; }
    [JsonPropertyName("dependencies")] public List<Dependency> Dependencies { get; set; } = [];
    [JsonPropertyName("subtasks")] public List<Subtask> Subtasks { get; set; } = [];
    [JsonPropertyName("comments")] public List<TaskComment> Comments { get; set; } = [];
    [JsonPropertyName("tracked_start")] public string? TrackedStart { get; set; }
    [JsonPropertyName("reminder")] public string? Reminder { get; set; }
    [JsonPropertyName("git_repo_path")] public string? GitRepoPath { get; set; }
    [JsonPropertyName("recurrence")] public RecurrenceRule? Recurrence { get; set; }
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = "";
    [JsonPropertyName("updated_at")] public string UpdatedAt { get; set; } = "";
}

public sealed class RecurrenceRule : ExtensibleModel
{
    [JsonPropertyName("freq")] public string Freq { get; set; } = "daily";
    [JsonPropertyName("interval")] public int Interval { get; set; } = 1;
    [JsonPropertyName("byWeekday")] public List<int>? ByWeekday { get; set; }
    [JsonPropertyName("end")] public string End { get; set; } = "never";
    [JsonPropertyName("count")] public int? Count { get; set; }
    [JsonPropertyName("until")] public string? Until { get; set; }
    [JsonPropertyName("sourceTaskId")] public string? SourceTaskId { get; set; }
}

public sealed class Dependency : ExtensibleModel
{
    [JsonPropertyName("taskId")] public string TaskId { get; set; } = "";
    [JsonPropertyName("dayOffset")] public double DayOffset { get; set; }
}
public sealed class Subtask : ExtensibleModel
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("done")] public bool Done { get; set; }
}
public sealed class TaskComment : ExtensibleModel
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("content")] public string Content { get; set; } = "";
    [JsonPropertyName("created_at")] public string CreatedAt { get; set; } = "";
}
public sealed class Tag : ExtensibleModel
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("color")] public string Color { get; set; } = "#4f46e5";
}
public sealed class ChangelogEntry : ExtensibleModel
{
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("date")] public string Date { get; set; } = "";
    [JsonPropertyName("info")] public string Info { get; set; } = "";
    [JsonPropertyName("auto")] public bool? Auto { get; set; }
}
public sealed class Milestone : ExtensibleModel
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("date")] public string Date { get; set; } = "";
    [JsonPropertyName("color")] public string Color { get; set; } = "#4f46e5";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
}
public sealed class LegacyTemplate : ExtensibleModel
{
    [JsonPropertyName("dirs")] public List<string> Dirs { get; set; } = [];
    [JsonPropertyName("files")] public List<string> Files { get; set; } = [];
    [JsonPropertyName("file_contents")] public Dictionary<string, string> FileContents { get; set; } = [];
}

public sealed record ValidationIssue(string Path, string Message)
{
    public override string ToString() => string.IsNullOrEmpty(Path) ? Message : $"{Path}：{Message}";
}
public sealed class PmValidationException : Exception
{
    public IReadOnlyList<ValidationIssue> Issues { get; }
    public PmValidationException(IEnumerable<ValidationIssue> issues) : this(issues.ToArray()) { }
    private PmValidationException(ValidationIssue[] issues) : base(string.Join("；", issues.Take(5))) => Issues = issues;
}

public sealed record ProjectStatistics(int Total, int Todo, int Active, int Completed, int Cancelled, int Overdue, int DueToday, int HighPriority, double CompletionRate);
