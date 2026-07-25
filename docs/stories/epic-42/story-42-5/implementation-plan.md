# Implementation Plan — Story 42-5: Tool-Use DCB Audit

## Reconciled scope — differs from the story file

**Epic 42 was reconciled against Epic 43 on 2026-07-25.** 42-5's verdict is **"Narrowed — keeps the
invocation trio; loses the governance events (Epic 43 owns one event family) and its dependency on
`InlineToolLoopResult.ToolCalls`, which is documented as always empty."** The deltas:

| Story file says | Reconciled |
|---|---|
| Scope §1's payload: `TOOL.INVOKED` carries `toolName, permissionClass, autonomyAtCall, toolCallId, argsRedacted` | **`permissionClass` and `autonomyAtCall` are dropped.** `PermissionClass` no longer exists — 42-1 is rewritten and `ToolDescriptor` is now `(RequiredSecret, Suspends)`. `autonomyAtCall` is a governance datum belonging to Epic 43's single event family; reading the dial here would be a second consumer of a value Epic 43 Story 1 is still turning into one constant. Tags become `toolName` + `toolCallId` (D3). |
| Scope §1's parenthetical routing `TOOL.RESOLVED`/`DENIED`/`ESCALATED`/`AUTHORIZED` to 42-3 and `TOOL.BINDING_*` to 42-2 | **42-2 and 42-3 are DELETED.** Those events do not exist and are not this story's to reference. Epic 43 owns **one** governance event family, emitted at its own seams. This story owns the **invocation trio and nothing else**. |
| **Scope §5** — "it must also **populate `InlineToolLoopResult.ToolCalls`** … so 32-6's `EmitTrailToolCallsAsync` stops being structurally dead" | **DELETED.** The reconciliation removes this story's dependency on that field. Verified as documented-always-empty at `IInlineToolLoopRunner.cs:133-134` (*"Empty in the verbatim extraction (the loop tracks a count only); populated by a follow-on"*) and `:112-114`. Reviving `AGENT.TOOL_CALL.*` is a second durable tool-call family alongside `TOOL.*` — exactly the duplication the reconciliation exists to remove. The **documentation** half of §5 survives as D6. |
| **AC8** — "`ToolCalls` is populated for both branches, and a run that executes N tools produces N `AGENT.TOOL_CALL.*` rows" | **DELETED** with Scope §5. |
| Dependencies: "**42-1** — `PermissionClass` for the tag, `RequiredSecret` for the field-suppression set" | `PermissionClass` is gone. `RequiredSecret` is still useful for field suppression but **not required** — pattern redaction covers a tool with no descriptor. **42-1 is downgraded from a blocker to a soft enhancement** (D7), so this story can land in parallel with it. |
| Dependencies: "**Wave 0.5 cleanup** — the two independently-sourced allowlists … 42-3 owns reconciling them" | 42-3 is deleted. The two allowlists (`InlineToolLoopRunner.cs:262-263` vs `:342`/`:419`) still both produce rejections, and **nobody owns reconciling them**. Epic 43's Seam B lands on this same path and its README notes the existing fail-open allowlists stay. Recorded as **G1**; this story audits whichever rejection fires and does not reconcile them. |

**Unchanged and still in scope:** the trio itself, the both-branches hook requirement, the direct
`IEventRepository` append in `Tamma.Api`, the `issueId` threading fix, the redaction rule, and the
never-fail-the-run posture.

## Scope & Deliverable

When this story is done, every tool invocation on **both** execution branches of `InlineToolLoopRunner`
writes a durable, secret-free DCB pair to `domain_events`: `TOOL.INVOKED` before execution and exactly one
of `TOOL.SUCCEEDED` / `TOOL.FAILED` after it — including the timeout, exception and rejected-by-validator
paths. Rows carry `issueId` (which is **dropped today**, and this story fixes that), `tenantId`, `toolName`,
`toolCallId` and `correlationId`, and never a credential. Emission is Api-side, appended directly through
`IEventRepository`, sink-independent, and never fails the run. The ephemeral `TOOL_LOOP.*` SSE stream is
untouched. Nothing here governs anything — this is the permanent record Epic 43's gate decisions will be
correlated against.

## Pre-Reading

