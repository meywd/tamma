# Story 42-3: Per-Tool Permission & Autonomy Gating

Status: drafted

## MANDATORY: Before You Code

**ALL contributors MUST read and follow the comprehensive development process:**

[BEFORE_YOU_CODE.md](../../../guides/BEFORE_YOU_CODE.md)

Then check the knowledge base: `.dev/spikes/`, `.dev/bugs/`, `.dev/findings/`, `.dev/decisions/`.

## User Story

As the **orchestrator dispatching a workflow step**, I want the agent to be handed **only the tools it
is permitted to use, for its role, at the current autonomy level** — and any destructive or above-floor
**action** to be authorized by an actor against its **concrete arguments** before it runs — so that a
tool call carries the same governance as accepting a document: low-risk reads run free at high
autonomy, and flipping a prod flag or deleting a specific VPS is a decision taken by someone, never an
unattended side effect.

## Priority

P0 / Wave 1 — the **enforcement** story, and the largest item in the wave. 42-1 lets a tool declare its
class/floor and 42-2 stores per-principal overrides; this story is where those become behavior. Every
family (42-7/8/9) and MCP tool (42-6) is inert-but-safe until this gates them.

## The gap (READ FIRST)

**Gap 1 — there is no per-tool permission or autonomy gate anywhere, and the surface earlier drafts
named is dead code.** *Corrected: earlier drafts of this story sited enforcement in
`ResolveToolsActivity` (`Tamma.Activities/LlmCall/ResolveToolsActivity.cs`).* That activity is
**never executed**: it is referenced nowhere in `src/`, `tests/`, or `workflows/` (the one apparent
hit, `LlmCallWorkflow.cs` `Id = "ResolveTools"`, is a plain Elsa `SetVariable`). It also could not host
the gate if it were live — it injects only `ILogger` + `IConfiguration`, has no `IToolExecutorRegistry`
and no principal, and its built-in switch has exactly three arms (`search_code`, `read_file`,
`run_tests`) of which `read_file` matches no registered executor (`FileReadTool.ToolName` is
`file_read`). The **live** path is:

- `ManagedAgent.ToResolvedTools` (`ManagedAgent.cs` L923–937) →
  `names.Select(n => new ResolvedTool { Name = n })` — bare names, no description, no schema, no
  descriptor, no filter. This is where the tool array the LLM sees is actually built.
- `InlineToolLoopRunner` executes what comes back, on **two** branches:
  parallel (L335–405, via `ParallelToolExecutor`) and sequential (L406–431 → `executor.ExecuteAsync`
  at L462). `ToolLoopConfig.EnableParallelTools` defaults to **`false`**, so **sequential is the
  default path** — a gate on one branch governs nothing.
- Neither branch consults a permission model. `ParallelToolExecutor.ExecuteSingleToolAsync` does
  `registry.GetExecutor(name)` → `ExecuteAsync`, nothing else.

Two allowlists already run here and neither is a permission model:
`ToolCallValidator.Validate(tc, allowedToolNames)` (L262–267) checks membership in the **resolved-tool
name list**, and `_toolRegistry.IsAllowed(name, loopConfig.AllowedTools)` (L342 / L419) checks a
**separate** list. They are independently sourced, and the second is **fail-open by default**:
`ToolExecutorRegistry.IsAllowed` returns `true` when the allowlist is null or empty, and
`ToolLoopConfig.AllowedTools` defaults to `null`.

**Gap 2 — the autonomy dial has zero control-flow consumers.** `AcceptanceRules.AutonomyLevel` is
declared (`AcceptanceRules.cs` L32), validated to `[70,100]` (L85–86), defaulted (`AcceptanceDefaults`
L31/85), carried on a channel message and a DTO, and emitted in audit — and **never compared,
switched, or branched on anywhere in the codebase** (nor is the per-type `AcceptorRequirement` floor).
`AutonomyFloor ≤ currentAutonomy` is therefore the **first** control-flow use of the dial. Worse, the
one place resolved rules reach a running workflow caches them: `DocumentLifecycleWorkflow` calls
`ResolveRules` once at Init (L184) into serialized lifecycle state and every later stage reads
`state.Rules` — contrary to Epic 39's own written rule that the dial is *read when needed, never cached
into a running workflow*. So 42-3 must **ship** the live-read seam, not call an existing one, and that
is an **Epic 39 design change needing sign-off** (see Risks).

