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
        var maxPerRun = builder.WithVariable<int>("MaxPerRun", 10);

        var issuesDispatched = builder.WithVariable<int>("IssuesDispatched", 0);
        var consecutiveEmpty = builder.WithVariable<int>("ConsecutiveEmpty", 0);

        // Selected issue data
        var selectedIssueJson = builder.WithVariable<string?>("SelectedIssueJson", null);
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
            ResolvedMaxIssuesPerRun = new Output<int>(maxPerRun),
            ResolvedConfigJson = new Output<string>(configJson),
        };
        initConfig.SetDisplayText("Load Config");

        // ================================================================
        // 2. Select Issue
        // ================================================================
        var selectIssue = new SelectIssueActivity
        {
            Id = "SelectIssue",
            Name = "Select Issue",
            Repository = new Input<string>(ctx => repository.Get(ctx)),
            IssueLabels = new Input<string[]>(ctx => issueLabels.Get(ctx)),
            BotAssignee = new Input<string>(ctx => botAssignee.Get(ctx)),
            IssueJson = new Output<string?>(selectedIssueJson),
            IssueNumber = new Output<int>(selectedIssueNumber),
        };
        selectIssue.SetDisplayText("Select Issue");

        // ================================================================
        // 3. Issue Found?
        // ================================================================
        var issueFound = new FlowDecision(ctx => !string.IsNullOrEmpty(selectedIssueJson.Get(ctx)))
        {
            Id = "IssueFound",
            Name = "Issue Found?"
        };
        issueFound.SetDisplayText("Issue Found?");

        // ================================================================
        // 4. Check Limits
        // ================================================================
        var checkLimits = new CheckLimitsActivity
        {
            Id = "CheckLimits",
            Name = "Check Limits",
            IssuesCompleted = new Input<int>(ctx => issuesDispatched.Get(ctx)),
            ConsecutiveFailures = new Input<int>(0), // failures tracked by engine callbacks
            DailyQuota = new Input<int>(20),
            MaxPerRun = new Input<int>(ctx => maxPerRun.Get(ctx)),
            MaxConsecutiveFailures = new Input<int>(100), // effectively disabled — engine handles this
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
                ["issueJson"] = selectedIssueJson.Get(ctx) ?? "",
                ["issueNumber"] = selectedIssueNumber.Get(ctx),
                ["botAssignee"] = botAssignee.Get(ctx),
                ["baseBranch"] = baseBranch.Get(ctx),
            }),
            WaitForCompletion = new(false), // fire & forget
        };
        dispatchCycle.SetDisplayText("Dispatch Issue Cycle");

        // ================================================================
        // 6. Increment dispatched count
        // ================================================================
        var incrementCount = new SetVariable
        {
            Id = "IncrementDispatched",
            Name = "Track Dispatch",
            Variable = issuesDispatched,
            Value = new Input<object?>(ctx =>
            {
                consecutiveEmpty.Set(ctx, 0); // reset empty counter on successful dispatch
                return (object)(issuesDispatched.Get(ctx) + 1);
            })
        };
        incrementCount.SetDisplayText("Track Dispatch");

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
        var setOutputsDone = new Sequence
        {
            Id = "SetOutputsDone",
            Name = "Output (No Issues)",
            Activities =
            {
                WithLabel(new SetOutput { Id = "OutTotal", OutputName = new("totalDispatched"), OutputValue = new(ctx => (object)issuesDispatched.Get(ctx)) }, "Set Total"),
                WithLabel(new SetOutput { Id = "OutReason", OutputName = new("exitReason"), OutputValue = new(ctx => (object)"noIssues") }, "Set Reason"),
            }
        };
        setOutputsDone.SetDisplayText("Output (No Issues)");

        var setOutputsLimits = new Sequence
        {
            Id = "SetOutputsLimits",
            Name = "Output (Limits)",
            Activities =
            {
                WithLabel(new SetOutput { Id = "OutLimTotal", OutputName = new("totalDispatched"), OutputValue = new(ctx => (object)issuesDispatched.Get(ctx)) }, "Set Total"),
                WithLabel(new SetOutput { Id = "OutLimReason", OutputName = new("exitReason"), OutputValue = new(ctx => (object)"limitsReached") }, "Set Reason"),
            }
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
                initConfig, selectIssue, issueFound,
                checkLimits, dispatchCycle, incrementCount, cooldown,
                setOutputsDone, setOutputsLimits, finish
            },
            Connections =
            {
                // Load Config → Select Issue
                Connect(initConfig, selectIssue),

                // Select Issue → Issue Found?
                Connect(selectIssue, issueFound),

                // No issue → Output (No Issues) → Finish
                ConnectOutcome(issueFound, "False", setOutputsDone),
                Connect(setOutputsDone, finish),

                // Issue found → Check Limits
                ConnectOutcome(issueFound, "True", checkLimits),

                // Limits reached → Output (Limits) → Finish
                ConnectOutcome(checkLimits, "Stop", setOutputsLimits),
                Connect(setOutputsLimits, finish),

                // Within limits → Dispatch (fire & forget) → Track → Cooldown → Loop
                ConnectOutcome(checkLimits, "Continue", dispatchCycle),
                Connect(dispatchCycle, incrementCount),
                Connect(incrementCount, cooldown),
                Connect(cooldown, selectIssue), // loop back
            }
        };
    }

    private static FlowConnection Connect(IActivity source, IActivity target)
        => new(new FlowEndpoint(source), new FlowEndpoint(target));

    private static FlowConnection ConnectOutcome(IActivity source, string outcome, IActivity target)
        => new(new FlowEndpoint(source, outcome), new FlowEndpoint(target));
}
