# Epic 28 Multi-Agent Review — Round 2 (2026-04-26)

**Subject**: same commit as round 1 — `5ff35d7` on `feat/wave-a` (no new code since the round-1 review at `docs/review/epic-28-multi-agent-review-2026-04-26.md`).

**Reviewers dispatched**: 4 in parallel (architect-review, security-auditor, csharp-pro, Explore for cross-epic + story adherence). Each was blinded to the round-1 review file. All four returned with concrete file:line citations.

**Verdict change vs round 1**: round 1 surfaced 6 HIGH / 12 MEDIUM / ~15 LOW. Round 2 surfaces **1 CRITICAL** + ~14 HIGH + ~17 MEDIUM + ~14 LOW, with the critical and several highs **missed by round 1**. The story-adherence agent (Explore) reported "PASSED" on every story — that's a different lens (did the code ship for each AC?) and is consistent with round 1's pass on cross-epic integration.

---

## NEW finding missed by round 1

### C1 — CRITICAL — Privilege escalation: every signed-up user can call platform-admin endpoints

**Where**: `apps/tamma-elsa/src/Tamma.Api/Auth/Permissions.cs:19` + `apps/tamma-elsa/src/Tamma.Api/Auth/PermissionHandler.cs:17` + `apps/tamma-elsa/src/Tamma.Api/Endpoints/AuthEndpoints.cs:206,960` + every `RequireAuthorization("OwnerAccess")` site under `/api/admin/*` (50+ endpoints across `Program.cs:932-1068`).

**Mechanism (verified)**:
1. `Permissions.Matrix["users:manage"] = ["owner"]` — `users:manage` requires role `owner`.
2. `OwnerAccess` policy → `PermissionRequirement("users:manage")` (Program.cs:713).
3. `PermissionHandler` only checks `ClaimTypes.Role`, never `platformRole` (PermissionHandler.cs:17).
4. Registration adds every new user as `owner` of their personal tenant (`AuthEndpoints.cs:206` plain registration; `:960` GitHub OAuth callback).
5. `JwtService.GenerateAccessToken` puts the user's per-tenant role on the JWT as `role` (line 87).
6. `RetryTenant` / `DeleteTenant` / `ForceDeleteTenant` / `CleanupTenant` / `UpdateTenantPlan` / `Impersonate` operate on the URL path's `tenantId` with `IgnoreQueryFilters()` and no membership check (`AdminTenantsEndpoints.cs:264-538`).

**Exploitation**: a fresh signup (or GitHub-login) gets a JWT with `role: owner`. They can:
- `POST /api/admin/tenants/{any-victim-uuid}/actions/force-delete` with `X-Admin-Confirm: <same-uuid-from-url>` → destructive force-delete of any tenant
- `POST /api/admin/tenants/{any-victim-uuid}/cleanup` → kicks the cleanup workflow
- `GET /api/admin/tenants/{any-victim-uuid}/events/stream` → live cross-tenant SSE tail of platform_events.Tags + Data JSONB
- `POST /api/admin/pools/{any-victim-uuid}/evict` → DoS another tenant's connection pool
- `POST /api/admin/kek/rotate/start` → trigger or DoS KEK rotation
- `PUT /api/admin/tenants/{any-victim-uuid}/plan` → change anyone's plan/billing tier

The `platformRole` claim ("platform_admin" if role==owner else "user") **exists** but no policy consumes it. `JwtService.cs:64-67` even comments on this as a "convention" but builds the entire admin surface on it.

**Why round 1 missed it**: round 1 focused on the new code in commit `5ff35d7`. The OwnerAccess policy + PermissionHandler are pre-existing, but Epic 28 is what introduced the destructive admin endpoints sitting on top of them. The round-1 security agent looked at audit-trail completeness and credential hygiene, not the authz model itself.

**Severity**: CRITICAL. Shipping blocker.

---

## Aggregated findings — round 2

### CRITICAL (1)

| # | Source | Finding | File:line |
|---|---|---|---|
| **C1** | security | OwnerAccess gates 50+ admin endpoints on `users:manage` permission, which any registered user holds because every user is auto-`owner` of their personal tenant. PermissionHandler ignores the `platformRole` claim. | Permissions.cs:19, PermissionHandler.cs:17, AuthEndpoints.cs:206,960, Program.cs:932-1068 |

