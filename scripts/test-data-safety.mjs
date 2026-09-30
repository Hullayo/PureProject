/**
 * 数据安全纯逻辑自测（无测试框架，直接断言）
 *
 * 覆盖：
 *   1. `.pm` 校验：畸形文件必须被**带字段路径**地拒绝
 *   2. 格式迁移：旧文件（无 schema_version）自动升到当前版本，且不吞数据
 *   3. 全量备份：往返一致、不含凭据、合并策略（新增/覆盖/跳过）
 *   4. 存储健康：主存储写入失败必须返回 false 并置为可见的 error 状态
 *   5. 外部副本（v0.5.5）：移动端/跨平台路径**不得**再冒充主存储失败（真机踩坑回归）
 *
 * 运行：pnpm test:data
 */
import { parsePmText, validatePm, formatPmErrors, CURRENT_PM_SCHEMA, detectSchemaVersion } from '$lib/utils/pm-schema';
import { buildBackup, parseBackup, mergeBackupInto, backupFileName, BACKUP_KIND } from '$lib/utils/backup';
import { safeSetItem, refreshStorageUsage, estimateLocalStorageBytes, fmtBytes, storageHealth, externalSyncIssues, reportExternalSyncIssue, clearExternalSyncIssue, ownerMarks, setOwnerMark, clearOwnerMark } from '$lib/stores/storage-health';
import { planLocalFileWrite, planLocalFileWriteWithOwner, isForeignDesktopPath, pmFileName, pmFilePath } from '$lib/utils/local-file-target';
import { resolveInheritedStorage, withInheritedStorage, normalizeProjectName } from '$lib/utils/storage-inherit';
import { storagePrefs, rememberStoragePref, getStoragePref, forgetStoragePref, storagePrefCount } from '$lib/utils/storage-prefs';
import { buildManifestUrl, getUpdateSource, DEFAULT_UPDATE_HOST, DEFAULT_UPDATE_PORT, DEFAULT_GITHUB_REPO } from '$lib/updater/config';
import { getSyncMode, SYNC_KEYS } from '$lib/sync/config';
import { get } from 'svelte/store';

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

// ─── localStorage 桩（带容量，便于测“写满”） ────────────────────────────────
function installLocalStorage(capacityBytes = Infinity) {
  const map = new Map();
  const used = () => [...map.entries()].reduce((n, [k, v]) => n + (k.length + v.length) * 2, 0);
  globalThis.localStorage = {
    get length() { return map.size; },
    key: (i) => [...map.keys()][i] ?? null,
    getItem: (k) => (map.has(k) ? map.get(k) : null),
    setItem: (k, v) => {
      const next = used() - (map.has(k) ? (k.length + String(map.get(k)).length) * 2 : 0) + (k.length + String(v).length) * 2;
      if (next > capacityBytes) {
        const err = new Error('The quota has been exceeded.');
        err.name = 'QuotaExceededError';
        throw err;
      }
      map.set(k, String(v));
    },
    removeItem: (k) => { map.delete(k); },
    clear: () => { map.clear(); },
  };
  return map;
}

const mkPm = (name, extra = {}) => ({
  version: '1.0',
  schema_version: CURRENT_PM_SCHEMA,
  project: { name, description: '', color: '#4f46e5', created_at: '2026-01-01T00:00:00.000Z', updated_at: '2026-01-02T00:00:00.000Z', template: 'default', default_task_group_id: 'g1' },
  task_groups: [{
    id: 'g1', name: '默认任务组', sort_order: 1024, archived: false,
    initial_status_id: 's-todo', completion_status_id: 's-done',
    statuses: [
      { id: 's-todo', name: '待办', color: '#6b7280', category: 'todo', sort_order: 1024 },
      { id: 's-active', name: '进行中', color: '#4f46e5', category: 'active', sort_order: 2048 },
      { id: 's-done', name: '已完成', color: '#10b981', category: 'done', sort_order: 3072 },
    ],
  }],
  tasks: [{ id: 't1', title: 'T1', task_group_id: 'g1', status_id: 's-todo', completed_at: null }],
  tags: [], milestones: [], changelog: [],
  ...extra,
});

