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
///
/// <para><b>There is no public constructor</b> (review 2.2, 2026-07-30). A
/// snapshot is minted through one of the two NAMED factories below, so a caller
/// must STATE whether these rows came from a successful read or from ignorance.
/// <see cref="IsAuthoritative"/> was previously an <c>init</c> property
/// defaulting to TRUE for test convenience — a default-true safety bit, which
/// means the next hand-built snapshot written by someone who has not read this
/// comment silently claims an authority it may not have and restores the
/// fail-open. Test convenience is not a reason to default a production safety
/// property.</para>
/// </summary>
public sealed record GovernancePolicySnapshot
{
    private static readonly IReadOnlyDictionary<string, ActionAssignmentValue> s_none =
        new Dictionary<string, ActionAssignmentValue>(StringComparer.Ordinal);

    private GovernancePolicySnapshot(
        IReadOnlyDictionary<string, ActionAssignmentValue> platformActionRows,
        IReadOnlyDictionary<string, ActionAssignmentValue> platformGroupRows,
        IReadOnlyDictionary<string, ActionAssignmentValue> principalActionRows,
        IReadOnlyDictionary<string, ActionAssignmentValue> principalGroupRows,
        bool isAuthoritative)
    {
        ArgumentNullException.ThrowIfNull(platformActionRows);
        ArgumentNullException.ThrowIfNull(platformGroupRows);
        ArgumentNullException.ThrowIfNull(principalActionRows);
        ArgumentNullException.ThrowIfNull(principalGroupRows);
        PlatformActionRows = platformActionRows;
        PlatformGroupRows = platformGroupRows;
        PrincipalActionRows = principalActionRows;
        PrincipalGroupRows = principalGroupRows;
        IsAuthoritative = isAuthoritative;
    }

    /// <summary>Platform-scope action rows (the ceiling), keyed by
    /// <see cref="ActionKey.ToWire"/>.</summary>
    public IReadOnlyDictionary<string, ActionAssignmentValue> PlatformActionRows { get; }

    /// <summary>Platform-scope group rows, keyed by
    /// <see cref="ActionGroupExtensions.ToWire"/>.</summary>
    public IReadOnlyDictionary<string, ActionAssignmentValue> PlatformGroupRows { get; }

    /// <summary>The resolving principal's own action rows.</summary>
    public IReadOnlyDictionary<string, ActionAssignmentValue> PrincipalActionRows { get; }

    /// <summary>The resolving principal's own group rows.</summary>
    public IReadOnlyDictionary<string, ActionAssignmentValue> PrincipalGroupRows { get; }

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
    /// non-authoritative snapshot (<see cref="AutonomyGateEvaluator.ReasonPolicySnapshotUnavailable"/>).</para>
    ///
    /// <para>It is set ONLY by <see cref="FromSuccessfulRead"/> (true) and
    /// <see cref="Unavailable"/> (false) — there is no way to construct a
    /// snapshot without choosing one.</para>
    /// </summary>
    public bool IsAuthoritative { get; }

    /// <summary>
    /// THE authoritative factory: these rows came back from a read of
    /// <c>action_assignments</c> that SUCCEEDED, so the absence of a row is the
    /// absence of policy and every member without a row resolves to its shipped
    /// default. Zero rows here is the ordinary zero-config deployment.
    /// </summary>
    public static GovernancePolicySnapshot FromSuccessfulRead(
        IReadOnlyDictionary<string, ActionAssignmentValue> platformActionRows,
        IReadOnlyDictionary<string, ActionAssignmentValue> platformGroupRows,
        IReadOnlyDictionary<string, ActionAssignmentValue> principalActionRows,
        IReadOnlyDictionary<string, ActionAssignmentValue> principalGroupRows)
        => new(platformActionRows, platformGroupRows,
               principalActionRows, principalGroupRows, isAuthoritative: true);

    /// <summary>Zero rows, AUTHORITATIVELY — the table was read and it is empty,
    /// so every member resolves to its shipped default (AC10).</summary>
    public static GovernancePolicySnapshot Empty { get; } =
        FromSuccessfulRead(s_none, s_none, s_none, s_none);

    /// <summary>
    /// The DEGRADED snapshot: no successful read has ever completed, so nothing
    /// may be concluded from the absence of a row (43-5 F6). It carries NO rows
    /// by construction — a snapshot that cannot testify about the rows it lacks
    /// has no business testifying about the rows it holds either.
    /// </summary>
    public static GovernancePolicySnapshot Unavailable { get; } =
        new(s_none, s_none, s_none, s_none, isAuthoritative: false);
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

