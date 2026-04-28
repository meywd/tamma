# Epic 28 Multi-Agent Review — 2026-04-26

**Subject**: commit `5ff35d7` on `feat/wave-a` — "feat(epic-28): finish stories 28-4 → 28-12 (LRU pool cutover, cleanup workflow, platform task worker, status cache, switch-org logout-all, hourly rollup scheduler, SSE event stream, postgres-roles + KEK runbook)"
**Reviewers dispatched**: 6 agents in parallel
**Reviewers trusted**: 4 (architect-review, security-auditor, csharp-pro, Explore cross-epic)
**Reviewers discarded**: 2
- `bmm-document-reviewer` — hallucinated file paths (`Tamma.Domain/...`, `Tamma.Application/...`) that don't exist; described 28-5 as "RLS Phase-3" and 28-12 as "Status Cache" — wrong story titles. Output unreliable.
- `bmm-tech-debt-auditor` — ran in an isolated worktree without the new commit and correctly refused to fabricate findings.

---

## Aggregated findings — ranked by severity

### HIGH (6) — should-fix-before-next-merge

| # | Source | Finding | File:line |
|---|---|---|---|
| H1 | security | Plaintext passwords leak via `pg_stat_activity`, PG log files (`log_statement=ddl`), and `ps auxe` during bootstrap. The `psql --command="SELECT set_config('tamma.admin_password', '$TAMMA_ADMIN_PASSWORD', false)"` pattern is visible in `pg_stat_activity` while running, and the `EXECUTE format('CREATE ROLE ... PASSWORD %L', current_setting(...))` inside the `DO` block ends up in `log_statement=ddl` logs with the password embedded. | `scripts/db/postgres-roles.sql`, `scripts/db/docker-entrypoint-bootstrap.sh:38-44` |
| H2 | security + csharp | `Logout?all=true` has no step-up auth and emits no audit event. A stolen JWT can lock out the legitimate user with no audit trail. | `AuthEndpoints.cs:493-545` |
| H3 | cross-epic | KEK rotation runbook references `POST /api/admin/secrets/rekey` but the endpoint is **not wired** to `KekRotationCoordinator`. Runbook step 3 cannot execute. | `.dev/runbooks/kek-rotation.md:97`, no handler in `Program.cs` |
| H4 | csharp | `TenantStatusEndpoint.BuildStepLadder(IEnumerable<dynamic>)` — DLR overhead on every poll + nullable-analysis blackout. Trivial fix: define a private record `StepEventRow(string Type, string? Tags, DateTime CreatedAt)` and project to it. | `TenantStatusEndpoint.cs:145-173` |
| H5 | csharp | `HandleFinalLeaseReleased` fires `Task.Run(...)` unobserved — outstanding deferred-disposes not awaited at shutdown. Risk: `NpgsqlDataSource.DisposeAsync` running after host shutdown with no cancellation link. | `LruPooledTenantConnectionResolver.cs:680-693` |
| H6 | csharp | `LeaseAsync_Then_Evict_Defers_DataSource_Dispose...` test is timing-dependent — depends on `Task.Run` from the dispose callback NOT having executed yet. Flaky on busy CI boxes. | `LruResolverLeaseAndDiagnosticsTests.cs:104-137` |

### MEDIUM (12) — fix in next iteration

