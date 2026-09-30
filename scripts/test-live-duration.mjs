/**
 * 实时时长（ticker）纯逻辑自测
 *
 * 覆盖：
 *   1. `createTicker()` 在 `active()` 为 false 时**不触发**（避免无谓重渲染）
 *   2. 激活后按 1s 触发；取消订阅后停止定时器
 *   3. `formatDurationHMS(start, end, nowMs)` 的输出严格由 `nowMs` 决定
 *
 * 运行：pnpm test:live-duration
 */
import { createTicker } from '$lib/utils/live-duration';
import { formatDurationHMS } from '$lib/utils/date';

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

// ─── 1. ticker 的触发语义（用可控的假定时器） ───────────────────────────────
console.log('\n[1] createTicker 触发语义');
{
  const origSI = globalThis.setInterval;
  const origCI = globalThis.clearInterval;
  const origNow = Date.now;
  let captured = null;
  globalThis.setInterval = (fn) => { captured = fn; return 123; };
  globalThis.clearInterval = () => { captured = null; };
  // svelte 的 readable 对「值未变化」会去重，同一毫秒的 Date.now() 相同 → 需要假时钟
  let fakeNow = 1_000_000;
  Date.now = () => (fakeNow += 1000);

  try {
    let active = false;
    const ticker = createTicker(() => active);
    const seen = [];
    const unsub = ticker.subscribe((v) => seen.push(v));

    ok(seen.length === 1, '订阅时收到初始值');
    ok(typeof captured === 'function', '订阅后已启动定时器');

    // 未激活：连续 5 次 tick 都不应产生新值
    for (let i = 0; i < 5; i++) captured?.();
    eq(seen.length, 1, '★ active()=false 时不触发');

    // 激活后：每次 tick 产生一个值
    active = true;
    captured?.();
    captured?.();
    eq(seen.length, 3, '★ active()=true 后触发');

    // 取消订阅 → 停止定时器，再 tick 不再产生值
    unsub();
    ok(captured === null, '取消订阅后清除定时器');
    eq(seen.length, 3, '取消订阅后不再触发');
  } finally {
    globalThis.setInterval = origSI;
    globalThis.clearInterval = origCI;
    Date.now = origNow;
  }
}

// ─── 2. formatDurationHMS 的 nowMs 参数 ─────────────────────────────────────
console.log('\n[2] formatDurationHMS(start, end, nowMs)');
{
  const start = '2026-01-01T00:00:00.000Z';
  eq(formatDurationHMS(start, '2026-01-01T01:02:03.000Z'), '1:02:03', '显式 end 生效');
  eq(formatDurationHMS(start, undefined, Date.parse('2026-01-01T01:02:03.000Z')), '1:02:03', '★ nowMs 控制输出');
  eq(formatDurationHMS(start, undefined, Date.parse('2026-01-01T00:00:00.000Z')), '0:00:00', 'nowMs = start → 0');
  eq(formatDurationHMS(start, undefined, Date.parse('2025-12-31T23:59:59.000Z')), '0:00:00', 'nowMs 早于 start → 归零（不为负）');
  eq(formatDurationHMS(start, '2026-01-01T00:00:59.900Z'), '0:00:59', '不足 1s 向下取整');

  // 两个不同的 nowMs 必须得到不同输出（这正是「工时一直不变」的回归点）
  const a = formatDurationHMS(start, undefined, Date.parse('2026-01-01T00:00:05.000Z'));
  const b = formatDurationHMS(start, undefined, Date.parse('2026-01-01T00:00:06.000Z'));
  ok(a !== b, '★ 不同 nowMs → 不同输出（秒级刷新成立）', `${a} vs ${b}`);
}

console.log(fails === 0 ? '\n全部通过 ✅' : `\n${fails} 个失败 ❌`);
process.exit(fails === 0 ? 0 : 1);