// ─── 1. 校验 ────────────────────────────────────────────────────────────────
console.log('\n[1] .pm 校验');
{
  const bad = [
    ['空文件', ''],
    ['非 JSON', '{oops'],
    ['缺 project', JSON.stringify({ version: '1.0' })],
    ['project 非对象', JSON.stringify({ version: '1.0', project: [] })],
    ['缺 name', JSON.stringify({ version: '1.0', project: {} })],
    ['tasks 非数组', JSON.stringify({ version: '1.0', project: { name: 'A' }, tasks: {} })],
    ['task 缺 title', JSON.stringify({ version: '1.0', project: { name: 'A' }, tasks: [{ id: 'x' }] })],
    ['name 超长', JSON.stringify({ version: '1.0', project: { name: 'a'.repeat(600) } })],
  ];
  for (const [label, text] of bad) {
    const r = parsePmText(text, label);
    ok(!r.ok, `拒绝：${label}`, r.ok ? '（竟然通过了）' : '');
  }
  const good = parsePmText(JSON.stringify(mkPm('Good')), 'good.pm');
  ok(good.ok, '合法文件通过');
  eq(good.ok && good.pm.project.name, 'Good', '返回解析后的项目名');

  const badLegacyTemplate = parsePmText(JSON.stringify(mkPm('BadTemplate', {
    template: { dirs: [], files: ['bad.txt'], file_contents: { 'bad.txt': 123 } },
  })), 'bad-template.pm');
  ok(!badLegacyTemplate.ok, 'legacy 模板文件内容不是字符串时拒绝导入');

  // 旧数据中的未声明状态必须保留，并给出迁移警告
  const warn = parsePmText(JSON.stringify({
    version: '1.0', schema_version: 3,
    project: { name: 'W', description: '', color: '#fff', created_at: 'a', updated_at: 'b', kanban_columns: ['todo', 'done'] },
    tasks: [{ id: 't', title: 'T', status: 'weird' }],
  }), 'warn.pm');
  ok(warn.ok && warn.warnings.length === 1, '未知 status → 警告但允许导入');
  ok(warn.ok && warn.pm.task_groups[0].statuses.some(status => status.name === 'weird'), '未知 status 被保留为稳定状态定义');
}

// ─── 2. 迁移 ────────────────────────────────────────────────────────────────
console.log('\n[2] 格式迁移');
{
  const legacy = {
    version: '1.0',
    project: { name: 'Legacy', description: '', color: '#fff', created_at: 'a', updated_at: 'b' },
    tasks: [{ id: 't1', title: 'T1' }],
  };
  eq(detectSchemaVersion(legacy), 1, '无 schema_version → v1');
  eq(detectSchemaVersion(mkPm('x')), CURRENT_PM_SCHEMA, '有 schema_version → 当前版本');

  const r = parsePmText(JSON.stringify(legacy), 'legacy.pm');
  ok(r.ok, 'v1 文件可导入');
  eq(r.ok && r.pm.schema_version, CURRENT_PM_SCHEMA, `迁移后被打上 schema_version=${CURRENT_PM_SCHEMA}`);
  eq(r.ok && r.migratedFrom, 1, '记录迁移来源 v1');
  eq(r.ok && r.pm.tasks[0].due_time, null, '迁移补齐可空字段（due_time=null）');
  eq(r.ok && Array.isArray(r.pm.tasks[0].subtasks), true, '迁移补齐数组字段（subtasks=[]）');
  eq(r.ok && r.pm.tags, [], '迁移补齐 tags=[]');

  // 迁移绝不能“修正”类型错误（那等于吞数据）
  const wrongType = { version: '1.0', project: { name: 'X' }, tasks: { a: 1 } };
  const wr = parsePmText(JSON.stringify(wrongType), 'type.pm');
  ok(!wr.ok, '类型错误的 tasks 会被拒绝（迁移不掩盖问题）');
}

