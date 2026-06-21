# Story 38-2 — Agent-dispatch step mediation (Class C) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking. Project is test-first (TDD) — every task writes tests
> before implementation.

**Date:** 2026-06-21

**Goal:** Re-point the three Class-C AgentDispatch activities (`DispatchAgentWorkflowActivity` /
`GitHubActionsExecutor`, `MonitorAgentWorkflowActivity`, `CollectAgentResultsActivity`) from the
co-hosted `IGitHubActionsClient` (Octokit in API, Null in engine) to new `Tamma.Api` endpoints
(`POST /api/v1/agent-dispatch/{repo}/runs` + `GET /api/v1/agent-dispatch/{repo}/runs/{id}`) that hold
the per-tenant GitHub Actions token (Epic 28/29 cabinet, BYOK→platform), reuse Story 38-1's
`IGitRepoAuthorizer` cross-tenant guard, dispatch/poll the run, and emit a DCB audit event. The
activities collapse into thin `TammaApiClient` clients holding no token. The **inbound**
`workflow_run.completed` webhook + `WebhookSignalRegistry` are explicitly OUT of scope (inbound, no
outbound call to mediate — design §5.3) and must stay byte-for-byte unchanged.

**Story file:** `docs/stories/epic-38/story-38-2/38-2-agent-dispatch-step-mediation.md`
**Design spec:** `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md` (§1.2 Class-C
rows, §1.3 cross-tenant-token risk, §5.1 Class-C endpoints, §5.3 inbound webhooks out of scope)
**Template story:** `docs/stories/epic-32/story-32-5/32-5-managed-agent-execution-layer.md`
**Prerequisite story:** `docs/stories/epic-38/story-38-1/38-1-git-platform-step-mediation.md` (shared
`IGitRepoAuthorizer`)

**Tech stack:** .NET 9 / Elsa 3 in `apps/tamma-elsa` (central API `Tamma.Api` + activities
`Tamma.Activities` + engine `Tamma.ElsaServer`). Tests live in
`apps/tamma-elsa/tests/Tamma.Api.Tests/` and `apps/tamma-elsa/tests/Tamma.Activities.Tests/` (xUnit).
Docker-bound suites run via `sg docker -c "dotnet test ..."` (session docker group is stale; plain
`dotnet build` needs no wrapper). **`packages/api` is DELETED — all of this is C#.**

---

## Non-goals (YAGNI guard)

- **NO new Actions/CI client.** Reuse the existing `OctokitGitHubActionsClient` impl — only move *where
  it is called from* (into `Tamma.Api`, never the engine). Do not reimplement dispatch/poll.
- **NO inbound-webhook change.** `workflow_run.completed` / `WebhookSignalRegistry` / the durable
  bookmark suspend/resume are inbound, out of scope by nature (design §5.3). This plan must NOT touch
  them — it adds a regression test proving they are unchanged.
- **NO duplicate cross-tenant guard.** Reuse Story 38-1's `IGitRepoAuthorizer` — do not author a second.
- **NO git-platform / Slack mediation.** Those are 38-1 / 38-3.
- **NO guardrail analyzer.** That is 38-4; this story proves the cutover by `grep`.
- **NO new control-plane table.** Reuse Epic 28/29 + the tenant `domain_events` stream (38-1 owns any
  CP tenant↔repo table).
- **NO LLM-`AgentRunResult` reuse.** The CI-run record here is a distinct type in a distinct namespace.

---

## Current-state findings (verify against the worktree before coding)

