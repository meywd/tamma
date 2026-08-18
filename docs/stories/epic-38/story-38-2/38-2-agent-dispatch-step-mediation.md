# Story 38-2: Agent-dispatch step mediation (Class C)

Status: done
<!-- Flipped drafted -> done 2026-08-18. The deliverable named in the acceptance criteria
     was located in apps/tamma-elsa/src (and its suites in apps/tamma-elsa/tests) before this
     header was changed — not taken from a changelog. The per-story evidence is recorded
     inline on this story's line in docs/sprint-status.yaml.
-->

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)

## User Story

As a **platform engineer running the Tamma engine as per-tenant dedicated compute (the Cranl path)**,
I want the agent-dispatch steps (`DispatchAgentWorkflowActivity` / `GitHubActionsExecutor`, `MonitorAgentWorkflowActivity`, `CollectAgentResultsActivity`) to stop resolving the co-hosted `IGitHubActionsClient` and instead call internal `Tamma.Api` agent-dispatch endpoints that hold the GitHub Actions token, authorize that *this* tenant may dispatch into *this* repo, dispatch/poll the run, and audit it,
So that **a workflow step never holds a GitHub Actions token in the engine process** — closing the Class-C rule-1 violation the same way Story 38-1 closed the git-platform writes and Story 32-5's `/llm/call` closed the LLM keys.

## Priority

P1 — Class C is a high-blast-radius non-LLM violator alongside Class A (design §1.3): "high the moment the engine is not co-hosted with `Tamma.Api`: a mis-scoped platform token = cross-tenant write/merge." Agent dispatch triggers GitHub Actions **workflow runs** in a repo — a cross-tenant dispatch is a cross-tenant code execution. It is Phase 1 of Epic 38 with Story 38-1, mirrors the same `/llm/call` template, and reuses the cross-tenant guard 38-1 introduces.

## Context

### What exists today (the violation — design §1.2)

Three AgentDispatch activities reach GitHub Actions through **`IGitHubActionsClient`** resolved from the DI container:

| Activity | Operation | Notes |
|---|---|---|
| `AgentDispatch/DispatchAgentWorkflowActivity` (via `GitHubActionsExecutor`) | trigger an Actions workflow run | **write — triggers code execution** |
| `AgentDispatch/MonitorAgentWorkflowActivity` | poll a run's status | read |
| `AgentDispatch/CollectAgentResultsActivity` | fetch a completed run's outputs/artifacts | read |

`IGitHubActionsClient` has two impls: **`OctokitGitHubActionsClient` in `Tamma.Api`** (holds the Actions token) and **`NullGitHubActionsClient` in the engine** (no-op). So today the dispatch only does real work when the engine and the API are **co-hosted** — and would silently no-op (or, worse, need a token pushed in) the moment the engine is per-tenant dedicated compute. Per design §1.1, *co-hosting is not compliance*: the step must call an internal endpoint **over the wire**, not resolve an injected vendor service.

### The inbound side is explicitly OUT of scope (design §5.3)

`DispatchAgentWorkflowActivity` suspends the workflow on a durable bookmark; when the GitHub `workflow_run.completed` webhook arrives, **`Tamma.Api` receives it, verifies the signature, and signals the engine in-process** via `WebhookSignalRegistry`. This inbound path is **NOT a violator** and is **NOT mediated by this story**: it is *inbound* — there is no outbound external call to centralize. The signature-verification secret already lives in the API. Per design §5.3, inbound webhooks "received by `Tamma.Api`, signalled to the engine in-process — no outbound external call to mediate. Signature verification + secret stay in the API. Out of scope by nature." This story mediates only the **outbound** dispatch + poll + collect.

### What this story does (mirror `/llm/call` — design §5.1 Class C)

Re-point the outbound activities to new `Tamma.Api` agent-dispatch endpoints (design §5.1):

```
POST /api/v1/agent-dispatch/{repo}/runs        # DispatchAgentWorkflow — trigger a run
GET  /api/v1/agent-dispatch/{repo}/runs/{id}   # Monitor (status) + CollectAgentResults (outputs)
```

