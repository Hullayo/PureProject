<script lang="ts">
  import { t } from '$lib/i18n';
  import { showSettings } from '$lib/stores';
  import SettingsAppearance from '../settings/SettingsAppearance.svelte';
  import SettingsColors from '../settings/SettingsColors.svelte';
  import SettingsData from '../settings/SettingsData.svelte';
  import SettingsNotifications from '../settings/SettingsNotifications.svelte';
  import SettingsAI from '../settings/SettingsAI.svelte';
  import SettingsAbout from '../settings/SettingsAbout.svelte';
  import SettingsPlugins from '../settings/SettingsPlugins.svelte';

  type Tab = 'appearance' | 'colors' | 'data' | 'notifications' | 'ai' | 'plugins' | 'about';
  let activeTab = $state<Tab>('appearance');

  const tabs: { id: Tab; key: string }[] = [
    { id: 'appearance', key: 'settings.appearance' },
    { id: 'colors', key: 'settings.colors' },
    { id: 'data', key: 'settings.data' },
    { id: 'notifications', key: 'settings.notifications' },
    { id: 'ai', key: 'settings.ai' },
    { id: 'plugins', key: 'settings.plugins' },
    { id: 'about', key: 'settings.about' },
  ];

  let tabNav = $state<HTMLElement | null>(null);
  let tabIndicatorStyle = $state('');

  function updateTabIndicator() {
    if (!tabNav) return;
    const btns = tabNav.querySelectorAll('.tab-btn') as NodeListOf<HTMLElement>;
    const idx = tabs.findIndex(t => t.id === activeTab);
    const btn = btns[idx];
    if (!btn) return;
    const navRect = tabNav.getBoundingClientRect();
    const btnRect = btn.getBoundingClientRect();
    tabIndicatorStyle = `top:${btnRect.top - navRect.top}px;height:${btnRect.height}px;`;
  }

  $effect(() => {
    void activeTab;
    requestAnimationFrame(updateTabIndicator);
  });
</script>

{#if $showSettings}
  <div class="overlay" onclick={() => showSettings.set(false)} onkeydown={(e) => { if (e.key === 'Escape') showSettings.set(false); }} role="dialog" aria-modal="true" tabindex="-1" aria-label={$t('settings.title')}>
    <div class="panel" onclick={e => e.stopPropagation()}>
      <div class="panel-header">
        <span class="panel-title">{$t('settings.title')}</span>
        <button class="close-btn" aria-label={$t('shortcut.closePanel')} onclick={() => showSettings.set(false)}>&times;</button>
      </div>
      <div class="panel-body">
        <nav class="tab-nav" bind:this={tabNav}>
          <div class="tab-indicator" style={tabIndicatorStyle}></div>
          {#each tabs as tab}
            <button class="tab-btn" class:active={activeTab === tab.id} onclick={() => activeTab = tab.id}>
              {$t(tab.key)}
            </button>
          {/each}
        </nav>

        <div class="tab-content">
          {#key activeTab}
            <div class="tab-content-inner">
              {#if activeTab === 'appearance'}
                <SettingsAppearance />
              {:else if activeTab === 'colors'}
                <SettingsColors />
              {:else if activeTab === 'data'}
                <SettingsData />
              {:else if activeTab === 'notifications'}
                <SettingsNotifications />
              {:else if activeTab === 'ai'}
                <SettingsAI />
              {:else if activeTab === 'plugins'}
                <SettingsPlugins />
              {:else if activeTab === 'about'}
                <SettingsAbout />
              {/if}
            </div>
          {/key}
        </div>
      </div>
    </div>
  </div>
{/if}

<style>
  .overlay { position: fixed; inset: 0; background: rgba(0,0,0,0.5); display: flex; align-items: center; justify-content: center; z-index: 1000; }
  .panel { width: 560px; max-height: 80vh; background: var(--bg); border: 1px solid var(--border); border-radius: 12px; box-shadow: 0 16px 48px rgba(0,0,0,0.3); display: flex; flex-direction: column; overflow: hidden; }
  .panel-header { display: flex; align-items: center; justify-content: space-between; padding: 14px 20px; border-bottom: 1px solid var(--border); flex-shrink: 0; }
  .panel-title { font-size: 15px; font-weight: 600; }
  .close-btn { font-size: 22px; color: var(--text-muted); line-height: 1; }
  .close-btn:hover { color: var(--text); }
  .panel-body { display: flex; flex: 1; overflow: hidden; }

  .tab-nav { display: flex; flex-direction: column; width: 140px; padding: 12px 8px; border-right: 1px solid var(--border); flex-shrink: 0; gap: 2px; position: relative; }
  .tab-indicator { position: absolute; left: 8px; right: 8px; background: var(--accent-light); border-radius: 6px; z-index: 0; pointer-events: none; }
  .tab-btn { padding: 8px 12px; font-size: 12px; border-radius: 6px; color: var(--text-secondary); text-align: left; white-space: nowrap; position: relative; z-index: 1; transition: color 0.15s; }
  .tab-btn:hover { color: var(--text); }
  .tab-btn.active { color: var(--accent); font-weight: 500; }

  .tab-content { flex: 1; overflow-y: auto; padding: 16px 20px; display: flex; flex-direction: column; gap: 20px; }
  .tab-content-inner { display: flex; flex-direction: column; gap: 20px; }

  @media (max-width: 768px) {
    .panel { width: 95vw; max-height: 90vh; }
    .panel-body { flex-direction: column; }
    .tab-nav { flex-direction: row; width: 100%; border-right: none; border-bottom: 1px solid var(--border); padding: 8px; overflow-x: auto; gap: 4px; }
    .tab-nav::-webkit-scrollbar { display: none; }
    .tab-indicator { display: none; }
    .tab-content { padding: 12px; }
  }
</style>
