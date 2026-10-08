using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using PureProject.Core;
using PureProject.Infrastructure;
using Windows.Storage.Pickers;
using System.Text;

namespace PureProject.WinUI;

public sealed partial class MainWindow
{
    private List<SyncConflict> _syncConflicts = [];

    private async Task ImportAsync()
    {
        if (!_ready || _busy) return;
        var picker = new FileOpenPicker();
        foreach (var extension in new[] { ".pureproject", ".mm", ".xlsx", ".pm", ".json" }) picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        var imported = await RunTransferAsync("正在读取并校验导入文件", token =>
        {
            if (Path.GetExtension(file.Path).ToLowerInvariant() is ".pm" or ".json")
            {
                using var stream = File.OpenRead(file.Path);
                return PmSerializer.ReadInternalAsync(stream, token).GetAwaiter().GetResult().ToList();
            }
            return new ProjectExchangeService().ImportFile(file.Path, token).ToList();
        }, discardResultOnCancellation: true);
        if (imported is null) return;
        var existingIds = _projects.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var collisions = imported.Where(p => existingIds.Contains(p.Id)).ToList();
        var content = Column(12);
        content.Children.Add(Label($"已校验 {imported.Count:N0} 个项目、{imported.Sum(p => p.TaskGroups.Count):N0} 个任务组、{imported.Sum(p => p.Tasks.Count):N0} 条任务。", true));
        content.Children.Add(Label($"新增 {imported.Count - collisions.Count:N0} 个项目；替换 {collisions.Count:N0} 个相同 ID 的项目。导入将一次性保存，可在本次运行中撤销。", true));
        if (collisions.Count > 0) content.Children.Add(Label("将替换：" + string.Join("、", collisions.Take(5).Select(p => p.Name)) + (collisions.Count > 5 ? $" 等 {collisions.Count} 个项目" : ""), true));
        var preview = new ContentDialog { Title = "确认导入", Content = content, PrimaryButtonText = "导入并保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        AutomationProperties.SetAutomationId(preview, "ImportPreview");
        if (await DialogAsync(preview) != ContentDialogResult.Primary) return;
        await ApplyImportedProjectsAsync(imported);
        ShowMessage($"导入完成：{imported.Count:N0} 个项目、{imported.Sum(p => p.Tasks.Count):N0} 条任务，已保存至本地。", InfoBarSeverity.Success);
    }

    private Task ApplyImportedProjectsAsync(IReadOnlyList<Project> imported) => CommitAsync(projects =>
    {
        foreach (var project in imported)
        {
            var index = projects.FindIndex(old => old.Id == project.Id);
            if (index >= 0) projects[index] = project; else projects.Add(project);
        }
    }, $"已导入 {imported.Count:N0} 个项目", collectionOnly: true);

    private async Task<T?> RunTransferAsync<T>(string title, Func<CancellationToken, T> work, bool discardResultOnCancellation = false) where T : class
    {
        if (_busy || _dialogOpen) return null;
        using var cancellation = new CancellationTokenSource();
        var content = Column(12);
        content.Children.Add(Label("数据较多时可能需要一些时间。完成校验前可取消。", true));
        content.Children.Add(new ProgressBar { IsIndeterminate = true, MinWidth = 320, Foreground = ThemeBrush("AccentBrush"), Background = ThemeBrush("SubtleBackgroundBrush") });
        var dialog = new ContentDialog { Title = title, Content = content, CloseButtonText = "取消" };
        AutomationProperties.SetAutomationId(dialog, "TransferProgress");
        var finished = false;
        dialog.Closing += (_, args) =>
        {
            if (finished) return;
            args.Cancel = true; cancellation.Cancel(); dialog.CloseButtonText = "正在取消…";
        };
        var showing = DialogAsync(dialog);
        _busy = true;
        try
        {
            var result = await Task.Run(() => work(cancellation.Token));
            // A canceled read can be discarded even at the final dispatcher handoff.
            // An export that already atomically committed must report success.
            if (discardResultOnCancellation) cancellation.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            ShowMessage("操作已取消。原有项目和目标文件保持完整。"); return null;
        }
        finally { finished = true; _busy = false; dialog.Hide(); await showing; }
    }

    private async Task SaveTextAsync(string text, string name, string extension)
    {
        var picker = new FileSavePicker();
        picker.FileTypeChoices.Add(extension switch { ".pm" => "简项项目", ".csv" => "CSV 表格", ".md" => "Markdown 报告", _ => "JSON 备份" }, new List<string> { extension });
        picker.SuggestedFileName = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        await AtomicFile.WriteAsync(file.Path, text);
        ShowMessage($"已导出至 {file.Path}", InfoBarSeverity.Success);
    }

    private async Task ShowExportAsync()
    {
        if (!_ready || _busy) return;
        var format = new ComboBox { Header = "文件格式", SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var label in new[] { "简项完整数据包（.pureproject）", "开源思维导图（FreeMind / Freeplane .mm）", "Excel 工作簿（.xlsx · 合并单元格）" }) format.Items.Add(label);
        var scope = new ComboBox { Header = "导出范围", SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        scope.Items.Add($"全部项目（{_projects.Count:N0} 个 / {_projects.Sum(p => p.Tasks.Count):N0} 条任务）");
        var current = Current;
        if (current is not null) scope.Items.Add($"当前项目（{current.Tasks.Count:N0} 条任务）");
        var hint = Label("完整数据包适合备份和迁移，包含所有任务属性及扩展字段。", true);
        format.SelectionChanged += (_, _) => hint.Text = format.SelectedIndex switch
        {
            1 => "可用开源 Freeplane / FreeMind 打开。支持编辑层级名称、任务标题与归属后还原；请保留节点标识和数据属性。",
            2 => "每个项目独立工作表，分组标题合并、表头冻结。支持修改可见字段后还原；请保留 ID、结构和元数据工作表。",
            _ => "完整数据包适合备份和迁移，包含所有任务属性及扩展字段。"
        };
        AutomationProperties.SetAutomationId(format, "ExportFormat"); AutomationProperties.SetAutomationId(scope, "ExportScope");
        var panel = Column(12); panel.Children.Add(format); panel.Children.Add(scope); panel.Children.Add(hint);
        panel.Children.Add(Label("三种格式均支持通过本软件导入还原，包含归档项目与任务组；同步地址和凭据不导出。", true));
        var choice = await DialogAsync(new ContentDialog { Title = "备份与导出", Content = panel, PrimaryButtonText = "选择保存位置", SecondaryButtonText = "其他导出", CloseButtonText = "取消" });
        if (choice == ContentDialogResult.Secondary) { await ShowOtherExportsAsync(current); return; }
        if (choice != ContentDialogResult.Primary) return;
        var extension = format.SelectedIndex switch { 1 => ".mm", 2 => ".xlsx", _ => ".pureproject" };
        var selected = scope.SelectedIndex == 1 && current is not null ? new List<Project> { current } : _projects.ToList();
        var picker = new FileSavePicker { SuggestedFileName = $"PureProject-{DateTime.Now:yyyyMMdd-HHmm}" };
        picker.FileTypeChoices.Add(format.SelectedItem.ToString()!, new List<string> { extension });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        var result = await RunTransferAsync("正在生成导出文件", token =>
        {
            new ProjectExchangeService().ExportFile(file.Path, selected, token); return file.Path;
        });
        if (result is not null) ShowMessage($"已导出 {selected.Count:N0} 个项目、{selected.Sum(p => p.Tasks.Count):N0} 条任务至 {result}", InfoBarSeverity.Success);
    }

    private async Task ShowOtherExportsAsync(Project? current)
    {
        var options = new ComboBox { Header = "导出内容", SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        options.Items.Add("全部项目原生备份（JSON）");
        if (current is not null) foreach (var name in new[] { "当前项目（.pm）", "当前项目任务表（CSV）", "当前项目报告（Markdown）" }) options.Items.Add(name);
        var panel = Column(12); panel.Children.Add(options);
        panel.Children.Add(Label("原生 JSON 与 .pm 可重新导入。CSV 和 Markdown 用于阅读与报告，不包含完整还原数据。", true));
        if (await DialogAsync(new ContentDialog { Title = "其他导出", Content = panel, PrimaryButtonText = "选择保存位置", CloseButtonText = "取消" }) != ContentDialogResult.Primary) return;
        var index = options.SelectedIndex;
        var extension = index switch { 1 => ".pm", 2 => ".csv", 3 => ".md", _ => ".json" };
        var picker = new FileSavePicker { SuggestedFileName = $"PureProject-{DateTime.Now:yyyyMMdd-HHmm}" };
        picker.FileTypeChoices.Add(options.SelectedItem.ToString()!, new List<string> { extension });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync(); if (file is null) return;
        var path = file.Path; var snapshot = _projects.ToList();
        var saved = await RunTransferAsync("正在生成导出文件", token =>
        {
            if (index == 0) JsonProjectRepository.ExportBackupAsync(path, snapshot, token).GetAwaiter().GetResult();
            else if (current is not null)
            {
                var text = index switch { 1 => PmSerializer.ExportPm(current), 2 => ExportCsv(current), _ => ExportMarkdown(current) };
                AtomicFile.WriteAsync(path, text, cancellationToken: token).GetAwaiter().GetResult();
            }
            return path;
        });
        if (saved is not null) ShowMessage("已导出至 " + saved, InfoBarSeverity.Success);
    }

    private static string ExportCsv(Project p)
    {
        static string Cell(string? value)
        {
            var text = value ?? "";
            if (text.TrimStart().FirstOrDefault() is '=' or '+' or '-' or '@' || text.StartsWith('\t') || text.StartsWith('\r')) text = "'" + text;
            return "\"" + text.Replace("\"", "\"\"") + "\"";
        }
        var output = new StringBuilder("\uFEFF标题,任务组,状态,优先级,截止日期,标签,说明\r\n");
        foreach (var task in p.Tasks)
            output.AppendLine(string.Join(",", new[] { task.Title, p.TaskGroups.FirstOrDefault(g => g.Id == task.TaskGroupId)?.Name,
                Status(p, task)?.Name, PriorityText(task.Priority), task.DueDate, string.Join("; ", task.Tags), task.Description }.Select(Cell)));
        return output.ToString();
    }
    private static string ExportMarkdown(Project p)
    {
        static string Escape(string text) => text.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
        var output = new StringBuilder($"# {Escape(p.Name)}\n\n{p.Description}\n\n导出时间：{DateTimeOffset.Now:yyyy-MM-dd HH:mm}\n\n");
        foreach (var group in p.TaskGroups.OrderBy(g => g.SortOrder))
        {
            output.AppendLine($"## {Escape(group.Name)}{(group.Archived ? "（归档）" : "")}\n\n| 任务 | 状态 | 优先级 | 截止日期 |\n|---|---|---|---|");
            foreach (var task in p.Tasks.Where(t => t.TaskGroupId == group.Id)) output.AppendLine($"| {Escape(task.Title)} | {Escape(Status(p, task)?.Name ?? "")} | {PriorityText(task.Priority)} | {task.DueDate ?? "—"} |");
            output.AppendLine();
        }
        return output.ToString();
    }

    private async Task SyncAsync()
    {
        if (_busy || _repository is null || _settingsRepository is null) return;
        if (string.IsNullOrWhiteSpace(_settings.SyncServerUrl)) throw new InvalidOperationException("请先填写同步服务器地址。");
        _busy = true;
        SetSaveStatus("正在同步…");
        try
        {
            using var client = new RestSyncClient();
            var result = await Task.Run(() => new ManualSyncService(client).SyncAsync(_projects, _settings));
            var before = _projects;
            await Task.Run(() => _repository.SaveAsync(result.Projects));
            _undo.Push(before); _redo.Clear(); _projects = result.Projects; TrimHistory();
            _syncConflicts = result.Conflicts;
            Render();
            try { await _settingsRepository.SaveAsync(result.Settings); _settings = result.Settings; }
            catch (Exception ex) { throw new IOException("项目数据已保存，但同步版本记录未能保存。下次同步将重新核对，请勿重复覆盖：" + ex.Message, ex); }
            ShowMessage($"同步完成：上传 {result.Uploaded} 个、下载 {result.Downloaded} 个" + (_syncConflicts.Count > 0 ? $"，有 {_syncConflicts.Count} 个冲突待处理。" : "。"), _syncConflicts.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
            SetSaveStatus("同步完成 · " + DateTime.Now.ToString("HH:mm:ss"));
        }
        catch { SetSaveStatus("同步未完成 · 本地数据已保留"); throw; }
        finally { _busy = false; }
        if (_syncConflicts.Count > 0) await ResolveConflictsAsync();
    }

    private async Task ResolveConflictsAsync()
    {
        foreach (var conflict in _syncConflicts.ToList())
        {
            var local = _projects.FirstOrDefault(p => p.Id == conflict.Id);
            if (local is null || conflict.Deleted || conflict.ServerData is null)
            {
                ShowMessage("存在删除或缺失项目的同步冲突。请先导出备份，并核对服务器与本地项目：" + (local?.Name ?? conflict.Id), InfoBarSeverity.Warning);
                continue;
            }
            var remote = PmSerializer.ParsePm(conflict.ServerData);
            if (remote.Id != conflict.Id) throw new InvalidDataException("服务器冲突数据的项目 ID 不一致，已拒绝覆盖。");
            var panel = Column(); panel.Children.Add(Label($"项目：{local.Name}\n本地：{local.Tasks.Count} 项任务，更新于 {local.UpdatedAt}\n服务器：{remote.Tasks.Count} 项任务，更新于 {remote.UpdatedAt}\n\n选择要保留的版本。覆盖前会保存本地历史。", true));
            var result = await DialogAsync(new ContentDialog { Title = "处理同步冲突", Content = panel, PrimaryButtonText = "保留本地并上传", SecondaryButtonText = "采用服务器版本", CloseButtonText = "稍后处理", DefaultButton = ContentDialogButton.Close });
            if (result == ContentDialogResult.None) continue;
            if (result == ContentDialogResult.Primary)
            {
                _busy = true;
                try
                {
                    using var client = new RestSyncClient();
                    var push = await client.PushAsync(_settings, local, conflict.ServerRev);
                    if (push.Conflict is not null) { ShowMessage("服务器版本再次变化，请重新同步后处理。", InfoBarSeverity.Warning); continue; }
                    await RecordSyncBaselineAsync(local, push.Rev!.Value);
                }
                finally { _busy = false; }
            }
            else
            {
                await CommitAsync(projects => projects[projects.FindIndex(p => p.Id == local.Id)] = remote, "已应用服务器版本", collectionOnly: true);
                await RecordSyncBaselineAsync(remote, conflict.ServerRev);
            }
            _syncConflicts.Remove(conflict);
        }
        // Establish revisions and hashes from the server after the explicit resolution.
        if (_syncConflicts.Count == 0) ShowMessage("冲突已处理；下次同步将重新核对版本。", InfoBarSeverity.Success);
    }

    private async Task RecordSyncBaselineAsync(Project project, long revision)
    {
        var settings = _settings with
        {
            SyncStateServerUrl = RestSyncClient.ValidateServerUri(_settings.SyncServerUrl).AbsoluteUri,
            SyncRevisions = new Dictionary<string, long>(_settings.SyncRevisions) { [project.Id] = revision },
            SyncedProjectHashes = new Dictionary<string, string>(_settings.SyncedProjectHashes) { [project.Id] = ManualSyncService.Hash(project) }
        };
        if (_settingsRepository is null) throw new InvalidOperationException("设置存储未就绪。");
        await _settingsRepository.SaveAsync(settings);
        _settings = settings;
    }
}
