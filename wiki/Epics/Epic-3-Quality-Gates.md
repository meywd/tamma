# Epic 3: Quality Gates & Intelligence Layer

**Status:** Near Complete (8/12 done; 3-4..3-7 drafted)
**Stories:** 12 (3-1 through 3-12)
**MVP Critical:** All 12 stories
**Tech Spec:** [tech-spec-epic-3.md](https://github.com/meywd/tamma/blob/main/docs/stories/epic-3/tech-spec-epic-3.md)

## Overview

Epic 3 is the "don't let Tamma break itself or its host" epic. It wraps the Epic-2 autonomous loop with **quality gates** (build automation, test execution, static analysis, security scanning) and with **intelligence gates** (research, clarifying questions, ambiguity detection, multi-option design proposals, task complexity assessment). Every gate follows the same shape — execute, fail, ask the AI to fix, retry up to 3 times, **mandatorily escalate** to a human if retries exhaust.

The retry + escalate pattern is the spine of the epic: it prevents the infinite-retry failure mode where an autonomous agent burns money forever on a problem it can't solve. When the 3-retry limit is hit, an `EscalationTriggered` event is emitted, a PR comment with full failure context is posted, a `needs-human-review` label is added, and the loop is paused for that issue. A human unblocks it; the event trail captures the decision.

The "intelligence" side of the epic adds the skills that let Tamma know *when* a task needs human input before it even tries: detect requirement ambiguity before writing code (score 0-100; >70 prompts clarifying questions; >90 suggests decomposition), generate clarifying questions, present multi-option designs for complex features, and assess task complexity against agent skill. Stories 3-10..3-12 layer on performance monitoring, cost-aware AI usage (daily / weekly / monthly budgets with configurable alerts), and complexity-based routing. Cost controls never compromise security or testing gates — those always run.

## Architecture

Quality gates sit **around** loop steps, not inside them. Each gate is an Elsa workflow that wraps an activity: `CiWithDebugRetryWorkflow` wraps CI polling with a debug-retry loop; `TddWithDebugRetryWorkflow` wraps the TDD cycle; `CheckSecurityActivity` invokes the security scanner. When a wrapped step fails, the gate workflow launches the corresponding debug workflow (`DebuggingWorkflow` / `BlockerDiagnosisWorkflow`), which collects error + reproduction context, calls an LLM for a proposed fix, applies the fix, and re-invokes the wrapped step. The retry counter lives in workflow variables; at 3 it triggers escalation.

The TypeScript `@tamma/gates` package implements the **permission side** (layered on top, also consumed by Epic 6): `PermissionEnforcer` evaluates each tool/command request from an agent against a per-agent / per-project policy and returns allow / deny / require_approval. `PermissionResolver` merges global + project + agent-specific policies. Violations route to `ViolationRecorder` and `ViolationAlerter`.

Intelligence gates (ambiguity detection, complexity assessment, research, multi-option design) live as LLM-backed activities inside `ContextGatheringWorkflow` and `PlanGenerationWorkflow` — they run before code generation, not after. The outputs (ambiguity score, complexity estimate, knowledge-gap queue) become workflow variables that later stages read.

## Components

| Component | Purpose | Key files | Status |
|-----------|---------|-----------|--------|
| `CiWithDebugRetryWorkflow` | Wrap CI polling with debug-retry loop, cap at 3 attempts | `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/CiWithDebugRetryWorkflow.cs` | Done (3-1) |
| `TddWithDebugRetryWorkflow` | Wrap TDD cycle with retry on test failure | `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TddWithDebugRetryWorkflow.cs` | Done (3-2) |
| `DebuggingWorkflow` | Collect errors / git history / reproduction / code, LLM diagnosis, record resolution | `Tamma.ElsaServer/Workflows/DebuggingWorkflow.cs` + `Tamma.Activities/Debug/*` | Done (3-1, 3-2) |
| `BlockerDiagnosisWorkflow` | Classify blocker, escalate to senior, detect stalls | `Tamma.ElsaServer/Workflows/BlockerDiagnosisWorkflow.cs` + `Tamma.Activities/Blocker/*` | Done (3-3) |
| `EscalateToSeniorActivity` | Post PR comment, add label, notify, emit event | `Tamma.Activities/Blocker/EscalateToSeniorActivity.cs` | Done (3-3) |
| `CheckSecurityActivity` | Invoke static security scanner (CodeQL / Semgrep) | `Tamma.Activities/Testing/CheckSecurityActivity.cs` | Done (3-9) |
| Static analysis | Lint / format / type-check integrated into CI retry | `.github/workflows/ci.yml`, `packages/*/eslint.config.*` | Done (3-8) |
| `ActionGate` | Runtime allow / deny / require_approval for tool calls from LLM | `Tamma.Activities/Security/ActionGate.cs` | Done |
| `ContentSanitizer` | Sanitize LLM-facing content (prompt-injection defence) | `Tamma.Activities/Security/ContentSanitizer.cs` | Done |
| `ToolCallValidator` | Validate structured tool arguments against schema | `Tamma.Activities/Security/ToolCallValidator.cs` | Done |
| `ErrorRedactor` | Redact secrets from error messages before logging/emission | `Tamma.Activities/Security/ErrorRedactor.cs` | Done |
| `PromptHardening` | Apply defensive-prompt framing | `Tamma.Activities/Security/PromptHardening.cs` | Done |
| `ProviderAllowlist` | Whitelist which providers may be used per tenant | `Tamma.Activities/Security/ProviderAllowlist.cs` | Done |
| `PermissionEnforcer` | Per-agent / per-project tool + command policy evaluation | `packages/gates/src/permissions/permission-enforcer.ts` | Done |
| `PermissionResolver` | Merge global → project → agent policies | `packages/gates/src/permissions/permission-resolver.ts` | Done |
| `ViolationRecorder` + `ViolationAlerter` | Capture + alert on permission violations | `packages/gates/src/violations/` | Done |
| Research activities | Generate research queries, call web / docs, classify findings | `docs/stories/epic-3/story-3-4/` | Drafted |
| Clarifying questions | LLM generates questions for ambiguous requirements | `docs/stories/epic-3/story-3-5/` | Drafted |
| Ambiguity detection | Score issue body 0-100, branch workflow on threshold | `docs/stories/epic-3/story-3-6/` | Drafted |
| Multi-option design | Generate 2-3 design proposals for complex features | `docs/stories/epic-3/story-3-7/` | Drafted |
| Agent performance monitoring | Track per-provider / per-task success + latency + cost | `packages/providers/src/provider-health.ts` | Done (3-10) |
| Cost-aware AI usage | Budget limits (daily/weekly/monthly), alerts, emergency halt | `packages/cost-monitor/src/*` | Done (3-11) |
| Task complexity assessment | LLM-scored complexity → route to appropriate agent | `Tamma.Activities/Assessment/*` | Done (3-12) |

## Class diagram

```
  CiWithDebugRetryWorkflow                       TddWithDebugRetryWorkflow
  - retryCount : int (var)                        - retryCount : int (var)
  - maxRetries = 3                                - maxRetries = 3
  + Build():                                      + Build():
      CheckCiStatusActivity                           TddWorkflow (red-green-refactor)
       -> if fail: DebuggingWorkflow                    -> if fail: DebuggingWorkflow
                    -> ApplyFix                                    -> retry++
                    -> retry++ < 3 ? loop : escalate               < 3 ? loop : escalate

  DebuggingWorkflow
  + Build():
      CollectErrorMessagesActivity
      CollectGitHistoryActivity
      CollectRelevantCodeActivity
      CollectReproductionStepsActivity
      CollectTestResultsActivity
      ClassifyDebugContextActivity
      AIDiagnosisActivity           --> LLM with context
      SelectHypothesisActivity
      RefineHypothesisActivity      (loop while insufficient)
      CompileDebugReportActivity
      WriteRegressionTestActivity
      RecordResolutionActivity

  BlockerDiagnosisWorkflow
  + Build():
      CollectCIStatusActivity
      CollectCommunicationActivity
      CollectGitActivityActivity
      CollectInactivityActivity
      DetectProgressActivity
      ClassifyBlockerActivity
      EscalateToSeniorActivity       --> post PR comment + label + event

  Security activities  (Tamma.Activities.Security)
  - IContentSanitizer  <<interface>>            - IToolCallValidator <<interface>>
  - ContentSanitizer                            - ToolCallValidator
  - ActionGate                                  - ProviderAllowlist
  - IErrorRedactor <<interface>>                - PromptHardening
  - ErrorRedactor                               - SanitizationResult

  Permission system  (packages/gates)
  PermissionEnforcer
  - resolver : PermissionResolver
  - recorder : ViolationRecorder
  + evaluate(agent, project, action) : 'allow'|'deny'|'require_approval'
         |
         v
  PermissionResolver
  - globalPolicy, projectPolicy, agentPolicy
  + resolve(ctx) : EffectivePermissions
```

## Data flow — "build failure triggers retry, then escalation" sequence

```
SingleIssueCycle  CiWithDebugRetry   DebuggingWorkflow   AI Agent   GitHub       Event Store
     |                  |                 |                |          |              |
     |---- dispatch --->|                 |                |          |              |
     |                  | retryCount = 0  |                |          |              |
     |                  |                 |                |          |              |
     |                  | CheckCiStatus --------------- GET /check-runs ---->        |
     |                  |<--------- status: failure -----------------------|         |
     |                  |                 |                |          |              |
     |                  | emit CI.FAILED.ATTEMPT_1 ------------------------------->|
     |                  |                 |                |          |              |
     |                  | launch -------->|                |          |              |
     |                  |                 | CollectErrorMessages -- GET logs ->|     |
     |                  |                 | CollectGitHistory                        |
     |                  |                 | CollectRelevantCode                      |
     |                  |                 | ClassifyDebugContext                     |
     |                  |                 | AIDiagnosis  --> LLM(context) -->|       |
     |                  |                 |<------- proposed fix + test -----|       |
     |                  |                 | WriteRegressionTest                      |
     |                  |                 | RecordResolution                         |
     |                  |                 |                                          |
     |                  |<----------------| commit fix                               |
     |                  |                 |                                          |
     |                  | retryCount = 1  |                                          |
     |                  | CheckCiStatus (re-run)                                     |
     |                  |<--------- status: failure -----------------------|         |
     |                  | emit CI.FAILED.ATTEMPT_2 ------------------------------->|
     |                  | [similar retry]                                            |
     |                  | retryCount = 2  |                                          |
     |                  | CheckCiStatus                                              |
     |                  |<--------- status: failure -----------------------|         |
     |                  | emit CI.FAILED.ATTEMPT_3 ------------------------------->|
     |                  |                                                            |
     |                  | retryCount >= 3 => ESCALATE                                |
     |                  | EscalateToSeniorActivity ----> POST PR comment ---->      |
     |                  |                           ----> PUT label needs-human-review
     |                  |                           ----> notify (Slack/email)      |
     |                  | emit ESCALATION.TRIGGERED ------------------------------->|
     |                  |                                                            |
     |<------ result: needsHuman                                                     |
     |                                                                               |
     | report ESCALATED to ADL Orchestrator                                          |
     | loop pauses for this issue until human responds                               |
```

## Use cases

- **CI fails because a dependency version bumped**: `CiWithDebugRetryWorkflow` runs → `DebuggingWorkflow` classifies failure as `dependency-mismatch` → LLM proposes lockfile update → commit → CI green → cycle continues.
- **Test fails for genuinely hard logic bug**: 3 debug retries all produce variants of the wrong fix → `EscalateToSeniorActivity` fires → dev sees PR comment "3 attempts exhausted; diagnosis: ..." → fixes manually → comments `unblock` → loop resumes.
- **Issue is ambiguous** ("make the button better"): `ContextGatheringWorkflow` runs ambiguity detection → score 82 → `ClarifyingQuestionsActivity` generates 3 questions → posts comment → engine suspends on bookmark → issue reporter answers → workflow resumes with enriched context.
- **Complex feature** ("implement caching"): task complexity ≥ 7/10 → `MultiOptionDesignActivity` generates 3 design proposals (in-memory LRU / Redis / CDN) → human picks → plan review uses chosen design.
- **Unknown technology** ("add Svelte 5 runes"): LLM signals low confidence → `ResearchActivity` fetches Svelte 5 docs + examples → feeds into context → code generation proceeds with real docs not training-era guesses.
- **Cost budget exceeded**: monthly spend crosses 90% of `budget.monthly_usd` → `CostMonitor` sends alert → approaching 100%: switch to cheaper provider tier → 100%: emergency halt of non-security work; security + test gates continue.

## Dependencies

**Upstream:**
- [Epic 1](Epic-1-Foundation.md) — `IAgentProvider` used for all AI-assisted fixes.
- [Epic 2](Epic-2-Autonomous-Loop.md) — gates wrap loop activities.

**Downstream:**
- [Epic 4](Epic-4-Event-Sourcing.md) — gate results + escalation events captured.
- [Epic 5](Epic-5-Observability.md) — gate success / failure / cost metrics surface in dashboards; alerts.
- [Epic 6](Epic-6-Context-Knowledge.md) — research capability pulls from the RAG / knowledge base; permission + cost packages co-owned.
- [Epic 11](Epic-11-Security.md) — security scanning + ActionGate + ContentSanitizer build on 3-9.

## Current state

**Landed:**

- **Retry + escalation** (3-1, 3-2, 3-3) — `CiWithDebugRetryWorkflow`, `TddWithDebugRetryWorkflow`, `BlockerDiagnosisWorkflow` running in production. Retry cap of 3 enforced.
- **Static analysis + security** (3-8, 3-9) — ESLint + TypeScript strict on all TS packages; `CheckSecurityActivity` invokes CodeQL / Semgrep paths; `.github/workflows/codeql.yml` runs on PRs.
- **Security runtime** — `ActionGate`, `ContentSanitizer`, `ToolCallValidator`, `ProviderAllowlist`, `ErrorRedactor`, `PromptHardening` under `Tamma.Activities/Security/`.
- **Agent performance monitoring** (3-10) — `provider-health.ts` tracks success / latency / cost per provider.
- **Cost-aware usage** (3-11) — `@tamma/cost-monitor` package with tracker, limit manager, alert manager, pricing config, budget reports.
- **Task complexity assessment** (3-12) — `Tamma.Activities/Assessment/*` (skill profile, complexity classification, question generation).

**Drafted:**

- 3-4 Research Capability for Unfamiliar Concepts — story brief + context XML; activity not yet landed.
- 3-5 Clarifying Questions for Ambiguous Requirements — story brief exists; partial overlap with `Assessment` activities.
- 3-6 Ambiguity Detection Scoring — scoring approach defined; not wired into `ContextGatheringWorkflow` yet.
- 3-7 Multi-Option Design Proposals — story brief exists; not yet a standalone activity.

**Drift from briefs:**

- The original Epic 3 put "escalation workflow" as a single story (3-3). Actual implementation spreads across `BlockerDiagnosisWorkflow` (stalled agents), `CiWithDebugRetryWorkflow` / `TddWithDebugRetryWorkflow` retry-exhaustion paths, and `EscalateToSeniorActivity`. Wiki page now reflects the fuller structure.
- Story 3-9 "security scanning" is more developed than the brief: in addition to SAST integration, the `Security/` activities subtree provides a full runtime gate (ActionGate + ContentSanitizer + ProviderAllowlist). This work landed partly under Epic 11 scope.
- The 3-11 cost-monitoring package lives at `packages/cost-monitor/` rather than inside `@tamma/gates` — it's structurally closer to Epic 6.
- Some tracking docs mark ambiguity detection (3-6) as Done; the wiki lists Drafted because the scoring activity is not yet wired into the production `ContextGatheringWorkflow` — only research prototypes exist.

## See also

- **Docs:** [docs/stories/epic-3/](https://github.com/meywd/tamma/tree/main/docs/stories/epic-3) — all 12 story briefs + implementation plans.
- **Tech spec:** [tech-spec-epic-3.md](https://github.com/meywd/tamma/blob/main/docs/stories/epic-3/tech-spec-epic-3.md).
- **Related wiki pages:**
  - [Workflow: Debugging](Workflow-Debugging) — debug activity flow.
  - [Workflow: Blocker Diagnosis](Workflow-Blocker-Diagnosis) — escalation flow.
  - [Workflow: CI with Debug Retry](Workflow-CI-With-Debug-Retry) — CI gate with retry.
  - [Workflow: TDD with Debug Retry](Workflow-TDD-With-Debug-Retry) — TDD gate with retry.
  - [Security](Security) — security posture and runtime gates.
  - [Epic 6: Context & Knowledge](Epic-6-Context-Knowledge.md) — permission system and cost monitor both surface through dashboards tied to this epic.
  - [Epic 11: Security](Epic-11-Security.md) — broader security epic.
- **Code paths:**
  - `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/DebuggingWorkflow.cs`, `BlockerDiagnosisWorkflow.cs`, `CiWithDebugRetryWorkflow.cs`, `TddWithDebugRetryWorkflow.cs`.
  - `apps/tamma-elsa/src/Tamma.Activities/Debug/`, `Blocker/`, `Security/`, `Assessment/`.
  - `packages/gates/src/permissions/`, `packages/gates/src/violations/`.
  - `packages/cost-monitor/src/` (cost-aware usage).
  - `packages/providers/src/provider-health.ts` (agent performance monitoring).
