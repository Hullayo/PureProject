<script lang="ts">
/**
 * FlowShapeNode — @xyflow/svelte 自定义节点组件（精简版）
 * 矩形/菱形/圆形三种形状，双击编辑标签，顶部/底部连接点。
 */

  import { Handle, Position, useSvelteFlow, type NodeProps } from '@xyflow/svelte';

  let { data, id, selected }: NodeProps = $props();
  const nodeData = data as any;
  const nodeColor = nodeData.color || '#a33b32';
  const { updateNodeData } = useSvelteFlow();

  let editing = $state(false);
  let editValue = $state('');
  let editInput: HTMLInputElement | undefined = $state(undefined);

  function startEdit() {
    editValue = nodeData.label || '';
    editing = true;
    requestAnimationFrame(() => { editInput?.focus(); editInput?.select(); });
  }
  function saveEdit() {
    if (editing) {
      updateNodeData(id, { label: editValue });
      editing = false;
    }
  }
  function handleEditKeydown(e: KeyboardEvent) {
    if (e.key === 'Enter') saveEdit();
    if (e.key === 'Escape') editing = false;
  }
</script>

<!-- svelte-ignore a11y_no_static_element_interactions -->
<div class="flow-shape-node" class:selected ondblclick={startEdit}>
  {#if editing}
    <div class="edit-overlay">
      <input bind:this={editInput} class="edit-input" type="text" bind:value={editValue}
        onblur={saveEdit} onkeydown={handleEditKeydown} />
    </div>
  {:else if nodeData.nodeType === 'diamond'}
    <div class="diamond-wrapper">
      <div class="diamond-shape" style="background:{nodeColor}30; border-color:{selected ? '#ef4444' : nodeColor}; border-width:{selected ? 3 : 2}px"></div>
      <div class="node-label">{nodeData.label || '节点'}</div>
    </div>
  {:else if nodeData.nodeType === 'circle'}
    <div class="circle-shape" style="background:{nodeColor}30; border-color:{selected ? '#ef4444' : nodeColor}; border-width:{selected ? 3 : 2}px">
      <div class="node-label">{nodeData.label || '节点'}</div>
    </div>
  {:else}
    <div class="rect-shape" style="background:{nodeColor}30; border-color:{selected ? '#ef4444' : nodeColor}; border-width:{selected ? 3 : 2}px">
      <div class="node-label">{nodeData.label || '节点'}</div>
    </div>
  {/if}
  <Handle type="target" position={Position.Top} class="flow-handle" style={`background:${nodeColor}`} />
  <Handle type="source" position={Position.Bottom} class="flow-handle" style={`background:${nodeColor}`} />
</div>

<style>
  .flow-shape-node { position: relative; display: flex; align-items: center; justify-content: center; min-width: 60px; min-height: 40px; }
  .rect-shape { display: flex; align-items: center; justify-content: center; padding: 12px 20px; border-radius: 8px; border-style: solid; width: 100%; height: 100%; box-sizing: border-box; }
  .diamond-wrapper { position: relative; display: flex; align-items: center; justify-content: center; width: 100px; height: 80px; }
  .diamond-shape { position: absolute; inset: 0; transform: rotate(45deg) scale(0.75); border-radius: 4px; border-style: solid; }
  .diamond-wrapper .node-label { position: relative; z-index: 1; }
  .circle-shape { display: flex; align-items: center; justify-content: center; padding: 16px 24px; border-radius: 50%; border-style: solid; min-width: 80px; min-height: 80px; box-sizing: border-box; }
  .node-label { font-size: 12px; color: var(--text, #333); text-align: center; pointer-events: none; user-select: none; }
  .edit-overlay { position: absolute; inset: 0; display: flex; align-items: center; justify-content: center; z-index: 10; background: var(--bg, #fff); border-radius: 6px; }
  .edit-input { width: 90%; padding: 4px 6px; font-size: 12px; border: 2px solid var(--accent, #a33b32); border-radius: 4px; background: var(--bg, #fff); color: var(--text, #333); text-align: center; outline: none; }
</style>
