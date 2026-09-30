# 数据安全 & 工程规范 · 方案与预期目标

> 版本：v0.5.4 ｜ 状态：**Phase 0 / Phase 1 已完成**，Phase 2 待排期（见 §4）
> 目标读者：后续开发/接手者

---

## 0. 现状盘点（都是实测结论，不是猜测）

| # | 现状 | 证据 | 风险 |
|---|---|---|---|
| 1 | **项目没有纳入版本控制**（无 `.git`，只有 `.gitignore`） | `git status` → `fatal: not a git repository` | 🔴 改错一行就回不去；无法对比/二分定位 |
| 2 | 所有 localStorage 写入**静默吞错** | `project-repo.ts:45`、`pm-file-repo.ts:74` 均为 `try { setItem } catch {}` | 🔴 WebView 配额（~5MB）写满时**无声丢数据**，且 `.pm` 快照会变旧 → 同步推旧数据 |
| 3 | `.pm` 文件写入**非原子** | `project_file.rs:16` 直接 `std::fs::write` | 🟠 写到一半断电/崩溃 → 文件截断损坏 |
| 4 | 导入 `.pm` **没有 schema 校验** | `stores/project.ts` `importProject()` 只做 `JSON.parse` + 两个字段判空，失败返回 `false` 不告知用户 | 🟠 半截数据落地 / 用户看不到失败原因 |
| 5 | **没有数据格式版本与迁移框架** | `PmFile.version` 固定 `'1.0'` 且从未被读取 | 🟠 以后改字段只能靠“兼容旧数据”的散落判断 |
| 6 | 撤销历史只存在内存 | `stores/history.ts` 两个数组 | 🟠 刷新/重开即丢，误操作无法挽回 |
| 7 | 没有「一键全量备份 / 恢复」 | 只有单项目导出 `.pm` | 🟠 换设备/灾后恢复要手工逐个导出 |
| 8 | 服务端无自动快照、无审计日志 | `<同步数据目录>` 只有 `index.json` + `projects/*.json`，备份靠手工 `cp -a` | 🟠 服务器误删 = 全端同步删除 |
| 9 | 无 lint / format / CI，60 条 svelte-check warning 长期挂着 | 无 `.eslintrc`/`.prettierrc`；`pnpm check` → 0 error / **60 warning** | 🟡 风格漂移、真问题被淹没 |
| 10 | i18n 双语靠人工同步 | zh/en 各 488 key | 🟡 漏 key 时界面直接显示 key 本身 |
| 11 | 无测试框架 | 已有 4 个零依赖脚本（见下） | 🟡 回归靠人肉 |

**已有的好底子（不要推倒重来）**：`tsconfig` 已 `strict: true`；模块级 JSDoc + `scripts/generate-docs.mjs`；同步有墓碑/备份/冲突弹窗（HANDOVER §13）；已有 4 个可执行的测试脚本。

---

## 1. 总方案：三层防线

```
L2 灾难恢复   快照 / 审计 / 恢复演练 / 一键备份包
                 ↑ 出事能救回来
L1 写入可靠   不静默失败 / 原子写 / schema 校验 / 迁移框架
                 ↑ 不出事
L0 可回滚     版本控制 / 发布 tag / CHANGELOG
                 ↑ 出错能退回去
```

---

## 2. Phase 0：立刻可做（**本次已落地**）

| 项 | 做法 | 命令 |
|---|---|---|
| Android 权限自检 | `scripts/check-android-acl.mjs`：解析 `capabilities/*.json` + 插件 `permissions/*.toml`，比对前端实际调用的命令 | `pnpm check:acl` |
| i18n 对齐自检 | `scripts/check-i18n.mjs`：key 集合 + `{占位符}` + 空文案 | `pnpm check:i18n` |
| 统一质量闸门 | `pnpm verify` = 类型检查 + i18n + ACL + 逻辑测试 | `pnpm verify` |

> 起因就是本次真机报错 `Command plugin:android-fs|write_text_file not allowed by ACL`
> —— 这类问题桌面/浏览器开发完全看不出来，`check:acl` 现在能在本地 1 秒内抓到。

---

## 3. Phase 1：数据安全底座（✅ v0.5.4 已完成）

### 3.1 纳入版本控制【P0，最高优先】

✅ 已完成：`git init` + 基线提交 + `v0.5.3` / `v0.5.3-release` tag
- `git init` + 首次基线提交（`.gitignore` 已覆盖 `node_modules/`、`src-tauri/target/`、`src-tauri/gen/`、`dist-updates/`、`build/`）
- 每次发布打 tag（`v0.5.4`），CHANGELOG 段落与 tag 一一对应
- **验收**：任意一次线上问题都能 `git log`/`git diff` 定位；发布版本可一键 checkout 复现

