# Epic 44: Native Work Tracking — the mutable system of record for projects, work items, boards and iterations

## Overview

Tamma plans work in three places and tracks it in none.

1. **Its own work** lives in `docs/sprint-status.yaml` — 689 lines of hand-maintained YAML declaring
   `tracking_system: file-system` and `story_location: {project-root}/docs/stories` (`:44-45`), over
   45 epic directories and 914 markdown files. **Nothing programmatic reads it**: a grep for
   `sprint-status` / `sprint_status` / `sprintStatus` across `apps/` and `packages/` `.cs`/`.ts`/`.tsx`
   returns zero hits. Its only machine consumer is `apps/wiki-site/scripts/sync-content.ts:273-280`,
   which publishes `docs/stories/` to wiki.tamma.dev as prose.

2. **Customer work** is a GitHub issue, reached label-first by `SelectWorkItemActivity`
   (`Tamma.Activities/ADL/SelectWorkItemActivity.cs:36`) over `GET /api/engine/issues`
   (`Tamma.Api/Program.cs:2823` → `EngineEndpoints.cs:689-707`). **There is no local copy of it
   anywhere** — no `issues` table in `TenantDbContext` (DbSets `:52-102`) or `ControlPlaneDbContext`
   (`:440-779`), no mirror, no cache.

3. **Work the platform itself generates** — `DecompositionTask`
   (`Tamma.Core/Documents/Types/Decomposition.cs:29`: `id`, `title`, `description`,
   `acceptanceCriteria`, `estimateHours`, `complexity`, `dependsOn`) and `PlanTask`
   (`Types/Plan.cs:12`: `id`, `description`, `files`, `dependsOn`, `testing`) — are *already* work
   items in every respect except identity. They live inside a JSONB document body, have no row, no
   status, no assignee, and no way to be listed, filtered, reordered or reported on.

**What this epic builds:** the mutable, queryable, event-sourced record those three are all reaching
for — projects, work items with a bounded kind hierarchy, a closed status vocabulary, assignment,
ranking, iterations, and a board — as first-class tenant data, plus the seams that let the existing
autonomous loop take work off it and the Epic 41 planning workflows write their accepted proposals
into it.

**The product requirement:** task/story management built into Tamma, Linear-style — projects, epics,
stories/issues, statuses, assignment, boards, sprints/cycles — rather than only consuming issues from
an external platform.

---

## Why there is almost nothing to extend

Nine things in the tree look like a work item. None is one. The table is the whole case for a new
model rather than an extension.

| Candidate | Where | Verdict |
|---|---|---|
| `WorkItem` POCO | `Tamma.Activities/ADL/SelectWorkItemActivity.cs:279-291` | Transient DTO mirroring a GitHub issue. No PK, no EF mapping, no DbSet, no state. Serialized to a workflow variable at `:149` and discarded. **And its live path is broken** — `:186`/`:220` deserialize `List<WorkItem>` from a body `EngineEndpoints.cs:706` returns as `{ issues, total }`; the `JsonException` is swallowed at `:229-232`, so the non-mock path *always* takes `NothingFound`. Same bug at `FetchUntriagedItemsActivity.cs:92-93`. Only `SimulateCandidates()` (`:254-265`) functions. |
| `Story` entity + `stories` table | `Tamma.Core/Entities/Story.cs:8`; `TammaModelConfiguration.cs:2390-2410` | A real table, in `TenantDbContext:102` and `ControlPlaneDbContext:779` since `20260610013731_InitialTenant`. But it is the **mentorship-simulation** schema (`Story.cs:6`: "to be completed during mentorship"), **has no status column at all**, uses an incompatible 1–5 int complexity scale (`ComplexityLevels`, `:56-72`), and its `JiraTicketId` (`:41`) is not even mapped to a column. Dormant. **The type name `Story` is taken.** |
| `Issue` record | `Tamma.Platforms.Abstractions/Models/Issue.cs:7-13` | Six fields, `IssueState { Open, Closed }`. **Dead code** — never returned by any `IGitPlatformClient` method and never constructed anywhere in `src/`. Its own doc (`:4-5`) concedes "We don't surface assignee or milestone in 31-1". |
| Triage vocabularies | `Tamma.Core/Documents/Types/TriageDecision.cs:14-49` | **Genuinely reusable.** Four count-pinned `[Wire]` enums — `TriagePriority` (urgent/high/normal/low), `TriageIssueType` (bug/feature/chore/question/security/docs), `TriageComplexity` (trivial/simple/medium/complex/epic), `TriageAutomation` — with an alias-aware parser (`:52-109`) and tests (`TriageDecisionTypeTests.cs:34-43`). But they are referenced **nowhere outside their own file and tests**; the runtime triage path uses raw strings via `TriagePoDecisionHelper.cs:39`, and the shipped `triage-intake` prompt teaches a *different* vocabulary (P0..P3/severity/ownerRole) that gets clamped away — recorded in-code at `TriageDecision.cs:212-217`. |
| `TaskRef` / `TaskAssigned` | `Tamma.Api/Services/Access/ITaskAudienceResolver.cs:34`; `Tamma.Core/Documents/Channels/ChannelMessages.cs:58-67` | A *task* in Epic 39's vocabulary is **a human decision on a document revision**: `TaskAssigned` carries `decisionSessionId`, `documentTypeKey`, `documentId`, `autonomyLevel`. `TaskRef` is a coordinate `(tenant, initiator, repo, issue)` with no title, status, priority or order. Nothing in `src/` ever constructs a `TaskAssigned`; its `TaskId` is a bare `Guid` with no backing table. |
| `platform_queued_tasks` / `queued_tasks` | `Tamma.Data/Entities/PlatformQueuedTask.cs:14`, `QueuedTask.cs:15` | Infrastructure queues. Eight task types, all `provisioning.*` / `billing.*` / `tenant.move` / `RETIRE_SECRET_VERSION` / `github.*` webhook routing. No title, assignee, priority or user-visible state. `FOR UPDATE SKIP LOCKED` reservation (`PlatformQueuedTaskRepository.cs:91-97`). Not user work. |
| `TASK.*` DCB family | — | **Does not exist.** `Tamma.Activities/Documents/ChannelEvents.cs:6-7` reserves it explicitly for Story 39-20. The only TASK-bearing constants are `AGENT.TASK.{SUCCESS,FAILED,PARTIAL}` (`AgentTrailEventTypes.cs:20,23,27`) — LLM agent-run records. |
| `ITaskAudienceResolver` | `Tamma.Api/Services/Access/ITaskAudienceResolver.cs:45-56` | Interface real, DI-registered (`Program.cs:446-447`). Its only implementation is a self-declared fail-closed stub, and its sole production consumer `ChannelOutboxService.cs:143` **hardcodes `InitiatorUserId: null`** — so `EligibleAudienceAsync` returns empty unconditionally and task fan-out is a **total no-op in production today**. Nothing may be built on it working. |
| `DocumentInstanceStatus` | `Tamma.Core/Documents/Store/DocumentInstanceStatus.cs:20-29` | The most board-like state machine in the repo — 7 `[Wire]` members, count-pinned, mirrored by a DB CHECK `ck_document_instances_status`. **The pattern to copy**, but its subject is a document revision, not a work item. |

