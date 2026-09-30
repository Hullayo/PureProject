<script lang="ts">
  /**
   * TaskDetailRecurrence — 任务详情里的「循环」设置
   *
   * 交互约定：
   * - 频率 / 步长 / 星期 / 结束条件都先用本地 `$state` 编辑，
   *   **在切换开关、切频率、失焦（blur）时才提交一次** `setRecurrence()`
   *   —— 避免每敲一个字符都 `pushHistory()` 污染撤销栈。
   * - 已设置时顶部显示 `describeRule()` 摘要 + 「清除」。
   */
  import { activeProject, activeTask, setRecurrence } from '$lib/stores';
  import type { RecurrenceFreq, RecurrenceRule } from '$lib/types';
  import { describeRule, normalizeRule } from '$lib/utils/recurrence';
  import { t } from '$lib/i18n';
  import Icon from '$lib/components/shared/Icon.svelte';

  const FREQS: { id: RecurrenceFreq; labelKey: string }[] = [
    { id: 'daily', labelKey: 'recurrence.daily' },
    { id: 'weekly', labelKey: 'recurrence.weekly' },
    { id: 'monthly', labelKey: 'recurrence.monthly' },
    { id: 'yearly', labelKey: 'recurrence.yearly' },
  ];
  const WD_KEYS = ['wd0', 'wd1', 'wd2', 'wd3', 'wd4', 'wd5', 'wd6'];

  /** 当前任务是否启用循环（rerender 时同步本地草稿） */
  const rule = $derived($activeTask?.recurrence ?? null);

  let interval = $state(1);
  let byWeekday = $state<number[]>([]);
  let count = $state(1);
  let until = $state('');

  // 任务或其规则变化时，把草稿同步过来（仅在值确实不同时写，避免覆盖用户正在输入的内容）
  $effect(() => {
    const r = rule;
    const next = r ? normalizeRule(r) : null;
    interval = next?.interval ?? 1;
    byWeekday = next?.byWeekday ?? [];
    count = next?.end === 'count' ? (next.count ?? 1) : 1;
    until = next?.end === 'until' ? (next.until ?? '') : '';
  });

  function commit(patch: Partial<RecurrenceRule>): void {
    if (!$activeProject || !$activeTask || !rule) return;
    setRecurrence($activeProject.id, $activeTask.id, { ...normalizeRule(rule), ...patch });
  }

  function toggleRecurring(): void {
    if (!$activeProject || !$activeTask) return;
    if (rule) {
      setRecurrence($activeProject.id, $activeTask.id, null);
    } else {
      setRecurrence($activeProject.id, $activeTask.id, normalizeRule({ freq: 'weekly', interval: 1, end: 'never' }));
    }
  }

  function setFreq(freq: RecurrenceFreq): void {
    const base: RecurrenceRule = { freq, interval, end: rule?.end ?? 'never' };
    if (freq === 'weekly') base.byWeekday = byWeekday.length ? byWeekday : [new Date().getDay()];
    if (base.end === 'count') base.count = Math.max(1, count);
    if (base.end === 'until' && until) base.until = until;
    commit(base);
  }

  function toggleWeekday(day: number): void {
    const next = byWeekday.includes(day) ? byWeekday.filter((d) => d !== day) : [...byWeekday, day].sort((a, b) => a - b);
    byWeekday = next;
    if (next.length === 0) return; // 至少保留一天，不提交空集合
    commit({ byWeekday: next });
  }

  function setEnd(end: 'never' | 'count' | 'until'): void {
    const patch: Partial<RecurrenceRule> = { end };
    if (end === 'count') patch.count = Math.max(1, count);
    if (end === 'until') patch.until = until || new Date().toISOString().slice(0, 10);
    commit(patch);
  }

  const summary = $derived(rule ? describeRule(rule, $t) : '');
</script>

