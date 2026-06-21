# Story 32-18: Agent Registry Enablement Gate + Epic-27 Prompt Source (amends 32-2)

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)

## User Story

As a **tenant owner/admin (SaaS) or self-hosted user (single-user)**,
I want the agent registry to select and resolve only the personas/agents my tenant has **enabled**, and to pull a persona's system/role prompt from the **Epic 27 prompt store** keyed `(principal, role, action)` instead of from the agent's config,
So that the shipped 32-2 resolution chain enforces per-tenant enablement (a public persona nobody enabled is not silently usable), the persona reframe (cross-role named personas from 32-15) actually drives prompt selection, and BYOK∘persona credential resolution at call time stays clean — all without forking the registry/resolver or adding a new table.

## Priority

P0 — This is sequence step **E** of the Epic-32 architecture pivot (re-plan §4). The shipped 32-2 `CanUse()` returns `true` for **any** public agent, so the per-tenant enablement layer added by 32-16 is inert until the registry/resolver consume it. Likewise, 32-15's persona reframe makes `Agent.Role` nullable and prompt-free, so `MaterialiseAsync` must pull prompts from Epic 27 — otherwise personas resolve with no prompt. Without this story the call-LLM endpoint (32-5) would resolve un-enabled personas and render empty prompts. It is a hard prerequisite for **F** (the call-LLM lynchpin).

## Context

Story 32-2 (shipped on `feat/exec-wave-02`) built the registry/resolution/RBAC API: a four-branch `ResolveForRoleAsync` precedence chain (tenant-private selection → tenant-public selection → system-default public → fail-loud), `GetSystemDefaultPublicAsync(role)` that matched `Agent.Role == role`, a `CanUse()` predicate that admitted **any** public agent, and a `MaterialiseAsync` that sourced the system prompt from the agent's pinned `ConfigJson`. That was correct under the **old** model (per-role `tamma-<role>` public agents, no enablement layer, prompts baked into agent config).

The revised architecture (`docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` §3) changes three things the registry/resolver must consume:

1. **Personas are cross-role named agents** (32-15): `Agent.Role` is nullable for public personas; `GetSystemDefaultPublicAsync` can no longer find "the public agent whose `Role == role`" — it must return the tenant's **enabled default persona** regardless of role.
2. **Enablement is per-tenant** (32-16): the new `TenantAgentEnablement` entity records which public personas a tenant exposes. The usable set tightens from `public ∪ own-private` to **`enabled(public) ∪ own-private`**.
3. **Personas are prompt-free** (§3.1/§3.5): a persona's system/role prompt comes from the **Epic 27** store keyed `(principal, role, action)`; only **custom (private) agents** carry their own prompts (that branch is owned by 32-17).

This story owns the **registry/resolver consumption** of those three changes. It does **not** own the `TenantAgentEnablement` entity/API/events (32-16), the persona seeder / `Agent.Role`-nullable migration / `ConfigJson.prompts` schema (32-15/32-17), nor the credential resolver (32-3). It amends the **existing** 32-2 services in place — no new table, no new migration snapshot branch.

> **Boundary discipline (avoid double-implementation).** The `ITenantAgentEnablementReader` read seam (`IsEnabledForPrincipalAsync` / `ListEnabledPublicAgentIdsAsync` / `GetEnabledDefaultPersonaIdAsync`), the enable/disable endpoints, and `AGENT.ENABLED/DISABLED.SUCCESS` events are **32-16**. The persona seeder, the nullable-`Role` migration, and the **`IPersonaPromptResolver`** seam (the Epic-27 persona-prompt resolution body) are **32-15**. The custom-agent (`ConfigJson.prompts`) prompt branch — the **`ICustomAgentPromptResolver`** seam — is **32-17**. **This story (32-18)** applies the enablement gate inside selection/resolution, returns the `enabled ∪ own-private` visible set, rewrites the enabled-default lookup (consuming `GetEnabledDefaultPersonaIdAsync`), wires the `MaterialiseAsync` prompt-source **PRECEDENCE/dispatch** (public → `IPersonaPromptResolver`, private → `ICustomAgentPromptResolver`) **without re-inlining either resolution body**, and adds `AGENT.SELECT.NOT_ENABLED`.

## Acceptance Criteria

