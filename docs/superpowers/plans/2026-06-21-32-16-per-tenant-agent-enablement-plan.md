# Story 32-16 — Per-Tenant Agent/Persona Enablement (`TenantAgentEnablement`)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every task writes tests
> before implementation.

**Goal:** Add the **genuinely missing** per-tenant agent/persona enablement layer (locked model rule
6, design §3.3). Introduce the `TenantAgentEnablement` entity (CP-resident; user-keyed in
single-user; XOR/index discipline identical to `AgentRoleSelection`/`prompt_overrides`), the
enable/disable/list API (`tenant_owner`/`tenant_admin`; member → 403), the `AGENT.ENABLED.SUCCESS` /
`AGENT.DISABLED.SUCCESS` events, the seeded-default hook, and — the load-bearing deliverable — the
read-only **`ITenantAgentEnablementReader`** seam exposing `IsEnabledForPrincipalAsync` /
`ListEnabledPublicAgentIdsAsync` / `GetEnabledDefaultPersonaIdAsync` **query primitives** (all async,
explicit `Principal` arg) that the sibling story **32-18** injects + consumes to gate
selection/resolution and to resolve the enabled default. The write/admin
`ITenantAgentEnablementService : ITenantAgentEnablementReader` adds `EnableAsync`/`DisableAsync`/`ListAsync`
(one impl implements both). This story owns the ENTITY + API + events + read seam + primitives; it does
**NOT** wire the gate into the registry/resolver (that is 32-18).

**Story file:** `docs/stories/epic-32/story-32-16/32-16-per-tenant-agent-enablement.md`
**Design of record:** `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` (§3.3, §3.0, §3.5)
**Re-plan / sequence:** `docs/superpowers/plans/2026-06-20-epic-32-37-replan.md` (sequence step C)

**Tech stack:** .NET 9 / Elsa 3 in `apps/tamma-elsa` (central API `Tamma.Api` + data `Tamma.Data`).
Tests live in `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/` (xUnit). Docker-bound suites run via
`sg docker -c "dotnet test ..."` (session docker group is stale; plain `dotnet build` /
`dotnet ef` need no wrapper). **`packages/api` is DELETED — all of this is C#.**

---

## Non-goals (YAGNI guard)

- **NO registry/resolver gate wiring.** `CanUse()` → `IsPublic && IsEnabledForPrincipal`, and the
  enablement-aware `SelectForRoleAsync` / `ResolveUsableAgentAsync` / `ListVisibleAsync` /
  `GetSystemDefaultPublicAsync` (incl. resolve-time fail-loud) are **story 32-18**. This story ships
  the interface + primitive ONLY. Do not touch `AgentRegistryService`/`AgentResolverService`.
- **NO per-user enablement layer.** Members use the tenant's enabled set; no per-user rows in SaaS
  (CLAUDE.md "no per-user override layer"). Single-user keys by `UserId` because the sole user *is*
  the tenant-equivalent.
- **NO public-catalog management.** Creating/retiring the personas themselves stays `PlatformOwnerAccess`
  (32-15/32-2). This story only toggles per-tenant membership of an already-existing persona.
- **NO new migration baseline branch.** Extend the existing EF snapshot (stories are implemented
  SEQUENTIALLY against one migration snapshot — an additive table, not a CHECK edit on the baseline).
- **NO markup/billing/analytics.** Enablement is a gate, not a metering surface.

---

## Current-state findings (the seams this story plugs into)

