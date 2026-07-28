/**
 * InvitePendingPage — /invites/pending?inviteId= (Story 45-3 AC7).
 *
 * The API emails `{customer-base}/invites/pending?inviteId=` to the INVITEE
 * when a pending invite is RESENT (OrgEndpoints.ResendInvite). The resend
 * deliberately does not mint a new token — only a hash of the original token
 * is stored, so the raw token cannot be recovered — which means this page
 * structurally CANNOT accept the invite, and says so plainly (45-3 D5).
 *
 * DEVIATION FROM THE STORY, per the server as it exists today:
 *   - The story imagined this URL going to the inviter as a status view. The
 *     resend email is actually sent to the invitee (invite.Email), so the
 *     copy addresses the invitee.
 *   - There is no invite-lookup endpoint reachable with only an inviteId
 *     (the tenant-scoped list/resend routes are admin-gated and need a
 *     tenantId this URL does not carry), so no status can be fetched and no
 *     resend button can work from here. The page is informational: use the
 *     ORIGINAL invitation email's accept link, or ask the inviter to resend.
 */

import type { JSX } from 'react';
import { Link, useSearchParams } from 'react-router-dom';

export function InvitePendingPage(): JSX.Element {
  const [params] = useSearchParams();
  const inviteId = params.get('inviteId');

  return (
    <div className="min-h-screen flex items-center justify-center bg-gray-50">
      <div className="w-full max-w-sm bg-white rounded-lg shadow-md p-8 text-center">
        <h1 className="text-2xl font-bold text-gray-900 mb-2">Tamma</h1>
        <p className="text-gray-900 font-medium">You have a pending invitation</p>
        {inviteId && (
          <p className="mt-1 text-xs text-gray-400 break-all">
            Invite reference: <span className="font-mono">{inviteId}</span>
          </p>
        )}
        <p className="text-sm text-gray-500 mt-3">
          This page cannot accept the invitation — accepting requires the secure link from the
          original invitation email. Open that email and use its accept link to join.
        </p>
        <p className="text-sm text-gray-500 mt-2">
          Can&apos;t find it, or has it expired? Ask the person who invited you to resend the
          invitation.
        </p>
        <div className="mt-4 flex justify-center gap-3">
          <Link
            to="/login"
            className="px-4 py-2 text-sm font-medium text-white bg-gray-900 rounded-md"
          >
            Sign in
          </Link>
          <Link
            to="/"
            className="px-4 py-2 text-sm font-medium text-gray-700 bg-gray-100 hover:bg-gray-200 rounded-md"
          >
            Go to dashboard
          </Link>
        </div>
      </div>
    </div>
  );
}
