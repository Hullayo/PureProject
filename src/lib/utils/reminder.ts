/**
 * 任务提醒工具
 *
 * 提供提醒时间检查和通知发送功能。
 * 支持 Tauri 原生通知和 Web Notification API 两种方式。
 *
 * @module utils/reminder
 */

import type { Project, Task, TaskGroup, TaskStatusDefinition } from '$lib/types';
import { isTaskClosed } from './task-status';

const REMINDERS_ENABLED_KEY = 'pm_reminders_enabled';
const REMINDER_INTERVAL_KEY = 'pm_reminder_interval';
const DUE_REMINDER_ADVANCE_KEY = 'pm_due_reminder_advance_minutes';
const REMINDER_NOTIFIED_KEY = 'pm_reminder_notified';

export interface ReminderSettings {
  enabled: boolean;
  intervalSeconds: number;
  intervalMs: number;
  dueReminderAdvanceMinutes: number;
  dueReminderAdvanceMs: number;
}

type ReminderStorage = Pick<Storage, 'getItem' | 'setItem'>;

function getStorage(storage?: ReminderStorage): ReminderStorage | null {
  if (storage) return storage;
  if (typeof localStorage === 'undefined') return null;
  return localStorage;
}

function readStorage(storage: ReminderStorage | null, key: string, fallback: string): string {
  try {
    return storage?.getItem(key) ?? fallback;
  } catch {
    return fallback;
  }
}

function writeStorage(storage: ReminderStorage | null, key: string, value: string): void {
  try {
    storage?.setItem(key, value);
  } catch {
    // Ignore storage failures so reminders can still work for this session.
  }
}

function clampIntervalSeconds(value: number): number {
  if (!Number.isFinite(value)) return 30;
  return Math.min(300, Math.max(10, Math.round(value)));
}

function clampAdvanceMinutes(value: number): number {
  if (!Number.isFinite(value)) return 1440;
  return Math.min(43200, Math.max(0, Math.round(value)));
}

export function getReminderSettings(storage?: ReminderStorage): ReminderSettings {
  const target = getStorage(storage);
  const enabled = readStorage(target, REMINDERS_ENABLED_KEY, 'true') !== 'false';
  const intervalSeconds = clampIntervalSeconds(parseInt(readStorage(target, REMINDER_INTERVAL_KEY, '30'), 10));
  const dueReminderAdvanceMinutes = clampAdvanceMinutes(parseInt(readStorage(target, DUE_REMINDER_ADVANCE_KEY, '1440'), 10));
  return {
    enabled,
    intervalSeconds,
    intervalMs: intervalSeconds * 1000,
    dueReminderAdvanceMinutes,
    dueReminderAdvanceMs: dueReminderAdvanceMinutes * 60 * 1000
  };
}

export function getTaskDeadline(task: Task): Date | null {
  if (!task.due_date) return null;
  const [year, month, day] = task.due_date.split('-').map(Number);
  if (!year || !month || !day) return null;

  const time = task.due_time || '23:59';
  const [hourRaw, minuteRaw] = time.split(':').map(Number);
  const hour = Number.isFinite(hourRaw) ? hourRaw : 23;
  const minute = Number.isFinite(minuteRaw) ? minuteRaw : 59;

  return new Date(year, month - 1, day, hour, minute, 0, 0);
}

export function getTaskReminderTriggerTime(task: Task, settings: ReminderSettings): Date | null {
  const deadline = getTaskDeadline(task);
  if (!deadline) return null;
  return new Date(deadline.getTime() - settings.dueReminderAdvanceMs);
}

export function getReminderNotificationKey(task: Task, settings?: ReminderSettings): string {
  if (task.reminder) return `${task.id}:manual:${task.reminder}`;
  const activeSettings = settings ?? getReminderSettings();
  const deadline = getTaskDeadline(task);
  if (!deadline) return `${task.id}:none`;
  return `${task.id}:deadline:${deadline.toISOString()}:advance:${activeSettings.dueReminderAdvanceMinutes}`;
}

export function getStoredReminderNotifications(storage?: ReminderStorage): Set<string> {
  const target = getStorage(storage);
  const raw = readStorage(target, REMINDER_NOTIFIED_KEY, '[]');
  try {
    const parsed = JSON.parse(raw);
    return new Set(Array.isArray(parsed) ? parsed.filter((item): item is string => typeof item === 'string') : []);
  } catch {
    return new Set();
  }
}

export function markReminderNotified(storage: ReminderStorage | undefined, task: Task, settings?: ReminderSettings): Set<string> {
  const target = getStorage(storage);
  const notified = getStoredReminderNotifications(target ?? undefined);
  notified.add(getReminderNotificationKey(task, settings));
  writeStorage(target, REMINDER_NOTIFIED_KEY, JSON.stringify([...notified]));
  return notified;
}

/**
 * 检查是否有任务的提醒时间已到
 *
 * 扫描任务列表，筛选出满足以下条件的任务：
 * - 设置了提醒时间（reminder 不为 null）
 * - 任务状态不是已完成
 * - 提醒时间已过（<= 当前时间）
 * - 本次会话中尚未检查过该任务
 *
 * @param tasks - 任务列表
 * @param alreadyChecked - 本次会话中已检查过的任务 ID 集合，用于去重
 * @param currentTime - 当前时间，用于测试或自定义检查时间
 * @returns 需要触发提醒的任务列表
 */
export function checkReminders(
  tasks: Task[],
  alreadyChecked: Set<string>,
  currentTime = new Date(),
  settings = getReminderSettings()
): Task[] {
  return tasks.filter(t => {
    if (t.completed_at) return false;
    if (alreadyChecked.has(getReminderNotificationKey(t, settings))) return false;

    if (t.reminder) return new Date(t.reminder) <= currentTime;

    const triggerTime = getTaskReminderTriggerTime(t, settings);
    if (!triggerTime) return false;
    return triggerTime <= currentTime;
  });
}

export interface ReminderCandidate {
  project: Project;
  taskGroup: TaskGroup;
  status: TaskStatusDefinition;
  task: Task;
}

export function checkReminderCandidates(
  candidates: ReminderCandidate[],
  alreadyChecked: Set<string>,
  currentTime = new Date(),
  settings = getReminderSettings(),
): ReminderCandidate[] {
  return candidates.filter(({ project, task }) => {
    if (isTaskClosed(project, task)) return false;
    if (alreadyChecked.has(getReminderNotificationKey(task, settings))) return false;
    if (task.reminder) return new Date(task.reminder) <= currentTime;
    const triggerTime = getTaskReminderTriggerTime(task, settings);
    return !!triggerTime && triggerTime <= currentTime;
  });
}

/**
 * 发送桌面通知
 *
 * 优先使用 Tauri 原生通知插件（在桌面端更可靠），
 * 若不在 Tauri 环境中则回退到 Web Notification API。
 *
 * @param title - 通知标题
 * @param body - 通知正文内容
 */
export async function sendNotification(title: string, body: string): Promise<void> {
  try {
    const { isPermissionGranted, requestPermission, sendNotification: tauriNotify } = await import('@tauri-apps/plugin-notification');
    const granted = await isPermissionGranted();
    if (!granted) {
      const result = await requestPermission();
      if (result !== 'granted') return;
    }
    tauriNotify({ title, body });
  } catch {
    // Fallback to Web Notification API
    if (typeof Notification !== 'undefined') {
      if (Notification.permission === 'granted') {
        new Notification(title, { body });
      } else if (Notification.permission !== 'denied') {
        const perm = await Notification.requestPermission();
        if (perm === 'granted') new Notification(title, { body });
      }
    }
  }
}
