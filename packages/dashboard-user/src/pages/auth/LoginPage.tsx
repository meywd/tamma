/**
 * LoginPage — email + password form + "Sign in with GitHub" link.
 *
 * On success, redirects to `?redirect=` if present, else to `/`. Error
 * responses surface as an inline alert.
 */

import { useState, type FormEvent } from 'react';
import { useNavigate, useSearchParams, Link } from 'react-router-dom';
import { useAuth } from '../../hooks/useAuth';

export function LoginPage(): JSX.Element {
  const { login } = useAuth();
  const navigate = useNavigate();
  const [params] = useSearchParams();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [submitting, setSubmitting] = useState(false);

  async function handleSubmit(e: FormEvent): Promise<void> {
    e.preventDefault();
    setError(null);
    setSubmitting(true);
    try {
      await login(email, password);
      const redirect = params.get('redirect') || '/';
      navigate(redirect, { replace: true });
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Sign-in failed');
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <div className="min-h-screen flex items-center justify-center bg-gray-50">
      <div className="w-full max-w-sm bg-white rounded-lg shadow-md p-8">
        <h1 className="text-2xl font-bold text-gray-900 mb-1 text-center">Tamma</h1>
        <p className="text-sm text-gray-500 mb-6 text-center">Sign in to your account</p>

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

          <div>
            <label htmlFor="password" className="block text-sm font-medium text-gray-700">
              Password
            </label>
            <input
              id="password"
              type="password"
              autoComplete="current-password"
              required
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              className="mt-1 block w-full rounded-md border border-gray-300 px-3 py-2 text-sm focus:border-gray-900 focus:outline-none"
            />
          </div>

          <button
            type="submit"
            disabled={submitting}
            className="w-full px-4 py-2.5 text-sm font-medium text-white bg-gray-900 hover:bg-gray-800 disabled:bg-gray-400 rounded-md"
          >
            {submitting ? 'Signing in...' : 'Sign in'}
          </button>
        </form>

        <div className="my-4 flex items-center gap-2 text-xs text-gray-400">
          <span className="flex-1 h-px bg-gray-200" />
          <span>or</span>
          <span className="flex-1 h-px bg-gray-200" />
        </div>

        <a
          href="/api/auth/github"
          className="inline-flex items-center justify-center gap-2 w-full px-4 py-2.5 text-sm font-medium text-white bg-gray-800 hover:bg-gray-700 rounded-md"
        >
          Sign in with GitHub
        </a>

        <p className="mt-6 text-sm text-center text-gray-500">
          New to Tamma?{' '}
          <Link to="/register" className="text-gray-900 font-medium hover:underline">
            Create an account
          </Link>
        </p>
      </div>
    </div>
  );
}
