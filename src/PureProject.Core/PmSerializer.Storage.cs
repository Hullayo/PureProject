using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace PureProject.Core;

public static partial class PmSerializer
{
    /// <summary>Bound native storage by UTF-8 bytes; legacy string imports retain their existing limit.</summary>
    public const long MaxStorageBytes = 256L * 1024 * 1024;
    private static readonly JsonSerializerOptions StorageWriteOptions = CreateStorageWriteOptions();
    private static readonly JsonSerializerOptions CanonicalReadOptions = new(Options) { AllowDuplicateProperties = false };

    /// <summary>Read native schema 4 without constructing a DOM for the complete database.
    /// Seekable streams permit bounded legacy migration; the caller retains ownership of the stream.</summary>
    public static Task<IReadOnlyList<Project>> ReadInternalAsync(Stream stream, CancellationToken cancellationToken = default)
        => ReadInternalCoreAsync(stream, validateOnly: false, cancellationToken);

    /// <summary>Validate a staged database, retaining only project IDs after each project is checked.</summary>
    public static async Task ValidateInternalAsync(Stream stream, CancellationToken cancellationToken = default)
        => _ = await ReadInternalCoreAsync(stream, validateOnly: true, cancellationToken).ConfigureAwait(false);

    private static async Task<IReadOnlyList<Project>> ReadInternalCoreAsync(Stream stream, bool validateOnly, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek) throw new ArgumentException("项目存储读取需要可定位的流。", nameof(stream));
        var start = stream.Position;
        if (stream.Length - start > MaxStorageBytes) throw Error("", "项目数据超过 256 MiB 文件上限");
        cancellationToken.ThrowIfCancellationRequested();
        // File.ReadAllText historically accepted a UTF-8 BOM. Preserve that compatibility.
        var prefix = new byte[3];
        var count = await stream.ReadAsync(prefix, cancellationToken).ConfigureAwait(false);
        stream.Position = count == 3 && prefix.AsSpan().SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }) ? start + 3 : start;
        var converter = new StorageProjectConverter(validateOnly, cancellationToken);
        var options = new JsonSerializerOptions(CanonicalReadOptions) { Converters = { converter } };
        try
        {
            var document = await JsonSerializer.DeserializeAsync<StorageDocument>(stream, options, cancellationToken).ConfigureAwait(false);
            if (document is null || document.Projects is null || document.Schema.ValueKind == JsonValueKind.Undefined || document.Kind is not null)
                throw new LegacyStorageException();
            var schema = ReadSchema(document.Schema);
            if (schema != CurrentSchema) throw new LegacyStorageException();
            return validateOnly ? [] : document.Projects;
        }
        catch (Exception error) when (error is JsonException or PmValidationException or LegacyStorageException)
        {
            // Migration is deliberately isolated from the fast native path. It remains bounded and
            // uses the established migration/parser semantics, including inherited schema versions.
            stream.Position = start;
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024, leaveOpen: true);
            var text = new StringBuilder();
            var buffer = new char[64 * 1024];
            int read;
            while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (text.Length + read > MaxCharacters)
                    throw error is PmValidationException ? error : Error("", "旧格式文件超过 32 MB 字符上限，请按项目拆分迁移");
                text.Append(buffer, 0, read);
            }
            var projects = ParseProjects(text.ToString());
            return validateOnly ? [] : projects;
        }
    }

    /// <summary>Write schema 4 directly to a stream, with at most one project's JSON buffered.</summary>
    public static async Task WriteInternalAsync(Stream stream, IEnumerable<Project> projects, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(projects);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Encoder = Options.Encoder, Indented = false, MaxDepth = 128 });
        writer.WriteStartObject();
        writer.WriteNumber("schema_version", CurrentSchema);
        writer.WriteStartArray("projects");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ids.Count >= 2000) throw Error("projects", "项目数超过 2000");
            EnsureValid(project);
            if (!ids.Add(project.Id)) throw Error("projects.id", "项目 ID 重复");
            JsonSerializer.Serialize(writer, project, StorageWriteOptions);
            if (writer.BytesCommitted + writer.BytesPending > MaxStorageBytes) throw Error("", "项目数据超过 256 MiB 文件上限");
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
        if (writer.BytesCommitted + writer.BytesPending > MaxStorageBytes) throw Error("", "项目数据超过 256 MiB 文件上限");
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Strictly parse one flat schema 4 project, preserving all extension namespaces and values.</summary>
    public static Project ParseCanonicalProject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Error("project", "应为项目对象");
        if (element.TryGetProperty("schema_version", out var schema) && ReadSchema(schema) != CurrentSchema)
            throw Error("schema_version", "此入口仅接受 schema v4 项目");
        ValidateCanonicalStructure(element);
        try
        {
            var project = element.Deserialize<Project>(CanonicalReadOptions) ?? throw Error("project", "无法读取项目");
            EnsureValid(project);
            return project;
        }
        catch (JsonException error) { throw Error(error.Path ?? "project", $"JSON 格式或字段类型错误：{error.Message}"); }
    }

    private static int ReadSchema(JsonElement element)
    {
        var number = 0;
        var valid = element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out number);
        if (!valid && element.ValueKind == JsonValueKind.String)
            valid = int.TryParse(element.GetString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out number);
        if (!valid || number < 1) throw Error("schema_version", "应为正整数");
        if (number > CurrentSchema) throw Error("schema_version", $"项目使用未来版本，当前仅支持 schema v{CurrentSchema}");
        return number;
    }

    private static void ValidateCanonicalStructure(JsonElement project)
    {
        void RequiredText(JsonElement owner, string field, string path)
        {
            if (!owner.TryGetProperty(field, out var value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                throw Error(path, "缺失或不是非空字符串");
        }
        JsonElement Array(JsonElement owner, string field, string path, int limit, bool required = false)
        {
            if (!owner.TryGetProperty(field, out var value) && !required) return default;
            if (value.ValueKind != JsonValueKind.Array) throw Error(path, "应为数组");
            if (value.GetArrayLength() > limit) throw Error(path, $"超过 {limit} 个元素");
            foreach (var item in value.EnumerateArray()) if (item.ValueKind == JsonValueKind.Null) throw Error(path, "数组不能包含 null");
            return value;
        }
        foreach (var key in new[] { "id", "name", "default_task_group_id" }) RequiredText(project, key, "project." + key);
        var tasks = Array(project, "tasks", "tasks", 20000, true);
        var groups = Array(project, "task_groups", "task_groups", 2000, true);
        Array(project, "tags", "tags", 5000);
        Array(project, "milestones", "milestones", 2000);
        Array(project, "changelog", "changelog", 20000);
        var index = 0;
        foreach (var task in tasks.EnumerateArray())
        {
            var path = $"tasks[{index++}]";
            if (task.ValueKind != JsonValueKind.Object) throw Error(path, "应为对象");
            foreach (var key in new[] { "id", "title", "task_group_id", "status_id" }) RequiredText(task, key, path + "." + key);
            foreach (var (field, limit) in new[] { ("dependencies", 500), ("subtasks", 2000), ("comments", 5000), ("tags", 200) }) Array(task, field, path + "." + field, limit);
            if (task.TryGetProperty("recurrence", out var recurrence) && recurrence.ValueKind == JsonValueKind.Object)
                RequiredText(recurrence, "freq", path + ".recurrence.freq");
            if (task.TryGetProperty("dependencies", out var dependencies))
                foreach (var dependency in dependencies.EnumerateArray())
                {
                    if (dependency.ValueKind != JsonValueKind.Object) throw Error(path + ".dependencies", "应为依赖对象");
                    RequiredText(dependency, "taskId", path + ".dependencies.taskId");
                }
        }
        index = 0;
        foreach (var group in groups.EnumerateArray())
        {
            var path = $"task_groups[{index++}]";
            if (group.ValueKind != JsonValueKind.Object) throw Error(path, "应为对象");
            foreach (var key in new[] { "id", "name", "initial_status_id", "completion_status_id" }) RequiredText(group, key, path + "." + key);
            var statuses = Array(group, "statuses", path + ".statuses", 500, true);
            foreach (var status in statuses.EnumerateArray())
            {
                if (status.ValueKind != JsonValueKind.Object) throw Error(path + ".statuses", "应为状态对象");
                foreach (var key in new[] { "id", "name", "color", "category" }) RequiredText(status, key, path + ".statuses." + key);
            }
        }
        ValidateStringLengths(project);
    }

    // Depth-first traversal keeps the stack bounded by JSON nesting, unlike the former full-tree queue.
    private static void ValidateStringLengths(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String && element.GetString()!.Length > 200000)
            throw Error("project", "字符串超过 200000 字符");
        if (element.ValueKind == JsonValueKind.Object)
            foreach (var property in element.EnumerateObject()) ValidateStringLengths(property.Value);
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) ValidateStringLengths(child);
    }

    private static JsonSerializerOptions CreateStorageWriteOptions()
    {
        var resolver = new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info =>
        {
            if (info.Type != typeof(Project)) return;
            var extension = info.Properties.Single(property => property.IsExtensionData);
            extension.Get = model =>
            {
                var extra = ((Project)model).Extra;
                return extra.ContainsKey("storage") || extra.ContainsKey("schema_version")
                    ? extra.Where(pair => pair.Key is not "storage" and not "schema_version").ToDictionary(pair => pair.Key, pair => pair.Value)
                    : extra;
            };
        });
        return new JsonSerializerOptions(Options) { WriteIndented = false, TypeInfoResolver = resolver };
    }

    private sealed class StorageDocument
    {
        [JsonPropertyName("schema_version")] public JsonElement Schema { get; set; }
        [JsonPropertyName("kind")] public string? Kind { get; set; }
        [JsonPropertyName("projects")] public List<Project>? Projects { get; set; }
    }

    private sealed class StorageProjectConverter(bool validateOnly, CancellationToken cancellationToken) : JsonConverter<Project>
    {
        private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
        public override bool HandleNull => true;
        public override Project Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_ids.Count >= 2000) throw Error("projects", "项目数超过 2000");
            using var document = JsonDocument.ParseValue(ref reader);
            var project = ParseCanonicalProject(document.RootElement);
            if (!_ids.Add(project.Id)) throw Error("projects.id", "项目 ID 重复");
            project.Extra.Remove("storage");
            project.Extra.Remove("schema_version");
            return validateOnly ? null! : project;
        }
        public override void Write(Utf8JsonWriter writer, Project value, JsonSerializerOptions options)
            => throw new NotSupportedException();
    }

    private sealed class LegacyStorageException : Exception;
}
