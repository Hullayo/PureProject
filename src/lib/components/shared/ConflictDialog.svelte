<script lang="ts">
  /**
   * ConflictDialog — 同步冲突处理
   *
   * 冲突策略为「服务器优先 + 提示用户」：双方都改动时弹出，
   * 默认推荐「使用服务器版本」，也允许用户保留本地并覆盖服务器。
   */
  import { syncConflicts, resolveConflict } from '$lib/sync';
  import Icon from '$lib/components/shared/Icon.svelte';

  let busy = $state(false);
  let errorMessage = $state('');

  async function choose(id: string, choice: 'server' | 'local') {
    busy = true;
    errorMessage = '';
    try {
      await resolveConflict(id, choice);
    } catch (error) {
      errorMessage = error instanceof Error ? error.message : String(error);
    } finally {
      busy = false;
    }
  }

  function fmt(iso: string | null | undefined): string {
    if (!iso) return '—';
    try { return new Date(iso).toLocaleString(); } catch { return iso; }
  }
</script>

{#if $syncConflicts.length > 0}
  <div class="overlay" role="dialog" aria-modal="true">
    <div class="card">
      <div class="header">
        <span class="icon"><Icon name="alert-triangle" size={20} /></span>
        <div>
          <div class="title">同步冲突</div>
          <div class="subtitle">本地与服务器都发生了修改（{ $syncConflicts.length } 个项目）</div>
        </div>
      </div>

      <div class="body">
        {#each $syncConflicts as c (c.id)}
          <div class="item">
            <div class="item-name">{c.name}</div>
            <div class="item-meta">
              <span>本地: {fmt(c.localUpdatedAt)}</span>
              <span>服务器: {fmt(c.serverUpdatedAt)}</span>
              <span>数据结构: 本地 v{c.localSchemaVersion} / 服务器 v{c.serverSchemaVersion}</span>
            </div>
            {#if c.reason === 'schema_downgrade'}
              <div class="item-warn">服务器拒绝低版本数据覆盖高版本项目。请使用服务器版本或升级产生本地数据的客户端。</div>
            {/if}
            {#if c.serverDeleted}
              <div class="item-warn">服务器已删除该项目；选择「服务器版本」将移除本地副本（删除前会自动备份）。</div>
            {/if}
            <div class="item-actions">
              <button class="btn btn-primary" disabled={busy} onclick={() => choose(c.id, 'server')}>
                {c.serverDeleted ? '接受删除（移除本地）' : '使用服务器版本（推荐）'}
              </button>
              <button class="btn btn-ghost" disabled={busy || c.localSchemaVersion < c.serverSchemaVersion} onclick={() => choose(c.id, 'local')}>
                保留本地并覆盖服务器
              </button>
            </div>
          </div>
        {/each}
        {#if errorMessage}<div class="item-warn">{errorMessage}</div>{/if}
      </div>

      <div class="footer">冲突未处理前，该项目的自动同步会暂停。</div>
    </div>
  </div>
{/if}

<style>
  .overlay {
    position: fixed;
    inset: 0;
    z-index: 2100;
    display: flex;
    align-items: center;
    justify-content: center;
    background: rgba(0, 0, 0, 0.45);
  }
  .card {
    width: 460px;
    max-width: 92vw;
    max-height: 80vh;
    display: flex;
    flex-direction: column;
    background: var(--bg);
    border: 1px solid var(--border);
    border-radius: 12px;
    box-shadow: 0 8px 32px rgba(0, 0, 0, 0.3);
    overflow: hidden;
  }
  .header {
    display: flex;
    align-items: center;
    gap: 12px;
    padding: 16px 20px;
    border-bottom: 1px solid var(--border);
  }
  .icon { font-size: 22px; }
  .title { font-size: 15px; font-weight: 600; color: var(--text); }
  .subtitle { font-size: 12px; color: var(--text-muted); margin-top: 2px; }
  .body { padding: 16px 20px; overflow-y: auto; display: flex; flex-direction: column; gap: 14px; }
  .item { border: 1px solid var(--border); border-radius: 8px; padding: 12px; }
  .item-name { font-size: 14px; font-weight: 600; color: var(--text); }
  .item-meta {
    display: flex;
    flex-direction: column;
    gap: 2px;
    margin: 6px 0 10px;
    font-size: 11px;
    color: var(--text-muted);
  }
  .item-actions { display: flex; flex-direction: column; gap: 6px; }
  .item-warn { font-size: 11px; color: var(--red, #ef4444); margin: 0 0 8px; line-height: 1.5; }
  .btn {
    padding: 8px 12px;
    font-size: 13px;
    border-radius: 6px;
    border: 1px solid transparent;
    cursor: pointer;
  }
  .btn:disabled { opacity: 0.5; cursor: not-allowed; }
  .btn-primary { background: var(--accent); color: #fff; }
  .btn-ghost { background: var(--surface); color: var(--text-secondary); border-color: var(--border); }
  .footer { padding: 12px 20px; border-top: 1px solid var(--border); font-size: 11px; color: var(--text-muted); }
</style>
