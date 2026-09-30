<script lang="ts">
/**
 * +layout — 根布局组件
 *
 * 应用的最外层布局，提供：
 * - 全局 CSS 引入
 * - 顶部导航栏（Logo、标题、帮助按钮、主题切换）
 * - 深色/浅色主题 class 切换
 * - 首次打开自动弹出教程
 * - 任务提醒轮询（每 30 秒检查一次到期提醒）
 * - Tutorial 组件渲染
 * - 自定义标题栏（窗口拖拽、最小化/最大化/关闭按钮）
 */

  import '../app.css';
  import { onMount } from 'svelte';
  import { isDark, showTutorial, projects, activeProject, showSidebar, showSettings } from '$lib/stores';
  import { storageHealth, clearStorageFailure } from '$lib/stores/storage-health';
  import { openSearch } from '$lib/stores/search';
  import { htmlPreview } from '$lib/stores/html-preview';
  import { animationLevel } from '$lib/stores/animation';
  import {
    checkReminderCandidates,
    getReminderSettings,
    getStoredReminderNotifications,
    markReminderNotified,
    sendNotification
  } from '$lib/utils/reminder';
  import Icon from '$lib/components/shared/Icon.svelte';
  import Tutorial from '$lib/components/shared/Tutorial.svelte';
  import Settings from '$lib/components/shared/Settings.svelte';
  import HtmlPreview from '$lib/components/shared/HtmlPreview.svelte';
  import UpdateNotification from '$lib/components/shared/UpdateNotification.svelte';
  import ConflictDialog from '$lib/components/shared/ConflictDialog.svelte';
  import Toast from '$lib/components/shared/Toast.svelte';
  import ExportDialog from '$lib/components/shared/ExportDialog.svelte';
  import { startAutoSync, stopAutoSync } from '$lib/sync';
  import { initPlugins } from '$lib/plugins';
  import { get } from 'svelte/store';
  import { t } from '$lib/i18n';
  let { children } = $props();

  /** 窗口最大化状态 */
  let isMaximized = $state(false);

  /** 已提醒过的任务提醒键集合（任务 ID + 提醒时间），优先持久化到 localStorage。 */
  let reminderChecked = getStoredReminderNotifications();

  onMount(() => {
    // 首次使用自动弹出教程
    if (!localStorage.getItem('pm_tutorial_seen')) {
      setTimeout(() => showTutorial.set(true), 500);
    }

    // 按设置循环检查任务提醒。用 setTimeout 让下次调度能读取最新设置。
    let reminderTimer: ReturnType<typeof setTimeout>;

    const scheduleReminderCheck = () => {
      const settings = getReminderSettings();
      reminderTimer = setTimeout(runReminderCheck, settings.intervalMs);
    };

    const runReminderCheck = async () => {
      const settings = getReminderSettings();
      if (!settings.enabled) {
        scheduleReminderCheck();
        return;
      }

      reminderChecked = getStoredReminderNotifications();
      const candidates = get(projects).flatMap(project => project.tasks.flatMap(task => {
        const taskGroup = project.task_groups.find(group => group.id === task.task_group_id);
        const status = taskGroup?.statuses.find(item => item.id === task.status_id);
        return taskGroup && status && !taskGroup.archived ? [{ project, taskGroup, status, task }] : [];
      }));
      const due = checkReminderCandidates(candidates, reminderChecked, new Date(), settings);
      for (const candidate of due) {
        await sendNotification(get(t)('notification.reminderTitle'), `${candidate.project.name} · ${candidate.taskGroup.name}\n${candidate.task.title}`);
        reminderChecked = markReminderNotified(undefined, candidate.task, settings);
      }
      scheduleReminderCheck();
    };

    scheduleReminderCheck();

    // 启动云同步调度器（仅当同步模式为 auto 时生效，内部自行判断）
    startAutoSync();

    // 加载本地插件（已启用的才会执行；错误隔离在插件注册表内）
    initPlugins();

    // 初始化 Tauri 窗口控制（最大化状态、窗口事件监听）
    initTauriWindow();

    return () => { clearTimeout(reminderTimer); stopAutoSync(); };
  });

  // 启动时检查更新（桌面端走 tauri-plugin-updater，Android 走自定义命令）
  import { showUpdateDialog, updateInfo } from '$lib/stores/update';
  import { checkForUpdate } from '$lib/updater';
  setTimeout(async () => {
    try {
      const info = await checkForUpdate();
      if (info) {
        updateInfo.set(info);
        showUpdateDialog.set(true);
      }
    } catch { /* 静默失败 */ }
  }, 5000);

  async function initTauriWindow() {
    try {
      const { getCurrentWindow } = await import('@tauri-apps/api/window');
      const appWindow = getCurrentWindow();
      isMaximized = await appWindow.isMaximized();
      appWindow.onResized(async () => {
        isMaximized = await appWindow.isMaximized();
      });
    } catch {
      // 非 Tauri 环境（Web 预览），不做任何事
    }
  }

  async function windowMinimize() {
    try {
      const { getCurrentWindow } = await import('@tauri-apps/api/window');
      await getCurrentWindow().minimize();
    } catch { /* 非 Tauri 环境 */ }
  }

  async function windowToggleMaximize() {
    try {
      const { getCurrentWindow } = await import('@tauri-apps/api/window');
      await getCurrentWindow().toggleMaximize();
    } catch { /* 非 Tauri 环境 */ }
  }

  async function windowClose() {
    try {
      const { getCurrentWindow } = await import('@tauri-apps/api/window');
      await getCurrentWindow().close();
    } catch { /* 非 Tauri 环境 */ }
  }
