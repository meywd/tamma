# Implementation Plan — Story 41-20: Scheduled Security Audit Workflow

> **Read the blocker first.** This story's AC1 ("scheduled, tenant-scoped, idempotent") depends on
> the **tenant-aware scheduled-trigger seam**, which does not exist and which **no story in Epic 41
> owns** (README Wave-0 table, owner *"none — must be written"*). Everything else in this plan is
> buildable today and is written so the workflow ships **dispatchable-but-untriggered**, with AC1
> explicitly marked unreachable until the seam lands. Do not clone
> `HourlyAnalyticsRollupScheduler` to close the gap — see D8.

## Scope & Deliverable

A new binding `DefinitionId = "security-audit"` that, per tenant per window, runs the three security
audit lenses — `(security, audit-dependencies)`, `(security, audit-secrets)`,
`(security, review-compliance)` — each as one `document-lifecycle` child producing a typed
`Findings` document, with a per-lens fail-closed edge (a failed lens is recorded, never dropped) and
a `SECURITY_AUDIT.*` audit trail. Findings carry evidence, severity and remediation; the
exposed-secret path escalates and can dispatch the existing `rotate-secret` saga. The binding
contributes no parse, no `Finish`, no `llm-call`.

**Not in scope:** building the scheduler seam (D8); building governed audit tooling (that is Epic 42,
and the story already carries the caveat); any change to the CodeQL workflow or the secrets
subsystem.

## Pre-Reading