**Nothing planned introduces one either.** 39-19's Task View is "an event projection with the
audience resolver as its filter; **no new table**" (its plan `:35`, `:105-107`). 39-20's four tables
are an *access* model (`teams`/`team_members`/`repositories`/`repo_access_grants`), and its own scope
note is explicit: "this story supplies the SET, not the CHOICE" (`39-20:59`). 41-3 and 41-6 produce
`BacklogOrdering` and `SprintPlan` as **immutable accepted documents** in `document_instances` —
proposals about a record that does not exist.

**So this is greenfield with pinned contracts to honour, not a re-presentation of an existing store.**

---

## The model

### 1. Naming — every obvious noun is taken

| Wanted | Taken by | Chosen |
|---|---|---|
| `Task` | `TaskRef`/`TaskAssigned`/`ITaskAudienceResolver` (39-18/39-20), `PlanTask`, `DecompositionTask`, `QueuedTask`, `PlatformQueuedTask`, `TaskKind` (41-29), `TaskStatus` (`TddModels.cs:8`), `/api/tasks` (39-19), `tasks:assign` (39-20), the reserved `TASK.*` family | **`WorkItem`** |
| `Story` | `Tamma.Core/Entities/Story.cs:8` + the live `stories` table | `WorkItemKind.Story` (a kind, not a type) |
| `Issue` | `Tamma.Platforms.Abstractions/Models/Issue.cs:7` | — |
| `Cycle` | `CycleEvents.cs:44-47` (`CYCLE.STARTED/STEP_FAILED/COMPLETED/FAILED`), `SingleIssueCycleWorkflow`, `TriageItemCycleWorkflow`, `CycleExitReason`, `WaitForCycleCallbackActivity` | **`Iteration`** |
| `Sprint` | free, but `SPRINT.*` is claimed by 41-6 for its `SprintPlan` document lifecycle | **`Iteration`** |

Home namespace **`Tamma.Core.Tracking`** — verified free. `Tamma.Core` is the only assembly reachable
from `Tamma.Data`, `Tamma.Activities`, `Tamma.ElsaServer` and `Tamma.Api` alike (zero
`ProjectReference`s), which is why `AgentAction`, `DocumentTypeKey` and Epic 43's `ActionKey` all live
there.

Routes `/api/projects`, `/api/work-items`, `/api/iterations` — all verified absent from `Program.cs`.
Permission `tracker:manage` — verified absent from `Auth/Permissions.cs` (18 keys, `:12-95`).

**One collision is accepted and must be flagged, not silently absorbed:** `TriageComplexity` already
has a `[Wire("epic")]` member (`TriageDecision.cs:39`). `WorkItemKind.Epic` is a *different axis* — a
hierarchy level, not a size estimate. They must never be unified. Likewise `ITERATION.*` as a
top-level event family is free, but `CODE_REVIEW.ITERATION.STARTED` (`CodeReviewEvents.cs:63`) and
`AGENT.ITERATION.*` (`Program.cs:825`) exist as *sub*-segments; a grep for `ITERATION.` returns both.

### 2. `issueId` is the join key — and it costs nothing

This is the single most valuable property of the design.

`DocumentInstance.IssueId` is a **`string`** (`Tamma.Data/Entities/DocumentInstance.cs:37`). DCB
events carry `issueId` inside the `Tags` `jsonb` column (`domain_events`,
`20260610013731_InitialTenant.cs:103`). `TaskRef.IssueId` is `string?`. `TaskAssigned.IssueId` is
`string`. None of them is constrained to a GitHub issue number.

**Therefore a native work item that mints its key into that namespace inherits the entire Epic 39
spine unchanged** — the document store, the lineage API (39-11, `done`), latest-accepted re-entry
(39-10, `done`), the escalation/approval surface (39-8, `done`), the decision inbox (39-19), replay
(4-8) — with zero modification to any of them. No adapter, no dual-write, no translation layer.

Key format: `<PROJECT_KEY>-<n>` (e.g. `TAM-142`), human-readable, unique per project, minted from a
per-project sequence. Stored alongside a UUIDv7 PK (`Tamma.Core/Documents/UuidV7.cs`).

**One honest consequence:** `domain_events.IssueNumber` is `integer NULL`
(`20260610013731_InitialTenant.cs:102`) with two indexes on it (`:462-466`). A non-numeric key cannot
populate it. Native work items leave it `NULL` and are queried through `Tags->>'issueId'`, which is
what `EventRepository.QueryAsync` already does for everything else. Stated so nobody "fixes" it by
widening the column.

### 3. One table, a closed kind vocabulary, and a parenting matrix

