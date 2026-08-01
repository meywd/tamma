using Tamma.Core.Actions;
using Tamma.Core.Logging;
using Tamma.Core.Documents.Policy;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Actions;

/// <summary>
/// Story 43-5 — the DB-backed <see cref="IAutonomyGate"/>: composes the
/// principal resolver → the policy snapshot → the principal's resolved BASE
/// acceptance rules (the dial + the legacy always-escalate list, AC11's
/// <c>ResolveBase*Async</c>) → the PURE <see cref="AutonomyGateEvaluator"/> →
/// the audit event family. The Core/Api split copies
/// <c>IAcceptanceRulesResolver</c>/<c>AcceptanceRulesService</c> (39-5 D1).
///
/// <para>Ships with NO production seam caller (43-5 D12) — Story 43-9 adds
/// all five.</para>
///
/// <para><b>FAILURE POSTURE — fail-closed, and loud (43-5 F6 close,
/// 2026-07-30; supersedes the previous "degrade to shipped defaults"
/// behaviour).</b> A base-rules read failure used to substitute
/// <c>AcceptanceDefaults.Rules</c>, whose <c>AlwaysEscalate</c> list is EMPTY —
/// so a blip silently DISCARDED the principal's legacy always-escalate floor
/// and turned a pinned-to-human action into an automated one. Every input this
/// service composes can only TIGHTEN, so a failed read cannot be answered with
/// "then there is nothing"; the read result is now passed to the evaluator as
/// <c>null</c> ("unreadable"), which fails CLOSED at
/// <see cref="AutonomyDial.AlwaysHuman"/> with
/// <see cref="ActionAssignmentSource.Unavailable"/> provenance,
/// <c>Enforced = true</c>, and a reason that names WHICH input was unreadable.
/// The failure is logged at ERROR and — because a degraded decision is never
/// <c>system-default</c> and is always enforced — is guaranteed an audit row
/// (<c>.REQUIRES_HUMAN</c>/<c>.DENIED</c> emission is not swallowed).</para>
///
/// <para><b>WHEN THAT GUARANTEED ROW CANNOT BE WRITTEN (review F2,
/// 2026-08-01).</b> The non-swallowing emission rethrows, and that rethrow used to
/// leave this method as an anonymous exception — which every 43-9 seam's catch-all
/// reads as a transient fault and answers by PROCEEDING. So the exception raised
/// because a denial happened became the reason the denial did not. It is now
/// wrapped in <see cref="AutonomyGateDecisionUnrecordedException"/>, which carries
/// the decision, so a seam re-applies the block instead of guessing. The wrapping
/// is deliberately WIDE: it covers the break-glass row and the decision row, i.e.
/// every audit emission that happens AFTER a decision exists.</para>
///
/// <para>"Read failed" and "read succeeded, no overrides exist" are DIFFERENT
/// answers here: the latter returns a real
/// <see cref="ResolvedAcceptanceRules"/> carrying
/// <see cref="AcceptanceRulesSource.SystemDefault"/> and evaluates normally
/// (zero-config deployments keep automating). Only an exception produces
/// <c>null</c>.</para>
///
/// <para><b>BREAK-GLASS (43-5 F11 close, 2026-07-30).</b> When
/// <see cref="IGovernanceBreakGlass"/> reports an engaged, unexpired override,
/// the fail-closed substitution above is suspended — and ONLY that. A denial
/// produced by a policy row that was read successfully is unaffected. Every
/// decision the override actually lets through is logged at ERROR and written to
/// the audit stream as <c>ACTION.GATE.BREAK_GLASS_BYPASS</c> on the
/// NON-swallowing append path, so an unrecordable bypass fails rather than
/// happening silently.</para>
///
/// <para><b>THE LEDGER CONSULT (Story 43-9 AC12).</b> After the pure evaluation
/// and before the audit row, a <see cref="AutonomyOutcome.RequiresHuman"/>
/// decision is offered to <see cref="IActionAuthorizationLedger.TryConsumeAsync"/>
/// for this <c>(principal, correlationId)</c>. A live grant — action-scoped, or
/// group-scoped covering this member — turns it into
/// <see cref="AutonomyOutcome.Automated"/> stamped with
/// <see cref="AutonomyDecision.AuthorizationId"/> and
/// <see cref="AutonomyDecision.CoveredBy"/>. That is the whole point of the
/// ledger: ONE human decision covers one deploy, not one per retry and not one
/// per seam. Four boundaries, each deliberate:
/// <list type="bullet">
/// <item><b>Only <c>RequiresHuman</c> is offered.</b> A
/// <see cref="AutonomyOutcome.Denied"/> is either a non-escalatable target
/// (nobody could have been asked, so no grant can exist honestly) or a
/// disabled/role-excluded row, which a grant must not be able to override —
/// a grant answers "may the system do this without asking again", never "may
/// this happen at all".</item>
/// <item><b>Only a decision a seam can actually BLOCK on is offered</b> —
/// <see cref="AutonomyQuery.SeamCanBlock"/> AND
/// <see cref="AutonomyDecision.Enforced"/>. Consuming a single-use grant for a
/// resolution that proceeds regardless would burn the person's decision on a
/// seam that was never going to block. Review F4 (2026-08-01): this guard used
/// to read <c>Enforced</c> alone, which under epic D1 defaults TRUE — so Seam A,
/// the observe-only route, reached <c>TryConsumeAsync</c> on every call naming an
/// action with a correlation id, and the real ask that followed found
/// nothing.</item>
/// <item><b>No correlation id, no consult.</b> The ledger is scoped by
/// correlation by construction; without one there is no run for a decision to
/// cover.</item>
/// <item><b>A ledger failure keeps the block.</b> The catch below does not
/// re-open the gate — an unreadable ledger is ignorance, and ignorance may not
/// be read as a grant (the F6 posture, applied to this input too).</item>
/// </list>
/// </para>
/// </summary>
public sealed class AutonomyGateService : IAutonomyGate
{
    private readonly IGovernancePrincipalResolver _principals;
    private readonly IGovernancePolicySnapshotProvider _snapshots;
    private readonly IAcceptanceRulesResolver _acceptanceRules;
    private readonly ActionGateEventsService _events;
    private readonly IGovernanceBreakGlass? _breakGlass;
    private readonly IActionAuthorizationLedger? _ledger;
    private readonly ILogger<AutonomyGateService>? _logger;
    private readonly TimeProvider _time;

