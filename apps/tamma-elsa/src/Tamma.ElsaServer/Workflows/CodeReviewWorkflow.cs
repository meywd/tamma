using Elsa.Expressions.Models;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Models;
using Tamma.Activities.Review;
using Tamma.Activities.Review.Models;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Code Review sub-workflow.
///
/// Manages the full PR lifecycle:
///   1. Create PR
///   2. Request review
///   3. Monitor review (bookmark: waits for webhook)
///   4. If approved  -> merge and complete
///   5. If changes requested -> deliver guidance -> wait for fixes (bookmark)
///      -> re-request review -> loop back to monitor (max 5 iterations)
///   6. If max iterations or timeout -> escalate (bookmark: waits for senior)
///      -> resolved -> merge, rejected -> fail
///
/// This is a code-first IWorkflow using the Flowchart composite activity
/// with bookmark-based waiting for external events.
/// </summary>
public class CodeReviewWorkflow : WorkflowBase
{
    // Helper to disambiguate the Input constructor overloads.
    private static Input<T> Expr<T>(Func<ExpressionExecutionContext, T> func)
        => new(func);

    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Code Review Sub-Workflow";
        builder.DefinitionId = "code-review";
        builder.Description = "Manages the full PR lifecycle from creation through review, " +
                              "fix guidance, and merge with bookmark-based waiting.";

        // ============================================
        // Workflow variables
        // ============================================
        var sessionId = builder.WithVariable<string>("SessionId", "");
        var sessionIdGuid = builder.WithVariable<Guid>("SessionIdGuid", Guid.Empty);
        var storyId = builder.WithVariable<string>("StoryId", "");
        var juniorId = builder.WithVariable<string>("JuniorId", "");
        var prNumber = builder.WithVariable<int>("PRNumber", 0);
        var prUrl = builder.WithVariable<string>("PRUrl", "");
        var iteration = builder.WithVariable<int>("Iteration", 0);
        var maxIterations = builder.WithVariable<int>("MaxIterations", 5);
        var reviewCommentsJson = builder.WithVariable<string>("ReviewCommentsJson", "[]");
        var mergeStrategy = builder.WithVariable<MergeStrategy>("MergeStrategy", MergeStrategy.Squash);

        // ============================================
        // Activities
        // ============================================

        // 1. Create the pull request
        var createPR = new CreatePRActivity
        {
            Id = "CreatePR",
            SessionId = Expr<Guid>(ctx => sessionIdGuid.Get(ctx)),
            StoryId = Expr<string>(ctx => storyId.Get(ctx)),
            JuniorId = Expr<string>(ctx => juniorId.Get(ctx)),
            Name = "Create Pull Request"
        };

        // 2. Check if PR creation succeeded — use workflow variable to track
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

        var prCreatedCheck = new FlowDecision(ctx => prNumber.Get(ctx) > 0)
        { Id = "PRCreatedCheck", Name = "PR Created?" };

        // 3. Request review
        var requestReview = new RequestReviewActivity
        {
            Id = "RequestReview",
            SessionId = Expr<Guid>(ctx => sessionIdGuid.Get(ctx)),
            PRNumber = Expr<int>(ctx => prNumber.Get(ctx)),
            StoryId = Expr<string>(ctx => storyId.Get(ctx)),
            JuniorId = Expr<string>(ctx => juniorId.Get(ctx)),
            Name = "Request Code Review"
        };

        // 4. Monitor review (bookmark-based)
        var monitorReview = new MonitorReviewActivity
        {
            Id = "MonitorReview",
            SessionId = Expr<string>(ctx => sessionId.Get(ctx)),
            PRNumber = Expr<int>(ctx => prNumber.Get(ctx)),
            TimeoutHours = new(24),
            Name = "Monitor Review Status"
        };

        // 5. Store review comments when changes are requested
        var storeReviewComments = new SetVariable
        {
            Id = "StoreReviewComments",
            Name = "Store Review Comments",
            Variable = reviewCommentsJson,
            Value = Expr<object?>(ctx =>
            {
                var review = monitorReview.GetOutput<ReviewResult?>(ctx, "ReviewResult");
                if (review?.Comments != null && review.Comments.Count > 0)
                {
                    return System.Text.Json.JsonSerializer.Serialize(review.Comments);
                }
                return "[]";
            })
        };

