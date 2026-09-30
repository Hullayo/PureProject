<script lang="ts">
  import { t } from '$lib/i18n';

  function safeGet(key: string, fallback: string): string { try { return localStorage.getItem(key) ?? fallback; } catch { return fallback; } }
  function safeSet(key: string, val: string): void { try { localStorage.setItem(key, val); } catch {} }

  let remindersEnabled = $state(safeGet('pm_reminders_enabled', 'true') !== 'false');
  let reminderInterval = $state(parseInt(safeGet('pm_reminder_interval', '30')));
  let dueReminderAdvanceMinutes = $state(parseInt(safeGet('pm_due_reminder_advance_minutes', '1440')));

  function saveNotificationSettings() {
    safeSet('pm_reminders_enabled', String(remindersEnabled));
    safeSet('pm_reminder_interval', String(reminderInterval));
    safeSet('pm_due_reminder_advance_minutes', String(dueReminderAdvanceMinutes));
  }
</script>

<div class="section">
  <label class="toggle-row">
    <span class="toggle-label">{$t('settings.remindersEnabled')}</span>
    <button class="toggle-switch" class:on={remindersEnabled} onclick={() => { remindersEnabled = !remindersEnabled; saveNotificationSettings(); }}>
      <span class="toggle-knob"></span>
    </button>
  </label>
</div>
<div class="section">
  <span class="section-title">{$t('settings.reminderInterval')}</span>
  <input class="input" type="number" min="10" max="300" bind:value={reminderInterval} oninput={saveNotificationSettings} />
</div>
<div class="section">
  <span class="section-title">{$t('settings.dueReminderAdvance')}</span>
  <select class="input" bind:value={dueReminderAdvanceMinutes} onchange={saveNotificationSettings}>
    <option value={60}>{$t('settings.dueReminderAdvance1Hour')}</option>
    <option value={360}>{$t('settings.dueReminderAdvance6Hours')}</option>
    <option value={1440}>{$t('settings.dueReminderAdvance1Day')}</option>
    <option value={4320}>{$t('settings.dueReminderAdvance3Days')}</option>
    <option value={10080}>{$t('settings.dueReminderAdvance7Days')}</option>
  </select>
</div>

<style>
  .section { display: flex; flex-direction: column; gap: 8px; }
  .section-title { font-size: 11px; color: var(--text-muted); text-transform: uppercase; letter-spacing: 0; }
  .input { width: 100%; padding: 8px 10px; font-size: 13px; border-radius: 6px; background: var(--surface); border: 1px solid var(--border); color: var(--text); }
  .input:focus { border-color: var(--accent); outline: none; }
  .toggle-row { display: flex; align-items: center; justify-content: space-between; }
  .toggle-label { font-size: 13px; color: var(--text); }
  .toggle-switch { width: 40px; height: 22px; border-radius: 11px; background: var(--border); position: relative; transition: background 0.2s; }
  .toggle-switch.on { background: var(--accent); }
  .toggle-knob { position: absolute; top: 2px; left: 2px; width: 18px; height: 18px; border-radius: 50%; background: #fff; transition: transform 0.2s; }
  .toggle-switch.on .toggle-knob { transform: translateX(18px); }
</style>
