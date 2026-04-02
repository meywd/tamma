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
/// Issue Triage Workflow — fetches untriaged items (issues, security alerts,
/// CodeQL, Dependabot), runs a panel review for each, then PO decides
/// priority and labels.
///
/// Flow:
///   Fetch Untriaged Items → For Each Item:
///     Gather Context → Panel Review (security/dev/devops/qa)
///     → PO Decision → Apply Labels → Post Comment
///
/// Triggered by:
///   - ADL Orchestrator (NeedsTriage outcome)
///   - GitHub webhook (issues.opened)
///   - Manual dispatch
/// </summary>
public class IssueTriageWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "Issue Triage";
        builder.DefinitionId = "issue-triage";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Triage untriaged items: panel review + PO decision + labels";

        // ================================================================
        // Variables
        // ================================================================
        var repository = builder.WithVariable<string>("Repository", "");
        var itemsJson = builder.WithVariable<string>("ItemsJson", "[]");
        var currentItemIndex = builder.WithVariable<int>("CurrentItemIndex", 0);
        var totalItems = builder.WithVariable<int>("TotalItems", 0);
        var currentItemJson = builder.WithVariable<string>("CurrentItemJson", "");
        var contextJson = builder.WithVariable<string>("ContextJson", "");
        var panelResultJson = builder.WithVariable<string>("PanelResultJson", "");
        var poDecisionJson = builder.WithVariable<string>("PODecisionJson", "");
        var triagedCount = builder.WithVariable<int>("TriagedCount", 0);
        var subResult = builder.WithVariable<IDictionary<string, object>?>();

        // ================================================================
        // 1. Fetch Untriaged Items (issues + security alerts + CodeQL + Dependabot)
        // ================================================================
        var fetchItems = new FetchUntriagedItemsActivity
        {
            Id = "FetchItems",
            Name = "Fetch Untriaged Items",
            Repository = new Input<string>(ctx =>
            {
                var repo = ctx.GetInput<string>("repository") ?? "";
                repository.Set(ctx, repo);
                return repo;
            }),
            ItemsJson = new Output<string>(itemsJson),
            TotalCount = new Output<int>(totalItems),
        };
        fetchItems.SetDisplayText("Fetch Untriaged Items");

        // ================================================================
        // 2. Has Items?
        // ================================================================
        var hasItems = new FlowDecision(ctx => totalItems.Get(ctx) > 0)
        {
            Id = "HasItems",
            Name = "Has Items?"
        };
        hasItems.SetDisplayText("Has Items?");

        // ================================================================
        // 3. Extract Current Item
        // ================================================================
        var extractItem = new SetVariable
        {
            Id = "ExtractItem",
            Name = "Extract Current Item",
            Variable = currentItemJson,
            Value = new Input<object?>(ctx =>
            {
                try
                {
                    var items = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(itemsJson.Get(ctx));
                    var idx = currentItemIndex.Get(ctx);
                    return (object)items[idx].GetRawText();
                }
                catch { return (object)"{}"; }
            })
        };
        extractItem.SetDisplayText("Extract Current Item");

        // ================================================================
        // 4. Gather Context (code usage, deps, CVE details)
        // ================================================================
        var gatherContext = new DispatchWorkflow
        {
            Id = "GatherTriageContext",
            Name = "Gather Triage Context",
            WorkflowDefinitionId = new("triage-context-gathering"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["repository"] = repository.Get(ctx),
                ["itemJson"] = currentItemJson.Get(ctx),
            }),
            WaitForCompletion = new(true),
            Result = new(subResult),
        };
        gatherContext.SetDisplayText("Gather Triage Context");

        var extractContext = new SetVariable
        {
            Id = "ExtractContext",
            Name = "Extract Context",
            Variable = contextJson,
            Value = new Input<object?>(ctx =>
            {
                var result = subResult.Get(ctx);
                if (result != null && result.TryGetValue("contextJson", out var c))
                    return (object)(c?.ToString() ?? "");
                return (object)"";
            })
        };
        extractContext.SetDisplayText("Extract Context");

        // ================================================================
        // 5. Panel Review (security analyst, dev, devops, qa)
        // ================================================================
        var panelReview = new DispatchWorkflow
        {
            Id = "PanelReview",
            Name = "Panel Review",
            WorkflowDefinitionId = new("triage-panel-review"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["repository"] = repository.Get(ctx),
                ["itemJson"] = currentItemJson.Get(ctx),
                ["contextJson"] = contextJson.Get(ctx),
            }),
            WaitForCompletion = new(true),
            Result = new(subResult),
        };
        panelReview.SetDisplayText("Panel Review");

        var extractPanelResult = new SetVariable
        {
            Id = "ExtractPanelResult",
            Name = "Extract Panel Result",
            Variable = panelResultJson,
            Value = new Input<object?>(ctx =>
            {
                var result = subResult.Get(ctx);
                if (result != null && result.TryGetValue("panelResultJson", out var p))
                    return (object)(p?.ToString() ?? "");
                return (object)"";
            })
        };
        extractPanelResult.SetDisplayText("Extract Panel Result");

        // ================================================================
        // 6. PO Decision (priority, labels, automation level)
        // ================================================================
        var poDecision = new DispatchWorkflow
        {
            Id = "PODecision",
            Name = "PO Decision",
            WorkflowDefinitionId = new("triage-po-decision"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["repository"] = repository.Get(ctx),
                ["itemJson"] = currentItemJson.Get(ctx),
                ["panelResultJson"] = panelResultJson.Get(ctx),
            }),
            WaitForCompletion = new(true),
            Result = new(subResult),
        };
        poDecision.SetDisplayText("PO Decision");

        var extractDecision = new SetVariable
        {
            Id = "ExtractDecision",
            Name = "Extract Decision",
            Variable = poDecisionJson,
            Value = new Input<object?>(ctx =>
            {
                var result = subResult.Get(ctx);
                if (result != null && result.TryGetValue("decisionJson", out var d))
                    return (object)(d?.ToString() ?? "");
                return (object)"";
            })
        };
        extractDecision.SetDisplayText("Extract Decision");

        // ================================================================
        // 7. Apply Labels + Post Comment (fire & forget)
        // ================================================================
        var applyLabels = new ApplyTriageResultActivity
        {
            Id = "ApplyLabels",
            Name = "Apply Labels & Comment",
            Repository = new Input<string>(ctx => repository.Get(ctx)),
            ItemJson = new Input<string>(ctx => currentItemJson.Get(ctx)),
            DecisionJson = new Input<string>(ctx => poDecisionJson.Get(ctx)),
        };
        applyLabels.SetDisplayText("Apply Labels & Comment");

        // ================================================================
        // 8. Increment + Loop
        // ================================================================
        var incrementTriaged = new SetVariable
        {
            Id = "IncrTriaged",
            Name = "Increment Triaged",
            Variable = triagedCount,
            Value = new Input<object?>(ctx => (object)(triagedCount.Get(ctx) + 1))
        };
        incrementTriaged.SetDisplayText("Increment Triaged");

        var incrementIndex = new SetVariable
        {
            Id = "IncrIndex",
            Name = "Next Item",
            Variable = currentItemIndex,
            Value = new Input<object?>(ctx => (object)(currentItemIndex.Get(ctx) + 1))
        };
        incrementIndex.SetDisplayText("Next Item");

        var hasMoreItems = new FlowDecision(ctx => currentItemIndex.Get(ctx) < totalItems.Get(ctx))
        {
            Id = "HasMoreItems",
            Name = "More Items?"
        };
        hasMoreItems.SetDisplayText("More Items?");

        // ================================================================
        // 9. Report Complete
        // ================================================================
        var reportComplete = new ReportCycleResultActivity
        {
            Id = "ReportTriageComplete",
            Name = "Report Triage Complete",
            Reason = new("triageComplete"),
        };
        reportComplete.SetDisplayText("Report Triage Complete");

        var finish = new Finish { Id = "Finish", Name = "Complete" };
        finish.SetDisplayText("Complete");

        // ================================================================
        // Flowchart
        // ================================================================
        builder.Root = new Flowchart
        {
            Id = "IssueTriageFlowchart",
            Start = fetchItems,
            Activities =
            {
                fetchItems, hasItems,
                extractItem, gatherContext, extractContext,
                panelReview, extractPanelResult,
                poDecision, extractDecision,
                applyLabels, incrementTriaged, incrementIndex, hasMoreItems,
                reportComplete, finish,
            },
            Connections =
            {
                // Fetch → Has Items?
                Connect(fetchItems, hasItems),

                // No items → report → finish
                ConnectOutcome(hasItems, "False", reportComplete),
                Connect(reportComplete, finish),

                // Has items → extract → gather context → panel → PO → apply → loop
                ConnectOutcome(hasItems, "True", extractItem),
                Connect(extractItem, gatherContext),
                Connect(gatherContext, extractContext),
                Connect(extractContext, panelReview),
                Connect(panelReview, extractPanelResult),
                Connect(extractPanelResult, poDecision),
                Connect(poDecision, extractDecision),
                Connect(extractDecision, applyLabels),
                Connect(applyLabels, incrementTriaged),
                Connect(incrementTriaged, incrementIndex),
                Connect(incrementIndex, hasMoreItems),

                // More items → loop back
                ConnectOutcome(hasMoreItems, "True", extractItem),

                // Done → report → finish
                ConnectOutcome(hasMoreItems, "False", reportComplete),
            }
        };
    }

    private static FlowConnection Connect(IActivity source, IActivity target)
        => new(new FlowEndpoint(source), new FlowEndpoint(target));

    private static FlowConnection ConnectOutcome(IActivity source, string outcome, IActivity target)
        => new(new FlowEndpoint(source, outcome), new FlowEndpoint(target));
}