        // 6. Increment iteration counter
        var incrementIteration = new SetVariable
        {
            Id = "IncrementIteration",
            Name = "Increment Review Iteration",
            Variable = iteration,
            Value = Expr<object?>(ctx => (object)(iteration.Get(ctx) + 1))
        };

        // 7. Deliver fix guidance
        var deliverGuidance = new DeliverGuidanceActivity
        {
            Id = "DeliverGuidance",
            SessionId = Expr<Guid>(ctx => sessionIdGuid.Get(ctx)),
            JuniorId = Expr<string>(ctx => juniorId.Get(ctx)),
            PRNumber = Expr<int>(ctx => prNumber.Get(ctx)),
            Iteration = Expr<int>(ctx => iteration.Get(ctx)),
            ReviewCommentsJson = Expr<string>(ctx => reviewCommentsJson.Get(ctx)),
            Name = "Deliver Fix Guidance"
        };

        // 8. Wait for fixes (bookmark-based)
        var waitForFixes = new WaitForFixesActivity
        {
            Id = "WaitForFixes",
            SessionId = Expr<string>(ctx => sessionId.Get(ctx)),
            PRNumber = Expr<int>(ctx => prNumber.Get(ctx)),
            Iteration = Expr<int>(ctx => iteration.Get(ctx)),
            TimeoutHours = new(24),
            Name = "Wait for Fix Submission"
        };

        // 9. Re-request review
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

        // 10. Check if max iterations reached
        var maxIterationsCheck = new FlowDecision(ctx => iteration.Get(ctx) >= maxIterations.Get(ctx))
        { Id = "MaxIterationsCheck", Name = "Max Iterations Reached?" };

        // 11. Merge and complete
        var mergeAndComplete = new MergeAndCompleteReviewActivity
        {
            Id = "MergeAndComplete",
            SessionId = Expr<Guid>(ctx => sessionIdGuid.Get(ctx)),
            StoryId = Expr<string>(ctx => storyId.Get(ctx)),
            JuniorId = Expr<string>(ctx => juniorId.Get(ctx)),
            PRNumber = Expr<int>(ctx => prNumber.Get(ctx)),
            Strategy = Expr<MergeStrategy>(ctx => mergeStrategy.Get(ctx)),
            TotalIterations = Expr<int>(ctx => iteration.Get(ctx)),
            Name = "Merge and Complete Review"
        };

        // 12. Escalate review (max iterations)
        var escalateReview = new EscalateReviewActivity
        {
            Id = "EscalateReview",
            SessionId = Expr<Guid>(ctx => sessionIdGuid.Get(ctx)),
            PRNumber = Expr<int>(ctx => prNumber.Get(ctx)),
            JuniorId = Expr<string>(ctx => juniorId.Get(ctx)),
            Reason = new(EscalationReason.MaxIterationsReached),
            IterationsAttempted = Expr<int>(ctx => iteration.Get(ctx)),
            EscalationMessage = new("Maximum fix iterations reached during code review."),
            Name = "Escalate: Max Iterations"
        };

        // 13. Escalate due to timeout
        var escalateTimeout = new EscalateReviewActivity
        {
            Id = "EscalateTimeout",
            SessionId = Expr<Guid>(ctx => sessionIdGuid.Get(ctx)),
            PRNumber = Expr<int>(ctx => prNumber.Get(ctx)),
            JuniorId = Expr<string>(ctx => juniorId.Get(ctx)),
            Reason = new(EscalationReason.ReviewTimeout),
            IterationsAttempted = Expr<int>(ctx => iteration.Get(ctx)),
            EscalationMessage = new("Review or fix submission timed out."),
            Name = "Escalate: Review Timeout"
        };

        // 14. Terminal nodes (SetOutput sequences)
        var failedEnd = new Sequence
        {
            Id = "FailedEnd",
            Name = "Emit Failure Outputs",
            Activities =
            {
                new SetOutput { Id = "OutputFailedSuccess", Name = "Output Failed Success", OutputName = new("success"), OutputValue = new(ctx => (object)false) },
                new SetOutput { Id = "OutputErrorMessage", Name = "Output Error Message", OutputName = new("errorMessage"), OutputValue = new(ctx => (object)"Code review failed") }
            }
        };

