# CLI Mode Degradation: Service Fallback Behavior Without Postgres

Status: reference

## Purpose

This document defines what happens to each service when Tamma runs in CLI/standalone mode without a PostgreSQL database connection. All services must degrade gracefully -- no crashes, no unhandled errors, just reduced functionality with clear logging.

## Principle

CLI mode is the simplest deployment: a single `tamma` process on a developer's machine with no external dependencies. Every service that normally depends on Postgres must have an in-memory or file-based fallback that provides basic functionality without persistence.

## Service Fallback Matrix

| Service | Normal Mode (Postgres) | CLI Fallback (No Postgres) | Notes |
|---------|----------------------|---------------------------|-------|
| **Prompt Store** | `PgPromptStore` reading from `prompts`, `system_prompts`, `action_prompts` tables | `InMemoryPromptStore` seeded from `default-prompts.ts` | All 80 role+action templates, 8 system prompts, 10 action defaults loaded from code. No account overrides possible. |
| **Agent Config** | `PgAgentConfigStore` reading from `agent_configs` table | File-based via `normalizeAgentsConfig()` from `tamma.config.json` | Uses the existing CLI config path. `mergeConfig()` in `packages/cli/src/config.ts` handles resolution. |
| **Health Tracker** | `PgProviderHealthTracker` with shared state via Postgres/Redis | In-memory `ProviderHealthTracker` from `packages/providers/src/provider-health.ts` | Circuit breaker state is process-local, not shared. Resets on restart. Acceptable for single-process CLI. |
| **Diagnostics** | `PgDiagnosticsStore` with persistent cost tracking and budget enforcement | In-memory diagnostics with `DiagnosticsQueue` draining to console/log only | Cost data is logged but not persisted. Budget enforcement uses session-local counters. No historical reports. |
| **Content Sanitization** | `PgSanitizationRuleStore` with per-account rules | Default sanitization rules from code (`packages/shared/src/security/`) | System-default rules always applied. No account-specific overrides possible. |
| **Tenant Context** | `tenantId` from JWT, resolved from `tenants` table | `DEFAULT_TENANT_ID` sentinel (`00000000-0000-0000-0000-000000000000`) | All resources scoped to the default tenant. No multi-tenant isolation. |
| **User Store** | `PgUserStore` with full user model | Not applicable in CLI mode | CLI mode does not require user authentication. Operations run as the local user. |
| **Event Store** | Postgres-backed DCB event stream | In-memory event log (optional: file-based append-only log) | Events are captured for the session but not persisted across restarts. Useful for debugging. |
| **Refresh Tokens** | `PgRefreshTokenStore` | Not applicable in CLI mode | No login/session management in CLI mode. |
| **Org/Membership** | `PgOrgStore` with full multi-tenant model | Not applicable in CLI mode | Single implicit tenant with single implicit owner. |

## Detection Logic

The service initialization should detect whether Postgres is available:

```typescript
async function initializeServices(config: TammaConfig): Promise<ServiceContainer> {
  const pgAvailable = await testPostgresConnection(config.database);

  if (!pgAvailable) {
    logger.warn('PostgreSQL not available -- running in CLI fallback mode');
    logger.warn('Services will use in-memory/file-based backends. Data will not persist across restarts.');
  }

  return {
    promptStore: pgAvailable
      ? new PgPromptStore(pool)
      : new InMemoryPromptStore(getDefaultPrompts()),
    agentConfigStore: pgAvailable
      ? new PgAgentConfigStore(pool)
      : new FileAgentConfigStore(config),
    healthTracker: pgAvailable
      ? new PgProviderHealthTracker(pool)
      : new ProviderHealthTracker({ logger }),
    diagnosticsStore: pgAvailable
      ? new PgDiagnosticsStore(pool)
      : new InMemoryDiagnosticsStore({ logger }),
    sanitizationRules: pgAvailable
      ? new PgSanitizationRuleStore(pool)
      : new DefaultSanitizationRules(),
    tenantId: pgAvailable
      ? null  // resolved from JWT at request time
      : DEFAULT_TENANT_ID,
  };
}
```

## Logging

When running in fallback mode, each service should log at startup:

- `WARN` level: `"[ServiceName] running in fallback mode (no Postgres). Data will not persist."`
- `INFO` level: `"[ServiceName] initialized with N default entries"` (for seeded stores)

During operation, fallback services should log at `DEBUG` level when an operation that would normally persist data is discarded.

## Constraints

1. **No silent data loss**: If a service would normally persist data but is running in fallback mode, it must log a warning. The user should know that their CLI session's data will not survive a restart.
2. **No feature crashes**: All API endpoints and engine operations must work in CLI mode. If a feature requires Postgres (e.g., "list all tenants"), the endpoint should return the default tenant only, not a 500 error.
3. **No configuration required**: CLI mode should work with zero configuration. Default values for all services must be built into the code.
4. **Upgrade path**: When the user later connects Postgres, the transition should be seamless. No data migration from in-memory to Postgres is required (CLI sessions are ephemeral).

## References

- **Epic 9**: All API services have fallback behavior defined per-story
- **Epic 27**: `InMemoryPromptStore` seeded from `default-prompts.ts`
- **Epic 18**: Tenant model uses `DEFAULT_TENANT_ID` sentinel for CLI mode

---

**Last Updated**: 2026-04-09
**Owner**: Architecture Team
