# Story 27-15: AgentRole/AgentAction Taxonomy + RolePhaseMap Rebuild

## Story

As the Tamma platform, I need a single typed `(role, action)` taxonomy owned by
`RolePhaseMap`, so that prompts, conventions, agent resolution, provider
routing, and workflow dispatch all key off the same canonical vocabulary and
cannot drift.

Canonical design: see `docs/superpowers/specs/2026-05-18-role-action-taxonomy-and-resolution-design.md` (SPEC §3, §4).

## Priority

P0 (Critical) — foundation for 27-8, 27-9, 27-13, 27-16, 27-18, 27-19.

## Dependencies

None (pure code-defined types; no DB, no Epic 17 dependency).

## Acceptance Criteria

1. `AgentRole` enum exists with exactly: `Developer, Tester, Security, Devops,
   Architect, ProductOwner, SeniorDeveloper, TechWriter`.
2. `AgentAction` enum exists as the **union of all distinct action tokens** in
   SPEC §4 (~70 distinct values). Shared tokens (`context-scan`, `code-review`,
   `plan-review`, `write-tests`) are single enum values reused across roles.
3. `AgentRole.ToWire()` / `AgentAction.ToWire()` return the canonical
   kebab/snake string (`PlanSystemDesign` → `"plan-system-design"`,
   `ProductOwner` → `"product_owner"`). One mapping table, one place.
4. `AgentRole.Parse(string)` / `AgentAction.Parse(string)`: apply
   `RolePhaseMap.LegacyRoleAliases` first (`"implementer"`→`Developer`,
   `"analyst"`→`ProductOwner`), then exact match; throw `TammaError`
   (code `INVALID_ROLE` / `INVALID_ACTION`) on unknown.
5. Round-trip invariant holds for every enum value: `Parse(x.ToWire()) == x`.
6. Wire format remains a primitive string — no `JsonConverter`, no change to
   Elsa serialized dispatch payloads or persisted workflow state.
7. `RolePhaseMap` is rebuilt on the enums:
   - `ValidRoles` / `ValidActions` derive from `Enum.GetValues<>()`.
   - The per-role action set from SPEC §4 replaces `s_eligibleRoles`.
   - `IsRoleEligibleForPhase(role, action)` returns "is `action` in `role`'s
     SPEC §4 set".
   - `GetPrimaryActionForRole`, normalization, legacy aliases keep current
     observable behaviour, keyed off enums.
8. The four existing consumers (`AgentResolverService`, `ProviderChainResolver`,
   `AgentEndpoints`, `DefaultAgentConfig`) compile unchanged and exhibit
   identical observable behaviour (regression tests pass).

## Technical Context

- Files: create `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentRole.cs`,
  `AgentAction.cs`; modify
  `apps/tamma-elsa/src/Tamma.Api/Services/Agents/RolePhaseMap.cs`.
- The per-role action lists are the authority for codegen (Story 27-16) and the
  drift test (Story 27-17). They are reproduced verbatim from SPEC §4 in the
  enum/RolePhaseMap source as the single code-side source of truth.
- No DB tables. Roles/actions stay code-defined (SPEC §2).

## Estimate

8 hours.
