using System.Text.Json;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime.Activities;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;
using FlowConnection = Elsa.Workflows.Activities.Flowchart.Models.Connection;

using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Plan Review — 7-role LLM panel reviews the implementation plan.
/// Roles: Architect, Developer, QA, Security, DevOps, PO, Senior Developer (orchestrator perspective).
/// Iterative discussion rounds until consensus or max 3 rounds.
///
/// Flow:
///   Init → Sequential Review (7 roles via llm-call) → Aggregate Verdicts → All Approve?
///     Yes → Output (approved + plan)
///     No  → Discussion Round (PO sees all concerns via llm-call)
///           → Resolve Each Concern (fix/defer/split/accept/needsHuman)
///           → Has Modified Plan? → Re-review (max 3 rounds)
///           → Output (decision + modified plan + deferred + split + discussion log)
///
/// Inputs: repository, issueNumber, planJson, contextIds
/// Outputs: decision, planJson, reviewNotes, deferred, split, discussionLog
/// </summary>
public class PlanReviewWorkflow : WorkflowBase
{
    // The 7 reviewing roles
    private static readonly string[] ReviewRoles =
    [
        "architect",
        "developer",
        "tester",
        "security",
        "devops",
        "product_owner",
        "senior_developer",
    ];

    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Plan Review";
        builder.DefinitionId = "plan-review";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "7-role LLM panel reviews the implementation plan with iterative discussion";

        // ================================================================
        // Variables
        // ================================================================
        var repository = builder.WithVariable<string>("Repository", "");
        var issueNumber = builder.WithVariable<int>("IssueNumber", 0);
        var planJson = builder.WithVariable<string>("PlanJson", "");
        var contextIds = builder.WithVariable<string>("ContextIds", "[]");
        var workItemJson = builder.WithVariable<string>("WorkItemJson", "");

        // Per-role review results (JSON strings)
        var architectReview = builder.WithVariable<string>("ArchitectReview", "{}");
        var developerReview = builder.WithVariable<string>("DeveloperReview", "{}");
        var testerReview = builder.WithVariable<string>("TesterReview", "{}");
        var securityReview = builder.WithVariable<string>("SecurityReview", "{}");
        var devopsReview = builder.WithVariable<string>("DevOpsReview", "{}");
        var productOwnerReview = builder.WithVariable<string>("ProductOwnerReview", "{}");
        var seniorDeveloperReview = builder.WithVariable<string>("SeniorDeveloperReview", "{}");

        // Aggregation
        var allReviewsJson = builder.WithVariable<string>("AllReviewsJson", "[]");
        var allApproved = builder.WithVariable<bool>("AllApproved", false);

        // Discussion
        var roundCount = builder.WithVariable<int>("RoundCount", 0);
        var discussionLog = builder.WithVariable<string>("DiscussionLog", "[]");
        var discussionResult = builder.WithVariable<string>("DiscussionResult", "{}");

        // Final outputs
        var decision = builder.WithVariable<string>("Decision", "needsHuman");
        var reviewNotes = builder.WithVariable<string>("ReviewNotes", "");
        var deferred = builder.WithVariable<string>("Deferred", "[]");
        var split = builder.WithVariable<string>("Split", "[]");

        // Shared LLM result
        var llmResult = builder.WithVariable<IDictionary<string, object>?>();

        // Role-variable mapping for extraction
        var roleVariables = new Dictionary<string, Variable<string>>
        {
            ["architect"] = architectReview,
            ["developer"] = developerReview,
            ["tester"] = testerReview,
            ["security"] = securityReview,
            ["devops"] = devopsReview,
            ["product_owner"] = productOwnerReview,
            ["senior_developer"] = seniorDeveloperReview,
        };

