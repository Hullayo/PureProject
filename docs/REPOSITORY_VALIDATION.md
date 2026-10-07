# 独立仓库验证

验证日期：2026-10-07。最终位置：`C:\Users\34890\Desktop\jianxiang`。

| 检查 | 结果 |
|---|---|
| 源码来源核对 | 原 `winui/` 的 240 个已跟踪文件与历史提交逐文件字节一致 |
| 独立 Git | 拆分验证时为独立 `.git` 和 `main`；后续接入 `https://github.com/Hullayo/PureProject.git`，旧版保存在 `legacy/tauri-v1.2` |
| 应用业务源码 | Core、Infrastructure、WinUI C# / XAML 未改动；仅资源再生成脚本与其说明调整外部输入 |
| 便携 SDK / 依赖 | 独立文件副本，无指向旧工程的重解析点；缓存不纳入 Git |
| 离线 Restore | 通过；搬到最终路径后再次通过 |
| Windows x64 Release Publish | 通过；搬到最终路径后重新构建并发布通过 |
| Core | 32/32，通过；最终目录再次通过 |
| Exchange | 67/67，通过 |
| Exchange 修复专项 | 31/31，通过 |
| Infrastructure | 26 项通过，包含当前 Windows 用户 DPAPI 往返 |
| Storage | 17/18 通过，1 项因 Windows 符号链接权限不足跳过；100,000 任务保存并重开指纹一致 |
| 规模工具 | 编译及 `--help` 入口通过，输出根目录定位不再回退旧 `winui/` |
| PowerShell | 根目录脚本及图标生成器语法解析通过 |
| 候选包来源校验 | 在最终目录用独立 Git 提交和新清单执行 `package-release.ps1 -ValidateOnly` 通过 |

Infrastructure 最初在受限执行上下文内无法使用用户 DPAPI；切换正常 Windows 用户上下文后同一组测试全部通过，未改动应用凭据实现。Storage 的符号链接分支仍保留为跳过，未记为通过。

本次验证用于确认仓库独立性、构建和现有回归。未执行新的原生 GUI、长时间压力测试、可选 Node.js REST 集成测试或 GitHub Actions；拆分验证阶段未创建或上传线上仓库与发布包；后续已按用户指定接入 PureProject 仓库，分支安排见 README。历史性能限制见原修复报告和支持矩阵。

本机详细日志和文件清单位于 `artifacts/repository-validation/`。发布程序为 `artifacts/publish/win-x64-2.0.1/PureProject.exe`，使用根目录 `run.cmd` 启动。
