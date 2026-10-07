# PureProject 开发指南

PureProject（简项）使用 C#、.NET 10 和 WinUI 3 开发，当前应用版本为 **2.0.1**。源码、测试、构建脚本和发布工具均位于 [Hullayo/PureProject](https://github.com/Hullayo/PureProject) 仓库。

## 获取源码

在 Windows x64 环境中安装 Git，然后克隆仓库：

```powershell
git clone https://github.com/Hullayo/PureProject.git
Set-Location PureProject
```

应用支持 Windows 10 2004（19041）及更新版本，推荐 Windows 11。建议选择较短的本地检出路径，避免 Windows 构建工具遇到路径长度限制。

## 仓库结构

| 路径 | 用途 |
|---|---|
| `PureProject.sln` | 解决方案入口 |
| `src/PureProject.Core/` | 项目与任务模型、业务规则、输入校验 |
| `src/PureProject.Infrastructure/` | 本地存储、数据交换与同步 |
| `src/PureProject.WinUI/` | Windows 客户端、界面与应用资源 |
| `tests/` | Core、Storage、Infrastructure、Exchange 四组控制台自检及固定样本 |
| `tools/ScaleFixture/` | 合成规模数据工具 |
| `docs/` | 开发、支持边界、打包和验证文档 |
| `.github/workflows/winui.yml` | Windows CI |
| `build.ps1` / `run.cmd` | 本地构建与启动入口 |
| `package-release.ps1` | 校验冻结目录并生成候选发布包 |

`.tools/`、`.nuget/`、`.dotnet-home/`、`artifacts/`、`bin/` 和 `obj/` 是本机生成内容，不提交到 Git。

## SDK 与构建

仓库固定使用 .NET SDK **10.0.401**、Windows App SDK **1.8.260921001**。首次执行 `build.ps1` 时会下载并校验便携 .NET SDK，SDK、NuGet 依赖和缓存保存在仓库目录内。

在仓库根目录执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Task Restore
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Task Build -Configuration Release
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Task Test -Configuration Release
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Task Publish -Configuration Release
```

默认发布目录为 `artifacts/publish/win-x64-2.0.1`。需要保留多个构建时，通过 `-PublishDirectory <输出目录>` 指定独立目录。

SDK、NuGet 缓存与 `.tools/nuget-feed` 齐备后，可在上述命令中加入 `-Offline`。新克隆的仓库应先在线恢复依赖。应用构建和运行不需要 Node.js；可选 REST 集成测试需要 Node.js，CI 使用 Node.js 22。

## 运行与数据隔离

在仓库根目录双击 `run.cmd`，启动脚本会优先使用默认发布目录中的程序。也可以通过 `build.ps1 -Task Run -Configuration Release` 构建并启动，或直接运行发布目录中的 `PureProject.exe`。

发布目录包含 .NET、WinUI 运行时和应用资源，分发时须保留整个目录。

默认数据目录为 `%LOCALAPPDATA%\PureProject\WinUI\`。进行手工回归或调试时，可在启动前设置 `PUREPROJECT_DATA_DIR`，将测试数据写入独立目录：

```powershell
$env:PUREPROJECT_DATA_DIR = Join-Path (Get-Location) 'artifacts/manual-test-data'
.\build.ps1 -Task Run -Configuration Release
```

## 测试与 CI

测试工程使用控制台自检，由 `build.ps1 -Task Test` 运行，不使用 `dotnet test`。Storage 默认包含 100,000 任务的合成规模用例；Infrastructure 的 DPAPI 测试需要正常 Windows 用户上下文。固定样本随相应测试工程提交，详见各测试工程 README。

GitHub Actions 执行构建、控制台回归、REST 集成测试与发布文件检查，并上传日志和文件哈希清单。CI 不分发应用二进制，也不替代原生界面、输入法、外部软件交互或长稳验收。

候选包的生成要求见 [冻结与打包](PACKAGING.md)，容量与验证范围见 [支持边界](SUPPORT_MATRIX.md)。

## 贡献代码

从 `main` 创建工作分支，围绕具体问题提交变更。涉及行为调整时补充相应的控制台回归或界面验证，并在 Pull Request 中说明问题、预期行为和实际验证结果。

请保留许可证、字体许可和固定测试样本。涉及存储、交换或同步契约的变更，应同步检查已有数据的读取与还原，并更新对应文档。构建产物、用户数据和同步凭据不纳入提交。
