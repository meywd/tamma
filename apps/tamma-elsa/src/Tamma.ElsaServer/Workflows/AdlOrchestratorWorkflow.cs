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
        var lastExitReason = builder.WithVariable<string>("LastExitReason", "");
        var stopReason = builder.WithVariable<string>("StopReason", "");

        var cycleResult = builder.WithVariable<IDictionary<string, object>?>();

        // ================================================================
        // Activities
        // ================================================================

        // 1. Init config from inputs
        var initConfig = new SetVariable
        {
            Id = "InitAdlConfig",
            Name = "Load Config",
            Variable = repository,
            Value = new Input<object?>(ctx =>
            {
                var repo = ctx.GetInput<string>("repository") ?? "";
                var config = ctx.GetInput<string>("configJson");
                if (!string.IsNullOrEmpty(config))
                {
                    configJson.Set(ctx, config);
                    var parsed = SafeDeserialize<AdlConfig>(config);
                    if (parsed != null)
                    {
                        repo = string.IsNullOrEmpty(repo) ? parsed.Repository : repo;
                        issueLabels.Set(ctx, parsed.IssueLabels);
                        botAssignee.Set(ctx, parsed.BotAssignee);
                        baseBranch.Set(ctx, parsed.BaseBranch);
                        cooldownSeconds.Set(ctx, parsed.CooldownSeconds);
                    }
                }

                var directLabels = ctx.GetInput<string[]>("issueLabels");
                if (directLabels != null) issueLabels.Set(ctx, directLabels);
                var directBot = ctx.GetInput<string>("botAssignee");
                if (!string.IsNullOrEmpty(directBot)) botAssignee.Set(ctx, directBot);
                var directBase = ctx.GetInput<string>("baseBranch");
                if (!string.IsNullOrEmpty(directBase)) baseBranch.Set(ctx, directBase);

                return (object)repo;
            })
        };
        initConfig.SetDisplayText("Load Config");

        // 2. Check operational limits
        var checkLimits = new CheckLimitsActivity
        {
            Id = "CheckLimits",
            Name = "Check Limits",
            IssuesCompleted = new Input<int>(ctx => issuesCompleted.Get(ctx)),
            ConfigJson = new Input<string?>(ctx => configJson.Get(ctx)),
            StopReason = new Output<string?>(stopReason)
        };
        checkLimits.SetDisplayText("Check Limits");

        // 3. Guard: limits OK?
        var limitsOk = new FlowDecision(ctx => string.IsNullOrEmpty(stopReason.Get(ctx)))
        {
            Id = "LimitsOk",
            Name = "Within Limits?"
        };
        limitsOk.SetDisplayText("Within Limits?");

        // 4. Dispatch single-issue-cycle
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

        // 5. Parse cycle result
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
                    issuesCompleted.Set(ctx, issuesCompleted.Get(ctx) + 1);

                return (object)reason;
            })
        };
        parseResult.SetDisplayText("Parse Result");

        // 6. Guard: should continue looping?
        var shouldContinue = new FlowDecision(ctx =>
        {
            var reason = lastExitReason.Get(ctx);
            return reason != "noIssues";
        })
        {
            Id = "ShouldContinue",
            Name = "More Issues?"
        };
        shouldContinue.SetDisplayText("More Issues?");

        // 7. Cooldown delay
        var cooldown = new Delay
        {
            Id = "CooldownDelay",
            Name = "Cooldown",
            TimeSpan = new Input<TimeSpan>(ctx =>
                System.TimeSpan.FromSeconds(cooldownSeconds.Get(ctx)))
        };
        cooldown.SetDisplayText("Cooldown");

        // 8. Set final outputs (limits reached path)
        var setOutputsLimits = new Sequence
        {
            Id = "SetOutputsLimits",
            Name = "Output (Limits)",
            Activities =
            {
                WithLabel(new SetOutput { Id = "SetOutputLimitsTotal", Name = "Set Total Completed (Limits)", OutputName = new("totalIssuesCompleted"), OutputValue = new(ctx => (object)issuesCompleted.Get(ctx)) }, "Set Total Completed (Limits)"),
                WithLabel(new SetOutput { Id = "SetOutputLimitsReason", Name = "Set Exit Reason (Limits)", OutputName = new("exitReason"), OutputValue = new(ctx => (object)(stopReason.Get(ctx) ?? "limitsReached")) }, "Set Exit Reason (Limits)")
            }
        };
        setOutputsLimits.SetDisplayText("Output (Limits)");

        // 9. Set final outputs (no issues path)
        var setOutputsNoIssues = new Sequence
        {
            Id = "SetOutputsNoIssues",
            Name = "Output (No Issues)",
            Activities =
            {
                WithLabel(new SetOutput { Id = "SetOutputNoIssuesTotal", Name = "Set Total Completed (No Issues)", OutputName = new("totalIssuesCompleted"), OutputValue = new(ctx => (object)issuesCompleted.Get(ctx)) }, "Set Total Completed (No Issues)"),
                WithLabel(new SetOutput { Id = "SetOutputNoIssuesReason", Name = "Set Exit Reason (No Issues)", OutputName = new("exitReason"), OutputValue = new(ctx => (object)"noIssues") }, "Set Exit Reason (No Issues)")
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
                initConfig, checkLimits, limitsOk,
                dispatchCycle, parseResult, shouldContinue,
                cooldown,
                setOutputsLimits, setOutputsNoIssues, finish
            },
            Connections =
            {
                // Init → Check Limits
                Connect(initConfig, checkLimits),

                // Check Limits → Within Limits?
                Connect(checkLimits, limitsOk),

                // Within Limits? Yes → Dispatch Cycle
                ConnectOutcome(limitsOk, "True", dispatchCycle),

                // Within Limits? No → Output (Limits) → Finish
                ConnectOutcome(limitsOk, "False", setOutputsLimits),
                Connect(setOutputsLimits, finish),

                // Dispatch Cycle → Parse Result
                Connect(dispatchCycle, parseResult),

                // Parse Result → More Issues?
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

    private static T? SafeDeserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json); }
        catch { return null; }
    }
}
