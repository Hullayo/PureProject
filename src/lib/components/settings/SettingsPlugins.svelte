<script lang="ts">
/**
 * SettingsPlugins — 「设置 → 插件」页签
 *
 * - 安装：粘贴 manifest(JSON) + 源码，或从本地文件夹选 `.js` 填入源码
 * - 管理：启用 / 禁用 / 卸载
 * - 排错：查看最近 50 条插件错误
 * - 预览：渲染插件通过 `ctx.registerView()` 注册的 HTML 视图
 *
 * @example
 * <SettingsPlugins />
 */

  import { t } from '$lib/i18n';
  import {
    clearPluginErrors,
    installPlugin,
    installPluginFromJson,
    pluginErrors,
    pluginViews,
    plugins,
    setPluginEnabledAndReload,
    uninstallPluginById,
  } from '$lib/plugins';
  import { EXAMPLE_PLUGIN } from '$lib/plugins/builtin/example-plugin';
  import { pickFile } from '$lib/utils/pick-file';

  let manifestText = $state('');
  let codeText = $state('');
  let msg = $state('');
  let msgOk = $state(true);

  const installed = $derived(Object.values($plugins));

  function flash(text: string, ok: boolean) {
    msg = text;
    msgOk = ok;
    setTimeout(() => { if (msg === text) msg = ''; }, 6000);
  }

  function handleInstall() {
    const res = installPluginFromJson(manifestText, codeText);
    if (res.ok) {
      flash(`${$t('settings.plugins.installOk')}：${res.manifest.name} v${res.manifest.version}`, true);
      manifestText = '';
      codeText = '';
    } else {
      flash(`${$t('settings.plugins.installFailed')}：${res.error}`, false);
    }
  }

  async function pickCode() {
    const file = await pickFile('.js,text/javascript');
    if (!file) return;
    codeText = await file.text();
  }

  function installExample() {
    installPlugin({ ...EXAMPLE_PLUGIN, manifest: { ...EXAMPLE_PLUGIN.manifest, enabled: true } });
    // 触发重新加载
    setPluginEnabledAndReload(EXAMPLE_PLUGIN.manifest.id, true);
    flash(`${$t('settings.plugins.installOk')}：${EXAMPLE_PLUGIN.manifest.name}`, true);
  }

  /** 渲染插件视图（插件代码可能出错，这里兜底） */
  function renderView(render: () => string): string {
    try {
      return render();
    } catch (e) {
      return `<div style="padding:12px;color:var(--red)">渲染失败：${e instanceof Error ? e.message : String(e)}</div>`;
    }
  }
</script>

