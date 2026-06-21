# Story 32-12 — Persona-Aware Benchmarking

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every task writes tests
> before implementation.

**Date:** 2026-06-21

**Goal:** Make the benchmark `persona` dimension correct under the **locked agent model** and add the
persona-comparison facets a tenant reads to pick a persona per role. Concretely: key the `persona`
dimension on the **named cross-role persona-agent identity** (`AgentId`/`AgentName` = `claude`/`gemini`/
`codegpt` from **32-15**) instead of a style row; add **provider / model / prompt-version /
`credentialSource`** facets so a persona's runs are comparable like-vs-like **within a role**; and
expose the persona-comparison query on **32-10's** existing leaderboard endpoint. This is a *refinement*
of 32-10's persona dimension — **no new entity, no new fold, no new service**.

**Story file:** `docs/stories/epic-32/story-32-12/32-12-agent-personas-and-persona-aware-benchmarking.md`
**Design of record:** `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` (§3.0 reframe table, §3.1 persona = system-agent entity, §3.4 disposition of 32-12)
**Re-plan:** `docs/superpowers/plans/2026-06-20-epic-32-37-replan.md` (story disposition + sequence)

**Tech stack:** .NET 9 / Elsa 3 in `apps/tamma-elsa` (`Tamma.Api` services + endpoints, `Tamma.Data`
tenant entities + EF migrations). Tests live in `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/` (xUnit).
Docker-bound suites run via `sg docker -c "dotnet test ..."` (session docker group is stale; plain
`dotnet build` needs no wrapper). **There is no TypeScript path — all C#.**

---

## The reframe (why this is a rewrite, not a new feature)

The shipped/drafted v1.0.0 of 32-12 modelled **persona = a style/tone overlay** (`atlas`/`nova`) within
one role, with a new `Persona` table + `PersonaComposer` + style-aware benchmarking. The locked model
(design §3.0) redefines the word:

| Term | v1.0.0 (superseded) | Locked model (this plan) |
|---|---|---|
| **Persona** | style/tone overlay within a role | the **named cross-role system agent** (`claude`/`gemini`/`codegpt`) from 32-15 |
| **New entity** | `Persona` table + `PersonaComposer` + `StyleJson` | **none** — persona IS the public `Agent`; benchmark rides 32-10 |
| **Benchmark key** | `(role, personaId)` where persona = style | `(role, personaAgentId)` + provider/model/promptVersion/credentialSource facets |
| **Style/voice** | conflated into "persona" | **split out → 32-19** "Agent style/voice variants" (a *variant*, not a persona) |

So this plan **deletes** the persona-entity/composer/CRUD work and **adds** a persona-aware view over
32-10's existing read model. The style/voice overlay is a separate optional story (**32-19**) — not here.

---

## Non-goals (YAGNI guard)

- **NO new entity.** No `Persona` table, no `PersonaComposer`, no `StyleJson`, no persona CRUD endpoints,
  no resolver persona-composition path. The persona IS the 32-15 public `Agent`. (All of that v1.0.0
  scope is dropped.)
- **NO new control-plane table.** The persona is the 32-15 public `Agent` (already CP-registered); the
  benchmark rows are 32-10's **tenant-schema** entities (owned by `EfTenantDbMigrator`). Nothing is
  appended to the `Program.cs` startup-reset DROP list; `ControlPlaneDbContextModelTests` is untouched.
- **NO new fold / projection engine.** The metrics come from 32-10's `BenchmarkProjectionService`
  reducer; this story keys + facets only. No second projection.
- **NO new source event.** It folds the same 32-6/32-8/32-9 events 32-10 folds; persona lifecycle reuses
  32-10's `BENCHMARK.PROJECTION.*` family.
- **NO style/voice variant.** `atlas`/`nova` tone/verbosity is **32-19**. Not built or assumed here.
- **NO cross-tenant persona-benchmark admin route.** The only cross-tenant view is 32-10's k-anonymous
  public-agent fleet rollup — owned there, not extended.
- **NO baseline rewrite / migration branch.** If facets are persisted, it is a single additive **Tenant**
  migration on the existing linear snapshot.

---

