# Story 32-1 — Agent Entity Model & Versioned Saved Config (public/private)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every task writes tests
> before implementation.

**Story:** `docs/stories/epic-32/story-32-1/32-1-agent-entity-model-and-versioned-saved-config.md`
**Epic:** 32 — Agents: First-Class Agent Entities, Managed Execution, Benchmarking & Learning
**Design of record:** `docs/superpowers/specs/2026-06-17-agent-entities-benchmarking-design.md`
**Priority:** P0 (foundational — everything in Epic 32 joins on `agent_id` + config version)
**Est. effort:** 4-5 days
**Date:** 2026-06-17

---

## Goal

Promote agents from the anonymous, role-keyed `agent_configs` JSONB blob into first-class,
identity-bearing **entities** with immutable, monotonically **versioned** saved-config snapshots.
Introduce two control-plane-resident EF entities — `Agent` (stable identity, role,
public/private visibility, owner, status, current-version pointer) and `AgentVersion` (immutable
config snapshot) — with a CHECK-enforced ownership invariant, a versioning transaction, DCB audit
events, a public-agent seeder, and per-mode/per-tenant RBAC'd REST endpoints. This is the
canonical owner of the `Agent`/`AgentVersion` entities the rest of Epic 32 builds on.

## Non-goals (YAGNI guard)

- **NO performance/action data on these entities.** Per the design, performance is ALWAYS
  tenant-scoped and lands in the tenant schema in later stories (32-6, 32-10). `Agent`/`AgentVersion`
  are definition-only.
- **NO cutover of the legacy `agent_configs` blob.** The new model coexists; repointing workflows
  and retiring `agent_configs` is a separate, later Epic 32 story. Keep the blast radius small.
- **NO managed execution layer, no resolution-with-merge, no benchmarking.** Those are 32-2/32-5/32-10.
  This story stops at: entity + version + ownership + seed + CRUD endpoints + events.
- **NO TenantDbContext changes.** Definitions are control-plane-resident (cross-tenant identity is a
  CP concern). Do not add these entities to `TenantDbContext`.
- **NO new auth policies.** Reuse `PlatformOwnerAccess` (public writes), the owner/admin gate
  (private writes), and `SettingsView` (reads). Member 403 is enforced at the handler, mirroring
  the Prompt Store.
- **NO BYOK / credential wiring.** Agent definitions are credential-agnostic (provider+model+prompt,
  never raw keys). Credential resolution is 32-3.

---

## Current-state findings (verified 2026-06-17, repo @ main 98cfb1c2)

### The thing being replaced

| Site | Today |
|---|---|
| `src/Tamma.Data/Entities/AgentConfig.cs` | One row per principal: `Id`, nullable `TenantId` (NULL = system default, non-NULL = tenant override), `Config` jsonb blob, `Version` int, audit cols. No identity, no per-agent versioning history (Version is bumped in place on the single row), no public/private split beyond null-tenant. |
| `src/Tamma.Data/TammaModelConfiguration.cs` ~675-712 | `agent_configs` table config: jsonb `Config`, partial unique index `HasFilter("\"TenantId\" IS NOT NULL")`, `omitTenantIdColumn`/`isTenantContext` branches. |
| `src/Tamma.Api/Endpoints/AgentEndpoints.cs` | `GetConfig`/`UpdateConfig`/`ValidateConfig` over the blob; `ResolveAgent`/`ResolveForPhase` via `IAgentResolverService`. `UpdateConfig` validates via private `ValidateConfigShape` (provider regex `^[a-z0-9][a-z0-9_-]{0,63}$`, `maxBudgetUsd` ∈ [0,100], ReDoS guard, prototype-pollution rejection) and emits `AGENT_CONFIG.UPDATED.SUCCESS` ONLY after a real write (Story 28-1 PR A discipline). |
| `src/Tamma.ElsaServer/AgentSeeder.cs` | `BackgroundService` seeding the **Elsa Agents** store (`IAgentManager`) with 9 `tamma-<role>` definitions (`tamma-architect`, `tamma-tester`, `tamma-reviewer`, …), each with prompt + `temperature` + `maxTokens=4096` + `providerChain ["anthropic","openai","openrouter"]` + `maxBudgetUsd=10.0`. Idempotent skip-by-name. This seeds Elsa's store, NOT the Tamma CP tables — the new `AgentEntitySeeder` is additive and reuses the same handles/values. |

