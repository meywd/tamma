using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Tamma.Api.Auth;
using Tamma.Api.Services.Billing;
using Tamma.Api.Services.Pricing;
using Tamma.Api.Services.PromptStore;
using Tamma.Api.Services.Security;
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

    // ── POST /api/pricing/subscribe ── (Story 34-4 AC11, SettingsManage)
    //
    // Tenant self-service: a tenant_owner picks a PUBLIC (active, non-custom)
    // plan for their OWN tenant. The tenant is resolved STRICTLY from
    // ITenantContext (SaaS) / the sole user's instance (single-user) — a request
    // body NEVER selects the tenant, so a caller can never subscribe/affect
    // another tenant (tenant isolation, AC12/AC14). A member-role caller is
    // rejected 403 by the SettingsManage policy on the route; subscribing to a
    // custom / draft / deprecated / unknown plan returns 422.
    public static async Task<IResult> Subscribe(
        SubscribeRequest? req,
        ClaimsPrincipal user,
        ITenantContext tenantContext,
        ITammaModeProvider modeProvider,
        IPlanCatalogService planCatalog,
        IPlanAssignmentService assignments,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Tamma.Api.Endpoints.PricingEndpoints");

        if (req is null || string.IsNullOrWhiteSpace(req.PlanSlug))
        {
            return Results.BadRequest(new { error = "plan_slug_required" });
        }

        // Tenant strictly from context — never from the body.
        if (tenantContext.TenantId is not Guid tenantId)
        {
            return Results.NotFound(new { error = "no_active_tenant" });
        }

        // Only a PUBLIC (active, non-custom) plan is subscribable here. A custom
        // plan's slug is never resolvable through this route (IsCustom filter),
        // and draft/deprecated are excluded by construction.
        var snapshot = await planCatalog.GetActivePublicBySlugAsync(req.PlanSlug, ct);
        if (snapshot is null)
        {
            return Results.UnprocessableEntity(new { error = "plan_not_public" });
        }

        var actor = Tamma.Api.Endpoints.Admin.AdminTenantsEndpoints.ActorTriple(user);
        try
        {
            var result = await assignments.AssignAsync(
                tenantId, snapshot.PlanId,
                new AssignPlanOptions(
                    ActorUserId: actor.UserId,
                    Reason: "tenant self-service subscribe",
                    Source: "self-service",
                    ActorEmail: actor.Email,
                    ActorPlatformRole: actor.Role),
                ct);

            logger.LogInformation(
                "Tenant {TenantId} subscribed to plan {Slug} v{Version} (mode={Mode})",
                tenantId, snapshot.Slug, snapshot.Version, modeProvider.Mode);

            return Results.Ok(
                Tamma.Api.Endpoints.Admin.AdminTenantsEndpoints.ToPlanAssignmentResponse(tenantId, result));
        }
        catch (TammaError ex)
        {
            return Tamma.Api.Endpoints.Admin.AdminTenantsEndpoints.MapPlanAssignmentError(ex);
        }
    }

    // ── BYOK toggle (Story 34-3) ──────────────────────────────────────────
    // The tenant chooses, per provider, byok (their own key) vs platform
    // (Tamma's key). The tenant is resolved STRICTLY from ITenantContext (SaaS)
    // / the sole user's instance (single-user) — a caller-supplied tenantId is
    // never accepted, so there is no cross-tenant IDOR (stronger than the spec's
    // route-tenant 404). Writes are gated to tenant_owner / tenant_admin by the
    // PricingManage route policy (member → 403). The raw key is NEVER echoed
    // back (reveal-once cabinet rule) — responses carry { provider, mode, keySet }.

    private const int MinByokKeyLength = 8;
    private const int MaxByokKeyLength = 8192;

    // ── POST /api/pricing/providers/{provider}/byok ── (PricingManage)
    //
    // Body { apiKey }. Stores the key in the tenant's Epic 29 cabinet under the
    // canonical slug 32-3 reads, flips the (tenant, provider) owner row to byok,
    // invalidates 32-3's credential cache, emits PRICING.BYOK.ENABLED. A provider
    // that is NOT SaaS-eligible (a cli-token harness provider, or an unknown
    // provider — fail-closed) is rejected 422 in SaaS via Story 32-4's
    // IProviderAuthLookup (single-user is unaffected — CLI providers are
    // single-user only). Idempotent: a re-enable rotates the key + updates the
    // one active row (never a duplicate).
    public static async Task<IResult> EnableByok(
        string provider,
        EnableByokRequest? body,
        ClaimsPrincipal user,
        ITenantContext tenantContext,
        ITammaModeProvider modeProvider,
        IProviderAuthLookup authLookup,
        [FromServices] ITenantProviderBillingService billing,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        var logger = loggerFactory.CreateLogger("Tamma.Api.Endpoints.PricingEndpoints");

        if (string.IsNullOrWhiteSpace(provider))
        {
            return Results.BadRequest(new { error = "invalid_provider" });
        }

        var apiKey = body?.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return Results.BadRequest(new { error = "invalid_key", detail = "apiKey is required." });
        }
        if (apiKey.Length is < MinByokKeyLength or > MaxByokKeyLength)
        {
            return Results.BadRequest(new
            {
                error = "invalid_key",
                detail = $"apiKey must be between {MinByokKeyLength} and {MaxByokKeyLength} chars.",
            });
        }

        // Tenant strictly from context (never the body). Single-user resolves to
        // the sole user's personal tenant, so this is populated in both modes.
        if (tenantContext.TenantId is not Guid tid)
        {
            return Results.BadRequest(new
            {
                error = "no_tenant_context",
                detail = "enabling BYOK requires tenant context.",
            });
        }

        var canonical = BillingProviderKey.Canonicalize(provider);
        if (canonical.Length == 0)
        {
            return Results.BadRequest(new { error = "invalid_provider" });
        }

        // 32-4 SaaS eligibility gate — only api-key providers are BYOK-eligible in
        // SaaS. A cli-token / unknown provider (fail-closed) → 422. Single-user is
        // a hard no-op ("CLI providers are single-user only"). Uses the canonical
        // family key so a vendor handle ("anthropic-claude") still classifies.
        if (modeProvider.Mode == TammaMode.SaaS)
        {
            var authModel = await authLookup.AuthModelAsync(canonical, ct).ConfigureAwait(false);
            if (authModel != ProviderAuthModel.ApiKey)
            {
                logger.LogWarning(
                    "BYOK enable rejected — provider not SaaS-eligible: tenantId={TenantId} provider={Provider}",
                    tid, canonical);
                return Results.UnprocessableEntity(new { error = "CLI providers are single-user only" });
            }
        }

        try
        {
            var result = await billing
                .EnableByokAsync(tid, canonical, apiKey, user.GetUserId(), ct)
                .ConfigureAwait(false);
            // Reveal-safe: provider + mode + keySet only, NEVER the key.
            return Results.Ok(new { provider = result.Provider, mode = result.Mode, keySet = result.KeySet });
        }
        catch (ArgumentException)
        {
            return Results.BadRequest(new { error = "invalid_provider" });
        }
    }

    // ── DELETE /api/pricing/providers/{provider}/byok ── (PricingManage)
    //
    // Flips the (tenant, provider) owner row back to platform, retires the cabinet
    // secret, invalidates 32-3's credential cache, emits PRICING.BYOK.DISABLED.
    // Idempotent (a disable with no active byok row still returns mode=platform).
    public static async Task<IResult> DisableByok(
        string provider,
        ClaimsPrincipal user,
        ITenantContext tenantContext,
        [FromServices] ITenantProviderBillingService billing,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return Results.BadRequest(new { error = "invalid_provider" });
        }

        if (tenantContext.TenantId is not Guid tid)
        {
            return Results.NotFound();
        }

        var canonical = BillingProviderKey.Canonicalize(provider);
        if (canonical.Length == 0)
        {
            return Results.BadRequest(new { error = "invalid_provider" });
        }

        var result = await billing
            .DisableByokAsync(tid, canonical, user.GetUserId(), ct)
            .ConfigureAwait(false);
        return Results.Ok(new { provider = result.Provider, mode = result.Mode, keySet = result.KeySet });
    }

    // ── GET /api/pricing/providers/{provider} ── (MemberAccess: read)
    //
    // The current mode for the caller's OWN (tenant, provider) + whether a key is
    // set. Never returns the key value (keySet only).
    public static async Task<IResult> GetProviderMode(
        string provider,
        ITenantContext tenantContext,
        [FromServices] ITenantProviderBillingService billing,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return Results.BadRequest(new { error = "invalid_provider" });
        }

        if (tenantContext.TenantId is not Guid tid)
        {
            return Results.NotFound(new { error = "no_active_tenant" });
        }

        var canonical = BillingProviderKey.Canonicalize(provider);
        if (canonical.Length == 0)
        {
            return Results.BadRequest(new { error = "invalid_provider" });
        }

        var result = await billing.GetModeAsync(tid, canonical, ct).ConfigureAwait(false);
        return Results.Ok(new { provider = result.Provider, mode = result.Mode, keySet = result.KeySet });
    }

    // ── GET /api/pricing/providers ── (MemberAccess: read)
    //
    // The caller's OWN active per-provider modes. Empty when no tenant context or
    // no explicit rows (platform is the default). Never returns any key value.
    public static async Task<IResult> ListProviderModes(
        ITenantContext tenantContext,
        [FromServices] ITenantProviderBillingService billing,
        CancellationToken ct)
    {
        if (tenantContext.TenantId is not Guid tid)
        {
            return Results.Ok(new { providers = Array.Empty<object>() });
        }

        var modes = await billing.ListModesAsync(tid, ct).ConfigureAwait(false);
        var providers = modes
            .Select(m => new { provider = m.Provider, mode = m.Mode, keySet = m.KeySet })
            .ToList();
        return Results.Ok(new { providers });
    }
}

/// <summary>Story 34-4 — request body for <c>POST /api/pricing/subscribe</c>.</summary>
public sealed record SubscribeRequest(string PlanSlug);

/// <summary>
/// Story 34-3 — request body for <c>POST /api/pricing/providers/{provider}/byok</c>.
/// The key is write-only; it is stored in the Epic 29 cabinet and NEVER echoed back.
/// </summary>
public sealed record EnableByokRequest(string? ApiKey);
