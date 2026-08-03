using Tamma.Api.Services.Actions;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Agents;

/// <summary>
/// Story 43-14 (Amendment 2-A, D6) — the DENIAL-path ledger consult for Seam B
/// (the tool-loop gate). The sync <see cref="IToolLoopAutonomyGate"/> is untouched
/// (43-9's "the per-tool-call gate must never block on a database" holds for the
/// ALLOW path); this broker is consulted ONLY when that sync gate says
/// <c>Denied</c>, so the hot path never touches the DB.
///
/// <para>It is what makes a high-frequency action (shell per tool-call, tens per
/// run) coverable by ONE correlation-standing ask per run instead of one ask per
/// call:
/// <list type="bullet">
///   <item><description><see cref="TryCoverAsync"/> — a live grant covering the
///   action for this run? A correlation-standing grant covers every call without
///   being consumed; the denied call then proceeds instead of being rejected.</description></item>
///   <item><description><see cref="EnsurePendingAsync"/> — no cover: mint the
///   pending ask, idempotent per (principal, correlation, target) via the open-row
///   unique index, so a loop of N denied shell calls raises exactly ONE pending
///   row (AC5).</description></item>
/// </list></para>
///
/// <para>The principal is resolved the same way the gate service consults grants
/// (<c>IGovernancePrincipalResolver.ResolveAsync(caller: null)</c>), so the row is
/// keyed to the principal the run resolves as.</para>
/// </summary>
public sealed class ToolLoopAuthorizationBroker
{
    private readonly IActionAuthorizationLedger _ledger;
    private readonly IGovernancePrincipalResolver _principals;
    private readonly ILogger<ToolLoopAuthorizationBroker>? _logger;

    public ToolLoopAuthorizationBroker(
        IActionAuthorizationLedger ledger,
        IGovernancePrincipalResolver principals,
        ILogger<ToolLoopAuthorizationBroker>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(principals);
        _ledger = ledger;
        _principals = principals;
        _logger = logger;
    }

    /// <summary>
    /// Is a live grant covering <paramref name="actionKeyWire"/> present for this
    /// run? A correlation-standing grant returns true on every call (never
    /// consumed); a single-use grant returns true once. False when nothing covers
    /// — the caller then mints the pending ask.
    /// </summary>
    public async Task<bool> TryCoverAsync(
        string actionKeyWire, string correlationId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actionKeyWire) || string.IsNullOrWhiteSpace(correlationId))
        {
            return false;
        }

        var principal = await _principals.ResolveAsync(caller: null, ct).ConfigureAwait(false);
        var grant = await _ledger.TryConsumeAsync(
            principal.TenantId, principal.UserId, correlationId, actionKeyWire, ct)
            .ConfigureAwait(false);
        return grant is not null;
    }

    /// <summary>
    /// Idempotently mint the PENDING authorization row a person decides on. Returns
    /// the row id (for the denial message), or null when the ledger is unavailable.
    /// The open-row unique index makes N calls in one run converge on ONE row.
    /// </summary>
    public async Task<Guid?> EnsurePendingAsync(
        string actionKeyWire, string correlationId, int? autonomyLevelAtRequest,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(actionKeyWire) || string.IsNullOrWhiteSpace(correlationId))
        {
            return null;
        }

        try
        {
            var principal = await _principals.ResolveAsync(caller: null, ct).ConfigureAwait(false);
            var row = await _ledger.RequestAsync(
                principal.TenantId, principal.UserId, correlationId,
                targetKind: "action", targetKey: actionKeyWire,
                reason: "tool-loop autonomy-gate denial (Seam B)",
                autonomyLevelAtRequest: autonomyLevelAtRequest, ttl: null, ct: ct)
                .ConfigureAwait(false);
            return row.Id;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // A ledger blip must not break the tool loop — the denial still stands
            // (the call was rejected), just without an actionable authorization id.
            _logger?.LogWarning(ex,
                "ToolLoopAuthorizationBroker: could not mint the pending authorization for {Action}.",
                actionKeyWire);
            return null;
        }
    }
}
