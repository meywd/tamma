using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Tamma.Api.Extensions;
using Tamma.Api.Services.PlatformTasks;
using Tamma.Api.Services.Provisioning;
using Tamma.Api.Services.Provisioning.Cranl;
using Tamma.Api.Services.Provisioning.V2.Cranl;
using Tamma.Data;
using Tamma.Data.Abstractions;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Provisioning;

/// <summary>
/// Task 4 (Epic 30 Phase A) — tests for the platform-queue handlers that
/// consume the <c>provisioning.tenant</c> / <c>provisioning.tenant.deprovision</c>
/// rows enqueued by <see cref="CranlTenantProviderV2"/> and drive the
/// <see cref="CranlProvisioningWorkflow"/> REST walk.
///
/// <para>These close the orphan-enqueue gap: before this wave nothing
/// handled those platform-queue task types, so a v2 Cranl provision parked
/// forever and the dispatch probe timed out to <c>Failed</c>.</para>
///
/// <para>Each happy-path test uses a <b>real</b>
/// <see cref="CranlProvisioningWorkflow"/> over the shared Postgres test
/// container (so the tenant row genuinely advances) with a strict
/// <see cref="ICranlApiClient"/> mock standing in for the Cranl API.</para>
/// </summary>
[TestFixture]
public sealed class CranlPlatformTaskHandlerTests
{
    private IServiceScope _scope = null!;
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

    private CranlProvisionPlatformTaskHandler BuildProvisionHandler() =>
        new(_workflow, _options, NullLogger<CranlProvisionPlatformTaskHandler>.Instance);

    private CranlDeprovisionPlatformTaskHandler BuildDeprovisionHandler() =>
        new(_workflow, NullLogger<CranlDeprovisionPlatformTaskHandler>.Instance);

