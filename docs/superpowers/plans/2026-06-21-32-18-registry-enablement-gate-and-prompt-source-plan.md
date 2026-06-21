# Story 32-18 — Agent Registry Enablement Gate + Epic-27 Prompt Source (amends 32-2)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every task writes tests
> before implementation.

**Date:** 2026-06-21 · **Sequence:** step **E** of the Epic-32 pivot (re-plan §4)

**Goal:** Amend the **shipped** 32-2 registry/resolver so it (1) enforces the per-tenant **enablement
gate** in selection and resolution — a public persona NOT enabled for the principal is not selectable
(`AGENT.SELECT.NOT_ENABLED`, 409) and not resolvable (degrade to the enabled default); (2) returns the
`enabled(public) ∪ own-private` visible set; (3) rewrites `GetSystemDefaultPublicAsync` to return the
tenant's **enabled default persona** (cross-role, since `Agent.Role` is now nullable) and fail loud if
nothing is enabled; and (4) sources a **persona's** system/role prompt from the **Epic 27** store keyed
`(principal, role, action)` in `MaterialiseAsync` (custom-agent prompts stay 32-17's branch).

**This story is a logic amendment only — NO new entity, NO new table, NO new migration.** It consumes
32-16's `ITenantAgentEnablementReader` (async `IsEnabledForPrincipalAsync` / `ListEnabledPublicAgentIdsAsync` /
`GetEnabledDefaultPersonaIdAsync`), 32-15's `IPersonaPromptResolver` seam + persona seeder +
`DefaultPersonaName`, and 32-17's `ICustomAgentPromptResolver` seam. For the persona prompt it
**dispatches** to `IPersonaPromptResolver` (which reads Epic 27 internally) — it does **NOT** re-inline
`IPromptStoreService` and adds no prompt-resolution body of its own.

**Story file:** `docs/stories/epic-32/story-32-18/32-18-registry-enablement-gate-and-prompt-source.md`
**Design of record:** `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` (§3.1, §3.3, §3.5)
**Re-plan:** `docs/superpowers/plans/2026-06-20-epic-32-37-replan.md` (§1 disposition of 32-2)

**Tech stack:** .NET 9 / Elsa 3 in `apps/tamma-elsa` (central API `Tamma.Api`). Tests in
`apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/` (xUnit). Docker-bound suites run via
`sg docker -c "dotnet test ..."` (session docker group is stale; plain `dotnet build` needs no wrapper).
**`packages/api` is DELETED — all of this is C#.**

---

## Non-goals (YAGNI guard)

- **NO `TenantAgentEnablement` entity / enable-disable API / `AGENT.ENABLED/DISABLED` events.** Those
  are **32-16**. This story only *reads* `ITenantAgentEnablementReader`.
- **NO new table, column, migration, or DROP-list edit.** The nullable-`Agent.Role` migration + persona
  seeder are **32-15**; the `ConfigJson.prompts` column is **32-17**. `has-pending-model-changes` MUST
  stay clean (AC 11).
- **NO prompt-resolution body — dispatch only.** This story wires the `MaterialiseAsync` prompt-source
  PRECEDENCE only: public persona → `IPersonaPromptResolver` (32-15, which reads Epic 27 internally),
  private/custom → `ICustomAgentPromptResolver` (32-17). It implements **neither** body and does **not**
  re-inline `IPromptStoreService`. Both seams are called, never implemented here.
- **NO credential resolution.** The persona names provider+model; the key is **32-3**'s job at call time
  inside the call-LLM endpoint (**32-5**). The resolver must never touch or log a key.
