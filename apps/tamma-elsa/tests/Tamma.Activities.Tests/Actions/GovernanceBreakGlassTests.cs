using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Tamma.Activities.Tests.LlmCall; // FakeTimeProvider (local test helper)
using Tamma.Api.Services.Actions;
using Tamma.Core.Actions;

namespace Tamma.Activities.Tests.Actions;

/// <summary>
/// Story 43-5 follow-up <b>F11</b>, closed 2026-07-30 — the CONFIGURATION SOURCE
/// of the break-glass override.
///
/// <para>Two product decisions are what these tests actually protect:</para>
///
/// <list type="number">
/// <item><b>The expiry is mandatory.</b> Missing, unparseable or already-past ⇒
/// the override refuses to engage. A break-glass that can be left on forever is
/// not a break-glass; it is the permanent configuration, which is exactly the
/// fail-open the F6 close removed.</item>
/// <item><b>The state is captured at construction.</b> Engaging requires a
/// configuration change AND a restart. Only EXPIRY is re-evaluated per call,
/// because expiry has to be able to arrive while the process runs. There is
/// deliberately no endpoint and no writer: an API that can switch off a
/// governance posture is itself a governance surface.</item>
/// </list>
/// </summary>
[TestFixture]
public class GovernanceBreakGlassTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-07-30T12:00:00Z");

    private static ConfigurationGovernanceBreakGlass Build(
        FakeTimeProvider time, params (string Key, string? Value)[] settings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s =>
                new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
        return new ConfigurationGovernanceBreakGlass(config, logger: null, timeProvider: time);
    }

    [Test]
    public void NoConfiguration_IsNotEngaged()
    {
        Build(new FakeTimeProvider(Now)).Current().Should().Be(BreakGlassState.NotEngaged);
    }

    [Test]
    public void EnabledFalse_IsNotEngaged_EvenWithAValidFutureExpiry()
    {
        var bg = Build(new FakeTimeProvider(Now),
            (ConfigurationGovernanceBreakGlass.EnabledKey, "false"),
            (ConfigurationGovernanceBreakGlass.ExpiresAtUtcKey, "2026-07-30T18:00:00Z"));

        bg.Current().IsEngaged.Should().BeFalse();
    }

    [Test]
    public void EnabledWithAFutureExpiry_Engages_AndCarriesTheExpiryAndReason()
    {
        var bg = Build(new FakeTimeProvider(Now),
            (ConfigurationGovernanceBreakGlass.EnabledKey, "true"),
            (ConfigurationGovernanceBreakGlass.ExpiresAtUtcKey, "2026-07-30T18:00:00Z"),
            (ConfigurationGovernanceBreakGlass.ReasonKey, "control plane unreachable, INC-4412"));

        var state = bg.Current();

        state.IsEngaged.Should().BeTrue();
        state.ExpiresAtUtc.Should().Be(DateTimeOffset.Parse("2026-07-30T18:00:00Z"));
        state.Reason.Should().Be("control plane unreachable, INC-4412");
    }

    // ── The refusals ────────────────────────────────────────────────────────

    [Test]
    public void EnabledWithNoExpiry_REFUSES_ToEngage()
    {
        var bg = Build(new FakeTimeProvider(Now),
            (ConfigurationGovernanceBreakGlass.EnabledKey, "true"));

        bg.Current().IsEngaged.Should().BeFalse(
            "an override with no end becomes the permanent configuration; the fail-closed "
            + "posture stays in force and the refusal is logged at ERROR");
    }

    [TestCase("soon")]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("2026-13-45T99:99:99Z")]
    public void EnabledWithAnUnparseableExpiry_REFUSES_ToEngage(string raw)
    {
        var bg = Build(new FakeTimeProvider(Now),
            (ConfigurationGovernanceBreakGlass.EnabledKey, "true"),
            (ConfigurationGovernanceBreakGlass.ExpiresAtUtcKey, raw));

        bg.Current().IsEngaged.Should().BeFalse();
    }

    [Test]
    public void EnabledWithAnAlreadyPastExpiry_REFUSES_ToEngage()
    {
        var bg = Build(new FakeTimeProvider(Now),
            (ConfigurationGovernanceBreakGlass.EnabledKey, "true"),
            (ConfigurationGovernanceBreakGlass.ExpiresAtUtcKey, "2026-07-30T11:59:59Z"));

        bg.Current().IsEngaged.Should().BeFalse(
            "a stale expiry left in configuration must not re-engage the override on the "
            + "next restart");
    }

    // ── Expiry arrives while the process runs ───────────────────────────────

    [Test]
    public void AnEngagedOverride_StopsBeingEngaged_TheInstantItExpires()
    {
        var time = new FakeTimeProvider(Now);
        var bg = Build(time,
            (ConfigurationGovernanceBreakGlass.EnabledKey, "true"),
            (ConfigurationGovernanceBreakGlass.ExpiresAtUtcKey, "2026-07-30T13:00:00Z"));

        bg.Current().IsEngaged.Should().BeTrue();

        time.Advance(TimeSpan.FromMinutes(59));
        bg.Current().IsEngaged.Should().BeTrue();

        time.Advance(TimeSpan.FromMinutes(1)); // exactly at the expiry
        bg.Current().IsEngaged.Should().BeFalse(
            "expiry is inclusive-closed: at the stated instant the override is over");
        bg.Current().Should().Be(BreakGlassState.NotEngaged,
            "an expired override is indistinguishable from one that was never set — there is "
            + "no lingering half-engaged state");
    }

    // ── Expiry parsing ──────────────────────────────────────────────────────

    [Test]
    public void ABareTimestamp_IsReadAsUtc_NotAsServerLocalTime()
    {
        // The key is named ExpiresAtUtc. Reading a bare instant as LOCAL time
        // would make the same configuration mean different things on two hosts —
        // and could silently extend or shorten the window by hours.
        ConfigurationGovernanceBreakGlass.TryParseUtc("2026-07-30T18:00:00", out var parsed)
            .Should().BeTrue();
        parsed.Should().Be(DateTimeOffset.Parse("2026-07-30T18:00:00Z"));
    }

    [Test]
    public void AnExplicitOffset_IsHonoured()
    {
        ConfigurationGovernanceBreakGlass.TryParseUtc("2026-07-30T20:00:00+02:00", out var parsed)
            .Should().BeTrue();
        parsed.Should().Be(DateTimeOffset.Parse("2026-07-30T18:00:00Z"));
    }

    // ── The shape of the lever ──────────────────────────────────────────────

    /// <summary>
    /// Config-sourced, not an endpoint — pinned structurally. If a writer ever
    /// appears on this type, the override becomes flippable from inside a running
    /// process (and, one step later, from an HTTP request), which is the design
    /// this decision explicitly rejected: an endpoint that can switch off
    /// governance is itself a governance hole that would need governing.
    /// </summary>
    [Test]
    public void TheOverrideHasNoWriter()
    {
        typeof(IGovernanceBreakGlass).GetMethods()
            .Should().OnlyContain(m => m.Name == nameof(IGovernanceBreakGlass.Current),
                "the break-glass contract is READ-ONLY; there is no Engage/Disengage/Set");

        typeof(ConfigurationGovernanceBreakGlass).GetProperties()
            .Should().NotContain(p => p.CanWrite,
                "no settable state — the source of truth is configuration plus a restart");

        typeof(ConfigurationGovernanceBreakGlass)
            .GetMethods(System.Reflection.BindingFlags.Public
                        | System.Reflection.BindingFlags.Instance
                        | System.Reflection.BindingFlags.DeclaredOnly)
            .Should().OnlyContain(m => m.Name == nameof(IGovernanceBreakGlass.Current),
                "the implementation exposes no mutator either — engaging is a config change "
                + "plus a restart, and that friction is the feature");
    }
}
