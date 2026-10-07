# 简项候选包的冻结与打包

`package-release.ps1` 只负责把已经验证并冻结的 WinUI 发布目录封装为候选包。它不会编译、启动 GUI 或执行容量验收；性能与功能结论以 `STRESS_REMEDIATION_20261006.md` 和 `SUPPORT_MATRIX.md` 为准。脚本可在 Windows PowerShell 5.1 或 PowerShell 7 中运行，需要 Git。

GitHub Actions 会构建、执行控制台回归并验证发布目录所需文件，但只上传验证日志和文件哈希清单，不上传应用二进制。CI 绿色结果不能绕过原生界面或性能阻断；可下载候选必须另行通过本文打包流程。清单、原始压测日志和本机 `artifacts/` 不随源码 checkout 分发，示例中的路径须替换为本次构建实际保存的位置。

构建使用已经提交的 SVG、图标和字体，不运行资源再生成脚本。`src/PureProject.WinUI/Assets/Icons/Generate.ps1` 是可选的历史提取工具，本仓库未附原 ProjectManager 的 `v1.2` 组件。只有重新提取图标时才需要另备原始 `Icon.svelte` / `Sidebar.svelte`，并使用必填参数 `-Source` / `-SidebarSource` 指定文件；脚本不查找相邻旧仓库。普通构建和测试不依赖它们。Core、Exchange 和 Infrastructure 的运行样本已随各测试工程的 `Fixtures` 提交。

## 输入与版本

以下命令在简项独立仓库根目录执行。版本从 `src/PureProject.WinUI/PureProject.WinUI.csproj` 读取。打包必须显式提供生产冻结清单和完整源码提交，不接受空提交或仅凭当前 HEAD 猜测来源。`-SourceRepository` 默认指向脚本所在的独立仓库根目录；只有使用另一份简项 checkout 核验时才需要覆盖。

```powershell
.\package-release.ps1 `
  -PublishDirectory 'C:\build\PureProject\publish' `
  -OutputDirectory 'C:\build\PureProject\release-candidate-01' `
  -FreezeManifest 'C:\build\PureProject\production-freeze-final.json' `
  -SourceRepository 'C:\src\jianxiang' `
  -SourceCommit '<与冻结源码完全一致的完整 Git commit SHA>' `
  -ValidateOnly
```

`-ValidateOnly` 通过后，移除该开关，用同一组输入执行打包。输出目录必须尚不存在；脚本不会覆盖旧包。生产发布目录、输出目录和冻结清单应相互独立，输出目录不能位于发布目录内。打包脚本所在的 `src`、独立仓库源码提交中的 `src`、冻结清单的源码哈希必须完全一致。旧混合仓库中仅有 `winui/src` 的提交不能直接作为新目录的打包来源。

冻结清单的必要结构如下。`sourceFiles` 的路径相对于 `src`，`binaries` 的路径相对于发布目录；路径使用 `/`，SHA-256 为 64 位十六进制字符串。下面只示意结构，实际清单须包含全部文件。

```json
{
  "schemaVersion": 1,
  "kind": "pureproject-production-freeze",
  "productVersion": "2.0.1",
  "assemblyVersion": "2.0.1.0",
  "sourceFiles": {
    "PureProject.Core/Models.cs": "<SHA-256>"
  },
  "binaries": [
    { "path": "PureProject.exe", "bytes": 123456, "sha256": "<SHA-256>" }
  ]
}
```

应在生产编译、必要的原生界面验收完成后生成最终清单，并保留构建日志、测试证据、稳定的源码清单副本。源码有变化时，须重新构建和冻结；不能用更新后的源码清单与旧二进制并列充当同一构建的证明。清单哈希和 Git 对照建立文件来源链，不能替代编译器证明或运行验收。

## 门禁与输出

打包前逐项核验以下内容：

- 当前生产源码的完整文件集合和 SHA-256（排除构建生成的 `bin`、`obj`）。
- 发布目录的完整文件集合、字节数和 SHA-256，以及必要的 EXE、DLL、.NET/WinUI 运行时、PRI、XBF、图标、字体及字体许可。
- 主程序集版本、产品版本、项目版本与冻结版本一致。
- 指定 Git 提交中 `src` 的文件集合与源码一致；Git blob 使用原始字节计算 SHA-256，不经过 PowerShell 文本换行转换。
- 修复报告、支持矩阵、软件 MIT 和字体许可均存在。输入清单不接受越界路径或重解析点。

全部通过后，脚本才创建独立 `staging`，复制并复核发布文件。`release-info/` 内附候选状态、报告、支持矩阵、独立许可和 `release-manifest.json`。该发布清单记录源码提交、冻结清单 SHA-256、生产文件和包内附加文件的哈希；清单不把自身列入 `packageFiles`，避免自引用哈希。

ZIP 先写为 `.partial`，随后逐条解压读取，核对每个文件的长度和 SHA-256。核验通过才改名为最终 `PureProject-<版本>-win-x64-candidate.zip`，并生成 `SHA256SUMS.txt`。输出目录另存同一份 `release-manifest.json`，`staging` 保留供复核。失败会保留独立输出以便排查；再次运行应选择新目录。

包内标记保持 `candidate`。冻结与打包成功仅表示来源、版本与归档完整性通过，不能把尚未完成的全字段满长 100k、2+8 小时长稳或原生交互项目改记为通过。发布时仍应带上报告中真实的已验证范围、失败与限制。
