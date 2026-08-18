# Story 32-17: Custom-Agent Prompts (`ConfigJson.prompts` + resolver prompt-source branch)

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

As a **tenant owner/admin (SaaS) or the sole user (single-user) who needs prompts different from the system defaults**,
I want **to author a self-contained private (custom) agent that carries its OWN prompts alongside its provider+model+config**,
So that **I can customize agent behaviour without editing the shared role/action prompt store** — the sanctioned escape hatch that Epic 27 deliberately does NOT expose as per-tenant persona-prompt editing in SaaS, and so that the managed execution path resolves a custom agent's prompts from the agent itself (fail-loud) while public personas keep resolving prompts from the Epic 27 store.

## Priority

P0 — This is **sequence step D** of the Epic 32 architecture pivot (locked-model **rule 5: custom prompts ⇔ custom agent**). It establishes the only sanctioned way a tenant gets different prompts under the revised model, and it owns the **custom/private branch** of `MaterialiseAsync` that the managed `call-LLM` path (32-5) depends on for prompt resolution. Without it, a tenant has no prompt-customization story at all in SaaS, and `MaterialiseAsync` has only the persona/public branch (32-15) — leaving private agents prompt-less and the no-empty-fallback invariant unenforced.

## Context

Under the revised agent architecture (`docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` §3.2, rule 5), the locked model redefines a **custom agent** as *a private `Agent` whose differentiator is its own prompts*. The private-`Agent` entity already shipped in **32-1** (`Visibility=Private`, owner-keyed, versioned `AgentVersion.ConfigJson` jsonb, the `ck_agents_visibility_ownership` XOR CHECK, and the per-owner partial indexes `IX_agents_private_tenant_name` / `IX_agents_private_user_name`). What 32-1 did **not** give it is a place to carry tenant-authored prompts, and what the revised model forbids is editing the shared Epic 27 role/action prompt store on a per-tenant basis in SaaS (CLAUDE.md: "No per-user override layer in SaaS … per-user personalization on top of tenant prompts is intentionally NOT a feature"). The custom agent **is** that missing prompt layer — a self-contained, owner-scoped artifact.

This story adds the **optional `prompts` block** to the `AgentVersion.ConfigJson` shape (no new entity, no new column — a JSON sub-object inside the existing jsonb column), the **validation invariant** that public personas reject a non-empty `prompts` block (prompt-free by contract, rule 4), and the **custom/private branch** of `AgentResolverService.MaterialiseAsync` that sources the rendered prompt from the agent's own `prompts` instead of the Epic 27 store. Both branches **fail loud, never empty/plain** (`feedback_resolution_no_empty_fallback`).

**Branch ownership is split across three sibling stories and MUST NOT be double-implemented:**

| Concern | Owner | Seam |
|---|---|---|
| `MaterialiseAsync` **persona/public** branch → Epic 27 store `(principal, role, action)` | **32-15** (persona reframe + Epic-27 wiring) | `IPersonaPromptResolver` |
| `MaterialiseAsync` **custom/private** branch → the agent's own embedded `prompts` + the `ConfigJson.prompts` schema + the public-must-be-empty invariant | **THIS story (32-17)** | `ICustomAgentPromptResolver` |
| Registry **enablement gate** in selection (`SelectForRoleAsync`/`ResolveUsableAgentAsync`) | **32-18** (amends 32-2) | `ITenantAgentEnablementReader` |

This story therefore introduces a single, documented `if (agent is custom/private with embedded prompts) → own prompts via this story's `ICustomAgentPromptResolver`; else → delegate to 32-15's `IPersonaPromptResolver`` conditional. 32-15 owns the `else` leg (the `IPersonaPromptResolver` seam by that exact name); this story owns the `if` leg (the parallel `ICustomAgentPromptResolver` seam) and the schema/validation. The branch contract is defined here so the two compose into one `MaterialiseAsync` without either re-implementing the other's leg.

## Acceptance Criteria

1. **`ConfigJson.prompts` schema is defined and documented.** The `AgentVersion.ConfigJson` saved-config shape (32-1) gains an **optional** top-level `prompts` block: `{ provider, model, params, prompts?: { system?: string, byRoleAction?: { "<role>:<action>": string, ... } } }`. The block is **absent by default**; only private/custom agents may carry it. A typed C# model `AgentPromptSet { string? System; IReadOnlyDictionary<string,string>? ByRoleAction; }` (`apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentPromptSet.cs`) deserializes it. `byRoleAction` keys are `"<role>:<action>"` using the Epic 27 `AgentRole`/`AgentAction` wire forms (validated, not free-form).

