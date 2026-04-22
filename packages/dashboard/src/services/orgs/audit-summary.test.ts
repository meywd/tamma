import { describe, expect, it } from 'vitest';
import { eventToSummary } from './audit-summary.js';

describe('eventToSummary', () => {
  it('renders TENANT.CREATED with actor name', () => {
    const result = eventToSummary({
      type: 'TENANT.CREATED.SUCCESS',
      data: { slug: 'acme', name: 'Acme' },
      actor: 'Alice',
    });
    expect(result.summary).toBe('Alice created the organization.');
    expect(result.icon).toBe('plus');
  });

  it('renders TENANT.MEMBER_INVITED with email + role', () => {
    const result = eventToSummary({
      type: 'TENANT.MEMBER_INVITED.SUCCESS',
      data: { email: 'bob@example.com', role: 'admin' },
      actor: 'Alice',
    });
    expect(result.summary).toBe('Alice invited bob@example.com as admin.');
    expect(result.icon).toBe('user-plus');
  });

  it('renders TENANT.MEMBER_INVITE_RESENT with email', () => {
    const result = eventToSummary({
      type: 'TENANT.MEMBER_INVITE_RESENT.SUCCESS',
      data: { email: 'bob@example.com' },
      actor: 'Alice',
    });
    expect(result.summary).toBe('Alice resent the invite to bob@example.com.');
    expect(result.icon).toBe('send');
  });

  it('renders TENANT.MEMBER_JOINED with role', () => {
    const result = eventToSummary({
      type: 'TENANT.MEMBER_JOINED.SUCCESS',
      data: { role: 'member' },
      actor: 'Bob',
    });
    expect(result.summary).toBe('Bob accepted the invite as member.');
    expect(result.icon).toBe('user-check');
  });

  it('renders TENANT.MEMBER_ROLE_CHANGED with old + new', () => {
    const result = eventToSummary({
      type: 'TENANT.MEMBER_ROLE_CHANGED.SUCCESS',
      data: { targetUserId: 'bob-id', oldRole: 'member', newRole: 'admin' },
      actor: 'Alice',
    });
    expect(result.summary).toBe(
      "Alice changed bob-id's role from member to admin.",
    );
    expect(result.icon).toBe('user-cog');
  });

  it('renders TENANT.MEMBER_REMOVED with target id', () => {
    const result = eventToSummary({
      type: 'TENANT.MEMBER_REMOVED.SUCCESS',
      data: { removedUserId: 'bob-id', removedRole: 'member' },
      actor: 'Alice',
    });
    expect(result.summary).toBe('Alice removed bob-id.');
    expect(result.icon).toBe('user-minus');
  });

  it('renders TENANT.OWNERSHIP_TRANSFERRED', () => {
    const result = eventToSummary({
      type: 'TENANT.OWNERSHIP_TRANSFERRED.SUCCESS',
      data: { previousOwnerId: 'alice-id', newOwnerId: 'bob-id' },
      actor: 'Alice',
    });
    expect(result.summary).toBe('Alice transferred ownership to bob-id.');
    expect(result.icon).toBe('crown');
  });

  it('renders TENANT.DELETED', () => {
    const result = eventToSummary({
      type: 'TENANT.DELETED.SUCCESS',
      data: { phase: 'soft-delete' },
      actor: 'Alice',
    });
    expect(result.summary).toBe('Alice soft-deleted the organization.');
    expect(result.icon).toBe('trash');
  });

  it('renders TENANT.PURGED', () => {
    const result = eventToSummary({
      type: 'TENANT.PURGED.SUCCESS',
      data: { phase: 'hard-delete' },
      actor: 'Alice',
    });
    expect(result.summary).toBe('Alice permanently deleted the organization.');
    expect(result.icon).toBe('flame');
  });

  it('falls back to "Someone" when actor is missing', () => {
    const result = eventToSummary({
      type: 'TENANT.MEMBER_INVITED.SUCCESS',
      data: { email: 'bob@example.com', role: 'member' },
      actor: null,
    });
    expect(result.summary).toBe('Someone invited bob@example.com as member.');
  });

  it('falls back to "Someone" when actor is empty string', () => {
    const result = eventToSummary({
      type: 'TENANT.CREATED.SUCCESS',
      data: { slug: 'acme', name: 'Acme' },
      actor: '',
    });
    expect(result.summary).toBe('Someone created the organization.');
  });

  it('renders unknown types verbatim with info icon', () => {
    const result = eventToSummary({
      type: 'TENANT.SOME_NEW_EVENT.SUCCESS',
      data: {},
      actor: 'Alice',
    });
    expect(result.summary).toBe('TENANT.SOME_NEW_EVENT.SUCCESS');
    expect(result.icon).toBe('info');
  });

  it('handles missing payload fields gracefully', () => {
    // No data fields at all — must not throw and should fall back.
    const result = eventToSummary({
      type: 'TENANT.MEMBER_INVITED.SUCCESS',
      data: {},
      actor: 'Alice',
    });
    expect(result.summary).toBe('Alice invited a user as member.');
  });

  it('handles non-string data fields gracefully', () => {
    const result = eventToSummary({
      type: 'TENANT.MEMBER_INVITED.SUCCESS',
      data: { email: 42, role: null },
      actor: 'Alice',
    });
    // Both non-string fields fall back to literal placeholder defaults.
    expect(result.summary).toBe('Alice invited a user as member.');
  });
});
