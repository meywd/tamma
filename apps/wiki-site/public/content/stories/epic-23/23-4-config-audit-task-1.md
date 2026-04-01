---
title: "Task 1: Configuration Audit API Routes & Services"
sidebar:
  order: 230
---

**Story:** 23-4-configuration-audit
**Epic:** 23

## Task Description

Create backend API routes and services for the configuration audit screen: config key inventory with validation, config source metadata, diff against defaults, environment variable completeness, missing config alerts, provider/platform connectivity validation, and config change history with restore capability.

## Acceptance Criteria

- `GET /api/monitoring/config/inventory` returns all config keys with values (secrets redacted), sources, and validation status
- `GET /api/monitoring/config/sources` returns config source metadata with priority order
- `GET /api/monitoring/config/diff` returns diff between current and default configuration
- `GET /api/monitoring/config/env-vars` returns environment variable completeness (redacted values)
- `GET /api/monitoring/config/missing` returns list of missing/invalid required configuration
- `POST /api/monitoring/config/validate-provider` tests provider connectivity
- `POST /api/monitoring/config/validate-platform` tests git platform connectivity
- `GET /api/monitoring/config/history` returns config change history
- `POST /api/monitoring/config/restore` restores a previous config (owner-only)
- Secret redaction enforced server-side: API keys, tokens, passwords never returned in full

## Implementation Details

### Technical Requirements

- [ ] Create `packages/api/src/routes/monitoring/config-routes.ts`:
  ```typescript
  export function registerConfigMonitoringRoutes(
    app: FastifyInstance,
    configAuditService: ConfigAuditService,
    configChangeLogger: ConfigChangeLogger,
    envVarChecker: EnvVarChecker,
  ): void;
  ```
  - Restore endpoint requires `requirePermission('settings:manage')` (owner-only)
  - Validate endpoints are POST to prevent caching and accidental re-execution

- [ ] Create `packages/api/src/services/monitoring/config-audit-service.ts`:
  ```typescript
  export interface ConfigKeyEntry {
    key: string;                       // dotted path, e.g., "agents.defaults.providerChain"
    value: unknown;                    // current value (secrets redacted)
    source: 'env' | 'user-settings' | 'repo-config' | 'api-override' | 'default';
    defaultValue: unknown;
    validationStatus: 'valid' | 'invalid' | 'warning';
    validationMessage: string | null;
    lastModified: string | null;
    dataType: 'string' | 'number' | 'boolean' | 'array' | 'object';
    isSecret: boolean;
  }

  export interface ConfigSource {
    name: string;
    type: 'env' | 'file' | 'database' | 'memory' | 'default';
    location: string;
    status: 'active' | 'inactive';
    lastModified: string | null;
    priority: number;                  // 1 = highest
  }

  export interface ConfigDiffEntry {
    key: string;
    currentValue: unknown;             // redacted if secret
    defaultValue: unknown;
    changeType: 'added' | 'modified' | 'removed';
  }

  export class ConfigAuditService {
    constructor(deps: {
      configService: ConfigService;
      healthService: HealthService;
    });

    async getInventory(): Promise<ConfigKeyEntry[]>;
    async getSources(): Promise<ConfigSource[]>;
    async getDiff(): Promise<ConfigDiffEntry[]>;
    async getMissing(): Promise<ConfigKeyEntry[]>;
    async validateProvider(provider: string): Promise<{ success: boolean; latencyMs: number; error?: string; models?: string[] }>;
    async validatePlatform(): Promise<{ success: boolean; latencyMs: number; error?: string; repos?: string[] }>;
  }
  ```
  - Inventory: reflects over agents config, security config, provider config, engine config, server config, GitHub config, ELSA config
  - Secret detection: key name contains `apiKey`, `secret`, `password`, `token` -> redact to `****` + last 4 chars
  - Validation: checks required fields, regex patterns, numeric ranges
  - Diff: deep-compare current config against hardcoded defaults in ConfigService
  - Provider validation: creates temporary provider instance, calls `isAvailable()`, disposes immediately
  - Platform validation: calls platform API to verify token, lists first 5 repos

