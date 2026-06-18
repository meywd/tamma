# Story 32-1: Agent Entity Model & Versioned Saved Config (public/private)

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

This mandatory guide includes the 7-Phase Development Workflow (Read → Research → Break Down → TDD → Quality Gates → Failure Handling), Knowledge Base usage (`.dev/` directory), TRACE/DEBUG logging requirements, Test-Driven Development, 100% critical-path coverage, and build-success enforcement.

**Failure to follow this process will result in rework.**

## User Story

As a **platform owner (public/system agents) and a tenant owner/admin (private agents)**,
I want **agents to be first-class, identity-bearing entities whose saved configuration is captured as immutable, monotonically-versioned snapshots**,
So that **an agent's history, actions, and performance can be tracked by stable identity across config edits, prior versions can be rolled back, and public (platform-owned) vs private (tenant-owned) ownership is structurally enforced** — replacing the anonymous, role-keyed `agent_configs` JSONB blob.

## Priority

P0 — Foundational. Epic 32's entire action-trail, benchmarking, leaderboard, and learning stack (stories 32-2 … 32-14) joins on `agent_id` + config version. Nothing downstream can be built until the entity + versioning + ownership model exists. This is the canonical owner of the `Agent` / `AgentVersion` control-plane entities.

## Acceptance Criteria

1. Two new EF Core entities exist in `apps/tamma-elsa/src/Tamma.Data/Entities/`: `Agent` (`Id` Guid PK, `Name` string, `Role` string from the `AgentRole` taxonomy wire form, `Visibility` enum `public|private`, `OwnerTenantId` Guid?, `OwnerUserId` Guid?, `Status` enum `active|archived`, `CurrentVersionId` Guid?, `CreatedAt`/`CreatedBy`, `UpdatedAt`/`UpdatedBy`) and `AgentVersion` (`Id` Guid PK, `AgentId` Guid FK, `Version` int, `ConfigJson` jsonb, `Notes` string?, `CreatedAt`/`CreatedBy`, immutable after insert). Both are registered as `DbSet`s on `ControlPlaneDbContext` and configured **only** in `TammaModelConfiguration.cs` (the single source of model config), with a new additive migration under `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/`.

2. The definition rows are **control-plane-resident** (cross-tenant visibility/identity is a CP concern); they are NOT added to `TenantDbContext`. All performance/action data stays in the tenant schema per later Epic 32 stories — no performance columns appear on `Agent`/`AgentVersion`.

3. A `CHECK` constraint `ck_agents_visibility_ownership` enforces exactly-one ownership semantics consistent with the `Visibility` discriminator: `Visibility='public' ⇒ OwnerTenantId IS NULL AND OwnerUserId IS NULL`; `Visibility='private'` in SaaS ⇒ `OwnerTenantId IS NOT NULL AND OwnerUserId IS NULL`; `Visibility='private'` in single-user ⇒ `OwnerUserId IS NOT NULL AND OwnerTenantId IS NULL`. The constraint mirrors the `ck_prompt_overrides_principal_xor` pattern (a private agent never has both owner columns set, and never both null).

4. A unique partial index `IX_agents_public_name_role` on `(Name, Role) WHERE Visibility='public'` guarantees public/system agents have unique `(name, role)` handles; a separate unique partial index scopes private-agent name uniqueness per owner (`(OwnerTenantId, Name)` and `(OwnerUserId, Name)` filtered on `Visibility='private'`) so two tenants may each own a private agent named `atlas` without collision.

5. A unique index `IX_agent_versions_agent_version` on `(AgentId, Version)` guarantees monotonic, non-duplicated versions per agent. `AgentVersion.AgentId` is an FK to `Agent.Id` with `OnDelete(DeleteBehavior.Restrict)` (versions are immutable audit history; archive, never cascade-delete).

6. A new `IAgentRepository` / `AgentRepository` in `apps/tamma-elsa/src/Tamma.Data/Repositories/` exposes `CreateAsync`, `PublishVersionAsync`, `ArchiveAsync`, `GetByIdAsync`, `GetVersionAsync`, `ListVersionsAsync`, and `ListVisibleAsync(principal)`. `PublishVersionAsync` writes a new immutable `AgentVersion` row with `Version = max(existing)+1`, then atomically updates the parent `Agent.CurrentVersionId` and `UpdatedAt`/`UpdatedBy` **in a single transaction**; prior versions remain queryable for rollback.

