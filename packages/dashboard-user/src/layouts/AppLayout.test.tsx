/**
 * Sidebar honesty pin (Story 45-2 AC6 / test 9): every <Link to> in the
 * sidebar must be a declared route. The previous sidebar carried /repos,
 * /runs and /settings — copies of the ADMIN app's routes that have never
 * existed here, each a silent blank pane. This test makes the next copied
 * link fail CI instead of shipping.
 */

import { describe, it, expect, vi } from 'vitest';
import { render } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { AppLayout } from './AppLayout';
import { ROUTE_PATHS } from '../App';

vi.mock('../hooks/useAuth', () => ({
  useAuth: () => ({
    user: {
      id: 'u-1',
      email: 'owner@acme.dev',
      displayName: 'Owner',
      tenantId: 'tnt-1',
      role: 'owner',
    },
    loading: false,
    error: null,
    login: vi.fn(),
    register: vi.fn(),
    logout: vi.fn(),
    refresh: vi.fn(),
  }),
}));

describe('AppLayout sidebar', () => {
  it('links only to declared routes', () => {
    const { container } = render(
      <MemoryRouter>
        <AppLayout>
          <p>content</p>
        </AppLayout>
      </MemoryRouter>,
    );

    const sidebarLinks = Array.from(container.querySelectorAll('aside nav a')).map((a) =>
      a.getAttribute('href'),
    );

    expect(sidebarLinks.length).toBeGreaterThan(0);
    for (const href of sidebarLinks) {
      expect(ROUTE_PATHS, `sidebar links to undeclared route ${href}`).toContain(href);
    }
  });

  it('no longer links to the admin app routes that never existed here', () => {
    const { container } = render(
      <MemoryRouter>
        <AppLayout>
          <p>content</p>
        </AppLayout>
      </MemoryRouter>,
    );

    const hrefs = Array.from(container.querySelectorAll('a')).map((a) => a.getAttribute('href'));
    expect(hrefs).not.toContain('/repos');
    expect(hrefs).not.toContain('/runs');
    expect(hrefs).not.toContain('/settings');
  });

  it('renders children in place of the outlet when given', () => {
    const { getByText } = render(
      <MemoryRouter>
        <AppLayout>
          <p>injected child</p>
        </AppLayout>
      </MemoryRouter>,
    );
    expect(getByText('injected child')).toBeInTheDocument();
  });
});
