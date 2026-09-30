<script lang="ts">
/**
 * Tutorial — 新手引导教程组件
 *
 * 分步引导用户了解应用的主要功能和操作方式。
 * 功能包括：
 * - 聚光灯高亮目标元素
 * - 智能定位的提示卡片（自动检测溢出并回退位置）
 * - 键盘导航（方向键、Enter、Esc）
 * - 自动跳过不存在的步骤
 * - 完成后标记教程已查看
 *
 * @example
 * <Tutorial bind:show={showTutorial} />
 */

  import Icon from '$lib/components/shared/Icon.svelte';
  import { t } from '$lib/i18n';
  import { animationLevel } from '$lib/stores/animation';
  import { fade, scale } from 'svelte/transition';

  let { show = $bindable(false) }: { show: boolean } = $props();

  interface Step {
    target: string;
    titleKey: string;
    descKey: string;
    position?: 'top' | 'bottom' | 'left' | 'right';
  }

  const steps: Step[] = [
    { target: '.new-project-btn', titleKey: 'tutorial.step1Title', descKey: 'tutorial.step1Desc', position: 'bottom' },
    { target: '.group-panel', titleKey: 'tutorial.step2Title', descKey: 'tutorial.step2Desc', position: 'right' },
    { target: '.header-command', titleKey: 'tutorial.step3Title', descKey: 'tutorial.step3Desc', position: 'bottom' },
    { target: '.task-add', titleKey: 'tutorial.step4Title', descKey: 'tutorial.step4Desc', position: 'bottom' },
    { target: '.kanban-cols', titleKey: 'tutorial.step5Title', descKey: 'tutorial.step5Desc', position: 'top' },
    { target: '.view-toggle', titleKey: 'tutorial.step6Title', descKey: 'tutorial.step6Desc', position: 'bottom' },
    { target: '.detail', titleKey: 'tutorial.step7Title', descKey: 'tutorial.step7Desc', position: 'left' },
    { target: '.sidebar-actions', titleKey: 'tutorial.step8Title', descKey: 'tutorial.step8Desc', position: 'top' },
  ];

  let step = $state(0);
  let highlight = $state({ top: 0, left: 0, width: 0, height: 0, radius: 8 });
  let tooltipPos = $state({ top: 0, left: 0 });
  let tooltipPlacement = $state<'top' | 'bottom' | 'left' | 'right'>('bottom');
  let tooltipCenterY = $state(false);
  let ready = $state(false);
  let tooltipEl = $state<HTMLDivElement | null>(null);

  function getVisibleSteps(): { step: Step; index: number }[] {
    return steps.map((s, i) => ({ step: s, index: i })).filter(({ step: s }) => {
      if (!s.target) return true;
      return !!document.querySelector(s.target);
    });
  }

  let visibleSteps = $derived(getVisibleSteps());
  let currentVisibleIndex = $derived(visibleSteps.findIndex(s => s.index === step));
  let currentStep = $derived(visibleSteps[currentVisibleIndex]);

  function updatePositions() {
    const s = steps[step];
    if (!s.target) {
      highlight = { top: -100, left: -100, width: 0, height: 0, radius: 0 };
      tooltipPlacement = s.position ?? 'bottom';
      tooltipPos = { top: window.innerHeight / 2, left: window.innerWidth / 2 };
      return;
    }

    const el = document.querySelector(s.target);
    if (!el) {
      goNext();
      return;
    }

    const rect = el.getBoundingClientRect();
    const pad = 6;
    highlight = {
      top: rect.top - pad,
      left: rect.left - pad,
      width: rect.width + pad * 2,
      height: rect.height + pad * 2,
      radius: 10
    };

    const gap = 14;
    const tooltipW = tooltipEl?.offsetWidth ?? 320;
    const tooltipH = tooltipEl?.offsetHeight ?? 200;
    const vw = window.innerWidth;
    const vh = window.innerHeight;
    const margin = 12;

    let placement: string = s.position ?? 'bottom';
    let tTop = 0, tLeft = 0;

    const anchorMid = rect.top + rect.height / 2;
    const useTopAnchor = anchorMid < vh / 2;

    function calcPos(p: string) {
      // For left/right: smart anchor — if button is in top half of screen, anchor to top edge; otherwise center
      const anchorTop = rect.top + 4;
      const vRef = useTopAnchor ? anchorTop : anchorMid;
      const vTransform = useTopAnchor ? 0 : tooltipH / 2;

      switch (p) {
        case 'bottom': return { t: rect.bottom + pad + gap, l: rect.left + rect.width / 2 };
        case 'top': return { t: rect.top - pad - gap - tooltipH, l: rect.left + rect.width / 2 };
        case 'right': return { t: vRef - vTransform, l: rect.right + pad + gap };
        case 'left': return { t: vRef - vTransform, l: rect.left - pad - gap - tooltipW };
        default: return { t: rect.bottom + pad + gap, l: rect.left + rect.width / 2 };
      }
    }

    function overflows(p: string, pos: { t: number; l: number }) {
      switch (p) {
        case 'bottom': return pos.t + tooltipH > vh - margin;
        case 'top': return pos.t < margin;
        case 'right': return pos.l + tooltipW > vw - margin;
        case 'left': return pos.l < margin;
        default: return false;
      }
    }

    const fallbacks: Record<string, string[]> = {
      bottom: ['top', 'right', 'left'],
      top: ['bottom', 'right', 'left'],
      right: ['left', 'bottom', 'top'],
      left: ['right', 'bottom', 'top'],
    };

    let pos = calcPos(placement);
    if (overflows(placement, pos)) {
      for (const fb of fallbacks[placement] ?? []) {
        const fbPos = calcPos(fb);
        if (!overflows(fb, fbPos)) {
          placement = fb;
          pos = fbPos;
          break;
        }
      }
    }

    tooltipPlacement = placement as typeof tooltipPlacement;
    tooltipCenterY = (placement === 'left' || placement === 'right') && !useTopAnchor;

    // Clamp to viewport
    if (placement === 'bottom' || placement === 'top') {
      pos.l = Math.max(margin + tooltipW / 2, Math.min(vw - margin - tooltipW / 2, pos.l));
    }
    if (placement === 'right' || placement === 'left') {
      pos.t = Math.max(margin, Math.min(vh - margin - tooltipH, pos.t));
    }
    if (placement === 'bottom') {
      pos.t = Math.max(margin, Math.min(vh - margin - tooltipH, pos.t));
    }
    if (placement === 'top') {
      pos.t = Math.max(margin, pos.t);
    }

    tooltipPos = { top: pos.t, left: pos.l };
  }

  function goNext() {
    if (currentVisibleIndex < visibleSteps.length - 1) {
      step = visibleSteps[currentVisibleIndex + 1].index;
      requestAnimationFrame(updatePositions);
    } else {
      close();
    }
  }

  function goPrev() {
    if (currentVisibleIndex > 0) {
      step = visibleSteps[currentVisibleIndex - 1].index;
      requestAnimationFrame(updatePositions);
    }
  }

  function close() {
    show = false;
    step = 0;
    try { localStorage.setItem('pm_tutorial_seen', '1'); } catch {}
  }

  function handleWheel(e: WheelEvent) { e.preventDefault(); }

  function handleKeydown(e: KeyboardEvent) {
    if (!show) return;
    if (e.key === 'Escape') { close(); return; }
    if (e.key === 'ArrowRight' || e.key === 'Enter' || e.key === ' ') { e.preventDefault(); goNext(); return; }
    if (e.key === 'ArrowLeft') { e.preventDefault(); goPrev(); return; }
  }

  // Re-measure when step changes and tooltip is rendered
  $effect(() => {
    // Access step and tooltipEl to trigger on change
    void step;
    void tooltipEl;
    if (!show || !ready) return;
    // Double rAF: first for layout, second for measurement
    requestAnimationFrame(() => requestAnimationFrame(updatePositions));
  });

  $effect(() => {
    if (show) {
      step = 0;
      ready = false;
      setTimeout(() => {
        const vs = getVisibleSteps();
        if (vs.length > 0) {
          step = vs[0].index;
        }
        ready = true;
        requestAnimationFrame(() => requestAnimationFrame(updatePositions));
      }, 100);
    }
  });

  $effect(() => {
    if (!show) return;
    const onResize = () => updatePositions();
    window.addEventListener('resize', onResize);
    return () => window.removeEventListener('resize', onResize);
  });
