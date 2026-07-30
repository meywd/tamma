using Tamma.Core.Documents.Policy;

namespace Tamma.Core.Actions;

/// <summary>
/// The principal a governance decision is resolved FOR (Story 43-5 AC7).
/// Exactly one of <see cref="TenantId"/> (SaaS) / <see cref="UserId"/>
/// (single-user) is set for a principal-scoped resolution; BOTH null is the
/// platform-only resolution (SaaS request with no resolvable tenant — the
/// resolver emits <c>ACTION.GATE.PRINCIPAL_UNRESOLVED</c> and applies platform
/// rows + shipped defaults only, never a user row).
/// </summary>
public sealed record GovernancePrincipal(Guid? TenantId, Guid? UserId)
{
    /// <summary>Neither key set — platform rows + shipped defaults only.</summary>
    public bool IsPlatformOnly => TenantId is null && UserId is null;

    /// <summary>SaaS principal.</summary>
    public static GovernancePrincipal ForTenant(Guid tenantId) => new(tenantId, null);

    /// <summary>Single-user principal.</summary>
    public static GovernancePrincipal ForUser(Guid userId) => new(null, userId);

    /// <summary>The unresolved / platform-only principal.</summary>
    public static GovernancePrincipal Platform { get; } = new(null, null);
}

/// <summary>
/// The three nullable policy fields of one <c>action_assignments</c> row
/// (Story 43-5 AC2/D4): every field is independently "unset" (null → inherit
/// from the next tier). "No opinion at all" is the ABSENCE of the row, not a
/// null-stuffed one.
/// </summary>
public sealed record ActionAssignmentValue(
    int? MinAutonomy,
    bool? Enforce,
    bool? Enabled,
    IReadOnlyList<string>? AllowedRoles);

/// <summary>
/// The per-principal projection of the <c>action_assignments</c> table the pure
/// evaluator consumes (Story 43-5 D6): platform-scope rows (the ceiling) plus
/// the resolving principal's own rows, each split by target kind. Keys are wire
/// strings — <see cref="ActionKey.ToWire"/> for action rows,
/// <see cref="ActionGroupExtensions.ToWire"/> for group rows.
/// </summary>
public sealed record GovernancePolicySnapshot(
    IReadOnlyDictionary<string, ActionAssignmentValue> PlatformActionRows,
    IReadOnlyDictionary<string, ActionAssignmentValue> PlatformGroupRows,
    IReadOnlyDictionary<string, ActionAssignmentValue> PrincipalActionRows,
    IReadOnlyDictionary<string, ActionAssignmentValue> PrincipalGroupRows)
{
    private static readonly IReadOnlyDictionary<string, ActionAssignmentValue> s_none =
        new Dictionary<string, ActionAssignmentValue>(StringComparer.Ordinal);

    /// <summary>
    /// TRUE when these rows are backed by a SUCCESSFUL read of
    /// <c>action_assignments</c> (43-5 F6 close, 2026-07-30). FALSE means the
    /// store has never completed a load — priming failed and no lazy refresh has
    /// landed yet — so "zero rows" is IGNORANCE, not the absence of policy.
    ///
    /// <para><b>Why the distinction is load-bearing:</b> before this field the two
    /// states were byte-identical (both were <see cref="Empty"/>), so a restart
    /// with the control plane down served an empty snapshot and every admin
    /// tightening was silently unenforced until a refresh succeeded — the gate
    /// failed OPEN on a degraded read. The evaluator now fails CLOSED on a
    /// non-authoritative snapshot (<see cref="AutonomyGateEvaluator.ReasonPolicySnapshotUnavailable"/>).
    /// Defaults to TRUE so every hand-built snapshot (tests, the 43-6 fresh-read
    /// pin) keeps meaning "these ARE the rows".</para>
    /// </summary>
    public bool IsAuthoritative { get; init; } = true;

    /// <summary>Zero rows, AUTHORITATIVELY — the table was read and it is empty,
    /// so every member resolves to its shipped default (AC10).</summary>
    public static GovernancePolicySnapshot Empty { get; } = new(s_none, s_none, s_none, s_none);

    /// <summary>The DEGRADED snapshot: no successful read has ever completed, so
    /// nothing may be concluded from the absence of a row (43-5 F6).</summary>
    public static GovernancePolicySnapshot Unavailable { get; } =
        Empty with { IsAuthoritative = false };
}

