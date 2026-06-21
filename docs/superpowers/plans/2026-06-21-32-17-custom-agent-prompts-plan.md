# Story 32-17 — Custom-Agent Prompts (`ConfigJson.prompts` + resolver prompt-source branch)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every task writes tests
> before implementation.

**Date:** 2026-06-21

**Goal:** Make the locked-model **rule 5 — custom prompts ⇔ custom agent** real. A tenant that wants
different prompts authors a private (custom) `Agent` (32-1) carrying its OWN prompts inside the
existing `AgentVersion.ConfigJson` jsonb (`prompts` block). Add (a) the optional `prompts` schema +
typed `AgentPromptSet`, (b) the **public-must-be-empty** validation invariant (rule 4: personas are
prompt-free), and (c) the **custom/private branch** of `AgentResolverService.MaterialiseAsync` that
sources prompts from the agent itself — fail-loud, never empty/plain, never falling through to the
Epic 27 store. **No new entity, no new column** — a JSON sub-object + validation + one resolver leg.

**Story file:** `docs/stories/epic-32/story-32-17/32-17-custom-agent-prompts.md`
**Design of record:** `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` (§3.0, §3.1, §3.2)
**Sequence:** step **D** (after 34-11/A, 32-15/B, 32-16/C; before 32-18/E and 32-4/32-5 rewrite/F)

**Tech stack:** .NET 9 / Elsa 3 in `apps/tamma-elsa` (`Tamma.Api` services + `Tamma.Data` EF). Tests
in `apps/tamma-elsa/tests/Tamma.Api.Tests/` (xUnit). Docker-bound suites run via
`sg docker -c "dotnet test ..."` (session docker group is stale; plain `dotnet build` needs no
wrapper). **`packages/api` is DELETED — there is no TypeScript path; all of this is C#.**

---

## Non-goals (YAGNI guard)

- **NO new entity / table / column.** A custom agent is *just* a `Visibility=Private` `Agent` (32-1).
  `prompts` lives inside the existing `ConfigJson` jsonb. Therefore **no `DbSet`, no
  `ControlPlaneDbContext` change, no `ControlPlaneDbContextModelTests` strict-list edit, and no
  Program.cs startup-reset DROP-list edit** (those are required only when adding a *table*).
- **NO persona/public → Epic 27 prompt branch.** That `else` leg of `MaterialiseAsync` is **32-15**.
  This story defines the branch selector + the `if` (custom) leg (the `ICustomAgentPromptResolver` seam)
  only, and calls 32-15's **`IPersonaPromptResolver`** seam for the `else`. Do NOT re-implement the Epic 27 leg.
- **NO enablement gate.** Selection gating (`SelectForRoleAsync`/`ResolveUsableAgentAsync`) is **32-18**.
- **NO credential / gate / cost changes.** 32-3 / 32-4 / 32-9 / 34 are untouched — only the prompt-source
  leg of agent resolution.
- **NO per-user prompt override layer.** Members run the tenant's custom agents as-authored (CLAUDE.md:
  per-user personalization is intentionally NOT a feature).
- **NO new providers, NO change to the Epic 27 store contents or schema.** The custom agent is the
  escape hatch *around* the store, not an edit to it.

---

## Current-state findings (from 32-1, design §3.2)

| Seam | Where it is | How 32-17 uses it |
|---|---|---|
| **`Agent` / `AgentVersion`** | `Tamma.Data/Entities/Agent.cs`, `AgentVersion.cs` (32-1) — `Visibility` enum, `ConfigJson` jsonb, versioned. | Unchanged. `prompts` is a sub-object of the existing `ConfigJson`; `Visibility` drives the public-must-be-empty rule and the resolver branch. |
| **`ck_agents_visibility_ownership` CHECK + partial indexes** | `TammaModelConfiguration.cs` (32-1). | **Reused unchanged.** Custom agent = private agent; XOR/ownership/index discipline already correct. |
| **`AgentConfigValidator`** | `Tamma.Api/Services/Agents/AgentConfigValidator.cs` (32-1) — provider regex, budget range, ReDoS/prototype-pollution guards; called before every write. | **Extended:** add a `visibility` discriminator + the `prompts` rules (public-must-be-empty, role:action key, non-empty template). Reuse the existing prototype-pollution guard. |
| **`AgentResolverService.MaterialiseAsync`** | `Tamma.Api/Services/Agents/AgentResolverService.cs` (32-2; persona leg added by 32-15 via `IPersonaPromptResolver`). | **Extended (joint with 32-15):** add the custom/private `if` leg as the `ICustomAgentPromptResolver` seam + the one branch selector; call 32-15's `IPersonaPromptResolver` seam in the `else`. |
| **Role/action taxonomy** | `Tamma.Core/Agents/AgentRole.cs`, `RolePhaseMap.cs`, `AgentAction` (Epic 27). | Validate `byRoleAction` keys (`"<role>:<action>"`) against it on write. |
| **Mode** | `Tamma.Api/Services/PromptStore/TammaMode.cs` — `ITammaModeProvider`. | Owner-column derivation (32-1) + per-mode ownership of custom prompts. |
| **No-empty-fallback** | `.dev/findings/feedback_resolution_no_empty_fallback`. | The custom branch is `byRoleAction → system → ERROR`; never empty/plain, never fall through. |

