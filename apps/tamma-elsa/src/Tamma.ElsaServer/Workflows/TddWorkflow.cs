using Elsa.Expressions.Models;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Elsa.Workflows.Management.Activities.SetOutput;
using Tamma.Activities.TDD;
using Tamma.Activities.TDD.Models;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;
using FlowConnection = Elsa.Workflows.Activities.Flowchart.Models.Connection;
using TaskStatus = Tamma.Activities.TDD.Models.TaskStatus;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// TDD Cycle workflow: drives the red-green-refactor TDD cycle for a single task.
/// Called in a loop from the main workflow's START_IMPLEMENTATION state.
///
/// Flow:
///   Init -> RED phase (WriteTests -> RunTests -> CheckTestsFail guard)
///     -> GREEN phase (WriteImplementation -> RunFullTests -> debug loop if failing)
///     -> REFACTOR phase (AnalyzeCode -> optionally ApplyRefactoring -> verify tests)
///     -> CommitChanges -> SetOutputs
/// </summary>
public class TddWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "TDD Cycle";
        builder.DefinitionId = "tdd-cycle";
        builder.Description = "Drives the red-green-refactor TDD cycle for a single implementation task";

        // -- Workflow variables --
        var sessionId = builder.WithVariable<Guid>();
        var storyId = builder.WithVariable<string>();
        var taskDescription = builder.WithVariable<string>();
        var taskFiles = builder.WithVariable<List<string>>();
        var repositoryUrl = builder.WithVariable<string>();
        var branchName = builder.WithVariable<string>();
        var skillLevel = builder.WithVariable<int>();
        var codeContext = builder.WithVariable<string>();

        // Activity output variables (bound via Output<T>)
        var testGenResult = builder.WithVariable<TestGenerationResult>();
        var implResult = builder.WithVariable<ImplementationResult>();
        var analysisResult = builder.WithVariable<RefactoringAnalysis>();
        var refactorResult = builder.WithVariable<RefactoringResult>();
        var commitResultVar = builder.WithVariable<CommitResult>();

        // Phase tracking scalars
        var rewriteAttempt = builder.WithVariable<int>();
        var debugAttempt = builder.WithVariable<int>();
        var debuggingInvoked = builder.WithVariable<bool>();
        var testRunAllPassed = builder.WithVariable<bool>();
        var testRunPassedCount = builder.WithVariable<int>();
        var testRunFailedCount = builder.WithVariable<int>();
        var refactorApplied = builder.WithVariable<bool>();

        // Phase timestamps
        var redPhaseStart = builder.WithVariable<DateTime>();
        var greenPhaseStart = builder.WithVariable<DateTime>();
        var refactorPhaseStart = builder.WithVariable<DateTime>();

        // ============================
        // Activity definitions
        // ============================

        // --- INIT: Capture inputs ---
        var setSessionId = Assign(sessionId, ctx => (object)ctx.GetInput<Guid>("sessionId"), "SetSessionId", "Capture Session ID");
        var setStoryId = Assign(storyId, ctx => (object)(ctx.GetInput<string>("storyId") ?? ""), "SetStoryId", "Capture Story ID");
        var setTaskDescription = Assign(taskDescription, ctx => (object)(ctx.GetInput<string>("taskDescription") ?? ""), "SetTaskDescription", "Capture Task Description");
        var setTaskFiles = Assign(taskFiles, ctx => (object)(ctx.GetInput<List<string>>("taskFiles") ?? new List<string>()), "SetTaskFiles", "Capture Task Files");
        var setRepositoryUrl = Assign(repositoryUrl, ctx => (object)(ctx.GetInput<string>("repositoryUrl") ?? ""), "SetRepositoryUrl", "Capture Repository URL");
        var setBranchName = Assign(branchName, ctx => (object)(ctx.GetInput<string>("branchName") ?? ""), "SetBranchName", "Capture Branch Name");
        var setSkillLevel = Assign(skillLevel, ctx => (object)ctx.GetInput<int>("skillLevel"), "SetSkillLevel", "Capture Skill Level");
        var initRewriteAttempt = Assign(rewriteAttempt, _ => (object)0, "InitRewriteAttempt", "Initialize Rewrite Counter");
        var initDebugAttempt = Assign(debugAttempt, _ => (object)0, "InitDebugAttempt", "Initialize Debug Counter");
        var initDebuggingInvoked = Assign(debuggingInvoked, _ => (object)false, "InitDebuggingInvoked", "Initialize Debugging Flag");
        var initRefactorApplied = Assign(refactorApplied, _ => (object)false, "InitRefactorApplied", "Initialize Refactor Flag");

        // --- RED PHASE ---
        var logRedPhaseStart = Assign(redPhaseStart, _ => (object)DateTime.UtcNow, "LogRedPhaseStart", "Log RED Phase Start");

        // WriteTests: output bound to testGenResult variable
        var writeTests = new WriteTestsActivity
        {
            Id = "WriteTests",
            Name = "Write Tests",
            SessionId = new Input<Guid>(ctx => sessionId.Get(ctx)),
            StoryId = new Input<string>(ctx => storyId.Get(ctx)),
            TaskDescription = new Input<string>(ctx => taskDescription.Get(ctx)),
            TaskFiles = new Input<List<string>>(ctx => taskFiles.Get(ctx)),
            CodeContext = new Input<string?>(ctx => codeContext.Get(ctx)),
            SkillLevel = new Input<int>(ctx => skillLevel.Get(ctx)),
            IsRewrite = new Input<bool>(ctx => rewriteAttempt.Get(ctx) > 0),
            PreviousTestCode = new Input<string?>(ctx =>
            {
                var gen = testGenResult.Get(ctx);
                return gen?.TestCode;
            }),
            Result = new Output<TestGenerationResult>(testGenResult)
        };

        // TODO: Replace mock test runs with DispatchWorkflow calls to testing-pipeline (7-1C)
        // Mock: simulate running new tests (tests FAIL = correct TDD)
        var mockNewTestsFail = Assign(testRunAllPassed, _ => (object)false, "MockNewTestsFail", "Mock: New Tests Fail");
        var mockNewTestsFailCount = Assign(testRunFailedCount, ctx =>
        {
            var gen = testGenResult.Get(ctx);
            return (object)(gen?.TestCount ?? 2);
        }, "MockNewTestsFailCount", "Mock: Set Failed Count");
        var mockNewTestsPassCount = Assign(testRunPassedCount, _ => (object)0, "MockNewTestsPassCount", "Mock: Set Passed Count");

        // RED phase guard
        var checkTestsFail = new CheckTestsFailActivity
        {
            Id = "CheckTestsFail",
            Name = "Check Tests Fail",
            SessionId = new Input<Guid>(ctx => sessionId.Get(ctx)),
            TestRunResult = new Input<TestRunResult>(ctx =>
            {
                var gen = testGenResult.Get(ctx);
                return new TestRunResult
                {
                    AllPassed = testRunAllPassed.Get(ctx),
                    TotalTests = gen?.TestCount ?? 0,
                    PassedTests = testRunPassedCount.Get(ctx),
                    FailedTests = testRunFailedCount.Get(ctx),
                    FailureMessages = new List<string> { "Not yet implemented" }
                };
            }),
            RewriteAttempt = new Input<int>(ctx => rewriteAttempt.Get(ctx)),
            MaxRewriteAttempts = new Input<int>(2)
        };

        var incrementRewrite = Assign(rewriteAttempt, ctx => (object)(rewriteAttempt.Get(ctx) + 1), "IncrRewrite", "Increment Rewrite Attempt");

        // If tests pass AND max rewrites exhausted -> proceed anyway
        var maxRewritesCheck = new FlowDecision(ctx => rewriteAttempt.Get(ctx) >= 2)
        { Id = "MaxRewritesCheck", Name = "Max Rewrites?" };

        // --- GREEN PHASE ---
        var logGreenPhaseStart = Assign(greenPhaseStart, _ => (object)DateTime.UtcNow, "LogGreenPhaseStart", "Log GREEN Phase Start");

        var writeImplementation = new WriteImplementationActivity
        {
            Id = "WriteImplementation",
            Name = "Write Implementation",
            SessionId = new Input<Guid>(ctx => sessionId.Get(ctx)),
            StoryId = new Input<string>(ctx => storyId.Get(ctx)),
            TaskDescription = new Input<string>(ctx => taskDescription.Get(ctx)),
            TestCode = new Input<string>(ctx =>
            {
                var gen = testGenResult.Get(ctx);
                return gen?.TestCode ?? "";
            }),
            TestFailureOutput = new Input<string?>(ctx => (string?)null),
            CodeContext = new Input<string?>(ctx => codeContext.Get(ctx)),
            SkillLevel = new Input<int>(ctx => skillLevel.Get(ctx)),
            Result = new Output<ImplementationResult>(implResult)
        };

        // TODO: Replace mock test runs with DispatchWorkflow calls to testing-pipeline (7-1C)
        // Mock: simulate full test suite passing
        var mockFullTestsPass = Assign(testRunAllPassed, _ => (object)true, "MockFullTestsPass", "Mock: Full Tests Pass");
        var mockFullTestsPassedCount = Assign(testRunPassedCount, ctx =>
        {
            var gen = testGenResult.Get(ctx);
            return (object)((gen?.TestCount ?? 0) + 10);
        }, "MockFullTestsPassedCount", "Mock: Full Tests Passed Count");
        var mockFullTestsFailedCount = Assign(testRunFailedCount, _ => (object)0, "MockFullTestsFailedCount", "Mock: Full Tests Failed Count");

        var greenTestsPassCheck = new FlowDecision(ctx => testRunAllPassed.Get(ctx))
        { Id = "GreenTestsPassCheck", Name = "Green Tests Pass?" };

        // Debug loop
        var markDebug = Assign(debuggingInvoked, _ => (object)true, "MarkDebug", "Mark Debug Invoked");
        var incrementDebug = Assign(debugAttempt, ctx => (object)(debugAttempt.Get(ctx) + 1), "IncrDebug", "Increment Debug Attempt");
        var maxDebugCheck = new FlowDecision(ctx => debugAttempt.Get(ctx) >= 3)
        { Id = "MaxDebugCheck", Name = "Max Debug?" };

        // --- REFACTOR PHASE ---
        var logRefactorPhaseStart = Assign(refactorPhaseStart, _ => (object)DateTime.UtcNow, "LogRefactorPhaseStart", "Log REFACTOR Phase Start");

        var analyzeCode = new AnalyzeCodeActivity
        {
            Id = "AnalyzeCode",
            Name = "Analyze Code",
            SessionId = new Input<Guid>(ctx => sessionId.Get(ctx)),
            StoryId = new Input<string>(ctx => storyId.Get(ctx)),
            TestCode = new Input<string>(ctx =>
            {
                var gen = testGenResult.Get(ctx);
                return gen?.TestCode ?? "";
            }),
            ImplementationCode = new Input<string>(ctx =>
            {
                var impl = implResult.Get(ctx);
                return impl?.ImplementationCode ?? "";
            }),
            SkillLevel = new Input<int>(ctx => skillLevel.Get(ctx)),
            ConfidenceThreshold = new Input<double>(0.6),
            Result = new Output<RefactoringAnalysis>(analysisResult)
        };

        var refactoringNeededCheck = new FlowDecision(ctx =>
        {
            var analysis = analysisResult.Get(ctx);
            return analysis != null && analysis.HasSuggestions && analysis.Confidence >= 0.6;
        })
        { Id = "RefactoringNeededCheck", Name = "Refactoring Needed?" };

        var applyRefactoring = new ApplyRefactoringActivity
        {
            Id = "ApplyRefactoring",
            Name = "Apply Refactoring",
            SessionId = new Input<Guid>(ctx => sessionId.Get(ctx)),
            StoryId = new Input<string>(ctx => storyId.Get(ctx)),
            ImplementationCode = new Input<string>(ctx =>
            {
                var impl = implResult.Get(ctx);
                return impl?.ImplementationCode ?? "";
            }),
            TestCode = new Input<string>(ctx =>
            {
                var gen = testGenResult.Get(ctx);
                return gen?.TestCode ?? "";
            }),
            Suggestions = new Input<List<RefactoringSuggestion>>(ctx =>
            {
                var analysis = analysisResult.Get(ctx);
                return analysis?.Suggestions ?? new List<RefactoringSuggestion>();
            }),
            SkillLevel = new Input<int>(ctx => skillLevel.Get(ctx)),
            Result = new Output<RefactoringResult>(refactorResult)
        };

        var markRefactored = Assign(refactorApplied, _ => (object)true, "MarkRefactored", "Mark Refactoring Applied");

        // TODO: Replace mock test runs with DispatchWorkflow calls to testing-pipeline (7-1C)
        // Mock: refactored tests pass
        var mockRefactorTestsPass = Assign(testRunAllPassed, _ => (object)true, "MockRefactorTestsPass", "Mock: Refactor Tests Pass");

        var refactorTestsPassCheck = new FlowDecision(ctx => testRunAllPassed.Get(ctx))
        { Id = "RefactorTestsPassCheck", Name = "Refactor Tests Pass?" };

        var revertRefactoring = new RevertRefactoringActivity
        {
            Id = "RevertRefactoring",
            Name = "Revert Refactoring",
            SessionId = new Input<Guid>(ctx => sessionId.Get(ctx)),
            RepositoryUrl = new Input<string>(ctx => repositoryUrl.Get(ctx)),
            BranchName = new Input<string>(ctx => branchName.Get(ctx)),
            FilesToRevert = new Input<List<string>>(ctx =>
            {
                var rf = refactorResult.Get(ctx);
                return rf?.FilesChanged ?? new List<string>();
            })
        };

        // --- COMMIT ---
        var commitChanges = new CommitChangesActivity
        {
            Id = "CommitChanges",
            Name = "Commit Changes",
            SessionId = new Input<Guid>(ctx => sessionId.Get(ctx)),
            StoryId = new Input<string>(ctx => storyId.Get(ctx)),
            TaskDescription = new Input<string>(ctx => taskDescription.Get(ctx)),
            RepositoryUrl = new Input<string>(ctx => repositoryUrl.Get(ctx)),
            BranchName = new Input<string>(ctx => branchName.Get(ctx)),
            TestFiles = new Input<List<string>>(ctx =>
            {
                var gen = testGenResult.Get(ctx);
                return gen?.TestFiles ?? new List<string>();
            }),
            ImplementationFiles = new Input<List<string>>(ctx =>
            {
                var impl = implResult.Get(ctx);
                return impl?.ImplementationFiles ?? new List<string>();
            }),
            Result = new Output<CommitResult>(commitResultVar)
        };

        // --- OUTPUT (SetOutput sequences) ---
        var setCompletedOutputs = new Sequence
        {
            Id = "SetCompletedOutputs",
            Name = "Set Completed Outputs",
            Activities =
            {
                new SetOutput { Id = "SetOutputSuccess", Name = "Set Success", OutputName = new("success"), OutputValue = new(ctx => (object)true) },
                new SetOutput { Id = "SetOutputTestCount", Name = "Set Test Count", OutputName = new("testCount"), OutputValue = new(ctx => (object)(testGenResult.Get(ctx)?.TestCount ?? 0)) },
                new SetOutput { Id = "SetOutputCommitSha", Name = "Set Commit SHA", OutputName = new("commitSha"), OutputValue = new(ctx => (object)(commitResultVar.Get(ctx)?.CommitSha ?? "")) },
                new SetOutput { Id = "SetOutputFilesChanged", Name = "Set Files Changed", OutputName = new("filesChanged"), OutputValue = new(ctx => {
                    var impl = implResult.Get(ctx);
                    var gen = testGenResult.Get(ctx);
                    var files = new List<string>();
                    if (gen?.TestFiles != null) files.AddRange(gen.TestFiles);
                    if (impl?.ImplementationFiles != null) files.AddRange(impl.ImplementationFiles);
                    return (object)System.Text.Json.JsonSerializer.Serialize(files);
                }) }
            }
        };

        var setFailedOutputs = new Sequence
        {
            Id = "SetFailedOutputs",
            Name = "Set Failed Outputs",
            Activities =
            {
                new SetOutput { Id = "SetOutputFailed", Name = "Set Failed", OutputName = new("success"), OutputValue = new(ctx => (object)false) },
                new SetOutput { Id = "SetOutputErrorMessage", Name = "Set Error Message", OutputName = new("errorMessage"), OutputValue = new(ctx => (object)$"GREEN phase failed after {debugAttempt.Get(ctx)} debug iterations") }
            }
        };

        var finish = new Finish { Id = "FinishSuccess", Name = "Finish Success" };
        var finishFailed = new Finish { Id = "FinishFailed", Name = "Finish Failed" };

        // ============================
        // Flowchart
        // ============================
        builder.Root = new Flowchart
        {
            Id = "TddCycleFlowchart",
            Name = "TDD Cycle Flowchart",
            Activities =
            {
                // Init
                setSessionId, setStoryId, setTaskDescription, setTaskFiles,
                setRepositoryUrl, setBranchName, setSkillLevel,
                initRewriteAttempt, initDebugAttempt, initDebuggingInvoked, initRefactorApplied,

                // RED phase
                logRedPhaseStart,
                writeTests,
                mockNewTestsFail, mockNewTestsFailCount, mockNewTestsPassCount,
                checkTestsFail,
                incrementRewrite, maxRewritesCheck,

                // GREEN phase
                logGreenPhaseStart,
                writeImplementation,
                mockFullTestsPass, mockFullTestsPassedCount, mockFullTestsFailedCount,
                greenTestsPassCheck,
                markDebug, incrementDebug, maxDebugCheck,

                // REFACTOR phase
                logRefactorPhaseStart,
                analyzeCode,
                refactoringNeededCheck,
                applyRefactoring, markRefactored,
                mockRefactorTestsPass,
                refactorTestsPassCheck,
                revertRefactoring,

                // Commit
                commitChanges,

                // Outputs
                setCompletedOutputs, setFailedOutputs,
                finish, finishFailed
            },

            Connections =
            {
                // --- INIT chain ---
                Connect(setSessionId, setStoryId),
                Connect(setStoryId, setTaskDescription),
                Connect(setTaskDescription, setTaskFiles),
                Connect(setTaskFiles, setRepositoryUrl),
                Connect(setRepositoryUrl, setBranchName),
                Connect(setBranchName, setSkillLevel),
                Connect(setSkillLevel, initRewriteAttempt),
                Connect(initRewriteAttempt, initDebugAttempt),
                Connect(initDebugAttempt, initDebuggingInvoked),
                Connect(initDebuggingInvoked, initRefactorApplied),
                Connect(initRefactorApplied, logRedPhaseStart),

                // --- RED PHASE ---
                Connect(logRedPhaseStart, writeTests),
                Connect(writeTests, mockNewTestsFail),
                Connect(mockNewTestsFail, mockNewTestsFailCount),
                Connect(mockNewTestsFailCount, mockNewTestsPassCount),
                Connect(mockNewTestsPassCount, checkTestsFail),

                // "TestsFail" (correct TDD) -> GREEN
                ConnectOutcome(checkTestsFail, "TestsFail", logGreenPhaseStart),
                // "TestsPass" (bad tests) -> check max rewrites
                ConnectOutcome(checkTestsFail, "TestsPass", maxRewritesCheck),

                // Max rewrites exhausted (True) -> GREEN anyway (pre-implemented)
                ConnectOutcome(maxRewritesCheck, "True", logGreenPhaseStart),
                // More attempts (False) -> increment and loop
                ConnectOutcome(maxRewritesCheck, "False", incrementRewrite),
                Connect(incrementRewrite, writeTests),

                // --- GREEN PHASE ---
                Connect(logGreenPhaseStart, writeImplementation),
                Connect(writeImplementation, mockFullTestsPass),
                Connect(mockFullTestsPass, mockFullTestsPassedCount),
                Connect(mockFullTestsPassedCount, mockFullTestsFailedCount),
                Connect(mockFullTestsFailedCount, greenTestsPassCheck),

                // Tests pass (True) -> REFACTOR
                ConnectOutcome(greenTestsPassCheck, "True", logRefactorPhaseStart),
                // Tests fail (False) -> debug loop
                ConnectOutcome(greenTestsPassCheck, "False", markDebug),
                Connect(markDebug, incrementDebug),
                Connect(incrementDebug, maxDebugCheck),

                // Max debug (True) -> FAILED
                ConnectOutcome(maxDebugCheck, "True", setFailedOutputs),
                // More debug (False) -> retry
                ConnectOutcome(maxDebugCheck, "False", writeImplementation),

                // --- REFACTOR PHASE ---
                Connect(logRefactorPhaseStart, analyzeCode),
                Connect(analyzeCode, refactoringNeededCheck),

                // No refactoring (False) -> commit
                ConnectOutcome(refactoringNeededCheck, "False", commitChanges),
                // Refactoring (True) -> apply
                ConnectOutcome(refactoringNeededCheck, "True", applyRefactoring),
                Connect(applyRefactoring, markRefactored),
                Connect(markRefactored, mockRefactorTestsPass),
                Connect(mockRefactorTestsPass, refactorTestsPassCheck),

                // Refactored pass (True) -> commit
                ConnectOutcome(refactorTestsPassCheck, "True", commitChanges),
                // Refactored fail (False) -> revert then commit
                ConnectOutcome(refactorTestsPassCheck, "False", revertRefactoring),
                Connect(revertRefactoring, commitChanges),

                // --- COMMIT & OUTPUT ---
                Connect(commitChanges, setCompletedOutputs),
                Connect(setCompletedOutputs, finish),

                // --- FAILED ---
                Connect(setFailedOutputs, finishFailed)
            }
        };
    }

    /// <summary>
    /// Create a SetVariable with proper Input&lt;object?&gt; boxing.
    /// </summary>
    private static SetVariable Assign(Variable variable, Func<ExpressionExecutionContext, object?> valueFunc, string? id = null, string? name = null)
    {
        var activityId = id ?? Guid.NewGuid().ToString("N")[..8];
        return new SetVariable
        {
            Id = activityId,
            Name = name ?? activityId,
            Variable = variable,
            Value = new Input<object?>(valueFunc)
        };
    }

    /// <summary>
    /// Create a simple flowchart connection between two activities.
    /// </summary>
    private static FlowConnection Connect(IActivity source, IActivity target)
    {
        return new FlowConnection(new FlowEndpoint(source), new FlowEndpoint(target));
    }

    /// <summary>
    /// Create a connection from a specific outcome of an activity to another activity.
    /// </summary>
    private static FlowConnection ConnectOutcome(IActivity source, string outcome, IActivity target)
    {
        return new FlowConnection(new FlowEndpoint(source, outcome), new FlowEndpoint(target));
    }
}
