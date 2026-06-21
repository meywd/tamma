# Epic 38: Step → Internal-API Mediation for Non-LLM Integrations

## Overview

Epic 32 closed the **LLM** rule-1 violation: a workflow step no longer calls Anthropic/OpenAI from inside the Elsa engine — it calls `POST /api/v1/llm/call`, and `Tamma.Api` holds the credential, gates the call, performs the external HTTP, meters, and audits. Epic 38 finishes the job for **every other external effect** the engine still reaches (or reaches *only because* it is co-hosted with `Tamma.Api` in the current single-process deploy).

The locked principle (design §1.1): the Elsa engine (`Tamma.ElsaServer` / `Tamma.Activities`) is a **deterministic orchestrator that holds no secrets**. Any activity needing an external effect **delegates over HTTP to a `Tamma.Api` endpoint** through `TammaApiClient` (Bearer `Tamma:ApiToken` via `TammaEngineAuthHandler` + `X-Tenant-Id`). The credential-holding code, the authorization decision, the external HTTP call, and the metering/audit emission **all live in `Tamma.Api`**.

> **Co-hosting is NOT compliance.** Many activities today resolve a credential-holding *service from the same DI container* (`IGitHubIntegrationService`, `IGitHubActionsClient`, `IIntegrationService`) because, in the single-process deploy, the engine activities and the API services share a container. Rule #1 forbids this: the step must call an internal endpoint **over the wire**, not resolve an injected vendor service. This matters the moment the engine runs as **per-tenant dedicated compute** (the Cranl path) — then the token would have to be pushed *into the engine process*, exactly what Epic 38 prevents.

Epic 38 reuses the **`/llm/call` template** verbatim: engine activity → `TammaApiClient` → an internal `/api/v1` endpoint that holds the credential, authorizes the `tenant ↔ resource` relationship, performs the call, and emits the audit event. The activities collapse into thin clients that hold **no token**.

### The compliant references already in-tree (the template — design §1.1)

| Reference | Pattern | What Epic 38 borrows |
|---|---|---|
| **`TammaApiClient`** | Engine→API HTTP delegation: `Authorization: Bearer <Tamma:ApiToken>` + `X-Tenant-Id`; `PostAsync<T>` + `AddTenantHeader` + `RecordHealthAsync`. Already routes agent-resolve / budget / diagnostics / provider-session / **`/llm/call`**. | The transport + auth plane for every Epic 38 endpoint. |
| **`TriggerCIActivity`** | Already POSTs to an internal `Engine:CallbackUrl/api/engine/trigger-ci`; holds no CI-vendor credential. | The "internal POST, no key in engine" shape for synchronous request/response. |
| **`QueueWelcomeEmailActivity`** | The **outbox pattern**: the step writes intent to `platform_email_outbox`; an out-of-band `OutboxSmtpSender` in the API holds the SMTP credential and performs transport. | The model for **fire-and-forget** external effects (Class D Slack). |

## Relationship to Epic 32 (this epic does NOT block it)

Epic 32 mediated the **LLM path only** (design §5.2 "Now"). Epic 38 is the explicitly-named **follow-up sibling** ("Step → internal-API mediation for non-LLM integrations", design §5.2/§6) for everything else. The LLM violators were P0 — the only steps that, in **any** deploy topology, put a live external key in the engine — and were fixed in Epic 32 (Story 32-5). The non-LLM violators are **VIOLATION-by-co-hosting**: latent today (the credential service is unregistered/null in the standalone engine), but live the moment the engine is per-tenant dedicated compute. Epic 38 closes them on its own schedule and adds a build-time guardrail so they cannot reappear.

## The violator classes (derived from the §1.2 audit table)

