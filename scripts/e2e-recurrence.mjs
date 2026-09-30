/**
 * 循环任务端到端验证（CDP 驱动 headless Chrome）
 *
 * 覆盖：
 *   - 完成循环任务 → 原任务 done + 生成下一个实例（due_date 正确、sourceTaskId 指向原任务、
 *     子任务重置、规则被消费）
 *   - 列表出现 ↻ 标记；详情面板显示 summarize 后的规则并可修改/清除
 *   - 规则用尽（count=0）时不再生成新实例
 *
 * 前置：pnpm dev + 9223 端口的 headless Chrome
 * 运行：pnpm test:e2e-recurrence
 */
const PORT = Number(process.env.CDP_PORT || 9223);
const APP = 'http://localhost:1420/';
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

class CDP {
  constructor(ws) { this.ws = ws; this.id = 0; this.pending = new Map(); this.exceptions = []; }
  static async connect(url) {
    const ws = new WebSocket(url);
    await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });
    const c = new CDP(ws);
    ws.onmessage = (ev) => {
      const m = JSON.parse(ev.data);
      if (m.method === 'Runtime.exceptionThrown') c.exceptions.push((m.params.exceptionDetails.exception?.description || '').split('\n')[0].slice(0, 300));
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
    const r = await this.send('Runtime.evaluate', { expression, returnByValue: true });
    if (r.exceptionDetails) throw new Error('eval: ' + (r.exceptionDetails.exception?.description || JSON.stringify(r.exceptionDetails)));
    return r.result.value;
  }
}

let fails = 0;
const check = (cond, msg, extra = '') => {
  if (cond) console.log('ok   ', msg);
  else { fails++; console.log('FAIL ', msg, extra); }
};
const clickText = async (cdp, selector, text) => {
  const ok = await cdp.eval(`(() => { const e = [...document.querySelectorAll(${JSON.stringify(selector)})].find(x => x.textContent.includes(${JSON.stringify(text)})); if (!e) return false; e.click(); return true; })()`);
  await sleep(400);
  return ok;
};

async function preflight() {
  try { await fetch('http://localhost:1420/', { method: 'HEAD' }); }
  catch { console.error('✗ 开发服务器未启动：请先运行 `pnpm dev`'); process.exit(2); }
  try {
    const r = await fetch(`http://127.0.0.1:${PORT}/json/version`);
    if (!r.ok) throw new Error(String(r.status));
  } catch {
    console.error('✗ 调试端口未就绪，请先启动：\n' +
      '  /opt/chrome-headless/chrome-headless-shell --headless --no-sandbox \\\n' +
      '    --remote-debugging-port=9223 --user-data-dir=/tmp/cdp-data about:blank');
    process.exit(2);
  }
}


const rectOfJs = (js) => `(() => { const e = ${js}; if (!e) return null; const r = e.getBoundingClientRect(); return { x: r.left + r.width/2, y: r.top + r.height/2, h: r.height, w: r.width }; })()`;

/** 鼠标拖拽（与 e2e-drag 同一套坐标语义） */
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
  await sleep(300);
}

const iso = (d) => new Date(d).toISOString().slice(0, 10);
const shift = (days) => iso(Date.now() + days * 86400000);
const GROUP_ID = 'group-rec';
const STATUS_TODO = 'status-rec-todo';
const STATUS_ACTIVE = 'status-rec-active';
const STATUS_DONE = 'status-rec-done';
const TASK_GROUPS = [{
  id: GROUP_ID, name: '默认任务组', sort_order: 1024, archived: false,
  initial_status_id: STATUS_TODO, completion_status_id: STATUS_DONE,
  statuses: [
    { id: STATUS_TODO, name: '待办', color: '#6b7280', category: 'todo', sort_order: 1024 },
    { id: STATUS_ACTIVE, name: '进行中', color: '#4f46e5', category: 'active', sort_order: 2048 },
    { id: STATUS_DONE, name: '已完成', color: '#10b981', category: 'done', sort_order: 3072 },
  ],
}];

