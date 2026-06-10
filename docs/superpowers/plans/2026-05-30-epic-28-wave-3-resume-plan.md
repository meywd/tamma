# Epic 28 — Wave 3 Resume Plan

**Created:** 2026-05-30
**Branch:** `feat/wave-b` (in sync with origin)
**HEAD:** `0e9c4005`
**Suite state:** 2664 passed / 0 failed / 8 skipped (verified before push)

This plan exists so the next session can pick up Epic 28 cleanly after a Claude Code update + restart. It captures (a) what just shipped, (b) what's still open, (c) recommended Wave 3 dispatch, and (d) every user decision queued.

---

## What just shipped (Waves 1 + 2)

Commits on `feat/wave-b` since `695ac0e0` (Epic 27 completion HEAD):

```
0e9c4005 feat(epic-28-5,28-9): verify-email provisioning trigger + reuse-detected platform_events
e7cbc8bf fix(epic-28-8): mode-aware EnsurePersonalTenantMiddleware + status-code mapping
34e82fa8 docs(epic-28): Wave 2 residual verification + 28-1 follow-up disposition
842ed1a4 docs: ApexYard adoption evaluation proposal
0a40c4f1 docs: draft missing-config notifications epic (placeholder epic-XX)
6ae94763 feat(epic-28-9): refresh-token tenant binding + reuse-detection (AC3)
d2a902ab docs(epic-28): status audit + per-story verdict flips
```

**Story scoreboard:**
- DONE — 28-2, 28-3, 28-4, 28-6, 28-7, 28-8, 28-11 (7 stories)
- MOSTLY DONE — 28-1, 28-5, 28-9, 28-10, 28-12 (5 stories)
- PARTIAL — none
- NOT STARTED — none

**Authoritative status docs (point-in-time snapshots — do not edit):**
- `docs/superpowers/plans/2026-05-29-epic-28-status-audit.md` (initial broad audit)
- `docs/superpowers/plans/2026-05-30-epic-28-residual-verification.md` (deep verification — surfaces 16 real gaps)

---

## Why "finish 28" isn't actually finished

Story Status fields are leading indicators (every story closes its surface-level audit residuals). The verification report uncovered **16 REAL GAPS** that the original audit hid behind "needs human verification" lines. 3 of those are security-relevant and still open.

---

## Wave 3 recommended scope

### Priority 1 — Security gaps (3 stories, parallel-safe)

These touch non-overlapping file paths, so 3 parallel agents are safe.

#### Agent W3-A: 28-9 AC2 — atomic SwitchOrg

- **Gap:** `SwitchOrg` in `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs` runs 5 mutations with NO transaction wrap and NO `FOR UPDATE` lock. Concurrent calls can leave half-rotated session state.
- **Fix:** wrap the 5-step revoke-old + insert-new + issue-token + emit-event sequence in a single CP transaction. Add `SELECT FOR UPDATE` on the user's current refresh-token row to serialise concurrent switch-org calls.
- **Files:** `AuthEndpoints.cs` (SwitchOrg handler), `SwitchOrgEndpointTests.cs` (add concurrent-call test).
- **Effort:** ~50 LoC + ~3 new tests.
- **Story doc update:** 28-9 — change `AC2 5-step atomicity verification` from residual to closed.

#### Agent W3-B: 28-3 AC3 — release-build hard-fail on missing ControlPlane

- **Gap:** `StubTenantConnectionResolver` is unconditionally registered in `AddTammaData`. If `ConnectionStrings:ControlPlane` is missing in `ASPNETCORE_ENVIRONMENT=Production`, the API silently runs on the stub (Info-log only, no hard-fail).
- **Fix:** in `apps/tamma-elsa/src/Tamma.Data/DependencyInjection.cs` `AddTammaData`, when `IHostEnvironment.IsProduction()` and `ConnectionStrings:ControlPlane` is null/empty, throw at registration time with a clear error message naming the missing key.
- **Files:** `DependencyInjection.cs`, new test under `Tamma.Api.Tests/Epic28/`.
- **Effort:** ~20 LoC + 2 tests (prod-mode-without-CP throws, prod-mode-with-CP succeeds).
- **Story doc update:** 28-3 — close AC3 release-build-throws residual.

#### Agent W3-C: 28-12 AC1+AC2 — role-split enforcement

- **Gap:** `docker-compose.{yml,prod.yml}` does NOT slot the 3 distinct DB role URLs (`tamma_admin` / `tamma_provisioner` / `tamma_app`); `scripts/db/postgres-roles.sql` exists but is never enforced at runtime, AND no startup `SELECT current_user` assertion catches the regression.
- **Fix (2 parts):**
  1. Add 3 connection-string slots (`ConnectionStrings:TammaAppDb`, `ConnectionStrings:TammaProvisionerDb`, `ConnectionStrings:TammaAdminDb`) to `docker-compose.prod.yml` env (and `.yml` for dev parity).
  2. Add startup health-check in `Program.cs` that runs `SELECT current_user` against `TammaAppDb` and asserts the result IS NOT `tamma_provisioner` and IS NOT `tamma_admin`. Fail fast on mismatch.
