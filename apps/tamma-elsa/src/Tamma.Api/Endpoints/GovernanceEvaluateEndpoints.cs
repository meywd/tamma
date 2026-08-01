using Tamma.Activities.Policy;
using Tamma.Api.Services.Actions;
using Tamma.Core.Actions;

namespace Tamma.Api.Endpoints;

/// <summary>
/// Story 43-9 <b>Seam E</b> (AC10, D9) — <c>POST /api/v1/governance/evaluate</c>,
/// the engine's mediation route to the autonomy gate.
///
/// <para><b>Why the route exists at all.</b> <c>Tamma.ElsaServer</c> registers no
/// repository and mediates everything through <c>TammaApiClient</c>, so
/// <c>IAutonomyGate</c> — which reads the control plane — cannot be injected into
/// an Elsa activity. <c>CheckActionGateActivity</c> asks over HTTP instead.</para>
///
/// <para><b>THE ROUTE CANNOT GATE ITSELF.</b> It mints no <c>ExternalEffect</c>
/// member (it is a read) and carries no <c>.Governs</c> binding; it is baselined
/// in <c>KnownUngovernedEndpoints</c> with the justification
/// <c>gate-evaluation-endpoint-cannot-gate-itself</c>. Anything else is circular:
/// a governed gate-evaluation route would have to evaluate the gate to decide
/// whether it may evaluate the gate.</para>
///
/// <para><b>It is a READ that can WRITE one row, and that is deliberate.</b> When
/// the decision requires a human and the caller supplied a correlation id, the
/// handler records a PENDING <c>action_authorizations</c> row and returns its id.
/// Without it the engine would be told "a person must decide" with no person able
/// to — <c>DecideAsync</c> needs a row to transition. The write is idempotent per
/// <c>(principal, correlation, target)</c> by the ledger's partial unique index,
/// so a retrying workflow re-finds the same pending row rather than minting a
/// queue of them.</para>
///
/// <para><b>Auth: <c>EngineServiceOnly</c></b>, like every other mediation route
/// — the typed service principal that <c>ApiKeyAuthHandler</c> mints for a
/// <c>service</c>-scope key. A user JWT authenticates but never produces one ⇒
/// 403.</para>
/// </summary>
public static class GovernanceEvaluateEndpoints
{
    /// <summary>
    /// Evaluate one catalogued action for the ambient principal and project the
    /// decision onto the wire.
    /// </summary>
    public static async Task<IResult> Evaluate(
        GovernanceEvaluateRequest request,
        IAutonomyGate gate,
        IGovernancePrincipalResolver principals,
        IActionAuthorizationRequests authorizations,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Action)
            || !ActionKey.TryParse(request.Action, out var actionKey))
        {
            // A malformed key is a 400, never a coerced evaluation: answering
            // "automated" to a question we could not parse is exactly the silent
            // ungoverning this epic exists to prevent. (A well-formed key with no
            // catalog entry is DIFFERENT and is allowed through by epic D2 — the
            // evaluator answers Automated with reason `uncatalogued`.)
            return Results.BadRequest(new
            {
                code = "ACTION.GATE.BAD_KEY",
                error = "action must be a well-formed catalog key wire, e.g. "
                    + "'effect:deploy.promote-prod'.",
            });
        }

        var principal = await principals.ResolveAsync(caller: null, ct).ConfigureAwait(false);

        var decision = await gate.EvaluateAsync(
            new AutonomyQuery(
                actionKey,
                principal,
                request.Role,
                request.Operation,
                request.Target,
                request.CorrelationId),
            ct).ConfigureAwait(false);

        Guid? authorizationId = decision.AuthorizationId;
        if (authorizationId is null
            && decision.Enforced
            && decision.Outcome == AutonomyOutcome.RequiresHuman
            && !string.IsNullOrWhiteSpace(request.CorrelationId))
        {
            authorizationId = await authorizations
                .RequestAsync(principal, decision, request.CorrelationId!, ct)
                .ConfigureAwait(false);
        }

        return Results.Ok(new GovernanceEvaluateResponse(
            Outcome: OutcomeWire(decision.Outcome),
            Action: decision.Action.ToWire(),
            Group: decision.Group.ToWire(),
            AutonomyLevel: decision.AutonomyLevel,
            EffectiveMinAutonomy: decision.EffectiveMinAutonomy,
            Enforced: decision.Enforced,
            Source: decision.Source.ToString(),
            Reason: decision.Reason,
            AuthorizationId: authorizationId,
            CoveredBy: decision.CoveredBy));
    }

    private static string OutcomeWire(AutonomyOutcome outcome) => outcome switch
    {
        AutonomyOutcome.Automated => GovernanceEvaluateResponse.OutcomeAutomated,
        AutonomyOutcome.RequiresHuman => GovernanceEvaluateResponse.OutcomeRequiresHuman,
        AutonomyOutcome.Denied => GovernanceEvaluateResponse.OutcomeDenied,
        // Fail CLOSED on an unmapped enum member: a new outcome nobody projected
        // must not read as "proceed" on the wire.
        _ => GovernanceEvaluateResponse.OutcomeDenied,
    };
}