- **NO change to the legacy `/api/v1/agents/*` JSONB path** or `AgentResolverService.ResolveAsync`.
- **NO style/voice variant** (that's the split-out 32-12-rewrite follow-on, not this).

---

## Current-state findings (verified against the shipped 32-2 spec + design of record)

| Seam | Where it is today (post-32-2/15/16/17) | How 32-18 uses it |
|---|---|---|
| **`CanUse(agent)`** | 32-2 `AgentRegistryService` — returns `true` for **any** public agent | Rewrite → async `CanUseAsync(agent, principal, ct)` ⇒ `agent.IsPublic ? await IsEnabledForPrincipalAsync(agent.Id, principal, ct) : agent.IsOwnedBy(principal)` |
| **`SelectForRoleAsync`** | 32-2 — upserts a selection for any visible target | Add enablement gate before upsert → `AGENT.SELECT.NOT_ENABLED` / 409 for disabled public persona |
| **`ListAsync`** | 32-2 — `public ∪ own-private` | Tighten → `enabled(public) ∪ own-private` (via batch `ListEnabledPublicAgentIdsAsync`); `?includeDisabled=true` (admin) returns full catalog + `enabled` flag |
| **`GetSystemDefaultPublicAsync(role)`** | 32-2 — matched `Agent.Role == role` | Rewrite → tenant's **enabled default persona** (cross-role; consumes `GetEnabledDefaultPersonaIdAsync`); null ⇒ resolver fails loud |
| **resolve precedence** | 32-2 — selection usable if `IsPublic` | Use enablement-aware `CanUseAsync`; degrade past a now-disabled selection with `AGENT.RESOLVE.DEGRADED` |
| **`MaterialiseAsync`** | 32-2 sourced prompt from agent config; 32-15 owns the `IPersonaPromptResolver` body | **Dispatch only**: public → `IPersonaPromptResolver` (32-15); private → `ICustomAgentPromptResolver` (32-17). No inline resolve body. |
| **`ITenantAgentEnablementReader`** | **32-16** — async `IsEnabledForPrincipalAsync(agentId, principal, ct)`, `ListEnabledPublicAgentIdsAsync(principal, ct)`, `GetEnabledDefaultPersonaIdAsync(principal, ct)` | Inject + consume; do NOT define |
| **`IPersonaPromptResolver`** | **32-15** — persona/public prompt body (reads Epic 27 `(principal, role, action)`, fail-loud) | Dispatch the public branch to it; do NOT re-inline Epic 27 |
| **`ICustomAgentPromptResolver`** | **32-17** — private-agent prompt seam | Dispatch the private branch to it; body not owned here |
| **`AgentEventTypes`** | 32-2 constants | Add `SelectNotEnabled`, `ResolveDegraded`, `NoEnabledDefault` |

**Key insight:** the only genuinely new code is the *enablement-aware predicate* (async, consuming the
32-16 reader) + the *prompt-source dispatch* (public → `IPersonaPromptResolver` 32-15, private →
`ICustomAgentPromptResolver` 32-17 — NO resolution body here) + three event constants + the endpoint
status mappings. No data model work, no inline Epic-27 resolve. Everything else is consuming existing
seams in the existing 32-2 services.

---

## Architecture

```
SelectForRoleAsync(role, agentId)
   target = ResolveTarget(agentId, principal)            -- public CP ∪ own-private; cross-tenant => null => 404
   if target.IsPublic && !await enablement.IsEnabledForPrincipalAsync(target.Id, principal, ct):
        emit AGENT.SELECT.NOT_ENABLED  -> throw TammaError("AGENT.SELECT.NOT_ENABLED") -> 409 agent_not_enabled
   else: upsert selection + AGENT.SELECTED_FOR_ROLE.SUCCESS

ResolveForRoleAsync(role, action)
   1+2 selection? -> agent -> if await CanUseAsync(agent, principal, ct): Materialise(...)   -- enabled public OR own-private
                          else (disabled): AGENT.RESOLVE.DEGRADED (WARN), fall through
   3   GetSystemDefaultPublicAsync(role) -> enabled default persona -> Materialise(..., "system-public")
            (configured DefaultPersonaName if enabled, else await enablement.GetEnabledDefaultPersonaIdAsync(principal))
   4   none -> AGENT.RESOLVE.FAILED + TammaError("AGENT.RESOLVE.NO_ENABLED_DEFAULT")   -- NO empty fallback

Materialise(agent, role, action, source)   -- PROMPT SOURCE = DISPATCH ONLY (no resolution body here)
   cfg = merge(DefaultAgentConfig.ForRole(role), agent.ActiveVersion.ConfigJson)     -- provider/model/params
   if agent.IsPublic:  systemPrompt = await _personaPrompts.ResolveAsync(principal, role, action)  -- IPersonaPromptResolver (32-15) -> Epic 27
   else:               systemPrompt = await _customAgentPrompts.ResolveAsync(agent, role, action)  -- ICustomAgentPromptResolver (32-17)
   return ResolvedAgentConfig { SystemPrompt, Provider, Model, AgentId, AgentVersion, Source }
        -- credential resolved LATER by 32-3 at call time from Provider; resolver NEVER touches a key
```

Per-mode (CLAUDE.md two-scoping rule): single-user gate/prompt keyed by `user_id`; SaaS by `tenant_id`;
no per-user enablement or per-user prompt layer; mode from `ITammaModeProvider`.

---

## Task breakdown

Order: T1 (event constants + endpoint status map) → T2 (`CanUseAsync` + selection gate) → T3 (resolve
precedence: degrade + enabled default) → T4 (prompt-source dispatch + action plumbing) →
T5 (listing `enabled ∪ own-private`) → T6 (mode matrix + no-credential + no-regression). T2 and T4 are
independent of each other (both depend on the seams existing); T3 depends on T2's `CanUseAsync`.

### T1 — Event constants + endpoint status mapping

**Scope:** Add the three DCB event-type constants and wire the endpoint error mappings. No behaviour yet.

**Files:** modify `Services/Agents/AgentEventTypes.cs` (`SelectNotEnabled = "AGENT.SELECT.NOT_ENABLED"`,
`ResolveDegraded = "AGENT.RESOLVE.DEGRADED"`, `NoEnabledDefault = "AGENT.RESOLVE.NO_ENABLED_DEFAULT"`);
modify `Endpoints/AgentEndpoints.cs` (map `TammaError("AGENT.SELECT.NOT_ENABLED")` → 409
`agent_not_enabled`; `TammaError("AGENT.RESOLVE.NO_ENABLED_DEFAULT")` → 404/409 with code).

**Tests (first):** `AgentEndpointsTests` — status-code mapping asserts (a fake registry that throws the
typed errors; assert 409 body `{ "error": "agent_not_enabled" }`).

**Acceptance:**
- [ ] Constants present; builds clean.
- [ ] Endpoint maps the typed errors to the documented status codes.

### T2 — `CanUseAsync` enablement-aware + selection gate (AC1–AC3, AC9)

**Scope:** Inject `ITenantAgentEnablementReader` (32-16). Rewrite `CanUse` → async
`CanUseAsync(agent, principal, ct)` calling `await _enablement.IsEnabledForPrincipalAsync(...)`. Add the
gate in `SelectForRoleAsync` before upsert (also async). Emit `AGENT.SELECT.NOT_ENABLED`.

**Files:** modify `Services/Agents/AgentRegistryService.cs`, `IAgentRegistryService.cs` (expose/keep
`CanUseAsync`; document).

**Tests (first):** `AgentRegistryServiceTests` (faking `ITenantAgentEnablementReader`) — enabled public →
select OK + `AGENT.SELECTED_FOR_ROLE.SUCCESS`; disabled public → `TammaError("AGENT.SELECT.NOT_ENABLED")`
+ exactly one `AGENT.SELECT.NOT_ENABLED` event; own-private → OK (implicit enable); cross-tenant private →
404; `CanUseAsync` truth table (public+enabled/disabled, own-private, other-tenant-private).

**Acceptance:**
- [ ] `CanUseAsync` no longer returns true for a public persona solely because it is public; calls `IsEnabledForPrincipalAsync`.
- [ ] Disabled-persona selection blocked with the typed error + event; own-private accepted.

### T3 — Resolve precedence: degrade-on-disabled + enabled default (AC2, AC5, AC9)

**Scope:** In `AgentResolverService.ResolveForRoleAsync`, use the enablement-aware `CanUseAsync`; a
selection pointing at a now-disabled persona degrades (WARN + `AGENT.RESOLVE.DEGRADED`) instead of
resolving it. Rewrite `GetSystemDefaultPublicAsync` → tenant's enabled default persona (via
`DefaultPersonaName` enabled, else 32-16's `GetEnabledDefaultPersonaIdAsync` — defined in 32-16,
consumed here). Null ⇒ fail loud `AGENT.RESOLVE.NO_ENABLED_DEFAULT`.