## Current-state findings (verify in-repo before coding)

| Seam | Where it is today (32-10 / 32-15) | How 32-12 uses/refines it |
|---|---|---|
| **Projection engine** | `Tamma.Api/Services/Agents/BenchmarkProjectionService.cs` — idempotent, `SequenceNumber`-cursor fold; `dimensionKeyFor` maps agent/provider/prompt/**persona** from `Tags`. | Refine the `persona` branch: key = `agentId` (the persona-agent identity); add facet sub-keys (provider/model/agentVersion/promptRef/credentialSource). |
| **Projection entities** | `Tamma.Data/Entities/BenchmarkProjection.cs` + `BenchmarkProjectionCursor.cs` — tenant-schema, `UNIQUE (TenantId, Dimension, DimensionKey, Window)`; `persona` dimension `DimensionKey=personaId`. | Reuse. Persist facet columns **only** if query-time aggregation is too costly (else compute on read). |
| **Leaderboard API** | `Tamma.Api/Endpoints/AgentLeaderboardEndpoints.cs` — `GET /api/v1/orgs/{tenantId}/agents/leaderboard?dimension=…&window=…&minRuns=…`; tie-break chain + `belowThreshold` guard; `RequireTenantMembershipFilter`. | Add persona facets: `?dimension=persona&role=&personaId=&credentialSource=&promptVersion=`. Member-read; tenant-scoped. |
| **Persona identity** | 32-15 — public `Agent` `claude`/`gemini`/`codegpt`, `Role=NULL`, explicit `provider`+`model`; `agentId` is the persona identity. | The persona dimension key + the provider/model facets come from these. |
| **Trail tags** | 32-6 — `agentId`/`agentVersion`/`provider`/`promptRef` flat tags on `AGENT.TASK.*` / `AGENT.ITERATION.COMPLETED` / `AGENT.PANEL.AGGREGATED`. | The persona key + facets read these flat tags. |
| **credentialSource** | 32-5/32-9 — surfaced on the run + tagged on `AGENT.USAGE.RECORDED`. | The `credentialSource` facet; degrades to explicit `unknown` until those land. |
| **Isolation** | 32-10 — `ITenantDbContextFactory`, null-tenant-guarded `IEventRepository`, per-tenant `SequenceNumber` composite cursor; k-anonymous public-agent admin rollup. | Reused wholesale; persona rows inherit the isolation. |
| **Mode** | `Tamma.Api/Services/PromptStore/TammaMode.cs` — `ITammaModeProvider` (SingleUser | SaaS). | Per-mode principal derivation (single-user keyed by `UserId`, SaaS by `TenantId`). |

**Key insight:** the read model already exists (32-10) and already has a `persona` dimension. The only
genuinely new code is (1) keying that dimension on the persona-agent identity, (2) a pure facet
extractor (provider/model/promptVersion/credentialSource with an explicit `unknown` bucket), and (3) the
persona-comparison query params on the existing 32-10 endpoint. No entity, no fold, no service.

---

## Architecture

```
BenchmarkProjectionService (32-10, tenant-scoped, idempotent, SequenceNumber cursor)
   dimensionKeyFor("persona", tags) -> tags["agentId"]        // persona = the 32-15 public Agent identity
   PersonaBenchmarkFacets.Extract(tags) -> { provider, model, agentVersion, promptRef, credentialSource }
        absent facet tag -> explicit "unknown" bucket          // NEVER silently merged (no-empty-fallback)
        malformed dimension -> throw BENCHMARK.DIMENSION.INVALID

Read (member-scoped to the caller's tenant by RequireTenantMembershipFilter):
   GET /orgs/{tenantId}/agents/leaderboard?dimension=persona&role=reviewer&window=30d
        -> rank claude/gemini/codegpt AS reviewer (like-vs-like within a role)   [32-10 tie-break + minRuns]
   GET …?dimension=persona&personaId={agentId}&role=reviewer
        -> one persona's facet breakdown (provider/model/promptVersion/credentialSource)
   GET …?dimension=persona&role=reviewer&credentialSource=byok | &promptVersion=v4
        -> facet-filtered comparison

Per-tenant scoping (design ownership rule): every persona benchmark row lives in the resolving tenant's
t_<hex> schema; two tenants running public persona `claude` build separate private profiles; the platform
owner sees neither (only 32-10's k-anonymous public-agent rollup). Cursor is the per-schema BIGSERIAL,
composite-keyed incl. TenantId — no shared global cursor.
```

Per-mode ownership (CLAUDE.md two-scoping-model): the persona *definition* is platform-global
(`PlatformOwnerAccess` to curate, NOT `OwnerAccess`); the persona *benchmark data* is the tenant's
(SaaS) / the sole user's (single-user) — member-read, never cross-tenant. Mode from `ITammaModeProvider`.

---

## Task breakdown

Order: T1 (persona dimension key) → T2 (facet extractor + unknown-bucket) → T3 (persona-comparison query)
→ T4 (optional persisted facet columns) → T5 (isolation + equivalence + lifecycle + no-regression).
T1 must land first (everything keys on the persona-agent identity). T2 feeds T3/T4. T4 is OPTIONAL —
take it only if query-time facet aggregation (T3) proves too costly.

### T1 — Persona dimension key = the persona-agent identity (AC1)

**Scope:** Refine `BenchmarkProjectionService.dimensionKeyFor`'s `persona` branch to key on
`tags["agentId"]` (the 32-15 public-persona `Agent` identity), with `AgentName` as the label — NOT a
style row. If 32-10 already keys `persona` by `agentId`, confirm and leave; if it keys a style value,
correct it.

**Files:** modify `Tamma.Api/Services/Agents/BenchmarkProjectionService.cs`; modify
`tests/Tamma.Api.Tests/Agents/BenchmarkProjectionServiceTests.cs`.

**Tests (first):**
- a fold over a fixture stream whose `agentId` is the public `claude` persona produces a persona row keyed by that `agentId`/`AgentName` — assert the key is the persona-agent identity, **never** a tone/verbosity value.
- a malformed `dimension` throws `BENCHMARK.DIMENSION.INVALID` (no silent fallback).

**Acceptance:**
- [ ] `persona` dimension key = `agentId` (persona-agent identity); `AgentName` is the label.
- [ ] No `Persona`/`StyleJson`/`PersonaComposer` type introduced (grep proves it).

### T2 — Facet extractor + explicit `unknown` bucket (AC2/AC6)

**Scope:** New pure `PersonaBenchmarkFacets.Extract(tags)` returning
`{ provider, model, agentVersion, promptRef, credentialSource }` from the **same flat trail tags**.
Absent facet tag → explicit `"unknown"` bucket; **never** silently merged into a populated facet.

**Files:** new `Tamma.Api/Services/Agents/PersonaBenchmarkFacets.cs`; new
`tests/Tamma.Api.Tests/Agents/PersonaBenchmarkFacetsTests.cs`.

**Tests (first):**
- runs of persona `claude` split by `credentialSource` (`byok` vs `platform`) → distinct facet buckets, same `personaId`.
- prompt v3 vs v4 (`promptRef`) split distinctly; provider/model split distinctly.
- a run with no `credentialSource` tag (pre-32-5) → explicit `unknown` bucket, NOT merged into `byok`/`platform`.
- no raw config/prompt/key appears in any facet value.

**Acceptance:**
- [ ] Facets extracted from flat tags; absent → explicit `unknown` (no silent merge).
- [ ] Facet values are flat strings only — never a raw config/prompt/key.

### T3 — Persona-comparison query (like-vs-like within a role) (AC4/AC5)

**Scope:** Extend the 32-10 leaderboard endpoint for the persona dimension:
`?dimension=persona&role=&window=` ranks personas as that role (like-vs-like, scoped to one role);
`?personaId=` returns one persona's facet breakdown; `&credentialSource=`/`&promptVersion=` narrow the
comparison. Reuse 32-10's tie-break chain + `minRuns` guard. Member-read; tenant-scoped by
`RequireTenantMembershipFilter`.

**Files:** modify `Tamma.Api/Endpoints/AgentLeaderboardEndpoints.cs`; modify
`Tamma.Api/Services/Agents/BenchmarkProjectionService.cs` (`GetLeaderboardAsync` persona role+facet
read; new method on `IBenchmarkProjectionService` ONLY if a new signature is needed); modify
`Tamma.Api/Dtos/Agents/BenchmarkDtos.cs` (`PersonaLeaderboardRow` + facet-breakdown DTO); new
`tests/Tamma.Api.Tests/Agents/PersonaLeaderboardTests.cs`.

**Tests (first):**
- persona `claude` used as `reviewer` and as `architect` → two separate per-role rows.
- `?dimension=persona&role=reviewer` ranks `claude`/`gemini`/`codegpt` only as reviewer; an architect-role persona never appears in the reviewer leaderboard.
- `?personaId={claude}&role=reviewer` → facet breakdown across provider/model/promptVersion/credentialSource.
- a persona with `runCount < minRuns` → `belowThreshold`, never `ranked` (32-10 guard reused).
- tie-break chain (`successRate` desc → `avgIterationsToDone` asc → `avgCostBasisUsd` asc → key asc) holds.

**Acceptance:**
- [ ] Persona comparison is like-vs-like within a single role; cross-role is structurally impossible.
- [ ] Member-read, tenant-scoped; facet filters work; min-sample guard + tie-break reused from 32-10.

### T4 — (OPTIONAL) persisted facet columns (AC9, Tenant migration only)

**Scope:** ONLY if query-time facet aggregation (T3) is too costly: add additive facet columns
(`Provider`, `Model`, `AgentVersion`, `PromptRef`, `CredentialSource`) to 32-10's **tenant-schema**
`BenchmarkProjection`. Single additive **Tenant** migration on the existing linear snapshot.

**Files (only if taken):** modify `Tamma.Data/Entities/BenchmarkProjection.cs`; modify
`Tamma.Data/TammaModelConfiguration.cs`; new
`Tamma.Data/Migrations/Tenant/<ts>_AddPersonaBenchmarkFacets.cs` (+ Designer + snapshot).

**Tests (first):**
- migration applies; `dotnet ef migrations has-pending-model-changes` (Tenant context) → none.
- the facet columns round-trip; the tenant `EfTenantDbMigrator` owns them.

**Acceptance:**
- [ ] (If taken) additive **Tenant** migration; NOT a CP table; `Program.cs` DROP list + `ControlPlaneDbContextModelTests` untouched.
- [ ] Default path is **no schema change** (compute facets at query time) — take T4 only on a measured need.

### T5 — Isolation, equivalence, lifecycle, no-regression (AC3/AC7/AC8/AC10)

**Scope:** Reuse 32-10's tenant isolation for the persona dimension; assert incremental-vs-rebuild
equivalence on persona rows; assert `BENCHMARK.PROJECTION.*` lifecycle (no new source event); assert no
regression on agent/provider/prompt dimensions and no CP churn.

**Files:** modify `tests/Tamma.Api.Tests/Agents/BenchmarkIsolationTests.cs` (persona-dimension case);
modify `tests/Tamma.Api.Tests/Agents/BenchmarkProjectionServiceTests.cs` (equivalence + lifecycle +
no-regression).

**Tests (first):**
- seed persona rows for tenant A and B; `GET …/orgs/{B}/…?dimension=persona` returns only B's; a member of A hitting B's path → 403/404; platform owner has no per-tenant persona route; a public persona run by A leaves persona rows only in A's schema.
- fold persona events, fold again (cursor skip → no double-count), `RebuildAsync` → byte-identical persona rows + facets; the per-tenant composite cursor never crosses tenants.
- a persona fold emits `BENCHMARK.PROJECTION.UPDATED` (`dimension:"persona"`); rebuild emits `BENCHMARK.PROJECTION.REBUILT`; an empty fold emits nothing; grep confirms no new persona-specific source event.
- agent/provider/prompt dimensions + leaderboard shape unchanged; persona metric numbers equal the 32-10 reducer's for the same runs; `has-pending-model-changes` → none; suite green.

**Acceptance:**
- [ ] Per-tenant isolation holds for the persona dimension; platform admin denied per-tenant.
- [ ] Incremental and full-rebuild persona rows are byte-identical; per-tenant cursor never crosses tenants.
- [ ] No new source event; lifecycle reuses 32-10's family; no CP churn; full `Tamma.Api.Tests` green.

---

## Story order & dependencies

External prereqs (must land first): **32-10** (the benchmark projection + leaderboard + `persona`
dimension + `SequenceNumber` cursor + isolation this story extends) and **32-15** (the persona-agent
identity this story keys on). Consumed by tag contract / soft-degrade: **32-6** (trail tags), **32-8**
(outcome/defect), **32-9** + **32-5** (`credentialSource` + cost — facet degrades to `unknown` until
they land), **32-16** (enablement constrains which personas a tenant runs). Internal: T1 → T2 → T3 →
(T4 optional) → T5. Downstream consumers (32-13 dashboards, 32-14 A/B) depend on this; not blockers.
Sibling **32-19** (style/voice variants — the split-out style overlay) is **NOT** a dependency.

## Verification

```bash
# build (no docker wrapper needed)
dotnet build apps/tamma-elsa/Tamma.sln
# persona dimension key + facets + comparison
sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Api.Tests/ --filter FullyQualifiedName~PersonaBenchmark"
sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Api.Tests/ --filter FullyQualifiedName~PersonaLeaderboard"
sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Api.Tests/ --filter FullyQualifiedName~BenchmarkIsolation"
# no new persona entity / style overlay introduced by this story
! grep -rn "class Persona\b\|PersonaComposer\|StyleJson" apps/tamma-elsa/src/Tamma.Api apps/tamma-elsa/src/Tamma.Data
# persona dimension keys on agentId (the persona-agent identity), not a style row
grep -rn "\"persona\"" apps/tamma-elsa/src/Tamma.Api/Services/Agents/BenchmarkProjectionService.cs
# no CP table added -> Program.cs DROP list unchanged; if facets persisted, Tenant migration only
sg docker -c "dotnet ef migrations has-pending-model-changes --context TenantDbContext --project apps/tamma-elsa/src/Tamma.Data"
```

## Risks

- **Old style model leaks through (T1):** the persona dimension must key on the 32-15 persona-agent
  `AgentId`, not a tone/verbosity row. Mitigation: AC1 + an explicit test asserting the key is the
  persona-agent identity; grep proves no `Persona`/`StyleJson`/`PersonaComposer` type is introduced.
- **Facet silently merges absent values (T2):** dropping the no-empty-fallback rule on facets would
  mis-attribute runs. Mitigation: absent facet → explicit `unknown` bucket; malformed dimension →
  throw; facet-bucketing test asserts no silent merge (`feedback_resolution_no_empty_fallback`).
- **Reimplementing the 32-10 fold/metrics (T1/T3):** a second fold would drift. Mitigation: this story
  keys + facets only; metrics come from the same 32-10 reducer; assert identical numbers for the same
  runs; no second projection.
- **Cross-tenant leakage of per-persona data (T5):** Mitigation: reuse 32-10's structural isolation
  (`ITenantDbContextFactory`, null-tenant-guarded reads, no cross-tenant route, per-tenant composite
  cursor); explicit isolation test incl. platform-admin-denied; the only cross-tenant view is 32-10's
  k-anonymous public-agent rollup (owned there).
- **`credentialSource` facet depends on un-landed 32-5/32-9 (T2):** Mitigation: degrade to explicit
  `unknown`, backfill on rebuild once the tag exists; align the tag name with 32-9 before merge; never
  invent a parallel usage event.
- **Accidental CP table / DROP-list churn (T4):** Mitigation: AC9 — the persona is the 32-15 public
  `Agent` (CP-registered); benchmark rows are tenant-schema 32-10 entities (owned by
  `EfTenantDbMigrator`); facets (if persisted) are a **Tenant** migration; `ControlPlaneDbContextModelTests`
  untouched. Prefer the no-schema-change path (query-time facets).
- **Cross-role persona comparison sneaks in (T3):** Mitigation: persona key is `(role, agentId)`; the
  comparison query always names a role; test asserts an architect-role persona never appears in the
  reviewer leaderboard.
</content>
