/**
 * OverrideBadge — visual indicator for convention source.
 * Reuses the same visual pattern as prompts/OverrideBadge but for conventions.
 *
 * Story 27-12 AC: override highlighting.
 */

import type { JSX } from 'react';
import type { ConventionSource } from '../../services/admin/conventions-api-client.js';

interface OverrideBadgeProps {
  source: ConventionSource;
  isOverride?: boolean;
}

export function ConventionOverrideBadge({ source, isOverride }: OverrideBadgeProps): JSX.Element {
  if (isOverride || source === 'tenant') {
    return (
      <span className="inline-flex items-center px-2 py-0.5 text-xs font-medium bg-blue-100 text-blue-800 rounded-full dark:bg-blue-900 dark:text-blue-200">
        Override
      </span>
    );
  }
  return (
    <span className="inline-flex items-center px-2 py-0.5 text-xs font-medium bg-gray-100 text-gray-600 rounded-full dark:bg-gray-800 dark:text-gray-400">
      System Default
    </span>
  );
}
