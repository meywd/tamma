# Story 32-15 — Persona Reframe + Seeding (Role-nullable cross-role personas)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every task writes tests
> before implementation.

**Date:** 2026-06-21

**Goal:** Amend the **shipped** 32-1 `Agent`/`AgentVersion` entity model so public agents become
named, cross-role **PERSONAS** (`claude`/`gemini`/`codegpt`) instead of per-role `tamma-<role>` rows.
Concretely: make `Agent.Role` nullable (NULL for public personas), swap the public unique index
`(Name, Role)` → `(Name)`, rewrite `AgentEntitySeeder` to seed named cross-role personas each with an
**explicit** `provider`+`model` (no longer leaning on `DefaultAgentConfig.ForRole` → `claude-sonnet-4`),
rewrite `GetSystemDefaultPublicAsync(role)` to return the platform-configured **default persona**
(`DefaultPersonaName`, role-independent) and delete the per-role ambiguity warning, and wire
`AgentResolverService.MaterialiseAsync` so the persona's system/role prompt comes from the **Epic 27
prompt store** keyed `(principal, role, action)` — personas are prompt-free.

**Story file:** `docs/stories/epic-32/story-32-15/32-15-persona-reframe-and-seeding.md`
**Design of record:** `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` (§3.0–§3.1)
**Re-plan:** `docs/superpowers/plans/2026-06-20-epic-32-37-replan.md` (§1, §4 sequence step B)

**Tech stack:** .NET 9 / Elsa 3 in `apps/tamma-elsa` (`Tamma.Data` entities + EF migrations, `Tamma.Api`
services, `Tamma.ElsaServer` seeding host). Tests live in `apps/tamma-elsa/tests/Tamma.Api.Tests/`
(xUnit). Docker-bound suites run via `sg docker -c "dotnet test ..."` (session docker group is stale;
plain `dotnet build` needs no wrapper). **There is no TypeScript path — all C#.**

---

## Non-goals (YAGNI guard)

- **NO new table.** This is a schema *amendment* of the existing 32-1 `agents`/`agent_versions`
  tables (nullable `Role` + index swap). Nothing is added to the `Program.cs` startup-reset DROP list.
- **NO per-tenant enablement.** `TenantAgentEnablement` + `IsEnabledForPrincipal` is **32-16**. This
  story seeds personas; it does not gate which tenant sees which.
- **NO custom-agent (private) prompt branch.** `ConfigJson.prompts` + the private path in
  `MaterialiseAsync` is **32-17**. This story implements ONLY the public/persona prompt branch and
  leaves a marked seam for the private one.
- **NO registry enablement gate.** `CanUse`/`SelectForRoleAsync`/`ResolveUsableAgentAsync` enablement
  is **32-18** (amends 32-2). This story rewrites only `GetSystemDefaultPublicAsync` + the prompt source.
- **NO new provider, NO pricing logic.** The personas' `(provider, model)` must already be
  `IProviderPricingService.IsKnown` (owned by **34-11**). This story only *consumes* `IsKnown`.
- **NO new REST routes.** `DefaultPersonaName` is config; persona CRUD already exists from 32-1.
- **NO baseline rewrite / migration branch.** Sequential implementation on the single linear snapshot.

---

## Current-state findings (verify in-repo before coding)

