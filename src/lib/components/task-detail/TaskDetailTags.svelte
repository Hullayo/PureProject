<script lang="ts">
  import { activeProject, activeTask, updateTask } from '$lib/stores';
  import Icon from '$lib/components/shared/Icon.svelte';
  import { t } from '$lib/i18n';
  import { animationLevel } from '$lib/stores/animation';
  import { fade } from 'svelte/transition';

  let newTag = $state('');

  function addTag() {
    if (!$activeProject || !$activeTask || !newTag.trim() || $activeTask.tags.includes(newTag.trim())) return;
    updateTask($activeProject.id, $activeTask.id, { tags: [...$activeTask.tags, newTag.trim()] });
    newTag = '';
  }

  function rmTag(tag: string) {
    if ($activeProject && $activeTask) updateTask($activeProject.id, $activeTask.id, { tags: $activeTask.tags.filter(t => t !== tag) });
  }
</script>

{#if $activeTask}
  <span class="label">{$t('detail.tags')}</span>
  <div class="tag-list">
    {#each $activeTask.tags as tag (tag)}
      <span class="tag-item" in:fade={{ duration: $animationLevel === 'none' ? 0 : 120 }}>{tag}<button class="tag-x" aria-label={$t('detail.delete')} onclick={() => rmTag(tag)}><Icon name="close" size={10} /></button></span>
    {/each}
  </div>
  <div class="tag-add">
    <input class="tag-input" placeholder={$t('detail.tagPlaceholder')} bind:value={newTag} onkeydown={e => e.key === 'Enter' && addTag()} />
    <button class="tag-add-btn" onclick={addTag}>+</button>
  </div>
{/if}

<style>
  .label { font-size: 11px; color: var(--text-muted); text-transform: uppercase; letter-spacing: 0; margin-bottom: -8px; }
  .tag-list { display: flex; flex-wrap: wrap; gap: 6px; }
  .tag-item { display: flex; align-items: center; gap: 4px; padding: 3px 10px; font-size: 12px; border-radius: 999px; background: var(--accent-light); color: var(--accent); }
  .tag-x { font-size: 14px; line-height: 1; }
  .tag-x:hover { color: #fff; }
  .tag-add { display: flex; gap: 4px; }
  .tag-input { flex: 1; padding: 5px 8px; font-size: 12px; border-radius: 6px; background: var(--surface); border: 1px solid var(--border); color: var(--text); }
  .tag-input:focus { border-color: var(--accent); }
  .tag-input::placeholder { color: var(--text-muted); }
  .tag-add-btn { padding: 5px 10px; font-size: 12px; border-radius: 6px; background: var(--accent); color: #fff; }
</style>
