# Workflow Structural Audit — 2026-06-22

Full structural audit of all **37 Elsa workflow files** (≈150 activities) in `apps/tamma-elsa`,
assessed on 7 dimensions (purpose/ownership · architecture+pivot alignment · structural correctness ·
error-handling+resilience · DCB event emission · naming/conventions · per-mode correctness), in light
of the Epic-32/34/37/38 agent-architecture pivot and other story updates.

**Totals: P0 = 11 · P1 = 67 · P2 = 48** (~126 findings).

| Cluster | Report | P0 | P1 | P2 |
|---|---|---|---|---|
| agent / LLM execution (8) | [workflow-audit-agent-llm.md](workflow-audit-agent-llm.md) | 9 | 9 | 10 |
| planning / mentorship (7) | [workflow-audit-planning.md](workflow-audit-planning.md) | 1 | 15 | 11 |
| triage (7) | [workflow-audit-triage.md](workflow-audit-triage.md) | 0 | 12 | 8 |
| cycle / git / CI / deploy (8) | [workflow-audit-cycle-git-cicd.md](workflow-audit-cycle-git-cicd.md) | 0 | 21 | 13 |
| tenant / infra / analytics (7) | [workflow-audit-tenant-infra.md](workflow-audit-tenant-infra.md) | 1 | 10 | 6 |

## The two dominant, systemic themes
1. **Silent-failure / empty-fallback / fail-open** — the single largest category, spanning triage,
   deploy, CI, task-review, update-issue-status, context-gathering. A failed sub-step is repeatedly
   swallowed into a *successful*-looking result (`{}` context, fabricated default decisions, COMPLETED
   on a logged failure, deploy stage defaulting to "success"). This violates the project's explicit
   **no-false-success** + **tenant→system→error (never empty/plain)** rules. Mostly **independent of the
   pivot** → the highest-value fix-now wave.
2. **Sparse / divergent DCB events** — many steps (esp. git writes and several thin wrappers) emit no
   audit-trail events, and triage event names diverge from Story 26-1 AC9. A systemic audit-trail hole.

There are **no live-vendor-key rule-1 violations outside Epic-32's known scope** — all LLM work routes
through the centralized `llm-call` sub-workflow, and triage git writes already go via the tamma-api
engine callback. The remaining in-engine direct calls are (a) the 9 direct-LLM activities (Epic-32) and
(b) git-platform writes that are co-hosting violators latent until per-tenant dedicated compute (Epic 38).

---

## Bucket A — Auto-resolved by 32-5 T6 (caller-cutover). NO separate workflow work.
The fix is in the **activity**, not the workflow graph; repointing the 9 direct-LLM callers through the
`call-LLM` endpoint resolves these. Workflow graphs are unchanged.
- `TddWorkflow` — `WriteTests` / `WriteImplementation` / `AnalyzeCode` / `ApplyRefactoring` (4 P0)
- `DebuggingWorkflow` — `AIDiagnosisActivity` (P0, no fallback today)
- `ReviewFixWorkflow` — `ApplyReviewFixesActivity` second direct-LLM path (P0)
- `LlmCallWorkflow` — `CallLlmInlineActivity` repoint (P0) — **keep** the retry/provider-chain/CB boundary
- Compliant LLM paths (`BlockerDiagnosis`, `ReviewFix.generateFixes`, `Debugging.applyFix`,
  all triage/planning LLM) inherit mediation for free once the activity is repointed.

## Bucket B — Coordinated with the pivot: thread `tenantId` (SaaS correctness)
Workflows dispatch `llm-call` (and vector-store/PO-summary callbacks) without seeding/forwarding
`tenantId`, so SaaS silently falls back to **system-default prompts/conventions + platform creds**. The
mediated path (32-5) consumes `tenantId`, so thread it through the dispatch at the same time. Affects
~7 workflows: `PlanReview` (×14 dispatches), `ContextGathering` (×6 + vector store), `Mentorship`,
`TaskReview` (×4), `TaskCreation`, `PlanGeneration` is the lone compliant reference. **P1.**

## Bucket C — Epic 38 (non-LLM step mediation: git / Slack / agent-dispatch)
Git-platform write activities call `IGitHubIntegrationService` directly in-engine (co-hosting violators,
latent until per-tenant dedicated compute) **and** emit no DCB events:
- `BranchCreationWorkflow` (`CreateBranchActivity`), `PullRequestWorkflow` (`CreatePullRequestActivity`),
  `MergeWorkflow` (highest blast radius), `CodeReviewWorkflow` (PR create/merge),
  `ReviewFixWorkflow` (`AnalyzeReview`). **P1, "Depends on: Epic 38".**

