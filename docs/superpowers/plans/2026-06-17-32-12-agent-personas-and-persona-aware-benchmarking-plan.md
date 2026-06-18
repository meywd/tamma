# Story 32-12 — Agent Personas & Persona-Aware Benchmarking (implementation plan)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every task writes tests
> before implementation. C# docker-bound suites run via `sg docker -c "dotnet test ..."`
> (`reference_dotnet_test_docker`); the build itself needs no wrapper.

**Goal:** Add **personas** — a named, reusable styling/behavior layer (tone, verbosity,
risk-tolerance, review-strictness) that composes onto an existing agent without forking its provider
config — and make persona a first-class **benchmarking dimension**. A single agent definition (e.g.
the public `tamma-reviewer`) can be run under two personas (`atlas`, `nova`) on a tenant's own work,
and per-tenant leaderboards compare those personas **like-vs-like within a role**. Persona
definitions are public (platform) or private (tenant); per-persona benchmark data is always
tenant-scoped.

**Story file:** `docs/stories/epic-32/story-32-12/32-12-agent-personas-and-persona-aware-benchmarking.md`
**Design of record:** `docs/superpowers/specs/2026-06-17-agent-entities-benchmarking-design.md`
("the Agent is the entity; personas are named variants within a role; benchmark like-vs-like").

**Tech stack:** .NET 9 / EF Core 9 / Npgsql in `apps/tamma-elsa` (control-plane API + Elsa engine).
Tests live in `apps/tamma-elsa/tests/Tamma.Api.Tests/` (xUnit). **`packages/api` is deleted — never
reference it.** The agent stack is C#: `Tamma.Data` entities + `Tamma.Api` `AgentEndpoints` /
`AgentResolverService` / `PromptStoreService`, DCB `DomainEvent` + `IEventRepository`.

---

## Non-goals (YAGNI guard)

- **NO new provider/model fields on the persona.** A persona is style + an optional prompt fragment
  ref only. Provider/model/credential/budget belong to the *agent* (32-1). A persona that re-providers
  is a fork, which is exactly what this story exists to avoid.
- **NO change to resolution semantics for persona-free runs.** `ResolveForRoleAsync(role)` /
  `ResolveForPhaseAsync(phase, role)` with no `personaId` stay byte-for-byte. The `personaId` param is
  additive and optional; `personaId == null` is the legitimate persona-free path.
- **NO empty/plain fallback.** A requested-but-unresolvable persona or fragment is a hard
  `TammaError`/4xx (`feedback_resolution_no_empty_fallback`). Never return the bare agent config "as
  if no persona were asked for"; never return a blank fragment.
- **NO per-user persona layer in SaaS.** Mirrors the Prompt Store: SaaS persona ownership is tenant
  (owner/admin edit, member read); single-user ownership is the sole user. No per-member persona
  personalization.
- **NO building the 32-10 leaderboard API itself.** This story owns the persona *dimension* (trail
  tags + a group-by-persona projection) and the query-facet contract; 32-10 surfaces it. 32-10's
  story file does not yet exist — coordinate the facet at integration.
- **NO new dashboard UI.** Surfacing personas in the admin/tenant benchmark dashboards is 32-13.

---

## Current-state findings (verified 2026-06-17, repo @ main)

