# Story 32-12: Agent Personas & Persona-Aware Benchmarking

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

This mandatory guide includes the 7-Phase Development Workflow (Read → Research → Break Down → TDD → Quality Gates → Failure Handling), Knowledge Base usage (`.dev/` directory), TRACE/DEBUG logging requirements, Test-Driven Development, 100% critical-path coverage, and build-success enforcement.

**Failure to follow this process will result in rework.**

## User Story

As a **tenant owner/admin (SaaS) or self-hosted user (single-user)**,
I want **named, reusable personas — styling/behavior variants (tone, verbosity, risk-tolerance, review-strictness) that compose onto an existing agent without forking its provider config — and the ability to benchmark personas like-vs-like within a role**,
So that **a single agent definition (e.g. the public `tamma-reviewer`) can be run under two personas (`atlas` and `nova`) on my own work, and my per-tenant leaderboards tell me which *style* performs best for my context — finding the right voice for the job without cloning a dozen near-identical agents**.

## Priority

P2 — A refinement layer on top of the first-class agent stack. Agents (32-1), resolution (32-2), and the action trail (32-6) must exist first; personas add a second benchmarking *dimension* (style) orthogonal to the agent/provider/prompt/version dimensions 32-10 already slices. Valuable, not foundational: workflows run fine without personas; personas make style a measurable, tunable knob.

## Acceptance Criteria

