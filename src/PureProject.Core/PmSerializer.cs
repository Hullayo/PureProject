using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PureProject.Core;

/// <summary>The single import/export boundary for .pm files, legacy arrays and backup bundles.</summary>
public static partial class PmSerializer
{
    public const int CurrentSchema = 4;
    public const int MaxCharacters = 32 * 1024 * 1024;
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        MaxDepth = 128
    };
    private static readonly string[] ContentKeys = ["tasks", "task_groups", "tags", "changelog", "milestones", "flowchart"];
    private static readonly HashSet<string> EnvelopeKeys = ["version", "schema_version", "project", "tasks", "task_groups", "tags", "changelog", "milestones", "flowchart", "template", "readme_file", "readme", "storage"];

    public static Project ParsePm(string text)
    {
        var projects = ParseProjects(text);
        if (projects.Count != 1) throw Error("projects", "请提供恰好一个项目");
        return projects[0];
    }

    /// <summary>Parses all entries before returning; a bad entry never causes a partial restore.</summary>
    public static IReadOnlyList<Project> ParseProjects(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw Error("", "文件为空");
        if (text.Length > MaxCharacters) throw Error("", "文件超过 32 MB 字符上限");
        try
        {
            var node = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { MaxDepth = 128, AllowDuplicateProperties = false });
            var root = node as JsonObject;
            if (root is not null) CheckVersion(root);
            JsonArray? bundle = node as JsonArray;
            int? inheritedSchema = null;
            if (root?.ContainsKey("projects") == true)
            {
                var kind = Text(root["kind"]);
                if (kind is not null && kind is not "pureproject-backup" and not "projectmanager-backup")
                    throw Error("kind", $"无法识别备份类型 {kind}");
                bundle = root["projects"] as JsonArray ?? throw Error("projects", "应为数组");
                inheritedSchema = root.ContainsKey("schema_version") ? Version(root) : null;
            }
            if (bundle?.Count > 2000) throw Error("projects", "项目数超过 2000");
            var items = bundle is null ? new JsonNode?[] { node } : bundle.ToArray();
            var projects = new List<Project>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < items.Length; index++)
            {
                if (items[index] is not JsonObject item) throw Error($"projects[{index}]", "应为项目对象");
                var project = ParseObject((JsonObject)item.DeepClone(), inheritedSchema);
                if (!ids.Add(project.Id)) throw Error($"projects[{index}].id", "项目 ID 重复");
                projects.Add(project);
            }
            return projects;
        }
        catch (JsonException ex) { throw Error(ex.Path ?? "", $"JSON 格式或字段类型错误：{ex.Message}"); }
        catch (InvalidOperationException ex) { throw Error("", $"字段类型错误：{ex.Message}"); }
    }

    public static string ExportPm(Project project)
    {
        EnsureValid(project);
        return ToPmNode(project).ToJsonString(Options);
    }

    public static string ExportBackup(IEnumerable<Project> projects)
    {
        var array = new JsonArray();
        foreach (var project in projects) { EnsureValid(project); array.Add(ToPmNode(project)); }
        return new JsonObject
        {
            ["kind"] = "pureproject-backup", ["schema_version"] = CurrentSchema,
            ["app_version"] = "2.0.0", ["exported_at"] = DateTimeOffset.UtcNow.ToString("O"),
            ["projects"] = array, ["settings"] = new JsonObject(), ["sync"] = new JsonObject()
        }.ToJsonString(Options);
    }

    public static string ExportInternal(IEnumerable<Project> projects)
    {
        var array = new JsonArray();
        foreach (var project in projects)
        {
            EnsureValid(project);
            var item = JsonSerializer.SerializeToNode(project, Options)!.AsObject();
            item.Remove("storage");
            array.Add(item);
        }
        return new JsonObject { ["schema_version"] = CurrentSchema, ["projects"] = array }.ToJsonString(Options);
    }

    public static Project Clone(Project project) => Copy(project);
    public static ProjectTask CloneTask(ProjectTask task) => Copy(task);
    internal static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;

    private static JsonObject ToPmNode(Project project)
    {
        var metadata = JsonSerializer.SerializeToNode(project, Options)!.AsObject();
        var root = new JsonObject();
        foreach (var pair in project.PmExtensions) if (!EnvelopeKeys.Contains(pair.Key)) root[pair.Key] = JsonNode.Parse(pair.Value.GetRawText());
        root["version"] = "1.0";
        root["schema_version"] = CurrentSchema;
        foreach (var key in ContentKeys)
        {
            root[key] = metadata[key]?.DeepClone();
            metadata.Remove(key);
        }
        foreach (var key in new[] { "legacy_template", "legacy_readme_file", "readme", "_pm_extensions", "storage", "schema_version" }) metadata.Remove(key);
        metadata["sync_enabled"] = true;
        root["project"] = metadata;
        if (project.LegacyTemplate is not null)
        {
            root["template"] = JsonSerializer.SerializeToNode(project.LegacyTemplate, Options);
            if (project.LegacyReadmeFile is not null) root["readme_file"] = project.LegacyReadmeFile;
            // Retain the original template while also preserving separately edited README text.
            var file = project.LegacyReadmeFile ?? "README.md";
            if (project.Readme != project.LegacyTemplate.FileContents.GetValueOrDefault(file, "")) root["readme"] = project.Readme;
        }
        else if (project.Readme.Length > 0)
        {
            var file = project.LegacyReadmeFile ?? "README.md";
            root["readme_file"] = file;
            root["template"] = JsonSerializer.SerializeToNode(new LegacyTemplate { Files = [file], FileContents = new() { [file] = project.Readme } }, Options);
        }
        return root;
    }

    private static Project ParseObject(JsonObject raw, int? inheritedSchema)
    {
        CheckVersion(raw);
        var isPm = raw.ContainsKey("project");
        JsonObject pm;
        if (isPm) pm = raw;
        else
        {
            // Internal Project objects store metadata and content together.
            pm = new JsonObject { ["version"] = "1.0", ["schema_version"] = raw["schema_version"]?.DeepClone() ?? JsonValue.Create(inheritedSchema ?? (raw["task_groups"] is JsonArray ? 4 : 1)) };
            foreach (var key in ContentKeys) { if (raw.ContainsKey(key)) pm[key] = raw[key]?.DeepClone(); raw.Remove(key); }
            if (raw["legacy_template"] is not null) pm["template"] = raw["legacy_template"]!.DeepClone();
            if (raw["legacy_readme_file"] is not null) pm["readme_file"] = raw["legacy_readme_file"]!.DeepClone();
            if (raw["readme"] is not null) pm["readme"] = raw["readme"]!.DeepClone();
            foreach (var key in new[] { "schema_version", "legacy_template", "legacy_readme_file", "readme" }) raw.Remove(key);
            pm["project"] = raw;
        }
        if (pm["project"] is not JsonObject metadata) throw Error("project", "缺失项目元数据对象");
        if (string.IsNullOrWhiteSpace(Text(pm["version"]))) throw Error("version", "缺失版本字符串");
        var schema = Version(pm);
        if (schema < 4) Migrate(pm, metadata);
        ValidateStructure(pm);
        var model = (JsonObject)metadata.DeepClone();
        foreach (var key in ContentKeys) if (pm.ContainsKey(key)) model[key] = pm[key]?.DeepClone();
        if (pm.ContainsKey("template")) model["legacy_template"] = pm["template"]?.DeepClone();
        if (pm.ContainsKey("readme_file")) model["legacy_readme_file"] = pm["readme_file"]?.DeepClone();
        model.Remove("storage");
        model.Remove("schema_version");
        var project = model.Deserialize<Project>(Options) ?? throw Error("project", "无法读取项目");
        project.Id = string.IsNullOrWhiteSpace(project.Id) ? StableId("project", $"{project.Name}:{project.CreatedAt}") : project.Id;
        project.Readme = Text(pm["readme"]) ?? project.LegacyTemplate?.FileContents.GetValueOrDefault(project.LegacyReadmeFile ?? "README.md", "") ?? "";
        foreach (var pair in pm)
            if (!EnvelopeKeys.Contains(pair.Key)) project.PmExtensions[pair.Key] = JsonSerializer.SerializeToElement(pair.Value, Options);
        project.Extra.Remove("storage");
        EnsureValid(project);
        return project;
    }

    private static void Migrate(JsonObject pm, JsonObject metadata)
    {
        var schema = Version(pm);
        if (schema <= 1)
        {
            foreach (var key in new[] { "tasks", "tags", "milestones", "changelog" }) if (pm[key] is null) pm[key] = new JsonArray();
            if (pm["tasks"] is JsonArray tasks)
                foreach (var task in tasks.OfType<JsonObject>())
                {
                    foreach (var key in new[] { "tags", "dependencies", "subtasks", "comments" }) if (task[key] is null) task[key] = new JsonArray();
                    if (task["description"] is null) task["description"] = "";
                    foreach (var key in new[] { "due_date", "due_time", "start_offset", "tracked_start", "reminder" }) if (!task.ContainsKey(key)) task[key] = null;
                }
        }
        if (schema <= 2 && pm["tasks"] is JsonArray legacyTasks)
            foreach (var task in legacyTasks.OfType<JsonObject>()) if (!task.ContainsKey("recurrence")) task["recurrence"] = null;

        var keyId = Text(metadata["id"]);
        if (string.IsNullOrWhiteSpace(keyId)) keyId = StableId("project", $"{Text(metadata["name"])}:{Text(metadata["created_at"])}");
        var values = new List<string>();
        if (metadata["kanban_columns"] is JsonArray columns) values.AddRange(columns.Select(Text).OfType<string>().Where(s => !string.IsNullOrWhiteSpace(s)));
        if (pm["tasks"] is JsonArray originalTasks) values.AddRange(originalTasks.OfType<JsonObject>().Select(t => Text(t["status"])).OfType<string>().Where(s => !string.IsNullOrWhiteSpace(s)));
        var group = CreateLegacyGroup(keyId, values);
        if (pm["tasks"] is JsonArray taskArray)
            foreach (var task in taskArray.OfType<JsonObject>())
            {
                var legacyStatus = Text(task["status"]) ?? "todo";
                var status = group.Statuses.FirstOrDefault(s => s.Id == StableId("status", $"v4:status:{keyId}:{legacyStatus}"))
                    ?? group.Statuses.FirstOrDefault(s => s.Category == LegacyCategory(legacyStatus)) ?? group.Statuses[0];
                // Keep the legacy status extension for round trips; business logic uses status_id.
                task["task_group_id"] = group.Id;
                task["status_id"] = status.Id;
                task["completed_at"] = status.Category == "done" ? Text(task["completed_at"]) ?? Text(task["updated_at"]) ?? Text(task["created_at"]) ?? Text(metadata["updated_at"]) ?? Text(metadata["created_at"]) ?? "1970-01-01T00:00:00.000Z" : null;
            }
        pm["task_groups"] = JsonSerializer.SerializeToNode(new[] { group }, Options);
        metadata["default_task_group_id"] = group.Id;
        pm["schema_version"] = CurrentSchema;
    }

    internal static TaskGroup CreateLegacyGroup(string projectKey, IEnumerable<string>? values = null)
    {
        var raw = values?.Distinct(StringComparer.Ordinal).ToList() ?? [];
        if (raw.Count == 0) raw = ["todo", "in_progress", "done"];
        if (!raw.Any(v => LegacyCategory(v) == "done")) raw.Add("done");
        var statuses = raw.Select((value, index) => new TaskStatusDefinition
        {
            Id = StableId("status", $"v4:status:{projectKey}:{value}"),
            Name = value switch { "todo" => "待办", "in_progress" => "进行中", "done" => "已完成", "cancelled" => "已取消", _ => value.Trim() },
            Category = LegacyCategory(value), Color = CategoryColor(LegacyCategory(value)), SortOrder = (index + 1) * 1024
        }).ToList();
        return new TaskGroup
        {
            Id = StableId("group", $"v4:group:{projectKey}:default"), Name = "默认任务组", SortOrder = 1024,
            Statuses = statuses, InitialStatusId = (statuses.FirstOrDefault(s => s.Category == "todo") ?? statuses.FirstOrDefault(s => s.Category == "active") ?? statuses[0]).Id,
            CompletionStatusId = statuses.First(s => s.Category == "done").Id
        };
    }

    public static string StableId(string prefix, string value)
    {
        uint hash = 0x811c9dc5;
        foreach (var character in value) { hash ^= character; hash = unchecked(hash * 0x01000193); }
        return $"{prefix}-{hash:x8}";
    }
    public static string CategoryColor(string category) => category switch { "active" => "#4f46e5", "done" => "#10b981", "cancelled" => "#9ca3af", _ => "#6b7280" };
    private static string LegacyCategory(string text)
    {
        var value = new string(text.Trim().ToLowerInvariant().Where(c => !char.IsWhiteSpace(c) && c is not '_' and not '-').ToArray());
        return value switch
        {
            "done" or "complete" or "completed" or "finished" or "已完成" or "完成" or "已关闭" or "关闭" => "done",
            "cancelled" or "canceled" or "cancel" or "已取消" or "取消" => "cancelled",
            "inprogress" or "active" or "doing" or "进行中" or "处理中" or "开发中" or "修复中" or "评审中" => "active",
            _ => "todo"
        };
    }
    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static int Version(JsonObject node)
    {
        if (!node.TryGetPropertyValue("schema_version", out var version) || version is null) return 1;
        if (version is JsonValue value && (value.TryGetValue<int>(out var number) || int.TryParse(Text(value), NumberStyles.None, CultureInfo.InvariantCulture, out number)) && number >= 1) return number;
        throw Error("schema_version", "应为正整数");
    }
    private static void CheckVersion(JsonObject node) { if (Version(node) > CurrentSchema) throw Error("schema_version", $"项目使用未来版本，当前仅支持 schema v{CurrentSchema}"); }
    private static PmValidationException Error(string path, string message) => new([new ValidationIssue(path, message)]);
    public static void EnsureValid(Project project)
    {
        var issues = Validate(project);
        if (issues.Count > 0) throw new PmValidationException(issues);
    }

    private static void ValidateStructure(JsonObject pm)
    {
        var issues = new List<ValidationIssue>();
        void RequiredText(JsonObject owner, string field, string path)
        {
            if (string.IsNullOrWhiteSpace(Text(owner[field]))) issues.Add(new(path, "缺失或不是非空字符串"));
        }
        void Array(JsonObject owner, string field, string path, int limit, bool required = false)
        {
            if (!owner.ContainsKey(field) && !required) return;
            if (owner[field] is not JsonArray array) { issues.Add(new(path, "应为数组")); return; }
            if (array.Count > limit) issues.Add(new(path, $"超过 {limit} 个元素"));
            if (array.Any(item => item is null)) issues.Add(new(path, "数组不能包含 null"));
        }
        Array(pm, "tasks", "tasks", 20000, true);
        Array(pm, "task_groups", "task_groups", 2000, true);
        Array(pm, "tags", "tags", 5000);
        Array(pm, "milestones", "milestones", 2000);
        Array(pm, "changelog", "changelog", 20000);
        if (pm["project"] is JsonObject metadata)
        {
            RequiredText(metadata, "name", "project.name");
            RequiredText(metadata, "default_task_group_id", "project.default_task_group_id");
        }
        if (pm["tasks"] is JsonArray tasks)
            for (var i = 0; i < tasks.Count; i++)
            {
                if (tasks[i] is not JsonObject task) { issues.Add(new($"tasks[{i}]", "应为对象")); continue; }
                foreach (var key in new[] { "id", "title", "task_group_id", "status_id" }) RequiredText(task, key, $"tasks[{i}].{key}");
                foreach (var (field, limit) in new[] { ("dependencies", 500), ("subtasks", 2000), ("comments", 5000), ("tags", 200) }) Array(task, field, $"tasks[{i}].{field}", limit);
                if (task["recurrence"] is JsonObject recurrence && string.IsNullOrWhiteSpace(Text(recurrence["freq"]))) issues.Add(new($"tasks[{i}].recurrence.freq", "缺少循环频率"));
                if (task["dependencies"] is JsonArray dependencies)
                    for (var d = 0; d < dependencies.Count; d++)
                    {
                        if (dependencies[d] is not JsonObject dependency) issues.Add(new($"tasks[{i}].dependencies[{d}]", "应为依赖对象"));
                        else RequiredText(dependency, "taskId", $"tasks[{i}].dependencies[{d}].taskId");
                    }
            }
        if (pm["task_groups"] is JsonArray groups)
            for (var i = 0; i < groups.Count; i++)
            {
                if (groups[i] is not JsonObject group) { issues.Add(new($"task_groups[{i}]", "应为对象")); continue; }
                foreach (var key in new[] { "id", "name", "initial_status_id", "completion_status_id" }) RequiredText(group, key, $"task_groups[{i}].{key}");
                Array(group, "statuses", $"task_groups[{i}].statuses", 500, true);
                if (group["statuses"] is JsonArray statuses)
                    for (var s = 0; s < statuses.Count; s++)
                    {
                        if (statuses[s] is not JsonObject status) issues.Add(new($"task_groups[{i}].statuses[{s}]", "应为状态对象"));
                        else foreach (var key in new[] { "id", "name", "color", "category" }) RequiredText(status, key, $"task_groups[{i}].statuses[{s}].{key}");
                    }
            }
        var queue = new Queue<(JsonNode? Node, string Path)>();
        queue.Enqueue((pm, ""));
        while (queue.TryDequeue(out var entry))
        {
            if (entry.Node is JsonObject obj) foreach (var item in obj) queue.Enqueue((item.Value, entry.Path + "." + item.Key));
            else if (entry.Node is JsonArray list) for (var i = 0; i < list.Count; i++) queue.Enqueue((list[i], $"{entry.Path}[{i}]"));
            else if (Text(entry.Node) is string value && value.Length > 200000) issues.Add(new(entry.Path, "字符串超过 200000 字符"));
        }
        if (issues.Count > 0) throw new PmValidationException(issues);
    }

    public static IReadOnlyList<ValidationIssue> Validate(Project project)
    {
        var issues = new List<ValidationIssue>();
        void Required(string? value, string path, int max = 200) { if (string.IsNullOrWhiteSpace(value) || value.Length > max) issues.Add(new(path, $"必须为 1–{max} 字符")); }
        Required(project.Id, "project.id"); Required(project.Name, "project.name", 500);
        if (project.Tasks is null || project.TaskGroups is null || project.Tags is null || project.Milestones is null || project.Changelog is null)
        { issues.Add(new("project", "项目数组不能为 null")); return issues; }
        // Validate runtime-created models too; do not allow malformed null values to reach dictionaries or the UI.
        if (project.Description is null || project.Color is null || project.Readme is null || project.Template is null || project.CreatedAt is null || project.UpdatedAt is null)
            issues.Add(new("project", "项目文本字段不能为 null"));
        if (project.Tasks.Any(t => t is null) || project.TaskGroups.Any(g => g is null) || project.Tags.Any(t => t is null) || project.Milestones.Any(m => m is null) || project.Changelog.Any(c => c is null))
        { issues.Add(new("project", "项目数组不能包含 null")); return issues; }
        if (project.Tasks.Count > 20000) issues.Add(new("tasks", "任务数超过 20000"));
        var groupIds = new HashSet<string>(); var statusIds = new HashSet<string>();
        var statuses = new Dictionary<string, (string Group, string Category)>();
        foreach (var group in project.TaskGroups)
        {
            Required(group.Id, "task_groups.id"); Required(group.Name, $"task_groups[{group.Id}].name", 500);
            if (group.Id is null) continue;
            if (!groupIds.Add(group.Id)) issues.Add(new("task_groups.id", "任务组 ID 重复"));
            if (group.Statuses is null || group.Statuses.Count == 0) { issues.Add(new($"task_groups[{group.Id}].statuses", "至少需要一个状态")); continue; }
            foreach (var status in group.Statuses)
            {
                if (status is null) { issues.Add(new("statuses", "状态不能为 null")); continue; }
                Required(status.Id, "statuses.id"); Required(status.Name, $"statuses[{status.Id}].name", 500); Required(status.Color, $"statuses[{status.Id}].color", 64);
                if (status.Id is null) continue;
                if (!statusIds.Add(status.Id)) issues.Add(new("statuses.id", "状态 ID 在项目内重复"));
                if (status.Category is not "todo" and not "active" and not "done" and not "cancelled") issues.Add(new($"statuses[{status.Id}].category", "类别必须为 todo/active/done/cancelled"));
                statuses[status.Id] = (group.Id, status.Category);
            }
            if (!group.Statuses.Any(s => s is not null && s.Id == group.InitialStatusId)) issues.Add(new($"task_groups[{group.Id}].initial_status_id", "初始状态不属于任务组"));
            if (!group.Statuses.Any(s => s is not null && s.Id == group.CompletionStatusId && s.Category == "done")) issues.Add(new($"task_groups[{group.Id}].completion_status_id", "完成状态必须属于任务组且类别为 done"));
        }
        if (!project.TaskGroups.Any(g => g.Id == project.DefaultTaskGroupId && !g.Archived)) issues.Add(new("project.default_task_group_id", "默认任务组不存在或已归档"));
        var taskIds = new HashSet<string>();
        foreach (var task in project.Tasks)
        {
            Required(task.Id, "tasks.id"); Required(task.Title, $"tasks[{task.Id}].title", 2000);
            if (task.Id is null || task.StatusId is null || task.TaskGroupId is null)
            { issues.Add(new("tasks", "任务 ID、任务组和状态引用不能为 null")); continue; }
            if (!taskIds.Add(task.Id)) issues.Add(new("tasks.id", "任务 ID 重复"));
            if (!groupIds.Contains(task.TaskGroupId)) issues.Add(new($"tasks[{task.Id}].task_group_id", "任务组不存在"));
            if (!statuses.TryGetValue(task.StatusId, out var status) || status.Group != task.TaskGroupId) issues.Add(new($"tasks[{task.Id}].status_id", "状态不属于任务组"));
            if (status.Category == "done" ? string.IsNullOrWhiteSpace(task.CompletedAt) : task.CompletedAt is not null) issues.Add(new($"tasks[{task.Id}].completed_at", "仅 done 类别任务必须有完成时间，其余必须为 null"));
            if (task.Priority is not "high" and not "medium" and not "low") issues.Add(new($"tasks[{task.Id}].priority", "优先级必须为 high/medium/low"));
            if (task.Dependencies is null || task.Tags is null || task.Subtasks is null || task.Comments is null) issues.Add(new($"tasks[{task.Id}]", "任务数组不能为 null"));
            if (task.Description is null || task.CreatedAt is null || task.UpdatedAt is null) issues.Add(new($"tasks[{task.Id}]", "任务文本字段不能为 null"));
            if (task.Dependencies?.Any(d => d is null || string.IsNullOrWhiteSpace(d.TaskId) || !double.IsFinite(d.DayOffset)) == true) issues.Add(new($"tasks[{task.Id}].dependencies", "依赖必须包含目标 ID 和有限天数"));
            if (task.Subtasks?.Any(s => s is null || string.IsNullOrWhiteSpace(s.Id) || string.IsNullOrWhiteSpace(s.Title)) == true) issues.Add(new($"tasks[{task.Id}].subtasks", "子任务缺少 ID 或标题"));
            if (task.Comments?.Any(c => c is null || string.IsNullOrWhiteSpace(c.Id) || c.Content is null) == true) issues.Add(new($"tasks[{task.Id}].comments", "评论缺少 ID 或正文"));
            if (task.Recurrence is { } rule)
            {
                if (rule.Freq is not "daily" and not "weekly" and not "monthly" and not "yearly") issues.Add(new($"tasks[{task.Id}].recurrence.freq", "循环频率非法"));
                if (rule.Interval < 1 || rule.Count < 0 || rule.End is not "never" and not "count" and not "until" || rule.ByWeekday?.Any(d => d < 0 || d > 6) == true) issues.Add(new($"tasks[{task.Id}].recurrence", "循环间隔、次数或星期范围非法"));
                if (rule.End == "until" && !DateOnly.TryParseExact(rule.Until, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) issues.Add(new($"tasks[{task.Id}].recurrence.until", "截止日期必须为 YYYY-MM-DD"));
            }
        }
        foreach (var task in project.Tasks)
            foreach (var dependency in task.Dependencies ?? [])
                if (dependency is not null && !taskIds.Contains(dependency.TaskId)) issues.Add(new($"tasks[{task.Id}].dependencies", $"依赖目标不存在：{dependency.TaskId}"));
        if (issues.Count == 0 && HasDependencyCycle(project)) issues.Add(new("tasks.dependencies", "任务依赖不能形成循环"));
        return issues;
    }

    public static bool HasDependencyCycle(Project project)
    {
        var knownIds = project.Tasks.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var nodes = project.Tasks.ToDictionary(t => t.Id, t => t.Dependencies?.Select(d => d.TaskId).Where(knownIds.Contains).Distinct().ToList() ?? []);
        var degree = nodes.ToDictionary(p => p.Key, p => p.Value.Count);
        var reverse = nodes.Keys.ToDictionary(id => id, _ => new List<string>());
        foreach (var pair in nodes) foreach (var dependency in pair.Value) reverse[dependency].Add(pair.Key);
        var ready = new Queue<string>(degree.Where(p => p.Value == 0).Select(p => p.Key));
        var seen = 0;
        while (ready.TryDequeue(out var id)) { seen++; foreach (var next in reverse[id]) if (--degree[next] == 0) ready.Enqueue(next); }
        return seen != nodes.Count;
    }
}
