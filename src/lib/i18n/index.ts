/**
 * 国际化 (i18n) 核心模块
 *
 * 基于 Svelte store 的轻量 i18n 实现，支持中英文切换。
 * 语言偏好自动持久化到 localStorage。
 *
 * @module i18n
 *
 * @example
 * ```svelte
 * <script lang="ts">
 *   import { t, tf } from '$lib/i18n';
 * </script>
 * <button>{$t('sidebar.newProject')}</button>
 * <span>{$tf('calendar.more', { n: 3 })}</span>
 * ```
 */

import { writable, get } from 'svelte/store';
import type { Readable } from 'svelte/store';
import { zh } from './zh';
import { en } from './en';

export type Locale = 'zh' | 'en';

type TranslationFn = (key: string) => string;
type InterpolationFn = (key: string, params: Record<string, string | number>) => string;

const locales: Record<Locale, Record<string, string>> = { zh, en };

/** 当前语言 store，切换后所有使用 $t 的组件自动更新 */
export const locale = writable<Locale>(loadLocale());

function loadLocale(): Locale {
  try {
    const v = localStorage.getItem('pm_locale');
    if (v === 'en') return 'en';
  } catch {}
  return 'zh';
}

locale.subscribe(v => {
  try { localStorage.setItem('pm_locale', v); } catch {}
});

function getTranslationFn(): TranslationFn {
  const current = get(locale);
  return (key: string) => locales[current][key] ?? key;
}

/**
 * 翻译函数 store
 *
 * 在模板中使用 $t('key') 获取响应式翻译，语言切换后自动更新。
 * 在脚本回调中使用 get(t)('key') 获取当前翻译。
 */
export const t: Readable<TranslationFn> & { get: () => TranslationFn } = (() => {
  const { subscribe } = writable<TranslationFn>(getTranslationFn(), (set) => {
    return locale.subscribe(() => set(getTranslationFn()));
  });
  return { subscribe, get: () => getTranslationFn() };
})();

function getInterpolationFn(): InterpolationFn {
  const translate = get(t);
  return (key: string, params: Record<string, string | number>) => {
    let s = translate(key);
    for (const [k, v] of Object.entries(params)) {
      s = s.replaceAll(`{${k}}`, String(v));
    }
    return s;
  };
}

/**
 * 带插值的翻译函数 store
 *
 * 模板中使用 $tf('key', { n: 3 })，脚本中使用 get(tf)('key', { n: 3 })
 */
export const tf: Readable<InterpolationFn> & { get: () => InterpolationFn } = (() => {
  const { subscribe } = writable<InterpolationFn>(getInterpolationFn(), (set) => {
    return locale.subscribe(() => set(getInterpolationFn()));
  });
  return { subscribe, get: () => getInterpolationFn() };
})();
