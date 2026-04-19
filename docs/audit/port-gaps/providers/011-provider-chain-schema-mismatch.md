# Finding 011: Provider chain JSON shape diverges — TS `roles.<r>.providerChain[]` vs C# `chains.<r>.<a>[]`

**Scope**: providers
**Severity**: P1 (chains configured via TS JSON return empty in C# resolver)
**Status**: Behavioral drift (schema rewrite)
**Estimated port effort**: 4–6h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/services/agent-resolver.ts`.

- File: `packages/api/src/services/agent-resolver.ts:343-350`
- Contract/behavior: Chains lived under `config.roles.<role>.providerChain` (role-scoped) with fallback to `config.defaults.providerChain`. No per-action dimension.

```typescript
// packages/api/src/services/agent-resolver.ts (9e9a57c~1) — lines 343-350
function _getProviderChain(config: IAgentsConfig, role: AgentType): readonly ProviderChainEntry[] {
  const roleConfig = config.roles?.[role];
  const roleChain = roleConfig?.providerChain;
  if (roleChain !== undefined && roleChain.length > 0) {
    return roleChain;
  }
  return config.defaults.providerChain;
}
```

- The shared shape (`IAgentsConfig`):

```
{
  "defaults": { "providerChain": [{provider, model?}, ...] },
  "roles": {
    "implementer": { "providerChain": [{provider, model?}, ...] }
  }
}
```

- Persisted by the TS `agents-routes.ts` PUT handler into `agent_configs.config` JSONB.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Services/Providers/ProviderChainResolver.cs:107-153`
- Contract/behavior: Chains live under `config.chains.<role>.<action>` (role × action 2D) with fallbacks to `chains.<role>.default`, then `chains.<role>` (treated as a default array if it's itself an array), then `chains.default`.

```csharp
// apps/tamma-elsa/src/Tamma.Api/Services/Providers/ProviderChainResolver.cs — lines 124-150
// 1. chains[role][action]
if (chains.TryGetProperty(role, out var roleNode) && roleNode.ValueKind == JsonValueKind.Object)
{
    if (roleNode.TryGetProperty(action, out var roleActionArr) &&
        roleActionArr.ValueKind == JsonValueKind.Array)
    {
        return ParseHandles(roleActionArr);
    }
    // 2. chains[role]["default"]
    if (roleNode.TryGetProperty("default", out var roleDefault) &&
        roleDefault.ValueKind == JsonValueKind.Array)
    {
        return ParseHandles(roleDefault);
    }
}
// 3. chains["default"]
if (chains.TryGetProperty("default", out var defaultNode) && ...)
{
    return ParseHandles(defaultNode);
}
```

- The expected C# JSON shape:

```
{
  "chains": {
    "default": [{"provider":"anthropic","model":"claude-sonnet-4"}, ...],
    "developer": {
      "implement": [{"provider":"openai","model":"gpt-4o"}, ...],
      "default":  [{"provider":"anthropic"}]
    }
  }
}
```

## 3. The gap

- Key name: TS `roles` vs C# `chains`.
- Dimensionality: TS is 1D (role → chain array); C# is 2D (role → action → chain array) with a "default" action fallback.
- Fallback order: TS `role → defaults`; C# `chains[role][action] → chains[role][default] → chains[role]-as-array → chains[default]`.
- Persisted rows written by TS (shape `{roles:{developer:{providerChain:[...]}}}`) are **invisible** to the C# resolver because:
  - `chains` key is missing — returns `Array.Empty<ProviderHandle>()` at `ProviderChainResolver.cs:119-122`.
  - Even if you alias `roles → chains`, the inner shape is `{providerChain:[...]}` not `{implement:[...], default:[...]}` — the C# parser looks for an action-key-named array or a `default` key, finds neither, returns empty.
- For a caller with TS-era `agent_configs` and a `POST /api/providers/chain/resolve {role:"developer", action:"implement"}`:
  - TS: returns `{ordered:[{provider:"claude-code",...}], skipped:[]}`.
  - C#: returns `{ordered:[], skipped:[], error:"EMPTY_PROVIDER_CHAIN", message:"No provider chain configured for role='developer' action='implement'."}`.

Error paths:
- TS: there was no error for "no chain" — `defaults.providerChain` always resolved.
- C#: `ChainResolveResult(ErrorCode:"EMPTY_PROVIDER_CHAIN", ErrorMessage:"No provider chain configured for role='X' action='Y'.")` on cold tenants.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-9/story-9-5/9-5-provider-chain.md` (provider chain) + `docs/stories/epic-9/story-9-8/9-8-role-based-agent-resolver.md`.
- Story 9-8 AC 2 describes resolution: "Role -> provider chain (account config or default)" — singular dimension, matching the TS `roles.<r>.providerChain` shape.
- Story 9-1 AC 1 pins the types: "`AgentsConfig`, `ProviderChainEntry`, `AgentRoleConfig`…" — all referencing `providerChain` on `AgentRoleConfig` (a 1D shape).
- Story alignment:
  - [x] Matches TS behavior (C# is a drift vs both story and TS).
  - [ ] Matches C# behavior.
  - [ ] Describes a third behavior — the 2D role×action shape is a C# invention, not in any story.
  - [ ] No story — there is one, and it's contradicted.

## 5. Status

- **Classification**: Behavioral drift / semantic rewrite.
- **What's needed to finish**:
  1. Decide canonical shape. Recommendation: **both**. Keep C# 2D for granularity (useful: "my developer uses OpenAI for implement, Anthropic for code-review"). Accept TS 1D as input by normalizing `roles.<r>.providerChain → chains.<r>.default`.
  2. In `ProviderChainResolver.LoadChainAsync`, also read `root.roles?.<role>?.providerChain` and `root.defaults?.providerChain` as fallbacks.
  3. Write a migration that, for each `agent_configs` row, rewrites JSONB `roles.*.providerChain → chains.*.default` and `defaults.providerChain → chains.default`.
  4. Update `AgentEndpoints.ValidateConfigShape` to accept either shape.
  5. Update Story 9-5 and 9-8 to document the 2D role × action grid.
- **Is it "just a stub" or is scope missing?** Scope is missing. The 2D grid was implemented without a story.
- **Blockers**: Depends on finding 001 (role taxonomy) because the JSONB rewrite must happen in the same migration that renames role keys.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Services/Providers/ProviderChainResolver.cs:107-153`
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/AgentEndpoints.cs:201-223` (validator)
- Files to create:
  - `apps/tamma-elsa/src/Tamma.Data/Migrations/<next>_NormalizeProviderChainShape.cs`
- Tests to add:
  - `ProviderChainResolver_LegacyRolesProviderChainShape_StillResolves`
  - `ProviderChainResolver_NewRoleActionShape_ResolvesWithActionKey`
  - `ProviderChainResolver_FallsBackToRoleDefault_WhenActionAbsent`
  - `ValidateConfigShape_AcceptsBothLegacyAnd2DChainShapes`
- Estimated effort: 5h.

## Remediation status

- **Confirmed**: 2026-04-19 by agent
- **Outcome**: Fixed (dual-shape: canonical 2D + legacy 1D fallbacks)
- **Commit**: `32bba50` `fix(providers): land P1 sanitizer/clamping/chain/rate-limit fixes [findings 006, 007, 011, 020]`
- **Notes**: `ProviderChainResolver.LoadChainAsync` now reads the canonical `chains.<role>.<action>` grid first (unchanged), then falls back to the TS legacy shape `roles.<role>.providerChain`, then `defaults.providerChain`. Walks the role alias map (finding 001) so a row written as `roles.implementer.providerChain` resolves for callers asking for `developer`. `EMPTY_PROVIDER_CHAIN` no longer fires for cold tenants whose JSONB was migrated from TS. `ValidateConfigShape` (finding 014) recognises both shapes and validates each entry's provider name against the regex.

## References

- TS source: `packages/api/src/services/agent-resolver.ts:343-350`, `packages/shared/src/types/agent-config.ts:123` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Services/Providers/ProviderChainResolver.cs:107-153`
- Story: `docs/stories/epic-9/story-9-5/9-5-provider-chain.md`, `docs/stories/epic-9/story-9-8/9-8-role-based-agent-resolver.md`
- Related findings: `001-role-phase-vocabulary-schism.md`, `007-task-overrides-clamping-lost.md`
- Archived SQL migration: `database/archived-sql-migrations/013_agent_configs.sql`
