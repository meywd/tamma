using System.Reflection;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Tracking;

namespace Tamma.Core.Tests.Tracking;

/// <summary>
/// Story 44-0 AC15 — the core is pure: no EF, no HttpClient, no ILogger, no
/// I/O, and every public type in <c>Tamma.Core.Tracking</c> is constructible
/// without DI. <c>Tamma.Core</c> must also keep zero project references — it is
/// the one assembly every other assembly can reach, which is the whole reason
/// the tracker vocabulary lives there.
/// </summary>
[TestFixture]
public class TrackingPurityTests
{
    private static readonly string[] ForbiddenNamespacePrefixes =
    [
        "System.Net.Http",
        "Microsoft.EntityFrameworkCore",
        "Microsoft.Extensions.Logging",
        "Microsoft.Extensions.DependencyInjection",
    ];

    private static Type[] TrackingTypes() =>
        typeof(WorkItemKind).Assembly.GetTypes()
            .Where(t => t.Namespace == "Tamma.Core.Tracking" && t.IsPublic)
            .ToArray();

    [Test]
    public void Namespace_has_no_io_dependencies()
    {
        var types = TrackingTypes();
        types.Should().NotBeEmpty();

        foreach (var type in types)
        {
            foreach (var ctor in type.GetConstructors())
            {
                foreach (var parameter in ctor.GetParameters())
                {
                    var ns = parameter.ParameterType.Namespace ?? "";
                    ForbiddenNamespacePrefixes.Should().NotContain(
                        prefix => ns.StartsWith(prefix, StringComparison.Ordinal),
                        because: $"{type.Name}..ctor({parameter.Name}) must not depend on I/O types");
                }
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                var ns = property.PropertyType.Namespace ?? "";
                ForbiddenNamespacePrefixes.Should().NotContain(
                    prefix => ns.StartsWith(prefix, StringComparison.Ordinal),
                    because: $"{type.Name}.{property.Name} must not expose I/O types");
            }
        }
    }

    [Test]
    public void Assembly_keeps_zero_tamma_references_and_no_io_stacks()
    {
        // Tamma.Core is reachable from Tamma.Data, Tamma.Activities,
        // Tamma.ElsaServer and Tamma.Api precisely because it references no
        // other Tamma assembly (zero ProjectReferences). If this fails, the
        // vocabulary has stopped being universally bindable.
        var referenced = typeof(WorkItemKind).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? "")
            .ToArray();

        referenced.Should().NotContain(
            name => name.StartsWith("Tamma.", StringComparison.Ordinal),
            because: "Tamma.Core must keep zero ProjectReferences");
        referenced.Should().NotContain(name =>
            name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ||
            name.StartsWith("Microsoft.Extensions.Logging", StringComparison.Ordinal) ||
            name.StartsWith("System.Net.Http", StringComparison.Ordinal));
    }

    [Test]
    public void Every_public_type_is_constructible_without_di()
    {
        foreach (var type in TrackingTypes())
        {
            var constructibleWithoutDi =
                type.IsEnum ||
                type.IsValueType ||
                (type.IsAbstract && type.IsSealed) || // static class
                type.GetConstructors().Any(c => c.GetParameters().All(IsPlainParameter));

            constructibleWithoutDi.Should().BeTrue(
                because: $"{type.Name} must be creatable with plain values, no container");
        }
    }

    private static bool IsPlainParameter(ParameterInfo parameter)
    {
        var t = parameter.ParameterType;
        if (t == typeof(string) || t.IsValueType || t.IsEnum)
            return true;

        // Plain collections of plain values (e.g. IReadOnlyList<string>) are fine.
        if (t.IsGenericType && t.GenericTypeArguments.All(a => a == typeof(string) || a.IsValueType))
            return true;

        return false;
    }
}
