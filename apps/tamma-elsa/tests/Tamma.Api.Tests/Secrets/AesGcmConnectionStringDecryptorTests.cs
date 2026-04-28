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
        // R2-H13 update: when kekVersion is null (legacy row), the
        // adapter still walks primary then secondary. Test confirms
        // that fallback path.
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

        // kekVersion=null exercises the legacy heuristic: try primary,
        // fall back to secondary.
        var result = sut.Decrypt(envelope, kekVersion: null);

        result.Should().Be(Plaintext, "fallback to secondary covers the rotation overlap (legacy heuristic)");
    }

    [Test]
    public void Decrypt_With_Version_Hint_Uses_Secondary_Slot_Directly()
    {
        // R2-H13: when the row carries a kekVersion that matches the
        // SECONDARY slot's version (i.e. the row was written under the
        // previous primary, which is now the secondary), the adapter
        // uses GetByVersion() to look up the secondary directly — no
        // primary-then-secondary heuristic needed.
        var oldPrimary = BuildKek(seed: 1);
        var newPrimary = BuildKek(seed: 50);

        var envelope = AesGcmConnectionStringDecryptor.EncryptWithKey(Plaintext, oldPrimary);
        var dict = new Dictionary<string, string?>
        {
            [KekProvider.PrimaryConfigKey] = Convert.ToBase64String(newPrimary),
            [KekProvider.SecondaryConfigKey] = Convert.ToBase64String(oldPrimary),
            [KekProvider.ActiveVersionConfigKey] = "2",
        };
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        var provider = new KekProvider(cfg, NullLogger<KekProvider>.Instance);
        var sut = CreateSut(provider);

        // The OLD primary version is 1 (active=2, secondary=active-1=1).
        // GetByVersion(1) should return the secondary slot.
        var result = sut.Decrypt(envelope, kekVersion: 1);
        result.Should().Be(Plaintext);
    }

    [Test]
    public void Decrypt_With_Version_Hint_Uses_Retired_Slot_After_Two_Rotations()
    {
        // R2-H13: a row two versions back must remain decryptable via
        // the retired-keys ring after a rotation completes. Simulate by
        // staging + promoting twice and confirming a row still tagged
        // with the original version decrypts via the retired ring.
        var v1Key = BuildKek(seed: 1);
        var v2Key = BuildKek(seed: 50);
        var v3Key = BuildKek(seed: 100);

        var envelope = AesGcmConnectionStringDecryptor.EncryptWithKey(Plaintext, v1Key);
        var dict = new Dictionary<string, string?>
        {
            [KekProvider.PrimaryConfigKey] = Convert.ToBase64String(v1Key),
            [KekProvider.ActiveVersionConfigKey] = "1",
            [KekProvider.RetainedHistorySizeConfigKey] = "5",
        };
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
        var provider = new KekProvider(cfg, NullLogger<KekProvider>.Instance);

        // First rotation: v1 → v2. v1 moves into retired ring.
        provider.StageSecondary(v2Key);
        provider.PromoteSecondaryToPrimary(2);

        // Second rotation: v2 → v3. v2 moves into retired ring.
        provider.StageSecondary(v3Key);
        provider.PromoteSecondaryToPrimary(3);

        var sut = CreateSut(provider);

        // Row was encrypted under v1; cabinet still holds v1 in the
        // retired ring.
        var result = sut.Decrypt(envelope, kekVersion: 1);
        result.Should().Be(Plaintext, "retired-keys ring keeps v1 decryptable after two rotations");
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
        // R2-H13 update: with no primary configured and kekVersion=null
        // (legacy path), the adapter throws InvalidOperationException
        // with a clear message about the missing primary KEK.
        var provider = new KekProvider(
            BuildConfig(primary: null, secondary: null),
            NullLogger<KekProvider>.Instance);
        var sut = CreateSut(provider);
        var envelope = new byte[32];

        Action act = () => sut.Decrypt(envelope, kekVersion: null);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*No primary KEK*");
    }

    [Test]
    public void Decrypt_With_Unknown_Version_Throws_Cryptographic_Error()
    {
        // R2-H13: when the caller supplies a kekVersion that's not in
        // the cabinet (active / secondary / retired-ring), the adapter
        // throws a CryptographicException with a clear message about
        // the missing slot — it does NOT fall back to the legacy
        // primary-then-secondary heuristic.
        var primary = BuildKek(seed: 1);
        var envelope = AesGcmConnectionStringDecryptor.EncryptWithKey(Plaintext, primary);
        var provider = new KekProvider(
            BuildConfig(primary, secondary: null),
            NullLogger<KekProvider>.Instance);
        var sut = CreateSut(provider);

        Action act = () => sut.Decrypt(envelope, kekVersion: 99);

        act.Should().Throw<CryptographicException>()
            .WithMessage("*KEK version 99*not present*");
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
    public void Decrypt_Null_KekVersion_Uses_Legacy_Heuristic()
    {
        // R2-H13: kekVersion=null exercises the legacy primary-then-
        // secondary heuristic. Used for rows that pre-date the
        // KekVersion column.
        var primary = BuildKek(seed: 1);
        var envelope = AesGcmConnectionStringDecryptor.EncryptWithKey(Plaintext, primary);
        var provider = new KekProvider(
            BuildConfig(primary, secondary: null),
            NullLogger<KekProvider>.Instance);
        var sut = CreateSut(provider);

        sut.Decrypt(envelope, kekVersion: null).Should().Be(Plaintext);
    }
}
