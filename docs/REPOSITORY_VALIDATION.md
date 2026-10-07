# PureProject 构建与验证记录

验证日期：2026-10-07。应用源码提交：[`d0228b180ccda0f403c7e72fbab2422deeb5e66c`](https://github.com/Hullayo/PureProject/commit/d0228b180ccda0f403c7e72fbab2422deeb5e66c)，产品版本：2.0.1，平台：Windows x64。

本记录汇总当前应用的本地构建与回归结果。文档调整不改变此处测试对应的源码提交；GitHub Actions 的结果以仓库中的实际运行记录为准。

| 检查 | 结果 |
|---|---|
| Windows x64 Release Publish | 通过；已构建 `run.cmd` 使用的自包含发布目录 |
| Core | 42/42 通过，包含 64/1024/20 字输入边界、Unicode 字符计数、保存原子性与已有数据保留 |
| Exchange | 67/67 通过 |
| Infrastructure | 26 项通过，包含当前 Windows 用户上下文中的 DPAPI 往返 |
| Storage | 17 项通过、1 项跳过；跳过原因为缺少 Windows 符号链接权限 |
| 100,000 任务保存与重开 | 项目指纹完全一致；该结果只覆盖此合成数据用例 |
| 原生界面完整回归 | 最终发布构建 44 步通过，包含输入长度校验、编辑保存、撤销重做、任务操作、主题和 1024×768 界面 |
| 界面外观 | 检查了浅色、深色任务编辑器与项目设置中的字数提示 |

## 验证方式与范围

Core、Exchange、Infrastructure 和 Storage 使用仓库内的控制台自检。原生界面回归使用实际 WinUI 控件与 `ButtonAutomationPeer/IInvokeProvider`，在全新隔离数据目录运行。

文本专项覆盖边界输入、超限替换、组合表情与扩展区汉字、已有超长内容、分页编辑及粘贴处理函数。未直接操作系统剪贴板，也未以真实中文输入法键盘操作进行人工回归，不能把程序内控件检查等同于这两类验证。

本次没有完成新的十小时长稳、所有 DPI 组合、全部读屏入口或真实磁盘满卷测试。普通文本合成数据的结果不构成任意满长数据或持续运行性能的承诺。当前容量契约与待验证项目见 [支持范围](SUPPORT_MATRIX.md)。

## 本地结果

- 最终程序：`artifacts/publish/win-x64-2.0.1/PureProject.exe`。
- 最终原生回归：`artifacts/text-input-validation/native-final/ui-smoke-result.json`。
- 该目录同时包含步骤日志、隔离测试数据与界面截图。

`artifacts/` 属于本地生成内容，不随源码提交。重新验证请按 [开发指南](REPOSITORY.md) 和测试工程的 README 执行。
