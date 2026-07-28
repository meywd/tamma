/**
 * Org-invite API (Story 45-3). Built on the shared ApiClient — never a bare
 * `fetch` (see 45-1).
 *
 * Server contract (read, not guessed — Endpoints/OrgEndpoints.cs AcceptInvite,
 * registered at Program.cs `orgs.MapPost("/invites/accept", …)` inside the
 * `/api/v1/orgs` group which carries `.RequireAuthorization("MemberAccess")`
 * — the caller MUST already be authenticated):
 *
 *   POST /api/v1/orgs/invites/accept   { token }
 *     - 401                                        anonymous caller
 *     - 400 { error: "Invalid or expired invite token" }   blank/unknown token
 *       (a revoked/deleted invite also lands here — the token hash no longer
 *        resolves, so "revoked" is NOT a distinguishable server outcome)
 *     - 400 { error: "Invite has already been accepted" }
 *     - 400 { error: "Invite has expired" }
 *     - 200 { tenantId, role, message: "You are already a member of this organization" }
 *       (idempotent re-accept)
 *     - 200 { tenantId, role, message: "You have joined the organization" }
 *
 * NOTE — the endpoint does NOT compare the invite's email to the caller's,
 * so "this invite was for a different account" is not a distinguishable
 * outcome either. The three 400 strings above are the complete failure
 * vocabulary; the UI renders exactly those three plus a generic fallback
 * (45-3 D6: render what the server distinguishes, do not fabricate branches).
 *
 * NOTE — there is NO invitee-facing invite-lookup or resend endpoint. The
 * tenant-scoped list/resend routes (Program.cs `/{tenantId}/invites…`) are
 * admin-gated and need a tenantId the `/invites/pending?inviteId=` email does
 * not carry — so InvitePendingPage is informational by design (45-3 D5).
 */

import { apiClient } from './client';

/** Mirrors the anonymous object AcceptInvite returns on 200. */
export interface AcceptInviteResponse {
  tenantId: string;
  role: string;
  message: string;
}

export async function acceptInvite(token: string): Promise<AcceptInviteResponse> {
  return apiClient.post<AcceptInviteResponse>('/api/v1/orgs/invites/accept', { token });
}
