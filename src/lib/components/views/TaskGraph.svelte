<script lang="ts">
/**
 * TaskGraph — 任务依赖关系图视图
 *
 * 以有向图形式可视化任务之间的依赖关系。
 * 功能包括：
 * - 拓扑排序自动分层布局
 * - 贝塞尔曲线箭头连接（带天数偏移标注）
 * - 节点展示优先级色条、状态圆点、标题和日期
 * - 拖拽平移画布
 * - 点击节点选中任务
 * - 优先级和状态图例
 *
 * @example
 * <TaskGraph />
 */

  import { activeProject, activeTaskId, taskGroupFilter, selectTask as openTask } from '$lib/stores';
  import type { Task, Priority } from '$lib/types';
  import { get } from 'svelte/store';
  import { t, tf } from '$lib/i18n';
  import { getTaskStatus, isTaskCompleted } from '$lib/utils/task-status';

  const pCfg: Record<Priority, { color: string }> = {
    high: { color: '#ef4444' },
    medium: { color: '#f59e0b' },
    low: { color: '#10b981' }
  };
  function pLabel(p: Priority): string { return get(t)(`priority.${p}`); }
  let groups = $derived(($activeProject?.task_groups ?? []).filter(group => !group.archived).sort((a,b) => a.sort_order - b.sort_order));
  let visibleTasks = $derived.by(() => {
    if (!$activeProject || $taskGroupFilter === 'all') return $activeProject?.tasks ?? [];
    const selected = $activeProject.tasks.filter(task => task.task_group_id === $taskGroupFilter);
    const ids = new Set(selected.map(task => task.id));
    for (const task of selected) for (const dep of task.dependencies) ids.add(dep.taskId);
    for (const task of $activeProject.tasks) if (task.dependencies.some(dep => ids.has(dep.taskId))) ids.add(task.id);
    return $activeProject.tasks.filter(task => ids.has(task.id));
  });
  let legendStatuses = $derived.by(() => !$activeProject ? [] : [...new Map(visibleTasks.map(task => getTaskStatus($activeProject!, task)).filter(Boolean).map(status => [status!.id, status!])).values()]);

  const NODE_W = 160;
  const NODE_H = 76;
  const GAP_X = 60;
  const GAP_Y = 30;

  interface LayoutNode {
    task: Task;
    col: number;
    row: number;
    x: number;
    y: number;
  }

  // 拓扑排序分层
  function layoutNodes(tasks: Task[]): LayoutNode[] {
    if (tasks.length === 0) return [];

    const taskMap = new Map(tasks.map(t => [t.id, t]));
    const inDegree = new Map<string, number>();
    const layers = new Map<string, number>();

    tasks.forEach(t => inDegree.set(t.id, 0));
    tasks.forEach(t => {
      for (const dep of t.dependencies) {
        if (taskMap.has(dep.taskId)) {
          inDegree.set(t.id, (inDegree.get(t.id) || 0) + 1);
        }
      }
    });

    // BFS 分层
    const queue: string[] = [];
    for (const [id, deg] of inDegree) {
      if (deg === 0) { queue.push(id); layers.set(id, 0); }
    }

    let maxLayer = 0;
    while (queue.length > 0) {
      const cur = queue.shift()!;
      const curLayer = layers.get(cur)!;
      const curTask = taskMap.get(cur)!;
      for (const dep of curTask.dependencies) {
        // dep.taskId 是被依赖的任务，cur 依赖它
        // 但我们遍历的是：哪些任务依赖了 dep.taskId
      }
      // 反向：找到所有依赖 cur 的任务
      for (const t of tasks) {
        if (t.dependencies.some(d => d.taskId === cur)) {
          const newLayer = curLayer + 1;
          const old = layers.get(t.id);
          if (old === undefined || newLayer > old) {
            layers.set(t.id, newLayer);
            if (newLayer > maxLayer) maxLayer = newLayer;
          }
          const deg = (inDegree.get(t.id) || 1) - 1;
          inDegree.set(t.id, deg);
          if (deg === 0) queue.push(t.id);
        }
      }
    }

    // 没有被分层的任务（循环依赖或孤立）放到最外层
    tasks.forEach(t => {
      if (!layers.has(t.id)) {
        maxLayer++;
        layers.set(t.id, maxLayer);
      }
    });

    // 每层的行位置
    const layerCounters = new Map<number, number>();
    const nodes: LayoutNode[] = [];

    // 按层级排序
    const sorted = [...tasks].sort((a, b) => (layers.get(a.id) || 0) - (layers.get(b.id) || 0));

    for (const t of sorted) {
      const col = layers.get(t.id) || 0;
      const row = layerCounters.get(col) || 0;
      layerCounters.set(col, row + 1);
      nodes.push({
        task: t,
        col,
        row,
        x: col * (NODE_W + GAP_X),
        y: row * (NODE_H + GAP_Y)
      });
    }

    return nodes;
  }

  // 箭头数据
  interface Arrow {
    from: { x: number; y: number };
    to: { x: number; y: number };
    dayOffset: number;
  }

  function computeArrows(nodes: LayoutNode[]): Arrow[] {
    const nodeMap = new Map(nodes.map(n => [n.task.id, n]));
    const arrows: Arrow[] = [];

    for (const node of nodes) {
      for (const dep of node.task.dependencies) {
        const src = nodeMap.get(dep.taskId);
        if (!src) continue;
        arrows.push({
          from: { x: src.x + NODE_W, y: src.y + NODE_H / 2 },
          to: { x: node.x, y: node.y + NODE_H / 2 },
          dayOffset: dep.dayOffset
        });
      }
    }
    return arrows;
  }

  function totalSize(nodes: LayoutNode[]): { w: number; h: number } {
    if (nodes.length === 0) return { w: 0, h: 0 };
    const maxX = Math.max(...nodes.map(n => n.x + NODE_W));
    const maxY = Math.max(...nodes.map(n => n.y + NODE_H));
    return { w: maxX + 40, h: maxY + 40 };
  }

  // 拖拽平移
  let scrollEl = $state<HTMLDivElement | null>(null);
  let dragging = $state(false);
  let didDrag = false;
  let dragStart = { x: 0, y: 0, scrollLeft: 0, scrollTop: 0 };

  function onMouseDown(e: MouseEvent) {
    if (!scrollEl || e.button !== 0) return;
    didDrag = false;
    dragging = true;
    dragStart = { x: e.clientX, y: e.clientY, scrollLeft: scrollEl.scrollLeft, scrollTop: scrollEl.scrollTop };
    window.addEventListener('mousemove', onMouseMove);
    window.addEventListener('mouseup', onMouseUp);
  }
  function onMouseMove(e: MouseEvent) {
    if (!dragging || !scrollEl) return;
    const dx = e.clientX - dragStart.x;
    const dy = e.clientY - dragStart.y;
    if (Math.abs(dx) > 3 || Math.abs(dy) > 3) didDrag = true;
    scrollEl.scrollLeft = dragStart.scrollLeft - dx;
    scrollEl.scrollTop = dragStart.scrollTop - dy;
  }
  function onMouseUp() {
    dragging = false;
    window.removeEventListener('mousemove', onMouseMove);
    window.removeEventListener('mouseup', onMouseUp);
  }

  // 触摸拖拽支持（移动端）
  function onTouchStart(e: TouchEvent) {
    if (!scrollEl || e.touches.length !== 1) return;
    didDrag = false;
    dragging = true;
    dragStart = { x: e.touches[0].clientX, y: e.touches[0].clientY, scrollLeft: scrollEl.scrollLeft, scrollTop: scrollEl.scrollTop };
  }
  function onTouchMove(e: TouchEvent) {
    if (!dragging || !scrollEl || e.touches.length !== 1) return;
    e.preventDefault();
    const dx = e.touches[0].clientX - dragStart.x;
    const dy = e.touches[0].clientY - dragStart.y;
    if (Math.abs(dx) > 3 || Math.abs(dy) > 3) didDrag = true;
    scrollEl.scrollLeft = dragStart.scrollLeft - dx;
    scrollEl.scrollTop = dragStart.scrollTop - dy;
  }
  function onTouchEnd() {
    dragging = false;
  }

  function selectTask(id: string) {
    if (!didDrag && $activeProject) openTask($activeProject.id, id);
  }
