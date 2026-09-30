<script lang="ts">
  import { t } from '$lib/i18n';
  import { onMount } from 'svelte';
  import { get } from 'svelte/store';
  import { downloadPmFile, importProject, activeProjectId, exportBackupJson, restoreBackupJson } from '$lib/stores';
  import { storageHealth, refreshStorageUsage, fmtBytes, STORAGE_BUDGET_BYTES, externalSyncIssues, clearExternalSyncIssue, clearAllExternalSyncIssues } from '$lib/stores/storage-health';
  import { updateProject, projects } from '$lib/stores/project';
  import { storagePrefs, getStoragePref, forgetStoragePref } from '$lib/utils/storage-prefs';
  import { normalizeProjectName } from '$lib/utils/storage-inherit';
  import { activeProject } from '$lib/stores/filter';
  import { getDeviceId } from '$lib/utils/device';
  import { saveTextFile } from '$lib/utils/save-file';
  import { pickFile } from '$lib/utils/pick-file';
  import { backupFileName } from '$lib/utils/backup';
  import { toast } from '$lib/stores/toast';
  import {
    testConnection,
    syncNow,
    pushAllNow,
    pullAllNow,
    syncRuntime,
    syncConflicts,
    restartAutoSync,
    getSyncConfig,
    getSyncBackend,
    getSyncMode,
    getSyncInterval,
    getServerUrl,
    getServerToken,
    WEBDAV_PRESETS,
  } from '$lib/sync';
  import type { SyncBackend, SyncMode } from '$lib/sync';

  // ─── 本地设置读写 ─────────────────────────────────────────────────────────
  function loadSync(key: string, fallback = '') {
    try { return localStorage.getItem(key) ?? fallback; } catch { return fallback; }
  }
  function saveSync(key: string, val: string) {
    try { localStorage.setItem(key, val); } catch { /* ignore */ }
  }

  let syncBackend = $state<SyncBackend>(getSyncBackend());
  let syncMode = $state<SyncMode>(getSyncMode());
  let syncInterval = $state(getSyncInterval());
  let syncPresetIdx = $state(Number(loadSync('pm_sync_preset', '0')));

  // WebDAV
  let syncUrl = $state(loadSync('pm_sync_url', ''));
  let syncUser = $state(loadSync('pm_sync_user', ''));
  let syncPass = $state(loadSync('pm_sync_pass', ''));
  // GitHub Gist
  let syncGistId = $state(loadSync('pm_sync_gist', ''));
  // 服务器
  let serverUrl = $state(getServerUrl());
  let serverToken = $state(getServerToken());

  let syncLoading = $state(false);
  let syncMsg = $state('');
  let syncOk = $state(true);
  let lastSync = $state(loadSync('pm_sync_last', ''));

  function saveSyncSettings() {
    saveSync('pm_sync_backend', syncBackend);
    saveSync('pm_sync_mode', syncMode);
    saveSync('pm_sync_interval', String(syncInterval));
    saveSync('pm_sync_preset', String(syncPresetIdx));
    saveSync('pm_sync_url', syncUrl);
    saveSync('pm_sync_user', syncUser);
    saveSync('pm_sync_gist', syncGistId);
    saveSync('pm_sync_server_url', serverUrl);
    saveSync('pm_sync_token', serverToken);
  }

  /** 持久化并重启调度器（仅在后端/模式/间隔等结构变化时调用，避免每次输入都重连） */
  function saveSyncSettingsAndRestart() {
    saveSyncSettings();
    restartAutoSync();
  }

  function setBackend(b: SyncBackend) {
    syncBackend = b;
    if (b === 'webdav' && WEBDAV_PRESETS[syncPresetIdx]) syncUrl = WEBDAV_PRESETS[syncPresetIdx].url;
    saveSyncSettingsAndRestart();
  }

  function setMode(m: SyncMode) {
    syncMode = m;
    saveSyncSettingsAndRestart();
  }

  $effect(() => {
    if (syncBackend === 'webdav' && WEBDAV_PRESETS[syncPresetIdx]) {
      syncUrl = WEBDAV_PRESETS[syncPresetIdx].url;
    }
  });

  // 状态文案
  const stateLabel = $derived(
    $syncRuntime.state === 'syncing' ? $t('sync.syncing')
    : $syncRuntime.state === 'error' ? $t('sync.connectionFailed')
    : $syncRuntime.state === 'offline' ? $t('sync.offline')
    : $syncRuntime.state === 'stopped' ? $t('sync.stopped')
    : $t('sync.idle')
  );

  async function withLoading(fn: () => Promise<void>, okMsg: string) {
    syncLoading = true;
    syncMsg = '';
    try {
      await fn();
      syncOk = true;
      syncMsg = okMsg;
      lastSync = loadSync('pm_sync_last', '');
    } catch (e: unknown) {
      syncOk = false;
      syncMsg = e instanceof Error ? e.message : String(e);
    } finally {
      syncLoading = false;
    }
  }

  const handleSyncTest = () =>
    withLoading(async () => {
      const ok = await testConnection(getSyncConfig());
      if (!ok) throw new Error($t('sync.connectionFailed'));
    }, $t('sync.connectionOk'));

  const handleSyncNow = () => withLoading(async () => { await syncNow(); }, $t('sync.connectionOk'));
  const handleSyncPush = () => withLoading(async () => { await pushAllNow(); }, $t('sync.connectionOk'));
  const handleSyncPull = () => withLoading(async () => { await pullAllNow(); }, $t('sync.connectionOk'));

  let clearConfirm = $state(false);

  function handleExport() {
    const pid = get(activeProjectId);
    if (pid) downloadPmFile(pid);
  }

  async function handleImport() {
    const file = await pickFile('.pm,.json');
    if (!file) return;
    const text = await file.text();
    importProject(text, file.name);
  }

  // ─── 全量备份 / 恢复 ──────────────────────────────────────────────────────

  /**
   * 本地存储占用
   *
   * 直接用 store 派生（不要用 `$state` + `$effect` 互写，会自触发循环）；
   * 进入页面时刷新一次，之后每次备份/恢复后再刷。
   */
  const usage = $derived($storageHealth);
  const usedPct = $derived(Math.min(100, Math.round((usage.usedBytes / STORAGE_BUDGET_BYTES) * 100)));
  onMount(() => { refreshStorageUsage(); });

  let backupMsg = $state('');

  // ─── 外部副本（项目自选的本地文件夹）问题 ────────────────────────────────
  const externalIssues = $derived(Object.values($externalSyncIssues));

  /** 把项目改回「应用内部存储」，从此不再尝试写外部文件夹（**两步确认**，避免误点永久丢失路径） */
  let switchConfirm = $state<string | null>(null);
  function switchToAppStorage(projectId: string, name: string) {
    if (switchConfirm !== projectId) {
      switchConfirm = projectId;
      setTimeout(() => { if (switchConfirm === projectId) switchConfirm = null; }, 6000);
      return;
    }
    switchConfirm = null;
    updateProject(projectId, { storage: undefined });
    // 用户主动改回内部存储：清掉“历史文件夹”记录，避免下次误报“配置丢失”
    forgetStoragePref(name);
    clearExternalSyncIssue(projectId);
    toast(`${$t('settings.externalSwitched')}：${name}`, 'ok');
  }

  /**
   * 「原本用本地文件夹、但 storage 已丢失」的项目
   *
   * 依据本机旁路记录 `pm_storage_prefs`（只在 persistProject 时写入，见 utils/storage-prefs.ts）。
   */
  const lostStorageProjects = $derived.by(() => {
    const prefs = $storagePrefs;
    return $projects.filter((p) => !p.storage && !!prefs[normalizeProjectName(p.name)]);
  });

  /** 一键恢复本机记录的本地文件夹配置 */
  function restoreStorage(projectId: string, name: string) {
    const pref = getStoragePref(name);
    if (!pref) return;
    updateProject(projectId, { storage: pref });
    toast(`${$t('settings.storageLostRestored')}：${name}`, 'ok');
  }

  /**
   * 当前项目的本地文件夹归属信息（只读展示）
   *
   * - 本机是归属：本项目由本机写入本地文件夹
   * - 别的设备是归属：非归属设备只参与服务器同步（不写本地）
   * - 还没有归属：首次成功写入后才确定
   */
  const storageOwnerInfo = $derived.by(() => {
    const proj = $activeProject;
    const storage = proj?.storage;
    if (!proj || !storage || storage.type !== 'local' || !storage.path) return null;
    const owner = storage.ownerDeviceId;
    if (!owner) return { kind: 'unknown' as const, name: '' };
    return owner === getDeviceId()
      ? { kind: 'self' as const, name: storage.ownerDeviceName || '' }
      : { kind: 'other' as const, name: storage.ownerDeviceName || owner.slice(0, 8) };
  });

  /** 项目是否还在列表里（可能已被删除，此时按钮无意义） */
  function projectExists(id: string): boolean {
    return $projects.some((p) => p.id === id);
  }

  /** 导出全量备份（所有项目 + 非敏感设置；不含任何凭据） */
  async function handleBackup() {
    backupMsg = '';
    const json = exportBackupJson();
    const name = backupFileName();
    const res = await saveTextFile(name, json, 'application/json');
    if (res.kind === 'saved') {
      const kb = (json.length / 1024).toFixed(0);
      backupMsg = `${$t('settings.backupDone')} ${res.path ?? name}（${kb} KB）`;
      toast(`${$t('settings.backupDone')}（${kb} KB）`, 'ok');
    } else if (res.kind === 'error') {
      backupMsg = `${$t('export.failed')}：${res.message}`;
      toast(`${$t('export.failed')}：${res.message}`, 'err');
    }
    refreshStorageUsage();
  }

  /** 从备份包恢复（合并；只统计新增/覆盖/跳过，不删除本地项目） */
  async function handleRestore() {
    const file = await pickFile('.json,application/json');
    if (!file) return;
    const text = await file.text();
    const res = restoreBackupJson(text);
    if (!res.ok) {
      backupMsg = `${$t('export.failed')}：${res.error}`;
      toast(`${$t('export.failed')}：${res.error}`, 'err', 12000);
      return;
    }
    const { added, replaced, skipped, skippedProjects } = res.report;
    backupMsg = $t('settings.restoreDone')
      .replace('{a}', String(added)).replace('{r}', String(replaced)).replace('{s}', String(skipped));
    toast(backupMsg, added + replaced > 0 ? 'ok' : 'info', 10000);
    if (skippedProjects.length > 0) {
      console.warn('[Restore] 跳过的项目：', skippedProjects);
    }
    refreshStorageUsage();
  }

  /** 清理前的安全备份（尽力而为，不阻断清理） */
  async function backupBeforeClear() {
    const res = await saveTextFile(backupFileName(), exportBackupJson(), 'application/json');
    return res.kind === 'saved';
  }

  async function handleClearData() {
    // 两步确认 + **强制先导出备份**：清空是不可逆操作，不能只靠连点两下
    if (!clearConfirm) {
      clearConfirm = true;
      setTimeout(() => clearConfirm = false, 8000);
      return;
    }
    clearConfirm = false;
    backupMsg = $t('settings.backupBeforeClear');
    try { await backupBeforeClear(); } catch { /* 用户取消也允许继续，但已提示 */ }
    try { localStorage.clear(); } catch { /* ignore */ }
    location.reload();
  }