1. **Persona entity.** A new EF Core entity `Persona` exists in `apps/tamma-elsa/src/Tamma.Data/Entities/Persona.cs` with: `Id` (Guid PK), `Name` (stable handle, e.g. `atlas`), `Role` (the `AgentRole` wire string the persona is scoped to — a persona is a *named variant within a role*, mirroring the design-of-record's "personas are named variants within a role"), `Visibility` (reuses the `AgentVisibility` enum from 32-1: `public|private`), `OwnerTenantId` (Guid?), `OwnerUserId` (Guid?), `Status` (reuses `AgentStatus`: `active|archived`), `StyleJson` (jsonb — the style/behavior trait bag: `tone`, `verbosity`, `riskTolerance`, `reviewStrictness`, …), `SystemPromptFragmentRef` (string? — a Prompt-Store fragment key, never inline secret content), `CreatedAt`/`CreatedBy`, `UpdatedAt`/`UpdatedBy`. It is **control-plane-resident** for public personas and tenant-owned for private personas, exactly mirroring the `Agent` ownership model from 32-1.

2. **Ownership invariants mirror Agent (32-1).** A `CHECK` constraint `ck_personas_visibility_ownership` enforces: `Visibility='public' ⇒ OwnerTenantId IS NULL AND OwnerUserId IS NULL`; `Visibility='private'` in SaaS ⇒ `OwnerTenantId IS NOT NULL AND OwnerUserId IS NULL`; `Visibility='private'` in single-user ⇒ `OwnerUserId IS NOT NULL AND OwnerTenantId IS NULL` — the same exactly-one-principal XOR pattern as `ck_agents_visibility_ownership` / `ck_prompt_overrides_principal_xor`. A unique partial index `IX_personas_public_name_role` on `(Name, Role) WHERE Visibility='public'` and per-owner private indexes (`(OwnerTenantId, Name, Role)` and `(OwnerUserId, Name, Role)` filtered on `Visibility='private'`) let two tenants each own a private persona named `atlas` for the same role without collision. Configured **only** in `TammaModelConfiguration.cs` (the single source of model config), with one additive migration under `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/`.

3. **Multiple personas per role with the *same* agent.** Two distinct active personas (e.g. `atlas` and `nova`) may both target `role=reviewer` and both compose onto the same resolved reviewer agent, each producing a different `ResolvedAgentConfig` (different style params + prompt fragment) **without forking the underlying agent definition or provider config**. A test seeds one reviewer agent + two reviewer personas and asserts both resolve to the same `AgentId`/`AgentVersion` but distinct effective system prompts / style.

4. **Stable persona name is the benchmark join key.** `Persona.Id` is the immutable identity and `Persona.Name` (within a role) is the stable, human-readable handle used as the join key for all per-persona metrics — exactly as `Agent.Id`/`Name` is for agents. Editing a persona's `StyleJson` or `SystemPromptFragmentRef` does **not** create a new persona identity, so a persona's benchmark history survives style edits (consistent with 32-1's "stable identity, dynamic config" premise). Persona edits are captured as an audit event (AC 8), not a new entity.

5. **Persona composes at resolution time (no agent mutation).** `IAgentResolverService` gains an optional `personaId` parameter — `ResolveForRoleAsync(string role, Guid? personaId = null, …)` (and the phase variant). When a `personaId` is supplied, the resolver: (a) loads the persona and validates it is visible to the principal (public ∪ own private) AND its `Role` matches the requested role; (b) merges the persona's `StyleJson` (temperature/verbosity/etc.) and prompt fragment **onto** the already-resolved `ResolvedAgentConfig` returning a **new** object (never mutating the agent's pinned version, per CLAUDE.md state-immutability rule); (c) stamps `PersonaId`/`PersonaName` onto the returned config. A persona that does not match the role, or is not visible, is rejected (400/404) — it is **never** silently dropped.

6. **Prompt composition reuses Epic 27 layering and never falls back to empty/plain.** The persona's `SystemPromptFragmentRef` is resolved through the existing Prompt Store (`PromptStoreService.ResolveRoleActionAsync` for single-user / `ResolveRoleActionForTenantAsync` for SaaS) and the resolved fragment is **appended** to the agent's system prompt in a deterministic order: `system role identity → role+action template → persona fragment`. The persona fragment layers *on top of* the existing prompt resolution; it never replaces it. If a non-null `SystemPromptFragmentRef` cannot be resolved, the resolver fails loud with a `TammaError` (mirroring `PromptStoreService.NoPromptError` / `feedback_resolution_no_empty_fallback`) — it must **never** fall back to an empty or plain persona fragment.

7. **Persona is recorded on every run + action-trail entry as a tag.** The 32-6 action-trail tag builder is extended so every `AGENT.TASK.*` / `AGENT.ITERATION.COMPLETED` / `AGENT.PANEL.AGGREGATED` / `REVIEW.BUG.RECORDED` event (and the `AgentRunResult` it derives from) carries a `personaId` and `personaName` tag (flat string values, empty/absent when no persona was applied — agents run persona-free by default). This is the substrate that lets 32-10 compute per-persona leaderboards; the tag is populated from the same shared trail-tag builder so every emission site is consistent, with no raw `StyleJson` or prompt content in the tag.

8. **DCB lifecycle + application events.** Events follow `AGGREGATE.ACTION.STATUS` and are appended via `IEventRepository.AppendAsync`: `PERSONA.CREATED.SUCCESS` (on create), `PERSONA.UPDATED.SUCCESS` (on style/fragment edit), `PERSONA.ARCHIVED.SUCCESS` (on archive), and `AGENT.PERSONA_APPLIED.SUCCESS` (each time a persona is composed onto an agent at resolution) with `Tags` `{ personaId, personaName, agentId, agentVersion, role, mode }`. `Metadata` carries `{ workflowVersion: "1.0.0", eventSource: "system" }`. Events fire only after a real state transition / real composition (no "lie" events). For private/SaaS personas `DomainEvent.TenantId` is the `OwnerTenantId` (or the resolving tenant for `AGENT.PERSONA_APPLIED`); for public personas it is NULL (platform feed).

9. **Persona-aware leaderboards compare personas within a role.** The 32-10 benchmark projection gains a **persona slice**: per-tenant leaderboards can group by `(role, personaId)` so the question "which reviewer *persona* has the best success rate / fewest functional bugs / lowest cost on my work?" is answerable, comparing `atlas` vs `nova` **like-vs-like within the reviewer role** (never reviewer-persona vs architect-persona). Since 32-10's story file does not yet exist, this story defines the persona dimension contract (the `personaId`/`personaName` trail tags from AC 7 + a `groupBy=persona` / `?personaId=` query facet) that 32-10 consumes; the projection slice is delivered here against the 32-6 trail, and 32-10 surfaces it in its leaderboard API.

10. **Per-mode public/private ownership (mandatory two-scoping-model answer).** In **single-user** mode "public" personas are the shipped system personas (read-only); the sole user owns/creates private personas (`OwnerUserId` set, `OwnerTenantId` NULL); their persona benchmarks are the user's. In **SaaS** mode public personas are platform-owned (control-plane resident, every tenant may *use* but not edit), and private personas are tenant-owned (`OwnerTenantId` set, in the tenant's `t_<hex>` data plane for benchmark data). A tenant's usable persona set = **all public personas ∪ its own private personas**. Performance/benchmark data per persona is **always tenant-scoped** — two tenants running public persona `atlas` build separate, private profiles; the platform owner who owns `atlas` sees neither.

11. **RBAC mirrors agents.** Persona reads (`GET`) are allowed to any tenant member (SaaS) / the sole user (single-user). Private-persona create/update/archive/select require `tenant_owner`/`tenant_admin` (the existing `AgentManage` = `agents:manage` policy, or a sibling `personas:manage`); a SaaS `member` gets **403**. Public-persona mutation requires `PlatformOwnerAccess`; a tenant attempting `visibility:public` create/version gets **403** (`persona_public_write_forbidden`). Cross-tenant private read returns **404** (not 403, to avoid existence leak), mirroring 32-1/32-2.

