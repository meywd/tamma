# Finding 001: Role / Phase vocabulary schism between TS and C#

**Scope**: providers
**Severity**: P0 (cutover-blocking)
**Status**: Semantic rewrite (taxonomies do not map)
**Estimated port effort**: 8–12h

## 1. What's in TS

Pre-delete snapshot at `git show 9e9a57c~1:packages/shared/src/types/knowledge.ts` and
`git show 9e9a57c~1:packages/shared/src/types/agent-config.ts`.

- File: `packages/shared/src/types/knowledge.ts:38-47` — `AgentType` (9 roles; `scrum_master | architect | researcher | analyst | planner | implementer | reviewer | tester | documenter`).
- File: `packages/shared/src/types/agent-config.ts:18-41` — `WorkflowPhase` = 8 `UPPER_SNAKE` phases plus `DEFAULT_PHASE_ROLE_MAP`.
- Contract/behavior: The union type is used as a discriminator throughout the API (`agent_configs.config.roles`, `agent_configs.config.phaseRoleMap`, `POST /resolve-for-phase` body), the persisted JSONB shape, and as the `agent_type` column value in `provider_diagnostics`.

```typescript
// packages/shared/src/types/knowledge.ts (9e9a57c~1) — lines 38-47
export type AgentType =
  | 'scrum_master'
  | 'architect'
  | 'researcher'
  | 'analyst'
  | 'planner'
  | 'implementer'
  | 'reviewer'
  | 'tester'
  | 'documenter';
```

```typescript
// packages/shared/src/types/agent-config.ts (9e9a57c~1) — lines 18-41
export type WorkflowPhase =
  | 'ISSUE_SELECTION'
  | 'CONTEXT_ANALYSIS'
  | 'PLAN_GENERATION'
  | 'CODE_GENERATION'
  | 'PR_CREATION'
  | 'CODE_REVIEW'
  | 'TEST_EXECUTION'
  | 'STATUS_MONITORING';

export const DEFAULT_PHASE_ROLE_MAP: Record<WorkflowPhase, AgentType> = Object.freeze({
  ISSUE_SELECTION: 'scrum_master',
  CONTEXT_ANALYSIS: 'analyst',
  PLAN_GENERATION: 'architect',
  CODE_GENERATION: 'implementer',
  PR_CREATION: 'implementer',
  CODE_REVIEW: 'reviewer',
  TEST_EXECUTION: 'tester',
  STATUS_MONITORING: 'scrum_master',
});
```

- Dependencies: `agent-resolver.ts`, `agent-resolver-routes.ts` (route params), `diagnostics-store.ts` (`agentType` column), `prompts-routes.ts` (`VALID_ROLES`).
- Tests that exercised this: `packages/shared/src/types/agent-config.test.ts`, `packages/api/src/routes/agents/__tests__/agent-resolver-routes.test.ts`.

## 2. What's in C#

Current state on `feat/auth-foundation`.

- File: `apps/tamma-elsa/src/Tamma.Api/Services/Agents/RolePhaseMap.cs:24-52`
- Contract/behavior: 8 roles and 10 "actions". Different names, different lengths, different casing conventions, and different mapping semantics (role→primary action, action→eligible roles — not phase→role).

```csharp
// apps/tamma-elsa/src/Tamma.Api/Services/Agents/RolePhaseMap.cs — lines 24-52
public static readonly FrozenSet<string> ValidRoles = new HashSet<string>
{
    "developer",
    "tester",
    "security",
    "devops",
    "architect",
    "product_owner",
    "senior_developer",
    "tech_writer",
}.ToFrozenSet();

public static readonly FrozenSet<string> ValidActions = new HashSet<string>
{
    "context-scan",
    "plan",
    "plan-review",
    "implement",
    "write-tests",
    "refactor",
    "code-review",
    "triage",
    "summarize",
    "debug",
}.ToFrozenSet();
```

- Dependencies: `AgentResolverService`, `DefaultAgentConfig`, `AgentEndpoints.ValidateConfigShape` (lines 216-222 reject any role not in `ValidRoles`), `PromptEndpoints` (role path params).
- Tests: `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/*` — cover the C# taxonomy only; no compatibility test for TS-era JSON.

## 3. The gap

