using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Actions;

namespace Tamma.Api.Tests.Actions;

/// <summary>
/// Story 43-8 (AC5, D2) — REGISTRATION-LEVEL sweep of the <c>automation:*</c> plane:
/// reflects the BUILT <see cref="IServiceCollection"/> of the real <c>Tamma.Api</c>
/// host and asserts every <see cref="IHostedService"/> descriptor maps to a
/// catalogued <see cref="BackgroundActor"/>.
///
/// <para><b>How this differs from the type-level sweep.</b>
/// <c>Tamma.Activities.Tests/Actions/BackgroundActorCatalogSweepTests</c> asks
/// "does every hosted-service CLASS have a catalog entry?". This one asks "is every
/// hosted service the host actually REGISTERS catalogued?" — the question that
/// matters for what runs unattended. They fail on different things: a class that
/// exists but is never registered passes here and fails there; a registration whose
/// implementation type is invisible to a class scan fails here and passes
/// there.</para>
///
/// <para><b>The two registration shapes a naive scan misses, both real, both
/// covered here:</b></para>
/// <list type="number">
///   <item><b>Factory overload.</b> <c>AddHostedService(sp =&gt; …)</c> produces a
///   descriptor whose <c>ImplementationType</c> is <b>null</b>. A sweep keyed on
///   <c>ImplementationType</c> silently skips it — the single most likely way this
///   harness could have been written as a lie. Resolution falls through to the
///   factory delegate's generic argument and then to a NAMED pair list; a
///   descriptor that resolves by neither is a FAILURE, never a skip
///   (<see cref="EveryHostedServiceDescriptor_ResolvesToAType"/>).</item>
///   <item><b><c>TryAddEnumerable</c> inside an extension method.</b>
///   <c>PlatformTaskWorker</c> has no <c>AddHostedService&lt;&gt;</c> line anywhere
///   in <c>Program.cs</c>; it is registered in
///   <c>PlatformTaskServiceCollectionExtensions</c>. A source grep misses it
///   entirely — descriptor reflection sees it with a NON-null
///   <c>ImplementationType</c>, which is exactly why this harness reflects the
///   collection rather than the source
///   (<see cref="PlatformTaskWorker_isSeenByTheRegistrationSweep"/>).</item>
/// </list>
///
/// <para><b>WHAT THIS SWEEP CANNOT SEE</b> — recorded so a green run is not
/// over-read:</para>
/// <list type="bullet">
///   <item><b>The <c>Tamma.ElsaServer</c> host.</b> Its six hosted services are
///   registered in a DIFFERENT PROCESS whose composition this assembly cannot boot
///   (<c>Tamma.Api.Tests</c> does not reference <c>Tamma.ElsaServer</c>, and its
///   host needs an Elsa/EF composition of its own). They are covered at CLASS level
///   by <c>BackgroundActorCatalogSweepTests</c> only. Rather than leave that as an
///   invisible gap, the actors concerned are named and count-pinned in
///   <see cref="ActorsRegisteredInTheElsaServerHostOnly"/>, so a new ElsaServer
///   actor still fails a pin here.</item>
///   <item><b>Config-conditional registrations.</b> The test host runs single-user
///   with no Cranl key; a hosted service behind a SaaS-only branch is absent from
///   the collection and is recorded in
///   <see cref="ActorsNotRegisteredInTheTestHost"/>.</item>
///   <item><b>What an actor DOES.</b> A registration is bound to a catalog member,
///   not to a behaviour.</item>
/// </list>
/// </summary>
[TestFixture]
public class BackgroundActorRegistrationSweepTests
{
    // ====================================================================
    // Named lists — each a RATCHET: justified, staleness-checked, count-pinned
    // ====================================================================

    /// <summary>
    /// Descriptors whose implementation type can be resolved by NEITHER
    /// <c>ImplementationType</c>, <c>ImplementationInstance</c>, nor the factory
    /// delegate's generic argument — mapped by hand rather than skipped (43-8 AC5).
    /// Keyed by the descriptor's ordinal position among <c>IHostedService</c>
    /// descriptors, which is stable for a given composition and changes loudly.
    /// Empty means every descriptor resolves structurally, which is the good case —
    /// it is NOT the same as "there are no factory registrations"; see
    /// <see cref="FactoryOverloadRegistration_existsAndIsNotSkipped"/>.
    /// </summary>
    private static readonly IReadOnlyDictionary<int, string> KnownFactoryRegisteredServices =
        new Dictionary<int, string>();