| # | Source | Finding |
|---|---|---|
| M1 | security | Exception `ex.Message` lands in `tenants.ProvisioningDetail` + `platform_events.data` payloads. Sensitive paths/SQL fragments could leak into the long-lived event store. |
| M2 | security | Missing actor identity in 8 admin-action audit events (RetryTenant, DeleteTenant, ForceDeleteTenant, CleanupTenant, UpdateTenantPlan, Logout?all=true, SwitchOrg, Pool Evict). `BuildAdminEvent` sets `source: "admin"` but never captures the requesting `userId`. Defeats SOC2 attribution. |
| M3 | arch | `CleanUpFailedTenantActivity` is a hand-rolled mini-orchestrator inside one Elsa activity (200-line `ExecuteAsync` with custom `RunStep` helper). Bypasses Elsa's per-step replay/observability/cancellation. Should refactor to `Sequence` of `TryCatch`-wrapped activities. |
| M4 | arch | SSE endpoint pins `ControlPlaneDbContext` for 30 min (DI scope = request scope). Conn-pool exhaustion risk: 50 admins watching = 50 long-lived CP connections (Npgsql default `MaxPoolSize=100`). |
| M5 | arch | Per-pod 10s status cache too loose for security-relevant flips. Tenant suspended on pod A continues to be treated active on pod B for up to 10s. **Resolved by Postgres LISTEN/NOTIFY follow-up.** |
| M6 | arch | Lease count > MaxEntries → effective LRU lockout. With 1000 SSE streams holding leases for 30 min and `MaxEntries=500`, the LRU is pinned. No guard. |
| M7 | arch | Master handle's `OnDisposed` adapter (`stateCallback = state => onDisposed(this)`) is dead code — the state parameter is unused. Fixing it eliminates the `UnsafeRawDataSource` smell. |
| M8 | arch | SSE endpoint doesn't use the `LeaseAsync` infrastructure that was designed for it. The lease mechanism is well-engineered but currently has no production consumer. |
| M9 | arch | SSE back-pressure cap silently drops events under load. Client doesn't see `: lag N events behind` keepalive. |
| M10 | arch | `PlatformTaskWorker` single-task-per-tick caps throughput at 12/min/pod. Justified by "ordering" comment but `FOR UPDATE SKIP LOCKED` already breaks ordering across pods — single-task-per-pod buys nothing. |
| M11 | arch | Multi-pod `HourlyAnalyticsRollupScheduler` triple-dispatches at minute 5 (3 pods × 1 dispatch). UPSERT idempotent but wasteful + noisy in audit. Needs Postgres advisory lock for leader election. |
| M12 | csharp | `LruPooledTenantConnectionResolver.DisposeAsync` calls `_metrics.Dispose()` but `_metrics` is registered as a singleton in DI — co-ownership bug, double-dispose at shutdown. |

### LOW (~15) — defer to backlog

- ConfigureAwait(false) inconsistency between Pooling layer (careful) and Endpoints (inconsistent)
- Primary constructors used inconsistently across new files
- `MemoryTenantStatusCache` "eviction" via `Keys.Take(N)` is not LRU despite name
- `LruPooledTenantConnectionResolver.LeaseAsync` 3-retry cap could legitimately fail on high-churn tenants — add metric + bump to 5
- `_lastReaperRun` `DateTimeOffset` torn-read on 32-bit (worker is single-threaded today)
- `MemoryTenantStatusCacheTests.Concurrent_SetAndGet_AreThreadSafe` uses 50 ops — too few to provoke real races
- `PlatformTaskWorkerTests.NewWorker` rebuilds a second `ServiceCollection` (brittle if InMemory provider changes)
- CP-CS gating duplicated in `Program.cs:245` and extension `TenantConnectionPoolServiceCollectionExtensions.cs:64`
- Idempotency claim on `AddTenantConnectionPool` is weaker than stated (`AddSingleton<LruPooledTenantConnectionResolver>` not under `RemoveAll`)
- `TenantConnectionHandle.HandleState.OnDisposed` — `required` + nullable is unusual (pick one)
- `AdminTenantEventsSseEndpoint` uses raw `DateTimeOffset.UtcNow` instead of injected `TimeProvider` — testability gap
- `TenantStatusEndpoint` uses raw `DateTime.UtcNow` — same testability gap
- `Last-Event-ID` header ignored on SSE reconnect — resumption gap
- `ListWarmTenants` cap clamp `_options.MaxEntries * 11 / 10` overflows `int.MaxValue` if MaxEntries > ~195M (not a real risk)
- Pattern matching opportunity in `LruPooledTenantConnectionResolver:540-541`

---

## Cross-epic integration assessment

