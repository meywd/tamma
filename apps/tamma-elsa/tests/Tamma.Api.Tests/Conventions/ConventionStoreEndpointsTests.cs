using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NUnit.Framework;
using Tamma.Api.Auth;
using Tamma.Api.Dtos.Conventions;
using Tamma.Api.Endpoints;
using Tamma.Api.Services.Agents;
using Tamma.Api.Services.Conventions;
using Tamma.Api.Services.PromptStore;
using Tamma.Data;
using Tamma.Data.Pooling;
using Tamma.Data.Repositories;
using Testcontainers.PostgreSql;

namespace Tamma.Api.Tests.Conventions;

/// <summary>
/// Story 27-10 — endpoint-level coverage for <see cref="ConventionStoreEndpoints"/>.
///
/// <para>Mirrors <c>PromptEndpointsTenantAdminTests</c> (direct invocation of the
/// public endpoint delegates against constructed <see cref="ClaimsPrincipal"/> /
/// <see cref="ITenantContext"/> / <see cref="ITammaModeProvider"/> stubs) and the
/// <c>ConventionStoreTests</c> Postgres-testcontainer setup (the store needs a
/// real DB for <c>NULLS NOT DISTINCT</c> + the seeded system-default rows).</para>
///
/// <para>HTTP-level RBAC (the <c>ConventionManage</c> / <c>PlatformOwnerAccess</c>
/// policies returning 403) cannot be exercised through direct invocation, so the
/// permission contract is pinned separately in
/// <see cref="ConventionManagePermissionTests"/>.</para>
/// </summary>
[TestFixture]
public class ConventionStoreEndpointsTests
{
    private const AgentRole Role = AgentRole.Developer;
    private const AgentAction Action = AgentAction.ImplementFeature;
    private static readonly string RoleWire = Role.ToWire();
    private static readonly string ActionWire = Action.ToWire();

    private PostgreSqlContainer _postgres = null!;
    private string _connectionString = null!;
    private NpgsqlDataSource _dataSource = null!;
    private StubTenantConnectionResolver _resolver = null!;

    private static readonly Guid AmbientTenant =
        Guid.Parse("bbbbbbbb-1111-2222-3333-bbbbbbbbbbbb");

    private static int ExpectedCellCount =>
        RolePhaseMap.EligibleActions.Sum(kv => kv.Value.Count);

    [OneTimeSetUp]
    public async Task OneTimeSetUp()
    {
        _postgres = new PostgreSqlBuilder()
            .WithImage("postgres:17-alpine")
            .WithDatabase("convention_endpoints_test")
            .WithUsername("tamma")
            .WithPassword("tamma")
            .Build();
        await _postgres.StartAsync();
        _connectionString = _postgres.GetConnectionString();

        await using (var ext = new NpgsqlConnection(_connectionString))
        {
            await ext.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "CREATE EXTENSION IF NOT EXISTS \"uuid-ossp\";"
              + "CREATE EXTENSION IF NOT EXISTS \"pgcrypto\";",
                ext);
            await cmd.ExecuteNonQueryAsync();
        }

        var migrator = new EfTenantDbMigrator();
        await migrator.MigrateTenantAppAsync(_connectionString);

