using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Research;

namespace Tamma.Activities.Tests.Research;

/// <summary>
/// Story 3.4 — unit coverage for <see cref="ResearchParsing.ParseReport"/>. Proves the
/// parser recovers the structured report on a well-formed synthesis response, RANKS
/// findings by relevance then confidence, and FAILS CLOSED (returns null) on every
/// degraded/empty/malformed input so the workflow routes to RESEARCH.FAILED rather than
/// fabricating a report.
/// </summary>
[TestFixture]
public class ResearchParsingTests
{
    private const string ValidReport =
        """
        Here is the synthesized research:
        {
          "topic": "caching layer",
          "summary": "Redis is the incumbent cache; no per-tenant isolation exists yet.",
          "findings": [
            { "title": "Low relevance", "summary": "Minor note", "relevance": 0.2, "confidence": 0.9, "citations": ["a.cs"] },
            { "title": "High relevance", "summary": "Core finding", "relevance": 0.95, "confidence": 0.6, "citations": ["b.cs", "https://x"] },
            { "title": "Mid relevance", "summary": "Secondary", "relevance": 0.5, "confidence": 0.8 }
          ],
          "overallConfidence": 0.77
        }
        """;

    [Test]
    public void ParseReport_ValidResponse_RecoversReport()
    {
        var report = ResearchParsing.ParseReport(ValidReport);

        report.Should().NotBeNull();
        report!.Topic.Should().Be("caching layer");
        report.Summary.Should().Contain("Redis");
        report.Findings.Should().HaveCount(3);
        report.OverallConfidence.Should().Be(0.77m);
        // After ranking, "High relevance" (the finding carrying the citations) is first.
        report.Findings[0].Title.Should().Be("High relevance");
        report.Findings[0].Citations.Should().Contain("https://x");
    }

    [Test]
    public void ParseReport_RanksFindings_ByRelevanceThenConfidence()
    {
        var report = ResearchParsing.ParseReport(ValidReport);

        report!.Findings.Select(f => f.Title).ToList()
            .Should().Equal(
                new[] { "High relevance", "Mid relevance", "Low relevance" },
                "findings must be ranked by relevance descending (AC: ranked by relevance and confidence)");
    }

    [Test]
    public void ParseReport_MissingOverallConfidence_ComputesMean()
    {
        const string noOverall =
            """
            { "summary": "s", "findings": [
              { "summary": "a", "relevance": 0.5, "confidence": 0.4 },
              { "summary": "b", "relevance": 0.5, "confidence": 0.6 }
            ] }
            """;

        var report = ResearchParsing.ParseReport(noOverall);

        report.Should().NotBeNull();
        report!.OverallConfidence.Should().Be(0.5m, "mean of 0.4 and 0.6 when overallConfidence is omitted");
    }

    [Test]
    public void ParseReport_UsesFallbackTopic_WhenResponseOmitsTopic()
    {
        const string noTopic = """{ "summary": "s", "findings": [ { "summary": "a" } ] }""";

        var report = ResearchParsing.ParseReport(noTopic, topic: "fallback-topic");

        report!.Topic.Should().Be("fallback-topic");
    }

    [Test]
    public void ParseReport_DropsEmptyShellFindings()
    {
        const string withShell =
            """
            { "summary": "s", "findings": [
              { "title": "", "summary": "" },
              { "summary": "real finding" }
            ] }
            """;

        var report = ResearchParsing.ParseReport(withShell);

        report!.Findings.Should().ContainSingle(f => f.Summary == "real finding",
            "empty-shell findings (no title and no summary) must be dropped, not admitted blank");
    }

    // ── Fail-closed cases (all → null) ─────────────────────────────────
    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("no json here at all")]
    [TestCase("{ not valid json")]
    public void ParseReport_DegradedInput_FailsClosed(string? input)
    {
        ResearchParsing.ParseReport(input).Should().BeNull(
            "degraded/empty/malformed synthesis output must fail closed (no fabricated report)");
    }

    [Test]
    public void ParseReport_MissingSummary_FailsClosed()
    {
        const string noSummary = """{ "findings": [ { "summary": "a" } ] }""";
        ResearchParsing.ParseReport(noSummary).Should().BeNull(
            "the overview summary is load-bearing — a report without it must fail closed");
    }

    [Test]
    public void ParseReport_NoFindings_FailsClosed()
    {
        const string emptyFindings = """{ "summary": "s", "findings": [] }""";
        ResearchParsing.ParseReport(emptyFindings).Should().BeNull(
            "a report with no findings researched nothing — it must fail closed");
    }

    [Test]
    public void ParseReport_AllShellFindings_FailsClosed()
    {
        const string allShells = """{ "summary": "s", "findings": [ { "title": "", "summary": "" } ] }""";
        ResearchParsing.ParseReport(allShells).Should().BeNull(
            "when every finding is an empty shell the report has no usable content — fail closed");
    }
}
