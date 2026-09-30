# 简项（PureProject）V1.2

简项（PureProject）是基于 Tauri 2、Svelte 5 和 TypeScript 的本地优先项目管理工具，支持桌面端与 Android。V1.2 继承 V1.1 的核心数据模型：

```text
项目 -> 任务组 -> 状态 -> 任务
```

每个任务组拥有独立的状态集合。状态名称可以自由定义，跨视图业务逻辑由 `todo / active / done / cancelled` 四种状态类别统一驱动。

## 核心能力

- 多项目管理：创建、编辑、删除与拖拽排序。
- 任务组管理：创建、重命名、排序、设为默认、归档、恢复和空组删除。
- 动态状态：每个任务组可独立配置状态名称、颜色、类别、顺序、初始状态和完成状态。
- 五个项目视图：看板、列表、日历、时间线、依赖图。
- 全局仪表盘：跨项目汇总待处理、进行中、已完成、已取消和逾期任务。
- 任务能力：优先级、标签、日期、提醒、子任务、评论、依赖、循环规则和工时追踪。
- 数据安全：schema v4 迁移、引用完整性校验、原子文件写入、全量备份恢复和同步防降级。
- 本地插件：插件 API v1 支持命令、视图和四级结构事件钩子。
- 中英文界面、主题、动画、报告导出和 OpenAI 兼容 API。

## 五个项目视图

| 顺序 | 视图 | 主要行为 |
|---:|---|---|
| 1 | 看板 | 一次显示一个任务组；列来自该组状态定义；支持卡片跨列和列内排序 |
| 2 | 列表 | 支持全部任务组或单组筛选，并显示任务组与实际状态 |
| 3 | 日历 | 按截止日期查看任务，可按任务组过滤 |
| 4 | 时间线 | 甘特排期、跨组依赖和项目级里程碑 |
| 5 | 依赖图 | 展示跨任务组依赖关系和动态状态颜色 |

项目级概览、README、文件、统计和 Git 页签已从 V1.1 移除。旧项目中的 README、模板文件、文件树、变更日志和 `git_repo_path` 作为 legacy 数据保留，不在新界面中继续生成或编辑。

## 快速开始

要求：Node.js 22+、pnpm 11+；构建 Tauri 桌面应用还需要 Rust 1.77.2+。

```bash
pnpm install --frozen-lockfile
pnpm dev
```