## Bucket D — FIX-NOW, independent of the pivot (the immediate enhancement wave)
These don't wait on 32-5 or Epic 38 and most are correctness/safety:
- **Fail-open gates (fail-closed violations):**
  - `DeploymentPipelineWorkflow` — stage gating defaults to "success"; no human gate before prod; no real deploy/release/tag step despite the description. (P1)
  - `TestingWorkflow` — `WaitForCIResults` timeout declared-but-unenforced (infinite hang); parse-failure fails open into a default payload. (P1)
- **Triage silent-failure → false success:** `TriageItemCycle` (zero error handling), `TriagePanelReview`
  (failed reviews aggregated as `{}`), `TriagePODecision` (fabricated default `needs-human` on LLM
  failure), `UpdateIssueStatusWorkflow` (swallows failure → COMPLETED, no FAILED event),
  `TriageContextGathering` (context → `{}`). (P1)
- **Graph correctness bugs:**
  - `BlockerDiagnosisWorkflow` — escalation never sets `isResolved`, so it **always** reports "Escalated". (P1)
  - `SingleIssueCycleWorkflow` — merge-dispatch + wait-bookmark are parallel edges with **no failure path** (hangs on merge failure); no error edges off any sub-workflow dispatch (silent failure); redundant close/merged notices. (P1)
  - `CodeReviewWorkflow` — `Commented` outcome can spin. (P1)
  - `DebuggingWorkflow` — loop bound is activity-internal, not graph-enforced. (P2→P1)
- **Dead / orphaned workflows — decide wire-or-delete:** `MergeApprovalWorkflow` and
  `CiWithDebugRetryWorkflow` are unreachable (nothing dispatches them; the cycle runs CI inside
  `ExecuteAgentActivity`). `ProvisionTenantV2Workflow` is DI-wired but has **zero production callers**
  (admin endpoint still uses v1) with no-op RegisterSecrets/quota stubs. (P1)
- **`RotateSecretWorkflow` P0** — `RETIRE_SECRET_VERSION` rows the saga enqueues are **never drained**
  (`SweepDueRetireTasksAsync` has no hosted-service/endpoint caller) → old secret versions never retired
  past the grace window. Latent only because the `rotate-secret` workflow has no production trigger
  (rotation goes via `ISecretRevealService`/`KekRotationCoordinator`). The documented **type-aware task
  reservation** fix unblocks both this and the safe enablement of `PlatformTaskWorker:RunOnStartup`. (P0)
- **`DeleteTenantWorkflow`** — a failed drop step leaves no `TENANT.DELETE.FAILED` terminal event. (P1)
- **DCB event gaps + Story 26-1 event-name alignment** (`TRIAGE.ISSUE.STARTED/COMPLETED` expected;
  impl emits `TRIAGE.FETCH.ITEMS`/`TRIAGE.APPLY.RESULT`); add missing per-stage lifecycle events. (P1)

## Bucket E — Feature gaps & P2 polish
- `AssessmentWorkflow` **P0** — its two "AI" steps are hardcoded heuristics (length/keyword scoring)
  that never call an LLM, feeding fake skill signal into Mentorship routing; gathered context discarded.
  **Depends on: 32-5 (wire to `llm-call`) + Epic 6 (use the context).**
- Story 26-1 triage gaps (caching/re-triage, milestone/board assignment, `tamma-auto`→ADL pickup);
  `CranlProvisioning` poll-loop backoff/CB + `TENANT.PROVISION.*` events; `HourlyAnalytics` permanently
  excludes events arriving >5 min after the hour close; assorted naming/convention P2s.

---

## Recommended sequencing
1. **Wave 1 (now, independent):** Bucket D — fail-closed gates, triage silent-failure, the graph bugs,
   the dead-workflow decisions, the `RotateSecret` drain, the missing terminal/lifecycle events. Highest
   value, no pivot dependency, aligns with the project's own no-false-success rule.
2. **With the pivot:** Bucket B (`tenantId` threading) lands alongside 32-5's mediated path; Bucket A is
   free once 32-5 T6 repoints the activities.
3. **Epic 38 workstream:** Bucket C (git/Slack mediation + git-step events).
4. **Backlog:** Bucket E feature gaps.
