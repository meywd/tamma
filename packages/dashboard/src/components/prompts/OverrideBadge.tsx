/**
 * OverrideBadge — visual indicator for whether a prompt is a system
 * default (falls through from the shipped registry) or a tenant-scoped
 * override.
 *
 * Story 27-5 AC #2, AC #3.
 */
import type { PromptSource } from '../../hooks/useTenantPrompts.js';

import type { JSX } from "react";

interface OverrideBadgeProps {
  source: PromptSource;
}

export function OverrideBadge({ source }: OverrideBadgeProps): JSX.Element {
  if (source === 'user') {
    return (
      <span className="inline-flex items-center px-2 py-0.5 text-xs font-medium bg-blue-100 text-blue-800 rounded-full dark:bg-blue-900 dark:text-blue-200">
        Override
      </span>
    );
  }
  return (
    <span className="inline-flex items-center px-2 py-0.5 text-xs font-medium bg-gray-100 text-gray-600 rounded-full dark:bg-gray-800 dark:text-gray-400">
      Default
    </span>
  );
}
