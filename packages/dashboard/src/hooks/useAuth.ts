/**
 * useAuth hook — fetches the current user from /api/auth/me.
 *
 * Returns { user, loading, error } where user matches the JWT payload
 * set by the GitHub OAuth flow (see routes/auth/github-oauth.ts).
 */

import { useState, useEffect } from 'react';

export interface AuthUser {
  id: string;
  username: string;
  githubId: number;
  role: string;
}

interface AuthState {
  user: AuthUser | null;
  loading: boolean;
  error: string | null;
}

export function useAuth(): AuthState {
  const [state, setState] = useState<AuthState>({
    user: null,
    loading: true,
    error: null,
  });

  useEffect(() => {
    let cancelled = false;

    async function fetchUser(): Promise<void> {
      try {
        const response = await fetch('/api/auth/me', { credentials: 'include' });
        if (!response.ok) {
          if (!cancelled) {
            setState({ user: null, loading: false, error: 'Not authenticated' });
          }
          return;
        }
        const data = (await response.json()) as { user: AuthUser };
        if (!cancelled) {
          setState({ user: data.user, loading: false, error: null });
        }
      } catch {
        if (!cancelled) {
          setState({ user: null, loading: false, error: 'Failed to fetch user' });
        }
      }
    }

    void fetchUser();

    return () => {
      cancelled = true;
    };
  }, []);

  return state;
}