</script>

<svelte:window onkeydown={handleKeydown} />

{#if show && ready && currentStep}
  <!-- svelte-ignore a11y_no_static_element_interactions -->
  <div class="tutorial-overlay" onwheel={handleWheel} transition:fade={{ duration: $animationLevel === 'rich' ? 300 : 200 }}>
    <!-- Spotlight highlight -->
    {#if steps[step].target}
      <div
        class="spotlight"
        style="
          top:{highlight.top}px;
          left:{highlight.left}px;
          width:{highlight.width}px;
          height:{highlight.height}px;
          border-radius:{highlight.radius}px;
          box-shadow: 0 0 0 9999px rgba(0,0,0,0.55);
        "
      ></div>
    {:else}
      <div class="spotlight-full"></div>
    {/if}

    <!-- Tooltip card -->
    <div
      bind:this={tooltipEl}
      class="tooltip"
      class:centered={!steps[step].target}
      style="
        {steps[step].target ? `top:${tooltipPos.top}px;left:${tooltipPos.left}px;` : ''}
        transform: {tooltipPlacement === 'bottom' || tooltipPlacement === 'top' ? 'translateX(-50%)' : ''}
                   {tooltipCenterY && (tooltipPlacement === 'left' || tooltipPlacement === 'right') ? 'translateY(-50%)' : ''}
                   {!steps[step].target ? 'translate(-50%, -50%)' : ''}
      "
    >
      <div class="tooltip-header">
        <span class="tooltip-step">{currentVisibleIndex + 1}/{visibleSteps.length}</span>
        <button class="tooltip-close" onclick={close}><Icon name="close" size={14} /></button>
      </div>
      <h3 class="tooltip-title">{$t(currentStep.step.titleKey)}</h3>
      <p class="tooltip-desc">{$t(currentStep.step.descKey)}</p>
      <div class="tooltip-footer">
        <div class="dots">
          {#each visibleSteps as _, i}
            <span class="dot" class:active={i === currentVisibleIndex}></span>
          {/each}
        </div>
        <div class="tooltip-actions">
          {#if currentVisibleIndex > 0}
            <button class="btn-secondary" onclick={goPrev}>{$t('tutorial.prev')}</button>
          {/if}
          <button class="btn-primary" onclick={goNext}>
            {currentVisibleIndex === visibleSteps.length - 1 ? $t('tutorial.start') : $t('tutorial.next')}
          </button>
        </div>
      </div>
    </div>
  </div>
{/if}

<style>
  .tutorial-overlay {
    position: fixed;
    inset: 0;
    z-index: 999;
    pointer-events: auto;
  }
  .spotlight {
    position: fixed;
    pointer-events: none;
    transition: all 0.35s cubic-bezier(0.4, 0, 0.2, 1);
    z-index: 1000;
  }
  .spotlight-full {
    position: fixed;
    inset: 0;
    background: rgba(0, 0, 0, 0.55);
    z-index: 1000;
  }
  .tooltip {
    position: fixed;
    width: 320px;
    background: var(--bg);
    border: 1px solid var(--border);
    border-radius: 12px;
    box-shadow: 0 8px 32px rgba(0, 0, 0, 0.3);
    z-index: 1001;
    animation: fadeIn 0.25s ease;
  }
  .tooltip.centered {
    position: fixed;
    top: 50%;
    left: 50%;
    transform: translate(-50%, -50%) !important;
  }
  .tooltip-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 12px 16px 0;
  }
  .tooltip-step {
    font-size: 11px;
    color: var(--text-muted);
    padding: 2px 8px;
    background: var(--surface);
    border-radius: 999px;
  }
  .tooltip-close {
    color: var(--text-muted);
    font-size: 14px;
  }
  .tooltip-close:hover {
    color: var(--text);
  }
  .tooltip-title {
    font-size: 15px;
    font-weight: 600;
    padding: 8px 16px 0;
    color: var(--text);
  }
  .tooltip-desc {
    font-size: 13px;
    color: var(--text-secondary);
    padding: 6px 16px 0;
    line-height: 1.6;
  }
  .tooltip-footer {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 12px 16px;
  }
  .dots {
    display: flex;
    gap: 5px;
  }
  .dot {
    width: 6px;
    height: 6px;
    border-radius: 50%;
    background: var(--border);
    transition: all 0.2s;
  }
  .dot.active {
    background: var(--accent);
    width: 16px;
    border-radius: 3px;
  }
  .tooltip-actions {
    display: flex;
    gap: 6px;
  }
  .btn-secondary {
    padding: 6px 14px;
    font-size: 12px;
    border-radius: 6px;
    background: var(--surface);
    color: var(--text-secondary);
    border: 1px solid var(--border);
  }
  .btn-secondary:hover {
    border-color: var(--accent);
    color: var(--accent);
  }
  .btn-primary {
    padding: 6px 14px;
    font-size: 12px;
    border-radius: 6px;
    background: var(--accent);
    color: #fff;
  }
  .btn-primary:hover {
    opacity: 0.9;
  }
  @keyframes fadeIn {
    from { opacity: 0; transform: translateY(4px); }
    to { opacity: 1; transform: translateY(0); }
  }
</style>