12. **Persona CRUD endpoints wired + RBAC-gated.** New handlers on `AgentEndpoints.cs` (or a colocated `PersonaEndpoints.cs`) mapped in `Program.cs` under `/api/personas`: `GET /api/personas` (list public ∪ own private; filters `?role=&visibility=&status=`), `POST /api/personas` (create; private ⇒ `AgentManage`/owner, public ⇒ `PlatformOwnerAccess`), `GET /api/personas/{id}` (get one | 404), `PUT /api/personas/{id}` (edit style/fragment), `POST /api/personas/{id}/archive`. The `resolve` surface from 32-2 (`GET /api/agents/resolve?role=&phase=`) accepts an optional `&personaId=` so workflows can request a persona-composed config. The endpoint shape is identical between modes — the auth middleware decides which owner column (`OwnerUserId` vs `OwnerTenantId`) applies based on mode + caller identity (Prompt Store precedent).

13. **No empty/plain fallback, ever.** A requested persona that is unresolvable (unknown id, archived, role-mismatch, cross-tenant, or unresolvable prompt fragment) is a hard `TammaError` / 4xx — the resolver **never** returns the bare agent config "as if no persona were requested" and **never** returns a blank persona fragment (`feedback_resolution_no_empty_fallback`). Requesting *no* persona (`personaId == null`) is the legitimate persona-free path and returns the plain resolved agent config unchanged.

14. **No regression.** Persona-free resolution is byte-for-byte unchanged: `IAgentResolverService.ResolveForRoleAsync(role)` / `ResolveForPhaseAsync(phase, role)` with no `personaId` return exactly what they returned before; the legacy `/api/v1/agents/*` routes and JSONB path are untouched. The new migration applies cleanly, `dotnet ef migrations has-pending-model-changes` reports **none**, and the full `Tamma.Api.Tests` suite stays green.

15. **Tests** cover: persona composition into the resolved config (style merge + fragment append order, agent version unchanged); two personas / one agent / same role producing distinct configs; persona tagging on the action trail; the per-persona benchmark dimension (group-by-persona leaderboard within a role); RBAC/visibility matrix (member 403 on write, tenant public-write 403, cross-tenant 404); the no-empty-fallback failures (unknown persona, role mismatch, unresolvable fragment); per-mode principal derivation; and DCB event emission / no-emission-on-failure.

## Technical Design

### Architectural placement (per the Epic 32 design of record)

Per `docs/superpowers/specs/2026-06-17-agent-entities-benchmarking-design.md`: **the Agent is the entity**; personas are a *named styling/behavior layer above* an agent, scoped to a role, that composes at resolution time. A persona is NOT a new provider config and NOT a fork of an agent — it is a thin, reusable overlay (style params + a prompt fragment) that rides on top of an already-resolved `ResolvedAgentConfig`. Benchmarking is like-vs-like: persona `atlas` vs persona `nova` **within the reviewer role**, on the tenant's own data, never cross-role and never cross-tenant.

Ownership & data scoping reuse the design's key rule verbatim, one layer up:

| Concern | Scope |
|---|---|
| **Persona definition** | Public/system (platform-owned, control-plane, usable by every tenant) **OR** private/tenant-owned (control-plane row keyed to the owner; usable only by it). Shipped defaults are public. |
| **Persona performance/benchmark data** | **ALWAYS tenant-scoped** — the tenant that generated it owns it; never cross-tenant; platform admin who owns a public persona sees none of any tenant's per-persona metrics. |

Story 32-1 (Agent/AgentVersion entities, `AgentVisibility`/`AgentStatus` enums, `IAgentRepository`, the visibility/ownership CHECK + per-owner partial-index pattern) and Story 32-2 (`IAgentResolverService.ResolveForRoleAsync`, the enriched `ResolvedAgentConfig` with `AgentId`/`AgentVersion`/`Source`, the `AgentManage` policy, the `/api/agents` route group) are **prerequisites**; the Agent entity is in-flight, so it is referenced **by interface** here. Every place a 32-1/32-2 field is depended on but not yet confirmed shipped is marked **(coordinate with 32-1/32-2)**.

### C# namespace / file structure

```
apps/tamma-elsa/src/
  Tamma.Data/
    Entities/
      Persona.cs                       # NEW — control-plane entity (style overlay, role-scoped)
                                       #   reuses AgentVisibility / AgentStatus enums (32-1)
    ControlPlaneDbContext.cs           # MODIFY — add DbSet<Persona>
    TammaModelConfiguration.cs         # MODIFY — Persona config: CHECK + partial unique indexes
    Repositories/
      IPersonaRepository.cs            # NEW — Create/Update/Archive/GetById/ListVisible
      PersonaRepository.cs             # NEW
    Migrations/ControlPlane/
      <ts>_AddPersonaEntity.cs         # NEW — additive migration (+ Designer + snapshot)
  Tamma.Api/
    Services/Agents/
      AgentResolverService.cs          # MODIFY — personaId overload: compose style + fragment
      IAgentResolverService.cs         # MODIFY — add optional personaId param
      ResolvedAgentConfig.cs           # MODIFY — add PersonaId/PersonaName (additive)
      PersonaComposer.cs               # NEW — pure merge: persona StyleJson + fragment → config
      PersonaEventTypes.cs             # NEW — PERSONA.* / AGENT.PERSONA_APPLIED.* constants
    Services/PromptStore/
      PromptStoreService.cs            # REUSE — resolve SystemPromptFragmentRef (no change to API)
    Endpoints/
      AgentEndpoints.cs                # MODIFY — persona CRUD handlers (or new PersonaEndpoints.cs)
    Dtos/Agents/
      PersonaDtos.cs                   # NEW — request/response records
    Program.cs                         # MODIFY — map /api/personas, RBAC, DI, &personaId= on resolve
```

