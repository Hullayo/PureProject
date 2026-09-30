/**
 * 通用「指针拖拽排序」Svelte action
 *
 * 为什么不用 HTML5 拖放（`draggable` + `dragstart/dragover/drop`）？
 * 因为移动端 WebView / Chrome 的**触摸事件不会触发 HTML5 拖放**，
 * 于是同一份代码在桌面能用、在 Android 上毫无反应。
 * 这里统一用 Pointer Events，鼠标与触摸走同一条代码路径。
 *
 * 手势策略（兼顾「页面滚动」不被劫持）：
 * - 鼠标 / 触控笔：按住移动 > 4px 即开始拖拽（整行可拖）
 * - 触摸：默认**必须按住手柄**（`handle`，它在 CSS 里带 `touch-action: none`），
 *   移动 > 8px 视为滚动并取消；长按 `longPressMs` 后进入拖拽
 *
 * 视觉反馈：
 * - 被拖元素降低透明度留在原位（充当“空槽”）
 * - 竖向列表模式下其余兄弟节点用 `translateY` 让位
 *   —— 不插入 / 删除 DOM，避免和 Svelte 的 keyed each 复用打架
 * - 跟随手指的浮层（ghost）是 `position: fixed` 克隆体，只改 `transform`
 *
 * 落点计算：数「指针越过了多少条中位线」，得到的是「移除被拖元素后」的插入下标，
 * 与 `arr.splice(from, 1); arr.splice(index, 0, x)` 完全一致。
 *
 * 约定（使用方提供）：
 * - 容器上加 `use:sortable`
 * - 每个可拖元素加 `data-sortable-item` 与 `data-sortable-id="<唯一 id>"`
 * - 看板这类多列布局：给每一列的可放置区域加 `data-sortable-zone="<列 key>"`
 *
 * @module actions/sortable
 *
 * @example 侧边栏项目排序
 * ```svelte
 * <div class="project-list" use:sortable={{ handle: '.drag-handle', axis: 'y', onDrop: (from, t) => reorderProject(from, t.index) }}>
 *   {#each projects as p (p.id)}
 *     <div data-sortable-item data-sortable-id={p.id}>
 *       …
 *       <span class="drag-handle"></span>
 *     </div>
 *   {/each}
 * </div>
 * ```
 */

/** 拖放目标 */
export interface DropTarget {
  /** 所处 drop 区（`data-sortable-zone` 的值；列表模式为 null） */
  zone: string | null;
  /** 该区内「移除被拖元素后」的插入下标 */
  index: number;
}

/** action 参数 */
export interface SortableParams {
  /** 是否启用（默认 true） */
  disabled?: boolean;
  /** 拖拽手柄选择器（如 `'.drag-handle'`），触摸端默认只在手柄上起拖 */
  handle?: string;
  /** 触摸端是否强制从手柄起拖（默认 true） */
  handleOnly?: boolean;
  /** 可拖元素选择器（默认 `[data-sortable-item]`） */
  itemSelector?: string;
  /** 放置区域选择器（默认 `[data-sortable-zone]`；传 null 禁用分区） */
  zoneSelector?: string | null;
  /** `'x'` / `'y'` = 单轴列表（兄弟节点让位）；`'free'` = 看板自由布局（由使用方画指示线） */
  axis?: 'x' | 'y' | 'free';
  /** 触摸长按进入拖拽的毫秒数（默认 220） */
  longPressMs?: number;
  /** 是否自动滚动最近的滚动容器（默认 true） */
  autoScroll?: boolean;
  /** 拖拽开始；返回 false 可取消（此时不会发生任何排序） */
  onStart?: (from: number) => boolean | void;
  /** 落点变化（看板用来自画指示线） */
  onOver?: (target: DropTarget) => void;
  /** 松手落位 */
  onDrop: (fromIndex: number, target: DropTarget, fromId: string) => void;
  /** 拖拽被取消（滚动、pointercancel 等） */
  onCancel?: () => void;
}

interface ItemRect {
  id: string;
  zone: string | null;
  el: HTMLElement;
  top: number;
  left: number;
  height: number;
  width: number;
  index: number;
}

const ITEM_SELECTOR = '[data-sortable-item]';
const ZONE_SELECTOR = '[data-sortable-zone]';
const MOUSE_THRESHOLD = 4;
const TOUCH_CANCEL_THRESHOLD = 8;
const EDGE = 56;

