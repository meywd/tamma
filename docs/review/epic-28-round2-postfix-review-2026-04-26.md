# Epic 28 Round-2 Post-Fix Review — 2026-04-26

**Subject**: independent multi-agent review of `integration/epic-28-r2-fixes` (tip `b39ddde`).

**Reviewers dispatched**: 4 in parallel (architect, security, csharp-pro, Explore cross-epic). Each branch in worktree isolation, blinded to the round-1 review, the round-2 review, and the final-delta report. Independent verification of: (a) did the merged integration actually close the named findings, (b) were new issues introduced during the merge.

**Reviewer reliability**:
- ✅ **Explore (cross-epic)** — completed. Verified per-finding closure with file:line evidence; no cross-epic regressions.
- ✅ **security-auditor** — completed. 17 findings.
- ✅ **csharp-pro** — completed. 25 findings.
- ⚠️ **architect-review** — stalled at 600s mid-analysis; 3 partial findings captured (all "acceptable"). No further dispatch.

---

## TL;DR

The final-delta report I wrote earlier (`docs/review/epic-28-round2-final-delta-2026-04-26.md`) claimed all round-2 findings were closed. **That claim was based on the agent batches' self-reports.** The post-fix review found:

- **2 round-2 findings whose fixes are INCOMPLETE** (C1 privilege escalation — leaks via two un-migrated routes; H1 postgres-roles leak — runbook claim is false, argv leak still real).
- **1 round-2 fix that introduces a new HIGH security gap** (impersonation JWT inherits `platform_admin` → impersonation token = unscoped admin token).
- **A dispatcher-bypass for the H11 fix** (single-arg `TenantSecretProtector.FromConfiguration` overload still wired and silently HKDFs from `Cranl:ApiKey` in production).
- **Several merge-fixup residues** (4 copies of `ITenantConnectionResolver` test doubles, 7 copies of recording publisher, dead conditional in `JwtService`, missing migration designer file).

**Net assessment**: the integration substantially closes the round-2 findings, but **3 of the 4 HIGH security findings** below are functional regressions or incomplete fixes — not nice-to-haves. They should be fixed before this branch lands on `main`.

---

## NEW issues found by the post-fix review (not in round-2 review)

### Security (4 HIGH)

| # | Severity | Finding | File:line | Why round-2 missed it |
|---|---|---|---|---|
| **PF-S1** | HIGH | C1 fix INCOMPLETE — `AdminEndpoints.UpdateUserRole` and `DeleteUser` were not migrated from `OwnerAccess` → `PlatformOwnerAccess`. Both mutate the **global** `users` table. Any signed-up user can call `PUT /api/admin/users/{id}/role` and `DELETE /api/admin/users/{id}` against any platform user. | `Program.cs:1031-1032`, `AdminEndpoints.cs:140-189,191-345` | A2 swept ~30 admin routes but missed these two, which sit in `AdminEndpoints.cs` (not `Admin/AdminTenantsEndpoints.cs`). The Explore agent verified the routes A2 named were on `PlatformOwnerAccess`; it didn't enumerate ALL `OwnerAccess` references. |
| **PF-S2** | HIGH | H1 fix INCOMPLETE — postgres bootstrap STILL leaks plaintext via `psql --set="admin_password=$X"` argv (visible in `/proc/<pid>/cmdline`). The runbook's claim "NEVER visible in /proc/<pid>/cmdline" is false. The `pg_stat_activity`/`log_statement` mitigations DO work; the argv leak is what's missed. | `scripts/db/docker-entrypoint-bootstrap.sh:74-80` | B's fix focused on server-side `log_statement` suppression. Argv exposure is a separate vector that the runbook claim misrepresented. |
| **PF-S3** | HIGH | NEW HIGH (introduced by follow-up B) — Impersonation JWT inherits the operator's `platformRole=platform_admin`. While impersonating tenant X, the operator (or anyone with the 15-min token) can hit `/api/admin/tenants/Y/...`, KEK rotation, alerts — every `PlatformOwnerAccess` route. `imp_id` is only an audit breadcrumb, not a scope reduction. A stolen impersonation JWT = a stolen platform-admin JWT. | `AdminImpersonationService.cs:175-180`, `JwtService.cs:86-88`, `PermissionHandler.cs:40-43` | The impersonation feature was specced as "becomes the target", but the JWT mint reuses the operator's `platformRole` claim. Round-2 didn't catch it because the threat model assumed scope reduction. |
| **PF-S4** | HIGH | H11 dispatcher-bypass — `TenantSecretProtector.FromConfiguration(IConfiguration, ILogger?)` (single-arg overload) is still wired in production via `PlatformEventsServiceCollectionExtensions.cs:64`, calling itself with `environment: null`. Silently HKDFs from `Cranl:ApiKey` regardless of environment. Any DI ordering regression that registers the events extension before the provisioning extension yields a production deploy that silently encrypts under the dev fallback. | `PlatformEventsServiceCollectionExtensions.cs:64`, `TenantSecretProtector.cs:81` | B's fix added the two-arg overload but didn't delete or guard the single-arg one. `TryAddSingleton<ITenantConnectionStringProtector>` falls through. |

