using System.Text.Json;
using Elsa.Extensions;
using Elsa.Scheduling.Activities;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Contracts;
using Elsa.Workflows.Management.Activities.SetOutput;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime.Activities;
using Tamma.Activities.ADL;
using Tamma.Activities.ADL.Models;

namespace Tamma.ElsaServer.Workflows;

/// <summary>
/// ADL Orchestrator Workflow — the top-level loop that continuously picks
/// GitHub issues and dispatches single-issue-cycle workflows for each.
///
/// Design: Sequence with While loop.
///
/// Flow:
///   1. Load config from input
///   2. While (continueLoop):
///      a. CheckLimitsActivity → if Stop: break
///      b. DispatchWorkflow("single-issue-cycle")
///      c. Parse result:
///         - success → increment issuesCompleted
///         - noIssues → break
///         - error/rejected → log, continue
///      d. Delay(cooldown)
///   3. SetOutput: totalIssuesCompleted, exitReason
/// </summary>
public class AdlOrchestratorWorkflow : WorkflowBase
{
    protected override void Build(IWorkflowBuilder builder)
    {
        builder.Name = "ADL Orchestrator";
        builder.DefinitionId = "adl-orchestrator";
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

        var continueLoop = builder.WithVariable<bool>("ContinueLoop", true);
        var issuesCompleted = builder.WithVariable<int>("IssuesCompleted", 0);
        var lastExitReason = builder.WithVariable<string>("LastExitReason", "");
        var stopReason = builder.WithVariable<string>("StopReason", "");

        var cycleResult = builder.WithVariable<IDictionary<string, object>?>();

        // ================================================================
        // Workflow body
        // ================================================================
        builder.Root = new Sequence
        {
            Activities =
            {
                // Step 1: Initialize from input
                new SetVariable
                {
                    Id = "InitAdlConfig",
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

                        // Allow direct input overrides
                        var directLabels = ctx.GetInput<string[]>("issueLabels");
                        if (directLabels != null) issueLabels.Set(ctx, directLabels);
                        var directBot = ctx.GetInput<string>("botAssignee");
                        if (!string.IsNullOrEmpty(directBot)) botAssignee.Set(ctx, directBot);
                        var directBase = ctx.GetInput<string>("baseBranch");
                        if (!string.IsNullOrEmpty(directBase)) baseBranch.Set(ctx, directBase);

                        return (object)repo;
                    })
                },

                // Step 2: Main loop
                new While(ctx => continueLoop.Get(ctx))
                {
                    Id = "AdlMainLoop",
                    Body = new Sequence
                    {
                        Activities =
                        {
                            // 2a. Check limits
                            new CheckLimitsActivity
                            {
                                Id = "CheckLimits",
                                IssuesCompleted = new Input<int>(ctx => issuesCompleted.Get(ctx)),
                                ConfigJson = new Input<string?>(ctx => configJson.Get(ctx)),
                                StopReason = new Output<string?>(stopReason)
                            },

                            // If limits say stop, break
                            new If
                            {
                                Id = "CheckLimitsResult",
                                Condition = new(ctx => !string.IsNullOrEmpty(stopReason.Get(ctx))),
                                Then = new SetVariable
                                {
                                    Id = "SetStopLoop",
                                    Variable = continueLoop,
                                    Value = new Input<object?>(_ => (object)false)
                                },
                                Else = new Sequence
                                {
                                    Activities =
                                    {
                                        // 2b. Dispatch single-issue-cycle
                                        new DispatchWorkflow
                                        {
                                            Id = "DispatchIssueCycle",
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
                                        },

                                        // 2c. Parse result
                                        new SetVariable
                                        {
                                            Id = "ParseCycleResult",
                                            Variable = lastExitReason,
                                            Value = new Input<object?>(ctx =>
                                            {
                                                var result = cycleResult.Get(ctx);
                                                var reason = "unknown";
                                                if (result != null && result.TryGetValue("exitReason", out var er))
                                                    reason = er?.ToString() ?? "unknown";

                                                lastExitReason.Set(ctx, reason);

                                                switch (reason)
                                                {
                                                    case "success":
                                                        issuesCompleted.Set(ctx, issuesCompleted.Get(ctx) + 1);
                                                        break;
                                                    case "noIssues":
                                                        continueLoop.Set(ctx, false);
                                                        break;
                                                    // error, rejected, tddFailed, ciFailed, mergeFailed:
                                                    // log and continue to next issue
                                                }

                                                return (object)reason;
                                            })
                                        },

                                        // 2d. Cooldown delay
                                        new Delay
                                        {
                                            Id = "CooldownDelay",
                                            TimeSpan = new Input<TimeSpan>(ctx =>
                                                TimeSpan.FromSeconds(cooldownSeconds.Get(ctx)))
                                        }
                                    }
                                }
                            }
                        }
                    }
                },

                // Step 3: Set final outputs
                new SetOutput
                {
                    OutputName = new("totalIssuesCompleted"),
                    OutputValue = new(ctx => (object)issuesCompleted.Get(ctx))
                },
                new SetOutput
                {
                    OutputName = new("exitReason"),
                    OutputValue = new(ctx =>
                    {
                        var sr = stopReason.Get(ctx);
                        if (!string.IsNullOrEmpty(sr)) return (object)sr;
                        var lr = lastExitReason.Get(ctx);
                        return (object)(lr ?? "completed");
                    })
                }
            }
        };
    }

    private static T? SafeDeserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json); }
        catch { return null; }
    }
}
