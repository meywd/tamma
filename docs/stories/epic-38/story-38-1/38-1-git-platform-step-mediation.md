# Story 38-1: Git-platform step mediation (Class A)

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)

## User Story

As a **platform engineer running the Tamma engine as per-tenant dedicated compute (the Cranl path)**,
I want the git-platform write/read steps (`CreateBranch` / `CreatePullRequest` / `MergePullRequest` / `UpdateIssueStatus` / `AnalyzeReview`) to stop resolving the co-hosted `IGitHubIntegrationService` and instead call internal `Tamma.Api` git endpoints that hold the platform token, authorize that *this* tenant may act on *this* repo, perform the call, and audit it,
So that **a workflow step never holds a git-platform token in the engine process** — closing the highest-blast-radius rule-1 violation (a mis-scoped token = cross-tenant write/merge), exactly the way Story 32-5's `/llm/call` closed it for LLM keys.

## Priority

P1 — Class A is the **high-blast-radius** non-LLM violator (design §1.3): "high the moment the engine is not co-hosted with `Tamma.Api`: a mis-scoped platform token = cross-tenant write/merge." It is the first Epic 38 story because git writes (branch/PR/**merge**/issue) are the most dangerous surface to leave co-hosted. It mirrors the 32-5 `/llm/call` template and is the proof-of-pattern the rest of Epic 38 (38-2 agent-dispatch, 38-3 Slack) follows.

## Context

### What exists today (the violation — design §1.2)

Five ADL activities perform git-platform effects by resolving **`IGitHubIntegrationService`** from the DI container:

| Activity | Operation | Notes |
|---|---|---|
| `ADL/CreateBranchActivity` | create a branch | write |
| `ADL/CreatePullRequestActivity` | open a PR | write |
| `ADL/MergePullRequestActivity` | merge a PR | **write — highest risk** |
| `ADL/UpdateIssueStatusActivity` | label/close/transition an issue | write |
| `ADL/AnalyzeReviewActivity` | read PR review comments | read |

`IGitHubIntegrationService` is **implemented in `Tamma.Api`** and holds `GitHub:Token` (the PAT / installation token). In the standalone engine it is **unregistered/null** — so today the call only succeeds because the engine and the API are **co-hosted** in one process. Per design §1.1, *co-hosting is not compliance*: rule #1 forbids the step resolving a credential-holding vendor service; it must call an internal endpoint **over the wire**. The moment the engine runs as per-tenant dedicated compute, the token would have to be pushed into the engine process — exactly what this story prevents.

### What this story does (mirror `/llm/call` — design §5.1 Class A)

Re-point the five activities to new `Tamma.Api` git endpoints (design §5.1):

```
POST  /api/v1/git/{repo}/branches                  # CreateBranch
POST  /api/v1/git/{repo}/pull-requests             # CreatePullRequest
PUT   /api/v1/git/{repo}/pull-requests/{n}/merge   # MergePullRequest
GET   /api/v1/git/{repo}/pull-requests/{n}/comments# AnalyzeReview (read)
PATCH /api/v1/git/{repo}/issues/{n}                # UpdateIssueStatus
```

The API endpoints:
1. **Authorize** that the tenant from `X-Tenant-Id` may act on `{repo}` — the **cross-tenant guard** (the load-bearing control; deny → key-free typed failure, never a platform call).
2. **Resolve the token** for that tenant: the tenant's BYOK PAT / installation token from the **Epic 29 cabinet** (keyed per-tenant via Epic 28) → else the platform-provided token where allowed — **tenant→system→error**, never empty/default (`feedback_resolution_no_empty_fallback`). The token is request-scoped and dropped after the call.
3. **Perform the platform call** via the existing `IGitHubIntegrationService` impl, **inside `Tamma.Api`** (the only place the token lives).
4. **Emit the audit event** from the API via the tenant `IEventRepository`.

The five activities collapse into **thin `TammaApiClient` clients** holding no token and no Octokit/vendor dependency. This is the same cutover shape as Story 32-5's thin `CallLlmInlineActivity`.

### Explicitly out of scope (referenced, not implemented here)