| Seam | Where it is today | How 38-2 uses it |
|---|---|---|
| **Actions client** | `IGitHubActionsClient` — `OctokitGitHubActionsClient` in `Tamma.Api` (holds the Actions token); `NullGitHubActionsClient` in the engine (no-op). | Delegated to **inside `Tamma.Api`** only, with a request-scoped per-tenant token; the engine `Null` registration is removed. |
| **Dispatch activities** | `Tamma.Activities/AgentDispatch/{DispatchAgentWorkflow,MonitorAgentWorkflow,CollectAgentResults}Activity.cs` + `GitHubActionsExecutor` — inject `IGitHubActionsClient`. | Gutted to thin `TammaApiClient` clients; drop the injection. |
| **Inbound signal** | `AgentDispatch/WebhookSignalRegistry` — `Tamma.Api` receives `workflow_run.completed`, verifies the signature, signals the engine in-process; the dispatch activity suspends on a durable bookmark. | **UNCHANGED** (inbound, §5.3). A regression test proves suspend/resume still works. |
| **Cross-tenant guard** | Story 38-1's `IGitRepoAuthorizer` (tenant↔repo registry, fail-closed). | **Reused** as the guard, FIRST, before token/dispatch. |
| **Per-tenant token** | Epic 29 cabinet (the Actions analogue of 38-1's git token / 32-3's LLM key). | `ActionsTokenResolver` resolves BYOK→platform inside the API. |
| **Engine→API callback** | `TammaApiClient` (Bearer `Tamma:ApiToken` + `X-Tenant-Id`; `PostAsync<T>`/`GetAsync<T>` + `AddTenantHeader` + `RecordHealthAsync`). | The transport + auth plane for the 2 endpoints; add 2 client methods. |
| **DCB events** | `Tamma.Data` `IEventRepository.AppendAsync(DomainEvent)`, tenant-scoped. | Emit `AGENT_DISPATCH.RUN_TRIGGERED.*` / `RUN_POLLED.*` from the API. |
| **Mode** | `Tamma.Api/Services/PromptStore/TammaMode.cs` — `ITammaModeProvider` (SingleUser | SaaS). | Principal keying for guard + token + audit. |

**Key insight:** the only genuinely new code is the *endpoint shell* (`AgentDispatchEndpoints`), the
*composition service* (`AgentDispatchMediationService`), the *token resolver* (`ActionsTokenResolver`),
the *wire records*, and the *thin-client cutover* of three activities + two new `TammaApiClient`
methods. The dispatch/poll itself is the existing `OctokitGitHubActionsClient`; the guard is 38-1's;
the inbound signal path is untouched.

---

## Architecture

```
Engine: {DispatchAgentWorkflow(+GitHubActionsExecutor),MonitorAgentWorkflow,CollectAgentResults}Activity
   |  (thin TammaApiClient client — NO token, NO Octokit)        ... then EXISTING bookmark suspend (unchanged)
   v
TammaApiClient.{DispatchAgentRun,GetAgentRun}Async
   |  Bearer Tamma:ApiToken + X-Tenant-Id
   v
Tamma.Api  AgentDispatchEndpoints  ->  AgentDispatchMediationService.{TriggerRun,GetRun}Async:
   1 authorize tenant↔repo   (IGitRepoAuthorizer — SHARED with 38-1)  -- CROSS-TENANT GUARD, FIRST -> 403 on deny
   2 resolve token           (IActionsTokenResolver)  -- Epic 29 cabinet BYOK->platform -> 503 on null
   3 dispatch/poll           (IGitHubActionsClient, request-scoped token, API-only)
   4 emit AGENT_DISPATCH.*   (tenant IEventRepository)  -- exactly one terminal event
   5 return AgentDispatchResult -> ToHttpResult (200 | 200 success:false | 403 | 503)

INBOUND (UNCHANGED, §5.3):  GitHub workflow_run.completed -> Tamma.Api (verify sig) -> WebhookSignalRegistry
                            -> in-process signal -> engine bookmark resume.   NO outbound call to mediate.
```

Per-mode ownership (CLAUDE.md two-scoping-model): single-user = the sole user's repo(s) + the user's
token + events/signal in the user's instance; SaaS = only the `X-Tenant-Id` tenant's repo(s) + the
tenant's BYOK token → platform + events in the tenant `t_<hex>` store, never cross-tenant; inbound
signal in-process to that tenant's engine, unchanged. Mode from `ITammaModeProvider`.

---

## Task breakdown

Order: T1 (wire records + event types) → T2 (token resolver) → T3 (mediation service: happy path)
→ T4 (typed failures + audit) → T5 (endpoints) → T6 (thin-activity cutover + client methods) →
T7 (engine-registration removal + inbound-unchanged regression). T1 ∥ T2.

### T1 — Wire records + event-type constants

**Scope:** The request/response shapes and `AGENT_DISPATCH.*` event constants. No behaviour.

