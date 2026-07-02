using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tamma.Api.Services.Providers;
using Tamma.Core;
using Tamma.Data;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Pricing;

/// <summary>
/// Story 34-5 — DB-backed <see cref="IMarginPolicyResolver"/> over the
/// control-plane <c>margin_policies</c> table. Scoped (depends on the scoped
/// <see cref="ControlPlaneDbContext"/>). Resolves provider-override -> plan ->
/// global with timestamp-effective selection, mirroring the 34-11
/// <c>ProviderCostResolver.ResolveAtAsync</c> window logic on the sell side.
/// </summary>
public sealed class MarginPolicyResolver : IMarginPolicyResolver
{
    private readonly ControlPlaneDbContext _db;
    private readonly ILogger<MarginPolicyResolver> _logger;

    public MarginPolicyResolver(
        ControlPlaneDbContext db,
        ILogger<MarginPolicyResolver> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<MarginPolicy> ResolveAsync(
        string provider, string? planSlug, DateTime atTimestamp, CancellationToken ct = default)
    {
        var at = NormalizeUtc(atTimestamp);

        // Provider scope uses the canonical provider key (alias-normalized) so a
        // provider-scoped override keys off the same handle 34-11 stores.
        var providerKey = string.IsNullOrWhiteSpace(provider)
            ? provider
            : ProviderRateLookup.Canonicalize(provider);

        // Most-specific first: provider override -> plan -> global.
        var providerHit = await ResolveAtScopeAsync("provider", providerKey, at, ct);
        if (providerHit is not null)
        {
            return Resolved(providerHit, at);
        }

        if (!string.IsNullOrWhiteSpace(planSlug))
        {
            var planHit = await ResolveAtScopeAsync("plan", planSlug, at, ct);
            if (planHit is not null)
            {
                return Resolved(planHit, at);
            }
        }

        var globalHit = await ResolveAtScopeAsync("global", null, at, ct);
        if (globalHit is not null)
        {
            return Resolved(globalHit, at);
        }

        _logger.LogWarning(
            "No margin policy resolves for provider={Provider}, planSlug={PlanSlug} at {AtTimestamp}",
            providerKey, planSlug ?? "", at);

        throw new TammaError(
            "PRICING.MARGIN.NO_POLICY",
            $"No margin policy resolves for provider '{providerKey}'"
            + (planSlug is null ? "" : $" / plan '{planSlug}'")
            + $" at {at:O}. Seed a global policy or configure a plan/provider override.",
            new Dictionary<string, object?>
            {
                ["provider"] = providerKey,
                ["planSlug"] = planSlug,
                ["atTimestamp"] = at.ToString("O"),
            },
            retryable: false,
            severity: TammaErrorSeverity.High);
    }

    /// <summary>
    /// The row for <paramref name="scope"/>/<paramref name="refKey"/> with the
    /// greatest <c>EffectiveFrom &lt;= atTimestamp</c> (active OR superseded —
    /// the time-travel selection). Null when the scope has no matching row.
    /// </summary>
    private async Task<MarginPolicy?> ResolveAtScopeAsync(
        string scope, string? refKey, DateTime at, CancellationToken ct)
    {
        var query = _db.MarginPolicies
            .AsNoTracking()
            .Where(p => p.Scope == scope && p.EffectiveFrom <= at);

        // RefKey NULL (global) needs an IS NULL comparison, not = NULL.
        query = refKey is null
            ? query.Where(p => p.RefKey == null)
            : query.Where(p => p.RefKey == refKey);

        return await query
            .OrderByDescending(p => p.EffectiveFrom)
            .ThenByDescending(p => p.CreatedAt)
            .FirstOrDefaultAsync(ct);
    }

    private MarginPolicy Resolved(MarginPolicy policy, DateTime at)
    {
        _logger.LogDebug(
            "Margin policy resolved: scope={Scope} refKey={RefKey} effectiveFrom={EffectiveFrom} at {AtTimestamp}",
            policy.Scope, policy.RefKey ?? "", policy.EffectiveFrom, at);
        return policy;
    }

    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value.ToUniversalTime(),
    };
}
