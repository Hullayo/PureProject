/**
 * 插件系统纯逻辑自测
 *
 * 覆盖：
 *   1. `validateManifest()`：合法 / 缺字段 / 非法 id / capabilities 过滤
 *   2. `majorOf()` / `isCompatible()`：主版本兼容判定
 *   3. `pluginStorageKey()`：私有 key 命名空间与非法字符清洗
 *   4. 错误日志：最多 50 条、新的在前、可清空
 *
 * 运行：pnpm test:plugins
 */
import { validateManifest, isCompatible, majorOf, pluginStorageKey, APP_VERSION, PLUGIN_API_VERSION } from '$lib/plugins/types';

let fails = 0;
const eq = (a, b, msg) => {
  const ok = JSON.stringify(a) === JSON.stringify(b);
  if (!ok) { fails++; console.log('FAIL', msg, '\n  got     ', JSON.stringify(a), '\n  expected', JSON.stringify(b)); }
  else console.log('ok  ', msg);
};
const ok = (cond, msg, extra = '') => {
  if (cond) console.log('ok  ', msg);
  else { fails++; console.log('FAIL', msg, extra); }
};

// ─── 1. manifest 校验 ───────────────────────────────────────────────────────
console.log('\n[1] validateManifest');
{
  const good = validateManifest({
    id: 'com.example.hello',
    name: 'Hello Plugin',
    version: '1.0.0',
    main: 'inline',
    capabilities: ['commands', 'hooks'],
    description: 'd',
    author: 'me',
  });
  ok(good.ok, '合法 manifest 通过');
  eq(good.ok && good.manifest.capabilities, ['commands', 'hooks'], 'capabilities 保留');
  eq(good.ok && good.manifest.enabled, false, '未声明 enabled → false');

  const enabled = validateManifest({ id: 'a', name: 'A', version: '1.0.0', main: 'x', capabilities: ['views'], enabled: true });
  eq(enabled.ok && enabled.manifest.enabled, true, 'enabled:true 保留');
  eq(enabled.ok && enabled.manifest.main, 'x', 'main 保留');

  const noMain = validateManifest({ id: 'a', name: 'A', version: '1.2.3', capabilities: ['commands'] });
  eq(noMain.ok && noMain.manifest.main, 'inline', '缺少 main → 默认 inline');

  for (const [label, raw] of [
    ['非对象', []],
    ['缺 id', { name: 'A', version: '1.0.0', capabilities: ['commands'] }],
    ['缺 name', { id: 'a', version: '1.0.0', capabilities: ['commands'] }],
    ['缺 version', { id: 'a', name: 'A', capabilities: ['commands'] }],
    ['id 非法字符', { id: 'a b/c', name: 'A', version: '1.0.0', capabilities: ['commands'] }],
    ['version 非法', { id: 'a', name: 'A', version: 'v1', capabilities: ['commands'] }],
    ['无合法 capability', { id: 'a', name: 'A', version: '1.0.0', capabilities: ['nope'] }],
    ['capabilities 非数组', { id: 'a', name: 'A', version: '1.0.0', capabilities: 'commands' }],
  ]) {
    ok(!validateManifest(raw).ok, `拒绝：${label}`);
  }

  const filtered = validateManifest({ id: 'a', name: 'A', version: '1.0.0', capabilities: ['commands', 'bogus', 'hooks'] });
  eq(filtered.ok && filtered.manifest.capabilities, ['commands', 'hooks'], 'capabilities 过滤未知项');
}

// ─── 2. 版本兼容 ────────────────────────────────────────────────────────────
console.log('\n[2] 版本兼容');
{
  eq(majorOf('0.1.0'), 0, 'majorOf 0.1.0 → 0');
  eq(majorOf('2.3.4'), 2, 'majorOf 2.3.4 → 2');
  eq(majorOf('bad'), null, 'majorOf 非法 → null');
  eq(majorOf(''), null, 'majorOf 空 → null');

  eq(isCompatible('1.0.0', '1.1.0'), true, '同主版本 → 兼容');
  eq(isCompatible('1.99.0', '1.1.0'), true, '同主版本（次版本不同）→ 兼容');
  eq(isCompatible('0.8.4', '1.1.0'), false, '旧 0.x 插件 → 拒绝');
  eq(isCompatible('2.0.0', '1.1.0'), false, '未来主版本 → 拒绝');
  eq(isCompatible('bad', '1.1.0'), false, '版本非法 → 拒绝');
  eq(majorOf(APP_VERSION), 1, `APP_VERSION(${APP_VERSION}) 主版本为 1`);
  eq(PLUGIN_API_VERSION, 1, '四级任务模型使用插件 API v1');
}

