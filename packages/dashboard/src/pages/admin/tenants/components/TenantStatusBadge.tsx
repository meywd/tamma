import { Badge } from '../../../../components/common/Badge.js';

import type { JSX } from "react";

/**
 * Story 28-11 — renders a color-coded status chip for a tenant's lifecycle
 * status. Maps the six canonical states from Doc 01 §7.2 to the shared
 * Badge component's variant vocabulary.
 *
 * Null / legacy-row rendering: tenants created before the Epic-28 shadow
 * columns show as "active" because they predate the state machine.
 */

export type TenantStatusVariant =
  | 'active'
  | 'provisioning'
  | 'pending_verification'
  | 'failed'
  | 'deleting'
  | 'deleted'
  | 'unknown';

const STATUS_LABEL: Record<TenantStatusVariant, string> = {
  active: 'Active',
  provisioning: 'Provisioning',
  pending_verification: 'Pending verification',
  failed: 'Failed',
  deleting: 'Deleting',
  deleted: 'Deleted',
  unknown: 'Unknown',
};

const STATUS_VARIANT: Record<TenantStatusVariant, 'healthy' | 'warning' | 'error' | 'info' | 'neutral'> = {
  active: 'healthy',
  provisioning: 'info',
  pending_verification: 'info',
  failed: 'error',
  deleting: 'warning',
  deleted: 'neutral',
  unknown: 'neutral',
};

function toVariant(status: string | null): TenantStatusVariant {
  if (!status) return 'active';
  const s = status.toLowerCase();
  if (s === 'active' || s === 'provisioning' || s === 'pending_verification'
      || s === 'failed' || s === 'deleting' || s === 'deleted') {
    return s as TenantStatusVariant;
  }
  return 'unknown';
}

interface TenantStatusBadgeProps {
  status: string | null;
}

export function TenantStatusBadge({ status }: TenantStatusBadgeProps): JSX.Element {
  const variant = toVariant(status);
  return <Badge variant={STATUS_VARIANT[variant]}>{STATUS_LABEL[variant]}</Badge>;
}
