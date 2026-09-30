<script lang="ts">
  import {
    activeProject,
    activeTaskGroup,
    activeTaskGroupId,
    createTask,
    duplicateTasksInStatus,
    createTaskGroup,
    createTaskStatus,
    updateTask,
    reorderTask,
    selectTask,
    setActiveTaskGroup,
    setDefaultTaskGroup,
    archiveTaskGroup,
    restoreTaskGroup,
    renameTaskGroup,
    reorderTaskGroups,
    reorderTaskStatuses,
    updateTaskStatusDefinition,
    setGroupCompletionStatus,
  } from '$lib/stores';
  import type { Task } from '$lib/types';
  import { sortable, type DropTarget } from '$lib/actions/sortable';
  import { resolveInsertIndex } from '$lib/utils/reorder-target';
  import { isTaskClosed, isTaskCompleted } from '$lib/utils/task-status';
  import Icon from '$lib/components/shared/Icon.svelte';
  import { t, tf } from '$lib/i18n';
  import { toast } from '$lib/stores/toast';

  let drawerOpen = $state(false);
  let addGroupOpen = $state(false);
  let addStatusOpen = $state(false);
  let groupMenuId = $state<string | null>(null);
  let statusMenuId = $state<string | null>(null);
  let renameGroupId = $state<string | null>(null);
  let renameGroupName = $state('');
  let newGroupName = $state('');
  let newStatusName = $state('');
  let newTaskTitles = $state<Record<string, string>>({});
  let overZone = $state<string | null>(null);
  let overIndex = $state(-1);
  let editingStatusId = $state<string | null>(null);
  let editingStatusName = $state('');

  const priorityColors: Record<Task['priority'], string> = {
    high: '#ef4444',
    medium: '#f59e0b',
    low: '#10b981',
  };

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
    if (createTaskGroup($activeProject.id, newGroupName)) {
      newGroupName = '';
      addGroupOpen = false;
      drawerOpen = false;
    }
  }

  function openAddGroup() {
    newGroupName = '';
    groupMenuId = null;
    addGroupOpen = true;
  }

  function addStatus() {
    if (!$activeProject || !$activeTaskGroup || !newStatusName.trim()) return;
    if (createTaskStatus($activeProject.id, $activeTaskGroup.id, newStatusName, 'active')) {
      newStatusName = '';
      addStatusOpen = false;
    }
  }

  function openAddStatus() {
    newStatusName = '';
    statusMenuId = null;
    addStatusOpen = true;
  }

  function copyStatusTasks(statusId: string) {
    if (!$activeProject || !$activeTaskGroup) return;
    const count = duplicateTasksInStatus($activeProject.id, $activeTaskGroup.id, statusId);
    toast(count > 0
      ? $tf('statusManager.copyTasksSuccess', { n: count })
      : $t('statusManager.copyTasksEmpty'), count > 0 ? 'ok' : 'info');
    statusMenuId = null;
  }

  function setCompletionColumn(statusId: string) {
    if (!$activeProject || !$activeTaskGroup) return;
    setGroupCompletionStatus($activeProject.id, $activeTaskGroup.id, statusId);
    statusMenuId = null;
  }

  function addTask(statusId: string) {
    const title = newTaskTitles[statusId]?.trim();
    if (!$activeProject || !$activeTaskGroup || !title) return;
    createTask({ projectId: $activeProject.id, taskGroupId: $activeTaskGroup.id, statusId, title });
    newTaskTitles = { ...newTaskTitles, [statusId]: '' };
  }

  function beginStatusRename(statusId: string, name: string) {
    editingStatusId = statusId;
    editingStatusName = name;
  }

  function commitStatusRename(statusId: string) {
    if (editingStatusId !== statusId) return;
    const name = editingStatusName.trim();
    const currentName = $activeTaskGroup?.statuses.find(status => status.id === statusId)?.name;
    editingStatusId = null;
    editingStatusName = '';
    if (!$activeProject || !$activeTaskGroup || !name || name === currentName) return;
    updateTaskStatusDefinition($activeProject.id, $activeTaskGroup.id, statusId, { name });
  }

  function cancelStatusRename() {
    editingStatusId = null;
    editingStatusName = '';
  }

  function focusAndSelect(node: HTMLInputElement) {
    requestAnimationFrame(() => {
      node.focus();
      node.select();
    });
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

  function moveId(ids: string[], fromId: string, targetIndex: number): string[] | null {
    const next = [...ids];
    const from = next.indexOf(fromId);
    if (from < 0) return null;
    const [moved] = next.splice(from, 1);
    next.splice(Math.max(0, Math.min(targetIndex, next.length)), 0, moved);
    return next.every((id, index) => id === ids[index]) ? null : next;
  }

  function onDropGroup(_fromIndex: number, target: DropTarget, fromId: string) {
    if (!$activeProject) return;
    const next = moveId(groups.map(group => group.id), fromId, target.index);
    if (next) reorderTaskGroups($activeProject.id, next);
  }

  function onDropStatus(_fromIndex: number, target: DropTarget, fromId: string) {
    if (!$activeProject || !$activeTaskGroup) return;
    const next = moveId(columns.map(column => column.id), fromId, target.index);
    if (next) reorderTaskStatuses($activeProject.id, $activeTaskGroup.id, next);
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

</script>

<div class="kanban-shell">
  {#if drawerOpen}<button class="drawer-scrim" aria-label={$t('taskGroup.close')} onclick={() => drawerOpen = false}></button>{/if}
  <aside class:drawer-open={drawerOpen} class="group-panel">
    <div class="group-panel-title">
      <span>{$t('taskGroup.title')}</span>
      <button class="group-add-trigger" title={$t('taskGroup.add')} aria-label={$t('taskGroup.add')} onclick={openAddGroup}><Icon name="plus" size={15} /></button>
    </div>
    <div
      class="group-list"
      use:sortable={{ itemSelector: '[data-sortable-group]', handle: '.group-item', handleOnly: true, axis: 'y' as const, onStart: () => { groupMenuId = null; }, onDrop: onDropGroup }}
    >
      {#each groups as group (group.id)}
        <div class="group-row" class:menu-open={groupMenuId === group.id} data-sortable-group data-sortable-id={group.id}>
          {#if renameGroupId === group.id}
            <div class="group-rename">
              <input aria-label={$t('taskGroup.name')} bind:value={renameGroupName} onkeydown={event => { if (event.key === 'Enter') saveRename(); if (event.key === 'Escape') renameGroupId = null; }} />
              <button title={$t('taskGroup.saveName')} aria-label={$t('taskGroup.saveName')} onclick={saveRename}><Icon name="check" size={14} /></button>
            </div>
          {:else}
            <button
              class="group-item"
              class:active={$activeTaskGroupId === group.id}
              class:default={$activeProject?.default_task_group_id === group.id}
              class:archived={group.archived}
              onclick={() => !group.archived && openGroup(group.id)}
              title={group.archived ? `${group.name}${$t('taskGroup.archivedSuffix')}` : group.name}
            >
              <span class="group-name">{group.name}</span>
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
            </div>
          {/if}
        </div>
      {/each}
    </div>
  </aside>

  {#if addGroupOpen}
    <!-- svelte-ignore a11y_no_static_element_interactions a11y_click_events_have_key_events -->
    <div class="form-modal-overlay" role="dialog" aria-modal="true" aria-label={$t('taskGroup.add')} tabindex="-1" onclick={() => addGroupOpen = false} onkeydown={event => event.key === 'Escape' && (addGroupOpen = false)}>
      <!-- svelte-ignore a11y_no_noninteractive_element_interactions -->
      <div class="form-modal-card" role="document" onclick={event => event.stopPropagation()} onkeydown={event => { event.stopPropagation(); if (event.key === 'Escape') addGroupOpen = false; }}>
        <div class="form-modal-header">
          <strong>{$t('taskGroup.add')}</strong>
          <button title={$t('sidebar.cancel')} aria-label={$t('sidebar.cancel')} onclick={() => addGroupOpen = false}><Icon name="close" size={15} /></button>
        </div>
        <div class="form-modal-body">
          <label for="new-group-name">{$t('taskGroup.name')}</label>
          <input id="new-group-name" placeholder={$t('taskGroup.newPlaceholder')} bind:value={newGroupName} use:focusAndSelect onkeydown={event => { if (event.key === 'Enter') addGroup(); if (event.key === 'Escape') addGroupOpen = false; }} />
        </div>
        <div class="form-modal-footer">
          <button onclick={() => addGroupOpen = false}>{$t('sidebar.cancel')}</button>
          <button class="confirm" onclick={addGroup} disabled={!newGroupName.trim()}>{$t('taskGroup.add')}</button>
        </div>
      </div>
    </div>
  {/if}

  <section class="board-area">
    {#if $activeProject && $activeTaskGroup}
      <header class="board-header">
        <button class="group-toggle" title={$t('taskGroup.select')} aria-label={$t('taskGroup.select')} onclick={() => drawerOpen = true}><Icon name="menu" size={17} /></button>
        <span class="project-dot" style="background:{$activeProject.color}"></span>
        <div class="heading"><strong>{$activeTaskGroup.name}</strong></div>
        <button class="header-command" onclick={openAddStatus}>{$t('statusManager.new')}</button>
      </header>

      {#if addStatusOpen}
        <!-- svelte-ignore a11y_no_static_element_interactions a11y_click_events_have_key_events -->
        <div class="form-modal-overlay" role="dialog" aria-modal="true" aria-label={$t('statusManager.new')} tabindex="-1" onclick={() => addStatusOpen = false} onkeydown={event => event.key === 'Escape' && (addStatusOpen = false)}>
          <!-- svelte-ignore a11y_no_noninteractive_element_interactions -->
          <div class="form-modal-card" role="document" onclick={event => event.stopPropagation()} onkeydown={event => { event.stopPropagation(); if (event.key === 'Escape') addStatusOpen = false; }}>
            <div class="form-modal-header">
              <strong>{$t('statusManager.new')}</strong>
              <button title={$t('sidebar.cancel')} aria-label={$t('sidebar.cancel')} onclick={() => addStatusOpen = false}><Icon name="close" size={15} /></button>
            </div>
            <div class="form-modal-body">
              <label for="new-status-name">{$t('statusManager.name')}</label>
              <input id="new-status-name" bind:value={newStatusName} use:focusAndSelect onkeydown={event => { if (event.key === 'Enter') addStatus(); if (event.key === 'Escape') addStatusOpen = false; }} />
            </div>
            <div class="form-modal-footer">
              <button onclick={() => addStatusOpen = false}>{$t('sidebar.cancel')}</button>
              <button class="confirm" onclick={addStatus} disabled={!newStatusName.trim()}>{$t('statusManager.confirmAdd')}</button>
            </div>
          </div>
        </div>
      {/if}

      <div class="kanban-cols" use:sortable={{ handleOnly: false, axis: 'free' as const, longPressMs: 240, onOver: target => { if (target.zone) { overZone = target.zone; overIndex = target.index; } }, onDrop: onDropCard, onCancel: () => { overZone = null; overIndex = -1; } }}>
        <div class="kanban-track" use:sortable={{ itemSelector: '[data-sortable-column]', zoneSelector: null, handle: '.col-header', handleOnly: true, axis: 'x' as const, onStart: () => { statusMenuId = null; }, onDrop: onDropStatus }}>
        {#each columns as column (column.id)}
          <div class="kanban-col" class:menu-open={statusMenuId === column.id} data-sortable-column data-sortable-id={column.id}>
            <div class="col-header">
              <span class="status-dot" style="background:{column.color}"></span>
              {#if editingStatusId === column.id}
                <input
                  class="status-name-input"
                  aria-label={$t('statusManager.name')}
                  bind:value={editingStatusName}
                  use:focusAndSelect
                  onblur={() => commitStatusRename(column.id)}
                  onkeydown={event => {
                    if (event.key === 'Enter') {
                      event.preventDefault();
                      commitStatusRename(column.id);
                    } else if (event.key === 'Escape') {
                      event.preventDefault();
                      cancelStatusRename();
                    }
                  }}
                />
              {:else}
                <button class="status-name" title={column.name} ondblclick={() => beginStatusRename(column.id, column.name)}>{column.name}</button>
              {/if}
              <button class="status-more" title={$t('statusManager.actions')} aria-label={`${column.name}: ${$t('statusManager.actions')}`} onclick={() => statusMenuId = statusMenuId === column.id ? null : column.id}><Icon name="more-horizontal" size={15} /></button>
            </div>
            {#if statusMenuId === column.id}
              <div class="status-menu">
                <button onclick={() => copyStatusTasks(column.id)}>{$t('statusManager.copyTasks')}</button>
                <button disabled={$activeTaskGroup.completion_status_id === column.id} onclick={() => setCompletionColumn(column.id)}>{$activeTaskGroup.completion_status_id === column.id ? $t('statusManager.currentCompletion') : $t('statusManager.setCompletion')}</button>
              </div>
            {/if}
            <div class="kanban-cards" class:drop-target={overZone === column.id} data-sortable-zone={column.id}>
              <div class="task-add"><input placeholder={$t('task.addPlaceholder')} value={newTaskTitles[column.id] ?? ''} oninput={event => newTaskTitles = { ...newTaskTitles, [column.id]: (event.target as HTMLInputElement).value }} onkeydown={event => event.key === 'Enter' && addTask(column.id)} /><button title={$t('task.add')} aria-label={$t('task.add')} onclick={() => addTask(column.id)}><Icon name="plus" size={14} /></button></div>
              {#each column.cards as task, index (task.id)}
                <article
                  class="task-card"
                  class:closed={isTaskClosed($activeProject, task)}
                  class:completed={isTaskCompleted($activeProject, task)}
                  class:drop-before={overZone === column.id && overIndex === index}
                  style:border-left-color={priorityColors[task.priority]}
                  data-sortable-item
                  data-sortable-id={task.id}
                  role="button"
                  tabindex="0"
                  onclick={() => selectTask($activeProject!.id, task.id)}
                  onkeydown={event => event.key === 'Enter' && selectTask($activeProject!.id, task.id)}
                >
                  <span class="task-title">{task.title}</span>
                  {#if task.due_date}<time class:overdue={!isTaskClosed($activeProject, task) && new Date(task.due_date) < new Date()}>{task.due_date}</time>{/if}
                  {#if task.tags.length}<div class="tags">{#each task.tags.slice(0, 3) as tag}<span>{tag}</span>{/each}</div>{/if}
                </article>
              {/each}
            </div>
          </div>
        {/each}
        </div>
      </div>
    {:else}
      <div class="empty-state"><strong>{$t('taskGroup.emptyTitle')}</strong><span>{$t('taskGroup.emptyHint')}</span></div>
    {/if}
  </section>
</div>

<style>
  .kanban-shell { flex: 1; min-height: 0; display: flex; overflow: hidden; background: var(--bg); }
  .group-panel { width: 256px; flex: 0 0 256px; display: flex; flex-direction: column; border-right: 1px solid var(--border); background: var(--sidebar); z-index: 20; }
  .group-panel-title { min-height: 49px; padding: 0 8px 0 14px; display: flex; align-items: center; justify-content: space-between; border-bottom: 1px solid var(--border); font-size: 12px; font-weight: 700; color: var(--text-secondary); }
  .group-add-trigger { width: 30px; height: 30px; display: inline-flex; align-items: center; justify-content: center; color: var(--text-muted); }
  .group-add-trigger:hover { color: var(--accent); background: var(--accent-light); }
  .group-list { flex: 1; overflow-y: auto; padding: 8px; }
  .group-row { position: relative; display: flex; align-items: center; }
  .group-row.menu-open { z-index: 4; }
  .group-item { flex: 1; min-width: 0; min-height: 38px; display: flex; align-items: center; padding: 7px 8px 7px 9px; text-align: left; border-left: 3px solid transparent; color: var(--text-secondary); cursor: grab; user-select: none; }
  .group-item:hover { background: var(--surface-raised); color: var(--text); }
  .group-item.default { border-left-color: var(--jade); }
  .group-item.active { background: var(--accent-light); color: var(--accent); }
  .group-item.archived { opacity: .48; }
  .group-more { width: 28px; height: 30px; flex: 0 0 28px; display: inline-flex; align-items: center; justify-content: center; color: var(--text-muted); }
  .group-more:hover { color: var(--accent); background: var(--accent-light); }
  .group-menu { position: absolute; top: 34px; right: 0; width: 164px; padding: 5px; display: flex; flex-direction: column; gap: 2px; border: 1px solid var(--border-strong); background: var(--surface-raised); box-shadow: 0 10px 28px rgba(0,0,0,.16); z-index: 10; }
  .group-menu > button { min-height: 30px; padding: 5px 8px; text-align: left; font-size: 11px; color: var(--text-secondary); }
  .group-menu > button:hover:not(:disabled) { color: var(--accent); background: var(--accent-light); }
  .group-menu button:disabled { opacity: .38; cursor: not-allowed; }
  .group-rename { width: 100%; min-height: 38px; display: flex; gap: 5px; align-items: center; }
  .group-rename input { min-width: 0; flex: 1; padding: 6px 7px; border: 1px solid var(--accent); background: var(--surface-raised); color: var(--text); }
  .group-rename button { width: 28px; height: 28px; display: inline-flex; align-items: center; justify-content: center; color: var(--accent); }
  .group-name { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .task-add { display: flex; gap: 6px; }
  .task-add input { min-width: 0; flex: 1; border: 1px solid var(--border); background: var(--surface-raised); color: var(--text); padding: 7px 8px; font-size: 12px; }
  .task-add button { width: 30px; height: 30px; display: inline-flex; align-items: center; justify-content: center; color: var(--text-secondary); }
  .task-add button:hover { color: var(--accent); background: var(--accent-light); }
  .board-area { flex: 1; min-width: 0; display: flex; flex-direction: column; overflow: hidden; position: relative; }
  .board-header { min-height: 49px; padding: 7px 14px; display: flex; align-items: center; gap: 9px; border-bottom: 1px solid var(--border); background: var(--surface-raised); }
  .project-dot, .status-dot { width: 8px; height: 8px; flex: 0 0 8px; }
  .heading { flex: 1; min-width: 0; }
  .heading strong { font-size: 14px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .header-command { min-height: 30px; padding: 0 9px; border: 1px solid var(--border); color: var(--text-secondary); font-size: 11px; }
  .header-command:hover { border-color: var(--accent); color: var(--accent); }
  .header-command:disabled { opacity: .4; }
  .group-toggle { display: none; width: 32px; height: 32px; }
  .form-modal-overlay { position: fixed; inset: 0; z-index: 1000; display: flex; align-items: center; justify-content: center; padding: 18px; background: rgba(0,0,0,.45); }
  .form-modal-card { width: 360px; max-width: 100%; border: 1px solid var(--border-strong); border-radius: 6px; background: var(--surface-raised); box-shadow: var(--shadow-float); }
  .form-modal-header { min-height: 48px; padding: 0 16px; display: flex; align-items: center; justify-content: space-between; border-bottom: 1px solid var(--border); }
  .form-modal-header strong { font-size: 14px; }
  .form-modal-header button { width: 28px; height: 28px; display: inline-flex; align-items: center; justify-content: center; color: var(--text-muted); }
  .form-modal-header button:hover { color: var(--text); background: var(--surface); }
  .form-modal-body { padding: 18px 16px; display: flex; flex-direction: column; gap: 7px; }
  .form-modal-body label { font-size: 11px; font-weight: 700; color: var(--text-secondary); }
  .form-modal-body input { width: 100%; height: 34px; padding: 7px 9px; border: 1px solid var(--border); background: var(--surface); color: var(--text); outline: none; }
  .form-modal-body input:focus { border-color: var(--accent); }
  .form-modal-footer { padding: 12px 16px; display: flex; justify-content: flex-end; gap: 8px; border-top: 1px solid var(--border); }
  .form-modal-footer button { min-width: 72px; min-height: 30px; padding: 0 12px; border: 1px solid var(--border); color: var(--text-secondary); }
  .form-modal-footer .confirm { border-color: var(--accent); background: var(--accent); color: var(--accent-contrast); }
  .form-modal-footer button:disabled { opacity: .42; cursor: not-allowed; }
  .kanban-cols { flex: 1; min-height: 0; overflow: auto; padding: 14px; }
  .kanban-track { min-width: max-content; min-height: 100%; display: flex; gap: 12px; align-items: stretch; }
  .kanban-col { position: relative; flex: 0 0 272px; min-width: 248px; display: flex; flex-direction: column; background: var(--surface); border-top: 3px solid var(--border-strong); }
  .kanban-col.menu-open { z-index: 6; }
  .col-header { min-height: 40px; display: flex; align-items: center; gap: 8px; padding: 0 6px 0 10px; font-size: 12px; font-weight: 700; border-bottom: 1px solid var(--border); cursor: grab; user-select: none; }
  .status-name { flex: 1; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; text-align: left; font-weight: 700; }
  .status-name-input { flex: 1; min-width: 0; height: 28px; padding: 4px 6px; border: 1px solid var(--accent); background: var(--surface-raised); color: var(--text); font-size: 12px; font-weight: 700; outline: none; }
  .status-more { width: 28px; height: 28px; flex: 0 0 28px; display: inline-flex; align-items: center; justify-content: center; color: var(--text-muted); }
  .status-more:hover { color: var(--accent); background: var(--accent-light); }
  .status-menu { position: absolute; top: 37px; right: 6px; z-index: 8; width: 164px; padding: 5px; display: flex; flex-direction: column; gap: 2px; border: 1px solid var(--border-strong); background: var(--surface-raised); box-shadow: 0 10px 28px rgba(0,0,0,.16); }
  .status-menu button { min-height: 30px; padding: 5px 8px; text-align: left; font-size: 11px; color: var(--text-secondary); }
  .status-menu button:hover:not(:disabled) { color: var(--accent); background: var(--accent-light); }
  .status-menu button:disabled { opacity: .42; cursor: not-allowed; }
  .kanban-cards { flex: 1; min-height: 100px; padding: 8px; overflow-y: auto; }
  .kanban-cards.drop-target { background: var(--accent-light); }
  .task-add { margin-bottom: 8px; }
  .task-card { min-height: 72px; margin-bottom: 8px; padding: 10px; display: grid; grid-template-columns: minmax(0,1fr); gap: 7px; align-items: start; border: 1px solid var(--border); border-left: 3px solid var(--border-strong); background: var(--surface-raised); cursor: pointer; position: relative; }
  .task-card:hover { border-color: var(--accent); }
  .task-card.closed { opacity: .65; }
  .task-card.completed .task-title { text-decoration: line-through; }
  .task-card.drop-before::before { content: ''; position: absolute; left: 0; right: 0; top: -5px; height: 2px; background: var(--accent); }
  .task-title { display: -webkit-box; max-height: 4.35em; overflow: hidden; font-size: 12px; font-weight: 400; line-height: 1.45; word-break: break-word; line-clamp: 3; -webkit-box-orient: vertical; -webkit-line-clamp: 3; }
  .task-card time { font-size: 10px; color: var(--text-muted); }
  .task-card time.overdue { color: var(--red); }
  .tags { display: flex; gap: 4px; flex-wrap: wrap; }
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
    .form-modal-overlay { padding: 12px; }
  }
</style>
