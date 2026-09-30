<script lang="ts">
/**
 * HtmlPreview — HTML 报告预览弹窗
 *
 * 在应用内直接渲染 HTML 报告，支持下载和打印。
 * 使用 srcdoc 属性嵌入 HTML，不依赖外部文件或跨窗口通信。
 */

    import Icon from '$lib/components/shared/Icon.svelte';
  import { t } from '$lib/i18n';
  import { animationLevel } from '$lib/stores/animation';
  import { closeHtmlPreview } from '$lib/stores/html-preview';
  import { openExportDialog } from '$lib/stores/export-dialog';
  import { fade, scale } from 'svelte/transition';

  let { show = false, title = '', html = '', autoPrint = false, projectId = null }: { show: boolean; title?: string; html?: string; autoPrint?: boolean; projectId?: string | null } = $props();

  /** 自动打印只触发一次 */
  let printed = false;

  /** 「下载 HTML」→ 打开导出对话框（先让用户选位置，绝不静默下载） */
  function handleDownload() {
    if (!projectId) return;
    openExportDialog(projectId, 'html');
  }

  /** 「导出 PDF」→ 同样先询问 */
  function handleExportPdf() {
    if (!projectId) return;
    openExportDialog(projectId, 'pdf');
  }

  /** 触发 iframe 内文档打印（走系统「另存为 PDF」） */
  function printFrame() {
    const iframe = document.querySelector('.preview-frame') as HTMLIFrameElement | null;
    if (!iframe) return;
    try {
      iframe.contentWindow?.postMessage('print', '*');
    } catch {
      try { iframe.contentWindow?.print(); } catch { /* ignore */ }
    }
  }

  function handleFrameLoad() {
    if (autoPrint && !printed) {
      printed = true;
      setTimeout(printFrame, 200);
    }
  }

  // 每次打开重置自动打印状态
  $effect(() => { if (show) printed = false; });

  function handleClose() {
    closeHtmlPreview();
  }

  function handleKeydown(e: KeyboardEvent) {
    if (e.key === 'Escape') handleClose();
  }

  // 给 iframe 内的文档注入打印触发脚本（$derived 确保 html 变化时重新计算）
  let previewHtml = $derived(html.replace('</body>', `
<script>
  window.addEventListener('message', function(e) {
    if (e.data === 'print') window.print();
  });
<\/script>
</body>`));
</script>

<svelte:window onkeydown={handleKeydown} />

{#if show}
  <!-- svelte-ignore a11y_no_static_element_interactions a11y_click_events_have_key_events a11y_interactive_supports_focus -->
  <div
    class="preview-overlay"
    onclick={handleClose}
    onkeydown={e => e.key === 'Escape' && handleClose()}
    role="dialog"
    aria-modal="true"
    tabindex="-1"
    transition:fade={{ duration: $animationLevel === 'rich' ? 200 : ($animationLevel === 'none' ? 0 : 120) }}
  >
    <!-- svelte-ignore a11y_no_static_element_interactions a11y_click_events_have_key_events -->
    <div
      class="preview-card"
      onclick={e => e.stopPropagation()}
      transition:scale={{ duration: $animationLevel === 'rich' ? 200 : ($animationLevel === 'none' ? 0 : 120), start: 0.95 }}
    >
      <div class="preview-header">
        <span class="preview-title">{title}</span>
        <div class="preview-actions-top">
          <button type="button" class="preview-btn preview-btn-dl" onclick={handleDownload}>
            <Icon name="download" size={14} /> {$t('preview.downloadHtml')}
          </button>
          <button type="button" class="preview-btn preview-btn-print" onclick={handleExportPdf}>
            <Icon name="printer" size={14} /> {$t('preview.exportPdf')}
          </button>
          <button type="button" class="preview-close" onclick={handleClose}>
            <Icon name="close" size={16} />
          </button>
        </div>
      </div>
      <div class="preview-body">
        <iframe
          class="preview-frame"
          srcdoc={previewHtml}
          sandbox="allow-scripts allow-modals"
          title="HTML preview"
          onload={handleFrameLoad}
        ></iframe>
      </div>
    </div>
  </div>
{/if}

<style>
  .preview-overlay {
    position: fixed;
    inset: 0;
    z-index: 1000;
    display: flex;
    align-items: center;
    justify-content: center;
    background: rgba(0,0,0,0.45);
  }
  .preview-card {
    width: 90vw;
    height: 85vh;
    max-width: 1200px;
    background: var(--bg);
    border: 1px solid var(--border);
    border-radius: 12px;
    box-shadow: 0 8px 32px rgba(0,0,0,0.3);
    display: flex;
    flex-direction: column;
    overflow: hidden;
  }
  .preview-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 12px 16px;
    border-bottom: 1px solid var(--border);
    background: var(--sidebar);
    flex-shrink: 0;
  }
  .preview-title {
    font-size: 14px;
    font-weight: 600;
    color: var(--text);
  }
  .preview-actions-top {
    display: flex;
    gap: 6px;
    align-items: center;
  }
  .preview-btn {
    display: flex;
    align-items: center;
    gap: 4px;
    padding: 6px 12px;
    font-size: 12px;
    border-radius: 6px;
    border: none;
    cursor: pointer;
    transition: all 0.15s;
  }
  .preview-btn-dl {
    background: var(--accent);
    color: #fff;
  }
  .preview-btn-dl:hover {
    opacity: 0.9;
  }
  .preview-btn-print {
    background: var(--surface);
    color: var(--text-secondary);
    border: 1px solid var(--border);
  }
  .preview-btn-print:hover {
    border-color: var(--accent);
    color: var(--accent);
  }
  .preview-close {
    padding: 4px;
    border-radius: 6px;
    background: none;
    border: none;
    color: var(--text-muted);
    cursor: pointer;
  }
  .preview-close:hover {
    background: var(--surface);
    color: var(--text);
  }
  .preview-body {
    flex: 1;
    overflow: hidden;
  }
  .preview-frame {
    width: 100%;
    height: 100%;
    border: none;
    background: #fff;
  }
</style>