/** 造一个带循环规则的任务项目并 reload */
async function seed(cdp, tasks) {
  const proj = {
    id: 'p-rec', name: 'RecurProj', description: '', color: '#4f46e5', template: 'default',
    created_at: '2026-01-01T00:00:00.000Z', updated_at: '2026-01-01T00:00:00.000Z',
    archived: false, sync_enabled: true, sort_order: 0,
    default_task_group_id: GROUP_ID, task_groups: structuredClone(TASK_GROUPS),
    tasks, tags: [], milestones: [], changelog: [], readme: '# x',
  };
  await cdp.eval(`localStorage.clear(); localStorage.setItem('pm_tutorial_seen','1'); localStorage.setItem('pm_projects', ${JSON.stringify(JSON.stringify({ schema_version: 4, projects: [proj] }))});`);
  await cdp.send('Page.reload');
  await sleep(2500);
}

const mkTask = (id, title, extra = {}) => ({
  id, title, description: 'desc', task_group_id: GROUP_ID, status_id: STATUS_TODO, completed_at: null, priority: 'high', tags: ['t1'], due_date: shift(0),
  due_time: null, start_offset: null, dependencies: [], comments: [], tracked_start: null, reminder: null,
  subtasks: [{ id: `${id}-s1`, title: 'sub', done: true }],
  created_at: '2026-01-01T00:00:00.000Z', updated_at: '2026-01-01T00:00:00.000Z',
  recurrence: null, ...extra,
});

const tasksOf = `JSON.parse(localStorage.pm_projects).projects[0].tasks`;

async function openList(cdp) {
  await cdp.eval(`document.querySelector('.project-list [data-sortable-item]')?.click()`);
  await sleep(600);
  await clickText(cdp, '.toggle-btn', '列表');
  await sleep(500);
}

