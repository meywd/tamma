# Story 32-14: A/B Experiment Framework for Agents (Phase 2: cohorts, significance, rollout/rollback)

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

This mandatory guide includes the 7-Phase Development Workflow (Read → Research → Break Down → TDD → Quality Gates → Failure Handling), Knowledge Base usage (`.dev/` directory), TRACE/DEBUG logging requirements, Test-Driven Development, 100% critical-path coverage, and build-success enforcement.

**Failure to follow this process will result in rework.**

## User Story

As a **tenant owner/admin (SaaS) or self-hosted user (single-user)**,
I want to run a controlled A/B experiment for one role — defining variants that differ by agent, config version, provider, prompt, or persona, splitting my workflow runs into cohorts, and measuring each variant against the benchmark metrics my own runs already produce —
So that I can decide *with statistical evidence* which agent configuration performs best for my work (higher success rate, fewer iterations-to-done, lower defect rate, lower cost), then **auto-promote the winner** to my per-role selection (or **roll back** on a guarded regression) instead of guessing from a leaderboard snapshot.

## Priority

P2 — Phase 2 of Epic 32. It is the production realization of the A/B-testing acceptance criterion that Story 1-13 specified but never built. It is additive on top of the agent entity (32-1), registry/selection (32-2), action trail (32-6), usage/cost emission (32-9), and benchmark projections (32-10); nothing else in Epic 32 depends on it. Experiments are strictly **tenant-scoped** — they observe and mutate only the calling tenant's data.

## Acceptance Criteria

1. **Experiment entity (tenant-scoped).** A new `AgentExperiment` entity persists in the **resolving tenant's** `t_<hex>` schema (NOT the control plane — performance data and the experiments that read it are always tenant-owned per the Epic 32 design tenancy rule). Fields: `Id` (Guid PK), `Name`, `Role` (one of `RolePhaseMap.ValidRoles`), `Metric` (enum `success-rate` | `iterations-to-done` | `defect-rate` | `cost`), `MinSampleSize` (int per variant), `SignificanceThreshold` (double, default `0.05` = 95% confidence), `MaxSpendUsd` (decimal? guardrail), `Status` (enum `draft` | `running` | `concluded` | `rolled-back`), `WinnerVariantId` (Guid?), `BaselineVariantId` (Guid, the control), `StartedAt`/`ConcludedAt`/`CreatedAt`/`CreatedBy`/`UpdatedAt`/`UpdatedBy`. In single-user mode the principal is the sole user; in SaaS the principal is the tenant — derived via `ITammaModeProvider` exactly as 32-1/32-2 do (no per-user layer in SaaS).

2. **Variants.** A child `AgentExperimentVariant` entity (`Id`, `ExperimentId` FK `OnDelete(Cascade)`, `Label`, `AgentId` Guid, `AgentVersion` int?, `Provider` string?, `PromptRef` string?, `PersonaId` Guid?, `TrafficWeight` int) defines each arm. Every variant references an agent the tenant can actually resolve (public ∪ own private, validated through `IAgentRegistryService` from 32-2). Exactly one variant per experiment is the baseline (`BaselineVariantId`). Variant weights must be positive integers summing to a configured total (default 100); an experiment with `< 2` variants or weights summing to `0` is rejected at create with 400.

3. **Deterministic cohort assignment across runs.** When a workflow resolves the role under test for an eligible run, an `ExperimentAssignmentService` deterministically maps a stable assignment key — `hash(experimentId + correlationId)` (the run/issue correlation id, NOT wall clock) — onto the weighted variant split, so the same run always lands in the same cohort and the distribution converges to the configured weights. The chosen `experimentId` + `variantId` are pinned onto the `ResolvedAgentConfig` (32-2) for that run **and** stamped onto the action trail (32-6) as tags `experimentId` / `variantId`, so every `AGENT.TASK.*` / cost event the run emits is attributable to its arm. A run not eligible for any running experiment resolves exactly as it does today (zero behaviour change off the experiment path).

4. **Outcome metrics reuse 32-10 projections — no new measurement plumbing.** Significance is computed over the existing per-tenant benchmark read models (32-10) and action-trail/diagnostics data (32-6/32-9), sliced by `experimentId` + `variantId`. Per variant the framework derives: trials (`n`), successes (for `success-rate`/`defect-rate`), and the per-trial continuous values (iterations-to-done, cost) — by querying the tenant's own `domain_events` / projections filtered on the experiment tags. No experiment-specific metric is recomputed from scratch; the experiment is a *slice* of the benchmark substrate.

5. **Statistical significance test.** A pure `SignificanceCalculator` selects the appropriate test by metric type: a **two-proportion z-test** for rate metrics (`success-rate`, `defect-rate`) and **Welch's two-sample t-test** for continuous metrics (`iterations-to-done`, `cost`), each comparing a challenger variant against the baseline. It returns `{ pValue, effectSize, baselineN, challengerN, baselineMean|Rate, challengerMean|Rate, significant }` where `significant = pValue < SignificanceThreshold`. The math is deterministic and fully unit-tested against fixed fixtures with known expected p-values/effect sizes.