- `docs/stories/epic-41/story-41-20/41-20-scheduled-security-audit.md` — the story (ACs are source of truth)
- `docs/stories/epic-41/README.md` — rule 1's six thinness clauses; the Wave-0 table row for the unowned scheduler; the Dependencies bullet "Scheduled workflows have no reusable pattern"; the Epic 42 table row marking 41-20's audit path *"possible but ungoverned and unclassified"*
- `apps/tamma-elsa/src/Tamma.Core/Documents/Types/Findings.cs` — **read this before writing AC2**: the `Finding` record (`:12-22` — `title`, `summary`, `relevance`, `confidence`, `citations`, `rank`; **no `severity`, no `remediation`**), `FindingsDocumentType` violation constants (`:49-76`), and the `EMPTY_FINDINGS` rule (`:58`, enforced at `:110-116`)
- `apps/tamma-elsa/src/Tamma.Core/Documents/IDocumentType.cs:32-44` — the `ValidateWithContext` default-interface-member seam
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TestCaseCreationWorkflow.cs:148` — the only landed consumer of `validationContextJson`; the recipe this story reuses
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/ResearchWorkflow.cs` — the landed `Findings` producer; **the closest sibling to copy**
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/TaskCreationWorkflow.cs` + `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/TaskCreationWorkflowStructureTests.cs` — the reference binding + reference structure-test set
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/RotateSecretWorkflow.cs:34` (`DefinitionId = "rotate-secret"`) — the saga AC3 integrates with
- **The existing secrets subsystem — read before proposing any new machinery** (`apps/tamma-elsa/src/Tamma.Api/Services/Secrets/`): `ISecretStore`/`SecretStore`, `Postgres/PostgresSecretStoreBackend` + `SecretEnvelope` (envelope encryption), `KekProvider`/`KekRotationCoordinator`, `Query/SecretQueryService`, `Reveal/SecretRevealService` + `RevealTokenSweeper`, `Rotation/RotationTriggerService` (the per-secret concurrency guard), `Rotation/SecretAutoRotationScheduler` (Story 29-6), `RotationSchedule`/`RotationScheduleCalculator` (incl. its registered-`CronEvaluator` seam), `Handlers/*` (Postgres role / Cranl env-var / generic HTTP rotation handlers), `Stopgap/*`
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/HourlyAnalyticsRollupScheduler.cs` (`FireAtMinute` `:34`, `_lastFired` `:83`, hardcoded target `:198-200`, `ComputeAdvisoryLockKey(year, dayOfYear, hour)` `:241`) — the **non**-pattern
- `.github/workflows/codeql.yml` — the existing static-analysis surface (`push`/`pull_request` only; **no `schedule:`**; languages `javascript-typescript`, `csharp`, `actions`; config at `.github/codeql/codeql-config.yml`)
- `apps/tamma-elsa/src/Tamma.Api/Program.cs:753-764` — the six registered `IToolExecutor`s (`FileRead`, `FileWrite`, `SearchCode`, `ShellExecute`, `GitOperations`, `RunTests`) — all coding-oriented; `ShellExecuteTool` is the ungoverned audit path the story's Epic 42 caveat names
- `apps/tamma-elsa/src/Tamma.Core/Agents/AgentAction.cs:92-94`, `RolePhaseMap.cs:133-135`, and `apps/tamma-elsa/src/Tamma.Api/Prompts/security/{audit-dependencies,audit-secrets,review-compliance}.md` — **all three cells and all three prompt files exist**
- `apps/tamma-elsa/src/Tamma.Core/Documents/Policy/AcceptanceRules.cs:195-210` + `AcceptanceGuardrails.cs:45-134` — the escalation-class mechanism (read before designing AC3)
- **NOT FOUND:** any tenant-aware scheduled-trigger seam; `.github/dependabot.yml`; any `schedule:` trigger on `codeql.yml`; any registered audit/scan `IToolExecutor`

## Corrections to the story

1. **CONFIRMED — all three cells and all three prompt files exist; this story mints no taxonomy
   cell.** `AgentAction.cs:92-94` (`AuditDependencies`, `AuditSecrets`, `ReviewCompliance`),
   `RolePhaseMap.cs:133-135` (all three in `Security`'s `FreezeSet`), and
   `src/Tamma.Api/Prompts/security/audit-dependencies.md`, `audit-secrets.md`,
   `review-compliance.md` all ship. So **no `AgentAction` member, no `RolePhaseMap` edit, no new
   prompt file, and no bump of `AgentActionTests.cs:38` (`Be(80)`) or `RolePhaseMapTests.cs:64`
   (`HaveCount(80)`).** This story has **no 41-1a dependency**.

2. **CONFIRMED — `rotate-secret` exists** (`RotateSecretWorkflow.cs:34`), and the secrets subsystem
   behind it is substantial and landed (Epic 29): `ISecretStore` + a Postgres envelope-encrypted
   backend, KEK provider + rotation coordinator, a per-secret concurrency guard
   (`RotationTriggerService`), three rotation handlers, a reveal service with a token sweeper, and a
   query service. **AC3 needs no new rotation machinery** — it needs one `DispatchWorkflow` at the
   escalation edge. Do not build a second rotation path.

3. **CONFIRMED — the scheduler indictment.** `HourlyAnalyticsRollupScheduler` hardcodes its target
   (`:198-200`), exposes one `FireAtMinute` int (`:34`), keeps last-fired in a process field (`:83`),
   and locks on `(year, dayOfYear, hour)` with **no tenant component** (`:241`). AC1 is unreachable
   without a new seam, and **no story writes one**.

4. **NEW — there is a second, better-shaped scheduler the story does not mention, and it is still not
   the seam.** `SecretAutoRotationScheduler` (Story 29-6,
   `Tamma.Api/Services/Secrets/Rotation/`) gets idempotency *right in the way this story needs*: its
   state is a **durable per-row `NextRotationDueAt`** in the database plus a per-secret concurrency
   guard via `IRotationTriggerService`, not an in-process `_lastFired`. That is the shape the seam
   should copy. But it too hardcodes exactly one target (`rotate-secret`), is `Enabled = false` by
   default, and is **not tenant-partitioned**. Also relevant: `RotationScheduleCalculator` already
   defines a pluggable `CronEvaluator` delegate seam (`RegisterCronEvaluator`) that Story 29-2 was to
   fill with Cronos — a ready-made cron-shape seam for whoever writes the trigger. **Record all
   three facts for the seam's future owner; build none of it here.**

5. **NEW — AC2 is factually impossible against the shipped `Findings` type, in two separate ways.**
   - **(a) `severity` and `remediation` are not fields.** `Finding` (`Findings.cs:12-22`) has exactly
     `title`, `summary`, `relevance`, `confidence`, `citations`, `rank`. AC2 requires "severity +
     remediation" per finding.
   - **(b) "empty ⇒ valid empty report" is the exact opposite of what the type does.**
     `FindingsDocumentType.EmptyFindings` (`:58`) is a **violation**, enforced unconditionally at
     `:110-116` with the comment *"an empty list is a violation, not a valid 'nothing found'"*. A
     clean audit producing `findings: []` would be **rejected**.

   **Correction / plan of record (D2):** add `severity` and `remediation` as **optional** members of
   `Finding` (additive, nullable — `research` and `triage-context-gathering` are unaffected), and make
   them **required only for this story's producers** via `FindingsDocumentType.ValidateWithContext`
   gated on a `validationContextJson` this binding supplies — the exact seam 41-18 uses and
   `TestCaseCreationWorkflow` already drives. For (b), adopt the **all-clear finding** convention: a
   clean lens emits **one** finding ("no vulnerable dependencies found") with `severity: "none"` and a
   citation to the scan output. This keeps `EMPTY_FINDINGS` intact for every other `Findings`
   producer and gives the audit an auditable positive record instead of an empty document. The
   story's AC2 wording is amended accordingly.

6. **NEW — "three parallel lenses aggregating into ONE ranked `Findings` document" is not expressible
   as a thin binding.** The lifecycle produces **one** document from **one** producer cell
   (`DocumentLifecycleWorkflow` reads a single `producerRole`/`producerAction`,
   `:169-171`). Three producers into one document would require a bespoke aggregator in the binding —
   which fails thinness clauses (a) and (c) outright. The review-panel machinery is not a substitute:
   the panel's per-member action comes from `RolePhaseMap.GetPanelActionForRole` and a role appears
   at most once in a roster, so `security` cannot sit three times with three lenses.
   **Correction (D3): three lenses ⇒ three `Findings` documents**, produced by one dispatch node
   executed once per lens in a loop (the 41-17 Half B / per-item shape), each with its own
   producer-scoped issue id. "One ranked report" becomes a **read-side** concern: the three documents
   share an `issueId`/`repository` lineage and 39-11's lineage API assembles the view. No aggregation
   document, no aggregator code, thinness intact.

7. **NEW — AC3's "exposed live secret is an always-escalate class" is not expressible as a class.**
   Same finding as 41-19's Correction 4: `EscalationClassKind` is `document-type` or `agent-action`
   **only** (`AcceptanceRules.cs:200-210`), matched by exact string equality
   (`AcceptanceGuardrails.TryPreGate`, `:50-68`). `{"kind":"agent-action","key":"audit-secrets"}`
   escalates **every** secret audit, including a clean one — contradicting the story's own 85–100
   autonomy row ("agent audits and self-accepts"). **See D4** for the two payload-aware mechanisms
   that do exist (`Validate` and the `Clamp` `BlockingReviewViolation` arm) and the routing edge that
   actually carries AC3.

8. **NEW — the story's Epic 42 caveat is accurate and slightly understated.** Verified: exactly six
   `IToolExecutor`s are registered (`Program.cs:753-764`), all coding-oriented; the only audit path
   is `ShellExecuteTool`. Additionally, the repo **already runs CodeQL** (`.github/workflows/codeql.yml`,
   C#/TS/actions) but only on `push`/`pull_request` — there is **no `schedule:`** and **no
   `dependabot.yml`** — and nothing in Tamma reads GitHub's code-scanning or secret-scanning
   results back. **Design consequence (D5):** the audit lenses should consume *existing* evidence
   surfaces where they exist (CodeQL alerts, the platform's secret-scanning surface, the dependency
   manifest) through the Git-platform abstraction, rather than shelling out to re-run scanners. Note
   for the record: `run_secret_scanning` is a **GitHub MCP tool available to development agents**, not
   a Tamma runtime capability — it must not be cited as machinery this workflow can call.

9. **NEW — AC4's `[ResumeBehavior(LatestStateReEntry)]` is, unusually, correct as written.** Unlike
   41-18/41-19 (whose stories say `Both`), this story already names the honest mode for a thin
   binding. No correction needed.

10. **NEW — the story omits the two rule-1(f) lockstep obligations** (a `WorkflowDocumentInterface`
    row + the `WorkflowInterfaceGraphTests.cs:45` edge-pin bump) **and the `ContractBindingTests`
    obligation** (three new `Bindings` entries — one per lens — with authority
    `FindingsDocumentType.Validate`, mandated by the universal-authority pin at `:626`). All four are
    added to this plan's DoD.

## Design Decisions

- **D1 — New `DefinitionId = "security-audit"`; no incumbent, no rewiring.** Inputs: `repository`,
  `tenantId`, `windowKey` (the trigger's idempotency key — a plain string input so the binding is
  trigger-agnostic), `lensesJson?` (defaults to all three), `issueId?`, `acceptanceRulesJson?`,
  `complianceChecklistJson?`. Outputs: `status`, `lensResultsJson` (one row per lens: lens, status,
  outcome, documentId, findingCount, highSeverityCount), `escalatedCount`.
  `builder.Version = WorkflowVersions.ComputedVersion`.

- **D2 — `severity` + `remediation` are additive optional members on `Finding`, made required for
  this story's producers through `ValidateWithContext` (Correction 5).** Concretely, in
  `Tamma.Core/Documents/Types/Findings.cs`:
  - `Finding` gains `[JsonPropertyName("severity")] public string? Severity { get; init; }` and
    `[JsonPropertyName("remediation")] public string? Remediation { get; init; }` — **nullable**, so
    every existing `research` / `triage-context-scan` fixture round-trips byte-identically.
  - `FindingsDocumentType` gains `SEVERITY_REQUIRED`, `SEVERITY_OUT_OF_VOCABULARY` (closed set:
    `none|low|medium|high|critical`) and `REMEDIATION_REQUIRED`, applied **only** from
    `ValidateWithContext` when the context carries `{"requireSeverityAndRemediation":true}`. With an
    empty context `ValidateWithContext` is byte-identical to `Validate` — the no-regression
    guarantee, asserted by test.
  - **Why not a new document type:** the README's reuse-first rule names `Findings` for the security
    audit, and forking it would add a vocabulary member plus two count-pin bumps plus an
    `AcceptanceDefaults` arm for one extra field pair.
  - **Why not unconditional:** `research` and `triage-context-gathering` produce `Findings` today and
    have no severity concept; an unconditional rule would invalidate every one of their documents.
  - **The all-clear convention** (Correction 5b) is a *prompt* rule, not a validator rule:
    `audit-*.md` instructs "if the lens finds nothing, emit exactly one finding with
    `severity: "none"` citing the scan output". `EMPTY_FINDINGS` stays untouched and keeps meaning
    "the model returned nothing", which is still a real failure.

- **D3 — One dispatch node, N lens iterations, N `Findings` documents (Correction 6).** The graph
  loops: `hasMoreLenses(FlowDecision) → extractCurrentLens → ComputeReEntryPosition(scoped) →
  DispatchLifecycle → ReadLensExit → EmitAuditLens → incrementLens`. Each lens uses a
  producer-scoped id, `CreationBindingHelper.ScopeIssueId(auditId, lensWire)`
  (→ `{repository}#{windowKey}#audit-dependencies`), so:
  - the three lenses never collide in 39-11's `(issueId, documentType)`-scoped latest-accepted read
    (the `TaskCreationWorkflow` D2 problem, three-way);
  - **per-lens idempotency is the lifecycle's own re-entry** — a process kill mid-audit re-enters at
    `Complete` for finished lenses and `Produce` for the rest. That is what makes AC1's "each lens
    fail-closed" and the durability half mechanical.
  Thinness clause (a) is satisfied literally: **one** `DispatchWorkflow` node in the graph, executed
  three times. Clause (c) holds: the per-lens failure edge rejoins `EmitAuditLens` and continues the
  loop — no `Finish`, no bespoke terminal.

