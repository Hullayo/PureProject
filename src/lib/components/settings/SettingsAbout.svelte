<script lang="ts">
  import { onMount } from 'svelte';
  import { t } from '$lib/i18n';
  import { getVersion } from '@tauri-apps/api/app';
  import { showUpdateDialog, updateInfo } from '$lib/stores/update';
  import {
    checkForUpdate,
    buildManifestUrl,
    getUpdateSource,
    getUpdateServerHost,
    getUpdateServerPort,
    getUpdateGithubRepo,
    saveUpdateConfig,
    DEFAULT_UPDATE_HOST,
    DEFAULT_UPDATE_PORT,
    DEFAULT_GITHUB_REPO,
    type UpdateSource,
  } from '$lib/updater';

  let checking = $state(false);
  let checkResult = $state('');

  const shortcutKeys = [
    { keys: ['Ctrl', 'N'], key: 'shortcut.newTask' },
    { keys: ['Ctrl', 'F'], key: 'shortcut.search' },
    { keys: ['Ctrl', 'Z'], key: 'shortcut.undo' },
    { keys: ['Ctrl', 'Shift', 'Z'], key: 'shortcut.redo' },
    { keys: ['1', '-', '5'], key: 'shortcut.switchView' },
    { keys: ['Esc'], key: 'shortcut.closePanel' },
    { keys: ['?'], key: 'shortcut.showHelp' },
  ];

  let currentVersion = $state('');

  // ─── 更新源配置（服务器 / GitHub）────────────────────────────────────
  let updateSource = $state<UpdateSource>(getUpdateSource());
  let serverHost = $state(getUpdateServerHost());
  let serverPort = $state(getUpdateServerPort());
  let githubRepo = $state(getUpdateGithubRepo());

  const updateConfig = $derived({
    source: updateSource,
    host: serverHost,
    port: serverPort,
    repo: githubRepo,
  });
  /** 实时预览最终请求的清单地址，方便排查 */
  const manifestPreview = $derived(buildManifestUrl(updateConfig, 'desktop'));

  function setSource(s: UpdateSource) {
    updateSource = s;
    saveUpdateConfig({ source: s });
  }
  function saveHost() { saveUpdateConfig({ host: serverHost }); }
  function savePort() { saveUpdateConfig({ port: serverPort }); }
  function saveRepo() { saveUpdateConfig({ repo: githubRepo }); }
  function resetUpdateConfig() {
    updateSource = 'github';
    serverHost = DEFAULT_UPDATE_HOST;
    serverPort = DEFAULT_UPDATE_PORT;
    githubRepo = DEFAULT_GITHUB_REPO;
    saveUpdateConfig({
      source: 'github',
      host: DEFAULT_UPDATE_HOST,
      port: DEFAULT_UPDATE_PORT,
      repo: DEFAULT_GITHUB_REPO,
    });
  }

  onMount(async () => {
    try {
      currentVersion = await getVersion();
    } catch {
      currentVersion = '0.4.3';
    }
  });

  async function handleCheckUpdate() {
    checking = true;
    checkResult = '';
    try {
      const info = await checkForUpdate();
      if (info) {
        updateInfo.set(info);
        showUpdateDialog.set(true);
        checkResult = '发现新版本 v' + info.version;
      } else {
        checkResult = '已是最新版本';
      }
    } catch (e: unknown) {
      checkResult = typeof e === 'string' ? e : '检查更新失败';
    } finally {
      checking = false;
    }
  }
</script>

<div class="section">
  <span class="section-title">{$t('settings.version')}</span>
  <p class="version">v{currentVersion}</p>
</div>

<div class="section">
  <span class="section-title">更新</span>

  <div class="src-row">
    <button class="seg" class:active={updateSource === 'server'} onclick={() => setSource('server')}>服务器</button>
    <button class="seg" class:active={updateSource === 'github'} onclick={() => setSource('github')}>GitHub</button>
  </div>

  {#if updateSource === 'server'}
    <div class="field-row">
      <input class="text-input" placeholder="服务器地址" bind:value={serverHost} oninput={saveHost} />
      <input class="text-input port" placeholder="端口" bind:value={serverPort} oninput={savePort} inputmode="numeric" />
    </div>
  {:else}
    <input class="text-input" placeholder="owner/repo" bind:value={githubRepo} oninput={saveRepo} />
  {/if}

  <p class="manifest-preview">{manifestPreview}</p>

  <div class="btn-row">
    <button class="check-update-btn" onclick={handleCheckUpdate} disabled={checking}>
      {checking ? '检查中...' : '检查更新'}
    </button>
    <button class="ghost-btn" onclick={resetUpdateConfig}>恢复默认</button>
  </div>
  {#if checkResult}
    <p class="check-result">{checkResult}</p>
  {/if}
</div>

<div class="section">
  <span class="section-title">{$t('settings.shortcuts')}</span>
  {#each shortcutKeys as s}
    <div class="shortcut-row">
      <span class="keys">
        {#each s.keys as key, i}
          {#if i > 0}<span class="sep">+</span>{/if}
          <kbd>{key}</kbd>
        {/each}
      </span>
      <span class="desc">{$t(s.key)}</span>
    </div>
  {/each}
</div>

<style>
  .section { display: flex; flex-direction: column; gap: 8px; }
  .section-title { font-size: 11px; color: var(--text-muted); text-transform: uppercase; letter-spacing: 0; }
  .version { font-size: 16px; font-weight: 600; color: var(--text); }
  .check-update-btn {
    align-self: flex-start;
    padding: 6px 14px;
    font-size: 12px;
    border-radius: 6px;
    background: var(--accent);
    color: #fff;
    border: none;
    cursor: pointer;
  }
  .check-update-btn:hover:not(:disabled) { opacity: 0.9; }
  .check-update-btn:disabled { opacity: 0.5; cursor: not-allowed; }
  .btn-row { display: flex; align-items: center; gap: 8px; }
  .ghost-btn {
    padding: 6px 12px; font-size: 12px; border-radius: 6px;
    background: var(--surface); color: var(--text-secondary); border: 1px solid var(--border); cursor: pointer;
  }
  .ghost-btn:hover { color: var(--text); }
  .src-row { display: flex; gap: 6px; }
  .seg {
    flex: 1; padding: 6px 10px; font-size: 12px; border-radius: 6px; cursor: pointer;
    background: var(--surface); color: var(--text-secondary); border: 1px solid var(--border);
  }
  .seg.active { background: var(--accent-light); color: var(--accent); border-color: var(--accent); }
  .field-row { display: flex; gap: 6px; }
  .text-input {
    flex: 1; min-width: 0; padding: 6px 10px; font-size: 12px; border-radius: 6px;
    background: var(--surface); border: 1px solid var(--border); color: var(--text);
  }
  .text-input:focus { outline: none; border-color: var(--accent); }
  .text-input.port { flex: 0 0 84px; }
  .manifest-preview {
    font-size: 11px; color: var(--text-muted); word-break: break-all;
    background: var(--surface); border-radius: 6px; padding: 5px 8px;
  }
  .check-result { font-size: 12px; color: var(--text-secondary); }
  .shortcut-row { display: flex; align-items: center; justify-content: space-between; padding: 4px 0; }
  .keys { display: flex; align-items: center; gap: 4px; }
  kbd { display: inline-block; padding: 2px 8px; font-size: 12px; font-family: inherit; border-radius: 4px; background: var(--surface); border: 1px solid var(--border); color: var(--text-secondary); }
  .sep { font-size: 11px; color: var(--text-muted); }
  .desc { font-size: 13px; color: var(--text-secondary); }
</style>
