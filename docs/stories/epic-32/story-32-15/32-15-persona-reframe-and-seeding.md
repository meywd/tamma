# Story 32-15: Persona Reframe + Seeding (Role-nullable cross-role personas)

Status: done
<!-- Flipped drafted -> done 2026-08-18. The deliverable named in the acceptance criteria
     was located in apps/tamma-elsa/src (and its suites in apps/tamma-elsa/tests) before this
     header was changed — not taken from a changelog. The per-story evidence is recorded
     inline on this story's line in docs/sprint-status.yaml.
-->

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)

This mandatory guide includes the 7-Phase Development Workflow (Read → Research → Break Down → TDD → Quality Gates → Failure Handling), Knowledge Base usage (`.dev/` directory), TRACE/DEBUG logging requirements, Test-Driven Development, 100% critical-path coverage, and build-success enforcement.

**Failure to follow this process will result in rework.**

## User Story

As a **platform owner (who curates the public agent catalogue) and a tenant whose users pick an agent for any role**,
I want **public/system agents to be named, cross-role PERSONAS (`claude`/`gemini`/`codegpt`) that preset a real provider + an explicit model, with prompts coming from the Epic 27 prompt store rather than the persona config**,
So that **one persona serves every role (no more 8 `tamma-<role>` rows on a single provider chain), each persona pins its own provider+model so they no longer all collapse to `claude-sonnet-4`, and the role/system prompt stays the single Epic 27-owned source of truth (personas are prompt-free) — implementing rules 4 and the "public agent → cross-role persona" reframe of the locked agent model.**

## Priority

P0 — Sequence step **B** in the Epic 32 redesign (`docs/superpowers/plans/2026-06-20-epic-32-37-replan.md` §4). This is the amendment that makes public agents *exist as personas at all*; the per-tenant enablement (32-16), custom-agent prompts (32-17), the registry enablement gate (32-18), and the call-LLM endpoint (32-5, lynchpin F) all resolve against the persona shape this story establishes. It directly amends the **shipped** 32-1 entity model (on `main`).

## Context

The shipped 32-1 (`docs/stories/epic-32/story-32-1/32-1-agent-entity-model-and-versioned-saved-config.md`) created the `Agent` / `AgentVersion` control-plane entities and seeded **one public agent per role** as `tamma-<role>` handles (`tamma-architect`, `tamma-tester`, …) on a single provider chain, with `Agent.Role` a **required identity column** and a public unique index on `(Name, Role)`. The seeded `AgentVersion.ConfigJson` **omits `model`** and relies on `DefaultAgentConfig.ForRole(role)` to fill in `claude-sonnet-4` — fine when every public agent is Anthropic, wrong the moment personas differ by provider.

The locked model (`docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` §3.0–§3.1) redefines "public agent":

| Term | 32-1 as built | Locked model (rule 4) |
|---|---|---|
| **Public agent** | per-role `tamma-<role>`, role IS its identity, one provider chain | a **named PERSONA** (`claude`/`gemini`/`codegpt`) presetting provider+model+config, usable across **ALL roles**; role is NOT its identity |
| **Persona** | (the style-overlay idea in old 32-12) | the **system agent itself** — preset provider+model+config; prompts from Epic 27, **no custom prompts** |
| **Selection** | per-`(principal,role)` pick of any visible agent | per-`(principal,role)` pick, constrained to the tenant's enabled set (32-16) |

**The entity model from 32-1 is sound and KEPT.** What changes is (1) `Role` stops being identity for public personas (becomes nullable), (2) the public unique index drops `Role`, (3) the seeder produces named cross-role personas with explicit provider+model, (4) `GetSystemDefaultPublicAsync(role)` stops looking up "the public agent whose `Role==role`" and returns the platform-configured **default persona**, and (5) `AgentResolverService.MaterialiseAsync` pulls the persona's system/role prompt from the **Epic 27 store** keyed `(principal, role, action)` instead of from the persona config (personas are prompt-free).