| Class | Activities (engine) | External target | How it reaches it today | Verdict (§1.2) | Epic 38 story |
|---|---|---|---|---|---|
| **A — Git platform** | `ADL/CreateBranchActivity`, `ADL/CreatePullRequestActivity`, `ADL/MergePullRequestActivity`, `ADL/UpdateIssueStatusActivity`, `ADL/AnalyzeReviewActivity` | GitHub / GitLab / … | `IGitHubIntegrationService` (impl + `GitHub:Token` in `Tamma.Api`; **unregistered/null in engine**) | VIOLATION-by-co-hosting — **high blast radius**: a mis-scoped platform token = cross-tenant write/merge | **38-1** |
| **C — Agent dispatch** | `AgentDispatch/DispatchAgentWorkflowActivity` / `GitHubActionsExecutor`, `AgentDispatch/MonitorAgentWorkflowActivity`, `AgentDispatch/CollectAgentResultsActivity` | GitHub Actions | `IGitHubActionsClient` (`OctokitGitHubActionsClient` in API; `NullGitHubActionsClient` in engine) | VIOLATION-by-co-hosting | **38-2** |
| **D — Slack / notifications** | `Integration/SlackActivity` | Slack | `IIntegrationService` (impl + Slack token in API; unregistered in engine) | VIOLATION-by-co-hosting — **low blast radius** (token-holding but reads no tenant data) | **38-3** |
| **E — Billing / Stripe** | (future — Epic 35 unbuilt) | Stripe | none yet | **Enforce-by-design**: the activity emits an intent / outbox row; the API holds the Stripe key, charges/invoices, meters. **Prohibited at design time** to call Stripe from an activity. | covered by the 38-4 guardrail; enforced when Epic 35 lands |

### Already compliant (no story — referenced as the template)

- **`Testing/TriggerCIActivity`** — already POSTs to an internal engine callback; holds no CI-vendor credential. Epic 38 only *formalizes* it under `/api/v1` opportunistically; it is not a violator.
- **`TenantLifecycle/QueueWelcomeEmailActivity`** — the outbox reference pattern (Class D borrows it).
- **`AgentDispatch/WebhookSignalRegistry`** — the inbound `workflow_run.completed` receiver. **Not a violator** (inbound): received by `Tamma.Api`, signalled to the engine in-process; there is **no outbound external call to mediate**. Out of scope by nature (design §5.3) — see Story 38-2.

## Stories

| Story | Title | Class | Priority | Status | Est. Effort |
|-------|-------|-------|----------|--------|-------------|
| 38-1 | Git-platform step mediation (Class A) | A | P1 | drafted | 5-6 days |
| 38-2 | Agent-dispatch step mediation (Class C) | C | P1 | drafted | 4-5 days |
| 38-3 | Slack / notifications step mediation (Class D) | D | P2 | drafted | 2-3 days |
| 38-4 | Build-time guardrail analyzer (no in-engine external calls) | — | P1 | drafted | 3-4 days |

## Architecture

```
+-----------------------------------------------------------------------------+
|     EPIC 38: STEP → INTERNAL-API MEDIATION FOR NON-LLM INTEGRATIONS          |
+-----------------------------------------------------------------------------+
|                                                                             |
|   ENGINE (Tamma.ElsaServer / Tamma.Activities) — HOLDS NO SECRETS           |
|   +----------------------+   +----------------------+   +----------------+   |
|   | ADL/* git activities |   | AgentDispatch/*      |   | Integration/   |   |
|   | (thin clients)       |   | (thin clients)       |   | SlackActivity  |   |
|   +----------+-----------+   +----------+-----------+   +-------+--------+   |
|              |                          |                       |            |
|              |  TammaApiClient (Bearer Tamma:ApiToken + X-Tenant-Id)        |
|              v                          v                       v            |
|   ......................................................................... |
|   :  Tamma.Api  — HOLDS CREDENTIALS, AUTHORIZES, CALLS, METERS, AUDITS    : |
|   :  +------------------------+  +-----------------------+  +-----------+  : |
|   :  | GitEndpoints (38-1)    |  | AgentDispatchEndpoints |  | Notif-    |  : |
|   :  | /api/v1/git/{repo}/... |  | (38-2)                 |  | ications  |  : |
|   :  |  - PAT/installation    |  | /api/v1/agent-dispatch |  | (38-3)    |  : |
|   :  |  - per-tenant token    |  |  - dispatch/poll runs  |  | /slack    |  : |
|   :  |    (Epic 28/29 cabinet)|  +-----------------------+  | (outbox)  |  : |
|   :  |  - tenant↔repo guard   |                             +-----------+  : |
|   :  |  - audit event         |   Class E (Stripe): enforce-by-design     : |
|   :  +------------------------+                                            : |
|   ......................................................................... |
|                                                                             |
|   BUILD-TIME GUARDRAIL (38-4): fail the build if any Tamma.Activities class  |
|   references HttpClient/PostAsync to a non-TammaApiClient host, or injects   |
|   a credential-holding vendor service. Violations cannot reappear.          |
|                                                                             |
+-----------------------------------------------------------------------------+
```