| Seam | Where it is today (32-1/32-2) | How 32-15 amends it |
|---|---|---|
| **`Agent` entity** | `Tamma.Data/Entities/Agent.cs` — `Role` is `string` (required identity). | `Role` → `string?` (NULL for public personas). |
| **EF model config** | `Tamma.Data/TammaModelConfiguration.cs` — `Role.IsRequired().HasMaxLength(64)`; `IX_agents_public_name_role` on `(Name, Role) WHERE Visibility=0`. | Drop `.IsRequired()`; replace public index with `IX_agents_public_name` on `(Name) WHERE Visibility=0`. Private indexes unchanged. |
| **Seeder** | `Tamma.ElsaServer/AgentEntitySeeder.cs` — 8 `tamma-<role>` rows on one provider chain; `ConfigJson` omits `model`; insert-missing-only keyed by `(Name, Role)`. | Rewrite to N named cross-role personas (`claude`/`gemini`/`codegpt`), `Role=NULL`, explicit `provider`+`model`, no prompts; idempotency keyed by `Name`; legacy disposition (AC11). |
| **Default lookup** | `Tamma.Api/Services/Agents/AgentRegistryService.cs` — `GetSystemDefaultPublicAsync` matches `Agent.Role==role`, warns on >1. | Return `DefaultPersonaName` persona, role-independent; delete the ambiguity warning; fail loud if absent. |
| **Materialise** | `Tamma.Api/Services/Agents/AgentResolverService.cs` — `MaterialiseAsync` merges `ConfigJson` onto `DefaultAgentConfig.ForRole(role)`, stamps `AgentId`/`AgentVersion`; prompt from config/default. | Keep merge + stamp; **prompt source for personas = the new `IPersonaPromptResolver` seam** (reads Epic 27 `(principal, role, action)`), fail loud; private branch = `ICustomAgentPromptResolver` seam for 32-17. |
| **Cost basis** | `Tamma.Api/Services/Providers/IProviderPricingService` — `IsKnown(provider, model)` / `Compute(...)`. (34-11 promotes the frozen table to a DB entity behind the same seam.) | Seeder validates each persona's `(provider, model)` is `IsKnown` before write. |
| **Prompt store** | Epic 27 `IPromptStore` — `(principal, role, action)` resolution, tenant → system → error. | New prompt source for personas in `MaterialiseAsync`. |
| **Mode** | `Tamma.Api/Services/PromptStore/TammaMode.cs` — `ITammaModeProvider` (SingleUser | SaaS). | Principal derivation `(tenantId XOR userId)` for the Epic 27 key. |
| **Model contract test** | `tests/Tamma.Api.Tests/Epic28/ControlPlaneDbContextModelTests.cs` — strict `BeEquivalentTo`. | Update to reflect nullable `Role` + renamed index. |

**Key insight:** the entity model is sound — the only genuinely new code is the *nullable-Role migration*,
the *index swap*, the *seeder rewrite* (named personas + explicit model + legacy disposition), the
*default-persona resolution rewrite*, and the *prompt-source wiring* in `MaterialiseAsync`. No new table,
no new routes, no new provider.

---

## Architecture

```
AgentEntitySeeder (CP, insert-missing-only)
   seeds:  claude  -> { provider: anthropic, model: claude-sonnet-4-20250514 }   Role=NULL, Public
           gemini  -> { provider: google,    model: gemini-2.5-pro }             Role=NULL, Public
           codegpt -> { provider: openai,     model: gpt-4o }                     Role=NULL, Public
   guard:  IProviderPricingService.IsKnown(provider, model)  (else WARN + skip)   [34-11]
   event:  AGENT.CREATED.SUCCESS per new persona; skip => no event

Resolution path (consumed by 32-5 lynchpin):
   GetSystemDefaultPublicAsync(role)
        -> persona = GetPublicByName(DefaultPersonaName)   role-INDEPENDENT
        -> null => FAIL LOUD (AGENT_DEFAULT_PERSONA_MISSING)
   MaterialiseAsync(agent, role, action, principal)
        -> merge ConfigJson onto DefaultAgentConfig.ForRole(role)   (kept)
        -> stamp AgentId / AgentVersion                              (kept)
        -> if Public (persona): SystemPrompt = IPersonaPromptResolver.ResolveAsync(principal, role, action)
                                 (the seam THIS story ships; reads Epic 27, fail-loud PROMPT_UNRESOLVED)
           else (private):       SEAM for 32-17 (ICustomAgentPromptResolver — custom agent's own prompts)
```

Per-mode ownership (CLAUDE.md two-scoping-model): personas are platform-global `Visibility='public'`
(`PlatformOwnerAccess` to edit, NOT `OwnerAccess`) in both modes; the Epic 27 prompt key principal is
the sole user (`userId`) in single-user and the tenant (`tenantId`) in SaaS — never a per-user layer in
SaaS. Mode from `ITammaModeProvider`.

---

## Task breakdown

Order: T1 (entity + migration) → T2 (seeder rewrite) → T3 (default-persona resolution) →
T4 (MaterialiseAsync prompt source) → T5 (model-contract test + legacy disposition + wiring).
T1 must land first (everything reads the nullable column). T2/T3/T4 are independent given T1.

