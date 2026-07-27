using FluentAssertions;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;
using Tamma.Core.Actions;

namespace Tamma.Activities.Tests.Actions;

/// <summary>
/// REFLECTION SWEEP binding the catalog's <c>automation:*</c> plane to the real
/// hosted-service classes across the three production assemblies (the Core-side
/// keyset test is self-referential and says so). Descriptor <c>SiteKey</c>s
/// carry full type names, so the sweep matches classes without building a DI
/// container — which also means it sees the two registrations a grep misses:
/// <c>PlatformTaskWorker</c> (TryAddEnumerable descriptor, no
/// <c>AddHostedService&lt;&gt;</c> line) and
/// <c>TenantStatusInvalidationListener</c> (factory overload, null
/// <c>ImplementationType</c>). THE SWEEP IS THE SOURCE OF TRUTH: a new
/// <see cref="IHostedService"/> class fails this test until catalogued.
///
/// <para>LIMITATION: this is a TYPE-level sweep — it cannot see whether a type
/// is actually registered (that needs a composed host, Story 43-8's
/// registration-level harness). A hosted-service class that exists but is never
/// registered would still demand a catalog entry here; per the epic's rule,
/// don't write such placeholder classes.</para>
/// </summary>
[TestFixture]
public class BackgroundActorCatalogSweepTests
{
    private static IReadOnlyList<Type> HostedServiceTypes() =>
        new[]
        {
            typeof(Tamma.Activities.LlmCall.Tools.GitOperationsTool).Assembly, // Tamma.Activities
            typeof(Tamma.Api.Services.PlatformTasks.PlatformTaskWorker).Assembly, // Tamma.Api
            typeof(Tamma.ElsaServer.WorkflowSeeder).Assembly, // Tamma.ElsaServer
        }
        .Distinct()
        .SelectMany(a => a.GetTypes())
        .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(IHostedService).IsAssignableFrom(t))
        .ToArray();

    [Test]
    public void Every_hosted_service_class_and_every_catalogued_actor_match_bidirectionally()
    {
        var hostedTypeNames = HostedServiceTypes().Select(t => t.FullName!).ToArray();

        var cataloguedSites = ActionCatalog.All
            .Where(d => d.Key.Ns == ActionNamespace.Automation)
            .Select(d => d.SiteKey)
            .ToArray();

        cataloguedSites.Should().BeEquivalentTo(hostedTypeNames,
            "the catalog is derived from the code: a new IHostedService class must be catalogued "
            + "(add a BackgroundActor member + descriptor), and a deleted one un-catalogued");
    }

    [Test]
    public void The_hosted_service_count_is_pinned_at_25()
    {
        // Derivation 2026-07-27: 5 ElsaServer + 19 Tamma.Api BackgroundService/
        // IHostedService classes + PlatformTaskWorker = 25, matching the 24
        // AddHostedService registrations + the TryAddEnumerable descriptor 1:1.
        HostedServiceTypes().Should().HaveCount(25);
    }
}
