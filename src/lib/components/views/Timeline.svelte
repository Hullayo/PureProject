<script lang="ts">
/**
 * Timeline — 甘特图时间线视图
 *
 * 以水平条形图展示任务的时间跨度和依赖关系。
 * 功能包括：
 * - 自动计算任务开始天数（基于依赖、start_offset 或创建日期）
 * - 月份和周数双层时间轴标头
 * - 任务条颜色按优先级区分
 * - 今日红线标记
 * - 里程碑虚线标记
 * - 逾期任务红色边框高亮
 * - 拖拽平移画布
 * - 优先级和状态图例
 *
 * @example
 * <Timeline />
 */

  import { activeProject, activeTaskId, taskGroupFilter, selectTask as openTask, createMilestone, deleteMilestone } from '$lib/stores';
  import type { Task, Priority } from '$lib/types';
  import { get } from 'svelte/store';
  import { t, tf } from '$lib/i18n';
  import { animationLevel } from '$lib/stores/animation';
  import { fade } from 'svelte/transition';
  import { getTaskStatus, isTaskClosed, isTaskCompleted } from '$lib/utils/task-status';

  const pCfg: Record<Priority, string> = {
    high: '#ef4444',
    medium: '#f59e0b',
    low: '#10b981'
  };
  let groups = $derived(($activeProject?.task_groups ?? []).filter(group => !group.archived).sort((a,b) => a.sort_order - b.sort_order));
  let displayedTasks = $derived(($activeProject?.tasks ?? []).filter(task => $taskGroupFilter === 'all' || task.task_group_id === $taskGroupFilter));
  let legendStatuses = $derived.by(() => {
    if (!$activeProject) return [];
    const source = $taskGroupFilter === 'all' ? groups.flatMap(group => group.statuses) : groups.find(group => group.id === $taskGroupFilter)?.statuses ?? [];
    return [...new Map(source.map(status => [status.id, status])).values()];
  });
  let milestoneTitle = $state('');
  let milestoneDate = $state('');

  interface ComputedTask {
    task: Task;
    startDay: number;  // 相对于项目创建日的天数
    duration: number;  // 持续天数
  }

  // 计算每个任务的实际开始天数和持续天数
  function computeTasks(tasks: Task[]): ComputedTask[] {
    const projectStart = tasks.length > 0
      ? new Date(tasks.reduce((min, t) => t.created_at < min ? t.created_at : min, tasks[0].created_at))
      : new Date();
    projectStart.setHours(0, 0, 0, 0);

    const cache = new Map<string, ComputedTask>();

    function getDuration(task: Task): number {
      if (task.due_date) {
        const start = new Date(task.created_at);
        start.setHours(0, 0, 0, 0);
        const end = new Date(task.due_date);
        end.setHours(0, 0, 0, 0);
        return Math.max(1, Math.ceil((end.getTime() - start.getTime()) / 86400000));
      }
      return 7;
    }

    function compute(task: Task): ComputedTask {
      if (cache.has(task.id)) return cache.get(task.id)!;

      let startDay: number;

      if (task.dependencies.length > 0) {
        // 有依赖：从所有依赖任务的结束时间 + offset 取最大值
        startDay = 0;
        for (const dep of task.dependencies) {
          const depTask = tasks.find(t => t.id === dep.taskId);
          if (depTask) {
            const depComputed = compute(depTask);
            const depEnd = depComputed.startDay + depComputed.duration;
            startDay = Math.max(startDay, depEnd + dep.dayOffset - depComputed.duration);
          }
        }
      } else if (task.start_offset !== null) {
        // 根任务：使用 start_offset
        startDay = task.start_offset;
      } else {
        // 默认：从创建日计算
        const created = new Date(task.created_at);
        created.setHours(0, 0, 0, 0);
        startDay = Math.max(0, Math.floor((created.getTime() - projectStart.getTime()) / 86400000));
      }

      const ct: ComputedTask = { task, startDay, duration: getDuration(task) };
      cache.set(task.id, ct);
      return ct;
    }

    return tasks.map(t => compute(t));
  }

  // 根据计算结果确定时间线范围
  function getTimelineRange(computed: ComputedTask[], milestoneDates: string[]): { startDay: number; totalDays: number; startDate: Date } {
    if (computed.length === 0 && milestoneDates.length === 0) {
      const now = new Date();
      return { startDay: -7, totalDays: 44, startDate: new Date(now.getTime() - 7 * 86400000) };
    }
    const projectStart = computed.length > 0
      ? new Date(computed[0].task.created_at)
      : new Date();
    projectStart.setHours(0, 0, 0, 0);

    const taskDays = computed.flatMap(c => [c.startDay, c.startDay + c.duration]);
    const msDays = milestoneDates.map(d => {
      const dt = new Date(d); dt.setHours(0,0,0,0);
      return Math.floor((dt.getTime() - projectStart.getTime()) / 86400000);
    });
    const allDays = [...taskDays, ...msDays];

    const minDay = Math.min(-7, ...allDays);
    const maxDay = Math.max(37, ...allDays.map(d => d + 7));
    const totalDays = maxDay - minDay;
    const startDate = new Date(projectStart.getTime() + minDay * 86400000);

    return { startDay: minDay, totalDays, startDate };
  }

  function dayToPercent(day: number, startDay: number, totalDays: number): number {
    return ((day - startDay) / totalDays) * 100;
  }

  function getTodayOffset(startDay: number, totalDays: number, startDate: Date): number {
    const now = new Date();
    now.setHours(0, 0, 0, 0);
    const daysSinceStart = (now.getTime() - startDate.getTime()) / 86400000;
    return (daysSinceStart / totalDays) * 100;
  }

  function getWeekDays(startDate: Date, totalDays: number): { label: string; offset: number }[] {
    const result: { label: string; offset: number }[] = [];
    const d = new Date(startDate);
    d.setDate(d.getDate() - d.getDay());
    for (let i = 0; i < totalDays + 7; i++) {
      const offset = ((d.getTime() - startDate.getTime()) / (totalDays * 86400000)) * 100;
      if (offset >= 0 && offset <= 100 && d.getDay() === 1) {
        result.push({ label: `${d.getMonth() + 1}/${d.getDate()}`, offset });
      }
      d.setDate(d.getDate() + 1);
    }
    return result;
  }

  function getMonthDays(startDate: Date, totalDays: number): { label: string; offset: number }[] {
    const result: { label: string; offset: number }[] = [];
    const d = new Date(startDate.getFullYear(), startDate.getMonth(), 1);
    for (let i = 0; i < 12; i++) {
      const offset = ((d.getTime() - startDate.getTime()) / (totalDays * 86400000)) * 100;
      if (offset >= 0 && offset <= 100) {
        const monthNames = get(t)('timeline.months').split(',');
        result.push({ label: monthNames[d.getMonth()], offset });
      }
      d.setMonth(d.getMonth() + 1);
    }
    return result;
  }

  function isOverdue(task: Task): boolean {
    if (!task.due_date || isTaskClosed($activeProject!, task)) return false;
    return new Date(task.due_date) < new Date();
  }

  // 计算里程碑在时间线上的位置
  function computeMilestoneDay(dateStr: string, projectStart: Date): number {
    const d = new Date(dateStr);
    d.setHours(0, 0, 0, 0);
    return Math.floor((d.getTime() - projectStart.getTime()) / 86400000);
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

  function addMilestone() {
    if (!$activeProject || !milestoneTitle.trim() || !milestoneDate) return;
    createMilestone($activeProject.id, milestoneTitle, milestoneDate, $activeProject.color);
    milestoneTitle = '';
    milestoneDate = '';
  }

</script>

<div class="timeline">
  {#if $activeProject}
    <div class="timeline-header">
      <span class="dot" style="background:{$activeProject.color}"></span>
      <h2>{$activeProject.name}</h2>
      <span class="meta">{$t('timeline.viewTitle')} · {$tf('task.count', { n: $activeProject.tasks.length })}</span>
      <select class="group-filter" bind:value={$taskGroupFilter}><option value="all">全部任务组</option>{#each groups as group}<option value={group.id}>{group.name}</option>{/each}</select>
    </div>

    <div class="milestone-tools">
      <strong>项目里程碑</strong>
      {#each $activeProject.milestones ?? [] as milestone}<span class="milestone-chip" style="border-color:{milestone.color}">{milestone.title} · {milestone.date}<button title="删除里程碑" aria-label="删除里程碑" onclick={() => deleteMilestone($activeProject!.id, milestone.id)}>×</button></span>{/each}
      <input placeholder="里程碑名称" bind:value={milestoneTitle} /><input type="date" bind:value={milestoneDate} /><button onclick={addMilestone}>添加</button>
    </div>

    {#if displayedTasks.length === 0}
      <div class="empty">{$t('timeline.noTasks')}</div>
    {:else}
      {@const computed = computeTasks(displayedTasks)}
      {@const range = getTimelineRange(computed, $activeProject.milestones?.map(m => m.date) ?? [])}
      {@const weeks = getWeekDays(range.startDate, range.totalDays)}
      {@const months = getMonthDays(range.startDate, range.totalDays)}
      {@const today = getTodayOffset(range.startDay, range.totalDays, range.startDate)}

      <div class="gantt">
        <!-- Month header -->
        <div class="gantt-header">
          <div class="task-label-header">{$t('timeline.task')}</div>
          <div class="timeline-header-row">
            {#each months as m}
              <span class="month-label" style="left:{m.offset}%">{m.label}</span>
            {/each}
          </div>
        </div>

        <!-- Week header -->
        <div class="gantt-subheader">
          <div class="task-label-sub"></div>
          <div class="timeline-sub-row">
            {#each weeks as w}
              <span class="week-label" style="left:{w.offset}%">{w.label}</span>
            {/each}
          </div>
        </div>

        <!-- Task rows -->
        <div class="gantt-body" class:grabbing={dragging} bind:this={scrollEl} onmousedown={onMouseDown} ontouchstart={onTouchStart} ontouchmove={onTouchMove} ontouchend={onTouchEnd}>
          {#each computed as ct, idx (ct.task.id)}
            {@const left = dayToPercent(ct.startDay, range.startDay, range.totalDays)}
            {@const width = dayToPercent(ct.startDay + ct.duration, range.startDay, range.totalDays) - left}
            <div class="gantt-row" class:selected={$activeTaskId === ct.task.id} role="button" tabindex="0" aria-label={ct.task.title} onclick={() => selectTask(ct.task.id)} onkeydown={(e) => { if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); selectTask(ct.task.id); } }} in:fade={{ duration: $animationLevel === 'none' ? 0 : ($animationLevel === 'rich' ? 250 : 150) }}>
              <div class="task-label">
                <span class="task-status-dot" style="background:{getTaskStatus($activeProject, ct.task)?.color ?? 'var(--text-muted)'}"></span>
                <span class="task-name" class:done={isTaskCompleted($activeProject, ct.task)}>{ct.task.title}</span>
              </div>
              <div class="timeline-bar-area">
                {#each weeks as w}
                  <div class="grid-line" style="left:{w.offset}%"></div>
                {/each}
                <div class="today-line" style="left:{today}%"></div>
                {#each $activeProject.milestones ?? [] as ms}
                  {@const msDay = computeMilestoneDay(ms.date, range.startDate)}
                  {@const msPct = dayToPercent(msDay, range.startDay, range.totalDays)}
                  {#if msPct >= 0 && msPct <= 100}
                    {#if idx === 0}
                      <div class="milestone-line" style="left:{msPct}%;border-color:{ms.color}">
                        <span class="milestone-label" style="color:{ms.color}">{ms.title}</span>
                      </div>
                    {:else}
                      <div class="milestone-line" style="left:{msPct}%;border-color:{ms.color}"></div>
                    {/if}
                  {/if}
                {/each}
                <div
                  class="bar"
                  class:overdue={isOverdue(ct.task)}
                  class:done={isTaskCompleted($activeProject, ct.task)}
                  style="left:{left}%;width:{Math.max(2, width)}%;background:{pCfg[ct.task.priority]}"
                >
                  <span class="bar-label">{ct.task.title} ({$tf('timeline.days', { n: ct.duration })})</span>
                </div>
              </div>
            </div>
          {/each}
        </div>
      </div>

      <!-- Legend -->
      <div class="legend">
        <div class="legend-group">
          <span class="legend-title">{$t('timeline.priority')}</span>
          <span class="legend-item"><span class="legend-color" style="background:#ef4444"></span>{$t('priority.high')}</span>
          <span class="legend-item"><span class="legend-color" style="background:#f59e0b"></span>{$t('priority.medium')}</span>
          <span class="legend-item"><span class="legend-color" style="background:#10b981"></span>{$t('priority.low')}</span>
        </div>
        <div class="legend-group">
          <span class="legend-title">{$t('timeline.status')}</span>
          {#each legendStatuses as status}<span class="legend-item"><span class="legend-color" style="background:{status.color}"></span>{status.name}</span>{/each}
        </div>
      </div>
    {/if}
  {:else}
    <div class="empty">{$t('timeline.selectProject')}</div>
  {/if}
</div>

<style>
  .timeline { flex: 1; display: flex; flex-direction: column; overflow: hidden; }
  .timeline-header { display: flex; align-items: center; gap: 10px; padding: 16px 24px; border-bottom: 1px solid var(--border); }
  .timeline-header .dot { width: 12px; height: 12px; border-radius: 50%; }
  .timeline-header h2 { font-size: 16px; font-weight: 600; }
  .timeline-header .meta { font-size: 12px; color: var(--text-muted); }
  .group-filter { margin-left: auto; max-width: 180px; min-height: 30px; padding: 4px 7px; border: 1px solid var(--border); background: var(--surface-raised); color: var(--text); font-size: 11px; }
  .milestone-tools { min-height: 42px; display: flex; align-items: center; gap: 7px; padding: 6px 24px; border-bottom: 1px solid var(--border); overflow-x: auto; }
  .milestone-tools strong { font-size: 11px; white-space: nowrap; }
  .milestone-tools input { min-height: 28px; padding: 4px 7px; border: 1px solid var(--border); background: var(--surface-raised); color: var(--text); font-size: 11px; }
  .milestone-tools > button { min-height: 28px; padding: 0 9px; background: var(--accent); color: var(--accent-contrast); }
  .milestone-chip { display: inline-flex; align-items: center; gap: 5px; padding: 3px 6px; border: 1px solid; font-size: 10px; white-space: nowrap; }
  .milestone-chip button { color: var(--text-muted); }
  .empty { flex: 1; display: flex; align-items: center; justify-content: center; color: var(--text-muted); font-size: 14px; }

  .gantt { flex: 1; display: flex; flex-direction: column; overflow: hidden; }

  .gantt-header { display: flex; border-bottom: 1px solid var(--border); background: var(--sidebar); }
  .task-label-header { width: 200px; padding: 8px 16px; font-size: 12px; font-weight: 600; color: var(--text-secondary); flex-shrink: 0; border-right: 1px solid var(--border); }
  .timeline-header-row { flex: 1; position: relative; height: 28px; }
  .month-label { position: absolute; top: 6px; font-size: 12px; font-weight: 600; color: var(--text-secondary); transform: translateX(-50%); }

  .gantt-subheader { display: flex; border-bottom: 1px solid var(--border); background: var(--sidebar); }
  .task-label-sub { width: 200px; flex-shrink: 0; border-right: 1px solid var(--border); }
  .timeline-sub-row { flex: 1; position: relative; height: 24px; }
  .week-label { position: absolute; top: 4px; font-size: 10px; color: var(--text-muted); transform: translateX(-50%); }

  .gantt-body { flex: 1; overflow: hidden; cursor: grab; }
  .gantt-body.grabbing { cursor: grabbing; }
  .gantt-row { display: flex; height: 36px; border-bottom: 1px solid var(--border); cursor: pointer; transition: background 0.15s; }
  .gantt-row:hover { background: var(--surface); }
  .gantt-row.selected { background: var(--accent-light); }

  .task-label { width: 200px; display: flex; align-items: center; gap: 8px; padding: 0 16px; flex-shrink: 0; border-right: 1px solid var(--border); overflow: hidden; }
  .task-status-dot { width: 8px; height: 8px; border-radius: 50%; flex-shrink: 0; }
  .task-name { font-size: 12px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .task-name.done { text-decoration: line-through; color: var(--text-muted); }

  .timeline-bar-area { flex: 1; position: relative; }
  .grid-line { position: absolute; top: 0; bottom: 0; width: 1px; background: var(--border); opacity: 0.5; }
  .today-line { position: absolute; top: 0; bottom: 0; width: 2px; background: var(--red); z-index: 2; opacity: 0.7; }
  .milestone-line { position: absolute; top: 0; bottom: 0; width: 0; border-left: 2px dashed; z-index: 1; opacity: 0.8; pointer-events: none; }
  .milestone-label { position: absolute; top: -18px; left: 4px; font-size: 10px; font-weight: 600; white-space: nowrap; max-width: 120px; overflow: hidden; text-overflow: ellipsis; }

  .bar { position: absolute; top: 6px; height: 24px; border-radius: 4px; display: flex; align-items: center; padding: 0 8px; min-width: 20px; opacity: 0.9; transition: opacity 0.15s; overflow: hidden; }
  .bar:hover { opacity: 1; }
  .bar.done { opacity: 0.5; }
  .bar.overdue { box-shadow: 0 0 0 2px var(--red); }
  .bar-label { font-size: 10px; color: #fff; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; font-weight: 500; }

  .legend { display: flex; gap: 24px; padding: 12px 24px; border-top: 1px solid var(--border); background: var(--sidebar); flex-wrap: wrap; }
  .legend-group { display: flex; align-items: center; gap: 12px; }
  .legend-title { font-size: 11px; color: var(--text-muted); font-weight: 600; }
  .legend-item { display: flex; align-items: center; gap: 4px; font-size: 11px; color: var(--text-secondary); }
  .legend-color { width: 10px; height: 10px; border-radius: 2px; }

  @media (max-width: 768px) {
    .timeline { overflow: visible; flex: none; }
    .timeline-header { padding: 12px; flex-wrap: wrap; }
    .task-label-header { width: 120px; padding: 8px; font-size: 11px; }
    .task-label-sub { width: 120px; }
    .task-label { width: 120px; padding: 0 8px; }
    .task-name { font-size: 11px; }
    .gantt-row { height: 32px; }
    .legend { padding: 8px 12px; gap: 12px; }
    .bar { height: 20px; top: 6px; }
    .bar-label { font-size: 9px; }
  }
</style>
