# 简项独立仓库

本仓库维护简项 PureProject 的 Windows 原生客户端，解决方案为根目录 `PureProject.sln`。

- 开发、构建和修复均在本仓库完成，不回写旧 `porjectmanagement`、`github-publish` 或 `github-publish-modu` 目录。
- 源码在 `src/`，不再使用 `winui/` 前缀。构建和打包脚本通过自身目录定位输入，不依赖旧工程目录。
- `build.ps1 -Task Restore/Build/Test/Publish` 是本地入口，详见 README。测试为控制台自检，不使用 `dotnet test`。
- `.tools/`、`.nuget/`、`.dotnet-home/`、`artifacts/`、`bin/` 和 `obj/` 是本机生成内容，不提交。
- 保留固定测试样本、许可证及对应字节哈希；历史验证记录中的仓库链接只作为来源证据。
- 简项默认远程 origin 为 https://github.com/Hullayo/PureProject.git，main 维护 WinUI 原生版，legacy/tauri-v1.2 保留旧版 Tauri。不得把墨渡或旧 ProjectManager 的其他仓库当作默认推送目标；历史托管链接只作来源证据。