### Patterns to mirror (verified)

- **Ownership XOR CHECK + NULLS-NOT-DISTINCT unique index:** `PromptOverride` in
  `TammaModelConfiguration.cs` ~714-756 — `ck_prompt_overrides_principal_xor`
  (`("UserId" IS NOT NULL AND "TenantId" IS NULL) OR (...)`), and
  `HasIndex(...).IsUnique().AreNullsDistinct(false)`. This is the canonical template for
  `ck_agents_visibility_ownership` and the partial unique indexes.
- **Partial unique index:** `AgentConfig` ~700/704 — `HasIndex(e => e.TenantId).IsUnique().HasFilter("\"TenantId\" IS NOT NULL")`.
- **Audit-event-only-on-real-write:** `AgentEndpoints.UpdateConfig` ~91-113 — `IEventRepository.AppendAsync(new DomainEvent { Type, TenantId, Tags=JSON, Metadata=JSON, Data=JSON, CreatedAt })`. `DomainEvent` fields verified in `Entities/DomainEvent.cs` (`Id, Type, TenantId, Tags, Metadata, Data, CreatedAt, SequenceNumber`).
- **Insert-missing-only seeder:** `ConventionStoreSeeder` (per memory) / `AgentSeeder` skip-by-existing — `AgentEntitySeeder` follows the same shape.
- **CP DbContext + DbSet registration:** `ControlPlaneDbContext.cs` line 185 (`DbSet<AgentConfig>`), full DbSet list verified; add `DbSet<Agent>` / `DbSet<AgentVersion>` alongside.
- **CP migration pipeline:** `src/Tamma.Data/Migrations/ControlPlane/` (namespace `Tamma.Data.Migrations.ControlPlane`, e.g. `20260609205701_InitialControlPlane`); `ControlPlaneDesignTimeDbContextFactory.cs` exists.
- **Mode + identity seams:** `ITammaModeProvider` (`Services/PromptStore/TammaMode.cs`, singleton, `Mode` is `SingleUser`|`SaaS`), `ITenantContext` (`Tamma.Data/ITenantContext.cs`, `Guid? TenantId`), `ClaimsPrincipal.GetUserId()`.
- **Taxonomy:** `Tamma.Core/Agents/AgentRole.cs` (8 roles, `[Wire(...)]` strings, `AgentRoleExtensions.Parse` → `RolePhaseMap.NormalizeRole`), `RolePhaseMap.ValidRoles` / `LegacyRoleAliases` / `ForbiddenKeys`.
- **Auth policies:** `Program.cs` ~966-1090 — `PlatformOwnerAccess` (platform_admin claim), `OwnerAccess` (users:manage), `SettingsManage`, `SettingsView`. Agents group mapped ~1679-1688 (`/api/v1/agents` `.RequireAuthorization("SettingsView")`).
- **Test fixtures:** `tests/Tamma.Api.Tests/Agents/` exists; CP model/isolation precedents in `tests/Tamma.Api.Tests/Epic28/ControlPlaneDbContextModelTests.cs` + `CrossTenantIsolationPostgresTests.cs`; in-memory + Postgres fixtures in `Infrastructure/`. Run via `sg docker -c "dotnet test ..."`.

### Environment notes

- C# tests: `sg docker -c "dotnet test apps/tamma-elsa/... "` (session docker group stale; build needs no wrapper).
- Postgres 17 in prod/CI — `NULLS NOT DISTINCT` and partial-index filters are available.
- `dotnet ef migrations has-pending-model-changes --context ControlPlaneDbContext` must report none after the migration.

---

## Architecture (one paragraph)