**Key insight:** the only genuinely new code is `AgentPromptSet` (a record), the validator's
`visibility`-conditional rules, and ONE `if/else` leg in `MaterialiseAsync`. Everything else reuses
32-1/32-2 seams. **Coordinate the `MaterialiseAsync` edit with 32-15** so the two legs share one
conditional.

---

## Architecture

```
WRITE PATH (create / publish version):
  AgentEndpoints.Create|PublishVersion
      └─ AgentConfigValidator.Validate(configJson, visibility)     [32-1 extended — THIS story]
            ├─ public + populated prompts  → reject PROMPTS_NOT_ALLOWED_ON_PUBLIC   (rule 4)
            └─ populated prompts           → validate keys/templates (AC3)
      (+ optional DB CHECK ck_agents_public_no_prompts as backstop)

READ / RESOLVE PATH (managed run, via 32-5 call-LLM):
  AgentResolverService.MaterialiseAsync(resolved, role, action)
      └─ promptSet = (Visibility==Private) ? read ConfigJson.prompts : null
         if promptSet non-empty:                         ── CUSTOM branch (THIS story 32-17) ──
             resolved.SystemPrompt = await _customAgentPrompts.ResolveAsync(agent, role, action)
                 (ICustomAgentPromptResolver: byRoleAction["role:action"] ?? system; blank → throw
                  CustomPromptUnresolvedException → 32-5 FailureCode)   [fail-loud]
             PromptSource = CustomAgent
         else:                                            ── PERSONA branch (32-15) ──
             resolved = await _personaPrompts.ResolveAsync(...)   (IPersonaPromptResolver → Epic 27 store)
             PromptSource = Epic27Store
```

Per-mode ownership (CLAUDE.md two-scoping-model rule): single-user = the sole user authors custom
agents (`OwnerUserId`); SaaS = `tenant_owner`/`tenant_admin` author them (`OwnerTenantId`), members
run them as-authored (no per-user override). Mode from `ITammaModeProvider`.

---

## Task breakdown

Order: **T1** (records/enum/exception) → **T2** (validator invariant) → **T3** (resolver custom
branch, joint with 32-15) → **T4** (provenance tag + no-leak) → **T5** (optional DB CHECK). T1 is a
prerequisite for T2/T3. T5 is optional and last.

### T1 — Typed models: `AgentPromptSet`, `AgentPromptSource`, `CustomPromptUnresolvedException`

**Scope:** The data shapes + the typed fail-loud signal. No behaviour.

**Files (new):**
- `Tamma.Api/Services/Agents/AgentPromptSet.cs` — `record { string? System; IReadOnlyDictionary<string,string>? ByRoleAction; bool IsEmpty }`.
- `Tamma.Api/Services/Agents/AgentPromptSource.cs` — `enum { Epic27Store, CustomAgent }`.
- `Tamma.Api/Services/Agents/CustomPromptUnresolvedException.cs` — carries `AgentId` + the `<role>:<action>` key (no template body).
- `Tamma.Api/Services/Agents/ICustomAgentPromptResolver.cs` — the custom/private prompt seam `Task<RenderedPrompt> ResolveAsync(Agent agent, string role, string? action, CancellationToken ct)`.

**Tests (first):** `tests/Tamma.Api.Tests/Agents/AgentPromptSetTests.cs` — deserialize from a
`ConfigJson` string: absent `prompts` → null set; full block → both populated; `system`-only;
`byRoleAction`-only; `prompts: {}` → `IsEmpty == true`. Round-trip equality.

**Acceptance:**
- [ ] `AgentPromptSet` deserializes all four shapes; `IsEmpty` correct for empty/null.
- [ ] Builds clean; no analyzer warnings.

### T2 — `AgentConfigValidator` invariant: public-must-be-empty + prompts shape (AC2, AC3)

**Scope:** Extend the 32-1 validator with a `visibility` discriminator and the `prompts` rules. Runs
before every write (create + publish-version).

**Files:** modify `Tamma.Api/Services/Agents/AgentConfigValidator.cs` (add
`Validate(configJson, AgentVisibility visibility)`; keep the existing 32-1 overload/behaviour for the
shape rules). Wire the new overload into the `AgentEndpoints` create + publish paths (they already
call the validator — pass `visibility`).