1. **Enablement gate in selection (`SelectForRoleAsync`).** `IAgentRegistryService.SelectForRoleAsync(role, agentId)` rejects a target that is a **public persona NOT enabled for the principal**: it emits `AGENT.SELECT.NOT_ENABLED` and returns/throws a typed failure mapping to **409 Conflict** (`agent_not_enabled`) — selecting a disabled persona is a state conflict, not a missing resource. A target that is the caller's **own private/custom agent** is implicitly enabled (you authored it) and is accepted. A cross-tenant private target still resolves to 404 (existing 32-2 behaviour, unchanged).
2. **Enablement gate in resolution (`ResolveUsableAgentAsync` / the resolve precedence).** The resolver's "is this selected agent usable?" check changes from `IsPublic` (today: any public ⇒ usable) to **`(IsPublic && IsEnabledForPrincipalAsync) || IsOwnPrivate`** using 32-16's `ITenantAgentEnablementReader.IsEnabledForPrincipalAsync(agentId, principal, ct)` primitive (async). A selection pointing at a persona the tenant has since **disabled** degrades to the next precedence branch (system-default enabled persona), never resolves the disabled persona, and logs a WARN. The `feedback_resolution_no_empty_fallback` rule is preserved end-to-end.
3. **`CanUseAsync()` rewritten.** The 32-2 `CanUse(agent, principal)` predicate becomes the async `CanUseAsync(agent, principal, ct)` ⇒ `agent.IsPublic ? await _enablement.IsEnabledForPrincipalAsync(agent.Id, principal, ct) : agent.IsOwnedBy(principal)`. No code path treats a public persona as usable solely because it is public.
4. **Visible/enabled listing.** `ListAsync` (the existing `/api/agents` list) — or a new `ListEnabledAsync` it delegates to — returns **`enabled(public) ∪ own-private`** for the principal, NOT all public ∪ own-private. The filter set (`?role=&visibility=&status=`) still applies on top. A `?includeDisabled=true` query (owner/admin only) MAY return the full catalog with an `enabled` flag per row so admins can see what they could enable; members never see disabled public personas.
5. **Enabled-default lookup rewritten (`GetSystemDefaultPublicAsync`).** It no longer matches `Agent.Role == role`. It returns the tenant's configured **default persona** (the platform `DefaultPersonaName`, e.g. `claude`) **only if it is enabled for the principal**; if the configured default is not enabled, it returns the principal's enabled default per the tenant's enablement (32-16's notion of "the enabled default"). If the tenant has enabled **no** persona and has no own-private agent for the role, resolution **fails loud** — emits `AGENT.RESOLVE.FAILED` (existing) and throws `TammaError("AGENT.RESOLVE.NO_ENABLED_DEFAULT", severity: High)`. The old per-role ">1 public agent" ambiguity warning is deleted. **No empty/plain fallback.**
6. **`MaterialiseAsync` prompt-source PRECEDENCE/dispatch only (no inline persona resolve).** This story wires the `MaterialiseAsync` prompt-source PRECEDENCE to dispatch **public personas → `IPersonaPromptResolver` (32-15)** and **private/custom agents → `ICustomAgentPromptResolver` (32-17)**; **this story adds no prompt-resolution body of its own.** It does NOT re-inline the Epic-27 persona resolve (`IPromptStoreService.ResolveAsync`) — that body lives inside 32-15's `IPersonaPromptResolver`. When the resolved agent is a **public persona** (`IsPublic`), `MaterialiseAsync` calls `_personaPrompts.ResolveAsync(principal, role, action, ct)` (the 32-15 seam, which reads the Epic 27 store `(principal, role, action)` tenant→system→error). When the resolved agent is **private/custom**, it calls `_customAgentPrompts.ResolveAsync(agent, role, action, ct)` (the 32-17 seam). Both seams are fail-loud internally (`PROMPT_UNRESOLVED` / `CUSTOM_PROMPT_UNRESOLVED`); **never fall back to empty/plain**. The persona's `ConfigJson` supplies provider/model/params/tools but **MUST NOT** supply the prompt (personas are prompt-free by contract).
7. **Action key plumbed through resolution.** `ResolveForRoleAsync` / `ResolveForRoleAndPhaseAsync` accept (or derive) the Epic-27 `action` key alongside `role`, so the persona prompt is resolved at `(principal, role, action)` — matching the `LlmCallRequest.action` field the call-LLM endpoint (32-5) passes. When `action` is absent, the action-default Epic-27 branch is used (still tenant→system→error, never empty).
8. **BYOK∘persona resolve order documented + tied to the resolver.** The resolved `ResolvedAgentConfig` carries `Provider` + `Model` (from the persona, 32-15). The **credential** is resolved separately and later by 32-3's `IProviderCredentialResolver.ResolveAsync(tenantId, resolvedConfig.Provider)` (BYOK→platform) **inside the call-LLM endpoint** — NOT in the registry/resolver. `credentialSource` is decided by `(tenant, persona.provider)`, persona-independent; the registry/resolver never touch a key. This story documents the end-to-end resolve order (enablement gate → resolve config → Epic-27 prompt → 32-3 credential) so 32-5 composes it correctly, and asserts (test) that no registry/resolver code path resolves or logs a credential.
9. **DCB events.** New: `AGENT.SELECT.NOT_ENABLED` (tags `{ agentId, personaName, role, mode, tenantId|userId }`) on a blocked selection. Existing `AGENT.SELECTED_FOR_ROLE.SUCCESS` / `AGENT.RESOLVE.FAILED` keep firing. A resolution that degrades past a now-disabled selection emits an `AGENT.RESOLVE.DEGRADED` (tags `{ role, staleAgentId, fallbackSource, mode }`) WARN-level event so the disablement is auditable. `AGENT.ENABLED/DISABLED.SUCCESS` are owned by 32-16 (NOT emitted here).
10. **Per-mode RBAC + principal unchanged from 32-2.** Reads (list/resolve) allowed for any member. Role selection requires `tenant_owner`/`tenant_admin` (member → 403). Principal is `tenant_id` in SaaS, `user_id` in single-user, sourced from `ITammaModeProvider` + `ITenantContext`/`ClaimsPrincipal`. The enablement gate is evaluated against the **same principal** — no per-user enablement layer (matches CLAUDE.md and 32-16).
11. **No-regression + clean migration graph.** This story **adds no table and no migration**. The 32-2/32-15/32-16 entities and migrations are reused as-is. The legacy `/api/v1/agents/*` JSONB path and `AgentResolverService.ResolveAsync(tenantId, role)` stay byte-for-byte working. `dotnet ef migrations has-pending-model-changes` reports **none** (this story makes service/resolver edits only). The full `dotnet test` suite stays green.
12. **Unit + integration tests** cover: the enablement gate on selection (enabled persona accepted; disabled persona → 409 + `AGENT.SELECT.NOT_ENABLED`; own-private accepted; cross-tenant private → 404); resolution degrading past a disabled selection to the enabled default; `GetSystemDefaultPublicAsync` returning the enabled default (via the configured default, else `GetEnabledDefaultPersonaIdAsync`) and failing loud when nothing is enabled (`AGENT.RESOLVE.NO_ENABLED_DEFAULT`); persona prompt **dispatched to `IPersonaPromptResolver`** (and the custom branch to `ICustomAgentPromptResolver`) — asserting this story calls neither resolution body directly nor `IPromptStoreService`; the no-empty-fallback propagation; the mode-parameterized principal; and an assertion that no resolver/registry path resolves a credential.

## Technical Design

### Architectural placement (per the Epic 32 design of record)

Per `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` §3.3 ("Changes to 32-2") + §3.1 (prompt source) + §3.5 (BYOK∘persona at call time):

