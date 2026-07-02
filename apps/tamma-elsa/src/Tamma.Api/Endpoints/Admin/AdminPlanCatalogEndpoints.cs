using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Tamma.Api.Dtos.Pricing;
using Tamma.Api.Services.Pricing;
using Tamma.Core;
using Tamma.Core.Enums;

namespace Tamma.Api.Endpoints.Admin;

/// <summary>
/// Story 34-2 — the plan-catalog ADMIN write surface under
/// <c>/api/admin/pricing/plans*</c>. Every route MUST be gated behind the
/// <c>PlatformOwnerAccess</c> policy at the wiring site (NOT <c>OwnerAccess</c>,
/// which admits every personal-tenant owner — Finding C1): the price book is
/// platform-GLOBAL in both single-user and SaaS modes with no per-tenant
/// override layer, so it is platform-scoped admin work.
///
/// <para>All mutation goes through the immutable, versioned
/// <see cref="IPlanVersionEditor"/> (Story 34-1) — this class never mutates a
/// plan row in place and never duplicates the supersede/deprecate logic. It maps
/// the wire DTOs onto <c>PlanDraftSpec</c>, validates the closed-enum fields
/// (fail-loud, before any write), translates the editor's stable
/// <c>TammaError</c> codes onto HTTP status codes (422/400/404/409), and returns
/// the typed <see cref="PlanSnapshot"/> projection (AC13 — never a raw EF
/// entity). The editor emits the <c>PLAN.CATALOG.UPDATED</c> /
/// <c>PLAN.CUSTOM.CREATED</c> DCB events.</para>
/// </summary>
public static class AdminPlanCatalogEndpoints
{
    // ── GET /api/admin/pricing/plans?status=&isCustom=&tenantId= ──
    public static async Task<IResult> ListForAdmin(
        string? status,
        bool? isCustom,
        Guid? tenantId,
        IPlanCatalogService catalog,
        CancellationToken ct)
    {
        var plans = await catalog.ListAllForAdminAsync(
            new PlanListFilter(Status: status, IsCustom: isCustom, TenantId: tenantId), ct);
        return Results.Ok(new { plans });
    }

