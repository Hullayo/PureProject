/**
 * 拖拽排序端到端验证（CDP 驱动 headless Chrome）
 *
 * 覆盖：项目拖拽（鼠标 / 触摸长按）、任务列表拖拽、看板跨列、Ctrl+Z 撤销、
 *       sort_order 写入 .pm 载荷、按 sort_order 重排（跨设备顺序同步）、搜索过滤。
 *
 * 前置（两个进程都要先起来）：
 *   1) pnpm dev
 *   2) /opt/chrome-headless/chrome-headless-shell --headless --no-sandbox \
 *        --remote-debugging-port=9222 --user-data-dir=/tmp/cdp-profile about:blank
 *
 * 运行：pnpm test:e2e-drag
 *
 * 注意：这是开发期验证脚本，不参与构建；断言失败会以非 0 退出。
 */
const PORT = 9222;
const APP = 'http://localhost:1420/';
const GROUP_ID = 'group-default';
const STATUS_TODO = 'status-todo';
const STATUS_ACTIVE = 'status-active';
const STATUS_DONE = 'status-done';
const TASK_GROUPS = [{
  id: GROUP_ID, name: '默认任务组', sort_order: 1024, archived: false,
  initial_status_id: STATUS_TODO, completion_status_id: STATUS_DONE,
  statuses: [
    { id: STATUS_TODO, name: '待办', color: '#6b7280', category: 'todo', sort_order: 1024 },
    { id: STATUS_ACTIVE, name: '进行中', color: '#4f46e5', category: 'active', sort_order: 2048 },
    { id: STATUS_DONE, name: '已完成', color: '#10b981', category: 'done', sort_order: 3072 },
  ],
}];

const SEED = [
  ['p1', 'Alpha', ['A1', 'A2', 'A3']],
  ['p2', 'Beta', ['B1', 'B2']],
  ['p3', 'Gamma', ['C1']],
  ['p4', 'Delta', []],
].map(([id, name, tasks], i) => ({
  id, name, description: '', color: '#4f46e5', template: 'default',
  created_at: '2026-01-01T00:00:00.000Z', updated_at: '2026-01-01T00:00:00.000Z',
  archived: false, sync_enabled: true, sort_order: i * 1000,
  default_task_group_id: GROUP_ID, task_groups: structuredClone(TASK_GROUPS),
  tasks: tasks.map(t => ({
    id: t.toLowerCase(), title: t, description: '', task_group_id: GROUP_ID, status_id: STATUS_TODO, completed_at: null, priority: 'medium',
    tags: [], due_date: null, due_time: null, start_offset: null, dependencies: [],
    subtasks: [], comments: [], tracked_start: null, reminder: null,
    created_at: '2026-01-01T00:00:00.000Z', updated_at: '2026-01-01T00:00:00.000Z',
  })),
  tags: [], changelog: [], milestones: [], readme: '# x',
}));

const sleep = ms => new Promise(r => setTimeout(r, ms));

class CDP {
  constructor(ws) { this.ws = ws; this.id = 0; this.pending = new Map(); }
  static async connect(url) {
    const ws = new WebSocket(url);
    await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });
    const c = new CDP(ws);
    ws.onmessage = (ev) => {
      const m = JSON.parse(ev.data);
      if (m.id && c.pending.has(m.id)) {
        const { resolve, reject } = c.pending.get(m.id);
        c.pending.delete(m.id);
        m.error ? reject(new Error(JSON.stringify(m.error))) : resolve(m.result);
      }
    };
    return c;
  }
  send(method, params = {}) {
    const id = ++this.id;
    this.ws.send(JSON.stringify({ id, method, params }));
    return new Promise((resolve, reject) => this.pending.set(id, { resolve, reject }));
  }
  async eval(expression) {
    const r = await this.send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise: true });
    if (r.exceptionDetails) throw new Error('eval: ' + (r.exceptionDetails.exception?.description || JSON.stringify(r.exceptionDetails)));
    return r.result.value;
  }
}

let fails = 0;
const check = (cond, msg, extra = '') => {
  if (cond) console.log('ok   ', msg);
  else { fails++; console.log('FAIL ', msg, extra); }
};

const rectOfJs = (js) => `(() => { const e = ${js}; if (!e) return null; const r = e.getBoundingClientRect(); return { x: r.left + r.width/2, y: r.top + r.height/2, h: r.height, w: r.width }; })()`;
const projectByName = (name) => `[...document.querySelectorAll('.project-list [data-sortable-item]')].find(e => e.querySelector('.project-name')?.textContent.trim() === ${JSON.stringify(name)})`;

async function mouseClick(cdp, x, y) {
  await cdp.send('Input.dispatchMouseEvent', { type: 'mousePressed', x, y, button: 'left', buttons: 1, clickCount: 1 });
  await cdp.send('Input.dispatchMouseEvent', { type: 'mouseReleased', x, y, button: 'left', buttons: 0, clickCount: 1 });
  await sleep(350);
}

