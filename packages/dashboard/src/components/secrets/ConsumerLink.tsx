import type { ConsumerRef } from '../../services/secrets/secrets-api-client.js';

/**
 * Story 29-4 / 29-5 — typed consumer renderer. The `ConsumerRef.type`
 * drives what link / iconography the UI shows:
 *
 *  - `postgres` → "Postgres role=<target>" + link to the RLS runbook.
 *  - `cranl` → "Cranl app=<target>" + link to the tenant page.
 *  - `github_webhook` → "GitHub installation=<target>".
 *  - `hmac_shared` → "HMAC shared with <target>".
 *  - `tamma_engine` → "Engine request-signing" w/ link to engine health.
 *  - `generic` → plain text fallback.
 *
 * Design constraint: no strings are hand-typed per secret — the
 * (type, target) tuple drives the label via the lookup table here.
 * This matches Story 29-4 AC6 and Story 29-1's
 * `ConsumerRefLookup.Describe`.
 */
export interface ConsumerLinkProps {
  readonly consumer: ConsumerRef;
  /** Optional tenant id — drives the cranl link target. */
  readonly tenantId?: string | null;
}

export function ConsumerLink({ consumer, tenantId }: ConsumerLinkProps): JSX.Element {
  switch (consumer.type) {
    case 'postgres':
      return (
        <span className="inline-flex items-center gap-1 text-sm">
          <span className="text-gray-700">
            Postgres role <code className="font-mono">{consumer.target}</code>
          </span>
          <a
            href="/admin/runtime/dbcontexts"
            className="text-blue-600 hover:underline text-xs"
          >
            (RLS runbook)
          </a>
        </span>
      );

    case 'cranl':
      return (
        <span className="inline-flex items-center gap-1 text-sm">
          <span className="text-gray-700">
            Cranl app <code className="font-mono">{consumer.target}</code>
          </span>
          {tenantId ? (
            <a
              href={`/admin/tenants/${tenantId}`}
              className="text-blue-600 hover:underline text-xs"
            >
              (tenant page)
            </a>
          ) : null}
        </span>
      );

    case 'github_webhook':
      return (
        <span className="text-sm text-gray-700">
          GitHub installation{' '}
          <code className="font-mono">{consumer.target}</code>
        </span>
      );

    case 'hmac_shared':
      return (
        <span className="text-sm text-gray-700">
          HMAC shared with <code className="font-mono">{consumer.target}</code>
        </span>
      );

    case 'tamma_engine':
      return (
        <span className="inline-flex items-center gap-1 text-sm">
          <span className="text-gray-700">
            Engine <code className="font-mono">{consumer.target}</code>
          </span>
          <a href="/admin" className="text-blue-600 hover:underline text-xs">
            (runtime health)
          </a>
        </span>
      );

    default:
      return (
        <span className="text-sm text-gray-700">
          <code className="font-mono">{consumer.label ?? consumer.target}</code>
        </span>
      );
  }
}