// ─── 2b. v2/v3 → v4：循环字段与四级结构 ────────────────────────────────────
console.log('\n[2b] v2/v3 → v4');
{
  const v2 = parsePmText(JSON.stringify({
    version: '1.0', schema_version: 2,
    project: { name: 'V2', description: '', color: '#fff', created_at: 'a', updated_at: 'b' },
    tasks: [{ id: 't1', title: 'T1', status: 'todo' }],
  }), 'v2.pm');
  ok(v2.ok, 'v2 文件可导入（自动升到 v4）');
  eq(v2.ok && v2.pm.schema_version, 4, 'v2 → v4');
  eq(v2.ok && v2.pm.tasks[0].recurrence, null, 'v2→v4 迁移：旧任务补 recurrence = null');
  ok(v2.ok && Boolean(v2.pm.tasks[0].task_group_id) && Boolean(v2.pm.tasks[0].status_id), 'v2→v4 任务补齐组和状态外键');
  ok(v2.ok && !('isRecurring' in v2.pm.tasks[0]), '不引入 isRecurring 冗余字段');

  const withRec = parsePmText(JSON.stringify({
    version: '1.0', schema_version: 3,
    project: { name: 'V3', description: '', color: '#fff', created_at: 'a', updated_at: 'b' },
    tasks: [{ id: 't1', title: 'T1', recurrence: { freq: 'weekly', interval: 2, byWeekday: [1, 3], end: 'count', count: 5 } }],
  }), 'v3.pm');
  ok(withRec.ok, '合法的 recurrence 通过校验');
  eq(withRec.ok && withRec.pm.tasks[0].recurrence.freq, 'weekly', 'recurrence 字段被完整保留');
  eq(withRec.ok && withRec.pm.schema_version, 4, 'v3 → v4');

  const badRec = parsePmText(JSON.stringify({
    version: '1.0', schema_version: 3,
    project: { name: 'Bad', description: '', color: '#fff', created_at: 'a', updated_at: 'b' },
    tasks: [{ id: 't1', title: 'T1', recurrence: {} }],
  }), 'bad-rec.pm');
  ok(!badRec.ok, '★ recurrence:{} 被拒绝（而不是被迁移吞成 null）', badRec.ok ? '' : JSON.stringify(badRec.errors[0]));

  const badFreq = parsePmText(JSON.stringify({
    version: '1.0', schema_version: 3,
    project: { name: 'BadFreq', description: '', color: '#fff', created_at: 'a', updated_at: 'b' },
    tasks: [{ id: 't1', title: 'T1', recurrence: { freq: 'hourly', interval: 1 } }],
  }), 'bad-freq.pm');
  ok(!badFreq.ok, 'recurrence.freq 非法值被拒绝');

  // 往返：导出 → 导入 一致
  const round = parsePmText(JSON.stringify(withRec.ok ? withRec.pm : {}), 'roundtrip.pm');
  eq(round.ok && round.pm.tasks[0].recurrence, withRec.ok ? withRec.pm.tasks[0].recurrence : null, 'recurrence 往返一致');
  eq(round.ok && round.pm.task_groups, withRec.ok ? withRec.pm.task_groups : null, 'v4 任务组和状态定义往返一致');

  const deterministicSource = {
    version: '1.0', schema_version: 3,
    project: { id: 'p-deterministic', name: 'Deterministic', description: '', color: '#fff', created_at: 'a', updated_at: 'b', kanban_columns: ['backlog', 'in_progress', 'done'] },
    tasks: [
      { id: 't-backlog', title: 'Backlog', status: 'backlog' },
      { id: 't-custom', title: 'Custom', status: 'qa_review' },
      { id: 't-done', title: 'Done', status: 'done', updated_at: '2026-03-04T05:06:07.000Z' },
    ],
  };
  const deterministicA = parsePmText(JSON.stringify(deterministicSource), 'device-a.pm');
  const deterministicB = parsePmText(JSON.stringify(deterministicSource), 'device-b.pm');
  eq(deterministicA.ok && deterministicA.pm.project.default_task_group_id, deterministicB.ok && deterministicB.pm.project.default_task_group_id, '不同设备生成相同的任务组 ID');
  eq(deterministicA.ok && deterministicA.pm.task_groups[0].statuses.map(status => status.id), deterministicB.ok && deterministicB.pm.task_groups[0].statuses.map(status => status.id), '不同设备生成相同的状态 ID');
  ok(deterministicA.ok && deterministicA.pm.task_groups[0].statuses.some(status => status.name === 'backlog'), '自定义 kanban_columns 被迁移');
  ok(deterministicA.ok && deterministicA.pm.task_groups[0].statuses.some(status => status.name === 'qa_review'), '任务使用的未声明状态被保留');
  eq(deterministicA.ok && deterministicA.pm.tasks.find(task => task.id === 't-done')?.completed_at, '2026-03-04T05:06:07.000Z', '旧完成任务用 updated_at 补 completed_at');

  const future = parsePmText(JSON.stringify({ ...mkPm('Future'), schema_version: CURRENT_PM_SCHEMA + 1 }), 'future.pm');
  ok(!future.ok && future.errors.some(error => error.path === 'schema_version'), '未来 schema 被明确拒绝');
}

// ─── 2c. v4 引用完整性 ─────────────────────────────────────────────────────
console.log('\n[2c] v4 引用完整性');
{
  const reject = (label, mutate) => {
    const sample = structuredClone(mkPm(label));
    mutate(sample);
    const result = parsePmText(JSON.stringify(sample), `${label}.pm`);
    ok(!result.ok, `拒绝：${label}`, result.ok ? '（竟然通过了）' : formatPmErrors(result.errors));
  };

  reject('缺失任务组引用', sample => { sample.tasks[0].task_group_id = 'missing'; });
  reject('状态不属于任务组', sample => {
    sample.task_groups.push({ id: 'g2', name: 'G2', sort_order: 2048, archived: false, initial_status_id: 's2-todo', completion_status_id: 's2-done', statuses: [
      { id: 's2-todo', name: 'Todo', color: '#666', category: 'todo', sort_order: 1024 },
      { id: 's2-done', name: 'Done', color: '#090', category: 'done', sort_order: 2048 },
    ] });
    sample.tasks[0].status_id = 's2-todo';
  });
  reject('重复任务组 ID', sample => { sample.task_groups.push(structuredClone(sample.task_groups[0])); });
  reject('重复状态 ID', sample => { sample.task_groups[0].statuses[1].id = 's-todo'; });
  reject('重复任务 ID', sample => { sample.tasks.push(structuredClone(sample.tasks[0])); });
  reject('完成状态类别无效', sample => { sample.task_groups[0].statuses.find(status => status.id === 's-done').category = 'active'; });
  reject('默认任务组已归档', sample => { sample.task_groups[0].archived = true; });
  reject('依赖目标缺失', sample => { sample.tasks[0].dependencies = [{ taskId: 'missing' }]; });
  reject('循环依赖', sample => {
    sample.tasks[0].dependencies = [{ taskId: 't2' }];
    sample.tasks.push({ id: 't2', title: 'T2', task_group_id: 'g1', status_id: 's-todo', completed_at: null, dependencies: [{ taskId: 't1' }] });
  });
  reject('未完成任务含完成时间', sample => { sample.tasks[0].completed_at = '2026-01-01T00:00:00.000Z'; });
  reject('完成任务缺完成时间', sample => { sample.tasks[0].status_id = 's-done'; sample.tasks[0].completed_at = null; });
}

