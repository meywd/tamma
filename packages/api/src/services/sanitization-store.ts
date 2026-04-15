/**
 * Sanitization Store Interface + InMemory Implementation
 *
 * Story 9-7: Sanitization Service + API
 *
 * Per-account sanitization rule configuration. Wraps the existing
 * ContentSanitizer class from @tamma/shared with account-scoped
 * configuration stored in the database.
 */

import { ContentSanitizer } from '@tamma/shared';

// ---------------------------------------------------------------------------
// Types
// ---------------------------------------------------------------------------

/** Sanitization rules for an account. */
export interface SanitizationRules {
  id: string;
  accountId: string | null;
  enabled: boolean;
  extraInjectionPatterns: string[];
  blockedCommandPatterns: string[];
  maxFetchSizeBytes: number;
  validateUrls: boolean;
  gateActions: boolean;
  createdAt: string;
  updatedAt: string;
}

/** Input for updating sanitization rules. */
export interface SanitizationRulesInput {
  enabled?: boolean;
  extraInjectionPatterns?: string[];
  blockedCommandPatterns?: string[];
  maxFetchSizeBytes?: number;
  validateUrls?: boolean;
  gateActions?: boolean;
}

/** Result of sanitizing content. */
export interface SanitizeResult {
  result: string;
  warnings: string[];
}

// ---------------------------------------------------------------------------
// ISanitizationStore Interface
// ---------------------------------------------------------------------------

export interface ISanitizationStore {
  /** Get sanitization rules for an account. Returns system defaults if none exist. */
  getRules(accountId: string | null): Promise<SanitizationRules>;

  /** Create or update sanitization rules for an account. */
  upsertRules(accountId: string | null, input: SanitizationRulesInput): Promise<SanitizationRules>;

  /** Sanitize content using the account's configured rules. */
  sanitize(accountId: string | null, content: string, direction: 'input' | 'output'): Promise<SanitizeResult>;
}

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

/** Maximum accepted length of a user-supplied regex pattern. */
const MAX_PATTERN_LENGTH = 256;

/**
 * Reject patterns that contain a nested quantifier inside a group — the
 * canonical recipe for catastrophic backtracking (ReDoS). We match any
 * group that contains `*`/`+`/`?`/`{n,m}` AND is itself followed by a
 * quantifier. Covers `(a+)+`, `(a*)+`, `(.*)*`, `(a{1,})+`, etc.
 *
 * This is a heuristic, not a proof — but combined with the length cap and
 * the owner-only auth gate on the upsert endpoint, it materially shrinks
 * the ReDoS attack surface without forcing us to adopt a non-backtracking
 * engine like `re2`.
 *
 * Note: this does not recurse into nested groups. For defence in depth we
 * also cap pattern length to 256 chars, which further limits reachable
 * states.
 */
const NESTED_QUANTIFIER = /\([^)]*[*+?{][^)]*\)[*+?{]/;

function validatePatterns(patterns: string[], label: string): void {
  for (const pattern of patterns) {
    if (typeof pattern !== 'string') {
      throw new Error(`Invalid regex pattern in ${label}: not a string`);
    }
    if (pattern.length > MAX_PATTERN_LENGTH) {
      throw new Error(
        `Invalid regex pattern in ${label}: too long (${pattern.length} > ${MAX_PATTERN_LENGTH})`,
      );
    }
    if (NESTED_QUANTIFIER.test(pattern)) {
      throw new Error(
        `Invalid regex pattern in ${label}: unsafe nested quantifier "${pattern}"`,
      );
    }
    // At this point the pattern is length-bounded and free of the common
    // ReDoS shape, so compiling it is safe. The `js/regex-injection`
    // CodeQL rule is excluded in .github/codeql/codeql-config.yml — see
    // that file for the full justification.
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
// InMemorySanitizationStore
// ---------------------------------------------------------------------------

let nextRuleId = 1;

export class InMemorySanitizationStore implements ISanitizationStore {
  private rules = new Map<string | null, SanitizationRules>();

  async getRules(accountId: string | null): Promise<SanitizationRules> {
    // Try account-specific rules first
    if (accountId !== null) {
      const accountRules = this.rules.get(accountId);
      if (accountRules) return { ...accountRules };
    }

    // Fall back to system defaults
    const systemRules = this.rules.get(null);
    if (systemRules) return { ...systemRules };

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

    const existing = this.rules.get(accountId);
    const now = new Date().toISOString();

    const rules: SanitizationRules = {
      id: existing?.id ?? `rule-${nextRuleId++}`,
      accountId,
      enabled: input.enabled ?? existing?.enabled ?? SYSTEM_DEFAULTS.enabled,
      extraInjectionPatterns: input.extraInjectionPatterns ?? existing?.extraInjectionPatterns ?? [...SYSTEM_DEFAULTS.extraInjectionPatterns],
      blockedCommandPatterns: input.blockedCommandPatterns ?? existing?.blockedCommandPatterns ?? [...SYSTEM_DEFAULTS.blockedCommandPatterns],
      maxFetchSizeBytes: input.maxFetchSizeBytes ?? existing?.maxFetchSizeBytes ?? SYSTEM_DEFAULTS.maxFetchSizeBytes,
      validateUrls: input.validateUrls ?? existing?.validateUrls ?? SYSTEM_DEFAULTS.validateUrls,
      gateActions: input.gateActions ?? existing?.gateActions ?? SYSTEM_DEFAULTS.gateActions,
      createdAt: existing?.createdAt ?? now,
      updatedAt: now,
    };

    this.rules.set(accountId, rules);
    return { ...rules };
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
}