```csharp
public enum WorkItemKind {                    public enum WorkItemStatus {
    [Wire("epic")]   Epic,                        [Wire("backlog")]     Backlog,
    [Wire("story")]  Story,                       [Wire("ready")]       Ready,
    [Wire("task")]   Task,                        [Wire("in_progress")] InProgress,
    [Wire("bug")]    Bug,                         [Wire("in_review")]   InReview,
    [Wire("chore")]  Chore,                       [Wire("blocked")]     Blocked,
    [Wire("spike")]  Spike,                       [Wire("done")]        Done,
}                                                 [Wire("cancelled")]   Cancelled,
                                              }
```

**Not one table per level.** Separate `epics`/`stories`/`tasks` tables would mean 3× the CRUD, 3× the
board code and 3× the event families, for levels that differ only in *what may parent what*. The
allowed-parent set is a matrix, and the repo already has that idiom: `RolePhaseMap.cs:43-163` is a
93-cell `(role, action)` eligibility set with a *built*, never hand-maintained, index
(`:170-171`). `TrackerHierarchy` is that shape for `(parentKind, childKind)`.

**Depth is bounded at 3** (`Epic > Story|Bug|Spike > Task|Chore`). Not because deeper is
conceptually wrong but because an unbounded tree makes every list query recursive and every board
render ambiguous. Enforced in the service and pinned by a test.

`Project` is a **separate table** — it owns the key prefix, the key sequence, and the repository
binding. It is not a work item and never appears on a board.

`Iteration` is **orthogonal to the hierarchy**, an FK on the work item, not a level.

`WorkItemStatus` is count-pinned and mirrored by a CHECK constraint —
`DocumentInstanceStatus.cs:12-14` is the exact precedent (7 members, count-pinned, DB CHECK
`ck_document_instances_status` mirroring the wire strings).

**Priority and type reuse the shipped triage vocabularies.** `TriagePriority` and `TriageIssueType`
(`TriageDecision.cs:14-31`) are already `[Wire]`, already alias-parsed, already count-pinned, and
currently used by nothing but their own tests. A tracker giving them a consumer is the cheapest
possible win and removes a dead vocabulary. `TriageComplexity` is **not** adopted — see §1.

### 4. Ranking — a fractional index, not an integer

`Rank` is a lexicographically-sortable string (base-62 midpoint between neighbours). A drag is **one**
`UPDATE`; ordering is `ORDER BY "Rank"` in SQL with no application-side sort.

Rejected: an integer rank (a move rewrites O(n) rows and every board drag becomes a transaction over
the whole column); a float (precision exhausts after ~50 insertions between a fixed pair, which is
one afternoon of grooming).

**One rank per work item, project-scoped** — not one per (project, status). The board column's order
*is* the project rank filtered by status. Two rank columns are two things that can disagree, and
nothing reads the difference.

### 5. Storage: tenant schema — and this epic fixes the migration-reach gap

Every operational tenant table is tenant-resident: `document_instances`
(`20260722180002_AddDocumentInstances`), `channel_outbox` (`20260722211145_AddChannelOutbox`),
`acceptance_rules_overrides` (`20260722011909_AddAcceptanceRulesOverrides`). Work items are
operational tenant data at the highest row count in the system. Control-plane residency would put
every tenant's backlog in one shared table and forfeit the isolation
`tests/Tamma.Api.Tests/Tenancy/SchemaPerTenantMigrationTests.cs:82-86` exists to prove.

**But the migration-reach gap is real, and unlike Epic 43 this epic cannot dodge it.**
`ITenantDbMigrator.MigrateTenantAppAsync` (`Tamma.Data/Abstractions/ITenantDbMigrator.cs:33`, impl
`Pooling/EfTenantDbMigrator.cs:25`) has **exactly two production call sites**, both creation-only:

- `Tamma.Api/Services/Provisioning/TenantProvisioningService.cs:172`, reached only from
  `Middleware/EnsurePersonalTenantMiddleware.cs:176-177` — and it runs *only* when
  `password is not null` (a fresh role); an idempotent re-run skips it (`:167-176`).
- `Tamma.Activities/TenantLifecycle/MigrateTenantDatabaseActivity.cs:53`, step 4 of
  `CreateTenantWorkflow` (`Tamma.ElsaServer/Workflows/CreateTenantWorkflow.cs:158-160`).

There is **no** migrate-all endpoint (`Endpoints/Admin/` has no `migrate` handler), no hosted service,
no queued-task handler. `Program.cs:3278` migrates the *control plane* only. So a new tenant migration
reaches only tenants provisioned after the deploy; every existing tenant gets `42P01` on first read.

Epic 43 avoided this by going control-plane-resident (its README `:238-246`). Epic 44 cannot. So
**Story 44-1 builds the sweep** — an admin route over `tenants` × `LruPooledTenantConnectionResolver`
calling the already-idempotent `EfTenantDbMigrator`. It is ~40 lines, it is the missing piece rather
than a redesign, and it is honestly this epic's to build because this is the first feature that cannot
ship without it.

### 6. Ownership in both operating modes — answered separately

Mode is settled at startup by `TammaModeProvider.Resolve` (`Tamma.Api/Services/PromptStore/TammaMode.cs:67`):
explicit `Tamma:Mode`, else inferred SaaS from `Tamma:TenantSharedSecret` / `ConnectionStrings:ControlPlane`,
else `SingleUser`.

| | single-user | SaaS |
|---|---|---|
| **Who owns a work item** | The sole user, inside the personal tenant auto-provisioned by `EnsurePersonalTenantMiddleware.cs:176`. `CreatedByUserId` and `AssigneeUserId` are always that user. | The **tenant**. `CreatedByUserId`/`AssigneeUserId` are tenant members. |
| **Who may read** | Everything. There is one principal. | Tenant membership in v1. Once 39-20 lands, `ITaskAudienceResolver.CanSeeAsync` narrows it to initiator-or-repo-access. |
| **Who may write** | Any user (sole user owns everything — the `Operating Modes` rule). | `tenant_owner` / `tenant_admin` for project + iteration structure; any `member` for their own work items' status and assignment. |
| **Assignee picker source** | The one user. | `ITaskAudienceResolver.EligibleAudienceAsync(taskRef, roleWire)` — 39-20. Until 39-20 lands the stub returns empty, so v1 falls back to tenant membership and **says so in the UI**, rather than rendering an empty picker. |