**Gap 3 — authorization cannot happen where the tool set is resolved.** `ResolvedTool` is
`{ Name, Description, InputSchema }` — a schema, never a value. Arguments first exist as
`LlmToolCall.ArgumentsJson`, populated from the provider response *after* the model call
(`InlineToolLoopRunner` L242–244) and first read at execution. A grant issued at resolve time
authorizes a **capability** (`cloud_resource_write`), which silently covers delete-any-resource. The
gate must therefore be **two-stage**.

## Scope

1. **Stage 1 — resolve-time eligible-set build, in `ManagedAgent.ToResolvedTools`.**
   That method is the only place where the principal, the role, the registry, and the descriptors are
   all reachable, and `IManagedAgent` is DI-**Scoped**, so the Scoped `IToolBindingResolver` (42-2) and
   `IAcceptanceRulesResolver` inject cleanly. (`ParallelToolExecutor` and `ToolCallValidator` are
   **Singletons** with no principal — neither can host this.)
   For each requested tool name, resolve via 42-2 to `{ Enabled, AutonomyFloor, AllowedRoles }`
   (falling through to the 42-1 descriptor) and keep the tool as **callable** only if: `Enabled`
   **and** the agent's `AgentRole` ∈ `AllowedRoles` **and** `AutonomyFloor ≤ currentAutonomy`.
   Also stop returning bare names: populate `Description` + `InputSchema` from the executor so the
   LLM sees a real tool definition.

   **DECIDED — stage 1 keys on the binding-resolved *effective ceiling*, never on the raw descriptor
   max.** *Corrected: an earlier draft added `Descriptor.PermissionClass != Destructive` to the
   filter. That is wrong and 42-7, 42-8A, 42-8B and 42-9 each flagged it as a blocking cross-story
   conflict.* 42-1's `PermissionClass` is the family's **maximum** over its operations, and every
   write-half executor in Wave 3 (`cloud_resource_write`, `feature_flag_write`, `deploy_control`,
   `http_request`) advertises `Destructive` as that maximum. Filtering on the raw max would mean the
   model is never handed those tools, never emits a call, stage 2 never fires, and "route the action
   to an actor" silently degrades to "the capability does not exist" — the whole Wave-3 catalog dead
   on arrival. So:
   - the **effective ceiling** is `min(descriptor max, the highest class the resolved binding grants
     this principal + role at this autonomy)`. A tool is dropped from the eligible set only when that
     ceiling is empty — i.e. the principal may perform **no** operation of that family (disabled, role
     not granted, or every operation the family exposes sits above the resolved floor);
   - a tool whose ceiling is non-empty is **offered** even when its descriptor max is `Destructive`.
     The concrete destructive *call* is then caught by stage 2 against its arguments and escalated.
   - `Destructive` is therefore a **stage-2** discriminator, not a stage-1 exclusion. Stage 1 answers
     "may this principal use this capability at all"; stage 2 answers "may this action run now".
   A read-half executor (`cloud_resource_read`, `feature_flag_read`, `deploy_status`) still resolves
   to a `ReadOnly` ceiling and runs free at its floor — the split remains what makes reads cheap.
   **Principal plumbing is part of this story:** `ManagedAgentRequest` carries `TenantId` + `Role` but
   **no `UserId`**, and single-user binding resolution keys on `user_id`. Add an auth-derived
   `UserId` alongside `TenantId`, with the same Finding-C1 posture — never sourced from the wire body.

