# ProjectManager 插件系统

> 版本兼容：本文档对应 **ProjectManager v1.2 / 插件 API v1**。0.x 插件不会被新版本加载。

ProjectManager 的插件是**单文件 JS/TS 模块**，可在不修改主程序的前提下扩展命令、
视图与生命周期钩子。插件在本应用进程内运行。

---

## 1. 什么是 ProjectManager 插件

**适用场景**

- 给命令面板添加自定义命令（批量操作、外部工具调用）。
- 注册一个自定义视图（用 HTML 字符串展示统计、说明、外部数据）。
- 监听数据事件（项目保存、任务创建、任务状态变化、导出）做记录或联动。

**不适用场景**

- ❌ 需要访问浏览器之外的系统能力（文件系统、进程、网络底层）—— 请走主程序的
  Tauri 命令，而不是插件。
- ❌ 需要修改 `.pm` 数据结构或同步协议 —— 那属于核心，应由主程序版本升级完成。
- ❌ 需要长期后台运行的守护逻辑 —— 插件没有独立的生命周期与后台线程。
- ❌ 处理机密数据 —— 插件代码与主应用同权限，见第 7 节。

---

## 2. 目录结构与加载时机

```
src/lib/plugins/
├── index.ts        统一入口：initPlugins / installPluginFromJson / runHook
├── registry.ts     注册表：已安装插件、错误日志、运行期命令/视图/钩子
├── loader.ts       用 new Function('ctx', code) 执行插件并注入 PluginContext
├── types.ts        PluginManifest / PluginContext 等类型 + 纯校验
└── builtin/
    └── example-plugin.ts   内置示例（默认不启用）
```

- **源码与清单**保存在本机 `localStorage`：
  - `pm_plugins`：`{ [id]: { manifest, code } }`
  - `pm_plugin_errors`：最近 50 条错误
  - `pm_plugin_<id>_<key>`：插件私有存储
- **加载时机**：应用启动时（`+layout.svelte` 的 `onMount`）调用 `initPlugins()`，
  只加载 `enabled: true` 的插件；安装 / 启用 / 禁用 / 卸载后会立即重新加载。
- 插件注册的命令会出现在**命令面板**（分类「插件」）；注册的视图可在
  「设置 → 插件 → 插件视图」中预览。

---

## 3. 快速上手：10 行 hello-world

在「设置 → 插件」的安装表单里：

**清单（manifest JSON）**

```json
{
  "id": "com.example.hello",
  "name": "Hello",
  "version": "1.0.0",
  "main": "inline",
  "capabilities": ["commands"]
}
```

**源码**

```js
ctx.registerCommand({
  id: 'hello',
  title: 'Hello',
  run: () => ctx.log('hello from plugin'),
});
```

点「安装并启用」后，按 `Ctrl+K` 打开命令面板，搜索 `Hello` 即可运行。

> 也可点「安装示例插件」体验 `commands` / `views` / `hooks` 三类扩展点。

---

## 4. PluginContext API 参考

插件源码会在 `new Function('ctx', code)` 中执行，`ctx` 即下列对象（**只读引用**）：

```ts
interface PluginContext {
  /** 宿主插件 API 主版本；四级任务模型为 1 */
  readonly apiVersion: 1;

  /** 自身 manifest（只读） */
  plugin: PluginManifest;

  /** 注册一条命令（命令面板 / 快捷键） */
  registerCommand(cmd: {
    id: string;            // 插件内唯一即可，宿主会加 `<pluginId>:` 前缀
    title: string;         // 显示名
    category?: string;     // 分类（默认取插件名）
    run: () => void | Promise<void>;
  }): void;

  /** 注册一个自定义视图（返回 HTML 字符串） */
  registerView(view: {
    id: string;
    title: string;
    render: () => string;  // 返回一段 HTML
  }): void;

  /** 订阅生命周期 / 数据事件 */
  on(event: 'onProjectSave' | 'onTaskCreate' | 'onTaskStatusChange' |
            'onTaskGroupChange' | 'onTaskGroupCreate' | 'onTaskGroupUpdate' |
            'onTaskGroupDelete' | 'onStatusDefinitionCreate' |
            'onStatusDefinitionUpdate' | 'onStatusDefinitionDelete' | 'onExport',
     handler: (payload: unknown) => void): void;

  /** 插件私有 key-value（落 pm_plugin_<id>_<key>，与主数据隔离） */
  storage: {
    get(key: string): string | null;
    set(key: string, value: string): void;
    remove(key: string): void;
  };

  /** 写日志（同时可用于排查） */
  log(...args: unknown[]): void;

  /** 只读 i18n；**不允许**修改字典 */
  i18n: { t(key: string): string };
}
```

### 4.1 事件 payload