// ─── 3. 备份 / 恢复 ─────────────────────────────────────────────────────────
console.log('\n[3] 全量备份与恢复');
{
  installLocalStorage();
  localStorage.setItem('pm_locale', 'zh');
  localStorage.setItem('pm_sync_token', 'SECRET-TOKEN');
  localStorage.setItem('pm_sync_pass', 'SECRET-PASS');
  localStorage.setItem('pm_sync_server_url', 'http://example.com:8787');

  const projects = [
    { id: 'p1', name: 'One', description: '', color: '#4f46e5', template: 'default', created_at: 'c', updated_at: '2026-01-01T00:00:00.000Z', archived: false, tasks: [], tags: [], changelog: [], milestones: [], readme: '' },
    { id: 'p2', name: 'Two', description: '', color: '#4f46e5', template: 'default', created_at: 'c', updated_at: '2026-02-01T00:00:00.000Z', archived: false, tasks: [], tags: [], changelog: [], milestones: [], readme: '' },
  ];
  const toPm = (p) => {
    const base = mkPm(p.name, { tasks: [] });
    return {
      ...base,
      project: { ...base.project, id: p.id, created_at: p.created_at, updated_at: p.updated_at },
      readme_file: 'README.md',
      template: { dirs: [], files: [], file_contents: {} },
    };
  };

  const json = buildBackup(projects, toPm);
  const raw = JSON.parse(json);
  eq(raw.kind, BACKUP_KIND, '备份包有 kind 标识');
  eq(raw.schema_version, CURRENT_PM_SCHEMA, '备份包带 schema_version');
  eq(raw.projects.length, 2, '包含全部项目（2 个）');
  eq(raw.settings.pm_locale, 'zh', '包含界面设置');
  eq('pm_sync_token' in raw.sync, false, '**不导出** 同步 token（安全）');
  eq('pm_sync_pass' in raw.sync, false, '**不导出** WebDAV 密码（安全）');
  eq(raw.sync.pm_sync_server_url, 'http://example.com:8787', '导出非敏感的服务器地址');
  ok(!json.includes('SECRET-TOKEN') && !json.includes('SECRET-PASS'), '备份文件正文里也不含任何凭据字串');

  // V1.1 不再生成模板文件，但必须无损透传旧 .pm 已有的文件内容。
  const { projectToPm, projectFromPm } = await import('$lib/utils/pm-convert');
  const legacyTemplatePm = mkPm('LegacyFiles', {
    readme_file: 'docs/README.md',
    template: {
      dirs: ['docs', 'src'],
      files: ['docs/README.md', 'src/legacy.txt'],
      file_contents: {
        'docs/README.md': '# Legacy README',
        'src/legacy.txt': 'keep this exact content',
      },
    },
  });
  const legacyProject = projectFromPm(legacyTemplatePm, 'legacy-files');
  ok(Boolean(legacyProject), '旧模板项目可重建为内部模型');
  const legacyRoundTrip = projectToPm(legacyProject);
  eq(legacyRoundTrip.readme_file, legacyTemplatePm.readme_file, 'legacy README 路径原样保留');
  eq(legacyRoundTrip.template, legacyTemplatePm.template, 'legacy 模板目录、文件列表和内容原样保留');
  ok(!('kanban_columns' in legacyRoundTrip.project), 'V4 导出不再写 kanban_columns');

  const cleanProject = { ...legacyProject, readme: '', legacy_template: undefined, legacy_readme_file: undefined };
  const cleanPm = projectToPm(cleanProject);
  ok(!('template' in cleanPm) && !('readme_file' in cleanPm), 'V1.1 新项目不生成 README 或模板文件');

  const parsed = parseBackup(json);
  ok(parsed.ok, '备份包可解析');
  eq(parsed.ok && parsed.projects.map((p) => p.project.name), ['One', 'Two'], '解析出 2 个项目');

  // 单项目 .pm 也能当备份导入
  const single = parseBackup(JSON.stringify(mkPm('Solo')));
  ok(single.ok && single.projects.length === 1, '单个 .pm 也能被当作备份导入');

  // 损坏包
  const broken = parseBackup('not json at all');
  ok(!broken.ok, '非 JSON 备份被拒绝');
  const wrongKind = parseBackup(JSON.stringify({ kind: 'other', projects: [] }));
  ok(!wrongKind.ok, '非本应用的备份被拒绝');

  // 坏项目跳过、好项目保留
  const mixed = { kind: BACKUP_KIND, schema_version: 2, projects: [mkPm('GoodAtom'), { version: '1.0', project: {} }] };
  const mixedRes = parseBackup(JSON.stringify(mixed));
  ok(mixedRes.ok && mixedRes.projects.length === 1 && mixedRes.skipped.length === 1,
    '坏条目跳过、好条目保留', mixedRes.ok ? JSON.stringify({ keep: mixedRes.projects.length, skip: mixedRes.skipped.length }) : mixedRes.error);

  // 合并语义
  const fromPm = (pm, id) => ({
    id: pm.project.id ?? id ?? pm.project.name, name: pm.project.name, description: '', color: '#4f46e5', template: 'default',
    created_at: 'c', updated_at: pm.project.updated_at ?? '2026-03-01T00:00:00.000Z', archived: false,
    tasks: [], tags: [], changelog: [], milestones: [], readme: '',
  });
  const local = [
    { id: 'p1', name: 'One(local-old)', updated_at: '2025-01-01T00:00:00.000Z' },
    { id: 'p9', name: 'Nine(local-only)', updated_at: '2026-01-01T00:00:00.000Z' },
  ];
  const incoming = [
    { ...mkPm('One(from-backup)'), project: { id: 'p1', name: 'One(from-backup)', updated_at: '2026-09-01T00:00:00.000Z', created_at: 'c', description: '', color: '#fff' } },
    { ...mkPm('Three(new)'), project: { id: 'p3', name: 'Three(new)', updated_at: '2026-09-01T00:00:00.000Z', created_at: 'c', description: '', color: '#fff' } },
  ];
  const { projects: merged, report } = mergeBackupInto(incoming, local, fromPm);
  eq(report.added, 1, '合并：新增 1 个（本地没有）');
  eq(report.replaced, 1, '合并：覆盖 1 个（备份里的 updated_at 更新）');
  eq(merged.length, 3, '合并：本地独有的项目不会被删除');
  ok(merged.some((p) => p.id === 'p9'), '合并：本地独有项目仍在');

  // 本地更新时跳过
  const incomingOld = [{ ...mkPm('One(old-backup)'), project: { id: 'p1', name: 'One(old-backup)', updated_at: '2020-01-01T00:00:00.000Z', created_at: 'c', description: '', color: '#fff' } }];
  const r2 = mergeBackupInto(incomingOld, [{ id: 'p1', name: 'One(newer-local)', updated_at: '2026-01-01T00:00:00.000Z' }], fromPm);
  eq(r2.report, { added: 0, replaced: 0, skipped: 1 }, '合并：本地更新时跳过（不倒退）');

  eq(backupFileName(new Date(2026, 8, 15, 13, 4)), 'ProjectManager-backup-20260915-1304.json', '备份文件名带时间戳');
}

