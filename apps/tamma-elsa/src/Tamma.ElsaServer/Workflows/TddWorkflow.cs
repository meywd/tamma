using Elsa.Expressions.Models;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Runtime.Activities;
using Tamma.Activities.CodeIndex;
using Tamma.Activities.TDD;
using Tamma.Activities.TDD.Models;
using Tamma.Activities.Testing.Models;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;
using FlowConnection = Elsa.Workflows.Activities.Flowchart.Models.Connection;
using TaskStatus = Tamma.Activities.TDD.Models.TaskStatus;

using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

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
        builder.Version = WorkflowVersions.ComputedVersion;
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
        var testSyntaxResult = builder.WithVariable<TestSyntaxValidationResult>();
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

        // Captures the result dictionary returned by DispatchWorkflow("testing-pipeline").
        // Reused across RED, GREEN, and REFACTOR phases (each dispatch overwrites it
        // before the corresponding result-extraction SetVariable runs).
        var testingPipelineResult = builder.WithVariable<IDictionary<string, object>?>();

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
        writeTests.SetDisplayText("Write Tests");

        // Validate test syntax BEFORE dispatching the testing-pipeline. This
        // closes the validateTestSyntax() AC from story 2-5: catch obviously
        // broken test files (compile/parse errors) before burning cycles
        // running them. The validator is best-effort — if no compiler is on
        // PATH the activity records the language as "skipped" and we proceed.
        var validateTestSyntax = new ValidateTestSyntaxActivity
        {
            Id = "ValidateTestSyntax",
            Name = "Validate Test Syntax",
            SessionId = new Input<Guid>(ctx => sessionId.Get(ctx)),
            TestGeneration = new Input<TestGenerationResult>(ctx =>
                testGenResult.Get(ctx) ?? new TestGenerationResult()),
            Result = new Output<TestSyntaxValidationResult>(testSyntaxResult)
        };
        validateTestSyntax.SetDisplayText("Validate Test Syntax");

        // True = invalid syntax → fall through to a finish step that fails the
        // workflow. False = valid (or all-skipped) → continue to RED dispatch.
        var testSyntaxValidCheck = new FlowDecision(ctx =>
        {
            var r = testSyntaxResult.Get(ctx);
            return r != null && !r.IsValid;
        })
        { Id = "TestSyntaxValidCheck", Name = "Test Syntax Invalid?" };
        testSyntaxValidCheck.SetDisplayText("Test Syntax Invalid?");

        // RED phase: dispatch testing-pipeline to run the newly written tests against
        // the (not-yet-implemented) target code. Per TDD, tests SHOULD fail here —
        // CheckTestsFail downstream routes "TestsPass" back to a rewrite loop.
        var dispatchTestsRed = new DispatchWorkflow
        {
            Id = "DispatchTestsRed",
            Name = "Run New Tests (RED)",
            WorkflowDefinitionId = new("testing-pipeline"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["SessionId"] = sessionId.Get(ctx),
                ["Repository"] = repositoryUrl.Get(ctx),
                ["Branch"] = branchName.Get(ctx),
                ["SkillLevel"] = skillLevel.Get(ctx)
            }),
            WaitForCompletion = new(true),
            Result = new(testingPipelineResult)
        };
        dispatchTestsRed.SetDisplayText("Run New Tests (RED)");

        var setRedTestsAllPassed = Assign(testRunAllPassed,
            ctx => (object)ExtractPassed(testingPipelineResult.Get(ctx)),
            "SetRedTestsAllPassed", "Capture RED: AllPassed");
        var setRedFailedCount = Assign(testRunFailedCount,
            ctx => (object)ExtractFailedCount(testingPipelineResult.Get(ctx),
                fallback: testGenResult.Get(ctx)?.TestCount ?? 0),
            "SetRedFailedCount", "Capture RED: Failed Count");
        var setRedPassedCount = Assign(testRunPassedCount,
            ctx => (object)ExtractPassedCount(testingPipelineResult.Get(ctx), fallback: 0),
            "SetRedPassedCount", "Capture RED: Passed Count");

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
        checkTestsFail.SetDisplayText("Check Tests Fail");

        var incrementRewrite = Assign(rewriteAttempt, ctx => (object)(rewriteAttempt.Get(ctx) + 1), "IncrRewrite", "Increment Rewrite Attempt");

        // If tests pass AND max rewrites exhausted -> proceed anyway
        var maxRewritesCheck = new FlowDecision(ctx => rewriteAttempt.Get(ctx) >= 2)
        { Id = "MaxRewritesCheck", Name = "Max Rewrites?" };
        maxRewritesCheck.SetDisplayText("Max Rewrites?");

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
        writeImplementation.SetDisplayText("Write Implementation");

        // GREEN phase: dispatch testing-pipeline to run the FULL test suite against
        // the now-implemented code. Tests should pass; failure routes to the debug loop.
        var dispatchTestsGreen = new DispatchWorkflow
        {
            Id = "DispatchTestsGreen",
            Name = "Run Full Test Suite (GREEN)",
            WorkflowDefinitionId = new("testing-pipeline"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["SessionId"] = sessionId.Get(ctx),
                ["Repository"] = repositoryUrl.Get(ctx),
                ["Branch"] = branchName.Get(ctx),
                ["SkillLevel"] = skillLevel.Get(ctx)
            }),
            WaitForCompletion = new(true),
            Result = new(testingPipelineResult)
        };
        dispatchTestsGreen.SetDisplayText("Run Full Test Suite (GREEN)");

        var setGreenTestsAllPassed = Assign(testRunAllPassed,
            ctx => (object)ExtractPassed(testingPipelineResult.Get(ctx)),
            "SetGreenTestsAllPassed", "Capture GREEN: AllPassed");
        var setGreenPassedCount = Assign(testRunPassedCount,
            ctx => (object)ExtractPassedCount(testingPipelineResult.Get(ctx), fallback: 0),
            "SetGreenPassedCount", "Capture GREEN: Passed Count");
        var setGreenFailedCount = Assign(testRunFailedCount,
            ctx => (object)ExtractFailedCount(testingPipelineResult.Get(ctx), fallback: 0),
            "SetGreenFailedCount", "Capture GREEN: Failed Count");

        var greenTestsPassCheck = new FlowDecision(ctx => testRunAllPassed.Get(ctx))
        { Id = "GreenTestsPassCheck", Name = "Green Tests Pass?" };
        greenTestsPassCheck.SetDisplayText("Green Tests Pass?");

        // Debug loop
        var markDebug = Assign(debuggingInvoked, _ => (object)true, "MarkDebug", "Mark Debug Invoked");
        var incrementDebug = Assign(debugAttempt, ctx => (object)(debugAttempt.Get(ctx) + 1), "IncrDebug", "Increment Debug Attempt");
        var maxDebugCheck = new FlowDecision(ctx => debugAttempt.Get(ctx) >= 3)
        { Id = "MaxDebugCheck", Name = "Max Debug?" };
        maxDebugCheck.SetDisplayText("Max Debug?");

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
        analyzeCode.SetDisplayText("Analyze Code");

        var refactoringNeededCheck = new FlowDecision(ctx =>
        {
            var analysis = analysisResult.Get(ctx);
            return analysis != null && analysis.HasSuggestions && analysis.Confidence >= 0.6;
        })
        { Id = "RefactoringNeededCheck", Name = "Refactoring Needed?" };
        refactoringNeededCheck.SetDisplayText("Refactoring Needed?");

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
        applyRefactoring.SetDisplayText("Apply Refactoring");

        var markRefactored = Assign(refactorApplied, _ => (object)true, "MarkRefactored", "Mark Refactoring Applied");

        // REFACTOR phase: re-run testing-pipeline after applying refactoring.
        // If tests still pass, commit the refactored code; otherwise revert.
        var dispatchTestsRefactor = new DispatchWorkflow
        {
            Id = "DispatchTestsRefactor",
            Name = "Run Tests After Refactor",
            WorkflowDefinitionId = new("testing-pipeline"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["SessionId"] = sessionId.Get(ctx),
                ["Repository"] = repositoryUrl.Get(ctx),
                ["Branch"] = branchName.Get(ctx),
                ["SkillLevel"] = skillLevel.Get(ctx)
            }),
            WaitForCompletion = new(true),
            Result = new(testingPipelineResult)
        };
        dispatchTestsRefactor.SetDisplayText("Run Tests After Refactor");

        var setRefactorTestsAllPassed = Assign(testRunAllPassed,
            ctx => (object)ExtractPassed(testingPipelineResult.Get(ctx)),
            "SetRefactorTestsAllPassed", "Capture REFACTOR: AllPassed");

        var refactorTestsPassCheck = new FlowDecision(ctx => testRunAllPassed.Get(ctx))
        { Id = "RefactorTestsPassCheck", Name = "Refactor Tests Pass?" };
        refactorTestsPassCheck.SetDisplayText("Refactor Tests Pass?");

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
        revertRefactoring.SetDisplayText("Revert Refactoring");

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
        commitChanges.SetDisplayText("Commit Changes");

        // --- UPDATE CODE INDEX (fire-and-forget) ---
        var updateCodeIndex = new UpdateCodeIndexActivity
        {
            Id = "UpdateCodeIndex",
            Name = "Update Code Index",
            ChangedFilesJson = new Input<string?>(ctx =>
            {
                var commit = commitResultVar.Get(ctx);
                return commit?.FilesCommitted != null
                    ? System.Text.Json.JsonSerializer.Serialize(commit.FilesCommitted)
                    : null;
            }),
            RepositoryPath = new Input<string?>(ctx => repositoryUrl.Get(ctx))
        };
        updateCodeIndex.SetDisplayText("Update Code Index");

        // --- OUTPUT (SetOutput sequences) ---
        var setCompletedOutputs = new Sequence
        {
            Id = "SetCompletedOutputs",
            Name = "Set Completed Outputs",
            Activities =
            {
                WithLabel(new SetOutput { Id = "SetOutputSuccess", Name = "Set Success", OutputName = new("success"), OutputValue = new(ctx => (object)true) }, "Set Success"),
                WithLabel(new SetOutput { Id = "SetOutputTestCount", Name = "Set Test Count", OutputName = new("testCount"), OutputValue = new(ctx => (object)(testGenResult.Get(ctx)?.TestCount ?? 0)) }, "Set Test Count"),
                WithLabel(new SetOutput { Id = "SetOutputCommitSha", Name = "Set Commit SHA", OutputName = new("commitSha"), OutputValue = new(ctx => (object)(commitResultVar.Get(ctx)?.CommitSha ?? "")) }, "Set Commit SHA"),
                WithLabel(new SetOutput { Id = "SetOutputFilesChanged", Name = "Set Files Changed", OutputName = new("filesChanged"), OutputValue = new(ctx => {
                    var impl = implResult.Get(ctx);
                    var gen = testGenResult.Get(ctx);
                    var files = new List<string>();
                    if (gen?.TestFiles != null) files.AddRange(gen.TestFiles);
                    if (impl?.ImplementationFiles != null) files.AddRange(impl.ImplementationFiles);
                    return (object)System.Text.Json.JsonSerializer.Serialize(files);
                }) }, "Set Files Changed")
            }
        };
        setCompletedOutputs.SetDisplayText("Set Completed Outputs");

        var setFailedOutputs = new Sequence
        {
            Id = "SetFailedOutputs",
            Name = "Set Failed Outputs",
            Activities =
            {
                WithLabel(new SetOutput { Id = "SetOutputFailed", Name = "Set Failed", OutputName = new("success"), OutputValue = new(ctx => (object)false) }, "Set Failed"),
                WithLabel(new SetOutput { Id = "SetOutputErrorMessage", Name = "Set Error Message", OutputName = new("errorMessage"), OutputValue = new(ctx => (object)$"GREEN phase failed after {debugAttempt.Get(ctx)} debug iterations") }, "Set Error Message")
            }
        };
        setFailedOutputs.SetDisplayText("Set Failed Outputs");

        // Dedicated failure sink for the syntax-validation step. We surface
        // both a finishReason ("test-syntax-invalid") and the parsed error
        // payload so callers (and the audit trail) can tell exactly which
        // file / line tripped the validator. Mirrors the SetFailedOutputs
        // shape used elsewhere in this workflow.
        var setSyntaxInvalidOutputs = new Sequence
        {
            Id = "SetSyntaxInvalidOutputs",
            Name = "Set Syntax Invalid Outputs",
            Activities =
            {
                WithLabel(new SetOutput { Id = "SetOutputSyntaxFailed", Name = "Set Failed (Syntax)", OutputName = new("success"), OutputValue = new(ctx => (object)false) }, "Set Failed (Syntax)"),
                WithLabel(new SetOutput { Id = "SetOutputFinishReasonSyntax", Name = "Set Finish Reason", OutputName = new("finishReason"), OutputValue = new(ctx => (object)"test-syntax-invalid") }, "Set Finish Reason"),
                WithLabel(new SetOutput { Id = "SetOutputSyntaxErrors", Name = "Set Syntax Errors", OutputName = new("syntaxErrors"), OutputValue = new(ctx =>
                {
                    var r = testSyntaxResult.Get(ctx);
                    return (object)System.Text.Json.JsonSerializer.Serialize(r?.Errors ?? new List<TestSyntaxError>());
                }) }, "Set Syntax Errors")
            }
        };
        setSyntaxInvalidOutputs.SetDisplayText("Set Syntax Invalid Outputs");

        var finish = new Finish { Id = "FinishSuccess", Name = "Finish Success" };
        finish.SetDisplayText("Finish Success");
        var finishFailed = new Finish { Id = "FinishFailed", Name = "Finish Failed" };
        finishFailed.SetDisplayText("Finish Failed");
        var finishSyntaxInvalid = new Finish { Id = "FinishSyntaxInvalid", Name = "Finish: Test Syntax Invalid" };
        finishSyntaxInvalid.SetDisplayText("Finish: Test Syntax Invalid");

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
                validateTestSyntax, testSyntaxValidCheck,
                dispatchTestsRed, setRedTestsAllPassed, setRedFailedCount, setRedPassedCount,
                checkTestsFail,
                incrementRewrite, maxRewritesCheck,

                // GREEN phase
                logGreenPhaseStart,
                writeImplementation,
                dispatchTestsGreen, setGreenTestsAllPassed, setGreenPassedCount, setGreenFailedCount,
                greenTestsPassCheck,
                markDebug, incrementDebug, maxDebugCheck,

                // REFACTOR phase
                logRefactorPhaseStart,
                analyzeCode,
                refactoringNeededCheck,
                applyRefactoring, markRefactored,
                dispatchTestsRefactor, setRefactorTestsAllPassed,
                refactorTestsPassCheck,
                revertRefactoring,

                // Commit & Index
                commitChanges,
                updateCodeIndex,

                // Outputs
                setCompletedOutputs, setFailedOutputs, setSyntaxInvalidOutputs,
                finish, finishFailed, finishSyntaxInvalid
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
                // WriteTests -> ValidateTestSyntax -> guard -> dispatch (or finish-with-reason)
                Connect(writeTests, validateTestSyntax),
                Connect(validateTestSyntax, testSyntaxValidCheck),
                // True = invalid syntax → fail workflow with finishReason
                ConnectOutcome(testSyntaxValidCheck, "True", setSyntaxInvalidOutputs),
                Connect(setSyntaxInvalidOutputs, finishSyntaxInvalid),
                // False = valid (or skipped) → continue to RED dispatch
                ConnectOutcome(testSyntaxValidCheck, "False", dispatchTestsRed),
                Connect(dispatchTestsRed, setRedTestsAllPassed),
                Connect(setRedTestsAllPassed, setRedFailedCount),
                Connect(setRedFailedCount, setRedPassedCount),
                Connect(setRedPassedCount, checkTestsFail),

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
                Connect(writeImplementation, dispatchTestsGreen),
                Connect(dispatchTestsGreen, setGreenTestsAllPassed),
                Connect(setGreenTestsAllPassed, setGreenPassedCount),
                Connect(setGreenPassedCount, setGreenFailedCount),
                Connect(setGreenFailedCount, greenTestsPassCheck),

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
                Connect(markRefactored, dispatchTestsRefactor),
                Connect(dispatchTestsRefactor, setRefactorTestsAllPassed),
                Connect(setRefactorTestsAllPassed, refactorTestsPassCheck),

                // Refactored pass (True) -> commit
                ConnectOutcome(refactorTestsPassCheck, "True", commitChanges),
                // Refactored fail (False) -> revert then commit
                ConnectOutcome(refactorTestsPassCheck, "False", revertRefactoring),
                Connect(revertRefactoring, commitChanges),

                // --- COMMIT & INDEX & OUTPUT ---
                Connect(commitChanges, updateCodeIndex),
                Connect(updateCodeIndex, setCompletedOutputs),
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
        var sv = new SetVariable
        {
            Id = activityId,
            Name = name ?? activityId,
            Variable = variable,
            Value = new Input<object?>(valueFunc)
        };
        if (name != null) sv.SetDisplayText(name);
        return sv;
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

    // ================================================================
    // Helpers for parsing the dispatched testing-pipeline result.
    // The testing-pipeline workflow exposes:
    //   - "passed"        : bool
    //   - "qualityReport" : JSON-serialized QualityReport (TotalTests/PassedTests/FailedTests/...)
    //   - "teachingFeedback": string
    // We tolerate missing keys / shape drift by falling back to safe defaults.
    // ================================================================

    private static bool ExtractPassed(IDictionary<string, object>? result)
    {
        if (result == null) return false;
        if (result.TryGetValue("passed", out var p))
        {
            if (p is bool b) return b;
            if (p is string s && bool.TryParse(s, out var parsed)) return parsed;
        }
        return false;
    }

    private static int ExtractPassedCount(IDictionary<string, object>? result, int fallback)
    {
        var report = TryParseQualityReport(result);
        return report?.PassedTests ?? fallback;
    }

    private static int ExtractFailedCount(IDictionary<string, object>? result, int fallback)
    {
        var report = TryParseQualityReport(result);
        return report?.FailedTests ?? fallback;
    }

    private static QualityReport? TryParseQualityReport(IDictionary<string, object>? result)
    {
        if (result == null) return null;
        if (!result.TryGetValue("qualityReport", out var raw) || raw == null) return null;
        try
        {
            var json = raw as string ?? raw.ToString();
            if (string.IsNullOrWhiteSpace(json)) return null;
            return System.Text.Json.JsonSerializer.Deserialize<QualityReport>(json);
        }
        catch
        {
            return null;
        }
    }
}
