using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Tamma.Api.Services.Provisioning;
using Tamma.Api.Services.Secrets;

namespace Tamma.Api.Tests.Secrets;

/// <summary>
/// Story 28-12 — unit suite for the AES-GCM
/// <see cref="AesGcmConnectionStringDecryptor"/> adapter that wraps
/// <see cref="TenantSecretProtector"/> behind the
/// <see cref="Tamma.Data.Abstractions.IConnectionStringDecryptor"/>
/// seam consumed by the Story 28-4 LRU pooled resolver.
/// </summary>
[TestFixture]
public class AesGcmConnectionStringDecryptorTests
{
    private const string Plaintext =
        "Host=localhost;Port=5432;Database=tenant_db;Username=tamma_app;Password=hunter2";

    private static byte[] BuildKek(byte seed)
    {
        var key = new byte[32];
        for (var i = 0; i < key.Length; i++) key[i] = (byte)(seed + i);
        return key;
    }

    private static IConfiguration BuildConfig(byte[]? primary, byte[]? secondary)
    {
        var dict = new Dictionary<string, string?>();
        if (primary is not null)
            dict[KekProvider.PrimaryConfigKey] = Convert.ToBase64String(primary);
        if (secondary is not null)
            dict[KekProvider.SecondaryConfigKey] = Convert.ToBase64String(secondary);
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    private static AesGcmConnectionStringDecryptor CreateSut(KekProvider provider)
        => new(provider, NullLogger<AesGcmConnectionStringDecryptor>.Instance);

    [Test]
    public void Decrypt_Round_Trip_Under_Primary()
    {
        var primary = BuildKek(seed: 1);
        var envelope = AesGcmConnectionStringDecryptor.EncryptWithKey(Plaintext, primary);
        var provider = new KekProvider(
            BuildConfig(primary, secondary: null),
            NullLogger<KekProvider>.Instance);
        var sut = CreateSut(provider);

        var result = sut.Decrypt(envelope, kekVersion: 1);

        result.Should().Be(Plaintext);
    }

    [Test]
    public void Decrypt_Falls_Back_To_Secondary_When_Primary_Mismatches()
    {
        var oldPrimary = BuildKek(seed: 1);
        var newPrimary = BuildKek(seed: 50);

        // Envelope was written under the OLD primary — once we deploy
        // newPrimary it lands in the primary slot and the previous
        // primary moves to secondary for the rotation window.
        var envelope = AesGcmConnectionStringDecryptor.EncryptWithKey(Plaintext, oldPrimary);
        var provider = new KekProvider(
            BuildConfig(primary: newPrimary, secondary: oldPrimary),
            NullLogger<KekProvider>.Instance);
        var sut = CreateSut(provider);

        var result = sut.Decrypt(envelope, kekVersion: 1);

        result.Should().Be(Plaintext, "fallback to secondary covers the rotation overlap");
    }

    [Test]
    public void Decrypt_Throws_When_Both_Keys_Mismatch()
    {
        var actualEncryptionKey = BuildKek(seed: 9);
        var primary = BuildKek(seed: 1);
        var secondary = BuildKek(seed: 50);

        var envelope = AesGcmConnectionStringDecryptor.EncryptWithKey(Plaintext, actualEncryptionKey);
        var provider = new KekProvider(
            BuildConfig(primary, secondary),
            NullLogger<KekProvider>.Instance);
        var sut = CreateSut(provider);

        Action act = () => sut.Decrypt(envelope, kekVersion: 1);

        act.Should().Throw<CryptographicException>();
    }

    [Test]
    public void Decrypt_Without_Primary_Throws_Clear_Error()
    {
        var provider = new KekProvider(
            BuildConfig(primary: null, secondary: null),
            NullLogger<KekProvider>.Instance);
        var sut = CreateSut(provider);
        var envelope = new byte[32];

        Action act = () => sut.Decrypt(envelope, kekVersion: 1);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*No primary KEK*");
    }

    [Test]
    public void Decrypt_Empty_Envelope_Throws_ArgumentException()
    {
        var provider = new KekProvider(
            BuildConfig(BuildKek(seed: 1), secondary: null),
            NullLogger<KekProvider>.Instance);
        var sut = CreateSut(provider);

        Action act = () => sut.Decrypt(Array.Empty<byte>(), kekVersion: 1);

        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void Decrypt_Null_Envelope_Throws_ArgumentNullException()
    {
        var provider = new KekProvider(
            BuildConfig(BuildKek(seed: 1), secondary: null),
            NullLogger<KekProvider>.Instance);
        var sut = CreateSut(provider);

        Action act = () => sut.Decrypt(null!, kekVersion: 1);

        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void Decrypt_Tamper_Detected_Via_Auth_Tag()
    {
        var primary = BuildKek(seed: 1);
        var envelope = AesGcmConnectionStringDecryptor.EncryptWithKey(Plaintext, primary);
        // Flip a single byte in the ciphertext region (after the 12-byte
        // nonce). Auth tag must reject the tampered payload.
        envelope[20] ^= 0xFF;

        var provider = new KekProvider(
            BuildConfig(primary, secondary: null),
            NullLogger<KekProvider>.Instance);
        var sut = CreateSut(provider);

        Action act = () => sut.Decrypt(envelope, kekVersion: 1);

        act.Should().Throw<CryptographicException>();
    }

    [Test]
    public void EncryptWithKey_DecryptWithKey_Round_Trip_Without_Provider()
    {
        var key = BuildKek(seed: 42);
        var envelope = AesGcmConnectionStringDecryptor.EncryptWithKey(Plaintext, key);

        var roundTripped = AesGcmConnectionStringDecryptor.DecryptWithKey(envelope, key);

        roundTripped.Should().Be(Plaintext);
    }

    [Test]
    public void Decrypt_Ignores_KekVersion_Hint()
    {
        // The version hint is informational — the adapter still tries
        // primary first regardless. This test confirms a stale hint
        // does not prevent a successful decrypt.
        var primary = BuildKek(seed: 1);
        var envelope = AesGcmConnectionStringDecryptor.EncryptWithKey(Plaintext, primary);
        var provider = new KekProvider(
            BuildConfig(primary, secondary: null),
            NullLogger<KekProvider>.Instance);
        var sut = CreateSut(provider);

        sut.Decrypt(envelope, kekVersion: null).Should().Be(Plaintext);
        sut.Decrypt(envelope, kekVersion: 99).Should().Be(Plaintext);
    }
}
