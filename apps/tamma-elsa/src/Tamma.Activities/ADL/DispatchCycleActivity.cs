using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Elsa.Workflows.Runtime;
using Elsa.Workflows.Runtime.Contracts;
using Elsa.Workflows.Runtime.Requests;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Activities.Core;
using Tamma.Core.Deployment;

namespace Tamma.Activities.ADL;

/// <summary>
/// Dispatches a SingleIssueCycle workflow (fire & forget) with event emission.
/// Wraps Elsa's DispatchWorkflow to add audit trail.
///
/// <para><b>Deployment-mode threading (IMPORTANT fix, 2026-06-22).</b> The
/// downstream <c>deployment-pipeline</c> gates its production deploy on a human
/// approval bookmark when <c>mode == "business"</c> (or an explicit
/// <c>requireProdApproval</c>). Nothing upstream used to set <c>mode</c>, so prod
/// auto-deployed with no gate (violating "zero deployments without approval in
/// Business Mode"). This activity now derives the real operating mode from
/// configuration — mirroring <c>Tamma.Api.Services.PromptStore.TammaModeProvider</c>'s
/// detection (the engine layer cannot reference <c>Tamma.Api</c> without a
/// dependency cycle, so the shared, pure <see cref="DeploymentMode.Resolve"/> in
/// Tamma.Core re-derives the SAME decision from the SAME config signals) — and
/// forwards <c>mode</c> + <c>tenantId</c> + <c>requireProdApproval</c> to
/// <c>single-issue-cycle</c>, which threads them into the pipeline.</para>
///
/// <para><b>Fail-safe:</b> an absent/unknown mode resolves to <c>business</c>
/// (REQUIRE approval), never a silent prod auto-deploy. An operator can force the
/// gate even in single-user mode via <c>Deployment:RequireProdApproval=true</c>.</para>
/// </summary>
[Activity(
    "Tamma.ADL",
    "Dispatch Issue Cycle",
    "Fire-and-forget dispatch of a single issue cycle workflow",
    Kind = ActivityKind.Task
)]
public class DispatchCycleActivity : TammaAsyncActivity
{
    public override string? EventType => "ADL.CYCLE.DISPATCH";

    private readonly IWorkflowDispatcher? _dispatcher;
    private readonly IConfiguration? _configuration;

    [Input(Description = "Repository (owner/repo)")]
    public Input<string> Repository { get; set; } = default!;

    [Input(Description = "Work item JSON")]
    public Input<string> WorkItemJson { get; set; } = default!;

    [Input(Description = "Issue number")]
    public Input<int> IssueNumber { get; set; } = default!;

    [Input(Description = "Bot assignee")]
    public Input<string> BotAssignee { get; set; } = default!;

    [Input(Description = "Base branch")]
    public Input<string> BaseBranch { get; set; } = default!;

    [Input(Description = "Tenant ID for tenant-scoped prompt resolution (empty = system defaults)")]
    public Input<string> TenantId { get; set; } = new("");

    /// <summary>
    /// Deployment mode (<c>business</c> | <c>dev</c>) forwarded to the cycle and,
    /// from there, into the deployment pipeline's production-approval gate. Left
    /// empty here means "derive from configuration in <see cref="RunAsync"/>" — the
    /// orchestrator wires this from the real operating mode; an explicit override is
    /// honoured if a caller sets it.
    /// </summary>
    [Input(Description = "Deployment mode (business|dev). Empty = derive from configuration.")]
    public Input<string> Mode { get; set; } = new("");

    [Output(Description = "Dispatched workflow instance ID")]
    public Output<string?> InstanceId { get; set; } = default!;

    [JsonConstructor]
    public DispatchCycleActivity() { }

    public DispatchCycleActivity(
        ILogger<DispatchCycleActivity> logger,
        IWorkflowDispatcher dispatcher,
        IConfiguration configuration)
    {
        Logger = logger;
        _dispatcher = dispatcher;
        _configuration = configuration;
    }

