using Microsoft.Extensions.Configuration;
using Tamma.Core.Actions;
using Tamma.Core.Logging;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Actions;

/// <summary>
/// Story 43-9 (AC12(c), AC13) — the REQUEST half of the authorization ledger:
/// when a seam blocks with <see cref="AutonomyOutcome.RequiresHuman"/>, this
/// mints (or re-finds) the <c>pending</c> row a person then decides on at
/// <c>POST /api/actions/authorizations/{id}/decide</c>.
///
/// <para><b>It exists to give <c>Tamma:Governance:AuthorizationTtlHours</c> a
/// reader.</b> Before this story that key appeared only in two doc-comments —
/// <c>ActionAuthorization</c> and <c>EfActionAuthorizationLedger</c> both
/// promised "+24h from <c>Tamma:Governance:AuthorizationTtlHours</c>", and
/// nothing anywhere read it; the ledger's hard-coded 24 h default was the whole
/// implementation. A documented configuration key with no reader is a lie in the
/// operator's mental model, so the TTL is resolved HERE and passed explicitly to
/// <see cref="IActionAuthorizationLedger.RequestAsync"/>, which already takes an
/// optional <c>ttl</c> for exactly this.</para>
///
/// <para><b>Which target is requested — the ACTION, never the group.</b> The
/// ledger's <c>TryConsumeAsync</c> lets a group-scoped grant cover every member,
/// so a group grant is strictly more powerful; a seam must therefore never
/// request one on a person's behalf. A human who wants to authorise a whole
/// group does it deliberately, from the admin surface, and the grant they create
/// is then consumed by every member within that correlation.</para>
///
/// <para><b>Failure is swallowed and returns null.</b> The request row is a
/// convenience for the person, not the block: the block already happened, is
/// already audited on the non-swallowing path, and must not be converted into a
/// 500 because the ledger insert failed. The 409 then carries a null
/// <c>authorizationId</c>, which is honest.</para>
/// </summary>
public interface IActionAuthorizationRequests
{
    /// <summary>The resolved grant lifetime (config, default 24 h).</summary>
    TimeSpan Ttl { get; }

    /// <summary>
    /// Record a pending authorization for <paramref name="decision"/> in
    /// <paramref name="correlationId"/>, returning its id, or null when the
    /// ledger is unavailable or the insert failed.
    /// </summary>
    /// <param name="principal">
    /// The principal the DECISION was resolved for — passed in rather than
    /// re-resolved, so the pending row can never be keyed to a different
    /// principal than the one that was blocked (in single-user mode the human
    /// plane resolves from claims and the engine plane from the sole-user
    /// provider; re-resolving without the caller would silently key some rows to
    /// the wrong one, and the consult would then never find them).
    /// </param>
    Task<Guid?> RequestAsync(
        GovernancePrincipal principal,
        AutonomyDecision decision,
        string correlationId,
        CancellationToken ct = default);
}

/// <inheritdoc />
public sealed class ActionAuthorizationRequests : IActionAuthorizationRequests
{
    /// <summary>The configuration key the ledger's doc-comments already name.</summary>
    public const string TtlConfigKey = "Tamma:Governance:AuthorizationTtlHours";

    /// <summary>The shipped default when the key is absent (AC12).</summary>
    public const double DefaultTtlHours = 24;

    private readonly IActionAuthorizationLedger? _ledger;
    private readonly ILogger<ActionAuthorizationRequests>? _logger;

    public ActionAuthorizationRequests(
        IConfiguration configuration,
        IActionAuthorizationLedger? ledger = null,
        ILogger<ActionAuthorizationRequests>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _ledger = ledger;
        _logger = logger;

        // A non-positive or unparseable value falls back to the shipped default
        // rather than minting grants that expire instantly (or never): a TTL of
        // zero would make every grant unconsumable, which reads as "the decide
        // endpoint is broken" rather than as "the config is wrong".
        var configured = configuration.GetValue<double?>(TtlConfigKey);
        var hours = configured is double h && h > 0 ? h : DefaultTtlHours;
        if (configured is double bad && bad <= 0)
        {
            _logger?.LogWarning(
                "{Key} is {Value}, which is not a usable grant lifetime; using the shipped "
                + "default of {Default} hours.", TtlConfigKey, bad, DefaultTtlHours);
        }
        Ttl = TimeSpan.FromHours(hours);
    }

    /// <inheritdoc />
    public TimeSpan Ttl { get; }

    /// <inheritdoc />
    public async Task<Guid?> RequestAsync(
        GovernancePrincipal principal,
        AutonomyDecision decision,
        string correlationId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(decision);
        if (_ledger is null || string.IsNullOrWhiteSpace(correlationId)) return null;

        try
        {
            var row = await _ledger.RequestAsync(
                principal.TenantId,
                principal.UserId,
                correlationId,
                targetKind: "action",
                targetKey: decision.Action.ToWire(),
                reason: decision.Reason,
                autonomyLevelAtRequest: decision.AutonomyLevel,
                ttl: Ttl,
                ct: ct).ConfigureAwait(false);
            return row.Id;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "Could not record a pending authorization for {ActionKey} in correlation "
                + "{CorrelationId}; the denial STANDS and is audited, the 409 simply carries no "
                + "authorizationId.",
                // correlationId is caller-supplied (X-Tamma-Correlation-Id header or
                // ?correlationId=), so it is a log-forging vector; the action key comes
                // from the catalog and needs no cleaning.
                decision.Action.ToWire(), LogSanitizer.Clean(correlationId));
            return null;
        }
    }
}
