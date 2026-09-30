<script lang="ts">
/**
 * TaskDetail — 任务详情面板
 *
 * 右侧滑出的详细编辑面板，提供任务的完整属性编辑。
 * 子功能拆分为独立组件：标签、子任务、工时追踪、依赖、评论、Git。
 *
 * @example
 * <TaskDetail />
 */

  import { activeProject, activeTask, activeTaskId, updateTask, moveTaskToGroup, deleteTask, setReminder, clearReminder, TASK_COLORS } from '$lib/stores';
  import { visibleFields } from '$lib/stores/task-fields';
  import type { Priority } from '$lib/types';
  import Icon from '$lib/components/shared/Icon.svelte';
  import { t, locale } from '$lib/i18n';
  import { polishText } from '$lib/ai';
  import { animationLevel } from '$lib/stores/animation';
  import { fly } from 'svelte/transition';
  import TaskDetailTags from './task-detail/TaskDetailTags.svelte';
  import TaskDetailSubtasks from './task-detail/TaskDetailSubtasks.svelte';
  import TaskDetailTimeTracking from './task-detail/TaskDetailTimeTracking.svelte';
  import TaskDetailDependencies from './task-detail/TaskDetailDependencies.svelte';
  import TaskDetailRecurrence from './task-detail/TaskDetailRecurrence.svelte';
  import TaskDetailComments from './task-detail/TaskDetailComments.svelte';
  import { getInitialStatus, getTaskStatus } from '$lib/utils/task-status';

  let aiPolishing = $state(false);

  async function handleAiPolish() {
    if (!$activeProject || !$activeTask || !$activeTask.description?.trim()) return;
    aiPolishing = true;
    try {
      const result = await polishText($activeTask.description);
      upd('description', result);
    } catch (e: any) { alert(e?.message ?? String(e)); }
    aiPolishing = false;
  }

  const priorities: { v: Priority; c: string }[] = [
    { v: 'high', c: '#ef4444' },
    { v: 'medium', c: '#f59e0b' },
    { v: 'low', c: '#10b981' }
  ];
  let moveGroupId = $state('');
  let moveStatusId = $state('');
  let lastTaskId = '';
  let currentGroup = $derived($activeProject?.task_groups.find(group => group.id === $activeTask?.task_group_id) ?? null);
  let currentStatus = $derived($activeProject && $activeTask ? getTaskStatus($activeProject, $activeTask) : null);
  let moveGroup = $derived($activeProject?.task_groups.find(group => group.id === moveGroupId) ?? null);

  $effect(() => {
    if ($activeTask && $activeTask.id !== lastTaskId) {
      lastTaskId = $activeTask.id;
      moveGroupId = $activeTask.task_group_id;
      moveStatusId = $activeTask.status_id;
    }
  });

  const dateLocale = $derived($locale === 'zh' ? 'zh-CN' : 'en-US');

  function upd(field: string, val: unknown) { if ($activeProject && $activeTask) updateTask($activeProject.id, $activeTask.id, { [field]: val }); }
  function del() { if ($activeProject && $activeTask) deleteTask($activeProject.id, $activeTask.id); }
  function saveAndClose() { activeTaskId.set(null); }
  function chooseGroup(groupId: string) {
    if (!$activeProject || !$activeTask) return;
    const target = $activeProject.task_groups.find(group => group.id === groupId);
    if (!target) return;
    moveGroupId = target.id;
    const matches = currentStatus ? target.statuses.filter(status => status.category === currentStatus!.category) : [];
    moveStatusId = matches.length === 1 ? matches[0].id : getInitialStatus(target).id;
  }
  function applyMove() {
    if (!$activeProject || !$activeTask || !moveGroupId || !moveStatusId) return;
    moveTaskToGroup($activeProject.id, $activeTask.id, moveGroupId, moveStatusId);
  }
</script>

