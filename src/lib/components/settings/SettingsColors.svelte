<script lang="ts">
/**
 * SettingsColors — 颜色方案管理标签页
 *
 * 允许用户管理自定义颜色方案：添加、编辑、删除颜色。
 * 内置颜色（builtin）不可删除，仅可查看。
 * 所有更改自动持久化到 localStorage（pm_custom_colors）。
 *
 * @example
 * <SettingsColors />
 */

  import { t } from '$lib/i18n';
  import { colorScheme, addColor, updateColor, deleteColor, resetColors, isColorDeletable } from '$lib/stores/color';
  import type { ColorItem } from '$lib/types';

  // —— 添加新颜色状态 ——
  let newName = $state('');
  let newValue = $state('#a33b32');
  let showAddForm = $state(false);

  // —— 编辑状态 ——
  let editingId = $state<string | null>(null);
  let editName = $state('');
  let editValue = $state('');

  // —— 删除确认 ——
  let deletingId = $state<string | null>(null);

  function handleAdd() {
    if (!newName.trim()) return;
    addColor(newName.trim(), newValue);
    newName = '';
    newValue = '#a33b32';
    showAddForm = false;
  }

  function startEdit(item: ColorItem) {
    editingId = item.id;
    editName = item.name;
    editValue = item.value;
  }

  function handleEdit() {
    if (editingId && editName.trim()) {
      updateColor(editingId, { name: editName.trim(), value: editValue });
    }
    editingId = null;
  }

  function cancelEdit() {
    editingId = null;
  }

  function confirmDelete(id: string) {
    deletingId = id;
  }

  function handleDelete(id: string) {
    deleteColor(id);
    deletingId = null;
  }

  function handleReset() {
    resetColors();
  }
</script>

<div class="section">
  <div class="section-header">
    <span class="section-title">{$t('colors.title')}</span>
    <button class="btn-sm btn-ghost" onclick={handleReset}>{$t('colors.reset')}</button>
  </div>
  <p class="section-desc">
    颜色可供项目、任务和标签使用。至少保留 5 个颜色。
  </p>
</div>