2. **Stage 2 — invocation-time, argument-bound authorization, in `InlineToolLoopRunner`.**
   Hoist a single pre-execution filter that runs for **both** branches (or place it before the
   `EnableParallelTools` fork, at the `executableToolCalls` boundary, L330–332 — one call site covers
   both). For every call it derives the **normalized action** from the concrete arguments and
   authorizes *that*, not the capability:

   - 42-1's descriptor advertises the family's **maximum** class. This story defines the per-call seam
     the families implement: `ToolInvocationFacts Describe(string argumentsJson)` returning
     `{ PermissionClass, Operation, Target }`. The fail-safe default (unimplemented, or a throw) is
     the descriptor's max class with `Operation = ToolName`, `Target = null` — deny-by-default, never
     permissive.
   - If the derived class is `Destructive`, or the resolved floor exceeds the current autonomy, the
     call is **not executed**: it raises a `ToolAuthorizationRequest` (Scope 3) and the loop stops.
   - Do **not** build stage 2 on `_toolRegistry.IsAllowed(name, loopConfig.AllowedTools)` — that path
     returns `true` for everything when `AllowedTools` is null, which is its default.
   - Arguments are compared **post-sanitization**: `ToolCallValidator` rewrites `tc.ArgumentsJson` with
     the sanitized form (L271) before execution, so any digest computed pre-sanitization will never
     match.

3. **`ToolAuthorizationRequest` — a sibling record on the existing decision-gate plumbing.**
   *(Decided; this was previously deferred to an open question. `AcceptanceRequest` is **not**
   reusable: all seven of its properties are `required`, including a `review`-typed `DocumentEnvelope`,
   and `AcceptanceRequestFactory` is its only constructor and rejects a non-`review` envelope.)*
   Reuse the **machinery** — bookmark / suspend / resume / Task View — not the record.

   **Payload (explicit, because the approver must authorize a bounded action, not a capability):**
   `{ SessionId, TenantId?, ToolName, Operation, Target, RedactedArgumentsJson, PermissionClass,
   ResolvedAutonomyFloor, CurrentAutonomy, RulesReference, RequestedAtUtc, IssueId?, CorrelationId }`.
   Arguments ride redacted through the shipped `ToolOutputHelper.RedactSecrets` (already applied to
   tool output at `InlineToolLoopRunner` L317/L394 — a regex pass over known key/token/PEM/JWT shapes)
   so no credential enters the request, the event, or the Task View. Note its limit honestly: it is
   pattern-based, not schema-aware, so a family whose arguments carry a credential in an unusual shape
   must redact by field before handing them to the gate.

   **The suspend cannot happen inside the loop, and the story must not pretend otherwise.** The tool
   loop runs server-side inside a **blocking** `POST /api/v1/llm/call` (`CallLlmInlineActivity` is a
   thin client over `TammaApiClient`); `Tamma.Api` has no `ActivityExecutionContext` and cannot create
   an Elsa bookmark. The shape is therefore:
   - Stage 2 refuses the call and `ManagedAgent` terminates the run with a **new**
     `AgentRunFailureCodes.ToolAuthorizationRequired` (non-retryable, alongside `BUDGET_EXCEEDED` /
     `AGENT_UNRESOLVED`), carrying the `ToolAuthorizationRequest` on the result envelope.
   - The **engine-side** workflow branches on that outcome into a new
     `WaitForToolAuthorizationActivity` — a sibling of `WaitForDocumentDecisionActivity` — which emits
     the request event and suspends on its own bookmark; a sibling resume endpoint mirrors
     `DocumentDecisionResumeEndpoint` (keyed **tenant + session**, not session alone).
   - On an `Authorize` decision the workflow re-dispatches the call with a **single-use grant** keyed
     on `(SessionId, ToolName, Operation, Target)`. Stage 2 admits exactly one matching invocation and
     consumes the grant; a second call, or a call whose normalized action differs, re-gates.

   **Three adaptation costs this story owns** (the machinery is reusable; its vocabulary is not):
   - a **tool-authorization decision vocabulary** — the existing gate's
     `[FlowNode("Accept","RequestRevision","Reject","Escalate")]` and its `ReadDecision` /
     `ParseDecisionFailClosed` are pinned to the four `AcceptanceDecision` kinds. Define
     `ToolAuthorizationDecision` (`Authorize` / `Deny` / `Escalate`) with its own outcome set and its
     own fail-closed parse defaulting to **`Deny`** (the document gate fail-closes to `Escalate`;
     for a capability, deny is the safe pole).
   - a **`RequestedAtUtc` equivalent** — the existing gate throws
     `DOCUMENT.DECISION.MISSING_REQUESTED_AT` when it is missing/unparseable, because the resume
     callback runs on a rehydrated activity. Carry the same required input and the same loud failure.
   - a **new bookmark prefix + registration** — add
     `LifecycleBookmarks.ForToolAuthorization(tenantId, sessionId)` alongside `ForDecisionSession` /
     `ForDocumentInput`, and register `WaitForToolAuthorizationActivity` in
     `LifecycleBookmarks.CanonicalSuspendActivities` (L98–105) or the structural build gate rejects it
     as a non-canonical suspend.

