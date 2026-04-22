# Providers / Agents / Diagnostics / Health / Sanitization Port-Gap Findings

Audit root: `/home/meywd/tamma/docs/audit/port-gaps/providers/`
Source commit (pre-delete): `9e9a57c~1` (TS `packages/api/src/routes/(agents|settings)/*`, `packages/api/src/services/{agent-resolver,*-store,provider-session,settings/*}.ts`, `packages/api/src/persistence/{,pg-}agent-config-store.ts`)
Target: `apps/tamma-elsa/src/Tamma.Api/` on `feat/auth-foundation`

Severity legend:
- **P0** — cutover-blocking; existing persisted data or deployed behaviour breaks on day 1
- **P1** — feature broken but not data-destroying
- **P2** — correctness / hardening regression
- **P3** — contract drift / low-impact
- **None** — positive finding (C# is at or above TS)

| # | File | Sev | Summary |
|---|------|-----|---------|
| 001 | `001-role-phase-vocabulary-schism.md` | P0 | TS `AgentType` (9 roles: `scrum_master`,`implementer`,`reviewer`,…) + `WorkflowPhase` (8 UPPER_SNAKE) vs C# 8 roles (`developer`,`senior_developer`,…) + 10 lowercase-hyphen actions. Taxonomies don't map; TS-era `agent_configs.config` JSONB fails `ValidateConfigShape`. |
| 002 | `002-settings-rbac-status.md` | P2 | C# actually HAS RBAC (`RequireAuthorization("SettingsView"/"SettingsManage")` at group + per-endpoint level). Minor drift: `/providers/chain/resolve` is `SettingsView` not `SettingsManage`; `POST /sanitize` is `SettingsManage` not `SettingsView`. The audit-summary original P0 claim of "no RBAC" is overstated. |
| 003 | `003-provider-execution-stub-cli-agents.md` | P0 | `HttpProviderClient` whitelists 4 providers (Anthropic, OpenAI, Copilot, Gemini). Claude-Code, OpenCode, Zen MCP, OpenRouter, z.ai, local LLMs fall through to a generic POST against a mis-configured `HttpClient` and error out. Biggest single gap; 25-40h to port CLI-agent subprocess + MCP transport. |
| 004 | `004-cost-accounting-hardcoded-zero.md` | P0 | `HttpProviderClient.ParseResponse` returns `cost = 0m` for both Anthropic and OpenAI branches. No pricing table ported. Every `ProviderDiagnostic` row has `Cost = 0` → budget `spent = 0` → enforcement never fires. |
| 005 | `005-budget-enforcement-no-op.md` | P0 | `InMemoryBudgetConfigProvider` returns `LimitUsd = 0` unconditionally. No endpoint, no table, no persistence. `IsOverBudget` always `false`. Cost runaway silent. |
| 006 | `006-prompt-injection-detection-gone.md` | P1 | `ContentSanitizer` (HTML strip, zero-width strip, injection heuristics × 5, URL validation with private-IP octet parse, action gating, secure fetch size cap, input/output direction) deleted. C# `SanitizationService` is a pure regex-replace redactor with none of the 6 required behaviours from Story 9-7 AC 6. |
| 007 | `007-task-overrides-clamping-lost.md` | P1 | TS 3-level merge (`defaults < role < taskOverrides`) with budget `Math.min`, permission env-gate, tool intersection. C# has 2-level merge only; `ResolveForPhaseAsync` doesn't accept `taskOverrides`. Security downgrade — privileged role can't be scoped down per task. |
| 008 | `008-diagnostics-taxonomy-collapsed.md` | P1 | 7 columns dropped from `ProviderDiagnostic` entity: `event_type`, `agent_type`, `project_id`, `engine_id`, `task_id`, `task_type`, `correlation_id`, `error_code`. `input_tokens`+`output_tokens` collapsed to `TokensUsed`. Cross-request tracing broken; cost recomputation impossible. |
| 009 | `009-diagnostics-report-groupby-dropped.md` | P1 | TS `/diagnostics/report?groupBy=provider\|model\|agentType` returned per-dimension buckets. C# returns time-bucketed (`?bucketSize=5m\|hour\|day`) across all providers. No attribution axis. |
| 010 | `010-diagnostics-batch-ingest-missing.md` | P2 | TS `POST /diagnostics` accepted single record OR array (up to 100). C# binds one `IngestDiagnosticRequest` only; array → `400`. 50x HTTP overhead for batched emitters. |
| 011 | `011-provider-chain-schema-mismatch.md` | P1 | TS persisted `{roles:{developer:{providerChain:[...]}}}`. C# resolver reads `{chains:{developer:{implement:[...],default:[...]}}}`. TS-era rows return `EMPTY_PROVIDER_CHAIN` from `ProviderChainResolver`. |
| 012 | `012-health-api-response-shape.md` | P2 | TS `GET /health/providers/:key` returned `200` with synthesized healthy body when unseen. C# returns `404`. Fields renamed: `failures`→`failureCount`, `halfOpen`→`halfOpenInProgress`, added `state` enum + `status` string. `GET /health` shape changed from keyed map to array wrapper. |
| 013 | `013-health-key-validation-missing.md` | P2 | TS validated `:key` against `/^[a-zA-Z0-9._\-:/]+$/` + max 256 length at route. C# only checks length in `CircuitBreakerService.ValidateKey`; no char-set regex. Keys with whitespace, newlines, or control chars persist to the DB. |
| 014 | `014-agent-config-crud-validation-gaps.md` | P2 | C# `ValidateConfigShape` only checks JSON + root-is-object + role-is-known. TS enforced 6 semantic rules (Story 9-1 AC 6): provider-name regex, budget range `[0,100]`, ReDoS-shape rejection, `blockedCommandPatterns` compile, `maxFetchSizeBytes` range, `bypassPermissions` env-gate. C# implements 0 of 6. |
| 015 | `015-sanitization-data-model-rewrite.md` | P1 | TS `sanitization_rules` had 6 typed fields (`extra_injection_patterns[]`, `blocked_command_patterns[]`, `max_fetch_size_bytes`, `validate_urls`, `gate_actions`, `enabled`). C# `SanitizationRule` has `{Id, TenantId, Rules jsonb}` where `Rules` is an opaque array of regex-replace rules. Shapes are not interconvertible. |
| 016 | `016-sanitization-missing-unique-tenant.md` | P2 | Archived `016_sanitization_rules.sql` declared `UNIQUE (account_id)`. C# EF model for `SanitizationRule` has no `HasIndex(e => e.TenantId).IsUnique()`. `LoadOrCreateRowAsync` races can produce duplicate rows per tenant; `GetRulesAsync` returns a nondeterministic one. |
| 017 | `017-sanitization-missing-cascade-fk.md` | P3 | Archived SQL `016_sanitization_rules.sql:12` had `account_id REFERENCES tenants(id) ON DELETE CASCADE`. C# EF model declares no `HasOne(Tenant).WithMany().OnDelete(Cascade)`. Tenant deletion orphans child rows in `sanitization_rules`, `agent_configs`, `provider_health`, `provider_diagnostics`. |
| 018 | `018-user-scoped-providers-put-no-op.md` | P1 | TS `PUT /providers` was user-scoped (`IUserStore.updateUserSettings`). C# `PUT /api/config/providers` is a 1-line stub `return Results.Ok(new { message = "Providers config updated" })`. Body ignored; nothing persisted. |
| 019 | `019-prompts-config-put-no-op.md` | P1 | `PUT /api/config/prompts/{role}` stubbed to `return Results.Ok(new { message = "..." })`. TS version called `ConfigService.updatePromptTemplate` + ELSA Agents DB sync. C# has a separate working prompt store at `/api/prompts/{role}/{action}` (Story 12-5); should either route this through to it or delete the stub route. |
| 020 | `020-rate-limiting-missing-on-settings-routes.md` | P2 | TS used `@fastify/rate-limit` per-route (100/min read, 30/min write). C# has no `AddRateLimiter` / `UseRateLimiter` call anywhere. All settings/provider/agent endpoints unthrottled. |
| 021 | `021-agent-configs-unique-index-positive.md` | None | **Audit-summary claim "no unique index on TenantId" was incorrect.** C# declares `HasIndex(e => e.TenantId).IsUnique()` at `TammaDbContext.cs:246`. Minor drift: C# treats NULL as distinct so two system-default rows could coexist; not the same as TS `((1))` partial index. |
| 022 | `022-provider-health-unique-index-positive.md` | None | **Audit-summary claim "no unique on (ProviderKey, TenantId)" was incorrect.** C# declares `HasIndex(e => new { e.ProviderKey, e.TenantId }).IsUnique()` at `TammaDbContext.cs:282`. Plus per-tenant partitioning (TS was global). Minor drift: cold-start race in `GetOrCreateAsync` can produce `500` on first concurrent failure. |
| 023 | `023-diagnostics-missing-composite-indexes.md` | P2 | Archived 014 had 7 indexes: `(account_id,created_at DESC)`, `(provider_name,created_at DESC)`, `(model,…)`, `(event_type,…)`, `(engine_id,…)`, `(correlation_id) WHERE NOT NULL`, `(account_id,created_at) WHERE success=true`. C# has only `(ProviderKey, CreatedAt)`. Dashboard queries seq-scan. |
| 024 | `024-circuit-breaker-window-reset-semantic-change.md` | P3 | TS: failure counter accumulates until a success resets it (5 lifetime failures → open). C#: sliding 60s window (5 failures in 60s → open). Both compliant with Story 9-3 AC; different operational characteristics. Doc-only. |
| 025 | `025-sanitization-redos-defense-stronger-positive.md` | None | TS used write-time `NESTED_QUANTIFIER` heuristic. C# uses runtime 100ms `MatchTimeout` per rule — strictly stronger (catches all ReDoS shapes, not just nested-quantifier). Optional hardening: add TS's write-time heuristic as a warning layer. |
| 026 | `026-circuit-breaker-stronger-positive.md` | None | C# `CircuitBreakerService` has `TryProbeAsync` (atomic HalfOpen claim) + `ISystemClock` (testable) + per-tenant partitioning + separation of state-query from state-transition. TS had no atomic probe claim — in multi-replica deployments all replicas could probe simultaneously. |

**Total**: 26 findings. 4 P0 (001, 003, 004, 005), 7 P1 (006, 007, 008, 009, 011, 018, 019), 8 P2 (002, 010, 012, 013, 014, 016, 020, 023), 3 P3 (015 — P1 in table above; 017, 024), 1 P1 (015 counted P1), 3 positive findings (021, 022, 025, 026 — 4 positive).

Re-tally: **P0 × 4, P1 × 8, P2 × 8, P3 × 2, None × 4 = 26**.

## Recommended sequencing

1. **Schema hardening** (1.5h) — add the missing cascade FKs (finding 017), unique(TenantId) on sanitization (016), composite indexes on diagnostics (023). Small, low-risk, unblock subsequent work.
2. **Role / phase vocabulary reconciliation + migration** (10h) — finding 001. Blocks 007, 008, 011.
3. **RBAC and rate-limit polish** (5h) — findings 002 (minor) + 020.
4. **Diagnostics columns + indexes + report groupBy + batch ingest** (10h) — findings 008, 009, 010, 023 together.
5. **Cost table + budget persistence** (5h + 5h) — findings 004, 005.
6. **taskOverrides clamping + env-gate for bypassPermissions** (7h) — finding 007.
7. **Agent config semantic validation port** (5h) — finding 014.
8. **Sanitization data-model expansion + ContentSanitizer port** (16h + 14h = 30h) — findings 006, 015 together (largest logical block after provider execution).
9. **Provider chain schema migration + dual-read** (5h) — finding 011.
10. **User-scoped providers PUT implementation** (7h) — finding 018.
11. **Prompts-config settings-path decision** (1h or 3h) — finding 019.
12. **Health API response shape restoration** (3h) + key validation (1h) — findings 012, 013.
13. **Provider execution — largest deferred initiative** (30h) — finding 003. CLI-agent subprocess, MCP transport, per-provider adapter split. Do this last because it depends on cost enrichment (004), budget enforcement (005), and taxonomy reconciliation (001). Realistically a separate epic.

**Total (excluding provider execution port)**: ~45h. **With provider execution**: ~75h. (audit-summary estimated 70-95h and 40h specifically for the provider port, consistent).

## Inputs I could not locate

- **TS CLI-agent adapter sources.** `packages/providers/src/*-provider.ts` exist in the `9e9a57c~1` tree as referenced by Story 9-4, but the full implementations of `AnthropicClaudeProvider`, `ClaudeCodeProvider`, `OpenCodeProvider`, `ZenMcpProvider` are in the `packages/providers/` package, which I accessed via `git show 9e9a57c~1:packages/providers/src/...` only as needed (they weren't in my initial snapshot list). For the per-adapter port work in finding 003, the remediator will need to diff those sources.
- **TS `validateAgentsConfig` / `validateSecurityConfig`**. Referenced in `packages/shared/src/config/validate-*.ts`. I cited them by path but did not pull the bodies into the snapshot — the exact regex and range constants should come from the pre-delete snapshot when the port is executed (finding 014).
- **TS `ContentSanitizer` body**. Similarly in `packages/shared/src/security/content-sanitizer.ts`; finding 006 cites the file path but the 5 injection-category heuristics themselves were not extracted verbatim into the finding. The remediator should `git show 9e9a57c~1:packages/shared/src/security/content-sanitizer.ts` before porting.
- **Pricing table**. Mentioned in finding 004; the exact `$/token` values per `provider:model` live in `packages/providers/src/pricing.ts` (or similar) at `9e9a57c~1`; not extracted.

All architectural decisions, endpoint shapes, service boundaries, and data-model deltas have been documented against real source at both pre-delete and current commits, plus the relevant story / CLAUDE.md / archived SQL references.
