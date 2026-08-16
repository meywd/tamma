using Elsa.Expressions.Models;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime.Activities;
using Tamma.Activities.Review;
using Tamma.Activities.Review.Models;
using Tamma.Api.Services.Agents;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;

using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Code Review sub-workflow (Story 7-1D). Manages the full PR lifecycle for the mentorship
/// engine:
///   1. Bind inputs + resolve CodeReview:* config (defect #1 / #9)
///   2. Validate inputs (story/repo/junior present, ≥1 reviewer) → specific failure (#3)
///   3. Create PR → emit CODE_REVIEW.PR_CREATED.* (#8)
///   4. Request review → monitor (bookmark)
///   5. Approved → CI-gated, strategy-aware, retry-once merge (#5) → structured result (#6)
///   6. ChangesRequested → AnalyzeChanges + GenerateGuidance via mediated llm-call (#4)
///      → deliver → wait for fixes (bookmark) → re-request → loop (≤ max)
///   7. Max iterations / timeout / guidance-failure → escalate (bookmark) → resolve→merge,
///      reject→fail
///
/// Every terminal path produces a structured <see cref="CodeReviewWorkflowResult"/> via
/// BuildResult and emits the matching CODE_REVIEW.* DCB event. The pre-existing
/// MentorshipEvent rows are retained (written inside the activities).
///
/// LLM is reached ONLY through DispatchWorkflow("llm-call") — no in-engine provider call.
/// The fix-guidance roles are canonical (reviewer normalises to senior_developer): the
/// call-LLM endpoint 422s on unknown roles.
/// </summary>
public class CodeReviewWorkflow : WorkflowBase
{
    /// <summary>
    /// Cap on how many times a resolved merge-failure escalation may route back to the merge
    /// step before the run terminates as rejected. Each loop is human-gated (a senior responds),
    /// so this is not a CPU spin — but without a cap the escalate→merge→escalate cycle has no
    /// terminal guarantee. Two re-merges (then a rejected terminal) is the bound.
    /// </summary>
    private const int MaxMergeEscalations = 2;

    // Helper to disambiguate the Input constructor overloads.
    private static Input<T> Expr<T>(Func<ExpressionExecutionContext, T> func)
        => new(func);

    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Code Review Sub-Workflow";
        builder.DefinitionId = "code-review";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Manages the full PR lifecycle from creation through review, " +
                              "mediated-LLM fix guidance, and CI-gated merge with bookmark-based waiting.";

        // ============================================
        // Workflow variables
        // ============================================
        var sessionId = builder.WithVariable<string>("SessionId", "").Persisted();
        var sessionIdGuid = builder.WithVariable<Guid>("SessionIdGuid", Guid.Empty).Persisted();
        var storyId = builder.WithVariable<string>("StoryId", "").Persisted();
        var juniorId = builder.WithVariable<string>("JuniorId", "").Persisted();
        var tenantId = builder.WithVariable<string>("TenantId", "").Persisted();
        var repositoryUrl = builder.WithVariable<string>("RepositoryUrl", "").Persisted();
        var branchName = builder.WithVariable<string>("BranchName", "").Persisted();
        var baseBranch = builder.WithVariable<string>("BaseBranch", "main").Persisted();
        var reviewerIdsJson = builder.WithVariable<string>("ReviewerIdsJson", "").Persisted();
        var resolvedReviewers = builder.WithVariable<string>("ResolvedReviewers", "").Persisted();
        var skillLevel = builder.WithVariable<int>("SkillLevel", 3).Persisted();
        var prNumber = builder.WithVariable<int>("PRNumber", 0).Persisted();
        var prUrl = builder.WithVariable<string>("PRUrl", "").Persisted();
        var iteration = builder.WithVariable<int>("Iteration", 0).Persisted();
        var maxIterations = builder.WithVariable<int>("MaxIterations", 5).Persisted();
        var maxIterationsInput = builder.WithVariable<int>("MaxIterationsInput", 0).Persisted();
        var mergeStrategyInput = builder.WithVariable<string>("MergeStrategyInput", "").Persisted();
        var reviewCommentsJson = builder.WithVariable<string>("ReviewCommentsJson", "[]").Persisted();
        var analysisText = builder.WithVariable<string>("AnalysisText", "").Persisted();
        var guidanceText = builder.WithVariable<string>("GuidanceText", "").Persisted();
        var validationError = builder.WithVariable<string>("ValidationError", "").Persisted();
        var escalationResolution = builder.WithVariable<string>("EscalationResolution", "").Persisted();
        var mergeShaVar = builder.WithVariable<string>("MergeSha", "").Persisted();
        // Bound (#IMPORTANT) on the escalate→merge re-merge loop. Each time the merge-failure
        // escalation resolves and routes back to MergeAndComplete we increment this; once it
        // reaches the cap the run terminates as rejected (with a distinct, auditable event)
        // instead of cycling between merge and escalation forever.
        var mergeRetryCount = builder.WithVariable<int>("MergeRetryCount", 0).Persisted();

        // Config-resolved variables (filled by BindConfig)
        var mergeStrategy = builder.WithVariable<MergeStrategy>("MergeStrategy", MergeStrategy.Squash).Persisted();
        var reviewTimeoutHours = builder.WithVariable<int>("ReviewTimeoutHours", 24).Persisted();
        var fixTimeoutHours = builder.WithVariable<int>("FixTimeoutHours", 1).Persisted();
        var verifyCi = builder.WithVariable<bool>("VerifyCIBeforeMerge", true).Persisted();
        var deleteBranch = builder.WithVariable<bool>("DeleteBranchAfterMerge", true).Persisted();