**The principal-XOR pattern does not apply to work items, and this is deliberate.** `prompt_overrides`
(`20260610013731_InitialTenant.cs:188`) and `acceptance_rules_overrides` (`:32` of its migration) carry
`ck_*_principal_xor` because a *setting* has exactly one owning principal and the two planes must never
join. A work item is **content**, not configuration: it has a creator, an assignee and a project, all
within one tenant schema, and schema-per-tenant already supplies the isolation the XOR supplies for
CP-resident config tables. Adding a nullable `UserId` column to `work_items` would encode a second
ownership plane that has no reader.

**Where the pattern does apply, this epic uses it exactly:** `tracker_preferences` — default project,
default kind, default board grouping — is genuine per-principal configuration, keyed on `UserId` in
single-user and `TenantId` in SaaS, with the strong XOR form
(`(A NOT NULL AND B NULL) OR (A NULL AND B NOT NULL)`, per `acceptance_rules_overrides`, **not** the
weak `audit_records` form which permits both NULL) and a unique index carrying
`.Annotation("Npgsql:NullsDistinct", false)`. The repository gets the parallel never-joined surfaces
`IAcceptanceRulesRepository.cs:5-12` documents as the rule.

**Deferred with reasons:** per-principal *status-set customization*. A customizable status vocabulary
turns a count-pinned closed enum into open data, which breaks both the CHECK constraint and the board
projection's drift guarantee. v1 ships the fixed 7-member set.

### 7. The board is a query, not a table

No `boards` table. A board is `GET /api/work-items?projectId=…&groupBy=status`, returning ordered
columns. Same posture as `DocumentInstance`'s "read-optimized projection, rebuildable" doc
(`DocumentInstance.cs:7-14`) and as 39-19's D7 ("no new table"). Saved board configurations,
custom filters and swimlane definitions are deferred.

---

## The external-platform relationship: **native-only system of record, one-way import, narrow outbound link. No sync.**

This is the largest decision in the epic and the code makes it for us.

**What the abstraction actually offers.** `Tamma.Platforms.Abstractions/IGitPlatformClient.cs:29`
declares **twelve** methods — `GetRepoAsync:34`, `ListRepoBranchesAsync:40`, `GetFileContentAsync:47`,
`CreateBranchAsync:53`, `OpenPullRequestAsync:59`, `GetPullRequestAsync:65`,
`ListPullRequestFilesAsync:71`, `CreatePullRequestReviewCommentAsync:81`, `MergePullRequestAsync:87`,
`CreateIssueCommentAsync:93`, `RegisterWebhookAsync:101`, `ListAccessibleReposAsync:119`. **Exactly one
touches an issue, and it is a comment write.** There is no `GetIssue`, `ListIssues`, `CreateIssue`,
`UpdateIssue`, `CloseIssue`, no labels, no milestones, no projects, no assignees. The normalized
`Issue` record (`Models/Issue.cs:7`) is dead code.

**What the platform coverage actually is.** `PlatformKind.cs:18-26` declares **six** members, not seven
— there is no plain-Git value at all. Drivers exist for **GitHub** (`GitHubPlatformClient.cs:29`),
**Gitea** (`GiteaPlatformClient.cs:23`), **GitLab** (`GitLabPlatformClient.cs:33`) and **Forgejo** —
which is a 100% delegating wrapper over Gitea (`ForgejoPlatformDriver.cs:34`, `_inner` at `:36`).
**Bitbucket and Azure DevOps have no implementation class**: an enum value, a capability-matrix row
(`PlatformKindCapabilityMatrix.cs:84,94`) and a webhook URL slug (`WebhookEndpoints.cs:338`), nothing
else. `PlatformKind.cs:12-16` says the drivers "land in 31-11 / 31-12".

**What the inbound half would need.** Issue webhooks *are* classified —
`DefaultWebhookEventCategoryMapper.cs:30,42,53` maps GitHub `issues`/`issue_comment`, Gitea/Forgejo
`issues`/`issue_comment` and GitLab `issue`/`note` to `WebhookEventCategory.Issue`
(`PlatformWebhookEvent.cs:128`), and the `opened`/`closed`/`labeled` sub-action is captured
(`WebhookEndpoints.cs:364,373,381`). But **there are zero production `IWebhookHandler`
implementations** — the only ones in the repo are test doubles. An issue webhook today is verified,
deduped, categorized, dispatched to nothing, and reports `dispatched: 0`. The legacy GitHub path
enqueues `github.issues.<action>` into `queued_tasks` (`InstallationRouterService.cs:354-357,556-558`)
with the raw payload and **no observed consumer**.

**So two-way sync would require**, before the first board renders: six new methods on the abstraction
× four existing drivers; two entirely new drivers; the `IWebhookHandler` layer that has never been
built; a conflict-resolution model; a reconciliation sweep; and a mapping for four platform concepts
(assignee, milestone, project, custom state) that three of the six platforms model differently. That
is larger than this entire epic.

**Therefore v1:**

- **Native is the source of truth.** A work item is not a projection of anything external.
- **Inbound is an explicit, one-time import.** `POST /api/projects/{id}/import` pulls open issues for a
  bound repo through the GitHub-only `IGitHubEngineCallbackService.ListIssuesAsync:45` and creates work
  items carrying an `ExternalRef(PlatformKind, RepoFullName, Number, Url)`. A snapshot, not a
  subscription — re-running it skips already-linked numbers.
- **Outbound is limited to what already ships, and only for GitHub**: a comment
  (`IGitHubEngineCallbackService.PostIssueCommentAsync:54`) and label add/remove (`:58`, `:62`), both
  behind an explicit per-project opt-in. No title, body or state write-back.