<div class="section">
  <span class="section-title">{$t('settings.plugins.installed')}</span>
  {#if installed.length === 0}
    <p class="hint">{$t('settings.plugins.none')}</p>
  {:else}
    {#each installed as p (p.manifest.id)}
      <div class="plugin-item">
        <div class="plugin-info">
          <span class="plugin-name">{p.manifest.name} <em>v{p.manifest.version}</em></span>
          <span class="plugin-id">{p.manifest.id} · {p.manifest.capabilities.join(', ')}</span>
          {#if p.manifest.description}<span class="plugin-desc">{p.manifest.description}</span>{/if}
        </div>
        <div class="plugin-actions">
          <button class="btn-ghost small" onclick={() => setPluginEnabledAndReload(p.manifest.id, !p.manifest.enabled)}>
            {p.manifest.enabled ? $t('settings.plugins.disable') : $t('settings.plugins.enable')}
          </button>
          <button class="btn-ghost small danger" onclick={() => uninstallPluginById(p.manifest.id)}>
            {$t('settings.plugins.uninstall')}
          </button>
        </div>
      </div>
    {/each}
  {/if}
</div>

<div class="section">
  <span class="section-title">{$t('settings.plugins.installTitle')}</span>
  <label class="label" for="plugin-manifest">{$t('settings.plugins.manifestLabel')}</label>
  <textarea id="plugin-manifest" class="code" bind:value={manifestText} placeholder={'{\n  "id": "com.example.hello",\n  "name": "Hello",\n  "version": "0.1.0",\n  "main": "inline",\n  "capabilities": ["commands"]\n}'}></textarea>
  <label class="label" for="plugin-code">{$t('settings.plugins.codeLabel')}</label>
  <textarea id="plugin-code" class="code" bind:value={codeText} placeholder={"ctx.registerCommand({ id: 'hi', title: 'Hello', run: () => ctx.log('hi') });"}></textarea>
  <div class="btn-row">
    <button class="btn-ghost" onclick={pickCode}>{$t('settings.plugins.pickFile')}</button>
    <button class="btn-ghost" onclick={installExample}>{$t('settings.plugins.installExample')}</button>
    <button class="btn-primary" onclick={handleInstall} disabled={!manifestText.trim() || !codeText.trim()}>
      {$t('settings.plugins.install')}
    </button>
  </div>
  {#if msg}<p class="hint" class:ok={msgOk} class:err={!msgOk}>{msg}</p>{/if}
  <p class="hint security">⚠ {$t('settings.plugins.securityHint')}</p>
</div>

{#if $pluginViews.length > 0}
  <div class="section">
    <span class="section-title">{$t('settings.plugins.views')}</span>
    {#each $pluginViews as v (v.id)}
      <div class="plugin-view">
        <div class="plugin-view-title">{v.title}</div>
        <!-- svelte-ignore a11y_no_static_element_interactions -->
        <div class="plugin-view-body">{@html renderView(v.render)}</div>
      </div>
    {/each}
  </div>
{/if}

<div class="section">
  <div class="ext-head">
    <span class="section-title">{$t('settings.plugins.errors')} <span class="ext-count">{$pluginErrors.length}</span></span>
    {#if $pluginErrors.length > 0}
      <button class="btn-ghost small" onclick={clearPluginErrors}>{$t('settings.plugins.clearErrors')}</button>
    {/if}
  </div>
  {#if $pluginErrors.length === 0}
    <p class="hint">{$t('settings.plugins.noErrors')}</p>
  {:else}
    {#each $pluginErrors as e, i (e.at + i)}
      <div class="err-item">
        <span class="err-plugin">{e.pluginId}</span>
        <span class="err-msg">{e.message}</span>
        <span class="err-at">{e.at}</span>
      </div>
    {/each}
  {/if}
</div>

<style>
  .section { display: flex; flex-direction: column; gap: 8px; }
  .section-title { font-size: 11px; color: var(--text-muted); text-transform: uppercase; letter-spacing: 0; }
  .hint { font-size: 11px; color: var(--text-muted); line-height: 1.5; }
  .hint.ok { color: var(--green, #10b981); }
  .hint.err { color: var(--red, #ef4444); }
  .hint.security { color: var(--yellow, #f59e0b); }
  .label { font-size: 11px; color: var(--text-muted); }
  .plugin-item { display: flex; align-items: center; gap: 8px; padding: 8px; border: 1px solid var(--border); border-radius: 8px; background: var(--surface); }
  .plugin-info { flex: 1; min-width: 0; display: flex; flex-direction: column; gap: 2px; }
  .plugin-name { font-size: 12px; font-weight: 600; color: var(--text); }
  .plugin-name em { font-style: normal; color: var(--text-muted); font-weight: 400; }
  .plugin-id { font-size: 10px; color: var(--text-muted); }
  .plugin-desc { font-size: 10px; color: var(--text-muted); }
  .plugin-actions { display: flex; gap: 4px; flex-shrink: 0; }
  .btn-row { display: flex; gap: 6px; flex-wrap: wrap; }
  .btn-ghost { padding: 6px 10px; font-size: 12px; border-radius: 6px; background: var(--surface); color: var(--text-secondary); border: 1px solid var(--border); cursor: pointer; }
  .btn-ghost:hover { border-color: var(--accent); color: var(--accent); }
  .btn-ghost.small { padding: 4px 8px; font-size: 11px; }
  .btn-ghost.danger:hover { border-color: var(--red); color: var(--red); }
  .btn-primary { padding: 6px 12px; font-size: 12px; border-radius: 6px; background: var(--accent); color: #fff; border: 1px solid var(--accent); cursor: pointer; }
  .btn-primary:disabled { opacity: 0.4; cursor: not-allowed; }
  .code { width: 100%; min-height: 90px; padding: 8px 10px; font-size: 11px; font-family: monospace; border-radius: 6px; background: var(--bg); border: 1px solid var(--border); color: var(--text); resize: vertical; line-height: 1.5; }
  .code:focus { border-color: var(--accent); outline: none; }
  .ext-head { display: flex; align-items: center; justify-content: space-between; gap: 8px; }
  .ext-count { font-size: 10px; color: var(--text-muted); background: var(--surface); padding: 1px 6px; border-radius: 999px; }
  .plugin-view { border: 1px solid var(--border); border-radius: 8px; overflow: hidden; }
  .plugin-view-title { font-size: 11px; font-weight: 600; color: var(--text-secondary); padding: 6px 10px; background: var(--surface); border-bottom: 1px solid var(--border); }
  .plugin-view-body { padding: 4px; }
  .err-item { display: flex; flex-direction: column; gap: 2px; padding: 6px 8px; border-left: 2px solid var(--red, #ef4444); background: var(--surface); border-radius: 4px; }
  .err-plugin { font-size: 10px; color: var(--accent); }
  .err-msg { font-size: 11px; color: var(--text-secondary); word-break: break-word; }
  .err-at { font-size: 10px; color: var(--text-muted); }
</style>