- TS persisted `agent_configs.config` JSONB with roles keyed on `scrum_master`, `analyst`, `planner`, `implementer`, `reviewer`, `documenter`, etc. The C# validator at `AgentEndpoints.cs:216-222` rejects every one of those keys as `Unknown role`.
- TS `POST /resolve-for-phase` body has `phase: 'CODE_GENERATION' | 'ISSUE_SELECTION' | ...`. C# `AgentEndpoints.ResolveForPhase` expects `phase: 'implement' | 'plan' | 'context-scan' | ...` (via `RolePhaseMap.AssertValidPhase`).
- TS `provider_diagnostics.agent_type` column (per archived migration `014_provider_diagnostics.sql:16`) held values like `'implementer'` or `'reviewer'`. The C# entity `ProviderDiagnostic` has dropped this column entirely (see finding 008), but any historical replay / export still speaks TS vocab.
- Mapping table (the real conversion a migration would need):

  | TS `AgentType` | Plausible C# role | TS `WorkflowPhase` | Plausible C# action |
  |---|---|---|---|
  | `implementer` | `developer` | `CODE_GENERATION` | `implement` |
  | `reviewer` | `security` or `senior_developer` | `CODE_REVIEW` | `code-review` |
  | `tester` | `tester` | `TEST_EXECUTION` | `write-tests` |
  | `architect` | `architect` | `PLAN_GENERATION` | `plan` |
  | `analyst` | **no mapping** | `CONTEXT_ANALYSIS` | `context-scan` |
  | `scrum_master` | **no mapping** (`product_owner`?) | `ISSUE_SELECTION` | `triage` |
  | `planner` | **no mapping** (`senior_developer`?) | — | — |
  | `documenter` | `tech_writer` | — | `summarize` |
  | `researcher` | **no mapping** | — | — |

- In production with existing data / deployed clients, this means: the moment a tenant row written by the TS engine is read by C#, either the resolver silently returns platform defaults (no override applied, per `AgentResolverService.cs:44-47` which swallows unknown role keys) or the validator rejects the next `PUT /api/v1/agents/config` with `Unknown role '<ts-role>'`. Elsa workflows keyed to TS identifiers simply fall through to defaults.

Error paths:
- TS error: `400 {error: 'Invalid workflow phase: "implement"'}` (not a TS phase).
- C# error: `400 {error: 'Unknown phase: \'CODE_GENERATION\'. Valid phases: context-scan, plan, ...'}` on the other direction.

## 4. Gap from stories

Which Epic / story file describes what this surface SHOULD be?

- Referenced story: `docs/stories/epic-9/story-9-1/9-1-configuration-schema.md` + `docs/stories/epic-9/story-9-8/9-8-role-based-agent-resolver.md`.
- Story 9-1 AC 1: "`AgentsConfig`, `SecurityConfig`, `ProviderChainEntry`, `AgentRoleConfig`, `WorkflowPhase`, and `PermissionMode` types are defined in `packages/shared/src/types/agent-config.ts`" — authoritatively locks in the `UPPER_SNAKE` phase names and the 9-role `AgentType`.
- Story 9-8 AC 2: resolution "Phase -> role mapping via `phaseRoleMap` (account config or default) … Role -> provider chain …" — requires the `DEFAULT_PHASE_ROLE_MAP` vocabulary end-to-end.
- Story 12-5 (`12-5-prompt-engineering-framework.md`) cross-refs the TS 80 role+action template grid; the shipping C# `DefaultAgentConfig.cs` was composed against the new 8-role × 10-action grid instead.
- Story alignment:
  - [x] Matches C# behavior (story was NOT updated during port; C# adopted a different, richer taxonomy unilaterally).
  - [ ] Matches TS behavior.
  - [x] Describes a third behavior? Partially — Story 9-1 pins TS names; Story 12-5 references TS names; Epic 19 implementation plans did not propose a rename.
  - [x] No story — spec gap: there is no story that documents renaming `implementer → developer` or `CODE_GENERATION → implement`, nor the migration path for existing JSONB rows.

## 5. Status