- The **entity + enable/disable API + events** for `TenantAgentEnablement` are **32-16**; this story injects + consumes its `ITenantAgentEnablementReader` read seam (`IsEnabledForPrincipalAsync` / `ListEnabledPublicAgentIdsAsync` / `GetEnabledDefaultPersonaIdAsync`).
- The **persona seeder**, `Agent.Role`-nullable migration, the public unique index `(Name) WHERE Public`, the explicit `model` in `ConfigJson`, and the **`IPersonaPromptResolver`** seam (the persona/Epic-27 prompt resolution body) are **32-15**.
- The **custom-agent prompt branch** (`ConfigJson.prompts` + the **`ICustomAgentPromptResolver`** seam) is **32-17**.
- The **credential resolution** (BYOK→platform, `credentialSource`) is **32-3**, invoked by the call-LLM endpoint (32-5), never by the registry/resolver.

This story (32-18) edits the **existing** 32-2 services — `AgentRegistryService`, `AgentResolverService` — plus a small event-types addition. No new entity, no new table, no new migration.

### What changes in the registry (`AgentRegistryService`)

```csharp
// Tamma.Api/Services/Agents/AgentRegistryService.cs — MODIFY

// Injected (NEW): the 32-16 enablement read seam. Interface owned by 32-16.
private readonly ITenantAgentEnablementReader _enablement;   // 32-16

/// <summary>
/// The usability predicate. CHANGED from "any public agent is usable" to
/// "public personas are usable only when enabled for the principal;
/// own-private agents are always usable (you authored them)".
/// </summary>
private async Task<bool> CanUseAsync(Agent agent, Principal principal, CancellationToken ct)
    => agent.IsPublic
        ? await _enablement.IsEnabledForPrincipalAsync(agent.Id, principal, ct)   // 32-16 (async)
        : agent.IsOwnedBy(principal);                                            // own-private => implicit

public async Task SelectForRoleAsync(string role, Guid agentId, CancellationToken ct = default)
{
    var principal = _principal.Resolve();
    var agent = await ResolveTargetAsync(agentId, principal, ct);  // public CP ∪ own private; cross-tenant => null
    if (agent is null)
        return NotFound(agentId);                                  // 404 — existing 32-2 behaviour

    if (agent.IsPublic && !await _enablement.IsEnabledForPrincipalAsync(agent.Id, principal, ct))   // NEW gate (async)
    {
        await _events.AppendAsync(new DomainEvent {
            Type = AgentEventTypes.SelectNotEnabled,               // "AGENT.SELECT.NOT_ENABLED"
            Tags = Json(new { agentId, personaName = agent.Name, role, mode = principal.Mode, tenantId = principal.TenantId, userId = principal.UserId }),
        }, ct);
        throw new TammaError("AGENT.SELECT.NOT_ENABLED",
            $"Persona '{agent.Name}' is not enabled for this {principal.ModeLabel}.",
            severity: Medium, retryable: false);                   // → 409 agent_not_enabled at endpoint
    }
    // ... existing upsert + AGENT.SELECTED_FOR_ROLE.SUCCESS ...
}

/// <summary>enabled(public) ∪ own-private (was: public ∪ own-private).</summary>
public async Task<IReadOnlyList<AgentSummary>> ListAsync(AgentListFilter filter, CancellationToken ct = default)
{
    var principal = _principal.Resolve();
    var ownPrivate = await ReadOwnPrivateAsync(principal, filter, ct);          // tenant schema
    var publicAgents = await ReadPublicAsync(filter, ct);                       // CP
    // batch the enabled-public set in one read (avoids per-row async calls)
    var enabledIds = (await _enablement.ListEnabledPublicAgentIdsAsync(principal, ct)).ToHashSet();
    var enabledPublic = publicAgents.Where(a => enabledIds.Contains(a.Id));
    var rows = enabledPublic.Concat(ownPrivate);
    // ?includeDisabled=true (owner/admin only) returns the full catalog with an `Enabled` flag.
    if (filter.IncludeDisabled && _principal.IsTenantAdminOrOwner())
        rows = publicAgents.Select(a => a.WithEnabled(enabledIds.Contains(a.Id))).Concat(ownPrivate);
    return ApplyFilters(rows, filter).ToList();
}

/// <summary>
/// The tenant's ENABLED default persona — was: the public agent whose Role==role.
/// Personas are cross-role (Agent.Role is NULL, 32-15), so role is no longer the key.
/// </summary>
public async Task<Agent?> GetSystemDefaultPublicAsync(string role, CancellationToken ct = default)
{
    var principal = _principal.Resolve();
    // 1. the platform-configured default persona, IF the tenant enabled it
    var configuredDefault = await ReadPersonaByNameAsync(_options.DefaultPersonaName, ct);   // e.g. "claude"
    if (configuredDefault is not null && await _enablement.IsEnabledForPrincipalAsync(configuredDefault.Id, principal, ct))
        return configuredDefault;
    // 2. the principal's enabled default per 32-16 (its notion of "the enabled default persona")
    var enabledDefaultId = await _enablement.GetEnabledDefaultPersonaIdAsync(principal, ct);  // 32-16 (defined there, consumed here)
    if (enabledDefaultId is { } id)
        return await ReadPersonaByIdAsync(id, ct);
    // 3. nothing enabled => caller fails loud (NO empty fallback). Returns null; resolver throws.
    return null;
}
```

> `Principal`, `_principal.Resolve()`, `ResolveTargetAsync`, and the `IsOwnedBy`/`IsOwnExpr` helpers are the 32-2 shapes (renamed here for clarity). `ITenantAgentEnablementReader` (`IsEnabledForPrincipalAsync`, `ListEnabledPublicAgentIdsAsync`, `GetEnabledDefaultPersonaIdAsync` — all async, explicit `Principal` arg) is the **read seam owned by 32-16**; this story **injects and consumes** it, does not define the entity or the primitives. `GetEnabledDefaultPersonaIdAsync` is **defined in 32-16** and matches the signature 32-16 ships (open question resolved — see Dev Notes).