        _dataSource = NpgsqlDataSource.Create(_connectionString);
        _resolver = new StubTenantConnectionResolver(_dataSource);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        await _resolver.DisposeAsync();
        await _dataSource.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    [SetUp]
    public async Task SeedSystemDefaults()
    {
        await using (var conn = new NpgsqlConnection(_connectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("TRUNCATE TABLE conventions;", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        var seeder = new ConventionStoreSeeder(
            _resolver, TimeProvider.System,
            NullLogger<ConventionStoreSeeder>.Instance);
        await using var db = NewContext();
        await seeder.SeedAsync(db, default);
    }

    private TenantDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseNpgsql(_dataSource, npgsql =>
                npgsql.MigrationsHistoryTable("__TenantMigrationsHistory"))
            .Options;
        return new TenantDbContext(options);
    }

    private ConventionStore NewStore()
    {
        var factory = new TenantDbContextFactory(_resolver);
        var tc = new TenantContext();
        tc.SetTenantId(AmbientTenant);
        var repo = new ConventionRepository(factory, tc);
        return new ConventionStore(repo);
    }

    private static TenantContext TenantCtx(Guid? tenantId)
    {
        var tc = new TenantContext();
        if (tenantId is { } id) tc.SetTenantId(id);
        else tc.SetTenantId(AmbientTenant); // ambient DB-routing id even in single-user
        return tc;
    }

    private static ClaimsPrincipal Principal(Guid userId, string? role = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()) };
        if (role is not null) claims.Add(new Claim(ClaimTypes.Role, role));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private static ITammaModeProvider Mode(TammaMode mode) => new StubModeProvider(mode);


    private static ConventionEventsService NoOpEvents()
    {
        var nullRepo = new NullConventionEventRepository();
        return new ConventionEventsService(nullRepo);
    }

    private sealed class NullConventionEventRepository : Tamma.Data.Repositories.IEventRepository
    {
        public Task<Tamma.Data.Entities.DomainEvent> AppendAsync(Tamma.Data.Entities.DomainEvent evt)
            => Task.FromResult(evt);
        public Task<Tamma.Data.Entities.DomainEvent?> GetByIdAsync(Guid id)
            => Task.FromResult<Tamma.Data.Entities.DomainEvent?>(null);
        public Task<List<Tamma.Data.Entities.DomainEvent>> QueryAsync(Guid? tenantId, string? type, int? issueNumber, int limit)
            => Task.FromResult(new List<Tamma.Data.Entities.DomainEvent>());
        public Task<Tamma.Data.Entities.DomainEvent?> GetLastByTypeAsync(Guid tenantId, string type)
            => Task.FromResult<Tamma.Data.Entities.DomainEvent?>(null);
        public Task ClearAsync(Guid tenantId)
            => Task.CompletedTask;
        public Task<(IReadOnlyList<Tamma.Data.Entities.DomainEvent> Events, int Total)> ListByTenantAsync(
            Guid tenantId, string? typePrefix, int limit, int offset)
            => Task.FromResult<(IReadOnlyList<Tamma.Data.Entities.DomainEvent>, int)>((new List<Tamma.Data.Entities.DomainEvent>(), 0));
        public Task<(IReadOnlyList<Tamma.Data.Entities.DomainEvent> Events, int Total)> QueryWithPaginationAsync(
            Guid? tenantId, string? type, int? issueNumber, int limit, int offset)
            => Task.FromResult<(IReadOnlyList<Tamma.Data.Entities.DomainEvent>, int)>((new List<Tamma.Data.Entities.DomainEvent>(), 0));
    }

    private sealed class StubModeProvider(TammaMode mode) : ITammaModeProvider
    {
        public TammaMode Mode { get; } = mode;
    }

    // -- IResult execution helpers ------------------------------------------

    private static async Task<(int Status, string Body)> ExecuteAsync(IResult result)
    {
        var services = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        var ctx = new DefaultHttpContext
        {
            RequestServices = services,
            Response = { Body = new MemoryStream() },
        };
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(ctx.Response.Body);
        var body = await reader.ReadToEndAsync();
        return (ctx.Response.StatusCode, body);
    }

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        })!;

    // ======================================================================
    // Merged list (AC: list with isOverride/source per cell)
    // ======================================================================

    [Test]
    public async Task ListAll_ReturnsMergedList_WithIsOverrideFlag()
    {
        var store = NewStore();
        var tenantId = Guid.NewGuid();
        await store.UpsertAsync(tenantId, Role, Action, "TENANT-LIST", enabled: true, Guid.NewGuid(), default);

        var result = await ConventionStoreEndpoints.ListAll(
            store, TenantCtx(tenantId), Mode(TammaMode.SaaS), default);

        var (status, body) = await ExecuteAsync(result);
        status.Should().Be(StatusCodes.Status200OK);

        var items = Deserialize<List<ConventionResponse>>(body);
        items.Should().HaveCount(ExpectedCellCount);

        var overridden = items.Single(i => i.Role == RoleWire && i.Action == ActionWire);
        overridden.Body.Should().Be("TENANT-LIST");
        overridden.IsOverride.Should().BeTrue();
        overridden.Source.Should().Be("tenant");

        items.Where(i => !(i.Role == RoleWire && i.Action == ActionWire))
            .Should().OnlyContain(i => i.Source == "system" && !i.IsOverride);
    }

    // ======================================================================
    // CRUD lifecycle: list/detail metadata consistency (Fix 1 guard)
    // ======================================================================

    /// <summary>
    /// Full CRUD lifecycle: upsert a tenant override → the list endpoint reports
    /// the CORRECT version/id/source (not hardcoded constants) → delete the
    /// override → the list falls back to system and reflects the real system
    /// metadata. Guards the Fix-1 metadata correctness (list/detail agreement).
    /// </summary>
    [Test]
    public async Task ListAll_AfterUpsert_ShowsRealVersionAndId_MatchingSingleItemGet()
    {
        var store = NewStore();
        var tenantId = Guid.NewGuid();
        var admin = Principal(Guid.NewGuid(), "admin");

        // 1. Create the override so it has a version bump (v1 is insert; a second
        //    upsert will produce v2 to prove we surface real version, not hardcoded 1).
        var req1 = new UpsertConventionRequest("V1-BODY", Enabled: true);
        await ExecuteAsync(await ConventionStoreEndpoints.UpsertTenantOverride(
            RoleWire, ActionWire, req1, store, NoOpEvents(), admin, TenantCtx(tenantId), Mode(TammaMode.SaaS), default));

        var req2 = new UpsertConventionRequest("V2-BODY", Enabled: true);
        await ExecuteAsync(await ConventionStoreEndpoints.UpsertTenantOverride(
            RoleWire, ActionWire, req2, store, NoOpEvents(), admin, TenantCtx(tenantId), Mode(TammaMode.SaaS), default));

        // 2. List must show isOverride:true, source:tenant, with the CORRECT version (2).
        var listResult = await ConventionStoreEndpoints.ListAll(
            store, TenantCtx(tenantId), Mode(TammaMode.SaaS), default);
        var (listStatus, listBody) = await ExecuteAsync(listResult);
        listStatus.Should().Be(StatusCodes.Status200OK);

        var listItems = Deserialize<List<ConventionResponse>>(listBody);
        var listCell = listItems.Single(i => i.Role == RoleWire && i.Action == ActionWire);
        listCell.IsOverride.Should().BeTrue();
        listCell.Source.Should().Be("tenant");
        listCell.Version.Should().Be(2, "list must surface real version, not hardcoded 1");
        listCell.Id.Should().NotBeNull("list must surface real row id, not null");

        // 3. Single-item GET must agree with the list on version and id.
        var singleResult = await ConventionStoreEndpoints.GetResolved(
            RoleWire, ActionWire, store, TenantCtx(tenantId), Mode(TammaMode.SaaS), default);
        var (singleStatus, singleBody) = await ExecuteAsync(singleResult);
        singleStatus.Should().Be(StatusCodes.Status200OK);

        var singleDto = Deserialize<ConventionResponse>(singleBody);
        singleDto.Version.Should().Be(listCell.Version, "list and detail version must agree");
        singleDto.Id.Should().Be(listCell.Id, "list and detail id must agree");

        // 4. Delete the override → list falls back to system (source:system, isOverride:false).
        var deleteResult = await ConventionStoreEndpoints.DeleteTenantOverride(
            RoleWire, ActionWire, store, NoOpEvents(), Principal(Guid.Empty), TenantCtx(tenantId), Mode(TammaMode.SaaS), default);
        var (deleteStatus, _) = await ExecuteAsync(deleteResult);
        deleteStatus.Should().Be(StatusCodes.Status204NoContent);

        var listAfterDelete = await ConventionStoreEndpoints.ListAll(
            store, TenantCtx(tenantId), Mode(TammaMode.SaaS), default);
        var (_, listAfterBody) = await ExecuteAsync(listAfterDelete);
        var listAfterItems = Deserialize<List<ConventionResponse>>(listAfterBody);

        var fallbackCell = listAfterItems.Single(i => i.Role == RoleWire && i.Action == ActionWire);
        fallbackCell.IsOverride.Should().BeFalse("after delete, cell falls back to system");
        fallbackCell.Source.Should().Be("system");
        fallbackCell.Id.Should().NotBeNull("system default has a real id too");
    }

    // ======================================================================
    // Get tenant-override vs system fallback
    // ======================================================================

    [Test]
    public async Task GetResolved_NoOverride_ReturnsSystemDefault()
    {
        var store = NewStore();
        var tenantId = Guid.NewGuid();

        var result = await ConventionStoreEndpoints.GetResolved(
            RoleWire, ActionWire, store, TenantCtx(tenantId), Mode(TammaMode.SaaS), default);

        var (status, body) = await ExecuteAsync(result);
        status.Should().Be(StatusCodes.Status200OK);

        var dto = Deserialize<ConventionResponse>(body);
        dto.Source.Should().Be("system");
        dto.IsOverride.Should().BeFalse();
        dto.Body.Should().Be(ConventionSeedSpecs.DefaultBody(RoleWire, ActionWire));
    }

    [Test]
    public async Task GetResolved_WithOverride_ReturnsTenantBody()
    {
        var store = NewStore();
        var tenantId = Guid.NewGuid();
        await store.UpsertAsync(tenantId, Role, Action, "TENANT-WINS", enabled: true, Guid.NewGuid(), default);

        var result = await ConventionStoreEndpoints.GetResolved(
            RoleWire, ActionWire, store, TenantCtx(tenantId), Mode(TammaMode.SaaS), default);

        var (status, body) = await ExecuteAsync(result);
        status.Should().Be(StatusCodes.Status200OK);

        var dto = Deserialize<ConventionResponse>(body);
        dto.Source.Should().Be("tenant");
        dto.IsOverride.Should().BeTrue();
        dto.Body.Should().Be("TENANT-WINS");
        dto.Id.Should().NotBeNull();
    }

    // ======================================================================
    // Tenant PUT creates override / PUT enabled:false / DELETE 204
    // ======================================================================

    [Test]
    public async Task UpsertTenantOverride_CreatesOverride()
    {
        var store = NewStore();
        var tenantId = Guid.NewGuid();
        var admin = Principal(Guid.NewGuid(), "admin");
        var req = new UpsertConventionRequest("TENANT-CREATED", Enabled: true);

        var result = await ConventionStoreEndpoints.UpsertTenantOverride(
            RoleWire, ActionWire, req, store, NoOpEvents(), admin, TenantCtx(tenantId), Mode(TammaMode.SaaS), default);

        var (status, body) = await ExecuteAsync(result);
        status.Should().Be(StatusCodes.Status200OK);

        var dto = Deserialize<ConventionResponse>(body);
        dto.Source.Should().Be("tenant");
        dto.IsOverride.Should().BeTrue();
        dto.Enabled.Should().BeTrue();

        var resolved = await store.ResolveAsync(tenantId, Role, Action, default);
        resolved.Source.Should().Be(ConventionSource.Tenant);
        resolved.Body.Should().Be("TENANT-CREATED");
    }

    [Test]
    public async Task UpsertTenantOverride_EnabledFalse_PersistsDisabled_AndFallsThroughToSystem()
    {
        var store = NewStore();
        var tenantId = Guid.NewGuid();
        var admin = Principal(Guid.NewGuid(), "owner");
        var req = new UpsertConventionRequest("TENANT-DISABLED", Enabled: false);

        var result = await ConventionStoreEndpoints.UpsertTenantOverride(
            RoleWire, ActionWire, req, store, NoOpEvents(), admin, TenantCtx(tenantId), Mode(TammaMode.SaaS), default);

        var (status, _) = await ExecuteAsync(result);
        status.Should().Be(StatusCodes.Status200OK);

        // Persisted with Enabled=false …
        await using (var db = NewContext())
        {
            var row = await db.Conventions.IgnoreQueryFilters()
                .FirstAsync(c => c.TenantId == tenantId && c.Role == RoleWire && c.Action == ActionWire);
            row.Enabled.Should().BeFalse();
        }

        // … so resolution falls through to the system default (AC9).
        var resolved = await store.ResolveAsync(tenantId, Role, Action, default);
        resolved.Source.Should().Be(ConventionSource.System);
        resolved.Body.Should().NotBe("TENANT-DISABLED");
    }

    [Test]
    public async Task UpsertTenantOverride_EnabledFalseThenReEnable_PersistsEnabled()
    {
        var store = NewStore();
        var tenantId = Guid.NewGuid();
        var admin = Principal(Guid.NewGuid(), "admin");

        await ExecuteAsync(await ConventionStoreEndpoints.UpsertTenantOverride(
            RoleWire, ActionWire, new UpsertConventionRequest("V1", Enabled: false),
            store, NoOpEvents(), admin, TenantCtx(tenantId), Mode(TammaMode.SaaS), default));

        await ExecuteAsync(await ConventionStoreEndpoints.UpsertTenantOverride(
            RoleWire, ActionWire, new UpsertConventionRequest("V2", Enabled: true),
            store, NoOpEvents(), admin, TenantCtx(tenantId), Mode(TammaMode.SaaS), default));

        var resolved = await store.ResolveAsync(tenantId, Role, Action, default);
        resolved.Source.Should().Be(ConventionSource.Tenant);
        resolved.Body.Should().Be("V2");
    }

    [Test]
    public async Task DeleteTenantOverride_Returns204_AndFallsBackToSystem()
    {
        var store = NewStore();
        var tenantId = Guid.NewGuid();
        await store.UpsertAsync(tenantId, Role, Action, "TO-DELETE", enabled: true, Guid.NewGuid(), default);

        var result = await ConventionStoreEndpoints.DeleteTenantOverride(
            RoleWire, ActionWire, store, NoOpEvents(), Principal(Guid.Empty), TenantCtx(tenantId), Mode(TammaMode.SaaS), default);

        var (status, _) = await ExecuteAsync(result);
        status.Should().Be(StatusCodes.Status204NoContent);

        var resolved = await store.ResolveAsync(tenantId, Role, Action, default);
        resolved.Source.Should().Be(ConventionSource.System);
    }

    [Test]
    public async Task DeleteTenantOverride_NoOverride_StillReturns204()
    {
        var store = NewStore();
        var tenantId = Guid.NewGuid();

        var result = await ConventionStoreEndpoints.DeleteTenantOverride(
            RoleWire, ActionWire, store, NoOpEvents(), Principal(Guid.Empty), TenantCtx(tenantId), Mode(TammaMode.SaaS), default);

        var (status, _) = await ExecuteAsync(result);
        status.Should().Be(StatusCodes.Status204NoContent);
    }

    // ======================================================================
    // Resolve — correct body + miss → 404 (NOT empty)
    // ======================================================================

    [Test]
    public async Task Resolve_ReturnsCorrectBody_AndSource()
    {
        var store = NewStore();
        var tenantId = Guid.NewGuid();
        await store.UpsertAsync(tenantId, Role, Action, "RESOLVE-TENANT", enabled: true, Guid.NewGuid(), default);

        var result = await ConventionStoreEndpoints.Resolve(
            new ResolveConventionRequest(RoleWire, ActionWire),
            store, TenantCtx(tenantId), Mode(TammaMode.SaaS), default);

        var (status, body) = await ExecuteAsync(result);
        status.Should().Be(StatusCodes.Status200OK);

        var dto = Deserialize<ResolvedConventionResponse>(body);
        dto.Body.Should().Be("RESOLVE-TENANT");
        dto.Source.Should().Be("tenant");
        dto.Role.Should().Be(RoleWire);
        dto.Action.Should().Be(ActionWire);
    }

    [Test]
    public async Task Resolve_Miss_Returns404_NotEmpty()
    {
        var store = NewStore();
        var tenantId = Guid.NewGuid();

        // Remove the system default so a taxonomy-valid cell has nothing to
        // resolve to — ResolveAsync throws CONVENTION_NOT_FOUND.
        await store.DeleteSystemDefaultAsync(Role, Action, default);

        var result = await ConventionStoreEndpoints.Resolve(
            new ResolveConventionRequest(RoleWire, ActionWire),
            store, TenantCtx(tenantId), Mode(TammaMode.SaaS), default);

        var (status, body) = await ExecuteAsync(result);
        status.Should().Be(StatusCodes.Status404NotFound, "miss must be 404, never an empty body");
        body.Should().NotBeNullOrWhiteSpace();
        body.Should().Contain("CONVENTION_NOT_FOUND");
    }

    // ======================================================================
    // Invalid / ineligible (role, action) → 400
    // ======================================================================

    [Test]
    public async Task GetResolved_UnknownRole_Returns400()
    {
        var store = NewStore();
        var result = await ConventionStoreEndpoints.GetResolved(
            "not-a-role", ActionWire, store, TenantCtx(null), Mode(TammaMode.SingleUser), default);

        var (status, body) = await ExecuteAsync(result);
        status.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("CONVENTION_INVALID_KEY");
    }

    [Test]
    public async Task GetResolved_IneligiblePair_Returns400()
    {
        // developer/deploy — deploy is devops-only, a known-but-ineligible pair.
        var store = NewStore();
        var result = await ConventionStoreEndpoints.GetResolved(
            "developer", "deploy", store, TenantCtx(null), Mode(TammaMode.SingleUser), default);

        var (status, body) = await ExecuteAsync(result);
        status.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("CONVENTION_INELIGIBLE_PAIR");
    }

    [Test]
    public async Task UpsertTenantOverride_EmptyBody_Returns400()
    {
        var store = NewStore();
        var admin = Principal(Guid.NewGuid(), "admin");

        var result = await ConventionStoreEndpoints.UpsertTenantOverride(
            RoleWire, ActionWire, new UpsertConventionRequest("   ", Enabled: true),
            store, NoOpEvents(), admin, TenantCtx(Guid.NewGuid()), Mode(TammaMode.SaaS), default);

        var (status, body) = await ExecuteAsync(result);
        status.Should().Be(StatusCodes.Status400BadRequest);
        body.Should().Contain("CONVENTION_BODY_REQUIRED");
    }

    // ======================================================================
    // Defaults list + one default
    // ======================================================================

    [Test]
    public async Task ListSystemDefaults_ReturnsEveryCell_AsSystem()
    {
        var store = NewStore();
        // A tenant override must NOT leak into the pure system-defaults view.
        await store.UpsertAsync(Guid.NewGuid(), Role, Action, "SHOULD-NOT-APPEAR", enabled: true, Guid.NewGuid(), default);

        var result = await ConventionStoreEndpoints.ListSystemDefaults(store, default);
        var (status, body) = await ExecuteAsync(result);
        status.Should().Be(StatusCodes.Status200OK);

        var items = Deserialize<List<ConventionResponse>>(body);
        items.Should().HaveCount(ExpectedCellCount);
        items.Should().OnlyContain(i => i.Source == "system" && !i.IsOverride);
        items.Should().NotContain(i => i.Body == "SHOULD-NOT-APPEAR");
    }

    [Test]
    public async Task GetSystemDefault_ReturnsTheSeededBody()
    {
        var store = NewStore();
        var result = await ConventionStoreEndpoints.GetSystemDefault(
            RoleWire, ActionWire, store, default);

        var (status, body) = await ExecuteAsync(result);
        status.Should().Be(StatusCodes.Status200OK);

        var dto = Deserialize<ResolvedConventionResponse>(body);
        dto.Source.Should().Be("system");
        dto.Body.Should().Be(ConventionSeedSpecs.DefaultBody(RoleWire, ActionWire));
    }

    // ======================================================================
    // Admin PUT / reset (platform-admin path — direct invocation)
    // ======================================================================

    [Test]
    public async Task UpsertSystemDefault_PersistsSystemRow()
    {
        var store = NewStore();
        var admin = Principal(Guid.NewGuid(), "owner");

        var result = await ConventionStoreEndpoints.UpsertSystemDefault(
            RoleWire, ActionWire, new UpsertConventionRequest("ADMIN-DEFAULT", Enabled: true),
            store, NoOpEvents(), admin, default);

        var (status, body) = await ExecuteAsync(result);
        status.Should().Be(StatusCodes.Status200OK);

        var dto = Deserialize<ConventionResponse>(body);
        dto.Source.Should().Be("system");
        dto.IsOverride.Should().BeFalse();

        var resolved = await store.ResolveAsync(Guid.NewGuid(), Role, Action, default);
        resolved.Source.Should().Be(ConventionSource.System);
        resolved.Body.Should().Be("ADMIN-DEFAULT");
    }

    [Test]
    public async Task UpsertSystemDefault_EnabledFalse_PersistsDisabled_AndResolveFails()
    {
        var store = NewStore();
        var admin = Principal(Guid.NewGuid(), "owner");

        var result = await ConventionStoreEndpoints.UpsertSystemDefault(
            RoleWire, ActionWire, new UpsertConventionRequest("ADMIN-DISABLED", Enabled: false),
            store, NoOpEvents(), admin, default);

        var (status, _) = await ExecuteAsync(result);
        status.Should().Be(StatusCodes.Status200OK);

        await using (var db = NewContext())
        {
            var row = await db.Conventions.IgnoreQueryFilters()
                .FirstAsync(c => c.TenantId == null && c.Role == RoleWire && c.Action == ActionWire);
            row.Enabled.Should().BeFalse();
        }

        // No enabled system default + no override → fail loud.
        var act = async () => await store.ResolveAsync(Guid.NewGuid(), Role, Action, default);
        await act.Should().ThrowAsync<Tamma.Core.TammaError>();
    }

    [Test]
    public async Task ResetSystemDefault_RestoresCodeBaseline_AndReEnables()
    {
        var store = NewStore();
        var admin = Principal(Guid.NewGuid(), "owner");
        var baseline = ConventionSeedSpecs.DefaultBody(RoleWire, ActionWire);

        // Admin disables + drifts the default first …
        await ExecuteAsync(await ConventionStoreEndpoints.UpsertSystemDefault(
            RoleWire, ActionWire, new UpsertConventionRequest("DRIFTED", Enabled: false),
            store, NoOpEvents(), admin, default));

        // … then reset restores the baseline and re-enables.
        var result = await ConventionStoreEndpoints.ResetSystemDefault(
            RoleWire, ActionWire, store, NoOpEvents(), admin, default);
        var (status, _) = await ExecuteAsync(result);
        status.Should().Be(StatusCodes.Status200OK);

        await using var db = NewContext();
        var row = await db.Conventions.IgnoreQueryFilters()
            .FirstAsync(c => c.TenantId == null && c.Role == RoleWire && c.Action == ActionWire);
        row.Body.Should().Be(baseline);
        row.Enabled.Should().BeTrue("reset is a canonical restore — it re-enables");
    }

    [Test]
    public async Task DeleteSystemDefault_RemovesRow_Returns204()
    {
        var store = NewStore();
        var result = await ConventionStoreEndpoints.DeleteSystemDefault(
            RoleWire, ActionWire, store, NoOpEvents(), Principal(Guid.Empty), default);

        var (status, _) = await ExecuteAsync(result);
        status.Should().Be(StatusCodes.Status204NoContent);

        await using var db = NewContext();
        (await db.Conventions.IgnoreQueryFilters()
            .CountAsync(c => c.TenantId == null && c.Role == RoleWire && c.Action == ActionWire))
            .Should().Be(0);
    }

    // ======================================================================
    // Registry endpoints
    // ======================================================================

    [Test]
    public async Task RegistryRoles_ReturnsAllEightRoles()
    {
        var result = ConventionStoreEndpoints.RegistryRoles();
        var (status, body) = await ExecuteAsync(result);
        status.Should().Be(StatusCodes.Status200OK);

        var roles = Deserialize<List<string>>(body);
        roles.Should().HaveCount(8);
        roles.Should().Contain(new[] { "developer", "tester", "security", "devops", "architect", "product_owner", "senior_developer", "tech_writer" });
    }

    [Test]
    public async Task RegistryRoleActions_ReturnsFullMatrix()
    {
        var result = ConventionStoreEndpoints.RegistryRoleActions();
        var (status, body) = await ExecuteAsync(result);
        status.Should().Be(StatusCodes.Status200OK);

        var cells = Deserialize<List<RoleActionCell>>(body);
        cells.Should().HaveCount(ExpectedCellCount);
        cells.Should().Contain(c => c.Role == RoleWire && c.Action == ActionWire);
    }

    [Test]
    public async Task RegistryActions_GroupsActionsPerRole()
    {
        var result = ConventionStoreEndpoints.RegistryActions();
        var (status, body) = await ExecuteAsync(result);
        status.Should().Be(StatusCodes.Status200OK);

        var perRole = Deserialize<List<RoleActionsResponse>>(body);
        perRole.Should().HaveCount(8);
        perRole.Single(r => r.Role == RoleWire).Actions.Should().Contain(ActionWire);
    }
}

/// <summary>
/// Story 27-10 — pins the <c>conventions:manage</c> permission contract (the
/// <c>ConventionManage</c> policy that gates tenant PUT/DELETE). Mirrors
/// <c>PromptManagePermissionTests</c>: admin+owner allowed, member denied.
/// </summary>
[TestFixture]
public class ConventionManagePermissionTests
{
    [Test]
    public void Owner_CanManageConventions()
        => Permissions.HasPermission("owner", "conventions:manage").Should().BeTrue();

    [Test]
    public void Admin_CanManageConventions()
        => Permissions.HasPermission("admin", "conventions:manage").Should().BeTrue(
            "tenant_admin must be able to manage tenant convention overrides");

    [Test]
    public void Member_CannotManageConventions()
        => Permissions.HasPermission("member", "conventions:manage").Should().BeFalse(
            "member users get 403 on PUT/DELETE in SaaS mode");

    [Test]
    public void GetRolePermissions_Member_ExcludesConventionsManage()
        => Permissions.GetRolePermissions("member").Should().NotContain("conventions:manage");

    [Test]
    public void GetRolePermissions_Admin_IncludesConventionsManage()
        => Permissions.GetRolePermissions("admin").Should().Contain("conventions:manage");
}
