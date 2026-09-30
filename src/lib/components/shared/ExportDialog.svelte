<script lang="ts">
  /**
   * ExportDialog — 导出报告
   *
   * 强制先询问：格式 + 保存位置；用户点「开始导出」才会写文件。
   * 结果分三种：已保存（含路径 + 打开）/ 已取消 / 失败（含重试）。
   */
  import { t } from '$lib/i18n';
  import { projects } from '$lib/stores/project';
  import { exportDialog, closeExportDialog } from '$lib/stores/export-dialog';
  import {
    exportReport,
    type ExportFormat,
    type ExportLocation,
    type ExportOutcome,
  } from '$lib/utils/html-export';
  import { isAndroidPlatform } from '$lib/utils/platform';
  import Icon from '$lib/components/shared/Icon.svelte';

  let format = $state<ExportFormat>('html');
  let location = $state<ExportLocation>('picker');
  let phase = $state<'choose' | 'working' | 'result'>('choose');
  let outcome = $state<ExportOutcome | null>(null);

  const project = $derived(
    $exportDialog.projectId ? ($projects.find((p) => p.id === $exportDialog.projectId) ?? null) : null
  );

  // 每次打开重置
  $effect(() => {
    if ($exportDialog.show) {
      format = $exportDialog.format;
      location = 'picker';
      phase = 'choose';
      outcome = null;
    }
  });

  async function start() {
    if (!project) return;
    phase = 'working';
    outcome = await exportReport(project, format, location);
    phase = 'result';
  }

  async function openSaved() {
    if (outcome?.kind !== 'saved') return;
    try {
      if (isAndroidPlatform && outcome.uri) {
        const A = await import('tauri-plugin-android-fs-api');
        await A.showViewFileAppChooser(outcome.uri as Parameters<typeof A.showViewFileAppChooser>[0]);
        return;
      }
      const { revealItemInDir } = await import('@tauri-apps/plugin-opener');
      await revealItemInDir(outcome.path);
    } catch (e) {
      console.warn('[Export] 打开失败:', e);
    }
  }

  function reset() {
    phase = 'choose';
    outcome = null;
  }
</script>

