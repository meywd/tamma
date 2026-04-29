using FluentAssertions;
using NUnit.Framework;
using Tamma.Platforms.Abstractions;

namespace Tamma.Platforms.Abstractions.Tests;

[TestFixture]
public sealed class PlatformResultTests
{
    [Test]
    public void Ok_carries_value()
    {
        var result = PlatformResult<int>.FromOk(42);
        result.IsOk.Should().BeTrue();
        result.GetValueOrDefault().Should().Be(42);

        if (result is not PlatformResult<int>.Ok ok)
        {
            Assert.Fail("expected Ok variant");
            return;
        }
        ok.Value.Should().Be(42);
    }

    [Test]
    public void Failed_carries_error()
    {
        var error = new PlatformError.NotFound();
        var result = PlatformResult<string>.FromError(error);
        result.IsOk.Should().BeFalse();
        result.GetValueOrDefault().Should().BeNull();

        if (result is not PlatformResult<string>.Failed failed)
        {
            Assert.Fail("expected Failed variant");
            return;
        }
        failed.Error.Should().BeSameAs(error);
    }

    [Test]
    public void ServiceUnavailable_has_no_payload()
    {
        var result = PlatformResult<bool>.FromServiceUnavailable();
        result.IsOk.Should().BeFalse();
        result.GetValueOrDefault().Should().BeFalse();
        result.Should().BeOfType<PlatformResult<bool>.ServiceUnavailable>();
    }

    [Test]
    public void Pattern_match_covers_every_variant()
    {
        PlatformResult<string>[] cases =
        [
            PlatformResult<string>.FromOk("hi"),
            PlatformResult<string>.FromError(new PlatformError.AuthExpired()),
            PlatformResult<string>.FromServiceUnavailable(),
        ];

        foreach (var c in cases)
        {
            var label = c switch
            {
                PlatformResult<string>.Ok                  => "ok",
                PlatformResult<string>.Failed              => "failed",
                PlatformResult<string>.ServiceUnavailable  => "unavailable",
                _ => throw new InvalidOperationException("unhandled variant"),
            };
            label.Should().NotBeNullOrEmpty();
        }
    }

    [Test]
    public void Map_projects_value_for_Ok()
    {
        var result = PlatformResult<int>.FromOk(7).Map(v => v * 2);
        result.GetValueOrDefault().Should().Be(14);
    }

    [Test]
    public void Map_preserves_Failed_variant()
    {
        var error = new PlatformError.NotFound();
        var result = PlatformResult<int>.FromError(error).Map(v => v.ToString());

        result.Should().BeOfType<PlatformResult<string>.Failed>();
        ((PlatformResult<string>.Failed)result).Error.Should().BeSameAs(error);
    }

    [Test]
    public void Map_preserves_ServiceUnavailable_variant()
    {
        var result = PlatformResult<int>.FromServiceUnavailable().Map(v => v.ToString());
        result.Should().BeOfType<PlatformResult<string>.ServiceUnavailable>();
    }

    [Test]
    public void Map_throws_on_null_selector()
    {
        var ok = PlatformResult<int>.FromOk(1);
        Action act = () => ok.Map<string>(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