7. `ConfigJson` is validated against the shape rules reused/extended from `AgentEndpoints.ValidateConfigShape` (provider name regex `^[a-z0-9][a-z0-9_-]{0,63}$`, `maxBudgetUsd` range `[0,100]`, provider-chain non-empty, prototype-pollution rejection, ReDoS guard on `blockedCommandPatterns`, `maxFetchSizeBytes` range), extended for the saved-config fields the Epic 32 design names (`provider`, `model`, `temperature`, `maxTokens`, `tokenBudget`, `tools[]`, `systemPromptRef`, `rag{}`). Validation runs **before any write** in both `CreateAsync` and `PublishVersionAsync`; an invalid config is rejected and no row is written and no event is emitted.

8. DCB events (pattern `AGGREGATE.ACTION.STATUS`) are appended via `IEventRepository.AppendAsync` to the **control-plane** `DomainEvents` store: `AGENT.CREATED.SUCCESS` (on first create), `AGENT.VERSION_PUBLISHED.SUCCESS` (on each new version), `AGENT.ARCHIVED.SUCCESS` (on archive). Each event's `Tags` JSON includes `{ agentId, version, visibility, ownerTenantId?, ownerUserId?, role, mode }`; `Metadata` carries `{ workflowVersion: "1.0.0", eventSource: "system" }`. Events are emitted only after a real state transition (mirroring the existing `AGENT_CONFIG.UPDATED.SUCCESS` discipline — never a "lie" event for a write that did not happen).

9. Single-user vs SaaS ownership is resolved **explicitly** via `ITammaModeProvider`: in `SingleUser` mode a private agent's principal is the sole user (`OwnerUserId` set, `OwnerTenantId` NULL); in `SaaS` mode it is the tenant (`OwnerTenantId` set, `OwnerUserId` NULL). The mapping is documented on the entity and enforced by an entity-level guard in `AgentRepository.CreateAsync` (rejecting a private create whose principal columns contradict the process mode) in addition to the DB `CHECK`.

