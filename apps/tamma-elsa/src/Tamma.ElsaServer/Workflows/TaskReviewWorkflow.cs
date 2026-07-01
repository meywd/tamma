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

using Tamma.Api.Services.Agents;
using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Task Review — 4-role LLM panel reviews implementation tasks.
/// Roles: Architect, Senior Developer, Developer, Tester.
/// No discussion rounds (unlike PlanReview). All must approve for "approved".
///
/// Flow:
///   Init → Architect Review → Sr Dev Review → Dev Review → Tester Review
///   → Aggregate Verdicts → All Approve?
///     ├─ Yes → Output (approved)
///     └─ No  → Output (needsChanges + review notes)
///
/// Inputs: repository, issueNumber, tasksJson, planJson
/// Outputs: decision (approved/needsChanges/needsHuman), tasksJson, reviewNotes
/// </summary>
public class TaskReviewWorkflow : WorkflowBase
{
    private static readonly string[] ReviewRoles =
    [
        "architect",
        "senior_developer",
        "developer",
        "tester",
    ];

    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Task Review";
        builder.DefinitionId = "task-review";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "4-role panel reviews implementation tasks before execution";

        // ================================================================
        // Variables
        // ================================================================
        var repository = builder.WithVariable<string>("Repository", "");
        var issueNumber = builder.WithVariable<int>("IssueNumber", 0);
        var tasksJson = builder.WithVariable<string>("TasksJson", "[]");
        var planJson = builder.WithVariable<string>("PlanJson", "");

        // Per-role review results
        var architectReview = builder.WithVariable<string>("ArchitectReview", "{}");
        var seniorDevReview = builder.WithVariable<string>("SeniorDevReview", "{}");
        var developerReview = builder.WithVariable<string>("DeveloperReview", "{}");
        var testerReview = builder.WithVariable<string>("TesterReview", "{}");

        // Aggregation
        var allReviewsJson = builder.WithVariable<string>("AllReviewsJson", "[]");
        var allApproved = builder.WithVariable<bool>("AllApproved", false);

        var tenantId = builder.WithVariable<string>("TenantId", "");

        // Final outputs
        var decision = builder.WithVariable<string>("Decision", "needsHuman");
        var reviewNotes = builder.WithVariable<string>("ReviewNotes", "");

        // Shared LLM result
        var llmResult = builder.WithVariable<IDictionary<string, object>?>();

        // Role-variable mapping
        var roleVariables = new Dictionary<string, Variable<string>>
        {
            ["architect"] = architectReview,
            ["senior_developer"] = seniorDevReview,
            ["developer"] = developerReview,
            ["tester"] = testerReview,
        };

        // ================================================================
        // 1. Init
        // ================================================================
        var init = new SetVariable
        {
            Id = "Init", Name = "Initialize",
            Variable = repository,
            Value = new Input<object?>(ctx =>
            {
                var repo = ctx.GetInput<string>("repository") ?? "";
                issueNumber.Set(ctx, ctx.GetInput<int>("issueNumber"));
                tasksJson.Set(ctx, ctx.GetInput<string>("tasksJson") ?? "[]");
                planJson.Set(ctx, ctx.GetInput<string>("planJson") ?? "");
                tenantId.Set(ctx, ctx.GetInput<string>("tenantId") ?? "");
                return (object)repo;
            })
        };
        init.SetDisplayText("Initialize");

        // ================================================================
        // 2. Role Reviews — 4 sequential llm-call dispatches
        // ================================================================

        // Architect review
        var archReviewCall = RoleReviewDispatch("ArchReview", "Architect Review", AgentRole.Architect,
            repository, tasksJson, planJson, allReviewsJson, tenantId, llmResult);
        var extractArch = ExtractReview(architectReview, llmResult,
            "ExtractArchReview", "Extract Architect Review");

        // Senior Developer review
        var srDevReviewCall = RoleReviewDispatch("SrDevReview", "Sr Dev Review", AgentRole.SeniorDeveloper,
            repository, tasksJson, planJson, allReviewsJson, tenantId, llmResult);
        var extractSrDev = ExtractReview(seniorDevReview, llmResult,
            "ExtractSrDevReview", "Extract Sr Dev Review");

        // Developer review
        var devReviewCall = RoleReviewDispatch("DevReview", "Developer Review", AgentRole.Developer,
            repository, tasksJson, planJson, allReviewsJson, tenantId, llmResult);
        var extractDev = ExtractReview(developerReview, llmResult,
            "ExtractDevReview", "Extract Developer Review");

        // Tester review
        var testerReviewCall = RoleReviewDispatch("TesterReview", "Tester Review", AgentRole.Tester,
            repository, tasksJson, planJson, allReviewsJson, tenantId, llmResult);
        var extractTester = ExtractReview(testerReview, llmResult,
            "ExtractTesterReview", "Extract Tester Review");