- **No continuous sync, no webhook consumption, no conflict model.** If the external issue and the work
  item diverge, the work item wins and the divergence is visible in the UI, not reconciled.

Two-way sync across the platform matrix is a separate epic with the abstraction work as its first
half. Pretending otherwise here would ship a feature whose most-advertised property is the one that
does not work.

---

## Boundaries with planned work — precise, and in both directions

None of the overlapping stories is absorbed. Two need a correction, recorded below.

| Planned work | It owns | Epic 44 owns | The boundary |
|---|---|---|---|
| **39-19** Orchestrator Chat + **Task View** (`ready-for-dev`, `sprint-status.yaml:548`) | The **decision inbox**: four task types `acceptance_decision \| review \| approval \| clarification` (plan `:88`), each a suspended 39-8 bookmark; a projection over `TASK.*` ∩ `APPROVAL.REQUESTED` minus completions; **no table** (plan `:35`, `:105-107`); `GET /api/tasks`, `GET /api/tasks/{sessionId}`; `/tasks` in `packages/dashboard-user`. | The **backlog**: mutable work items with title, description, kind, status, assignee, rank, parent, iteration; `/api/work-items`; `/work` in `packages/dashboard`. | **Disjoint nouns that share a word.** A Task-View row means *a workflow is suspended waiting for you*; a work item means *a thing to be done*. A work item can cause Task-View rows (its lifecycle documents suspend at accept gates); a Task-View row can never be a backlog entry — it has no title, rank, assignee-as-person or lifecycle beyond `pending \| completed`. **No change to 39-19.** Recommendation: 39-19 gains one disambiguating sentence, because two features called "tasks" with different meanings in adjacent nav is a support cost. |
| **39-20** Teams, Roles, Repo Access & **Task Routing** (`ready-for-dev`, `:549`) | `teams` / `team_members` / `repositories` / `repo_access_grants`, control-plane resident (its plan D1 `:32`); the real `ITaskAudienceResolver`; `TASK.ASSIGNED/REASSIGNED/COMPLETED`; `ITaskAssignmentService`; `teams:manage`, `repos:manage`, `tasks:assign`. | The *choice* of assignee, and its persistence on the work-item row. | 39-20 states it itself: *"this story supplies the SET, not the CHOICE"* (`39-20:59`). Epic 44 **consumes** `EligibleAudienceAsync` for assignee pickers and `CanSeeAsync` for list filtering, and **builds neither teams nor repo access**. Its `repositories` table is the natural FK for a Project's repo binding — **Epic 44 must not create a second repo registry** (44-1 D4). Hard dependency for the SaaS visibility AC; soft in single-user, where the stub's initiator-only rule is already correct. **Standing warning:** the stub is a total no-op today (`ChannelOutboxService.cs:143` passes `InitiatorUserId: null`), so nothing may assume fan-out works. |
| **41-3** Backlog Prioritization (`drafted [P2]`, `:606`) | The workflow producing an immutable, reviewed, **accepted `BacklogOrdering` document** in `document_instances` — "total order over the referenced item set; every item has a rationale + value/effort estimate; no ties" (41-1b `:32`). | The mutable `Rank` column the ordering is *applied to*. | 41-3 produces a **proposal**; Epic 44 holds the **record**. Epic 44 adds an apply seam `POST /api/work-items/apply-ordering` taking an accepted document id. **41-3 is unchanged** — its AC3 ("readable via the 39-11 store") still holds, and the apply seam is a new consumer, not a rewrite. |
| **41-6** Sprint Planning (`drafted [P2]`, `:609`) | The workflow producing an accepted **`SprintPlan` document**. | The mutable `iterations` table and `WorkItem.IterationId`. | Same shape: apply seam `POST /api/iterations/{id}/apply-plan`. **⚠ Recommend narrowing 41-6 AC3.** It currently reads *"Committed items produce role-scoped Task View entries via 39-20"* (`41-6:45`, with `:32` "the accepted plan seeds Task View assignments per committed item's owner-role"). That conflates *committing an item to a sprint* — a tracker mutation with no human decision pending — with *a suspended workflow gate*, which is the only thing the Task View is. Recommended AC3: **"Committed items are assigned to the iteration in the tracker (Epic 44); a Task-View entry is raised only for the `SprintPlan` document's own acceptance."** Without this, 41-6 either ships an inbox full of rows nobody can act on, or quietly invents a fifth task type. |
| **41-4** Roadmap Shaping (`drafted [P3]`, `:607`) | An audience-tagged **prose** roadmap. | Nothing. | No overlap — a roadmap is narrative, and Epic 44 stores no narrative. (Separately: 41-4 is blocked on 41-1c, whose `prose` type and `Audience` field do not exist in code.) |
| **41-29** Task-Level Flow Router (`drafted [P0]`, `:632`) | `PlanTask.Kind` (a 7-member closed `TaskKind`), a `FlowSwitch` in `SingleIssueCycleWorkflow`'s per-task loop, `ROUTE.*` events. | Nothing in v1. | Disjoint: 41-29's "task" is a `PlanTask` **inside a Plan document body**. **Recommendation: do not materialize `PlanTask`s as work items in v1.** It would double-write every plan, create a second lifecycle for content the 39-11 document store already owns, and make the accepted-document lineage and the tracker two sources of truth for the same rows. Listed under Deferred as a v2 candidate with the reconciliation it would require. |
| **43** The Action Catalog (`contexted`, `:665`) | Governance of every consequential action; a CI-blocking bidirectional drift harness (`epic-43/README.md:186-190`); an `issue-tracking` `ActionGroup` in the 15-member partition (`:104-107`) that is currently near-empty. | Descriptors for its own ~14 mutating routes. | **Epic 44 fills `issue-tracking`.** Ordering matters: if 43-8's ratchet arms first, 44-2 carries a hard dependency; if Epic 44 lands first, 43-3's partition gains real members instead of a placeholder group that its own index-build rule ("throws if any group has zero members", `:109`) would otherwise reject. Recommend 43-3 pre-reserve the keys either way. |

---

## Drift prevention

