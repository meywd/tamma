using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Tamma.Api.Extensions;
using Tamma.Api.Services.Provisioning.V2;
using Tamma.Api.Services.Provisioning.V2.Cranl;
using Tamma.Data;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Provisioning.V2;

/// <summary>
/// Story 30-3 — verifies the V2 Cranl provider is registered through
/// <see cref="ProvisioningServiceCollectionExtensions.AddTenantProvisioning"/>
/// when <c>Cranl:ApiKey</c> + <c>Cranl:OrganizationId</c> are populated, and
/// that the <see cref="TenantProviderRegistry"/> can resolve it by the
/// <c>"cranl"</c> key.
///
/// <para><b>Operating-mode coverage</b>:</para>
/// <list type="bullet">
///   <item><description><b>Single-user / dev (no Cranl config)</b>:
///     Registry contains only the <see cref="NullTenantProvider"/>.
///     Looking up <c>"cranl"</c> throws.</description></item>
///   <item><description><b>SaaS (Cranl configured)</b>: Registry contains
///     both the null seam and <see cref="CranlTenantProviderV2"/>; the
///     latter resolves and reports the documented capability matrix.</description></item>
/// </list>
/// </summary>
[TestFixture]
public sealed class CranlV2DiRegistrationTests
{
    private static IServiceProvider BuildServices(IConfiguration configuration)
    {
        var services = new ServiceCollection();

        // Minimal DI dependencies required by the AddTenantProvisioning
        // extension — IConfiguration, IHostEnvironment, logging, and the
        // ControlPlaneDbContext (in-memory; we don't query it here).
        services.AddSingleton(configuration);
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        services.AddLogging();
        services.AddDbContext<ControlPlaneDbContext>(opts =>
            opts.UseInMemoryDatabase("v2-cranl-di-tests-" + Guid.NewGuid()));

        // The Cranl client uses HttpClient; AddHttpClient is required by
        // AddTenantProvisioning when the options are populated.
        services.AddHttpClient();

        // Stub the platform-queue repository so the V2 provider can resolve
        // it when the registry walks dependencies (we never call into it
        // from these DI-only assertions).
        services.AddSingleton(new Mock<IPlatformQueuedTaskRepository>(
            MockBehavior.Loose).Object);

        services.AddTenantProvisioning(configuration);
        return services.BuildServiceProvider();
    }

    [Test]
    public void Registry_WithoutCranlConfig_OnlyNullProviderRegistered()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        using var sp = (ServiceProvider)BuildServices(configuration);
        var registry = sp.GetRequiredService<TenantProviderRegistry>();

        registry.RegisteredKeys.Should().Contain("null");
        registry.RegisteredKeys.Should().NotContain("cranl");

        var act = () => registry.GetProvider("cranl");
        act.Should().Throw<KeyNotFoundException>(
            because: "cranl is not registered when Cranl:ApiKey is unset");
    }

    [Test]
    public void Registry_WithCranlConfig_RegistersCranlProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Cranl:ApiKey"] = "cranl_sk_test_dummy",
                ["Cranl:OrganizationId"] = "org_test",
                // R2-H11: production hard-fails without an explicit encryption
                // key. Tests run in Development env, so the HKDF fallback is
                // permitted; we still set a valid base64-32-byte key here so
                // the assertion exercises the production code path.
                ["Cranl:EncryptionKey"] = Convert.ToBase64String(new byte[32]),
            })
            .Build();

        using var sp = (ServiceProvider)BuildServices(configuration);
        var registry = sp.GetRequiredService<TenantProviderRegistry>();

        registry.RegisteredKeys.Should().Contain("cranl");
        registry.RegisteredKeys.Should().Contain("null");

        var cranl = registry.GetProvider("cranl");
        cranl.ProviderKey.Should().Be("cranl");

        var caps = cranl.GetCapabilities();
        caps.SupportsTopology(ProvisioningTopology.DedicatedCompute).Should().BeTrue();
        caps.SupportsTopology(ProvisioningTopology.DatabaseOnly).Should().BeTrue();
        caps.SupportsTopology(ProvisioningTopology.Managed).Should().BeFalse();
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
