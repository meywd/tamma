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
/// Triage Panel Review — 4-role LLM panel assesses a triage item.
/// Roles: Security Analyst, Developer, DevOps, Tester.
///
/// Each role dispatches llm-call with a role-specific triage action
/// (security=assess-vulnerability, developer/tester=triage-defect,
/// devops=diagnose-incident).
/// Results are aggregated into a panel result JSON.
///
/// For security alerts: CVE impact, attack surface, breaking changes,
/// dependency chain, compatibility.
///
/// For issues: type classification, complexity estimate, scope.
///
/// Flow:
///   Init → Security Review → Dev Review → DevOps Review → Tester Review
///   → Aggregate Results → Output → Finish
///
/// Inputs: repository, itemJson, contextJson
/// Outputs: panelResultJson
/// </summary>
public class TriagePanelReviewWorkflow : WorkflowBase
{
    private static readonly string[] ReviewRoles =
    [
        "security",
        "developer",
        "devops",
        "tester",
    ];

    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Triage Panel Review";
        builder.DefinitionId = "triage-panel-review";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "4-role panel reviews item for triage (security/dev/devops/qa)";

        // ================================================================
        // Variables
        // ================================================================
        var repository = builder.WithVariable<string>("Repository", "");
        var itemJson = builder.WithVariable<string>("ItemJson", "");
        var contextJson = builder.WithVariable<string>("ContextJson", "{}");

        // Per-role review results
        var securityReview = builder.WithVariable<string>("SecurityReview", "{}");
        var developerReview = builder.WithVariable<string>("DeveloperReview", "{}");
        var devopsReview = builder.WithVariable<string>("DevOpsReview", "{}");
        var testerReview = builder.WithVariable<string>("TesterReview", "{}");

        // Aggregated result
        var panelResultJson = builder.WithVariable<string>("PanelResultJson", "{}");