</script>

<div class="app" class:dark={$isDark} data-animation={$animationLevel}>
  <header class="topbar" data-tauri-drag-region>
    <div class="topbar-left">
      <button class="hamburger" onclick={() => showSidebar.update(v => !v)}>☰</button>
      <span class="brand-mark"><Icon name="logo" size={25} /></span>
      <span class="brand-copy">
        <span class="topbar-title">简项</span>
      </span>
    </div>
    <div class="topbar-center" data-tauri-drag-region>
      {#if $activeProject}<span class="topbar-context">{$activeProject.name}</span>{/if}
    </div>
    <div class="topbar-right">
      <button class="search-btn" onclick={openSearch} title={$t('search.openHint')} aria-label={$t('search.openHint')}>
        <svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="11" cy="11" r="8"/><line x1="21" y1="21" x2="16.65" y2="16.65"/></svg>
      </button>
      <button class="settings-btn" onclick={() => showSettings.set(true)} title="Settings">⚙</button>
      <div class="window-controls">
        <button class="win-btn win-minimize" onclick={windowMinimize} aria-label="Minimize">
          <svg width="12" height="12" viewBox="0 0 12 12"><rect y="5" width="12" height="1.5" fill="currentColor"/></svg>
        </button>
        <button class="win-btn win-maximize" onclick={windowToggleMaximize} aria-label={isMaximized ? 'Restore' : 'Maximize'}>
          {#if isMaximized}
            <svg width="12" height="12" viewBox="0 0 12 12">
              <rect x="1.5" y="3.5" width="7" height="7" rx="1" fill="none" stroke="currentColor" stroke-width="1.2"/>
              <rect x="3.5" y="1.5" width="7" height="7" rx="1" fill="var(--sidebar)" stroke="currentColor" stroke-width="1.2"/>
            </svg>
          {:else}
            <svg width="12" height="12" viewBox="0 0 12 12"><rect x="1" y="1" width="10" height="10" rx="1.5" fill="none" stroke="currentColor" stroke-width="1.3"/></svg>
          {/if}
        </button>
        <button class="win-btn win-close" onclick={windowClose} aria-label="Close">
          <svg width="12" height="12" viewBox="0 0 12 12">
            <line x1="1" y1="1" x2="11" y2="11" stroke="currentColor" stroke-width="1.3" stroke-linecap="round"/>
            <line x1="11" y1="1" x2="1" y2="11" stroke="currentColor" stroke-width="1.3" stroke-linecap="round"/>
          </svg>
        </button>
      </div>
    </div>
  </header>
  {#if $storageHealth.error}
    <!-- 本地存储写入失败：必须可见，否则会静默丢数据 -->
    <div class="storage-alert" role="alert">
      <span class="sa-icon"><Icon name="alert-triangle" size={14} /></span>
      <span class="sa-text">
        {$t('storage.alertTitle')}
        <span class="sa-detail">{$storageHealth.detail}</span>
      </span>
      <button class="sa-btn" onclick={() => showSettings.set(true)}>{$t('storage.alertAction')}</button>
      <button class="sa-close" aria-label={$t('export.close')} onclick={clearStorageFailure}><Icon name="close" size={12} /></button>
    </div>
  {/if}
  <div class="main">
    {@render children()}
  </div>
  <Settings />
</div>
<Tutorial bind:show={$showTutorial} />
<HtmlPreview show={$htmlPreview.show} title={$htmlPreview.title} html={$htmlPreview.html} autoPrint={$htmlPreview.autoPrint} projectId={$htmlPreview.projectId} />
<UpdateNotification />
<ConflictDialog />
<ExportDialog />
<Toast />

<style>
  .app { display: flex; flex-direction: column; height: 100vh; background: var(--bg); color: var(--text); }
  .topbar { display: flex; align-items: center; height: 48px; padding: 0 0 0 14px; border-bottom: 3px double var(--border-strong); background: var(--surface-raised); flex-shrink: 0; user-select: none; }
  .topbar-left { display: flex; align-items: center; gap: 9px; flex-shrink: 0; min-width: 238px; }
  .brand-mark { display: flex; align-items: center; justify-content: center; filter: saturate(0.88); }
  .brand-copy { display: flex; align-items: baseline; gap: 7px; white-space: nowrap; }
  .topbar-center { flex: 1; height: 100%; display: flex; align-items: center; justify-content: center; min-width: 0; pointer-events: none; }
  .topbar-title { font-family: var(--font-serif); font-size: 14px; font-weight: 700; color: var(--text); }
  .topbar-context { max-width: min(44vw, 520px); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-family: var(--font-serif); font-size: 16px; font-weight: 600; color: var(--text); }
  .topbar-right { display: flex; align-items: center; gap: 4px; flex-shrink: 0; height: 100%; }
  .hamburger { display: none; align-items: center; justify-content: center; width: 28px; height: 28px; font-size: 14px; border-radius: 4px; background: var(--surface); color: var(--text-secondary); border: 1px solid var(--border); }
  .hamburger:hover { border-color: var(--accent); color: var(--accent); }
  .search-btn { width: 30px; height: 30px; border-radius: 3px; background: transparent; color: var(--text-secondary); border: 1px solid transparent; display: inline-flex; align-items: center; justify-content: center; }
  .search-btn:hover { background: var(--accent-light); border-color: var(--border); color: var(--accent); }
  .settings-btn { width: 30px; height: 30px; padding: 0; font-size: 15px; border-radius: 3px; background: transparent; color: var(--text-secondary); border: 1px solid transparent; }
  .settings-btn:hover { background: var(--accent-light); border-color: var(--border); color: var(--accent); }
  .main { display: flex; flex: 1; overflow: hidden; }

  /* ─── 本地存储告警条 ────────────────────────────────────────────────────── */
  .storage-alert { display: flex; align-items: center; gap: 8px; padding: 6px 12px; font-size: 12px; color: #fff; background: #b91c1c; flex-shrink: 0; }
  .sa-icon { display: flex; flex-shrink: 0; }
  /* 允许换行：这行是排障用的关键信息（谁写失败 + 底层错误），截断了就白报 */
  .sa-text { flex: 1; min-width: 0; }
  .sa-detail { opacity: 0.75; margin-left: 6px; word-break: break-all; }
  @media (min-width: 769px) {
    /* 桌面端空间够，单行展示更紧凑 */
    .sa-text { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .sa-detail { word-break: normal; }
  }
  .sa-btn { padding: 3px 10px; font-size: 11px; border-radius: 3px; background: rgba(255,255,255,0.2); color: #fff; flex-shrink: 0; }
  .sa-btn:hover { background: rgba(255,255,255,0.32); }
  .sa-close { display: flex; padding: 3px; border-radius: 3px; color: #fff; opacity: 0.8; flex-shrink: 0; }
  .sa-close:hover { opacity: 1; background: rgba(255,255,255,0.2); }

  /* ─── 窗口控制按钮 ────────────────────────────────────────────────────────── */
  .window-controls { display: flex; align-items: center; height: 100%; margin-left: 6px; border-left: 1px solid var(--border); }
  .win-btn { display: flex; align-items: center; justify-content: center; width: 46px; height: 100%; color: var(--text-secondary); transition: background 0.12s, color 0.12s; }
  .win-btn:hover { background: var(--surface); color: var(--text); }
  .win-close:hover { background: #e81123; color: #fff; }
  .dark .win-close:hover { background: #e81123; color: #fff; }

  @media (max-width: 768px) {
    .app {
      width: 100%;
      height: auto;
      min-height: 100vh;
      max-width: 480px;
      margin: 0 auto;
      box-shadow: var(--shadow-float);
    }
    :global(body) { overflow: auto; background: #d9d2c7; }
    :global(.dark) :global(body) { background: #12110f; }
    .hamburger { display: flex; }
    .brand-copy { display: none; }
    .topbar-left { min-width: 0; }
    .window-controls { display: none; }
    .main { overflow: visible; flex: none; flex-direction: column; }
    .topbar { position: sticky; top: 0; z-index: 50; padding-top: env(safe-area-inset-top); height: calc(44px + env(safe-area-inset-top)); }
  }
</style>