/** 找出从 node（含自身）往上所有可滚动的祖先（含页面本身） */
function collectScrollers(node: HTMLElement): HTMLElement[] {
  const out: HTMLElement[] = [];
  let el: HTMLElement | null = node;
  while (el) {
    const s = getComputedStyle(el);
    if (/(auto|scroll|overlay)/.test(`${s.overflowY}${s.overflowX}`)) out.push(el);
    el = el.parentElement;
  }
  const se = document.scrollingElement as HTMLElement | null;
  if (se && !out.includes(se)) out.push(se);
  return out;
}

/**
 * 指针拖拽排序 action
 */
export function sortable(node: HTMLElement, params: SortableParams) {
  let p = params;

  // ── 状态 ───────────────────────────────────────────────────────────────────
  let dragEl: HTMLElement | null = null;
  let dragId = '';
  let fromIndex = -1;
  let ghost: HTMLElement | null = null;
  let rects: ItemRect[] = [];
  let scrollers: HTMLElement[] = [];
  let pointerId = -1;
  let startX = 0;
  let startY = 0;
  let lastX = 0;
  let lastY = 0;
  let started = false;
  let pressTimer: number | null = null;
  let rafId = 0;
  let current: DropTarget | null = null;
  let lastScrollTop = 0;
  let lastScrollLeft = 0;
  let itemGap = 0;
  let touchDrag = false;

  // ── 工具 ───────────────────────────────────────────────────────────────────
  function itemSelector(): string {
    return p.itemSelector ?? ITEM_SELECTOR;
  }

  function zoneSelector(): string | null {
    return p.zoneSelector === undefined ? ZONE_SELECTOR : p.zoneSelector;
  }

  function items(): HTMLElement[] {
    return Array.from(node.querySelectorAll<HTMLElement>(itemSelector()));
  }

  function collect(): ItemRect[] {
    return items().map((el, index) => {
      const r = el.getBoundingClientRect();
      const selector = zoneSelector();
      const zoneEl = selector ? el.closest<HTMLElement>(selector) : null;
      return {
        id: el.dataset.sortableId ?? String(index),
        zone: zoneEl?.dataset.sortableZone ?? null,
        el,
        top: r.top,
        left: r.left,
        height: r.height,
        width: r.width,
        index,
      };
    });
  }

  function computeTarget(x: number, y: number): DropTarget {
    const under = document.elementFromPoint(x, y);
    const selector = zoneSelector();
    const zoneEl = selector && under instanceof Element ? under.closest<HTMLElement>(selector) : null;
    const zone = zoneEl?.dataset.sortableZone ?? null;

    let index = 0;
    for (const r of rects) {
      if (r.id === dragId || r.zone !== zone) continue;
      const passedMidpoint = p.axis === 'x'
        ? x > r.left + r.width / 2
        : y > r.top + r.height / 2;
      if (passedMidpoint) index++;
    }
    return { zone, index };
  }

  /** 单轴列表：兄弟节点让位，露出空槽 */
  function paint(t: DropTarget) {
    if (p.axis === 'free') return;
    const dragged = rects[fromIndex];
    if (!dragged) return;
    const distance = (p.axis === 'x' ? dragged.width : dragged.height) + itemGap;
    for (const r of rects) {
      if (r.id === dragId) continue;
      let shift = 0;
      if (r.index > fromIndex && r.index <= t.index) shift = -distance;
      else if (r.index < fromIndex && r.index >= t.index) shift = distance;
      r.el.style.transform = shift
        ? p.axis === 'x' ? `translateX(${shift}px)` : `translateY(${shift}px)`
        : '';
    }
  }

  function clearStyles() {
    for (const r of rects) {
      r.el.style.transform = '';
      r.el.style.opacity = '';
      r.el.classList.remove('sortable-dragging-item');
    }
    if (dragEl) {
      dragEl.style.transform = '';
      dragEl.style.opacity = '';
      dragEl.classList.remove('sortable-dragging-item');
    }
  }

  /**
   * 自动滚动 + 修正缓存的坐标
   *
   * 每次进帧都对齐一次滚动偏移（不管是不是我们自己滚的），
   * 这样用户用滚轮 / 触控板手动滚动时落点依旧正确。
   */
  function autoScrollStep() {
    if (!started) return;
    for (const sc of scrollers) {
      const r = sc === document.scrollingElement
        ? { top: 0, left: 0, right: window.innerWidth, bottom: window.innerHeight }
        : sc.getBoundingClientRect();

      if (sc.scrollWidth > sc.clientWidth + 1) {
        if (lastX < r.left + EDGE) sc.scrollLeft -= Math.ceil((r.left + EDGE - lastX) / 3);
        else if (lastX > r.right - EDGE) sc.scrollLeft += Math.ceil((lastX - (r.right - EDGE)) / 3);
      }
      if (sc.scrollHeight > sc.clientHeight + 1) {
        if (lastY < r.top + EDGE) sc.scrollTop -= Math.ceil((r.top + EDGE - lastY) / 3);
        else if (lastY > r.bottom - EDGE) sc.scrollTop += Math.ceil((lastY - (r.bottom - EDGE)) / 3);
      }
    }

    // 用真实滚动位置差修正缓存 rect（竖列表只需 top；横向用于看板）
    const top = scrollers.reduce((a, sc) => a + sc.scrollTop, 0);
    if (top !== lastScrollTop) {
      const d = top - lastScrollTop;
      for (const r of rects) r.top -= d;
      lastScrollTop = top;
    }
    const left = scrollers.reduce((a, sc) => a + sc.scrollLeft, 0);
    if (left !== lastScrollLeft) {
      const d = left - lastScrollLeft;
      for (const r of rects) r.left -= d;
      lastScrollLeft = left;
    }

    const t = computeTarget(lastX, lastY);
    if (!current || current.index !== t.index || current.zone !== t.zone) {
      current = t;
      paint(t);
      p.onOver?.(t);
    }
  }

  // ── 生命周期 ───────────────────────────────────────────────────────────────
  /** 触摸拖拽期间阻断页面滚动（pointermove 的 preventDefault 在部分 WebView 上不够） */
  function onTouchMoveBlock(e: TouchEvent) {
    if (e.cancelable) e.preventDefault();
  }

  function begin() {
    if (!dragEl || started) return;
    const rect = dragEl.getBoundingClientRect();

    rects = collect();
    fromIndex = rects.findIndex(r => r.id === dragId);
    const nodeStyle = getComputedStyle(node);
    itemGap = Number.parseFloat(p.axis === 'x' ? nodeStyle.columnGap : nodeStyle.rowGap) || 0;
    scrollers = collectScrollers(node);
    lastScrollTop = scrollers.reduce((a, sc) => a + sc.scrollTop, 0);
    lastScrollLeft = scrollers.reduce((a, sc) => a + sc.scrollLeft, 0);

    ghost = dragEl.cloneNode(true) as HTMLElement;
    ghost.classList.add('sortable-ghost');
    ghost.removeAttribute('data-sortable-item');
    Object.assign(ghost.style, {
      position: 'fixed',
      left: `${rect.left}px`,
      top: `${rect.top}px`,
      width: `${rect.width}px`,
      height: `${rect.height}px`,
      margin: '0',
      pointerEvents: 'none',
      zIndex: '4000',
      opacity: '0.92',
      willChange: 'transform',
    });
    document.body.appendChild(ghost);

    dragEl.classList.add('sortable-dragging-item');
    dragEl.style.opacity = '0.28';
    document.body.classList.add('is-sorting');
    try { node.setPointerCapture(pointerId); } catch { /* 不支持时忽略 */ }
    if (touchDrag) window.addEventListener('touchmove', onTouchMoveBlock, { passive: false });
    if ('vibrate' in navigator) navigator.vibrate(12);

    started = true;
    current = computeTarget(lastX, lastY);
    paint(current);
    p.onOver?.(current);
    rafId = requestAnimationFrame(function loop() {
      autoScrollStep();
      rafId = requestAnimationFrame(loop);
    });
  }

  function moveGhost() {
    if (!ghost) return;
    const dx = lastX - startX;
    const dy = lastY - startY;
    ghost.style.transform = `translate3d(${dx}px, ${dy}px, 0) scale(1.02)`;
  }

  /**
   * 拖拽结束后抑制紧接着的那一次 click
   *
   * 否则「拖完松手」会被当成一次点击，把项目/任务顺带选中。
   * 双重限定，避免误伤：
   *  1. 目标在当前容器内；
   *  2. 点击坐标与松手点几乎重合（±16px）—— 拖完立刻点别处不受影响。
   */
  function suppressNextClick() {
    const upX = lastX;
    const upY = lastY;
    const handler = (ev: MouseEvent) => {
      if (!(ev.target instanceof Node) || !node.contains(ev.target)) return;
      if (Math.hypot(ev.clientX - upX, ev.clientY - upY) > 16) return;
      ev.stopPropagation();
      ev.preventDefault();
    };
    window.addEventListener('click', handler, { capture: true, once: true });
    window.setTimeout(() => window.removeEventListener('click', handler, { capture: true }), 300);
  }

  function cleanupGesture() {
    if (pressTimer !== null) { clearTimeout(pressTimer); pressTimer = null; }
    if (rafId) { cancelAnimationFrame(rafId); rafId = 0; }
    ghost?.remove();
    ghost = null;
    clearStyles();
    document.body.classList.remove('is-sorting');
    if (pointerId >= 0) { try { node.releasePointerCapture(pointerId); } catch { /* ignore */ } }
    window.removeEventListener('pointermove', onPointerMove);
    window.removeEventListener('pointerup', onPointerUp);
    window.removeEventListener('pointercancel', onPointerCancel);
    window.removeEventListener('touchmove', onTouchMoveBlock);
    touchDrag = false;
    pointerId = -1;
    started = false;
    dragEl = null;
    dragId = '';
    fromIndex = -1;
    current = null;
    rects = [];
    itemGap = 0;
  }

  // ── 指针事件 ───────────────────────────────────────────────────────────────
  function onPointerDown(e: PointerEvent) {
    if (p.disabled || e.button !== 0) return;
    // 已有活跃手势（多指、或在拖拽中又按下）：直接忽略，避免 ghost / rAF 泄漏
    if (started || dragEl || pressTimer !== null) return;
    const target = e.target as HTMLElement | null;
    const item = target?.closest<HTMLElement>(itemSelector());
    if (!item || !node.contains(item)) return;

    const isTouch = e.pointerType === 'touch';
    const onHandle = p.handle ? !!target?.closest(p.handle) : false;
    // 触摸端默认必须按住手柄；桌面端可显式要求只能从手柄起拖。
    const requiresHandle = !!p.handle && (isTouch ? (p.handleOnly ?? true) : p.handleOnly === true);
    if (requiresHandle && !onHandle) return;

    dragEl = item;
    dragId = item.dataset.sortableId ?? '';
    pointerId = e.pointerId;
    touchDrag = isTouch;
    startX = lastX = e.clientX;
    startY = lastY = e.clientY;

    window.addEventListener('pointermove', onPointerMove, { passive: false });
    window.addEventListener('pointerup', onPointerUp);
    window.addEventListener('pointercancel', onPointerCancel);

    if (isTouch) {
      pressTimer = window.setTimeout(() => {
        pressTimer = null;
        if (p.onStart?.(items().indexOf(item)) === false) { cleanupGesture(); return; }
        begin();
      }, p.longPressMs ?? 220);
    }
  }

  function onPointerMove(e: PointerEvent) {
    if (e.pointerId !== pointerId) return;
    lastX = e.clientX;
    lastY = e.clientY;

    if (!started) {
      const dist = Math.hypot(lastX - startX, lastY - startY);
      if (pressTimer !== null) {
        // 长按计时中：移动过多 → 用户其实想滚动
        if (dist > TOUCH_CANCEL_THRESHOLD) { cleanupGesture(); }
        return;
      }
      if (dist < MOUSE_THRESHOLD) return;
      if (!dragEl) { cleanupGesture(); return; }
      if (p.onStart?.(items().indexOf(dragEl)) === false) { cleanupGesture(); return; }
      begin();
      return;
    }

    if (e.cancelable) e.preventDefault();
    moveGhost();
    const t = computeTarget(lastX, lastY);
    if (!current || current.index !== t.index || current.zone !== t.zone) {
      current = t;
      paint(t);
      p.onOver?.(t);
    }
  }

  function finish(commit: boolean) {
    const didDrag = started;
    const from = fromIndex;
    const target = current;
    const id = dragId;
    cleanupGesture();
    if (didDrag) suppressNextClick();
    if (commit && didDrag && target && from >= 0) p.onDrop(from, target, id);
    else if (didDrag) p.onCancel?.();
  }

  function onPointerUp(e: PointerEvent) {
    if (e.pointerId !== pointerId) return;
    finish(true);
  }

  function onPointerCancel(e: PointerEvent) {
    if (e.pointerId !== pointerId) return;
    finish(false);
  }

  node.addEventListener('pointerdown', onPointerDown);

  return {
    update(next: SortableParams) {
      p = next;
    },
    destroy() {
      cleanupGesture();
      node.removeEventListener('pointerdown', onPointerDown);
    },
  };
}
