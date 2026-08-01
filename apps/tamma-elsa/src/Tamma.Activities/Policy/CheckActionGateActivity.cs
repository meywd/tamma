using System.Text.Json.Serialization;
using Elsa.Extensions;
using Elsa.Workflows;
using Elsa.Workflows.Activities.Flowchart.Attributes;
using Elsa.Workflows.Attributes;
using Elsa.Workflows.Models;
using Microsoft.Extensions.Logging;
using Tamma.Activities.LlmCall;

namespace Tamma.Activities.Policy;

/// <summary>
/// Story 43-9 <b>Seam E</b> (AC10/AC11, D9/D10) — the engine's view of the
/// autonomy gate. Asks <c>POST /api/v1/governance/evaluate</c> through
/// <see cref="TammaApiClient"/> whether the system may perform a catalogued
/// action by itself, and routes to <c>Automated</c> or <c>RequiresHuman</c>.
///
/// <para><b>Why over HTTP.</b> <c>Tamma.ElsaServer</c> registers no repository
/// and mediates everything through <see cref="TammaApiClient"/>; its csproj
/// references only <c>Tamma.Activities</c> and the analyzer. <c>IAutonomyGate</c>
/// lives in <c>Tamma.Api</c> and reads the control-plane database, so injecting
/// it here is not possible — the engine ASKS.</para>
///
/// <para><b>It is the ONLY seam in this story where <c>RequiresHuman</c> means
/// anything.</b> Seam A never blocks, Seam B has no human on its path (its
/// outcome enum has no <c>RequiresHuman</c> case at all, deliberately), and a
/// Seam D sweeper cannot suspend for a person. Seam E is where a real human wait
/// already exists — <c>WaitForDeploymentApprovalActivity</c> — which is why
/// Story 43-5's follow-up F12 ("the degraded outcome is a denial, not an
/// escalation, until 43-9 lands") closes HERE and nowhere else.</para>
///
/// <para><b>FAIL OPEN on a transport failure, and that is safe ONLY because of
/// how the one v1 adoption is wired</b> (D10). The deployment pipeline ORs this
/// activity's outcome into an existing <c>prodApprovalNeeded</c> predicate; a
/// null response therefore leaves the pre-existing business-mode and
/// <c>requireProdApproval</c> terms exactly as they are, which is today's
/// behaviour. Fail-open here is "the new term contributed nothing", never "the
/// gate was removed". A future adoption that REPLACES a predicate rather than
/// OR-ing into it would make this posture wrong and must revisit it.</para>
///
/// <para>Outcomes:
/// <list type="bullet">
///   <item><c>Automated</c> — the system may proceed by itself. Also the outcome
///   for an observe-only (<c>enforced = false</c>) resolution and for a transport
///   failure; <see cref="Enforced"/> and <see cref="Outcome"/> are surfaced as
///   outputs so a graph that cares can tell them apart.</item>
///   <item><c>RequiresHuman</c> — a person decides. Also the outcome for any
///   enforced wire this activity does not recognise: an unknown non-<c>automated</c>
///   answer fails CLOSED onto the safe edge.</item>
///   <item><c>Denied</c> — the system may not do this and <b>no person on this
///   graph may authorise it either</b>.</item>
/// </list></para>
///
/// <para><b>Why <c>Denied</c> is its own edge (2026-08-01 review finding F1).</b>
/// It used to be folded into <c>RequiresHuman</c>, on the argument that a third
/// edge is one every adopting workflow must remember to wire and an unrouted
/// outcome silently falls through. That argument was sound about the RISK and
/// wrong about the REMEDY, and the fold-in produced a MONOTONICITY INVERSION at
/// the one live adoption: setting <c>effect:deploy.promote-prod</c> to
/// <c>AlwaysHuman</c> added a production wait, but DISABLING the action — the
/// strictly stronger admin setting — added nothing, because the deployment
/// pipeline routes on the <c>Outcome</c> VARIABLE (which carried the raw
/// <c>denied</c> wire) and wired both edges to the same node, making the edge
/// choice behaviourally inert.
///
/// <para>The two ways a catalogued effect resolves to <c>denied</c> are an
/// <c>Enabled = false</c> row and an <c>AllowedRoles</c> restriction that excludes
/// the actor. Neither is "a person may approve this": a human clicking Approve on
/// a deployment card is approving a DEPLOYMENT, not re-enabling an action an
/// admin switched off. Routing a denial into a standing approval flow would let
/// the approval flow override the admin — so it gets its own edge and each
/// adopting graph decides what a hard refusal means for it. The
/// remember-to-wire-it risk is answered by a STRUCTURAL TEST that every
/// <c>CheckActionGateActivity</c> in a workflow has all three outcomes connected
/// (<c>DeploymentPipelineGateTests.EveryGateOutcome_isWired_noDanglingEdge</c>),
/// which turns a forgotten edge into a build failure instead of a silent
/// fall-through.</para></para>
///
/// <para><b>Observe-only still never hard-refuses.</b> <c>enforced = false</c>
/// takes the <c>Automated</c> edge whatever the wire says, so an admin's "report
/// but do not block" can never route a graph into a refusal terminal.</para>
/// </summary>
[Activity(
    "Tamma.Policy",
    "Check Action Gate",
    "Ask the autonomy gate whether the system may perform a catalogued action by itself",
    Kind = ActivityKind.Task
)]
[FlowNode("Automated", "RequiresHuman", "Denied")]
public class CheckActionGateActivity : Activity
{
    /// <summary>The allow / proceed edge.</summary>
    public const string EdgeAutomated = "Automated";

    /// <summary>The "a person decides" edge.</summary>
    public const string EdgeRequiresHuman = "RequiresHuman";