    /// <summary>
    /// A policy input was UNREADABLE and the operator's BREAK-GLASS override was
    /// engaged, so the gate did NOT fail closed (43-5 F11 close, 2026-07-30).
    /// The resolution fell back to the shipped default (snapshot half) or kept
    /// the successfully-read row threshold minus the unreadable legacy floor
    /// (acceptance-rules half).
    ///
    /// <para>It is a THIRD value, distinct from both <see cref="SystemDefault"/>
    /// (a healthy decision) and <see cref="Unavailable"/> (a degraded decision
    /// that DID fail closed), because "an operator deliberately suspended the
    /// fail-closed posture" is a different fact from either, and an auditor must
    /// be able to select exactly those decisions.</para>
    ///
    /// <para><b>It never appears on a decision that a successfully-read policy
    /// row would have denied</b> — break-glass bypasses degradation only. That
    /// sentence shipped ahead of the code: until review MEDIUM-1 (2026-07-31) the
    /// evaluator stamped this value on the whole evaluation as soon as the
    /// override was in play, so a disabled row, a role mismatch and a platform
    /// ceiling all came back labelled <c>break-glass</c>. It is now true by
    /// construction, and true of MORE than it claimed: this value appears on
    /// exactly the decisions the override PERMITTED. A decision the override did
    /// not decide — denied by a read row, blocked by a shipped default, or allowed
    /// by a carve-out that was never going to be blocked — keeps the provenance of
    /// whatever actually decided it. Pinned in both directions by
    /// <c>BreakGlassProvenance_NeverAppearsOnADecisionTheOverrideDidNotPermit</c>
    /// and <c>BreakGlassProvenance_IsStillStamped_WhenTheOverrideGenuinelyPermitted</c>.
    /// See <see cref="AutonomyGateEvaluator"/>.</para>
    /// </summary>
    BreakGlass,
}

/// <summary>
/// THE BREAK-GLASS OVERRIDE for the fail-closed governance posture (Story 43-5
/// follow-up F11, closed 2026-07-30 — it was recorded as a BLOCKER on Story
/// 43-9).
///
/// <para><b>What it is for.</b> Since the F6 close, a governance policy input
/// that cannot be READ makes every catalogued action fail closed. That is the
/// right failure direction, but during a control-plane outage it leaves an
/// operator who has diagnosed the problem and accepted the risk with no lever
/// short of editing code. This is that lever.</para>
///
/// <para><b>What it is NOT.</b> It is not an off switch for policy. It suspends
/// exactly one thing: the substitution of
/// <see cref="Documents.Policy.AutonomyDial.AlwaysHuman"/> for an UNREADABLE
/// input. A decision denied by a policy row that WAS read successfully stays
/// denied while break-glass is engaged, and so does a shipped default that
/// blocks. That boundary is the difference between an outage lever and a
/// backdoor, and it is enforced by construction in
/// <see cref="AutonomyGateEvaluator"/> rather than by convention.</para>
///
/// <para><b>Why it carries a mandatory expiry.</b> A break-glass with no expiry
/// becomes the permanent configuration — the fail-open the F6 close removed,
/// re-introduced by an operator who forgot. The configuration source REFUSES to
/// engage without an explicit UTC expiry, or with one already in the past.</para>
/// </summary>
/// <param name="IsEngaged">Whether the override is in force right now.</param>
/// <param name="ExpiresAtUtc">When it stops being in force. Always non-null when
/// <paramref name="IsEngaged"/> is true — engaging without an expiry is refused
/// at the source.</param>
/// <param name="Reason">The operator's stated reason, carried into every audit
/// row the override produces.</param>
public sealed record BreakGlassState(
    bool IsEngaged,
    DateTimeOffset? ExpiresAtUtc,
    string? Reason)
{
    /// <summary>The ordinary state: no override, fail-closed intact.</summary>
    public static BreakGlassState NotEngaged { get; } = new(false, null, null);

    /// <summary>Engage until <paramref name="expiresAtUtc"/>.</summary>
    public static BreakGlassState Engaged(DateTimeOffset expiresAtUtc, string? reason) =>
        new(true, expiresAtUtc, reason);

    /// <summary>Reason text for audit, never null (the source defaults it).</summary>
    public string ReasonOrUnspecified =>
        string.IsNullOrWhiteSpace(Reason) ? "unspecified" : Reason!;
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
/// <param name="SeamCanBlock">
/// Adversarial review F4 (2026-08-01) — whether the SEAM asking this question is
/// capable of blocking on the answer. It gates the single-use ledger consult, and
/// nothing else.
///
/// <para><b>Why this and not <c>Enforced</c>.</b> The consult used to be gated on
/// <c>decision.Enforced</c>, which under epic D1 defaults TRUE — so Seam A
/// (<c>POST /api/v1/llm/call</c>), the one route whose entire design property is
/// "never blocks in any version", consumed a live grant on every call naming an
/// action with a correlation id. A single-use grant burned by a seam that was
/// never going to block is a human decision spent on nothing: the real, blocking
/// ask that follows finds no grant and escalates again. <c>Enforced</c> answers
/// "did the ADMIN ask for this to block"; this answers "CAN this caller block" —
/// two different facts, and the consult needs both.</para>
///
/// <para><b>It defaults to FALSE, deliberately.</b> A seam that forgets to say so
/// simply does not consume a grant, which leaves a <c>RequiresHuman</c> as
/// <c>RequiresHuman</c> — the fail-CLOSED direction. The opposite default would
/// make every future observe-only caller burn grants by omission.</para>
/// </param>
public sealed record AutonomyQuery(
    ActionKey Action,
    GovernancePrincipal Principal,
    string? Role = null,
    string? Operation = null,
    string? Target = null,
    string? CorrelationId = null,
    bool SeamCanBlock = false);

/// <summary>
/// One gate decision with every evaluated policy input, for audit tags and for
/// Story 43-9's seams. <see cref="Enforced"/> false means observe-only: the
/// outcome is reported but a seam must not block on it.
/// </summary>
/// <param name="AuthorizationId">
/// Story 43-9 AC12 — the <c>action_authorizations</c> row this decision is tied
/// to, when there is one. Two DIFFERENT facts share the field, told apart by
/// <see cref="CoveredBy"/>:
/// <list type="bullet">
/// <item>with <see cref="CoveredBy"/> NON-null: the id of the GRANT that was
/// consumed to turn a <see cref="AutonomyOutcome.RequiresHuman"/> into an
/// <see cref="AutonomyOutcome.Automated"/> — one human decision covering the
/// whole correlation rather than one per retry;</item>
/// <item>with <see cref="CoveredBy"/> null: the id of the PENDING request a
/// seam minted so the person has something to decide (the id a Seam C 409
/// hands back to the caller).</item>
/// </list>
/// The pure <see cref="AutonomyGateEvaluator"/> NEVER sets either — the ledger
/// is a database and the evaluator has no I/O. Both are stamped by
/// <c>AutonomyGateService</c> (the consult) and the seams (the request).
/// </param>
/// <param name="CoveredBy">
/// The wire string of the ledger grant's TARGET that covered this action — the
/// action key wire for an action-scoped grant, the group wire for a
/// group-scoped one. Non-null iff a grant was consumed. It is the group case
/// that makes the field worth carrying: an auditor must be able to see that one
/// <c>deploy-control</c> grant covered a member of that group, not a decision
/// taken about the member itself.
/// </param>
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
    string Reason,
    Guid? AuthorizationId = null,
    string? CoveredBy = null);

