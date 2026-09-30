<script lang="ts">
/**
 * SettingsTaskFields — 「简洁任务模型」设置区块
 *
 * 挂在「设置 → 外观」下：
 * - 二选一：完整模式 / 简洁模式
 * - 简洁模式下勾选需要显示的字段（title 恒显、不可关闭）
 *
 * 只影响渲染，不删除任何数据；切回完整模式立即恢复。
 *
 * @example
 * <SettingsTaskFields />
 */

  import { t } from '$lib/i18n';
  import {
    ALL_TASK_FIELDS,
    DEFAULT_SIMPLE_FIELDS,
    taskFields,
    toggleSimpleField,
    setFieldMode,
    resetSimpleFields,
    type TaskField,
  } from '$lib/stores/task-fields';

  /** 字段 → i18n key */
  const fieldKey = (f: TaskField) => `settings.taskField.${f}`;
</script>

<div class="section">
  <span class="section-title">{$t('settings.taskFields.title')}</span>
  <div class="btn-row">
    <button class="btn-toggle" class:active={$taskFields.mode === 'full'} onclick={() => setFieldMode('full')}>
      {$t('settings.taskFields.modeFull')}
    </button>
    <button class="btn-toggle" class:active={$taskFields.mode === 'simple'} onclick={() => setFieldMode('simple')}>
      {$t('settings.taskFields.modeSimple')}
    </button>
  </div>
  <p class="hint">{$t('settings.taskFields.hint')}</p>

  {#if $taskFields.mode === 'simple'}
    <div class="field-grid">
      {#each ALL_TASK_FIELDS as f (f)}
        <label class="field-item" class:locked={f === 'title' || f === 'task_group'}>
          <input
            type="checkbox"
            checked={f === 'title' || f === 'task_group' || $taskFields.simple.includes(f)}
            disabled={f === 'title' || f === 'task_group'}
            onchange={(e) => toggleSimpleField(f, (e.target as HTMLInputElement).checked)}
          />
          <span>{$t(fieldKey(f))}</span>
        </label>
      {/each}
    </div>
    <div class="btn-row">
      <button class="btn-ghost small" onclick={resetSimpleFields}>{$t('settings.taskFields.reset')}</button>
    </div>
  {/if}
</div>

<style>
  .section { display: flex; flex-direction: column; gap: 8px; }
  .section-title { font-size: 11px; color: var(--text-muted); text-transform: uppercase; letter-spacing: 0; }
  .btn-row { display: flex; gap: 6px; }
  .btn-toggle { flex: 1; padding: 7px; font-size: 12px; border-radius: 6px; background: var(--surface); color: var(--text-secondary); border: 1px solid var(--border); cursor: pointer; }
  .btn-toggle.active { background: var(--accent); color: #fff; border-color: var(--accent); }
  .btn-ghost { padding: 5px 10px; font-size: 11px; border-radius: 6px; background: var(--surface); color: var(--text-secondary); border: 1px solid var(--border); cursor: pointer; }
  .btn-ghost:hover { border-color: var(--accent); color: var(--accent); }
  .hint { font-size: 11px; color: var(--text-muted); }
  .field-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 6px 10px; }
  .field-item { display: flex; align-items: center; gap: 6px; font-size: 12px; color: var(--text-secondary); cursor: pointer; }
  .field-item.locked { opacity: 0.6; cursor: not-allowed; }
  .field-item input { accent-color: var(--accent); }
</style>
