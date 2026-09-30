<script lang="ts">
  import {
    activeProject,
    filteredTasks,
    activeTaskId,
    createTask,
    updateTask,
    deleteTask,
    toggleTaskStatus,
    searchQuery,
    priorityFilter,
    tagFilter,
    taskGroupFilter,
    statusFilter,
    reorderTask,
    startTracking,
    selectTask,
  } from '$lib/stores';
  import { sortable, type DropTarget } from '$lib/actions/sortable';
  import { resolveInsertIndex } from '$lib/utils/reorder-target';
  import { formatDuration } from '$lib/utils/date';
  import { getInitialStatus, getTaskStatus, isTaskClosed, isTaskCompleted } from '$lib/utils/task-status';
  import Icon from '$lib/components/shared/Icon.svelte';
  import LiveTimer from '$lib/components/shared/LiveTimer.svelte';
  import type { Priority, StatusCategory, Task } from '$lib/types';

  const categoryLabels: Record<StatusCategory, string> = { todo: '待处理', active: '进行中', done: '已完成', cancelled: '已取消' };
  const priorityColor: Record<Priority, string> = { high: '#ef4444', medium: '#f59e0b', low: '#10b981' };
  let newTitle = $state('');
  let targetGroupId = $state('');
  let editingId = $state<string | null>(null);
  let editTitle = $state('');
  let confirmDeleteId = $state<string | null>(null);

  let activeGroups = $derived(($activeProject?.task_groups ?? []).filter(group => !group.archived).sort((a, b) => a.sort_order - b.sort_order));
  let selectedGroup = $derived($taskGroupFilter === 'all' ? null : activeGroups.find(group => group.id === $taskGroupFilter) ?? null);
  let creationGroup = $derived(activeGroups.find(group => group.id === targetGroupId) ?? selectedGroup ?? activeGroups.find(group => group.id === $activeProject?.default_task_group_id) ?? activeGroups[0]);

  $effect(() => {
    if (creationGroup && !activeGroups.some(group => group.id === targetGroupId)) targetGroupId = creationGroup.id;
  });

  function addTask() {
    if (!$activeProject || !creationGroup || !newTitle.trim()) return;
    const selectedStatus = selectedGroup?.statuses.find(status => status.id === $statusFilter);
    createTask({
      projectId: $activeProject.id,
      taskGroupId: creationGroup.id,
      statusId: selectedStatus?.id ?? getInitialStatus(creationGroup).id,
      title: newTitle,
    });
    newTitle = '';
  }

  function startEdit(task: Task) { editingId = task.id; editTitle = task.title; }
  function saveEdit(task: Task) {
    if ($activeProject && editTitle.trim()) updateTask($activeProject.id, task.id, { title: editTitle.trim() });
    editingId = null;
  }
  function removeTask(event: MouseEvent, taskId: string) {
    event.stopPropagation();
    if (!$activeProject) return;
    if (confirmDeleteId === taskId) { deleteTask($activeProject.id, taskId); confirmDeleteId = null; }
    else { confirmDeleteId = taskId; setTimeout(() => confirmDeleteId = null, 2500); }
  }
  function onDropTask(fromDomIndex: number, target: DropTarget, fromId: string) {
    if (!$activeProject) return;
    const movedId = fromId || $filteredTasks[fromDomIndex]?.id;
    const fromIndex = $activeProject.tasks.findIndex(task => task.id === movedId);
    const toIndex = resolveInsertIndex($activeProject.tasks, $filteredTasks, movedId, target.index);
    if (fromIndex >= 0 && toIndex >= 0 && fromIndex !== toIndex) reorderTask($activeProject.id, movedId, toIndex);
  }
</script>