{#if $activeTask}
  <div class="rec-head">
    <span class="label">{$t('recurrence.title')}</span>
    <button class="rec-toggle" class:on={!!rule} onclick={toggleRecurring} aria-pressed={!!rule}>
      {rule ? $t('recurrence.clear') : $t('recurrence.none')}
    </button>
  </div>

  {#if rule}
    <div class="rec-summary"><Icon name="repeat" size={12} /> {summary}</div>

    <div class="rec-row">
      <span class="rec-label">{$t('recurrence.interval')}</span>
      <input
        class="rec-input"
        type="number"
        min="1"
        bind:value={interval}
        onblur={() => commit({ interval: Math.max(1, Math.floor(Number(interval) || 1)) })}
        onkeydown={(e) => { if (e.key === 'Enter') commit({ interval: Math.max(1, Math.floor(Number(interval) || 1)) }); }}
      />
    </div>

    <div class="rec-opts">
      {#each FREQS as f (f.id)}
        <button class="rec-opt" class:active={rule.freq === f.id} onclick={() => setFreq(f.id)}>{$t(f.labelKey)}</button>
      {/each}
    </div>

    {#if rule.freq === 'weekly'}
      <span class="rec-label">{$t('recurrence.weekdays')}</span>
      <div class="rec-opts">
        {#each WD_KEYS as key, day (key)}
          <button class="rec-opt wd" class:active={byWeekday.includes(day)} onclick={() => toggleWeekday(day)}>{$t(`recurrence.${key}`)}</button>
        {/each}
      </div>
    {/if}

    <span class="rec-label">{$t('recurrence.end')}</span>
    <div class="rec-opts">
      <button class="rec-opt" class:active={rule.end === 'never'} onclick={() => setEnd('never')}>{$t('recurrence.endNever')}</button>
      <button class="rec-opt" class:active={rule.end === 'count'} onclick={() => setEnd('count')}>{$t('recurrence.endCount')}</button>
      <button class="rec-opt" class:active={rule.end === 'until'} onclick={() => setEnd('until')}>{$t('recurrence.endUntil')}</button>
    </div>

    {#if rule.end === 'count'}
      <div class="rec-row">
        <span class="rec-label">{$t('recurrence.count')}</span>
        <input
          class="rec-input"
          type="number"
          min="1"
          bind:value={count}
          onblur={() => commit({ count: Math.max(1, Math.floor(Number(count) || 1)) })}
          onkeydown={(e) => { if (e.key === 'Enter') commit({ count: Math.max(1, Math.floor(Number(count) || 1)) }); }}
        />
      </div>
    {/if}

    {#if rule.end === 'until'}
      <div class="rec-row">
        <span class="rec-label">{$t('recurrence.until')}</span>
        <input class="rec-input wide" type="date" bind:value={until} onchange={() => commit({ until })} />
      </div>
    {/if}
  {/if}
{/if}

<style>
  .rec-head { display: flex; align-items: center; justify-content: space-between; gap: 8px; }
  .rec-toggle { padding: 4px 10px; font-size: 11px; border-radius: 6px; background: var(--surface); color: var(--text-secondary); border: 1px solid var(--border); }
  .rec-toggle.on { color: var(--accent); border-color: var(--accent); background: var(--accent-light); }
  .rec-summary { display: flex; align-items: center; gap: 6px; font-size: 12px; color: var(--text-secondary); }
  .rec-row { display: flex; align-items: center; gap: 8px; }
  .rec-label { font-size: 11px; color: var(--text-muted); }
  .rec-input { width: 70px; padding: 5px 8px; font-size: 12px; border-radius: 6px; background: var(--surface); border: 1px solid var(--border); color: var(--text); }
  .rec-input.wide { width: auto; flex: 1; }
  .rec-input:focus { border-color: var(--accent); outline: none; }
  .rec-opts { display: flex; flex-wrap: wrap; gap: 4px; }
  .rec-opt { padding: 4px 10px; font-size: 11px; border-radius: 6px; background: var(--surface); color: var(--text-secondary); border: 1px solid var(--border); }
  .rec-opt.active { background: var(--accent); color: #fff; border-color: var(--accent); }
  .rec-opt.wd { min-width: 34px; text-align: center; }
</style>