### 3.2 写入失败必须可见【P0】

✅ 已完成：`stores/storage-health.ts` + 顶栏告警条 + Toast + 配额预警
- `saveProjects()` / `savePmFile()` 改为返回 `boolean`，失败时：
  1. 记 `console.error`；
  2. Toast「本地存储已满，请导出备份」（i18n）；
  3. 同步状态区显示红色告警；
  4. 不再静默继续。
- 顺带做配额预警：写入前估算体积，超过阈值（如 4MB）就提示 + 建议导出。
- **验收**：人为把 localStorage 填满 → 100% 出现可见提示，且不产生“新列表 + 旧快照”的不一致。

### 3.3 `.pm` 原子写【P0】

✅ 已完成：tmp→fsync→.bak→rename，读取回退 .bak，Rust 单测 3 例
- Rust `write_pm_file` 改为：写 `*.tmp` → `fsync` → `rename` 覆盖；同时保留一份 `<name>.pm.bak`（滚动 1 份）。
- **验收**：写入过程中 kill 进程，原文件仍是上一版完整内容（脚本可从 `tests/manual/` 里复现）。

### 3.4 导入校验 + 数据格式版本【P1】

✅ 已完成：`utils/pm-schema.ts`（校验+迁移统一入口 `parsePmText`）
- `PmFile` 增 `schema_version`（数字，当前 `1`）；`utils/pm-schema.ts` 提供 `validatePm(json)`：
  必填字段 / 类型 / 数组上限 / 字符串长度；错误信息带**字段路径**。
- `utils/pm-migrate.ts`：`migratePm(raw)` 链式迁移（v1 → v2 → …），所有导入路径统一入口。
- 导入失败：明确弹窗（含原因），**绝不落地半截数据**。
- **验收**：构造 10 份畸形 `.pm`（缺字段/类型错/超大/空文件/非 JSON）→ 全部给出可读错误、0 崩溃、0 脏数据。

### 3.5 一键全量备份 / 恢复【P1】

✅ 已完成：`utils/backup.ts` + 设置→数据（不含凭据）
- 「设置 → 数据」新增：**导出全量备份**（单 `.json`，含 projects + 设置 + 同步状态元数据，带 schema_version）、**从备份恢复**（预览差异 + 二次确认）。
- 保留「自动备份」：每次导入/批量删除前自动写一份到 `pm_backup_<ts>`（localStorage 保留最近 3 份 + 可导出）。
- **验收**：换台设备 → 导入备份包 → 20 项目 / 2000 任务全量恢复，< 2 分钟。

### 3.6 撤销历史跨刷新【P2】

⬜ 未做（归入 Phase 1 尾项，仍待排期）
- 最近 20 步快照写入 `sessionStorage`（或 IndexedDB），重开应用仍可 `Ctrl+Z`。
- **验收**：拖拽排序 → 刷新 → `Ctrl+Z` 仍能撤销。

---

## 4. Phase 2：同步与灾难恢复（建议 v0.6.x）

| 项 | 做法 | 验收 |
|---|---|---|
| 同步状态可视化 | 项目行显示「本地已改 / 待推 / 已同步 / 冲突」；补「强制推送」按钮（已有强制下载） | 一眼看出哪个项目没同步上 |
| 服务端自动快照 | `index.json` 每小时快照保留 48 份；`projects/` 每日打包保留 7 天 | 误删可回滚到 1 小时前 |
| 服务端审计日志 | `/api/push`、删除操作 append-only 日志（id/时间/来源 IP/rev） | 能查“谁在什么时候删了什么” |
| 恢复演练 | `scripts/pm-sync-restore.sh <快照>` + 每季度实操一次 | 恢复 < 5 分钟 |
| 内容校验 | push 带 `content_hash`，服务端校验；pull 后本地校验 | 传输损坏可发现 |

---

## 5. 文档与代码规范方案

### 5.1 目录与职责（沿用现状，写进文档即可）
```
src/lib/
  actions/       与 DOM 打交道的可复用行为（sortable）
  components/    views(页面级) / shared(通用) / settings / task-detail
  stores/        领域状态，唯一改数据的地方
  repositories/  持久化（localStorage / .pm 文件）
  sync/          云同步（对账引擎 + 客户端）
  utils/         纯函数（无状态、可单测）
  i18n/          zh.ts / en.ts（必须成对）
```
**规则**：组件不直接读写 localStorage；纯逻辑放 `utils/` 并**必须可被脚本测试**。

### 5.2 JSDoc 规范（已有雏形，强制化）
- 每个模块头部：`@module <路径>` + 一句话职责 + `@example`（复杂模块）
- 导出的函数：`@param` / `@returns`；副作用、持久化行为**必须写明**
- 魔法值/约定（如 `sort_order` 步长、ACL 坑）写成行内注释 + 指向文档

