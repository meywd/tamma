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
}