4. **Ship the live-read autonomy seam.**
   *Corrected: an earlier draft asked for "an input for the current autonomy level, read live" while
   also asserting "the value is not captured into the workflow state" — these read as contradictory
   because the draft assumed a single Elsa-hosted gate. There are two surfaces, and the rule bites on
   only one of them.*
   - **Stage 1 runs in `Tamma.Api`, not in a workflow.** There is no `[Input]` and no workflow state:
     the resolver is consulted once per `POST /api/v1/llm/call`, which *is* the live read.
   - **The engine-side gate activity does run in a workflow**, and there the rule is load-bearing: the
     autonomy value must be an `Input<int>` bound to a **delegate that consults the resolver at each
     activity execution**, never a value seeded into a workflow variable at Init (the
     `DocumentLifecycleWorkflow` `ResolveRules`-at-Init anti-pattern). The re-read-on-resume behaviour
     this depends on is already proven in-repo: `WaitForDocumentDecisionActivity` re-reads
     `RequestedAtUtc.Get(context)` inside its rehydrated resume callback (L70–72, L178).
   - **`IAcceptanceRulesResolver` must be widened.** The Core interface declares only the two
     *per-document-type* methods; a tool call has no document type, so the dial 42-3 needs is the
     **principal base row**. `ResolveBaseAsync(Guid? userId)` / `ResolveBaseForTenantAsync(Guid tenantId)`
     exist on the concrete `AcceptanceRulesService` (L91/L100) but are **not** on the interface. Lift
     both onto `IAcceptanceRulesResolver` so the engine-side gate can reach the dial without a
     `Tamma.Api` reference.

5. **Loud failures, and reconciling the two allowlists.**
   - A step requesting a tool its role may **never** hold (not in `AllowedRoles` at any autonomy)
     fails at stage 1 with a typed `TammaError` — never a silent empty tool list that lets the agent
     hallucinate success.
   - Extend `ToolCallValidator`'s allowlist check to assert membership in the **eligible set for the
     run**, so a fabricated call to a filtered-out tool is rejected with the same posture as an
     off-allowlist call. Reconcile the two lists in the process: `loopConfig.AllowedTools` must either
     be derived from the eligible set or removed from the execution decision — leaving a fail-open
     `IsAllowed(name, null) == true` in the path defeats the gate.
   - **Carve-out for principal-bound executors (Story 39-5 D6).** `GetAcceptanceRulesTool` is
     constructed per principal by `GetAcceptanceRulesToolFactory` and deliberately **not**
     DI-registered as an `IToolExecutor`, so it is invisible to the registry and to stage 1. Today it
     is unreachable from the only tool loop (`GetExecutor` returns null for it) — nothing regresses.
     But stage 2's eligible-set check would reject it once a 39-17 host mounts it.
     **DECIDED — no blanket exemption; the host injects.** A factory-minted principal-bound executor
     is admitted only by being handed to the run explicitly: `ManagedAgent` accepts an optional
     per-run executor collection, folds those tools into the stage-1 eligible set (descriptor and
     binding resolution applied exactly as for a registered tool), and stage 2 then sees them as
     ordinary members. An unconditional exemption is rejected — it would create a second, ungoverned
     admission path, which is the failure this epic exists to close. Until a 39-17 host exists this
     collection is empty and nothing changes; an AC pins that an injected principal-bound tool is
     gated identically to a registered one, and that a tool absent from both the registry and the
     injected set is still rejected.

## Acceptance Criteria

1. `ManagedAgent.ToResolvedTools` returns only tools that are enabled, role-permitted and
   autonomy-eligible for the run context — table-driven across roles × autonomy levels × permission
   classes — and each returned `ResolvedTool` carries a non-empty `Description` and a non-null
   `InputSchema` (today it returns bare names).
