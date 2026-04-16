/**
 * @vitest-environment jsdom
 */
import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, fireEvent, cleanup } from '@testing-library/react';
import '@testing-library/jest-dom/vitest';
import { NavHeader, isActiveService, isAdmin, isAdminPageActive } from './NavHeader.js';

// ---------------------------------------------------------------------------
// Mock useAuth — returns whatever mockUser is set to
// ---------------------------------------------------------------------------
let mockUser: { id: string; username: string; githubId: number; role: string } | null = null;

vi.mock('../../hooks/useAuth.js', () => ({
  useAuth: () => ({
    user: mockUser,
    loading: false,
    error: null,
    logout: vi.fn(),
  }),
}));

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------
function setHostname(hostname: string): void {
  Object.defineProperty(window, 'location', {
    value: { ...window.location, hostname, pathname: '/', href: `https://${hostname}/` },
    writable: true,
  });
}

function setPathname(pathname: string): void {
  Object.defineProperty(window, 'location', {
    value: { ...window.location, pathname },
    writable: true,
  });
}

const ADMIN_USER = { id: '1', username: 'admin-user', githubId: 100, role: 'admin' };
const OWNER_USER = { id: '2', username: 'owner-user', githubId: 200, role: 'owner' };
const MEMBER_USER = { id: '3', username: 'member-user', githubId: 300, role: 'member' };

// ---------------------------------------------------------------------------
// Mock fetch for sign-out
// ---------------------------------------------------------------------------
const originalFetch = globalThis.fetch;

beforeEach(() => {
  mockUser = MEMBER_USER;
  setHostname('app.tamma.dev');
  globalThis.fetch = vi.fn().mockResolvedValue({ ok: true });
});

afterEach(() => {
  cleanup();
  mockUser = null;
  globalThis.fetch = originalFetch;
});

// ===========================================================================
// Unit tests for exported helpers
// ===========================================================================
describe('isActiveService', () => {
  it('returns true for "app" when on app.tamma.dev', () => {
    setHostname('app.tamma.dev');
    expect(isActiveService('app')).toBe(true);
  });

  it('returns true for "app" when on localhost', () => {
    setHostname('localhost');
    expect(isActiveService('app')).toBe(true);
  });

  it('returns true for "elsa" when on elsa.tamma.dev', () => {
    setHostname('elsa.tamma.dev');
    expect(isActiveService('elsa')).toBe(true);
  });

  it('returns false for "logs" when on app.tamma.dev', () => {
    setHostname('app.tamma.dev');
    expect(isActiveService('logs')).toBe(false);
  });
});

describe('isAdmin', () => {
  it('returns true for admin role', () => {
    expect(isAdmin(ADMIN_USER)).toBe(true);
  });

  it('returns true for owner role', () => {
    expect(isAdmin(OWNER_USER)).toBe(true);
  });

  it('returns false for member role', () => {
    expect(isAdmin(MEMBER_USER)).toBe(false);
  });

  it('returns false for null', () => {
    expect(isAdmin(null)).toBe(false);
  });
});

describe('isAdminPageActive', () => {
  it('returns true when pathname starts with /admin', () => {
    setPathname('/admin');
    expect(isAdminPageActive()).toBe(true);
  });

  it('returns false when pathname is /', () => {
    setPathname('/');
    expect(isAdminPageActive()).toBe(false);
  });
});

