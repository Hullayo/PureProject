# Core 回归测试

这是控制台自检工程，使用 .NET SDK 10.0.401。它不依赖本机用户数据、`v1.2` 源目录、Node.js 或 GUI。

在 `jianxiang` 仓库根目录运行。首次取得源码时先准备便携 SDK 和依赖；源码仓库不附带 `.tools`。已安装 SDK 10.0.401 时，也可以直接用 `dotnet` 替换下面的本地可执行文件路径。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -Task Restore
.\.tools\dotnet\dotnet.exe run --project tests/PureProject.Core.Tests -c Release
```

默认从构建输出目录中的 `Fixtures` 读取六份已有演示项目。测试工程会复制这些文件；直接复制完整测试输出目录后运行测试也可使用相同样本。可选第一个参数为其他样本根目录，该目录须具有 `static/demo.pm`、`static/demo2.pm`、`static/demo3.pm` 及相应 `v1.2/static/` 文件布局。该参数不参与默认测试。

`Fixtures/manifest.json` 记录仓库相对来源、SHA-256 和文件大小。样本说明见 [Fixtures/README.md](Fixtures/README.md)。不要以真实用户项目替换这些固定演示样本。