        // Shared LLM result
        var llmResult = builder.WithVariable<IDictionary<string, object>?>();

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
                itemJson.Set(ctx, ctx.GetInput<string>("itemJson") ?? "");
                contextJson.Set(ctx, ctx.GetInput<string>("contextJson") ?? "{}");
                return (object)repo;
            })
        };
        init.SetDisplayText("Initialize");

        // ================================================================
        // 2. Role Reviews — 4 sequential llm-call dispatches
        // ================================================================

        // Security review
        var secReviewCall = RoleTriageDispatch("SecReview", "Security Review", AgentRole.Security,
            repository, itemJson, contextJson, llmResult);
        var extractSec = ExtractTriageReview(securityReview, llmResult,
            "ExtractSecReview", "Extract Security Review");

        // Developer review
        var devReviewCall = RoleTriageDispatch("DevReview", "Developer Review", AgentRole.Developer,
            repository, itemJson, contextJson, llmResult);
        var extractDev = ExtractTriageReview(developerReview, llmResult,
            "ExtractDevReview", "Extract Developer Review");

        // DevOps review
        var devopsReviewCall = RoleTriageDispatch("DevOpsReview", "DevOps Review", AgentRole.Devops,
            repository, itemJson, contextJson, llmResult);
        var extractDevOps = ExtractTriageReview(devopsReview, llmResult,
            "ExtractDevOpsReview", "Extract DevOps Review");

        // Tester review
        var testerReviewCall = RoleTriageDispatch("TesterReview", "Tester Review", AgentRole.Tester,
            repository, itemJson, contextJson, llmResult);
        var extractTester = ExtractTriageReview(testerReview, llmResult,
            "ExtractTesterReview", "Extract Tester Review");

        // ================================================================
        // 3. Aggregate Results
        // ================================================================
        var aggregate = new SetVariable
        {
            Id = "Aggregate", Name = "Aggregate Results",
            Variable = panelResultJson,
            Value = new Input<object?>(ctx =>
            {
                var roleVars = new Dictionary<string, Variable<string>>
                {
                    ["security"] = securityReview,
                    ["developer"] = developerReview,
                    ["devops"] = devopsReview,
                    ["tester"] = testerReview,
                };

                var reviews = new List<object>();
                foreach (var role in ReviewRoles)
                {
                    var reviewJson = roleVars[role].Get(ctx);
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(reviewJson) && reviewJson != "{}")
                        {
                            var parsed = JsonSerializer.Deserialize<JsonElement>(reviewJson);
                            reviews.Add(new Dictionary<string, object>
                            {
                                ["role"] = role,
                                ["assessment"] = parsed.GetRawText(),
                            });
                        }
                        else
                        {
                            reviews.Add(new Dictionary<string, object>
                            {
                                ["role"] = role,
                                ["assessment"] = "{}",
                            });
                        }
                    }
                    catch
                    {
                        reviews.Add(new Dictionary<string, object>
                        {
                            ["role"] = role,
                            ["assessment"] = JsonSerializer.Serialize(new { raw = reviewJson }),
                        });
                    }
                }

                var result = JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["reviews"] = reviews,
                    ["reviewCount"] = reviews.Count,
                });

                return (object)result;
            })
        };
        aggregate.SetDisplayText("Aggregate Results");

        // ================================================================
        // 4. Set Outputs
        // ================================================================
        var setOutputs = new SetOutput
        { Id = "OutPanelResult", OutputName = new("panelResultJson"), OutputValue = new(ctx => (object)panelResultJson.Get(ctx)) };
        setOutputs.SetDisplayText("Output Panel Result");

        var finish = new Finish { Id = "Finish", Name = "Complete" };
        finish.SetDisplayText("Complete");

        // ================================================================
        // Flowchart
        // ================================================================
        builder.Root = new Flowchart
        {
            Id = "TriagePanelReviewFlowchart",
            Start = init,
            Activities =
            {
                init,
                secReviewCall, extractSec,
                devReviewCall, extractDev,
                devopsReviewCall, extractDevOps,
                testerReviewCall, extractTester,
                aggregate,
                setOutputs, finish,
            },
            Connections =
            {
                // Init → sequential role reviews
                Connect(init, secReviewCall),
                Connect(secReviewCall, extractSec),
                Connect(extractSec, devReviewCall),
                Connect(devReviewCall, extractDev),
                Connect(extractDev, devopsReviewCall),
                Connect(devopsReviewCall, extractDevOps),
                Connect(extractDevOps, testerReviewCall),
                Connect(testerReviewCall, extractTester),

                // → Aggregate → Output → Finish
                Connect(extractTester, aggregate),
                Connect(aggregate, setOutputs),
                Connect(setOutputs, finish),
            }
        };
    }

    // ================================================================
    // Helper: Create a DispatchWorkflow for a triage role review
    // ================================================================
    private static DispatchWorkflow RoleTriageDispatch(
        string id, string displayName, AgentRole role,
        Variable<string> repository, Variable<string> itemJson,
        Variable<string> contextJson,
        Variable<IDictionary<string, object>?> result)
    {
        var dispatch = new DispatchWorkflow
        {
            Id = id, Name = displayName,
            WorkflowDefinitionId = new("llm-call"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["role"] = role.ToWire(),
                ["action"] = RolePhaseMap.GetTriageActionForRole(role).ToWire(),
                ["variables"] = new Dictionary<string, object>
                {
                    ["itemJson"] = itemJson.Get(ctx),
                    ["contextJson"] = contextJson.Get(ctx),
                    ["repository"] = repository.Get(ctx),
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
    // Helper: Extract a role's triage review from llmResult
    // ================================================================
    private static SetVariable ExtractTriageReview(
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

                    // Wrap raw text
                    return (object)JsonSerializer.Serialize(new Dictionary<string, string>
                    {
                        ["rawAssessment"] = output,
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
}
