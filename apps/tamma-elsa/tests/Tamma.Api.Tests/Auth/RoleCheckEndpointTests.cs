using System.Net;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Endpoints;

namespace Tamma.Api.Tests.Auth;

/// <summary>
/// Story 16-5 audit: validates the <c>/api/auth/role-check</c> endpoint
/// behavior used by the nginx <c>auth_request</c> directive to gate
/// elsa.tamma.dev / logs.tamma.dev. Returns 200/403/400 by status code only —
/// nginx ignores the body.
/// </summary>
[TestFixture]
public class RoleCheckEndpointTests
{
    private static ClaimsPrincipal PrincipalWithRole(string? role)
    {
        if (role is null)
        {
            return new ClaimsPrincipal(new ClaimsIdentity());
        }
        var identity = new ClaimsIdentity(new[] { new Claim("role", role) }, "Test");
        return new ClaimsPrincipal(identity);
    }

    private static async Task<int> ExecuteAsync(IResult result)
    {
        // ASP.NET Core typed-result executors (Results.Ok / BadRequest / Json)
        // resolve ILoggerFactory + JSON options from RequestServices. A bare
        // DefaultHttpContext has none — wire a minimal DI container so
        // ExecuteAsync can complete without a NRE.
        var services = new ServiceCollection()
            .AddLogging()
            .AddOptions()
            .BuildServiceProvider();

        var ctx = new DefaultHttpContext
        {
            RequestServices = services,
            Response = { Body = new MemoryStream() },
        };
        await result.ExecuteAsync(ctx);
        return ctx.Response.StatusCode;
    }

    // ─── Service: elsa (admin/owner) ────────────────────────────────────────

    [Test]
    public async Task Elsa_AdminRole_Returns200()
    {
        var result = await AuthEndpoints.RoleCheck("elsa", PrincipalWithRole("admin"));
        var status = await ExecuteAsync(result);
        status.Should().Be((int)HttpStatusCode.OK);
    }

    [Test]
    public async Task Elsa_OwnerRole_Returns200()
    {
        var result = await AuthEndpoints.RoleCheck("elsa", PrincipalWithRole("owner"));
        var status = await ExecuteAsync(result);
        status.Should().Be((int)HttpStatusCode.OK);
    }

    [Test]
    public async Task Elsa_MemberRole_Returns403()
    {
        var result = await AuthEndpoints.RoleCheck("elsa", PrincipalWithRole("member"));
        var status = await ExecuteAsync(result);
        status.Should().Be((int)HttpStatusCode.Forbidden);
    }

    // ─── Service: logs (admin/owner) ────────────────────────────────────────

    [Test]
    public async Task Logs_AdminRole_Returns200()
    {
        var result = await AuthEndpoints.RoleCheck("logs", PrincipalWithRole("admin"));
        var status = await ExecuteAsync(result);
        status.Should().Be((int)HttpStatusCode.OK);
    }

    [Test]
    public async Task Logs_MemberRole_Returns403()
    {
        var result = await AuthEndpoints.RoleCheck("logs", PrincipalWithRole("member"));
        var status = await ExecuteAsync(result);
        status.Should().Be((int)HttpStatusCode.Forbidden);
    }

    // ─── Service: admin (admin/owner) ───────────────────────────────────────

    [Test]
    public async Task AdminPanel_OwnerRole_Returns200()
    {
        var result = await AuthEndpoints.RoleCheck("admin", PrincipalWithRole("owner"));
        var status = await ExecuteAsync(result);
        status.Should().Be((int)HttpStatusCode.OK);
    }

    [Test]
    public async Task AdminPanel_MemberRole_Returns403()
    {
        var result = await AuthEndpoints.RoleCheck("admin", PrincipalWithRole("member"));
        var status = await ExecuteAsync(result);
        status.Should().Be((int)HttpStatusCode.Forbidden);
    }

    // ─── Bad / unknown service ──────────────────────────────────────────────

    [Test]
    public async Task UnknownService_Returns400()
    {
        var result = await AuthEndpoints.RoleCheck("nope", PrincipalWithRole("owner"));
        var status = await ExecuteAsync(result);
        status.Should().Be((int)HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task MissingService_Returns400()
    {
        var result = await AuthEndpoints.RoleCheck(null, PrincipalWithRole("owner"));
        var status = await ExecuteAsync(result);
        status.Should().Be((int)HttpStatusCode.BadRequest);
    }

    [Test]
    public async Task EmptyService_Returns400()
    {
        var result = await AuthEndpoints.RoleCheck("", PrincipalWithRole("owner"));
        var status = await ExecuteAsync(result);
        status.Should().Be((int)HttpStatusCode.BadRequest);
    }

    // ─── No role claim defaults to "member" ─────────────────────────────────

    [Test]
    public async Task NoRoleClaim_TreatedAsMember_DeniedAdminServices()
    {
        var result = await AuthEndpoints.RoleCheck("elsa", PrincipalWithRole(null));
        var status = await ExecuteAsync(result);
        status.Should().Be((int)HttpStatusCode.Forbidden);
    }

    // ─── Service name lookup is case-insensitive ────────────────────────────

    [Test]
    public async Task ServiceName_IsCaseInsensitive()
    {
        var result = await AuthEndpoints.RoleCheck("ELSA", PrincipalWithRole("admin"));
        var status = await ExecuteAsync(result);
        status.Should().Be((int)HttpStatusCode.OK);
    }
}