Three mechanisms, all existing house patterns:

- **`[Wire]` closed vocabularies with count pins.** `WorkItemKind`, `WorkItemStatus` and the parenting
  matrix follow `DocumentTypeKey`/`DocumentInstanceStatus`: `EnumWire<T>`'s static constructor
  (`Tamma.Core/Agents/EnumWire.cs:39-59`) already throws on a missing `[Wire]`, a duplicate wire
  string, or a `[Flags]` enum, and parsing is ordinal/case-sensitive (`:65`) so non-canonical casing in
  persisted data is rejected rather than coerced. Count pins per `TriageDecisionTypeTests.cs:34-37`.
- **CHECK constraints mirroring the wire strings**, per `ck_document_instances_status`
  (`DocumentInstanceStatus.cs:14`), plus a test asserting enum and constraint agree.
- **A built, never hand-maintained parenting index** that throws at startup if a kind has no
  parenting rule — the `RolePhaseMap.cs:170-171` / `PromptFileLoader` fail-loud posture.

**And one mechanism the repo does not have, which this epic must add because it introduces three new
event families.** The survey found **no test asserting `AGGREGATE.ACTION.STATUS` naming or family
completeness** anywhere: `TaxonomyDriftBuildTests.cs:69` covers `(role, action)` pairs,
`ConventionSeedDriftTests.cs:28` covers seed keysets, and `SensitiveActionCatalog`'s accuracy was
verified *by grep at authoring time, not by a test* (`SensitiveActionCatalog.cs:16-28`). With ~300
event-type constants across ~62 prefixes, adding a fourth family unguarded is how the convention rots.

Story 44-5 ships a **ratchet-style** event-name shape test: it reflects over every
`public const string` in every `*Events.cs` / `*EventTypes.cs`, asserts the
`AGGREGATE[.SUB].ACTION.STATUS` shape, and seeds a shrink-only allowlist with the existing violators
(`GATE`/`APPROVAL.GATE` from `MergeApprovalEvents.cs:57-59`, the 2-segment `TRIAGE.LABELS.INVALID`
neighbours, `TOOL_LOOP.*`) — the `KnownContractViolations` discipline already used in
`ContractBindingTests`. Entries may only be removed. It is a small deliverable with repo-wide value,
and it belongs to whoever adds a family.

---

## Where the UI lives — and the honest cost

**`packages/dashboard`, not `packages/dashboard-user`.**

`packages/dashboard` is deployed: compose service `tamma-dashboard`
(`docker/docker-compose.yml:310-319`), built by `docker/Dockerfile.dashboard:18,24,31`, published to
GHCR (`.github/workflows/docker-publish.yml:142,170,185`), pinned and brought up by
`.github/workflows/deploy.yml:140-141,250` with a health loop at `:310`, and reverse-proxied at
`docker/nginx-proxy.conf.template:65-66,164`.

`packages/dashboard-user` **is not deployed at all.** Its complete non-doc footprint is one CI test
line (`.github/workflows/ci.yml:49-50`), an eslint entry (`eslint.config.js:75-76`), a vitest
exclusion (`vitest.config.ts:64`) and a lockfile row. No Dockerfile, no compose service, no image, no
nginx vhost. Its own `AppLayout.tsx:24-35` renders nav links to `/repos`, `/runs` and `/settings` —
**none of which exist in its `App.tsx:41-84`**. Shipping a board there means first building an entire
deployment path.

This creates a stated tension with 39-19, which puts `/chat` and `/tasks` in `dashboard-user`.
**Epic 44 does not resolve it and does not silently absorb it**: the tracker ships in the deployed
dashboard, and whoever lands 39-19 first must fund the `dashboard-user` deployment path. It is an open
question for the product owner (below).

**What does not exist, that a board needs:**

| Needed | State today |
|---|---|
| Grouped / collapsible table | **None.** `components/monitoring/DataTable.tsx:58` is flat — sort `:112`, filter `:82`, paginate `:107`, column-hide `:122`, no grouping. 25 files render a raw `<table>`; none groups rows. |
| Row-level expand/collapse | **None.** Rows are a single `<tr>` with `onRowClick` (`DataTable.tsx:228-249`). The only collapsibles are panel-level `<details>` (`PromptPreview.tsx:52`). |
| Cross-column drag-and-drop | **Partial.** `@dnd-kit` is a dependency and used in exactly one place — `settings/agents/ProviderChainEditor.tsx:40,178-179` — for **vertical list reordering in one container**. Multi-container board DnD has no precedent. |
| Data-fetching library | **None.** No React Query/SWR. ~22 hand-rolled copies of `const API_BASE = …` + a local `fetchJSON<T>`. |
| Grouping logic | One instance: `components/monitoring/events/event-explorer-utils.ts` (`groupByType`, used at `EventExplorerPage.tsx:35,100`). |

Closest existing shapes to model on: `pages/runs/RunsPage.tsx` (DataTable + status filter + StatusBadge
+ row→detail) and `pages/runs/RunDetailPage.tsx`.

**And a gap this story must not widen: `packages/dashboard`'s 449 tests do not run in CI.** The root
`vitest.config.ts:62` excludes `packages/dashboard/**` with a comment deferring to
`pnpm --filter @tamma/dashboard test`, and no workflow contains that line — `ci.yml:50` runs only the
`-user` filter. Neither dashboard is typechecked either (`package.json:25` builds five other packages).
44-6 adds ~9 days of React; it adds the CI line in the same change.

---

## Stories

Sequencing, not separate releases.