// ─── 4. 存储健康（写入失败必须可见） ───────────────────────────────────────
console.log('\n[4] 存储健康');
{
  installLocalStorage(2000);
  const okWrite = safeSetItem('k1', 'v1', '测试键');
  eq(okWrite, true, '正常写入返回 true');
  eq(get(storageHealth).error, false, '正常写入不置错');

  const big = 'x'.repeat(5000);
  const badWrite = safeSetItem('k2', big, '测试大键');
  eq(badWrite, false, '配额写满返回 false（不再静默吞掉）');
  eq(get(storageHealth).error, true, '配额写满后置为可见的 error 状态');
  eq(get(storageHealth).problem, 'quota', '问题类型识别为 quota');
  ok(get(storageHealth).detail.includes('测试大键'), '错误详情包含写入对象');

  // 恢复正常后应自动消除告警
  const retry = safeSetItem('k3', 'ok', '测试键');
  eq(retry, true, '恢复后可正常写入');
  eq(get(storageHealth).error, false, '写入成功后自动清除告警');

  installLocalStorage();
  localStorage.setItem('a', '12345');
  const used = estimateLocalStorageBytes();
  ok(used > 0, `占用估算可用（${used} 字节）`);
  eq(fmtBytes(512), '512 B', 'fmtBytes: B');
  eq(fmtBytes(2048), '2.0 KB', 'fmtBytes: KB');
  eq(fmtBytes(2 * 1024 * 1024), '2.00 MB', 'fmtBytes: MB');
}