    /// <summary>
    /// <c>automation:*</c> members whose hosted service is registered by the
    /// <c>Tamma.ElsaServer</c> host, which this harness cannot boot. Named and
    /// count-pinned so the blind spot is a declared, bounded set rather than
    /// silence.
    /// </summary>
    private static readonly IReadOnlySet<BackgroundActor> ActorsRegisteredInTheElsaServerHostOnly =
        new HashSet<BackgroundActor>
        {
            BackgroundActor.HourlyAnalyticsRollupScheduler,
            BackgroundActor.TenantCleanupRequestedTrigger,
            BackgroundActor.TenantDeleteRequestedTrigger,
            BackgroundActor.WorkflowSeeder,
            BackgroundActor.AgentSeeder,
            BackgroundActor.TenantScheduledTriggerService,
            // 2026-08-18 — autonomous-loop liveness. Both are AddHostedService lines in
            // Tamma.ElsaServer/Program.cs alongside HourlyAnalyticsRollupScheduler, so the
            // same blind spot applies: this harness cannot boot that host.
            BackgroundActor.AdlLoopWatchdogService,
            BackgroundActor.OrphanedCycleRecoveryService,
        };

    /// <summary>
    /// <c>Tamma.Api</c> actors that the TEST host does not register, with the
    /// configuration branch responsible. Seeded from the sweep itself.
    /// </summary>
    private static readonly IReadOnlyDictionary<BackgroundActor, string> ActorsNotRegisteredInTheTestHost =
        new Dictionary<BackgroundActor, string>
        {
            // SEEDED 2026-07-29 from the sweep itself. All four sit inside a
            // configuration branch that the Development/single-user test host does
            // not take. This is a REAL LIMIT on what this harness proves, not a
            // formality: in a deployment where these branches are on, four
            // background actors run that this sweep never examined.
            [BackgroundActor.SecretAutoRotationScheduler] =
                "conditional registration: inside Program.cs's secret-cabinet block, which the test "
                + "host does not enter (no cabinet wiring configured)",
            [BackgroundActor.RetireSweep] =
                "conditional registration: same secret-cabinet block as the auto-rotation scheduler",
            [BackgroundActor.RevealTokenSweeper] =
                "conditional registration: registered by AddSecretReveal, reached only from the "
                + "secret-cabinet block the test host does not enter",
            [BackgroundActor.TenantStatusInvalidationListener] =
                "conditional registration: Program.cs registers it (via the FACTORY overload) only "
                + "when a control-plane connection string is configured; the single-user test host "
                + "has none, so the null-ImplementationType descriptor is ABSENT here and the "
                + "factory-shape handling is proven by "
                + "Discrimination_aFactoryRegisteredHostedServiceHasNullImplementationType_andStillResolves "
                + "rather than by the live host",
        };

