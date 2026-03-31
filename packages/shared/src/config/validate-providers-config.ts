/**
 * Validation for IProvidersConfig (user-level provider settings).
 */

import { TammaError } from '../errors.js';
import { validateProviderName, validateMaxBudgetUsd } from '../types/agent-config.js';
import type { IProvidersConfig } from '../types/providers-config.js';

/** Forbidden property names (prototype pollution guard). */
const FORBIDDEN_KEYS = new Set(['__proto__', 'constructor', 'prototype']);

/**
 * Validates an IProvidersConfig object.
 * Throws TammaError for invalid configurations.
 * Returns an array of warning messages for non-fatal issues.
 */
export function validateProvidersConfig(config: IProvidersConfig): string[] {
  const warnings: string[] = [];

  if (!config.providers || typeof config.providers !== 'object') {
    throw new TammaError(
      'providers must be a non-null object',
      'CONFIG.INVALID_VALUE',
    );
  }

  const providerNames = Object.keys(config.providers);
  if (providerNames.length === 0) {
    throw new TammaError(
      'At least one provider must be defined in providers config',
      'CONFIG.EMPTY_PROVIDERS',
    );
  }

  for (const name of providerNames) {
    // Prototype pollution guard
    if (FORBIDDEN_KEYS.has(name)) {
      throw new TammaError(
        `Provider name "${name}" is forbidden (prototype pollution guard)`,
        'CONFIG.INVALID_PROVIDER_NAME',
      );
    }

    validateProviderName(name);

    const def = config.providers[name];
    if (def) {
      // Warn if apiKey looks empty
      if (def.apiKey !== undefined && def.apiKey.trim() === '') {
        warnings.push(`Provider "${name}" has an empty apiKey — it will likely fail at runtime`);
      }

      // Validate timeoutSeconds if present
      if (def.timeoutSeconds !== undefined) {
        if (!Number.isFinite(def.timeoutSeconds) || def.timeoutSeconds <= 0) {
          throw new TammaError(
            `Provider "${name}" timeoutSeconds must be a positive finite number (got ${def.timeoutSeconds})`,
            'CONFIG.INVALID_VALUE',
          );
        }
      }
    }
  }

  // Validate maxBudgetUsd if present
  if (config.maxBudgetUsd !== undefined) {
    validateMaxBudgetUsd(config.maxBudgetUsd);
  }

  // Validate permissionMode if present
  if (config.permissionMode !== undefined) {
    if (config.permissionMode !== 'bypassPermissions' && config.permissionMode !== 'default') {
      throw new TammaError(
        `permissionMode must be 'bypassPermissions' or 'default' (got '${config.permissionMode as string}')`,
        'CONFIG.INVALID_VALUE',
      );
    }
  }

  return warnings;
}
