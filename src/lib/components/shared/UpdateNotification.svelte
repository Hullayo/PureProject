<script lang="ts">
  /**
   * UpdateNotification — 更新通知组件
   *
   * 桌面端：走 tauri-plugin-updater，下载后自动安装并重启。
   * Android：走自定义命令下载 APK，提示用户手动安装。
   */

  import { listen } from '@tauri-apps/api/event';
  import { onMount, onDestroy } from 'svelte';
  import {
    showUpdateDialog,
    updateInfo,
    downloading,
    downloadProgress,
    downloadPath,
    installerLaunched,
    updateError
  } from '$lib/stores/update';
  import { installUpdate, isDesktopPlatform } from '$lib/updater';
  import { isTauriEnv } from '$lib/utils/platform';
  import Icon from '$lib/components/shared/Icon.svelte';

  const desktop = isDesktopPlatform();

  let unlisten: (() => void) | undefined;

  onMount(() => {
    if (!isTauriEnv) return;

    // Android 自定义下载通过事件上报进度
    listen<{ downloaded: number; total: number; percent: number }>(
      'update:download-progress',
      (event) => {
        downloadProgress.set(event.payload.percent);
      }
    ).then((fn) => {
      unlisten = fn;
    });
  });

  onDestroy(() => {
    unlisten?.();
  });

  async function startDownload() {
    if (!$updateInfo) return;
    downloading.set(true);
    updateError.set('');
    downloadProgress.set(0);
    try {
      const result = await installUpdate((percent) => downloadProgress.set(percent));
      if (result.kind === 'downloaded') {
        downloadPath.set(result.path ?? '');
      } else if (result.kind === 'installer-launched') {
        installerLaunched.set(true);
      }
      // kind === 'installed'：桌面端即将重启，无需额外处理
      downloading.set(false);
    } catch (e: unknown) {
      updateError.set(typeof e === 'string' ? e : '下载失败，请稍后重试');
      downloading.set(false);
    }
  }

  function close() {
    showUpdateDialog.set(false);
    updateInfo.set(null);
    downloading.set(false);
    downloadProgress.set(0);
    downloadPath.set('');
    installerLaunched.set(false);
    updateError.set('');
  }
</script>

{#if $showUpdateDialog && $updateInfo}
  <!-- svelte-ignore a11y_no_static_element_interactions a11y_click_events_have_key_events -->
  <div class="update-overlay" onclick={close} role="dialog" aria-modal="true" tabindex="-1">
    <!-- svelte-ignore a11y_no_static_element_interactions a11y_click_events_have_key_events -->
    <div class="update-card" onclick={e => e.stopPropagation()}>
      <div class="update-header">
        <span class="update-icon">📦</span>
        <div class="update-title-group">
          <span class="update-title">发现新版本</span>
          <span class="update-version">v{$updateInfo.version}</span>
        </div>
        <button class="update-close" onclick={close}>✕</button>
      </div>

      <div class="update-body">
        {#if $updateInfo.notes}
          <div class="update-release-notes">
            {#each $updateInfo.notes.split('\n') as line}
              <p>{line}</p>
            {/each}
          </div>
        {/if}

        {#if $updateError}
          <div class="update-error">{$updateError}</div>
        {/if}

        {#if $installerLaunched}
          <div class="update-done">
            <p><Icon name="check" size={14} /> 已打开系统安装界面</p>
            <p class="update-hint">请点击「安装」完成更新（Android 需用户确认）。</p>
          </div>
        {:else if $downloadPath}
          <div class="update-done">
            <p><Icon name="check" size={14} /> 下载完成！</p>
            <p class="update-hint">请在文件管理器中找到已下载的文件进行安装。</p>
            <p class="update-hint">{$downloadPath}</p>
          </div>
        {:else if $downloading}
          <div class="update-progress-section">
            <div class="update-progress-bar">
              <div class="update-progress-fill" style="width:{$downloadProgress}%"></div>
            </div>
            <span class="update-progress-text">{$downloadProgress}%</span>
          </div>
        {:else}
          <button class="update-download-btn" onclick={startDownload}>
            {desktop ? '下载并安装' : '下载更新'}
          </button>
        {/if}
      </div>

      {#if !$downloading && !$downloadPath && !$installerLaunched}
        <div class="update-footer">
          <button class="update-later-btn" onclick={close}>稍后再说</button>
        </div>
      {/if}
    </div>
  </div>
{/if}

<style>
  .update-overlay {
    position: fixed;
    inset: 0;
    z-index: 2000;
    display: flex;
    align-items: center;
    justify-content: center;
    background: rgba(0, 0, 0, 0.45);
  }
  .update-card {
    width: 400px;
    max-width: 90vw;
    background: var(--bg);
    border: 1px solid var(--border);
    border-radius: 12px;
    box-shadow: 0 8px 32px rgba(0, 0, 0, 0.3);
    display: flex;
    flex-direction: column;
    overflow: hidden;
  }
  .update-header {
    display: flex;
    align-items: center;
    gap: 12px;
    padding: 16px 20px;
    border-bottom: 1px solid var(--border);
  }
  .update-icon { font-size: 24px; }
  .update-title-group { flex: 1; }
  .update-title { font-size: 15px; font-weight: 600; color: var(--text); }
  .update-version { font-size: 12px; color: var(--accent); font-weight: 600; }
  .update-close {
    padding: 4px 8px;
    font-size: 14px;
    border-radius: 6px;
    background: none;
    border: none;
    color: var(--text-muted);
    cursor: pointer;
  }
  .update-close:hover { background: var(--surface); color: var(--text); }
  .update-body {
    display: flex;
    flex-direction: column;
    gap: 12px;
    padding: 20px;
  }
  .update-release-notes {
    font-size: 13px;
    color: var(--text-secondary);
    line-height: 1.6;
    max-height: 200px;
    overflow-y: auto;
  }
  .update-release-notes p { margin: 0; }
  .update-release-notes p + p { margin-top: 4px; }
  .update-error {
    padding: 8px 12px;
    font-size: 12px;
    color: var(--red);
    background: rgba(239, 68, 68, 0.1);
    border-radius: 6px;
  }
  .update-done { font-size: 13px; color: var(--green); }
  .update-hint { font-size: 12px; color: var(--text-muted); margin-top: 4px; word-break: break-all; }
  .update-progress-section {
    display: flex;
    align-items: center;
    gap: 12px;
  }
  .update-progress-bar {
    flex: 1;
    height: 8px;
    background: var(--surface);
    border-radius: 4px;
    overflow: hidden;
  }
  .update-progress-fill {
    height: 100%;
    background: var(--accent);
    border-radius: 4px;
    transition: width 0.3s;
  }
  .update-progress-text {
    font-size: 12px;
    color: var(--text-muted);
    min-width: 50px;
    text-align: right;
  }
  .update-download-btn {
    width: 100%;
    padding: 10px;
    font-size: 14px;
    border-radius: 8px;
    background: var(--accent);
    color: #fff;
    border: none;
    cursor: pointer;
    text-align: center;
  }
  .update-download-btn:hover { opacity: 0.9; }
  .update-footer {
    display: flex;
    justify-content: flex-end;
    padding: 12px 20px;
    border-top: 1px solid var(--border);
  }
  .update-later-btn {
    padding: 6px 16px;
    font-size: 13px;
    border-radius: 6px;
    background: var(--surface);
    color: var(--text-secondary);
    border: none;
    cursor: pointer;
  }
  .update-later-btn:hover { background: var(--border); }

  @media (max-width: 768px) {
    .update-card { width: 100%; max-width: 100%; border-radius: 0; max-height: 100vh; }
  }
</style>