### What changes in the resolver (`AgentResolverService`)

```csharp
// Tamma.Api/Services/Agents/AgentResolverService.cs — MODIFY

public async Task<ResolvedAgentConfig> ResolveForRoleAsync(
    string role, string? action = null, CancellationToken ct = default)
{
    if (!RolePhaseMap.ValidRoles.Contains(role))
        throw new ArgumentException($"Unknown role '{role}'");

    var principal = _principal.Resolve();
    var selection = await _registry.GetRoleSelectionsAsync(ct);

    // 1 + 2: tenant/user-selected agent — but ONLY if still usable (enabled public OR own private)
    if (selection.TryGetValue(role, out var sel))
    {
        var agent = await _registry.ResolveSelectedAgentAsync(sel.AgentId, ct);   // public ∪ own private
        if (agent is not null && await _registry.CanUseAsync(agent, principal, ct)) // NEW: enablement-aware (async)
            return await MaterialiseAsync(agent, role, action, source: SourceFor(agent), ct);

        if (agent is not null)   // selection points at a now-DISABLED persona => degrade, don't resolve it
        {
            _logger.LogWarning("Selection for role {Role} points at disabled persona {AgentId}; degrading to enabled default",
                role, sel.AgentId);
            await _events.AppendAsync(Degraded(role, sel.AgentId, "system-public-enabled", principal), ct);
        }
    }

    // 3: the tenant's ENABLED default persona
    var enabledDefault = await _registry.GetSystemDefaultPublicAsync(role, ct);
    if (enabledDefault is not null)
        return await MaterialiseAsync(enabledDefault, role, action, source: "system-public", ct);

    // 4: NO empty fallback — fail loud
    await _events.AppendAsync(ResolveFailed(role, principal), ct);
    await _missingConfig?.RecordAsync(new MissingConfigGap("agent", $"role:{role}", "system"), ct);  // best-effort
    throw new TammaError("AGENT.RESOLVE.NO_ENABLED_DEFAULT",
        $"No enabled agent resolvable for role '{role}'", severity: High, context: new { role });
}
```

### `MaterialiseAsync` — prompt-source PRECEDENCE/dispatch only (no inline resolve body)

This story owns the **dispatch/selector** in `MaterialiseAsync`: public personas → 32-15's `IPersonaPromptResolver`, private/custom → 32-17's `ICustomAgentPromptResolver`. **It adds no prompt-resolution body of its own** — neither the Epic-27 persona resolve (that lives inside `IPersonaPromptResolver`, 32-15) nor the custom resolve (inside `ICustomAgentPromptResolver`, 32-17). There is no inline `_promptStore.ResolveAsync` for the persona branch in this story.

```csharp
// Tamma.Api/Services/Agents/AgentResolverService.cs — MODIFY MaterialiseAsync (DISPATCH ONLY)

private async Task<ResolvedAgentConfig> MaterialiseAsync(
    Agent agent, string role, string? action, string source, CancellationToken ct)
{
    // provider/model/params/tools come from the agent config merged onto DefaultAgentConfig.ForRole(role)
    var cfg = MergeConfig(DefaultAgentConfig.ForRole(role), agent.ActiveVersion.ConfigJson);

    // PROMPT SOURCE — PRECEDENCE/DISPATCH only (this story adds NO resolution body):
    var principal = _principal.Resolve();
    var systemPrompt = agent.IsPublic
        // PERSONA => 32-15 IPersonaPromptResolver (reads Epic 27 (principal, role, action); fail-loud internally)
        ? (await _personaPrompts.ResolveAsync(principal, role, action, ct)).Text
        // CUSTOM/PRIVATE => 32-17 ICustomAgentPromptResolver (reads ConfigJson.prompts; fail-loud internally)
        : (await _customAgentPrompts.ResolveAsync(agent, role, action, ct)).Text;

    return cfg.ToResolvedConfig() with
    {
        SystemPrompt = systemPrompt,
        AgentId = agent.Id,
        AgentVersion = agent.ActiveVersion.Version,
        Provider = cfg.Provider,     // persona-supplied (32-15) — drives 32-3 credential resolve downstream
        Model = cfg.Model,
        Source = source,
    };
}
```

> `_personaPrompts` is **32-15's `IPersonaPromptResolver`** (its body reads the Epic 27 `IPromptStoreService` `(principal, role, action)`, tenant→system→error, and throws `PROMPT_UNRESOLVED`/`NoPromptError` on miss). `_customAgentPrompts` is the **32-17 `ICustomAgentPromptResolver`** seam. This story only **dispatches** to them and tests that personas take the persona seam and never the custom one (and vice-versa) — it implements neither body. The **credential** is intentionally absent here: it is resolved by 32-3 at call time from `ResolvedAgentConfig.Provider`.

### End-to-end resolve order (BYOK∘persona) — for the call-LLM endpoint (32-5)

This story does not call a provider; it produces the `ResolvedAgentConfig` the call-LLM endpoint composes. The documented order the endpoint (32-5) follows, with this story owning steps 2 and 4 (prompt) and the enablement gate inside step 2:

```
Step (engine) → POST /api/v1/llm/call { tenantId, role, action, agentId/persona?, prompt, params }
  1. Gate (32-4) — SaaS auth / entitlement / budget
  2. Resolve agent config (THIS STORY, 32-18 over 32-2):
       - ResolveForRoleAsync(role, action) / explicit agentId
       - ENABLEMENT GATE: CanUseAsync = (IsPublic && IsEnabledForPrincipalAsync) || IsOwnPrivate   (32-16 read seam)
       - => ResolvedAgentConfig { Provider, Model, AgentId, AgentVersion, ... }   (persona supplies Provider+Model, 32-15)
  3. Resolve PROMPT — DISPATCH ONLY (THIS STORY, 32-18 adds no resolution body):
       - persona (IsPublic) => IPersonaPromptResolver (32-15) → Epic 27 store (principal, role, action)   [tenant→system→ERROR]
       - custom agent       => ICustomAgentPromptResolver (32-17) → its own ConfigJson.prompts
  4. Resolve CREDENTIAL (32-3): IProviderCredentialResolver.ResolveAsync(tenantId, config.Provider)
       - BYOK-for-that-provider if present => credentialSource = byok (no markup)
       - else platform key                 => credentialSource = platform (markup off provider cost, 34-5)
       - credentialSource decided by (tenant, persona.provider) — persona-INDEPENDENT
  5. Provider call → meter (cost via IProviderPricingService / 34-11) → return { result, usage, credentialSource }
```

