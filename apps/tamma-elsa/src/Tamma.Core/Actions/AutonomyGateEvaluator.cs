using Tamma.Core.Documents;
using Tamma.Core.Documents.Policy;

namespace Tamma.Core.Actions;

/// <summary>
/// The PURE autonomy-resolution ladder (Story 43-5 AC8, D6/D7/D8). No I/O, no
/// DI, static — every ladder case is testable without a database, because the
/// ladder is the part most likely to be subtly wrong.
///
/// <para><b>The composition (epic README §4):</b></para>
/// <code>
/// effectiveMinAutonomy(action, principal) =
///     max( platformCeiling(action),            // platform rows: action → group → no ceiling
///          legacyAlwaysEscalateFloor(action),  // AcceptanceGuardrails.TryPreGate, always-escalate ONLY
///          principalLadder(action) )           // first present of: action row → group row → shipped default
/// </code>
///
/// <para><b><c>max()</c> outside, <c>??</c> inside — different operators for
/// different reasons, and the distinction is load-bearing (D7):</b> the
/// encoding is monotone (higher = more human), so <c>max()</c> means the
/// platform can only TIGHTEN and a tenant admin can never lower a platform
/// gate; inside the principal ladder an action override BEATS its group
/// outright (<c>??</c>) — that is what "individual actions override their
/// group" means, and it is what <c>AcceptanceRulesService</c>'s
/// override-beats-base walk already does. Consequence (recorded risk, not
/// designed away): an admin can lower one action below its group.</para>
///
/// <para><b>The legacy floor (D8):</b> the evaluator gives
/// <see cref="AcceptanceGuardrails.TryPreGate"/> its first production call
/// site, consuming ONLY the always-escalate contribution — the rules body
/// passed in is pre-filtered to the classes matching this <see cref="ActionKey"/>,
/// so a match is by construction about this key, and the rounds-exhausted
/// outcome is discarded by checking
/// <see cref="AcceptanceEscalationReason.AlwaysEscalateClass"/>. The floor
/// contributes <see cref="AutonomyDial.AlwaysHuman"/> into the <c>max()</c>,
/// so a legacy entry cannot be lowered from the new surface — only deleting it
/// in the acceptance-rules UI removes it (AC9).</para>
///
/// <para><b>Per-field independence:</b> <c>Enforce</c>, <c>Enabled</c> and
/// <c>AllowedRoles</c> resolve independently on their own ladders — a row that
/// sets only a threshold must not carry its NULLs down as decisions (the D4
/// bug class: a non-nullable <c>enabled DEFAULT TRUE</c> would silently
/// re-enable a group-disabled action).</para>
/// </summary>
public static class AutonomyGateEvaluator
{
    /// <summary>Machine-readable decision reasons (audit tag vocabulary).</summary>
    public const string ReasonUncatalogued = "uncatalogued";
    public const string ReasonNotEnforceable = "not-enforceable";
    public const string ReasonDisabled = "disabled";
    public const string ReasonRoleNotAllowed = "role-not-allowed";
    public const string ReasonAutomated = "at-or-above-min-autonomy";
    public const string ReasonAlwaysHuman = "always-human";
    public const string ReasonBelowMinAutonomy = "below-min-autonomy";

    /// <summary>
    /// Evaluate one action against a policy snapshot and the principal's
    /// resolved BASE acceptance rules (the dial + the legacy always-escalate
    /// list — Story 43-5 AC11's <c>ResolveBase*Async</c> provides it).
    /// </summary>
    public static AutonomyDecision Evaluate(
        AutonomyQuery query,
        GovernancePolicySnapshot snapshot,
        ResolvedAcceptanceRules baseRules)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(baseRules);

        var dial = baseRules.Rules.AutonomyLevel;

        if (!ActionCatalog.TryGet(query.Action, out var descriptor) || descriptor is null)
        {
            // Epic decision D2 — unclassified is allowed at RUNTIME (and
            // unmergeable in CI via the drift harnesses); a catalog gap must
            // never stall a live workflow. Not silent: callers log/audit it.
            return new AutonomyDecision(
                AutonomyOutcome.Automated, query.Action, default, default,
                dial, AutonomyDial.Min, ActionAssignmentSource.SystemDefault,
                Enforced: false, Enabled: true, AllowedRoles: null,
                Reason: ReasonUncatalogued);
        }

        var actionWire = descriptor.Key.ToWire();
        var groupWire = descriptor.Group.ToWire();

