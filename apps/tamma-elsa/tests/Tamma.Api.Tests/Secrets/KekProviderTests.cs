using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.Secrets;

namespace Tamma.Api.Tests.Secrets;

/// <summary>
/// Story 28-12 — unit suite for <see cref="KekProvider"/>. Confirms the
/// configuration shape (<c>Cranl:EncryptionKey</c> +
/// <c>Tamma:Kek:Secondary</c> + <c>Tamma:Kek:ActiveVersion</c>),
/// validates the 32-byte invariant, and verifies the
/// stage-then-promote rotation transition.
/// </summary>
[TestFixture]
public class KekProviderTests
{
    private static readonly byte[] PrimaryKey =
    {
        1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
        17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31, 32,
    };

    private static readonly byte[] SecondaryKey =
    {
        100, 101, 102, 103, 104, 105, 106, 107, 108, 109, 110, 111, 112,
        113, 114, 115, 116, 117, 118, 119, 120, 121, 122, 123, 124, 125,
        126, 127, 128, 129, 130, 131,
    };

    private static IConfiguration BuildConfig(
        string? primary = null, string? secondary = null, int? activeVersion = null)
    {
        var dict = new Dictionary<string, string?>();
        if (primary is not null) dict[KekProvider.PrimaryConfigKey] = primary;
        if (secondary is not null) dict[KekProvider.SecondaryConfigKey] = secondary;
        if (activeVersion is not null)
            dict[KekProvider.ActiveVersionConfigKey] = activeVersion.Value.ToString();
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    [Test]
    public void Loads_Primary_From_Configuration()
    {
        var cfg = BuildConfig(primary: Convert.ToBase64String(PrimaryKey));
        var sut = new KekProvider(cfg, NullLogger<KekProvider>.Instance);

        var primary = sut.GetPrimary();

        primary.Should().BeEquivalentTo(PrimaryKey);
        sut.GetSecondary().Should().BeNull();
        sut.GetActiveVersion().Should().Be(1, "default version is 1 when unset");
    }

    [Test]
    public void Loads_Secondary_From_Configuration()
    {
        var cfg = BuildConfig(
            primary: Convert.ToBase64String(PrimaryKey),
            secondary: Convert.ToBase64String(SecondaryKey),
            activeVersion: 7);
        var sut = new KekProvider(cfg, NullLogger<KekProvider>.Instance);

        sut.GetPrimary().Should().BeEquivalentTo(PrimaryKey);
        sut.GetSecondary().Should().BeEquivalentTo(SecondaryKey);
        sut.GetActiveVersion().Should().Be(7);
    }

    [Test]
    public void Missing_Primary_Is_Allowed_For_Dev()
    {
        var cfg = BuildConfig();
        var sut = new KekProvider(cfg, NullLogger<KekProvider>.Instance);

        sut.GetPrimary().Should().BeNull();
        sut.GetSecondary().Should().BeNull();
        sut.GetActiveVersion().Should().Be(1);
    }

    [Test]
    public void Bad_Base64_Throws()
    {
        var cfg = BuildConfig(primary: "this-is-not-base64!@#$");
        Action act = () => new KekProvider(cfg, NullLogger<KekProvider>.Instance);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not valid base64*");
    }

    [Test]
    public void Wrong_Length_Throws()
    {
        var shortKey = Convert.ToBase64String(new byte[16]);
        var cfg = BuildConfig(primary: shortKey);
        Action act = () => new KekProvider(cfg, NullLogger<KekProvider>.Instance);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*32 bytes*");
    }

    [Test]
    public void GetPrimary_Returns_Defensive_Copy()
    {
        var cfg = BuildConfig(primary: Convert.ToBase64String(PrimaryKey));
        var sut = new KekProvider(cfg, NullLogger<KekProvider>.Instance);

        var copy1 = sut.GetPrimary()!;
        copy1[0] = 0xFF;

        var copy2 = sut.GetPrimary()!;
        copy2[0].Should().Be(PrimaryKey[0],
            "mutating one returned copy must not corrupt the cabinet");
    }

    [Test]
    public void Stage_Secondary_Sets_Slot()
    {
        var cfg = BuildConfig(primary: Convert.ToBase64String(PrimaryKey));
        var sut = new KekProvider(cfg, NullLogger<KekProvider>.Instance);

        sut.GetSecondary().Should().BeNull();

        sut.StageSecondary(SecondaryKey);

        sut.GetSecondary().Should().BeEquivalentTo(SecondaryKey);
        // Primary unchanged.
        sut.GetPrimary().Should().BeEquivalentTo(PrimaryKey);
    }

    [Test]
    public void Stage_Secondary_Wrong_Length_Throws()
    {
        var cfg = BuildConfig(primary: Convert.ToBase64String(PrimaryKey));
        var sut = new KekProvider(cfg, NullLogger<KekProvider>.Instance);

        Action act = () => sut.StageSecondary(new byte[16]);

        act.Should().Throw<ArgumentException>().WithMessage("*32 bytes*");
    }

    [Test]
    public void Promote_Secondary_Becomes_Primary_And_Bumps_Version()
    {
        var cfg = BuildConfig(primary: Convert.ToBase64String(PrimaryKey), activeVersion: 1);
        var sut = new KekProvider(cfg, NullLogger<KekProvider>.Instance);
        sut.StageSecondary(SecondaryKey);

        sut.PromoteSecondaryToPrimary(2);

        sut.GetPrimary().Should().BeEquivalentTo(SecondaryKey);
        sut.GetSecondary().Should().BeNull("the previous secondary is now the primary");
        sut.GetActiveVersion().Should().Be(2);
    }

    [Test]
    public void Promote_Without_Secondary_Throws()
    {
        var cfg = BuildConfig(primary: Convert.ToBase64String(PrimaryKey));
        var sut = new KekProvider(cfg, NullLogger<KekProvider>.Instance);

        Action act = () => sut.PromoteSecondaryToPrimary(2);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*no secondary KEK is staged*");
    }

    [Test]
    public void Promote_With_Stale_Version_Throws()
    {
        var cfg = BuildConfig(primary: Convert.ToBase64String(PrimaryKey), activeVersion: 5);
        var sut = new KekProvider(cfg, NullLogger<KekProvider>.Instance);
        sut.StageSecondary(SecondaryKey);

        Action act = () => sut.PromoteSecondaryToPrimary(5);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*must exceed*");
    }

    [Test]
    public void GetSnapshot_Captures_All_Fields()
    {
        var cfg = BuildConfig(
            primary: Convert.ToBase64String(PrimaryKey),
            secondary: Convert.ToBase64String(SecondaryKey),
            activeVersion: 3);
        var sut = new KekProvider(cfg, NullLogger<KekProvider>.Instance);

        var snap = sut.GetSnapshot();

        snap.Primary.Should().BeEquivalentTo(PrimaryKey);
        snap.Secondary.Should().BeEquivalentTo(SecondaryKey);
        snap.ActiveVersion.Should().Be(3);
    }
}
