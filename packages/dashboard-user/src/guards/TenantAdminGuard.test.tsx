/**
 * TenantAdminGuard — role-gate tests.
 */

import { describe, it, expect, beforeEach, vi } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { MemoryRouter } from 'react-router-dom';
import { AuthProvider } from '../hooks/useAuth';
import { TenantAdminGuard } from './TenantAdminGuard';

function renderWith(role: string | null) {
  globalThis.fetch = vi.fn().mockResolvedValueOnce(
    new Response(
      JSON.stringify({
        user: {
          id: 'u',
          email: 'e',
          displayName: 'D',
          tenantId: 't',
          role,
        },
      }),
      { status: 200, headers: { 'content-type': 'application/json' } },
    ),
  ) as unknown as typeof fetch;

  return render(
    <MemoryRouter>
      <AuthProvider>
        <TenantAdminGuard>
          <div>gated-content</div>
        </TenantAdminGuard>
      </AuthProvider>
    </MemoryRouter>,
  );
}

describe('TenantAdminGuard', () => {
  beforeEach(() => vi.restoreAllMocks());

  it.each([
    ['admin', true],
    ['owner', true],
    ['member', false],
    [null, false],
  ])('role=%s → content visible=%s', async (role, visible) => {
    renderWith(role);
    if (visible) {
      await waitFor(() =>
        expect(screen.getByText('gated-content')).toBeInTheDocument(),
      );
    } else {
      await waitFor(() =>
        expect(screen.getByText(/admin-only/i)).toBeInTheDocument(),
      );
      expect(screen.queryByText('gated-content')).not.toBeInTheDocument();
    }
  });
});