Two CP-resident entities: `Agent` (identity + role + visibility + owner + status + `CurrentVersionId`
pointer) and `AgentVersion` (immutable `(AgentId, Version, ConfigJson)` snapshot). A DB `CHECK`
ties `Visibility` to ownership columns (public ⇒ no owner; private ⇒ exactly one of tenant/user
owner, picked by process mode). `AgentRepository.PublishVersionAsync` runs a single transaction:
insert next-version row → repoint `CurrentVersionId`; the `(AgentId, Version)` unique index makes
concurrent publishes safe (retry on conflict). Every state transition appends a DCB event
(`AGENT.CREATED|VERSION_PUBLISHED|ARCHIVED.SUCCESS`) to the CP `DomainEvents` store via
`IEventRepository`. An idempotent `AgentEntitySeeder` populates one public agent per role with the
existing `tamma-<role>` handles. REST endpoints on `/api/v1/agents` expose create/publish/archive/
list/get with per-mode RBAC (public writes → `PlatformOwnerAccess`; private writes → tenant
owner/admin; member → 403; reads scoped to public ∪ own private, cross-tenant → 404).

---

## Phased task breakdown (TDD — tests first in every task)

### Task 1 — Entities + EF model config + migration (AC 1-5, 13)

**Files:**
- New: `Tamma.Data/Entities/Agent.cs`, `AgentVersion.cs`, `AgentVisibility.cs`, `AgentStatus.cs`.
- Modify: `Tamma.Data/ControlPlaneDbContext.cs` — add `DbSet<Agent> Agents`, `DbSet<AgentVersion> AgentVersions`.
- Modify: `Tamma.Data/TammaModelConfiguration.cs` — `Entity<Agent>` (table `agents`,
  `ck_agents_visibility_ownership` CHECK mirroring `ck_prompt_overrides_principal_xor`, partial
  unique indexes `IX_agents_public_name_role` / `IX_agents_private_tenant_name` /
  `IX_agents_private_user_name`, enum→int conversions, default-value SQL) and `Entity<AgentVersion>`
  (table `agent_versions`, `IX_agent_versions_agent_version` unique, FK `OnDelete(Restrict)`).
  Note: `agents`/`agent_versions` are CP-only — do NOT apply `omitTenantIdColumn`/`isTenantContext`
  branches; they are not in `TenantDbContext`.
- New: migration `Migrations/ControlPlane/<ts>_AddAgentEntities.cs` via
  `dotnet ef migrations add AddAgentEntities --context ControlPlaneDbContext`.

**Approach:** Plain enum→int storage so the CHECK can compare numeric discriminators
(`Visibility = 0` public / `1` private), exactly as the comment in the story's model sketch.
`CurrentVersionId` is a bare nullable Guid pointer (no FK back to versions) to dodge a circular FK
on first-version create.

**Tests (first):** extend `tests/Tamma.Api.Tests/Epic28/ControlPlaneDbContextModelTests.cs` —
assert the two tables exist, the CHECK + three partial indexes + the `(AgentId, Version)` unique
index are in the model; `dotnet ef migrations has-pending-model-changes` reports none (add a model-
shape assertion test if the harness has one). New `AgentEntityMappingTests` against a Postgres
fixture: insert valid public/private rows; assert CHECK rejects public-with-owner,
private-with-no-owner, private-with-both-owners.

**Done when:** migration applies + rolls back cleanly; `has-pending-model-changes` clean; model
tests green.

### Task 2 — `AgentConfigValidator` (extract + extend) (AC 7)

**Files:**
- New: `Tamma.Api/Services/Agents/AgentConfigValidator.cs` — public
  `static (bool Valid, string[] Errors) Validate(string configJson)`.
- Modify: `Tamma.Api/Endpoints/AgentEndpoints.cs` — make the existing private `ValidateConfigShape`
  delegate to (or be moved into) `AgentConfigValidator` so rules are shared, not duplicated.