### T1 — Nullable `Role` + public-index swap (entity + migration)

**Scope:** Make `Agent.Role` nullable; replace the public unique index `(Name, Role)` → `(Name)`.
Single additive migration on the existing 32-1 snapshot.

**Files:** modify `Tamma.Data/Entities/Agent.cs` (`Role` → `string?`); modify
`Tamma.Data/TammaModelConfiguration.cs` (drop `Role.IsRequired()`; `IX_agents_public_name_role` →
`IX_agents_public_name`); new `Migrations/ControlPlane/<ts>_PersonaReframeRoleNullable.cs`
(+ `.Designer.cs`, snapshot).

**Migration DDL (generated, verify):**
```sql
ALTER TABLE agents ALTER COLUMN "Role" DROP NOT NULL;
DROP INDEX  "IX_agents_public_name_role";
CREATE UNIQUE INDEX "IX_agents_public_name" ON agents ("Name") WHERE "Visibility" = 0;
```

**Tests (first):** `tests/Tamma.Api.Tests/Agents/` (Postgres fixture) —
- insert a `Visibility='public'`, `Role=NULL` persona; read back; `Role` is null; `ck_agents_visibility_ownership` still passes.
- two public agents may NOT share a `Name` (with different/no `Role`) → second hits `IX_agents_public_name`.
- a public `claude` (Role=NULL) and a private `claude` coexist (private indexes unchanged).

**Acceptance:**
- [ ] `Agent.Role` is `string?`; EF config drops `.IsRequired()`, keeps `HasMaxLength(64)`.
- [ ] `IX_agents_public_name` exists on `(Name) WHERE Visibility=0`; `IX_agents_public_name_role` is gone.
- [ ] `dotnet ef migrations has-pending-model-changes --context ControlPlaneDbContext` → none.
- [ ] No new table → DROP list unchanged.

### T2 — Seeder rewrite: named cross-role personas with explicit model (AC3/AC4/AC12)

**Scope:** Rewrite `AgentEntitySeeder` to seed `claude`/`gemini`/`codegpt` (+ optional OpenRouter),
each `Visibility='public'`, `Role=NULL`, explicit `provider`+`model`, **no prompts**, insert-missing-only
keyed by `Name`, deterministic UUIDv7. Validate `(provider, model)` via `IProviderPricingService.IsKnown`
before write. Emit `AGENT.CREATED.SUCCESS` per new persona only.

**Files:** modify `Tamma.ElsaServer/AgentEntitySeeder.cs`; modify
`tests/Tamma.Api.Tests/Agents/AgentEntitySeederTests.cs`.

**Persona table (verify model strings price under 34-11):**
```csharp
("claude",  "anthropic", "claude-sonnet-4-20250514")
("gemini",  "google",    "gemini-2.5-pro")
("codegpt", "openai",    "gpt-4o")            // alias: gpt
// optional: ("openrouter-claude", "openrouter", "anthropic/claude-3.5-sonnet")
```

**ConfigJson built via the 32-1 `AgentConfigValidator`** (provider regex, budget range, ReDoS,
prototype-pollution) **with explicit `model` and NO `prompts`**. Validate before any write.

**Tests (first):**
- first run creates N personas (`Visibility='public'`, `Role=NULL`, `Version=1`, explicit `provider`+`model`, no `prompts`).
- second run creates 0, skips N; no `AGENT.CREATED.SUCCESS` on the skip run.
- after an admin publishes `claude` `Version=2`, re-run leaves `Version=2` intact, writes nothing (no-revert).
- a persona whose `(provider, model)` is not `IsKnown` → WARN + skip-write (no half-seeded row).
- each `AGENT.CREATED.SUCCESS` tags `{ agentId, version:1, visibility:"public", role:null, personaName, provider, model, mode }`.

**Acceptance:**
- [ ] Seeder creates named cross-role personas with explicit provider+model; zero `tamma-<role>` rows.
- [ ] Idempotent (2nd run = 0 created); never reverts an admin edit; `IsKnown` guard enforced.

### T3 — `GetSystemDefaultPublicAsync` rewrite + delete ambiguity warning (AC5/AC9)

**Scope:** Replace the `Role==role` lookup with a `DefaultPersonaName` (config) lookup, role-independent;
fail loud if absent; delete the per-role ">1 public agent" ambiguity warning.