### `Persona` entity (sketch)

```csharp
// Tamma.Data/Entities/Persona.cs — control-plane entity; reuses 32-1 enums
public class Persona
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;             // stable handle, e.g. "atlas"
    public string Role { get; set; } = null!;             // AgentRole wire string — persona is role-scoped
    public AgentVisibility Visibility { get; set; }        // reuse 32-1 enum: Public | Private
    public Guid? OwnerTenantId { get; set; }               // set iff Private + SaaS
    public Guid? OwnerUserId { get; set; }                 // set iff Private + SingleUser
    public AgentStatus Status { get; set; } = AgentStatus.Active;  // reuse 32-1 enum
    public string StyleJson { get; set; } = "{}";          // jsonb: tone, verbosity, riskTolerance, reviewStrictness
    public string? SystemPromptFragmentRef { get; set; }   // Prompt-Store fragment key (never inline secrets)
    public DateTime CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
}
```

> The persona carries **no** provider/model/credential fields — those are the agent's. A persona is purely a style overlay + an optional prompt fragment, by design credential-agnostic and provider-agnostic, so the same persona can ride any agent of the matching role.

### EF model configuration (in `TammaModelConfiguration.cs`, mirroring `Agent` from 32-1)

```csharp
modelBuilder.Entity<Persona>(entity =>
{
    entity.ToTable("personas", t =>
    {
        t.HasCheckConstraint(
            "ck_personas_visibility_ownership",
            "(\"Visibility\" = 0 AND \"OwnerTenantId\" IS NULL AND \"OwnerUserId\" IS NULL) " +     // 0 = Public
            "OR (\"Visibility\" = 1 AND \"OwnerTenantId\" IS NOT NULL AND \"OwnerUserId\" IS NULL) " + // 1 = Private/SaaS
            "OR (\"Visibility\" = 1 AND \"OwnerUserId\" IS NOT NULL AND \"OwnerTenantId\" IS NULL)");
    });
    entity.HasKey(e => e.Id);
    entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
    entity.Property(e => e.Name).IsRequired().HasMaxLength(128);
    entity.Property(e => e.Role).IsRequired().HasMaxLength(64);
    entity.Property(e => e.Visibility).HasConversion<int>();
    entity.Property(e => e.Status).HasConversion<int>().HasDefaultValue(AgentStatus.Active);
    entity.Property(e => e.StyleJson).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
    entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
    entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

    // Public persona handles unique on (Name, Role)
    entity.HasIndex(e => new { e.Name, e.Role })
        .IsUnique().HasFilter("\"Visibility\" = 0")
        .HasDatabaseName("IX_personas_public_name_role");
    // Private persona handles unique per owner within a role
    entity.HasIndex(e => new { e.OwnerTenantId, e.Name, e.Role })
        .IsUnique().HasFilter("\"Visibility\" = 1 AND \"OwnerTenantId\" IS NOT NULL")
        .HasDatabaseName("IX_personas_private_tenant_name_role");
    entity.HasIndex(e => new { e.OwnerUserId, e.Name, e.Role })
        .IsUnique().HasFilter("\"Visibility\" = 1 AND \"OwnerUserId\" IS NOT NULL")
        .HasDatabaseName("IX_personas_private_user_name_role");
});
```

### Resolver extension — persona composition