1b. **Max-class descriptors are still offered.** A tool whose descriptor max is `Destructive` but
   whose binding-resolved effective ceiling is non-empty **is** in the eligible set (test: a
   `Destructive`-max executor with a binding granting `Mutating` is returned by `ToResolvedTools`),
   and is dropped only when the ceiling is empty (test: same executor, binding disabled / role not
   granted → absent). Without this pair, a filter on the raw descriptor max passes AC1 while making
   every Wave-3 write tool unreachable.
2. Autonomy is read per call, not cached: two runs of the same step, with the principal's dial changed
   between them from 72 to 95, resolve different eligible sets for a tool with floor 85. A second test
   asserts the engine-side gate activity re-reads the dial **after** a suspend/resume (the value
   observed on resume reflects a mid-suspend change), and that no autonomy value is written into
   serialized lifecycle state.
3. A call whose derived `PermissionClass` is `Destructive` produces **no** `executor.ExecuteAsync`
   invocation (asserted on a spy executor), terminates the run with
   `AgentRunFailureCodes.ToolAuthorizationRequired`, and the emitted `ToolAuthorizationRequest`
   carries `ToolName`, `Operation`, `Target`, and redacted arguments. Asserted on **both** branches —
   once with `EnableParallelTools = false` (the default) and once with it `true`.
4. Authorization is single-use and action-bound: after an `Authorize` decision for
   `(session, tool, operation, targetA)`, one matching invocation executes; a second identical
   invocation re-gates, and an invocation for `targetB` re-gates. A `Deny` decision takes the loud
   handoff edge with no execution.
5. An unparseable / missing decision payload on resume fail-closes to `Deny` (not `Escalate`, not
   execute), and a missing `RequestedAtUtc` throws the typed loud error at suspend time.
6. A step requesting a tool its role is never granted fails at stage 1 with a typed error — asserted
   **not** to produce an empty-but-successful tool list.
7. `ToolCallValidator` rejects a fabricated call to a filtered-out tool at invocation time,
   independent of stage 1; and a test pins that the execution path no longer admits a call solely
   because `loopConfig.AllowedTools` is null.
8. **Principal-bound executors are gated, not exempt.** A factory-minted `IToolExecutor` handed to the
   run through `ManagedAgent`'s per-run executor collection (Scope 5) is subject to the identical
   stage-1 and stage-2 treatment as a registered tool — a test drives one through both stages and
   asserts a `Destructive`-classed call on it escalates rather than executing. A tool present in
   **neither** the registry nor the injected collection is still rejected (no third admission path).
   With no 39-17 host today the collection is empty; a test asserts an empty collection changes
   nothing about the six built-ins' eligibility.
9. single-user and SaaS both enforce: single-user reads the user's `tool_bindings` and
   `ResolveBaseAsync(userId)`; SaaS reads the tenant's and `ResolveBaseForTenantAsync(tenantId)`; a
   SaaS `member`-run agent gets the tenant_admin's resolved grants (no per-user layer), and the
   authorization request never crosses the tenant boundary (bookmark name is tenant-folded).

## Events

`TOOL.RESOLVED` (eligible-set size + filtered count, per run), `TOOL.DENIED` (role / autonomy denial,
with the denial reason), `TOOL.ESCALATED` (routed for authorization, carrying `operation` + `target`),
`TOOL.AUTHORIZED` / `TOOL.AUTHORIZATION_DENIED` (the decision outcome). All tagged
`issueId` / `tenantId` / `toolName` / `permissionClass`. Arguments, if included at all, are redacted.
(The invocation-level `TOOL.INVOKED/SUCCEEDED/FAILED` are 42-5.)

*Corrected: these are **not** emitted via `TammaEventEmitter` → `tamma:events` → `EventDrain`.* That
emitter structurally requires an `ActivityExecutionContext` **and** an `IActivity`
(`TammaActivity.Emit(context, source, logger, evt)`), and stages 1 and 2 run in `Tamma.Api`, outside
any workflow context — `ManagedAgent` already holds `IEventRepository` directly. Append directly there.
The **engine-side** authorization gate activity is the one exception: it *is* an Elsa activity with a
context, so it emits its request/provided pair through `TammaEventEmitter` exactly as
`WaitForDocumentDecisionActivity` emits `APPROVAL.REQUESTED` / `APPROVAL.PROVIDED`.

