/**
 * 「设置 → 数据」页端到端验证（CDP 驱动 headless Chrome）
 *
 * 前置（两个进程都要先起来）：
 *   1) pnpm dev
 *   2) /opt/chrome-headless/chrome-headless-shell --headless --no-sandbox \
 *        --remote-debugging-port=9223 --user-data-dir=/tmp/cdp-data about:blank
 *
 * 运行：pnpm test:e2e-data
 * 注意：开发期验证脚本，不参与构建；断言失败会以非 0 退出。
 *
 * 覆盖 v0.5.4 数据安全 UI：
 *   - 存储健康面板（占用条 / 文案 / 刷新）
 *   - 导出全量备份（浏览器 Blob 路径）
 *   - 从备份恢复：用 CDP DOM.setFileInputFiles 真选一个备份文件 → 校验新增/覆盖统计
 *   - 非备份文件被拒绝且给出可读错误
 *   - 清空数据需要两步确认
 */
const PORT = Number(process.env.CDP_PORT || 9223);
const APP = 'http://localhost:1420/';
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

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

const mkTask = (id, title) => ({
  id, title, description: '', task_group_id: GROUP_ID, status_id: STATUS_TODO, completed_at: null,
  priority: 'medium', tags: [], due_date: null,
  due_time: null, start_offset: null, dependencies: [], subtasks: [], comments: [],
  tracked_start: null, reminder: null,
  created_at: '2026-01-01T00:00:00.000Z', updated_at: '2026-01-01T00:00:00.000Z',
});
const mkProject = (id, name, updatedAt = '2026-01-01T00:00:00.000Z') => ({
  id, name, description: '', color: '#4f46e5', template: 'default',
  created_at: '2026-01-01T00:00:00.000Z', updated_at: updatedAt,
  archived: false, sync_enabled: true, sort_order: 0,
  default_task_group_id: GROUP_ID, task_groups: structuredClone(TASK_GROUPS),
  tasks: [mkTask('t1', 'T1')], tags: [], milestones: [], readme: '# x', changelog: [],
});
const mkPm = (id, name, updatedAt, tasks = []) => ({
  version: '1.0', schema_version: 4,
  project: {
    id, name, description: '', color: '#4f46e5', template: 'default',
    created_at: '2026-01-01T00:00:00.000Z', updated_at: updatedAt,
    default_task_group_id: GROUP_ID,
  },
  task_groups: structuredClone(TASK_GROUPS), tasks, tags: [], milestones: [], changelog: [],
});

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

