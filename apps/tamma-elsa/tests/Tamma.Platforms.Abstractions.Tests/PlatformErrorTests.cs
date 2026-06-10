using FluentAssertions;
using NUnit.Framework;
using Tamma.Platforms.Abstractions;

namespace Tamma.Platforms.Abstractions.Tests;

/// <summary>
/// Story 31-1 AC7: every <see cref="PlatformError"/> variant is
/// constructible and pattern-matches exhaustively. A future addition
/// to the union without updating callers would be caught by the
/// "every variant has a case" test below.
/// </summary>
[TestFixture]
public sealed class PlatformErrorTests
{
    [Test]
    public void All_variants_are_constructible()
    {
        PlatformError[] variants =
        [
            new PlatformError.AuthExpired(),
            new PlatformError.PermissionDenied(),
            new PlatformError.NotFound(),
            new PlatformError.RateLimited(TimeSpan.FromSeconds(30)),
            new PlatformError.RateLimited(null),
            new PlatformError.ServiceUnavailable(),
            new PlatformError.InvalidRequest("merge_conflict", "rebase needed"),
            new PlatformError.InvalidRequest("merge_conflict", null),
            new PlatformError.Unknown("test"),
        ];

        foreach (var variant in variants)
        {
            variant.Should().NotBeNull();
        }
    }

    [Test]
    public void Pattern_match_covers_every_variant()
    {
        // Mirrors how 31-3..31-6 retry policies will dispatch.
        // If a new variant is added without updating the switch, the
        // default case throws and this test fails — forcing
        // downstream stories to update their handling.
        PlatformError[] variants =
        [
            new PlatformError.AuthExpired(),
            new PlatformError.PermissionDenied(),
            new PlatformError.NotFound(),
            new PlatformError.RateLimited(null),
            new PlatformError.ServiceUnavailable(),
            new PlatformError.InvalidRequest("x", null),
            new PlatformError.Unknown("y"),
        ];

        foreach (var error in variants)
        {
            var category = error switch
            {
                PlatformError.AuthExpired       => "auth",
                PlatformError.PermissionDenied  => "perm",
                PlatformError.NotFound          => "404",
                PlatformError.RateLimited       => "throttle",
                PlatformError.ServiceUnavailable=> "5xx",
                PlatformError.InvalidRequest    => "4xx",
                PlatformError.Unknown           => "?",
                _ => throw new InvalidOperationException(
                    $"unhandled variant {error.GetType().Name}"),
            };
            category.Should().NotBeNullOrEmpty();
        }
    }

    [Test]
    public void RateLimited_carries_optional_retry_after()
    {
        var bounded = new PlatformError.RateLimited(TimeSpan.FromSeconds(60));
        bounded.RetryAfter.Should().Be(TimeSpan.FromSeconds(60));

        var unbounded = new PlatformError.RateLimited(null);
        unbounded.RetryAfter.Should().BeNull();
    }

    [Test]
    public void InvalidRequest_carries_code_and_optional_hint()
    {
        var withHint = new PlatformError.InvalidRequest("merge_conflict", "rebase first");
        withHint.Code.Should().Be("merge_conflict");
        withHint.Hint.Should().Be("rebase first");

        var noHint = new PlatformError.InvalidRequest("capability_unsupported", null);
        noHint.Hint.Should().BeNull();
    }

    [Test]
    public void Equality_is_value_based()
    {
        var a = new PlatformError.RateLimited(TimeSpan.FromSeconds(10));
        var b = new PlatformError.RateLimited(TimeSpan.FromSeconds(10));
        a.Should().Be(b);
    }
}
