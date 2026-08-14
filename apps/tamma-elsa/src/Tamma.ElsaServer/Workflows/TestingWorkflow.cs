using System.Text.Json;
using Elsa.Expressions.Models;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Activities.Flowchart.Models;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime.Activities;
using Tamma.Activities.CodeIndex;
using Tamma.Activities.Testing;
using Tamma.Activities.Testing.Models;
using Tamma.Api.Services.Agents;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Testing sub-workflow: runs the full testing/quality pipeline with skill-level-aware
/// thresholds, a timeout-enforced bookmark-based CI wait, an LLM-mediated auto-fix loop,
/// teaching feedback, a complete DCB audit trail, and a mandatory escalation terminal.
///
/// <para>Build-out (completeness audit 2026-06-22, <c>Testing.md</c> Phase 1 P0 + Phase 2
/// P1):</para>
/// <list type="bullet">
///   <item><description><b>#1/#6 — auto-fix that actually fixes.</b> The MajorIssues path
///     now dispatches <c>llm-call</c> (role=developer, action=implement-fix,
///     enableTools=true) to GENERATE the fix BEFORE <c>CommitFix</c>. A zero-files-changed
///     commit is treated as a NON-fix: it emits <c>GATE.AUTOFIX_NOOP</c> and routes to
///     escalation instead of re-triggering CI and pretending progress. No step ever calls
///     a provider directly — fix generation is mediated through <c>llm-call</c>.</description></item>
///   <item><description><b>#2 — CI-wait timeout.</b> <c>WaitForCIResultsActivity</c> now
///     arms a durable scheduled timeout bookmark alongside the result bookmark and exposes
///     a <c>Timeout</c> outcome → the workflow takes a deterministic escalation edge if CI
///     never reports (no permanent hang).</description></item>
///   <item><description><b>#3 — DCB audit trail.</b> <c>EmitTestingEventActivity</c> emits
///     <c>TEST.CI_TRIGGERED.*</c>, <c>TEST.RESULTS_RECEIVED</c>, <c>TEST.CI_TIMED_OUT</c>,
///     <c>GATE.EVALUATED</c>, <c>GATE.AUTOFIX_COMMITTED</c>/<c>NOOP</c>, <c>GATE.PASSED</c>,
///     <c>GATE.FAILED</c> and <c>GATE.ESCALATED</c> via the durable engine drain.</description></item>
///   <item><description><b>#4 — fail fast on trigger failure.</b> A <c>FlowDecision</c> on
///     <c>CITriggerResult.Success</c> after each trigger routes a failed trigger to
///     escalation instead of proceeding into a dead CI wait with <c>RunId="unknown"</c>.</description></item>
///   <item><description><b>#5 — mandatory escalation terminal.</b> Critical, retry-exhausted,
///     ci-timeout, ci-trigger-failed and autofix-noop all converge on a single escalation
///     terminal that emits <c>GATE.ESCALATED</c> with a structured reason and sets
///     <c>escalated=true</c> + <c>escalationReason</c> outputs so the parent loop can route
///     to a human gate. No infinite waits; no silent give-up.</description></item>
/// </list>
///
/// <para>Output contract (preserved, extended additively): <c>qualityReport</c> (JSON),
/// <c>passed</c> (bool), <c>teachingFeedback</c> (string) on every terminal path, plus the
/// new <c>escalated</c> (bool) + <c>escalationReason</c> (string) on the escalation path.
/// <c>passed</c> is only ever true on a real green gate result — never on a timeout, a
/// trigger failure, a no-op fix or a parse failure.</para>
///
/// <para><b>Deferred (P1 #6 — MinorIssues vs AllPass):</b> the MinorIssues outcome
/// INTENTIONALLY shares the AllPass check→report→pass path. Minor issues are
/// warning-severity and <c>passed</c> is reported truthfully (the report reflects the
/// warnings); a dedicated minor-auto-fix branch (generate→commit→re-evaluate for
/// <c>Severity==Warning</c> issues) is a follow-up, not faked here. The destructive cases
/// (MajorIssues / Critical) are the ones that gained real fix-generation + escalation.</para>
///
/// Inputs:  SessionId (Guid), Repository (string), Branch (string), SkillLevel (int),
///          ConsecutivePassCount (int), tenantId (string, optional), maxRetries (int, optional)
/// Outputs: qualityReport, passed, teachingFeedback, escalated, escalationReason
/// </summary>
public class TestingWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Testing Pipeline";
        builder.DefinitionId = "testing-pipeline";
        builder.Version = WorkflowVersions.ComputedVersion;

        // ============================================
        // Workflow Variables
        // ============================================
        var sessionIdVar = builder.WithVariable<Guid>("SessionId", default).Persisted();
        var repositoryVar = builder.WithVariable<string>("Repository", "").Persisted();
        var branchVar = builder.WithVariable<string>("Branch", "").Persisted();
        var skillLevelVar = builder.WithVariable<int>("SkillLevel", 3).Persisted();
        var consecutivePassCountVar = builder.WithVariable<int>("ConsecutivePassCount", 0).Persisted();
        var attemptNumberVar = builder.WithVariable<int>("AttemptNumber", 1).Persisted();
        var maxAttemptsVar = builder.WithVariable<int>("MaxAttempts", 3).Persisted();
        // Tenant scope (empty/single-user → platform-scope). MUST be named
        // "TenantId": TriggerCIActivity / WaitForCIResultsActivity (and
        // EventPersistenceMiddleware) resolve tenant ambiently via
        // GetVariable("TenantId") — the old name "TenantIdTag" was invisible
        // to that lookup, so the CI trigger + the DG-5 poller ran
        // platform-scoped in SaaS (Epic 31 review, F-high).
        var tenantIdVar = builder.WithVariable<string>("TenantId", "").Persisted();
        // The terminal escalation reason (set just before routing into the escalation leg).
        var escalationReasonVar = builder.WithVariable<string>("EscalationReason", "").Persisted();
        // The real underlying failure detail surfaced on an escalation (never empty).
        var escalationDetailVar = builder.WithVariable<string>("EscalationDetail", "").Persisted();

        // Result variables — activities write directly to these via Output<T>(variable)
        var ciTriggerResultVar = builder.WithVariable<CITriggerResult>("CITriggerResult", default!).Persisted();
        var ciResultsVar = builder.WithVariable<CIResultsPayload>("CIResultsPayload", default!).Persisted();
        var evaluationResultVar = builder.WithVariable<QualityGateResult>("EvaluationResult", default!).Persisted();
        var coverageResultVar = builder.WithVariable<CoverageCheckResult>("CoverageResult", default!).Persisted();
        var lintResultVar = builder.WithVariable<LintCheckResult>("LintResult", default!).Persisted();
        var securityResultVar = builder.WithVariable<SecurityCheckResult>("SecurityResult", default!).Persisted();
        var qualityReportVar = builder.WithVariable<QualityReport>("QualityReport", default!).Persisted();
        var commitFixResultVar = builder.WithVariable<CommitFixResult>("CommitFixResult", default!).Persisted();
        var ciResultsFromWaitVar = builder.WithVariable<CIResultsPayload>("CIResultsFromWait", default!).Persisted();
        var fixDispatchResultVar = builder.WithVariable<IDictionary<string, object>?>().Persisted();

        // ============================================
        // Step 0: Initialize — read optional maxRetries + tenantId input
        // ============================================
        var initInputs = new SetVariable
        {
            Id = "InitTestingInputs",
            Name = "Init Inputs",
            Variable = maxAttemptsVar,
            Value = new Input<object?>(ctx =>
            {
                // 2026-08-13 (engine-driven E2E run 36): capture the CALLER'S
                // SessionId/Repository/Branch/SkillLevel inputs — every dispatcher
                // (TddWorkflow, CiWithDebugRetry, Debugging, Mentorship) passes
                // them, but nothing ever read them, so the variables kept their
                // defaults: the CI wait suspended with repository="" and the DG-5
                // /elsa/api/ci/waits listing (fail-closed on a blank repository)
                // silently HID the wait — no CI seat could ever resume it and
                // every TDD leg timed out.
                var sid = ctx.GetInput<Guid>("SessionId");
                if (sid != Guid.Empty) sessionIdVar.Set(ctx, sid);
                var repo = ctx.GetInput<string>("Repository");
                if (!string.IsNullOrWhiteSpace(repo)) repositoryVar.Set(ctx, repo);
                var branch = ctx.GetInput<string>("Branch");
                if (!string.IsNullOrWhiteSpace(branch)) branchVar.Set(ctx, branch);
                var skill = ctx.GetInput<int?>("SkillLevel");
                if (skill is > 0) skillLevelVar.Set(ctx, skill.Value);

                // Best-effort tenant tag (callers may not supply it; single-user → empty).
                var tenant = ctx.GetInput<string>("tenantId");
                if (!string.IsNullOrWhiteSpace(tenant)) tenantIdVar.Set(ctx, tenant);

                var inputMaxRetries = ctx.GetInput<int?>("maxRetries");
                return (object)(inputMaxRetries ?? maxAttemptsVar.Get(ctx));
            })
        };
        initInputs.SetDisplayText("Init Inputs");

        // ============================================
        // Step 1: Trigger CI Pipeline
        // ============================================
        var triggerCI = new TriggerCIActivity
        {
            Id = "TriggerCI",
            Name = "Trigger CI Pipeline",
            SessionId = new(ctx => sessionIdVar.Get(ctx)),
            Repository = new(ctx => repositoryVar.Get(ctx)),
            Branch = new(ctx => branchVar.Get(ctx)),
            Result = new(ciTriggerResultVar)
        };
        triggerCI.SetDisplayText("Trigger CI Pipeline");

        var emitCiTriggered = EmitEvent("EmitCiTriggered", TestingEvents.CiTriggeredSuccess,
            sessionIdVar, repositoryVar, branchVar, tenantIdVar,
            runId: ctx => ciTriggerResultVar.Get(ctx)?.RunId ?? "",
            attempt: ctx => attemptNumberVar.Get(ctx),
            maxAttempts: ctx => maxAttemptsVar.Get(ctx));

        // #4 — fail fast on trigger failure: gate on CITriggerResult.Success.
        var triggerSucceeded = new FlowDecision(ctx => ciTriggerResultVar.Get(ctx)?.Success == true)
        { Id = "TriggerSucceeded", Name = "CI Trigger Succeeded?" };
        triggerSucceeded.SetDisplayText("CI Trigger Succeeded?");

        // Epic 31 P3 (§4.3 safety net) — the trigger's typed capability_unsupported
        // outcome routes to a DISTINCT terminal (passed=false, ciUnsupported=true)
        // that ci-with-debug-retry propagates upward WITHOUT burning debug retries:
        // an unsupported platform will answer identically on every retry. The
        // cycle-level alternative step (CI.WORKFLOW_DISPATCH.SKIPPED → human
        // merge-approval path) owns the audit event + routing.
        var triggerUnsupported = new FlowDecision(ctx => ciTriggerResultVar.Get(ctx)?.Unsupported == true)
        { Id = "TriggerUnsupported", Name = "CI Dispatch Unsupported?" };
        triggerUnsupported.SetDisplayText("CI Dispatch Unsupported?");

        // ============================================
        // Step 2: Wait for CI results (bookmark + durable timeout)
        // ============================================
        var waitForCI = new WaitForCIResultsActivity
        {
            Id = "WaitForCIResults",
            Name = "Wait for CI Results",
            SessionId = new(ctx => sessionIdVar.Get(ctx)),
            RunId = new(ctx => ciTriggerResultVar.Get(ctx)?.RunId ?? "unknown"),
            // DG-5 — the poller needs the repo on the bookmark payload.
            Repository = new(ctx => (string?)repositoryVar.Get(ctx)),
            TimeoutMinutes = new(30),
            Results = new(ciResultsFromWaitVar)
        };
        waitForCI.SetDisplayText("Wait for CI Results");

        // Store CI results into the shared variable via Inline
        var storeCIResults = new SetVariable
        {
            Id = "StoreCIResults",
            Name = "Store CI Results",
            Variable = ciResultsVar,
            Value = new(ctx => (object?)ciResultsFromWaitVar.Get(ctx))
        };
        storeCIResults.SetDisplayText("Store CI Results");

        var emitResultsReceived = EmitEvent("EmitResultsReceived", TestingEvents.ResultsReceived,
            sessionIdVar, repositoryVar, branchVar, tenantIdVar,
            runId: ctx => ciResultsVar.Get(ctx)?.RunId ?? "",
            attempt: ctx => attemptNumberVar.Get(ctx),
            maxAttempts: ctx => maxAttemptsVar.Get(ctx),
            configure: (a, _) =>
            {
                a.ErrorDetail = new Input<string?>(ctx =>
                {
                    var r = ciResultsVar.Get(ctx);
                    return r == null ? null : $"build={r.BuildPassed}, failedTests={r.FailedTests}/{r.TotalTests}";
                });
            });

        // ============================================
        // Step 3: Evaluate results with outcome-based routing
        // ============================================
        var evaluateResults = new EvaluateResultsActivity
        {
            Id = "EvaluateResults",
            Name = "Evaluate CI Results",
            CIResults = new(ctx => ciResultsVar.Get(ctx)!),
            SkillLevel = new(ctx => skillLevelVar.Get(ctx)),
            ConsecutivePassCount = new(ctx => consecutivePassCountVar.Get(ctx)),
            EvaluationResult = new(evaluationResultVar)
        };
        evaluateResults.SetDisplayText("Evaluate CI Results");

        var emitGateEvaluated = EmitGateEvaluated("EmitGateEvaluated",
            sessionIdVar, repositoryVar, branchVar, tenantIdVar, ciResultsVar,
            evaluationResultVar, attemptNumberVar, maxAttemptsVar, skillLevelVar);
        // Separate GATE.EVALUATED emit for the MajorIssues branch so it can fan out to the
        // auto-fix guard WITHOUT the pass-path emit also routing into both legs (each
        // flowchart activity owns exactly one outbound edge set).
        var emitGateEvaluatedMajor = EmitGateEvaluated("EmitGateEvaluatedMajor",
            sessionIdVar, repositoryVar, branchVar, tenantIdVar, ciResultsVar,
            evaluationResultVar, attemptNumberVar, maxAttemptsVar, skillLevelVar);

        // ============================================
        // AllPass/MinorIssues path: detailed checks
        // ============================================
        var checkCoverage = MakeCoverage("CheckCoverage", "Check Code Coverage", ciResultsVar, skillLevelVar, coverageResultVar);
        var checkLinting = MakeLint("CheckLinting", "Check Linting Rules", ciResultsVar, skillLevelVar, lintResultVar);
        var checkSecurity = MakeSecurity("CheckSecurity", "Check Security Issues", ciResultsVar, skillLevelVar, securityResultVar);
        var generateReport = MakeReport("GenerateQualityReport", "Generate Quality Report",
            sessionIdVar, ciResultsVar, coverageResultVar, lintResultVar, securityResultVar, skillLevelVar, consecutivePassCountVar, qualityReportVar);

        var emitGatePassed = EmitEvent("EmitGatePassed", TestingEvents.GatePassed,
            sessionIdVar, repositoryVar, branchVar, tenantIdVar,
            runId: ctx => ciResultsVar.Get(ctx)?.RunId ?? "",
            attempt: ctx => attemptNumberVar.Get(ctx),
            maxAttempts: ctx => maxAttemptsVar.Get(ctx),
            configure: (a, _) =>
            {
                a.Score = new Input<double>(ctx => qualityReportVar.Get(ctx)?.OverallScore ?? 0);
                a.SkillLevel = new Input<int>(ctx => skillLevelVar.Get(ctx));
            });

        // ============================================
        // Critical path: same checks, then escalate (no false pass)
        // ============================================
        var checkCoverageCritical = MakeCoverage("CheckCoverageCritical", "Check Coverage (Critical)", ciResultsVar, skillLevelVar, coverageResultVar);
        var checkLintCritical = MakeLint("CheckLintCritical", "Check Lint (Critical)", ciResultsVar, skillLevelVar, lintResultVar);
        var checkSecurityCritical = MakeSecurity("CheckSecurityCritical", "Check Security (Critical)", ciResultsVar, skillLevelVar, securityResultVar);
        var generateReportCritical = MakeReport("GenerateQualityReportCritical", "Generate Report (Critical)",
            sessionIdVar, ciResultsVar, coverageResultVar, lintResultVar, securityResultVar, skillLevelVar, consecutivePassCountVar, qualityReportVar);

        var setReasonCritical = new SetVariable
        {
            Id = "SetReasonCritical", Name = "Set Reason: Critical",
            Variable = escalationReasonVar,
            Value = new Input<object?>(_ => (object)TestingEvents.ReasonCritical)
        };
        setReasonCritical.SetDisplayText("Set Reason: Critical");
        var setDetailCritical = new SetVariable
        {
            Id = "SetDetailCritical", Name = "Set Detail: Critical",
            Variable = escalationDetailVar,
            Value = new Input<object?>(ctx => (object)(evaluationResultVar.Get(ctx)?.Summary ?? "Critical quality-gate failure"))
        };
        setDetailCritical.SetDisplayText("Set Detail: Critical");

        // ============================================
        // MajorIssues path: LLM-mediated GENERATE FIX -> commit -> verify changes
        // ============================================
        var maxAttemptGuard = new FlowDecision(ctx =>
            attemptNumberVar.Get(ctx) < maxAttemptsVar.Get(ctx))
        { Id = "MaxAttemptGuard", Name = "Fix Attempts Remaining?" };
        maxAttemptGuard.SetDisplayText("Fix Attempts Remaining?");

        // #1 — GENERATE the fix via the mediated llm-call sub-workflow BEFORE committing.
        // A step NEVER calls a provider directly; fix generation is routed through llm-call.
        var generateFix = new DispatchWorkflow
        {
            Id = "GenerateFix",
            Name = "Generate Fix (llm-call)",
            WorkflowDefinitionId = new("llm-call"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["role"] = AgentRole.Developer.ToWire(),
                ["action"] = AgentAction.ImplementFix.ToWire(),
                ["tenantId"] = tenantIdVar.Get(ctx),
                ["variables"] = new Dictionary<string, object>
                {
                    ["repository"] = repositoryVar.Get(ctx),
                    ["branch"] = branchVar.Get(ctx),
                    ["attemptNumber"] = attemptNumberVar.Get(ctx),
                    ["issuesJson"] = SerializeAutoFixableIssues(evaluationResultVar, ctx),
                    ["evaluationSummary"] = evaluationResultVar.Get(ctx)?.Summary ?? "",
                },
                ["enableTools"] = true,
            }),
            WaitForCompletion = new(true),
            Result = new(fixDispatchResultVar)
        };
        generateFix.SetDisplayText("Generate Fix (llm-call)");

        var commitFix = new CommitFixActivity
        {
            Id = "CommitFix",
            Name = "Commit Auto-Fix",
            SessionId = new(ctx => sessionIdVar.Get(ctx)),
            Repository = new(ctx => repositoryVar.Get(ctx)),
            Branch = new(ctx => branchVar.Get(ctx)),
            FixedIssues = new(ctx => evaluationResultVar.Get(ctx)?.Issues
                .Where(i => i.AutoFixable).ToList() ?? new List<QualityIssue>()),
            AttemptNumber = new(ctx => attemptNumberVar.Get(ctx)),
            MaxAttempts = new(ctx => maxAttemptsVar.Get(ctx)),
            FixDescription = new(ctx => GetFixSummary(fixDispatchResultVar.Get(ctx))),
            Result = new(commitFixResultVar)
        };
        commitFix.SetDisplayText("Commit Auto-Fix");

        // #1/#6 — treat a zero-files-changed commit as a NON-fix: do not loop pretending
        // progress. Only a commit that actually changed files continues the loop.
        var commitMadeChanges = new FlowDecision(ctx =>
        {
            var r = commitFixResultVar.Get(ctx);
            return r is { Success: true, FilesChanged: > 0 };
        })
        { Id = "CommitMadeChanges", Name = "Fix Changed Files?" };
        commitMadeChanges.SetDisplayText("Fix Changed Files?");

        var emitAutofixCommitted = EmitEvent("EmitAutofixCommitted", TestingEvents.AutofixCommitted,
            sessionIdVar, repositoryVar, branchVar, tenantIdVar,
            runId: ctx => ciResultsVar.Get(ctx)?.RunId ?? "",
            attempt: ctx => attemptNumberVar.Get(ctx),
            maxAttempts: ctx => maxAttemptsVar.Get(ctx),
            configure: (a, _) =>
            {
                a.FilesChanged = new Input<int>(ctx => commitFixResultVar.Get(ctx)?.FilesChanged ?? 0);
            });

        var emitAutofixNoop = EmitEvent("EmitAutofixNoop", TestingEvents.AutofixNoop,
            sessionIdVar, repositoryVar, branchVar, tenantIdVar,
            runId: ctx => ciResultsVar.Get(ctx)?.RunId ?? "",
            attempt: ctx => attemptNumberVar.Get(ctx),
            maxAttempts: ctx => maxAttemptsVar.Get(ctx),
            configure: (a, _) =>
            {
                a.FilesChanged = new Input<int>(ctx => commitFixResultVar.Get(ctx)?.FilesChanged ?? 0);
                a.EscalationReason = new Input<string?>(_ => TestingEvents.ReasonAutofixNoop);
                a.ErrorDetail = new Input<string?>(_ => "Auto-fix commit changed no files — the generated fix made no real change");
            });

        var setReasonNoop = new SetVariable
        {
            Id = "SetReasonNoop", Name = "Set Reason: Autofix No-op",
            Variable = escalationReasonVar,
            Value = new Input<object?>(_ => (object)TestingEvents.ReasonAutofixNoop)
        };
        setReasonNoop.SetDisplayText("Set Reason: Autofix No-op");
        var setDetailNoop = new SetVariable
        {
            Id = "SetDetailNoop", Name = "Set Detail: Autofix No-op",
            Variable = escalationDetailVar,
            Value = new Input<object?>(_ => (object)"Auto-fix produced no file changes; cannot make progress")
        };
        setDetailNoop.SetDisplayText("Set Detail: Autofix No-op");

        var updateCodeIndex = new UpdateCodeIndexActivity
        {
            Id = "UpdateCodeIndex",
            Name = "Update Code Index",
            ChangedFilesJson = new Input<string?>(ctx => (string?)null),
            RepositoryPath = new Input<string?>(ctx => repositoryVar.Get(ctx))
        };
        updateCodeIndex.SetDisplayText("Update Code Index");

        var incrementAttempt = new SetVariable<int>(attemptNumberVar, ctx =>
            attemptNumberVar.Get(ctx) + 1)
        {
            Id = "IncrementAttempt",
            Name = "Increment Fix Attempt"
        };
        incrementAttempt.SetDisplayText("Increment Fix Attempt");

        var reTriggerCI = new TriggerCIActivity
        {
            Id = "ReTriggerCI",
            Name = "Re-Trigger CI After Fix",
            SessionId = new(ctx => sessionIdVar.Get(ctx)),
            Repository = new(ctx => repositoryVar.Get(ctx)),
            Branch = new(ctx => branchVar.Get(ctx)),
            Result = new(ciTriggerResultVar)
        };
        reTriggerCI.SetDisplayText("Re-Trigger CI After Fix");

        var emitReCiTriggered = EmitEvent("EmitReCiTriggered", TestingEvents.CiTriggeredSuccess,
            sessionIdVar, repositoryVar, branchVar, tenantIdVar,
            runId: ctx => ciTriggerResultVar.Get(ctx)?.RunId ?? "",
            attempt: ctx => attemptNumberVar.Get(ctx),
            maxAttempts: ctx => maxAttemptsVar.Get(ctx));

        var reTriggerSucceeded = new FlowDecision(ctx => ciTriggerResultVar.Get(ctx)?.Success == true)
        { Id = "ReTriggerSucceeded", Name = "Re-Trigger Succeeded?" };
        reTriggerSucceeded.SetDisplayText("Re-Trigger Succeeded?");

        var waitForCIRetry = new WaitForCIResultsActivity
        {
            Id = "WaitForCIResultsRetry",
            Name = "Wait for CI Results (Retry)",
            SessionId = new(ctx => sessionIdVar.Get(ctx)),
            RunId = new(ctx => ciTriggerResultVar.Get(ctx)?.RunId ?? "unknown"),
            // DG-5 — the poller needs the repo on the bookmark payload.
            Repository = new(ctx => (string?)repositoryVar.Get(ctx)),
            TimeoutMinutes = new(30),
            Results = new(ciResultsFromWaitVar)
        };
        waitForCIRetry.SetDisplayText("Wait for CI Results (Retry)");

        var storeRetryResults = new SetVariable
        {
            Id = "StoreRetryResults",
            Name = "Store Retry CI Results",
            Variable = ciResultsVar,
            Value = new(ctx => (object?)ciResultsFromWaitVar.Get(ctx))
        };
        storeRetryResults.SetDisplayText("Store Retry CI Results");

        var emitRetryResultsReceived = EmitEvent("EmitRetryResultsReceived", TestingEvents.ResultsReceived,
            sessionIdVar, repositoryVar, branchVar, tenantIdVar,
            runId: ctx => ciResultsVar.Get(ctx)?.RunId ?? "",
            attempt: ctx => attemptNumberVar.Get(ctx),
            maxAttempts: ctx => maxAttemptsVar.Get(ctx));

        var evaluateRetryResults = new EvaluateResultsActivity
        {
            Id = "EvaluateRetryResults",
            Name = "Evaluate Retry Results",
            CIResults = new(ctx => ciResultsVar.Get(ctx)!),
            SkillLevel = new(ctx => skillLevelVar.Get(ctx)),
            ConsecutivePassCount = new(ctx => consecutivePassCountVar.Get(ctx)),
            EvaluationResult = new(evaluationResultVar)
        };
        evaluateRetryResults.SetDisplayText("Evaluate Retry Results");

        var emitRetryGateEvaluated = EmitGateEvaluated("EmitRetryGateEvaluated",
            sessionIdVar, repositoryVar, branchVar, tenantIdVar, ciResultsVar,
            evaluationResultVar, attemptNumberVar, maxAttemptsVar, skillLevelVar);
        var emitRetryGateEvaluatedMajor = EmitGateEvaluated("EmitRetryGateEvaluatedMajor",
            sessionIdVar, repositoryVar, branchVar, tenantIdVar, ciResultsVar,
            evaluationResultVar, attemptNumberVar, maxAttemptsVar, skillLevelVar);

        // Retry pass path: detailed checks
        var checkCoverageRetry = MakeCoverage("CheckCoverageRetry", "Check Coverage (Retry)", ciResultsVar, skillLevelVar, coverageResultVar);
        var checkLintRetry = MakeLint("CheckLintRetry", "Check Lint (Retry)", ciResultsVar, skillLevelVar, lintResultVar);
        var checkSecurityRetry = MakeSecurity("CheckSecurityRetry", "Check Security (Retry)", ciResultsVar, skillLevelVar, securityResultVar);
        var generateRetryReport = MakeReport("GenerateQualityReportRetry", "Generate Report (Retry)",
            sessionIdVar, ciResultsVar, coverageResultVar, lintResultVar, securityResultVar, skillLevelVar, consecutivePassCountVar, qualityReportVar);

        var emitRetryGatePassed = EmitEvent("EmitRetryGatePassed", TestingEvents.GatePassed,
            sessionIdVar, repositoryVar, branchVar, tenantIdVar,
            runId: ctx => ciResultsVar.Get(ctx)?.RunId ?? "",
            attempt: ctx => attemptNumberVar.Get(ctx),
            maxAttempts: ctx => maxAttemptsVar.Get(ctx),
            configure: (a, _) =>
            {
                a.Score = new Input<double>(ctx => qualityReportVar.Get(ctx)?.OverallScore ?? 0);
                a.SkillLevel = new Input<int>(ctx => skillLevelVar.Get(ctx));
            });

        // Retry Critical / exhaustion escalation reason setters
        var setReasonRetryCritical = new SetVariable
        {
            Id = "SetReasonRetryCritical", Name = "Set Reason: Retry Critical",
            Variable = escalationReasonVar,
            Value = new Input<object?>(_ => (object)TestingEvents.ReasonCritical)
        };
        setReasonRetryCritical.SetDisplayText("Set Reason: Retry Critical");
        var setDetailRetryCritical = new SetVariable
        {
            Id = "SetDetailRetryCritical", Name = "Set Detail: Retry Critical",
            Variable = escalationDetailVar,
            Value = new Input<object?>(ctx => (object)(evaluationResultVar.Get(ctx)?.Summary ?? "Critical quality-gate failure on retry"))
        };
        setDetailRetryCritical.SetDisplayText("Set Detail: Retry Critical");

        var setReasonExhausted = new SetVariable
        {
            Id = "SetReasonExhausted", Name = "Set Reason: Retry Exhausted",
            Variable = escalationReasonVar,
            Value = new Input<object?>(_ => (object)TestingEvents.ReasonRetryExhausted)
        };
        setReasonExhausted.SetDisplayText("Set Reason: Retry Exhausted");
        var setDetailExhausted = new SetVariable
        {
            Id = "SetDetailExhausted", Name = "Set Detail: Retry Exhausted",
            Variable = escalationDetailVar,
            Value = new Input<object?>(ctx => (object)$"Auto-fix did not converge after {attemptNumberVar.Get(ctx)}/{maxAttemptsVar.Get(ctx)} attempt(s): {evaluationResultVar.Get(ctx)?.Summary ?? "major issues remain"}")
        };
        setDetailExhausted.SetDisplayText("Set Detail: Retry Exhausted");

        // ci-timeout / ci-trigger-failed reason setters
        var setReasonTimeout = new SetVariable
        {
            Id = "SetReasonTimeout", Name = "Set Reason: CI Timeout",
            Variable = escalationReasonVar,
            Value = new Input<object?>(_ => (object)TestingEvents.ReasonCiTimeout)
        };
        setReasonTimeout.SetDisplayText("Set Reason: CI Timeout");
        var setDetailTimeout = new SetVariable
        {
            Id = "SetDetailTimeout", Name = "Set Detail: CI Timeout",
            Variable = escalationDetailVar,
            Value = new Input<object?>(_ => (object)"CI did not report results before the timeout deadline")
        };
        setDetailTimeout.SetDisplayText("Set Detail: CI Timeout");

        var emitCiTimedOut = EmitEvent("EmitCiTimedOut", TestingEvents.CiTimedOut,
            sessionIdVar, repositoryVar, branchVar, tenantIdVar,
            runId: ctx => ciTriggerResultVar.Get(ctx)?.RunId ?? "",
            attempt: ctx => attemptNumberVar.Get(ctx),
            maxAttempts: ctx => maxAttemptsVar.Get(ctx),
            configure: (a, _) =>
            {
                a.ErrorDetail = new Input<string?>(_ => "CI wait timed out");
            });

        var setReasonTriggerFailed = new SetVariable
        {
            Id = "SetReasonTriggerFailed", Name = "Set Reason: Trigger Failed",
            Variable = escalationReasonVar,
            Value = new Input<object?>(_ => (object)TestingEvents.ReasonCiTriggerFailed)
        };
        setReasonTriggerFailed.SetDisplayText("Set Reason: Trigger Failed");
        var setDetailTriggerFailed = new SetVariable
        {
            Id = "SetDetailTriggerFailed", Name = "Set Detail: Trigger Failed",
            Variable = escalationDetailVar,
            Value = new Input<object?>(ctx => (object)(ciTriggerResultVar.Get(ctx)?.Error ?? "CI trigger failed"))
        };
        setDetailTriggerFailed.SetDisplayText("Set Detail: Trigger Failed");

        var emitCiTriggerFailed = EmitEvent("EmitCiTriggerFailed", TestingEvents.CiTriggeredFailed,
            sessionIdVar, repositoryVar, branchVar, tenantIdVar,
            runId: ctx => ciTriggerResultVar.Get(ctx)?.RunId ?? "",
            attempt: ctx => attemptNumberVar.Get(ctx),
            maxAttempts: ctx => maxAttemptsVar.Get(ctx),
            configure: (a, _) =>
            {
                a.EscalationReason = new Input<string?>(_ => TestingEvents.ReasonCiTriggerFailed);
                a.ErrorDetail = new Input<string?>(ctx => ciTriggerResultVar.Get(ctx)?.Error ?? "CI trigger failed");
            });

        // ============================================
        // ESCALATION TERMINAL — mandatory after the bounded retry budget / on any hard
        // failure. Emits GATE.ESCALATED + sets escalated/escalationReason outputs so the
        // parent loop can route to a human gate. LOUD, never a silent give-up.
        // ============================================
        var emitGateEscalated = new EmitTestingEventActivity
        {
            Id = "EmitGateEscalated", Name = "Emit GATE.ESCALATED",
            EventType = new Input<string>(_ => TestingEvents.GateEscalated),
            SessionId = new Input<string?>(ctx => sessionIdVar.Get(ctx).ToString()),
            Repository = new Input<string?>(ctx => repositoryVar.Get(ctx)),
            Branch = new Input<string?>(ctx => branchVar.Get(ctx)),
            RunId = new Input<string?>(ctx => ciResultsVar.Get(ctx)?.RunId ?? ""),
            TenantId = new Input<string?>(ctx => tenantIdVar.Get(ctx)),
            Attempt = new Input<int>(ctx => attemptNumberVar.Get(ctx)),
            MaxAttempts = new Input<int>(ctx => maxAttemptsVar.Get(ctx)),
            Outcome = new Input<string?>(ctx => evaluationResultVar.Get(ctx)?.Outcome.ToString()),
            EscalationReason = new Input<string?>(ctx => escalationReasonVar.Get(ctx)),
            ErrorDetail = new Input<string?>(ctx => escalationDetailVar.Get(ctx)),
        };
        emitGateEscalated.SetDisplayText("Emit GATE.ESCALATED");

        // ============================================
        // SetOutput activities (expose workflow outputs before each Finish)
        // ============================================
        var setOutputPassReport = MakeOutput("SetOutputPassReport", "qualityReport",
            ctx => (object)JsonSerializer.Serialize(qualityReportVar.Get(ctx)));
        var setOutputPassPassed = MakeOutput("SetOutputPassPassed", "passed",
            ctx => (object)(qualityReportVar.Get(ctx)?.Passed ?? true));
        var setOutputPassFeedback = MakeOutput("SetOutputPassFeedback", "teachingFeedback",
            ctx => (object)(qualityReportVar.Get(ctx)?.TeachingFeedback ?? ""));
        var setOutputPassEscalated = MakeOutput("SetOutputPassEscalated", "escalated", _ => (object)false);
        var setOutputPassReason = MakeOutput("SetOutputPassReason", "escalationReason", _ => (object)"");

        var setOutputRetryPassReport = MakeOutput("SetOutputRetryPassReport", "qualityReport",
            ctx => (object)JsonSerializer.Serialize(qualityReportVar.Get(ctx)));
        var setOutputRetryPassPassed = MakeOutput("SetOutputRetryPassPassed", "passed",
            ctx => (object)(qualityReportVar.Get(ctx)?.Passed ?? true));
        var setOutputRetryPassFeedback = MakeOutput("SetOutputRetryPassFeedback", "teachingFeedback",
            ctx => (object)(qualityReportVar.Get(ctx)?.TeachingFeedback ?? ""));
        var setOutputRetryPassEscalated = MakeOutput("SetOutputRetryPassEscalated", "escalated", _ => (object)false);
        var setOutputRetryPassReason = MakeOutput("SetOutputRetryPassReason", "escalationReason", _ => (object)"");

        // Escalation/fail outputs — passed is ALWAYS false here (no false success).
        var setOutputFailReport = MakeOutput("SetOutputFailReport", "qualityReport",
            ctx => (object)JsonSerializer.Serialize(qualityReportVar.Get(ctx)));
        var setOutputFailPassed = MakeOutput("SetOutputFailPassed", "passed", _ => (object)false);
        var setOutputFailFeedback = MakeOutput("SetOutputFailFeedback", "teachingFeedback",
            ctx => (object)(qualityReportVar.Get(ctx)?.TeachingFeedback
                ?? $"Quality gate escalated to human review: {escalationDetailVar.Get(ctx)}"));
        var setOutputFailEscalated = MakeOutput("SetOutputFailEscalated", "escalated", _ => (object)true);
        var setOutputFailReason = MakeOutput("SetOutputFailReason", "escalationReason",
            ctx => (object)escalationReasonVar.Get(ctx));

        // ============================================
        // Finish activities
        // ============================================
        var finishPass = new Finish { Id = "FinishPass", Name = "Complete: Tests Passed" };
        finishPass.SetDisplayText("Complete: Tests Passed");
        var finishFail = new Finish { Id = "FinishFail", Name = "Complete: Escalated" };
        finishFail.SetDisplayText("Complete: Escalated");
        var finishRetryPass = new Finish { Id = "FinishRetryPass", Name = "Complete: Tests Passed After Retry" };
        finishRetryPass.SetDisplayText("Complete: Tests Passed After Retry");

        // Epic 31 P3 — the CI-unsupported terminal (never a pass, never an
        // escalation: the parent routes it to the §4 alternative step).
        var setOutputUnsupportedPassed = MakeOutput("SetOutputUnsupportedPassed", "passed", _ => (object)false);
        var setOutputUnsupportedFlag = MakeOutput("SetOutputUnsupportedFlag", "ciUnsupported", _ => (object)true);
        var setOutputUnsupportedEscalated = MakeOutput("SetOutputUnsupportedEscalated", "escalated", _ => (object)false);
        var setOutputUnsupportedReason = MakeOutput("SetOutputUnsupportedReason", "escalationReason",
            ctx => (object)(ciTriggerResultVar.Get(ctx)?.Error ?? "capability_unsupported: the platform cannot dispatch CI"));
        var finishUnsupported = new Finish { Id = "FinishCiUnsupported", Name = "Complete: CI Unsupported" };
        finishUnsupported.SetDisplayText("Complete: CI Unsupported");

        // ============================================
        // Build the Flowchart
        // ============================================
        var flowchart = new Flowchart { Id = "TestingPipelineFlowchart", Name = "Testing Pipeline Flowchart" };
        flowchart.SetDisplayText("Testing Pipeline Flowchart");

        var allActivities = new IActivity[]
        {
            initInputs,
            triggerCI, emitCiTriggered, triggerSucceeded,
            waitForCI, storeCIResults, emitResultsReceived, evaluateResults, emitGateEvaluated, emitGateEvaluatedMajor,
            // AllPass/MinorIssues pass path
            checkCoverage, checkLinting, checkSecurity, generateReport, emitGatePassed,
            setOutputPassReport, setOutputPassPassed, setOutputPassFeedback, setOutputPassEscalated, setOutputPassReason, finishPass,
            // Critical path
            checkCoverageCritical, checkLintCritical, checkSecurityCritical, generateReportCritical,
            setReasonCritical, setDetailCritical,
            // MajorIssues auto-fix loop
            maxAttemptGuard, generateFix, commitFix, commitMadeChanges, emitAutofixCommitted, emitAutofixNoop,
            setReasonNoop, setDetailNoop,
            updateCodeIndex, incrementAttempt, reTriggerCI, emitReCiTriggered, reTriggerSucceeded,
            waitForCIRetry, storeRetryResults, emitRetryResultsReceived, evaluateRetryResults, emitRetryGateEvaluated, emitRetryGateEvaluatedMajor,
            // Retry pass path
            checkCoverageRetry, checkLintRetry, checkSecurityRetry, generateRetryReport, emitRetryGatePassed,
            setOutputRetryPassReport, setOutputRetryPassPassed, setOutputRetryPassFeedback, setOutputRetryPassEscalated, setOutputRetryPassReason, finishRetryPass,
            // Escalation reason setters + terminal
            setReasonRetryCritical, setDetailRetryCritical, setReasonExhausted, setDetailExhausted,
            setReasonTimeout, setDetailTimeout, emitCiTimedOut,
            setReasonTriggerFailed, setDetailTriggerFailed, emitCiTriggerFailed,
            triggerUnsupported, setOutputUnsupportedPassed, setOutputUnsupportedFlag,
            setOutputUnsupportedEscalated, setOutputUnsupportedReason, finishUnsupported,
            emitGateEscalated,
            setOutputFailReport, setOutputFailPassed, setOutputFailFeedback, setOutputFailEscalated, setOutputFailReason, finishFail,
        };

        foreach (var activity in allActivities)
            flowchart.Activities.Add(activity);

        // ============================================
        // Wire connections
        // ============================================

        // Init -> Trigger -> emit -> trigger gate
        Connect(flowchart, initInputs, triggerCI);
        Connect(flowchart, triggerCI, emitCiTriggered);
        Connect(flowchart, emitCiTriggered, triggerSucceeded);
        // Trigger failed -> §4.3 typed-unsupported check FIRST (exact-code
        // match set by TriggerCIActivity), then the ordinary escalate chain.
        Connect(flowchart, triggerSucceeded, triggerUnsupported, "False");
        Connect(flowchart, triggerUnsupported, setOutputUnsupportedPassed, "True");
        Connect(flowchart, setOutputUnsupportedPassed, setOutputUnsupportedFlag);
        Connect(flowchart, setOutputUnsupportedFlag, setOutputUnsupportedEscalated);
        Connect(flowchart, setOutputUnsupportedEscalated, setOutputUnsupportedReason);
        Connect(flowchart, setOutputUnsupportedReason, finishUnsupported);
        Connect(flowchart, triggerUnsupported, setReasonTriggerFailed, "False");
        Connect(flowchart, setReasonTriggerFailed, setDetailTriggerFailed);
        Connect(flowchart, setDetailTriggerFailed, emitCiTriggerFailed);
        Connect(flowchart, emitCiTriggerFailed, emitGateEscalated);
        // Trigger ok -> wait
        Connect(flowchart, triggerSucceeded, waitForCI, "True");

        // Wait Received -> store -> emit -> evaluate -> emit gate
        Connect(flowchart, waitForCI, storeCIResults, "Received");
        Connect(flowchart, storeCIResults, emitResultsReceived);
        Connect(flowchart, emitResultsReceived, evaluateResults);
        Connect(flowchart, evaluateResults, emitGateEvaluated, "AllPass");
        Connect(flowchart, evaluateResults, emitGateEvaluated, "MinorIssues");
        // Wait Timeout -> escalate (ci-timeout)
        Connect(flowchart, waitForCI, setReasonTimeout, "Timeout");
        Connect(flowchart, setReasonTimeout, setDetailTimeout);
        Connect(flowchart, setDetailTimeout, emitCiTimedOut);
        Connect(flowchart, emitCiTimedOut, emitGateEscalated);

        // AllPass/MinorIssues -> checks -> report -> emit PASSED -> outputs -> finish
        Connect(flowchart, emitGateEvaluated, checkCoverage);
        Connect(flowchart, checkCoverage, checkLinting);
        Connect(flowchart, checkLinting, checkSecurity);
        Connect(flowchart, checkSecurity, generateReport);
        Connect(flowchart, generateReport, emitGatePassed);
        Connect(flowchart, emitGatePassed, setOutputPassReport);
        Connect(flowchart, setOutputPassReport, setOutputPassPassed);
        Connect(flowchart, setOutputPassPassed, setOutputPassFeedback);
        Connect(flowchart, setOutputPassFeedback, setOutputPassEscalated);
        Connect(flowchart, setOutputPassEscalated, setOutputPassReason);
        Connect(flowchart, setOutputPassReason, finishPass);

        // Critical -> emit gate -> checks -> report -> reason -> escalate
        var emitGateEvaluatedCritical = EmitGateEvaluated("EmitGateEvaluatedCritical",
            sessionIdVar, repositoryVar, branchVar, tenantIdVar, ciResultsVar,
            evaluationResultVar, attemptNumberVar, maxAttemptsVar, skillLevelVar);
        flowchart.Activities.Add(emitGateEvaluatedCritical);
        Connect(flowchart, evaluateResults, emitGateEvaluatedCritical, "Critical");
        Connect(flowchart, emitGateEvaluatedCritical, checkCoverageCritical);
        Connect(flowchart, checkCoverageCritical, checkLintCritical);
        Connect(flowchart, checkLintCritical, checkSecurityCritical);
        Connect(flowchart, checkSecurityCritical, generateReportCritical);
        Connect(flowchart, generateReportCritical, setReasonCritical);
        Connect(flowchart, setReasonCritical, setDetailCritical);
        Connect(flowchart, setDetailCritical, emitGateEscalated);

        // MajorIssues -> emit gate (major) -> guard
        Connect(flowchart, evaluateResults, emitGateEvaluatedMajor, "MajorIssues");
        Connect(flowchart, emitGateEvaluatedMajor, maxAttemptGuard);

        // Guard True (budget left) -> GENERATE FIX (mediated) -> commit -> verify changes
        Connect(flowchart, maxAttemptGuard, generateFix, "True");
        Connect(flowchart, generateFix, commitFix);
        Connect(flowchart, commitFix, commitMadeChanges);
        // Commit made real changes -> emit committed -> index -> increment -> re-trigger
        Connect(flowchart, commitMadeChanges, emitAutofixCommitted, "True");
        Connect(flowchart, emitAutofixCommitted, updateCodeIndex);
        Connect(flowchart, updateCodeIndex, incrementAttempt);
        Connect(flowchart, incrementAttempt, reTriggerCI);
        Connect(flowchart, reTriggerCI, emitReCiTriggered);
        Connect(flowchart, emitReCiTriggered, reTriggerSucceeded);
        // Re-trigger failed -> escalate (ci-trigger-failed)
        Connect(flowchart, reTriggerSucceeded, setReasonTriggerFailed, "False");
        Connect(flowchart, reTriggerSucceeded, waitForCIRetry, "True");
        // Commit was a no-op -> emit no-op -> reason -> escalate
        Connect(flowchart, commitMadeChanges, emitAutofixNoop, "False");
        Connect(flowchart, emitAutofixNoop, setReasonNoop);
        Connect(flowchart, setReasonNoop, setDetailNoop);
        Connect(flowchart, setDetailNoop, emitGateEscalated);
        // Guard False (exhausted) -> reason -> escalate
        Connect(flowchart, maxAttemptGuard, setReasonExhausted, "False");
        Connect(flowchart, setReasonExhausted, setDetailExhausted);
        Connect(flowchart, setDetailExhausted, emitGateEscalated);

        // Retry wait Received -> store -> emit -> evaluate -> emit gate
        Connect(flowchart, waitForCIRetry, storeRetryResults, "Received");
        Connect(flowchart, storeRetryResults, emitRetryResultsReceived);
        Connect(flowchart, emitRetryResultsReceived, evaluateRetryResults);
        Connect(flowchart, evaluateRetryResults, emitRetryGateEvaluated, "AllPass");
        Connect(flowchart, evaluateRetryResults, emitRetryGateEvaluated, "MinorIssues");
        // Retry wait Timeout -> escalate (ci-timeout)
        Connect(flowchart, waitForCIRetry, setReasonTimeout, "Timeout");

        // Retry pass path
        Connect(flowchart, emitRetryGateEvaluated, checkCoverageRetry);
        Connect(flowchart, checkCoverageRetry, checkLintRetry);
        Connect(flowchart, checkLintRetry, checkSecurityRetry);
        Connect(flowchart, checkSecurityRetry, generateRetryReport);
        Connect(flowchart, generateRetryReport, emitRetryGatePassed);
        Connect(flowchart, emitRetryGatePassed, setOutputRetryPassReport);
        Connect(flowchart, setOutputRetryPassReport, setOutputRetryPassPassed);
        Connect(flowchart, setOutputRetryPassPassed, setOutputRetryPassFeedback);
        Connect(flowchart, setOutputRetryPassFeedback, setOutputRetryPassEscalated);
        Connect(flowchart, setOutputRetryPassEscalated, setOutputRetryPassReason);
        Connect(flowchart, setOutputRetryPassReason, finishRetryPass);

        // Retry MajorIssues -> emit gate (major) -> guard (loop)
        Connect(flowchart, evaluateRetryResults, emitRetryGateEvaluatedMajor, "MajorIssues");
        Connect(flowchart, emitRetryGateEvaluatedMajor, maxAttemptGuard);
        // Retry Critical -> reason -> escalate
        Connect(flowchart, evaluateRetryResults, setReasonRetryCritical, "Critical");
        Connect(flowchart, setReasonRetryCritical, setDetailRetryCritical);
        Connect(flowchart, setDetailRetryCritical, emitGateEscalated);

        // Escalation terminal -> fail outputs -> finish
        Connect(flowchart, emitGateEscalated, setOutputFailReport);
        Connect(flowchart, setOutputFailReport, setOutputFailPassed);
        Connect(flowchart, setOutputFailPassed, setOutputFailFeedback);
        Connect(flowchart, setOutputFailFeedback, setOutputFailEscalated);
        Connect(flowchart, setOutputFailEscalated, setOutputFailReason);
        Connect(flowchart, setOutputFailReason, finishFail);

        builder.Root = flowchart;
    }

    // ================================================================
    // Activity factory helpers (keep the Build method readable)
    // ================================================================

    private static CheckCoverageActivity MakeCoverage(string id, string name,
        Variable<CIResultsPayload> ci, Variable<int> skill, Variable<CoverageCheckResult> result)
    {
        var a = new CheckCoverageActivity
        {
            Id = id, Name = name,
            CIResults = new(ctx => ci.Get(ctx)!),
            SkillLevel = new(ctx => skill.Get(ctx)),
            Result = new(result)
        };
        a.SetDisplayText(name);
        return a;
    }

    private static CheckLintingActivity MakeLint(string id, string name,
        Variable<CIResultsPayload> ci, Variable<int> skill, Variable<LintCheckResult> result)
    {
        var a = new CheckLintingActivity
        {
            Id = id, Name = name,
            CIResults = new(ctx => ci.Get(ctx)!),
            SkillLevel = new(ctx => skill.Get(ctx)),
            Result = new(result)
        };
        a.SetDisplayText(name);
        return a;
    }

    private static CheckSecurityActivity MakeSecurity(string id, string name,
        Variable<CIResultsPayload> ci, Variable<int> skill, Variable<SecurityCheckResult> result)
    {
        var a = new CheckSecurityActivity
        {
            Id = id, Name = name,
            CIResults = new(ctx => ci.Get(ctx)!),
            SkillLevel = new(ctx => skill.Get(ctx)),
            Result = new(result)
        };
        a.SetDisplayText(name);
        return a;
    }

    private static GenerateQualityReportActivity MakeReport(string id, string name,
        Variable<Guid> session, Variable<CIResultsPayload> ci,
        Variable<CoverageCheckResult> cov, Variable<LintCheckResult> lint, Variable<SecurityCheckResult> sec,
        Variable<int> skill, Variable<int> consecutive, Variable<QualityReport> result)
    {
        var a = new GenerateQualityReportActivity
        {
            Id = id, Name = name,
            SessionId = new(ctx => session.Get(ctx)),
            CIResults = new(ctx => ci.Get(ctx)!),
            CoverageResult = new(ctx => cov.Get(ctx)!),
            LintResult = new(ctx => lint.Get(ctx)!),
            SecurityResult = new(ctx => sec.Get(ctx)!),
            SkillLevel = new(ctx => skill.Get(ctx)),
            ConsecutivePassCount = new(ctx => consecutive.Get(ctx)),
            Result = new(result)
        };
        a.SetDisplayText(name);
        return a;
    }

    // 2026-08-13 (found by the engine-driven E2E): this parameter MUST be
    // Func<ExpressionExecutionContext, object>. With the former
    // Func<ActivityExecutionContext, object>, Input<object> has no matching
    // delegate constructor, so `new(value)` bound the LITERAL ctor and the Func
    // itself became the input's literal value — unserializable, which made the
    // workflow-definition store populator throw at startup and SKIP registering
    // this workflow AND every workflow after it in enumeration order
    // (testing-pipeline, triage-*, update-issue-status were all absent from the
    // engine's registry; NotifyIssue's update-issue-status dispatch then failed
    // "no published version" inside every cycle).
    private static SetOutput MakeOutput(
        string id, string outputName, Func<Elsa.Expressions.Models.ExpressionExecutionContext, object> value)
    {
        var a = new SetOutput
        {
            Id = id,
            Name = $"Output: {outputName}",
            OutputName = new(outputName),
            OutputValue = new(value)
        };
        a.SetDisplayText($"Output: {outputName}");
        return a;
    }

    private static EmitTestingEventActivity EmitEvent(
        string id, string eventType,
        Variable<Guid> session, Variable<string> repo, Variable<string> branch, Variable<string> tenant,
        Func<ExpressionExecutionContext, string> runId,
        Func<ExpressionExecutionContext, int> attempt,
        Func<ExpressionExecutionContext, int> maxAttempts,
        Action<EmitTestingEventActivity, ActivityExecutionContext>? configure = null)
    {
        var a = new EmitTestingEventActivity
        {
            Id = id,
            Name = $"Emit {eventType}",
            EventType = new Input<string>(_ => eventType),
            SessionId = new Input<string?>(ctx => session.Get(ctx).ToString()),
            Repository = new Input<string?>(ctx => repo.Get(ctx)),
            Branch = new Input<string?>(ctx => branch.Get(ctx)),
            RunId = new Input<string?>(ctx => runId(ctx)),
            TenantId = new Input<string?>(ctx => tenant.Get(ctx)),
            Attempt = new Input<int>(ctx => attempt(ctx)),
            MaxAttempts = new Input<int>(ctx => maxAttempts(ctx)),
        };
        configure?.Invoke(a, default!);
        a.SetDisplayText($"Emit {eventType}");
        return a;
    }

    private static EmitTestingEventActivity EmitGateEvaluated(
        string id,
        Variable<Guid> session, Variable<string> repo, Variable<string> branch, Variable<string> tenant,
        Variable<CIResultsPayload> ci, Variable<QualityGateResult> eval,
        Variable<int> attempt, Variable<int> maxAttempts, Variable<int> skill)
    {
        var a = new EmitTestingEventActivity
        {
            Id = id,
            Name = "Emit GATE.EVALUATED",
            EventType = new Input<string>(_ => TestingEvents.GateEvaluated),
            SessionId = new Input<string?>(ctx => session.Get(ctx).ToString()),
            Repository = new Input<string?>(ctx => repo.Get(ctx)),
            Branch = new Input<string?>(ctx => branch.Get(ctx)),
            RunId = new Input<string?>(ctx => ci.Get(ctx)?.RunId ?? ""),
            TenantId = new Input<string?>(ctx => tenant.Get(ctx)),
            Attempt = new Input<int>(ctx => attempt.Get(ctx)),
            MaxAttempts = new Input<int>(ctx => maxAttempts.Get(ctx)),
            Outcome = new Input<string?>(ctx => eval.Get(ctx)?.Outcome.ToString()),
            Score = new Input<double>(ctx => eval.Get(ctx)?.OverallScore ?? -1),
            SkillLevel = new Input<int>(ctx => skill.Get(ctx)),
        };
        a.SetDisplayText("Emit GATE.EVALUATED");
        return a;
    }

    /// <summary>Serialize the auto-fixable issues to JSON for the llm-call fix prompt.</summary>
    private static string SerializeAutoFixableIssues(Variable<QualityGateResult> eval, ExpressionExecutionContext ctx)
    {
        var issues = eval.Get(ctx)?.Issues.Where(i => i.AutoFixable).ToList() ?? new List<QualityIssue>();
        return JsonSerializer.Serialize(issues);
    }

    /// <summary>
    /// Best-effort summary of the generated fix from the llm-call dispatch result, used as
    /// the commit description. Never returns null (falls back to a stable default).
    /// </summary>
    private static string? GetFixSummary(IDictionary<string, object>? fixResult)
    {
        if (fixResult == null) return "Auto-fix quality issues (LLM-mediated)";
        if (fixResult.TryGetValue("llmResponse", out var r) && !string.IsNullOrWhiteSpace(r?.ToString()))
        {
            var s = r!.ToString()!;
            return s.Length > 120 ? s[..120] : s;
        }
        return "Auto-fix quality issues (LLM-mediated)";
    }

    /// <summary>Helper to add a connection with default (unnamed) port.</summary>
    private static void Connect(Flowchart flowchart, IActivity source, IActivity target)
    {
        flowchart.Connections.Add(new Connection(source, target));
    }

    /// <summary>Helper to add a connection with a named source port (for FlowNode outcomes).</summary>
    private static void Connect(Flowchart flowchart, IActivity source, IActivity target, string sourcePort)
    {
        flowchart.Connections.Add(new Connection(
            new Elsa.Workflows.Activities.Flowchart.Models.Endpoint(source, sourcePort),
            new Elsa.Workflows.Activities.Flowchart.Models.Endpoint(target)));
    }
}
