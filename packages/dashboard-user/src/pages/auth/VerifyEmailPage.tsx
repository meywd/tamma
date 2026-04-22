/**
 * VerifyEmailPage — auto-verifies the `?token=` query param on mount.
 *
 * When no token is present, falls back to a "check your email" state
 * (the user likely just registered). When the token is valid, transitions
 * to a success state with a CTA to continue. Errors surface inline.
 */

import { useEffect, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { apiClient } from '../../api/client';

type State = 'idle' | 'verifying' | 'verified' | 'error';

export function VerifyEmailPage(): JSX.Element {
  const [params] = useSearchParams();
  const token = params.get('token');
  const [state, setState] = useState<State>(token ? 'verifying' : 'idle');
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!token) return;

    let cancelled = false;
    async function doVerify(): Promise<void> {
      try {
        await apiClient.post('/api/v1/auth/verify-email', { token });
        if (!cancelled) setState('verified');
      } catch (err) {
        if (cancelled) return;
        setError(err instanceof Error ? err.message : 'Verification failed');
        setState('error');
      }
    }

    void doVerify();

    return () => {
      cancelled = true;
    };
  }, [token]);

  return (
    <div className="min-h-screen flex items-center justify-center bg-gray-50">
      <div className="w-full max-w-sm bg-white rounded-lg shadow-md p-8 text-center">
        <h1 className="text-2xl font-bold text-gray-900 mb-2">Tamma</h1>

        {state === 'idle' && (
          <>
            <p className="text-gray-700">Check your email</p>
            <p className="text-sm text-gray-500 mt-2">
              We&apos;ve sent you a verification link. Click it to activate your account.
            </p>
          </>
        )}

        {state === 'verifying' && (
          <p role="status" className="text-gray-500">
            Verifying...
          </p>
        )}

        {state === 'verified' && (
          <>
            <p className="text-gray-900 font-medium">Email verified</p>
            <p className="text-sm text-gray-500 mt-2 mb-4">
              Your account is ready to use.
            </p>
            <Link
              to="/"
              className="inline-block px-4 py-2 text-sm font-medium text-white bg-gray-900 rounded-md"
            >
              Continue
            </Link>
          </>
        )}

        {state === 'error' && (
          <div
            role="alert"
            className="p-3 text-sm text-red-700 bg-red-50 border border-red-200 rounded-md"
          >
            {error ?? 'Verification failed'}
          </div>
        )}
      </div>
    </div>
  );
}