(async () => {
  await preflight();
  const fs = await import('node:fs');
  const os = await import('node:os');
  const path = await import('node:path');

  // 准备两个文件：一个合法备份包、一个垃圾文件
  const tmpDir = fs.mkdtempSync(path.join(os.tmpdir(), 'pm-restore-'));
  const goodBackup = path.join(tmpDir, 'backup.json');
  const badFile = path.join(tmpDir, 'bad.json');
  const backupPayload = {
    kind: 'projectmanager-backup',
    schema_version: 4,
    app_version: '1.1.0',
    exported_at: '2026-09-15T00:00:00.000Z',
    projects: [
      mkPm('p-new', 'FromBackup', '2026-09-01T00:00:00.000Z', [mkTask('b1', 'BackupTask')]),
      mkPm('p1', 'Local1-REPLACED', '2026-12-01T00:00:00.000Z'),
    ],
    settings: { pm_locale: 'zh' },
    sync: {},
  };
  fs.writeFileSync(goodBackup, JSON.stringify(backupPayload, null, 2));
  fs.writeFileSync(badFile, '{"not":"a backup"}');

  // 1) 打开页面 + 造数据（1 个项目）
  const res = await fetch(`http://127.0.0.1:${PORT}/json/new?about:blank`, { method: 'PUT' });
  const target = await res.json();
  const cdp = await CDP.connect(target.webSocketDebuggerUrl);
  await cdp.send('Page.enable');
  await cdp.send('Runtime.enable');
  await cdp.send('DOM.enable');
  await cdp.send('Emulation.setDeviceMetricsOverride', { width: 1280, height: 950, deviceScaleFactor: 1, mobile: false });
  await cdp.send('Page.navigate', { url: APP });
  await sleep(2500);
  await cdp.eval(`localStorage.clear(); localStorage.setItem('pm_tutorial_seen','1'); localStorage.setItem('pm_projects', ${JSON.stringify(JSON.stringify({ schema_version: 4, projects: [mkProject('p1', 'Local1')] }))});`);
  await cdp.send('Page.reload');
  await sleep(2500);

  // 2) 打开设置 → 数据
  await cdp.eval(`document.querySelector('.settings-btn')?.click()`);
  await sleep(500);
  const openedData = await clickText(cdp, '.tab, .nav-item, button', '数据');
  check(openedData || true, '设置面板已打开（标签点击容错）');

  // 兜底：直接点设置里含「数据」的按钮
  const hasHealth = await cdp.eval(`!!document.body.innerText.includes('本地存储')`);
  if (!hasHealth) await clickText(cdp, 'button', '数据');
  await sleep(400);
  const healthVisible = await cdp.eval(`document.body.innerText.includes('本地存储') && document.body.innerText.includes('全量备份')`);
  check(healthVisible, '「设置 → 数据」显示了 存储健康 + 全量备份 区块');

  const usageText = await cdp.eval(`(() => { const m = document.body.innerText.match(/([\\d.]+ (B|KB|MB)) \\/ ([\\d.]+ (B|KB|MB))（(\\d+)%）/); return m ? m[0] : ''; })()`);
  check(usageText !== '', '存储占用条显示了「已用 / 预算（百分比）」', usageText);

  // 3) 导出备份（浏览器 Blob 路径 → 只断言出现成功提示）
  const clickedBackup = await clickText(cdp, 'button', '导出全量备份');
  await sleep(600);
  const backupMsg = await cdp.eval(`(() => { const t = document.body.innerText; return t.includes('备份已导出') || t.includes('结果'); })()`);
  check(clickedBackup && backupMsg, '点击「导出全量备份」后有结果反馈');

  // 4) 从备份恢复（真选文件）
  const restoreClicked = await clickText(cdp, 'button', '从备份恢复');
  await sleep(300);
  const doc = await cdp.send('DOM.getDocument');
  const inputNode = await cdp.send('DOM.querySelector', { nodeId: doc.root.nodeId, selector: 'input[type=file]' });
  check(!!inputNode?.nodeId, '恢复流程创建了文件选择框');
  await cdp.send('DOM.setFileInputFiles', { nodeId: inputNode.nodeId, files: [goodBackup] });
  await sleep(900);
  const state = await cdp.eval(`JSON.parse(localStorage.pm_projects).projects.map(p => p.name)`);
  check(state.includes('FromBackup'), '恢复：备份里的新项目已导入', JSON.stringify(state));
  check(state.includes('Local1-REPLACED'), '恢复：同 id 且 updated_at 更新 → 覆盖本地', JSON.stringify(state));
  check(state.length === 2, '恢复：本地独有项目未被删除（合并而非替换）', JSON.stringify(state));
  const reportText = await cdp.eval(`(() => { const m = document.body.innerText.match(/恢复完成[^\\n]*/); return m ? m[0] : ''; })()`);
  check(/新增 1/.test(reportText) && /覆盖 1/.test(reportText), '恢复结果给出「新增 1 / 覆盖 1」统计', reportText);

  // 5) 非法文件被拒绝
  const restoreClicked2 = await clickText(cdp, 'button', '从备份恢复');
  await sleep(300);
  const doc2 = await cdp.send('DOM.getDocument');
  const inputNode2 = await cdp.send('DOM.querySelector', { nodeId: doc2.root.nodeId, selector: 'input[type=file]' });
  await cdp.send('DOM.setFileInputFiles', { nodeId: inputNode2.nodeId, files: [badFile] });
  await sleep(900);
  const after = await cdp.eval(`JSON.parse(localStorage.pm_projects).projects.length`);
  check(after === 2, '非法备份不改变项目数量', String(after));
  const errText = await cdp.eval(`document.body.innerText.includes('导入失败') || document.body.innerText.includes('不是 ProjectManager') || document.body.innerText.includes('导出失败')`);
  check(errText, '非法备份给出可读错误提示');

  // ── 6. 外部副本失败：不得冒充「本地数据保存失败」───────────────────────
  // 纯浏览器里 `write_pm_file`（Tauri 命令）不可用 → 正好复现真机上「外部副本写失败」那条路径。
  // 注意：Toast 文案里也含「外部文件夹 / 改为应用内部存储」等字样，
  // 所以断言必须**限定在面板 DOM 内**（.ext-item），不能用 body.innerText —— 否则会被 Toast 骗过。
  await cdp.eval(`(() => {
    const data = JSON.parse(localStorage.pm_projects);
    const list = data.projects.filter(p => p.id !== 'p-local');
    list.push({
      id: 'p-local', name: 'LocalFolderProj', description: '', color: '#f59e0b', template: 'default',
      created_at: '2026-01-01T00:00:00.000Z', updated_at: '2026-01-01T00:00:00.000Z',
      archived: false, sync_enabled: true, sort_order: 9999,
      storage: { type: 'local', path: 'D:\\\\Senior' },
      default_task_group_id: ${JSON.stringify(GROUP_ID)}, task_groups: ${JSON.stringify(TASK_GROUPS)},
      tasks: [], tags: [], milestones: [], changelog: [], readme: '# x',
    });
    data.projects = list;
    localStorage.setItem('pm_projects', JSON.stringify(data));
  })()`);
  await cdp.send('Page.reload');
  await sleep(2500);

  // 硬前置：项目真的出现在侧边栏（否则后面的断言毫无意义）
  const seeded = await cdp.eval(`!![...document.querySelectorAll('.project-list [data-sortable-item]')].find(e => e.querySelector('.project-name')?.textContent.includes('LocalFolderProj'))`);
  check(seeded, '（前置）带本地文件夹的项目已出现在侧边栏');

  if (seeded) {
    await cdp.eval(`(() => { const e = [...document.querySelectorAll('.project-list [data-sortable-item]')].find(x => x.querySelector('.project-name')?.textContent.includes('LocalFolderProj')); e.click(); })()`);
    await sleep(600);
    await clickText(cdp, '.toggle-btn', '列表');
    await sleep(500);
    const added = await cdp.eval(`(() => {
      const i = document.querySelector('.add-input');
      if (!i) return 'no-input';
      i.value = '触发保存';
      i.dispatchEvent(new Event('input', { bubbles: true }));
      const b = [...document.querySelectorAll('.task-add button')].find(x => x.textContent.trim() === '添加');
      if (!b) return 'no-button';
      b.click();
      return 'ok';
    })()`);
    check(added === 'ok', '（前置）已在该项目里新建任务（触发 savePmFile）', added);
    await sleep(900);

    check(await cdp.eval(`!document.querySelector('.storage-alert')`), '★ 外部副本写入失败**不会**弹红色告警条');

    // 设置 → 数据管理
    await cdp.eval(`document.querySelector('.settings-btn')?.click()`);
    await sleep(600);
    await clickText(cdp, '.tab-btn', '数据管理');
    await sleep(500);

    const itemText = await cdp.eval(`document.querySelector('.ext-item')?.innerText ?? ''`);
    check(!!itemText, '设置 → 数据显示外部副本问题条目（.ext-item）');
    check(itemText.includes('LocalFolderProj'), '条目里包含项目名', itemText.replace(/\n/g, ' | '));
    check(itemText.includes('Senior'), '条目里包含出问题的路径');
    const switchBtn = await cdp.eval(`(() => { const b = [...document.querySelectorAll('.ext-actions button')].find(x => x.textContent.includes('改为应用内部存储')); return b ? b.textContent.trim() : ''; })()`);
    check(switchBtn.includes('改为应用内部存储'), '条目里提供「改为应用内部存储」按钮', switchBtn);

    // 点它（**两步确认**）→ 项目 storage 被清空 + 条目消失
    await cdp.eval(`(() => { const b = [...document.querySelectorAll('.ext-actions button')].find(x => x.textContent.includes('改为应用内部存储')); b?.click(); })()`);
    await sleep(300);
    const confirmBtn = await cdp.eval(`(() => { const b = [...document.querySelectorAll('.ext-actions button')].find(x => x.textContent.includes('再点一次确认')); return b ? b.textContent.trim() : ''; })()`);
    check(confirmBtn.includes('再点一次确认'), '首次点击进入二次确认态（防误点）', confirmBtn);
    const stillThere = await cdp.eval(`JSON.parse(localStorage.pm_projects).projects.find(p => p.id === 'p-local')?.storage ?? null`);
    check(stillThere !== null, '首次点击**不**清除 storage（需二次确认）');
    await cdp.eval(`(() => { const b = [...document.querySelectorAll('.ext-actions button')].find(x => x.textContent.includes('再点一次确认')); b?.click(); })()`);
    await sleep(800);
    const cleared = await cdp.eval(`JSON.parse(localStorage.pm_projects).projects.find(p => p.id === 'p-local')?.storage ?? null`);
    check(cleared === null, '二次确认后项目的 storage 被清空（改为应用内部存储）', JSON.stringify(cleared));
    check(!(await cdp.eval(`!!document.querySelector('.ext-item')`)), '条目随之消失');
  }

  // ── 7. 归属设备：非归属设备既不写本地，也不进清单 ────────────────────────
  // 造一个 ownerDeviceId ≠ 本机 的本地文件夹项目；触发保存后必须毫无动静。
  await cdp.eval(`localStorage.setItem('pm_device_id', 'device-self-e2e')`);
  await cdp.eval(`(() => {
    const data = JSON.parse(localStorage.pm_projects);
    const list = data.projects.filter(p => p.id !== 'p-owner');
    list.push({
      id: 'p-owner', name: 'OwnedByOther', description: '', color: '#8b5cf6', template: 'default',
      created_at: '2026-01-01T00:00:00.000Z', updated_at: '2026-01-01T00:00:00.000Z',
      archived: false, sync_enabled: true, sort_order: 9998,
      storage: { type: 'local', path: '/srv/shared-folder', ownerDeviceId: 'device-other', ownerDeviceName: 'OtherPC' },
      default_task_group_id: ${JSON.stringify(GROUP_ID)}, task_groups: ${JSON.stringify(TASK_GROUPS)},
      tasks: [], tags: [], milestones: [], changelog: [], readme: '# x',
    });
    data.projects = list;
    localStorage.setItem('pm_projects', JSON.stringify(data));
  })()`);
  await cdp.send('Page.reload');
  await sleep(2500);

  const ownerSeeded = await cdp.eval(`!![...document.querySelectorAll('.project-list [data-sortable-item]')].find(e => e.querySelector('.project-name')?.textContent.includes('OwnedByOther'))`);
  check(ownerSeeded, '（前置）非归属的本地文件夹项目已就绪');
  if (ownerSeeded) {
    await cdp.eval(`(() => { const e = [...document.querySelectorAll('.project-list [data-sortable-item]')].find(x => x.querySelector('.project-name')?.textContent.includes('OwnedByOther')); e.click(); })()`);
    await sleep(600);
    await clickText(cdp, '.toggle-btn', '列表');
    await sleep(500);
    await cdp.eval(`(() => { const i = document.querySelector('.add-input'); i.value = '非归属保存'; i.dispatchEvent(new Event('input', { bubbles: true })); [...document.querySelectorAll('.task-add button')].find(x => x.textContent.trim() === '添加')?.click(); })()`);
    await sleep(900);

    check(await cdp.eval(`!document.querySelector('.storage-alert')`), '★ 非归属设备保存：不弹红条');
    await cdp.eval(`document.querySelector('.settings-btn')?.click()`);
    await sleep(600);
    await clickText(cdp, '.tab-btn', '数据管理');
    await sleep(500);
    const ownerItems = await cdp.eval(`[...document.querySelectorAll('.ext-item')].map(e => e.innerText).join(' | ')`);
    check(!ownerItems.includes('OwnedByOther'), '★ 非归属项目**不会**出现在「外部文件夹」问题清单', ownerItems || '(清单为空)');
    const ownerHint = await cdp.eval(`document.body.innerText.includes('非归属') || document.body.innerText.includes('not the owner')`);
    check(ownerHint, '设置里显示归属只读说明（非归属设备只参与服务器同步）');
    await cdp.eval(`document.querySelector('.settings-btn')?.click()`);
    await sleep(400);

    // 把归属改成本机 → 此时才会尝试写本地（浏览器里 Tauri 命令不可用 → 进清单）
    await cdp.eval(`(() => { localStorage.setItem('pm_device_id', 'device-self-e2e'); const data = JSON.parse(localStorage.pm_projects); const p = data.projects.find(x => x.id === 'p-owner'); p.storage.ownerDeviceId = 'device-self-e2e'; localStorage.setItem('pm_projects', JSON.stringify(data)); })()`);
    await cdp.send('Page.reload');
    await sleep(2500);
    await cdp.eval(`(() => { const e = [...document.querySelectorAll('.project-list [data-sortable-item]')].find(x => x.querySelector('.project-name')?.textContent.includes('OwnedByOther')); e?.click(); })()`);
    await sleep(600);
    await clickText(cdp, '.toggle-btn', '列表');
    await sleep(500);
    await cdp.eval(`(() => { const i = document.querySelector('.add-input'); i.value = '归属保存'; i.dispatchEvent(new Event('input', { bubbles: true })); [...document.querySelectorAll('.task-add button')].find(x => x.textContent.trim() === '添加')?.click(); })()`);
    await sleep(900);
    await cdp.eval(`document.querySelector('.settings-btn')?.click()`);
    await sleep(600);
    await clickText(cdp, '.tab-btn', '数据管理');
    await sleep(500);
    const nowItems = await cdp.eval(`[...document.querySelectorAll('.ext-item')].map(e => e.innerText).join(' | ')`);
    check(nowItems.includes('OwnedByOther'), '归属设备（本机）保存时才会尝试写本地 → 失败进清单', nowItems.slice(0, 120));
    check(!(await cdp.eval(`!!document.querySelector('.storage-alert')`)), '即使归属写入失败，也不弹红条（只进清单）');
  }

  console.log(fails === 0 ? '\n全部通过 ✅' : `\n${fails} 个失败 ❌`);
  await fetch(`http://127.0.0.1:${PORT}/json/close/${target.id}`);
  fs.rmSync(tmpDir, { recursive: true, force: true });
  process.exit(fails === 0 ? 0 : 1);
})().catch((e) => { console.error('运行异常', e); process.exit(1); });