        var platformAction = Row(snapshot.PlatformActionRows, actionWire);
        var platformGroup = Row(snapshot.PlatformGroupRows, groupWire);
        var principalAction = Row(snapshot.PrincipalActionRows, actionWire);
        var principalGroup = Row(snapshot.PrincipalGroupRows, groupWire);

        // ── The threshold ladder (principal ladder + platform ceiling — the
        //    same composition the Seam B tool-loop gate reads) ───────────────
        var (effectiveMin, source) = ResolveEffectiveMinAutonomy(descriptor, snapshot);

        // Legacy always-escalate floor (agent-action / document-type planes
        // only — EscalationClassKind has no other members). Strictly-greater:
        // it can only raise, never relabel an already-AlwaysHuman resolution.
        if (LegacyAlwaysEscalates(descriptor.Key, baseRules.Rules)
            && AutonomyDial.AlwaysHuman > effectiveMin)
        {
            (effectiveMin, source) =
                (AutonomyDial.AlwaysHuman, ActionAssignmentSource.AlwaysEscalateLegacy);
        }

        // ── Per-field ladders (independent — never inherited from the
        //    threshold row's NULLs) ───────────────────────────────────────────
        // Enforce: a platform opinion wins when present, else the principal's,
        // else the v1 default TRUE (epic D1 — v1 enforces).
        var enforced =
            (platformAction?.Enforce ?? platformGroup?.Enforce)
            ?? (principalAction?.Enforce ?? principalGroup?.Enforce)
            ?? true;

        // Enabled: monotone like the threshold — EITHER plane resolving FALSE
        // disables (a tenant cannot re-enable a platform-disabled action, and
        // a platform row without an opinion leaves the tenant's disable alone).
        var platformEnabled = platformAction?.Enabled ?? platformGroup?.Enabled ?? true;
        var principalEnabled = principalAction?.Enabled ?? principalGroup?.Enabled ?? true;
        var enabled = platformEnabled && principalEnabled;

        // AllowedRoles: the principal's restriction wins outright when present
        // (it is a principal-authored allowlist); else the platform's.
        var allowedRoles =
            (principalAction?.AllowedRoles ?? principalGroup?.AllowedRoles)
            ?? (platformAction?.AllowedRoles ?? platformGroup?.AllowedRoles);

        // A member the gate may never block on (effect:secret.reveal is the
        // only shipped one): informational only, whatever the rows say.
        if (!descriptor.Enforceable)
        {
            return Decision(AutonomyOutcome.Automated, ReasonNotEnforceable, enforcedOverride: false);
        }

        if (!enabled)
        {
            return Decision(AutonomyOutcome.Denied, ReasonDisabled);
        }

        if (allowedRoles is { Count: > 0 }
            && (query.Role is null || !allowedRoles.Contains(query.Role, StringComparer.Ordinal)))
        {
            return Decision(AutonomyOutcome.Denied, ReasonRoleNotAllowed);
        }

        // THE v1 dial semantics: automated iff dial >= MinAutonomy.
        // AlwaysHuman is strictly above Max, so it blocks at every valid dial
        // position with no special case.
        if (dial >= effectiveMin)
        {
            return Decision(AutonomyOutcome.Automated, ReasonAutomated);
        }

        // Below the threshold: a person decides where a human wait can exist;
        // a non-escalatable target (every automation:* member) can only be
        // denied — there is nobody on that path to wait for (Seam D).
        var outcome = descriptor.EscalatableToHuman
            ? AutonomyOutcome.RequiresHuman
            : AutonomyOutcome.Denied;
        return Decision(
            outcome,
            effectiveMin == AutonomyDial.AlwaysHuman ? ReasonAlwaysHuman : ReasonBelowMinAutonomy);