    /// <summary>
    /// Hosted services the FRAMEWORK registers. They are not Tamma capabilities and
    /// are not catalogued; named individually and count-pinned so "not ours" cannot
    /// become a wildcard that swallows a Tamma service.
    /// </summary>
    private static readonly IReadOnlySet<string> FrameworkHostedServices =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckPublisherHostedService",
            "Microsoft.AspNetCore.DataProtection.Internal.DataProtectionHostedService",
            "Microsoft.AspNetCore.Hosting.GenericWebHostService",
        };

    private static readonly string[] JustificationKeywords =
    [
        "conditional registration",
        "test-host configuration",
        "different host process",
    ];

    // ====================================================================
    // Discovery + the classifier
    // ====================================================================

    /// <summary>Every <see cref="IHostedService"/> descriptor of the built host, in order.</summary>
    private static IReadOnlyList<ServiceDescriptor> HostedServiceDescriptors() =>
        GovernanceHostFixture.ServiceDescriptors
            .Where(d => d.ServiceType == typeof(IHostedService))
            .ToArray();

    /// <summary>
    /// Resolves a descriptor's implementation type WITHOUT skipping the awkward
    /// shapes. Returns null only when every avenue fails — and the caller turns
    /// that into a failure, never a silent skip.
    /// </summary>
    internal static Type? ResolveImplementationType(ServiceDescriptor descriptor, int ordinal)
    {
        if (descriptor.ImplementationType is not null) return descriptor.ImplementationType;
        if (descriptor.ImplementationInstance is not null) return descriptor.ImplementationInstance.GetType();

        if (descriptor.ImplementationFactory is not null)
        {
            // AddHostedService<T>(Func<IServiceProvider, T>) hands the SAME delegate
            // instance to the descriptor — delegate covariance is a reference
            // conversion, so the runtime type is still Func<IServiceProvider, T> and
            // its second generic argument is the real implementation type. When a
            // caller erased that (a Func<IServiceProvider, IHostedService> built by
            // hand), this yields an interface and we fall through to the named list.
            var args = descriptor.ImplementationFactory.GetType().GenericTypeArguments;
            if (args.Length == 2 && args[1] != typeof(object) && !args[1].IsInterface)
                return args[1];
        }

        if (KnownFactoryRegisteredServices.TryGetValue(ordinal, out var typeName))
            return Type.GetType(typeName) ?? AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType(typeName))
                .FirstOrDefault(t => t is not null);

        return null;
    }

    private static readonly IReadOnlyDictionary<string, BackgroundActor> ActorsBySiteKey =
        Enum.GetValues<BackgroundActor>()
            .ToDictionary(
                a => ActionCatalog.Get(new ActionKey(ActionNamespace.Automation, a.ToWire())).SiteKey,
                a => a,
                StringComparer.Ordinal);

    // ====================================================================
    // The sweep against reality
    // ====================================================================

    [Test]
    public void The_sweep_actually_sees_hosted_service_registrations()
    {
        // ANTI-NO-OP TRIPWIRE. If the descriptor capture ever stops running (the
        // ConfigureServices hook moved, the service type changed), every assertion
        // below would pass vacuously on an empty list.
        HostedServiceDescriptors().Should().HaveCountGreaterThan(10,
            "the Tamma.Api host registers well over ten hosted services; an empty or tiny result "
            + "means the descriptor capture in GovernanceHostFixture broke");
    }

    [Test]
    public void EveryHostedServiceDescriptor_ResolvesToAType()
    {
        // The anti-skip assertion. A descriptor whose type cannot be determined is a
        // FAILURE — the alternative (skipping it) is precisely how a sweep reads as
        // coverage while covering nothing.
        var unresolved = HostedServiceDescriptors()
            .Select((d, i) => (Descriptor: d, Ordinal: i))
            .Where(x => ResolveImplementationType(x.Descriptor, x.Ordinal) is null)
            .Select(x =>
                $"  descriptor #{x.Ordinal} (lifetime {x.Descriptor.Lifetime}, "
                + $"factory={x.Descriptor.ImplementationFactory is not null}): implementation type "
                + "could not be resolved. Add a KnownFactoryRegisteredServices entry mapping this "
                + "ordinal to the implementation type's full name — do NOT skip it.")
            .ToList();

        unresolved.Should().BeEmpty(
            "every IHostedService registration must be attributable to a class:"
            + Environment.NewLine + string.Join(Environment.NewLine, unresolved));
    }

    [Test]
    public void FactoryOverloadRegistrations_areResolvedNotSkipped()
    {
        // Story 43-8 calls the factory overload out because a naive sweep misses
        // exactly it. MEASURED 2026-07-29: this host has ZERO factory-registered
        // hosted services — the one production instance
        // (TenantStatusInvalidationListener) sits behind a control-plane
        // connection-string branch the single-user test host does not take, so it is
        // in ActorsNotRegisteredInTheTestHost.
        //
        // That is stated rather than papered over, and it is WHY the handling of the
        // shape is proven against a synthetic ServiceCollection in
        // Discrimination_aFactoryRegisteredHostedServiceHasNullImplementationType_andStillResolves
        // instead of against the live host. Asserting NotBeEmpty here would be a
        // pin on a thing that does not exist; asserting nothing would leave the
        // shape unexercised. The assertion that IS meaningful over the live host is
        // that any such descriptor resolves.
        var factoryRegistrations = HostedServiceDescriptors()
            .Select((d, i) => (Descriptor: d, Ordinal: i))
            .Where(x => x.Descriptor.ImplementationType is null && x.Descriptor.ImplementationFactory is not null)
            .ToList();

        foreach (var (descriptor, ordinal) in factoryRegistrations)
        {
            ResolveImplementationType(descriptor, ordinal).Should().NotBeNull(
                $"factory-registered descriptor #{ordinal} must be mapped to a type, never skipped");
        }

        // Pin the measured fact, so the day a factory registration DOES appear in
        // this host, someone reads this comment instead of assuming it was always
        // covered.
        factoryRegistrations.Should().HaveCount(0,
            "measured 2026-07-29: the single-user Development test host registers no hosted service "
            + "through the factory overload. If this fails, the live host now exercises the "
            + "null-ImplementationType path — good; confirm ResolveImplementationType handled it and "
            + "update this pin.");
    }

    [Test]
    public void PlatformTaskWorker_isSeenByTheRegistrationSweep()
    {
        // D2 REGRESSION PIN. PlatformTaskWorker is registered by a TryAddEnumerable
        // inside an extension method — there is no AddHostedService<> line for it
        // anywhere. If someone converts that registration into a shape this sweep
        // cannot see, the sweep must go RED, not quiet.
        RegisteredImplementationTypes().Select(t => t.FullName)
            .Should().Contain("Tamma.Api.Services.PlatformTasks.PlatformTaskWorker");
    }

    /// <summary>
    /// THE code → catalog rule, extracted as a pure function over its inputs (review
    /// F18(a)) so the discrimination test below can DRIVE IT with synthetic input
    /// instead of asserting a precondition and hoping the rule would have fired.
    /// </summary>
    internal static List<string> ClassifyRegistrations(
        IReadOnlyList<Type> registered,
        IReadOnlySet<string> frameworkExemptions,
        IReadOnlyDictionary<string, BackgroundActor> actorsBySiteKey) =>
        registered
            .Where(t => !frameworkExemptions.Contains(t.FullName!))
            .Where(t => !actorsBySiteKey.ContainsKey(t.FullName!))
            .Select(t =>
                $"  {t.FullName}: registered as an IHostedService but has no automation:* catalog "
                + "member. Add a BackgroundActor member and a descriptor whose SiteKey is this full "
                + "type name — a background actor is the one capability class that runs with no human "
                + "in the loop at all.")
            .Distinct()
            .ToList();

    [Test]
    public void EveryRegisteredHostedService_IsACataloguedAutomationMember()
    {
        var uncatalogued = ClassifyRegistrations(
            RegisteredImplementationTypes(), FrameworkHostedServices, ActorsBySiteKey);

        uncatalogued.Should().BeEmpty(
            "every registered background actor must be catalogued:"
            + Environment.NewLine + string.Join(Environment.NewLine, uncatalogued));
    }

    [Test]
    public void EveryAutomationMember_HasARegistrationOrANamedReason()
    {
        var registered = RegisteredImplementationTypes()
            .Select(t => t.FullName!)
            .ToHashSet(StringComparer.Ordinal);

        var problems = new List<string>();

        foreach (var actor in Enum.GetValues<BackgroundActor>())
        {
            var siteKey = ActionCatalog.Get(new ActionKey(ActionNamespace.Automation, actor.ToWire())).SiteKey;
            if (registered.Contains(siteKey)) continue;
            if (ActorsRegisteredInTheElsaServerHostOnly.Contains(actor)) continue;
            if (ActorsNotRegisteredInTheTestHost.ContainsKey(actor)) continue;

            problems.Add(
                $"  automation:{actor.ToWire()} ({siteKey}): nothing in the Tamma.Api host registers "
                + "it. If the actor is gone, DELETE the BackgroundActor member and its descriptor — a "
                + "catalogued actor with no registration renders in the admin UI as a governed thing "
                + "that runs, and it does not run. If it belongs to the ElsaServer host or to a "
                + "configuration branch this host does not take, add it to the corresponding named "
                + "list with a reason.");
        }

        // Staleness: a named exception that IS registered here is dead weight.
        problems.AddRange(ActorsRegisteredInTheElsaServerHostOnly
            .Where(a => registered.Contains(
                ActionCatalog.Get(new ActionKey(ActionNamespace.Automation, a.ToWire())).SiteKey))
            .Select(a => $"  automation:{a.ToWire()}: listed as ElsaServer-only but the Tamma.Api host "
                         + "registers it — delete the entry."));

        problems.AddRange(ActorsNotRegisteredInTheTestHost.Keys
            .Where(a => registered.Contains(
                ActionCatalog.Get(new ActionKey(ActionNamespace.Automation, a.ToWire())).SiteKey))
            .Select(a => $"  automation:{a.ToWire()}: listed as not-registered-in-the-test-host but it "
                         + "IS registered — delete the entry."));

        problems.Should().BeEmpty(
            "the automation:* plane and the host's registrations must agree in BOTH directions:"
            + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    [Test]
    public void ElsaServerOnlyActors_countIsPinned()
    {
        // The blind spot is DECLARED and BOUNDED. A seventh ElsaServer hosted
        // service fails here even though this harness cannot boot that host.
        // 6 → 8 (2026-08-18): + AdlLoopWatchdogService and OrphanedCycleRecoveryService,
        // the autonomous-loop liveness pair, registered next to the analytics scheduler.
        ActorsRegisteredInTheElsaServerHostOnly.Should().HaveCount(8,
            "Tamma.ElsaServer/Program.cs has exactly eight AddHostedService lines. A change means "
            + "the un-swept blind spot grew or shrank — say so explicitly rather than letting it "
            + "drift.");
    }

    [Test]
    public void NotRegisteredInTestHost_isJustifiedAndPinned()
    {
        var unclassified = ActorsNotRegisteredInTheTestHost
            .Where(kv => string.IsNullOrWhiteSpace(kv.Value)
                         || !JustificationKeywords.Any(k => kv.Value.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .Select(kv => $"  automation:{kv.Key.ToWire()}: {kv.Value}")
            .ToList();

        unclassified.Should().BeEmpty(
            "every not-registered-here entry must name why (["
            + string.Join(", ", JustificationKeywords) + "]):"
            + Environment.NewLine + string.Join(Environment.NewLine, unclassified));

        ActorsNotRegisteredInTheTestHost.Should().HaveCount(4,
            "the un-swept set may only SHRINK; a new entry is a new blind spot and must be a "
            + "deliberate, reviewed addition");
    }

    [Test]
    public void FrameworkHostedServices_areNamedAndCountPinned()
    {
        // The exemption must be a NAMED SET, never a rule like "types outside the
        // Tamma.* namespace". A rule would swallow a future Tamma service that
        // happens to live elsewhere; the list forces a decision each time.
        var registered = RegisteredImplementationTypes().Select(t => t.FullName!).ToHashSet(StringComparer.Ordinal);

        FrameworkHostedServices.Should().HaveCount(3,
            "the host picks up exactly three framework hosted services (health-check publisher, "
            + "data protection, and the web host itself)");

        var stale = FrameworkHostedServices.Where(n => !registered.Contains(n)).ToList();
        stale.Should().BeEmpty(
            "a framework exemption for a service that is no longer registered is dead weight: "
            + string.Join(", ", stale));

        FrameworkHostedServices.Should().OnlyContain(n => n.StartsWith("Microsoft.", StringComparison.Ordinal),
            "only framework services belong in this list — a Tamma service must be catalogued, "
            + "not exempted");
    }

    private static IReadOnlyList<Type> RegisteredImplementationTypes() =>
        HostedServiceDescriptors()
            .Select((d, i) => ResolveImplementationType(d, i))
            .Where(t => t is not null)
            .Select(t => t!)
            .Distinct()
            .ToArray();

    // ====================================================================
    // DISCRIMINATION PROOFS — driven through the REAL resolver
    // ====================================================================

    private sealed class UncataloguedFixtureHostedService : BackgroundService
    {
        protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.CompletedTask;
    }

    [Test]
    public void Discrimination_aTypeRegisteredHostedServiceResolves()
    {
        var services = new ServiceCollection();
        services.AddHostedService<UncataloguedFixtureHostedService>();
        var descriptor = services.Single(d => d.ServiceType == typeof(IHostedService));

        descriptor.ImplementationType.Should().Be(typeof(UncataloguedFixtureHostedService));
        ResolveImplementationType(descriptor, 0).Should().Be(typeof(UncataloguedFixtureHostedService));
    }

    [Test]
    public void Discrimination_aFactoryRegisteredHostedServiceHasNullImplementationType_andStillResolves()
    {
        // THE proof that this harness does not have the blind spot the story warns
        // about: the descriptor really does carry a null ImplementationType, and the
        // real resolver still returns the concrete class.
        var services = new ServiceCollection();
        services.AddSingleton<UncataloguedFixtureHostedService>();
        services.AddHostedService(sp => sp.GetRequiredService<UncataloguedFixtureHostedService>());
        var descriptor = services.Single(d => d.ServiceType == typeof(IHostedService));

        descriptor.ImplementationType.Should().BeNull(
            "this is the exact shape a naive ImplementationType-keyed sweep silently skips");
        ResolveImplementationType(descriptor, 0).Should().Be(typeof(UncataloguedFixtureHostedService),
            "the resolver must see through the factory overload");
    }

    [Test]
    public void Discrimination_anErasedFactoryIsUnresolvable_soTheSweepWouldFail()
    {
        // The residual case the named list exists for: a hand-built
        // Func<IServiceProvider, IHostedService> erases the implementation type. The
        // resolver must return null (→ EveryHostedServiceDescriptor_ResolvesToAType
        // fails) rather than quietly dropping the registration.
        IServiceCollection services = new ServiceCollection();
        services.AddSingleton<UncataloguedFixtureHostedService>();
        services.Add(new ServiceDescriptor(
            typeof(IHostedService),
            (Func<IServiceProvider, object>)(sp => sp.GetRequiredService<UncataloguedFixtureHostedService>()),
            ServiceLifetime.Singleton));
        var descriptor = services.Single(d => d.ServiceType == typeof(IHostedService));

        ResolveImplementationType(descriptor, 999).Should().BeNull(
            "an unresolvable descriptor must surface as a failure, never as a skip");
    }

    [Test]
    public void Discrimination_anUncataloguedHostedServiceIsReported()
    {
        // STRENGTHENED 2026-07-29 (review F18(a)). This test used to assert only that
        // the fixture type was absent from ActorsBySiteKey — a precondition, not the
        // rule — so its name overclaimed: it would have stayed green even if the rule
        // had been gutted. It now runs the REAL rule
        // (ClassifyRegistrations, the same function
        // EveryRegisteredHostedService_IsACataloguedAutomationMember calls) over the
        // real catalog lookup, with the fixture type as the registered surface.
        typeof(UncataloguedFixtureHostedService).FullName.Should().NotBeNull();

        var problems = ClassifyRegistrations(
            [typeof(UncataloguedFixtureHostedService)], FrameworkHostedServices, ActorsBySiteKey);

        problems.Should().ContainSingle(
            "an uncatalogued hosted service MUST be reported — if it is not, a background actor can "
            + "start running unattended with no catalog member and this whole fixture reads as "
            + "coverage while covering nothing")
            .Which.Should().Contain(nameof(UncataloguedFixtureHostedService));
    }

    [Test]
    public void Discrimination_aCataloguedHostedServiceIsNotReported()
    {
        // The complement: prove the rule is not simply always-red. Feed it a type
        // whose full name IS a catalogued automation SiteKey, taken from the real
        // catalog rather than from a literal.
        var catalogued = RegisteredImplementationTypes()
            .First(t => ActorsBySiteKey.ContainsKey(t.FullName!));

        ClassifyRegistrations([catalogued], FrameworkHostedServices, ActorsBySiteKey)
            .Should().BeEmpty();
    }

    [Test]
    public void Discrimination_aFrameworkHostedServiceIsExempted_butOnlyByName()
    {
        // The exemption arm, driven rather than described: a framework service is
        // silent, and an IDENTICALLY-SHAPED type that is not on the named list is not.
        var frameworkName = FrameworkHostedServices.First();
        var frameworkType = RegisteredImplementationTypes().First(t => t.FullName == frameworkName);

        ClassifyRegistrations([frameworkType], FrameworkHostedServices, ActorsBySiteKey)
            .Should().BeEmpty("a named framework service is exempt");

        ClassifyRegistrations(
                [frameworkType],
                new HashSet<string>(StringComparer.Ordinal),
                ActorsBySiteKey)
            .Should().ContainSingle(
                "the exemption comes ONLY from the named list — there is no namespace rule that could "
                + "swallow a future Tamma service that happens to live elsewhere");
    }

    [Test]
    public void Discrimination_theCatalogLookupIsPopulated()
    {
        // Complement: the map the sweep matches against must be non-empty, or every
        // "is catalogued" check would be trivially false and every "has a
        // registration" check trivially unsatisfiable.
        ActorsBySiteKey.Should().HaveCount(Enum.GetValues<BackgroundActor>().Length);
        ActorsBySiteKey.Keys.Should().OnlyContain(k => k.Contains('.'),
            "automation SiteKeys are full type names");
    }
}
