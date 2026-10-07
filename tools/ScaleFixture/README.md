# 独立规模审查数据

生成 10 个项目 × 10 个任务组 × 10 个状态 × 20 个任务，共 20,000 个任务。生成器直接引用应用的 Core 与 Infrastructure，不使用外部包；写入前通过 `PmSerializer` 校验，写入后用 `JsonProjectRepository` 与 `SettingsRepository` 重新读取并核对计数、全局唯一 ID、引用与序列化往返。

使用已安装的 .NET SDK 10.0.401，在仓库根目录执行：

```powershell
dotnet run --project tools/ScaleFixture/PureProject.ScaleFixture.csproj -c Release -- --profile 20k
```

也可以先按主 README 用 `build.ps1 -Task Restore` 准备本地 SDK，再以 `.\.tools\dotnet\dotnet.exe` 替换 `dotnet`。`generate-scale.ps1` 是原本地审查环境的快捷入口，使用 `.tools/nuget-feed` 作为包源；该离线目录不随源码分发。普通 checkout 优先使用上述直接命令，参数 `--date`、`--verify`、`--replace` 分别对应脚本的 `-ReferenceDate`、`-VerifyOnly`、`-Replace`。

数据只写到 `artifacts/scale-audit/data`，设置为 Dark。项目与任务 ID 以及标签、子任务、评论、里程碑 ID 均以 `scale-` 开头。日期相对生成日；指定 `-ReferenceDate 2026-10-06` 可重复生成完全相同的 `projects.json`。标记文件中的生成时间会反映实际运行时间。

生成器默认保留已存在的标记数据，仅重新校验。`-VerifyOnly` 只验证；显式传入 `-Replace` 才重建已标记的测试数据，并由仓储保留 JSON 备份。重建前先关闭使用这个数据目录的应用。自定义 `--output` 仅允许 `artifacts/scale-audit` 下的独立子目录，拒绝默认用户数据路径。

从仓库根目录双击 `run-scale.cmd` 打开已构建的发布程序和这套独立数据。启动器不会重新生成或重置测试操作，也不会启动 UI 自动回归。日常启动入口为根目录 `run.cmd`。

规模启动器默认使用 `build.ps1 -Task Publish` 的 `artifacts/publish/win-x64-2.0.1` 输出；测试独立候选时可执行 `run-scale.ps1 -Profile 100k -PublishDirectory <候选发布目录>`。数据目录仍由合成夹具标记校验，不会因此切换到日常用户数据。

`scale-fixture.json` 在全部校验完成后写入，包含固定 `fixtureKind=pureproject-scale-audit`、`synthetic=true`、绝对数据目录、维度、实际计数、初始 JSON SHA-256 与内容分布统计。初始哈希用于审查前后对照，不作为日常启动条件，以允许在测试集中继续操作。

内容含合理中文名称、长标题、优先级、日期、标签、少量无环依赖、子任务、评论和里程碑。合成项目禁用外部同步，任务不设置运行中计时、提醒或循环生成。

2026-10-06 已执行验证：生成工具编译运行成功，真实仓储关闭后重新打开得到 10 个项目、100 个任务组、1,000 个状态与 20,000 个任务。JSON 共 21,673,306 个字符、25,209,826 字节，低于应用 32 Mi 字符的导入上限。内容分布为 3,000 个长标题、1,000 条依赖、12,000 个子任务、1,000 条评论、80 个标签和 50 个里程碑。

`-VerifyOnly` 前后 `projects.json`、`settings.json` 和 `scale-fixture.json` 的 SHA-256 均保持一致；另在 `artifacts/scale-audit/determinism-check` 以相同日期独立生成，两个 `projects.json` 的 SHA-256 相同：`15738D8EAB99B547475230AA41FF44D3EF08F11D2E129D1DFD37B159283AEC99`。生成及启动 PowerShell 脚本通过语法解析检查。以上仅证明数据和入口验证，不代表界面规模审查结果。

只读保存 CPU 基准可通过以下命令运行，重复次数可设为 1–10，默认 5 次：

```powershell
dotnet run --project tools/ScaleFixture/PureProject.ScaleFixture.csproj -c Release -- --profile 20k --benchmark --iterations 5
```

基准仅读取已标记夹具，在内存中按重构前保存链的步骤调用当前 Core 方法；不构造仓储实例、不写项目文件、不启动 GUI。当前生产代码已改为项目级快照、受限历史和后台保存工作，这个历史对比工具不代表现在 `CommitAsync` 的实际调用顺序。输出 `artifacts/scale-audit/serialization-benchmark.json`，含逐轮时间、分配量、GC 次数、汇总和前后文件哈希。它不测磁盘落盘、界面渲染或完整用户保存延迟，也不累积长期撤销历史。测量解释见 [历史保存 CPU 基线](SAVE_CPU_BASELINE.md)。
