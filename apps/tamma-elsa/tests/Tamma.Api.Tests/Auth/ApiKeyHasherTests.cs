using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Auth;

namespace Tamma.Api.Tests.Auth;

[TestFixture]
public class ApiKeyHasherTests
{
    [Test]
    public void NewKey_StartsWithTammaSkPrefix()
    {
        var k = ApiKeyHasher.NewKey();
        k.Should().StartWith("tamma_sk_");
    }

    [Test]
    public void NewKey_UsesBase64UrlCharset_NoPlusSlashEquals()
    {
        for (int i = 0; i < 50; i++)
        {
            var k = ApiKeyHasher.NewKey();
            k.Should().NotContain("+");
            k.Should().NotContain("/");
            k.Should().NotContain("=");
        }
    }

    [Test]
    public void Hash_IsDeterministic()
    {
        var k = ApiKeyHasher.NewKey();
        ApiKeyHasher.Hash(k).Should().Be(ApiKeyHasher.Hash(k));
    }

    [Test]
    public void Hash_Is64HexChars()
    {
        var k = ApiKeyHasher.NewKey();
        var h = ApiKeyHasher.Hash(k);
        h.Should().HaveLength(64);
        h.Should().MatchRegex("^[0-9a-f]+$");
    }

    [Test]
    public void Prefix_IsFirst12Chars()
    {
        var k = ApiKeyHasher.NewKey();
        ApiKeyHasher.Prefix(k).Should().HaveLength(12);
        ApiKeyHasher.Prefix(k).Should().Be(k[..12]);
    }

    [Test]
    public void LegacyScryptHash_DiffersFromSha256()
    {
        var k = ApiKeyHasher.NewKey();
        ApiKeyHasher.LegacyScryptHash(k).Should().NotBe(ApiKeyHasher.Hash(k));
    }

    // ── Story 28-7 deferred-item: Argon2id dual-verify ─────────────────

    [Test]
    public void HashArgon2_StartsWithArgon2Marker()
    {
        var k = ApiKeyHasher.NewKey();
        var h = ApiKeyHasher.HashArgon2(k);
        h.Should().StartWith("argon2id$");
    }

    [Test]
    public void HashArgon2_IsSaltedAndNonDeterministic()
    {
        var k = ApiKeyHasher.NewKey();
        // Per-key salt means two calls on the same raw key produce different
        // hashes (but both verify).
        var h1 = ApiKeyHasher.HashArgon2(k);
        var h2 = ApiKeyHasher.HashArgon2(k);
        h1.Should().NotBe(h2);
    }

    [Test]
    public void Verify_ArgonHash_Succeeds()
    {
        var k = ApiKeyHasher.NewKey();
        var h = ApiKeyHasher.HashArgon2(k);
        ApiKeyHasher.Verify(k, h).Should().BeTrue();
    }

    [Test]
    public void Verify_ArgonHash_RejectsMismatch()
    {
        var k1 = ApiKeyHasher.NewKey();
        var k2 = ApiKeyHasher.NewKey();
        var h = ApiKeyHasher.HashArgon2(k1);
        ApiKeyHasher.Verify(k2, h).Should().BeFalse();
    }

    [Test]
    public void Verify_Sha256Hash_Succeeds_LegacyFallback()
    {
        var k = ApiKeyHasher.NewKey();
        var h = ApiKeyHasher.Hash(k);
        ApiKeyHasher.Verify(k, h).Should().BeTrue();
    }

    [Test]
    public void Verify_ScryptHash_Succeeds_LegacyFallback()
    {
        var k = ApiKeyHasher.NewKey();
        var h = ApiKeyHasher.LegacyScryptHash(k);
        ApiKeyHasher.Verify(k, h).Should().BeTrue();
    }

    [Test]
    public void Verify_MismatchedSha_RejectsWithoutFalling()
    {
        var k1 = ApiKeyHasher.NewKey();
        var k2 = ApiKeyHasher.NewKey();
        ApiKeyHasher.Verify(k2, ApiKeyHasher.Hash(k1)).Should().BeFalse();
    }

    [Test]
    public void Verify_GarbageHash_ReturnsFalse()
    {
        var k = ApiKeyHasher.NewKey();
        ApiKeyHasher.Verify(k, "not-a-hash").Should().BeFalse();
        ApiKeyHasher.Verify(k, string.Empty).Should().BeFalse();
        ApiKeyHasher.Verify(k, "argon2id$invalid").Should().BeFalse();
    }

    [Test]
    public void NeedsRehash_ArgonRow_False()
    {
        var k = ApiKeyHasher.NewKey();
        ApiKeyHasher.NeedsRehash(ApiKeyHasher.HashArgon2(k)).Should().BeFalse();
    }

    [Test]
    public void NeedsRehash_Sha256Row_True()
    {
        var k = ApiKeyHasher.NewKey();
        ApiKeyHasher.NeedsRehash(ApiKeyHasher.Hash(k)).Should().BeTrue();
    }

    [Test]
    public void NeedsRehash_ScryptRow_True()
    {
        var k = ApiKeyHasher.NewKey();
        ApiKeyHasher.NeedsRehash(ApiKeyHasher.LegacyScryptHash(k)).Should().BeTrue();
    }

    [Test]
    public void NeedsRehash_EmptyHash_False()
    {
        ApiKeyHasher.NeedsRehash(string.Empty).Should().BeFalse();
    }

    [Test]
    public void NeedsRehash_TwoArg_ArgonRow_False_EvenOnMismatch()
    {
        // Argon2 row never needs rehash — the two-arg overload preserves
        // backward compat with the pre-Story-28-7 call sites.
        var k = ApiKeyHasher.NewKey();
        var argon = ApiKeyHasher.HashArgon2(k);
        ApiKeyHasher.NeedsRehash(argon, "irrelevant-sha").Should().BeFalse();
    }
}