{#if $exportDialog.show}
  <!-- svelte-ignore a11y_no_static_element_interactions a11y_click_events_have_key_events -->
  <div class="ex-overlay" onclick={closeExportDialog} role="dialog" aria-modal="true" tabindex="-1">
    <!-- svelte-ignore a11y_no_static_element_interactions a11y_click_events_have_key_events -->
    <div class="ex-card" onclick={(e) => e.stopPropagation()}>
      <div class="ex-header">
        <span class="ex-icon"><Icon name="upload" size={18} /></span>
        <span class="ex-title">{$t('export.dialogTitle')}</span>
        <button class="ex-close" onclick={closeExportDialog} aria-label="close">
          <Icon name="close" size={16} />
        </button>
      </div>

      {#if phase === 'choose'}
        <div class="ex-body">
          <div class="ex-row">
            <span class="ex-label">{$t('export.format')}</span>
            <div class="ex-opts">
              <button class="ex-opt" class:active={format === 'pdf'} onclick={() => (format = 'pdf')}>PDF</button>
              <button class="ex-opt" class:active={format === 'html'} onclick={() => (format = 'html')}>HTML</button>
            </div>
          </div>

          <div class="ex-row">
            <span class="ex-label">{$t('export.location')}</span>
            <div class="ex-opts col">
              <button class="ex-opt wide" class:active={location === 'picker'} onclick={() => (location = 'picker')}>
                <Icon name="folder" size={14} /> {$t('export.locPicker')}
              </button>
              <button class="ex-opt wide" class:active={location === 'downloads'} onclick={() => (location = 'downloads')}>
                <Icon name="download" size={14} /> {$t('export.locDownloads')}
              </button>
            </div>
          </div>

          {#if project}
            <div class="ex-file">{project.name}.{format}</div>
          {/if}
          {#if format === 'pdf'}
            <div class="ex-hint">{$t('export.pdfHint')}</div>
          {/if}
        </div>

        <div class="ex-footer">
          <button class="ex-btn ghost" onclick={closeExportDialog}>{$t('export.cancel')}</button>
          <button class="ex-btn primary" onclick={start} disabled={!project}>{$t('export.start')}</button>
        </div>
      {:else if phase === 'working'}
        <div class="ex-body center">
          <div class="ex-spinner"></div>
          <span class="ex-hint">{$t('export.working')}</span>
        </div>
      {:else if outcome?.kind === 'saved'}
        <div class="ex-body">
          <div class="ex-ok"><Icon name="check" size={16} /> {$t('export.savedTitle')}</div>
          <div class="ex-path">{outcome.path}</div>
        </div>
        <div class="ex-footer">
          <button class="ex-btn ghost" onclick={closeExportDialog}>{$t('export.close')}</button>
          <button class="ex-btn primary" onclick={openSaved}>
            {isAndroidPlatform ? $t('export.open') : $t('export.reveal')}
          </button>
        </div>
      {:else if outcome?.kind === 'cancelled'}
        <div class="ex-body">
          <div class="ex-warn"><Icon name="ban" size={16} /> {$t('export.cancelledTitle')}</div>
        </div>
        <div class="ex-footer">
          <button class="ex-btn ghost" onclick={closeExportDialog}>{$t('export.close')}</button>
          <button class="ex-btn primary" onclick={reset}>{$t('export.retry')}</button>
        </div>
      {:else}
        <div class="ex-body">
          <div class="ex-err"><Icon name="alert-triangle" size={16} /> {$t('export.errorTitle')}</div>
          <div class="ex-msg">{outcome?.kind === 'error' ? outcome.message : ''}</div>
          {#if format === 'pdf'}
            <div class="ex-hint">{$t('export.pdfHint')}</div>
          {/if}
        </div>
        <div class="ex-footer">
          <button class="ex-btn ghost" onclick={closeExportDialog}>{$t('export.close')}</button>
          <button class="ex-btn primary" onclick={reset}>{$t('export.retry')}</button>
        </div>
      {/if}
    </div>
  </div>
{/if}

<style>
  .ex-overlay {
    position: fixed;
    inset: 0;
    z-index: 2200;
    display: flex;
    align-items: center;
    justify-content: center;
    background: rgba(0, 0, 0, 0.45);
  }
  .ex-card {
    width: 440px;
    max-width: 92vw;
    background: var(--bg);
    border: 1px solid var(--border);
    border-radius: 12px;
    box-shadow: 0 8px 32px rgba(0, 0, 0, 0.3);
    display: flex;
    flex-direction: column;
    overflow: hidden;
  }
  .ex-header {
    display: flex;
    align-items: center;
    gap: 10px;
    padding: 14px 18px;
    border-bottom: 1px solid var(--border);
  }
  .ex-icon { font-size: 18px; }
  .ex-title { flex: 1; font-size: 15px; font-weight: 600; color: var(--text); }
  .ex-close { background: none; border: none; color: var(--text-muted); cursor: pointer; padding: 2px; }
  .ex-body { display: flex; flex-direction: column; gap: 14px; padding: 18px; }
  .ex-body.center { align-items: center; justify-content: center; padding: 32px 18px; gap: 12px; }
  .ex-row { display: flex; flex-direction: column; gap: 8px; }
  .ex-label { font-size: 11px; text-transform: uppercase; letter-spacing: 0; color: var(--text-muted); }
  .ex-opts { display: flex; gap: 8px; }
  .ex-opts.col { flex-direction: column; }
  .ex-opt {
    flex: 1;
    display: inline-flex;
    align-items: center;
    gap: 6px;
    padding: 8px 10px;
    font-size: 13px;
    border-radius: 8px;
    background: var(--surface);
    color: var(--text-secondary);
    border: 1px solid var(--border);
    cursor: pointer;
    text-align: center;
  }
  .ex-opt.wide { text-align: left; justify-content: flex-start; }
  .ex-opt.active { background: var(--accent); color: #fff; border-color: var(--accent); }
  .ex-file { font-size: 12px; color: var(--text-muted); }
  .ex-hint { font-size: 11px; color: var(--text-muted); line-height: 1.5; }
  .ex-footer {
    display: flex;
    justify-content: flex-end;
    gap: 8px;
    padding: 14px 18px;
    border-top: 1px solid var(--border);
  }
  .ex-btn {
    padding: 8px 16px;
    font-size: 13px;
    border-radius: 8px;
    border: 1px solid transparent;
    cursor: pointer;
  }
  .ex-btn:disabled { opacity: 0.5; cursor: not-allowed; }
  .ex-btn.primary { background: var(--accent); color: #fff; }
  .ex-btn.ghost { background: var(--surface); color: var(--text-secondary); border-color: var(--border); }
  .ex-ok { display: flex; align-items: center; gap: 6px; font-size: 15px; font-weight: 600; color: var(--green); }
  .ex-warn { display: flex; align-items: center; gap: 6px; font-size: 15px; font-weight: 600; color: var(--text-secondary); }
  .ex-err { display: flex; align-items: center; gap: 6px; font-size: 15px; font-weight: 600; color: var(--red); }
  .ex-path,
  .ex-msg {
    font-size: 12px;
    color: var(--text-secondary);
    word-break: break-all;
    background: var(--surface);
    border-radius: 8px;
    padding: 8px 10px;
  }
  .ex-spinner {
    width: 26px;
    height: 26px;
    border: 3px solid var(--border);
    border-top-color: var(--accent);
    border-radius: 50%;
    animation: ex-spin 0.8s linear infinite;
  }
  @keyframes ex-spin { to { transform: rotate(360deg); } }
</style>
