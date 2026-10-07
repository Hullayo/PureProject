using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using PureProject.Core;
using PureProject.Infrastructure;

const string FixtureKind = "pureproject-scale-audit";
const string IdPrefix = "scale-";
var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    WriteIndented = true,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
};

try
{
    var workspace = FindRepositoryDirectory();
    string? output = null;
    var profile = "20k";
    var persistenceCheck = false;
    var referenceDate = DateOnly.FromDateTime(DateTime.Now);
    var verifyOnly = false;
    var replace = false;
    var benchmark = false;
    var iterations = 5;
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--output" when i + 1 < args.Length: output = args[++i]; break;
            case "--profile" when i + 1 < args.Length: profile = args[++i]; break;
            case "--persistence-check": persistenceCheck = true; break;
            case "--date" when i + 1 < args.Length:
                referenceDate = DateOnly.ParseExact(args[++i], "yyyy-MM-dd", CultureInfo.InvariantCulture); break;
            case "--verify": verifyOnly = true; break;
            case "--replace": replace = true; break;
            case "--benchmark": benchmark = true; break;
            case "--iterations" when i + 1 < args.Length:
                iterations = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
            case "--help":
                Console.WriteLine("ScaleFixture [--profile 20k|100k] [--date yyyy-MM-dd] [--output path] [--verify | --replace | --benchmark] [--persistence-check] [--iterations 1..10]");
                Console.WriteLine("Outputs must stay below the matching artifacts/scale-audit or scale-100k directory. Existing data is never replaced without --replace and a valid synthetic marker.");
                return 0;
            default: throw new ArgumentException("Unknown or incomplete argument: " + args[i]);
        }
    }
    if (verifyOnly && replace) throw new ArgumentException("--verify and --replace cannot be combined.");
    if (benchmark && (replace || verifyOnly)) throw new ArgumentException("--benchmark cannot be combined with --verify or --replace.");
    if (iterations is < 1 or > 10) throw new ArgumentException("--iterations must be between 1 and 10.");
    var tasksPerStatus = profile switch { "20k" => 20, "100k" => 100, _ => throw new ArgumentException("--profile must be 20k or 100k.") };
    if (benchmark && (profile != "20k" || persistenceCheck)) throw new ArgumentException("The legacy CPU benchmark only supports 20k; use --persistence-check for storage verification with either profile.");
    var artifactRoot = Path.Combine(workspace, "artifacts", profile == "20k" ? "scale-audit" : "scale-100k");
    output ??= Path.Combine(artifactRoot, "data");
    output = Path.GetFullPath(output);
    var allowedRoot = Path.GetFullPath(artifactRoot) + Path.DirectorySeparatorChar;
    if (!output.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase)
        || Path.GetFullPath(JsonProjectRepository.DefaultDataDirectory).Equals(output, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("The output must be a dedicated subdirectory of the selected scale profile's artifact directory.");

    var markerPath = Path.Combine(output, "scale-fixture.json");
    var projectsPath = Path.Combine(output, "projects.json");
    if (benchmark)
    {
        VerifyMarker(markerPath, output, tasksPerStatus);
        var reportPath = Path.Combine(workspace, "artifacts", "scale-audit", "serialization-benchmark.json");
        await SerializationBenchmark.RunAsync(output, reportPath, iterations);
        return 0;
    }
    var existing = File.Exists(projectsPath) || File.Exists(markerPath) || File.Exists(Path.Combine(output, "settings.json"));
    if (existing)
    {
        VerifyMarker(markerPath, output, tasksPerStatus);
        if (!replace) verifyOnly = true;
    }
    if (verifyOnly)
    {
        VerifyMarker(markerPath, output, tasksPerStatus);
        using var repository = new JsonProjectRepository(output);
        var reloaded = await repository.LoadAsync();
        var counts = Verify(reloaded, tasksPerStatus);
        var settings = await new SettingsRepository(output).LoadAsync();
        Console.WriteLine(JsonSerializer.Serialize(new { action = "verified-existing", output, counts, theme = settings.Theme }, jsonOptions));
        Console.WriteLine("Existing project data and settings were preserved.");
        if (persistenceCheck) await PersistenceCheck.RunAsync(output, artifactRoot, reloaded);
        return 0;
    }

    var projects = BuildProjects(referenceDate, tasksPerStatus);
    var expected = Verify(projects, tasksPerStatus);
    var expectedHash = await HashModelsAsync(projects);
    Console.WriteLine($"Validated {expected.Tasks:N0} tasks before writing with the streaming internal format.");

    // A marker is written last, after both repositories have persisted and reloaded.
    // The repository's exclusive lock also prevents replacing an open audit session.
    using (var repository = new JsonProjectRepository(output))
    {
        _ = await repository.LoadAsync();
        await repository.SaveAsync(projects);
    }
    await new SettingsRepository(output).SaveAsync(new AppSettings { Theme = "Dark" });
    List<Project> verified;
    using (var repository = new JsonProjectRepository(output)) verified = await repository.LoadAsync();
    var verifiedCounts = Verify(verified, tasksPerStatus);
    var verifiedSettings = await new SettingsRepository(output).LoadAsync();
    if (verifiedSettings.Theme != "Dark" || verifiedSettings.SyncServerUrl.Length != 0 || verifiedSettings.SyncToken.Length != 0)
        throw new InvalidDataException("The isolated fixture settings did not reload as expected.");
    var hash = await HashFileAsync(projectsPath);
    if (!StringComparer.Ordinal.Equals(expectedHash, hash) || !StringComparer.Ordinal.Equals(expectedHash, await HashModelsAsync(verified)))
        throw new InvalidDataException("Repository round-trip changed the deterministic fixture.");
    var marker = new
    {
        schemaVersion = 1,
        fixtureKind = FixtureKind,
        synthetic = true,
        idPrefix = IdPrefix,
        profile,
        generatedAtUtc = DateTimeOffset.UtcNow,
        referenceDate = referenceDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        dataDirectory = output,
        dimensions = new { projects = 10, groupsPerProject = 10, statusesPerGroup = 10, tasksPerStatus },
        expectedCounts = expected,
        verifiedCounts,
        initialProjectsSha256 = hash,
        projectsBytes = new FileInfo(projectsPath).Length,
        features = new
        {
            longTitles = verified.Sum(p => p.Tasks.Count(t => t.Title.Length >= 80)),
            dependencies = verified.Sum(p => p.Tasks.Sum(t => t.Dependencies.Count)),
            subtasks = verified.Sum(p => p.Tasks.Sum(t => t.Subtasks.Count)),
            comments = verified.Sum(p => p.Tasks.Sum(t => t.Comments.Count)),
            tags = verified.Sum(p => p.Tags.Count),
            milestones = verified.Sum(p => p.Milestones.Count),
            tasksWithDueDates = verified.Sum(p => p.Tasks.Count(t => t.DueDate is not null))
        },
        validation = new { serializer = "PmSerializer.EnsureValid/ReadInternalAsync/WriteInternalAsync", repository = "JsonProjectRepository.SaveAsync/LoadAsync", settings = "SettingsRepository.SaveAsync/LoadAsync", exactRoundTrip = true, comparison = "SHA256 of canonical compact internal serialization" }
    };
    await File.WriteAllTextAsync(markerPath, JsonSerializer.Serialize(marker, jsonOptions), new UTF8Encoding(false));
    Console.WriteLine(JsonSerializer.Serialize(marker, jsonOptions));
    if (persistenceCheck) await PersistenceCheck.RunAsync(output, artifactRoot, verified);
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error.ToString());
    return 1;
}

