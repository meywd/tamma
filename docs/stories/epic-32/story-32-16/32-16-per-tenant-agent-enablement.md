# Story 32-16: Per-Tenant Agent/Persona Enablement (`TenantAgentEnablement`)

Status: done
<!-- Flipped drafted -> done 2026-08-18. The deliverable named in the acceptance criteria
     was located in apps/tamma-elsa/src (and its suites in apps/tamma-elsa/tests) before this
     header was changed — not taken from a changelog. The per-story evidence is recorded
     inline on this story's line in docs/sprint-status.yaml.
-->

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)

## User Story

As a **tenant owner/admin (SaaS) or self-hosted user (single-user)**,
I want to control **which public personas** (and my own private/custom agents) are part of my tenant's usable catalog — enabling and disabling them for the whole tenant,
So that my tenant's members only ever select and run from a curated set of agents, the usable set is `enabled(public) ∪ own-private` rather than every public persona on the platform, and that catalog decision is made **once per tenant** (not per user) with a full audit trail.

## Priority

P0 — This is the **genuinely missing layer** of the locked agent model (design of record rule 6, §3.3). Neither the shipped 32-1 (`Agent`/`AgentVersion`) nor the drafted 32-2 (registry/resolution/selection) has it: `AgentRoleSelection` answers "which agent serves role X for principal P" but lets a principal select **any** visible public agent. Without enablement, "the tenant enables which personas exist for it" has no home, and selection (32-2) has no gate to constrain it. Enablement is the **catalog-membership** primitive that 32-18 (the registry enablement gate) consumes; sequenced step C, ahead of 32-18 (step E) and the call-LLM resolution path (32-5, step F).

## Context

The Epic 32 architecture pivot (`docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md`) reframes public agents as **named cross-role personas** (`claude`, `gemini`, `codegpt`) that preset provider+model+config and work for all roles (32-15). The locked model adds a constraint the original 32-1/32-2 never modelled: **enablement is per-tenant** (rule 6). The tenant decides which personas exist for it; its users simply use Tamma with what's enabled; there is **no per-user enablement layer** — exactly mirroring CLAUDE.md's "no per-user override layer in SaaS" for prompts.

Two distinct concepts must not be conflated:

- **Enablement** (this story) = *catalog membership*. Which personas/agents are part of this tenant's usable set. Per-tenant. Owns `TenantAgentEnablement`.
- **Selection** (`AgentRoleSelection`, 32-2) = *role binding*. Which agent (from the enabled set) serves a given role for the principal.

Enablement is the **gate that constrains selection**: a public persona that is not enabled for the tenant is not selectable, not resolvable, and not visible in the tenant's catalog. The tenant's usable set tightens the design-of-record's `public ∪ own-private` to **`enabled(public) ∪ own-private`**. Own private/custom agents (32-17) are **implicitly enabled** (you authored them); enablement is primarily about which **public personas** the tenant exposes.

This story owns the **entity**, the **enable/disable API**, the **events**, and — critically — the **`IsEnabledForPrincipal` query primitive**. It does **not** own the registry/resolver wiring of the gate (`CanUse` → `IsPublic && IsEnabledForPrincipal`, the enablement-aware `SelectForRoleAsync`/`ResolveUsableAgentAsync`/`ListVisibleAsync`/`GetSystemDefaultPublicAsync`). That wiring is the **sibling story 32-18** (a 32-2 amendment), which **consumes** the primitive this story defines. Keeping the boundary precise prevents the two stories from overlap-implementing the gate. See **Dev Notes → Boundary with 32-18**.

`TenantAgentEnablement` is **CP-resident in SaaS** (control-plane, like the public `Agent` catalog it gates and like every cross-tenant-shared decision) and **user-keyed in single-user** mode, following the same XOR/index discipline as `AgentRoleSelection` (32-2) and `prompt_overrides` (Epic 27). Because it is a NEW control-plane / public-schema table, it MUST be added to the `Program.cs` startup-reset DROP list and the `ControlPlaneDbContextModelTests` strict entity list (see AC8, AC9 and Dev Notes).

## Acceptance Criteria

1. **New entity `TenantAgentEnablement`** (`apps/tamma-elsa/src/Tamma.Data/Entities/TenantAgentEnablement.cs`) with fields: `Id` (UUID PK), `TenantId` (UUID, NULL in single-user), `UserId` (UUID, NULL in SaaS), `AgentId` (UUID NOT NULL — a public persona OR an own private/custom agent), `Enabled` (BOOLEAN NOT NULL), `CreatedAt`/`CreatedBy`, `UpdatedAt`/`UpdatedBy`. EF config carries the **principal XOR** CHECK constraint and the **`UNIQUE NULLS NOT DISTINCT (TenantId, UserId, AgentId)`** index, mirroring `AgentRoleSelection` exactly.

