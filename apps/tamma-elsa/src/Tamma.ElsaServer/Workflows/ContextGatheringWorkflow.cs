using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime.Activities;
using Tamma.Activities.Context;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;
using FlowConnection = Elsa.Workflows.Activities.Flowchart.Models.Connection;

using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Context Gathering — sequential role-based codebase scanning via LLM Call sub-workflow.
///
/// Each role dispatches LlmCallWorkflow with role-specific prompt and tools.
/// Results accumulate — each role sees previous findings.
/// Stored in vector DB, PO summarizes.
///
/// Pipeline:
///   Init → Dev Scan (llm-call) → QA Scan (llm-call) → Security Scan (llm-call)
///   → DevOps Scan (llm-call) → Architect Scan (llm-call)
///   → Store in Vector DB → PO Review (llm-call) → Output
/// </summary>
public class ContextGatheringWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Context Gathering";
        builder.DefinitionId = "context-gathering";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Sequential role-based codebase scanning via LLM Call sub-workflow";

        // ================================================================
        // Variables
        // ================================================================
        var repository = builder.WithVariable<string>("Repository", "");
        var issueNumber = builder.WithVariable<int>("IssueNumber", 0);
        var workItemJson = builder.WithVariable<string>("WorkItemJson", "");
        var workItemType = builder.WithVariable<string>("WorkItemType", "feature");

        // Accumulated findings
        var devFindings = builder.WithVariable<string>("DevFindings", "{}");
        var qaFindings = builder.WithVariable<string>("QAFindings", "{}");
        var securityFindings = builder.WithVariable<string>("SecurityFindings", "{}");
        var devopsFindings = builder.WithVariable<string>("DevOpsFindings", "{}");
        var architectFindings = builder.WithVariable<string>("ArchitectFindings", "{}");

        // Output
        var contextIds = builder.WithVariable<string>("ContextIds", "[]");
        var poSummary = builder.WithVariable<string>("POSummary", "");
        var links = builder.WithVariable<string>("Links", "[]");

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
                issueNumber.Set(ctx, ctx.GetInput<int>("issueNumber"));
                workItemJson.Set(ctx, ctx.GetInput<string>("workItemJson") ?? "");
                var itemJson = ctx.GetInput<string>("workItemJson") ?? "";
                var type = "feature";
                if (itemJson.Contains("\"type\":\"bug\"", System.StringComparison.OrdinalIgnoreCase)) type = "bug";
                else if (itemJson.Contains("\"type\":\"security", System.StringComparison.OrdinalIgnoreCase)) type = "security";
                else if (itemJson.Contains("\"type\":\"test", System.StringComparison.OrdinalIgnoreCase)) type = "test";
                else if (itemJson.Contains("\"type\":\"docs", System.StringComparison.OrdinalIgnoreCase)) type = "docs";
                workItemType.Set(ctx, type);
                return (object)repo;
            })
        };
        init.SetDisplayText("Initialize");

        // ================================================================
        // 2-6. Role Scans — each dispatches LlmCallWorkflow
        // ================================================================
        var devScan = RoleScan("DevScan", "Dev Scan", "developer",
            repository, workItemJson, workItemType, "{}",
            llmResult);
        var extractDev = Extract(devFindings, llmResult, "ExtractDev", "Extract Dev Findings");

        var qaScan = RoleScan("QAScan", "QA Scan", "tester",
            repository, workItemJson, workItemType,
            ctx => devFindings.Get(ctx),
            llmResult);
        var extractQA = Extract(qaFindings, llmResult, "ExtractQA", "Extract QA Findings");

        var secScan = RoleScan("SecurityScan", "Security Scan", "security",
            repository, workItemJson, workItemType,
            ctx => $"{{\"dev\":{devFindings.Get(ctx)},\"qa\":{qaFindings.Get(ctx)}}}",
            llmResult);
        var extractSec = Extract(securityFindings, llmResult, "ExtractSec", "Extract Security Findings");

        var devopsScan = RoleScan("DevOpsScan", "DevOps Scan", "devops",
            repository, workItemJson, workItemType,
            ctx => $"{{\"dev\":{devFindings.Get(ctx)},\"qa\":{qaFindings.Get(ctx)},\"security\":{securityFindings.Get(ctx)}}}",
            llmResult);
        var extractDevOps = Extract(devopsFindings, llmResult, "ExtractDevOps", "Extract DevOps Findings");

        var archScan = RoleScan("ArchScan", "Architect Scan", "architect",
            repository, workItemJson, workItemType,
            ctx => $"{{\"dev\":{devFindings.Get(ctx)},\"qa\":{qaFindings.Get(ctx)},\"security\":{securityFindings.Get(ctx)},\"devops\":{devopsFindings.Get(ctx)}}}",
            llmResult);
        var extractArch = Extract(architectFindings, llmResult, "ExtractArch", "Extract Architect Findings");

        // ================================================================
        // 7. Store in Vector DB
        // ================================================================
        var store = new StoreFindingsActivity
        {
            Id = "StoreFindings", Name = "Store in Vector DB",
            Repository = new Input<string>(ctx => repository.Get(ctx)),
            IssueNumber = new Input<int>(ctx => issueNumber.Get(ctx)),
            DevFindingsJson = new Input<string>(ctx => devFindings.Get(ctx)),
            QAFindingsJson = new Input<string>(ctx => qaFindings.Get(ctx)),
            SecurityFindingsJson = new Input<string>(ctx => securityFindings.Get(ctx)),
            DevOpsFindingsJson = new Input<string>(ctx => devopsFindings.Get(ctx)),
            ArchitectFindingsJson = new Input<string>(ctx => architectFindings.Get(ctx)),
            ContextIdsJson = new Output<string>(contextIds),
        };
        store.SetDisplayText("Store in Vector DB");

        // ================================================================
        // 8. PO Review (via LlmCallWorkflow)
        // ================================================================
        var poReviewScan = new DispatchWorkflow
        {
            Id = "POReview", Name = "PO Review",
            WorkflowDefinitionId = new("llm-call"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["role"] = "product_owner",
                ["action"] = "summarize",
                ["variables"] = new Dictionary<string, object>
                {
                    ["workItemJson"] = workItemJson.Get(ctx),
                    ["devFindings"] = devFindings.Get(ctx),
                    ["qaFindings"] = qaFindings.Get(ctx),
                    ["securityFindings"] = securityFindings.Get(ctx),
                    ["devopsFindings"] = devopsFindings.Get(ctx),
                    ["architectFindings"] = architectFindings.Get(ctx),
                    ["contextIds"] = contextIds.Get(ctx),
                },
                ["enableTools"] = false,
            }),
            WaitForCompletion = new(true),
            Result = new(llmResult),
        };
        poReviewScan.SetDisplayText("PO Review");

        var extractPO = new SetVariable
        {
            Id = "ExtractPO", Name = "Extract PO Summary",
            Variable = poSummary,
            Value = new Input<object?>(ctx =>
            {
                var result = llmResult.Get(ctx);
                if (result != null && result.TryGetValue("llmResponse", out var r))
                {
                    var output = r?.ToString() ?? "";
                    poSummary.Set(ctx, output);
                    // Try to extract links
                    try
                    {
                        var jsonStart = output.IndexOf('{');
                        var jsonEnd = output.LastIndexOf('}');
                        if (jsonStart >= 0 && jsonEnd > jsonStart)
                        {
                            var parsed = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(output[jsonStart..(jsonEnd + 1)]);
                            if (parsed.TryGetProperty("links", out var l)) links.Set(ctx, l.GetRawText());
                            if (parsed.TryGetProperty("summary", out var s)) return (object)(s.GetString() ?? output);
                        }
                    }
                    catch { }
                    return (object)output;
                }
                return (object)"";
            })
        };
        extractPO.SetDisplayText("Extract PO Summary");

        // ================================================================
        // 9. Set Outputs
        // ================================================================
        var setOutputs = new Sequence
        {
            Id = "SetOutputs", Name = "Set Outputs",
            Activities =
            {
                new Elsa.Workflows.Management.Activities.SetOutput.SetOutput
                    { Id = "OutSummary", OutputName = new("summary"), OutputValue = new(ctx => (object)poSummary.Get(ctx)) },
                new Elsa.Workflows.Management.Activities.SetOutput.SetOutput
                    { Id = "OutContextIds", OutputName = new("contextIds"), OutputValue = new(ctx => (object)contextIds.Get(ctx)) },
                new Elsa.Workflows.Management.Activities.SetOutput.SetOutput
                    { Id = "OutLinks", OutputName = new("links"), OutputValue = new(ctx => (object)links.Get(ctx)) },
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
            Id = "ContextGatheringFlowchart",
            Start = init,
            Activities =
            {
                init,
                devScan, extractDev,
                qaScan, extractQA,
                secScan, extractSec,
                devopsScan, extractDevOps,
                archScan, extractArch,
                store, poReviewScan, extractPO,
                setOutputs, finish,
            },
            Connections =
            {
                Connect(init, devScan),
                Connect(devScan, extractDev),
                Connect(extractDev, qaScan),
                Connect(qaScan, extractQA),
                Connect(extractQA, secScan),
                Connect(secScan, extractSec),
                Connect(extractSec, devopsScan),
                Connect(devopsScan, extractDevOps),
                Connect(extractDevOps, archScan),
                Connect(archScan, extractArch),
                Connect(extractArch, store),
                Connect(store, poReviewScan),
                Connect(poReviewScan, extractPO),
                Connect(extractPO, setOutputs),
                Connect(setOutputs, finish),
            }
        };
    }

    /// <summary>
    /// Creates a DispatchWorkflow that calls LlmCallWorkflow with role + action + variables.
    /// Prompts are resolved from the prompt registry, not hardcoded.
    /// </summary>
    private static DispatchWorkflow RoleScan(
        string id, string name, string role,
        Variable<string> repository, Variable<string> workItemJson,
        Variable<string> workItemType, string previousFindings,
        Variable<IDictionary<string, object>?> result)
    {
        var dispatch = new DispatchWorkflow
        {
            Id = id, Name = name,
            WorkflowDefinitionId = new("llm-call"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["role"] = role,
                ["action"] = "context-scan",
                ["variables"] = new Dictionary<string, object>
                {
                    ["workItemJson"] = workItemJson.Get(ctx),
                    ["workItemType"] = workItemType.Get(ctx),
                    ["previousFindings"] = previousFindings,
                    ["repository"] = repository.Get(ctx),
                },
                ["enableTools"] = true,
            }),
            WaitForCompletion = new(true),
            Result = new(result),
        };
        dispatch.SetDisplayText(name);
        return dispatch;
    }

    /// <summary>
    /// Overload with dynamic previous findings.
    /// </summary>
    private static DispatchWorkflow RoleScan(
        string id, string name, string role,
        Variable<string> repository, Variable<string> workItemJson,
        Variable<string> workItemType,
        Func<Elsa.Expressions.Models.ExpressionExecutionContext, string> previousFindingsBuilder,
        Variable<IDictionary<string, object>?> result)
    {
        var dispatch = new DispatchWorkflow
        {
            Id = id, Name = name,
            WorkflowDefinitionId = new("llm-call"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["role"] = role,
                ["action"] = "context-scan",
                ["variables"] = new Dictionary<string, object>
                {
                    ["workItemJson"] = workItemJson.Get(ctx),
                    ["workItemType"] = workItemType.Get(ctx),
                    ["previousFindings"] = previousFindingsBuilder(ctx),
                    ["repository"] = repository.Get(ctx),
                },
                ["enableTools"] = true,
            }),
            WaitForCompletion = new(true),
            Result = new(result),
        };
        dispatch.SetDisplayText(name);
        return dispatch;
    }

    private static SetVariable Extract(Variable<string> target,
        Variable<IDictionary<string, object>?> result, string id, string name)
    {
        var sv = new SetVariable
        {
            Id = id, Name = name,
            Variable = target,
            Value = new Input<object?>(ctx =>
            {
                var r = result.Get(ctx);
                if (r != null && r.TryGetValue("llmResponse", out var o))
                    return (object)(o?.ToString() ?? "{}");
                return (object)"{}";
            })
        };
        sv.SetDisplayText(name);
        return sv;
    }

    private static FlowConnection Connect(IActivity source, IActivity target)
        => new(new FlowEndpoint(source), new FlowEndpoint(target));
}
