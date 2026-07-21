# Implementation Plan — Story 39-9: Deterministic Repair Ring — Validator-Feedback Repair in the Managed Layer

## Scope & Deliverable

When this story is done, the managed execution layer (`ManagedAgent` → `InlineToolLoopRunner`) can run a bounded, harness-generated repair turn when a produced document fails its deterministic validator: the domain-phrased violations are appended verbatim as a user-role message in the SAME conversation, the model is re-invoked, and the output is re-validated — at most `RepairRingOptions.MaxRepairTurns` times (default 1, hard cap 2), gated per document type (default: no types enabled, mechanism ships dark). Exhaustion surfaces as a typed, non-transient content failure (`CONTENT_VALIDATION_FAILED`, body httpStatusCode 422) carrying the final violations and per-turn history for the 39-6 lifecycle's `ValidationExhausted` lineage — never a bare exception. `ProviderAttemptDiagnostic` gains an additive nullable `FailureCode`, and both `RecordDiagnostics*` activities exclude `"content_validation"` diagnostics from circuit-breaker failure recording, so content failures can never open the breaker on a healthy provider. Three new `LLM.*` DCB events make repair rates measurable per `(role, action) × documentType` cell through the existing Story 4-7 query path.

## Pre-Reading

- `docs/stories/epic-39/story-39-9/39-9-deterministic-repair-ring-validator-feedback-repair-in-managed.md` — the story (source of truth for ACs)
- `docs/stories/epic-39/README.md` — lifecycle diagram ("invalid: domain-phrased errors → bounded repair turn (innermost ring)"), platform invariants
- `apps/tamma-elsa/src/Tamma.Api/Services/Agents/ManagedAgent.cs` — the composition layer the ring's result mapping and event emission live in (note `FailTerminalAsync`, `EmitAsync`, the optional-trailing-ctor-param house pattern)
- `apps/tamma-elsa/src/Tamma.Api/Services/Agents/InlineToolLoopRunner.cs` + `IInlineToolLoopRunner.cs` — the loop; the `tool_result` batching precedent is `BuildAnthropicMultiTurnBody` (~lines 709–741); `CallAnthropicMultiTurn`/`CallOpenAiMultiTurn` are the re-invocation seam the repair turn reuses
- `apps/tamma-elsa/src/Tamma.Api/Services/Agents/ManagedAgentRequest.cs`, `AgentRunResult.cs`, `LlmCallRequest.cs`, `LlmCallResponse.cs`, `ILlmCallResponseMapper.cs` (holds `AgentRunFailureCodes`), `LlmCallResponseMapper.cs` — request/result/wire contracts this story extends additively
- `apps/tamma-elsa/src/Tamma.Api/Services/Agents/AgentRunEventTypes.cs` — the event-constants file pattern the new `LLM.*` constants copy
- `apps/tamma-elsa/src/Tamma.Api/Endpoints/LlmCallEndpoints.cs` — the endpoint that builds `ManagedAgentRequest` (where the validator delegate is composed)
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmInlineActivity.cs` — the THIN CLIENT shim (post-Epic-32); `MapResponseToVariables`/`BuildTransportFailure` are where `FailureCode` gets populated
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Models/LlmCallModels.cs` — `ProviderAttemptDiagnostic` (gains `FailureCode`), `NormalizedLlmResponse`, `ToolLoopConfig`
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Models/TammaApiModels.cs` — `LlmCallApiRequest`/`LlmCallApiResponse`, the engine-side wire mirrors (camelCase `[JsonPropertyName]` discipline)
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/RecordDiagnosticsInlineActivity.cs` — `RecordFailure` call at line ~87: the breaker-exclusion site
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/RecordDiagnosticsActivity.cs` — the second recorder: local breaker dict + `TammaApiClient.RecordProviderFailureAsync` (lines ~189–199), same exclusion needed
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CheckCircuitBreakerActivity.cs` — static `RecordSuccess`/`RecordFailure` over the workflow-variable breaker dict
- `apps/tamma-elsa/src/Tamma.Api/Services/Providers/CircuitBreakerService.cs`, `CircuitBreakerState.cs`, `CircuitBreakerOptions.cs`, `ICircuitBreakerService.cs` — the API-side breaker (unchanged; its only writers are `ProviderEndpoints.RecordFailure/RecordSuccess`, driven by the engine recorders)
- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Tools/ToolOutputHelper.cs` — `RedactSecrets`, the redaction seam AC8 reuses
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/LlmCallWorkflow.cs` — input plumbing (`agentRole`/`action`/`variables`/`tenantId`) the new `documentType`/`issueId` inputs join
- `apps/tamma-elsa/src/Tamma.Data/Repositories/IEventRepository.cs` — `QueryEventsAsync` (Story 4-7), the AC7 measurability path
- `apps/tamma-elsa/src/Tamma.Api/Program.cs` — DI registrations (`IInlineToolLoopRunner`/`IManagedAgent` ~line 755) and the `AddOptions<T>().Configure(... GetSection ... Bind)` pattern (`TenantBackupOptions`, ~line 974) `RepairRingOptions` copies
- `apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/InlineToolLoopRunnerTests.cs` (scripted `SequencedCapturingHandler` fake-HTTP style), `ManagedAgentTests.cs` (strict Moq collaborators, `RecordingEventRepository`), `AgentTrailRepositoryTests.cs` (Testcontainers Postgres over the real `EventRepository`), `LlmCallContractTests.cs` (wire-shape guard)
- `docs/stories/epic-39/story-39-2/implementation-plan.md`, `story-39-6/implementation-plan.md` — the planned `DocumentValidationResult`/`DocumentViolation` and lifecycle-side repair accounting this story interlocks with
- **NOT FOUND:** `apps/tamma-elsa/src/Tamma.Core/Documents/` — the 39-2/39-3 validator contracts (`IDocumentType.Validate`, `DocumentValidationResult`, `DocumentViolation`, `DocumentTypeRegistry`) exist only as plans today. Hard prerequisite; see Dependencies & Sequencing.