2. **Public personas reject a non-empty `prompts` block (the prompt-free invariant, rule 4).** `AgentConfigValidator.Validate` (extended from 32-1) is amended so that when the agent being created/published is `Visibility=public`, a `ConfigJson` carrying a **non-empty** `prompts` block (any `system` or any `byRoleAction` entry) is **rejected** with a clear validation error (`PROMPTS_NOT_ALLOWED_ON_PUBLIC`). Public agents may omit `prompts` entirely or carry an empty/null block; a populated one is a hard error → 400, no row written, no event emitted. Private agents may carry a populated `prompts` block (or none).

3. **`prompts` content is validated when present.** When a `prompts` block is non-empty: each `byRoleAction` key parses as `"<role>:<action>"` with `role`/`action` valid per `RolePhaseMap.NormalizeRole` / the Epic 27 `AgentAction` taxonomy (invalid key → reject `PROMPTS_INVALID_KEY`); each template value is a non-empty string after trim (empty/whitespace value → reject `PROMPTS_EMPTY_TEMPLATE`, consistent with no-empty-fallback); prototype-pollution keys (`__proto__`, `constructor`, `prototype`) are rejected (reusing the existing 32-1 guard). Validation runs **before any write** in both the create and publish-version paths.

4. **`MaterialiseAsync` gains the custom/private prompt-source branch as the `ICustomAgentPromptResolver` seam.** `AgentResolverService.MaterialiseAsync` (extended jointly with 32-15) resolves the system/role prompt via a **single documented conditional**: if the resolved agent is **private/custom AND its `ConfigJson.prompts` is non-empty**, the prompt is sourced from the agent's own `AgentPromptSet` via this story's **`ICustomAgentPromptResolver.ResolveAsync(...)`** seam; otherwise control flows to the persona/public branch via 32-15's **`IPersonaPromptResolver`** seam (→ Epic 27 store). This story implements **only** the `if` (custom/private) leg + the `ICustomAgentPromptResolver` seam and the branch selector; it MUST NOT re-implement the Epic 27 persona leg (32-15's `IPersonaPromptResolver` owns it).

5. **Custom prompt resolution order is `byRoleAction[role:action] → system → ERROR` (fail-loud, never empty/plain).** For a custom agent at call time with `(role, action)`: (a) if `prompts.byRoleAction["<role>:<action>"]` exists, use it; (b) else if `prompts.system` exists, use it as the system prompt; (c) else — if the custom branch was entered (non-empty `prompts`) but neither a matching role:action nor a system entry resolves for the requested `(role, action)` — **fail loud** with a typed error (`CUSTOM_PROMPT_UNRESOLVED`), **never** fall back to an empty/plain prompt and **never** silently fall through to the Epic 27 store. (A custom agent with an *empty* `prompts` block does not enter the custom branch at all — it is treated as a persona-style agent and resolves via 32-15; only a *non-empty* `prompts` block commits the agent to the custom branch and its fail-loud contract.)

6. **No schema migration beyond validation/CHECK; no new entity.** A custom agent remains a `Visibility=Private` `Agent`/`AgentVersion` from 32-1. `prompts` lives **inside** the existing `ConfigJson` jsonb column — there is **no new table, no new column**. The only persistence-layer delta is **validation logic** (AC2/AC3) and, optionally, a lightweight DB `CHECK` (or application-level guard) asserting public agents carry no populated `prompts`; the existing `ck_agents_visibility_ownership` CHECK and per-owner partial indexes are **reused unchanged**. If a DB `CHECK` is added it is an additive amendment to the existing `agents`/`agent_versions` config in `TammaModelConfiguration.cs` (single source) on the existing migration snapshot — it does NOT branch the snapshot.

7. **DCB events reflect the prompt source, without leaking template content.** When a managed run resolves a custom prompt, the existing `AGENT.RUN.*` (32-5) / resolution events are tagged `promptSource = "custom-agent"` (vs `"epic27-store"` for the persona branch, owned by 32-15); `agentId`/`version` already identify which custom agent. **Prompt template bodies are NEVER placed in event `Data`/`Tags` or logs** — only the source label and the resolved key (`<role>:<action>`). A `CUSTOM_PROMPT_UNRESOLVED` failure surfaces as a typed failure on the managed-run result (32-5 `FailureCode`), not a thrown bare exception, and emits the standard terminal failed event.

