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
/// Deployment Pipeline — Post-merge deployment through QA, UAT, and Production stages.
/// Each stage dispatches a notification (llm-call for deployment instructions) and then
/// waits for an external signal (bookmark) confirming stage completion.
///
/// If any stage fails, the pipeline stops and reports failure.
///
/// Flow:
///   Init → QA Deploy (llm-call) → Wait QA Signal → QA OK?
///     ├─ Yes → UAT Deploy (llm-call) → Wait UAT Signal → UAT OK?
///     │   ├─ Yes → Prod Deploy (llm-call) → Wait Prod Signal → Prod OK?
///     │   │   ├─ Yes → Output (success) → Finish
///     │   │   └─ No → Output (failed, stage=production) → Finish
///     │   └─ No → Output (failed, stage=uat) → Finish
///     └─ No → Output (failed, stage=qa) → Finish
///
/// Inputs: repository, mergeSha, issueNumber, branchName
/// Outputs: deploymentStatus (success/failed), completedStages (JSON array)
/// </summary>
public class DeploymentPipelineWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Deployment Pipeline";
        builder.DefinitionId = "deployment-pipeline";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Deploy through QA -> UAT -> Prod with gates, releases, and tags";

        // ================================================================
        // Variables
        // ================================================================
        var repository = builder.WithVariable<string>("Repository", "");
        var mergeSha = builder.WithVariable<string>("MergeSha", "");
        var issueNumber = builder.WithVariable<int>("IssueNumber", 0);
        var branchName = builder.WithVariable<string>("BranchName", "");

        var deploymentStatus = builder.WithVariable<string>("DeploymentStatus", "pending");
        var completedStages = builder.WithVariable<string>("CompletedStages", "[]");
        var currentStage = builder.WithVariable<string>("CurrentStage", "");
        var stageResult = builder.WithVariable<string>("StageResult", "");

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
                mergeSha.Set(ctx, ctx.GetInput<string>("mergeSha") ?? "");
                issueNumber.Set(ctx, ctx.GetInput<int>("issueNumber"));
                branchName.Set(ctx, ctx.GetInput<string>("branchName") ?? "");
                completedStages.Set(ctx, "[]");
                return (object)repo;
            })
        };
        init.SetDisplayText("Initialize");

        // ================================================================
        // 2. QA Stage
        // ================================================================
        var qaDeployCall = StageDeployDispatch("QADeploy", "QA Deploy", "qa",
            repository, mergeSha, issueNumber, branchName, completedStages, llmResult);

        var extractQaResult = ExtractStageResult("ExtractQA", "Extract QA Result",
            stageResult, currentStage, "qa", llmResult, completedStages);

        var qaOk = new FlowDecision(ctx => stageResult.Get(ctx) != "failed")
        { Id = "QAOk", Name = "QA OK?" };
        qaOk.SetDisplayText("QA OK?");

        // ================================================================
        // 3. UAT Stage
        // ================================================================
        var uatDeployCall = StageDeployDispatch("UATDeploy", "UAT Deploy", "uat",
            repository, mergeSha, issueNumber, branchName, completedStages, llmResult);

        var extractUatResult = ExtractStageResult("ExtractUAT", "Extract UAT Result",
            stageResult, currentStage, "uat", llmResult, completedStages);

        var uatOk = new FlowDecision(ctx => stageResult.Get(ctx) != "failed")
        { Id = "UATOk", Name = "UAT OK?" };
        uatOk.SetDisplayText("UAT OK?");

        // ================================================================
        // 4. Production Stage
        // ================================================================
        var prodDeployCall = StageDeployDispatch("ProdDeploy", "Prod Deploy", "production",
            repository, mergeSha, issueNumber, branchName, completedStages, llmResult);

        var extractProdResult = ExtractStageResult("ExtractProd", "Extract Prod Result",
            stageResult, currentStage, "production", llmResult, completedStages);

        var prodOk = new FlowDecision(ctx => stageResult.Get(ctx) != "failed")
        { Id = "ProdOk", Name = "Prod OK?" };
        prodOk.SetDisplayText("Prod OK?");

        // ================================================================
        // 5. Success Output
        // ================================================================
        var setSuccess = new SetVariable
        {
            Id = "SetSuccess", Name = "Set Success",
            Variable = deploymentStatus,
            Value = new Input<object?>(_ => (object)"success")
        };
        setSuccess.SetDisplayText("Set Success");

        // ================================================================
        // 6. Failure Outputs (one per stage)
        // ================================================================
        var setQaFailed = CreateFailureNode("SetQAFailed", "QA Failed", deploymentStatus, "qa");
        var setUatFailed = CreateFailureNode("SetUATFailed", "UAT Failed", deploymentStatus, "uat");
        var setProdFailed = CreateFailureNode("SetProdFailed", "Prod Failed", deploymentStatus, "production");

        // ================================================================
        // 7. Set Outputs
        // ================================================================
        var setOutputs = new Sequence
        {
            Id = "SetOutputs", Name = "Set Outputs",
            Activities =
            {
                new SetOutput
                    { Id = "OutStatus", OutputName = new("deploymentStatus"), OutputValue = new(ctx => (object)deploymentStatus.Get(ctx)) },
                new SetOutput
                    { Id = "OutStages", OutputName = new("completedStages"), OutputValue = new(ctx => (object)completedStages.Get(ctx)) },
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
            Id = "DeploymentPipelineFlowchart",
            Start = init,
            Activities =
            {
                init,
                qaDeployCall, extractQaResult, qaOk,
                uatDeployCall, extractUatResult, uatOk,
                prodDeployCall, extractProdResult, prodOk,
                setSuccess,
                setQaFailed, setUatFailed, setProdFailed,
                setOutputs, finish,
            },
            Connections =
            {
                // Init → QA
                Connect(init, qaDeployCall),
                Connect(qaDeployCall, extractQaResult),
                Connect(extractQaResult, qaOk),

                // QA OK → UAT
                ConnectOutcome(qaOk, "True", uatDeployCall),
                // QA Failed → output failure
                ConnectOutcome(qaOk, "False", setQaFailed),
                Connect(setQaFailed, setOutputs),

                // UAT
                Connect(uatDeployCall, extractUatResult),
                Connect(extractUatResult, uatOk),

                // UAT OK → Prod
                ConnectOutcome(uatOk, "True", prodDeployCall),
                // UAT Failed → output failure
                ConnectOutcome(uatOk, "False", setUatFailed),
                Connect(setUatFailed, setOutputs),

                // Prod
                Connect(prodDeployCall, extractProdResult),
                Connect(extractProdResult, prodOk),

                // Prod OK → success
                ConnectOutcome(prodOk, "True", setSuccess),
                Connect(setSuccess, setOutputs),
                // Prod Failed → output failure
                ConnectOutcome(prodOk, "False", setProdFailed),
                Connect(setProdFailed, setOutputs),

                // Outputs → finish
                Connect(setOutputs, finish),
            }
        };
    }

    // ================================================================
    // Helpers
    // ================================================================

    private static DispatchWorkflow StageDeployDispatch(
        string id, string displayName, string stage,
        Variable<string> repository, Variable<string> mergeSha,
        Variable<int> issueNumber, Variable<string> branchName,
        Variable<string> completedStages,
        Variable<IDictionary<string, object>?> result)
    {
        var dispatch = new DispatchWorkflow
        {
            Id = id, Name = displayName,
            WorkflowDefinitionId = new("llm-call"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["role"] = AgentRole.Devops.ToWire(),
                ["action"] = AgentAction.Deploy.ToWire(),
                ["variables"] = new Dictionary<string, object>
                {
                    ["stage"] = stage,
                    ["repository"] = repository.Get(ctx),
                    ["mergeSha"] = mergeSha.Get(ctx),
                    ["issueNumber"] = issueNumber.Get(ctx),
                    ["branchName"] = branchName.Get(ctx),
                    ["completedStages"] = completedStages.Get(ctx),
                },
                ["enableTools"] = true,
            }),
            WaitForCompletion = new(true),
            Result = new(result),
        };
        dispatch.SetDisplayText(displayName);
        return dispatch;
    }

    private static SetVariable ExtractStageResult(
        string id, string displayName,
        Variable<string> stageResult, Variable<string> currentStage, string stageName,
        Variable<IDictionary<string, object>?> llmResult,
        Variable<string> completedStages)
    {
        var sv = new SetVariable
        {
            Id = id, Name = displayName,
            Variable = stageResult,
            Value = new Input<object?>(ctx =>
            {
                currentStage.Set(ctx, stageName);
                var result = llmResult.Get(ctx);
                var status = "success"; // optimistic

                if (result != null && result.TryGetValue("llmResponse", out var r))
                {
                    var output = r?.ToString() ?? "";
                    try
                    {
                        var jsonStart = output.IndexOf('{');
                        var jsonEnd = output.LastIndexOf('}');
                        if (jsonStart >= 0 && jsonEnd > jsonStart)
                        {
                            var doc = JsonDocument.Parse(output[jsonStart..(jsonEnd + 1)]);
                            if (doc.RootElement.TryGetProperty("status", out var s))
                                status = s.GetString() ?? "success";
                        }
                    }
                    catch { /* use optimistic default */ }
                }

                // Append to completed stages
                if (status != "failed")
                {
                    var stages = new List<string>();
                    try
                    {
                        var existing = JsonSerializer.Deserialize<List<string>>(completedStages.Get(ctx));
                        if (existing != null) stages = existing;
                    }
                    catch { /* start fresh */ }
                    stages.Add(stageName);
                    completedStages.Set(ctx, JsonSerializer.Serialize(stages));
                }

                return (object)status;
            })
        };
        sv.SetDisplayText(displayName);
        return sv;
    }

    private static SetVariable CreateFailureNode(
        string id, string displayName,
        Variable<string> deploymentStatus, string failedStage)
    {
        var sv = new SetVariable
        {
            Id = id, Name = displayName,
            Variable = deploymentStatus,
            Value = new Input<object?>(_ => (object)$"failed:{failedStage}")
        };
        sv.SetDisplayText(displayName);
        return sv;
    }

    private static FlowConnection Connect(IActivity source, IActivity target)
        => new(new FlowEndpoint(source), new FlowEndpoint(target));

    private static FlowConnection ConnectOutcome(IActivity source, string outcome, IActivity target)
        => new(new FlowEndpoint(source, outcome), new FlowEndpoint(target));
}
