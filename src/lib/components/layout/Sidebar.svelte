<script lang="ts">
/**
 * Sidebar — 应用侧边栏组件
 *
 * 提供项目管理和导航的核心界面，包含：
 * - 项目列表（选择、创建、删除）
 * - 新建项目表单（名称、描述、颜色和存储方式）
 * - 导出功能（.pm / Markdown / HTML）
 * - 导入 .pm 文件和加载 Demo
 * - 状态筛选（全部/待办/进行中/已完成）
 * - 教程入口
 *
 * @example
 * <Sidebar />
 */

  import { get } from 'svelte/store';
  import { projects, activeProject, activeProjectId, createProject, deleteProject, downloadPmFile, importProject, exportMarkdownFile, exportHtmlFile, exportHtmlAsFile, exportPdfFile, pendingAction, showSidebar, showSettings, updateProject, reorderProject } from '$lib/stores';
  import { sortable, type DropTarget } from '$lib/actions/sortable';
  import { resolveInsertIndex } from '$lib/utils/reorder-target';
  import { pickFile } from '$lib/utils/pick-file';
  import type { Project, ProjectStorage } from '$lib/types';
  import { pickDirectory } from '$lib/repositories';
  import { generateReport, saveReport } from '$lib/ai';
  import Icon from '$lib/components/shared/Icon.svelte';
  import { t, tf } from '$lib/i18n';
  import { animationLevel } from '$lib/stores/animation';
  import { fade, scale } from 'svelte/transition';
  import ColorPicker from '$lib/components/shared/ColorPicker.svelte';
  import { colorScheme } from '$lib/stores/color';
  import { showFlowchart } from '$lib/stores';
  import { ownerMarks } from '$lib/stores/storage-health';
  import { getDeviceId } from '$lib/utils/device';
  import { buildProjectAiTaskContext } from '$lib/utils/ai-context';

  /**
   * 本项目是否由「别的设备」管理本地文件夹；是则返回对方的可读名。
   *
   * 仅当 storage 为本地文件夹、且 ownerDeviceId 不是本机时返回；
   * 无 ownerDeviceId（尚未写入）或就是本机 → null。
   */
  function ownerBadgeName(project: { id: string; storage?: ProjectStorage }): string | null {
    const s = project.storage;
    if (!s || s.type !== 'local' || !s.ownerDeviceId) return null;
    if (s.ownerDeviceId === getDeviceId()) return null;
    return s.ownerDeviceName || $ownerMarks[project.id]?.ownerName || s.ownerDeviceId.slice(0, 8);
  }

  let showNew = $state(false);
  let newName = $state('');
  let newDesc = $state('');
  let newColor = $state('#a33b32');
  let useLocalCopy = $state(false);
  let storagePath = $state('');
  let newStartDate = $state('');
  let newEndDate = $state('');
  let confirmDeleteId = $state<string | null>(null);
  let showExportMenu = $state(false);
  let projectSearch = $state('');
  let showProjectSearch = $state(false);
  let projectSearchInput = $state<HTMLInputElement | null>(null);
  let editingProjectId = $state<string | null>(null);
  let editName = $state('');
  let editDescription = $state('');
  let editColor = $state('#a33b32');
  let editStartDate = $state('');
  let editEndDate = $state('');
  let editDateInvalid = $derived(Boolean(editStartDate && editEndDate && editEndDate < editStartDate));

  /** 搜索过滤后的项目列表（渲染顺序与拖拽下标都基于它） */
  let filteredProjects = $derived.by(() => {
    const q = projectSearch.trim().toLowerCase();
    return q ? $projects.filter(p => p.name.toLowerCase().includes(q)) : $projects;
  });

  /**
   * 项目拖拽落位
   *
   * `fromDomIndex` 是**可见列表**里的下标；搜索过滤时与 `$projects` 的全量下标不同，
   * 所以用「目标位置后面那一个项目的 id」当锚点，再换算回全量下标。
   */
  function onDropProject(fromDomIndex: number, target: DropTarget, fromId: string) {
    const visible = filteredProjects;
    const movedId = fromId || visible[fromDomIndex]?.id;
    if (!movedId) return;
    const all = $projects;
    const fromAll = all.findIndex(p => p.id === movedId);
    if (fromAll === -1) return;
    const toAll = resolveInsertIndex(all, visible, movedId, target.index);
    if (toAll < 0 || toAll === fromAll) return;
    reorderProject(fromAll, toAll);
  }

  // 监听命令面板触发的待执行动作
  $effect(() => {
    const action = $pendingAction;
    if (!action) return;
    pendingAction.set(null);
    if (action === 'new-project') {
      showNew = true;
    } else if (action === 'import-project') {
      handleImport();
    }
  });

  function addProject() {
    console.log('[Modal] addProject called, name:', newName, 'color:', newColor, 'useLocalCopy:', useLocalCopy);
    if (!newName.trim()) {
      console.warn('[Modal] Project name is empty, aborting');
      return;
    }
    let storage: ProjectStorage | undefined;
    if (useLocalCopy && !storagePath) return;
    if (useLocalCopy && storagePath) {
      storage = { type: 'local', path: storagePath };
      console.log('[Modal] Storage: local folder ->', storagePath);
    } else {
      console.log('[Modal] Storage: app internal');
    }
    createProject(newName.trim(), newDesc.trim(), newColor, 'default', storage, newStartDate || undefined, newEndDate || undefined);
    const createdId = get(activeProjectId);
    console.log('[Modal] Project created with ID:', createdId);
    newName = ''; newDesc = ''; storagePath = ''; useLocalCopy = false;
    showNew = false;
    console.log('[Modal] Modal closed, state reset');
  }

  async function handlePickFolder() {
    const dir = await pickDirectory();
    if (dir) storagePath = dir;
  }

  function handleDelete(e: MouseEvent, id: string) {
    e.stopPropagation();
    if (confirmDeleteId === id) { deleteProject(id); confirmDeleteId = null; }
    else { confirmDeleteId = id; setTimeout(() => confirmDeleteId = null, 3000); }
  }

  function openProjectSettings(event: MouseEvent, project: Project) {
    event.stopPropagation();
    editingProjectId = project.id;
    editName = project.name;
    editDescription = project.description ?? '';
    editColor = project.color;
    editStartDate = project.start_date ?? '';
    editEndDate = project.end_date ?? '';
  }

  function closeProjectSettings() {
    editingProjectId = null;
  }

  function saveProjectSettings() {
    if (!editingProjectId || !editName.trim() || editDateInvalid) return;
    updateProject(editingProjectId, {
      name: editName.trim(),
      description: editDescription.trim(),
      color: editColor,
      start_date: editStartDate || undefined,
      end_date: editEndDate || undefined,
    });
    editingProjectId = null;
  }

  async function doExport(fmt: 'pm' | 'md' | 'html' | 'html-save' | 'pdf' | 'report') {
    if (!$activeProjectId) return;
    showExportMenu = false;
    if (fmt === 'pm') downloadPmFile($activeProjectId);
    else if (fmt === 'md') exportMarkdownFile($activeProjectId);
    else if (fmt === 'html') await exportHtmlFile($activeProjectId);
    else if (fmt === 'html-save') await exportHtmlAsFile($activeProjectId);
    else if (fmt === 'pdf') await exportPdfFile($activeProjectId);
    else if (fmt === 'report') await handleGenerateReport();
  }

  async function handleGenerateReport() {
    const proj = get(activeProject);
    if (!proj) return;
    try {
      const tasksText = buildProjectAiTaskContext(proj);
      const milestonesText = proj.milestones?.map(m => `- ${m.title}${m.date ? ' (' + m.date + ')' : ''}`).join('\n') || get(t)('common.none');
      const html = await generateReport({
        name: proj.name,
        description: proj.description || '',
        created_at: proj.created_at,
        updated_at: proj.updated_at,
        tasks: tasksText,
        milestones: milestonesText,
        taskCount: String(proj.tasks.length),
      });
      const path = await saveReport(proj.name, html);
      console.log('[Sidebar] Report saved to:', path);
    } catch (e: any) {
      console.error('[Sidebar] Report generation failed:', e);
    }
  }

  function handleExportClick(e: MouseEvent) {
    e.stopPropagation();
    showExportMenu = !showExportMenu;
  }

  function closeExportMenu() {
    showExportMenu = false;
  }

  $effect(() => {
    if (!showExportMenu) return;
    const handler = () => showExportMenu = false;
    document.addEventListener('click', handler);
    return () => document.removeEventListener('click', handler);
  });

  async function handleImport() {
    const file = await pickFile('.pm,.json');
    if (!file) return;
    const text = await file.text();
    importProject(text, file.name);
  }

  function toggleProjectSearch() {
    showProjectSearch = !showProjectSearch;
    if (showProjectSearch) requestAnimationFrame(() => projectSearchInput?.focus());
  }

  function closeProjectSearch() {
    projectSearch = '';
    showProjectSearch = false;
  }


