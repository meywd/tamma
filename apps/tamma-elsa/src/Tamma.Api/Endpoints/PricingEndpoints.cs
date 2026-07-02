using Microsoft.AspNetCore.Http;
using Tamma.Api.Services.Pricing;
using Tamma.Api.Services.PromptStore;
using Tamma.Core;
using Tamma.Core.Enums;
using Tamma.Data;

namespace Tamma.Api.Endpoints;

/// <summary>
/// Story 34-5 — tenant-facing pricing endpoints under <c>/api/pricing</c>
/// (<c>MemberAccess</c>). Powers the upgrade / cost UI in
/// <c>packages/dashboard-user</c> by pricing a hypothetical usage line under the
/// caller's OWN plan + the caller's <c>(tenant, provider)</c> pricing mode.
///
/// <para><b>Tenant isolation (AC14):</b> the plan and pricing mode are resolved
/// strictly from <see cref="ITenantContext.TenantId"/> (SaaS) / the sole user's
/// instance (single-user). There is no cross-tenant read path here, and the
/// margin-policy rows themselves are only mutable via the platform-owner admin
/// routes — a tenant role gets 403 on <c>/api/admin/pricing/*</c>.</para>
/// </summary>
public static class PricingEndpoints
{
    // ── GET /api/pricing/estimate?provider=&model=&inputTokens=&outputTokens= ──
    public static async Task<IResult> GetEstimate(
        string? provider,
        string? model,
        int inputTokens,
        int outputTokens,
        ITenantContext tenantContext,
        ITammaModeProvider modeProvider,
        IPlanCatalogService planCatalog,
        ITenantProviderPricingModeResolver modeResolver,
        IMarginPolicyResolver policyResolver,
        IUsagePricingEngine engine,
        TimeProvider time,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Tamma.Api.Endpoints.PricingEndpoints");

        if (string.IsNullOrWhiteSpace(provider))
        {
            return Results.BadRequest(new { error = "provider_required" });
        }
        if (inputTokens < 0 || outputTokens < 0)
        {
            return Results.BadRequest(new { error = "negative_tokens" });
        }

        // Resolve the caller's tenant strictly — SaaS uses the ambient tenant;
        // single-user has no tenant (the sole user's instance uses the global
        // policy, no per-tenant plan tier).
        var tenantId = modeProvider.Mode == TammaMode.SaaS && tenantContext.TenantId is Guid tid
            ? tid
            : (Guid?)null;

        string? planSlug = null;
        if (tenantId is Guid resolvedTenant)
        {
            var snapshot = await planCatalog.GetForTenantAsync(resolvedTenant, ct);
            planSlug = snapshot?.Slug;
        }

        var pricingMode = await modeResolver.ResolveModeAsync(tenantId, provider, ct);
        var occurredAt = time.GetUtcNow().UtcDateTime;
        var line = new UsageLine(provider, model, inputTokens, outputTokens, pricingMode, occurredAt);

        try
        {
            var policy = await policyResolver.ResolveAsync(provider, planSlug, occurredAt, ct);
            var priced = engine.PriceUsage(line, policy);

            logger.LogInformation(
                "Pricing estimate served: tenantId={TenantId} provider={Provider} model={Model} mode={Mode}",
                tenantId, provider, model ?? "", pricingMode);

            return Results.Ok(new
            {
                provider,
                model,
                inputTokens,
                outputTokens,
                pricingMode = priced.PricingMode.ToString(),
                costBasisUsd = priced.CostBasisUsd,
                marginUsd = priced.MarginUsd,
                sellPriceUsd = priced.SellPriceUsd,
                invoice = new
                {
                    costBasisUsd = PricedUsage.InvoiceUsd(priced.CostBasisUsd),
                    marginUsd = PricedUsage.InvoiceUsd(priced.MarginUsd),
                    sellPriceUsd = PricedUsage.InvoiceUsd(priced.SellPriceUsd),
                },
            });
        }
        catch (TammaError ex) when (ex.Code == "PRICING.UNKNOWN_MODEL")
        {
            // Client asked for an unpriced (provider, model) — a 4xx, not a 500.
            return Results.BadRequest(new { error = ex.Code, message = ex.Message });
        }
        catch (TammaError ex) when (ex.Code == "PRICING.MARGIN.NO_POLICY")
        {
            // No margin policy resolves — a server-side misconfiguration
            // (global policy not seeded). Fail loud, never price at zero margin.
            logger.LogError(
                "Pricing estimate failed — no margin policy: provider={Provider} planSlug={PlanSlug}",
                provider, planSlug ?? "");
            return Results.Problem(
                title: ex.Code, detail: ex.Message, statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}