| # | Title | Days |
|---|---|---|
| **44-0** | Tracker core: vocabularies, `WorkItemRef`, parenting matrix, rank algebra, fail-loud index | 4 |
| **44-1** | Storage, repositories, tenant migration — and the migrate-all-provisioned-tenants sweep | 6 |
| **44-2** | Work-item & project API, RBAC, `tracker_preferences`, action-catalog descriptors | 5 |
| **44-3** | Hierarchy, ranking, and the `BacklogOrdering` apply seam | 4 |
| **44-4** | Iterations, the board read projection, and the `SprintPlan` apply seam | 4 |
| **44-5** | DCB events (`PROJECT.*` / `WORKITEM.*` / `ITERATION.*`) + the event-name drift ratchet | 4 |
| **44-6** | Tracker UI in `packages/dashboard` — list, board, detail — plus the missing CI test line | 9 |
| **44-7** | Loop integration: native work items as an intake source, the `issueId` join, and the broken selector fix | 5 |
| **44-8** | External link: GitHub import, `ExternalRef`, opt-in outbound comment/label | 4 |
| **44-9** | Dogfood: `docs/sprint-status.yaml` becomes generated from the tracker | 4 |

**Total ~49 days.**

**44-0, 44-1 and 44-2 are the spine**; nothing else starts before 44-1. **44-7 is the story that makes
the tracker matter** — without it the tracker is a second place to write things down, which is exactly
the problem the epic exists to remove; it also carries the fix for the swallowed-`JsonException`
selector bug, which is worth landing regardless. **44-6 is the least reliable estimate in the plan**
for the same reason Epic 43's Story 7 was: three React primitives with no in-repo precedent, and here
one of them is multi-container drag-and-drop.

---

## Deferred (v2 or later — named, with reasons)

- **Two-way platform sync.** Needs issue CRUD on `IGitPlatformClient` (six methods × four drivers), the
  two missing drivers, the `IWebhookHandler` layer that has never been built, and a conflict model.
  A separate epic.
- **Materializing `PlanTask`s as work items** (41-29 reconciliation). Would double-write every plan and
  give the same rows two sources of truth. Needs a decision on which store is authoritative first.
- **Comments on work items.** The DCB stream *is* the activity feed — `WORKITEM.*` plus the existing
  `DOCUMENT.*`/`APPROVAL.*` for the same `issueId` already renders a full timeline. A comments table
  earns its keep only once humans discuss items outside a workflow.
- **Custom fields, saved views, swimlane definitions, per-principal status sets.** Each converts a
  count-pinned closed vocabulary into open data and breaks the CHECK constraint + projection guarantee.
- **Estimation, velocity, burndown.** `EstimateHours` is stored from 44-0; nothing reads it in v1.
  Charts belong with Epic 36 (analytics), not here.
- **Notifications on tracker changes.** 39-18's channels ship (`Api/Hubs/*`, `channel_outbox`), but
  their audience resolver is a no-op stub. Wiring the tracker to a dead resolver would ship silence.
- **Porting the tracker UI into `packages/dashboard-user`.** Blocked on that package having any
  deployment path at all.
- **Jira as a second tracker source.** `IJiraMediationService.cs:14-15` offers get + update ticket
  only, per-tenant BYOK. Real, but a third sync surface before the first is proven.

---

## Dependencies

- **39-11 Document Store & Lineage API** (`done`) — `DocumentInstance.IssueId` being a `string` is the
  join key the whole design rests on. No change required to it.
- **39-20 Teams, Roles, Repo Access** (`ready-for-dev`) — supplies `ITaskAudienceResolver` for assignee
  eligibility and list visibility, and the `repositories` table Epic 44 binds projects to. **Blocking
  for the SaaS visibility AC of 44-2; not blocking in single-user.** 44-1 D4 forbids a second repo
  registry.
- **Epic 43 The Action Catalog** — Epic 44's mutations need descriptors in the `issue-tracking` group.
  Blocking only if 43-8's ratchet arms first; see the boundary table.
- **41-1b New Document Types** (`drafted`) — defines `BacklogOrdering` and `SprintPlan`. The apply seams
  in 44-3/44-4 are written against their shape and are **not blocked**: they take a document id and read
  through `IDocumentInstanceRepository`, so they compile and test against a fixture before 41-1b lands.
- **Existing, no change required:** `EnumWire`/`[Wire]`; `IEventRepository.AppendAsync`
  (`IEventRepository.cs:7`); `EfTenantDbMigrator`; `LruPooledTenantConnectionResolver`;
  `TammaModeProvider`; the acceptance-rules admin-config stack as the shape to copy.
- **Nothing in Epic 40, 41 or 42 blocks this epic.** All three are docs-only (`sprint-status.yaml:555-567`).

---

## Decisions