(async () => {
  await preflight();
  const res = await fetch(`http://127.0.0.1:${PORT}/json/new?about:blank`, { method: 'PUT' });
  const target = await res.json();
  const cdp = await CDP.connect(target.webSocketDebuggerUrl);
  await cdp.send('Page.enable');
  await cdp.send('Runtime.enable');
  await cdp.send('Emulation.setDeviceMetricsOverride', { width: 1280, height: 950, deviceScaleFactor: 1, mobile: false });
  await cdp.send('Page.navigate', { url: APP });
  await sleep(2500);

  // ── 1. 完成循环任务 → 生成下一个实例 ─────────────────────────────────────
  await seed(cdp, [mkTask('t1', '每周例会', { recurrence: { freq: 'weekly', interval: 1, end: 'never' } })]);
  await openList(cdp);
  check(await cdp.eval(`!!document.querySelector('.rec-mark')`), '列表里显示了 ↻ 循环标记');

  await cdp.eval(`document.querySelector('.task-row .check')?.click()`);
  await sleep(900);
  let tasks = await cdp.eval(tasksOf);
  check(tasks.length === 2, '完成后面生成了新实例（共 2 条）', JSON.stringify(tasks.map(t => t.title)));
  const done = tasks.find(t => t.id === 't1');
  const next = tasks.find(t => t.id !== 't1');
  check(done?.status_id === STATUS_DONE && Boolean(done?.completed_at), '原任务进入完成状态并记录 completed_at');
  check(done?.recurrence === null, '原任务的 recurrence 被置 null（不会二次触发）');
  check(next?.task_group_id === GROUP_ID && next?.status_id === STATUS_TODO && next?.completed_at === null, '新实例保留任务组并回到初始状态');
  check(next?.due_date === shift(7), `新实例 due_date = 今天 +7 天（${shift(7)}）`, String(next?.due_date));
  check(next?.recurrence?.sourceTaskId === 't1', '新实例记录 sourceTaskId = 原任务');
  check(next?.recurrence?.freq === 'weekly' && next?.recurrence?.end === 'never', '新实例继承每周规则');
  check(next?.subtasks?.[0]?.done === false, '子任务被重置为未完成');
  check(next?.subtasks?.[0]?.id !== 't1-s1', '子任务换了新 id（避免跨实例串味）');
  const changelog = await cdp.eval(`JSON.parse(localStorage.pm_projects).projects[0].changelog`);
  check(changelog.length === 0, 'V1.1 不再为任务操作自动生成 changelog', JSON.stringify(changelog));

  // ── 2. 详情面板：摘要 + 修改 ──────────────────────────────────────────────
  await cdp.eval(`[...document.querySelectorAll('.task-row')].find(r => r.querySelector('.task-title')?.textContent.includes('每周例会') && !r.querySelector('.check.done'))?.click()`);
  await sleep(700);
  const selected = await cdp.eval(`document.querySelector('.rec-toggle')?.textContent.trim() ?? ''`);
  check(selected === '清除循环', '（前置）选中的是「带循环规则的未完成任务」', selected);
  const hasPanel = await cdp.eval(`!!document.querySelector('.rec-summary')`);
  check(hasPanel, '详情面板显示循环摘要');
  const summary = await cdp.eval(`document.querySelector('.rec-summary')?.innerText ?? ''`);
  check(summary.includes('每周'), '摘要内容正确（每周）', summary);

  // 切成「每月」并加截止日期
  await cdp.eval(`(() => { const sec = document.querySelector('.rec-summary')?.parentElement; const b = [...document.querySelectorAll('.rec-opt')].find(x => x.textContent.trim() === '每月'); b?.click(); })()`);
  await sleep(600);
  const changed = await cdp.eval(`JSON.parse(localStorage.pm_projects).projects[0].tasks.find(t => t.title === '每周例会' && t.status_id === ${JSON.stringify(STATUS_DONE)})?.recurrence`);
  check(changed === null || changed === undefined, '修改的是「当前任务」的规则（已完成任务 recurrence 仍为 null）', JSON.stringify(changed));

  // ── 3. 用尽规则（count=0）不再生成 ───────────────────────────────────────
  await seed(cdp, [
    mkTask('t2', '一次性循环', { recurrence: { freq: 'daily', interval: 1, end: 'count', count: 0 } }),
  ]);
  await openList(cdp);
  await cdp.eval(`document.querySelector('.task-row .check')?.click()`);
  await sleep(900);
  tasks = await cdp.eval(tasksOf);
  check(tasks.length === 1, 'count=0（已用尽）→ 不生成新实例', JSON.stringify(tasks.map(t => t.title)));
  check(tasks[0]?.status_id === STATUS_DONE, '原任务仍被标记完成');

  // ── 4. count=1 → 生成一次后规则归零 ─────────────────────────────────────
  await seed(cdp, [mkTask('t3', '两次循环', { recurrence: { freq: 'daily', interval: 2, end: 'count', count: 1 } })]);
  await openList(cdp);
  await cdp.eval(`document.querySelector('.task-row .check')?.click()`);
  await sleep(900);
  tasks = await cdp.eval(tasksOf);
  const spawned = tasks.find(t => t.title === '两次循环' && t.status_id === STATUS_TODO);
  check(!!spawned, 'count=1 → 生成一次新实例');
  check(spawned?.recurrence?.count === 0, '新实例的规则被消费为 count=0（下次不再生成）', JSON.stringify(spawned?.recurrence));
  check(spawned?.due_date === shift(2), 'daily interval=2 → 下次日期 = 今天 +2', String(spawned?.due_date));

  // ── 5. 清除循环 ─────────────────────────────────────────────────────────
  await cdp.eval(`[...document.querySelectorAll('.task-row')].find(r => r.querySelector('.task-title')?.textContent.includes('两次循环') && !r.querySelector('.check.done'))?.click()`);
  await sleep(700);
  await cdp.eval(`(() => { const b = [...document.querySelectorAll('.rec-toggle')].find(x => x.textContent.includes('清除')); b?.click(); })()`);
  await sleep(700);
  const cleared = await cdp.eval(`JSON.parse(localStorage.pm_projects).projects[0].tasks.find(t => t.title === '两次循环' && t.status_id === ${JSON.stringify(STATUS_TODO)})?.recurrence`);
  check(cleared === null, '「清除循环」把 recurrence 置回 null', JSON.stringify(cleared));
  check(!(await cdp.eval(`!!document.querySelector('.rec-summary')`)), '清除后摘要区域消失');

  // ── 6. 看板：拖到「已完成」列也生成下一实例 ─────────────────────────────
  await seed(cdp, [mkTask('t4', '看板循环', { recurrence: { freq: 'weekly', interval: 1, end: 'never' } })]);
  await cdp.eval(`document.querySelector('.project-list [data-sortable-item]')?.click()`);
  await sleep(600);
  await clickText(cdp, '.toggle-btn', '看板');
  await sleep(600);

  const zones = await cdp.eval(`[...document.querySelectorAll('[data-sortable-zone]')].map(e => e.dataset.sortableZone)`);
  check(zones.length === 3, '看板三列就绪', JSON.stringify(zones));

  const card = await cdp.eval(rectOfJs(`document.querySelectorAll('[data-sortable-zone]')[0]?.querySelector('[data-sortable-item]')`));
  const doneCol = await cdp.eval(rectOfJs(`document.querySelectorAll('[data-sortable-zone]')[2]`));
  check(!!card && !!doneCol, '（前置）取到卡片与「已完成」列坐标');
  if (card && doneCol) {
    await dragMouseTo(cdp, card, doneCol.y, doneCol.x);
    const after = await cdp.eval(tasksOf);
    check(after.length === 2, '★ 看板拖到「已完成」列 → 生成下一实例', JSON.stringify(after.map(t => `${t.title}:${t.status_id}`)));
    const finished = after.find(t => t.id === 't4');
    const spawned = after.find(t => t.id !== 't4');
    check(finished?.status_id === STATUS_DONE && finished?.recurrence === null, '原卡片被完成且 recurrence 置 null');
    check(spawned?.due_date === shift(7), '新实例 due_date = +7 天', String(spawned?.due_date));
    check(spawned?.recurrence?.sourceTaskId === 't4', '新实例 sourceTaskId 指向原任务');
  }

  // 普通任务拖到已完成列仍走 updateTask（不生成新实例）
  await seed(cdp, [mkTask('t5', '普通任务')]);
  await cdp.eval(`document.querySelector('.project-list [data-sortable-item]')?.click()`);
  await sleep(600);
  await clickText(cdp, '.toggle-btn', '看板');
  await sleep(600);
  const card2 = await cdp.eval(rectOfJs(`document.querySelectorAll('[data-sortable-zone]')[0]?.querySelector('[data-sortable-item]')`));
  const doneCol2 = await cdp.eval(rectOfJs(`document.querySelectorAll('[data-sortable-zone]')[2]`));
  if (card2 && doneCol2) {
    await dragMouseTo(cdp, card2, doneCol2.y, doneCol2.x);
    const after2 = await cdp.eval(tasksOf);
    check(after2.length === 1 && after2[0].status_id === STATUS_DONE, '普通任务拖到「已完成」列：只改状态、不生成新任务', JSON.stringify(after2.map(t => t.status_id)));
  }

  console.log(fails === 0 ? '\n全部通过 ✅' : `\n${fails} 个失败 ❌`);
  await fetch(`http://127.0.0.1:${PORT}/json/close/${target.id}`);
  process.exit(fails === 0 ? 0 : 1);
})().catch((e) => { console.error('运行异常', e); process.exit(1); });
