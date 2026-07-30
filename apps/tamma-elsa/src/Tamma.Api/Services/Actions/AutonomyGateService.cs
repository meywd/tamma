using Tamma.Core.Actions;
using Tamma.Core.Documents.Policy;

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
/// <para>"Read failed" and "read succeeded, no overrides exist" are DIFFERENT
/// answers here: the latter returns a real
/// <see cref="ResolvedAcceptanceRules"/> carrying
/// <see cref="AcceptanceRulesSource.SystemDefault"/> and evaluates normally
/// (zero-config deployments keep automating). Only an exception produces
/// <c>null</c>.</para>
/// </summary>
public sealed class AutonomyGateService : IAutonomyGate
{
    private readonly IGovernancePrincipalResolver _principals;
    private readonly IGovernancePolicySnapshotProvider _snapshots;
    private readonly IAcceptanceRulesResolver _acceptanceRules;
    private readonly ActionGateEventsService _events;
    private readonly ILogger<AutonomyGateService>? _logger;
    private readonly TimeProvider _time;

    public AutonomyGateService(
        IGovernancePrincipalResolver principals,
        IGovernancePolicySnapshotProvider snapshots,
        IAcceptanceRulesResolver acceptanceRules,
        ActionGateEventsService events,
        ILogger<AutonomyGateService>? logger = null,
        TimeProvider? timeProvider = null)
    {
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
        try
        {
            var snapshot = _snapshots.GetSnapshot(principal);
            var baseRules = await ResolveBaseRulesAsync(principal, ct).ConfigureAwait(false);
            decision = AutonomyGateEvaluator.Evaluate(query, snapshot, baseRules);
        }
        catch (Exception ex)
        {
            await _events.EmitEvaluationFailedAsync(
                query.Action.ToWire(), ex.Message, principal.TenantId, principal.UserId)
                .ConfigureAwait(false);
            throw;
        }

        await _events.EmitDecisionAsync(decision, query).ConfigureAwait(false);
        return decision;
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