`IAgentResolverService` (extend; `personaId` is optional and defaults to today's behaviour):

```csharp
// Add an optional personaId to the 32-2 resolve methods (additive, non-breaking).
Task<ResolvedAgentConfig> ResolveForRoleAsync(
    string role, Guid? personaId = null, CancellationToken ct = default);

Task<ResolvedAgentConfig> ResolveForRoleAndPhaseAsync(
    string phase, string role, Guid? personaId = null, CancellationToken ct = default);
```

Composition flow (no agent mutation — returns a new object):

```csharp
public async Task<ResolvedAgentConfig> ResolveForRoleAsync(
    string role, Guid? personaId, CancellationToken ct)
{
    // 1. Resolve the agent exactly as 32-2 does (precedence chain; fail-loud on no agent).
    var resolved = await ResolveAgentForRoleAsync(role, ct);
    if (personaId is null) return resolved;          // legitimate persona-free path (unchanged)

    // 2. Load + validate the persona (visible to principal AND role matches).
    var persona = await _personas.GetVisibleAsync(personaId.Value, _principal.Resolve(), ct)
        ?? throw new TammaError("PERSONA.RESOLVE.NOT_FOUND", ..., severity: High);   // 404, not silent
    if (!string.Equals(persona.Role, role, StringComparison.Ordinal))
        throw new TammaError("PERSONA.ROLE_MISMATCH", ..., severity: High);          // 400, not silent

    // 3. Resolve the prompt fragment via Epic 27 store; fail-loud if non-null & unresolvable.
    var fragment = persona.SystemPromptFragmentRef is null
        ? null
        : await ResolveFragmentOrThrow(persona.SystemPromptFragmentRef, role, ct);  // NEVER empty fallback

    // 4. Pure merge: style params + appended fragment → NEW ResolvedAgentConfig.
    var composed = PersonaComposer.Compose(resolved, persona, fragment);
    composed = composed with { PersonaId = persona.Id, PersonaName = persona.Name };

    // 5. Audit the composition (real event, after a real compose).
    await _events.AppendAsync(AgentPersonaApplied(persona, resolved, role), ct);
    return composed;
}
```

`PersonaComposer.Compose` is pure and deterministic:
- **Style merge:** persona `StyleJson` overrides the agent's style-adjacent fields it explicitly sets (e.g. `temperature`, verbosity hints), leaving provider/model/budget/tools untouched (a persona styles, it does not re-provider).
- **Prompt append order:** `system role identity → role+action template → persona fragment` — the persona fragment is *appended* to the already-resolved `SystemPrompt`, never replacing it (AC 6). Order is fixed and tested.
- Returns a brand-new `ResolvedAgentConfig`; the input is never mutated (CLAUDE.md state-immutability rule).

### `ResolvedAgentConfig` (additive)

```csharp
// Tamma.Api/Services/Agents/ResolvedAgentConfig.cs — ADDITIVE fields only
public class ResolvedAgentConfig
{
    // ... all existing fields unchanged (Role, Handle, Provider, Model, Temperature,
    //     MaxTokens, TokenBudget, Tools, SystemPrompt, Source, Phase, MaxBudgetUsd,
    //     PermissionMode, AllowedTools, and the 32-2 AgentId/AgentVersion) ...

    /// <summary>Persona composed onto this config (32-12). Null = persona-free run.</summary>
    public Guid? PersonaId { get; init; }

    /// <summary>Stable persona handle — the benchmark join key (e.g. "atlas").</summary>
    public string? PersonaName { get; init; }
}
```

### Benchmark slice (the persona dimension)

The action trail (32-6) is the substrate. This story:
1. Extends the **shared trail-tag builder** so every trail event + `AgentRunResult` carries `personaId`/`personaName` (flat strings; absent ⇒ persona-free). This is the join key 32-10 groups on.
2. Delivers a **per-persona projection facet** over the 32-6 trail: aggregate success rate, avg iterations-to-done, bug-counts-by-type, cost, and latency grouped by `(role, personaId)` for the calling tenant only. This answers "best reviewer *persona* on my work."
3. Defines the **dimension contract** 32-10 consumes (the leaderboard API gains `?groupBy=persona` / `?personaId=` facets). 32-10's story file does not yet exist; this story owns the persona tags + the projection slice, and 32-10 surfaces them in its leaderboard endpoint. Like-vs-like is enforced by always pairing `personaId` with its `role` — persona comparisons are scoped within a role, never across roles, never across tenants.

### DCB events (NEW)

| Event | When | Tags |
|---|---|---|
| `PERSONA.CREATED.SUCCESS` | new `Persona` committed | `personaId, personaName, role, visibility, ownerTenantId?, ownerUserId?, mode` |
| `PERSONA.UPDATED.SUCCESS` | style/fragment edited | `personaId, personaName, role, visibility, mode` |
| `PERSONA.ARCHIVED.SUCCESS` | `Status` → archived | `personaId, personaName, role, visibility, mode` |
| `AGENT.PERSONA_APPLIED.SUCCESS` | persona composed onto an agent at resolution | `personaId, personaName, agentId, agentVersion, role, mode` |

Appended via `IEventRepository.AppendAsync(DomainEvent { Type, TenantId, Tags, Metadata, Data, CreatedAt })` into the same store the rest of Epic 32 uses (`SequenceNumber` total-order cursor is server-assigned). Private/SaaS persona lifecycle events carry `TenantId = OwnerTenantId`; public-persona lifecycle events carry `TenantId = NULL` (platform feed); `AGENT.PERSONA_APPLIED` carries the *resolving* tenant's id (the run is the tenant's, even for a public persona — consistent with the design's "data is always tenant-scoped").

### API shape

```
GET    /api/personas                  → 200 [PersonaSummary]   (public ∪ own private; ?role=&visibility=&status=)
POST   /api/personas                  → 201 PersonaResponse     (private ⇒ AgentManage/owner; public ⇒ PlatformOwnerAccess; member ⇒ 403)
GET    /api/personas/{id}             → 200 PersonaDetail | 404 (cross-tenant private ⇒ 404)
PUT    /api/personas/{id}             → 200 PersonaResponse      (same ownership rule; emits PERSONA.UPDATED.SUCCESS)
POST   /api/personas/{id}/archive     → 200 { id, status: "archived" }
GET    /api/agents/resolve?role=&phase=&personaId=   → 200 ResolvedAgentConfig (persona composed) | 400 role-mismatch | 404 persona-not-found
```

Per-mode + per-tenant handling reuses the 32-2 endpoint conventions: public-scope writes gated by `PlatformOwnerAccess`; private-scope writes derive principal columns from `ITammaModeProvider` + `ITenantContext`/`ClaimsPrincipal` (SaaS ⇒ `OwnerTenantId`; single-user ⇒ `OwnerUserId`); member-role SaaS callers get 403 on writes; cross-tenant private read ⇒ 404.

### Per-mode ownership (mandatory two-scoping-model answer)

| Question | single-user | SaaS |
|---|---|---|
| Who owns a **private** persona? | The sole user (`OwnerUserId`; `OwnerTenantId` NULL). | The tenant (`OwnerTenantId`); `tenant_owner`/`tenant_admin` edit, `member` read-only. |
| Who owns a **public** persona? | Shipped system personas (read-only to the user). | Platform owner (`PlatformOwnerAccess`); CP-resident; every tenant may *use* but not edit. |
| Who can apply a persona to a run? | The user. | Any member (apply ≈ read of the persona + resolve); editing the persona needs owner/admin. |
| Where do per-persona benchmarks live? | The user's data — `user_id`-keyed. | The tenant's `t_<hex>` data plane; never cross-tenant. |
| Resolution / benchmark principal | `user_id` | `tenant_id` |
| Mode source | `ITammaModeProvider` (process-stable) | same |

### Integration points

- **`Agent` / `AgentVersion` / `AgentVisibility` / `AgentStatus` / `IAgentRepository`** (32-1, in-flight) — persona reuses the enums + the visibility/ownership CHECK + per-owner partial-index pattern; referenced **by interface**.
- **`IAgentResolverService` / `ResolvedAgentConfig`** (`Tamma.Api/Services/Agents/`) — the resolver gains the optional `personaId`; `ResolvedAgentConfig` gains `PersonaId`/`PersonaName` (additive).
- **`AgentEndpoints.cs`** (`apps/tamma-elsa/src/Tamma.Api/Endpoints/AgentEndpoints.cs`) — persona CRUD handlers + `&personaId=` on resolve.
- **`PromptStoreService`** (`apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/PromptStoreService.cs`) — `ResolveRoleActionAsync` / `ResolveRoleActionForTenantAsync` resolve the persona fragment through the existing Epic 27 layering; no Prompt-Store API change.
- **`RolePhaseMap` / `AgentRole`** (`Tamma.Core/Agents/`) — persona `Role` validated on create/resolve.
- **`IEventRepository`** (`Tamma.Data/Repositories/EventRepository.cs`) + **`DomainEvent`** (`Tamma.Data/Entities/DomainEvent.cs`) — DCB emission.
- **`ITammaModeProvider`** (`Tamma.Api/Services/PromptStore/TammaMode.cs`) + **`ITenantContext`** + `ClaimsPrincipal` — per-mode principal derivation.
- **Action trail (32-6)** (`AGENT.TASK.*` etc.) — extended tag builder carries `personaId`/`personaName`.
- **Benchmark leaderboards (32-10)** — consumes the persona dimension contract defined here.
- **Auth policies** (`Program.cs`): `PlatformOwnerAccess`, `AgentManage` (`agents:manage`), member read access — the 32-2 precedent.

## Dependencies

- **Prerequisite**: Story 32-1 (Agent entity model & versioned saved config) — provides `Agent`/`AgentVersion`, the `AgentVisibility`/`AgentStatus` enums the persona reuses, and the visibility/ownership CHECK + per-owner partial-index pattern. **In-flight — reference by interface.**
- **Prerequisite**: Story 32-2 (Agent registry, resolution & RBAC API) — provides `IAgentResolverService.ResolveForRoleAsync`, the enriched `ResolvedAgentConfig` (`AgentId`/`AgentVersion`/`Source`), the `AgentManage` policy, the `/api/agents` route group, and the no-empty-fallback resolution discipline this story extends.
- **Prerequisite / consumer**: Story 32-10 (Benchmark projections & leaderboards) — defines the leaderboard surface; this story supplies the **persona dimension** (trail tags + group-by-persona projection) it slices on. 32-10's story file does not yet exist; coordinate the leaderboard query facet (`?groupBy=persona` / `?personaId=`) at integration.
- **Reuses**: Story 32-6 (action trail — extended with persona tags), Epic 27 (Prompt Store — fragment resolution + the RBAC/per-mode model mirrored), Epic 28 (schema-per-tenant — structural isolation of private personas + per-persona benchmark data).
- **Design of record**: `docs/superpowers/specs/2026-06-17-agent-entities-benchmarking-design.md` (Epic 32 design — "the Agent is the entity; personas are named variants within a role; benchmark like-vs-like").
- **Related project rule**: `feedback_resolution_no_empty_fallback` — persona/fragment resolution is fail-loud, never empty/plain.

## Testing Strategy

Tests are xUnit under `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/` (and `Personas/`). Docker-bound suites run via `sg docker -c "dotnet test ..."` (see `reference_dotnet_test_docker`). TDD: write the failing test first.

1. **Composition** (`PersonaComposerTests` / `AgentResolverServiceTests`): a persona's `StyleJson` overrides only style-adjacent fields (temperature/verbosity), leaving provider/model/budget/tools untouched; the fragment is *appended* to the agent system prompt in the fixed order `role identity → role+action → persona fragment`; the input `ResolvedAgentConfig` and the agent's pinned version are unmodified (immutability); `PersonaId`/`PersonaName` stamped on the output.
2. **Two personas / one agent / same role** (`AgentResolverServiceTests`): seed one reviewer agent + personas `atlas` and `nova` (both `role=reviewer`); resolve under each — both return the same `AgentId`/`AgentVersion`, distinct effective `SystemPrompt`/style. Proves no agent fork.
3. **Persona tagging on the trail** (`PersonaTrailTaggingTests`): a persona-composed run's `AGENT.TASK.*` / `AGENT.ITERATION.COMPLETED` / `REVIEW.BUG.RECORDED` events carry `personaId`/`personaName`; a persona-free run carries empty/absent persona tags; no raw `StyleJson`/prompt content in tags (redaction).
4. **Per-persona benchmark dimension** (`PersonaLeaderboardProjectionTests`): seed trail rows for `atlas` and `nova` under `role=reviewer`; the group-by-`(role, personaId)` projection ranks them within the reviewer role on success rate / bug counts / cost; an architect-role persona never appears in the reviewer leaderboard (like-vs-like); tenant B's rows never appear in tenant A's projection (isolation).
5. **RBAC / visibility matrix** (`PersonaEndpointsTests`, in-process `WebApplicationFactory`): member create/update/archive → 403; tenant `POST /api/personas {visibility:public}` → 403 `persona_public_write_forbidden`; platform owner public create → 201; cross-tenant `GET /api/personas/{B-private-id}` → 404; `GET /api/personas` from A never returns B's private rows.
6. **No empty/plain fallback** (`AgentResolverServiceTests`): unknown `personaId` → `PERSONA.RESOLVE.NOT_FOUND` 404; persona role-mismatch → `PERSONA.ROLE_MISMATCH` 400; non-null `SystemPromptFragmentRef` that the Prompt Store can't resolve → `TammaError` (no blank fragment); `personaId == null` → plain resolved config (legitimate persona-free path).
7. **Per-mode principal derivation** (`[Theory]` over `TammaMode.SingleUser`/`SaaS`): single-user private create sets `OwnerUserId`/null tenant; SaaS sets `OwnerTenantId`/null user; CHECK rejects public-with-owner / private-with-no-owner / private-with-both.
8. **DCB events** (`PersonaEventsTests`): create/update/archive emit exactly one `PERSONA.*` event each with correct tags; a composed resolution emits one `AGENT.PERSONA_APPLIED.SUCCESS` with `{personaId, agentId, role}`; a validation/resolution failure leaves the event store untouched (no-emission-on-failure).
9. **Migration + no regression** (`ControlPlaneDbContextModelTests` extended): migration applies, `has-pending-model-changes` → none, the `personas` table/indexes/CHECK exist; persona-free resolution and the legacy `/api/v1/agents/*` routes stay byte-for-byte green.

**Coverage**: critical paths (composition merge, fragment-fail-loud, ownership guard, event emission, persona tagging) → 100%; entity/repository line ≥ 80%.

## Estimated Effort

4-5 days

## Files Created / Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Data/Entities/Persona.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Repositories/IPersonaRepository.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Repositories/PersonaRepository.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/<ts>_AddPersonaEntity.cs` (+ `.Designer.cs`, snapshot) | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/PersonaComposer.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/PersonaEventTypes.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Dtos/Agents/PersonaDtos.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Personas/PersonaComposerTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Personas/PersonaEndpointsTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Personas/PersonaEventsTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Personas/PersonaLeaderboardProjectionTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Personas/PersonaTrailTaggingTests.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Entities/Persona.cs` style/visibility reuse of `AgentVisibility`/`AgentStatus` (32-1) | Reference (coordinate with 32-1) |
| `apps/tamma-elsa/src/Tamma.Data/ControlPlaneDbContext.cs` | Modify (add `DbSet<Persona>`) |
| `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` | Modify (Persona entity config: CHECK + partial indexes) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentResolverService.cs` | Modify (personaId compose path) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/IAgentResolverService.cs` | Modify (optional `personaId` param) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/ResolvedAgentConfig.cs` | Modify (add `PersonaId`/`PersonaName`) |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/AgentEndpoints.cs` | Modify (persona CRUD handlers + `&personaId=` on resolve) |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (map `/api/personas`, RBAC, DI `IPersonaRepository`) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/AgentResolverServiceTests.cs` | Modify (persona composition + no-fallback cases) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Epic28/ControlPlaneDbContextModelTests.cs` | Modify (assert `personas` table/indexes/CHECK) |

> The 32-6 shared trail-tag builder is extended to carry `personaId`/`personaName`; the exact file lands when 32-6 is implemented — coordinate so the persona tags are added to the single shared builder rather than per emission site.

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)
2. Searched `.dev/` for related spikes, bugs, findings, decisions (esp. `feedback_resolution_no_empty_fallback`)
3. Read the Epic 32 design of record: `docs/superpowers/specs/2026-06-17-agent-entities-benchmarking-design.md`
4. Confirmed which `Agent`/`AgentVersion` fields and which enums (`AgentVisibility`/`AgentStatus`) Story 32-1 actually shipped, and which resolve methods + `ResolvedAgentConfig` fields Story 32-2 shipped — every **(coordinate with 32-1/32-2)** marker must be reconciled before coding
5. Reviewed the Prompt Store RBAC + per-mode precedent (`PromptEndpoints.cs`, `PromptManage`) and the 32-1/32-2 agent ownership/visibility pattern this mirrors
6. Planned the TDD approach (Red-Green-Refactor)