## Key Technical Decisions

### Mirror `/llm/call`, do not invent a new pattern

Every Epic 38 endpoint mirrors `LlmCallEndpoints` (Story 32-5): same auth plane (`TammaEngineAuthHandler` Bearer + `X-Tenant-Id`), same engine-only authorization policy, same "the credential is resolved *inside* the API, request-scoped, never logged/returned" discipline, same DCB-audit-from-the-API contract. The activity becomes a thin `TammaApiClient` shim that holds no token and owns no vendor logic.

### The cross-tenant guard is the load-bearing control (Class A/C)

The §1.3 risk: "high the moment the engine is not co-hosted with `Tamma.Api`: a **mis-scoped platform token = cross-tenant write/merge**." The API endpoint MUST authorize that *this* tenant (from `X-Tenant-Id`) may act on *this* `{repo}` before it resolves a token or calls the platform — deny → key-free typed failure, never an external call. Resolution is **tenant→system→error** (`feedback_resolution_no_empty_fallback`): never fall back to a default/empty token.

### Per-tenant tokens via the Epic 28/29 cabinet

A tenant's own platform PAT / installation token resolves from the Epic 29 secret cabinet (BYOK), keyed per-tenant (Epic 28 tenancy), falling back to the platform-provided token where allowed — exactly the BYOK→platform shape Story 32-3 established for LLM keys. The resolved token is request-scoped and dropped after the call.

### Outbox for fire-and-forget; request/response for synchronous

Class D (Slack) is fire-and-forget → the `QueueWelcomeEmailActivity` **outbox** model (write intent, out-of-band sender holds the token). Class A (git) and Class C (agent dispatch) need a result back synchronously → the `TriggerCIActivity` **internal-POST** model. (38-3 / the outbox detail is owned by the separately-authored 38-3.)

### Inbound webhooks are out of scope (design §5.3)

The GitHub `workflow_run.completed` receiver + `WebhookSignalRegistry` are **inbound**: received by `Tamma.Api`, signature-verified there, signalled to the engine in-process. There is no outbound external call to mediate — signature verification + secret already live in the API. Story 38-2 explicitly excludes them.

### Build-time guardrail so it cannot regress (38-4)

A Roslyn analyzer / architecture test fails the build if any class under `Tamma.Activities` references `HttpClient`/`PostAsync`/`PostAsJsonAsync` to a non-`TammaApiClient` host, or injects a credential-holding vendor service. This makes rule #1 structural, not a review convention. (38-4 is authored separately.)

## Dependencies

### On Other Epics

- **Epic 32** (Story 32-5): the `/llm/call` mediation **template** every Epic 38 endpoint mirrors (endpoint shape, `TammaApiClient` cutover, request-scoped-credential discipline, DCB-audit-from-API). Not a hard blocker — Epic 38 can proceed in parallel once 32-5's pattern is settled.
- **Epic 28** (tenancy): `ControlPlaneDbContext` / `TenantDbContext`, schema-per-tenant, `ITenantContext` — the per-tenant token keying and the `tenant ↔ repo` authorization data.
- **Epic 29** (secret cabinet): encrypted per-tenant platform tokens (BYOK PAT / installation token), resolved BYOK→platform inside the API.
- **Epic 9** (unified agent API): the `TammaApiClient` / `TammaEngineAuthHandler` engine↔API callback convention reused by every endpoint.
- **Epic 4** (DCB event sourcing): `DomainEvent` / `IEventRepository`, tenant-scoped, for the audit events each endpoint emits.
- **Epic 35** (billing — future): Class E is enforced-by-design when Epic 35 lands; the 38-4 guardrail forbids an in-activity Stripe call before then.

### External Dependencies

- None new. All work is in the C# `apps/tamma-elsa` stack (`Tamma.Api`, `Tamma.Activities`, `Tamma.ElsaServer`, `Tamma.Data`). Reuses the existing `IGitHubIntegrationService` / `IGitHubActionsClient` / `IIntegrationService` implementations (Octokit etc.) — but only **inside `Tamma.Api`**, never in the engine. **`packages/api` is deleted — never referenced.**

