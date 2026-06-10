# Elsa Two-Tier Topology — Global + Per-Tenant

> **Superseded/extended by the unified schema-per-tenant model** — see `docs/superpowers/plans/2026-06-09-unified-schema-per-tenant.md` (complete 2026-06-10).

**Status:** Design — pending implementation
**Depends on:** `01-control-plane-split.md` (control-plane / tenant-DB split — assumed as context)
**Supersedes:** the single-Elsa-instance model currently running in `apps/tamma-elsa/docker-compose.yml`
**Written against repo state:** `worktree-agent-a36a5298` branch (Epic 19 Phase 3 complete — C# API is primary backend)

---

## 0. Scope and intent

Tamma today runs **one shared Elsa server** (`apps/tamma-elsa/src/Tamma.ElsaServer/`) that hosts every Tamma workflow against a single PostgreSQL database. Every tenant's mentorship sessions, LLM calls, triage cycles, and code-review workflows all execute on that instance, sharing the same `workflow_instances` / `workflow_definitions` / bookmark tables.

We are splitting this into two tiers:

1. **Global Elsa** — one instance, shared across all tenants, hosts platform-lifecycle and orchestration workflows (tenant provisioning, tenant deletion, the autonomous-development orchestrator). Its DB lives with the control plane.
2. **Per-tenant Elsa** — one logical Elsa runtime per tenant, hosts tenant-scoped engine work (LLM calls, code generation, PR lifecycle, mentorship, CI monitoring). Its DB is `tamma_tenant_<id>_elsa`, co-located with the tenant's application DB.

The orchestrator TypeScript state machine in `packages/orchestrator/src/engine.ts` is being retired. Its 14-step loop is ported to an Elsa workflow (`OrchestratorWorkflow`) that runs on **global Elsa**. Tenant-specific work it triggers runs on **per-tenant Elsa**.

This document is the design — no code changes in this wave.

---

## 1. Current Elsa topology (audit)

### Deployment

- **One** `elsa-server` container defined in `apps/tamma-elsa/docker-compose.yml` and `docker-compose.prod.yml` (prod runs 2 replicas of the same instance, load-balanced — not per-tenant).
- **One** `elsa-studio` container (workflow authoring UI).
- Both point at one Postgres (`tamma` database) via `ConnectionStrings__DefaultConnection`.

### Bootstrap (`Tamma.ElsaServer/Program.cs`)

All of the following are configured against the **same** Postgres connection string:

- `UseWorkflowManagement` → Elsa workflow-definition tables
- `UseWorkflowRuntime` → bookmarks, execution logs, instance state
- `UseAgentPersistence` → Elsa Agents module (agent definitions, API keys, services)

All 30+ activities from `Tamma.Activities.dll` are registered via one `AddActivitiesFrom<ClaudeAnalysisActivity>()` call. All code-first workflows are registered via one `AddWorkflowsFrom<LlmCallWorkflow>()` call.

### Workflows present today (`Tamma.ElsaServer/Workflows/`)

Count: **30 code-first workflow classes.**

| Workflow | Scope today | Notes |
|---|---|---|
| `AdlOrchestratorWorkflow` | Cross-tenant (runs anywhere) | Issue-selection loop. Overlaps with the TS engine's role. |
| `SingleIssueCycleWorkflow` | Tenant engine work | Processes one work item validate → merge. |
| `IssueTriageWorkflow` / `TriageItemCycleWorkflow` / `TriageContextGatheringWorkflow` / `TriagePanelReviewWorkflow` / `TriagePODecisionWorkflow` | Tenant engine | Panel review + PO decision for a tenant's backlog. |
| `LlmCallWorkflow` | Tenant engine | Universal LLM call, budget, circuit breaker, provider chain. |
| `ContextGatheringWorkflow` | Tenant engine | Pulls repo / history / findings context. |
| `PlanGenerationWorkflow` / `PlanReviewWorkflow` | Tenant engine | |
| `TaskCreationWorkflow` / `TaskReviewWorkflow` | Tenant engine | |
| `TddWorkflow` / `TddWithDebugRetryWorkflow` | Tenant engine | |
| `TestingWorkflow` | Tenant engine | |
| `DebuggingWorkflow` / `BlockerDiagnosisWorkflow` | Tenant engine | |
| `BranchCreationWorkflow` / `PullRequestWorkflow` / `MergeApprovalWorkflow` / `MergeWorkflow` | Tenant engine (touches tenant's repo) | |
| `CiWithDebugRetryWorkflow` | Tenant engine | |
| `CodeReviewWorkflow` / `ReviewFixWorkflow` | Tenant engine | |
| `AssessmentWorkflow` | Tenant engine | |
| `DeploymentPipelineWorkflow` | Tenant engine | QA / UAT / Prod stages. |
| `MentorshipWorkflow` | Tenant engine | 28-state mentorship loop. |
| `UpdateIssueStatusWorkflow` | Tenant engine | |

### DbContext story today

- **Elsa's own DbContexts** (management + runtime + agents) → the `tamma` shared DB. One set of tables serves every tenant.
- **`TammaDbContext`** (`apps/tamma-elsa/src/Tamma.Data/TammaDbContext.cs`) mirrors `workflow_definitions` and `workflow_instances` into the Tamma side alongside domain entities (`Tenant`, `User`, `DomainEvent`, `QueuedTask`, etc.). This is the table the Tamma API reads — **not** Elsa's own tables. The mirror is written by a `WorkflowSeeder` hosted service plus application code on dispatch.
- Tenant scoping on the Tamma mirror is by `TenantId` column + EF Core global query filters driven by `ITenantContext`. **Elsa's own tables have no tenancy column** — tenant identity today is smuggled inside workflow variables (`tenantId`).

### Consequences of the single-instance model (why we're splitting)

1. One runaway tenant workflow can consume the shared runtime's thread pool / DB connections.
2. Bookmarks and workflow instances for all tenants share indexes — query latency degrades with combined tenant volume.
3. The platform cannot run a workflow **to create a tenant** — `CreateTenantWorkflow` would need a tenant context that doesn't exist yet. Today tenant creation is imperative C# in `Tamma.Api` and does not emit an audit trail through Elsa.
4. No blast-radius isolation. A schema migration to Elsa's runtime tables affects every tenant simultaneously.

---

## 2. The two-tier proposal

### 2.1 Global Elsa

- **One** instance, shared by all tenants, deployed as a container named `elsa-global`.
- Own DbContext connected to the **control-plane Elsa DB** (`tamma_control_elsa`, assumed from `01-control-plane-split.md`). Schema: Elsa's standard workflow-management + runtime + agents tables.
- Hosts only workflows whose scope is **the platform**, not any one tenant:
  - `CreateTenantWorkflow` — provisions a new tenant: creates tenant DBs (app + elsa), starts/configures the per-tenant Elsa runtime, seeds default agent configs, emits `TENANT.CREATED.SUCCESS` to the control-plane event store.
  - `DeleteTenantWorkflow` — erasure: quiesces tenant engines, drains workflows, tombstones the tenant in control plane, drops tenant DBs after retention window.
  - `OrchestratorWorkflow` — the ported 14-step TS engine loop (see §4). Runs one instance per active tenant, dispatches tenant-scoped sub-workflows onto the tenant's Elsa.
  - `PlatformAnalyticsRollupWorkflow` — nightly cross-tenant metrics aggregation (placeholder for future).
  - `PlatformHealthSweepWorkflow` — scheduled ping across tenant engines, writes results to control-plane health table.
- Does **not** host `LlmCallWorkflow`, `MentorshipWorkflow`, `SingleIssueCycleWorkflow`, or any of the tenant engine workflows. Those are not registered in its DI at all.

### 2.2 Per-tenant Elsa

- Logical identity: one Elsa runtime **per tenant**. Physical deployment: see §6.
- DbContext connects to `tamma_tenant_<id>_elsa`. Schema: Elsa's standard tables (runtime + management). No tenancy column needed — the database **is** the tenant boundary.
- Hosts **only** tenant-scoped workflows. Every workflow in the current 30-count list that isn't on the global list above lives here.
- Provisioned by `CreateTenantWorkflow` during tenant creation:
  1. Create DB `tamma_tenant_<id>_elsa`.
  2. Run Elsa's EF migrations against it.
  3. Seed tenant-specific agent definitions into the per-tenant `AgentDefinitions` table.
  4. Register / boot the per-tenant Elsa runtime (deploy-mode specific — see §6).
  5. Seed workflow definitions (either from `Tamma.ElsaServer.dll` code-first classes or from per-tenant authored definitions stored in `tamma_tenant_<id>` application DB).
- Destroyed / drained by `DeleteTenantWorkflow`.

### 2.3 The two tiers, side by side

```
┌─────────────────────────────────────────────────────────────────────┐
│ Global Elsa (one instance)                                          │
│  - CreateTenantWorkflow                                             │
│  - DeleteTenantWorkflow                                             │
│  - OrchestratorWorkflow                                             │
│  - PlatformAnalyticsRollupWorkflow                                  │
│  DB: tamma_control_elsa                                             │
└───────────────────────────────┬─────────────────────────────────────┘
                                │
                                │  dispatch (HTTP via Tamma API)
                                ▼
┌─────────────────────────────────────────────────────────────────────┐
│ Tamma API (resolves tenant → per-tenant Elsa endpoint)              │
└───┬─────────────────────────────────────────────────────────────────┘
    │
    ├─► Tenant A Elsa ──► tamma_tenant_A_elsa
    │      (LlmCall, SingleIssueCycle, Mentorship, Triage, …)
    │
    ├─► Tenant B Elsa ──► tamma_tenant_B_elsa
    │
    └─► Tenant C Elsa ──► tamma_tenant_C_elsa
```

---

## 3. Activity placement

Activities in `Tamma.Activities/` either consume platform-global state (nothing today really does) or tenant-scoped state (most do — they read `tenantId` from workflow variables). With a real tenant DbContext behind them, most become implicitly tenant-scoped.

| Activity (folder/class) | Tier | Rationale |
|---|---|---|
| **Orchestration / ADL** | | |
| `DispatchCycleActivity` | **Global** | Part of the orchestrator loop — dispatches a per-tenant `SingleIssueCycleWorkflow` via Tamma API. Must run in global context. |
| `DispatchAdlActivity` | **Global** | Kicks off ADL cycles across a tenant. Same rationale. |
| `DispatchTriageActivity` | **Global** | Orchestrator decides to trigger tenant triage. |
| `SelectWorkItemActivity` | **Tenant** | Reads tenant's GitHub installation and issue backlog — needs tenant credentials. |
| `FetchUntriagedItemsActivity` | **Tenant** | Same. |
| `InitAdlConfigActivity` | **Tenant** | Reads tenant `AgentConfig`. |
| `CheckLimitsActivity` | **Tenant** | Tenant's concurrency budget. |
| `CooldownActivity` | **Either** | Stateless timer; runs wherever the workflow runs. |
| `ValidateWorkItemActivity` | **Tenant** | Tenant-owned issue. |
| `ApplyTriageResultActivity` | **Tenant** | Writes labels to tenant's repo. |
| `AnalyzeReviewActivity`, `ApplyReviewFixesActivity` | **Tenant** | Tenant's PR. |
| `CreateBranchActivity`, `CreatePullRequestActivity`, `MergePullRequestActivity` | **Tenant** | Tenant's repo credentials. |
| `UpdateIssueStatusActivity` | **Tenant** | Tenant's issue. |
| `ReportCycleResultActivity` | **Global** | Reports back to the orchestrator — must reach orchestrator's bookmarks. Implementation: HTTP callback to global Elsa signal endpoint, NOT a direct in-process write. |
| `SetExitReasonActivity` | **Global** | Orchestrator output. |
| `WaitForCycleCallbackActivity` | **Global** | Orchestrator bookmark. |
| `WaitForMergeApprovalActivity`, `WaitForPlanApprovalActivity`, `WaitForPRApprovalActivity`, `WaitForPRMergedActivity` | **Tenant** | Tenant-scoped human-in-the-loop gates. |
| **LLM Call** (`Tamma.Activities/LlmCall/`) | | |
| `CallLlmActivity`, `CallLlmInlineActivity` | **Tenant** | Tenant provider credentials + budget. |
| `CheckBudgetActivity` | **Tenant** | Tenant `AgentConfig.maxBudgetUsd`. |
| `CheckCircuitBreakerActivity` | **Tenant** | Tenant's `provider_health` row. |
| `CheckLlmConcurrencyActivity`, `ConcurrencyWaitDelayActivity` | **Tenant** | Tenant concurrency limits. |
| `ResolveAgentConfigActivity`, `ResolvePromptFromRegistryActivity`, `ResolveLlmPromptActivity`, `ResolveToolsActivity` | **Tenant** | Reads tenant's `agent_configs`, `prompt_overrides`. |
| `RecordDiagnosticsActivity`, `RecordDiagnosticsInlineActivity` | **Tenant** | Writes to tenant's `provider_diagnostics`. |
| **AI** (`Tamma.Activities/AI/`) | | |
| `ClaudeAnalysisActivity`, `ContextGatheringActivity`, `SuggestionGeneratorActivity` | **Tenant** | Same provider-credential reason. |
| **Assessment** | | |
| `GenerateQuestionsActivity`, `DeliverQuestionsActivity`, `WaitForResponseActivity`, `AnalyzeResponseActivity`, `ClassifyResultActivity`, `UpdateSkillProfileActivity` | **Tenant** | Mentorship per-tenant. |
| **Blocker** | | |
| `ClassifyBlockerActivity`, `CollectCIStatusActivity`, `CollectCommunicationActivity`, `CollectGitActivityActivity`, `CollectInactivityActivity`, `DetectProgressActivity`, `EscalateToSeniorActivity` | **Tenant** | Tenant artifacts. |
| **CodeIndex** | | |
| `UpdateCodeIndexActivity` | **Tenant** | Tenant's indexed repos. |
| **Context** | | |
| `ApplyBudgetActivity`, `AssembleContextActivity`, `FetchFileContentsActivity`, `FetchRecentCommitsActivity`, `FetchSessionHistoryActivity`, `FetchSimilarPatternsActivity`, `FetchStoryMetadataActivity`, `FetchTestResultsActivity`, `ReadRepoConventionsActivity`, `StoreFindingsActivity`, `StoreRoleFindingActivity` | **Tenant** | Tenant DB / repo. |
| **Debug** | | |
| All 12 `Tamma.Activities/Debug/*` | **Tenant** | Tenant artifacts. |
| **Integration** | | |
| `EmailActivity` | **Either** | Global uses platform FROM address; tenant uses tenant FROM. Resolve via tenant context when present; fall back to platform config. |
| `GitHubActivity` | **Tenant** | Tenant's installation token. |
| `JiraActivity`, `SlackActivity` | **Tenant** | Tenant integrations. |
| **Mentorship** | | |
| All `Tamma.Activities/Mentorship/*` (7 classes) | **Tenant** | Tenant mentorship sessions. |
| **Review** | | |
| All `Tamma.Activities/Review/*` (9 classes) | **Tenant** | Tenant PRs. |
| **TDD** | | |
| All `Tamma.Activities/TDD/*` (7 classes) | **Tenant** | Tenant repos. |
| **Testing** | | |
| All `Tamma.Activities/Testing/*` (8 classes) | **Tenant** | Tenant CI. |
| **ToolExecution** | | |
| `IFileSystemTool`, `ParallelToolExecutor`, `ToolLoopEventEmitter` | **Tenant** | Tenant FS sandbox and event sink. |
| **Security** (`Tamma.Activities/Security/`) | | |
| `ActionGate`, `ContentSanitizer`, `ErrorRedactor`, `PromptHardening`, `ProviderAllowlist`, `ToolCallValidator` | **Either** (utility classes, not activities) | These are DI services consumed by activities — register in **both** hosts. Configuration is read from each host's `appsettings`, with tenant-specific overrides resolved through `ITenantContext` when in tenant mode. |

### Shared activity assembly, host-specific registration

Activity binaries stay in **one** assembly (`Tamma.Activities.dll`). Each host chooses which activities to register in its Elsa pipeline:

- **Global Elsa DI:** registers only the activities flagged Global or Either above.
- **Per-tenant Elsa DI:** registers only the activities flagged Tenant or Either.

No code split of `Tamma.Activities` is needed. We add per-activity category attributes (e.g. `[ActivityTier(ActivityTier.Global)]`) for discoverability, but registration is explicit in each `Program.cs` to avoid accidental leakage.

---

## 4. Porting the TS orchestrator to global Elsa

### 4.1 TS engine state map (from `packages/orchestrator/src/engine.ts`)

The current engine has these terminal states in `EngineState`:

```
IDLE → SELECTING_ISSUE → ANALYZING → PLANNING → AWAITING_APPROVAL →
  (branch-create) → IMPLEMENTING → CREATING_PR → MONITORING → MERGING →
  (close issue) → IDLE
```

with error branches recording `EngineEventType.ERROR_OCCURRED` at any step and `ERROR` as a transient terminal state. Event emission (`recordEvent`) happens around every transition into `IEventStore`.

### 4.2 `OrchestratorWorkflow` — global Elsa workflow

**Definition ID:** `orchestrator`
**Tier:** Global
**Lives in:** `apps/tamma-elsa/src/Tamma.ElsaServer.Global/Workflows/OrchestratorWorkflow.cs` (new folder structure — see §6)

Top-level shape (Flowchart):

```
Start
  → ResolveTenantActivity            (input: tenantId)
  → SelectIssueDispatchActivity      (calls tenant Elsa → SelectIssueWorkflow)
  → [IssueSelected?]
       No  → CooldownActivity → Start                     (loop)
       Yes → AnalyzeIssueDispatchActivity                 (tenant)
  → GeneratePlanDispatchActivity                          (tenant)
  → AwaitApprovalActivity                                 (bookmark; signal from tenant-side UI or auto-approve)
  → CreateBranchDispatchActivity                          (tenant)
  → ImplementCodeDispatchActivity                         (tenant, long-running)
  → CreatePRDispatchActivity                              (tenant)
  → MonitorAndMergeDispatchActivity                       (tenant, long-running, emits progress)
  → RecordCompletionActivity                              (global event store)
  → CooldownActivity → Start                              (loop for next issue)

On any dispatch failure → RecordErrorActivity → CooldownActivity → Start
```

### 4.3 Tenant-interaction pattern: option (a), HTTP via Tamma API

We chose to have the orchestrator dispatch tenant work **through the Tamma API**, not by calling per-tenant Elsa endpoints directly.

Why:

- **Credentials lookup is centralized.** The Tamma API already holds tenant GitHub installation tokens, provider secrets, and resolves `ITenantContext`. Reimplementing that resolution in Elsa activity code is duplication.
- **Elsa activities don't need to know per-tenant Elsa endpoints.** That list is volatile (tenants come and go); keeping it behind the API means no activity code churn when a tenant moves from shared to dedicated Elsa (see §6).
- **Audit.** Every cross-tier dispatch hits one logging path in the API.

The pattern, per activity:

```
GlobalOrchestratorActivity
    ↓ POST /api/v1/tenants/{id}/workflows
Tamma API
  resolves tenantId → per-tenant Elsa endpoint (control plane lookup)
    ↓ POST {tenantElsa}/elsa/api/workflow-definitions/by-name/{name}/execute
Tenant Elsa
```

The orchestrator treats each dispatch as **synchronous kickoff, asynchronous completion**: the API returns a `workflowInstanceId` immediately (HTTP 202); the orchestrator either polls or bookmarks and waits for a callback signal.

### 4.4 Long-running steps: bookmarks and callbacks

`ImplementCodeDispatchActivity` and `MonitorAndMergeDispatchActivity` can run for minutes to hours. The orchestrator must not block on them.

Pattern:

1. Dispatch activity returns immediately with `workflowInstanceId`.
2. Orchestrator workflow bookmarks a signal (`cycle-complete-{instanceId}`).
3. Tenant-side workflow, on completion (success or failure), calls the orchestrator's callback endpoint: `POST /api/v1/orchestrator/callbacks/{instanceId}` with a payload `{ status, result, error? }`.
4. Tamma API forwards the signal to global Elsa: `POST {globalElsa}/elsa/api/workflow-instances/{instanceId}/signals/cycle-complete`.
5. Orchestrator resumes.

This mirrors the existing `WaitForCycleCallbackActivity` but redirected to cross-tier.

### 4.5 Tenant iteration

The orchestrator workflow is **per-tenant-per-active-engine**. When a tenant enables autonomous development, the Tamma API starts one `OrchestratorWorkflow` instance on global Elsa with that tenant's ID. Multiple tenants → multiple concurrent orchestrator instances on the same global Elsa. Each instance polls only its own tenant.

We explicitly do **not** run a single orchestrator that iterates over all tenants — that would couple all tenants' issue loops into one Elsa workflow and make cancellation/restart harder.

---

## 5. Cross-tier communication

### 5.1 Global → tenant (dispatch)

Transport: **HTTP via Tamma API.**

Justification over direct HTTP to tenant Elsa:

- Endpoint resolution lives in the API (already has `ITenantContext` + control plane).
- API can short-circuit on tenant quiescence, maintenance mode, or billing state.
- API enriches the dispatch with tenant headers (`X-Tenant-Id`, trace/correlation ID).

Justification over RabbitMQ (also in stack):

- **Workflow dispatch is request/reply-ish** — the caller wants `workflowInstanceId` back immediately to set up its bookmark. HTTP is simpler for that round-trip.
- RabbitMQ adds a second failure mode (broker availability) for no win on the fast path.
- We **do** use RabbitMQ for the completion callback (see §5.2) because that path is genuinely one-way and tolerant of buffering.

Endpoint contract (new on Tamma API):

```
POST /api/v1/tenants/{tenantId}/workflows
  body:    { workflowName, input }
  auth:    service token (global Elsa → API)
  returns: 202 { workflowInstanceId, tenantElsaEndpoint }
```

### 5.2 Tenant → global (completion callback)

Transport: **RabbitMQ** — topic exchange `tamma.orchestrator.callbacks`, routing key `cycle-complete.{instanceId}`.

Justification:

- Tenant Elsa completes work; the orchestrator may have checkpointed, scaled down, or briefly be unavailable during a deploy. A broker-buffered message avoids lost callbacks.
- Rabbit is already in the compose stack — no new infrastructure.
- Decouples tenant Elsa from global Elsa's uptime.

Consumer: Tamma API service `OrchestratorCallbackConsumer` subscribes, signals global Elsa. If global Elsa is down, the message stays in the queue.

### 5.3 Signal callbacks from external systems

Webhooks from GitHub / GitLab etc. continue to hit the Tamma API's existing webhook endpoints. The API enqueues a `QueuedTask` in the **tenant** DB, and the tenant's workflow (via its own signal bookmark) picks it up. Global Elsa is not involved.

---

## 6. Deployment topology

### 6.1 Three options

**A — One container, multiplexed DbContext.** One `tenant-elsa` container, connection-string swaps per request based on `X-Tenant-Id` header.
- Pros: low footprint, one deploy target.
- Cons: defeats the DB-isolation win — a bug in Elsa's internal connection pooling could leak bookmarks across tenants. Noisy-neighbor CPU/RAM still shared. Complex DbContext plumbing.

**B — One container per tenant.** N containers named `tamma-tenant-<id>-elsa`.
- Pros: strongest isolation (CPU, memory, process, DB, log stream).
- Cons: at 100 tenants = 100 containers; at 10k = impossible on single host; needs Kubernetes with pod-per-tenant autoscaling; cold start on provisioning is visible to user.

**C — Hybrid: shared-container-until-threshold.** Tenants share a pool of `tenant-elsa` replicas by default; heavy tenants get promoted to dedicated containers.
- Pros: cost-optimal for long tail of small tenants, isolation for heavy ones.
- Cons: most operationally complex — migration choreography (drain, snapshot bookmarks, restart on dedicated, re-route) is a non-trivial workflow in itself.

### 6.2 Recommendation

**Start with (A), plan for (C) at scale.**

Rationale:

- At **100 tenants initial**, (A) is sufficient and cheap. DbContext-per-tenant is the standard multi-tenant EF Core pattern — Elsa's Entity Framework module accepts a factory. We connect to `tamma_tenant_<id>_elsa` based on an ambient tenant context that a middleware resolves from `X-Tenant-Id` or from the workflow variable at runtime.
- **DB isolation** (which was the primary goal of db-per-tenant) is already delivered by (A) — bookmarks, definitions, and activity execution data all live in the per-tenant DB. The container is just a runtime; it carries no per-tenant state between requests.
- Noisy-neighbor risk is bounded by horizontal replicas (2-4 `tenant-elsa` pods behind a load balancer) and per-tenant concurrency limits in the orchestrator.
- At **10k tenants**, (C) becomes necessary. The key signal to promote a tenant: sustained workflow runtime > threshold (e.g. >10% of a replica's CPU for >1h). Migration is:
  1. Pause orchestrator for the tenant.
  2. Wait for in-flight tenant workflows to drain (Elsa bookmarks persist to DB, so nothing is lost even on hard cutover — but clean drain is safer).
  3. Start a dedicated tenant container pointed at the same DB.
  4. Update control plane `tenant.elsaEndpoint` field.
  5. Resume orchestrator.

Because the DB is the source of truth and the runtime is stateless, (A) → (C) is a runtime-only change, not a data migration.

### 6.3 Deployment manifests (sketch level — no YAML)

**Docker Compose (local / small prod, option A):**

```
services:
  elsa-global:
    image: tamma/elsa-global
    depends_on: [postgres]
    env: ConnectionStrings__DefaultConnection=...tamma_control_elsa
    replicas: 1

  tenant-elsa:
    image: tamma/elsa-tenant
    depends_on: [postgres]
    env: ConnectionStrings__TenantTemplate=Server=postgres;Database=tamma_tenant_{tenantId}_elsa;...
    replicas: 2

  tamma-api:
    image: tamma/api
    env:
      GLOBAL_ELSA_URL=http://elsa-global:5000
      TENANT_ELSA_URL=http://tenant-elsa:5000
      # tenant routing is resolved via control-plane tenant.elsaEndpoint column
```

The old single `elsa-server` container is retired. The existing `elsa-studio` container points at both tiers (it already talks to multiple back-ends via its own workspace config).

**Kubernetes (scale path, option C):**

- `Deployment: elsa-global` — 1-2 replicas, own Service/Ingress.
- `Deployment: tenant-elsa-shared` — N replicas, HPA on CPU/memory. Service/Ingress path `/tenant/*`.
- `Deployment: tamma-tenant-<id>-elsa` — created by `CreateTenantWorkflow` via Kubernetes API when a tenant crosses the promotion threshold. Owned by an operator CRD (`TenantElsa`) so delete/update is declarative.
- `Service: tenant-router` — custom or Envoy — routes `X-Tenant-Id` to either the shared deployment or a dedicated one, based on control-plane lookup.

### 6.4 Target scale check

- **100 tenants:** 1 elsa-global replica + 2 tenant-elsa replicas + 1 tamma-api replica + 1 Postgres = ~5 containers, comfortable on the current Hetzner CPX42 (16 GB).
- **1,000 tenants:** 1-2 elsa-global + 6-10 tenant-elsa replicas + DB sized up. Still option (A).
- **10,000 tenants:** option (C) — ~50 shared tenant-elsa replicas + ~20 dedicated tenant containers for heavy customers. Requires Kubernetes. Postgres moved to managed service with per-tenant DB still on one cluster up to PG's per-cluster DB count limit (~1000 recommended) — at that point we re-shard Postgres too, but that's a separate document.

---

## 7. Elsa DB schema per tenant

Each tenant's Elsa instance uses **Elsa's standard schema** inside `tamma_tenant_<id>_elsa`. Elsa's EF migrations produce tables:

- `WorkflowDefinitions`, `WorkflowDefinitionsIndex`
- `WorkflowInstances`, `WorkflowInstancesIndex`
- `ActivityExecutions`
- `Bookmarks`
- `Triggers`
- `WorkflowExecutionLogRecords`
- Agents module: `AgentDefinitions`, `ApiKeysDefinitions`, `ServicesDefinitions`

### 7.1 Relationship to `TammaDbContext.WorkflowDefinitions` / `WorkflowInstances`

Today these tables in the Tamma app DB are a **mirror** of the Elsa state. With the two-tier split, two changes:

1. **Remove the mirror as a source of truth for dispatch.** Dispatch now goes through Tamma API → Tamma API calls Elsa's own API. The mirror becomes purely a **read model** for the dashboard.
2. **Keep it, rename responsibility.** Maintain it as a projection updated from Elsa events:
   - When Elsa fires `WorkflowInstance.Started` → append to `TammaDbContext.WorkflowInstances`.
   - When Elsa fires `WorkflowInstance.Completed` → update row.
   - Decouples the dashboard's query latency from Elsa's runtime tables, which can get large.

Syncing mechanism: an Elsa `IWorkflowLifecycleEventHandler` in each per-tenant Elsa process writes to the tenant's `TammaDbContext`. **Same DB, same transaction** — no distributed transaction needed.

### 7.2 Propagating to `domain_events` (audit trail)

Yes. On `WorkflowInstance.Completed` (success or failure), the same lifecycle handler appends a `domain_events` row with:

```
type:     WORKFLOW.COMPLETED.SUCCESS | WORKFLOW.COMPLETED.FAILED
tenantId: (from ITenantContext)
tags:     { workflowName, workflowInstanceId, definitionId, issueId?, prId? }
data:     { durationMs, finalState, outputs }
```

This keeps the existing DCB event-sourcing audit promise intact after the split. Note: lifecycle events **per activity** are opt-in via `WorkflowExecutionLogRecords` which Elsa already writes — we do not duplicate those into `domain_events` unless explicitly needed, to avoid doubling write volume.

### 7.3 Elsa migrations vs tenant app DB migrations

These are **versioned independently**:

- Elsa migrations are owned by the Elsa NuGet packages and advance with Elsa version upgrades. They run at Elsa startup against the per-tenant `_elsa` DB (`ef.RunMigrations = true` — matches today's `Program.cs`).
- Tamma app migrations are in `Tamma.Data/Migrations/` and run against `tamma_tenant_<id>` (the app DB, not the Elsa DB).

Having them in **separate databases** makes the independence mechanical: an Elsa upgrade does not require us to also bump app migrations, and vice versa.

A tenant's Elsa DB can be ahead of another tenant's Elsa DB (rolling upgrade). Since nothing cross-references tenant Elsa DBs, this is safe.

---

## 8. Versioning and hot-reload

### 8.1 Global workflows

`CreateTenantWorkflow`, `DeleteTenantWorkflow`, `OrchestratorWorkflow`, platform rollups — these are code-first `WorkflowBase` subclasses compiled into the `elsa-global` container image. Upgrade path:

1. Merge code change → CI builds new image.
2. Deploy new `elsa-global` image (rolling restart of its 1-2 replicas).
3. `WorkflowSeeder` on startup publishes new workflow definition versions (`WorkflowVersions.ComputedVersion` — existing pattern in `WorkflowVersions.cs`).
4. In-flight workflow instances continue on their old version (Elsa supports parallel versions). New dispatches get the new version.

No hot-reload — this is deploy-time. Acceptable because global workflows change rarely.

### 8.2 Per-tenant workflows

Two sub-categories:

**a. Built-in tenant workflows** (`LlmCallWorkflow`, `SingleIssueCycleWorkflow`, `MentorshipWorkflow`, etc. — the Tamma-authored ones):
- Shipped as code-first classes in the `elsa-tenant` container image.
- Upgraded the same way as global workflows — rolling restart of tenant-elsa replicas.
- Definition published per tenant on first run by `WorkflowSeeder` (existing pattern). Per-tenant versioning → each tenant's `_elsa` DB gets a row per definition per version.

**b. Tenant-authored workflows** (future — user writes a custom workflow via Elsa Studio):
- Stored in tenant's app DB `workflow_definitions` table (already exists in `TammaDbContext`). `Version` column already present.
- Loaded at runtime by the tenant's Elsa via a custom `IWorkflowDefinitionProvider` that reads from `tamma_tenant_<id>`.
- CRUD via Tamma API (`POST /api/v1/tenants/{id}/workflow-definitions`). On write, bump `Version`.
- Hot-reload: the provider is invalidated on definition update (pub-sub notification via Rabbit or simple polling with `UpdatedAt`).

Elsa supports this pattern natively via custom `IWorkflowDefinitionStore` / `IWorkflowDefinitionProvider` — we register the custom provider **only in the tenant-elsa host**, not the global one.

---

## 9. Bootstrapping sequence

### 9.1 Fresh deploy (cluster cold start)

1. **Postgres** starts.
2. **Control-plane DB** (`tamma_control`) migrated. Seeded with default platform config.
3. **Control-plane Elsa DB** (`tamma_control_elsa`) migrated (Elsa's own migrations).
4. **`elsa-global` container** starts. On startup:
   - Runs Elsa runtime + management migrations (idempotent — already-migrated DBs are no-op).
   - `WorkflowSeeder` publishes `CreateTenantWorkflow`, `DeleteTenantWorkflow`, `OrchestratorWorkflow` definitions.
5. **`tamma-api` container** starts. Reads tenant list from control-plane DB. For each existing tenant, resolves `tenant.elsaEndpoint`.
6. **For each existing tenant:**
   - If their `_elsa` DB exists and their tenant-elsa container is up, nothing to do.
   - If their `_elsa` DB was missing (e.g. new env): Tamma API dispatches `CreateTenantWorkflow` on global Elsa for that tenant. The workflow provisions the DB, runs Elsa migrations, seeds agent/workflow definitions, and (in deploy-mode (A)) registers the tenant against the shared tenant-elsa pool.
7. **Orchestrator activation** (optional, per tenant). For tenants with `autonomousMode=true`, Tamma API dispatches an `OrchestratorWorkflow` instance per tenant on global Elsa. Each picks up from its last checkpoint (if any) via Elsa bookmarks.

### 9.2 If global Elsa is down during tenant registration

The user's question: does `POST /register` block, or return 202?

**Answer: return 202 (accepted, provisioning), with status polling.**

Flow:

1. `POST /api/v1/tenants` (new tenant registration).
2. Tamma API **does not** block on Elsa. It:
   - Writes tenant row to control plane with `status = 'provisioning'`.
   - Creates the tenant app DB and `_elsa` DB synchronously (direct SQL, no Elsa involvement). These are cheap.
   - Enqueues a `CreateTenantFollowup` message to RabbitMQ.
   - Returns 202 with `{ tenantId, status: 'provisioning', statusUrl }`.
3. A Tamma API background consumer picks up the message, attempts to dispatch `CreateTenantWorkflow` on global Elsa. If global Elsa is down, the message stays in the queue with retry.
4. When global Elsa comes back, the workflow runs: seeds agent defs, registers with tenant-elsa pool, updates `tenant.status = 'active'`.
5. Client polls `GET /api/v1/tenants/{id}` for `status = 'active'`.

This means: tenant creation is robust to global Elsa outages. The tenant can even log in and view a "provisioning" screen while the workflow catches up. The alternative — blocking `/register` on Elsa uptime — is unacceptable from the user's directive: *"first creation shouldn't depend on tenant db, while a global elsa workflow kicks in to create tenant objects and resources, elsa server, etc"*.

### 9.3 Per-tenant Elsa migrations timing

Tenant `_elsa` migrations run in one of two places:

- **At DB creation** (step 6 of 9.1, or step 2 of 9.2): Tamma API runs Elsa's migration SQL directly using the Elsa EF design-time model. This is what lets the tenant-elsa pool pick up the DB without re-migrating.
- **At tenant-elsa startup with `RunMigrations = true`** (backup — idempotent). This is a safety net; it lets a tenant-elsa replica that encounters an unmigrated DB fix it automatically. Matches current `Program.cs` behavior.

Both paths converge to a migrated DB. No ordering problem.

---

## 10. Observability

### 10.1 Structured log fields

Every log line from any Elsa host MUST carry:

- `service` — `elsa-global` or `elsa-tenant` (existing field; was `tamma-elsa`).
- `elsa_instance` — `global` or `tenant:<id>` (new field). For shared tenant-elsa containers, derived from the active `ITenantContext` on the request.
- `tenantId` — duplicated at top level (for easy filtering in OpenSearch).
- `workflow_instance_id` — current workflow correlation ID.
- `workflow_name` — definition ID.
- `correlation_id` — upstream correlation from Tamma API (header `X-Correlation-Id`).

The Serilog config in the current `Program.cs` already enriches with `service` and `environment`. We extend the enricher with `ElsaInstanceEnricher` that reads tenant context from `AsyncLocal<TenantContext>`.

### 10.2 Correlation flow

```
User request → Tamma API                        X-Correlation-Id: abc-123
             → POST /tenants/{id}/workflows
             → Tenant Elsa                      X-Correlation-Id: abc-123, X-Tenant-Id: t-xyz
             → WorkflowInstanceStarted          correlation_id=abc-123, tenantId=t-xyz, elsa_instance=tenant:t-xyz
             → Activity logs                    ↑ same
             → WorkflowInstanceCompleted        ↑ same
             → RabbitMQ callback                correlation_id=abc-123 in message headers
             → Tamma API consumer
             → POST signal to global Elsa       X-Correlation-Id: abc-123
             → OrchestratorWorkflow resumes     correlation_id=abc-123, tenantId=t-xyz, elsa_instance=global
```

OpenSearch query `correlation_id:abc-123` returns the full cross-tier trace.

### 10.3 Metrics

Each Elsa host exposes `/metrics` (Prometheus scrape). Metrics:

| Metric | Global | Per-tenant | Notes |
|---|---|---|---|
| `elsa_workflow_instances_active` | total | per tenant | gauge |
| `elsa_workflow_instances_started_total` | total | per tenant | counter |
| `elsa_workflow_instances_failed_total` | total | per tenant | counter |
| `elsa_activity_execution_duration_ms` | per activity | per activity + tenant | histogram |
| `elsa_bookmark_queue_depth` | total | per tenant | gauge |
| `orchestrator_cycles_completed_total` | per tenant | - | counter, global-only |
| `orchestrator_cycle_errors_total` | per tenant | - | counter, global-only |

Per-tenant labels (`tenantId=…`) are added only in per-tenant scope. Global never labels with tenantId except when labeling orchestrator metrics.

### 10.4 Control-plane rollup

Per-tenant Elsa instances push a **summary heartbeat** to the control plane every 30s: `{ tenantId, activeInstances, bookmarkDepth, failureRate1m }`. Stored in `tamma_control.tenant_elsa_health` (new table, part of 01-control-plane-split). The platform team's single dashboard pane reads this table and flags tenants with anomalies.

This closes the loop: we don't need to query every tenant's Postgres to know the fleet is healthy.

---

## 11. What is explicitly out of scope for this wave

- Writing the Elsa workflows / activity registrations — follow-up implementation.
- Choosing the inter-service auth mechanism between global Elsa and Tamma API (likely service token from the existing auth system — documented in 01-control-plane-split).
- Replacing Elsa with a different workflow engine — user has committed to Elsa.
- Sharding Postgres itself — `_elsa` DBs stay in one cluster up to ~1000 tenants; beyond that is a separate design.
- GDPR erasure timing details for `DeleteTenantWorkflow` — covered by compliance spec elsewhere.
- Elsa Studio UI multi-tier authoring — Studio today points at one Elsa endpoint; we will need a tenant selector, but the UI design lives in the studio/frontend plan.

---

## 12. Open decisions (for follow-up)

1. **Should `ReportCycleResultActivity` live in the Activities assembly at all, or become a Tamma API client inside global Elsa only?** Leaning: keep in assembly, have the activity class take `ITammaApiClient` and `IRabbitPublisher` via DI, and let the global host wire both (tenant hosts don't register this activity).
2. **Orchestrator per tenant vs. orchestrator singleton with tenant-fanout.** Doc recommends per-tenant instances. Revisit if Elsa runtime cost per instance becomes problematic (unlikely at 100 tenants).
3. **Where do tenant-authored workflow definitions live in deploy-mode (A)?** Proposed: tenant app DB. Alternative: promote to tenant `_elsa` DB in an Elsa-native table. Tenant app DB is less coupled to Elsa internals, recommended.
4. **Elsa Studio multi-tenancy.** Single studio container with a tenant dropdown, or studio-per-tenant? Deferred to the studio plan.

---

## Summary of key decisions

| Decision | Choice |
|---|---|
| **Global Elsa hosts** | `CreateTenantWorkflow`, `DeleteTenantWorkflow`, `OrchestratorWorkflow` (+ platform crons) |
| **Tenant Elsa hosts** | Everything else (27 of 30 current workflows) |
| **Activities assembly** | Stays unified; per-host registration selects tier |
| **Orchestrator port target** | Elsa `OrchestratorWorkflow` on global, one instance per tenant |
| **Cross-tier dispatch** | HTTP via Tamma API (global → tenant) |
| **Cross-tier completion** | RabbitMQ (tenant → global) |
| **Deploy mode starting** | (A) shared tenant-elsa container pool with per-tenant DbContext; migrate to (C) hybrid at scale |
| **Tenant Elsa DB** | `tamma_tenant_<id>_elsa`, Elsa standard schema, migrations at DB creation + startup safety net |
| **Tenant DB → Elsa DB ratio** | 1:1, same Postgres cluster up to ~1000 tenants |
| **TammaDbContext workflow tables** | Demoted to read-model / projection, kept for dashboard query speed |
| **`POST /register` when global Elsa is down** | 202 accepted, provisioning state, RabbitMQ-buffered workflow dispatch |
| **Observability** | Structured logs with `elsa_instance` and `tenantId` fields; correlation IDs propagate through RabbitMQ headers; control-plane heartbeat table for fleet view |
