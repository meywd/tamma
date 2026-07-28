/**
 * ResetConfirm (Story 46-2)
 *
 * Inline confirm step for resetting a provider's platform settings row
 * (DELETE /api/admin/providers/:key/settings). The copy names the fallback
 * tiers using the SOURCE_BADGE_LABELS map — the page's one permitted text
 * mapping (plan D4) — because the actual post-delete provenance is resolved
 * server-side and re-read from the roster after the delete.
 */

import type { JSX } from 'react';
import { SOURCE_BADGE_LABELS } from './ProviderRow.js';

export interface ResetConfirmProps {
  providerDisplayName: string;
  busy: boolean;
  error: string | null;
  onConfirm: () => void;
  onCancel: () => void;
}

export function ResetConfirm({
  providerDisplayName,
  busy,
  error,
  onConfirm,
  onCancel,
}: ResetConfirmProps): JSX.Element {
  return (
    <div
      data-testid="reset-confirm"
      className="mt-3 border border-amber-300 bg-amber-50 rounded-md p-3 text-sm dark:bg-amber-950 dark:border-amber-800"
    >
      <p className="text-amber-900 mb-3 dark:text-amber-200">
        Remove the platform settings for <strong>{providerDisplayName}</strong>? The default
        model will fall back to &ldquo;{SOURCE_BADGE_LABELS.config}&rdquo; or
        &ldquo;{SOURCE_BADGE_LABELS.descriptor}&rdquo;, as reported after the reset.
      </p>
      {error && (
        <div
          data-testid="reset-error"
          className="mb-3 text-xs text-red-600 dark:text-red-400"
        >
          {error}
        </div>
      )}
      <div className="flex gap-2">
        <button
          type="button"
          data-testid="reset-confirm-button"
          disabled={busy}
          onClick={onConfirm}
          className="px-3 py-1.5 text-xs font-medium text-white bg-amber-600 rounded-md hover:bg-amber-700 disabled:opacity-50"
        >
          {busy ? 'Resetting…' : 'Confirm reset'}
        </button>
        <button
          type="button"
          disabled={busy}
          onClick={onCancel}
          className="px-3 py-1.5 text-xs font-medium text-gray-700 border border-gray-300 bg-white rounded-md hover:bg-gray-50 dark:bg-gray-800 dark:text-gray-300 dark:border-gray-600"
        >
          Cancel
        </button>
      </div>
    </div>
  );
}
