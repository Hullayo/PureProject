import type { SyncConfig, SyncResult } from './types';

/**
 * Rust 侧同步命令封装（WebDAV / GitHub Gist）
 *
 * @module sync/rust
 */

async function invoke<T>(cmd: string, args?: Record<string, unknown>): Promise<T> {
  const { invoke: tauriInvoke } = await import('@tauri-apps/api/core');
  return tauriInvoke<T>(cmd, args);
}

export async function rustSyncPush(config: SyncConfig, projectJson: string): Promise<SyncResult> {
  return invoke<SyncResult>('sync_push', { config, projectJson });
}

export async function rustSyncPull(config: SyncConfig): Promise<string> {
  return invoke<string>('sync_pull', { config });
}

export async function rustSyncTest(config: SyncConfig): Promise<boolean> {
  return invoke<boolean>('sync_test', { config });
}

export async function saveCredential(service: string, key: string, value: string): Promise<void> {
  return invoke<void>('save_credential', { service, key, value });
}

export async function loadCredential(service: string, key: string): Promise<string> {
  return invoke<string>('load_credential', { service, key });
}
