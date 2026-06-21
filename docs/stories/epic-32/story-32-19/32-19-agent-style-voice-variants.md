# Story 32-19: Agent Style/Voice Variants (`AgentStyleVariant`)

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)

## User Story

As a **tenant owner/admin (SaaS) or self-hosted user (single-user)**,
I want to define optional **style/voice variants** — small tone/verbosity overlays (e.g. _terse_ vs _verbose_, _formal_ vs _casual_ review voice) — and bind at most one to a given role, so that a run keeps the **same persona/custom agent, provider, model, and credential** but speaks in my preferred voice,
So that the **style/tone overlay** (the `atlas`/`nova` idea split out of the old 32-12) becomes a first-class, **orthogonal** dimension that the call-LLM endpoint (32-5) merges **on top of** the Epic-27/custom prompt — additively, never replacing it, never empty-fallback — without being confused with a persona (the named system agent) or a custom agent (own prompts).

## Priority

P2 — This is an **optional, additive** overlay split out of 32-12 per design §3.4. The locked model reserves the word **persona** for the named cross-role system agent (provider+model+config — 32-15) and **custom agent** for own-prompt private agents (32-17); the old 32-12 "persona = style/tone overlay within a role" (`atlas`/`nova`) **directly contradicts** that vocabulary. Rather than discard the still-valuable tone/verbosity idea, this story re-homes it as a **separate, optional `AgentStyleVariant`** — explicitly **NOT a persona**. It is orthogonal to the whole agent pivot (a run may apply zero or one variant; default = none = no behaviour change), so it sequences **after** the lynchpin (sequence **G**, post-F): the 32-5 endpoint must already render the base prompt before a variant can ride on top of it. Nothing in the pivot blocks on this story.

## Context

The Epic 32 architecture pivot (`docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md`) locks three distinct concepts that the old wording blurred (design §3.0 table, §3.4):

| Concept | What it is | Owned by | Changes provider/model/cred? |
|---|---|---|---|
| **Persona** | a **named cross-role system agent** presetting `{ provider, model, config }`; prompt-free (prompt from Epic 27) | 32-15 | yes — it _is_ the provider+model |
| **Custom agent** | a private `Agent` carrying **its own prompts** + config | 32-17 | yes — it carries provider+model+prompts |
| **Style/voice variant** (this story) | an **optional tone/verbosity overlay** applied on top of a resolved agent at call time | **32-19** | **no** — voice only |

A **style/voice variant** is a small, declarative **style descriptor** — tone/verbosity knobs (`tone`, `verbosity`, `format`, `audience`) and/or a short free-text **style-prompt fragment** — that says _how_ the resolved agent should speak, not _what_ provider/model/prompt it uses. It is the `atlas`/`nova` "review voice" idea from the old 32-12, correctly named and decoupled. Key invariants:

