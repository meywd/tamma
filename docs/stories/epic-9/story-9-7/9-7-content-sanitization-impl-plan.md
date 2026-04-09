# Story 9-7: Sanitization Service + API — Implementation Plan

## Overview

Add per-account sanitization rule configuration stored in Postgres and expose Fastify API endpoints for sanitizing content and managing rules. The existing `ContentSanitizer` class remains the core implementation. The API wraps it with account-scoped configuration loaded from the `sanitization_rules` table. Elsa workflows call `POST /api/v1/sanitize` instead of running C# sanitization locally.

---

## Step-by-Step Implementation Tasks

### Task 1: Create the Migration SQL File (2 hours)

**File to create**: `database/migrations/011_sanitization_rules.sql`

```sql
-- Per-account sanitization rules
-- Epic 9, Story 9-7

CREATE TABLE IF NOT EXISTS sanitization_rules (
  id                       UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  account_id               UUID UNIQUE,    -- NULL = system default; FK deferred to Epic 17
  enabled                  BOOLEAN NOT NULL DEFAULT true,
  extra_injection_patterns TEXT[] NOT NULL DEFAULT '{}',
  blocked_command_patterns TEXT[] NOT NULL DEFAULT '{}',
  max_fetch_size_bytes     INTEGER NOT NULL DEFAULT 10485760 CHECK (max_fetch_size_bytes >= 0),
  validate_urls            BOOLEAN NOT NULL DEFAULT true,
  gate_actions             BOOLEAN NOT NULL DEFAULT true,
  created_at               TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at               TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

-- Partial unique index for system default
CREATE UNIQUE INDEX IF NOT EXISTS idx_sanitization_rules_system_default
  ON sanitization_rules (account_id)
  WHERE account_id IS NULL;

-- Lookup index
CREATE INDEX IF NOT EXISTS idx_sanitization_rules_account_id
  ON sanitization_rules (account_id)
  WHERE account_id IS NOT NULL;

-- Seed system default
INSERT INTO sanitization_rules (account_id, enabled, extra_injection_patterns, blocked_command_patterns, max_fetch_size_bytes, validate_urls, gate_actions)
VALUES (
  NULL,
  true,
  '{}',
  ARRAY['rm\s+-rf\s+/', 'DROP\s+TABLE', 'DELETE\s+FROM'],
  10485760,
  true,
  true
)
ON CONFLICT DO NOTHING;
```

---

### Task 2: Define ISanitizationStore Interface + Types (1.5 hours)

**File to create**: `packages/api/src/services/sanitization-store.ts`

```typescript
/** Sanitization rules for an account. */
export interface SanitizationRules {
  enabled: boolean;
  extraInjectionPatterns: string[];
  blockedCommandPatterns: string[];
  maxFetchSizeBytes: number;
  validateUrls: boolean;
  gateActions: boolean;
}

/** Result of sanitization. */
export interface SanitizationResult {
  result: string;
  warnings: string[];
}

/** Interface for the sanitization store. */
export interface ISanitizationStore {
  /** Get sanitization rules for account (account override -> system default -> hardcoded). */
  getRules(accountId: string | null): Promise<SanitizationRules>;
  /** Update sanitization rules for an account. */
  upsertRules(accountId: string | null, rules: Partial<SanitizationRules>): Promise<SanitizationRules>;
  /** Sanitize content using account-specific rules. */
  sanitize(accountId: string | null, content: string, direction: 'input' | 'output'): Promise<SanitizationResult>;
}

/** Hardcoded default rules. */
export const DEFAULT_SANITIZATION_RULES: SanitizationRules = {
  enabled: true,
  extraInjectionPatterns: [],
  blockedCommandPatterns: ['rm\\s+-rf\\s+/', 'DROP\\s+TABLE', 'DELETE\\s+FROM'],
  maxFetchSizeBytes: 10_485_760,
  validateUrls: true,
  gateActions: true,
};
```

---

### Task 3: Implement PgSanitizationStore (3 hours)

**File to create**: `packages/api/src/services/pg-sanitization-store.ts`

