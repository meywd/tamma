# Finding 007: `taskOverrides` clamping lost — no runtime budget/tool/permission intersection

**Scope**: providers
**Severity**: P1 (security downgrade on per-task scope-down)
**Status**: Incomplete (merge ported, clamping not ported)
**Estimated port effort**: 6–8h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/api/src/services/agent-resolver.ts`.

- File: `packages/api/src/services/agent-resolver.ts:361-435`
- Contract/behavior: A three-level merge `defaults < role < taskOverrides` with **clamping**: task overrides can only **restrict**, never **expand**.

```typescript
// packages/api/src/services/agent-resolver.ts (9e9a57c~1) — lines 395-427
// Level 3: Task overrides with clamping
if (taskOverrides !== undefined) {
  // Budget clamping
  if (taskOverrides.maxBudgetUsd !== undefined) {
    if (maxBudgetUsd !== null) {
      maxBudgetUsd = Math.min(taskOverrides.maxBudgetUsd, maxBudgetUsd);
    } else {
      maxBudgetUsd = taskOverrides.maxBudgetUsd;
    }
  }

  // Permission clamping
  if (taskOverrides.permissionMode !== undefined) {
    if (taskOverrides.permissionMode === 'bypassPermissions') {
      const envAllow = process.env['TAMMA_ALLOW_BYPASS_PERMISSIONS'];
      if (envAllow === 'true') {
        permissionMode = 'bypassPermissions';
      }
      // Otherwise keep current permissionMode
    } else {
      permissionMode = taskOverrides.permissionMode;
    }
  }

  // Tool clamping: intersection only
  if (taskOverrides.allowedTools !== undefined) {
    if (allowedTools.length > 0) {
      const currentSet = new Set(allowedTools);
      allowedTools = taskOverrides.allowedTools.filter((t: string) => currentSet.has(t));
    } else {
      allowedTools = [...taskOverrides.allowedTools];
    }
  }
}
```

- Rules:
  1. `maxBudgetUsd = Math.min(taskOverride, roleOrDefault)` — **ceiling cannot be raised**.
  2. `permissionMode === 'bypassPermissions'` requires env var `TAMMA_ALLOW_BYPASS_PERMISSIONS=true` — a process-level safety gate.
  3. `allowedTools` intersection with the role/default set — **no new tools can be added**.
- The `POST /api/v1/agents/resolve-for-phase` endpoint accepted `taskOverrides` in the body (see `agent-resolver-routes.ts:38-46`): `{maxBudgetUsd?, allowedTools?, permissionMode?, prompt?, cwd?, model?, sessionId?}`.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentResolverService.cs:126-151` (`MergeOverride`)
- Contract/behavior: Two-level merge only (`platform-default < tenant-override`). The `ResolveForPhaseAsync` signature at lines 56-87 does **not accept** a `taskOverrides` parameter at all.

```csharp
// apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentResolverService.cs — lines 126-151
private static ResolvedAgentConfig MergeOverride(
    ResolvedAgentConfig baseConfig, JsonElement roleOverride)
{
    var provider = GetStringOrDefault(roleOverride, "provider", baseConfig.Provider);
    var model = GetStringOrDefault(roleOverride, "model", baseConfig.Model);
    var temperature = GetDoubleOrDefault(roleOverride, "temperature", baseConfig.Temperature);
    var maxTokens = GetIntOrDefault(roleOverride, "maxTokens", baseConfig.MaxTokens);
    var tokenBudget = GetIntOrDefault(roleOverride, "tokenBudget", baseConfig.TokenBudget);
    var systemPrompt = GetStringOrDefault(roleOverride, "systemPrompt", baseConfig.SystemPrompt);
    var handle = GetStringOrDefault(roleOverride, "handle", baseConfig.Handle);
    var tools = GetStringArrayOrDefault(roleOverride, "tools", baseConfig.Tools);

    return new ResolvedAgentConfig { /* direct replace; no clamping */ };
}
```

- The DTO `ResolveForPhaseRequest` only has `Phase`, `Role`, `TaskType` fields; there is no `TaskOverrides` field (see `apps/tamma-elsa/src/Tamma.Api/Dtos/Agents/ResolveForPhaseRequest.cs`).
- No `TAMMA_ALLOW_BYPASS_PERMISSIONS` env var is consulted anywhere in the C# API.
- `AgentEndpoints.ResolveForPhase` (lines 145-167) passes `tenantId, phase, role` to the service — no overrides path.
- Dependencies: none — the TS taskOverrides code was self-contained.

## 3. The gap

- TS allowed a workflow activity to say "this particular task should cap at $0.50, only `Read`+`Grep`, no bypass-permissions", intersecting with the role's defaults. C# cannot scope a task down.
- TS gated `bypassPermissions` behind an operator-set env var; C# has no bypass concept **and** no env gate. A tenant config that sets `permissionMode: 'bypassPermissions'` at the role level takes effect unconditionally (there's no check).
- TS `allowedTools` intersection meant a per-task override could never smuggle in a tool the role didn't have (SECURITY); C# doesn't have `allowedTools` at the task level at all.
- For a caller sending `POST /resolve-for-phase {phase:"CODE_GENERATION", taskOverrides:{maxBudgetUsd:0.25, allowedTools:["Read"]}}`:
  - TS returns `{maxBudgetUsd: 0.25, allowedTools: ['Read']}` (clamped).
  - C# ignores the `taskOverrides` field (not bound) and returns the role's full config.