## Design Decisions

- **D1 — The ring hooks into `InlineToolLoopRunner.RunAsync` post-completion, driven by a new explicit parameter.** Only the runner holds the `messages` conversation list, so validate-then-repair must run there (story technical note; the `tool_result` feedback move generalized). Add a `RepairRingPlan? repair` parameter to `IInlineToolLoopRunner.RunAsync` with **no default value**: C# expression trees reject omitted optional arguments (CS0854), so a defaulted parameter would break the strict Moq setups in `ManagedAgentTests` anyway — an explicit parameter makes every call site (ManagedAgent + ~12 mock setups + `InlineToolLoopRunnerTests` calls) a conscious mechanical edit rather than a silent hole. Rejected alternative: a second interface method with a default implementation — it forks the loop's entry contract, exactly what 32-5 unified.
- **D2 — The validator crosses the wire as a document-type KEY; the delegate is composed API-side.** A delegate cannot ride HTTP from the engine. The wire request gains optional `documentType` (+ `issueId` for event tags); `LlmCallEndpoints` composes `ManagedAgentRequest.DocumentValidation` — a `DocumentContentValidation` record holding the key plus a `Func<string, DocumentValidationResult>` built over `DocumentTypeRegistry.Resolve(key).Validate(...)` with a local fence-strip + `JsonDocument.Parse` front (precedent: `ApplyReviewFixesActivity.ExtractJson`, line ~267). An unparseable payload yields a synthetic `DocumentViolation("PAYLOAD_NOT_JSON", …)`; an unknown/unregistered key fails loud at the endpoint (the `TammaError` from the registry maps to the `AGENT_UNRESOLVED` 422-in-200 envelope — fail-closed, never "skip validation"). `ManagedAgent` and the runner see only the delegate — registry-free and unit-testable with fakes, which is also how tests proceed before 39-3 lands.
- **D3 — A repair turn is one model re-invocation; no tool execution.** The repair turn calls `CallAnthropicMultiTurn`/`CallOpenAiMultiTurn` once with the same `tools` declarations (Anthropic requires them when history contains tool blocks) but does NOT execute any tool the model requests — a `ToolUse` stop with no usable text simply re-validates as invalid and consumes the turn. Keeps the ring deterministic, cheap, and bounded; the full agentic loop already ran during produce.
- **D4 — The runner returns per-turn history; `ManagedAgent` emits the events.** The runner stays free of `IEventRepository` (it has no event-store collaborator today; `ToolLoopEventEmitter` is SSE streaming, not DCB). It returns `RepairTurns` + `RepairHistory` (validation verdict per turn) on `InlineToolLoopResult`; `ManagedAgent` — which already owns `IEventRepository` and the best-effort `EmitAsync` posture — replays the history into `LLM.VALIDATION.FAILED` / `LLM.REPAIR.SUCCEEDED` / `LLM.REPAIR.EXHAUSTED` after the run. Emission is best-effort and order-stable, matching `AGENT.RUN.*`.
- **D5 — Typed exhaustion = `CONTENT_VALIDATION_FAILED`, body httpStatusCode 422, no usage row.** New constant on the existing `AgentRunFailureCodes`; `LlmCallResponseMapper.ToHttpResult` stamps body status 422 (the `AgentUnresolved` precedent) — NOT in `RetryCheck`'s transient set `{0, 429, 502, 503, 504}`, so the provider chain does not retry a content failure. Consistent with the 32-9 decision, the failure path emits no `IUsageEmitter` row (the terminal `AGENT.RUN.FAILED` + `LLM.*` events are the durable signal) — but token counts (including repair-turn spend) ride the result via `FailTerminalAsync`'s existing `inTok`/`outTok` parameters, keeping budget accounting truthful.
- **D6 — Diagnostic `FailureCode` is a small closed vocabulary; breaker exclusion keys ONLY on `"content_validation"`.** New `DiagnosticFailureCodes` constants (`content_validation` | `transport` | `rate_limit` | `budget`) beside `ProviderAttemptDiagnostic`. `CallLlmInlineActivity` populates it on the paths it owns: `BuildTransportFailure` → `transport`; `MapResponseToVariables` maps the wire `FailureCode`/status (`CONTENT_VALIDATION_FAILED` → `content_validation`, `BUDGET_EXCEEDED` → `budget`, 429 → `rate_limit`, 0/5xx → `transport`; anything else stays `null` — classify only what is certain). The exclusion branch lives at the two decision sites (`RecordDiagnosticsInlineActivity`, `RecordDiagnosticsActivity`) via a shared pure helper, so a content failure records NEITHER failure NOR success, locally and via `RecordProviderFailureAsync` → `CircuitBreakerService`. `CircuitBreakerService`/`ProviderEndpoints` themselves stay untouched — nothing ever reports a content failure into them.
- **D7 — Two repair rings coexist; this story is the INNER one. (Story/plan tension, resolved.)** The 39-6 plan drew a graph-level `DispatchRepair` llm-call (fresh conversation, `BuildRepairVariables` feedback) governed by `MaxValidationRepairAttempts`. Story 39-9 explicitly forbids bolting the in-conversation ring onto the graph — the story file wins for THIS mechanism: the managed-layer ring runs inside a single produce dispatch and is invisible to the graph except through the result. The lifecycle's own fresh-dispatch repair (39-6's graph-level `DispatchRepair`, bounded by `MaxValidationRepairAttempts`) remains the outer ring; the README's "innermost ring" wording refers to THIS story's inner same-conversation ring (parked default-OFF until a provider is configured), not 39-6's outer one — so "innermost" and "outer" are two distinct rings, not a contradiction. The violations + history surfaced on the wire (`contentValidation` block) are exactly what 39-6's `BuildRepairVariables`/lineage consume. No cap interaction: inner turns are `RepairRingOptions`-bounded, outer attempts are `AcceptanceRules`-bounded.
- **D8 — `RepairRingOptions` is global config; the hard cap is enforced by a clamp, not by validation.** `MaxRepairTurns` default 1; `EffectiveMaxRepairTurns => Math.Clamp(MaxRepairTurns, 0, 2)` makes it structurally impossible for any config value, workflow, or prompt to exceed 2 (AC2's "no call site can raise it" — there is no per-call knob at all). `EnabledDocumentTypes` defaults empty (AC9); flipping a type on requires a `.dev/findings/` entry with real-provider failure-rate evidence (process note in the option's XML doc).
- **D9 — Repair message composition is a pure static function, redacted, golden-pinned.** `RepairMessageComposer.Compose(violations)` = fixed instruction preamble + one line per violation (`- [{Code}] {Message}`, verbatim) + fixed re-emit instruction. Output passes through `ToolOutputHelper.RedactSecrets` before appending (violation messages may quote model output). It never sees provider error bodies — those live on `NormalizedLlmResponse.ErrorMessage`, a different axis (D6). A snapshot test pins the exact template so prompt drift is a conscious edit.
- **D10 — `issueId`/`documentType` thread through as additive optional inputs end-to-end.** `LlmCallWorkflow` gains `documentType`/`issueId` inputs (default empty — zero behavior change for the 30+ existing dispatchers), forwarded via two new `Input<>` props on `CallLlmInlineActivity` into the wire request. This is the minimum plumbing that makes the ring reachable by 39-6's produce dispatch and gives AC6 its tags; consuming it is 39-6/39-12 scope.