async function dragMouseTo(cdp, from, toY, toX = from.x, steps = 12) {
  await cdp.send('Input.dispatchMouseEvent', { type: 'mousePressed', x: from.x, y: from.y, button: 'left', buttons: 1, clickCount: 1 });
  await sleep(50);
  for (let i = 1; i <= steps; i++) {
    await cdp.send('Input.dispatchMouseEvent', {
      type: 'mouseMoved', button: 'left', buttons: 1,
      x: from.x + (toX - from.x) * i / steps,
      y: from.y + (toY - from.y) * i / steps,
    });
    await sleep(16);
  }
  await cdp.send('Input.dispatchMouseEvent', { type: 'mouseReleased', x: toX, y: toY, button: 'left', buttons: 0, clickCount: 1 });
  await sleep(250);
}

async function dragTouchTo(cdp, from, toY, holdMs = 340) {
  const p = (x, y) => [{ x, y, radiusX: 6, radiusY: 6, force: 1, id: 1 }];
  await cdp.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: p(from.x, from.y) });
  await sleep(holdMs);
  const steps = 12;
  for (let i = 1; i <= steps; i++) {
    await cdp.send('Input.dispatchTouchEvent', { type: 'touchMove', touchPoints: p(from.x, from.y + (toY - from.y) * i / steps) });
    await sleep(16);
  }
  await cdp.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
  await sleep(300);
}

const names = `JSON.parse(localStorage.pm_projects).projects.map(p => p.name)`;
const orders = `JSON.parse(localStorage.pm_projects).projects.map(p => p.sort_order)`;
const tasksOf = (name) => `(JSON.parse(localStorage.pm_projects).projects.find(p => p.name === ${JSON.stringify(name)}) || {tasks:[]}).tasks.map(t => t.title)`;

async function preflight() {
  try {
    await fetch('http://localhost:1420/', { method: 'HEAD' });
  } catch {
    console.error('✗ 开发服务器未启动：请先运行 `pnpm dev`');
    process.exit(2);
  }
  try {
    const r = await fetch(`http://127.0.0.1:${PORT}/json/version`);
    if (!r.ok) throw new Error(String(r.status));
  } catch {
    console.error('✗ 调试端口 9222 未就绪，请先启动：\n' +
      '  /opt/chrome-headless/chrome-headless-shell --headless --no-sandbox \\\n' +
      '    --remote-debugging-port=9222 --user-data-dir=/tmp/cdp-profile about:blank');
    process.exit(2);
  }
}

