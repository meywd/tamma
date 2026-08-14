using Elsa.Extensions;
using Elsa.Scheduling.Activities;
using Elsa.Workflows;
using Elsa.Workflows.Activities;
using Elsa.Workflows.Activities.Flowchart.Activities;
using Elsa.Workflows.IncidentStrategies;
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

        // CORRECTNESS (loop durability) — continue-with-incidents, matching the
        // SingleIssueCycle / MergeApproval / TriageItemCycle / DeploymentPipeline /
        // ReviewFix / CleanUpFailedTenant precedent. This workflow needs it MORE than
        // any of them: the restart is the LAST step of the instance it restarts
        // (cooldown → DispatchAdl → Finish), nothing else re-dispatches
        // `adl-orchestrator` — there is no cron trigger and no watchdog — so under
        // Elsa's default fault strategy a single throwing activity anywhere upstream
        // faults the instance BEFORE its successor exists and the autonomous loop
        // stops PERMANENTLY until a human dispatches one by hand. Under
        // continue-with-incidents the tick records the incident and still reaches the
        // restart edge, so a transient failure costs one cycle, never the loop.
        builder.WorkflowOptions.IncidentStrategyType = typeof(ContinueWithIncidentsStrategy);

        // ================================================================
        // Variables
        // ================================================================
        var configJson = builder.WithVariable<string>("ConfigJson", "{}").Persisted();
        var repository = builder.WithVariable<string>("Repository", "").Persisted();
        var issueLabels = builder.WithVariable<string[]>("IssueLabels", Array.Empty<string>()).Persisted();
        var botAssignee = builder.WithVariable<string>("BotAssignee", "tamma-bot").Persisted();
        var baseBranch = builder.WithVariable<string>("BaseBranch", "main").Persisted();
        var cooldownSeconds = builder.WithVariable<int>("CooldownSeconds", 10).Persisted();
        var maxConcurrent = builder.WithVariable<int>("MaxConcurrent", 1).Persisted();

        // Deployment mode + tenant threaded to each dispatched cycle (and from
        // there into the deployment pipeline's production-approval gate). These
        // are PASS-THROUGH from the orchestrator's own input: at the engine/
        // orchestrator layer the process-wide operating mode is a CONFIG concern,
        // not a per-instance input, so the current self-restart loop carries them
        // empty and DispatchCycleActivity derives the real mode from configuration
        // (mirrors TammaModeProvider). A SaaS dispatcher / operator may still set
        // `mode`/`tenantId` on the orchestrator input to override per-instance.
        var mode = builder.WithVariable<string>("Mode", "").Persisted();
        var tenantId = builder.WithVariable<string>("TenantId", "").Persisted();

        // Selected work item data
        var selectedItemJson = builder.WithVariable<string?>("SelectedItemJson", null).Persisted();
        var selectedIssueNumber = builder.WithVariable<int>("SelectedIssueNumber", 0).Persisted();

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
        // 2b. Dispatch Triage
        // ================================================================
        var dispatchTriage = new DispatchTriageActivity
        {
            Id = "DispatchTriage",
            Name = "Dispatch Triage",
            Repository = new Input<string>(ctx => repository.Get(ctx)),
        };
        dispatchTriage.SetDisplayText("Dispatch Triage");

        // ================================================================
        // 3. Check Limits
        // ================================================================
        var checkLimits = new CheckLimitsActivity
        {
            Id = "CheckLimits",
            Name = "Check Limits",
            MaxConcurrent = new Input<int>(ctx => maxConcurrent.Get(ctx)),
        };
        checkLimits.SetDisplayText("Check Limits");

        // ================================================================
        // 4. Dispatch Cycle (fire & forget)
        // ================================================================
        var dispatchCycle = new DispatchCycleActivity
        {
            Id = "DispatchIssueCycle",
            Name = "Dispatch Issue Cycle",
            Repository = new Input<string>(ctx => repository.Get(ctx)),
            WorkItemJson = new Input<string>(ctx => selectedItemJson.Get(ctx) ?? ""),
            IssueNumber = new Input<int>(ctx => selectedIssueNumber.Get(ctx)),
            BotAssignee = new Input<string>(ctx => botAssignee.Get(ctx)),
            BaseBranch = new Input<string>(ctx => baseBranch.Get(ctx)),
            // Thread the operating mode + tenant end-to-end so the deployment
            // pipeline's production-approval gate engages for business/SaaS.
            // Pass-through from the orchestrator input (empty in the self-restart
            // loop); DispatchCycleActivity derives the real mode from config when
            // empty, fail-safe to "business" (gate ON) — never a silent prod
            // auto-deploy.
            Mode = new Input<string>(ctx => ctx.GetInput<string>("mode") ?? mode.Get(ctx)),
            TenantId = new Input<string>(ctx => ctx.GetInput<string>("tenantId") ?? tenantId.Get(ctx)),
        };
        dispatchCycle.SetDisplayText("Dispatch Issue Cycle");

        // ================================================================
        // 5. Cooldown
        // ================================================================
        var cooldown = new CooldownActivity
        {
            Id = "CooldownDelay",
            Name = "Cooldown",
            Seconds = new Input<int>(ctx => cooldownSeconds.Get(ctx)),
        };
        cooldown.SetDisplayText("Cooldown");

        // 2026-08-13 (engine-driven E2E): the WAIT is a stock scheduling Delay
        // (timer bookmark ⇒ the instance SUSPENDS and frees the dispatch
        // worker). CooldownActivity used to Task.Delay in-process, which held
        // the runtime's dispatch slot for the whole cooldown — with a real
        // 3600s cooldown, every subsequently dispatched workflow (all the
        // cycle's llm-calls included) queued behind the sleeping orchestrator
        // and the loop deadlocked itself. CooldownActivity now only emits the
        // ADL.COOLDOWN audit pair; this node does the waiting.
        var cooldownWait = new Elsa.Scheduling.Activities.Delay(
            ctx => TimeSpan.FromSeconds(Math.Max(1, cooldownSeconds.Get(ctx))))
        {
            Id = "CooldownWait",
            Name = "Cooldown Wait",
        };
        cooldownWait.SetDisplayText("Cooldown Wait");

        // ================================================================
        // Exit paths
        // ================================================================
        var exitNoIssues = new SetExitReasonActivity
        {
            Id = "ExitNoIssues",
            Name = "Exit (No Issues)",
            Reason = new Input<string>("noIssues"),
        };
        exitNoIssues.SetDisplayText("Exit (No Issues)");

        var exitLimits = new SetExitReasonActivity
        {
            Id = "ExitLimits",
            Name = "Exit (Limits)",
            Reason = new Input<string>("limitsReached"),
        };
        exitLimits.SetDisplayText("Exit (Limits)");

        // ================================================================
        // Restart: Dispatch new ADL instance after cooldown
        // ================================================================
        var dispatchAdl = new DispatchAdlActivity
        {
            Id = "DispatchAdl",
            Name = "Dispatch ADL",
            ConfigJson = new Input<string>(ctx => configJson.Get(ctx)),
        };
        dispatchAdl.SetDisplayText("Dispatch ADL");

        var finish = new Finish { Id = "Finish", Name = "Complete" };
        finish.SetDisplayText("Complete");

        // ================================================================
        // Flowchart — every path → cooldown → dispatch ADL → finish
        // ================================================================
        builder.Root = new Flowchart
        {
            Id = "AdlOrchestratorFlowchart",
            Start = initConfig,
            Activities =
            {
                initConfig, selectWorkItem, dispatchTriage,
                checkLimits, dispatchCycle, cooldown, cooldownWait,
                exitNoIssues, exitLimits, dispatchAdl, finish
            },
            Connections =
            {
                // Load Config → Select Work Item
                Connect(initConfig, selectWorkItem),

                // Nothing found → report → cooldown
                ConnectOutcome(selectWorkItem, "NothingFound", exitNoIssues),
                Connect(exitNoIssues, cooldown),

                // Needs triage → dispatch triage (f&f) → cooldown
                ConnectOutcome(selectWorkItem, "NeedsTriage", dispatchTriage),
                Connect(dispatchTriage, cooldown),

                // Selected → Check Limits
                ConnectOutcome(selectWorkItem, "Selected", checkLimits),

                // Limits reached → report → cooldown
                ConnectOutcome(checkLimits, "Stop", exitLimits),
                Connect(exitLimits, cooldown),

                // Within limits → Dispatch cycle (f&f) → cooldown
                ConnectOutcome(checkLimits, "Continue", dispatchCycle),
                Connect(dispatchCycle, cooldown),

                // All paths: cooldown (emit) → cooldown wait (timer bookmark)
                // → dispatch new ADL → finish this instance
                Connect(cooldown, cooldownWait),
                Connect(cooldownWait, dispatchAdl),
                Connect(dispatchAdl, finish),
            }
        };
    }

    private static FlowConnection Connect(IActivity source, IActivity target)
        => new(new FlowEndpoint(source), new FlowEndpoint(target));

    private static FlowConnection ConnectOutcome(IActivity source, string outcome, IActivity target)
        => new(new FlowEndpoint(source, outcome), new FlowEndpoint(target));
}
