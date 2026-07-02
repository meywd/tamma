using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Audit;

namespace Tamma.Api.Tests.Audit;

/// <summary>
/// Story 37-3 (T1) — pure parse/validate/cursor tests for
/// <see cref="AuditQueryFilter"/>. No DB. Parsing must be total: every bad input
/// yields a descriptive error string, never an exception (AC3).
/// </summary>
[TestFixture]
public class AuditQueryFilterTests
{
    private static (AuditQueryFilter? Filter, string? Error) Parse(
        string? category = null, string? action = null, string? actorUserId = null,
        string? targetType = null, string? targetId = null, string? severity = null,
        string? outcome = null, string? ipAddress = null, DateTime? from = null,
        DateTime? to = null, string? q = null, int? limit = null, string? cursor = null)
        => AuditQueryFilter.TryParse(
            category, action, actorUserId, targetType, targetId, severity,
            outcome, ipAddress, from, to, q, limit, cursor);

    [Test]
    public void Empty_Query_Parses_With_Defaults()
    {
        var (f, err) = Parse();
        err.Should().BeNull();
        f.Should().NotBeNull();
        f!.Limit.Should().Be(AuditQueryFilter.DefaultLimit);
        f.Category.Should().BeNull();
        f.Cursor.Should().BeNull();
    }

    [Test]
    public void Valid_Combination_Parses()
    {
        var actor = Guid.NewGuid();
        var (f, err) = Parse(
            category: "Secret", action: "SECRET.REVEAL", actorUserId: actor.ToString(),
            targetType: "secret", targetId: "abc", severity: "Critical", outcome: "Success",
            ipAddress: "10.0.0.1", from: new DateTime(2026, 1, 1), to: new DateTime(2026, 2, 1),
            q: "  hello  ", limit: 25);

        err.Should().BeNull();
        f.Should().NotBeNull();
        f!.Category.Should().Be("secret", "enum values normalise to lowercase");
        f.Severity.Should().Be("critical");
        f.Outcome.Should().Be("success");
        f.Action.Should().Be("SECRET.REVEAL", "action_code is a free-form exact match, not lowercased");
        f.ActorUserId.Should().Be(actor);
        f.Search.Should().Be("hello", "q is trimmed");
        f.From!.Value.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Test]
    public void Invalid_Category_Yields_Error()
    {
        var (f, err) = Parse(category: "not-a-category");
        f.Should().BeNull();
        err.Should().Contain("category");
    }

    [Test]
    public void Invalid_Severity_Yields_Error()
    {
        var (f, err) = Parse(severity: "extreme");
        f.Should().BeNull();
        err.Should().Contain("severity");
    }

    [Test]
    public void Invalid_Outcome_Yields_Error()
    {
        var (f, err) = Parse(outcome: "maybe");
        f.Should().BeNull();
        err.Should().Contain("outcome");
    }

    [Test]
    public void Notice_Severity_Is_Valid()
    {
        // The real AuditSeverity enum includes Notice (the spec omitted it).
        var (f, err) = Parse(severity: "notice");
        err.Should().BeNull();
        f!.Severity.Should().Be("notice");
    }

    [Test]
    public void Invalid_ActorUserId_Yields_Error()
    {
        var (f, err) = Parse(actorUserId: "not-a-guid");
        f.Should().BeNull();
        err.Should().Contain("actorUserId");
    }

    [Test]
    public void From_After_To_Yields_Error()
    {
        var (f, err) = Parse(from: new DateTime(2026, 3, 1), to: new DateTime(2026, 1, 1));
        f.Should().BeNull();
        err.Should().Contain("from");
    }

    [Test]
    public void From_Equal_To_Is_Allowed()
    {
        var t = new DateTime(2026, 1, 1);
        var (f, err) = Parse(from: t, to: t);
        err.Should().BeNull();
        f.Should().NotBeNull();
    }

    [Test]
    public void Limit_Is_Clamped_Not_Rejected()
    {
        Parse(limit: 0).Filter!.Limit.Should().Be(AuditQueryFilter.MinLimit);
        Parse(limit: 999).Filter!.Limit.Should().Be(AuditQueryFilter.MaxLimit);
        Parse(limit: -5).Filter!.Limit.Should().Be(AuditQueryFilter.MinLimit);
        Parse(limit: null).Filter!.Limit.Should().Be(AuditQueryFilter.DefaultLimit);
        Parse(limit: 50).Filter!.Limit.Should().Be(50);
    }

    [Test]
    public void Whitespace_Search_Normalises_To_Null()
    {
        Parse(q: "   ").Filter!.Search.Should().BeNull();
        Parse(q: "").Filter!.Search.Should().BeNull();
    }

    [Test]
    public void Cursor_RoundTrips_Exactly()
    {
        foreach (var seq in new[] { 0L, 1L, 42L, long.MaxValue, 9_876_543_210L })
        {
            var encoded = AuditQueryFilter.EncodeCursor(seq);
            encoded.Should().NotContain("+").And.NotContain("/").And.NotContain("=",
                "cursor is base64URL (opaque, URL-safe)");
            AuditQueryFilter.TryDecodeCursor(encoded, out var decoded).Should().BeTrue();
            decoded.Should().Be(seq);

            // And through the full parse path.
            var (f, err) = Parse(cursor: encoded);
            err.Should().BeNull();
            f!.Cursor.Should().Be(seq);
        }
    }

    [Test]
    public void Garbage_Cursor_Yields_Error()
    {
        var (f, err) = Parse(cursor: "!!!not-base64!!!");
        f.Should().BeNull();
        err.Should().Contain("cursor");
    }

    [Test]
    public void ToAuditableShape_Contains_Applied_Filters_Only()
    {
        var (f, _) = Parse(category: "secret", severity: "critical", limit: 10);
        var shape = f!.ToAuditableShape();
        shape.Should().ContainKey("category");
        shape.Should().ContainKey("severity");
        shape.Should().ContainKey("limit");
        shape.Should().NotContainKey("action");
        shape.Should().NotContainKey("actorUserId");
    }

    [Test]
    public void AppliedFilterKeys_Never_Leaks_Values()
    {
        var (f, _) = Parse(q: "sensitive@example.com", ipAddress: "10.0.0.1");
        var keys = f!.AppliedFilterKeys();
        keys.Should().Contain("q").And.Contain("ipAddress");
        keys.Should().NotContain("sensitive@example.com");
        keys.Should().NotContain("10.0.0.1");
    }
}