`credentialSource` is orthogonal to persona/enablement: tenant A with a BYOK Anthropic key running persona `claude` → `byok`; the same tenant running `gemini` with no Google BYOK → `platform`.

### Where it lives (files touched — all EXISTING 32-2/32-15/32-16 files)

```
apps/tamma-elsa/src/Tamma.Api/Services/Agents/
  AgentRegistryService.cs        # MODIFY — CanUseAsync enablement-aware (ITenantAgentEnablementReader); ListAsync enabled∪own-private;
                                 #          SelectForRoleAsync gate (AGENT.SELECT.NOT_ENABLED);
                                 #          GetSystemDefaultPublicAsync => enabled default (consumes GetEnabledDefaultPersonaIdAsync)
  IAgentRegistryService.cs       # MODIFY — CanUseAsync made part of the contract (or kept internal + tested via Select/Resolve);
                                 #          ListAsync filter gains IncludeDisabled (owner/admin only)
  AgentResolverService.cs        # MODIFY — resolve precedence uses CanUseAsync; degrade-on-disabled;
                                 #          MaterialiseAsync prompt-source DISPATCH ONLY (public→IPersonaPromptResolver 32-15,
                                 #          private→ICustomAgentPromptResolver 32-17; no inline resolve body); action plumbed
  IAgentResolverService.cs       # MODIFY — ResolveForRoleAsync/ResolveForRoleAndPhaseAsync gain `action` param
  AgentEventTypes.cs             # MODIFY — add SelectNotEnabled, ResolveDegraded, NoEnabledDefault constants
  ResolvedAgentConfig.cs         # (unchanged — already carries AgentId/AgentVersion/Provider/Model from 32-2/32-15)

apps/tamma-elsa/src/Tamma.Api/Endpoints/
  AgentEndpoints.cs              # MODIFY — SelectForRole maps AGENT.SELECT.NOT_ENABLED TammaError => 409 agent_not_enabled;
                                 #          Resolve maps AGENT.RESOLVE.NO_ENABLED_DEFAULT => 404/409; List honours ?includeDisabled
```

### EF migrations

**None.** This story adds no entity and no column. It edits service/resolver logic only. The `TenantAgentEnablement` table is owned by **32-16**; the nullable-`Agent.Role` migration + persona seeder are owned by **32-15**. `dotnet ef migrations has-pending-model-changes` MUST report none after this story (AC 11). Because no public/control-plane table is added here, the **Program.cs startup-reset DROP list** does NOT change (32-16 appends `tenant_agent_enablement` to it if it is CP-resident).

### DCB events

| Event | Tags | When | Owner |
|---|---|---|---|
| `AGENT.SELECT.NOT_ENABLED` | `{ agentId, personaName, role, mode, tenantId\|userId }` | selection blocked by enablement gate | **32-18 (this)** |
| `AGENT.RESOLVE.DEGRADED` | `{ role, staleAgentId, fallbackSource, mode }` | selection pointed at a now-disabled persona; degraded to enabled default | **32-18 (this)** |
| `AGENT.SELECTED_FOR_ROLE.SUCCESS` | `{ agentId, role, source, mode }` | successful role selection | 32-2 (kept) |
| `AGENT.RESOLVE.FAILED` | `{ role, phase, source, mode }` | nothing enabled/own resolvable → fail loud | 32-2 (kept) |
| `AGENT.ENABLED.SUCCESS` / `AGENT.DISABLED.SUCCESS` | `{ agentId, personaName, mode, … }` | enable/disable a persona | **32-16 (NOT here)** |

All appended via `IEventRepository.AppendAsync` (tenant-scoped in SaaS; platform-events path in single-user, `TenantId == null`).

### Per-mode / per-tenant ownership (mandatory two-scoping-model answer)

| Question | single-user | SaaS |
|---|---|---|
| Who owns **enablement** (which personas exist)? | The sole user (`user_id`-keyed `TenantAgentEnablement` row; CP-resident — owned by 32-16). The gate is evaluated against `user_id`. | The tenant (`tenant_id`-keyed). `tenant_owner`/`tenant_admin` enable/disable; `member` sees the enabled set, cannot change it. Gate evaluated against `tenant_id`. |
| Whose enabled set constrains **selection**? | The sole user's. `SelectForRoleAsync` rejects a public persona the user has not enabled. | The tenant's. `member` users select only within `enabled(public) ∪ own-private`; no per-user enablement layer. |
| Where does the **persona prompt** come from? | Epic 27 store keyed `(user_id, role, action)` — user override → system default → ERROR. | Epic 27 store keyed `(tenant_id, role, action)` — tenant override → system default → ERROR. No per-user prompt layer in SaaS (CLAUDE.md). |
| Where does the **enabled-default persona** come from? | The user's enabled default (32-16) or the platform `DefaultPersonaName` if the user enabled it. | The tenant's enabled default or `DefaultPersonaName` if the tenant enabled it. Fail loud if nothing enabled. |
| Who resolves the **credential** for the persona's provider? | 32-3 at call time: user BYOK-for-provider → platform. Never the registry/resolver. | 32-3 at call time: tenant BYOK-for-provider → platform. `credentialSource` decided by `(tenant, provider)`. |
| Mode source | `ITammaModeProvider` (process-stable). | same |

## Dependencies

**Internal (hard prerequisites):**

