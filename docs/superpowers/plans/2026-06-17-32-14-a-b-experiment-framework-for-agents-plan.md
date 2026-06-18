# Story 32-14 — A/B Experiment Framework for Agents (Phase 2)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan step-by-step. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every step writes tests
> before implementation. Read [BEFORE_YOU_CODE.md](../../guides/BEFORE_YOU_CODE.md) first.

**Goal:** Let a tenant (SaaS) or sole user (single-user) run a controlled A/B experiment for one
role — variants differing by agent / config version / provider / prompt / persona — split workflow
runs into deterministic cohorts, measure each arm against the **existing** per-tenant benchmark
read models (32-10), compute statistical significance, and **auto-promote the winner** (atomic
update of the per-role selection from 32-2) or **roll back** on a guarded regression, with
guardrails (min sample, max spend, provider gating) and tenant-scoped lifecycle events. This is the
production realization of the A/B-testing AC that Story 1-13 specified but never built.

**Story file:** `docs/stories/epic-32/story-32-14/32-14-a-b-experiment-framework-for-agents.md`
**Design of record:** `docs/superpowers/specs/2026-06-17-agent-entities-benchmarking-design.md` (§"Phase 2").

**Tech stack:** .NET 9 / EF Core 9 / Npgsql in `apps/tamma-elsa` (control-plane API + Elsa engine),
React/Vite dashboard in `packages/dashboard-user` (Vitest). C# tests live in
`apps/tamma-elsa/tests/Tamma.Api.Tests/` (xUnit; docker-bound suites run via
`sg docker -c "dotnet test ..."`). Experiments + variants + assignments + results are **tenant-schema-resident**
(`TenantDbContext`, `t_<hex>`) — never the control plane, never cross-tenant.

---

## Non-goals (YAGNI guard)

- **NO new measurement plumbing.** Significance is computed by *slicing* the 32-10 benchmark
  projections / 32-6 action-trail / 32-9 cost events by `experimentId` + `variantId`. The only
  measurement change is adding two tags to the existing trail builder. If 32-10's projections can
  measure it, this story reads them; it never recomputes a metric from raw events except where it
  already aggregates per-variant `VariantStats`.
- **NO change to resolution semantics off the experiment path.** A role with no running experiment
  resolves byte-for-byte as today (32-2 precedence). Assignment returns null and nothing changes.
  The seam is purely additive — `feedback_resolution_no_empty_fallback` stays intact.
- **NO control-plane experiment storage and NO platform-admin read path.** Performance data is
  always tenant-owned (Epic 32 design rule); a platform owner cannot read a tenant's experiment
  results, mirroring the 32-6 action-trail isolation.
- **NO per-user override layer in SaaS.** SaaS principal = tenant (`AgentManage` = owner/admin;
  member read-only). Single-user principal = the sole user. Mode via `ITammaModeProvider`, exactly
  as 32-1/32-2.
- **NO multi-experiment-per-role concurrency.** At most one `running` experiment per `(principal, role)`
  — a partial unique index + a 409 at start. Keeps cohort assignment unambiguous.
- **NO external heavyweight stats dependency on the core path.** The two-proportion z-test and
  Welch's t-test are implemented directly (standard-normal/t CDFs). A vetted package may back them
  only if pinned and behind the `ISignificanceCalculator` contract.
- **NO Bayesian / sequential-testing / multi-armed-bandit sophistication.** Fixed-horizon
  frequentist A/B with a min-sample floor is the v1; bandits are a later story if wanted.

---

## Current-state findings (verified 2026-06-17, repo @ main)

### What exists today (consumed, not built here)