</script>

<div class="graph-view">
  {#if $activeProject}
    <div class="graph-header">
      <span class="dot" style="background:{$activeProject.color}"></span>
      <h2>{$activeProject.name}</h2>
      <span class="meta">{$t('graph.viewTitle')} · {$tf('task.count', { n: $activeProject.tasks.length })}</span>
      <select class="group-filter" bind:value={$taskGroupFilter}><option value="all">全部任务组</option>{#each groups as group}<option value={group.id}>{group.name}</option>{/each}</select>
    </div>

    {#if visibleTasks.length === 0}
      <div class="empty">{$t('graph.noTasks')}</div>
    {:else}
      {@const nodes = layoutNodes(visibleTasks)}
      {@const arrows = computeArrows(nodes)}
      {@const size = totalSize(nodes)}

      <div class="graph-scroll" class:grabbing={dragging} bind:this={scrollEl} onmousedown={onMouseDown} ontouchstart={onTouchStart} ontouchmove={onTouchMove} ontouchend={onTouchEnd}>
        <svg class="graph-svg" width={size.w} height={size.h}>
          <!-- 箭头 -->
          {#each arrows as a}
            {@const mx = (a.from.x + a.to.x) / 2}
            <path
              d="M {a.from.x} {a.from.y} C {mx} {a.from.y}, {mx} {a.to.y}, {a.to.x} {a.to.y}"
              fill="none"
              stroke="var(--accent)"
              stroke-width="2"
              opacity="0.5"
              marker-end="url(#arrowhead)"
            />
            {#if a.dayOffset > 0}
              <text
                x={mx}
                y={(a.from.y + a.to.y) / 2 - 6}
                text-anchor="middle"
                font-size="10"
                fill="var(--text-muted)"
              >{$tf('graph.dayOffset', { n: a.dayOffset })}</text>
            {/if}
          {/each}

          <!-- 箭头标记 -->
          <defs>
            <marker id="arrowhead" markerWidth="8" markerHeight="6" refX="8" refY="3" orient="auto">
              <polygon points="0 0, 8 3, 0 6" fill="var(--accent)" opacity="0.6" />
            </marker>
          </defs>

          <!-- 节点 -->
          {#each nodes as node}
            {@const selected = $activeTaskId === node.task.id}
            <!-- svelte-ignore a11y_click_events_have_key_events a11y_no_static_element_interactions -->
            <g
              class="node-group"
              class:selected
              role="button"
              tabindex="0"
              onclick={() => selectTask(node.task.id)}
              onkeydown={e => e.key === 'Enter' && selectTask(node.task.id)}
            >
              <rect
                x={node.x}
                y={node.y}
                width={NODE_W}
                height={NODE_H}
                rx="8"
                fill="var(--surface)"
                stroke={selected ? 'var(--accent)' : 'var(--border)'}
                stroke-width={selected ? 2 : 1}
              />
              <!-- 优先级色条 -->
              <rect
                x={node.x}
                y={node.y}
                width="4"
                height={NODE_H}
                rx="2"
                fill={pCfg[node.task.priority].color}
              />
              <!-- 状态圆点 -->
              <circle
                cx={node.x + 16}
                cy={node.y + 18}
                r="4"
                fill={getTaskStatus($activeProject, node.task)?.color ?? 'var(--text-muted)'}
              />
              <!-- 标题 -->
              <text
                x={node.x + 26}
                y={node.y + 22}
                font-size="12"
                font-weight="600"
                fill="var(--text)"
                class:done-text={isTaskCompleted($activeProject, node.task)}
              >
                {node.task.title.length > 10 ? node.task.title.slice(0, 10) + '...' : node.task.title}
              </text>
              <!-- 天数 -->
              <text
                x={node.x + 16}
                y={node.y + 43}
                font-size="10"
                fill="var(--text-muted)"
              >
                {$activeProject.task_groups.find(group => group.id === node.task.task_group_id)?.name ?? ''} · {getTaskStatus($activeProject, node.task)?.name ?? ''}
              </text>
              <text x={node.x + 16} y={node.y + 61} font-size="9" fill="var(--text-muted)">{node.task.due_date ? `${$t('graph.due')} ${new Date(node.task.due_date).getMonth() + 1}/${new Date(node.task.due_date).getDate()}` : ''}</text>
              <!-- 优先级标签 -->
              <text
                x={node.x + NODE_W - 10}
                y={node.y + 18}
                font-size="9"
                text-anchor="end"
                fill={pCfg[node.task.priority].color}
              >{pLabel(node.task.priority)}</text>
            </g>
          {/each}
        </svg>
      </div>

      <!-- Legend -->
      <div class="legend">
        <div class="legend-group">
          <span class="legend-title">{$t('graph.priority')}:</span>
          <span class="legend-item"><span class="legend-color" style="background:#ef4444"></span>{$t('priority.high')}</span>
          <span class="legend-item"><span class="legend-color" style="background:#f59e0b"></span>{$t('priority.medium')}</span>
          <span class="legend-item"><span class="legend-color" style="background:#10b981"></span>{$t('priority.low')}</span>
        </div>
        <div class="legend-group">
          <span class="legend-title">{$t('graph.status')}:</span>
          {#each legendStatuses as status}<span class="legend-item"><span class="legend-dot" style="background:{status.color}"></span>{status.name}</span>{/each}
        </div>
        <div class="legend-group">
          <span class="legend-title">{$t('graph.arrows')}:</span>
          <span class="legend-item">{$t('graph.arrowDesc')}</span>
        </div>
      </div>
    {/if}
  {:else}
    <div class="empty">{$t('graph.selectProject')}</div>
  {/if}
</div>

<style>
  .graph-view { flex: 1; display: flex; flex-direction: column; overflow: hidden; }
  .graph-header { display: flex; align-items: center; gap: 10px; padding: 16px 24px; border-bottom: 1px solid var(--border); }
  .graph-header .dot { width: 12px; height: 12px; border-radius: 50%; }
  .graph-header h2 { font-size: 16px; font-weight: 600; }
  .graph-header .meta { font-size: 12px; color: var(--text-muted); }
  .group-filter { margin-left: auto; max-width: 180px; min-height: 30px; padding: 4px 7px; border: 1px solid var(--border); background: var(--surface-raised); color: var(--text); font-size: 11px; }
  .empty { flex: 1; display: flex; align-items: center; justify-content: center; color: var(--text-muted); font-size: 14px; }

  .graph-scroll { flex: 1; overflow: hidden; padding: 24px; cursor: grab; }
  .graph-scroll.grabbing { cursor: grabbing; }
  .graph-svg { display: block; }

  .node-group { cursor: pointer; }
  .node-group:hover rect:first-child { stroke: var(--accent); }
  .done-text { text-decoration: line-through; fill: var(--text-muted) !important; }

  .legend { display: flex; gap: 24px; padding: 12px 24px; border-top: 1px solid var(--border); background: var(--sidebar); flex-wrap: wrap; }
  .legend-group { display: flex; align-items: center; gap: 12px; }
  .legend-title { font-size: 11px; color: var(--text-muted); font-weight: 600; }
  .legend-item { display: flex; align-items: center; gap: 4px; font-size: 11px; color: var(--text-secondary); }
  .legend-color { width: 10px; height: 10px; border-radius: 2px; }
  .legend-dot { width: 8px; height: 8px; border-radius: 50%; }

  @media (max-width: 768px) {
    .graph-view { padding: 12px; }
  }
</style>