        // ================================================================
        // 3. Aggregate Verdicts
        // ================================================================
        var aggregate = new SetVariable
        {
            Id = "Aggregate", Name = "Aggregate Verdicts",
            Variable = allApproved,
            Value = new Input<object?>(ctx =>
            {
                var reviews = new List<object>();
                var approved = true;
                var notes = new List<string>();

                foreach (var role in ReviewRoles)
                {
                    var reviewJson = roleVariables[role].Get(ctx);
                    var verdict = "concerns";
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
                        comments = reviewJson;
                    }

                    if (verdict != "approve")
                    {
                        approved = false;
                        if (!string.IsNullOrWhiteSpace(comments))
                            notes.Add($"[{role}] {comments}");
                        if (!string.IsNullOrWhiteSpace(suggestedChanges))
                            notes.Add($"[{role} changes] {suggestedChanges}");
                    }

                    reviews.Add(new Dictionary<string, object>
                    {
                        ["role"] = role,
                        ["verdict"] = verdict,
                        ["comments"] = comments,
                        ["suggestedChanges"] = suggestedChanges,
                    });
                }

                allReviewsJson.Set(ctx, JsonSerializer.Serialize(reviews));
                reviewNotes.Set(ctx, string.Join("\n", notes));

                return (object)approved;
            })
        };
        aggregate.SetDisplayText("Aggregate Verdicts");

        // ================================================================
        // 4. All Approved?
        // ================================================================
        var allApprovedCheck = new FlowDecision(ctx => allApproved.Get(ctx))
        { Id = "AllApproved", Name = "All Approved?" };
        allApprovedCheck.SetDisplayText("All Approved?");

        // ================================================================
        // 5. Approved path
        // ================================================================
        var setApproved = new SetVariable
        {
            Id = "SetApproved", Name = "Set Approved",
            Variable = decision,
            Value = new Input<object?>(ctx =>
            {
                reviewNotes.Set(ctx, "All 4 reviewers approved the tasks.");
                return (object)"approved";
            })
        };
        setApproved.SetDisplayText("Set Approved");

        // ================================================================
        // 6. Not approved path — set needsChanges
        // ================================================================
        var setNeedsChanges = new SetVariable
        {
            Id = "SetNeedsChanges", Name = "Set Needs Changes",
            Variable = decision,
            Value = new Input<object?>(_ => (object)"needsChanges")
        };
        setNeedsChanges.SetDisplayText("Set Needs Changes");

        // ================================================================
        // 7. Set Outputs
        // ================================================================
        var setOutputs = new Sequence
        {
            Id = "SetOutputs", Name = "Set Outputs",
            Activities =
            {
                new SetOutput
                    { Id = "OutDecision", OutputName = new("decision"), OutputValue = new(ctx => (object)decision.Get(ctx)) },
                new SetOutput
                    { Id = "OutTasksJson", OutputName = new("tasksJson"), OutputValue = new(ctx => (object)tasksJson.Get(ctx)) },
                new SetOutput
                    { Id = "OutReviewNotes", OutputName = new("reviewNotes"), OutputValue = new(ctx => (object)reviewNotes.Get(ctx)) },
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
            Id = "TaskReviewFlowchart",
            Start = init,
            Activities =
            {
                init,

                // 4 role reviews (sequential)
                archReviewCall, extractArch,
                srDevReviewCall, extractSrDev,
                devReviewCall, extractDev,
                testerReviewCall, extractTester,

                // Aggregate + branch
                aggregate, allApprovedCheck,

                // Paths
                setApproved, setNeedsChanges,

                // Outputs
                setOutputs, finish,
            },
            Connections =
            {
                // Init → sequential role reviews
                Connect(init, archReviewCall),
                Connect(archReviewCall, extractArch),
                Connect(extractArch, srDevReviewCall),
                Connect(srDevReviewCall, extractSrDev),
                Connect(extractSrDev, devReviewCall),
                Connect(devReviewCall, extractDev),
                Connect(extractDev, testerReviewCall),
                Connect(testerReviewCall, extractTester),

                // → Aggregate
                Connect(extractTester, aggregate),
                Connect(aggregate, allApprovedCheck),

                // All approved → set approved → outputs → finish
                ConnectOutcome(allApprovedCheck, "True", setApproved),
                Connect(setApproved, setOutputs),

                // Not approved → set needsChanges → outputs → finish
                ConnectOutcome(allApprovedCheck, "False", setNeedsChanges),
                Connect(setNeedsChanges, setOutputs),

                // Outputs → finish
                Connect(setOutputs, finish),
            }
        };
    }

    // ================================================================
    // Helper: Create a DispatchWorkflow for a role review
    // ================================================================
    private static DispatchWorkflow RoleReviewDispatch(
        string id, string displayName, AgentRole role,
        Variable<string> repository, Variable<string> tasksJson,
        Variable<string> planJson, Variable<string> allReviewsJson,
        Variable<string> tenantId,
        Variable<IDictionary<string, object>?> result)
    {
        var dispatch = new DispatchWorkflow
        {
            Id = id, Name = displayName,
            WorkflowDefinitionId = new("llm-call"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["role"] = role.ToWire(),
                ["action"] = RolePhaseMap.GetReviewActionForRole(role).ToWire(),
                ["tenantId"] = tenantId.Get(ctx),
                ["variables"] = new Dictionary<string, object>
                {
                    ["tasksJson"] = tasksJson.Get(ctx),
                    ["planJson"] = planJson.Get(ctx),
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
    // Helper: Extract a role's review from llmResult
    // ================================================================
    private static SetVariable ExtractReview(
        Variable<string> target,
        Variable<IDictionary<string, object>?> llmResult,
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

                    var jsonStart = output.IndexOf('{');
                    var jsonEnd = output.LastIndexOf('}');
                    if (jsonStart >= 0 && jsonEnd > jsonStart)
                    {
                        var jsonCandidate = output[jsonStart..(jsonEnd + 1)];
                        try
                        {
                            JsonDocument.Parse(jsonCandidate);
                            return (object)jsonCandidate;
                        }
                        catch { /* not valid JSON */ }
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

    private static FlowConnection Connect(IActivity source, IActivity target)
        => new(new FlowEndpoint(source), new FlowEndpoint(target));

    private static FlowConnection ConnectOutcome(IActivity source, string outcome, IActivity target)
        => new(new FlowEndpoint(source, outcome), new FlowEndpoint(target));
}
