# 简项 · PureProject — Windows 原生版

简项的 C# / WinUI 3 客户端，目标版本 **2.0.1**。运行不依赖 WebView、Node.js 或 Rust。本目录是简项的独立 Git 仓库 `jianxiang`，源码、测试、构建和发布入口都位于仓库内部。旧版 ProjectManager（Svelte / Tauri）保留在原 `porjectmanagement` 目录；墨渡 InkFerry 是另一个应用。简项使用独立数据目录。

本次变更修复压测发现的交换文件完整性、输入契约、进度排序及密集日历问题，并改造长文本编辑、视图释放和历史内存预算。**修复后的长稳性能仍需重新验证，不能把旧版 100k 普通文本通过的局部测试当作全字段满长或十小时长稳通过。** 进度见 [修复与发布验证](docs/STRESS_REMEDIATION_20261006.md)，容量边界见 [支持矩阵](docs/SUPPORT_MATRIX.md)。

## 获取与运行

支持 Windows 10 2004（19041）及更新版本，x64；推荐 Windows 11。发布包是自包含 ZIP：解压整个目录后运行 `PureProject.exe`。必须保留同目录 DLL、XAML 资源及 Assets，不能仅复制一个 EXE。当前没有 MSIX / 安装向导、后台服务或自动升级功能。

从源码运行，在仓库根目录双击 `run.cmd`。启动脚本优先使用 `artifacts/publish/win-x64-2.0.1` 中的新构建。

## 构建与测试

固定 .NET SDK `10.0.401`、Windows App SDK `1.8.260921001`。首次运行 `build.ps1` 会下载经过 SHA512 校验的便携 SDK，NuGet 依赖和缓存保存在本目录。Git 跟踪的源码不包含 SDK、缓存和压测数据。本机独立目录已复制一套 SDK 和依赖缓存，可在上述命令中加入 `-Offline` 使用。

```powershell
# 在 jianxiang 仓库根目录执行
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Task Restore
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Task Build -Configuration Release
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Task Test -Configuration Release
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Task Publish -Configuration Release
```

默认发布至 `artifacts/publish/win-x64-2.0.1`；可使用 `-PublishDirectory <独立输出目录>` 保留不同构建。`-Offline` 仅适用于已准备 SDK、NuGet 缓存与 `.tools/nuget-feed` 的环境。

测试工程使用控制台自检，非 `dotnet test`。Storage 默认包含 100k 合成规模用例；Infrastructure 的 DPAPI 测试需要正常 Windows 用户上下文。交换问题的小型旧格式复现文件已随 `tests/PureProject.Exchange.Tests/Fixtures` 提交，可脱离本机压测目录运行；详见该测试工程 README。

## 仓库边界

- `src/`：Core、Infrastructure 与 WinUI 客户端源码。
- `tests/`：四组控制台回归与固定测试样本。
- `tools/ScaleFixture/`：合成规模数据工具。
- `docs/`：兼容性、发布和历史修复说明。
- `.github/workflows/winui.yml`：独立仓库的 Windows CI。
- `artifacts/`：本机生成的构建、日志和测试输出，不纳入 Git。