- **Files:** `docker-compose.yml`, `docker-compose.prod.yml`, `Program.cs` (health-check section), new test.
- **Effort:** ~30 LoC + 2 tests + compose edits.
- **Story doc update:** 28-12 — close AC1+AC2 residuals.

### Priority 2 — Other real gaps (sequential after P1)

- **28-1 AC2/AC3** — write `scripts/db/bootstrap-shared-dbs.{sh,ps1}` + `scripts/db/reset-all.{sh,ps1}`. Wire into compose entrypoint or one-shot `db-bootstrap` service.
- **28-5 AC2 step-10** — add `QueueWelcomeEmailActivity` inside `CreateTenantWorkflow` (writes to `platform_email_outbox` per Epic 28 README conflict-resolution #2). Currently welcome emails enqueue from `AuthEndpoints` directly.
- **28-5 AC4** — verify pg_dump backup behind `Backup:DeletionBackup=true` + `pg_terminate_backend` + cooling-off window in `DropTenantDatabaseActivity` / `TenantCleanupRequestedTrigger`. May be already present — verification needed.
- **28-11 AC2** — add 24h `resourceSummary` analytics join to `AdminTenantsEndpoints` GET tenant detail handler. Read from `platform_analytics_hourly` table.

### Priority 3 — Spec divergences (batch story-doc update, no code)

These are deliberate implementation choices that diverge from spec. Update the AC bullets in story docs to match implementation reality, with a brief rationale per bullet:

- 28-1 `KekVersion` shape
- 28-4 metric names
- 28-4 envelope byte layout
- 28-5 `pg_terminate` via `FORCE` option
- 28-10 metric model (wide-row vs spec'd long-narrow) — **needs user decision first**
- 28-12 coordinator-instead-of-workflow (no `RekeyTenantConnectionStringsWorkflow.cs`)

### Priority 4 — User decisions needed

Block on these before touching the relevant gap:

1. **28-10 analytics metric model** — accept shipped wide-row fact-table (~30% spec coverage) or migrate to spec'd long-narrow `MetricKey/Tags` table? Biggest architectural divergence in the audit. Affects whether 28-10 closes as DONE or needs a significant follow-up story.
2. **28-10 1k/5k/10k idle-orchestrator benchmark** — intentionally deferred to Story 30 production-scale gate, or required for 28-10 closure? Affects whether 28-10 ships DONE today or stays MOSTLY DONE.

### Out of scope for Wave 3

- **Convention store I-2/X-1 hazards** — Epic 27 → 28 cutover bundle markers in `IConventionStore.cs`, `IConventionRepository.cs`, `Convention.cs`. Pure documentation today; runtime fanout-or-fail-loud guards needed once convention admin/repo paths actually run on per-tenant DBs in production. Separate work, lower priority than security gaps.
- **OpenBao KEK backend (28-13)** — deferred per epic README until a documented trigger fires. See `project_epic28_kek_decision.md` memory.

---

## Dispatch suggestion for next session

Open the session, read this doc, then:

```
Dispatch 3 parallel agents (W3-A, W3-B, W3-C) targeting the security gaps.
Non-overlapping file paths — safe parallel.

While they run:
- Ask user about 28-10 metric model + benchmark decisions (Priority 4).
- Sketch Priority 2 follow-ups if user wants to keep going after Wave 3.

After agents return:
- Verify diffs match claims.
- Run full suite: `sg docker -c "dotnet test apps/tamma-elsa/Tamma.sln"`.
- Commit per-agent (3 commits) + push.
- Update project_epic28_execution.md memory with post-Wave-3 state.
```

---

## Open decisions across all session work (not just Epic 28)

From the parallel-dispatch turn that produced the notifications + ApexYard docs:

**Notifications epic** (`docs/stories/epic-XX-missing-config-notifications/README.md`):
1. Email digest cadence default — daily or weekly?
2. Nightly-scan execution — central CP or per-tenant DB? (Depends on Epic 28 routing seam state.)
3. Extend Alerts module (`Story 5-6` infrastructure already does ~60% of the job) or build new `missing_config_registry` table per the doc's recommendation?
4. Confirm epic number = 32? (27-31 + 33 are taken.)

**ApexYard adoption** (`docs/stories/apexyard-adoption-evaluation.md`):
5. Migration AgDR enforcement — hard block (recommended) or soft warning?
6. AI-reviewer required check — adopt now (builds the muscle) or defer to SaaS launch (lower value in solo mode)?

**Epic 28 specific:**
7. Story 28-10 metric model — wide-row vs long-narrow (see Priority 3 / Priority 4 above).
8. Story 28-10 orchestrator benchmark — defer or required.

---

## How to use this doc

- Read top-to-bottom to recover context.
- Start with the Wave 3 Priority 1 agents — they're concretely scoped and parallel-safe.
- Defer Priority 2 unless user pushes for full closure.
- Block Priority 3 on user decisions for Priority 4.

Memory page `~/.claude/projects/-home-meywd-tamma/memory/project_epic28_execution.md` carries the same state in shorter form for future sessions.
