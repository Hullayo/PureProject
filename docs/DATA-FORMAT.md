# 数据格式与存储约定

> 适用版本：ProjectManager V1.2 / `.pm schema_version = 4`

本文档定义四级任务模型、内部存储、`.pm` 交换格式、迁移校验和同步版本保护。相关实现位于 `src/lib/types/*`、`src/lib/utils/pm-schema.ts`、`src/lib/repositories/*`、`src/lib/sync/*` 与 `server/pm-sync-server.mjs`。

## 1. 四级领域模型

```text
项目 Project
  -> 任务组 TaskGroup
    -> 状态 TaskStatusDefinition
      -> 任务 Task
```

任务保存在 `Project.tasks` 扁平数组中，通过 `task_group_id` 和 `status_id` 外键关联。这样可以继续支持跨任务组依赖、统一搜索、日历、时间线、报告与统计。

```ts
type StatusCategory = 'todo' | 'active' | 'done' | 'cancelled';

interface TaskStatusDefinition {
  id: string;
  name: string;
  color: string;
  category: StatusCategory;
  sort_order: number;
}

interface TaskGroup {
  id: string;
  name: string;
  sort_order: number;
  archived: boolean;
  initial_status_id: string;
  completion_status_id: string;
  statuses: TaskStatusDefinition[];
}

interface Task {
  id: string;
  task_group_id: string;
  status_id: string;
  completed_at: string | null;
  status_before_closed_id?: string;
}
```

状态名称是展示数据，状态类别才决定业务语义：

| 行为 | `todo` | `active` | `done` | `cancelled` |
|---|---:|---:|---:|---:|
| 计入未完成 | 是 | 是 | 否 | 否 |
| 计入完成 | 否 | 否 | 是 | 否 |
| 显示逾期与提醒 | 是 | 是 | 否 | 否 |
| 满足前置依赖 | 否 | 否 | 是 | 否 |
| 触发循环任务下一实例 | 否 | 否 | 是 | 否 |

## 2. 数据位置

| 位置 | 键或路径 | 说明 |
|---|---|---|
| 内部主存储 | `localStorage.pm_projects` | `{ schema_version: 4, projects: Project[] }` |
| 项目快照 | `localStorage.pm_file_<projectId>` | 权威 schema v4 `.pm` JSON；同步上传来源 |
| UI 偏好 | `pm_last_task_group_by_project` | 每台设备最后选择的任务组，不参与导出同步 |
| 同步配置 | `pm_sync_*` | 服务器、revision、冲突和凭据；凭据不进入备份 |
| 删除前备份 | `pm_projects_backup` | 最近的可恢复项目快照 |
| 本地额外副本 | 项目配置目录中的 `.pm` | 桌面端可选，原子写并保留 `.bak` |
| 同步服务器 | `index.json`、`projects/<id>.json` | 项目快照、revision、schema 和墓碑 |

内部主存储与 `pm_file_<id>` 必须使用同一序列化器。保存、撤销、导入、恢复和同步拉取后都要重写受影响项目快照，避免把旧快照推回服务器。

### 内部主存储格式

```json
{
  "schema_version": 4,
  "projects": [
    {
      "id": "project-1",
      "name": "移动端改版",
      "default_task_group_id": "group-dev",
      "task_groups": [],
      "tasks": []
    }
  ]
}
```

历史原始数组 `Project[]` 在启动时迁移到该包装格式。迁移失败时保留原数据备份，不把半迁移数据写回主键。

## 3. `.pm` schema v4

下面示例省略了部分普通任务字段，但保持引用关系完整：

```json
{
  "version": "1.0",
  "schema_version": 4,
  "project": {
    "id": "project-1",
    "name": "移动端改版",
    "description": "",
    "color": "#4f46e5",
    "created_at": "2026-09-01T00:00:00.000Z",
    "updated_at": "2026-09-30T00:00:00.000Z",
    "start_date": "2026-09-01",
    "end_date": "2026-12-31",
    "sort_order": 1024,
    "sync_enabled": true,
    "default_task_group_id": "group-dev"
  },
  "task_groups": [
    {
      "id": "group-dev",
      "name": "功能开发",
      "sort_order": 1024,
      "archived": false,
      "initial_status_id": "status-dev-todo",
      "completion_status_id": "status-dev-done",
      "statuses": [
        {
          "id": "status-dev-todo",
          "name": "待开发",
          "color": "#6b7280",
          "category": "todo",
          "sort_order": 1024
        },
        {
          "id": "status-dev-active",
          "name": "开发中",
          "color": "#4f46e5",
          "category": "active",
          "sort_order": 2048
        },
        {
          "id": "status-dev-done",
          "name": "已完成",
          "color": "#10b981",
          "category": "done",
          "sort_order": 3072
        }
      ]
    }
  ],
  "tasks": [
    {
      "id": "task-1",
      "title": "接入支付接口",
      "description": "",
      "task_group_id": "group-dev",
      "status_id": "status-dev-active",
      "completed_at": null,
      "priority": "high",
      "tags": [],
      "due_date": "2026-10-12",
      "due_time": null,
      "dependencies": [],
      "subtasks": [],
      "comments": [],
      "recurrence": null,
      "created_at": "2026-09-30T08:00:00.000Z",
      "updated_at": "2026-09-30T08:00:00.000Z"
    }
  ],
  "tags": [],
  "milestones": [],
  "changelog": []
}
```

