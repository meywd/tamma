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
/// all five. A base-rules read failure degrades to the shipped defaults (the
/// <c>AcceptanceRulesEndpoints.ListEffective</c> posture) so a CP/tenant-DB
/// blip cannot stall a gate; with zero override rows that fallback is
/// byte-identical anyway.</para>
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

    private async Task<ResolvedAcceptanceRules> ResolveBaseRulesAsync(
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
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "Base acceptance-rules read failed during gate evaluation; "
                + "degrading to the shipped defaults (byte-identical with zero overrides).");
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
