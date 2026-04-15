/**
 * PostgreSQL-backed Sanitization Store
 *
 * Story 9-7: Sanitization Service + API
 *
 * Persists per-account sanitization rules to the sanitization_rules table
 * created in migration 016.
 */

import type pg from 'pg';
import { ContentSanitizer } from '@tamma/shared';

import type {
  ISanitizationStore,
  SanitizationRules,
  SanitizationRulesInput,
  SanitizeResult,
} from './sanitization-store.js';

// ---------------------------------------------------------------------------
// Default rules
// ---------------------------------------------------------------------------

const SYSTEM_DEFAULTS: Omit<SanitizationRules, 'id' | 'createdAt' | 'updatedAt'> = {
  accountId: null,
  enabled: true,
  extraInjectionPatterns: [],
  blockedCommandPatterns: ['rm\\s+-rf\\s+/', 'DROP\\s+TABLE', 'DELETE\\s+FROM'],
  maxFetchSizeBytes: 10_485_760,
  validateUrls: true,
  gateActions: true,
};

// ---------------------------------------------------------------------------
// Validation
// ---------------------------------------------------------------------------

function validatePatterns(patterns: string[], label: string): void {
  for (const pattern of patterns) {
    try {
      new RegExp(pattern);
    } catch {
      throw new Error(`Invalid regex pattern in ${label}: "${pattern}"`);
    }
  }
}

function validateRulesInput(input: SanitizationRulesInput): void {
  if (input.extraInjectionPatterns !== undefined) {
    validatePatterns(input.extraInjectionPatterns, 'extraInjectionPatterns');
  }
  if (input.blockedCommandPatterns !== undefined) {
    validatePatterns(input.blockedCommandPatterns, 'blockedCommandPatterns');
  }
  if (input.maxFetchSizeBytes !== undefined) {
    if (!Number.isFinite(input.maxFetchSizeBytes) || input.maxFetchSizeBytes < 0) {
      throw new Error('maxFetchSizeBytes must be a non-negative finite number');
    }
  }
}

// ---------------------------------------------------------------------------
// PgSanitizationStore
// ---------------------------------------------------------------------------

export class PgSanitizationStore implements ISanitizationStore {
  constructor(private readonly pool: pg.Pool) {}

  async getRules(accountId: string | null): Promise<SanitizationRules> {
    // Try account-specific rules first
    if (accountId !== null) {
      const accountResult = await this.pool.query<Record<string, unknown>>(
        'SELECT * FROM sanitization_rules WHERE account_id = $1',
        [accountId],
      );
      if (accountResult.rows.length > 0) {
        return this._mapRow(accountResult.rows[0]!);
      }
    }

    // Fall back to system defaults (account_id IS NULL)
    const systemResult = await this.pool.query<Record<string, unknown>>(
      'SELECT * FROM sanitization_rules WHERE account_id IS NULL',
    );
    if (systemResult.rows.length > 0) {
      return this._mapRow(systemResult.rows[0]!);
    }

    // Return hardcoded defaults
    const now = new Date().toISOString();
    return {
      id: 'default',
      ...SYSTEM_DEFAULTS,
      accountId,
      createdAt: now,
      updatedAt: now,
    };
  }

  async upsertRules(accountId: string | null, input: SanitizationRulesInput): Promise<SanitizationRules> {
    validateRulesInput(input);

    // Get current rules for defaults
    const current = await this.getRules(accountId);

    const enabled = input.enabled ?? current.enabled;
    const extraInjectionPatterns = input.extraInjectionPatterns ?? current.extraInjectionPatterns;
    const blockedCommandPatterns = input.blockedCommandPatterns ?? current.blockedCommandPatterns;
    const maxFetchSizeBytes = input.maxFetchSizeBytes ?? current.maxFetchSizeBytes;
    const validateUrls = input.validateUrls ?? current.validateUrls;
    const gateActions = input.gateActions ?? current.gateActions;

    let result: pg.QueryResult<Record<string, unknown>>;

    if (accountId === null) {
      result = await this.pool.query<Record<string, unknown>>(
        `INSERT INTO sanitization_rules
           (account_id, enabled, extra_injection_patterns, blocked_command_patterns,
            max_fetch_size_bytes, validate_urls, gate_actions)
         VALUES (NULL, $1, $2, $3, $4, $5, $6)
         ON CONFLICT (account_id) WHERE account_id IS NULL
         DO UPDATE SET
           enabled = EXCLUDED.enabled,
           extra_injection_patterns = EXCLUDED.extra_injection_patterns,
           blocked_command_patterns = EXCLUDED.blocked_command_patterns,
           max_fetch_size_bytes = EXCLUDED.max_fetch_size_bytes,
           validate_urls = EXCLUDED.validate_urls,
           gate_actions = EXCLUDED.gate_actions,
           updated_at = NOW()
         RETURNING *`,
        [enabled, extraInjectionPatterns, blockedCommandPatterns, maxFetchSizeBytes, validateUrls, gateActions],
      );
    } else {
      result = await this.pool.query<Record<string, unknown>>(
        `INSERT INTO sanitization_rules
           (account_id, enabled, extra_injection_patterns, blocked_command_patterns,
            max_fetch_size_bytes, validate_urls, gate_actions)
         VALUES ($1, $2, $3, $4, $5, $6, $7)
         ON CONFLICT (account_id)
         DO UPDATE SET
           enabled = EXCLUDED.enabled,
           extra_injection_patterns = EXCLUDED.extra_injection_patterns,
           blocked_command_patterns = EXCLUDED.blocked_command_patterns,
           max_fetch_size_bytes = EXCLUDED.max_fetch_size_bytes,
           validate_urls = EXCLUDED.validate_urls,
           gate_actions = EXCLUDED.gate_actions,
           updated_at = NOW()
         RETURNING *`,
        [accountId, enabled, extraInjectionPatterns, blockedCommandPatterns, maxFetchSizeBytes, validateUrls, gateActions],
      );
    }

    return this._mapRow(result.rows[0]!);
  }

  async sanitize(accountId: string | null, content: string, direction: 'input' | 'output'): Promise<SanitizeResult> {
    const rules = await this.getRules(accountId);

    const sanitizer = new ContentSanitizer({
      enabled: rules.enabled,
      extraInjectionPatterns: rules.extraInjectionPatterns,
    });

    if (direction === 'output') {
      return sanitizer.sanitizeOutput(content);
    }
    return sanitizer.sanitize(content);
  }

  private _mapRow(row: Record<string, unknown>): SanitizationRules {
    const extraPatterns = row['extra_injection_patterns'];
    const blockedPatterns = row['blocked_command_patterns'];

    return {
      id: String(row['id']),
      accountId: row['account_id'] !== null && row['account_id'] !== undefined ? String(row['account_id']) : null,
      enabled: Boolean(row['enabled']),
      extraInjectionPatterns: Array.isArray(extraPatterns) ? extraPatterns as string[] : [],
      blockedCommandPatterns: Array.isArray(blockedPatterns) ? blockedPatterns as string[] : [],
      maxFetchSizeBytes: Number(row['max_fetch_size_bytes'] ?? 10_485_760),
      validateUrls: Boolean(row['validate_urls']),
      gateActions: Boolean(row['gate_actions']),
      createdAt: String(row['created_at']),
      updatedAt: String(row['updated_at']),
    };
  }
}
