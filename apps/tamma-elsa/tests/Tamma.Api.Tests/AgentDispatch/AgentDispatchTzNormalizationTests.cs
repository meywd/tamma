using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Endpoints;
using Tamma.Api.Services.GitHub;

namespace Tamma.Api.Tests.AgentDispatch;

/// <summary>
/// Review-session 2026-06-30 finding 1 (TZ bug) — the GitHub Actions discovery
/// window (<c>created:&gt;=</c>) must denote the CORRECT UTC instant regardless of
/// host TZ. On a non-UTC host (e.g. Europe/Berlin) the old code formatted a
/// <see cref="System.DateTimeKind.Local"/> value with a literal <c>Z</c>, shifting
/// the window +2h into the FUTURE and EXCLUDING the just-dispatched run — which the
/// monitor then routed as Failed for a run that actually succeeded. The bug is
/// invisible on a UTC CI/VPS host (Local == UTC), so these tests assert the
/// normalization functions directly in a TZ-independent, deterministic way.
/// </summary>
[TestFixture]
public class AgentDispatchTzNormalizationTests
{
    [Test]
    public void FormatCreatedFilter_UtcValue_ProducesCorrectZFilter()
    {
        var utc = new DateTime(2026, 6, 30, 12, 0, 0, DateTimeKind.Utc);
        OctokitGitHubActionsClient.FormatCreatedFilter(utc).Should().Be(">=2026-06-30T12:00:00Z");
    }

    [Test]
    public void FormatCreatedFilter_LocalKindValue_NormalizesToUtc_TzIndependent()
    {
        // A Kind=Local value that represents 12:00Z on ANY host (round-tripped through
        // the machine TZ). The buggy literal-Z format would stamp the local wall-clock
        // (e.g. 14:00Z on Berlin); ToUniversalTime() recovers the true 12:00Z instant.
        var localNoonUtc = new DateTime(2026, 6, 30, 12, 0, 0, DateTimeKind.Utc).ToLocalTime();
        localNoonUtc.Kind.Should().Be(DateTimeKind.Local);

        OctokitGitHubActionsClient.FormatCreatedFilter(localNoonUtc).Should().Be(">=2026-06-30T12:00:00Z");
    }

    [Test]
    public void ParseCreatedAfterUtc_ExplicitOffset_ConvertsToUtc_TzIndependent()
    {
        // Deterministic AND TZ-independent: a +02:00 offset string is 12:00Z on EVERY
        // host. The old endpoint bound DateTime directly (Kind=Local), which the
        // downstream literal-Z formatter then rendered as 14:00Z — wrong.
        var parsed = AgentDispatchEndpoints.ParseCreatedAfterUtc("2026-06-30T14:00:00+02:00");

        parsed.Kind.Should().Be(DateTimeKind.Utc);
        parsed.Should().Be(new DateTime(2026, 6, 30, 12, 0, 0, DateTimeKind.Utc));
        OctokitGitHubActionsClient.FormatCreatedFilter(parsed).Should().Be(">=2026-06-30T12:00:00Z");
    }

    [Test]
    public void ParseCreatedAfterUtc_ZuluString_StaysUtc()
    {
        var parsed = AgentDispatchEndpoints.ParseCreatedAfterUtc("2026-06-30T12:00:00Z");

        parsed.Kind.Should().Be(DateTimeKind.Utc);
        parsed.Should().Be(new DateTime(2026, 6, 30, 12, 0, 0, DateTimeKind.Utc));
    }

    [Test]
    public void ParseCreatedAfterUtc_RoundTripsWithFormatter_ForEngineWireValue()
    {
        // End-to-end: the engine sends DateTime.UtcNow.ToString("o") (…Z); parsing it and
        // re-formatting must yield the same wall-clock second, never a shifted window.
        var instant = new DateTime(2026, 6, 30, 9, 30, 15, DateTimeKind.Utc);
        var wire = instant.ToString("o", System.Globalization.CultureInfo.InvariantCulture);

        var parsed = AgentDispatchEndpoints.ParseCreatedAfterUtc(wire);

        OctokitGitHubActionsClient.FormatCreatedFilter(parsed).Should().Be(">=2026-06-30T09:30:15Z");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("not-a-date")]
    public void ParseCreatedAfterUtc_MissingOrUnparseable_FallsBackToEpoch_NeverFuture(string? raw)
    {
        // A bad value must NOT over-filter into the future (which would drop the run);
        // epoch filters nothing so discovery still finds the most-recent run on the branch.
        AgentDispatchEndpoints.ParseCreatedAfterUtc(raw).Should().Be(DateTime.UnixEpoch);
    }
}