Error paths:
- TS: no error — silent clamping with the clamped value in the response.
- C#: no error — the extra body field is ignored.

## 4. Gap from stories

- Referenced story: `docs/stories/epic-9/story-9-8/9-8-role-based-agent-resolver.md`.
- Story 9-8 AC 4: **"Config merge clamping rules preserved: `maxBudgetUsd` cannot exceed ceiling from defaults/role; `bypassPermissions` requires `TAMMA_ALLOW_BYPASS_PERMISSIONS=true`; `allowedTools` intersection only (restrict, never expand)."** — explicitly specifies all three clamping rules.
- Story alignment:
  - [x] Matches TS behavior (C# is a regression vs both story and TS).
  - [ ] Matches C# behavior.
  - [ ] Describes a third behavior.
  - [ ] No story — there is a story, and it is directly contradicted.

## 5. Status

- **Classification**: Incomplete port.
- **What's needed to finish**:
  1. Extend `ResolveForPhaseRequest` DTO with optional `TaskOverrides` record: `{MaxBudgetUsd?, AllowedTools?, PermissionMode?, Model?}`.
  2. Plumb it through `AgentResolverService.ResolveForPhaseAsync(tenantId, phase, role, overrides)`.
  3. Implement the three clamping rules after `MergeOverride` returns.
  4. Read `TAMMA_ALLOW_BYPASS_PERMISSIONS` from `IConfiguration` (ASP.NET) — not `Environment.GetEnvironmentVariable`, so appsettings can set it.
  5. Add `MaxBudgetUsd` and `AllowedTools` fields to `ResolvedAgentConfig` so the clamped values are carried in the response (today `ResolvedAgentConfig` has `Tools` and `TokenBudget` but no `MaxBudgetUsd` or `PermissionMode`).
  6. Add corresponding fields to `DefaultAgentConfig.ForRole` so the role baseline ceiling exists to clamp against.
- **Is it "just a stub" or is scope missing?** Port is incomplete — the two-level merge was done, the third level (`taskOverrides`) was dropped without being replaced.
- **Blockers**: None.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentResolverService.cs`
  - `apps/tamma-elsa/src/Tamma.Api/Services/Agents/ResolvedAgentConfig.cs`
  - `apps/tamma-elsa/src/Tamma.Api/Services/Agents/DefaultAgentConfig.cs`
  - `apps/tamma-elsa/src/Tamma.Api/Dtos/Agents/ResolveForPhaseRequest.cs`
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/AgentEndpoints.cs:145-167`
- Files to create:
  - `apps/tamma-elsa/src/Tamma.Api/Dtos/Agents/TaskOverrides.cs`
- Tests to add:
  - `AgentResolver_TaskBudgetOverrideHigher_ClampsToRoleCeiling`
  - `AgentResolver_TaskBudgetOverrideLower_UsesLower`
  - `AgentResolver_BypassPermissions_WithoutEnvVar_StaysAtRoleMode`
  - `AgentResolver_BypassPermissions_WithEnvVar_TakesEffect`
  - `AgentResolver_ToolOverride_IsIntersectionNotUnion`
  - `AgentResolver_ToolOverride_TryingToAddUnlistedTool_ExcludesIt`
- Estimated effort: 7h broken down as:
  - DTO + signature plumbing: 2h
  - Three clamping impls + env-var read: 2h
  - Tests: 3h

## Remediation status

- **Confirmed**: 2026-04-19 by agent
- **Outcome**: Fixed
- **Commit**: `32bba50` `fix(providers): land P1 sanitizer/clamping/chain/rate-limit fixes [findings 006, 007, 011, 020]`
- **Notes**: Added `Dtos.Agents.TaskOverrides` (`MaxBudgetUsd`, `AllowedTools`, `PermissionMode`, `Model`) and a fourth optional field on `ResolveForPhaseRequest`. Extended `ResolvedAgentConfig` with `MaxBudgetUsd`, `PermissionMode`, `AllowedTools`. New `IAgentResolverService.ResolveForPhaseAsync(tenantId, phase, role, overrides)` overload applies the three TS clamping rules: budget = `Math.Min`, tools intersected (cannot expand), `bypassPermissions` requires `TAMMA_ALLOW_BYPASS_PERMISSIONS=true` (env var OR `Tamma:AllowBypassPermissions=true` in appsettings — checked via `IConfiguration` so staging can flip it without redeploying). `AgentEndpoints.ResolveForPhase` plumbs the override through. Rejected overrides log a warning so misconfigured workflows are observable.

## References

- TS source: `packages/api/src/services/agent-resolver.ts:361-435`, `packages/api/src/routes/agents/agent-resolver-routes.ts:38-46` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentResolverService.cs:126-151`
- Story: `docs/stories/epic-9/story-9-8/9-8-role-based-agent-resolver.md` AC 4
- Related findings: `001-role-phase-vocabulary-schism.md`, `011-provider-chain-schema-mismatch.md`
- CLAUDE.md section: "Security Requirements — Credential Management" implies principle-of-least-privilege across task invocations.
