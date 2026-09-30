<script lang="ts">
  import { activeProject, activeTask, addDependency, removeDependency, updateStartOffset } from '$lib/stores';
  import Icon from '$lib/components/shared/Icon.svelte';
  import { t, tf } from '$lib/i18n';
  import { animationLevel } from '$lib/stores/animation';
  import { fade } from 'svelte/transition';

  let depTaskId = $state('');
  let depDays = $state(1);

  let availableTasks = $derived(
    $activeProject?.tasks.filter(t =>
      $activeTask && t.id !== $activeTask.id && !$activeTask.dependencies.some(d => d.taskId === t.id)
    ) ?? []
  );

  function handleAddDep() {
    if (!$activeProject || !$activeTask || !depTaskId) return;
    addDependency($activeProject.id, $activeTask.id, { taskId: depTaskId, dayOffset: depDays });
    depTaskId = ''; depDays = 1;
  }

  function handleRemoveDep(depId: string) {
    if ($activeProject && $activeTask) removeDependency($activeProject.id, $activeTask.id, depId);
  }

  function handleStartOffset(e: Event) {
    const val = (e.target as HTMLInputElement).value;
    if ($activeProject && $activeTask) updateStartOffset($activeProject.id, $activeTask.id, val ? parseInt(val) : null);
  }
</script>

{#if $activeTask}
<label class="label" for="task-offset">{$t('detail.startOffset')}</label>
<input class="input" id="task-offset" type="number" min="0" placeholder={$t('detail.offsetHint')} value={$activeTask.start_offset ?? ''} oninput={handleStartOffset} />

<span class="label">{$t('detail.dependencies')}</span>
{#if $activeTask.dependencies.length > 0}
  <div class="dep-list">
    {#each $activeTask.dependencies as dep (dep.taskId)}
      {@const src = $activeProject?.tasks.find(t => t.id === dep.taskId)}
      {#if src}
        <div class="dep-item" in:fade={{ duration: $animationLevel === 'none' ? 0 : 150 }}>
          <span class="dep-name">{src.title}</span>
          <span class="dep-days">{$tf('detail.dayOffset', { n: dep.dayOffset })}</span>
          <button class="dep-x" aria-label={$t('detail.delete')} onclick={() => handleRemoveDep(dep.taskId)}><Icon name="close" size={10} /></button>
        </div>
      {/if}
    {/each}
  </div>
{/if}
{#if availableTasks.length > 0}
  <div class="dep-add">
    <select class="dep-select" bind:value={depTaskId}>
      <option value="">{$t('detail.selectTask')}</option>
      {#each availableTasks as t}
        <option value={t.id}>{$activeProject?.task_groups.find(group => group.id === t.task_group_id)?.name} / {t.title}</option>
      {/each}
    </select>
    <input class="dep-days-input" type="number" min="1" bind:value={depDays} />
    <span class="dep-days-label">{$t('detail.dayUnit')}</span>
    <button class="dep-add-btn" onclick={handleAddDep} disabled={!depTaskId}>+</button>
  </div>
{/if}
{/if}

<style>
  .label { font-size: 11px; color: var(--text-muted); text-transform: uppercase; letter-spacing: 0; margin-bottom: -8px; }
  .input { width: 100%; padding: 8px 10px; font-size: 13px; border-radius: 6px; background: var(--surface); border: 1px solid var(--border); color: var(--text); }
  .input:focus { border-color: var(--accent); }
  .dep-list { display: flex; flex-direction: column; gap: 4px; }
  .dep-item { display: flex; align-items: center; gap: 6px; padding: 4px 8px; border-radius: 6px; background: var(--surface); font-size: 12px; }
  .dep-name { flex: 1; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .dep-days { font-size: 11px; color: var(--accent); font-weight: 600; flex-shrink: 0; }
  .dep-x { font-size: 14px; color: var(--text-muted); flex-shrink: 0; }
  .dep-x:hover { color: var(--red); }
  .dep-add { display: flex; gap: 4px; align-items: center; }
  .dep-select { flex: 1; padding: 5px 8px; font-size: 12px; border-radius: 6px; background: var(--surface); border: 1px solid var(--border); color: var(--text); }
  .dep-days-input { width: 48px; padding: 5px 6px; font-size: 12px; border-radius: 6px; background: var(--surface); border: 1px solid var(--border); color: var(--text); text-align: center; }
  .dep-days-label { font-size: 11px; color: var(--text-muted); }
  .dep-add-btn { padding: 5px 10px; font-size: 12px; border-radius: 6px; background: var(--accent); color: #fff; }
  .dep-add-btn:disabled { opacity: 0.4; cursor: not-allowed; }
</style>
