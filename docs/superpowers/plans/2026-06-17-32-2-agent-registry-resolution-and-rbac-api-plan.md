# Story 32-2 — Agent Registry, Resolution & RBAC API — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan phase-by-phase. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every phase writes tests
> before implementation.

**Story:** `docs/stories/epic-32/story-32-2/32-2-agent-registry-resolution-and-rbac-api.md`
**Epic 32 design of record:** `docs/superpowers/specs/2026-06-17-agent-entities-benchmarking-design.md`

---

## Goal

Promote the first-class agent entities introduced in Story 32-1 into the live workflow by adding:
(1) an `AgentRegistryService` that lists/creates/versions/archives agents and persists per-principal
role→agent selections; (2) an enriched `AgentResolverService` that resolves the *effective* agent for
a `(role, phase)` via a deterministic, no-empty-fallback precedence chain; (3) the `/api/agents`
minimal-API surface with per-mode RBAC mirroring the Prompt Store; (4) the DCB events and
`MISSING_CONFIG` integration. Public agents live in the control plane; private agents + selections
live in the per-tenant schema — isolation is structural.

## Non-goals (YAGNI guard)

- NO new agent entity model — `Agent`/`AgentVersion` come from Story 32-1. This plan only adds
  `agent_role_selections` and (if 32-1 didn't) a system-default-per-role marker.
- NO managed execution layer (`IManagedAgent`) — that is Story 32-5; this story returns an enriched
  `ResolvedAgentConfig`, which 32-5 consumes.
- NO provider credential resolution / BYOK (Story 32-3). Resolved configs stay credential-agnostic.
- NO action trail / benchmarking / panels (32-6/32-7/32-10). This story emits only the
  selection/lifecycle/resolve-failure DCB events.
- NO change to resolution semantics elsewhere: `tenant → system → TammaError`
  (`feedback_resolution_no_empty_fallback`) is preserved exactly; the new chain is the agent-entity
  analogue, never an empty/plain fallback.
- NO change to the legacy `/api/v1/agents/*` JSONB endpoints or `AgentResolverService.ResolveAsync`.
- NO dashboard work — admin/tenant UI for agents is Story 32-13.

## Current-state findings (verified 2026-06-17, repo @ main 98cfb1c2)

| Concern | Where it lives today | Note for this story |
|---|---|---|
| Legacy resolver | `src/Tamma.Api/Services/Agents/AgentResolverService.cs` (+ `IAgentResolverService.cs`, `DefaultAgentConfig.cs`) | Merges platform default + tenant JSONB override into `ResolvedAgentConfig`. Reuse its merge/validation; add the new entity-aware resolve methods alongside, do not rewrite. |
| Resolved config DTO | `src/Tamma.Api/Services/Agents/ResolvedAgentConfig.cs` | Already has `Source` (string: `platform-default`/`tenant-override`). Add `AgentId`/`AgentVersion`; extend `Source` value set. |
| Existing agent endpoints | `src/Tamma.Api/Endpoints/AgentEndpoints.cs` → mapped at `Program.cs` ~1679-1688 under `/api/v1/agents` (`GetConfig`/`UpdateConfig`/`ValidateConfig`/`ResolveAgent`/`ResolveForPhase`) | Keep untouched. Add new handlers in the same static class; map a new `/api/agents` group. |
| Legacy JSONB entity | `src/Tamma.Data/Entities/AgentConfig.cs` (`agent_configs`, `TenantId?` null=system default) | Untouched. The new entities are 32-1's `Agent`/`AgentVersion`. |
| Auth policies | `Program.cs` ~966-1085: `AdminAccess`, `OwnerAccess`, `PlatformOwnerAccess`, `MemberAccess`, `SettingsManage`, `PromptManage` (`prompts:manage`), `ConventionManage` (`conventions:manage`) | Add `AgentManage` (`agents:manage` = admin+owner) mirroring `PromptManage`. Public mutation → `PlatformOwnerAccess` / in-handler `IsPlatformOwner`. |
| Permission matrix | `src/Tamma.Api/Auth/Permissions.cs` — `["prompts:manage"] = ["admin","owner"]`, `["conventions:manage"] = ["admin","owner"]` | Add `["agents:manage"] = ["admin","owner"]`. |
| Principal / role claim | `ClaimsPrincipalExtensions.GetUserId`; JWT `role` claim (`JwtService.cs` ~167, `RoleClaimType="role"`); per-tenant role on `HttpContext.Items["TenantRole"]` (`RequireTenantMembershipFilter`) | Use `GetUserId` for single-user principal; the `role` claim + `PermissionRequirement` drive the member 403. Add `IsPlatformOwner()` helper (reads `platformRole`/`platform_admin`). |
| Mode | `src/Tamma.Api/Services/PromptStore/TammaMode.cs` — `ITammaModeProvider` (singleton, process-stable) | Drives principal selection (SaaS ⇒ tenant_id; single-user ⇒ user_id). |
| Tenancy | `ITenantContext` (`src/Tamma.Data/ITenantContext.cs`); `TenantDbContext` (`t_<hex>`) has `AgentConfigs`/`PromptOverrides`/`Conventions`/`DomainEvents`; `ControlPlaneDbContext` is shared | Private agents + `agent_role_selections` → tenant context; public agents → control plane (per 32-1). Single-user user-keyed selection rows → CP context. |
| Events | `IEventRepository.AppendAsync(DomainEvent)` (`src/Tamma.Data/Repositories/EventRepository.cs`); tenant-scope ⇒ tenant store, `TenantId==null` ⇒ platform-events path | All `AGENT.*` events go through this; tenant-scope carry ambient `TenantId`. |
| Migrations | Collapsed baseline: `Migrations/ControlPlane/...InitialControlPlane`, `Migrations/Tenant/...InitialTenant` + snapshots. Entity config lives ONLY in `TammaModelConfiguration.cs`. | Additive `agent_role_selections` migration per context; verify `has-pending-model-changes` → none. |
| RBAC precedent | `PromptEndpoints.cs` + `PromptManage` policy; convention store splits tenant routes (`ConventionManage`) vs system-default routes (`PlatformOwnerAccess`) | Mirror exactly. |
| No-empty-fallback rule | `feedback_resolution_no_empty_fallback`; `PromptStoreService.NoPromptError`, `ConventionStore.NoConventionError` | The 4th resolution branch is a hard `TammaError`, never blank config. |
| Missing-config recorder | `IMissingConfigRecorder` (Missing-Config Notifications epic, not yet merged) | Inject as optional; degrade gracefully. The `AGENT.RESOLVE.FAILED` event is mandatory regardless. |

**Key dependency gap:** Story 32-1's exact `Agent`/`AgentVersion` shape (does it carry
`Visibility`/`Status`/`Role`/active-version pointer; how is the *system-default public agent per role*
marked?) is the one unknown that gates Phase 1. Resolve it before coding (Phase 0).