## Implementation Steps

1. **CREATE `apps/tamma-elsa/src/Tamma.Api/Services/Agents/RepairRingOptions.cs`** (AC2, AC9):

   ```csharp
   public sealed class RepairRingOptions
   {
       public const string SectionName = "RepairRing";
       public const int HardCap = 2;
       public int MaxRepairTurns { get; set; } = 1;
       public string[] EnabledDocumentTypes { get; set; } = Array.Empty<string>(); // dark by default
       public int EffectiveMaxRepairTurns => Math.Clamp(MaxRepairTurns, 0, HardCap);
       public bool IsEnabledFor(string documentTypeKey) =>
           EnabledDocumentTypes.Contains(documentTypeKey, StringComparer.OrdinalIgnoreCase);
   }
   ```

   **MODIFY `apps/tamma-elsa/src/Tamma.Api/Program.cs`**: register via `AddOptions<RepairRingOptions>().Configure(o => builder.Configuration.GetSection(RepairRingOptions.SectionName).Bind(o))` — copy the `TenantBackupOptions` block (~line 974).

2. **MODIFY `apps/tamma-elsa/src/Tamma.Api/Services/Agents/IInlineToolLoopRunner.cs`** — the ring contract (D1):

   ```csharp
   public sealed record RepairRingPlan(
       string DocumentTypeKey,
       Func<string, Tamma.Core.Documents.DocumentValidationResult> Validate,
       bool RepairEnabled,          // gate: EnabledDocumentTypes membership
       int MaxRepairTurns);         // already clamped (EffectiveMaxRepairTurns)

   public sealed record RepairTurnRecord(              // turn 0 = initial produce validation
       int Turn, bool Valid,
       IReadOnlyList<Tamma.Core.Documents.DocumentViolation> Violations);
   ```

   Extend `InlineToolLoopResult` (additive init-only): `bool? ContentValid` (null = no validator supplied), `int RepairTurns`, `IReadOnlyList<RepairTurnRecord> RepairHistory = Array.Empty<...>()`. Change `RunAsync` signature: insert `RepairRingPlan? repair` before `ct` (no default value — D1).