6. **Guardrails — never conclude early, never overspend, honor provider gating.** (a) A variant is **ineligible to win** until its trial count `≥ MinSampleSize`; significance is not even evaluated until *every* variant meets the floor — a hot run loop cannot trip an early conclusion. (b) If cumulative experiment spend (summed from the tenant's cost events tagged with the experiment, reusing 32-9) reaches `MaxSpendUsd`, the experiment auto-concludes on current evidence (winner if significant, else baseline) and emits `AGENT.EXPERIMENT.BUDGET_REACHED`. (c) Every variant's agent/provider must pass SaaS provider auth gating (32-4) at create time — a variant naming a CLI/token provider in SaaS is rejected with 400 (`experiment_variant_provider_gated`); per-tenant budgets (32-9) still clamp each individual run regardless of the experiment.

7. **Rollout to winner / rollback — atomic against the per-role selection.** Concluding a running experiment with a statistically significant winner can (when `autoRollout` is set, or on an explicit `POST .../conclude?promote=true`) update the tenant's per-role agent selection (`agent_role_selections`, 32-2) to the winning variant's `(agentId, agentVersion)` **in a single transaction**, snapshotting the *prior* selection into the experiment row first. `POST .../rollback` restores that snapshotted prior selection atomically and flips status to `rolled-back`. A conclusion with no significant winner promotes nothing and leaves the baseline selection untouched.

8. **Lifecycle events on the tenant DCB stream.** `AGENT.EXPERIMENT.STARTED` (draft → running), `AGENT.EXPERIMENT.VARIANT_ASSIGNED` (each cohort assignment; deduped/sampled so a 1000×/min loop does not flood the stream — emit per-run, not per-resolution-call), `AGENT.EXPERIMENT.CONCLUDED` (with `winnerVariantId`, `pValue`, `promoted` bool), `AGENT.EXPERIMENT.ROLLED_BACK`, and the guardrail `AGENT.EXPERIMENT.BUDGET_REACHED` are appended via `IEventRepository.AppendAsync` into the **tenant's** `domain_events` table (`TenantId` = the experiment's tenant). Tags carry `experimentId`, `variantId?`, `role`, `metric`, `mode`. Events are emitted only after a real state transition (same discipline as `AGENT_CONFIG.UPDATED.SUCCESS` — never a lie event).

9. **Management API + per-mode RBAC (owner/admin only for writes).** Endpoints added to `AgentEndpoints.cs` (or a new `AgentExperimentEndpoints.cs`) and mapped in `Program.cs`:
   - `POST   /api/v1/agents/experiments` — create (draft).
   - `GET    /api/v1/agents/experiments` / `GET .../experiments/{id}` — list / detail (live cohort config + current results).
   - `GET    /api/v1/agents/experiments/{id}/results` — per-variant `n`, metric value, and significance vs baseline (computed live from 32-10).
   - `POST   /api/v1/agents/experiments/{id}/start` — draft → running.
   - `POST   /api/v1/agents/experiments/{id}/conclude` (`?promote=true|false`) — running → concluded, optional winner rollout.
   - `POST   /api/v1/agents/experiments/{id}/rollback` — restore prior selection.
   Writes require tenant **owner/admin** (the `AgentManage` policy = `agents:manage` from 32-2, mirroring Prompt Store RBAC); SaaS `member` → **403**. Reads are member-readable. Single-user: the sole user does everything, no gate. Cross-tenant access returns **404** (existence not leaked).

10. **Cross-tenant isolation.** An experiment, its variants, its assignments, and its results are visible and mutable only within the owning tenant's schema. Two tenants each running an experiment named `atlas-vs-orion` on the `reviewer` role never see, assign into, or conclude each other's experiment. A platform owner has **no** read path into a tenant's experiment results (mirrors the 32-6 action-trail isolation). Explicitly tested.