---

## Architecture (one-paragraph)

A request resolves through `AgentResolverService.ResolveForRoleAsync(role)`: derive principal from
`ITammaModeProvider` + `ITenantContext`/`ClaimsPrincipal`; read the principal's `agent_role_selections`
row for the role; if it points at an agent still in (public ∪ own private), materialise that agent's
active version into `ResolvedAgentConfig` (source `tenant-private`/`tenant-public`); else fall to the
system-default public agent (source `system-public`); else emit `AGENT.RESOLVE.FAILED`, best-effort
record a `MISSING_CONFIG` gap, and throw `TammaError`. `AgentRegistryService` owns list/create/version/
archive/select with public→CP, private→tenant placement and in-handler public-write gating. Endpoints
mirror the Prompt Store: member read-only, admin/owner manage private, platform-owner manages public.

---

## Phased tasks (TDD throughout)

### Phase 0 — 32-1 reconciliation (no code)

- [ ] Read the merged Story 32-1 entities (`Agent.cs`, `AgentVersion.cs`) and their DbSets in
      `TenantDbContext`/`ControlPlaneDbContext` + config in `TammaModelConfiguration.cs`.
- [ ] Confirm: `Visibility` (public/private), `Status` (active/archived), `Role`, `Name`, and the
      **active-version pointer** (flag on `AgentVersion.IsActive` OR `Agent.ActiveVersionId`).
- [ ] Confirm how the **system-default public agent per role** is marked. If 32-1 ships it → use it.
      If not → this story adds a CP `agent_default_selections` table (1 row per role) as part of
      Phase 1, and the story's "(NEW — coordinate with 32-1)" markers resolve here.