### 5.3 i18n 规范
- 新增文案**同时**改 `zh.ts` + `en.ts`，跑 `pnpm check:i18n`
- key 命名：`<领域>.<语义>`，禁止复用含义不同的 key

### 5.4 提交 / 版本 / 发布规范
- Conventional Commits：`feat:` / `fix:` / `docs:` / `chore:` / `refactor:` / `test:`
- 版本号三处一起改（`pnpm verify` 后可加脚本校验一致性）
- 发布：`pnpm verify` → 构建 → 签名 → `pnpm publish:update` → `git tag vX.Y.Z` → 更新 `HANDOVER.md` 状态表
- `CHANGELOG.md` 按「日期 + 序号 + 版本号」段落（现有格式），每条写 问题 / 修改内容 / 结果

### 5.5 质量闸门
| 闸门 | 内容 | 何时跑 |
|---|---|---|
| `pnpm check` | svelte-check 0 error | 每次改代码 |
| `pnpm check:i18n` | 双语对齐 | 改了文案 |
| `pnpm check:acl` | Android 权限 | 改了导出/文件/插件 |
| `pnpm test` | 纯逻辑（版本号、拖拽排序换算） | 每次 |
| `pnpm test:e2e-*` | CDP 真界面回归（拖拽、变更日志） | 发版前 |
| `pnpm verify` | 上面 1+2+3+4 一键 | 发版前必跑 |

**warning 清零计划**：60 → 40（v0.5.4）→ 20（v0.5.5）→ 0（v0.6.0）；每个版本修一部分，不允许新增。

### 5.6 架构与决策文档
- `docs/ARCHITECTURE.md`：数据流、模块职责、同步协议、localStorage key 约定
- `docs/DATA-FORMAT.md`：`.pm` / 备份包字段表 + schema_version + 迁移规则
- `docs/DECISIONS.md`（ADR）：为什么 `sort_order` 稀疏整数、为什么用指针拖拽而非 HTML5 DnD、为什么同步是项目级快照、为什么 ACL 要单独授权

---

## 6. 预期目标（可量化）

| 维度 | 现状 | 目标（v0.6.0） |
|---|---|---|
| 可回滚 | ❌ 无版本控制 | ✅ 每次发布有 tag，任意版本可 checkout |
| 写入失败可见性 | ❌ 静默吞错 4 处 | ✅ 0 处静默；失败必有 UI 提示 |
| 断电安全性 | ❌ 非原子写 | ✅ 原子写 + `.bak` |
| 导入健壮性 | 🟡 无校验 | ✅ 10 份畸形样本全部优雅报错 |
| 灾后恢复 | 🟡 手工 | ✅ 一键备份/恢复 + 服务端小时级快照 + 季度演练 |
| 类型/风格 | 🟡 0 error + 60 warning | ✅ 0 error + 0 warning + lint/format 接入 |
| 文案一致性 | 🟡 人工 | ✅ 脚本强校验 |
| 测试 | 🟡 4 个脚本 | ✅ 纯逻辑 100% 覆盖核心 utils + 关键 UI e2e |
| 文档 | 🟡 HANDOVER + README | ✅ + ARCHITECTURE / DATA-FORMAT / DECISIONS |

---

## 7. 排期建议

| 版本 | 内容 | 预估 |
|---|---|---|
| **v0.5.4** | git 基线 + 写入失败可见 + 原子写 + 导入校验（3.1–3.4） | 1 天 |
| **v0.5.5** | 全量备份/恢复 + 撤销跨刷新 + warning 降到 20（3.5–3.6） | 1 天 |
| **v0.6.0** | 同步可视化 + 服务端快照/审计 + ARCHITECTURE/DECISIONS 文档 + warning 清零 | 2 天 |

**最小可用底线（如果只做一件事）**：先做 **3.1 git init**（成本 5 分钟，收益最大）。

---

## 8. 附：本次已落地的改动

- `scripts/check-android-acl.mjs` + `pnpm check:acl`：提前发现「真机 ACL 报错」这类只在设备上暴露的问题；
  并修掉了真实 bug（`android-fs:default` 不含 `write_file/write_text_file/remove_file`，导致手机端导出 HTML/PDF 报
  `Command plugin:android-fs|write_text_file not allowed by ACL`）。
- `scripts/check-i18n.mjs` + `pnpm check:i18n`：zh/en 504 个 key 已校验对齐。
- `pnpm verify`：发版前一条命令跑完 类型 + i18n + ACL + 逻辑测试。
- `scripts/ts-resolve.mjs` / `ts-alias.mjs`：让 Node 也能 import 应用模块（`$lib`、无扩展名），
  于是 `scripts/test-data-safety.mjs` 可以直接单测 schema / 迁移 / 备份 / 存储健康。
