# Wave 4 PR review — 2026-04-27

Independent multi-agent review of the 6 open PRs against `feat/wave-a` (Wave 4 deferred-backlog work). Each PR was reviewed by one agent, in worktree isolation, blinded to the other agents' work.

## Summary

| PR | Title | Verdict | Reviewer agent | Key finding |
|---|---|---|---|---|
| **#335** | nested lockfile cleanup (~120 alerts) | ✅ APPROVE | frontend-developer | 1 MEDIUM (forward-looking npm overrides), 2 LOW |
| **#336** | vitest 3→4 (first of 28 deferred majors) | ❌ REQUEST CHANGES | frontend-developer | **HIGH**: dual vitest 3+4 install breaks CI in `dashboard-user` (24/61 fail) and `dashboard` (127/244 fail) via `@testing-library/jest-dom/vitest` resolution coupling |
| **#337** | H6 flake — false positive verification | ✅ APPROVE | debugger | RefCount trace + 100/100 stress runs confirm no race window. Doc-only PR. |
| **#338** | Story 28-1 PR A: platform defaults | ❌ REQUEST CHANGES | csharp-pro | **HIGH**: false `AGENT_CONFIG.UPDATED.SUCCESS` audit-trail event emitted for no-op writes — violates DCB integrity. **MEDIUM**: 4 admin endpoint response bodies lie about no-op writes. |
| **#339** | Story 28-1 PR B: outbox/queue scope split | ❌ REQUEST CHANGES | architect-review | **HIGH**: `tenantId` parameter is decorative on `EmailOutboxRepository.ClaimNextPendingAsync` + 5 `QueuedTaskRepository` methods — Postgres SQL has no `WHERE TenantId =` predicate; `FindAsync(id)` keyed on PK only. Tenant A's call claims tenant B's rows in shared-DB transition. |
| **#340** | Story 28-1 PR C: cross-tenant query routing | ❌ REQUEST CHANGES | architect-review | **HIGH**: legacy UNION half in `EventRepository.QueryAsync` has no `TenantId == null` filter — caller passing tenant-scoped event type with `tenantId: null` gets rows from EVERY tenant via the legacy half. |

**Tally**: 2 APPROVE, 4 REQUEST CHANGES (each with at least one HIGH).

## Cross-cutting patterns

### Original-agent verification gaps

Every REQUEST CHANGES finding shares a pattern: the original implementing agent verified the wrong test surface.

- **PR #336** (vitest 4): agent ran `pnpm vitest` from root — used root's vitest 4. Didn't run `pnpm --filter @tamma/dashboard-user test` — would have used dashboard-user's pinned vitest 3 with root-hoisted jest-dom resolved against vitest 4. Resolution conflict invisible from the test surface the agent ran.
- **PR #338** (platform defaults): agent verified the GET path (`PutConfig_WithoutTenantContext_IsNoOp_AndGetReturnsPlatformDefault`) — sidestepped the PUT response body shape. Wrote tests that confirm "the value isn't persisted" but didn't test "the response says it was persisted".
- **PR #339** (outbox/queue): agent's tests use single-tenant or two-tenant aggregation scenarios. Never enqueued in tenant A and asked for tenant B's pending — so the missing `WHERE TenantId =` predicate was invisible. Compounded by EF-InMemory not enforcing the relational predicate the way Postgres would.
- **PR #340** (cross-tenant queries): agent's tests always registered `IPlatformEventRepository` in DI. The optional-fallback path was tested only indirectly via `OutboxSmtpSenderPlatformPathTests`. The legacy UNION's missing `TenantId == null` predicate was invisible because no test enqueued a tenant-scoped event row and queried with `tenantId: null`.

**Lesson**: original-agent verification is biased toward "the changes I made do what I intended." Independent post-merge review is biased toward "the contract I see in the diff is what callers actually get." Both lenses are needed; neither alone suffices.

### EF-InMemory hides correctness gaps

PR #339's reviewer surfaced this explicitly: EF-InMemory treats all tenants as the same shared database, so missing `TenantId` predicates produce results that "look right" in tests but leak in Postgres. Two of the four REQUEST CHANGES PRs (#339, #340) had findings that EF-InMemory hid.

**Pattern**: any tenant-isolation test that uses EF-InMemory must include explicit cross-tenant **negative** assertions ("seeded in tenant A, queried by tenant B, got nothing"). Aggregation tests are insufficient.