// ─── 5. 外部副本：失败必须与主存储分开 ─────────────────────────────────────
console.log('\n[5] 外部副本（.pm 本地文件）');
{
  // 5.1 决策：纯函数
  eq(planLocalFileWrite(null, false), { kind: 'none' }, '没配外部文件夹 → none');
  eq(planLocalFileWrite('', true), { kind: 'none' }, '空路径 → none');
  eq(planLocalFileWrite('/home/u/proj/x.pm', false), { kind: 'write', path: '/home/u/proj/x.pm' }, '桌面端正常路径 → write');
  eq(planLocalFileWrite('D:\\Senior\\x.pm', true), { kind: 'unsupported', path: 'D:\\Senior\\x.pm', reason: 'mobile' }, '**移动端 + Windows 路径 → 跳过（真机根因）**');
  eq(planLocalFileWrite('/storage/emulated/0/x.pm', true), { kind: 'unsupported', path: '/storage/emulated/0/x.pm', reason: 'mobile' }, '移动端任何路径 → 跳过');
  eq(planLocalFileWrite('D:\\Senior\\x.pm', false), { kind: 'foreignDesktopPath', path: 'D:\\Senior\\x.pm', reason: 'windows-drive' }, '桌面端遇到 Windows 盘符（如 Linux 上）→ 不盲目尝试');
  eq(planLocalFileWrite('\\\\srv\\share\\x.pm', false), { kind: 'foreignDesktopPath', path: '\\\\srv\\share\\x.pm', reason: 'unc' }, '桌面端遇到 UNC 路径 → 不盲目尝试');
  eq(isForeignDesktopPath('E:/SoftWare/x.pm'), 'windows-drive', 'isForeignDesktopPath: 正斜杠盘符也认');
  eq(isForeignDesktopPath('/home/u/x.pm'), null, 'isForeignDesktopPath: Unix 路径不误判');
  eq(isForeignDesktopPath('relative/x.pm'), null, 'isForeignDesktopPath: 相对路径不误判');
  // ★ 回归：Windows 本机上盘符/UNC 是合法路径，不能当异平台拒写
  eq(isForeignDesktopPath('E:\\SoftWare\\x.pm', 'windows'), null, '★ Windows 本机：盘符路径不算异平台');
  eq(isForeignDesktopPath('\\\\srv\\share\\x.pm', 'windows'), null, '★ Windows 本机：UNC 路径不算异平台');
  eq(isForeignDesktopPath('D:\\Senior\\x.pm', 'linux'), 'windows-drive', '非 Windows：盘符仍是异平台');
  eq(planLocalFileWrite('E:\\SoftWare\\x.pm', false, 'windows'), { kind: 'write', path: 'E:\\SoftWare\\x.pm' }, '★ Windows 本机 + 盘符 → write（不再跳过外部拷贝）');

  // ★ 回归（v0.7.2）：写外部副本必须用「目录 + 文件名」拼出的**文件**路径，
  //    绝不能把 storage 里的**目录**当目标路径（否则 rename(file, dir) → os error 5 拒绝访问）
  const st = (path) => ({ type: 'local', path });
  eq(pmFileName('MusicPlayer优化'), 'MusicPlayer优化.pm', 'pmFileName：普通名 + .pm');
  eq(pmFileName('a/b:c*d'), 'a_b_c_d.pm', 'pmFileName：Windows 非法字符替换为 _');
  eq(
    pmFilePath(st('E:\\SoftWare-HardWare-Learning-Guiding\\MusicPlayer'), 'MusicPlayer优化'),
    'E:\\SoftWare-HardWare-Learning-Guiding\\MusicPlayer/MusicPlayer优化.pm',
    '★ pmFilePath 拼出的是文件路径（不是目录）'
  );
  eq(pmFilePath(st('C:\\Users\\u\\Desktop\\'), 'x'), 'C:\\Users\\u\\Desktop/x.pm', 'pmFilePath：去掉目录末尾分隔符');
  eq(pmFilePath(st('E:/Data'), 'x'), 'E:/Data/x.pm', 'pmFilePath：正斜杠目录');
  eq(pmFilePath(undefined, 'x'), null, 'pmFilePath：无 storage → null');
  eq(pmFilePath({ type: 'server', url: 'x' }, 'x'), null, 'pmFilePath：server → null');
  eq(pmFilePath({ type: 'local', path: '   ' }, 'x'), null, 'pmFilePath：空白路径 → null');

  // 5.2 上报：按项目去重，且**不**置红（关键回归）
  installLocalStorage();
  eq(get(storageHealth).error, false, '起点：无告警');
  reportExternalSyncIssue({ projectId: 'p1', name: 'P1', path: 'D:\\x.pm', message: '当前平台不支持写入外部文件夹', unsupported: true });
  reportExternalSyncIssue({ projectId: 'p1', name: 'P1', path: 'D:\\x.pm', message: '当前平台不支持写入外部文件夹', unsupported: true });
  eq(Object.keys(get(externalSyncIssues)).length, 1, '同一项目重复失败 → 只保留 1 条（不刷屏、不涨内存）');
  reportExternalSyncIssue({ projectId: 'p2', name: 'P2', path: 'E:\\y.pm', message: 'path not found', unsupported: false });
  eq(Object.keys(get(externalSyncIssues)).length, 2, '不同项目各一条');
  eq(get(storageHealth).error, false, '★ 外部副本失败**不会**置红（真机反复弹条回归）');
  clearExternalSyncIssue('p1');
  eq(Object.keys(get(externalSyncIssues)), ['p2'], '单个清除');
  clearExternalSyncIssue('p2');
  eq(Object.keys(get(externalSyncIssues)).length, 0, '恢复后列表为空');
}

