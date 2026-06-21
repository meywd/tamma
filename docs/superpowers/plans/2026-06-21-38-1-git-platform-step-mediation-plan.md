# Story 38-1 — Git-platform step mediation (Class A) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every task writes tests
> before implementation.

**Date:** 2026-06-21

**Goal:** Re-point the five Class-A ADL git activities (`CreateBranch` / `CreatePullRequest` /
`MergePullRequest` / `UpdateIssueStatus` / `AnalyzeReview`) from the co-hosted
`IGitHubIntegrationService` to new `Tamma.Api` endpoints (`/api/v1/git/{repo}/...`) that hold the
per-tenant git token (Epic 28/29 cabinet, BYOK→platform), authorize the `tenant ↔ repo` relationship
(the cross-tenant guard — the load-bearing control), perform the platform call, and emit a DCB audit
event. The activities collapse into thin `TammaApiClient` clients holding no token. This mirrors the
Story 32-5 `/llm/call` template verbatim and is the proof-of-pattern for the rest of Epic 38.

**Story file:** `docs/stories/epic-38/story-38-1/38-1-git-platform-step-mediation.md`
**Design spec:** `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` (§1.2 Class-A
rows, §1.3 cross-tenant-token risk, §5.1 Class-A endpoints, §5.3 webhooks out of scope)
**Template story:** `docs/stories/epic-32/story-32-5/32-5-managed-agent-execution-layer.md`

**Tech stack:** .NET 9 / Elsa 3 in `apps/tamma-elsa` (central API `Tamma.Api` + activities
`Tamma.Activities` + engine `Tamma.ElsaServer`). Tests live in
`apps/tamma-elsa/tests/Tamma.Api.Tests/` and `apps/tamma-elsa/tests/Tamma.Activities.Tests/` (xUnit).
Docker-bound suites run via `sg docker -c "dotnet test ..."` (session docker group is stale; plain
`dotnet build` needs no wrapper). **`packages/api` is DELETED — all of this is C#.**

---

## Non-goals (YAGNI guard)

- **NO new git/platform client.** Reuse the existing `IGitHubIntegrationService` impl (Octokit) — only
  move *where it is called from* (into `Tamma.Api`, never the engine). Do not reimplement branch/PR
  logic.
- **NO multi-platform broadening.** This story mediates the existing service; GitLab/Gitea/Bitbucket
  fan-out beyond the current abstraction is out of scope.
- **NO agent-dispatch / Slack mediation.** Those are 38-2 / 38-3.
- **NO guardrail analyzer.** That is 38-4; this story proves the cutover by `grep`, not by a build gate.
- **NO inbound-webhook changes.** `workflow_run.completed` / `WebhookSignalRegistry` are inbound, out
  of scope by nature (design §5.3).