**Files (new):** `Services/AgentDispatch/AgentDispatchRequests.cs` (`DispatchRunRequest`),
`Services/AgentDispatch/AgentDispatchResponses.cs` (`AgentDispatchResult`, `ArtifactDto`),
`Services/AgentDispatch/AgentDispatchEventTypes.cs` (`AGENT_DISPATCH.RUN_TRIGGERED.*`,
`AGENT_DISPATCH.RUN_POLLED.*`).

**Tests (first):** `tests/Tamma.Api.Tests/AgentDispatch/AgentDispatchResultTests.cs` — record equality;
`Success=false` always carries `FailureCode`+`FailureReason`; `CredentialSource` is `byok|platform`; no
token field exists; the type is distinct from the LLM `AgentRunResult` (namespace check).

**Acceptance:**
- [ ] Records compile; `AgentDispatchResult` has all AC fields; **no field can carry the raw token**.
- [ ] No name/namespace collision with `Tamma.Api/Services/Agents/AgentRunResult`.

### T2 — Per-tenant Actions-token resolver (`IActionsTokenResolver`) — BYOK→platform, fail-closed

**Scope:** `ActionsTokenResolver : IActionsTokenResolver` — `ResolveAsync(tenantId, repo, ct)` →
`{ Token, Source }`. Epic 29 cabinet (tenant BYOK Actions token) → else platform-provided where
allowed → else **null** (NEVER empty/default). Mirrors 38-1's `GitTokenResolver` / Story 32-3.

**Files (new):** `Services/AgentDispatch/IActionsTokenResolver.cs`,
`Services/AgentDispatch/ActionsTokenResolver.cs`.

**Tests (first):** `tests/Tamma.Api.Tests/AgentDispatch/ActionsTokenResolverTests.cs` — cabinet has
tenant token → `Source="byok"`; absent + platform allowed → `Source="platform"`; absent + not allowed
→ null (fail-closed); the returned `Token` is never logged.

**Acceptance:**
- [ ] BYOK→platform→null order; null when unresolvable (no empty/default fallback).
- [ ] `Source` is `byok|platform`; token never surfaces in logs.

### T3 — `AgentDispatchMediationService` core composition (happy path)

