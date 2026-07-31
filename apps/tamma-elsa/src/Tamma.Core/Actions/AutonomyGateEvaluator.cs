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
///
/// <para><b>EVERY cross-plane field composes MONOTONELY (F10 close,
/// 2026-07-30).</b> The threshold has always been <c>max()</c> and
/// <c>Enabled</c> has always been <c>AND</c>; <c>Enforce</c> and
/// <c>AllowedRoles</c> used to be "one plane wins outright", which meant a
/// platform <c>Enforce=false</c> would OVERRIDE a principal's <c>true</c> and a
/// principal roles list would WIDEN a platform restriction — the platform
/// ceiling LOOSENING, the exact inverse of what a ceiling is. They are now
/// <c>OR</c> (either plane asking for enforcement gets it) and INTERSECTION
/// (both restrictions apply). The invariant, which no future ceiling endpoint
/// may break: <b>adding a row on either plane can only make the resolution more
/// restrictive, never less.</b> The only value a plane may lower is the SHIPPED
/// default when no other plane has an opinion.</para>
///
/// <para><b>A degraded input FAILS CLOSED (F6 close, 2026-07-30).</b> A
/// non-authoritative <see cref="GovernancePolicySnapshot"/> (never loaded) or a
/// null <c>baseRules</c> (the read threw) resolves to
/// <see cref="AutonomyDial.AlwaysHuman"/> with
/// <see cref="ActionAssignmentSource.Unavailable"/> provenance and
/// <c>Enforced = true</c>. Rationale: every governance input here can only
/// TIGHTEN (a platform ceiling, a legacy always-escalate floor, a disable), so
/// failing to read one cannot be answered with "then there is none" —
/// <b>ignorance is not absence</b>. The two degraded causes carry DIFFERENT
/// reasons, and both are distinct from a successful read that found nothing
/// (which stays <see cref="ActionAssignmentSource.SystemDefault"/> and is
/// perfectly automatable).</para>
///
/// <para><b>The two carve-outs from fail-closed still carry degraded
/// PROVENANCE (review 2.1, 2026-07-30).</b> An uncatalogued key (epic D2) and a
/// non-enforceable member (epic OQ2) stay <see cref="AutonomyOutcome.Automated"/>
/// under degradation — that is deliberate and unchanged — but the decision is
/// stamped <see cref="ActionAssignmentSource.Unavailable"/> so it is not
/// indistinguishable from a healthy shipped-default allow. That stamp is what
/// carries it past <c>ActionGateEventsService</c>'s <c>.ALLOWED</c> volume gate
/// (which suppresses <see cref="ActionAssignmentSource.SystemDefault"/>
/// allows) and sets the <c>degraded</c> audit tag. During a control-plane outage
/// the carve-outs are the surfaces that STAY OPEN, which is exactly why they
/// need an audit row.</para>
///
/// <para><b>BREAK-GLASS bypasses DEGRADATION ONLY — never a real denial (F11
/// close, 2026-07-30).</b> An engaged <see cref="BreakGlassState"/> suspends the
/// <see cref="AutonomyDial.AlwaysHuman"/> substitution that a degraded input
/// triggers, and nothing else. Concretely, everything below still bites while it
/// is engaged:
/// <list type="bullet">
/// <item>a resolved <c>Enabled = false</c> still DENIES (checked above the
/// degradation branch, deliberately);</item>
/// <item>a resolved <c>AllowedRoles</c> restriction still DENIES;</item>
/// <item>a threshold that came from a SUCCESSFULLY-READ row still applies — in
/// the acceptance-rules-degraded case the rows WERE read, so the ladder's answer
/// is kept and only the unreadable legacy floor is skipped;</item>
/// <item>a shipped default that blocks still blocks — in the snapshot-degraded
/// case there are provably no rows to keep (<see cref="GovernancePolicySnapshot.Unavailable"/>
/// carries none), so the fallback is <see cref="ActionDescriptor.DefaultMinAutonomy"/>,
/// which for e.g. <c>document-type:design</c> or <c>effect:mcp.tool.invoke</c> is
/// itself <see cref="AutonomyDial.AlwaysHuman"/>.</item>
/// </list>
/// So the override can only ever restore the answer the system would have given
/// had the unreadable input said "nothing" — it can never produce an answer more
/// permissive than a healthy read of the rows it could not see would have
/// allowed at their most permissive.</para>
///
/// <para><b>PROVENANCE follows the same boundary, and did not used to (review
/// MEDIUM-1, 2026-07-31).</b>
/// <see cref="ActionAssignmentSource.BreakGlass"/> is stamped at exactly ONE
/// return — the automated one reached with the bypass in force — so it marks
/// decisions the override PERMITTED and nothing else. Every decision the
/// override did not decide keeps the provenance of whatever DID decide it: a
/// disabled row reports the plane that disabled, a role mismatch reports the
/// plane whose restriction excluded, a platform ceiling reports
/// <see cref="ActionAssignmentSource.PlatformCeiling"/>, a shipped default
/// resolved over an unreadable snapshot reports
/// <see cref="ActionAssignmentSource.Unavailable"/>. This matters beyond
/// tidiness: the <c>breakGlass</c> audit tag and the dedicated
/// <c>ACTION.GATE.BREAK_GLASS_BYPASS</c> row are gated on this provenance, so
/// the old blanket stamp emitted bypass rows for denials the override had never
/// touched — and, because that append deliberately does not swallow, could fail
/// the whole evaluation over one.</para>
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

    /// <summary>Fail-closed: the assignment snapshot has never loaded, so the
    /// absence of a ceiling/disable row proves nothing (F6).</summary>
    public const string ReasonPolicySnapshotUnavailable = "policy-snapshot-unavailable";

    /// <summary>Fail-closed: the principal's base acceptance rules could not be
    /// read, so a legacy always-escalate floor cannot be ruled out (F6).</summary>
    public const string ReasonAcceptanceRulesUnavailable = "acceptance-rules-unavailable";

    /// <summary>
    /// The fail-closed substitution was SUSPENDED by an engaged break-glass
    /// override (F11) and the decision came out automated. It appears ONLY where
    /// the decision would otherwise have been the degraded block — never on a
    /// denial that a successfully-read policy row produced, which keeps its own
    /// reason.
    /// </summary>
    public const string ReasonBreakGlassBypass = "break-glass-bypass";

    /// <summary>
    /// Evaluate one action against a policy snapshot and the principal's
    /// resolved BASE acceptance rules (the dial + the legacy always-escalate
    /// list — Story 43-5 AC11's <c>ResolveBase*Async</c> provides it).
    /// </summary>
    /// <param name="baseRules">
    /// The principal's resolved base rules, or <c>NULL</c> meaning <b>the read
    /// FAILED</b> (F6). Null is not "no rules" — a principal with no override
    /// rows resolves to a NON-null <see cref="ResolvedAcceptanceRules"/> carrying
    /// the shipped defaults. A caller must never substitute the shipped defaults
    /// for a failed read: that is precisely the fail-open this parameter exists
    /// to make unrepresentable.
    /// </param>
    /// <param name="breakGlass">
    /// The operator's break-glass override (F11), or null / not-engaged for the
    /// ordinary posture. When engaged it suspends the fail-closed substitution
    /// for an UNREADABLE input and NOTHING else — see the type doc.
    /// </param>
    public static AutonomyDecision Evaluate(
        AutonomyQuery query,
        GovernancePolicySnapshot snapshot,
        ResolvedAcceptanceRules? baseRules,
        BreakGlassState? breakGlass = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(snapshot);

        // The most conservative dial when the rules are unreadable: the dial is
        // "how much the system may decide itself", so the LOWEST valid position
        // is the safe assumption. (Dial degrade was already safe; it is the
        // FLOOR loss that made the old fallback dangerous.)
        var dial = baseRules?.Rules.AutonomyLevel ?? AutonomyDial.Min;

        // ── Degradation (F6): WHICH input we could not read, if any. Checked in
        //    a fixed order so the audit reason is deterministic when both are
        //    degraded at once (the snapshot is the outer, cheaper failure).
        //
        //    Computed BEFORE the uncatalogued short-circuit (review 2.1,
        //    2026-07-30). It used to be computed after, so an uncatalogued key
        //    evaluated during an outage came out with SystemDefault provenance —
        //    which the `.ALLOWED` volume gate then suppressed entirely, and whose
        //    `degraded` tag (keyed on Unavailable provenance) would have read
        //    false anyway. The uncatalogued surface is precisely the surface that
        //    STAYS OPEN during a control-plane outage, so it is precisely the
        //    surface an auditor needs a record of.
        var degradedReason =
            !snapshot.IsAuthoritative ? ReasonPolicySnapshotUnavailable
            : baseRules is null ? ReasonAcceptanceRulesUnavailable
            : null;

        // F11 — the break-glass override applies ONLY where there is degradation
        // to bypass. `bypassing` is false on every healthy evaluation, so an
        // engaged override changes nothing at all while the control plane is up.
        var bypassing = degradedReason is not null && breakGlass is { IsEngaged: true };

        if (!ActionCatalog.TryGet(query.Action, out var descriptor) || descriptor is null)
        {
            // Epic decision D2 — unclassified is allowed at RUNTIME (and
            // unmergeable in CI via the drift harnesses); a catalog gap must
            // never stall a live workflow. Not silent: callers log/audit it.
            //
            // The OUTCOME is unchanged under degradation (still Automated, still
            // observe-only, still reason `uncatalogued` — an unread policy table
            // does not create a catalog entry, and D2 stands). Only the
            // PROVENANCE changes: `Unavailable` records that this allow was
            // decided over an unreadable policy input, which is also what carries
            // it past the volume gate into the audit stream with `degraded=true`.
            //
            // Break-glass (F11) is deliberately NOT stamped here even when
            // engaged: this allow was never going to be blocked, so the override
            // bypassed nothing. `BreakGlass` provenance means "the fail-closed
            // path was suspended for THIS decision" and must stay narrow enough
            // to be worth auditing.
            return new AutonomyDecision(
                AutonomyOutcome.Automated, query.Action, default, default,
                dial, AutonomyDial.Min,
                degradedReason is null
                    ? ActionAssignmentSource.SystemDefault
                    : ActionAssignmentSource.Unavailable,
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
        var (effectiveMin, source) = ResolveEffectiveMinAutonomy(descriptor, snapshot, breakGlass);

        // F11, corrected 2026-07-31 (review MEDIUM-1). `ResolveEffectiveMinAutonomy`
        // reports `BreakGlass` to mean "the fail-closed SUBSTITUTION was suspended
        // and the SHIPPED DEFAULT was used instead" — a fact about the
        // RESOLUTION, not yet about the decision. Whether the override actually
        // PERMITTED anything is only knowable at the outcome, so the provenance is
        // carried honestly here (the value in hand was resolved over an unreadable
        // snapshot ⇒ `Unavailable`, which is what keeps the `degraded` audit tag
        // true) and `BreakGlass` is stamped at the ONE return where the bypass
        // decided the answer. Before this, every guard below inherited
        // `BreakGlass` and a disabled row / role mismatch / platform ceiling came
        // out labelled as a bypass, emitting a spurious BREAK_GLASS_BYPASS row for
        // a denial the override had nothing to do with.
        if (source == ActionAssignmentSource.BreakGlass)
        {
            source = ActionAssignmentSource.Unavailable;
        }

        if (baseRules is null && !bypassing)
        {
            // The base-rules read FAILED. The legacy always-escalate floor lives
            // in that body, and it can only RAISE — so "I could not read it"
            // must resolve the same way "it says always-escalate" does. The old
            // code substituted AcceptanceDefaults.Rules here, which has an EMPTY
            // AlwaysEscalate list, and thereby concluded "no floor" from a
            // failure (F6): triage-intake, which ships at Min with a live legacy
            // floor, became AUTOMATED on a blip.
            (effectiveMin, source) = (AutonomyDial.AlwaysHuman, ActionAssignmentSource.Unavailable);
        }
        // else if (baseRules is null) — BREAK-GLASS, acceptance-rules half (F11).
        // Deliberately NO branch: the ASSIGNMENT ROWS were read successfully here
        // (degradedReason is checked in a fixed order — a non-authoritative
        // snapshot wins, so reaching here with null baseRules means the snapshot
        // IS authoritative), so `effectiveMin` AND its provenance are a REAL,
        // successfully-read resolution and are kept verbatim. The override skips
        // the unreadable LEGACY FLOOR and nothing else. If that real resolution
        // blocks, it still blocks, and it is attributed to the ROW that blocked it
        // — this is the anti-backdoor boundary, and stamping `BreakGlass` here (as
        // the code did until review MEDIUM-1, 2026-07-31) both invented a bypass
        // that had not happened and destroyed the real provenance.
        else if (baseRules is not null
            && LegacyAlwaysEscalates(descriptor.Key, baseRules.Rules)
            && AutonomyDial.AlwaysHuman > effectiveMin)
        {
            // Legacy always-escalate floor (agent-action / document-type planes
            // only — EscalationClassKind has no other members). Strictly-greater:
            // it can only raise, never relabel an already-AlwaysHuman resolution.
            (effectiveMin, source) =
                (AutonomyDial.AlwaysHuman, ActionAssignmentSource.AlwaysEscalateLegacy);
        }

        // ── Per-field ladders (independent — never inherited from the
        //    threshold row's NULLs), each MONOTONE across planes (F10) ────────
        // Enforce: OR — either plane asking for enforcement gets it. A platform
        // ceiling must never be able to switch a principal's enforcement OFF
        // (the pre-F10 "platform wins when present" did exactly that). When
        // NEITHER plane has an opinion the v1 default is TRUE (epic D1).
        var platformEnforce = platformAction?.Enforce ?? platformGroup?.Enforce;
        var principalEnforce = principalAction?.Enforce ?? principalGroup?.Enforce;
        var enforced = (platformEnforce, principalEnforce) switch
        {
            (null, null) => true,                 // epic D1 — v1 enforces
            (bool p, null) => p,
            (null, bool q) => q,
            (bool p, bool q) => p || q,           // monotone: neither plane may un-enforce the other
        };

        // Enabled: monotone like the threshold — EITHER plane resolving FALSE
        // disables (a tenant cannot re-enable a platform-disabled action, and
        // a platform row without an opinion leaves the tenant's disable alone).
        var platformEnabled = platformAction?.Enabled ?? platformGroup?.Enabled ?? true;
        var principalEnabled = principalAction?.Enabled ?? principalGroup?.Enabled ?? true;
        var enabled = platformEnabled && principalEnabled;

        // AllowedRoles: INTERSECTION — a restriction on either plane applies, so
        // a principal allowlist can only narrow a platform one, never widen it
        // (the pre-F10 "principal wins outright" let a tenant list ADD roles the
        // platform had excluded). An intersection that comes out EMPTY is still
        // a restriction — it allows nobody — which is why the guard below tests
        // for null rather than for Count.
        var allowedRoles = IntersectRestrictions(
            principalAction?.AllowedRoles ?? principalGroup?.AllowedRoles,
            platformAction?.AllowedRoles ?? platformGroup?.AllowedRoles);

        // A member the gate may never block on (effect:secret.reveal is the
        // only shipped one): informational only, whatever the rows say — and
        // that holds under degradation too. Turning a credential fetch into a
        // human gate during a control-plane blip would amplify the outage, not
        // make anything safer (epic OQ2: reading a secret never needs a human).
        //
        // BREAK-GLASS (F11) is deliberately NOT stamped here even when engaged,
        // for exactly the reason already given at the uncatalogued short-circuit
        // above: this allow was never going to be blocked, so the override
        // bypassed nothing (review MEDIUM-2, 2026-07-31 — the identical reasoning
        // had simply never been applied to this carve-out, so a not-enforceable
        // allow came back stamped `BreakGlass` and drove the non-swallowing bypass
        // append, which could fail the whole evaluation over a decision nothing
        // had bypassed). The DEGRADED provenance is kept, matching the
        // uncatalogued carve-out: an allow decided over an unreadable input is
        // still the surface an auditor needs a record of.
        if (!descriptor.Enforceable)
        {
            return Decision(
                AutonomyOutcome.Automated, ReasonNotEnforceable, enforcedOverride: false,
                sourceOverride: degradedReason is null
                    ? source
                    : ActionAssignmentSource.Unavailable);
        }

        if (!enabled)
        {
            // Strictly more restrictive than the degraded outcome below, so it
            // legitimately wins when both apply. It stamps the ROW that disabled
            // (review MEDIUM-1): a denial must name what denied it, and inheriting
            // the threshold ladder's provenance made a disabled row unattributable
            // — and, while break-glass was engaged, made it look like a bypass.
            return Decision(
                AutonomyOutcome.Denied, ReasonDisabled, sourceOverride: DisabledSource());
        }

        if (allowedRoles is not null
            && (query.Role is null || !allowedRoles.Contains(query.Role, StringComparer.Ordinal)))
        {
            return Decision(
                AutonomyOutcome.Denied, ReasonRoleNotAllowed,
                sourceOverride: RoleRestrictionSource());
        }

        if (degradedReason is not null && !bypassing)
        {
            // FAIL CLOSED. Enforced is forced TRUE: a degraded decision that a
            // seam is free to ignore is the fail-open hole wearing a warning
            // label. The distinct reason + Unavailable provenance are what make
            // this distinguishable in the audit stream from a successful read
            // that found nothing.
            return Decision(
                descriptor.EscalatableToHuman
                    ? AutonomyOutcome.RequiresHuman
                    : AutonomyOutcome.Denied,
                degradedReason,
                enforcedOverride: true);
        }

        // BREAK-GLASS falls THROUGH to the ordinary dial comparison below rather
        // than returning an allow (F11). That is the whole anti-backdoor design:
        // the two denials above (Enabled=false, AllowedRoles) have already had
        // their say, and the threshold applied below is a real one — the
        // successfully-read ladder in the acceptance-rules-degraded case, the
        // shipped default in the snapshot-degraded case (where there are provably
        // no rows). An action pinned above the dial by either is STILL blocked.

        // THE v1 dial semantics: automated iff dial >= MinAutonomy.
        // AlwaysHuman is strictly above Max, so it blocks at every valid dial
        // position with no special case.
        if (dial >= effectiveMin)
        {
            // THE ONE PLACE `BreakGlass` provenance is stamped (F11, narrowed by
            // review MEDIUM-1 on 2026-07-31). Reaching here with `bypassing` true
            // means the decision came out AUTOMATED and, without the override,
            // would have been the degraded block — i.e. the override actually
            // permitted this. Every other return above and below is a decision the
            // override did not decide, and must not be labelled as one: the
            // `breakGlass` audit tag and the dedicated BREAK_GLASS_BYPASS row are
            // gated on this provenance, and a dashboard selecting on it must get
            // exactly the set of things the operator's lever let through.
            return Decision(
                AutonomyOutcome.Automated,
                bypassing ? ReasonBreakGlassBypass : ReasonAutomated,
                sourceOverride: bypassing ? ActionAssignmentSource.BreakGlass : null);
        }

        // Below the threshold: a person decides where a human wait can exist;
        // a non-escalatable target (every automation:* member) can only be
        // denied — there is nobody on that path to wait for (Seam D). The
        // provenance is the THRESHOLD's own (`source`), so a platform ceiling
        // reports `PlatformCeiling` and a shipped default resolved over an
        // unreadable snapshot reports `Unavailable` — never `BreakGlass`, because
        // a block is precisely what the override did not achieve.
        var outcome = descriptor.EscalatableToHuman
            ? AutonomyOutcome.RequiresHuman
            : AutonomyOutcome.Denied;
        return Decision(
            outcome,
            effectiveMin == AutonomyDial.AlwaysHuman ? ReasonAlwaysHuman : ReasonBelowMinAutonomy);

        AutonomyDecision Decision(
            AutonomyOutcome o, string reason,
            bool? enforcedOverride = null,
            ActionAssignmentSource? sourceOverride = null) =>
            new(o, descriptor.Key, descriptor.Group, descriptor.Risk,
                dial, effectiveMin, sourceOverride ?? source,
                enforcedOverride ?? enforced, enabled, allowedRoles, reason);

        // Which plane's row resolved Enabled to FALSE. The PLATFORM plane is
        // reported first when both disable: it is the one a tenant admin cannot
        // undo, so it is the one an operator reading the audit row needs to see.
        // Mirrors the `??` masking of the resolution above — a platform action row
        // saying `true` hides its group's `false`, and so does this.
        ActionAssignmentSource DisabledSource() =>
            (platformAction?.Enabled ?? platformGroup?.Enabled) is false
                ? ActionAssignmentSource.PlatformCeiling
                : principalAction?.Enabled is not null
                    ? ActionAssignmentSource.ActionOverride
                    : ActionAssignmentSource.GroupOverride;

        // Which plane's role restriction excludes this caller. Platform first for
        // the same reason; an INTERSECTION that excludes everybody without either
        // plane excluding on its own is attributed to the platform half, since
        // that is the half the tenant cannot widen.
        ActionAssignmentSource RoleRestrictionSource()
        {
            var platformRoles = platformAction?.AllowedRoles ?? platformGroup?.AllowedRoles;
            var principalRoles = principalAction?.AllowedRoles ?? principalGroup?.AllowedRoles;
            if (Excludes(platformRoles)) return ActionAssignmentSource.PlatformCeiling;
            if (Excludes(principalRoles))
            {
                return principalAction?.AllowedRoles is not null
                    ? ActionAssignmentSource.ActionOverride
                    : ActionAssignmentSource.GroupOverride;
            }
            return ActionAssignmentSource.PlatformCeiling;

            bool Excludes(IReadOnlyList<string>? restriction) =>
                restriction is { Count: > 0 }
                && (query.Role is null
                    || !restriction.Contains(query.Role, StringComparer.Ordinal));
        }
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
    ///
    /// <para><b>F6:</b> a snapshot that has never loaded
    /// (<see cref="GovernancePolicySnapshot.IsAuthoritative"/> false) resolves
    /// FAIL-CLOSED to <see cref="AutonomyDial.AlwaysHuman"/> with
    /// <see cref="ActionAssignmentSource.Unavailable"/> — not to the shipped
    /// default, because an unread table cannot testify that no ceiling exists.
    /// A LOADED-and-empty table is a completely different answer and still
    /// resolves to the shipped default.</para>
    ///
    /// <para><b>F11:</b> an engaged <paramref name="breakGlass"/> suspends that
    /// substitution and falls back to
    /// <see cref="ActionDescriptor.DefaultMinAutonomy"/> with
    /// <see cref="ActionAssignmentSource.BreakGlass"/> provenance. This CANNOT
    /// discard a stored policy row: a non-authoritative snapshot carries none by
    /// construction (<see cref="GovernancePolicySnapshot.Unavailable"/>), which
    /// is why the bypass is sited inside this branch and not around the whole
    /// method. And it is not automatically permissive — a descriptor whose
    /// shipped default is <see cref="AutonomyDial.AlwaysHuman"/> still blocks.</para>
    /// </summary>
    public static (int EffectiveMinAutonomy, ActionAssignmentSource Source)
        ResolveEffectiveMinAutonomy(
            ActionDescriptor descriptor,
            GovernancePolicySnapshot snapshot,
            BreakGlassState? breakGlass = null)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(snapshot);

        if (!snapshot.IsAuthoritative)
        {
            return breakGlass is { IsEngaged: true }
                ? (descriptor.DefaultMinAutonomy, ActionAssignmentSource.BreakGlass)
                : (AutonomyDial.AlwaysHuman, ActionAssignmentSource.Unavailable);
        }

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

    /// <summary>
    /// Compose two role restrictions MONOTONELY (F10): null/empty means "this
    /// plane imposes no restriction" (the identity), one restriction present
    /// means that one applies, and both present means BOTH apply — the
    /// intersection. The result is never widened by adding a plane.
    ///
    /// <para>A stored EMPTY array keeps its historical reading of "no
    /// restriction" (the pre-F10 <c>Count &gt; 0</c> guard) so no stored row
    /// changes meaning; only an intersection that comes out empty denies
    /// everybody, which is the arithmetically honest answer to "developer only"
    /// AND "tester only".</para>
    /// </summary>
    internal static IReadOnlyList<string>? IntersectRestrictions(
        IReadOnlyList<string>? principal, IReadOnlyList<string>? platform)
    {
        var p = principal is { Count: > 0 } ? principal : null;
        var q = platform is { Count: > 0 } ? platform : null;
        if (p is null) return q;
        if (q is null) return p;
        return p.Where(r => q.Contains(r, StringComparer.Ordinal))
                .ToArray();
    }
}
