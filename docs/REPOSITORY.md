# 简项独立仓库说明

拆分日期：2026-10-07。

简项 PureProject 2.0.1 Windows 原生客户端现在由 `jianxiang` 独立目录维护。该目录拥有自己的 `.git`、`main` 分支、解决方案、构建入口和 Windows CI，未配置远程仓库。

## 目录对应

| 用途 | 本机位置 |
|---|---|
| 简项当前开发仓库 | `C:\Users\34890\Desktop\jianxiang` |
| 旧版 ProjectManager 工程 | `C:\Users\34890\Desktop\porjectmanagement` |
| 原简项源码副本 | 旧工程的 `winui/`，仅保留作为拆分时参考 |
| 旧 ProjectManager 发布副本 | 旧工程的 `github-publish/` |
| 墨渡及历史简项混合发布副本 | 旧工程的 `github-publish-modu/` |

后续简项变更在新仓库进行。旧工程及其 Git 发布副本未移动或删除；旧版 Tauri、历史版本、用户数据与压测原始记录仍保留在原处。

## 来源与范围

迁入文件来自旧工程 `winui/`。拆分前核对了历史发布仓库跟踪的 240 个 WinUI 文件，全部与本机文件字节一致。对应历史仓库为 `https://github.com/Hullayo/ModuForKindle.git`，提交为 `dcd5f03e4256f15e6cfb6c7e22897a1141e9f822`。

新仓库从这个已核对的源码快照建立独立提交；不复制混合仓库的 Git 历史、远程地址或墨渡代码。WinUI 子目录平铺至仓库根目录，另迁入简项自己的 CI 并调整路径。

本次调整包括 README、构建与测试操作路径、候选包的 Git 源码校验路径、CI 的运行根目录，以及将历史图标提取器的外部输入改为显式参数。应用名称、程序集、用户数据路径、业务实现和兼容性契约沿用现有简项原生版。

许可证与固定测试样本随源码保存。旧 CI、PR 和性能报告链接保持原始地址，作为历史证据，不代表新仓库已经在线发布。

## 本机构建环境

本机目录包含独立复制的 .NET SDK 10.0.401、NuGet 包和离线包源，均被 Git 忽略，没有指向旧目录的符号链接。将整个目录放在上述独立位置后，可执行：

```powershell
Set-Location C:\Users\34890\Desktop\jianxiang
.\build.ps1 -Task Restore -Offline
.\build.ps1 -Task Publish -Configuration Release -Offline
.\build.ps1 -Task Test -Configuration Release -Offline
```

从新的 Git checkout 开始时，按 README 先在线 Restore。源码本身不需要旧工程、Node.js 或 Rust；可选 REST 集成测试单独需要 Node.js。

本次构建与测试的详细记录保存在本机 `artifacts/repository-validation/`。实际验证结果记录于 [独立仓库验证](REPOSITORY_VALIDATION.md)。日常启动双击根目录 `run.cmd`，默认使用本仓库发布输出。