| | Decision | Rejected |
|---|---|---|
| **D1** | **`WorkItem` / `Iteration` / `Project`** in `Tamma.Core.Tracking`; events `WORKITEM.*` / `ITERATION.*` / `PROJECT.*`; routes `/api/work-items`, `/api/iterations`, `/api/projects`. | `Task`/`Story`/`Issue`/`Cycle`/`Sprint` — every one collides with shipped or reserved names (§1). |
| **D2** | **The work-item key is minted into the existing `issueId` string namespace**, so the entire Epic 39 spine works unchanged. | A separate `workItemId` tag — would fork document lineage, re-entry, the decision inbox and replay into two coordinate systems. |
| **D3** | **Native-only system of record. One-way import, GitHub-only opt-in outbound comment/label. No sync.** | Two-way sync (needs 6 methods × 4 drivers + 2 missing drivers + a webhook-handler layer that has never existed + a conflict model — larger than this epic); one-way *mirror* from the platform (makes the tracker read-only, which is not the ask). |
| **D4** | **One `work_items` table, closed `WorkItemKind`, a built `(parentKind, childKind)` matrix, depth bounded at 3.** | A table per level (3× CRUD/board/events for levels that differ only in allowed children); an unbounded tree (every query recursive, every board render ambiguous). |
| **D5** | **Tenant-schema residency, and this epic builds the migrate-all-provisioned-tenants sweep.** | Control-plane residency (would put every tenant's backlog in one shared table and forfeit the isolation `SchemaPerTenantMigrationTests` proves); shipping the migration without the sweep (every existing tenant gets `42P01`). |
| **D6** | **No principal XOR on work items** — content, not configuration; schema-per-tenant already isolates. The XOR *is* used, in its strong form, for `tracker_preferences`. | Copying the XOR onto `work_items` — a nullable `UserId` encoding a second ownership plane with no reader. |
| **D7** | **Fractional-index string `Rank`, one per work item, project-scoped.** | Integer rank (a drag rewrites O(n) rows); float (precision exhausts in ~50 insertions); a second rank per status column (two things that can disagree). |
| **D8** | **The board is a query, not a table.** | A `boards` table — nothing would read a column definition that `groupBy=status` already answers. |
| **D9** | **UI in `packages/dashboard`** (deployed), not `packages/dashboard-user` (no Dockerfile, no compose service, no deploy step). | Shipping into `dashboard-user` and absorbing its deployment build-out unbudgeted. |
| **D10** | **Priority and item type reuse the shipped `TriagePriority` / `TriageIssueType` `[Wire]` enums**, giving two dead vocabularies their first consumer. | New parallel enums (a fifth priority vocabulary in a repo that already has drift between the triage enums and the `triage-intake` prompt, `TriageDecision.cs:212-217`). |
| **D11** | **`WorkItemStatus` is a fixed 7-member closed enum in v1**, CHECK-mirrored and count-pinned. | Customizable status sets — converts a closed vocabulary into open data and breaks both the constraint and the projection guarantee. |

---

## Open questions for the product owner

These are not derivable from the code.

1. **Is `packages/dashboard-user` intended to be the customer-facing application?** If yes, someone must
   fund its deployment path — Dockerfile, compose service, GHCR image, deploy step, nginx vhost, none of
   which exist — and 39-19's `/chat` and `/tasks` are blocked on the same work. If no, 39-19 should be
   re-targeted at `packages/dashboard` and this epic's D9 becomes uncontroversial. **This blocks nothing
   in Epic 44 but it decides whether the platform ends up with two user-facing dashboards.**

2. ~~**Is the tracker for Tamma's own development, for customers, or both?**~~ — **ANSWERED
   (2026-07-25, product owner): both, and Tamma is tenant #1.**

   > "tamma will self maintain, so the first tenant is tamma itself"

   This is not a preference between two audiences; it is a statement that **the platform's own
   development runs on the platform**, as an ordinary tenant. It aligns with CLAUDE.md's standing
   self-maintenance goal ("Tamma is designed to autonomously develop features for itself") and with
   the `tenant_databases` pool already auto-bootstrapping the central DB as member #1.

   **Consequences, and they are not small:**

   - **44-9 is no longer a cuttable dogfood story — it is the proof.** The execution plan lists it
     first on the cut line; that is now wrong. If `sprint-status.yaml` is not generated from the
     tracker, Tamma is not running on its own tracker and tenant #1 does not exist in any meaningful
     sense. Its 4 days are load-bearing. *(The `EXECUTION-PLAN.md` cut-line section carries the
     correction.)*
   - **Both directions are required, not one.** The internal side needs the `docs/stories/*.md` and
     wiki-publish coupling; the customer side needs import/export. "Customers first" was offered as
     a way to drop scope and is off the table.
   - **Tenant zero has a bootstrap problem.** The tenant that runs the platform cannot be
     provisioned *by* the platform through the normal path — the provisioner would need a running
     platform to provision the tenant that runs it. The central-DB-as-pool-member-#1 bootstrap is
     the existing precedent; 44-1 must state explicitly how the Tamma tenant row comes into
     existence, and it is not via `POST /api/admin/tenants/{id}/provision`.
   - **Self-modification changes the risk class of Epic 43's dial.** With Tamma as a tenant, its
     autonomy dial governs agents changing *Tamma itself*. The failure mode is not "a bad deploy for
     a customer" but "a bad deploy that removes the ability to deploy". Epic 43's `deploy-control`
     group defaults deserve a different conversation for tenant #1 than for tenant *n*, and there
     must be a **break-glass path that does not run through Tamma** — if Tamma breaks its own
     deployment there is no Tamma to fix it.
   - **Question 3 below (may an agent create work items unattended?) is now sharper**, because the
     agent filing them is working on this repository.
   - **The provider-config direction interacts** (`.dev/findings/provider-abstraction-and-openai-compatible-candidates.md`):
     tenant #1 needs its own provider descriptors and credentials like any tenant, which is a useful
     forcing function — if the Tamma tenant cannot be configured through the admin surface, neither
     can a customer.

3. **Should a work item be creatable by an agent without a human?** The autonomous loop decomposes an
   issue into `DecompositionTask`s today. If those may become work items automatically, tracker creation
   is a consequential action needing an Epic 43 `MinAutonomy` threshold and a policy for what happens
   when an agent files 40 items overnight. If they may not, 44-7's intake seam is read-only and the
   epic is materially simpler. **This changes 44-2 and 44-7 and should be settled before 44-0 freezes
   the descriptor shape.**

4. **What is the intended relationship between a work item's status and the workflow that is executing
   it?** Two coherent answers: (a) status is human-owned and workflows only *report* (`in_progress` is
   set by a person), or (b) the loop drives status transitions (`SingleIssueCycleWorkflow` moves an item
   to `in_review` when it opens a PR). (b) is more useful and much more coupled — it makes every
   workflow a tracker writer and needs an idempotency rule for re-entry. v1 as drafted assumes (a) with
   an explicit opt-in for (b) per project; confirm.

5. **Is `TriageComplexity` meant to survive?** It has an `epic` member (`TriageDecision.cs:39`) that
   reads as a hierarchy level next to `WorkItemKind.Epic`, it is used by nothing but its own tests, and
   its `triage-intake` prompt teaches a different vocabulary entirely (`:212-217`). Keeping both is a
   standing confusion; retiring it is a 39-16-adjacent decision this epic should not make unilaterally.

6. **Does the external link need to survive a repository being disconnected?** `ExternalRef` points at a
   `(platform, repo, number)` that a tenant can revoke via `PlatformInstallEndpoints`. v1 keeps the ref
   as inert text. If audit or compliance needs the linked issue's content preserved, that is a snapshot
   requirement and it changes the import story's scope.