        AutonomyDecision Decision(AutonomyOutcome o, string reason, bool? enforcedOverride = null) =>
            new(o, descriptor.Key, descriptor.Group, descriptor.Risk,
                dial, effectiveMin, source,
                enforcedOverride ?? enforced, enabled, allowedRoles, reason);
    }

    /// <summary>
    /// The threshold ladder WITHOUT the legacy floor — the piece the Seam B
    /// tool-loop gate reads synchronously (tool-plane keys structurally cannot
    /// carry a legacy floor: <see cref="Documents.Policy.EscalationClassKind"/>
    /// has only document-type and agent-action members): the principal ladder
    /// (action row → group row → shipped default, <c>??</c>) composed with the
    /// platform ceiling (action → group → none) by <c>max()</c>. Provenance
    /// flips to <see cref="ActionAssignmentSource.PlatformCeiling"/> only when
    /// the ceiling actually raised the value — a tenant row trying to go below
    /// the ceiling resolves to the ceiling with ceiling provenance.
    /// </summary>
    public static (int EffectiveMinAutonomy, ActionAssignmentSource Source)
        ResolveEffectiveMinAutonomy(ActionDescriptor descriptor, GovernancePolicySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(snapshot);

        var actionWire = descriptor.Key.ToWire();
        var groupWire = descriptor.Group.ToWire();

        var principalAction = Row(snapshot.PrincipalActionRows, actionWire);
        var principalGroup = Row(snapshot.PrincipalGroupRows, groupWire);
        var platformAction = Row(snapshot.PlatformActionRows, actionWire);
        var platformGroup = Row(snapshot.PlatformGroupRows, groupWire);

        // Principal ladder: ?? — an action override beats its group outright.
        var (effectiveMin, source) =
            principalAction?.MinAutonomy is int pa
                ? (pa, ActionAssignmentSource.ActionOverride)
                : principalGroup?.MinAutonomy is int pg
                    ? (pg, ActionAssignmentSource.GroupOverride)
                    : (descriptor.DefaultMinAutonomy, ActionAssignmentSource.SystemDefault);

        // Platform ceiling: composes by max() — it can only raise.
        var ceiling = platformAction?.MinAutonomy ?? platformGroup?.MinAutonomy;
        if (ceiling is int c && c > effectiveMin)
        {
            (effectiveMin, source) = (c, ActionAssignmentSource.PlatformCeiling);
        }

        return (effectiveMin, source);
    }

    /// <summary>
    /// The D8 bridge — TRUE iff the principal's legacy
    /// <see cref="AcceptanceRules.AlwaysEscalate"/> list pins this key to a
    /// person, decided by <see cref="AcceptanceGuardrails.TryPreGate"/> itself
    /// (its first production call site). The rules are pre-filtered to the
    /// classes matching this key so (a) a match is by construction about this
    /// key and (b) an unrelated document-type class can never false-match the
    /// synthetic context. Rounds state is pinned to zero AND the escalation
    /// reason is checked, so the rounds-exhausted short-circuit inside
    /// <c>TryPreGate</c> can never leak into the threshold (AC9's
    /// <c>RoundsExhausted_DoesNotAffectActionThreshold</c>).
    /// </summary>
    internal static bool LegacyAlwaysEscalates(ActionKey action, AcceptanceRules rules)
    {
        if (action.Ns is not (ActionNamespace.AgentAction or ActionNamespace.DocumentType))
        {
            return false; // EscalationClassKind has no other planes.
        }

        var matching = rules.AlwaysEscalate
            .Where(cls => Matches(cls, action))
            .ToArray();
        if (matching.Length == 0)
        {
            return false;
        }

        DocumentTypeKey documentType = default;
        string? agentActionWire = null;
        if (action.Ns == ActionNamespace.DocumentType)
        {
            if (!DocumentTypeKeyExtensions.TryParse(action.Key, out documentType))
            {
                return false; // not a real document type — no legacy floor
            }
        }
        else
        {
            agentActionWire = action.Key;
        }

        var ctx = new AcceptanceGateContext(
            DocumentType: documentType,
            AgentActionWire: agentActionWire,
            Review: new ReviewFacts(Documents.Types.ReviewDecision.Approve, HasBlockingIssues: false),
            RoundsUsed: 0,
            Rules: rules with { AlwaysEscalate = matching },
            DeciderChannel: ApprovalChannel.Orchestrator);

        return AcceptanceGuardrails.TryPreGate(ctx, out var escalation)
            && escalation.Reason == AcceptanceEscalationReason.AlwaysEscalateClass;
    }

    private static bool Matches(EscalationClass cls, ActionKey action) => cls.Kind switch
    {
        EscalationClassKind.DocumentType =>
            action.Ns == ActionNamespace.DocumentType
                && string.Equals(cls.Key, action.Key, StringComparison.Ordinal),
        EscalationClassKind.AgentAction =>
            action.Ns == ActionNamespace.AgentAction
                && string.Equals(cls.Key, action.Key, StringComparison.Ordinal),
        _ => false,
    };

    private static ActionAssignmentValue? Row(
        IReadOnlyDictionary<string, ActionAssignmentValue> rows, string key) =>
        rows.TryGetValue(key, out var value) ? value : null;
}