| Seam | Where it is today | How 32-16 uses it |
|---|---|---|
| **XOR/dual-keying precedent** | `Tamma.Data/Entities/AgentRoleSelection.cs` + its `TammaModelConfiguration` block (XOR check `ck_agent_role_selections_principal_xor`, `HasIndex(...).IsUnique().AreNullsDistinct(false)`). | Mirror EXACTLY for `tenant_agent_enablements` (`ck_tenant_agent_enablements_principal_xor`, unique-nulls-not-distinct on `(TenantId, UserId, AgentId)`). |
| **CP DbContext** | `Tamma.Data/ControlPlaneDbContext.cs` (public `Agent`/`AgentVersion`, `prompt_overrides` single-user rows, etc.). | Add `DbSet<TenantAgentEnablement>` — CP-resident in BOTH modes. |
| **CP model contract test** | `tests/.../Epic28/ControlPlaneDbContextModelTests.cs` — strict `Model_Has_ExpectedControlPlaneEntities` `BeEquivalentTo`. | **Append `TenantAgentEnablement`** or the test fails (known gotcha). |
| **Startup-reset DROP list** | `Tamma.Api/Program.cs` — "Wiping Tamma-managed public-schema tables" block. | **Append `tenant_agent_enablements`** or 2nd host boot fails `relation already exists`. |
| **Agent catalog + visibility** | 32-1 `Agent` (Visibility public/private), 32-2 `/api/agents` group + `AgentManage` policy (`agents:manage`=admin+owner) + `IsPlatformOwner()` + cross-tenant 404. | Validate target ∈ (public ∪ own-private); reuse the route group + policy + 404 convention. |
| **Persona default** | 32-15 `DefaultPersonaName` (e.g. `claude`) + persona seeding. | Seed it enabled for a fresh tenant (insert-missing-only). Supplies `personaName` for event tags. |
| **DCB events** | `Tamma.Data/Repositories/IEventRepository.cs` — `AppendAsync(DomainEvent)`, tenant-scoped; existing `AGENT.*` family. | Emit `AGENT.ENABLED/DISABLED.SUCCESS` tagged `{ agentId, personaName, mode, tenantId|userId }`. |
| **Mode + principal** | `ITammaModeProvider` (`TammaMode.cs`), `ITenantContext`/`ClaimsPrincipal`. | Derive principal (SaaS ⇒ `TenantId`; single-user ⇒ `UserId`) for every read/write. |

**Key insight:** the only genuinely new code is the **entity**, its **EF config + CP migration**, the
**`TenantAgentEnablementService`** (upsert + implicit-private rule + the two primitives + events), the
**three endpoints**, the **DTOs**, the **seeded-default hook**, and the two mandatory amendments
(DROP list + CP model test). Everything else is wiring existing collaborators.

---

## Architecture

```
PUT/DELETE /api/agents/{agentId}/enablement   GET /api/agents/enablement   (AgentEndpoints, reuses 32-2 group)
        |                                              |
        v                                              v
ITenantAgentEnablementService  (Tamma.Api/Services/Agents/)
  Enable/Disable  -> validate target ∈ (public ∪ own-private)         [404 unseen / 409 disable-own-private]
                  -> upsert CP row (principal = TenantId XOR UserId)
                  -> emit AGENT.ENABLED|DISABLED.SUCCESS
  List            -> catalog view (public-with-flag ∪ own-private implicit)
  --- read seam ITenantAgentEnablementReader (consumed by 32-18, the gate) ---
  IsEnabledForPrincipalAsync(agentId, principal)   own-private => true; public => enabled-row exists; else false
  ListEnabledPublicAgentIdsAsync(principal)        set of enabled public ids for the principal
  GetEnabledDefaultPersonaIdAsync(principal)       configured DefaultPersonaName if enabled, else single
                                                   enabled persona if unambiguous, else null
        |
        v
ControlPlaneDbContext.tenant_agent_enablements   (XOR check + unique-nulls-not-distinct; CP-resident both modes)
```

Per-mode (CLAUDE.md two-scoping rule): single-user = `UserId`-keyed CP rows, sole user writes;
SaaS = `TenantId`-keyed CP rows, `tenant_owner`/`tenant_admin` write, members read-only. Mode from
`ITammaModeProvider`. No per-user layer in SaaS.

---

## Task breakdown

Order: T1 (entity + EF + migration + both mandatory amendments) → T2 (service: upsert + implicit rule
+ events) → T3 (the two primitives) → T4 (endpoints + DTOs + RBAC) → T5 (seeded default) → T6
(isolation + mode matrix + constraint tests). T1 must land first (everything depends on the schema).

### T1 — Entity, EF config, CP migration, DROP-list + CP-model-test amendments

**Scope:** The schema and the two known-gotcha amendments. No behaviour yet.

