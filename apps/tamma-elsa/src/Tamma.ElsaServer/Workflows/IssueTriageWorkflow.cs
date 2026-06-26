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
/// Issue Triage — fetches untriaged items and dispatches a
/// triage-item-cycle for each one (fire & forget).
///
/// The per-item processing (context → panel → PO → labels) is handled by
/// TriageItemCycleWorkflow. Each dispatch is independent (NOT a singleton — a
/// SingletonStrategy would REJECT, not queue, concurrent dispatches and silently drop
/// items from this per-item fan-out; see the cycle's header). tenantId is threaded from
/// this workflow's input into each dispatch so the cycle's TRIAGE.ISSUE.* events are
/// tenant-scoped.
///
/// Flow:
///   Fetch Untriaged Items → Has Items? → Loop:
///     Extract Item → Dispatch Triage Item Cycle (fire & forget)
///     → Next Item → More Items? → loop / Report Complete → Finish
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
        builder.Description = "Fetch untriaged items and dispatch singleton triage cycles";

        // ================================================================
        // Variables
        // ================================================================
        var repository = builder.WithVariable<string>("Repository", "");
        // Tenant id threaded from this workflow's input through to each cycle dispatch so
        // a SaaS caller's per-item TRIAGE.ISSUE.* events carry the tenant tag (the cycle
        // forwards it onward to its context/panel/PO sub-workflows). Empty in single-user
        // mode → platform-scope events (honest default — no fabricated tenant).
        var tenantId = builder.WithVariable<string>("TenantId", "");
        var itemsJson = builder.WithVariable<string>("ItemsJson", "[]");
        var currentItemIndex = builder.WithVariable<int>("CurrentItemIndex", 0);
        var totalItems = builder.WithVariable<int>("TotalItems", 0);
        var currentItemJson = builder.WithVariable<string>("CurrentItemJson", "");

        // ================================================================
        // 1. Fetch Untriaged Items
        // ================================================================
        var fetchItems = new FetchUntriagedItemsActivity
        {
            Id = "FetchItems",
            Name = "Fetch Untriaged Items",
            Repository = new Input<string>(ctx =>
            {
                var repo = ctx.GetInput<string>("repository") ?? "";
                repository.Set(ctx, repo);
                // Capture tenantId from this workflow's input so each cycle dispatch can
                // forward it (tenant-scoped TRIAGE.ISSUE.* events).
                tenantId.Set(ctx, ctx.GetInput<string>("tenantId") ?? "");
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
        // 4. Dispatch Triage Item Cycle (fire & forget)
        // ================================================================
        var dispatchCycle = new DispatchWorkflow
        {
            Id = "DispatchTriageCycle",
            Name = "Dispatch Triage Cycle",
            WorkflowDefinitionId = new("triage-item-cycle"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["repository"] = repository.Get(ctx),
                ["itemJson"] = currentItemJson.Get(ctx),
                // Thread tenantId so the cycle's TRIAGE.ISSUE.* events (and its
                // context/panel/PO sub-workflows) are tenant-scoped.
                ["tenantId"] = tenantId.Get(ctx),
            }),
            // Fire & forget: each cycle runs independently (NOT a singleton — the child's
            // header explains why a SingletonStrategy would silently DROP fan-out items).
            WaitForCompletion = new(false),
        };
        dispatchCycle.SetDisplayText("Dispatch Triage Cycle");

        // ================================================================
        // 5. Next Item
        // ================================================================
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
        // 6. Report Complete
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
                extractItem, dispatchCycle,
                incrementIndex, hasMoreItems,
                reportComplete, finish,
            },
            Connections =
            {
                // Fetch → Has Items?
                Connect(fetchItems, hasItems),

                // No items → report → finish
                ConnectOutcome(hasItems, "False", reportComplete),
                Connect(reportComplete, finish),

                // Has items → extract → dispatch (f&f) → next → loop
                ConnectOutcome(hasItems, "True", extractItem),
                Connect(extractItem, dispatchCycle),
                Connect(dispatchCycle, incrementIndex),
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
