<script lang="ts">
/**
 * CommandPalette — 快捷命令面板
 *
 * 类似 VS Code 的命令面板，通过 Ctrl+K 打开。
 * 支持模糊搜索、键盘导航、分类显示。
 *
 * @example
 * <CommandPalette bind:show={showPalette} onViewChange={(v) => view = v} onShowShortcutHelp={() => showHelp = true} />
 */

  import { undo, redo, statusFilter, taskGroupFilter, downloadPmFile, exportMarkdownFile, exportHtmlFile, exportPdfFile, activeProjectId, activeProject, pendingAction, showSettings, setThemeMode } from '$lib/stores';
  import { get } from 'svelte/store';
  import { t, locale } from '$lib/i18n';
  import { generateReport, saveReport } from '$lib/ai';
  import { animationLevel } from '$lib/stores/animation';
  import { pluginCommands } from '$lib/plugins';
  import { fade, fly } from 'svelte/transition';
  import { buildProjectAiTaskContext } from '$lib/utils/ai-context';

  interface Props {
    show: boolean;
    onViewChange: (view: string) => void;
    onShowShortcutHelp: () => void;
  }

  let { show = $bindable(false), onViewChange, onShowShortcutHelp }: Props = $props();

  interface Command {
    id: string;
    nameKey: string;
    descKey: string;
    catKey: string;
    shortcut?: string;
    /** 插件命令直接提供显示名（优先级高于 nameKey） */
    name?: string;
    /** 插件命令直接提供说明 */
    desc?: string;
    action: () => void;
  }

  function getCommands(): Command[] {
    const builtin: Command[] = [
      // 视图切换
      { id: 'view:kanban', nameKey: 'cmd.view.kanban', descKey: 'cmd.view.kanbanDesc', catKey: 'command.cat.view', shortcut: '1', action: () => onViewChange('kanban') },
      { id: 'view:list', nameKey: 'cmd.view.list', descKey: 'cmd.view.listDesc', catKey: 'command.cat.view', shortcut: '2', action: () => onViewChange('list') },
      { id: 'view:calendar', nameKey: 'cmd.view.calendar', descKey: 'cmd.view.calendarDesc', catKey: 'command.cat.view', shortcut: '3', action: () => onViewChange('calendar') },
      { id: 'view:timeline', nameKey: 'cmd.view.timeline', descKey: 'cmd.view.timelineDesc', catKey: 'command.cat.view', shortcut: '4', action: () => onViewChange('timeline') },
      { id: 'view:graph', nameKey: 'cmd.view.graph', descKey: 'cmd.view.graphDesc', catKey: 'command.cat.view', shortcut: '5', action: () => onViewChange('graph') },

      // 任务操作
      { id: 'task:new', nameKey: 'cmd.task.new', descKey: 'cmd.task.newDesc', catKey: 'command.cat.task', shortcut: 'Ctrl+N', action: () => { document.querySelector<HTMLInputElement>('.add-input')?.focus(); } },
      { id: 'task:search', nameKey: 'cmd.task.search', descKey: 'cmd.task.searchDesc', catKey: 'command.cat.task', shortcut: 'Ctrl+F', action: () => { document.querySelector<HTMLInputElement>('.filter-search')?.focus(); } },
      { id: 'task:undo', nameKey: 'cmd.task.undo', descKey: 'cmd.task.undoDesc', catKey: 'command.cat.task', shortcut: 'Ctrl+Z', action: () => undo() },
      { id: 'task:redo', nameKey: 'cmd.task.redo', descKey: 'cmd.task.redoDesc', catKey: 'command.cat.task', shortcut: 'Ctrl+Shift+Z', action: () => redo() },

      // 项目操作
      { id: 'project:new', nameKey: 'cmd.project.new', descKey: 'cmd.project.newDesc', catKey: 'command.cat.project', action: () => pendingAction.set('new-project') },
      { id: 'project:import', nameKey: 'cmd.project.import', descKey: 'cmd.project.importDesc', catKey: 'command.cat.project', action: () => pendingAction.set('import-project') },

      // 主题（与设置里的三个按钮等价）
      { id: 'theme:system', nameKey: 'cmd.theme.system', descKey: 'cmd.theme.systemDesc', catKey: 'command.cat.view', action: () => setThemeMode('system') },
      { id: 'theme:light', nameKey: 'cmd.theme.light', descKey: 'cmd.theme.lightDesc', catKey: 'command.cat.view', action: () => setThemeMode('light') },
      { id: 'theme:dark', nameKey: 'cmd.theme.dark', descKey: 'cmd.theme.darkDesc', catKey: 'command.cat.view', action: () => setThemeMode('dark') },

      // 导出
      { id: 'export:pm', nameKey: 'cmd.export.pm', descKey: 'cmd.export.pmDesc', catKey: 'command.cat.export', action: () => { const id = get(activeProjectId); if (id) downloadPmFile(id); } },
      { id: 'export:md', nameKey: 'cmd.export.md', descKey: 'cmd.export.mdDesc', catKey: 'command.cat.export', action: () => { const id = get(activeProjectId); if (id) exportMarkdownFile(id); } },
      { id: 'export:html', nameKey: 'cmd.export.html', descKey: 'cmd.export.htmlDesc', catKey: 'command.cat.export', action: () => { const id = get(activeProjectId); if (id) exportHtmlFile(id); } },
      { id: 'export:pdf', nameKey: 'cmd.export.pdf', descKey: 'cmd.export.pdfDesc', catKey: 'command.cat.export', action: () => { const id = get(activeProjectId); if (id) exportPdfFile(id); } },

      // 筛选
      { id: 'filter:all', nameKey: 'cmd.filter.all', descKey: 'cmd.filter.allDesc', catKey: 'command.cat.filter', action: () => statusFilter.set('all') },
      { id: 'filter:todo', nameKey: 'cmd.filter.todo', descKey: 'cmd.filter.todoDesc', catKey: 'command.cat.filter', action: () => { taskGroupFilter.set('all'); statusFilter.set('todo'); } },
      { id: 'filter:progress', nameKey: 'cmd.filter.progress', descKey: 'cmd.filter.progressDesc', catKey: 'command.cat.filter', action: () => { taskGroupFilter.set('all'); statusFilter.set('active'); } },
      { id: 'filter:done', nameKey: 'cmd.filter.done', descKey: 'cmd.filter.doneDesc', catKey: 'command.cat.filter', action: () => { taskGroupFilter.set('all'); statusFilter.set('done'); } },

      // 设置
      { id: 'setting:open', nameKey: 'cmd.setting.open', descKey: 'cmd.setting.openDesc', catKey: 'command.cat.setting', shortcut: 'Ctrl+,', action: () => showSettings.set(true) },

      // 窗口
      { id: 'window:minimize', nameKey: 'cmd.window.minimize', descKey: 'cmd.window.minimizeDesc', catKey: 'command.cat.window', action: async () => { const { getCurrentWindow } = await import('@tauri-apps/api/window'); getCurrentWindow().hide(); } },

      // Git

      // AI
      { id: 'ai:report', nameKey: 'cmd.report.generate', descKey: 'cmd.report.generateDesc', catKey: 'command.cat.ai', action: async () => {
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
          alert(`${get(t)('ai.reportSaved')}: ${path}`);
        } catch (e: any) { alert(e?.message ?? get(t)('ai.reportFailed')); }
      }},

    ];

    // 插件命令（由插件通过 ctx.registerCommand 注册）
    const plugin: Command[] = $pluginCommands.map((c) => ({
      id: `plugin:${c.id}`,
      nameKey: '',
      descKey: '',
      catKey: 'command.cat.plugin',
      name: c.title,
      desc: c.category ?? '',
      action: () => { void c.run(); },
    }));

    return [...builtin, ...plugin];
  }

  let query = $state('');
  let selected = $state(0);
  let inputEl = $state<HTMLInputElement | null>(null);
  let listEl = $state<HTMLDivElement | null>(null);

  let filtered = $derived(() => {
    void $locale; // reactive dependency on locale changes
    const commands = getCommands();
    const q = query.trim().toLowerCase();
    if (!q) return commands;
    const tr = get(t);
    return commands.filter(cmd => {
      const label = (cmd.name ?? tr(cmd.nameKey)).toLowerCase();
      const desc = (cmd.desc ?? tr(cmd.descKey)).toLowerCase();
      return label.includes(q) || desc.includes(q) || tr(cmd.catKey).toLowerCase().includes(q);
    });
  });

  function handleInput() {
    selected = 0;
  }

  function handleKeydown(e: KeyboardEvent) {
    const items = filtered();
    if (e.key === 'ArrowDown') {
      e.preventDefault();
      selected = (selected + 1) % items.length;
      scrollToSelected();
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      selected = (selected - 1 + items.length) % items.length;
      scrollToSelected();
    } else if (e.key === 'Enter') {
      e.preventDefault();
      if (items[selected]) execute(items[selected]);
    } else if (e.key === 'Escape') {
      e.preventDefault();
      close();
    }
  }

  function scrollToSelected() {
    if (!listEl) return;
    const el = listEl.querySelector('.cmd-item.active') as HTMLElement | null;
    el?.scrollIntoView({ block: 'nearest' });
  }

  function execute(cmd: Command) {
    close();
    cmd.action();
  }

  function close() {
    show = false;
    query = '';
    selected = 0;
  }

  $effect(() => {
    if (show) {
      requestAnimationFrame(() => inputEl?.focus());
    }
  });
