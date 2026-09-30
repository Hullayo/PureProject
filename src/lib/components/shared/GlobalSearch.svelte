<script lang="ts">
/**
 * GlobalSearch — 全局搜索面板（项目 / 任务 / 子任务）
 *
 * 与 `CommandPalette`（命令面板）职责不同：这里只搜索**业务数据**。
 * 可在任意视图通过顶栏按钮或 `Ctrl+Shift+F` 唤出。
 *
 * 交互：
 * - `↑` / `↓` 选择，`Enter` 跳转，`Esc` 关闭；鼠标 hover 同步选中
 * - 点击/回车：`activeProjectId.set()`；结果带任务 ID 时再 `activeTaskId.set()`，然后关闭
 * - 关键词高亮；已完成的任务/子任务加删除线
 *
 * 数据全部来自 `$lib/stores/search`（`$searchResults` 是基于 `projects` 的派生 store，
 * 项目变化会自动刷新），组件自身不读写 localStorage。
 *
 * @example
 * <GlobalSearch />
 */

  import { t } from '$lib/i18n';
  import { activeProjectId, setActiveTaskGroup, selectTask } from '$lib/stores';
  import {
    closeSearch,
    searchKeyword,
    searchOpen,
    searchResults,
    type SearchResult,
    type SearchResultType,
  } from '$lib/stores/search';
  import { animationLevel } from '$lib/stores/animation';
  import { fade, fly } from 'svelte/transition';

  let inputEl = $state<HTMLInputElement | null>(null);
  let listEl = $state<HTMLDivElement | null>(null);
  let selected = $state(0);

  /** 扁平结果（键盘选择按此顺序） */
  const results = $derived($searchResults);

  /** 按类型分组（顺序：项目 → 任务 → 子任务）；空组不渲染 */
  const groups = $derived.by(() => {
    const order: SearchResultType[] = ['project', 'task_group', 'task', 'subtask'];
    const buckets: Record<SearchResultType, SearchResult[]> = { project: [], task_group: [], task: [], subtask: [] };
    for (const r of results) buckets[r.type].push(r);
    return order
      .map((type) => ({ type, items: buckets[type] }))
      .filter((g) => g.items.length > 0);
  });

  /** id → 扁平下标，供分组渲染时定位选中态 */
  const indexById = $derived.by(() => {
    const map = new Map<string, number>();
    results.forEach((r, i) => map.set(r.id, i));
    return map;
  });

  const typeLabelKey: Record<SearchResultType, string> = {
    project: 'search.type.project',
    task_group: 'search.type.taskGroup',
    task: 'search.type.task',
    subtask: 'search.type.subtask',
  };

  /** 打开时聚焦输入框 */
  $effect(() => {
    if ($searchOpen) {
      requestAnimationFrame(() => inputEl?.focus());
    }
  });

  /** 关键词变化（或结果数量变化）时把选中重置到第一条 */
  $effect(() => {
    void $searchKeyword;
    selected = 0;
  });

  /** 结果变少时避免选中越界 */
  $effect(() => {
    if (selected >= results.length) selected = 0;
  });

  function scrollToSelected() {
    if (!listEl) return;
    const el = listEl.querySelector('.gs-item.active') as HTMLElement | null;
    el?.scrollIntoView({ block: 'nearest' });
  }

  function move(delta: number) {
    if (results.length === 0) return;
    selected = (selected + delta + results.length) % results.length;
    scrollToSelected();
  }

  function handleKeydown(e: KeyboardEvent) {
    if (e.key === 'ArrowDown') {
      e.preventDefault();
      move(1);
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      move(-1);
    } else if (e.key === 'Enter') {
      e.preventDefault();
      const hit = results[selected];
      if (hit) jump(hit);
    } else if (e.key === 'Escape') {
      e.preventDefault();
      closeSearch();
    }
  }

  /** 跳转到结果所属项目（并选中任务）后关闭面板 */
  function jump(r: SearchResult) {
    if (r.taskId) selectTask(r.projectId, r.taskId);
    else {
      activeProjectId.set(r.projectId);
      if (r.taskGroupId) setActiveTaskGroup(r.projectId, r.taskGroupId);
    }
    closeSearch();
  }

  /** 关键词高亮：把文本切成命中/未命中片段，模板里用 <mark> 渲染（不用 innerHTML） */
  function highlight(text: string, keyword: string): { text: string; hit: boolean }[] {
    const q = (keyword ?? '').trim().toLowerCase();
    if (!q || !text) return [{ text, hit: false }];
    const lower = text.toLowerCase();
    const parts: { text: string; hit: boolean }[] = [];
    let i = 0;
    while (i < text.length) {
      const idx = lower.indexOf(q, i);
      if (idx === -1) {
        parts.push({ text: text.slice(i), hit: false });
        break;
      }
      if (idx > i) parts.push({ text: text.slice(i, idx), hit: false });
      parts.push({ text: text.slice(idx, idx + q.length), hit: true });
      i = idx + q.length;
    }
    return parts.filter((p) => p.text.length > 0);
  }
</script>

