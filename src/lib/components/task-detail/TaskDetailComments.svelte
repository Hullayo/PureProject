<script lang="ts">
  import { activeProject, activeTask, addComment, updateComment, deleteComment } from '$lib/stores';
  import { t, locale } from '$lib/i18n';
  import { animationLevel } from '$lib/stores/animation';
  import { fade } from 'svelte/transition';

  const dateLocale = $derived($locale === 'zh' ? 'zh-CN' : 'en-US');

  let newComment = $state('');
  let editingCommentId = $state<string | null>(null);
  let editingContent = $state('');

  function handleAddComment() {
    if (!$activeProject || !$activeTask || !newComment.trim()) return;
    addComment($activeProject.id, $activeTask.id, newComment.trim());
    newComment = '';
  }
  function startEditComment(commentId: string, content: string) {
    editingCommentId = commentId;
    editingContent = content;
  }
  function handleSaveComment() {
    if (!$activeProject || !$activeTask || !editingCommentId || !editingContent.trim()) return;
    updateComment($activeProject.id, $activeTask.id, editingCommentId, editingContent.trim());
    editingCommentId = null;
    editingContent = '';
  }
  function handleDeleteComment(commentId: string) {
    if ($activeProject && $activeTask) deleteComment($activeProject.id, $activeTask.id, commentId);
  }
</script>

{#if $activeTask}
  <span class="label">{$t('detail.comments')} ({$activeTask.comments.length})</span>
  {#if $activeTask.comments.length > 0}
    <div class="comment-list">
      {#each $activeTask.comments as comment (comment.id)}
        <div class="comment-item" in:fade={{ duration: $animationLevel === 'none' ? 0 : 150 }}>
          {#if editingCommentId === comment.id}
            <textarea class="comment-edit-input" bind:value={editingContent}></textarea>
            <div class="comment-edit-actions">
              <button class="comment-save-btn" onclick={handleSaveComment}>{$t('detail.save')}</button>
              <button class="comment-cancel-btn" onclick={() => { editingCommentId = null; editingContent = ''; }}>{$t('detail.cancel')}</button>
            </div>
          {:else}
            <p class="comment-content">{comment.content}</p>
            <div class="comment-meta">
              <span class="comment-time">{new Date(comment.created_at).toLocaleString(dateLocale)}</span>
              <div class="comment-actions">
                <button class="comment-action-btn" onclick={() => startEditComment(comment.id, comment.content)}>{$t('detail.edit')}</button>
                <button class="comment-action-btn danger" onclick={() => handleDeleteComment(comment.id)}>{$t('detail.delete')}</button>
              </div>
            </div>
          {/if}
        </div>
      {/each}
    </div>
  {:else}
    <p class="comment-empty">{$t('detail.noComments')}</p>
  {/if}
  <div class="comment-add">
    <textarea class="comment-input" placeholder={$t('detail.addComment')} bind:value={newComment} onkeydown={e => { if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); handleAddComment(); } }}></textarea>
    <button class="comment-send-btn" onclick={handleAddComment} disabled={!newComment.trim()}>{$t('detail.send')}</button>
  </div>
{/if}

<style>
  .label { font-size: 11px; color: var(--text-muted); text-transform: uppercase; letter-spacing: 0; margin-bottom: -8px; }
  .comment-list { display: flex; flex-direction: column; gap: 8px; }
  .comment-item { padding: 8px 10px; background: var(--surface); border: 1px solid var(--border); border-radius: 8px; }
  .comment-content { font-size: 13px; color: var(--text); line-height: 1.5; margin: 0; white-space: pre-wrap; word-break: break-word; }
  .comment-meta { display: flex; align-items: center; justify-content: space-between; margin-top: 6px; }
  .comment-time { font-size: 11px; color: var(--text-muted); }
  .comment-actions { display: flex; gap: 8px; }
  .comment-action-btn { font-size: 11px; color: var(--text-muted); padding: 1px 4px; }
  .comment-action-btn:hover { color: var(--accent); }
  .comment-action-btn.danger:hover { color: var(--red); }
  .comment-empty { font-size: 12px; color: var(--text-muted); }
  .comment-add { display: flex; flex-direction: column; gap: 6px; }
  .comment-input { width: 100%; padding: 8px 10px; font-size: 13px; border-radius: 6px; background: var(--surface); border: 1px solid var(--border); color: var(--text); resize: vertical; min-height: 56px; }
  .comment-input:focus { border-color: var(--accent); }
  .comment-input::placeholder { color: var(--text-muted); }
  .comment-send-btn { align-self: flex-end; padding: 5px 14px; font-size: 12px; border-radius: 6px; background: var(--accent); color: #fff; }
  .comment-send-btn:disabled { opacity: 0.4; cursor: not-allowed; }
  .comment-edit-input { width: 100%; padding: 6px 8px; font-size: 13px; border-radius: 6px; background: var(--bg); border: 1px solid var(--accent); color: var(--text); resize: vertical; min-height: 48px; }
  .comment-edit-input:focus { outline: none; }
  .comment-edit-actions { display: flex; gap: 6px; margin-top: 6px; justify-content: flex-end; }
  .comment-save-btn { padding: 3px 10px; font-size: 11px; border-radius: 4px; background: var(--accent); color: #fff; }
  .comment-cancel-btn { padding: 3px 10px; font-size: 11px; border-radius: 4px; background: var(--surface); color: var(--text-muted); }
</style>