**Scope:** `AgentDispatchMediationService : IAgentDispatchMediationService` with `TriggerRunAsync` and
`GetRunAsync`. Compose guard(1, reuse 38-1's `IGitRepoAuthorizer`) → token(2) → `IGitHubActionsClient`(3)
→ audit(4) → result(5). Token request-scoped, dropped after the call.

**Files (new):** `Services/AgentDispatch/IAgentDispatchMediationService.cs`,
`Services/AgentDispatch/AgentDispatchMediationService.cs`.

**Collaborators (constructor-injected — fakes in tests):** `IGitRepoAuthorizer` (38-1),
`IActionsTokenResolver`, `IGitHubActionsClient`, `IEventRepository`, `ITammaModeProvider`,
`ILogger<AgentDispatchMediationService>`.

**Tests (first):** `tests/Tamma.Api.Tests/AgentDispatch/AgentDispatchMediationServiceTests.cs` —
trigger + poll happy paths: guard→token→client→audit called once in order; correct response DTO
populated; `credentialSource` copied from the resolver; exactly one terminal `AGENT_DISPATCH.*`
event with the right tags.

**Acceptance:**
- [ ] Trigger + poll return a fully-populated `AgentDispatchResult{Success=true}`.
- [ ] Guard runs BEFORE token; token BEFORE dispatch; one terminal event per call.

### T4 — Typed failures + audit discipline (fail-closed, status preservation)

**Scope:** Each step gets a typed-failure exit: guard deny → 403 `REPO_NOT_AUTHORIZED` (client never
called, token never resolved); token null → 503 `ACTIONS_TOKEN_UNAVAILABLE` (`retryable:false`);
platform error → **200 `success:false`** with `failureCode ∈ {WORKFLOW_NOT_FOUND, RUN_NOT_FOUND,
DISPATCH_REJECTED, PLATFORM_ERROR}` and **preserved** `platformStatusCode`. No expected failure throws;
no raw 5xx. Exactly one terminal `AGENT_DISPATCH.*.FAILED` event.

**Files:** extend `AgentDispatchMediationService`; add a `ToHttpResult()` mapper.

**Tests (first):** extend `AgentDispatchMediationServiceTests` — one test per failure code; guard-deny →
client never invoked; token-null → client never invoked; platform 5xx → 200 `success:false` +
`platformStatusCode` preserved (assert no raw 5xx); "exactly one terminal event per call" invariant.

**Acceptance:**
- [ ] All failure codes produce typed results; none throw; raw 5xx never leaks.
- [ ] Guard-deny and token-null short-circuit before the dispatch.

### T5 — Endpoints (`AgentDispatchEndpoints.cs`)

**Scope:** Map `POST /api/v1/agent-dispatch/{repo}/runs` + `GET /api/v1/agent-dispatch/{repo}/runs/{id}`;
engine-only auth (`EngineAuthPolicy` — same plane as `/llm/call`); bind `{repo}`/`{id}`/body; derive
`tenantId` from `X-Tenant-Id`; delegate; `ToHttpResult`.

**Files (new):** `Endpoints/AgentDispatchEndpoints.cs`; modify `Tamma.Api/Program.cs` (map endpoints;
register service/token; `IGitHubActionsClient` stays API-only).

**Tests (first):** `tests/Tamma.Api.Tests/Endpoints/AgentDispatchEndpointsTests.cs` — 401 missing/invalid
bearer; valid bearer + `X-Tenant-Id` → bound + delegated; 403 on guard deny; 200 `success:false` +
`platformStatusCode` on platform failure; happy 200 per route. `WebApplicationFactory` + fakes.

**Acceptance:**
- [ ] Both routes served, engine-only, 401 on missing bearer; DI resolves at host startup.

### T6 — Thin-activity cutover + `TammaApiClient` methods (AC5)

**Scope:** Gut `DispatchAgentWorkflowActivity` (+ `GitHubActionsExecutor`),
`MonitorAgentWorkflowActivity`, `CollectAgentResultsActivity` to thin `TammaApiClient` clients (drop
`IGitHubActionsClient`); add two client methods (`DispatchAgentRunAsync` / `GetAgentRunAsync`)
following the existing `PostAsync<T>`/`GetAsync<T>` + `AddTenantHeader` + `RecordHealthAsync` pattern.
Each activity writes the **same** workflow variables it writes today; the **dispatch activity's durable
bookmark suspend is left exactly as-is** after the thin dispatch call.

**Files:** modify `Tamma.Activities/AgentDispatch/{DispatchAgentWorkflow,MonitorAgentWorkflow,
CollectAgentResults}Activity.cs` + `GitHubActionsExecutor.cs`; modify `Tamma.Api/Clients/TammaApiClient.cs`.

**Tests (first):** `tests/Tamma.Activities.Tests/AgentDispatch/AgentDispatchThinClientTests.cs` — given
each `AgentDispatchResult`, the activity writes the same `DispatchedRunId`/`RunStatus`/`AgentResults`
variables as the legacy path; a minimal dispatch workflow branches on them unchanged; the activity
holds no `IGitHubActionsClient`.

**Acceptance:**
- [ ] Three activities cut over; same variables out; no Octokit/platform-HTTP reference.
- [ ] `grep` over `Tamma.Activities` for `IGitHubActionsClient` → zero.

### T7 — Engine-registration removal + inbound-unchanged regression + credential-safety sweep

**Scope:** Remove the engine's `NullGitHubActionsClient` registration in `Tamma.ElsaServer/Program.cs`;
confirm no Actions token can be pushed into the engine process. Prove the inbound
`workflow_run.completed` / `WebhookSignalRegistry` bookmark suspend/resume is unchanged. Credential-
safety sweep: token never in any response/log/event.

**Files:** modify `Tamma.ElsaServer/Program.cs`; add the inbound regression + credential-safety tests.

**Tests (first):** `tests/Tamma.Activities.Tests/AgentDispatch/InboundSignalUnchangedTests.cs` —
dispatch → durable bookmark suspend → simulated in-process `WebhookSignalRegistry` signal → resume;
assert the path is unchanged and no outbound call is added to it. A credential-safety test over
response/log/event payloads (no token substring). A host-boot smoke test asserting the engine resolves
the activities **without** any Actions-client registration.

**Acceptance:**
- [ ] Engine holds no Actions token / Actions-client registration.
- [ ] Inbound suspend→signal→resume unchanged (regression green).
- [ ] Credential-safety test green: token absent from every response, log, and event.

---

## Story order & dependencies

External/pattern prereqs: **Story 38-1** (the shared `IGitRepoAuthorizer` + the Class-A/C endpoint
pattern — sequenced first in Epic 38 Phase 1), **Story 32-5** (the `/llm/call` template), **Epic 28**
(tenant↔repo registry + per-tenant keying), **Epic 29** (cabinet — per-tenant Actions token),
**Epic 9** (`TammaApiClient` plane + the inbound `WebhookSignalRegistry` signalling contract to
preserve), **Epic 4** (DCB). Code to their interfaces with fakes until landed. Internal order:
T1 ∥ T2 → T3 → T4 → T5 → T6 → T7. Downstream: 38-4 guardrail proves the cutover stays cut (not a
blocker).

## Verification

```bash
# build (no docker wrapper needed)
dotnet build apps/tamma-elsa/Tamma.sln
# tests (docker-bound suites need the sg wrapper; session docker group is stale)
sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Api.Tests/ --filter FullyQualifiedName~AgentDispatch"
sg docker -c "dotnet test apps/tamma-elsa/tests/Tamma.Activities.Tests/ --filter FullyQualifiedName~AgentDispatch"
# cutover proof: zero IGitHubActionsClient injections in the engine activities
grep -rn "IGitHubActionsClient" apps/tamma-elsa/src/Tamma.Activities
# inbound path untouched: WebhookSignalRegistry unchanged (review diff = empty for that file)
git diff --stat -- apps/tamma-elsa/src/Tamma.Activities/AgentDispatch/WebhookSignalRegistry.cs
```

## Risks

- **Mis-scoped token → cross-tenant Actions dispatch (code execution)** (design §1.3 — THE Class-C
  risk): Critical. The shared `IGitRepoAuthorizer` guard runs FIRST, before token resolution or
  dispatch; deny → 403, never a call. Dedicated cross-tenant test asserts the client is unreachable on
  deny. Token is per-tenant from the cabinet, never a shared default.
- **Breaking the inbound bookmark suspend/resume** (design §5.3): Critical. This story does NOT touch
  `WebhookSignalRegistry` or the `workflow_run.completed` path; the `InboundSignalUnchangedTests`
  integration test proves dispatch→suspend→signal→resume is unchanged and no outbound call is added;
  the `git diff --stat` for `WebhookSignalRegistry.cs` is empty.
- **Raw 5xx leak breaks dispatch outcome mapping / bookmark contract:** High. The mapper always returns
  200 `success:false` + preserved `platformStatusCode` for expected platform failures; 403/401/503 only
  for guard/auth/credential; HTTP-status-fidelity test.
- **Engine still holds an Actions token after cutover:** High. `IGitHubActionsClient` stays API-only;
  remove `NullGitHubActionsClient` registration; `grep` confirms zero injections; T7 host-boot smoke
  test; 38-4 makes it permanent.
- **Token leak into log/response/event:** High. Request-scoped token dropped after the call;
  `credentialSource` is the only credential field surfaced; explicit credential-safety test.
- **Thin activity writes different variables → dispatch workflow breaks:** High. Map each result to the
  exact legacy variable shapes; minimal dispatch workflow integration test.
- **`AgentDispatchResult` vs LLM `AgentRunResult` collision:** Medium. Separate namespaces
  (`Services/AgentDispatch` vs `Services/Agents`); no shared type; T1 namespace check.
- **Empty/default token fallback:** Medium. Fail-closed: 503 `ACTIONS_TOKEN_UNAVAILABLE`,
  `retryable:false`; never call with an empty token (`feedback_resolution_no_empty_fallback`).
- **EF / control-plane table:** Low. This story adds **no** CP table (reuses Epic 28/29 + the tenant
  `domain_events` stream; 38-1 owns any tenant↔repo CP table). No DROP-list entry, no
  `ControlPlaneDbContextModelTests` edit. This plan **amends/extends the existing EF migration
  snapshot**, it does not branch it (sequential implementation on one snapshot).
- **Dependency timing (38-1 guard / 32-5 pattern / Epic 28-29):** Medium. Reuse 38-1's guard; mirror
  32-5; code to interfaces; fakes until landed.