**Files (new/modify):**
- new `Tamma.Data/Entities/TenantAgentEnablement.cs` (fields per story AC1).
- modify `Tamma.Data/TammaModelConfiguration.cs` — `ToTable("tenant_agent_enablements")`, XOR check
  `ck_tenant_agent_enablements_principal_xor`, `HasIndex(x => new { x.TenantId, x.UserId, x.AgentId })
  .IsUnique().AreNullsDistinct(false)` (mirror `AgentRoleSelection`).
- modify `Tamma.Data/ControlPlaneDbContext.cs` — `DbSet<TenantAgentEnablement>`.
- **modify `Tamma.Api/Program.cs`** — append `tenant_agent_enablements` to the "Wiping Tamma-managed
  public-schema tables" DROP list (AC8).
- **modify `tests/.../Epic28/ControlPlaneDbContextModelTests.cs`** — add `TenantAgentEnablement` to the
  strict `BeEquivalentTo` list (AC9).
- new `Migrations/ControlPlane/*_AddTenantAgentEnablements.cs` (generated).

**Generate the migration:**
```bash
dotnet ef migrations add AddTenantAgentEnablements \
  --context ControlPlaneDbContext --output-dir Migrations/ControlPlane \
  --project apps/tamma-elsa/src/Tamma.Data
dotnet ef migrations has-pending-model-changes --context ControlPlaneDbContext   # → none
```

**Tests (first):**
- `ControlPlaneDbContextModelTests` updated list passes (the entity is in the CP model).
- A "second host boot succeeds" assertion (the DROP-list amendment) — reuse the existing test-host
  bootstrap pattern; first boot creates, wipe runs, second boot does not throw `relation already exists`.
- Constraint tests (can be in T6): XOR check rejects both/neither principal; unique index rejects a
  duplicate `(TenantId, UserId, AgentId)`.

**Acceptance:**
- [ ] `TenantAgentEnablement` has all AC1 fields; EF config matches `AgentRoleSelection` discipline.
- [ ] `has-pending-model-changes --context ControlPlaneDbContext` → none.
- [ ] CP model test green; second test-host boot succeeds.

### T2 — `TenantAgentEnablementService` core (upsert + implicit-private rule + events)

