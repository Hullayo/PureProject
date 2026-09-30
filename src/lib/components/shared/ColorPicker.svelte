<script lang="ts">
/**
 * ColorPicker — 可复用颜色选择器
 *
 * 从颜色方案 store 读取可用颜色，以网格形式展示。
 * 当前选中色块显示高亮边框与右下角对勾。
 * 支持显示颜色名称，便于用户按语义选择。
 *
 * @example
 * <ColorPicker value={task.color} showName onselect={(c) => updateTask(..., { color: c })} />
 */

  import { colorScheme, getColorName } from '$lib/stores/color';

  interface Props {
    /** 当前选中的色值 */
    value?: string | null;
    /** 色块尺寸 */
    size?: 'sm' | 'md';
    /** 是否显示颜色名称 */
    showName?: boolean;
    /** 是否允许通过原生取色器选择任意颜色 */
    allowCustom?: boolean;
    /** 选中回调 */
    onselect: (value: string) => void;
  }

  let { value = null, size = 'md', showName = false, allowCustom = false, onselect }: Props = $props();

  const sizeClass = $derived(size === 'sm' ? 'picker-sm' : 'picker-md');

  function handleCustomInput(e: Event) {
    const target = e.target as HTMLInputElement;
    if (target.value) onselect(target.value);
  }
</script>

<div class="color-picker {sizeClass}" class:with-names={showName}>
  {#each $colorScheme as color (color.id)}
    {@const selected = value?.toLowerCase() === color.value.toLowerCase()}
    <button
      type="button"
      class="color-swatch"
      class:selected
      class:named={showName}
      style="--swatch-color: {color.value}"
      title="{color.name} ({color.value})"
      aria-label="选择颜色 {color.name}"
      onclick={() => onselect(color.value)}
    >
      <span class="swatch-fill" style="background:{color.value}"></span>
      {#if showName}
        <span class="swatch-name">{color.name}</span>
      {/if}
      {#if selected}
        <span class="swatch-check">
          <svg width="10" height="10" viewBox="0 0 12 12" fill="none">
            <path d="M2 6L5 9L10 3" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/>
          </svg>
        </span>
      {/if}
    </button>
  {/each}

  {#if allowCustom}
    <label class="color-swatch custom-swatch" class:selected={!$colorScheme.some(c => c.value.toLowerCase() === (value ?? '').toLowerCase()) && !!value} title="自定义颜色">
      <input type="color" value={value ?? '#a33b32'} oninput={handleCustomInput} />
      <span class="swatch-fill custom-fill">
        <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
          <circle cx="12" cy="12" r="10"></circle>
          <line x1="12" y1="8" x2="12" y2="16"></line>
          <line x1="8" y1="12" x2="16" y2="12"></line>
        </svg>
      </span>
    </label>
  {/if}
</div>

{#if value}
  <div class="color-preview-row">
    <span class="preview-dot" style="background:{value}"></span>
    <span class="preview-name">{getColorName(value)}</span>
    <span class="preview-value">{value}</span>
  </div>
{/if}

<style>
  .color-picker {
    display: flex;
    flex-wrap: wrap;
    gap: 8px;
  }

  .color-swatch {
    position: relative;
    display: flex;
    align-items: center;
    justify-content: center;
    padding: 0;
    border-radius: 50%;
    border: 2px solid transparent;
    background: transparent;
    cursor: pointer;
    transition: transform 0.15s, border-color 0.15s, box-shadow 0.15s;
  }

  .picker-md .color-swatch {
    width: 28px;
    height: 28px;
  }

  .picker-sm .color-swatch {
    width: 22px;
    height: 22px;
  }

  .color-swatch:hover {
    transform: scale(1.12);
  }

  .color-swatch.selected {
    border-color: var(--accent);
    box-shadow: 0 0 0 2px var(--accent-light), 0 0 8px var(--accent);
    transform: scale(1.08);
  }

  .swatch-fill {
    display: block;
    width: 100%;
    height: 100%;
    border-radius: 50%;
    border: 1px solid rgba(0, 0, 0, 0.08);
  }

  .swatch-check {
    position: absolute;
    right: -2px;
    bottom: -2px;
    width: 14px;
    height: 14px;
    border-radius: 50%;
    background: var(--accent);
    color: #fff;
    display: flex;
    align-items: center;
    justify-content: center;
    box-shadow: 0 1px 3px rgba(0,0,0,0.2);
  }

  .picker-sm .swatch-check {
    width: 12px;
    height: 12px;
  }

  .swatch-check svg {
    width: 8px;
    height: 8px;
  }

  /* 显示名称模式 */
  .color-picker.with-names {
    gap: 6px;
  }

  .color-swatch.named {
    width: auto;
    height: auto;
    min-height: 28px;
    padding: 4px 8px;
    border-radius: 6px;
    display: inline-flex;
    flex-direction: row;
    align-items: center;
    gap: 6px;
    background: var(--surface);
    border: 1px solid var(--border);
  }

  .color-swatch.named .swatch-fill {
    width: 14px;
    height: 14px;
    flex-shrink: 0;
  }

  .color-swatch.named.selected {
    border-color: var(--accent);
    box-shadow: 0 0 0 2px var(--accent-light);
    transform: none;
  }

  .color-swatch.named .swatch-check {
    position: static;
    width: 14px;
    height: 14px;
    margin-left: 2px;
  }

  .swatch-name {
    font-size: 11px;
    color: var(--text-secondary);
    white-space: nowrap;
  }

  .color-swatch.named.selected .swatch-name {
    color: var(--accent);
    font-weight: 500;
  }

  /* 自定义颜色 */
  .custom-swatch {
    position: relative;
  }

  .custom-swatch input[type="color"] {
    position: absolute;
    inset: 0;
    opacity: 0;
    width: 100%;
    height: 100%;
    cursor: pointer;
  }

  .custom-fill {
    display: flex;
    align-items: center;
    justify-content: center;
    background: var(--surface);
    color: var(--text-secondary);
  }

  /* 当前颜色预览 */
  .color-preview-row {
    display: flex;
    align-items: center;
    gap: 8px;
    margin-top: 8px;
    padding: 6px 8px;
    background: var(--surface);
    border: 1px solid var(--border);
    border-radius: 6px;
  }

  .preview-dot {
    width: 14px;
    height: 14px;
    border-radius: 50%;
    flex-shrink: 0;
  }

  .preview-name {
    font-size: 12px;
    color: var(--text);
    font-weight: 500;
  }

  .preview-value {
    font-size: 11px;
    color: var(--text-muted);
    font-family: monospace;
    margin-left: auto;
  }
</style>