| 事件 | 触发时机 | payload |
|---|---|---|
| `onProjectSave` | 项目被持久化时 | `{ project: Project }` |
| `onTaskCreate` | 新建任务后 | `{ projectId, taskGroupId, task }` |
| `onTaskStatusChange` | 任务状态变更后 | `{ projectId, taskId, taskGroupId, fromStatusId, toStatusId, fromCategory, toCategory }` |
| `onTaskGroupChange` | 任务跨任务组移动后 | `{ projectId, taskId, fromTaskGroupId, toTaskGroupId, toStatusId }` |
| `onTaskGroupCreate` | 新建任务组后 | `{ projectId, taskGroup }` |
| `onTaskGroupUpdate` | 任务组被重命名、排序、归档或角色状态改变后 | `{ projectId, taskGroup }` |
| `onTaskGroupDelete` | 删除空任务组后 | `{ projectId, taskGroupId }` |
| `onStatusDefinitionCreate` | 新建状态定义后 | `{ projectId, taskGroupId, status }` |
| `onStatusDefinitionUpdate` | 状态定义名称、颜色、类别或顺序变化后 | `{ projectId, taskGroupId, status }` |
| `onStatusDefinitionDelete` | 删除状态并完成任务迁移后 | `{ projectId, taskGroupId, statusId, migrateToStatusId? }` |
| `onExport` | 导出 `.pm` / Markdown 后 | `{ projectId, format: 'pm' \| 'markdown' }` |

> payload 里的对象是**引用**，请勿修改；需要持久化请用 `ctx.storage`。

---

## 5. 三类扩展点示例

### 5.1 commands

```js
ctx.registerCommand({
  id: 'log-active',
  title: '记录当前项目',
  run: () => ctx.log('active project:', ctx.plugin.name),
});

ctx.log('host plugin API:', ctx.apiVersion);
```

命令面板会显示在分类「插件」下；`run` 可以是 `async` 函数。

### 5.2 views

```js
ctx.registerView({
  id: 'about',
  title: '插件说明',
  render: () => '<div style="padding:16px">由插件生成的 HTML</div>',
});
```

> `render()` 的返回值由宿主用 `{@html}` 渲染。**请只返回你自己可控的 HTML**，
> 不要拼接外部输入，避免 XSS。

### 5.3 hooks

```js
let created = 0;
ctx.on('onTaskCreate', () => { created += 1; });
ctx.on('onProjectSave', (p) => ctx.log('saved', p));
```

- 每个处理器都被宿主 `try/catch` 包裹，单个插件抛错不会影响主应用。
- 出错信息写入 `pm_plugin_errors`，可在「设置 → 插件」查看。

---

## 6. 插件私有存储与错误上报

**私有存储**（与项目数据完全隔离）：

```js
const n = Number(ctx.storage.get('count') || '0') + 1;
ctx.storage.set('count', String(n));
ctx.storage.remove('count');
```

实际落键：`pm_plugin_<id>_<key>`，其中非法字符会被替换为 `_`。

**错误上报**：

- 加载失败（语法错误、主版本不兼容）→ 记录 `pluginId` + 原因。
- 命令 / 视图 / 钩子运行时抛错 → 记录原因与堆栈。
- 最多保留最近 **50** 条，新的在前；可在设置页清空。

---

## 7. 安全模型与安装来源提示

> ⚠️ **插件代码在本应用进程内执行，拥有与主程序相同的权限。
> 只安装可信来源的插件。**

- 插件通过 `new Function('ctx', code)` 执行（不是 `eval`，但同样能访问全局对象）。
- 插件**可以**读写 `localStorage`、发起网络请求、改动页面 DOM。
- 插件**不能**修改 i18n 字典（`ctx.i18n` 是只读副本），但这只是约定，不是沙箱。
- 卸载插件会清掉它的运行期注册项；是否保留 `pm_plugin_<id>_*` 私有数据由实现决定。
- 不要从不明来源粘贴插件源码。安装表单里的源码框就是它的全部权限边界。

---

## 8. 版本兼容性约定

- 插件 manifest 的 `version` 形如 `A.B.C`。
- **主版本号 `A` 必须与应用主版本号一致**，否则拒绝加载并记入错误日志。
  - 应用 `1.1.0` ⇒ 插件须为 `1.x.y`。
- 插件可读取 `ctx.apiVersion` 判断宿主 API；V1.2 返回 `1`。
- API v1 的任务没有旧 `status` 字段。插件必须使用 `task.task_group_id`、`task.status_id`，并结合项目中的任务组和状态定义解析业务语义。
- 次版本 / 修订号由插件作者自行管理，宿主不限制。
- 当核心 API 发生不兼容变更时，应用会提升主版本号，届时需要同步升级插件。