static string FindRepositoryDirectory()
{
    foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        for (DirectoryInfo? directory = new(start); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "src", "PureProject.Core", "PureProject.Core.csproj"))) return directory.FullName;
        }
    throw new DirectoryNotFoundException("Run this tool from the PureProject workspace.");
}

static void VerifyMarker(string path, string output, int tasksPerStatus)
{
    if (!File.Exists(path)) throw new InvalidDataException("Existing data has no synthetic fixture marker; it was not modified.");
    using var document = JsonDocument.Parse(File.ReadAllText(path));
    var marker = document.RootElement;
    if (marker.GetProperty("schemaVersion").GetInt32() != 1
        || marker.GetProperty("fixtureKind").GetString() != "pureproject-scale-audit"
        || !marker.GetProperty("synthetic").GetBoolean()
        || marker.GetProperty("idPrefix").GetString() != "scale-"
        || !Path.GetFullPath(marker.GetProperty("dataDirectory").GetString()!).Equals(output, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("The directory does not contain this tool's synthetic fixture marker.");
    var dimensions = marker.GetProperty("dimensions");
    if (dimensions.GetProperty("projects").GetInt32() != 10 || dimensions.GetProperty("groupsPerProject").GetInt32() != 10
        || dimensions.GetProperty("statusesPerGroup").GetInt32() != 10 || dimensions.GetProperty("tasksPerStatus").GetInt32() != tasksPerStatus)
        throw new InvalidDataException("The existing fixture belongs to another profile; no files were modified.");
}

static FixtureCounts Verify(IReadOnlyList<Project> projects, int tasksPerStatus)
{
    if (projects.Count != 10) throw new InvalidDataException("Expected 10 projects.");
    var ids = new HashSet<string>(StringComparer.Ordinal);
    void Id(string id)
    {
        if (!id.StartsWith("scale-", StringComparison.Ordinal) || !ids.Add(id))
            throw new InvalidDataException("An ID is duplicated or missing the scale- prefix: " + id);
    }
    foreach (var project in projects)
    {
        PmSerializer.EnsureValid(project);
        Id(project.Id);
        if (project.TaskGroups.Count != 10 || project.Tasks.Count != 100 * tasksPerStatus) throw new InvalidDataException("The project dimensions do not match the selected profile.");
        if (project.SyncEnabled) throw new InvalidDataException("Synthetic projects must not sync externally.");
        foreach (var group in project.TaskGroups)
        {
            Id(group.Id);
            if (group.Statuses.Count != 10 || project.Tasks.Count(t => t.TaskGroupId == group.Id) != 10 * tasksPerStatus)
                throw new InvalidDataException("The task group dimensions do not match the selected profile.");
            foreach (var status in group.Statuses)
            {
                Id(status.Id);
                if (project.Tasks.Count(t => t.TaskGroupId == group.Id && t.StatusId == status.Id) != tasksPerStatus)
                    throw new InvalidDataException("The task status dimensions do not match the selected profile.");
            }
        }
        var taskIds = project.Tasks.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var tagNames = project.Tags.Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var task in project.Tasks)
        {
            Id(task.Id);
            foreach (var subtask in task.Subtasks) Id(subtask.Id);
            foreach (var comment in task.Comments) Id(comment.Id);
            if (task.Dependencies.Any(d => d.TaskId == task.Id || !taskIds.Contains(d.TaskId)) || task.Tags.Any(t => !tagNames.Contains(t)))
                throw new InvalidDataException("A task dependency or tag reference is invalid.");
            if (task.TrackedStart is not null || task.Reminder is not null || task.Recurrence is not null)
                throw new InvalidDataException("Synthetic tasks must not start timers, reminders or recurrence.");
            if (!DateOnly.TryParseExact(task.DueDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                throw new InvalidDataException("A fixture task is missing a valid due date.");
        }
        foreach (var tag in project.Tags) Id(tag.Id);
        foreach (var milestone in project.Milestones) Id(milestone.Id);
    }
    var counts = new FixtureCounts(projects.Count, projects.Sum(p => p.TaskGroups.Count), projects.Sum(p => p.TaskGroups.Sum(g => g.Statuses.Count)), projects.Sum(p => p.Tasks.Count));
    if (counts != new FixtureCounts(10, 100, 1000, 1000 * tasksPerStatus)) throw new InvalidDataException("Fixture totals do not match the selected profile.");
    return counts;
}

static List<Project> BuildProjects(DateOnly day, int tasksPerStatus)
{
    string Date(int offset) => day.AddDays(offset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    string Stamp(int offset, int minute = 0) => new DateTimeOffset(day.AddDays(offset).ToDateTime(new TimeOnly(9, minute)), TimeSpan.FromHours(8)).ToString("O", CultureInfo.InvariantCulture);
    string[] projectNames = ["客户服务平台升级", "智能净水设备产品研发", "区域营销与渠道协同计划", "供应链质量改进", "企业知识库与流程规范建设", "数字化展厅体验提升", "移动端客户应用迭代", "制造执行系统整合", "年度品牌内容与活动交付", "跨区域交付与售后服务标准化专项"];
    string[] groupNames = ["需求调研", "产品规划", "交互与视觉设计", "客户端开发", "服务端开发", "测试与质量保障", "数据迁移及接口联调", "内容准备与培训", "上线发布与运行观察", "跨部门风险排查及客户验收跟踪事项"];
    string[] statusNames = ["待收集", "待澄清", "等待资料补齐与需求确认", "已排期", "进行中", "内部评审", "外部协同与交付前复核", "已验收", "已归档", "已取消"];
    string[] categories = ["todo", "todo", "todo", "active", "active", "active", "active", "done", "done", "cancelled"];
    string[] colors = ["#64748b", "#8b7355", "#b7791f", "#4f71a8", "#6e56a5", "#a95b70", "#287d83", "#3d7a5e", "#577e6a", "#9a6460"];
    string[] subjects = ["确认字段与验收口径", "整理访谈记录", "复核接口输入输出", "更新异常处理说明", "补充交互状态", "核对通知触发规则", "组织跨团队评审", "完成数据样本抽查", "跟进客户反馈", "确认上线依赖", "整理培训案例", "修订内容和提示文案", "验证权限与访问范围", "补充回归检查清单", "梳理历史数据映射", "检查布局与文字截断", "确认时区和日期边界", "记录运行观察结果", "处理长名称与多标签显示", "完成交付资料复核"];
    string[] tags = ["重点关注", "跨部门", "需要评审", "客户反馈", "风险跟踪", "资料核对", "体验优化", "常规事项"];
    var projects = new List<Project>(10);
    for (var p = 1; p <= 10; p++)
    {
        var projectId = $"scale-p{p:00}";
        var project = new Project
        {
            Id = projectId, Name = $"压力测试 {p:00}｜{projectNames[p - 1]}",
            Description = "独立合成项目，用于验证大量项目、任务组、状态与任务同时存在时的可读性和操作效率。所有内容均为测试样本。",
            Color = colors[p - 1], CreatedAt = Stamp(-45), UpdatedAt = Stamp(-1), StartDate = Date(-45), EndDate = Date(60),
            DefaultTaskGroupId = projectId + "-g01", SortOrder = p - 1, SyncEnabled = false,
            Readme = $"# 合成压力测试项目\n\n10 个任务组，每组 10 个状态，每个状态 {tasksPerStatus} 个任务。包含长标题、日期、标签、子任务和无环依赖。\n\n仅用于独立 UI / UE / UX 审查。"
        };
        project.Tags = tags.Select((name, i) => new Tag { Id = $"{projectId}-tag{i + 1:00}", Name = name, Color = colors[i] }).ToList();
        project.Milestones = Enumerable.Range(1, 5).Select(m => new Milestone
        {
            Id = $"{projectId}-milestone{m:00}", Title = new[] { "调研资料冻结", "方案评审", "联调完成", "试运行复盘", "交付验收" }[m - 1],
            Date = Date(-15 + (m - 1) * 15), Color = colors[(p + m) % 10], Description = "合成里程碑，用于时间线与日期密度检查。"
        }).ToList();
        project.Changelog.Add(new ChangelogEntry { Version = "scale-fixture-v1", Date = Date(0), Info = "生成确定性压力测试样本。", Auto = false });
        for (var g = 1; g <= 10; g++)
        {
            var groupId = $"{projectId}-g{g:00}";
            var group = new TaskGroup { Id = groupId, Name = $"{g:00} {groupNames[g - 1]}", SortOrder = g - 1, InitialStatusId = groupId + "-s01", CompletionStatusId = groupId + "-s08" };
            project.TaskGroups.Add(group);
            for (var s = 1; s <= 10; s++)
            {
                var statusId = $"{groupId}-s{s:00}";
                group.Statuses.Add(new TaskStatusDefinition { Id = statusId, Name = statusNames[s - 1], Category = categories[s - 1], Color = colors[s - 1], SortOrder = s - 1 });
                for (var t = 1; t <= tasksPerStatus; t++)
                {
                    var taskId = $"{statusId}-t{t:00}";
                    var dueOffset = categories[s - 1] is "done" or "cancelled" ? -1 - t % 14 : -12 + (p * 3 + g * 5 + s + t * 2) % 45;
                    var title = $"{subjects[(t - 1) % subjects.Length]}｜{p:00}.{g:00}.{s:00}.{t:00}";
                    if ((t - 1) % 20 + 1 is 7 or 14 or 19)
                        title += "：围绕跨部门协作、历史数据一致性及客户验收要求，核对不同窗口宽度下的完整任务标题、多项标签、截止日期与操作入口，补齐可复现步骤和说明，确保后续同事能够准确理解与继续处理这项工作";
                    var task = new ProjectTask
                    {
                        Id = taskId, Title = title, TaskGroupId = groupId, StatusId = statusId,
                        Description = $"合成样本 {p:00}/{g:00}/{s:00}/{t:00}，所属工作：{groupNames[g - 1]}。\n验收要点：核对内容完整性、状态及日期显示，保留清晰的下一步说明。",
                        Priority = new[] { "high", "medium", "low" }[(p + g + s + t) % 3],
                        Tags = [tags[(g + t) % tags.Length], tags[(g + t + 3) % tags.Length]],
                        DueDate = Date(dueOffset), DueTime = t % 5 == 0 ? "18:00" : null,
                        StartOffset = 45 + dueOffset - 3 - t % 5,
                        CreatedAt = Stamp(-30 + t % 12), UpdatedAt = Stamp(-1, t % 60),
                        CompletedAt = categories[s - 1] == "done" ? Stamp(-1 - t % 7) : null,
                        StatusBeforeClosedId = categories[s - 1] is "done" or "cancelled" ? groupId + "-s05" : null
                    };
                    if (t % 5 == 0)
                        task.Subtasks = Enumerable.Range(1, 3).Select(i => new Subtask { Id = $"{taskId}-sub{i:00}", Title = new[] { "收集输入材料并核对范围", "完成主要处理和自查", "补充验收依据与交接说明" }[i - 1], Done = categories[s - 1] == "done" || i == 1 }).ToList();
                    if (t % 20 == 0) task.Dependencies.Add(new Dependency { TaskId = $"{statusId}-t{t - 1:00}", DayOffset = 1 });
                    if (t % 20 == 10) task.Comments.Add(new TaskComment { Id = taskId + "-comment01", Content = "已核对本次输入材料，请在下一次评审前补齐交付说明。此评论为合成样本。", CreatedAt = Stamp(-2) });
                    project.Tasks.Add(task);
                }
            }
        }
        projects.Add(project);
    }
    return projects;
}

static async Task<string> HashModelsAsync(IEnumerable<Project> projects)
{
    using var sha = SHA256.Create();
    await using var stream = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write);
    await PmSerializer.WriteInternalAsync(stream, projects);
    await stream.FlushFinalBlockAsync();
    return Convert.ToHexString(sha.Hash!);
}

static async Task<string> HashFileAsync(string path)
{
    await using var stream = File.OpenRead(path);
    return Convert.ToHexString(await SHA256.HashDataAsync(stream));
}

internal sealed record FixtureCounts(int Projects, int TaskGroups, int Statuses, int Tasks);
