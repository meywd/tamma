/**
 * Audit Log Tab
 *
 * Feature-flagged behind VITE_FEATURE_ADMIN_AUDIT_LOG.
 * When disabled: shows a "Coming soon" placeholder.
 * When enabled: shows paginated audit events with filters.
 */

import { Card } from '../../components/common/Card.js';

import type { JSX } from "react";

const FEATURE_ENABLED = import.meta.env.VITE_FEATURE_ADMIN_AUDIT_LOG === 'true';

function ComingSoonPlaceholder(): JSX.Element {
  return (
    <div>
      <h2 className="text-lg font-semibold text-gray-900 mb-4 dark:text-gray-100">Audit Log</h2>
      <Card>
        <div className="text-center py-12">
          <div className="text-4xl mb-4 text-gray-300" aria-hidden="true">
            &#128220;
          </div>
          <h3 className="text-lg font-medium text-gray-900 mb-2 dark:text-gray-100">Coming Soon</h3>
          <p className="text-sm text-gray-500 max-w-md mx-auto dark:text-gray-400">
            The audit log viewer will provide paginated access to all admin events
            with filters for event type, scope, and date range. The backing API is
            under development.
          </p>
        </div>
      </Card>
    </div>
  );
}

function AuditLogViewer(): JSX.Element {
  // Full implementation will be wired when /api/admin/audit-log lands
  return (
    <div>
      <h2 className="text-lg font-semibold text-gray-900 mb-4 dark:text-gray-100">Audit Log</h2>
      <Card>
        <p className="text-sm text-gray-500 dark:text-gray-400">
          Audit log viewer — API integration pending.
        </p>
      </Card>
    </div>
  );
}

export function AuditLogTab(): JSX.Element {
  if (!FEATURE_ENABLED) {
    return <ComingSoonPlaceholder />;
  }

  return <AuditLogViewer />;
}
