<script lang="ts">
  import { activeProject, activeTask, startTracking } from '$lib/stores';
  import { formatDuration } from '$lib/utils/date';
  import Icon from '$lib/components/shared/Icon.svelte';
  import LiveTimer from '$lib/components/shared/LiveTimer.svelte';
  import { t, locale } from '$lib/i18n';
  import { isTaskClosed } from '$lib/utils/task-status';

  const dateLocale = $derived($locale === 'zh' ? 'zh-CN' : 'en-US');
</script>

{#if $activeTask}
  <span class="label">{$t('detail.timeTracking')}</span>
  <div class="time-section">
    {#if $activeTask.tracked_start}
      <div class="time-info">
        <span class="time-label">{$t('detail.timeStart')}</span>
        <span class="time-value">{new Date($activeTask.tracked_start).toLocaleString(dateLocale)}</span>
      </div>
      {#if $activeProject && isTaskClosed($activeProject, $activeTask)}
        <div class="time-info">
          <span class="time-label">{$t('detail.timeEnd')}</span>
          <span class="time-value">{new Date($activeTask.completed_at ?? $activeTask.updated_at).toLocaleString(dateLocale)}</span>
        </div>
        <div class="time-total">
          <span class="time-total-label">{$t('detail.timeTotal')}</span>
          <span class="time-total-value">{formatDuration($activeTask.tracked_start, $activeTask.completed_at ?? $activeTask.updated_at)}</span>
        </div>
      {:else}
        <div class="time-total active">
          <span class="time-total-label">{$t('detail.timeElapsed')}</span>
          <span class="time-total-value"><span class="time-dot"></span><LiveTimer start={$activeTask.tracked_start} /></span>
        </div>
      {/if}
    {:else}
      <button class="time-start-btn" onclick={() => $activeProject && $activeTask && startTracking($activeProject.id, $activeTask.id)}>
        <Icon name="play" size={14} /> {$t('detail.startBtn')}
      </button>
    {/if}
  </div>
{/if}

<style>
  .label { font-size: 11px; color: var(--text-muted); text-transform: uppercase; letter-spacing: 0; margin-bottom: -8px; }
  .time-section { display: flex; flex-direction: column; gap: 6px; padding: 8px 10px; background: var(--surface); border-radius: 8px; }
  .time-info { display: flex; justify-content: space-between; font-size: 12px; }
  .time-label { color: var(--text-muted); }
  .time-value { color: var(--text-secondary); }
  .time-total { display: flex; justify-content: space-between; align-items: center; padding-top: 6px; border-top: 1px solid var(--border); }
  .time-total-label { font-size: 11px; color: var(--text-muted); }
  .time-total-value { font-size: 14px; font-weight: 600; color: var(--text); display: flex; align-items: center; gap: 6px; }
  .time-total.active .time-total-value { color: var(--accent); }
  .time-dot { width: 6px; height: 6px; border-radius: 50%; background: var(--accent); animation: pulse 1.5s ease-in-out infinite; }
  @keyframes pulse { 0%, 100% { opacity: 1; } 50% { opacity: 0.4; } }
  .time-start-btn { display: flex; align-items: center; justify-content: center; gap: 6px; padding: 8px; font-size: 13px; border-radius: 8px; background: var(--accent); color: #fff; }
  .time-start-btn:hover { opacity: 0.9; }
</style>
