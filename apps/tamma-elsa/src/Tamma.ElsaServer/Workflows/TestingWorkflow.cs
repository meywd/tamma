using System.Text.Json;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Activities.Flowchart.Models;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Models;
using Tamma.Activities.Testing;
using Tamma.Activities.Testing.Models;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Testing sub-workflow: runs the full testing/quality pipeline with skill-level-aware thresholds,
/// bookmark-based CI wait, auto-fix loops (using LLM Call sub-workflow), and teaching feedback.
///
/// Flow:
///   1. TriggerCI -> WaitForCIResults (bookmark) -> EvaluateResults
///   2. EvaluateResults routes to:
///      - AllPass:       CheckCoverage -> CheckLinting -> CheckSecurity -> GenerateQualityReport -> Finish(pass)
///      - MinorIssues:   Same check pipeline (report determines final status)
///      - MajorIssues:   Auto-fix loop (CommitFix -> re-trigger CI, max 3 attempts)
///      - Critical:      Checks -> GenerateQualityReport -> Finish(fail)
///
/// Inputs:  SessionId (Guid), Repository (string), Branch (string), SkillLevel (int), ConsecutivePassCount (int)
/// Outputs: QualityReport
/// </summary>
public class TestingWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.DefinitionId = "testing-pipeline";

        // ============================================
        // Workflow Variables
        // ============================================
        var sessionIdVar = builder.WithVariable<Guid>("SessionId", default);
        var repositoryVar = builder.WithVariable<string>("Repository", "");
        var branchVar = builder.WithVariable<string>("Branch", "");
        var skillLevelVar = builder.WithVariable<int>("SkillLevel", 3);
        var consecutivePassCountVar = builder.WithVariable<int>("ConsecutivePassCount", 0);
        var attemptNumberVar = builder.WithVariable<int>("AttemptNumber", 1);
        var maxAttemptsVar = builder.WithVariable<int>("MaxAttempts", 3);

        // Result variables — activities write directly to these via Output<T>(variable)
        var ciTriggerResultVar = builder.WithVariable<CITriggerResult>("CITriggerResult", default!);
        var ciResultsVar = builder.WithVariable<CIResultsPayload>("CIResultsPayload", default!);
        var evaluationResultVar = builder.WithVariable<QualityGateResult>("EvaluationResult", default!);
        var coverageResultVar = builder.WithVariable<CoverageCheckResult>("CoverageResult", default!);
        var lintResultVar = builder.WithVariable<LintCheckResult>("LintResult", default!);
        var securityResultVar = builder.WithVariable<SecurityCheckResult>("SecurityResult", default!);
        var qualityReportVar = builder.WithVariable<QualityReport>("QualityReport", default!);
        var commitFixResultVar = builder.WithVariable<CommitFixResult>("CommitFixResult", default!);
        var ciResultsFromWaitVar = builder.WithVariable<CIResultsPayload>("CIResultsFromWait", default!);

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

        // ============================================
        // Step 2: Wait for CI results (bookmark-based)
        // ============================================
        var waitForCI = new WaitForCIResultsActivity
        {
            Id = "WaitForCIResults",
            Name = "Wait for CI Results",
            SessionId = new(ctx => sessionIdVar.Get(ctx)),
            RunId = new(ctx => ciTriggerResultVar.Get(ctx)?.RunId ?? "unknown"),
            TimeoutMinutes = new(30),
            Results = new(ciResultsFromWaitVar)
        };

        // Store CI results into the shared variable via Inline
        var storeCIResults = new SetVariable
        {
            Id = "StoreCIResults",
            Name = "Store CI Results",
            Variable = ciResultsVar,
            Value = new(ctx => (object?)ciResultsFromWaitVar.Get(ctx))
        };

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

        // ============================================
        // AllPass/MinorIssues path: detailed checks
        // ============================================
        var checkCoverage = new CheckCoverageActivity
        {
            Id = "CheckCoverage",
            Name = "Check Code Coverage",
            CIResults = new(ctx => ciResultsVar.Get(ctx)!),
            SkillLevel = new(ctx => skillLevelVar.Get(ctx)),
            Result = new(coverageResultVar)
        };

        var checkLinting = new CheckLintingActivity
        {
            Id = "CheckLinting",
            Name = "Check Linting Rules",
            CIResults = new(ctx => ciResultsVar.Get(ctx)!),
            SkillLevel = new(ctx => skillLevelVar.Get(ctx)),
            Result = new(lintResultVar)
        };

        var checkSecurity = new CheckSecurityActivity
        {
            Id = "CheckSecurity",
            Name = "Check Security Issues",
            CIResults = new(ctx => ciResultsVar.Get(ctx)!),
            SkillLevel = new(ctx => skillLevelVar.Get(ctx)),
            Result = new(securityResultVar)
        };

        var generateReport = new GenerateQualityReportActivity
        {
            Id = "GenerateQualityReport",
            Name = "Generate Quality Report",
            SessionId = new(ctx => sessionIdVar.Get(ctx)),
            CIResults = new(ctx => ciResultsVar.Get(ctx)!),
            CoverageResult = new(ctx => coverageResultVar.Get(ctx)!),
            LintResult = new(ctx => lintResultVar.Get(ctx)!),
            SecurityResult = new(ctx => securityResultVar.Get(ctx)!),
            SkillLevel = new(ctx => skillLevelVar.Get(ctx)),
            ConsecutivePassCount = new(ctx => consecutivePassCountVar.Get(ctx)),
            Result = new(qualityReportVar)
        };

        // ============================================
        // Critical path: same checks, different finish
        // ============================================
        var checkCoverageCritical = new CheckCoverageActivity
        {
            Id = "CheckCoverageCritical",
            Name = "Check Coverage (Critical)",
            CIResults = new(ctx => ciResultsVar.Get(ctx)!),
            SkillLevel = new(ctx => skillLevelVar.Get(ctx)),
            Result = new(coverageResultVar)
        };

        var checkLintCritical = new CheckLintingActivity
        {
            Id = "CheckLintCritical",
            Name = "Check Lint (Critical)",
            CIResults = new(ctx => ciResultsVar.Get(ctx)!),
            SkillLevel = new(ctx => skillLevelVar.Get(ctx)),
            Result = new(lintResultVar)
        };

        var checkSecurityCritical = new CheckSecurityActivity
        {
            Id = "CheckSecurityCritical",
            Name = "Check Security (Critical)",
            CIResults = new(ctx => ciResultsVar.Get(ctx)!),
            SkillLevel = new(ctx => skillLevelVar.Get(ctx)),
            Result = new(securityResultVar)
        };

        var generateReportCritical = new GenerateQualityReportActivity
        {
            Id = "GenerateQualityReportCritical",
            Name = "Generate Report (Critical)",
            SessionId = new(ctx => sessionIdVar.Get(ctx)),
            CIResults = new(ctx => ciResultsVar.Get(ctx)!),
            CoverageResult = new(ctx => coverageResultVar.Get(ctx)!),
            LintResult = new(ctx => lintResultVar.Get(ctx)!),
            SecurityResult = new(ctx => securityResultVar.Get(ctx)!),
            SkillLevel = new(ctx => skillLevelVar.Get(ctx)),
            ConsecutivePassCount = new(ctx => consecutivePassCountVar.Get(ctx)),
            Result = new(qualityReportVar)
        };

        // ============================================
        // MajorIssues path: auto-fix loop
        // ============================================
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
            FixDescription = new("Auto-fix quality issues via LLM"),
            Result = new(commitFixResultVar)
        };

        var incrementAttempt = new SetVariable<int>(attemptNumberVar, ctx =>
            attemptNumberVar.Get(ctx) + 1)
        {
            Id = "IncrementAttempt",
            Name = "Increment Fix Attempt"
        };

        // Re-trigger CI after fix
        var reTriggerCI = new TriggerCIActivity
        {
            Id = "ReTriggerCI",
            Name = "Re-Trigger CI After Fix",
            SessionId = new(ctx => sessionIdVar.Get(ctx)),
            Repository = new(ctx => repositoryVar.Get(ctx)),
            Branch = new(ctx => branchVar.Get(ctx)),
            Result = new(ciTriggerResultVar)
        };

        var waitForCIRetry = new WaitForCIResultsActivity
        {
            Id = "WaitForCIResultsRetry",
            Name = "Wait for CI Results (Retry)",
            SessionId = new(ctx => sessionIdVar.Get(ctx)),
            RunId = new(ctx => ciTriggerResultVar.Get(ctx)?.RunId ?? "unknown"),
            TimeoutMinutes = new(30),
            Results = new(ciResultsFromWaitVar)
        };

        var storeRetryResults = new SetVariable
        {
            Id = "StoreRetryResults",
            Name = "Store Retry CI Results",
            Variable = ciResultsVar,
            Value = new(ctx => (object?)ciResultsFromWaitVar.Get(ctx))
        };

        // Re-evaluate after retry
        var evaluateRetryResults = new EvaluateResultsActivity
        {
            Id = "EvaluateRetryResults",
            Name = "Evaluate Retry Results",
            CIResults = new(ctx => ciResultsVar.Get(ctx)!),
            SkillLevel = new(ctx => skillLevelVar.Get(ctx)),
            ConsecutivePassCount = new(ctx => consecutivePassCountVar.Get(ctx)),
            EvaluationResult = new(evaluationResultVar)
        };

        // Retry pass path: detailed checks
        var checkCoverageRetry = new CheckCoverageActivity
        {
            Id = "CheckCoverageRetry",
            Name = "Check Coverage (Retry)",
            CIResults = new(ctx => ciResultsVar.Get(ctx)!),
            SkillLevel = new(ctx => skillLevelVar.Get(ctx)),
            Result = new(coverageResultVar)
        };

        var checkLintRetry = new CheckLintingActivity
        {
            Id = "CheckLintRetry",
            Name = "Check Lint (Retry)",
            CIResults = new(ctx => ciResultsVar.Get(ctx)!),
            SkillLevel = new(ctx => skillLevelVar.Get(ctx)),
            Result = new(lintResultVar)
        };

        var checkSecurityRetry = new CheckSecurityActivity
        {
            Id = "CheckSecurityRetry",
            Name = "Check Security (Retry)",
            CIResults = new(ctx => ciResultsVar.Get(ctx)!),
            SkillLevel = new(ctx => skillLevelVar.Get(ctx)),
            Result = new(securityResultVar)
        };

        var generateRetryReport = new GenerateQualityReportActivity
        {
            Id = "GenerateQualityReportRetry",
            Name = "Generate Report (Retry)",
            SessionId = new(ctx => sessionIdVar.Get(ctx)),
            CIResults = new(ctx => ciResultsVar.Get(ctx)!),
            CoverageResult = new(ctx => coverageResultVar.Get(ctx)!),
            LintResult = new(ctx => lintResultVar.Get(ctx)!),
            SecurityResult = new(ctx => securityResultVar.Get(ctx)!),
            SkillLevel = new(ctx => skillLevelVar.Get(ctx)),
            ConsecutivePassCount = new(ctx => consecutivePassCountVar.Get(ctx)),
            Result = new(qualityReportVar)
        };

        // ============================================
        // Max-attempt guard for MajorIssues retry loop
        // ============================================
        var maxAttemptGuard = new FlowDecision(ctx =>
            attemptNumberVar.Get(ctx) < maxAttemptsVar.Get(ctx))
        { Id = "MaxAttemptGuard", Name = "Fix Attempts Remaining?" };

        // ============================================
        // SetOutput activities (expose workflow outputs before each Finish)
        // ============================================

        // Pass path outputs
        var setOutputPassReport = new SetOutput
        {
            Id = "SetOutputPassReport",
            Name = "Output: Quality Report (Pass)",
            OutputName = new("qualityReport"),
            OutputValue = new(ctx => (object)JsonSerializer.Serialize(qualityReportVar.Get(ctx)))
        };
        var setOutputPassPassed = new SetOutput
        {
            Id = "SetOutputPassPassed",
            Name = "Output: Passed Flag (Pass)",
            OutputName = new("passed"),
            OutputValue = new(ctx => (object)(qualityReportVar.Get(ctx)?.Passed ?? true))
        };
        var setOutputPassFeedback = new SetOutput
        {
            Id = "SetOutputPassFeedback",
            Name = "Output: Teaching Feedback (Pass)",
            OutputName = new("teachingFeedback"),
            OutputValue = new(ctx => (object)(qualityReportVar.Get(ctx)?.TeachingFeedback ?? ""))
        };

        // Fail path outputs
        var setOutputFailReport = new SetOutput
        {
            Id = "SetOutputFailReport",
            Name = "Output: Quality Report (Fail)",
            OutputName = new("qualityReport"),
            OutputValue = new(ctx => (object)JsonSerializer.Serialize(qualityReportVar.Get(ctx)))
        };
        var setOutputFailPassed = new SetOutput
        {
            Id = "SetOutputFailPassed",
            Name = "Output: Passed Flag (Fail)",
            OutputName = new("passed"),
            OutputValue = new(ctx => (object)(qualityReportVar.Get(ctx)?.Passed ?? false))
        };
        var setOutputFailFeedback = new SetOutput
        {
            Id = "SetOutputFailFeedback",
            Name = "Output: Teaching Feedback (Fail)",
            OutputName = new("teachingFeedback"),
            OutputValue = new(ctx => (object)(qualityReportVar.Get(ctx)?.TeachingFeedback ?? ""))
        };

        // Retry pass path outputs
        var setOutputRetryPassReport = new SetOutput
        {
            Id = "SetOutputRetryPassReport",
            Name = "Output: Quality Report (Retry Pass)",
            OutputName = new("qualityReport"),
            OutputValue = new(ctx => (object)JsonSerializer.Serialize(qualityReportVar.Get(ctx)))
        };
        var setOutputRetryPassPassed = new SetOutput
        {
            Id = "SetOutputRetryPassPassed",
            Name = "Output: Passed Flag (Retry Pass)",
            OutputName = new("passed"),
            OutputValue = new(ctx => (object)(qualityReportVar.Get(ctx)?.Passed ?? true))
        };
        var setOutputRetryPassFeedback = new SetOutput
        {
            Id = "SetOutputRetryPassFeedback",
            Name = "Output: Teaching Feedback (Retry Pass)",
            OutputName = new("teachingFeedback"),
            OutputValue = new(ctx => (object)(qualityReportVar.Get(ctx)?.TeachingFeedback ?? ""))
        };

        // ============================================
        // Finish activities
        // ============================================
        var finishPass = new Finish { Id = "FinishPass", Name = "Complete: Tests Passed" };
        var finishFail = new Finish { Id = "FinishFail", Name = "Complete: Tests Failed" };
        var finishRetryPass = new Finish { Id = "FinishRetryPass", Name = "Complete: Tests Passed After Retry" };

        // ============================================
        // Build the Flowchart
        // ============================================
        var flowchart = new Flowchart { Id = "TestingPipelineFlowchart", Name = "Testing Pipeline Flowchart" };

        // Add all activities to the flowchart
        var allActivities = new IActivity[]
        {
            // Main pipeline
            triggerCI, waitForCI, storeCIResults, evaluateResults,
            // AllPass/MinorIssues path
            checkCoverage, checkLinting, checkSecurity, generateReport,
            setOutputPassReport, setOutputPassPassed, setOutputPassFeedback, finishPass,
            // Critical path
            checkCoverageCritical, checkLintCritical, checkSecurityCritical,
            generateReportCritical,
            setOutputFailReport, setOutputFailPassed, setOutputFailFeedback, finishFail,
            // MajorIssues (auto-fix loop) path with max-attempt guard
            maxAttemptGuard, commitFix, incrementAttempt, reTriggerCI, waitForCIRetry,
            storeRetryResults, evaluateRetryResults,
            // Retry pass path
            checkCoverageRetry, checkLintRetry, checkSecurityRetry,
            generateRetryReport,
            setOutputRetryPassReport, setOutputRetryPassPassed, setOutputRetryPassFeedback,
            finishRetryPass
        };

        foreach (var activity in allActivities)
        {
            flowchart.Activities.Add(activity);
        }

        // ============================================
        // Wire connections
        // ============================================

        // Main pipeline: Trigger -> Wait -> Store -> Evaluate
        Connect(flowchart, triggerCI, waitForCI);
        Connect(flowchart, waitForCI, storeCIResults);
        Connect(flowchart, storeCIResults, evaluateResults);

        // AllPass outcome: detailed checks -> report -> SetOutputs -> finish
        Connect(flowchart, evaluateResults, checkCoverage, "AllPass");
        Connect(flowchart, checkCoverage, checkLinting);
        Connect(flowchart, checkLinting, checkSecurity);
        Connect(flowchart, checkSecurity, generateReport);
        Connect(flowchart, generateReport, setOutputPassReport);
        Connect(flowchart, setOutputPassReport, setOutputPassPassed);
        Connect(flowchart, setOutputPassPassed, setOutputPassFeedback);
        Connect(flowchart, setOutputPassFeedback, finishPass);

        // MinorIssues outcome: same check pipeline (report captures the status)
        Connect(flowchart, evaluateResults, checkCoverage, "MinorIssues");

        // Critical outcome: checks -> report -> SetOutputs -> fail
        Connect(flowchart, evaluateResults, checkCoverageCritical, "Critical");
        Connect(flowchart, checkCoverageCritical, checkLintCritical);
        Connect(flowchart, checkLintCritical, checkSecurityCritical);
        Connect(flowchart, checkSecurityCritical, generateReportCritical);
        Connect(flowchart, generateReportCritical, setOutputFailReport);
        Connect(flowchart, setOutputFailReport, setOutputFailPassed);
        Connect(flowchart, setOutputFailPassed, setOutputFailFeedback);
        Connect(flowchart, setOutputFailFeedback, finishFail);

        // MajorIssues outcome: guard -> auto-fix loop
        Connect(flowchart, evaluateResults, maxAttemptGuard, "MajorIssues");
        // Guard True (attempts < max): proceed with fix
        Connect(flowchart, maxAttemptGuard, commitFix, "True");
        // Guard False (attempts >= max): fail out via SetOutputs -> finishFail
        Connect(flowchart, maxAttemptGuard, setOutputFailReport, "False");
        Connect(flowchart, commitFix, incrementAttempt);
        Connect(flowchart, incrementAttempt, reTriggerCI);
        Connect(flowchart, reTriggerCI, waitForCIRetry);
        Connect(flowchart, waitForCIRetry, storeRetryResults);
        Connect(flowchart, storeRetryResults, evaluateRetryResults);

        // Retry evaluation routes
        Connect(flowchart, evaluateRetryResults, checkCoverageRetry, "AllPass");
        Connect(flowchart, evaluateRetryResults, checkCoverageRetry, "MinorIssues");
        Connect(flowchart, checkCoverageRetry, checkLintRetry);
        Connect(flowchart, checkLintRetry, checkSecurityRetry);
        Connect(flowchart, checkSecurityRetry, generateRetryReport);
        Connect(flowchart, generateRetryReport, setOutputRetryPassReport);
        Connect(flowchart, setOutputRetryPassReport, setOutputRetryPassPassed);
        Connect(flowchart, setOutputRetryPassPassed, setOutputRetryPassFeedback);
        Connect(flowchart, setOutputRetryPassFeedback, finishRetryPass);

        // Retry MajorIssues: go through max-attempt guard before looping
        Connect(flowchart, evaluateRetryResults, maxAttemptGuard, "MajorIssues");

        // Retry Critical: fail out via SetOutputs
        Connect(flowchart, evaluateRetryResults, setOutputFailReport, "Critical");

        builder.Root = flowchart;
    }

    /// <summary>
    /// Helper to add a connection with default (unnamed) port.
    /// </summary>
    private static void Connect(Flowchart flowchart, IActivity source, IActivity target)
    {
        flowchart.Connections.Add(new Connection(source, target));
    }

    /// <summary>
    /// Helper to add a connection with a named source port (for FlowNode outcomes).
    /// </summary>
    private static void Connect(Flowchart flowchart, IActivity source, IActivity target, string sourcePort)
    {
        flowchart.Connections.Add(new Connection(
            new Elsa.Workflows.Activities.Flowchart.Models.Endpoint(source, sourcePort),
            new Elsa.Workflows.Activities.Flowchart.Models.Endpoint(target)));
    }
}