3. **CREATE `apps/tamma-elsa/src/Tamma.Api/Services/Agents/RepairMessageComposer.cs`** (AC8, D9) — `public static class RepairMessageComposer { public static string Compose(IReadOnlyList<DocumentViolation> violations); }`. Pure; deterministic ordering (input order preserved); fixed instruction text; caller redacts via `ToolOutputHelper.RedactSecrets`.

4. **MODIFY `apps/tamma-elsa/src/Tamma.Api/Services/Agents/InlineToolLoopRunner.cs`** (AC1, AC2) — after the tool loop exits with `response.Success && !string.IsNullOrEmpty(ResponseText)` and `repair != null`:
   validate → record `RepairTurnRecord(0, …)` → while invalid ∧ `repair.RepairEnabled` ∧ `repairTurns < repair.MaxRepairTurns`: append `new ConversationMessage { Role = "user", Content = RedactSecrets(Compose(violations)) }` to the SAME `messages` list, re-invoke `CallAnthropicMultiTurn`/`CallOpenAiMultiTurn` (same client/config/tools — D3), accumulate `totalPromptTokens`/`totalCompletionTokens`, re-validate, record the turn. A transport failure (`!response.Success`) during a repair turn breaks out and surfaces exactly as today's provider failure (orthogonality — story technical note). Repair turns never touch `loopConfig.MaxSteps`/`completedTurns`. Cumulative token totals (incl. repair spend) still land on `lastResponse.PromptTokens/CompletionTokens` so `InputTokens`/`OutputTokens` stay truthful. Populate the three new result fields.