- `docs/stories/epic-42/story-42-5/42-5-tool-use-dcb-audit.md` — the story (**read the Reconciled scope table first**; its three "Corrected —" blocks in *The gap* are accurate and verified, see below)
- `docs/stories/epic-42/README.md` — the reconciliation verdicts; "DCB audit transport (42-5)" in Dependencies
- `docs/stories/epic-43/README.md` — Enforcement Seam B (*"one call site in the shared tool-loop path… after sanitization and before the parallel/sequential fork"*) and **Audit** (*"one event family… emission is best-effort except denials under enforcement"*) — the two places Epic 43 lands on this same code
- **`apps/tamma-elsa/src/Tamma.Api/Services/Agents/InlineToolLoopRunner.cs`** — the whole `AgenticToolLoop` (`:119-640`). Ctor `:45-67` (ten params, **all nullable**, six defaulted). Turn loop `:153`. `rejectedToolCalls` `:259`. Validator block `:260-282` with the `tools`-derived allowlist at `:262-263` and the sanitized-args write-back at `:271`. Rejected calls → tool messages `:301-327`. `executableToolCalls` `:330-332`. **The fork at `:335`** (`EnableParallelTools && _parallelExecutor != null && _toolRegistry != null && executableToolCalls.Count > 0`). **Parallel branch `:335-405`** — the `loopConfig.AllowedTools` filter at `:342`, `validForExecution` `:339`/`:369`, `ExecuteToolsInParallelAsync` `:375-379`. **Sequential branch `:406-522`** — registry-null `:413-418`, allowlist `:419`, `GetExecutor` `:431`, unknown-tool `:432-440`, linked CTS `:458-460`, **`executor.ExecuteAsync` `:462-463`**, `TOOL_EXECUTING`/`TOOL_COMPLETED` emits `:444-449` / `:490-496`. Output sanitization at all four sites `:312-317`, `:355-360`, `:389-394`, `:506-511`
- `apps/tamma-elsa/src/Tamma.Api/Services/Agents/IInlineToolLoopRunner.cs` — `RunAsync` `:73-86` (the signature this story changes); `InlineToolLoopResult` `:116-156`; **`ToolCalls` `:135` with the always-empty docs at `:133-134` and `:112-114`**; `ToolCallSummary` `:163-167`
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Models/LlmCallModels.cs:464-474` — `ToolExecutionResult(ToolCallId, ToolName, Success, Output, DurationMs)`, `ErrorMessage` `:473`; `:479-501` `ToolLoopConfig`, **`EnableParallelTools` default `false` at `:500`**
- `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/ToolLoopEventEmitter.cs` — five methods and their event strings: `EmitTurnStarted` `:34`/`:53`, `EmitToolExecuting` `:59`/`:76`, `EmitToolCompleted` `:82`/`:102`, `EmitTurnCompleted` `:108`/`:126`, **`EmitLoopCompleted` `:132`/`:151`**
- `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/IToolLoopEventSink.cs` — the interface `:7`, `WriteEventAsync` `:16`, `NullToolLoopEventSink` `:23-31`; `apps/tamma-elsa/src/Tamma.Api/Services/Streaming/BusToolLoopEventSink.cs:92`/`:98`/`:105` — the three mappings (note `TOOL_LOOP.COMPLETED` → `Final` at `:105`)
- `apps/tamma-elsa/src/Tamma.Activities/ToolExecution/ParallelToolExecutor.cs` — class `:16`, ctor `:21-24` (`ILogger` only), `ExecuteToolsInParallelAsync` `:42-49` (**returns one result per call, in input order, never throws**), `ExecuteSingleToolAsync` `:91-186` with the timeout catch `:147-166` and general catch `:167-185`
- `apps/tamma-elsa/src/Tamma.Data/Repositories/IEventRepository.cs:7` — `Task<DomainEvent> AppendAsync(DomainEvent evt);` — **no `CancellationToken` anywhere on the interface**
- The three direct-append precedents and their **three different failure policies**:
  `Tamma.Api/Services/Alerts/AlertEventEmitter.cs` (appends `:71`, `:111`, `:151`, `:231`; **swallow + warn** `:81-86`; the no-CT note `:88`), `Tamma.Api/Services/PromptStore/PromptEventsService.cs` (append `:111`; **swallow + warn** `:113-119`, *"never block the prompt mutation on event-store failure"*),
  `Tamma.Api/Services/Documents/EscalationDispositionService.cs` (append `:116`; **fail-loud** `:115`, *"the event IS the operation"*)
- `apps/tamma-elsa/src/Tamma.Activities/Core/TammaActivity.cs:82-147` — `TammaEventEmitter`; every method takes both an `ActivityExecutionContext` and an `IActivity`; `EmitInternal` `:129-147` writes only to `TransientProperties["tamma:events"]` and **never touches `IEventRepository`**
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/ToolOutputHelper.cs` — `MaxOutputBytes` `:12`, `Truncate` `:23`, `RedactSecrets` `:72-120`; `apps/tamma-elsa/src/Tamma.Core/Redaction/CredentialRedactor.cs:71` — `Clean(string?)`
- `apps/tamma-elsa/src/Tamma.Api/Program.cs:775` (`ParallelToolExecutor`, **Singleton**), `:795-796`/`:800-801` (`IToolLoopEventSink`), `:808-809` (`IInlineToolLoopRunner`, **Scoped**), `:810-811` (`ManagedAgent`, Scoped)
- `docs/stories/epic-42/story-42-4/implementation-plan.md` — D8's redaction split; this story is the "never-hold + pattern after it" half

