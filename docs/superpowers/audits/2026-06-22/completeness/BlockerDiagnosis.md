# Completeness Audit — BlockerDiagnosisWorkflow

**Date:** 2026-06-22
**Workflow:** `blocker-diagnosis` (`BlockerDiagnosisWorkflow`)
**File:** `/home/meywd/tamma/apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/BlockerDiagnosisWorkflow.cs`
**Maturity:** **partial**

---

## Purpose & Owner

Reusable Elsa sub-workflow for the mentorship engine. Diagnoses *why* a junior developer is stuck and applies a progressive resolution ladder (Hint → Guidance → Assistance → Escalation), waiting for progress between levels and escalating to a senior on exhaustion.

- **Owning epic:** Epic 7 (Junior Developer Mentorship).
- **Owning stories:** **7-1G** "Blocker Diagnosis Sub-Workflow" (base spec) and **7-11** "Blocker Diagnosis Context Enrichment and Resolution Intelligence" (enrichment spec).
- `sprint-status.yaml:161` marks `7-7-blocker-diagnosis-sub-workflow: done`. That claim covers the structural skeleton only; AC9 (observability) of 7-1G and *all* of 7-11 are unimplemented (see below).

---

## Current Capabilities (what it actually does today)

The workflow is a real, non-trivial Flowchart — far past a stub. It implements:

1. **CaptureInputs** — reads `sessionId`, `storyId`, `juniorId`, `skillLevel` (floored to ≥1), `blockerContext`, `repository`, `branchName` (defaults `feature/{storyId}`); stamps `startTime`.
2. **ParallelSignals** — true `Parallel` fan-out of 4 collectors:
   - `CollectGitActivityActivity` — real `IIntegrationService.GetGitHubCommitsAsync` + `GetGitHubFileChangesAsync` (commit count, files changed, additions/deletions, time-since-last-commit).
   - `CollectCIStatusActivity` — real `GetBuildStatusAsync` + `TriggerTestsAsync` (build status/error, pass/fail counts, failing test names, coverage).
   - `CollectInactivityActivity` — real commits-as-activity proxy with inactivity threshold.
   - `CollectCommunicationActivity` — **partially stubbed**: when a Slack ID is present it returns `HasRecentCommunication=true` but `RecentMessageCount=0`/`QuestionsAsked=0` with a code comment "In a real implementation, we would query Slack history."
   - Each collector swallows API failures and returns `CollectionSucceeded=false` (graceful partial-signal behaviour — matches 7-1G AC3).
3. **AggregateSignals** — composes `AggregatedSignals` with a successful-collector count.
4. **AIDiagnosis** — `DispatchWorkflow → "llm-call"` (role=`SeniorDeveloper`, action=`ResolveBlocker`), `WaitForCompletion=true`. **Correctly routes through the LLM mediation seam** — no direct provider call. Prompt built by `BuildDiagnosisPrompt`, with `SecurityHelpers.SanitizeForPrompt` on all signal-derived free text.
5. **ClassifyBlocker** (`ClassifyBlockerActivity`) — parses the LLM JSON (`blocker_type`/`confidence`/`root_cause`/`recommended_approach`/`evidence`), maps to the 8 `BlockerCategory` values, derives severity from inactivity + skill + type; on missing/invalid LLM output falls back to a rule-based `ClassifyFromSignals`.
6. **DetermineStartLevel** — skill 1–2 starts at Guidance, 3+ starts at Hint (7-1G AC8).
7. **Progressive resolution** — four `If`-gated `Sequence` levels:
   - **Hint** (Socratic, role=SeniorDeveloper/action=MentorFeedback), 15 min wait (30 min for skill ≥4), skipped for 1–2.
   - **Guidance** (role=SeniorDeveloper/MentorFeedback), 30 min.
   - **Assistance** (role=Developer/ImplementFix, code example), 45 min.
   - **Escalation** (`EscalateToSeniorActivity` — compiles context dump, notifies senior via `IIntegrationService.SendSlackMessageAsync`, bookmark-waits for senior response).
   - Each LLM call routes through `"llm-call"`; each prompt is sanitized.