    // ── POST /api/admin/pricing/plans ── (create initial version → 201)
    public static async Task<IResult> CreatePlan(
        CreatePlanRequest body,
        IPlanVersionEditor editor,
        IPlanCatalogService catalog,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.Slug))
        {
            return Results.BadRequest(new { error = "slug_required" });
        }

        try
        {
            var draft = ToDraft(
                body.DisplayName, body.BillingInterval,
                body.Features, body.Entitlements, body.Prices);

            var plan = await editor.CreateInitialVersionAsync(
                body.Slug.Trim(), draft, ActorFrom(principal), ct);

            var snapshot = await catalog.GetByIdAsync(plan.Id, ct);
            return Results.Json(snapshot, statusCode: StatusCodes.Status201Created);
        }
        catch (TammaError ex)
        {
            return MapError(ex);
        }
        catch (DbUpdateException dbEx) when (IsUniqueViolation(dbEx))
        {
            return Results.Conflict(new { error = "PLAN.SLUG_VERSION.CONFLICT", message = "A plan with this (slug, version) already exists." });
        }
    }

    // ── PUT /api/admin/pricing/plans/{slug} ── (new version → 200)
    public static async Task<IResult> VersionPlan(
        string slug,
        VersionPlanRequest body,
        IPlanVersionEditor editor,
        IPlanCatalogService catalog,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return Results.BadRequest(new { error = "slug_required" });
        }

        body ??= new VersionPlanRequest();

        try
        {
            var draft = ToDraft(
                body.DisplayName, body.BillingInterval,
                body.Features, body.Entitlements, body.Prices);

            var plan = await editor.VersionPlanAsync(slug.Trim(), draft, ActorFrom(principal), ct);

            var snapshot = await catalog.GetByIdAsync(plan.Id, ct);
            return Results.Ok(snapshot);
        }
        catch (TammaError ex)
        {
            return MapError(ex);
        }
        catch (DbUpdateException dbEx) when (IsUniqueViolation(dbEx))
        {
            return Results.Conflict(new { error = "PLAN.SLUG_VERSION.CONFLICT", message = "A plan with this (slug, version) already exists." });
        }
    }

    // ── POST /api/admin/pricing/plans/custom ── (mint bespoke plan → 201)
    public static async Task<IResult> CreateCustomPlan(
        CreateCustomPlanRequest body,
        IPlanVersionEditor editor,
        IPlanCatalogService catalog,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        if (body is null || body.TenantId == Guid.Empty)
        {
            return Results.BadRequest(new { error = "tenantId_required" });
        }

        // AC5 — a custom plan must NEVER surface in the public catalog. A request
        // that asks for public visibility is a fail-loud 400 (the IsCustom filter
        // is the real defence; this 400 is the explicit rejection signal).
        if (body.MakePublic == true)
        {
            return Results.BadRequest(new
            {
                error = "PLAN.CUSTOM.PUBLIC_REJECTED",
                message = "A custom (bespoke enterprise) plan cannot be published to the public catalog.",
            });
        }

        try
        {
            var draft = ToDraft(
                body.DisplayName, body.BillingInterval,
                body.Features, body.Entitlements, body.Prices);

            var plan = await editor.CreateCustomVersionAsync(
                body.TenantId, draft, ActorFrom(principal), ct);

            var snapshot = await catalog.GetByIdAsync(plan.Id, ct);
            return Results.Json(snapshot, statusCode: StatusCodes.Status201Created);
        }
        catch (TammaError ex)
        {
            return MapError(ex);
        }
        catch (DbUpdateException dbEx) when (IsUniqueViolation(dbEx))
        {
            return Results.Conflict(new { error = "PLAN.SLUG_VERSION.CONFLICT", message = "A plan with this (slug, version) already exists." });
        }
    }

    // ── DELETE /api/admin/pricing/plans/{slug}/versions/{version}?force= ──
    public static async Task<IResult> DeprecateVersion(
        string slug,
        int version,
        bool? force,
        IPlanVersionEditor editor,
        ClaimsPrincipal principal,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(slug))
        {
            return Results.BadRequest(new { error = "slug_required" });
        }

        try
        {
            var result = await editor.DeprecateVersionAsync(
                slug.Trim(), version, force ?? false, ActorFrom(principal), ct);

            if (!result.Deprecated)
            {
                // Blocked by active assignments + no force (AC7).
                return Results.Conflict(new
                {
                    error = "PLAN.DEPRECATE.HAS_ASSIGNMENTS",
                    message = "Version has active tenant assignments; pass force=true to deprecate anyway.",
                    affectedTenantCount = result.AffectedTenantCount,
                });
            }

            return Results.NoContent();
        }
        catch (TammaError ex)
        {
            return MapError(ex);
        }
    }

    // ── Mapping + validation helpers ──

    /// <summary>
    /// Map the wire DTO fields onto a <c>PlanDraftSpec</c>. Parses + validates
    /// each entitlement metric key against the <c>EntitlementMetricKey</c> enum
    /// (AC8) — an unknown key throws <c>PLAN.METRIC_KEY.UNKNOWN</c> which
    /// <see cref="MapError"/> translates to 422. Null child collections are
    /// preserved as null (⇒ "copy prior version" on the version path).
    /// </summary>
    private static PlanDraftSpec ToDraft(
        string? displayName,
        string? billingInterval,
        IReadOnlyList<PlanFeatureDto>? features,
        IReadOnlyList<PlanEntitlementDto>? entitlements,
        IReadOnlyList<PlanPriceDto>? prices)
    {
        var featureDrafts = features?
            .Select(f => new PlanFeatureDraft(f.FeatureKey, f.BoolValue, f.StringValue))
            .ToList();

        var entitlementDrafts = entitlements?
            .Select(e => new PlanEntitlementDraft(
                ParseMetricKey(e.MetricKey), e.LimitValue, e.Period, e.OverageMode))
            .ToList();

        var priceDrafts = prices?
            .Select(p => new PlanPriceDraft(
                p.PricingMode, p.RecurringUsd, p.SeatUsd,
                string.IsNullOrWhiteSpace(p.MeteredComponentJson) ? "{}" : p.MeteredComponentJson))
            .ToList();

        return new PlanDraftSpec(
            DisplayName: string.IsNullOrWhiteSpace(displayName) ? null : displayName,
            BillingInterval: string.IsNullOrWhiteSpace(billingInterval) ? null : billingInterval,
            Features: featureDrafts,
            Entitlements: entitlementDrafts,
            Prices: priceDrafts);
    }

    /// <summary>
    /// Parse a snake_case metric key string, re-throwing as
    /// <c>PLAN.METRIC_KEY.INVALID</c> (a stable 422 code) so a malformed catalog
    /// write names the offending key.
    /// </summary>
    private static EntitlementMetricKey ParseMetricKey(string metricKey)
    {
        if (string.IsNullOrWhiteSpace(metricKey))
        {
            throw new TammaError(
                "PLAN.METRIC_KEY.INVALID",
                "Entitlement metric key is required.",
                retryable: false,
                severity: TammaErrorSeverity.High);
        }

        try
        {
            return EntitlementMetricKeyExtensions.Parse(metricKey);
        }
        catch (TammaError ex)
        {
            // Normalise the 34-1 PLAN.METRIC_KEY.UNKNOWN code to the AC8
            // PLAN.METRIC_KEY.INVALID contract while preserving the offending key.
            throw new TammaError(
                "PLAN.METRIC_KEY.INVALID",
                ex.Message,
                new Dictionary<string, object?> { ["metricKey"] = metricKey },
                retryable: false,
                severity: TammaErrorSeverity.High);
        }
    }

    private static PlanEditorPrincipal ActorFrom(ClaimsPrincipal? principal)
    {
        var userId = principal?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
            ?? principal?.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var email = principal?.FindFirst(JwtRegisteredClaimNames.Email)?.Value
            ?? principal?.FindFirst(ClaimTypes.Email)?.Value
            ?? principal?.FindFirst("email")?.Value;
        return new PlanEditorPrincipal(userId, email);
    }

    private static IResult MapError(TammaError ex) => ex.Code switch
    {
        "PLAN.METRIC_KEY.INVALID" or "PLAN.METRIC_KEY.UNKNOWN" or "PLAN.METRIC_KEY.UNMAPPED"
            or "PLAN.PRICING_MODE.INVALID" or "PLAN.BILLING_INTERVAL.INVALID"
            or "PLAN.ENTITLEMENT_PERIOD.INVALID" or "PLAN.OVERAGE_MODE.INVALID"
            or "PLAN.PRICE.INVALID" or "PLAN.LIMIT.INVALID"
            => Results.UnprocessableEntity(new { error = ex.Code, message = ex.Message, context = ex.Context }),

        "PLAN.CUSTOM.PUBLIC_REJECTED" or "PLAN.DISPLAY_NAME.REQUIRED" or "PLAN.CUSTOM.TENANT_REQUIRED"
            => Results.BadRequest(new { error = ex.Code, message = ex.Message }),

        "PLAN.SLUG.EXISTS" or "PLAN.VERSION.IMMUTABLE"
            => Results.Conflict(new { error = ex.Code, message = ex.Message }),

        "PLAN.VERSION.NOT_FOUND" or "PLAN.VERSION.NO_ACTIVE"
            => Results.NotFound(new { error = ex.Code, message = ex.Message }),

        _ => Results.Problem(title: ex.Code, detail: ex.Message, statusCode: StatusCodes.Status500InternalServerError),
    };

    /// <summary>
    /// Postgres 23505 unique-violation from the <c>UX_plans_Slug_Version</c>
    /// index (AC12) — surfaced as 409, not a bare 500. Mirrors
    /// <c>AdminPricingEndpoints.IsUniqueViolation</c>.
    /// </summary>
    private static bool IsUniqueViolation(DbUpdateException dbEx)
        => dbEx.InnerException is Npgsql.PostgresException pgEx
           && string.Equals(pgEx.SqlState, "23505", StringComparison.Ordinal);
}
