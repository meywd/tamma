using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Activities.Flowchart.Models;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime.Activities;
using System.Text.Json;
using Tamma.Activities.CodeIndex;
using Tamma.Activities.Debug;
using Tamma.Activities.Security;
using Tamma.Activities.Debug.Models;
using Tamma.Api.Services.Agents;
using Endpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;

using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Debugging sub-workflow: systematic AI-driven debugging with 3 entry modes.
///
/// Modes:
///   - TddFailure: tests fail during GREEN phase -- focus on making tests pass
///   - RuntimeError: unexpected runtime errors -- broader investigation
///   - BugInvestigation: pre-implementation bug investigation -- TDD for bugs
///
/// Flow:
///   1. ReadInputs -> Initialize (start time, iteration, max iterations from config)
///   2. ClassifyDebugContext -> route by mode; emit DEBUG.SESSION.STARTED
///   3. FlowFork: parallel context gathering, durably bounded by a 15s timeout
///      (ContextCollectionTimeoutActivity / Debugging:ContextCollectionTimeoutSeconds)
///   4. Serialize collector outputs to string variables (partial on timeout)
///   5. AIDiagnosis (mediated call-LLM) -> ranked hypotheses; DEBUG.DIAGNOSIS.SUCCESS/FAILED
///   6. Debug loop (max N iterations, graph-enforced bound):
///      a. Select highest-confidence untried hypothesis; DEBUG.HYPOTHESIS.SELECTED
///      b. BugInvestigation: write regression test, run it, REQUIRE it to FAIL (AC7)
///      c. Apply fix via mediated llm-call; capture result, branch on success (no false success)
///      d. Run tests (testing-pipeline)
///      e. Pass -> RecordResolution -> serialize DebugResult -> done (DEBUG.RESOLVED.SUCCESS)
///      f. Fail -> mark hypothesis outcome + FixAttempt -> RefineHypothesis -> loop
///   7. No hypothesis / max iterations / invalid regression test -> CompileDebugReport ->
///      serialize DebugResult -> escalate (DEBUG.ESCALATED)
///
/// Invoked as child workflow via DispatchWorkflow or standalone via ELSA REST API.
/// Callers (TddWithDebugRetry / CiWithDebugRetry) pass sessionId/storyId/debugContextMode/
/// errorOutput/repositoryUrl/branchName/skillLevel and (optionally) tenantId.
/// </summary>
public class DebuggingWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Debugging";
        builder.DefinitionId = "debugging";
        builder.Version = WorkflowVersions.ComputedVersion;

        // ---- Workflow variables ----
        var sessionId = builder.WithVariable<Guid>();
        var storyId = builder.WithVariable<string>();
        var debugContextMode = builder.WithVariable<string>();
        var errorOutput = builder.WithVariable<string>();
        var relevantFiles = builder.WithVariable<string>();
        var issueDescription = builder.WithVariable<string>();
        var repositoryUrl = builder.WithVariable<string>();
        var branchName = builder.WithVariable<string>();
        var skillLevel = builder.WithVariable<int>();
        // Named "TenantId" so MediatedLlmText.ResolveTenantId + the event drain resolve
        // the tenant scope from this ambient variable (SaaS prompts/conventions/creds).
        var tenantId = builder.WithVariable<string>("TenantId", "");

        // Typed result variables for collector activity outputs (bound via Output<T>)
        var collectErrorsResult = builder.WithVariable<ErrorMessages>();
        var collectCodeResult = builder.WithVariable<RelevantCode>();
        var collectGitResult = builder.WithVariable<GitHistoryContext>();
        var collectTestsResult = builder.WithVariable<TestResultsContext>();
        var collectReproResult = builder.WithVariable<ReproductionSteps>();

        // Gathered context variables (string serializations consumed by AIDiagnosis)
        var errorMessages = builder.WithVariable<string>();
        var relevantCode = builder.WithVariable<string>();
        var gitHistory = builder.WithVariable<string>();
        var testResults = builder.WithVariable<string>();
        var reproductionSteps = builder.WithVariable<string>();

        // Typed result variables for diagnosis/hypothesis activities
        var diagnosisResultVar = builder.WithVariable<DiagnosisResult>();
        var selectedHypothesisVar = builder.WithVariable<Hypothesis?>();
        var refinedDiagnosisVar = builder.WithVariable<DiagnosisResult>();

        // Diagnosis & loop variables
        var hypothesesJson = builder.WithVariable<string>();
        var iterationContextJson = builder.WithVariable<string>();
        var currentIteration = builder.WithVariable<int>();
        var maxIterations = builder.WithVariable<int>();
        var selectedHypothesisJson = builder.WithVariable<string>();
        var debugStartTime = builder.WithVariable<string>();
        var debugResultJson = builder.WithVariable<string>();
        var allFilesModified = builder.WithVariable<string>();
        var attemptsJson = builder.WithVariable<string>();
        var regressionTestWritten = builder.WithVariable<bool>();
        var contextGatherDone = builder.WithVariable<bool>();
        var debugStatus = builder.WithVariable<string>();
        var escalationReason = builder.WithVariable<string>();
        var runTestsOutput = builder.WithVariable<IDictionary<string, object>?>();
        var applyFixOutput = builder.WithVariable<IDictionary<string, object>?>();
        var regressionRunOutput = builder.WithVariable<IDictionary<string, object>?>();
        var regressionTestResultVar = builder.WithVariable<TestGenerationResult>();
        var compileReportResultVar = builder.WithVariable<DebugReport>();

        // ---- Activities ----

        // 0. Read workflow inputs into named variables (no auto-binding for anonymous vars).
        var readInputs = new SetVariable<string>(debugStartTime,
            ctx =>
            {
                var sid = ctx.GetInput<Guid>("sessionId");
                if (sid != Guid.Empty) sessionId.Set(ctx, sid);
                var story = ctx.GetInput<string>("storyId");
                if (!string.IsNullOrEmpty(story)) storyId.Set(ctx, story);
                var mode = ctx.GetInput<string>("debugContextMode");
                debugContextMode.Set(ctx, string.IsNullOrEmpty(mode) ? "RuntimeError" : mode);
                var err = ctx.GetInput<string>("errorOutput");
                if (!string.IsNullOrEmpty(err)) errorOutput.Set(ctx, err);
                var files = ctx.GetInput<string>("relevantFiles");
                if (!string.IsNullOrEmpty(files)) relevantFiles.Set(ctx, files);
                var issue = ctx.GetInput<string>("issueDescription");
                if (!string.IsNullOrEmpty(issue)) issueDescription.Set(ctx, issue);
                var repo = ctx.GetInput<string>("repositoryUrl");
                if (!string.IsNullOrEmpty(repo)) repositoryUrl.Set(ctx, repo);
                var branch = ctx.GetInput<string>("branchName");
                if (!string.IsNullOrEmpty(branch)) branchName.Set(ctx, branch);
                var skill = ctx.GetInput<int>("skillLevel");
                if (skill > 0) skillLevel.Set(ctx, skill);
                var tenant = ctx.GetInput<string>("tenantId");
                if (!string.IsNullOrEmpty(tenant)) tenantId.Set(ctx, tenant);

                return DateTime.UtcNow.ToString("o");
            })
        { Id = "readInputs", Name = "Read Inputs & Init Start Time" };
        readInputs.SetDisplayText("Read Inputs & Init Start Time");

        var initIteration = new SetVariable<int>(currentIteration, _ => 1)
        { Id = "initIteration", Name = "Initialize Iteration" };
        initIteration.SetDisplayText("Initialize Iteration");

        // #13 (cheap): MaxIterations from Debugging:MaxIterations config (default 5).
        var initMaxIterations = new SetVariable<int>(maxIterations,
            ctx =>
            {
                var cfg = ctx.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
                var raw = cfg["Debugging:MaxIterations"];
                return int.TryParse(raw, out var n) && n > 0 ? n : 5;
            })
        { Id = "initMaxIterations", Name = "Initialize Max Iterations" };
        initMaxIterations.SetDisplayText("Initialize Max Iterations");

        var initFilesModified = new SetVariable<string>(allFilesModified, _ => "[]")
        { Id = "initFilesModified", Name = "Initialize Files Modified" };
        initFilesModified.SetDisplayText("Initialize Files Modified");

        var initAttempts = new SetVariable<string>(attemptsJson, _ => "[]")
        { Id = "initAttempts", Name = "Initialize Attempts" };
        initAttempts.SetDisplayText("Initialize Attempts");

        var initRegressionTest = new SetVariable<bool>(regressionTestWritten, _ => false)
        { Id = "initRegressionTest", Name = "Initialize Regression Test Flag" };
        initRegressionTest.SetDisplayText("Initialize Regression Test Flag");

        var initContextDone = new SetVariable<bool>(contextGatherDone, _ => false)
        { Id = "initContextDone", Name = "Initialize Context Gather Flag" };
        initContextDone.SetDisplayText("Initialize Context Gather Flag");

        var initIterationContext = new SetVariable<string>(iterationContextJson,
            _ => "{\"currentIteration\":0,\"hypotheses\":[],\"previousAttempts\":[]}")
        { Id = "initIterationContext", Name = "Initialize Iteration Context" };
        initIterationContext.SetDisplayText("Initialize Iteration Context");

        // 2. Classify debug context
        var classify = new ClassifyDebugContextActivity
        {
            Id = "classifyContext",
            Name = "Classify Debug Context",
            DebugContextMode = new Input<string>(ctx => debugContextMode.Get(ctx) ?? "RuntimeError"),
            SessionId = new Input<Guid>(ctx => sessionId.Get(ctx))
        };
        classify.SetDisplayText("Classify Debug Context");

        // 3. Context-specific emphasis logging (one per branch, all converge)
        var tddEmphasis = new WriteLine("Debug mode: TDD Failure -- emphasizing test output and implementation code")
        { Id = "tddEmphasis", Name = "TDD Emphasis" };
        tddEmphasis.SetDisplayText("TDD Emphasis");

        var runtimeEmphasis = new WriteLine("Debug mode: Runtime Error -- emphasizing stack traces and recent changes")
        { Id = "runtimeEmphasis", Name = "Runtime Emphasis" };
        runtimeEmphasis.SetDisplayText("Runtime Emphasis");

        var bugEmphasis = new WriteLine("Debug mode: Bug Investigation -- emphasizing issue description and reproduction steps")
        { Id = "bugEmphasis", Name = "Bug Emphasis" };
        bugEmphasis.SetDisplayText("Bug Emphasis");

        // #8: DEBUG.SESSION.STARTED (after classify)
        var emitSessionStarted = new EmitDebugEventActivity
        {
            Id = "emitSessionStarted", Name = "Emit DEBUG.SESSION.STARTED",
            EventType = new Input<string>(_ => DebugEvents.SessionStarted),
            SessionId = new Input<string?>(ctx => sessionId.Get(ctx).ToString()),
            StoryId = new Input<string?>(ctx => storyId.Get(ctx)),
            Mode = new Input<string?>(ctx => debugContextMode.Get(ctx)),
            TenantId = new Input<string?>(ctx => tenantId.Get(ctx)),
            Iteration = new Input<int>(ctx => currentIteration.Get(ctx)),
            MaxIterations = new Input<int>(ctx => maxIterations.Get(ctx)),
        };
        emitSessionStarted.SetDisplayText("Emit DEBUG.SESSION.STARTED");

        // 4. Parallel context gathering activities -- Result output wired to typed variables
        var collectErrors = new CollectErrorMessagesActivity
        {
            Id = "collectErrors",
            Name = "Collect Error Messages",
            ErrorOutput = new Input<string>(ctx => errorOutput.Get(ctx) ?? ""),
            DebugContextMode = new Input<string>(ctx => debugContextMode.Get(ctx) ?? "RuntimeError"),
            RepositoryUrl = new Input<string>(ctx => repositoryUrl.Get(ctx) ?? ""),
            BranchName = new Input<string>(ctx => branchName.Get(ctx) ?? ""),
            Result = new Output<ErrorMessages>(collectErrorsResult)
        };
        collectErrors.SetDisplayText("Collect Error Messages");

        var collectCode = new CollectRelevantCodeActivity
        {
            Id = "collectCode",
            Name = "Collect Relevant Code",
            RelevantFiles = new Input<List<string>?>(ctx =>
            {
                var files = relevantFiles.Get(ctx);
                if (string.IsNullOrEmpty(files)) return null;
                try { return JsonSerializer.Deserialize<List<string>>(files); }
                catch { return new List<string> { files }; }
            }),
            RepositoryUrl = new Input<string>(ctx => repositoryUrl.Get(ctx) ?? ""),
            BranchName = new Input<string>(ctx => branchName.Get(ctx) ?? ""),
            DebugContextMode = new Input<string>(ctx => debugContextMode.Get(ctx) ?? "RuntimeError"),
            Result = new Output<RelevantCode>(collectCodeResult)
        };
        collectCode.SetDisplayText("Collect Relevant Code");

        var collectGit = new CollectGitHistoryActivity
        {
            Id = "collectGit",
            Name = "Collect Git History",
            RepositoryUrl = new Input<string>(ctx => repositoryUrl.Get(ctx) ?? ""),
            BranchName = new Input<string>(ctx => branchName.Get(ctx) ?? ""),
            DebugContextMode = new Input<string>(ctx => debugContextMode.Get(ctx) ?? "RuntimeError"),
            Result = new Output<GitHistoryContext>(collectGitResult)
        };
        collectGit.SetDisplayText("Collect Git History");

        var collectTests = new CollectTestResultsActivity
        {
            Id = "collectTests",
            Name = "Collect Test Results",
            RepositoryUrl = new Input<string>(ctx => repositoryUrl.Get(ctx) ?? ""),
            BranchName = new Input<string>(ctx => branchName.Get(ctx) ?? ""),
            DebugContextMode = new Input<string>(ctx => debugContextMode.Get(ctx) ?? "RuntimeError"),
            ErrorOutput = new Input<string>(ctx => errorOutput.Get(ctx) ?? ""),
            Result = new Output<TestResultsContext>(collectTestsResult)
        };
        collectTests.SetDisplayText("Collect Test Results");

        var collectRepro = new CollectReproductionStepsActivity
        {
            Id = "collectRepro",
            Name = "Collect Reproduction Steps",
            IssueDescription = new Input<string>(ctx => issueDescription.Get(ctx) ?? ""),
            DebugContextMode = new Input<string>(ctx => debugContextMode.Get(ctx) ?? "RuntimeError"),
            Result = new Output<ReproductionSteps>(collectReproResult)
        };
        collectRepro.SetDisplayText("Collect Reproduction Steps");

        // 5. FlowFork for parallel context gathering (5 collectors + 1 durable timeout guard)
        var fork = new FlowFork
        {
            Id = "contextFork",
            Name = "Context Fork",
            Branches = new Input<ICollection<string>>(new List<string>
            {
                "CollectErrors",
                "CollectCode",
                "CollectGit",
                "CollectTests",
                "CollectRepro",
                "Timeout"
            })
        };
        fork.SetDisplayText("Context Fork");

        // #11 AC4: durable 15s context-collection timeout racing the collectors.
        var contextTimeout = new ContextCollectionTimeoutActivity
        {
            Id = "contextTimeout",
            Name = "Context Collection Timeout",
            SessionId = new Input<string>(ctx => sessionId.Get(ctx).ToString())
        };
        contextTimeout.SetDisplayText("Context Collection Timeout");

        // 6. FlowJoin -- waits for all 5 collectors (the timeout branch resumes separately).
        var join = new FlowJoin
        {
            Id = "contextJoin",
            Name = "Context Join",
            Mode = new Input<FlowJoinMode>(FlowJoinMode.WaitAll)
        };
        join.SetDisplayText("Context Join");

        // #11: guard so the FIRST of (join-completed / timeout-fired) proceeds to
        // serialization and the second is short-circuited.
        var contextGatherGate = new FlowDecision(ctx =>
        {
            // True == first to arrive (proceed); set the flag so the loser short-circuits.
            if (contextGatherDone.Get(ctx)) return false;
            contextGatherDone.Set(ctx, true);
            return true;
        })
        { Id = "contextGatherGate", Name = "Context Gathered First?" };
        contextGatherGate.SetDisplayText("Context Gathered First?");

        var contextGateSink = new WriteLine("Context already gathered -- short-circuiting the slower context branch")
        { Id = "contextGateSink", Name = "Context Gate Sink" };
        contextGateSink.SetDisplayText("Context Gate Sink");

        var joinLog = new WriteLine("Debug context gathered -- proceeding to serialization")
        { Id = "joinLog", Name = "Join Log" };
        joinLog.SetDisplayText("Join Log");

        // 6a. Serialize typed collector outputs to string variables for AIDiagnosis
        // (a collector that timed out / errored leaves its var null -> serialized as "").
        var serializeErrors = new SetVariable<string>(errorMessages,
            ctx =>
            {
                var result = collectErrorsResult.Get(ctx);
                return result != null ? JsonSerializer.Serialize(result) : "";
            })
        { Id = "serializeErrors", Name = "Serialize Error Messages" };
        serializeErrors.SetDisplayText("Serialize Error Messages");

        var serializeCode = new SetVariable<string>(relevantCode,
            ctx =>
            {
                var result = collectCodeResult.Get(ctx);
                return result != null ? JsonSerializer.Serialize(result) : "";
            })
        { Id = "serializeCode", Name = "Serialize Relevant Code" };
        serializeCode.SetDisplayText("Serialize Relevant Code");

        var serializeGit = new SetVariable<string>(gitHistory,
            ctx =>
            {
                var result = collectGitResult.Get(ctx);
                return result != null ? JsonSerializer.Serialize(result) : "";
            })
        { Id = "serializeGit", Name = "Serialize Git History" };
        serializeGit.SetDisplayText("Serialize Git History");

        var serializeTests = new SetVariable<string>(testResults,
            ctx =>
            {
                var result = collectTestsResult.Get(ctx);
                return result != null ? JsonSerializer.Serialize(result) : "";
            })
        { Id = "serializeTests", Name = "Serialize Test Results" };
        serializeTests.SetDisplayText("Serialize Test Results");

        var serializeRepro = new SetVariable<string>(reproductionSteps,
            ctx =>
            {
                var result = collectReproResult.Get(ctx);
                return result != null ? JsonSerializer.Serialize(result) : "";
            })
        { Id = "serializeRepro", Name = "Serialize Reproduction Steps" };
        serializeRepro.SetDisplayText("Serialize Reproduction Steps");

        // 7. AI Diagnosis -- mediated call-LLM; reads serialized context, outputs typed result
        var aiDiagnosis = new AIDiagnosisActivity
        {
            Id = "aiDiagnosis",
            Name = "AI Diagnosis",
            SessionId = new Input<Guid>(ctx => sessionId.Get(ctx)),
            DebugContextMode = new Input<string>(ctx => debugContextMode.Get(ctx) ?? "RuntimeError"),
            ErrorContext = new Input<string>(ctx => errorMessages.Get(ctx) ?? ""),
            CodeContext = new Input<string>(ctx => relevantCode.Get(ctx) ?? ""),
            GitContext = new Input<string>(ctx => gitHistory.Get(ctx) ?? ""),
            TestContext = new Input<string>(ctx => testResults.Get(ctx) ?? ""),
            ReproductionContext = new Input<string>(ctx => reproductionSteps.Get(ctx) ?? ""),
            PreviousContext = new Input<string?>(ctx => iterationContextJson.Get(ctx)),
            SkillLevel = new Input<int>(ctx => skillLevel.Get(ctx)),
            Result = new Output<DiagnosisResult>(diagnosisResultVar)
        };
        aiDiagnosis.SetDisplayText("AI Diagnosis");

        // 7a. Serialize diagnosis result to hypothesesJson string variable
        var serializeDiagnosis = new SetVariable<string>(hypothesesJson,
            ctx =>
            {
                var result = diagnosisResultVar.Get(ctx);
                return result?.Hypotheses != null
                    ? JsonSerializer.Serialize(result.Hypotheses)
                    : "[]";
            })
        { Id = "serializeDiagnosis", Name = "Serialize Diagnosis Hypotheses" };
        serializeDiagnosis.SetDisplayText("Serialize Diagnosis Hypotheses");

        // #8: DEBUG.DIAGNOSIS.SUCCESS / .FAILED (failed == diagnosis failed — e.g.
        // unparseable LLM output / failed call — OR zero usable hypotheses). Shared
        // predicate: DebugEvents.IsDiagnosisProduced.
        var diagnosisProduced = new FlowDecision(ctx =>
            DebugEvents.IsDiagnosisProduced(diagnosisResultVar.Get(ctx)))
        { Id = "diagnosisProduced", Name = "Diagnosis Produced?" };
        diagnosisProduced.SetDisplayText("Diagnosis Produced?");

        var emitDiagnosisSuccess = new EmitDebugEventActivity
        {
            Id = "emitDiagnosisSuccess", Name = "Emit DEBUG.DIAGNOSIS.SUCCESS",
            EventType = new Input<string>(_ => DebugEvents.DiagnosisSuccess),
            SessionId = new Input<string?>(ctx => sessionId.Get(ctx).ToString()),
            StoryId = new Input<string?>(ctx => storyId.Get(ctx)),
            Mode = new Input<string?>(ctx => debugContextMode.Get(ctx)),
            TenantId = new Input<string?>(ctx => tenantId.Get(ctx)),
            Iteration = new Input<int>(ctx => currentIteration.Get(ctx)),
            MaxIterations = new Input<int>(ctx => maxIterations.Get(ctx)),
        };
        emitDiagnosisSuccess.SetDisplayText("Emit DEBUG.DIAGNOSIS.SUCCESS");

        var emitDiagnosisFailed = new EmitDebugEventActivity
        {
            Id = "emitDiagnosisFailed", Name = "Emit DEBUG.DIAGNOSIS.FAILED",
            EventType = new Input<string>(_ => DebugEvents.DiagnosisFailed),
            SessionId = new Input<string?>(ctx => sessionId.Get(ctx).ToString()),
            StoryId = new Input<string?>(ctx => storyId.Get(ctx)),
            Mode = new Input<string?>(ctx => debugContextMode.Get(ctx)),
            TenantId = new Input<string?>(ctx => tenantId.Get(ctx)),
            Iteration = new Input<int>(ctx => currentIteration.Get(ctx)),
            MaxIterations = new Input<int>(ctx => maxIterations.Get(ctx)),
            // Carry the diagnosis's own failure reason (e.g. diagnosis-parse-failure)
            // into the event data; genuinely-empty hypotheses keep the legacy reason.
            Reason = new Input<string?>(ctx => DebugEvents.DiagnosisFailureReason(diagnosisResultVar.Get(ctx))),
        };
        emitDiagnosisFailed.SetDisplayText("Emit DEBUG.DIAGNOSIS.FAILED");

        // 8. Select hypothesis -- outputs typed Hypothesis?, wired to selectedHypothesisVar
        var selectHypothesis = new SelectHypothesisActivity
        {
            Id = "selectHypothesis",
            Name = "Select Hypothesis",
            HypothesesJson = new Input<string>(ctx => hypothesesJson.Get(ctx) ?? "[]"),
            CurrentIteration = new Input<int>(ctx => currentIteration.Get(ctx)),
            MaxIterations = new Input<int>(ctx => maxIterations.Get(ctx)),
            Result = new Output<Hypothesis?>(selectedHypothesisVar)
        };
        selectHypothesis.SetDisplayText("Select Hypothesis");

        // 8a. Serialize selected hypothesis to selectedHypothesisJson string variable
        var serializeSelectedHypothesis = new SetVariable<string>(selectedHypothesisJson,
            ctx =>
            {
                var result = selectedHypothesisVar.Get(ctx);
                return result != null ? JsonSerializer.Serialize(result) : "null";
            })
        { Id = "serializeSelectedHypothesis", Name = "Serialize Selected Hypothesis" };
        serializeSelectedHypothesis.SetDisplayText("Serialize Selected Hypothesis");

        // 9. Check if hypothesis was selected (not null/exhausted)
        var hasHypothesis = new FlowDecision(ctx =>
        {
            var json = selectedHypothesisJson.Get(ctx);
            return !string.IsNullOrEmpty(json) && json != "null";
        })
        { Id = "hasHypothesis", Name = "Has Hypothesis?" };
        hasHypothesis.SetDisplayText("Has Hypothesis?");

        // #8: DEBUG.HYPOTHESIS.SELECTED
        var emitHypothesisSelected = new EmitDebugEventActivity
        {
            Id = "emitHypothesisSelected", Name = "Emit DEBUG.HYPOTHESIS.SELECTED",
            EventType = new Input<string>(_ => DebugEvents.HypothesisSelected),
            SessionId = new Input<string?>(ctx => sessionId.Get(ctx).ToString()),
            StoryId = new Input<string?>(ctx => storyId.Get(ctx)),
            Mode = new Input<string?>(ctx => debugContextMode.Get(ctx)),
            TenantId = new Input<string?>(ctx => tenantId.Get(ctx)),
            Iteration = new Input<int>(ctx => currentIteration.Get(ctx)),
            MaxIterations = new Input<int>(ctx => maxIterations.Get(ctx)),
            Hypothesis = new Input<string?>(ctx => DescribeHypothesis(selectedHypothesisVar.Get(ctx))),
        };
        emitHypothesisSelected.SetDisplayText("Emit DEBUG.HYPOTHESIS.SELECTED");

        // 10. BugInvestigation guard: write regression test if needed
        var isBugMode = new FlowDecision(ctx =>
            debugContextMode.Get(ctx) == "BugInvestigation" && !regressionTestWritten.Get(ctx))
        { Id = "isBugMode", Name = "Is Bug Mode?" };
        isBugMode.SetDisplayText("Is Bug Mode?");

        var writeRegressionTest = new WriteRegressionTestActivity
        {
            Id = "writeRegressionTest",
            Name = "Write Regression Test",
            SessionId = new Input<Guid>(ctx => sessionId.Get(ctx)),
            StoryId = new Input<string>(ctx => storyId.Get(ctx) ?? ""),
            BugDescription = new Input<string>(ctx => issueDescription.Get(ctx) ?? ""),
            HypothesisJson = new Input<string>(ctx => selectedHypothesisJson.Get(ctx) ?? "{}"),
            CodeContext = new Input<string>(ctx => relevantCode.Get(ctx) ?? ""),
            RepositoryUrl = new Input<string>(ctx => repositoryUrl.Get(ctx) ?? ""),
            BranchName = new Input<string>(ctx => branchName.Get(ctx) ?? ""),
            Result = new Output<TestGenerationResult>(regressionTestResultVar)
        };
        writeRegressionTest.SetDisplayText("Write Regression Test");

        // Track the regression test file into allFilesModified.
        var captureRegressionFile = new SetVariable<string>(allFilesModified,
            ctx => MergeFiles(allFilesModified.Get(ctx), regressionTestResultVar.Get(ctx)?.TestFilePath))
        { Id = "captureRegressionFile", Name = "Capture Regression Test File" };
        captureRegressionFile.SetDisplayText("Capture Regression Test File");

        // #4 AC7: run the regression test and REQUIRE it to FAIL before fixing.
        var runRegressionTest = new DispatchWorkflow
        {
            Id = "runRegressionTest",
            Name = "Run Regression Test",
            WorkflowDefinitionId = new("testing-pipeline"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["SessionId"] = sessionId.Get(ctx),
                ["Repository"] = repositoryUrl.Get(ctx) ?? "",
                ["Branch"] = branchName.Get(ctx) ?? "",
                ["SkillLevel"] = skillLevel.Get(ctx),
                ["tenantId"] = tenantId.Get(ctx) ?? "",
            }),
            WaitForCompletion = new(true),
            Result = new(regressionRunOutput)
        };
        runRegressionTest.SetDisplayText("Run Regression Test");

        // AC7 guard: the regression test must FAIL (reproduce the bug). honor
        // Debugging:BugInvestigation:RequireRegressionTest (default true).
        var regressionFailsAsExpected = new FlowDecision(ctx =>
        {
            var cfg = ctx.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
            var requireRaw = cfg["Debugging:BugInvestigation:RequireRegressionTest"];
            var require = !bool.TryParse(requireRaw, out var r) || r; // default true
            if (!require) return true; // guard disabled -> proceed to fix

            // The regression test must reproduce the bug == the pipeline must NOT pass.
            var output = regressionRunOutput.Get(ctx);
            var passed = output != null
                && output.TryGetValue("passed", out var p) && p is bool b && b;
            // Fails-as-expected when the run did NOT pass.
            return !passed;
        })
        { Id = "regressionFailsAsExpected", Name = "Regression Fails As Expected?" };
        regressionFailsAsExpected.SetDisplayText("Regression Fails As Expected?");

        var markRegressionTestWritten = new SetVariable<bool>(regressionTestWritten, _ => true)
        { Id = "markRegressionTestWritten", Name = "Mark Regression Test Written" };
        markRegressionTestWritten.SetDisplayText("Mark Regression Test Written");

        // AC7: regression test PASSED (did not reproduce the bug) -> abort/escalate.
        var setRegressionInvalidReason = new SetVariable<string>(escalationReason,
            _ => DebugEvents.ReasonRegressionInvalid)
        { Id = "setRegressionInvalidReason", Name = "Set Regression Invalid Reason" };
        setRegressionInvalidReason.SetDisplayText("Set Regression Invalid Reason");

        var emitRegressionInvalid = new EmitDebugEventActivity
        {
            Id = "emitRegressionInvalid", Name = "Emit DEBUG.REGRESSION_TEST.INVALID",
            EventType = new Input<string>(_ => DebugEvents.RegressionInvalid),
            SessionId = new Input<string?>(ctx => sessionId.Get(ctx).ToString()),
            StoryId = new Input<string?>(ctx => storyId.Get(ctx)),
            Mode = new Input<string?>(ctx => debugContextMode.Get(ctx)),
            TenantId = new Input<string?>(ctx => tenantId.Get(ctx)),
            Iteration = new Input<int>(ctx => currentIteration.Get(ctx)),
            MaxIterations = new Input<int>(ctx => maxIterations.Get(ctx)),
            Reason = new Input<string?>(_ => DebugEvents.ReasonRegressionInvalid),
        };
        emitRegressionInvalid.SetDisplayText("Emit DEBUG.REGRESSION_TEST.INVALID");

        // 11. Apply fix via LLM call sub-workflow (mediated, tenant-scoped)
        var applyFix = new DispatchWorkflow
        {
            Id = "applyFix",
            Name = "Apply Fix",
            WorkflowDefinitionId = new("llm-call"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["agentRole"] = AgentRole.Developer.ToWire(),
                ["action"] = AgentAction.Debug.ToWire(),
                ["taskPrompt"] = $"Apply fix for hypothesis: {SecurityHelpers.SanitizeForPrompt(selectedHypothesisJson.Get(ctx) ?? "unknown")} (mode: {debugContextMode.Get(ctx)}, iteration: {currentIteration.Get(ctx)})",
                ["sessionId"] = sessionId.Get(ctx).ToString(),
                ["tenantId"] = tenantId.Get(ctx) ?? ""
            }),
            WaitForCompletion = new(true),
            Result = new(applyFixOutput)
        };
        applyFix.SetDisplayText("Apply Fix");

        // #2: accumulate files from the selected hypothesis affected_files (the LLM call's
        // structured file list is not surfaced by llm-call; hypothesis affected_files is the
        // authoritative signal of what the fix touched).
        var captureFixFiles = new SetVariable<string>(allFilesModified,
            ctx => MergeFiles(allFilesModified.Get(ctx), selectedHypothesisVar.Get(ctx)?.AffectedFiles))
        { Id = "captureFixFiles", Name = "Capture Fix Files" };
        captureFixFiles.SetDisplayText("Capture Fix Files");

        // #3 no-false-success: branch on the llm-call success flag.
        var fixApplied = new FlowDecision(ctx =>
        {
            var output = applyFixOutput.Get(ctx);
            if (output != null && output.TryGetValue("success", out var s))
                return s is true || s?.ToString() == "True";
            return false;
        })
        { Id = "fixApplied", Name = "Fix Applied?" };
        fixApplied.SetDisplayText("Fix Applied?");

        // #8: DEBUG.FIX.ATTEMPTED (carries success flag in data)
        var emitFixAttempted = new EmitDebugEventActivity
        {
            Id = "emitFixAttempted", Name = "Emit DEBUG.FIX.ATTEMPTED",
            EventType = new Input<string>(_ => DebugEvents.FixAttempted),
            SessionId = new Input<string?>(ctx => sessionId.Get(ctx).ToString()),
            StoryId = new Input<string?>(ctx => storyId.Get(ctx)),
            Mode = new Input<string?>(ctx => debugContextMode.Get(ctx)),
            TenantId = new Input<string?>(ctx => tenantId.Get(ctx)),
            Iteration = new Input<int>(ctx => currentIteration.Get(ctx)),
            MaxIterations = new Input<int>(ctx => maxIterations.Get(ctx)),
            Hypothesis = new Input<string?>(ctx => DescribeHypothesis(selectedHypothesisVar.Get(ctx))),
            FixSucceeded = new Input<bool>(ctx =>
            {
                var output = applyFixOutput.Get(ctx);
                return output != null && output.TryGetValue("success", out var s)
                    && (s is true || s?.ToString() == "True");
            }),
        };
        emitFixAttempted.SetDisplayText("Emit DEBUG.FIX.ATTEMPTED");

        // 12. Run tests via testing-pipeline sub-workflow (tenant-scoped)
        var runTests = new DispatchWorkflow
        {
            Id = "runTests",
            Name = "Run Tests",
            WorkflowDefinitionId = new("testing-pipeline"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["SessionId"] = sessionId.Get(ctx),
                ["Repository"] = repositoryUrl.Get(ctx) ?? "",
                ["Branch"] = branchName.Get(ctx) ?? "",
                ["SkillLevel"] = skillLevel.Get(ctx),
                ["tenantId"] = tenantId.Get(ctx) ?? ""
            }),
            WaitForCompletion = new(true),
            Result = new(runTestsOutput)
        };
        runTests.SetDisplayText("Run Tests");

        // 13. Check test results from DispatchWorkflow output
        var testsPass = new FlowDecision(ctx =>
        {
            var output = runTestsOutput.Get(ctx);
            if (output != null && output.TryGetValue("passed", out var p) && p is bool passed)
                return passed;
            return false;
        })
        { Id = "testsPass", Name = "Tests Pass?" };
        testsPass.SetDisplayText("Tests Pass?");

        var emitTestsPassed = new EmitDebugEventActivity
        {
            Id = "emitTestsPassed", Name = "Emit DEBUG.TESTS.PASSED",
            EventType = new Input<string>(_ => DebugEvents.TestsPassed),
            SessionId = new Input<string?>(ctx => sessionId.Get(ctx).ToString()),
            StoryId = new Input<string?>(ctx => storyId.Get(ctx)),
            Mode = new Input<string?>(ctx => debugContextMode.Get(ctx)),
            TenantId = new Input<string?>(ctx => tenantId.Get(ctx)),
            Iteration = new Input<int>(ctx => currentIteration.Get(ctx)),
            MaxIterations = new Input<int>(ctx => maxIterations.Get(ctx)),
        };
        emitTestsPassed.SetDisplayText("Emit DEBUG.TESTS.PASSED");

        var emitTestsFailed = new EmitDebugEventActivity
        {
            Id = "emitTestsFailed", Name = "Emit DEBUG.TESTS.FAILED",
            EventType = new Input<string>(_ => DebugEvents.TestsFailed),
            SessionId = new Input<string?>(ctx => sessionId.Get(ctx).ToString()),
            StoryId = new Input<string?>(ctx => storyId.Get(ctx)),
            Mode = new Input<string?>(ctx => debugContextMode.Get(ctx)),
            TenantId = new Input<string?>(ctx => tenantId.Get(ctx)),
            Iteration = new Input<int>(ctx => currentIteration.Get(ctx)),
            MaxIterations = new Input<int>(ctx => maxIterations.Get(ctx)),
        };
        emitTestsFailed.SetDisplayText("Emit DEBUG.TESTS.FAILED");

        // 14. Record resolution (tests passed)
        var recordResolution = new RecordResolutionActivity
        {
            Id = "recordResolution",
            Name = "Record Resolution",
            SessionId = new Input<Guid>(ctx => sessionId.Get(ctx)),
            StoryId = new Input<string>(ctx => storyId.Get(ctx) ?? ""),
            DebugContextMode = new Input<string>(ctx => debugContextMode.Get(ctx) ?? ""),
            RootCause = new Input<string>(ctx => DescribeHypothesis(selectedHypothesisVar.Get(ctx)) ?? "unknown"),
            FixApproach = new Input<string>(ctx => selectedHypothesisVar.Get(ctx)?.SuggestedFix ?? "unknown"),
            FilesChangedJson = new Input<string>(ctx => allFilesModified.Get(ctx) ?? "[]"),
            Attempts = new Input<int>(ctx => currentIteration.Get(ctx)),
            StartTime = new Input<string>(ctx => debugStartTime.Get(ctx) ?? DateTime.UtcNow.ToString("o"))
        };
        recordResolution.SetDisplayText("Record Resolution");

        var updateCodeIndex = new UpdateCodeIndexActivity
        {
            Id = "UpdateCodeIndex",
            Name = "Update Code Index",
            ChangedFilesJson = new Input<string?>(ctx => allFilesModified.Get(ctx)),
            RepositoryPath = new Input<string?>(ctx => repositoryUrl.Get(ctx))
        };
        updateCodeIndex.SetDisplayText("Update Code Index");

        // #1 / #16: serialize a real DebugResult (status=Resolved) into debugResultJson.
        var serializeResolvedResult = new SetVariable<string>(debugResultJson,
            ctx => BuildResolvedResultJson(
                selectedHypothesisVar.Get(ctx),
                selectedHypothesisJson.Get(ctx),
                hypothesesJson.Get(ctx),
                allFilesModified.Get(ctx),
                currentIteration.Get(ctx),
                regressionTestWritten.Get(ctx)))
        { Id = "serializeResolvedResult", Name = "Serialize Resolved Result" };
        serializeResolvedResult.SetDisplayText("Serialize Resolved Result");

        var setResolvedStatus = new SetVariable<string>(debugStatus, _ => DebugStatus.Resolved.ToString())
        { Id = "setResolvedStatus", Name = "Set Resolved Status" };
        setResolvedStatus.SetDisplayText("Set Resolved Status");

        var emitResolved = new EmitDebugEventActivity
        {
            Id = "emitResolved", Name = "Emit DEBUG.RESOLVED.SUCCESS",
            EventType = new Input<string>(_ => DebugEvents.ResolvedSuccess),
            SessionId = new Input<string?>(ctx => sessionId.Get(ctx).ToString()),
            StoryId = new Input<string?>(ctx => storyId.Get(ctx)),
            Mode = new Input<string?>(ctx => debugContextMode.Get(ctx)),
            TenantId = new Input<string?>(ctx => tenantId.Get(ctx)),
            Iteration = new Input<int>(ctx => currentIteration.Get(ctx)),
            MaxIterations = new Input<int>(ctx => maxIterations.Get(ctx)),
            Hypothesis = new Input<string?>(ctx => DescribeHypothesis(selectedHypothesisVar.Get(ctx))),
        };
        emitResolved.SetDisplayText("Emit DEBUG.RESOLVED.SUCCESS");

        var setResolvedOutputs = new Sequence
        {
            Id = "setResolvedOutputs",
            Name = "Set Resolved Outputs",
            Activities =
            {
                WithLabel(new WriteLine("Debug resolved -- fix verified by tests") { Id = "setResolved", Name = "Log Resolved" }, "Log Resolved"),
                WithLabel(new SetOutput { Id = "outputResolvedSuccess", Name = "Output Resolved Success", OutputName = new("success"), OutputValue = new(ctx => (object)true) }, "Output Resolved Success"),
                WithLabel(new SetOutput { Id = "outputResolvedStatus", Name = "Output Resolved Status", OutputName = new("status"), OutputValue = new(ctx => (object)(debugStatus.Get(ctx) ?? DebugStatus.Resolved.ToString())) }, "Output Resolved Status"),
                WithLabel(new SetOutput { Id = "outputResolution", Name = "Output Resolution", OutputName = new("resolution"), OutputValue = new(ctx => (object)(debugResultJson.Get(ctx) ?? "{}")) }, "Output Resolution"),
                WithLabel(new SetOutput { Id = "outputResolvedIterations", Name = "Output Resolved Iterations", OutputName = new("iterations"), OutputValue = new(ctx => (object)currentIteration.Get(ctx)) }, "Output Resolved Iterations")
            }
        };
        setResolvedOutputs.SetDisplayText("Set Resolved Outputs");

        // 15. Test-fail branch: record outcome + attempt, then refine hypothesis.
        // #5: mark the tried hypothesis DidNotFix/MadeWorse + append a FixAttempt.
        var recordFailedAttempt = new SetVariable<string>(attemptsJson,
            ctx => AppendFailedAttempt(
                attemptsJson.Get(ctx),
                selectedHypothesisVar.Get(ctx),
                currentIteration.Get(ctx),
                testResults.Get(ctx),
                allFilesModified.Get(ctx)))
        { Id = "recordFailedAttempt", Name = "Record Failed Attempt" };
        recordFailedAttempt.SetDisplayText("Record Failed Attempt");

        // recordFailedAttempt updated hypothesesJson out-of-band; persist it too.
        var persistOutcomeHypotheses = new SetVariable<string>(hypothesesJson,
            ctx => MarkHypothesisOutcome(
                hypothesesJson.Get(ctx),
                selectedHypothesisVar.Get(ctx),
                collectTestsResult.Get(ctx),
                runTestsOutput.Get(ctx)))
        { Id = "persistOutcomeHypotheses", Name = "Persist Hypothesis Outcomes" };
        persistOutcomeHypotheses.SetDisplayText("Persist Hypothesis Outcomes");

        var refineHypothesis = new RefineHypothesisActivity
        {
            Id = "refineHypothesis",
            Name = "Refine Hypothesis",
            SessionId = new Input<Guid>(ctx => sessionId.Get(ctx)),
            TriedHypothesisJson = new Input<string>(ctx => selectedHypothesisJson.Get(ctx) ?? "{}"),
            TestResults = new Input<string>(ctx => testResults.Get(ctx) ?? ""),
            UpdatedErrors = new Input<string>(ctx => errorMessages.Get(ctx) ?? ""),
            IterationContextJson = new Input<string>(ctx => iterationContextJson.Get(ctx) ?? "{}"),
            Result = new Output<DiagnosisResult>(refinedDiagnosisVar)
        };
        refineHypothesis.SetDisplayText("Refine Hypothesis");

        // 15a. Serialize refined diagnosis to hypothesesJson and update iterationContextJson
        var serializeRefinedHypotheses = new SetVariable<string>(hypothesesJson,
            ctx =>
            {
                var result = refinedDiagnosisVar.Get(ctx);
                return result?.Hypotheses != null
                    ? JsonSerializer.Serialize(result.Hypotheses)
                    : "[]";
            })
        { Id = "serializeRefinedHypotheses", Name = "Serialize Refined Hypotheses" };
        serializeRefinedHypotheses.SetDisplayText("Serialize Refined Hypotheses");

        var updateIterationContext = new SetVariable<string>(iterationContextJson,
            ctx =>
            {
                var iterCtx = new DebugIterationContext
                {
                    CurrentIteration = currentIteration.Get(ctx),
                    Hypotheses = refinedDiagnosisVar.Get(ctx)?.Hypotheses ?? new List<Hypothesis>(),
                    PreviousAttempts = DeserializeAttempts(attemptsJson.Get(ctx)),
                    LatestTestResults = testResults.Get(ctx) ?? "",
                    LatestErrors = errorMessages.Get(ctx) ?? "",
                    AllFilesModified = DeserializeFiles(allFilesModified.Get(ctx)),
                    RegressionTestWritten = regressionTestWritten.Get(ctx)
                };
                return JsonSerializer.Serialize(iterCtx);
            })
        { Id = "updateIterationContext", Name = "Update Iteration Context" };
        updateIterationContext.SetDisplayText("Update Iteration Context");

        // 16. Increment iteration
        var incrementIteration = new SetVariable<int>(currentIteration,
            ctx => currentIteration.Get(ctx) + 1)
        { Id = "incrementIteration", Name = "Increment Iteration" };
        incrementIteration.SetDisplayText("Increment Iteration");

        // #12: graph-enforced loop bound -- explicit FlowDecision on currentIteration>maxIterations.
        var iterationsExhausted = new FlowDecision(ctx =>
            currentIteration.Get(ctx) > maxIterations.Get(ctx))
        { Id = "iterationsExhausted", Name = "Iterations Exhausted?" };
        iterationsExhausted.SetDisplayText("Iterations Exhausted?");

        var setMaxIterationsReason = new SetVariable<string>(escalationReason,
            _ => DebugEvents.ReasonMaxIterations)
        { Id = "setMaxIterationsReason", Name = "Set Max Iterations Reason" };
        setMaxIterationsReason.SetDisplayText("Set Max Iterations Reason");

        var setNoHypothesisReason = new SetVariable<string>(escalationReason,
            _ => DebugEvents.ReasonNoHypothesis)
        { Id = "setNoHypothesisReason", Name = "Set No-Hypothesis Reason" };
        setNoHypothesisReason.SetDisplayText("Set No-Hypothesis Reason");

        // 17. Compile debug report (escalation)
        var compileReport = new CompileDebugReportActivity
        {
            Id = "compileReport",
            Name = "Compile Debug Report",
            SessionId = new Input<Guid>(ctx => sessionId.Get(ctx)),
            StoryId = new Input<string>(ctx => storyId.Get(ctx) ?? ""),
            DebugContextMode = new Input<string>(ctx => debugContextMode.Get(ctx) ?? ""),
            HypothesesJson = new Input<string>(ctx => hypothesesJson.Get(ctx) ?? "[]"),
            AttemptsJson = new Input<string>(ctx => attemptsJson.Get(ctx) ?? "[]"),
            RemainingFailures = new Input<string>(ctx => testResults.Get(ctx) ?? ""),
            FilesInvestigated = new Input<string>(ctx => allFilesModified.Get(ctx) ?? "[]"),
            StartTime = new Input<string>(ctx => debugStartTime.Get(ctx) ?? DateTime.UtcNow.ToString("o")),
            Result = new Output<DebugReport>(compileReportResultVar)
        };
        compileReport.SetDisplayText("Compile Debug Report");

        var setEscalatedStatus = new SetVariable<string>(debugStatus, _ => DebugStatus.Escalated.ToString())
        { Id = "setEscalatedStatus", Name = "Set Escalated Status" };
        setEscalatedStatus.SetDisplayText("Set Escalated Status");

        // #1 / #16: serialize a real DebugResult (status=Escalated) into debugResultJson.
        var serializeEscalatedResult = new SetVariable<string>(debugResultJson,
            ctx => BuildEscalatedResultJson(
                compileReportResultVar.Get(ctx),
                hypothesesJson.Get(ctx),
                allFilesModified.Get(ctx),
                currentIteration.Get(ctx),
                regressionTestWritten.Get(ctx)))
        { Id = "serializeEscalatedResult", Name = "Serialize Escalated Result" };
        serializeEscalatedResult.SetDisplayText("Serialize Escalated Result");

        var emitEscalated = new EmitDebugEventActivity
        {
            Id = "emitEscalated", Name = "Emit DEBUG.ESCALATED",
            EventType = new Input<string>(_ => DebugEvents.Escalated),
            SessionId = new Input<string?>(ctx => sessionId.Get(ctx).ToString()),
            StoryId = new Input<string?>(ctx => storyId.Get(ctx)),
            Mode = new Input<string?>(ctx => debugContextMode.Get(ctx)),
            TenantId = new Input<string?>(ctx => tenantId.Get(ctx)),
            Iteration = new Input<int>(ctx => currentIteration.Get(ctx)),
            MaxIterations = new Input<int>(ctx => maxIterations.Get(ctx)),
            Reason = new Input<string?>(ctx =>
            {
                var r = escalationReason.Get(ctx);
                return string.IsNullOrEmpty(r) ? DebugEvents.ReasonMaxIterations : r;
            }),
        };
        emitEscalated.SetDisplayText("Emit DEBUG.ESCALATED");

        var setEscalatedOutputs = new Sequence
        {
            Id = "setEscalatedOutputs",
            Name = "Set Escalated Outputs",
            Activities =
            {
                WithLabel(new WriteLine("Debug ESCALATED -- could not resolve, report compiled") { Id = "setEscalated", Name = "Log Escalated" }, "Log Escalated"),
                WithLabel(new SetOutput { Id = "outputEscalatedSuccess", Name = "Output Escalated Success", OutputName = new("success"), OutputValue = new(ctx => (object)false) }, "Output Escalated Success"),
                WithLabel(new SetOutput { Id = "outputEscalatedStatus", Name = "Output Escalated Status", OutputName = new("status"), OutputValue = new(ctx => (object)(debugStatus.Get(ctx) ?? DebugStatus.Escalated.ToString())) }, "Output Escalated Status"),
                WithLabel(new SetOutput { Id = "outputDebugReport", Name = "Output Debug Report", OutputName = new("debugReport"), OutputValue = new(ctx => (object)(debugResultJson.Get(ctx) ?? "{}")) }, "Output Debug Report"),
                WithLabel(new SetOutput { Id = "outputEscalatedReason", Name = "Output Escalated Reason", OutputName = new("escalationReason"), OutputValue = new(ctx => (object)(escalationReason.Get(ctx) ?? DebugEvents.ReasonMaxIterations)) }, "Output Escalated Reason"),
                WithLabel(new SetOutput { Id = "outputEscalatedIterations", Name = "Output Escalated Iterations", OutputName = new("iterations"), OutputValue = new(ctx => (object)currentIteration.Get(ctx)) }, "Output Escalated Iterations")
            }
        };
        setEscalatedOutputs.SetDisplayText("Set Escalated Outputs");

        // 18. Final finish
        var finish = new Finish { Id = "finish", Name = "Complete: Debugging Done" };
        finish.SetDisplayText("Complete: Debugging Done");

        // ---- Build Flowchart ----
        builder.Root = new Flowchart
        {
            Id = "DebuggingFlowchart",
            Name = "Debugging Flowchart",
            Activities =
            {
                readInputs, initIteration, initMaxIterations,
                initFilesModified, initAttempts, initRegressionTest, initContextDone, initIterationContext,
                classify,
                tddEmphasis, runtimeEmphasis, bugEmphasis,
                emitSessionStarted,
                fork,
                collectErrors, collectCode, collectGit, collectTests, collectRepro, contextTimeout,
                join, contextGatherGate, contextGateSink, joinLog,
                serializeErrors, serializeCode, serializeGit, serializeTests, serializeRepro,
                aiDiagnosis, serializeDiagnosis, diagnosisProduced, emitDiagnosisSuccess, emitDiagnosisFailed,
                selectHypothesis, serializeSelectedHypothesis, hasHypothesis, emitHypothesisSelected,
                isBugMode, writeRegressionTest, captureRegressionFile, runRegressionTest,
                regressionFailsAsExpected, markRegressionTestWritten,
                setRegressionInvalidReason, emitRegressionInvalid,
                applyFix, captureFixFiles, fixApplied, emitFixAttempted,
                runTests, testsPass, emitTestsPassed, emitTestsFailed,
                recordResolution, updateCodeIndex, serializeResolvedResult, setResolvedStatus, emitResolved, setResolvedOutputs,
                recordFailedAttempt, persistOutcomeHypotheses,
                refineHypothesis, serializeRefinedHypotheses, updateIterationContext,
                incrementIteration, iterationsExhausted,
                setMaxIterationsReason, setNoHypothesisReason,
                compileReport, setEscalatedStatus, serializeEscalatedResult, emitEscalated, setEscalatedOutputs,
                finish
            },
            Connections =
            {
                // Initialization chain
                new(readInputs, initIteration),
                new(initIteration, initMaxIterations),
                new(initMaxIterations, initFilesModified),
                new(initFilesModified, initAttempts),
                new(initAttempts, initRegressionTest),
                new(initRegressionTest, initContextDone),
                new(initContextDone, initIterationContext),
                new(initIterationContext, classify),

                // Classification branches (FlowNode outcomes)
                new(new Endpoint(classify, "TddFailure"), new Endpoint(tddEmphasis)),
                new(new Endpoint(classify, "RuntimeError"), new Endpoint(runtimeEmphasis)),
                new(new Endpoint(classify, "BugInvestigation"), new Endpoint(bugEmphasis)),

                // All emphasis branches converge -> session started -> fork
                new(tddEmphasis, emitSessionStarted),
                new(runtimeEmphasis, emitSessionStarted),
                new(bugEmphasis, emitSessionStarted),
                new(emitSessionStarted, fork),

                // Fork branches to parallel collection activities + the timeout guard
                new(new Endpoint(fork, "CollectErrors"), new Endpoint(collectErrors)),
                new(new Endpoint(fork, "CollectCode"), new Endpoint(collectCode)),
                new(new Endpoint(fork, "CollectGit"), new Endpoint(collectGit)),
                new(new Endpoint(fork, "CollectTests"), new Endpoint(collectTests)),
                new(new Endpoint(fork, "CollectRepro"), new Endpoint(collectRepro)),
                new(new Endpoint(fork, "Timeout"), new Endpoint(contextTimeout)),

                // All 5 collection activities converge at FlowJoin (WaitAll)
                new(collectErrors, join),
                new(collectCode, join),
                new(collectGit, join),
                new(collectTests, join),
                new(collectRepro, join),

                // Join (all collected) AND timeout (partial) both funnel through the gate.
                new(join, contextGatherGate),
                new(new Endpoint(contextTimeout, "TimedOut"), new Endpoint(contextGatherGate)),
                new(new Endpoint(contextTimeout, "Armed"), new Endpoint(contextGateSink)),

                // Gate: first to arrive proceeds; the loser short-circuits.
                new(new Endpoint(contextGatherGate, "True"), new Endpoint(joinLog)),
                new(new Endpoint(contextGatherGate, "False"), new Endpoint(contextGateSink)),

                // Join -> log -> serialize collector outputs
                new(joinLog, serializeErrors),
                new(serializeErrors, serializeCode),
                new(serializeCode, serializeGit),
                new(serializeGit, serializeTests),
                new(serializeTests, serializeRepro),

                // Serialization -> AI Diagnosis -> serialize diagnosis
                new(serializeRepro, aiDiagnosis),
                new(aiDiagnosis, serializeDiagnosis),
                new(serializeDiagnosis, diagnosisProduced),

                // Diagnosis success/failed event, then select hypothesis
                new(new Endpoint(diagnosisProduced, "True"), new Endpoint(emitDiagnosisSuccess)),
                new(new Endpoint(diagnosisProduced, "False"), new Endpoint(emitDiagnosisFailed)),
                new(emitDiagnosisSuccess, selectHypothesis),
                // Diagnosis produced nothing -> still try select (will exhaust) -> escalate path.
                new(emitDiagnosisFailed, selectHypothesis),

                // Select Hypothesis -> serialize selected -> check
                new(selectHypothesis, serializeSelectedHypothesis),
                new(serializeSelectedHypothesis, hasHypothesis),

                // Has hypothesis? Yes -> emit selected -> bug-mode guard
                new(new Endpoint(hasHypothesis, "True"), new Endpoint(emitHypothesisSelected)),
                new(emitHypothesisSelected, isBugMode),

                // Has hypothesis? No -> set reason -> escalate
                new(new Endpoint(hasHypothesis, "False"), new Endpoint(setNoHypothesisReason)),
                new(setNoHypothesisReason, compileReport),

                // Bug mode? Yes -> write regression test -> capture file -> run it -> guard
                new(new Endpoint(isBugMode, "True"), new Endpoint(writeRegressionTest)),
                new(writeRegressionTest, captureRegressionFile),
                new(captureRegressionFile, runRegressionTest),
                new(runRegressionTest, regressionFailsAsExpected),

                // Regression FAILS as expected -> mark written -> apply fix
                new(new Endpoint(regressionFailsAsExpected, "True"), new Endpoint(markRegressionTestWritten)),
                new(markRegressionTestWritten, applyFix),

                // Regression PASSES (does not reproduce) -> escalate (AC7)
                new(new Endpoint(regressionFailsAsExpected, "False"), new Endpoint(setRegressionInvalidReason)),
                new(setRegressionInvalidReason, emitRegressionInvalid),
                new(emitRegressionInvalid, compileReport),

                // Bug mode? No -> apply fix directly
                new(new Endpoint(isBugMode, "False"), new Endpoint(applyFix)),

                // Apply fix -> capture files -> emit attempted -> check success
                new(applyFix, captureFixFiles),
                new(captureFixFiles, emitFixAttempted),
                new(emitFixAttempted, fixApplied),

                // Fix applied? Yes -> run tests. No -> treat as failed attempt -> refine.
                new(new Endpoint(fixApplied, "True"), new Endpoint(runTests)),
                new(new Endpoint(fixApplied, "False"), new Endpoint(recordFailedAttempt)),

                // Run tests -> check results
                new(runTests, testsPass),

                // Tests pass? Yes -> emit passed -> record resolution
                new(new Endpoint(testsPass, "True"), new Endpoint(emitTestsPassed)),
                new(emitTestsPassed, recordResolution),
                new(recordResolution, updateCodeIndex),
                new(updateCodeIndex, serializeResolvedResult),
                new(serializeResolvedResult, setResolvedStatus),
                new(setResolvedStatus, emitResolved),
                new(emitResolved, setResolvedOutputs),
                new(setResolvedOutputs, finish),

                // Tests pass? No -> emit failed -> record attempt -> outcomes -> refine
                new(new Endpoint(testsPass, "False"), new Endpoint(emitTestsFailed)),
                new(emitTestsFailed, recordFailedAttempt),
                new(recordFailedAttempt, persistOutcomeHypotheses),
                new(persistOutcomeHypotheses, refineHypothesis),
                new(refineHypothesis, serializeRefinedHypotheses),
                new(serializeRefinedHypotheses, updateIterationContext),
                new(updateIterationContext, incrementIteration),

                // #12: graph-enforced loop bound on increment.
                new(incrementIteration, iterationsExhausted),
                new(new Endpoint(iterationsExhausted, "True"), new Endpoint(setMaxIterationsReason)),
                new(setMaxIterationsReason, compileReport),
                // Within budget -> select next hypothesis (loop back)
                new(new Endpoint(iterationsExhausted, "False"), new Endpoint(selectHypothesis)),

                // Escalation path -> status -> serialize -> emit -> outputs
                new(compileReport, setEscalatedStatus),
                new(setEscalatedStatus, serializeEscalatedResult),
                new(serializeEscalatedResult, emitEscalated),
                new(emitEscalated, setEscalatedOutputs),
                new(setEscalatedOutputs, finish)
            }
        };
    }

    // ====================================================================
    // Pure helpers (no Elsa context) — file/attempt/result building.
    // ====================================================================

    private static string? DescribeHypothesis(Hypothesis? h)
        => string.IsNullOrEmpty(h?.Description) ? null : h!.Description;

    /// <summary>Merge a single file path into the JSON list var (dedup, drop empties).</summary>
    private static string MergeFiles(string? existingJson, string? newFile)
    {
        var list = DeserializeFiles(existingJson);
        if (!string.IsNullOrWhiteSpace(newFile) && !list.Contains(newFile))
            list.Add(newFile);
        return JsonSerializer.Serialize(list);
    }

    /// <summary>Merge a set of file paths into the JSON list var (dedup, drop empties).</summary>
    private static string MergeFiles(string? existingJson, List<string>? newFiles)
    {
        var list = DeserializeFiles(existingJson);
        if (newFiles != null)
        {
            foreach (var f in newFiles)
                if (!string.IsNullOrWhiteSpace(f) && !list.Contains(f))
                    list.Add(f);
        }
        return JsonSerializer.Serialize(list);
    }

    private static List<string> DeserializeFiles(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<string>();
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>(); }
        catch { return new List<string>(); }
    }

    private static List<FixAttempt> DeserializeAttempts(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<FixAttempt>();
        try { return JsonSerializer.Deserialize<List<FixAttempt>>(json) ?? new List<FixAttempt>(); }
        catch { return new List<FixAttempt>(); }
    }

    private static List<Hypothesis> DeserializeHypotheses(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new List<Hypothesis>();
        try { return JsonSerializer.Deserialize<List<Hypothesis>>(json) ?? new List<Hypothesis>(); }
        catch { return new List<Hypothesis>(); }
    }

    /// <summary>
    /// #5: classify a failed attempt's outcome. MadeWorse when the failing-test count
    /// after the fix exceeds the pre-fix baseline (collectTests context); otherwise DidNotFix.
    /// </summary>
    private static HypothesisOutcome ClassifyOutcome(
        TestResultsContext? baseline, IDictionary<string, object>? testOutput)
    {
        var baselineFailing = baseline?.FailingTests ?? 0;
        var afterFailing = ExtractFailingCount(testOutput);
        if (afterFailing > baselineFailing && baselineFailing >= 0 && afterFailing >= 0)
            return HypothesisOutcome.MadeWorse;
        return HypothesisOutcome.DidNotFix;
    }

    private static int ExtractFailingCount(IDictionary<string, object>? testOutput)
    {
        if (testOutput == null) return -1;
        foreach (var key in new[] { "failingTests", "failedTests", "failures" })
        {
            if (testOutput.TryGetValue(key, out var v))
            {
                if (v is int i) return i;
                if (v is long l) return (int)l;
                if (int.TryParse(v?.ToString(), out var p)) return p;
            }
        }
        return -1;
    }

    /// <summary>
    /// #5: re-serialize hypothesesJson with the tried hypothesis's Outcome/FixAttempted set,
    /// so CompileDebugReport and the iteration context carry real outcomes.
    /// </summary>
    private static string MarkHypothesisOutcome(
        string? hypothesesJson, Hypothesis? tried,
        TestResultsContext? baseline, IDictionary<string, object>? testOutput)
    {
        var list = DeserializeHypotheses(hypothesesJson);
        if (tried != null)
        {
            var outcome = ClassifyOutcome(baseline, testOutput);
            var match = list.FirstOrDefault(h =>
                string.Equals(h.Description, tried.Description, StringComparison.Ordinal));
            if (match != null)
            {
                match.Outcome = outcome;
                match.FixAttempted = tried.SuggestedFix;
                match.FailureReason = outcome == HypothesisOutcome.MadeWorse
                    ? "Fix increased the failing-test count"
                    : "Fix did not make the tests pass";
            }
        }
        return JsonSerializer.Serialize(list);
    }

    /// <summary>
    /// #5: append a FixAttempt record (this iteration) to attemptsJson. The
    /// outcome-marking of the tried hypothesis is persisted separately by
    /// <see cref="MarkHypothesisOutcome"/> (the workflow's persistOutcomeHypotheses step).
    /// </summary>
    private static string AppendFailedAttempt(
        string? attemptsJson, Hypothesis? tried,
        int iteration, string? testResults, string? filesJson)
    {
        var attempts = DeserializeAttempts(attemptsJson);
        attempts.Add(new FixAttempt
        {
            Iteration = iteration,
            HypothesisDescription = tried?.Description ?? "unknown",
            Approach = tried?.SuggestedFix ?? "unknown",
            TestResult = Truncate(testResults, 2000),
            Resolved = false,
            FilesModified = DeserializeFiles(filesJson),
            StartedAt = DateTime.UtcNow
        });
        return JsonSerializer.Serialize(attempts);
    }

    private static string Truncate(string? s, int max)
        => string.IsNullOrEmpty(s) ? "" : (s!.Length <= max ? s : s[..max]);

    /// <summary>#1: build the resolved DebugResult JSON.</summary>
    private static string BuildResolvedResultJson(
        Hypothesis? selected, string? selectedJson, string? hypothesesJson,
        string? filesJson, int iteration, bool regressionAdded)
    {
        // Mark the winning hypothesis FixedIssue in the surfaced list.
        var hypotheses = DeserializeHypotheses(hypothesesJson);
        if (selected != null)
        {
            var match = hypotheses.FirstOrDefault(h =>
                string.Equals(h.Description, selected.Description, StringComparison.Ordinal));
            if (match != null) match.Outcome = HypothesisOutcome.FixedIssue;
            else { selected.Outcome = HypothesisOutcome.FixedIssue; hypotheses.Insert(0, selected); }
        }

        var result = new DebugResult
        {
            Status = DebugStatus.Resolved,
            RootCause = selected?.Description ?? "unknown",
            FixApplied = selected?.SuggestedFix ?? "",
            Attempts = iteration,
            Hypotheses = hypotheses,
            RegressionTestAdded = regressionAdded,
            FilesChanged = DeserializeFiles(filesJson),
            DebugReport = ""
        };
        return JsonSerializer.Serialize(result);
    }

    /// <summary>#1: build the escalated DebugResult JSON from the compiled report.</summary>
    private static string BuildEscalatedResultJson(
        DebugReport? report, string? hypothesesJson,
        string? filesJson, int iteration, bool regressionAdded)
    {
        var hypotheses = report?.AllHypotheses != null && report.AllHypotheses.Count > 0
            ? report.AllHypotheses
            : DeserializeHypotheses(hypothesesJson);

        var result = new DebugResult
        {
            Status = DebugStatus.Escalated,
            RootCause = "",
            FixApplied = "",
            Attempts = iteration,
            Hypotheses = hypotheses,
            RegressionTestAdded = regressionAdded,
            FilesChanged = report?.FilesInvestigated != null && report.FilesInvestigated.Count > 0
                ? report.FilesInvestigated
                : DeserializeFiles(filesJson),
            DebugReport = report?.ReportText ?? ""
        };
        return JsonSerializer.Serialize(result);
    }
}