- **NO new control-plane table.** Reuse the Epic 28/29 tenant↔repo registry + cabinet + the tenant
  `domain_events` stream. (If a CP-resident tenant↔repo table proves unavoidable, it must be appended
  to `Program.cs`'s startup-reset DROP list AND `ControlPlaneDbContextModelTests` — see Risks.)
- **NO markup/billing.** Git operations are not metered for cost here; the audit event is the only
  emission.

---

## Current-state findings (verify against the worktree before coding)

| Seam | Where it is today | How 38-1 uses it |
|---|---|---|
| **Git platform service** | `Tamma.Api` `IGitHubIntegrationService` (impl + `GitHub:Token`); **unregistered/null in the engine**. | Delegated to **inside `Tamma.Api`** only; the endpoints call it with a request-scoped per-tenant token. |
| **Git activities** | `Tamma.Activities/ADL/{CreateBranch,CreatePullRequest,MergePullRequest,UpdateIssueStatus,AnalyzeReview}Activity.cs` — inject `IGitHubIntegrationService`. | Gutted to thin `TammaApiClient` clients; drop the injection. |
| **Engine→API callback** | `TammaApiClient` (Bearer `Tamma:ApiToken` via `TammaEngineAuthHandler` + `X-Tenant-Id`; `PostAsync<T>`/`PatchAsync<T>` + `AddTenantHeader` + `RecordHealthAsync`). Routes agent-resolve/budget/diagnostics/provider-session/`/llm/call`. | The transport + auth plane for the 5 git endpoints; add 5 client methods. |
| **Per-tenant token** | Epic 29 cabinet (the git analogue of Story 32-3's LLM credential resolver), keyed per-tenant per Epic 28. | `GitTokenResolver` resolves BYOK→platform inside the API. |
| **Tenant↔repo registry** | Epic 28 tenancy data (the tenant's configured repo(s)). | `GitRepoAuthorizer` checks ownership BEFORE token/platform. |
| **DCB events** | `Tamma.Data` `IEventRepository.AppendAsync(DomainEvent)`, tenant-scoped. | Emit `GIT.*.SUCCESS|FAILED` from the API. |
| **Mode** | `Tamma.Api/Services/PromptStore/TammaMode.cs` — `ITammaModeProvider` (SingleUser | SaaS), process-stable. | Principal keying (user vs tenant) for the guard + token + audit. |

**Key insight:** the only genuinely new code is the *endpoint shell* (`GitEndpoints`), the
*composition service* (`GitMediationService`), the *guard* (`GitRepoAuthorizer`), the *token resolver*
(`GitTokenResolver`), the *wire records*, and the *thin-client cutover* of five activities + five new
`TammaApiClient` methods. The platform call itself is the existing `IGitHubIntegrationService`.

---

## Architecture

```
Engine: {CreateBranch,CreatePullRequest,MergePullRequest,UpdateIssueStatus,AnalyzeReview}Activity
   |  (thin TammaApiClient client — NO token, NO Octokit)
   v
TammaApiClient.{CreateBranch,CreatePullRequest,MergePullRequest,UpdateIssueStatus,GetPullRequestComments}Async
   |  Bearer Tamma:ApiToken + X-Tenant-Id
   v
Tamma.Api  GitEndpoints  ->  GitMediationService.<Op>Async:
   1 authorize tenant↔repo   (IGitRepoAuthorizer)   -- CROSS-TENANT GUARD, FIRST  -> 403 on deny
   2 resolve token           (IGitTokenResolver)    -- Epic 29 cabinet BYOK->platform -> 503 on null
   3 call platform           (IGitHubIntegrationService, request-scoped token, API-only)
   4 emit GIT.*.SUCCESS|FAILED  (tenant IEventRepository)   -- exactly one terminal event
   5 return GitMediationResult -> ToHttpResult (200 | 200 success:false | 403 | 503)
```

Per-mode ownership (CLAUDE.md two-scoping-model): single-user = the sole user's repo(s) + the user's
token + events in the user's store; SaaS = only the `X-Tenant-Id` tenant's repo(s) + the tenant's BYOK
token → platform + events in the tenant `t_<hex>` store, never cross-tenant. Mode from
`ITammaModeProvider`.

---

## Task breakdown

Order: T1 (wire records + event types) → T2 (guard) → T3 (token resolver) → T4 (mediation service:
happy path) → T5 (typed failures + audit) → T6 (endpoints) → T7 (thin-activity cutover + client
methods) → T8 (engine-registration removal + DI wiring). T1 is parallel-safe with T2/T3.

### T1 — Wire records + event-type constants

**Scope:** The request/response shapes and `GIT.*` event constants. No behaviour.

**Files (new):** `Services/Git/GitRequests.cs` (`CreateBranchRequest`, `CreatePullRequestRequest`,
`MergePrRequest`, `UpdateIssueRequest`), `Services/Git/GitResponses.cs` (`GitMediationResult`,
`PrCommentDto`), `Services/Git/GitEventTypes.cs` (`GIT.BRANCH_CREATED.*`, `GIT.PR_OPENED.*`,
`GIT.PR_MERGED.*` / `GIT.PR_MERGE.FAILED`, `GIT.ISSUE_UPDATED.*`, `GIT.PR_COMMENTS_READ.*`).

**Tests (first):** `tests/Tamma.Api.Tests/Git/GitMediationResultTests.cs` — record equality; `Success=false`
always carries `FailureCode`+`FailureReason`; `CredentialSource` is `byok|platform`; no token field exists.

**Acceptance:**
- [ ] Records compile; `GitMediationResult` has all AC fields; **no field can carry the raw token**.

### T2 — Cross-tenant guard (`IGitRepoAuthorizer`) — the load-bearing control

**Scope:** `GitRepoAuthorizer : IGitRepoAuthorizer` — `AuthorizeAsync(tenantId, repo, ct)` against the
Epic 28 tenant↔repo registry. Deny/unevaluable → not authorized (NEVER a default-allow).

**Files (new):** `Services/Git/IGitRepoAuthorizer.cs`, `Services/Git/GitRepoAuthorizer.cs`.

**Tests (first):** `tests/Tamma.Api.Tests/Git/GitRepoAuthorizerTests.cs` — tenant owns repo → allow;
tenant does not own repo → deny; unevaluable (registry error / missing mapping) → deny (fail-closed);
single-user mode → the sole user's configured repo allowed.

**Acceptance:**
- [ ] No default-allow path exists; deny is the safe default.
- [ ] Mode matrix (single-user user-owned vs SaaS tenant-owned) passes.

### T3 — Per-tenant token resolver (`IGitTokenResolver`) — BYOK→platform, fail-closed

**Scope:** `GitTokenResolver : IGitTokenResolver` — `ResolveAsync(tenantId, repo, ct)` → `{ Token,
Source }`. Epic 29 cabinet (tenant BYOK PAT / installation token) → else platform-provided where
allowed → else **null** (NEVER an empty/default token). Mirrors Story 32-3.

**Files (new):** `Services/Git/IGitTokenResolver.cs`, `Services/Git/GitTokenResolver.cs`.

**Tests (first):** `tests/Tamma.Api.Tests/Git/GitTokenResolverTests.cs` — cabinet has tenant token →
`Source="byok"`; absent + platform allowed → `Source="platform"`; absent + not allowed → null
(fail-closed); the returned `Token` is never logged.

**Acceptance:**
- [ ] BYOK→platform→null order; null when unresolvable (no empty/default fallback).
- [ ] `Source` is `byok|platform`; token never surfaces in logs.

### T4 — `GitMediationService` core composition (happy path, per operation)

**Scope:** `GitMediationService : IGitMediationService` with one method per operation
(`CreateBranchAsync`, `CreatePullRequestAsync`, `MergePullRequestAsync`, `UpdateIssueStatusAsync`,
`GetPullRequestCommentsAsync`). Compose guard(1) → token(2) → `IGitHubIntegrationService`(3) → audit(4)
→ result(5). Token is request-scoped, dropped after the call.

**Files (new):** `Services/Git/IGitMediationService.cs`, `Services/Git/GitMediationService.cs`.

**Collaborators (constructor-injected — fakes in tests):** `IGitRepoAuthorizer`, `IGitTokenResolver`,
`IGitHubIntegrationService`, `IEventRepository`, `ITammaModeProvider`, `ILogger<GitMediationService>`.

**Tests (first):** `tests/Tamma.Api.Tests/Git/GitMediationServiceTests.cs` — each operation happy path:
guard→token→service→audit called once in order; correct response DTO populated; `credentialSource`
copied from the resolver; exactly one terminal `GIT.*.SUCCESS` event with the right tags.

**Acceptance:**
- [ ] Each operation returns a fully-populated `GitMediationResult{Success=true}`.
- [ ] Guard runs BEFORE token; token BEFORE platform; one terminal event per call.

### T5 — Typed failures + audit discipline (fail-closed, status preservation)

**Scope:** Each step gets a typed-failure exit:
- guard deny → 403 `REPO_NOT_AUTHORIZED` (platform never called, token never resolved);
- token null → 503 `GIT_TOKEN_UNAVAILABLE` (`retryable:false`);
- platform error → **200 `success:false`** with `failureCode ∈ {GIT_CONFLICT, NOT_MERGEABLE,
  NOT_FOUND, PLATFORM_ERROR}` and **preserved** `platformStatusCode`.
No expected failure throws; a raw 5xx is never produced. Exactly one terminal `GIT.*.FAILED` event
(except guard-deny, which emits a `GIT.*.DENIED` or `FAILED` with `failureCode=REPO_NOT_AUTHORIZED` —
pin in a test).

**Files:** extend `GitMediationService`; add a `ToHttpResult()` mapper (in `GitEndpoints` or a small
mapper type).

**Tests (first):** extend `GitMediationServiceTests` — one test per failure code; guard-deny → service
never invoked; token-null → platform never invoked; platform 5xx → 200 `success:false` +
`platformStatusCode` preserved (assert no raw 5xx); "exactly one terminal event per call" invariant.

**Acceptance:**
- [ ] All failure codes produce typed results; none throw; raw 5xx never leaks.
- [ ] Guard-deny and token-null short-circuit before the platform call.

### T6 — Endpoints (`GitEndpoints.cs`)

**Scope:** Map the five routes; engine-only auth (`EngineAuthPolicy` — same plane as `/llm/call`);
bind `{repo}` + body; derive `tenantId` from `X-Tenant-Id`; delegate to `GitMediationService`;
`ToHttpResult`.

**Files (new):** `Endpoints/GitEndpoints.cs`; modify `Tamma.Api/Program.cs` (map endpoints; register
guard/token/mediation; ensure `IGitHubIntegrationService` registered in the API).

**Tests (first):** `tests/Tamma.Api.Tests/Endpoints/GitEndpointsTests.cs` — 401 missing/invalid bearer;
valid bearer + `X-Tenant-Id` → bound + delegated; 403 on guard deny; 200 `success:false` +
`platformStatusCode` on platform failure; happy 200 per route. Use `WebApplicationFactory` + fakes.

**Acceptance:**
- [ ] All five routes served, engine-only, 401 on missing bearer; DI resolves at host startup.

### T7 — Thin-activity cutover + `TammaApiClient` methods (AC5)

**Scope:** Gut the five ADL activities to thin `TammaApiClient` clients (drop
`IGitHubIntegrationService`); add five client methods (`CreateBranchAsync`/`CreatePullRequestAsync`/
`MergePullRequestAsync`/`UpdateIssueStatusAsync`/`GetPullRequestCommentsAsync`) following the existing
`PostAsync<T>`/`PatchAsync<T>` + `AddTenantHeader` + `RecordHealthAsync` pattern. Each activity writes
the **same** workflow variables it writes today.

**Files:** modify `Tamma.Activities/ADL/{CreateBranch,CreatePullRequest,MergePullRequest,
UpdateIssueStatus,AnalyzeReview}Activity.cs`; modify `Tamma.Api/Clients/TammaApiClient.cs`.

**Tests (first):** `tests/Tamma.Activities.Tests/ADL/GitActivityThinClientTests.cs` — given each
`GitMediationResult`, the activity writes the same `BranchRef`/`PrNumber`/`MergeResult`/`IssueStatus`/
`ReviewComments` variables as the legacy path; a minimal ADL workflow branches on them unchanged; the
activity holds no `IGitHubIntegrationService`.

**Acceptance:**
- [ ] Five activities cut over; same variables out; no Octokit/platform-HTTP reference.
- [ ] `grep` over `Tamma.Activities` for `IGitHubIntegrationService` → zero.

### T8 — Engine-registration removal + credential-safety sweep

**Scope:** Ensure `IGitHubIntegrationService` is NOT registered/reachable in `Tamma.ElsaServer`;
confirm no git token can be pushed into the engine process. Credential-safety sweep: token never in
any response/log/event.

**Files:** modify `Tamma.ElsaServer/Program.cs` (remove any git-service registration).

**Tests (first):** a credential-safety test over response/log/event payloads (no token substring); a
host-boot smoke test asserting the engine resolves the activities **without** any git-service
registration.

**Acceptance:**
- [ ] Engine holds no git token / git-service registration.
- [ ] Credential-safety test green: token absent from every response, log, and event.

---

## Story order & dependencies

External/pattern prereqs: **Story 32-5** (the `/llm/call` template to mirror), **Epic 28** (tenant↔repo
registry + per-tenant keying), **Epic 29** (cabinet — per-tenant git token), **Epic 9** (`TammaApiClient`
plane), **Epic 4** (DCB). Code to their interfaces with fakes until landed. Internal order:
T1 ∥ T2 ∥ T3 → T4 → T5 → T6 → T7 → T8. Downstream: 38-4 guardrail proves the cutover stays cut (not a
blocker).

## Verification

```bash
# build (no docker wrapper needed)
dotnet build apps/tamma-elsa/Tamma.sln
# tests (docker-bound suites need the sg wrapper; session docker group is stale)
sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Api.Tests/ --filter FullyQualifiedName~Git|FullyQualifiedName~GitEndpoints"
sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Activities.Tests/ --filter FullyQualifiedName~ADL"
# cutover proof: zero IGitHubIntegrationService injections in the engine activities
grep -rn "IGitHubIntegrationService" apps/tamma-elsa/src/Tamma.Activities
# credential-safety: no token substring leaks (manual review + the safety test)
```

## Risks

- **Cross-tenant write/merge via a mis-scoped token (design §1.3 — THE Class-A risk):** Critical. The
  guard (`IGitRepoAuthorizer`) runs FIRST, before token resolution or platform call; deny → 403, never
  a call. Dedicated cross-tenant test asserts the platform is unreachable on deny. Token is per-tenant
  from the cabinet, never a shared default.
- **Raw 5xx leak breaks ADL outcome mapping:** High. The mapper always returns 200 `success:false` +
  preserved `platformStatusCode` for expected platform failures; 403/401/503 only for
  guard/auth/credential; HTTP-status-fidelity test.
- **Engine still holds a git token after cutover:** High. `IGitHubIntegrationService` stays API-only;
  `grep` confirms zero injections; T8 host-boot smoke test; 38-4 makes it permanent.
- **Token leak into log/response/event:** High. Request-scoped token dropped after the call;
  `credentialSource` is the only credential field surfaced; explicit credential-safety test.
- **Thin activity writes different variables → ADL workflow breaks:** High. Map each result to the
  exact legacy variable shapes; minimal ADL workflow integration test.
- **Empty/default token fallback:** Medium. Fail-closed: 503 `GIT_TOKEN_UNAVAILABLE`,
  `retryable:false`; never call with an empty token (`feedback_resolution_no_empty_fallback`).
- **EF / control-plane table (only if a CP tenant↔repo table is unavoidable):** Medium. Default is **no
  new CP table** (reuse Epic 28/29 registry + cabinet). IF one is added: append it to `Program.cs`'s
  startup-reset "Wiping Tamma-managed public-schema tables" DROP list (else a 2nd host boot fails with
  `relation already exists`) AND update the strict `Model_Has_ExpectedControlPlaneEntities`
  `BeEquivalentTo` contract test. This plan **amends/extends the existing EF migration snapshot**, it
  does not branch it (stories are implemented sequentially on one snapshot).
- **Dependency timing (32-5 pattern / Epic 28-29):** Medium. Mirror 32-5; code to interfaces; fakes
  until landed.
