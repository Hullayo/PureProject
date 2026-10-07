# Infrastructure 回归测试

基础 suite 使用合成临时库、模拟 HTTP 响应和 Windows DPAPI，使用 .NET SDK 10.0.401。它不需要 Node.js，也不会连接实际同步服务器。DPAPI 检查应在正常 Windows 用户上下文运行。

在 `jianxiang` 仓库根目录运行。首次取得源码时先用 `build.ps1 -Task Restore` 准备便携 SDK 和依赖；源码仓库不附带 `.tools` 运行时。如果已经安装 SDK 10.0.401，也可以将下面的 `.\.tools\dotnet\dotnet.exe` 换成 `dotnet`。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Task Restore
.\.tools\dotnet\dotnet.exe run --project tests/PureProject.Infrastructure.Tests -c Release
```

可选 `--integration` 会启动测试输出中 `Fixtures/server/pm-sync-server.mjs` 的固定协议服务器，在回环地址、随机端口和隔离临时数据目录验证真实同步服务。样本来自既有 ProjectManager 服务器，保持原始字节，随附 MIT 许可、来源与 SHA-256；启动前会核对大小和哈希。即使将本仓库放在独立目录，或复制完整测试输出目录，这项测试也不需要仓库根目录中的 `server/`。

仅此模式需要可用的 Node.js。优先读取 `PUREPROJECT_NODE_PATH` 指定的可执行文件；未设置时从 `PATH` 查找 `node.exe`（Windows）或 `node`。不会依赖本机旧版的 `v1.2/runtime` 路径。

```powershell
# Node.js 已在 PATH 中时：
.\.tools\dotnet\dotnet.exe run --project tests/PureProject.Infrastructure.Tests -c Release -- --integration

# 如需显式指定，先将环境变量设为你的 Node.js 可执行文件绝对路径：
$env:PUREPROJECT_NODE_PATH = (Get-Command node.exe).Source
```

明确设置的 `PUREPROJECT_NODE_PATH` 不存在时直接报错；不会静默换用另一份运行时。集成进程退出后清理其隔离数据，不使用应用的默认数据目录。
