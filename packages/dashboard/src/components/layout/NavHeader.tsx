/**
 * NavHeader — Unified cross-service navigation bar (Story 16.4).
 *
 * Native React component for the Tamma Dashboard. Mirrors the visual
 * design of the injected tamma-nav.html used on third-party dashboards
 * (OpenSearch, ELSA Studio) via nginx sub_filter.
 *
 * All three service links (Dashboard, Workflows, Logs) are always visible
 * to every authenticated user. Only the Admin link is role-gated
 * (admin/owner).
 */

import { useState, useEffect, useRef, useCallback, type JSX } from 'react';
import { useAuth } from '../../hooks/useAuth.js';
import type { AuthUser } from '../../hooks/useAuth.js';
import { useTheme, type ThemeMode } from '../../stores/theme-store.js';
import './NavHeader.css';

export interface ServiceLink {
  key: string;
  label: string;
  url: string;
}

export const ALL_SERVICES: ServiceLink[] = [
  { key: 'app', label: 'Dashboard', url: 'https://app.tamma.dev' },
  { key: 'elsa', label: 'Workflows', url: 'https://elsa.tamma.dev' },
  { key: 'logs', label: 'Logs', url: 'https://logs.tamma.dev' },
];

export function isActiveService(key: string): boolean {
  const host = window.location.hostname;
  if (key === 'app') return host === 'app.tamma.dev' || host === 'localhost';
  return host === `${key}.tamma.dev`;
}

export function isAdmin(user: AuthUser | null): boolean {
  return user?.role === 'admin' || user?.role === 'owner';
}

export function isAdminPageActive(): boolean {
  return window.location.pathname.startsWith('/admin');
}

export function NavHeader(): JSX.Element {
  const { user, logout } = useAuth();
  const [menuOpen, setMenuOpen] = useState(false);
  const userRef = useRef<HTMLDivElement>(null);
  const menuBtnRef = useRef<HTMLButtonElement>(null);

  // Close menu on outside click
  useEffect(() => {
    function handleClick(e: MouseEvent): void {
      if (userRef.current && !userRef.current.contains(e.target as Node)) {
        setMenuOpen(false);
      }
    }
    document.addEventListener('mousedown', handleClick);
    return () => document.removeEventListener('mousedown', handleClick);
  }, []);

  // Escape key closes menu and returns focus to trigger button
  const handleKeyDown = useCallback((e: React.KeyboardEvent): void => {
    if (e.key === 'Escape' && menuOpen) {
      setMenuOpen(false);
      menuBtnRef.current?.focus();
    }
  }, [menuOpen]);

  function handleSignOut(e: React.MouseEvent): void {
    e.preventDefault();
    logout();
  }

  return (
    <nav className="tamma-nav-bar" aria-label="Tamma services">
      <a href="#main-content" className="tn-skip">Skip to main content</a>

      <a className="tn-logo" href="https://app.tamma.dev" style={{ display: 'flex', alignItems: 'center', gap: '8px' }}>
        <img src="/logo.png" alt="" style={{ width: '24px', height: '24px', borderRadius: '4px' }} />
        Tamma
      </a>

      <div className="tn-links">
        {ALL_SERVICES.map((svc) => (
          <a
            key={svc.key}
            href={svc.url}
            className={isActiveService(svc.key) ? 'tn-active' : ''}
            aria-current={isActiveService(svc.key) ? 'page' : undefined}
          >
            {svc.label}
          </a>
        ))}
        {isAdmin(user) && (
          <a
            href="https://app.tamma.dev/admin"
            className={isAdminPageActive() ? 'tn-active' : ''}
            aria-current={isAdminPageActive() ? 'page' : undefined}
          >
            Admin
          </a>
        )}
      </div>

      <div className="tn-spacer" />

      <ThemeToggleButton />

      {user && (
        <div
          className="tn-user"
          ref={userRef}
          onKeyDown={handleKeyDown}
        >
          <button
            ref={menuBtnRef}
            className="tn-user-trigger"
            onClick={() => setMenuOpen((prev) => !prev)}
            aria-haspopup="menu"
            aria-expanded={menuOpen}
            type="button"
          >
            <img
              className="tn-avatar"
              src={`https://github.com/${user.username}.png?size=56`}
              alt=""
            />
            <span className="tn-username">{user.username}</span>
          </button>

          {menuOpen && (
            <div className="tn-menu" role="menu">
              <a href="/account" role="menuitem">Settings</a>
              <a href="/" role="menuitem" onClick={handleSignOut}>
                Sign Out
              </a>
            </div>
          )}
        </div>
      )}
    </nav>
  );
}

/**
 * Three-state theme toggle: light → dark → system → light. Single button so
 * the nav bar doesn't grow a dropdown for what's a transient choice. The
 * icon reflects the *resolved* theme (sun in light, moon in dark) and the
 * tooltip names the active *mode* (which may be `system` even though the
 * resolved appearance is light or dark).
 */
function ThemeToggleButton(): JSX.Element {
  const { mode, resolved, cycle } = useTheme();
  const label = themeLabel(mode);
  return (
    <button
      type="button"
      onClick={cycle}
      title={`Theme: ${label} (click to cycle)`}
      aria-label={`Theme: ${label}. Click to switch.`}
      className="tn-theme-toggle"
    >
      {resolved === 'dark' ? <MoonIcon /> : <SunIcon />}
    </button>
  );
}

function themeLabel(mode: ThemeMode): string {
  switch (mode) {
    case 'light': return 'Light';
    case 'dark': return 'Dark';
    case 'system': return 'System';
  }
}

function SunIcon(): JSX.Element {
  return (
    <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor"
         strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <circle cx="12" cy="12" r="4" />
      <path d="M12 2v2M12 20v2M4.93 4.93l1.41 1.41M17.66 17.66l1.41 1.41M2 12h2M20 12h2M4.93 19.07l1.41-1.41M17.66 6.34l1.41-1.41" />
    </svg>
  );
}

function MoonIcon(): JSX.Element {
  return (
    <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor"
         strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d="M21 12.79A9 9 0 1 1 11.21 3 7 7 0 0 0 21 12.79z" />
    </svg>
  );
}
