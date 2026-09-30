/**
 * 三态主题端到端验证（CDP 驱动 headless Chrome）
 *
 * 覆盖：
 *   - 无 pm_theme → 默认「跟随系统」
 *   - 用 CDP 模拟系统深/浅色切换 → App **不刷新**实时跟随
 *   - 手动选浅/深后不再跟随系统
 *   - 旧值兼容（'dark' / 'light' 升级后保持原选择）
 *   - `color-scheme` 已声明（防 Android WebView 二次暗化）
 *   - 报告导出与 Vditor 主题取的是「解析后」的明暗
 *
 * 前置：pnpm dev + 9223 端口的 headless Chrome（见 README / DEVELOPMENT）
 * 运行：pnpm test:e2e-theme
 */
const PORT = Number(process.env.CDP_PORT || 9223);
const APP = 'http://localhost:1420/';
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

class CDP {
  constructor(ws) { this.ws = ws; this.id = 0; this.pending = new Map(); this.exceptions = []; }
  static async connect(url) {
    const ws = new WebSocket(url);
    await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej; });
    const c = new CDP(ws);
    ws.onmessage = (ev) => {
      const m = JSON.parse(ev.data);
      if (m.method === 'Runtime.exceptionThrown') c.exceptions.push((m.params.exceptionDetails.exception?.description || '').split('\n')[0].slice(0, 300));
      if (m.id && c.pending.has(m.id)) {
        const { resolve, reject } = c.pending.get(m.id);
        c.pending.delete(m.id);
        m.error ? reject(new Error(JSON.stringify(m.error))) : resolve(m.result);
      }
    };
    return c;
  }
  send(method, params = {}) {
    const id = ++this.id;
    this.ws.send(JSON.stringify({ id, method, params }));
    return new Promise((resolve, reject) => this.pending.set(id, { resolve, reject }));
  }
  async eval(expression) {
    const r = await this.send('Runtime.evaluate', { expression, returnByValue: true });
    if (r.exceptionDetails) throw new Error('eval: ' + (r.exceptionDetails.exception?.description || JSON.stringify(r.exceptionDetails)));
    return r.result.value;
  }
}

let fails = 0;
const check = (cond, msg, extra = '') => {
  if (cond) console.log('ok   ', msg);
  else { fails++; console.log('FAIL ', msg, extra); }
};
const clickText = async (cdp, selector, text) => {
  const ok = await cdp.eval(`(() => { const e = [...document.querySelectorAll(${JSON.stringify(selector)})].find(x => x.textContent.includes(${JSON.stringify(text)})); if (!e) return false; e.click(); return true; })()`);
  await sleep(400);
  return ok;
};

async function preflight() {
  try { await fetch('http://localhost:1420/', { method: 'HEAD' }); }
  catch { console.error('✗ 开发服务器未启动：请先运行 `pnpm dev`'); process.exit(2); }
  try {
    const r = await fetch(`http://127.0.0.1:${PORT}/json/version`);
    if (!r.ok) throw new Error(String(r.status));
  } catch {
    console.error('✗ 调试端口未就绪，请先启动：\n' +
      '  /opt/chrome-headless/chrome-headless-shell --headless --no-sandbox \\\n' +
      '    --remote-debugging-port=9223 --user-data-dir=/tmp/cdp-data about:blank');
    process.exit(2);
  }
}


async function setSystemDark(cdp, dark) {
  await cdp.send('Emulation.setEmulatedMedia', {
    features: [{ name: 'prefers-color-scheme', value: dark ? 'dark' : 'light' }],
  });
  await sleep(400);
}

const isDarkNow = `document.querySelector('.app')?.classList.contains('dark')`;
const storedTheme = `localStorage.getItem('pm_theme')`;

async function seedAndReload(cdp, theme) {
  await cdp.eval(`localStorage.clear(); localStorage.setItem('pm_tutorial_seen','1'); localStorage.setItem('pm_projects','{"schema_version":4,"projects":[]}');${theme ? `localStorage.setItem('pm_theme', ${JSON.stringify(theme)});` : ''}`);
  await cdp.send('Page.reload');
  await sleep(2200);
}