2. **`IsEnabledForPrincipalAsync(agentId, principal)` query primitive** is exposed by a new **read seam `ITenantAgentEnablementReader`** (`Tamma.Api/Services/Agents/`), which the write/admin `ITenantAgentEnablementService : ITenantAgentEnablementReader` extends (see AC2a for the ISP split). Semantics:
   - An **own private/custom agent** is **implicitly enabled** (returns `true` without requiring a row) — you authored it.
   - A **public persona** is enabled **iff** an enablement row exists for the principal `(tenantId XOR userId, agentId)` with `Enabled = true`. Absent a row, a public persona is **NOT enabled** (default-deny for catalog membership — see AC10 for the seeded-default carve-out).
   - A `ListEnabledPublicAgentIdsAsync(principal)` companion returns the set of public agent ids enabled for the principal (for the consumer in 32-18's `ListVisibleAsync`).
   - A `GetEnabledDefaultPersonaIdAsync(principal)` companion returns the principal's **enabled default persona id** — the configured `DefaultPersonaName` (32-15) if it is enabled, else the single enabled persona if unambiguous, else `null` — for the consumer in 32-18's `GetSystemDefaultPublicAsync` (which 32-18 CONSUMES, never redefines).

2a. **ISP split — read seam vs write/admin service.** The enablement contract is split per the Interface Segregation Principle into:
   - **`ITenantAgentEnablementReader`** (read-only): `Task<bool> IsEnabledForPrincipalAsync(Guid agentId, Principal principal, CancellationToken ct)`, `Task<IReadOnlyList<Guid>> ListEnabledPublicAgentIdsAsync(Principal principal, CancellationToken ct)`, `Task<Guid?> GetEnabledDefaultPersonaIdAsync(Principal principal, CancellationToken ct)`. This is the seam **32-18 injects and consumes** (it never sees the write methods).
   - **`ITenantAgentEnablementService : ITenantAgentEnablementReader`** (write/admin): adds `EnableAsync`/`DisableAsync`/`ListAsync`.
   - **One implementation** (`TenantAgentEnablementService`) implements both interfaces. The read methods are async and take an explicit `Principal` argument (matching the signatures 32-18 calls).

3. **Enable API** — `PUT /api/agents/{agentId}/enablement` (body `{ "enabled": true }` or a dedicated `POST .../enable`) upserts an enablement row for the **current principal's tenant** (SaaS) or **user** (single-user), sets `Enabled = true`, and emits `AGENT.ENABLED.SUCCESS`. Returns `200` with the resolved enablement state. Enabling an agent the tenant cannot see (not public and not its own private) → `404` (existence-leak-safe, matching 32-2's cross-tenant rule).

4. **Disable API** — `DELETE /api/agents/{agentId}/enablement` (or `POST .../disable`) sets `Enabled = false` (or removes the row) for the principal, and emits `AGENT.DISABLED.SUCCESS`. Returns `200`. Disabling a public persona removes it from the tenant's usable set; the consumer gate (32-18) makes it non-selectable/non-resolvable. **Disabling an own private/custom agent is a no-op / `409`** (private agents are implicitly enabled by authorship and cannot be removed from your own catalog this way — they are removed by archiving the agent, 32-2).

5. **List API** — `GET /api/agents/enablement` returns the tenant's enablement view: every visible public persona with its `enabled` flag (true/false) plus own-private agents marked implicitly-enabled. This is the catalog-management surface; **any tenant member may read it** (reads are not gated).

6. **Per-mode RBAC** mirrors the Prompt Store / 32-2:
   - SaaS `member` → **403** on the enable/disable writes (`PUT`/`DELETE` / enable / disable). Reads (`GET /api/agents/enablement`) allowed.
   - SaaS `tenant_owner` / `tenant_admin` → may enable/disable for **their own tenant only**.
   - **Single-user** mode → the sole user (auto-owner) may enable/disable for themselves; no member gate; principal is `UserId`.
   - **Public-catalog management** (which personas exist platform-wide) is explicitly **out of scope** here and stays `PlatformOwnerAccess` (NOT `OwnerAccess`, which admits every personal-tenant owner). This story only touches per-tenant enablement of an already-existing public persona.

7. **DCB events** `AGENT.ENABLED.SUCCESS` and `AGENT.DISABLED.SUCCESS` are emitted via `IEventRepository.AppendAsync`, tagged `{ agentId, personaName, mode, tenantId | userId }`. Exactly one event per successful write. Tenant-scope events carry the ambient `TenantId`; single-user/platform-scope events resolve via the platform-events path (`TenantId == null`, principal recorded as `userId`).

8. **`Program.cs` startup-reset DROP list** is amended: the new public-schema/control-plane table `tenant_agent_enablements` is appended to the destructive test-host wipe list ("Wiping Tamma-managed public-schema tables") so a second host boot does not fail with `relation "tenant_agent_enablements" already exists`. (Tenant-schema `t_<hex>` tables do NOT go in that list; this table is CP-resident — see AC9.)

9. **`ControlPlaneDbContextModelTests.Model_Has_ExpectedControlPlaneEntities`** strict `BeEquivalentTo` list is updated to include `TenantAgentEnablement`, and the entity is registered as a `DbSet` on **`ControlPlaneDbContext`** (CP-resident; SaaS rows keyed by `TenantId`, single-user rows keyed by `UserId`), with its EF config in the single `TammaModelConfiguration` source. `dotnet ef migrations has-pending-model-changes --context ControlPlaneDbContext` reports none after the new migration.