`storage` 是本机配置，不写入 `.pm`，也不参与设备间同步。旧 README、模板文件、变更日志、流程图和 `git_repo_path` 在原数据存在时继续无损导出，但新项目不再生成这些内容。

`sync_enabled` 是旧版项目级同步开关的兼容字段。V1.1.0 起不再提供项目级开关，导入时统一归一化为 `true`；是否自动同步由全局同步模式控制，首次使用默认为自动模式，尚未配置服务器或凭据时不会发起网络请求。

## 4. 数据不变量

`validatePm()` 在导入、恢复和同步应用前检查：

- 项目至少有一个未归档任务组。
- `default_task_group_id` 指向未归档任务组。
- 组 ID、状态 ID和任务 ID 在相应范围内唯一。
- 每个任务组至少有一个状态。
- `initial_status_id` 属于当前任务组。
- `completion_status_id` 属于当前任务组，且类别为 `done`。
- 任务的 `task_group_id` 存在。
- 任务的 `status_id` 属于任务指定的组。
- `done` 任务必须有 `completed_at`，其它类别必须为 `null`。
- 依赖目标存在，且依赖图不能形成循环。
- 任务总数不超过 20,000，单文件不超过 32 MB。

错误包含精确字段路径，例如：

```text
tasks[3].status_id：状态不属于任务指定的任务组
task_groups[0].completion_status_id：完成状态的类别必须为 done
```

校验失败不会写入任何主数据。

## 5. 版本迁移

| schema | 说明 |
|---|---|
| 缺失 / v1 | 历史任务格式，可能缺数组和可空字段 |
| v2 | 补齐基础任务字段 |
| v3 | 增加 `recurrence` |
| v4 | 增加任务组、动态状态外键、状态类别和 `completed_at` |

v3 到 v4 的迁移规则：

1. 依据项目 ID；若无 ID，则依据名称和创建时间，生成确定性默认任务组 ID。
2. 合并 `project.kanban_columns` 与任务实际使用的旧 `status` 值。
3. 为每个旧状态生成确定性状态 ID，不同设备得到相同结果。
4. 每个任务写入 `task_group_id` 与 `status_id`，删除运行时旧 `status`。
5. 旧完成任务用自身 `completed_at`、`updated_at` 或 `created_at` 补完成时间。
6. 未声明的任务状态会保留为自定义状态，并返回迁移警告。
7. `kanban_columns`、README、模板文件和 Git 字段仅作为 legacy 数据保留。

迁移器只补缺失字段，不会把类型错误静默改成默认值。比如 `tasks: {}` 会被拒绝，不会被改成空数组。

高于当前版本的 schema 直接拒绝：

```text
该项目使用 schema v5，当前客户端仅支持 v4，请升级客户端
```

## 6. 导入、备份与恢复

所有数据入口统一执行：

```text
文本
  -> JSON 解析
  -> schema 检测
  -> v1/v2/v3 确定性迁移
  -> v4 引用校验
  -> 写内部主存储与 pm_file_<id>
  -> 必要时写本地额外副本
```

全量备份格式：

```json
{
  "kind": "projectmanager-backup",
  "schema_version": 4,
  "app_version": "1.2.0",
  "exported_at": "2026-09-30T10:00:00.000Z",
  "projects": [],
  "settings": {},
  "sync": {}
}
```

备份不包含同步 token、WebDAV 密码、Gist token 或 AI 密钥。恢复采用合并策略：同 ID 比较 `updated_at`，更新者胜；本地独有项目不删除；坏条目跳过并进入恢复报告。

## 7. 同步 schema 防降级

同步服务器为每个项目保存 `rev` 和 `schema_version`。PUT 顺序是：

1. 校验 JSON、项目对象及 URL ID。
2. 校验 `X-Base-Rev` 乐观锁。
3. 比较服务器与上传快照的 schema。
4. 只有 revision 和 schema 都可接受时，才原子替换项目文件并更新索引。

当服务器为 v4、上传为 v3 时返回 HTTP 409：

```json
{
  "conflict": true,
  "reason": "schema_downgrade",
  "serverSchemaVersion": 4,
  "incomingSchemaVersion": 3
}
```

客户端必须停止覆盖，并提示用户升级旧设备。冲突未解决前不得清除冲突项。服务器项目列表、单项目拉取、增量变更、SSE 事件和墓碑都携带 schema。

## 8. 本地额外副本

应用内部存储始终开启。本地文件夹只是额外副本：

- 只有归属设备写本地 `.pm`。
- 非归属设备只参与服务器同步，不把对方路径视为错误。
- 桌面端使用临时文件、fsync、`.bak` 和 rename 原子写。
- 移动端遇到桌面路径会跳过外部副本，不影响主存储。
- 项目改名不会改变项目 ID；外部文件名包含项目 ID，避免同名冲突。

## 9. 验证命令

```bash
pnpm test:data
pnpm test:sync-schema
pnpm test:plugins
pnpm verify
```

`test:data` 覆盖 v1/v2/v3 到 v4、确定性 ID、自定义与未知状态、完成时间、未来 schema、重复 ID、跨组状态、依赖缺失与循环、v4 往返和备份恢复。`test:sync-schema` 真实启动服务验证 v3 不能覆盖 v4。
