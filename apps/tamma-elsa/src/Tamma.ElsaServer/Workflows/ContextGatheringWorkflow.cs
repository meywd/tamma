using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime.Activities;
using Tamma.Activities.Context;
using Tamma.Api.Services.Agents;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;
using FlowConnection = Elsa.Workflows.Activities.Flowchart.Models.Connection;

using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Context Gathering — sequential role-based codebase scanning via LLM Call sub-workflow.
///
/// Each role dispatches LlmCallWorkflow with role-specific prompt and tools.
/// Results accumulate — each role sees previous findings.
/// Each role's findings are stored in the vector DB immediately after extraction,
/// so partial results persist even if later scans fail.
/// PO summarizes all findings at the end.
///
/// Pipeline:
///   Init → Dev Scan → Store Dev → QA Scan → Store QA → Security Scan → Store Sec
///   → DevOps Scan → Store DevOps → Architect Scan → Store Arch
///   → PO Review (llm-call) → Output
/// </summary>
public class ContextGatheringWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Context Gathering";
        builder.DefinitionId = "context-gathering";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Sequential role-based codebase scanning with per-role vector DB storage";

        // ================================================================
        // Variables
        // ================================================================
        var tenantId = builder.WithVariable<string>("TenantId", "").Persisted();
        var repository = builder.WithVariable<string>("Repository", "").Persisted();
        var issueNumber = builder.WithVariable<int>("IssueNumber", 0).Persisted();
        var workItemJson = builder.WithVariable<string>("WorkItemJson", "").Persisted();
        var workItemType = builder.WithVariable<string>("WorkItemType", "feature").Persisted();

        // Accumulated findings
        var devFindings = builder.WithVariable<string>("DevFindings", "{}").Persisted();
        var qaFindings = builder.WithVariable<string>("QAFindings", "{}").Persisted();
        var securityFindings = builder.WithVariable<string>("SecurityFindings", "{}").Persisted();
        var devopsFindings = builder.WithVariable<string>("DevOpsFindings", "{}").Persisted();
        var architectFindings = builder.WithVariable<string>("ArchitectFindings", "{}").Persisted();

        // Context IDs accumulated from per-role storage
        var contextIds = builder.WithVariable<string>("ContextIds", "[]").Persisted();
        var poSummary = builder.WithVariable<string>("POSummary", "").Persisted();
        var links = builder.WithVariable<string>("Links", "[]").Persisted();

        var llmResult = builder.WithVariable<IDictionary<string, object>?>().Persisted();

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
                tenantId.Set(ctx, ctx.GetInput<string>("tenantId") ?? "");
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
        // 2-6. Role Scans — each: LLM call → extract → store in vector DB
        // ================================================================

        // Dev Scan
        var devScan = RoleScan("DevScan", "Dev Scan", AgentRole.Developer,
            repository, workItemJson, workItemType, "{}",
            tenantId, llmResult);
        var extractDev = Extract(devFindings, llmResult, "ExtractDev", "Extract Dev Findings");
        var storeDev = StoreRole("StoreDev", "Store Dev", AgentRole.Developer.ToWire(),
            repository, issueNumber, devFindings, contextIds);

        // QA Scan
        var qaScan = RoleScan("QAScan", "QA Scan", AgentRole.Tester,
            repository, workItemJson, workItemType,
            ctx => devFindings.Get(ctx),
            tenantId, llmResult);
        var extractQA = Extract(qaFindings, llmResult, "ExtractQA", "Extract QA Findings");
        var storeQA = StoreRole("StoreQA", "Store QA", AgentRole.Tester.ToWire(),
            repository, issueNumber, qaFindings, contextIds);

        // Security Scan
        var secScan = RoleScan("SecurityScan", "Security Scan", AgentRole.Security,
            repository, workItemJson, workItemType,
            ctx => System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["dev"] = devFindings.Get(ctx),
                ["qa"] = qaFindings.Get(ctx),
            }),
            tenantId, llmResult);
        var extractSec = Extract(securityFindings, llmResult, "ExtractSec", "Extract Security Findings");
        var storeSec = StoreRole("StoreSec", "Store Security", AgentRole.Security.ToWire(),
            repository, issueNumber, securityFindings, contextIds);

        // DevOps Scan
        var devopsScan = RoleScan("DevOpsScan", "DevOps Scan", AgentRole.Devops,
            repository, workItemJson, workItemType,
            ctx => System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["dev"] = devFindings.Get(ctx),
                ["qa"] = qaFindings.Get(ctx),
                ["security"] = securityFindings.Get(ctx),
            }),
            tenantId, llmResult);
        var extractDevOps = Extract(devopsFindings, llmResult, "ExtractDevOps", "Extract DevOps Findings");
        var storeDevOps = StoreRole("StoreDevOps", "Store DevOps", AgentRole.Devops.ToWire(),
            repository, issueNumber, devopsFindings, contextIds);

        // Architect Scan
        var archScan = RoleScan("ArchScan", "Architect Scan", AgentRole.Architect,
            repository, workItemJson, workItemType,
            ctx => System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["dev"] = devFindings.Get(ctx),
                ["qa"] = qaFindings.Get(ctx),
                ["security"] = securityFindings.Get(ctx),
                ["devops"] = devopsFindings.Get(ctx),
            }),
            tenantId, llmResult);
        var extractArch = Extract(architectFindings, llmResult, "ExtractArch", "Extract Architect Findings");
        var storeArch = StoreRole("StoreArch", "Store Architect", AgentRole.Architect.ToWire(),
            repository, issueNumber, architectFindings, contextIds);

        // ================================================================
        // 7. PO Review (via LlmCallWorkflow)
        // ================================================================
        var poReviewScan = new DispatchWorkflow
        {
            Id = "POReview", Name = "PO Review",
            WorkflowDefinitionId = new("llm-call"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["role"] = AgentRole.ProductOwner.ToWire(),
                ["action"] = AgentAction.SummarizeStakeholder.ToWire(),
                ["tenantId"] = tenantId.Get(ctx),
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
        // 8. Set Outputs
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
                devScan, extractDev, storeDev,
                qaScan, extractQA, storeQA,
                secScan, extractSec, storeSec,
                devopsScan, extractDevOps, storeDevOps,
                archScan, extractArch, storeArch,
                poReviewScan, extractPO,
                setOutputs, finish,
            },
            Connections =
            {
                Connect(init, devScan),
                Connect(devScan, extractDev),
                Connect(extractDev, storeDev),
                Connect(storeDev, qaScan),

                Connect(qaScan, extractQA),
                Connect(extractQA, storeQA),
                Connect(storeQA, secScan),

                Connect(secScan, extractSec),
                Connect(extractSec, storeSec),
                Connect(storeSec, devopsScan),

                Connect(devopsScan, extractDevOps),
                Connect(extractDevOps, storeDevOps),
                Connect(storeDevOps, archScan),

                Connect(archScan, extractArch),
                Connect(extractArch, storeArch),
                Connect(storeArch, poReviewScan),

                Connect(poReviewScan, extractPO),
                Connect(extractPO, setOutputs),
                Connect(setOutputs, finish),
            }
        };
    }

    // ================================================================
    // Helpers
    // ================================================================

    private static DispatchWorkflow RoleScan(
        string id, string name, AgentRole role,
        Variable<string> repository, Variable<string> workItemJson,
        Variable<string> workItemType, string previousFindings,
        Variable<string> tenantId,
        Variable<IDictionary<string, object>?> result)
    {
        var dispatch = new DispatchWorkflow
        {
            Id = id, Name = name,
            WorkflowDefinitionId = new("llm-call"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["role"] = role.ToWire(),
                ["action"] = AgentAction.ContextScan.ToWire(),
                ["tenantId"] = tenantId.Get(ctx),
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

    private static DispatchWorkflow RoleScan(
        string id, string name, AgentRole role,
        Variable<string> repository, Variable<string> workItemJson,
        Variable<string> workItemType,
        Func<Elsa.Expressions.Models.ExpressionExecutionContext, string> previousFindingsBuilder,
        Variable<string> tenantId,
        Variable<IDictionary<string, object>?> result)
    {
        var dispatch = new DispatchWorkflow
        {
            Id = id, Name = name,
            WorkflowDefinitionId = new("llm-call"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["role"] = role.ToWire(),
                ["action"] = AgentAction.ContextScan.ToWire(),
                ["tenantId"] = tenantId.Get(ctx),
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

    /// <summary>
    /// Store one role's findings immediately. Appends the returned context ID
    /// to the accumulated contextIds JSON array.
    /// </summary>
    private static StoreRoleFindingActivity StoreRole(
        string id, string name, string role,
        Variable<string> repository, Variable<int> issueNumber,
        Variable<string> findingsVar, Variable<string> contextIds)
    {
        var store = new StoreRoleFindingActivity
        {
            Id = id, Name = name,
            Repository = new Input<string>(ctx => repository.Get(ctx)),
            IssueNumber = new Input<int>(ctx => issueNumber.Get(ctx)),
            Role = new Input<string>(role),
            FindingsJson = new Input<string>(ctx => findingsVar.Get(ctx)),
            ContextId = new Output<string>(new Variable<string>()),
        };
        store.SetDisplayText(name);
        return store;
    }

    private static FlowConnection Connect(IActivity source, IActivity target)
        => new(new FlowEndpoint(source), new FlowEndpoint(target));
}