- **D4 — AC3 rides three existing mechanisms, none of them a new escalation class (Correction 7).**
  1. **Validation** — an `audit-secrets` finding at `severity: "critical"` with an empty
     `remediation` is rejected by D2's `REMEDIATION_REQUIRED`; the model cannot report an exposed
     secret without stating what to do about it.
  2. **The landed clamp** — `AcceptanceGuardrails.Clamp`'s `BlockingReviewViolation` arm
     (`:103-110`) already forces `Accept` → `Escalate` whenever the review is not a clean approval or
     carries a blocking issue. A security reviewer raising a critical issue on an exposed-secret
     finding escalates with **zero new code**.
  3. **The routing edge this story does own** — after a lens exits, `SecurityAuditBindingHelper`
     reads the accepted `Findings` for `severity == "critical"` on the `audit-secrets` lens and, when
     present, the binding takes a `DispatchWorkflow("rotate-secret")` edge (`WaitForCompletion=false`)
     and emits `SECURITY_AUDIT.SECRET_EXPOSED`. This is a **side-effect dispatch on a typed value**,
     not a quality decision, and is the direct analogue of `DeploymentPipelineWorkflow`'s rollback
     branch. It is the one place this story adds a second `DispatchWorkflow` node — declare and
     justify the deviation from a literal reading of clause (a) in the story's ACs, exactly as rule 1
     requires ("any story that cannot meet (a)–(f) must name the deviation and justify it"). The
     structure test pins that the second dispatch's literal definition id is `rotate-secret` and that
     it is unreachable except from the critical-severity edge.
  4. **Deliberately NOT chosen:** `{"kind":"agent-action","key":"audit-secrets"}` in
     `AlwaysEscalate` — it escalates every secret audit. It stays a valid *deployment* choice for a
     paranoid tenant and the tests prove it works; it is not the mechanism AC3 rests on.

