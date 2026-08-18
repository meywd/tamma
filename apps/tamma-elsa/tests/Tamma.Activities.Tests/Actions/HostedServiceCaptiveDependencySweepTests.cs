using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Hosting;
using NUnit.Framework;

namespace Tamma.Activities.Tests.Actions;

/// <summary>
/// REFLECTION SWEEP over every <see cref="IHostedService"/> class in the three
/// production assemblies, enforcing one rule: <b>a hosted service must not take an
/// Elsa service in its constructor.</b>
///
/// <para><b>Why this test exists.</b> Hosted services are registered as singletons.
/// Elsa registers its runtime services (<c>IWorkflowDispatcher</c>,
/// <c>IWorkflowDefinitionService</c>, <c>IWorkflowInstanceStore</c>, …) SCOPED. Holding
/// one in a singleton constructor is a captive dependency: with
/// <c>ValidateScopes</c> on — which <c>WebApplicationBuilder</c> turns on in
/// Development — the host refuses to build at all, and in Production one Elsa service,
/// and the database session behind it, is silently promoted to process lifetime.</para>
///
/// <para>The rule is not hypothetical and not a one-off. On 2026-08-18
/// <c>HourlyAnalyticsRollupScheduler</c> had exactly this defect removed, and on the
/// same day <c>AdlLoopWatchdogService</c> was written with it in a parallel worktree —
/// nine lines below in the same <c>Program.cs</c>. The per-class pin added with the
/// first fix (<c>Scheduler_ResolvesUnderScopeValidation_NoCaptiveDependency</c>) could
/// not catch the second, because it builds only its own scheduler. A sweep catches the
/// class of defect rather than one instance of it.</para>
///
/// <para><b>The remedy is always the same</b>: take <c>IServiceScopeFactory</c>, create a
/// scope per tick, and resolve the Elsa service inside it.</para>
///
/// <para>LIMITATION, stated so it is not mistaken for more than it is: this is a
/// TYPE-level check keyed on the declaring assembly of the parameter type. It catches
/// Elsa services specifically — the ones this codebase has actually got wrong twice —
/// not every scoped registration in the container. A scoped Tamma service held by a
/// singleton would still slip through; only a composed host with ValidateOnBuild sees
/// those.</para>
/// </summary>
[TestFixture]
public class HostedServiceCaptiveDependencySweepTests
{
    private static IReadOnlyList<Assembly> SweptAssemblies() =>
        new[]
        {
            typeof(Tamma.Activities.LlmCall.Tools.GitOperationsTool).Assembly, // Tamma.Activities
            typeof(Tamma.Api.Services.PlatformTasks.PlatformTaskWorker).Assembly, // Tamma.Api
            typeof(Tamma.ElsaServer.WorkflowSeeder).Assembly, // Tamma.ElsaServer
        };

    private static IEnumerable<Type> HostedServices() =>
        SweptAssemblies()
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsAbstract: false, IsInterface: false }
                        && typeof(IHostedService).IsAssignableFrom(t));

    /// <summary>
    /// An Elsa service is anything declared by an <c>Elsa.*</c> assembly. Concrete
    /// option/record types are not services, so only interfaces count.
    /// </summary>
    private static bool IsElsaService(Type t) =>
        t.IsInterface && (t.Assembly.GetName().Name?.StartsWith("Elsa.", StringComparison.Ordinal) ?? false);

    /// <summary>
    /// Guard against a VACUOUS pass. If the reflection query silently returned nothing —
    /// a renamed anchor type, a dropped project reference — the sweep below would report
    /// "no offenders" forever while checking nothing at all.
    /// </summary>
    [Test]
    public void TheSweep_ActuallyFindsTheHostedServices()
    {
        HostedServices().Should().HaveCountGreaterThan(30,
            "the catalog pins 35 background actors; a sweep finding far fewer is not "
            + "scanning the host assemblies");
    }

    [Test]
    public void NoHostedService_TakesAnElsaServiceInItsConstructor()
    {
        var offenders = new List<string>();

        foreach (var type in HostedServices())
        {
            foreach (var ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
            {
                foreach (var p in ctor.GetParameters().Where(p => IsElsaService(p.ParameterType)))
                {
                    offenders.Add($"{type.FullName}.ctor({p.ParameterType.Name} {p.Name})");
                }
            }
        }

        offenders.Should().BeEmpty(
            "a hosted service is a singleton and Elsa services are scoped — take "
            + "IServiceScopeFactory and resolve inside a per-tick scope instead. "
            + "Offenders: {0}", string.Join(", ", offenders));
    }
}
