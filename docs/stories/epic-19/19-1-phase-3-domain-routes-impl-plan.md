# Phase 3 Implementation Plan: Domain Routes Port (TS to C#)

**Epic**: 19 — API Consolidation  
**Story**: 19-1 — API Consolidation from TypeScript to C#  
**Phase**: 3 — Domain Routes (agents, prompts, settings, KB, convention-templates)  
**Estimated effort**: 44 hours  
**Total endpoints**: 58  
**Prerequisites**: Phase 1 (EF Core DbContext + Auth) and Phase 2 (core routes) complete  

---

## Table of Contents

1. [Route Group A: Agent Config + Resolver (5 endpoints)](#route-group-a-agent-config--resolver)
2. [Route Group B: Prompt Routes (9 endpoints)](#route-group-b-prompt-routes)
3. [Route Group C: Settings — Config Group (11 endpoints)](#route-group-c-settings--config-group)
4. [Route Group D: Settings — Providers Group (15 endpoints)](#route-group-d-settings--providers-group)
5. [Route Group E: Convention Templates (2 endpoints)](#route-group-e-convention-templates)
6. [Route Group F: Knowledge Base (30 endpoints)](#route-group-f-knowledge-base)
7. [Cross-Cutting Concerns](#cross-cutting-concerns)
8. [nginx Routing Changes](#nginx-routing-changes)
9. [Test Strategy Summary](#test-strategy-summary)
10. [Rollback Plan](#rollback-plan)

---

## Route Group A: Agent Config + Resolver

**TS source**: `packages/api/src/routes/agents/agent-config-routes.ts`, `agent-resolver-routes.ts`  
**C# target**: `Tamma.Api/Endpoints/Agents/`  
**Repository**: `IAgentConfigRepository` (from Phase 1 Task 1.6)  

### Endpoint Mapping (5 endpoints)

| # | Method | Path | TS Handler | C# Endpoint Class | C# Method |
|---|--------|------|------------|-------------------|-----------|
| 1 | GET | `/api/v1/agents/config` | `agent-config-routes.ts` GET /config | `AgentConfigEndpoints.cs` | `GetConfig` |
| 2 | PUT | `/api/v1/agents/config` | `agent-config-routes.ts` PUT /config | `AgentConfigEndpoints.cs` | `UpsertConfig` |
| 3 | POST | `/api/v1/agents/config/validate` | `agent-config-routes.ts` POST /config/validate | `AgentConfigEndpoints.cs` | `ValidateConfig` |
| 4 | GET | `/api/v1/agents/{role}/resolve` | `agent-resolver-routes.ts` GET /:role/resolve | `AgentResolverEndpoints.cs` | `ResolveForRole` |
| 5 | POST | `/api/v1/agents/resolve-for-phase` | `agent-resolver-routes.ts` POST /resolve-for-phase | `AgentResolverEndpoints.cs` | `ResolveForPhase` |

### Tasks

**Task A.1: Create DTOs** (`Tamma.Api/Models/Agents/`)
- `AgentConfigResponse.cs` — maps to TS `{ config, security, source, version }`
- `AgentConfigUpsertRequest.cs` — maps to TS `PutBody { config?, security? }`
- `AgentConfigValidateRequest.cs` — maps to TS `ValidateBody`
- `AgentResolveForPhaseRequest.cs` — maps to TS `ResolveForPhaseBody { phase, projectId?, engineId?, taskOverrides? }`
- `TaskOverridesDto.cs` — maps to TS `taskOverrides` nested object
- Port the `VALID_PHASES` set and `FORBIDDEN_KEYS` set as static `HashSet<string>` constants

**Task A.2: Create AgentResolverService** (`Tamma.Api/Services/AgentResolverService.cs`)
- Interface `IAgentResolverService` with methods:
  - `ResolveForRole(accountId, role, options)` — resolves agent config from `IAgentConfigRepository`
  - `ResolveForPhase(accountId, phase, options)` — maps workflow phase to agent role, then resolves
- Port resolution logic from TS `packages/api/src/services/agent-resolver.ts`
- Inject `IAgentConfigRepository` (Phase 1) via constructor DI
- Validate role name length (1-64 chars) and prototype pollution guard (`__proto__`, `constructor`, `prototype`)

**Task A.3: Create AgentConfigValidator** (`Tamma.Api/Services/AgentConfigValidator.cs`)
- Port `validateConfigDocument()` logic from TS — validates both agents config and security config
- Use FluentValidation or manual validation matching TS `validateAgentsConfig()` / `validateSecurityConfig()`
- Return `string[]` error array for compatibility with existing API contract

**Task A.4: Register Minimal API endpoints** (`Tamma.Api/Endpoints/Agents/AgentConfigEndpoints.cs`, `AgentResolverEndpoints.cs`)
- Use `MapGroup("/api/v1/agents")` to register route group
- `GetConfig`: extract accountId from auth claims (fallback to `00000000-...` for CLI mode), call `IAgentConfigRepository.Resolve()`
- `UpsertConfig`: validate body, merge with existing config, save via repository
- `ValidateConfig`: validate without persisting, return `{ valid, errors }`
- `ResolveForRole`: validate role param, call `IAgentResolverService.ResolveForRole()`
- `ResolveForPhase`: validate phase against `VALID_PHASES`, call `IAgentResolverService.ResolveForPhase()`
- Rate limiting: 100 req/min for reads, 30 req/min for writes (use ASP.NET Core rate limiting middleware)

**Task A.5: Register DI** (`Program.cs` additions)
- `builder.Services.AddScoped<IAgentResolverService, AgentResolverService>()`
- `builder.Services.AddScoped<AgentConfigValidator>()`

### Test Strategy (20 tests)

| Test Category | Count | What to Test |
|---|---|---|
| GET /config — default config | 2 | Returns valid default when no override exists; returns tenant-specific override |
| PUT /config — upsert | 4 | Happy path; validation error; partial update (config only, security only); merge with existing |
| POST /config/validate | 3 | Valid config returns `{ valid: true }`; invalid returns errors; empty body returns 400 |
| GET /:role/resolve | 4 | Valid role; unknown role fallback; forbidden role name (400); role length validation |
| POST /resolve-for-phase | 4 | Valid phase; invalid phase (400); with taskOverrides; missing phase field (400) |
| Auth edge cases | 3 | No auth = default accountId; tenant isolation; invalid JWT returns 401 |

**Test approach**: EF Core InMemory provider for `IAgentConfigRepository`. Mock `IAgentResolverService` for endpoint-level tests. Integration tests use `WebApplicationFactory<Program>`.

---

## Route Group B: Prompt Routes

**TS source**: `packages/api/src/routes/prompts/prompt-routes.ts`  
**C# target**: `Tamma.Api/Endpoints/Prompts/PromptEndpoints.cs`  
**Repository**: `IPromptRepository` (from Phase 1 Task 1.6, backed by `prompt_overrides` table)  

### Endpoint Mapping (9 endpoints)

| # | Method | Path | C# Method | Auth |
|---|--------|------|-----------|------|
| 1 | GET | `/api/prompts/system` | `ListSystemDefaults` | Any authenticated |
| 2 | GET | `/api/prompts/system/{role}/{action}` | `GetSystemDefault` | Any authenticated |
| 3 | PUT | `/api/prompts/system/{role}/{action}` | `UpsertSystemDefault` | Platform admin only |
| 4 | DELETE | `/api/prompts/system/{role}/{action}` | `ResetSystemDefault` | Platform admin only |
| 5 | GET | `/api/prompts` | `ListResolved` | Any authenticated |
| 6 | GET | `/api/prompts/{role}/{action}` | `GetResolved` | Any authenticated |
| 7 | PUT | `/api/prompts/{role}/{action}` | `UpsertTenantOverride` | Any authenticated |
| 8 | DELETE | `/api/prompts/{role}/{action}` | `DeleteTenantOverride` | Any authenticated |
| 9 | POST | `/api/prompts/{role}/{action}/render` | `RenderPrompt` | Any authenticated |

### Tasks

**Task B.1: Create DTOs** (`Tamma.Api/Models/Prompts/`)
- `PromptUpsertRequest.cs` — `{ template, variables?, systemPrompt?, enableTools?, maxTokens? }`
- `PromptRenderRequest.cs` — `{ variables: Dictionary<string, string> }`
- `PromptTemplateResponse.cs` — standard prompt template shape
- `PromptListResponse.cs` — `{ templates, total }`

**Task B.2: Create PromptStoreService** (`Tamma.Api/Services/PromptStoreService.cs`)
- Interface `IPromptStoreService` with methods:
  - `ListSystemDefaults()` — returns all hardcoded + DB-overridden system defaults
  - `GetSystemDefault(role, action)` — single system default lookup
  - `UpsertSystemDefault(role, action, input, userId?)` — admin override of system default
  - `ResetSystemDefault(role, action, userId?)` — delete DB override, restore hardcoded
  - `List(tenantId?)` — resolved list (tenant overrides merged with system defaults)
  - `Get(tenantId?, role, action)` — resolved single prompt
  - `Upsert(tenantId, role, action, input, userId?)` — create/update tenant override
  - `Delete(tenantId, role, action, userId?)` — delete tenant override
  - `Render(tenantId?, role, action, variables)` — resolve + interpolate `{{variable}}` placeholders
- Port resolution order: user role+action override > system default role+action > user action default > system action default
- Port template rendering with `{{variable}}` interpolation (simple string replace, not Handlebars)

**Task B.3: Port hardcoded system defaults** (`Tamma.Api/Data/DefaultPrompts.cs`)
- Port from TS `packages/api/src/services/default-prompts.ts` (or `packages/shared`)
- Static dictionary of `(role, action) -> PromptTemplate` for 80 role+action combinations
- These are the "immutable" defaults that `ResetSystemDefault` restores to

**Task B.4: Validation logic**
- Template: non-empty string, max 500,000 chars
- Variables: must be `string[]` if provided
- MaxTokens: positive finite number
- Render variables: all values must be strings
- Platform admin check for system default writes: `User.IsInRole("owner")`

**Task B.5: Register Minimal API endpoints** (`Tamma.Api/Endpoints/Prompts/PromptEndpoints.cs`)
- **IMPORTANT**: Register `/api/prompts/system` routes BEFORE parametric `/{role}/{action}` routes to avoid ASP.NET routing ambiguity (same issue as in TS Fastify)
- TenantId resolution: `HttpContext.Items["TenantId"]` (from `TenantContextMiddleware`) > `X-Tenant-Id` header > null
- UserId passthrough for event sourcing audit trail

### Test Strategy (25 tests)

| Test Category | Count | What to Test |
|---|---|---|
| System defaults CRUD | 6 | List all; get existing; get non-existent (404); upsert (admin); delete/reset (admin); non-admin 403 |
| Tenant-scoped CRUD | 6 | List resolved (merged); get resolved; upsert override; delete override; delete non-existent (404); null tenant fallback |
| Render | 4 | Happy path interpolation; missing template (404); invalid variables (400); partial variable substitution |
| Validation | 5 | Empty template (400); oversized template (400); invalid maxTokens (400); invalid variables type (400); valid body accepted |
| Multi-tenant isolation | 4 | Tenant A cannot see Tenant B overrides; system defaults visible to all; override masks system default; delete reveals system default |

---

## Route Group C: Settings — Config Group

**TS source**: `packages/api/src/routes/settings/` (agents, security, prompts, providers sub-routes)  
**C# target**: `Tamma.Api/Endpoints/Settings/`  
**Prefix**: `/api/config`  
**RBAC**: GET requires `settings:view` (admin, owner); PUT/POST requires `settings:manage` (owner only)  

### Endpoint Mapping (11 endpoints)

| # | Method | Path | TS Source | C# Endpoint Class | C# Method |
|---|--------|------|-----------|-------------------|-----------|
| 1 | GET | `/api/config/agents` | `agents-routes.ts` | `AgentsSettingsEndpoints.cs` | `GetAgentsConfig` |
| 2 | PUT | `/api/config/agents` | `agents-routes.ts` | `AgentsSettingsEndpoints.cs` | `UpdateAgentsConfig` |
| 3 | GET | `/api/config/security` | `security-routes.ts` | `SecuritySettingsEndpoints.cs` | `GetSecurityConfig` |
| 4 | PUT | `/api/config/security` | `security-routes.ts` | `SecuritySettingsEndpoints.cs` | `UpdateSecurityConfig` |
| 5 | POST | `/api/config/sanitize` | `security-routes.ts` | `SecuritySettingsEndpoints.cs` | `SanitizeContent` |
| 6 | GET | `/api/config/sanitize/rules` | `security-routes.ts` | `SecuritySettingsEndpoints.cs` | `GetSanitizationRules` |
| 7 | PUT | `/api/config/sanitize/rules` | `security-routes.ts` | `SecuritySettingsEndpoints.cs` | `UpdateSanitizationRules` |
| 8 | GET | `/api/config/prompts` | `prompts-routes.ts` | `PromptsSettingsEndpoints.cs` | `GetPromptTemplates` |
| 9 | PUT | `/api/config/prompts/{role}` | `prompts-routes.ts` | `PromptsSettingsEndpoints.cs` | `UpdatePromptTemplate` |
| 10 | GET | `/api/config/providers` | `providers-routes.ts` | `ProvidersSettingsEndpoints.cs` | `GetUserProviders` |
| 11 | PUT | `/api/config/providers` | `providers-routes.ts` | `ProvidersSettingsEndpoints.cs` | `UpdateUserProviders` |

### Tasks

**Task C.1: Create DTOs** (`Tamma.Api/Models/Settings/`)
- `AgentsConfigDto.cs` — mirrors TS `IAgentsConfig`
- `SecurityConfigDto.cs` — mirrors TS `SecurityConfig`
- `SanitizeRequest.cs` — `{ content: string, direction: "input" | "output" }`
- `SanitizeResponse.cs` — `{ result: string, warnings: string[] }`
- `SanitizationRulesDto.cs` — mirrors TS `SanitizationRulesInput`
- `ProvidersConfigDto.cs` — mirrors TS `IProvidersConfig`
- `PromptTemplateUpdateRequest.cs` — `{ systemPrompt?, providerPrompts? }`

**Task C.2: Create ConfigService** (`Tamma.Api/Services/ConfigService.cs`)
- Interface `IConfigService` with methods:
  - `GetAgentsConfig()` / `UpdateAgentsConfig(config)`
  - `GetSecurityConfig()` / `UpdateSecurityConfig(config)`
  - `GetPromptTemplates()` / `UpdatePromptTemplate(role, body)`
  - `GetUserProviders(userId)` / `UpdateUserProviders(userId, config)`
- Backed by `IAgentConfigRepository` for agents/security config
- Backed by `IPromptRepository` for settings-level prompt templates
- User providers: scoped to authenticated user ID

**Task C.3: Create ISanitizationRepository** (`Tamma.Data/Repositories/ISanitizationRepository.cs`)
- Methods: `GetRules(accountId)`, `UpsertRules(accountId, input)`, `Sanitize(accountId, content, direction)`
- EF Core implementation backed by `sanitization_rules` table (migration 016)
- Sanitize method: load rules for account, apply regex patterns based on direction
- Account ID extracted from auth claims; dev fallback via `X-Account-Id` header

**Task C.4: Register Minimal API endpoints**
- Use `MapGroup("/api/config")` with RBAC policy:
  - GET methods: `.RequireAuthorization("SettingsView")`
  - PUT/POST methods: `.RequireAuthorization("SettingsManage")`
- Port the `VALID_ROLES` set for prompt template role validation: `defaults, scrum_master, architect, researcher, analyst, planner, implementer, reviewer, tester, documenter`
- Providers endpoints require authentication (401 if no userId)

**Task C.5: Authorization policies** (in `Program.cs`)
- `"SettingsView"` — requires role `admin` or `owner`
- `"SettingsManage"` — requires role `owner`

### Test Strategy (15 tests within the settings group)

| Test Category | Count | What to Test |
|---|---|---|
| Agents config | 3 | GET returns default; PUT updates; PUT with invalid body returns 400 |
| Security config | 3 | GET returns default; PUT updates; sanitize endpoint with valid/invalid direction |
| Sanitization rules | 3 | GET rules (empty default); PUT rules; sanitize content with loaded rules |
| Prompts settings | 2 | GET templates; PUT with invalid role returns 400 |
| Providers settings | 2 | GET requires auth (401 without); PUT updates user config |
| RBAC | 2 | Non-owner cannot PUT (403); admin can GET |

---

## Route Group D: Settings — Providers Group

**TS source**: `packages/api/src/routes/settings/` (health, diagnostics, diagnostics-ingest, providers-factory)  
**C# target**: `Tamma.Api/Endpoints/Settings/`  
**Prefix**: `/api/providers`  
**RBAC**: All require `settings:view`  

### Endpoint Mapping (15 endpoints)

| # | Method | Path | TS Source | C# Endpoint Class | C# Method |
|---|--------|------|-----------|-------------------|-----------|
| 1 | GET | `/api/providers/health` | `health-routes.ts` | `ProviderHealthEndpoints.cs` | `GetHealthStatus` |
| 2 | GET | `/api/providers/health/providers` | `health-routes.ts` | `ProviderHealthEndpoints.cs` | `GetAllProviderHealth` |
| 3 | GET | `/api/providers/health/providers/{key}` | `health-routes.ts` | `ProviderHealthEndpoints.cs` | `GetProviderHealth` |
| 4 | POST | `/api/providers/health/providers/{key}/failure` | `health-routes.ts` | `ProviderHealthEndpoints.cs` | `RecordFailure` |
| 5 | POST | `/api/providers/health/providers/{key}/success` | `health-routes.ts` | `ProviderHealthEndpoints.cs` | `RecordSuccess` |
| 6 | POST | `/api/providers/health/providers/{key}/reset` | `health-routes.ts` | `ProviderHealthEndpoints.cs` | `ResetCircuitBreaker` |
| 7 | GET | `/api/providers/diagnostics` | `diagnostics-routes.ts` | `DiagnosticsEndpoints.cs` | `GetDiagnostics` |
| 8 | GET | `/api/providers/diagnostics/query` | `diagnostics-routes.ts` | `DiagnosticsEndpoints.cs` | `QueryDiagnostics` |
| 9 | GET | `/api/providers/diagnostics/report` | `diagnostics-routes.ts` | `DiagnosticsEndpoints.cs` | `GetReport` |
| 10 | GET | `/api/providers/diagnostics/budget/{accountId}` | `diagnostics-routes.ts` | `DiagnosticsEndpoints.cs` | `GetBudget` |
| 11 | POST | `/api/providers/diagnostics` | `diagnostics-ingest-routes.ts` | `DiagnosticsEndpoints.cs` | `IngestDiagnostics` |
| 12 | POST | `/api/providers/providers/create` | `providers-factory-routes.ts` | `ProviderFactoryEndpoints.cs` | `CreateSession` |
| 13 | POST | `/api/providers/providers/{handle}/execute` | `providers-factory-routes.ts` | `ProviderFactoryEndpoints.cs` | `ExecuteTask` |
| 14 | DELETE | `/api/providers/providers/{handle}` | `providers-factory-routes.ts` | `ProviderFactoryEndpoints.cs` | `DisposeSession` |
| 15 | GET | `/api/providers/providers/sessions` | `providers-factory-routes.ts` | `ProviderFactoryEndpoints.cs` | `ListSessions` |

### Tasks

**Task D.1: Create DTOs** (`Tamma.Api/Models/Settings/`)
- `ProviderHealthResponse.cs` — `{ healthy, failures, circuitOpen, circuitOpenUntil, halfOpen }`
- `RecordFailureRequest.cs` — maps to TS `RecordFailureInput`
- `DiagnosticsQueryParams.cs` — `{ provider?, model?, from?, to?, limit?, offset? }`
- `DiagnosticsReportParams.cs` — `{ from?, to?, groupBy? }` with validation: groupBy in `[provider, model, agentType]`
- `BudgetResponse.cs` — `{ spent, limit, remaining, percentUsed }`
- `DiagnosticsRecordInput.cs` — for ingest endpoint (single or array)
- `CreateSessionRequest.cs` — `{ provider, model?, apiKeyRef?, config? }`
- `CreateSessionResponse.cs` — `{ handle, provider, model }`
- `ExecuteTaskRequest.cs` — maps to TS `AgentTaskConfig` (requires `prompt: string`)

**Task D.2: Create IProviderHealthRepository** (`Tamma.Data/Repositories/IProviderHealthRepository.cs`)
- Methods: `GetAll()`, `Get(key)`, `RecordFailure(key, input)`, `RecordSuccess(key)`, `Reset(key)`
- EF Core implementation backed by `provider_health` table (migration 015)
- Circuit breaker logic: 5 failures in 60s opens circuit for 300s
- Key validation: `^[a-zA-Z0-9._\-:/]+$`, max 256 chars
- Unknown keys return healthy default (no 404)

**Task D.3: Create IDiagnosticsRepository** (`Tamma.Data/Repositories/IDiagnosticsRepository.cs`)
- Methods: `Insert(records[])`, `Query(options)`, `Report(options)`, `GetBudget(accountId, limitUsd)`
- EF Core implementation backed by `provider_diagnostics` table (migration 014)
- Query: supports provider, model, from/to date range, limit (1-200), offset
- Report: aggregated grouping by provider/model/agentType
- Budget: sum costs for accountId, compare against limit
- Valid event types for legacy endpoint: `tool:invoke, tool:complete, tool:error, provider:call, provider:complete, provider:error`

**Task D.4: Create IProviderSessionService** (`Tamma.Api/Services/ProviderSessionService.cs`)
- In-memory session store (same pattern as TS — sessions are ephemeral, not persisted)
- `Create(input)` — instantiate provider, return UUID handle
- `Execute(handle, config)` — find session, execute task, return result
- `Dispose(handle)` — cleanup provider resources, remove from store
- `ListSessions()` — return active session summaries
- Handle validation: UUID format regex `^[0-9a-f]{8}-...$`
- **Note**: Provider factory routes bridge Elsa C# workflows to TS provider implementations. In Phase 3, this service will call out to the TS providers package via HTTP or be a thin in-process adapter. Full native C# providers come later (Epic 1 backport).

**Task D.5: Register Minimal API endpoints**
- Health group: `MapGroup("/api/providers/health")`
- Diagnostics group: `MapGroup("/api/providers/diagnostics")`
- Provider factory group: `MapGroup("/api/providers/providers")`
- All require `settings:view` authorization

### Test Strategy (15 tests within the providers group)

| Test Category | Count | What to Test |
|---|---|---|
| Health endpoints | 4 | GET all providers; GET specific key; record failure opens circuit; record success closes circuit |
| Diagnostics query | 3 | Query with filters; report with groupBy; budget calculation |
| Diagnostics ingest | 2 | Single record insert; batch insert; invalid body (400) |
| Provider factory | 4 | Create session; execute task; dispose session; list sessions; invalid handle (400) |
| Key validation | 2 | Invalid characters rejected; oversized key rejected |

---

## Route Group E: Convention Templates

**TS source**: `packages/api/src/routes/convention-templates.ts`  
**C# target**: `Tamma.Api/Endpoints/ConventionTemplateEndpoints.cs`  
**No repository needed** — convention templates are static read-only data shipped with the application  

### Endpoint Mapping (2 endpoints)

| # | Method | Path | C# Method | Auth |
|---|--------|------|-----------|------|
| 1 | GET | `/api/convention-templates` | `ListTemplates` | None (public) |
| 2 | GET | `/api/convention-templates/{key}` | `GetTemplate` | None (public) |

### Tasks

**Task E.1: Port static template data** (`Tamma.Api/Data/ConventionTemplates.cs`)
- Port from TS `packages/api/src/services/convention-templates.ts`
- Static class with `Dictionary<string, ConventionTemplate>` containing 20 language/framework templates
- Each template: `{ name, description, conventions }` (the `conventions` string is the actual content)

**Task E.2: Create DTOs** (`Tamma.Api/Models/ConventionTemplates/`)
- `ConventionTemplateSummary.cs` — `{ key, name, description }` (for list endpoint)
- `ConventionTemplateDetail.cs` — `{ key, name, description, conventions }` (for detail endpoint)

**Task E.3: Register Minimal API endpoints** (`Tamma.Api/Endpoints/ConventionTemplateEndpoints.cs`)
- `GET /api/convention-templates` — return list of summaries (no auth required)
- `GET /api/convention-templates/{key}` — return full template or 404
- Use `.AllowAnonymous()` since these are read-only reference data

### Test Strategy (10 tests)

| Test Category | Count | What to Test |
|---|---|---|
| List templates | 3 | Returns non-empty array; each item has key/name/description; response is JSON array |
| Get by key | 4 | Valid key returns full template with conventions; unknown key returns 404; key is case-sensitive |
| Data integrity | 3 | All 20 templates present; no empty conventions strings; no duplicate keys |

---

## Route Group F: Knowledge Base

**TS source**: `packages/api/src/routes/knowledge-base/` (6 sub-route files)  
**C# target**: `Tamma.Api/Endpoints/KnowledgeBase/`  
**Prefix**: `/api/kb` (changed from `/api/knowledge-base` in TS — verify in story)  
**Note**: KB services are currently mocks in TS. The C# port maintains the same service interface but with real implementations to be wired later.  

### Endpoint Mapping (30 endpoints)

#### Index Management (6 endpoints) — `IndexEndpoints.cs`

| # | Method | Path | C# Method |
|---|--------|------|-----------|
| 1 | GET | `/api/kb/index/status` | `GetStatus` |
| 2 | POST | `/api/kb/index/trigger` | `TriggerIndex` |
| 3 | DELETE | `/api/kb/index/cancel` | `CancelIndex` |
| 4 | GET | `/api/kb/index/history` | `GetHistory` |
| 5 | GET | `/api/kb/index/config` | `GetConfig` |
| 6 | PUT | `/api/kb/index/config` | `UpdateConfig` |

#### Vector DB (6 endpoints) — `VectorDbEndpoints.cs`

| # | Method | Path | C# Method |
|---|--------|------|-----------|
| 7 | GET | `/api/kb/vector-db/collections` | `ListCollections` |
| 8 | POST | `/api/kb/vector-db/collections` | `CreateCollection` |
| 9 | GET | `/api/kb/vector-db/collections/{name}/stats` | `GetCollectionStats` |
| 10 | DELETE | `/api/kb/vector-db/collections/{name}` | `DeleteCollection` |
| 11 | POST | `/api/kb/vector-db/search` | `Search` |
| 12 | GET | `/api/kb/vector-db/storage` | `GetStorageUsage` |

#### RAG Pipeline (4 endpoints) — `RagEndpoints.cs`

| # | Method | Path | C# Method |
|---|--------|------|-----------|
| 13 | GET | `/api/kb/rag/config` | `GetConfig` |
| 14 | PUT | `/api/kb/rag/config` | `UpdateConfig` |
| 15 | GET | `/api/kb/rag/metrics` | `GetMetrics` |
| 16 | POST | `/api/kb/rag/test` | `TestQuery` |

#### MCP Server Management (8 endpoints) — `McpEndpoints.cs`

| # | Method | Path | C# Method |
|---|--------|------|-----------|
| 17 | GET | `/api/kb/mcp/servers` | `ListServers` |
| 18 | GET | `/api/kb/mcp/servers/{name}` | `GetServerStatus` |
| 19 | POST | `/api/kb/mcp/servers/{name}/start` | `StartServer` |
| 20 | POST | `/api/kb/mcp/servers/{name}/stop` | `StopServer` |
| 21 | POST | `/api/kb/mcp/servers/{name}/restart` | `RestartServer` |
| 22 | GET | `/api/kb/mcp/servers/{name}/tools` | `ListTools` |
| 23 | POST | `/api/kb/mcp/servers/{name}/tools/{tool}/invoke` | `InvokeTool` |
| 24 | GET | `/api/kb/mcp/servers/{name}/logs` | `GetServerLogs` |

#### Context Testing (3 endpoints) — `ContextEndpoints.cs`

| # | Method | Path | C# Method |
|---|--------|------|-----------|
| 25 | POST | `/api/kb/context/test` | `TestContext` |
| 26 | POST | `/api/kb/context/feedback` | `SubmitFeedback` |
| 27 | GET | `/api/kb/context/history` | `GetHistory` |

#### Analytics (3 endpoints) — `AnalyticsEndpoints.cs`

| # | Method | Path | C# Method |
|---|--------|------|-----------|
| 28 | GET | `/api/kb/analytics/usage` | `GetUsageAnalytics` |
| 29 | GET | `/api/kb/analytics/quality` | `GetQualityAnalytics` |
| 30 | GET | `/api/kb/analytics/costs` | `GetCostAnalytics` |

### Tasks

**Task F.1: Create service interfaces** (`Tamma.Api/Services/KnowledgeBase/`)
- `IIndexManagementService.cs` — `GetStatus, TriggerIndex, CancelIndex, GetHistory, GetConfig, UpdateConfig`
- `IVectorDBManagementService.cs` — `ListCollections, CreateCollection, GetCollectionStats, DeleteCollection, Search, GetStorageUsage`
- `IRAGManagementService.cs` — `GetConfig, UpdateConfig, GetMetrics, TestQuery`
- `IMCPManagementService.cs` — `ListServers, GetServerStatus, Start/Stop/RestartServer, ListTools, InvokeTool, GetServerLogs`
- `IContextTestingService.cs` — `TestContext, SubmitFeedback, GetRecentTests`
- `IAnalyticsService.cs` — `GetUsageAnalytics, GetQualityAnalytics, GetCostAnalytics`

**Task F.2: Create stub implementations** (`Tamma.Api/Services/KnowledgeBase/`)
- Each service returns empty/zero state by default (matching TS mock behavior)
- Constructor accepts optional real dependency (same DI pattern as TS `createKBServices()`)
- When a real implementation is wired (e.g., `ICodebaseIndexer`, `IVectorStoreService`), delegate to it

**Task F.3: Create DTOs** (`Tamma.Api/Models/KnowledgeBase/`)
- `IndexStatusResponse.cs`, `TriggerIndexRequest.cs`, `IndexHistoryResponse.cs`, `IndexConfigDto.cs`
- `CollectionStatsResponse.cs`, `CreateCollectionRequest.cs`, `VectorSearchRequest.cs`, `StorageUsageResponse.cs`
- `RAGConfigDto.cs`, `RAGTestRequest.cs`, `RAGTestResponse.cs`, `RAGMetricsResponse.cs`
- `MCPServerStatus.cs`, `MCPToolInvokeRequest.cs`, `MCPToolInvokeResponse.cs`
- `ContextTestRequest.cs`, `ContextFeedbackRequest.cs`, `ContextTestResponse.cs`
- `UsageAnalyticsResponse.cs`, `QualityAnalyticsResponse.cs`, `CostAnalyticsResponse.cs`
- `AnalyticsPeriod.cs` — shared `{ start, end }` with default 30-day window

**Task F.4: Input validation**
- `TriggerIndex`: validate `repositoryPath` and `changedFiles` against directory traversal (`..` not allowed)
- `changedFiles`: must be `string[]` if present
- `CreateCollection`: `name` and `dimensions` required
- `VectorSearchRequest`: validate body shape
- `MCPToolInvokeRequest`: construct from route params + body arguments
- `limit` query params: default 20 for history, 100 for logs; parse safely

**Task F.5: Register Minimal API endpoints** (6 endpoint classes)
- Use `MapGroup("/api/kb")` as parent, then sub-groups: `/index`, `/vector-db`, `/rag`, `/mcp`, `/context`, `/analytics`
- Status codes: 202 for async triggers (index/start/restart), 409 for conflict (already indexing), 404 for not-found

**Task F.6: Register DI** (`Program.cs` additions)
- Register all 6 KB service interfaces as scoped
- Factory method `AddKnowledgeBaseServices(this IServiceCollection)` extension method for clean registration

### Test Strategy (20 tests)

| Test Category | Count | What to Test |
|---|---|---|
| Index endpoints | 4 | Get status; trigger index (202); cancel (409 when idle); history with limit |
| Vector DB endpoints | 4 | List collections; create collection; delete non-existent (404); search |
| RAG endpoints | 3 | Get config; update config; test query |
| MCP endpoints | 4 | List servers; start/stop; invoke tool; get logs with limit |
| Context endpoints | 2 | Test context; submit feedback |
| Analytics endpoints | 3 | Usage with default period; quality with custom period; costs |

---

## Cross-Cutting Concerns

### 1. Tenant Resolution (shared across all groups)

Port the tenant resolution pattern consistently:

```
Priority:
  1. HttpContext.Items["TenantId"] (from TenantContextMiddleware, Phase 1)
  2. X-Tenant-Id header (service-to-service, Elsa workflows)
  3. tenantId query parameter (fallback)
  4. null (system-level / no tenant scoping)
```

Create `TenantResolver` helper class used by all endpoint groups.

### 2. Account ID Resolution (settings routes)

```
Priority:
  1. Auth claims (User.FindFirst("accountId"))
  2. X-Account-Id header (dev/testing only, NEVER in production)
  3. null
```

Create `AccountResolver` helper class.

### 3. Rate Limiting

Apply ASP.NET Core rate limiting middleware per route group:
- Agent config reads: 100 req/min
- Agent config writes: 30 req/min
- All other routes: default global rate limit

### 4. Error Response Format

All error responses must match the existing TS format:
```json
{ "error": "Human-readable error message" }
```

Optional `errors` array for validation failures:
```json
{ "error": "Validation failed", "errors": ["field X is required", "field Y must be positive"] }
```

### 5. Shared Extension Methods

Create `Tamma.Api/Extensions/EndpointRouteBuilderExtensions.cs`:
- `MapPhase3Routes(this IEndpointRouteBuilder)` — registers all Phase 3 route groups
- Called from `Program.cs` after Phase 2 routes

---

## nginx Routing Changes

Add the following location blocks to the nginx config (after Phase 2 blocks):

```nginx
# Phase 3: domain routes to C# API
location /api/v1/agents/ {
    proxy_pass http://tamma-api-dotnet:5080;
}
location /api/prompts/ {
    proxy_pass http://tamma-api-dotnet:5080;
}
location /api/config/ {
    proxy_pass http://tamma-api-dotnet:5080;
}
location /api/providers/ {
    proxy_pass http://tamma-api-dotnet:5080;
}
location /api/convention-templates {
    proxy_pass http://tamma-api-dotnet:5080;
}
location /api/kb/ {
    proxy_pass http://tamma-api-dotnet:5080;
}

# Phase 2 blocks still active (health, admin, auth, orgs, tenants)
# Catch-all: remaining routes to TS API
location /api/ {
    proxy_pass http://tamma-api:3100/api/;
}
```

**Deploy procedure**: Deploy C# API with Phase 3 endpoints first, verify with integration tests, then update nginx config to flip traffic.

---

## Test Strategy Summary

| Route Group | Endpoint Class | xUnit Tests | Replaces Vitest |
|---|---|---|---|
| A: Agent Config + Resolver | `AgentConfigEndpoints`, `AgentResolverEndpoints` | 20 | `agent-config-routes.test.ts`, `agent-resolver-routes.test.ts` |
| B: Prompt Routes | `PromptEndpoints` | 25 | `prompt-routes.test.ts` |
| C: Settings — Config | `AgentsSettings`, `SecuritySettings`, `PromptsSettings`, `ProvidersSettings` | 15 | `settings-routes.test.ts`, `sanitization-routes.test.ts`, `providers-routes.test.ts` |
| D: Settings — Providers | `ProviderHealth`, `Diagnostics`, `ProviderFactory` | 15 | `diagnostics-store-routes.test.ts`, `health-store-routes.test.ts`, `provider-factory-routes.test.ts` |
| E: Convention Templates | `ConventionTemplateEndpoints` | 10 | (inline in integration tests) |
| F: Knowledge Base | 6 endpoint classes | 20 | `knowledge-base-routes.test.ts`, `knowledge-base-services.test.ts` |
| **Total** | **14 endpoint classes** | **105** | **11 Vitest test files** |

### Test Infrastructure

- **Test project**: `Tamma.Api.Tests` (xUnit + `Microsoft.AspNetCore.Mvc.Testing`)
- **Database**: EF Core InMemory provider for all repository tests
- **HTTP**: `WebApplicationFactory<Program>` with `HttpClient` for endpoint integration tests
- **Auth mocking**: Custom `TestAuthHandler` that injects claims (tenantId, userId, role)
- **Service mocking**: Substitute interfaces with NSubstitute or Moq for unit-level endpoint tests

### Parity Verification

After all Phase 3 tests pass, run a cross-verification script:
1. Start both TS and C# APIs
2. Send identical requests to both
3. Compare response status codes + JSON bodies (ignoring timestamps)
4. Document any intentional differences

---

## Rollback Plan

1. Remove Phase 3 `location` blocks from nginx config
2. Reload nginx: `docker exec nginx nginx -s reload`
3. All Phase 3 traffic falls back to the catch-all `location /api/` block pointing at the TS API
4. The TS API retains all routes throughout Phase 3 — no TS code is removed until Phase 5
5. No database rollback needed — Phase 3 uses the same tables/schema as Phase 1

---

## File Summary

### New C# Files (Phase 3)

```
Tamma.Api/
  Endpoints/
    Agents/
      AgentConfigEndpoints.cs
      AgentResolverEndpoints.cs
    Prompts/
      PromptEndpoints.cs
    Settings/
      AgentsSettingsEndpoints.cs
      SecuritySettingsEndpoints.cs
      PromptsSettingsEndpoints.cs
      ProvidersSettingsEndpoints.cs
      ProviderHealthEndpoints.cs
      DiagnosticsEndpoints.cs
      ProviderFactoryEndpoints.cs
    ConventionTemplateEndpoints.cs
    KnowledgeBase/
      IndexEndpoints.cs
      VectorDbEndpoints.cs
      RagEndpoints.cs
      McpEndpoints.cs
      ContextEndpoints.cs
      AnalyticsEndpoints.cs
  Models/
    Agents/
      AgentConfigResponse.cs
      AgentConfigUpsertRequest.cs
      AgentConfigValidateRequest.cs
      AgentResolveForPhaseRequest.cs
      TaskOverridesDto.cs
    Prompts/
      PromptUpsertRequest.cs
      PromptRenderRequest.cs
      PromptTemplateResponse.cs
      PromptListResponse.cs
    Settings/
      AgentsConfigDto.cs
      SecurityConfigDto.cs
      SanitizeRequest.cs
      SanitizeResponse.cs
      SanitizationRulesDto.cs
      ProvidersConfigDto.cs
      PromptTemplateUpdateRequest.cs
      ProviderHealthResponse.cs
      RecordFailureRequest.cs
      DiagnosticsQueryParams.cs
      DiagnosticsReportParams.cs
      BudgetResponse.cs
      DiagnosticsRecordInput.cs
      CreateSessionRequest.cs
      CreateSessionResponse.cs
      ExecuteTaskRequest.cs
    ConventionTemplates/
      ConventionTemplateSummary.cs
      ConventionTemplateDetail.cs
    KnowledgeBase/
      (16 DTO files as listed in Task F.3)
  Services/
    AgentResolverService.cs
    AgentConfigValidator.cs
    ConfigService.cs
    PromptStoreService.cs
    ProviderSessionService.cs
    KnowledgeBase/
      IIndexManagementService.cs + IndexManagementService.cs
      IVectorDBManagementService.cs + VectorDBManagementService.cs
      IRAGManagementService.cs + RAGManagementService.cs
      IMCPManagementService.cs + MCPManagementService.cs
      IContextTestingService.cs + ContextTestingService.cs
      IAnalyticsService.cs + AnalyticsService.cs
  Data/
    DefaultPrompts.cs
    ConventionTemplates.cs
  Extensions/
    EndpointRouteBuilderExtensions.cs

Tamma.Data/
  Repositories/
    ISanitizationRepository.cs + SanitizationRepository.cs
    IProviderHealthRepository.cs + ProviderHealthRepository.cs
    IDiagnosticsRepository.cs + DiagnosticsRepository.cs

Tamma.Api.Tests/
  Endpoints/
    Agents/
      AgentConfigEndpointsTests.cs
      AgentResolverEndpointsTests.cs
    Prompts/
      PromptEndpointsTests.cs
    Settings/
      SettingsEndpointsTests.cs
      ProviderHealthEndpointsTests.cs
      DiagnosticsEndpointsTests.cs
      ProviderFactoryEndpointsTests.cs
    ConventionTemplateEndpointsTests.cs
    KnowledgeBase/
      IndexEndpointsTests.cs
      VectorDbEndpointsTests.cs
      RagEndpointsTests.cs
      McpEndpointsTests.cs
      ContextEndpointsTests.cs
      AnalyticsEndpointsTests.cs
  Helpers/
    TestAuthHandler.cs
    WebApplicationFactoryExtensions.cs
```

**Total new files**: ~80 C# files  
**Estimated lines of code**: ~4,500 (endpoints + DTOs + services + tests)
