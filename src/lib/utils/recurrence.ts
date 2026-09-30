/**
 * 循环任务：纯逻辑
 *
 * 全部是纯函数（只依赖 `Date` 与传入参数），因此可被 `scripts/test-recurrence.mjs`
 * 直接 import 单测，不需要浏览器环境。
 *
 * 约定：
 * - 日期一律用 `YYYY-MM-DD` 字符串表示（只取前 10 位，容忍传入完整 ISO 串）
 * - **不做时区魔法**：按 UTC 构造，避免跨时区把日期算偏一天
 * - `monthly` / `yearly` 遇到不存在的日期**归位**而不是溢出（1/31 → 2/28，平年 2/29 → 2/28）
 *
 * @module utils/recurrence
 */

import type { RecurrenceFreq, RecurrenceRule } from '$lib/types';

/** 合法频率 */
const FREQS: RecurrenceFreq[] = ['daily', 'weekly', 'monthly', 'yearly'];

/** 把任意日期串规整成 YYYY-MM-DD（非法则回退今天） */
export function normalizeDate(input: string | null | undefined): string {
  const s = String(input ?? '').trim().slice(0, 10);
  if (/^\d{4}-\d{2}-\d{2}$/.test(s)) return s;
  return new Date().toISOString().slice(0, 10);
}

/** 解析成 UTC Date（避免本地时区把日期算偏） */
function parse(date: string): Date {
  const [y, m, d] = normalizeDate(date).split('-').map(Number);
  return new Date(Date.UTC(y, m - 1, d));
}

/** Date → YYYY-MM-DD */
function fmt(d: Date): string {
  return d.toISOString().slice(0, 10);
}

/** 加天数 */
function addDays(date: string, days: number): string {
  const d = parse(date);
  d.setUTCDate(d.getUTCDate() + days);
  return fmt(d);
}

/** 某年某月的天数 */
function daysInMonth(year: number, monthIndex0: number): number {
  return new Date(Date.UTC(year, monthIndex0 + 1, 0)).getUTCDate();
}

/** 构造「YYYY-MM-DD」，日超出当月天数时归位到当月最后一天 */
function makeClampedDate(year: number, monthIndex0: number, day: number): string {
  const max = daysInMonth(year, monthIndex0);
  return fmt(new Date(Date.UTC(year, monthIndex0, Math.min(Math.max(1, day), max))));
}

/** 取有效步长（≥1，缺省 1） */
export function normalizeInterval(interval: unknown): number {
  const n = typeof interval === 'number' ? interval : parseInt(String(interval ?? ''), 10);
  return Number.isFinite(n) && n >= 1 ? Math.floor(n) : 1;
}

/** 取有效的星期集合（去重、0..6、升序）；无有效值返回空数组 */
export function normalizeWeekdays(days: unknown): number[] {
  if (!Array.isArray(days)) return [];
  const set = new Set<number>();
  for (const d of days) {
    const n = typeof d === 'number' ? d : parseInt(String(d), 10);
    if (Number.isFinite(n) && n >= 0 && n <= 6) set.add(Math.floor(n));
  }
  return [...set].sort((a, b) => a - b);
}

/** 补全规则（容错：非法频率回退 daily，步长归位，非法结束条件回退 never） */
export function normalizeRule(rule: Partial<RecurrenceRule> | null | undefined): RecurrenceRule {
  const freq = FREQS.includes(rule?.freq as RecurrenceFreq) ? (rule!.freq as RecurrenceFreq) : 'daily';
  const end = rule?.end === 'count' || rule?.end === 'until' ? rule.end : 'never';
  const out: RecurrenceRule = { freq, interval: normalizeInterval(rule?.interval), end };
  if (freq === 'weekly') {
    const wd = normalizeWeekdays(rule?.byWeekday);
    if (wd.length) out.byWeekday = wd;
  }
  if (end === 'count') out.count = Math.max(0, Math.floor(Number(rule?.count ?? 0)) || 0);
  if (end === 'until' && rule?.until) out.until = normalizeDate(rule.until);
  if (rule?.sourceTaskId) out.sourceTaskId = rule.sourceTaskId;
  return out;
}

/**
 * 判断规则是否**已经**用尽（不需要再算日期）
 *
 * - `end='count'`：剩余次数 ≤ 0
 * - `end='until'`：截止日期已早于今天
 *
 * 注意：`until` 的最终判定在 {@link nextOccurrence}（算出的日期超过 until 也会返回 null）。
 */
export function isRuleExhausted(rule: RecurrenceRule | null | undefined): boolean {
  if (!rule) return true;
  const r = normalizeRule(rule);
  if (r.end === 'count') return (r.count ?? 0) <= 0;
  if (r.end === 'until') return !!r.until && normalizeDate(r.until) < new Date().toISOString().slice(0, 10);
  return false;
}

