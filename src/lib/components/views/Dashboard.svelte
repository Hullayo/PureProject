<script lang="ts">
/**
 * Dashboard — 全局主界面仪表盘
 *
 * 作为应用默认启动视图，展示所有项目的概览信息：
 * - 项目卡片列表（名称、进度、任务统计、截止日期）
 * - 未来 7 天到期的任务（按优先级排序）
 * - 各项目状态分布简易统计
 *
 * 使用 Svelte 5 runes 语法（$state / $derived / $effect）。
 *
 * @example
 * <Dashboard />
 */

  import { projects, activeProjectId, pendingAction, selectTask } from '$lib/stores';
  import type { Project, Task, Priority } from '$lib/types';
  import { t } from '$lib/i18n';
  import { getTaskStatus, isTaskClosed, isTaskCompleted } from '$lib/utils/task-status';
  import Icon from '$lib/components/shared/Icon.svelte';

  type ProjectSortKey = 'updated' | 'tasks' | 'progress';
  type SortDirection = 'asc' | 'desc';

  let projectSortKey = $state<ProjectSortKey>('updated');
  let sortDirection = $state<SortDirection>('desc');

  // —— 派生数据 ——

  /** 所有未归档的项目列表 */
  const activeProjects = $derived(
    ($projects as Project[]).filter(p => !p.archived)
  );

  /** 所有项目的总任务统计 */
  const totalStats = $derived.by(() => {
    const entries = activeProjects.flatMap(project => project.tasks.filter(task => !project.task_groups.find(group => group.id === task.task_group_id)?.archived).map(task => ({ project, task })));
    return {
      total: entries.length,
      done: entries.filter(({ project, task }) => isTaskCompleted(project, task)).length,
      inProgress: entries.filter(({ project, task }) => getTaskStatus(project, task)?.category === 'active').length,
      cancelled: entries.filter(({ project, task }) => getTaskStatus(project, task)?.category === 'cancelled').length,
    };
  });

  /** 未来 7 天到期的任务（跨所有项目），按优先级排序 */
  const upcomingTasks = $derived.by(() => {
    const now = new Date();
    const today = now.toISOString().slice(0, 10);
    const end = new Date(now);
    end.setDate(end.getDate() + 7);
    const endStr = end.toISOString().slice(0, 10);

    const tasks: { task: Task; project: Project }[] = [];
    for (const p of activeProjects) {
      for (const t of p.tasks) {
        const group = p.task_groups.find(item => item.id === t.task_group_id);
        if (group && !group.archived && t.due_date && t.due_date >= today && t.due_date <= endStr && !isTaskClosed(p, t)) {
          tasks.push({ task: t, project: p });
        }
      }
    }

    // 按优先级排序：high > medium > low
    const priorityOrder: Record<Priority, number> = { high: 0, medium: 1, low: 2 };
    tasks.sort((a, b) => {
      const pa = priorityOrder[a.task.priority] ?? 1;
      const pb = priorityOrder[b.task.priority] ?? 1;
      if (pa !== pb) return pa - pb;
      return (a.task.due_date ?? '').localeCompare(b.task.due_date ?? '');
    });

    return tasks;
  });

  /** 各项目的完成率 */
  const projectStats = $derived(
    activeProjects.map(p => {
      const tasks = p.tasks.filter(task => !p.task_groups.find(group => group.id === task.task_group_id)?.archived);
      const total = tasks.length;
      const done = tasks.filter(task => isTaskCompleted(p, task)).length;
      const inProgress = tasks.filter(task => getTaskStatus(p, task)?.category === 'active').length;
      return {
        project: p,
        total,
        done,
        inProgress,
        percent: total > 0 ? Math.round((done / total) * 100) : 0,
      };
    })
  );

  /** 项目总览卡片排序；相同值按项目名稳定排序。 */
  const sortedProjectStats = $derived.by(() => {
    const direction = sortDirection === 'asc' ? 1 : -1;
    return [...projectStats].sort((a, b) => {
      let difference = 0;
      if (projectSortKey === 'updated') {
        const aTime = Date.parse(a.project.updated_at) || 0;
        const bTime = Date.parse(b.project.updated_at) || 0;
        difference = aTime - bTime;
      } else if (projectSortKey === 'tasks') {
        difference = a.total - b.total;
      } else {
        difference = a.percent - b.percent;
      }
      return difference === 0
        ? a.project.name.localeCompare(b.project.name)
        : difference * direction;
    });
  });

  const overallProgress = $derived(
    totalStats.total > 0 ? Math.round(totalStats.done / totalStats.total * 100) : 0
  );

  /** 今天日期字符串 */
  function todayStr(): string {
    return new Date().toISOString().slice(0, 10);
  }

  /** 格式化到期日（相对） */
  function fmtDue(due: string): string {
    const today = todayStr();
    if (due === today) return '今天';
    const dt = new Date(due);
    const diff = Math.round((dt.getTime() - Date.now()) / 86400000);
    if (diff === 1) return '明天';
    if (diff === 2) return '后天';
    return due;
  }

  /** 触发新建项目（通过命令面板的待执行动作机制） */
  function triggerNewProject() {
    pendingAction.set('new-project');
  }

  /** 跳转到某个项目 */
  function selectProject(id: string) {
    activeProjectId.set(id);
  }

  /** 优先级颜色 */
  const priorityColors: Record<Priority, string> = {
    high: '#ef4444',
    medium: '#f59e0b',
    low: '#10b981',
  };

  const priorityLabels: Record<Priority, string> = {
    high: '高',
    medium: '中',
    low: '低',
  };