### Security (3 MEDIUM worth flagging)

| # | Finding | File:line |
|---|---|---|
| **PF-S5** | KEK retry mutates `KekProvider._secondary` in-memory BEFORE acquiring the cluster-wide advisory lock. Two pods racing `/retry` both mount the same secondary. The advisory-lock catch path also force-sets `acquired=true` on transient errors → two pods can race the rotation. | `KekRotationCoordinator.cs:300-302,328-330,381-395` |
| **PF-S6** | `BuildAuthAuditEvent` trusts unvalidated `X-Forwarded-For` for `actorIp`. Any internet-facing request can spoof IPs in audit events for `USER.LOGOUT_ALL.SUCCESS`/`USER.ORG_SWITCHED.SUCCESS`. Audit-log poisoning. | `AuthEndpoints.cs:659-664` |
| **PF-S7** | `ErrorRedactor` regex set doesn't catch `postgresql://user:pass@host/db` connection-string credentials. Tenant DB connection-failure exceptions whose `ex.Message` includes the connection string flow through the cleanup workflow → `tenants.ProvisioningDetail` (visible to platform admins via SSE). | `ErrorRedactor.cs:31-102` |
| **PF-S8** | KEK advisory-lock acquisition silently falls through to "lock acquired" on any non-cancellation exception (transient DB blip during `pg_try_advisory_lock` flips `acquired=true`). | `KekRotationCoordinator.cs:381-395` |
| **PF-S9** | Bootstrap superadmin race: two concurrent first-user registrations both observe `existingUserCount==0` → both get `platform_admin`. Comment acknowledges "fine if two", but a coordinated registration burst against a freshly-deployed instance could mint several. | `AuthEndpoints.cs:187-188,1182-1183` |
| **PF-S10** | LRU resolver health check skips `WHERE KekVersion IS NULL` — legacy rows are invisible to the laggard check. After two rotations they fall off the retired-keys ring and become permanently undecryptable, but readiness still passes. | `KekCabinetHealthCheck.cs:71` |

### C# / lint (8 HIGH — most concerning)

| # | Finding | File:line |
|---|---|---|
| **PF-C1** | Fire-and-forget `Task.Run(...)` in `TenantStatusInvalidationListener.OnNotification` uses `CancellationToken.None`, untracked. Host shutdown can't await in-flight evictions — same pattern Batch D fixed in the LRU resolver, but the new listener didn't follow it. | `TenantStatusInvalidationListener.cs:247-260` |
| **PF-C2** | Dead conditional ternary: `var lifetime = impId.HasValue ? TimeSpan.FromMinutes(15) : TimeSpan.FromMinutes(15);` — both branches identical. The XML doc claims `min(MaxSessionMinutes, 15)` — comment lies. | `JwtService.cs:163-165` |
| **PF-C3** | KEK advisory-lock connection-pool lifecycle ambiguity: `lockCtx` is an `IDbContextFactory`-pooled context. Npgsql sends `DISCARD ALL` on connection return → **releases session-level advisory locks**. The single `lockCtx` held for the rotation masks this in the happy path; if EF returns the connection mid-rotation the lock disappears silently. | `KekRotationCoordinator.cs:716-732` |
| **PF-C4** | 4 copies of `ITenantConnectionResolver` test doubles (`RecordingConnectionResolver` ×2, `RecordingResolver`, `NoopConnectionResolver`), 7 copies of `IPlatformEventPublisher` doubles, 2 of `RecordingInvalidationBus`. Each agent batch invented its own; merge-fixup b8260c4 reconciled signatures but didn't dedupe. | `tests/.../AdminTenantsTests.cs:91`, `AdminTenantsAuditAndNoteTests.cs:311`, `Epic28/QuickWinsRound2Tests.cs:341`, `TenantStatus/...:54` |
| **PF-C5** | `KekRotationCoordinator` has 42 awaits with 1 `ConfigureAwait(false)`. Singleton background-driver — exactly the lib/infra shape that should be context-agnostic. Sibling `LruPooledTenantConnectionResolver` uses ConfigureAwait on 18 of 21 awaits. | (file-level) |
| **PF-C6** | `ITenantStatusProbe.Invalidate(Guid)` is on the interface but no resolver caller invokes it. Doc says "read-only". Dead surface that future agents will misuse. | `ITenantStatusProbe.cs:33` |
| **PF-C7** | Migration `20260426120000_KekRotations.cs` has no companion `.Designer.cs` file (every other migration in the directory does). Hand-written migration → model snapshot may not match → next `dotnet ef migrations add` may detect drift. | (migration directory) |
| **PF-C8** | Async-correctness inconsistency between regular and `*ForCleanupActivity` siblings — cleanup variants use `ConfigureAwait(false)`; their non-cleanup peers don't. `EvictTenantPoolForCleanupActivity:70` is missing it where its peers have it. | `Tamma.Activities/TenantLifecycle/*.cs` |

