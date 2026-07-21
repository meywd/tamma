# Implementation Plan — Story 39-19: Orchestrator Chat — primary user interface, and the Task View

## Scope & Deliverable

When this story is done, the user dashboard (`packages/dashboard-user`) has two new surfaces: **Orchestrator Chat** (`/chat`) — a per-user conversation with the 39-17 agent over the 39-18 user channel, with streamed replies, reloadable history, permission-scoped answers, and conversational workflow initiation — and the **Task View** (`/tasks`) — an inbox of decisions/reviews/approvals/clarifications assigned to the current user, each backed by a suspended 39-8 gate and acted on through the idempotent resume surface. Server-side, `Tamma.Api` gains the `CHAT.*` event family with a fail-loud transcript recorder (implementing 39-17's `IChatTranscriptRecorder` seam), chat-history and task-inbox read endpoints that are pure projections of the DCB stream (no new tables), and a single authorized workflow-initiation door that stamps `initiatedBy` into every chat-caused dispatch. Visibility on both surfaces is enforced server-side through the 39-20 `ITaskAudienceResolver` seam (conservatively stubbed until 39-20 lands).

## Pre-Reading

- `docs/stories/epic-39/story-39-19/39-19-orchestrator-chat-primary-user-interface-and-task-view.md` — the story (ACs are source of truth)
- `docs/stories/epic-39/README.md` — settled principles: "Chat is the front door; the Task View is the inbox"; "Access is a model, enforced server-side"; every chat turn is a `CHAT.*` DCB event, history is a projection
- `docs/guides/BEFORE_YOU_CODE.md` — mandatory process
- Sibling plans (contracts consumed here): `docs/stories/epic-39/story-39-17/implementation-plan.md` (`IChatTranscriptRecorder` seam D12 — implemented HERE; `HandleConversationAsync(userId, message)` per-user attenuation; `OrchestratorAgentRegistry`; `WorkflowControlTool` dispatch verb; `ScriptedTurnRunner`), `story-39-8/implementation-plan.md` (`POST /api/documents/decisions/{sessionId}/resume`, server-derived decider/channel D6/D7, `APPROVAL.REQUESTED/PROVIDED` payloads), `story-39-11/implementation-plan.md` (`GET /api/documents/issues/{issueId}/lineage` — the Task View's lineage link target)
- Lockstep story files (no plans yet): `docs/stories/epic-39/story-39-18/39-18-real-time-channels-workflow-orchestrator-and-user-orchestrator.md` (user hub, `AgentConversationMessage`/`TaskAssigned` typed messages, outbox+replay), `story-39-20/39-20-teams-roles-repo-access-and-task-routing.md` (`ITaskAudienceResolver`, `TASK.*` events, initiation permission)
- `apps/tamma-elsa/src/Tamma.Api/Auth/Permissions.cs` — `Permissions.Matrix` + `HasPermission` (story-referenced; the seam 39-20 extends — read-only here)
- `apps/tamma-elsa/src/Tamma.Data/Repositories/IEventRepository.cs` + `EventRepository.cs`, `Entities/DomainEvent.cs` — `QueryEventsAsync` (type-prefix + `actor`→`Tags.userId` + `correlationId`→`Tags.correlationId`, hard tenant guard), `QueryAsync` (nullable-tenant legacy read), `AppendAsync`
- `apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/PromptEventsService.cs` — the Api-side emit shape (constants + tags dictionary); note its BEST-EFFORT posture, deliberately NOT copied (D3)
- `apps/tamma-elsa/src/Tamma.Core/Redaction/CredentialRedactor.cs` — `Clean(string?)`, `MaxLength = 1024`, `Placeholder` — the redaction pass AC6 requires on chat content
- `apps/tamma-elsa/src/Tamma.Api/Services/ElsaWorkflowService.cs` + `IElsaWorkflowService.cs` — `StartWorkflowAsync(string workflowName, Dictionary<string, object> input)` — the dispatch the initiation door wraps
- `apps/tamma-elsa/src/Tamma.Api/Endpoints/AdlEndpoints.cs` (~L525–540) — `ResolveApprover`-style claims fallback chain for server-derived identity
- `apps/tamma-elsa/src/Tamma.Api/Endpoints/ReposRunsEndpoints.cs` — fail-closed null-tenant guard + entity-level re-check posture; `apps/tamma-elsa/src/Tamma.Api/Program.cs` ~L1463 (`MemberAccess`), ~L1591 (`AuthenticatedAny`), ~L2735 (per-route mapping precedent)
- `apps/tamma-elsa/src/Tamma.Api/Endpoints/Admin/AdminTenantEventsSseEndpoint.cs` + `Services/Streaming/ILlmRunStreamBus.cs` — the one-way SSE surface (UNCHANGED by this story; the bidirectional user channel is 39-18's)
- `apps/tamma-elsa/src/Tamma.Api/Services/PromptStore/TammaMode.cs` (`ITammaModeProvider`), `Tamma.Data/ITenantContext.cs` — mode + tenant seams for the two-scoping-models read paths
- Dashboard precedents: `packages/dashboard-user/src/App.tsx` (route tree + guard nesting), `src/api/client.ts` (`ApiClient`, refresh-on-401), `src/hooks/useAuth.tsx` (`AuthUser` shape), `src/layouts/AppLayout.tsx` (nav links), `src/pages/alerts/TenantAlertFeed.tsx` + `src/api/alerts.ts` (+ their `.test.tsx/.test.ts`) — the page/api-module/test style to copy; `packages/dashboard-user/package.json` (deps — `@microsoft/signalr` is NEW)
- Tests to copy: `apps/tamma-elsa/tests/Tamma.Api.Tests/Analytics/TenantAnalyticsIntegrationTests.cs` (two-schema Testcontainers), `Tamma.Api.Tests/Dashboard/ReposRunsEndpointsGuardTests.cs` (guard style), `Tamma.Api.Tests/PromptStore/PromptEndpointsTenantAdminTests.cs` (RBAC parity)
- **All story-referenced repo paths exist** (`packages/dashboard`, `packages/dashboard-user`, `Tamma.Api/Auth/Permissions.cs`). **NOT FOUND (planned by prerequisite/lockstep stories, no code yet):** `Tamma.Api/Services/Orchestrator/` (39-17: `IChatTranscriptRecorder`, `OrchestratorAgentRegistry`, `WorkflowControlTool`), the 39-18 hubs/outbox/typed messages, `ITaskAudienceResolver` + `TASK.*` catalogue (39-20), `Tamma.Activities/Documents/` + `DocumentDecisionEndpoints` (39-8), `IDocumentInstanceRepository`/lineage endpoints (39-11). All stubbed per Dependencies & Sequencing.

## Design Decisions

- **D1 — Ownership split: this story ships surfaces + server glue; transport, agent, and access stay with their owners.** Server pieces land in `Tamma.Api/Services/Chat/` and `Tamma.Api/Services/Tasks/` + two endpoint classes; the SignalR hub is 39-18's, the agent turn is 39-17's, the access model 39-20's. Everything cross-story enters through five seams: `IChatTranscriptRecorder` (39-17-defined, implemented here), `IUserChannelSender` (defined here, implemented by 39-18's hub), `IWorkflowInitiationAuthorizer` (defined here, implemented by 39-20), `ITaskAudienceResolver` (canon 39-20 shape — created by whichever story lands first, see D7), and the 39-8 resume surface (consumed as-is).
- **D2 — Chat history is a pure projection of `CHAT.*` events; conversation identity is `(userId, conversationId)`.** No chat table (technical note 1: the event store IS the record). `CHAT.MESSAGE.*` events tag `userId`, `tenantId`, `conversationId`, `turnId`, and ALSO set `correlationId = conversationId` so reads ride the existing `ix_domain_events_tags_correlationid` index via `QueryEventsAsync(tenantId, type: "CHAT.MESSAGE.", typeIsPrefix: true, correlationId: conversationId, actor: userId, …)`. The `actor` filter makes cross-user reads structurally empty: user B replaying A's `conversationId` gets nothing — conversations are never shared (AC1). Two-scoping-models read path: SaaS folds ambient `tenantId` (hard guard throws on empty); single-user (ambient tenant null) falls back to two exact-type `QueryAsync(null, …)` calls (`RECEIVED` + `SENT`) merged + filtered in memory — honest v1, flagged in Open Questions as a candidate repository method.
- **D3 — Transcript recording is FAIL-LOUD and redacted; one `SENT` event per logical agent turn.** Unlike `PromptEventsService` (best-effort telemetry), here the event IS the history — a failed append fails the turn (the 39-8 D9 "the event IS the operation" posture, deviation from the copied file shape documented in the class comment). Every recorded string passes `CredentialRedactor` before serialization (AC6 last clause). Because `Clean` caps at 1024 chars (an error-string budget, wrong for chat prose), add an additive overload `Clean(string? value, int maxLength)` to `CredentialRedactor` — existing single-arg behavior byte-identical, pinned by test; chat uses a 32 KiB cap. Streamed replies (AC1) are partial `AgentConversationMessage` frames on the channel (39-18 lockstep — transport, not truth); only the FINAL frame is recorded as `CHAT.MESSAGE.SENT`, so event volume is per-turn, not per-chunk.
- **D4 — Availability check precedes recording; offline chat refuses loudly and queues nothing.** `OrchestratorChatService.HandleUserMessageAsync` first asks the 39-17 registry whether the tenant's agent instance is reachable; if not, it returns a typed `ChatUnavailable` result (no events emitted — the agent never received the turn, so recording `RECEIVED` would forge history) and the UI shows a persistent banner with the composer preserved (technical note 4). When available: record `CHAT.MESSAGE.RECEIVED` (fail-loud) → forward to `HandleConversationAsync(userId, message)` (which rebuilds the toolset with `ActingUserId = userId`, 39-17 D12 — the permission envelope of AC1) → record `CHAT.MESSAGE.SENT` → relay via `IUserChannelSender`. The permission-shaped refusal for an inaccessible repo is produced by the attenuated tools returning nothing + the agent's prompt contract; this story pins the SURFACE behavior end-to-end with a scripted turn (Test Plan), not the tool internals (39-17's tests own those).
- **D5 — ONE workflow-initiation door, wired under 39-17's `workflow_control` dispatch verb.** `ChatWorkflowInitiationService.InitiateAsync(acting, workflowName, repoRef, input, conversationId, turnId)` is the only path from chat to `IElsaWorkflowService.StartWorkflowAsync`: it (1) authorizes via `IWorkflowInitiationAuthorizer` (D7) — refusal emits `CHAT.WORKFLOW.REFUSED` and returns a typed refusal the agent must relay (auditable, AC2); (2) on success injects `initiatedBy` (= acting userId), `conversationId`, `chatTurnId` into the workflow input dictionary and emits `CHAT.WORKFLOW.INITIATED` (tags: `userId`/`initiatedBy`, `tenantId`, `conversationId`, `turnId`, `issueId` when known; data: `workflowName`, `workflowInstanceId`, `repo`). `initiatedBy` in the input is the hook 39-20's visibility predicate reads ("workflows they initiated"); the initiation event is the stream-side record correlating dispatch → originating turn (AC6). 39-17's `WorkflowControlTool` dispatch verb, when `ActingUserId != null`, MUST route through this service (lockstep MODIFY if 39-17 has landed; hand-off note otherwise).
- **D6 — Confirm-before-dispatch is conversational + UI-affordance; authorization is the enforcement.** The agent's prompt contract instructs it to state the intended dispatch and await user confirmation before calling the tool; the UI renders dispatch confirmations distinctly. This is deliberately NOT a server-side two-phase commit — the server-enforced guarantees are authorization, `initiatedBy` stamping, and audit (AC2's testable clauses); confirmation quality is prompt behavior, exercised by scripted-turn test and listed in completion notes as unverified-without-provider (the 39-17 AC7 posture).
- **D7 — Task inbox is an event projection with the audience resolver as its filter; no new table.** `TaskInboxProjection` derives pending tasks from the tenant stream: `TASK.ASSIGNED`/`TASK.REASSIGNED` (assignee, eligibility basis, autonomy context, task type, decision-session id — emitted by 39-17's `ITaskAssignmentService` via 39-20) joined with `APPROVAL.REQUESTED` (subject document, `issueId`, `rulesReference`, `requestedAtUtc` → age) minus completions — a task is completed when `APPROVAL.PROVIDED` for its session OR `TASK.COMPLETED` exists (either suffices; the 39-8 409 discipline makes completion single, so it "leaves the inbox for every eligible user" structurally, AC3). Listing filter: `ITaskAudienceResolver.CanSee(user, task)`. The interface file `Tamma.Api/Services/Access/ITaskAudienceResolver.cs` is created by whichever of 39-19/39-20 lands first with the canon shape (`CanSee(user, task)`, `EligibleAssignees(task)`); this story's default DI implementation is `ConservativeAudienceResolver`: single-user → the sole user sees everything; SaaS → visible iff the user is the task's current assignee OR its workflow's `initiatedBy` — a strict SUBSET of 39-20's predicate (assignee/initiator ⊆ eligible), so nothing is ever shown that 39-20 would hide; repo-access breadth arrives with the real resolver. If 39-18's `InitiatorOnlyTaskAudienceResolver` stub is already registered, this resolver replaces it (a strict widening: + assignee; one default stub, never two). Fail-closed by construction, recorded as lockstep debt.
- **D8 — Task actions go straight to the 39-8 resume surface; chat has no backdoor (AC5).** The Task View "act" button posts to `POST /api/documents/decisions/{sessionId}/resume` — no new action endpoint; decider identity and channel are already server-derived there (39-8 D6/D7). Chat-side: discussing a task is read-only tooling; a resume attempted through the agent's `workflow_control` resume verb while acting for user X executes with X's server-derived identity (39-17 D5 construction), and no chat code path accepts a caller-supplied decider — pinned by test.
- **D9 — RBAC: `MemberAccess` on all `/api/chat/*` and `/api/tasks*` routes; per-user scoping inside handlers.** Any tenant member chats and receives tasks (39-8 D10's reasoning: assigned deciders ARE members). Current-user identity always derives from claims (the `AdlEndpoints` fallback chain), never from query parameters; SaaS handlers open with the `ReposRunsEndpoints` fail-closed null-tenant guard, single-user handlers scope to the sole user (`ITammaModeProvider` consulted at identity construction only, never a behavior branch).
- **D10 — Frontend: both pages in `packages/dashboard-user`; the channel client is one isolating module.** `@microsoft/signalr` is a new dependency; `src/api/userChannel.ts` is the ONLY file that knows the hub URL and message names (39-18 owns both — a rename is a one-file edit), exposing a typed `UserChannel` with connection-state callbacks, `sendChatMessage`, and `onTaskUpdate`/`onChatMessage` subscriptions plus reconnect (replay is the server's outbox job, 39-18 AC4 — the client just reconnects and refetches). The Task View is fully functional with the channel down (REST list + resume still work; live-update degrades to manual refresh) — technical note 4.
- **Story-vs-canon tension (minor): `CHAT.WORKFLOW.REFUSED` extends the canon event list.** Canon pins `CHAT.MESSAGE.RECEIVED/SENT, CHAT.WORKFLOW.INITIATED` (as examples — "e.g."); AC2's "the refusal is auditable" needs a stream record, and overloading `CHAT.WORKFLOW.INITIATED` with a failed status would make every consumer filter on status. A fourth constant in the same family follows `AGGREGATE.ACTION.STATUS` and is flagged for reviewer sign-off. No other tensions found: the story and canon agree on both surfaces, event tagging, projection-not-store, and the resume-only action path.

## Implementation Steps

1. **CREATE `apps/tamma-elsa/src/Tamma.Api/Services/Chat/ChatEvents.cs`; MODIFY `apps/tamma-elsa/src/Tamma.Core/Redaction/CredentialRedactor.cs`.** Constants catalogue (file shape of `PromptEventsService`'s constant block, standalone static class): `MessageReceived = "CHAT.MESSAGE.RECEIVED"`, `MessageSent = "CHAT.MESSAGE.SENT"`, `WorkflowInitiated = "CHAT.WORKFLOW.INITIATED"`, `WorkflowRefused = "CHAT.WORKFLOW.REFUSED"`; doc-comment pins the tag set (`userId`, `tenantId`, `conversationId`, `turnId`, `correlationId = conversationId`, `issueId` when known) and D3's fail-loud posture. Redactor: add `public static string Clean(string? value, int maxLength)`; the existing `Clean(string?)` delegates with `MaxLength` (byte-identical, pinned).

2. **CREATE `apps/tamma-elsa/src/Tamma.Api/Services/Chat/EventStoreChatTranscriptRecorder.cs`.** Implements 39-17's `IChatTranscriptRecorder` — the SOLE registration (39-17 registers no default; `OrchestratorChatService` ctor-requires the recorder, so chat cannot be wired without it — settled design review 2026-07-21):

   ```csharp
   public sealed class EventStoreChatTranscriptRecorder : IChatTranscriptRecorder
   {
       // ctor(IEventRepository events)
       public Task<Guid> RecordUserTurnAsync(ChatTurnContext ctx, string content, CancellationToken ct);   // → CHAT.MESSAGE.RECEIVED, returns turnId (UUID v7)
       public Task RecordAgentReplyAsync(ChatTurnContext ctx, Guid turnId, string content, CancellationToken ct); // → CHAT.MESSAGE.SENT
   }
   public sealed record ChatTurnContext(Guid? TenantId, Guid UserId, Guid ConversationId, string? IssueId);
   ```

   Content passes `CredentialRedactor.Clean(content, ChatRedactionMaxLength)`; append failure throws (D3). If 39-17's seam signature differs when it lands, reconcile toward 39-17's file — this class is the implementation, not the contract owner.

3. **CREATE `apps/tamma-elsa/src/Tamma.Api/Services/Chat/ChatHistoryProjection.cs` + `apps/tamma-elsa/src/Tamma.Api/Endpoints/ChatEndpoints.cs`; MODIFY `apps/tamma-elsa/src/Tamma.Api/Program.cs`.** Projection (D2): `ListConversationsAsync(principal)` — `QueryEventsAsync` prefix `"CHAT.MESSAGE."` + `actor` (SaaS) / merged `QueryAsync` (single-user), grouped by `conversationId` tag into `(conversationId, lastActivityAt, lastSnippet, messageCount)`; `GetConversationAsync(principal, conversationId, cursor, limit)` — same read + `correlationId: conversationId`, oldest-first by `SequenceNumber`. Endpoints: `GET /api/chat/conversations`, `GET /api/chat/conversations/{conversationId:guid}` — static handlers, fail-closed tenant guard, userId from claims. Program.cs: per-route `.RequireAuthorization("MemberAccess")` beside the ~L2735 block (D9).

4. **CREATE `apps/tamma-elsa/src/Tamma.Api/Services/Chat/OrchestratorChatService.cs` + `IUserChannelSender.cs`.** The glue the 39-18 hub invokes (D1/D4):

   ```csharp
   public interface IUserChannelSender   // implemented by 39-18's hub; NoOp + capturing fakes here
   { Task SendToUserAsync(Guid? tenantId, Guid userId, object message, CancellationToken ct); }

   public sealed class OrchestratorChatService
   {
       // ctor(IOrchestratorAgentRegistry agents, IChatTranscriptRecorder recorder, IUserChannelSender channel)
       public Task<ChatSendResult> HandleUserMessageAsync(
           Guid? tenantId, Guid userId, Guid conversationId, string content, CancellationToken ct);
       // ChatSendResult: Accepted(turnId) | Unavailable(reason) — closed set
   }
   ```

   Order per D4: availability → record RECEIVED → `HandleConversationAsync` → record SENT → relay. Until 39-17 lands, an `IOrchestratorAgentRegistry` stand-in interface with an always-unavailable stub keeps DI honest (the chat surface truthfully reports "agent offline").

5. **CREATE `apps/tamma-elsa/src/Tamma.Api/Services/Chat/ChatWorkflowInitiationService.cs` + `IWorkflowInitiationAuthorizer.cs`; lockstep MODIFY 39-17's `Tamma.Api/Services/Orchestrator/Tools/WorkflowControlTool.cs` (or hand-off note).** D5/D6: authorizer seam `Task<InitiationAuthzResult> AuthorizeAsync(Guid userId, Guid? tenantId, string workflowName, string? repoRef, CancellationToken ct)` with default `TenantMembershipInitiationAuthorizer` (any tenant member — today's flat-visibility reality per the 39-20 story context; 39-20 replaces it with grant-aware checks, comment names the debt). Service refuses (emit `CHAT.WORKFLOW.REFUSED`, typed result) or dispatches via `StartWorkflowAsync` with `initiatedBy`/`conversationId`/`chatTurnId` input keys + emits `CHAT.WORKFLOW.INITIATED`. Wire as the dispatch path of `workflow_control` when `ActingUserId != null`.

6. **CREATE `apps/tamma-elsa/src/Tamma.Api/Services/Access/ITaskAudienceResolver.cs` (if 39-20 has not landed) + `ConservativeAudienceResolver.cs`, `apps/tamma-elsa/src/Tamma.Api/Services/Tasks/TaskInboxProjection.cs` + `TaskViewItem.cs`, `apps/tamma-elsa/src/Tamma.Api/Endpoints/TaskEndpoints.cs`; MODIFY `Program.cs`.** D7:

   ```csharp
   public sealed record TaskViewItem(
       Guid SessionId, string TaskType,            // acceptance_decision | review | approval | clarification (closed)
       string? DocumentType, Guid? DocumentId, string? IssueId, string? CorrelationId,
       Guid? AssigneeUserId, string EligibilityBasis,  // initiator | repo-access (canon)
       string? AutonomyContext,                    // e.g. "autonomy level 82: Design requires human acceptance"
       DateTimeOffset AssignedAt, string Status);  // pending | completed
   ```

   `TaskInboxProjection.ListForUserAsync(principal)` reads `TASK.`-prefixed + `APPROVAL.`-prefixed events (`QueryEventsAsync`; local private event-name consts with a comment deferring the catalogue to 39-20's `TaskEvents.cs`), folds assignment→reassignment→completion per session, maps `TaskViewItem`, filters `CanSee`, orders oldest-pending-first. Endpoints: `GET /api/tasks` (pending for current user), `GET /api/tasks/{sessionId:guid}` (detail incl. lineage link `/api/documents/issues/{issueId}/lineage` — 39-11's route, rendered as a link, not proxied). `MemberAccess`, claims-derived user, fail-closed tenant guard.

7. **Frontend chat. CREATE `packages/dashboard-user/src/api/userChannel.ts`, `src/api/chat.ts`, `src/hooks/useUserChannel.tsx`, `src/pages/chat/OrchestratorChat.tsx`; MODIFY `src/App.tsx`, `src/layouts/AppLayout.tsx`, `package.json`.** D10: add `@microsoft/signalr`; `chat.ts` wraps the step-3 endpoints on `apiClient` (`alerts.ts` style); `OrchestratorChat.tsx` — conversation list + message pane, history loaded via REST, live turns via the channel, partial-frame accumulation for streamed replies, offline banner + disabled send when connection state ≠ connected (send never buffers locally), permission-refusal turns rendered as normal agent replies, dispatch-confirmation turns visually distinct (D6). Routes `/chat` under `AuthGuard → AppLayout`; nav link "Chat" in `AppLayout.tsx`.

8. **Frontend Task View. CREATE `packages/dashboard-user/src/api/tasks.ts`, `src/pages/tasks/TaskView.tsx`; MODIFY `src/App.tsx`, `src/layouts/AppLayout.tsx`.** Inbox table: task type, subject document + lineage link, workflow, age (from `AssignedAt`), autonomy context string (AC3); row action opens a decision panel posting to 39-8's `POST /api/documents/decisions/{sessionId}/resume` (kind/notes per its `DecisionRequest`); on `TaskAssigned`/task-update channel messages, refetch; a 404/409 from resume (someone else completed) removes the row with a notice — idempotent single completion surfaced honestly. Route `/tasks` + nav link "Tasks".

9. **Server test suites** per the Test Plan under `apps/tamma-elsa/tests/Tamma.Api.Tests/Chat/` and `Tasks/`; run `dotnet ef migrations has-pending-model-changes --context TenantDbContext` (must stay clean — no entity edits) + `dotnet test`.

10. **Frontend tests + docs.** Vitest suites beside each new file (step 7/8); completion notes: D6's unverified-without-provider remainder, the D7 conservative-resolver debt, the 39-18/39-20 hand-off list (`IUserChannelSender`, hub URL/message names, `ITaskAudienceResolver` swap, `TaskEvents.cs` constants adoption), and the `CHAT.WORKFLOW.REFUSED` extension flag.

## Data & Migrations

None. Chat history and the task inbox are projections over the existing `domain_events` table (D2/D7); no entity, no DbSet, no migration. `dotnet ef migrations has-pending-model-changes` stays clean (verified in step 9).

## Events

Constants in `Tamma.Api/Services/Chat/ChatEvents.cs`:

- **Emits:** `CHAT.MESSAGE.RECEIVED` (user turn; data: redacted content, snippet; tags: `userId`, `tenantId`, `conversationId`, `turnId`, `correlationId=conversationId`, `issueId` when known), `CHAT.MESSAGE.SENT` (final agent reply per turn, same tags + `turnId` of the prompting turn), `CHAT.WORKFLOW.INITIATED` (data: `workflowName`, `workflowInstanceId`, `repo`; tags: as above + `initiatedBy`), `CHAT.WORKFLOW.REFUSED` (data: `workflowName`, `repo`, `reason`; same tags — the auditable refusal, D5).
- **Consumes (projection only):** `TASK.ASSIGNED` / `TASK.REASSIGNED` / `TASK.COMPLETED` (39-20's canon names, emitted via 39-17's `ITaskAssignmentService`), `APPROVAL.REQUESTED` / `APPROVAL.PROVIDED` (39-8) — folded into `TaskViewItem`s (D7). `ORCHESTRATOR.TOOL_INVOKED` (39-17) is the tool trail chat turns correlate with; nothing re-emitted here.
- **Not emitted here:** `TASK.*` (39-20), `APPROVAL.*` (39-8), `GUIDANCE.*` (39-18).

## Test Plan

NUnit + FluentAssertions + Moq under `apps/tamma-elsa/tests/Tamma.Api.Tests/Chat/` + `Tasks/`; Testcontainers where marked; Vitest + Testing Library for the dashboard.

- **`ChatEventsTests`** — pins the exact 4 constant strings; `CredentialRedactor.Clean(value, maxLength)` overload: single-arg behavior byte-identical, chat cap honors long prose, a `sk-ant-…` key in a chat message becomes `[REDACTED]`. **AC6 (constants + redaction).**
- **`ChatTranscriptRecorderTests`** (Moq'd `IEventRepository`) — RECEIVED/SENT carry the full D2 tag set incl. `correlationId=conversationId`; content is redacted BEFORE append; append failure throws (fail-loud — no silent history loss); one SENT per logical turn. **AC6.**
- **`ChatHistoryProjectionTests`** — ordering by `SequenceNumber`; conversation grouping; user B requesting A's `conversationId` → empty (actor filter, D2); single-user null-tenant path returns the sole user's history; endpoint guards (null-tenant → 404 before any repo call, `ReposRunsEndpointsGuardTests` style). **AC1 (history persisted/reloadable, never shared).**
- **`OrchestratorChatServiceTests`** (fakes: capturing recorder/channel; 39-17 `ScriptedTurnRunner`-style scripted agent or scripted registry stub) — offline: `Unavailable` returned, ZERO events, channel untouched; online: RECEIVED recorded before the agent turn, SENT after, reply relayed to exactly `(tenantId, userId)`; scripted inaccessible-repo question (fake resolver with disjoint scopes) yields a permission-shaped refusal reply and no foreign data in any recorded event. **AC1 (scope refusal, pinned), technical note 4.**
- **`ChatWorkflowInitiationTests`** — authorized: `StartWorkflowAsync` input contains `initiatedBy`/`conversationId`/`chatTurnId`; `CHAT.WORKFLOW.INITIATED` tagged to the originating turn; unauthorized (refusing authorizer): NO dispatch, `CHAT.WORKFLOW.REFUSED` emitted, typed refusal returned; a script-supplied `initiatedBy` in tool args is ignored — server-derived only. **AC2.**
- **`ChatNoBackdoorResumeTests`** — scripted chat turn discusses a pending task (reads succeed), then attempts resume: the only path invoked is `ResumeDocumentDecisionAsync` with decider = the acting user's server-derived identity; a scripted attempt to resume "as" another user produces the same acting-user call (no argument accepted that overrides identity); Task View resume rides the identical endpoint. **AC5.**
- **`TaskInboxProjectionTests`** — ASSIGNED appears with basis + autonomy context + age; REASSIGNED moves assignee; `APPROVAL.PROVIDED` alone removes it, `TASK.COMPLETED` alone removes it (either-suffices, single completion for all viewers); unknown task type falls back closed-set-safely. **AC3.**
- **`TaskViewScopingTests`** — fake `ITaskAudienceResolver` with two same-tenant users, disjoint repo access, one workflow initiated by user A: each `GET /api/tasks` sees exactly their own set (A: initiated + own-repo; B: own-repo only); `ConservativeAudienceResolver` pins: single-user sole user sees all, SaaS assignee/initiator only; handlers never accept a userId query param. **AC4.**
- **`ChatAuditReplayTests`** — from a captured event list ALONE (chat turns + initiation + 39-8 `APPROVAL.*` fixtures + `TASK.*` fixtures): rebuild ordered conversations per user, link `CHAT.WORKFLOW.INITIATED` → its `turnId`/`conversationId` and `workflowInstanceId`, correlate a task assignment and completion back to the initiating turn ("who asked, what was said, who decided"); assert no event payload contains an unredacted planted secret. **AC6 (replay test).**
- **`ChatHistoryIsolationTests`** (**Testcontainers**, two tenant schemas — `TenantAnalyticsIntegrationTests` pattern) — two tenants + two users per tenant, real `EventRepository`: cross-tenant conversation reads structurally empty (hard guard), cross-user within one tenant empty via actor filter; task listing likewise disjoint. **AC1 + AC4 (structural halves).**
- **Frontend (Vitest):** `OrchestratorChat.test.tsx` — history render, partial-frame streaming accumulation, offline banner + disabled send (no silent queue), refusal + confirmation turns rendered; `TaskView.test.tsx` — inbox fields incl. autonomy context + lineage link, act → resume POST with correct body, channel task-update removes row, 409 → "already completed" notice; `api/chat.test.ts` / `api/tasks.test.ts` — path/shape pins; `useUserChannel.test.tsx` — connection states, reconnect triggers refetch callback. **AC1/AC3 (UI halves), technical note 4.**

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — chat surface: per-user sessions, streamed replies, persisted/reloadable history, scope-restricted answers + refusal | 1–4, 7 (D2/D3/D4) | `ChatHistoryProjectionTests`, `ChatHistoryIsolationTests`, `OrchestratorChatServiceTests` (refusal), `OrchestratorChat.test.tsx` |
| 2 — workflow initiation: authz, confirm-first, `initiatedBy` stamped, auditable refusal | 5 (D5/D6), 7 | `ChatWorkflowInitiationTests`; D6's confirmation clause via the scripted scenario + completion-notes remainder |
| 3 — Task View inbox: type/subject/lineage/workflow/age/autonomy context, live updates, resume-driven, single completion | 6, 8 (D7/D8) | `TaskInboxProjectionTests`, `TaskView.test.tsx` |
| 4 — scoped delivery per 39-20 predicate; two-user disjoint test; single-user trivial | 6 (D7) | `TaskViewScopingTests`, `ChatHistoryIsolationTests` (task half) |
| 5 — no chat backdoor: completion only via authorized resume, decider server-derived | 5, 6, 8 (D8) | `ChatNoBackdoorResumeTests` |
| 6 — everything evented: `CHAT.*` family, tags, correlation to dispatch/tasks, replay, redaction | 1, 2, 5 (D2/D3/D5) | `ChatEventsTests`, `ChatTranscriptRecorderTests`, `ChatAuditReplayTests` |

## Dependencies & Sequencing

- **Hard prerequisites (behavioral):** 39-17 (the agent behind `HandleConversationAsync` + `IChatTranscriptRecorder` contract + `WorkflowControlTool`), 39-18 (user hub + `AgentConversationMessage`), 39-8 (resume endpoint the Task View posts to). Steps 1–3 and 6 compile and test with NONE of them landed (projections + recorder depend only on `IEventRepository`); steps 4–5 compile against stand-in interfaces if 39-17 lags.
- **Lockstep — 39-18:** implements `IUserChannelSender` (defined here, step 4) over its hub + outbox; owns the hub URL + partial-frame streaming shape consumed by `userChannel.ts` (one-file client isolation, D10); its AC3 explicitly serves these two surfaces.
- **Lockstep — 39-20:** supplies the real `ITaskAudienceResolver` + `IWorkflowInitiationAuthorizer` implementations and the `TaskEvents.cs` constants; until then `ConservativeAudienceResolver` (strict-subset visibility) + `TenantMembershipInitiationAuthorizer` (documented debt) carry; the interface file is created by whichever story lands first (D7).
- **Lockstep — 39-17:** the recorder implemented here replaces its no-op DI default; its `WorkflowControlTool` dispatch verb routes through step 5's service; its `ScriptedTurnRunner` powers the chat pipeline tests.
- **Stubbed, not pulled in:** 39-11 (lineage links rendered as URLs to its endpoints — no import), 39-5 (autonomy context arrives as a pre-rendered string in `TASK.ASSIGNED` data), 39-2..39-6 (document machinery — only event fixtures referenced).
- **In place, verified:** `IEventRepository.QueryEventsAsync`/`QueryAsync`/`AppendAsync`, `CredentialRedactor`, `Permissions.Matrix` + `MemberAccess`, `ElsaWorkflowService.StartWorkflowAsync`, `ITenantContext`/`ITammaModeProvider`, dashboard-user SPA scaffolding + test rig.
- **Feeds:** the autonomy routing loop (assigned tasks land here), escalation UX, 39-12..39-15 migrated lifecycles (their gates surface in this inbox).

## Risks & Mitigations

- **Three-way lockstep (39-17/39-18/39-20) with only 39-17 planned.** Mitigation: every cross-story dependency is one small interface with a shipping stub; the surfaces are REST-first (chat history, task list, resume) so the product is demonstrable before any hub exists; channel wiring is additive.
- **Conservative resolver under-shows tasks in SaaS (repo-access-eligible non-assignees see nothing until 39-20).** Deliberate fail-closed trade-off (D7) — never over-shows; recorded in completion notes and the resolver's class comment so 39-20's swap is a conscious widening.
- **Projection reads scan `TASK.`/`APPROVAL.`/`CHAT.MESSAGE.` prefixes per request.** Bounded by `QueryEventsAsync` keyset pagination + the `correlationId`/`userId` tag indexes; if inbox volume bites, 39-20's precomputed-scope note (its technical note 2) is the sanctioned read-model seam — endpoint shapes here survive that swap.
- **`CredentialRedactor` overload touches a shared Core type.** Additive-only; single-arg path pinned byte-identical by `ChatEventsTests`.
- **Streamed-reply shape (partial frames) is guessing 39-18's wire.** Isolated to `userChannel.ts` + one `OrchestratorChatService` relay call; the recorded-event contract (one SENT per turn) is transport-independent, so a 39-18 protocol change never rewrites history semantics.
- **Confirmation-before-dispatch is prompt-level (D6).** Server authz + `initiatedBy` audit bound the blast radius of an over-eager agent; scripted tests pin the pipeline; the judgment gap is documented per the 39-9/39-17 posture.

## Effort Breakdown

| Step(s) | Work | Days |
|---|---|---|
| 1 | `ChatEvents` + redactor overload | 0.25 |
| 2 | Transcript recorder (fail-loud, redacted) | 0.5 |
| 3 | History projection + chat endpoints + mapping | 0.75 |
| 4 | `OrchestratorChatService` + `IUserChannelSender` + stubs | 0.75 |
| 5 | Initiation service + authorizer + `workflow_control` wiring | 0.75 |
| 6 | Task inbox projection + resolver + task endpoints | 1.0 |
| 7 | Chat UI: channel client, hook, page, routes | 1.25 |
| 8 | Task View UI: page, resume action, live updates | 0.75 |
| 9 | Server suites incl. Testcontainers isolation | 1.0 |
| 10 | Frontend suites + completion notes/hand-offs | 0.5 |
| **Total** | | **7.5** (story estimate: 6–8 days) |