- [ ] Update the story's Files table / AC if the 32-1 shape diverges from the assumptions.

**Done when:** every "(NEW — coordinate with 32-1)" marker in the story is reconciled to a concrete
field/table; no assumption remains unverified.

### Phase 1 — `agent_role_selections` entity + migration (core data)

**Files:** new `src/Tamma.Data/Entities/AgentRoleSelection.cs`; DbSet in `TenantDbContext.cs` +
`ControlPlaneDbContext.cs`; config in `TammaModelConfiguration.cs` (principal XOR check;
`UNIQUE NULLS NOT DISTINCT (tenant_id, user_id, role)`); additive EF migrations per context.
(If Phase 0 found no 32-1 system-default marker: also add `agent_default_selections` CP table here.)

**Approach:** mirror `PromptOverride` config exactly for the XOR + unique-nulls-not-distinct index.
Tenant-context table for SaaS; CP-context table for single-user user-keyed rows. Generate migrations
with `dotnet ef migrations add ... --context <Ctx> --output-dir Migrations/<Ctx>`; verify
`has-pending-model-changes` → none for both contexts.

**Tests first** (`tests/Tamma.Data.Tests/` or the docker-bound migration test):
- [ ] XOR violation (both tenant_id and user_id set, or neither) is rejected by the CHECK.
- [ ] Duplicate `(principal, role)` rejected by the unique index; NULL principal halves collapse
      correctly (nulls-not-distinct).
- [ ] Migration applies + rolls back cleanly; `has-pending-model-changes` → none.

**Done when:** both migrations apply, entity round-trips in both contexts, constraints enforced.

### Phase 2 — `IAgentRegistryService` + `AgentRegistryService` (registry core)

**Files:** new `IAgentRegistryService.cs`, `AgentRegistryService.cs`, DTOs under `Dtos/Agents/`
(`AgentSummary`, `AgentWithVersions`, `CreateAgentRequest`, `PublishVersionRequest`,
`SelectRoleRequest`, `AgentListFilter`, `AgentResponse`), `AgentEventTypes.cs` (event-type constants).

**Approach:** constructor injects `ControlPlaneDbContext`/`ITenantDbContextFactory`, `ITenantContext`,
`ITammaModeProvider`, `IEventRepository`, `ClaimsPrincipal`-derived principal, optional
`IMissingConfigRecorder?`. `ListAsync` UNIONs public CP rows with ambient-tenant private rows.
`CreateAsync`/`PublishVersionAsync` reject `visibility == public` unless platform owner (defensive,
belt to the endpoint policy). `SelectForRoleAsync` validates target ∈ (public ∪ own private), upserts
the selection, emits `AGENT.SELECTED_FOR_ROLE.SUCCESS`. Cross-tenant target id → returns null (→ 404
at endpoint). Lifecycle ops emit `AGENT.CREATED/VERSION_PUBLISHED/ARCHIVED.SUCCESS`.

**Tests first** (`tests/Tamma.Api.Tests/Agents/AgentRegistryServiceTests.cs`):
- [ ] `ListAsync` returns public ∪ own private; never another tenant's private rows.
- [ ] `CreateAsync` with `visibility=private` succeeds in tenant schema; `visibility=public` from a
      non-platform-owner throws (forbidden).
- [ ] `PublishVersionAsync` appends a version + emits event; `activate:"prior"` re-activates a prior
      version (rollback).
- [ ] `SelectForRoleAsync` validates target membership (own private OR any public); cross-tenant
      target → not-found; emits exactly one `AGENT.SELECTED_FOR_ROLE.SUCCESS`.
- [ ] `ArchiveAsync` flips status, emits event; archived agent excluded from default `ListAsync`.

**Done when:** registry CRUD + selection works against real per-context DbContexts with correct
placement and events; cross-tenant isolation holds at the service layer.

### Phase 3 — enriched `AgentResolverService` resolution chain (the heart)

**Files:** modify `ResolvedAgentConfig.cs` (add `AgentId`/`AgentVersion`; extend `Source`),
`IAgentResolverService.cs` (declare `ResolveForRoleAsync`/`ResolveForRoleAndPhaseAsync`),
`AgentResolverService.cs` (implement the precedence chain; reuse existing merge/validation to
materialise the pinned version config).

