using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Activities.Flowchart.Models;
using Elsa.Workflows.Contracts;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime.Activities;
using System.Text.Json;
using Tamma.Activities.Debug;
using Tamma.Activities.Debug.Models;
using Endpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;

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
///   4. AIDiagnosis -> ranked hypotheses
///   5. Debug loop (max 5 iterations):
///      a. Select highest-confidence untried hypothesis
///      b. Apply fix (mode-specific: TDD/Runtime/Bug)
///      c. Run tests
///      d. Pass -> RecordResolution -> done
///      e. Fail -> RefineHypothesis -> loop
///   6. Max iterations -> CompileDebugReport -> escalate
///
/// Invoked as child workflow via RunWorkflow or standalone via ELSA REST API.
/// </summary>
public class DebuggingWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Debugging";
        builder.DefinitionId = "debugging";

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

        // Gathered context variables
        var errorMessages = builder.WithVariable<string>();
        var relevantCode = builder.WithVariable<string>();
        var gitHistory = builder.WithVariable<string>();
        var testResults = builder.WithVariable<string>();
        var reproductionSteps = builder.WithVariable<string>();

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
        { Id = "initialize" };

        var initIteration = new SetVariable<int>(currentIteration, _ => 1)
        { Id = "initIteration" };

        var initMaxIterations = new SetVariable<int>(maxIterations, _ => 5)
        { Id = "initMaxIterations" };

        var initFilesModified = new SetVariable<string>(allFilesModified, _ => "[]")
        { Id = "initFilesModified" };

        var initRegressionTest = new SetVariable<bool>(regressionTestWritten, _ => false)
        { Id = "initRegressionTest" };

        var initIterationContext = new SetVariable<string>(iterationContextJson,
            _ => "{\"currentIteration\":0,\"hypotheses\":[],\"previousAttempts\":[]}")
        { Id = "initIterationContext" };

        // 2. Classify debug context
        var classify = new ClassifyDebugContextActivity
        {
            Id = "classifyContext",
            DebugContextMode = new Input<string>(ctx => debugContextMode.Get(ctx) ?? "RuntimeError"),
            SessionId = new Input<Guid>(ctx => sessionId.Get(ctx))
        };

        // 3. Context-specific emphasis logging (one per branch, all converge)
        var tddEmphasis = new WriteLine("Debug mode: TDD Failure -- emphasizing test output and implementation code")
        { Id = "tddEmphasis" };

        var runtimeEmphasis = new WriteLine("Debug mode: Runtime Error -- emphasizing stack traces and recent changes")
        { Id = "runtimeEmphasis" };

        var bugEmphasis = new WriteLine("Debug mode: Bug Investigation -- emphasizing issue description and reproduction steps")
        { Id = "bugEmphasis" };

        // 4. Parallel context gathering activities
        var collectErrors = new CollectErrorMessagesActivity
        {
            Id = "collectErrors",
            ErrorOutput = new Input<string>(ctx => errorOutput.Get(ctx) ?? ""),
            DebugContextMode = new Input<string>(ctx => debugContextMode.Get(ctx) ?? "RuntimeError"),
            RepositoryUrl = new Input<string>(ctx => repositoryUrl.Get(ctx) ?? ""),
            BranchName = new Input<string>(ctx => branchName.Get(ctx) ?? "")
        };

        var collectCode = new CollectRelevantCodeActivity
        {
            Id = "collectCode",
            RelevantFiles = new Input<List<string>?>(ctx =>
            {
                var files = relevantFiles.Get(ctx);
                if (string.IsNullOrEmpty(files)) return null;
                try { return JsonSerializer.Deserialize<List<string>>(files); }
                catch { return new List<string> { files }; }
            }),
            RepositoryUrl = new Input<string>(ctx => repositoryUrl.Get(ctx) ?? ""),
            BranchName = new Input<string>(ctx => branchName.Get(ctx) ?? ""),
            DebugContextMode = new Input<string>(ctx => debugContextMode.Get(ctx) ?? "RuntimeError")
        };

        var collectGit = new CollectGitHistoryActivity
        {
            Id = "collectGit",
            RepositoryUrl = new Input<string>(ctx => repositoryUrl.Get(ctx) ?? ""),
            BranchName = new Input<string>(ctx => branchName.Get(ctx) ?? ""),
            DebugContextMode = new Input<string>(ctx => debugContextMode.Get(ctx) ?? "RuntimeError")
        };

        var collectTests = new CollectTestResultsActivity
        {
            Id = "collectTests",
            RepositoryUrl = new Input<string>(ctx => repositoryUrl.Get(ctx) ?? ""),
            BranchName = new Input<string>(ctx => branchName.Get(ctx) ?? ""),
            DebugContextMode = new Input<string>(ctx => debugContextMode.Get(ctx) ?? "RuntimeError"),
            ErrorOutput = new Input<string>(ctx => errorOutput.Get(ctx) ?? "")
        };

        var collectRepro = new CollectReproductionStepsActivity
        {
            Id = "collectRepro",
            IssueDescription = new Input<string>(ctx => issueDescription.Get(ctx) ?? ""),
            DebugContextMode = new Input<string>(ctx => debugContextMode.Get(ctx) ?? "RuntimeError")
        };

        // 5. FlowFork for parallel context gathering (branch names used in connections)
        var fork = new FlowFork
        {
            Id = "contextFork",
            Branches = new Input<ICollection<string>>(new List<string>
            {
                "CollectErrors",
                "CollectCode",
                "CollectGit",
                "CollectTests",
                "CollectRepro"
            })
        };

        // 6. FlowJoin -- waits for all parallel branches to complete
        var join = new FlowJoin
        {
            Id = "contextJoin",
            Mode = new Input<FlowJoinMode>(FlowJoinMode.WaitAll)
        };

        var joinLog = new WriteLine("All debug context gathered -- proceeding to AI diagnosis")
        { Id = "joinLog" };

        // 7. AI Diagnosis
        var aiDiagnosis = new AIDiagnosisActivity
        {
            Id = "aiDiagnosis",
            SessionId = new Input<Guid>(ctx => sessionId.Get(ctx)),
            DebugContextMode = new Input<string>(ctx => debugContextMode.Get(ctx) ?? "RuntimeError"),
            ErrorContext = new Input<string>(ctx => errorMessages.Get(ctx) ?? ""),
            CodeContext = new Input<string>(ctx => relevantCode.Get(ctx) ?? ""),
            GitContext = new Input<string>(ctx => gitHistory.Get(ctx) ?? ""),
            TestContext = new Input<string>(ctx => testResults.Get(ctx) ?? ""),
            ReproductionContext = new Input<string>(ctx => reproductionSteps.Get(ctx) ?? ""),
            PreviousContext = new Input<string?>(ctx => iterationContextJson.Get(ctx)),
            SkillLevel = new Input<int>(ctx => skillLevel.Get(ctx))
        };

        // 8. Select hypothesis
        var selectHypothesis = new SelectHypothesisActivity
        {
            Id = "selectHypothesis",
            HypothesesJson = new Input<string>(ctx => hypothesesJson.Get(ctx) ?? "[]"),
            CurrentIteration = new Input<int>(ctx => currentIteration.Get(ctx)),
            MaxIterations = new Input<int>(ctx => maxIterations.Get(ctx))
        };

        // 9. Check if hypothesis was selected (not null/exhausted)
        var hasHypothesis = new FlowDecision(ctx =>
        {
            var json = selectedHypothesisJson.Get(ctx);
            return !string.IsNullOrEmpty(json) && json != "null";
        })
        { Id = "hasHypothesis" };

        // 10. BugInvestigation guard: write regression test if needed
        var isBugMode = new FlowDecision(ctx =>
            debugContextMode.Get(ctx) == "BugInvestigation" && !regressionTestWritten.Get(ctx))
        { Id = "isBugMode" };

        var writeRegressionTest = new WriteRegressionTestActivity
        {
            Id = "writeRegressionTest",
            SessionId = new Input<Guid>(ctx => sessionId.Get(ctx)),
            StoryId = new Input<string>(ctx => storyId.Get(ctx) ?? ""),
            BugDescription = new Input<string>(ctx => issueDescription.Get(ctx) ?? ""),
            HypothesisJson = new Input<string>(ctx => selectedHypothesisJson.Get(ctx) ?? "{}"),
            CodeContext = new Input<string>(ctx => relevantCode.Get(ctx) ?? ""),
            RepositoryUrl = new Input<string>(ctx => repositoryUrl.Get(ctx) ?? ""),
            BranchName = new Input<string>(ctx => branchName.Get(ctx) ?? "")
        };

        var markRegressionTestWritten = new SetVariable<bool>(regressionTestWritten, _ => true)
        { Id = "markRegressionTestWritten" };

        // 11. Apply fix via LLM call sub-workflow
        var applyFix = new DispatchWorkflow
        {
            Id = "applyFix",
            WorkflowDefinitionId = new("llm-call"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["agentRole"] = "implementer",
                ["taskPrompt"] = $"Apply fix for hypothesis: {selectedHypothesisJson.Get(ctx) ?? "unknown"} (mode: {debugContextMode.Get(ctx)}, iteration: {currentIteration.Get(ctx)})",
                ["sessionId"] = sessionId.Get(ctx).ToString()
            }),
            WaitForCompletion = new(true)
        };

        // 12. Run tests via testing-pipeline sub-workflow
        var runTests = new DispatchWorkflow
        {
            Id = "runTests",
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

        // 13. Check test results from DispatchWorkflow output
        var testsPass = new FlowDecision(ctx =>
        {
            var output = runTestsOutput.Get(ctx);
            if (output != null && output.TryGetValue("passed", out var p) && p is bool passed)
                return passed;
            return false;
        })
        { Id = "testsPass" };

        // 14. Record resolution (tests passed)
        var recordResolution = new RecordResolutionActivity
        {
            Id = "recordResolution",
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

        var setResolvedOutputs = new Sequence
        {
            Activities =
            {
                new WriteLine("Debug resolved -- fix verified by tests") { Id = "setResolved" },
                new SetOutput { OutputName = new("success"), OutputValue = new(ctx => (object)true) },
                new SetOutput { OutputName = new("resolution"), OutputValue = new(ctx => (object)(debugResultJson.Get(ctx) ?? "{}")) },
                new SetOutput { OutputName = new("iterations"), OutputValue = new(ctx => (object)currentIteration.Get(ctx)) }
            }
        };

        // 15. Refine hypothesis (tests failed)
        var refineHypothesis = new RefineHypothesisActivity
        {
            Id = "refineHypothesis",
            SessionId = new Input<Guid>(ctx => sessionId.Get(ctx)),
            TriedHypothesisJson = new Input<string>(ctx => selectedHypothesisJson.Get(ctx) ?? "{}"),
            TestResults = new Input<string>(ctx => testResults.Get(ctx) ?? ""),
            UpdatedErrors = new Input<string>(ctx => errorMessages.Get(ctx) ?? ""),
            IterationContextJson = new Input<string>(ctx => iterationContextJson.Get(ctx) ?? "{}")
        };

        // 16. Increment iteration
        var incrementIteration = new SetVariable<int>(currentIteration,
            ctx => currentIteration.Get(ctx) + 1)
        { Id = "incrementIteration" };

        // 17. Compile debug report (escalation)
        var compileReport = new CompileDebugReportActivity
        {
            Id = "compileReport",
            SessionId = new Input<Guid>(ctx => sessionId.Get(ctx)),
            StoryId = new Input<string>(ctx => storyId.Get(ctx) ?? ""),
            DebugContextMode = new Input<string>(ctx => debugContextMode.Get(ctx) ?? ""),
            HypothesesJson = new Input<string>(ctx => hypothesesJson.Get(ctx) ?? "[]"),
            AttemptsJson = new Input<string>(ctx => iterationContextJson.Get(ctx) ?? "[]"),
            RemainingFailures = new Input<string>(ctx => testResults.Get(ctx) ?? ""),
            FilesInvestigated = new Input<string>(ctx => allFilesModified.Get(ctx) ?? "[]"),
            StartTime = new Input<string>(ctx => debugStartTime.Get(ctx) ?? DateTime.UtcNow.ToString("o"))
        };

        var setEscalatedOutputs = new Sequence
        {
            Activities =
            {
                new WriteLine("Debug ESCALATED -- max iterations reached, report compiled") { Id = "setEscalated" },
                new SetOutput { OutputName = new("success"), OutputValue = new(ctx => (object)false) },
                new SetOutput { OutputName = new("debugReport"), OutputValue = new(ctx => (object)(debugResultJson.Get(ctx) ?? "{}")) },
                new SetOutput { OutputName = new("iterations"), OutputValue = new(ctx => (object)currentIteration.Get(ctx)) }
            }
        };

        // 18. Final finish
        var finish = new Finish { Id = "finish" };

        // ---- Build Flowchart ----
        builder.Root = new Flowchart
        {
            Activities =
            {
                initialize, initIteration, initMaxIterations,
                initFilesModified, initRegressionTest, initIterationContext,
                classify,
                tddEmphasis, runtimeEmphasis, bugEmphasis,
                fork,
                collectErrors, collectCode, collectGit, collectTests, collectRepro,
                join, joinLog,
                aiDiagnosis,
                selectHypothesis, hasHypothesis,
                isBugMode, writeRegressionTest, markRegressionTestWritten,
                applyFix, runTests, testsPass,
                recordResolution, setResolvedOutputs,
                refineHypothesis, incrementIteration,
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

                // Join -> log -> AI Diagnosis
                new(join, joinLog),
                new(joinLog, aiDiagnosis),

                // AI Diagnosis -> Select Hypothesis
                new(aiDiagnosis, selectHypothesis),

                // Select -> check if we have a hypothesis
                new(selectHypothesis, hasHypothesis),

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
                new(recordResolution, setResolvedOutputs),
                new(setResolvedOutputs, finish),

                // Tests pass? No -> refine hypothesis
                new(new Endpoint(testsPass, "False"), new Endpoint(refineHypothesis)),
                new(refineHypothesis, incrementIteration),

                // Loop back: increment -> select next hypothesis
                new(incrementIteration, selectHypothesis),

                // Escalation path
                new(compileReport, setEscalatedOutputs),
                new(setEscalatedOutputs, finish)
            }
        };
    }
}