5. **MODIFY `apps/tamma-elsa/src/Tamma.Api/Services/Agents/ManagedAgentRequest.cs`** — add `public DocumentContentValidation? DocumentValidation { get; init; }` and `public string? IssueId { get; init; }`; new record `DocumentContentValidation(string DocumentTypeKey, Func<string, DocumentValidationResult> Validate)` in the same file. `From(...)` gains the two pass-throughs. **MODIFY `LlmCallRequest.cs`**: add optional `string? DocumentType` / `string? IssueId`. **CREATE `apps/tamma-elsa/src/Tamma.Api/Services/Agents/DocumentValidationBinder.cs`** — `public static DocumentContentValidation? Bind(string? documentTypeKey)`: null/empty → null; else `DocumentTypeRegistry.Resolve` (fail-loud, D2) + fence-strip/parse wrapper producing `PAYLOAD_NOT_JSON` on unparseable output. **MODIFY `apps/tamma-elsa/src/Tamma.Api/Endpoints/LlmCallEndpoints.cs`**: call the binder, catch `TammaError` → `AGENT_UNRESOLVED`-style 422 envelope.

6. **CREATE `apps/tamma-elsa/src/Tamma.Api/Services/Agents/RepairRingEventTypes.cs`** (AC6) — copy the `AgentRunEventTypes` file shape:

   ```csharp
   public static class RepairRingEventTypes
   {
       public const string ValidationFailed = "LLM.VALIDATION.FAILED";
       public const string RepairSucceeded  = "LLM.REPAIR.SUCCEEDED";
       public const string RepairExhausted  = "LLM.REPAIR.EXHAUSTED";
   }
   ```

7. **MODIFY `apps/tamma-elsa/src/Tamma.Api/Services/Agents/ManagedAgent.cs`** (AC1, AC3, AC6, AC9) — ctor gains optional trailing `Microsoft.Extensions.Options.IOptions<RepairRingOptions>? repairOptions = null` (house pattern; null ⇒ `new RepairRingOptions()`). Before step 6 build the plan: `request.DocumentValidation is null ? null : new RepairRingPlan(key, validate, _repairOptions.IsEnabledFor(key), _repairOptions.EffectiveMaxRepairTurns)`; pass to `_runner.RunAsync(..., repair, ct)`. After a transport-successful loop: replay `loop.RepairHistory` into events (one `ValidationFailed` per invalid turn, tags `{ issueId, documentType, role, action, repairTurn, correlationId, tenantId }`, data = violation code/message summaries; `RepairSucceeded` when a turn > 0 validates; `RepairExhausted` when still invalid with `RepairEnabled` and turns == cap). If `loop.ContentValid == false` → `FailTerminalAsync(..., AgentRunFailureCodes.ContentValidationFailed, keyFreeSummary, httpStatus: 422, inTok, outTok, ...)` extended to carry the new result fields. **MODIFY `AgentRunResult.cs`**: additive `bool? ContentValid`, `int RepairTurns`, `IReadOnlyList<RepairTurnRecord>? RepairHistory`, `IReadOnlyList<DocumentViolation>? ContentViolations`. **MODIFY `ILlmCallResponseMapper.cs`**: add `public const string ContentValidationFailed = "CONTENT_VALIDATION_FAILED";` to `AgentRunFailureCodes`.

8. **MODIFY the wire response + mapper** (AC3): `LlmCallResponse.cs` gains `ContentValidationDto? ContentValidation` (`record ContentValidationDto(bool Valid, int RepairTurns, IReadOnlyList<ContentViolationDto> Violations, IReadOnlyList<RepairTurnDto> History)` in the same file, key-free); `LlmCallResponseMapper.ToResponse` projects it; `ToHttpResult` adds `AgentRunFailureCodes.ContentValidationFailed => Results.Ok(WithBodyStatus(body, body.HttpStatusCode ?? 422))`. Mirror the DTOs (camelCase `[JsonPropertyName]`) on `LlmCallApiResponse` in `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Models/TammaApiModels.cs`, and add `documentType`/`issueId` to `LlmCallApiRequest`.

9. **MODIFY `apps/tamma-elsa/src/Tamma.Activities/LlmCall/Models/LlmCallModels.cs`** (AC4) — `ProviderAttemptDiagnostic` gains `public string? FailureCode { get; set; }` (nullable ⇒ old JSON deserializes clean; default STJ ignores unknown members ⇒ old readers unaffected) + a `DiagnosticFailureCodes` constants class (D6).