- **Story 32-2** (Agent registry, resolution & RBAC API) — the services this story amends in place (`AgentRegistryService`, `AgentResolverService`, `AgentEndpoints`, `AgentEventTypes`, `ResolvedAgentConfig`). Shipped on `feat/exec-wave-02`.
- **Story 32-15** (Persona reframe + seeding) — makes `Agent.Role` nullable, seeds named cross-role personas with explicit `provider`+`model`, provides the **`IPersonaPromptResolver`** seam (the persona→Epic-27 resolution body) and `DefaultPersonaName`. This story DISPATCHES the public branch to that seam; it does not re-inline the Epic-27 resolve.
- **Story 32-16** (Per-tenant agent/persona enablement) — owns the `TenantAgentEnablement` entity, the enable/disable API, `AGENT.ENABLED/DISABLED.SUCCESS`, and the `ITenantAgentEnablementReader` read seam (`IsEnabledForPrincipalAsync`, `ListEnabledPublicAgentIdsAsync`, `GetEnabledDefaultPersonaIdAsync` — all async) this story injects + consumes.
- **Story 32-17** (Custom-agent prompts) — owns the `ConfigJson.prompts` schema + the private-agent prompt-source seam (**`ICustomAgentPromptResolver`**); this story dispatches the private branch to it only.
- **Epic 27** (Prompt store) — `IPromptStoreService.ResolveAsync(principal, role, action)` (tenant→system→error, NEVER empty/plain) is the persona prompt source, reached **through 32-15's `IPersonaPromptResolver` seam** (this story does not call `IPromptStoreService` directly).

**Consumers (downstream, not blockers):**

- **Story 32-5 (rewrite)** — the call-LLM endpoint composes the resolve order documented above (enablement gate → config → Epic-27 prompt → 32-3 credential → provider call → meter).
- **Story 32-3** — its `IProviderCredentialResolver.ResolveAsync(tenantId, config.Provider)` consumes `ResolvedAgentConfig.Provider` at call time; this story guarantees `Provider` is persona-supplied and never resolves a credential itself.
- **Story 32-4** — SaaS provider gate runs before resolution in the endpoint.

**External:** none.

## Testing Strategy

Tests are xUnit under `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/`. Docker-bound suites run via `sg docker -c "dotnet test ..."` (see `reference_dotnet_test_docker`). TDD: write the failing test first. `ITenantAgentEnablementReader` (32-16), `IPersonaPromptResolver` (32-15), and `ICustomAgentPromptResolver` (32-17) are faked — this story dispatches to them and never re-implements a resolution body.

1. **Enablement gate on selection** (`AgentRegistryServiceTests`): (a) selecting an **enabled** public persona → 200 + `AGENT.SELECTED_FOR_ROLE.SUCCESS`; (b) selecting a **disabled** public persona → `TammaError("AGENT.SELECT.NOT_ENABLED")` → endpoint 409 `agent_not_enabled`, exactly one `AGENT.SELECT.NOT_ENABLED` event; (c) selecting the caller's **own private** agent → 200 (implicitly enabled); (d) cross-tenant private target → 404 (unchanged).
2. **`CanUseAsync` predicate** (`AgentRegistryServiceTests`): public+enabled → usable; public+disabled → not usable; own-private → usable; other-tenant-private → not usable (via the faked `ITenantAgentEnablementReader.IsEnabledForPrincipalAsync`). Asserts the predicate no longer returns true for a public persona solely because it is public.
3. **Resolution degrades past a disabled selection** (`AgentResolverServiceTests`): selection points at persona later disabled → resolve returns the **enabled default**, emits `AGENT.RESOLVE.DEGRADED` (WARN), never materialises the disabled persona.
4. **Enabled-default lookup** (`AgentResolverServiceTests`): (a) `DefaultPersonaName` enabled → returned; (b) `DefaultPersonaName` not enabled but another persona is the enabled default → that one returned; (c) nothing enabled and no own-private for the role → `AGENT.RESOLVE.FAILED` + `TammaError("AGENT.RESOLVE.NO_ENABLED_DEFAULT")`, **no blank config**.
5. **Persona prompt dispatched to `IPersonaPromptResolver`** (`AgentResolverServiceTests`): a resolved **public persona** has its system prompt resolved via the faked **`IPersonaPromptResolver`** seam (32-15) — assert `MaterialiseAsync` calls that seam and does NOT call `IPromptStoreService` directly (no inline persona resolve in 32-18); assert the persona's `ConfigJson` prompt (if any test fixture sets one) is **NOT** used; a seam miss (`PROMPT_UNRESOLVED`) propagates (no empty/plain).
6. **Action key plumbing** (`AgentResolverServiceTests`): `ResolveForRoleAsync(role, action)` passes `action` to the prompt store; absent `action` → action-default branch (still tenant→system→error).
7. **Custom-agent branch untouched** (`AgentResolverServiceTests`): a resolved **private** agent dispatches to the 32-17 **`ICustomAgentPromptResolver`** seam, NOT the persona seam / Epic-27 store — proves the boundary; this story does not implement either branch's body.
8. **Mode-parameterized principal** (`[Theory]` over `TammaMode.SingleUser`/`SaaS`): the enablement gate + prompt resolution evaluate against `user_id` vs `tenant_id`; no per-user enablement layer.
9. **No credential in the resolve path** (`AgentResolverServiceNoCredentialTests`): a resolve never calls any `IProviderCredentialResolver`/cabinet seam and never logs a key; `ResolvedAgentConfig` carries `Provider`/`Model` but no `ApiKey`.
10. **Endpoint mapping** (`AgentEndpointsTests`, `WebApplicationFactory`): `PUT role-selections/{role}` at a disabled persona → 409 `agent_not_enabled`; `GET /api/agents` returns only enabled∪own-private for a member; `?includeDisabled=true` from an admin returns the full catalog with `enabled` flags; from a member → ignored/forbidden.
11. **No-regression**: existing 32-2 `AgentEndpointsTests`/`AgentResolverServiceTests`, the legacy `/api/v1/agents/*` JSONB path, and `has-pending-model-changes` → none stay green.