        // ================================================================
        // 1. Init — read inputs, set round count to 1
        // ================================================================
        var init = new SetVariable
        {
            Id = "Init", Name = "Initialize",
            Variable = repository,
            Value = new Input<object?>(ctx =>
            {
                var repo = ctx.GetInput<string>("repository") ?? "";
                issueNumber.Set(ctx, ctx.GetInput<int>("issueNumber"));
                planJson.Set(ctx, ctx.GetInput<string>("planJson") ?? "");
                contextIds.Set(ctx, ctx.GetInput<string>("contextIds") ?? "[]");
                workItemJson.Set(ctx, ctx.GetInput<string>("workItemJson") ?? "");
                roundCount.Set(ctx, 1);
                discussionLog.Set(ctx, "[]");
                return (object)repo;
            })
        };
        init.SetDisplayText("Initialize");

        // ================================================================
        // 2. Role Reviews — 7 sequential llm-call dispatches
        // Each role: action="plan-review", gets plan + context
        // ================================================================

        // Architect review
        var archReviewCall = RoleReviewDispatch("ArchReview", "Architect Review", "architect",
            repository, planJson, contextIds, workItemJson, allReviewsJson, llmResult);
        var extractArch = ExtractReview(architectReview, llmResult, "architect",
            "ExtractArchReview", "Extract Architect Review");

        // Developer review
        var devReviewCall = RoleReviewDispatch("DevReview", "Developer Review", "developer",
            repository, planJson, contextIds, workItemJson, allReviewsJson, llmResult);
        var extractDev = ExtractReview(developerReview, llmResult, "developer",
            "ExtractDevReview", "Extract Developer Review");

        // Tester review
        var testerReviewCall = RoleReviewDispatch("TesterReview", "Tester Review", "tester",
            repository, planJson, contextIds, workItemJson, allReviewsJson, llmResult);
        var extractTester = ExtractReview(testerReview, llmResult, "tester",
            "ExtractTesterReview", "Extract Tester Review");

        // Security review
        var secReviewCall = RoleReviewDispatch("SecReview", "Security Review", "security",
            repository, planJson, contextIds, workItemJson, allReviewsJson, llmResult);
        var extractSec = ExtractReview(securityReview, llmResult, "security",
            "ExtractSecReview", "Extract Security Review");

        // DevOps review
        var devopsReviewCall = RoleReviewDispatch("DevOpsReview", "DevOps Review", "devops",
            repository, planJson, contextIds, workItemJson, allReviewsJson, llmResult);
        var extractDevOps = ExtractReview(devopsReview, llmResult, "devops",
            "ExtractDevOpsReview", "Extract DevOps Review");

        // Product Owner review
        var poReviewCall = RoleReviewDispatch("POReview", "PO Review", "product_owner",
            repository, planJson, contextIds, workItemJson, allReviewsJson, llmResult);
        var extractPO = ExtractReview(productOwnerReview, llmResult, "product_owner",
            "ExtractPOReview", "Extract PO Review");

        // Senior Developer review (orchestrator perspective)
        var srDevReviewCall = RoleReviewDispatch("SrDevReview", "Senior Dev Review", "senior_developer",
            repository, planJson, contextIds, workItemJson, allReviewsJson, llmResult);
        var extractSrDev = ExtractReview(seniorDeveloperReview, llmResult, "senior_developer",
            "ExtractSrDevReview", "Extract Senior Dev Review");

