/** Sentinel UUID for the default tenant used in CLI/self-hosted mode. */
export const DEFAULT_TENANT_ID = '00000000-0000-0000-0000-000000000000';

/** Supported billing plans. */
export type TenantPlan = 'free' | 'pro' | 'enterprise';

/** Represents a tenant (organization/user) in the Tamma SaaS platform. */
export interface Tenant {
  id: string;
  name: string;
  slug: string;
  externalId: string | null;
  plan: TenantPlan;
  settings: Record<string, unknown>;
  createdAt: string;
  updatedAt: string;
  deletedAt: string | null;
}