8. **DetectProgressActivity** — bookmark-based suspend/resume (`blocker-progress-{sessionId}-{level}`, AutoBurn), reading `ProgressDetected` on resume. On no-progress each level advances `currentLevel` to the next.
9. **SetOutput** — emits a `BlockerResolution` (status Resolved/Escalated, type, severity, attempts, level, resolution time, diagnosis details, feedback list).

**Architecture-pivot compliance:** GOOD. Every LLM interaction is a `DispatchWorkflow("llm-call")` — the step never touches an external provider directly (honours the rule-#1 mediation principle from `docs/superpowers/specs/2026-06-20-epic-32-revised-agent-architecture.md`). Prompt-injection sanitization is applied consistently.

---

## Intended Full Scope (with citations)

**7-1G** (`docs/stories/epic-7/story-7-1G/7-1G-blocker-diagnosis-sub-workflow.md`):
- AC2: output status enum includes **`Timeout`** (3rd terminal state) — not just Resolved/Escalated.
- AC3: `Join` waits for all signals with a **15-second timeout**.
- AC6: progress detected → terminal **`Resolved`** and **stop the ladder** (don't keep running later level `If`s).
- AC9 **Observability**: each attempt logged (level, content, wait time, outcome); blocker-type distribution tracked; resolution rate per level; metrics `blocker.total`, `blocker.resolved_rate`, `blocker.avg_resolution_time`, `blocker.escalation_rate`.
- Config block (`BlockerDiagnosis:*`) for signal timeout, per-level/per-skill wait times, `SkipHintForLevels`, escalation channel, progress-detection thresholds — wait times are currently **hard-coded constants** in the workflow.

**7-11** (`docs/stories/epic-7/story-7-11/7-11-blocker-diagnosis-improvements.md`) — entirely unimplemented:
- AC1: diagnosis prompt must include **story title/description/expected files + project tech-stack summary + resolution history**.
- AC2–4: hint/guidance/assistance prompts must include **relevant code snippets, project patterns/conventions, the actual CI error, and the prior level's actual content**.
- AC5: **resolution history accumulation** as structured `ResolutionAttempt` entries (actual LLM content, not metadata strings), fed forward (cap 3 most recent).
- AC6: enhanced rule-based fallback parsing specific CI error patterns (TS2322/TS2345/TS2304/ENOENT/EACCES…).
- AC7: richer signal capture — `CollectCIStatusActivity` build-error text (2000 chars) + failing-test output (5000 chars); `CollectGitActivityActivity` last-commit diff/message/files.
- New pre-steps: **Fetch Story Context** + **Detect Project Conventions** running parallel to signal collection.

**Project rules** (CLAUDE.md): DCB audit events for every operation (`AGGREGATE.ACTION.STATUS`); tenant→system→error scoping with no empty/plain fallback; no silent-failure/false-success; multi-tenant scoping for any tenant-aware feature (perf/action data ALWAYS tenant-scoped per Epic 32).

**Domain best-practice** for a progressive-escalation human-in-the-loop flow: bounded bookmark waits (a never-resumed bookmark must time out → `Timeout`, not hang forever); idempotent re-entry; an audit/action trail of every intervention for the benchmarking/learning loop (Epic 32); a feedback hook so resolution outcomes inform future diagnosis (Epic 6 RAG / Epic 32 learning).

---

## Missing Capabilities

| # | Capability | Priority | dependsOn |
|---|-----------|----------|-----------|
| 1 | **No bookmark timeout.** `DetectProgressActivity` and `EscalateToSeniorActivity` create AutoBurn bookmarks with **no scheduled timer** — if no external resume ever fires, the workflow suspends forever. The per-level `WaitTimeMinutes` is passed in but never enforced. Violates 7-1G AC6 wait semantics and the no-hang rule. | P0 | none |
| 2 | **`Timeout` status never produced.** `BlockerResolutionStatus.Timeout` exists but the workflow only ever returns Resolved/Escalated. A wait that expires with no progress should be auditable as a distinct outcome. | P0 | #1 |
| 3 | **No DCB audit events.** Neither the workflow nor any Blocker activity emits any event. 7-1G AC9 + CLAUDE.md require `BLOCKER.DIAGNOSED.*`, `BLOCKER.RESOLUTION_ATTEMPTED.*`, `BLOCKER.PROGRESS_DETECTED`, `BLOCKER.ESCALATED`, `BLOCKER.RESOLVED`/`BLOCKER.TIMED_OUT` with `{sessionId, juniorId, storyId, tenantId, level, blockerType}` tags. | P0 | none |
| 4 | **No tenant scoping.** No `tenantId` captured or threaded into LLM calls, escalation, or any (future) persisted record. Epic 32 requires perf/action data ALWAYS tenant-scoped; the `llm-call` mediation needs `tenantId` for provider/key resolution. | P0 | 32-5 mediation |
| 5 | **`CollectCommunicationActivity` is a stub** — never queries real Slack history (`RecentMessageCount`/`QuestionsAsked` hard-zero). Communication signal feeds classification (PersonalBlocker rule) so this degrades diagnosis quality. | P1 | none |
| 6 | **No resolution-history accumulation (7-11 AC5).** `feedbackProvided` stores metadata strings (`"[Hint] Socratic hints provided for X"`), not the actual LLM content. Each level's LLM call starts blind to prior interventions → repeated/contradictory advice. | P1 | none |
| 7 | **Context-poor prompts (7-11 AC1–4).** Diagnosis/hint/guidance/assistance prompts get raw signals only — no story title/description/expected files, no project conventions, no relevant code, no CI error text. Generic, low-value LLM output. | P1 | 7-1F context-gathering; 2-17 conventions |
| 8 | **No Fetch-Story-Context / Detect-Project-Conventions pre-steps (7-11).** Required to source the data for #7. | P1 | 7-1F; 2-17 |
| 9 | **Signal collection lacks rich payloads (7-11 AC7).** No build-error text / failing-test output / last-commit diff captured — only counts and status. | P1 | none |
| 10 | **`ClassifyBlocker` fallback is naive (7-11 AC6).** No CI error-code pattern matching (TS2322/TS2304/ENOENT/EACCES); coarse keyword rules only. | P2 | none |
| 11 | **Wait times / skip-levels / channels hard-coded.** 7-1G config block (`BlockerDiagnosis:WaitTimeMinutes`, `SkipHintForLevels`, `SignalCollectionTimeoutSeconds`, progress thresholds) not bound — constants live in code. | P2 | none |
| 12 | **No signal-collection timeout (7-1G AC3).** The `Parallel` join has no 15s cap; a hung collector blocks the whole flow (collectors are async with no per-call timeout). | P2 | none |
| 13 | **No observability metrics (7-1G AC9).** `blocker.total`, `blocker.resolved_rate`, `blocker.avg_resolution_time`, `blocker.escalation_rate`, per-level resolution-rate, blocker-type distribution — none emitted (no OTel meter). | P2 | none |
| 14 | **`ProgressDetectionResult.Result` output discarded.** `DetectProgressActivity` exposes a structured `Result` (type/details) but the workflow only reads the boolean `ProgressDetected`; progress *type/detail* is lost (needed for history #6 and audit #3). | P2 | none |
| 15 | **No escalation-resolved feedthrough.** `EscalateToSeniorActivity.Resolved`/`SeniorResponse` outputs are not wired back into `isResolved`/output — an escalation the senior *resolves* still reports `Escalated`, never `Resolved`. | P2 | none |
| 16 | **No learning/RAG write-back.** Resolved blockers (type, what worked, resolution level) are not persisted to the mentorship/learning store to improve future diagnosis. | P3 | Epic 6 RAG; Epic 32 learning |

---

## Ordered Build-out Spec

Sequenced so safety/correctness (P0) lands first, then scope (P1), then polish (P2/P3). All steps read-only on existing contracts unless noted.

### Phase A — Correctness & safety (P0)

1. **Enforce bookmark timeouts (#1, #2).** In `DetectProgressActivity.Execute`, alongside the progress bookmark, schedule an Elsa timer/delay of `WaitTimeMinutes` (sourced from config, Phase D). Whichever fires first wins; if the timer fires, set `ProgressDetected=false` and a new `TimedOut=true` output, then complete. Same pattern for `EscalateToSeniorActivity` with a configurable senior-response SLA (e.g. `BlockerDiagnosis:EscalationTimeoutMinutes`). Never leave an un-timed bookmark.
2. **Add a `Timeout` terminal path (#2).** Add a workflow variable `timedOut`. When the *Escalation* level's wait expires with no senior response, set the final status to `BlockerResolutionStatus.Timeout` (not `Escalated`). In `SetOutput`, choose `Resolved` (isResolved) → else `Escalated` (senior notified, awaiting) → else `Timeout` (escalation SLA blown).
3. **Short-circuit the ladder on resolution (verify #6/AC6).** Each level `If` already guards on `!isResolved`; confirm a Hint-level resolution actually skips Guidance/Assistance/Escalation `If`s (they do, via the shared `isResolved` guard) — add a fast `FlowDecision`/early `SetOutput` edge from each "Check Progress" so a resolved blocker emits output immediately rather than walking three no-op `If`s. Add explicit `BLOCKER.RESOLVED` event at that edge.
4. **Thread `tenantId` (#4).** Capture `tenantId` in `CaptureInputs` (`context.GetInput<string>("tenantId")`); include it in every `"llm-call"` Input dictionary (mediation needs it for provider/key resolution — 32-5), in escalation context, and in all DCB event tags. If absent → resolve via the active-tenant claim; if still absent → fail closed (no empty/plain fallback per project rule).
5. **Emit DCB audit events (#3).** Add an `EmitBlockerEventActivity` (or reuse the existing event-store seam used elsewhere) and emit, with tags `{tenantId, sessionId, juniorId, storyId, level, blockerType, severity}`:
   - after ClassifyBlocker → `BLOCKER.DIAGNOSED.SUCCESS` (type, severity, confidence) / `.FAILED` on classify error.
   - at each level entry → `BLOCKER.RESOLUTION_ATTEMPTED` (level, attempt#).
   - on progress detect resume → `BLOCKER.PROGRESS_DETECTED` (type, details) or `BLOCKER.PROGRESS_TIMED_OUT`.
   - on escalation → `BLOCKER.ESCALATED` (channel, severity).
   - terminal → `BLOCKER.RESOLVED` / `BLOCKER.TIMED_OUT`.

### Phase B — Resolution intelligence (7-11, P1)

6. **Add `ResolutionAttempt` model + accumulation (#6).** Add to `BlockerModels.cs`: `ResolutionAttempt { Level, AttemptedAt, LlmResponse, ProgressDetected, ProgressDetails }`. Add workflow var `resolutionHistory : List<ResolutionAttempt>`. In each level, after the `"llm-call"` DispatchWorkflow, read `llmResponse` from its `Result`, append a `ResolutionAttempt` (with the structured progress detail from #14). Cap to the 3 most recent before passing forward.
7. **Wire `ProgressDetectionResult.Result` through (#14).** Add a second output binding on each `DetectProgressActivity` to a `progressResult` var; feed `ProgressType`/`Details` into the `ResolutionAttempt` and the `BLOCKER.PROGRESS_DETECTED` event.
8. **Add context pre-steps (#8).** Before/parallel to `ParallelSignals`: a `DispatchWorkflow` to the context-gathering workflow (7-1F) → `storyContext` (title, description, ACs, expected/relevant files); a `DetectProjectConventionsActivity` (2-17) → `projectConventions` (CompactSummary). On failure, follow tenant→system→error — log + proceed with whatever context resolved, never silently substitute empty if a tenant convention was expected.
9. **Enrich prompts (#7).** Extend `BuildDiagnosisPrompt` and the per-level prompt builders to inject `storyContext`, `projectConventions`, the CI error text (Phase C), accumulated `resolutionHistory`, and (guidance/assistance) the prior level's actual content. Assistance prompt must name the exact language/framework/conventions. Keep `SecurityHelpers.SanitizeForPrompt` on every interpolated free-text field.

### Phase C — Richer signals (7-11 AC6/AC7, P1/P2)

10. **Extend `CIStatusSignal` + collector (#9).** Add `BuildErrorText` (≤2000 chars) and `FailingTestOutput` (≤5000 chars); fetch via GitHub Actions jobs/steps output through `IIntegrationService` (add a method if missing). On rate-limit/slow → timeout + fall back to metadata-only (`CollectionSucceeded` stays true, text empty).
11. **Extend `GitActivitySignal` + collector.** Add `LastCommitDiff`/`LastCommitMessage`/`LastCommitFiles`; fetch via commit detail API.
12. **Implement real Slack history in `CollectCommunicationActivity` (#5).** Query message history / questions when Slack ID present (add `IIntegrationService.GetSlackHistoryAsync`); keep best-effort fail-soft.
13. **Enhanced classify fallback (#10).** Add CI error-code pattern matching (TS2322/TS2345/TS2304/Cannot find name → TechnicalKnowledgeGap; ENOENT/MODULE_NOT_FOUND/EACCES → EnvironmentIssue) with confidence adjustment, before the coarse keyword rules.

### Phase D — Config, observability, learning (P2/P3)

14. **Bind the `BlockerDiagnosis:*` config block (#11).** Per-level/per-skill wait times, `SkipHintForLevels`, `SignalCollectionTimeoutSeconds`, escalation channel, progress thresholds, escalation SLA — read via `IConfiguration` in the activities (replace hard-coded constants).
15. **Add the 15s signal-collection timeout (#12).** Wrap the `Parallel` join (or each collector) with a configurable cancellation deadline; late collectors land as `CollectionSucceeded=false`.
16. **Emit OTel metrics (#13).** A `BlockerMetrics` meter: `blocker.total` (counter, tag blockerType), `blocker.resolved` / `blocker.escalated` / `blocker.timed_out`, `blocker.resolution_time` (histogram), per-level resolution-rate counters. Tag with `tenantId`.
17. **Escalation-resolved feedthrough (#15).** Wire `EscalateToSeniorActivity.Resolved` back into `isResolved` and `SeniorResponse` into the output/history; a senior-resolved escalation reports `Resolved` at level `Escalation`.
18. **Learning write-back (#16).** On terminal resolution, persist `{tenantId, blockerType, resolutionLevel, whatWorked}` to the mentorship/learning store for future diagnosis (Epic 6 RAG / Epic 32 learning) — emit `BLOCKER.LEARNING_RECORDED`.

---

## Overall Assessment

**Maturity: partial.** This is **not** a thin happy-path skeleton like the PullRequest example — it has real parallel signal collection, correct LLM-mediation routing, an 8-category classifier with LLM+rule-based fallback, a genuine 4-level progressive ladder with bookmark-based human-in-the-loop waits, skill-level adaptation, and a sanitized prompt pipeline. The 7-1G *structure* is largely built.

The gaps are real and material: a **safety hole** (un-timed bookmarks can hang the workflow forever; `Timeout` never produced), **zero DCB audit events and metrics** (7-1G AC9, a hard project rule), **no tenant scoping**, a **stubbed communication collector**, and the **entire 7-11 enrichment story** (context-poor prompts, no resolution-history accumulation, thin signals, naive classify fallback). The `done` marker in sprint-status overstates completion.

**Overall priority: P1** (driven by P0 correctness/audit items that must land, but the workflow is functionally operational today). **Effort: L** — Phase A is contained, but Phases B–C (7-11) depend on context-gathering (7-1F), conventions (2-17), and richer GitHub integration, plus mediation tenant-threading (32-5).