10. **MODIFY `apps/tamma-elsa/src/Tamma.Activities/LlmCall/CallLlmInlineActivity.cs`** (AC4, D10) — `BuildTransportFailure` sets `FailureCode = DiagnosticFailureCodes.Transport`; `MapResponseToVariables` sets it per the D6 mapping and writes a new `ContentValidationJson` workflow variable (empty when absent); new `DocumentTypeProp`/`IssueIdProp` inputs forwarded onto `LlmCallApiRequest`. **MODIFY `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/LlmCallWorkflow.cs`**: read `documentType`/`issueId` inputs into variables, wire them to the activity props, surface a `contentValidation` output from the new variable (all defaults empty — additive).

11. **MODIFY the two recorders** (AC5): `RecordDiagnosticsInlineActivity.cs` — replace the `else RecordFailure(...)` branch with a check on a new shared pure helper; `RecordDiagnosticsActivity.cs` — same guard around both the local `CheckCircuitBreakerActivity.RecordFailure` and `apiClient.RecordProviderFailureAsync`. Helper lives beside the model: `public static bool CountsAsProviderFailure(ProviderAttemptDiagnostic d) => !d.Succeeded && d.FailureCode != DiagnosticFailureCodes.ContentValidation;` (a content failure is also NOT a success — it records nothing).

12. **CREATE the test files** (see Test Plan) and **MODIFY** `ManagedAgentTests.cs` / `InlineToolLoopRunnerTests.cs` mock setups + call sites for the new `RunAsync` arity, and `LlmCallContractTests.cs` for the additive wire fields.

## Data & Migrations

None. No EF entities change; events land in the existing `domain_events` stream through the existing `IEventRepository.AppendAsync` path. `dotnet ef migrations has-pending-model-changes` stays clean.

## Events

Emitted (constants in `apps/tamma-elsa/src/Tamma.Api/Services/Agents/RepairRingEventTypes.cs`; none consumed):

- `LLM.VALIDATION.FAILED` — per failed validation (incl. turn 0); tags `issueId`, `documentType`, `role`, `action`, `repairTurn`, `correlationId`, `tenantId`; data: violation summaries (code + domain-phrased message, redacted)
- `LLM.REPAIR.SUCCEEDED` — a repair turn produced a valid document; data: turn number
- `LLM.REPAIR.EXHAUSTED` — cap hit, still invalid; data: turn count + final violation count

Unchanged but adjacent: exactly one terminal `AGENT.RUN.SUCCESS`/`AGENT.RUN.FAILED` still fires per run (content failure is a `FAILED` with `failureCode: CONTENT_VALIDATION_FAILED`).

## Test Plan

All NUnit + FluentAssertions; Moq for `ManagedAgent` collaborators; fake HTTP handlers (no live provider); Testcontainers only for the AC7 rate test.