### C# / lint (selected MEDIUM)

| # | Finding |
|---|---|
| PF-C9 | `AdminImpersonationsEndpoints.cs:153` — `["startedAt"] = DateTime.UtcNow` directly in audit-event payload, while siblings use `result.ExpiresAt` (TimeProvider-aware). |
| PF-C10 | `JwtService.cs:171` — `expires: DateTime.UtcNow.Add(lifetime)` not via `TimeProvider`. Tests can't pin the clock; the merge introduced `impId` time math here without addressing it. |
| PF-C11 | `LruPooledTenantConnectionResolver.cs:791,836` — uses `DateTimeOffset.UtcNow` directly for tenant-row cache TTL while `MemoryTenantStatusCache` (sibling per-pod LRU) takes `TimeProvider`. Inconsistent. |
| PF-C12 | `PlatformTaskWorker.cs:230` — `await repo.FailAsync(task.Id, $"{ex.GetType().Name}: {ex.Message}", ...)` concatenates raw `ex.Message`. Round-2 M1 added `IErrorRedactor` for this exact case in cleanup; the platform task worker wasn't updated to use it. |
| PF-C13 | `KekRotationCoordinator.WaitForCompletionAsync()` and `EmitCleanupTerminalEventActivity.BuildFailureSummaryForTesting()` are public methods documented "Test-only" — should be `internal` + `[InternalsVisibleTo]`. |
| PF-C14 | EF migration naming inconsistent: `KekRotations` (no verb), `AddUsersPlatformRole` (Add), `PlatformQueuedTaskClaimedByUnprocessable` (no verb, awkward). Mix of `idx_*` snake_case and `IX_*` PascalCase index names within `AddAdminImpersonations.cs`. |
| PF-C15 | Listener convergence asymmetry: admin endpoint sequences `cache.Invalidate → resolver.EvictAsync → bus.PublishAsync` (sync + 2 awaits); listener does `cache.Invalidate` (sync) then **fires-and-forgets** `Task.Run(resolver.EvictAsync)`. Originating pod waits for resolver eviction; sibling pods don't. Timing-asymmetry complicates reasoning. |
| PF-C16 | Mixed primary/classic constructor conventions in `PermissionHandler.cs`: `PermissionRequirement` primary-ctor, `PermissionHandler` classic, `PlatformPermissionRequirement` primary-ctor, `PlatformPermissionHandler` classic — visible inconsistency at agent-batch boundary. |

### Architecture (3 partial, all "acceptable")

The architect agent stalled mid-LRU-pool analysis. The 3 captured findings:

| # | Finding | Architect's verdict |
|---|---|---|
| PF-A1 | `EvictAsync` drops `_outstandingLeases` counter — re-built pool sees count=0 while old leases on deferred-disposing entry persist | "Acceptable" — bounded by dispose drain |
| PF-A2 | Resolver hot-path Status check only fires when `_statusProbe` returns a value; cache-miss path serves stale data source | "Acceptable" — NOTIFY loop covers cross-pod convergence |
| PF-A3 | `DecrementLeaseCount` TOCTOU race in `AddOrUpdate`+`TryRemove` sequence | "Acceptable" — `TryRemove(KeyValuePair)` rejects on value-mismatch, race converges |

Sections never reached: cleanup workflow decomposition, KEK lifecycle, impersonations, integration cliffs.

### Cross-epic (Explore) — clean

Explore independently verified all named round-2 closures with file:line evidence. **No cross-epic regressions** found. **No skipped tests.** **Documentation up to date.** The Explore pass reflects the surface-level "did the named files/endpoints get added" view; security + csharp went deeper and found the gaps above.