    public AutonomyGateService(
        IGovernancePrincipalResolver principals,
        IGovernancePolicySnapshotProvider snapshots,
        IAcceptanceRulesResolver acceptanceRules,
        ActionGateEventsService events,
        IGovernanceBreakGlass? breakGlass = null,
        ILogger<AutonomyGateService>? logger = null,
        TimeProvider? timeProvider = null,
        IActionAuthorizationLedger? ledger = null)
    {
        _breakGlass = breakGlass;
        ArgumentNullException.ThrowIfNull(principals);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(acceptanceRules);
        ArgumentNullException.ThrowIfNull(events);
        _principals = principals;
        _snapshots = snapshots;
        _acceptanceRules = acceptanceRules;
        _events = events;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        // NULLABLE by registration, not by preference: the ledger is registered
        // only when a control-plane DbContext factory is wired
        // (ActionCatalogGovernanceServiceCollectionExtensions), exactly like the
        // assignment repository. A host without one has no grants to consult and
        // every RequiresHuman simply stays RequiresHuman — fail-closed.
        _ledger = ledger;
    }

    /// <inheritdoc />
    public async Task<AutonomyDecision> EvaluateAsync(
        AutonomyQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var principal = query.Principal;
        if (principal.IsPlatformOnly)
        {
            // Callers that did not pre-resolve pass GovernancePrincipal.Platform;
            // resolve the real ambient principal here so a seam cannot forget.
            // (A genuine SaaS-without-tenant resolution comes back platform-only
            // again, with the PRINCIPAL_UNRESOLVED event emitted by the resolver.)
            principal = await _principals.ResolveAsync(caller: null, ct).ConfigureAwait(false);
            query = query with { Principal = principal };
        }

        AutonomyDecision decision;
        var breakGlass = _breakGlass?.Current() ?? BreakGlassState.NotEngaged;
        try
        {
            var snapshot = _snapshots.GetSnapshot(principal);
            var baseRules = await ResolveBaseRulesAsync(principal, ct).ConfigureAwait(false);
            decision = AutonomyGateEvaluator.Evaluate(query, snapshot, baseRules, breakGlass);
        }
        catch (Exception ex)
        {
            await _events.EmitEvaluationFailedAsync(
                query.Action.ToWire(), ex.Message, principal.TenantId, principal.UserId)
                .ConfigureAwait(false);
            throw;
        }

        // F11 — a bypassed decision is loud AND audited, per decision. The
        // dedicated row goes first and on the non-swallowing path: if it cannot
        // be written, the bypass does not happen quietly.
        if (decision.Source == ActionAssignmentSource.BreakGlass)
        {
            _logger?.LogError(
                "GOVERNANCE BREAK-GLASS BYPASS: the autonomy gate did NOT fail closed for "
                + "{ActionKey} because the break-glass override is engaged until {ExpiresAt:O} "
                + "(reason: {Reason}). Outcome={Outcome}, EffectiveMinAutonomy={EffectiveMin}, "
                + "Dial={Dial}.",
                decision.Action.ToWire(), breakGlass.ExpiresAtUtc, breakGlass.ReasonOrUnspecified,
                decision.Outcome, decision.EffectiveMinAutonomy, decision.AutonomyLevel);

            await RecordAsync(
                decision,
                () => _events.EmitBreakGlassBypassAsync(
                    decision.Action.ToWire(),
                    decision.Group.ToWire(),
                    breakGlass,
                    seam: "autonomy-gate",
                    outcome: decision.Outcome.ToString().ToLowerInvariant(),
                    autonomyLevel: decision.AutonomyLevel,
                    effectiveMinAutonomy: decision.EffectiveMinAutonomy,
                    tenantId: principal.TenantId,
                    userId: principal.UserId,
                    correlationId: query.CorrelationId,
                    degradedReason: decision.Reason))
                .ConfigureAwait(false);
        }

        decision = await ConsultLedgerAsync(decision, query, principal, ct).ConfigureAwait(false);

        var recorded = decision;
        await RecordAsync(recorded, () => _events.EmitDecisionAsync(recorded, query))
            .ConfigureAwait(false);
        return decision;
    }