</script>

<div class="dashboard">
  <!-- 欢迎横幅 -->
  <div class="welcome-banner">
    <div class="welcome-text">
      <h1 class="welcome-title">项目总览</h1>
    </div>
    <div class="overview-metrics" aria-label={$t('dashboard.metrics')}>
      <div class="overview-metric">
        <span class="overview-label">{$t('dashboard.totalTasks')}</span>
        <strong>{totalStats.total}</strong>
      </div>
      <div class="overview-metric">
        <span class="overview-label">{$t('dashboard.inProgress')}</span>
        <strong class="metric-active">{totalStats.inProgress}</strong>
      </div>
      <div class="overview-metric">
        <span class="overview-label">{$t('dashboard.completed')}</span>
        <strong class="metric-done">{totalStats.done}</strong>
      </div>
      <div class="overview-metric">
        <span class="overview-label">{$t('dashboard.progress')}</span>
        <strong class="metric-done">{overallProgress}%</strong>
      </div>
    </div>
  </div>

  <div class="dashboard-grid">
    <!-- 左侧：项目卡片列表 -->
    <div class="dashboard-col projects-col">
      <div class="col-header">
        <h2 class="col-title">{$t('dashboard.allProjects')}</h2>
        <div class="project-sort">
          <select class="sort-select" bind:value={projectSortKey} aria-label={$t('dashboard.sortLabel')}>
            <option value="updated">{$t('dashboard.sortUpdated')}</option>
            <option value="tasks">{$t('dashboard.sortTasks')}</option>
            <option value="progress">{$t('dashboard.sortProgress')}</option>
          </select>
          <button
            class="sort-direction"
            title={sortDirection === 'asc' ? $t('dashboard.sortAsc') : $t('dashboard.sortDesc')}
            aria-label={sortDirection === 'asc' ? $t('dashboard.sortAsc') : $t('dashboard.sortDesc')}
            onclick={() => sortDirection = sortDirection === 'asc' ? 'desc' : 'asc'}
          ><Icon name={sortDirection === 'asc' ? 'chevron-up' : 'chevron-down'} size={15} /></button>
        </div>
      </div>
      {#if projectStats.length === 0}
        <div class="empty-state">
          <p>暂无项目</p>
          <button class="action-btn primary" onclick={triggerNewProject}>创建第一个项目</button>
        </div>
      {:else}
        <div class="project-cards">
          {#each sortedProjectStats as stat (stat.project.id)}
            {@const p = stat.project}
            <button class="project-card" onclick={() => selectProject(p.id)}>
              <div class="card-top">
                <span class="card-color-dot" style="background:{p.color}"></span>
                <span class="card-name">{p.name}</span>
              </div>
              {#if p.description}
                <p class="card-desc">{p.description}</p>
              {/if}
              <div class="card-stats-row">
                <span class="card-stat">
                  <span class="stat-num">{stat.total}</span> 任务
                </span>
                <span class="card-stat">
                  <span class="stat-num done">{stat.done}</span> 完成
                </span>
                {#if p.end_date}
                  <span class="card-stat due">截止 {p.end_date}</span>
                {/if}
              </div>
              <div class="progress-bar-bg">
                <div class="progress-bar-fill" style="width:{stat.percent}%;background:{p.color}"></div>
              </div>
              <div class="card-percent">{stat.percent}%</div>
            </button>
          {/each}
        </div>
      {/if}
    </div>

    <!-- 右侧：即将到期任务 -->
    <div class="dashboard-col tasks-col">
      <div class="col-header">
        <h2 class="col-title">{$t('dashboard.upcoming')}</h2>
      </div>
      {#if upcomingTasks.length === 0}
        <div class="empty-state">
          <p>未来 7 天没有到期的任务</p>
        </div>
      {:else}
        <div class="upcoming-list">
          {#each upcomingTasks as { task, project }}
            <button class="upcoming-item" style="border-left-color:{project.color}" onclick={() => selectTask(project.id, task.id)}>
              <div class="upcoming-left">
                <span class="priority-dot" style="background:{priorityColors[task.priority]}"></span>
                <div class="upcoming-info">
                  <span class="upcoming-title">{task.title}</span>
                  <span class="upcoming-project">{project.name} · {project.task_groups.find(group => group.id === task.task_group_id)?.name}</span>
                </div>
              </div>
              <div class="upcoming-right">
                <span class="upcoming-due" class:urgent={task.due_date === todayStr()}>
                  {fmtDue(task.due_date!)}
                </span>
                <span class="upcoming-priority">{priorityLabels[task.priority]}</span>
              </div>
            </button>
          {/each}
        </div>
      {/if}

    </div>
  </div>
</div>

<style>
  .dashboard {
    flex: 1;
    display: flex;
    flex-direction: column;
    gap: 22px;
    padding: 25px 30px 30px;
    overflow-y: auto;
    /* 允许 flex 子项收缩到界面宽度，防止内部内容把容器撑破 */
    min-width: 0;
    width: 100%;
    max-width: 100%;
  }

  /* 欢迎横幅 */
  .welcome-banner {
    display: flex;
    align-items: center;
    justify-content: space-between;
    min-height: 78px;
    padding: 6px 0 17px;
    border-bottom: 3px double var(--border-strong);
  }
  .welcome-text {
    display: flex;
    align-items: center;
    padding-left: 18px;
    border-left: 3px solid var(--accent);
  }
  .welcome-title {
    font-size: 25px;
    font-weight: 700;
    color: var(--text);
    margin: 0;
    line-height: 1.25;
  }
  .overview-metrics {
    display: grid;
    grid-template-columns: repeat(4, minmax(96px, 1fr));
    border: 1px solid var(--border);
    background: var(--surface-raised);
  }
  .overview-metric {
    min-width: 96px;
    padding: 7px 12px;
    border-right: 1px solid var(--border);
    text-align: center;
  }
  .overview-metric:last-child { border-right: 0; }
  .overview-label { display: block; margin-bottom: 2px; font-size: 10px; color: var(--text-muted); white-space: nowrap; }
  .overview-metric strong { font-family: var(--font-serif); font-size: 16px; color: var(--text); }
  .overview-metric .metric-active { color: var(--accent); }
  .overview-metric .metric-done { color: var(--green); }

  .action-btn {
    display: flex;
    align-items: center;
    gap: 6px;
    padding: 8px 14px;
    font-size: 13px;
    border-radius: 2px;
    cursor: pointer;
    border: 1px solid var(--border);
    background: var(--surface);
    color: var(--text-secondary);
    transition: all 0.12s;
  }
  .action-btn:hover { border-color: var(--accent); color: var(--accent); }
  .action-btn.primary {
    background: var(--accent);
    color: var(--accent-contrast);
    border-color: var(--accent);
  }
  .action-btn.primary:hover { background: var(--accent-hover); border-color: var(--accent-hover); }

  /* 主网格：项目列表占两份，到期任务占一份，中间以边框分隔。 */
  .dashboard-grid {
    display: grid;
    grid-template-columns: minmax(0, 2fr) minmax(0, 1fr);
    gap: 0;
    flex: 1;
    min-height: 0;
    align-content: start;
  }
  .dashboard-col {
    display: flex;
    flex-direction: column;
    gap: 12px;
    min-height: 0;
    /* grid 轨道防内容撑破 */
    min-width: 0;
  }
  .projects-col { padding-right: 24px; }
  .tasks-col { padding-left: 24px; border-left: 1px solid var(--border-strong); }
  .col-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 12px;
    min-height: 35px;
    padding: 3px 0 9px;
    border-bottom: 3px double var(--border);
  }
  .col-title {
    display: flex;
    align-items: center;
    gap: 9px;
    font-family: var(--font-serif);
    font-size: 16px;
    font-weight: 700;
    color: var(--text);
    margin: 0;
  }
  .col-title::before { content: ''; width: 3px; height: 16px; background: var(--accent); }
  .project-sort { display: flex; align-items: center; gap: 5px; flex-shrink: 0; }
  .sort-select { height: 28px; min-width: 104px; padding: 0 24px 0 8px; border: 1px solid var(--border); border-radius: 3px; background: var(--surface-raised); color: var(--text-secondary); font-size: 11px; cursor: pointer; }
  .sort-select:focus { outline: none; border-color: var(--accent); }
  .sort-direction { width: 28px; height: 28px; display: inline-flex; align-items: center; justify-content: center; border: 1px solid var(--border); border-radius: 3px; background: var(--surface-raised); color: var(--text-secondary); }
  .sort-direction:hover { color: var(--accent); border-color: var(--accent); }

  /* 空状态 */
  .empty-state {
    flex: 1;
    display: flex;
    flex-direction: column;
    align-items: center;
    justify-content: center;
    gap: 10px;
    padding: 40px;
    color: var(--text-muted);
    font-size: 14px;
  }

  /* 项目卡片：auto-fill 自动按容器宽度排布列数，避免卡片被拉得过宽 */
  .project-cards {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(min(240px, 100%), 1fr));
    gap: 12px;
    padding-top: 2px;
    overflow-y: auto;
    align-content: start;
  }
  .project-card {
    display: flex;
    flex-direction: column;
    gap: 8px;
    padding: 15px 16px 13px;
    background: var(--surface-raised);
    border: 1px solid var(--border);
    border-radius: 3px;
    cursor: pointer;
    text-align: left;
    transition: box-shadow 0.12s, border-color 0.12s, transform 0.12s;
    min-width: 0;
    overflow: hidden;
  }
  .project-card:hover {
    border-color: var(--accent);
    box-shadow: var(--shadow-soft);
    transform: translateY(-1px);
  }

  .card-top {
    display: flex;
    align-items: center;
    gap: 8px;
  }
  .card-color-dot {
    width: 9px;
    height: 9px;
    border-radius: 50%;
    flex-shrink: 0;
  }
  .card-name {
    font-family: var(--font-serif);
    font-size: 14px;
    font-weight: 700;
    color: var(--text);
    flex: 1;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    overflow-wrap: anywhere;
  }
  .card-desc {
    font-size: 11px;
    color: var(--text-muted);
    margin: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    overflow-wrap: anywhere;
  }
  .card-stats-row {
    display: flex;
    gap: 12px;
    font-size: 11px;
    color: var(--text-muted);
  }
  .card-stat .stat-num { font-weight: 600; color: var(--text); }
  .card-stat .stat-num.done { color: var(--green); }
  .card-stat.due { margin-left: auto; }

  .progress-bar-bg {
    height: 4px;
    background: var(--paper-deep);
    border-radius: 0;
    overflow: hidden;
  }
  .progress-bar-fill {
    height: 100%;
    border-radius: 0;
    transition: width 0.3s;
  }
  .card-percent {
    font-size: 11px;
    color: var(--text-muted);
    text-align: right;
  }

  /* 即将到期列表 */
  .upcoming-list {
    display: flex;
    flex-direction: column;
    gap: 6px;
    overflow-y: auto;
    flex: 1;
  }
  .upcoming-item {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 10px 12px;
    background: var(--surface-raised);
    border: 1px solid var(--border);
    border-left: 3px solid var(--accent);
    border-radius: 2px;
    gap: 8px;
    min-width: 0;
    text-align: left;
  }
  .upcoming-left {
    display: flex;
    align-items: center;
    gap: 8px;
    flex: 1;
    min-width: 0;
  }
  .priority-dot {
    width: 8px;
    height: 8px;
    border-radius: 50%;
    flex-shrink: 0;
  }
  .upcoming-info {
    display: flex;
    flex-direction: column;
    align-items: flex-start;
    min-width: 0;
    text-align: left;
  }
  .upcoming-title {
    font-size: 13px;
    color: var(--text);
    font-weight: 500;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
    overflow-wrap: anywhere;
  }
  .upcoming-project {
    font-size: 11px;
    color: var(--text-muted);
  }
  .upcoming-right {
    display: flex;
    flex-direction: column;
    align-items: flex-end;
    flex-shrink: 0;
    gap: 2px;
  }
  .upcoming-due {
    font-size: 12px;
    color: var(--text-secondary);
    font-weight: 500;
  }
  .upcoming-due.urgent {
    color: var(--red, #ef4444);
    font-weight: 600;
  }
  .upcoming-priority {
    font-size: 10px;
    color: var(--text-muted);
  }

  /* 响应式 */
  @media (max-width: 768px) {
    .dashboard { padding: 14px 12px 20px; gap: 16px; }
    .welcome-banner { min-height: 64px; gap: 12px; }
    .welcome-text { padding-left: 12px; }
    .welcome-title { font-size: 21px; }
    .overview-metrics { grid-template-columns: repeat(4, minmax(0, 1fr)); }
    .overview-metric { min-width: 0; padding: 6px 7px; }
    .dashboard-grid { grid-template-columns: 1fr; }
    .projects-col { padding-right: 0; }
    .tasks-col { padding-left: 0; border-left: 0; }
    .project-cards { grid-template-columns: 1fr; }
  }

  @media (max-width: 480px) {
    .welcome-banner { align-items: stretch; flex-direction: column; }
    .overview-metrics { width: 100%; }
    .col-header { align-items: flex-start; }
  }
</style>