### Key design decisions

- **A persona is an overlay, not a fork.** The whole point is one agent definition, many styles — so the persona carries no provider/model/credential fields and composes at resolution time into a *new* `ResolvedAgentConfig`. Cloning an agent to change its tone would defeat the benchmark ("which style?" becomes unanswerable like-vs-like). This is the design-of-record's "personas are named variants within a role."
- **Stable name is the join key.** Persona benchmark history must survive style edits, exactly as agent history survives config edits (32-1). Edit ⇒ `PERSONA.UPDATED.SUCCESS` audit event, never a new identity.
- **Role-scoped so comparisons are like-vs-like.** A persona is bound to a role; leaderboards group by `(role, personaId)`. Comparing a reviewer persona to an architect persona is meaningless, so the model forbids it structurally.
- **Fail loud, never plain.** A requested-but-unresolvable persona (or fragment) is a hard error — the load-bearing project rule. The only "no persona" path is an explicit `personaId == null`, which returns the plain agent config unchanged.
- **Reuse 32-1 enums + the ownership/visibility pattern wholesale.** Personas mirror agents byte-for-byte on visibility, ownership XOR CHECK, per-owner partial indexes, and 404-not-403 cross-tenant reads. Do not invent a parallel ownership model.
- **Data is always tenant-scoped.** Even running a *public* persona, the per-persona benchmark data belongs to the resolving tenant — the platform owner who authored the public persona sees none of any tenant's per-persona metrics.

