using PureProject.Core;

namespace PureProject.Infrastructure;

public sealed class RepositoryInUseException(string directory, Exception innerException)
    : IOException($"此数据目录已由另一个简项窗口使用，或无法取得写入锁：{directory}", innerException);

public sealed class RepositoryDataException(string filePath, string backupPath, Exception innerException)
    : IOException($"项目文件无法读取或校验失败，原文件已保留。请检查 {filePath}；可显式恢复备份 {backupPath}。", innerException)
{
    public string FilePath { get; } = filePath;
    public string BackupPath { get; } = backupPath;
}

/// <summary>Owns an exclusive session lock. A failed load never becomes an empty successful save.</summary>
public sealed class JsonProjectRepository : IDisposable
{
    private readonly FileStream _sessionLock;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private bool _canSave;
    private bool _disposed;
    private bool _temporaryRecoveryChecked;

    public static string DefaultDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PureProject", "WinUI");
    public string DataDirectory { get; }
    public string DataFilePath => Path.Combine(DataDirectory, "projects.json");
    public string BackupFilePath => DataFilePath + ".bak";
    public ProjectTemporaryRecoveryReport TemporaryRecovery { get; private set; } = ProjectTemporaryRecoveryReport.Empty;

    public JsonProjectRepository(string? directory = null)
    {
        DataDirectory = Path.GetFullPath(directory ?? DefaultDataDirectory);
        Directory.CreateDirectory(DataDirectory);
        try
        {
            _sessionLock = new FileStream(Path.Combine(DataDirectory, "session.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error) { throw new RepositoryInUseException(DataDirectory, error); }
    }

    public async Task<List<Project>> LoadAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _canSave = false;
            if (!_temporaryRecoveryChecked)
            {
                TemporaryRecovery = await AtomicProjectFile.RecoverTemporaryFilesAsync(DataFilePath, cancellationToken).ConfigureAwait(false);
                _temporaryRecoveryChecked = true;
            }
            if (!File.Exists(DataFilePath))
            {
                if (File.Exists(BackupFilePath))
                    throw new RepositoryDataException(DataFilePath, BackupFilePath,
                        new FileNotFoundException("主数据文件缺失，但存在备份；请先恢复备份。"));
                if (TemporaryRecovery.HasUncommittedFiles)
                    throw new RepositoryDataException(DataFilePath, BackupFilePath,
                        new FileNotFoundException("主数据文件缺失，但存在未提交或尚未检查的暂存候选；请检查恢复候选后显式导入，未自动建立空库。"));
                _canSave = true;
                return [];
            }
            try
            {
                await using var stream = OpenRead(DataFilePath);
                var projects = (await PmSerializer.ReadInternalAsync(stream, cancellationToken).ConfigureAwait(false)).ToList();
                _canSave = true;
                return projects;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                throw new RepositoryDataException(DataFilePath, BackupFilePath, error);
            }
        }
        finally { _operationLock.Release(); }
    }

    public async Task SaveAsync(IEnumerable<Project> projects, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_canSave) throw new InvalidOperationException("必须成功读取项目或显式恢复备份后，才能保存。原文件未修改。");
            await AtomicProjectFile.WriteAsync(DataFilePath,
                (stream, token) => PmSerializer.WriteInternalAsync(stream, projects, token),
                PmSerializer.ValidateInternalAsync, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally { _operationLock.Release(); }
    }

    public async Task<List<Project>> RestoreBackupAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _operationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<Project> projects;
            await using (var stream = OpenRead(BackupFilePath))
                projects = (await PmSerializer.ReadInternalAsync(stream, cancellationToken).ConfigureAwait(false)).ToList();
            if (File.Exists(DataFilePath))
            {
                var retained = Path.Combine(DataDirectory, $"projects.before-restore.{DateTime.UtcNow:yyyyMMddHHmmssfff}.{Guid.NewGuid():N}.json");
                File.Copy(DataFilePath, retained, overwrite: false);
            }
            // Preserve the known-good .bak instead of replacing it with the corrupt primary.
            await AtomicProjectFile.WriteAsync(DataFilePath,
                (stream, token) => PmSerializer.WriteInternalAsync(stream, projects, token),
                PmSerializer.ValidateInternalAsync, keepBackup: false, cancellationToken).ConfigureAwait(false);
            _canSave = true;
            return projects;
        }
        finally { _operationLock.Release(); }
    }

    public static async Task<IReadOnlyList<Project>> ImportAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = OpenRead(path);
        return await PmSerializer.ReadInternalAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    public static Task ExportProjectAsync(string path, Project project, CancellationToken cancellationToken = default)
        => AtomicFile.WriteAsync(path, PmSerializer.ExportPm(project), cancellationToken: cancellationToken);

    public static Task ExportBackupAsync(string path, IEnumerable<Project> projects, CancellationToken cancellationToken = default)
        => AtomicProjectFile.WriteAsync(path,
            (stream, token) => PmSerializer.WriteInternalAsync(stream, projects, token),
            PmSerializer.ValidateInternalAsync, keepBackup: false, cancellationToken);

    private static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
        64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sessionLock.Dispose();
        _operationLock.Dispose();
    }
}