---

## What the final-delta report got right

- All EF migrations exist, columns match the spec, model-snapshot updated.
- `PlatformOwnerAccess` policy + `PlatformPermissionHandler` exist and are wired into the routes A2 explicitly named.
- Status cache is read by both `TenantContextMiddleware` and `ApiKeyAuthHandler`.
- LRU resolver hot path consults `ITenantStatusProbe`; admin endpoints call `EvictAsync`.
- KEK rotation has an advisory-lock pattern + persistence + retired-key ring + a `/retry` endpoint.
- LISTEN/NOTIFY pipeline has `pg_notify` (parameterized, not concat) + listener with reconnect.
- admin_impersonations table + service + middleware + endpoints exist with reason-charset CHECK constraint.
- Cross-epic untouched (Epics 9, 12, 19, 27, 29, 30, 31).
- 3168 tests pass / 0 fail / 3 skip (verified again post-merge).

## What the final-delta report got wrong / missed

1. **C1 was claimed "closed" — actually leaks via 2 un-migrated routes** (`UpdateUserRole`, `DeleteUser` on `OwnerAccess`).
2. **H1 was claimed "verified leak-free" — verification was server-log-side only**; argv leak still exists, runbook claim is false.
3. **Follow-up B (admin_impersonations) shipped a new HIGH** — JWT scope-reduction is missing; impersonation = full platform-admin.
4. **H11 dispatcher-bypass** — single-arg overload still wired; production hard-fail can be silently bypassed by DI ordering regression.
5. **Test-double duplication** — final-delta noted "merge-fixup reconciled signatures" but didn't note the ~13 duplicate test doubles that are now scattered across test files.
6. **`JwtService.cs:163-165` dead conditional ternary** — final-delta didn't review the impersonation lifetime math; both branches return 15 min, doc claims `min(MaxSessionMinutes, 15)`.
7. **KEK migration designer file missing** — the `KekRotations` migration is hand-written without the `.Designer.cs` companion. Model-drift risk on next `dotnet ef`.

---

## Punchlist — concrete remediation work

Ordered by severity:

### Must-fix before merge (4 items) — ✅ ALL CLOSED in `2ce43b3` (merged via `ee37568`)

1. **PF-S1** — ✅ Closed. `Program.cs:1031-1032` swapped to `PlatformOwnerAccess`; handlers now refuse cross-platform-admin demote/delete; emit `USER.ROLE_CHANGED.SUCCESS` / `USER.DELETED.SUCCESS` events with `actorUserId`+`actorEmail`; cross-PA-block path emits `USER.ROLE_CHANGE.BLOCKED` / `USER.DELETE.BLOCKED` warnings. **+8 tests.**
2. **PF-S2** — ✅ Closed. `docker-entrypoint-bootstrap.sh` rewritten to use `mktemp` + `chmod 0600` preamble + `psql --file=-` stdin pipe. **No password ever appears in `psql` argv.** Verification harness `scripts/db/test-no-argv-leak.sh` runs the bootstrap inside a real `postgres:17-alpine` container with `psql` shadowed by a wrapper that logs argv — confirms `argv = [--dbname, --username, ON_ERROR_STOP, cp_database, --file=-]` only, no `--set=*_password`. Runbook updated to remove the false claim and document the actual mitigation. **Verification proof committed.**
3. **PF-S3** — ✅ Closed. `JwtService.GenerateAccessToken` mints `platformRole="user"` whenever `impId.HasValue`, regardless of the operator's actual role. New `actor_user_id` + `actor_email` claims preserve operator identity for audit. `ImpersonationContextMiddleware` reads them via `GetActorUserId`/`GetActorEmail` accessors. Decision: scope-reduction convention is `platformRole="user"` for every impersonation token; per-tenant reach via `role` claim (target's role inside target tenant). **Stolen impersonation token = target-scoped session, not cross-tenant platform-admin ticket.** Also collapsed the dead `impId.HasValue ? 15min : 15min` ternary as side-cleanup (PF-C2). **+8 tests.**
4. **PF-S4** — ✅ Closed. Option A taken: deleted the single-arg `TenantSecretProtector.FromConfiguration(IConfiguration, ILogger?)` overload entirely. Updated `PlatformEventsServiceCollectionExtensions.cs:64` to resolve `IHostEnvironment` from `sp` and call the two-arg overload. Tests that genuinely lack environment context call the two-arg overload with `environment: null` explicitly (grep-able for future audits). **+2 tests verifying the DI path hard-fails in production when `Cranl:EncryptionKey` is missing.**

