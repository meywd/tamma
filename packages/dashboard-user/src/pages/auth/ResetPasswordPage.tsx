/**
 * ResetPasswordPage — /reset-password?token= (Story 45-3 AC3/AC4).
 *
 * The confirm half of the password-reset flow; the API emails
 * `{customer-base}/reset-password?token=` (AuthEndpoints.BuildResetUrl).
 * Follows VerifyEmailPage's shape: token from the query, POST it in the
 * body, distinct rendered states.
 *
 * States: missing token / form (with client pre-flight) / invalid-or-expired
 * token / weak password (server `details` surfaced) / success → /login.
 *
 * Password rules are MIRRORED from the server's PasswordStrengthValidator as
 * a pre-flight only (api/auth.ts passwordPreflightErrors) — the server stays
 * authoritative and its "Password too weak" details are rendered verbatim
 * when the two ever disagree (e.g. the common-password list, which the
 * client does not mirror).
 */

import { useState, type FormEvent, type JSX } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { confirmPasswordReset, passwordPreflightErrors } from '../../api/auth';
import { ApiError } from '../../api/client';

type State = 'form' | 'success' | 'invalid-token';

export function ResetPasswordPage(): JSX.Element {
  const [params] = useSearchParams();
  const token = params.get('token');

  const [state, setState] = useState<State>('form');
  const [password, setPassword] = useState('');
  const [confirm, setConfirm] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [errors, setErrors] = useState<string[]>([]);

  // Missing token — the page was opened without the email's link. No POST is
  // ever issued from this state.
  if (!token) {
    return (
      <Shell>
        <p className="text-gray-900 font-medium">This link is incomplete</p>
        <p className="text-sm text-gray-500 mt-2">
          The reset link is missing its token. Open the link from the password reset email, or
          request a new one.
        </p>
        <Link
          to="/forgot-password"
          className="mt-4 inline-block text-sm text-gray-900 font-medium hover:underline"
        >
          Request a new reset link
        </Link>
      </Shell>
    );
  }

  async function handleSubmit(e: FormEvent): Promise<void> {
    e.preventDefault();
    setErrors([]);

    // Client pre-flight (UX speedup; server authoritative — see header).
    const preflight = passwordPreflightErrors(password);
    if (preflight.length > 0) {
      setErrors(preflight);
      return;
    }
    if (password !== confirm) {
      setErrors(['Passwords do not match']);
      return;
    }

    setSubmitting(true);
    try {
      await confirmPasswordReset(token as string, password);
      setState('success');
    } catch (err) {
      if (err instanceof ApiError) {
        const body = err.body as { error?: string; details?: string[] } | null;
        if (body?.error === 'Invalid or expired reset token') {
          setState('invalid-token');
        } else if (body?.details && body.details.length > 0) {
          // "Password too weak" — surface the server's own rule list.
          setErrors(body.details);
        } else {
          setErrors([body?.error ?? 'Password reset failed. Please try again.']);
        }
      } else {
        setErrors(['Password reset failed. Please try again.']);
      }
    } finally {
      setSubmitting(false);
    }
  }

  if (state === 'success') {
    return (
      <Shell>
        <p role="status" className="text-gray-900 font-medium">
          Password reset successfully
        </p>
        <p className="text-sm text-gray-500 mt-2">You can sign in with your new password now.</p>
        <Link
          to="/login"
          className="mt-4 inline-block px-4 py-2 text-sm font-medium text-white bg-gray-900 rounded-md"
        >
          Sign in
        </Link>
      </Shell>
    );
  }

  if (state === 'invalid-token') {
    return (
      <Shell>
        <div role="alert">
          <p className="text-gray-900 font-medium">This reset link is invalid or has expired</p>
          <p className="text-sm text-gray-500 mt-2">
            Reset links can only be used once and expire after an hour. Request a new one to
            continue.
          </p>
        </div>
        <Link
          to="/forgot-password"
          className="mt-4 inline-block text-sm text-gray-900 font-medium hover:underline"
        >
          Request a new reset link
        </Link>
      </Shell>
    );
  }

  return (
    <div className="min-h-screen flex items-center justify-center bg-gray-50">
      <div className="w-full max-w-sm bg-white rounded-lg shadow-md p-8">
        <h1 className="text-2xl font-bold text-gray-900 mb-1 text-center">Tamma</h1>
        <p className="text-sm text-gray-500 mb-6 text-center">Choose a new password</p>

        {errors.length > 0 && (
          <div
            role="alert"
            className="mb-4 p-3 text-sm text-red-700 bg-red-50 border border-red-200 rounded-md"
          >
            <ul className={errors.length > 1 ? 'list-disc pl-4 space-y-1' : ''}>
              {errors.map((e) => (
                <li key={e}>{e}</li>
              ))}
            </ul>
          </div>
        )}

        <form onSubmit={handleSubmit} className="space-y-4">
          <div>
            <label htmlFor="new-password" className="block text-sm font-medium text-gray-700">
              New password
            </label>
            <input
              id="new-password"
              type="password"
              autoComplete="new-password"
              required
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 text-sm focus:border-gray-900 focus:outline-none"
            />
            <p className="mt-1 text-xs text-gray-400">
              At least 8 characters, with an uppercase letter, a lowercase letter and a digit.
            </p>
          </div>

          <div>
            <label htmlFor="confirm-password" className="block text-sm font-medium text-gray-700">
              Confirm new password
            </label>
            <input
              id="confirm-password"
              type="password"
              autoComplete="new-password"
              required
              value={confirm}
              onChange={(e) => setConfirm(e.target.value)}
              className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 text-sm focus:border-gray-900 focus:outline-none"
            />
          </div>

          <button
            type="submit"
            disabled={submitting}
            className="w-full px-4 py-2.5 text-sm font-medium text-white bg-gray-900 hover:bg-gray-800 disabled:bg-gray-400 rounded-md"
          >
            {submitting ? 'Resetting...' : 'Reset password'}
          </button>
        </form>
      </div>
    </div>
  );
}

function Shell({ children }: { children: React.ReactNode }): JSX.Element {
  return (
    <div className="min-h-screen flex items-center justify-center bg-gray-50">
      <div className="w-full max-w-sm bg-white rounded-lg shadow-md p-8 text-center">
        <h1 className="text-2xl font-bold text-gray-900 mb-2">Tamma</h1>
        {children}
      </div>
    </div>
  );
}