### HIGH (14)

| # | Source | Finding | File:line | Round 1? |
|---|---|---|---|---|
| H1 | security | Postgres role bootstrap leaks plaintext passwords via `psql --command`, `EXECUTE format(... PASSWORD %L ...)` → visible in `pg_stat_activity`, `log_statement=ddl` server log, and `/proc/<pid>/cmdline`. | scripts/db/postgres-roles.sql:42-46,54-57,71-74; scripts/db/docker-entrypoint-bootstrap.sh:38-44 | ✅ same |
| H2 | security + arch | `/auth/logout?all=true` and `/auth/switch-org` emit zero audit events; no step-up. Stolen JWT can lock out victim silently. SwitchOrg revokes every refresh token for the user when no token is in the body — silent multi-device logout. | AuthEndpoints.cs:493-549,643-727,688-704 | ✅ extended |
| H3 | security + arch + Explore | KEK rotation runbook references `/api/admin/secrets/rekey/{start,status,retry}`, code wires `/api/admin/kek/rotate/{start,status}` only — no `/retry` endpoint at all. Step 4 of the failure-recovery runbook can't execute. | .dev/runbooks/kek-rotation.md:97-101,158,182 vs Program.cs:987-990 | ✅ same |
| H4 | csharp + arch | `BuildStepLadder(IEnumerable<dynamic>)` disables nullable analysis on every member access; brittle to projection-shape changes. | TenantStatusEndpoint.cs:146-173 | ✅ same |
| H5 | arch + csharp | `HandleFinalLeaseReleased` fires `_ = Task.Run(...)` to dispose `NpgsqlDataSource` — unobserved at shutdown; deferred-dispose backlog unmetered. | LruPooledTenantConnectionResolver.cs:679-694 | ✅ same |
| H6 | arch + Explore | `CleanUpFailedTenantActivity` is a 200-line hand-rolled mini-orchestrator inside one Elsa activity (`RunStep` local function with manual try/catch). Bypasses Elsa's per-step replay/cancel/observability. | CleanUpFailedTenantActivity.cs:66-260; CleanUpFailedTenantWorkflow.cs:72-79 | ✅ same |
| **H7** | arch | **Status cache is dead code.** `ITenantStatusCache.TryGet` and `Set` are never called anywhere — only `Invalidate`. The "10s-TTL cache" advertised in Story 28-8 has no readers; `TenantContextMiddleware` still hits CP on every request. | MemoryTenantStatusCache.cs:43-81; AdminTenantsEndpoints.cs:267,318,368 | ❌ **NEW** |
| **H8** | arch | **`PlatformTaskWorker` has zero handlers registered + `RunOnStartup=true`.** Every queued task dead-letters with "no handler" on first prod deploy. | PlatformTaskWorkerOptions.cs:45; no `AddPlatformTaskHandler<>` calls in Program.cs | ❌ **NEW** |
| **H9** | arch + csharp | **`HourlyAnalyticsRollupScheduler` registered as plain `AddHostedService` with no leader election.** N pods → N redundant Elsa workflow dispatches per hour. UPSERT idempotency hides the cost. | HourlyAnalyticsRollupScheduler.cs:70,138-157 | ⚠️ was M11 |
| H10 | csharp | **`ControlPlaneDbContext` is double-registered**: scoped via `AddDbContext` AND as a pooled factory via `AddPooledDbContextFactory`. Two parallel options pipelines for the same context type. | DependencyInjection.cs:49 + TenantConnectionPoolServiceCollectionExtensions.cs:90-94 | ❌ **NEW** |
| H11 | security | **`TenantSecretProtector` silently falls back to deriving the AES-GCM key from `Cranl:ApiKey` via HKDF when `Cranl:EncryptionKey` is unset, only logs `LogWarning`.** Production deploy can ship with a key derived from the API key. Runbook env-var names (`TAMMA_TENANT_KEK`) don't match what the code reads (`Cranl:EncryptionKey`). | TenantSecretProtector.cs:64-115 | ❌ **NEW** |
| H12 | security | **LRU resolver hot path doesn't re-check `tenants.Status`** — a tenant flipped `suspended`/`failed`/`deleting` keeps serving traffic until LRU evicts. Admin endpoints invalidate the (dead) status cache but never call `connectionResolver.EvictAsync`. | LruPooledTenantConnectionResolver.cs:171-198,540-542; AdminTenantsEndpoints.cs:263-416 | ❌ **NEW** |
| H13 | security | **`AesGcmConnectionStringDecryptor` ignores its `kekVersion` argument** — the decryptor tries primary, then secondary, on any GCM failure. After two rotations (v3→v4→v5), rows still on v3 are unrecoverable, even though the operator believes both slots are valid. The runbook claim that dual-slot handles N+N-1 is correct; the runbook also claims it handles multi-rotation history (it doesn't). | AesGcmConnectionStringDecryptor.cs:66-137 | ❌ **NEW** |
| H14 | security | **`KekRotationCoordinator.StartAsync` has no Postgres advisory lock or DB-level singleton** — two pods racing the start endpoint can stage different KEKs, lose the first one, and corrupt the rotation lifecycle. Recovery requires editing `kek_rotations` rows by hand. | KekRotationCoordinator.cs:135-179; KekProvider.cs:152-167 | ❌ **NEW** |

### MEDIUM (17)

| # | Source | Finding | Round 1? |
|---|---|---|---|
| M1 | security + arch | Exception `ex.Message` lands in `tenants.ProvisioningDetail` + `platform_events.data` — sensitive paths/SQL fragments leak into the long-lived event store. SSE endpoint passes those JSONB blobs through to clients without scrubbing (M-cross-tenant-leak via H7). | ✅ same, amplified |
| M2 | security + arch | Missing actor identity in 8+ admin-action audit events. `BuildAdminEvent` only sets `source: "admin"` — never captures the requesting `userId`/`email`/`jti`. | ✅ same |
| **M3** | security | **`POST /api/admin/tenants/{id}/cleanup` emits a `TENANT.CLEANUP.REQUESTED` event but no Elsa trigger consumes it.** Endpoint returns 200 "Cleanup queued" while no work is scheduled. | ❌ **NEW** |
| **M4** | security + arch | **SSE endpoint passes `platform_events.Tags` + `Data` (raw JSONB) straight through to the client.** Combined with C1, any user can subscribe to any tenant's SSE stream and exfiltrate JSONB payloads in real time. Even fixing C1 leaves un-scrubbed event payloads on the wire. | ❌ **NEW** |
| M5 | arch | SSE endpoint pins one scoped `ControlPlaneDbContext` for up to 30 min per concurrent client; no max-clients cap. Pool exhaustion at fan-out. | ✅ same (was M4) |
| M6 | arch | `MemoryTenantStatusCache` "LRU eviction" is `_entries.Keys.Take(N)` — arbitrary order, not LRU. Docstring claims LRU. | ✅ now upgraded |
| M7 | arch | LRU resolver `LeaseAsync` 3-attempt cap throws `InvalidOperationException` on exhaustion; no backoff, no per-tenant lease cap. | ✅ same |
| **M8** | arch | **`PlatformQueuedTaskRepository.ReserveNextAsync` accepts `workerId` but never persists it on the row.** Reaper has nothing to identify the original claimant; worker docstring is false. | ❌ **NEW** |
| **M9** | arch | **`ReapStaleProcessingAsync` is `ToList()` + iterate + `SaveChanges`** — no `FOR UPDATE SKIP LOCKED`; two reapers across pods double-decrement `RetryCount` and dead-letter rows that should retry. | ❌ **NEW** |
| **M10** | arch | **`IPlatformTaskHandler` registered as Singleton, but per-tick handlers typically need scoped EF DbContext** — lifetime mismatch will surprise the first handler PR. | ❌ **NEW** |
| M11 | arch + csharp | `PoolWarmupService` uses `_ = Task.Run(...)` with the start token captured into a long-running scope; if host shuts down mid-warmup, scope `DisposeAsync` runs on a thread that may already be torn down. | ✅ same (was Batch A item) |
| M12 | csharp | `LruPooledTenantConnectionResolver.DisposeAsync` calls `_metrics.Dispose()` but DI also owns `_metrics` (singleton). Today idempotent (Meter.Dispose), but reaches across DI ownership. | ✅ same |
| M13 | arch | Build-locks `ConcurrentDictionary` is never trimmed — slow leak per distinct tenant id over process lifetime. | ❌ NEW (low risk) |
| M14 | csharp | `JsonSerializer.Serialize(evt)` in SSE uses default options (PascalCase) while rest of API uses camelCase — wire-protocol inconsistency. | ❌ NEW |
| M15 | csharp | SSE catches `Exception ex` per tick, writes `: error <type>` and continues forever — no consecutive-failure breaker. CP DB down for an hour → infinite quiet retry loop. | ❌ NEW |
| M16 | csharp | 12 raw `DateTime.UtcNow` reads in `AdminTenantsEndpoints` and `CleanUpFailedTenantActivity` despite `TimeProvider` registered globally. | ✅ same (was Low) |
| M17 | security | `X-Admin-Note` header (up to 500 chars) stored verbatim into `platform_events.data["note"]` and `tenants.ProvisioningDetail` — stored XSS into platform-admin dashboard if renderer doesn't escape. | ❌ NEW |

### LOW (~14, abbreviated)

ConfigureAwait(false) inconsistency · primary-constructors mixed · `MemoryTenantStatusCache` constructor takes optional TimeProvider (inconsistent with rest of codebase) · `PoolsAdminEndpoints` uses `IServiceProvider` service-locator instead of `[FromServices]` · SSE error frames don't differentiate transient vs permanent · `Status` shadow-column null semantics inconsistent (`null` treated as `"active"` in `IsDeletable` but as non-empty string in list filter) · `LruPooledTenantConnectionResolver.DisposeAsync` race on shutdown (no graceful drain) · `AddPlatformTaskWorker` idempotency claim weaker than stated · `_buildLocks` semaphores live for process lifetime · `_lastFired` torn-read on 32-bit (single-threaded today) · plaintext connection strings linger in managed heap until LRU evicts + GC reclaims · ApplicationName carries tenant id (cardinality leak in postgres logs) · BuildAdminEvent doesn't take ClaimsPrincipal · Pattern matching opportunities in resolver line 540.

---

## Cross-epic / story adherence (Explore agent)

The Explore agent's "story shipped per AC" pass came back ✅ on all 12 stories. That is **NOT inconsistent** with the architect/security/lint findings: a story can ship its named deliverables while still having authz, audit, and integration cliffs. Two story-level callouts the Explore agent surfaced:

- **CLAUDE.md is stale.** The "Multi-tenant provisioning (Cranl)" section says routing is "STUBBED — per-request DB connection switching by tenant is not yet wired." Commit `5ff35d7` flipped this on conditionally (`Program.cs:245-268` — wired when `ConnectionStrings:ControlPlane` is present). The doc should now read "wired in production via `ConnectionStrings:ControlPlane`; falls back to `StubTenantConnectionResolver` in dev/test."
- **`CreateTenantWorkflow` + `DeleteTenantWorkflow` Elsa correlation triggers** are still unwired (the activity + endpoint side ships; the workflow trigger does not). This is the same shape as M3 above (cleanup endpoint emits but nobody consumes).

Cross-epic conflicts: none material. Epic 19 RLS is now vestigial — not load-bearing. Epic 9 / 12 / 27 / 30 / 31 unaffected.

---

## Diff vs round 1

### Findings present in **both** rounds (consistent)
H1 postgres-roles passwords · H2 logout-all audit · H3 KEK rotation runbook ↔ endpoint mismatch · H4 dynamic in BuildStepLadder · H5 deferred-dispose Task.Run · H6 cleanup is hand-rolled mini-orchestrator · M1 exception messages in events · M2 missing actor identity · M5 SSE pins CP DbContext · M7 lease retry cap · M11 PoolWarmupService Task.Run · M12 metrics double-dispose · plus most LOWs.

### Findings new in round 2 (missed by round 1)
**C1 OwnerAccess privilege escalation** (CRITICAL) · H7 status cache dead code · H8 zero handlers + dead-letter on startup · H10 CP DbContext double-registration · H11 silent KEK fallback in production · H12 hot-path skips Status flip · H13 decryptor ignores kekVersion · H14 KEK rotation no advisory lock · M3 cleanup endpoint emits but no Elsa consumer · M4 SSE leaks raw JSONB cross-tenant · M8 workerId not persisted · M9 reaper not atomic · M10 handler singleton vs scoped mismatch · M14 JSON casing inconsistency · M15 SSE infinite quiet retry · M17 X-Admin-Note stored XSS · CLAUDE.md staleness about routing.

### Round 1 H6 (flaky test) — not re-flagged
Round-1 said `LeaseAsync_Then_Evict_Defers_DataSource_Dispose...` test was timing-dependent. Round-2 csharp agent flagged a related issue (`AsTask().Wait()` blocking pattern in handle tests) but didn't independently confirm the flakiness. May have been fixed in `321f436` or earlier — worth a separate verification.

### Round-1 architectural-cliff thesis still holds, with new evidence
Round 1's thesis: "Epic 28 has integration cliffs where one component assumes a feature exists that another component doesn't actually expose." Round 2 hardens this:

- KEK runbook ↔ endpoints (H3, both rounds)
- Status cache: round 1 said per-pod TTL too loose. **Round 2: there's no read path at all** (H7). The cliff is bigger than thought.
- SSE infrastructure built against the lease subsystem but not consumed there (M5, both rounds)
- **NEW: `POST /api/admin/tenants/{id}/cleanup` returns 200 with no Elsa consumer** (M3) — same shape as the KEK runbook gap
- **NEW: PlatformTaskWorker fully wired with no handlers** (H8) — the worker awaits a contract no caller fulfills

---

## Re-validation: do the two original follow-ups still fit?

Round 1 named two deferred follow-ups: **(A) cluster-wide invalidation via Postgres LISTEN/NOTIFY** (was tagged to fix M5 in round 1) and **(B) `admin_impersonations` table + middleware + endpoints** (was tagged to address part of M2 actor identity).

### (A) Cluster-wide invalidation via Postgres LISTEN/NOTIFY

**Round 1 framing**: per-pod 10s status cache is too loose for security-relevant flips (suspended/deleted). LISTEN/NOTIFY would converge invalidation across pods.

**Round 2 framing**: H7 says **the status cache has no readers** — `TryGet` is never called, only `Invalidate`. Therefore the per-pod 10s window doesn't actually exist as a security gap **today**, because nothing reads from the cache; every authz check still hits the CP DB. M5 from round 1 is currently moot.

**Verdict**: still fits, but **wrong order**. Doing LISTEN/NOTIFY before fixing H7 ships invalidation infrastructure for a feature that doesn't exist.

**New ordering**: 
1. Fix H7 — wire `TenantContextMiddleware` (or `ApiKeyAuthHandler`) to consume `ITenantStatusCache.TryGet` on the per-request path.
2. Then fix H12 — call `connectionResolver.EvictAsync` on Status flips so live sessions terminate.
3. Then add LISTEN/NOTIFY for cross-pod cache invalidation (originally A).

The follow-up's **scope is unchanged**; what changed is that two prerequisites surfaced.

### (B) `admin_impersonations` table + middleware + endpoints

**Round 1 framing**: closes a SOC2 gap by capturing impersonation events as a first-class audit row.

**Round 2 framing**: M2 is now wider than round 1 thought — actor identity is missing on Logout-all, SwitchOrg, Pool-Evict, RetryTenant, DeleteTenant, ForceDeleteTenant, CleanupTenant, UpdateTenantPlan, KEK rotation. The `BuildAdminEvent` helper takes no `ClaimsPrincipal`. Closing JUST the impersonation gap leaves 8+ other admin actions unidentified.

Plus **C1 (privilege escalation) makes impersonation theatre** — anyone can already act as anyone via the broken `OwnerAccess` policy. There's no authoritative "you're now impersonating tenant X" boundary because every user already has admin against every tenant.

**Verdict**: still fits, but **wrong order again**. 

**New ordering**:
1. Fix C1 — make `OwnerAccess` consume `platformRole` and add a real platform-owner authorization model (or repurpose to per-tenant-owner-only and add a new `PlatformOwnerAccess` policy for the `/api/admin/*` surface).
2. Fix M2 broadly — thread `ClaimsPrincipal` through `BuildAdminEvent` and the KEK rotation coordinator so every admin event captures actor identity.
3. Then ship `admin_impersonations` (originally B) as a first-class table for the specific impersonation path, with its own ENTER/EXIT events that link the impersonator → impersonated session.

Both follow-ups still fit cleanly in the final shape. Both were correctly identified as next-up in round 1; the new findings just re-prioritise their prerequisites.

---

## Suggested fix batches — round 2

| Batch | Scope | Items | Effort | Risk | Dispatchable in parallel? |
|---|---|---|---|---|---|
| **0 — SHIPPING BLOCKER** | Privilege escalation | C1 (OwnerAccess → PlatformOwnerAccess + platformRole consumption + per-route membership check on tenant-scoped admin actions) | ~3-5h | High (touches every admin route + tests) | NO — all admin tests need refresh |
| **A — Quick wins** | Mechanical | H4 (dynamic→record), H10 (CP double-registration), M11 (PoolWarmupService BackgroundService), M12 (drop _metrics.Dispose), M14 (JSON casing), M15 (SSE breaker), M16 (TimeProvider), CLAUDE.md staleness | ~3h | Low | YES, one agent per file group |
| **B — Wiring gaps** | Integration cliffs | H7 (wire status cache reads), H8 (default RunOnStartup=false until handler ships, OR retry-pending-on-no-handler), M3 (wire cleanup endpoint to Elsa trigger or dispatch inline), H3 (rename runbook OR add `/retry` endpoint) | ~5-7h | Medium | YES, one agent per cliff |
| **C — Security hardening** | Audit + secrets + isolation | H1 (postgres-roles via PGPASSWORD + log-suppression), H2 (audit events on Logout-all + SwitchOrg + step-up), H11 (hard-fail on missing KEK in prod), H12 (resolver.EvictAsync on Status flip + hot-path Status check), H13 (decryptor consumes kekVersion via versioned KekProvider), H14 (advisory lock on rotation), M1 (redact exception text), M2 (ClaimsPrincipal through BuildAdminEvent), M4 (scrub SSE Tags/Data), M17 (X-Admin-Note charset whitelist) | ~10-12h | High | YES with care — H11/H13/H14 touch the same KekProvider |
| **D — Pool & worker correctness** | LRU + platform-task | H5 (track deferred-disposes), H9 (advisory lock on rollup scheduler), M5 (SSE → IDbContextFactory), M6 (real LRU in status cache), M7 (typed lease retry exception + per-tenant cap), M8 (persist workerId), M9 (atomic reaper), M10 (handler scope), M13 (build-lock trim) | ~6-8h | Medium | YES, one agent per subsystem |
| **E — Bigger refactors** | Architecture | H6 (Elsa Sequence/TryCatch decomposition of cleanup activity) | ~1-2 days | High (judgment-heavy) | NO — design discussion first |
| **F — Originally-deferred follow-ups** | After A-D | Cluster-wide LISTEN/NOTIFY (was original A); admin_impersonations table + middleware + endpoints (was original B) | ~6-10h | Medium | NO — depends on B (H7) and C (M2) |

**Total scope to fix everything except E and F**: ~27-35h, parallel-dispatchable except for batch 0.

---

## Reviewer reliability note

- `architect-review`, `security-auditor`, `csharp-pro`, and `Explore` (cross-epic) all produced consistent, file:line-referenced findings. Same four trusted in round 1.
- The **biggest miss between round 1 and round 2** was the OwnerAccess privilege escalation (C1). The round-1 security agent looked at audit-trail completeness; the round-2 security agent looked at the actual permission-matrix → endpoint-policy → JWT-claim chain. Both are valid scopes; the latter found the bigger problem. Lesson: brief security agents to walk the entire authz chain, not just the events emitted by it.
- The Explore agent reports "story-by-story shipped per AC" — useful for confirming features are merged but does NOT catch authz, audit, or integration-cliff problems. Always pair it with security and architect for a real review.