- **D5 — Lenses read existing evidence surfaces; they do not re-implement scanners (Correction 8).**
  `audit-dependencies` consumes the dependency manifest + advisory data; `audit-secrets` consumes the
  platform's secret-scanning surface; `review-compliance` consumes a supplied checklist. Where a
  surface is reachable only by shelling out, the run is **ungoverned and unclassified until Epic 42**
  — the story already carries that caveat and this plan does not weaken it. Concretely for the first
  cut: pass the evidence in as `producerVariablesJson` (assembled by the caller/trigger or a context
  scan), so the produce cell runs **without tools** and the Epic 42 gap does not block the story.
  Tool-enabled lenses are a later flip of `enableTools` in the prompt front matter.

- **D6 — `[ResumeBehavior(ResumeMode.LatestStateReEntry)]`, no allowlist entry** (the story is
  already correct — Correction 9), with one `ComputeReEntryPositionActivity` node in the graph
  (39-10 clause (c)) and no `Wait*` activity.

- **D7 — New event family `SECURITY_AUDIT.*`.** New file
  `apps/tamma-elsa/src/Tamma.Activities/Security/SecurityAuditEvents.cs` in the `ResearchEvents.cs`
  shape: `Started` = `SECURITY_AUDIT.STARTED`, `Lens` = `SECURITY_AUDIT.LENS` (one per lens, success
  **or** failure — AC1's "a lens failure is recorded, not dropped"),
  `Report` = `SECURITY_AUDIT.REPORT` (terminal roll-up: per-lens counts),
  `SecretExposed` = `SECURITY_AUDIT.SECRET_EXPOSED` (LOUD, D4.3),
  `Failed` = `SECURITY_AUDIT.FAILED` (LOUD terminal). `ParseTenantId` + `StatusForEvent`
  (a `LENS` row carrying a failure detail and the two LOUD members are error-status).
  Tags: `repository`, `tenantId`, `windowKey`, `lens`.

- **D8 — The scheduler seam is a declared dependency, not a deliverable, and the workflow ships
  trigger-agnostic.** The binding takes `windowKey` as a plain input and treats it as opaque; every
  producer-scoped id derives from it. That means: (i) a manual/API dispatch works today and is
  fully testable; (ii) the day a seam exists, wiring is a registration, not a workflow change;
  (iii) **at-most-once-per-window-per-tenant across the fleet is NOT provided by this story** — only
  per-lens idempotency within a given `windowKey` is (D3). AC1 is therefore split in the DoD table
  into the half this story delivers and the half it cannot. For whoever writes the seam, the shape is
  in Correction 4: durable per-row due-time (`SecretAutoRotationScheduler`), a tenant component in the
  advisory-lock key (the bug at `HourlyAnalyticsRollupScheduler.cs:241`), a `tenantId` threaded into
  the dispatch, a target definition id as data not a constant, and a cron/window shape — for which
  `RotationScheduleCalculator.RegisterCronEvaluator` is a ready-made seam.

## Implementation Steps

1. **Precondition check (no code).** `dotnet build` green. Confirm in tree: `FindingsDocumentType`
   registered (`DocumentTypeRegistry.cs:30`), `IDocumentType.ValidateWithContext` present, the
   lifecycle's `validationContextJson` forwarding present (`DocumentLifecycleWorkflow.cs:338-343`),
   `RotateSecretWorkflow` present, the three `(security, audit-*)`/`review-compliance` cells and
   prompt files present. **All verified present at plan time.** Confirm no scheduler seam exists —
   and if one has since landed, revisit D8 and step 9.

2. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Documents/Types/Findings.cs`** (D2, AC2) —
   add the two nullable members to `Finding`; add `SEVERITY_REQUIRED`,
   `SEVERITY_OUT_OF_VOCABULARY`, `REMEDIATION_REQUIRED` constants; **override
   `ValidateWithContext(JsonElement, string)`**: empty/whitespace context ⇒ `Validate(payload)`
   verbatim; non-empty with `requireSeverityAndRemediation` ⇒ `Validate` then the three rules, with
   domain-phrased violations naming the offending finding title. Extend the `Contract` const
   (`:190`) with one sentence describing the optional `severity`/`remediation` pair — **shared by
   all `Findings` producers**, so word it as optional guidance that `research.md` and
   `triage-context-scan.md` remain valid without.

3. **HAND-EDIT the three prompt cells** — `apps/tamma-elsa/src/Tamma.Api/Prompts/security/audit-dependencies.md`,
   `audit-secrets.md`, `review-compliance.md`: canonical `Findings` wire (`"summary"`, `"findings"`,
   `"title"`, `"relevance"`, `"confidence"`, `"citations"`, `"overallConfidence"`) **plus**
   `"severity"` + `"remediation"`, **plus** the all-clear convention (D2). Bump `version` in each
   front matter; note each cell's declared `variables` — the binding's `feedbackVariableName` must
   name one of them (clause (e), the render-drop lesson). No 39-16 generated-region marker exists in
   any prompt file (verified), so these are hand edits.

4. **CREATE `apps/tamma-elsa/src/Tamma.Activities/Security/SecurityAuditEvents.cs`** (+ an
   `EmitSecurityAuditEventActivity` if the house per-family emitter pattern applies) — D7.

5. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/Helpers/SecurityAuditBindingHelper.cs`** —
   pure, Elsa-free, total, fail-closed:

   ```csharp
   public static class SecurityAuditBindingHelper
   {
       public static IReadOnlyList<string> ResolveLenses(string? lensesJson);        // default: all three
       public static string ScopeLensId(string repository, string windowKey, string lensWire);
       public static string BuildValidationContext();                                // D2's requireSeverityAndRemediation
       public static (int FindingCount, int HighSeverity, bool HasCriticalSecret) ReadLensFindings(string documentJson);
       public static string BuildLensDetail(LifecycleBindingHelper.LifecycleExit exit);
       public static string BuildLensResultsJson(/* accumulated rows */);
   }
   ```

6. **CREATE `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/SecurityAuditWorkflow.cs`** — the
   binding. `builder.DefinitionId = "security-audit"`,
   `[ResumeBehavior(ResumeMode.LatestStateReEntry)]` (D6). Graph:
   `ReadInputs (resolve lenses, windowKey) → EmitAuditStarted → hasMoreLenses(FlowDecision)`
   → *(True)* `extractCurrentLens → ComputeReEntryPosition(scopedLensId, "findings") → ReadPositionStage
   → DispatchLifecycle` (`document-lifecycle`, `WaitForCompletion=true`) with
   `documentType = "findings"`, `producerRole = AgentRole.Security.ToWire()`,
   `producerAction = <current lens wire>`, `producerVariablesJson` (manifest / scan surface /
   checklist per D5), a **declared** `feedbackVariableName`, `validationContextJson`
   (`BuildValidationContext`, D2), `issueId = scopedLensId`, `correlationId`, `tenantId`,
   `acceptanceRulesJson`
   `→ ReadLensExit → EmitAuditLens → criticalSecret(FlowDecision)` → *(True)*
   `DispatchRotateSecret` (`rotate-secret`, `WaitForCompletion=false`) + `EmitSecretExposed` → join;
   *(False)* join `→ incrementLens` → back to `hasMoreLenses`
   → *(False, loop done)* `EmitAuditReport → ExposeOutput`.
   **Zero `Finish`; zero `DispatchWorkflow("llm-call")`; exactly TWO `DispatchWorkflow` nodes —
   `document-lifecycle` and `rotate-secret`** (the declared, justified deviation from clause (a),
   D4.3 — state it in the story's ACs); no `ValidationErrors`/`RetryCount`/`MaxRetries`/`*Valid`
   variables.

7. **MODIFY `apps/tamma-elsa/tests/Tamma.Activities.Tests/Workflows/ContractBindingTests.cs`**
   (Correction 10) — add **three** `Bindings` entries, all with authority
   `FindingsDocumentType.Validate` and the `Findings` token groups plus `"severity"` /
   `"remediation"`:

   ```csharp
   // Story 41-20 — SecurityAuditWorkflow binds the three security audit lenses as the produce step
   // of its per-lens document-lifecycle dispatches; shape authority is
   // Tamma.Core/Documents/Types/Findings.cs (FindingsDocumentType.Validate /
   // ValidateWithContext for the severity+remediation ring).
   [("security", "audit-dependencies")] = new("FindingsDocumentType.Validate", [ … ]),
   [("security", "audit-secrets")]      = new("FindingsDocumentType.Validate", [ … ]),
   [("security", "review-compliance")]  = new("FindingsDocumentType.Validate", [ … ]),
   ```

   Then run the whole fixture: all three must be *discovered* via the lifecycle-binding walk. **Note
   a real risk here:** the walk materialises the `(role, action)` pair from the dispatch's `Input`
   delegate; this binding's `producerAction` is a **loop variable**, not a constant, so the walk may
   fail to materialise it. If so, the three pairs go into `TaxonomyDriftBuildTests`'
   `NonMaterializableSupplement` (its curated supplement, which exists precisely for this) with a
   justification — check `TaxonomyDriftBuildTests` for the supplement's sync guard before choosing.
   **Resolve this before writing the workflow**, since it may argue for three constant-pair dispatch
   nodes instead of one loop node.

8. **MODIFY `apps/tamma-elsa/src/Tamma.Core/Documents/DocumentTypeRegistry.cs`** (Correction 10,
   rule 1(f)) — add
   `new WorkflowDocumentInterface("security-audit", empty, DocumentTypeKey.Findings, false)` to
   `BuildSeed`. **MODIFY `apps/tamma-elsa/tests/Tamma.Core.Tests/Documents/WorkflowInterfaceGraphTests.cs:45`**
   — bump `HaveCount(16)` by one, with a comment naming Story 41-20.
   **MODIFY `TaxonomyDriftBuildTests.ExpectedContributingWorkflows`** — add `"SecurityAuditWorkflow"`.

9. **DO NOT WIRE A TRIGGER.** Register nothing in `Program.cs`. Record in the story that the workflow
   is dispatch-only until the seam of D8 exists, and file the seam as an epic-level blocker naming
   its seven consumers (41-5, 41-7, 41-11, 41-16, 41-17 PR sweep, 41-20, 41-23).

10. **CREATE the tests** — see Test Plan. Finish with full `dotnet test` and
    `dotnet ef migrations has-pending-model-changes` (must stay clean).

## Data & Migrations

None. `Findings` documents persist through 39-11's existing `document_instances` table; the two new
`Finding` members are additive JSON payload fields inside the existing `jsonb` payload — **no schema
change**. `SECURITY_AUDIT.*` rides the existing `TammaEventEmitter` → `EventPersistenceMiddleware` →
`EventRepository` → `domain_events` drain. `dotnet ef migrations has-pending-model-changes` stays
clean. *(A persisted last-fired-window table, if any, belongs to the scheduler seam's owning story.)*

## Events

- **Emits (new constants, `Tamma.Activities/Security/SecurityAuditEvents.cs`):**
  `SECURITY_AUDIT.STARTED` (data `windowKey`, `lenses`), `SECURITY_AUDIT.LENS` (one per lens,
  success **or** failure; data `lens`, `status`, `outcome`, `documentId`, `findingCount`,
  `highSeverityCount`), `SECURITY_AUDIT.SECRET_EXPOSED` (LOUD; data `documentId`, `findingTitle`),
  `SECURITY_AUDIT.REPORT` (terminal roll-up), `SECURITY_AUDIT.FAILED` (LOUD terminal).
  Tags: `repository`, `tenantId`, `windowKey`, `lens`, `correlationId`.
- **Emitted by the machinery this binding wires in (not by this story's code):** the `DOCUMENT.*`
  family per lens (incl. `DOCUMENT.VALIDATED.FAILED` carrying `REMEDIATION_REQUIRED` /
  `SEVERITY_OUT_OF_VOCABULARY`), `APPROVAL.*`, `ESCALATION.TRIGGERED`, and the `SECRET.ROTATION.*`
  family from the dispatched `rotate-secret` saga.

## Test Plan

NUnit + FluentAssertions (+ Moq; Testcontainers for the execution suite).

- **`FindingsSeverityRemediationTests`** (`Tamma.Core.Tests`, AC2, D2) — **the no-regression proof
  first:** `ValidateWithContext(payload, "")` is byte-identical to `Validate(payload)` for a
  `research`-shaped and a `triage-context-scan`-shaped fixture, and both still round-trip with the
  two new members absent. Then, with the `requireSeverityAndRemediation` context: a finding with no
  `severity` ⇒ `SEVERITY_REQUIRED`; `severity: "urgent"` ⇒ `SEVERITY_OUT_OF_VOCABULARY` naming the
  value; a critical finding with empty `remediation` ⇒ `REMEDIATION_REQUIRED`; a well-formed
  security finding validates. Plus the **all-clear** fixture: one finding at `severity: "none"` with
  a citation validates, and `findings: []` still fails `EMPTY_FINDINGS` (Correction 5b, pinned so
  nobody "fixes" it later). **Covers AC2.**
- **`SecurityAuditWorkflowStructureTests`** (modelled on `TaskCreationWorkflowStructureTests`) —
  thinness clauses as executable pins, with the declared deviation: exactly two `DispatchWorkflow`
  nodes, literal def ids `{document-lifecycle, rotate-secret}`; zero `llm-call` dispatches;
  `OfType<Finish>()` empty; no retry-plumbing variables; the lifecycle dispatch materialises
  `documentType == "findings"` + a declared `feedbackVariableName` + a non-empty
  `validationContextJson`; `DefinitionId == "security-audit"`; threads `TenantId`; one
  `ComputeReEntryPositionActivity`; no `Wait*`;
  `[ResumeBehavior(ResumeMode.LatestStateReEntry)]`. **Plus** a reachability pin:
  `DispatchRotateSecret` is reachable **only** from the `criticalSecret` True edge, and the per-lens
  failure edge reaches `EmitAuditLens`, never a terminal (AC1's fail-closed structure). **Covers AC1
  (fail-closed half), AC4.**
- **`SecurityAuditBindingHelperTests`** — `ResolveLenses` (default all three / explicit subset /
  malformed → default, fail-safe); `ScopeLensId` determinism + three-way collision-freedom;
  `BuildValidationContext` produces exactly the shape `FindingsDocumentType.ValidateWithContext`
  reads (**pin both sides in one test** so they cannot drift); `ReadLensFindings` on valid /
  unreadable → fail-closed zeros and `HasCriticalSecret == false`; `BuildLensDetail` names each
  reachable `DocumentLifecycleOutcome` wire + `rejected`.
- **Drift-guard runs (steps 7–8, self-verifying)** — full `ContractBindingTests` fixture green
  (three new entries; if the loop variable defeats materialisation, the supplement is used with a
  justification and its sync guard stays green); `ResumableStandardStructuralTests` green with **no**
  `SecurityAuditWorkflow` allowlist entry; `WorkflowInterfaceGraphTests` at the bumped count.
  **Covers AC4.**
- **`SecurityAuditLifecycleExecutionTests`** (Testcontainers, on the shared 39-6/39-10 fixture) —
  (a) **three-lens happy path:** dispatch with one `windowKey`; scripted valid `Findings` per lens →
  reviews approve → accepts; assert **three** accepted `Findings` documents with three distinct
  scoped ids, three `SECURITY_AUDIT.LENS` events, one `SECURITY_AUDIT.REPORT`, and that all three are
  retrievable under the shared `repository` lineage through 39-11 (the "one ranked report" read-side
  claim of D3). **Covers AC1 (per-lens half), AC2.**
  (b) **AC1 fail-closed:** the `review-compliance` lens is scripted to exhaust validation; assert its
  `SECURITY_AUDIT.LENS` is error-status with a typed detail, the other two lenses still complete, the
  run reaches `SECURITY_AUDIT.REPORT`, and **no `Finish` is reached**.
  (c) **AC3:** the `audit-secrets` lens produces a `critical`-severity exposed-secret finding →
  `SECURITY_AUDIT.SECRET_EXPOSED` emitted **and** a `rotate-secret` dispatch observed (capture the
  dispatcher); a clean secret audit produces **neither**. Plus the clamp path: a
  critical-severity **review** issue on an accepted-looking audit forces `Accept` → `Escalate`
  (`BlockingReviewViolation`) with no new code. **Covers AC3.**
  (d) **durability within a window (AC1's reachable half):** kill the process after the second
  `SECURITY_AUDIT.LENS`; re-dispatch with the **same** `windowKey`; assert exactly three accepted
  `Findings` documents exist, no lens re-produced, none dropped. **Explicitly note in the test name
  that at-most-once-per-window ACROSS the fleet is NOT covered — it needs the trigger seam (D8).**
  (e) **always-escalate-class control:** a rules JSON with
  `{"kind":"agent-action","key":"audit-secrets"}` escalates the secrets lens via
  `TryPreGate` — proving the deployment option works while the default path does not use it (D4.4).

## Definition of Done

| AC | Satisfied by step(s) | Verified by |
|---|---|---|
| 1a — per-lens fail-closed (a lens failure is recorded, not dropped) | 6 (D3) | structure test reachability pin; execution (b) |
| 1b — idempotent within a `windowKey` across a restart | 6 (D3) | execution (d) |
| 1c — **scheduled, tenant-scoped, at-most-once-per-window across the fleet** | — | **UNREACHABLE — needs the unowned scheduler seam (D8). Not claimed.** |
| 2 — findings cite evidence with severity + remediation; clean audit is a valid all-clear report | 2, 3 (D2) | `FindingsSeverityRemediationTests`; execution (a) |
| 3 — exposed-secret path escalates and integrates with `rotate-secret` | 6 (D4) | execution (c) |
| 4 — `[ResumeBehavior(LatestStateReEntry)]`; 39-10 green without allowlist | 6 (D6) | `ResumableStandardStructuralTests` |
| 4b — *(added, rule 1(f))* interface row + edge-pin bump | 8 | `WorkflowInterfaceGraphTests` |
| 4c — *(added)* three cells classified in `Bindings` with the typed authority | 7 | full `ContractBindingTests` fixture |

## Risks & Mitigations

- **AC1's headline promise cannot be delivered by this story.** Mitigation: the DoD table splits AC1
  into three and marks 1c unreachable by name; D8 records the seam's required shape and its seven
  consumers; step 9 forbids wiring a trigger. **The failure mode to guard against is a
  `HourlyAnalyticsRollupScheduler` clone**, which would reintroduce the tenant-suppression bug at
  `:241` — one tenant's leader silencing every other tenant's audit is a *security* regression here,
  not just an operational one.
- **`Findings` is a shared type with two live producers (D2).** Mitigation: both new members are
  nullable and the rules are context-gated; the first test in the suite is the byte-identical
  no-regression proof against `research`- and `triage-context-scan`-shaped fixtures; the `Contract`
  sentence is worded as optional guidance so the two sibling `ContractBindingTests` entries
  (`:91-96`, `:202-207`) stay green unchanged.
- **The loop-variable dispatch may defeat the drift walk (step 7).** Mitigation: resolve it *before*
  writing the workflow — if `ScanLifecycleBindingDispatches` cannot materialise a loop-variable
  action, switch to three constant-pair dispatch nodes (which also simplifies the structure test, at
  the cost of a larger graph). Do not silently let the pairs go undiscovered; the coverage guard
  exists to catch exactly that.
- **Two `DispatchWorkflow` nodes is a declared deviation from thinness clause (a).** Mitigation: rule
  1 permits it *if named and justified in the story's ACs*; D4.3 gives the justification (a
  side-effect dispatch on a typed value, the `DeploymentPipelineWorkflow` rollback analogue) and the
  structure test pins that it is unreachable except from the critical-severity edge. **Add the
  deviation to the story file's ACs before merging.**
- **The Epic 42 gap invites shelling out.** Mitigation: D5's first cut passes evidence in as producer
  variables with `enableTools` off, so nothing ungoverned runs; flipping tools on is a later,
  separate decision that the story's own caveat already governs.
- **Story-vs-code tensions:** Corrections 5, 6 and 7 are three genuine impossibilities in the story
  as drafted (`Findings` fields, one-document aggregation, escalation-class expressiveness). Each is
  resolved with a mechanism that exists, and the residual gaps (payload-predicate escalation classes;
  fleet-wide window idempotency) are stated, not papered over.

## Est. Effort

| Step(s) | Work | Days |
|---|---|---|
| 1 | Precondition check + resolve the step-7 materialisation question | 0.25 |
| 2 | `Findings` additive members + `ValidateWithContext` + `Contract` sentence | 0.6 |
| 3 | Three prompt-cell rewrites (canonical wire + all-clear convention) | 0.6 |
| 4–5 | `SecurityAuditEvents` + `SecurityAuditBindingHelper` | 0.5 |
| 6 | `SecurityAuditWorkflow` binding (lens loop + rotate-secret edge) | 1.0 |
| 7–8 | Three contract entries + registry row + edge-pin bump + drift-guard | 0.4 |
| 10 | `Tamma.Core.Tests` severity/remediation/all-clear suite | 0.5 |
| 10 | Structure + helper tests | 0.5 |
| 10 | Testcontainers scenarios (a)–(e) | 1.0 |
| **Total** | | **5.35** (story estimate: 5–6 days — confirmed, **for the buildable scope only**; the scheduler seam is additional and is not this story's) |

## Blocks / Blocked by

- **Blocked by (hard, for AC1c only): the tenant-aware scheduled-trigger seam — UNOWNED.** No Epic 41
  story writes it (README Wave-0 table, owner *"none — must be written"*). It also blocks **41-5**,
  **41-7**, **41-11**, **41-16**, **41-17** (PR-triage half) and **41-23** — seven consumers, one
  missing seam. Everything else in this plan ships without it.
- **Blocked by:** Epic 39 — `Findings` + `FindingsDocumentType` (39-3), `document-lifecycle` +
  `validationContextJson` forwarding (39-6/39-15), `document-review`/`review-panel` (39-7), the
  accept gate (39-8), the resume standard (39-10), the document store + lineage API (39-11).
  **All landed and verified in tree.** Also depends on `rotate-secret` (`RotateSecretWorkflow.cs:34`)
  and the Epic 29 secrets subsystem behind it — **landed** (Correction 2).
- **NOT blocked by 41-1a** — all three cells and all three prompt files exist (Correction 1).
- **NOT blocked by 41-1b or 41-1c** — `Findings` is an existing structured type; nothing prose here.
- **Degraded (not blocked) by Epic 42** — the story's caveat is accurate: the tool-using half of the
  agent path is ungoverned and unclassified until 42 lands. D5's first cut avoids the issue by
  passing evidence in as producer variables with tools off.
- **Blocks:** **41-12** (dependency & upgrade planning consumes this story's `audit-dependencies`
  `Findings`) — soft edge; 41-12 can also be triggered directly.
- **Related:** **41-21** (security incident analysis) consumes an audit `Findings` when an audit
  finding becomes an incident; the two stories share the `security` role's cells and event-family
  conventions but no files beyond the shared drift guards.
- **Shared-file register (coordinate before editing):** `Tamma.Core/Documents/Types/Findings.cs`
  (also read by 41-7, 41-11, 41-14, 41-23 — this story is the first to extend it, so land the
  additive members early and tell those stories); `ContractBindingTests.Bindings` (41-17, 41-18,
  41-19, 41-21, 41-1a); `DocumentTypeRegistry.BuildSeed` + `WorkflowInterfaceGraphTests.cs:45`
  (every producing-workflow story — serialize the bumps);
  `TaxonomyDriftBuildTests.ExpectedContributingWorkflows` (+ possibly
  `NonMaterializableSupplement`, step 7).
