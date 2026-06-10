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
///   1. ClassifyDebugContext -> route by mode
///   2. FlowFork: parallel context gathering (error messages, code, git, tests, repro steps)
///   3. FlowJoin (WaitAll)
///   4. Serialize collector outputs to string variables
///   5. AIDiagnosis -> ranked hypotheses
///   6. Debug loop (max 5 iterations):
///      a. Select highest-confidence untried hypothesis
///      b. Apply fix (mode-specific: TDD/Runtime/Bug)
///      c. Run tests
///      d. Pass -> RecordResolution -> done
///      e. Fail -> RefineHypothesis -> loop
///   7. Max iterations -> CompileDebugReport -> escalate
///
/// Invoked as child workflow via RunWorkflow or standalone via ELSA REST API.
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
        var regressionTestWritten = builder.WithVariable<bool>();
        var runTestsOutput = builder.WithVariable<IDictionary<string, object>?>();

        // ---- Activities ----

        // 1. Initialize variables from workflow inputs
        var initialize = new SetVariable<string>(debugStartTime,
            _ => DateTime.UtcNow.ToString("o"))
        { Id = "initialize", Name = "Initialize Start Time" };
        initialize.SetDisplayText("Initialize Start Time");

        var initIteration = new SetVariable<int>(currentIteration, _ => 1)
        { Id = "initIteration", Name = "Initialize Iteration" };
        initIteration.SetDisplayText("Initialize Iteration");

        var initMaxIterations = new SetVariable<int>(maxIterations, _ => 5)
        { Id = "initMaxIterations", Name = "Initialize Max Iterations" };
        initMaxIterations.SetDisplayText("Initialize Max Iterations");

        var initFilesModified = new SetVariable<string>(allFilesModified, _ => "[]")
        { Id = "initFilesModified", Name = "Initialize Files Modified" };
        initFilesModified.SetDisplayText("Initialize Files Modified");

        var initRegressionTest = new SetVariable<bool>(regressionTestWritten, _ => false)
        { Id = "initRegressionTest", Name = "Initialize Regression Test Flag" };
        initRegressionTest.SetDisplayText("Initialize Regression Test Flag");

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

        // 5. FlowFork for parallel context gathering (branch names used in connections)
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
                "CollectRepro"
            })
        };
        fork.SetDisplayText("Context Fork");

        // 6. FlowJoin -- waits for all parallel branches to complete
        var join = new FlowJoin
        {
            Id = "contextJoin",
            Name = "Context Join",
            Mode = new Input<FlowJoinMode>(FlowJoinMode.WaitAll)
        };
        join.SetDisplayText("Context Join");

        var joinLog = new WriteLine("All debug context gathered -- proceeding to serialization")
        { Id = "joinLog", Name = "Join Log" };
        joinLog.SetDisplayText("Join Log");

        // 6a. Serialize typed collector outputs to string variables for AIDiagnosis
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

        // 7. AI Diagnosis -- reads from serialized string variables, outputs typed result
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
            BranchName = new Input<string>(ctx => branchName.Get(ctx) ?? "")
        };
        writeRegressionTest.SetDisplayText("Write Regression Test");

        var markRegressionTestWritten = new SetVariable<bool>(regressionTestWritten, _ => true)
        { Id = "markRegressionTestWritten", Name = "Mark Regression Test Written" };
        markRegressionTestWritten.SetDisplayText("Mark Regression Test Written");

        // 11. Apply fix via LLM call sub-workflow
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
                ["sessionId"] = sessionId.Get(ctx).ToString()
            }),
            WaitForCompletion = new(true)
        };
        applyFix.SetDisplayText("Apply Fix");

        // 12. Run tests via testing-pipeline sub-workflow
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
                ["SkillLevel"] = skillLevel.Get(ctx)
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

        // 14. Record resolution (tests passed)
        var recordResolution = new RecordResolutionActivity
        {
            Id = "recordResolution",
            Name = "Record Resolution",
            SessionId = new Input<Guid>(ctx => sessionId.Get(ctx)),
            StoryId = new Input<string>(ctx => storyId.Get(ctx) ?? ""),
            DebugContextMode = new Input<string>(ctx => debugContextMode.Get(ctx) ?? ""),
            RootCause = new Input<string>(ctx =>
            {
                try
                {
                    var h = JsonSerializer.Deserialize<Hypothesis>(selectedHypothesisJson.Get(ctx) ?? "{}");
                    return h?.Description ?? "unknown";
                }
                catch { return "unknown"; }
            }),
            FixApproach = new Input<string>(ctx =>
            {
                try
                {
                    var h = JsonSerializer.Deserialize<Hypothesis>(selectedHypothesisJson.Get(ctx) ?? "{}");
                    return h?.SuggestedFix ?? "unknown";
                }
                catch { return "unknown"; }
            }),
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

        var setResolvedOutputs = new Sequence
        {
            Id = "setResolvedOutputs",
            Name = "Set Resolved Outputs",
            Activities =
            {
                WithLabel(new WriteLine("Debug resolved -- fix verified by tests") { Id = "setResolved", Name = "Log Resolved" }, "Log Resolved"),
                WithLabel(new SetOutput { Id = "outputResolvedSuccess", Name = "Output Resolved Success", OutputName = new("success"), OutputValue = new(ctx => (object)true) }, "Output Resolved Success"),
                WithLabel(new SetOutput { Id = "outputResolution", Name = "Output Resolution", OutputName = new("resolution"), OutputValue = new(ctx => (object)(debugResultJson.Get(ctx) ?? "{}")) }, "Output Resolution"),
                WithLabel(new SetOutput { Id = "outputResolvedIterations", Name = "Output Resolved Iterations", OutputName = new("iterations"), OutputValue = new(ctx => (object)currentIteration.Get(ctx)) }, "Output Resolved Iterations")
            }
        };
        setResolvedOutputs.SetDisplayText("Set Resolved Outputs");

        // 15. Refine hypothesis (tests failed) -- outputs typed DiagnosisResult
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
                    LatestTestResults = testResults.Get(ctx) ?? "",
                    LatestErrors = errorMessages.Get(ctx) ?? ""
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

        // 17. Compile debug report (escalation)
        var compileReport = new CompileDebugReportActivity
        {
            Id = "compileReport",
            Name = "Compile Debug Report",
            SessionId = new Input<Guid>(ctx => sessionId.Get(ctx)),
            StoryId = new Input<string>(ctx => storyId.Get(ctx) ?? ""),
            DebugContextMode = new Input<string>(ctx => debugContextMode.Get(ctx) ?? ""),
            HypothesesJson = new Input<string>(ctx => hypothesesJson.Get(ctx) ?? "[]"),
            AttemptsJson = new Input<string>(ctx => iterationContextJson.Get(ctx) ?? "[]"),
            RemainingFailures = new Input<string>(ctx => testResults.Get(ctx) ?? ""),
            FilesInvestigated = new Input<string>(ctx => allFilesModified.Get(ctx) ?? "[]"),
            StartTime = new Input<string>(ctx => debugStartTime.Get(ctx) ?? DateTime.UtcNow.ToString("o"))
        };
        compileReport.SetDisplayText("Compile Debug Report");

        var setEscalatedOutputs = new Sequence
        {
            Id = "setEscalatedOutputs",
            Name = "Set Escalated Outputs",
            Activities =
            {
                WithLabel(new WriteLine("Debug ESCALATED -- max iterations reached, report compiled") { Id = "setEscalated", Name = "Log Escalated" }, "Log Escalated"),
                WithLabel(new SetOutput { Id = "outputEscalatedSuccess", Name = "Output Escalated Success", OutputName = new("success"), OutputValue = new(ctx => (object)false) }, "Output Escalated Success"),
                WithLabel(new SetOutput { Id = "outputDebugReport", Name = "Output Debug Report", OutputName = new("debugReport"), OutputValue = new(ctx => (object)(debugResultJson.Get(ctx) ?? "{}")) }, "Output Debug Report"),
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
                initialize, initIteration, initMaxIterations,
                initFilesModified, initRegressionTest, initIterationContext,
                classify,
                tddEmphasis, runtimeEmphasis, bugEmphasis,
                fork,
                collectErrors, collectCode, collectGit, collectTests, collectRepro,
                join, joinLog,
                serializeErrors, serializeCode, serializeGit, serializeTests, serializeRepro,
                aiDiagnosis, serializeDiagnosis,
                selectHypothesis, serializeSelectedHypothesis, hasHypothesis,
                isBugMode, writeRegressionTest, markRegressionTestWritten,
                applyFix, runTests, testsPass,
                recordResolution, updateCodeIndex, setResolvedOutputs,
                refineHypothesis, serializeRefinedHypotheses, updateIterationContext,
                incrementIteration,
                compileReport, setEscalatedOutputs,
                finish
            },
            Connections =
            {
                // Initialization chain
                new(initialize, initIteration),
                new(initIteration, initMaxIterations),
                new(initMaxIterations, initFilesModified),
                new(initFilesModified, initRegressionTest),
                new(initRegressionTest, initIterationContext),
                new(initIterationContext, classify),

                // Classification branches (FlowNode outcomes)
                new(new Endpoint(classify, "TddFailure"), new Endpoint(tddEmphasis)),
                new(new Endpoint(classify, "RuntimeError"), new Endpoint(runtimeEmphasis)),
                new(new Endpoint(classify, "BugInvestigation"), new Endpoint(bugEmphasis)),

                // All emphasis branches converge at fork
                new(tddEmphasis, fork),
                new(runtimeEmphasis, fork),
                new(bugEmphasis, fork),

                // Fork branches to parallel collection activities
                new(new Endpoint(fork, "CollectErrors"), new Endpoint(collectErrors)),
                new(new Endpoint(fork, "CollectCode"), new Endpoint(collectCode)),
                new(new Endpoint(fork, "CollectGit"), new Endpoint(collectGit)),
                new(new Endpoint(fork, "CollectTests"), new Endpoint(collectTests)),
                new(new Endpoint(fork, "CollectRepro"), new Endpoint(collectRepro)),

                // All collection activities converge at FlowJoin
                new(collectErrors, join),
                new(collectCode, join),
                new(collectGit, join),
                new(collectTests, join),
                new(collectRepro, join),

                // Join -> log -> serialize collector outputs
                new(join, joinLog),
                new(joinLog, serializeErrors),
                new(serializeErrors, serializeCode),
                new(serializeCode, serializeGit),
                new(serializeGit, serializeTests),
                new(serializeTests, serializeRepro),

                // Serialization -> AI Diagnosis -> serialize diagnosis
                new(serializeRepro, aiDiagnosis),
                new(aiDiagnosis, serializeDiagnosis),

                // Serialize diagnosis -> Select Hypothesis -> serialize selected
                new(serializeDiagnosis, selectHypothesis),
                new(selectHypothesis, serializeSelectedHypothesis),

                // Serialize selected -> check if we have a hypothesis
                new(serializeSelectedHypothesis, hasHypothesis),

                // Has hypothesis? Yes -> check if bug mode needs regression test
                new(new Endpoint(hasHypothesis, "True"), new Endpoint(isBugMode)),

                // Has hypothesis? No -> escalate
                new(new Endpoint(hasHypothesis, "False"), new Endpoint(compileReport)),

                // Bug mode? Yes -> write regression test
                new(new Endpoint(isBugMode, "True"), new Endpoint(writeRegressionTest)),
                new(writeRegressionTest, markRegressionTestWritten),
                new(markRegressionTestWritten, applyFix),

                // Bug mode? No -> apply fix directly
                new(new Endpoint(isBugMode, "False"), new Endpoint(applyFix)),

                // Apply fix -> run tests
                new(applyFix, runTests),

                // Run tests -> check results
                new(runTests, testsPass),

                // Tests pass? Yes -> record resolution
                new(new Endpoint(testsPass, "True"), new Endpoint(recordResolution)),
                new(recordResolution, updateCodeIndex),
                new(updateCodeIndex, setResolvedOutputs),
                new(setResolvedOutputs, finish),

                // Tests pass? No -> refine hypothesis -> serialize -> update context -> increment -> loop
                new(new Endpoint(testsPass, "False"), new Endpoint(refineHypothesis)),
                new(refineHypothesis, serializeRefinedHypotheses),
                new(serializeRefinedHypotheses, updateIterationContext),
                new(updateIterationContext, incrementIteration),

                // Loop back: increment -> select next hypothesis
                new(incrementIteration, selectHypothesis),

                // Escalation path
                new(compileReport, setEscalatedOutputs),
                new(setEscalatedOutputs, finish)
            }
        };
    }
}
