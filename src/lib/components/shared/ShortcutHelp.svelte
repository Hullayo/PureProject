<script lang="ts">
/**
 * ShortcutHelp — 快捷键帮助弹窗
 *
 * 显示应用的所有键盘快捷键列表。
 * 通过模态对话框展示，点击遮罩层或关闭按钮可关闭。
 *
 * @example
 * <ShortcutHelp bind:show={showShortcuts} />
 */

  import { t } from '$lib/i18n';
  import { animationLevel } from '$lib/stores/animation';
  import { fade, scale } from 'svelte/transition';

  let { show = $bindable(false) }: { show: boolean } = $props();

  const shortcutKeys = [
    { keys: ['Ctrl', 'N'], key: 'shortcut.newTask' },
    { keys: ['Ctrl', 'F'], key: 'shortcut.search' },
    { keys: ['Ctrl', 'Shift', 'F'], key: 'shortcut.globalSearch' },
    { keys: ['Ctrl', 'Z'], key: 'shortcut.undo' },
    { keys: ['Ctrl', 'Shift', 'Z'], key: 'shortcut.redo' },
    { keys: ['1', '-', '5'], key: 'shortcut.switchView' },
    { keys: ['Esc'], key: 'shortcut.closePanel' },
    { keys: ['?'], key: 'shortcut.showHelp' },
  ];
</script>

{#if show}
  <div class="overlay" onclick={() => show = false} onkeydown={(e) => { if (e.key === 'Escape') show = false; }} role="dialog" aria-modal="true" tabindex="-1" aria-label={$t('shortcut.title')} transition:fade={{ duration: $animationLevel === 'rich' ? 250 : 150 }}>
    <div class="card" onclick={e => e.stopPropagation()} transition:scale={{ duration: $animationLevel === 'rich' ? 250 : 150, start: $animationLevel === 'rich' ? 0.95 : 0.98 }}>
      <div class="card-header">
        <span class="card-title">{$t('shortcut.title')}</span>
        <button class="close-btn" aria-label={$t('shortcut.closePanel')} onclick={() => show = false}>&times;</button>
      </div>
      <div class="card-body">
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
    </div>
  </div>
{/if}

<style>
  .overlay {
    position: fixed;
    inset: 0;
    background: rgba(0, 0, 0, 0.5);
    display: flex;
    align-items: center;
    justify-content: center;
    z-index: 1000;
  }
  .card {
    background: var(--bg);
    border: 1px solid var(--border);
    border-radius: 12px;
    width: 360px;
    box-shadow: 0 8px 32px rgba(0, 0, 0, 0.3);
  }
  .card-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 16px 20px;
    border-bottom: 1px solid var(--border);
  }
  .card-title {
    font-size: 15px;
    font-weight: 600;
  }
  .close-btn {
    font-size: 20px;
    color: var(--text-muted);
    line-height: 1;
  }
  .close-btn:hover {
    color: var(--text);
  }
  .card-body {
    padding: 16px 20px;
    display: flex;
    flex-direction: column;
    gap: 10px;
  }
  .shortcut-row {
    display: flex;
    align-items: center;
    justify-content: space-between;
  }
  .keys {
    display: flex;
    align-items: center;
    gap: 4px;
  }
  kbd {
    display: inline-block;
    padding: 2px 8px;
    font-size: 12px;
    font-family: inherit;
    border-radius: 4px;
    background: var(--surface);
    border: 1px solid var(--border);
    color: var(--text-secondary);
  }
  .sep {
    font-size: 11px;
    color: var(--text-muted);
  }
  .desc {
    font-size: 13px;
    color: var(--text-secondary);
  }
</style>
