using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Auth;

namespace Tamma.Api.Tests.Auth;

[TestFixture]
public class PasswordServiceTests
{
    private PasswordService _service = null!;

    [SetUp]
    public void Setup() => _service = new PasswordService();

    [Test]
    public void HashPassword_RoundTrip_VerifiesArgon2()
    {
        var hash = _service.HashPassword("CorrectHorseBattery1");
        hash.Should().StartWith("$argon2id$");
        _service.VerifyPassword("CorrectHorseBattery1", hash).Should().BeTrue();
        _service.VerifyPassword("wrong-password", hash).Should().BeFalse();
    }

    [Test]
    public void VerifyPassword_WithScryptFormatHash_ReturnsTrue()
    {
        // Fixture: hash of "secret" computed via Node scrypt with the TS
        // params {N=16384, r=8, p=1, keylen=32, salt=16}. Generated at
        // remediation time via a one-shot Node script.
        // To keep tests self-contained without network, we generate the
        // fixture at test time using the same scrypt code path the app
        // uses (BouncyCastle SCrypt.Generate). This does NOT cover wire
        // compat against TS's own output but DOES cover the scrypt-format
        // parsing path in PasswordService.
        var password = "secret";
        var saltHex = "0123456789abcdef0123456789abcdef";
        var salt = Convert.FromHexString(saltHex);
        var derived = Org.BouncyCastle.Crypto.Generators.SCrypt.Generate(
            System.Text.Encoding.UTF8.GetBytes(password),
            salt, 16384, 8, 1, 32);
        var derivedHex = Convert.ToHexString(derived).ToLowerInvariant();
        var stored = $"scrypt:16384:8:1:32:{saltHex}:{derivedHex}";

        _service.VerifyPassword(password, stored).Should().BeTrue();
        _service.VerifyPassword("wrong", stored).Should().BeFalse();
    }

    [Test]
    public void VerifyPassword_MalformedScrypt_ReturnsFalse()
    {
        _service.VerifyPassword("any", "scrypt:16384:8:1:32:notvalidhex:notvalidhex").Should().BeFalse();
        _service.VerifyPassword("any", "scrypt:bad").Should().BeFalse();
    }

    [Test]
    public void VerifyPassword_EmptyHash_ReturnsFalse()
    {
        _service.VerifyPassword("any", "").Should().BeFalse();
        _service.VerifyPassword("any", null!).Should().BeFalse();
    }

    [Test]
    public void NeedsRehash_FlagsScryptHashes()
    {
        var argon = _service.HashPassword("Test1234!");
        _service.NeedsRehash(argon).Should().BeFalse();
        _service.NeedsRehash("scrypt:16384:8:1:32:aa:bb").Should().BeTrue();
        _service.NeedsRehash("").Should().BeTrue();
    }

    [Test]
    public void DummyHash_IsValidArgon2_AndNeverVerifies()
    {
        var dummy = _service.DummyHash;
        dummy.Should().StartWith("$argon2id$");
        _service.VerifyPassword("anything", dummy).Should().BeFalse();
    }
}
