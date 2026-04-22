using System.Security.Cryptography;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Secrets.Postgres;

namespace Tamma.Api.Tests.Secrets.Postgres;

/// <summary>
/// Tests for <see cref="SecretEnvelope"/> — the AES-256-GCM
/// envelope-encryption helper introduced by Story 29-2. Pins the
/// wire format, the round-trip contract, and the
/// tamper-detection / format-version-mismatch error paths.
/// </summary>
[TestFixture]
public class SecretEnvelopeTests
{
    private const byte PrimaryKekId = 1;
    private byte[] _kek = null!;
    private TestKekProvider _provider = null!;

    [SetUp]
    public void SetUp()
    {
        _kek = RandomNumberGenerator.GetBytes(SecretEnvelope.KeySize);
        _provider = new TestKekProvider(PrimaryKekId, _kek);
    }

    [Test]
    public void Encrypt_ProducesEnvelopeWithExpectedHeader()
    {
        var envelope = SecretEnvelope.Encrypt("hunter2", PrimaryKekId, _kek);

        envelope[0].Should().Be(SecretEnvelope.CurrentFormatVersion,
            "format version byte at offset 0");
        envelope[1].Should().Be(PrimaryKekId,
            "kek id byte at offset 1");
        // Header (74 bytes) + plaintext "hunter2" (7 UTF-8 bytes) +
        // value tag (16 bytes) = 97. Pinning the math here so a
        // future format-version bump can't silently change the
        // wire size without bumping the test.
        envelope.Length.Should().Be(74 + 7 + 16);
    }

    [Test]
    public void Encrypt_Decrypt_RoundTripsPlaintext()
    {
        var envelope = SecretEnvelope.Encrypt("hunter2", PrimaryKekId, _kek);
        var decrypted = SecretEnvelope.Decrypt(envelope, _provider);
        decrypted.Should().Be("hunter2");
    }

    [Test]
    public void Encrypt_TwoCalls_ProduceDifferentEnvelopes()
    {
        // Fresh DEK + nonces per call → identical plaintext yields
        // different ciphertext (semantic security).
        var first = SecretEnvelope.Encrypt("same-input", PrimaryKekId, _kek);
        var second = SecretEnvelope.Encrypt("same-input", PrimaryKekId, _kek);
        first.Should().NotEqual(second);
    }

    [TestCase("")]
    [TestCase("a")]
    [TestCase("the quick brown fox jumps over the lazy dog")]
    public void Encrypt_Decrypt_HandlesVariousLengths(string plaintext)
    {
        var envelope = SecretEnvelope.Encrypt(plaintext, PrimaryKekId, _kek);
        var decrypted = SecretEnvelope.Decrypt(envelope, _provider);
        decrypted.Should().Be(plaintext);
    }

    [Test]
    public void Encrypt_HandlesUnicodePlaintext()
    {
        // Non-ASCII chars + multi-byte emoji to confirm the UTF-8
        // round-trip stays bit-exact.
        const string plaintext = "café \ud83d\udd10 secret";
        var envelope = SecretEnvelope.Encrypt(plaintext, PrimaryKekId, _kek);
        var decrypted = SecretEnvelope.Decrypt(envelope, _provider);
        decrypted.Should().Be(plaintext);
    }

    [Test]
    public void Encrypt_RejectsWrongLengthKek()
    {
        Action act = () =>
            SecretEnvelope.Encrypt("x", PrimaryKekId, new byte[16]);
        act.Should().Throw<ArgumentException>()
            .WithMessage("*32 bytes*");
    }

