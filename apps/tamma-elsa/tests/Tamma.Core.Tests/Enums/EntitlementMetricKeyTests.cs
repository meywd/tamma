using FluentAssertions;
using NUnit.Framework;
using Tamma.Core;
using Tamma.Core.Enums;

namespace Tamma.Core.Tests.Enums;

/// <summary>
/// Story 34-1 (AC5) — pins the snake_case contract for
/// <see cref="EntitlementMetricKey"/>. The persisted string is the stable
/// wire/DB form shared by entitlement, pricing, metering, and enforcement —
/// these tests guard against ordinal drift and against adding an enum member
/// without a matching mapping.
/// </summary>
[TestFixture]
public class EntitlementMetricKeyTests
{
    [TestCase(EntitlementMetricKey.Agents, "agents")]
    [TestCase(EntitlementMetricKey.WorkflowRuns, "workflow_runs")]
    [TestCase(EntitlementMetricKey.LlmTokens, "llm_tokens")]
    [TestCase(EntitlementMetricKey.Seats, "seats")]
    [TestCase(EntitlementMetricKey.Repos, "repos")]
    [TestCase(EntitlementMetricKey.RagStorageMb, "rag_storage_mb")]
    [TestCase(EntitlementMetricKey.BenchmarkRetentionDays, "benchmark_retention_days")]
    public void ToMetricString_Pins_SnakeCase(EntitlementMetricKey key, string expected)
    {
        key.ToMetricString().Should().Be(expected);
    }

    [Test]
    public void EveryMember_RoundTrips_Through_String()
    {
        foreach (EntitlementMetricKey key in Enum.GetValues<EntitlementMetricKey>())
        {
            var s = key.ToMetricString();
            EntitlementMetricKeyExtensions.Parse(s).Should().Be(key,
                "every member must round-trip through ToMetricString/Parse");
        }
    }

    [Test]
    public void Parse_Unknown_Throws_TammaError()
    {
        var act = () => EntitlementMetricKeyExtensions.Parse("not_a_metric");
        act.Should().Throw<TammaError>()
            .Which.Code.Should().Be("PLAN.METRIC_KEY.UNKNOWN");
    }

    [Test]
    public void Parse_Null_Throws()
    {
        var act = () => EntitlementMetricKeyExtensions.Parse(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void SnakeCaseMap_Has_Exactly_OneEntry_Per_Member_NoDuplicates()
    {
        // Guards against adding an enum member without a mapping (or two
        // members mapping to the same string).
        var memberCount = Enum.GetValues<EntitlementMetricKey>().Length;
        var strings = EntitlementMetricKeyExtensions.AllMetricStrings;

        memberCount.Should().Be(7, "the closed enum has exactly 7 members");
        strings.Should().HaveCount(memberCount,
            "every member maps to exactly one distinct snake_case string");
        strings.Distinct().Should().HaveCount(memberCount, "no duplicate mappings");
    }
}