</script>

<div class="section">
  <span class="section-title">{$t('settings.sync')}</span>

  <!-- 后端选择 -->
  <div class="btn-row">
    <button class="btn-toggle" class:active={syncBackend === 'webdav'} onclick={() => setBackend('webdav')}>{$t('sync.backendWebdav')}</button>
    <button class="btn-toggle" class:active={syncBackend === 'github_gist'} onclick={() => setBackend('github_gist')}>{$t('sync.backendGist')}</button>
    <button class="btn-toggle" class:active={syncBackend === 'server'} onclick={() => setBackend('server')}>{$t('sync.backendServer')}</button>
  </div>

  {#if syncBackend === 'webdav'}
    <div class="btn-row">
      {#each WEBDAV_PRESETS as p, i}
        <button class="btn-toggle small" class:active={syncPresetIdx === i} onclick={() => { syncPresetIdx = i; saveSyncSettings(); }}>{p.name}</button>
      {/each}
    </div>
    <input class="input" placeholder={$t('sync.url')} bind:value={syncUrl} oninput={saveSyncSettings} />
    <input class="input" placeholder={$t('sync.username')} bind:value={syncUser} oninput={saveSyncSettings} />
  {:else if syncBackend === 'github_gist'}
    <input class="input" placeholder={$t('sync.gistId')} bind:value={syncGistId} oninput={saveSyncSettings} />
  {:else}
    <input class="input" placeholder={$t('sync.serverUrl')} bind:value={serverUrl} oninput={saveSyncSettings} />
    <p class="hint">{$t('sync.serverHint')}</p>
  {/if}

  <!-- 凭据 -->
  <div class="sync-pass-row">
    {#if syncBackend === 'server'}
      <input class="input" type="password" placeholder={$t('sync.token')} bind:value={serverToken} oninput={saveSyncSettings} />
    {:else}
      <input class="input" type="password" placeholder={syncBackend === 'webdav' ? $t('sync.password') : 'ghp_xxx'} bind:value={syncPass} oninput={saveSyncSettings} />
    {/if}
  </div>

  <!-- 同步模式 -->
  <span class="section-title">{$t('sync.mode')}</span>
  <div class="btn-row">
    <button class="btn-toggle" class:active={syncMode === 'manual'} onclick={() => setMode('manual')}>{$t('sync.modeManual')}</button>
    <button class="btn-toggle" class:active={syncMode === 'auto'} onclick={() => setMode('auto')}>{$t('sync.modeAuto')}</button>
  </div>
  {#if syncMode === 'auto'}
    <div class="interval-row">
      <span class="hint">{$t('sync.interval')}</span>
      <input class="input interval" type="number" min="3" max="300" bind:value={syncInterval} onchange={saveSyncSettingsAndRestart} />
    </div>
  {/if}

  <!-- 状态 -->
  <div class="status-line">
    <span class="dot" class:on={$syncRuntime.running} class:err={$syncRuntime.state === 'error' || $syncRuntime.state === 'offline'}></span>
    <span>{$t('sync.status')}: {stateLabel}</span>
    {#if $syncRuntime.pending > 0}<span>· {$t('sync.pending')} {$syncRuntime.pending}</span>{/if}
    {#if $syncConflicts.length > 0}<span class="conflict">· {$t('sync.conflictCount')} {$syncConflicts.length}</span>{/if}
    {#if lastSync}<span>· {$t('sync.lastSync')}: {lastSync}</span>{/if}
  </div>
  <div class="status-line">
    <span>{syncMode === 'auto' ? $t('sync.autoOn') : $t('sync.autoOff')}</span>
  </div>

  {#if syncMsg}
    <p class="status-msg" class:ok={syncOk} class:err={!syncOk}>{syncMsg}</p>
  {/if}
  {#if $syncRuntime.lastError}
    <p class="status-msg err">{$syncRuntime.lastError}</p>
  {/if}

  <div class="btn-row">
    <button class="btn btn-ghost" onclick={handleSyncTest} disabled={syncLoading}>{$t('sync.test')}</button>
    <button class="btn btn-primary" onclick={handleSyncNow} disabled={syncLoading}>{$t('sync.now')}</button>
  </div>
  <div class="btn-row">
    <button class="btn btn-ghost" onclick={handleSyncPush} disabled={syncLoading}>{$t('sync.push')}</button>
    <button class="btn btn-ghost" onclick={handleSyncPull} disabled={syncLoading}>{$t('sync.pull')}</button>
  </div>
</div>

<div class="section">
  <span class="section-title">{$t('settings.export')} / {$t('settings.import')}</span>
  <div class="btn-row">
    <button class="btn btn-ghost" onclick={handleExport}>{$t('settings.export')} .pm</button>
    <button class="btn btn-ghost" onclick={handleImport}>{$t('settings.import')} .pm</button>
  </div>
  <p class="hint">{$t('settings.singleProjectHint')}</p>
</div>

<!-- 存储健康：写入失败时必须在这里也能看到 -->
<div class="section">
  <span class="section-title">{$t('settings.storageHealth')}</span>
  <div class="health-row">
    <div class="health-bar"><div class="health-fill" class:full={usedPct >= 80} style="width:{usedPct}%"></div></div>
    <span class="health-label">{fmtBytes(usage.usedBytes)} / {fmtBytes(STORAGE_BUDGET_BYTES)}（{usedPct}%）</span>
  </div>
  {#if $storageHealth.error}
    <p class="health-err">{$t('storage.alertTitle')} — {$storageHealth.detail}</p>
  {:else if usedPct >= 80}
    <p class="hint">{$t('settings.storageNearLimit')}</p>
  {:else}
    <p class="hint">{$t('settings.storageOk')}</p>
  {/if}
  <div class="btn-row">
    <button class="btn btn-ghost" onclick={() => { refreshStorageUsage(); toast(fmtBytes($storageHealth.usedBytes), 'info', 3000); }}>{$t('settings.refresh')}</button>
  </div>
</div>

<!-- 本地文件夹归属：只读信息（非归属设备不写本地，是预期行为） -->
{#if storageOwnerInfo}
  <div class="section">
    <span class="section-title">{$t('settings.storageOwner')}</span>
    <p class="hint">
      {#if storageOwnerInfo.kind === 'self'}
        {$t('settings.storageOwnerSelf')}
      {:else if storageOwnerInfo.kind === 'other'}
        {$t('settings.storageOwnerOther').replace('{name}', storageOwnerInfo.name)}
      {:else}
        {$t('settings.storageOwnerUnknown')}
      {/if}
    </p>
  </div>
{/if}

<!-- 外部副本（本地文件夹）问题：只在这里提示，不弹红色告警条 -->
{#if externalIssues.length > 0}
  <div class="section">
    <div class="ext-head">
      <span class="section-title">{$t('settings.externalTitle')} <span class="ext-count">{externalIssues.length}</span></span>
      <button class="btn btn-ghost small" onclick={() => clearAllExternalSyncIssues()}>{$t('settings.externalIgnoreAll')}</button>
    </div>
    <p class="hint">{$t('settings.externalHint')}</p>
    {#each externalIssues as issue (issue.projectId)}
      <div class="ext-item">
        <div class="ext-info">
          <span class="ext-name">{issue.name}</span>
          <span class="ext-path" title={issue.path}>{issue.path}</span>
          <span class="ext-msg">{issue.message}</span>
        </div>
        <div class="ext-actions">
          {#if projectExists(issue.projectId)}
            <button class="btn btn-ghost small" onclick={() => switchToAppStorage(issue.projectId, issue.name)}>{switchConfirm === issue.projectId ? $t('settings.switchConfirmShort') : $t('settings.externalSwitch')}</button>
          {/if}
          <button class="btn btn-ghost small" onclick={() => clearExternalSyncIssue(issue.projectId)}>{$t('settings.externalIgnore')}</button>
        </div>
      </div>
    {/each}
  </div>
{/if}

<!-- 本地文件夹配置丢失：可见提示 + 一键恢复（不静默回退） -->
{#if lostStorageProjects.length > 0}
  <div class="section">
    <div class="ext-head">
      <span class="section-title">{$t('settings.storageLostTitle')} <span class="ext-count">{lostStorageProjects.length}</span></span>
    </div>
    <p class="hint">{$t('settings.storageLostHint')}</p>
    {#each lostStorageProjects as proj (proj.id)}
      <div class="ext-item">
        <div class="ext-info">
          <span class="ext-name">{proj.name}</span>
          <span class="ext-path" title={$storagePrefs[normalizeProjectName(proj.name)]?.path}>{$storagePrefs[normalizeProjectName(proj.name)]?.path}</span>
        </div>
        <div class="ext-actions">
          <button class="btn btn-ghost small" onclick={() => restoreStorage(proj.id, proj.name)}>{$t('settings.storageLostRestore')}</button>
          <button class="btn btn-ghost small" onclick={() => forgetStoragePref(proj.name)}>{$t('settings.storageLostDismiss')}</button>
        </div>
      </div>
    {/each}
  </div>
{/if}

<!-- 全量备份 / 恢复 -->
<div class="section">
  <span class="section-title">{$t('settings.backup')}</span>
  <div class="btn-row">
    <button class="btn btn-primary" onclick={handleBackup}>{$t('settings.backupExport')}</button>
    <button class="btn btn-ghost" onclick={handleRestore}>{$t('settings.backupRestore')}</button>
  </div>
  <p class="hint">{$t('settings.backupHint')}</p>
  {#if backupMsg}<p class="hint ok">{$t('settings.result')}：{backupMsg}</p>{/if}
</div>

<div class="section danger">
  <span class="section-title">{$t('settings.clearData')}</span>
  <button class="btn btn-danger" onclick={handleClearData}>
    {clearConfirm ? $t('settings.clearDataConfirmShort') : $t('settings.clearData')}
  </button>
  <p class="hint">{$t('settings.clearDataHint')}</p>
</div>

<style>
  .section { display: flex; flex-direction: column; gap: 8px; }
  .section-title { font-size: 11px; color: var(--text-muted); text-transform: uppercase; letter-spacing: 0; }
  .section.danger { padding-top: 12px; border-top: 1px solid var(--border); }
  .health-row { display: flex; align-items: center; gap: 8px; }
  .health-bar { flex: 1; height: 6px; border-radius: 3px; background: var(--surface); border: 1px solid var(--border); overflow: hidden; }
  .health-fill { height: 100%; background: var(--accent); transition: width 0.2s; }
  .health-fill.full { background: var(--red, #ef4444); }
  .health-label { font-size: 11px; color: var(--text-muted); font-variant-numeric: tabular-nums; }
  .health-err { font-size: 11px; color: var(--red, #ef4444); line-height: 1.5; }
  .hint.ok { color: var(--green, #10b981); }
  .ext-head { display: flex; align-items: center; justify-content: space-between; gap: 8px; }
  .ext-count { font-size: 10px; color: var(--text-muted); background: var(--surface); padding: 1px 6px; border-radius: 999px; }
  .ext-item { display: flex; align-items: center; gap: 8px; padding: 8px; border: 1px solid var(--border); border-radius: 8px; background: var(--surface); }
  .ext-info { flex: 1; min-width: 0; display: flex; flex-direction: column; gap: 2px; }
  .ext-name { font-size: 12px; font-weight: 600; color: var(--text); }
  .ext-path { font-size: 10px; color: var(--text-muted); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .ext-msg { font-size: 10px; color: var(--yellow, #f59e0b); }
  .ext-actions { display: flex; gap: 4px; flex-shrink: 0; }
  .btn.small { padding: 4px 8px; font-size: 11px; }
  .btn-primary { padding: 7px 12px; font-size: 12px; border-radius: 6px; background: var(--accent); color: #fff; border: 1px solid var(--accent); }
  .btn-row { display: flex; gap: 6px; }
  .btn-toggle { flex: 1; padding: 7px; font-size: 12px; border-radius: 6px; background: var(--surface); color: var(--text-secondary); border: 1px solid var(--border); cursor: pointer; }
  .btn-toggle.active { background: var(--accent); color: #fff; border-color: var(--accent); }
  .btn-toggle.small { padding: 5px 8px; font-size: 11px; flex: none; }
  .sync-pass-row { display: flex; gap: 6px; align-items: center; }
  .sync-pass-row .input { flex: 1; }
  .interval-row { display: flex; align-items: center; gap: 8px; }
  .input.interval { width: 90px; }
  .hint { font-size: 11px; color: var(--text-muted); }
  .input { width: 100%; padding: 8px 10px; font-size: 13px; border-radius: 6px; background: var(--surface); border: 1px solid var(--border); color: var(--text); }
  .input:focus { border-color: var(--accent); outline: none; }
  .status-line { display: flex; flex-wrap: wrap; gap: 6px; align-items: center; font-size: 11px; color: var(--text-muted); }
  .dot { width: 8px; height: 8px; border-radius: 50%; background: var(--text-muted); display: inline-block; }
  .dot.on { background: var(--green); }
  .dot.err { background: var(--red); }
  .conflict { color: var(--red); }
  .btn { padding: 8px 14px; font-size: 13px; border-radius: 6px; flex: 1; }
  .btn-primary { background: var(--accent); color: #fff; }
  .btn-primary:hover { opacity: 0.9; }
  .btn-primary:disabled { opacity: 0.4; cursor: not-allowed; }
  .btn-ghost { background: var(--surface); color: var(--text-secondary); border: 1px solid var(--border); }
  .btn-ghost:hover { border-color: var(--accent); color: var(--accent); }
  .btn-ghost:disabled { opacity: 0.4; cursor: not-allowed; }
  .btn-danger { background: rgba(239,68,68,0.15); color: var(--red); }
  .btn-danger:hover { background: rgba(239,68,68,0.25); }
  .status-msg { font-size: 12px; padding: 6px 10px; border-radius: 6px; white-space: pre-wrap; word-break: break-all; }
  .status-msg.ok { color: var(--green); background: rgba(16,185,129,0.1); }
  .status-msg.err { color: var(--red); background: rgba(239,68,68,0.1); }
</style>