    private async Task<Tenant> SeedTenantAsync(string state = "pending")
    {
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = "Acme",
            Slug = "acme-" + Guid.NewGuid().ToString("N").Substring(0, 6),
            ProvisioningState = state,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };
        _db.Tenants.Add(tenant);
        await _db.SaveChangesAsync();
        return tenant;
    }

    private async Task<Tenant> ReloadAsync(Guid tenantId)
    {
        foreach (var entry in _db.ChangeTracker.Entries<Tenant>().ToList())
            entry.State = EntityState.Detached;
        return await _db.Tenants.IgnoreQueryFilters().FirstAsync(t => t.Id == tenantId);
    }

    // ─── TaskType routing identifiers ─────────────────────────────────────────

    [Test]
    public void ProvisionHandler_TaskType_MatchesProviderConstant()
    {
        BuildProvisionHandler().TaskType.Should().Be("provisioning.tenant");
        BuildProvisionHandler().TaskType.Should().Be(CranlTenantProviderV2.ProvisioningTaskType);
    }

    [Test]
    public void DeprovisionHandler_TaskType_MatchesProviderConstant()
    {
        BuildDeprovisionHandler().TaskType.Should().Be("provisioning.tenant.deprovision");
        BuildDeprovisionHandler().TaskType
            .Should().Be(CranlTenantProviderV2.DeprovisioningTaskType);
    }

    // ─── Provision happy path ─────────────────────────────────────────────────

    [Test]
    public async Task ProvisionHandler_HappyPath_DrivesWorkflowToReady()
    {
        var tenant = await SeedTenantAsync();
        SetupFullProvisionWalk();

        var task = new PlatformQueuedTask
        {
            Id = Guid.NewGuid(),
            Type = CranlTenantProviderV2.ProvisioningTaskType,
            TenantId = tenant.Id,
            Payload = JsonSerializer.Serialize(new ProvisioningTaskPayload
            {
                TenantId = tenant.Id,
                Region = "germany-1",
                CustomName = null
            })
        };

        await BuildProvisionHandler().HandleAsync(task, CancellationToken.None);

        var refreshed = await ReloadAsync(tenant.Id);
        refreshed.ProvisioningState.Should().Be("ready");
        // B3: walk ids land in the provider_resource_ids JSONB.
        var ids = CranlResourceIds.Read(_db.Entry(refreshed));
        ids.Should().Contain(CranlResourceIds.ProjectId, "proj-1");
        ids.Should().Contain(CranlResourceIds.AppId, "app-1");
        // The handler genuinely walked the Cranl REST flow via the engine.
        _cranl.Verify(c => c.CreateProjectAsync(
            It.IsAny<string>(), "org-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ProvisionHandler_BlankRegion_FallsBackToConfiguredDefault()
    {
        var tenant = await SeedTenantAsync();
        SetupFullProvisionWalk(expectedServerId: "germany-1");

        var task = new PlatformQueuedTask
        {
            Id = Guid.NewGuid(),
            Type = CranlTenantProviderV2.ProvisioningTaskType,
            TenantId = tenant.Id,
            // Region intentionally blank → handler must use CranlOptions.DefaultRegion.
            Payload = JsonSerializer.Serialize(new ProvisioningTaskPayload
            {
                TenantId = tenant.Id,
                Region = "",
                CustomName = null
            })
        };

        await BuildProvisionHandler().HandleAsync(task, CancellationToken.None);

        var refreshed = await ReloadAsync(tenant.Id);
        refreshed.ProvisioningState.Should().Be("ready");
        _cranl.Verify(c => c.CreateDatabaseAsync(
            It.Is<CreateDatabaseRequest>(r => r.ServerId == "germany-1"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ─── Provision guard clauses ──────────────────────────────────────────────

    [Test]
    public async Task ProvisionHandler_NullTask_ThrowsArgumentNull()
    {
        var act = async () => await BuildProvisionHandler().HandleAsync(null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Test]
    public async Task ProvisionHandler_MalformedJson_ThrowsTerminal()
    {
        var task = new PlatformQueuedTask
        {
            Id = Guid.NewGuid(),
            Type = CranlTenantProviderV2.ProvisioningTaskType,
            Payload = "not-valid-json {"
        };

        var act = async () => await BuildProvisionHandler().HandleAsync(task, CancellationToken.None);
        await act.Should().ThrowAsync<PlatformTaskTerminalException>();
    }

    [Test]
    public async Task ProvisionHandler_EmptyPayload_ThrowsTerminal()
    {
        var task = new PlatformQueuedTask
        {
            Id = Guid.NewGuid(),
            Type = CranlTenantProviderV2.ProvisioningTaskType,
            Payload = ""
        };

        var act = async () => await BuildProvisionHandler().HandleAsync(task, CancellationToken.None);
        await act.Should().ThrowAsync<PlatformTaskTerminalException>();
    }

    [Test]
    public async Task ProvisionHandler_EmptyTenantId_ThrowsTerminal()
    {
        var task = new PlatformQueuedTask
        {
            Id = Guid.NewGuid(),
            Type = CranlTenantProviderV2.ProvisioningTaskType,
            Payload = JsonSerializer.Serialize(new ProvisioningTaskPayload
            {
                TenantId = Guid.Empty,
                Region = "germany-1"
            })
        };

        var act = async () => await BuildProvisionHandler().HandleAsync(task, CancellationToken.None);
        await act.Should().ThrowAsync<PlatformTaskTerminalException>();
    }

    // ─── Deprovision happy path ───────────────────────────────────────────────

    [Test]
    public async Task DeprovisionHandler_HappyPath_DrivesWorkflowToDeprovisioned()
    {
        var tenant = await SeedTenantAsync(state: "ready");
        var entry = _db.Entry(tenant);
        CranlResourceIds.Set(entry, CranlResourceIds.ProjectId, "proj-1");
        CranlResourceIds.Set(entry, CranlResourceIds.DatabaseId, "db-1");
        CranlResourceIds.Set(entry, CranlResourceIds.AppId, "app-1");
        CranlResourceIds.Set(entry, CranlResourceIds.AppUrl, "x.cranl.net");
        await _db.SaveChangesAsync();

        _cranl.Setup(c => c.DeleteApplicationAsync("app-1", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _cranl.Setup(c => c.DeleteDatabaseAsync("db-1", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _cranl.Setup(c => c.DeleteProjectAsync("proj-1", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var task = new PlatformQueuedTask
        {
            Id = Guid.NewGuid(),
            Type = CranlTenantProviderV2.DeprovisioningTaskType,
            TenantId = tenant.Id,
            Payload = JsonSerializer.Serialize(new ProvisioningTaskPayload
            {
                TenantId = tenant.Id,
                Region = "germany-1"
            })
        };

        await BuildDeprovisionHandler().HandleAsync(task, CancellationToken.None);

        var refreshed = await ReloadAsync(tenant.Id);
        refreshed.ProvisioningState.Should().Be("deprovisioned");
        // B3: teardown clears the Cranl ids from the JSONB.
        var ids = CranlResourceIds.Read(_db.Entry(refreshed));
        ids.Should().NotContainKey(CranlResourceIds.ProjectId);
        ids.Should().NotContainKey(CranlResourceIds.AppId);
    }

    [Test]
    public async Task DeprovisionHandler_MalformedJson_ThrowsTerminal()
    {
        var task = new PlatformQueuedTask
        {
            Id = Guid.NewGuid(),
            Type = CranlTenantProviderV2.DeprovisioningTaskType,
            Payload = "}{ not json"
        };

        var act = async () => await BuildDeprovisionHandler().HandleAsync(task, CancellationToken.None);
        await act.Should().ThrowAsync<PlatformTaskTerminalException>();
    }

    [Test]
    public async Task DeprovisionHandler_EmptyTenantId_ThrowsTerminal()
    {
        var task = new PlatformQueuedTask
        {
            Id = Guid.NewGuid(),
            Type = CranlTenantProviderV2.DeprovisioningTaskType,
            Payload = JsonSerializer.Serialize(new ProvisioningTaskPayload
            {
                TenantId = Guid.Empty,
                Region = "germany-1"
            })
        };

        var act = async () => await BuildDeprovisionHandler().HandleAsync(task, CancellationToken.None);
        await act.Should().ThrowAsync<PlatformTaskTerminalException>();
    }

    // ─── Registry routing (the orphan-enqueue is now consumed) ────────────────

    [Test]
    public void Registry_WithCranlConfig_ResolvesHandlersForBothProviderTaskTypes()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cranl:ApiKey"] = "cranl_sk_test_dummy",
                ["Cranl:OrganizationId"] = "org_test",
                ["Cranl:EncryptionKey"] = Convert.ToBase64String(new byte[32]),
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        services.AddLogging();
        services.AddDbContext<ControlPlaneDbContext>(opts =>
            opts.UseInMemoryDatabase("cranl-platform-handler-di-" + Guid.NewGuid()));
        services.AddHttpClient();
        services.AddSingleton(new Mock<IPlatformQueuedTaskRepository>(MockBehavior.Loose).Object);
        // Deps the sibling ProvisionTenantV2TaskHandler's workflow needs —
        // resolving the IPlatformTaskHandler enumerable constructs every
        // registered handler, so the whole graph must be satisfiable.
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Mock.Of<IPlatformEventPublisher>());
        // Epic 30 Phase B — CranlProvisioningWorkflow now takes the pool-row
        // protector + the schema-move engine (both provided by
        // AddPlatformEventBus in production, not wired in this bare graph).
        services.AddSingleton(Mock.Of<ITenantMoveService>());
        services.AddSingleton<ITenantConnectionStringProtector>(sp =>
            new TenantSecretProtectorAdapter(sp.GetRequiredService<TenantSecretProtector>()));
        services.AddTenantProvisioning(configuration);

        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var handlers = scope.ServiceProvider.GetServices<IPlatformTaskHandler>().ToList();
        var registry = new PlatformTaskHandlerRegistry(handlers);

        registry.Resolve(CranlTenantProviderV2.ProvisioningTaskType)
            .Should().BeOfType<CranlProvisionPlatformTaskHandler>();
        registry.Resolve(CranlTenantProviderV2.DeprovisioningTaskType)
            .Should().BeOfType<CranlDeprovisionPlatformTaskHandler>();
    }

    [Test]
    public void Registry_WithoutCranlConfig_NoCranlPlatformHandlersRegistered()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        services.AddLogging();
        services.AddDbContext<ControlPlaneDbContext>(opts =>
            opts.UseInMemoryDatabase("cranl-platform-handler-di-off-" + Guid.NewGuid()));
        services.AddHttpClient();
        services.AddSingleton(new Mock<IPlatformQueuedTaskRepository>(MockBehavior.Loose).Object);
        // ProvisionTenantV2TaskHandler is still registered (it is mode-agnostic);
        // its workflow needs these to construct when the enumerable is resolved.
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Mock.Of<IPlatformEventPublisher>());
        services.AddTenantProvisioning(configuration);

        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        var handlers = scope.ServiceProvider.GetServices<IPlatformTaskHandler>().ToList();
        var registry = new PlatformTaskHandlerRegistry(handlers);

        registry.Resolve(CranlTenantProviderV2.ProvisioningTaskType).Should().BeNull();
        registry.Resolve(CranlTenantProviderV2.DeprovisioningTaskType).Should().BeNull();
    }

    // ─── Cranl mock walk helper ───────────────────────────────────────────────

    private void SetupFullProvisionWalk(string expectedServerId = "germany-1")
    {
        _cranl.Setup(c => c.CreateProjectAsync(
                It.Is<string>(s => s.StartsWith("tamma-tenant-")),
                "org-1",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CranlProject { Id = "proj-1", Name = "tamma-tenant-x" });

        _cranl.Setup(c => c.CreateDatabaseAsync(
                It.Is<CreateDatabaseRequest>(r =>
                    r.ProjectId == "proj-1" && r.ServerId == expectedServerId && r.Type == "postgresql"),
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

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "Tamma.Api.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