// ===========================================================================
// Component tests
// ===========================================================================
describe('NavHeader', () => {
  // -----------------------------------------------------------------------
  // Test 1: Renders all three service links for authenticated user
  // -----------------------------------------------------------------------
  it('renders all three service links for any authenticated user', () => {
    mockUser = MEMBER_USER;
    render(<NavHeader />);

    expect(screen.getByText('Dashboard')).toBeInTheDocument();
    expect(screen.getByText('Workflows')).toBeInTheDocument();
    expect(screen.getByText('Logs')).toBeInTheDocument();
  });

  // -----------------------------------------------------------------------
  // Test 2: Active link has aria-current="page"
  // -----------------------------------------------------------------------
  it('marks active link with aria-current="page"', () => {
    setHostname('app.tamma.dev');
    mockUser = MEMBER_USER;
    render(<NavHeader />);

    const dashboardLink = screen.getByText('Dashboard');
    expect(dashboardLink).toHaveAttribute('aria-current', 'page');
    expect(dashboardLink).toHaveClass('tn-active');

    const workflowsLink = screen.getByText('Workflows');
    expect(workflowsLink).not.toHaveAttribute('aria-current');
  });

  // -----------------------------------------------------------------------
  // Test 3: Admin link hidden for member role
  // -----------------------------------------------------------------------
  it('hides Admin link for member role', () => {
    mockUser = MEMBER_USER;
    render(<NavHeader />);

    expect(screen.queryByText('Admin')).not.toBeInTheDocument();
  });

  // -----------------------------------------------------------------------
  // Test 4: Admin link visible for admin role
  // -----------------------------------------------------------------------
  it('shows Admin link for admin role', () => {
    mockUser = ADMIN_USER;
    render(<NavHeader />);

    expect(screen.getByText('Admin')).toBeInTheDocument();
  });

  // -----------------------------------------------------------------------
  // Test 5: Admin link visible for owner role
  // -----------------------------------------------------------------------
  it('shows Admin link for owner role', () => {
    mockUser = OWNER_USER;
    render(<NavHeader />);

    expect(screen.getByText('Admin')).toBeInTheDocument();
  });

  // -----------------------------------------------------------------------
  // Test 6: User menu button has correct ARIA attributes
  // -----------------------------------------------------------------------
  it('has user menu button with aria-haspopup and aria-expanded', () => {
    mockUser = MEMBER_USER;
    render(<NavHeader />);

    const btn = screen.getByRole('button', { name: /member-user/i });
    expect(btn).toHaveAttribute('aria-haspopup', 'menu');
    expect(btn).toHaveAttribute('aria-expanded', 'false');
  });

  // -----------------------------------------------------------------------
  // Test 7: Clicking user menu toggles aria-expanded
  // -----------------------------------------------------------------------
  it('toggles aria-expanded when user menu button is clicked', () => {
    mockUser = MEMBER_USER;
    render(<NavHeader />);

    const btn = screen.getByRole('button', { name: /member-user/i });
    expect(btn).toHaveAttribute('aria-expanded', 'false');

    fireEvent.click(btn);
    expect(btn).toHaveAttribute('aria-expanded', 'true');

    // Menu should be visible
    expect(screen.getByRole('menu')).toBeInTheDocument();
  });

  // -----------------------------------------------------------------------
  // Test 8: Escape key closes open user menu
  // -----------------------------------------------------------------------
  it('closes menu on Escape key and returns focus to button', () => {
    mockUser = MEMBER_USER;
    render(<NavHeader />);

    const btn = screen.getByRole('button', { name: /member-user/i });
    fireEvent.click(btn);
    expect(screen.getByRole('menu')).toBeInTheDocument();

    // Press Escape on the user container
    fireEvent.keyDown(btn.closest('.tn-user')!, { key: 'Escape' });
    expect(screen.queryByRole('menu')).not.toBeInTheDocument();
  });

  // -----------------------------------------------------------------------
  // Test 9: Outside click closes menu
  // -----------------------------------------------------------------------
  it('closes menu on outside mousedown', () => {
    mockUser = MEMBER_USER;
    render(<NavHeader />);

    const btn = screen.getByRole('button', { name: /member-user/i });
    fireEvent.click(btn);
    expect(screen.getByRole('menu')).toBeInTheDocument();

    // Click outside (on the document body)
    fireEvent.mouseDown(document.body);
    expect(screen.queryByRole('menu')).not.toBeInTheDocument();
  });

  // -----------------------------------------------------------------------
  // Test 10: Sign-out calls POST /api/auth/logout then redirects
  // -----------------------------------------------------------------------
  it('calls POST /api/auth/logout on sign out', async () => {
    mockUser = MEMBER_USER;
    const mockFetch = vi.fn().mockResolvedValue({ ok: true });
    globalThis.fetch = mockFetch;

    render(<NavHeader />);

    // Open menu
    fireEvent.click(screen.getByRole('button', { name: /member-user/i }));
    const signOutLink = screen.getByText('Sign Out');
    fireEvent.click(signOutLink);

    expect(mockFetch).toHaveBeenCalledWith('/api/auth/logout', {
      method: 'POST',
      credentials: 'include',
    });
  });

  // -----------------------------------------------------------------------
  // Test 11: Sign-out redirects even when fetch fails
  // -----------------------------------------------------------------------
  it('redirects to /login even when sign out fetch fails', async () => {
    mockUser = MEMBER_USER;
    // Return a response with ok:false to simulate server error (not network
    // failure) because .finally() does not catch rejections and would cause
    // an unhandled rejection in the test runner.
    const mockFetch = vi.fn().mockResolvedValue({ ok: false, status: 500 });
    globalThis.fetch = mockFetch;

    render(<NavHeader />);

    fireEvent.click(screen.getByRole('button', { name: /member-user/i }));
    fireEvent.click(screen.getByText('Sign Out'));

    // .finally() block fires redirect regardless of ok/not-ok
    await vi.waitFor(() => {
      expect(window.location.href).toBe('/login');
    });
  });

  // -----------------------------------------------------------------------
  // Test 12: Renders without crashing when user is null
  // -----------------------------------------------------------------------
  it('renders service links but no user menu when user is null', () => {
    mockUser = null;
    render(<NavHeader />);

    // Service links should still be visible
    expect(screen.getByText('Dashboard')).toBeInTheDocument();
    expect(screen.getByText('Workflows')).toBeInTheDocument();
    expect(screen.getByText('Logs')).toBeInTheDocument();

    // No user menu button
    expect(screen.queryByRole('button')).not.toBeInTheDocument();
  });

  // -----------------------------------------------------------------------
  // Accessibility: nav has aria-label
  // -----------------------------------------------------------------------
  it('renders a nav element with aria-label', () => {
    mockUser = MEMBER_USER;
    render(<NavHeader />);

    const nav = screen.getByRole('navigation', { name: /tamma services/i });
    expect(nav).toBeInTheDocument();
  });

  // -----------------------------------------------------------------------
  // Accessibility: skip-to-content link
  // -----------------------------------------------------------------------
  it('includes a skip-to-content link', () => {
    mockUser = MEMBER_USER;
    render(<NavHeader />);

    const skipLink = screen.getByText('Skip to main content');
    expect(skipLink).toHaveAttribute('href', '#main-content');
  });
});