8. **Unit + integration tests** cover: schema deserialization (full block, `system`-only, `byRoleAction`-only, absent); public-with-populated-prompts rejected on create AND publish (AC2); invalid `byRoleAction` key, empty template value, prototype-pollution key rejected (AC3); private-with-prompts accepted; `MaterialiseAsync` custom branch picks `byRoleAction` over `system` over ERROR; `CUSTOM_PROMPT_UNRESOLVED` is fail-loud (no empty fallback, no fall-through to Epic 27); a custom agent with an empty `prompts` block delegates to the persona branch (32-15) and does NOT enter the custom branch; `promptSource` tag is `custom-agent`; no template body appears in any emitted event or log line.

## Technical Design

### Where it lives

```
apps/tamma-elsa/src/Tamma.Api/Services/Agents/
  AgentPromptSet.cs              # NEW — typed model for ConfigJson.prompts { System, ByRoleAction }
  ICustomAgentPromptResolver.cs  # NEW — custom/private prompt seam (byRoleAction -> system -> ERROR, fail-loud)
  CustomAgentPromptResolver.cs   # NEW — impl reading the agent's ConfigJson.prompts
  AgentConfigValidator.cs        # MODIFY (from 32-1) — public-must-be-empty invariant + prompts shape rules
  AgentResolverService.cs        # MODIFY (jointly with 32-15) — custom branch = ICustomAgentPromptResolver; persona branch = 32-15 IPersonaPromptResolver
  IAgentResolverService.cs       # MODIFY (only if a prompt-source enum/return field is surfaced)
  AgentPromptSource.cs           # NEW — enum { Epic27Store, CustomAgent } for the resolved-prompt provenance tag

apps/tamma-elsa/src/Tamma.Data/
  TammaModelConfiguration.cs     # MODIFY (optional) — additive CHECK ck_agents_public_no_prompts (or app-level guard)
  Migrations/ControlPlane/
    <ts>_AddPublicAgentNoPromptsCheck.cs  # NEW (optional) — additive CHECK only, no column/table
```

> No new entity, no `DbSet`, no `ControlPlaneDbContext` change → **no `ControlPlaneDbContextModelTests` strict-list edit and no Program.cs startup-reset DROP-list edit** (those are required only when adding a *table*; this story adds at most a CHECK on the existing `agents` table). If the public-no-prompts guard is enforced purely in `AgentConfigValidator` (recommended, see Risks), there is **no migration at all**.

### The `prompts` block — `ConfigJson` shape (AC1)

```jsonc
// AgentVersion.ConfigJson for a PRIVATE/CUSTOM agent (the new optional "prompts" block):
{
  "provider": "anthropic",
  "model": "claude-sonnet-4-20250514",
  "params": { "temperature": 0.4, "maxTokens": 4096, "tokenBudget": 200000 },
  "tools": ["file-read", "shell-execute"],
  "prompts": {                                   // OPTIONAL — private agents only
    "system": "You are ACME's house implementer. Always cite the ADR.",
    "byRoleAction": {
      "implementer:write-implementation": "Implement per ACME conventions: ...",
      "reviewer:review-pr": "Review against ACME's security checklist: ..."
    }
  }
}

// AgentVersion.ConfigJson for a PUBLIC PERSONA (rule 4 — prompts MUST be absent/empty):
{
  "provider": "anthropic",
  "model": "claude-sonnet-4-20250514",
  "params": { "temperature": 0.3, "maxTokens": 4096 }
  // NO "prompts" key (a populated one => PROMPTS_NOT_ALLOWED_ON_PUBLIC)
}
```

### Typed model (C#)

```csharp
namespace Tamma.Api.Services.Agents;

/// <summary>
/// The optional embedded prompt set carried by a CUSTOM (private) agent inside
/// AgentVersion.ConfigJson["prompts"] (Epic 32 rule 5: custom prompts ⇔ custom agent).
/// Public personas are prompt-free by contract and MUST leave this null/empty —
/// their prompts come from the Epic 27 store (resolved by sibling story 32-15).
/// </summary>
public sealed record AgentPromptSet
{
    /// <summary>Fallback system prompt used when no role:action template matches.</summary>
    public string? System { get; init; }

    /// <summary>Templates keyed by "&lt;role&gt;:&lt;action&gt;" (Epic 27 wire forms).</summary>
    public IReadOnlyDictionary<string, string>? ByRoleAction { get; init; }

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(System) && (ByRoleAction is null || ByRoleAction.Count == 0);
}

/// <summary>Provenance of a resolved agent prompt — emitted as the promptSource tag.</summary>
public enum AgentPromptSource { Epic27Store, CustomAgent }
```