10. **No-empty-fallback alignment + seeded default**: enablement does NOT silently fall back to "all public personas enabled." A tenant that has enabled nothing has an empty enabled-public set, and 32-18's `GetSystemDefaultPublicAsync` fails loud (no empty fallback) — **except** that a fresh tenant is seeded with the platform default persona enabled (the `DefaultPersonaName`, e.g. `claude`), so a brand-new tenant is usable out of the box. The seeder is **insert-missing-only** (never reverts an explicit disable). This story provides the seeding hook; the resolve-time fail-loud lives in 32-18.

11. **Cross-tenant isolation**: a tenant cannot enable/disable for another tenant; an enablement write/read scopes to the ambient principal only. Targeting another tenant's private agent → `404`. Enabling a public persona for tenant A never affects tenant B's enabled set.

12. **Unit + integration tests** cover: enable/disable upsert + events; `IsEnabledForPrincipalAsync` for (own-private implicit-true, enabled-public true, no-row-public false); `GetEnabledDefaultPersonaIdAsync` (returns the configured `DefaultPersonaName` id when enabled, the single enabled persona when unambiguous, and `null` when nothing/ambiguous is enabled); member 403 on writes; reads allowed for member; single-user vs SaaS principal keying (mode-parameterized); cross-tenant isolation (A's enablement never affects B); disable-own-private → 409 no-op; seeded-default tenant has its default persona enabled; the XOR check + unique-nulls-not-distinct constraint; and `has-pending-model-changes` → none.

## Technical Design

### Where it lives

```
apps/tamma-elsa/src/Tamma.Data/Entities/
  TenantAgentEnablement.cs                 # NEW — the enablement entity (CP-resident; user-keyed single-user)

apps/tamma-elsa/src/Tamma.Data/
  TammaModelConfiguration.cs               # MODIFY — entity config: XOR check + unique-nulls-not-distinct index
  ControlPlaneDbContext.cs                 # MODIFY — DbSet<TenantAgentEnablement>
  Migrations/ControlPlane/*_AddTenantAgentEnablements.cs   # NEW (generated)

apps/tamma-elsa/src/Tamma.Api/Services/Agents/
  ITenantAgentEnablementReader.cs          # NEW — read seam (IsEnabledForPrincipalAsync / ListEnabledPublicAgentIdsAsync / GetEnabledDefaultPersonaIdAsync); 32-18 injects this
  ITenantAgentEnablementService.cs         # NEW — : ITenantAgentEnablementReader; adds Enable/Disable/List
  TenantAgentEnablementService.cs          # NEW — impl of BOTH (upsert, events, implicit-private rule, the three read primitives)
  AgentEnablementEventTypes.cs             # NEW — AGENT.ENABLED.SUCCESS / AGENT.DISABLED.SUCCESS constants

apps/tamma-elsa/src/Tamma.Api/Endpoints/
  AgentEndpoints.cs                        # MODIFY — add Enable/Disable/ListEnablement handlers under /api/agents

apps/tamma-elsa/src/Tamma.Api/Dtos/Agents/
  AgentEnablementResponse.cs, SetEnablementRequest.cs      # NEW — request/response DTOs

apps/tamma-elsa/src/Tamma.Api/Program.cs   # MODIFY — DI registration; route mapping; STARTUP-RESET DROP-LIST amend (AC8)

apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentEntitySeeder.cs (or a small TenantEnablementSeeder)
                                           # MODIFY/NEW — seed DefaultPersonaName enabled for a fresh tenant (AC10, insert-missing-only)
```

### `TenantAgentEnablement` entity (NEW)

```csharp
// Tamma.Data/Entities/TenantAgentEnablement.cs
namespace Tamma.Data.Entities;

/// <summary>
/// Per-tenant agent/persona enablement (Epic 32, rule 6 / design §3.3).
/// Catalog membership: which PUBLIC personas a tenant exposes. Own private/custom
/// agents are implicitly enabled (no row required). CP-resident in SaaS (keyed by
/// TenantId); user-keyed in single-user (keyed by UserId). Exactly one of
/// TenantId/UserId is non-null (principal XOR), mirroring AgentRoleSelection /
/// prompt_overrides. There is NO per-user enablement layer in SaaS.
/// </summary>
public class TenantAgentEnablement
{
    public Guid Id { get; set; }

    /// <summary>Set in SaaS; NULL in single-user. XOR with UserId.</summary>
    public Guid? TenantId { get; set; }

    /// <summary>Set in single-user; NULL in SaaS. XOR with TenantId.</summary>
    public Guid? UserId { get; set; }

    /// <summary>A public persona OR an own private/custom agent. Logical FK only —
    /// public agents live in the CP catalog; no cross-schema DB FK. The service
    /// validates the target is in (public ∪ own-private) at write time.</summary>
    public Guid AgentId { get; set; }

    /// <summary>True = part of the tenant's usable catalog. A disable sets false
    /// (or removes the row). Absent row for a public persona = not enabled.</summary>
    public bool Enabled { get; set; }

    public DateTime CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
}
```

EF model config (in `TammaModelConfiguration.cs`, the single source — **identical discipline to `AgentRoleSelection`**):

```csharp
modelBuilder.Entity<TenantAgentEnablement>(b =>
{
    b.ToTable("tenant_agent_enablements");
    b.HasKey(x => x.Id);
    b.Property(x => x.AgentId).IsRequired();
    b.Property(x => x.Enabled).IsRequired();

    // principal XOR (mirrors prompt_overrides / agent_role_selections)
    b.ToTable(t => t.HasCheckConstraint(
        "ck_tenant_agent_enablements_principal_xor",
        "((tenant_id IS NOT NULL AND user_id IS NULL) OR (tenant_id IS NULL AND user_id IS NOT NULL))"));

    // UNIQUE NULLS NOT DISTINCT (tenant_id, user_id, agent_id) — one row per (principal, agent)
    b.HasIndex(x => new { x.TenantId, x.UserId, x.AgentId })
        .IsUnique()
        .AreNullsDistinct(false);
});
```

> **CP-resident, not tenant-schema.** Like the public `Agent` catalog it gates, `tenant_agent_enablements` lives in the **control plane** (`ControlPlaneDbContext`), with SaaS rows scoped by `TenantId` and single-user rows by `UserId`. This is the same dual-keying that `prompt_overrides`/`AgentRoleSelection` use for their CP-resident single-user path; here the SaaS path is ALSO CP-resident because enablement is a cross-tenant-shared catalog decision keyed by tenant id, not a `t_<hex>` tenant-private row. Therefore it joins the **CP DROP list (AC8)** and the **`ControlPlaneDbContextModelTests` strict list (AC9)** — and does NOT go through the per-tenant `EfTenantDbMigrator`.

### `ITenantAgentEnablementReader` + `ITenantAgentEnablementService` (NEW) — ISP split: read seam + write/admin

```csharp
// Tamma.Api/Services/Agents/ITenantAgentEnablementReader.cs
// The READ-ONLY seam 32-18 injects + consumes (it never touches the write methods).
public interface ITenantAgentEnablementReader
{
    /// <summary>True iff the agent is part of the principal's usable catalog:
    /// own private/custom => implicitly true; public persona => an enabled row exists.
    /// Absent row for a public persona => false (default-deny; seeded-default carve-out
    /// in AC10). This is the gate 32-18 calls from CanUse / SelectForRoleAsync /
    /// ResolveUsableAgentAsync.</summary>
    Task<bool> IsEnabledForPrincipalAsync(Guid agentId, Principal principal, CancellationToken ct);

    /// <summary>The set of PUBLIC agent ids enabled for the principal — for 32-18's
    /// ListVisibleAsync (enabled(public) ∪ own-private).</summary>
    Task<IReadOnlyList<Guid>> ListEnabledPublicAgentIdsAsync(Principal principal, CancellationToken ct);

    /// <summary>The principal's enabled DEFAULT persona id: the configured DefaultPersonaName
    /// (32-15) if enabled, else the single enabled persona if unambiguous, else null. The
    /// enabled-default primitive 32-18's GetSystemDefaultPublicAsync CONSUMES (never redefines).</summary>
    Task<Guid?> GetEnabledDefaultPersonaIdAsync(Principal principal, CancellationToken ct);
}

// Tamma.Api/Services/Agents/ITenantAgentEnablementService.cs
// The WRITE/ADMIN service — extends the read seam; one impl implements both.
public interface ITenantAgentEnablementService : ITenantAgentEnablementReader
{
    /// <summary>Enable a public persona (or confirm an own private/custom agent)
    /// for the current principal. Validates target ∈ (public ∪ own-private) else 404.
    /// Emits AGENT.ENABLED.SUCCESS. Idempotent upsert.</summary>
    Task<AgentEnablementState> EnableAsync(Guid agentId, CancellationToken ct = default);

    /// <summary>Disable a public persona for the current principal (sets Enabled=false
    /// or removes the row). Emits AGENT.DISABLED.SUCCESS. Disabling an OWN private/custom
    /// agent is a no-op/409 (it is implicitly enabled; remove via archive, 32-2).</summary>
    Task<AgentEnablementState> DisableAsync(Guid agentId, CancellationToken ct = default);

    /// <summary>Catalog view: every visible public persona with its enabled flag,
    /// plus own-private agents (implicitly enabled). Any member may read.</summary>
    Task<IReadOnlyList<AgentEnablementState>> ListAsync(CancellationToken ct = default);
}

public sealed record AgentEnablementState(
    Guid AgentId,
    string? PersonaName,
    bool Enabled,
    bool ImplicitlyEnabled);   // true for own-private (cannot be toggled here)
```

> **One implementation, two interfaces.** `TenantAgentEnablementService` implements `ITenantAgentEnablementService` (and therefore `ITenantAgentEnablementReader`). 32-18 depends only on `ITenantAgentEnablementReader` (registered to the same singleton/scoped instance) — it gets the three async read methods (`IsEnabledForPrincipalAsync`, `ListEnabledPublicAgentIdsAsync`, `GetEnabledDefaultPersonaIdAsync`) and never the write methods. The read methods take an explicit `Principal` argument so 32-18 passes its already-resolved principal; the write methods derive the principal from the ambient request.

The implementation derives the principal from `ITammaModeProvider` + `ITenantContext`/`ClaimsPrincipal` (SaaS ⇒ `TenantId`; single-user ⇒ `UserId`), reads/writes `ControlPlaneDbContext.TenantAgentEnablements`, validates the target id is in (public CP catalog ∪ ambient principal's own-private agents), and appends the DCB event.

### Endpoints (`AgentEndpoints.cs`, extend the existing `/api/agents` group from 32-2)

```csharp
// Reads — any member
public static async Task<IResult> ListEnablement(
    ITenantAgentEnablementService svc, ClaimsPrincipal user)
    => Results.Ok(await svc.ListAsync());   // 200 catalog view

// Writes — tenant_owner/tenant_admin (member → 403 via AgentManage policy)
public static async Task<IResult> SetEnablement(
    Guid agentId, SetEnablementRequest req,
    ITenantAgentEnablementService svc)
{
    var state = req.Enabled
        ? await svc.EnableAsync(agentId)
        : await svc.DisableAsync(agentId);     // 404 unseen / 409 disable-own-private
    return Results.Ok(AgentEnablementResponse.From(state));
}
```

`Program.cs` route mapping (reusing 32-2's `/api/agents` group, `AgentManage` policy = `agents:manage` = admin+owner):

```csharp
agentsV2.MapGet("/enablement", AgentEndpoints.ListEnablement);                  // MemberAccess (reads)
agentsV2.MapPut("/{agentId:guid}/enablement", AgentEndpoints.SetEnablement)     // PUT {enabled:true|false}
    .RequireAuthorization("AgentManage").RequireRateLimiting("ConfigWrite");
agentsV2.MapDelete("/{agentId:guid}/enablement", AgentEndpoints.DisableEnablement)
    .RequireAuthorization("AgentManage").RequireRateLimiting("ConfigWrite");
```

> Public-catalog management (creating/retiring the personas themselves) is **not** here — it stays under `PlatformOwnerAccess` per 32-15/32-2. This route group only toggles an existing public persona's membership in *this* tenant's catalog.

### DCB events

| Event | Tags | When |
|---|---|---|
| `AGENT.ENABLED.SUCCESS` | `{ agentId, personaName, mode, tenantId \| userId }` | enable upsert (`Enabled=true`) |
| `AGENT.DISABLED.SUCCESS` | `{ agentId, personaName, mode, tenantId \| userId }` | disable (`Enabled=false`/removed) |

Appended via `IEventRepository.AppendAsync`; tenant-scope events carry ambient `TenantId`; single-user events carry `userId` (`TenantId == null`).

### Startup-reset DROP list (AC8) — explicit codebase gotcha

`tenant_agent_enablements` is a NEW control-plane / public-schema table. Append it to the destructive test-host wipe list in `Program.cs` (the "Wiping Tamma-managed public-schema tables" block) alongside `agent_role_selections`, `prompt_overrides`, the public `Agent`/`AgentVersion` catalog, etc. Without this, a second test-host boot fails with `relation "tenant_agent_enablements" already exists`. It is CP-resident, so it goes in the CP wipe list — **not** the per-tenant `EfTenantDbMigrator` path (which owns `t_<hex>` tables only).

### Boundary with 32-18 (do NOT implement the gate here)

This story owns: the **entity**, its EF config + migration, the **enable/disable/list API**, the **events**, the **seeding hook**, and the **`ITenantAgentEnablementReader` read seam** with its three async primitives (`IsEnabledForPrincipalAsync`, `ListEnabledPublicAgentIdsAsync`, `GetEnabledDefaultPersonaIdAsync`).

Story **32-18** (the 32-2 amendment) owns the **consumption**: rewriting `CanUse()` to `IsPublic && IsEnabledForPrincipalAsync`, and threading the primitives through `SelectForRoleAsync` / `ResolveUsableAgentAsync` / `ListVisibleAsync` / `GetSystemDefaultPublicAsync` (including the resolve-time fail-loud when a tenant has enabled nothing — AC10, and consuming `GetEnabledDefaultPersonaIdAsync` for the enabled-default lookup). 32-18 injects **`ITenantAgentEnablementReader`** (the read-only seam this story ships) and uses it; this story MUST NOT modify the registry/resolver selection or resolution code. The seam is the `ITenantAgentEnablementReader` interface.

## Dependencies

**Internal:**

- **Story 32-1** (Agent entity model & versioned saved config) — establishes `Agent`/`AgentVersion` and the public-vs-private visibility; enablement references `Agent.Id` and reads visibility to apply the implicit-private rule. Hard prerequisite.
- **Story 32-2** (Agent registry, resolution & RBAC API) — provides the `/api/agents` route group, the `AgentManage` policy (`agents:manage` = admin+owner), `IsPlatformOwner()`, the `AgentRoleSelection` XOR/index precedent this story mirrors, and the cross-tenant 404 convention. Hard prerequisite.
- **Story 32-15** (Persona reframe + seeding) — public personas (cross-role named agents) are what enablement primarily toggles; supplies `personaName` for event tags and the `DefaultPersonaName` seeded-default (AC10). Hard prerequisite for meaningful semantics.
- **Epic 27** (Prompt Store) — the per-mode RBAC + dual-keying model this mirrors (`prompt_overrides`).
- **Epic 28** (schema-per-tenant) — the CP-vs-tenant placement decision (this table is CP-resident, like the public catalog it gates).

**Consumers (downstream, not blockers):**

- **Story 32-18** (Agent registry enablement gate + Epic-27 prompt source — a 32-2 amendment) — injects `ITenantAgentEnablementReader` and consumes `IsEnabledForPrincipalAsync` / `ListEnabledPublicAgentIdsAsync` / `GetEnabledDefaultPersonaIdAsync` to gate selection/resolution/visibility and to resolve the enabled default. The **primary** consumer.
- **Story 32-5** (Call-LLM endpoint + managed execution) — resolution inside `/api/v1/llm/call` runs through 32-18's enablement-aware resolver, so an un-enabled persona cannot be executed.
- **Story 32-17** (Custom-agent prompts) — own private/custom agents are implicitly enabled by this story's rule; no enablement row needed.

**External:** none new (reuses EF Core, `IEventRepository`, the existing auth/policy stack).

## Testing Strategy

Tests are xUnit under `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/`. Docker-bound suites run via `sg docker -c "dotnet test ..."` (session docker group is stale; see `reference_dotnet_test_docker`). TDD: write the failing test first.

1. **Enable/disable upsert + events** (`TenantAgentEnablementServiceTests`): `EnableAsync(publicId)` creates a row `Enabled=true` and appends exactly one `AGENT.ENABLED.SUCCESS` tagged `{ agentId, personaName, mode, tenantId|userId }`; `DisableAsync(publicId)` flips to false (or removes) and appends one `AGENT.DISABLED.SUCCESS`. Re-enable is idempotent (single row, no duplicate event family explosion).
2. **`IsEnabledForPrincipalAsync` truth table**: own-private/custom agent ⇒ `true` with no row; enabled public persona ⇒ `true`; public persona with no row ⇒ `false`; disabled public persona ⇒ `false`.
3. **`ListEnabledPublicAgentIdsAsync`**: returns exactly the set of enabled public ids for the principal; excludes disabled and no-row public; excludes private (private handled separately by the consumer).
3a. **`GetEnabledDefaultPersonaIdAsync`**: returns the configured `DefaultPersonaName` persona id when that persona is enabled; returns the single enabled persona's id when `DefaultPersonaName` is not enabled but exactly one other persona is; returns `null` when nothing is enabled or the choice is ambiguous (multiple enabled, none the configured default). Pure-read, no writes/events.
4. **RBAC matrix** (`AgentEnablementEndpointsTests`, in-process `WebApplicationFactory`): SaaS `member` → 403 on `PUT`/`DELETE /api/agents/{id}/enablement`; member `GET /api/agents/enablement` → 200; `tenant_owner`/`tenant_admin` enable/disable → 200; **public-catalog mutation is not exposed here** (asserted absent / 404 on any platform-catalog write attempt through this group).
5. **Mode-parameterized principal** (`[Theory]` over `TammaMode.SingleUser`/`SaaS`): enable keyed by `UserId` (single-user) vs `TenantId` (SaaS); the correct column is set, the other is NULL (XOR holds); events tag the correct principal.
6. **Cross-tenant isolation** (`TenantAgentEnablementIsolationTests`): tenant A enabling persona X never appears in tenant B's `ListAsync`/`IsEnabledForPrincipal`; A cannot enable/disable targeting B's private agent (404); A's disable does not affect B.
7. **Disable-own-private** ⇒ `409`/no-op (own private/custom agent stays implicitly enabled; not removable via this API).
8. **404 on unseen target**: enabling an agent that is neither public nor the principal's own private ⇒ 404 (existence-leak-safe).
9. **Seeded default** (`TenantEnablementSeederTests`): a fresh tenant has the platform `DefaultPersonaName` (e.g. `claude`) enabled out of the box; the seeder is insert-missing-only (running it again does NOT revert an explicit disable of the default).
10. **Constraint tests**: the XOR CHECK rejects a row with both/neither principal set; the unique-nulls-not-distinct index rejects a duplicate `(TenantId, UserId, AgentId)`.
11. **CP model contract**: `ControlPlaneDbContextModelTests.Model_Has_ExpectedControlPlaneEntities` includes `TenantAgentEnablement`; `dotnet ef migrations has-pending-model-changes --context ControlPlaneDbContext` → none; a second test-host boot succeeds (DROP-list amendment proven).

## Estimated Effort

3-4 days

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Data/Entities/TenantAgentEnablement.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` | Modify (entity config: XOR check + unique-nulls-not-distinct index) |
| `apps/tamma-elsa/src/Tamma.Data/ControlPlaneDbContext.cs` | Modify (DbSet) |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/*_AddTenantAgentEnablements.cs` | Create (generated) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/ITenantAgentEnablementReader.cs` | Create (read seam: `IsEnabledForPrincipalAsync` / `ListEnabledPublicAgentIdsAsync` / `GetEnabledDefaultPersonaIdAsync`; injected by 32-18) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/ITenantAgentEnablementService.cs` | Create (`: ITenantAgentEnablementReader`; adds `EnableAsync`/`DisableAsync`/`ListAsync`) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/TenantAgentEnablementService.cs` | Create (implements both interfaces) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentEnablementEventTypes.cs` | Create (`AGENT.ENABLED.SUCCESS` / `AGENT.DISABLED.SUCCESS`) |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/AgentEndpoints.cs` | Modify (add ListEnablement/SetEnablement/DisableEnablement handlers) |
| `apps/tamma-elsa/src/Tamma.Api/Dtos/Agents/AgentEnablementResponse.cs`, `SetEnablementRequest.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentEntitySeeder.cs` (or new `TenantEnablementSeeder.cs`) | Modify/Create (seed `DefaultPersonaName` enabled, insert-missing-only) |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (DI registration; `/api/agents/enablement` routes; **STARTUP-RESET DROP-LIST amend**) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/TenantAgentEnablementServiceTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/AgentEnablementEndpointsTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/TenantAgentEnablementIsolationTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/TenantEnablementSeederTests.cs` | Create |
| `apps/tamma-elsa/tests/.../Epic28/ControlPlaneDbContextModelTests.cs` | Modify (add `TenantAgentEnablement` to strict entity list) |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
2. Searched `.dev/` directory for related spikes, bugs, findings, and decisions (esp. `feedback_resolution_no_empty_fallback`)
3. Reviewed the `AgentRoleSelection` entity + `TammaModelConfiguration` config (32-2) — this story mirrors its XOR/index discipline **exactly**
4. Reviewed `ControlPlaneDbContextModelTests` (the strict `BeEquivalentTo` list) and the `Program.cs` "Wiping Tamma-managed public-schema tables" block — **both must be amended** (AC8, AC9)
5. Confirmed the 32-1/32-2/32-15 contracts (Agent visibility, `/api/agents` group, `AgentManage` policy, `DefaultPersonaName`) are landed before wiring
6. Planned TDD approach (Red-Green-Refactor cycle)

### Key design decisions

- **Enablement = catalog membership; selection = role binding.** Two separate entities, two separate concerns. `TenantAgentEnablement` says "this persona is part of my tenant's set"; `AgentRoleSelection` (32-2) says "this (already-enabled) agent serves this role." Enablement gates selection — never the reverse.
- **Per-tenant, NOT per-user.** Members see and use the tenant's enabled set; they cannot enable/disable. This matches CLAUDE.md's "no per-user override layer in SaaS" exactly. Single-user mode keys by `UserId` because the sole user *is* the tenant-equivalent.
- **Own private/custom agents are implicitly enabled.** You authored a private agent; it is in your catalog by construction. No enablement row is required; `IsEnabledForPrincipal` short-circuits to `true`. Disabling one via this API is a no-op/409 — you remove it by archiving (32-2), not by toggling membership.
- **Default-deny for public personas, with a seeded default.** Catalog membership is opt-in: an un-enabled public persona is not in the set. To keep a fresh tenant usable, the seeder enables the platform `DefaultPersonaName` (insert-missing-only — never reverts an explicit disable). The resolve-time fail-loud (no empty fallback) lives in 32-18.
- **CP-resident in both modes.** Unlike `AgentRoleSelection` (tenant-schema in SaaS, CP for single-user), enablement is CP-resident in *both* modes because it gates the CP-resident public catalog and is keyed by tenant id, not stored per `t_<hex>`. Hence the DROP-list + CP-model-test amendments.
- **Own the primitive, not the gate.** This story ships the `ITenantAgentEnablementReader` read seam (`IsEnabledForPrincipalAsync` / `ListEnabledPublicAgentIdsAsync` / `GetEnabledDefaultPersonaIdAsync`); 32-18 injects that reader and wires it into `CanUse`/resolution/the enabled-default lookup. The boundary is the read interface — keeps the two stories from both editing the resolver.

### Per-mode ownership (mandatory two-scoping-model answer, per CLAUDE.md)

| Question | single-user mode | SaaS mode |
|---|---|---|
| Who owns the enablement decision? | The sole user (`user_id`-keyed CP row; `tenant_id` NULL). | The tenant (`tenant_id`-keyed CP row; `user_id` NULL). `tenant_owner`/`tenant_admin` write; `member` read-only. |
| Is there a per-user layer? | N/A — the sole user *is* the principal. | **No.** Members see/use the tenant's enabled set; they cannot enable/disable (403 on writes). |
| Where does the enablement row live? | `ControlPlaneDbContext.tenant_agent_enablements`, keyed by `UserId`. | `ControlPlaneDbContext.tenant_agent_enablements`, keyed by `TenantId` (CP-resident, not `t_<hex>`). |
| What does the usable set become? | `enabled(public) ∪ own-private`, keyed by the user. | `enabled(public) ∪ own-private`, keyed by the tenant — identical for every member. |
| Who manages the public catalog itself? | Shipped system personas (read-only to the user; the user enables/disables membership). | Platform owner (`PlatformOwnerAccess`) — out of scope here; this story only toggles per-tenant membership. |
| Where do `AGENT.ENABLED/DISABLED.SUCCESS` land? | The user's (platform-events) feed; `TenantId == null`, principal = `userId`. | The tenant's event store via tenant-scoped `IEventRepository`; `TenantId` set. |
| Mode source | `ITammaModeProvider` (`TammaMode.cs`) — process-stable. | same |

### Risks and Mitigations

| Risk | Severity | Mitigation |
|------|----------|------------|
| New CP table breaks the second test-host boot (`relation already exists`) | High | Amend the `Program.cs` "Wiping Tamma-managed public-schema tables" DROP list (AC8); test asserts a second boot succeeds. |
| `ControlPlaneDbContextModelTests` strict list fails after adding the entity | High | Update the `BeEquivalentTo` list in the same PR (AC9); it is a known gotcha, not a regression. |
| Overlap-implementing the gate with 32-18 | High | Hard boundary: this story ships the **interface + primitive**, 32-18 consumes it. No registry/resolver edits here. Cross-referenced both ways. |
| Default-deny locks a fresh tenant out (no persona usable) | Medium | Seeded default persona enabled out of the box (AC10); resolve-time fail-loud only when a tenant explicitly disables everything (32-18). |
| Disabling an own private agent surprises the user | Medium | Implicit-enabled rule + 409/no-op on disable-own-private; documented; removal is via archive (32-2). |
| XOR/keying drift from `AgentRoleSelection` | Medium | Mirror `TammaModelConfiguration` config byte-for-byte (XOR check name pattern, unique-nulls-not-distinct); constraint tests (AC12). |
| Stale enablement after a persona is retired platform-wide | Low | `IsEnabledForPrincipal` re-validates the target is still a live public agent at read time; a retired persona resolves out (consumer 32-18 degrades to default). |

### Success Metrics

- [ ] A tenant's usable set is `enabled(public) ∪ own-private` — proven by an integration test where a disabled public persona is non-selectable via 32-18.
- [ ] 100% of enable/disable writes emit exactly one `AGENT.ENABLED/DISABLED.SUCCESS` event tagged with the principal.
- [ ] Member write attempts are 403; reads are 200 — RBAC matrix green.
- [ ] Second test-host boot succeeds (DROP-list amendment) and `has-pending-model-changes` → none.

## Related

- Design of record: `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` (§3.3 per-tenant enablement, §3.0 reframe, §3.5 BYOK ∘ persona)
- Re-plan / sequence: `docs/superpowers/plans/2026-06-20-epic-32-37-replan.md`
- Implementation plan: `docs/superpowers/plans/2026-06-21-32-16-per-tenant-agent-enablement-plan.md`
- Sibling stories: `docs/stories/epic-32/story-32-15/` (persona reframe + seeding), `story-32-18/` (registry enablement gate — the consumer), `story-32-17/` (custom-agent prompts), `story-32-2/` (registry/selection — the XOR/index precedent), `story-32-5/` (call-LLM endpoint — runs the enablement-aware resolver)
- Reused precedent: `apps/tamma-elsa/src/Tamma.Data/Entities/AgentRoleSelection.cs`, `prompt_overrides` (Epic 27)

## Logging Requirements

- **INFO**: persona enabled / disabled for the principal (agentId, personaName, mode, tenantId|userId); catalog view requested (count enabled/disabled).
- **DEBUG**: `IsEnabledForPrincipalAsync` branch taken (implicit-private / enabled-public / no-row), `GetEnabledDefaultPersonaIdAsync` outcome (configured-default / single-enabled / none), enablement upsert duration, seeded-default applied (or skipped because a row exists).
- **WARN**: enable/disable target no longer a live public agent ⇒ ignored/degraded (agentId); disable-own-private rejected (409); cross-tenant target ⇒ 404 (caller principal, target id).
- **ERROR**: DCB event append failure (the write still committed; the append failure is logged, not swallowed silently); migration / DB write failure; XOR/unique-constraint violation surfaced from EF.
- **Structured context**: include `{ agentId, personaName, mode, tenantId, userId }` where applicable.
- **Credential safety**: enablement data is credential-agnostic (it references agent ids + persona names, never provider keys). NEVER log provider credentials — keys resolve later in 32-3 from the secret cabinet; enablement never touches them.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-21 | 1.0.0   | Initial story creation | Claude |
| 2026-06-21 | 1.0.1   | Cross-spec reconciliation (C2/C3): split the enablement contract into a read-only `ITenantAgentEnablementReader` (the seam 32-18 injects) + write/admin `ITenantAgentEnablementService : ITenantAgentEnablementReader`; standardized the three read primitives on async signatures with an explicit `Principal` arg (`IsEnabledForPrincipalAsync`, `ListEnabledPublicAgentIdsAsync`); **defined `GetEnabledDefaultPersonaIdAsync(principal) -> Task<Guid?>`** (configured default if enabled, else single unambiguous enabled persona, else null) for 32-18 to consume. | Claude |
