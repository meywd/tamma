/**
 * Backend → user-readable error-copy mapping (Story 18-8 brief
 * §error-copy-mapping). Single source of truth for the strings the
 * tenant-admin UI shows when the backend returns 4xx.
 *
 * Centralised so component tests can verify each mapping in isolation
 * and so future backend message changes only require one update here.
 */

const COPY: Record<string, string> = {
  'role must be one of: owner, admin, member': 'Select a valid role.',
  'Only owners can change owner-level roles':
    'Only the organization owner can promote or demote an owner.',
  'Cannot change role of users at or above your level':
    "You can't change the role of someone at your level or above.",
  'Cannot promote users to or above your level':
    "You can't promote someone to your own role.",
  'Cannot remove the last owner':
    'There must be at least one owner. Transfer ownership first.',
  'Cannot remove yourself as the last owner':
    'There must be at least one owner. Transfer ownership first.',
  'Cannot remove an owner': 'Admins cannot remove an owner.',
  'Cannot delete yourself':
    "You can't remove yourself; ask another admin.",
  'Invite has already been accepted':
    'This invite has already been accepted.',
  'Invite has expired':
    'This invite has expired. Send a new one.',
  'Not a member of this organization':
    "You don't have access to this organization.",
  'Requires admin role or higher':
    'You need admin or owner role to do that.',
  'Requires admin role or higher to invite':
    'You need admin or owner role to invite.',
  'Only the owner can transfer ownership':
    'Only the organization owner can transfer ownership.',
  rate_limited:
    'Too many requests. Try again in a few minutes.',
};

/**
 * Maps a backend error message (typically `{error: "..."}` payload) to a
 * UI-ready string. Falls through to the original message when no
 * mapping exists — keeps the UI useful for surfaces we haven't enumerated.
 */
export function mapOrgError(backendMessage: string | null | undefined): string {
  if (!backendMessage) return 'Something went wrong. Try again.';
  return COPY[backendMessage] ?? backendMessage;
}

/** HTTP-status-aware mapping for cases where the message alone is ambiguous. */
export function mapOrgHttpError(
  backendMessage: string | null | undefined,
  status: number | undefined,
): string {
  if (status === 429) return COPY['rate_limited'] ?? 'Too many requests.';
  if (status === 403 && !backendMessage) {
    return COPY['Not a member of this organization'] ?? 'Forbidden.';
  }
  return mapOrgError(backendMessage);
}