## Corrections to the story

The story's own three "Corrected —" blocks are **verified accurate**: `TammaEventEmitter` cannot be used
here; `Tamma.Api` does hold `IEventRepository` directly; and hooking `ParallelToolExecutor` alone would miss
the default path (`EnableParallelTools` defaults `false` at `LlmCallModels.cs:500`). Four further
corrections:

- **Z1 — `TOOL_LOOP.COMPLETED` is never emitted, and something already consumes it.**
  `ToolLoopEventEmitter.EmitLoopCompleted` (`:132`) has **no call site in `InlineToolLoopRunner`** — the
  runner calls only `EmitTurnStarted`, `EmitToolExecuting`, `EmitToolCompleted` and `EmitTurnCompleted`.
  Meanwhile `BusToolLoopEventSink:105` maps `TOOL_LOOP.COMPLETED` → `Final`. So the SSE stream's terminal
  frame is dead code on the producing side, and two more events (`TURN_STARTED`, `TURN_COMPLETED`) map to
  nothing on the consuming side. This matters for AC7 ("`TOOL_LOOP.*` unchanged"): the honest baseline is
  *three of five emitter methods reach a sink mapping, one event the sink maps is never produced*. Pin the
  baseline as-is; **do not opportunistically fix it** inside an audit story — file it.