| Seam | Where | Used for |
|---|---|---|
| `TenantDbContext` (per-tenant `t_<hex>`; tenant entities config'd via `TammaModelConfiguration.ConfigureTenantEntities`) | `apps/tamma-elsa/src/Tamma.Data/TenantDbContext.cs` | Where the new experiment tables live (additive tenant migration). |
| `DomainEvent` row + `IEventRepository.AppendAsync` (tenant-scoped writes route to `t_<hex>.domain_events`) | `apps/tamma-elsa/src/Tamma.Data/Entities/DomainEvent.cs`, `Repositories/IEventRepository.cs`, `EventRepository.cs` | Lifecycle DCB events (`AGENT.EXPERIMENT.*`). |
| `TaskQueueProcessor` (`ProcessOnceAsync`, `RunOnStartup`, poll loop) + `QueuedTask` (`Type`, `TenantId`, `Payload`, `Status`, retry) | `apps/tamma-elsa/src/Tamma.Api/Services/TaskQueue/TaskQueueProcessor.cs`, `Tamma.Data/Entities/QueuedTask.cs` | Async significance re-eval + `MaxSpendUsd` budget watch (`agent.experiment.evaluate` task). |
| Agent config endpoint discipline (validate-before-write, audit event only after a real write) | `apps/tamma-elsa/src/Tamma.Api/Endpoints/AgentEndpoints.cs` (`UpdateConfig`) | The "never a lie event" pattern the lifecycle events follow. |
| Auth policies `AgentManage` (`agents:manage`, owner/admin — from 32-2), `PlatformOwnerAccess`, `SettingsManage` | `apps/tamma-elsa/src/Tamma.Api/Program.cs` (~986–1115), `Auth/Permissions.cs` | Write RBAC on experiment endpoints. |
| `ITammaModeProvider` (process-stable SingleUser/SaaS) | `apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/TammaMode.cs` | Per-mode principal derivation. |
| Tenant-isolation test precedent | `apps/tamma-elsa/tests/Tamma.Api.Tests/Epic28/CrossTenantIsolationPostgresTests.cs` | Template for the isolation integration test. |

### Sibling-story seams (drafted; coordinate, do not re-implement)

| From | Artifact | This story's use |
|---|---|---|
| **32-1** | `Agent` / `AgentVersion` CP entities + `IAgentRepository`; immutable versions | Variants reference `(agentId, agentVersion)`; pinned versions survive archive. |
| **32-2** | `IAgentRegistryService` (`SelectForRoleAsync`, visibility-scoped `ListAsync`); `agent_role_selections` (tenant); `ResolvedAgentConfig` `+ AgentId/AgentVersion`; `AgentManage` policy | Variant validation (resolvable agent), the selection table rollout mutates, the config assignment pins onto, the write RBAC. |
| **32-4** | SaaS provider auth gating (API-key only; CLI/token single-user only) | Variant create rejects gated providers (400). |
| **32-6** | `AgentTrailTags.Build` + tenant action-trail event families | Add `experimentId`/`variantId` tags so trail + cost events are arm-attributable. |
| **32-9** | Usage & cost emission (per-run `costUsd`, tagged) | The `cost` metric and the `MaxSpendUsd` guardrail tally. |
| **32-10** | Per-tenant benchmark projections / leaderboards (success rate, iterations-to-done, defect rate, cost — sliceable) | The read substrate `ExperimentResultsService` slices by `experimentId`/`variantId`. **Hard dependency.** |
| **32-12** | Personas + persona-aware benchmarking | The `personaId` variant axis. |
| **32-13** | Tenant private benchmark dashboard | Where the experiment UI surface lives alongside. |

> If a 32-N artifact is not yet in `main` when this story starts, build against its named seam
> (interface/method) and add a thin local shim only where unavoidable; never fork its data model.

---

## Architecture

**Define → assign → measure → test → conclude (rollout/rollback)**, all tenant-scoped, reusing the
benchmark substrate end-to-end:

1. **`AgentExperiment` + `AgentExperimentVariant`** (new tenant-schema entities) — the experiment
   header (role, metric, min-sample, threshold, max-spend, status, baseline/winner, prior-selection
   snapshot) and its arms (agent/version/provider/prompt/persona + traffic weight). Additive
   `TenantDbContext` migration; config in `ConfigureTenantEntities` only.
2. **`ExperimentAssignmentService`** — on resolve of the role-under-test for an eligible run,
   deterministically map `StableHash(experimentId + correlationId)` onto the weighted split, pin
   `experimentId`/`variantId` + the variant's `(agentId, version, provider, promptRef, personaId)`
   onto `ResolvedAgentConfig` (32-2), and add the two tags to the action-trail builder (32-6).
   No running experiment → null → today's behaviour.
3. **`SignificanceCalculator`** (pure) — two-proportion z-test for rate metrics, Welch's t-test for
   continuous metrics; returns `{ pValue, effectSize, baselineN, challengerN, values, significant }`.
4. **`ExperimentResultsService`** — slices the 32-10 projections by `experimentId`/`variantId` into
   per-variant `VariantStats`; feeds the calculator; also computes the spend tally (32-9) for the
   guardrail.
5. **`ExperimentRolloutController`** — atomic winner-promote (snapshot prior selection → upsert
   `agent_role_selections` via 32-2) / rollback (restore snapshot), one tenant transaction.
6. **`AgentExperimentService`** — lifecycle orchestration (create/start/conclude/rollback) +
   guardrails (min-sample floor, max-spend, provider gating, single-running-per-role) + DCB events.
7. **TaskQueue handler** (`agent.experiment.evaluate`) — async re-eval + budget watch off the
   existing poll loop; auto-conclude/promote when `AutoRollout` + floors met + significant, or on
   `MaxSpendUsd`.
8. **Endpoints + dashboard** — `/api/v1/agents/experiments/*` (writes `AgentManage`, reads member);
   tenant-facing experiment UI in `packages/dashboard-user`.

### Per-mode ownership (mandatory two-scoping-model answer, per CLAUDE.md)

| Question | single-user mode | SaaS mode |
|---|---|---|
| Who owns an experiment? | The sole user — it's their instance/schema. | The tenant — lives in `t_<hex>`. |
| Who can create/start/conclude/rollback? | The user (no gate). | `tenant_owner`/`tenant_admin` (`AgentManage`); `member` → 403. |
| Who can read results? | The user. | Any tenant member (read-only). Platform owner: **no read path** (isolation). |
| Where do events land? | Tenant feed (= the user's). | Tenant `domain_events` (`TenantId` = tenant). |
| Principal source | `ITammaModeProvider` (process-stable). | same. |

---

## Story breakdown

### S1 — Experiment entities + tenant migration + repository (core)

**Scope:** New tenant-schema entities + DbSets + model config + additive migration + repository
CRUD. No assignment/significance/rollout yet.

**Files:**
- New: `Tamma.Data/Entities/AgentExperiment.cs`, `AgentExperimentVariant.cs`,
  `AgentExperimentMetric.cs` (enum), `AgentExperimentStatus.cs` (enum).
- Modify: `Tamma.Data/TenantDbContext.cs` (DbSets), `TammaModelConfiguration.cs`
  (`ConfigureTenantEntities`: tables, `ck_agent_experiments_min_sample`,
  `ck_agent_experiments_threshold`, `ck_agent_experiment_variants_weight`, cascade FK,
  `IX_agent_experiments_one_running_per_role` partial unique on `Status=Running`,
  `IX_agent_experiment_variants_label`). Additive migration under `Migrations/Tenant/`.
- New: `Tamma.Data/Repositories/IAgentExperimentRepository.cs` + `AgentExperimentRepository.cs`
  (`CreateAsync`, `GetByIdAsync`, `ListAsync`, `UpdateStatusAsync`, `SetWinnerAndSnapshotAsync`).

**Tests (first):** `tests/Tamma.Api.Tests/Agents/AgentExperimentRepositoryTests.cs` — create writes
header + variants (cascade); CHECK rejections (min-sample 0, threshold ≤0/≥1, weight 0); one-running-
per-role partial index rejects a second running row for the same role (Postgres fixture); list scoped
to the schema.

**Acceptance criteria:**
- [ ] Tenant migration applies; `dotnet ef migrations has-pending-model-changes --context TenantDbContext` → none.
- [ ] CHECK + partial-unique constraints enforced (verified against a real Postgres fixture).
- [ ] Variants cascade-delete with their experiment; full suite stays green.

### S2 — `SignificanceCalculator` (pure, fixture-driven — zero DB)

**Scope:** Two-proportion z-test + Welch's t-test + standard-normal & t CDFs. No I/O.

**Files:** New `Tamma.Api/Services/Agents/ISignificanceCalculator.cs` + `SignificanceCalculator.cs`
(`VariantStats`, `SignificanceResult` records).

**Tests (first):** `tests/Tamma.Api.Tests/Agents/SignificanceCalculatorTests.cs` — z-test against
precomputed `pValue`/`z`/effect for clearly-different / marginal / equal rates; degenerate `SE=0` →
`pValue=1` not significant. Welch's t against precomputed mean-diff / `df` / p for different / equal
means / zero-variance; Cohen's d; `N<2` guarded. Determinism (same input → same output).

**Acceptance criteria:**
- [ ] Computed p-values within tight tolerance of fixture expectations (cross-checked against a known stats reference offline).
- [ ] Degenerate cases (SE=0, zero variance, N<2) never throw and never declare significance.

### S3 — `ExperimentAssignmentService` (deterministic cohorts)

**Scope:** Resolve the running experiment for `(role)` in the tenant, deterministically assign a run
to a variant by weighted split keyed on the run correlation id, pin onto `ResolvedAgentConfig`, add
trail tags.

**Files:**
- New: `Tamma.Api/Services/Agents/IExperimentAssignmentService.cs` + `ExperimentAssignmentService.cs`
  (`StableHash` via SHA-256 → uint64; weighted-bucket select; `ExperimentAssignment` record).
- Modify: `Tamma.Api/Services/Agents/AgentTrailTags.cs` (add `experimentId`/`variantId` — coordinate
  with 32-6); wire assignment into the 32-2 resolve path so the variant's pinned `(agentId, version,
  provider, promptRef, personaId)` materializes into `ResolvedAgentConfig`.

**Tests (first):** `tests/Tamma.Api.Tests/Agents/ExperimentAssignmentServiceTests.cs` — 100k keys
across weights `{50,30,20}` → empirical split within tolerance; same key → same variant
(determinism) across two process runs; different `experimentId` reshuffles; no running experiment →
null (no behaviour change); `StableHash` is NOT `string.GetHashCode()` (assert stability).

**Acceptance criteria:**
- [ ] Distribution converges to configured weights; assignment is stable per key and process-independent.
- [ ] A role with no running experiment resolves exactly as today (assignment returns null).
- [ ] `VARIANT_ASSIGNED` emitted once per run (not per resolution call).

### S4 — `ExperimentResultsService` (slice 32-10) + spend tally

**Scope:** Build per-variant `VariantStats` for the experiment's metric by reading the tenant's
32-10 projections / 32-6 trail / 32-9 cost events filtered by `experimentId`/`variantId`; compute
cumulative experiment spend for the guardrail.

**Files:** New `Tamma.Api/Services/Agents/ExperimentResultsService.cs` (`GetResultsAsync` →
`{ per-variant VariantStats, metric, status }`; `GetSpendUsdAsync`).

**Tests (first):** `tests/Tamma.Api.Tests/Agents/ExperimentResultsServiceTests.cs` — seed synthetic
trail/cost events tagged per arm; success-rate / defect-rate / iterations / cost each aggregate to
the right `VariantStats`; spend tally sums tagged cost; an arm with zero events → `N=0`.

**Acceptance criteria:**
- [ ] Results derive entirely from the existing substrate (no new measurement events introduced).
- [ ] Each metric maps to the correct `VariantStats` shape (successes for rates; Sum/SumSq for continuous).

### S5 — `ExperimentRolloutController` (atomic promote / rollback)

**Scope:** Atomic winner promotion (snapshot prior selection → upsert `agent_role_selections` via
32-2 `SelectForRoleAsync`) and rollback (restore snapshot), one tenant transaction each.

**Files:** New `Tamma.Api/Services/Agents/ExperimentRolloutController.cs` +
`IExperimentRolloutController`.

**Tests (first):** `tests/Tamma.Api.Tests/Agents/ExperimentRolloutControllerTests.cs` — promote
upserts the selection to the winner + snapshots prior in one tx; rollback restores it; forced
mid-tx failure leaves the selection unchanged; rollback with no prior selection deletes the
experiment-set selection (falls back to system default per 32-2), not a dangling row.

**Acceptance criteria:**
- [ ] Promote + snapshot are atomic; a partial promote never leaves a dangling/half-applied selection.
- [ ] Rollback restores exactly the snapshotted prior state (or clean fallback when none).

### S6 — `AgentExperimentService` lifecycle + guardrails + DCB events

**Scope:** Orchestrate create/start/conclude/rollback; enforce guardrails (min-sample floor before
any conclusion, max-spend auto-conclude, provider gating at create, single-running-per-role,
weights-sum); emit `AGENT.EXPERIMENT.*` events only after real transitions.

**Files:** New `Tamma.Api/Services/Agents/AgentExperimentService.cs`, `AgentExperimentEventTypes.cs`.
Validates variants via `IAgentRegistryService` (resolvable) + 32-4 provider gating.

**Tests (first):** `tests/Tamma.Api.Tests/Agents/AgentExperimentServiceTests.cs` — min-sample guard
(no winner below floor even if "significant"); max-spend auto-concludes + `BUDGET_REACHED`;
provider-gating reject at create (SaaS CLI provider → 400, single-user allows); single-running-per-
role 409; weights-sum mismatch 400; each transition emits exactly one event; no event on a no-op
(re-conclude). Mode-parameterized (SingleUser vs SaaS principal).

**Acceptance criteria:**
- [ ] Significance is never evaluated until every variant clears `MinSampleSize`.
- [ ] Max-spend / provider-gating / single-running-per-role / weights-sum guardrails all enforced.
- [ ] Lifecycle events emitted only after a real state transition.

### S7 — Endpoints + RBAC + TaskQueue handler

**Scope:** REST surface + per-mode RBAC + async re-eval/budget-watch off `TaskQueueProcessor`.

**Files:**
- New: `Tamma.Api/Endpoints/AgentExperimentEndpoints.cs`, `Dtos/Agents/AgentExperimentDtos.cs`.
- Modify: `Program.cs` (map routes under `/api/v1/agents/experiments`; writes `AgentManage`, reads
  member; register services; register the `agent.experiment.evaluate` queued-task handler).

**Tests (first):** `tests/Tamma.Api.Tests/Agents/AgentExperimentEndpointsTests.cs` — RBAC matrix
(owner/admin write 200, member write 403, member read 200, single-user all 200); cross-tenant
GET → 404; create→start→(synthetic results)→conclude(promote)→rollback end-to-end within one tenant;
202/async re-eval auto-concludes when wired.

**Acceptance criteria:**
- [ ] Endpoint shape identical between modes; auth middleware decides scope (Prompt Store precedent).
- [ ] Cross-tenant access returns 404 (existence not leaked); platform owner has no results path.

### S8 — Dashboard surface (tenant-facing)

**Scope:** Experiment list + detail (cohort config, live per-variant results + significance,
conclude/rollback actions) in the user dashboard. No platform-admin experiment UI.

**Files:** New `packages/dashboard-user/src/services/experiments-client.ts`,
`pages/experiments/ExperimentsPage.tsx`, `pages/experiments/ExperimentDetailPage.tsx`; register
routes alongside the 32-13 benchmark dashboard.

**Tests (first):** colocated Vitest + Testing Library — list renders rows/status; detail renders
per-variant `n` + significance; conclude/rollback call the client; member sees results without
write actions; zero-experiment empty state.

**Acceptance criteria:**
- [ ] Tenant owner/admin sees conclude/rollback; member sees read-only results.
- [ ] `pnpm test --filter @tamma/dashboard-user` green; no new lint errors.

---

## Story order & dependencies

S1 → S2 (parallel-safe, pure) → S3 → S4 → S5 → S6 (needs S2/S4/S5) → S7 → S8.
S2 and S3 have zero DB dependency and can start immediately (TDD-friendly first targets).
S1 is the only hard prerequisite for S4–S7. S8 needs S7's API.

## Risks

- **Determinism / split correctness:** using `string.GetHashCode()` (process-randomized) instead of
  a content-addressed hash silently breaks reproducibility and the distribution. S3's tests must
  assert process-stable hashing and run the distribution check.
- **Statistics correctness:** a wrong CDF approximation produces plausible-but-wrong p-values →
  bad rollouts. S2 cross-checks every fixture against a known stats reference offline; the
  min-sample floor + significance gate together are the safety net against thin-evidence wins.
- **Cross-tenant leakage:** experiment results read benchmark data — the read path must scope to the
  tenant schema via `ITenantDbContextFactory`; a platform owner must have no path. Covered by the
  isolation integration test (mirror `CrossTenantIsolationPostgresTests.cs`).
- **Rollout atomicity:** a half-applied promote (selection moved but snapshot not stored, or vice
  versa) corrupts rollback. S5 wraps both in one tenant transaction and tests the forced-failure
  path.
- **Dependency on unbuilt siblings (32-9/32-10/32-12/32-13 story files not yet written):** build
  against their named seams from the design spec; if a seam is absent at start, add a thin shim
  behind the interface and flag it for reconciliation — never fork the data model. The Epic 32
  design spec is the contract.
- **Migration discipline:** `agent_experiments` / `agent_experiment_variants` are additive
  tenant-schema tables — normal `dotnet ef migrations add ... --context TenantDbContext`; verify
  `has-pending-model-changes` reports none; mirror config only in `TammaModelConfiguration`.
- **Alert/event volume:** `VARIANT_ASSIGNED` must emit once per run, not per resolution call, or a
  hot loop floods the tenant stream. S3 pins this.
