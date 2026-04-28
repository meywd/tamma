using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Analytics;

namespace Tamma.Activities.Tests.Analytics;

/// <summary>
/// Story 28-10 — unit tests for the LLM-usage aggregator inside
/// <see cref="ComputeTenantRollupActivity"/>. Exercised via the
/// internal static helper so the test doesn't need real EF plumbing.
/// </summary>
[TestFixture]
public class ComputeTenantRollupAggregationTests
{
    [Test]
    public void AggregateLlmUsage_SumsCostAndTokens()
    {
        var blobs = new[]
        {
            "{\"costUsd\":0.01,\"inputTokens\":100,\"outputTokens\":50}",
            "{\"costUsd\":0.025,\"inputTokens\":200,\"outputTokens\":75}",
            "{\"costUsd\":0.12,\"inputTokens\":500,\"outputTokens\":150}",
        };

        var (cost, tokensIn, tokensOut) = ComputeTenantRollupActivity.AggregateLlmUsage(blobs);

        cost.Should().Be(0.1550m);
        tokensIn.Should().Be(800L);
        tokensOut.Should().Be(275L);
    }

    [Test]
    public void AggregateLlmUsage_AcceptsStringEncodedNumbers()
    {
        var blobs = new[]
        {
            "{\"costUsd\":\"0.5\",\"inputTokens\":\"100\",\"outputTokens\":\"200\"}",
        };

        var (cost, tokensIn, tokensOut) = ComputeTenantRollupActivity.AggregateLlmUsage(blobs);

        cost.Should().Be(0.5m);
        tokensIn.Should().Be(100L);
        tokensOut.Should().Be(200L);
    }

    [Test]
    public void AggregateLlmUsage_SkipsMalformedRows()
    {
        var blobs = new[]
        {
            "not json",
            "{\"costUsd\":0.5,\"inputTokens\":100}", // Missing outputTokens — tolerated.
            "{}", // Empty — zero contribution.
            null,
            "   ",
            "{\"costUsd\":1.0,\"inputTokens\":50,\"outputTokens\":25}",
        };

        var (cost, tokensIn, tokensOut) = ComputeTenantRollupActivity.AggregateLlmUsage(blobs);

        cost.Should().Be(1.5m);
        tokensIn.Should().Be(150L);
        tokensOut.Should().Be(25L);
    }

    [Test]
    public void AggregateLlmUsage_RoundsCostTo4Decimals()
    {
        // 0.00005 * 4 = 0.00020 rounded = 0.0002
        var blobs = new[]
        {
            "{\"costUsd\":0.00005}",
            "{\"costUsd\":0.00005}",
            "{\"costUsd\":0.00005}",
            "{\"costUsd\":0.00005}",
        };

        var (cost, _, _) = ComputeTenantRollupActivity.AggregateLlmUsage(blobs);

        cost.Should().Be(0.0002m);
    }

    [Test]
    public void AggregateLlmUsage_EmptyBatchProducesZeros()
    {
        var (cost, tokensIn, tokensOut) =
            ComputeTenantRollupActivity.AggregateLlmUsage(Array.Empty<string?>());

        cost.Should().Be(0m);
        tokensIn.Should().Be(0L);
        tokensOut.Should().Be(0L);
    }

    [Test]
    public void AggregateLlmUsage_IgnoresNonObjectRoots()
    {
        var blobs = new[]
        {
            "[1,2,3]",
            "\"just a string\"",
            "42",
            "{\"costUsd\":0.5}",
        };

        var (cost, _, _) = ComputeTenantRollupActivity.AggregateLlmUsage(blobs);

        cost.Should().Be(0.5m);
    }
}
