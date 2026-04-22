using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Secrets.Postgres;

namespace Tamma.Api.Tests.Secrets.Postgres;

/// <summary>
/// Tests for <see cref="EnvKekProvider"/> — the env-var-sourced
/// implementation of <see cref="IKekProvider"/> introduced by Story
/// 29-2. Covers the parse / validation contract on the
/// <c>kekId:base64key</c> spec format and the fail-fast behaviour
/// on a misconfigured host.
/// </summary>
[TestFixture]
public class EnvKekProviderTests
{
    private static readonly string ValidKey32 =
        Convert.ToBase64String(new byte[32]); // all zeros, 32 bytes

    private static readonly string AnotherValidKey32 =
        Convert.ToBase64String(Enumerable.Range(0, 32)
            .Select(i => (byte)i).ToArray());

    [Test]
    public void Constructor_AcceptsPrimaryOnly()
    {
        var provider = new EnvKekProvider($"1:{ValidKey32}");
        provider.PrimaryKekId.Should().Be(1);
        provider.GetKek(1).Should().HaveCount(32);
    }

    [Test]
    public void Constructor_AcceptsPrimaryAndSecondary()
    {
        var provider = new EnvKekProvider(
            $"1:{ValidKey32}",
            $"2:{AnotherValidKey32}");

        provider.PrimaryKekId.Should().Be(1);
        provider.GetKek(1).Should().HaveCount(32);
        provider.GetKek(2).Should().HaveCount(32);
    }

    [Test]
    public void GetKek_ReturnsBitExactKeyMaterial()
    {
        var provider = new EnvKekProvider($"7:{AnotherValidKey32}");
        var fetched = provider.GetKek(7);
        fetched.Should().Equal(Enumerable.Range(0, 32).Select(i => (byte)i));
    }

    [Test]
    public void GetKek_ReturnsDefensiveCopy()
    {
        // Mutating the returned array must not corrupt the provider's
        // backing buffer for subsequent callers.
        var provider = new EnvKekProvider($"1:{ValidKey32}");
        var first = provider.GetKek(1);
        first[0] = 99;
        var second = provider.GetKek(1);
        second[0].Should().Be(0, "defensive copy isolates callers from each other");
    }

    [Test]
    public void GetKek_ThrowsOnUnknownSlot()
    {
        var provider = new EnvKekProvider($"1:{ValidKey32}");
        Action act = () => provider.GetKek(99);
        act.Should().Throw<KekNotAvailableException>()
            .Where(ex => ex.KekId == 99);
    }

    [Test]
    public void TryGetKek_ReturnsTrueForLoadedSlot()
    {
        var provider = new EnvKekProvider($"1:{ValidKey32}");
        provider.TryGetKek(1, out var key).Should().BeTrue();
        key.Should().NotBeNull();
        key!.Length.Should().Be(32);
    }

    [Test]
    public void TryGetKek_ReturnsFalseForUnknownSlot()
    {
        var provider = new EnvKekProvider($"1:{ValidKey32}");
        provider.TryGetKek(99, out var key).Should().BeFalse();
        key.Should().BeNull();
    }