// ─── 6. 归属设备：非归属设备**不得**写本地 .pm（v0.6.0 回归）─────────────────
console.log('\n[6] 本地文件夹归属设备');
{
  const L = (path, owner) => ({ type: 'local', path, ...(owner ? { ownerDeviceId: owner, ownerDeviceName: 'Peer' } : {}) });
  const kind = (d) => d.kind;

  eq(kind(planLocalFileWriteWithOwner(undefined, false, 'devA')), 'none', '无 storage → none');
  eq(kind(planLocalFileWriteWithOwner({ type: 'server', url: 'x' }, false, 'devA')), 'none', 'server 类型 → none');
  eq(kind(planLocalFileWriteWithOwner(L(''), false, 'devA')), 'none', '无 path → none');

  eq(kind(planLocalFileWriteWithOwner(L('/x/y', 'devB'), false, 'devA')), 'notOwner', '★ 非归属设备 → notOwner');
  eq(planLocalFileWriteWithOwner(L('/x/y', 'devB'), false, 'devA').ownerDeviceName, 'Peer', 'notOwner 带上归属设备名（供 UI 展示）');
  eq(kind(planLocalFileWriteWithOwner(L('/x/y', 'devA'), false, 'devA')), 'write', '归属设备 → write');
  eq(kind(planLocalFileWriteWithOwner(L('/x/y'), false, 'devA')), 'write', '历史数据无 owner → 视为本机（首次写入后补写归属）');

  eq(kind(planLocalFileWriteWithOwner(L('D:\\x', ''), true, 'devA')), 'unsupported', '移动端 → unsupported');
  eq(kind(planLocalFileWriteWithOwner(L('D:\\x', 'devB'), true, 'devA')), 'unsupported', '移动端优先于归属判定（平台根本写不了）');
  eq(kind(planLocalFileWriteWithOwner(L('D:\\x'), false, 'devA')), 'foreignDesktopPath', '桌面端遇到 Windows 盘符 → foreignDesktopPath');
  eq(kind(planLocalFileWriteWithOwner(L('\\\\srv\\s\\x'), false, 'devA')), 'foreignDesktopPath', '桌面端遇到 UNC → foreignDesktopPath');
  // ★ 回归：Windows 本机上同样的盘符路径必须放行
  eq(kind(planLocalFileWriteWithOwner(L('E:\\SoftWare\\x'), false, 'devA', 'windows')), 'write', '★ Windows 本机归属设备 → write');
  eq(kind(planLocalFileWriteWithOwner(L('\\\\srv\\s\\x'), false, 'devA', 'windows')), 'write', '★ Windows 本机 UNC → write');

  // ★ 关键：notOwner 分支绝不能进「外部副本问题清单」，也不能置红
  installLocalStorage();
  eq(get(storageHealth).error, false, '起点：无红色告警');
  eq(Object.keys(get(externalSyncIssues)).length, 0, '起点：外部副本清单为空');
  const decision = planLocalFileWriteWithOwner(L('/x/y', 'devB'), false, 'devA');
  eq(decision.kind, 'notOwner', '（前置）判定为非归属');
  // 模拟 savePmFile 里 notOwner 分支的行为：只 clear + 打只读角标，不上报
  clearExternalSyncIssue('p-owner');
  eq(Object.keys(get(externalSyncIssues)).length, 0, '★ notOwner 分支不会写入 externalSyncIssues');
  eq(get(storageHealth).error, false, '★ notOwner 分支不会置红条');

  // 任务 2：非归属设备改为只读角标（ownerMarks）——不弹窗、不进清单、不置红
  setOwnerMark('p-owner', decision.ownerDeviceName || decision.ownerDeviceId.slice(0, 8));
  eq(get(ownerMarks)['p-owner']?.ownerName, 'Peer', '★ notOwner 写入 ownerMarks（只读角标）');
  eq(Object.keys(get(externalSyncIssues)).length, 0, '★ ownerMark 不会污染外部副本清单');
  eq(get(storageHealth).error, false, '★ ownerMark 不会置红条');
  clearOwnerMark('p-owner');
  eq(Object.keys(get(ownerMarks)).length, 0, 'cleanup: clearOwnerMark 生效');
}

// ─── 7. storage 三级兜底（v0.6.1：修复「本地文件夹突然丢失」） ─────────────
console.log('\n[7] storage 三级兜底（同 ID → 同名 → 保留）');
{
  const P = (id, name, storage) => ({ id, name, storage });
  const LOCAL = { type: 'local', path: 'D:\\Senior' };
  const LOCAL2 = { type: 'local', path: '/home/u/other' };

  eq(normalizeProjectName('  Senior  '), 'senior', '项目名归一化：去空白 + 小写');

  // 1) 同 ID 覆盖 → 沿用本地 storage
  {
    const local = [P('p1', 'Senior', LOCAL)];
    const incoming = P('p1', 'Senior 改名', undefined);
    eq(resolveInheritedStorage(incoming, local), LOCAL, '★ 同 ID 覆盖 → storage 保留');
    eq(withInheritedStorage(incoming, local).storage, LOCAL, '★ withInheritedStorage 同 ID → storage 保留');
  }

  // 2) 同名不同 ID 覆盖 → 沿用本地 storage
  {
    const local = [P('old-id', 'Senior', LOCAL)];
    const incoming = P('new-id', 'senior', undefined); // 大小写不同也算同名
    eq(resolveInheritedStorage(incoming, local), LOCAL, '★ 同名不同 ID → storage 保留（大小写不敏感）');
    eq(withInheritedStorage(incoming, local).storage, LOCAL, '★ withInheritedStorage 同名不同 ID → storage 保留');
  }

  // 3) 本地无该项目（跨设备恢复 / 全新） → undefined 且不崩
  {
    eq(resolveInheritedStorage(P('x', 'Brand New', undefined), []), undefined, '★ 本地为空 → storage 为 undefined（不崩）');
    eq(resolveInheritedStorage(P('x', 'Brand New', undefined), [P('y', 'Other', LOCAL)]), undefined, '★ 无同 ID / 同名 → undefined');
    const w = withInheritedStorage(P('x', 'Brand New', undefined), [P('y', 'Other', LOCAL)]);
    eq(w.storage, undefined, '★ 无匹配时保留原值（undefined）');
    ok(w.name === 'Brand New', '无匹配时不误改其它字段');
  }

  // 4) 显式传入 byId（调用方已按 ID 查到）优先于同名
  {
    const byId = P('p1', 'Senior', LOCAL2);
    const local = [byId, P('p2', 'Senior', LOCAL)];
    eq(resolveInheritedStorage(P('p1', 'Senior', undefined), local, byId), LOCAL2, 'byId 优先于同名项');
  }

  // 5) incoming 自带 storage 且本地无匹配 → 保留 incoming.storage
  {
    const incoming = P('z', 'Z', LOCAL);
    eq(resolveInheritedStorage(incoming, []), LOCAL, '本地无匹配 → 保留 incoming.storage');
  }

  // 6) 同名但本地那项没有 storage → 不误传 undefined（返回 incoming）
  {
    const local = [P('a', 'Same', undefined)];
    const incoming = P('b', 'Same', LOCAL);
    eq(resolveInheritedStorage(incoming, local), LOCAL, '同名项无 storage → 保留 incoming.storage');
  }
}

