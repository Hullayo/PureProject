# PureProject 开发约定

本仓库维护 PureProject（简项）Windows 桌面应用，解决方案为根目录 `PureProject.sln`。

- 开发、构建、测试和修复均在本仓库完成。
- 源码位于 `src/`。构建与打包脚本通过自身目录定位输入，保持仓库可独立检出和构建。
- `build.ps1 -Task Restore/Build/Test/Publish` 是本地入口，详见 README。测试为控制台自检，不使用 `dotnet test`。
- `.tools/`、`.nuget/`、`.dotnet-home/`、`artifacts/`、`bin/` 和 `obj/` 是本机生成内容，不提交。
- 保留固定测试样本、许可证及对应字节哈希；验证记录中的来源链接与原始证据保持准确。
- 默认远程 `origin` 为 `https://github.com/Hullayo/PureProject.git`，`main` 维护当前应用。推送前核对远程地址与工作区状态。
- 对外介绍围绕 PureProject 当前已实现的功能、平台和使用方式撰写，性能结论以实际验证结果为准。
