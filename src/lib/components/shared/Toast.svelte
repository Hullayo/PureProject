<script lang="ts">
  /** Toast — 操作结果提示（导出等异步动作的反馈） */
  import { toasts } from '$lib/stores/toast';
</script>

{#if $toasts.length > 0}
  <div class="toast-wrap">
    {#each $toasts as t (t.id)}
      <div class="toast {t.kind}">{t.text}</div>
    {/each}
  </div>
{/if}

<style>
  .toast-wrap {
    position: fixed;
    left: 50%;
    bottom: 24px;
    transform: translateX(-50%);
    z-index: 3000;
    display: flex;
    flex-direction: column;
    gap: 8px;
    align-items: center;
    pointer-events: none;
    max-width: 92vw;
  }
  .toast {
    padding: 10px 16px;
    border-radius: 8px;
    font-size: 13px;
    line-height: 1.5;
    color: #fff;
    background: #374151;
    box-shadow: 0 6px 24px rgba(0, 0, 0, 0.25);
    word-break: break-all;
    max-width: 640px;
  }
  .toast.ok { background: #059669; }
  .toast.err { background: #dc2626; }
</style>