**Final state**: 3186 tests passing (3168 baseline + 18 new), 0 failing, 3 skipped (the same Story 28-1 aspirational tests). One transient flake on first run that didn't reproduce on rerun — consistent with the round-1 H6 flake-watch punchlist item; not a regression.

**Original must-fix punchlist (above) preserved for historical reference.** The same 4 items have moved from must-fix to closed.

### Should-fix soon (8 items)

5. **PF-S5 + PF-S8** — Move `RestoreStagedSecondary` and the rotation-row-status flip into `RunRotationAsync` AFTER advisory-lock acquisition. Remove the `acquired = true` fall-through (or limit it to detectable in-memory-provider scenarios).
6. **PF-S6** — Honour `X-Forwarded-For` only when origin is in a configured trusted-proxy CIDR list.
7. **PF-S7** — Add `(postgres|postgresql|mysql|mongodb)://[^:@\s]+:[^@\s]+@\S+` regex to `ErrorRedactor`.
8. **PF-S9** — Use `INSERT ... WHERE NOT EXISTS` for bootstrap-superadmin promotion, OR a single dedicated `platform_bootstrap` row with unique-constraint enforcement.
9. **PF-S10** — Treat `KekVersion IS NULL` as version 0 in `KekCabinetHealthCheck`; surface "n legacy rows lack version stamp" as `Unhealthy`.
10. **PF-C1** — Track `TenantStatusInvalidationListener.OnNotification` tasks and drain in `StopAsync`. Use the listener's `stoppingToken`, not `CancellationToken.None`.
11. **PF-C2** — Either implement the `min(MaxSessionMinutes, 15)` doc claim, or simplify to a single 15-min literal and rewrite the comment.
12. **PF-C3** — Bind a single dedicated `NpgsqlConnection` for the rotation lock (bypass EF entirely), OR document that pooled-context disposal preserves session-level advisory locks (verify via Npgsql docs first).

### Hygiene cleanups (5 items)

13. **PF-C4** — Promote shared test doubles into `Tamma.Api.Tests.TestDoubles` namespace (`internal`).
14. **PF-C5** — Add `ConfigureAwait(false)` to every await in `KekRotationCoordinator`, `KekProvider`, `AesGcmConnectionStringDecryptor`, `KekCabinetHealthCheck`, `AdminImpersonationService`. Or codify via `.editorconfig` / CA2007.
15. **PF-C6** — Remove `Invalidate` from `ITenantStatusProbe`; callers use `ITenantStatusCache.Invalidate` directly.
16. **PF-C7** — Regenerate `KekRotations` migration via `dotnet ef migrations add` to produce the `.Designer.cs` companion + verify snapshot.
17. **PF-C12** — `PlatformTaskWorker.FailAsync` should use `IErrorRedactor` (Round-2 M1 missed this caller).

### Plus (already noted in final-delta) — known punchlist
- KEK retry path inherits original Started actor (no fresh ClaimsPrincipal in `RetryAsync`)
- `*ForCleanupActivity` step uses `ex.GetType().Name` as failure code rather than the structured codes from C's deleted classifier
- SSE `Last-Event-ID` resumption header still unused
- Round-1 H6 flake-watch in CI not yet verified

---

## Reviewer reliability — lessons for the next round

- **Explore agent's "all closed" pass is shallow by design** — it verifies named files/endpoints exist, NOT that the fix logic is correct. Always pair with security + csharp + architect for real review. The final-delta report I wrote was anchored on Explore-style reasoning ("everything cited is in tree"), which is why it missed PF-S1 through PF-S4.
- **architect-review stalls on deep solo analysis** — second time this run. Worth dispatching with a tighter scope (5 areas instead of 10) to avoid the watchdog timeout.
- **security-auditor + csharp-pro are the highest-value reviewers for post-merge audit** — both caught real issues with file:line evidence in <15 min each.
- **Multi-agent merges accumulate convention drift** that no single agent can see. The 4 different test doubles for the same interface, the mixed primary/classic constructors in one file, the 6 missing `ConfigureAwait` calls — these are signatures of "every batch had its own taste". Worth one cleanup pass per major merge.

---

## Verification

The integration tip `b39ddde` still builds + tests green:

```
$ dotnet test → 3168 passed / 0 failed / 3 skipped
```

None of the post-fix findings are test-suite breakage. They're correctness, scope, and hygiene gaps that the merged tests don't exercise.
