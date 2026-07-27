/**
 * ForgotPasswordPage — /forgot-password (Story 45-3 AC1).
 *
 * The "request" half of the password-reset flow (the email carries the
 * confirm half at /reset-password?token=). The success state is IDENTICAL
 * whether or not the address has an account — the server already returns an
 * indistinguishable 200 for both (AuthEndpoints.PasswordResetRequest), and
 * this page never branches on existence, so the form cannot be used as an
 * account-enumeration oracle (D4). The only distinct failures surfaced are
 * rate limiting (429) and transport errors.
 */

import { useState, type FormEvent, type JSX } from 'react';
import { Link } from 'react-router-dom';
import { requestPasswordReset } from '../../api/auth';
import { ApiError } from '../../api/client';

export function ForgotPasswordPage(): JSX.Element {
  const [email, setEmail] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [sent, setSent] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function handleSubmit(e: FormEvent): Promise<void> {
    e.preventDefault();
    setError(null);
    setSubmitting(true);
    try {
      await requestPasswordReset(email);
      setSent(true);
    } catch (err) {
      if (err instanceof ApiError && err.status === 429) {
        setError('Too many reset requests. Please try again later.');
      } else {
        // Generic — never anything that reveals whether the address exists.
        setError('Something went wrong. Please try again.');
      }
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <div className="min-h-screen flex items-center justify-center bg-gray-50">
      <div className="w-full max-w-sm bg-white rounded-lg shadow-md p-8">
        <h1 className="text-2xl font-bold text-gray-900 mb-1 text-center">Tamma</h1>
        <p className="text-sm text-gray-500 mb-6 text-center">Reset your password</p>

        {sent ? (
          <div className="text-center">
            <p role="status" className="text-gray-900 font-medium">
              Check your email
            </p>
            <p className="text-sm text-gray-500 mt-2">
              If that address has an account, we&apos;ve sent it a password reset link.
            </p>
            <Link
              to="/login"
              className="mt-4 inline-block text-sm text-gray-900 font-medium hover:underline"
            >
              Back to sign in
            </Link>
          </div>
        ) : (
          <>
            {error && (
              <div
                role="alert"
                className="mb-4 p-3 text-sm text-red-700 bg-red-50 border border-red-200 rounded-md"
              >
                {error}
              </div>
            )}

            <form onSubmit={handleSubmit} className="space-y-4">
              <div>
                <label htmlFor="email" className="block text-sm font-medium text-gray-700">
                  Email
                </label>
                <input
                  id="email"
                  type="email"
                  autoComplete="email"
                  required
                  value={email}
                  onChange={(e) => setEmail(e.target.value)}
                  className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 text-sm focus:border-gray-900 focus:outline-none"
                />
              </div>

              <button
                type="submit"
                disabled={submitting}
                className="w-full px-4 py-2.5 text-sm font-medium text-white bg-gray-900 hover:bg-gray-800 disabled:bg-gray-400 rounded-md"
              >
                {submitting ? 'Sending...' : 'Send reset link'}
              </button>
            </form>

            <p className="mt-6 text-sm text-center text-gray-500">
              Remembered it?{' '}
              <Link to="/login" className="text-gray-900 font-medium hover:underline">
                Sign in
              </Link>
            </p>
          </>
        )}
      </div>
    </div>
  );
}