**Rules added:**
- `Visibility==Public` + non-empty `prompts` → `PROMPTS_NOT_ALLOWED_ON_PUBLIC` (reject, 400, no row, no event).
- non-empty `prompts`: each `byRoleAction` key parses `"<role>:<action>"` via `RolePhaseMap.NormalizeRole` + `AgentAction` taxonomy (else `PROMPTS_INVALID_KEY`); each template non-empty after trim (else `PROMPTS_EMPTY_TEMPLATE`); prototype-pollution keys rejected (reuse 32-1 guard → `PROMPTS_PROTO_POLLUTION`).
- a `prompts` object that parses but is wholly empty is allowed for private agents (treated as absent).

**Tests (first):** `tests/Tamma.Api.Tests/Agents/AgentConfigValidatorPromptsTests.cs` —
public+populated rejected (create & publish); private+populated accepted; private+absent accepted;
invalid key / empty template / proto-pollution key each rejected with the right code; public+empty
accepted.

**Acceptance:**
- [ ] Public + populated `prompts` rejected on BOTH create and publish endpoints.
- [ ] All AC3 content rules enforced with the documented error codes.
- [ ] Existing 32-1 validator tests still pass (shape rules unchanged).

### T3 — `MaterialiseAsync` custom/private prompt-source branch (AC4, AC5) — joint with 32-15

**Scope:** Add the single documented prompt-source conditional + ship the `ICustomAgentPromptResolver`
seam (`CustomAgentPromptResolver` impl). **This story owns the `if` (custom) leg + the seam + the
selector; 32-15 owns the `else` (persona/Epic 27) leg via `IPersonaPromptResolver`.** Coordinate so
there is exactly one `if/else`.