10. Public/system agents are migrated from the existing seeder into `Agent` + `AgentVersion` rows idempotently. A new `AgentEntitySeeder` (CP-resident, mirroring `ConventionStoreSeeder`'s insert-missing-only pattern) creates one public agent per role with `Visibility='public'`, `OwnerTenantId/OwnerUserId NULL`, preserving the `tamma-<role>` handles (`tamma-architect`, `tamma-tester`, …) currently produced by `Tamma.ElsaServer/AgentSeeder.cs`, each with a `Version=1` `AgentVersion` capturing the shipped provider chain / prompt / temperature / maxTokens. Re-running the seeder inserts nothing new (skip-by-existing-handle).

11. New REST endpoints are added to `AgentEndpoints.cs` and mapped under the `/api/v1/agents` group in `Program.cs` with per-mode RBAC: `POST /api/v1/agents` (create; public ⇒ `PlatformOwnerAccess`, private ⇒ tenant owner/admin), `POST /api/v1/agents/{id}/versions` (publish version; same ownership rule), `POST /api/v1/agents/{id}/archive`, `GET /api/v1/agents` (list visible = all public ∪ caller's own private), `GET /api/v1/agents/{id}` and `GET /api/v1/agents/{id}/versions[/{version}]` (read). A `member` role in SaaS gets read access and a **403** on create/publish/archive (mirrors Prompt Store RBAC). The legacy `GET/PUT /api/v1/agents/config` endpoints remain untouched in this story (cutover is later in the epic).

12. Cross-tenant isolation is provable: a SaaS caller listing visible agents sees every public agent plus only their own tenant's private agents — never another tenant's private agent — and a direct `GET /api/v1/agents/{id}` for another tenant's private agent returns **404** (not 403, to avoid leaking existence).

13. The new migration applies cleanly and `dotnet ef migrations has-pending-model-changes` reports **none** after it is added; the full `Tamma.Api.Tests` suite stays green.

14. Unit tests cover: version increment + monotonicity; rollback pointer integrity (`CurrentVersionId` always points at the highest published version, prior versions still fetchable); ownership `CHECK` rejection (public-with-owner, private-with-no-owner, private-with-both-owners); public vs private visibility queries; per-mode principal derivation; ConfigJson validation rejection; DCB event emission/no-emission-on-failure; seeder idempotency.

15. **Logging**: structured Pino-equivalent (`ILogger<>`) logs at INFO for create/publish/archive (with `{ agentId, version, visibility, role }`), DEBUG for validation pass, WARN for validation rejection and CHECK violations surfaced as 400/409, ERROR for transaction failure — never logging raw `ConfigJson` if it could contain a secret reference (config is credential-agnostic by design, but redact `systemPromptRef` resolution failures).

## Technical Design

### C# namespace / file structure

```
apps/tamma-elsa/src/
  Tamma.Data/
    Entities/
      Agent.cs                         # NEW — control-plane entity (identity)
      AgentVersion.cs                  # NEW — immutable config snapshot
      AgentVisibility.cs               # NEW — enum { Public, Private }
      AgentStatus.cs                   # NEW — enum { Active, Archived }
    ControlPlaneDbContext.cs           # MODIFY — add DbSet<Agent>, DbSet<AgentVersion>
    TammaModelConfiguration.cs         # MODIFY — entity config, CHECK, partial indexes
    Repositories/
      IAgentRepository.cs              # NEW
      AgentRepository.cs               # NEW
    Migrations/ControlPlane/
      <ts>_AddAgentEntities.cs         # NEW — additive migration
  Tamma.Api/
    Endpoints/
      AgentEndpoints.cs                # MODIFY — add Create/PublishVersion/Archive/List/Get
    Dtos/Agents/
      AgentDtos.cs                     # NEW — request/response records
    Services/Agents/
      AgentConfigValidator.cs          # NEW — extract+extend ValidateConfigShape
    Program.cs                         # MODIFY — map new routes with per-mode RBAC
  Tamma.ElsaServer/  (or Tamma.Api seeding host)
    AgentEntitySeeder.cs               # NEW — public-agent seeding into CP rows
```

> Note: the existing `Tamma.ElsaServer/AgentSeeder.cs` seeds the **Elsa Agents** store (`IAgentManager`) with `tamma-<role>` handles. The new `AgentEntitySeeder` populates the **Tamma control-plane** `agents`/`agent_versions` tables and is the source of truth for Epic 32. Both can coexist until the Elsa-side seeder is retired in a later story; `AgentEntitySeeder` reuses the same handles and shipped config values so the two stay aligned.

### Entities (sketch)

```csharp
// Tamma.Data/Entities/AgentVisibility.cs
public enum AgentVisibility { Public, Private }

// Tamma.Data/Entities/AgentStatus.cs
public enum AgentStatus { Active, Archived }

// Tamma.Data/Entities/Agent.cs
public class Agent
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;            // stable handle, e.g. "tamma-architect"
    public string Role { get; set; } = null!;            // AgentRole wire string (NormalizeRole-valid)
    public AgentVisibility Visibility { get; set; }       // Public => system; Private => tenant/user-owned
    public Guid? OwnerTenantId { get; set; }              // set iff Private + SaaS
    public Guid? OwnerUserId { get; set; }                // set iff Private + SingleUser
    public AgentStatus Status { get; set; } = AgentStatus.Active;
    public Guid? CurrentVersionId { get; set; }           // pointer to the active AgentVersion
    public DateTime CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }

    public ICollection<AgentVersion> Versions { get; set; } = new List<AgentVersion>();
}

// Tamma.Data/Entities/AgentVersion.cs  (immutable — never UPDATEd after insert)
public class AgentVersion
{
    public Guid Id { get; set; }
    public Guid AgentId { get; set; }
    public int Version { get; set; }                      // 1-based, monotonic per AgentId
    public string ConfigJson { get; set; } = "{}";        // jsonb; the saved-config snapshot
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }

    public Agent? Agent { get; set; }
}
```

### EF model configuration (in `TammaModelConfiguration.cs`, mirroring PromptOverride/AgentConfig)

```csharp
modelBuilder.Entity<Agent>(entity =>
{
    entity.ToTable("agents", t =>
    {
        // Visibility ⇄ ownership invariant (mirrors ck_prompt_overrides_principal_xor)
        t.HasCheckConstraint(
            "ck_agents_visibility_ownership",
            "(\"Visibility\" = 0 AND \"OwnerTenantId\" IS NULL AND \"OwnerUserId\" IS NULL) " +   // 0 = Public
            "OR (\"Visibility\" = 1 AND \"OwnerTenantId\" IS NOT NULL AND \"OwnerUserId\" IS NULL) " + // 1 = Private/SaaS
            "OR (\"Visibility\" = 1 AND \"OwnerUserId\" IS NOT NULL AND \"OwnerTenantId\" IS NULL)");
    });
    entity.HasKey(e => e.Id);
    entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
    entity.Property(e => e.Name).IsRequired().HasMaxLength(128);
    entity.Property(e => e.Role).IsRequired().HasMaxLength(64);
    entity.Property(e => e.Visibility).HasConversion<int>();
    entity.Property(e => e.Status).HasConversion<int>().HasDefaultValue(AgentStatus.Active);
    entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
    entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");

    // Public handles unique on (Name, Role)
    entity.HasIndex(e => new { e.Name, e.Role })
        .IsUnique().HasFilter("\"Visibility\" = 0")
        .HasDatabaseName("IX_agents_public_name_role");
    // Private handles unique per owner
    entity.HasIndex(e => new { e.OwnerTenantId, e.Name })
        .IsUnique().HasFilter("\"Visibility\" = 1 AND \"OwnerTenantId\" IS NOT NULL")
        .HasDatabaseName("IX_agents_private_tenant_name");
    entity.HasIndex(e => new { e.OwnerUserId, e.Name })
        .IsUnique().HasFilter("\"Visibility\" = 1 AND \"OwnerUserId\" IS NOT NULL")
        .HasDatabaseName("IX_agents_private_user_name");
});

modelBuilder.Entity<AgentVersion>(entity =>
{
    entity.ToTable("agent_versions");
    entity.HasKey(e => e.Id);
    entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
    entity.Property(e => e.ConfigJson).HasColumnType("jsonb").HasDefaultValueSql("'{}'::jsonb");
    entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
    entity.HasIndex(e => new { e.AgentId, e.Version })
        .IsUnique().HasDatabaseName("IX_agent_versions_agent_version");
    entity.HasOne(e => e.Agent)
        .WithMany(a => a.Versions)
        .HasForeignKey(e => e.AgentId)
        .OnDelete(DeleteBehavior.Restrict);  // versions are immutable history
});
```

> `Agent.CurrentVersionId` is left as a plain nullable Guid pointer (no FK navigation back into `agent_versions`) to avoid a circular FK that complicates the create-then-publish-first-version flow. Pointer integrity is enforced in the repository transaction + an integration assertion, not a DB FK.

### Repository (sketch)

```csharp
// Tamma.Data/Repositories/IAgentRepository.cs
public interface IAgentRepository
{
    Task<Agent> CreateAsync(Agent agent, string firstVersionConfigJson, string? notes,
                            Guid? createdBy, CancellationToken ct = default);
    Task<AgentVersion> PublishVersionAsync(Guid agentId, string configJson, string? notes,
                                           Guid? updatedBy, CancellationToken ct = default);
    Task<Agent?> ArchiveAsync(Guid agentId, Guid? updatedBy, CancellationToken ct = default);
    Task<Agent?> GetByIdAsync(Guid agentId, CancellationToken ct = default);
    Task<AgentVersion?> GetVersionAsync(Guid agentId, int version, CancellationToken ct = default);
    Task<IReadOnlyList<AgentVersion>> ListVersionsAsync(Guid agentId, CancellationToken ct = default);
    // Visibility-scoped list: all public ∪ caller's own private.
    Task<IReadOnlyList<Agent>> ListVisibleAsync(Guid? tenantId, Guid? userId, CancellationToken ct = default);
}
```

`PublishVersionAsync` (the load-bearing transaction):

```
BEGIN
  nextVersion = (SELECT COALESCE(MAX(Version),0)+1 FROM agent_versions WHERE AgentId=@id)
  INSERT agent_versions (AgentId=@id, Version=nextVersion, ConfigJson=@cfg, Notes=@n, CreatedBy=@by)
  UPDATE agents SET CurrentVersionId=<new id>, UpdatedAt=now(), UpdatedBy=@by WHERE Id=@id
COMMIT
```

The unique index `(AgentId, Version)` makes a concurrent double-publish fail the second INSERT (caught → retried with a fresh `MAX(Version)+1`), guaranteeing monotonicity under races.

### DCB event names (NEW)

| Event | When | Tags |
|---|---|---|
| `AGENT.CREATED.SUCCESS` | new `Agent` + `Version=1` committed | `agentId, version=1, visibility, ownerTenantId?, ownerUserId?, role, mode` |
| `AGENT.VERSION_PUBLISHED.SUCCESS` | new `AgentVersion` committed + pointer moved | `agentId, version, visibility, ownerTenantId?, ownerUserId?, role, mode` |
| `AGENT.ARCHIVED.SUCCESS` | `Status` → archived | `agentId, visibility, ownerTenantId?, ownerUserId?, role, mode` |

Emitted via the existing `IEventRepository.AppendAsync(DomainEvent { Type, TenantId, Tags, Metadata, Data, CreatedAt })` into the CP `DomainEvents` table — the same store `AlertRuleEvaluator` polls and the same path used by `AGENT_CONFIG.UPDATED.SUCCESS`. For private/SaaS agents `DomainEvent.TenantId` is set to `OwnerTenantId`; for public/system agents it is NULL (platform feed).

### API shape

```
POST   /api/v1/agents
  body: { name, role, visibility: "public"|"private", config: {...}, notes? }
  → 201 { id, name, role, visibility, status, currentVersion: 1 }
  RBAC: public ⇒ PlatformOwnerAccess; private ⇒ tenant owner/admin (SettingsManage-equivalent); member ⇒ 403

POST   /api/v1/agents/{id}/versions
  body: { config: {...}, notes? }
  → 200 { id, version, createdAt }
  RBAC: matches the agent's ownership

POST   /api/v1/agents/{id}/archive   → 200 { id, status: "archived" }

GET    /api/v1/agents                 → 200 [ AgentSummary ]  (all public ∪ own private)
GET    /api/v1/agents/{id}            → 200 AgentDetail | 404
GET    /api/v1/agents/{id}/versions   → 200 [ AgentVersionSummary ]
GET    /api/v1/agents/{id}/versions/{version} → 200 AgentVersionDetail | 404
```

Per-mode + per-tenant handling at the endpoint layer:
- **Public-scope writes** are gated by `PlatformOwnerAccess` (platform admin only) — same gate the spec assigns to public agent CRUD.
- **Private-scope writes**: principal columns are derived from `ITammaModeProvider.Mode` + `ITenantContext`/`ClaimsPrincipal` — SaaS sets `OwnerTenantId` from the active tenant; single-user sets `OwnerUserId` from the sole user. Member-role SaaS callers are rejected 403.
- **Reads** apply `ListVisibleAsync(tenantId, userId)`; a `GET {id}` for a private agent not owned by the caller returns 404.

### Validation

`AgentConfigValidator.Validate(string configJson) → (bool Valid, string[] Errors)` is extracted from the private `AgentEndpoints.ValidateConfigShape` (so the rules are shared, not duplicated) and extended to recognise the Epic 32 saved-config fields (`model` string, `temperature` ∈ `[0,2]`, `maxTokens` > 0, `tokenBudget` ≥ 0, `tools[]` of strings, `systemPromptRef` string, `rag{}` object) while keeping the existing provider-regex / budget-range / ReDoS / prototype-pollution guards. Both `CreateAsync`-backing endpoint and `PublishVersionAsync`-backing endpoint validate before persisting.

### Integration points

- **`AgentRole` taxonomy** (`Tamma.Core/Agents/AgentRole.cs`, `RolePhaseMap.cs`): `Role` is stored as the wire string; `AgentRoleExtensions.Parse` / `RolePhaseMap.NormalizeRole` validate it on create.
- **`IEventRepository`** (`Tamma.Data/Repositories/EventRepository.cs`): DCB emission.
- **`ITammaModeProvider`** (`Tamma.Api/Services/PromptStore/TammaMode.cs`): per-mode principal derivation.
- **`ITenantContext`** (`Tamma.Data/ITenantContext.cs`) + `ClaimsPrincipal.GetUserId()`: caller identity.
- **`ControlPlaneDbContext`** (`Tamma.Data/ControlPlaneDbContext.cs`): the repository resolves against the CP context (definitions are CP-resident), NOT `TenantDbContext`.
- **Auth policies** (`Program.cs`): `PlatformOwnerAccess`, `SettingsManage`/owner-admin gates, `SettingsView` for reads.
- **Downstream (later stories)**: 32-2 (registry/resolution API consumes `IAgentRepository`), 32-5/32-6 (managed execution + action trail join on `agentId` + version), 32-10 (benchmarks slice by version).

## Dependencies

- **Prerequisite**: Epic 27 — `AgentRole`/`AgentAction` taxonomy (`Tamma.Core/Agents/`) for the `Role` attribute and config validation.
- **Prerequisite**: Epic 28 — `ControlPlaneDbContext`, schema-per-tenant model, and the CP migration pipeline (`Migrations/ControlPlane/`).
- **Design of record**: `docs/superpowers/specs/2026-06-17-agent-entities-benchmarking-design.md` (Epic 32 design).
- **Blocks**: 32-2 (registry/resolution & RBAC API), 32-3 (provider credential resolution), 32-5 (managed execution layer), 32-6 (action trail), 32-10 (benchmarks) — all join on the entity + version this story introduces.
- **Related (not modified here)**: `AgentConfig` / `agent_configs` (legacy role-keyed blob) — coexists; its cutover/retirement is a later Epic 32 story.

## Testing Strategy

**Unit tests** (`tests/Tamma.Api.Tests/Agents/` and/or `tests/Tamma.Api.Tests/MissingConfig`-style; in-memory or Postgres fixture per `Infrastructure/InMemoryDbFixture.cs` / `Epic28/ControlPlaneDbContextModelTests.cs` precedent):
1. `CreateAsync` writes one `Agent` + one `Version=1` `AgentVersion`, sets `CurrentVersionId`, emits exactly one `AGENT.CREATED.SUCCESS`.
2. `PublishVersionAsync` increments `Version` monotonically (1→2→3), each writes a new immutable row, moves `CurrentVersionId`, emits one `AGENT.VERSION_PUBLISHED.SUCCESS` per call; prior versions remain fetchable via `GetVersionAsync`.
3. Rollback-pointer integrity: after N publishes, `CurrentVersionId` resolves to the row with `Version=N`; an explicit pointer set back to an older version (rollback path) leaves all versions intact.
4. Ownership `CHECK` rejection: public-with-owner, private-with-no-owner, private-with-both-owner-columns all throw on `SaveChanges` (Postgres `CHECK` violation) — verified against a real Postgres fixture.
5. Per-mode principal derivation: `SingleUser` create sets `OwnerUserId`/null tenant; `SaaS` create sets `OwnerTenantId`/null user; contradictory input rejected by the entity-level guard before DB.
6. `ConfigJson` validation: invalid provider name / budget out of range / empty chain / prototype-pollution key / bad temperature → rejected, no row, no event.
7. DCB no-emission-on-failure: a validation failure or transaction rollback leaves the `DomainEvents` store untouched.
8. Seeder idempotency: first run creates one public agent per role with `tamma-<role>` handles + `Version=1`; second run inserts nothing.

**Integration tests** (Postgres-bound, run via `sg docker -c "dotnet test ..."`):
9. Migration applies + `has-pending-model-changes` reports none; `ControlPlaneDbContextModelTests` extended to assert the new tables/indexes/CHECK.
10. Endpoint RBAC matrix: `PlatformOwnerAccess` required for public create; tenant owner/admin for private; SaaS `member` → 403 on create/publish/archive, 200 on reads.
11. **Tenant-isolation** (mirrors `Epic28/CrossTenantIsolationPostgresTests.cs`): tenant A creates private agent `atlas`; tenant B's `GET /api/v1/agents` returns public agents + B's own only (never A's `atlas`); `GET /api/v1/agents/{A-atlas-id}` as B → 404; two tenants may each own a private `atlas` (partial-index allows it).

**Coverage**: critical paths (version transaction, ownership guard, validation, event emission) → 100%; entity/repository line ≥ 80%.

## Estimated Effort

4-5 days

## Files Created / Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Data/Entities/Agent.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Entities/AgentVersion.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Entities/AgentVisibility.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Entities/AgentStatus.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Repositories/IAgentRepository.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Repositories/AgentRepository.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/<ts>_AddAgentEntities.cs` (+ `.Designer.cs`, snapshot) | Create |
| `apps/tamma-elsa/src/Tamma.Api/Dtos/Agents/AgentDtos.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentConfigValidator.cs` | Create |
| `apps/tamma-elsa/src/Tamma.ElsaServer/AgentEntitySeeder.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/AgentRepositoryTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/AgentEntitySeederTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/AgentEndpointsTests.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/ControlPlaneDbContext.cs` | Modify (add `DbSet<Agent>`, `DbSet<AgentVersion>`) |
| `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` | Modify (entity config, CHECK, partial indexes) |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/AgentEndpoints.cs` | Modify (add Create/PublishVersion/Archive/List/Get; extract validator) |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (map new routes + RBAC; register `IAgentRepository`, `AgentEntitySeeder`) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Epic28/ControlPlaneDbContextModelTests.cs` | Modify (assert new tables/indexes/CHECK) |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:
1. Read [BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)
2. Searched `.dev/` for related spikes, bugs, findings, decisions
3. Read the Epic 32 design of record: `docs/superpowers/specs/2026-06-17-agent-entities-benchmarking-design.md`
4. Reviewed the closest existing patterns: `PromptOverride` (XOR CHECK + NULLS-NOT-DISTINCT unique index in `TammaModelConfiguration.cs`), `AgentConfig` (partial unique index + audit-event discipline in `AgentEndpoints.UpdateConfig`), `ConventionStoreSeeder` (insert-missing-only idempotency)
5. Planned the TDD approach (Red-Green-Refactor)

### Key design decisions

- **Identity is the entity, not the role.** `Agent.Id` is the immutable join key for all later metrics; `Role` is a benchmarking attribute, not a primary key. This is the central premise of the Epic 32 design ("the Agent is the entity").
- **Definitions in CP, data in tenant.** Public agent definitions are shared cross-tenant → control-plane. Private agent definitions are CP too (visibility/identity is a CP concern), but ALL performance/action data lands in the tenant schema in later stories. No performance column belongs on these entities — keep them definition-only.
- **Versions are immutable; archive, never delete.** `AgentVersion` rows are insert-only; `OnDelete(Restrict)` prevents cascade loss of history. Rollback = repoint `CurrentVersionId`, not delete-and-recreate.
- **Mode-aware ownership is explicit, two ways.** The DB `CHECK` is the structural backstop; the repository's entity-level guard fails fast with a clear error before hitting the DB. Per CLAUDE.md "Universal rule for any tenant-aware feature", both single-user (`OwnerUserId`) and SaaS (`OwnerTenantId`) ownership models are answered separately — never assume the SaaS model and bolt single-user on.
- **Reuse the existing validator.** Extract `ValidateConfigShape` rather than re-implement; the provider-regex / budget / ReDoS / prototype-pollution guards are battle-tested (Finding 014) and must apply to saved configs too.
- **Coexist with `agent_configs`.** This story adds the new model and seeds public agents; it does NOT migrate the legacy role-keyed blob off `agent_configs` or repoint workflows. Cutover is a later, separately-reviewable Epic 32 story to keep the blast radius small.

### Migration discipline (Epic 28 conventions)

- `agent_versions` / `agents` are **additive** tables — a normal `dotnet ef migrations add AddAgentEntities --context ControlPlaneDbContext`, not a baseline CHECK edit.
- After adding, run `dotnet ef migrations has-pending-model-changes --context ControlPlaneDbContext` → must report none.
- Mirror entity config **only** in `TammaModelConfiguration.cs` (the established single source); the snapshot/Designer are generated, not hand-edited.
- Run C# tests with `sg docker -c "dotnet test ..."` (session docker group is stale; build needs no wrapper).

### Edge cases

- Concurrent double-publish on one agent: second INSERT hits the `(AgentId, Version)` unique index → catch, recompute `MAX(Version)+1`, retry. Test this race.
- Create with a role string that needs normalization (legacy alias) → `RolePhaseMap.NormalizeRole` first, store canonical wire form.
- Archiving an already-archived agent → idempotent no-op, no second `AGENT.ARCHIVED.SUCCESS`.
- Public agent name collision with an existing handle → 409 (partial unique index), no event.

## Logging Requirements

- **INFO**: agent created (`agentId, name, role, visibility`), version published (`agentId, version`), agent archived (`agentId`), seeder summary (`created, skipped`).
- **DEBUG**: config validation passed (`agentId|name`), visibility-scoped list resolved (`count, mode, tenantId?`).
- **WARN**: config validation rejected (`errors[]` — no raw config body), CHECK/ownership-guard violation surfaced as 400/409, member-role 403 on write, concurrent-publish retry.
- **ERROR**: publish transaction failed/rolled back (`agentId`), event append failure after commit.
- **Structured context**: include `{ agentId, version, visibility, role, mode }` where applicable.
- **Credential safety**: never log raw `ConfigJson` if it could carry a `systemPromptRef` resolving to sensitive content; configs are credential-agnostic by design (no raw keys) but redact on validation-error logs to be safe.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