/// <summary>
/// Adversarial review F2 (2026-08-01) — <b>the gate DECIDED, and the decision
/// could not be RECORDED.</b> Thrown by the gate implementation when the
/// non-swallowing audit append for an enforced denial/escalation fails
/// (<c>ActionGateEventsService</c> deliberately rethrows those: a block with no
/// audit row is a compliance hole).
///
/// <para><b>Why it must be a distinct type.</b> Every 43-9 seam wraps the gate in
/// a catch-all that fails OPEN, on the stated posture "deny on a DECISION, never
/// on an ERROR". A rethrown audit failure is not an error in that sense — the
/// decision exists, it blocks, and only the record of it is missing. Before this
/// type the two were indistinguishable, so a genuine enforced DENIAL whose append
/// failed was read as a transient fault and the request PROCEEDED. That is not a
/// remote coincidence: 43-5's fail-closed degradation fires exactly when the
/// control plane is unreadable, and <c>domain_events</c> lives in the SAME
/// Postgres, so one blip produces the fail-closed decision AND the failing append
/// together.</para>
///
/// <para>A seam that can act on it re-applies <see cref="Decision"/> (see
/// <see cref="Blocks"/>); a seam that cannot lets it propagate, which is the
/// fail-CLOSED behaviour the tool-loop seam already had.</para>
/// </summary>
public sealed class AutonomyGateDecisionUnrecordedException : Exception
{
    /// <summary>Create the wrapper for a decision whose audit row failed.</summary>
    public AutonomyGateDecisionUnrecordedException(AutonomyDecision decision, Exception inner)
        : base(
            $"The autonomy gate decided '{decision?.Outcome}' for "
            + $"'{decision?.Action.ToWire()}' but the decision could not be recorded in the "
            + "audit stream. The decision STANDS — an unrecordable block is still a block.",
            inner)
    {
        ArgumentNullException.ThrowIfNull(decision);
        Decision = decision;
    }

    /// <summary>The decision that WAS made, and whose audit row failed.</summary>
    public AutonomyDecision Decision { get; }

    /// <summary>
    /// Whether <see cref="Decision"/> is one a seam must block on: enforced, and
    /// not <see cref="AutonomyOutcome.Automated"/>. The same predicate every seam
    /// applies to a decision it received normally — an audit failure changes
    /// nothing about it.
    /// </summary>
    public bool Blocks =>
        Decision.Enforced && Decision.Outcome != AutonomyOutcome.Automated;
}

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