// ─── 3. 私有存储 key ────────────────────────────────────────────────────────
console.log('\n[3] pluginStorageKey');
{
  eq(pluginStorageKey('com.example.a', 'count'), 'pm_plugin_com.example.a_count', '正常 id/key');
  eq(pluginStorageKey('a/b', 'x y'), 'pm_plugin_a_b_x_y', '非法字符清洗为 _');
  ok(pluginStorageKey('a', 'k').startsWith('pm_plugin_'), '前缀 pm_plugin_');
  ok(pluginStorageKey('a', 'k') !== pluginStorageKey('b', 'k'), '不同插件互相隔离');
}

// ─── 4. 错误日志（需要 localStorage 桩） ────────────────────────────────────
console.log('\n[4] 插件错误日志');
{
  const map = new Map();
  globalThis.localStorage = {
    get length() { return map.size; },
    key: (i) => [...map.keys()][i] ?? null,
    getItem: (k) => (map.has(k) ? map.get(k) : null),
    setItem: (k, v) => { map.set(k, String(v)); },
    removeItem: (k) => { map.delete(k); },
    clear: () => map.clear(),
  };

  const { pushPluginError, pluginErrors, clearPluginErrors, MAX_PLUGIN_ERRORS } = await import('$lib/plugins/registry');
  const { get } = await import('svelte/store');

  const origWarn = console.warn;
  console.warn = () => {}; // 静音预期内的错误警告
  try {
    clearPluginErrors();
    eq(get(pluginErrors).length, 0, '起点为空');

    for (let i = 0; i < MAX_PLUGIN_ERRORS + 10; i++) {
      pushPluginError({ pluginId: `p${i}`, message: `err ${i}` });
    }
  } finally {
    console.warn = origWarn;
  }
  const list = get(pluginErrors);
  eq(list.length, MAX_PLUGIN_ERRORS, `★ 最多保留 ${MAX_PLUGIN_ERRORS} 条`);
  eq(list[0].message, `err ${MAX_PLUGIN_ERRORS + 9}`, '★ 新的在前');
  ok(typeof list[0].at === 'string' && list[0].at.length > 0, '自动补 at 时间戳');
  ok(localStorage.getItem('pm_plugin_errors') !== null, '已持久化到 pm_plugin_errors');

  clearPluginErrors();
  eq(get(pluginErrors).length, 0, 'clearPluginErrors 生效');

  console.log('\n[5] PluginContext v1 与四级结构事件');
  const { createPluginContext } = await import('$lib/plugins/loader');
  const { pluginHooks, resetRuntimeRegistries } = await import('$lib/plugins/registry');
  resetRuntimeRegistries();
  const manifest = validateManifest({ id: 'com.example.v1', name: 'V1', version: '1.0.0', capabilities: ['hooks'] });
  ok(manifest.ok, 'v1 测试插件 manifest 合法');
  if (manifest.ok) {
    const context = createPluginContext(manifest.manifest);
    eq(context.apiVersion, 1, 'ctx.apiVersion 暴露为只读 API v1');
    let receivedGroupId = '';
    context.on('onTaskCreate', payload => { receivedGroupId = payload.taskGroupId; });
    context.on('onTaskGroupCreate', () => {});
    context.on('onStatusDefinitionUpdate', () => {});
    const taskCreateHook = pluginHooks.get('onTaskCreate')?.[0];
    taskCreateHook?.({ projectId: 'p1', taskGroupId: 'g1', task: {} });
    eq(receivedGroupId, 'g1', 'onTaskCreate payload 包含 taskGroupId');
    ok(pluginHooks.has('onTaskGroupCreate'), '可订阅 onTaskGroupCreate');
    ok(pluginHooks.has('onStatusDefinitionUpdate'), '可订阅 onStatusDefinitionUpdate');
  }
}

console.log(fails === 0 ? '\n全部通过 ✅' : `\n${fails} 个失败 ❌`);
process.exit(fails === 0 ? 0 : 1);