本地目录拥有独立 `.git`，远程 `origin` 为 [Hullayo/PureProject](https://github.com/Hullayo/PureProject)（`https://github.com/Hullayo/PureProject.git`）。`main` 维护简项 WinUI 原生版；旧版 Tauri 保存在 [legacy/tauri-v1.2](https://github.com/Hullayo/PureProject/tree/legacy/tauri-v1.2) 分支。今后简项的开发与构建都从这里开始。拆分来源、旧目录对应关系与验证记录见 [独立仓库说明](docs/REPOSITORY.md)。

## 主要功能

- 项目、任务组、动态状态、任务的创建、编辑、归档和恢复；主要写入先校验，再原子保存。
- 看板、列表、日历、时间线、依赖图；列表和日期明细使用虚拟容器。
- 子任务、依赖、优先级、标签、日期、循环规则、工时记录与评论。
- `.pureproject`、`.xlsx`、`.mm` 可恢复交换格式，以及旧 `.pm` 和 JSON 导入；CSV/Markdown 用于展示，不作为完整备份。
- 手动 REST 同步、revision 防覆盖、冲突选择和 Windows DPAPI 令牌保护。
- 浅色 / 深色纸色主题、键盘导航、HarmonyOS Sans SC 字体与应用内提醒。

| 快捷键 | 行为 |
|---|---|
| Ctrl+N | 总览中新建项目；项目内新建任务 |
| Ctrl+F / Ctrl+Shift+F | 项目内 / 全局搜索 |
| Ctrl+, | 设置 |
| Ctrl+Z / Ctrl+Y | 撤销 / 重做 |

## 输入与显示

新建或实际修改的项目名最多 **64 个可见字符**，任务标题最多 **128 个可见字符**。组合字符和组合表情按 Unicode 字素计数，另保留现有 UTF-16 编码上限。64 个可见字符不等于 64 字节。超限会提示并保留输入；已存在的合法长标题在未修改时保留原文，导入和备份也不静默截断。

说明、评论和极长子任务标题采用分段编辑；保存时合并全文，取消时不提交。卡片、菜单及工具提示显示有界摘要。日历密集日期显示任务数及开始 / 截止数量，点击查看全部明细；数量和明细来自同一日期集合。仪表盘卡片与进度排序均按未归档任务组统计。

旧项目名和任务标题中的换行在编辑与保存时保留；顶部、侧栏和页面标题只显示单行摘要。项目名和重命名编辑框支持多行，Enter 用于换行，请用可见的保存按钮提交；长子任务多行编辑同样使用“+”提交。

撤销历史在进程内保存，受步数、任务版本数和估算模型字节预算共同约束。估算字节预算不等于整个进程的内存上限。

## 存储、恢复和兼容

默认数据目录为 `%LOCALAPPDATA%\PureProject\WinUI\`，包含 `projects.json`、`projects.json.bak`、设置文件和 `session.lock`。`PUREPROJECT_DATA_DIR` 可指定隔离目录。不会自动扫描或修改旧 Tauri / 浏览器数据。

保存使用同目录临时文件、校验、刷盘及原子替换。启动取得排他锁后，仅检查精确匹配本库命名的遗留暂存文件；有效或尚不能验证的非空内容作为恢复候选保留，不自动当作已提交版本。主库缺失且存在候选时拒绝悄悄建立空库。主库损坏或缺失但备份存在时，提示显式恢复并保留原件。

旧 schema v1/v2/v3 迁移到 v4；未知未来版本及错误引用拒绝导入。旧合法长标题可继续交换。新版 MM v2 对 Freeplane 会损坏的补充平面字符采用 `\u{HEX}` 可逆显示，字面反斜杠双写；完整元数据始终保存原文。旧 MM v1 出现无法区分原文与外部损坏的字符差异时明确拒绝，避免静默覆盖。Excel 旧文件只有换行规范化变化时保留规范载荷原文。

## 当前边界

原生库 256 MiB、单交换文件 256 MiB、ZIP 展开总量 512 MiB（包括清单）、单交换项目 64 MiB。超限明确拒绝，建议按项目分批备份。详见 [支持矩阵](docs/SUPPORT_MATRIX.md)。

旧版尚未迁移能力包括：JavaScript 插件、AI 工作流、独立流程图编辑、WebDAV / Gist / SSE、自动同步删除传播、后台系统通知，以及 Android / Web / macOS / Linux 客户端。原有 Tauri 功能清单不能视为原生版已实现范围。

源代码遵循 [MIT 许可证](LICENSE)，保留原 ProjectManager 版权声明。随软件提供的未修改 HarmonyOS Sans 字体另按 [字体许可](src/PureProject.WinUI/Assets/Fonts/LICENSE.txt)分发。
