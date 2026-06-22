using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;
using Tamma.Api.Auth;

namespace Tamma.Api.Tests.Auth;

/// <summary>
/// I4 — the engine→API callback (<c>POST /api/engine/events</c>) is gated by
/// <c>EngineServiceOnly</c> (<see cref="ServicePrincipalRequirement"/>) instead
/// of <c>WorkflowsManage</c>, so a tenant owner/admin (who holds
/// <c>workflows:manage</c>) can NOT forge audit events. These pin the handler's
/// allow/deny contract: only a platform service principal passes.
/// </summary>
[TestFixture]
public class ServicePrincipalHandlerTests
{
    [Test]
    public async Task TenantAdmin_WithWorkflowsManage_ButNoServiceScope_IsDenied()
    {
        // A tenant admin's user-scope key: per-tenant role + permission claims,
        // but never the platform "*" permission and no ServiceAuthPrincipal.
        var ctx = BuildContext(
            new Claim(ClaimTypes.Role, "admin"),
            new Claim("permission", "workflows:manage"));

        await Handler(httpContext: null).HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeFalse(
            "a tenant admin must not be able to POST forged audit events to the engine callback");
    }

    [Test]
    public async Task ServiceKey_WithWildcardPermission_IsAllowed()
    {
        var ctx = BuildContext(new Claim("permission", "*"));

        await Handler(httpContext: null).HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeTrue("the platform service key carries the '*' permission");
    }

    [Test]
    public async Task ResolvedServiceAuthPrincipal_IsAllowed()
    {
        var http = new DefaultHttpContext();
        http.SetAuthPrincipal(new ServiceAuthPrincipal(
            Guid.NewGuid(), "tamma-engine", new[] { "engine:events" }, TenantId: null));

        // No "*" claim on the ClaimsPrincipal — the resolved principal alone
        // must satisfy the requirement.
        var ctx = BuildContext();

        await Handler(http).HandleAsync(ctx);

        ctx.HasSucceeded.Should().BeTrue("a resolved ServiceAuthPrincipal is the authoritative service signal");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static ServicePrincipalHandler Handler(HttpContext? httpContext)
    {
        var accessor = new HttpContextAccessor { HttpContext = httpContext };
        return new ServicePrincipalHandler(accessor);
    }

    private static AuthorizationHandlerContext BuildContext(params Claim[] claims)
    {
        var identity = new ClaimsIdentity(claims, authenticationType: "ApiKey");
        var user = new ClaimsPrincipal(identity);
        var requirement = new ServicePrincipalRequirement();
        return new AuthorizationHandlerContext(new[] { requirement }, user, resource: null);
    }
}