## Endpoints (design §5.1)

```
# Class A — git platform (38-1)
POST  /api/v1/git/{repo}/branches
POST  /api/v1/git/{repo}/pull-requests
PUT   /api/v1/git/{repo}/pull-requests/{n}/merge
GET   /api/v1/git/{repo}/pull-requests/{n}/comments
PATCH /api/v1/git/{repo}/issues/{n}

# Class C — agent dispatch (38-2)
POST  /api/v1/agent-dispatch/{repo}/runs
GET   /api/v1/agent-dispatch/{repo}/runs/{id}

# Class D — Slack / notifications (38-3, authored separately)
POST  /api/v1/notifications/slack
```

All endpoints are internal/engine-only (Bearer `Tamma:ApiToken` via `TammaEngineAuthHandler` + `X-Tenant-Id`); a missing/invalid bearer → 401, and the tenant↔resource guard → key-free typed failure on denial.

## Implementation Phases

### Phase 1: High-blast-radius writes (38-1, 38-2) — P1

The git-platform writes (branch/PR/merge/issue) and the agent-dispatch (Actions run + poll) are the highest-risk surfaces once the engine is per-tenant dedicated compute — a mis-scoped token here is a cross-tenant write/merge. Mediate them first, behind the cross-tenant guard.
Estimated: 9-11 days

### Phase 2: Low-blast-radius effects + guardrail (38-3, 38-4) — P2/P1

Slack/notification mediation (low blast radius, fire-and-forget via outbox) and the build-time guardrail analyzer that makes rule #1 structural and prevents regression of the whole epic (and pre-enforces Class E Stripe).
Estimated: 5-7 days

## Success Metrics

- 100% of Class A/C/D activities are thin `TammaApiClient` clients holding **no** external token; `grep` over `Tamma.Activities` finds **zero** injections of `IGitHubIntegrationService` / `IGitHubActionsClient` / `IIntegrationService` and zero non-`TammaApiClient` `HttpClient`/`PostAsync` hosts.
- Every mediated endpoint authorizes the `tenant ↔ repo` relationship before resolving a token; a cross-tenant request is denied with a key-free typed failure and **never** reaches the platform.
- Every mediated call emits exactly one terminal DCB audit event from `Tamma.Api`, tagged with `tenantId` + `repo` + the operation; no token appears in any log, response body, or event payload.
- The 38-4 guardrail fails the build on a re-introduced direct external call or injected vendor service (verified by a deliberately-violating fixture).
- Inbound webhook handling (`WebhookSignalRegistry`) is unchanged (out of scope by nature — design §5.3).

## Reference Documents

- [Epic 32 Revised Agent Architecture — Design of Record](../../superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md) — §1 (steps never call external APIs; §1.2 audit table; §1.3 prioritized violators), §5 (non-LLM step mediation: §5.1 endpoints, §5.2 phasing, §5.3 what cannot be mediated)
- [Epic 32 → 37 Re-plan](../../superpowers/plans/2026-06-20-epic-32-37-replan.md)
- [Story 32-5 — Call-LLM Endpoint + Managed Execution](../epic-32/story-32-5/32-5-managed-agent-execution-layer.md) — the `/llm/call` template Epic 38 mirrors
- [Story 38-1 — Git-platform step mediation (Class A)](./story-38-1/38-1-git-platform-step-mediation.md)
- [Story 38-2 — Agent-dispatch step mediation (Class C)](./story-38-2/38-2-agent-dispatch-step-mediation.md)
- [Story 38-3 — Slack / notifications step mediation (Class D)](./story-38-3/38-3-slack-notifications-step-mediation.md)
- [Story 38-4 — Build-time guardrail analyzer](./story-38-4/38-4-build-time-guardrail-analyzer.md)
- [CLAUDE.md](../../../CLAUDE.md) — operating modes + per-mode two-scoping-model rule; the unified schema-per-tenant tenancy model

---

**Last Updated**: 2026-06-21
**Epic Owner**: TBD
**Implementation Start**: TBD (does NOT block Epic 32)
**Total Estimated Effort**: 14-18 days