{#if $searchOpen}
  <!-- svelte-ignore a11y_no_static_element_interactions a11y_click_events_have_key_events a11y_interactive_supports_focus -->
  <div
    class="gs-overlay"
    onclick={closeSearch}
    onkeydown={(e) => e.key === 'Escape' && closeSearch()}
    role="dialog"
    aria-modal="true"
    aria-label={$t('search.placeholder')}
    tabindex="-1"
    transition:fade={{ duration: $animationLevel === 'rich' ? 220 : 130 }}
  >
    <!-- svelte-ignore a11y_click_events_have_key_events -->
    <div
      class="gs-panel"
      onclick={(e) => e.stopPropagation()}
      transition:fly={{ y: $animationLevel === 'rich' ? -20 : -10, duration: $animationLevel === 'rich' ? 220 : 130 }}
    >
      <div class="gs-search">
        <svg class="gs-icon" width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
          <circle cx="11" cy="11" r="8"/><line x1="21" y1="21" x2="16.65" y2="16.65"/>
        </svg>
        <input
          class="gs-input"
          placeholder={$t('search.placeholder')}
          bind:this={inputEl}
          bind:value={$searchKeyword}
          onkeydown={handleKeydown}
        />
        <span class="gs-esc">{$t('search.escHint')}</span>
      </div>

      <div class="gs-list" bind:this={listEl}>
        {#if !$searchKeyword.trim()}
          <div class="gs-empty">{$t('search.emptyHint')}</div>
        {:else if results.length === 0}
          <div class="gs-empty">{$t('search.noMatch')}</div>
        {:else}
          {#each groups as group (group.type)}
            <div class="gs-group-label">{$t(typeLabelKey[group.type])}</div>
            {#each group.items as r (r.id)}
              {@const idx = indexById.get(r.id) ?? 0}
              <!-- svelte-ignore a11y_no_static_element_interactions a11y_click_events_have_key_events a11y_interactive_supports_focus -->
              <div
                class="gs-item"
                class:active={idx === selected}
                onclick={() => jump(r)}
                onmouseenter={() => (selected = idx)}
                role="option"
                aria-selected={idx === selected}
                tabindex="-1"
              >
                <span class="gs-dot" style="background:{r.projectColor}"></span>
                <div class="gs-text">
                  <span class="gs-title" class:done={r.done}>
                    {#each highlight(r.title, $searchKeyword) as part}
                      {#if part.hit}<mark>{part.text}</mark>{:else}{part.text}{/if}
                    {/each}
                  </span>
                  {#if r.context}
                    <span class="gs-context">
                      {#each highlight(r.context, $searchKeyword) as part}
                        {#if part.hit}<mark>{part.text}</mark>{:else}{part.text}{/if}
                      {/each}
                    </span>
                  {/if}
                </div>
                <span class="gs-project">{r.projectName}</span>
              </div>
            {/each}
          {/each}
        {/if}
      </div>

      <div class="gs-footer">
        <span class="gs-footer-hint">{$t('search.navHint')}</span>
        <span class="gs-footer-open">{$t('search.openHint')}</span>
      </div>
    </div>
  </div>
{/if}

<style>
  .gs-overlay {
    position: fixed;
    inset: 0;
    background: rgba(0, 0, 0, 0.5);
    display: flex;
    align-items: flex-start;
    justify-content: center;
    padding-top: 12vh;
    z-index: 1100;
  }

  .gs-panel {
    width: 620px;
    max-width: calc(100vw - 24px);
    max-height: 70vh;
    background: var(--bg);
    border: 1px solid var(--border);
    border-radius: 12px;
    box-shadow: 0 16px 48px rgba(0, 0, 0, 0.3);
    display: flex;
    flex-direction: column;
    overflow: hidden;
  }

  .gs-search {
    display: flex;
    align-items: center;
    gap: 10px;
    padding: 14px 16px;
    border-bottom: 1px solid var(--border);
    flex-shrink: 0;
  }

  .gs-icon {
    color: var(--text-muted);
    flex-shrink: 0;
  }

  .gs-input {
    flex: 1;
    min-width: 0;
    font-size: 14px;
    background: none;
    border: none;
    outline: none;
    color: var(--text);
  }

  .gs-input::placeholder {
    color: var(--text-muted);
  }

  .gs-esc {
    font-size: 11px;
    color: var(--text-muted);
    padding: 2px 6px;
    background: var(--surface);
    border-radius: 4px;
    flex-shrink: 0;
  }

  .gs-list {
    flex: 1;
    overflow-y: auto;
    padding: 6px;
  }

  .gs-group-label {
    font-size: 10px;
    font-weight: 600;
    color: var(--text-muted);
    text-transform: uppercase;
    letter-spacing: 0;
    padding: 8px 10px 4px;
  }

  .gs-item {
    display: flex;
    align-items: center;
    gap: 10px;
    padding: 8px 10px;
    border-radius: 8px;
    cursor: pointer;
    transition: background 0.1s;
  }

  .gs-item:hover,
  .gs-item.active {
    background: var(--surface);
  }

  .gs-item.active {
    background: var(--accent-light);
  }

  .gs-dot {
    width: 8px;
    height: 8px;
    border-radius: 50%;
    flex-shrink: 0;
  }

  .gs-text {
    flex: 1;
    min-width: 0;
    display: flex;
    flex-direction: column;
    gap: 1px;
  }

  .gs-title {
    font-size: 13px;
    color: var(--text);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .gs-title.done {
    text-decoration: line-through;
    color: var(--text-muted);
  }

  .gs-context {
    font-size: 11px;
    color: var(--text-muted);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  mark {
    background: var(--accent-light);
    color: var(--accent);
    border-radius: 2px;
    padding: 0 1px;
  }

  .gs-project {
    font-size: 11px;
    color: var(--text-muted);
    flex-shrink: 0;
    max-width: 160px;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .gs-empty {
    padding: 32px 24px;
    text-align: center;
    font-size: 13px;
    color: var(--text-muted);
  }

  .gs-footer {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
    padding: 7px 14px;
    border-top: 1px solid var(--border);
    background: var(--surface);
    font-size: 11px;
    color: var(--text-muted);
    flex-shrink: 0;
  }

  .gs-footer-open {
    opacity: 0.75;
  }

  @media (max-width: 768px) {
    .gs-overlay { padding-top: 8vh; }
    .gs-footer-open { display: none; }
    .gs-project { max-width: 90px; }
  }
</style>