### Migration discipline (Epic 28 conventions)

- `personas` is an **additive** table — a normal `dotnet ef migrations add AddPersonaEntity --context ControlPlaneDbContext`, not a baseline CHECK edit.
- After adding, run `dotnet ef migrations has-pending-model-changes --context ControlPlaneDbContext` → must report none.
- Mirror entity config **only** in `TammaModelConfiguration.cs` (the single source); the snapshot/Designer are generated, not hand-edited.
- Run C# tests with `sg docker -c "dotnet test ..."` (session docker group is stale; build needs no wrapper).

### Edge cases

- Persona whose `Role` differs from the requested role → `PERSONA.ROLE_MISMATCH` 400 (never composed onto a mismatched agent).
- Archived persona requested at resolve → treat as not-found (404); a persona archived mid-flight does not retroactively rewrite past trail tags.
- `SystemPromptFragmentRef = null` → no fragment append; style merge still applies (a style-only persona is valid).
- Public persona name collision with an existing handle → 409 (partial unique index), no event.
- Two tenants each own a private persona `atlas`/`reviewer` → allowed (per-owner partial index); benchmarks stay separate.

## Logging Requirements

- **INFO**: persona created / updated / archived (`personaId, personaName, role, visibility`), persona applied at resolution (`personaId, agentId, role, mode`).
- **DEBUG**: composition merge summary (which style fields overridden, fragment appended yes/no — never the fragment body), visibility-scoped persona list resolved (`count, mode, tenantId?`).
- **WARN**: requested persona not visible / role-mismatch surfaced as 404/400 (`personaId, requestedRole, personaRole`), member-role 403 on write, tenant public-write 403.
- **ERROR**: non-null `SystemPromptFragmentRef` unresolvable (fail-loud — `personaId, fragmentRef`), event append failure after a real compose, migration/DB write failure.
- **Structured context**: include `{ personaId, personaName, agentId, agentVersion, role, mode, tenantId }` where applicable.
- **Credential safety**: personas are credential-agnostic and provider-agnostic by design (style + fragment ref only); never log raw `StyleJson` if it could carry sensitive hints and never log resolved fragment bodies.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