```typescript
import type pg from 'pg';
import { ContentSanitizer } from '@tamma/shared';
import type { ISanitizationStore, SanitizationRules, SanitizationResult } from './sanitization-store.js';
import { DEFAULT_SANITIZATION_RULES } from './sanitization-store.js';

export class PgSanitizationStore implements ISanitizationStore {
  constructor(private readonly pool: pg.Pool) {}

  async getRules(accountId: string | null): Promise<SanitizationRules> {
    // 1. Account override
    if (accountId !== null) {
      const result = await this.pool.query<Record<string, unknown>>(
        'SELECT * FROM sanitization_rules WHERE account_id = $1',
        [accountId],
      );
      if (result.rows.length > 0) return this._mapRow(result.rows[0]!);
    }
    // 2. System default
    const system = await this.pool.query<Record<string, unknown>>(
      'SELECT * FROM sanitization_rules WHERE account_id IS NULL',
    );
    if (system.rows.length > 0) return this._mapRow(system.rows[0]!);
    // 3. Hardcoded
    return { ...DEFAULT_SANITIZATION_RULES };
  }

  async upsertRules(accountId: string | null, rules: Partial<SanitizationRules>): Promise<SanitizationRules> {
    // Validate patterns compile as regex before saving
    if (rules.extraInjectionPatterns) {
      for (const pattern of rules.extraInjectionPatterns) {
        try { RegExp(pattern); } catch { throw new Error(`Invalid injection pattern: ${pattern}`); }
      }
    }
    if (rules.blockedCommandPatterns) {
      for (const pattern of rules.blockedCommandPatterns) {
        try { RegExp(pattern); } catch { throw new Error(`Invalid blocked pattern: ${pattern}`); }
      }
    }

    // UPSERT with merge of provided fields
    const current = await this.getRules(accountId);
    const merged: SanitizationRules = {
      enabled: rules.enabled ?? current.enabled,
      extraInjectionPatterns: rules.extraInjectionPatterns ?? current.extraInjectionPatterns,
      blockedCommandPatterns: rules.blockedCommandPatterns ?? current.blockedCommandPatterns,
      maxFetchSizeBytes: rules.maxFetchSizeBytes ?? current.maxFetchSizeBytes,
      validateUrls: rules.validateUrls ?? current.validateUrls,
      gateActions: rules.gateActions ?? current.gateActions,
    };

    // UPSERT SQL with ON CONFLICT (account_id)
    await this.pool.query(`
      INSERT INTO sanitization_rules (account_id, enabled, extra_injection_patterns, blocked_command_patterns, max_fetch_size_bytes, validate_urls, gate_actions)
      VALUES ($1, $2, $3, $4, $5, $6, $7)
      ON CONFLICT (account_id) DO UPDATE SET
        enabled = $2,
        extra_injection_patterns = $3,
        blocked_command_patterns = $4,
        max_fetch_size_bytes = $5,
        validate_urls = $6,
        gate_actions = $7,
        updated_at = NOW()
    `, [
      accountId,
      merged.enabled,
      merged.extraInjectionPatterns,
      merged.blockedCommandPatterns,
      merged.maxFetchSizeBytes,
      merged.validateUrls,
      merged.gateActions,
    ]);

    return merged;
  }

  async sanitize(accountId: string | null, content: string, direction: 'input' | 'output'): Promise<SanitizationResult> {
    const rules = await this.getRules(accountId);

    if (!rules.enabled) {
      return { result: content, warnings: [] };
    }

    // Create a ContentSanitizer configured with account-specific rules
    const sanitizer = new ContentSanitizer({
      enabled: rules.enabled,
      extraInjectionPatterns: rules.extraInjectionPatterns,
    });

    if (direction === 'input') {
      return sanitizer.sanitize(content);
    }
    return sanitizer.sanitizeOutput(content);
  }

  private _mapRow(row: Record<string, unknown>): SanitizationRules {
    return {
      enabled: Boolean(row['enabled']),
      extraInjectionPatterns: (row['extra_injection_patterns'] as string[]) ?? [],
      blockedCommandPatterns: (row['blocked_command_patterns'] as string[]) ?? [],
      maxFetchSizeBytes: Number(row['max_fetch_size_bytes']),
      validateUrls: Boolean(row['validate_urls']),
      gateActions: Boolean(row['gate_actions']),
    };
  }
}
```

---

### Task 4: Implement Fastify Routes (3 hours)

**File to modify**: `packages/api/src/routes/settings/security-routes.ts`

Replace placeholder with full endpoints:

```typescript
import type { FastifyInstance } from 'fastify';
import type { ISanitizationStore } from '../../services/sanitization-store.js';

export function registerSecurityRoutes(app: FastifyInstance, store: ISanitizationStore): void {
  // POST /api/v1/sanitize — sanitize content
  app.post('/sanitize', {
    schema: {
      body: {
        type: 'object',
        required: ['content', 'direction'],
        properties: {
          content: { type: 'string', maxLength: 1_000_000 },
          direction: { type: 'string', enum: ['input', 'output'] },
        },
      },
      response: {
        200: {
          type: 'object',
          properties: {
            result: { type: 'string' },
            warnings: { type: 'array', items: { type: 'string' } },
          },
        },
      },
    },
  }, async (request, reply) => {
    const accountId = (request as any).accountId ?? null;
    const { content, direction } = request.body as { content: string; direction: 'input' | 'output' };
    const result = await store.sanitize(accountId, content, direction);
    return reply.send(result);
  });

  // GET /api/v1/sanitize/rules — get rules for account
  app.get('/sanitize/rules', async (request, reply) => {
    const accountId = (request as any).accountId ?? null;
    const rules = await store.getRules(accountId);
    return reply.send(rules);
  });

  // PUT /api/v1/sanitize/rules — update rules for account
  app.put('/sanitize/rules', {
    schema: {
      body: {
        type: 'object',
        properties: {
          enabled: { type: 'boolean' },
          extraInjectionPatterns: { type: 'array', items: { type: 'string' } },
          blockedCommandPatterns: { type: 'array', items: { type: 'string' } },
          maxFetchSizeBytes: { type: 'integer', minimum: 0 },
          validateUrls: { type: 'boolean' },
          gateActions: { type: 'boolean' },
        },
      },
    },
  }, async (request, reply) => {
    const accountId = (request as any).accountId ?? null;
    const rules = request.body as Partial<SanitizationRules>;
    try {
      const updated = await store.upsertRules(accountId, rules);
      return reply.send({ rules: updated });
    } catch (err) {
      return reply.status(400).send({ error: err instanceof Error ? err.message : 'Invalid rules' });
    }
  });
}
```