**Scope boundaries (cross-referenced siblings — NOT owned here):**
- The **per-tenant enablement** gate (`TenantAgentEnablement` + `IsEnabledForPrincipal`) is **32-16**.
- The **custom-agent prompt branch** (`ConfigJson.prompts` for private agents + the resolver's private-prompt path) is **32-17**.
- The **registry-side enablement gate** in `IAgentRegistryService.SelectForRoleAsync`/`ResolveUsableAgentAsync`/`CanUse` is **32-18** (amends 32-2).

This story owns the **entity schema amendment** (Role nullable + index change), the **seeder rewrite** (named cross-role personas, explicit model, `DefaultPersonaName`), the **`GetSystemDefaultPublicAsync` rewrite** (configured default persona, delete the per-role ambiguity warning), and the **persona-prompt-source seam `IPersonaPromptResolver`** (public/persona → Epic 27) plus the wiring of `MaterialiseAsync`'s public branch to that seam. It does NOT add a new table.

## Acceptance Criteria

1. **`Agent.Role` becomes nullable.** The entity property `Role` changes from `string` (required) to `string?` (`Tamma.Data/Entities/Agent.cs`), and the EF config in `TammaModelConfiguration.cs` drops `.IsRequired()` on `Role` (keeps `HasMaxLength(64)`). A new additive migration under `Migrations/ControlPlane/` alters the `agents.Role` column to `NULL`-able. Public personas are seeded with `Role = NULL` (cross-role). Private/custom agents MAY keep a non-null `Role` (their role-binding is unchanged).

2. **The public unique index drops `Role`.** `IX_agents_public_name_role` on `(Name, Role) WHERE Visibility='public'` is replaced by **`IX_agents_public_name` on `(Name) WHERE Visibility='public'`** (persona handles are globally unique among public agents). The two private partial indexes (`IX_agents_private_tenant_name`, `IX_agents_private_user_name`) are **unchanged**. The migration drops the old index and creates the new one. `Role` is removed from the seeder's idempotency/skip-by-existing key (now keyed by `Name` alone for public rows).

3. **`AgentEntitySeeder` is rewritten to seed named cross-role personas.** Instead of 8 `tamma-<role>` rows on one provider chain, it seeds **N named cross-role personas** (`claude`, `gemini`, `codegpt`/`gpt`, optionally an OpenRouter-backed persona), each `Visibility='public'`, `Role=NULL`, `OwnerTenantId/OwnerUserId NULL`, with a `Version=1` `AgentVersion` whose `ConfigJson` carries an **explicit `provider` AND `model`**:
   - `claude` → `{ provider: "anthropic", model: "claude-sonnet-4-20250514", … }`
   - `gemini` → `{ provider: "google", model: "gemini-2.5-pro", … }`
   - `codegpt` (alias `gpt`) → `{ provider: "openai", model: "gpt-4o", … }`
   - (optional) `openrouter-*` → `{ provider: "openrouter", model: "…", … }`
   The model strings MUST be ones `IProviderPricingService.IsKnown(provider, model)` accepts (cost basis must resolve — depends on **34-11** the Provider Cost Price-Book). The seeded `ConfigJson` carries **no prompts** (personas are prompt-free by contract). `ConfigJson` MAY carry a per-role hint block (e.g. `{ roles: { reviewer: { temperature: 0.2 } } }`), but the persona is selectable for any role.

4. **Seeder is insert-missing-only (idempotent), keyed by persona name.** Mirroring `ConventionStoreSeeder`, re-running the seeder inserts nothing for a persona whose `Name` already exists as a public agent (skip-by-existing-handle), and **never reverts an admin edit** to an existing persona's config or version. A first run creates each persona + its `Version=1`; a second run is a no-op (0 created, N skipped). The seeder no longer creates `tamma-<role>` rows; migrating/retiring any previously-seeded `tamma-<role>` rows is handled per AC11.

5. **`GetSystemDefaultPublicAsync(role)` is rewritten.** It no longer searches for "the public agent whose `Role==role`." It returns the platform's configured **default persona** identified by a new config value **`DefaultPersonaName`** (e.g. `Tamma:Agents:DefaultPersonaName = "claude"`), resolved by persona `Name` among `Visibility='public'` agents, **regardless of `role`**. The per-role ">1 public agent for this role" ambiguity warning is **deleted** (it is meaningless once public agents are cross-role). If the configured default persona does not exist, the method **fails loud** (no empty/plain fallback, per `feedback_resolution_no_empty_fallback`).

6. **`AgentResolverService.MaterialiseAsync` keeps its merge + stamp, but the prompt source changes — shipped as an injectable seam.** It still merges the resolved agent's `ConfigJson` onto `DefaultAgentConfig.ForRole(role)` and stamps `AgentId`/`AgentVersion` onto the materialised `ResolvedAgentConfig`. **The system/role prompt for public/persona agents now comes from the Epic 27 prompt store** keyed `(principal, role, action)` — NOT from the persona config. This story ships that as an explicit injectable seam: a new interface **`IPersonaPromptResolver`** with `Task<RenderedPrompt> ResolveAsync(Principal principal, string role, string? action, CancellationToken ct)` that reads the Epic 27 prompt store `(principal, role, action)` and is **fail-loud, never empty/plain** (tenant → system → error). `MaterialiseAsync`'s PUBLIC branch calls this seam (NOT an inline `_promptStore.ResolveAsync`). This is the key wiring change of the story — it ships the SEAM, not an inline resolve.

7. **Persona-prompt-source branch is documented and tested as the public path via the `IPersonaPromptResolver` seam; the custom-agent (private) prompt path is delegated to 32-17.** `MaterialiseAsync` resolves the prompt source by visibility: `Visibility='public'` (persona) → `IPersonaPromptResolver.ResolveAsync(principal, role, action)` (32-15, this story; reads the Epic 27 store). The `Visibility='private'` branch (custom agent's own embedded prompts) is the parallel seam **`ICustomAgentPromptResolver`** owned by **32-17** and wired there; this story leaves a clearly-marked seam/extension point and does not implement the private-prompt branch. Both branches fail loud.

8. **No new table; the changed schema stays in sync with the 32-1 contracts.** `Agent`/`AgentVersion` remain the control-plane tables introduced by 32-1 (already in the `Program.cs` startup-reset DROP list and in `ControlPlaneDbContextModelTests`). This story adds **no** table, so it adds nothing to the DROP list, but the **nullable `Role` column and the renamed index MUST be reflected** in `ControlPlaneDbContextModelTests.Model_Has_ExpectedControlPlaneEntities` / the index assertions so the strict model contract test stays green.

9. **Migration applies cleanly** and `dotnet ef migrations has-pending-model-changes --context ControlPlaneDbContext` reports **none** after it is added. The migration is a single additive amendment on the existing linear snapshot (alter `Role` to nullable + drop `IX_agents_public_name_role` + create `IX_agents_public_name`); it does NOT branch or rewrite the 32-1 baseline. The full `Tamma.Api.Tests` suite stays green.

10. **RBAC is unchanged for persona CRUD.** Public-catalogue writes (creating/editing/seeding personas) remain `PlatformOwnerAccess` (NOT `OwnerAccess`, which admits every personal-tenant owner). No tenant- or member-level write path to public personas is introduced. Reads of public personas remain available to any principal.

11. **Disposition of legacy `tamma-<role>` rows is explicit and idempotent.** Because 32-1 already seeded 8 `tamma-<role>` public rows on `main`, the seeder/migration documents and applies a deterministic disposition: the new persona seeder does NOT create `tamma-<role>` rows; any pre-existing `tamma-<role>` public rows are left in place (archive, never destructive-delete — versions are immutable history) OR archived via `Status='archived'` so the default-persona resolution and enablement (32-16) operate over the named personas. The chosen disposition is insert-missing-only safe and re-runnable. (No-migration-anxiety per CLAUDE.md applies — app is pre-production — but the seeder must still be idempotent.)

12. **DCB events are emitted on real state transitions only.** The persona seeder emits `AGENT.CREATED.SUCCESS` per newly-created persona (tags `{ agentId, version: 1, visibility: "public", role: null, personaName, provider, model, mode }`) and `AGENT.VERSION_PUBLISHED.SUCCESS` only when a version is actually written — never a "lie" event for a skipped (already-existing) persona. Archiving a legacy `tamma-<role>` row (AC11) emits `AGENT.ARCHIVED.SUCCESS`. Events go to the control-plane `DomainEvents` store with `TenantId = NULL` (platform feed, mirroring 32-1).

13. **Unit + integration tests** cover: nullable-`Role` round-trip (insert a `Role=NULL` public persona, read it back); the public unique index now rejects a duplicate `Name` across any/no role and **allows** what `(Name,Role)` formerly required to differ; seeder creates N named personas with explicit provider+model and `Role=NULL`; seeder idempotency (2nd run = 0 created); seeder never reverts an admin-edited persona; `GetSystemDefaultPublicAsync` returns the `DefaultPersonaName` persona regardless of `role` and fails loud when it is absent; the deleted ambiguity warning no longer fires; `MaterialiseAsync` sources the prompt from Epic 27 for a public persona (and fails loud when Epic 27 returns nothing); the model-contract test reflects the nullable column + renamed index.

14. **Logging**: structured `ILogger<>` logs at INFO for seeder summary (`created, skipped` + persona names) and default-persona resolution (`personaName, role`), DEBUG for prompt-source selection (`visibility, role, action`), WARN for a missing configured default persona / a persona whose model is not `IsKnown` (surfaced before write), ERROR for migration/transaction failure — never logging raw `ConfigJson`. Credential-agnostic by design: persona config carries provider+model, **never a key**; nothing in this path logs a credential (see Logging Requirements).

## Technical Design

### C# namespace / file structure

```
apps/tamma-elsa/src/
  Tamma.Data/
    Entities/
      Agent.cs                              # MODIFY — Role: string -> string?
    TammaModelConfiguration.cs              # MODIFY — Role drops .IsRequired(); index (Name,Role)->(Name)
    Migrations/ControlPlane/
      <ts>_PersonaReframeRoleNullable.cs     # NEW — alter Role nullable + swap public index
  Tamma.Api/
    Services/Agents/
      IPersonaPromptResolver.cs             # NEW — persona/public prompt seam (reads Epic 27 (principal, role, action), fail-loud)
      PersonaPromptResolver.cs              # NEW — impl over the Epic 27 IPromptStore
      AgentResolverService.cs               # MODIFY — MaterialiseAsync PUBLIC branch calls IPersonaPromptResolver
      AgentRegistryService.cs               # MODIFY — GetSystemDefaultPublicAsync rewrite; delete ambiguity warning
      DefaultPersonaOptions.cs              # NEW — bind Tamma:Agents:DefaultPersonaName
    Program.cs                              # MODIFY — bind DefaultPersonaOptions; (no new routes)
  Tamma.ElsaServer/  (or Tamma.Api seeding host — same host 32-1 used)
    AgentEntitySeeder.cs                    # MODIFY (rewrite) — named cross-role personas, explicit model
```

> The `AgentEntitySeeder` rewritten here is the **same** class 32-1 introduced (CP-resident, insert-missing-only). It stops producing `tamma-<role>` rows and produces named personas instead. The legacy `Tamma.ElsaServer/AgentSeeder.cs` (the Elsa Agents store) is untouched by this story.

### Entity change (`Agent.cs`)

```csharp
public class Agent
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;     // persona handle, globally unique among public agents
    public string? Role { get; set; }             // CHANGED: nullable — NULL for cross-role public personas
    public AgentVisibility Visibility { get; set; }
    public Guid? OwnerTenantId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public AgentStatus Status { get; set; } = AgentStatus.Active;
    public Guid? CurrentVersionId { get; set; }
    // … CreatedAt/By, UpdatedAt/By, Versions unchanged …
}
```

### EF config change (`TammaModelConfiguration.cs`)

```csharp
modelBuilder.Entity<Agent>(entity =>
{
    // … ToTable + ck_agents_visibility_ownership CHECK unchanged …
    entity.Property(e => e.Name).IsRequired().HasMaxLength(128);
    entity.Property(e => e.Role).HasMaxLength(64);          // CHANGED: no .IsRequired()

    // CHANGED: public handles unique on (Name) alone — personas are cross-role
    entity.HasIndex(e => e.Name)
        .IsUnique().HasFilter("\"Visibility\" = 0")          // 0 = Public
        .HasDatabaseName("IX_agents_public_name");
    // Private partial indexes UNCHANGED
    entity.HasIndex(e => new { e.OwnerTenantId, e.Name })
        .IsUnique().HasFilter("\"Visibility\" = 1 AND \"OwnerTenantId\" IS NOT NULL")
        .HasDatabaseName("IX_agents_private_tenant_name");
    entity.HasIndex(e => new { e.OwnerUserId, e.Name })
        .IsUnique().HasFilter("\"Visibility\" = 1 AND \"OwnerUserId\" IS NOT NULL")
        .HasDatabaseName("IX_agents_private_user_name");
});
```

> The `ck_agents_visibility_ownership` CHECK is **unaffected** — it constrains `Visibility`/owner columns, not `Role`. A public persona with `Role=NULL` still satisfies `Visibility='public' ⇒ OwnerTenantId IS NULL AND OwnerUserId IS NULL`.

### Migration (additive amendment, single snapshot)

```
ALTER TABLE agents ALTER COLUMN "Role" DROP NOT NULL;
DROP INDEX  "IX_agents_public_name_role";
CREATE UNIQUE INDEX "IX_agents_public_name" ON agents ("Name") WHERE "Visibility" = 0;
-- (legacy tamma-<role> disposition handled by the seeder at startup, not in DDL — AC11)
```

`dotnet ef migrations add PersonaReframeRoleNullable --context ControlPlaneDbContext` against the existing 32-1 snapshot; then `has-pending-model-changes` → none. **No baseline rewrite, no branch** — this extends the linear chain (EF parallel-migration hazard: stories are implemented sequentially on one snapshot).

### Seeder rewrite (`AgentEntitySeeder`)

```csharp
// Deterministic UUIDv7 per persona name; insert-missing-only (skip if a public agent named X exists).
private static readonly IReadOnlyList<PersonaSeed> Personas = new[]
{
    new PersonaSeed("claude",  "anthropic", "claude-sonnet-4-20250514"),
    new PersonaSeed("gemini",  "google",    "gemini-2.5-pro"),
    new PersonaSeed("codegpt", "openai",    "gpt-4o"),          // alias: gpt
    // new PersonaSeed("openrouter-claude", "openrouter", "anthropic/claude-3.5-sonnet"),  // optional
};

// For each persona:
//   if exists public agent with Name == persona.Name  -> skip (no event)         [AC4]
//   else create Agent { Name, Role = null, Visibility = Public, owners = null }   [AC1/AC3]
//        + AgentVersion v1 { ConfigJson = { provider, model, params, /* NO prompts */ } }
//        validate provider+model via IProviderPricingService.IsKnown (warn+skip-write if unknown) [AC3/AC14]
//        emit AGENT.CREATED.SUCCESS { agentId, version:1, visibility:"public", role:null,
//                                     personaName, provider, model, mode }          [AC12]
```

`PersonaSeed.ConfigJson` is built from the existing `AgentConfigValidator` shape (provider regex, budget range, ReDoS guard, prototype-pollution rejection) extended in 32-1, with an **explicit `model`** and **no `prompts`** block. Validation runs before any write.

### `GetSystemDefaultPublicAsync` rewrite (`AgentRegistryService`)

```csharp
// BEFORE (32-2): find the public agent whose Role == role; warn if >1.
// AFTER (this story): return the configured default persona, role-independent.
public async Task<Agent> GetSystemDefaultPublicAsync(string role, CancellationToken ct)
{
    var name = _defaultPersona.Value.DefaultPersonaName;        // "claude" by default
    var persona = await _agents.GetPublicByNameAsync(name, ct);  // Visibility='public' AND Name==name
    if (persona is null)
        throw new TammaError("AGENT_DEFAULT_PERSONA_MISSING",
            $"Configured default persona '{name}' is not seeded; cannot resolve a default for role '{role}'.",
            retryable: false, severity: Severity.High);          // FAIL LOUD — no empty fallback [AC5]
    // NOTE: the ">1 public agent for role" ambiguity warning is DELETED — public agents are cross-role.
    return persona;
}
```

`DefaultPersonaName` is bound from `Tamma:Agents:DefaultPersonaName` (default `"claude"`). In 32-16 this method additionally constrains to the tenant's **enabled** personas; that constraint is added there, not here.

### `IPersonaPromptResolver` seam + `MaterialiseAsync` wiring (`AgentResolverService`)

This story ships the persona/public prompt branch as an **explicit injectable seam** — `IPersonaPromptResolver` — rather than an inline `_promptStore.ResolveAsync` call inside `MaterialiseAsync`. The custom/private branch is the parallel `ICustomAgentPromptResolver` seam owned by 32-17.

```csharp
// NEW seam (this story owns it). The PUBLIC/persona prompt leg.
public interface IPersonaPromptResolver
{
    /// <summary>Resolve a persona's system/role prompt from the Epic 27 store keyed
    /// (principal, role, action). Tenant -> system -> ERROR; fail-loud, NEVER empty/plain.</summary>
    Task<RenderedPrompt> ResolveAsync(Principal principal, string role, string? action, CancellationToken ct);
}

// PersonaPromptResolver impl reads the Epic 27 IPromptStore and fails loud on a miss:
//   var rendered = await _promptStore.ResolveAsync(principal, role, action, ct)
//       ?? throw new TammaError("PROMPT_UNRESOLVED",            // tenant->system->error
//              $"No Epic 27 prompt for ({role},{action}); personas carry no prompts.",
//              retryable: false, severity: Severity.High);
//   return rendered;
```

```csharp
// AgentResolverService.MaterialiseAsync — merge + stamp are KEPT (32-1/32-2); the PROMPT SOURCE is the seam.
var merged = DefaultAgentConfig.ForRole(role).MergeWith(agent.CurrentVersion.ConfigJson);
merged.AgentId      = agent.Id;          // stamp (unchanged)
merged.AgentVersion = agent.CurrentVersion.Version;

// KEY CHANGE: prompt no longer comes from persona config — it comes from the IPersonaPromptResolver seam.
if (agent.Visibility == AgentVisibility.Public)               // PERSONA -> IPersonaPromptResolver -> Epic 27
{
    merged.SystemPrompt = (await _personaPrompts.ResolveAsync(principal, role, action, ct)).Text;
    // _personaPrompts is fail-loud internally (PROMPT_UNRESOLVED); no empty/plain fallback here.
}
else
{
    // Private/custom agent's own embedded prompts — owned by Story 32-17 via ICustomAgentPromptResolver.
    // SEAM: 32-17 wires merged.SystemPrompt from agent ConfigJson.prompts here. (Not implemented in 32-15.)
}
```

> `_personaPrompts` is the `IPersonaPromptResolver` seam this story ships; its impl wraps the Epic 27 prompt-store seam (`IPromptStore`/prompt+convention resolution), already round-tripped through `Tamma.Api`. `principal` = `(tenantId XOR userId)` from `ITammaModeProvider` + `ITenantContext` — the same principal `prompt_overrides`/`AgentRoleSelection` use. 32-18 wires the same `IPersonaPromptResolver` for its persona branch (the dispatch lives in 32-18; the resolve body lives here).

### Integration points

- **Story 34-11** (Provider Cost Price-Book) — `IProviderPricingService.IsKnown(provider, model)` must accept the personas' explicit `(provider, model)` pairs; the seeded models must price. **Hard prerequisite** (sequence A before B).
- **Story 32-1** (shipped) — the `Agent`/`AgentVersion` entities, `AgentEntitySeeder`, `AgentConfigValidator`, `IAgentRepository`, the DROP list, and `ControlPlaneDbContextModelTests` this story amends.
- **Story 32-2** (on `feat/exec-wave-02`) — `AgentRegistryService.GetSystemDefaultPublicAsync` and `AgentResolverService.MaterialiseAsync` this story rewrites.
- **Epic 27 prompt store** — `IPromptStore` resolution `(principal, role, action)`, tenant → system → error; the new prompt source for personas.
- **`ITammaModeProvider`** (`Tamma.Api/Services/PromptStore/TammaMode.cs`) — principal derivation for the Epic 27 key.

## Dependencies

**Internal:**

- **Story 34-11** (Provider Cost Price-Book) — the personas' explicit `(provider, model)` must be `IsKnown`/priceable. Hard prerequisite (sequence A).
- **Story 32-1** (Agent entity model — shipped on `main`) — the entity, seeder, validator, repository, DROP list, and model-contract test this story amends.
- **Story 32-2** (Registry/resolution/RBAC — on `feat/exec-wave-02`) — owner of `GetSystemDefaultPublicAsync` + `MaterialiseAsync` this story rewrites.
- **Epic 27 prompt store** — the new persona prompt source (`(principal, role, action)`, tenant → system → error).

**Consumers (downstream, not blockers):**

- **Story 32-16** (Per-tenant agent/persona enablement) — adds the enablement constraint over the personas this story seeds; constrains `GetSystemDefaultPublicAsync` to the tenant's enabled set.
- **Story 32-17** (Custom-agent prompts) — owns the **private** branch as the parallel `ICustomAgentPromptResolver` seam, and points its persona-leg delegation at this story's `IPersonaPromptResolver` by that exact name.
- **Story 32-18** (Registry enablement gate + Epic 27 prompt source) — the registry-side `CanUse`/`SelectForRoleAsync` enablement gate over these personas.
- **Story 32-5** (Call-LLM endpoint + managed execution — lynchpin F) — resolves these personas at call time.

**External:** none new.

## Testing Strategy

**Unit tests** (`tests/Tamma.Api.Tests/Agents/`, Postgres fixture per `Infrastructure/InMemoryDbFixture.cs` / `Epic28/ControlPlaneDbContextModelTests.cs` precedent):

1. **Nullable-`Role` round-trip:** insert a `Visibility='public'`, `Role=NULL` persona; read it back; `Role` is null; the `ck_agents_visibility_ownership` CHECK still passes (public + no owners).
2. **Public unique index swap:** two public agents may NOT share a `Name` (even with different/no `Role`) → second insert hits `IX_agents_public_name`; a public agent named `claude` with `Role=NULL` and a *private* agent named `claude` coexist (private partial indexes unchanged).
3. **Seeder creates named personas:** first run creates `claude`/`gemini`/`codegpt` (+ optional), each `Visibility='public'`, `Role=NULL`, `Version=1`, with explicit `provider`+`model` in `ConfigJson` and **no** `prompts` block.
4. **Seeder idempotency:** second run creates 0, skips N; no `AGENT.CREATED.SUCCESS` emitted on the skip run.
5. **Seeder never reverts an admin edit:** after an admin publishes `claude` `Version=2`, re-running the seeder leaves `Version=2` intact and writes nothing.
6. **`IsKnown` guard:** a persona whose `(provider, model)` is not `IProviderPricingService.IsKnown` is WARN-logged and not written (no half-seeded row).
7. **`GetSystemDefaultPublicAsync` returns the default persona:** with `DefaultPersonaName="claude"`, the method returns the `claude` persona for **any** `role` (architect, tester, reviewer); never role-matches.
8. **`GetSystemDefaultPublicAsync` fails loud:** with the configured persona absent, it throws `AGENT_DEFAULT_PERSONA_MISSING` (no empty/plain fallback).
9. **Ambiguity warning deleted:** seeding multiple public personas no longer logs the per-role ">1 public agent" warning (assert absence).
10. **`MaterialiseAsync` prompt source = `IPersonaPromptResolver` for personas:** a public persona resolves its system prompt via the `IPersonaPromptResolver` seam (over a fake `IPromptStore` keyed `(principal, role, action)`); the persona's `ConfigJson` carries no prompt and is NOT used as the prompt source. `MaterialiseAsync`'s public branch invokes the seam, not an inline `_promptStore.ResolveAsync`.
11. **`IPersonaPromptResolver` fails loud:** Epic 27 returns null for `(role, action)` → `PROMPT_UNRESOLVED` thrown from the seam (never empty/plain).
12. **Stamp preserved:** `MaterialiseAsync` still stamps `AgentId`/`AgentVersion` and merges onto `DefaultAgentConfig.ForRole(role)`.
13. **Legacy `tamma-<role>` disposition (AC11):** with pre-seeded `tamma-<role>` rows present, the seeder is re-runnable and applies the chosen disposition (left/archived) deterministically; archive emits `AGENT.ARCHIVED.SUCCESS` once.

**Integration tests** (Postgres-bound, `sg docker -c "dotnet test ..."`):

14. **Migration applies + `has-pending-model-changes` reports none;** `ControlPlaneDbContextModelTests.Model_Has_ExpectedControlPlaneEntities` (and the index assertions) extended to reflect the nullable `Role` column + the renamed `IX_agents_public_name`.
15. **End-to-end seed → resolve:** seed personas, set `DefaultPersonaName="gemini"`, call `GetSystemDefaultPublicAsync("architect")` → returns `gemini`; `MaterialiseAsync` produces a config whose `Provider="google"`, `Model="gemini-2.5-pro"`, prompt from Epic 27.

**Coverage**: critical paths (Role-nullable migration, seeder idempotency, default-persona resolution, prompt-source wiring, fail-loud) → 100%; entity/seeder line ≥ 80%.

Docker-bound C# suites run via `sg docker -c "dotnet test apps/tamma-elsa/..."` (session docker group is stale; plain `dotnet build` needs no wrapper).

## Estimated Effort

3-4 days

## Files Created / Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Data/Entities/Agent.cs` | Modify (`Role`: `string` → `string?`) |
| `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` | Modify (drop `Role.IsRequired()`; swap public index `(Name,Role)`→`(Name)`) |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/<ts>_PersonaReframeRoleNullable.cs` (+ `.Designer.cs`, snapshot) | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/IPersonaPromptResolver.cs` | Create (persona/public prompt seam — reads Epic 27 `(principal, role, action)`, fail-loud) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/PersonaPromptResolver.cs` | Create (impl over the Epic 27 `IPromptStore`) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentResolverService.cs` | Modify (`MaterialiseAsync` PUBLIC branch calls `IPersonaPromptResolver`; private branch = `ICustomAgentPromptResolver` seam owned by 32-17) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentRegistryService.cs` | Modify (`GetSystemDefaultPublicAsync` rewrite; delete ambiguity warning) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/DefaultPersonaOptions.cs` | Create (bind `Tamma:Agents:DefaultPersonaName`) |
| `apps/tamma-elsa/src/Tamma.ElsaServer/AgentEntitySeeder.cs` | Modify (rewrite: named cross-role personas, explicit model, no prompts, legacy disposition) |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (bind `DefaultPersonaOptions`) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/AgentEntitySeederTests.cs` | Modify (persona seeding, idempotency, no-revert, IsKnown guard, legacy disposition) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/AgentRegistryServiceTests.cs` | Modify/Create (default-persona resolution, fail-loud, no ambiguity warning) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/AgentResolverServiceTests.cs` | Modify/Create (prompt source = Epic 27, fail-loud, stamp preserved) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Epic28/ControlPlaneDbContextModelTests.cs` | Modify (nullable `Role`, renamed `IX_agents_public_name`) |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:
1. Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
2. Searched `.dev/` for related spikes, bugs, findings, decisions
3. Read the design of record: `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` (§3.0–§3.1) + the re-plan `docs/superpowers/plans/2026-06-20-epic-32-37-replan.md` (§1)
4. Re-read shipped 32-1 (the entity/seeder you amend) and 32-2 (the resolver/registry you rewrite)
5. Reviewed the closest existing patterns: `ConventionStoreSeeder` (insert-missing-only idempotency), `PromptOverride` (XOR CHECK + principal resolution), the Epic 27 `IPromptStore` resolution
6. Planned the TDD approach (Red-Green-Refactor)

### Key design decisions

- **Persona = system agent, not style overlay.** Public agents stop being per-role `tamma-<role>` and become named cross-role personas (`claude`/`gemini`/`codegpt`). `Role` becomes a *selection-time* concern, not a baked-in identity column — hence nullable for public personas. (The old 32-12 "style overlay within a role" is a different, optional story — not this one.)
- **Explicit model per persona.** Today the seed omits `model` and leans on `DefaultAgentConfig.ForRole` → `claude-sonnet-4`. That collapses every persona to Anthropic. Each persona now pins its own `provider`+`model` so `gemini`/`codegpt` actually run on Google/OpenAI; the models must price under 34-11.
- **Personas are prompt-free.** The role/system prompt is the Epic 27 store's job, keyed `(principal, role, action)`, tenant → system → error. A persona never carries a prompt — that keeps SaaS audit/compliance simple (no per-persona prompt fork). Custom prompts ⇔ custom agent (32-17).
- **Fail loud, never empty.** Both the default-persona resolution and the persona-prompt resolution fail loud when their source is absent (`feedback_resolution_no_empty_fallback`). No silent fallback to a plain/empty prompt or a wrong-provider default.
- **Amend, don't rebuild.** The 32-1 entity model is sound; this is a schema *amendment* (nullable column + index swap) on the existing linear migration snapshot — not a baseline rewrite. Sequential implementation keeps the EF snapshot consistent.
- **Sibling boundaries are firm.** Enablement (32-16), the private-prompt branch (32-17), and the registry enablement gate (32-18) are explicitly NOT in scope; this story leaves the `MaterialiseAsync` private branch as a marked seam for 32-17.

### Per-mode ownership (mandatory two-scoping-model answer, per CLAUDE.md)

| Question | single-user mode | SaaS mode |
|---|---|---|
| Who owns the public personas? | The platform — personas are `Visibility='public'`, `OwnerTenantId/OwnerUserId NULL`; the sole user uses them as-is. | The platform — same public personas, shared cross-tenant. Tenants do not own/edit personas (they enable a subset via 32-16). |
| Who may create/edit a persona? | `PlatformOwnerAccess` (platform-owner only). The sole user is NOT a platform owner by virtue of owning their data. | `PlatformOwnerAccess` (platform-owner only; NOT `OwnerAccess`, which admits every personal-tenant owner). Tenant owner/admin/member cannot edit public personas. |
| What principal keys the persona's prompt (Epic 27)? | The sole user (`OwnerUserId` / user principal) — `(userId, role, action)`. | The tenant (`OwnerTenantId` / tenant principal) — `(tenantId, role, action)`. No per-user prompt layer in SaaS. |
| Who picks the default persona? | The platform config `DefaultPersonaName`; the sole user's selection (32-2 `AgentRoleSelection`) may override per role. | The platform config `DefaultPersonaName`, then constrained to the tenant's **enabled** set (32-16); tenant selection (`AgentRoleSelection`) overrides per role within the enabled set. |
| Where do persona seeding events land? | Control-plane `DomainEvents`, `TenantId = NULL` (platform feed). | Control-plane `DomainEvents`, `TenantId = NULL` (platform feed). Persona definitions are platform-global; no tenant-scoped persona data here. |
| Mode source | `ITammaModeProvider` (`TammaMode.cs`) — process-stable. | same |

### Migration discipline (Epic 28 conventions)

- The migration is **additive amendment** on the existing 32-1 snapshot: `ALTER COLUMN "Role" DROP NOT NULL` + drop/create the public partial index. Not a baseline CHECK edit, not a branch.
- After adding, `dotnet ef migrations has-pending-model-changes --context ControlPlaneDbContext` → must report none.
- Mirror entity config **only** in `TammaModelConfiguration.cs`; the snapshot/Designer are generated, not hand-edited.
- `Agent`/`AgentVersion` are already in the `Program.cs` startup-reset DROP list (32-1) — **no new table**, so nothing is added to the DROP list; but the **renamed index + nullable column** must be reflected in `ControlPlaneDbContextModelTests` so the strict `BeEquivalentTo` model contract test stays green.
- Run C# tests with `sg docker -c "dotnet test ..."` (session docker group is stale; build needs no wrapper).

### Edge cases

- A persona whose configured model is not `IProviderPricingService.IsKnown` (34-11 not yet seeded that model) → WARN + skip-write (no half-seeded persona); fix is to seed the price first.
- `DefaultPersonaName` points at a persona that is archived/absent → `GetSystemDefaultPublicAsync` fails loud (AC5/AC8).
- A pre-existing `tamma-<role>` row (from 32-1 on `main`) → AC11 disposition (left-in-place or archived), idempotent on re-run; never destructive-delete (immutable history).
- Two public personas with the same `Name` (e.g. a re-seed race) → second hits `IX_agents_public_name` → 409/skip; the seeder's skip-by-existing-name avoids the race.
- A private agent named `claude` (a tenant's custom agent) coexists with the public `claude` persona — private partial indexes are unchanged and scope by owner.

## Risks & Mitigations

| Risk | Severity | Mitigation |
|------|----------|------------|
| Nullable-`Role` migration breaks the strict model-contract test | High | Update `ControlPlaneDbContextModelTests` in the same change; assert `Role` nullable + `IX_agents_public_name` present and `IX_agents_public_name_role` absent. |
| A persona's model isn't priceable (34-11 gap) → seeded persona can't meter | High | `IsKnown` guard before write (WARN + skip); 34-11 is a hard prerequisite; integration test asserts every seeded model prices. |
| Prompt source silently empties (regression of the no-empty-fallback rule) | High | `MaterialiseAsync` and `GetSystemDefaultPublicAsync` both fail loud; explicit fail-loud tests; never substitute a plain/empty prompt. |
| Legacy `tamma-<role>` rows shadow the named-persona default resolution | Medium | AC11 deterministic disposition (archive or leave) applied idempotently by the seeder; default resolves over named personas only. |
| Rewriting `MaterialiseAsync` collides with 32-17's private-prompt branch | Medium | This story implements ONLY the public/persona branch via the `IPersonaPromptResolver` seam and leaves the parallel `ICustomAgentPromptResolver` seam for the private branch; 32-17 fills it; tests scope to the public branch. |
| Branch/snapshot drift (32-2 lives on `feat/exec-wave-02`, 32-1 on `main`) | Medium | Sequential implementation on a single linear snapshot; the re-plan recommends merging `feat/exec-wave-02` before the redesign stories; this story amends whichever snapshot is current. |

## Success Metrics

- [ ] Public agents are named cross-role personas (`claude`/`gemini`/`codegpt`) with `Role=NULL` and explicit `provider`+`model`; zero `tamma-<role>` rows are created by the rewritten seeder.
- [ ] `GetSystemDefaultPublicAsync(role)` returns the configured `DefaultPersonaName` persona for every role; the per-role ambiguity warning is gone.
- [ ] Every persona run's system prompt comes from the Epic 27 store (grep confirms no prompt is sourced from persona `ConfigJson`); fail-loud on absence.
- [ ] `has-pending-model-changes` reports none; the model-contract test reflects the nullable column + renamed index; the `Tamma.Api.Tests` suite is green.

## Related

- Design of record: `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` (§3.0 reframe table, §3.1 persona entity)
- Re-plan: `docs/superpowers/plans/2026-06-20-epic-32-37-replan.md` (§1 disposition of 32-1; §4 sequence step B)
- Implementation plan: `docs/superpowers/plans/2026-06-21-32-15-persona-reframe-and-seeding-plan.md`
- Amends: `docs/stories/epic-32/story-32-1/32-1-agent-entity-model-and-versioned-saved-config.md` (entity + seeder), `docs/stories/epic-32/story-32-2/` (registry/resolver)
- Sibling stories: 34-11 (Provider Cost Price-Book — prereq), 32-16 (enablement), 32-17 (custom-agent prompts), 32-18 (registry enablement gate), 32-5 (call-LLM endpoint)

## Logging Requirements

- **INFO**: seeder summary (`created, skipped` + persona names), each persona created (`personaName, agentId, provider, model`), default-persona resolution (`personaName, role`), legacy `tamma-<role>` disposition (`disposition, count`).
- **DEBUG**: prompt-source selection in `MaterialiseAsync` (`visibility, role, action` — never the prompt body), config-validation pass (`personaName`).
- **WARN**: configured default persona missing (before failing loud), a persona whose `(provider, model)` is not `IProviderPricingService.IsKnown` (skip-write), persona name collision on re-seed.
- **ERROR**: migration/transaction failure (`migration`), event-append failure after a state transition.
- **Structured context**: include `{ agentId, personaName, version, visibility, role, provider, model, mode }` where applicable.
- **Credential safety**: persona `ConfigJson` carries `provider`+`model` only — **never an API key** (BYOK/platform keys are resolved at call time in `Tamma.Api` by 32-3, never on a persona). Never log raw `ConfigJson`; never log a prompt body; nothing on this path holds or logs a credential.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-21 | 1.0.0   | Initial story creation | Claude |
| 2026-06-21 | 1.0.1   | Cross-spec reconciliation (C1): this story now OWNS the persona/public prompt leg as an explicit injectable seam — interface `IPersonaPromptResolver.ResolveAsync(Principal, role, action?, ct)` (reads the Epic 27 store, fail-loud). AC6/AC7 reworded so `MaterialiseAsync`'s PUBLIC branch calls the seam (not an inline `_promptStore.ResolveAsync`); the private branch points at 32-17's parallel `ICustomAgentPromptResolver`. | Claude |
