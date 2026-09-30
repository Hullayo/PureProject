<script lang="ts">
/**
 * Calendar — 日历视图（任务时间线条 + 日期任务面板）
 *
 * 左侧：月历网格，每个有截止日期的任务从开始日到截止日画一条彩色线条。
 * 右侧：点击日期后显示该日所有任务（开始 + 截止）的详情列表。
 */

  import { activeProject, taskGroupFilter, selectTask } from '$lib/stores';
  import type { Task, Priority } from '$lib/types';
  import { get } from 'svelte/store';
  import { t, tf } from '$lib/i18n';
  import Icon from '$lib/components/shared/Icon.svelte';
  import { getTaskStatus, isTaskClosed, isTaskCompleted } from '$lib/utils/task-status';

  const pCfg: Record<Priority, string> = {
    high: '#ef4444', medium: '#f59e0b', low: '#10b981'
  };
  const priorityLabels: Record<string, string> = { high: '高', medium: '中', low: '低' };

  let year = $state(new Date().getFullYear());
  let month = $state(new Date().getMonth());
  let selectedDate = $state<string | null>(null); // YYYY-MM-DD
  let groups = $derived(($activeProject?.task_groups ?? []).filter(group => !group.archived).sort((a,b) => a.sort_order - b.sort_order));
  let displayedTasks = $derived(($activeProject?.tasks ?? []).filter(task => $taskGroupFilter === 'all' || task.task_group_id === $taskGroupFilter));

  const weekDays = $derived(get(t)('calendar.weekdays').split(','));

  function prevMonth() { if (month === 0) { month = 11; year--; } else month--; }
  function nextMonth() { if (month === 11) { month = 0; year++; } else month++; }
  function goToday() { const now = new Date(); year = now.getFullYear(); month = now.getMonth(); selectedDate = null; }

  function isToday(y: number, m: number, d: number): boolean {
    const now = new Date(); return now.getFullYear() === y && now.getMonth() === m && now.getDate() === d;
  }

  function dateStr(y: number, m: number, d: number): string {
    return `${y}-${String(m + 1).padStart(2, '0')}-${String(d).padStart(2, '0')}`;
  }

  // 生成日历网格
  function getCalendarDays(): { day: number; month: number; year: number; current: boolean }[] {
    const firstDay = new Date(year, month, 1);
    const lastDay = new Date(year, month + 1, 0);
    const daysInMonth = lastDay.getDate();
    let startWeekday = firstDay.getDay() - 1;
    if (startWeekday < 0) startWeekday = 6;
    const days: { day: number; month: number; year: number; current: boolean }[] = [];
    const prevLast = new Date(year, month, 0);
    for (let i = startWeekday - 1; i >= 0; i--)
      days.push({ day: prevLast.getDate() - i, month: month - 1 < 0 ? 11 : month - 1, year: month - 1 < 0 ? year - 1 : year, current: false });
    for (let d = 1; d <= daysInMonth; d++)
      days.push({ day: d, month, year, current: true });
    const remaining = 42 - days.length;
    for (let d = 1; d <= remaining; d++)
      days.push({ day: d, month: month + 1 > 11 ? 0 : month + 1, year: month + 1 > 11 ? year + 1 : year, current: false });
    return days;
  }

  // ─── 任务时间范围计算 ─────────────────────────────────────────────

  interface TaskLine {
    taskId: string; title: string; color: string; start: string; end: string;
  }

  const taskLines = $derived.by((): TaskLine[] => {
    const proj = $activeProject;
    if (!proj) return [];
    const projCreated = proj.created_at.slice(0, 10);
    return displayedTasks
      .filter(t => t.due_date)
      .map(t => {
        let start = projCreated;
        if (t.start_offset != null && t.start_offset > 0) {
          const d = new Date(projCreated);
          d.setDate(d.getDate() + t.start_offset);
          start = dateStr(d.getFullYear(), d.getMonth(), d.getDate());
        }
        const end = t.due_date!;
        if (start > end) start = end;
        return { taskId: t.id, title: t.title, color: pCfg[t.priority], start, end };
      })
      .filter(l => l.start <= l.end);
  });

  function linesOnDay(y: number, m: number, d: number): TaskLine[] {
    const ds = dateStr(y, m, d);
    return taskLines.filter(l => ds >= l.start && ds <= l.end);
  }

  // ─── 选定日期的所有任务 ───────────────────────────────────────────

  function getDateTasks(targetDate: string): { task: Task; type: 'start' | 'due' | 'both' | 'active' }[] {
    if (!$activeProject) return [];
    const result: { task: Task; type: 'start' | 'due' | 'both' | 'active' }[] = [];
    const projCreated = $activeProject.created_at.slice(0, 10);

    for (const t of displayedTasks) {
      if (!t.due_date) continue; // 没有截止日期的任务不纳入

      // 计算任务的开始日
      let start = projCreated;
      if (t.start_offset != null && t.start_offset > 0) {
        const d = new Date(projCreated);
        d.setDate(d.getDate() + t.start_offset);
        start = dateStr(d.getFullYear(), d.getMonth(), d.getDate());
      }
      const end = t.due_date;
      const isStart = start === targetDate;
      const isDue = end === targetDate;
      const inRange = targetDate >= start && targetDate <= end;

      if (!inRange) continue; // 不在任务范围内的日期不显示

      if (isStart && isDue) result.push({ task: t, type: 'both' });
      else if (isStart) result.push({ task: t, type: 'start' });
      else if (isDue) result.push({ task: t, type: 'due' });
      else result.push({ task: t, type: 'active' });
    }
    return result;
  }

  const selectedTasks = $derived(selectedDate ? getDateTasks(selectedDate) : []);

  function overdue(task: Task): boolean {
    return !!task.due_date && !isTaskClosed($activeProject!, task) && new Date(task.due_date) < new Date();
  }

  function selectDay(y: number, m: number, d: number) {
    selectedDate = selectedDate === dateStr(y, m, d) ? null : dateStr(y, m, d);
  }

  function fmtDateNice(ymd: string): string {
    const d = new Date(ymd + 'T00:00:00');
    const ds = d.toLocaleDateString('zh-CN', { weekday: 'long', month: 'long', day: 'numeric' });
    return ds;
  }

  // 当天截止但不属于选定日期的任务（在格子内显示）
  function tasksForDay(day: number): Task[] {
    if (!$activeProject) return [];
    const ds = dateStr(year, month, day);
    return displayedTasks.filter(t => t.due_date === ds);
  }
  function tasksStartingOnDay(day: number): Task[] {
    if (!$activeProject) return [];
    const ds = dateStr(year, month, day);
    const projCreated = $activeProject.created_at.slice(0, 10);
    return displayedTasks.filter(t => {
      if (!t.due_date) return false;
      let start = projCreated;
      if (t.start_offset != null && t.start_offset > 0) {
        const d = new Date(projCreated);
        d.setDate(d.getDate() + t.start_offset);
        start = dateStr(d.getFullYear(), d.getMonth(), d.getDate());
      }
      return start === ds;
    });
  }
