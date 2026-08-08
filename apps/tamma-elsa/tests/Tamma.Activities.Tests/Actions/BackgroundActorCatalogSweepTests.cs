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
    // ⚠ META-GUARD — THE ASSEMBLY LIST BELOW IS THE SWEEP'S BLIND SPOT. A
    // reflection sweep only binds the catalog to code it actually scans: an
    // IHostedService declared in an assembly missing from this list is
    // invisible here and ships uncatalogued. The list must cover EVERY
    // production assembly — today the three are Tamma.Activities, Tamma.Api
    // and Tamma.ElsaServer (kept in lockstep with ToolExecutorCatalogSweepTests,
    // and pinned by The_swept_assemblies_are_the_three_production_assemblies) —
    // and MUST GROW the day a fourth production assembly is added to
    // Tamma.sln, even if it declares no hosted services yet.
    private static IReadOnlyList<System.Reflection.Assembly> SweptAssemblies() =>
        new[]
        {
            typeof(Tamma.Activities.LlmCall.Tools.GitOperationsTool).Assembly, // Tamma.Activities
            typeof(Tamma.Api.Services.PlatformTasks.PlatformTaskWorker).Assembly, // Tamma.Api
            typeof(Tamma.ElsaServer.WorkflowSeeder).Assembly, // Tamma.ElsaServer
        }
        .Distinct()
        .ToArray();

    private static IReadOnlyList<Type> HostedServiceTypes() =>
        SweptAssemblies()
        .SelectMany(a => a.GetTypes())
        .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(IHostedService).IsAssignableFrom(t))
        .ToArray();

    [Test]
    public void The_swept_assemblies_are_the_three_production_assemblies()
    {
        // Meta-assertion for the sweep's own blind spot (see the note above):
        // if this fails because a production assembly was added or renamed,
        // grow the list here AND in ToolExecutorCatalogSweepTests — never
        // shrink a sweep to make it pass.
        SweptAssemblies().Select(a => a.GetName().Name)
            .Should().BeEquivalentTo(new[] { "Tamma.Activities", "Tamma.Api", "Tamma.ElsaServer" });
    }

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
    public void The_hosted_service_count_is_pinned_at_32()
    {
        // 31 → 32 (Epic 31 P3, 2026-08-08): + CiCompletionPollerService
        // (Tamma.Api.Services.Ci) — the DG-5 durable CI completion poller that
        // resumes suspended CI-result waits on run completion; catalogued as
        // automation:ci-completion-poller.
        // 29 → 31 (Epic 31 P2, 2026-08-07): + PlatformDriverCacheInvalidator
        // (the Story 31-2 cache-invalidation subscriber) and
        // + GitHubInstallationBridgeBackfillService (the seam-14 registry
        // backfill), both in Tamma.Api.Services.Platforms and both catalogued
        // as automation:* members.
        // Derivation 2026-07-29 (Story 43-5): 6 ElsaServer (incl. Story 41-30's
        // TenantScheduledTriggerService — the tenant-aware scheduled-trigger
        // seam, registered inside the conditional control-plane block) + 22
        // Tamma.Api BackgroundService/IHostedService classes (incl. the Epic
        // 46 review-F1 ProviderSettingsStorePrimingService, Story 43-4's
        // ActionCatalogStartupValidator, and Story 43-5's
        // GovernancePolicySnapshotPrimingService — both registered via
        // TryAddEnumerable in AddActionCatalogGovernance) +
        // PlatformTaskWorker = 29.
        HostedServiceTypes().Should().HaveCount(32);
    }
}