{#if $activeTask}
  <aside class="detail" transition:fly={{ x: 320, duration: $animationLevel === 'rich' ? 350 : 200, easing: $animationLevel === 'rich' ? (t => t * (2 - t)) : (t => t) }}>
    <div class="detail-header">
      <span class="detail-title">{$t('detail.title')}</span>
      <button class="close-btn" aria-label={$t('shortcut.closePanel')} onclick={() => activeTaskId.set(null)}><Icon name="close" size={14} /></button>
    </div>

    <div class="detail-body">
      <label class="label" for="task-title">{$t('detail.titleLabel')}</label>
      <textarea class="input title-input" id="task-title" rows="3" value={$activeTask.title} oninput={e => upd('title', (e.target as HTMLTextAreaElement).value)}></textarea>

      <span class="label">任务组</span>
      <div class="move-row">
        <select class="input" value={moveGroupId} onchange={e => chooseGroup((e.target as HTMLSelectElement).value)}>
          {#each $activeProject?.task_groups.filter(group => !group.archived) ?? [] as group}<option value={group.id}>{group.name}</option>{/each}
        </select>
        {#if moveGroupId !== $activeTask.task_group_id || moveStatusId !== $activeTask.status_id}<button class="move-confirm" onclick={applyMove}>确认移动</button>{/if}
      </div>

      {#if $visibleFields.has('status')}
        <span class="label">{$t('detail.status')}</span>
        {#if moveGroupId === $activeTask.task_group_id}
          <div class="btn-row status-row">
            {#each currentGroup?.statuses.slice().sort((a,b) => a.sort_order - b.sort_order) ?? [] as status}
              <button class="status-btn" class:active={$activeTask.status_id === status.id} style:--status-color={status.color} onclick={() => upd('status_id', status.id)}>{status.name}</button>
            {/each}
          </div>
        {:else}
          <select class="input" bind:value={moveStatusId}>{#each moveGroup?.statuses.slice().sort((a,b) => a.sort_order - b.sort_order) ?? [] as status}<option value={status.id}>{status.name}</option>{/each}</select>
        {/if}
      {/if}

      {#if $visibleFields.has('priority')}
        <span class="label">{$t('detail.priority')}</span>
        <div class="btn-row">
          {#each priorities as p}
            <button class="priority-btn" class:active={$activeTask.priority === p.v} style="color:{p.c};background:{p.c}22" onclick={() => upd('priority', p.v)}>{$t(`priority.${p.v}`)}</button>
          {/each}
        </div>
      {/if}

      {#if $visibleFields.has('description')}
        <label class="label" for="task-desc">{$t('detail.description')}</label>
        <textarea class="textarea" id="task-desc" placeholder={$t('detail.descPlaceholder')} value={$activeTask.description} oninput={e => upd('description', (e.target as HTMLTextAreaElement).value)}></textarea>
        <button class="ai-btn" onclick={handleAiPolish} disabled={aiPolishing || !$activeTask.description?.trim()}>
          {aiPolishing ? $t('detail.aiPolishing') : $t('detail.aiPolish')}
        </button>
      {/if}

      {#if $visibleFields.has('color')}
        <span class="label">{$t('detail.color')}</span>
        <div class="detail-color-row">
          {#each TASK_COLORS as c}
            <button type="button" class="detail-color-dot" class:active={($activeTask.color ?? '#a33b32') === c} style="background:{c}" onclick={() => upd('color', c)}></button>
          {/each}
        </div>
      {/if}

      {#if $visibleFields.has('due_date')}
        <label class="label" for="task-due">{$t('detail.dueDate')}</label>
        <input class="input" id="task-due" type="date" value={$activeTask.due_date ?? ''} oninput={e => upd('due_date', (e.target as HTMLInputElement).value || null)} />

        <label class="label" for="task-due-time">{$t('detail.dueTime')}</label>
        <input class="input" id="task-due-time" type="time" value={$activeTask.due_time ?? ''} oninput={e => upd('due_time', (e.target as HTMLInputElement).value || null)} />
      {/if}

      {#if $visibleFields.has('reminder')}
        <span class="label">{$t('detail.reminder')}</span>
        {#if $activeTask.reminder}
          <div class="reminder-row">
            <span class="reminder-time">{new Date($activeTask.reminder).toLocaleString(dateLocale)}</span>
            <button class="reminder-clear" onclick={() => $activeProject && $activeTask && clearReminder($activeProject.id, $activeTask.id)}>
              <Icon name="close" size={12} />
            </button>
          </div>
        {:else}
          <input class="input" type="datetime-local" onchange={(e) => {
            const val = (e.target as HTMLInputElement).value;
            if (val && $activeProject && $activeTask) setReminder($activeProject.id, $activeTask.id, new Date(val).toISOString());
          }} />
        {/if}
      {/if}

      {#if $visibleFields.has('tags')}<TaskDetailTags />{/if}
      {#if $visibleFields.has('subtasks')}<TaskDetailSubtasks />{/if}
      {#if $visibleFields.has('tracked_start')}<TaskDetailTimeTracking />{/if}
      {#if $visibleFields.has('recurrence')}<TaskDetailRecurrence />{/if}
      {#if $visibleFields.has('dependencies')}<TaskDetailDependencies />{/if}
      {#if $visibleFields.has('comments')}<TaskDetailComments />{/if}

      <div class="meta">
        <p>{$t('detail.created')} {new Date($activeTask.created_at).toLocaleString(dateLocale)}</p>
        <p>{$t('detail.updated')} {new Date($activeTask.updated_at).toLocaleString(dateLocale)}</p>
      </div>

    </div>

    <div class="detail-footer">
      <button class="delete-btn" onclick={del}>{$t('detail.delete')}</button>
      <button class="save-btn" onclick={saveAndClose}>{$t('detail.save')}</button>
    </div>
  </aside>
{/if}

<style>
  .detail { width: 340px; max-width: 100%; height: 100%; border-left: 1px solid var(--border-strong); background: var(--sidebar); display: flex; flex-direction: column; flex-shrink: 0; box-shadow: -8px 0 24px rgba(45, 35, 24, 0.06); }
  .detail-header { display: flex; align-items: center; justify-content: space-between; min-height: 51px; padding: 11px 16px; border-bottom: 3px double var(--border); background: var(--surface-raised); }
  .detail-title { display: flex; align-items: center; gap: 8px; font-family: var(--font-serif); font-size: 15px; font-weight: 700; }
  .detail-title::before { content: ''; width: 3px; height: 17px; background: var(--accent); }
  .close-btn { font-size: 18px; color: var(--text-muted); }
  .close-btn:hover { color: var(--text); }
  .detail-body { flex: 1; overflow-y: auto; padding: 17px 16px 20px; display: flex; flex-direction: column; gap: 13px; min-height: 0; }
  .label { font-size: 11px; font-weight: 600; color: var(--text-secondary); letter-spacing: 0; margin-bottom: -8px; }
  .input { width: 100%; padding: 8px 10px; font-size: 13px; border-radius: 2px; background: var(--surface-raised); border: 1px solid var(--border); color: var(--text); }
  .input:focus { border-color: var(--accent); box-shadow: 0 0 0 3px var(--focus-ring); }
  .title-input { min-height: 72px; line-height: 1.5; resize: vertical; font-family: inherit; }
  .textarea { width: 100%; padding: 9px 10px; font-size: 13px; line-height: 1.65; border-radius: 2px; background: var(--surface-raised); border: 1px solid var(--border); color: var(--text); resize: vertical; min-height: 120px; flex: 1; }
  .textarea:focus { border-color: var(--accent); box-shadow: 0 0 0 3px var(--focus-ring); }
  .textarea::placeholder { color: var(--text-muted); }
  .btn-row { display: flex; gap: 6px; }
  .move-row { display: flex; gap: 6px; align-items: center; }
  .move-row .input { flex: 1; }
  .move-confirm { min-height: 34px; padding: 0 9px; border: 1px solid var(--accent); color: var(--accent); font-size: 11px; white-space: nowrap; }
  .status-row { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 6px; }
  .status-btn { min-width: 0; min-height: 32px; padding: 7px; font-size: 12px; line-height: 1.3; overflow-wrap: anywhere; border-radius: 2px; border: 1px solid var(--border); background: var(--surface-raised); color: var(--text-secondary); transition: all 0.15s; }
  .status-btn:hover { background: var(--border); }
  .status-btn.active { background: var(--status-color, var(--accent)); border-color: var(--status-color, var(--accent)); color: #fff; }
  .priority-btn { flex: 1; padding: 7px; font-size: 12px; font-weight: 600; border-radius: 2px; border: 1px solid transparent; transition: all 0.15s; }
  .priority-btn.active { border-color: currentColor; box-shadow: inset 0 -2px 0 currentColor; }
  .reminder-row { display: flex; align-items: center; gap: 8px; padding: 7px 10px; background: var(--surface-raised); border: 1px solid var(--border); border-radius: 2px; }
  .reminder-time { flex: 1; font-size: 13px; color: var(--accent); min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .reminder-clear { color: var(--text-muted); padding: 2px; }
  .reminder-clear:hover { color: var(--red); }
  .detail-color-row { display: flex; gap: 5px; flex-wrap: wrap; }
  .detail-color-dot { width: 20px; height: 20px; border-radius: 2px; border: 2px solid transparent; cursor: pointer; transition: transform 0.15s, border-color 0.15s; flex-shrink: 0; }
  .detail-color-dot.active { border-color: var(--text); box-shadow: 0 0 0 2px var(--surface-raised); transform: scale(1.12); }
  .detail-color-dot:hover { transform: scale(1.15); }
  .meta { padding-top: 8px; border-top: 1px solid var(--border); font-size: 11px; color: var(--text-muted); line-height: 1.8; }
  .detail-footer { padding: 13px 16px; border-top: 3px double var(--border); background: var(--surface-raised); display: flex; gap: 8px; }
  .delete-btn, .save-btn { flex: 1; padding: 8px; font-size: 13px; border-radius: 2px; }
  .delete-btn { border: 1px solid rgba(182,59,52,0.35); background: rgba(182,59,52,0.1); color: var(--red); }
  .delete-btn:hover { background: rgba(239,68,68,0.25); }
  .save-btn { border: 1px solid var(--accent); background: var(--accent); color: var(--accent-contrast); }
  .save-btn:hover { background: var(--accent-hover); border-color: var(--accent-hover); }
  .ai-btn { padding: 5px 12px; font-size: 12px; border-radius: 2px; border: 1px solid var(--jade); background: var(--jade); color: var(--accent-contrast); align-self: flex-end; }
  .ai-btn:hover:not(:disabled) { opacity: 0.9; }
  .ai-btn:disabled { opacity: 0.4; cursor: not-allowed; }
  @media (max-width: 768px) {
    .detail { position: fixed; inset: 0; width: 100%; z-index: 500; border-left: none; }
    .close-btn { font-size: 24px; padding: 4px 8px; }
    .detail-body { padding: 12px; }
  }
</style>
