using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Tamma.Activities.Debug;

namespace Tamma.Activities.Tests.Debug;

/// <summary>
/// Completeness audit 2026-06-22 (<c>Debugging.md</c> §Missing #11, AC4) — coverage for
/// the configurable context-collection timeout read
/// (<see cref="ContextCollectionTimeoutActivity.ResolveTimeoutSeconds"/>). The activity
/// itself arms a DURABLE <c>DelayFor</c> bookmark (not an in-memory scheduler), which is
/// runtime behaviour the structural workflow test pins; this unit covers the config seam.
/// </summary>
[TestFixture]
public class ContextCollectionTimeoutActivityTests
{
    private static IConfiguration Config(params (string Key, string Value)[] kvps)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(kvps.Select(k =>
                new KeyValuePair<string, string?>(k.Key, k.Value)))
            .Build();

    [Test]
    public void ResolveTimeoutSeconds_DefaultsTo15_WhenUnset()
    {
        ContextCollectionTimeoutActivity.ResolveTimeoutSeconds(Config()).Should().Be(15);
        ContextCollectionTimeoutActivity.ResolveTimeoutSeconds(null).Should().Be(15);
    }

    [Test]
    public void ResolveTimeoutSeconds_HonorsConfiguredValue()
    {
        var cfg = Config(("Debugging:ContextCollectionTimeoutSeconds", "30"));
        ContextCollectionTimeoutActivity.ResolveTimeoutSeconds(cfg).Should().Be(30);
    }

    [Test]
    public void ResolveTimeoutSeconds_NonPositive_DisablesGuard()
    {
        var cfg = Config(("Debugging:ContextCollectionTimeoutSeconds", "0"));
        ContextCollectionTimeoutActivity.ResolveTimeoutSeconds(cfg).Should().Be(0);

        var neg = Config(("Debugging:ContextCollectionTimeoutSeconds", "-5"));
        ContextCollectionTimeoutActivity.ResolveTimeoutSeconds(neg).Should().Be(0,
            "a negative value floors to disabled rather than arming a negative delay");
    }

    [Test]
    public void ResolveTimeoutSeconds_Garbage_FallsBackToDefault()
    {
        var cfg = Config(("Debugging:ContextCollectionTimeoutSeconds", "fifteen"));
        ContextCollectionTimeoutActivity.ResolveTimeoutSeconds(cfg).Should().Be(15);
    }
}