## Estimated Effort

3-4 days

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentRegistryService.cs` | Modify (`CanUseAsync` enablement-aware via `ITenantAgentEnablementReader`; `ListAsync` enabled∪own-private; `SelectForRoleAsync` gate; `GetSystemDefaultPublicAsync` → enabled default, consumes `GetEnabledDefaultPersonaIdAsync`) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/IAgentRegistryService.cs` | Modify (`ListAsync` filter `IncludeDisabled`; expose `CanUseAsync` or document internal) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentResolverService.cs` | Modify (resolve precedence uses `CanUseAsync`; degrade-on-disabled; `MaterialiseAsync` prompt-source DISPATCH ONLY → public:`IPersonaPromptResolver` (32-15) / private:`ICustomAgentPromptResolver` (32-17), no inline resolve body; `action` plumbed) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/IAgentResolverService.cs` | Modify (`ResolveForRoleAsync`/`ResolveForRoleAndPhaseAsync` gain `action`) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentEventTypes.cs` | Modify (add `SelectNotEnabled`, `ResolveDegraded`, `NoEnabledDefault`) |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/AgentEndpoints.cs` | Modify (`SelectForRole` → 409 `agent_not_enabled`; `Resolve` → 404/409 on no-enabled-default; `List` honours `?includeDisabled`) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/AgentRegistryServiceTests.cs` | Create/Modify (gate, `CanUseAsync`, enabled-default) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/AgentResolverServiceTests.cs` | Create/Modify (degrade, Epic-27 prompt, action plumbing, custom-branch boundary, fail-loud) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/AgentResolverServiceNoCredentialTests.cs` | Create (no credential in resolve path) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/AgentEndpointsTests.cs` | Create/Modify (409 mapping, enabled-listing, `?includeDisabled`) |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
2. Searched `.dev/` directory for related spikes, bugs, findings, and decisions (esp. `feedback_resolution_no_empty_fallback`)
3. Confirmed **32-15** (`Agent.Role` nullable + persona seeder + the `IPersonaPromptResolver` seam + `DefaultPersonaName`), **32-16** (`ITenantAgentEnablementReader` with async `IsEnabledForPrincipalAsync` + `ListEnabledPublicAgentIdsAsync` + `GetEnabledDefaultPersonaIdAsync`), and **32-17** (`ICustomAgentPromptResolver`) have landed — this story consumes their seams and must not re-implement them
4. Reviewed the shipped 32-2 `AgentRegistryService`/`AgentResolverService` and the Epic 27 `IPromptStoreService.ResolveAsync(principal, role, action)` precedence
5. Planned TDD approach (Red-Green-Refactor cycle)

### Key design decisions