    [Test]
    public void Encrypt_RejectsNullPlaintext()
    {
        Action act = () =>
            SecretEnvelope.Encrypt(null!, PrimaryKekId, _kek);
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void Encrypt_RejectsNullKek()
    {
        Action act = () =>
            SecretEnvelope.Encrypt("x", PrimaryKekId, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void Decrypt_DetectsTamperedCiphertext()
    {
        var envelope = SecretEnvelope.Encrypt("hunter2", PrimaryKekId, _kek);
        // Flip a bit in the value ciphertext region (offset 74).
        envelope[74] ^= 0x01;
        Action act = () => SecretEnvelope.Decrypt(envelope, _provider);
        act.Should().Throw<CryptographicException>(
            "AES-GCM tag check fails on tampered ciphertext");
    }

    [Test]
    public void Decrypt_DetectsTamperedWrapTag()
    {
        var envelope = SecretEnvelope.Encrypt("hunter2", PrimaryKekId, _kek);
        // Wrap tag sits at offset 46..62.
        envelope[46] ^= 0x01;
        Action act = () => SecretEnvelope.Decrypt(envelope, _provider);
        act.Should().Throw<CryptographicException>(
            "AES-GCM wrap-tag check fails on tampered tag bytes");
    }

    [Test]
    public void Decrypt_DetectsTamperedValueTag()
    {
        var envelope = SecretEnvelope.Encrypt("hunter2", PrimaryKekId, _kek);
        // Value tag is the last 16 bytes.
        envelope[^1] ^= 0x01;
        Action act = () => SecretEnvelope.Decrypt(envelope, _provider);
        act.Should().Throw<CryptographicException>();
    }

    [Test]
    public void Decrypt_DetectsTamperedKekIdByte()
    {
        var envelope = SecretEnvelope.Encrypt("hunter2", PrimaryKekId, _kek);
        envelope[1] = 99; // claim a different KEK slot
        Action act = () => SecretEnvelope.Decrypt(envelope, _provider);
        // The provider doesn't have slot 99 → KekNotAvailable.
        act.Should().Throw<KekNotAvailableException>();
    }

    [Test]
    public void Decrypt_RejectsUnsupportedFormatVersion()
    {
        var envelope = SecretEnvelope.Encrypt("hunter2", PrimaryKekId, _kek);
        envelope[0] = 99; // unknown future version
        Action act = () => SecretEnvelope.Decrypt(envelope, _provider);
        act.Should().Throw<SecretEnvelopeFormatException>()
            .WithMessage("*format version 99*");
    }

    [Test]
    public void Decrypt_RejectsTooShortEnvelope()
    {
        var truncated = new byte[10];
        Action act = () => SecretEnvelope.Decrypt(truncated, _provider);
        act.Should().Throw<SecretEnvelopeFormatException>()
            .WithMessage("*shorter than the minimum*");
    }

    [Test]
    public void Decrypt_WithWrongKek_FailsTagCheck()
    {
        var envelope = SecretEnvelope.Encrypt("hunter2", PrimaryKekId, _kek);
        // Provider returns a DIFFERENT 32-byte key for the same slot id
        // → AES-GCM unwrap of the DEK fails the tag check.
        var wrongKek = RandomNumberGenerator.GetBytes(SecretEnvelope.KeySize);
        var wrongProvider = new TestKekProvider(PrimaryKekId, wrongKek);
        Action act = () => SecretEnvelope.Decrypt(envelope, wrongProvider);
        act.Should().Throw<CryptographicException>();
    }

    [Test]
    public void ReadFormatVersion_ReturnsByteAtOffsetZero()
    {
        var envelope = SecretEnvelope.Encrypt("x", PrimaryKekId, _kek);
        SecretEnvelope.ReadFormatVersion(envelope)
            .Should().Be(SecretEnvelope.CurrentFormatVersion);
    }

    [Test]
    public void ReadKekId_ReturnsByteAtOffsetOne()
    {
        var envelope = SecretEnvelope.Encrypt("x", PrimaryKekId, _kek);
        SecretEnvelope.ReadKekId(envelope).Should().Be(PrimaryKekId);
    }

    [Test]
    public void ReadFormatVersion_RejectsEmptyEnvelope()
    {
        Action act = () => SecretEnvelope.ReadFormatVersion(Array.Empty<byte>());
        act.Should().Throw<SecretEnvelopeFormatException>();
    }

    [Test]
    public void ReadKekId_RejectsTooShortEnvelope()
    {
        Action act = () => SecretEnvelope.ReadKekId(new byte[1]);
        act.Should().Throw<SecretEnvelopeFormatException>();
    }

    [Test]
    [Repeat(50)]
    public void RandomPlaintexts_RoundTripWithoutLoss()
    {
        // Property-style test: 50 randomly-sized random plaintexts
        // round-trip bit-exact. Repeat(50) for a quick sweep without
        // adding a property-test framework dep.
        var length = Random.Shared.Next(1, 1024);
        var bytes = RandomNumberGenerator.GetBytes(length);
        // Use base64 to keep the plaintext valid UTF-8 (the wire
        // format uses UTF-8 byte length, not raw byte length).
        var plaintext = Convert.ToBase64String(bytes);

        var envelope = SecretEnvelope.Encrypt(plaintext, PrimaryKekId, _kek);
        var decrypted = SecretEnvelope.Decrypt(envelope, _provider);
        decrypted.Should().Be(plaintext);
    }

    /// <summary>
    /// Test double for <see cref="IKekProvider"/> — returns a single
    /// known KEK for a single slot id, throws
    /// <see cref="KekNotAvailableException"/> for any other slot.
    /// </summary>
    private sealed class TestKekProvider : IKekProvider
    {
        private readonly byte _slot;
        private readonly byte[] _key;

        public TestKekProvider(byte slot, byte[] key)
        {
            _slot = slot;
            _key = key;
        }

        public byte PrimaryKekId => _slot;

        public byte[] GetKek(byte kekId)
        {
            if (kekId != _slot) throw new KekNotAvailableException(kekId);
            return (byte[])_key.Clone();
        }

        public bool TryGetKek(byte kekId, out byte[]? key)
        {
            if (kekId != _slot)
            {
                key = null;
                return false;
            }
            key = (byte[])_key.Clone();
            return true;
        }
    }
}
