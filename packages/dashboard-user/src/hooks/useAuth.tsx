/**
 * useAuth — React context + hook that holds the current user and exposes
 * login / register / logout / refresh mutations.
 *
 * Data is backed by /api/auth/me; the API client handles the 401-refresh
 * dance so the hook only needs to treat UnauthorizedError as "anonymous".
 */

import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
  type JSX,
} from 'react';
import { apiClient, UnauthorizedError } from '../api/client';

export interface AuthUser {
  id: string;
  email: string;
  displayName: string | null;
  tenantId?: string | null;
  role?: string | null;
}

export interface AuthContextValue {
  user: AuthUser | null;
  loading: boolean;
  error: string | null;
  login: (email: string, password: string) => Promise<void>;
  register: (email: string, password: string, displayName: string) => Promise<void>;
  logout: () => Promise<void>;
  refresh: () => Promise<void>;
}

const AuthContext = createContext<AuthContextValue | null>(null);

interface MeResponse {
  user: AuthUser;
}

async function fetchMe(): Promise<AuthUser | null> {
  try {
    const res = await apiClient.get<MeResponse>('/api/auth/me');
    return res.user;
  } catch (err) {
    if (err instanceof UnauthorizedError) {
      return null;
    }
    throw err;
  }
}

export function AuthProvider({ children }: { children: ReactNode }): JSX.Element {
  const [user, setUser] = useState<AuthUser | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  const refresh = useCallback(async () => {
    setLoading(true);
    setError(null);
    try {
      const next = await fetchMe();
      setUser(next);
    } catch (err) {
      setUser(null);
      setError(err instanceof Error ? err.message : 'Failed to load session');
    } finally {
      setLoading(false);
    }
  }, []);

  const login = useCallback(
    async (email: string, password: string) => {
      await apiClient.post('/api/v1/auth/login', { email, password });
      await refresh();
    },
    [refresh],
  );

  const register = useCallback(
    async (email: string, password: string, displayName: string) => {
      await apiClient.post('/api/v1/auth/register', {
        email,
        password,
        displayName,
      });
      // Registration usually requires email verification before /auth/me
      // returns a user, so we refresh but don't force authenticated state.
      await refresh();
    },
    [refresh],
  );

  const logout = useCallback(async () => {
    try {
      await apiClient.post('/api/auth/logout', {});
    } catch {
      // Even if logout fails server-side, clear local state so the user
      // can't keep acting under a stale session.
    }
    setUser(null);
  }, []);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  const value = useMemo<AuthContextValue>(
    () => ({ user, loading, error, login, register, logout, refresh }),
    [user, loading, error, login, register, logout, refresh],
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
  const ctx = useContext(AuthContext);
  if (ctx === null) {
    throw new Error('useAuth must be used inside <AuthProvider>');
  }
  return ctx;
}
