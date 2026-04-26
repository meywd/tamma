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

    // ── R2-H13: GetByVersion lookup tests ────────────────────────────

    [Test]
    public void GetByVersion_Returns_Primary_For_Active_Version()
    {
        var cfg = BuildConfig(
            primary: Convert.ToBase64String(PrimaryKey),
            activeVersion: 5);
        var sut = new KekProvider(cfg, NullLogger<KekProvider>.Instance);

        var slot = sut.GetByVersion(5);

        slot.Should().NotBeNull();
        slot!.Version.Should().Be(5);
        slot.Material.Should().BeEquivalentTo(PrimaryKey);
        slot.Kind.Should().Be(KekSlotKind.Primary);
    }

    [Test]
    public void GetByVersion_Returns_Secondary_For_Previous_Version_When_Configured_At_Startup()
    {
        // When the secondary is configured at startup (rotation step 1
        // shape), the cabinet treats the secondary as the
        // "previous primary" — secondaryVersion = activeVersion - 1.
        var cfg = BuildConfig(
            primary: Convert.ToBase64String(PrimaryKey),
            secondary: Convert.ToBase64String(SecondaryKey),
            activeVersion: 5);
        var sut = new KekProvider(cfg, NullLogger<KekProvider>.Instance);

        var slot = sut.GetByVersion(4);

        slot.Should().NotBeNull();
        slot!.Material.Should().BeEquivalentTo(SecondaryKey);
        slot.Kind.Should().Be(KekSlotKind.Secondary);
    }

    [Test]
    public void GetByVersion_Returns_Secondary_For_Staged_Plus_One_After_StageSecondary()
    {
        // After StageSecondary (rotation step 2), the secondary is the
        // upcoming primary — secondaryVersion = activeVersion + 1.
        var cfg = BuildConfig(
            primary: Convert.ToBase64String(PrimaryKey),
            activeVersion: 5);
        var sut = new KekProvider(cfg, NullLogger<KekProvider>.Instance);

        sut.StageSecondary(SecondaryKey);

        var slot = sut.GetByVersion(6);
        slot.Should().NotBeNull();
        slot!.Material.Should().BeEquivalentTo(SecondaryKey);
        slot.Kind.Should().Be(KekSlotKind.Secondary);
    }

    [Test]
    public void GetByVersion_Returns_Retired_Slot_After_Promotion()
    {
        // After PromoteSecondaryToPrimary, the previous primary moves
        // into the retired-keys ring.
        var cfg = BuildConfig(
            primary: Convert.ToBase64String(PrimaryKey),
            activeVersion: 5);
        var sut = new KekProvider(cfg, NullLogger<KekProvider>.Instance);
        sut.StageSecondary(SecondaryKey);
        sut.PromoteSecondaryToPrimary(6);

        // Active is now SecondaryKey at version 6; PrimaryKey is in
        // the retired ring at version 5.
        var activeSlot = sut.GetByVersion(6);
        activeSlot!.Material.Should().BeEquivalentTo(SecondaryKey);
        activeSlot.Kind.Should().Be(KekSlotKind.Primary);

        var retiredSlot = sut.GetByVersion(5);
        retiredSlot.Should().NotBeNull();
        retiredSlot!.Material.Should().BeEquivalentTo(PrimaryKey);
        retiredSlot.Kind.Should().Be(KekSlotKind.Retired);
    }

    [Test]
    public void GetByVersion_Returns_Null_For_Pruned_Or_Unknown_Version()
    {
        var cfg = BuildConfig(
            primary: Convert.ToBase64String(PrimaryKey),
            activeVersion: 5);
        var sut = new KekProvider(cfg, NullLogger<KekProvider>.Instance);

        sut.GetByVersion(99).Should().BeNull("99 is not in the cabinet");
        sut.GetByVersion(0).Should().BeNull("0 is not a valid version");
        sut.GetByVersion(-1).Should().BeNull("negative versions are invalid");
    }

    [Test]
    public void GetByVersion_Returns_Defensive_Copy_Of_Material()
    {
        var cfg = BuildConfig(
            primary: Convert.ToBase64String(PrimaryKey),
            activeVersion: 1);
        var sut = new KekProvider(cfg, NullLogger<KekProvider>.Instance);

        var slot1 = sut.GetByVersion(1)!;
        slot1.Material[0] = 0xFF;
        var slot2 = sut.GetByVersion(1)!;
        slot2.Material[0].Should().Be(PrimaryKey[0],
            "mutating the returned material must not corrupt the cabinet");
    }

    [Test]
    public void RetainedHistorySize_Bounds_The_Retired_Ring()
    {
        // History size of 1 means after two promotions, only the most
        // recent retired key is kept.
        var dict = new Dictionary<string, string?>
        {
            [KekProvider.PrimaryConfigKey] = Convert.ToBase64String(PrimaryKey),
            [KekProvider.ActiveVersionConfigKey] = "1",
            [KekProvider.RetainedHistorySizeConfigKey] = "1",
        };
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        var sut = new KekProvider(cfg, NullLogger<KekProvider>.Instance);

        var k2 = new byte[32]; for (var i = 0; i < 32; i++) k2[i] = (byte)(i + 50);
        var k3 = new byte[32]; for (var i = 0; i < 32; i++) k3[i] = (byte)(i + 100);

        sut.StageSecondary(k2);
        sut.PromoteSecondaryToPrimary(2);
        // After this: active=v2 (k2), retired=[v1 (PrimaryKey)]

        sut.StageSecondary(k3);
        sut.PromoteSecondaryToPrimary(3);
        // After this: active=v3 (k3), retired=[v2 (k2)] — v1 pruned

        sut.GetByVersion(3).Should().NotBeNull("v3 is the primary");
        sut.GetByVersion(2).Should().NotBeNull("v2 is the only retired slot kept");
        sut.GetByVersion(1).Should().BeNull(
            "v1 is older than RetainedHistorySize=1 — it was pruned");
        sut.RetainedHistorySize.Should().Be(1);
    }

    [Test]
    public void RestoreStagedSecondary_Loads_Persisted_Material_Across_Restart()
    {
        // R2-H14: simulate a restart where the in-memory secondary was
        // lost but kek_rotations row still has it. The coordinator
        // calls RestoreStagedSecondary at startup to repopulate the
        // cabinet. After this the version-explicit decrypt path can
        // answer for the staged version.
        var cfg = BuildConfig(
            primary: Convert.ToBase64String(PrimaryKey),
            activeVersion: 1);
        var sut = new KekProvider(cfg, NullLogger<KekProvider>.Instance);

        sut.RestoreStagedSecondary(SecondaryKey, newSecondaryVersion: 2);

        sut.GetSecondary().Should().BeEquivalentTo(SecondaryKey);
        var slot = sut.GetByVersion(2);
        slot.Should().NotBeNull();
        slot!.Material.Should().BeEquivalentTo(SecondaryKey);
        slot.Kind.Should().Be(KekSlotKind.Secondary);
    }

    [Test]
    public void GetAllSlots_Returns_Active_Plus_Secondary_Plus_Retired_NewestFirst()
    {
        var cfg = BuildConfig(
            primary: Convert.ToBase64String(PrimaryKey),
            activeVersion: 1);
        var sut = new KekProvider(cfg, NullLogger<KekProvider>.Instance);

        var k2 = new byte[32]; for (var i = 0; i < 32; i++) k2[i] = (byte)(i + 50);
        sut.StageSecondary(k2);
        sut.PromoteSecondaryToPrimary(2);

        var slots = sut.GetAllSlots();

        slots.Should().HaveCount(2, "primary v2 + retired v1");
        slots[0].Version.Should().Be(2);
        slots[0].Kind.Should().Be(KekSlotKind.Primary);
        slots[1].Version.Should().Be(1);
        slots[1].Kind.Should().Be(KekSlotKind.Retired);
    }
}
