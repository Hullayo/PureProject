/**
 * 云同步类型定义
 *
 * @module sync/types
 */

/** 同步后端：WebDAV / GitHub Gist / 自建服务器 */
export type SyncBackend = 'webdav' | 'github_gist' | 'server';

/** 同步模式：手动 / 自动 */
export type SyncMode = 'manual' | 'auto';

export interface SyncConfig {
  backend: SyncBackend;
  /** WebDAV/Gist 地址，或自建服务器基址（由用户在设置里填写） */
  url: string;
  username: string;
  /** WebDAV 密码 / Gist Token / 服务器 Token */
  password: string;
}

export interface SyncResult {
  success: boolean;
  message: string;
}

export interface WebDavPreset {
  name: string;
  url: string;
}

export const WEBDAV_PRESETS: WebDavPreset[] = [
  { name: '坚果云', url: 'https://dav.jianguoyun.com/dav/' },
  { name: '自定义', url: '' },
];

/** 服务器同步：项目元数据 */
export interface ServerProjectMeta {
  id: string;
  name: string;
  rev: number;
  updated_at: string;
  schema_version: number;
  deleted?: boolean;
}

/** 服务器同步：增量变更项 */
export interface ServerChange extends ServerProjectMeta {
  /** 完整 .pm JSON（删除项无此字段） */
  data?: string;
}

/** 服务器同步：乐观锁冲突（HTTP 409） */
export interface ServerConflict {
  conflict: true;
  reason?: 'revision_conflict' | 'schema_downgrade';
  id: string;
  serverRev: number;
  serverUpdatedAt: string | null;
  serverData: string | null;
  serverSchemaVersion?: number;
  incomingSchemaVersion?: number;
  /** 服务器端该项目已被删除（墓碑）；此时 `serverData` 为 null，"服务器版本"即删除 */
  deleted?: boolean;
}

/** 服务器 SSE 事件 */
export interface ServerEvent {
  type: 'hello' | 'change' | 'delete';
  id?: string;
  rev: number;
  updated_at?: string;
  name?: string;
  schema_version?: number;
}

/** 默认服务器地址 */
/** 自建服务器地址默认值：**留空**（不内置任何地址，由用户配置） */
export const DEFAULT_SERVER_URL = '';

/** 默认自动同步间隔（秒） */
export const DEFAULT_SYNC_INTERVAL = 10;
