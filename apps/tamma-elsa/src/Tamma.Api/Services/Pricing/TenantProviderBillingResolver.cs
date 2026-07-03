using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tamma.Api.Services.Billing;
using Tamma.Core;
using Tamma.Core.Enums;
using Tamma.Data;

namespace Tamma.Api.Services.Pricing;

/// <summary>
/// Story 34-3 — reads the AUTHORITATIVE per-<c>(tenant, provider)</c> billing
/// mode from the <c>TenantProviderBilling</c> owner table. This replaces the
/// interim per-TENANT <c>BillingCustomer.BillingMode</c> read
/// (<see cref="BillingCustomerPricingModeResolver"/>) with the true per-provider
/// source of truth — the "one-line swap behind the seam" the interim resolver's
/// doc anticipated.
///
/// <para><b>Resolution:</b> a null tenant (single-user) ⇒
/// <see cref="MetricBillingMode.PlatformProvided"/>. Otherwise the single
/// <c>active</c> row for <c>(tenant, provider)</c> decides; no row ⇒
/// <see cref="MetricBillingMode.PlatformProvided"/> (BYOK is opt-in and only ever
/// an explicit row). An <c>active</c> row whose <c>Mode</c> is neither
/// <c>"platform"</c> nor <c>"byok"</c> is a corrupt owner record — the DB CHECK
/// prevents it, but if one is ever read the resolver FAILS LOUD rather than
/// silently defaulting to platform (never a silent mistag).</para>
/// </summary>
public sealed class TenantProviderBillingResolver : ITenantProviderBillingResolver
{
    private readonly ControlPlaneDbContext _db;
    private readonly ILogger<TenantProviderBillingResolver> _logger;

    public TenantProviderBillingResolver(
        ControlPlaneDbContext db,
        ILogger<TenantProviderBillingResolver> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async Task<MetricBillingMode> ResolveModeAsync(
        Guid? tenantId, string provider, CancellationToken ct = default)
    {
        // Single-user / null tenant — always platform-provided (35-2 AC8).
        if (tenantId is not Guid tid)
        {
            return MetricBillingMode.PlatformProvided;
        }

        // Fix 2 — canonicalize the (possibly vendor-handle / mixed-case) provider to
        // the lowercase family key the owner row is stored under, so a call keyed
        // "anthropic-claude" matches an owner row keyed "anthropic". r.ProviderKey is
        // ALSO lowercased for the compare so a legacy mixed-case stored key still
        // matches; the write path is documented to persist the canonical key.
        var normalized = BillingProviderKey.Canonicalize(provider);

        var mode = await _db.TenantProviderBillings
            .AsNoTracking()
            .Where(r => r.TenantId == tid
                        && r.Status == "active"
                        && r.ProviderKey.ToLower() == normalized)
            .Select(r => r.Mode)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        // No active owner row → the safe default. Existing all-platform tenants
        // are unaffected (the status quo is preserved, never flipped to an error).
        if (mode is null)
        {
            return MetricBillingMode.PlatformProvided;
        }

        // A row exists — honour its DECLARED intent. A value outside the closed
        // domain is a corrupt record (CHECK-prevented); fail loud, never mistag.
        if (!MetricBillingModeExtensions.TryParseToken(mode, out var parsed))
        {
            _logger.LogError(
                "TenantProviderBilling row for tenant {TenantId} provider {Provider} "
                + "has an unparseable Mode '{Mode}' — refusing to silently default.",
                tid, normalized, mode);
            throw new TammaError(
                "BILLING_MODE_CORRUPT",
                $"TenantProviderBilling.Mode '{mode}' is not a valid billing mode "
                + $"for tenant '{tid}' provider '{normalized}'.",
                new Dictionary<string, object?>
                {
                    ["tenantId"] = tid,
                    ["provider"] = normalized,
                    ["mode"] = mode,
                },
                retryable: false,
                severity: TammaErrorSeverity.High);
        }

        return parsed;
    }
}