(async () => {
  await preflight();
  const res = await fetch(`http://127.0.0.1:${PORT}/json/new?about:blank`, { method: 'PUT' });
  const target = await res.json();
  const cdp = await CDP.connect(target.webSocketDebuggerUrl);
  await cdp.send('Page.enable');
  await cdp.send('Runtime.enable');
  // 固定视口，避免看板第三列跑到视口外
  await cdp.send('Emulation.setDeviceMetricsOverride', { width: 1280, height: 900, deviceScaleFactor: 1, mobile: false });
  await cdp.send('Page.navigate', { url: APP });
  await sleep(2500);
  await cdp.eval(`localStorage.clear(); localStorage.setItem('pm_tutorial_seen','1'); localStorage.setItem('pm_projects', ${JSON.stringify(JSON.stringify({ schema_version: 4, projects: SEED }))});`);
  await cdp.send('Page.reload');
  await sleep(2000);

  let n = await cdp.eval(`document.querySelectorAll('.project-list [data-sortable-item]').length`);
  check(n === 4, `侧边栏渲染 4 个项目（实际 ${n}）`);

  // ── 1. 鼠标拖项目：Alpha → Gamma 之后 ────────────────────────────────────
  let from = await cdp.eval(rectOfJs(`${projectByName('Alpha')}.querySelector('.drag-handle')`));
  let tgt = await cdp.eval(rectOfJs(projectByName('Gamma')));
  await dragMouseTo(cdp, from, tgt.y + tgt.h / 2 - 2);
  check(JSON.stringify(await cdp.eval(names)) === JSON.stringify(['Beta', 'Gamma', 'Alpha', 'Delta']),
    '鼠标拖拽：Alpha → Gamma 之后', JSON.stringify(await cdp.eval(names)));
  check(JSON.stringify(await cdp.eval(orders)) === JSON.stringify([1000, 2000, 2500, 3000]),
    'sort_order 中点插入 2500（其余不动，只推一份 .pm）', JSON.stringify(await cdp.eval(orders)));

  // ── 2. 触摸长按拖项目：Delta → 最前 ──────────────────────────────────────
  from = await cdp.eval(rectOfJs(`${projectByName('Delta')}.querySelector('.drag-handle')`));
  tgt = await cdp.eval(rectOfJs(projectByName('Beta')));
  await dragTouchTo(cdp, from, tgt.y - tgt.h / 2 + 3);
  check(JSON.stringify(await cdp.eval(names)) === JSON.stringify(['Delta', 'Beta', 'Gamma', 'Alpha']),
    '触摸长按拖拽：Delta → 最前', JSON.stringify(await cdp.eval(names)));

  await sleep(500);   // 等过 click 抑制窗口
  // ── 3. 选中 Beta → 列表视图 → 任务拖拽 ──────────────────────────────────
  let beta = await cdp.eval(rectOfJs(projectByName('Beta')));
  await mouseClick(cdp, beta.x, beta.y);
  const toList = await cdp.eval(`(() => { const b = [...document.querySelectorAll('.toggle-btn')].find(x => x.textContent.trim() === '列表'); if (!b) return false; b.click(); return true; })()`);
  await sleep(400);
  check(toList, '切换到「列表」视图');
  let ids = await cdp.eval(`[...document.querySelectorAll('.task-list [data-sortable-item]')].map(e => e.dataset.sortableId)`);
  check(ids.length === 2, `Beta 显示 2 个任务（${JSON.stringify(ids)}）`);

  // 点 Alpha 拿 3 个任务
  const alpha = await cdp.eval(rectOfJs(projectByName('Alpha')));
  await mouseClick(cdp, alpha.x, alpha.y);
  await cdp.eval(`(() => { const b = [...document.querySelectorAll('.toggle-btn')].find(x => x.textContent.trim() === '列表'); if (b) b.click(); })()`);
  await sleep(400);
  ids = await cdp.eval(`[...document.querySelectorAll('.task-list [data-sortable-item]')].map(e => e.dataset.sortableId)`);
  check(ids.length === 3, `Alpha 显示 3 个任务（${JSON.stringify(ids)}）`);

  from = await cdp.eval(rectOfJs(`document.querySelector('.task-list [data-sortable-item]:nth-child(1) .drag-handle')`));
  const lastRow = await cdp.eval(rectOfJs(`document.querySelector('.task-list [data-sortable-item]:nth-child(3)')`));
  await dragMouseTo(cdp, from, lastRow.y + lastRow.h / 2 - 2);
  check(JSON.stringify(await cdp.eval(tasksOf('Alpha'))) === JSON.stringify(['A2', 'A3', 'A1']),
    '任务拖拽：A1 → 末尾', JSON.stringify(await cdp.eval(tasksOf('Alpha'))));

  // ── 4. Ctrl+Z 撤销 ──────────────────────────────────────────────────────
  await cdp.send('Input.dispatchKeyEvent', { type: 'rawKeyDown', key: 'z', code: 'KeyZ', windowsVirtualKeyCode: 90, modifiers: 2 });
  await cdp.send('Input.dispatchKeyEvent', { type: 'keyUp', key: 'z', code: 'KeyZ', windowsVirtualKeyCode: 90, modifiers: 2 });
  await sleep(400);
  check(JSON.stringify(await cdp.eval(tasksOf('Alpha'))) === JSON.stringify(['A1', 'A2', 'A3']),
    'Ctrl+Z 撤销任务拖拽', JSON.stringify(await cdp.eval(tasksOf('Alpha'))));

  // ── 5. 看板跨列 ─────────────────────────────────────────────────────────
  const toKanban = await cdp.eval(`(() => { const b = [...document.querySelectorAll('.toggle-btn')].find(x => x.textContent.trim() === '看板'); if (b) b.click(); return !!b; })()`);
  await sleep(500);
  check(toKanban, '切换到「看板」视图');
  const zones = await cdp.eval(`[...document.querySelectorAll('[data-sortable-zone]')].map(e => e.dataset.sortableZone)`);
  check(zones.length === 3, `看板 3 列 ${JSON.stringify(zones)}`);

  const card = await cdp.eval(rectOfJs(`document.querySelectorAll('[data-sortable-zone]')[0].querySelector('[data-sortable-item]')`));
  const doneCol = await cdp.eval(rectOfJs(`document.querySelectorAll('[data-sortable-zone]')[2]`));
  const hit = await cdp.eval(`(() => { const el = document.elementFromPoint(${doneCol.x}, ${doneCol.y}); return el ? (el.closest('[data-sortable-zone]')?.dataset.sortableZone ?? el.className) : 'null'; })()`);
  check(hit === STATUS_DONE, `(自检) 落点命中完成列（实际 ${hit}）`);
  await dragMouseTo(cdp, card, doneCol.y, doneCol.x);
  const statuses = await cdp.eval(`JSON.parse(localStorage.pm_projects).projects.find(p => p.name==='Alpha').tasks.map(t => t.title + ':' + t.status_id)`);
  check(statuses.includes(`A1:${STATUS_DONE}`), '看板拖拽：A1 → 「已完成」列（status_id 指向完成状态）', JSON.stringify(statuses));

  // ── 5b. 推给服务器的 .pm 载荷里带 sort_order ────────────────────────────
  const pmOrder = await cdp.eval(`JSON.parse(localStorage.getItem('pm_file_p4')||'{}').project?.sort_order`);
  check(pmOrder === 0, `pm_file_<id> 载荷携带 sort_order（Delta=${pmOrder}）`);

  // ── 6. 刷新后顺序保持 ───────────────────────────────────────────────────
  await cdp.send('Page.reload');
  await sleep(1800);
  const after = await cdp.eval(`[...document.querySelectorAll('.project-list [data-sortable-item] .project-name')].map(e => e.textContent.trim())`);
  check(JSON.stringify(after) === JSON.stringify(['Delta', 'Beta', 'Gamma', 'Alpha']),
    '刷新后项目顺序保持', JSON.stringify(after));

  // ── 7. 项目搜索不再影响拖拽（过滤后拖动） ────────────────────────────────
  await cdp.eval(`(() => { const i = document.querySelector('.project-search .search-input'); i.value = 'a'; i.dispatchEvent(new Event('input', { bubbles: true })); })()`);
  await sleep(400);
  const visible = await cdp.eval(`[...document.querySelectorAll('.project-list [data-sortable-item] .project-name')].map(e => e.textContent.trim())`);
  check(JSON.stringify(visible) === JSON.stringify(['Delta', 'Beta', 'Gamma', 'Alpha']),
    `搜索过滤 "a" 命中全部 4 个（都含 a）`, JSON.stringify(visible));
  await cdp.eval(`(() => { const i = document.querySelector('.project-search .search-input'); i.value = 'ga'; i.dispatchEvent(new Event('input', { bubbles: true })); })()`);
  await sleep(400);
  const visible2 = await cdp.eval(`[...document.querySelectorAll('.project-list [data-sortable-item] .project-name')].map(e => e.textContent.trim())`);
  check(JSON.stringify(visible2) === JSON.stringify(['Gamma']), `搜索过滤 "ga" 只剩 Gamma`, JSON.stringify(visible2));

  // ── 8. 模拟「另一台设备拉下来的顺序」：数组顺序与 sort_order 不一致 ────
  const shuffled = SEED.map((p, i) => ({ ...p, sort_order: [3000, 0, 2000, 1000][i] }));  // Alpha,Beta,Gamma,Delta
  await cdp.eval(`localStorage.setItem('pm_projects', ${JSON.stringify(JSON.stringify({ schema_version: 4, projects: shuffled }))});`);
  await cdp.send('Page.reload');
  await sleep(1800);
  const reordered = await cdp.eval(`[...document.querySelectorAll('.project-list [data-sortable-item] .project-name')].map(e => e.textContent.trim())`);
  check(JSON.stringify(reordered) === JSON.stringify(['Beta', 'Delta', 'Gamma', 'Alpha']),
    '按远端 sort_order 重排（跨设备顺序同步的关键）', JSON.stringify(reordered));

  // ── 9. 回归：筛选到只剩 1 项时拖动，不应把它扔到末尾 ────────────────────
  await cdp.eval(`localStorage.setItem('pm_projects', ${JSON.stringify(JSON.stringify({ schema_version: 4, projects: SEED }))});`);
  await cdp.send('Page.reload');
  await sleep(1800);
  await cdp.eval(`(() => { const i = document.querySelector('.project-search .search-input'); i.value = 'gam'; i.dispatchEvent(new Event('input', { bubbles: true })); })()`);
  await sleep(400);
  const only = await cdp.eval(`[...document.querySelectorAll('.project-list [data-sortable-item]')].map(e => e.dataset.sortableId)`);
  check(JSON.stringify(only) === JSON.stringify(['p3']), `筛选只剩 Gamma（${JSON.stringify(only)}）`);
  const onlyRow = await cdp.eval(rectOfJs(`document.querySelector('.project-list [data-sortable-item]')`));
  await dragMouseTo(cdp, { x: onlyRow.x - 30, y: onlyRow.y }, onlyRow.y + 40);
  const after9 = await cdp.eval(names);
  check(JSON.stringify(after9) === JSON.stringify(['Alpha', 'Beta', 'Gamma', 'Delta']),
    '筛选只剩 1 项时拖动 → 顺序不变（回归）', JSON.stringify(after9));

  console.log(fails === 0 ? '\n全部通过 ✅' : `\n${fails} 个失败 ❌`);
  await fetch(`http://127.0.0.1:${PORT}/json/close/${target.id}`);
  process.exit(fails === 0 ? 0 : 1);
})().catch(e => { console.error('运行异常', e); process.exit(1); });
