using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.ADL;

namespace Tamma.Activities.Tests.ADL;

/// <summary>
/// Regression guard for the silent-intake bug: <c>GET /api/engine/issues</c> returns
/// <c>{ issues, total }</c> (<c>EngineEndpoints.GetIssues</c> —
/// <c>Results.Ok(new { issues = r.Issues, total = r.Total })</c>), but all three
/// engine-side call sites deserialized the body straight into
/// <c>List&lt;WorkItem&gt;</c>. That throws <see cref="JsonException"/> against an
/// object, and <c>SelectWorkItemActivity.FetchCandidates</c> wraps the whole fetch in
/// a broad <c>catch (Exception)</c> that only logs — so the real (non-mock) intake
/// path returned zero candidates and every run reported <c>NothingFound</c>. The
/// failure was invisible: HTTP 200, no exception surfaced, one log line.
///
/// <para>These tests pin the envelope shape rather than the activities, because the
/// defect was entirely in the wire contract between the endpoint and its callers, and
/// a shape test needs no HTTP harness to be a real guard.</para>
/// </summary>
[TestFixture]
public class EngineIssuesResponseTests
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The exact body shape the endpoint produces.</summary>
    private const string EndpointBody = """
        {
          "issues": [
            { "number": 42, "title": "Fix the thing", "labels": ["tamma-auto", "bug"] },
            { "number": 43, "title": "Another thing", "labels": ["tamma-auto"] }
          ],
          "total": 2
        }
        """;

    [Test]
    public void Deserializes_TheEnvelopeTheEndpointActuallyReturns()
    {
        var parsed = JsonSerializer.Deserialize<EngineIssuesResponse>(EndpointBody, Options);

        parsed.Should().NotBeNull();
        parsed!.Total.Should().Be(2);
        parsed.Issues.Should().HaveCount(2);
        parsed.Issues[0].Number.Should().Be(42);
        parsed.Issues[0].Labels.Should().Contain("tamma-auto");
    }

    [Test]
    public void TheOldShape_ThrowsAgainstTheRealBody_WhichIsWhyTheBugWasSilent()
    {
        // This is the pre-fix call. It must throw — if this ever stops throwing, the
        // endpoint has changed shape and the envelope type above is now wrong.
        var act = () => JsonSerializer.Deserialize<List<WorkItem>>(EndpointBody, Options);

        act.Should().Throw<JsonException>(
            "the endpoint returns an object, so deserializing it as an array cannot work — "
            + "the pre-fix code swallowed exactly this exception and reported NothingFound");
    }

    [Test]
    public void MissingIssuesKey_YieldsEmpty_NotNull()
    {
        // Defensive: a body with only `total` must not NRE a caller that iterates.
        var parsed = JsonSerializer.Deserialize<EngineIssuesResponse>("""{ "total": 0 }""", Options);

        parsed.Should().NotBeNull();
        parsed!.Issues.Should().NotBeNull().And.BeEmpty();
    }
}