        // LLM dispatch result holders
        var analyzeResult = builder.WithVariable<IDictionary<string, object>?>().Persisted();
        var guidanceResult = builder.WithVariable<IDictionary<string, object>?>().Persisted();

        // ============================================
        // 0. Bind inputs (defect #1) — mirror AssessmentWorkflow input binding
        // ============================================
        var bindInputs = new SetVariable
        {
            Id = "BindInputs",
            Name = "Bind Inputs",
            Variable = sessionId,
            Value = new(ctx =>
            {
                var sid = ctx.GetInput<string>("SessionId") ?? ctx.GetInput<string>("sessionId") ?? "";
                sessionIdGuid.Set(ctx, Guid.TryParse(sid, out var g) ? g : Guid.Empty);
                storyId.Set(ctx, ctx.GetInput<string>("StoryId") ?? ctx.GetInput<string>("storyId") ?? "");
                juniorId.Set(ctx, ctx.GetInput<string>("JuniorId") ?? ctx.GetInput<string>("juniorId") ?? "");
                tenantId.Set(ctx, ctx.GetInput<string>("TenantId") ?? ctx.GetInput<string>("tenantId") ?? "");
                repositoryUrl.Set(ctx, ctx.GetInput<string>("RepositoryUrl") ?? ctx.GetInput<string>("repositoryUrl") ?? "");
                var story = ctx.GetInput<string>("StoryId") ?? ctx.GetInput<string>("storyId") ?? "";
                baseBranch.Set(ctx, ctx.GetInput<string>("BaseBranch") ?? ctx.GetInput<string>("baseBranch") ?? "main");
                branchName.Set(ctx,
                    ctx.GetInput<string>("BranchName") ?? ctx.GetInput<string>("branchName")
                    ?? (string.IsNullOrEmpty(story) ? "" : $"feature/{story}"));
                reviewerIdsJson.Set(ctx, ctx.GetInput<string>("ReviewerIds") ?? ctx.GetInput<string>("reviewerIds") ?? "");
                skillLevel.Set(ctx, Math.Max(1, ctx.GetInput<int>("SkillLevel")));
                maxIterationsInput.Set(ctx, ctx.GetInput<int>("MaxIterations"));
                mergeStrategyInput.Set(ctx, ctx.GetInput<string>("MergeStrategy") ?? ctx.GetInput<string>("mergeStrategy") ?? "");
                return sid;
            })
        };
        bindInputs.SetDisplayText("Bind Inputs");

        // ============================================
        // 0b. Resolve CodeReview:* config (#9)
        // ============================================
        var bindConfig = new BindCodeReviewConfigActivity
        {
            Id = "BindConfig",
            Name = "Bind Code Review Config",
            MaxIterationsInput = Expr<int>(ctx => maxIterationsInput.Get(ctx)),
            MergeStrategyInput = Expr<string?>(ctx => mergeStrategyInput.Get(ctx)),
            MaxIterations = new(maxIterations),
            MergeStrategy = new(mergeStrategy),
            ReviewTimeoutHours = new(reviewTimeoutHours),
            FixTimeoutHours = new(fixTimeoutHours),
            VerifyCIBeforeMerge = new(verifyCi),
            DeleteBranchAfterMerge = new(deleteBranch)
        };
        bindConfig.SetDisplayText("Bind Code Review Config");

        // ============================================
        // 1. Validate inputs (#3)
        // ============================================
        var validateInputs = new ValidateCodeReviewInputsActivity
        {
            Id = "ValidateInputs",
            Name = "Validate Inputs",
            StoryId = Expr<string?>(ctx => storyId.Get(ctx)),
            RepositoryUrl = Expr<string?>(ctx => repositoryUrl.Get(ctx)),
            JuniorId = Expr<string?>(ctx => juniorId.Get(ctx)),
            ReviewerIdsJson = Expr<string?>(ctx => reviewerIdsJson.Get(ctx)),
            ErrorMessage = new(validationError),
            ResolvedReviewers = new(resolvedReviewers)
        };
        validateInputs.SetDisplayText("Validate Inputs");

        // ============================================
        // 2. Create PR
        // ============================================
        var createPR = new CreatePRActivity
        {
            Id = "CreatePR",
            SessionId = Expr<Guid>(ctx => sessionIdGuid.Get(ctx)),
            StoryId = Expr<string>(ctx => storyId.Get(ctx)),
            JuniorId = Expr<string>(ctx => juniorId.Get(ctx)),
            BaseBranch = Expr<string>(ctx => baseBranch.Get(ctx)),
            HeadBranch = Expr<string?>(ctx => branchName.Get(ctx)),
            Name = "Create Pull Request"
        };
        createPR.SetDisplayText("Create Pull Request");

        var storePRResult = new SetVariable
        {
            Id = "StorePRResult",
            Name = "Store PR Result",
            Variable = prNumber,
            Value = Expr<object?>(ctx =>
            {
                var output = createPR.GetOutput<PRCreationResult>(ctx, "Result");
                if (output is { Success: true, PRNumber: not null })
                {
                    prUrl.Set(ctx, output.PRUrl ?? "");
                    return output.PRNumber.Value;
                }
                return 0;
            })
        };
        storePRResult.SetDisplayText("Store PR Result");

        var prCreatedCheck = new FlowDecision(ctx => prNumber.Get(ctx) > 0)
        { Id = "PRCreatedCheck", Name = "PR Created?" };
        prCreatedCheck.SetDisplayText("PR Created?");

