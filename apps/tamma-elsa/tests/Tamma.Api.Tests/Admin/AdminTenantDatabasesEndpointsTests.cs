using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using NUnit.Framework;
using Tamma.Api.Dtos.Admin;
using Tamma.Api.Endpoints.Admin;
using Tamma.Api.Tests.Infrastructure;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Pooling;
using Tamma.Data.Seeders;

namespace Tamma.Api.Tests.Admin;

/// <summary>
/// Unified-tenancy Phase 4 Task 2 — handler-direct tests for
/// <see cref="AdminTenantDatabasesEndpoints"/> against the REAL Postgres
/// container (<see cref="ApiTestFixture"/>): the CRUD touches shadow
/// columns, the live <c>SELECT 1</c> reachability probe, the AES-GCM
/// protector and the singleton <see cref="ITenantDatabasePool"/> decrypt
/// cache — none of which the EF InMemory provider exercises.
///
/// <para>Coverage: CRUD happy paths, 409 duplicate label, 409
/// delete-with-tenants (tenant provisioned onto the row through the real
/// pipeline), 422 unreachable connection string, the
/// secret-never-serialised invariant (asserted on actual JSON), and
/// PATCH conn-string rotation evicting the pool's decrypt cache
/// (observable: <c>GetAdminConnectionStringAsync</c> returns the NEW
/// string after rotation). The member-role 403 gate lives in
/// <see cref="AdminTenantDatabasesAuthTests"/> below (production-mode
/// factory — the shared fixture's permissive-dev branch bypasses
/// policies).</para>
/// </summary>
[TestFixture]
public class AdminTenantDatabasesEndpointsTests
{
    private IServiceScope _scope = null!;
    private ControlPlaneDbContext _db = null!;
    private ITenantConnectionStringProtector _protector = null!;
    private ITenantDatabasePool _pool = null!;
    private TimeProvider _timeProvider = null!;

    [SetUp]
    public async Task SetUp()
    {
        await ApiTestFixture.ResetDatabaseAsync();
        _scope = ApiTestFixture.Factory.Services.CreateScope();
        _db = _scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        _protector = _scope.ServiceProvider
            .GetRequiredService<ITenantConnectionStringProtector>();
        // Singleton — the SAME instance the production handlers use, so the
        // rotation test's cache-eviction observable is the real thing.
        _pool = _scope.ServiceProvider.GetRequiredService<ITenantDatabasePool>();
        _timeProvider = _scope.ServiceProvider.GetRequiredService<TimeProvider>();
    }

    [TearDown]
    public void TearDown()
    {
        // _db is owned by the scope, but NUnit1032 wants the explicit
        // Dispose on the field; harmless double-dispose.
        _db.Dispose();
        _scope.Dispose();
    }

    /// <summary>A reachable admin connection string (the CP container).</summary>
    private static string ReachableConnString =>
        ApiTestFixture.Postgres.GetConnectionString();

    /// <summary>A second reachable string (the tenant container) — distinct
    /// Host/Port from the CP container, for parse assertions.</summary>
    private static string SecondReachableConnString =>
        ApiTestFixture.TenantPostgres.GetConnectionString();

    /// <summary>Closed port on loopback — connection refused fast.</summary>
    private const string UnreachableConnString =
        "Host=127.0.0.1;Port=1;Database=nope;Username=nobody;Password=wrong;Timeout=2";