</script>

{#if show}
  <!-- svelte-ignore a11y_no_static_element_interactions a11y_click_events_have_key_events a11y_interactive_supports_focus -->
  <div class="overlay" onclick={close} onkeydown={e => e.key === 'Escape' && close()} role="dialog" tabindex="-1" transition:fade={{ duration: $animationLevel === 'rich' ? 250 : 150 }}>
    <div class="palette" onclick={e => e.stopPropagation()} transition:fly={{ y: $animationLevel === 'rich' ? -20 : -10, duration: $animationLevel === 'rich' ? 250 : 150 }}>
      <div class="search-wrap">
        <svg class="search-icon" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
          <circle cx="11" cy="11" r="8"/><line x1="21" y1="21" x2="16.65" y2="16.65"/>
        </svg>
        <input
          class="search-input"
          placeholder={$t('command.placeholder')}
          bind:this={inputEl}
          bind:value={query}
          oninput={handleInput}
          onkeydown={handleKeydown}
        />
        <span class="search-hint">{$t('command.escHint')}</span>
      </div>
      <div class="command-list" bind:this={listEl}>
        {#each filtered() as cmd, i (cmd.id)}
          <div
            class="cmd-item"
            class:active={i === selected}
            onclick={() => execute(cmd)}
            onmouseenter={() => selected = i}
            role="option"
            aria-selected={i === selected}
          >
            <span class="cmd-category">{$t(cmd.catKey)}</span>
            <div class="cmd-text">
              <span class="cmd-name">{cmd.name ?? $t(cmd.nameKey)}</span>
              <span class="cmd-desc">{cmd.desc ?? $t(cmd.descKey)}</span>
            </div>
            {#if cmd.shortcut}
              <kbd class="cmd-shortcut">{cmd.shortcut}</kbd>
            {/if}
          </div>
        {/each}
        {#if filtered().length === 0}
          <div class="cmd-empty">{$t('command.noMatch')}</div>
        {/if}
      </div>
    </div>
  </div>
{/if}

<style>
  .overlay {
    position: fixed;
    inset: 0;
    background: rgba(0, 0, 0, 0.5);
    display: flex;
    align-items: flex-start;
    justify-content: center;
    padding-top: 15vh;
    z-index: 1000;
  }

  .palette {
    width: 520px;
    max-height: 420px;
    background: var(--bg);
    border: 1px solid var(--border);
    border-radius: 12px;
    box-shadow: 0 16px 48px rgba(0, 0, 0, 0.3);
    display: flex;
    flex-direction: column;
    overflow: hidden;
    animation: slideIn 0.15s ease;
  }

  @keyframes slideIn {
    from { opacity: 0; transform: translateY(-8px) scale(0.98); }
    to { opacity: 1; transform: translateY(0) scale(1); }
  }

  .search-wrap {
    display: flex;
    align-items: center;
    gap: 10px;
    padding: 14px 16px;
    border-bottom: 1px solid var(--border);
  }

  .search-icon {
    color: var(--text-muted);
    flex-shrink: 0;
  }

  .search-input {
    flex: 1;
    font-size: 14px;
    background: none;
    border: none;
    outline: none;
    color: var(--text);
  }

  .search-input::placeholder {
    color: var(--text-muted);
  }

  .search-hint {
    font-size: 11px;
    color: var(--text-muted);
    padding: 2px 6px;
    background: var(--surface);
    border-radius: 4px;
    flex-shrink: 0;
  }

  .command-list {
    flex: 1;
    overflow-y: auto;
    padding: 6px;
  }

  .cmd-item {
    display: flex;
    align-items: center;
    gap: 10px;
    padding: 8px 10px;
    border-radius: 8px;
    cursor: pointer;
    transition: background 0.1s;
  }

  .cmd-item:hover,
  .cmd-item.active {
    background: var(--surface);
  }

  .cmd-item.active {
    background: var(--accent-light);
  }

  .cmd-category {
    font-size: 10px;
    font-weight: 600;
    color: var(--accent);
    background: var(--accent-light);
    padding: 2px 8px;
    border-radius: 999px;
    flex-shrink: 0;
    min-width: 40px;
    text-align: center;
  }

  .cmd-text {
    flex: 1;
    min-width: 0;
    display: flex;
    flex-direction: column;
    gap: 1px;
  }

  .cmd-name {
    font-size: 13px;
    font-weight: 500;
    color: var(--text);
  }

  .cmd-desc {
    font-size: 11px;
    color: var(--text-muted);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .cmd-shortcut {
    font-size: 11px;
    font-family: inherit;
    color: var(--text-muted);
    padding: 2px 8px;
    background: var(--surface);
    border: 1px solid var(--border);
    border-radius: 4px;
    flex-shrink: 0;
  }

  .cmd-empty {
    padding: 24px;
    text-align: center;
    font-size: 13px;
    color: var(--text-muted);
  }
</style>
