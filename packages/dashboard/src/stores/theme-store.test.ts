// @vitest-environment jsdom
/**
 * Theme store contract tests.
 *
 * Pins the three behaviours the toggle button + the rest of the dashboard
 * rely on:
 *
 *   1. setMode persists to localStorage and applies the dark class to
 *      <html> immediately. No reload.
 *   2. cycle walks light → dark → system → light deterministically.
 *   3. system mode resolves to the OS preference and updates when the
 *      OS preference changes (matchMedia 'change' event).
 */

import { describe, it, expect, beforeEach, vi } from 'vitest';

// matchMedia isn't in jsdom by default; install a controllable stub
// before importing the store (the store reads matchMedia on first
// load).
type MqListener = (e: MediaQueryListEvent) => void;
const mqListeners: MqListener[] = [];
let mqMatches = false;

function installMatchMedia(): void {
  Object.defineProperty(window, 'matchMedia', {
    writable: true,
    configurable: true,
    value: (_query: string): MediaQueryList => {
      // Use a getter so the store's closure-captured `mq.matches`
      // reflects the current `mqMatches` flag at read time. Without a
      // getter, the value would freeze at the call to matchMedia() and
      // the OS-preference-change listener would always observe the
      // initial value.
      const mql = {
        get matches() { return mqMatches; },
        media: _query,
        onchange: null,
        addEventListener: (_evt: 'change', cb: MqListener) => {
          mqListeners.push(cb);
        },
        removeEventListener: () => { /* not exercised */ },
        addListener: () => { /* legacy */ },
        removeListener: () => { /* legacy */ },
        dispatchEvent: () => true,
      } as unknown as MediaQueryList;
      return mql;
    },
  });
}

describe('useThemeStore', () => {
  beforeEach(() => {
    mqListeners.length = 0;
    mqMatches = false;
    installMatchMedia();
    window.localStorage.clear();
    document.documentElement.classList.remove('dark');
    // Re-import the store fresh per test so init logic re-runs.
    vi.resetModules();
  });

  it('defaults to system mode and resolves to OS preference', async () => {
    mqMatches = true; // OS says dark
    installMatchMedia();
    const { useThemeStore } = await import('./theme-store.js');
    const state = useThemeStore.getState();
    expect(state.mode).toBe('system');
    expect(state.resolved).toBe('dark');
    expect(document.documentElement.classList.contains('dark')).toBe(true);
  });

  it('setMode writes to localStorage AND toggles the html class immediately', async () => {
    const { useThemeStore } = await import('./theme-store.js');
    useThemeStore.getState().setMode('dark');
    expect(window.localStorage.getItem('tamma-theme')).toBe('dark');
    expect(document.documentElement.classList.contains('dark')).toBe(true);

    useThemeStore.getState().setMode('light');
    expect(window.localStorage.getItem('tamma-theme')).toBe('light');
    expect(document.documentElement.classList.contains('dark')).toBe(false);
  });

  it('cycle walks light → dark → system → light', async () => {
    const { useThemeStore } = await import('./theme-store.js');
    useThemeStore.getState().setMode('light');
    expect(useThemeStore.getState().mode).toBe('light');

    useThemeStore.getState().cycle();
    expect(useThemeStore.getState().mode).toBe('dark');

    useThemeStore.getState().cycle();
    expect(useThemeStore.getState().mode).toBe('system');

    useThemeStore.getState().cycle();
    expect(useThemeStore.getState().mode).toBe('light');
  });

  it('system mode follows OS preference change events', async () => {
    mqMatches = false;
    installMatchMedia();
    const { useThemeStore } = await import('./theme-store.js');
    useThemeStore.getState().setMode('system');
    expect(useThemeStore.getState().resolved).toBe('light');

    // OS flips to dark — fire the matchMedia change listener.
    mqMatches = true;
    for (const listener of mqListeners) {
      listener({ matches: true } as MediaQueryListEvent);
    }
    expect(useThemeStore.getState().resolved).toBe('dark');
    expect(document.documentElement.classList.contains('dark')).toBe(true);
  });

  it('explicit override ignores OS preference change', async () => {
    const { useThemeStore } = await import('./theme-store.js');
    useThemeStore.getState().setMode('light');

    mqMatches = true;
    for (const listener of mqListeners) {
      listener({ matches: true } as MediaQueryListEvent);
    }
    expect(useThemeStore.getState().resolved).toBe('light');
    expect(document.documentElement.classList.contains('dark')).toBe(false);
  });
});