/// <summary>Gate outcome for one governed action (Story 43-5 AC8).</summary>
public enum AutonomyOutcome
{
    /// <summary>The system may perform the action itself at the current dial.</summary>
    Automated,

    /// <summary>A person decides — only meaningful where a human wait exists
    /// (<see cref="ActionDescriptor.EscalatableToHuman"/>).</summary>
    RequiresHuman,

    /// <summary>The action may not proceed and there is no human route on this
    /// path (the tool loop, a sweeper) — calling it escalation would be a lie
    /// (43-5 step 8).</summary>
    Denied,
}

/// <summary>Provenance of the winning tier of a resolution (Story 43-5 AC8).</summary>
public enum ActionAssignmentSource
{
    /// <summary>A platform-scope row raised the value via <c>max()</c>.</summary>
    PlatformCeiling,

    /// <summary>The legacy <c>AcceptanceRules.AlwaysEscalate</c> floor
    /// (via <see cref="AcceptanceGuardrails.TryPreGate"/>) raised it to
    /// <see cref="AutonomyDial.AlwaysHuman"/>.</summary>
    AlwaysEscalateLegacy,

    /// <summary>The principal's own action-scoped row.</summary>
    ActionOverride,

    /// <summary>The principal's group-scoped row.</summary>
    GroupOverride,

    /// <summary>The shipped <see cref="ActionDescriptor.DefaultMinAutonomy"/>.</summary>
    SystemDefault,

    /// <summary>
    /// NO tier could be resolved — a policy input was UNREADABLE (43-5 F6 close,
    /// 2026-07-30): either the assignment snapshot has never loaded
    /// (<see cref="GovernancePolicySnapshot.IsAuthoritative"/> false) or the
    /// principal's base acceptance rules could not be read, which means the
    /// legacy always-escalate floor cannot be ruled out. The decision is
    /// FAIL-CLOSED at <see cref="Documents.Policy.AutonomyDial.AlwaysHuman"/> and
    /// <c>Enforced</c> is forced TRUE — never a silently-defaulted automation.
    /// It is a distinct provenance precisely so a degraded decision can never be
    /// mistaken for <see cref="SystemDefault"/> in the audit stream.
    /// </summary>
    Unavailable,
}

/// <summary>One gate question (Story 43-5 AC8).</summary>
/// <param name="Action">The catalog address of the action about to happen.</param>
/// <param name="Principal">Who policy is resolved for (43-5 AC7).</param>
/// <param name="Role">Optional acting agent-role wire, checked against a
/// resolved <c>AllowedRoles</c> restriction.</param>
/// <param name="Operation">Optional free-text operation tag for audit.</param>
/// <param name="Target">Optional free-text target tag for audit.</param>
/// <param name="CorrelationId">Optional run correlation id for audit + the
/// authorization ledger.</param>
public sealed record AutonomyQuery(
    ActionKey Action,
    GovernancePrincipal Principal,
    string? Role = null,
    string? Operation = null,
    string? Target = null,
    string? CorrelationId = null);

/// <summary>
/// One gate decision with every evaluated policy input, for audit tags and for
/// Story 43-9's seams. <see cref="Enforced"/> false means observe-only: the
/// outcome is reported but a seam must not block on it.
/// </summary>
public sealed record AutonomyDecision(
    AutonomyOutcome Outcome,
    ActionKey Action,
    ActionGroup Group,
    ActionRisk Risk,
    int AutonomyLevel,
    int EffectiveMinAutonomy,
    ActionAssignmentSource Source,
    bool Enforced,
    bool Enabled,
    IReadOnlyList<string>? AllowedRoles,
    string Reason);

/// <summary>
/// THE autonomy gate (Story 43-5): resolves whether an action is automated,
/// requires a person, or is denied, for the current principal, composing the
/// platform ceiling, the legacy always-escalate floor and the principal ladder.
/// Interface lives in <c>Tamma.Core</c> (the <c>IAcceptanceRulesResolver</c>
/// Core/Api split, 39-5 D1); the DB-backed implementation
/// (<c>AutonomyGateService</c>) lives in <c>Tamma.Api</c>. Ships in 43-5 with
/// no production seam caller — Story 43-9 owns all five seams.
/// </summary>
public interface IAutonomyGate
{
    /// <summary>Evaluate one governed action for the ambient principal.</summary>
    Task<AutonomyDecision> EvaluateAsync(AutonomyQuery query, CancellationToken ct = default);
}