**Approach:** validate role against `RolePhaseMap.ValidRoles` (and phase eligibility when present).
Derive principal from mode. Run the 4-branch chain (private selection → public selection →
system-default public → fail-loud). Materialise the active `AgentVersion.Config` into
`ResolvedAgentConfig` via the existing merge path, stamping `AgentId`/`AgentVersion`/`Source`.
Recompute provenance at resolve time (stale/archived selection target degrades to system default with
a WARN). On the 4th branch: emit `AGENT.RESOLVE.FAILED`, best-effort `IMissingConfigRecorder?.RecordAsync`
(domain `agent`, config_key `role:{role}`, scope `system`), then `throw TammaError("AGENT.RESOLVE.NO_DEFAULT", severity High)`.

**Tests first** (`tests/Tamma.Api.Tests/Agents/AgentResolverServiceTests.cs`):
- [ ] Branch (a): tenant-selected private wins; `Source == "tenant-private"`, correct `AgentId/Version`.
- [ ] Branch (b): tenant-selected public wins over system default; `Source == "tenant-public"`.
- [ ] Branch (c): no selection ⇒ system-default public; `Source == "system-public"`.
- [ ] Branch (d): no system default ⇒ `AGENT.RESOLVE.FAILED` emitted + `TammaError("AGENT.RESOLVE.NO_DEFAULT")`;
      assert **no** `ResolvedAgentConfig` is returned (never blank).
- [ ] Stale selection (target archived) degrades to system default with WARN.
- [ ] Rollback: re-activate v1 after v2 → resolve returns v1 config + `AgentVersion==1`.
- [ ] Unknown role → `ArgumentException`/400 before any resolution; phase ineligibility → 400.
- [ ] `[Theory]` over `TammaMode.SingleUser`/`SaaS`: principal sourced from user_id vs tenant_id;
      selection read from the correct context.
- [ ] Missing-config recorder absent ⇒ event still fires, throw still happens (no crash).

**Done when:** every AC-3 branch + mode matrix + rollback + no-empty-fallback proven green.

### Phase 4 — endpoints + RBAC wiring

**Files:** modify `AgentEndpoints.cs` (List/GetOne/Create/PublishVersion/Archive/SelectForRole/Resolve),
`Permissions.cs` (`agents:manage`), `ClaimsPrincipalExtensions.cs` (`IsPlatformOwner()` if absent),
`Program.cs` (`AgentManage` policy, DI for `IAgentRegistryService`, `/api/agents` route group +
rate-limit groups `ConfigRead`/`ConfigWrite`).

**Approach:** read routes under `MemberAccess` (any member); write routes under `AgentManage`
(admin+owner). In `Create`/`PublishVersion`: reject `visibility==public` unless `IsPlatformOwner()`
→ 403 `agent_public_write_forbidden`. `Resolve` maps `ArgumentException`→400,
`TammaError("AGENT.RESOLVE.NO_DEFAULT")`→404/409 (match prompt-store HTTP mapping). Cross-tenant
private GET → 404. Mirror `PromptEndpoints` structure for principal/mode derivation.

**Tests first** (`tests/Tamma.Api.Tests/Agents/AgentEndpointsTests.cs`, in-process `WebApplicationFactory`):
- [ ] RBAC matrix: member create/version/archive/select → 403; tenant_owner/tenant_admin private
      manage → 200/201; platform owner public create → 201.
- [ ] Tenant `POST /api/agents {visibility:public}` → 403 `agent_public_write_forbidden`;
      tenant `POST /api/agents/{publicId}/versions` → 403.
- [ ] `GET /api/agents` returns public ∪ own private; cross-tenant private GET → 404.
- [ ] `PUT /api/agents/role-selections/{role}` persists + respected by subsequent `GET /api/agents/resolve`.
- [ ] `GET /api/agents/resolve?role=&phase=` returns enriched config; bad role → 400; unresolvable → 404/409.

**Done when:** full RBAC + isolation enforced at the HTTP boundary; no-regression on `/api/v1/agents/*`.

### Phase 5 — DCB events + cross-tenant isolation tests + green-suite gate

**Files:** `tests/Tamma.Api.Tests/Agents/AgentEventsTests.cs`,
`tests/Tamma.Api.Tests/Agents/AgentRegistryIsolationTests.cs`.

