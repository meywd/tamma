using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Actions;

/// <summary>
/// Story 43-14 (Amendment 2-B, D3/D7) — mints the correlation-standing grants a
/// workflow's human approval implies, so Seam C honours the human's "yes"
/// instead of 409ing the approved run's next mediated call. Driven entirely by
/// the <see cref="ApprovalChains"/> fixture: given a chain name + the run
/// correlation, it mints one grant per <see cref="ApprovalChains.Chain.MintedTargetKeys"/>
/// entry (all TargetKind=<c>action</c>) and audits the mint (AC8).
///
/// <para>The principal is resolved the SAME way the gate consults grants
/// (<c>IGovernancePrincipalResolver.ResolveAsync(caller: null)</c>) so the minted
/// row is keyed to the principal the downstream mediated calls resolve as — a
/// mismatch would mint grants the consult can never find.</para>
///
/// <para>Grants are LLM-scoped: they cover the gated LLM mediation path only; a
/// human caller never needs one (43-13). A machinery chain mints NOTHING (its
/// minted set is empty) — the seam still exists so the fixture stays load-bearing.</para>
/// </summary>
public sealed class ApprovalGrantMinter
{
    private readonly IActionAuthorizationLedger _ledger;
    private readonly IGovernancePrincipalResolver _principals;
    private readonly ActionGateEventsService _events;
    private readonly ILogger<ApprovalGrantMinter>? _logger;

    public ApprovalGrantMinter(
        IActionAuthorizationLedger ledger,
        IGovernancePrincipalResolver principals,
        ActionGateEventsService events,
        ILogger<ApprovalGrantMinter>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(principals);
        ArgumentNullException.ThrowIfNull(events);
        _ledger = ledger;
        _principals = principals;
        _events = events;
        _logger = logger;
    }

    /// <summary>
    /// Mint the correlation-standing grants for <paramref name="chainName"/> on
    /// <paramref name="correlationId"/> (the run correlation the approved
    /// workflow's mediated calls carry). No-op with a WARN if the chain is
    /// unknown; no-op (but seam exercised) for a machinery chain's empty set.
    /// </summary>
    /// <param name="decidedByUserId">The authenticated approver's user id (audit +
    /// the row's DecidedByUserId). <see cref="Guid.Empty"/> in single-user planes
    /// with no user id.</param>
    /// <param name="approver">The server-derived approver string (audit).</param>
    public async Task MintForChainAsync(
        string chainName,
        string correlationId,
        Guid decidedByUserId,
        string? approver,
        string? workflowInstanceId = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chainName);
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);

        var chain = ApprovalChains.Find(chainName);
        if (chain is null)
        {
            _logger?.LogWarning(
                "ApprovalGrantMinter: unknown chain '{Chain}' — nothing minted.", chainName);
            return;
        }

        if (chain.MintedTargetKeys.Count == 0)
        {
            // Machinery chain (Amendment 4) — the seam is exercised but there is
            // nothing dial-gated to cover.
            _logger?.LogDebug(
                "ApprovalGrantMinter: chain '{Chain}' has an empty gated-target set (machinery) — nothing minted.",
                chainName);
            return;
        }

        var principal = await _principals.ResolveAsync(caller: null, ct).ConfigureAwait(false);

        var reason = $"minted by {chainName} approval (correlation-standing)";
        foreach (var targetKey in chain.MintedTargetKeys)
        {
            await _ledger.MintStandingGrantAsync(
                principal.TenantId, principal.UserId, correlationId,
                targetKind: "action", targetKey: targetKey,
                decidedByUserId: decidedByUserId, reason: reason, ttl: null, ct: ct)
                .ConfigureAwait(false);
        }

        // AC8 — one auditable fact tying the human's "yes" to the grants it made.
        // Swallowing path (D9): the grant ROWS are the durable record.
        await _events.EmitGrantMintedAsync(
            principal.TenantId, principal.UserId,
            chainName, correlationId, workflowInstanceId,
            decidedByUserId, approver, chain.MintedTargetKeys)
            .ConfigureAwait(false);

        _logger?.LogInformation(
            "Minted {Count} correlation-standing grant(s) for chain '{Chain}' on correlation {Correlation}.",
            chain.MintedTargetKeys.Count, chainName, correlationId);
    }
}