**Files:** new `Tamma.Api/Services/Agents/CustomAgentPromptResolver.cs` (the `ICustomAgentPromptResolver`
impl); modify `Tamma.Api/Services/Agents/AgentResolverService.cs`; `IAgentResolverService.cs` (surface
`PromptSource` on the resolved config if 32-15 hasn't already).

**Logic:**
```
promptSet = (resolved.Visibility == Private) ? TryReadPromptSet(resolved.ConfigJson) : null;
if (promptSet is { IsEmpty: false }) {                     // CUSTOM branch (32-17)
    resolved = resolved with {                             // ICustomAgentPromptResolver: byRoleAction -> system -> ERROR
        SystemPrompt = (await _customAgentPrompts.ResolveAsync(resolved.Agent, role, action, ct)).Text,
        PromptSource = CustomAgent };                       // resolver throws CustomPromptUnresolvedException on no-resolve (fail-loud)
} else {                                                   // PERSONA branch (32-15)
    resolved = resolved with {
        SystemPrompt = (await _personaPrompts.ResolveAsync(principal, role, action, ct)).Text,   // 32-15 IPersonaPromptResolver
        PromptSource = Epic27Store };
}
```

**Tests (first):** `tests/Tamma.Api.Tests/Agents/AgentResolverServicePromptBranchTests.cs` +
`CustomAgentPromptResolverTests.cs` —
- both present → `byRoleAction` wins;
- only `system`, non-matching `(role,action)` → `system` used;
- neither matches → `CustomPromptUnresolvedException` AND the persona seam is **never** called (assert fake `IPersonaPromptResolver` not invoked);
- empty `prompts` block → custom branch NOT entered → 32-15's `IPersonaPromptResolver.ResolveAsync` invoked, `PromptSource==Epic27Store`;
- no-empty-fallback: no path returns an empty/plain prompt for a custom agent.

**Acceptance:**
- [ ] Custom branch resolution order is `byRoleAction → system → ERROR` (inside `ICustomAgentPromptResolver`).
- [ ] `CUSTOM_PROMPT_UNRESOLVED` is fail-loud; Epic 27 store NEVER consulted on the custom path.
- [ ] Empty-prompts custom agent delegates to 32-15's `IPersonaPromptResolver` branch.
- [ ] Exactly ONE prompt-source conditional in `MaterialiseAsync` (grep; co-verified with 32-15).

### T4 — Provenance tag + no-leak (AC7)

**Scope:** The resolved config carries `PromptSource`. Ensure the managed run (32-5) can tag
`promptSource="custom-agent"|"epic27-store"` + the `<role>:<action>` key, and that **no template body**
enters events/logs. (32-5 owns the event emission; this story guarantees the *resolved-config carries
the provenance* and that the validator/resolver logs never include the body.)

**Files:** confirm `PromptSource` on the resolved-config record (T3); audit `AgentResolverService` +
`AgentConfigValidator` log statements — log only the source label + the key, never the template body
or the full `ConfigJson`.

**Tests (first):** extend `AgentResolverServicePromptBranchTests` — captured-log assertion: a resolved
custom prompt logs `promptSource=custom-agent` + key but no body; a fake `IEventRepository` (if the
resolver emits any resolution event) carries no template body in `Data`/`Tags`.

**Acceptance:**
- [ ] `PromptSource` set correctly on both branches.
- [ ] No template body appears in any log line or emitted event from the validator/resolver.

### T5 — (Optional) DB CHECK backstop `ck_agents_public_no_prompts`

**Scope:** Belt-and-suspenders DB enforcement of public-must-be-empty. **Optional** — prefer
validator-only (T2) to keep the migration footprint at zero. Only add if a reviewer wants a structural
backstop.

**Files (if added):** modify `Tamma.Data/TammaModelConfiguration.cs` (additive CHECK on the existing
`agent_versions`/`agents` config — a jsonb predicate asserting public rows have no populated
`prompts`); new additive migration `Migrations/ControlPlane/<ts>_AddPublicAgentNoPromptsCheck.cs`
(CHECK only — **no column, no table**).

**Tests (first):** integration (Postgres fixture, `sg docker`): inserting a public `agent_versions`
row with populated `prompts` violates the CHECK; `dotnet ef migrations has-pending-model-changes
--context ControlPlaneDbContext` reports none after the migration.

**Acceptance (only if T5 is taken):**
- [ ] CHECK rejects public+populated-prompts at the DB layer.
- [ ] `has-pending-model-changes` reports none; additive amendment to the single snapshot (not a branch).
- [ ] No `ControlPlaneDbContextModelTests` strict-list edit needed (no new entity), and no Program.cs
      startup-reset DROP-list edit (no new table).

---

## Story order & dependencies

External prereqs (must land first): **32-1** (Agent entity, validator, CHECK, indexes, `ConfigJson`),
**32-15** (persona/Epic-27 `MaterialiseAsync` leg + the `IPersonaPromptResolver` seam — **co-author of
the conditional**; land it before T3). **Sequence:** A (34-11) → B (32-15) → C (32-16) → **D (this)** →
E (32-18) → F (32-4/32-5). Internal: T1 → T2 ∥ T3 → T4 → (optional) T5. Downstream consumer: **32-5**
maps `CUSTOM_PROMPT_UNRESOLVED` to a typed `FailureCode` and emits the `promptSource`-tagged events
(not a blocker for this story).

> **EF parallel-migration hazard:** stories are implemented **sequentially** on one migration snapshot.
> If T5 is taken, it **amends** the existing snapshot (additive CHECK), it does not branch it. Most
> likely this story ships with **zero migration** (validator-only enforcement).

## Verification

```bash
# build (no docker wrapper needed)
dotnet build apps/tamma-elsa/Tamma.sln
# tests (docker-bound suites need the sg wrapper; session docker group is stale)
sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Api.Tests/ --filter FullyQualifiedName~Agents"
# AC4 single-conditional check: exactly one prompt-source branch in MaterialiseAsync
grep -n "PromptSource\|_personaPrompts\|IPersonaPromptResolver\|_customAgentPrompts\|ICustomAgentPromptResolver\|CustomPromptUnresolved" apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentResolverService.cs
# (only if T5 taken) confirm no pending model changes after the additive CHECK
sg docker -c "dotnet ef migrations has-pending-model-changes --context ControlPlaneDbContext --project apps/tamma-elsa/src/Tamma.Data"
```

## Risks

- **Double-implemented `MaterialiseAsync` branch (32-15 ∥ 32-17):** High. Mitigation: one documented
  `if/else`; this story owns the `if` (`ICustomAgentPromptResolver`), 32-15 the `else`
  (`IPersonaPromptResolver`); land 32-15 first; a test asserts each branch does not invoke the other's
  seam; grep confirms one conditional.
- **Silent fall-through to Epic 27 / empty prompt (no-empty-fallback breach):** High. Mitigation: a
  non-empty `prompts` block commits to the custom branch; `CustomPromptUnresolvedException` is the only
  no-resolve outcome; test asserts the persona seam is NOT consulted and no empty/plain prompt returns.
- **Public persona smuggles prompts:** High. Mitigation: `PROMPTS_NOT_ALLOWED_ON_PUBLIC` at create AND
  publish; optional DB CHECK (T5) as backstop; tested on both endpoints.
- **Prompt body leaks into events/logs:** Medium. Mitigation: log/tag only the source label + the
  `<role>:<action>` key; captured-log + fake-repo assertions; extends 32-1's no-raw-ConfigJson rule.
- **`byRoleAction` key taxonomy drift from Epic 27:** Medium. Mitigation: validate keys via the same
  `RolePhaseMap.NormalizeRole` / `AgentAction` taxonomy on write; reuse 32-1's normalization path.
- **Unnecessary migration:** Low. Mitigation: prefer validator-only (zero migration); T5's CHECK is
  optional and additive on the single snapshot if taken.
