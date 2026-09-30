/**
 * 循环任务纯逻辑自测（无测试框架，直接断言）
 *
 * 覆盖 `utils/recurrence.ts` 的全部边界：
 *   - daily / weekly（含 byWeekday 多选）/ monthly（月末归位）/ yearly（闰年归位）
 *   - interval 归一化（≥1）
 *   - end='count' / end='until' 的耗尽判定与消费
 *   - describeRule 的 i18n 拼装（不硬编码文案）
 *
 * 运行：pnpm test:recurrence
 */
import {
  nextOccurrence,
  isRuleExhausted,
  consumeRule,
  describeRule,
  normalizeRule,
  normalizeInterval,
  normalizeWeekdays,
  normalizeDate,
} from '$lib/utils/recurrence';

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

const R = (freq, extra = {}) => ({ freq, interval: 1, end: 'never', ...extra });
/** 相对今天的日期（YYYY-MM-DD）：涉及 end='until' 的用例必须用相对日期，
 *  因为 isRuleExhausted 会与「真实今天」比较，硬编码日期会随时间失效。 */
const shift = (days) => new Date(Date.now() + days * 86400000).toISOString().slice(0, 10);

// ─── 1. daily ──────────────────────────────────────────────────────────────
console.log('\n[1] daily');
{
  eq(nextOccurrence(R('daily'), '2026-09-15'), '2026-09-16', '每天：+1 天');
  eq(nextOccurrence(R('daily', { interval: 3 }), '2026-09-15'), '2026-09-18', '每 3 天：+3 天');
  eq(nextOccurrence(R('daily', { interval: 0 }), '2026-09-15'), '2026-09-16', 'interval=0 归一化为 1（不会原地打转）');
  eq(nextOccurrence(R('daily'), '2026-12-31'), '2027-01-01', '跨年正确');
}

// ─── 2. weekly ─────────────────────────────────────────────────────────────
console.log('\n[2] weekly');
{
  // 2026-09-15 是周二
  eq(nextOccurrence(R('weekly'), '2026-09-15'), '2026-09-22', '每周：+7 天');
  eq(nextOccurrence(R('weekly', { interval: 2 }), '2026-09-15'), '2026-09-29', '每 2 周：+14 天');
  eq(nextOccurrence(R('weekly', { byWeekday: [1, 3] }), '2026-09-15'), '2026-09-16', '周一周三 / 从周二算 → 本周三');
  eq(nextOccurrence(R('weekly', { byWeekday: [1, 3] }), '2026-09-16'), '2026-09-21', '周一周三 / 从周三算 → 下周一');
  eq(nextOccurrence(R('weekly', { byWeekday: [2] }), '2026-09-15'), '2026-09-22', '同一星期几 → 下一周同一天');
  eq(nextOccurrence(R('weekly', { byWeekday: [1, 3], interval: 2 }), '2026-09-16'), '2026-09-28', '每 2 周 + 多选：跳到下下周一');
  eq(nextOccurrence(R('weekly', { byWeekday: [0] }), '2026-09-15'), '2026-09-20', '只在周日：本周日');
  eq(normalizeWeekdays([3, 1, 3, 9, -1, 1]), [1, 3], '星期集合去重、过滤越界、升序');
}

// ─── 3. monthly（月末归位）─────────────────────────────────────────────────
console.log('\n[3] monthly');
{
  eq(nextOccurrence(R('monthly'), '2026-09-15'), '2026-10-15', '每月：同日下月');
  eq(nextOccurrence(R('monthly'), '2026-01-31'), '2026-02-28', '★ 1/31 → 2/28（归位，不溢出到 3 月）');
  eq(nextOccurrence(R('monthly'), '2026-03-31'), '2026-04-30', '★ 3/31 → 4/30');
  eq(nextOccurrence(R('monthly'), '2026-05-31'), '2026-06-30', '★ 5/31 → 6/30');
  eq(nextOccurrence(R('monthly'), '2026-12-15'), '2027-01-15', '跨年');
  eq(nextOccurrence(R('monthly', { interval: 3 }), '2026-01-31'), '2026-04-30', '每 3 个月 + 月末归位');
  eq(nextOccurrence(R('monthly'), '2026-01-30'), '2026-02-28', '1/30 → 2/28');
}

// ─── 4. yearly（闰年）──────────────────────────────────────────────────────
console.log('\n[4] yearly');
{
  eq(nextOccurrence(R('yearly'), '2026-09-15'), '2027-09-15', '每年：同月同日');
  eq(nextOccurrence(R('yearly'), '2024-02-29'), '2025-02-28', '★ 2/29 → 平年 2/28');
  eq(nextOccurrence(R('yearly'), '2027-02-28'), '2028-02-28', '2/28 → 次年仍是 2/28（不自行跳到 2/29）');
  eq(nextOccurrence(R('yearly', { interval: 4 }), '2024-02-29'), '2028-02-29', '每 4 年：闰日得以保留');
}