    protected override async Task RunAsync(ActivityExecutionContext context)
    {
        // Context fallback: Elsa rehydrates a persisted definition through the
        // [JsonConstructor], on which path the DI-injected field is null.
        var dispatcher = _dispatcher ?? context.GetService<IWorkflowDispatcher>();
        if (dispatcher == null)
        {
            Logger?.LogWarning("No IWorkflowDispatcher available, skipping dispatch");
            InstanceId.Set(context, (string?)null);
            return;
        }

        // Resolve the deployment mode end-to-end so the pipeline's production
        // approval gate engages for business/SaaS deployments. An explicit Mode
        // input wins; otherwise derive it from configuration (mirrors
        // TammaModeProvider's single-vs-SaaS detection). Fail-safe: an
        // absent/unknown mode resolves to "business" (REQUIRE approval).
        var mode = ResolveMode(context);

        // requireProdApproval lets an operator force the gate even in single-user
        // mode (Deployment:RequireProdApproval=true). Defaults false.
        var requireProdApproval =
            _configuration?.GetValue<bool>("Deployment:RequireProdApproval") ?? false;

        var input = new Dictionary<string, object>
        {
            ["repository"] = Repository.Get(context),
            ["workItemJson"] = WorkItemJson.Get(context),
            ["issueNumber"] = IssueNumber.Get(context),
            ["botAssignee"] = BotAssignee.Get(context),
            ["baseBranch"] = BaseBranch.Get(context),
            ["tenantId"] = TenantId.Get(context),
            ["mode"] = mode,
            ["requireProdApproval"] = requireProdApproval,
        };

        var instanceId = Guid.NewGuid().ToString();

        // FIRE & FORGET, and non-fatal by design. This activity sits upstream of the
        // orchestrator's cooldown → restart edge, so an exception here faults the
        // instance BEFORE it can dispatch its successor and the autonomous loop stops
        // permanently. Failing to start ONE issue cycle must cost that issue, never
        // the loop — the issue is still selectable on the next tick. The version-id
        // resolve lives INSIDE the try for the same reason (a transient DB read
        // failure must never fault the loop).
        try
        {
            // 2026-08-13 — the request ctor takes the VERSION id, not the definition
            // id (see PublishedWorkflowDispatch: every background dispatch failed
            // WorkflowGraphNotFound before this resolve step existed).
            var definitionVersionId = await Tamma.Activities.Core.PublishedWorkflowDispatch
                .ResolvePublishedVersionIdAsync(
                    context.GetRequiredService<Elsa.Workflows.Management.IWorkflowDefinitionService>(),
                    "single-issue-cycle");
            var request = new DispatchWorkflowDefinitionRequest(definitionVersionId)
            {
                Input = input,
                InstanceId = instanceId,
                // Story 43-14 (D5) — the cycle's correlation IS the cycle instance id.
                // The RunCorrelation middleware puts this on the ambient during the
                // cycle's execution, and CorrelationPropagatingWorkflowDispatcher
                // propagates it to every sub-workflow — so the whole chain shares one
                // ledger-visible correlation and a human's approval covers the run.
                CorrelationId = instanceId,
            };

            await dispatcher.DispatchAsync(request, default);

            InstanceId.Set(context, instanceId);

            Logger?.LogInformation(
                "Dispatched single-issue-cycle for issue #{IssueNumber}, instance {InstanceId} " +
                "(mode={Mode}, requireProdApproval={RequireProdApproval})",
                IssueNumber.Get(context), instanceId, mode, requireProdApproval);
        }
        catch (Exception ex)
        {
            InstanceId.Set(context, (string?)null);
            Logger?.LogError(
                ex,
                "Failed to dispatch single-issue-cycle for issue #{IssueNumber}; continuing so the "
                + "ADL loop restarts (the issue stays selectable on the next tick)",
                IssueNumber.Get(context));
        }
    }

    private string ResolveMode(ActivityExecutionContext context)
        => ResolveMode(Mode.Get(context), _configuration);

    /// <summary>
    /// Resolve the deployment <c>mode</c> threaded to the cycle/pipeline. An
    /// explicit <paramref name="explicitInput"/> (the orchestrator's <c>Mode</c>
    /// input) wins; otherwise derive it from the same config signals
    /// <c>TammaModeProvider</c> reads, via the shared, pure
    /// <see cref="DeploymentMode.Resolve"/>. Fail-safe to <c>business</c> (gate ON)
    /// when nothing can be determined — never a silent prod auto-deploy.
    ///
    /// <para>Pure + static (config in, token out) so the threading decision is
    /// unit-testable without an Elsa <c>ActivityExecutionContext</c>.</para>
    /// </summary>
    public static string ResolveMode(string? explicitInput, IConfiguration? configuration)
    {
        if (!string.IsNullOrWhiteSpace(explicitInput))
        {
            // Honour an explicit override but normalise it through the same
            // resolver so "saas"/"single-user" aliases map to the wire tokens.
            return DeploymentMode.Resolve(explicitInput, false, false);
        }

        var tammaMode = configuration?["Tamma:Mode"];
        var hasSharedSecret = !string.IsNullOrWhiteSpace(configuration?["Tamma:TenantSharedSecret"]);
        var hasControlPlane =
            !string.IsNullOrWhiteSpace(configuration?.GetConnectionString("ControlPlane"));

        return DeploymentMode.Resolve(tammaMode, hasSharedSecret, hasControlPlane);
    }

    public override Dictionary<string, object?> BuildStartData(ActivityExecutionContext context) => new()
    {
        ["issueNumber"] = IssueNumber.Get(context),
        ["repository"] = Repository.Get(context),
        ["mode"] = ResolveMode(context),
    };

    public override Dictionary<string, object?> BuildEndData(ActivityExecutionContext context) => new()
    {
        ["issueNumber"] = IssueNumber.Get(context),
        ["instanceId"] = this.GetOutput<string?>(context, nameof(InstanceId)),
    };
}