- [ ] Create `packages/api/src/services/monitoring/config-change-logger.ts`:
  ```typescript
  export interface ConfigChangeEntry {
    id: string;
    timestamp: string;
    userId: string;
    key: string;
    oldValue: unknown;     // redacted
    newValue: unknown;     // redacted
    source: 'api' | 'cli' | 'dashboard';
  }

  export class ConfigChangeLogger {
    private history: ConfigChangeEntry[];
    private readonly maxEntries: number;  // default 200

    constructor(maxEntries?: number);
    record(entry: Omit<ConfigChangeEntry, 'id' | 'timestamp'>): void;
    getHistory(options?: { limit?: number; since?: string }): ConfigChangeEntry[];
    getEntryById(id: string): ConfigChangeEntry | null;
  }
  ```
  - In-memory ring buffer (last 200 changes)
  - `record()` called from ConfigService instrumentation
  - Values redacted before storage using same secret detection logic

- [ ] Create `packages/api/src/services/monitoring/env-var-checker.ts`:
  ```typescript
  export interface EnvVarStatus {
    name: string;
    set: boolean;
    source: 'env' | '.env' | null;
    redactedValue: string | null;      // first 4 chars + "****" or null
    referencedBy: string | null;       // which config key references this
    required: boolean;
  }

  export class EnvVarChecker {
    constructor(deps: { configService: ConfigService });
    async check(): Promise<EnvVarStatus[]>;
  }
  ```
  - Checks standard env vars: ANTHROPIC_API_KEY, OPENAI_API_KEY, GITHUB_TOKEN, etc.
  - Checks `apiKeyRef` values from provider chain entries
  - Flags missing referenced env vars as errors

### Files to Create

- CREATE `packages/api/src/routes/monitoring/config-routes.ts`
- CREATE `packages/api/src/services/monitoring/config-audit-service.ts`
- CREATE `packages/api/src/services/monitoring/config-change-logger.ts`
- CREATE `packages/api/src/services/monitoring/env-var-checker.ts`
- CREATE `packages/api/src/routes/monitoring/__tests__/config-routes.test.ts`
- CREATE `packages/api/src/services/monitoring/__tests__/config-audit-service.test.ts`
- CREATE `packages/api/src/services/monitoring/__tests__/config-change-logger.test.ts`
- CREATE `packages/api/src/services/monitoring/__tests__/env-var-checker.test.ts`

### Files to Modify

- MODIFY `packages/api/src/routes/monitoring/index.ts` -- register config routes
- MODIFY `packages/api/src/services/settings/ConfigService.ts` -- add onChange callback for change logging

### Dependencies

- Story 23-11: route registration
- ConfigService (existing), HealthService (existing)
- Provider instances for validation

## Testing Strategy

### Unit Tests

- [ ] ConfigAuditService: inventory returns all expected config keys
- [ ] ConfigAuditService: secrets are redacted (never returns full API keys)
- [ ] ConfigAuditService: validation catches missing required fields
- [ ] ConfigAuditService: diff shows only non-default values
- [ ] ConfigAuditService: validateProvider tests provider connectivity
- [ ] ConfigChangeLogger: records changes to ring buffer
- [ ] ConfigChangeLogger: drops oldest when exceeding maxEntries
- [ ] ConfigChangeLogger: redacts secret values before storage
- [ ] EnvVarChecker: detects set and unset environment variables
- [ ] EnvVarChecker: flags missing apiKeyRef references as errors
- [ ] Config routes: restore requires owner permission
- [ ] Config routes: inventory never leaks secrets

## Completion Checklist

- [ ] All 9 API endpoints implemented
- [ ] Secret redaction enforced at service level
- [ ] Config change logging instrumented in ConfigService
- [ ] Environment variable checker scans all known vars
- [ ] Provider/platform validation with temporary instances
- [ ] Tests written and passing
- [ ] TypeScript strict mode compiles
