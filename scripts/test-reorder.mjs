/**
 * 拖拽排序纯逻辑自测（无测试框架，直接断言）
 *
 * 覆盖最容易写错的换算：
 *  - 「筛选后的可视下标 / 看板列内下标」→「全量数组插入下标」
 *  - `sort_order` 的中点插入、追加、全量重排、历史数据迁移与排序
 *
 * 运行：pnpm test:reorder
 */
import { resolveInsertIndex } from '../src/lib/utils/reorder-target.ts';
import { sortProjects, migrateProjects, renumberProjects, nextOrder, ORDER_GAP, hasFullOrder } from '../src/lib/utils/project-order.ts';

let fails = 0;
const eq = (a, b, msg) => {
  const ok = JSON.stringify(a) === JSON.stringify(b);
  if (!ok) { fails++; console.log('FAIL', msg, '\n  got     ', JSON.stringify(a), '\n  expected', JSON.stringify(b)); }
  else console.log('ok  ', msg);
};

/** 模拟 store 的 reorder：splice(from,1) + splice(to,0) */
const applyMove = (arr, from, to) => { const c = [...arr]; const [m] = c.splice(from, 1); c.splice(to, 0, m); return c; };
const ids = (arr) => arr.map(x => x.id);

// ── 1. 全量列表（无过滤）下拖拽 ───────────────────────────────────────────────
{
  const all = ids([{id:'a'},{id:'b'},{id:'c'},{id:'d'}].map(x=>({...x})));
  const area = all.map(id => ({id}));
  const allObjs = all.map(id => ({id}));
  // 把 a 拖到末尾：指针在 d 下面 → target.index = 3（rest=[b,c,d]，越过 3 条中线）
  let to = resolveInsertIndex(allObjs, area, 'a', 3);
  eq(applyMove(ids(allObjs), 0, to), ['b','c','d','a'], '无过滤：a → 末尾');
  // 把 d 拖到最前 → target.index = 0
  to = resolveInsertIndex(allObjs, area, 'd', 0);
  eq(applyMove(ids(allObjs), 3, to), ['d','a','b','c'], '无过滤：d → 最前');
  // 把 a 拖到 b、c 之间 → rest=[b,c,d]，下标 1
  to = resolveInsertIndex(allObjs, area, 'a', 1);
  eq(applyMove(ids(allObjs), 0, to), ['b','a','c','d'], '无过滤：a → b 之后');
}

// ── 2. 过滤后拖拽（可见 a、c、d；b 隐藏） ─────────────────────────────────────
{
  const allObjs = ['a','b','c','d'].map(id => ({id}));
  const area = ['a','c','d'].map(id => ({id}));   // b 被过滤掉
  // 把 a 拖到最末（可见 c、d 之后）→ target.index = 2
  let to = resolveInsertIndex(allObjs, area, 'a', 2);
  eq(applyMove(ids(allObjs), 0, to), ['b','c','d','a'], '过滤：a → 可见末尾');
  // 把 d 拖到最前 → target.index = 0
  to = resolveInsertIndex(allObjs, area, 'd', 0);
  eq(applyMove(ids(allObjs), 3, to), ['d','a','b','c'], '过滤：d → 可见最前');
  // 把 a 移到 c 之后（可见顺序 a,c,d → c 是 rest[0]）→ target.index = 1
  to = resolveInsertIndex(allObjs, area, 'a', 1);
  eq(applyMove(ids(allObjs), 0, to), ['b','c','a','d'], '过滤：a → 可见 c 之后');
}

// ── 3. 看板：跨列（列内为空） ────────────────────────────────────────────────
{
  // tasks: t1/t2 属于待办状态，t3 属于完成状态
  const allObjs = [{id:'t1',status_id:'s-todo'},{id:'t2',status_id:'s-todo'},{id:'t3',status_id:'s-done'}];
  const done = allObjs.filter(t => t.status_id === 's-done');   // [t3]
  // 把 t1 拖到 done 列（列内 t3 之后）→ target.index = 1
  let to = resolveInsertIndex(allObjs, done, 't1', 1);
  eq(applyMove(ids(allObjs), 0, to), ['t2','t3','t1'], '看板：t1 → done 列末尾');

  // done 列为空时（t3 不存在）
  const all2 = [{id:'t1',status_id:'s-todo'},{id:'t2',status_id:'s-todo'}];
  to = resolveInsertIndex(all2, [], 't1', 0, 'append');
  eq(applyMove(ids(all2), 0, to), ['t2','t1'], '看板：跨列拖到空列 → 追加到末尾');
}