        // DCB event: PR created success / failed
        var emitPrCreated = EmitEvent("EmitPrCreated", "Emit PR Created",
            CodeReviewEvents.PrCreatedSuccess, sessionId, storyId, juniorId, tenantId,
            prNumber, prUrl, iteration, mergeShaVar, new((string?)null));
        var emitPrFailed = EmitEvent("EmitPrFailed", "Emit PR Failed",
            CodeReviewEvents.PrCreatedFailed, sessionId, storyId, juniorId, tenantId,
            prNumber, prUrl, iteration, mergeShaVar,
            new("PR creation failed (story/repository not resolvable)"));

        // ============================================
        // 3. Request review
        // ============================================
        var requestReview = new RequestReviewActivity
        {
            Id = "RequestReview",
            SessionId = Expr<Guid>(ctx => sessionIdGuid.Get(ctx)),
            PRNumber = Expr<int>(ctx => prNumber.Get(ctx)),
            StoryId = Expr<string>(ctx => storyId.Get(ctx)),
            JuniorId = Expr<string>(ctx => juniorId.Get(ctx)),
            Reviewers = Expr<string?>(ctx => resolvedReviewers.Get(ctx)),
            Name = "Request Code Review"
        };
        requestReview.SetDisplayText("Request Code Review");

        // ============================================
        // 4. Monitor review (bookmark-based)
        // ============================================
        var monitorReview = new MonitorReviewActivity
        {
            Id = "MonitorReview",
            SessionId = Expr<string>(ctx => sessionId.Get(ctx)),
            PRNumber = Expr<int>(ctx => prNumber.Get(ctx)),
            TimeoutHours = Expr<int>(ctx => reviewTimeoutHours.Get(ctx)),
            Name = "Monitor Review Status"
        };
        monitorReview.SetDisplayText("Monitor Review Status");

        // 5. Store review comments when changes requested
        var storeReviewComments = new SetVariable
        {
            Id = "StoreReviewComments",
            Name = "Store Review Comments",
            Variable = reviewCommentsJson,
            Value = Expr<object?>(ctx =>
            {
                var review = monitorReview.GetOutput<ReviewResult?>(ctx, "ReviewResult");
                if (review?.Comments != null && review.Comments.Count > 0)
                    return System.Text.Json.JsonSerializer.Serialize(review.Comments);
                return "[]";
            })
        };
        storeReviewComments.SetDisplayText("Store Review Comments");

        // 6. Increment iteration counter
        var incrementIteration = new SetVariable
        {
            Id = "IncrementIteration",
            Name = "Increment Review Iteration",
            Variable = iteration,
            Value = Expr<object?>(ctx => (object)(iteration.Get(ctx) + 1))
        };
        incrementIteration.SetDisplayText("Increment Review Iteration");

        // DCB event: iteration started
        var emitIteration = EmitEvent("EmitIteration", "Emit Iteration Started",
            CodeReviewEvents.IterationStarted, sessionId, storyId, juniorId, tenantId,
            prNumber, prUrl, iteration, mergeShaVar, new((string?)null));