## Single-user vs SaaS

- **single-user:** the sole user's `tool_bindings` (42-2) drive role/floor/enablement, and the dial is
  the user's principal base row; authorization of a gated action routes to the single
  orchestrator/user.
- **SaaS:** the tenant's bindings and the tenant's base row drive it; authorization routes to the
  tenant orchestrator or a holder of the appropriate tenant role — hard-scoped to the tenant. The
  bookmark name is tenant-folded (`LifecycleBookmarks.Compose` normalizes the tenant segment), so a
  cross-tenant resume cannot resolve another tenant's authorization gate.

## Dependencies

- **42-1** (`PermissionClass` / `AutonomyFloor` on the descriptor), **42-2** (`IToolBindingResolver`;
  and 42-2's note that `ManagedAgentRequest` has no `UserId` — threading it is this story's).
- **Wave 0.5 cleanup** — delete `ResolveToolsActivity` (or fix its `read_file` built-in to
  `file_read`) before anything lands here, and reconcile the two allowlists.
- **Epic 39 machinery — reused, with three named adaptation costs** (Scope 3):
  `WaitForDocumentDecisionActivity` + `DocumentDecisionResumeEndpoint` (keyed tenant + session),
  `LifecycleBookmarks`, and the Task View. `AcceptanceRequest` itself is **not** reusable.
- **Epic 39 autonomy dial — NOT an existing consumable behaviour.** No code branches on
  `AutonomyLevel`; `IAcceptanceRulesResolver` does not expose the base row; and the live-read seam does
  not exist. All three are **this story's** to ship, not inherited from 39-5.
- **`Tamma.Core/Agents` `AgentRole`** for the role check (Epic 41-1's added roles —
  `ux_designer` / `scrum_master` / `project_manager` — extend the grantable set with no change here).
- **Unblocks:** every tool family (42-7/8/9) and MCP (42-6) — governed the moment they declare a
  descriptor **and** a per-call `Describe`.

## Risks

- **Epic 39 sign-off is a hard prerequisite, not a courtesy.** `AutonomyFloor ≤ currentAutonomy` is the
  first control-flow use of `AcceptanceRules.AutonomyLevel` anywhere. Epic 39 must confirm (a) the dial
  is intended to gate behaviour rather than annotate audit, (b) the live-read seam 42-3 ships is the
  one it wants, and (c) whether `DocumentLifecycleWorkflow`'s resolve-at-Init cache is corrected in
  the same change or left as a known divergence. Building the filter before that answer risks
  reworking the seam.
- **Cross-process suspend.** The gate spans two processes: the decision is *detected* in `Tamma.Api`
  inside a blocking HTTP call and *taken* in the Elsa engine. If the engine-side branch is not built,
  stage 2 degrades to a plain refusal — safe, but the "acceptor is an actor" promise is unmet and the
  step just fails. Land the engine branch and the API refusal together, and pin the round trip in AC3.
- **Grant scope creep.** A grant keyed on `(tool, operation, target)` still permits non-target
  arguments to differ between the gated call and the re-dispatched one (the model re-emits arguments
  from a fresh turn). Mitigation: the redacted full argument set rides both the request and the
  `TOOL.AUTHORIZED` event, so a divergence is auditable; families whose non-target arguments are
  themselves consequential must fold them into `Target`. This is a stated residual, not a solved one.
- **Two-stage drift.** Stage 1 and stage 2 evaluating different rules is worse than one stage: the
  agent is offered a tool it can never fire. Mitigation: both stages call the same resolver and the
  same `Describe`; AC1 and AC3 assert against one shared table of role × autonomy × class fixtures.
- **Silent empty tool list.** The classic failure is handing the agent zero tools and letting it claim
  success. AC6 pins this as a loud, typed failure.

## Estimated Effort

Large. ~6–8 days (two enforcement stages across two processes, a new suspend activity + bookmark +
resume endpoint + decision vocabulary, the live-read resolver seam and the `IAcceptanceRulesResolver`
widening, plus a heavy cross-mode × cross-autonomy × both-execution-branches test matrix). Gated on
the Epic 39 sign-off above.
