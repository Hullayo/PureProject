/**
 * 内置示例插件
 *
 * 不自动启用；用户在「设置 → 插件」点「安装示例插件」后即可看到三类扩展点：
 * - `commands`：命令面板里出现「示例：打个招呼」
 * - `views`：注册一个返回 HTML 字符串的视图
 * - `hooks`：订阅 `onTaskCreate`，把新任务写进插件私有存储
 *
 * @module plugins/builtin/example-plugin
 *
 * @example
 * ```ts
 * import { EXAMPLE_PLUGIN } from '$lib/plugins/builtin/example-plugin';
 * installPlugin({ ...EXAMPLE_PLUGIN, manifest: { ...EXAMPLE_PLUGIN.manifest, enabled: true } });
 * ```
 */

import type { StoredPlugin } from '../types';

/** 示例插件源码（`ctx` 由宿主注入） */
export const EXAMPLE_PLUGIN_CODE = `
// 1) 命令扩展点
ctx.registerCommand({
  id: 'hello',
  title: '示例：打个招呼',
  category: '示例插件',
  run: () => {
    const n = Number(ctx.storage.get('greetCount') || '0') + 1;
    ctx.storage.set('greetCount', String(n));
    ctx.log('Hello from example plugin! 这是第 ' + n + ' 次打招呼');
    alert('Hello from example plugin!\\n已打招呼 ' + n + ' 次（记录在插件私有存储）');
  },
});

// 2) 视图扩展点（返回一段 HTML 字符串）
ctx.registerView({
  id: 'dashboard',
  title: '示例视图',
  render: () => '<div style="padding:16px;font-size:13px;color:var(--text)">' +
    '<h3>示例插件视图</h3>' +
    '<p>这是由插件返回的 HTML 字符串。</p></div>',
});

// 3) 钩子扩展点
ctx.on('onTaskCreate', (payload) => {
  ctx.log('捕获到 onTaskCreate（API v' + ctx.apiVersion + '）：', payload.taskGroupId, payload.task);
  const count = Number(ctx.storage.get('taskCreateCount') || '0') + 1;
  ctx.storage.set('taskCreateCount', String(count));
});

ctx.on('onProjectSave', (payload) => {
  ctx.log('捕获到 onProjectSave：', payload);
});

ctx.on('onTaskGroupChange', (payload) => {
  ctx.log('任务跨组移动：', payload.fromTaskGroupId, '->', payload.toTaskGroupId, payload.toStatusId);
});
`;

/** 示例插件（默认未启用） */
export const EXAMPLE_PLUGIN: StoredPlugin = {
  manifest: {
    id: 'builtin.example',
    name: '示例插件',
    version: '1.0.0',
    description: '演示 commands / views / hooks 三类扩展点',
    author: 'ProjectManager',
    main: 'inline',
    capabilities: ['commands', 'views', 'hooks'],
    enabled: false,
  },
  code: EXAMPLE_PLUGIN_CODE,
};