- **Class C — agent dispatch** (`DispatchAgentWorkflow` / `Monitor` / `CollectAgentResults`) → **Story 38-2**.
- **Class D — Slack/notifications** (`SlackActivity`) → **Story 38-3** (authored separately).
- **The build-time guardrail analyzer** (fail the build on a re-introduced direct git call / injected vendor service) → **Story 38-4** (authored separately) — this story leaves the regression net to 38-4 but proves the cutover by `grep`.
- **Inbound webhooks** (`workflow_run.completed` / `WebhookSignalRegistry`) are inbound, not an outbound effect — out of scope by nature (design §5.3); they belong to Story 38-2's exclusion list, not here.
- **Multi-platform fan-out** (GitLab/Gitea/Bitbucket/etc.) beyond the existing `IGitHubIntegrationService` abstraction — this story mediates the existing service; broadening the platform set is a separate concern.

## Acceptance Criteria

1. **The five endpoints exist.** `Tamma.Api/Endpoints/GitEndpoints.cs` serves `POST /api/v1/git/{repo}/branches`, `POST /api/v1/git/{repo}/pull-requests`, `PUT /api/v1/git/{repo}/pull-requests/{n}/merge`, `GET /api/v1/git/{repo}/pull-requests/{n}/comments`, and `PATCH /api/v1/git/{repo}/issues/{n}`. All are internal/engine-only, authenticated on the **same plane as `/llm/call`**: Bearer `Tamma:ApiToken` (via `TammaEngineAuthHandler`) + `X-Tenant-Id`. A missing/invalid bearer → **HTTP 401**. `{repo}` is bound from the route; the acting `tenantId` is derived from `X-Tenant-Id`.

2. **The cross-tenant guard runs first and is fail-closed.** Before any token resolution or platform call, the endpoint authorizes that the `X-Tenant-Id` tenant owns/may act on `{repo}` (via an `IGitRepoAuthorizer` over the tenant↔repo registry). A denied or unevaluable relationship → **HTTP 403** `REPO_NOT_AUTHORIZED` with a **key-free** body; the platform is **never** called and no token is resolved. Resolution is **tenant→system→error**, never a default/empty token (`feedback_resolution_no_empty_fallback`).