- **`RepairRingOptionsTests.cs`** (`apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/`) — defaults pin (MaxRepairTurns 1, `EnabledDocumentTypes` empty); `EffectiveMaxRepairTurns` clamps 5→2, −1→0; `IsEnabledFor` case-insensitive. **Covers AC2 (bounds/global), AC9 (default off).**
- **`RepairMessageComposerTests.cs`** (same dir) — determinism (same violations → byte-identical message, twice); snapshot/golden pin of the exact template for a two-violation input; violations appear verbatim; a violation message embedding a secret-shaped token is redacted through `ToolOutputHelper.RedactSecrets` at the runner append site; no provider-error text path exists (composer takes only `DocumentViolation`s — compile-time). **Covers AC8.**
- **`InlineToolLoopRunnerRepairTests.cs`** (same dir; `SequencedCapturingHandler` style from `InlineToolLoopRunnerTests`) — (a) invalid→valid script: second HTTP request body contains the composed repair message as a user message AND the original system prompt + prior turns (conversation preserved); result `ContentValid == true`, `RepairTurns == 1`, history `[invalid, valid]`, token totals include the repair turn; (b) always-invalid: stops at `MaxRepairTurns`, `ContentValid == false`, history length = 1 + cap; (c) `RepairEnabled == false`: exactly ONE HTTP call, `ContentValid == false`, `RepairTurns == 0`; (d) transport 503 during the repair turn → surfaces as provider failure with preserved status (orthogonality); (e) repair turns leave `Turns`/`Exhausted` (MaxSteps accounting) untouched; (f) `repair == null` → result fields default (`ContentValid == null`) and behavior byte-identical to today. **Covers AC1, AC2 (re-validate/exit-on-pass), AC9 (runner half), technical notes.**
- **`ManagedAgentContentValidationTests.cs`** (same dir; `ManagedAgentTests` fixture style, `RecordingEventRepository`) — exhausted-invalid run → `Success == false`, `FailureCode == "CONTENT_VALIDATION_FAILED"`, `HttpStatusCode == 422`, violations + history + token counts on the result, no usage row, exactly one `AGENT.RUN.FAILED`; event trail asserts `LLM.VALIDATION.FAILED` (×2, `repairTurn` 0 and 1, tags carry `issueId`/`documentType`/`role`/`action`) + `LLM.REPAIR.EXHAUSTED`; repaired run → success result with `RepairTurns == 1` + `LLM.REPAIR.SUCCEEDED`; gate-off run → only `LLM.VALIDATION.FAILED` emitted, plan passed to runner has `RepairEnabled == false`; no `DocumentValidation` on the request → no `LLM.*` events, no new fields set. Plus `LlmCallResponseMapper` case: content failure rides 200 envelope, body status 422 (not transient). **Covers AC1 (managed-layer orchestration), AC3, AC6, AC9.**
- **`DiagnosticFailureCodeTests.cs`** (`apps/tamma-elsa/tests/Tamma.Activities.Tests/LlmCall/`) — old-JSON (no `failureCode`) deserializes to `FailureCode == null`; round-trip preserves the value; `MapResponseToVariables` mapping table (CONTENT_VALIDATION_FAILED→content_validation, BUDGET_EXCEEDED→budget, 429→rate_limit, 0/503→transport, AGENT_UNRESOLVED→null); `BuildTransportFailure` → transport. **Covers AC4.**
- **`RecordDiagnosticsBreakerExclusionTests.cs`** (same dir) — via the pure `CountsAsProviderFailure` helper + `CheckCircuitBreakerActivity.RecordFailure` state dict: 5 consecutive `content_validation` diagnostics leave the breaker `Closed` with `ConsecutiveFailures == 0`; the same 5 as `transport` open it; a content failure also does NOT reset counters (not a success); `RecordDiagnosticsActivity` path asserts `RecordProviderFailureAsync` is never invoked for a content diagnostic (capturing fake `TammaApiClient` per `TammaApiClientTests`). **Covers AC5 (the story's explicit N-vs-N proof).**
- **`RepairRingEventRateTests.cs`** (`apps/tamma-elsa/tests/Tamma.Api.Tests/Agents/`; Testcontainers Postgres, `AgentTrailRepositoryTests` fixture style) — seed a mixed `LLM.*` stream across two `(role, action) × documentType` cells through the real `EventRepository`; compute validation-failure rate, first-repair success rate, and exhaustion rate per cell using ONLY `IEventRepository.QueryEventsAsync` (type prefix `"LLM."`) + tag grouping; assert the expected ratios — proving the tags carry all three dimensions with no extra store. **Covers AC7.**
- **MODIFIED guards** — `LlmCallContractTests.cs` (additive wire fields remain key-free and don't break the pinned shape), `ManagedAgentTests.cs`/`InlineToolLoopRunnerTests.cs` (new `RunAsync` arity; behavior pins unchanged).

## Definition of Done

| AC | Satisfied by | Verified by |
|---|---|---|
| 1 — repair turn in the managed layer, same conversation | Steps 2, 3, 4 (runner ring), 5, 7 (plan wiring) | `InlineToolLoopRunnerRepairTests` (a) conversation-preservation assert; `ManagedAgentContentValidationTests` orchestration cases |
| 2 — `maxRepairTurns` default 1, hard cap 2, global; re-validate, exit on pass | Steps 1 (clamp), 4 | `RepairRingOptionsTests` clamp pins; `InlineToolLoopRunnerRepairTests` (a)(b) |
| 3 — typed, non-transient content failure with violations + per-turn history | Steps 7, 8 (D5) | `ManagedAgentContentValidationTests` (422-in-200, payload completeness, mapper case) |
| 4 — additive `FailureCode` on `ProviderAttemptDiagnostic`, populated by the shim | Steps 9, 10 | `DiagnosticFailureCodeTests` (old-JSON tolerance, mapping table) |
| 5 — content failures never trip the breaker (N-vs-N proof) | Step 11 (D6) | `RecordDiagnosticsBreakerExclusionTests` |
| 6 — `LLM.VALIDATION.FAILED` / `LLM.REPAIR.SUCCEEDED` / `LLM.REPAIR.EXHAUSTED` with tags | Steps 6, 7 (D4) | `ManagedAgentContentValidationTests` event-trail asserts |
| 7 — per-cell rates computable from events alone via the 4-7 query path | Steps 6, 7 (tag dimensions) | `RepairRingEventRateTests` |
| 8 — deterministic, safe repair message; golden-pinned | Step 3 (D9), redaction at step 4 | `RepairMessageComposerTests` |
| 9 — gated per document type, default OFF; gate-off = zero extra turns + only VALIDATION.FAILED | Steps 1, 4, 7 (D8) | `RepairRingOptionsTests` defaults; `InlineToolLoopRunnerRepairTests` (c); `ManagedAgentContentValidationTests` gate-off case |

## Dependencies & Sequencing

- **Blocking — 39-2/39-3 (`Tamma.Core/Documents`):** `DocumentValidationResult`/`DocumentViolation` are the ring's violation currency and `DocumentTypeRegistry` backs the endpoint binder. Do not start until 39-2 compiles (39-3 needed only for a real registered type; every test in this story uses fake validator delegates through the D2 seam, so 39-3 can land in parallel). If sequencing pressure demands, steps 1–4 + 9–11 compile against 39-2 alone; only `DocumentValidationBinder` (step 5) touches the registry.
- **Lockstep — 39-6:** consumes the `contentValidation` wire block and maps `CONTENT_VALIDATION_FAILED` into `ValidationExhausted` (its D7 accounting). Contract to agree NOW: the wire field name `contentValidation` and its DTO shape (step 8). Nothing in 39-6 is pulled into this story; the lifecycle is exercised here only as "the caller reads the typed result" via wire-shape tests.
- **Existing, verified:** Epic 32 managed layer (`ManagedAgent`/`InlineToolLoopRunner`/`LlmCallEndpoints`), Story 4-7 `QueryEventsAsync`, the breaker stack, `ToolOutputHelper.RedactSecrets` — all present.
- **Consumers (later):** 39-12..39-15 get repair for free by threading `documentType`/`issueId` on their produce dispatches and flipping the gate per evidence; no code from them is anticipated here.

## Risks & Mitigations

- **`RunAsync` signature change ripples through strict mocks.** Mechanical but noisy (~15 setups). Mitigation: no default value ⇒ the compiler enumerates every site; behavior pins in existing tests stay untouched, so green-after-arity-fix is meaningful.
- **Repair-turn provider quirks (tool_use on a repair turn, empty text).** Mitigation: D3 treats any no-text response as a failed repair turn — bounded by the cap; test (b) pins it. No new provider surface is introduced.
- **Conflation regression: someone later keys breaker exclusion on `Succeeded` or message text.** Mitigation: the exclusion is a named pure helper with the N-vs-N test as a tripwire; `DiagnosticFailureCodes` is the only vocabulary.
- **39-2 contract drift (names/shapes still plan-only).** Mitigation: only `DocumentValidationResult`/`DocumentViolation`/`DocumentTypeRegistry.Resolve` are referenced — all pinned in the 39-2 plan and canon; any rename is mechanical here. The delegate seam keeps the blast radius to one binder file.
- **Token-spend surprise from dark-launch misconfig.** Mitigation: gate default empty AND clamp cap; with the gate off the ring costs zero extra turns by construction (test (c)); enablement requires a findings entry (D8).

## Effort Breakdown

| Step(s) | Work | Days |
|---|---|---|
| 1 | `RepairRingOptions` + registration | 0.25 |
| 2–3 | Ring contract types + `RepairMessageComposer` | 0.5 |
| 4 | Runner ring implementation + token accounting | 1.0 |
| 5 | Request seam: binder, endpoint, wire request fields | 0.75 |
| 6–8 | Events, `ManagedAgent` orchestration, result/mapper/wire response | 1.25 |
| 9–11 | Diagnostic `FailureCode`, shim population + plumbing, breaker exclusion | 0.75 |
| 12 | New test classes + mock-arity/contract-test updates | 1.5 |
| — | 39-6 wire-contract coordination, review polish | 0.5 |
| **Total** | | **6.5** (story estimate: 5–7 days) |