</script>

<div class="calendar">
  {#if $activeProject}
    <div class="calendar-header">
      <span class="dot" style="background:{$activeProject.color}"></span>
      <h2>{$activeProject.name}</h2>
      <select class="group-filter" aria-label="任务组筛选" bind:value={$taskGroupFilter}><option value="all">全部任务组</option>{#each groups as group}<option value={group.id}>{group.name}</option>{/each}</select>
      {#if taskLines.length > 0}
        <span class="header-range">{taskLines.length} 个任务有时间线</span>
      {/if}
    </div>

    <div class="calendar-nav">
      <button class="nav-btn" onclick={prevMonth}>&lt;</button>
      <span class="nav-title">{$tf('calendar.monthTitle', { year, month: month + 1 })}</span>
      <button class="nav-btn" onclick={nextMonth}>&gt;</button>
      <button class="nav-today" onclick={goToday}>{$t('calendar.today')}</button>
      {#if selectedDate}
        <button class="nav-close-panel" onclick={() => selectedDate = null}>关闭面板</button>
      {/if}
    </div>

    <div class="calendar-body">
      <!-- 左侧：日历网格 -->
      <div class="calendar-left">
        <div class="calendar-grid">
          {#each weekDays as wd}
            <div class="weekday">{wd}</div>
          {/each}
          {#each getCalendarDays() as cell}
            {@const cellDate = dateStr(cell.year, cell.month, cell.day)}
            {@const lines = linesOnDay(cell.year, cell.month, cell.day)}
            {@const dueTasks = tasksForDay(cell.day)}
            {@const startTasks = tasksStartingOnDay(cell.day)}
            <div
              class="day-cell"
              class:other={!cell.current}
              class:today={cell.current && isToday(cell.year, cell.month, cell.day)}
              class:selected={selectedDate === cellDate}
              role="button" tabindex="0"
              aria-label="{cell.month + 1}/{cell.day}"
              onclick={() => selectDay(cell.year, cell.month, cell.day)}
              onkeydown={(e) => { if (e.key === 'Enter') selectDay(cell.year, cell.month, cell.day); }}
            >
              {#each lines as l, i}
                {@const isStart = l.start === cellDate}
                {@const isEnd   = l.end   === cellDate}
                <div class="task-line"
                  class:line-start={isStart} class:line-end={isEnd}
                  style="top:{i * 4}px;background:{l.color}"
                  title="{l.title}: {l.start} → {l.end}">
                </div>
              {/each}

              <span class="day-num" class:today-num={cell.current && isToday(cell.year, cell.month, cell.day)}
                style:margin-top={lines.length > 0 ? `${lines.length * 4 + 1}px` : '0'}>
                {cell.day}
              </span>
              <div class="day-tasks">
                {#each startTasks as task}
                  <div class="day-task day-task-start"
                    style="border-left:3px solid {pCfg[task.priority]}"
                    onclick={(e) => { e.stopPropagation(); selectTask($activeProject!.id, task.id); }}
                    onkeydown={(e) => { if (e.key === 'Enter') selectTask($activeProject!.id, task.id); }}
                    role="button" tabindex="0">
                    {#if task.recurrence}<span class="rec-prefix" title={$t('task.recurring')}>↻</span>{/if}{task.title}
                  </div>
                {/each}
                {#each dueTasks as task}
                  <div class="day-task"
                    style="border-left:3px solid {pCfg[task.priority]}"
                    class:overdue={overdue(task)}
                    class:done={isTaskCompleted($activeProject, task)}
                    onclick={(e) => { e.stopPropagation(); selectTask($activeProject!.id, task.id); }}
                    onkeydown={(e) => { if (e.key === 'Enter') selectTask($activeProject!.id, task.id); }}
                    role="button" tabindex="0">
                    {#if task.recurrence}<span class="rec-prefix" title={$t('task.recurring')}>↻</span>{/if}{task.title}
                  </div>
                {/each}
              </div>
            </div>
          {/each}
        </div>
      </div>

      <!-- 右侧：日期任务详情面板 -->
      {#if selectedDate}
        <div class="calendar-sidebar">
          <div class="sidebar-date-header">
            <span class="sidebar-date">{fmtDateNice(selectedDate)}</span>
            <span class="sidebar-task-count">{selectedTasks.length} 个任务</span>
            <button class="sidebar-close" onclick={() => selectedDate = null}>&times;</button>
          </div>

          <div class="sidebar-task-list">
            {#if selectedTasks.length === 0}
              <div class="sidebar-empty">该日无任务</div>
            {:else}

            {#each selectedTasks as st}
              <div
                class="sidebar-task-card"
                class:card-start={st.type === 'start'}
                class:card-due={st.type === 'due'}
                class:card-both={st.type === 'both'}
                class:card-active={st.type === 'active'}
                onclick={() => selectTask($activeProject!.id, st.task.id)}
                onkeydown={(e) => { if (e.key === 'Enter') selectTask($activeProject!.id, st.task.id); }}
                role="button" tabindex="0"
              >
                <div class="card-top">
                  <span class="card-priority" style="background:{pCfg[st.task.priority]}"></span>
                  <span class="card-title">{st.task.title}</span>
                  <span class="card-badge" class:badge-start={st.type === 'start'} class:badge-due={st.type === 'due'} class:badge-both={st.type === 'both'} class:badge-active={st.type === 'active'}>
                    {st.type === 'start' ? '开始' : st.type === 'due' ? '截止' : st.type === 'both' ? '开始·截止' : '进行中'}
                  </span>
                </div>
                {#if st.task.description}
                  <p class="card-desc">{st.task.description}</p>
                {/if}
                <div class="card-meta">
                  <span class="card-status" class:status-done={isTaskCompleted($activeProject, st.task)}>{getTaskStatus($activeProject, st.task)?.name ?? '未知状态'}</span>
                  <span class="card-pri-label">{priorityLabels[st.task.priority]}优先级</span>
                  {#if st.task.due_date}
                    <span class="card-due-label" class:overdue-label={overdue(st.task)}>
                      截止 {st.task.due_date}
                    </span>
                  {/if}
                  {#if st.task.subtasks && st.task.subtasks.length > 0}
                    <span class="card-subtask-count">
                      {st.task.subtasks.filter(s => s.done).length}/{st.task.subtasks.length}
                    </span>
                  {/if}
                </div>
              </div>
            {/each}
          {/if}
          </div>
        </div>
      {/if}
    </div>
  {:else}
    <div class="empty">{$t('calendar.selectProject')}</div>
  {/if}
</div>

<style>
  .rec-prefix { color: var(--accent); margin-right: 2px; }
  .calendar { flex: 1; display: flex; flex-direction: column; overflow: hidden; }
  .calendar-header { display: flex; align-items: center; gap: 10px; padding: 16px 24px; border-bottom: 1px solid var(--border); }
  .calendar-header .dot { width: 12px; height: 12px; border-radius: 50%; }
  .calendar-header h2 { font-size: 16px; font-weight: 600; }
  .group-filter { margin-left: auto; max-width: 180px; min-height: 30px; padding: 4px 7px; border: 1px solid var(--border); background: var(--surface-raised); color: var(--text); font-size: 11px; }
  .header-range { font-size: 11px; color: var(--text-muted); padding: 2px 8px; background: var(--surface); border-radius: 4px; }
  .empty { flex: 1; display: flex; align-items: center; justify-content: center; color: var(--text-muted); font-size: 14px; }

  .calendar-nav { display: flex; align-items: center; gap: 12px; padding: 12px 24px; border-bottom: 1px solid var(--border); }
  .nav-btn { padding: 4px 10px; font-size: 14px; border-radius: 6px; color: var(--text-secondary); }
  .nav-btn:hover { background: var(--surface); }
  .nav-title { font-size: 15px; font-weight: 600; color: var(--text); min-width: 100px; text-align: center; }
  .nav-today { padding: 4px 12px; font-size: 12px; border-radius: 6px; background: var(--surface); color: var(--text-secondary); border: 1px solid var(--border); margin-left: auto; }
  .nav-today:hover { border-color: var(--accent); color: var(--accent); }
  .nav-close-panel { padding: 4px 10px; font-size: 11px; border-radius: 6px; background: var(--surface); color: var(--text-secondary); border: 1px solid var(--border); }
  .nav-close-panel:hover { border-color: var(--red); color: var(--red); }

  /* ── 左右分栏 ── */
  .calendar-body { flex: 1; display: flex; overflow: hidden; }

  .calendar-left { flex: 1; display: flex; flex-direction: column; overflow: hidden; min-width: 0; }

  /* ── 日历网格 ── */
  .calendar-grid { flex: 1; display: grid; grid-template-columns: repeat(7, 1fr); grid-template-rows: 28px repeat(6, 1fr); overflow: auto; padding: 0 24px 16px; }
  .weekday { font-size: 11px; font-weight: 600; color: var(--text-muted); text-align: center; padding: 6px 0; border-bottom: 1px solid var(--border); }

  .day-cell {
    border-right: 1px solid var(--border);
    border-bottom: 1px solid var(--border);
    padding: 4px 6px;
    min-height: 0;
    overflow-y: auto;
    display: flex;
    flex-direction: column;
    position: relative;
    cursor: pointer;
  }
  .day-cell:nth-child(7n) { border-right: none; }
  .day-cell.other { background: var(--surface); }
  .day-cell.today { background: var(--accent-light); }
  .day-cell.selected { outline: 2px solid var(--accent); outline-offset: -2px; z-index: 3; }

  /* ── 任务线条 ── */
  .task-line {
    position: absolute; left: 2px; right: 2px; height: 3px; border-radius: 0;
    z-index: 1; opacity: 0.5; pointer-events: none;
  }
  .task-line.line-start { opacity: 0.85; border-top-left-radius: 4px; border-bottom-left-radius: 4px; left: 3px; }
  .task-line.line-start::after {
    content: ''; position: absolute; left: -2px; top: -1.5px;
    width: 6px; height: 6px; border-radius: 50%; background: inherit; opacity: 1;
  }
  .task-line.line-end { border-top-right-radius: 4px; border-bottom-right-radius: 4px; right: 3px; }
  .task-line.line-end::after {
    content: ''; position: absolute; right: -2px; top: -1.5px;
    width: 6px; height: 6px; border-radius: 50%; background: inherit; opacity: 1;
  }

  .day-num { font-size: 12px; font-weight: 500; color: var(--text-muted); margin-bottom: 2px; position: relative; z-index: 2; }
  .day-num.today-num { color: var(--accent); font-weight: 700; }

  .day-tasks { flex: 1; overflow: hidden; display: flex; flex-direction: column; gap: 2px; position: relative; z-index: 2; }
  .day-task { font-size: 10px; padding: 2px 4px; border-radius: 3px; background: var(--bg); color: var(--text); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; cursor: pointer; transition: background 0.1s; }
  .day-task-start { background: color-mix(in srgb, var(--accent) 8%, transparent); font-weight: 500; }
  .day-task:hover { background: var(--surface); }
  .day-task.done { opacity: 0.5; text-decoration: line-through; }
  .day-task.overdue { color: var(--red); }

  /* ═══ 右侧任务面板 ═══ */
  .calendar-sidebar {
    width: 300px;
    border-left: 1px solid var(--border);
    background: var(--bg);
    display: flex;
    flex-direction: column;
    overflow: hidden;
    flex-shrink: 0;
  }
  .sidebar-date-header {
    display: flex; align-items: center; gap: 8px;
    padding: 12px 16px; border-bottom: 1px solid var(--border);
  }
  .sidebar-date { font-size: 14px; font-weight: 600; }
  .sidebar-task-count { font-size: 11px; color: var(--text-muted); margin-left: auto; }
  .sidebar-close { font-size: 18px; color: var(--text-muted); padding: 0 4px; }
  .sidebar-close:hover { color: var(--red); }

  .sidebar-task-list { flex: 1; overflow-y: auto; padding: 8px; display: flex; flex-direction: column; gap: 6px; }
  .sidebar-empty { text-align: center; color: var(--text-muted); font-size: 13px; padding: 32px 0; }

  .sidebar-task-card {
    padding: 10px 12px; border-radius: 8px; background: var(--surface);
    border: 1px solid var(--border); cursor: pointer; transition: all 0.12s;
  }
  .sidebar-task-card:hover { border-color: var(--accent); }
  .card-start { border-left: 3px solid var(--accent); }
  .card-due { border-left: 3px solid var(--red); }
  .card-both { border-left: 3px solid var(--green); }
  .card-active { border-left: 3px solid var(--accent); opacity: 0.85; }

  .card-top { display: flex; align-items: center; gap: 6px; margin-bottom: 4px; }
  .card-priority { width: 8px; height: 8px; border-radius: 50%; flex-shrink: 0; }
  .card-title { font-size: 13px; font-weight: 600; flex: 1; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  .card-badge { font-size: 9px; padding: 1px 6px; border-radius: 8px; flex-shrink: 0; }
  .badge-start { background: color-mix(in srgb, var(--accent) 15%, transparent); color: var(--accent); }
  .badge-due { background: rgba(239,68,68,0.12); color: #ef4444; }
  .badge-both { background: rgba(16,185,129,0.12); color: #10b981; }
  .badge-active { background: color-mix(in srgb, var(--accent) 10%, transparent); color: var(--accent); }

  .card-desc { font-size: 11px; color: var(--text-muted); line-height: 1.5; margin-bottom: 6px; overflow: hidden; display: -webkit-box; -webkit-line-clamp: 2; -webkit-box-orient: vertical; }

  .card-meta { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
  .card-status { font-size: 10px; padding: 1px 6px; border-radius: 4px; background: var(--bg); color: var(--text-secondary); }
  .card-status.status-done { text-decoration: line-through; opacity: 0.6; }
  .card-pri-label { font-size: 10px; color: var(--text-muted); }
  .card-due-label { font-size: 10px; color: var(--text-muted); }
  .overdue-label { color: var(--red); }
  .card-subtask-count { font-size: 10px; color: var(--text-muted); }
</style>
