using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime.Activities;
using Tamma.Activities.ADL;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;
using FlowConnection = Elsa.Workflows.Activities.Flowchart.Models.Connection;

using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// Context Gathering — sequential role-based codebase scanning.
///
/// Each role scans from their perspective, accumulating findings.
/// If a role finds nothing relevant, it skips. Each role sees what
/// previous roles found.
///
/// Pipeline:
///   Dev → QA → Security → DevOps → Architect → Store in Vector DB → PO Summary
///
/// Flow:
///   Init → Dev Scan → QA Scan → Security Scan → DevOps Scan → Architect Scan
///   → Store Findings → PO Review → Output (summary + context IDs + links)
/// </summary>
public class ContextGatheringWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Context Gathering";
        builder.DefinitionId = "context-gathering";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Sequential role-based codebase scanning with vector DB storage and PO summary";

        // ================================================================
        // Variables
        // ================================================================
        var repository = builder.WithVariable<string>("Repository", "");
        var issueNumber = builder.WithVariable<int>("IssueNumber", 0);
        var workItemJson = builder.WithVariable<string>("WorkItemJson", "");
        var workItemType = builder.WithVariable<string>("WorkItemType", "feature");

        // Accumulated findings from each role
        var devFindingsJson = builder.WithVariable<string>("DevFindings", "{}");
        var qaFindingsJson = builder.WithVariable<string>("QAFindings", "{}");
        var securityFindingsJson = builder.WithVariable<string>("SecurityFindings", "{}");
        var devopsFindingsJson = builder.WithVariable<string>("DevOpsFindings", "{}");
        var architectFindingsJson = builder.WithVariable<string>("ArchitectFindings", "{}");

        // Storage + output
        var contextIdsJson = builder.WithVariable<string>("ContextIds", "[]");
        var poSummary = builder.WithVariable<string>("POSummary", "");
        var linksJson = builder.WithVariable<string>("Links", "[]");

        var subResult = builder.WithVariable<IDictionary<string, object>?>();

        // ================================================================
        // 1. Init — extract inputs
        // ================================================================
        var init = new SetVariable
        {
            Id = "Init",
            Name = "Initialize",
            Variable = repository,
            Value = new Input<object?>(ctx =>
            {
                var repo = ctx.GetInput<string>("repository") ?? "";
                issueNumber.Set(ctx, ctx.GetInput<int>("issueNumber"));
                workItemJson.Set(ctx, ctx.GetInput<string>("workItemJson") ?? "");

                // Determine work item type
                var itemJson = ctx.GetInput<string>("workItemJson") ?? "";
                var type = "feature";
                if (itemJson.Contains("\"type\":\"bug\"", System.StringComparison.OrdinalIgnoreCase)) type = "bug";
                else if (itemJson.Contains("\"type\":\"security", System.StringComparison.OrdinalIgnoreCase)) type = "security";
                else if (itemJson.Contains("\"type\":\"test", System.StringComparison.OrdinalIgnoreCase)) type = "test";
                else if (itemJson.Contains("\"type\":\"docs", System.StringComparison.OrdinalIgnoreCase)) type = "docs";
                else if (itemJson.Contains("\"type\":\"chore", System.StringComparison.OrdinalIgnoreCase)) type = "chore";
                workItemType.Set(ctx, type);

                return (object)repo;
            })
        };
        init.SetDisplayText("Initialize");

        // ================================================================
        // 2. Dev Scan — source files, interfaces, deps, patterns
        // ================================================================
        var devScan = new RoleScanActivity
        {
            Id = "DevScan",
            Name = "Dev Scan",
            Role = new Input<string>("developer"),
            Repository = new Input<string>(ctx => repository.Get(ctx)),
            WorkItemJson = new Input<string>(ctx => workItemJson.Get(ctx)),
            WorkItemType = new Input<string>(ctx => workItemType.Get(ctx)),
            PreviousFindingsJson = new Input<string>("{}"),
            ScanPrompt = new Input<string>("Scan the codebase for source files, interfaces, dependencies, and implementation patterns relevant to this work item. Focus on files that will need to be modified or referenced."),
            FindingsJson = new Output<string>(devFindingsJson),
        };
        devScan.SetDisplayText("Dev Scan");

        // ================================================================
        // 3. QA Scan — existing tests, coverage, test patterns
        // ================================================================
        var qaScan = new RoleScanActivity
        {
            Id = "QAScan",
            Name = "QA Scan",
            Role = new Input<string>("tester"),
            Repository = new Input<string>(ctx => repository.Get(ctx)),
            WorkItemJson = new Input<string>(ctx => workItemJson.Get(ctx)),
            WorkItemType = new Input<string>(ctx => workItemType.Get(ctx)),
            PreviousFindingsJson = new Input<string>(ctx => devFindingsJson.Get(ctx)),
            ScanPrompt = new Input<string>("Based on the dev findings, scan for existing tests, coverage gaps, test patterns, fixtures, and mocking approaches. Identify what tests exist for the affected code and what's missing."),
            FindingsJson = new Output<string>(qaFindingsJson),
        };
        qaScan.SetDisplayText("QA Scan");

        // ================================================================
        // 4. Security Scan — attack surface, input validation, auth
        // ================================================================
        var securityScan = new RoleScanActivity
        {
            Id = "SecurityScan",
            Name = "Security Scan",
            Role = new Input<string>("security"),
            Repository = new Input<string>(ctx => repository.Get(ctx)),
            WorkItemJson = new Input<string>(ctx => workItemJson.Get(ctx)),
            WorkItemType = new Input<string>(ctx => workItemType.Get(ctx)),
            PreviousFindingsJson = new Input<string>(ctx =>
                $"{{\"dev\":{devFindingsJson.Get(ctx)},\"qa\":{qaFindingsJson.Get(ctx)}}}"),
            ScanPrompt = new Input<string>("Review the affected code for security concerns: input validation, authentication, authorization, injection risks, sensitive data handling. Skip if no security relevance."),
            FindingsJson = new Output<string>(securityFindingsJson),
        };
        securityScan.SetDisplayText("Security Scan");

        // ================================================================
        // 5. DevOps Scan — deploy config, CI, infrastructure
        // ================================================================
        var devopsScan = new RoleScanActivity
        {
            Id = "DevOpsScan",
            Name = "DevOps Scan",
            Role = new Input<string>("devops"),
            Repository = new Input<string>(ctx => repository.Get(ctx)),
            WorkItemJson = new Input<string>(ctx => workItemJson.Get(ctx)),
            WorkItemType = new Input<string>(ctx => workItemType.Get(ctx)),
            PreviousFindingsJson = new Input<string>(ctx =>
                $"{{\"dev\":{devFindingsJson.Get(ctx)},\"qa\":{qaFindingsJson.Get(ctx)},\"security\":{securityFindingsJson.Get(ctx)}}}"),
            ScanPrompt = new Input<string>("Check for deployment impact: Docker configs, CI workflows, environment variables, infrastructure changes needed. Skip if no deployment relevance."),
            FindingsJson = new Output<string>(devopsFindingsJson),
        };
        devopsScan.SetDisplayText("DevOps Scan");

        // ================================================================
        // 6. Architect Scan — patterns, conventions, interfaces
        // ================================================================
        var architectScan = new RoleScanActivity
        {
            Id = "ArchitectScan",
            Name = "Architect Scan",
            Role = new Input<string>("architect"),
            Repository = new Input<string>(ctx => repository.Get(ctx)),
            WorkItemJson = new Input<string>(ctx => workItemJson.Get(ctx)),
            WorkItemType = new Input<string>(ctx => workItemType.Get(ctx)),
            PreviousFindingsJson = new Input<string>(ctx =>
                $"{{\"dev\":{devFindingsJson.Get(ctx)},\"qa\":{qaFindingsJson.Get(ctx)},\"security\":{securityFindingsJson.Get(ctx)},\"devops\":{devopsFindingsJson.Get(ctx)}}}"),
            ScanPrompt = new Input<string>("Review architecture: coding patterns, naming conventions, CLAUDE.md rules, interface design, module boundaries. Identify conventions the implementation must follow."),
            FindingsJson = new Output<string>(architectFindingsJson),
        };
        architectScan.SetDisplayText("Architect Scan");

        // ================================================================
        // 7. Store Findings in Vector DB
        // ================================================================
        var storeFindings = new StoreFindingsActivity
        {
            Id = "StoreFindings",
            Name = "Store in Vector DB",
            Repository = new Input<string>(ctx => repository.Get(ctx)),
            IssueNumber = new Input<int>(ctx => issueNumber.Get(ctx)),
            DevFindingsJson = new Input<string>(ctx => devFindingsJson.Get(ctx)),
            QAFindingsJson = new Input<string>(ctx => qaFindingsJson.Get(ctx)),
            SecurityFindingsJson = new Input<string>(ctx => securityFindingsJson.Get(ctx)),
            DevOpsFindingsJson = new Input<string>(ctx => devopsFindingsJson.Get(ctx)),
            ArchitectFindingsJson = new Input<string>(ctx => architectFindingsJson.Get(ctx)),
            ContextIdsJson = new Output<string>(contextIdsJson),
        };
        storeFindings.SetDisplayText("Store in Vector DB");

        // ================================================================
        // 8. PO Review — summarize all findings
        // ================================================================
        var poReview = new POContextReviewActivity
        {
            Id = "POReview",
            Name = "PO Review",
            Repository = new Input<string>(ctx => repository.Get(ctx)),
            WorkItemJson = new Input<string>(ctx => workItemJson.Get(ctx)),
            AllFindingsJson = new Input<string>(ctx =>
                $"{{\"dev\":{devFindingsJson.Get(ctx)},\"qa\":{qaFindingsJson.Get(ctx)},\"security\":{securityFindingsJson.Get(ctx)},\"devops\":{devopsFindingsJson.Get(ctx)},\"architect\":{architectFindingsJson.Get(ctx)}}}"),
            ContextIdsJson = new Input<string>(ctx => contextIdsJson.Get(ctx)),
            Summary = new Output<string>(poSummary),
            LinksJson = new Output<string>(linksJson),
        };
        poReview.SetDisplayText("PO Review");

        // ================================================================
        // 9. Set Outputs
        // ================================================================
        var setOutputs = new Sequence
        {
            Id = "SetOutputs",
            Name = "Set Outputs",
            Activities =
            {
                new Elsa.Workflows.Management.Activities.SetOutput.SetOutput
                    { Id = "OutSummary", OutputName = new("summary"), OutputValue = new(ctx => (object)poSummary.Get(ctx)) },
                new Elsa.Workflows.Management.Activities.SetOutput.SetOutput
                    { Id = "OutContextIds", OutputName = new("contextIds"), OutputValue = new(ctx => (object)contextIdsJson.Get(ctx)) },
                new Elsa.Workflows.Management.Activities.SetOutput.SetOutput
                    { Id = "OutLinks", OutputName = new("links"), OutputValue = new(ctx => (object)linksJson.Get(ctx)) },
            }
        };
        setOutputs.SetDisplayText("Set Outputs");

        var finish = new Finish { Id = "Finish", Name = "Complete" };
        finish.SetDisplayText("Complete");

        // ================================================================
        // Flowchart — sequential pipeline
        // ================================================================
        builder.Root = new Flowchart
        {
            Id = "ContextGatheringFlowchart",
            Start = init,
            Activities =
            {
                init, devScan, qaScan, securityScan, devopsScan, architectScan,
                storeFindings, poReview, setOutputs, finish,
            },
            Connections =
            {
                Connect(init, devScan),
                Connect(devScan, qaScan),
                Connect(qaScan, securityScan),
                Connect(securityScan, devopsScan),
                Connect(devopsScan, architectScan),
                Connect(architectScan, storeFindings),
                Connect(storeFindings, poReview),
                Connect(poReview, setOutputs),
                Connect(setOutputs, finish),
            }
        };
    }

    private static FlowConnection Connect(IActivity source, IActivity target)
        => new(new FlowEndpoint(source), new FlowEndpoint(target));
}
