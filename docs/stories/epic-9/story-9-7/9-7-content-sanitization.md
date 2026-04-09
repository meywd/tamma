# Story 9-7: Sanitization Service + API

## User Story

As a platform operator, I want content sanitization rules configurable per account and accessible via API, so that both the TS engine and Elsa workflows sanitize content consistently and account admins can customize sanitization behavior.

## Goal

Expose the existing `ContentSanitizer` and `SecureAgentProvider` functionality via a Fastify API endpoint. Add per-account sanitization rule configuration stored in Postgres. Both consumers call the API or use the shared sanitizer instance.

## Acceptance Criteria

1. API endpoints:
   - `POST /api/v1/sanitize` -- sanitize content using the account's configured rules. Returns sanitized content and warnings.
   - `GET /api/v1/sanitize/rules` -- get sanitization rules for the authenticated account.
   - `PUT /api/v1/sanitize/rules` -- update sanitization rules (e.g., extra injection patterns, enabled/disabled, custom blocked commands).
2. Sanitization rules stored in Postgres per account with system default fallback.
3. The existing `ContentSanitizer` class in `packages/shared/src/security/content-sanitizer.ts` remains the core implementation. The API wraps it with account-scoped configuration.
4. The existing `SecureAgentProvider` decorator continues to work in-process.
5. Elsa's `IContentSanitizer` C# interface delegates to the API for sanitization.
6. All existing sanitization behaviors preserved:
   - HTML stripping (quote-aware state machine)
   - Zero-width character removal (20+ Unicode code points)
   - Prompt injection detection (5 categories)
   - URL validation (numeric octet parsing for private IPs)
   - Action gating (blocked command patterns)
   - Secure fetch (redirect validation, size limits)

## Technical Context

### Existing Files

- `packages/shared/src/security/content-sanitizer.ts` -- `ContentSanitizer`, `IContentSanitizer`
- `packages/shared/src/security/url-validator.ts` -- `validateUrl()`, `isPrivateHost()`
- `packages/shared/src/security/action-gating.ts` -- `evaluateAction()`
- `packages/shared/src/security/secure-fetch.ts` -- `secureFetch()`
- `packages/providers/src/secure-agent-provider.ts` -- `SecureAgentProvider` decorator
- `packages/api/src/routes/settings/security-routes.ts` -- placeholder security routes
- `apps/tamma-elsa/src/Tamma.Activities/Security/` -- C# sanitization (to delegate to API)

### Database Schema

```sql
CREATE TABLE sanitization_rules (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  account_id UUID NULL REFERENCES tenants(id) ON DELETE CASCADE,
  enabled BOOLEAN NOT NULL DEFAULT true,
  extra_injection_patterns TEXT[] DEFAULT '{}',
  blocked_command_patterns TEXT[] DEFAULT '{}',
  max_fetch_size_bytes INTEGER DEFAULT 10485760,
  validate_urls BOOLEAN DEFAULT true,
  gate_actions BOOLEAN DEFAULT true,
  created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
  UNIQUE (account_id)
);
```

### API Routes

```
POST /api/v1/sanitize
  → Body: { content: string, direction: 'input' | 'output' }
  → accountId from JWT
  → Loads rules for account, applies ContentSanitizer
  → Returns: { result: string, warnings: string[] }

GET /api/v1/sanitize/rules
  → accountId from JWT
  → Returns: SanitizationRules (account override or system default)

PUT /api/v1/sanitize/rules
  → accountId from JWT
  → Body: Partial<SanitizationRules>
  → Validates patterns compile as regex
  → Returns: { rules: SanitizationRules, version: number }
```

### Architecture

```
TS Engine (in-process)              Elsa Workflow (C#)
      │                                    │
  SecureAgentProvider                 HTTP POST to
  → ContentSanitizer                 /api/v1/sanitize
      │                                    │
      └─── loads rules ───►  SanitizationService ◄──┘
                                     │
                             sanitization_rules (Postgres)
```

## Files

- CREATE `packages/api/src/services/sanitization-store.ts` -- per-account sanitization rules
- CREATE `packages/api/src/services/sanitization-store.test.ts`
- MODIFY `packages/api/src/routes/settings/security-routes.ts` -- implement sanitization endpoints
- CREATE `database/migrations/NNNN_create_sanitization_rules.sql`
- No changes to `packages/shared/src/security/` (used as-is via service wrapper)

## Dependencies

- **Epic 16** (tenants table for account_id FK)
- **Epic 17** (JWT auth for API endpoints)

## Effort Estimate

**14 hours**

- 3h: Database migration + seed data
- 4h: Sanitization store service (load rules, apply sanitizer with account config)
- 4h: API routes (POST sanitize, GET/PUT rules)
- 3h: Tests (service + routes + rule validation)