3. **Token resolution is per-tenant, BYOK→platform, request-scoped, never leaked.** The endpoint resolves the git token via the Epic 29 cabinet (the tenant's BYOK PAT / installation token, keyed per-tenant per Epic 28) → else the platform-provided token where allowed. The resolved token is set on the platform request, used for that one call, and dropped; it **NEVER** appears in any response body, log line, or DCB event. The decision stamps a `credentialSource` (`byok` | `platform`) onto the audit event and response — never the token.

4. **The platform call happens inside `Tamma.Api` only.** Each endpoint delegates to the existing `IGitHubIntegrationService` impl (Octokit etc.), which is DI-registered **in the API process** and is **removed from / never reachable in the engine**. The endpoint maps the request DTO → the service call → a normalized response DTO (`{ branchRef }` / `{ prNumber, prUrl }` / `{ merged, mergeSha }` / `{ comments[] }` / `{ issueNumber, status }`).

5. **The five activities become thin `TammaApiClient` clients.** `CreateBranchActivity`, `CreatePullRequestActivity`, `MergePullRequestActivity`, `UpdateIssueStatusActivity`, and `AnalyzeReviewActivity` no longer inject `IGitHubIntegrationService`. Each maps its `Input<>` props into a request record, calls a **new `TammaApiClient` method** (`CreateBranchAsync` / `CreatePullRequestAsync` / `MergePullRequestAsync` / `UpdateIssueStatusAsync` / `GetPullRequestCommentsAsync`) following the existing `PostAsync<T>`/`PatchAsync<T>` + `AddTenantHeader` + `RecordHealthAsync` pattern, and writes the **same workflow variables it writes today** (e.g. `BranchRef`, `PrNumber`, `MergeResult`, `IssueStatus`, `ReviewComments`) so the surrounding ADL workflows are unchanged. Each holds **no** token, no Octokit, and no platform HTTP.

6. **Error semantics — typed, key-free, fail-closed (mirrors 32-5 AC7).** The endpoints always return a typed, key-free body:
   - **HTTP 403** `REPO_NOT_AUTHORIZED` — the tenant↔repo guard denied (AC2). The platform is never called.
   - **HTTP 200 + `success:false`** for *expected platform failures* (e.g. branch already exists, PR not mergeable, 404 issue), with `platformStatusCode` preserved and a key-free `failureReason`, so the ADL workflow can branch on the outcome the way it does today. `failureCode ∈ { GIT_CONFLICT, NOT_MERGEABLE, NOT_FOUND, PLATFORM_ERROR }`.
   - **HTTP 401** — the engine bearer is absent/invalid.
   - **HTTP 503** `GIT_TOKEN_UNAVAILABLE` only when the credential genuinely cannot be resolved (fail-closed) — never call the platform with an empty token.
   A raw provider 5xx must never leak (it would null the `TammaApiClient` body and break the activity's outcome mapping).

7. **DCB audit from the API (exactly one terminal event).** Each endpoint emits exactly one terminal DCB event from `Tamma.Api` via the tenant `IEventRepository`, tagged `{ tenantId, repo, operation, credentialSource, correlationId }`; the event family is `GIT.<OPERATION>.SUCCESS|FAILED` (e.g. `GIT.BRANCH_CREATED.SUCCESS`, `GIT.PR_MERGED.SUCCESS`, `GIT.PR_MERGE.FAILED`, `GIT.ISSUE_UPDATED.SUCCESS`, `GIT.PR_COMMENTS_READ.SUCCESS`). `FAILED` events additionally tag `failureCode`. The event payload is **key-free** and references PR/issue numbers + repo, never the token or auth header.

8. **No control-plane table added.** This story adds **no** new control-plane table (it reuses the Epic 28/29 tenant↔repo registry + cabinet and the existing tenant `domain_events` stream). Therefore: **no** entry in `ElsaServer`/`Program.cs`'s startup-reset DROP list and **no** `ControlPlaneDbContextModelTests` edit. If a tenant↔repo mapping table proves necessary and lives in the control plane, it MUST be appended to the DROP list and the model contract test — call this out in the plan.

9. **Tests cover endpoints + guard + cutover.** Endpoint auth (401 missing bearer); the cross-tenant guard (403 `REPO_NOT_AUTHORIZED`, platform never called); BYOK vs platform `credentialSource`; each typed platform failure (`GIT_CONFLICT`, `NOT_MERGEABLE`, `NOT_FOUND`, `PLATFORM_ERROR` with preserved `platformStatusCode`); `GIT_TOKEN_UNAVAILABLE` fail-closed; the thin activities map responses to the same workflow variables; exactly one terminal DCB event per call; and a credential-safety assertion that the token never appears in any response/log/event. A `grep` over `Tamma.Activities` confirms **zero** `IGitHubIntegrationService` injections remain.

## Technical Design

### Where it lives

```
apps/tamma-elsa/src/Tamma.Api/Endpoints/
  GitEndpoints.cs                  # NEW — the 5 routes; engine-only auth; guard → token → service → audit

apps/tamma-elsa/src/Tamma.Api/Services/Git/
  IGitRepoAuthorizer.cs            # NEW — tenant↔repo cross-tenant guard
  GitRepoAuthorizer.cs             # NEW — checks the Epic 28/29 tenant↔repo registry
  IGitTokenResolver.cs             # NEW — BYOK→platform git token resolution (cabinet-backed)
  GitTokenResolver.cs              # NEW — Epic 29 cabinet → platform fallback; { Token, Source }
  GitMediationService.cs           # NEW — composes guard → token → IGitHubIntegrationService → audit
  GitRequests.cs / GitResponses.cs # NEW — wire records (CreateBranchRequest, MergePrRequest, … + DTOs)
  GitEventTypes.cs                 # NEW — GIT.BRANCH_CREATED.* / GIT.PR_MERGED.* / … constants

apps/tamma-elsa/src/Tamma.Activities/ADL/
  CreateBranchActivity.cs          # GUT — thin TammaApiClient client; drop IGitHubIntegrationService
  CreatePullRequestActivity.cs     # GUT — thin client
  MergePullRequestActivity.cs      # GUT — thin client
  UpdateIssueStatusActivity.cs     # GUT — thin client
  AnalyzeReviewActivity.cs         # GUT — thin client (read comments)

apps/tamma-elsa/src/Tamma.Api/Clients/   (wherever TammaApiClient lives)
  TammaApiClient.cs                # MODIFY — add CreateBranchAsync / CreatePullRequestAsync /
                                   #          MergePullRequestAsync / UpdateIssueStatusAsync /
                                   #          GetPullRequestCommentsAsync (PostAsync<T>/PatchAsync<T>
                                   #          + AddTenantHeader + RecordHealthAsync)

apps/tamma-elsa/src/Tamma.Api/Program.cs        # MODIFY — map GitEndpoints; register guard/token/service
apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs # MODIFY — ensure IGitHubIntegrationService is NOT
                                                #          registered/reachable in the engine
```

### The endpoint (`GitEndpoints.cs`)

```csharp
// All routes: internal, engine-only. Auth: Bearer Tamma:ApiToken (TammaEngineAuthHandler) + X-Tenant-Id.
app.MapPost("/api/v1/git/{repo}/branches", async (
        string repo, CreateBranchRequest body, HttpContext http,
        IGitMediationService git, CancellationToken ct) =>
{
    var tenantId = ResolveTenant(http);                  // from X-Tenant-Id; 401 already enforced by the scheme
    var result   = await git.CreateBranchAsync(tenantId, repo, body, ct);
    return result.ToHttpResult();                         // 200 success | 200 success:false | 403 | 503
})
.RequireAuthorization(EngineAuthPolicy)
.WithName("CreateBranch");

// PUT /api/v1/git/{repo}/pull-requests/{n}/merge — the highest-risk write
app.MapPut("/api/v1/git/{repo}/pull-requests/{n:int}/merge", async (
        string repo, int n, MergePrRequest body, HttpContext http,
        IGitMediationService git, CancellationToken ct) =>
{
    var tenantId = ResolveTenant(http);
    var result   = await git.MergePullRequestAsync(tenantId, repo, n, body, ct);
    return result.ToHttpResult();
})
.RequireAuthorization(EngineAuthPolicy)
.WithName("MergePullRequest");
// … POST pull-requests, GET pull-requests/{n}/comments, PATCH issues/{n} likewise.
```

### `GitMediationService.CreateBranchAsync` composition (inside `Tamma.Api`)

```
1. authz = await _authorizer.AuthorizeAsync(tenantId, repo, ct)          // CROSS-TENANT GUARD, first
             -> denied/unevaluable => 403 REPO_NOT_AUTHORIZED  (platform NEVER called, no token resolved)
2. cred  = await _tokenResolver.ResolveAsync(tenantId, repo, ct)         // Epic 29 cabinet BYOK -> platform
             -> { Token, Source }; null => 503 GIT_TOKEN_UNAVAILABLE (fail-closed, retryable:false)
3. res   = await _github.CreateBranchAsync(repo, body, cred.Token, ct)   // IGitHubIntegrationService, request-scoped token
             -> platform error => 200 success:false { failureCode, platformStatusCode preserved }
4. emit GIT.BRANCH_CREATED.SUCCESS|FAILED  { tenantId, repo, operation, credentialSource, correlationId }
5. return GitMediationResult -> { success, branchRef?, credentialSource, failureCode?, platformStatusCode? }
```

The token (`cred.Token`) is passed to the service call and dropped; it is never logged, returned, or persisted. `credentialSource` (the label) is safe to surface; the token is not.

### Wire records (`GitRequests.cs` / `GitResponses.cs`)

```csharp
public sealed record CreateBranchRequest   { public required string BranchName { get; init; }
                                              public required string BaseRef { get; init; }
                                              public required string CorrelationId { get; init; } }
public sealed record CreatePullRequestRequest { public required string HeadRef { get; init; }
                                                public required string BaseRef { get; init; }
                                                public required string Title { get; init; }
                                                public string? Body { get; init; }
                                                public required string CorrelationId { get; init; } }
public sealed record MergePrRequest         { public string? MergeMethod { get; init; }  // merge|squash|rebase
                                              public string? CommitMessage { get; init; }
                                              public required string CorrelationId { get; init; } }
public sealed record UpdateIssueRequest     { public string? Status { get; init; }       // open|closed
                                              public IReadOnlyList<string>? Labels { get; init; }
                                              public required string CorrelationId { get; init; } }

public sealed record GitMediationResult
{
    public required bool Success { get; init; }
    public string? CredentialSource { get; init; }       // "byok" | "platform" — NEVER the token
    public string? BranchRef { get; init; }
    public int? PrNumber { get; init; }
    public string? PrUrl { get; init; }
    public bool? Merged { get; init; }
    public string? MergeSha { get; init; }
    public IReadOnlyList<PrCommentDto>? Comments { get; init; }
    public string? IssueStatus { get; init; }
    // failure-only:
    public string? FailureCode { get; init; }            // GIT_CONFLICT | NOT_MERGEABLE | NOT_FOUND | PLATFORM_ERROR
    public string? FailureReason { get; init; }          // key-free
    public int? PlatformStatusCode { get; init; }        // preserved
}
```

### The thin `MergePullRequestActivity` shim (the cutover shape)

```csharp
// no IGitHubIntegrationService; no Octokit; no platform HTTP. Same workflow variables out.
var req = new MergePrRequest {
    MergeMethod = mergeMethod, CommitMessage = commitMessage,
    CorrelationId = context.WorkflowExecutionContext.Id
};
var resp = await _api.MergePullRequestAsync(repo, prNumber, req, tenantId, ct);   // NEW client method

context.SetVariable("MergeResult", new MergeResultVar {
    Merged = resp.Merged ?? false, MergeSha = resp.MergeSha,
    Success = resp.Success, FailureCode = resp.FailureCode });
```

The surrounding ADL workflow reads `MergeResult` exactly as today (AC5).

## Dependencies

**Internal (hard prerequisites):**

- **Story 32-5** (the `/llm/call` template) — the endpoint shape, `TammaApiClient` cutover convention, request-scoped-credential discipline, and DCB-audit-from-API contract this story mirrors. Not a code dependency, a *pattern* dependency; settle 32-5's pattern first.
- **Epic 28** (tenancy) — the tenant↔repo registry data + per-tenant keying for the cross-tenant guard and token resolution; `ITenantContext`.
- **Epic 29** (secret cabinet) — encrypted per-tenant git PAT / installation token (BYOK), resolved BYOK→platform inside the API (the git analogue of Story 32-3's LLM credential resolver).
- **Epic 9** (unified agent API) — the `TammaApiClient` / `TammaEngineAuthHandler` engine↔API callback convention reused by `GitEndpoints`.
- **Epic 4** (DCB) — `DomainEvent` / `IEventRepository`, tenant-scoped, for the `GIT.*` audit events.
- **`IGitHubIntegrationService`** — the existing in-API platform service the endpoints delegate to (now API-only).

**Consumers (downstream, not blockers):**

- **Story 38-4** (guardrail analyzer) — proves this cutover stays cut (zero `IGitHubIntegrationService` injections in the engine).
- The ADL workflows that already consume `BranchRef` / `PrNumber` / `MergeResult` / `IssueStatus` / `ReviewComments` — unchanged.

**Follow-ons (referenced, separate stories):** 38-2 (agent-dispatch mediation), 38-3 (Slack/notifications mediation), 38-4 (build-time guardrail).

**External:** none new (reuses the existing Octokit-backed `IGitHubIntegrationService` — now only in the API process).

## Testing Strategy

1. **Endpoint auth.** Missing/invalid bearer → 401; valid bearer + `X-Tenant-Id` → request bound, `tenantId` derived from header, `{repo}` from route.
2. **Cross-tenant guard (AC2).** A tenant requesting a repo it does not own → **403 `REPO_NOT_AUTHORIZED`**; assert `IGitHubIntegrationService` is **never** invoked and no token is resolved (fakes record calls).
3. **BYOK vs platform `credentialSource` (AC3).** Cabinet has a tenant PAT → `credentialSource="byok"`; absent → platform token → `"platform"`; both reach the service with a non-empty token; the token never appears in the response/log/event.
4. **`GIT_TOKEN_UNAVAILABLE` fail-closed (AC6).** Token resolver returns null → **503**, `retryable:false`; the platform is never called.
5. **Typed platform failures (AC6).** `IGitHubIntegrationService` reports branch-exists / not-mergeable / 404 / 5xx → **200 `success:false`** with the right `failureCode` and **preserved** `platformStatusCode`; assert a raw 5xx is never produced.
6. **Each operation happy path (AC4).** Branch/PR/merge/issue/comments → correct response DTO populated; exactly one terminal `GIT.*.SUCCESS` DCB event with the right tags.
7. **Thin-activity mapping (AC5).** Given each `GitMediationResult`, the activity writes the same `BranchRef` / `PrNumber` / `MergeResult` / `IssueStatus` / `ReviewComments` workflow variables as today; a minimal ADL workflow branches on them unchanged.
8. **Cutover proof (AC5/AC9).** `grep` over `Tamma.Activities` for `IGitHubIntegrationService` → **zero** injections; the five activities hold no Octokit/platform-HTTP reference.
9. **Credential safety (AC3/AC7).** Assert the token never appears in any `GitMediationResult`, response body, log line, or DCB event payload.
10. **Audit invariant (AC7).** Exactly one terminal `GIT.*` event per call (success or failure) via a fake `IEventRepository`; tags match AC7; `FAILED` carries `failureCode`.

Docker-bound C# suites run via `sg docker -c "dotnet test apps/tamma-elsa/..."` (session docker group is stale; plain `dotnet build` needs no wrapper).

## Estimated Effort

5-6 days (five endpoints + the cross-tenant guard + BYOK→platform token resolver + the five thin-activity cutovers + the audit wiring + the engine-registration removal).

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/GitEndpoints.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Git/IGitRepoAuthorizer.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Git/GitRepoAuthorizer.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Git/IGitTokenResolver.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Git/GitTokenResolver.cs` | Create (Epic 29 cabinet → platform) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Git/IGitMediationService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Git/GitMediationService.cs` | Create (guard→token→service→audit) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Git/GitRequests.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/Git/GitResponses.cs` | Create (+ `PrCommentDto`, `GitMediationResult`) |
| `apps/tamma-elsa/src/Tamma.Api/Services/Git/GitEventTypes.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Activities/ADL/CreateBranchActivity.cs` | Gut → thin client |
| `apps/tamma-elsa/src/Tamma.Activities/ADL/CreatePullRequestActivity.cs` | Gut → thin client |
| `apps/tamma-elsa/src/Tamma.Activities/ADL/MergePullRequestActivity.cs` | Gut → thin client |
| `apps/tamma-elsa/src/Tamma.Activities/ADL/UpdateIssueStatusActivity.cs` | Gut → thin client |
| `apps/tamma-elsa/src/Tamma.Activities/ADL/AnalyzeReviewActivity.cs` | Gut → thin client |
| `apps/tamma-elsa/src/Tamma.Api/Clients/TammaApiClient.cs` | Modify (add 5 git client methods) |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (map `GitEndpoints`; register guard/token/mediation; `IGitHubIntegrationService` stays API-only) |
| `apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs` | Modify (ensure `IGitHubIntegrationService` not registered/reachable in the engine) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Endpoints/GitEndpointsTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Git/GitMediationServiceTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/ADL/GitActivityThinClientTests.cs` | Create |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
2. Searched `.dev/` for related spikes, bugs, findings, and decisions (esp. `feedback_resolution_no_empty_fallback`).
3. Read the design of record §1 (steps never call external APIs; §1.2 audit table — the five Class-A rows; §1.3 cross-tenant-token risk) and §5 (§5.1 Class-A endpoints; §5.3 webhooks are out of scope) IN FULL.
4. Read **Story 32-5** as the template (`LlmCallEndpoints` shape, the thin `CallLlmInlineActivity` cutover, request-scoped-credential discipline, DCB-audit-from-API), and reviewed `TammaApiClient` (the `PostAsync<T>`/`AddTenantHeader`/`RecordHealthAsync` pattern) + `IGitHubIntegrationService` (the service you delegate to, now API-only).
5. Confirmed the Epic 28 tenant↔repo registry + Epic 29 cabinet contracts you authorize/resolve against are landed (or code to their interfaces with fakes).
6. Planned the TDD approach; remember the cross-tenant guard MUST run **before** token resolution and platform call.

### Key Design Decisions

- **Mirror `/llm/call`, do not invent.** Same auth plane, same engine-only policy, same request-scoped-credential discipline, same DCB-audit-from-API contract as Story 32-5. The activity is a dumb shim.
- **The cross-tenant guard is the load-bearing control (design §1.3).** "A mis-scoped platform token = cross-tenant write/merge." Authorize the `tenant ↔ repo` relationship FIRST; deny → 403 `REPO_NOT_AUTHORIZED`, platform never called, no token resolved. Fail-closed.
- **Fail-closed, never empty (`feedback_resolution_no_empty_fallback`).** Token resolution is tenant→system→error; an unresolvable token → 503 `GIT_TOKEN_UNAVAILABLE`, never a call with an empty/default token.
- **Status preservation for expected failures.** Platform failures (branch-exists, not-mergeable, 404) return **200 `success:false` + preserved `platformStatusCode`**, never a raw 5xx, so the ADL workflow branches on the outcome the way it does today (the same load-bearing discipline as 32-5 AC7).
- **DCB audit from the API.** Emitted where the tenant `IEventRepository` + cabinet live, not from the engine. Performance/action data is ALWAYS tenant-scoped (Epic 32 ownership rule); the `GIT.*` audit lands in the tenant's `t_<hex>` store.
- **No new control-plane table (AC8).** Reuses the Epic 28/29 tenant↔repo registry + cabinet + the tenant `domain_events` stream. No DROP-list entry, no `ControlPlaneDbContextModelTests` edit. *If* a CP-resident tenant↔repo mapping table is unavoidable, append it to `Program.cs`'s startup-reset "Wiping Tamma-managed public-schema tables" DROP list AND the strict `Model_Has_ExpectedControlPlaneEntities` contract test — flagged in the plan.
- **`PlatformOwnerAccess`, not `OwnerAccess`** — these are engine-callback routes (engine bearer), not `/api/admin/*`; no platform-owner policy applies. The relevant control is the per-request tenant↔repo guard, not RBAC.

### Per-mode ownership (mandatory two-scoping-model answer, per CLAUDE.md)

| Question | single-user mode | SaaS mode |
|---|---|---|
| Who is the principal of a git-mediation request? | The sole user (keyed by `UserId`; `TenantId` may be null). | The tenant (keyed by `TenantId` from `X-Tenant-Id`). No per-user layer. |
| Who may act on `{repo}`? | The sole user — the guard verifies the user owns the configured repo(s). | Only the tenant whose `X-Tenant-Id` owns `{repo}` in the tenant↔repo registry; a cross-tenant request → 403 `REPO_NOT_AUTHORIZED`. |
| Whose git token does the call use? | The sole user's BYOK PAT / installation token → else platform default; resolved in the API. | The tenant's BYOK token (Epic 29 cabinet, keyed by `TenantId`) → else platform-provided (where allowed). `credentialSource` records which. |
| Where do `GIT.*` audit events land? | The user's (sole) tenant event store. | The tenant's `t_<hex>` event store via the tenant-scoped `IEventRepository`; `TenantId` set. Never cross-tenant. |
| Who owns the git operation's audit data? | The user. | The tenant — platform admin sees none of it (Epic 32 ownership rule). |
| Mode source | `ITammaModeProvider` (`TammaMode.cs`) — process-stable. | same |

### Risks and Mitigations

| Risk | Severity | Mitigation |
|------|----------|------------|
| **Mis-scoped token → cross-tenant write/merge** (design §1.3 — THE Class-A risk) | Critical | The `IGitRepoAuthorizer` cross-tenant guard runs FIRST, before any token resolution or platform call; deny → 403 `REPO_NOT_AUTHORIZED`; dedicated cross-tenant test asserting the platform is never reached; token is per-tenant from the cabinet, never a shared default. |
| Endpoint returns a raw 5xx → ADL outcome mapping silently breaks | High | The result mapper always returns 200 `success:false` + preserved `platformStatusCode` for expected platform failures; 403/401/503 only for guard/auth/credential; HTTP-status-fidelity test. |
| Engine still holds a git token after cutover | High | `IGitHubIntegrationService` stays API-only; `grep` `Tamma.Activities` for `IGitHubIntegrationService` / Octokit / `api.github.com` → zero; 38-4 guardrail makes it permanent. |
| Token leaks into a log / response / event | High | Token is request-scoped, dropped after the call; `credentialSource` is the only credential field surfaced; explicit credential-safety test over response/log/event. |
| Thin activity writes different variables → ADL workflow breaks | High | Map each `GitMediationResult` to the exact `BranchRef`/`PrNumber`/`MergeResult`/`IssueStatus`/`ReviewComments` shapes; minimal ADL workflow integration test. |
| Empty/default token fallback on resolution failure | Medium | Fail-closed: 503 `GIT_TOKEN_UNAVAILABLE`, `retryable:false`; never call with an empty token (`feedback_resolution_no_empty_fallback`). |
| Co-hosting hides the violation (design §1.1) | Medium | The activity calls over the wire via `TammaApiClient`, never resolves an injected vendor service — verified the moment the engine runs as per-tenant dedicated compute (Cranl). |
| Depends on 32-5 pattern / Epic 28-29 registry+cabinet not yet settled | Medium | Code to the interfaces; mirror 32-5; use fakes in tests until they land. |

### Success Metrics

- [ ] `grep` over `Tamma.Activities` finds **zero** `IGitHubIntegrationService` injections (all five activities cut over).
- [ ] Every git-mediation request authorizes the `tenant ↔ repo` relationship before resolving a token; a cross-tenant request is 403'd and never reaches the platform (isolation test).
- [ ] 100% of mediated git calls emit exactly one terminal `GIT.*` event from `Tamma.Api`, tagged `{ tenantId, repo, operation, credentialSource, correlationId }`.
- [ ] The git token never appears in any response body, log line, or DCB event payload (credential-safety test green).
- [ ] The five ADL activities hold no Octokit/platform-HTTP reference; the surrounding ADL workflows are unchanged.

## Related

- Design of record: `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` (§1 steps-never-call-external-APIs; §1.2 audit table — Class-A rows; §1.3 cross-tenant-token risk; §5.1 Class-A endpoints; §5.3 webhooks out of scope)
- Epic 38 README: `docs/stories/epic-38/README.md`
- Template story: `docs/stories/epic-32/story-32-5/32-5-managed-agent-execution-layer.md` (the `/llm/call` mediation this mirrors)
- Implementation plan: `docs/superpowers/plans/2026-06-21-38-1-git-platform-step-mediation-plan.md`
- Sibling stories: `story-38-2/` (agent-dispatch mediation), `story-38-3/` (Slack/notifications mediation), `story-38-4/` (build-time guardrail analyzer)
- Cross-epic: `docs/stories/epic-32/story-32-3/` (the LLM BYOK→platform credential resolver this story's git token resolver mirrors); Epic 28 (tenancy / tenant↔repo registry); Epic 29 (secret cabinet — per-tenant git tokens)
- Reused code: `IGitHubIntegrationService` (now API-only), `TammaApiClient`, the ADL workflows consuming `BranchRef`/`PrNumber`/`MergeResult`/`IssueStatus`/`ReviewComments`

## Logging Requirements

- **INFO**: git-mediation received (correlationId, repo, operation, tenantId — never the token); authorization decision (allow/deny); operation completed (success, operation, prNumber/issueNumber, durationMs, credentialSource).
- **DEBUG**: composition step boundaries (guard → token → service → audit); request DTO shape (no token).
- **WARN**: typed failure paths (`REPO_NOT_AUTHORIZED`, `GIT_CONFLICT`, `NOT_MERGEABLE`, `NOT_FOUND`, `PLATFORM_ERROR` + `platformStatusCode`, `GIT_TOKEN_UNAVAILABLE`) with `failureCode` + `correlationId`.
- **ERROR**: contract violations (null body), DCB append failure (the call still returns its result; the append failure is logged, not swallowed), and any attempt to return a raw 5xx (guardrail).
- **Structured context**: `{ tenantId, repo, operation, correlationId, credentialSource }` where applicable.
- **Credential safety (LOAD-BEARING)**: NEVER log, return, or persist the resolved git PAT / installation token or any `Authorization` header. `credentialSource` (the label `byok`/`platform`) is safe; the token is not. The `GitMediationResult` body, all DCB event payloads, and the audit trail are token-free by contract — mirroring Story 32-5's credential-safety rule for LLM keys.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-21 | 1.0.0   | Initial story creation — Class-A git-platform step mediation. Re-points the five ADL git activities (`CreateBranch`/`CreatePullRequest`/`MergePullRequest`/`UpdateIssueStatus`/`AnalyzeReview`) from the co-hosted `IGitHubIntegrationService` to new `Tamma.Api` `/api/v1/git/{repo}/...` endpoints that hold the per-tenant token (Epic 28/29 cabinet, BYOK→platform), authorize the `tenant ↔ repo` relationship (the cross-tenant guard — the high-blast-radius control), perform the call, and audit it. Activities become thin `TammaApiClient` clients holding no token. Mirrors the Story 32-5 `/llm/call` template. | Claude |
