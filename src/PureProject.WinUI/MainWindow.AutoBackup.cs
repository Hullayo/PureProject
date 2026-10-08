using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PureProject.Infrastructure;

namespace PureProject.WinUI;

public sealed partial class MainWindow
{
    private readonly AutoBackupService _autoBackupService = new();
    private DispatcherTimer? _autoBackupTimer;
    private CancellationTokenSource? _autoBackupCancellation;
    private bool _autoBackupRunning;
    private string? _lastAutoBackupError;

    private void ConfigureAutoBackup()
    {
        StopAutoBackup();
        if (!_ready || !_settingsLoaded || !_settings.AutoBackupEnabled) return;
        _autoBackupCancellation = new CancellationTokenSource();
        _autoBackupTimer ??= CreateAutoBackupTimer();
        _autoBackupTimer.Interval = TimeSpan.FromMinutes(_settings.AutoBackupIntervalMinutes);
        _autoBackupTimer.Start();
    }

    private DispatcherTimer CreateAutoBackupTimer()
    {
        var timer = new DispatcherTimer();
        timer.Tick += async (_, _) => await RunAutoBackupAsync();
        return timer;
    }

    private void StopAutoBackup()
    {
        _autoBackupTimer?.Stop();
        _autoBackupCancellation?.Cancel();
        _autoBackupCancellation?.Dispose();
        _autoBackupCancellation = null;
    }

    private async Task RunAutoBackupAsync()
    {
        if (!_ready || !_settingsLoaded || !_settings.AutoBackupEnabled || _busy || _autoBackupRunning
            || _autoBackupCancellation is not { } cancellation) return;
        var token = cancellation.Token;
        // Published project versions are immutable; taking the list on the dispatcher
        // gives the worker one consistent snapshot without blocking editing to clone it.
        var snapshot = _projects.ToList();
        _autoBackupRunning = true;
        try
        {
            var result = await _autoBackupService.BackupAsync(snapshot, token);
            if (token.IsCancellationRequested || !_ready) return;
            if (result.Failures.Count > 0)
                throw new IOException(string.Join("；", result.Failures.Take(3).Select(failure => failure.ProjectName + "：" + failure.Message)));
            _lastAutoBackupError = null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!_ready || token.IsCancellationRequested || _lastAutoBackupError == ex.Message) return;
            _lastAutoBackupError = ex.Message;
            ShowMessage("自动备份未完成：" + ex.Message + "。请检查备份目录的权限和磁盘空间。", InfoBarSeverity.Warning);
        }
        finally { _autoBackupRunning = false; }
    }
}
