using System.Text.Json;
using System.Text.Json.Serialization;

namespace PureProject.Infrastructure;

public sealed record AppSettings
{
    public string Theme { get; init; } = "System";
    public string SyncServerUrl { get; init; } = "";
    public string SyncToken { get; init; } = "";
    public string SyncStateServerUrl { get; init; } = "";
    public Dictionary<string, long> SyncRevisions { get; init; } = [];
    public Dictionary<string, string> SyncedProjectHashes { get; init; } = [];
}

public sealed class SettingsRepository(string? directory = null)
{
    private readonly string _path = Path.Combine(Path.GetFullPath(directory ?? JsonProjectRepository.DefaultDataDirectory), "settings.json");
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public string FilePath => _path;

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_path)) return new();
            var stored = JsonSerializer.Deserialize<StoredSettings>(await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false), JsonOptions)
                ?? throw new InvalidDataException("设置文件为空，未自动覆盖。");
            if (stored.Version != 1) throw new InvalidDataException("设置文件版本不受支持。");
            return new AppSettings
            {
                Theme = stored.Theme,
                SyncServerUrl = stored.SyncServerUrl,
                SyncToken = string.IsNullOrEmpty(stored.ProtectedSyncToken) ? "" : WindowsDataProtection.Unprotect(stored.ProtectedSyncToken),
                SyncStateServerUrl = stored.SyncStateServerUrl,
                SyncRevisions = stored.SyncRevisions ?? [],
                SyncedProjectHashes = stored.SyncedProjectHashes ?? []
            };
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        if (settings.Theme is not ("System" or "Light" or "Dark")) throw new ArgumentException("主题必须为 System、Light 或 Dark。", nameof(settings));
        if (!string.IsNullOrWhiteSpace(settings.SyncServerUrl)) _ = RestSyncClient.ValidateServerUri(settings.SyncServerUrl);
        var stored = new StoredSettings
        {
            Theme = settings.Theme,
            SyncServerUrl = settings.SyncServerUrl.Trim().TrimEnd('/'),
            ProtectedSyncToken = string.IsNullOrEmpty(settings.SyncToken) ? "" : WindowsDataProtection.Protect(settings.SyncToken),
            SyncStateServerUrl = settings.SyncStateServerUrl,
            SyncRevisions = settings.SyncRevisions,
            SyncedProjectHashes = settings.SyncedProjectHashes
        };
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { await AtomicFile.WriteAsync(_path, JsonSerializer.Serialize(stored, JsonOptions), cancellationToken: cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private sealed class StoredSettings
    {
        public int Version { get; init; } = 1;
        public string Theme { get; init; } = "System";
        public string SyncServerUrl { get; init; } = "";
        public string ProtectedSyncToken { get; init; } = "";
        public string SyncStateServerUrl { get; init; } = "";
        public Dictionary<string, long>? SyncRevisions { get; init; }
        public Dictionary<string, string>? SyncedProjectHashes { get; init; }
    }
}