**Scope:** `EnableAsync` / `DisableAsync` / `ListAsync`. Principal from `ITammaModeProvider` +
`ITenantContext`/`ClaimsPrincipal`. Validate target ∈ (public CP catalog ∪ principal's own-private).
Own-private/custom ⇒ implicitly enabled (disable ⇒ 409/no-op). Emit the DCB events.

**Files (new):** `Services/Agents/ITenantAgentEnablementReader.cs` (read seam — the three async
primitives 32-18 consumes), `Services/Agents/ITenantAgentEnablementService.cs`
(`: ITenantAgentEnablementReader`; adds `EnableAsync`/`DisableAsync`/`ListAsync`),
`Services/Agents/TenantAgentEnablementService.cs` (implements BOTH),
`Services/Agents/AgentEnablementEventTypes.cs` (`AGENT.ENABLED.SUCCESS`, `AGENT.DISABLED.SUCCESS`).

**Collaborators (constructor-injected — fakes in tests):** `ControlPlaneDbContext` (or a thin
repository), an agent-catalog reader (to resolve visibility + `personaName`; reuse 32-2's registry
read seam), `ITammaModeProvider`, `ITenantContext`/principal accessor, `IEventRepository`,
`ILogger<TenantAgentEnablementService>`.

**Tests (first):** `TenantAgentEnablementServiceTests`:
- `EnableAsync(publicId)` ⇒ row `Enabled=true` + exactly one `AGENT.ENABLED.SUCCESS` tagged
  `{ agentId, personaName, mode, tenantId|userId }`; idempotent re-enable (single row).
- `DisableAsync(publicId)` ⇒ `Enabled=false`/removed + one `AGENT.DISABLED.SUCCESS`.
- `DisableAsync(ownPrivateId)` ⇒ 409/no-op (still implicitly enabled).
- `EnableAsync(unseenId)` ⇒ 404 (existence-leak-safe).

**Acceptance:**
- [ ] Enable/disable upsert correctly per principal; XOR holds (one column set).
- [ ] Exactly one event per successful write, correctly tagged.
- [ ] Own-private disable is rejected; unseen target is 404.

### T3 — The read seam + query primitives (`ITenantAgentEnablementReader`)

**Scope:** The deliverables 32-18 consumes, on the read-only `ITenantAgentEnablementReader` seam (one
impl shared with the write service). All async, explicit `Principal` arg.
- `IsEnabledForPrincipalAsync(agentId, principal)`: own-private/custom ⇒ `true` (no row); enabled-public
  ⇒ `true`; no-row-public / disabled ⇒ `false`.
- `ListEnabledPublicAgentIdsAsync(principal)` ⇒ set of enabled public ids for the principal.
- `GetEnabledDefaultPersonaIdAsync(principal)` ⇒ the configured `DefaultPersonaName` (32-15) id if
  enabled, else the single enabled persona id if unambiguous, else `null`. (32-18 CONSUMES this for
  `GetSystemDefaultPublicAsync`; it never redefines it.)

**Files:** new `ITenantAgentEnablementReader.cs`; extend `ITenantAgentEnablementService`
(`: ITenantAgentEnablementReader`) / `TenantAgentEnablementService` (T2) to implement the seam.

**Tests (first):** extend `TenantAgentEnablementServiceTests`:
- truth table for `IsEnabledForPrincipalAsync` (own-private true / enabled-public true / no-row-public
  false / disabled-public false / retired-public false).
- `ListEnabledPublicAgentIdsAsync` returns exactly the enabled public set; excludes disabled, no-row,
  and private ids.
- `GetEnabledDefaultPersonaIdAsync`: configured-default-enabled → that id; default-not-enabled +
  exactly-one-other-enabled → that id; nothing/ambiguous enabled → `null`.

**Acceptance:**
- [ ] Truth table passes; primitives are pure-read (no writes/events).
- [ ] `ListEnabledPublicAgentIdsAsync` set is correct and principal-scoped.
- [ ] `GetEnabledDefaultPersonaIdAsync` returns the documented id/null per the rules.

### T4 — Endpoints + DTOs + RBAC (reuse 32-2 `/api/agents` group)

**Scope:** `GET /api/agents/enablement` (any member), `PUT /api/agents/{agentId}/enablement`
(`{enabled:true|false}`), `DELETE /api/agents/{agentId}/enablement` — both writes gated by
`AgentManage` (`agents:manage` = admin+owner; member → 403).

**Files:** modify `Tamma.Api/Endpoints/AgentEndpoints.cs` (handlers); new
`Tamma.Api/Dtos/Agents/AgentEnablementResponse.cs`, `SetEnablementRequest.cs`; modify
`Tamma.Api/Program.cs` (DI register `ITenantAgentEnablementService` Scoped; map the three routes onto
the existing `agentsV2` group with `MemberAccess` for the read and `AgentManage` for the writes).

**Tests (first):** `AgentEnablementEndpointsTests` (in-process `WebApplicationFactory`):
- SaaS `member` → 403 on `PUT`/`DELETE`; member `GET` → 200.
- `tenant_owner`/`tenant_admin` enable/disable → 200.
- platform-catalog write through this group → absent/404 (this group does not expose catalog mutation).

**Acceptance:**
- [ ] RBAC matrix green; DI resolves the chain at host startup (smoke).
- [ ] PUT/DELETE produce `AgentEnablementResponse`; 404 unseen / 409 disable-own-private surfaced.

### T5 — Seeded default (fresh tenant usable out of the box)

**Scope:** Seed the platform `DefaultPersonaName` (32-15, e.g. `claude`) enabled for a fresh tenant.
Insert-missing-only (NEVER reverts an explicit disable). Hook into the existing agent/tenant seeding
path (`AgentEntitySeeder` or a small `TenantEnablementSeeder`).

**Files:** modify `Services/Agents/AgentEntitySeeder.cs` or new `TenantEnablementSeeder.cs`; wire into
the tenant-bootstrap seeding sequence.

**Tests (first):** `TenantEnablementSeederTests`:
- fresh tenant ⇒ `DefaultPersonaName` enabled.
- re-run seeder ⇒ an explicit disable of the default is NOT reverted (insert-missing-only).

**Acceptance:**
- [ ] Fresh tenant has the default persona enabled; usable without manual enablement.
- [ ] Seeder is idempotent and never reverts an admin disable.

### T6 — Isolation, mode matrix, constraint tests

**Scope:** Prove per-tenant scoping, mode keying, and the DB constraints.

**Files:** `TenantAgentEnablementIsolationTests`; extend service tests with a `[Theory]` over
`TammaMode.SingleUser`/`SaaS`; constraint tests (can fold into T1).

**Tests (first):**
- isolation: A enabling persona X never appears in B's `ListAsync`/`IsEnabledForPrincipal`; A cannot
  enable/disable B's private agent (404); A's disable does not affect B.
- mode-parameterized: single-user keys `UserId` (TenantId NULL); SaaS keys `TenantId` (UserId NULL);
  events tag the correct principal.
- constraints: XOR check rejects both/neither; unique-nulls-not-distinct rejects a duplicate
  `(TenantId, UserId, AgentId)`.

**Acceptance:**
- [ ] Cross-tenant isolation holds; mode matrix passes; constraint tests pass.

---

## Story order & dependencies

External prereqs (must land first): **32-1** (Agent + visibility), **32-2** (`/api/agents` group,
`AgentManage`, `IsPlatformOwner`, `AgentRoleSelection` XOR precedent, cross-tenant 404), **32-15**
(persona reframe + `DefaultPersonaName`). Code to their interfaces; use fakes until landed.

Internal: T1 → T2 → T3 → (T4 ∥ T5) → T6. Downstream consumer **32-18** depends on the primitives this
story ships (it is the only story allowed to wire the gate into the registry/resolver); **32-5** runs
the enablement-aware resolver inside `/api/v1/llm/call`. Neither is a blocker for THIS story.

## Verification

```bash
# build (no docker wrapper needed)
dotnet build apps/tamma-elsa/Tamma.sln

# migration is clean (no docker wrapper needed)
dotnet ef migrations has-pending-model-changes --context ControlPlaneDbContext \
  --project apps/tamma-elsa/src/Tamma.Data   # → none

# tests (docker-bound suites need the sg wrapper; session docker group is stale)
sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Api.Tests/ --filter FullyQualifiedName~Enablement"
sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Api.Tests/ --filter FullyQualifiedName~ControlPlaneDbContextModel"

# boundary check: this story must NOT edit the registry/resolver gate (that is 32-18)
git diff --name-only | grep -E 'AgentRegistryService|AgentResolverService' && echo "BOUNDARY VIOLATION (32-18 owns the gate)" || echo "boundary ok"
```

## Risks

- **CP DROP list / model test (T1):** the two recurring gotchas. Amend both in the same change; the
  "second host boot" + CP-model tests are the net. Skipping either fails CI deterministically.
- **Overlap with 32-18 (boundary):** this story ships the interface + primitive ONLY. Any edit to
  `AgentRegistryService`/`AgentResolverService` selection/resolution belongs to 32-18 — the
  verification grep guards it. Cross-referenced both ways in the story.
- **Default-deny locks out a fresh tenant:** mitigated by the seeded default (T5, insert-missing-only).
  The resolve-time fail-loud (no empty fallback) is 32-18's concern, not this story's.
- **Disable-own-private confusion:** own private/custom agents are implicitly enabled; disable via this
  API is a no-op/409. Removal is by archive (32-2). Documented + tested (T2).
- **XOR/keying drift from `AgentRoleSelection`:** mirror the `TammaModelConfiguration` block exactly
  (check-name pattern, unique-nulls-not-distinct). Constraint tests prove it (T6).
- **CP-resident placement (both modes):** unlike `AgentRoleSelection` (tenant-schema in SaaS), this
  table is CP-resident in BOTH modes because it gates the CP catalog and is keyed by tenant id, not
  per `t_<hex>`. Confirm with the Epic 28 team before coding; it drives the DROP-list/CP-model-test
  choice (vs the per-tenant `EfTenantDbMigrator` path).
- **Dependency timing:** 32-1/32-2/32-15 may land just before; code to their interfaces, fakes until
  landed; this story is the entity+primitive owner, not the resolver owner.
