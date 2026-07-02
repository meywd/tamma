using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Analytics;

namespace Tamma.Activities.Tests.Analytics;

/// <summary>
/// Story 28-10 — assert the rollup-event builder produces the shape the
/// dashboards expect and the hour-truncation helper is robust.
/// </summary>
[TestFixture]
public class AnalyticsRollupEventsTests
{
    private static readonly Guid Tenant = new("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
    private static readonly DateTime Hour =
        new(2026, 04, 18, 12, 00, 00, DateTimeKind.Utc);

    [Test]
    public void BuildEvent_TenantScoped_StampsTenantIdAndTags()
    {
        var evt = AnalyticsRollupEvents.BuildEvent(
            AnalyticsRollupEvents.TenantRollupCompleted,
            Hour,
            Tenant,
            data: new Dictionary<string, object?>
            {
                ["workflowsStarted"] = 5,
            });

        evt.Type.Should().Be(AnalyticsRollupEvents.TenantRollupCompleted);
        evt.TenantId.Should().Be(Tenant);

        using var tags = JsonDocument.Parse(evt.Tags);
        tags.RootElement.GetProperty("tenantId").GetString().Should().Be(Tenant.ToString("D"));
        tags.RootElement.GetProperty("hour").GetString().Should().NotBeNullOrEmpty();

        using var data = JsonDocument.Parse(evt.Data);
        data.RootElement.GetProperty("workflowsStarted").GetInt32().Should().Be(5);
    }

    [Test]
    public void BuildEvent_PlatformWide_OmitsTenantIdTag()
    {
        var evt = AnalyticsRollupEvents.BuildEvent(
            AnalyticsRollupEvents.HourCompleted,
            Hour);

        evt.Type.Should().Be(AnalyticsRollupEvents.HourCompleted);
        evt.TenantId.Should().BeNull();

        using var tags = JsonDocument.Parse(evt.Tags);
        tags.RootElement.TryGetProperty("tenantId", out _).Should().BeFalse();
        tags.RootElement.GetProperty("hour").GetString().Should().NotBeNullOrEmpty();
    }

    [Test]
    public void BuildEvent_Metadata_MarksSystemSource()
    {
        var evt = AnalyticsRollupEvents.BuildEvent(
            AnalyticsRollupEvents.PlatformRollupCompleted,
            Hour);

        using var meta = JsonDocument.Parse(evt.Metadata);
        meta.RootElement.GetProperty("eventSource").GetString().Should().Be("system");
    }

    [Test]
    public void BuildEvent_ThrowsOnBlankType()
    {
        Action act = () => AnalyticsRollupEvents.BuildEvent("  ", Hour);
        act.Should().Throw<ArgumentException>().WithParameterName("type");
    }

    [Test]
    public void DimensionalEventConstants_FollowAggregateActionStatus()
    {
        // Story 36-2 — the new dimensional/compact/purge/lag constants follow
        // the AGGREGATE.ACTION.STATUS convention (dotted, uppercase segments).
        var constants = new[]
        {
            AnalyticsRollupEvents.TenantDimensionalRollupCompleted,
            AnalyticsRollupEvents.TenantDimensionalRollupFailed,
            AnalyticsRollupEvents.DailyCompacted,
            AnalyticsRollupEvents.UsageHourlyPurged,
            AnalyticsRollupEvents.UsageHourlyPurgeFailed,
            AnalyticsRollupEvents.DimensionalLag,
        };

        foreach (var c in constants)
        {
            c.Should().StartWith("ANALYTICS.");
            c.Split('.').Length.Should().BeGreaterThanOrEqualTo(3, $"{c} must be AGGREGATE.ACTION.STATUS");
            c.Should().Be(c.ToUpperInvariant());
        }

        AnalyticsRollupEvents.TenantDimensionalRollupCompleted
            .Should().Be("ANALYTICS.ROLLUP.TENANT_DIMENSIONAL_COMPLETED");
        AnalyticsRollupEvents.DimensionalLag.Should().Be("ANALYTICS.ROLLUP.DIMENSIONAL_LAG");
    }

    [Test]
    public void TruncateToHour_StripsMinutesSecondsMilliseconds()
    {
        var instant = new DateTime(2026, 04, 18, 12, 34, 56, 789, DateTimeKind.Utc);
        var truncated = AnalyticsRollupEvents.TruncateToHour(instant);

        truncated.Should().Be(new DateTime(2026, 04, 18, 12, 0, 0, DateTimeKind.Utc));
        truncated.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Test]
    public void TruncateToHour_AlreadyUtc_PreservesKind()
    {
        // A DateTime already tagged Kind=Utc should come back as Utc
        // with the minute / second stripped. No conversion happens.
        var instant = new DateTime(2026, 04, 18, 12, 34, 56, DateTimeKind.Utc);
        var truncated = AnalyticsRollupEvents.TruncateToHour(instant);

        truncated.Should().Be(new DateTime(2026, 04, 18, 12, 0, 0, DateTimeKind.Utc));
        truncated.Kind.Should().Be(DateTimeKind.Utc);
    }
}