**Approach:** assert exact event shapes/tags; assert two tenants selecting the same public agent for a
role resolve independently (no bleed); run the whole suite + `has-pending-model-changes`.

**Tests first:**
- [ ] Selection appends exactly one `AGENT.SELECTED_FOR_ROLE.SUCCESS` with `{agentId, role, source}`.
- [ ] Unresolvable role appends exactly one `AGENT.RESOLVE.FAILED` with `{role, phase, source, mode}`.
- [ ] Lifecycle ops append create/version/archive events with correct tags.
- [ ] Two-tenant invariant: same public agent selected → separate selection rows, independent resolve.
- [ ] `sg docker -c "dotnet test apps/tamma-elsa/..."` full suite green; both contexts'
      `has-pending-model-changes` → none.

**Done when:** all events verified, isolation invariant holds, full suite + migration check green.

---

## Sequencing

Phase 0 → Phase 1 → Phase 2 → Phase 3 → Phase 4 → Phase 5.
Phase 0 is a hard gate (32-1 shape). Phases 2 and 3 may overlap once Phase 1's entity exists (registry
and resolver share the selection table). Phase 4 needs 2+3. Phase 5 is the closeout gate.

## Risks

- **32-1 shape drift** (primary): the resolver/registry assume `Agent.Visibility`/`Status`, an
  active-version pointer, and a system-default-per-role marker. If 32-1 modelled these differently,
  Phase 0 must adjust the story + this plan before coding. Mitigation: Phase 0 is a no-code gate.
- **Empty/plain fallback regression**: the cardinal project rule. The 4th branch MUST throw, never
  return blank — pin it with branch (d) tests AND assert no config object is produced. Mirror
  `PromptStoreService.NoPromptError` precisely.
- **Cross-schema selection target**: a tenant selection may point at a CP-resident public agent — there
  is no DB FK across schemas. The registry validates membership in code at write AND recomputes at
  resolve; never trust the stored `Visibility`. Stale/deleted targets degrade to system default, not
  to error, except when no system default exists.
- **Cross-tenant leak**: private agents/selections must never escape the tenant schema. Enforce via the
  per-tenant `TenantDbContext`; cover with `AgentRegistryIsolationTests` (real two-tenant setup), not
  mocks. Use 404 (not 403) for cross-tenant private reads to avoid existence leak.
- **Event-store topology (Story 28-1)**: `AGENT.RESOLVE.FAILED` for a *system* gap is platform-scope;
  ensure system-scope resolve failures append via the platform-events path (`TenantId==null`) while
  selection/lifecycle events stay tenant-scoped. Mirror the recorder discipline from the missing-config
  plan.
- **Missing-config epic not merged**: inject `IMissingConfigRecorder?` optional; the gap record is
  best-effort, the event + throw are not. Tests cover the absent-recorder path.
- **Migration discipline**: `agent_role_selections` is additive (new table), but collapsed-baseline
  rules still apply — entity config only in `TammaModelConfiguration.cs`, verify
  `has-pending-model-changes` → none for BOTH contexts after generating migrations.
- **Public-write gate split**: route policy (`AgentManage`) admits tenant admins; the platform-owner
  check is in-handler. A missing in-handler check would let a tenant admin mint a public agent — pin
  with the `agent_public_write_forbidden` 403 tests on both create and version routes.

## Acceptance criteria (plan-level)

- [ ] All 15 story ACs satisfied; each mapped to at least one passing test.
- [ ] Resolution precedence: all 4 branches green, including the no-empty-fallback throw + event.
- [ ] Per-mode (single-user user_id vs SaaS tenant_id) proven by mode-parameterized tests.
- [ ] RBAC: member 403s, tenant private-only management, platform-owner public management, tenant
      public-write 403 — all green.
- [ ] Cross-tenant isolation (private read 404, no list bleed, two-tenant independent resolve) green.
- [ ] Rollback-to-prior-version resolution green.
- [ ] DCB events `AGENT.SELECTED_FOR_ROLE.SUCCESS` / `AGENT.RESOLVE.FAILED` (+ lifecycle) emitted with
      correct tags.
- [ ] No regression on `/api/v1/agents/*` and `AgentResolverService.ResolveAsync`.
- [ ] Both contexts' migrations apply + roll back; `has-pending-model-changes` → none.
- [ ] Full `dotnet test` suite green (`sg docker -c "dotnet test ..."`).
