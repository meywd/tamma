/**
 * Audit log summary generation (Story 18-8 brief §audit-log-summary-generation).
 *
 * Maps `event.type` + `event.data` to a human-friendly summary string for
 * the tenant-admin Audit tab. Pure function — testable without React.
 *
 * Unknown event types fall through to the raw type string so adding new
 * event types in the backend never breaks the UI.
 */

export interface AuditEventSummaryInput {
  type: string;
  /** Parsed `event.data` JSON object (caller `JSON.parse`s once). */
  data: Record<string, unknown>;
  /** Optional already-resolved actor display name; when absent we fall
   *  through to `tags.userId` (the caller resolves that one-hop and
   *  passes it in). */
  actor?: string | null;
}

export interface AuditEventSummary {
  summary: string;
  /** Lucide-style icon hint (rendered as text or unicode glyph for now). */
  icon: 'plus' | 'user-plus' | 'user-check' | 'user-cog' | 'user-minus' | 'send' | 'crown' | 'trash' | 'flame' | 'info';
}

function asString(value: unknown, fallback = ''): string {
  return typeof value === 'string' ? value : fallback;
}

function actorOrFallback(actor?: string | null): string {
  return actor && actor.length > 0 ? actor : 'Someone';
}

export function eventToSummary(input: AuditEventSummaryInput): AuditEventSummary {
  const { type, data } = input;
  const actor = actorOrFallback(input.actor);

  switch (type) {
    case 'TENANT.CREATED.SUCCESS':
      return {
        summary: `${actor} created the organization.`,
        icon: 'plus',
      };

    case 'TENANT.MEMBER_INVITED.SUCCESS':
      return {
        summary: `${actor} invited ${asString(data['email'], 'a user')} as ${asString(data['role'], 'member')}.`,
        icon: 'user-plus',
      };

    case 'TENANT.MEMBER_INVITE_RESENT.SUCCESS':
      return {
        summary: `${actor} resent the invite to ${asString(data['email'], 'a pending invitee')}.`,
        icon: 'send',
      };

    case 'TENANT.MEMBER_JOINED.SUCCESS':
      return {
        summary: `${actor} accepted the invite as ${asString(data['role'], 'member')}.`,
        icon: 'user-check',
      };

    case 'TENANT.MEMBER_ROLE_CHANGED.SUCCESS':
      return {
        summary: `${actor} changed ${asString(data['targetUserId'], 'a user')}'s role from ${asString(data['oldRole'], '?')} to ${asString(data['newRole'], '?')}.`,
        icon: 'user-cog',
      };

    case 'TENANT.MEMBER_REMOVED.SUCCESS':
      return {
        summary: `${actor} removed ${asString(data['removedUserId'], 'a member')}.`,
        icon: 'user-minus',
      };

    case 'TENANT.OWNERSHIP_TRANSFERRED.SUCCESS':
      return {
        summary: `${actor} transferred ownership to ${asString(data['newOwnerId'], 'another user')}.`,
        icon: 'crown',
      };

    case 'TENANT.DELETED.SUCCESS':
      return {
        summary: `${actor} soft-deleted the organization.`,
        icon: 'trash',
      };

    case 'TENANT.PURGED.SUCCESS':
      return {
        summary: `${actor} permanently deleted the organization.`,
        icon: 'flame',
      };

    default:
      // Unknown type — render the raw type so new event types stay
      // safe and visible.
      return {
        summary: type,
        icon: 'info',
      };
  }
}

/**
 * Tenant audit event-type families used by the chip-group filter on the
 * Audit tab. Each value is the prefix sent to
 * `GET /api/v1/orgs/:tenantId/audit?type=`.
 */
export const AUDIT_EVENT_FAMILIES = [
  { label: 'All', prefix: '' },
  { label: 'Invites', prefix: 'TENANT.MEMBER_INVITED' },
  { label: 'Resends', prefix: 'TENANT.MEMBER_INVITE_RESENT' },
  { label: 'Joins', prefix: 'TENANT.MEMBER_JOINED' },
  { label: 'Role changes', prefix: 'TENANT.MEMBER_ROLE_CHANGED' },
  { label: 'Removals', prefix: 'TENANT.MEMBER_REMOVED' },
  { label: 'Ownership', prefix: 'TENANT.OWNERSHIP_TRANSFERRED' },
  { label: 'Org lifecycle', prefix: 'TENANT.CREATED' }, // sloppy — clicked together with delete/purge below
] as const;
