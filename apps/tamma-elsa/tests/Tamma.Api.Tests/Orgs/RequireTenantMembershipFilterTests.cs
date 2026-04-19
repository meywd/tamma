using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Tamma.Api.Authorization;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Orgs;

[TestFixture]
public class RequireTenantMembershipFilterTests
{
    private Mock<ITenantMembershipRepository> _repo = null!;
    private RequireTenantMembershipFilter _filter = null!;

    [SetUp]
    public void Setup()
    {
        _repo = new Mock<ITenantMembershipRepository>(MockBehavior.Strict);
        _filter = new RequireTenantMembershipFilter(_repo.Object);
    }

    private static HttpContext Context(Guid? userId, Guid? pathTenantId, bool authenticated = true)
    {
        var ctx = new DefaultHttpContext { RequestServices = ApiTestFixture.Factory.Services };
        ctx.Response.Body = new MemoryStream();
        if (authenticated && userId.HasValue)
        {
            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId.Value.ToString()),
            }, authenticationType: "Test");
            ctx.User = new ClaimsPrincipal(identity);
        }
        else
        {
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity());
        }
        if (pathTenantId.HasValue)
        {
            ctx.Request.RouteValues["tenantId"] = pathTenantId.Value.ToString();
        }
        return ctx;
    }

    private static async Task<int> RunFilter(RequireTenantMembershipFilter filter, HttpContext ctx)
    {
        var invocation = new DefaultEndpointFilterInvocationContext(ctx);
        EndpointFilterDelegate next = _ => ValueTask.FromResult<object?>(Results.Ok());
        var result = await filter.InvokeAsync(invocation, next);
        if (result is IResult r)
        {
            await r.ExecuteAsync(ctx);
        }
        return ctx.Response.StatusCode;
    }

    [Test]
    public async Task Returns401_WhenUnauthenticated()
    {
        var ctx = Context(userId: null, pathTenantId: Guid.NewGuid(), authenticated: false);
        var status = await RunFilter(_filter, ctx);
        status.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Test]
    public async Task Returns400_WhenPathTenantIdMissing()
    {
        var ctx = Context(userId: Guid.NewGuid(), pathTenantId: null);
        var status = await RunFilter(_filter, ctx);
        status.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task Returns403_WhenUserNotMember()
    {
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        _repo.Setup(r => r.GetRoleAsync(tenantId, userId)).ReturnsAsync((string?)null);

        var ctx = Context(userId, tenantId);
        var status = await RunFilter(_filter, ctx);
        status.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Test]
    public async Task Succeeds_AndStashesRoleOnSuccess()
    {
        var userId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();
        _repo.Setup(r => r.GetRoleAsync(tenantId, userId)).ReturnsAsync("admin");

        var ctx = Context(userId, tenantId);
        var status = await RunFilter(_filter, ctx);
        status.Should().Be(StatusCodes.Status200OK);

        ctx.Items[RequireTenantMembershipFilter.TenantRoleItemKey].Should().Be("admin");
        ctx.Items[RequireTenantMembershipFilter.PathTenantIdItemKey].Should().Be(tenantId);
    }
}
