<script lang="ts">
  import { activeProject, activeTask, addSubtask, toggleSubtask, removeSubtask } from '$lib/stores';
  import Icon from '$lib/components/shared/Icon.svelte';
  import { t } from '$lib/i18n';
  import { animationLevel } from '$lib/stores/animation';
  import { fade } from 'svelte/transition';
  import { breakdownTask, parseBreakdownResult } from '$lib/ai';
  import { getTaskStatus } from '$lib/utils/task-status';

  let newSubtask = $state('');
  let aiBreaking = $state(false);
  function handleAddSubtask() {
    if (!$activeProject || !$activeTask || !newSubtask.trim()) return;
    addSubtask($activeProject.id, $activeTask.id, newSubtask.trim());
    newSubtask = '';
  }

  async function handleAiBreakdown() {
    if (!$activeProject || !$activeTask) return;
    aiBreaking = true;
    try {
      const existing = $activeTask.subtasks?.map(s => s.title).join(', ') || $t('common.none');
      const result = await breakdownTask({
        title: $activeTask.title,
        description: $activeTask.description || '',
        status: getTaskStatus($activeProject, $activeTask)?.name ?? '',
        task_group_name: $activeProject.task_groups.find(group => group.id === $activeTask!.task_group_id)?.name ?? '',
        status_category: getTaskStatus($activeProject, $activeTask)?.category ?? 'todo',
        priority: $activeTask.priority,
        due_date: $activeTask.due_date || '',
        existing_subtasks: existing,
      });
      const items = parseBreakdownResult(result);
      for (const item of items) {
        addSubtask($activeProject.id, $activeTask.id, item.title);
      }
    } catch (e: any) { alert(e?.message ?? String(e)); }
    aiBreaking = false;
  }

</script>

{#if $activeTask}
  <span class="label">{$t('detail.subtasks')}</span>
  {#if $activeTask.subtasks?.length > 0}
    {@const doneCount = $activeTask.subtasks.filter(s => s.done).length}
    <div class="sub-progress">
      <div class="sub-progress-bar"><div class="sub-progress-fill" style="width:{(doneCount / $activeTask.subtasks.length) * 100}%"></div></div>
      <span class="sub-progress-text">{doneCount}/{$activeTask.subtasks.length}</span>
    </div>
    <div class="sub-list">
      {#each $activeTask.subtasks as sub (sub.id)}
        <div class="sub-item" class:done={sub.done} in:fade={{ duration: $animationLevel === 'none' ? 0 : 150 }}>
          <button class="sub-check" class:checked={sub.done} aria-label={sub.done ? $t('status.todo') : $t('status.done')} onclick={() => $activeProject && $activeTask && toggleSubtask($activeProject.id, $activeTask.id, sub.id)}>
            {#if sub.done}<Icon name="check" size={10} />{/if}
          </button>
          <span class="sub-title">{sub.title}</span>
          <button class="sub-del" aria-label={$t('detail.delete')} onclick={() => $activeProject && $activeTask && removeSubtask($activeProject.id, $activeTask.id, sub.id)}><Icon name="close" size={10} /></button>
        </div>
      {/each}
    </div>
  {/if}
  <div class="sub-add">
    <input class="sub-input" placeholder={$t('detail.subtaskPlaceholder')} bind:value={newSubtask} onkeydown={e => e.key === 'Enter' && handleAddSubtask()} />
    <button class="sub-add-btn" onclick={handleAddSubtask}>+</button>
    <button class="ai-btn small" onclick={handleAiBreakdown} disabled={aiBreaking}>
      {aiBreaking ? $t('detail.aiBreaking') : $t('detail.aiBreakdown')}
    </button>
  </div>
{/if}

<style>
  .label { font-size: 11px; color: var(--text-muted); text-transform: uppercase; letter-spacing: 0; margin-bottom: -8px; }
  .sub-progress { display: flex; align-items: center; gap: 8px; margin-bottom: 4px; }
  .sub-progress-bar { flex: 1; height: 4px; background: var(--surface); border-radius: 2px; overflow: hidden; }
  .sub-progress-fill { height: 100%; background: var(--accent); border-radius: 2px; transition: width 0.2s; }
  .sub-progress-text { font-size: 11px; color: var(--text-muted); flex-shrink: 0; }
  .sub-list { display: flex; flex-direction: column; gap: 2px; margin-bottom: 4px; }
  .sub-item { display: flex; align-items: center; gap: 6px; padding: 4px 6px; border-radius: 4px; }
  .sub-item.done .sub-title { text-decoration: line-through; color: var(--text-muted); }
  .sub-check { width: 16px; height: 16px; border-radius: 4px; border: 1.5px solid var(--border); display: flex; align-items: center; justify-content: center; flex-shrink: 0; transition: all 0.15s; }
  .sub-check.checked { background: var(--accent); border-color: var(--accent); color: #fff; }
  .sub-title { font-size: 12px; flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .sub-del { color: var(--text-muted); padding: 2px; opacity: 0; transition: opacity 0.15s; }
  .sub-item:hover .sub-del { opacity: 1; }
  .sub-del:hover { color: var(--red); }
  .sub-add { display: flex; gap: 4px; }
  .sub-input { flex: 1; padding: 5px 8px; font-size: 12px; border-radius: 6px; background: var(--surface); border: 1px solid var(--border); color: var(--text); }
  .sub-input:focus { border-color: var(--accent); }
  .sub-input::placeholder { color: var(--text-muted); }
  .sub-add-btn { padding: 5px 10px; font-size: 12px; border-radius: 6px; background: var(--accent); color: #fff; }
  .ai-btn { padding: 5px 12px; font-size: 12px; border-radius: 2px; border: 1px solid var(--jade); background: var(--jade); color: var(--accent-contrast); align-self: flex-end; }
  .ai-btn:hover:not(:disabled) { opacity: 0.9; }
  .ai-btn:disabled { opacity: 0.4; cursor: not-allowed; }
  .ai-btn.small { align-self: auto; padding: 5px 10px; }
</style>