### Validation invariant (AC2/AC3) — extends 32-1's `AgentConfigValidator`

`AgentConfigValidator.Validate(configJson, visibility)` is extended (32-1 introduced the validator; this story adds the `visibility` discriminator and the `prompts` rules — both `CreateAsync`- and `PublishVersionAsync`-backing endpoints call it):

```csharp
// pseudo — additive rules layered onto the existing 32-1 shape validation
var prompts = TryReadPromptSet(configJson);   // null if "prompts" key absent

if (visibility == AgentVisibility.Public && prompts is { IsEmpty: false })
    errors.Add("PROMPTS_NOT_ALLOWED_ON_PUBLIC: public personas are prompt-free (Epic 32 rule 4)");

if (prompts is { IsEmpty: false })
{
    foreach (var kvp in prompts.ByRoleAction ?? Empty)
    {
        if (IsPrototypePollutionKey(kvp.Key)) errors.Add("PROMPTS_PROTO_POLLUTION");          // reuse 32-1 guard
        else if (!TryParseRoleAction(kvp.Key, out _, out _)) errors.Add("PROMPTS_INVALID_KEY"); // RolePhaseMap.NormalizeRole + AgentAction
        if (string.IsNullOrWhiteSpace(kvp.Value)) errors.Add("PROMPTS_EMPTY_TEMPLATE");        // no-empty-fallback
    }
    // a "prompts" object that parses but is wholly empty is allowed (treated as absent) for private agents
}
```

> Reuses the existing 32-1 prototype-pollution / ReDoS / shape guards; this story adds only the `visibility`-conditional rejection and the role:action key + non-empty-template rules.

### `MaterialiseAsync` — the documented conditional branch (AC4/AC5)

The single branch selector. **This story owns the `if` (custom) leg + the `ICustomAgentPromptResolver` seam + the selector; 32-15 owns the `else` (persona/Epic 27) leg via `IPersonaPromptResolver`.** Neither re-implements the other.

```csharp
// NEW seam (this story owns it). The CUSTOM/PRIVATE prompt leg.
public interface ICustomAgentPromptResolver
{
    /// <summary>Resolve a custom (private) agent's prompt from its own ConfigJson.prompts:
    /// byRoleAction["<role>:<action>"] -> system -> ERROR. Fail-loud, NEVER empty/plain,
    /// NEVER fall through to the Epic 27 store.</summary>
    Task<RenderedPrompt> ResolveAsync(Agent agent, string role, string? action, CancellationToken ct);
}
```

```csharp
// AgentResolverService.MaterialiseAsync(...) — prompt-source section (joint with 32-15)
//
//   resolved = base config merged onto DefaultAgentConfig.ForRole(role)  // (existing 32-1/32-2)
//
// PROMPT SOURCE — single documented conditional (Epic 32 §3.2):
var promptSet = (resolved.Visibility == AgentVisibility.Private)
    ? TryReadPromptSet(resolved.ConfigJson)
    : null;

if (promptSet is { IsEmpty: false })
{
    // ── CUSTOM / PRIVATE branch (THIS story 32-17, via ICustomAgentPromptResolver) ──────
    //    byRoleAction["<role>:<action>"] -> system -> ERROR. The resolver fails loud
    //    (CustomPromptUnresolvedException) — NEVER empty/plain, NEVER fall through to Epic 27.
    resolved = resolved with {
        SystemPrompt = (await _customAgentPrompts.ResolveAsync(resolved.Agent, role, action, ct)).Text,
        PromptSource = AgentPromptSource.CustomAgent
    };
}
else
{
    // ── PERSONA / PUBLIC branch (sibling story 32-15, via IPersonaPromptResolver) ───────
    //    persona/public → Epic 27 store (principal, role, action); tenant→system→ERROR.
    //    (Owned by 32-15 — this story does NOT implement this leg.)
    resolved = resolved with {
        SystemPrompt = (await _personaPrompts.ResolveAsync(principal, role, action, ct)).Text,
        PromptSource = AgentPromptSource.Epic27Store
    };  // _personaPrompts is the 32-15 IPersonaPromptResolver seam
}
```

