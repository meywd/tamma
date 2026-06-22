namespace Tamma.Api.Services.Agents;

/// <summary>
/// Story 32-5 — MINIMAL interim seam for the 34-5 markup engine (NOT yet
/// landed). <c>ManagedAgent</c> (T3) consumes this to derive the billed
/// <c>priceUsd</c> from the raw provider cost basis. The real 34-5 markup
/// engine (per-tenant plan markup, tiers, ...) replaces
/// <see cref="PassthroughProviderMarkupEngine"/> behind this same seam.
///
/// <para><b>Pricing rule (story rule 7), load-bearing:</b> <c>priceUsd</c> is
/// the marked-up basis ONLY on the <c>"platform"</c> credential leg, and
/// <c>0</c> on the <c>"byok"</c> leg (the tenant pays their own provider
/// directly; Tamma bills no token price). <c>providerCostUsd</c> (the raw
/// basis) is identical on BOTH legs — it is never branched on
/// <c>credentialSource</c>.</para>
/// </summary>
public interface IProviderMarkupEngine
{
    /// <summary>
    /// Derive the billed price for one run from its raw provider cost basis.
    /// </summary>
    /// <param name="costBasisUsd">The raw <c>IProviderPricingService.Compute</c>
    /// basis (34-11). Identical regardless of credential source.</param>
    /// <param name="credentialSource"><c>"byok"</c> | <c>"platform"</c> | null.
    /// BYOK ⇒ token price 0; platform ⇒ markup applied; null (run never reached
    /// credential resolution) ⇒ 0.</param>
    /// <param name="provider">Provider key (for future per-provider markup).</param>
    /// <param name="model">Model key (for future per-model markup).</param>
    /// <param name="tenantId">Tenant scope (for future per-tenant plan markup).</param>
    decimal Apply(
        decimal costBasisUsd,
        string? credentialSource,
        string provider,
        string model,
        Guid? tenantId);
}

/// <summary>
/// Story 32-5 — the SAFE interim default until 34-5 lands. It applies the
/// rule-7 branch with NO markup multiplier yet:
/// <list type="bullet">
///   <item><description><c>"byok"</c> ⇒ <c>0</c> (Tamma bills no token price;
///     the tenant pays their provider directly).</description></item>
///   <item><description><c>"platform"</c> ⇒ the raw basis passed through
///     (markup multiplier == 1.0 until 34-5 supplies the real
///     per-plan/per-tier markup). <b>34-5 follow-on TODO.</b></description></item>
///   <item><description>null source ⇒ <c>0</c>.</description></item>
/// </list>
/// <para>This is deliberately NOT an identity/passthrough on both legs — a flat
/// passthrough would bill BYOK runs, which is wrong. The branch is the
/// load-bearing part; the multiplier (currently 1.0) is the part 34-5 owns.</para>
/// </summary>
public sealed class PassthroughProviderMarkupEngine : IProviderMarkupEngine
{
    /// <inheritdoc />
    public decimal Apply(
        decimal costBasisUsd,
        string? credentialSource,
        string provider,
        string model,
        Guid? tenantId)
    {
        // Rule 7: byok ⇒ 0 token price; platform ⇒ basis (no markup multiplier
        // until 34-5); unresolved source ⇒ 0. NEVER bill the BYOK leg.
        return credentialSource == CredentialSourceLabel.Platform
            ? costBasisUsd
            : 0m;
    }
}