**Files:** modify `Services/Agents/AgentResolverService.cs`, `AgentRegistryService.cs`
(`GetSystemDefaultPublicAsync`).

**Tests (first):** `AgentResolverServiceTests` — (a) selection→disabled ⇒ resolves enabled default +
`AGENT.RESOLVE.DEGRADED`, disabled persona never materialised; (b) `DefaultPersonaName` enabled ⇒
returned; (c) `DefaultPersonaName` disabled but another is the enabled default ⇒ that one; (d) nothing
enabled + no own-private ⇒ `AGENT.RESOLVE.FAILED` + `TammaError("AGENT.RESOLVE.NO_ENABLED_DEFAULT")`,
**no blank config**.

**Acceptance:**
- [ ] Disabled selection degrades, never resolves.
- [ ] Enabled-default lookup is cross-role (no `Role==role` match); fails loud when nothing enabled.

### T4 — Prompt-source DISPATCH only + `action` plumbing (AC6, AC7; boundary with 32-15/32-17)

**Scope:** Wire the `MaterialiseAsync` prompt-source PRECEDENCE/dispatch ONLY — this story adds **no**
prompt-resolution body. Public persona → `await _personaPrompts.ResolveAsync(principal, role, action)`
(32-15's `IPersonaPromptResolver`, which reads Epic 27 internally, fail-loud). Private/custom →
`await _customAgentPrompts.ResolveAsync(agent, role, action)` (32-17's `ICustomAgentPromptResolver`).
**Do NOT call `IPromptStoreService` directly** (32-15 owns that body). Plumb `action` through
`ResolveForRoleAsync`/`ResolveForRoleAndPhaseAsync`. Persona `ConfigJson` prompt is ignored.

**Files:** modify `Services/Agents/AgentResolverService.cs` (`MaterialiseAsync` dispatch),
`IAgentResolverService.cs` (`action` param on the resolve methods).

**Tests (first):** `AgentResolverServiceTests` — public persona dispatches to the faked
`IPersonaPromptResolver` seam (assert `MaterialiseAsync` does NOT call `IPromptStoreService` directly,
nor read the persona `ConfigJson` prompt); a seam miss (`PROMPT_UNRESOLVED`) propagates (no empty/plain);
`action` absent ⇒ still passed through to the seam; a **private** agent dispatches to
`ICustomAgentPromptResolver`, NOT the persona seam (boundary proof).

**Acceptance:**
- [ ] Persona prompt is **dispatched** to `IPersonaPromptResolver` (32-15); no inline `IPromptStoreService` call here; fail-loud propagates.
- [ ] Custom-agent branch is dispatched to 32-17's `ICustomAgentPromptResolver`, not implemented here.

### T5 — Listing `enabled(public) ∪ own-private` (AC4)

**Scope:** `ListAsync` returns `enabled(public) ∪ own-private`; `?includeDisabled=true` (owner/admin
only) returns the full catalog with an `enabled` flag per row; members never see disabled public
personas. Use 32-16's batch `ListEnabledPublicAgentIdsAsync(principal, ct)` (one read → set membership;
avoids per-row async calls).

**Files:** modify `Services/Agents/AgentRegistryService.cs` (`ListAsync`), `IAgentRegistryService.cs`
(`AgentListFilter.IncludeDisabled`), `Endpoints/AgentEndpoints.cs` (List honours `?includeDisabled`,
admin-only).

**Tests (first):** `AgentRegistryServiceTests` + `AgentEndpointsTests` — member list = enabled∪own-private;
admin `?includeDisabled=true` = full catalog + `enabled` flags; member `?includeDisabled=true` ignored.

**Acceptance:**
- [ ] Member listing excludes disabled public personas; admin can see the full catalog with flags.

### T6 — Mode matrix, no-credential, no-regression (AC8, AC10, AC11, AC12)

**Scope:** Mode-parameterized principal (`user_id` vs `tenant_id`) for gate + prompt; no per-user
enablement layer. Assert no resolver/registry path resolves or logs a credential. Confirm the legacy
JSONB path + existing 32-2 tests stay green and `has-pending-model-changes` → none.

**Files:** extend `AgentRegistryServiceTests`/`AgentResolverServiceTests`; new
`AgentResolverServiceNoCredentialTests`.

**Tests (first):**
- `[Theory]` over `TammaMode.SingleUser`/`SaaS`: gate + prompt keyed by the right principal.
- No-credential: a resolve never invokes any `IProviderCredentialResolver`/cabinet seam, never logs a
  key; `ResolvedAgentConfig` carries `Provider`/`Model` but no key field.
- No-regression: existing 32-2 endpoint/resolver tests + legacy `/api/v1/agents/*` green;
  `dotnet ef migrations has-pending-model-changes` → none.

**Acceptance:**
- [ ] Mode matrix passes; no per-user enablement.
- [ ] No credential ever resolved/logged in the registry/resolver.
- [ ] Full suite green; zero pending model changes.

---

## Story order & dependencies

External prereqs (must land first): **32-2** (the services amended), **32-15** (persona seeder +
`Agent.Role` nullable + the `IPersonaPromptResolver` seam + `DefaultPersonaName`), **32-16**
(`ITenantAgentEnablementReader` — async `IsEnabledForPrincipalAsync` / `ListEnabledPublicAgentIdsAsync` /
`GetEnabledDefaultPersonaIdAsync`), **32-17** (`ICustomAgentPromptResolver`). Epic 27's `IPromptStoreService`
is reached **through** 32-15's `IPersonaPromptResolver`, not directly. Code to their interfaces; use fakes
until landed. Internal: T1 → (T2 ∥ T4) → T3 (needs T2) → T5 → T6.
Downstream consumer: **32-5** (call-LLM endpoint) composes the resolve order this story documents; it is
NOT a blocker.

## Verification

```bash
# build (no docker wrapper needed)
dotnet build apps/tamma-elsa/Tamma.sln
# tests (docker-bound suites need the sg wrapper; session docker group is stale)
sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Api.Tests/ --filter FullyQualifiedName~Agents"
# AC11 — no schema drift from this story
sg docker -c "dotnet ef migrations has-pending-model-changes --project apps/tamma-elsa/src/Tamma.Data --context TenantDbContext"
sg docker -c "dotnet ef migrations has-pending-model-changes --project apps/tamma-elsa/src/Tamma.Data --context ControlPlaneDbContext"
# boundary check — this story adds NO entity/migration of its own
git -C apps/tamma-elsa diff --name-only | grep -i 'Migrations/' && echo "UNEXPECTED migration — 32-18 adds none" || echo "OK: no migration"
# credential-safety grep — registry/resolver hold no key
grep -rn "ApiKey\|IProviderCredentialResolver\|cabinet" apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentResolverService.cs apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentRegistryService.cs || echo "OK: no credential in registry/resolver"
```

## Risks

- **Double-implementation (HIGH):** the easiest failure mode is re-creating the `TenantAgentEnablement`
  entity, the persona seeder, or re-inlining the Epic-27 persona-resolve / custom-prompt body here.
  Mitigation: this story adds NO entity/migration; it injects `ITenantAgentEnablementReader` (32-16),
  dispatches to 32-15's `IPersonaPromptResolver` and 32-17's `ICustomAgentPromptResolver` (calls, never
  implements), and uses the 32-15 persona seeder. The `git diff` migration check +
  `has-pending-model-changes` + a test asserting no direct `IPromptStoreService` call are the guard.
- **A disabled persona still resolves (HIGH):** if the gate is applied on selection but not on resolve.
  Mitigation: centralise the predicate in `CanUseAsync`; both `SelectForRoleAsync` and the resolve
  precedence call it; T3 tests the degrade path explicitly.
- **Empty/plain prompt fallback (HIGH):** mitigation — persona branch dispatches to 32-15's
  `IPersonaPromptResolver` (whose body resolves Epic 27 tenant→system→error and throws `PROMPT_UNRESOLVED`/
  `NoPromptError` on a miss); a miss propagates; T4 asserts the seam is invoked (not an inline resolve)
  and the miss propagates. `feedback_resolution_no_empty_fallback`.
- **Credential leak into resolver (HIGH):** mitigation — dedicated no-credential test (T6); the resolver
  carries only `Provider`/`Model`; credential is 32-3 at call time.
- **Seam-shape drift (MEDIUM, mostly RESOLVED):** 32-16 ships the read seam `ITenantAgentEnablementReader`
  with async `IsEnabledForPrincipalAsync` + batch `ListEnabledPublicAgentIdsAsync` + `GetEnabledDefaultPersonaIdAsync`
  (the exact signatures this story calls); `ListAsync` uses the batch read. Remaining open item: 32-15
  may expose `DefaultPersonaName` as config vs a CP row — code to whichever 32-15 ships.
- **Dependency timing (MEDIUM):** 32-15/16/17 land before this (sequence B/C/D before E). Mitigation:
  interfaces + fakes; this story is the integrator, gated behind them.
