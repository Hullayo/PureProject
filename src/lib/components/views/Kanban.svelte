<script lang="ts">
  import {
    activeProject,
    activeTaskGroup,
    activeTaskGroupId,
    createTask,
    createTaskGroup,
    createTaskStatus,
    updateTask,
    reorderTask,
    selectTask,
    setActiveTaskGroup,
    setDefaultTaskGroup,
    archiveTaskGroup,
    restoreTaskGroup,
    deleteTaskGroup,
    renameTaskGroup,
    reorderTaskGroups,
    updateTaskStatusDefinition,
    setGroupInitialStatus,
    setGroupCompletionStatus,
    reorderTaskStatuses,
    deleteTaskStatus,
  } from '$lib/stores';
  import type { Priority, StatusCategory, Task } from '$lib/types';
  import { sortable, type DropTarget } from '$lib/actions/sortable';
  import { resolveInsertIndex } from '$lib/utils/reorder-target';
  import { isTaskClosed, isTaskCompleted } from '$lib/utils/task-status';
  import Icon from '$lib/components/shared/Icon.svelte';
  import { t, tf } from '$lib/i18n';

  const priorityColor: Record<Priority, string> = { high: '#ef4444', medium: '#f59e0b', low: '#10b981' };
  const categories: { value: StatusCategory; labelKey: string }[] = [
    { value: 'todo', labelKey: 'statusManager.categoryTodo' },
    { value: 'active', labelKey: 'statusManager.categoryActive' },
    { value: 'done', labelKey: 'statusManager.categoryDone' },
    { value: 'cancelled', labelKey: 'statusManager.categoryCancelled' },
  ];

  let drawerOpen = $state(false);
  let managerOpen = $state(false);
  let groupMenuId = $state<string | null>(null);
  let renameGroupId = $state<string | null>(null);
  let renameGroupName = $state('');
  let confirmDeleteGroupId = $state<string | null>(null);
  let newGroupName = $state('');
  let newStatusName = $state('');
  let newStatusCategory = $state<StatusCategory>('todo');
  let pendingCategory = $state<{ statusId: string; category: StatusCategory } | null>(null);
  let pendingDeleteStatusId = $state<string | null>(null);
  let migrateToStatusId = $state('');
  let newTaskTitles = $state<Record<string, string>>({});
  let overZone = $state<string | null>(null);
  let overIndex = $state(-1);

  let groups = $derived(($activeProject?.task_groups ?? []).slice().sort((a, b) => a.sort_order - b.sort_order));
  let columns = $derived.by(() => {
    if (!$activeProject || !$activeTaskGroup) return [];
    return $activeTaskGroup.statuses.slice().sort((a, b) => a.sort_order - b.sort_order).map(status => ({
      ...status,
      cards: $activeProject!.tasks.filter(task => task.task_group_id === $activeTaskGroup!.id && task.status_id === status.id),
    }));
  });

  function openGroup(groupId: string) {
    if (!$activeProject) return;
    setActiveTaskGroup($activeProject.id, groupId);
    drawerOpen = false;
  }

  function addGroup() {
    if (!$activeProject || !newGroupName.trim()) return;
    createTaskGroup($activeProject.id, newGroupName);
    newGroupName = '';
  }

  function addStatus() {
    if (!$activeProject || !$activeTaskGroup || !newStatusName.trim()) return;
    createTaskStatus($activeProject.id, $activeTaskGroup.id, newStatusName, newStatusCategory);
    newStatusName = '';
  }

  function addTask(statusId: string) {
    const title = newTaskTitles[statusId]?.trim();
    if (!$activeProject || !$activeTaskGroup || !title) return;
    createTask({ projectId: $activeProject.id, taskGroupId: $activeTaskGroup.id, statusId, title });
    newTaskTitles = { ...newTaskTitles, [statusId]: '' };
  }

  function tasksByStatus(statusId: string): Task[] {
    return columns.find(column => column.id === statusId)?.cards ?? [];
  }

  function onDropCard(_fromDomIndex: number, target: DropTarget, fromId: string) {
    const project = $activeProject;
    const group = $activeTaskGroup;
    const zone = target.zone ?? overZone;
    overZone = null;
    overIndex = -1;
    if (!project || !group || !fromId || !zone || !group.statuses.some(status => status.id === zone)) return;
    const all = project.tasks;
    const fromAll = all.findIndex(task => task.id === fromId);
    if (fromAll < 0) return;
    const crossColumn = all[fromAll].status_id !== zone;
    const toAll = resolveInsertIndex(all, tasksByStatus(zone), fromId, target.index, crossColumn ? 'append' : 'noop');
    if (toAll < 0) return;
    if (crossColumn && !updateTask(project.id, fromId, { status_id: zone })) return;
    if (toAll !== fromAll) reorderTask(project.id, fromId, toAll, crossColumn);
  }

  function activeCount(groupId: string): number {
    if (!$activeProject) return 0;
    return $activeProject.tasks.filter(task => task.task_group_id === groupId && !isTaskClosed($activeProject!, task)).length;
  }

  function taskCount(groupId: string): number {
    return $activeProject?.tasks.filter(task => task.task_group_id === groupId).length ?? 0;
  }

  function beginRename(groupId: string, name: string) {
    renameGroupId = groupId;
    renameGroupName = name;
    groupMenuId = null;
  }

  function saveRename() {
    if (!$activeProject || !renameGroupId || !renameGroupName.trim()) return;
    renameTaskGroup($activeProject.id, renameGroupId, renameGroupName);
    renameGroupId = null;
    renameGroupName = '';
  }

  function moveGroup(groupId: string, direction: -1 | 1) {
    if (!$activeProject) return;
    const ids = groups.map(group => group.id);
    const from = ids.indexOf(groupId);
    const to = from + direction;
    if (from < 0 || to < 0 || to >= ids.length) return;
    [ids[from], ids[to]] = [ids[to], ids[from]];
    reorderTaskGroups($activeProject.id, ids);
    groupMenuId = null;
  }

  function archiveGroup(groupId: string) {
    if (!$activeProject) return;
    archiveTaskGroup($activeProject.id, groupId);
    groupMenuId = null;
  }

  function restoreGroup(groupId: string) {
    if (!$activeProject) return;
    restoreTaskGroup($activeProject.id, groupId);
    groupMenuId = null;
  }

  function removeEmptyGroup(groupId: string) {
    if (!$activeProject) return;
    if (confirmDeleteGroupId !== groupId) {
      confirmDeleteGroupId = groupId;
      return;
    }
    deleteTaskGroup($activeProject.id, groupId);
    confirmDeleteGroupId = null;
    groupMenuId = null;
  }

  function referenceCount(statusId: string): number {
    if (!$activeProject || !$activeTaskGroup) return 0;
    return $activeProject.tasks.filter(task => task.task_group_id === $activeTaskGroup!.id && task.status_id === statusId).length;
  }

  function requestCategoryChange(statusId: string, category: StatusCategory) {
    const status = $activeTaskGroup?.statuses.find(item => item.id === statusId);
    if (!status || status.category === category) return;
    pendingCategory = { statusId, category };
  }

  function confirmCategoryChange() {
    if (!$activeProject || !$activeTaskGroup || !pendingCategory) return;
    updateTaskStatusDefinition($activeProject.id, $activeTaskGroup.id, pendingCategory.statusId, { category: pendingCategory.category });
    pendingCategory = null;
  }

  function moveStatus(statusId: string, direction: -1 | 1) {
    if (!$activeProject || !$activeTaskGroup) return;
    const ids = $activeTaskGroup.statuses
      .slice()
      .sort((a, b) => a.sort_order - b.sort_order)
      .map(status => status.id);
    const from = ids.indexOf(statusId);
    const to = from + direction;
    if (from < 0 || to < 0 || to >= ids.length) return;
    [ids[from], ids[to]] = [ids[to], ids[from]];
    reorderTaskStatuses($activeProject.id, $activeTaskGroup.id, ids);
  }

  function requestDeleteStatus(statusId: string) {
    if (!$activeTaskGroup) return;
    pendingDeleteStatusId = statusId;
    migrateToStatusId = $activeTaskGroup.statuses.find(status => status.id !== statusId)?.id ?? '';
  }

  function confirmDeleteStatus() {
    if (!$activeProject || !$activeTaskGroup || !pendingDeleteStatusId) return;
    const target = referenceCount(pendingDeleteStatusId) > 0 ? migrateToStatusId : undefined;
    if (referenceCount(pendingDeleteStatusId) > 0 && !target) return;
    if (deleteTaskStatus($activeProject.id, $activeTaskGroup.id, pendingDeleteStatusId, target)) {
      pendingDeleteStatusId = null;
      migrateToStatusId = '';
    }
  }