    [Test]
    public void Constructor_RejectsNullPrimary()
    {
        Action act = () => new EnvKekProvider(null!);
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void Constructor_RejectsEmptyPrimary()
    {
        Action act = () => new EnvKekProvider("");
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void Constructor_RejectsMalformedSpec_NoColon()
    {
        Action act = () => new EnvKekProvider(ValidKey32); // no "1:" prefix
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*colon separator*");
    }

    [Test]
    public void Constructor_RejectsMalformedSpec_ColonAtEnd()
    {
        Action act = () => new EnvKekProvider("1:");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*colon separator*");
    }

    [Test]
    public void Constructor_RejectsMalformedSpec_ColonAtStart()
    {
        Action act = () => new EnvKekProvider($":{ValidKey32}");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*colon separator*");
    }

    [Test]
    public void Constructor_RejectsNonByteSlotId()
    {
        Action act = () => new EnvKekProvider($"99999:{ValidKey32}");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not a byte*");
    }

    [Test]
    public void Constructor_RejectsNonNumericSlotId()
    {
        Action act = () => new EnvKekProvider($"abc:{ValidKey32}");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not a byte*");
    }

    [Test]
    public void Constructor_RejectsBadBase64Key()
    {
        Action act = () => new EnvKekProvider("1:not-valid-base64!@#");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*base64*");
    }

    [Test]
    public void Constructor_RejectsTooShortKey()
    {
        // 16-byte key (AES-128) → not accepted; we mandate AES-256.
        var shortKey = Convert.ToBase64String(new byte[16]);
        Action act = () => new EnvKekProvider($"1:{shortKey}");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*key length is 16 bytes*");
    }

    [Test]
    public void Constructor_RejectsTooLongKey()
    {
        var longKey = Convert.ToBase64String(new byte[64]);
        Action act = () => new EnvKekProvider($"1:{longKey}");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*key length is 64 bytes*");
    }

    [Test]
    public void Constructor_RejectsDuplicateSlotIds()
    {
        Action act = () => new EnvKekProvider(
            $"1:{ValidKey32}",
            $"1:{AnotherValidKey32}");
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*share slot id 1*");
    }

    [Test]
    public void FromEnvironment_ThrowsWhenPrimaryUnset()
    {
        // Snapshot + clear + restore so we don't pollute the test
        // process for other suites.
        var saved = Environment.GetEnvironmentVariable(EnvKekProvider.PrimaryEnvVar);
        Environment.SetEnvironmentVariable(EnvKekProvider.PrimaryEnvVar, null);
        try
        {
            Action act = () => EnvKekProvider.FromEnvironment();
            act.Should().Throw<InvalidOperationException>()
                .WithMessage($"*{EnvKekProvider.PrimaryEnvVar}*");
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvKekProvider.PrimaryEnvVar, saved);
        }
    }

    [Test]
    public void FromEnvironment_LoadsPrimaryFromEnv()
    {
        var savedPrimary = Environment.GetEnvironmentVariable(EnvKekProvider.PrimaryEnvVar);
        var savedSecondary = Environment.GetEnvironmentVariable(EnvKekProvider.SecondaryEnvVar);
        Environment.SetEnvironmentVariable(
            EnvKekProvider.PrimaryEnvVar, $"1:{ValidKey32}");
        Environment.SetEnvironmentVariable(EnvKekProvider.SecondaryEnvVar, null);
        try
        {
            var provider = EnvKekProvider.FromEnvironment();
            provider.PrimaryKekId.Should().Be(1);
            provider.GetKek(1).Should().HaveCount(32);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvKekProvider.PrimaryEnvVar, savedPrimary);
            Environment.SetEnvironmentVariable(EnvKekProvider.SecondaryEnvVar, savedSecondary);
        }
    }

    [Test]
    public void FromEnvironment_LoadsBothKeysFromEnv()
    {
        var savedPrimary = Environment.GetEnvironmentVariable(EnvKekProvider.PrimaryEnvVar);
        var savedSecondary = Environment.GetEnvironmentVariable(EnvKekProvider.SecondaryEnvVar);
        Environment.SetEnvironmentVariable(
            EnvKekProvider.PrimaryEnvVar, $"3:{ValidKey32}");
        Environment.SetEnvironmentVariable(
            EnvKekProvider.SecondaryEnvVar, $"4:{AnotherValidKey32}");
        try
        {
            var provider = EnvKekProvider.FromEnvironment();
            provider.PrimaryKekId.Should().Be(3);
            provider.GetKek(3).Should().HaveCount(32);
            provider.GetKek(4).Should().HaveCount(32);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvKekProvider.PrimaryEnvVar, savedPrimary);
            Environment.SetEnvironmentVariable(EnvKekProvider.SecondaryEnvVar, savedSecondary);
        }
    }
}
