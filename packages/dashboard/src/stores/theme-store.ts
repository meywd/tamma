/**
 * Theme store — light/dark/system, persisted in localStorage.
 *
 * Tailwind v4 dark mode is wired to a `dark` class on <html>
 * (see index.css `@variant dark`). The store is the single source of
 * truth for which class is applied; React components subscribe to it
 * via the `useTheme` hook.
 *
 * Three modes:
 *   • light  — explicit override, dark class never applied
 *   • dark   — explicit override, dark class always applied
 *   • system — follow the OS prefers-color-scheme media query
 *
 * Default is `system` so first-time visitors match their OS preference.
 * Once they pick a different mode it persists across sessions.
 */

import { create } from 'zustand';

export type ThemeMode = 'light' | 'dark' | 'system';

const STORAGE_KEY = 'tamma-theme';
const MEDIA_QUERY = '(prefers-color-scheme: dark)';

interface ThemeState {
  /** User's chosen mode. Persisted. */
  mode: ThemeMode;
  /** Resolved effective mode after system fallback — what's actually applied. */
  resolved: 'light' | 'dark';
  setMode: (mode: ThemeMode) => void;
  /** Convenience cycle: light → dark → system → light. Used by the toggle button. */
  cycle: () => void;
}

function loadMode(): ThemeMode {
  if (typeof window === 'undefined') return 'system';
  const stored = window.localStorage.getItem(STORAGE_KEY);
  if (stored === 'light' || stored === 'dark' || stored === 'system') return stored;
  return 'system';
}

function resolveMode(mode: ThemeMode): 'light' | 'dark' {
  if (mode !== 'system') return mode;
  if (typeof window === 'undefined') return 'light';
  return window.matchMedia(MEDIA_QUERY).matches ? 'dark' : 'light';
}

function applyToDocument(resolved: 'light' | 'dark'): void {
  if (typeof document === 'undefined') return;
  const root = document.documentElement;
  if (resolved === 'dark') root.classList.add('dark');
  else root.classList.remove('dark');
}

export const useThemeStore = create<ThemeState>((set, get) => {
  const initialMode = loadMode();
  const initialResolved = resolveMode(initialMode);
  applyToDocument(initialResolved);

  // Watch the OS preference for users in `system` mode so their OS-level
  // theme switch flips Tamma in real time. Only react when in system mode
  // — explicit overrides should ignore the OS.
  if (typeof window !== 'undefined' && typeof window.matchMedia === 'function') {
    const mq = window.matchMedia(MEDIA_QUERY);
    const onChange = (): void => {
      if (get().mode !== 'system') return;
      const resolved: 'light' | 'dark' = mq.matches ? 'dark' : 'light';
      applyToDocument(resolved);
      set({ resolved });
    };
    mq.addEventListener('change', onChange);
  }

  return {
    mode: initialMode,
    resolved: initialResolved,
    setMode: (mode) => {
      window.localStorage.setItem(STORAGE_KEY, mode);
      const resolved = resolveMode(mode);
      applyToDocument(resolved);
      set({ mode, resolved });
    },
    cycle: () => {
      const order: ThemeMode[] = ['light', 'dark', 'system'];
      const current = get().mode;
      const idx = order.indexOf(current);
      const next = order[(idx + 1) % order.length] ?? 'system';
      get().setMode(next);
    },
  };
});

/** Convenience hook — returns the resolved theme + cycle action. */
export function useTheme(): { resolved: 'light' | 'dark'; mode: ThemeMode; cycle: () => void } {
  const mode = useThemeStore((s) => s.mode);
  const resolved = useThemeStore((s) => s.resolved);
  const cycle = useThemeStore((s) => s.cycle);
  return { mode, resolved, cycle };
}