// ── 4. sort_order：中点插入只改一项 ─────────────────────────────────────────
{
  const list = [0,1,2,3].map(i => ({id:String(i), sort_order:i*ORDER_GAP}));
  const parsed = list.map(p => ({...p}));
  // 把 index0 移到 index1 之后
  const from = 0, to = 1;
  const moved = parsed.splice(from,1)[0];
  const target = Math.max(0, Math.min(to, parsed.length));
  parsed.splice(target, 0, moved);
  const prev = parsed[target-1], next = parsed[target+1];
  const between = Math.floor((prev.sort_order + next.sort_order)/2);
  eq(between, 1500, 'sort_order：中点 1000/2000 → 1500');
  eq(hasFullOrder(parsed), true, 'sort_order：全量都有值');
  eq(nextOrder(list), 4000, 'sort_order：追加值 = max + GAP');
}

// ── 5. 迁移与排序 ───────────────────────────────────────────────────────────
{
  const legacy = [{id:'a'},{id:'b',sort_order: 5000},{id:'c'}];
  const migrated = migrateProjects(legacy);
  eq(migrated.every(p => typeof p.sort_order === 'number'), true, '迁移：补齐缺失 sort_order');
  eq('updated_at' in migrated[0], false, '迁移：不写 updated_at（避免同步风暴）');
  const sorted = sortProjects([{id:'x',sort_order:3000},{id:'y',sort_order:1000},{id:'z',sort_order:2000}]);
  eq(ids(sorted), ['y','z','x'], '排序：按 sort_order 升序');
  // 缺值时保序
  eq(ids(sortProjects([{id:'p'},{id:'q',sort_order:0},{id:'r'}])), ['p','q','r'], '排序：缺值按 下标*GAP 兜底（同键时按下标稳定）');
  const rn = renumberProjects([{id:'a',sort_order:7},{id:'b',sort_order:9}]);
  eq(rn.list.map(p=>p.sort_order), [0, 1000], '重排：i * GAP');
  eq(rn.changed.length, 2, '重排：变化项被记录');
}

// ── 6. 回归：可见区只剩被拖元素时不应乱跑 ───────────────────────────────
{
  const allObjs = ['a','b','c','d'].map(id => ({id}));
  // 竖列表：筛选后只剩 a 自己 → 拖动必须 no-op（不能被扔到末尾）
  eq(resolveInsertIndex(allObjs, [{id:'a'}], 'a', 0), 0, '过滤只剩 1 项：resolveInsertIndex 返回原位（noop）');
  // 看板：拖进空列 → 追加到末尾
  eq(resolveInsertIndex(allObjs, [], 'b', 0, 'append'), 3, "空列 fallback='append' → 追加到末尾");
  // 看板：同列只有自己 → no-op
  eq(resolveInsertIndex(allObjs, [{id:'b'}], 'b', 0, 'noop'), 1, "同列只有自己 fallback='noop' → 原位");
}

// ── 7. 迁移：mixed 数据也要确定、无重复、保持“显式值优先”的次序 ──────────
{
  const mixed = [{id:'a'},{id:'b',sort_order:5000},{id:'c'}];
  const m = migrateProjects(mixed);
  eq(m.map(p=>p.id), ['a','c','b'], '迁移：缺值按当前下标兜底后排序（a、c 在前，显式 5000 的 b 在后）');
  eq(new Set(m.map(p=>p.sort_order)).size, 3, '迁移：无重复排序值');
  eq(m.map(p=>p.sort_order), [0, 1000, 2000], '迁移：重排为 i*GAP');
}

// ── 8. 间隙 == 2 时仍可插入（不该白白触发全量重排） ─────────────────────
{
  const prev = 1000, next = 1002;
  const mid = Math.floor((prev + next) / 2);
  eq(mid > prev && mid < next, true, '间隙 2：中点仍严格居中（canSplit 用 >= 2）');
}

console.log(fails === 0 ? '\n全部通过 ✅' : `\n${fails} 个失败 ❌`);
process.exit(fails === 0 ? 0 : 1);