        // ================================================================
        // 3. Aggregate Verdicts — collect all reviews, check if all approved
        // ================================================================
        var aggregate = new SetVariable
        {
            Id = "Aggregate", Name = "Aggregate Verdicts",
            Variable = allApproved,
            Value = new Input<object?>(ctx =>
            {
                var reviews = new List<object>();
                var approved = true;

                foreach (var role in ReviewRoles)
                {
                    var reviewJson = roleVariables[role].Get(ctx);
                    var verdict = "concerns"; // default pessimistic
                    var comments = "";
                    var suggestedChanges = "";

                    try
                    {
                        if (!string.IsNullOrWhiteSpace(reviewJson) && reviewJson != "{}")
                        {
                            var doc = JsonDocument.Parse(reviewJson);
                            var root = doc.RootElement;
                            if (root.TryGetProperty("verdict", out var v))
                                verdict = v.GetString() ?? "concerns";
                            if (root.TryGetProperty("comments", out var c))
                                comments = c.GetString() ?? "";
                            if (root.TryGetProperty("suggestedChanges", out var s))
                                suggestedChanges = s.GetString() ?? "";
                        }
                    }
                    catch
                    {
                        // Treat parse errors as concerns
                        comments = reviewJson;
                    }

                    if (verdict != "approve")
                        approved = false;

                    reviews.Add(new Dictionary<string, object>
                    {
                        ["role"] = role,
                        ["verdict"] = verdict,
                        ["comments"] = comments,
                        ["suggestedChanges"] = suggestedChanges,
                    });
                }

                var reviewsArray = JsonSerializer.Serialize(reviews);
                allReviewsJson.Set(ctx, reviewsArray);

                // Append to discussion log
                var currentLog = discussionLog.Get(ctx);
                var logEntries = new List<object>();
                try
                {
                    if (!string.IsNullOrWhiteSpace(currentLog) && currentLog != "[]")
                        logEntries = JsonSerializer.Deserialize<List<object>>(currentLog) ?? [];
                }
                catch { /* start fresh */ }

                var round = roundCount.Get(ctx);
                foreach (var review in reviews)
                {
                    logEntries.Add(new Dictionary<string, object>
                    {
                        ["round"] = round,
                        ["type"] = "review",
                        ["data"] = review,
                    });
                }
                discussionLog.Set(ctx, JsonSerializer.Serialize(logEntries));

                return (object)approved;
            })
        };
        aggregate.SetDisplayText("Aggregate Verdicts");

        // ================================================================
        // 4. All Approved? — branch
        // ================================================================
        var allApprovedCheck = new FlowDecision(ctx => allApproved.Get(ctx))
        { Id = "AllApproved", Name = "All Approved?" };
        allApprovedCheck.SetDisplayText("All Approved?");

        // ================================================================
        // 5. Approved path — set decision = "approved"
        // ================================================================
        var setApproved = new SetVariable
        {
            Id = "SetApproved", Name = "Set Approved",
            Variable = decision,
            Value = new Input<object?>(ctx =>
            {
                reviewNotes.Set(ctx, "All 7 reviewers approved the plan.");
                return (object)"approved";
            })
        };
        setApproved.SetDisplayText("Set Approved");