        // ============================================
        // 7. AC7 mediated LLM (#4): AnalyzeChanges (role=senior_developer / code-review)
        // ============================================
        var analyzeChanges = new DispatchWorkflow
        {
            Id = "AnalyzeChanges",
            Name = "Analyze Changes (LLM)",
            WorkflowDefinitionId = new("llm-call"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["role"] = Tamma.Api.Services.Agents.AgentRole.SeniorDeveloper.ToWire(),
                ["action"] = Tamma.Api.Services.Agents.AgentAction.CodeReview.ToWire(),
                ["tenantId"] = tenantId.Get(ctx) ?? "",
                // Only data placeholders — the seeded (senior_developer, code-review) template
                // is the prompt (mediated prompt-store design). A hand-written variables["prompt"]
                // would be INERT: LlmCallWorkflow reads the top-level prompt/taskPrompt, not
                // variables. The template renders {{reviewCommentsJson}}.
                ["variables"] = new Dictionary<string, object>
                {
                    ["reviewCommentsJson"] = reviewCommentsJson.Get(ctx) ?? "[]",
                },
                ["enableTools"] = false,
            }),
            WaitForCompletion = new(true),
            Result = new(analyzeResult)
        };
        analyzeChanges.SetDisplayText("Analyze Changes (LLM)");

        var storeAnalysis = new SetVariable
        {
            Id = "StoreAnalysis",
            Name = "Store Analysis",
            Variable = analysisText,
            Value = Expr<object?>(ctx => (object)(ExtractLlmResponse(analyzeResult.Get(ctx)) ?? ""))
        };
        storeAnalysis.SetDisplayText("Store Analysis");

        // GenerateGuidance (role=senior_developer / mentor-feedback, skill-level aware)
        var generateGuidance = new DispatchWorkflow
        {
            Id = "GenerateGuidance",
            Name = "Generate Guidance (LLM)",
            WorkflowDefinitionId = new("llm-call"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["role"] = Tamma.Api.Services.Agents.AgentRole.SeniorDeveloper.ToWire(),
                ["action"] = Tamma.Api.Services.Agents.AgentAction.MentorFeedback.ToWire(),
                ["tenantId"] = tenantId.Get(ctx) ?? "",
                // Only data placeholders — the seeded (senior_developer, mentor-feedback)
                // template is the prompt (skill-level-aware). A hand-written variables["prompt"]
                // would be INERT here (LlmCallWorkflow reads top-level prompt/taskPrompt). The
                // template renders {{analysis}} / {{skillLevel}}.
                ["variables"] = new Dictionary<string, object>
                {
                    ["analysis"] = analysisText.Get(ctx) ?? "",
                    ["skillLevel"] = skillLevel.Get(ctx),
                },
                ["enableTools"] = false,
            }),
            WaitForCompletion = new(true),
            Result = new(guidanceResult)
        };
        generateGuidance.SetDisplayText("Generate Guidance (LLM)");

        var storeGuidance = new SetVariable
        {
            Id = "StoreGuidance",
            Name = "Store Guidance",
            Variable = guidanceText,
            Value = Expr<object?>(ctx => (object)(ExtractLlmResponse(guidanceResult.Get(ctx)) ?? ""))
        };
        storeGuidance.SetDisplayText("Store Guidance");

        // 8. Deliver guidance (formats + delivers the mediated-LLM output)
        var deliverGuidance = new DeliverGuidanceActivity
        {
            Id = "DeliverGuidance",
            SessionId = Expr<Guid>(ctx => sessionIdGuid.Get(ctx)),
            JuniorId = Expr<string>(ctx => juniorId.Get(ctx)),
            PRNumber = Expr<int>(ctx => prNumber.Get(ctx)),
            Iteration = Expr<int>(ctx => iteration.Get(ctx)),
            ReviewCommentsJson = Expr<string>(ctx => reviewCommentsJson.Get(ctx)),
            GuidanceText = Expr<string?>(ctx => guidanceText.Get(ctx)),
            Name = "Deliver Fix Guidance"
        };
        deliverGuidance.SetDisplayText("Deliver Fix Guidance");

        var emitGuidanceDelivered = EmitEvent("EmitGuidanceDelivered", "Emit Guidance Delivered",
            CodeReviewEvents.GuidanceDeliveredSuccess, sessionId, storyId, juniorId, tenantId,
            prNumber, prUrl, iteration, mergeShaVar, new((string?)null));

        var emitGuidanceFailed = EmitEvent("EmitGuidanceFailed", "Emit Guidance Failed",
            CodeReviewEvents.GuidanceDeliveredFailed, sessionId, storyId, juniorId, tenantId,
            prNumber, prUrl, iteration, mergeShaVar,
            new("Mediated-LLM guidance could not be generated/delivered; escalating."));

        // 9. Wait for fixes (bookmark-based) — fix timeout (#9 drift fix)
        var waitForFixes = new WaitForFixesActivity
        {
            Id = "WaitForFixes",
            SessionId = Expr<string>(ctx => sessionId.Get(ctx)),
            PRNumber = Expr<int>(ctx => prNumber.Get(ctx)),
            Iteration = Expr<int>(ctx => iteration.Get(ctx)),
            TimeoutHours = Expr<int>(ctx => fixTimeoutHours.Get(ctx)),
            Name = "Wait for Fix Submission"
        };
        waitForFixes.SetDisplayText("Wait for Fix Submission");

        // 10. Re-request review
        var reRequestReview = new ReRequestReviewActivity
        {
            Id = "ReRequestReview",
            SessionId = Expr<Guid>(ctx => sessionIdGuid.Get(ctx)),
            PRNumber = Expr<int>(ctx => prNumber.Get(ctx)),
            StoryId = Expr<string>(ctx => storyId.Get(ctx)),
            JuniorId = Expr<string>(ctx => juniorId.Get(ctx)),
            Iteration = Expr<int>(ctx => iteration.Get(ctx)),
            MaxIterations = Expr<int>(ctx => maxIterations.Get(ctx)),
            Name = "Re-Request Code Review"
        };
        reRequestReview.SetDisplayText("Re-Request Code Review");

        // 11. Max iterations check (authoritative guard — #11 dedup)
        var maxIterationsCheck = new FlowDecision(ctx => iteration.Get(ctx) >= maxIterations.Get(ctx))
        { Id = "MaxIterationsCheck", Name = "Max Iterations Reached?" };
        maxIterationsCheck.SetDisplayText("Max Iterations Reached?");

        // 12. Merge and complete (CI-gated, strategy-aware, retry-once, branch-delete — #5)
        var mergeAndComplete = new MergeAndCompleteReviewActivity
        {
            Id = "MergeAndComplete",
            SessionId = Expr<Guid>(ctx => sessionIdGuid.Get(ctx)),
            StoryId = Expr<string>(ctx => storyId.Get(ctx)),
            JuniorId = Expr<string>(ctx => juniorId.Get(ctx)),
            PRNumber = Expr<int>(ctx => prNumber.Get(ctx)),
            HeadBranch = Expr<string?>(ctx => branchName.Get(ctx)),
            Strategy = Expr<MergeStrategy>(ctx => mergeStrategy.Get(ctx)),
            TotalIterations = Expr<int>(ctx => iteration.Get(ctx)),
            VerifyCIBeforeMerge = Expr<bool>(ctx => verifyCi.Get(ctx)),
            DeleteBranchAfterMerge = Expr<bool>(ctx => deleteBranch.Get(ctx)),
            Name = "Merge and Complete Review"
        };
        mergeAndComplete.SetDisplayText("Merge and Complete Review");

        var storeMergeSha = new SetVariable
        {
            Id = "StoreMergeSha",
            Name = "Store Merge Sha",
            Variable = mergeShaVar,
            Value = Expr<object?>(ctx =>
            {
                var r = mergeAndComplete.GetOutput<ReviewMergeResult?>(ctx, "Result");
                return (object)(r?.MergeSha ?? "");
            })
        };
        storeMergeSha.SetDisplayText("Store Merge Sha");

        var emitMerged = EmitEvent("EmitMerged", "Emit Merged",
            CodeReviewEvents.MergedSuccess, sessionId, storyId, juniorId, tenantId,
            prNumber, prUrl, iteration, mergeShaVar, new((string?)null));

        // DCB event: merge failed (CI red / merge failed after retry) — emitted on the
        // MergeAndComplete "Failed" edge BEFORE escalating, so the repeated failure is
        // auditable (the CODE_REVIEW.MERGED.FAILED type was defined but never emitted).
        var emitMergeFailed = EmitEvent("EmitMergeFailed", "Emit Merge Failed",
            CodeReviewEvents.MergedFailed, sessionId, storyId, juniorId, tenantId,
            prNumber, prUrl, iteration, mergeShaVar,
            new("CI not green or merge failed after retry; escalating to senior."));

        // 13. Escalate review (max iterations)
        var escalateReview = new EscalateReviewActivity
        {
            Id = "EscalateReview",
            SessionId = Expr<Guid>(ctx => sessionIdGuid.Get(ctx)),
            PRNumber = Expr<int>(ctx => prNumber.Get(ctx)),
            JuniorId = Expr<string>(ctx => juniorId.Get(ctx)),
            // Story 4-6 — thread the DCB escalation-event tags so the raise-time
            // CODE_REVIEW.ESCALATED event carries storyId + tenantId.
            StoryId = Expr<string?>(ctx => storyId.Get(ctx)),
            TenantId = Expr<string?>(ctx => tenantId.Get(ctx)),
            Reason = new(EscalationReason.MaxIterationsReached),
            IterationsAttempted = Expr<int>(ctx => iteration.Get(ctx)),
            EscalationMessage = new("Maximum fix iterations reached during code review."),
            Name = "Escalate: Max Iterations"
        };
        escalateReview.SetDisplayText("Escalate: Max Iterations");

        // 14. Escalate due to timeout
        var escalateTimeout = new EscalateReviewActivity
        {
            Id = "EscalateTimeout",
            SessionId = Expr<Guid>(ctx => sessionIdGuid.Get(ctx)),
            PRNumber = Expr<int>(ctx => prNumber.Get(ctx)),
            JuniorId = Expr<string>(ctx => juniorId.Get(ctx)),
            // Story 4-6 — thread the DCB escalation-event tags so the raise-time
            // CODE_REVIEW.ESCALATED event carries storyId + tenantId.
            StoryId = Expr<string?>(ctx => storyId.Get(ctx)),
            TenantId = Expr<string?>(ctx => tenantId.Get(ctx)),
            Reason = new(EscalationReason.ReviewTimeout),
            IterationsAttempted = Expr<int>(ctx => iteration.Get(ctx)),
            EscalationMessage = new("Review or fix submission timed out."),
            Name = "Escalate: Review Timeout"
        };
        escalateTimeout.SetDisplayText("Escalate: Review Timeout");

        // 15. Escalate due to guidance-generation / merge failure
        var escalateGuidance = new EscalateReviewActivity
        {
            Id = "EscalateGuidance",
            SessionId = Expr<Guid>(ctx => sessionIdGuid.Get(ctx)),
            PRNumber = Expr<int>(ctx => prNumber.Get(ctx)),
            JuniorId = Expr<string>(ctx => juniorId.Get(ctx)),
            // Story 4-6 — thread the DCB escalation-event tags so the raise-time
            // CODE_REVIEW.ESCALATED event carries storyId + tenantId.
            StoryId = Expr<string?>(ctx => storyId.Get(ctx)),
            TenantId = Expr<string?>(ctx => tenantId.Get(ctx)),
            Reason = new(EscalationReason.Other),
            IterationsAttempted = Expr<int>(ctx => iteration.Get(ctx)),
            EscalationMessage = new("Automated fix guidance could not be generated."),
            Name = "Escalate: Guidance Failure"
        };
        escalateGuidance.SetDisplayText("Escalate: Guidance Failure");

        var escalateMerge = new EscalateReviewActivity
        {
            Id = "EscalateMerge",
            SessionId = Expr<Guid>(ctx => sessionIdGuid.Get(ctx)),
            PRNumber = Expr<int>(ctx => prNumber.Get(ctx)),
            JuniorId = Expr<string>(ctx => juniorId.Get(ctx)),
            // Story 4-6 — thread the DCB escalation-event tags so the raise-time
            // CODE_REVIEW.ESCALATED event carries storyId + tenantId.
            StoryId = Expr<string?>(ctx => storyId.Get(ctx)),
            TenantId = Expr<string?>(ctx => tenantId.Get(ctx)),
            Reason = new(EscalationReason.MergeConflict),
            IterationsAttempted = Expr<int>(ctx => iteration.Get(ctx)),
            EscalationMessage = new("CI not green or merge failed after retry; senior review required."),
            Name = "Escalate: Merge Failure"
        };
        escalateMerge.SetDisplayText("Escalate: Merge Failure");

        // Record escalation resolution for the structured result (all escalate nodes)
        var captureEscalated = new SetVariable
        {
            Id = "CaptureEscalated",
            Name = "Capture Escalation Resolution",
            Variable = escalationResolution,
            Value = Expr<object?>(ctx => (object)"resolved")
        };
        captureEscalated.SetDisplayText("Capture Escalation Resolution");

        // Story 4-6 — the RESOLVE companion to the raise-time CODE_REVIEW.ESCALATED (emitted
        // by EscalateReviewActivity at suspend). This fires on the Resolved→merge edge, so the
        // senior's resolution is a distinct audit row (a rejection instead lands on
        // CODE_REVIEW.FAILED; an SLA expiry on the timed-out terminal).
        var emitEscalated = EmitEvent("EmitEscalated", "Emit Escalation Resolved",
            CodeReviewEvents.EscalationResolved, sessionId, storyId, juniorId, tenantId,
            prNumber, prUrl, iteration, mergeShaVar, new("Escalation resolved by senior developer."));

        // ---- Merge re-escalation loop bound (#IMPORTANT) -------------------------------
        // The merge-failure escalation resolving routes back to MergeAndComplete; count the
        // re-merges and terminate as rejected once the cap is hit instead of looping forever.
        var incrementMergeRetry = new SetVariable
        {
            Id = "IncrementMergeRetry",
            Name = "Increment Merge Retry",
            Variable = mergeRetryCount,
            Value = Expr<object?>(ctx => (object)(mergeRetryCount.Get(ctx) + 1))
        };
        incrementMergeRetry.SetDisplayText("Increment Merge Retry");

        var mergeRetryCapCheck = new FlowDecision(ctx => mergeRetryCount.Get(ctx) >= MaxMergeEscalations)
        { Id = "MergeRetryCapCheck", Name = "Merge Retry Cap Reached?" };
        mergeRetryCapCheck.SetDisplayText("Merge Retry Cap Reached?");

        // Distinct, auditable terminal event for the capped merge loop (LOUD error-status).
        var emitMergeLoopExhausted = EmitEvent("EmitMergeLoopExhausted", "Emit Merge Loop Exhausted",
            CodeReviewEvents.MergedFailed, sessionId, storyId, juniorId, tenantId,
            prNumber, prUrl, iteration, mergeShaVar,
            new($"Merge could not be completed after {MaxMergeEscalations} senior re-merge attempts; terminating as rejected."));

        var buildMergeExhaustedResult = BuildResult("BuildMergeExhaustedResult", "Build Merge-Exhausted Result",
            PRReviewStatus.Error, false, prNumber, prUrl, mergeShaVar, iteration,
            wasEscalated: true, escalationResolution: new("merge-loop-exhausted", "merge-loop-exhausted"),
            message: new($"Merge could not be completed after {MaxMergeEscalations} senior re-merge attempts."));

        // ---- Escalation senior-SLA timeout terminal (durable timeout P0) ----------------
        // A never-answered escalation now resumes via the durable Delay bookmark on the
        // TimedOut outcome — terminate LOUD (never a silent suspend-forever / false success).
        var emitEscalationTimedOut = EmitEvent("EmitEscalationTimedOut", "Emit Escalation Timed Out",
            CodeReviewEvents.Failed, sessionId, storyId, juniorId, tenantId,
            prNumber, prUrl, iteration, mergeShaVar,
            new("Senior-response SLA expired with no response; escalation timed out."));

        var buildEscalationTimedOutResult = BuildResult("BuildEscalationTimedOutResult", "Build Escalation-TimedOut Result",
            PRReviewStatus.TimedOut, false, prNumber, prUrl, mergeShaVar, iteration,
            wasEscalated: true, escalationResolution: new("timed-out", "timed-out"),
            message: new("Senior-response SLA expired with no response; escalation timed out."));

        // ============================================
        // Terminal: structured results (#6) + DCB FAILED event (#8)
        // ============================================
        // Merge-success terminal — shared by the direct-approval and escalation-resolved
        // merge paths. wasEscalated/escalationResolution are read from the escalationResolution
        // variable so an escalated-then-merged run is reported as escalated (not a false
        // "direct approval").
        var buildSuccessResult = new BuildCodeReviewResultActivity
        {
            Id = "BuildSuccessResult",
            Name = "Build Success Result",
            FinalStatus = new(PRReviewStatus.Approved),
            Success = new(true),
            PRNumber = Expr<int>(ctx => prNumber.Get(ctx)),
            PRUrl = Expr<string?>(ctx => prUrl.Get(ctx)),
            MergeSha = Expr<string?>(ctx => mergeShaVar.Get(ctx)),
            TotalIterations = Expr<int>(ctx => iteration.Get(ctx)),
            WasEscalated = Expr<bool>(ctx => !string.IsNullOrEmpty(escalationResolution.Get(ctx))),
            EscalationResolution = Expr<string?>(ctx =>
            {
                var r = escalationResolution.Get(ctx);
                return string.IsNullOrEmpty(r) ? null : r;
            }),
            Message = Expr<string?>(ctx => string.IsNullOrEmpty(escalationResolution.Get(ctx))
                ? "PR approved and merged."
                : "Escalation resolved by senior; PR merged.")
        };
        buildSuccessResult.SetDisplayText("Build Success Result");

        var buildValidationFailedResult = BuildResult("BuildValidationFailedResult", "Build Validation-Failed Result",
            PRReviewStatus.Error, false, prNumber, prUrl, mergeShaVar, iteration,
            wasEscalated: false, escalationResolution: new("", null),
            message: new(ctx => validationError.Get(ctx)));

        // PR-creation failure has its own terminal + message — it must NOT reuse the
        // validation-failed result (whose message reads ValidationError, which is empty on
        // this path → an empty terminal Message). Never-empty.
        var buildPrFailedResult = BuildResult("BuildPrFailedResult", "Build PR-Failed Result",
            PRReviewStatus.Error, false, prNumber, prUrl, mergeShaVar, iteration,
            wasEscalated: false, escalationResolution: new("", null),
            message: new("PR creation failed (story/repository not resolvable); review cannot proceed."));

        var buildRejectedResult = BuildResult("BuildRejectedResult", "Build Rejected Result",
            PRReviewStatus.ChangesRequested, false, prNumber, prUrl, mergeShaVar, iteration,
            wasEscalated: true, escalationResolution: new("rejected", "rejected"),
            message: new("Senior rejected the PR."));

        var emitValidationFailed = EmitEvent("EmitValidationFailed", "Emit Validation Failed",
            CodeReviewEvents.Failed, sessionId, storyId, juniorId, tenantId,
            prNumber, prUrl, iteration, mergeShaVar, new(ctx => validationError.Get(ctx)));

        var emitRejected = EmitEvent("EmitRejected", "Emit Review Failed",
            CodeReviewEvents.Failed, sessionId, storyId, juniorId, tenantId,
            prNumber, prUrl, iteration, mergeShaVar, new("Senior rejected the PR."));

        var finish = new Finish { Id = "Finish", Name = "Finish" };
        finish.SetDisplayText("Finish");

        // ============================================
        // Flowchart with connections
        // ============================================
        builder.Root = new Flowchart
        {
            Id = "CodeReviewFlowchart",
            Name = "Code Review Flowchart",
            Activities =
            {
                bindInputs, bindConfig, validateInputs,
                createPR, storePRResult, prCreatedCheck, emitPrCreated, emitPrFailed,
                requestReview, monitorReview,
                storeReviewComments, incrementIteration, emitIteration,
                analyzeChanges, storeAnalysis, generateGuidance, storeGuidance,
                deliverGuidance, emitGuidanceDelivered, emitGuidanceFailed,
                waitForFixes, reRequestReview, maxIterationsCheck,
                mergeAndComplete, storeMergeSha, emitMerged, emitMergeFailed,
                escalateReview, escalateTimeout, escalateGuidance, escalateMerge,
                captureEscalated, emitEscalated,
                incrementMergeRetry, mergeRetryCapCheck, emitMergeLoopExhausted, buildMergeExhaustedResult,
                emitEscalationTimedOut, buildEscalationTimedOutResult,
                buildSuccessResult, buildValidationFailedResult, buildRejectedResult, buildPrFailedResult,
                emitValidationFailed, emitRejected, finish
            },
            Connections =
            {
                // Head: bind inputs -> bind config -> validate
                new(bindInputs, bindConfig),
                new(bindConfig, validateInputs),

                // Validation: Valid -> create PR ; Invalid -> validation-failed terminal
                new(new FlowEndpoint(validateInputs, "Valid"), new FlowEndpoint(createPR)),
                new(new FlowEndpoint(validateInputs, "Invalid"), new FlowEndpoint(emitValidationFailed)),
                new(emitValidationFailed, buildValidationFailedResult),
                new(buildValidationFailedResult, finish),

                // Create PR -> store -> check
                new(createPR, storePRResult),
                new(storePRResult, prCreatedCheck),
                new(new FlowEndpoint(prCreatedCheck, "True"), new FlowEndpoint(emitPrCreated)),
                new(new FlowEndpoint(prCreatedCheck, "False"), new FlowEndpoint(emitPrFailed)),
                new(emitPrCreated, requestReview),
                new(emitPrFailed, buildPrFailedResult),
                new(buildPrFailedResult, finish),

                // Request review -> monitor (bookmark)
                new(requestReview, monitorReview),

                // Monitor outcomes
                new(new FlowEndpoint(monitorReview, "Approved"), new FlowEndpoint(mergeAndComplete)),
                new(new FlowEndpoint(monitorReview, "ChangesRequested"), new FlowEndpoint(storeReviewComments)),
                new(new FlowEndpoint(monitorReview, "TimedOut"), new FlowEndpoint(escalateTimeout)),

                // Changes requested -> store -> increment -> iteration event -> analyze (LLM)
                new(storeReviewComments, incrementIteration),
                new(incrementIteration, emitIteration),
                new(emitIteration, analyzeChanges),
                new(analyzeChanges, storeAnalysis),
                new(storeAnalysis, generateGuidance),
                new(generateGuidance, storeGuidance),
                new(storeGuidance, deliverGuidance),

                // Deliver guidance outcomes
                new(new FlowEndpoint(deliverGuidance, "Delivered"), new FlowEndpoint(emitGuidanceDelivered)),
                new(new FlowEndpoint(deliverGuidance, "Failed"), new FlowEndpoint(emitGuidanceFailed)),
                new(emitGuidanceDelivered, waitForFixes),
                new(emitGuidanceFailed, escalateGuidance),

                // Wait for fixes outcomes
                new(new FlowEndpoint(waitForFixes, "FixesReceived"), new FlowEndpoint(reRequestReview)),
                new(new FlowEndpoint(waitForFixes, "TimedOut"), new FlowEndpoint(escalateTimeout)),

                // Re-request -> max-iterations guard
                new(reRequestReview, maxIterationsCheck),
                new(new FlowEndpoint(maxIterationsCheck, "True"), new FlowEndpoint(escalateReview)),
                new(new FlowEndpoint(maxIterationsCheck, "False"), new FlowEndpoint(monitorReview)),

                // Merge outcomes (CI-gated, retry-once)
                new(new FlowEndpoint(mergeAndComplete, "Merged"), new FlowEndpoint(storeMergeSha)),
                // Failed -> emit CODE_REVIEW.MERGED.FAILED (auditable) -> escalate
                new(new FlowEndpoint(mergeAndComplete, "Failed"), new FlowEndpoint(emitMergeFailed)),
                new(emitMergeFailed, escalateMerge),
                new(storeMergeSha, emitMerged),
                new(emitMerged, buildSuccessResult),
                new(buildSuccessResult, finish),

                // Escalations (bookmark) outcomes — Resolved -> capture+merge ; Rejected -> fail ;
                // TimedOut -> senior-SLA-expired terminal (durable timeout; never a silent suspend).
                new(new FlowEndpoint(escalateReview, "Resolved"), new FlowEndpoint(captureEscalated)),
                new(new FlowEndpoint(escalateReview, "Rejected"), new FlowEndpoint(emitRejected)),
                new(new FlowEndpoint(escalateReview, "TimedOut"), new FlowEndpoint(emitEscalationTimedOut)),
                new(new FlowEndpoint(escalateTimeout, "Resolved"), new FlowEndpoint(captureEscalated)),
                new(new FlowEndpoint(escalateTimeout, "Rejected"), new FlowEndpoint(emitRejected)),
                new(new FlowEndpoint(escalateTimeout, "TimedOut"), new FlowEndpoint(emitEscalationTimedOut)),
                new(new FlowEndpoint(escalateGuidance, "Resolved"), new FlowEndpoint(captureEscalated)),
                new(new FlowEndpoint(escalateGuidance, "Rejected"), new FlowEndpoint(emitRejected)),
                new(new FlowEndpoint(escalateGuidance, "TimedOut"), new FlowEndpoint(emitEscalationTimedOut)),
                // EscalateMerge.Resolved goes through the re-merge loop bound, not straight to merge.
                new(new FlowEndpoint(escalateMerge, "Resolved"), new FlowEndpoint(incrementMergeRetry)),
                new(new FlowEndpoint(escalateMerge, "Rejected"), new FlowEndpoint(emitRejected)),
                new(new FlowEndpoint(escalateMerge, "TimedOut"), new FlowEndpoint(emitEscalationTimedOut)),

                // Merge re-escalation loop bound: increment -> cap check.
                //   cap reached  -> distinct MERGED.FAILED event -> rejected terminal (no loop)
                //   under the cap -> capture + re-merge
                new(incrementMergeRetry, mergeRetryCapCheck),
                new(new FlowEndpoint(mergeRetryCapCheck, "True"), new FlowEndpoint(emitMergeLoopExhausted)),
                new(new FlowEndpoint(mergeRetryCapCheck, "False"), new FlowEndpoint(captureEscalated)),
                new(emitMergeLoopExhausted, buildMergeExhaustedResult),
                new(buildMergeExhaustedResult, finish),

                // Escalation resolved -> emit escalated -> merge -> success(escalated) result
                new(captureEscalated, emitEscalated),
                new(emitEscalated, mergeAndComplete),

                // Escalation rejected -> rejected result
                new(emitRejected, buildRejectedResult),
                new(buildRejectedResult, finish),

                // Escalation senior-SLA timed out -> escalation-timeout terminal
                new(emitEscalationTimedOut, buildEscalationTimedOutResult),
                new(buildEscalationTimedOutResult, finish),

                // Merge success after escalation reuses buildSuccessResult path via storeMergeSha→emitMerged→buildSuccessResult.
            }
        };
    }

    // ================================================================
    // Helper: EmitCodeReviewEventActivity factory
    // ================================================================
    private static EmitCodeReviewEventActivity EmitEvent(
        string id, string displayName, string eventType,
        Variable<string> sessionId, Variable<string> storyId, Variable<string> juniorId,
        Variable<string> tenantId, Variable<int> prNumber, Variable<string> prUrl,
        Variable<int> iteration, Variable<string> mergeSha, Input<string?> detail)
    {
        var emit = new EmitCodeReviewEventActivity
        {
            Id = id,
            Name = displayName,
            EventType = new(eventType),
            SessionId = Expr<string?>(ctx => sessionId.Get(ctx)),
            StoryId = Expr<string?>(ctx => storyId.Get(ctx)),
            JuniorId = Expr<string?>(ctx => juniorId.Get(ctx)),
            TenantId = Expr<string?>(ctx => tenantId.Get(ctx)),
            PrNumber = Expr<int>(ctx => prNumber.Get(ctx)),
            PrUrl = Expr<string?>(ctx => prUrl.Get(ctx)),
            Iteration = Expr<int>(ctx => iteration.Get(ctx)),
            MergeSha = Expr<string?>(ctx => mergeSha.Get(ctx)),
            Detail = detail
        };
        emit.SetDisplayText(displayName);
        return emit;
    }

    // ================================================================
    // Helper: BuildCodeReviewResultActivity factory
    // ================================================================
    private static BuildCodeReviewResultActivity BuildResult(
        string id, string displayName, PRReviewStatus status, bool success,
        Variable<int> prNumber, Variable<string> prUrl, Variable<string> mergeSha,
        Variable<int> iteration, bool wasEscalated, Input<string?> escalationResolution,
        Input<string?> message)
    {
        var build = new BuildCodeReviewResultActivity
        {
            Id = id,
            Name = displayName,
            FinalStatus = new(status),
            Success = new(success),
            PRNumber = Expr<int>(ctx => prNumber.Get(ctx)),
            PRUrl = Expr<string?>(ctx => prUrl.Get(ctx)),
            MergeSha = Expr<string?>(ctx => mergeSha.Get(ctx)),
            TotalIterations = Expr<int>(ctx => iteration.Get(ctx)),
            WasEscalated = new(wasEscalated),
            EscalationResolution = escalationResolution,
            Message = message
        };
        build.SetDisplayText(displayName);
        return build;
    }

    // ================================================================
    // Helper: extract llmResponse from a DispatchWorkflow("llm-call") result dict
    // ================================================================
    private static string? ExtractLlmResponse(IDictionary<string, object>? result)
    {
        if (result == null) return null;
        if (result.TryGetValue("llmResponse", out var r))
            return r?.ToString();
        if (result.TryGetValue("response", out var r2))
            return r2?.ToString();
        return null;
    }
}