    /// <summary>The hard-refusal edge — nobody on this graph may authorise it.</summary>
    public const string EdgeDenied = "Denied";

    /// <summary>Every edge this activity can complete with, for structural sweeps.</summary>
    public static readonly IReadOnlyList<string> Edges =
        new[] { EdgeAutomated, EdgeRequiresHuman, EdgeDenied };

    private readonly ILogger<CheckActionGateActivity>? _logger;
    private readonly TammaApiClient? _apiClient;

    [Input(Description = "Catalog key wire, e.g. effect:deploy.promote-prod")]
    public Input<string> ActionKey { get; set; } = default!;

    [Input(Description = "Optional acting agent-role wire")]
    public Input<string?> Role { get; set; } = new((string?)null);

    [Input(Description = "Optional free-text operation tag (audit only)")]
    public Input<string?> Operation { get; set; } = new((string?)null);

    [Input(Description = "Optional free-text target tag (audit only)")]
    public Input<string?> Target { get; set; } = new((string?)null);

    [Input(Description = "Run correlation id — the key one human grant covers")]
    public Input<string?> CorrelationId { get; set; } = new((string?)null);

    [Input(Description = "Tenant id (GUID string); empty = single-user/platform scope")]
    public Input<string?> TenantId { get; set; } = new((string?)null);

    [Output(Description = "automated | requires-human | denied | unavailable")]
    public Output<string?> Outcome { get; set; } = default!;

    [Output(Description = "False means observe-only: report, do not block")]
    public Output<bool> Enforced { get; set; } = default!;

    [Output(Description = "Pending authorization id a person can decide on, when there is one")]
    public Output<string?> AuthorizationId { get; set; } = default!;

    [Output(Description = "Machine-readable decision reason")]
    public Output<string?> Reason { get; set; } = default!;

    /// <summary>The outcome wire written when the gate could not be reached.</summary>
    public const string OutcomeUnavailable = "unavailable";

    [JsonConstructor]
    public CheckActionGateActivity() { }

    /// <summary>DI constructor (the thin-client shape every mediated activity uses).</summary>
    public CheckActionGateActivity(
        ILogger<CheckActionGateActivity>? logger,
        TammaApiClient? apiClient)
    {
        _logger = logger;
        _apiClient = apiClient;
    }

    /// <inheritdoc />
    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var actionKey = ActionKey.Get(context) ?? "";
        var correlationId = Normalize(CorrelationId.Get(context))
            ?? context.WorkflowExecutionContext.Id;
        var tenantId = Normalize(TenantId.Get(context));

        GovernanceEvaluateResponse? response = null;
        try
        {
            var apiClient = _apiClient ?? context.GetService<TammaApiClient>();
            if (apiClient is not null)
            {
                response = await apiClient.EvaluateGovernanceAsync(
                    new GovernanceEvaluateRequest(
                        actionKey,
                        Normalize(Role.Get(context)),
                        Normalize(Operation.Get(context)),
                        Normalize(Target.Get(context)),
                        correlationId),
                    tenantId,
                    context.CancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // The client already swallows transport failures into null; this is
            // the belt for anything else (a DI resolution problem, a serializer
            // fault). An activity that THREW here would fault the deployment
            // pipeline over a governance read, which is strictly worse than the
            // OR term contributing nothing.
            _logger?.LogWarning(ex, "Action-gate evaluation threw for {ActionKey}", actionKey);
        }

        if (response is null)
        {
            _logger?.LogWarning(
                "Action-gate evaluation UNAVAILABLE for {ActionKey} (correlation {CorrelationId}); "
                + "taking the Automated edge so the pre-existing approval predicate is unchanged. "
                + "This is fail-open on an ERROR, not on a decision.",
                actionKey, correlationId);

            Outcome.Set(context, OutcomeUnavailable);
            Enforced.Set(context, false);
            Reason.Set(context, "gate-unavailable");
            await context.CompleteActivityWithOutcomesAsync(EdgeAutomated);
            return;
        }

        Outcome.Set(context, response.Outcome);
        Enforced.Set(context, response.Enforced);
        Reason.Set(context, response.Reason);
        AuthorizationId.Set(context, response.AuthorizationId?.ToString());

        await context.CompleteActivityWithOutcomesAsync(
            SelectEdge(response.Enforced, response.Outcome));
    }

    /// <summary>
    /// Map one resolved decision onto an edge. Pure, so the routing rule is
    /// testable without an Elsa execution context.
    ///
    /// <list type="bullet">
    ///   <item>Observe-only resolutions take <c>Automated</c>: <c>enforced = false</c>
    ///   is the admin's explicit "report but do not block", and honouring it here is
    ///   what lets an operator watch a tightening before it bites.</item>
    ///   <item><c>denied</c> takes <c>Denied</c> — a hard refusal, NOT an escalation
    ///   (see the class doc).</item>
    ///   <item>Everything else enforced and non-<c>automated</c> takes
    ///   <c>RequiresHuman</c>, INCLUDING a wire this build does not recognise: an
    ///   unknown enforced answer fails closed onto the safe edge rather than
    ///   proceeding.</item>
    /// </list>
    /// </summary>
    public static string SelectEdge(bool enforced, string? outcomeWire)
    {
        var wire = outcomeWire?.Trim();

        if (!enforced
            || string.Equals(wire, GovernanceEvaluateResponse.OutcomeAutomated, StringComparison.OrdinalIgnoreCase))
        {
            return EdgeAutomated;
        }

        return string.Equals(wire, GovernanceEvaluateResponse.OutcomeDenied, StringComparison.OrdinalIgnoreCase)
            ? EdgeDenied
            : EdgeRequiresHuman;
    }

    private static string? Normalize(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
}
