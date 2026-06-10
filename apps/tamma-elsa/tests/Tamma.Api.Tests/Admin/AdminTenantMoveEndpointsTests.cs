using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Moq;
using NUnit.Framework;
using Tamma.Api.Dtos.Admin;
using Tamma.Api.Endpoints.Admin;
using Tamma.Api.Services.Provisioning;
using Tamma.Api.Tests.Infrastructure;
using Tamma.Api.Tests.TestDoubles;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Admin;

/// <summary>
/// Unified-tenancy Phase 4 Task 4 — handler-direct tests for the admin
/// move-tenant endpoints (<see cref="AdminTenantsEndpoints.MoveTenant"/> +
/// <see cref="AdminTenantsEndpoints.GetTenantMove"/>). Mirrors the
/// <see cref="AdminTenantsTests"/> harness: EF InMemory
/// <see cref="ControlPlaneDbContext"/> for the shadow columns, a strict
/// <see cref="Mock{T}"/> of <see cref="IPlatformQueuedTaskRepository"/> as
/// the queue's test-visible surface (the same seam the Cranl v2 dispatcher
/// tests assert against), and the recording event publisher for the audit
/// event.
///
/// <para>Coverage: 404 tenant / 404 target row, 409 target == current
/// placement, 400 missing target id, 202 + `tenant.move` task enqueued
/// with a round-trippable payload + audit event, and the GET polling
/// surface (Status / FailureReason / DatabaseId / SchemaName). The
/// member-role 403 gate lives in <see cref="AdminTenantMoveAuthTests"/>
/// below (production-mode factory — the shared fixture's permissive-dev
/// branch bypasses policies).</para>
/// </summary>
[TestFixture]
public class AdminTenantMoveEndpointsTests
{
    private ControlPlaneDbContext _db = null!;
    private Mock<IPlatformQueuedTaskRepository> _platformTasks = null!;
    private RecordingPlatformEventPublisher _publisher = null!;
    private List<PlatformQueuedTask> _enqueued = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(
                Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _db = new ControlPlaneDbContext(options);

        _enqueued = new List<PlatformQueuedTask>();
        _platformTasks = new Mock<IPlatformQueuedTaskRepository>(MockBehavior.Strict);
        _platformTasks
            .Setup(q => q.EnqueueAsync(
                It.IsAny<PlatformQueuedTask>(), It.IsAny<CancellationToken>()))
            .Callback<PlatformQueuedTask, CancellationToken>((t, _) => _enqueued.Add(t))
            .ReturnsAsync((PlatformQueuedTask t, CancellationToken _) => t);

        _publisher = new RecordingPlatformEventPublisher();
    }

    [TearDown]
    public void TearDown() => _db.Dispose();

