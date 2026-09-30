import { writable } from 'svelte/store';

export type AnimationLevel = 'none' | 'moderate' | 'rich';

function safeGet(key: string): string | null { try { return localStorage.getItem(key); } catch { return null; } }
function safeSet(key: string, val: string): void { try { localStorage.setItem(key, val); } catch {} }

const stored = safeGet('pm_animation') as AnimationLevel | null;
export const animationLevel = writable<AnimationLevel>(stored || 'moderate');
animationLevel.subscribe(v => safeSet('pm_animation', v));