    /// <summary>
    /// A reachable conn string to a FRESH physical database on the CP
    /// container. The seeded central row already points at the container's
    /// main database, and the duplicate-physical-database guard 409s any
    /// row aliasing an existing (Host, Port, Database) — so each test row
    /// gets its own database.
    /// </summary>
    private static async Task<string> UniqueReachableConnStringAsync()
    {
        var name = $"pool_{Guid.NewGuid():N}";
        await using var conn = new NpgsqlConnection(ApiTestFixture.Postgres.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE {name}";
        await cmd.ExecuteNonQueryAsync();
        return new NpgsqlConnectionStringBuilder(ApiTestFixture.Postgres.GetConnectionString())
        {
            Database = name,
        }.ConnectionString;
    }

    private async Task<AdminTenantDatabaseListItem> CreateRowAsync(
        string label, string? connString = null)
    {
        var result = await AdminTenantDatabasesEndpoints.CreateDatabase(
            new CreateTenantDatabaseRequest(
                label, connString ?? await UniqueReachableConnStringAsync(),
                PlacementClass: "shared", TierEligibility: ["free", "team"]),
            _db, _protector, _pool, _timeProvider);
        var created = result.Should()
            .BeOfType<Created<AdminTenantDatabaseListItem>>().Subject;
        return created.Value!;
    }

    private static int StatusCodeOf(IResult result)
    {
        if (result is IStatusCodeHttpResult s && s.StatusCode.HasValue)
            return s.StatusCode.Value;
        throw new InvalidOperationException(
            $"Result type {result.GetType().FullName} does not expose a status code.");
    }

    // ── List ──

    [Test]
    public async Task ListDatabases_ReturnsSeededCentralRow_WithoutConnString()
    {
        var result = await AdminTenantDatabasesEndpoints.ListDatabases(_db);

        var ok = result.Should().BeOfType<Ok<AdminTenantDatabaseListResponse>>().Subject;
        ok.Value!.Total.Should().Be(1, "ResetDatabaseAsync reseeds exactly the central row");
        var central = ok.Value.Databases[0];
        central.Id.Should().Be(TenantDatabasesSeeder.CentralDatabaseId);
        central.Label.Should().Be("central");
        central.PlacementClass.Should().Be("shared");
        central.Status.Should().Be("active");
        central.KekVersion.Should().BeGreaterThanOrEqualTo(1);
    }

    // ── Create ──

    [Test]
    public async Task CreateDatabase_HappyPath_EncryptsAtRest_ParsesHostPortFromConnString()
    {
        var connString = SecondReachableConnString;
        var expected = new NpgsqlConnectionStringBuilder(connString);

        var item = await CreateRowAsync("shared-eu-1", connString);

        // Host/Port were parsed FROM the connection string — there are no
        // body fields for them, so no mismatch is possible.
        item.Host.Should().Be(expected.Host);
        item.Port.Should().Be(expected.Port);
        item.PlacementClass.Should().Be("shared");
        item.TierEligibility.Should().BeEquivalentTo("free", "team");
        item.TenantCount.Should().Be(0);
        item.Status.Should().Be("active");
        item.KekVersion.Should().Be(_protector.CurrentKekVersion);

        // At rest: an AES-GCM envelope, not the plaintext.
        var row = await _db.TenantDatabases.AsNoTracking()
            .FirstAsync(d => d.Id == item.Id);
        row.AdminConnectionStringEncrypted.Should().NotBeEmpty();
        Encoding.UTF8.GetString(row.AdminConnectionStringEncrypted)
            .Should().NotContain(expected.Password!,
                "the envelope must not embed the plaintext password");

        // Round-trip through the production pool decryptor.
        (await _pool.GetAdminConnectionStringAsync(item.Id)).Should().Be(connString);
    }

    [Test]
    public async Task CreateDatabase_DuplicateLabel_Returns409()
    {
        // "central" is seeded by ResetDatabaseAsync.
        var result = await AdminTenantDatabasesEndpoints.CreateDatabase(
            new CreateTenantDatabaseRequest("central", ReachableConnString),
            _db, _protector, _pool, _timeProvider);

        StatusCodeOf(result).Should().Be(StatusCodes.Status409Conflict);
    }

    [Test]
    public async Task CreateDatabase_UnreachableConnString_Returns422_WithNpgsqlError()
    {
        var result = await AdminTenantDatabasesEndpoints.CreateDatabase(
            new CreateTenantDatabaseRequest("dead-pool", UnreachableConnString),
            _db, _protector, _pool, _timeProvider);

        StatusCodeOf(result).Should().Be(StatusCodes.Status422UnprocessableEntity);
        // The Npgsql error must surface for the operator.
        var json = JsonSerializer.Serialize(
            ((IValueHttpResult)result).Value,
            ((IValueHttpResult)result).Value!.GetType());
        json.Should().Contain("database_unreachable");
        json.Should().Contain("detail");

        (await _db.TenantDatabases.CountAsync(d => d.Label == "dead-pool"))
            .Should().Be(0, "an unreachable row must not be persisted");
    }

    [Test]
    public async Task CreateDatabase_InvalidPlacementClass_Returns400()
    {
        var result = await AdminTenantDatabasesEndpoints.CreateDatabase(
            new CreateTenantDatabaseRequest(
                "weird", await UniqueReachableConnStringAsync(), PlacementClass: "exotic"),
            _db, _protector, _pool, _timeProvider);

        StatusCodeOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task CreateDatabase_UnparsableConnString_Returns400()
    {
        var result = await AdminTenantDatabasesEndpoints.CreateDatabase(
            new CreateTenantDatabaseRequest("garbled", "this is ;;= not a conn string=="),
            _db, _protector, _pool, _timeProvider);

        StatusCodeOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task CreateDatabase_AliasingExistingPhysicalDatabase_Returns409()
    {
        // The seeded central row already points at the CP container's main
        // database. A second row with the same (Host, Port, Database) —
        // even with cosmetically different connection-string text — would
        // let a tenant move between the two rows drop the live schema
        // (TenantMoveService aliasing hazard), so registration must bounce.
        var alias = new NpgsqlConnectionStringBuilder(ReachableConnString)
        {
            ApplicationName = "sneaky-alias",
        }.ConnectionString;
        alias.Should().NotBe(ReachableConnString,
            "the guard must compare the parsed physical identity, not the raw text");

        var result = await AdminTenantDatabasesEndpoints.CreateDatabase(
            new CreateTenantDatabaseRequest("alias-of-central", alias),
            _db, _protector, _pool, _timeProvider);

        StatusCodeOf(result).Should().Be(StatusCodes.Status409Conflict);
        var json = JsonSerializer.Serialize(
            ((IValueHttpResult)result).Value,
            ((IValueHttpResult)result).Value!.GetType());
        json.Should().Contain("duplicate_physical_database");
        json.Should().Contain("central", "the conflicting row must be named");

        (await _db.TenantDatabases.CountAsync(d => d.Label == "alias-of-central"))
            .Should().Be(0, "an aliasing row must not be persisted");
    }

    // ── Detail (tenant→DB view) ──

    [Test]
    public async Task GetDatabaseDetail_ListsTenantsPlacedOnTheRow()
    {
        var tenantId = Guid.NewGuid();
        _db.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Name = $"PoolView {tenantId:N}",
            Slug = $"poolview-{tenantId:N}",
            Plan = "free",
        });
        await _db.SaveChangesAsync();
        await ApiTestFixture.ProvisionTenantAsync(tenantId);

        var result = await AdminTenantDatabasesEndpoints.GetDatabaseDetail(
            TenantDatabasesSeeder.CentralDatabaseId, _db);

        var ok = result.Should()
            .BeOfType<Ok<AdminTenantDatabaseDetailResponse>>().Subject;
        ok.Value!.Database.Id.Should().Be(TenantDatabasesSeeder.CentralDatabaseId);
        var placed = ok.Value.Tenants.Should()
            .ContainSingle(t => t.Id == tenantId).Subject;
        placed.Slug.Should().Be($"poolview-{tenantId:N}");
        placed.SchemaName.Should().Be(TenantNaming.SchemaName(tenantId),
            "the detail view surfaces the SchemaName shadow column");
        placed.Status.Should().Be("active");
    }

    [Test]
    public async Task GetDatabaseDetail_UnknownId_Returns404()
    {
        var result = await AdminTenantDatabasesEndpoints.GetDatabaseDetail(
            Guid.NewGuid(), _db);

        StatusCodeOf(result).Should().Be(StatusCodes.Status404NotFound);
    }

    // ── Patch ──

    [Test]
    public async Task UpdateDatabase_PatchesMutableFields()
    {
        var item = await CreateRowAsync("patch-me");

        var result = await AdminTenantDatabasesEndpoints.UpdateDatabase(
            item.Id,
            new UpdateTenantDatabaseRequest(
                Label: "patched",
                TierEligibility: ["enterprise"],
                TenantCapacity: 42,
                Status: "draining"),
            _db, _protector, _pool, _timeProvider);

        var ok = result.Should().BeOfType<Ok<AdminTenantDatabaseListItem>>().Subject;
        ok.Value!.Label.Should().Be("patched");
        ok.Value.TierEligibility.Should().BeEquivalentTo("enterprise");
        ok.Value.TenantCapacity.Should().Be(42);
        ok.Value.Status.Should().Be("draining");

        var row = await _db.TenantDatabases.AsNoTracking()
            .FirstAsync(d => d.Id == item.Id);
        row.Status.Should().Be("draining");
        row.UpdatedAt.Should().BeOnOrAfter(item.UpdatedAt);
    }

    [Test]
    public async Task UpdateDatabase_DuplicateLabel_Returns409()
    {
        var item = await CreateRowAsync("renaming");

        var result = await AdminTenantDatabasesEndpoints.UpdateDatabase(
            item.Id,
            new UpdateTenantDatabaseRequest(Label: "central"),
            _db, _protector, _pool, _timeProvider);

        StatusCodeOf(result).Should().Be(StatusCodes.Status409Conflict);
    }

    [Test]
    public async Task UpdateDatabase_InvalidStatus_Returns400()
    {
        var item = await CreateRowAsync("status-check");

        var result = await AdminTenantDatabasesEndpoints.UpdateDatabase(
            item.Id,
            new UpdateTenantDatabaseRequest(Status: "exploded"),
            _db, _protector, _pool, _timeProvider);

        StatusCodeOf(result).Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task UpdateDatabase_UnknownId_Returns404()
    {
        var result = await AdminTenantDatabasesEndpoints.UpdateDatabase(
            Guid.NewGuid(),
            new UpdateTenantDatabaseRequest(Label: "ghost"),
            _db, _protector, _pool, _timeProvider);

        StatusCodeOf(result).Should().Be(StatusCodes.Status404NotFound);
    }

    [Test]
    public async Task UpdateDatabase_RotateConnString_EvictsPoolDecryptCache()
    {
        var original = await UniqueReachableConnStringAsync();
        var item = await CreateRowAsync("rotate-me", original);

        // Warm the singleton pool's decrypt cache with the ORIGINAL string.
        (await _pool.GetAdminConnectionStringAsync(item.Id)).Should().Be(original);

        // Rotate to a NEW valid string pointing at the same server (distinct
        // text via ApplicationName so a stale cache is detectable).
        var rotated = new NpgsqlConnectionStringBuilder(original)
        {
            ApplicationName = "tamma-rotated-probe",
        }.ConnectionString;
        rotated.Should().NotBe(original);

        var result = await AdminTenantDatabasesEndpoints.UpdateDatabase(
            item.Id,
            new UpdateTenantDatabaseRequest(AdminConnectionString: rotated),
            _db, _protector, _pool, _timeProvider);
        result.Should().BeOfType<Ok<AdminTenantDatabaseListItem>>();

        // The observable: without the evict, the pool would still return
        // the cached ORIGINAL string.
        (await _pool.GetAdminConnectionStringAsync(item.Id)).Should().Be(rotated,
            "PATCH conn-string rotation must evict the TenantDatabasePool decrypt cache");

        var row = await _db.TenantDatabases.AsNoTracking()
            .FirstAsync(d => d.Id == item.Id);
        row.KekVersion.Should().Be((short)_protector.CurrentKekVersion,
            "rotation re-stamps the current KEK version");
    }

    [Test]
    public async Task UpdateDatabase_RotateToUnreachableConnString_Returns422_AndKeepsOldEnvelope()
    {
        var original = await UniqueReachableConnStringAsync();
        var item = await CreateRowAsync("rotate-guard", original);

        var result = await AdminTenantDatabasesEndpoints.UpdateDatabase(
            item.Id,
            new UpdateTenantDatabaseRequest(AdminConnectionString: UnreachableConnString),
            _db, _protector, _pool, _timeProvider);

        StatusCodeOf(result).Should().Be(StatusCodes.Status422UnprocessableEntity);
        (await _pool.GetAdminConnectionStringAsync(item.Id)).Should().Be(original,
            "a failed rotation must leave the old envelope in place");
    }

    [Test]
    public async Task UpdateDatabase_RotateToAliasOfAnotherRow_Returns409_AndKeepsOldEnvelope()
    {
        // Rotating a row's conn string so it points at ANOTHER row's
        // physical database creates the same move-hazard alias as create —
        // 409. (Re-pointing a row at ITSELF with new credentials is the
        // normal rotation case and stays allowed — covered by
        // UpdateDatabase_RotateConnString_EvictsPoolDecryptCache.)
        var original = await UniqueReachableConnStringAsync();
        var item = await CreateRowAsync("alias-rotate", original);

        var result = await AdminTenantDatabasesEndpoints.UpdateDatabase(
            item.Id,
            new UpdateTenantDatabaseRequest(AdminConnectionString: ReachableConnString),
            _db, _protector, _pool, _timeProvider);

        StatusCodeOf(result).Should().Be(StatusCodes.Status409Conflict);
        var json = JsonSerializer.Serialize(
            ((IValueHttpResult)result).Value,
            ((IValueHttpResult)result).Value!.GetType());
        json.Should().Contain("duplicate_physical_database");
        (await _pool.GetAdminConnectionStringAsync(item.Id)).Should().Be(original,
            "a rejected rotation must leave the old envelope in place");
    }

    // ── Delete ──

    [Test]
    public async Task DeleteDatabase_WithZeroTenants_Returns204_AndHardDeletes()
    {
        var item = await CreateRowAsync("delete-me");

        var result = await AdminTenantDatabasesEndpoints.DeleteDatabase(
            item.Id, _db, _pool);

        result.Should().BeOfType<NoContent>();
        (await _db.TenantDatabases.CountAsync(d => d.Id == item.Id)).Should().Be(0,
            "delete is a hard delete (zero-data project)");
    }

    [Test]
    public async Task DeleteDatabase_WithTenantsPlacedOnRow_Returns409()
    {
        var tenantId = Guid.NewGuid();
        _db.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Name = $"Blocker {tenantId:N}",
            Slug = $"blocker-{tenantId:N}",
            Plan = "free",
        });
        await _db.SaveChangesAsync();
        // Real pipeline: placement stamps DatabaseId=central + TenantCount++.
        await ApiTestFixture.ProvisionTenantAsync(tenantId);

        var result = await AdminTenantDatabasesEndpoints.DeleteDatabase(
            TenantDatabasesSeeder.CentralDatabaseId, _db, _pool);

        StatusCodeOf(result).Should().Be(StatusCodes.Status409Conflict);
        (await _db.TenantDatabases
            .CountAsync(d => d.Id == TenantDatabasesSeeder.CentralDatabaseId))
            .Should().Be(1, "the row must survive the rejected delete");
    }