| Seam | State |
|---|---|
| `apps/tamma-elsa/src/Tamma.Data/Entities/` | Has `AgentConfig`, `DomainEvent`, `PromptOverride`. **`Agent`/`AgentVersion`/`AgentVisibility`/`AgentStatus` are 32-1 in-flight** (story drafted; entity files not yet on main) — reference by interface; reuse the enums + the `ck_agents_visibility_ownership` CHECK + per-owner partial-index pattern. |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/IAgentResolverService.cs` | Real. Has `ResolveAsync(tenantId, role)`, `ResolveForPhaseAsync(...)`, and the task-overrides variant. 32-2 adds `ResolveForRoleAsync(role)` returning the enriched config. |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/ResolvedAgentConfig.cs` | Real. Has `Role`, `Handle`, `Provider`, `Model`, `Temperature`, `MaxTokens`, `TokenBudget`, `Tools`, `SystemPrompt`, `Source`, `Phase`, `MaxBudgetUsd`, `PermissionMode`, `AllowedTools`. 32-2 adds `AgentId`/`AgentVersion`. This story adds `PersonaId`/`PersonaName`. |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/AgentEndpoints.cs` | Real — the place persona CRUD handlers + `&personaId=` on resolve land. |
| `apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/PromptStoreService.cs` | Real. `ResolveRoleActionAsync(userId, role, action)` (single-user) + `ResolveRoleActionForTenantAsync(tenantId, role, action)` (SaaS); layering `system role → role+action template`; `ResolvedPrompt` record; fail-loud `NoPromptError`. Persona fragment resolves through this — no Prompt-Store API change. |
| `apps/tamma-elsa/src/Tamma.Data/Entities/DomainEvent.cs` | Real. `{ Id, Type, TenantId, IssueNumber, Tags, Metadata, Data, CreatedAt, SequenceNumber }`. DCB tag/`AGGREGATE.ACTION.STATUS` convention. Appended via `IEventRepository.AppendAsync`. |
| `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` | Single source of EF model config — CHECK + partial indexes live here only (PromptOverride XOR precedent). |
| Action trail (32-6) | Story drafted (`docs/stories/epic-32/story-32-6/`). Tags already: `agentId, agentVersion, role, provider, model, promptRef, issueId, iteration, correlationId, credentialSource`, via a shared tag builder. This story adds `personaId`/`personaName` to that builder. |
| RBAC | `PlatformOwnerAccess` (platform owner) + `AgentManage` (`agents:manage` = admin+owner, 32-2) + member-read; 403-on-write / 404-cross-tenant pattern from 32-1/32-2 + Prompt Store. |
| `ITammaModeProvider` | `Tamma.Api/Services/PromptStore/TammaMode.cs` — process-stable `SingleUser`/`SaaS`. |

**Key dependency posture:** 32-1 (entities/enums) and 32-2 (resolve + enriched config + policy)
are hard prerequisites and in-flight; every reference is by interface and marked
**(coordinate with 32-1/32-2)** in the story. 32-6 (trail) is reused; 32-10 (leaderboards) consumes
the dimension this story defines.

---

## Architecture

**Persona = role-scoped style overlay that composes at resolution time into a new
`ResolvedAgentConfig`, recorded on the trail, sliced by the leaderboard.**

1. **`Persona` entity** (CP-resident; public ∪ tenant-private) — `Name` (stable handle), `Role`,
   `Visibility`/owner columns (reuse 32-1 enums + ownership CHECK + per-owner partial indexes),
   `StyleJson` (jsonb traits), `SystemPromptFragmentRef` (Prompt-Store key). No provider/model.
2. **`PersonaComposer`** (pure) — merges persona `StyleJson` onto an already-resolved agent config
   (style-adjacent fields only; provider/model/budget/tools untouched) and **appends** the resolved
   prompt fragment in fixed order `role identity → role+action → persona fragment`. Returns a new
   object; never mutates input (state-immutability rule). Stamps `PersonaId`/`PersonaName`.
3. **Resolver extension** — `IAgentResolverService.ResolveForRoleAsync(role, personaId?, ct)` (+ phase
   variant): resolve agent (32-2 chain) → if `personaId` set, load+validate persona (visible AND role
   matches) → resolve fragment via Epic 27 (fail-loud if non-null & unresolvable) →
   `PersonaComposer.Compose` → emit `AGENT.PERSONA_APPLIED.SUCCESS`. `personaId == null` ⇒ unchanged.
4. **Trail tag** — extend the 32-6 shared tag builder so every `AGENT.TASK.*` /
   `AGENT.ITERATION.COMPLETED` / `AGENT.PANEL.AGGREGATED` / `REVIEW.BUG.RECORDED` event +
   `AgentRunResult` carries `personaId`/`personaName` (flat strings; empty when persona-free).
5. **Benchmark slice** — a per-persona projection over the 32-6 trail grouping by `(role, personaId)`
   for the calling tenant only (success rate, avg iterations-to-done, bug-by-type, cost, latency).
   Defines the `?groupBy=persona` / `?personaId=` facet contract 32-10 surfaces.
6. **CRUD + resolve endpoints** — `/api/personas` (list/create/get/put/archive) + `&personaId=` on
   `/api/agents/resolve`, per-mode RBAC (private ⇒ `AgentManage`/owner; public ⇒ `PlatformOwnerAccess`;
   member ⇒ 403; cross-tenant private ⇒ 404).

### Per-mode ownership (mandatory two-scoping-model answer, per CLAUDE.md)

| Question | single-user | SaaS |
|---|---|---|
| Who owns a **public** persona? | Shipped system personas (read-only to the user). | Platform owner (`PlatformOwnerAccess`); CP-resident; every tenant may *use*, not edit. |
| Who owns a **private** persona? | The sole user (`OwnerUserId`; `OwnerTenantId` NULL). | The tenant (`OwnerTenantId`); `tenant_owner`/`tenant_admin` edit, `member` read-only. |
| Who can apply a persona to a run? | The user. | Any member (apply ≈ read + resolve); editing needs owner/admin. |
| Where do per-persona benchmarks live? | The user's data (`user_id`). | The tenant's `t_<hex>` data plane; never cross-tenant; platform admin sees none. |
| Resolution / benchmark principal | `user_id` | `tenant_id` |
| Mode source | `ITammaModeProvider` (process-stable) | same |

---

## Task breakdown

### T1 — `Persona` entity + EF config + migration (core)

**Scope:** New CP entity, model config (CHECK + partial indexes), additive migration. No resolver
wiring yet.

**Files:**
- New: `src/Tamma.Data/Entities/Persona.cs` (reuses `AgentVisibility`/`AgentStatus` from 32-1).
- Modify: `src/Tamma.Data/ControlPlaneDbContext.cs` (add `DbSet<Persona>`),
  `src/Tamma.Data/TammaModelConfiguration.cs` (`ck_personas_visibility_ownership`,
  `IX_personas_public_name_role`, per-owner private partial indexes; `StyleJson` jsonb default).
- New: additive migration under `src/Tamma.Data/Migrations/ControlPlane/` (`AddPersonaEntity`).

**Tests (first):** `tests/Tamma.Api.Tests/Personas/PersonaEntityTests.cs` (or extend
`Epic28/ControlPlaneDbContextModelTests`) — table/indexes/CHECK exist; ownership CHECK rejects
public-with-owner / private-with-no-owner / private-with-both; two tenants each own private
`atlas`/`reviewer` (per-owner partial index allows it); public `(Name, Role)` uniqueness enforced.

**Acceptance:**
- [ ] Migration applies; `dotnet ef migrations has-pending-model-changes --context ControlPlaneDbContext` → none.
- [ ] CHECK + partial-index behaviour proven against a real Postgres fixture.
- [ ] Entity config lives **only** in `TammaModelConfiguration.cs`.

### T2 — `IPersonaRepository` + lifecycle DCB events

**Scope:** Repository (`CreateAsync`, `UpdateAsync`, `ArchiveAsync`, `GetByIdAsync`,
`GetVisibleAsync(id, principal)`, `ListVisibleAsync(principal, filter)`) with per-mode principal
derivation and `PERSONA.CREATED/UPDATED/ARCHIVED.SUCCESS` emission. `GetVisibleAsync` returns public
∪ own-private only; cross-tenant private ⇒ null (→ 404 at the endpoint).

**Files:**
- New: `src/Tamma.Data/Repositories/IPersonaRepository.cs`, `PersonaRepository.cs`.
- New: `src/Tamma.Api/Services/Agents/PersonaEventTypes.cs`
  (`PERSONA.CREATED.SUCCESS`, `PERSONA.UPDATED.SUCCESS`, `PERSONA.ARCHIVED.SUCCESS`,
  `AGENT.PERSONA_APPLIED.SUCCESS`).
- DI: register `IPersonaRepository` (Scoped) in `Program.cs`.

**Tests (first):** `tests/Tamma.Api.Tests/Personas/PersonaRepositoryTests.cs` +
`PersonaEventsTests.cs` — create/update/archive each emit exactly one event with correct tags
(`personaId, personaName, role, visibility, mode`); `GetVisibleAsync` returns public ∪ own-private,
null for other-tenant private; per-mode principal derivation (single-user `OwnerUserId` / SaaS
`OwnerTenantId`); no event on a rejected write (CHECK violation / mode contradiction).

**Acceptance:**
- [ ] Lifecycle events emit only after a real state transition (no "lie" events).
- [ ] Per-mode ownership derived from `ITammaModeProvider`; contradictory input rejected pre-DB.
- [ ] Cross-tenant private read returns null (→ 404), never another tenant's row.

### T3 — `PersonaComposer` + resolver extension + `ResolvedAgentConfig` fields

**Scope:** The load-bearing composition. Pure composer + the `personaId` resolve path + the
`AGENT.PERSONA_APPLIED.SUCCESS` event + fail-loud fragment resolution.

**Files:**
- Modify: `src/Tamma.Api/Services/Agents/ResolvedAgentConfig.cs` (add `Guid? PersonaId`,
  `string? PersonaName` — additive).
- New: `src/Tamma.Api/Services/Agents/PersonaComposer.cs` (pure: style merge + fragment append in
  fixed order; returns new config; never mutates input).
- Modify: `src/Tamma.Api/Services/Agents/IAgentResolverService.cs` +
  `AgentResolverService.cs` (add optional `personaId` to `ResolveForRoleAsync` / phase variant;
  load+validate persona via `IPersonaRepository.GetVisibleAsync`; role-match check; resolve fragment
  via `PromptStoreService.ResolveRoleActionAsync`/`ResolveRoleActionForTenantAsync`, fail-loud if
  non-null & unresolvable; compose; emit applied event).

**Tests (first):** `tests/Tamma.Api.Tests/Personas/PersonaComposerTests.cs` +
extend `Agents/AgentResolverServiceTests.cs`:
- style merge overrides only style-adjacent fields, provider/model/budget/tools untouched;
- fragment appended in fixed order `role identity → role+action → persona fragment`;
- input config + agent pinned version unmodified (immutability);
- two personas / one agent / same role → same `AgentId`/`AgentVersion`, distinct `SystemPrompt`;
- `personaId == null` → plain config unchanged (no regression);
- unknown persona → `PERSONA.RESOLVE.NOT_FOUND` (404); role-mismatch → `PERSONA.ROLE_MISMATCH` (400);
  non-null `SystemPromptFragmentRef` unresolvable → `TammaError`, no blank fallback;
- composed resolution emits exactly one `AGENT.PERSONA_APPLIED.SUCCESS` `{personaId, agentId, role}`;
  failure path emits nothing.

**Acceptance:**
- [ ] Persona-free resolution byte-for-byte unchanged.
- [ ] No empty/plain fallback on any unresolvable-persona path.
- [ ] Composition is pure + immutable; agent version never mutated.

### T4 — Persona CRUD + resolve endpoints + RBAC

**Scope:** `/api/personas` handlers + `&personaId=` on `/api/agents/resolve`, per-mode RBAC,
404-cross-tenant.

**Files:**
- Modify: `src/Tamma.Api/Endpoints/AgentEndpoints.cs` (or new `PersonaEndpoints.cs`) —
  `List`, `Create`, `GetOne`, `Update`, `Archive`; in-handler public-write gate
  (`persona_public_write_forbidden` 403 for `visibility:public` from non-platform-owner).
- New: `src/Tamma.Api/Dtos/Agents/PersonaDtos.cs` (request/response records).
- Modify: `src/Tamma.Api/Program.cs` — map `/api/personas` group (`MemberAccess` reads;
  `AgentManage` writes; rate-limiting mirroring 32-2), add `&personaId=` to the resolve handler.

**Tests (first):** `tests/Tamma.Api.Tests/Personas/PersonaEndpointsTests.cs`
(in-process `WebApplicationFactory`) — RBAC matrix: member create/update/archive → 403; tenant
`POST {visibility:public}` → 403 `persona_public_write_forbidden`; platform owner public create →
201; cross-tenant `GET /{B-private-id}` → 404; `GET /api/personas` from A never returns B's private;
`GET /api/agents/resolve?role=reviewer&personaId=...` returns a persona-composed config; role
mismatch via that route → 400.

**Acceptance:**
- [ ] Endpoint shape identical between modes; auth middleware picks the owner column by mode + identity.
- [ ] Cross-tenant private read → 404 (existence not leaked).
- [ ] Member → 403 on all writes; reads allowed.

### T5 — Persona trail tags + per-persona benchmark slice (the dimension)

**Scope:** Extend the 32-6 shared trail-tag builder with `personaId`/`personaName`; deliver the
per-persona projection grouping by `(role, personaId)` for the calling tenant; define the
`?groupBy=persona` / `?personaId=` facet contract for 32-10.

**Files:**
- Modify: the 32-6 shared trail-tag builder (file lands with 32-6 — add `personaId`/`personaName` to
  the single builder, not per emission site; coordinate ordering with 32-6).
- New: the per-persona projection helper + its `(role, personaId)` group-by (over the 32-6 trail;
  exact home coordinates with the 32-10 projection layer).

**Tests (first):** `tests/Tamma.Api.Tests/Personas/PersonaTrailTaggingTests.cs` +
`PersonaLeaderboardProjectionTests.cs` — persona-composed run's trail events carry
`personaId`/`personaName`; persona-free run carries empty/absent; no raw `StyleJson`/fragment in
tags (redaction); group-by-`(role, personaId)` ranks `atlas` vs `nova` within reviewer; an
architect-role persona never appears in the reviewer leaderboard (like-vs-like); tenant B rows never
appear in tenant A's projection (isolation).

**Acceptance:**
- [ ] Every trail event + `AgentRunResult` from a persona run carries the persona tags via the shared builder.
- [ ] Per-persona leaderboard compares within a role only, on the tenant's own data only.
- [ ] Facet contract (`?groupBy=persona` / `?personaId=`) documented for 32-10 to surface.

### T6 — Regression + migration verification + suite green

**Scope:** Prove no regression and clean migration.

**Files:** Modify `tests/Tamma.Api.Tests/Epic28/ControlPlaneDbContextModelTests.cs` (assert
`personas` table/indexes/CHECK); run the full `Tamma.Api.Tests` suite.

**Acceptance:**
- [ ] Persona-free resolution + legacy `/api/v1/agents/*` routes byte-for-byte green.
- [ ] `dotnet ef migrations has-pending-model-changes --context ControlPlaneDbContext` → none.
- [ ] Full `sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Api.Tests"` green.

---

## Task order & dependencies

T1 → T2 → T3 → T4 → T5 → T6.
T1 is the only hard prerequisite for everything else. T4 needs T2+T3; T5 needs T3 (composition stamps
the persona) + the 32-6 trail builder. T5 has a soft seam on 32-6/32-10 — implement the tags + the
projection here; the leaderboard *surface* is 32-10.

## Risks

- **32-1/32-2 in-flight:** the `Agent`/`AgentVersion` entities, `AgentVisibility`/`AgentStatus`
  enums, `ResolveForRoleAsync`, and the enriched `ResolvedAgentConfig`/`AgentManage` policy must land
  first. Every reference is by interface and marked **(coordinate with 32-1/32-2)** — reconcile each
  before coding. If 32-1 names the enums or ownership CHECK differently, follow 32-1, do not fork.
- **Composition altitude:** the composer must touch *only* style-adjacent fields. Letting persona
  `StyleJson` override provider/model/budget/tools turns a persona into a fork and breaks like-vs-like
  benchmarking. Pin the allowed-override field set in `PersonaComposerTests`.
- **Fail-loud fragment:** a non-null `SystemPromptFragmentRef` that the Prompt Store can't resolve
  MUST throw — the most likely accidental regression is "fall back to no fragment." The no-empty
  test (T3) is load-bearing (`feedback_resolution_no_empty_fallback`).
- **Trail-tag builder coupling (T5):** the persona tags must go in the *single* 32-6 shared builder,
  or emission sites will drift. Coordinate file ordering with 32-6 so this story extends the builder
  rather than re-implementing tags per site.
- **Leaderboard ownership (T5/32-10):** 32-10's story file does not exist yet. This story owns the
  persona tags + the projection slice + the facet contract; do not build the full leaderboard API
  here — keep the blast radius to the dimension.
- **Migration discipline (Epic 28):** `personas` is additive — normal `migrations add`, not a
  baseline CHECK edit; verify `has-pending-model-changes` reports none; mirror config only in
  `TammaModelConfiguration.cs`.
- **Cross-tenant leakage:** per-persona benchmark data must never escape the tenant — enforce via the
  per-tenant connection + 404-not-403 cross-tenant reads; covered by isolation tests in T2/T4/T5.
