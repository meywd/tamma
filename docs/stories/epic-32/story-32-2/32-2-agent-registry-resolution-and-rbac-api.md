# Story 32-2: Agent Registry, Resolution & RBAC API

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)

## User Story

As a **tenant owner/admin (SaaS) or self-hosted user (single-user)**,
I want to list, create, version, archive, and select first-class agents — and have workflows resolve the *effective* agent for a `(role, phase)` request through a deterministic precedence chain,
So that named, versioned agent entities (introduced in Story 32-1) actually drive workflow execution instead of the anonymous JSONB role config, with strict per-mode RBAC and zero silent-fallback behaviour.

## Priority

P0 — Without resolution + RBAC, the Story 32-1 agent entities are inert. This story is the seam that promotes them into the live `ResolvedAgentConfig` pipeline that `AgentResolverService` and the Elsa `CallLlmActivity` already consume. It is the prerequisite for managed execution (32-5), the action trail (32-6), and panels (32-7).

## Acceptance Criteria

1. **`AgentRegistryService`** (new, `Tamma.Api/Services/Agents/AgentRegistryService.cs`) implements `IAgentRegistryService` exposing: `ListAsync` (scoped to caller: all public agents ∪ own-tenant private agents), `GetWithVersionsAsync(agentId)`, `CreateAsync`, `PublishVersionAsync(agentId, version)`, `ArchiveAsync(agentId)`, `SelectForRoleAsync(role, agentId)`, and `GetRoleSelectionsAsync()`. Public-agent reads come from `ControlPlaneDbContext`; private-agent reads/writes and role selections come from the caller's `TenantDbContext` (`t_<hex>` schema).
2. **`AgentResolverService.ResolveForRoleAsync(role)` / `ResolveForPhaseAsync(phase, role)`** return an enriched `ResolvedAgentConfig` carrying the new fields `AgentId` (Guid), `AgentVersion` (int), and `Source` (one of `system-public` | `tenant-public` | `tenant-private`), in addition to all existing fields. The existing legacy `ResolveAsync(tenantId, role)` JSONB path is preserved as the merge target — the agent's pinned config version is materialised into the same `ResolvedAgentConfig` shape so `CallLlmActivity` is unchanged.
3. **Resolution precedence** for `(tenant, role)` is, in order, with NO empty/plain fallback (per `feedback_resolution_no_empty_fallback`):
   1. Tenant-selected **private** agent for that role (from the tenant's `agent_role_selections`) → use it.
   2. Tenant-selected **public** agent for that role (selection points at a control-plane public agent) → use it.
   3. **System-default public** agent for that role (the shipped default selection) → use it.
   4. None resolvable → emit `AGENT.RESOLVE.FAILED` + record a `MISSING_CONFIG` gap, then throw `TammaError("AGENT.RESOLVE.NO_DEFAULT", ..., severity: High)`. NEVER return a blank `ResolvedAgentConfig`.
4. **Role-to-agent selection is persisted per tenant** in a new `agent_role_selections` table (tenant schema in SaaS; user-keyed row in single-user) — one selection per `(principal, role)`. `ResolveForRoleAsync` respects it; absent a tenant selection, resolution falls to the system-default public agent for the role (AC 3.3), never to empty.
5. **Endpoints** wired in `AgentEndpoints.cs` and mapped in `Program.cs` under `/api/agents`:
   - `GET  /api/agents` — list (public ∪ own private), filters `?role=&visibility=&status=`.
   - `POST /api/agents` — create a **private** agent (SaaS: `AgentManage`; single-user: owner).
   - `GET  /api/agents/{id}` — get one agent with its version history.
   - `POST /api/agents/{id}/versions` — publish a new pinned config version.
   - `POST /api/agents/{id}/archive` — archive (soft, status flip).
   - `PUT  /api/agents/role-selections/{role}` — select which agent serves a role for the principal.
   - `GET  /api/agents/resolve?role=&phase=` — return the enriched `ResolvedAgentConfig`.
6. **Per-mode RBAC**, mirroring the Prompt Store:
   - SaaS `member` → **403** on POST/PUT/POST-versions/POST-archive and role-selection writes; reads allowed.
   - SaaS `tenant_owner`/`tenant_admin` → may create/version/archive/select only **private** agents owned by their own tenant.
   - Public-agent mutation (create/version/archive a `visibility = public` agent) requires `PlatformOwnerAccess`; a tenant attempting it gets **403**.
   - Single-user mode → sole user (auto-owner) may do everything; no member gate.
7. **Cross-tenant isolation**: a tenant cannot read another tenant's private agents (404, not 403, to avoid existence leak) and cannot select or mutate them. A `GET /api/agents` from tenant A never returns tenant B's private rows.
8. **Public-agent writes by a tenant are rejected**: a `POST /api/agents` body with `visibility: "public"` from a non-platform-owner returns **403** with code `agent_public_write_forbidden`; a tenant `POST /api/agents/{id}/versions` against a public agent returns **403**.
9. **Resolution never falls back to empty/plain config**: a missing system-default public agent for a taxonomy-valid role emits `AGENT.RESOLVE.FAILED` and records a `MISSING_CONFIG` gap (ties into the Missing-Config Notifications epic, domain `agent`, config_key `role:{role}`) rather than returning a blank config. The thrown `TammaError` matches the prompt/convention fail-loud pattern.
10. **Single-user mode** resolves with `user_id` as principal and no RBAC gate; **SaaS mode** resolves with `tenant_id` — verified by mode-parameterized tests over `ITammaModeProvider`.
11. **DCB events** (`AGGREGATE.ACTION.STATUS`) emitted via `IEventRepository.AppendAsync`:
    - `AGENT.SELECTED_FOR_ROLE.SUCCESS` on role selection, tags `{ agentId, role, source, mode }`.
    - `AGENT.RESOLVE.FAILED` on unresolvable role, tags `{ role, phase, source, mode }`.
    - `AGENT.CREATED.SUCCESS`, `AGENT.VERSION_PUBLISHED.SUCCESS`, `AGENT.ARCHIVED.SUCCESS` for lifecycle.
12. **Role validation**: `role` is validated against `RolePhaseMap.ValidRoles`; `phase` (when present) against `RolePhaseMap.IsRoleEligibleForPhase`. Unknown role/phase → 400, not a resolution attempt.
13. **Rollback-to-prior-version resolution**: selecting an agent whose pinned/active version was rolled back (a prior version re-activated via `POST /versions` with `activate: prior`) resolves to the *currently active* version's config — proven by an integration test.
14. **Unit + integration tests** cover: resolution precedence (all four AC-3 branches), the 403 paths (member create/version/archive/select; tenant mutating a public agent), cross-tenant private read (404), rollback-to-prior-version resolution, mode-parameterized principal selection, and the no-empty-fallback `AGENT.RESOLVE.FAILED` + `MISSING_CONFIG` path.
15. **No regression**: existing `/api/v1/agents/config`, `/api/v1/agents/{role}/resolve`, `/api/v1/agents/resolve-for-phase` endpoints and the legacy `AgentResolverService.ResolveAsync` JSONB behaviour stay byte-for-byte working; the full `dotnet test` suite stays green; `dotnet ef migrations has-pending-model-changes` reports none after the new migration.

## Technical Design

### Architectural placement (per the Epic 32 design of record)

Per `docs/superpowers/specs/2026-06-17-agent-entities-benchmarking-design.md` §"Ownership, visibility & data scoping":

- **Public (system) agent definitions** are shared → live in the **control plane** (`ControlPlaneDbContext`), referenced by `agent_id`. Managed by platform owner (`PlatformOwnerAccess`).
- **Private (tenant) agent definitions** + **role selections** are tenant-owned → live in the tenant's `t_<hex>` schema (`TenantDbContext`). Managed by `tenant_owner`/`tenant_admin` (new `AgentManage` policy = `agents:manage` = admin+owner, mirroring `PromptManage`).
- A tenant's usable set = **all public agents ∪ its own private agents**.

Story 32-1 is assumed to have created the `Agent` + `AgentVersion` entities and their DbSets in both contexts (control-plane for public, tenant for private). This story (32-2) adds the **selection** entity, the registry/resolver services, the endpoints, the new policy, and the enrichment of `ResolvedAgentConfig`. Where a 32-1 entity field is referenced and not yet confirmed present, it is marked **(NEW — coordinate with 32-1)**.

### Enriched `ResolvedAgentConfig` (modify existing)

```csharp
// Tamma.Api/Services/Agents/ResolvedAgentConfig.cs — ADDITIVE fields only
public class ResolvedAgentConfig
{
    // ... all existing fields unchanged (Role, Handle, Provider, Model,
    //     Temperature, MaxTokens, TokenBudget, Tools, SystemPrompt, Source,
    //     Phase, MaxBudgetUsd, PermissionMode, AllowedTools) ...

    /// <summary>Stable identity of the agent that produced this config (32-1).
    /// Null only on the legacy JSONB path for backward compatibility.</summary>
    public Guid? AgentId { get; init; }

    /// <summary>Pinned config version of the resolved agent.</summary>
    public int? AgentVersion { get; init; }

    // NOTE: `Source` already exists as string ("platform-default" |
    // "tenant-override"). Extend the documented value set to add
    // "system-public" | "tenant-public" | "tenant-private". Legacy values
    // remain valid for the JSONB path.
}
```

### `agent_role_selections` entity (NEW)

```csharp
// Tamma.Data/Entities/AgentRoleSelection.cs
public class AgentRoleSelection
{
    public Guid Id { get; set; }

    /// <summary>SaaS: the tenant schema scopes this implicitly; column kept
    /// for the single-user CP-resident path. Exactly one of TenantId/UserId
    /// is non-null (principal XOR), mirroring prompt_overrides.</summary>
    public Guid? TenantId { get; set; }
    public Guid? UserId { get; set; }

    /// <summary>One of RolePhaseMap.ValidRoles.</summary>
    public string Role { get; set; } = null!;

    /// <summary>The selected agent (public OR own private). FK is logical —
    /// public agents live in the CP, so no DB FK across schemas; the
    /// registry validates the target is in (public ∪ own private).</summary>
    public Guid AgentId { get; set; }

    /// <summary>Resolved provenance at selection time: tenant-private |
    /// tenant-public | system-public. Recomputed on resolve, not trusted.</summary>
    public string Visibility { get; set; } = null!;

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
}
```

EF model config (in `TammaModelConfiguration.cs`, the single source) for the tenant context (SaaS) and the CP context (single-user user-keyed rows):

```csharp
b.ToTable("agent_role_selections");
b.HasKey(x => x.Id);
b.Property(x => x.Role).IsRequired();
b.Property(x => x.AgentId).IsRequired();
// principal XOR (mirrors prompt_overrides)
b.ToTable(t => t.HasCheckConstraint(
    "ck_agent_role_selections_principal_xor",
    "((tenant_id IS NOT NULL AND user_id IS NULL) OR (tenant_id IS NULL AND user_id IS NOT NULL))"));
b.HasIndex(x => new { x.TenantId, x.UserId, x.Role })
    .IsUnique()
    .AreNullsDistinct(false);   // UNIQUE NULLS NOT DISTINCT
```

### `IAgentRegistryService` (NEW)

```csharp
// Tamma.Api/Services/Agents/IAgentRegistryService.cs
public interface IAgentRegistryService
{
    /// <summary>Public ∪ own-tenant private, filtered by role/visibility/status.</summary>
    Task<IReadOnlyList<AgentSummary>> ListAsync(AgentListFilter filter, CancellationToken ct = default);

    Task<AgentWithVersions?> GetWithVersionsAsync(Guid agentId, CancellationToken ct = default);

    /// <summary>Create a PRIVATE agent in the caller's tenant. Public creation
    /// goes through the platform-owner path (CreatePublicAsync) only.</summary>
    Task<Agent> CreateAsync(CreateAgentRequest req, CancellationToken ct = default);

    /// <summary>Publish a new pinned config version. `activate` controls whether
    /// the new version becomes active or a prior version is re-activated
    /// (rollback). Rejects cross-visibility writes (tenant→public ⇒ throw).</summary>
    Task<AgentVersion> PublishVersionAsync(Guid agentId, PublishVersionRequest req, CancellationToken ct = default);

    Task ArchiveAsync(Guid agentId, CancellationToken ct = default);

    /// <summary>Persist which agent serves a role for the current principal.
    /// Validates target ∈ (public ∪ own private). Emits
    /// AGENT.SELECTED_FOR_ROLE.SUCCESS.</summary>
    Task SelectForRoleAsync(string role, Guid agentId, CancellationToken ct = default);

    Task<IReadOnlyDictionary<string, AgentRoleSelection>> GetRoleSelectionsAsync(CancellationToken ct = default);
}
```

Authorization is enforced at the endpoint layer (policies) AND defensively in the service: `CreateAsync`/`PublishVersionAsync` reject `visibility == public` unless the caller is a platform owner; cross-tenant target ids resolve to `null`/404.

### `IAgentResolverService` (extend existing)

```csharp
// Add to IAgentResolverService.cs
/// <summary>
/// Resolve the EFFECTIVE agent for a (principal, role) via the 32-2
/// precedence chain (private selection → public selection → system-default
/// public → AGENT.RESOLVE.FAILED). Returns an enriched ResolvedAgentConfig
/// carrying AgentId/AgentVersion/Source. NEVER returns a blank config.
/// </summary>
Task<ResolvedAgentConfig> ResolveForRoleAsync(string role, CancellationToken ct = default);

/// <summary>Same chain plus phase eligibility validation
/// (RolePhaseMap.IsRoleEligibleForPhase).</summary>
Task<ResolvedAgentConfig> ResolveForRoleAndPhaseAsync(string phase, string role, CancellationToken ct = default);
```

The implementation derives the principal from `ITammaModeProvider` + `ITenantContext`/`ClaimsPrincipal` (SaaS ⇒ `tenant_id`; single-user ⇒ `user_id`), reads the role selection, materialises the pinned `AgentVersion.Config` into a `ResolvedAgentConfig` (reusing the existing merge/validation in `AgentResolverService`), and stamps `AgentId`/`AgentVersion`/`Source`. On no-resolution it calls the fail-loud path (AC 9).

### Resolution precedence (pseudocode)

```csharp
public async Task<ResolvedAgentConfig> ResolveForRoleAsync(string role, CancellationToken ct)
{
    if (!RolePhaseMap.ValidRoles.Contains(role))
        throw new ArgumentException($"Unknown role '{role}'");

    var principal = _principal.Resolve(); // (Mode, TenantId?, UserId?)

    // 1 + 2: tenant/user-selected agent (private OR public target)
    var selection = await _registry.GetRoleSelectionsAsync(ct);
    if (selection.TryGetValue(role, out var sel))
    {
        var agent = await _registry.ResolveSelectedAgentAsync(sel.AgentId, ct); // public ∪ own private
        if (agent is not null)
            return Materialise(agent, role, source: SourceFor(agent)); // tenant-private | tenant-public
    }

    // 3: system-default public agent for the role
    var systemDefault = await _registry.GetSystemDefaultPublicAsync(role, ct);
    if (systemDefault is not null)
        return Materialise(systemDefault, role, source: "system-public");

    // 4: NO empty fallback — fail loud
    await _events.AppendAsync(new DomainEvent {
        Type = "AGENT.RESOLVE.FAILED",
        Tags = Json(new { role, phase = (string?)null, source = "none", mode = principal.Mode }),
        // tenant-scoped in SaaS; CP/platform feed in single-user
    });
    await _missingConfig.RecordAsync(new MissingConfigGap(
        domain: "agent", configKey: $"role:{role}", scope: "system", ...), ct); // best-effort
    throw new TammaError("AGENT.RESOLVE.NO_DEFAULT",
        $"No agent resolvable for role '{role}'", severity: High,
        context: new { role });
}
```

> `IMissingConfigRecorder` is from the Missing-Config Notifications epic (`Tamma.Api/Services/MissingConfig/`). It is **(NEW — soft dependency)**: inject as optional (`IMissingConfigRecorder?`) so resolution works before that epic lands; the `AGENT.RESOLVE.FAILED` event + throw are mandatory regardless. If the recorder is absent, skip the gap record (the event still fires).

### `AgentEndpoints.cs` (extend existing static class)

```csharp
public static async Task<IResult> List(IAgentRegistryService registry, ClaimsPrincipal user,
    string? role, string? visibility, string? status) { ... } // 200 list

public static async Task<IResult> Create(CreateAgentRequest req, IAgentRegistryService registry,
    ITammaModeProvider mode, ClaimsPrincipal user)
{
    if (string.Equals(req.Visibility, "public", StringComparison.OrdinalIgnoreCase)
        && !user.IsPlatformOwner())
        return Results.Json(new { error = "agent_public_write_forbidden" }, statusCode: 403);
    var agent = await registry.CreateAsync(req);
    return Results.Created($"/api/agents/{agent.Id}", AgentResponse.From(agent));
}

public static async Task<IResult> PublishVersion(Guid id, PublishVersionRequest req, ...) { ... }
public static async Task<IResult> Archive(Guid id, ...) { ... }
public static async Task<IResult> SelectForRole(string role, SelectRoleRequest req, ...) { ... }
public static async Task<IResult> Resolve(string role, string? phase,
    IAgentResolverService resolver) // 200 ResolvedAgentConfig | 400 bad role | maps TammaError → 404/409
{ ... }
```

### Program.cs wiring

New policy mirroring `PromptManage`:

```csharp
options.AddPolicy("AgentManage", p =>
{
    p.AddAuthenticationSchemes(JwtBearerDefaults.AuthenticationScheme, "ApiKey");
    p.AddRequirements(new PermissionRequirement("agents:manage")); // admin+owner
});
```

Add `["agents:manage"] = ["admin", "owner"]` to `Permissions.Matrix` (mirrors `prompts:manage`). Register `IAgentRegistryService` (Scoped). Map the new routes:

```csharp
var agentsV2 = app.MapGroup("/api/agents")
    .RequireAuthorization("MemberAccess")        // reads allowed for any member
    .RequireRateLimiting("ConfigRead");
agentsV2.MapGet("/", AgentEndpoints.List);
agentsV2.MapGet("/{id:guid}", AgentEndpoints.GetOne);
agentsV2.MapGet("/resolve", AgentEndpoints.Resolve);
agentsV2.MapPost("/", AgentEndpoints.Create)
    .RequireAuthorization("AgentManage").RequireRateLimiting("ConfigWrite");
agentsV2.MapPost("/{id:guid}/versions", AgentEndpoints.PublishVersion)
    .RequireAuthorization("AgentManage").RequireRateLimiting("ConfigWrite");
agentsV2.MapPost("/{id:guid}/archive", AgentEndpoints.Archive)
    .RequireAuthorization("AgentManage").RequireRateLimiting("ConfigWrite");
agentsV2.MapPut("/role-selections/{role}", AgentEndpoints.SelectForRole)
    .RequireAuthorization("AgentManage").RequireRateLimiting("ConfigWrite");
```

> Public-agent mutation is gated *inside* the handler via `IsPlatformOwner()` (a tenant with `AgentManage` reaches the handler but is rejected 403 when `visibility == public`) — the same belt-and-suspenders pattern the convention store uses (system-default routes use `PlatformOwnerAccess`; tenant routes use `ConventionManage`). For clarity, dedicated public-agent admin routes MAY be added under `/api/admin/agents` with `PlatformOwnerAccess`; the in-handler check is the authoritative gate.

### EF migrations

Two additive migrations (collapsed-baseline discipline — additive table, not a CHECK edit on the baseline):

```bash
# Tenant context — agent_role_selections (SaaS path) + any private-agent FK indexes
dotnet ef migrations add AddAgentRoleSelections \
  --context TenantDbContext --output-dir Migrations/Tenant

# Control-plane context — user-keyed agent_role_selections (single-user path)
dotnet ef migrations add AddAgentRoleSelectionsCp \
  --context ControlPlaneDbContext --output-dir Migrations/ControlPlane

# Verify clean
dotnet ef migrations has-pending-model-changes --context TenantDbContext   # → none
dotnet ef migrations has-pending-model-changes --context ControlPlaneDbContext # → none
```

> If Story 32-1 already added the `Agent`/`AgentVersion` tables to both contexts, this story only adds `agent_role_selections`. Coordinate the migration ordering with 32-1 so the baselines don't collide.

### DCB events

| Event | Tags | When |
|---|---|---|
| `AGENT.SELECTED_FOR_ROLE.SUCCESS` | `{ agentId, role, source, mode }` | role selection upsert |
| `AGENT.CREATED.SUCCESS` | `{ agentId, visibility, mode }` | private/public agent created |
| `AGENT.VERSION_PUBLISHED.SUCCESS` | `{ agentId, version, activated, mode }` | new pinned version |
| `AGENT.ARCHIVED.SUCCESS` | `{ agentId, mode }` | archive |
| `AGENT.RESOLVE.FAILED` | `{ role, phase, source, mode }` | no agent resolvable |

All appended via `IEventRepository.AppendAsync`. Tenant-scope events carry the ambient `TenantId` (tenant store); single-user/platform-scope events resolve through the platform-events path (`TenantId == null`).

### Per-mode / per-tenant ownership (mandatory two-scoping-model answer)

| Question | single-user | SaaS |
|---|---|---|
| Who owns a **private** agent? | The sole user (`user_id`-keyed; `tenant_id` NULL). | The tenant (`tenant_id`; lives in `t_<hex>`). `tenant_owner`/`tenant_admin` edit; `member` read-only. |
| Who owns a **public** agent? | Shipped system agents (read-only to the user; the user creates their own private ones). | Platform owner (`PlatformOwnerAccess`); control-plane resident; every tenant may *use* but not edit. |
| Who selects which agent serves a role? | The user. | `tenant_owner`/`tenant_admin`; `member` → 403. |
| Resolution principal | `user_id` (CP-resident selection row). | `tenant_id` (tenant-schema selection row). |
| Mode source | `ITammaModeProvider` (process-stable). | same |

## Dependencies

- **Prerequisite**: Story 32-1 (Agent entity model & versioned saved config) — establishes `Agent` + `AgentVersion` entities and DbSets in the control-plane (public) and tenant (private) contexts. 32-2 layers registry/resolution/RBAC over them.
- **Soft dependency**: Missing-Config Notifications epic (`IMissingConfigRecorder`) — injected optionally; the `MISSING_CONFIG` gap record on `AGENT.RESOLVE.FAILED` degrades gracefully if absent.
- **Reuses**: `AgentResolverService` (legacy JSONB merge), `RolePhaseMap` (role/phase taxonomy), `IEventRepository`, `ITenantContext`, `ITammaModeProvider`, `ClaimsPrincipalExtensions`, the `PromptManage`/`ConventionManage` RBAC precedent, `PlatformOwnerAccess` policy.
- **Blocks**: Story 32-5 (managed execution resolves an `IManagedAgent` from this resolver), Story 32-6 (action trail tags `agent_id` from the resolved config), Story 32-7 (panels resolve N agents per role).
- **Related**: Epic 27 (Prompt Store — the RBAC + per-mode model this mirrors), Epic 28 (schema-per-tenant — structural isolation of private agents/selections).

## Testing Strategy

Tests are xUnit under `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/`. Docker-bound suites run via `sg docker -c "dotnet test ..."` (see `reference_dotnet_test_docker`). TDD: write the failing test first.

1. **Resolution precedence** (`AgentResolverServiceTests`): for each AC-3 branch — (a) tenant-selected private wins over everything; (b) tenant-selected public wins over system default; (c) no selection ⇒ system-default public; (d) no system default ⇒ `AGENT.RESOLVE.FAILED` + `TammaError("AGENT.RESOLVE.NO_DEFAULT")` and **no** blank config returned.
2. **Mode-parameterized principal** (`[Theory]` over `TammaMode.SingleUser`/`SaaS`): same precedence, principal sourced from `user_id` vs `tenant_id`; selection rows read from the correct context.
3. **RBAC matrix** (`AgentEndpointsTests`, in-process `WebApplicationFactory`): member create/version/archive/select → 403; tenant_owner/tenant_admin private create/version/archive → 200; tenant `POST /api/agents {visibility:public}` → 403 `agent_public_write_forbidden`; tenant `POST /{publicId}/versions` → 403; platform owner public create → 201.
4. **Cross-tenant isolation** (`AgentRegistryIsolationTests`, real per-tenant `TenantDbContext`): tenant A `GET /api/agents/{B-private-id}` → 404; `GET /api/agents` from A never returns B's private rows; A cannot `PUT role-selections/{role}` targeting B's private agent (404).
5. **Rollback-to-prior-version** (`AgentResolverServiceTests`): publish v2, then re-activate v1 via `POST /versions {activate:"prior"}`; resolve returns v1's config and `AgentVersion == 1`.
6. **DCB events** (`AgentEventsTests`): selection appends exactly one `AGENT.SELECTED_FOR_ROLE.SUCCESS` with `{agentId, role, source}`; unresolvable role appends exactly one `AGENT.RESOLVE.FAILED`; lifecycle ops append create/version/archive events.
7. **No regression**: existing `AgentEndpointsTests` for `/api/v1/agents/config|resolve|resolve-for-phase` and `AgentResolverService.ResolveAsync` JSONB tests stay green; `has-pending-model-changes` → none.
8. **Tenant-isolation invariant**: a dedicated test seeds two tenants with the *same* public agent selected for a role and asserts each resolves independently with its own selection rows — no bleed.

## Estimated Effort

4-5 days

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/IAgentRegistryService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentRegistryService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentResolverService.cs` | Modify (add `ResolveForRoleAsync` / `ResolveForRoleAndPhaseAsync` precedence chain) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/IAgentResolverService.cs` | Modify (declare new resolve methods) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/ResolvedAgentConfig.cs` | Modify (add `AgentId`, `AgentVersion`; extend `Source` value set) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentEventTypes.cs` | Create (DCB event-type constants) |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/AgentEndpoints.cs` | Modify (add List/GetOne/Create/PublishVersion/Archive/SelectForRole/Resolve handlers) |
| `apps/tamma-elsa/src/Tamma.Api/Dtos/Agents/` (AgentResponse, CreateAgentRequest, PublishVersionRequest, SelectRoleRequest, AgentListFilter, AgentSummary, AgentWithVersions) | Create |
| `apps/tamma-elsa/src/Tamma.Api/Auth/Permissions.cs` | Modify (add `agents:manage`) |
| `apps/tamma-elsa/src/Tamma.Api/Auth/ClaimsPrincipalExtensions.cs` | Modify (add `IsPlatformOwner()` helper if not present) |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (`AgentManage` policy, DI registration, `/api/agents` route group) |
| `apps/tamma-elsa/src/Tamma.Data/Entities/AgentRoleSelection.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` | Modify (entity config: XOR check, unique-nulls-not-distinct index) |
| `apps/tamma-elsa/src/Tamma.Data/TenantDbContext.cs` | Modify (DbSet) |
| `apps/tamma-elsa/src/Tamma.Data/ControlPlaneDbContext.cs` | Modify (DbSet, single-user path) |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/Tenant/*_AddAgentRoleSelections.cs` | Create (generated) |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/*_AddAgentRoleSelectionsCp.cs` | Create (generated) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/AgentRegistryServiceTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/AgentResolverServiceTests.cs` | Create/Modify |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/AgentEndpointsTests.cs` | Create/Modify |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/AgentRegistryIsolationTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/AgentEventsTests.cs` | Create |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
2. Searched `.dev/` directory for related spikes, bugs, findings, and decisions (esp. `feedback_resolution_no_empty_fallback`, `story-28-1-design-calls.md`)
3. Confirmed which `Agent`/`AgentVersion` fields Story 32-1 actually shipped — every **(NEW — coordinate with 32-1)** marker must be reconciled before coding
4. Reviewed the Prompt Store RBAC precedent (`PromptEndpoints.cs`, `PromptManage` policy) — this story mirrors it
5. Planned TDD approach (Red-Green-Refactor cycle)

### Key design decisions

- **Public in CP, private in tenant schema** — isolation is structural, not a WHERE clause. Do not store private agents in the control plane "for convenience"; that breaks the Epic 28 tenancy guarantee and the design of record.
- **404 over 403 for cross-tenant private reads** — returning 403 would leak the existence of another tenant's agent. The registry scopes reads to (public CP rows ∪ ambient-tenant private rows); anything else is simply not found.
- **No empty/plain fallback, ever** — this is the load-bearing project rule (`feedback_resolution_no_empty_fallback`). The fourth precedence branch is a hard `TammaError`, mirroring `PromptStoreService.NoPromptError` and `ConventionStore.NoConventionError`. The `AGENT.RESOLVE.FAILED` event fires even if the missing-config recorder is absent.
- **Selection points at an id, provenance is recomputed** — the stored `Visibility` on a selection is a hint; `ResolveForRoleAsync` recomputes whether the target is still in (public ∪ own private) at resolve time so a deleted/archived target degrades to the system default rather than resolving stale.
- **Legacy JSONB path preserved** — `AgentResolverService.ResolveAsync(tenantId, role)` and the `/api/v1/agents/*` routes are untouched; the new `/api/agents/*` group is the entity-aware surface. `CallLlmActivity` consumes the same `ResolvedAgentConfig` shape, so enrichment is additive.
- **In-handler public-write gate** — mirrors the convention store: tenant-reach policy (`AgentManage`) on the route, platform-owner check inside for `visibility == public`. Keeps one route group instead of duplicating per visibility.

### Open coordination items with Story 32-1

- Exact `Agent` entity shape (does it carry `Visibility`, `Status`, `Name`, `Role`, default-selection flag?) and whether system-default-per-role public agents are marked via a column or a CP `agent_default_selections` table. If 32-1 ships a system-default marker, AC 3.3 reads it directly; if not, 32-2 adds an `agent_default_selections` CP table.
- Whether `AgentVersion` carries an `IsActive` flag (needed for rollback AC 13) or active-version is a pointer on `Agent`.

## Logging Requirements

- **INFO**: agent created / version published / archived (agentId, visibility, version), role selection upserted (role, agentId, source), resolution succeeded (role, phase, agentId, version, source).
- **DEBUG**: resolution precedence branch taken (which of the 4), selection lookup hit/miss, materialise duration.
- **WARN**: selection target no longer in (public ∪ own private) ⇒ degrading to system default (role, staleAgentId); cross-tenant access attempt (caller tenant, target id) → 404.
- **ERROR**: `AGENT.RESOLVE.FAILED` — no agent resolvable for a taxonomy-valid role (role, phase, mode); migration / DB write failure.
- **Structured context**: include `{ agentId, role, phase, source, mode, tenantId }` where applicable.
- **Credential safety**: agent configs are credential-agnostic (provider+model+settings, never keys); never log resolved provider credentials — those resolve later in 32-3 from the secret cabinet.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