    /// <summary>
    /// Adversarial review F2 (2026-08-01) — run one NON-SWALLOWING audit emission
    /// for a decision that has already been MADE, and re-label its failure so a
    /// seam cannot mistake it for "the gate could not decide".
    ///
    /// <para><see cref="ActionGateEventsService"/> deliberately rethrows the append
    /// failure for an enforced denial/escalation (43-5 AC13: a block with no audit
    /// row is a compliance hole). That rethrow used to arrive at each 43-9 seam's
    /// catch-all as an anonymous <see cref="Exception"/>, and every one of those
    /// catch-alls fails OPEN on the stated posture "deny on a DECISION, never on an
    /// ERROR" — so the exception raised BECAUSE a denial happened was read as
    /// evidence that no denial happened, and the request proceeded. The identical
    /// append is fail-CLOSED at the tool-loop seam (a throw aborts the call) and
    /// was fail-OPEN at the three seams this wave added; nothing noticed the
    /// polarity flip.</para>
    ///
    /// <para>The exception still PROPAGATES — a caller that ignores it keeps the
    /// tool-loop's fail-closed behaviour — but it now carries the decision, so a
    /// seam that can act re-applies the block instead of guessing.</para>
    /// </summary>
    private async Task RecordAsync(AutonomyDecision decision, Func<Task> emit)
    {
        try
        {
            await emit().ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (AutonomyGateDecisionUnrecordedException) { throw; }
        catch (Exception ex)
        {
            _logger?.LogError(ex,
                "The autonomy gate decided {Outcome} for {ActionKey} (enforced={Enforced}, "
                + "source={Source}) but the audit row could NOT be written. The decision STANDS: "
                + "an unrecordable block is still a block, never a transient fault.",
                decision.Outcome, decision.Action.ToWire(), decision.Enforced, decision.Source);
            throw new AutonomyGateDecisionUnrecordedException(decision, ex);
        }
    }

    /// <summary>
    /// AC12 — one human decision covers one correlation. Returns the decision
    /// unchanged unless a live grant covering this action was consumed, in which
    /// case it comes back <see cref="AutonomyOutcome.Automated"/> carrying the
    /// grant's id and target. See the class doc for why only a BLOCKABLE, ENFORCED
    /// <see cref="AutonomyOutcome.RequiresHuman"/> with a correlation id is
    /// offered, and why a ledger failure keeps the block.
    /// </summary>
    private async Task<AutonomyDecision> ConsultLedgerAsync(
        AutonomyDecision decision,
        AutonomyQuery query,
        GovernancePrincipal principal,
        CancellationToken ct)
    {
        // F4 (2026-08-01) — BOTH halves of "this ask can actually block":
        // `SeamCanBlock` is the CALLER's capability, `Enforced` is the ADMIN's
        // instruction. Gating on `Enforced` alone let Seam A — which never blocks
        // in any version — burn single-use grants, because under epic D1 enforce
        // DEFAULTS to true.
        if (_ledger is null
            || decision.Outcome != AutonomyOutcome.RequiresHuman
            || !query.SeamCanBlock
            || !decision.Enforced
            || string.IsNullOrWhiteSpace(query.CorrelationId))
        {
            return decision;
        }

        Tamma.Data.Entities.ActionAuthorization? grant;
        try
        {
            grant = await _ledger.TryConsumeAsync(
                principal.TenantId, principal.UserId, query.CorrelationId!,
                decision.Action.ToWire(), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // NOT a re-open. "I could not read the grant table" is not "there is
            // a grant" — the decision keeps its block and the failure is loud.
            _logger?.LogError(ex,
                "Authorization-ledger consult FAILED for {ActionKey} (correlation {CorrelationId}); "
                + "the requires-human decision STANDS.",
                // Caller-supplied, and this line asserts a block STOOD — a forged copy
                // is a false governance record, so it goes through LogSanitizer like
                // every other user-controlled value the API logs.
                decision.Action.ToWire(), LogSanitizer.Clean(query.CorrelationId));
            await _events.EmitEvaluationFailedAsync(
                decision.Action.ToWire(), ex.Message, principal.TenantId, principal.UserId)
                .ConfigureAwait(false);
            return decision;
        }

        if (grant is null) return decision;

        // `group:` is prefixed so an auditor can tell a group grant from an
        // action grant at a glance; an action-scoped target is already a fully
        // qualified `ns:key` wire and needs no prefix.
        var coveredBy = string.Equals(grant.TargetKind, "group", StringComparison.Ordinal)
            ? $"group:{grant.TargetKey}"
            : grant.TargetKey;

        await _events.EmitAuthorizedAsync(
            principal.TenantId, principal.UserId,
            decision.Action.ToWire(), query.CorrelationId!, grant.Id)
            .ConfigureAwait(false);

        return decision with
        {
            Outcome = AutonomyOutcome.Automated,
            Reason = AutonomyGateEvaluator.ReasonCoveredByAuthorization,
            AuthorizationId = grant.Id,
            CoveredBy = coveredBy,
        };
    }

    /// <summary>
    /// The principal's base acceptance rules, or <c>NULL</c> meaning the read
    /// FAILED (F6). A platform-only principal has no rules row to read at all —
    /// that is a successful "nothing to read" and returns the shipped base, not
    /// null.
    /// </summary>
    private async Task<ResolvedAcceptanceRules?> ResolveBaseRulesAsync(
        GovernancePrincipal principal, CancellationToken ct)
    {
        try
        {
            if (principal.TenantId is Guid tenantId)
            {
                return await _acceptanceRules.ResolveBaseForTenantAsync(tenantId, ct)
                    .ConfigureAwait(false);
            }
            if (principal.UserId is Guid userId)
            {
                return await _acceptanceRules.ResolveBaseAsync(userId, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // NOT a warning and NOT a silent default: the legacy always-escalate
            // floor lives in the body we just failed to read, and it can only
            // raise. Returning the shipped defaults here would conclude "no
            // floor" from ignorance — the F6 fail-open.
            _logger?.LogError(ex,
                "Base acceptance-rules read FAILED during gate evaluation for "
                + "principal (tenant={TenantId}, user={UserId}); the legacy always-escalate "
                + "floor cannot be ruled out, so this evaluation FAILS CLOSED "
                + "(requires-human / denied).",
                principal.TenantId, principal.UserId);
            return null;
        }
        return SystemDefaultBase();
    }

    private ResolvedAcceptanceRules SystemDefaultBase() => new(
        Rules: AcceptanceDefaults.Rules,
        Source: AcceptanceRulesSource.SystemDefault,
        Version: 1,
        DocumentTypeKey: "base",
        ResolvedAt: _time.GetUtcNow());
}
