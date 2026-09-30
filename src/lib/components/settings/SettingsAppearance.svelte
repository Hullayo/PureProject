<script lang="ts">
  import { t, locale } from '$lib/i18n';
  import { isDark, themeMode, setThemeMode, systemPrefersDark, showTutorial, showSettings, vditorTheme } from '$lib/stores';
  import { animationLevel } from '$lib/stores/animation';
  import { safeSetItem } from '$lib/stores/storage-health';
  import SettingsTaskFields from './SettingsTaskFields.svelte';

  let currentLocale = $state<'zh' | 'en'>('zh');

  let customCSS = $state(localStorage.getItem('pm_vditor_css') || '');
  let loadingPreset = $state('');

  function saveCustomCSS() {
    safeSetItem('pm_vditor_css', customCSS, '编辑器自定义 CSS', false);
  }

  async function loadPreset(name: string) {
    loadingPreset = name;
    try {
      const res = await fetch(`/themes/${name}.css`);
      if (!res.ok) throw new Error('Failed to load theme');
      customCSS = await res.text();
      saveCustomCSS();
    } catch (e) {
      console.error('Failed to load theme preset:', e);
    }
    loadingPreset = '';
  }

  // 从 locale store 同步到本地状态
  $effect(() => {
    currentLocale = $locale;
    console.log('[Settings/Lang] Current locale:', $locale);
  });

  function setLocale(l: 'zh' | 'en') {
    console.log('[Settings/Lang] Switching to:', l);
    locale.set(l);
    currentLocale = l;
  }
</script>

<div class="section">
  <span class="section-title">{$t('settings.theme')}</span>
  <div class="btn-row">
    <button class="btn-toggle" class:active={$themeMode === 'system'} onclick={() => setThemeMode('system')}>{$t('settings.themeSystem')}</button>
    <button class="btn-toggle" class:active={$themeMode === 'light'} onclick={() => setThemeMode('light')}>{$t('settings.light')}</button>
    <button class="btn-toggle" class:active={$themeMode === 'dark'} onclick={() => setThemeMode('dark')}>{$t('settings.dark')}</button>
  </div>
  {#if $themeMode === 'system'}
    <p class="hint">
      {$t('settings.themeSystemNow')}：{$systemPrefersDark ? $t('settings.dark') : $t('settings.light')}
    </p>
  {/if}
</div>
<div class="section">
  <span class="section-title">{$t('settings.language')}</span>
  <div class="btn-row">
    <button class="btn-toggle" class:active={currentLocale === 'zh'} onclick={() => setLocale('zh')}>中文</button>
    <button class="btn-toggle" class:active={currentLocale === 'en'} onclick={() => setLocale('en')}>English</button>
  </div>
</div>
<div class="section">
  <span class="section-title">{$t('settings.animation')}</span>
  <div class="btn-row">
    <button class="btn-toggle" class:active={$animationLevel === 'none'} onclick={() => animationLevel.set('none')}>{$t('settings.animNone')}</button>
    <button class="btn-toggle" class:active={$animationLevel === 'moderate'} onclick={() => animationLevel.set('moderate')}>{$t('settings.animModerate')}</button>
    <button class="btn-toggle" class:active={$animationLevel === 'rich'} onclick={() => animationLevel.set('rich')}>{$t('settings.animRich')}</button>
  </div>
</div>
<div class="section">
  <span class="section-title">编辑器主题（README）</span>
  <div class="btn-row">
    <button class="btn-toggle" class:active={$vditorTheme === 'auto'} onclick={() => vditorTheme.set('auto')}>跟随系统</button>
    <button class="btn-toggle" class:active={$vditorTheme === 'light'} onclick={() => vditorTheme.set('light')}>浅色</button>
    <button class="btn-toggle" class:active={$vditorTheme === 'dark'} onclick={() => vditorTheme.set('dark')}>深色</button>
  </div>
  <div class="preset-row">
    <button class="btn-preset" onclick={() => { customCSS = ''; saveCustomCSS(); }} disabled={!customCSS}>清除</button>
    <button class="btn-preset" onclick={() => loadPreset('github')} disabled={loadingPreset !== ''}>
      {loadingPreset === 'github' ? '加载中…' : 'GitHub 风格'}
    </button>
    <button class="btn-preset" onclick={() => loadPreset('ink')} disabled={loadingPreset !== ''}>
      {loadingPreset === 'ink' ? '加载中…' : '学术风格'}
    </button>
  </div>
  <details class="css-details">
    <summary class="css-summary">自定义 CSS（覆盖编辑器样式）</summary>
    <textarea class="css-editor" bind:value={customCSS} oninput={saveCustomCSS} placeholder={'.vditor { background: #1a1a2e !important; }\n.vditor-ir { color: #e0e0e0 !important; }\n.vditor-ir h1 { color: #ff6b6b !important; }'}></textarea>
  </details>
</div>
<div class="section">
  <button class="btn btn-ghost" onclick={() => { showTutorial.set(true); showSettings.set(false); }}>
    {$t('settings.tutorial')}
  </button>
</div>

<SettingsTaskFields />

<style>
  .hint { font-size: 11px; color: var(--text-muted); line-height: 1.5; }
  .section { display: flex; flex-direction: column; gap: 8px; }
  .section-title { font-size: 11px; color: var(--text-muted); text-transform: uppercase; letter-spacing: 0; }
  .btn-row { display: flex; gap: 6px; }
  .btn-toggle { flex: 1; padding: 7px; font-size: 12px; border-radius: 6px; background: var(--surface); color: var(--text-secondary); border: 1px solid var(--border); }
  .btn-toggle.active { background: var(--accent); color: #fff; border-color: var(--accent); }
  .css-details { margin-top: 8px; }
  .css-summary { font-size: 12px; color: var(--accent); cursor: pointer; padding: 4px 0; }
  .preset-row { display: flex; gap: 4px; margin-top: 6px; flex-wrap: wrap; }
  .btn-preset { padding: 5px 10px; font-size: 11px; border-radius: 6px; background: var(--surface); color: var(--text-secondary); border: 1px solid var(--border); cursor: pointer; }
  .btn-preset:hover:not(:disabled) { border-color: var(--accent); color: var(--accent); }
  .btn-preset:disabled { opacity: 0.4; cursor: not-allowed; }
  .css-summary:hover { opacity: 0.8; }
  .css-editor { width: 100%; min-height: 120px; margin-top: 6px; padding: 10px; font-size: 12px; font-family: monospace; border-radius: 6px; background: var(--bg); border: 1px solid var(--border); color: var(--text); resize: vertical; line-height: 1.6; }
  .css-editor:focus { border-color: var(--accent); outline: none; }
  .btn { padding: 8px 14px; font-size: 13px; border-radius: 6px; }
  .btn-ghost { background: var(--surface); color: var(--text-secondary); border: 1px solid var(--border); }
  .btn-ghost:hover { border-color: var(--accent); color: var(--accent); }
</style>