---

### Task 5: Wire PgSanitizationStore + Update Settings Index (1.5 hours)

**File to modify**: `packages/api/src/routes/settings/index.ts`

```typescript
export interface SettingsServices {
  // ... existing
  sanitizationStore: ISanitizationStore;
}
```

Wire `registerSecurityRoutes(instance, svc.sanitizationStore)` in the `/api/config` route block.

---

### Task 6: Tests (3 hours)

**File to create**: `packages/api/src/services/sanitization-store.test.ts`

| # | Test | Assertion |
|---|------|-----------|
| 1 | `getRules(null)` returns system default | Matches seed data |
| 2 | `getRules(accountId)` returns account override | Account-specific rules |
| 3 | `getRules(accountId)` falls back to system default | When no override exists |
| 4 | `upsertRules()` creates new override | Persisted correctly |
| 5 | `upsertRules()` merges partial update | Unchanged fields preserved |
| 6 | `upsertRules()` rejects invalid regex pattern | Error thrown |
| 7 | `sanitize()` with enabled=true applies sanitization | HTML stripped, warnings generated |
| 8 | `sanitize()` with enabled=false returns content unchanged | No modifications |
| 9 | `sanitize()` with extra injection patterns | Additional patterns detected |
| 10 | `sanitize()` with direction='output' | Less aggressive sanitization |

**File to create**: `packages/api/src/routes/settings/__tests__/security-routes.test.ts`

| # | Test | Assertion |
|---|------|-----------|
| 11 | POST /sanitize with valid content returns 200 | Sanitized result |
| 12 | POST /sanitize with HTML content | HTML stripped |
| 13 | GET /sanitize/rules returns 200 | Rules shape |
| 14 | PUT /sanitize/rules with valid body returns 200 | Updated rules |
| 15 | PUT /sanitize/rules with invalid regex returns 400 | Error message |

**Total tests**: ~15

---

## Files to Create

| # | File Path | Purpose |
|---|-----------|---------|
| 1 | `database/migrations/011_sanitization_rules.sql` | DDL + seed data |
| 2 | `packages/api/src/services/sanitization-store.ts` | Interface + types |
| 3 | `packages/api/src/services/pg-sanitization-store.ts` | Postgres implementation |
| 4 | `packages/api/src/services/sanitization-store.test.ts` | Service tests |
| 5 | `packages/api/src/routes/settings/__tests__/security-routes.test.ts` | Route tests |

## Files to Modify

| # | File Path | Change |
|---|-----------|--------|
| 1 | `packages/api/src/routes/settings/security-routes.ts` | Replace placeholder with full endpoints |
| 2 | `packages/api/src/routes/settings/index.ts` | Wire PgSanitizationStore |

---

## Dependencies

- **Epic 16** (tenants table for account_id FK -- deferred)
- **Epic 17** (JWT auth for API endpoints)

## Migration from Existing Code

1. The `ContentSanitizer` in `packages/shared/src/security/content-sanitizer.ts` remains unchanged. `PgSanitizationStore.sanitize()` creates instances configured with per-account rules.
2. The `SecureAgentProvider` in `packages/providers/src/secure-agent-provider.ts` remains unchanged -- it wraps providers with the in-process sanitizer. In API mode, the sanitizer instance can be constructed with rules loaded from the store.
3. Elsa's C# `IContentSanitizer` delegates to `POST /api/v1/sanitize` instead of running local sanitization.
4. The existing `security-routes.ts` placeholder routes are replaced with full CRUD and sanitization endpoints.

---

## Estimated Effort

| Task | Hours |
|------|-------|
| Migration SQL (DDL + seed) | 2 |
| ISanitizationStore interface + types | 1.5 |
| PgSanitizationStore implementation | 3 |
| Fastify routes (POST, GET, PUT) | 3 |
| Settings index wiring | 1.5 |
| Tests (15 tests) | 3 |
| **Total** | **14 hours** |
