<script lang="ts">
/**
 * Flowchart — 流程图绘制工具
 */

  import { SvelteFlow, Controls, Background, BackgroundVariant, MiniMap,
    type Node, type Edge, type Connection,
  } from '@xyflow/svelte';
  import '@xyflow/svelte/dist/style.css';

  import FlowShapeNode from '$lib/components/flowchart/FlowShapeNode.svelte';
  import { showFlowchart, activeProject, activeProjectId, updateProject } from '$lib/stores';
  import { get } from 'svelte/store';

  // ── 持久化 ──
  const STORAGE_KEY = 'pm_flowchart_data';
  /** 从 localStorage 加载后备数据 */
  function loadBackup() { try { const raw = localStorage.getItem(STORAGE_KEY); return raw ? JSON.parse(raw) : null; } catch { return null; } }
  /** 保存到 localStorage 和当前项目 */
  function saveFlowchartData(n: Node[], e: Edge[]) {
    try { localStorage.setItem(STORAGE_KEY, JSON.stringify({ nodes: n, edges: e })); } catch {}
    const pid = get(activeProjectId);
    if (pid) {
      updateProject(pid, { flowchart: { nodes: n as any, edges: e as any } as any });
    }
  }

  const nodeTypes: any = { rect: FlowShapeNode, diamond: FlowShapeNode, circle: FlowShapeNode };

  let nodes: Node[] = $state([]);
  let edges: Edge[] = $state([]);
  let paletteType: 'rect' | 'diamond' | 'circle' | null = $state(null);
  let nodeIdCounter = $state(0);
  let edgeStyle: string = $state('smoothstep');

  // ── 撤销/重做（普通数组，不跟踪响应式） ──
  let undoStack: { n: Node[]; e: Edge[] }[] = [];
  let redoStack: { n: Node[]; e: Edge[] }[] = [];

  function pushUndo() {
    undoStack.push({ n: [...nodes], e: [...edges] });
    if (undoStack.length > 50) undoStack.shift();
    redoStack = [];
  }
  function undo() {
    if (undoStack.length === 0) return;
    redoStack.push({ n: [...nodes], e: [...edges] });
    const state = undoStack.pop()!;
    nodes = state.n;
    edges = state.e;
  }
  function redo() {
    if (redoStack.length === 0) return;
    undoStack.push({ n: [...nodes], e: [...edges] });
    const state = redoStack.pop()!;
    nodes = state.n;
    edges = state.e;
  }

  // ── 复制/粘贴 ──
  let copiedNodes = $state<any[]>([]);
  function copySelected() {
    const sel = nodes.filter((n: any) => n.selected);
    if (sel.length === 0) return;
    copiedNodes = sel.map((n: any) => ({ ...n, data: { ...(n.data || {}) }, position: { ...n.position } }));
  }
  function pasteNodes() {
    if (copiedNodes.length === 0) return;
    const maxId = nodes.reduce((m: number, n: any) => Math.max(m, parseInt((n.id || '').replace('node-', ''), 10) || 0), 0);
    let c = maxId + 1;
    const newNodes = copiedNodes.map(n => ({ ...n, id: `node-${c++}`, position: { x: n.position.x + 40, y: n.position.y + 40 }, selected: false }));
    nodes = [...nodes, ...newNodes];
    pushUndo();
  }
  function selectAll() {
    nodes = nodes.map((n: any) => ({ ...n, selected: true }));
  }

  // ── 选中节点 ──
  let selectedNodes = $derived(nodes.filter((n: any) => n.selected));
  let hasSelection = $derived(selectedNodes.length > 0);

  // ── 颜色 ──
  let nodeColorPicker = $state('#a33b32');
  $effect(() => {
    if (selectedNodes.length === 1) {
      const c = (selectedNodes[0] as any).data?.color;
      if (c) nodeColorPicker = c;
    }
  });
  function applyColor(color: string) {
    if (!hasSelection) return;
    for (const n of selectedNodes) {
      const oldData = (n as any).data || {};
      nodes = nodes.map((node: any) => node.id === n.id ? { ...node, data: { ...oldData, color } } : node);
    }
    nodeColorPicker = color;
  }

  // ── 加载（每个项目只初始加载一次）──
  //
  // ⚠️ 绝不能在本 effect 里先写 `nodes` 再读 `nodes`（如 reduce）：那会让 effect
  // 依赖自己写入的 state，第二次打开（项目/备份里已有数据）时触发
  // `effect_update_depth_exceeded`，Svelte 停止更新 → 整个应用“什么都点不动”。
  // 因此：只在切项目时加载一次（early return），且只用**局部变量**计算 id 计数。
  let loadedProjectKey: string | null = null;
  $effect(() => {
    const pid = get(activeProjectId) ?? '';
    if (loadedProjectKey === pid) return;
    loadedProjectKey = pid;

    const proj = get(activeProject);
    let n: Node[] = [];
    let e: Edge[] = [];
    if (proj?.flowchart && Array.isArray((proj.flowchart as any).nodes)) {
      const fc = proj.flowchart as any;
      n = fc.nodes;
      e = Array.isArray(fc.edges) ? fc.edges : [];
    } else {
      const backup = loadBackup();
      if (backup && Array.isArray(backup.nodes) && Array.isArray(backup.edges)) {
        n = backup.nodes;
        e = backup.edges;
      }
    }
    const maxNum = n.reduce((m: number, x: any) => Math.max(m, parseInt((x.id || '').replace('node-', ''), 10) || 0), 0);
    nodes = n;
    edges = e;
    nodeIdCounter = maxNum + 1;
  });

  // ── 保存 ──
  let saveTimer: ReturnType<typeof setTimeout> | null = null;
  $effect(() => {
    const _n = nodes, _e = edges;
    if (saveTimer) clearTimeout(saveTimer);
    saveTimer = setTimeout(() => saveFlowchartData(_n, _e), 500);
    return () => { if (saveTimer) clearTimeout(saveTimer); };
  });

  // ── 连线 ──
  function onConnect(connection: Connection) {
    edges = [...edges, { id: `edge-${Date.now()}`, source: connection.source, target: connection.target,
      type: edgeStyle === 'default' ? undefined : edgeStyle, animated: true, style: 'stroke:var(--text-secondary);stroke-width:2px;' } as Edge];
    pushUndo();
  }

  // ── 拖拽结束 ──
  function onNodeDragStop({ targetNode }: { targetNode: Node | null; nodes: Node[]; event: MouseEvent | TouchEvent }) {
    if (targetNode) nodes = nodes.map((n: any) => n.id === targetNode.id ? { ...n, position: { ...targetNode.position } } : n);
  }

  // ── 面板 ──
  const paletteItems = [
    { type: 'rect' as const, label: '矩形', icon: '▭' }, { type: 'diamond' as const, label: '菱形', icon: '◇' }, { type: 'circle' as const, label: '圆形', icon: '○' },
  ];
  function selectPalette(type: 'rect' | 'diamond' | 'circle') { paletteType = paletteType === type ? null : type; }
  function cancelPalette() { paletteType = null; }

  function onDragStart(e: DragEvent, type: string) { if (e.dataTransfer) { e.dataTransfer.setData('type', type); e.dataTransfer.effectAllowed = 'move'; } }
  function onDragOver(e: DragEvent) { e.preventDefault(); if (e.dataTransfer) e.dataTransfer.dropEffect = 'move'; }
  function onDrop(e: DragEvent) {
    e.preventDefault();
    const type = e.dataTransfer?.getData('type') as 'rect' | 'diamond' | 'circle' | null;
    if (!type) return;
    const rect = (e.currentTarget as HTMLElement).getBoundingClientRect();
    const x = e.clientX - rect.left, y = e.clientY - rect.top;
    const id = `node-${nodeIdCounter++}`;
    const w = type === 'diamond' ? 100 : 80, h = type === 'diamond' ? 80 : type === 'circle' ? 80 : 56;
    const colors = ['#a33b32','#3e7562','#ad7622','#b94a43','#73576f','#477477','#a85769','#6e7f48'];
    nodes = [...nodes, { id, type, position: { x: x - w / 2, y: y - h / 2 }, data: { label: '', color: colors[nodeIdCounter % colors.length], nodeType: type } } as Node];
    pushUndo();
  }

  function clearAll() { nodes = []; edges = []; nodeIdCounter = 0; pushUndo(); }

  function exportPng() {
    const el = document.querySelector('.svelte-flow__pane canvas');
    if (el instanceof HTMLCanvasElement) { const a = document.createElement('a'); a.download = 'flowchart.png'; a.href = el.toDataURL('image/png'); a.click(); }
  }

  function autoLayout() {
    if (nodes.length === 0) return;
    const cols = Math.ceil(Math.sqrt(nodes.length));
    nodes = nodes.map((n: any, i: number) => ({ ...n, position: { x: 100 + (i % cols) * 180, y: 80 + Math.floor(i / cols) * 120 } }));
    pushUndo();
  }

  function updateEdgeStyle() {
    edges = edges.map((e: any) => ({ ...e, type: edgeStyle === 'default' ? undefined : edgeStyle }));
    pushUndo();
  }

  // ── 键盘 ──
  function handleKeydown(e: KeyboardEvent) {
    if ((e.target as HTMLElement).closest('input, textarea, .edit-input')) return;
    if (e.key === 'Escape') { showFlowchart.set(false); return; }
    if (e.ctrlKey || e.metaKey) {
      if (e.key === 'z' && !e.shiftKey) { e.preventDefault(); undo(); return; }
      if ((e.key === 'z' && e.shiftKey) || e.key === 'Z') { e.preventDefault(); redo(); return; }
      if (e.key === 'c' || e.key === 'C') { e.preventDefault(); copySelected(); return; }
      if (e.key === 'v' || e.key === 'V') { e.preventDefault(); pasteNodes(); return; }
      if (e.key === 'a' || e.key === 'A') { e.preventDefault(); selectAll(); return; }
    }
  }
