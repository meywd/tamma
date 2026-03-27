/**
 * Validation for IRepoConfig (repo-level project settings).
 *
 * Key security rule: reject if any field looks like an API key.
 * This prevents accidental secret commits.
 */

import { TammaError } from '../errors.js';
import type { IRepoConfig } from '../types/repo-config.js';

/**
 * Patterns that look like API keys / secrets.
 * If any string value in the config matches, we reject it.
 * Uses boundary-aware patterns to catch tokens at start of string,
 * after whitespace, or after common delimiters like = : / "
 */
const SECRET_PATTERNS = [
  /(?:^|[\s=/:"])sk-/,          // Anthropic, OpenAI
  /(?:^|[\s=/:"])ghp_/,         // GitHub PAT
  /(?:^|[\s=/:"])ghu_/,         // GitHub user token
  /(?:^|[\s=/:"])ghs_/,         // GitHub server token
  /(?:^|[\s=/:"])gho_/,         // GitHub OAuth token
  /(?:^|[\s=/:"])github_pat_/,  // GitHub fine-grained PAT
  /(?:^|[\s=/:"])glpat-/,       // GitLab PAT
  /(?:^|[\s=/:"])xoxb-/,        // Slack bot token
  /(?:^|[\s=/:"])xoxp-/,        // Slack user token
  /(?:^|[\s=/:"])AKIA/,         // AWS access key
];

/** Maximum blocked command patterns */
const MAX_BLOCKED_PATTERNS = 100;
/** Maximum pattern length */
const MAX_PATTERN_LENGTH = 500;
/** Maximum fetch size (1 GiB) */
const MAX_FETCH_SIZE_BYTES = 1_073_741_824;

/**
 * Recursively check all string values in an object for secret-like patterns.
 */
function findEmbeddedSecrets(obj: unknown, path: string): string[] {
  const found: string[] = [];

  if (typeof obj === 'string') {
    for (const pattern of SECRET_PATTERNS) {
      if (pattern.test(obj)) {
        found.push(`${path} looks like an API key/secret (matches ${pattern.source})`);
        break;
      }
    }
  } else if (Array.isArray(obj)) {
    for (let i = 0; i < obj.length; i++) {
      found.push(...findEmbeddedSecrets(obj[i], `${path}[${i}]`));
    }
  } else if (obj !== null && typeof obj === 'object') {
    for (const [key, value] of Object.entries(obj)) {
      found.push(...findEmbeddedSecrets(value, `${path}.${key}`));
    }
  }

  return found;
}

/**
 * Validates an IRepoConfig object.
 * Throws TammaError for invalid configurations.
 */
export function validateRepoConfig(config: IRepoConfig): void {
  // Check for embedded secrets — top priority
  const secrets = findEmbeddedSecrets(config, 'config');
  if (secrets.length > 0) {
    throw new TammaError(
      `Repo config appears to contain secrets that should not be committed:\n  - ${secrets.join('\n  - ')}`,
      'CONFIG.EMBEDDED_SECRET',
    );
  }

  // Validate engine settings
  if (config.engine) {
    if (config.engine.approvalMode !== undefined) {
      if (config.engine.approvalMode !== 'cli' && config.engine.approvalMode !== 'auto') {
        throw new TammaError(
          `engine.approvalMode must be 'cli' or 'auto' (got '${config.engine.approvalMode as string}')`,
          'CONFIG.INVALID_VALUE',
        );
      }
    }

    if (config.engine.pollIntervalMs !== undefined) {
      if (!Number.isFinite(config.engine.pollIntervalMs) || config.engine.pollIntervalMs < 1000) {
        throw new TammaError(
          `engine.pollIntervalMs must be >= 1000 (got ${config.engine.pollIntervalMs})`,
          'CONFIG.INVALID_VALUE',
        );
      }
    }

    if (config.engine.ciPollIntervalMs !== undefined) {
      if (!Number.isFinite(config.engine.ciPollIntervalMs) || config.engine.ciPollIntervalMs < 1000) {
        throw new TammaError(
          `engine.ciPollIntervalMs must be >= 1000 (got ${config.engine.ciPollIntervalMs})`,
          'CONFIG.INVALID_VALUE',
        );
      }
    }

    if (config.engine.ciMonitorTimeoutMs !== undefined) {
      if (!Number.isFinite(config.engine.ciMonitorTimeoutMs) || config.engine.ciMonitorTimeoutMs < 10_000) {
        throw new TammaError(
          `engine.ciMonitorTimeoutMs must be >= 10000 (got ${config.engine.ciMonitorTimeoutMs})`,
          'CONFIG.INVALID_VALUE',
        );
      }
    }
  }

  // Validate security settings
  if (config.security) {
    if (config.security.blockedCommandPatterns !== undefined) {
      if (config.security.blockedCommandPatterns.length > MAX_BLOCKED_PATTERNS) {
        throw new TammaError(
          `security.blockedCommandPatterns exceeds maximum of ${MAX_BLOCKED_PATTERNS} patterns`,
          'CONFIG.INVALID_REGEX',
        );
      }

      for (const pattern of config.security.blockedCommandPatterns) {
        if (pattern.length > MAX_PATTERN_LENGTH) {
          throw new TammaError(
            `security.blockedCommandPattern exceeds maximum length of ${MAX_PATTERN_LENGTH} chars`,
            'CONFIG.INVALID_REGEX',
          );
        }

        try {
          new RegExp(pattern);
        } catch {
          throw new TammaError(
            `security.blockedCommandPattern is not a valid regex: "${pattern}"`,
            'CONFIG.INVALID_REGEX',
          );
        }
      }
    }

    if (config.security.maxFetchSizeBytes !== undefined) {
      if (
        !Number.isFinite(config.security.maxFetchSizeBytes) ||
        config.security.maxFetchSizeBytes < 0 ||
        config.security.maxFetchSizeBytes > MAX_FETCH_SIZE_BYTES
      ) {
        throw new TammaError(
          `security.maxFetchSizeBytes must be between 0 and ${MAX_FETCH_SIZE_BYTES}`,
          'CONFIG.INVALID_VALUE',
        );
      }
    }
  }

  // Validate role references are strings (actual provider existence checked at resolve time)
  if (config.roles) {
    for (const [roleName, roleConfig] of Object.entries(config.roles)) {
      if (!roleConfig) continue;
      if (typeof roleConfig.provider !== 'string' || roleConfig.provider.trim() === '') {
        throw new TammaError(
          `roles.${roleName}.provider must be a non-empty string`,
          'CONFIG.INVALID_VALUE',
        );
      }
    }
  }
}
