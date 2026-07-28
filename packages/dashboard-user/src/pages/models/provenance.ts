/**
 * Story 46-3 D3 — the ONE provenance text mapping for the tenant surface.
 *
 * The server reports four sources (tenant-override | platform-db | config |
 * descriptor — InlineToolLoopRunner.ResolveDefaultModel), but tenants see
 * exactly TWO states: "Your override" and "Platform default". Exposing the
 * platform's config/descriptor internals to customers is noise and leaks
 * deployment detail. This exported const is the sole mapping (mirrors 46-2's
 * D4 discipline in the admin app — packages/dashboard's provider settings
 * page keeps its own, deliberately unshared, per epic-45 sanctioned
 * divergence).
 */

export const TENANT_OVERRIDE_SOURCE = 'tenant-override';

export const TENANT_PROVENANCE_LABELS = {
  'tenant-override': 'Your override',
  'platform-default': 'Platform default',
} as const;

/** Every source except 'tenant-override' renders as the platform default. */
export function provenanceLabel(source: string): string {
  return source === TENANT_OVERRIDE_SOURCE
    ? TENANT_PROVENANCE_LABELS['tenant-override']
    : TENANT_PROVENANCE_LABELS['platform-default'];
}
