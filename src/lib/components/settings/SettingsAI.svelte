<script lang="ts">
  import { t } from '$lib/i18n';
  import { getPrompt, setPrompt, resetPrompt, getDefaultPrompt } from '$lib/ai/prompts';

  function safeGet(key: string, fallback: string): string { try { return localStorage.getItem(key) ?? fallback; } catch { return fallback; } }
  function safeSet(key: string, val: string): void { try { localStorage.setItem(key, val); } catch {} }

  interface ModelDef { id: string; label: string; }
  interface ProviderDef { id: string; label: string; baseURL: string; models: ModelDef[]; }

  const PROVIDERS: ProviderDef[] = [
    {
      id: 'deepseek', label: 'DeepSeek', baseURL: 'https://api.deepseek.com',
      models: [
        { id: 'deepseek-v4-flash', label: 'Flash (v4)' },
        { id: 'deepseek-v4-pro', label: 'Pro (v4)' },
        { id: 'deepseek-chat', label: 'DeepSeek Chat' },
        { id: 'deepseek-reasoner', label: 'DeepSeek Reasoner' },
      ],
    },
    {
      id: 'openai', label: 'OpenAI', baseURL: 'https://api.openai.com',
      models: [
        { id: 'gpt-4o', label: 'GPT-4o' },
        { id: 'gpt-4o-mini', label: 'GPT-4o-mini' },
        { id: 'gpt-4-turbo', label: 'GPT-4-turbo' },
        { id: 'gpt-4', label: 'GPT-4' },
        { id: 'gpt-3.5-turbo', label: 'GPT-3.5-turbo' },
      ],
    },
    {
      id: 'anthropic', label: 'Anthropic', baseURL: 'https://api.anthropic.com',
      models: [
        { id: 'claude-sonnet-4-20250514', label: 'Claude Sonnet 4' },
        { id: 'claude-3.5-sonnet-20241022', label: 'Claude 3.5 Sonnet' },
        { id: 'claude-3.5-haiku-20241022', label: 'Claude 3.5 Haiku' },
        { id: 'claude-opus-4-20250514', label: 'Claude Opus 4' },
      ],
    },
    {
      id: 'google', label: 'Google Gemini', baseURL: 'https://generativelanguage.googleapis.com',
      models: [
        { id: 'gemini-2.5-pro-exp-03-25', label: 'Gemini 2.5 Pro' },
        { id: 'gemini-2.0-flash', label: 'Gemini 2.0 Flash' },
        { id: 'gemini-1.5-pro', label: 'Gemini 1.5 Pro' },
        { id: 'gemini-1.5-flash', label: 'Gemini 1.5 Flash' },
      ],
    },
  ];

  const ALL_PRESET_MODELS = new Set(PROVIDERS.flatMap(p => p.models.map(m => m.id)));

  let aiBase = $state(safeGet('pm_ai_base', 'https://api.deepseek.com'));
  let aiKey = $state(safeGet('pm_ai_key', ''));
  let aiModel = $state(safeGet('pm_ai_model', 'deepseek-v4-flash'));
  let enableThinking = $state(safeGet('pm_ai_thinking', 'false') === 'true');

  function detectProvider(url: string, model: string): string {
    for (const p of PROVIDERS) {
      if (url.toLowerCase().includes(p.baseURL.replace(/https?:\/\//, '').split('.')[0])) return p.id;
    }
    for (const p of PROVIDERS) {
      if (p.models.some(m => m.id === model)) return p.id;
    }
    return 'custom';
  }

  let selectedProvider = $state(detectProvider(aiBase, aiModel));
  let customURL = $state('');
  let customModel = $state('');

  let currentProvider = $derived(PROVIDERS.find(p => p.id === selectedProvider));
  let isCustomProvider = $derived(selectedProvider === 'custom');

  function selectProvider(providerId: string) {
    selectedProvider = providerId;
    const p = PROVIDERS.find(pr => pr.id === providerId);
    if (p) {
      aiBase = p.baseURL;
      if (!p.models.some(m => m.id === aiModel)) {
        aiModel = p.models[0].id;
      }
      customURL = '';
      customModel = '';
    } else {
      aiBase = customURL || aiBase;
      aiModel = customModel || aiModel;
    }
    saveAiSettings();
  }

  function selectModel(modelId: string) {
    aiModel = modelId;
    customModel = '';
    saveAiSettings();
  }

  function applyCustomModel() {
    if (customModel.trim()) {
      aiModel = customModel.trim();
      saveAiSettings();
    }
  }

  function saveAiSettings() {
    safeSet('pm_ai_base', aiBase);
    safeSet('pm_ai_key', aiKey);
    safeSet('pm_ai_model', aiModel);
    safeSet('pm_ai_thinking', String(enableThinking));
  }

  function saveApiKey() {
    saveAiSettings();
  }

  let aiTestLoading = $state(false);
  let aiTestMsg = $state('');
  let aiTestOk = $state(true);

  async function handleAiTest() {
    aiTestLoading = true; aiTestMsg = '';
    try {
      const { aiChat } = await import('$lib/ai');
      await aiChat('回复OK', '测试连接');
      aiTestOk = true;
      aiTestMsg = $t('sync.connectionOk');
    } catch (e: any) { aiTestOk = false; aiTestMsg = e?.message ?? String(e); }
    aiTestLoading = false;
  }

  let expandedPrompt = $state<'polish' | 'report' | 'breakdown' | null>(null);
  let promptTexts = $state<Record<string, string>>({
    polish: getPrompt('polish'),
    report: getPrompt('report'),
    breakdown: getPrompt('breakdown'),
  });

  function togglePrompt(type: 'polish' | 'report' | 'breakdown') {
    if (expandedPrompt === type) { expandedPrompt = null; return; }
    promptTexts[type] = getPrompt(type);
    expandedPrompt = type;
  }

  function savePrompt(type: 'polish' | 'report' | 'breakdown') {
    setPrompt(type, promptTexts[type]);
  }

  function handleResetPrompt(type: 'polish' | 'report' | 'breakdown') {
    resetPrompt(type);
    promptTexts[type] = getDefaultPrompt(type);
  }
</script>

<div class="section">
  <span class="section-title">服务商 / Provider</span>
  <div class="provider-row">
    {#each PROVIDERS as p}
      <button class="btn-provider" class:active={selectedProvider === p.id} onclick={() => selectProvider(p.id)}>
        {p.label}
      </button>
    {/each}
    <button class="btn-provider" class:active={selectedProvider === 'custom'} onclick={() => selectProvider('custom')}>
      自定义
    </button>
  </div>
</div>

<div class="section">
  <span class="section-title">API 地址</span>
  {#if isCustomProvider}
    <input class="input" placeholder="https://api.example.com/v1" bind:value={customURL} oninput={() => { aiBase = customURL; saveAiSettings(); }} />
  {:else}
    <div class="url-display">
      <code class="url-code">{aiBase}</code>
      <span class="url-badge">自动</span>
    </div>
  {/if}
</div>

<div class="section">
  <span class="section-title">{$t('settings.aiKey')}</span>
  <input class="input" type="password" placeholder="sk-xxx" bind:value={aiKey} oninput={saveApiKey} />
</div>

<div class="section">
  <span class="section-title">{$t('settings.aiModel')}</span>

  {#if isCustomProvider}
    <div class="custom-row">
      <input class="input" placeholder="输入完整模型名..." bind:value={customModel} />
      <button class="btn btn-ghost small" onclick={applyCustomModel} disabled={!customModel.trim()}>应用</button>
    </div>
  {:else if currentProvider}
    <select class="model-select" onchange={(e) => selectModel((e.target as HTMLSelectElement).value)}>
      {#each currentProvider.models as m}
        <option value={m.id} selected={aiModel === m.id}>{m.label}</option>
      {/each}
    </select>
  {/if}

  <div class="current-model-hint">当前模型: {aiModel}</div>

  <label class="toggle-row" style="margin-top:6px">
    <span class="toggle-label">
      思考模式
      {#if selectedProvider === 'deepseek'}
        <span class="hint">(deepseek-reasoner / deepseek-v4-pro 支持)</span>
      {:else if selectedProvider === 'openai'}
        <span class="hint">(o1 / o3 系列支持)</span>
      {:else}
        <span class="hint">(部分模型支持)</span>
      {/if}
    </span>
    <button class="toggle-switch" class:on={enableThinking} onclick={() => { enableThinking = !enableThinking; saveAiSettings(); }}>
      <span class="toggle-knob"></span>
    </button>
  </label>
</div>

{#if aiTestMsg}
  <p class="status-msg" class:ok={aiTestOk} class:err={!aiTestOk}>{aiTestMsg}</p>
{/if}
<div class="btn-row">
  <button class="btn btn-ghost" onclick={handleAiTest} disabled={aiTestLoading}>{$t('settings.aiTest')}</button>
</div>

<div class="section" style="margin-top:8px">
  <span class="section-title">{$t('settings.aiPrompts')}</span>
  {#each (['polish', 'report', 'breakdown'] as const) as pType}
    <button class="prompt-toggle" onclick={() => togglePrompt(pType)}>
      <span>{$t(`settings.prompt.${pType}`)}</span>
      <span class="prompt-arrow">{expandedPrompt === pType ? '▾' : '▸'}</span>
    </button>
    {#if expandedPrompt === pType}
      <div class="prompt-editor">
        <textarea class="prompt-textarea" bind:value={promptTexts[pType]} rows="12"></textarea>
        <div class="prompt-actions">
          <button class="btn btn-ghost" onclick={() => handleResetPrompt(pType)}>{$t('settings.promptReset')}</button>
          <button class="btn btn-primary" onclick={() => savePrompt(pType)}>{$t('settings.promptSave')}</button>
        </div>
      </div>
    {/if}
  {/each}
</div>

<style>
  .section { display: flex; flex-direction: column; gap: 8px; }
  .section-title { font-size: 11px; color: var(--text-muted); text-transform: uppercase; letter-spacing: 0; }
  .provider-row { display: flex; flex-wrap: wrap; gap: 4px; }
  .btn-provider { padding: 7px 14px; font-size: 13px; border-radius: 6px; background: var(--surface); color: var(--text-secondary); border: 1px solid var(--border); cursor: pointer; transition: all 0.15s; }
  .btn-provider:hover { border-color: var(--accent); color: var(--text); }
  .btn-provider.active { background: var(--accent); color: #fff; border-color: var(--accent); font-weight: 500; }
  .url-display { display: flex; align-items: center; gap: 8px; padding: 8px 10px; border-radius: 6px; background: var(--surface); border: 1px solid var(--border); }
  .url-code { font-size: 12px; color: var(--text-secondary); font-family: monospace; flex: 1; }
  .url-badge { font-size: 10px; padding: 2px 6px; border-radius: 4px; background: rgba(16,185,129,0.15); color: var(--green); white-space: nowrap; }
  .input { width: 100%; padding: 8px 10px; font-size: 13px; border-radius: 6px; background: var(--surface); border: 1px solid var(--border); color: var(--text); }
  .input:focus { border-color: var(--accent); outline: none; }
  .model-select { width: 100%; padding: 8px 10px; font-size: 13px; border-radius: 6px; background: var(--surface); border: 1px solid var(--border); color: var(--text); cursor: pointer; }
  .model-select:focus { border-color: var(--accent); outline: none; }
  .model-select option { font-size: 13px; color: var(--text); background: var(--surface); padding: 6px 10px; }
  .current-model-hint { font-size: 11px; color: var(--text-muted); padding: 2px 4px; }
  .btn-row { display: flex; gap: 6px; }
  .btn { padding: 8px 14px; font-size: 13px; border-radius: 6px; }
  .btn-primary { background: var(--accent); color: #fff; }
  .btn-primary:hover { opacity: 0.9; }
  .btn-ghost { background: var(--surface); color: var(--text-secondary); border: 1px solid var(--border); }
  .btn-ghost:hover { border-color: var(--accent); color: var(--accent); }
  .btn.small { padding: 6px 12px; font-size: 12px; white-space: nowrap; }
  .custom-row { display: flex; gap: 6px; align-items: center; }
  .custom-row .input { flex: 1; }
  .toggle-row { display: flex; align-items: center; justify-content: space-between; padding: 6px 0; }
  .toggle-label { font-size: 13px; color: var(--text); }
  .toggle-label .hint { font-size: 11px; color: var(--text-muted); font-weight: normal; }
  .toggle-switch { width: 40px; height: 22px; border-radius: 11px; background: var(--border); border: none; cursor: pointer; position: relative; transition: background 0.2s; flex-shrink: 0; }
  .toggle-switch.on { background: var(--accent); }
  .toggle-knob { width: 18px; height: 18px; border-radius: 50%; background: #fff; position: absolute; top: 2px; left: 2px; transition: transform 0.2s; }
  .toggle-switch.on .toggle-knob { transform: translateX(18px); }
  .status-msg { font-size: 12px; padding: 6px 10px; border-radius: 6px; }
  .status-msg.ok { color: var(--green); background: rgba(16,185,129,0.1); }
  .status-msg.err { color: var(--red); background: rgba(239,68,68,0.1); }
  .prompt-toggle { display: flex; align-items: center; justify-content: space-between; width: 100%; padding: 8px 10px; font-size: 13px; border-radius: 6px; background: var(--surface); color: var(--text); border: 1px solid var(--border); margin-top: 4px; }
  .prompt-toggle:hover { border-color: var(--accent); }
  .prompt-arrow { font-size: 11px; color: var(--text-muted); }
  .prompt-editor { margin-top: 6px; display: flex; flex-direction: column; gap: 8px; }
  .prompt-textarea { width: 100%; padding: 10px; font-size: 12px; font-family: monospace; border-radius: 6px; background: var(--bg); border: 1px solid var(--border); color: var(--text); resize: vertical; line-height: 1.6; }
  .prompt-textarea:focus { border-color: var(--accent); outline: none; }
  .prompt-actions { display: flex; gap: 6px; justify-content: flex-end; }
</style>