</script>

<div class="kanban-shell">
  {#if drawerOpen}<button class="drawer-scrim" aria-label={$t('taskGroup.close')} onclick={() => drawerOpen = false}></button>{/if}
  <aside class:drawer-open={drawerOpen} class="group-panel">
    <div class="group-panel-title"><span>{$t('taskGroup.title')}</span><span>{groups.filter(group => !group.archived).length}</span></div>
    <div class="group-list">
      {#each groups as group, groupIndex (group.id)}
        <div class="group-row" class:menu-open={groupMenuId === group.id}>
          {#if renameGroupId === group.id}
            <div class="group-rename">
              <input aria-label={$t('taskGroup.name')} bind:value={renameGroupName} onkeydown={event => { if (event.key === 'Enter') saveRename(); if (event.key === 'Escape') renameGroupId = null; }} />
              <button title={$t('taskGroup.saveName')} aria-label={$t('taskGroup.saveName')} onclick={saveRename}><Icon name="check" size={14} /></button>
            </div>
          {:else}
            <button
              class="group-item"
              class:active={$activeTaskGroupId === group.id}
              class:archived={group.archived}
              onclick={() => !group.archived && openGroup(group.id)}
              title={group.archived ? `${group.name}${$t('taskGroup.archivedSuffix')}` : group.name}
            >
              <span class="group-name">{group.name}</span>
              {#if $activeProject?.default_task_group_id === group.id}<span class="default-mark" title={$t('taskGroup.default')}>◆</span>{/if}
              <span class="group-count">{activeCount(group.id)}</span>
            </button>
            <button class="group-more" title={$t('taskGroup.actions')} aria-label={`${group.name}: ${$t('taskGroup.actions')}`} onclick={() => groupMenuId = groupMenuId === group.id ? null : group.id}><Icon name="more-horizontal" size={15} /></button>
          {/if}
          {#if groupMenuId === group.id}
            <div class="group-menu">
              <button onclick={() => beginRename(group.id, group.name)}>{$t('taskGroup.rename')}</button>
              {#if !group.archived}
                <button disabled={$activeProject?.default_task_group_id === group.id} onclick={() => { if ($activeProject) setDefaultTaskGroup($activeProject.id, group.id); groupMenuId = null; }}>{$t('taskGroup.setDefault')}</button>
                <button disabled={groups.filter(item => !item.archived).length <= 1} title={groups.filter(item => !item.archived).length <= 1 ? $t('taskGroup.keepOne') : $t('taskGroup.archive')} onclick={() => archiveGroup(group.id)}>{$t('taskGroup.archive')}</button>
              {:else}
                <button onclick={() => restoreGroup(group.id)}>{$t('taskGroup.restore')}</button>
              {/if}
              <div class="menu-separator"></div>
              <div class="order-actions">
                <button title={$t('taskGroup.moveUp')} aria-label={$t('taskGroup.moveUp')} disabled={groupIndex === 0} onclick={() => moveGroup(group.id, -1)}><Icon name="chevron-up" size={14} /></button>
                <button title={$t('taskGroup.moveDown')} aria-label={$t('taskGroup.moveDown')} disabled={groupIndex === groups.length - 1} onclick={() => moveGroup(group.id, 1)}><Icon name="chevron-down" size={14} /></button>
              </div>
              <button class="danger-command" disabled={taskCount(group.id) > 0 || groups.filter(item => !item.archived && item.id !== group.id).length === 0} title={taskCount(group.id) > 0 ? $tf('taskGroup.deleteBlockedTasks', { n: taskCount(group.id) }) : $t('taskGroup.deleteEmpty')} onclick={() => removeEmptyGroup(group.id)}>{confirmDeleteGroupId === group.id ? $t('taskGroup.deleteConfirm') : $t('taskGroup.deleteEmpty')}</button>
            </div>
          {/if}
        </div>
      {/each}
    </div>
    <div class="group-add">
      <input aria-label={$t('taskGroup.name')} placeholder={$t('taskGroup.newPlaceholder')} bind:value={newGroupName} onkeydown={event => event.key === 'Enter' && addGroup()} />
      <button aria-label={$t('taskGroup.add')} title={$t('taskGroup.add')} onclick={addGroup} disabled={!newGroupName.trim()}><Icon name="plus" size={15} /></button>
    </div>
  </aside>

  <section class="board-area">
    {#if $activeProject && $activeTaskGroup}
      <header class="board-header">
        <button class="group-toggle" title={$t('taskGroup.select')} aria-label={$t('taskGroup.select')} onclick={() => drawerOpen = true}><Icon name="menu" size={17} /></button>
        <span class="project-dot" style="background:{$activeProject.color}"></span>
        <div class="heading"><strong>{$activeTaskGroup.name}</strong><span>{$activeProject.name}</span></div>
        <button class="header-command" onclick={() => managerOpen = !managerOpen}>{$t('statusManager.title')}</button>
      </header>

      {#if managerOpen}
        <div class="status-manager">
          <div class="status-manager-heading"><strong>{$t('statusManager.title')}</strong><button aria-label={$t('sidebar.cancel')} title={$t('sidebar.cancel')} onclick={() => managerOpen = false}><Icon name="close" size={14} /></button></div>
          <div class="status-roles">
            <label>{$t('statusManager.initial')}
              <select value={$activeTaskGroup.initial_status_id} onchange={event => setGroupInitialStatus($activeProject!.id, $activeTaskGroup!.id, (event.target as HTMLSelectElement).value)}>
                {#each $activeTaskGroup.statuses.slice().sort((a, b) => a.sort_order - b.sort_order) as status}<option value={status.id}>{status.name}</option>{/each}
              </select>
            </label>
            <label>{$t('statusManager.completion')}
              <select value={$activeTaskGroup.completion_status_id} onchange={event => setGroupCompletionStatus($activeProject!.id, $activeTaskGroup!.id, (event.target as HTMLSelectElement).value)}>
                {#each $activeTaskGroup.statuses.filter(status => status.category === 'done').sort((a, b) => a.sort_order - b.sort_order) as status}<option value={status.id}>{status.name}</option>{/each}
              </select>
            </label>
          </div>
          <div class="status-list">
            {#each $activeTaskGroup.statuses.slice().sort((a, b) => a.sort_order - b.sort_order) as status, statusIndex (status.id)}
              <div class="status-row">
                <input class="color-input" type="color" value={status.color} aria-label={$t('statusManager.color')} onchange={event => updateTaskStatusDefinition($activeProject!.id, $activeTaskGroup!.id, status.id, { color: (event.target as HTMLInputElement).value })} />
                <input value={status.name} aria-label={$t('statusManager.name')} onchange={event => updateTaskStatusDefinition($activeProject!.id, $activeTaskGroup!.id, status.id, { name: (event.target as HTMLInputElement).value })} />
                <select value={status.category} aria-label={$t('statusManager.category')} disabled={status.id === $activeTaskGroup.completion_status_id} title={status.id === $activeTaskGroup.completion_status_id ? $t('statusManager.completionLocked') : $t('statusManager.changeCategory')} onchange={event => requestCategoryChange(status.id, (event.target as HTMLSelectElement).value as StatusCategory)}>
                  {#each categories as category}<option value={category.value}>{$t(category.labelKey)}</option>{/each}
                </select>
                <div class="status-order">
                  <button title={$t('statusManager.moveBefore')} aria-label={$t('statusManager.moveBefore')} disabled={statusIndex === 0} onclick={() => moveStatus(status.id, -1)}><Icon name="chevron-up" size={13} /></button>
                  <button title={$t('statusManager.moveAfter')} aria-label={$t('statusManager.moveAfter')} disabled={statusIndex === $activeTaskGroup!.statuses.length - 1} onclick={() => moveStatus(status.id, 1)}><Icon name="chevron-down" size={13} /></button>
                </div>
                <button class="icon-command" disabled={status.id === $activeTaskGroup.initial_status_id || status.id === $activeTaskGroup.completion_status_id} title={status.id === $activeTaskGroup.initial_status_id ? $t('statusManager.deleteInitialBlocked') : status.id === $activeTaskGroup.completion_status_id ? $t('statusManager.deleteCompletionBlocked') : $t('statusManager.delete')} aria-label={$t('statusManager.delete')} onclick={() => requestDeleteStatus(status.id)}><Icon name="trash" size={14} /></button>
              </div>
            {/each}
          </div>
          {#if pendingCategory}
            <div class="manager-confirm warning-box">
              <div><strong>{$t('statusManager.confirmCategoryTitle')}</strong><span>{$tf('statusManager.confirmCategoryBody', { n: referenceCount(pendingCategory.statusId) })}</span></div>
              <button onclick={() => pendingCategory = null}>{$t('sidebar.cancel')}</button>
              <button class="primary-command" onclick={confirmCategoryChange}>{$t('statusManager.confirmChange')}</button>
            </div>
          {/if}
          {#if pendingDeleteStatusId}
            <div class="manager-confirm warning-box">
              <div><strong>{$t('statusManager.confirmDeleteTitle')}</strong><span>{$t('statusManager.deleteUndo')} {referenceCount(pendingDeleteStatusId) > 0 ? $tf('statusManager.deleteReferenced', { n: referenceCount(pendingDeleteStatusId) }) : $t('statusManager.deleteUnreferenced')}</span></div>
              {#if referenceCount(pendingDeleteStatusId) > 0}
                <label class="migration-target">{$t('statusManager.migrateTo')}
                  <select bind:value={migrateToStatusId}>
                    {#each $activeTaskGroup.statuses.filter(status => status.id !== pendingDeleteStatusId).sort((a, b) => a.sort_order - b.sort_order) as status}<option value={status.id}>{status.name}</option>{/each}
                  </select>
                </label>
              {/if}
              <button onclick={() => pendingDeleteStatusId = null}>{$t('sidebar.cancel')}</button>
              <button class="danger-command" onclick={confirmDeleteStatus}>{referenceCount(pendingDeleteStatusId) > 0 ? $t('statusManager.migrateDelete') : $t('statusManager.confirmDelete')}</button>
            </div>
          {/if}
          <div class="status-add">
            <input placeholder={$t('statusManager.name')} bind:value={newStatusName} onkeydown={event => event.key === 'Enter' && addStatus()} />
            <select aria-label={$t('statusManager.category')} bind:value={newStatusCategory}>{#each categories as category}<option value={category.value}>{$t(category.labelKey)}</option>{/each}</select>
            <button onclick={addStatus} disabled={!newStatusName.trim()}>{$t('statusManager.add')}</button>
          </div>
        </div>
      {/if}

      <div class="kanban-cols" use:sortable={{ handle: '.card-handle', handleOnly: false, axis: 'free' as const, longPressMs: 240, onOver: target => { if (target.zone) { overZone = target.zone; overIndex = target.index; } }, onDrop: onDropCard, onCancel: () => { overZone = null; overIndex = -1; } }}>
        {#each columns as column (column.id)}
          <div class="kanban-col">
            <div class="col-header"><span class="status-dot" style="background:{column.color}"></span><span title={column.name}>{column.name}</span><span class="col-count">{column.cards.length}</span></div>
            <div class="kanban-cards" class:drop-target={overZone === column.id} data-sortable-zone={column.id}>
              <div class="task-add"><input placeholder={$t('task.addPlaceholder')} value={newTaskTitles[column.id] ?? ''} oninput={event => newTaskTitles = { ...newTaskTitles, [column.id]: (event.target as HTMLInputElement).value }} onkeydown={event => event.key === 'Enter' && addTask(column.id)} /><button title={$t('task.add')} aria-label={$t('task.add')} onclick={() => addTask(column.id)}><Icon name="plus" size={14} /></button></div>
              {#each column.cards as task, index (task.id)}
                <article
                  class="task-card"
                  class:closed={isTaskClosed($activeProject, task)}
                  class:completed={isTaskCompleted($activeProject, task)}
                  class:drop-before={overZone === column.id && overIndex === index}
                  data-sortable-item
                  data-sortable-id={task.id}
                  role="button"
                  tabindex="0"
                  onclick={() => selectTask($activeProject!.id, task.id)}
                  onkeydown={event => event.key === 'Enter' && selectTask($activeProject!.id, task.id)}
                >
                  <span class="card-handle" title={$t('drag.handle')}>⠿</span>
                  <span class="priority" style="background:{priorityColor[task.priority]}"></span>
                  <strong>{task.title}</strong>
                  {#if task.due_date}<time class:overdue={!isTaskClosed($activeProject, task) && new Date(task.due_date) < new Date()}>{task.due_date}</time>{/if}
                  {#if task.tags.length}<div class="tags">{#each task.tags.slice(0, 3) as tag}<span>{tag}</span>{/each}</div>{/if}
                </article>
              {/each}
            </div>
          </div>
        {/each}
      </div>
    {:else}
      <div class="empty-state"><strong>{$t('taskGroup.emptyTitle')}</strong><span>{$t('taskGroup.emptyHint')}</span></div>
    {/if}
  </section>
</div>

<style>
  .kanban-shell { flex: 1; min-height: 0; display: flex; overflow: hidden; background: var(--bg); }
  .group-panel { width: 256px; flex: 0 0 256px; display: flex; flex-direction: column; border-right: 1px solid var(--border); background: var(--sidebar); z-index: 20; }
  .group-panel-title { min-height: 49px; padding: 0 14px; display: flex; align-items: center; justify-content: space-between; border-bottom: 1px solid var(--border); font-size: 12px; font-weight: 700; color: var(--text-secondary); }
  .group-list { flex: 1; overflow-y: auto; padding: 8px; }
  .group-row { position: relative; display: flex; align-items: center; }
  .group-row.menu-open { z-index: 4; }
  .group-item { flex: 1; min-width: 0; min-height: 38px; display: grid; grid-template-columns: minmax(0, 1fr) auto auto; align-items: center; gap: 7px; padding: 7px 5px 7px 9px; text-align: left; border-left: 3px solid transparent; color: var(--text-secondary); }
  .group-item:hover { background: var(--surface-raised); color: var(--text); }
  .group-item.active { border-left-color: var(--accent); background: var(--accent-light); color: var(--accent); }
  .group-item.archived { opacity: .48; }
  .group-more { width: 28px; height: 30px; flex: 0 0 28px; display: inline-flex; align-items: center; justify-content: center; color: var(--text-muted); }
  .group-more:hover { color: var(--accent); background: var(--accent-light); }
  .group-menu { position: absolute; top: 34px; right: 0; width: 164px; padding: 5px; display: flex; flex-direction: column; gap: 2px; border: 1px solid var(--border-strong); background: var(--surface-raised); box-shadow: 0 10px 28px rgba(0,0,0,.16); z-index: 10; }
  .group-menu > button { min-height: 30px; padding: 5px 8px; text-align: left; font-size: 11px; color: var(--text-secondary); }
  .group-menu > button:hover:not(:disabled) { color: var(--accent); background: var(--accent-light); }
  .group-menu button:disabled { opacity: .38; cursor: not-allowed; }
  .menu-separator { height: 1px; margin: 3px 0; background: var(--border); }
  .order-actions { display: grid; grid-template-columns: 1fr 1fr; gap: 4px; }
  .order-actions button { height: 28px; display: inline-flex; align-items: center; justify-content: center; border: 1px solid var(--border); color: var(--text-secondary); }
  .group-rename { width: 100%; min-height: 38px; display: flex; gap: 5px; align-items: center; }
  .group-rename input { min-width: 0; flex: 1; padding: 6px 7px; border: 1px solid var(--accent); background: var(--surface-raised); color: var(--text); }
  .group-rename button { width: 28px; height: 28px; display: inline-flex; align-items: center; justify-content: center; color: var(--accent); }
  .group-name { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .default-mark { font-size: 8px; color: var(--jade); }
  .group-count, .col-count { min-width: 23px; text-align: center; font-size: 11px; color: var(--text-muted); }
  .group-add, .task-add, .status-add { display: flex; gap: 6px; }
  .group-add { padding: 10px; border-top: 1px solid var(--border); }
  .group-add input, .task-add input, .status-add input, .status-row input:not(.color-input), .status-row select, .status-add select { min-width: 0; border: 1px solid var(--border); background: var(--surface-raised); color: var(--text); padding: 7px 8px; font-size: 12px; }
  .group-add input, .task-add input { flex: 1; }
  .group-add button, .task-add button, .icon-command { width: 30px; height: 30px; display: inline-flex; align-items: center; justify-content: center; color: var(--text-secondary); }
  .group-add button:hover, .task-add button:hover, .icon-command:hover { color: var(--accent); background: var(--accent-light); }
  .board-area { flex: 1; min-width: 0; display: flex; flex-direction: column; overflow: hidden; position: relative; }
  .board-header { min-height: 49px; padding: 7px 14px; display: flex; align-items: center; gap: 9px; border-bottom: 1px solid var(--border); background: var(--surface-raised); }
  .project-dot, .status-dot, .priority { width: 8px; height: 8px; flex: 0 0 8px; }
  .heading { flex: 1; min-width: 0; display: flex; flex-direction: column; }
  .heading strong { font-size: 14px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .heading span { font-size: 10px; color: var(--text-muted); }
  .header-command { min-height: 30px; padding: 0 9px; border: 1px solid var(--border); color: var(--text-secondary); font-size: 11px; }
  .header-command:hover { border-color: var(--accent); color: var(--accent); }
  .header-command:disabled { opacity: .4; }
  .group-toggle { display: none; width: 32px; height: 32px; }
  .status-manager { margin: 10px 14px 0; padding: 12px; border: 1px solid var(--border-strong); background: var(--surface-raised); box-shadow: 0 8px 24px rgba(0,0,0,.08); z-index: 5; }
  .status-manager-heading { display: flex; align-items: center; justify-content: space-between; margin-bottom: 9px; font-size: 13px; }
  .status-roles { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 10px; padding: 9px; margin-bottom: 9px; background: var(--surface); border: 1px solid var(--border); }
  .status-roles label, .migration-target { display: flex; align-items: center; gap: 8px; min-width: 0; font-size: 11px; color: var(--text-secondary); }
  .status-roles select, .migration-target select { flex: 1; min-width: 0; padding: 6px 7px; border: 1px solid var(--border); background: var(--surface-raised); color: var(--text); }
  .status-list { display: flex; flex-direction: column; gap: 6px; }
  .status-row { display: grid; grid-template-columns: 28px minmax(100px, 1fr) 110px 56px 30px; gap: 6px; align-items: center; }
  .status-order { display: grid; grid-template-columns: 1fr 1fr; }
  .status-order button { height: 28px; display: inline-flex; align-items: center; justify-content: center; color: var(--text-muted); }
  .status-order button:hover:not(:disabled) { color: var(--accent); background: var(--accent-light); }
  .status-order button:disabled, .icon-command:disabled { opacity: .3; cursor: not-allowed; }
  .color-input { width: 28px; height: 28px; padding: 2px; border: 1px solid var(--border); background: transparent; }
  .status-add { margin-top: 9px; }
  .status-add input { flex: 1; }
  .status-add button { padding: 0 12px; background: var(--accent); color: var(--accent-contrast); }
  .manager-confirm { margin-top: 9px; padding: 9px; display: flex; align-items: center; gap: 8px; border: 1px solid var(--amber, #b7791f); background: var(--surface); }
  .manager-confirm > div { flex: 1; min-width: 0; display: flex; flex-direction: column; gap: 3px; }
  .manager-confirm strong { font-size: 11px; }
  .manager-confirm span { font-size: 10px; line-height: 1.45; color: var(--text-muted); }
  .manager-confirm > button { min-height: 29px; padding: 0 9px; border: 1px solid var(--border); font-size: 11px; }
  .primary-command { color: var(--accent); border-color: var(--accent) !important; }
  .danger-command { color: var(--red) !important; }
  .kanban-cols { flex: 1; min-height: 0; display: flex; gap: 12px; overflow: auto; padding: 14px; align-items: stretch; }
  .kanban-col { flex: 0 0 272px; min-width: 248px; display: flex; flex-direction: column; background: var(--surface); border-top: 3px solid var(--border-strong); }
  .col-header { min-height: 40px; display: flex; align-items: center; gap: 8px; padding: 0 10px; font-size: 12px; font-weight: 700; border-bottom: 1px solid var(--border); }
  .col-header > span:nth-child(2) { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .kanban-cards { flex: 1; min-height: 100px; padding: 8px; overflow-y: auto; }
  .kanban-cards.drop-target { background: var(--accent-light); }
  .task-add { margin-bottom: 8px; }
  .task-card { min-height: 72px; margin-bottom: 8px; padding: 10px; display: grid; grid-template-columns: 12px 8px minmax(0,1fr); gap: 7px; align-items: start; border: 1px solid var(--border); border-left: 3px solid var(--border-strong); background: var(--surface-raised); cursor: pointer; position: relative; }
  .task-card:hover { border-color: var(--accent); }
  .task-card.closed { opacity: .65; }
  .task-card.completed strong { text-decoration: line-through; }
  .task-card.drop-before::before { content: ''; position: absolute; left: 0; right: 0; top: -5px; height: 2px; background: var(--accent); }
  .card-handle { color: var(--text-muted); cursor: grab; font-size: 13px; }
  .task-card strong { font-size: 12px; line-height: 1.45; word-break: break-word; }
  .task-card time { grid-column: 3; font-size: 10px; color: var(--text-muted); }
  .task-card time.overdue { color: var(--red); }
  .tags { grid-column: 3; display: flex; gap: 4px; flex-wrap: wrap; }
  .tags span { font-size: 9px; padding: 1px 4px; border: 1px solid var(--border); color: var(--text-muted); }
  .empty-state { flex: 1; display: flex; flex-direction: column; align-items: center; justify-content: center; gap: 7px; color: var(--text-muted); }
  .empty-state strong { color: var(--text); }
  .drawer-scrim { display: none; }
  @media (max-width: 980px) {
    .group-panel { position: absolute; inset: 0 auto 0 0; transform: translateX(-100%); transition: transform .18s ease; box-shadow: 12px 0 30px rgba(0,0,0,.16); }
    .group-panel.drawer-open { transform: translateX(0); }
    .group-toggle { display: inline-flex; align-items: center; justify-content: center; }
    .drawer-scrim { display: block; position: absolute; inset: 0; z-index: 15; background: rgba(0,0,0,.35); }
  }
  @media (max-width: 640px) {
    .header-command { flex: 0 0 auto; padding: 0 7px; }
    .kanban-cols { padding: 10px; }
    .kanban-col { flex-basis: min(84vw, 292px); }
    .status-manager { margin: 8px; }
    .status-manager { overflow-x: auto; }
    .status-roles { grid-template-columns: 1fr; min-width: 430px; }
    .status-row { grid-template-columns: 28px minmax(120px, 1fr) 90px 56px 30px; min-width: 430px; }
    .manager-confirm { min-width: 430px; align-items: stretch; flex-wrap: wrap; }
    .manager-confirm > div { flex-basis: 100%; }
  }
</style>
