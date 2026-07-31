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

    /// <summary>
    /// <b>LOW-4 (review 2026-07-31).</b> Expiry is LATCHED: once the process has
    /// seen the override expire, it stays expired for the lifetime of the process.
    /// It used to be re-derived from the clock on every call with nothing
    /// remembered, so a clock that went backwards — an NTP step correction, a
    /// suspended VM resuming, a host with a bad RTC — silently RE-ENGAGED the
    /// override. Silently is the operative word: the "EXPIRED" ERROR is latched by
    /// its own flag and the "ENGAGED" ERROR is constructor-only, so the second
    /// engagement produced ZERO log lines.
    /// </summary>
    [Test]
    public void AnExpiredOverride_StaysExpired_EvenIfTheClockGoesBackwards()
    {
        var time = new FakeTimeProvider(Now);
        var bg = Build(time,
            (ConfigurationGovernanceBreakGlass.EnabledKey, "true"),
            (ConfigurationGovernanceBreakGlass.ExpiresAtUtcKey, "2026-07-30T13:00:00Z"));

        bg.Current().IsEngaged.Should().BeTrue();

        time.Advance(TimeSpan.FromHours(1));
        bg.Current().IsEngaged.Should().BeFalse();

        time.Advance(TimeSpan.FromHours(-1)); // the clock steps back before the expiry
        bg.Current().Should().Be(BreakGlassState.NotEngaged,
            "an override that has already ended must not come back because the clock moved; "
            + "re-engaging is a configuration change plus a restart, and nothing else");
    }

    // ── MEDIUM-3: the maximum duration ──────────────────────────────────────

    /// <summary>
    /// A future expiry is not enough on its own: the window is CAPPED at 24 hours.
    /// Break-glass is an outage lever, and an outage still unresolved after a day
    /// needs a real fix rather than a longer bypass.
    /// </summary>
    [Test]
    public void EnabledWithAnExpiryBeyondTheMaximumDuration_REFUSES_ToEngage()
    {
        var bg = Build(new FakeTimeProvider(Now),
            (ConfigurationGovernanceBreakGlass.EnabledKey, "true"),
            (ConfigurationGovernanceBreakGlass.ExpiresAtUtcKey, "2026-07-31T12:00:01Z"));

        bg.Current().IsEngaged.Should().BeFalse(
            "one second past the 24h cap is past the cap; the refusal is logged at ERROR");
    }

    [Test]
    public void EnabledWithAnExpiryExactlyAtTheMaximumDuration_Engages()
    {
        var bg = Build(new FakeTimeProvider(Now),
            (ConfigurationGovernanceBreakGlass.EnabledKey, "true"),
            (ConfigurationGovernanceBreakGlass.ExpiresAtUtcKey, "2026-07-31T12:00:00Z"));

        bg.Current().IsEngaged.Should().BeTrue("the cap is inclusive — exactly 24h is allowed");
    }

    /// <summary>
    /// The concrete failure MEDIUM-3 named: only <c>expiresAt &lt;= now</c> was
    /// rejected, so a year-9999 expiry engaged and stayed engaged — precisely the
    /// "left on forever" outcome the mandatory expiry exists to prevent, wearing a
    /// timestamp.
    /// </summary>
    [Test]
    public void AFarFutureExpiry_REFUSES_ToEngage()
    {
        var bg = Build(new FakeTimeProvider(Now),
            (ConfigurationGovernanceBreakGlass.EnabledKey, "true"),
            (ConfigurationGovernanceBreakGlass.ExpiresAtUtcKey, "9999-12-31T23:59:59Z"));

        bg.Current().IsEngaged.Should().BeFalse(
            "a mandatory expiry with no upper bound is not a bound");
    }

    /// <summary>
    /// The permissive parses are INTENDED — <c>DateTimeOffset.TryParse</c> accepts
    /// partial forms, and rejecting them would only push operators toward
    /// copy-pasted full timestamps they understand less. What was missing was the
    /// bound: a month-precision value engaged for MONTHS. The cap is what makes the
    /// permissiveness safe, so these are pinned as REFUSALS rather than as parse
    /// failures.
    /// </summary>
    [TestCase("2026-12")]
    [TestCase("Dec 2026")]
    public void AMonthPrecisionExpiry_ParsesButIsRefusedByTheCap(string raw)
    {
        ConfigurationGovernanceBreakGlass.TryParseUtc(raw, out var parsed)
            .Should().BeTrue("the parse is deliberately permissive");
        parsed.Should().BeAfter(Now.AddHours(24));

        Build(new FakeTimeProvider(Now),
                (ConfigurationGovernanceBreakGlass.EnabledKey, "true"),
                (ConfigurationGovernanceBreakGlass.ExpiresAtUtcKey, raw))
            .Current().IsEngaged.Should().BeFalse(
                "a month-long break-glass is the permanent configuration with extra steps");
    }

    // ── INFO-8: a malformed Enabled flag fails CLOSED, at construction ───────

    /// <summary>
    /// <c>IConfiguration.GetValue&lt;bool&gt;</c> THROWS on a non-boolean string.
    /// Since this type is built inside a DI factory, that surfaced as a
    /// service-resolution failure on the first gate call — not a startup refusal,
    /// not an ERROR log, just a 500 from somewhere unrelated. A governance switch
    /// must fail CLOSED and say so.
    /// </summary>
    [TestCase("yes")]
    [TestCase("1")]
    [TestCase("on")]
    [TestCase("TRUE-ish")]
    public void AnUnparseableEnabledValue_FailsClosed_WithoutThrowing(string raw)
    {
        var act = () => Build(new FakeTimeProvider(Now),
            (ConfigurationGovernanceBreakGlass.EnabledKey, raw),
            (ConfigurationGovernanceBreakGlass.ExpiresAtUtcKey, "2026-07-30T18:00:00Z"));

        act.Should().NotThrow("a malformed governance flag must not take the DI graph down");
        act().Current().IsEngaged.Should().BeFalse(
            "an unreadable Enabled flag is not a true one — the fail-closed posture stays");
    }

    /// <summary>The genuine boolean spellings still work.</summary>
    [TestCase("true")]
    [TestCase("True")]
    public void AParseableEnabledValue_StillEngages(string raw)
    {
        Build(new FakeTimeProvider(Now),
                (ConfigurationGovernanceBreakGlass.EnabledKey, raw),
                (ConfigurationGovernanceBreakGlass.ExpiresAtUtcKey, "2026-07-30T18:00:00Z"))
            .Current().IsEngaged.Should().BeTrue();
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
