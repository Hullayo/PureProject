using System.Text.Json;

namespace PureProject.Infrastructure;

public sealed record AppSettings
{
    public string Theme { get; init; } = "System";
    public bool AutoBackupEnabled { get; init; }
    public int AutoBackupIntervalMinutes { get; init; } = 5;
    public Dictionary<string, string> Shortcuts { get; init; } = [];
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
            var settings = new AppSettings
            {
                Theme = stored.Theme,
                AutoBackupEnabled = stored.AutoBackupEnabled,
                AutoBackupIntervalMinutes = stored.AutoBackupIntervalMinutes,
                Shortcuts = stored.Shortcuts,
                SyncServerUrl = stored.SyncServerUrl,
                SyncStateServerUrl = stored.SyncStateServerUrl,
                SyncRevisions = stored.SyncRevisions ?? [],
                SyncedProjectHashes = stored.SyncedProjectHashes ?? []
            };
            try { Validate(settings); }
            catch (ArgumentException error) { throw new InvalidDataException("设置文件包含无效配置，原文件已保留。", error); }
            return settings with
            {
                Shortcuts = CanonicalShortcuts(settings.Shortcuts),
                SyncToken = string.IsNullOrEmpty(stored.ProtectedSyncToken) ? "" : WindowsDataProtection.Unprotect(stored.ProtectedSyncToken)
            };
        }
        finally { _gate.Release(); }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        Validate(settings);
        var stored = new StoredSettings
        {
            Theme = settings.Theme,
            AutoBackupEnabled = settings.AutoBackupEnabled,
            AutoBackupIntervalMinutes = settings.AutoBackupIntervalMinutes,
            Shortcuts = CanonicalShortcuts(settings.Shortcuts),
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

    private static Dictionary<string, string> CanonicalShortcuts(IReadOnlyDictionary<string, string> shortcuts)
        => shortcuts.ToDictionary(pair => pair.Key, pair => ShortcutSettings.Parse(pair.Value).ToString(), StringComparer.Ordinal);

    private static void Validate(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.Theme is not ("System" or "Light" or "Dark")) throw new ArgumentException("主题必须为 System、Light 或 Dark。", nameof(settings));
        if (settings.AutoBackupIntervalMinutes is not (1 or 5 or 10 or 15 or 30 or 60 or 120))
            throw new ArgumentException("自动备份间隔必须为 1、5、10、15、30、60 或 120 分钟。", nameof(settings));
        ArgumentNullException.ThrowIfNull(settings.Shortcuts);
        ShortcutSettings.Validate(settings.Shortcuts);
        ArgumentNullException.ThrowIfNull(settings.SyncServerUrl);
        if (!string.IsNullOrWhiteSpace(settings.SyncServerUrl)) _ = RestSyncClient.ValidateServerUri(settings.SyncServerUrl);
    }

    private sealed class StoredSettings
    {
        public int Version { get; init; } = 1;
        public string Theme { get; init; } = "System";
        public bool AutoBackupEnabled { get; init; }
        public int AutoBackupIntervalMinutes { get; init; } = 5;
        public Dictionary<string, string> Shortcuts { get; init; } = [];
        public string SyncServerUrl { get; init; } = "";
        public string ProtectedSyncToken { get; init; } = "";
        public string SyncStateServerUrl { get; init; } = "";
        public Dictionary<string, long>? SyncRevisions { get; init; }
        public Dictionary<string, string>? SyncedProjectHashes { get; init; }
    }
}