**Files:** modify `Tamma.Api/Services/Agents/AgentRegistryService.cs`; new
`Tamma.Api/Services/Agents/DefaultPersonaOptions.cs` (bind `Tamma:Agents:DefaultPersonaName`, default
`"claude"`); modify `Tamma.Api/Program.cs` (bind the options); add a `GetPublicByNameAsync` to the
agent repository if not present; modify/create `tests/Tamma.Api.Tests/Agents/AgentRegistryServiceTests.cs`.

**Tests (first):**
- with `DefaultPersonaName="claude"`, `GetSystemDefaultPublicAsync` returns `claude` for architect/tester/reviewer (any role).
- with the configured persona absent → throws `AGENT_DEFAULT_PERSONA_MISSING` (no empty/plain fallback).
- seeding multiple public personas does NOT log the per-role ambiguity warning (assert absence).

**Acceptance:**
- [ ] Default persona resolution is role-independent and config-driven; fails loud on absence.
- [ ] The ambiguity warning is deleted.

### T4 — `IPersonaPromptResolver` seam + `MaterialiseAsync` wiring (AC6/AC7)

**Scope:** Ship the persona/public prompt leg as an explicit injectable seam, NOT an inline resolve.
New interface `IPersonaPromptResolver.ResolveAsync(Principal principal, string role, string? action,
CancellationToken ct)` + `PersonaPromptResolver` impl over the Epic 27 `IPromptStore` (fail-loud on
null, `PROMPT_UNRESOLVED`). Keep the merge onto `DefaultAgentConfig.ForRole(role)` and the
`AgentId`/`AgentVersion` stamp. Wire `MaterialiseAsync`'s `Visibility='public'` (persona) branch to call
`_personaPrompts.ResolveAsync(...)`. Leave the `Visibility='private'` branch as a clearly-marked seam for
32-17's `ICustomAgentPromptResolver` (do NOT implement it).

**Files:** new `Tamma.Api/Services/Agents/IPersonaPromptResolver.cs` + `PersonaPromptResolver.cs`; modify
`Tamma.Api/Services/Agents/AgentResolverService.cs` (inject + call the seam); modify/create
`tests/Tamma.Api.Tests/Agents/AgentResolverServiceTests.cs` + `PersonaPromptResolverTests.cs`.

**Tests (first):**
- a public persona resolves its system prompt via the `IPersonaPromptResolver` seam (over a fake `IPromptStore` keyed `(principal, role, action)`); the persona's `ConfigJson` carries no prompt and is NOT used as the prompt source; `MaterialiseAsync` invokes the seam, not an inline `_promptStore.ResolveAsync`.
- the seam returns null/miss from Epic 27 for `(role, action)` → `PROMPT_UNRESOLVED` thrown (never empty/plain).
- merge + stamp preserved: `MaterialiseAsync` still stamps `AgentId`/`AgentVersion` and merges onto `DefaultAgentConfig.ForRole(role)`.
- principal derivation: single-user → `(userId, role, action)`; SaaS → `(tenantId, role, action)` (via `ITammaModeProvider`).

