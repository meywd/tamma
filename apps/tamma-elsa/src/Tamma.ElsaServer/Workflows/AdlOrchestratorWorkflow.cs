using Elsa.Extensions;
using Elsa.Scheduling.Activities;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime.Activities;
using Tamma.Activities.ADL;
using Tamma.Activities.ADL.Models;
using FlowEndpoint = Elsa.Workflows.Activities.Flowchart.Models.Endpoint;
using FlowConnection = Elsa.Workflows.Activities.Flowchart.Models.Connection;

using static Tamma.ElsaServer.Workflows.ActivityDisplayTextExtensions;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// ADL Orchestrator — the top-level loop that selects GitHub issues
/// and dispatches fire-and-forget single-issue-cycle workflows.
///
/// Flow:
///   Load Config → Select Issue → [Issue Found?]
///     No  → Finish (no issues)
///     Yes → Check Limits → [Within Limits?]
///       No  → Finish (limits reached)
///       Yes → Dispatch Cycle (fire & forget) → Cooldown → loop to Select Issue
/// </summary>
public class AdlOrchestratorWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "ADL Orchestrator";
        builder.DefinitionId = "adl-orchestrator";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Selects issues and dispatches autonomous development cycles";

        // ================================================================
        // Variables
        // ================================================================
        var configJson = builder.WithVariable<string>("ConfigJson", "{}");
        var repository = builder.WithVariable<string>("Repository", "");
        var issueLabels = builder.WithVariable<string[]>("IssueLabels", Array.Empty<string>());
        var botAssignee = builder.WithVariable<string>("BotAssignee", "tamma-bot");
        var baseBranch = builder.WithVariable<string>("BaseBranch", "main");
        var cooldownSeconds = builder.WithVariable<int>("CooldownSeconds", 10);
        var maxConcurrent = builder.WithVariable<int>("MaxConcurrent", 1);

        // Selected work item data
        var selectedItemJson = builder.WithVariable<string?>("SelectedItemJson", null);
        var selectedIssueNumber = builder.WithVariable<int>("SelectedIssueNumber", 0);

        // ================================================================
        // 1. Load Config
        // ================================================================
        var initConfig = new InitAdlConfigActivity
        {
            Id = "InitAdlConfig",
            Name = "Load Config",
            Repository = new Input<string?>(ctx => ctx.GetInput<string>("repository")),
            ConfigJson = new Input<string?>(ctx => ctx.GetInput<string>("configJson")),
            IssueLabels = new Input<string[]?>(ctx => ctx.GetInput<string[]>("issueLabels")),
            BotAssignee = new Input<string?>(ctx => ctx.GetInput<string>("botAssignee")),
            BaseBranch = new Input<string?>(ctx => ctx.GetInput<string>("baseBranch")),
            ResolvedRepository = new Output<string>(repository),
            ResolvedIssueLabels = new Output<string[]>(issueLabels),
            ResolvedBotAssignee = new Output<string>(botAssignee),
            ResolvedBaseBranch = new Output<string>(baseBranch),
            ResolvedCooldownSeconds = new Output<int>(cooldownSeconds),
            ResolvedMaxIssuesPerRun = new Output<int>(maxConcurrent),
            ResolvedConfigJson = new Output<string>(configJson),
        };
        initConfig.SetDisplayText("Load Config");

        // ================================================================
        // 2. Select Work Item (priority-based, multiple sources)
        // ================================================================
        var selectWorkItem = new SelectWorkItemActivity
        {
            Id = "SelectWorkItem",
            Name = "Select Work Item",
            Repository = new Input<string>(ctx => repository.Get(ctx)),
            AutoLabels = new Input<string[]>(ctx => issueLabels.Get(ctx)),
            BotAssignee = new Input<string>(ctx => botAssignee.Get(ctx)),
            WorkItemJson = new Output<string?>(selectedItemJson),
            IssueNumber = new Output<int>(selectedIssueNumber),
        };
        selectWorkItem.SetDisplayText("Select Work Item");

        // ================================================================
        // 2b. Dispatch Triage (when untriaged issues found but nothing ready)
        // ================================================================
        var dispatchTriage = new DispatchWorkflow
        {
            Id = "DispatchTriage",
            Name = "Dispatch Triage",
            WorkflowDefinitionId = new("issue-triage"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["repository"] = repository.Get(ctx),
            }),
            WaitForCompletion = new(true), // wait for triage to finish, then re-select
        };
        dispatchTriage.SetDisplayText("Dispatch Triage");

        // ================================================================
        // 4. Check Limits
        // ================================================================
        var checkLimits = new CheckLimitsActivity
        {
            Id = "CheckLimits",
            Name = "Check Limits",
            MaxConcurrent = new Input<int>(ctx => maxConcurrent.Get(ctx)),
        };
        checkLimits.SetDisplayText("Check Limits");

        // ================================================================
        // 5. Dispatch Cycle (fire & forget)
        // ================================================================
        var dispatchCycle = new DispatchWorkflow
        {
            Id = "DispatchIssueCycle",
            Name = "Dispatch Issue Cycle",
            WorkflowDefinitionId = new("single-issue-cycle"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["repository"] = repository.Get(ctx),
                ["workItemJson"] = selectedItemJson.Get(ctx) ?? "",
                ["issueNumber"] = selectedIssueNumber.Get(ctx),
                ["botAssignee"] = botAssignee.Get(ctx),
                ["baseBranch"] = baseBranch.Get(ctx),
            }),
            WaitForCompletion = new(false), // fire & forget
        };
        dispatchCycle.SetDisplayText("Dispatch Issue Cycle");

        // ================================================================
        // 7. Cooldown
        // ================================================================
        var cooldown = new Delay
        {
            Id = "CooldownDelay",
            Name = "Cooldown",
            TimeSpan = new Input<TimeSpan>(ctx =>
                System.TimeSpan.FromSeconds(cooldownSeconds.Get(ctx)))
        };
        cooldown.SetDisplayText("Cooldown");

        // ================================================================
        // Output & Finish
        // ================================================================
        var setOutputsDone = new SetOutput
        {
            Id = "SetOutputsDone",
            Name = "Output (No Issues)",
            OutputName = new("exitReason"),
            OutputValue = new(ctx => (object)"noIssues"),
        };
        setOutputsDone.SetDisplayText("Output (No Issues)");

        var setOutputsLimits = new SetOutput
        {
            Id = "SetOutputsLimits",
            Name = "Output (Limits)",
            OutputName = new("exitReason"),
            OutputValue = new(ctx => (object)"limitsReached"),
        };
        setOutputsLimits.SetDisplayText("Output (Limits)");

        var finish = new Finish { Id = "Finish", Name = "Complete" };
        finish.SetDisplayText("Complete");

        // ================================================================
        // Flowchart
        // ================================================================
        builder.Root = new Flowchart
        {
            Id = "AdlOrchestratorFlowchart",
            Start = initConfig,
            Activities =
            {
                initConfig, selectWorkItem, dispatchTriage,
                checkLimits, dispatchCycle, cooldown,
                setOutputsDone, setOutputsLimits, finish
            },
            Connections =
            {
                // Load Config → Select Work Item
                Connect(initConfig, selectWorkItem),

                // Nothing found → repo is clean → Finish
                ConnectOutcome(selectWorkItem, "NothingFound", setOutputsDone),
                Connect(setOutputsDone, finish),

                // Needs triage → dispatch triage → re-select
                ConnectOutcome(selectWorkItem, "NeedsTriage", dispatchTriage),
                Connect(dispatchTriage, selectWorkItem), // loop back after triage

                // Selected → Check Limits
                ConnectOutcome(selectWorkItem, "Selected", checkLimits),

                // Limits reached → Finish
                ConnectOutcome(checkLimits, "Stop", setOutputsLimits),
                Connect(setOutputsLimits, finish),

                // Within limits → Dispatch (fire & forget) → Cooldown → Loop
                ConnectOutcome(checkLimits, "Continue", dispatchCycle),
                Connect(dispatchCycle, cooldown),
                Connect(cooldown, selectWorkItem), // loop back to select next
            }
        };
    }

    private static FlowConnection Connect(IActivity source, IActivity target)
        => new(new FlowEndpoint(source), new FlowEndpoint(target));

    private static FlowConnection ConnectOutcome(IActivity source, string outcome, IActivity target)
        => new(new FlowEndpoint(source, outcome), new FlowEndpoint(target));
}