- **Classification**: Semantic rewrite / Data-model regression.
- **What's needed to finish**:
  1. Decide the authoritative taxonomy (recommendation: keep the richer C# `{role,action}` grid because `DefaultAgentConfig.cs` + `RolePhaseMap` are wired into DI and `PromptEndpoints`, and update Story 9-1 + Story 9-8 to match).
  2. Write a one-shot migration that rewrites `agent_configs.config` JSONB: `roles.implementer → roles.developer`, `roles.reviewer → roles.senior_developer`, drop `roles.analyst` + `roles.scrum_master` + `roles.planner` + `roles.researcher` (or merge into `roles.product_owner`), normalize `phaseRoleMap` keys/values.
  3. In `ValidateConfigShape` accept legacy role keys and warn (return `{valid:true, warnings:[...]}`) for one release window instead of hard-rejecting.
  4. In `ResolveForPhase` translate legacy `UPPER_SNAKE` phase names to the new action names before `AssertValidPhase`.
  5. Update Epic 9 and Epic 12 story docs; add an ADR in `.dev/decisions/` documenting the taxonomy switch.
- **Is it "just a stub" or is scope missing?** Scope is missing from spec. The rewrite was done in code without a story update or a data migration.
- **Blockers**: Requires coordination with Elsa workflow activities (they reference `CODE_GENERATION`-style phase names through `LlmCallWorkflow`). Depends on finding 008 (diagnostics agent_type column) because the `agent_type` values emitted by both systems must agree.

## Remediation

- Files to modify:
  - `apps/tamma-elsa/src/Tamma.Api/Services/Agents/RolePhaseMap.cs` (add legacy alias table)
  - `apps/tamma-elsa/src/Tamma.Api/Endpoints/AgentEndpoints.cs:216-222` (soft-reject → warn)
  - `docs/stories/epic-9/story-9-1/9-1-configuration-schema.md` (update AC 1 vocabulary)
  - `docs/stories/epic-9/story-9-8/9-8-role-based-agent-resolver.md` (update phase names)
- Files to create:
  - `apps/tamma-elsa/src/Tamma.Data/Migrations/<next>_NormalizeAgentConfigTaxonomy.cs`
  - `.dev/decisions/<next>-role-phase-taxonomy-rewrite.md`
- Tests to add:
  - `AgentEndpoints_UpdateConfig_AcceptsLegacyTsRoleKeysWithWarning`
  - `AgentResolver_ResolveForPhase_MapsUpperSnakePhaseToAction`
  - `Migration_NormalizeAgentConfigTaxonomy_RewritesImplementerToDeveloper`
- Estimated effort: 10h broken down as:
  - Alias table + soft-validation: 3h
  - JSONB migration + test fixtures: 4h
  - Story + ADR writes: 2h
  - Regression tests: 1h

## Remediation status

- **Confirmed**: 2026-04-19 by agent
- **Outcome**: Fixed (soft-acceptance + alias normalisation)
- **Commit**: `498889b` `fix(providers): land P0 pricing/budget/role/CLI-stub fixes [findings 001, 003, 004, 005]`
  - Follow-up `32bba50` extended `ProviderChainResolver` to also walk legacy aliases when reading the JSONB chain.
- **Notes**: Kept the C# 8-role × 10-action grid as the canonical taxonomy (matches `DefaultAgentConfig` + `RolePhaseMap` wiring). Added `LegacyRoleAliases` (`implementer→developer`, `reviewer→senior_developer`, `analyst→product_owner`, …) and `LegacyPhaseAliases` (`CODE_GENERATION→implement`, …) plus `NormalizeRole`/`NormalizePhase`. `AgentResolverService.ResolveAsync` and `ResolveForPhaseAsync` translate before strict validation; `ValidateConfigShape` no longer 400s on legacy keys; `TryGetRoleOverride` and `ProviderChainResolver` walk the alias map when looking up tenant JSONB. Story doc updates and a one-shot data migration are still pending — flagged as drift in the section-5 status (item 1 done, item 2 deferred).

## References

- TS source: `packages/shared/src/types/knowledge.ts`, `packages/shared/src/types/agent-config.ts`, `packages/api/src/services/agent-resolver.ts:308-322` (commit `9e9a57c~1`)
- C# source: `apps/tamma-elsa/src/Tamma.Api/Services/Agents/RolePhaseMap.cs`, `apps/tamma-elsa/src/Tamma.Api/Services/Agents/DefaultAgentConfig.cs`, `apps/tamma-elsa/src/Tamma.Api/Endpoints/AgentEndpoints.cs:216-222`
- Story: `docs/stories/epic-9/story-9-1/9-1-configuration-schema.md`, `docs/stories/epic-9/story-9-8/9-8-role-based-agent-resolver.md`, `apps/wiki-site/public/content/stories/epic-12/12-5-prompt-engineering-framework.md`
- Related findings: `007-task-overrides-clamping-lost.md`, `008-diagnostics-taxonomy-collapsed.md`, `011-provider-chain-schema-mismatch.md`
- CLAUDE.md section: "Event Types (Pattern: AGGREGATE.ACTION.STATUS)" — unchanged, but event names would need to speak the chosen vocab.
- Archived SQL migration: `database/archived-sql-migrations/013_agent_configs.sql`, `database/archived-sql-migrations/014_provider_diagnostics.sql`