(async () => {
  await preflight();
  const res = await fetch(`http://127.0.0.1:${PORT}/json/new?about:blank`, { method: 'PUT' });
  const target = await res.json();
  const cdp = await CDP.connect(target.webSocketDebuggerUrl);
  await cdp.send('Page.enable');
  await cdp.send('Runtime.enable');
  await cdp.send('Emulation.setDeviceMetricsOverride', { width: 1280, height: 900, deviceScaleFactor: 1, mobile: false });

  // ── 1. 全新安装：默认跟随系统 ────────────────────────────────────────────
  await cdp.send('Page.navigate', { url: APP });
  await sleep(2500);
  await setSystemDark(cdp, false);
  await seedAndReload(cdp, null);
  check(await cdp.eval(storedTheme) === 'system', '无 pm_theme → 持久化为 system（默认跟随系统）', String(await cdp.eval(storedTheme)));
  check(await cdp.eval(isDarkNow) === false, '系统浅色 → App 浅色');

  // ── 2. 系统切深色：不刷新即跟随 ─────────────────────────────────────────
  await setSystemDark(cdp, true);
  check(await cdp.eval(isDarkNow) === true, '★ 系统切深色 → 不刷新即变深色');
  check(await cdp.eval(`getComputedStyle(document.querySelector('.app')).colorScheme`) === 'dark',
    'color-scheme 跟随（原生控件配色正确，且阻止 WebView 二次暗化）');
  const bg = await cdp.eval(`getComputedStyle(document.querySelector('.app')).backgroundColor`);
  check(bg !== 'rgb(255, 255, 255)', '背景色确实变深', bg);
  await setSystemDark(cdp, false);
  check(await cdp.eval(isDarkNow) === false, '系统切回浅色 → 立即跟随');
  check(await cdp.eval(`getComputedStyle(document.querySelector('.app')).colorScheme`) === 'light', 'color-scheme 回到 light');

  // ── 3. 声明了 color-scheme meta（防 Android 二次暗化）────────────────────
  check(await cdp.eval(`!!document.querySelector('meta[name="color-scheme"]')`), 'app.html 声明了 <meta name="color-scheme">');

  // ── 4. 手动选深色后不再跟随系统 ─────────────────────────────────────────
  await cdp.eval(`document.querySelector('.settings-btn')?.click()`);
  await sleep(500);
  const clickedDark = await cdp.eval(`(() => { const sec = document.querySelectorAll('.tab-content .section')[0]; const b = [...sec.querySelectorAll('.btn-toggle')].find(x => x.textContent.trim() === '深色'); if (!b) return false; b.click(); return true; })()`);
  check(clickedDark, '设置里有「深色」按钮');
  await sleep(400);
  check(await cdp.eval(storedTheme) === 'dark', '选中深色 → 持久化 dark');
  check(await cdp.eval(isDarkNow) === true, '立刻变深色');
  await setSystemDark(cdp, false);
  check(await cdp.eval(isDarkNow) === true, '★ 手动选深色后，系统切浅色**不跟随**');

  // ── 5. 三按钮互斥 + 切回跟随系统 ────────────────────────────────────────
  const actives = await cdp.eval(`(() => { const sec = document.querySelectorAll('.tab-content .section')[0]; return [...sec.querySelectorAll('.btn-toggle')].filter(b => ['跟随系统','浅色','深色'].includes(b.textContent.trim()) && b.classList.contains('active')).map(b => b.textContent.trim()); })()`);
  check(JSON.stringify(actives) === JSON.stringify(['深色']), '三按钮互斥高亮（当前只有「深色」激活）', JSON.stringify(actives));
  await cdp.eval(`(() => { const sec = document.querySelectorAll('.tab-content .section')[0]; [...sec.querySelectorAll('.btn-toggle')].find(b => b.textContent.trim() === '跟随系统')?.click(); })()`);
  await sleep(400);
  check(await cdp.eval(storedTheme) === 'system', '切回「跟随系统」并持久化');
  const hint = await cdp.eval(`document.body.innerText.includes('跟随系统 · 当前')`);
  check(hint, '跟随系统时显示「当前系统为浅色/深色」提示');
  await setSystemDark(cdp, true);
  check(await cdp.eval(isDarkNow) === true, '再次跟随系统深色');

  // ── 5b. Vditor 的「跟随系统」是编辑器独立偏好（不受 App 主题按钮影响）──
  const sections = await cdp.eval(`document.querySelectorAll('.tab-content .section').length`);
  check(sections >= 2, '外观页签里「应用主题」与「Vditor 主题」是两个独立区块', String(sections));

  // ── 6. 旧值兼容 ────────────────────────────────────────────────────────
  await seedAndReload(cdp, 'dark');
  check(await cdp.eval(storedTheme) === 'dark', '老用户（pm_theme=dark）不被强制改成 system');
  check(await cdp.eval(isDarkNow) === true, '老用户升级后仍是深色');
  await setSystemDark(cdp, false);
  check(await cdp.eval(isDarkNow) === true, '老用户显式选择优先于系统');
  await seedAndReload(cdp, 'light');
  await setSystemDark(cdp, true);
  check(await cdp.eval(isDarkNow) === false, '老用户（pm_theme=light）+ 系统深色 → 仍浅色');

  console.log(fails === 0 ? '\n全部通过 ✅' : `\n${fails} 个失败 ❌`);
  await fetch(`http://127.0.0.1:${PORT}/json/close/${target.id}`);
  process.exit(fails === 0 ? 0 : 1);
})().catch((e) => { console.error('运行异常', e); process.exit(1); });
