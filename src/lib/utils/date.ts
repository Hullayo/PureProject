/**
 * 日期与时间工具函数
 *
 * 提供日期格式化、时间差计算等常用工具函数，
 * 用于任务显示、时间追踪和导出功能。
 *
 * @module utils/date
 */

/** 获取当前时间的 ISO 字符串 */
export function now(): string { return new Date().toISOString(); }

/** 获取今天日期的 ISO 字符串（仅日期部分，如 "2026-05-28"） */
export function today(): string { return new Date().toISOString().split('T')[0]; }

/**
 * 格式化日期为 YYYY-MM-DD 格式
 * @param d - ISO 日期字符串，null 时返回 '-'
 * @returns 格式化后的日期字符串
 */
export function fmtDate(d: string | null): string {
  if (!d) return '-';
  const dt = new Date(d);
  return `${dt.getFullYear()}-${String(dt.getMonth() + 1).padStart(2, '0')}-${String(dt.getDate()).padStart(2, '0')}`;
}

/**
 * 计算并格式化两个时间点之间的持续时间（简短格式）
 *
 * @param startIso - 开始时间的 ISO 字符串
 * @param endIso - 结束时间的 ISO 字符串，省略时使用当前时间
 * @returns 格式化的持续时间，如 "2h 30m" 或 "45m"
 */
export function formatDuration(startIso: string, endIso?: string): string {
  const ms = (endIso ? new Date(endIso) : new Date()).getTime() - new Date(startIso).getTime();
  return formatMs(ms);
}

/**
 * 将毫秒数格式化为人类可读的持续时间
 *
 * @param ms - 毫秒数
 * @returns 格式化的持续时间，如 "3h 15m" 或 "20m"
 */
export function formatMs(ms: number): string {
  if (ms < 0) return '0m';
  const totalMin = Math.floor(ms / 60000);
  const h = Math.floor(totalMin / 60);
  const m = totalMin % 60;
  if (h > 0) return `${h}h ${m}m`;
  return `${m}m`;
}

/**
 * 计算并格式化两个时间点之间的持续时间（精确格式）
 *
 * @param startIso - 开始时间的 ISO 字符串
 * @param endIso - 结束时间的 ISO 字符串，省略时使用 `nowMs`（再省略则用当前时间）
 * @param nowMs - 可选的「当前时间」毫秒时间戳；传入 `$ticker` 即可实现秒级实时刷新
 * @returns 格式化的持续时间，如 "2:30:45"（H:MM:SS）
 */
export function formatDurationHMS(startIso: string, endIso?: string, nowMs?: number): string {
  const end = endIso ? new Date(endIso).getTime() : (typeof nowMs === 'number' ? nowMs : Date.now());
  const ms = end - new Date(startIso).getTime();
  if (ms < 0) return '0:00:00';
  const totalSec = Math.floor(ms / 1000);
  const h = Math.floor(totalSec / 3600);
  const m = Math.floor((totalSec % 3600) / 60);
  const s = totalSec % 60;
  return `${h}:${String(m).padStart(2, '0')}:${String(s).padStart(2, '0')}`;
}