**Approach:** Lift the existing rules verbatim (provider regex, `maxBudgetUsd` [0,100], non-empty
chains, prototype-pollution, ReDoS `blockedCommandPatterns`, `maxFetchSizeBytes`). Extend for the
Epic 32 saved-config fields: `model` (string), `temperature` ∈ [0,2], `maxTokens` > 0,
`tokenBudget` ≥ 0, `tools[]` (strings), `systemPromptRef` (string), `rag{}` (object). Tolerant of
empty config (valid).

**Tests (first):** `tests/Tamma.Api.Tests/Agents/AgentConfigValidatorTests.cs` — each new rule's
accept/reject; regression cases proving the legacy rules still fire; existing `AgentEndpoints`
validation tests still pass after the extraction.

**Done when:** validator covers old + new rules; no behavior change to existing `ValidateConfig`.

### Task 3 — `IAgentRepository` / `AgentRepository` with versioning transaction + events (AC 6, 8, 9, 14)

**Files:**
- New: `Tamma.Data/Repositories/IAgentRepository.cs`, `AgentRepository.cs` (resolves against
  `ControlPlaneDbContext` + `IEventRepository`).

**Approach:**
- `CreateAsync(agent, firstVersionConfigJson, notes, createdBy)`: validate config (call site passes
  validated JSON; repo asserts non-empty); single transaction — insert `Agent`, insert
  `AgentVersion{Version=1}`, set `CurrentVersionId`; append `AGENT.CREATED.SUCCESS`.
- `PublishVersionAsync(agentId, configJson, notes, updatedBy)`: transaction —
  `nextVersion = MAX(Version)+1`, insert version, repoint `CurrentVersionId` + `UpdatedAt/By`;
  append `AGENT.VERSION_PUBLISHED.SUCCESS`. Catch unique-index violation on `(AgentId, Version)` →
  recompute + retry (bounded). Prior versions untouched.
- `ArchiveAsync(agentId, updatedBy)`: set `Status=Archived` (idempotent — no event if already
  archived); append `AGENT.ARCHIVED.SUCCESS` only on transition.
- `ListVisibleAsync(tenantId, userId)`: `WHERE Visibility=Public OR (Visibility=Private AND
  ((tenantId != null AND OwnerTenantId=tenantId) OR (userId != null AND OwnerUserId=userId)))`.
