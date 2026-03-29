/**
 * NavHeader — Unified cross-service navigation bar (Story 16.4).
 *
 * Native React component for the Tamma Dashboard. Mirrors the visual
 * design of the injected tamma-nav.html used on third-party dashboards
 * (OpenSearch, ELSA Studio) via nginx sub_filter.
 */

import { useState, useEffect, useRef } from 'react';
import { useAuth } from '../../hooks/useAuth.js';
import type { AuthUser } from '../../hooks/useAuth.js';
import './NavHeader.css';

interface ServiceLink {
  key: string;
  label: string;
  url: string;
}

const ALL_SERVICES: ServiceLink[] = [
  { key: 'app', label: 'Dashboard', url: 'https://app.tamma.dev' },
  { key: 'elsa', label: 'Workflows', url: 'https://elsa.tamma.dev' },
  { key: 'logs', label: 'Logs', url: 'https://logs.tamma.dev' },
];

function isActiveService(key: string): boolean {
  const host = window.location.hostname;
  if (key === 'app') return host === 'app.tamma.dev' || host === 'localhost';
  return host === `${key}.tamma.dev`;
}

function isAdmin(user: AuthUser | null): boolean {
  return user?.role === 'admin' || user?.role === 'owner';
}

export function NavHeader(): JSX.Element {
  const { user } = useAuth();
  const [menuOpen, setMenuOpen] = useState(false);
  const userRef = useRef<HTMLDivElement>(null);

  // Close menu on outside click
  useEffect(() => {
    function handleClick(e: MouseEvent): void {
      if (userRef.current && !userRef.current.contains(e.target as Node)) {
        setMenuOpen(false);
      }
    }
    document.addEventListener('click', handleClick);
    return () => document.removeEventListener('click', handleClick);
  }, []);

  // Filter services: members only see Dashboard; admins see all
  const services = isAdmin(user) ? ALL_SERVICES : ALL_SERVICES.filter((s) => s.key === 'app');

  function handleSignOut(e: React.MouseEvent): void {
    e.preventDefault();
    fetch('/api/auth/logout', { method: 'POST', credentials: 'include' })
      .finally(() => {
        window.location.href = '/login';
      });
  }

  return (
    <nav className="tamma-nav-bar">
      <a className="tn-logo" href="https://app.tamma.dev">
        Tamma
      </a>

      <div className="tn-links">
        {services.map((svc) => (
          <a
            key={svc.key}
            href={svc.url}
            className={isActiveService(svc.key) ? 'tn-active' : ''}
          >
            {svc.label}
          </a>
        ))}
        {isAdmin(user) && (
          <a href="https://app.tamma.dev/admin">Admin</a>
        )}
      </div>

      <div className="tn-spacer" />

      {user && (
        <div
          className="tn-user"
          ref={userRef}
          onClick={() => setMenuOpen((prev) => !prev)}
        >
          <img
            className="tn-avatar"
            src={`https://github.com/${user.username}.png?size=56`}
            alt={user.username}
          />
          <span className="tn-username">{user.username}</span>

          {menuOpen && (
            <div className="tn-menu">
              <a href="/account">Settings</a>
              <a href="/" onClick={handleSignOut}>
                Sign Out
              </a>
            </div>
          )}
        </div>
      )}
    </nav>
  );
}