// ─── 8. 本地文件夹偏好记录（丢失检测 / 一键恢复的旁路数据） ─────────────────
console.log('\n[8] 本地文件夹偏好记录');
{
  installLocalStorage();
  storagePrefs.set({});
  eq(storagePrefCount(), 0, '起点：记录为空');

  const LOCAL = { type: 'local', path: 'D:\\Senior', ownerDeviceId: 'devA' };
  rememberStoragePref('Senior', LOCAL);
  eq(getStoragePref('  senior '), LOCAL, '按项目名（归一化）能取回记录');
  eq(storagePrefCount(), 1, '记录数 +1');
  ok(localStorage.getItem('pm_storage_prefs')?.includes('D:\\\\Senior'), '已落盘到 pm_storage_prefs（走 safeSetItem）');

  // 非本地 / 无路径不记录
  rememberStoragePref('ServerProj', { type: 'server', url: 'http://x' });
  rememberStoragePref('NoPath', { type: 'local' });
  eq(storagePrefCount(), 1, 'server 类型 / 无路径不记录');

  // 重复记录同路径不重复写（幂等）
  rememberStoragePref('senior', LOCAL);
  eq(storagePrefCount(), 1, '重复记录不增长');

  forgetStoragePref('SENIOR');
  eq(storagePrefCount(), 0, '忘记后清空');
  eq(getStoragePref('Senior'), undefined, '忘记后读不到');
}

// ─── 9. 更新源配置（默认 GitHub / 服务器地址留空）────────────────────────────
console.log('\n[9] 更新源配置');
{
  eq(DEFAULT_UPDATE_HOST, '', '默认服务器地址留空（不再内置 IP）');
  eq(DEFAULT_UPDATE_PORT, '', '默认端口留空');
  eq(DEFAULT_GITHUB_REPO, 'AlicDanclic/ProjectManager', '默认 GitHub 仓库');
  eq(getUpdateSource(), 'github', '★ 未配置过时默认 GitHub（首次安装走 GitHub）');

  const gh = buildManifestUrl(
    { source: 'github', host: '', port: '', repo: DEFAULT_GITHUB_REPO },
    'desktop'
  );
  eq(gh, 'https://github.com/AlicDanclic/ProjectManager/releases/latest/download/latest.json', 'GitHub 桌面清单地址');
  eq(
    buildManifestUrl({ source: 'github', host: '', port: '', repo: 'o/r' }, 'android'),
    'https://github.com/o/r/releases/latest/download/android.json',
    'GitHub Android 清单地址'
  );

  eq(buildManifestUrl({ source: 'server', host: '', port: '', repo: '' }, 'desktop'), '', '★ 服务器地址未填 → 空串（由调用方提示）');
  eq(
    buildManifestUrl({ source: 'server', host: 'example.com', port: '8080', repo: '' }, 'desktop'),
    'http://example.com:8080/updates/latest.json',
    '服务器清单地址（带端口）'
  );
  eq(
    buildManifestUrl({ source: 'server', host: 'example.com', port: '', repo: '' }, 'android'),
    'http://example.com/updates/android.json',
    '服务器端口留空 → 不拼端口'
  );
  eq(
    buildManifestUrl({ source: 'server', host: 'https://example.com/', port: '8443', repo: '' }, 'desktop'),
    'https://example.com:8443/updates/latest.json',
    'https + 去末尾斜杠 + 拼端口'
  );
}

// ─── 10. 同步模式默认值 ──────────────────────────────────────────────────────
console.log('\n[10] 同步模式默认值');
{
  installLocalStorage();
  eq(getSyncMode(), 'auto', '未配置同步模式时默认自动同步');
  localStorage.setItem(SYNC_KEYS.mode, 'manual');
  eq(getSyncMode(), 'manual', '用户明确选择手动模式时保留手动同步');
  localStorage.setItem(SYNC_KEYS.mode, 'auto');
  eq(getSyncMode(), 'auto', '用户选择自动模式时启用自动同步');
  localStorage.setItem(SYNC_KEYS.mode, 'invalid');
  eq(getSyncMode(), 'auto', '未知同步模式安全回退到自动同步');
}

console.log(fails === 0 ? '\n全部通过 ✅' : `\n${fails} 个失败 ❌`);
process.exit(fails === 0 ? 0 : 1);