开发服务器默认运行在 [http://localhost:1420](http://localhost:1420)。桌面壳开发模式：

```bash
pnpm tauri dev
```

创建项目时，应用内部存储始终开启；可额外选择本地文件夹副本。云同步服务器地址在全局设置中配置，不属于项目级存储选项。

## 数据模型

任务继续保存在项目级扁平数组中，通过外键关联任务组与状态：

```ts
interface Project {
  default_task_group_id: string;
  task_groups: TaskGroup[];
  tasks: Task[];
}

interface TaskGroup {
  id: string;
  name: string;
  archived: boolean;
  initial_status_id: string;
  completion_status_id: string;
  statuses: TaskStatusDefinition[];
}

interface Task {
  task_group_id: string;
  status_id: string;
  completed_at: string | null;
}
```

只有 `done` 类别满足前置依赖。`cancelled` 属于关闭状态，但不计入完成率，也不满足依赖。归档任务组不进入日常仪表盘统计，仍保留在完整导出与历史数据中。

## 数据与存储

| 位置 | 内容 |
|---|---|
| `localStorage: pm_projects` | `{ schema_version: 4, projects: [...] }` 内部主数据 |
| `localStorage: pm_file_<id>` | 单项目 schema v4 `.pm` 权威快照 |
| `localStorage: pm_sync_*` | 同步配置、revision 与冲突状态 |
| `localStorage: pm_projects_backup` | 删除前自动备份 |
| 本地文件夹 | 可选的 `<项目ID>-<项目名>.pm` 外部副本 |
| 同步服务器 | `index.json` 与 `projects/<id>.json` 项目快照 |

所有入口，包括本地导入、备份恢复、同步拉取和冲突覆盖，都经过同一个迁移与校验器。无版本号的 v1、v2 和 v3 文件会确定性迁移到 v4；高于 v4 的文件会被拒绝并提示升级客户端。

完整格式见 [docs/DATA-FORMAT.md](docs/DATA-FORMAT.md)。

## 同步安全

自建同步服务位于 `server/pm-sync-server.mjs`，使用 REST、SSE、Bearer token 和 revision 乐观锁。

```powershell
$env:PM_SYNC_PORT = "8787"
$env:PM_SYNC_DATA = "C:\pm-sync-data"
$env:PM_SYNC_TOKEN = "replace-with-a-strong-token"
node server\pm-sync-server.mjs
```

服务端索引记录每个项目的 `schema_version`。当服务器已有 v4 项目时，旧客户端上传 v3 会收到：

```json
{
  "conflict": true,
  "reason": "schema_downgrade",
  "serverSchemaVersion": 4,
  "incomingSchemaVersion": 3
}
```

客户端也会拒绝应用未来 schema，并在冲突界面显示本地与服务器版本。混用旧客户端前应先完成全量备份；旧客户端不能写回 V1.1 项目。

## 插件 API v1

V1.1 的插件主版本为 `1.x.y`，上下文暴露 `ctx.apiVersion === 1`。任务事件提供 `taskGroupId`、`statusId` 和状态类别，不再伪造旧 `task.status`。

详见 [docs/PLUGINS.md](docs/PLUGINS.md)。

## 快捷键

| 快捷键 | 功能 |
|---|---|
| `Ctrl+N` | 聚焦新建任务 |
| `Ctrl+F` | 聚焦搜索 |
| `Ctrl+Z` / `Ctrl+Shift+Z` | 撤销 / 重做 |
| `Ctrl+K` | 命令面板 |
| `Ctrl+,` | 设置 |
| `1` - `5` | 看板 / 列表 / 日历 / 时间线 / 依赖图 |
| `Esc` | 关闭当前面板 |
| `?` / `F1` | 快捷键帮助 |

## 开发与验证

```bash
pnpm check
pnpm check:i18n
pnpm check:acl
pnpm test
pnpm build
```

`pnpm test` 覆盖版本号、v1-v4 数据迁移、引用完整性、同步 schema 防降级、循环任务、排序、插件 API、工时和 Rust 原子写。

## 版本管理

项目使用 Git 保存可比较、可回退的修改历史。完成一个可描述的小步骤后运行：

```bash
pnpm git:status
pnpm verify
.\save-version.cmd "feat: 简要说明本次修改"
git push
```

`save-version.cmd` 创建本地提交但不会自动上传；也可使用 `pnpm git:save -- "feat: 修改说明"`。远程备份、功能分支、发布标签和安全回退方式见 [Git 版本管理工作流](docs/GIT-WORKFLOW.md)。

发版前运行：

```bash
pnpm verify
```

## 构建

```bash
pnpm build
pnpm build:win
pnpm build:android
```

应用版本由 `package.json` 作为前端单一来源注入 Vite，并与 `src-tauri/tauri.conf.json`、`src-tauri/Cargo.toml` 保持一致。当前版本为 `1.2.0`。

## 主要目录

```text
src/lib/stores/              项目、任务、任务组、筛选和历史操作
src/lib/utils/pm-schema.ts   schema v4 迁移与引用校验
src/lib/utils/task-status.ts 状态解析和业务语义
src/lib/repositories/        内部存储与 .pm 快照
src/lib/sync/                同步客户端、对账和冲突处理
src/lib/plugins/             插件 API v1
src/lib/components/views/    五个项目视图与全局仪表盘
server/pm-sync-server.mjs    自建同步服务
scripts/                     数据、同步、插件与构建测试
docs/                        数据格式、插件和架构改造文档
```

## 文档

- [数据格式与存储](docs/DATA-FORMAT.md)
- [插件 API v1](docs/PLUGINS.md)
- [四级架构改造清单](docs/FOUR-LEVEL-ARCHITECTURE-CHANGE-CHECKLIST.md)
- [数据安全与工程规范](docs/DATA-SAFETY-AND-STANDARDS.md)

## License

[MIT](LICENSE)