/**
 * 从基准日期算出下一次触发日期
 *
 * @param rule - 循环规则
 * @param baseDate - 基准日期（通常是当前实例的 due_date；无则传今天）
 * @returns `YYYY-MM-DD`；规则已用尽 / 超过 until / 规则为空 → `null`
 */
export function nextOccurrence(rule: RecurrenceRule | null | undefined, baseDate: string): string | null {
  if (!rule) return null;
  const r = normalizeRule(rule);
  if (isRuleExhausted(r)) return null;

  const base = normalizeDate(baseDate);
  const d = parse(base);
  let next: string;

  switch (r.freq) {
    case 'daily':
      next = addDays(base, r.interval);
      break;

    case 'weekly': {
      const weekdays = normalizeWeekdays(r.byWeekday);
      if (weekdays.length === 0) {
        next = addDays(base, 7 * r.interval);
        break;
      }
      const dow = d.getUTCDay(); // 0=周日
      const sameWeek = weekdays.find((w) => w > dow);
      if (sameWeek !== undefined) {
        next = addDays(base, sameWeek - dow);
      } else {
        // 跳到「下一个周期的那一周」，取该周第一个选中的星期几
        const weekStart = addDays(base, -dow);           // 本周周日
        next = addDays(weekStart, 7 * r.interval + weekdays[0]);
      }
      break;
    }

    case 'monthly': {
      const totalMonth = d.getUTCMonth() + r.interval;
      const year = d.getUTCFullYear() + Math.floor(totalMonth / 12);
      const monthIndex0 = ((totalMonth % 12) + 12) % 12;
      next = makeClampedDate(year, monthIndex0, d.getUTCDate());
      break;
    }

    case 'yearly': {
      const year = d.getUTCFullYear() + r.interval;
      next = makeClampedDate(year, d.getUTCMonth(), d.getUTCDate());
      break;
    }

    default:
      return null;
  }

  // end='until'：算出来的日期超过截止日 → 不再生成
  if (r.end === 'until' && r.until && next > normalizeDate(r.until)) return null;

  return next;
}

/**
 * 完成一次后消费规则（不改原对象）
 *
 * - `end='count'`：`count - 1`（不低于 0）
 * - 其它：原样返回副本
 * - 会顺带把 `sourceTaskId` 清掉（新实例的归属由调用方重新写）
 */
export function consumeRule(rule: RecurrenceRule): RecurrenceRule {
  const r = normalizeRule(rule);
  const out: RecurrenceRule = { ...r };
  delete out.sourceTaskId;
  if (r.end === 'count') out.count = Math.max(0, (r.count ?? 0) - 1);
  return out;
}

/** 星期名 i18n key 后缀（0=周日…6=周六） */
const WD_KEYS = ['wd0', 'wd1', 'wd2', 'wd3', 'wd4', 'wd5', 'wd6'];

/**
 * 人类可读描述（用于列表 / 详情 / title）
 *
 * **不硬编码文案**：只做 i18n key 的拼装，翻译由调用方传入的 `t` 决定。
 *
 * @example
 * describeRule({ freq: 'weekly', interval: 2, byWeekday: [1, 3], end: 'count', count: 3 }, t)
 * // → "每 2 周 · 周一、周三 · 共 3 次"
 */
export function describeRule(rule: RecurrenceRule | null | undefined, t: (key: string) => string): string {
  if (!rule) return '';
  const r = normalizeRule(rule);
  const parts: string[] = [];

  const everyN = (key: string) => t(key).replace('{n}', String(r.interval));
  switch (r.freq) {
    case 'daily':
      parts.push(r.interval === 1 ? t('recurrence.daily') : everyN('recurrence.summaryDaily'));
      break;
    case 'weekly':
      parts.push(r.interval === 1 ? t('recurrence.weekly') : everyN('recurrence.summaryEveryNWeeks'));
      break;
    case 'monthly':
      parts.push(r.interval === 1 ? t('recurrence.monthly') : everyN('recurrence.summaryEveryNMonths'));
      break;
    case 'yearly':
      parts.push(r.interval === 1 ? t('recurrence.yearly') : everyN('recurrence.summaryEveryNYears'));
      break;
  }

  const weekdays = normalizeWeekdays(r.byWeekday);
  if (r.freq === 'weekly' && weekdays.length > 0) {
    const names = weekdays.map((w) => t(`recurrence.${WD_KEYS[w]}`)).join(t('recurrence.weekdayJoin'));
    parts.push(t('recurrence.summaryWithWeekdays').replace('{days}', names));
  }

  if (r.end === 'count') {
    parts.push(t('recurrence.summaryCount').replace('{n}', String(r.count ?? 0)));
  } else if (r.end === 'until' && r.until) {
    parts.push(t('recurrence.summaryUntil').replace('{date}', r.until));
  }

  return parts.join(t('recurrence.partJoin'));
}
