using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Tamma.Api.Auth;
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

            // AC10 — the tenant-facing estimate exposes ONLY the SELL price the
            // caller would be charged. The platform cost basis and margin are
            // business-confidential (a customer could otherwise compute the exact
            // platform markup) and are surfaced solely on the platform-owner admin
            // surface, never here.
            return Results.Ok(new
            {
                provider,
                model,
                inputTokens,
                outputTokens,
                pricingMode = priced.PricingMode.ToString(),
                sellPriceUsd = priced.SellPriceUsd,
                invoice = new
                {
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

    // ── GET /api/pricing/entitlements ─────────────────────────────────────
    // Story 34-6 (AC4/AC11) — the caller's OWN resolved entitlement set +
    // live headroom. MemberAccess: any authenticated tenant member reads their
    // own tenant (read is unprivileged — mirrors the PromptStore "GET resolved
    // = any member" RBAC). Tenant is taken from ITenantContext (SaaS) / the
    // sole user (single-user); a request body/param NEVER selects the tenant,
    // so a member can never read another tenant's entitlements.
    public static async Task<IResult> GetEntitlements(
        ClaimsPrincipal user,
        ITenantContext tenantContext,
        ITammaModeProvider modeProvider,
        IEntitlementService entitlements,
        IEntitlementUsageReader usageReader,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Tamma.Api.Endpoints.PricingEndpoints");

        // Per-mode principal (CLAUDE.md two-scoping-model rule):
        //  - single-user → the sole user → their personal tenant (ForUser)
        //  - SaaS        → the caller's ambient tenant (ForTenant)
        EntitlementPrincipal principal;
        if (modeProvider.Mode == TammaMode.SingleUser)
        {
            if (user.GetUserId() is not Guid userId)
            {
                return Results.Unauthorized();
            }

            principal = EntitlementPrincipal.ForUser(userId);
        }
        else
        {
            if (tenantContext.TenantId is not Guid tenantId)
            {
                return Results.NotFound(new { error = "no_active_tenant" });
            }

            principal = EntitlementPrincipal.ForTenant(tenantId);
        }

        try
        {
            var dto = await EntitlementResponseBuilder.BuildAsync(
                entitlements, usageReader, principal, logger, ct);
            return Results.Ok(dto);
        }
        catch (TammaError ex) when (ex.Code == "ENTITLEMENT.RESOLVE.NO_ASSIGNMENT")
        {
            // No active plan assignment — a 404, never a 500 (AC4/AC5).
            return Results.NotFound(new { error = "no_active_assignment" });
        }
        catch (TammaError ex) when (ex.Code == "ENTITLEMENT.RESOLVE.CATALOG_UNAVAILABLE")
        {
            // A pinned plan whose catalog snapshot vanished — a transient/config
            // fault (the snapshot SHOULD exist), NOT "no plan". Fail loud as 503
            // (typed ProblemDetails) so the caller retries; never a permissive
            // 200 and never a bare 500.
            logger.LogError(
                "Entitlement read failed — pinned plan has no catalog snapshot (member self-read)");
            return Results.Problem(
                title: ex.Code, detail: ex.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
        catch (TammaError ex) when (ex.Code == "ENTITLEMENT.RESOLVE.NO_PRINCIPAL")
        {
            // Resolver got a principal with neither a tenant nor a user id — for
            // the member self-read that is a missing/invalid identity → 401.
            return Results.Problem(
                title: ex.Code, detail: ex.Message,
                statusCode: StatusCodes.Status401Unauthorized);
        }
    }

    // ── GET /api/pricing/plans ── (Story 34-2 AC1, MemberAccess)
    //
    // The PUBLIC catalog: active, non-custom plans only. Deprecated / draft /
    // custom plans are excluded by construction (IPlanCatalogService filters
    // Status='active' AND IsCustom=false), so a bespoke enterprise plan can never
    // leak into the pricing/upgrade UI. Works identically in single-user and
    // SaaS mode — the list is platform-global with no per-tenant view.
    public static async Task<IResult> ListPublicPlans(
        IPlanCatalogService planCatalog,
        CancellationToken ct)
    {
        var plans = await planCatalog.ListActivePublicAsync(ct);
        return Results.Ok(new { plans });
    }

    // ── GET /api/pricing/plans/{slug} ── (Story 34-2 AC2, MemberAccess)
    //
    // The single active public plan for a slug. A custom plan's slug is never
    // resolvable here (it is IsCustom=true) → 404. Returns the typed PlanSnapshot
    // projection (AC13 — never a raw EF entity).
    public static async Task<IResult> GetPublicPlanBySlug(
        string slug,
        IPlanCatalogService planCatalog,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return Results.NotFound(new { error = "plan_not_found", slug });
        }

        var snapshot = await planCatalog.GetActivePublicBySlugAsync(slug, ct);
        return snapshot is null
            ? Results.NotFound(new { error = "plan_not_found", slug })
            : Results.Ok(snapshot);
    }
}