11. **Concurrency / single-running-per-role guard.** At most one experiment may be in `running` status for a given `(principal, role)` at a time — starting a second returns **409** (`experiment_role_conflict`). This keeps cohort assignment unambiguous (a run is in at most one experiment's split for its role).

12. **Tests** cover: cohort split distribution (assigning N keys yields a distribution within tolerance of the configured weights, and is stable/deterministic per key); significance math on fixtures (two-proportion z and Welch's t against precomputed expected values, including the degenerate equal-rate and zero-variance cases); winner rollout updates `agent_role_selections` and the prior selection is snapshotted; rollback restores it atomically; the `MinSampleSize` guard prevents conclusion below the floor; the `MaxSpendUsd` guardrail auto-concludes and emits `BUDGET_REACHED`; provider-gating rejection at create (SaaS CLI provider variant); the single-running-per-role 409; and tenant isolation (tenant B cannot read/assign/conclude tenant A's experiment, platform owner cannot read results). Critical paths (assignment determinism, significance, rollout/rollback transaction, guardrails) → 100%.

## Technical Design

### Architectural placement (per the Epic 32 design of record)

Per `docs/superpowers/specs/2026-06-17-agent-entities-benchmarking-design.md` §"Ownership, visibility & data scoping" and §"Tracking … Phase 2: A/B experiment framework":

- **Agent definitions** (public/private) are control-plane / CP-resident (32-1).
- **Performance + action data is ALWAYS tenant-scoped.** An A/B experiment *reads* that data and *acts on* the tenant's own per-role selection — so the experiment, its variants, its assignments, and its results all live in the tenant's `t_<hex>` schema (`TenantDbContext`), never on the control plane and never cross-tenant. This is the same structural isolation that backs the 32-6 action trail.
- This story is purely **additive**: new tenant-schema tables + a deterministic assignment seam wired into the existing resolve path + a significance calculator over the 32-10 read models + a rollout controller over the 32-2 selection table. It does not change resolution semantics off the experiment path.

Story 32-1 (entities), 32-2 (`IAgentRegistryService`, `agent_role_selections`, `ResolvedAgentConfig` enrichment, `AgentManage` policy), 32-6 (action-trail tags), 32-9 (cost emission), and 32-10 (benchmark projections) are assumed present. Where a sibling-story artifact is referenced and not yet confirmed in code, it is marked **(from 32-N)** and the integration is via that story's named seam, not a reimplementation.

### C# namespace / file structure

```
apps/tamma-elsa/src/
  Tamma.Data/
    Entities/
      AgentExperiment.cs                 # NEW — tenant-scoped experiment header
      AgentExperimentVariant.cs          # NEW — one arm (agent/version/provider/prompt/persona + weight)
      AgentExperimentMetric.cs           # NEW — enum { SuccessRate, IterationsToDone, DefectRate, Cost }
      AgentExperimentStatus.cs           # NEW — enum { Draft, Running, Concluded, RolledBack }
    TenantDbContext.cs                   # MODIFY — add DbSet<AgentExperiment>, DbSet<AgentExperimentVariant>
    TammaModelConfiguration.cs           # MODIFY — ConfigureTenantEntities: tables, indexes, CHECK, cascade FK
    Repositories/
      IAgentExperimentRepository.cs      # NEW
      AgentExperimentRepository.cs       # NEW
    Migrations/Tenant/
      <ts>_AddAgentExperiments.cs        # NEW — additive tenant-schema migration
  Tamma.Api/
    Services/Agents/
      IExperimentAssignmentService.cs    # NEW — deterministic weighted cohort assignment
      ExperimentAssignmentService.cs     # NEW
      SignificanceCalculator.cs          # NEW — pure: two-proportion z + Welch's t
      ISignificanceCalculator.cs         # NEW
      ExperimentResultsService.cs        # NEW — slices 32-10 projections by experiment/variant
      ExperimentRolloutController.cs     # NEW — atomic winner-promote / rollback over agent_role_selections
      AgentExperimentService.cs          # NEW — orchestrates lifecycle (create/start/conclude/rollback) + events
      AgentExperimentEventTypes.cs       # NEW — AGENT.EXPERIMENT.* constants
    Endpoints/
      AgentEndpoints.cs                  # MODIFY (or AgentExperimentEndpoints.cs NEW) — experiment routes
    Dtos/Agents/
      AgentExperimentDtos.cs             # NEW — request/response records
    Services/TaskQueue/                  # REUSE — async significance re-eval + budget-watch via QueuedTask
    Program.cs                           # MODIFY — register services + map routes with AgentManage RBAC
packages/dashboard-user/src/
  pages/experiments/                     # NEW — tenant experiment list + detail (cohort config, live results, conclude/rollback)
  services/experiments-client.ts         # NEW — typed API client
```

### Entities (sketch)

```csharp
// Tamma.Data/Entities/AgentExperimentMetric.cs
public enum AgentExperimentMetric { SuccessRate, IterationsToDone, DefectRate, Cost }

// Tamma.Data/Entities/AgentExperimentStatus.cs
public enum AgentExperimentStatus { Draft, Running, Concluded, RolledBack }

// Tamma.Data/Entities/AgentExperiment.cs  (tenant-schema; t_<hex>.agent_experiments)
public class AgentExperiment
{
    public Guid Id { get; set; }
    public string Name { get; set; } = null!;
    public string Role { get; set; } = null!;                 // RolePhaseMap.ValidRoles
    public AgentExperimentMetric Metric { get; set; }
    public int MinSampleSize { get; set; }                    // per variant, > 0
    public double SignificanceThreshold { get; set; } = 0.05; // p-value gate
    public decimal? MaxSpendUsd { get; set; }                 // guardrail; null = unbounded
    public bool AutoRollout { get; set; }
    public AgentExperimentStatus Status { get; set; } = AgentExperimentStatus.Draft;
    public Guid BaselineVariantId { get; set; }               // the control arm
    public Guid? WinnerVariantId { get; set; }
    public string? PriorSelectionSnapshot { get; set; }       // JSON {agentId, agentVersion} captured before promote
    public DateTime? StartedAt { get; set; }
    public DateTime? ConcludedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTime UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }

    public ICollection<AgentExperimentVariant> Variants { get; set; } = new List<AgentExperimentVariant>();
}

// Tamma.Data/Entities/AgentExperimentVariant.cs  (t_<hex>.agent_experiment_variants)
public class AgentExperimentVariant
{
    public Guid Id { get; set; }
    public Guid ExperimentId { get; set; }
    public string Label { get; set; } = null!;
    public Guid AgentId { get; set; }            // resolvable: public ∪ own private (validated via 32-2)
    public int? AgentVersion { get; set; }        // null = agent's current version
    public string? Provider { get; set; }         // optional variant axis
    public string? PromptRef { get; set; }
    public Guid? PersonaId { get; set; }          // 32-12 persona axis
    public int TrafficWeight { get; set; }        // positive int; weights sum to total (default 100)

    public AgentExperiment? Experiment { get; set; }
}
```

### EF model config (in `TammaModelConfiguration.ConfigureTenantEntities`, tenant-only)

```csharp
modelBuilder.Entity<AgentExperiment>(entity =>
{
    entity.ToTable("agent_experiments", t =>
    {
        t.HasCheckConstraint("ck_agent_experiments_min_sample", "\"MinSampleSize\" > 0");
        t.HasCheckConstraint("ck_agent_experiments_threshold",
            "\"SignificanceThreshold\" > 0 AND \"SignificanceThreshold\" < 1");
    });
    entity.HasKey(e => e.Id);
    entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
    entity.Property(e => e.Name).IsRequired().HasMaxLength(128);
    entity.Property(e => e.Role).IsRequired().HasMaxLength(64);
    entity.Property(e => e.Metric).HasConversion<int>();
    entity.Property(e => e.Status).HasConversion<int>().HasDefaultValue(AgentExperimentStatus.Draft);
    entity.Property(e => e.PriorSelectionSnapshot).HasColumnType("jsonb");
    entity.Property(e => e.MaxSpendUsd).HasColumnType("numeric(12,4)");
    entity.Property(e => e.CreatedAt).HasDefaultValueSql("now()");
    entity.Property(e => e.UpdatedAt).HasDefaultValueSql("now()");
    // AC 11: at most one running experiment per role (per principal == per tenant schema)
    entity.HasIndex(e => e.Role)
        .IsUnique()
        .HasFilter("\"Status\" = 1")   // 1 = Running
        .HasDatabaseName("IX_agent_experiments_one_running_per_role");
});

modelBuilder.Entity<AgentExperimentVariant>(entity =>
{
    entity.ToTable("agent_experiment_variants", t =>
        t.HasCheckConstraint("ck_agent_experiment_variants_weight", "\"TrafficWeight\" > 0"));
    entity.HasKey(e => e.Id);
    entity.Property(e => e.Id).HasDefaultValueSql("gen_random_uuid()");
    entity.HasOne(e => e.Experiment)
        .WithMany(x => x.Variants)
        .HasForeignKey(e => e.ExperimentId)
        .OnDelete(DeleteBehavior.Cascade);   // variants are owned by the experiment
    entity.HasIndex(e => new { e.ExperimentId, e.Label })
        .IsUnique().HasDatabaseName("IX_agent_experiment_variants_label");
});
```

> Per Epic 28 conventions these are tenant-resident: in the target architecture the tenant DB holds only that tenant's rows, so no `TenantId` column appears on the tables (the `ConfigureTenantEntities` configurator strips it). The `IX_agent_experiments_one_running_per_role` partial unique index is per-schema → naturally per-tenant; in single-user mode the user's instance is the schema.

### Deterministic cohort assignment

```csharp
// Tamma.Api/Services/Agents/IExperimentAssignmentService.cs
public interface IExperimentAssignmentService
{
    /// <summary>
    /// Resolve the active experiment (if any) for (role) within the calling
    /// tenant and deterministically assign this run to a variant by weighted
    /// split keyed on assignmentKey (the run/issue correlationId — NEVER wall
    /// clock — so a run is stable across retries and re-resolutions).
    /// Returns null when no experiment is running for the role.
    /// </summary>
    Task<ExperimentAssignment?> AssignAsync(string role, string assignmentKey, CancellationToken ct = default);
}

public sealed record ExperimentAssignment(Guid ExperimentId, Guid VariantId, Guid AgentId, int? AgentVersion);
```

Weighted-split algorithm (stable, weight-proportional):

```
total   = Σ variant.TrafficWeight
bucket  = (uint)(StableHash(experimentId + ":" + assignmentKey) % total)
acc     = 0
foreach variant ordered by Id:
    acc += variant.TrafficWeight
    if bucket < acc: return variant      // deterministic; same key → same arm
```

`StableHash` is a fixed, content-addressed hash (e.g. SHA-256 of the UTF-8 bytes, first 8 bytes as `uint64`) — **not** `string.GetHashCode()` (process-randomized in .NET). The hash and modulo are pure and unit-tested for distribution + determinism. The assignment is pinned onto `ResolvedAgentConfig` (32-2) and added to the action-trail tag builder (32-6) as `experimentId` / `variantId`, with `AGENT.EXPERIMENT.VARIANT_ASSIGNED` emitted once per run.

### Significance calculation (pure)

```csharp
// Tamma.Api/Services/Agents/SignificanceCalculator.cs
public sealed record VariantStats(Guid VariantId, int N, int Successes, double Sum, double SumSq);
public sealed record SignificanceResult(
    Guid BaselineVariantId, Guid ChallengerVariantId,
    double PValue, double EffectSize,
    int BaselineN, int ChallengerN,
    double BaselineValue, double ChallengerValue,
    bool Significant);

public interface ISignificanceCalculator
{
    // Rate metrics (success-rate, defect-rate): two-proportion z-test.
    SignificanceResult TwoProportionZ(VariantStats baseline, VariantStats challenger, double alpha);
    // Continuous metrics (iterations-to-done, cost): Welch's two-sample t-test.
    SignificanceResult WelchT(VariantStats baseline, VariantStats challenger, double alpha);
}
```

- **Two-proportion z-test**: pooled proportion `p̂ = (x₁+x₂)/(n₁+n₂)`; `SE = √(p̂(1−p̂)(1/n₁+1/n₂))`; `z = (p̂₁−p̂₂)/SE`; two-tailed `pValue = 2·(1−Φ(|z|))` via a standard-normal CDF approximation (Abramowitz–Stegun erf); `effectSize` = absolute rate difference. Degenerate `SE=0` (both rates 0 or 1) → `pValue = 1`, not significant.
- **Welch's t-test**: per-variant mean/variance from `Sum`/`SumSq`/`N`; Welch `t` and Welch–Satterthwaite `df`; two-tailed p via a t-distribution CDF (regularized incomplete beta); `effectSize` = Cohen's d (pooled-SD). Zero-variance/`N<2` guarded → not significant.

Both are deterministic, dependency-light (no external stats library required for the core path; if a vetted package is added, pin it and keep the public contract), and exhaustively fixture-tested.

### Results service — slicing 32-10, not re-measuring

`ExperimentResultsService.GetResultsAsync(experimentId)` reads the tenant's benchmark projections / action-trail (32-10/32-6) filtered by the `experimentId` tag, groups by `variantId`, and builds a `VariantStats` per arm for the experiment's `Metric`:
- `success-rate` → `N` = `AGENT.TASK.*` count, `Successes` = `AGENT.TASK.SUCCESS` count.
- `defect-rate` → `N` = task count, `Successes` = runs with ≥1 `REVIEW.BUG.RECORDED` (the "defect" event), reported as a rate.
- `iterations-to-done` → per-run `AGENT.ITERATION.COMPLETED` max / `iterations` from `AGENT.TASK.*` `Data`, accumulated into `Sum`/`SumSq`.
- `cost` → per-run `costUsd` from cost events (32-9), accumulated into `Sum`/`SumSq` (also the source for the `MaxSpendUsd` guardrail tally).

The framework adds **no** new measurement events — it is a query/slice over the existing substrate, which is why 32-10 is a hard dependency.

### Rollout controller — atomic selection mutation

```csharp
// Tamma.Api/Services/Agents/ExperimentRolloutController.cs
public interface IExperimentRolloutController
{
    // Promote the winner: snapshot prior selection into the experiment row,
    // then upsert agent_role_selections to the winner's (agentId, version) — one transaction.
    Task PromoteWinnerAsync(AgentExperiment exp, AgentExperimentVariant winner, CancellationToken ct);
    // Restore the snapshotted prior selection — one transaction.
    Task RollbackAsync(AgentExperiment exp, CancellationToken ct);
}
```

`PromoteWinnerAsync`:

```
BEGIN
  prior = SELECT agentId, version FROM agent_role_selections WHERE Role=@role   -- (32-2 selection)
  UPDATE agent_experiments SET PriorSelectionSnapshot=@prior, WinnerVariantId=@w,
         Status=Concluded, ConcludedAt=now() WHERE Id=@exp
  UPSERT agent_role_selections (Role=@role, AgentId=@winnerAgent, Version=@winnerVersion)  -- via IAgentRegistryService.SelectForRoleAsync
COMMIT
→ emit AGENT.EXPERIMENT.CONCLUDED { winnerVariantId, pValue, promoted:true }
```

`RollbackAsync` reverses the upsert from `PriorSelectionSnapshot`, flips `Status=RolledBack`, emits `AGENT.EXPERIMENT.ROLLED_BACK`. Both run through the tenant `TenantDbContext` transaction so a partial promote can never leave a dangling selection.

### Async re-evaluation + budget watch (reuse TaskQueue)

Significance is recomputed live on `GET .../results`, but a background re-evaluation (and the `MaxSpendUsd` budget watch) runs off the existing `TaskQueueProcessor` (`Services/TaskQueue/`): on each tick a `agent.experiment.evaluate` `QueuedTask` (tenant-scoped) re-reads results for running experiments, and — when `AutoRollout` is set, every variant meets `MinSampleSize`, and a winner is significant — auto-concludes + promotes; or auto-concludes on `MaxSpendUsd`. This reuses the same poll-loop pattern as queued tenant moves; no new background-service plumbing.

### DCB event names (NEW)

| Event | When | Tags |
|---|---|---|
| `AGENT.EXPERIMENT.STARTED` | draft → running | `experimentId, role, metric, mode` |
| `AGENT.EXPERIMENT.VARIANT_ASSIGNED` | a run is assigned to a cohort (once per run) | `experimentId, variantId, role, mode` |
| `AGENT.EXPERIMENT.CONCLUDED` | running → concluded | `experimentId, role, metric, mode` + `Data { winnerVariantId, pValue, promoted }` |
| `AGENT.EXPERIMENT.ROLLED_BACK` | prior selection restored | `experimentId, role, mode` |
| `AGENT.EXPERIMENT.BUDGET_REACHED` | `MaxSpendUsd` guardrail tripped | `experimentId, role, mode` + `Data { spendUsd, maxSpendUsd }` |

All appended via `IEventRepository.AppendAsync` into the **tenant** `domain_events` (`TenantId` = the experiment's tenant), `Metadata` = standard DCB envelope (`workflowVersion`, `eventSource:"system"`).

### API shape

```
POST /api/v1/agents/experiments
  body: { name, role, metric, minSampleSize, significanceThreshold?, maxSpendUsd?, autoRollout?,
          variants:[{ label, agentId, agentVersion?, provider?, promptRef?, personaId?, trafficWeight }],
          baselineLabel }
  → 201 ExperimentDetail   RBAC: AgentManage (owner/admin); member → 403
POST /api/v1/agents/experiments/{id}/start     → 200 (draft→running) | 409 experiment_role_conflict
GET  /api/v1/agents/experiments                 → 200 [ ExperimentSummary ]   (member-readable)
GET  /api/v1/agents/experiments/{id}            → 200 ExperimentDetail | 404
GET  /api/v1/agents/experiments/{id}/results    → 200 { variants:[{ variantId, n, value, significance? }], metric, status }
POST /api/v1/agents/experiments/{id}/conclude?promote=true|false  → 200 ExperimentDetail   RBAC: AgentManage
POST /api/v1/agents/experiments/{id}/rollback   → 200 ExperimentDetail   RBAC: AgentManage
```

Per-mode + per-tenant handling: writes gated by `AgentManage` (= `agents:manage`, admin+owner; 32-2). Reads member-readable. Single-user: sole user, no gate. A `GET {id}` for another tenant's experiment → **404** (existence not leaked); the tenant schema makes cross-tenant rows physically unreachable anyway.

### Integration points

- **`IAgentRegistryService`** (`Tamma.Api/Services/Agents/`, from 32-2): validates each variant's `agentId` is resolvable (public ∪ own private); `SelectForRoleAsync` is the seam the rollout controller upserts through.
- **`ResolvedAgentConfig`** (32-2): assignment pins `experimentId`/`variantId` + materializes the variant's `(agentId, version, provider, promptRef, personaId)` into the resolved config so the run executes the arm.
- **`AgentTrailTags.Build`** (`Tamma.Api/Services/Agents/AgentTrailTags.cs`, 32-6): add `experimentId`/`variantId` so every trail + cost event is arm-attributable.
- **Benchmark projections** (32-10) + **cost emission** (32-9): the read substrate the results service slices; the cost tally for the `MaxSpendUsd` guardrail.
- **Provider gating** (32-4): variant create validates SaaS API-key-only gating.
- **`IEventRepository`** (`Tamma.Data/Repositories/EventRepository.cs`): tenant DCB emission.
- **`ITammaModeProvider`** (`Tamma.Api/Services/PromptStore/TammaMode.cs`): per-mode principal (tenant vs sole user).
- **`TaskQueueProcessor`** (`Tamma.Api/Services/TaskQueue/`): async re-eval + budget watch.
- **`AgentManage` policy** (`Program.cs`, from 32-2): write RBAC.
- **`packages/dashboard-user/`**: tenant-facing experiment UI (list/detail/results/conclude/rollback).

## Dependencies

- **Prerequisite (hard)**: 32-1 (Agent/AgentVersion entities — variants reference `agentId` + version), 32-2 (registry/resolution, `agent_role_selections`, `ResolvedAgentConfig` enrichment, `AgentManage` policy — what assignment pins onto and rollout mutates), 32-10 (benchmark projections — the read models significance is computed over).
- **Prerequisite (guardrails)**: 32-4 (SaaS provider auth gating — variant validation), 32-9 (usage & cost emission — the cost tally for `MaxSpendUsd` and the `cost` metric).
- **Related**: 32-6 (action trail — assignment tags ride its `Tags` builder), 32-12 (personas — the `personaId` variant axis), 32-13 (dashboards — the experiment UI lives alongside the benchmark dashboard surface).
- **Design of record**: `docs/superpowers/specs/2026-06-17-agent-entities-benchmarking-design.md` (Epic 32 design; §"Phase 2: A/B experiment framework").
- **Origin**: realizes the A/B-testing acceptance criterion Story 1-13 specified but never built.
- **Blocks**: nothing — this is the terminal Phase-2 story of Epic 32.

## Testing Strategy

**Unit tests** (`tests/Tamma.Api.Tests/Agents/` — in-memory or Postgres fixture per `Infrastructure/InMemoryDbFixture.cs` / `Epic28/` precedent):
1. **Cohort split distribution**: assign 100k stable keys across weights `{50,30,20}` → empirical distribution within tolerance of the weights; the *same* key always returns the *same* variant (determinism); changing `experimentId` reshuffles. `StableHash` is process-stable (run twice, identical buckets).
2. **Significance math** — two-proportion z: fixtures with precomputed expected `pValue`/`z`/effect for clearly-different, marginal, and equal rates; degenerate `SE=0` → `pValue=1`, not significant; `n` below floor handled by the caller, not the calculator.
3. **Significance math** — Welch's t: fixtures for different means, equal means, and zero-variance; Cohen's d effect size; `N<2` guarded.
4. **MinSampleSize guard**: results with one variant under floor → no winner declared even if the over-floor variant is "significant"; all variants at floor + significant challenger → winner declared.
5. **Rollout + rollback atomicity**: promote upserts `agent_role_selections` to the winner and snapshots the prior selection in one transaction; rollback restores it; a forced failure mid-transaction leaves the selection unchanged (no dangling state).
6. **MaxSpendUsd guardrail**: cumulative tagged cost reaching the cap auto-concludes (winner if significant else baseline) and emits `AGENT.EXPERIMENT.BUDGET_REACHED`.
7. **Provider gating**: a SaaS create with a variant naming a CLI/token provider → 400 `experiment_variant_provider_gated`; single-user allows it.
8. **Lifecycle events**: start/conclude/rollback/budget each emit exactly one corresponding tenant DCB event; no event on a no-op (concluding an already-concluded experiment).
9. **Single-running-per-role**: starting a second experiment for the same role while one runs → 409; partial-unique-index backstop verified against Postgres.

**Integration tests** (Postgres-bound, run via `sg docker -c "dotnet test ..."`):
10. Tenant migration applies + `dotnet ef migrations has-pending-model-changes --context TenantDbContext` reports none; tenant-model tests assert the new tables/indexes/CHECK.
11. **Endpoint RBAC matrix**: `AgentManage` required for create/start/conclude/rollback; SaaS `member` → 403 on writes, 200 on reads; single-user sole user → all 200.
12. **End-to-end A/B**: create 2-variant experiment, start, emit synthetic 32-6 trail/cost events for both arms tagged with `experimentId`/`variantId`, `GET /results` computes per-variant `n` + significance, conclude with `promote=true` repoints `agent_role_selections`, `rollback` restores the baseline — all within one tenant.
13. **Tenant isolation** (mirrors `Epic28/CrossTenantIsolationPostgresTests.cs`): tenant A's experiment/results invisible to tenant B (404); platform owner has no results read path; two tenants run same-named experiments on the same role without collision.

**Coverage**: critical paths (assignment determinism, significance, rollout/rollback transaction, guardrails) → 100%; service/repository line ≥ 80%.

## Estimated Effort

6-7 days

## Files Created / Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Data/Entities/AgentExperiment.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Entities/AgentExperimentVariant.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Entities/AgentExperimentMetric.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Entities/AgentExperimentStatus.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Repositories/IAgentExperimentRepository.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Repositories/AgentExperimentRepository.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Data/Migrations/Tenant/<ts>_AddAgentExperiments.cs` (+ `.Designer.cs`, snapshot) | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/IExperimentAssignmentService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/ExperimentAssignmentService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/ISignificanceCalculator.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/SignificanceCalculator.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/ExperimentResultsService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/ExperimentRolloutController.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentExperimentService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentExperimentEventTypes.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Dtos/Agents/AgentExperimentDtos.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/AgentExperimentEndpoints.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/SignificanceCalculatorTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/ExperimentAssignmentServiceTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/ExperimentRolloutControllerTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/AgentExperimentEndpointsTests.cs` | Create |
| `packages/dashboard-user/src/pages/experiments/ExperimentsPage.tsx` | Create |
| `packages/dashboard-user/src/pages/experiments/ExperimentDetailPage.tsx` | Create |
| `packages/dashboard-user/src/services/experiments-client.ts` | Create |
| `apps/tamma-elsa/src/Tamma.Data/TenantDbContext.cs` | Modify (add `DbSet<AgentExperiment>`, `DbSet<AgentExperimentVariant>`) |
| `apps/tamma-elsa/src/Tamma.Data/TammaModelConfiguration.cs` | Modify (`ConfigureTenantEntities`: tables, indexes, CHECK, cascade FK) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentTrailTags.cs` | Modify (add `experimentId`/`variantId` tags — coordinate with 32-6) |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (register services; map experiment routes with `AgentManage` RBAC; wire TaskQueue handler) |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:
1. Read [BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)
2. Searched `.dev/` for related spikes, bugs, findings, decisions
3. Read the Epic 32 design of record: `docs/superpowers/specs/2026-06-17-agent-entities-benchmarking-design.md` (§"Phase 2")
4. Read sibling stories 32-1 (entities), 32-2 (registry/selection + `AgentManage` + `ResolvedAgentConfig`), 32-6 (action-trail tags), and the 32-9/32-10 specs (cost + benchmark projections) — this story *consumes* their seams, it does not re-implement them
5. Reviewed the closest existing patterns: `AgentConfig`/`AgentEndpoints.UpdateConfig` (tenant JSONB + audit-event discipline), `TammaModelConfiguration.ConfigureTenantEntities` (tenant-schema entity config + partial indexes), `TaskQueueProcessor` (`ProcessOnceAsync` poll loop), `Epic28/CrossTenantIsolationPostgresTests.cs` (isolation proof)
6. Planned the TDD approach (Red-Green-Refactor) — start with `SignificanceCalculatorTests` (pure, fixture-driven) and `ExperimentAssignmentServiceTests` (deterministic distribution), which have zero DB dependency

### Key design decisions

- **Tenant-scoped, full stop.** The experiment, its variants, its assignments, and its results all live in the tenant schema. Per CLAUDE.md "Universal rule for any tenant-aware feature", both ownership models are answered explicitly: SaaS → the tenant owns it (`AgentManage` = owner/admin; member read-only); single-user → the sole user owns it (no gate). A platform owner has **no** read path into a tenant's experiment results — the same isolation backbone as the 32-6 action trail. This is also why the experiment lives in `TenantDbContext`, not the control plane where the agent *definitions* live.
- **Slice, don't re-measure.** Significance is computed over the 32-10 benchmark projections filtered by `experimentId`/`variantId`. The framework introduces no new measurement events — it adds two tags to the existing trail and reads the existing substrate. That is the whole point of building it after 32-10.
- **Determinism is load-bearing.** Cohort assignment hashes the *run correlation id*, never the clock, so a run is stable across retries/re-resolutions and lands in exactly one arm. Use a content-addressed hash (SHA-256), never `string.GetHashCode()` (process-randomized in .NET) — getting this wrong silently breaks both reproducibility and the split distribution.
- **Guardrails before conclusions.** No significance is even evaluated until every variant clears `MinSampleSize`; spend is capped by `MaxSpendUsd` (auto-conclude + `BUDGET_REACHED`); SaaS provider gating (32-4) rejects ineligible variants at create; per-tenant budgets (32-9) still clamp each run. The framework can never declare a winner on thin evidence or run away on cost.
- **Atomic rollout/rollback.** Promotion snapshots the prior selection *before* repointing `agent_role_selections`, all in one tenant transaction; rollback restores from the snapshot. A partial promote can never leave a dangling or half-applied selection — the rollout controller is the only writer to the selection during conclusion.
- **No behaviour change off the experiment path.** A role with no running experiment resolves byte-for-byte as it does today; assignment returns null and the existing 32-2 precedence applies. The experiment seam is purely additive in the resolve path.

### Migration discipline (Epic 28 conventions)

- `agent_experiments` / `agent_experiment_variants` are **additive tenant-schema** tables — `dotnet ef migrations add AddAgentExperiments --context TenantDbContext`, not a baseline CHECK edit.
- After adding, run `dotnet ef migrations has-pending-model-changes --context TenantDbContext` → must report none.
- Mirror entity config **only** in `TammaModelConfiguration.ConfigureTenantEntities` (the established single source); the snapshot/Designer are generated, not hand-edited.
- Run C# tests with `sg docker -c "dotnet test ..."` (session docker group is stale; build needs no wrapper).

### Edge cases

- **Two variants tie / no significant winner**: conclude promotes nothing; baseline selection untouched; `AGENT.EXPERIMENT.CONCLUDED` carries `winnerVariantId:null, promoted:false`.
- **Variant references an agent that was archived mid-experiment**: assignment still pins the variant's pinned `(agentId, version)` (immutable history, 32-1); resolution materializes the pinned version even though the agent is archived — the experiment measures the version it started with.
- **Concluding an already-concluded / rolled-back experiment**: idempotent no-op, no second event.
- **Rollback when no prior selection existed** (role had no explicit selection, was resolving the system default): restore = delete the experiment-set selection so the role falls back to the system-default public agent (per 32-2 precedence), not a dangling row.
- **Spend cap and significance reached on the same tick**: budget-reached takes precedence in messaging but the conclusion uses the significant winner — one `CONCLUDED` event, plus `BUDGET_REACHED`.
- **Weights that don't sum to the configured total**: rejected at create (400) — never silently normalized, so the operator's intent is explicit.

## Logging Requirements

- **INFO**: experiment created (`experimentId, role, metric, variantCount`), started, concluded (`winnerVariantId, pValue, promoted`), rolled back, budget reached (`spendUsd, maxSpendUsd`), winner promoted to selection (`role, agentId, version`).
- **DEBUG**: cohort assignment resolved (`experimentId, variantId, role` — never the raw assignment key if it could embed sensitive context), significance evaluated (`baselineN, challengerN, pValue, significant`), results sliced from projections (`variantCount, totalTrials`).
- **WARN**: significance evaluation skipped — variant below `MinSampleSize` (`variantId, n, floor`); single-running-per-role 409; member-role 403 on write; provider-gating rejection at create (`provider`); weights-sum mismatch at create.
- **ERROR**: rollout/rollback transaction failed/rolled back (`experimentId, role`), event append failure after a committed transition, TaskQueue re-eval handler failure (isolated per tick, does not kill the loop).
- **Structured context**: include `{ experimentId, role, metric, mode }` where applicable; `{ variantId, n, pValue }` on significance logs.
- **Credential safety**: never log raw provider keys (variants are credential-agnostic — provider *name* only, never a key); redact any blob-referenced content.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-17 | 1.0.0   | Initial story creation | Claude |
