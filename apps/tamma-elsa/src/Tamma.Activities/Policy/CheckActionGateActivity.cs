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
///   <item><c>RequiresHuman</c> — a person decides.</item>
/// </list>
/// There is deliberately no <c>Denied</c> outcome: a <c>denied</c> resolution
/// (a disabled action, a role restriction, a non-escalatable target) is routed to
/// <c>RequiresHuman</c> so the graph's SAFE edge is taken. Minting a third edge
/// that every adopting workflow would have to remember to wire is how a governance
/// activity acquires an unrouted outcome and silently falls through.</para>
/// </summary>
[Activity(
    "Tamma.Policy",
    "Check Action Gate",
    "Ask the autonomy gate whether the system may perform a catalogued action by itself",
    Kind = ActivityKind.Task
)]
[FlowNode("Automated", "RequiresHuman")]
public class CheckActionGateActivity : Activity
{
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
            await context.CompleteActivityWithOutcomesAsync("Automated");
            return;
        }

        Outcome.Set(context, response.Outcome);
        Enforced.Set(context, response.Enforced);
        Reason.Set(context, response.Reason);
        AuthorizationId.Set(context, response.AuthorizationId?.ToString());

        // Observe-only resolutions take the Automated edge: `enforced = false` is
        // the admin's explicit "report but do not block", and honouring it here is
        // what lets an operator watch a tightening before it bites.
        var blocks = response.Enforced
            && !string.Equals(
                response.Outcome,
                GovernanceEvaluateResponse.OutcomeAutomated,
                StringComparison.Ordinal);

        await context.CompleteActivityWithOutcomesAsync(blocks ? "RequiresHuman" : "Automated");
    }

    private static string? Normalize(string? raw)
        => string.IsNullOrWhiteSpace(raw) ? null : raw.Trim();
}