        var successEnd = new Sequence
        {
            Id = "SuccessEnd",
            Name = "Emit Success Outputs",
            Activities =
            {
                new SetOutput { Id = "OutputSuccessFlag", Name = "Output Success Flag", OutputName = new("success"), OutputValue = new(ctx => (object)true) },
                new SetOutput { Id = "OutputPrUrl", Name = "Output PR URL", OutputName = new("prUrl"), OutputValue = new(ctx => (object)(prUrl.Get(ctx) ?? "")) },
                new SetOutput { Id = "OutputIterations", Name = "Output Iterations", OutputName = new("iterations"), OutputValue = new(ctx => (object)iteration.Get(ctx)) }
            }
        };

        // ============================================
        // Flowchart with connections
        // ============================================
        builder.Root = new Flowchart
        {
            Id = "CodeReviewFlowchart",
            Name = "Code Review Flowchart",
            Activities =
            {
                createPR,
                storePRResult,
                prCreatedCheck,
                requestReview,
                monitorReview,
                storeReviewComments,
                incrementIteration,
                deliverGuidance,
                waitForFixes,
                reRequestReview,
                maxIterationsCheck,
                mergeAndComplete,
                escalateReview,
                escalateTimeout,
                failedEnd,
                successEnd
            },
            Connections =
            {
                // Start: create PR -> store result -> check success
                new(createPR, storePRResult),
                new(storePRResult, prCreatedCheck),

                // PR created? true -> request review, false -> fail
                new(new FlowEndpoint(prCreatedCheck, "True"), new FlowEndpoint(requestReview)),
                new(new FlowEndpoint(prCreatedCheck, "False"), new FlowEndpoint(failedEnd)),

                // Request review -> monitor review (bookmark)
                new(requestReview, monitorReview),

                // Monitor review outcomes:
                //   Approved -> merge
                //   ChangesRequested -> store comments -> increment -> deliver guidance
                //   TimedOut -> escalate timeout
                new(new FlowEndpoint(monitorReview, "Approved"), new FlowEndpoint(mergeAndComplete)),
                new(new FlowEndpoint(monitorReview, "ChangesRequested"), new FlowEndpoint(storeReviewComments)),
                new(new FlowEndpoint(monitorReview, "Commented"), new FlowEndpoint(monitorReview)),
                new(new FlowEndpoint(monitorReview, "TimedOut"), new FlowEndpoint(escalateTimeout)),

                // Store comments -> increment iteration -> deliver guidance
                new(storeReviewComments, incrementIteration),
                new(incrementIteration, deliverGuidance),

                // Deliver guidance -> wait for fixes (bookmark)
                new(deliverGuidance, waitForFixes),

                // Wait for fixes outcomes:
                //   FixesReceived -> re-request review
                //   TimedOut -> escalate timeout
                new(new FlowEndpoint(waitForFixes, "FixesReceived"), new FlowEndpoint(reRequestReview)),
                new(new FlowEndpoint(waitForFixes, "TimedOut"), new FlowEndpoint(escalateTimeout)),

                // Re-request review -> check max iterations
                new(reRequestReview, maxIterationsCheck),

                // Max iterations? true -> escalate, false -> back to monitor
                new(new FlowEndpoint(maxIterationsCheck, "True"), new FlowEndpoint(escalateReview)),
                new(new FlowEndpoint(maxIterationsCheck, "False"), new FlowEndpoint(monitorReview)),

                // Merge -> success end
                new(mergeAndComplete, successEnd),

                // Escalation outcomes (both escalate activities):
                //   Resolved -> merge
                //   Rejected -> fail
                new(new FlowEndpoint(escalateReview, "Resolved"), new FlowEndpoint(mergeAndComplete)),
                new(new FlowEndpoint(escalateReview, "Rejected"), new FlowEndpoint(failedEnd)),
                new(new FlowEndpoint(escalateTimeout, "Resolved"), new FlowEndpoint(mergeAndComplete)),
                new(new FlowEndpoint(escalateTimeout, "Rejected"), new FlowEndpoint(failedEnd))
            }
        };
    }
}