- **Amend in place, no new table** — this is a logic change to shipped 32-2 services. No entity, no migration, no DROP-list edit, no migration-snapshot branch. Keeps the linear EF snapshot intact (sequential impl).
- **Consume, don't define** — `IsEnabledForPrincipalAsync`/`ListEnabledPublicAgentIdsAsync`/`GetEnabledDefaultPersonaIdAsync` (the `ITenantAgentEnablementReader` seam) are 32-16's; the `IPersonaPromptResolver` resolution body is 32-15's; the `ICustomAgentPromptResolver` body is 32-17's; `Agent.Role`-nullable + persona seeder are 32-15's. This story is purely the **registry/resolver consumer + prompt-source dispatcher** of those. The boundary is load-bearing: re-implementing any of them here (including re-inlining the Epic-27 persona resolve) would double-implement and corrupt ownership (and, for entities, the migration graph).
- **409 for disabled-persona selection, 404 for cross-tenant** — selecting a persona that exists but is disabled is a state conflict (the persona is visible in the catalog), so 409; a cross-tenant private agent must not even be acknowledged, so 404 (existence-leak guard, unchanged from 32-2).
- **Degrade, never resolve a disabled selection** — a stale selection (persona disabled after it was selected) degrades to the enabled default with an `AGENT.RESOLVE.DEGRADED` audit event, exactly like 32-2's "selection target no longer in (public ∪ own private)" WARN — but the trigger is now "no longer enabled," not "no longer visible."
- **Persona = `IPersonaPromptResolver`, never config; dispatch only** — personas are prompt-free by contract (rule #4). `MaterialiseAsync` for a public persona **dispatches** to 32-15's `IPersonaPromptResolver` (whose body reads `(principal, role, action)` from Epic 27); this story adds no resolution body and re-inlines nothing. If the persona `ConfigJson` somehow carries a prompt it is ignored (and a seeding invariant in 32-15 forbids it). Custom agents dispatch to 32-17's `ICustomAgentPromptResolver`. Both seams fail loud, never empty/plain.
- **Credential stays out of the resolver** — the persona only names provider+model; the key is 32-3's job at call time, decided by `(tenant, provider)`. The resolver carries `Provider`/`Model` so 32-3 can resolve BYOK→platform; it must never touch a key (tested in AC 12 / test 9).

### Resolved coordination items

- **32-16 read-seam shape (RESOLVED).** 32-16 ships `ITenantAgentEnablementReader` with `Task<bool> IsEnabledForPrincipalAsync(Guid agentId, Principal principal, CancellationToken ct)`, `Task<IReadOnlyList<Guid>> ListEnabledPublicAgentIdsAsync(Principal principal, CancellationToken ct)`, and `Task<Guid?> GetEnabledDefaultPersonaIdAsync(Principal principal, CancellationToken ct)`. These are the exact async signatures this story calls (`GetEnabledDefaultPersonaIdAsync` is **defined in 32-16, consumed here, never redefined**). `ListAsync` uses the batch `ListEnabledPublicAgentIdsAsync` for set-membership (avoids per-row calls).

### Open coordination items

- **`DefaultPersonaName` source** — confirm whether 32-15 exposes it as a config option (`AgentOptions.DefaultPersonaName`) or a CP `agent_default_selections` row; `GetSystemDefaultPublicAsync` reads whichever 32-15 ships.
- **`action` derivation** — confirm how `action` is derived when the call-LLM request omits it (phase→action map vs the Epic-27 action-default). Default to the action-default branch (still fail-loud).

## Risks and Mitigations

| Risk | Severity | Mitigation |
|------|----------|------------|
| Double-implementing the enablement entity / persona-prompt body / custom-prompt body | High | Strict boundary: consume `ITenantAgentEnablementReader` (32-16), dispatch to `IPersonaPromptResolver` (32-15) and `ICustomAgentPromptResolver` (32-17); add NO entity/migration and NO inline resolve body. Coordination checklist in Dev Notes; AC 11 asserts no pending model changes; test 5 asserts no direct `IPromptStoreService` call. |
| A disabled persona still resolves (gate not applied on a path) | High | Gate centralised in `CanUseAsync`; both `SelectForRoleAsync` and the resolve precedence call it; test 2 asserts the predicate; test 3 asserts degrade-on-disabled. |
| Empty/plain prompt fallback sneaks in | High | `MaterialiseAsync` dispatches the persona branch to `IPersonaPromptResolver` (whose body fails loud, tenant→system→error/`NoPromptError`); a miss propagates; test 5 asserts the seam is invoked (not an inline resolve) and a miss propagates. Mirrors `feedback_resolution_no_empty_fallback`. |
| Credential leaks into the resolver | High | `ResolvedAgentConfig` carries `Provider`/`Model` only; dedicated no-credential test (test 9) asserts no resolver path resolves or logs a key. Credential is 32-3 at call time. |
| `GetSystemDefaultPublicAsync` returns nothing when the tenant enabled nothing | Medium | That is the **correct** fail-loud behaviour (AC 5): resolver throws `NO_ENABLED_DEFAULT`; test 4(c) asserts it, never a blank config. |
| 32-15/16/17 land late | Medium | Code to their interfaces (`ITenantAgentEnablementReader`, the `MaterialiseAsync` branch, `_customAgentPrompts`, `IPromptStoreService`); use fakes; this story is the integrator, gated behind them. |
| Listing perf — per-row `IsEnabledForPrincipalAsync` | Low | `ListAsync` uses 32-16's batch `ListEnabledPublicAgentIdsAsync` (one read → a set membership check); the public catalog is small (N personas) regardless. |

## Success Metrics

- [ ] No code path treats a public persona as usable solely because it is public (grep + test 2).
- [ ] 100% of persona resolutions are dispatched to `IPersonaPromptResolver` (32-15) and custom resolutions to `ICustomAgentPromptResolver` (32-17); this story calls neither resolution body directly nor `IPromptStoreService`; neither ever empty/plain.
- [ ] A tenant that has enabled nothing fails loud (`AGENT.RESOLVE.NO_ENABLED_DEFAULT`) rather than resolving an un-enabled persona or a blank config.
- [ ] Zero new tables/migrations; `has-pending-model-changes` → none; full suite green.

## Related

- Design of record: `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` (§3.1 prompt source, §3.3 changes to 32-2, §3.5 BYOK∘persona)
- Re-plan: `docs/superpowers/plans/2026-06-20-epic-32-37-replan.md` (§1 disposition of 32-2; §4 sequence step E)
- Amended story: `docs/stories/epic-32/story-32-2/32-2-agent-registry-resolution-and-rbac-api.md`
- Implementation plan: `docs/superpowers/plans/2026-06-21-32-18-registry-enablement-gate-and-prompt-source-plan.md`
- Sibling stories: `story-32-15/` (persona reframe), `story-32-16/` (enablement entity/API/events), `story-32-17/` (custom-agent prompts), `story-32-3/` (credential resolver), `story-32-5/` (call-LLM endpoint — consumer)

## Logging Requirements

- **INFO**: role selection upserted (role, agentId, source, mode); resolution succeeded (role, action, agentId, version, source, promptSource∈`epic27`|`custom-agent`); enabled-default resolved (role, personaName).
- **DEBUG**: which precedence branch was taken; enablement gate decision per candidate (agentId, enabled); prompt-store resolution layer hit (tenant-override | system-default).
- **WARN**: selection target no longer **enabled** ⇒ degrading to enabled default (role, staleAgentId) → `AGENT.RESOLVE.DEGRADED`; selection blocked by enablement gate (agentId, personaName, role) → `AGENT.SELECT.NOT_ENABLED`.
- **ERROR**: `AGENT.RESOLVE.FAILED` / `AGENT.RESOLVE.NO_ENABLED_DEFAULT` — nothing enabled/own resolvable for a taxonomy-valid role (role, mode); prompt-store miss (`NoPromptError`) on a persona (role, action).
- **Structured context**: include `{ agentId, personaName, role, action, source, mode, tenantId }` where applicable.
- **Credential safety**: the registry/resolver are **credential-agnostic** (provider+model+settings only). NEVER log or resolve a provider API key here — credentials resolve later in 32-3 from the Epic 29 cabinet inside the call-LLM endpoint. `credentialSource` is not even known at resolve time and must not be inferred or logged here.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-21 | 1.0.0   | Initial story creation (amends 32-2: enablement gate + Epic-27 persona prompt source) | Claude |
| 2026-06-21 | 1.0.1   | Cross-spec reconciliation (C1/C2/C3): AC6 reworded to **PRECEDENCE/dispatch only** — public personas → `IPersonaPromptResolver` (32-15), private/custom → `ICustomAgentPromptResolver` (32-17); removed the duplicate inline Epic-27 persona-resolve body from `MaterialiseAsync` (this story adds no prompt-resolution body, no phantom `_personaPromptBranch`). Standardized on the 32-16 read seam `ITenantAgentEnablementReader` with **async** `IsEnabledForPrincipalAsync` / `ListEnabledPublicAgentIdsAsync` / `GetEnabledDefaultPersonaIdAsync`; `CanUse` → async `CanUseAsync`. Resolved the open question — `GetEnabledDefaultPersonaIdAsync` signature now matches what 32-16 defines (consumed here, never redefined). | Claude |