### PR-description integrity

PR #340's reviewer caught that the PR body listed `AlertRuleEvaluator` under "files refactored" when the diff only added a deferral comment. PR descriptions should be tested against the diff, not authored from intent. Worth a process pattern: agents must read their own diff before writing the PR body.

## Recommended next actions

### Before merge

For each REQUEST CHANGES PR, the highest-leverage fixes per reviewer:

**#336 vitest 3→4**: bump the 9 workspace `package.json` files from `vitest: ^3.0.6` to `^4.1.5` in this same PR. The PR description already inventories them. Mechanical change.

**#338 Story 28-1 PR A**:
1. Stop the false `AGENT_CONFIG.UPDATED.SUCCESS` audit event on no-op writes (`AgentEndpoints.cs:68-88`)
2. Make the 4 admin endpoint responses honest about no-op writes (return 400 OR explicit `{ persisted: false, source: "platform-default" }`)
3. Add 2-3 immutability tests pinning the fresh-clone-per-call contract on `*Defaults.Snapshot()`

**#339 Story 28-1 PR B**:
1. Add `WHERE "TenantId" = @tid` to `EmailOutboxRepository.ClaimViaPostgresAsync` SQL + EF naïve path
2. Add `&& t.TenantId == tenantId` to all `FindAsync(id)` callers in both `EmailOutboxRepository` and `QueuedTaskRepository`
3. `when (ex is not OperationCanceledException)` on per-tenant catches in drain methods
4. Add cross-tenant isolation negative tests (`EnqueueOnTenantA_ClaimByTenantB_ReturnsNull` etc.)
5. Either implement round-robin between tenant + platform queues OR update `OutboxSmtpSender` doc to admit possible platform-queue starvation

**#340 Story 28-1 PR C**:
1. Add `Where(e => e.TenantId == null)` to the legacy UNION half (or document accepted bleed pinned to PR D)
2. Drop `AlertRuleEvaluator` from the PR description's "files refactored" list, OR implement the per-tenant fan-out
3. Apply Decision #2 consistently across `DiagnosticsService.GetReportAsync` and `GetDimensionReportAsync`
4. Add dedicated `EventRepository_QueryAsync_NullPlatformRepo_ReturnsLegacyOnly_NoThrow` test
5. Reconcile platform-LIKE vs legacy-exact type predicate semantics

### After fixes land

Once #336/#338/#339/#340 are updated and merged:
- **PR D dispatches** (task #20 — Story 28-1 entity move). The blocker remains "PR A/B/C must merge cleanly first."
- The 9-test/14-mock vitest migration is sufficient; the 9 workspace-pin updates are the only outstanding piece.
- The MEDIUM forward-looking concerns from #335 (npm overrides for inner test-platform packages) are tracked as backlog, not a blocker.

## Independent reviewer reliability

All 6 reviewers produced findings with file:line references. None hallucinated paths or invented issues. Two notable cases:

- **PR #338 reviewer** correctly identified that the previous (killed) reviewer's "test failure" was a transient artifact — ran the suite twice on a clean checkout, both green. Distinguished real correctness gaps from transient infrastructure flakes.
- **PR #339 reviewer** went unprompted-but-deeper than the brief: checked all 10 callers in the matrix, ran independent SQL trace through the Postgres path AND the EF-InMemory path, and confirmed the missing predicate manifests in both. The reviewer's diagnosis ("EF-InMemory hides the missing predicate because every test uses single tenant or two-tenant aggregation tests") is the kind of cross-cutting observation that turns one bug fix into a test-suite hygiene rule.

## Open process question

The dispatcher worktree-isolation bug bit one reviewer (#338) — its first dispatch had HEAD at the wrong base 532a244 with empty status. I prematurely killed it; the killed-output excerpt suggested it was actually doing real work in the main checkout. Recovery: redispatched the same brief; the new run reproduced the analysis cleanly. Same lesson as the Wave-4 fix dispatch: **before TaskStop on a "stalled" agent, check `git status` in the main checkout** for any uncommitted activity matching the agent's expected scope.

The previous reviewer's test failure didn't reproduce on the redispatch — likely a transient port collision or leftover Postgres state in the killed agent's worktree. The redispatched reviewer's clean-checkout double-run is the more reliable signal.