| Epic | Status | Notes |
|---|---|---|
| 9 (provider chain) | ✅ Green | Tenant isolation maintained through ProviderChainResolver |
| 12 (context tools) | ✅ Green | `LeaseAsync` not adopted but no current activity needs it |
| 19 (RLS Phase-3) | ✅ Green | Architecture correctly evolved to per-tenant DB isolation; RLS now vestigial |
| 27 (prompt store) | ✅ Green | Status cache orthogonal to prompt data |
| 29 (secret store) + 28-12 | ⚠️ **HIGH** | Missing `/api/admin/secrets/rekey` endpoint blocks rotation runbook |
| 30 (provisioning) | ✅ Green | Cleanup DROP path orthogonal to Cranl API path |
| 31 (multi Git platform) | ✅ Green | Pre-existing 28-7 work unaffected |

**Wave A merge-readiness**: 7 of 8 deferred Part B items closed by this commit. Story 28-1 (EF migration scripts) remains the open critical-path blocker (not in this commit; expected separate).

---

## Suggested fix batches

| Batch | Scope | Items | Effort | Risk |
|---|---|---|---|---|
| **A — Quick wins** | Mechanical fixes | H4 (dynamic), H6 (flaky test), M7 (handle adapter), M12 (singleton dispose), M11 (multi-pod scheduler advisory lock) | ~2-3h | Low |
| **B — Security gaps** | Audit + secrets | H1 (postgres-roles passwords), H2 (logout audit + step-up), M1 (event exception text scrub), M2 (actor identity in audit events) | ~3-4h | Medium (security-sensitive) |
| **C — Wiring gaps** | Integration cliffs | H3 (rekey endpoint), H5 (deferred-dispose tracking) | ~3h | Low |
| **D — Original follow-ups** | Cluster invalidation + impersonations | LISTEN/NOTIFY listener (fixes M5), `admin_impersonations` table + service + middleware integration + endpoints | ~6-8h | Medium (EF migration + middleware) |
| **E — Bigger refactors** | Architecture | M3 (Elsa refactor), M4/M8/M9 (SSE redesign), M6 (lease cap), M10 (worker parallelism) | ~2-3 days | High (judgment-heavy, design choices) |

**Total fix scope**: ~14-18h across batches A-D, parallel-dispatchable. Batch E should be discussed before any auto-fix — these are design choices with multiple valid answers.

### Fit assessment for the two original follow-ups

Both still fit, and one resolves a Medium finding from this review:

- **Cluster-wide invalidation (Postgres LISTEN/NOTIFY)** = directly fixes **M5** (per-pod cache too loose for security flips)
- **`admin_impersonations` table** = standalone audit feature; doesn't fix anything on this list directly but addresses a SOC2 gap that complements **M2** (actor identity in audit events)

---

## Insight

The most important pattern in this aggregate: **H3 + M5 + the original deferred "cluster-wide invalidation" all point at the same gap** — Epic 28 has a half-wired control plane where one pod or runbook step assumes a feature exists that the next pod or step doesn't actually expose. KEK rotation has a runbook → no endpoint. Status cache invalidates locally → siblings stay stale. SSE endpoint exists → doesn't use the lease infra built for it. These are **integration cliffs**, not code bugs. Fixing them as a coordinated batch (Batches B+C+D) is more honest than treating them as separate stories.

---

## Reviewer notes (for future runs)

- The `bmm-document-reviewer` agent appears to default to inventing content when it can't access the actual code. **Do not trust its output without spot-checking file existence.** Possibly safer: hand it a list of confirmed files first.
- `bmm-tech-debt-auditor` ran in an isolated environment (likely a worktree) and didn't have the commit. Consider passing the commit ref + explicit note that the agent runs in the user's working tree, not a sandbox.
- The 4 trusted agents (architect-review, security-auditor, csharp-pro, Explore) produced consistent, file:line-referenced findings. These are the reliable choices for this kind of review.
- `Explore` for cross-epic conflicts is the highest-value-per-token agent — it surfaced the missing-endpoint issue (H3) that none of the focused-scope agents would have found.