</script>

<aside class="sidebar" class:open={$showSidebar}>
  <!-- 仪表盘是进入应用后的默认一级入口 -->
  <div class="dashboard-header" class:active={!$activeProjectId}>
    <button class="dashboard-entry" onclick={() => { activeProjectId.set(null); showSidebar.set(false); }}>
      <span class="sidebar-heading">
        <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
          <rect x="3" y="3" width="7" height="7" rx="1"/>
          <rect x="14" y="3" width="7" height="7" rx="1"/>
          <rect x="3" y="14" width="7" height="7" rx="1"/>
          <rect x="14" y="14" width="7" height="7" rx="1"/>
        </svg>
        <span class="sidebar-title">{$t('view.dashboard')}</span>
      </span>
    </button>
    <button class="sidebar-close-btn-mobile" onclick={() => showSidebar.set(false)} aria-label="关闭菜单">✕</button>
  </div>

  <div class="sidebar-header">
    <span class="sidebar-heading">
      <span class="sidebar-heading-icon" aria-hidden="true"><Icon name="folder" size={15} /></span>
      <span class="sidebar-title">{$t('sidebar.projectList')}</span>
    </span>
    <div class="sidebar-header-actions">
      <button
        class="header-icon-btn"
        class:active={showProjectSearch}
        title={$t('sidebar.searchProject')}
        aria-label={$t('sidebar.searchProject')}
        aria-expanded={showProjectSearch}
        onclick={toggleProjectSearch}
      ><Icon name="search" size={15} /></button>
      <button
        class="header-icon-btn new-project-btn"
        title={$t('sidebar.newProject')}
        aria-label={$t('sidebar.newProject')}
        onclick={() => { console.log('[Modal] Open button clicked'); showNew = true; }}
      ><Icon name="plus" size={16} /></button>
    </div>
  </div>

  {#if showProjectSearch}
    <div class="project-search" transition:fade={{ duration: $animationLevel === 'none' ? 0 : 100 }}>
      <Icon name="search" size={14} />
      <input
        class="search-input"
        type="search"
        placeholder={$t('sidebar.searchProject')}
        bind:this={projectSearchInput}
        bind:value={projectSearch}
        onkeydown={event => event.key === 'Escape' && closeProjectSearch()}
      />
      <button class="search-close" aria-label={$t('sidebar.cancel')} onclick={closeProjectSearch}><Icon name="close" size={13} /></button>
    </div>
  {/if}

  <div
    class="project-list"
    use:sortable={{
      handle: '.drag-handle',
      axis: 'y' as const,
      onDrop: onDropProject
    }}
  >
    {#each filteredProjects as project (project.id)}
      <!-- svelte-ignore a11y_click_events_have_key_events a11y_no_static_element_interactions -->
      <div
        class="project-item"
        class:active={$activeProjectId === project.id}
        role="button"
        tabindex="0"
        data-sortable-item
        data-sortable-id={project.id}
        onclick={() => { activeProjectId.set(project.id); showSidebar.set(false); }}
        onkeydown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); activeProjectId.set(project.id); showSidebar.set(false); } }}
      >
        <!-- 拖动抓手（触摸端必须按住它才能拖，避免劫持页面滚动） -->
        <span class="drag-handle" title={$t('drag.handle')} aria-hidden="true">
          <svg width="10" height="10" viewBox="0 0 24 24" fill="currentColor">
            <circle cx="9" cy="6" r="1.6"/><circle cx="15" cy="6" r="1.6"/>
            <circle cx="9" cy="12" r="1.6"/><circle cx="15" cy="12" r="1.6"/>
            <circle cx="9" cy="18" r="1.6"/><circle cx="15" cy="18" r="1.6"/>
          </svg>
        </span>
        <span class="dot" style="background:{project.color}"></span>
        <span class="project-name" title={project.name}>{project.name}</span>
        {#if ownerBadgeName(project)}
          <span class="owner-badge" title={$t('storage.ownerBadgeHint')} aria-label={$tf('storage.ownerBadge', { name: ownerBadgeName(project) ?? '' })}>🔒</span>
        {/if}
        <button class="project-settings" title={$t('sidebar.editProject')} aria-label={`${project.name}: ${$t('sidebar.editProject')}`} onclick={(event) => openProjectSettings(event, project)}><Icon name="pencil" size={13} /></button>
        <button class="delete-btn" class:confirm={confirmDeleteId === project.id} title={$t('sidebar.deleteProject')} aria-label={`${project.name}: ${$t('sidebar.deleteProject')}`} onclick={(e) => handleDelete(e, project.id)}>
          {#if confirmDeleteId === project.id}{$t('sidebar.confirm')}{:else}<Icon name="trash" size={13} />{/if}
        </button>
      </div>
    {/each}
    {#if filteredProjects.length === 0}
      <div class="project-empty">{$t('sidebar.noProjectMatch')}</div>
    {/if}
  </div>

  <div class="sidebar-actions">
    <!-- svelte-ignore a11y_no_static_element_interactions -->
    <div class="export-wrap">
      <button class="action-btn" onclick={handleExportClick} disabled={!$activeProjectId}>{$t('sidebar.export')}</button>
      {#if showExportMenu}
        <div class="export-menu" onclick={e => e.stopPropagation()}>
          <button class="export-item" onclick={() => doExport('pm')}>.pm (JSON)</button>
          <button class="export-item" onclick={() => doExport('md')}>Markdown</button>
          <div class="export-sep"></div>
          <button class="export-item" onclick={() => doExport('html')}><Icon name="chart" size={13} /> {$t('export.report')}</button>
          <button class="export-item" onclick={() => doExport('html-save')}><Icon name="file" size={13} /> {$t('export.html')}</button>
          <button class="export-item" onclick={() => doExport('pdf')}><Icon name="printer" size={13} /> {$t('export.pdf')}</button>
          <div class="export-sep"></div>
          <button class="export-item" onclick={() => doExport('report')}><Icon name="sparkles" size={13} /> {$t('export.ai')}</button>
        </div>
      {/if}
    </div>
    <button class="action-btn" onclick={handleImport}>{$t('sidebar.import')}</button>
  </div>

  <!-- 流程图独立工具入口 -->
  <div class="flowchart-entry">
    <button class="flowchart-btn" onclick={() => { showFlowchart.set(true); showSidebar.set(false); }}>
      <span class="flowchart-btn-icon">◇</span>
      <span>流程图</span>
    </button>
  </div>
</aside>
{#if $showSidebar}
  <!-- svelte-ignore a11y_click_events_have_key_events a11y_no_static_element_interactions a11y_interactive_supports_focus -->
  <div class="sidebar-overlay" onclick={() => showSidebar.set(false)} role="dialog" tabindex="-1"></div>
{/if}

<!-- 新建项目弹窗 -->
{#if showNew}
  <!-- svelte-ignore a11y_no_static_element_interactions a11y_click_events_have_key_events a11y_interactive_supports_focus -->
  <div
    class="modal-overlay"
    onclick={() => { console.log('[Modal] Overlay clicked, closing'); showNew = false; }}
    onkeydown={e => { if (e.key === 'Escape') { console.log('[Modal] Escape pressed, closing'); showNew = false; } }}
    role="dialog"
    aria-modal="true"
    aria-label={$t('sidebar.newProject')}
    transition:fade={{ duration: $animationLevel === 'rich' ? 200 : ($animationLevel === 'none' ? 0 : 120) }}
  >
    <!-- svelte-ignore a11y_no_static_element_interactions a11y_click_events_have_key_events -->
    <div
      class="modal-card"
      onclick={e => e.stopPropagation()}
      transition:scale={{ duration: $animationLevel === 'rich' ? 200 : ($animationLevel === 'none' ? 0 : 120), start: 0.95 }}
    >
      <div class="modal-header">
        <span class="modal-title">{$t('sidebar.newProject')}</span>
        <button type="button" class="modal-close" onclick={() => { console.log('[Modal] Close button clicked'); showNew = false; }}>
          <Icon name="close" size={16} />
        </button>
      </div>

      <div class="modal-body">
        <label class="field-label" for="new-project-name">{$t('sidebar.projectName')}</label>
        <input id="new-project-name" class="input" placeholder={$t('sidebar.projectName')} bind:value={newName} oninput={() => console.log('[Modal] name changed:', newName)} />

        <label class="field-label" for="new-project-desc">{$t('sidebar.description')}</label>
        <input id="new-project-desc" class="input" placeholder={$t('sidebar.description')} bind:value={newDesc} oninput={() => console.log('[Modal] desc changed')} />

        <label class="field-label">{$t('sidebar.color')}</label>
        <div class="color-row">
          <ColorPicker value={newColor} size="sm" showName onselect={(c) => { newColor = c; }} />
        </div>

        <!-- 项目时间范围 -->
        <div class="field-label">{$t('sidebar.projectDateRange')}</div>
        <div class="date-range">
          <div class="date-field">
            <span class="date-label">{$t('sidebar.startDate')}</span>
            <input class="input" type="date" bind:value={newStartDate} />
          </div>
          <span class="date-sep">→</span>
          <div class="date-field">
            <span class="date-label">{$t('sidebar.endDate')}</span>
            <input class="input" type="date" bind:value={newEndDate} />
          </div>
        </div>

        <label class="field-label">{$t('sidebar.storageTitle')}</label>
        <div class="storage-section">
          <div class="storage-fact"><Icon name="check" size={13} /> {$t('sidebar.storageInternalAlways')}</div>
          <label class="storage-option" class:active={useLocalCopy}>
            <input type="checkbox" bind:checked={useLocalCopy} />
            <span>{$t('sidebar.storageLocalCopy')}</span>
          </label>
          {#if useLocalCopy}
            <div class="storage-path">
              <span class="path-text">{storagePath || $t('sidebar.storageNoPath')}</span>
              <button type="button" class="btn btn-ghost small" onclick={handlePickFolder}>{$t('sidebar.storageBrowse')}</button>
            </div>
          {/if}
          <div class="storage-hint">{$t('sidebar.storageCloudHint')}</div>
        </div>
      </div>

      <div class="modal-footer">
        <button type="button" class="btn btn-ghost" onclick={() => showNew = false}>{$t('sidebar.cancel')}</button>
        <button type="button" class="btn btn-primary" disabled={!newName.trim() || (useLocalCopy && !storagePath)} onclick={addProject}>{$t('sidebar.create')}</button>
      </div>
    </div>
  </div>
{/if}

<!-- 项目设置弹窗：承接从项目概览页迁出的基础信息编辑能力 -->
{#if editingProjectId}
  <!-- svelte-ignore a11y_no_static_element_interactions a11y_click_events_have_key_events -->
  <div class="modal-overlay" onclick={closeProjectSettings} onkeydown={event => event.key === 'Escape' && closeProjectSettings()} role="dialog" aria-modal="true" aria-label={$t('sidebar.projectSettings')} tabindex="-1" transition:fade={{ duration: $animationLevel === 'rich' ? 200 : 120 }}>
    <!-- svelte-ignore a11y_no_static_element_interactions a11y_click_events_have_key_events -->
    <div class="modal-card" onclick={event => event.stopPropagation()} transition:scale={{ duration: $animationLevel === 'rich' ? 200 : 120, start: 0.96 }}>
      <div class="modal-header">
        <span class="modal-title">{$t('sidebar.projectSettings')}</span>
        <button type="button" class="modal-close" aria-label={$t('sidebar.cancel')} onclick={closeProjectSettings}><Icon name="close" size={16} /></button>
      </div>
      <div class="modal-body">
        <label class="field-label" for="edit-project-name">{$t('sidebar.projectName')}</label>
        <input id="edit-project-name" class="input" bind:value={editName} />

        <label class="field-label" for="edit-project-description">{$t('sidebar.projectDescription')}</label>
        <textarea id="edit-project-description" class="input project-description" rows="4" bind:value={editDescription}></textarea>

        <div class="field-label">{$t('sidebar.projectColor')}</div>
        <div class="color-row"><ColorPicker value={editColor} size="sm" showName onselect={color => editColor = color} /></div>

        <div class="field-label">{$t('sidebar.projectDateRange')}</div>
        <div class="date-range">
          <label class="date-field"><span class="date-label">{$t('sidebar.startDate')}</span><input class="input" type="date" bind:value={editStartDate} /></label>
          <span class="date-sep">→</span>
          <label class="date-field"><span class="date-label">{$t('sidebar.endDate')}</span><input class="input" type="date" bind:value={editEndDate} /></label>
        </div>
        {#if editDateInvalid}<div class="field-error">{$t('sidebar.endDateInvalid')}</div>{/if}
      </div>
      <div class="modal-footer">
        <button type="button" class="btn btn-ghost" onclick={closeProjectSettings}>{$t('sidebar.cancel')}</button>
        <button type="button" class="btn btn-primary" disabled={!editName.trim() || editDateInvalid} onclick={saveProjectSettings}>{$t('sidebar.save')}</button>
      </div>
    </div>
  </div>
{/if}

<style>
  .sidebar { width: 264px; height: 100%; display: flex; flex-direction: column; background: var(--sidebar); border-right: 1px solid var(--border-strong); flex-shrink: 0; }
  .dashboard-header, .sidebar-header { display: flex; align-items: center; justify-content: space-between; min-height: 52px; color: var(--text); border-bottom: 3px double var(--border); }
  .sidebar-header { padding: 9px 11px 9px 15px; }
  .dashboard-header { padding-right: 11px; }
  .dashboard-header.active { background: var(--surface-raised); color: var(--accent); box-shadow: inset 3px 0 0 var(--accent); }
  .sidebar-heading { display: flex; align-items: center; gap: 9px; min-width: 0; }
  .sidebar-heading-icon { display: inline-flex; align-items: center; justify-content: center; color: var(--accent); flex-shrink: 0; }
  .sidebar-title { font-family: var(--font-serif); font-size: 15px; font-weight: 700; white-space: nowrap; }
  .dashboard-entry {
    flex: 1;
    display: flex;
    align-items: center;
    align-self: stretch;
    min-width: 0;
    padding: 10px 15px;
    color: inherit;
    background: transparent;
    cursor: pointer;
    text-align: left;
    transition: color 0.12s, background 0.12s;
  }
  .dashboard-entry:hover { background: var(--surface); color: var(--accent); }
  .dashboard-entry svg { flex-shrink: 0; color: var(--accent); }
  .sidebar-header-actions { display: flex; align-items: center; gap: 4px; }
  .header-icon-btn { width: 28px; height: 28px; display: inline-flex; align-items: center; justify-content: center; border-radius: 3px; color: var(--text-muted); border: 1px solid transparent; }
  .header-icon-btn:hover, .header-icon-btn.active { color: var(--accent); background: var(--accent-light); border-color: var(--border); }
  .input { width: 100%; padding: 8px 10px; font-size: 13px; border-radius: 4px; background: var(--surface-raised); border: 1px solid var(--border); color: var(--text); }
  .input:focus { border-color: var(--accent); outline: none; box-shadow: 0 0 0 2px var(--accent-light); }
  select.input { cursor: pointer; }
  .date-range { display: flex; align-items: center; gap: 8px; }
  .date-field { flex: 1; display: flex; flex-direction: column; gap: 2px; }
  .date-label { font-size: 10px; color: var(--text-muted); }
  .date-sep { font-size: 14px; color: var(--text-muted); margin-top: 14px; }
  .color-row { display: flex; gap: 6px; flex-wrap: wrap; }
  .storage-section { display: flex; flex-direction: column; gap: 6px; padding: 8px; background: var(--surface-raised); border-radius: 4px; border: 1px solid var(--border); }
  .storage-option { flex: 1; display: flex; align-items: center; gap: 4px; padding: 4px 6px; font-size: 12px; border-radius: 4px; background: var(--surface); color: var(--text-secondary); cursor: pointer; border: 1px solid transparent; }
  .storage-option.active { border-color: var(--accent); color: var(--accent); }
  .storage-option input { accent-color: var(--accent); }
  .storage-fact { display: flex; align-items: center; gap: 6px; padding: 4px 6px; font-size: 12px; color: var(--jade); }
  .storage-hint { padding: 2px 6px; font-size: 10px; line-height: 1.5; color: var(--text-muted); }
  .storage-path { display: flex; align-items: center; gap: 4px; }
  .path-text { flex: 1; font-size: 11px; color: var(--text-muted); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .btn.small { padding: 3px 10px; font-size: 11px; }
  .btn { padding: 8px 16px; font-size: 13px; border-radius: 4px; transition: background 0.15s, border-color 0.15s, color 0.15s; border: 1px solid transparent; cursor: pointer; }
  .btn-primary { background: var(--accent); color: var(--accent-contrast); }
  .btn-primary:hover { background: var(--accent-hover); }
  .btn-ghost { background: var(--surface); color: var(--text-secondary); border-color: var(--border); }
  .btn-ghost:hover { border-color: var(--border-strong); color: var(--text); }
  /* 新建项目弹窗 */
  .modal-overlay { position: fixed; inset: 0; z-index: 1000; display: flex; align-items: center; justify-content: center; background: rgba(0,0,0,0.45); }
  .modal-card { width: 420px; max-width: 90vw; max-height: 85vh; overflow-y: auto; background: var(--surface-raised); border: 1px solid var(--border-strong); border-radius: 6px; box-shadow: var(--shadow-float); display: flex; flex-direction: column; }
  .modal-header { display: flex; align-items: center; justify-content: space-between; padding: 16px 20px; border-bottom: 1px solid var(--border); box-shadow: inset 0 -3px 0 var(--bg); }
  .modal-title { font-family: var(--font-serif); font-size: 16px; font-weight: 700; }
  .modal-close { padding: 4px; border-radius: 3px; background: none; border: none; color: var(--text-muted); cursor: pointer; }
  .modal-close:hover { background: var(--surface); color: var(--text); }
  .modal-body { display: flex; flex-direction: column; gap: 12px; padding: 20px; }
  .field-label { font-size: 12px; font-weight: 500; color: var(--text-secondary); margin-bottom: -8px; }
  .modal-footer { display: flex; gap: 8px; justify-content: flex-end; padding: 14px 20px; border-top: 1px solid var(--border); }
  .modal-footer .btn { min-width: 90px; }
  .project-list { flex: 1; min-height: 0; overflow-y: auto; padding: 2px 8px 10px; }
  .project-item { position: relative; display: flex; align-items: center; gap: 10px; width: 100%; min-height: 38px; padding: 8px 11px; border-radius: 2px; font-size: 13px; color: var(--text); border: 1px solid transparent; transition: background 0.15s, border-color 0.15s, color 0.15s; text-align: left; cursor: pointer; }
  .project-item:hover { background: var(--surface); }
  .project-item.active { background: var(--surface-raised); color: var(--accent); border-color: var(--border); box-shadow: inset 3px 0 0 var(--accent); }
  .project-empty { padding: 12px; font-size: 12px; color: var(--text-muted); text-align: center; }
  .drag-handle { display: flex; align-items: center; justify-content: center; width: 14px; flex-shrink: 0; color: var(--text-muted); opacity: 0; transition: opacity 0.15s; touch-action: none; -webkit-user-select: none; user-select: none; -webkit-touch-callout: none; cursor: grab; }
  .project-item:hover .drag-handle { opacity: 0.7; }
  .drag-handle:active { cursor: grabbing; }
  .dot { width: 8px; height: 8px; border-radius: 50%; flex-shrink: 0; box-shadow: 0 0 0 2px var(--surface-raised); }
  .project-name { flex: 1; max-width: 10em; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-weight: 500; }
  .owner-badge { flex-shrink: 0; font-size: 11px; line-height: 1; opacity: 0.75; cursor: help; }
  .project-settings { position: absolute; right: 35px; width: 23px; height: 23px; display: inline-flex; align-items: center; justify-content: center; color: var(--text-muted); opacity: 0; }
  .project-item:hover .project-settings, .project-settings:focus-visible { opacity: 1; }
  .project-settings:hover { color: var(--accent); background: var(--accent-light); }
  .delete-btn { position: absolute; right: 8px; min-width: 23px; height: 23px; display: inline-flex; align-items: center; justify-content: center; padding: 0 4px; font-size: 11px; color: var(--text-muted); opacity: 0; transition: opacity 0.15s; }
  .project-item:hover .delete-btn, .delete-btn:focus-visible, .delete-btn.confirm { opacity: 1; }
  .delete-btn:hover { color: var(--red); }
  .delete-btn.confirm { color: var(--red); background: rgba(182,59,52,0.1); }
  .sidebar-actions { display: flex; gap: 6px; padding: 10px 12px 9px; border-top: 3px double var(--border); flex-shrink: 0; }
  .action-btn { flex: 1; padding: 6px; font-size: 11px; border-radius: 4px; background: var(--surface); color: var(--text-secondary); border: 1px solid var(--border); text-align: center; }
  .action-btn:hover:not(:disabled) { border-color: var(--accent); color: var(--accent); }
  .action-btn:disabled { opacity: 0.4; cursor: not-allowed; }
  .export-wrap { position: relative; flex: 1; }
  .export-wrap .action-btn { width: 100%; }
  .export-menu { position: absolute; bottom: 100%; left: 0; margin-bottom: 2px; background: var(--surface-raised); border: 1px solid var(--border-strong); border-radius: 4px; box-shadow: var(--shadow-soft); z-index: 10; min-width: 120px; padding: 4px 0; }
  .export-item { display: flex; align-items: center; gap: 6px; width: 100%; padding: 7px 12px; font-size: 12px; text-align: left; color: var(--text-secondary); }
  .export-item:hover { background: var(--surface); color: var(--accent); }
  .export-sep { height: 1px; margin: 4px 0; background: var(--border); }
  .project-search { display: flex; align-items: center; gap: 7px; margin: 8px 12px 6px; padding: 0 8px; color: var(--text-muted); background: var(--surface-raised); border: 1px solid var(--border); border-radius: 4px; }
  .project-search:focus-within { border-color: var(--border); box-shadow: none; }
  .search-input { flex: 1; min-width: 0; padding: 7px 0; font-size: 12px; background: transparent; border: 0; color: var(--text); box-sizing: border-box; }
  .search-input:focus { outline: none; }
  .search-input::placeholder { color: var(--text-muted); }
  .search-input::-webkit-search-cancel-button { display: none; }
  .search-close { display: inline-flex; align-items: center; justify-content: center; padding: 3px; color: var(--text-muted); }
  .search-close:hover { color: var(--text); }
  .flowchart-entry { padding: 0 12px 12px; flex-shrink: 0; }
  .flowchart-btn {
    display: flex;
    align-items: center;
    gap: 8px;
    width: 100%;
    padding: 8px 12px;
    font-size: 13px;
    border-radius: 2px;
    background: var(--surface-raised);
    color: var(--accent);
    cursor: pointer;
    border: 1px solid var(--border);
    transition: opacity 0.12s;
  }
  .flowchart-btn:hover { border-color: var(--accent); background: var(--surface-raised); }
  .flowchart-btn-icon { font-size: 16px; }
  .sidebar-overlay { display: none; }
  .sidebar-close-btn-mobile { display: none; padding: 2px 8px; font-size: 14px; border-radius: 3px; color: var(--text-muted); background: none; border: none; cursor: pointer; }
  .sidebar-close-btn-mobile:hover { background: var(--surface); color: var(--text); }
  .project-description { resize: vertical; min-height: 82px; font-family: inherit; line-height: 1.5; }
  .field-error { margin-top: -6px; font-size: 11px; color: var(--red); }

  @media (max-width: 768px) {
    .sidebar { position: fixed; left: 0; top: 0; bottom: 0; width: 75vw; max-width: 320px; z-index: 900; transform: translateX(-100%); transition: transform 0.2s ease; box-shadow: 4px 0 24px rgba(0,0,0,0.3); padding-top: env(safe-area-inset-top); }
    .sidebar.open { transform: translateX(0); }
    .sidebar-overlay { display: block; position: fixed; inset: 0; background: rgba(0,0,0,0.45); z-index: 899; }
    .sidebar-close-btn-mobile { display: block; }
    .sidebar-actions { flex-wrap: wrap; }
    .action-btn { min-width: 0; }
    /* 触摸端没有 hover，抓手常显 */
    .drag-handle { opacity: 0.55; }
    .project-settings, .delete-btn { opacity: 1; }
  }
</style>