**Acceptance:**
- [ ] `IPersonaPromptResolver` ships; `MaterialiseAsync`'s public branch calls it (no inline `_promptStore.ResolveAsync` in `MaterialiseAsync`).
- [ ] Persona system prompt comes from Epic 27 via the seam, never from persona `ConfigJson`; fails loud on absence.
- [ ] Private branch is a marked seam (32-17's `ICustomAgentPromptResolver`); merge + stamp unchanged.

### T5 — Model-contract test, legacy disposition, final wiring (AC8/AC11/AC13/AC14)

**Scope:** Update `ControlPlaneDbContextModelTests` for the nullable column + renamed index; implement
the legacy `tamma-<role>` disposition (AC11) in the seeder (archive or leave, idempotent,
`AGENT.ARCHIVED.SUCCESS` once on archive); finalize logging.

**Files:** modify `tests/Tamma.Api.Tests/Epic28/ControlPlaneDbContextModelTests.cs`; modify
`Tamma.ElsaServer/AgentEntitySeeder.cs` (legacy disposition); confirm `Program.cs` wiring.

**Tests (first):**
- `Model_Has_ExpectedControlPlaneEntities` / index assertions reflect nullable `Role` + `IX_agents_public_name` (present) and `IX_agents_public_name_role` (absent).
- with pre-seeded `tamma-<role>` rows present, the seeder is re-runnable and applies the chosen disposition deterministically; archive emits `AGENT.ARCHIVED.SUCCESS` once.
- end-to-end: seed personas, set `DefaultPersonaName="gemini"`, `GetSystemDefaultPublicAsync("architect")` → `gemini`; `MaterialiseAsync` → `Provider="google"`, `Model="gemini-2.5-pro"`, prompt from Epic 27.

**Acceptance:**
- [ ] Model-contract test green with the amended schema.
- [ ] Legacy `tamma-<role>` disposition is idempotent and non-destructive (immutable history preserved).
- [ ] Full `Tamma.Api.Tests` suite green.

---

## Story order & dependencies

External prereqs (must land first): **34-11** (Provider Cost Price-Book — personas' `(provider, model)`
must be `IsKnown`/priceable), the shipped **32-1** (entity/seeder/validator/repository/DROP-list/model
test), **32-2** (registry/resolver this story rewrites), **Epic 27** (prompt store — new persona prompt
source). Internal: T1 → (T2 ∥ T3 ∥ T4) → T5. Downstream consumers (32-16 enablement, 32-17 custom-agent
prompts, 32-18 registry gate, 32-5 call-LLM lynchpin) depend on this; they are NOT blockers.

## Verification

```bash
# build (no docker wrapper needed)
dotnet build apps/tamma-elsa/Tamma.sln
# migration is clean / no pending model changes
sg docker -c "dotnet ef migrations has-pending-model-changes --context ControlPlaneDbContext --project apps/tamma-elsa/src/Tamma.Data"
# tests (docker-bound suites need the sg wrapper; session docker group is stale)
sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Api.Tests/ --filter FullyQualifiedName~Agents"
sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Api.Tests/ --filter FullyQualifiedName~ControlPlaneDbContextModel"
# no prompt sourced from persona config (persona = prompt-free); public branch calls the seam, not an inline store
grep -rn "ConfigJson\|_personaPrompts\|IPersonaPromptResolver" apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentResolverService.cs   # prompt must come from IPersonaPromptResolver, not config or an inline _promptStore
# no tamma-<role> rows produced by the rewritten seeder
grep -rn "tamma-" apps/tamma-elsa/src/Tamma.ElsaServer/AgentEntitySeeder.cs
```

## Risks

- **Model-contract test drift (T1/T5):** the strict `BeEquivalentTo` list breaks on the nullable
  column + renamed index. Mitigation: update the test in the same change; assert both the new index's
  presence and the old index's absence.
- **Persona model not priceable (T2):** a seeded `(provider, model)` not `IsKnown` under 34-11 can't
  meter. Mitigation: `IsKnown` guard before write (WARN + skip); 34-11 is a hard prerequisite; the
  end-to-end test asserts every seeded model prices.
- **Empty-prompt regression (T4):** dropping the persona-config prompt source must not silently empty
  the prompt. Mitigation: `MaterialiseAsync` + `GetSystemDefaultPublicAsync` both fail loud
  (`feedback_resolution_no_empty_fallback`); explicit fail-loud tests; never a plain/empty fallback.
- **Legacy `tamma-<role>` shadowing (T5):** the 8 rows 32-1 seeded on `main` could shadow the named
  default resolution. Mitigation: AC11 deterministic disposition (archive or leave), idempotent on
  re-run, never destructive-delete (immutable history); default resolves over named personas only.
- **Sibling-branch collision (T4):** the private-prompt branch is 32-17's `ICustomAgentPromptResolver`;
  implementing it here would collide. Mitigation: implement ONLY the public/persona branch via the
  `IPersonaPromptResolver` seam; leave the parallel `ICustomAgentPromptResolver` seam for 32-17; scope
  tests to the public branch.
- **Snapshot drift (32-1 on `main`, 32-2 on `feat/exec-wave-02`):** Mitigation: sequential
  implementation on a single linear snapshot; per the re-plan, merge `feat/exec-wave-02` before the
  redesign stories; amend whichever snapshot is current — never branch it.
