# PureProject · 简项

**一款面向 Windows 的本地项目与任务管理应用。**

PureProject 用项目和任务组整理工作，在看板、列表、日历、时间线与依赖图中呈现进度。把任务拆成子任务，安排截止日期与里程碑，再用标签、评论和工时记录跟进执行。数据默认保存在本机，可按需导出备份或开启自动备份。

[快速开始](#快速开始) · [开发指南](docs/REPOSITORY.md) · [支持范围](docs/SUPPORT_MATRIX.md) · [反馈问题](https://github.com/Hullayo/PureProject/issues)

## 主要功能

- **组织项目**：管理项目、任务组与自定义状态，支持归档和恢复。
- **安排任务**：设置优先级、标签、截止日期、子任务、依赖关系、循环规则与里程碑。
- **跟进执行**：记录工时与评论，通过项目内搜索、全局搜索和筛选查找工作。
- **管理数据**：导入、导出项目，保存备份，并在数据异常时按提示恢复。
- **自动备份**：应用运行期间按选定间隔保存项目快照，按项目文件分别保留历史备份。
- **桌面交互**：跟随系统、浅色与深色主题，可自定义键盘快捷键，支持撤销与重做，以及应用运行期间的提醒。

## 从不同视图安排工作

| 视图 | 用途 |
|---|---|
| 看板 | 按状态查看任务，跟进每个阶段的工作 |
| 列表 | 集中查看任务属性，搜索、筛选和调整顺序 |
| 日历 | 按日期查看任务，展开密集日期的任务明细 |
| 时间线 | 查看任务时间安排与里程碑 |
| 依赖图 | 梳理任务之间的前后关系 |

## 快速开始

支持 **Windows 10 2004（19041）及更新版本，x64**，推荐 Windows 11。当前主线版本为 **2.0.1**，使用 C#、WinUI 3 与 .NET 构建。

### 从源码构建

在 Windows PowerShell 中执行：

```powershell
git clone https://github.com/Hullayo/PureProject.git
cd PureProject
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Task Restore
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Task Publish -Configuration Release
.\run.cmd
```

首次构建会准备固定版本的便携 .NET SDK，并下载依赖，需要联网。构建完成后，双击仓库根目录的 `run.cmd` 即可启动。

### 运行构建产物

默认输出目录为 `artifacts/publish/win-x64-2.0.1/`，运行其中的 `PureProject.exe`。分发或移动程序时，请保留整个目录，包括 DLL、XAML 资源和 `Assets`。发布目录包含所需运行时，使用者无需另外安装 .NET。

当前提供自包含目录构建；安装向导、自动升级和其他平台客户端尚未提供。打包方式见 [发布与打包](docs/PACKAGING.md)。

## 文本输入

| 输入类别 | 上限 | 适用字段 |
|---|---:|---|
| 标题类 | **64 个字符** | 项目名称、任务标题、任务组名称、子任务标题、标签名称、里程碑名称 |
| 长文本类 | **1,024 个字符** | 项目说明、任务说明、里程碑说明、评论 |
| 状态类 | **20 个字符** | 任务状态名称 |

按 Unicode 字素（用户看到的完整字符）计数：一个汉字、扩展区汉字或组合表情均计一个字符。输入框显示当前字数与上限；超限输入或粘贴会被拒绝，并保留原有内容。保存时会再次校验。

已保存或导入的合法超长内容可以原样保留；修改后须符合对应的输入上限。具体数据契约见 [支持范围](docs/SUPPORT_MATRIX.md)。

## 数据与备份

项目数据默认保存在 `%LOCALAPPDATA%\PureProject\WinUI\`。应用使用原子保存，保留 `projects.json.bak` 备份，并提供异常恢复提示。需要隔离数据时，可通过 `PUREPROJECT_DATA_DIR` 指定目录。

- `.pureproject`、`.xlsx`、`.mm` 支持项目导出与还原；通过 Excel 或思维导图软件编辑后再导入时，需保留文件中的 ID、结构与元数据。
- 支持 `.pm` 与 JSON 数据导入。
- CSV 和 Markdown 用于阅读、整理及分享，不作为完整备份格式。
- 设置中的“自动备份”默认关闭，间隔默认 5 分钟，可选择 1m、5m、10m、15m、30m、1h 或 2h。关闭后停止定时备份，时间选项禁用。
- 自动备份保存在软件安装目录的 `backup/` 下，按项目名称与稳定标识建立文件夹，保存带时间戳的 `.pureproject` 完整快照；同名项目分别保存，项目重命名后沿用原备份目录。应用须保持运行，安装目录须可写。
- 在“设置 → 数据管理”中导出备份或选择备份文件恢复。恢复前会显示新增与替换的项目范围。

文件大小、任务数量和视图显示范围有明确限制，大型项目建议分批备份。容量上限与已验证范围见 [支持范围](docs/SUPPORT_MATRIX.md)。

## 常用快捷键

下表为默认组合，可在“设置 → 快捷键”中按键录制新组合、保存或恢复默认；重复组合会提示冲突。文本输入框保留自身编辑快捷键。

| 快捷键 | 操作 |
|---|---|
| Ctrl+N | 总览中新建项目；项目内新建任务 |
| Ctrl+F | 项目内搜索 |
| Ctrl+Shift+F | 全局搜索 |
| Ctrl+, | 打开设置 |
| Ctrl+Z / Ctrl+Y | 撤销 / 重做 |
| Ctrl+Shift+Z | 重做（备用） |
| Esc | 关闭面板 |

## 开发与反馈

- [开发指南](docs/REPOSITORY.md)：代码结构、环境准备、构建与测试。
- [支持范围](docs/SUPPORT_MATRIX.md)：平台、输入规则、容量与验证边界。
- [构建与验证记录](docs/REPOSITORY_VALIDATION.md)：当前应用的本地验证结果。
- [发布与打包](docs/PACKAGING.md)：发布目录校验与 ZIP 打包流程。

欢迎通过 [Issues](https://github.com/Hullayo/PureProject/issues) 反馈问题或提出建议。报告问题时，请提供系统版本、应用版本、复现步骤与预期结果。

## 许可证

源代码采用 [MIT 许可证](LICENSE)。随应用提供的 HarmonyOS Sans 字体适用独立的 [字体许可](src/PureProject.WinUI/Assets/Fonts/LICENSE.txt)。