- **Z2 — `ParallelToolExecutor`'s doc contradicts its registration.** Its class doc (`:13-14`) says it is
  *"scoped per activity execution (not static) to avoid cross-session interference in the semaphore
  dictionary"*, but `Program.cs:775` registers it **Singleton**, so its `_fileLocks`
  `ConcurrentDictionary` (`:19`) is process-wide. Not this story's to fix, and it does not affect audit
  correctness — but an implementer reading that doc will draw the wrong conclusion about instance lifetime
  when deciding where to put per-run state. Put no per-run state in the parallel executor; this story does
  not touch it at all (Scope's assembly-placement rule already says so).
- **Z3 — the `ManagedAgent` line cites are unverified; re-derive them.** The story names `ManagedAgent.cs`
  L453 (`EmitTrailToolCallsAsync` call), L751 (the repair-ring emitter's direct `IssueId` use), and
  `Program.cs` L829 (`IAgentTrailEmitter` registration), plus `ToolExecutorRegistryTests.cs:23`,
  `InlineToolLoopRunnerTests.cs:191`, `AgenticToolLoopIntegrationTests.cs:316` for the mock fixtures. Only
  the *claims* are load-bearing here — that `ManagedAgent.RunContext`/`ToTrailContext` carries no `IssueId`
  while `ManagedAgentRequest.IssueId` is in scope. **Verify each line before editing**; `ManagedAgent.cs` is
  a large file (`ToResolvedTools` alone is `:923-937`) and these numbers drift.
- **Z4 — `IEventRepository.AppendAsync` takes no `CancellationToken`, on any of its eleven members.** The
  story does not mention it; `AlertEventEmitter.cs:88` documents the workaround (`_ = ct;`). The emitter
  signature must therefore accept a `ct` for symmetry and discard it at the append, with the same comment —
  not pretend cancellation is honoured.

## Design Decisions

- **D1 — one private emit helper, called from both branches, never two inline appends.** The story's own
  Risks section names branch drift as the failure this story is correcting; the mitigation is structural.
  `IToolAuditEmitter` (in `Tamma.Api.Services.Agents`, modelled on the `AlertEventEmitter` shape) is injected
  into `InlineToolLoopRunner` as one more **nullable, defaulted** ctor parameter — matching the existing ten
  (`:45-67`) so no call site breaks — and the runner calls a single private
  `EmitToolAuditAsync(...)` wrapper from four places: before/after `executor.ExecuteAsync` on the sequential
  branch (`:462-463`), and before/after `ExecuteToolsInParallelAsync` on the parallel one (`:375-379`).
- **D2 — hook points, exactly.**

  | Branch | `TOOL.INVOKED` | Terminal |
  |---|---|---|
  | **Sequential** (default, `EnableParallelTools == false`) | immediately before `:462` | after `:462`, and inside **both** the timeout and the general catch — every path that produces a `ToolExecutionResult` |
  | **Parallel** (opt-in) | once per entry of `validForExecution` (`:339`/`:369`) before `:375` | once per returned result — `ExecuteToolsInParallelAsync` returns one result per call **in input order** and **never throws** (`:42-49`, catches at `:147-166` and `:167-185`), so the pairing is total by construction |
  | **Rejected before execution** — validator (`:260-282`) or either allowlist (`:342`, `:419`) or unknown tool (`:432-440`) or null registry (`:413-418`) | `TOOL.INVOKED` **is** emitted | `TOOL.FAILED` with the rejection reason. A call the model made and the platform refused is exactly the row an auditor needs; emitting nothing would make refusals invisible |

- **D3 — tags reuse the landed `AgentTrailTags` contract; no second vocabulary, no governance fields.** Every
  `TOOL.*` event is tagged through `AgentTrailTags.Build(ctx, extra)` with `extra` adding **`toolName`** and
  **`toolCallId`** only; `issueId`, `tenantId`, `correlationId`, `agentId` and `role` come from
  `AgentTrailContext`. Dropped per the Reconciled scope: `permissionClass` (no longer exists) and
  `autonomyAtCall` (Epic 43's). Data members: `TOOL.INVOKED` → `argsRedacted`; `TOOL.SUCCEEDED` →
  `durationMs`, `outputSizeBytes`; `TOOL.FAILED` → `durationMs`, `failureReason` (redacted). `durationMs`
  comes free from `ToolExecutionResult.DurationMs` (`LlmCallModels.cs:464-474`).
- **D4 — the `issueId` fix is the smallest change that makes the trail queryable, and it is a real signature
  change.** `IInlineToolLoopRunner.RunAsync` (`:73-86`) takes only a `correlationId`. One level up,
  `ManagedAgent.RunContext` carries `TenantId`/`Role`/`CorrelationId` but no `IssueId`, and `ToTrailContext`
  never projects one — so `AgentTrailContext.IssueId` is null on every trail event emitted today, even though
  `ManagedAgentRequest.IssueId` is in scope. Fix both halves: project `request.IssueId` into
  `ToTrailContext`, and extend `RunAsync` to take the resulting context. **Fallout:** the
  `Mock<IInlineToolLoopRunner>` fixtures in `ManagedAgentTests` and `ManagedAgentContentValidationTests`
  (both `MockBehavior.Strict`) and `BufferedNonRegressionTests` must be updated — re-derive the exact set
  from the build (Z3), do not trust the story's list.
- **D5 — never-hold + field suppression + pattern + truncation, in that order, and no value matching.** The
  emit call site receives `(toolCall, descriptor?, result-metadata)` — **there is no parameter through which
  a credential could arrive**, which is a signature property and is asserted as one. Argument fields named by
  the descriptor's `RequiredSecret` are dropped rather than printed (D7 makes this optional). Everything that
  does cross goes through `ToolOutputHelper.RedactSecrets` (`:72-120`) and `CredentialRedactor.Clean`
  (`Tamma.Core/Redaction/CredentialRedactor.cs:71`); oversized payloads through `ToolOutputHelper.Truncate`
  (`:23`, 50 KB, which redacts first). **No value-match denylist** — that would require holding plaintext at
  a boundary 42-4 D8 forbids it to reach. Both helpers are pattern-based and match no arbitrary bound token;
  the plan says so rather than implying DLP.
- **D6 — the three-sink invariant is documented, and the dead family is left dead.** Written down, in code
  comments and in the emitter's XML doc: `IToolLoopEventSink` = ephemeral live progress (with Z1's honest
  baseline); `TOOL.*` = the durable audit of record; `AGENT.TOOL_CALL.*` (32-6) = a per-agent analytics
  family that **structurally never fires** because `InlineToolLoopResult.ToolCalls` is always empty
  (`:133-134`), `EmitTrailToolCallsAsync` returns immediately on an empty list, and it is called only on the
  success path. Per the Reconciled scope this story does **not** revive it — two durable families both
  claiming to record tool calls is the duplication the Epic 43 reconciliation removed. **Filed** as a
  follow-on: either populate `ToolCalls` and keep `AGENT.TOOL_CALL.*` as analytics, or delete the family.
  Deciding that inside an audit story is how the duplication started.
- **D7 — 42-1 is a soft dependency, not a blocker.** With `permissionClass` gone, the only descriptor use
  left is D5's field suppression, which is an *enhancement*: a tool with a null descriptor still gets full
  pattern redaction. So this story compiles and passes against today's `IToolExecutor` and picks up field
  suppression automatically once 42-1 lands. That decoupling is worth having — it lets the audit trail exist
  before any family ships, which is the whole point of it being Wave 1.
- **D8 — failure policy is chosen, not copied: swallow and warn.** The three landed appenders disagree
  (`AlertEventEmitter` swallows, `PromptEventsService` swallows, `EscalationDispositionService` is fail-loud
  because "the event IS the operation"). Here the event is a *record of* an operation that already happened,
  so a write failure must not fail the tool call — swallow + WARN + a breadcrumb, the `AlertEventEmitter`
  posture. Note the asymmetry with Epic 43, whose audit is *"best-effort except denials under enforcement,
  which are not swallowed"*: that exception is about a **gate blocking** an action, which this story never
  does.

## Implementation Steps

1. **CREATE `apps/tamma-elsa/src/Tamma.Api/Services/Agents/IToolAuditEmitter.cs` + `ToolAuditEmitter.cs`** —
   direct `IEventRepository.AppendAsync`, tags via `AgentTrailTags.Build`, D5's redaction pipeline, D8's
   swallow-and-warn, and Z4's `ct`-accepted-and-discarded signature with the explanatory comment.
2. **MODIFY `apps/tamma-elsa/src/Tamma.Api/Services/Agents/ManagedAgent.cs`** — project `request.IssueId`
   into `ToTrailContext` (D4). Verify the line numbers first (Z3).
3. **MODIFY `apps/tamma-elsa/src/Tamma.Api/Services/Agents/IInlineToolLoopRunner.cs` + `InlineToolLoopRunner.cs`**
   — extend `RunAsync` (`:73-86`) to carry the trail context; add the nullable `IToolAuditEmitter` ctor
   parameter; add the one private `EmitToolAuditAsync` helper and its four call sites per D2. **Do not touch
   `ParallelToolExecutor`** (Z2, and the Scope's placement rule).
4. **MODIFY the affected test fixtures** — the strict `Mock<IInlineToolLoopRunner>` set (D4); derive from the
   build.
5. **MODIFY `apps/tamma-elsa/src/Tamma.Api/Program.cs`** — register `IToolAuditEmitter` next to
   `IInlineToolLoopRunner` (`:808-809`), Scoped to match.
6. **CREATE the test suites** (Test Plan).
7. **Finish:** full `dotnet test`; `dotnet ef migrations has-pending-model-changes` clean; file the two
   follow-ons (Z1's dead `TOOL_LOOP.COMPLETED`, D6's dead `AGENT.TOOL_CALL.*`) and G1.

## Data & Migrations

None. Rows go to the existing `domain_events` via `IEventRepository.AppendAsync`, which routes into the
resolving tenant's `t_<hex>` schema.

## Events

- **Defines:** `TOOL.INVOKED`, `TOOL.SUCCEEDED`, `TOOL.FAILED` — `AGGREGATE.ACTION.STATUS` with the `TOOL`
  aggregate, matching `AgentTrailEventTypes`. Tags per D3. This story also owns the **shared Api-side emit
  path and the redaction rule** that 42-4's `TOOL.SECRET_ACCESSED` reuses.
- **Does not define:** any governance event. Epic 43 owns one event family for gate decisions.
- **Unchanged:** the ephemeral `TOOL_LOOP.*` SSE events, with Z1's baseline pinned as-is.

## Test Plan

- **`ToolAuditEmitterTests`** (unit) — the trio's tag/data shape through `AgentTrailTags`; a **structural**
  assertion that no emitter method accepts a plaintext-credential parameter (D5's never-hold is a signature
  property); a throwing `IEventRepository` produces a WARN and no exception (D8).
- **`ToolAuditBothBranchesTests`** (integration, the mandatory pair) — one run with
  `EnableParallelTools == false` (**the default**) and one with it `true`; each asserts `TOOL.INVOKED` then
  exactly one terminal, with matching `toolCallId`. **The default-path test is mandatory** — without it the
  suite can pass while the path every run actually takes emits nothing. **Covers AC1, AC2.**
- **`ToolAuditFailurePathsTests`** — a failing tool, a timing-out tool (the linked CTS at `:458-460`), a
  throwing tool, and a validator-rejected call each produce `TOOL.FAILED` and never a silent success; the
  parallel executor's internal timeout/exception paths surface as `Success == false` rather than a throw
  (`:147-166`, `:167-185`), and are audited identically. Plus one rejected-by-allowlist case per allowlist
  (`:342` and `:419`) so whichever survives G1 is covered. **Covers AC3.**
- **`ToolAuditRedactionTests`** — args carrying `ghp_…`, `Authorization: Bearer …`, `api_key=…` emit
  `TOOL.INVOKED` with the placeholder; with 42-1 present, a field named by `RequiredSecret` is absent
  entirely; **without** 42-1 (null descriptor) the pattern path still redacts — pinning D7's soft dependency.
  **Covers AC4.**
- **`ToolAuditLineageTests`** — every `TOOL.*` row carries a **non-null** `issueId` round-tripped from
  `ManagedAgentRequest.IssueId` (it is null today — this is the regression this story fixes), plus
  `tenantId`/`toolName`/`toolCallId`; a replay test reconstructs an issue's tool timeline from
  `domain_events` alone. **Covers AC5.**
- **`ToolAuditSinkIndependenceTests`** — with `NullToolLoopEventSink` wired and `EnableStreaming == false`,
  the `TOOL.*` rows are still persisted; and a baseline pin on the SSE side asserting the **current**
  emitter/sink behaviour including Z1 (three emitter methods reach a mapping; `TOOL_LOOP.COMPLETED` is never
  produced) so a regression is detectable without this story silently fixing it. **Covers AC6, AC7.**

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1 — `TOOL.INVOKED` then exactly one terminal, matching `toolCallId` | 1, 3 (D1/D2) | `ToolAuditBothBranchesTests` |
| 2 — both branches covered, default path mandatory | 3 (D2) | `ToolAuditBothBranchesTests` (the `false` case is a separate required test) |
| 3 — failing / timing-out / throwing / rejected all emit `TOOL.FAILED` | 3 (D2) | `ToolAuditFailurePathsTests` |
| 4 — redaction, plus the structural never-hold guarantee | 1 (D5) | `ToolAuditRedactionTests` + the signature assertion in `ToolAuditEmitterTests` |
| 5 — non-null `issueId` + the rest of the tag set; replayable timeline | 2, 3 (D3/D4) | `ToolAuditLineageTests` |
| 6 — sink-independent durable emission | 1, 3 | `ToolAuditSinkIndependenceTests` |
| 7 — `TOOL_LOOP.*` unchanged (against Z1's honest baseline) | — | `ToolAuditSinkIndependenceTests` baseline pin |
| 9 — an append failure never fails the run | 1 (D8) | `ToolAuditEmitterTests` throwing-repository case |
| ~~8 — populate `ToolCalls`; N `AGENT.TOOL_CALL.*` rows~~ | — | **DELETED — see Reconciled scope; documented instead (D6) and filed** |

## Blocks / Blocked by

- **Blocked by — nothing hard.** 42-1 is a **soft** dependency (D7): field suppression improves when it
  lands; nothing here waits on it. This is a deliberate consequence of the reconciliation removing
  `permissionClass` from the tag set, and it lets the audit trail exist before any Wave-3 family ships.
- **Shares a code path with — Epic 43 Seam B**, which lands *"one call site in the shared tool-loop path…
  after sanitization and before the parallel/sequential fork"* — i.e. between `InlineToolLoopRunner.cs:282`
  and `:335`, immediately upstream of this story's hooks. **Coordinate the edit order**; the two are
  compatible (Epic 43 denies, this story records) but they touch adjacent lines, and Epic 43's Seam B is a
  *required* constructor parameter while this story's emitter is nullable-and-defaulted.
- **Unblocked dependency — G1: the two allowlists still disagree and nobody owns them.**
  `InlineToolLoopRunner` derives one allowlist from the `ResolvedTool` names it sent the model (`:262-263`)
  and a second from `loopConfig.AllowedTools` (`:342`, `:419`). 42-3 was to reconcile them; it is deleted,
  and Epic 43's README says the two existing fail-open allowlists **stay**. This story audits whichever
  rejection fires and does not reconcile them — recorded as an open item, not silently absorbed.
- **Blocks — every Wave-3 family.** No `Destructive`-shaped capability should ship without this trail; 42-7,
  42-8A, 42-8B and 42-9 all reuse `TOOL.*` with family-specific tags. **42-4** reuses this story's emit path
  and redaction rule for `TOOL.SECRET_ACCESSED`.
- **Filed follow-ons (not this story's):** Z1's never-emitted `TOOL_LOOP.COMPLETED` against a 32-x owner;
  D6's structurally-dead `AGENT.TOOL_CALL.*` against 32-6; Z2's Singleton-vs-doc contradiction in
  `ParallelToolExecutor`.

## Risks & Mitigations

- **Branch drift — a future third execution path silently unaudited.** The exact failure this story corrects.
  Mitigation: D1's single shared helper (never duplicated inline appends) plus AC2's per-branch tests; and
  because Epic 43's Seam B is landing on the same fork, a third path would have to defeat two gates.
- **Hot-path cost: two durable appends per tool call, unbatched.** Unlike the engine's `tamma:events` drain
  there is no batching seam. Mitigation: the emitter never throws and never blocks the result (D8); tool
  calls are already DB-round-trip-scale. If volume bites, the fix is a buffered writer behind the same
  interface — not fewer events.
- **The `RunAsync` signature change breaks strict mocks (D4).** Mitigation: derive the fixture list from the
  build rather than the story's cites (Z3); the breakage is a compile error, the safest failure mode.
- **Redaction gaps.** Pattern redaction is not DLP — a novel credential shape in a nested arg can slip
  through. Mitigation: never-hold makes the *bound* secret (the only class the platform issues) structurally
  unreachable; pattern redaction is the second line for user-supplied strings; the plan states the limit
  rather than implying coverage.
- **Temptation to "just also fix" Z1 and D6's dead families while in the file.** Both are one-line-looking
  changes with real semantics (reviving `AGENT.TOOL_CALL.*` recreates a second durable family; emitting
  `TOOL_LOOP.COMPLETED` changes an SSE contract the dashboard consumes). Mitigation: both explicitly filed,
  both pinned as baselines by `ToolAuditSinkIndependenceTests` so a drive-by change fails a test.
- **Story-vs-canon tensions:** none remaining. The story's own three corrections were verified accurate; the
  reconciliation removed the rest by deletion.

## Est. Effort

| Step(s) | Work | Days |
|---|---|---|
| 1 | `IToolAuditEmitter` + implementation + redaction pipeline | 0.75 |
| 2–3 | `issueId` threading, `RunAsync` signature, the shared helper + four hook sites | 1.0 |
| 4–5 | Strict-mock fixture fallout + DI wiring | 0.5 |
| 6 | Six test suites incl. both-branch integration and the replay test | 1.0 |
| 7 | Full green + filing the three follow-ons | 0.25 |
| **Total** | | **3.5** (story estimate: ~3–4 days) |

Narrowing removed the `ToolCalls`/`AGENT.TOOL_CALL.*` revival (roughly half a day, and a disproportionate
share of the story's risk) and two tag fields; it added nothing. The figure sits at the story's own midpoint
because the `RunAsync` signature change and its fixture fallout — not the emitter — were always the bulk.
