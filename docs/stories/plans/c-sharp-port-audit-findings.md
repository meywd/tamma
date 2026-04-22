# C# Port Audit — Consolidated Findings

**Date**: 2026-04-18
**Method**: 8 parallel read-only audit agents, each comparing pre-delete TS (`git show 9e9a57c~1:packages/api/...`) against current C# (`apps/tamma-elsa/src/Tamma.Api/...`), scoped to non-overlapping surfaces. No agent was permitted to write code or fix gaps.

## Executive summary

**The Epic 19 TypeScript→C# migration is a partial rewrite, not a port.** Tests pass because they exercise the new code paths against new data — they never load a scrypt hash, a legacy JWT cookie, a TS-vocabulary agent config, or hit any of the stub endpoints with realistic workflow payloads.

A production cutover with existing data today would:

- Lock out **every email-password user** (scrypt → argon2id hash change)
- Invalidate **every JWT cookie** (claim shape: `tenantId` → `tid`, missing `platformRole`/`name`/`authMethod`)
- Invalidate **every API key** (scrypt → SHA256 hash change)
- Break the **nginx gateway for `elsa.tamma.dev` / `logs.tamma.dev`** (role-check endpoint doesn't map `?service=` to permission)
- Break **every Elsa workflow activity** (`/api/engine/execute-task` is a one-line stub with incompatible DTO; 12+ activities POST `{prompt, analysisType, role}` expecting `{success, output, costUsd, durationMs}`)
- Break **all GitHub-facing engine routes** (`/issues`, `/security-alerts`, `/issue-comment`, `/issue-labels`, `/create-issue`, `/trigger-ci`, `/repo-config` all stubs)
- Break **customer GitHub App onboarding** (OAuth callback is `// TODO: not yet implemented`; secrets provisioner to inject `TAMMA_API_KEY` into customer repos is gone)
- Enable **cross-tenant reads** on every `/api/v1/orgs/:tenantId/...` endpoint (`MemberAccess` policy doesn't verify path tenant)
- Drop **tenant isolation to app-layer only** (8 Postgres RLS policies from `010_rls_tenant_isolation.sql` entirely absent; EF filter becomes permissive when `TenantId` unset)
- Show **empty Knowledge Base pages with leaked "(stub)" strings** (sidecar wired, but composition root never constructs real ChromaDB/RAG/MCP backends)
- Silently **mis-report cancelled workflows as failed** (SaaS workflow-result collapses to binary)
- Lose **prompt-injection detection and SSRF guards** (sanitization collapsed from multi-policy ContentSanitizer to plain named-regex redactor)
- Lose **budget enforcement** (InMemoryBudgetConfigProvider returns `LimitUsd=0m`; nothing writes budgets)
- Make **provider execution fail for all non-HTTP providers** (Claude-Code, OpenCode, Zen MCP, OpenRouter, z.ai, local LLMs all fall through to OpenAI-shaped POST against `provider` as hostname → 404)
- Report **cost = $0 on every LLM call** (`HttpProviderClient.InvokeAsync` hardcodes cost to 0m with a comment deferring enrichment)

## Total effort estimate

| Scope | Hours |
|---|---:|
| Auth / Users / Sessions / API Keys | 70-85 |
| Orgs / Tenants / Memberships | 36-48 |
| Providers / Agents / Diagnostics / Sanitization | 70-95 |
| Prompts / Conventions | 1-2 |
| Engine / Workflows / SaaS / Dashboard | 80-110 |
| GitHub Integration | 42-56 |
| Knowledge Base | 20-30 |
| Admin / Health / DB Schema | 40-50 |
| **Total** | **~360-475h** |

At 5 productive dev hours/day that's **~12-16 weeks** serial, **~4-6 weeks** with four parallel streams.

## P0 — Production-blocking (ship nothing to existing-data customers without these)

### Authentication cutover incompatibility

1. **Password hash algorithm** — TS=scrypt, C#=Argon2id. `PasswordService.VerifyPassword` rejects any hash without `$argon2id$`. Implement dual-verify: if hash starts with `scrypt:`, verify via scrypt; if it starts with `$argon2id$`, verify via argon2. Re-hash with argon2 on successful login. **4h**

2. **JWT claim shape** — restore `tenantId` (not `tid`), add `platformRole`, `name`, `authMethod`. Existing cookies in browsers must either continue to parse, or implement a forced-logout migration banner. **2h + coordination**

3. **API key hash algorithm** — TS=scrypt, C#=SHA256 (`ApiKeyAuthHandler.cs:31`). Existing `api_keys.key_hash` rows cannot be validated. Implement dual-lookup or re-issue all keys before cutover. **2h**

4. **Cookie content** — `tamma_session` holds refresh token in C#, access JWT in TS. Domain missing in C#. Dashboard/nginx/`/api/auth/me` all break. Restore: access JWT in cookie, 900s maxAge, `domain=.tamma.dev`. **2h**

### Engine contract breakage (blocks every Elsa workflow)

5. **`POST /api/engine/execute-task`** — `EngineEndpoints.cs:93` is a one-line stub returning `{message:"(stub)"}`. DTO is `ExecuteTaskRequest(string TaskType, object? Context)` — wrong shape. 12+ Elsa activities POST `{prompt, analysisType, role}` expecting `{success, output, costUsd, durationMs, tokensUsed}`. Wire to `IRoleBasedAgentResolver` + agent execution + proper response DTO. **8-12h**

6. **`/api/engine/cycle-result`** drops `exitReason` + `error` fields. Fix DTO. **2h**

7. **All 7 `/api/engine/*github*` routes are stubs.** Port via Octokit.NET + installation tokens. **~30h**

### Tenant isolation holes

8. **Every `/api/v1/orgs/:tenantId/...` endpoint allows cross-tenant access** — `MemberAccess` policy only checks JWT has *some* membership, not in the path tenant. Add per-handler `GetMembership(path.tenantId, jwt.sub)` check or a `requireTenant` policy that binds the path-tenant claim. **6h**

9. **EF query filter permissive when TenantId null** — `HasQueryFilter(e => tenantId == null || e.TenantId == tenantId)` returns **all tenants' rows** when context unset. `TenantContextMiddleware` should 403 on failed resolution (TS behavior); currently just calls `next`. **4h**

10. **RLS policies absent** — 8 policies from `010_rls_tenant_isolation.sql` not reproduced. `prevent_tenant_id_change` trigger missing. `tamma_app` non-superuser role missing. Defense-in-depth lost — any raw SQL bypasses. **15-20h**

11. **`EventRepository` uses `IgnoreQueryFilters()` everywhere** — callers passing `tenantId=null` (e.g. `WorkflowEndpoints.GetInstanceEvents`) get cross-tenant events. **4h**

### Customer GitHub onboarding broken

12. **OAuth callback is `// TODO: not yet implemented`** (`AuthEndpoints.cs:387-391`). GitHub SSO returns `{message: "not yet implemented"}`. **12-14h**

13. **OAuth start has no `state` parameter** — CSRF on login flow. `AuthEndpoints.cs:383`. **4-6h**

14. **Webhook signature fail-open when `GitHub:WebhookSecret` empty** — `GitHubEndpoints.cs:124`. Add fail-closed guard in `Program.cs`. **1h**

15. **Secrets provisioner gone** — no libsodium sealed-box implementation; customer repos never receive `TAMMA_API_KEY`. `ApiKeyRotationService.cs:13-16` explicitly admits it. **10-12h**

16. **Install-callback doesn't fetch from GitHub** — `AppId=0`, `AccountLogin=tenant.Slug` placeholder. **16-20h**

### Email verification stub

17. **`POST /api/v1/auth/verify-email`** hashes token and returns 200 without looking up user, checking expiry, or marking verified. `AuthEndpoints.cs:103-110`. **4h**

18. **Login doesn't check `EmailVerified`** — unverified users can log in. **0.5h**

## P1 — Major capability gaps (ship OK but features broken)

### Provider execution stub

19. **`HttpProviderClient.InvokeAsync`** only handles Anthropic + "OpenAI-shaped" HTTP. All CLI-agent providers (Claude-Code, OpenCode, Zen MCP, OpenRouter, Gemini CLI, z.ai, local LLMs) fall through to OpenAI-style POST against `provider` as hostname → 404/connect-refused. Port `IAgentProvider` hierarchy from `packages/providers/`. **25-40h**

20. **Cost accounting hardcoded to $0** — `HttpProviderClient.cs:145, 169` returns `cost=0m` with comment deferring to "cost-monitor (Epic 9)" that doesn't exist. **3h**

### Role/phase taxonomy mismatch

21. **C# role/phase vocabulary doesn't overlap with TS.** TS `implementer` ↔ C# `developer`; TS `analyst`/`scrum_master` have NO C# equivalent. Every persisted TS-era agent config fails `ValidateConfigShape`. Decide canonical taxonomy, write mapping migration. **8-12h**

### Sanitization downgrade

22. **Prompt-injection detection gone** — TS `ContentSanitizer` with prompt-injection heuristics + URL validation + fetch-size cap + gateActions replaced by plain named-regex redactor. SSRF reopened. **14-20h**

23. **No `direction: 'input' | 'output'`** distinction — same rules applied to agent input + output identically.

### Refresh token rotation broken

24. **`/api/v1/auth/refresh`** returns new access token without revoking the old refresh token or issuing a new one. No reuse-detection family logic. Stolen refresh tokens good for 7 days undetected. **3h**

### Privilege escalation

25. **No RBAC on `AgentEndpoints`/`SettingsEndpoints`/`ProviderEndpoints`** — any tenant member can edit agent configs, reset circuit breakers, poison cost reports, rewrite sanitization rules. TS had `requirePermission('settings:manage')` hooks. **3-4h**

26. **Admin `UpdateUserRole` has no self-protection, no owner-only-promotes check** — any admin can promote self to owner, demote owner. No last-owner guard on membership remove. Transfer-ownership is non-atomic. **6h**

### Knowledge Base empty-state

27. **Sidecar composition root never constructs real backends.** `packages/intelligence-server/src/server.ts:210` calls `startServer()` without `IntelligenceServicesBundle`. `adaptVectorStore`/`adaptRagPipeline`/`createVectorStoreFromEnv` defined but never called. ChromaDB running in compose, but env-vars `CHROMADB_URL`/`OPENAI_API_KEY`/`EMBEDDING` appear **zero times** in sidecar source. Wire composition root. **8-12h**

28. **Four user-visible "(stub)" string leaks in KB responses** — `IndexManagementService.triggerIndex`, `VectorDbManagementService.upsert/delete`, `McpManagementService.startServer/stopServer`. **2h**

### SaaS surfaces drifted

29. **`POST /workflows/:id/result` collapses `completed|failed|cancelled` to binary** — cancelled workflows counted as failures. **3h**

30. **`POST /workflows/:id/status` drops `step`, `progress`, `message`** — dashboard "current step" stuck. **3h**

31. **Key rotation doesn't re-provision to repos** — deployed engines keep old key. **8h** (depends on #15 secrets provisioner)

### Engine lifecycle

32. **Engine Registry doesn't exist** — `DashboardEndpoints.GetEngines()` always empty. Multi-engine deployments impossible. No heartbeat tracking, no crash detection. **16-20h**

33. **SSE streams replaced with one-shot JSON** for `/events/state`, `/events/logs`, `/workflows/instances/:id/events`. Dashboard live updates break. **12-16h**

### Invite flow non-functional

34. **Org invites**: Guid token (122 bits vs TS 256); no email sent; **raw token returned in HTTP response body** (leaks to access logs). **4h**

## P2 — Correctness & observability

35. **Diagnostics taxonomy collapsed** — `provider_diagnostics` missing `eventType`, `agentType`, `projectId`, `engineId`, `taskId`, `taskType`, `correlationId`, `errorCode`, separate `inputTokens`/`outputTokens`. Billing accuracy regression, cross-request tracing broken. **8h**

36. **Diagnostics report groups by time only** — TS groups by provider/model/agentType. Cost-by-model dashboards impossible. **6-10h**

37. **Budget enforcement is a no-op** — `InMemoryBudgetConfigProvider.LimitUsd=0m`. Add persistence + endpoint. **4-6h**

38. **`taskOverrides` clamping lost** — no runtime per-call budget/tool/permission intersection. Privileged roles can't be scoped down per task. **6-8h**

39. **Case-insensitive email index missing** — `idx_users_email_lower` from `002_users.sql` gone. Login comparisons become case-sensitive. **1h**

40. **`users.settings JSONB` column missing** — user-level provider config has no home. **2h + migration**

41. **`users.email` NOT NULL** — GitHub OAuth users without public emails cannot persist. **1h**

42. **`github_installations.app_id`, `users.github_id` narrowed bigint → integer** — 2^31 overflow risk for large GitHub accounts. **1h + migration**

43. **Missing partial active-row indexes** — `idx_api_keys_active WHERE revoked_at IS NULL` + equivalents on refresh_tokens, password_reset_tokens, provider_health. Hot-path auth lookups degrade to full scans at scale. **2h**

44. **Missing CHECK constraints** on role/plan/auth_method/scope/account_type. **1h**

45. **Installation router has no 60s-TTL cache** — every webhook hits DB. **4h**

46. **Admin `/api/admin/health` regressed to trivial stub** — 6 parallel service pings (Postgres, Elsa, OpenSearch, RabbitMQ, ChromaDB) gone. **6-8h**

47. **No webhook idempotency** on `X-GitHub-Delivery` — retry re-enqueues task. **6-8h**

48. **No rate limiting** on GitHub webhooks, OAuth endpoints, sanitization, user-api-key creation. **4-6h**

## P3 — Behavioural / contract drift

49. Login has no constant-time dummy hash for unknown users — timing oracle for email enumeration. **1h**

50. `requireSelfOrRole` has no C# equivalent — users cannot manage their own API keys. **3h**

51. `resend-verification` and `password-reset/request` have no rate limit (abusable as outbound email spam). **2h**

52. `password-reset/request` sends to GitHub-only users (silently flips auth_method). **1h**

53. `password-reset/confirm` has no password strength check. **0.5h**

54. Health endpoint has no `/live` vs `/ready` split. **1-2h**

55. Prompt system prompt templates — 16/80 templates have whitespace diffs (plan-review + code-review × 8 roles). Intentional per header comment but claimed "byte-for-byte." **1h**

56. `cpp` convention template drops "for readability". **5min**

57. Render endpoint response field names changed (`renderedTemplate` → `UserPrompt`). **1h**

58. `PUT /api/prompts/system/:role/:action` semantic drift — writes user override instead of updating system default. **1h**

59. Missing prompt endpoints: `/api/prompts/defaults`, `/defaults/:action`, `/defaults/:role/:action`, `POST /:role/:action/reset`. **2h**

60. `EmitCreatedAsync`/`EmitResetAsync` in PromptEventsService are dead code — never called. **0.5h**

61. Service-key prefix — CLAUDE.md says `tk_pl_`; both TS and C# use `tamma_sk_`. Documentation drift. **doc-only**

62. Service keys silently tenant-bound in C# (TS set `tenantId: null`). Breaks cross-tenant platform ops. **1h**

63. `DELETE /orgs/:id` is one-phase destructive — no HMAC confirmation, no `last_tenant` guard, no cascade, no event. **8h**

64. `POST /auth/switch-org` doesn't update cookie — dashboard org switcher broken. **2h**

65. Circuit breaker behaviour drift — C# resets failure count on 60s window; TS never reset without a success. "Long-tail flaky" providers no longer trip. **doc-only**

## Dead tables / data-model losses

- `user_installations` — gone; multi-install-per-user flows broken
- `tenant_invites` (Epic 18 spec) — absent; conflated with `user_invites`
- `engine_events` renamed to `domain_events` with lost `timestamp BIGINT` (ms-precision for DCB replay)
- `github_installations` PK changed from native `installation_id BIGINT` to surrogate uuid
- `sanitization_rules` flattened to `Rules jsonb` — 6 typed columns gone, UNIQUE(tenant_id) gone
- `agent_configs.TenantId` — no unique index, no FK → duplicate rows + nondeterministic GetAsync
- `provider_health` — no unique on `(ProviderKey, TenantId)` → concurrent RecordFailure races create duplicate rows → circuit state diverges
- 18 archived SQL migrations' partial indexes + triggers all absent

## Recommended remediation order

### Wave 1 (P0, ~6-8 weeks one team)
Block any customer-facing cutover until these are done:

1. Auth compatibility layer (scrypt dual-verify, JWT claim restore, cookie content, API-key dual-hash) — 10h
2. `execute-task` DTO + implementation — 12h
3. GitHub engine routes port (Octokit) — 30h
4. OAuth callback + state — 18h
5. Webhook signature fail-closed — 1h
6. Secrets provisioner — 12h
7. Install-callback enrichment — 20h
8. Email verification — 4h + login gate 0.5h
9. Cross-tenant path-tenant check on orgs routes — 6h
10. EF filter null-permissive fix + middleware 403 — 4h
11. RLS policy restoration — 20h
12. EventRepository tenant leak — 4h

### Wave 2 (P1, ~4-5 weeks parallel streams)
Onboard new customers after Wave 1; restore feature depth:

- Provider execution port (IAgentProvider hierarchy) — 40h
- Role/phase vocabulary reconciliation — 12h
- Sanitization multi-policy port — 20h
- Refresh token rotation — 3h
- Settings RBAC restoration — 4h
- KB sidecar composition root + ChromaDB wiring — 12h
- SaaS workflow DTO restorations — 9h
- Engine registry + SSE — 36h
- Invite flow (email, token-in-body) — 4h

### Wave 3 (P2/P3, ~2-3 weeks)
Correctness and observability cleanup. Largely parallelizable.

### Wave 4 (data-model)
Migration authoring to restore missing columns/indexes/CHECK constraints/triggers. Coordinate with any data already in production DBs.

## Next steps

Before dispatching implementation agents:

1. **Confirm this audit is accurate** — spot-check 3 findings against live code.
2. **Decide Wave 1 priority** — are we deferring customer cutover or doing compat-layer work to survive existing data?
3. **Decide the role/phase taxonomy** — canonical vocabulary must be chosen before any provider/agent work can proceed.
4. **Schedule Wave 1 as stories** in Epic 19.5 or a new epic (this is ~30 stories' worth of work).