The API endpoints:
1. **Authorize** that the tenant from `X-Tenant-Id` may dispatch into / read `{repo}` — the **cross-tenant guard** (reuses Story 38-1's `IGitRepoAuthorizer`; deny → key-free typed failure, never a platform call).
2. **Resolve the Actions token** for that tenant: the tenant's BYOK token from the **Epic 29 cabinet** (keyed per-tenant via Epic 28) → else the platform-provided token where allowed — **tenant→system→error**, never empty/default (`feedback_resolution_no_empty_fallback`). Request-scoped, dropped after the call.
3. **Perform the dispatch/poll** via the existing `IGitHubActionsClient` (`OctokitGitHubActionsClient`), **inside `Tamma.Api`** only.
4. **Emit the audit event** from the API via the tenant `IEventRepository`.

The three activities collapse into **thin `TammaApiClient` clients** holding no token and no Octokit/vendor dependency — the same cutover shape as Story 32-5 and Story 38-1.

### Explicitly out of scope (referenced, not implemented here)

- **The inbound `workflow_run.completed` webhook + `WebhookSignalRegistry`** — inbound, not an outbound effect; out of scope by nature (design §5.3). This story does **not** touch the bookmark/signal path; `DispatchAgentWorkflowActivity` still suspends and is still signalled in-process.
- **Class A — git platform** (`CreateBranch`/`CreatePullRequest`/`MergePullRequest`/`UpdateIssueStatus`/`AnalyzeReview`) → **Story 38-1** (this story reuses 38-1's `IGitRepoAuthorizer`).
- **Class D — Slack/notifications** (`SlackActivity`) → **Story 38-3** (authored separately).
- **The build-time guardrail analyzer** → **Story 38-4** (authored separately) — this story proves its cutover by `grep`.
- **A new CI dispatch backend** beyond the existing `IGitHubActionsClient` abstraction — this story mediates the existing client; broadening the CI backend set is a separate concern.

## Acceptance Criteria

1. **The two endpoints exist.** `Tamma.Api/Endpoints/AgentDispatchEndpoints.cs` serves `POST /api/v1/agent-dispatch/{repo}/runs` and `GET /api/v1/agent-dispatch/{repo}/runs/{id}`. Both are internal/engine-only, authenticated on the **same plane as `/llm/call`**: Bearer `Tamma:ApiToken` (via `TammaEngineAuthHandler`) + `X-Tenant-Id`. A missing/invalid bearer → **HTTP 401**. `{repo}` and `{id}` bind from the route; the acting `tenantId` is derived from `X-Tenant-Id`.

2. **The cross-tenant guard runs first and is fail-closed.** Before any token resolution or platform call, the endpoint authorizes that the `X-Tenant-Id` tenant may dispatch into / read `{repo}` (via the **shared `IGitRepoAuthorizer` from Story 38-1**). A denied or unevaluable relationship → **HTTP 403** `REPO_NOT_AUTHORIZED` with a **key-free** body; the platform is **never** called and no token is resolved. Resolution is **tenant→system→error**, never a default/empty token.

3. **Token resolution is per-tenant, BYOK→platform, request-scoped, never leaked.** The endpoint resolves the GitHub Actions token via the Epic 29 cabinet (the tenant's BYOK token, keyed per-tenant per Epic 28) → else the platform-provided token where allowed. The resolved token is set on the dispatch/poll request, used for that one call, and dropped; it **NEVER** appears in any response body, log line, or DCB event. The decision stamps a `credentialSource` (`byok` | `platform`) — never the token.

4. **The dispatch/poll happens inside `Tamma.Api` only.** Each endpoint delegates to the existing `IGitHubActionsClient` impl (`OctokitGitHubActionsClient`), DI-registered **in the API process** and **removed from / never reachable in the engine** (the engine's `NullGitHubActionsClient` registration is removed). `POST .../runs` maps `{ workflowRef, ref, inputs }` → a triggered run → `{ runId, runUrl, status }`; `GET .../runs/{id}` maps the run id → `{ runId, status, conclusion, outputs, artifacts }`.

5. **The three activities become thin `TammaApiClient` clients.** `DispatchAgentWorkflowActivity` (`GitHubActionsExecutor`), `MonitorAgentWorkflowActivity`, and `CollectAgentResultsActivity` no longer inject `IGitHubActionsClient`. Each maps its `Input<>` props into a request record, calls a **new `TammaApiClient` method** (`DispatchAgentRunAsync` / `GetAgentRunAsync`) following the existing `PostAsync<T>`/`GetAsync<T>` + `AddTenantHeader` + `RecordHealthAsync` pattern, and writes the **same workflow variables it writes today** (e.g. `DispatchedRunId`, `RunStatus`, `AgentResults`) so the surrounding dispatch workflow — **including the durable bookmark suspend/resume on `workflow_run.completed`** — is unchanged. Each holds **no** token, no Octokit, and no platform HTTP.

6. **Error semantics — typed, key-free, fail-closed (mirrors 32-5 AC7 / 38-1 AC6).** The endpoints always return a typed, key-free body:
   - **HTTP 403** `REPO_NOT_AUTHORIZED` — the tenant↔repo guard denied (AC2). The platform is never called.
   - **HTTP 200 + `success:false`** for *expected platform failures* (e.g. workflow not found, run not found, dispatch rejected), with `platformStatusCode` preserved and a key-free `failureReason`, so the dispatch workflow can branch on the outcome the way it does today. `failureCode ∈ { WORKFLOW_NOT_FOUND, RUN_NOT_FOUND, DISPATCH_REJECTED, PLATFORM_ERROR }`.
   - **HTTP 401** — the engine bearer is absent/invalid.
   - **HTTP 503** `ACTIONS_TOKEN_UNAVAILABLE` only when the credential genuinely cannot be resolved (fail-closed) — never call the platform with an empty token.
   A raw provider 5xx must never leak (it would null the `TammaApiClient` body and break the activity's outcome mapping + the durable bookmark contract).

7. **DCB audit from the API (exactly one terminal event).** Each endpoint emits exactly one terminal DCB event from `Tamma.Api` via the tenant `IEventRepository`, tagged `{ tenantId, repo, operation, runId?, credentialSource, correlationId }`; the event family is `AGENT_DISPATCH.RUN_TRIGGERED.SUCCESS|FAILED` and `AGENT_DISPATCH.RUN_POLLED.SUCCESS|FAILED`. `FAILED` events additionally tag `failureCode`. The event payload is **key-free** and references run id + repo, never the token. (This is distinct from any inbound `WebhookSignalRegistry` signalling, which this story leaves unchanged.)

8. **No control-plane table added.** This story adds **no** new control-plane table (it reuses the Epic 28/29 tenant↔repo registry + cabinet and the existing tenant `domain_events` stream). Therefore: **no** entry in `Program.cs`'s startup-reset DROP list and **no** `ControlPlaneDbContextModelTests` edit. (If 38-1's tenant↔repo data lives in a CP table, that table is owned by 38-1, not duplicated here.)

9. **Tests cover endpoints + guard + cutover + the inbound-unchanged invariant.** Endpoint auth (401 missing bearer); the cross-tenant guard (403 `REPO_NOT_AUTHORIZED`, platform never called); BYOK vs platform `credentialSource`; each typed platform failure (`WORKFLOW_NOT_FOUND`, `RUN_NOT_FOUND`, `DISPATCH_REJECTED`, `PLATFORM_ERROR` with preserved `platformStatusCode`); `ACTIONS_TOKEN_UNAVAILABLE` fail-closed; the thin activities map responses to the same workflow variables; exactly one terminal DCB event per call; the token never appears in any response/log/event; and the **inbound `workflow_run.completed` / `WebhookSignalRegistry` bookmark suspend/resume is unchanged** (an integration test proves dispatch → suspend → in-process signal → resume still works). A `grep` over `Tamma.Activities` confirms **zero** `IGitHubActionsClient` injections remain.

## Technical Design

### Where it lives

```
apps/tamma-elsa/src/Tamma.Api/Endpoints/
  AgentDispatchEndpoints.cs        # NEW — POST .../runs + GET .../runs/{id}; engine-only auth

apps/tamma-elsa/src/Tamma.Api/Services/AgentDispatch/
  IAgentDispatchMediationService.cs# NEW — composes guard → token → IGitHubActionsClient → audit
  AgentDispatchMediationService.cs # NEW
  IActionsTokenResolver.cs         # NEW — BYOK→platform Actions token resolution (cabinet-backed)
  ActionsTokenResolver.cs          # NEW — Epic 29 cabinet → platform fallback; { Token, Source }
  AgentDispatchRequests.cs         # NEW — DispatchRunRequest (+ DTOs)
  AgentDispatchResponses.cs        # NEW — AgentRunResult (run id/status/outputs) — NOTE: not the LLM AgentRunResult
  AgentDispatchEventTypes.cs       # NEW — AGENT_DISPATCH.RUN_TRIGGERED.* / RUN_POLLED.* constants
  # reuses Story 38-1's Tamma.Api/Services/Git/IGitRepoAuthorizer for the cross-tenant guard

apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/
  DispatchAgentWorkflowActivity.cs # GUT — thin TammaApiClient client; drop IGitHubActionsClient
  GitHubActionsExecutor.cs         # GUT/REMOVE — replaced by the thin client call
  MonitorAgentWorkflowActivity.cs  # GUT — thin client (GET run)
  CollectAgentResultsActivity.cs   # GUT — thin client (GET run outputs)
  WebhookSignalRegistry.cs         # UNCHANGED — inbound, out of scope (design §5.3)

apps/tamma-elsa/src/Tamma.Api/Clients/   (wherever TammaApiClient lives)
  TammaApiClient.cs                # MODIFY — add DispatchAgentRunAsync / GetAgentRunAsync
                                   #          (PostAsync<T>/GetAsync<T> + AddTenantHeader + RecordHealthAsync)

apps/tamma-elsa/src/Tamma.Api/Program.cs        # MODIFY — map AgentDispatchEndpoints; register service/token
apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs # MODIFY — remove NullGitHubActionsClient registration
```

### The endpoint (`AgentDispatchEndpoints.cs`)

```csharp
// Internal, engine-only. Auth: Bearer Tamma:ApiToken (TammaEngineAuthHandler) + X-Tenant-Id.
app.MapPost("/api/v1/agent-dispatch/{repo}/runs", async (
        string repo, DispatchRunRequest body, HttpContext http,
        IAgentDispatchMediationService dispatch, CancellationToken ct) =>
{
    var tenantId = ResolveTenant(http);                  // from X-Tenant-Id; 401 enforced by the scheme
    var result   = await dispatch.TriggerRunAsync(tenantId, repo, body, ct);
    return result.ToHttpResult();                         // 200 success | 200 success:false | 403 | 503
})
.RequireAuthorization(EngineAuthPolicy)
.WithName("DispatchAgentRun");

app.MapGet("/api/v1/agent-dispatch/{repo}/runs/{id}", async (
        string repo, string id, HttpContext http,
        IAgentDispatchMediationService dispatch, CancellationToken ct) =>
{
    var tenantId = ResolveTenant(http);
    var result   = await dispatch.GetRunAsync(tenantId, repo, id, ct);
    return result.ToHttpResult();
})
.RequireAuthorization(EngineAuthPolicy)
.WithName("GetAgentRun");
```

### `AgentDispatchMediationService.TriggerRunAsync` composition (inside `Tamma.Api`)

```
1. authz = await _authorizer.AuthorizeAsync(tenantId, repo, ct)             // SHARED 38-1 cross-tenant guard, FIRST
             -> denied/unevaluable => 403 REPO_NOT_AUTHORIZED  (platform NEVER called, no token resolved)
2. cred  = await _tokenResolver.ResolveAsync(tenantId, repo, ct)            // Epic 29 cabinet BYOK -> platform
             -> { Token, Source }; null => 503 ACTIONS_TOKEN_UNAVAILABLE (fail-closed, retryable:false)
3. run   = await _actions.DispatchAsync(repo, body, cred.Token, ct)         // IGitHubActionsClient, request-scoped token
             -> platform error => 200 success:false { failureCode, platformStatusCode preserved }
4. emit AGENT_DISPATCH.RUN_TRIGGERED.SUCCESS|FAILED  { tenantId, repo, operation, runId, credentialSource, correlationId }
5. return AgentDispatchResult -> { success, runId?, runUrl?, status?, credentialSource, failureCode?, platformStatusCode? }
```

`GetRunAsync` is the same shape with `_actions.GetRunAsync(...)` and `AGENT_DISPATCH.RUN_POLLED.*`. The token is request-scoped, dropped after the call; never logged, returned, or persisted.

### Wire records (`AgentDispatchRequests.cs` / `AgentDispatchResponses.cs`)

```csharp
public sealed record DispatchRunRequest
{
    public required string WorkflowRef { get; init; }     // workflow file / id to dispatch
    public required string Ref { get; init; }             // branch/tag/sha to run against
    public Dictionary<string, string> Inputs { get; init; } = new();
    public required string CorrelationId { get; init; }   // workflow instance id — ties to bookmark + audit
}

public sealed record AgentDispatchResult
{
    public required bool Success { get; init; }
    public string? CredentialSource { get; init; }        // "byok" | "platform" — NEVER the token
    public string? RunId { get; init; }
    public string? RunUrl { get; init; }
    public string? Status { get; init; }                  // queued|in_progress|completed
    public string? Conclusion { get; init; }              // success|failure|cancelled (poll only)
    public Dictionary<string, string>? Outputs { get; init; }
    public IReadOnlyList<ArtifactDto>? Artifacts { get; init; }
    // failure-only:
    public string? FailureCode { get; init; }             // WORKFLOW_NOT_FOUND | RUN_NOT_FOUND | DISPATCH_REJECTED | PLATFORM_ERROR
    public string? FailureReason { get; init; }           // key-free
    public int? PlatformStatusCode { get; init; }         // preserved
}
```

> **Naming note:** this `AgentDispatchResult` is the **CI-run** record (run id / status / outputs). It is **distinct from** the Story 32-5 LLM `AgentRunResult` (provider/model/tokens/cost). Keep the namespaces separate (`Tamma.Api/Services/AgentDispatch` vs `Tamma.Api/Services/Agents`) so they don't collide.

### The thin `DispatchAgentWorkflowActivity` shim (the cutover shape)

```csharp
// no IGitHubActionsClient; no Octokit; no platform HTTP. Same variables out; bookmark suspend unchanged.
var req = new DispatchRunRequest {
    WorkflowRef = workflowRef, Ref = gitRef, Inputs = inputs,
    CorrelationId = context.WorkflowExecutionContext.Id
};
var resp = await _api.DispatchAgentRunAsync(repo, req, tenantId, ct);   // NEW client method

context.SetVariable("DispatchedRunId", resp.RunId);
context.SetVariable("DispatchStatus", new DispatchStatusVar {
    Success = resp.Success, Status = resp.Status, FailureCode = resp.FailureCode });
// ... then the EXISTING durable bookmark suspend on workflow_run.completed — UNCHANGED (inbound, §5.3).
```

`MonitorAgentWorkflowActivity` / `CollectAgentResultsActivity` call `GetAgentRunAsync` and write the same `RunStatus` / `AgentResults` variables as today (AC5).

## Dependencies

**Internal (hard prerequisites):**

- **Story 38-1** (git-platform mediation) — supplies the **shared `IGitRepoAuthorizer`** cross-tenant guard and establishes the Class-A/C endpoint pattern + the per-tenant token resolver shape. (Sequenced first in Epic 38 Phase 1.)
- **Story 32-5** (the `/llm/call` template) — the endpoint shape, `TammaApiClient` cutover convention, request-scoped-credential discipline, and DCB-audit-from-API contract this story mirrors.
- **Epic 28** (tenancy) — the tenant↔repo registry data + per-tenant keying for the guard and token resolution; `ITenantContext`.
- **Epic 29** (secret cabinet) — encrypted per-tenant GitHub Actions token (BYOK), resolved BYOK→platform inside the API.
- **Epic 9** (unified agent API) — the `TammaApiClient` / `TammaEngineAuthHandler` engine↔API callback convention; **and the inbound `WebhookSignalRegistry` signalling contract this story must NOT break** (design §5.3).
- **Epic 4** (DCB) — `DomainEvent` / `IEventRepository`, tenant-scoped, for the `AGENT_DISPATCH.*` audit events.
- **`IGitHubActionsClient`** — the existing in-API Octokit-backed client the endpoints delegate to (now API-only; the engine `NullGitHubActionsClient` registration is removed).

**Consumers (downstream, not blockers):**

- **Story 38-4** (guardrail analyzer) — proves this cutover stays cut (zero `IGitHubActionsClient` injections in the engine).
- The dispatch workflow that consumes `DispatchedRunId` / `RunStatus` / `AgentResults` and suspends on the `workflow_run.completed` bookmark — unchanged.

**Follow-ons (referenced, separate stories):** 38-3 (Slack/notifications mediation), 38-4 (build-time guardrail).

**External:** none new (reuses the existing Octokit-backed `IGitHubActionsClient` — now only in the API process).

## Testing Strategy

1. **Endpoint auth.** Missing/invalid bearer → 401; valid bearer + `X-Tenant-Id` → request bound, `tenantId` derived from header, `{repo}`/`{id}` from route.
2. **Cross-tenant guard (AC2).** A tenant dispatching into a repo it does not own → **403 `REPO_NOT_AUTHORIZED`**; assert `IGitHubActionsClient` is **never** invoked and no token is resolved (fakes record calls). Reuses the 38-1 `IGitRepoAuthorizer` (test it is the same guard).
3. **BYOK vs platform `credentialSource` (AC3).** Cabinet has a tenant Actions token → `credentialSource="byok"`; absent → platform token → `"platform"`; both reach the client with a non-empty token; the token never appears in the response/log/event.
4. **`ACTIONS_TOKEN_UNAVAILABLE` fail-closed (AC6).** Token resolver returns null → **503**, `retryable:false`; the platform is never called.
5. **Typed platform failures (AC6).** `IGitHubActionsClient` reports workflow-not-found / run-not-found / dispatch-rejected / 5xx → **200 `success:false`** with the right `failureCode` and **preserved** `platformStatusCode`; assert a raw 5xx is never produced.
6. **Dispatch + poll happy path (AC4).** `POST .../runs` → `{ runId, status }`; `GET .../runs/{id}` → `{ status, conclusion, outputs, artifacts }`; exactly one terminal `AGENT_DISPATCH.*` event with the right tags.
7. **Thin-activity mapping (AC5).** Given each `AgentDispatchResult`, the activity writes the same `DispatchedRunId` / `RunStatus` / `AgentResults` variables as today; a minimal dispatch workflow branches on them unchanged.
8. **Inbound unchanged (AC9 — the load-bearing invariant).** An integration test exercises dispatch → durable bookmark suspend → simulated in-process `WebhookSignalRegistry` signal (`workflow_run.completed`) → resume; assert the bookmark path is byte-for-byte unchanged and no outbound call is added to it.
9. **Cutover proof (AC5/AC9).** `grep` over `Tamma.Activities` for `IGitHubActionsClient` → **zero** injections; the three activities hold no Octokit/platform-HTTP reference; `NullGitHubActionsClient` registration removed from the engine.
10. **Credential safety (AC3/AC7).** Assert the token never appears in any `AgentDispatchResult`, response body, log line, or DCB event payload.
11. **Audit invariant (AC7).** Exactly one terminal `AGENT_DISPATCH.*` event per call (success or failure) via a fake `IEventRepository`; tags match AC7; `FAILED` carries `failureCode`; distinct from inbound signalling.

Docker-bound C# suites run via `sg docker -c "dotnet test apps/tamma-elsa/..."` (session docker group is stale; plain `dotnet build` needs no wrapper).

## Estimated Effort

4-5 days (two endpoints + the BYOK→platform Actions-token resolver + the three thin-activity cutovers + the audit wiring + the engine-registration removal + the inbound-unchanged regression test; reuses 38-1's `IGitRepoAuthorizer`).

## Files Created/Modified

| File | Action |
|------|--------|
| `apps/tamma-elsa/src/Tamma.Api/Endpoints/AgentDispatchEndpoints.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/AgentDispatch/IAgentDispatchMediationService.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/AgentDispatch/AgentDispatchMediationService.cs` | Create (guard→token→client→audit) |
| `apps/tamma-elsa/src/Tamma.Api/Services/AgentDispatch/IActionsTokenResolver.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/AgentDispatch/ActionsTokenResolver.cs` | Create (Epic 29 cabinet → platform) |
| `apps/tamma-elsa/src/Tamma.Api/Services/AgentDispatch/AgentDispatchRequests.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Api/Services/AgentDispatch/AgentDispatchResponses.cs` | Create (+ `ArtifactDto`, `AgentDispatchResult`) |
| `apps/tamma-elsa/src/Tamma.Api/Services/AgentDispatch/AgentDispatchEventTypes.cs` | Create |
| `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/DispatchAgentWorkflowActivity.cs` | Gut → thin client |
| `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/GitHubActionsExecutor.cs` | Gut/Remove |
| `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/MonitorAgentWorkflowActivity.cs` | Gut → thin client |
| `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/CollectAgentResultsActivity.cs` | Gut → thin client |
| `apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/WebhookSignalRegistry.cs` | Unchanged (inbound — design §5.3) |
| `apps/tamma-elsa/src/Tamma.Api/Clients/TammaApiClient.cs` | Modify (add `DispatchAgentRunAsync` / `GetAgentRunAsync`) |
| `apps/tamma-elsa/src/Tamma.Api/Program.cs` | Modify (map `AgentDispatchEndpoints`; register service/token; `IGitHubActionsClient` stays API-only) |
| `apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs` | Modify (remove `NullGitHubActionsClient` registration) |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/Endpoints/AgentDispatchEndpointsTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Api.Tests/AgentDispatch/AgentDispatchMediationServiceTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/AgentDispatch/AgentDispatchThinClientTests.cs` | Create |
| `apps/tamma-elsa/tests/Tamma.Activities.Tests/AgentDispatch/InboundSignalUnchangedTests.cs` | Create |

## Dev Notes

### Development Process Reminder

Before implementing this story, ensure you have:

1. Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
2. Searched `.dev/` for related spikes, bugs, findings, and decisions (esp. `feedback_resolution_no_empty_fallback`).
3. Read the design of record §1 (steps never call external APIs; §1.2 audit table — the Class-C rows; §1.3 cross-tenant-token risk) and §5 (§5.1 Class-C endpoints; §5.3 **inbound webhooks are out of scope**) IN FULL.
4. Read **Story 38-1** (the shared `IGitRepoAuthorizer` + endpoint pattern you reuse) and **Story 32-5** (the `/llm/call` template), and reviewed `TammaApiClient`, `IGitHubActionsClient` (`OctokitGitHubActionsClient` / `NullGitHubActionsClient`), and the `WebhookSignalRegistry` bookmark suspend/resume path you must NOT touch.
5. Confirmed the Epic 28 tenant↔repo registry + Epic 29 cabinet contracts are landed (or code to their interfaces with fakes), and that Story 38-1's `IGitRepoAuthorizer` is available.
6. Planned the TDD approach; remember the guard runs **before** token resolution/dispatch, and the inbound signal path stays unchanged.

### Key Design Decisions

- **Mirror `/llm/call` + reuse 38-1's guard.** Same auth plane, same engine-only policy, same request-scoped-credential discipline, same DCB-audit-from-API contract; reuse Story 38-1's `IGitRepoAuthorizer` so there is one cross-tenant guard, not two.
- **Only the OUTBOUND dispatch/poll is mediated.** The inbound `workflow_run.completed` webhook + `WebhookSignalRegistry` are inbound (design §5.3) — received and signature-verified by `Tamma.Api`, signalled in-process. There is no outbound external call to mediate; this story leaves the bookmark suspend/resume untouched and proves it with a regression test.
- **The cross-tenant guard is load-bearing (design §1.3).** Dispatch triggers Actions code execution; a cross-tenant dispatch is cross-tenant execution. Authorize `tenant ↔ repo` FIRST; deny → 403, platform never called, no token resolved. Fail-closed.
- **Fail-closed, never empty (`feedback_resolution_no_empty_fallback`).** Token resolution is tenant→system→error; unresolvable → 503 `ACTIONS_TOKEN_UNAVAILABLE`, never a call with an empty/default token.
- **Status preservation for expected failures.** Platform failures (workflow/run not found, dispatch rejected) return **200 `success:false` + preserved `platformStatusCode`**, never a raw 5xx, so the dispatch workflow branches the way it does today (the same discipline as 32-5 AC7 / 38-1 AC6) — and the durable bookmark contract is not corrupted.
- **DCB audit from the API.** Emitted where the tenant `IEventRepository` + cabinet live; the `AGENT_DISPATCH.*` family is **distinct from** inbound signalling and from the LLM `AGENT.RUN.*` family — no double counting.
- **No new control-plane table (AC8).** Reuses Epic 28/29 + the tenant `domain_events` stream. No DROP-list entry, no `ControlPlaneDbContextModelTests` edit. (Story 38-1 owns any CP tenant↔repo table.)
- **Name collision guard.** The CI-run `AgentDispatchResult` (this story) is distinct from the LLM `AgentRunResult` (32-5) — separate namespaces, no shared type.

### Per-mode ownership (mandatory two-scoping-model answer, per CLAUDE.md)

| Question | single-user mode | SaaS mode |
|---|---|---|
| Who is the principal of an agent-dispatch request? | The sole user (keyed by `UserId`; `TenantId` may be null). | The tenant (keyed by `TenantId` from `X-Tenant-Id`). No per-user layer. |
| Who may dispatch into / read `{repo}`? | The sole user — the guard verifies the user owns the configured repo(s). | Only the tenant whose `X-Tenant-Id` owns `{repo}` in the tenant↔repo registry; a cross-tenant dispatch → 403 `REPO_NOT_AUTHORIZED`. |
| Whose Actions token does the dispatch use? | The sole user's BYOK token → else platform default; resolved in the API. | The tenant's BYOK token (Epic 29 cabinet, keyed by `TenantId`) → else platform-provided (where allowed). `credentialSource` records which. |
| Where do `AGENT_DISPATCH.*` audit events land? | The user's (sole) tenant event store. | The tenant's `t_<hex>` event store via the tenant-scoped `IEventRepository`; `TenantId` set. Never cross-tenant. |
| Where does the inbound `workflow_run.completed` signal go? | The user's engine instance (in-process via `WebhookSignalRegistry`); unchanged by this story. | The tenant's engine instance (in-process); unchanged by this story. |
| Who owns the dispatch's audit/run data? | The user. | The tenant — platform admin sees none of it (Epic 32 ownership rule). |
| Mode source | `ITammaModeProvider` (`TammaMode.cs`) — process-stable. | same |

### Risks and Mitigations

| Risk | Severity | Mitigation |
|------|----------|------------|
| **Mis-scoped token → cross-tenant Actions dispatch (code execution)** (design §1.3 — THE Class-C risk) | Critical | The shared `IGitRepoAuthorizer` cross-tenant guard runs FIRST, before token resolution or dispatch; deny → 403 `REPO_NOT_AUTHORIZED`; dedicated cross-tenant test asserting the client is never reached; token is per-tenant from the cabinet, never a shared default. |
| **Breaking the inbound bookmark suspend/resume** (design §5.3) | Critical | This story does NOT touch `WebhookSignalRegistry` or the `workflow_run.completed` path; the `InboundSignalUnchangedTests` integration test proves dispatch→suspend→signal→resume is byte-for-byte unchanged and no outbound call is added. |
| Endpoint returns a raw 5xx → dispatch outcome mapping / bookmark contract silently breaks | High | The result mapper always returns 200 `success:false` + preserved `platformStatusCode` for expected platform failures; 403/401/503 only for guard/auth/credential; HTTP-status-fidelity test. |
| Engine still holds an Actions token after cutover | High | `IGitHubActionsClient` stays API-only; remove `NullGitHubActionsClient` registration; `grep` `Tamma.Activities` for `IGitHubActionsClient` → zero; 38-4 guardrail makes it permanent. |
| Token leaks into a log / response / event | High | Token request-scoped, dropped after the call; `credentialSource` is the only credential field surfaced; explicit credential-safety test. |
| Thin activity writes different variables → dispatch workflow breaks | High | Map each `AgentDispatchResult` to the exact `DispatchedRunId`/`RunStatus`/`AgentResults` shapes; minimal dispatch workflow integration test. |
| `AgentDispatchResult` vs LLM `AgentRunResult` name collision | Medium | Separate namespaces (`Services/AgentDispatch` vs `Services/Agents`); no shared type; compile-time disambiguation. |
| Empty/default token fallback on resolution failure | Medium | Fail-closed: 503 `ACTIONS_TOKEN_UNAVAILABLE`, `retryable:false`; never call with an empty token (`feedback_resolution_no_empty_fallback`). |
| Co-hosting hides the violation (design §1.1) | Medium | The activity calls over the wire via `TammaApiClient`, never resolves an injected vendor service — verified the moment the engine runs as per-tenant dedicated compute (Cranl). |
| Depends on 38-1 guard / 32-5 pattern / Epic 28-29 not yet landed | Medium | Code to the interfaces; reuse 38-1's guard; mirror 32-5; use fakes in tests until they land. |

### Success Metrics

- [ ] `grep` over `Tamma.Activities` finds **zero** `IGitHubActionsClient` injections (all three activities cut over).
- [ ] Every agent-dispatch request authorizes the `tenant ↔ repo` relationship before resolving a token; a cross-tenant dispatch is 403'd and never reaches the platform (isolation test).
- [ ] 100% of mediated dispatch/poll calls emit exactly one terminal `AGENT_DISPATCH.*` event from `Tamma.Api`, tagged `{ tenantId, repo, operation, runId, credentialSource, correlationId }`.
- [ ] The Actions token never appears in any response body, log line, or DCB event payload (credential-safety test green).
- [ ] The inbound `workflow_run.completed` / `WebhookSignalRegistry` bookmark suspend/resume is unchanged (regression test green).
- [ ] The three activities hold no Octokit/platform-HTTP reference; the dispatch workflow is unchanged.

## Related

- Design of record: `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` (§1 steps-never-call-external-APIs; §1.2 audit table — Class-C rows; §1.3 cross-tenant-token risk; §5.1 Class-C endpoints; §5.3 inbound webhooks out of scope)
- Epic 38 README: `docs/stories/epic-38/README.md`
- Template story: `docs/stories/epic-32/story-32-5/32-5-managed-agent-execution-layer.md` (the `/llm/call` mediation this mirrors)
- Sibling story (prerequisite): `story-38-1/` (git-platform mediation — supplies the shared `IGitRepoAuthorizer` cross-tenant guard)
- Implementation plan: `docs/superpowers/plans/2026-06-21-38-2-agent-dispatch-step-mediation-plan.md`
- Sibling stories: `story-38-3/` (Slack/notifications mediation), `story-38-4/` (build-time guardrail analyzer)
- Cross-epic: Epic 28 (tenancy / tenant↔repo registry); Epic 29 (secret cabinet — per-tenant Actions tokens); Epic 9 (`TammaApiClient` + the inbound `WebhookSignalRegistry` signalling contract)
- Reused code: `IGitHubActionsClient` (`OctokitGitHubActionsClient`, now API-only), `TammaApiClient`, `WebhookSignalRegistry` (unchanged), the dispatch workflow consuming `DispatchedRunId`/`RunStatus`/`AgentResults`

## Logging Requirements

- **INFO**: agent-dispatch received (correlationId, repo, operation, tenantId — never the token); authorization decision (allow/deny); dispatch/poll completed (success, operation, runId, status, durationMs, credentialSource).
- **DEBUG**: composition step boundaries (guard → token → client → audit); request DTO shape (no token).
- **WARN**: typed failure paths (`REPO_NOT_AUTHORIZED`, `WORKFLOW_NOT_FOUND`, `RUN_NOT_FOUND`, `DISPATCH_REJECTED`, `PLATFORM_ERROR` + `platformStatusCode`, `ACTIONS_TOKEN_UNAVAILABLE`) with `failureCode` + `correlationId`.
- **ERROR**: contract violations (null body), DCB append failure (the call still returns its result; the append failure is logged, not swallowed), and any attempt to return a raw 5xx (guardrail).
- **Structured context**: `{ tenantId, repo, operation, runId, correlationId, credentialSource }` where applicable.
- **Credential safety (LOAD-BEARING)**: NEVER log, return, or persist the resolved GitHub Actions token or any `Authorization` header. `credentialSource` (the label `byok`/`platform`) is safe; the token is not. The `AgentDispatchResult` body, all DCB event payloads, and the audit trail are token-free by contract — mirroring Story 32-5's credential-safety rule for LLM keys and Story 38-1's for git tokens.

## Change Log

| Date       | Version | Changes                | Author |
| ---------- | ------- | ---------------------- | ------ |
| 2026-06-21 | 1.0.0   | Initial story creation — Class-C agent-dispatch step mediation. Re-points `DispatchAgentWorkflowActivity`/`GitHubActionsExecutor` + `MonitorAgentWorkflowActivity`/`CollectAgentResultsActivity` from the co-hosted `IGitHubActionsClient` (Octokit in API, Null in engine) to new `Tamma.Api` `POST /api/v1/agent-dispatch/{repo}/runs` + `GET /api/v1/agent-dispatch/{repo}/runs/{id}` endpoints that hold the per-tenant Actions token (Epic 28/29 cabinet, BYOK→platform), reuse Story 38-1's `IGitRepoAuthorizer` cross-tenant guard, dispatch/poll, and audit. Activities become thin `TammaApiClient` clients holding no token. The inbound `workflow_run.completed` webhook + `WebhookSignalRegistry` are explicitly OUT of scope (inbound, no outbound call to mediate — design §5.3) and unchanged. Mirrors the Story 32-5 `/llm/call` template. | Claude |
