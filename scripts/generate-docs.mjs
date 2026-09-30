#!/usr/bin/env node

/**
 * 文档生成器
 *
 * 解析项目源码中的 JSDoc 注释和 Rust 文档注释，
 * 生成类似 Rust Docs 风格的静态 HTML 文档站点。
 *
 * 用法：node scripts/generate-docs.mjs
 * 输出：docs/ 目录
 */

import fs from 'fs';
import path from 'path';
import { fileURLToPath } from 'url';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
const ROOT = path.resolve(__dirname, '..');
const OUT = path.join(ROOT, 'docs');

// ─── Source Scanning ──────────────────────────────────────────────────────────

function walk(dir, exts, files = []) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory() && !['node_modules', '.svelte-kit', 'build', 'target', 'dist', 'docs'].includes(entry.name)) {
      walk(full, exts, files);
    } else if (entry.isFile() && exts.some(e => entry.name.endsWith(e))) {
      files.push(full);
    }
  }
  return files;
}

// ─── TypeScript / Svelte Parser ──────────────────────────────────────────────

function parseTsFile(filePath, content) {
  const rel = path.relative(ROOT, filePath);
  const moduleName = rel.replace(/\.(ts|svelte)$/, '').replace(/\\/g, '/');
  const isSvelte = filePath.endsWith('.svelte');

  let text = content;
  if (isSvelte) {
    const scriptMatch = content.match(/<script[^>]*>([\s\S]*?)<\/script>/);
    text = scriptMatch ? scriptMatch[1] : '';
  }

  const result = {
    file: rel,
    moduleName,
    isSvelte,
    moduleDoc: '',
    interfaces: [],
    types: [],
    functions: [],
    constants: [],
    componentDoc: ''
  };

  // Extract @module doc
  const moduleMatch = text.match(/\/\*\*\s*\n\s*\*\s*([\s\S]*?)@module[\s\S]*?\*\//);
  if (moduleMatch) {
    result.moduleDoc = cleanJSDoc(moduleMatch[0]);
  }

  // For Svelte, extract component-level doc (first JSDoc block in script)
  if (isSvelte) {
    const compDocMatch = text.match(/\/\*\*\s*\n([\s\S]*?)\*\//);
    if (compDocMatch) {
      result.componentDoc = cleanJSDoc(compDocMatch[0]);
    }
  }

  // Extract interfaces
  const intRe = /(\/\*\*[\s\S]*?\*\/\s*)?export\s+interface\s+(\w+)(?:\s*<[^>]*>)?\s*\{([^}]*(?:\{[^}]*\}[^}]*)*)\}/g;
  let m;
  while ((m = intRe.exec(text)) !== null) {
    const doc = m[1] ? cleanJSDoc(m[1]) : '';
    const name = m[2];
    const body = m[3];
    const fields = parseFields(body);
    result.interfaces.push({ name, doc, fields });
  }

  // Extract type aliases
  const typeRe = /(\/\*\*[\s\S]*?\*\/\s*)?export\s+type\s+(\w+)\s*=\s*([^;]+);/g;
  while ((m = typeRe.exec(text)) !== null) {
    const doc = m[1] ? cleanJSDoc(m[1]) : '';
    result.types.push({ name: m[2], doc, definition: m[3].trim() });
  }

  // Extract functions
  const fnRe = /(\/\*\*[\s\S]*?\*\/\s*)?export\s+(?:async\s+)?function\s+(\w+)\s*(?:<[^>]*>)?\s*\(([^)]*)\)(?:\s*:\s*([^\{;{]+))?/g;
  while ((m = fnRe.exec(text)) !== null) {
    const doc = m[1] ? cleanJSDoc(m[1]) : '';
    const name = m[2];
    const params = parseParams(m[3] || '');
    const returns = m[4] ? m[4].trim() : 'void';
    result.functions.push({ name, doc, params, returns });
  }

  // Extract exported constants
  const constRe = /(\/\*\*[\s\S]*?\*\/\s*)?export\s+const\s+(\w+)\s*(?::\s*([^=]+))?\s*=/g;
  while ((m = constRe.exec(text)) !== null) {
    const doc = m[1] ? cleanJSDoc(m[1]) : '';
    result.constants.push({ name: m[2], doc, type: (m[3] || '').trim() });
  }

  return result;
}

function cleanJSDoc(raw) {
  return raw
    .replace(/\/\*\*\s*/, '')
    .replace(/\s*\*\//, '')
    .split('\n')
    .map(l => l.replace(/^\s*\*\s?/, ''))
    .filter(l => !l.trim().startsWith('@'))
    .join('\n')
    .trim();
}

function parseFields(body) {
  const fields = [];
  const fieldRe = /(\/\*\*[\s\S]*?\*\/\s*)?(\w+)\s*[?]?\s*:\s*([^;\n]+)/g;
  let m;
  while ((m = fieldRe.exec(body)) !== null) {
    const doc = m[1] ? cleanJSDoc(m[1]) : '';
    fields.push({ name: m[2], type: m[3].trim(), doc });
  }
  return fields;
}

function parseParams(raw) {
  if (!raw.trim()) return [];
  return raw.split(',').map(p => {
    const parts = p.trim().split(':');
    const name = parts[0]?.replace(/[?]/g, '').trim() || '';
    const type = parts[1]?.trim() || 'unknown';
    return { name, type };
  });
}

// ─── Rust Parser ─────────────────────────────────────────────────────────────

function parseRustFile(filePath, content) {
  const rel = path.relative(ROOT, filePath);
  return {
    file: rel,
    moduleName: rel.replace(/\.rs$/, '').replace(/\\/g, '/'),
    isRust: true,
    moduleDoc: extractRustDoc(content),
    functions: extractRustFns(content),
    interfaces: [],
    types: [],
    constants: [],
    componentDoc: ''
  };
}

function extractRustDoc(content) {
  const lines = content.split('\n');
  const docLines = [];
  for (const l of lines) {
    if (l.trim().startsWith('//!')) {
      docLines.push(l.trim().replace(/^\/\/!?\s?/, ''));
    } else if (l.trim().startsWith('#[') || l.trim() === '') {
      if (docLines.length > 0) break;
    } else {
      if (docLines.length > 0) break;
    }
  }
  return docLines.join('\n').trim();
}

function extractRustFns(content) {
  const fns = [];
  const fnRe = /(\/\/\/[\s\S]*?)?(?:pub\s+)?fn\s+(\w+)\s*\(([^)]*)\)(?:\s*->\s*(\S+))?/g;
  let m;
  while ((m = fnRe.exec(content)) !== null) {
    const doc = (m[1] || '').split('\n').map(l => l.replace(/^\/\/\/\s?/, '')).join('\n').trim();
    fns.push({
      name: m[2],
      doc,
      params: m[3] ? m[3].split(',').map(p => ({ name: p.trim().split(':')[0]?.trim() || '', type: p.trim().split(':')[1]?.trim() || '' })) : [],
      returns: m[4]?.trim() || ''
    });
  }
  return fns;
}

// ─── HTML Generation ─────────────────────────────────────────────────────────

function generateCss() {
  return `
:root {
  --bg: #ffffff; --sidebar-bg: #f5f5f5; --surface: #f9fafb;
  --border: #e5e7eb; --text: #111827; --text-secondary: #4b5563;
  --text-muted: #9ca3af; --accent: #4f46e5; --accent-light: rgba(79,70,229,0.1);
  --code-bg: #f3f4f6; --green: #10b981; --red: #ef4444;
}
.dark {
  --bg: #0f0f14; --sidebar-bg: #16161e; --surface: #1e1e2a;
  --border: #2a2a3a; --text: #e4e4e7; --text-secondary: #a1a1aa;
  --text-muted: #52525b; --accent: #6366f1; --accent-light: rgba(99,102,241,0.15);
  --code-bg: #1e1e2a;
}
*, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }
body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif; background: var(--bg); color: var(--text); line-height: 1.6; display: flex; min-height: 100vh; }

/* Sidebar */
.sidebar { width: 260px; background: var(--sidebar-bg); border-right: 1px solid var(--border); padding: 16px; overflow-y: auto; flex-shrink: 0; position: fixed; top: 0; bottom: 0; left: 0; }
.sidebar-title { font-size: 14px; font-weight: 700; color: var(--accent); margin-bottom: 16px; display: flex; align-items: center; gap: 8px; }
.sidebar-title svg { flex-shrink: 0; }
.nav-group { margin-bottom: 12px; }
.nav-group-title { font-size: 11px; font-weight: 600; color: var(--text-muted); text-transform: uppercase; letter-spacing: 0.05em; margin-bottom: 4px; padding-left: 8px; }
.nav-item { display: block; padding: 4px 8px; font-size: 13px; color: var(--text-secondary); text-decoration: none; border-radius: 4px; transition: all 0.15s; }
.nav-item:hover { background: var(--surface); color: var(--accent); }
.nav-item.active { background: var(--accent-light); color: var(--accent); font-weight: 500; }

/* Main */
.main { margin-left: 260px; flex: 1; padding: 32px 48px; max-width: 960px; }

/* Search */
.search-wrap { margin-bottom: 24px; }
.search-input { width: 100%; padding: 8px 12px; font-size: 13px; border-radius: 6px; background: var(--surface); border: 1px solid var(--border); color: var(--text); }
.search-input:focus { border-color: var(--accent); outline: none; }
.search-input::placeholder { color: var(--text-muted); }
.search-results { margin-top: 8px; display: none; }
.search-results.visible { display: block; }
.search-result { display: block; padding: 6px 12px; font-size: 13px; color: var(--text-secondary); text-decoration: none; border-radius: 4px; }
.search-result:hover { background: var(--surface); color: var(--accent); }
.search-result-kind { font-size: 10px; color: var(--text-muted); padding: 1px 6px; background: var(--surface); border-radius: 3px; margin-right: 6px; }

/* Header */
h1 { font-size: 24px; font-weight: 700; margin-bottom: 8px; }
h1 .file-path { font-size: 12px; color: var(--text-muted); font-weight: 400; margin-left: 12px; }
.module-desc { font-size: 14px; color: var(--text-secondary); margin-bottom: 32px; line-height: 1.7; }

/* Sections */
.section { margin-bottom: 32px; }
.section-title { font-size: 13px; font-weight: 600; color: var(--text-muted); text-transform: uppercase; letter-spacing: 0.05em; margin-bottom: 12px; padding-bottom: 8px; border-bottom: 1px solid var(--border); }

/* Items */
.item { padding: 16px; background: var(--surface); border: 1px solid var(--border); border-radius: 8px; margin-bottom: 12px; cursor: pointer; transition: border-color 0.15s; }
.item:hover { border-color: var(--accent); }
.item-header { display: flex; align-items: baseline; gap: 8px; margin-bottom: 4px; }
.item-name { font-size: 15px; font-weight: 600; font-family: 'SF Mono', 'Fira Code', monospace; color: var(--accent); }
.item-kind { font-size: 10px; color: var(--text-muted); padding: 1px 6px; background: var(--surface); border-radius: 3px; }
.item-sig { font-size: 13px; font-family: 'SF Mono', 'Fira Code', monospace; color: var(--text-secondary); margin-bottom: 8px; word-break: break-all; }
.item-doc { font-size: 13px; color: var(--text-secondary); line-height: 1.7; display: none; }
.item.expanded .item-doc { display: block; }
.item-fields { margin-top: 8px; display: none; }
.item.expanded .item-fields { display: block; }
.field { display: flex; gap: 12px; padding: 4px 0; font-size: 13px; border-bottom: 1px solid var(--border); }
.field:last-child { border-bottom: none; }
.field-name { font-family: 'SF Mono', 'Fira Code', monospace; font-weight: 500; color: var(--text); min-width: 120px; }
.field-type { font-family: 'SF Mono', 'Fira Code', monospace; color: var(--text-muted); min-width: 120px; }
.field-doc { color: var(--text-secondary); flex: 1; }
.param-list { margin-top: 8px; }
.param { display: flex; gap: 8px; font-size: 13px; padding: 2px 0; }
.param-name { font-family: 'SF Mono', 'Fira Code', monospace; color: var(--accent); }
.param-type { font-family: 'SF Mono', 'Fira Code', monospace; color: var(--text-muted); }

/* Theme toggle */
.theme-btn { position: fixed; top: 12px; right: 16px; padding: 6px 10px; font-size: 13px; border-radius: 6px; background: var(--surface); border: 1px solid var(--border); color: var(--text-secondary); cursor: pointer; z-index: 100; }
.theme-btn:hover { border-color: var(--accent); color: var(--accent); }

/* Responsive */
@media (max-width: 768px) {
  .sidebar { display: none; }
  .main { margin-left: 0; padding: 16px; }
}
`;
}

function generateIndexHtml(pages) {
  const nav = buildNav(pages);
  return `<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>ProjectManager 文档</title>
<style>${generateCss()}</style>
</head>
<body>
${generateSidebar(pages, 'index')}
<div class="main">
<h1>ProjectManager 文档</h1>
<div class="module-desc">
<p>多项目任务管理器 — Tauri 2 + Svelte 5 + TypeScript</p>
<p style="margin-top:8px">左侧导航浏览模块，点击展开查看详细信息。支持搜索函数和类型名。</p>
</div>
<div class="section">
<div class="section-title">模块列表</div>
${pages.map(p => `<a class="item" href="${p.slug}.html" style="display:block;text-decoration:none">
<div class="item-header"><span class="item-name">${p.title}</span><span class="item-kind">${p.kind}</span></div>
<div class="item-doc" style="display:block">${p.summary || ''}</div>
</a>`).join('\n')}
</div>
</div>
<button class="theme-btn" onclick="toggleTheme()">🌓</button>
<script>${themeScript()}</script>
</body>
</html>`;
}

function generatePageHtml(page, pages) {
  const nav = buildNav(pages);
  let html = `<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>${page.title} — ProjectManager 文档</title>
<style>${generateCss()}</style>
</head>
<body>
${generateSidebar(pages, page.slug)}
<div class="main">
<h1>${page.title} <span class="file-path">${page.file}</span></h1>
<div class="module-desc">${page.moduleDoc || ''}</div>

<div class="search-wrap">
<input class="search-input" placeholder="搜索函数、类型..." oninput="doSearch(this.value)" />
<div class="search-results" id="search-results"></div>
</div>`;

  // Component doc (Svelte)
  if (page.componentDoc) {
    html += `<div class="section"><div class="section-title">组件说明</div><div class="item expanded"><div class="item-doc" style="display:block">${formatDoc(page.componentDoc)}</div></div></div>`;
  }

  // Types
  if (page.types.length > 0) {
    html += `<div class="section"><div class="section-title">类型别名</div>`;
    for (const t of page.types) {
      html += `<div class="item" onclick="this.classList.toggle('expanded')">
<div class="item-header"><span class="item-name">${t.name}</span><span class="item-kind">type</span></div>
<div class="item-sig">${escapeHtml(t.definition)}</div>
<div class="item-doc">${formatDoc(t.doc)}</div></div>`;
    }
    html += `</div>`;
  }

  // Interfaces
  if (page.interfaces.length > 0) {
    html += `<div class="section"><div class="section-title">接口</div>`;
    for (const iface of page.interfaces) {
      html += `<div class="item" onclick="this.classList.toggle('expanded')">
<div class="item-header"><span class="item-name">${iface.name}</span><span class="item-kind">interface</span></div>
<div class="item-doc">${formatDoc(iface.doc)}</div>`;
      if (iface.fields.length > 0) {
        html += `<div class="item-fields">`;
        for (const f of iface.fields) {
          html += `<div class="field"><span class="field-name">${f.name}</span><span class="field-type">${escapeHtml(f.type)}</span><span class="field-doc">${formatDoc(f.doc)}</span></div>`;
        }
        html += `</div>`;
      }
      html += `</div>`;
    }
    html += `</div>`;
  }

  // Functions
  if (page.functions.length > 0) {
    html += `<div class="section"><div class="section-title">函数</div>`;
    for (const fn of page.functions) {
      const paramStr = fn.params.map(p => `${p.name}: ${p.type}`).join(', ');
      html += `<div class="item" onclick="this.classList.toggle('expanded')">
<div class="item-header"><span class="item-name">${fn.name}</span><span class="item-kind">function</span></div>
<div class="item-sig">(${escapeHtml(paramStr)}): ${escapeHtml(fn.returns)}</div>
<div class="item-doc">${formatDoc(fn.doc)}</div>`;
      if (fn.params.length > 0) {
        html += `<div class="item-fields"><div class="param-list">`;
        for (const p of fn.params) {
          html += `<div class="param"><span class="param-name">${p.name}</span><span class="param-type">${escapeHtml(p.type)}</span></div>`;
        }
        html += `</div></div>`;
      }
      html += `</div>`;
    }
    html += `</div>`;
  }

  // Constants
  if (page.constants.length > 0) {
    html += `<div class="section"><div class="section-title">常量 / Store</div>`;
    for (const c of page.constants) {
      html += `<div class="item" onclick="this.classList.toggle('expanded')">
<div class="item-header"><span class="item-name">${c.name}</span><span class="item-kind">const</span></div>
${c.type ? `<div class="item-sig">${escapeHtml(c.type)}</div>` : ''}
<div class="item-doc">${formatDoc(c.doc)}</div></div>`;
    }
    html += `</div>`;
  }

  html += `</div>
<button class="theme-btn" onclick="toggleTheme()">🌓</button>
<script>${themeScript()}${searchScript(page)}</script>
</body>
</html>`;
  return html;
}

function generateSidebar(pages, activeSlug) {
  const groups = {};
  for (const p of pages) {
    const group = p.group || '其他';
    if (!groups[group]) groups[group] = [];
    groups[group].push(p);
  }

  let html = `<div class="sidebar">
<div class="sidebar-title">
<svg width="20" height="20" viewBox="0 0 128 128" fill="none"><rect width="128" height="128" rx="28" fill="#4f46e5"/><rect x="42" y="50" width="12" height="12" rx="3" fill="#fff"/><path d="M45 56 L48 59 L52 53" stroke="#4f46e5" stroke-width="2.5" stroke-linecap="round"/><rect x="60" y="53" width="26" height="4" rx="2" fill="rgba(255,255,255,0.5)"/></svg>
ProjectManager
</div>
<a class="nav-item${activeSlug === 'index' ? ' active' : ''}" href="index.html">首页</a>`;

  for (const [group, items] of Object.entries(groups)) {
    html += `<div class="nav-group"><div class="nav-group-title">${group}</div>`;
    for (const p of items) {
      html += `<a class="nav-item${activeSlug === p.slug ? ' active' : ''}" href="${p.slug}.html">${p.title}</a>`;
    }
    html += `</div>`;
  }

  html += `</div>`;
  return html;
}

function buildNav(pages) {
  return pages.map(p => ({ slug: p.slug, title: p.title }));
}

function formatDoc(doc) {
  if (!doc) return '';
  return escapeHtml(doc).replace(/\n/g, '<br>');
}

function escapeHtml(s) {
  return (s || '').replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
}

function themeScript() {
  return `
(function() {
  var dark = localStorage.getItem('docs_theme') === 'dark';
  if (dark) document.body.classList.add('dark');
})();
function toggleTheme() {
  document.body.classList.toggle('dark');
  localStorage.setItem('docs_theme', document.body.classList.contains('dark') ? 'dark' : 'light');
}`;
}

function searchScript(page) {
  const items = [
    ...page.interfaces.map(i => ({ name: i.name, kind: 'interface', doc: i.doc })),
    ...page.types.map(t => ({ name: t.name, kind: 'type', doc: t.doc })),
    ...page.functions.map(f => ({ name: f.name, kind: 'function', doc: f.doc })),
    ...page.constants.map(c => ({ name: c.name, kind: 'const', doc: c.doc }))
  ];
  return `
var searchItems = ${JSON.stringify(items)};
function doSearch(q) {
  var el = document.getElementById('search-results');
  if (!q.trim()) { el.className = 'search-results'; return; }
  var lq = q.toLowerCase();
  var matches = searchItems.filter(function(i) { return i.name.toLowerCase().includes(lq); });
  if (matches.length === 0) { el.className = 'search-results'; return; }
  el.className = 'search-results visible';
  el.innerHTML = matches.map(function(i) {
    return '<a class="search-result" href="#' + i.name + '"><span class="search-result-kind">' + i.kind + '</span>' + i.name + '</a>';
  }).join('');
}`;
}

// ─── Main ────────────────────────────────────────────────────────────────────

function main() {
  console.log('Generating docs...');

  // Scan files
  const tsFiles = walk(path.join(ROOT, 'src'), ['.ts']);
  const svelteFiles = walk(path.join(ROOT, 'src'), ['.svelte']);
  const rsFiles = walk(path.join(ROOT, 'src-tauri', 'src'), ['.rs']);

  const pages = [];

  // Parse TS files
  for (const f of tsFiles) {
    const content = fs.readFileSync(f, 'utf-8');
    const parsed = parseTsFile(f, content);
    const rel = parsed.moduleName;
    const group = rel.startsWith('src/lib/types') ? '类型'
      : rel.startsWith('src/lib/utils') ? '工具函数'
      : rel.startsWith('src/lib/stores') ? '状态管理'
      : rel.startsWith('src/lib/repositories') ? '数据层'
      : rel.startsWith('src/lib') ? '库'
      : rel.startsWith('src/routes') ? '路由'
      : '其他';

    // Skip barrel re-exports with no content
    if (parsed.interfaces.length === 0 && parsed.functions.length === 0 && parsed.types.length === 0 && parsed.constants.length === 0 && !parsed.moduleDoc) continue;

    const title = rel.split('/').pop();
    pages.push({
      slug: rel.replace(/\//g, '-'),
      title,
      file: parsed.file,
      kind: '模块',
      group,
      summary: parsed.moduleDoc.split('\n')[0] || '',
      ...parsed
    });
  }

  // Parse Svelte files
  for (const f of svelteFiles) {
    const content = fs.readFileSync(f, 'utf-8');
    const parsed = parseTsFile(f, content);
    const rel = parsed.moduleName;
    const group = rel.includes('shared') ? '共享组件'
      : rel.includes('layout') ? '布局组件'
      : rel.includes('views') ? '视图组件'
      : rel.includes('routes') ? '路由'
      : '组件';

    const title = rel.split('/').pop().replace('.svelte', '');
    pages.push({
      slug: rel.replace(/\//g, '-'),
      title,
      file: parsed.file,
      kind: '组件',
      group,
      summary: parsed.componentDoc.split('\n')[0] || parsed.moduleDoc.split('\n')[0] || '',
      ...parsed
    });
  }

  // Parse Rust files
  for (const f of rsFiles) {
    const content = fs.readFileSync(f, 'utf-8');
    const parsed = parseRustFile(f, content);
    const rel = parsed.moduleName;
    const title = rel.split('/').pop();
    pages.push({
      slug: rel.replace(/\//g, '-'),
      title,
      file: parsed.file,
      kind: 'Rust',
      group: 'Rust 后端',
      summary: parsed.moduleDoc.split('\n')[0] || '',
      ...parsed
    });
  }

  // Sort pages by group then name
  pages.sort((a, b) => {
    if (a.group !== b.group) return a.group.localeCompare(b.group);
    return a.title.localeCompare(b.title);
  });

  // Clean output
  if (fs.existsSync(OUT)) fs.rmSync(OUT, { recursive: true });
  fs.mkdirSync(OUT, { recursive: true });

  // Generate pages
  fs.writeFileSync(path.join(OUT, 'index.html'), generateIndexHtml(pages));
  fs.writeFileSync(path.join(OUT, 'style.css'), generateCss());

  for (const page of pages) {
    fs.writeFileSync(path.join(OUT, `${page.slug}.html`), generatePageHtml(page, pages));
  }

  console.log(`Generated ${pages.length + 1} pages in docs/`);
}

main();
