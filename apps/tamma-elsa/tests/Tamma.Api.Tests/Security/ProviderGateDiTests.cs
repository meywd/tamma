using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Tamma.Api.Services.PromptStore;
using Tamma.Api.Services.Security;
using Tamma.Data;
using Tamma.Data.Repositories;

namespace Tamma.Api.Tests.Security;

/// <summary>
/// Story 32-4 — DI-wiring smoke test. Replicates the exact Program.cs gate
/// registration block (EntityProviderAuthLookup default, permissive entitlement,
/// metrics singleton, scoped gate) against a minimal service collection and
/// asserts every gate dependency resolves. Cheaper than a full
/// WebApplicationFactory boot; the registration shapes match Program.cs.
/// </summary>
[TestFixture]
public class ProviderGateDiTests
{
    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // Ambient deps the gate consumes (faked / minimal, as Program.cs supplies
        // the real ones elsewhere). The DbContext backs EntityProviderAuthLookup.
        services.AddDbContext<ControlPlaneDbContext>(o =>
            o.UseInMemoryDatabase("provider-gate-di-smoke"));
        services.AddSingleton<ITammaModeProvider>(new StubMode(TammaMode.SaaS));
        services.AddScoped<IEventRepository, RecordingGateEventRepository>();

        // ── The Program.cs gate registration block (mirrored verbatim) ──
        services.AddScoped<IProviderAuthLookup, EntityProviderAuthLookup>();
        services.AddSingleton<ITenantProviderEntitlement, PermissiveTenantProviderEntitlement>();
        services.AddSingleton<ProviderGatingMetrics>();
        services.AddScoped<ISaaSProviderGate, SaaSProviderGate>();

        return services.BuildServiceProvider();
    }

    [Test]
    public void Gate_lookup_metrics_and_entitlement_all_resolve()
    {
        using var sp = BuildProvider();
        using var scope = sp.CreateScope();
        var p = scope.ServiceProvider;

        p.GetService<ISaaSProviderGate>().Should().NotBeNull().And.BeOfType<SaaSProviderGate>();
        p.GetService<IProviderAuthLookup>().Should().NotBeNull().And.BeOfType<EntityProviderAuthLookup>(
            "EntityProviderAuthLookup is the production default (34-11 has landed)");
        p.GetService<ITenantProviderEntitlement>().Should().NotBeNull()
            .And.BeOfType<PermissiveTenantProviderEntitlement>();
        p.GetService<ProviderGatingMetrics>().Should().NotBeNull();
    }

    [Test]
    public void Metrics_is_a_singleton()
    {
        using var sp = BuildProvider();
        var a = sp.GetRequiredService<ProviderGatingMetrics>();
        var b = sp.GetRequiredService<ProviderGatingMetrics>();
        a.Should().BeSameAs(b);
    }

    [Test]
    public void Static_lookup_swap_also_resolves_contract_neutral()
    {
        // Proves the documented one-line swap to StaticProviderAuthLookup resolves.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ITammaModeProvider>(new StubMode(TammaMode.SaaS));
        services.AddScoped<IEventRepository, RecordingGateEventRepository>();
        services.AddSingleton<IProviderAuthLookup, StaticProviderAuthLookup>();
        services.AddSingleton<ITenantProviderEntitlement, PermissiveTenantProviderEntitlement>();
        services.AddSingleton<ProviderGatingMetrics>();
        services.AddScoped<ISaaSProviderGate, SaaSProviderGate>();

        using var sp = services.BuildServiceProvider();
        using var scope = sp.CreateScope();
        scope.ServiceProvider.GetService<ISaaSProviderGate>().Should().NotBeNull();
        scope.ServiceProvider.GetService<IProviderAuthLookup>().Should()
            .BeOfType<StaticProviderAuthLookup>();
    }
}
