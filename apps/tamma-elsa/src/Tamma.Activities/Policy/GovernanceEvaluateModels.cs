using System.Text.Json.Serialization;

namespace Tamma.Activities.Policy;

/// <summary>
/// Story 43-9 <b>Seam E</b> (AC10, D9) — the wire request for
/// <c>POST /api/v1/governance/evaluate</c>.
///
/// <para><b>Why an HTTP hop and not DI.</b> <c>Tamma.ElsaServer</c> registers no
/// repository and mediates everything through <c>TammaApiClient</c>;
/// <c>Tamma.ElsaServer.csproj</c> references only <c>Tamma.Activities</c> and the
/// analyzer, so <c>IAutonomyGate</c> — which lives in <c>Tamma.Api</c> and reads
/// the control-plane database — cannot be injected into an Elsa activity at all.
/// The engine therefore ASKS.</para>
///
/// <para>The models live in <c>Tamma.Activities</c> because both ends can see it:
/// the engine through its own reference, and <c>Tamma.Api</c> through the
/// reference it already has. Neither end owns a private copy of the contract.</para>
/// </summary>
/// <param name="Action">The catalog key wire, e.g. <c>effect:deploy.prod</c>.</param>
/// <param name="Role">Optional acting agent-role wire (checked against a resolved AllowedRoles restriction).</param>
/// <param name="Operation">Optional free-text operation tag, for audit only.</param>
/// <param name="Target">Optional free-text target tag, for audit only.</param>
/// <param name="CorrelationId">
/// The RUN this decision belongs to. Load-bearing rather than decorative: it is
/// the key the authorization ledger is scoped by, so one human grant covers the
/// whole run instead of one grant per retry.
/// </param>
public sealed record GovernanceEvaluateRequest(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("role")] string? Role = null,
    [property: JsonPropertyName("operation")] string? Operation = null,
    [property: JsonPropertyName("target")] string? Target = null,
    [property: JsonPropertyName("correlationId")] string? CorrelationId = null);

/// <summary>
/// Seam E's wire response — the projection of one <c>AutonomyDecision</c>.
/// </summary>
/// <param name="Outcome">
/// <c>automated</c> | <c>requires-human</c> | <c>denied</c>. Lower-kebab so a
/// workflow can compare it without knowing about a C# enum.
/// </param>
/// <param name="Action">The evaluated action key wire (echoed).</param>
/// <param name="Group">The action's group wire.</param>
/// <param name="AutonomyLevel">The dial position the decision was taken at.</param>
/// <param name="EffectiveMinAutonomy">The composed threshold that was applied.</param>
/// <param name="Enforced">
/// FALSE means observe-only: report the outcome, do NOT branch on it. A caller
/// that ignores this field turns an admin's "watch but do not block" into a block.
/// </param>
/// <param name="Source">Provenance wire of the winning tier.</param>
/// <param name="Reason">Machine-readable decision reason.</param>
/// <param name="AuthorizationId">
/// The pending <c>action_authorizations</c> row a person can now decide on (when
/// the outcome requires a human and a correlation id was supplied), or the grant
/// that was consumed (when <paramref name="CoveredBy"/> is set).
/// </param>
/// <param name="CoveredBy">The grant target that covered this action, when one did.</param>
public sealed record GovernanceEvaluateResponse(
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("group")] string? Group = null,
    [property: JsonPropertyName("autonomyLevel")] int AutonomyLevel = 0,
    [property: JsonPropertyName("effectiveMinAutonomy")] int EffectiveMinAutonomy = 0,
    [property: JsonPropertyName("enforced")] bool Enforced = false,
    [property: JsonPropertyName("source")] string? Source = null,
    [property: JsonPropertyName("reason")] string? Reason = null,
    [property: JsonPropertyName("authorizationId")] Guid? AuthorizationId = null,
    [property: JsonPropertyName("coveredBy")] string? CoveredBy = null)
{
    /// <summary>The three outcome wires, so neither end spells them by hand.</summary>
    public const string OutcomeAutomated = "automated";
    public const string OutcomeRequiresHuman = "requires-human";
    public const string OutcomeDenied = "denied";
}