> `_customAgentPrompts` is this story's `ICustomAgentPromptResolver`; `_personaPrompts` is 32-15's `IPersonaPromptResolver`. The selector body (`byRoleAction → system → ERROR`) lives **inside** `ICustomAgentPromptResolver.ResolveAsync`, which throws `CustomPromptUnresolvedException` on no-resolve. That exception (or a typed result, depending on the `RenderedPrompt` contract 32-15 settles on) is caught by the managed `call-LLM` path (32-5) and mapped to `AgentRunResult { Success=false, FailureCode="CUSTOM_PROMPT_UNRESOLVED" }` — it never propagates as a bare exception out of a managed run (AC7).

### Provenance tagging (AC7)

The resolved config carries `PromptSource ∈ { CustomAgent, Epic27Store }`. The managed run (32-5) tags its `AGENT.RUN.*` / resolution events `promptSource = "custom-agent" | "epic27-store"` and the resolved key `<role>:<action>`. **Template bodies never enter `Tags`, `Data`, or logs** — only the source label and the key. (Consistent with 32-1's "never log raw `ConfigJson` if it could carry sensitive content".)

### What this story does NOT do (boundary, for unambiguous composition)

- It does **not** implement the persona/public → Epic 27 prompt branch (that is **32-15**); it only defines the branch selector and calls the persona leg via 32-15's `IPersonaPromptResolver` seam.
- It does **not** add the registry **enablement gate** to selection (`SelectForRoleAsync`/`ResolveUsableAgentAsync`) — that is **32-18**.
- It does **not** add a new entity, table, or column, and does **not** touch `TenantAgentEnablement` (32-16).
- It does **not** change the credential resolution (32-3), the gate (32-4), or the cost/metering (32-9/34) — only the prompt-source leg of agent resolution.

## Dependencies

**Internal:**

- **Story 32-1** (Agent entity model & versioned saved config) — provides `Agent`/`AgentVersion`, the `ConfigJson` jsonb column, `AgentConfigValidator`, the `ck_agents_visibility_ownership` CHECK, the per-owner partial indexes, and `Visibility`. **Hard prerequisite** — this story extends the validator and the config shape it owns.
- **Story 32-15** (Persona reframe + seeding + `MaterialiseAsync` persona/Epic-27 branch) — owns the **persona/public** leg of the same `MaterialiseAsync` conditional and the **`IPersonaPromptResolver`** seam this story's selector calls. **Hard prerequisite / co-author of `MaterialiseAsync`** — the two stories must land the branch together; sequence 32-15 (step B) before this (step D).
- **Epic 27** (prompt/convention store, `AgentRole`/`AgentAction` taxonomy) — provides `RolePhaseMap.NormalizeRole` + the `AgentAction` wire forms used to validate `byRoleAction` keys; the persona branch (via 32-15) resolves against its store. The custom branch deliberately does NOT write to or read from the Epic 27 store (it is the escape hatch *around* it).
- **Story 32-2** (Agent registry/resolution) — owns `AgentResolverService`/`IAgentResolverService` and `MaterialiseAsync`; this story modifies it (custom-branch leg). 
- **Story 32-5** (Managed execution / `call-LLM`) — **consumer**: maps `CUSTOM_PROMPT_UNRESOLVED` to a typed `FailureCode` and emits the `promptSource`-tagged events. Not a blocker for authoring; the resolved-config contract (`PromptSource`, fail-loud) is what 32-5 consumes.

**Consumers (downstream, not blockers):**

- **Story 32-5** — uses the custom-resolved `SystemPrompt` + `PromptSource` in the managed run.
- **Story 32-18** (enablement gate) — composes with the same `MaterialiseAsync`; orthogonal to the prompt-source leg.

**External:** none new.

## Testing Strategy

1. **Schema deserialization** (`AgentPromptSetTests`): `prompts` absent → null; full block → `System` + `ByRoleAction` populated; `system`-only and `byRoleAction`-only round-trip; a `prompts: {}` object → `IsEmpty == true`.
2. **Public-must-be-empty, create path** (`AgentConfigValidatorTests`): `Visibility=public` + populated `prompts` (any `system` or any `byRoleAction`) → rejected `PROMPTS_NOT_ALLOWED_ON_PUBLIC`; no row, no event. Public + absent/empty `prompts` → accepted.
3. **Public-must-be-empty, publish-version path**: same rejection when publishing a new version of an existing public persona (AC2 applies to both endpoints).
4. **Private accepts prompts**: `Visibility=private` + populated `prompts` → accepted; `Visibility=private` + absent `prompts` → accepted (a custom agent may carry no prompts and behave persona-like).
5. **`prompts` content validation** (AC3): invalid `byRoleAction` key (`"bogus:nope"`, missing colon, unknown action) → `PROMPTS_INVALID_KEY`; empty/whitespace template value → `PROMPTS_EMPTY_TEMPLATE`; `__proto__`/`constructor`/`prototype` key → `PROMPTS_PROTO_POLLUTION`.
6. **`MaterialiseAsync` custom branch — selection order** (`AgentResolverServicePromptBranchTests`): custom agent with both → `byRoleAction[role:action]` wins; with only `system` and a non-matching `(role, action)` → `system` used; with neither matching → `CUSTOM_PROMPT_UNRESOLVED` (fail-loud), and the Epic 27 store is **never** consulted (assert the `IPersonaPromptResolver` seam is NOT invoked).
7. **Empty-prompts delegates to persona branch**: custom/private agent with an empty `prompts` block → custom branch NOT entered → 32-15's `IPersonaPromptResolver.ResolveAsync` is invoked; `PromptSource == Epic27Store`.
8. **No-empty-fallback invariant**: assert no code path returns an empty/plain prompt for a custom agent — `CUSTOM_PROMPT_UNRESOLVED` is the only outcome when nothing resolves (mirrors `feedback_resolution_no_empty_fallback`).
9. **Provenance tag / no-leak** (AC7): a resolved custom prompt sets `PromptSource=CustomAgent`; the managed run's emitted event carries `promptSource="custom-agent"` and the key `"<role>:<action>"` but **no template body** appears in any event `Data`/`Tags` or log line (assert via a fake `IEventRepository` + a captured-log assertion).
10. **(Optional, if a DB CHECK is added)** integration: inserting a public `agent_versions` row with populated `prompts` violates `ck_agents_public_no_prompts` against a real Postgres fixture; `has-pending-model-changes` reports none after the additive CHECK migration.

Docker-bound C# suites run via `sg docker -c "dotnet test apps/tamma-elsa/..."` (session docker group is stale; plain `dotnet build` needs no wrapper).

## Estimated Effort

2-3 days

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentPromptSet.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentPromptSource.cs` | Create (enum) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/ICustomAgentPromptResolver.cs` | Create (custom/private prompt seam — `byRoleAction → system → ERROR`, fail-loud) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/CustomAgentPromptResolver.cs` | Create (impl reading the agent's `ConfigJson.prompts`) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/CustomPromptUnresolvedException.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentConfigValidator.cs` | Modify (public-must-be-empty + prompts shape rules) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentResolverService.cs` | Modify (custom branch = `ICustomAgentPromptResolver`; persona branch = 32-15 `IPersonaPromptResolver` — joint with 32-15) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/IAgentResolverService.cs` | Modify (surface `PromptSource` on resolved config, if not already from 32-15) |
| `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` | Modify (optional additive CHECK `ck_agents_public_no_prompts`) |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/ControlPlane/<ts>_AddPublicAgentNoPromptsCheck.cs` (+ `.Designer.cs`, snapshot) | Create (optional — CHECK only, no column/table) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/AgentPromptSetTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/AgentConfigValidatorPromptsTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/AgentResolverServicePromptBranchTests.cs` | Create |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
2. Searched `.dev/` for related spikes, bugs, findings, decisions (especially `feedback_resolution_no_empty_fallback`)
3. Read the design of record §3.0, §3.1, §3.2 (`docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md`) and the companion 32-15 story (the co-author of `MaterialiseAsync`)
4. Reviewed 32-1's `AgentConfigValidator`, `AgentVersion.ConfigJson` shape, the `ck_agents_visibility_ownership` CHECK, and the per-owner partial indexes — this story extends them, it does not redefine them
5. Confirmed with the 32-15 author exactly where the `MaterialiseAsync` conditional lives and the shape of the `IPersonaPromptResolver` seam (so the two legs compose without a merge conflict or a double-implemented branch)
6. Planned the TDD approach (Red-Green-Refactor)

### Key Design Decisions

- **Custom prompts ⇔ custom agent (rule 5).** Tenants never edit the shared Epic 27 store in SaaS; they author a private agent that carries its own prompts. This keeps audit/compliance simple (one artifact, owner-scoped, versioned) and avoids "one user's customization broke an agent run" support cases (CLAUDE.md Prompt Store rationale).
- **The `prompts` block is a JSON sub-object, not a new entity.** A custom agent is *just* a `Visibility=Private` `Agent` (32-1). The whole schema delta is an optional key inside the existing `ConfigJson` jsonb + a validation rule — deliberately minimal, no migration anxiety, reuses every 32-1 invariant.
- **One documented conditional, two owners.** `MaterialiseAsync` has exactly one prompt-source `if/else`. This story owns the `if` (custom) leg + the selector; 32-15 owns the `else` (persona/Epic 27) leg. Documenting the boundary here is what lets the two stories land without either re-implementing `MaterialiseAsync`.
- **Fail loud, never empty/plain (both branches).** A non-empty `prompts` block commits the agent to the custom branch and its `byRoleAction → system → ERROR` contract — it must never silently fall through to the Epic 27 store, and never resolve to an empty prompt. An *empty* `prompts` block is treated as absent and delegates to the persona branch. This is the `feedback_resolution_no_empty_fallback` rule applied to the new escape hatch.
- **Prompt-free public personas (rule 4).** The public-must-be-empty invariant is the structural enforcement that personas have no custom prompts — without it, a public persona could smuggle prompts and break the "personas resolve from Epic 27" guarantee.
- **Validation, not a column.** Enforcing public-no-prompts in `AgentConfigValidator` (recommended) keeps the migration footprint at zero. A DB `CHECK` is offered as belt-and-suspenders but is optional; if added, it is an additive CHECK on the existing table, never a snapshot branch.

### Per-mode ownership (mandatory two-scoping-model answer, per CLAUDE.md)

| Question | single-user mode | SaaS mode |
|---|---|---|
| Who may author a custom agent (and thus custom prompts)? | The sole user (the principal). The private `Agent` is keyed `OwnerUserId` (32-1). | The `tenant_owner`/`tenant_admin`. The private `Agent` is keyed `OwnerTenantId`. A **member** cannot create/publish a custom agent → 403 (mirrors 32-1 RBAC + Prompt Store RBAC). |
| Whose `ConfigJson.prompts` does a run use? | The user's own custom agent's embedded prompts (when a populated `prompts` block is present and the run resolves it). | The tenant's own custom agent's embedded prompts. **No per-user prompt layer** — members run the tenant's custom agents as-authored, no personal override (CLAUDE.md: per-user personalization is intentionally NOT a feature). |
| Where do public personas get prompts? | Epic 27 store keyed `(userId, role, action)` via the 32-15 persona branch. | Epic 27 store keyed `(tenantId, role, action)` via the 32-15 persona branch. Custom prompts are the only per-tenant prompt customization; the shared store is not per-tenant editable in SaaS. |
| What is the prompt-source provenance? | `PromptSource=CustomAgent` for a resolved custom prompt; `Epic27Store` for a persona. | Same. Tagged on `AGENT.RUN.*`; performance/prompt-source data is tenant-scoped, never cross-tenant. |
| Mode source | `ITammaModeProvider` (`TammaMode.cs`) — process-stable; also drives the 32-1 owner-column derivation. | same |

### Risks and Mitigations

| Risk | Severity | Mitigation |
|------|----------|------------|
| `MaterialiseAsync` branch double-implemented by 32-15 and 32-17 (merge conflict / two `if`s) | High | This story documents the single conditional + the exact leg ownership; sequence 32-15 (B) before 32-17 (D); co-author the one `if/else` against the `IPersonaPromptResolver` (32-15) + `ICustomAgentPromptResolver` (32-17) seams; a test asserts the custom branch never calls the persona seam and vice-versa. |
| Custom agent silently falls through to Epic 27 / empty prompt (no-empty-fallback violation) | High | A non-empty `prompts` block commits to the custom branch; `CUSTOM_PROMPT_UNRESOLVED` is the only no-resolve outcome; explicit test asserts the persona seam is NOT consulted and no empty/plain prompt is returned. |
| Public persona smuggles prompts → breaks "personas resolve from Epic 27" | High | `PROMPTS_NOT_ALLOWED_ON_PUBLIC` rejected at create AND publish; optional DB `CHECK` as backstop; tested on both endpoints. |
| Prompt template body leaks into events/logs | Medium | Only `promptSource` + the `<role>:<action>` key are tagged/logged; a test asserts no body appears in event `Data`/`Tags` or captured logs (extends 32-1's no-raw-ConfigJson rule). |
| Optional DB CHECK adds a migration that branches the snapshot | Low | Prefer validator-only enforcement (zero migration). If a CHECK is added it is an additive amendment to the existing `agents`/`agent_versions` config on the single snapshot — never a baseline/branch edit; `has-pending-model-changes` must report none. |
| `byRoleAction` key taxonomy drifts from Epic 27 | Medium | Validate keys via the same `RolePhaseMap.NormalizeRole` / `AgentAction` taxonomy Epic 27 uses; reuse 32-1's role-normalization path on write. |

### Success Metrics

- [ ] A tenant can author a private custom agent with prompts and have a managed run resolve them — without any edit to the shared Epic 27 prompt store.
- [ ] 100% of public-persona create/publish attempts carrying a populated `prompts` block are rejected.
- [ ] `MaterialiseAsync` has exactly one prompt-source conditional (grep), with the custom leg owned here (`ICustomAgentPromptResolver`) and the persona leg owned by 32-15 (`IPersonaPromptResolver`) — no duplicate branch.
- [ ] Zero custom-prompt resolutions fall back to empty/plain (every no-resolve is a typed `CUSTOM_PROMPT_UNRESOLVED`).

## Related

- Design of record: `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` (§3.0, §3.1, §3.2 — rule 5)
- Re-plan / story disposition + sequence: `docs/superpowers/plans/2026-06-20-epic-32-37-replan.md`
- Implementation plan: `docs/superpowers/plans/2026-06-21-32-17-custom-agent-prompts-plan.md`
- Sibling stories: `docs/stories/epic-32/story-32-1/` (Agent entity + validator + CHECK/indexes — extended here), 32-15 (persona/Epic-27 branch — co-author of `MaterialiseAsync`), 32-16 (per-tenant enablement), 32-18 (registry enablement gate), 32-5 (managed `call-LLM` consumer)
- Resolution discipline: `.dev/findings/feedback_resolution_no_empty_fallback` (tenant→system→ERROR; NEVER empty/plain)

## Logging Requirements

- **INFO**: custom prompt resolved for a managed run (`agentId, version, role, action, promptSource="custom-agent"`); public-no-prompts validation rejected on create/publish (`agentId|name, visibility`).
- **DEBUG**: prompt-source branch selected (`custom-agent` vs `epic27-store`), `byRoleAction` key matched vs `system` fallback used (key only, never the template body).
- **WARN**: `CUSTOM_PROMPT_UNRESOLVED` (a non-empty `prompts` block resolved neither `<role>:<action>` nor `system` — `agentId, role, action`); `prompts` validation rejections (`PROMPTS_NOT_ALLOWED_ON_PUBLIC`, `PROMPTS_INVALID_KEY`, `PROMPTS_EMPTY_TEMPLATE`, `PROMPTS_PROTO_POLLUTION`) with the offending key, **never** the template body.
- **ERROR**: unexpected deserialization failure of a `prompts` block already validated at write time (a should-not-happen invariant breach).
- **Structured context**: include `{ agentId, version, role, action, visibility, promptSource }` where applicable.
- **Credential / content safety**: **NEVER** log a prompt template body, the full `ConfigJson`, or any `byRoleAction` value — only the source label and the `<role>:<action>` key. Prompts may carry tenant-proprietary instructions; treat them as sensitive content (extends 32-1's no-raw-ConfigJson rule). No credentials are involved in this story, but the same redaction discipline applies to prompt bodies.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-21 | 1.0.0   | Initial story creation | Claude |
| 2026-06-21 | 1.0.1   | Cross-spec reconciliation (C1): the custom/private prompt leg is now an explicit parallel seam `ICustomAgentPromptResolver.ResolveAsync(agent, role, action?, ct)` (was the unnamed `if` leg); the persona-leg delegation points at 32-15's `IPersonaPromptResolver` by that exact name (replacing the phantom `_personaPromptBranch`). | Claude |