        // ================================================================
        // 6. Discussion Round — PO + all roles see all concerns
        //    Dispatches llm-call with role=product_owner, action="plan-review-discussion"
        //    Produces: resolution for each concern + optional modified plan
        // ================================================================
        var discussionCall = new DispatchWorkflow
        {
            Id = "DiscussionRound", Name = "Discussion Round",
            WorkflowDefinitionId = new("llm-call"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["role"] = "product_owner",
                ["action"] = "plan-review-discussion",
                ["variables"] = new Dictionary<string, object>
                {
                    ["planJson"] = planJson.Get(ctx),
                    ["contextIds"] = contextIds.Get(ctx),
                    ["workItemJson"] = workItemJson.Get(ctx),
                    ["allReviews"] = allReviewsJson.Get(ctx),
                    ["roundNumber"] = roundCount.Get(ctx),
                    ["previousDiscussion"] = discussionLog.Get(ctx),
                },
                ["enableTools"] = true,
            }),
            WaitForCompletion = new(true),
            Result = new(llmResult),
        };
        discussionCall.SetDisplayText("Discussion Round");

        // ================================================================
        // 7. Extract Discussion Result
        //    Expected JSON:
        //    {
        //      "resolutions": [{ "concern": "...", "resolution": "fix|defer|split|accept|needsHuman", "detail": "..." }],
        //      "modifiedPlan": "..." (optional, JSON string of new plan),
        //      "deferred": [{ "title": "...", "body": "...", "labels": [], "reason": "..." }],
        //      "split": [{ "title": "...", "body": "...", "labels": [] }],
        //      "overallDecision": "approved|needsModification|defer|split|needsHuman",
        //      "reviewNotes": "..."
        //    }
        // ================================================================
        var extractDiscussion = new SetVariable
        {
            Id = "ExtractDiscussion", Name = "Extract Discussion",
            Variable = discussionResult,
            Value = new Input<object?>(ctx =>
            {
                var result = llmResult.Get(ctx);
                var output = "";
                if (result != null && result.TryGetValue("llmResponse", out var r))
                    output = r?.ToString() ?? "";

                // Extract JSON block
                var jsonStart = output.IndexOf('{');
                var jsonEnd = output.LastIndexOf('}');
                if (jsonStart >= 0 && jsonEnd > jsonStart)
                    output = output[jsonStart..(jsonEnd + 1)];

                // Parse and apply
                try
                {
                    var doc = JsonDocument.Parse(output);
                    var root = doc.RootElement;

                    // Extract modified plan if present
                    if (root.TryGetProperty("modifiedPlan", out var mp))
                    {
                        var modifiedPlan = mp.ValueKind == JsonValueKind.String
                            ? mp.GetString() ?? ""
                            : mp.GetRawText();
                        if (!string.IsNullOrWhiteSpace(modifiedPlan) && modifiedPlan != "{}")
                            planJson.Set(ctx, modifiedPlan);
                    }

                    // Extract deferred items
                    if (root.TryGetProperty("deferred", out var def))
                        deferred.Set(ctx, def.GetRawText());

                    // Extract split items
                    if (root.TryGetProperty("split", out var sp))
                        split.Set(ctx, sp.GetRawText());

                    // Extract overall decision
                    if (root.TryGetProperty("overallDecision", out var od))
                        decision.Set(ctx, od.GetString() ?? "needsHuman");

                    // Extract review notes
                    if (root.TryGetProperty("reviewNotes", out var rn))
                        reviewNotes.Set(ctx, rn.GetString() ?? "");

                    // Append resolutions to discussion log
                    if (root.TryGetProperty("resolutions", out var res))
                    {
                        var currentLog = discussionLog.Get(ctx);
                        var logEntries = new List<object>();
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(currentLog) && currentLog != "[]")
                                logEntries = JsonSerializer.Deserialize<List<object>>(currentLog) ?? [];
                        }
                        catch { /* start fresh */ }

                        var round = roundCount.Get(ctx);
                        logEntries.Add(new Dictionary<string, object>
                        {
                            ["round"] = round,
                            ["type"] = "discussion",
                            ["resolutions"] = res.GetRawText(),
                        });
                        discussionLog.Set(ctx, JsonSerializer.Serialize(logEntries));
                    }
                }
                catch
                {
                    // Couldn't parse discussion — escalate
                    decision.Set(ctx, "needsHuman");
                    reviewNotes.Set(ctx, $"Failed to parse discussion result: {output}");
                }

                return (object)output;
            })
        };
        extractDiscussion.SetDisplayText("Extract Discussion");

        // ================================================================
        // 8. Needs Re-review? — check if decision is needsModification
        //    and round < 3
        // ================================================================
        var needsReReview = new FlowDecision(ctx =>
        {
            var d = decision.Get(ctx);
            return d == "needsModification";
        })
        { Id = "NeedsReReview", Name = "Needs Re-review?" };
        needsReReview.SetDisplayText("Needs Re-review?");

        // ================================================================
        // 9. Increment round + check max
        // ================================================================
        var incrementRound = new SetVariable
        {
            Id = "IncrRound", Name = "Increment Round",
            Variable = roundCount,
            Value = new Input<object?>(ctx => (object)(roundCount.Get(ctx) + 1))
        };
        incrementRound.SetDisplayText("Increment Round");

        var canContinue = new FlowDecision(ctx => roundCount.Get(ctx) <= 3)
        { Id = "CanContinue", Name = "Round <= 3?" };
        canContinue.SetDisplayText("Round <= 3?");

        // ================================================================
        // 10. Max rounds exceeded — force needsHuman
        // ================================================================
        var forceNeedsHuman = new SetVariable
        {
            Id = "ForceNeedsHuman", Name = "Force Needs Human",
            Variable = decision,
            Value = new Input<object?>(ctx =>
            {
                reviewNotes.Set(ctx, "Max review rounds (3) exceeded without consensus. Escalating to human.");
                return (object)"needsHuman";
            })
        };
        forceNeedsHuman.SetDisplayText("Force Needs Human");

        // ================================================================
        // 11. Set Outputs
        // ================================================================
        var setOutputs = new Sequence
        {
            Id = "SetOutputs", Name = "Set Outputs",
            Activities =
            {
                new SetOutput
                    { Id = "OutDecision", OutputName = new("decision"), OutputValue = new(ctx => (object)decision.Get(ctx)) },
                new SetOutput
                    { Id = "OutPlanJson", OutputName = new("planJson"), OutputValue = new(ctx => (object)planJson.Get(ctx)) },
                new SetOutput
                    { Id = "OutReviewNotes", OutputName = new("reviewNotes"), OutputValue = new(ctx => (object)reviewNotes.Get(ctx)) },
                new SetOutput
                    { Id = "OutDeferred", OutputName = new("deferred"), OutputValue = new(ctx => (object)deferred.Get(ctx)) },
                new SetOutput
                    { Id = "OutSplit", OutputName = new("split"), OutputValue = new(ctx => (object)split.Get(ctx)) },
                new SetOutput
                    { Id = "OutDiscussionLog", OutputName = new("discussionLog"), OutputValue = new(ctx => (object)discussionLog.Get(ctx)) },
            }
        };
        setOutputs.SetDisplayText("Set Outputs");

        var finish = new Finish { Id = "Finish", Name = "Complete" };
        finish.SetDisplayText("Complete");

        // ================================================================
        // Flowchart
        // ================================================================
        builder.Root = new Flowchart
        {
            Id = "PlanReviewFlowchart",
            Start = init,
            Activities =
            {
                // Init
                init,

                // 7 role reviews (sequential)
                archReviewCall, extractArch,
                devReviewCall, extractDev,
                testerReviewCall, extractTester,
                secReviewCall, extractSec,
                devopsReviewCall, extractDevOps,
                poReviewCall, extractPO,
                srDevReviewCall, extractSrDev,

                // Aggregate + branch
                aggregate, allApprovedCheck,

                // Approved path
                setApproved,

                // Discussion path
                discussionCall, extractDiscussion,
                needsReReview, incrementRound, canContinue,
                forceNeedsHuman,

                // Outputs
                setOutputs, finish,
            },
            Connections =
            {
                // Init → sequential role reviews
                Connect(init, archReviewCall),
                Connect(archReviewCall, extractArch),
                Connect(extractArch, devReviewCall),
                Connect(devReviewCall, extractDev),
                Connect(extractDev, testerReviewCall),
                Connect(testerReviewCall, extractTester),
                Connect(extractTester, secReviewCall),
                Connect(secReviewCall, extractSec),
                Connect(extractSec, devopsReviewCall),
                Connect(devopsReviewCall, extractDevOps),
                Connect(extractDevOps, poReviewCall),
                Connect(poReviewCall, extractPO),
                Connect(extractPO, srDevReviewCall),
                Connect(srDevReviewCall, extractSrDev),

                // → Aggregate
                Connect(extractSrDev, aggregate),
                Connect(aggregate, allApprovedCheck),

                // All approved → set approved → outputs → finish
                ConnectOutcome(allApprovedCheck, "True", setApproved),
                Connect(setApproved, setOutputs),

                // Not all approved → discussion
                ConnectOutcome(allApprovedCheck, "False", discussionCall),
                Connect(discussionCall, extractDiscussion),
                Connect(extractDiscussion, needsReReview),

                // needsModification → increment round → check max
                ConnectOutcome(needsReReview, "True", incrementRound),
                Connect(incrementRound, canContinue),

                // round <= 3 → loop back to re-review (arch review starts again)
                ConnectOutcome(canContinue, "True", archReviewCall),

                // round > 3 → force needsHuman → outputs
                ConnectOutcome(canContinue, "False", forceNeedsHuman),
                Connect(forceNeedsHuman, setOutputs),

                // Not needsModification (approved/defer/split/needsHuman from discussion)
                // → go straight to outputs
                ConnectOutcome(needsReReview, "False", setOutputs),

                // Outputs → finish
                Connect(setOutputs, finish),
            }
        };
    }

    // ================================================================
    // Helper: Create a DispatchWorkflow for a role review
    // ================================================================
    private static DispatchWorkflow RoleReviewDispatch(
        string id, string displayName, string role,
        Variable<string> repository, Variable<string> planJson,
        Variable<string> contextIds, Variable<string> workItemJson,
        Variable<string> allReviewsJson,
        Variable<IDictionary<string, object>?> result)
    {
        var dispatch = new DispatchWorkflow
        {
            Id = id, Name = displayName,
            WorkflowDefinitionId = new("llm-call"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["role"] = role,
                ["action"] = "plan-review",
                ["variables"] = new Dictionary<string, object>
                {
                    ["planJson"] = planJson.Get(ctx),
                    ["contextIds"] = contextIds.Get(ctx),
                    ["workItemJson"] = workItemJson.Get(ctx),
                    ["previousReviews"] = allReviewsJson.Get(ctx),
                },
                ["enableTools"] = true,
            }),
            WaitForCompletion = new(true),
            Result = new(result),
        };
        dispatch.SetDisplayText(displayName);
        return dispatch;
    }

    // ================================================================
    // Helper: Extract a role's review from llmResult into a variable
    // ================================================================
    private static SetVariable ExtractReview(
        Variable<string> target,
        Variable<IDictionary<string, object>?> llmResult,
        string role,
        string id, string displayName)
    {
        var sv = new SetVariable
        {
            Id = id, Name = displayName,
            Variable = target,
            Value = new Input<object?>(ctx =>
            {
                var result = llmResult.Get(ctx);
                if (result != null && result.TryGetValue("llmResponse", out var r))
                {
                    var output = r?.ToString() ?? "{}";

                    // Try to extract JSON from the response
                    var jsonStart = output.IndexOf('{');
                    var jsonEnd = output.LastIndexOf('}');
                    if (jsonStart >= 0 && jsonEnd > jsonStart)
                    {
                        var jsonCandidate = output[jsonStart..(jsonEnd + 1)];
                        try
                        {
                            // Validate it's parseable JSON
                            JsonDocument.Parse(jsonCandidate);
                            return (object)jsonCandidate;
                        }
                        catch
                        {
                            // Not valid JSON — wrap as comments
                        }
                    }

                    // Fallback: wrap raw text as a concerns review
                    return (object)JsonSerializer.Serialize(new Dictionary<string, string>
                    {
                        ["verdict"] = "concerns",
                        ["comments"] = output,
                        ["suggestedChanges"] = "",
                    });
                }
                return (object)"{}";
            })
        };
        sv.SetDisplayText(displayName);
        return sv;
    }

    // ================================================================
    // Connection helpers
    // ================================================================
    private static FlowConnection Connect(IActivity source, IActivity target)
        => new(new FlowEndpoint(source), new FlowEndpoint(target));

    private static FlowConnection ConnectOutcome(IActivity source, string outcome, IActivity target)
        => new(new FlowEndpoint(source, outcome), new FlowEndpoint(target));
}
