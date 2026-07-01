using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Services.Provisioning;
using Tamma.Api.Services.Provisioning.Cranl;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;

namespace Tamma.Api.Tests.Provisioning;

/// <summary>
/// Tests for <see cref="CranlProvisioningWorkflow"/>. Mocks
/// <see cref="ICranlApiClient"/> end-to-end so the state machine is
/// exercised without any HTTP traffic; the DbContext comes from the
/// shared <see cref="ApiTestFixture"/> Postgres container so jsonb
/// columns and other Postgres-specific features behave like prod.
/// Each test seeds + cleans its own tenant row.
/// </summary>
[TestFixture]
public class CranlProvisioningWorkflowTests
{
    private IServiceScope _scope = null!;
    // Owned by _scope; disposing the scope cascades.
#pragma warning disable NUnit1032
    private ControlPlaneDbContext _db = null!;
#pragma warning restore NUnit1032
    private Mock<ICranlApiClient> _cranl = null!;
    private CranlOptions _options = null!;
    private TenantSecretProtector _protector = null!;
    private ITenantConnectionStringProtector _connProtector = null!;
    private Mock<ITenantMoveService> _moveService = null!;
    private IConfiguration _configuration = null!;
    private CranlProvisioningWorkflow _workflow = null!;

    [SetUp]
    public async Task SetUp()
    {
        await ApiTestFixture.ResetDatabaseAsync();
        _scope = ApiTestFixture.Factory.Services.CreateScope();
        _db = _scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();

        _cranl = new Mock<ICranlApiClient>(MockBehavior.Strict);
        _options = new CranlOptions
        {
            ApiKey = "cranl_sk_TESTTESTTESTTESTTESTTESTTESTTEST",
            OrganizationId = "org-1",
            RepositoryId = "repo-1",
            DefaultRegion = "germany-1",
            DefaultBuildType = "dockerfile",
            AppBuildPath = "/apps/tamma-elsa",
            DefaultBranch = "main"
        };
        _protector = new TenantSecretProtector(new byte[32]);
        _connProtector = new TenantSecretProtectorAdapter(_protector);
        // Loose mock — MoveAsync returns a completed Task by default (Moq's
        // async default value), so the happy-path walk is a no-op unless a
        // test overrides it.
        _moveService = new Mock<ITenantMoveService>();
        _configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Tamma:ControlPlaneUrl"] = "https://api.tamma.test",
                ["Tamma:TenantSharedSecret"] = "shared-secret-for-tests"
            })
            .Build();

        _workflow = new CranlProvisioningWorkflow(
            _db, _cranl.Object, _options, _configuration,
            NullLogger<CranlProvisioningWorkflow>.Instance,
            _connProtector, _moveService.Object);
    }

    [TearDown]
    public void TearDown() => _scope?.Dispose();

    /// <summary>
    /// Epic 30 Phase B (Task B3): the Cranl walk/resume ids live in the
    /// tenants.provider_resource_ids JSONB (via CranlResourceIds), not the
    /// dropped cranl_* columns. Read them back off a freshly-loaded row.
    /// </summary>
    private Dictionary<string, string> ResourceIds(Tenant tenant) =>
        CranlResourceIds.Read(_db.Entry(tenant));

    private async Task<Tenant> SeedTenantAsync()
    {
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "Acme",
            Slug = "acme-" + Guid.NewGuid().ToString("N").Substring(0, 6),
            ProvisioningState = "pending",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.Tenants.Add(tenant);
        await _db.SaveChangesAsync();
        return tenant;
    }

    private async Task<Tenant> ReloadAsync(Guid tenantId)
    {
        // Detach the cached instance so we re-read from the DB; otherwise
        // EF returns the entity we just modified in the workflow's own
        // SaveChanges via change-tracking.
        foreach (var entry in _db.ChangeTracker.Entries<Tenant>().ToList())
            entry.State = EntityState.Detached;
        return await _db.Tenants.IgnoreQueryFilters().FirstAsync(t => t.Id == tenantId);
    }

    // ─── Happy path ──────────────────────────────────────────────────────────

    [Test]
    public async Task ProvisionAsync_HappyPath_WalksFullStateMachine()
    {
        var tenant = await SeedTenantAsync();

        _cranl.Setup(c => c.CreateProjectAsync(
                It.Is<string>(s => s.StartsWith("tamma-tenant-")),
                "org-1",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CranlProject { Id = "proj-1", Name = "tamma-tenant-x" });

        _cranl.Setup(c => c.CreateDatabaseAsync(
                It.Is<CreateDatabaseRequest>(r =>
                    r.ProjectId == "proj-1" && r.ServerId == "germany-1" && r.Type == "postgresql"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CranlDatabase { Id = "db-1", Status = "pending" });

        _cranl.Setup(c => c.GetDatabaseAsync("db-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CranlDatabase
            {
                Id = "db-1",
                Status = "running",
                Host = "db1.cranl.internal",
                Port = 5432,
                Username = "admin",
                Password = "s3cret",
                Database = "tamma-x"
            });

        _cranl.Setup(c => c.CreateApplicationAsync(
                It.Is<CreateApplicationRequest>(r =>
                    r.ProjectId == "proj-1" && r.RepositoryId == "repo-1"
                    && r.BuildType == "dockerfile" && r.BuildPath == "/apps/tamma-elsa"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CranlApplication { Id = "app-1", Status = "pending" });

        _cranl.Setup(c => c.PutEnvironmentAsync("app-1", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _cranl.Setup(c => c.DeployApplicationAsync("app-1", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _cranl.Setup(c => c.GetApplicationAsync("app-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CranlApplication { Id = "app-1", Status = "running" });

        _cranl.Setup(c => c.GetApplicationDomainsAsync("app-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CranlAppDomains
            {
                DefaultDomain = "tamma-engine-x.cranl.net",
                Domains = new List<CranlAppDomain>
                {
                    new() { DomainId = "d1", Host = "tamma-engine-x.cranl.net", Https = true }
                }
            });

        await _workflow.ProvisionAsync(
            tenant.Id, new ProvisioningOptions("germany-1"), CancellationToken.None);

        var refreshed = await ReloadAsync(tenant.Id);
        refreshed.ProvisioningState.Should().Be("ready");
        // B3: the walk ids land in the provider_resource_ids JSONB, not
        // dedicated columns. The encrypted DB URL is no longer persisted on
        // the tenant row (it lives only on the pool row); the plaintext is
        // asserted via the env push below.
        var ids = ResourceIds(refreshed);
        ids.Should().Contain(CranlResourceIds.ProjectId, "proj-1");
        ids.Should().Contain(CranlResourceIds.DatabaseId, "db-1");
        ids.Should().Contain(CranlResourceIds.AppId, "app-1");
        ids.Should().Contain(CranlResourceIds.AppUrl, "tamma-engine-x.cranl.net");

        _cranl.Verify(c => c.PutEnvironmentAsync(
            "app-1",
            It.Is<string>(s =>
                s.Contains("DATABASE_URL=postgresql://admin:s3cret@")
                && s.Contains("TAMMA_CONTROL_PLANE_URL=https://api.tamma.test")
                && s.Contains($"TAMMA_TENANT_ID={tenant.Id:D}")
                && s.Contains("TAMMA_SHARED_SECRET=shared-secret-for-tests")),
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ─── Epic 30 Phase B (B2): pool-row registration + resource ids + move ───

    [Test]
    public async Task ProvisionAsync_DatabaseReady_RegistersDedicatedPoolRow_PersistsResourceIds_AndMovesInline()
    {
        var tenant = await SeedTenantAsync();
        // The v2 provider stamps CranlRegion before enqueueing; mirror that.
        CranlResourceIds.Set(_db.Entry(tenant), CranlResourceIds.Region, "germany-1");
        await _db.SaveChangesAsync();
        SetupFullCranlWalk();

        await _workflow.ProvisionAsync(
            tenant.Id, new ProvisioningOptions("germany-1"), CancellationToken.None);

        // A dedicated pool row was registered for the Cranl hosting DB.
        var label = "cranl-" + CranlProvisioningWorkflow.ShortenForName(tenant.Id);
        var poolRow = await _db.TenantDatabases
            .AsNoTracking().SingleAsync(d => d.Label == label);
        poolRow.PlacementClass.Should().Be("dedicated");
        poolRow.Status.Should().Be("active");
        poolRow.TenantCapacity.Should().Be(1);
        poolRow.Host.Should().Be("db1.cranl.internal");
        poolRow.Port.Should().Be(5432);
        poolRow.TierEligibility.Should().Contain(tenant.Plan);
        // Admin envelope is encrypted keyword form — NOT the raw libpq URI.
        var adminConn = _protector.Decrypt(poolRow.AdminConnectionStringEncrypted);
        adminConn.Should().Contain("Host=db1.cranl.internal");
        adminConn.Should().Contain("Database=tamma-x");
        adminConn.Should().Contain("Username=admin");
        adminConn.Should().NotContain("postgresql://");

        // The tenant's schema was moved onto the new row exactly once, inline.
        _moveService.Verify(m => m.MoveAsync(
            tenant.Id, poolRow.Id, It.IsAny<CancellationToken>()), Times.Once);

        // F1 — resource-ids map persisted onto the JSONB shadow column, with
        // cranl_app_id landing after the app was created.
        var refreshed = await ReloadAsync(tenant.Id);
        var json = _db.Entry(refreshed).Property<string?>("ProviderResourceIds").CurrentValue;
        json.Should().NotBeNullOrEmpty();
        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(json!)!;
        map.Should().Contain("cranl_project_id", "proj-1");
        map.Should().Contain("cranl_database_id", "db-1");
        map.Should().Contain("cranl_app_id", "app-1");
        map.Should().Contain("cranl_region", "germany-1");

        refreshed.ProvisioningState.Should().Be("ready");
    }

    [Test]
    public async Task ProvisionAsync_SecondPass_DoesNotDuplicatePoolRow()
    {
        var tenant = await SeedTenantAsync();
        CranlResourceIds.Set(_db.Entry(tenant), CranlResourceIds.Region, "germany-1");
        await _db.SaveChangesAsync();
        SetupFullCranlWalk();

        await _workflow.ProvisionAsync(
            tenant.Id, new ProvisioningOptions("germany-1"), CancellationToken.None);
        // Resume: a re-run must reuse the pool row keyed by its label.
        await _workflow.ProvisionAsync(
            tenant.Id, new ProvisioningOptions("germany-1"), CancellationToken.None);

        var label = "cranl-" + CranlProvisioningWorkflow.ShortenForName(tenant.Id);
        (await _db.TenantDatabases.CountAsync(d => d.Label == label)).Should().Be(1);
    }

    [Test]
    public async Task ProvisionAsync_TenantAlreadyPlacedOnPoolRow_SkipsMove()
    {
        var tenant = await SeedTenantAsync();
        CranlResourceIds.Set(_db.Entry(tenant), CranlResourceIds.Region, "germany-1");
        await _db.SaveChangesAsync();

        // Pre-create the pool row and place the tenant on it (a completed
        // prior move points DatabaseId at the row).
        var label = "cranl-" + CranlProvisioningWorkflow.ShortenForName(tenant.Id);
        var poolRow = new TenantDatabase
        {
            Id = Guid.NewGuid(),
            Label = label,
            Host = "db1.cranl.internal",
            Port = 5432,
            AdminConnectionStringEncrypted =
                _protector.Encrypt("Host=db1.cranl.internal;Port=5432;Username=admin;Password=s3cret;Database=tamma-x"),
            PlacementClass = "dedicated",
            TierEligibility = new[] { tenant.Plan },
            TenantCapacity = 1,
            TenantCount = 1,
            Status = "active",
            KekVersion = 1,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };
        _db.TenantDatabases.Add(poolRow);
        await _db.SaveChangesAsync();
        // A PROVABLY-complete prior move points DatabaseId at the row AND has
        // flipped the lifecycle Status back to 'active' (step 10). The
        // connection-string envelope is required by
        // ck_tenants_connection_string_present when Status is 'active'.
        var placedEntry = _db.Entry(tenant);
        placedEntry.Property<Guid?>("DatabaseId").CurrentValue = poolRow.Id;
        placedEntry.Property<string?>("Status").CurrentValue = "active";
        placedEntry.Property<byte[]?>("EncryptedConnectionString").CurrentValue = new byte[] { 1, 2, 3 };
        await _db.SaveChangesAsync();

        SetupFullCranlWalk();

        await _workflow.ProvisionAsync(
            tenant.Id, new ProvisioningOptions("germany-1"), CancellationToken.None);

        _moveService.Verify(m => m.MoveAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        // Still no duplicate row.
        (await _db.TenantDatabases.CountAsync(d => d.Label == label)).Should().Be(1);
    }

    // ─── FIX 2: post-repoint resume gap (move committed but left 'draining') ─
    [Test]
    public async Task ProvisionAsync_MoveCommittedButLeftDraining_ResumeReInvokesMove()
    {
        var tenant = await SeedTenantAsync();
        var entry = _db.Entry(tenant);
        CranlResourceIds.Set(entry, CranlResourceIds.Region, "germany-1");
        // A tenant that is mid-move carries a connection-string envelope; the
        // 'draining' status the fake move commits requires it
        // (ck_tenants_connection_string_present).
        entry.Property<byte[]?>("EncryptedConnectionString").CurrentValue = new byte[] { 1, 2, 3 };
        await _db.SaveChangesAsync();
        SetupFullCranlWalk();

        // Fake move: commit the step-7 re-point (DatabaseId → target) but stay
        // 'draining' and "die" before step-10 activate — exactly the crash
        // window that strands a tenant. MoveAsync's own idempotent resume tail
        // (which sweeps + re-verifies + activates) can only run if it is
        // re-invoked, so the workflow must NOT skip on a draining tenant that
        // already points at the pool row.
        _moveService
            .Setup(m => m.MoveAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .Returns(async (Guid _, Guid targetDbId, CancellationToken _) =>
            {
                var e = _db.Entry(tenant);
                e.Property<Guid?>("DatabaseId").CurrentValue = targetDbId;
                e.Property<string?>("Status").CurrentValue = "draining";
                await _db.SaveChangesAsync();
            });

        // Pass 1 — reaches Ready but the lifecycle Status is stuck 'draining'.
        await _workflow.ProvisionAsync(
            tenant.Id, new ProvisioningOptions("germany-1"), CancellationToken.None);
        // Pass 2 (resume) — must NOT skip: it re-invokes MoveAsync.
        await _workflow.ProvisionAsync(
            tenant.Id, new ProvisioningOptions("germany-1"), CancellationToken.None);

        _moveService.Verify(m => m.MoveAsync(
            tenant.Id, It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // ─── FIX 3: pool-row label keyed on the immutable tenant id, not slug ─────
    [Test]
    public async Task ProvisionAsync_SlugChangesBetweenPasses_ReusesSinglePoolRow()
    {
        var tenant = await SeedTenantAsync();
        CranlResourceIds.Set(_db.Entry(tenant), CranlResourceIds.Region, "germany-1");
        await _db.SaveChangesAsync();
        SetupFullCranlWalk();

        await _workflow.ProvisionAsync(
            tenant.Id, new ProvisioningOptions("germany-1"), CancellationToken.None);

        // Admin renames the tenant between provisioning passes. The pool-row
        // label must be keyed on the immutable id, so the idempotency lookup
        // still finds the first row (a slug-keyed label would miss it and mint
        // a SECOND row aliasing the same physical DB → move aliasing guard).
        tenant.Slug = "renamed-" + Guid.NewGuid().ToString("N").Substring(0, 6);
        await _db.SaveChangesAsync();

        await _workflow.ProvisionAsync(
            tenant.Id, new ProvisioningOptions("germany-1"), CancellationToken.None);

        var label = "cranl-" + CranlProvisioningWorkflow.ShortenForName(tenant.Id);
        (await _db.TenantDatabases.CountAsync(d => d.Label == label)).Should().Be(1);
        // And no stray slug-keyed Cranl row was minted on the second pass.
        (await _db.TenantDatabases.CountAsync(d => d.Label.StartsWith("cranl-"))).Should().Be(1);
    }

    [Test]
    public async Task ProvisionAsync_MoveThrows_FlipsToFailed_AndDoesNotDeployApp()
    {
        var tenant = await SeedTenantAsync();
        CranlResourceIds.Set(_db.Entry(tenant), CranlResourceIds.Region, "germany-1");
        await _db.SaveChangesAsync();

        // Walk only as far as the DB being ready — the app-creation mocks are
        // intentionally NOT set up, so the strict Cranl mock would throw if
        // the workflow wrongly continued past the failed move.
        _cranl.Setup(c => c.CreateProjectAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CranlProject { Id = "proj-1" });
        _cranl.Setup(c => c.CreateDatabaseAsync(
                It.IsAny<CreateDatabaseRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CranlDatabase { Id = "db-1", Status = "pending" });
        _cranl.Setup(c => c.GetDatabaseAsync("db-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CranlDatabase
            {
                Id = "db-1", Status = "running",
                Host = "db1.cranl.internal", Port = 5432,
                Username = "admin", Password = "s3cret", Database = "tamma-x"
            });

        _moveService
            .Setup(m => m.MoveAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("move boom"));

        await _workflow.ProvisionAsync(
            tenant.Id, new ProvisioningOptions("germany-1"), CancellationToken.None);

        var refreshed = await ReloadAsync(tenant.Id);
        refreshed.ProvisioningState.Should().Be("failed");
        // FIX 4: the persisted detail carries a STRUCTURED short code (the
        // exception type name) — NEVER the raw ex.Message, which can echo a
        // connection string. The full exception still reaches the log sink.
        refreshed.ProvisioningDetail.Should().Contain("tenant_schema_move_failed");
        refreshed.ProvisioningDetail.Should().Contain("InvalidOperationException");
        refreshed.ProvisioningDetail.Should().NotContain("move boom");
        // The app step never ran, so no cranl_app_id landed in the JSONB.
        ResourceIds(refreshed).Should().NotContainKey(CranlResourceIds.AppId);
        _cranl.Verify(c => c.CreateApplicationAsync(
            It.IsAny<CreateApplicationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        // The pool row is registered (committed before the move was attempted).
        var label = "cranl-" + CranlProvisioningWorkflow.ShortenForName(tenant.Id);
        (await _db.TenantDatabases.AnyAsync(d => d.Label == label)).Should().BeTrue();
    }

    [Test]
    public void ToNpgsqlKeywordConnectionString_ConvertsLibpqUri()
    {
        var kw = CranlProvisioningWorkflow.ToNpgsqlKeywordConnectionString(
            "postgresql://admin:s3cret@db1.cranl.internal:5432/tamma-x");
        var b = new Npgsql.NpgsqlConnectionStringBuilder(kw);
        b.Host.Should().Be("db1.cranl.internal");
        b.Port.Should().Be(5432);
        b.Username.Should().Be("admin");
        b.Password.Should().Be("s3cret");
        b.Database.Should().Be("tamma-x");
    }

    [Test]
    public void ToNpgsqlKeywordConnectionString_PassesThroughKeywordForm()
    {
        const string kw = "Host=h;Port=5432;Username=u;Password=p;Database=d";
        CranlProvisioningWorkflow.ToNpgsqlKeywordConnectionString(kw).Should().Be(kw);
    }

    // ─── FIX 1: URI-reserved chars in a raw (un-percent-encoded) password ─────
    // Cranl mints random passwords that regularly contain URI-reserved chars
    // (@ : / # ? % + space). The old `new Uri(...)` parse threw
    // UriFormatException on those, permanently bricking the pool-row
    // registration → tenant stuck 'failed'. The manual libpq-URI parser must
    // recover the exact raw password from a RAW URI.

    [TestCase("s3cret", "s3cret")]         // clean baseline (regression)
    [TestCase("p@ssword", "p@ssword")]     // '@' in password (Uri: bad host)
    [TestCase("pa/ss", "pa/ss")]           // '/' in password
    [TestCase("pa#ss", "pa#ss")]           // '#' (Uri: fragment)
    [TestCase("pa%ss", "pa%ss")]           // '%' not a valid escape
    [TestCase("pa+ss", "pa+ss")]           // '+' must NOT become space
    [TestCase("p ss", "p ss")]             // space (Uri: invalid)
    public void ToNpgsqlKeywordConnectionString_RawReservedCharsInPassword_RoundTrip(
        string rawPassword, string expectedPassword)
    {
        var uri = $"postgresql://admin:{rawPassword}@db1.cranl.internal:5432/tamma-x";
        var kw = CranlProvisioningWorkflow.ToNpgsqlKeywordConnectionString(uri);
        var b = new Npgsql.NpgsqlConnectionStringBuilder(kw);
        b.Host.Should().Be("db1.cranl.internal");
        b.Port.Should().Be(5432);
        b.Username.Should().Be("admin");
        b.Password.Should().Be(expectedPassword);
        b.Database.Should().Be("tamma-x");
    }

    [Test]
    public void ToNpgsqlKeywordConnectionString_MissingPort_DefaultsTo5432()
    {
        var kw = CranlProvisioningWorkflow.ToNpgsqlKeywordConnectionString(
            "postgresql://admin:s3cret@db1.cranl.internal/tamma-x");
        var b = new Npgsql.NpgsqlConnectionStringBuilder(kw);
        b.Host.Should().Be("db1.cranl.internal");
        b.Port.Should().Be(5432);
        b.Database.Should().Be("tamma-x");
    }

    [Test]
    public void ToNpgsqlKeywordConnectionString_EmptyDatabase_OmitsDatabase()
    {
        var kw = CranlProvisioningWorkflow.ToNpgsqlKeywordConnectionString(
            "postgresql://admin:s3cret@db1.cranl.internal:5432/");
        var b = new Npgsql.NpgsqlConnectionStringBuilder(kw);
        b.Host.Should().Be("db1.cranl.internal");
        b.Port.Should().Be(5432);
        b.Database.Should().BeNullOrEmpty();
    }

    [Test]
    public void ToNpgsqlKeywordConnectionString_PreservesSslModeQueryParam()
    {
        var kw = CranlProvisioningWorkflow.ToNpgsqlKeywordConnectionString(
            "postgresql://admin:s3cret@db1.cranl.internal:5432/tamma-x?sslmode=require");
        var b = new Npgsql.NpgsqlConnectionStringBuilder(kw);
        b.Host.Should().Be("db1.cranl.internal");
        b.Database.Should().Be("tamma-x");
        b.SslMode.Should().Be(Npgsql.SslMode.Require);
    }

    [Test]
    public void ToNpgsqlKeywordConnectionString_IPv6Host_StripsBrackets()
    {
        var kw = CranlProvisioningWorkflow.ToNpgsqlKeywordConnectionString(
            "postgresql://admin:s3cret@[::1]:5432/tamma-x");
        var b = new Npgsql.NpgsqlConnectionStringBuilder(kw);
        b.Host.Should().Be("::1");
        b.Port.Should().Be(5432);
        b.Database.Should().Be("tamma-x");
    }

    [Test]
    public void BuildConnectionString_EscapesCredentials_RoundTripsThroughParser()
    {
        // Belt-and-suspenders: BuildConnectionString percent-escapes the
        // username/password when it stitches the URI, and the parser
        // percent-decodes — so even a password full of reserved chars makes a
        // VALID libpq URI (correct DATABASE_URL for the Cranl engine too) AND
        // round-trips back to the exact credential.
        var db = new CranlDatabase
        {
            Host = "db1.cranl.internal",
            Port = 5432,
            Username = "adm@n",
            Password = "p@ss/w#rd+1 x%2",
            Database = "tamma-x",
        };
        var uri = db.BuildConnectionString();
        uri.Should().NotBeNull();
        // A valid libpq URI: the reserved chars are percent-encoded, so no raw
        // '@' or ':' survives inside the userinfo to confuse a re-parse.
        uri!.Should().NotContain("p@ss/w#rd");

        var kw = CranlProvisioningWorkflow.ToNpgsqlKeywordConnectionString(uri);
        var b = new Npgsql.NpgsqlConnectionStringBuilder(kw);
        b.Host.Should().Be("db1.cranl.internal");
        b.Port.Should().Be(5432);
        b.Username.Should().Be("adm@n");
        b.Password.Should().Be("p@ss/w#rd+1 x%2");
        b.Database.Should().Be("tamma-x");
    }

    private void SetupFullCranlWalk()
    {
        _cranl.Setup(c => c.CreateProjectAsync(
                It.Is<string>(s => s.StartsWith("tamma-tenant-")),
                "org-1",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CranlProject { Id = "proj-1", Name = "tamma-tenant-x" });

        _cranl.Setup(c => c.CreateDatabaseAsync(
                It.Is<CreateDatabaseRequest>(r =>
                    r.ProjectId == "proj-1" && r.ServerId == "germany-1" && r.Type == "postgresql"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CranlDatabase { Id = "db-1", Status = "pending" });

        _cranl.Setup(c => c.GetDatabaseAsync("db-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CranlDatabase
            {
                Id = "db-1",
                Status = "running",
                Host = "db1.cranl.internal",
                Port = 5432,
                Username = "admin",
                Password = "s3cret",
                Database = "tamma-x"
            });

        _cranl.Setup(c => c.CreateApplicationAsync(
                It.Is<CreateApplicationRequest>(r =>
                    r.ProjectId == "proj-1" && r.RepositoryId == "repo-1"
                    && r.BuildType == "dockerfile" && r.BuildPath == "/apps/tamma-elsa"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CranlApplication { Id = "app-1", Status = "pending" });

        _cranl.Setup(c => c.PutEnvironmentAsync("app-1", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _cranl.Setup(c => c.DeployApplicationAsync("app-1", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _cranl.Setup(c => c.GetApplicationAsync("app-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CranlApplication { Id = "app-1", Status = "running" });

        _cranl.Setup(c => c.GetApplicationDomainsAsync("app-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CranlAppDomains
            {
                DefaultDomain = "tamma-engine-x.cranl.net",
                Domains = new List<CranlAppDomain>
                {
                    new() { DomainId = "d1", Host = "tamma-engine-x.cranl.net", Https = true }
                }
            });
    }

    // ─── Resume from existing project / db ───────────────────────────────────

    [Test]
    public async Task ProvisionAsync_ResumesWhenProjectAlreadyExists_DoesNotCreateProject()
    {
        var tenant = await SeedTenantAsync();
        CranlResourceIds.Set(_db.Entry(tenant), CranlResourceIds.ProjectId, "proj-existing");
        await _db.SaveChangesAsync();

        _cranl.Setup(c => c.CreateDatabaseAsync(
                It.Is<CreateDatabaseRequest>(r => r.ProjectId == "proj-existing"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CranlDatabase { Id = "db-1", Status = "pending" });
        _cranl.Setup(c => c.GetDatabaseAsync("db-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CranlDatabase
            {
                Id = "db-1", Status = "running",
                Host = "h", Port = 5432, Username = "u", Password = "p", Database = "d"
            });
        _cranl.Setup(c => c.CreateApplicationAsync(It.IsAny<CreateApplicationRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CranlApplication { Id = "app-1", Status = "pending" });
        _cranl.Setup(c => c.PutEnvironmentAsync("app-1", It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _cranl.Setup(c => c.DeployApplicationAsync("app-1", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _cranl.Setup(c => c.GetApplicationAsync("app-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CranlApplication { Id = "app-1", Status = "running" });
        _cranl.Setup(c => c.GetApplicationDomainsAsync("app-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CranlAppDomains { DefaultDomain = "h.cranl.net" });

        await _workflow.ProvisionAsync(
            tenant.Id, new ProvisioningOptions("germany-1"), CancellationToken.None);

        _cranl.Verify(c => c.CreateProjectAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        var refreshed = await ReloadAsync(tenant.Id);
        refreshed.ProvisioningState.Should().Be("ready");
    }

    // ─── B3: resume mid-walk from JSONB working-state ─────────────────────────

    [Test]
    public async Task ProvisionAsync_ResumesAfterDatabaseCreated_SkipsProjectAndDbCreate_ContinuesToReady()
    {
        // Simulate a worker that died after creating the project + database
        // (the two ids are already in the provider_resource_ids JSONB). A
        // re-reserved task must read them back and continue project→db→app,
        // NOT restart (which would leak a duplicate project/db).
        var tenant = await SeedTenantAsync();
        var entry = _db.Entry(tenant);
        CranlResourceIds.Set(entry, CranlResourceIds.ProjectId, "proj-1");
        CranlResourceIds.Set(entry, CranlResourceIds.DatabaseId, "db-1");
        CranlResourceIds.Set(entry, CranlResourceIds.Region, "germany-1");
        await _db.SaveChangesAsync();

        // Only the steps AFTER db-create are mocked. The strict Cranl mock
        // would throw if the walk wrongly called CreateProject/CreateDatabase.
        _cranl.Setup(c => c.GetDatabaseAsync("db-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CranlDatabase
            {
                Id = "db-1", Status = "running",
                Host = "db1.cranl.internal", Port = 5432,
                Username = "admin", Password = "s3cret", Database = "tamma-x"
            });
        _cranl.Setup(c => c.CreateApplicationAsync(
                It.Is<CreateApplicationRequest>(r => r.ProjectId == "proj-1"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CranlApplication { Id = "app-1", Status = "pending" });
        _cranl.Setup(c => c.PutEnvironmentAsync("app-1", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _cranl.Setup(c => c.DeployApplicationAsync("app-1", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _cranl.Setup(c => c.GetApplicationAsync("app-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CranlApplication { Id = "app-1", Status = "running" });
        _cranl.Setup(c => c.GetApplicationDomainsAsync("app-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CranlAppDomains { DefaultDomain = "tamma-engine-x.cranl.net" });

        await _workflow.ProvisionAsync(
            tenant.Id, new ProvisioningOptions("germany-1"), CancellationToken.None);

        _cranl.Verify(c => c.CreateProjectAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        _cranl.Verify(c => c.CreateDatabaseAsync(
            It.IsAny<CreateDatabaseRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        // Resume moved forward: the app was created and the schema moved once.
        _cranl.Verify(c => c.CreateApplicationAsync(
            It.IsAny<CreateApplicationRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        _moveService.Verify(m => m.MoveAsync(
            tenant.Id, It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);

        var refreshed = await ReloadAsync(tenant.Id);
        refreshed.ProvisioningState.Should().Be("ready");
        var ids = ResourceIds(refreshed);
        ids.Should().Contain(CranlResourceIds.ProjectId, "proj-1");
        ids.Should().Contain(CranlResourceIds.DatabaseId, "db-1");
        ids.Should().Contain(CranlResourceIds.AppId, "app-1");
        ids.Should().Contain(CranlResourceIds.AppUrl, "tamma-engine-x.cranl.net");
    }

    [Test]
    public async Task ProvisionAsync_ResumesAfterAppCreated_SkipsAppCreate_PushesEnvAndDeploys()
    {
        // Worker died after the application was created (project + db + app ids
        // all in the JSONB). Resume must NOT re-create the app; it pushes env,
        // deploys, polls, and completes.
        var tenant = await SeedTenantAsync();
        var entry = _db.Entry(tenant);
        CranlResourceIds.Set(entry, CranlResourceIds.ProjectId, "proj-1");
        CranlResourceIds.Set(entry, CranlResourceIds.DatabaseId, "db-1");
        CranlResourceIds.Set(entry, CranlResourceIds.AppId, "app-1");
        CranlResourceIds.Set(entry, CranlResourceIds.Region, "germany-1");
        await _db.SaveChangesAsync();

        _cranl.Setup(c => c.GetDatabaseAsync("db-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CranlDatabase
            {
                Id = "db-1", Status = "running",
                Host = "db1.cranl.internal", Port = 5432,
                Username = "admin", Password = "s3cret", Database = "tamma-x"
            });
        _cranl.Setup(c => c.PutEnvironmentAsync("app-1", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _cranl.Setup(c => c.DeployApplicationAsync("app-1", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _cranl.Setup(c => c.GetApplicationAsync("app-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CranlApplication { Id = "app-1", Status = "running" });
        _cranl.Setup(c => c.GetApplicationDomainsAsync("app-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CranlAppDomains { DefaultDomain = "tamma-engine-x.cranl.net" });

        await _workflow.ProvisionAsync(
            tenant.Id, new ProvisioningOptions("germany-1"), CancellationToken.None);

        // Strict mock proves CreateApplication was NOT called (never set up).
        _cranl.Verify(c => c.CreateApplicationAsync(
            It.IsAny<CreateApplicationRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        _cranl.Verify(c => c.PutEnvironmentAsync(
            "app-1", It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
        _cranl.Verify(c => c.DeployApplicationAsync(
            "app-1", It.IsAny<CancellationToken>()), Times.Once);

        var refreshed = await ReloadAsync(tenant.Id);
        refreshed.ProvisioningState.Should().Be("ready");
        ResourceIds(refreshed).Should().Contain(CranlResourceIds.AppUrl, "tamma-engine-x.cranl.net");
    }

    // ─── Failure modes ───────────────────────────────────────────────────────

    [Test]
    public async Task ProvisionAsync_DatabaseEntersErrorState_FlipsToFailed()
    {
        var tenant = await SeedTenantAsync();

        _cranl.Setup(c => c.CreateProjectAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CranlProject { Id = "proj-1" });
        _cranl.Setup(c => c.CreateDatabaseAsync(It.IsAny<CreateDatabaseRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CranlDatabase { Id = "db-1", Status = "pending" });
        _cranl.Setup(c => c.GetDatabaseAsync("db-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CranlDatabase { Id = "db-1", Status = "error" });

        await _workflow.ProvisionAsync(
            tenant.Id, new ProvisioningOptions("germany-1"), CancellationToken.None);

        var refreshed = await ReloadAsync(tenant.Id);
        refreshed.ProvisioningState.Should().Be("failed");
        refreshed.ProvisioningDetail.Should().Contain("database_did_not_report_connection_string");
    }

    [Test]
    public async Task ProvisionAsync_CranlApiException_FlipsToFailedAndRethrows()
    {
        var tenant = await SeedTenantAsync();

        _cranl.Setup(c => c.CreateProjectAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CranlApiException(HttpStatusCode.Forbidden, "plan_limit_reached", "no more"));

        var act = async () => await _workflow.ProvisionAsync(
            tenant.Id, new ProvisioningOptions("germany-1"), CancellationToken.None);
        await act.Should().ThrowAsync<CranlApiException>();

        var refreshed = await ReloadAsync(tenant.Id);
        refreshed.ProvisioningState.Should().Be("failed");
        refreshed.ProvisioningDetail.Should().Contain("cranl_api_error:403");
    }

    // ─── Deprovisioning ──────────────────────────────────────────────────────

    [Test]
    public async Task DeprovisionAsync_DeletesInOrder_AppThenDbThenProject()
    {
        var tenant = await SeedTenantAsync();
        var entry = _db.Entry(tenant);
        CranlResourceIds.Set(entry, CranlResourceIds.ProjectId, "proj-1");
        CranlResourceIds.Set(entry, CranlResourceIds.DatabaseId, "db-1");
        CranlResourceIds.Set(entry, CranlResourceIds.AppId, "app-1");
        CranlResourceIds.Set(entry, CranlResourceIds.AppUrl, "x.cranl.net");
        CranlResourceIds.Set(entry, CranlResourceIds.Region, "germany-1");
        await _db.SaveChangesAsync();

        var sequence = new List<string>();
        _cranl.Setup(c => c.DeleteApplicationAsync("app-1", It.IsAny<CancellationToken>()))
            .Callback(() => sequence.Add("app"))
            .Returns(Task.CompletedTask);
        _cranl.Setup(c => c.DeleteDatabaseAsync("db-1", It.IsAny<CancellationToken>()))
            .Callback(() => sequence.Add("db"))
            .Returns(Task.CompletedTask);
        _cranl.Setup(c => c.DeleteProjectAsync("proj-1", It.IsAny<CancellationToken>()))
            .Callback(() => sequence.Add("project"))
            .Returns(Task.CompletedTask);

        await _workflow.DeprovisionAsync(tenant.Id, CancellationToken.None);

        sequence.Should().Equal("app", "db", "project");
        var refreshed = await ReloadAsync(tenant.Id);
        refreshed.ProvisioningState.Should().Be("deprovisioned");
        // B3: teardown clears the Cranl walk-state from the JSONB, keeping
        // only the region hint for a possible re-provision.
        var ids = ResourceIds(refreshed);
        ids.Should().NotContainKey(CranlResourceIds.ProjectId);
        ids.Should().NotContainKey(CranlResourceIds.DatabaseId);
        ids.Should().NotContainKey(CranlResourceIds.AppId);
        ids.Should().NotContainKey(CranlResourceIds.AppUrl);
        ids.Should().Contain(CranlResourceIds.Region, "germany-1");
    }

    [Test]
    public async Task DeprovisionAsync_404OnApp_TreatedAsAlreadyAbsent()
    {
        var tenant = await SeedTenantAsync();
        var entry = _db.Entry(tenant);
        CranlResourceIds.Set(entry, CranlResourceIds.ProjectId, "proj-1");
        CranlResourceIds.Set(entry, CranlResourceIds.DatabaseId, "db-1");
        CranlResourceIds.Set(entry, CranlResourceIds.AppId, "app-1");
        await _db.SaveChangesAsync();

        _cranl.Setup(c => c.DeleteApplicationAsync("app-1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new CranlApiException(HttpStatusCode.NotFound, "not found", "gone"));
        _cranl.Setup(c => c.DeleteDatabaseAsync("db-1", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        _cranl.Setup(c => c.DeleteProjectAsync("proj-1", It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        await _workflow.DeprovisionAsync(tenant.Id, CancellationToken.None);

        var refreshed = await ReloadAsync(tenant.Id);
        refreshed.ProvisioningState.Should().Be("deprovisioned");
    }

    // ─── Naming ──────────────────────────────────────────────────────────────

    [Test]
    public void ShortenForName_TakesFirst8HexChars()
    {
        var id = Guid.Parse("11112222-3333-4444-5555-666677778888");
        CranlProvisioningWorkflow.ShortenForName(id).Should().Be("11112222");
    }
}
