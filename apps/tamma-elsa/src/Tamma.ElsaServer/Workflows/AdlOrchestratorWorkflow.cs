using System.Text.Json;
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
/// ADL Orchestrator Workflow — the top-level loop that continuously picks
/// GitHub issues and dispatches single-issue-cycle workflows for each.
///
/// Design: Flowchart with loop-back connections for visual clarity in Studio.
///
/// Flow:
///   InitConfig → CheckLimits → [Continue?]
///     Yes → DispatchCycle → ParseResult → [noIssues?]
///       No  → Cooldown → loop back to CheckLimits
///       Yes → SetOutputs → Finish
///     No  → SetOutputs → Finish
/// </summary>
public class AdlOrchestratorWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "ADL Orchestrator";
        builder.DefinitionId = "adl-orchestrator";
        builder.Version = WorkflowVersions.ComputedVersion;
        builder.Description = "Top-level loop that picks issues and dispatches autonomous development cycles";

        // ================================================================
        // Variables
        // ================================================================
        var configJson = builder.WithVariable<string>("ConfigJson", "{}");
        var repository = builder.WithVariable<string>("Repository", "");
        var issueLabels = builder.WithVariable<string[]>("IssueLabels", Array.Empty<string>());
        var botAssignee = builder.WithVariable<string>("BotAssignee", "tamma-bot");
        var baseBranch = builder.WithVariable<string>("BaseBranch", "main");
        var cooldownSeconds = builder.WithVariable<int>("CooldownSeconds", 10);

        var issuesCompleted = builder.WithVariable<int>("IssuesCompleted", 0);
        var consecutiveFailures = builder.WithVariable<int>("ConsecutiveFailures", 0);
        var lastExitReason = builder.WithVariable<string>("LastExitReason", "");
        var stopReason = builder.WithVariable<string>("StopReason", "");
        var maxPerRun = builder.WithVariable<int>("MaxPerRun", 10);

        var cycleResult = builder.WithVariable<IDictionary<string, object>?>();

        // ================================================================
        // Activities
        // ================================================================

        // 1. Init config from inputs
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

        // 2. Check limits — uses outcomes directly (no separate FlowDecision)
        var checkLimits = new CheckLimitsActivity
        {
            Id = "CheckLimits",
            Name = "Check Limits",
            IssuesCompleted = new Input<int>(ctx => issuesCompleted.Get(ctx)),
            ConsecutiveFailures = new Input<int>(ctx => consecutiveFailures.Get(ctx)),
            DailyQuota = new Input<int>(20),
            MaxPerRun = new Input<int>(ctx => maxPerRun.Get(ctx)),
            MaxConsecutiveFailures = new Input<int>(3),
            StopReason = new Output<string?>(stopReason)
        };
        checkLimits.SetDisplayText("Check Limits");

        // 3. Dispatch single-issue-cycle
        var dispatchCycle = new DispatchWorkflow
        {
            Id = "DispatchIssueCycle",
            Name = "Dispatch Issue Cycle",
            WorkflowDefinitionId = new("single-issue-cycle"),
            Input = new(ctx => new Dictionary<string, object>
            {
                ["repository"] = repository.Get(ctx),
                ["issueLabels"] = issueLabels.Get(ctx),
                ["botAssignee"] = botAssignee.Get(ctx),
                ["baseBranch"] = baseBranch.Get(ctx)
            }),
            WaitForCompletion = new(true),
            Result = new(cycleResult)
        };
        dispatchCycle.SetDisplayText("Dispatch Issue Cycle");

        // 4. Parse cycle result — track success/failure counts
        var parseResult = new SetVariable
        {
            Id = "ParseCycleResult",
            Name = "Parse Result",
            Variable = lastExitReason,
            Value = new Input<object?>(ctx =>
            {
                var result = cycleResult.Get(ctx);
                var reason = "unknown";
                if (result != null && result.TryGetValue("exitReason", out var er))
                    reason = er?.ToString() ?? "unknown";

                if (reason == "success")
                {
                    issuesCompleted.Set(ctx, issuesCompleted.Get(ctx) + 1);
                    consecutiveFailures.Set(ctx, 0); // reset on success
                }
                else if (reason != "noIssues")
                {
                    consecutiveFailures.Set(ctx, consecutiveFailures.Get(ctx) + 1);
                }

                return (object)reason;
            })
        };
        parseResult.SetDisplayText("Parse Result");

        // 5. Should continue? (only stop on "noIssues" — failures are caught by CheckLimits)
        var shouldContinue = new FlowDecision(ctx => lastExitReason.Get(ctx) != "noIssues")
        {
            Id = "ShouldContinue",
            Name = "More Issues?"
        };
        shouldContinue.SetDisplayText("More Issues?");

        // 6. Cooldown — adaptive: longer after failures
        var cooldown = new Delay
        {
            Id = "CooldownDelay",
            Name = "Cooldown",
            TimeSpan = new Input<TimeSpan>(ctx =>
            {
                var baseSeconds = cooldownSeconds.Get(ctx);
                var failures = consecutiveFailures.Get(ctx);
                // Exponential backoff: 10s, 20s, 40s, 80s... capped at 5 min
                var multiplier = failures > 0 ? Math.Pow(2, Math.Min(failures, 5)) : 1;
                return System.TimeSpan.FromSeconds(baseSeconds * multiplier);
            })
        };
        cooldown.SetDisplayText("Cooldown");

        // 7. Set final outputs (limits reached path)
        var setOutputsLimits = new Sequence
        {
            Id = "SetOutputsLimits",
            Name = "Output (Limits)",
            Activities =
            {
                WithLabel(new SetOutput { Id = "SetOutputLimitsTotal", Name = "Set Total", OutputName = new("totalIssuesCompleted"), OutputValue = new(ctx => (object)issuesCompleted.Get(ctx)) }, "Set Total"),
                WithLabel(new SetOutput { Id = "SetOutputLimitsReason", Name = "Set Reason", OutputName = new("exitReason"), OutputValue = new(ctx => (object)(stopReason.Get(ctx) ?? "limitsReached")) }, "Set Reason"),
                WithLabel(new SetOutput { Id = "SetOutputLimitsFailures", Name = "Set Failures", OutputName = new("consecutiveFailures"), OutputValue = new(ctx => (object)consecutiveFailures.Get(ctx)) }, "Set Failures")
            }
        };
        setOutputsLimits.SetDisplayText("Output (Limits)");

        // 8. Set final outputs (no issues path)
        var setOutputsNoIssues = new Sequence
        {
            Id = "SetOutputsNoIssues",
            Name = "Output (No Issues)",
            Activities =
            {
                WithLabel(new SetOutput { Id = "SetOutputNoIssuesTotal", Name = "Set Total", OutputName = new("totalIssuesCompleted"), OutputValue = new(ctx => (object)issuesCompleted.Get(ctx)) }, "Set Total"),
                WithLabel(new SetOutput { Id = "SetOutputNoIssuesReason", Name = "Set Reason", OutputName = new("exitReason"), OutputValue = new(ctx => (object)"noIssues") }, "Set Reason")
            }
        };
        setOutputsNoIssues.SetDisplayText("Output (No Issues)");

        var finish = new Finish { Id = "Finish", Name = "Complete: Orchestrator Done" };
        finish.SetDisplayText("Complete: Orchestrator Done");

        // ================================================================
        // Flowchart
        // ================================================================
        builder.Root = new Flowchart
        {
            Id = "AdlOrchestratorFlowchart",
            Start = initConfig,
            Activities =
            {
                initConfig, checkLimits,
                dispatchCycle, parseResult, shouldContinue,
                cooldown,
                setOutputsLimits, setOutputsNoIssues, finish
            },
            Connections =
            {
                // Init → Check Limits
                Connect(initConfig, checkLimits),

                // Check Limits → Continue → Dispatch Cycle
                ConnectOutcome(checkLimits, "Continue", dispatchCycle),

                // Check Limits → Stop → Output (Limits) → Finish
                ConnectOutcome(checkLimits, "Stop", setOutputsLimits),
                Connect(setOutputsLimits, finish),

                // Dispatch Cycle → Parse Result → More Issues?
                Connect(dispatchCycle, parseResult),
                Connect(parseResult, shouldContinue),

                // More Issues? Yes → Cooldown → loop back to Check Limits
                ConnectOutcome(shouldContinue, "True", cooldown),
                Connect(cooldown, checkLimits),

                // More Issues? No → Output (No Issues) → Finish
                ConnectOutcome(shouldContinue, "False", setOutputsNoIssues),
                Connect(setOutputsNoIssues, finish)
            }
        };
    }

    private static FlowConnection Connect(IActivity source, IActivity target)
        => new(new FlowEndpoint(source), new FlowEndpoint(target));

    private static FlowConnection ConnectOutcome(IActivity source, string outcome, IActivity target)
        => new(new FlowEndpoint(source, outcome), new FlowEndpoint(target));

}