    [Test]
    public async Task DeleteDatabase_UnknownId_Returns404()
    {
        var result = await AdminTenantDatabasesEndpoints.DeleteDatabase(
            Guid.NewGuid(), _db, _pool);

        StatusCodeOf(result).Should().Be(StatusCodes.Status404NotFound);
    }

    // ── Secret-never-serialised invariant ──

    [Test]
    public async Task Responses_NeverSerialiseAdminConnectionString()
    {
        var connString = await UniqueReachableConnStringAsync();
        var password = new NpgsqlConnectionStringBuilder(connString).Password!;
        var item = await CreateRowAsync("sealed-row", connString);

        // Serialise every response shape the endpoints emit and assert the
        // plaintext, the password, and any conn-string-shaped key are absent.
        var listResult = await AdminTenantDatabasesEndpoints.ListDatabases(_db);
        var detailResult = await AdminTenantDatabasesEndpoints.GetDatabaseDetail(item.Id, _db);
        var patchResult = await AdminTenantDatabasesEndpoints.UpdateDatabase(
            item.Id, new UpdateTenantDatabaseRequest(TenantCapacity: 7),
            _db, _protector, _pool, _timeProvider);

        foreach (var result in new IResult[] { listResult, detailResult, patchResult })
        {
            var value = ((IValueHttpResult)result).Value!;
            var json = JsonSerializer.Serialize(value, value.GetType(),
                new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            json.Should().NotContain(password,
                "no response may carry the admin password");
            json.Should().NotContain(connString,
                "no response may carry the plaintext connection string");
            json.ToLowerInvariant().Should().NotContain("connectionstring",
                "no response may even have a connection-string-shaped field");
        }

        // Compile-time-ish guard: the DTOs expose no byte[] (envelope) and no
        // conn-string property — mirrors AdminTenantsTests' DTO assertion.
        foreach (var dto in new[]
        {
            typeof(AdminTenantDatabaseListItem),
            typeof(AdminTenantDatabaseDetailResponse),
            typeof(AdminTenantDatabaseTenantItem),
            typeof(AdminTenantDatabaseListResponse),
        })
        {
            dto.GetProperties().Should().NotContain(
                p => p.PropertyType == typeof(byte[])
                    || p.Name.Contains("ConnectionString"),
                $"{dto.Name} must never expose the envelope or a connection string");
        }
    }
}

/// <summary>
/// Phase 4 Task 2 — policy-gate verification for the tenant-databases admin
/// surface. The shared <see cref="ApiTestFixture"/> runs the permissive-dev
/// auth branch (every policy is AllowAnonymous), so this fixture mirrors
/// <see cref="Auth.PlatformOwnerAccessPolicyTests"/>: an isolated
/// production-mode factory with a real JWT pipeline, proving a member-role
/// (non-platform-admin) caller gets 403 and a platform admin clears the gate.
/// </summary>
[TestFixture]
public class AdminTenantDatabasesAuthTests
{
    private const string JwtSecret = "tenant-databases-auth-secret-32-chars-xx";
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
    public async Task MemberRole_Returns403_OnList()
    {
        using var client = ClientWith(role: "member", platformRole: "user");
        var response = await client.GetAsync("/api/admin/tenant-databases");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "a member-role user must not reach the tenant-databases pool CRUD");
    }

    [Test]
    public async Task MemberRole_Returns403_OnDelete()
    {
        using var client = ClientWith(role: "member", platformRole: "user");
        var response = await client.DeleteAsync(
            $"/api/admin/tenant-databases/{Guid.NewGuid()}");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task TenantOwner_WithoutPlatformAdmin_Returns403_OnList()
    {
        // Story 28-R2 C1 analogue — every signed-up user is owner of their
        // personal tenant; that must NOT clear the platform gate.
        using var client = ClientWith(role: "owner", platformRole: "user");
        var response = await client.GetAsync("/api/admin/tenant-databases");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task PlatformAdmin_ClearsGate_OnList()
    {
        using var client = ClientWith(role: "member", platformRole: "platform_admin");
        var response = await client.GetAsync("/api/admin/tenant-databases");
        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }
}
