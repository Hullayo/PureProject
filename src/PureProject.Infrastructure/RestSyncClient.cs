using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PureProject.Core;

namespace PureProject.Infrastructure;

public sealed record RemoteProject
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public long Rev { get; init; }
    [JsonPropertyName("updated_at")] public string UpdatedAt { get; init; } = "";
    [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; }
    public bool Deleted { get; init; }
}

public sealed record RemoteProjectIndex(long Rev, IReadOnlyList<RemoteProject> Projects);
public sealed record PulledProject(Project Project, long Rev);
public sealed record SyncConflict(string Id, string Reason, long ServerRev, string? ServerData = null, bool Deleted = false);
public sealed record PushResult(long? Rev, SyncConflict? Conflict)
{
    public bool Succeeded => Conflict is null && Rev is not null;
}

/// <summary>Matches the existing pm-sync-server REST API. Every PUT includes the known base revision.</summary>
public sealed class RestSyncClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    private const int CurrentSchema = 4;
    private const long MaximumResponseBytes = 40L * 1024 * 1024;

    public RestSyncClient(HttpClient? client = null)
    {
        _ownsClient = client is null;
        _http = client ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

    public static Uri ValidateServerUri(string address)
    {
        if (!Uri.TryCreate(address.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("请输入有效的 HTTP / HTTPS 同步服务器地址，不包含密码、查询参数或片段。", nameof(address));
        return uri;
    }

    public async Task<RemoteProjectIndex> ListAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        using var request = Request(settings, HttpMethod.Get, "api/projects");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var body = await ReadResponseAsync(response, cancellationToken).ConfigureAwait(false);
        var envelope = JsonSerializer.Deserialize<IndexEnvelope>(body, Options) ?? throw new InvalidDataException("同步索引为空。");
        if (envelope.Rev < 0 || envelope.Projects is null) throw new InvalidDataException("同步索引格式不正确。");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in envelope.Projects)
        {
            CheckSchema(project.SchemaVersion);
            if (string.IsNullOrWhiteSpace(project.Id) || project.Rev < 0 || !ids.Add(project.Id))
                throw new InvalidDataException("同步索引包含无效或重复项目。");
        }
        return new(envelope.Rev, envelope.Projects);
    }

    public async Task<PulledProject> PullAsync(AppSettings settings, string id, CancellationToken cancellationToken = default)
    {
        using var request = Request(settings, HttpMethod.Get, "api/projects/" + Uri.EscapeDataString(id));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var envelope = JsonSerializer.Deserialize<PullEnvelope>(await ReadResponseAsync(response, cancellationToken).ConfigureAwait(false), Options)
            ?? throw new InvalidDataException("服务器项目数据为空。");
        CheckSchema(envelope.SchemaVersion);
        if (envelope.Rev < 1 || string.IsNullOrEmpty(envelope.Data)) throw new InvalidDataException("服务器项目缺少版本或数据。");
        var project = PmSerializer.ParsePm(envelope.Data);
        if (!string.Equals(project.Id, id, StringComparison.Ordinal)) throw new InvalidDataException("服务器项目 ID 与请求不一致，未应用数据。");
        return new(project, envelope.Rev);
    }

    public async Task<PushResult> PushAsync(AppSettings settings, Project project, long baseRev, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(baseRev);
        var data = PmSerializer.ExportPm(project);
        _ = PmSerializer.ParsePm(data);
        using var request = Request(settings, HttpMethod.Put, "api/projects/" + Uri.EscapeDataString(project.Id));
        request.Headers.Add("X-Base-Rev", baseRev.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Content = new StringContent(data, Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            var conflict = JsonSerializer.Deserialize<ConflictEnvelope>(await ReadResponseAsync(response, cancellationToken).ConfigureAwait(false), Options)
                ?? throw new InvalidDataException("服务器冲突响应格式错误。");
            return new(null, new(project.Id, conflict.Reason ?? "revision_conflict", conflict.ServerRev, conflict.ServerData, conflict.Deleted));
        }
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var result = JsonSerializer.Deserialize<PushEnvelope>(await ReadResponseAsync(response, cancellationToken).ConfigureAwait(false), Options)
            ?? throw new InvalidDataException("服务器上传响应为空。");
        CheckSchema(result.SchemaVersion);
        if (result.Rev < 1) throw new InvalidDataException("服务器上传响应缺少有效版本号。");
        return new(result.Rev, null);
    }

    private static HttpRequestMessage Request(AppSettings settings, HttpMethod method, string relativePath)
    {
        var request = new HttpRequestMessage(method, new Uri(ValidateServerUri(settings.SyncServerUrl), relativePath));
        if (!string.IsNullOrWhiteSpace(settings.SyncToken)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.SyncToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static void CheckSchema(int schema)
    {
        if (schema > CurrentSchema) throw new InvalidDataException($"服务器使用 schema v{schema}，当前客户端仅支持 v{CurrentSchema}，请升级客户端。");
        if (schema < 1) throw new InvalidDataException("服务器响应缺少有效 schema_version。");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        // Avoid embedding server responses or request URLs that could contain credentials in UI errors.
        await Task.CompletedTask;
        cancellationToken.ThrowIfCancellationRequested();
        throw new HttpRequestException($"同步服务器返回 HTTP {(int)response.StatusCode}（{response.ReasonPhrase}）。", null, response.StatusCode);
    }

    private static async Task<string> ReadResponseAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw new InvalidDataException("服务器响应过大。");
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var block = new byte[64 * 1024];
        int read;
        while ((read = await source.ReadAsync(block, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaximumResponseBytes) throw new InvalidDataException("服务器响应过大。");
            buffer.Write(block, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    public void Dispose() { if (_ownsClient) _http.Dispose(); }

    private sealed class IndexEnvelope { public long Rev { get; init; } public List<RemoteProject>? Projects { get; init; } }
    private sealed class PullEnvelope
    {
        public long Rev { get; init; }
        [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; }
        public string? Data { get; init; }
    }
    private sealed class PushEnvelope
    {
        public long Rev { get; init; }
        [JsonPropertyName("schema_version")] public int SchemaVersion { get; init; }
    }
    private sealed class ConflictEnvelope
    {
        public string? Reason { get; init; }
        public long ServerRev { get; init; }
        public string? ServerData { get; init; }
        public bool Deleted { get; init; }
    }
}
