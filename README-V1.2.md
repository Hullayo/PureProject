# 简项 V1.2 开发仓库

本目录是以简项 V1.1 为基线创建的 V1.2 独立开发仓库，应用版本为 `1.2.0`，创建日期为 2026-09-30。

V1.1 已完成的前两轮 20 项界面改造全部作为 V1.2 基线保留，历史说明见 `V1.1-CHANGELOG.md`；V1.2 的后续改动记录在 `V1.2-CHANGELOG.md`。

## 直接运行

双击根目录中的 `run-v1.2.cmd`，即可启动已经编译完成的 Windows 桌面程序：

```text
app\Jianxiang-V1.2.exe
```

运行桌面程序不需要安装 Node.js、pnpm、npm 或 Rust。

## Web 版本

目录内同时包含便携 Node.js 运行时和完整前端依赖。双击 `run-web-v1.2.cmd` 可在以下地址启动：

```text
http://127.0.0.1:1420/
```

如果 `1420` 端口已被占用，可以在命令行指定其他端口：

```bat
run-web-v1.2.cmd 1421
```

停止 Web 服务时，在对应命令窗口按 `Ctrl+C`。

## 目录内容

- `app/Jianxiang-V1.2.exe`：基于 V1.1 全部 20 条改造构建、使用独立应用标识的 V1.2 Windows 桌面程序。
- `src/`、`static/`：前端源码和静态资源。
- `src-tauri/`：Tauri/Rust 桌面端源码、Cargo 依赖锁定文件及独立构建结果。
- `node_modules/`：完整 npm 依赖；所有 pnpm 链接均指向本目录内部。
- `runtime/node.exe`：便携 Node.js 26.7.0 运行时。
- `build/`：V1.2 前端生产构建。
- `scripts/`、`server/`、`docs/`：测试、服务端辅助代码和项目文档。
- `package.json`、`pnpm-lock.yaml`、`Cargo.lock`：版本及依赖锁定信息。
- `V1.1-CHANGELOG.md`：前两轮 20 条批注的完整历史基线。
- `V1.2-CHANGELOG.md`：V1.2 后续改动记录。

## 重新构建

前端构建可直接使用内置运行时和依赖：

```bat
runtime\node.exe node_modules\vite\bin\vite.js build
runtime\node.exe scripts\fix-tauri-assets.mjs
```

重新编译 Windows 桌面程序需要本机安装 Rust/MSVC 工具链：

```bat
cargo build --release --features custom-protocol --manifest-path src-tauri\Cargo.toml --target-dir src-tauri\target
```

构建后的桌面程序位于：

```text
src-tauri\target\release\projectmanager.exe
```

## 说明

- 本目录可以脱离外层原项目目录运行，启动脚本不会引用外层源码或依赖。
- 桌面程序以 Tauri 的 `custom-protocol` 生产模式编译，界面资源已内置，不依赖 `localhost` 或 Web 服务。
- 独立桌面程序使用专属应用标识 `com.root.projectmanager.v12`，与 V1.1 及更早版本的数据目录隔离。
- 独立副本关闭了单实例插件，可以与原版和 V1.1 同时运行，方便对照修改。
- V1.2 独立版拥有单独的 Windows 应用数据目录；如需旧版项目数据，可通过应用内“导出/导入”迁移。
- 请使用根目录的 `run-v1.2.cmd` 或 `app/Jianxiang-V1.2.exe`。
- `src-tauri/target` 和 `node_modules` 体积较大，是为了保留完整依赖和独立构建能力。
