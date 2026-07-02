using Microsoft.AspNetCore.Http;
using Tamma.Api.Authorization;
using Tamma.Api.Services.Billing;
using Tamma.Core;

namespace Tamma.Api.Endpoints.Billing;

/// <summary>
/// Story 35-4 — tenant-scoped subscription lifecycle endpoints under
/// <c>/api/v1/orgs/{tenantId}/billing/subscription</c>. Mounted (SaaS only) with
/// the <see cref="RequireTenantMembershipFilter"/> (cross-tenant access → 403).
/// Every mutation additionally requires <c>tenant_owner</c>/<c>tenant_admin</c>
/// (read from the membership-filter item key); a <c>member</c> caller gets 403
/// BEFORE any Stripe call (AC2). GET is allowed for any member (AC9).
/// </summary>
public static class SubscriptionEndpoints
{
    public sealed record CheckoutRequest(string PlanSlug, int? Seats, int? TrialDays);
    public sealed record ChangePlanRequest(string PlanSlug);
    public sealed record CancelRequest(bool AtPeriodEnd);
    public sealed record SeatsRequest(int Seats);

    public static IEndpointRouteBuilder MapSubscriptionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/orgs/{tenantId:guid}/billing/subscription")
            .RequireAuthorization("MemberAccess")
            .AddEndpointFilter<RequireTenantMembershipFilter>();

        group.MapGet("/", GetSubscription);
        group.MapPost("/checkout", Checkout);
        group.MapPost("/change", ChangePlan);
        group.MapPost("/cancel", Cancel);
        group.MapPost("/seats", ChangeSeats);
        return app;
    }

    internal static async Task<IResult> GetSubscription(
        Guid tenantId, HttpContext http, ISubscriptionService svc, CancellationToken ct)
    {
        var projection = await svc.GetAsync(tenantId, ct);
        return Results.Ok(projection);
    }

    internal static async Task<IResult> Checkout(
        Guid tenantId, CheckoutRequest req, HttpContext http, ISubscriptionService svc,
        ILoggerFactory logs, CancellationToken ct)
    {
        if (RequireAdmin(http) is { } forbidden) return forbidden;
        if (string.IsNullOrWhiteSpace(req?.PlanSlug))
            return Results.BadRequest(new { error = "plan_slug_required" });

        return await Guarded(logs, tenantId, "checkout", async () =>
        {
            var result = await svc.CreateCheckoutSessionAsync(
                tenantId, req.PlanSlug, req.Seats, req.TrialDays, ct);
            return Results.Ok(result);
        });
    }

    internal static async Task<IResult> ChangePlan(
        Guid tenantId, ChangePlanRequest req, HttpContext http, ISubscriptionService svc,
        ILoggerFactory logs, CancellationToken ct)
    {
        if (RequireAdmin(http) is { } forbidden) return forbidden;
        if (string.IsNullOrWhiteSpace(req?.PlanSlug))
            return Results.BadRequest(new { error = "plan_slug_required" });

        return await Guarded(logs, tenantId, "change", async () =>
            Results.Ok(await svc.ChangePlanAsync(tenantId, req.PlanSlug, ct)));
    }

    internal static async Task<IResult> Cancel(
        Guid tenantId, CancelRequest req, HttpContext http, ISubscriptionService svc,
        ILoggerFactory logs, CancellationToken ct)
    {
        if (RequireAdmin(http) is { } forbidden) return forbidden;

        return await Guarded(logs, tenantId, "cancel", async () =>
            Results.Ok(await svc.CancelAsync(tenantId, req?.AtPeriodEnd ?? false, ct)));
    }

    internal static async Task<IResult> ChangeSeats(
        Guid tenantId, SeatsRequest req, HttpContext http, ISubscriptionService svc,
        ILoggerFactory logs, CancellationToken ct)
    {
        if (RequireAdmin(http) is { } forbidden) return forbidden;
        if (req is null)
            return Results.BadRequest(new { error = "seats_required" });

        return await Guarded(logs, tenantId, "seats", async () =>
            Results.Ok(await svc.ChangeSeatsAsync(tenantId, req.Seats, ct)));
    }

    /// <summary>
    /// 403 unless the membership-filter role is <c>tenant_admin</c> or higher.
    /// Returns null when the caller is authorized (mutations proceed).
    /// </summary>
    private static IResult? RequireAdmin(HttpContext http)
    {
        var role = http.Items[RequireTenantMembershipFilter.TenantRoleItemKey] as string;
        if (TenantRoleHierarchy.IsAtLeast(role, TenantRoleHierarchy.Admin)) return null;
        return Results.Json(
            new { error = "forbidden", message = "Requires tenant owner or admin." },
            statusCode: StatusCodes.Status403Forbidden);
    }

    /// <summary>Run the service call, mapping <see cref="TammaError"/> codes + Stripe failures to HTTP.</summary>
    private static async Task<IResult> Guarded(
        ILoggerFactory logs, Guid tenantId, string op, Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (TammaError err)
        {
            var status = err.Code switch
            {
                SubscriptionService.SeatsBelowActiveMembersCode => StatusCodes.Status409Conflict,
                SubscriptionService.NoActiveSubscriptionCode => StatusCodes.Status409Conflict,
                SubscriptionService.NoCustomerCode => StatusCodes.Status409Conflict,
                SubscriptionService.SaasOnlyCode => StatusCodes.Status404NotFound,
                _ => StatusCodes.Status400BadRequest,
            };
            return Results.Json(new { error = err.Code, message = err.Message }, statusCode: status);
        }
        catch (Stripe.StripeException ex)
        {
            // Never log the Stripe key or customer payment details — only the class.
            logs.CreateLogger("Billing.Subscription").LogWarning(
                "Stripe {Op} failed for tenant {TenantId}: {ErrorClass}. Surfaced as 502; the 35-5 "
                + "webhook reconciles the confirmed state.", op, tenantId, ex.GetType().Name);
            return Results.Json(
                new { error = "BILLING.STRIPE.CALL_FAILED", message = "Stripe call failed; please retry." },
                statusCode: StatusCodes.Status502BadGateway);
        }
    }
}