- Entity-level ownership guard: a private create whose owner columns contradict the resolved
  principal throws a `TammaError`-style guard before DB (belt to the CHECK's suspenders).
- DCB tags include `{ agentId, version, visibility, ownerTenantId?, ownerUserId?, role, mode }`;
  `DomainEvent.TenantId = OwnerTenantId` for private/SaaS, NULL for public.

**Tests (first):** `tests/Tamma.Api.Tests/Agents/AgentRepositoryTests.cs` — version increment 1→2→3;
rollback-pointer integrity (CurrentVersionId = highest; older versions fetchable; explicit repoint
keeps all rows); concurrent double-publish race → monotonic, no dup; archive idempotency;
DCB event emitted once per transition; no event on validation/transaction failure; per-mode
principal derivation; ownership guard rejects contradictory input.

**Done when:** all repository tests green against the Postgres fixture; events land in `DomainEvents`.

### Task 4 — `AgentEntitySeeder` (idempotent public-agent seed) (AC 10)

**Files:**
- New: `Tamma.ElsaServer/AgentEntitySeeder.cs` (`BackgroundService`, or a hosted seeder invoked
  from `Program.cs`) — creates one public `Agent` + `Version=1` `AgentVersion` per role, reusing the
  `tamma-<role>` handles and shipped config values currently in `AgentSeeder.GetDefaultAgents()`.

**Approach:** Skip-by-existing-handle (query `agents WHERE Visibility=Public AND Name=@handle`).
Build `ConfigJson` from the shipped `providerChain` / `temperature` / `maxTokens` / `maxBudgetUsd`
so the CP rows mirror the Elsa-store definitions. Owner columns NULL (public). Idempotent: second
run inserts nothing.

**Tests (first):** `tests/Tamma.Api.Tests/Agents/AgentEntitySeederTests.cs` — first run creates N
public agents with correct handles + Version=1; second run is a no-op (count unchanged, no new
events); each seeded config validates.

**Done when:** seeder is idempotent and produces validating public agents preserving handles.

### Task 5 — REST endpoints + DTOs + route mapping with per-mode RBAC (AC 11, 12)

**Files:**
- New: `Tamma.Api/Dtos/Agents/AgentDtos.cs` (request/response records: `CreateAgentRequest`,
  `PublishVersionRequest`, `AgentSummary`, `AgentDetail`, `AgentVersionSummary/Detail`).
- Modify: `Tamma.Api/Endpoints/AgentEndpoints.cs` — add `CreateAgent`, `PublishVersion`,
  `ArchiveAgent`, `ListAgents`, `GetAgent`, `ListVersions`, `GetVersion` handlers. Derive principal
  from `ITammaModeProvider` + `ITenantContext`/`ClaimsPrincipal`; validate config via Task 2;
  reject member-role SaaS writes with 403; private `GET {id}` not owned by caller → 404.
- Modify: `Tamma.Api/Program.cs` — register `IAgentRepository`, `AgentConfigValidator` (if DI'd),
  `AgentEntitySeeder`; map routes under the existing `/api/v1/agents` group: public writes
  `.RequireAuthorization("PlatformOwnerAccess")`, private writes owner/admin gate (`SettingsManage`
  + handler ownership check), reads under `SettingsView`. Leave legacy `config` routes untouched.

**Approach:** Public-vs-private gating: a single create endpoint inspects `visibility` in the body;
for `public` it requires `PlatformOwnerAccess` (enforced via a handler-level check or a second
mapped route) — simplest is one route gated at the lower `SettingsManage` bar that then 403s public
creates from non-platform-admins, OR (preferred) two distinct mapped routes. Pick the simpler that
passes the RBAC matrix test; document the choice.

**Tests (first):** `tests/Tamma.Api.Tests/Agents/AgentEndpointsTests.cs` — RBAC matrix (platform
owner / tenant owner / tenant admin / member / cross-tenant); create→publish→get round-trip; list
returns public ∪ own-private; cross-tenant private `GET {id}` → 404; member write → 403; invalid
config → 400 + no row/event. **Tenant-isolation** test mirroring
`Epic28/CrossTenantIsolationPostgresTests.cs`: two tenants each own a private `atlas`; neither sees
the other's; partial index allows both.

**Done when:** endpoint + isolation tests green; routes mapped; legacy endpoints unaffected.

### Task 6 — Full-suite green + migration verification + docs touch-up (AC 13)

**Files:** none new (verification task). Optionally update `docs/stories/epic-32/story-32-1/` status
to `ready-for-dev` on hand-off.

**Approach:** `sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Api.Tests/..."` full suite;
`dotnet ef migrations has-pending-model-changes --context ControlPlaneDbContext` → none; confirm no
regression in existing `AgentEndpoints`/Epic28 tests.

**Done when:** full suite green; migration clean; no pending model changes.

---

## Sequencing & dependencies

```
Task 1 (entities+migration) ─┬─> Task 3 (repository+events) ─> Task 4 (seeder) ─> Task 5 (endpoints) ─> Task 6 (verify)
Task 2 (validator) ──────────┘                                          ▲
                                            Task 2 also feeds Task 5 ────┘
```

- **Task 1 is the only hard prerequisite for everything else** (no entity → nothing to persist).
- **Task 2** (validator) is independent of Task 1 and can run in parallel; Task 3 and Task 5 both
  depend on it.
- **Task 3** depends on Task 1 (+ Task 2 for validation at write).
- **Task 4** depends on Task 1 + Task 3 (uses the repository to create public agents).
- **Task 5** depends on Tasks 1-3 (and 2). **Task 6** is last.

External prerequisites already satisfied on `main`: Epic 27 taxonomy (`Tamma.Core/Agents/`),
Epic 28 `ControlPlaneDbContext` + CP migration pipeline, `ITammaModeProvider`, `IEventRepository`.

---

## Risks & mitigations

| Risk | Severity | Mitigation |
|---|---|---|
| Circular FK between `agents.CurrentVersionId` and `agent_versions.AgentId` blocks first-version create | Medium | `CurrentVersionId` is a bare nullable Guid pointer (no DB FK); integrity enforced in the repo transaction + an integration assertion. |
| Concurrent `PublishVersionAsync` races produce duplicate/non-monotonic versions | Medium | `(AgentId, Version)` unique index + catch-and-retry on conflict; explicit race test. |
| CHECK constraint numeric-vs-string mismatch (enum stored as int but CHECK written for text) | Medium | Store `Visibility`/`Status` via `HasConversion<int>()` and write the CHECK against the int discriminator (`= 0` / `= 1`); assert in the Postgres mapping test. |
| `has-pending-model-changes` non-empty because config was placed outside `TammaModelConfiguration.cs` | Medium | Mirror entity config **only** in `TammaModelConfiguration.cs` (single source); Task 6 gates on the EF check. |
| Scope creep into legacy `agent_configs` cutover or managed execution | Medium | Explicit non-goal; this story is definition + version + seed + CRUD only. Coexist with `agent_configs`. |
| Public/private RBAC matrix wrong → tenant pages itself for platform ops, or member edits public agents | High | Mirror Prompt Store RBAC exactly; `PlatformOwnerAccess` for public writes; member 403; encode the full matrix in `AgentEndpointsTests`. |
| Cross-tenant private-agent leak | High | `ListVisibleAsync` scoping + `GET {id}` 404 (not 403) for un-owned private; isolation test mirroring `CrossTenantIsolationPostgresTests`. |
| Seeder double-creates on restart | Low | Skip-by-existing-handle idempotency + idempotency test. |
| Event-store topology shift (Story 28-1 / Epic 30 per-tenant fan-out) | Low | Agent definition events are CP-resident by design; keep emitting via the CP `IEventRepository` so a later tenant-routing migration doesn't touch this code. |

---

## Acceptance criteria (mirrors the story)

- [ ] `Agent` + `AgentVersion` entities exist, CP-resident, registered as `DbSet`s, configured only in
      `TammaModelConfiguration.cs`, with an additive CP migration; not added to `TenantDbContext`.
- [ ] `ck_agents_visibility_ownership` CHECK enforces public⇒no-owner / private⇒exactly-one-owner
      (tenant in SaaS, user in single-user), mirroring `ck_prompt_overrides_principal_xor`.
- [ ] Partial unique indexes: public `(Name, Role)`; private per-owner `(OwnerTenantId, Name)` /
      `(OwnerUserId, Name)`; `(AgentId, Version)` unique on versions.
- [ ] `PublishVersionAsync` writes an immutable new version, increments monotonically, atomically
      repoints `CurrentVersionId`; prior versions remain queryable; concurrent publishes are safe.
- [ ] `ConfigJson` validated (reused+extended `ValidateConfigShape`) before any write; invalid ⇒ no
      row, no event.
- [ ] DCB events `AGENT.CREATED.SUCCESS` / `AGENT.VERSION_PUBLISHED.SUCCESS` /
      `AGENT.ARCHIVED.SUCCESS` appended to the CP `DomainEvents` store with the specified tags, only
      on real state transitions.
- [ ] Per-mode ownership explicit: single-user ⇒ `OwnerUserId`; SaaS ⇒ `OwnerTenantId`; enforced by
      both the entity-level guard and the DB CHECK.
- [ ] `AgentEntitySeeder` idempotently creates one public agent per role preserving `tamma-<role>`
      handles + `Version=1`.
- [ ] REST endpoints with per-mode RBAC: public writes `PlatformOwnerAccess`; private writes tenant
      owner/admin; member 403 on writes; reads = public ∪ own private; cross-tenant private `GET {id}` → 404.
- [ ] `has-pending-model-changes` reports none; full `Tamma.Api.Tests` suite green.
- [ ] Unit + integration tests cover version increment, rollback-pointer integrity, ownership CHECK
      rejection, public/private visibility queries, per-mode derivation, validation rejection, event
      emission/no-emission, seeder idempotency, and tenant isolation.