    private async Task<Guid> SeedTenantAsync(
        string? status = "active",
        Guid? databaseId = null,
        string? schemaName = null,
        string? failureReason = null)
    {
        var id = Guid.NewGuid();
        var tenant = new Tenant
        {
            Id = id,
            Name = "Acme",
            Slug = "acme-" + id.ToString("N").Substring(0, 6),
            Type = "team",
            Plan = "free",
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            UpdatedAt = DateTime.UtcNow,
        };
        _db.Tenants.Add(tenant);
        _db.Entry(tenant).Property("Status").CurrentValue = status;
        _db.Entry(tenant).Property("DatabaseId").CurrentValue = databaseId;
        _db.Entry(tenant).Property("SchemaName").CurrentValue = schemaName;
        _db.Entry(tenant).Property("FailureReason").CurrentValue = failureReason;
        await _db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> SeedDatabaseRowAsync(string label = "shared-eu-1")
    {
        var row = new TenantDatabase
        {
            Id = Guid.NewGuid(),
            Label = label,
            Host = "db.example.test",
            Port = 5432,
            AdminConnectionStringEncrypted = new byte[] { 1, 2, 3 },
            PlacementClass = "shared",
            TierEligibility = ["free", "team"],
            Status = "active",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.TenantDatabases.Add(row);
        await _db.SaveChangesAsync();
        return row.Id;
    }

    private Task<IResult> InvokeMoveAsync(Guid tenantId, MoveTenantRequest? req) =>
        AdminTenantsEndpoints.MoveTenant(
            tenantId, req, _db, _platformTasks.Object, _publisher,
            AdminTenantsTests.AdminPrincipal());

    private static int StatusCodeOf(IResult result)
    {
        if (result is IStatusCodeHttpResult s && s.StatusCode.HasValue)
            return s.StatusCode.Value;
        throw new InvalidOperationException(
            $"Result type {result.GetType().FullName} does not expose a status code.");
    }

    // ── POST /api/admin/tenants/{id}/move ──

    [Test]
    public async Task MoveTenant_TenantNotFound_Returns404_NothingEnqueued()
    {
        var targetId = await SeedDatabaseRowAsync();

        var result = await InvokeMoveAsync(Guid.NewGuid(), new MoveTenantRequest(targetId));

        StatusCodeOf(result).Should().Be(StatusCodes.Status404NotFound);
        _enqueued.Should().BeEmpty();
    }

    [Test]
    public async Task MoveTenant_TargetDatabaseNotFound_Returns404_NothingEnqueued()
    {
        var tenantId = await SeedTenantAsync();

        var result = await InvokeMoveAsync(
            tenantId, new MoveTenantRequest(Guid.NewGuid()));

        StatusCodeOf(result).Should().Be(StatusCodes.Status404NotFound);
        _enqueued.Should().BeEmpty();
    }

    [Test]
    public async Task MoveTenant_TargetEqualsCurrentPlacement_Returns409_WithMessage()
    {
        var targetId = await SeedDatabaseRowAsync();
        var tenantId = await SeedTenantAsync(databaseId: targetId, schemaName: "t_abc");

        var result = await InvokeMoveAsync(tenantId, new MoveTenantRequest(targetId));

        StatusCodeOf(result).Should().Be(StatusCodes.Status409Conflict);
        var body = result.Should().BeAssignableTo<IValueHttpResult>().Subject.Value;
        JsonSerializer.Serialize(body).Should()
            .Contain("already_on_target_database")
            .And.Contain("no-op");
        _enqueued.Should().BeEmpty();
    }

    [Test]
    public async Task MoveTenant_MissingTargetDatabaseId_Returns400()
    {
        var tenantId = await SeedTenantAsync();

        var nullBody = await InvokeMoveAsync(tenantId, null);
        var emptyGuid = await InvokeMoveAsync(
            tenantId, new MoveTenantRequest(Guid.Empty));

        StatusCodeOf(nullBody).Should().Be(StatusCodes.Status400BadRequest);
        StatusCodeOf(emptyGuid).Should().Be(StatusCodes.Status400BadRequest);
        _enqueued.Should().BeEmpty();
    }

    [Test]
    public async Task MoveTenant_HappyPath_Returns202_AndEnqueuesPlatformTask()
    {
        var sourceId = await SeedDatabaseRowAsync("source");
        var targetId = await SeedDatabaseRowAsync("target");
        var tenantId = await SeedTenantAsync(databaseId: sourceId, schemaName: "t_abc");

        var result = await InvokeMoveAsync(tenantId, new MoveTenantRequest(targetId));

        var accepted = result.Should()
            .BeOfType<Accepted<AdminTenantMoveAcceptedResponse>>().Subject;
        accepted.Location.Should().Be($"/api/admin/tenants/{tenantId}/move");
        accepted.Value!.TenantId.Should().Be(tenantId);
        accepted.Value.TargetDatabaseId.Should().Be(targetId);
        accepted.Value.Status.Should().Be("active");
        accepted.Value.StatusUrl.Should().Be($"/api/admin/tenants/{tenantId}/move");

        // The queue's test-visible surface: one `tenant.move` task whose
        // payload round-trips both ids (same seam the Cranl v2 dispatcher
        // tests verify EnqueueAsync against).
        _enqueued.Should().HaveCount(1);
        var task = _enqueued[0];
        task.Type.Should().Be(MoveTenantTaskPayload.TaskType);
        task.TenantId.Should().Be(tenantId);
        var payload = JsonSerializer.Deserialize<MoveTenantTaskPayload>(task.Payload)!;
        payload.TenantId.Should().Be(tenantId);
        payload.TargetDatabaseId.Should().Be(targetId);

        // Audit event with the actor breadcrumb.
        _publisher.Events.Should().ContainSingle(e => e.Type == "TENANT.MOVE.REQUESTED");
        var evt = _publisher.Events.Single(e => e.Type == "TENANT.MOVE.REQUESTED");
        evt.TenantId.Should().Be(tenantId);
        evt.Data.Should().Contain(targetId.ToString("D"));
    }

    // ── GET /api/admin/tenants/{id}/move ──

    [Test]
    public async Task GetTenantMove_TenantNotFound_Returns404()
    {
        var result = await AdminTenantsEndpoints.GetTenantMove(Guid.NewGuid(), _db);
        StatusCodeOf(result).Should().Be(StatusCodes.Status404NotFound);
    }

    [Test]
    public async Task GetTenantMove_ReportsStatusFailureReasonAndPlacement()
    {
        var dbId = await SeedDatabaseRowAsync();
        var tenantId = await SeedTenantAsync(
            status: "draining",
            databaseId: dbId,
            schemaName: "t_abc",
            failureReason: "InvalidOperationException: restore verify mismatch");

        var result = await AdminTenantsEndpoints.GetTenantMove(tenantId, _db);

        var ok = result.Should().BeOfType<Ok<AdminTenantMoveStatusResponse>>().Subject;
        ok.Value!.TenantId.Should().Be(tenantId);
        ok.Value.Status.Should().Be("draining");
        ok.Value.FailureReason.Should()
            .Be("InvalidOperationException: restore verify mismatch");
        ok.Value.DatabaseId.Should().Be(dbId);
        ok.Value.SchemaName.Should().Be("t_abc");
    }
}

/// <summary>
/// Unified-tenancy Phase 4 Task 4 — auth gate for the move endpoints.
/// Production-environment factory so the real <c>PlatformOwnerAccess</c>
/// policy applies (the shared fixture's permissive-dev branch bypasses
/// authorization). Mirrors <see cref="AdminTenantDatabasesAuthTests"/>.
/// </summary>
[TestFixture]
public class AdminTenantMoveAuthTests
{
    private const string JwtSecret = "tenant-move-auth-secret-32-chars-xxxxxxx";
    private static readonly SymmetricSecurityKey SigningKey =
        new(Encoding.UTF8.GetBytes(JwtSecret));

    private WebApplicationFactory<Program> _factory = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        Environment.SetEnvironmentVariable("Jwt__Secret", JwtSecret);
        Environment.SetEnvironmentVariable("Jwt__Issuer", "tamma");
        Environment.SetEnvironmentVariable("Jwt__Audience", "tamma-api");
        Environment.SetEnvironmentVariable("Cranl__ApiKey", null);
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__DefaultConnection",
            ApiTestFixture.Postgres.GetConnectionString());
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__TammaDb",
            ApiTestFixture.Postgres.GetConnectionString());
        Environment.SetEnvironmentVariable(
            "ConnectionStrings__ControlPlane",
            ApiTestFixture.Postgres.GetConnectionString());
        Environment.SetEnvironmentVariable(
            "Cranl__EncryptionKey",
            Convert.ToBase64String(new byte[32]));

        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Production");
                b.DisableAlertHostedServices();
            });
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _factory?.Dispose();
        Environment.SetEnvironmentVariable("Jwt__Secret", null);
        Environment.SetEnvironmentVariable("Jwt__Issuer", null);
        Environment.SetEnvironmentVariable("Jwt__Audience", null);
        Environment.SetEnvironmentVariable("ConnectionStrings__ControlPlane", null);
        Environment.SetEnvironmentVariable("Cranl__EncryptionKey", null);
    }

    private static string MintToken(string role, string platformRole)
    {
        var jwt = new JwtSecurityToken(
            issuer: "tamma",
            audience: "tamma-api",
            claims: new[]
            {
                new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()),
                new Claim("tenantId", Guid.NewGuid().ToString()),
                new Claim("role", role),
                new Claim("platformRole", platformRole),
                new Claim(JwtRegisteredClaimNames.Email, "actor@example.com"),
                new Claim("name", "Actor"),
                new Claim("authMethod", "email"),
                new Claim("tenants", "[]"),
            },
            expires: DateTime.UtcNow.AddMinutes(15),
            signingCredentials: new SigningCredentials(
                SigningKey, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    private HttpClient ClientWith(string role, string platformRole)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", MintToken(role, platformRole));
        return client;
    }

    [Test]
    public async Task MemberRole_Returns403_OnMovePost()
    {
        using var client = ClientWith(role: "member", platformRole: "user");
        var response = await client.PostAsJsonAsync(
            $"/api/admin/tenants/{Guid.NewGuid()}/move",
            new { targetDatabaseId = Guid.NewGuid() });
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "a member-role user must not be able to queue a tenant move");
    }

    [Test]
    public async Task MemberRole_Returns403_OnMoveGet()
    {
        using var client = ClientWith(role: "member", platformRole: "user");
        var response = await client.GetAsync(
            $"/api/admin/tenants/{Guid.NewGuid()}/move");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task TenantOwner_WithoutPlatformAdmin_Returns403_OnMovePost()
    {
        // Story 28-R2 C1 analogue — every signed-up user is owner of their
        // personal tenant; that must NOT clear the platform gate.
        using var client = ClientWith(role: "owner", platformRole: "user");
        var response = await client.PostAsJsonAsync(
            $"/api/admin/tenants/{Guid.NewGuid()}/move",
            new { targetDatabaseId = Guid.NewGuid() });
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task PlatformAdmin_ClearsGate_OnMoveGet()
    {
        using var client = ClientWith(role: "member", platformRole: "platform_admin");
        var response = await client.GetAsync(
            $"/api/admin/tenants/{Guid.NewGuid()}/move");
        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }
}
