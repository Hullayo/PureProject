<script lang="ts">
/**
 * LiveTimer — 秒级实时刷新的工时文本
 *
 * 只有「正在计时」的工时才需要实时刷新；把 ticker 收进这个小组件后，
 * 每分钟只有这些行会重渲染，避免整个列表每秒 diff（任务列表 >200 条时尤其重要）。
 *
 * @example
 * <LiveTimer start={task.tracked_start} />
 * <LiveTimer start={task.tracked_start} end={task.updated_at} />
 */

  import { createTicker } from '$lib/utils/live-duration';
  import { formatDurationHMS } from '$lib/utils/date';

  interface Props {
    /** 开始时间（ISO）；为空则渲染空串 */
    start: string | null | undefined;
    /** 结束时间（ISO）；提供后不再实时刷新 */
    end?: string | null;
  }

  let { start, end = null }: Props = $props();

  /** 只有在有开始时间、且没有结束时间时才需要每秒刷新 */
  const ticker = createTicker(() => !!start && !end);

  const text = $derived(start ? formatDurationHMS(start, end ?? undefined, $ticker) : '');
</script>

{text}
