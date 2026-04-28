import { describe, expect, it } from 'vitest';
import { mapOrgError, mapOrgHttpError } from './error-copy.js';

describe('mapOrgError', () => {
  it('maps every catalogued backend string', () => {
    const cases: [string, string][] = [
      ['role must be one of: owner, admin, member', 'Select a valid role.'],
      [
        'Only owners can change owner-level roles',
        'Only the organization owner can promote or demote an owner.',
      ],
      [
        'Cannot change role of users at or above your level',
        "You can't change the role of someone at your level or above.",
      ],
      [
        'Cannot promote users to or above your level',
        "You can't promote someone to your own role.",
      ],
      [
        'Cannot remove the last owner',
        'There must be at least one owner. Transfer ownership first.',
      ],
      [
        'Cannot remove an owner',
        'Admins cannot remove an owner.',
      ],
      [
        'Invite has already been accepted',
        'This invite has already been accepted.',
      ],
      [
        'Invite has expired',
        'This invite has expired. Send a new one.',
      ],
    ];
    for (const [backend, ui] of cases) {
      expect(mapOrgError(backend)).toBe(ui);
    }
  });

  it('falls through to the original message when uncatalogued', () => {
    expect(mapOrgError('something exotic the backend returned'))
      .toBe('something exotic the backend returned');
  });

  it('returns a generic fallback for null/undefined', () => {
    expect(mapOrgError(null)).toBe('Something went wrong. Try again.');
    expect(mapOrgError(undefined)).toBe('Something went wrong. Try again.');
    expect(mapOrgError('')).toBe('Something went wrong. Try again.');
  });
});

describe('mapOrgHttpError', () => {
  it('renders the rate-limited copy on 429 regardless of body', () => {
    expect(mapOrgHttpError('rate_limited', 429))
      .toBe('Too many requests. Try again in a few minutes.');
    // Even when backend forgot to send a body, 429 should still map.
    expect(mapOrgHttpError(null, 429))
      .toBe('Too many requests. Try again in a few minutes.');
  });

  it('renders forbidden copy on 403 with no body', () => {
    expect(mapOrgHttpError(null, 403))
      .toBe("You don't have access to this organization.");
  });

  it('passes through to mapOrgError for other statuses', () => {
    expect(mapOrgHttpError('Cannot remove the last owner', 400))
      .toBe('There must be at least one owner. Transfer ownership first.');
  });
});