</script>

<svelte:window onkeydown={handleKeydown} />

<div class="flowchart-layout">
  <aside class="flow-palette">
    <div class="palette-title">节点类型</div>
    {#each paletteItems as item}
      <button class="palette-item" class:active={paletteType === item.type}
        onclick={() => selectPalette(item.type)} draggable="true" ondragstart={(e) => onDragStart(e, item.type)}>
        <span class="palette-icon">{item.icon}</span>
        <span class="palette-label">{item.label}</span>
      </button>
    {/each}
    {#if paletteType}
      <button class="palette-cancel" onclick={cancelPalette}>← 取消</button>
    {/if}

    <div class="palette-divider"></div>

    {#if hasSelection}
      <div class="palette-section">
        <div class="palette-section-title">颜色 ({selectedNodes.length})</div>
        <div class="color-row">
          <input type="color" bind:value={nodeColorPicker} oninput={() => applyColor(nodeColorPicker)} class="color-input" />
          <span class="color-hex">{nodeColorPicker}</span>
        </div>
      </div>
      <div class="palette-divider"></div>
    {/if}

    <div class="palette-section">
      <div class="palette-section-title">连线样式</div>
      <select class="style-select" bind:value={edgeStyle} onchange={updateEdgeStyle}>
        <option value="default">默认</option>
        <option value="smoothstep">平滑阶梯</option>
        <option value="step">阶梯</option>
        <option value="straight">直线</option>
        <option value="bezier">贝塞尔</option>
      </select>
    </div>

    <div class="palette-divider"></div>

    <button class="palette-tool" onclick={autoLayout}>⊞ 自动布局</button>
    <button class="palette-tool" onclick={selectAll}>☐ 全选</button>
    <button class="palette-tool" onclick={copySelected} disabled={!hasSelection}>📋 复制</button>
    <button class="palette-tool" onclick={pasteNodes} disabled={copiedNodes.length === 0}>📄 粘贴</button>

    <div class="palette-divider"></div>

    <button class="palette-tool danger" onclick={clearAll}>🗑 清空</button>
    <button class="palette-tool" onclick={exportPng}>📷 导出</button>

    <div class="palette-spacer"></div>
    <div class="palette-hint">Ctrl+Z 撤销 · Ctrl+Shift+Z 重做<br/>Delete 删除 · Esc 关闭</div>
  </aside>

  <div class="flow-canvas-area" ondrop={onDrop} ondragover={onDragOver}>
    <SvelteFlow nodes={nodes} edges={edges} {nodeTypes}
      onconnect={onConnect} onnodedragstop={onNodeDragStop}
      fitView snapGrid={[20, 20]} deleteKey="Delete" multiSelectionKey="Shift"
      panOnDrag={[1, 2]} selectNodesOnDrag={false}>
      <Controls position="bottom-right" />
      <Background variant={BackgroundVariant.Dots} gap={20} size={1} />
      <MiniMap position="top-right" nodeStrokeColor="#a33b32" style="width:120px;height:80px;" />
    </SvelteFlow>
  </div>

  <div class="flow-status">
    {#if paletteType}
      点击画布放置 · 双击编辑 · Delete 删除
    {:else}
      {nodes.length} 节点 · {edges.length} 连线 {#if hasSelection}· {selectedNodes.length} 已选{/if}
    {/if}
  </div>
</div>

<style>
  .flowchart-layout { flex: 1; display: flex; overflow: hidden; background: var(--bg); position: relative; }
  .flow-palette { width: 130px; flex-shrink: 0; display: flex; flex-direction: column; gap: 2px; padding: 10px 8px; background: var(--sidebar); border-right: 1px solid var(--border); overflow-y: auto; z-index: 10; }
  .palette-title { font-size: 10px; font-weight: 600; color: var(--text-muted); text-transform: uppercase; letter-spacing: 0; padding: 4px 6px; margin-bottom: 4px; }
  .palette-item { display: flex; align-items: center; gap: 6px; padding: 8px; font-size: 12px; border-radius: 6px; color: var(--text-secondary); cursor: grab; border: 1px solid transparent; }
  .palette-item:hover { background: var(--surface); color: var(--text); border-color: var(--border); }
  .palette-item.active { background: var(--accent); color: #fff; border-color: var(--accent); }
  .palette-icon { font-size: 16px; }
  .palette-label { font-size: 12px; }
  .palette-cancel { padding: 5px 8px; font-size: 11px; border-radius: 6px; color: var(--accent); background: var(--accent-light); cursor: pointer; margin-top: 4px; border: none; }
  .palette-divider { height: 1px; background: var(--border); margin: 6px 4px; }
  .palette-section { display: flex; flex-direction: column; gap: 4px; padding: 0 4px; }
  .palette-section-title { font-size: 10px; font-weight: 600; color: var(--text-muted); text-transform: uppercase; letter-spacing: 0; }
  .color-row { display: flex; align-items: center; gap: 4px; }
  .color-input { width: 26px; height: 26px; padding: 0; border: 1px solid var(--border); border-radius: 4px; cursor: pointer; background: none; }
  .color-input::-webkit-color-swatch-wrapper { padding: 2px; }
  .color-input::-webkit-color-swatch { border: none; border-radius: 3px; }
  .color-hex { font-size: 10px; color: var(--text-muted); font-family: monospace; }
  .style-select { width: 100%; padding: 4px 6px; font-size: 11px; border-radius: 4px; background: var(--bg); border: 1px solid var(--border); color: var(--text); }
  .palette-tool { padding: 6px 8px; font-size: 11px; border-radius: 6px; color: var(--text-secondary); cursor: pointer; text-align: left; border: none; }
  .palette-tool:hover { background: var(--surface); color: var(--text); }
  .palette-tool:disabled { opacity: 0.3; cursor: not-allowed; }
  .palette-tool.danger:hover { color: var(--red); }
  .palette-spacer { flex: 1; }
  .palette-hint { font-size: 9px; color: var(--text-muted); text-align: center; line-height: 1.5; padding: 6px 4px; }
  .flow-canvas-area { flex: 1; position: relative; overflow: hidden; }
  .flow-status { position: absolute; bottom: 0; left: 130px; right: 0; padding: 4px 16px; font-size: 11px; color: var(--accent); background: var(--surface); border-top: 1px solid var(--border); z-index: 10; }
  @media (max-width: 768px) { .flow-palette { width: 100px; } .flow-status { left: 100px; } }
</style>
