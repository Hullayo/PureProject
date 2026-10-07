using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using PureProject.Core;
using PureProject.Infrastructure;

internal static class ServerIntegration
{
    public static async Task RunAsync()
    {
        var serverFile = FindServerFixture();
        var node = FindNodeExecutable();
        var scratchParent = Path.Combine(Path.GetTempPath(), "PureProject.ServerIntegration.Tests");
        var scratch = Path.Combine(scratchParent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var token = "isolated-integration-" + Guid.NewGuid().ToString("N");
        var settings = new AppSettings { SyncServerUrl = $"http://127.0.0.1:{port}", SyncToken = token };
        var start = new ProcessStartInfo(node)
        {
            WorkingDirectory = scratch, UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(serverFile);
        start.Environment["PM_SYNC_HOST"] = "127.0.0.1";
        start.Environment["PM_SYNC_PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["PM_SYNC_DATA"] = Path.Combine(scratch, "data");
        start.Environment["PM_REPORTS_DIR"] = Path.Combine(scratch, "reports");
        start.Environment["PM_SYNC_TOKEN"] = token;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动隔离同步服务器。");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            using var rawHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            rawHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            await WaitUntilReady(rawHttp, settings.SyncServerUrl, process, deadline.Token);
            using var client = new RestSyncClient();
            var service = new ProjectService();
            var original = service.CreateProject("协议集成自测", "只在隔离目录保存", "#A33B32");
            service.CreateTask(original, "验证现有 Node 同步服务");
            var first = await client.PushAsync(settings, original, 0, deadline.Token);
            Require(first.Succeeded && first.Rev > 0, "real server accepts initial project upload");
            var index = await client.ListAsync(settings, deadline.Token);
            Require(index.Projects.Single().Id == original.Id && index.Projects[0].SchemaVersion == 4,
                "real server index includes project and schema");
            var pulled = await client.PullAsync(settings, original.Id, deadline.Token);
            Require(pulled.Rev == first.Rev && ManualSyncService.Hash(pulled.Project) == ManualSyncService.Hash(original),
                "real server pull round trips project contents");

            var anotherClient = PmSerializer.Clone(original);
            anotherClient.Name = "另一客户端已更新";
            anotherClient.UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(1).ToString("O");
            var second = await client.PushAsync(settings, anotherClient, first.Rev!.Value, deadline.Token);
            Require(second.Succeeded && second.Rev > first.Rev, "real server advances revision on valid update");
            var localEdit = PmSerializer.Clone(original);
            localEdit.Description = "未同步的本地编辑";
            var stale = await client.PushAsync(settings, localEdit, first.Rev.Value, deadline.Token);
            Require(stale.Conflict?.Reason == "revision_conflict" && stale.Conflict.ServerRev == second.Rev,
                "real server rejects stale base revision with HTTP 409");
            var afterConflict = await client.PullAsync(settings, original.Id, deadline.Token);
            Require(afterConflict.Project.Name == anotherClient.Name, "stale upload does not overwrite remote data");

            var baseline = settings with
            {
                SyncStateServerUrl = RestSyncClient.ValidateServerUri(settings.SyncServerUrl).AbsoluteUri,
                SyncRevisions = new() { [original.Id] = first.Rev.Value },
                SyncedProjectHashes = new() { [original.Id] = ManualSyncService.Hash(original) }
            };
            var reconciled = await new ManualSyncService(client).SyncAsync([localEdit], baseline, deadline.Token);
            Require(reconciled.Conflicts.Single().Reason == "both_modified" && reconciled.Projects[0].Description == localEdit.Description,
                "real server reconciliation preserves concurrent local edits");

            // Simulate a future client against only this test server; production settings are never read.
            var future = JsonNode.Parse(PmSerializer.ExportPm(anotherClient))!.AsObject();
            future["schema_version"] = 5;
            using var upgrade = new HttpRequestMessage(HttpMethod.Put, $"{settings.SyncServerUrl}/api/projects/{Uri.EscapeDataString(original.Id)}");
            upgrade.Headers.Add("X-Base-Rev", second.Rev!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            upgrade.Content = new StringContent(future.ToJsonString(), Encoding.UTF8, "application/json");
            using var upgraded = await rawHttp.SendAsync(upgrade, deadline.Token);
            upgraded.EnsureSuccessStatusCode();
            var upgradeResult = JsonNode.Parse(await upgraded.Content.ReadAsStringAsync(deadline.Token))!.AsObject();
            var futureRevision = upgradeResult["rev"]!.GetValue<long>();
            await RequireRejectsFuture(() => client.PullAsync(settings, original.Id, deadline.Token), "real server future schema is rejected on pull");
            await RequireRejectsFuture(() => client.ListAsync(settings, deadline.Token), "real server future schema is rejected in index");
            var downgrade = await client.PushAsync(settings, original, futureRevision, deadline.Token);
            Require(downgrade.Conflict?.Reason == "schema_downgrade", "real server rejects schema v4 overwriting schema v5");
            Console.WriteLine("Real server integration: 10 checks passed (isolated loopback server).");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            _ = await stdout;
            _ = await stderr;
            var expectedParent = Path.GetFullPath(scratchParent);
            if (Path.GetDirectoryName(Path.GetFullPath(scratch)) == expectedParent && Directory.Exists(scratch))
                Directory.Delete(scratch, recursive: true);
        }
    }

    private static async Task WaitUntilReady(HttpClient client, string server, Process process, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            if (process.HasExited) throw new InvalidOperationException("隔离同步服务器提前退出。");
            try
            {
                using var response = await client.GetAsync(server + "/api/health", cancellationToken);
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            await Task.Delay(100, cancellationToken);
        }
        throw new TimeoutException("隔离同步服务器未能启动。");
    }

    private static string FindServerFixture()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "Fixtures", "server");
        var path = Path.Combine(directory, "pm-sync-server.mjs");
        if (!File.Exists(path)) throw new FileNotFoundException("测试输出缺少固定同步服务器样本；请完整复制 Fixtures 目录。", path);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "manifest.json")));
        using var stream = File.OpenRead(path);
        var hash = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(hash, manifest.RootElement.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase)
            || stream.Length != manifest.RootElement.GetProperty("bytes").GetInt64())
            throw new InvalidDataException("固定同步服务器样本与来源清单不一致，已停止集成测试。");
        return path;
    }

    private static string FindNodeExecutable()
    {
        var explicitPath = Environment.GetEnvironmentVariable("PUREPROJECT_NODE_PATH");
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var fullPath = Path.GetFullPath(explicitPath);
            if (File.Exists(fullPath)) return fullPath;
            throw new FileNotFoundException("PUREPROJECT_NODE_PATH 指定的 Node.js 可执行文件不存在。", fullPath);
        }
        var executable = OperatingSystem.IsWindows() ? "node.exe" : "node";
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var directory = entry.Trim().Trim('"');
            if (directory.Length == 0) continue;
            try
            {
                var candidate = Path.GetFullPath(Path.Combine(directory, executable));
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
            catch (NotSupportedException) { }
        }
        throw new FileNotFoundException("--integration 需要 Node.js；请将 node 加入 PATH，或将 PUREPROJECT_NODE_PATH 设为其可执行文件路径。基础 suite 不需要 Node.js。");
    }

    private static void Require(bool condition, string description)
    { if (!condition) throw new InvalidOperationException("FAILED: " + description); Console.WriteLine("PASS " + description); }

    private static async Task RequireRejectsFuture(Func<Task> operation, string description)
    {
        try { await operation(); }
        catch (InvalidDataException) { Require(true, description); return; }
        throw new InvalidOperationException("FAILED: " + description);
    }
}