// ─── 5. 结束条件 ───────────────────────────────────────────────────────────
console.log('\n[5] end: count / until');
{
  eq(isRuleExhausted(R('daily', { end: 'count', count: 2 })), false, 'count=2 → 未耗尽');
  eq(isRuleExhausted(R('daily', { end: 'count', count: 0 })), true, 'count=0 → 已耗尽');
  eq(nextOccurrence(R('daily', { end: 'count', count: 0 }), '2026-09-15'), null, 'count=0 → 不再生成');
  eq(nextOccurrence(R('daily', { end: 'count', count: 1 }), '2026-09-15'), '2026-09-16', 'count=1 → 还能生成一次');

  eq(consumeRule(R('daily', { end: 'count', count: 3 })).count, 2, 'consumeRule：count 3 → 2');
  eq(consumeRule(R('daily', { end: 'count', count: 0 })).count, 0, 'consumeRule：不会变成负数');
  const src = R('daily', { end: 'count', count: 3 });
  consumeRule(src);
  eq(src.count, 3, 'consumeRule 不修改原对象');

  eq(isRuleExhausted(R('daily', { end: 'until', until: shift(10) })), false, 'until 在未来 → 未耗尽');
  eq(isRuleExhausted(R('daily', { end: 'until', until: shift(-10) })), true, 'until 已过 → 耗尽');
  eq(nextOccurrence(R('daily', { end: 'until', until: shift(2) }), shift(0)), shift(1), 'until 内 → 正常生成');
  eq(nextOccurrence(R('daily', { end: 'until', until: shift(1) }), shift(0)), shift(1), '恰好等于 until → 仍生成');
  eq(nextOccurrence(R('daily', { end: 'until', until: shift(0) }), shift(0)), null, '下一实例超过 until → null');
  // 下个月的「同一天」最多在 31 天后，所以用 +40 天保证未超界
  eq(nextOccurrence(R('monthly', { end: 'until', until: shift(40) }), shift(0)) !== null, true, 'monthly + until 未超界可生成');
  eq(nextOccurrence(R('monthly', { end: 'until', until: shift(10) }), shift(0)), null, 'monthly + until 太近 → null');

  // 生成 → 消费 → 再生成的完整链路（模拟连续完成）
  let rule = R('weekly', { byWeekday: [1], end: 'count', count: 2 });
  let date = '2026-09-15'; // 周二
  const chain = [];
  for (let i = 0; i < 4; i++) {
    const next = nextOccurrence(rule, date);
    if (!next) break;
    chain.push(next);
    date = next;
    rule = consumeRule(rule);
  }
  eq(chain, ['2026-09-21', '2026-09-28'], 'count=2：只生成两次（周一序列）');
  eq(rule.count, 0, '消费到 0');
}

// ─── 6. 容错与工具 ─────────────────────────────────────────────────────────
console.log('\n[6] 容错');
{
  eq(nextOccurrence(null, '2026-09-15'), null, 'null 规则 → null');
  eq(nextOccurrence(undefined, '2026-09-15'), null, 'undefined 规则 → null');
  eq(normalizeInterval(0), 1, 'normalizeInterval(0) = 1');
  eq(normalizeInterval(-5), 1, 'normalizeInterval(-5) = 1');
  eq(normalizeInterval('2'), 2, 'normalizeInterval("2") = 2');
  eq(normalizeInterval(undefined), 1, 'normalizeInterval(undefined) = 1');
  eq(normalizeDate('2026-09-15T10:00:00.000Z'), '2026-09-15', 'ISO 串取日期部分');
  eq(nextOccurrence(R('daily'), '2026-09-15T23:00:00.000Z'), '2026-09-16', '基准带时间也能算');
  eq(normalizeRule({ freq: 'nope' }).freq, 'daily', '非法 freq 回退 daily');
  eq(normalizeRule({ freq: 'weekly', interval: 0, byWeekday: [] }).interval, 1, '归一化步长');
  eq('byWeekday' in normalizeRule({ freq: 'weekly', byWeekday: [] }), false, 'weekly 无有效星期则不输出 byWeekday 字段');
  eq('count' in normalizeRule({ freq: 'daily', end: 'never', count: 5 }), false, 'end=never 时不带 count');
}

// ─── 7. describeRule（i18n 拼装）───────────────────────────────────────────
console.log('\n[7] describeRule');
{
  // 用真实的 zh 文案当 t，验证拼装结果（模块本身不硬编码文案）
  const dict = {
    'recurrence.daily': '每天', 'recurrence.weekly': '每周', 'recurrence.monthly': '每月', 'recurrence.yearly': '每年',
    'recurrence.summaryDaily': '每 {n} 天', 'recurrence.summaryEveryNWeeks': '每 {n} 周',
    'recurrence.summaryEveryNMonths': '每 {n} 个月', 'recurrence.summaryEveryNYears': '每 {n} 年',
    'recurrence.summaryWithWeekdays': '{days}', 'recurrence.summaryUntil': '截止 {date}', 'recurrence.summaryCount': '共 {n} 次',
    'recurrence.weekdayJoin': '、', 'recurrence.partJoin': ' · ',
    'recurrence.wd0': '周日', 'recurrence.wd1': '周一', 'recurrence.wd2': '周二', 'recurrence.wd3': '周三',
    'recurrence.wd4': '周四', 'recurrence.wd5': '周五', 'recurrence.wd6': '周六',
  };
  const t = (k) => dict[k] ?? k;

  eq(describeRule(R('daily'), t), '每天', '每天');
  eq(describeRule(R('daily', { interval: 2 }), t), '每 2 天', '每 2 天');
  eq(describeRule(R('weekly', { byWeekday: [1, 3] }), t), '每周 · 周一、周三', '每周 + 多选星期');
  eq(describeRule(R('weekly', { interval: 2, byWeekday: [1] }), t), '每 2 周 · 周一', '每 2 周 + 星期');
  eq(describeRule(R('monthly', { end: 'count', count: 3 }), t), '每月 · 共 3 次', '每月 + 次数');
  eq(describeRule(R('yearly', { end: 'until', until: '2027-01-01' }), t), '每年 · 截止 2027-01-01', '每年 + 截止');
  eq(describeRule(null, t), '', 'null → 空串');
}

console.log(fails === 0 ? '\n全部通过 ✅' : `\n${fails} 个失败 ❌`);
process.exit(fails === 0 ? 0 : 1);