- **It is NOT a persona and NOT a custom agent.** It never selects or changes the provider, the model, the credential (`credentialSource` is untouched), or the base prompt. It carries no `provider`/`model`/key fields. The 32-5 endpoint resolves the agent (32-18) and credential (32-3) **exactly as it does today**, renders the base prompt (Epic 27 for personas / the custom agent's own prompts), and only **then** applies the variant as an **additive** overlay to the rendered prompt.
- **Additive, after base resolution, never empty-fallback.** The variant is merged **after** the Epic-27/custom-prompt resolution (design §2.6 step 4 → a new step 4b), as a deterministic suffix/section appended to the resolved system prompt. It can only **add** style guidance; it can never replace, blank, or short-circuit the base prompt. A bound-but-missing/disabled variant resolves to **no overlay** (silently — a variant is optional by definition), but the **base** prompt resolution still obeys tenant→system→**error** (32-5 AC3 / `feedback_resolution_no_empty_fallback`); the variant never weakens that.
- **Optional + orthogonal.** Zero or one variant per `(principal, role)`. **Default = none** → the rendered prompt is byte-for-byte what 32-5 produces without this story. Binding is per-`(principal, role)` (like agent selection in 32-2's `AgentRoleSelection`), constrained to the principal's **enabled** variants.

This story owns the **entity** (`AgentStyleVariant` + the per-role binding `AgentStyleVariantSelection`), the **CRUD + enable/bind API**, the **events**, and the **`ResolveActiveVariantAsync` / `ComposeOverlay` primitives** the 32-5 endpoint calls. It mirrors the same **visibility / principal-XOR / unique-nulls-not-distinct index discipline** as `prompt_overrides` (Epic 27), `AgentRoleSelection` (32-2), and `TenantAgentEnablement` (32-16). Because both tables are NEW control-plane / public-schema tables in SaaS, they MUST be added to the `Program.cs` startup-reset DROP list and the `ControlPlaneDbContextModelTests` strict entity list (see AC8, AC9 and Dev Notes).

### Boundary with sibling stories (do NOT cross these lines)

- **vs 32-12 (rewrite):** 32-12 is rewritten so "persona" = the named system agent. **Benchmarking (32-12 family / 32-10) keys on the persona-agent identity** (`AgentId`/persona name), NOT on a variant. A variant is a **separate optional dimension**; this story does **not** add a variant axis to benchmarking. If a future story wants to A/B a variant, it does so on top of 32-10's agent-keyed harness — out of scope here.
- **vs 32-15 (persona seeder):** the persona seeder seeds the named cross-role public personas (provider+model+config). This story seeds an **optional** shipped-default style-variant catalog (e.g. `terse`, `verbose`, `formal`) **separately**; a persona is never a variant and a variant never appears in the persona catalog.
- **vs 32-17 (custom-agent prompts):** a custom agent carries its **own base prompts**. A variant overlays voice on top of **any** resolved agent — persona or custom. The variant is not a substitute for, and does not edit, a custom agent's prompts; it is appended after them.
- **vs 32-5 (the endpoint that applies the overlay):** 32-5 owns the call-LLM composition. This story **does not** modify the gate/agent-resolve/credential/loop/meter sequence. It adds **one optional step (4b)** between "render base prompt" (step 4) and "emit `AGENT.RUN.STARTED`" (step 5): resolve the active variant and compose the overlay onto the rendered system prompt. The wiring point is the `IAgentStyleVariantService` interface this story ships; 32-5 (or a tiny follow-on amendment to it) calls it.
- **vs Epic 27 (the base prompt the overlay rides on):** Epic 27 owns the tenant→system→error base prompt. A variant is **never** an Epic-27 prompt override and never enters the prompt store. It is a thin, additive style layer on the rendered output of Epic 27 — it cannot blank or replace what Epic 27 resolved.

## Acceptance Criteria

1. **New entity `AgentStyleVariant`** (`apps/tamma-elsa/src/Tamma.Data/Entities/AgentStyleVariant.cs`) with fields: `Id` (UUID PK), `TenantId` (UUID, NULL in single-user), `UserId` (UUID, NULL in SaaS), `Visibility` (`Public` | `Private`), `Name` (e.g. `terse`, `formal-review`), `Description?`, a **style descriptor** `StyleJson` (`{ tone?, verbosity?, format?, audience?, stylePrompt? }` — knobs and/or a short free-text fragment), `Enabled` (BOOLEAN), `CreatedAt`/`CreatedBy`, `UpdatedAt`/`UpdatedBy`. EF config carries the **principal XOR** CHECK and a **`UNIQUE NULLS NOT DISTINCT (TenantId, UserId, Name)`** index for private variants, exactly mirroring `AgentRoleSelection` / `TenantAgentEnablement`. It carries **NO** `provider`, `model`, `credential`, or base-prompt fields — a variant cannot change those.

2. **New binding entity `AgentStyleVariantSelection`** (per-`(principal, role)`, at most one variant) with fields: `Id`, `TenantId?`, `UserId?` (principal XOR), `Role` (one of the 8 valid roles), `VariantId` (UUID NOT NULL), audit columns. **`UNIQUE NULLS NOT DISTINCT (TenantId, UserId, Role)`** — one bound variant per role per principal — mirroring `AgentRoleSelection`'s `(principal, role)` uniqueness. Binding a variant the principal cannot see (not public, not its own private) or that is disabled → `404`/`409` (existence-leak-safe, matching 32-2's cross-tenant rule).

3. **`ResolveActiveVariantAsync(role)` query primitive** is exposed by a new `IAgentStyleVariantService` (`Tamma.Api/Services/Agents/`). Semantics: returns the **single** variant bound for the current principal's `(principal, role)` **iff** it exists, is visible, **and is enabled**; otherwise returns **`null`** (no variant — the default, silent, no behaviour change). A disabled or retired bound variant resolves to `null` (the binding is not an error; a variant is optional by definition). This primitive is what 32-5 calls at step 4b.

4. **`ComposeOverlay(renderedSystemPrompt, variant)` is additive and deterministic.** A companion `ComposeOverlay` (pure, no I/O) appends the variant's style guidance to the already-rendered system prompt as a clearly-delimited **style section** (e.g. a trailing `\n\n## Response style\n<rendered knobs + stylePrompt>`). It **MUST** be a pure suffix: given `null` variant → returns the input unchanged; given a variant → returns `base + overlay`, with the base **substring-preserved verbatim** (no edit, no truncation, no reordering). It can **only add**; a test asserts the base prompt is a prefix of the result. The knobs render to stable, human-readable directives (`tone=formal` → "Use a formal tone."); an empty/whitespace `stylePrompt` contributes nothing (but never blanks the base).

5. **CRUD API** for variants under a new `/api/style-variants` group: `GET` (list visible — public ∪ own-private), `GET /{id}`, `POST` (create own-private variant), `PUT /{id}` (update own variant), `DELETE /{id}` (archive own variant). Public shipped variants are read-only to tenants (managed by `PlatformOwnerAccess`, NOT `OwnerAccess`). Create/update/delete of an **own-private** variant requires `tenant_owner`/`tenant_admin` (SaaS) / the sole user (single-user); SaaS `member` → **403** on writes, reads allowed.

6. **Enable + bind API.** `PUT /api/style-variants/{id}/enablement` (`{ "enabled": true|false }`) toggles a variant's membership in the principal's usable set (same per-tenant catalog-membership pattern as 32-16; own-private variants are implicitly enabled). `PUT /api/style-variants/bindings/{role}` (`{ "variantId": guid|null }`) binds at most one **enabled** variant to a role for the principal (`variantId:null` clears the binding → back to no overlay). Binding an un-enabled/unseen variant → `404`/`409`. Reads of bindings allowed for any member; writes are owner/admin (member → 403).

7. **Per-mode RBAC** mirrors the Prompt Store / 32-2 / 32-16:
   - SaaS `member` → **403** on variant create/update/delete, enablement writes, and bind writes; reads (`GET` list/bindings) allowed.
   - SaaS `tenant_owner` / `tenant_admin` → manage own-private variants, enablement, and bindings for **their own tenant only**.
   - **Single-user** mode → the sole user (auto-owner) manages variants/enablement/bindings for themselves; no member gate; principal is `UserId`.
   - **Public-catalog management** of shipped variants (creating/retiring the platform-wide `terse`/`verbose`/… set) stays `PlatformOwnerAccess` and is the seeder's domain (AC10), not exposed on the tenant route.

8. **`Program.cs` startup-reset DROP list** is amended: the two new public-schema/control-plane tables `agent_style_variants` and `agent_style_variant_selections` are appended to the destructive test-host wipe list ("Wiping Tamma-managed public-schema tables") so a second host boot does not fail with `relation "agent_style_variants" already exists`. Both are CP-resident (keyed by `TenantId` in SaaS, `UserId` in single-user); they do **not** go through the per-tenant `EfTenantDbMigrator` (which owns `t_<hex>` tables only).

9. **`ControlPlaneDbContextModelTests.Model_Has_ExpectedControlPlaneEntities`** strict `BeEquivalentTo` list is updated to include `AgentStyleVariant` and `AgentStyleVariantSelection`, and both are registered as `DbSet`s on **`ControlPlaneDbContext`** with their EF config in the single `TammaModelConfiguration` source. `dotnet ef migrations has-pending-model-changes --context ControlPlaneDbContext` reports none after the new migration.

10. **Seeded optional default catalog.** A small set of shipped **public** style variants (e.g. `terse`, `verbose`, `formal`, `casual`) is seeded via an `AgentStyleVariantSeeder` (insert-missing-only; never reverts a tenant edit). **No variant is bound by default** — a brand-new principal has zero bindings, so the default behaviour is **no overlay** (orthogonality preserved). The seeded variants are merely _available_ to bind; they change nothing until a principal explicitly binds one.

11. **Orthogonality + no-fallback proof (the load-bearing contract).**
    - **Default = none:** with no binding, the rendered system prompt the loop sees is **identical** to the 32-5 output without this story (a golden test asserts byte-for-byte equality vs `ComposeOverlay(base, null)`).
    - **Additive only:** with a binding, the base prompt is a **verbatim prefix** of the composed prompt; the variant never edits/blanks/replaces the base.
    - **Never weakens base resolution:** the variant overlay is applied **after** Epic-27/custom-prompt resolution; if the **base** prompt fails to resolve (tenant→system→error), 32-5 still fails loud — the variant cannot rescue or mask an empty base (consistent with `feedback_resolution_no_empty_fallback`). A missing/disabled **variant** is _not_ an error (optional) and resolves to no overlay.
    - **Provider/model/credential untouched:** an integration test asserts `providerUsed`/`modelUsed`/`credentialSource` are identical with and without a bound variant for the same agent.

12. **DCB events.** `AGENT.STYLE_VARIANT.CREATED/UPDATED/DELETED.SUCCESS`, `AGENT.STYLE_VARIANT.ENABLED/DISABLED.SUCCESS`, and `AGENT.STYLE_VARIANT.BOUND.SUCCESS` / `AGENT.STYLE_VARIANT.UNBOUND.SUCCESS` are emitted via `IEventRepository.AppendAsync`, tagged `{ variantId, variantName, role?, mode, tenantId | userId }`. Exactly one event per successful write. Additionally, when 32-5 applies a variant, the run's `AGENT.RUN.STARTED` (owned by 32-5) gains an optional `styleVariantId` tag (a tag addition only — this story does not own the event); when no variant applies, the tag is absent.

13. **Unit + integration tests** cover: entity XOR + unique-nulls-not-distinct constraints; `ResolveActiveVariantAsync` truth table (bound+enabled→variant, bound+disabled→null, unbound→null, retired→null); `ComposeOverlay` purity (null→identity; variant→base-is-prefix; empty stylePrompt→no contribution; knob rendering stable); per-mode principal keying (single-user `UserId` vs SaaS `TenantId`); member 403 on writes / reads allowed; cross-tenant isolation; the orthogonality golden test (default=none byte-for-byte); provider/model/credential-unchanged integration test against 32-5; DROP-list second-boot + CP model-test + `has-pending-model-changes` → none.

## Technical Design

### Where it lives

```
apps/tamma-elsa/src/Tamma.Data/Entities/
  AgentStyleVariant.cs                     # NEW — the style/voice variant entity (CP-resident; user-keyed single-user)
  AgentStyleVariantSelection.cs            # NEW — per-(principal,role) binding (at most one variant)

apps/tamma-elsa/src/Tamma.Data/
  TammaModelConfiguration.cs               # MODIFY — both entities: XOR check + unique-nulls-not-distinct index
  ControlPlaneDbContext.cs                 # MODIFY — DbSet<AgentStyleVariant>, DbSet<AgentStyleVariantSelection>
  Migrations/ControlPlane/*_AddAgentStyleVariants.cs   # NEW (generated)

apps/tamma-elsa/src/Tamma.Api/Services/Agents/
  IAgentStyleVariantService.cs             # NEW — CRUD/enable/bind + ResolveActiveVariantAsync + ComposeOverlay
  AgentStyleVariantService.cs              # NEW — impl (upsert, events, visibility/XOR, additive compose)
  AgentStyleVariantEventTypes.cs           # NEW — AGENT.STYLE_VARIANT.* constants
  AgentStyleVariantSeeder.cs               # NEW — seed shipped public variants (insert-missing-only); NO default binding

apps/tamma-elsa/src/Tamma.Api/Endpoints/
  StyleVariantEndpoints.cs                 # NEW — /api/style-variants CRUD + enablement + bindings

apps/tamma-elsa/src/Tamma.Api/Dtos/Agents/
  StyleVariantResponse.cs, StyleVariantRequest.cs, SetVariantBindingRequest.cs   # NEW — DTOs

apps/tamma-elsa/src/Tamma.Api/Program.cs   # MODIFY — DI; /api/style-variants routes; STARTUP-RESET DROP-LIST amend (AC8)
```

> **The 32-5 call site (4b) is owned by 32-5, wired via the interface.** This story ships `IAgentStyleVariantService` and does NOT edit `ManagedAgent.RunAsync`. 32-5 (or a tiny amendment to it) injects the service and inserts step 4b. The contract is the interface — this keeps 32-19 from touching the call-LLM composition.

### `AgentStyleVariant` entity (NEW)

```csharp
// Tamma.Data/Entities/AgentStyleVariant.cs
namespace Tamma.Data.Entities;

/// <summary>
/// An OPTIONAL tone/voice/verbosity overlay applied on top of a resolved agent
/// (persona OR custom) at call time (Epic 32, design §3.4 — the style/voice idea
/// split out of the old 32-12). It is NOT a persona (= the named system agent,
/// 32-15) and NOT a custom agent (= own prompts, 32-17): it never changes
/// provider/model/credential/base-prompt. It only ADDS style guidance, merged by
/// 32-5 AFTER Epic-27/custom-prompt resolution. CP-resident in SaaS (keyed by
/// TenantId); user-keyed in single-user (keyed by UserId). Principal XOR, same
/// discipline as AgentRoleSelection / prompt_overrides.
/// </summary>
public class AgentStyleVariant
{
    public Guid Id { get; set; }

    /// <summary>Set in SaaS; NULL in single-user. XOR with UserId. NULL for Public shipped variants.</summary>
    public Guid? TenantId { get; set; }

    /// <summary>Set in single-user; NULL in SaaS. XOR with TenantId.</summary>
    public Guid? UserId { get; set; }

    /// <summary>Public (shipped, read-only to tenants) or Private (own).</summary>
    public AgentVisibility Visibility { get; set; }

    /// <summary>Stable handle, e.g. "terse", "formal-review".</summary>
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    /// <summary>The STYLE DESCRIPTOR — tone/verbosity knobs and/or a short style-prompt
    /// fragment: { tone?, verbosity?, format?, audience?, stylePrompt? }. NO provider,
    /// model, credential, or base-prompt fields — a variant cannot change those.</summary>
    public string StyleJson { get; set; } = "{}";

    /// <summary>Catalog membership for the principal (per-tenant), like 32-16.</summary>
    public bool Enabled { get; set; }

    public DateTime CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
}
```

```csharp
// Tamma.Data/Entities/AgentStyleVariantSelection.cs — per-(principal, role), at most one variant.
public class AgentStyleVariantSelection
{
    public Guid Id { get; set; }
    public Guid? TenantId { get; set; }   // XOR
    public Guid? UserId { get; set; }     // XOR
    public string Role { get; set; } = string.Empty;   // one of the 8 valid roles
    public Guid VariantId { get; set; }   // the single bound variant
    public DateTime CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
}
```

EF model config (in `TammaModelConfiguration.cs`, the single source — **identical discipline to `AgentRoleSelection` / `TenantAgentEnablement`**):

```csharp
modelBuilder.Entity<AgentStyleVariant>(b =>
{
    b.ToTable("agent_style_variants");
    b.HasKey(x => x.Id);
    b.Property(x => x.Name).IsRequired();
    b.Property(x => x.StyleJson).IsRequired();
    b.Property(x => x.Enabled).IsRequired();

    // principal XOR (mirrors prompt_overrides / agent_role_selections / tenant_agent_enablements)
    b.ToTable(t => t.HasCheckConstraint(
        "ck_agent_style_variants_principal_xor",
        "((tenant_id IS NOT NULL AND user_id IS NULL) OR (tenant_id IS NULL AND user_id IS NOT NULL))"));

    // one private variant name per principal
    b.HasIndex(x => new { x.TenantId, x.UserId, x.Name })
        .IsUnique().AreNullsDistinct(false);
});

modelBuilder.Entity<AgentStyleVariantSelection>(b =>
{
    b.ToTable("agent_style_variant_selections");
    b.HasKey(x => x.Id);
    b.Property(x => x.Role).IsRequired();
    b.Property(x => x.VariantId).IsRequired();

    b.ToTable(t => t.HasCheckConstraint(
        "ck_agent_style_variant_selections_principal_xor",
        "((tenant_id IS NOT NULL AND user_id IS NULL) OR (tenant_id IS NULL AND user_id IS NOT NULL))"));

    // at most one bound variant per (principal, role)
    b.HasIndex(x => new { x.TenantId, x.UserId, x.Role })
        .IsUnique().AreNullsDistinct(false);
});
```

> **CP-resident, not tenant-schema.** Like the public `Agent`/persona catalog and `TenantAgentEnablement`, both variant tables live in the **control plane** (`ControlPlaneDbContext`), SaaS rows scoped by `TenantId`, single-user rows by `UserId`. They join the **CP DROP list (AC8)** and the **`ControlPlaneDbContextModelTests` strict list (AC9)** and do NOT go through the per-tenant `EfTenantDbMigrator`.

### `IAgentStyleVariantService` (NEW) — owns the entities + the resolve/compose primitives

```csharp
// Tamma.Api/Services/Agents/IAgentStyleVariantService.cs
public interface IAgentStyleVariantService
{
    // ---- CRUD (own-private; public shipped variants are read-only) ----
    Task<IReadOnlyList<StyleVariantState>> ListAsync(CancellationToken ct = default);     // public ∪ own-private
    Task<StyleVariantState?> GetAsync(Guid id, CancellationToken ct = default);
    Task<StyleVariantState> CreateAsync(StyleVariantInput input, CancellationToken ct = default);
    Task<StyleVariantState> UpdateAsync(Guid id, StyleVariantInput input, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);                              // archive own-private

    // ---- catalog membership + per-role binding ----
    Task<StyleVariantState> SetEnabledAsync(Guid id, bool enabled, CancellationToken ct = default);
    Task BindAsync(string role, Guid? variantId, CancellationToken ct = default);          // null clears (no overlay)

    // ---- The primitives 32-5 calls at step 4b ----

    /// <summary>The single variant bound for (current principal, role) IFF it exists,
    /// is visible, and is enabled; else null (no overlay — the default). A disabled or
    /// retired bound variant resolves to null (optional, never an error).</summary>
    Task<ResolvedStyleVariant?> ResolveActiveVariantAsync(string role, CancellationToken ct = default);

    /// <summary>PURE, additive. Appends the variant's style guidance to an ALREADY-RENDERED
    /// system prompt as a delimited style section. variant==null => returns base unchanged.
    /// Otherwise base is a verbatim PREFIX of the result. NEVER edits/blanks/replaces base.</summary>
    string ComposeOverlay(string renderedSystemPrompt, ResolvedStyleVariant? variant);
}

public sealed record StyleVariantState(
    Guid Id, string Name, string? Description, AgentVisibility Visibility,
    StyleDescriptor Style, bool Enabled, bool ImplicitlyEnabled);

public sealed record StyleDescriptor(
    string? Tone, string? Verbosity, string? Format, string? Audience, string? StylePrompt);

public sealed record ResolvedStyleVariant(Guid VariantId, string Name, StyleDescriptor Style);
```

The impl derives the principal from `ITammaModeProvider` + `ITenantContext`/`ClaimsPrincipal` (SaaS ⇒ `TenantId`; single-user ⇒ `UserId`), reads/writes `ControlPlaneDbContext.AgentStyleVariants` / `…Selections`, validates targets are in (public ∪ own-private), and appends the DCB events.

### The 32-5 call-site (step 4b — owned by 32-5, wired here via the interface)

```
... 32-5 ManagedAgent.RunAsync composition (design §2.6) ...
 4. prompt = await _promptRenderer.RenderAsync(principal, role, action, resolved, variables) // Epic 27 / custom — tenant→system→ERROR
 4b. variant = await _styleVariants.ResolveActiveVariantAsync(role, ct)        // 32-19 — null = no overlay (default)
     systemPrompt = _styleVariants.ComposeOverlay(prompt.System, variant)       // additive; base is a verbatim prefix
 5. emit AGENT.RUN.STARTED { ..., styleVariantId = variant?.VariantId }         // optional tag only (event owned by 32-5)
 6. loop = await _toolLoop.RunAsync(..., systemPrompt: systemPrompt, ...)       // unchanged otherwise
 ... provider/model/credential/cost path UNCHANGED ...
```

The base prompt resolution (step 4) keeps its tenant→system→**error** contract; step 4b can only **add**. A `null` variant makes the systemPrompt identical to step 4's output — the orthogonality guarantee (AC11).

### `ComposeOverlay` — additive style section (pure)

```csharp
// variant == null  => return base verbatim (no overlay; the default).
// variant != null  => base + "\n\n## Response style\n" + RenderDirectives(style)
//   RenderDirectives: stable knob lines ("Use a formal tone.", "Be terse.") + the
//   trimmed stylePrompt fragment. Empty/whitespace stylePrompt contributes nothing.
//   Invariant (tested): base is a PREFIX of the returned string; never edits the base.
```

### DCB events

| Event | Tags | When |
|---|---|---|
| `AGENT.STYLE_VARIANT.CREATED/UPDATED/DELETED.SUCCESS` | `{ variantId, variantName, mode, tenantId \| userId }` | own-private CRUD |
| `AGENT.STYLE_VARIANT.ENABLED/DISABLED.SUCCESS` | `{ variantId, variantName, mode, tenantId \| userId }` | catalog-membership toggle |
| `AGENT.STYLE_VARIANT.BOUND/UNBOUND.SUCCESS` | `{ variantId?, variantName?, role, mode, tenantId \| userId }` | per-role bind/clear |

Appended via `IEventRepository.AppendAsync`; tenant-scope events carry the ambient `TenantId`; single-user events carry `userId` (`TenantId == null`). The run-level `AGENT.RUN.STARTED` `styleVariantId` tag is owned by 32-5 (tag addition only).

### Startup-reset DROP list (AC8) — explicit codebase gotcha

`agent_style_variants` and `agent_style_variant_selections` are NEW control-plane / public-schema tables. Append both to the destructive test-host wipe list in `Program.cs` (the "Wiping Tamma-managed public-schema tables" block) alongside `agent_role_selections`, `tenant_agent_enablements`, `prompt_overrides`, the public `Agent`/`AgentVersion` catalog, etc. Without this, a second test-host boot fails with `relation "agent_style_variants" already exists`. Both are CP-resident → CP wipe list, **not** the per-tenant `EfTenantDbMigrator` path.

## Dependencies

**Internal:**

- **Story 32-12 (rewrite)** — establishes the locked vocabulary (persona = named system agent); this story is the **split-out** of 32-12's old style/tone overlay idea per design §3.4. Benchmarking stays agent-keyed (this story adds no variant axis to it). Conceptual prerequisite; cross-referenced both ways.
- **Story 32-15** (Persona reframe + seeding) — supplies the persona/agent catalog a variant overlays. A persona is never a variant; the variant seeder is separate. Hard conceptual prerequisite.
- **Story 32-16** (Per-tenant agent/persona enablement) — the catalog-membership + per-tenant + XOR/index precedent this story mirrors for variant enablement (`Enabled` + implicit-private). Pattern source.
- **Story 32-17** (Custom-agent prompts) — a variant overlays on top of a custom agent's own prompts too (after them); it does not edit them. Boundary cross-reference.
- **Story 32-5** (Call-LLM endpoint + managed execution) — the **consumer** that calls `ResolveActiveVariantAsync` + `ComposeOverlay` at step 4b. This story ships the interface; 32-5 wires it. Primary consumer.
- **Story 32-2** (Agent registry/selection) — the `AgentRoleSelection` `(principal, role)` binding + XOR/index precedent the variant **binding** mirrors; the `/api/agents` policy conventions reused for `/api/style-variants`.
- **Epic 27** (Prompt Store) — the **base** prompt the overlay rides on top of (tenant→system→error). A variant is never an Epic-27 override and never enters the prompt store. Boundary cross-reference + the no-empty-fallback discipline this story preserves.
- **Epic 28** (schema-per-tenant) — the CP-vs-tenant placement decision (these tables are CP-resident, like the catalog they style).

**Consumers (downstream, not blockers):**

- **Story 32-5** — applies the overlay (primary consumer).
- **Story 32-6 / 32-8 / 32-9** (action trail / outcome / usage) — observe the optional `styleVariantId` tag on `AGENT.RUN.*` if present; they do not key on it.

**External:** none new (reuses EF Core, `IEventRepository`, the existing auth/policy stack).

## Testing Strategy

Tests are xUnit under `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/`. Docker-bound suites run via `sg docker -c "dotnet test ..."` (session docker group is stale; see `reference_dotnet_test_docker`). TDD: write the failing test first.

1. **Entity constraints** (`AgentStyleVariantModelTests`): XOR CHECK rejects both/neither principal; unique-nulls-not-distinct rejects a duplicate `(TenantId, UserId, Name)` variant and a duplicate `(TenantId, UserId, Role)` binding (one variant per role).
2. **`ResolveActiveVariantAsync` truth table** (`AgentStyleVariantServiceTests`): bound+enabled+visible → the variant; bound+disabled → `null`; unbound → `null`; bound-but-variant-retired → `null`; cross-tenant binding target → not visible → `null`/404 at bind time.
3. **`ComposeOverlay` purity** (`StyleOverlayComposeTests`, pure unit, no DB): `null` → base unchanged (reference-equal or value-equal); a variant → base is a **prefix** of the result; empty/whitespace `stylePrompt` contributes nothing; knob rendering is stable/deterministic; never mutates or truncates the base (property-style: `result.StartsWith(base)`).
4. **Orthogonality golden test (AC11)**: for a fixed agent + rendered base prompt, **no binding** ⇒ the systemPrompt 32-5 would feed the loop equals `ComposeOverlay(base, null)` byte-for-byte (== the base). Default = none = no behaviour change.
5. **Provider/model/credential-unchanged integration test (AC11)**: run 32-5 (or a 32-5-shaped harness over `IAgentStyleVariantService`) for the same agent with and without a bound variant; assert `providerUsed`/`modelUsed`/`credentialSource` identical; only the system prompt differs (and only additively).
6. **Never weakens base resolution (AC11)**: if the base prompt fails to resolve (tenant→system→error), the presence of a bound variant does NOT rescue/mask it — the run still fails loud; a disabled/missing **variant** is not an error.
7. **CRUD + enablement + bind APIs + events** (`StyleVariantServiceTests`): create/update/delete own-private; enable/disable (own-private implicitly enabled, disable-own ⇒ 409/no-op like 32-16); bind/clear; each write emits exactly one `AGENT.STYLE_VARIANT.*` event tagged `{ variantId, variantName, role?, mode, tenantId|userId }`.
8. **RBAC matrix** (`StyleVariantEndpointsTests`, in-process `WebApplicationFactory`): SaaS `member` → 403 on create/update/delete/enable/bind; member reads (`GET` list/bindings) → 200; `tenant_owner`/`tenant_admin` writes → 200; public-catalog mutation not exposed on the tenant route (asserted absent / 404).
9. **Mode-parameterized principal** (`[Theory]` over `TammaMode.SingleUser`/`SaaS`): variant + binding keyed by `UserId` (single-user) vs `TenantId` (SaaS); the correct column set, the other NULL (XOR holds); events tag the correct principal.
10. **Cross-tenant isolation** (`StyleVariantIsolationTests`): tenant A's private variant/binding never appears in tenant B's list/resolve; A cannot bind/enable B's private variant (404); A's changes never affect B.
11. **Seeded catalog, no default binding** (`AgentStyleVariantSeederTests`): a fresh principal has the shipped public variants available but **zero bindings** (default = no overlay); the seeder is insert-missing-only (rerun does not revert a tenant edit).
12. **CP model contract + DROP-list**: `ControlPlaneDbContextModelTests.Model_Has_ExpectedControlPlaneEntities` includes both entities; `dotnet ef migrations has-pending-model-changes --context ControlPlaneDbContext` → none; a second test-host boot succeeds (DROP-list amendment proven for both tables).

## Estimated Effort

3-4 days (two CP entities + service + CRUD/enable/bind API + the pure `ComposeOverlay` + seeder + the 32-5 wiring seam; no provider/credential/cost surface touched).

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Data/Entities/AgentStyleVariant.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Entities/AgentStyleVariantSelection.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` | Modify (both entities: XOR check + unique-nulls-not-distinct indexes) |
| `apps/tamma-elsa/src/Tamma.Data/ControlPlaneDbContext.cs` | Modify (two DbSets) |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/*_AddAgentStyleVariants.cs` | Create (generated) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/IAgentStyleVariantService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentStyleVariantService.cs` | Create (CRUD, enable, bind, resolve, ComposeOverlay) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentStyleVariantEventTypes.cs` | Create (`AGENT.STYLE_VARIANT.*` constants) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentStyleVariantSeeder.cs` | Create (shipped public variants, insert-missing-only; no default binding) |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/StyleVariantEndpoints.cs` | Create (`/api/style-variants` CRUD + enablement + bindings) |
| `apps/tamma-elsa/src/Tamma.Api/Dtos/Agents/StyleVariantResponse.cs`, `StyleVariantRequest.cs`, `SetVariantBindingRequest.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (DI; `/api/style-variants` routes; **STARTUP-RESET DROP-LIST amend** — both tables) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/AgentStyleVariantServiceTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/StyleOverlayComposeTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/StyleVariantEndpointsTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/StyleVariantIsolationTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/AgentStyleVariantSeederTests.cs` | Create |
| `apps/tamma-elsa/tests/.../Epic28/ControlPlaneDbContextModelTests.cs` | Modify (add both entities to the strict list) |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
2. Searched `.dev/` directory for related spikes, bugs, findings, and decisions (esp. `feedback_resolution_no_empty_fallback`).
3. Reviewed `AgentRoleSelection` + `TenantAgentEnablement` (32-16) + `TammaModelConfiguration` — this story mirrors their XOR/index discipline **exactly** (two entities + the per-role binding).
4. Reviewed `ControlPlaneDbContextModelTests` (the strict `BeEquivalentTo` list) and the `Program.cs` "Wiping Tamma-managed public-schema tables" block — **both must be amended** for the two new tables (AC8, AC9).
5. Read design §3.4 (the 32-12 split: variant ≠ persona) and 32-5's §2.6 composition (the step-4 prompt render this overlay rides on top of) IN FULL, so the overlay stays **additive and after** base resolution.
6. Planned the TDD approach; treat `ComposeOverlay` as a pure function with a base-is-prefix property test, and the orthogonality "default = none" golden test as the load-bearing guard.

### Key design decisions

- **A variant is NOT a persona and NOT a custom agent.** This is the entire point of the §3.4 split. The entity carries no provider/model/credential/base-prompt — it cannot change any of them. It is a thin, optional **voice** layer. Naming, route (`/api/style-variants`, never `/api/personas`), event family (`AGENT.STYLE_VARIANT.*`), and the strict "no provider/model fields" entity shape all enforce the distinction.
- **Additive, after base resolution, never empty-fallback.** The overlay is applied at step **4b**, after Epic-27/custom-prompt resolution (step 4), as a pure suffix. It can only **add** style guidance. The base prompt keeps its tenant→system→**error** contract (32-5 AC3); the variant never rescues, blanks, or replaces it. A missing/disabled variant is silently "no overlay" (optional), but it can never weaken the base resolution.
- **Optional + orthogonal; default = none.** Zero or one variant per `(principal, role)`; an unbound principal sees byte-for-byte the 32-5 output without this story (golden test). Provider/model/credential are identical with and without a variant for the same agent (integration test).
- **Catalog-membership + per-role binding, mirroring 32-16 + 32-2.** Variants are enabled per-tenant (like personas in 32-16) and bound per-`(principal, role)` (like agents in 32-2's `AgentRoleSelection`) — but to at most one variant. Same XOR/unique-nulls-not-distinct discipline; own-private variants implicitly enabled.
- **CP-resident in both modes.** Like `TenantAgentEnablement`, both variant tables are CP-resident (SaaS keyed by `TenantId`, single-user by `UserId`) because they style the CP-resident catalog and are keyed by principal id, not stored per `t_<hex>`. Hence the DROP-list + CP-model-test amendments for **both** tables.
- **Benchmarking stays agent-keyed.** 32-12/32-10 benchmark the persona-agent identity. A variant is a separate optional dimension; this story does **not** add a variant axis to benchmarking. Any future variant A/B rides on the agent-keyed harness — out of scope.
- **Own the primitive, not the call site.** This story ships `ResolveActiveVariantAsync` + `ComposeOverlay`; 32-5 wires step 4b. The boundary is the interface — keeps this story from editing the call-LLM composition.

### Per-mode ownership (mandatory two-scoping-model answer, per CLAUDE.md)

| Question | single-user mode | SaaS mode |
|---|---|---|
| Who owns a style variant + its bindings? | The sole user (`user_id`-keyed CP rows; `tenant_id` NULL). | The tenant (`tenant_id`-keyed CP rows; `user_id` NULL). `tenant_owner`/`tenant_admin` write; `member` read-only. |
| Is there a per-user layer? | N/A — the sole user *is* the principal. | **No.** Members see/use the tenant's variants + bindings; they cannot create/enable/bind (403 on writes) — mirrors "no per-user override layer in SaaS." |
| Where do the variant rows live? | `ControlPlaneDbContext.agent_style_variants` / `…_selections`, keyed by `UserId`. | Same tables, keyed by `TenantId` (CP-resident, not `t_<hex>`). |
| Does a variant change provider/model/credential? | **No** (in either mode). It is a voice overlay only; `credentialSource` is untouched. | **No.** Same. |
| What is the default (no binding)? | No overlay — the rendered prompt is exactly the 32-5 output. | No overlay — identical for every member. |
| Who manages the shipped public variant catalog? | Shipped system variants (read-only to the user; the user enables/binds). | Platform owner (`PlatformOwnerAccess`) — out of scope here; tenants only enable/bind existing ones + author own-private. |
| Where do `AGENT.STYLE_VARIANT.*` events land? | The user's (platform-events) feed; `TenantId == null`, principal = `userId`. | The tenant's event store via tenant-scoped `IEventRepository`; `TenantId` set. |
| Mode source | `ITammaModeProvider` (`TammaMode.cs`) — process-stable. | same |

### Risks and Mitigations

| Risk | Severity | Mitigation |
|------|----------|------------|
| Variant treated/named as a persona → vocabulary regression (the §3.4 mistake) | High | Strict entity shape (no provider/model fields), distinct route (`/api/style-variants`), distinct event family (`AGENT.STYLE_VARIANT.*`); cross-referenced vs 32-15/32-12; story title says "NOT a persona." |
| Overlay replaces/blanks the base prompt → empty-fallback regression | High | `ComposeOverlay` is a pure **suffix**; base-is-prefix property test; null-variant→identity; the base keeps its tenant→system→error contract in 32-5 (the variant never touches step 4). |
| Default behaviour changes when nobody bound a variant | High | Seeder binds nothing; orthogonality golden test asserts byte-for-byte equality vs the no-variant 32-5 output. |
| Variant accidentally changes provider/model/credential/cost | High | Entity carries none of those fields; integration test asserts `providerUsed`/`modelUsed`/`credentialSource` identical with/without a variant. |
| New CP tables break the second test-host boot (`relation already exists`) | High | Amend the `Program.cs` DROP list for **both** tables (AC8); test asserts a second boot succeeds. |
| `ControlPlaneDbContextModelTests` strict list fails after adding the entities | High | Update the `BeEquivalentTo` list in the same PR for both (AC9); known gotcha, not a regression. |
| This story edits the 32-5 composition (overlap) | Medium | Hard boundary: ship the interface + primitives; 32-5 owns step 4b. No `ManagedAgent.RunAsync` edits here. |
| XOR/keying drift from `AgentRoleSelection`/`TenantAgentEnablement` | Medium | Mirror `TammaModelConfiguration` config (XOR check name pattern, unique-nulls-not-distinct); constraint tests (AC13.1). |
| Style fragment used as a prompt-injection vector | Medium | The fragment is appended to the **system** prompt of a run the principal already controls; it is owner/admin-authored, not user content; sanitization remains in 32-5's loop; the overlay adds no tool/credential surface. |

### Success Metrics

- [ ] With no binding, the system prompt 32-5 feeds the loop is **byte-for-byte** the no-variant output (orthogonality golden test green).
- [ ] For the same agent, `providerUsed`/`modelUsed`/`credentialSource` are identical with and without a bound variant.
- [ ] `ComposeOverlay` is provably additive (base is a prefix of the result; null → identity) across the property tests.
- [ ] 100% of variant writes emit exactly one `AGENT.STYLE_VARIANT.*` event tagged with the principal.
- [ ] Second test-host boot succeeds (DROP-list amendment, both tables) and `has-pending-model-changes` → none.

## Related

- Design of record: `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` (§3.4 the 32-12 split — variant ≠ persona; §3.0 reframe; §2.6 composition step 4 the overlay rides on)
- Re-plan / sequence: `docs/superpowers/plans/2026-06-20-epic-32-37-replan.md` (sequence step G)
- Implementation plan: `docs/superpowers/plans/2026-06-21-32-19-agent-style-voice-variants-plan.md`
- Sibling stories: `docs/stories/epic-32/story-32-12/` (persona rewrite — benchmarking stays agent-keyed), `story-32-15/` (persona seeder), `story-32-16/` (per-tenant enablement — the catalog-membership/XOR precedent), `story-32-17/` (custom-agent prompts — base prompts the overlay rides on), `story-32-5/` (call-LLM endpoint — applies the overlay at step 4b), `story-32-2/` (`AgentRoleSelection` binding precedent)
- Reused precedent: `apps/tamma-elsa/src/Tamma.Data/Entities/AgentRoleSelection.cs`, `TenantAgentEnablement` (32-16), `prompt_overrides` (Epic 27)

## Logging Requirements

- **INFO**: variant created/updated/deleted/enabled/disabled (variantId, variantName, mode, tenantId|userId); variant bound/cleared for a role (role, variantId?); call-LLM applied a variant (correlationId, role, variantId) — emitted by 32-5 at step 4b.
- **DEBUG**: `ResolveActiveVariantAsync` branch taken (bound-enabled / bound-disabled→null / unbound→null / retired→null); `ComposeOverlay` invoked (base length, overlay length, no-overlay short-circuit); seeded shipped variants applied (or skipped, row exists).
- **WARN**: bind/enable target no longer a live/visible variant ⇒ ignored/degraded (variantId); disable-own-private rejected (409); cross-tenant target ⇒ 404; a variant style fragment exceeding a sane length cap ⇒ truncated-for-overlay (never blanks the base).
- **ERROR**: DCB event append failure (the write still committed; the append failure is logged, not swallowed); migration / DB write failure; XOR/unique-constraint violation surfaced from EF.
- **Structured context**: include `{ variantId, variantName, role, mode, tenantId, userId, correlationId }` where applicable.
- **Credential safety (LOAD-BEARING)**: a style variant is **credential-agnostic** — it references variant ids/names + tone/verbosity knobs + a style fragment, NEVER a provider key. NEVER log provider credentials; the variant path never touches `credentialSource` resolution (that stays in 32-3, invoked by 32-5). The style fragment is owner/admin-authored config text, not a secret, but is still logged at length-summary granularity (not verbatim) to avoid leaking inadvertently-pasted secrets.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-21 | 1.0.0   | Initial story creation — optional style/voice variant overlay split out of 32-12 per design §3.4 (explicitly NOT a persona); additive, after base resolution, never empty-fallback; CP-resident dual-keyed entities; applied by 32-5 at step 4b. | Claude |
