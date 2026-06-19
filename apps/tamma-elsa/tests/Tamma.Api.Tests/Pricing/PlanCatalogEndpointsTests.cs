using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Endpoints.Admin;
using Tamma.Api.Services.Pricing;
using Tamma.Api.Tests.TestDoubles;
using Tamma.Data;
using Tamma.Data.Seeders;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Pricing;

/// <summary>
/// Story 34-1 (Phase 5) — the three read-only <c>GET /api/admin/plans*</c>
/// handlers on <see cref="PlanCatalogEndpoints"/>. Drives the handler methods
/// directly against a real catalog service (Postgres testcontainer) and
/// executes the <see cref="IResult"/> into an <see cref="HttpContext"/> to read
/// the status code (the robust pattern the agent endpoint tests use). The
/// <c>OwnerAccess</c> RBAC gate is applied at the Program.cs wiring site (the
/// dev-mode test auth short-circuits named policies). Write endpoints are
/// deferred to Story 34-2.
/// </summary>
[TestFixture]
public class PlanCatalogEndpointsTests
{
    private PostgreSqlContainer _postgres = null!;
    private string _cs = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("plan_endpoint_test")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();
        _cs = _postgres.GetConnectionString();

        await using var ctx = NewContext();
        await ctx.Database.MigrateAsync();
        await PlansSeeder.SeedAsync(ctx);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    private ControlPlaneDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ControlPlaneDbContext>().UseNpgsql(_cs).Options);

    private IPlanCatalogService BuildCatalog(ControlPlaneDbContext ctx) =>
        new PlanCatalogService(ctx, NullLogger<PlanCatalogService>.Instance);

    private static async Task<(int Status, JsonElement Body)> ExecuteAsync(IResult result)
    {
        var services = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        var ctx = new DefaultHttpContext
        {
            RequestServices = services,
            Response = { Body = new MemoryStream() },
        };
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        if (ctx.Response.Body.Length == 0)
        {
            return (ctx.Response.StatusCode, default);
        }
        using var doc = await JsonDocument.ParseAsync(ctx.Response.Body);
        return (ctx.Response.StatusCode, doc.RootElement.Clone());
    }

    [Test]
    public async Task ListActive_Returns_200_With_Active_Snapshots()
    {
        await using var ctx = NewContext();
        var (status, body) = await ExecuteAsync(
            await PlanCatalogEndpoints.ListActive(BuildCatalog(ctx), default));

        status.Should().Be(StatusCodes.Status200OK);
        body.GetProperty("plans").GetArrayLength().Should().Be(3);
    }

    [Test]
    public async Task GetActiveBySlug_Known_Returns_200()
    {
        await using var ctx = NewContext();
        var (status, body) = await ExecuteAsync(
            await PlanCatalogEndpoints.GetActiveBySlug("team", BuildCatalog(ctx), default));

        status.Should().Be(StatusCodes.Status200OK);
        body.GetProperty("slug").GetString().Should().Be("team");
    }

    [Test]
    public async Task GetActiveBySlug_Unknown_Returns_404()
    {
        await using var ctx = NewContext();
        var (status, _) = await ExecuteAsync(
            await PlanCatalogEndpoints.GetActiveBySlug("ghost", BuildCatalog(ctx), default));

        status.Should().Be(StatusCodes.Status404NotFound);
    }

    [Test]
    public async Task GetVersions_Known_Returns_200_With_Chain()
    {
        await using (var editCtx = NewContext())
        {
            var editor = new PlanVersionEditor(
                editCtx, new RecordingPlatformEventPublisher(),
                TimeProvider.System, NullLogger<PlanVersionEditor>.Instance);
            // Idempotent for the suite: only supersede if still v1 active.
            var active = await editCtx.Plans.FirstAsync(p => p.Slug == "enterprise" && p.Status == "active");
            if (active.Version == 1)
            {
                await editor.CreateNewVersionAsync(
                    "enterprise", new PlanDraftSpec(), new PlanEditorPrincipal("u", null));
            }
        }

        await using var ctx = NewContext();
        var (status, body) = await ExecuteAsync(
            await PlanCatalogEndpoints.GetVersions("enterprise", BuildCatalog(ctx), default));

        status.Should().Be(StatusCodes.Status200OK);
        body.GetProperty("versions").GetArrayLength().Should().BeGreaterThanOrEqualTo(2);
    }

    [Test]
    public async Task GetVersions_Unknown_Returns_404()
    {
        await using var ctx = NewContext();
        var (status, _) = await ExecuteAsync(
            await PlanCatalogEndpoints.GetVersions("ghost", BuildCatalog(ctx), default));

        status.Should().Be(StatusCodes.Status404NotFound);
    }
}