<div class="list-view">
  {#if $activeProject}
    <header class="list-header"><span class="project-dot" style="background:{$activeProject.color}"></span><div><h2>{$activeProject.name}</h2><span>{$activeProject.tasks.length} 个任务</span></div></header>

    <div class="create-row">
      <input class="add-input" placeholder="添加任务" bind:value={newTitle} onkeydown={event => event.key === 'Enter' && addTask()} />
      {#if $taskGroupFilter === 'all'}
        <select aria-label="新任务所属任务组" bind:value={targetGroupId}>{#each activeGroups as group}<option value={group.id}>{group.name}</option>{/each}</select>
      {/if}
      <button onclick={addTask} disabled={!newTitle.trim()}>添加</button>
    </div>

    <div class="filters">
      <select aria-label="任务组筛选" bind:value={$taskGroupFilter} onchange={() => $statusFilter = 'all'}>
        <option value="all">全部任务组</option>
        {#each activeGroups as group}<option value={group.id}>{group.name}</option>{/each}
      </select>
      <select aria-label="状态筛选" bind:value={$statusFilter}>
        <option value="all">全部状态</option>
        {#if selectedGroup}
          {#each selectedGroup.statuses.slice().sort((a,b) => a.sort_order - b.sort_order) as status}<option value={status.id}>{status.name}</option>{/each}
        {:else}
          {#each Object.entries(categoryLabels) as [value, label]}<option value={value}>{label}</option>{/each}
        {/if}
      </select>
      <select bind:value={$priorityFilter}><option value="all">全部优先级</option><option value="high">高</option><option value="medium">中</option><option value="low">低</option></select>
      <select bind:value={$tagFilter}><option value="all">全部标签</option>{#each $activeProject.tags as tag}<option value={tag.name}>{tag.name}</option>{/each}</select>
      <input class="filter-search" placeholder="搜索任务" bind:value={$searchQuery} />
    </div>

    <div class="task-list" use:sortable={{ handle: '.drag-handle', axis: 'y' as const, onDrop: onDropTask }}>
      {#if $filteredTasks.length === 0}<div class="empty"><Icon name="clipboard" size={30} /><span>没有符合条件的任务</span></div>{/if}
      {#each $filteredTasks as task (task.id)}
        {@const group = $activeProject.task_groups.find(item => item.id === task.task_group_id)}
        {@const status = getTaskStatus($activeProject, task)}
        <div class="task-row" class:selected={$activeTaskId === task.id} data-sortable-item data-sortable-id={task.id} role="button" tabindex="0" onclick={() => selectTask($activeProject!.id, task.id)} onkeydown={event => event.key === 'Enter' && selectTask($activeProject!.id, task.id)}>
          <span class="drag-handle" title="拖动排序">⠿</span>
          <button class="check" class:done={isTaskCompleted($activeProject, task)} aria-label={isTaskClosed($activeProject, task) ? '重新打开' : '完成任务'} onclick={event => { event.stopPropagation(); toggleTaskStatus($activeProject!.id, task.id); }}>
            {#if isTaskCompleted($activeProject, task)}<Icon name="check" size={12} />{/if}
          </button>
          <span class="priority" style="background:{priorityColor[task.priority]}"></span>
          <div class="task-main">
            {#if editingId === task.id}
              <input bind:value={editTitle} onblur={() => saveEdit(task)} onkeydown={event => event.key === 'Enter' && saveEdit(task)} onclick={event => event.stopPropagation()} />
            {:else}
              <strong class:done={isTaskCompleted($activeProject, task)} ondblclick={event => { event.stopPropagation(); startEdit(task); }}>{task.title}</strong>
            {/if}
            <div class="context"><span class="group-badge" title={group?.name}>{group?.name ?? '未知任务组'}</span><span class="status-badge" style="color:{status?.color}">{status?.name ?? '未知状态'}</span></div>
          </div>
          {#if task.tracked_start && !isTaskClosed($activeProject, task)}<span class="timer"><LiveTimer start={task.tracked_start} /></span>
          {:else if task.tracked_start && task.completed_at}<span class="timer">{formatDuration(task.tracked_start, task.completed_at)}</span>
          {:else}<button class="icon-btn" title="开始计时" aria-label="开始计时" onclick={event => { event.stopPropagation(); startTracking($activeProject!.id, task.id); }}><Icon name="play" size={11} /></button>{/if}
          {#if task.due_date}<time class:overdue={!isTaskClosed($activeProject, task) && new Date(task.due_date) < new Date()}>{task.due_date.slice(5)}</time>{/if}
          <button class="delete-btn" class:confirm={confirmDeleteId === task.id} title={confirmDeleteId === task.id ? '再次点击确认删除' : '删除任务'} aria-label="删除任务" onclick={event => removeTask(event, task.id)}><Icon name="trash" size={13} /></button>
        </div>
      {/each}
    </div>
  {/if}
</div>

<style>
  .list-view { flex: 1; min-height: 0; display: flex; flex-direction: column; overflow: hidden; padding: 18px 22px; background: var(--bg); }
  .list-header { display: flex; align-items: center; gap: 10px; margin-bottom: 14px; }
  .project-dot, .priority { width: 8px; height: 8px; flex: 0 0 8px; }
  h2 { margin: 0; font-size: 17px; }
  .list-header span { font-size: 11px; color: var(--text-muted); }
  .create-row, .filters { display: flex; gap: 7px; margin-bottom: 10px; }
  .create-row input { flex: 1; min-width: 120px; }
  input, select { min-width: 0; min-height: 34px; padding: 7px 9px; border: 1px solid var(--border); background: var(--surface-raised); color: var(--text); font-size: 12px; }
  .create-row button { padding: 0 16px; background: var(--accent); color: var(--accent-contrast); }
  .filters .filter-search { flex: 1; }
  .task-list { flex: 1; min-height: 0; overflow-y: auto; border-top: 1px solid var(--border); }
  .task-row { min-height: 58px; display: grid; grid-template-columns: 18px 28px 8px minmax(170px,1fr) auto auto 30px; gap: 9px; align-items: center; padding: 7px 8px; border-bottom: 1px solid var(--border); cursor: pointer; }
  .task-row:hover, .task-row.selected { background: var(--surface-raised); }
  .task-row.selected { box-shadow: inset 3px 0 var(--accent); }
  .drag-handle { color: var(--text-muted); cursor: grab; }
  .check { width: 22px; height: 22px; display: inline-flex; align-items: center; justify-content: center; border: 1px solid var(--border-strong); color: var(--accent-contrast); }
  .check.done { background: var(--green); border-color: var(--green); }
  .task-main { min-width: 0; }
  .task-main strong { display: block; font-size: 13px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .task-main strong.done { text-decoration: line-through; color: var(--text-muted); }
  .task-main input { width: 100%; }
  .context { display: flex; gap: 7px; margin-top: 4px; min-width: 0; font-size: 10px; }
  .group-badge { max-width: 150px; padding: 1px 5px; background: var(--surface); border: 1px solid var(--border); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .status-badge { font-weight: 600; }
  .timer, time { font-size: 10px; color: var(--text-muted); white-space: nowrap; }
  time.overdue { color: var(--red); }
  .icon-btn, .delete-btn { width: 28px; height: 28px; display: inline-flex; align-items: center; justify-content: center; color: var(--text-muted); }
  .delete-btn:hover, .delete-btn.confirm { color: var(--red); background: rgba(239,68,68,.1); }
  .empty { height: 180px; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 8px; color: var(--text-muted); }
  @media (max-width: 760px) {
    .list-view { padding: 12px; overflow: visible; }
    .create-row, .filters { flex-wrap: wrap; }
    .filters > * { flex: 1 1 42%; }
    .task-list { overflow: visible; }
    .task-row { grid-template-columns: 16px 26px 7px minmax(0,1fr) 28px; }
    .timer, time { display: none; }
  }
</style>