<!-- 颜色列表 -->
<div class="color-list">
  {#each $colorScheme as color (color.id)}
    {@const isEditing = editingId === color.id}
    {@const canDelete = isColorDeletable(color.id)}

    <div class="color-row" class:editing={isEditing}>
      {#if isEditing}
        <!-- 编辑模式 -->
        <div class="edit-color-inputs">
          <div class="color-preview-wrap">
            <input
              type="color"
              bind:value={editValue}
              class="color-input-native"
              aria-label={$t('colors.value')}
            />
            <span class="color-preview-circle" style="background:{editValue}"></span>
          </div>
          <input
            class="input edit-name-input"
            type="text"
            bind:value={editName}
            placeholder={$t('colors.colorNamePlaceholder')}
            onkeydown={(e) => { if (e.key === 'Enter') handleEdit(); if (e.key === 'Escape') cancelEdit(); }}
          />
          <span class="edit-hex">{editValue}</span>
        </div>
        <div class="color-actions">
          <button class="btn-sm btn-primary" onclick={handleEdit}>{$t('colors.save')}</button>
          <button class="btn-sm btn-ghost" onclick={cancelEdit}>{$t('colors.cancel')}</button>
        </div>
      {:else}
        <!-- 展示模式 -->
        <div class="color-info">
          <span class="color-swatch-dot" style="background:{color.value}"></span>
          <span class="color-name">{color.name}</span>
          <span class="color-hex">{color.value}</span>
          {#if color.builtin}
            <span class="builtin-badge">内置</span>
          {/if}
        </div>
        <div class="color-actions">
          {#if deletingId === color.id}
            <span class="confirm-delete">
              <span class="confirm-text">{$t('colors.confirmDelete')}</span>
              <button class="btn-sm btn-danger" onclick={() => handleDelete(color.id)}>{$t('colors.delete')}</button>
              <button class="btn-sm btn-ghost" onclick={() => { deletingId = null; }}>{$t('colors.cancel')}</button>
            </span>
          {:else}
            <button class="btn-sm btn-ghost" onclick={() => startEdit(color)}>{$t('colors.edit')}</button>
            <button class="btn-sm btn-danger-ghost" onclick={() => confirmDelete(color.id)} disabled={!canDelete}>
              {$t('colors.delete')}
            </button>
          {/if}
        </div>
      {/if}
    </div>
  {/each}
</div>

<!-- 添加新颜色 -->
<div class="section add-section">
  {#if showAddForm}
    <div class="add-form">
      <div class="add-form-row">
        <div class="color-preview-wrap">
          <input
            type="color"
            bind:value={newValue}
            class="color-input-native"
            aria-label={$t('colors.value')}
          />
          <span class="color-preview-circle" style="background:{newValue}"></span>
        </div>
        <input
          class="input"
          type="text"
          bind:value={newName}
          placeholder={$t('colors.colorNamePlaceholder')}
          onkeydown={(e) => { if (e.key === 'Enter') handleAdd(); if (e.key === 'Escape') showAddForm = false; }}
        />
      </div>
      <div class="add-form-actions">
        <button class="btn-sm btn-primary" onclick={handleAdd}>{$t('colors.add')}</button>
        <button class="btn-sm btn-ghost" onclick={() => { showAddForm = false; }}>{$t('colors.cancel')}</button>
      </div>
    </div>
  {:else}
    <button class="btn btn-primary add-btn" onclick={() => { showAddForm = true; }}>
      + {$t('colors.addNew')}
    </button>
  {/if}
</div>

<style>
  .section { display: flex; flex-direction: column; gap: 8px; }
  .section-header { display: flex; align-items: center; justify-content: space-between; }
  .section-title { font-size: 11px; color: var(--text-muted); text-transform: uppercase; letter-spacing: 0; }
  .section-desc { font-size: 11px; color: var(--text-muted); line-height: 1.5; margin: 0; }
  .color-list { display: flex; flex-direction: column; gap: 4px; }

  .color-row {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 8px 10px;
    background: var(--surface);
    border: 1px solid var(--border);
    border-radius: 8px;
    gap: 8px;
  }
  .color-row.editing {
    flex-wrap: wrap;
    border-color: var(--accent);
  }

  .color-info {
    display: flex;
    align-items: center;
    gap: 8px;
    flex: 1;
    min-width: 0;
  }
  .color-swatch-dot {
    width: 18px;
    height: 18px;
    border-radius: 50%;
    flex-shrink: 0;
    border: 1px solid rgba(0,0,0,0.08);
  }
  .color-name {
    font-size: 13px;
    color: var(--text);
    font-weight: 500;
  }
  .color-hex {
    font-size: 11px;
    color: var(--text-muted);
    font-family: monospace;
  }
  .builtin-badge {
    font-size: 10px;
    color: var(--accent);
    background: var(--accent-light);
    padding: 1px 6px;
    border-radius: 4px;
    margin-left: 4px;
  }

  .color-actions {
    display: flex;
    align-items: center;
    gap: 4px;
    flex-shrink: 0;
  }

  .confirm-delete {
    display: flex;
    align-items: center;
    gap: 4px;
  }
  .confirm-text {
    font-size: 11px;
    color: var(--red);
    margin-right: 4px;
  }

  /* 编辑模式输入 */
  .edit-color-inputs {
    display: flex;
    align-items: center;
    gap: 8px;
    flex: 1;
    min-width: 0;
  }
  .color-preview-wrap {
    position: relative;
    width: 28px;
    height: 28px;
    flex-shrink: 0;
  }
  .color-input-native {
    position: absolute;
    inset: 0;
    opacity: 0;
    width: 100%;
    height: 100%;
    cursor: pointer;
    z-index: 1;
  }
  .color-preview-circle {
    display: block;
    width: 100%;
    height: 100%;
    border-radius: 50%;
    border: 1px solid rgba(0,0,0,0.08);
  }
  .edit-name-input {
    flex: 1;
    min-width: 80px;
  }
  .edit-hex {
    font-size: 11px;
    color: var(--text-muted);
    font-family: monospace;
  }

  /* 按钮 */
  .btn-sm {
    padding: 4px 10px;
    font-size: 11px;
    border-radius: 5px;
    cursor: pointer;
    white-space: nowrap;
  }
  .btn-primary {
    background: var(--accent);
    color: #fff;
    border: none;
  }
  .btn-primary:hover { opacity: 0.9; }
  .btn-ghost {
    background: transparent;
    color: var(--text-secondary);
    border: 1px solid var(--border);
  }
  .btn-ghost:hover { border-color: var(--accent); color: var(--accent); }
  .btn-danger {
    background: var(--red, #ef4444);
    color: #fff;
    border: none;
  }
  .btn-danger:hover { opacity: 0.9; }
  .btn-danger-ghost {
    background: transparent;
    color: var(--red, #ef4444);
    border: 1px solid var(--red, #ef4444);
  }
  .btn-danger-ghost:hover { background: var(--red, #ef4444); color: #fff; }

  .input {
    padding: 6px 10px;
    font-size: 12px;
    border-radius: 5px;
    background: var(--bg);
    border: 1px solid var(--border);
    color: var(--text);
  }
  .input:focus { border-color: var(--accent); outline: none; }

  /* 添加区域 */
  .add-section { margin-top: 8px; }
  .add-form {
    display: flex;
    flex-direction: column;
    gap: 8px;
    padding: 10px;
    background: var(--surface);
    border: 1px solid var(--accent);
    border-radius: 8px;
  }
  .add-form-row {
    display: flex;
    align-items: center;
    gap: 8px;
  }
  .add-form-row .input {
    flex: 1;
  }
  .add-form-actions {
    display: flex;
    gap: 4px;
  }
  .add-btn {
    width: 100%;
  }
  .btn {
    padding: 7px 14px;
    font-size: 12px;
    border-radius: 6px;
    cursor: pointer;
    border: none;
  }
</style>
