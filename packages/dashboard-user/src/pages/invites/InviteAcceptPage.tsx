/**
 * InviteAcceptPage — /invites/accept?token= (Story 45-3 AC5/AC6).
 *
 * The API emails `{customer-base}/invites/accept?token=` to the invitee
 * (OrgEndpoints.CreateInvite). POST /api/v1/orgs/invites/accept is gated
 * MemberAccess — the caller must be signed in — but an invitee may have no
 * account, so this route is deliberately OUTSIDE AuthGuard and branches on
 * auth state itself.
 *
 * TOKEN-ACROSS-THE-AUTH-BOUNDARY MECHANISM (AC5 — stated per the story):
 *   - Sign-in: the token rides the URL, not storage. The "Sign in" link is
 *     `/login?redirect=<encoded /invites/accept?token=…>`, reusing the exact
 *     `?redirect=` round-trip AuthGuard/LoginPage already ship and test. The
 *     token's lifetime stays equal to the navigation's; nothing lingers in
 *     browser storage after the flow. (45-3 D2)
 *   - Registration: after registering, the user must VERIFY their email
 *     before /api/auth/me returns a user, and verification arrives as a new
 *     link in a new email — a navigation this page cannot thread a query
 *     through (it may even open on a different device). So the page does NOT
 *     stash the token; it tells the user, in explicit copy, to click the
 *     invite link in their email again once verified. The invite token stays
 *     valid until its expiry regardless. (45-3 D3, option b)
 *
 * Failure vocabulary is the server's real one (see api/invites.ts): three
 * distinguishable 400 strings. "Wrong account" and "revoked" are NOT
 * distinguishable server-side and are deliberately not faked here (D6).
 */

import { useEffect, useRef, useState, type JSX } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router-dom';
import { useAuth } from '../../hooks/useAuth';
import { acceptInvite } from '../../api/invites';
import { ApiError } from '../../api/client';

type AcceptState =
  | 'idle'
  | 'accepting'
  | 'expired'
  | 'already-accepted'
  | 'invalid'
  | 'error';

export function InviteAcceptPage(): JSX.Element {
  const [params] = useSearchParams();
  const token = params.get('token');
  const { user, loading } = useAuth();
  const navigate = useNavigate();

  const [state, setState] = useState<AcceptState>('idle');
  const startedRef = useRef(false);

  useEffect(() => {
    // Auto-accept exactly once, only when signed in and a token is present.
    if (loading || user === null || !token || startedRef.current) return;
    startedRef.current = true;

    let cancelled = false;
    async function doAccept(): Promise<void> {
      setState('accepting');
      try {
        await acceptInvite(token as string);
        // Success (including the idempotent already-a-member 200): land on
        // the dashboard — the membership is live and the active tenant is
        // set server-side when the user had none.
        if (!cancelled) navigate('/', { replace: true });
      } catch (err) {
        if (cancelled) return;
        if (err instanceof ApiError) {
          const body = err.body as { error?: string } | null;
          switch (body?.error) {
            case 'Invite has expired':
              setState('expired');
              return;
            case 'Invite has already been accepted':
              setState('already-accepted');
              return;
            case 'Invalid or expired invite token':
              setState('invalid');
              return;
            default:
              setState('error');
              return;
          }
        }
        setState('error');
      }
    }
    void doAccept();

    return () => {
      cancelled = true;
    };
  }, [loading, user, token, navigate]);

  // ── No token: the link is broken/incomplete ──
  if (!token) {
    return (
      <Shell>
        <p className="text-gray-900 font-medium">This invite link is incomplete</p>
        <p className="text-sm text-gray-500 mt-2">
          The link is missing its invite token. Open the link from the invitation email; if it
          keeps failing, ask the person who invited you to resend the invite.
        </p>
        <Link
          to="/login"
          className="mt-4 inline-block text-sm text-gray-900 font-medium hover:underline"
        >
          Go to sign in
        </Link>
      </Shell>
    );
  }

  if (loading) {
    return (
      <Shell>
        <p role="status" className="text-gray-500">
          Loading...
        </p>
      </Shell>
    );
  }

  // ── Anonymous: offer sign-in / registration, both preserving the flow ──
  if (user === null) {
    const redirect = encodeURIComponent(`/invites/accept?token=${token}`);
    return (
      <Shell>
        <p className="text-gray-900 font-medium">You&apos;ve been invited to an organization</p>
        <p className="text-sm text-gray-500 mt-2">
          Sign in to accept the invitation, or create an account first.
        </p>
        <div className="mt-4 space-y-2">
          <Link
            to={`/login?redirect=${redirect}`}
            className="block w-full px-4 py-2.5 text-sm font-medium text-white bg-gray-900 hover:bg-gray-800 rounded-md"
          >
            Sign in
          </Link>
          <Link
            to="/register"
            className="block w-full px-4 py-2.5 text-sm font-medium text-gray-700 bg-gray-100 hover:bg-gray-200 rounded-md"
          >
            Create an account
          </Link>
        </div>
        <p className="mt-4 text-xs text-gray-500">
          Creating an account? You&apos;ll need to verify your email first — after verifying,
          click the invite link in your email again to join the organization.
        </p>
      </Shell>
    );
  }

  // ── Authenticated: accept is in flight or has failed ──
  switch (state) {
    case 'idle':
    case 'accepting':
      return (
        <Shell>
          <p role="status" className="text-gray-500">
            Accepting invitation...
          </p>
        </Shell>
      );
    case 'expired':
      return (
        <Shell>
          <div role="alert">
            <p className="text-gray-900 font-medium">This invitation has expired</p>
            <p className="text-sm text-gray-500 mt-2">
              Invitations are valid for 72 hours. Ask the person who invited you to send a new
              one.
            </p>
          </div>
          <HomeLink />
        </Shell>
      );
    case 'already-accepted':
      return (
        <Shell>
          <div role="alert">
            <p className="text-gray-900 font-medium">This invitation was already accepted</p>
            <p className="text-sm text-gray-500 mt-2">
              If that was you, you&apos;re already a member — head to the dashboard. Otherwise ask
              the person who invited you to send a fresh invite.
            </p>
          </div>
          <HomeLink />
        </Shell>
      );
    case 'invalid':
      return (
        <Shell>
          <div role="alert">
            <p className="text-gray-900 font-medium">This invite link is not valid</p>
            <p className="text-sm text-gray-500 mt-2">
              The invite may have been revoked or the link is damaged. Ask the person who invited
              you to resend it.
            </p>
          </div>
          <HomeLink />
        </Shell>
      );
    default:
      return (
        <Shell>
          <div role="alert">
            <p className="text-gray-900 font-medium">Could not accept the invitation</p>
            <p className="text-sm text-gray-500 mt-2">
              Something went wrong. Try opening the invite link again; if it keeps failing,
              contact support.
            </p>
          </div>
          <HomeLink />
        </Shell>
      );
  }
}

function HomeLink(): JSX.Element {
  return (
    <Link
      to="/"
      className="mt-4 inline-block px-4 py-2 text-sm font-medium text-white bg-gray-900 rounded-md"
    >
      Go to dashboard
    </Link>
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
