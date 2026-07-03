using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Audit;

namespace Tamma.Core.Tests.Audit;

/// <summary>Story 37-2 (AC2) — hasher determinism + avalanche + genesis.</summary>
[TestFixture]
public class AuditChainHasherTests
{
    private static readonly string Prev = AuditChainGenesis.HashHex;

    [Test]
    public void Compose_Returns_64_Lowercase_Hex_Chars()
    {
        var hex = AuditChainHasher.ComposeHex(Prev, new byte[] { 1, 2, 3 });
        hex.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Test]
    public void Compose_Is_Deterministic()
    {
        var a = AuditChainHasher.ComposeHex(Prev, Encoding.UTF8.GetBytes("canon"));
        var b = AuditChainHasher.ComposeHex(Prev, Encoding.UTF8.GetBytes("canon"));
        a.Should().Be(b);
    }

    [Test]
    public void Flipping_One_Bit_Of_Canonical_Changes_The_Hash()
    {
        var canon = new byte[] { 0x10, 0x20, 0x30 };
        var flipped = new byte[] { 0x10, 0x21, 0x30 };
        AuditChainHasher.ComposeHex(Prev, canon)
            .Should().NotBe(AuditChainHasher.ComposeHex(Prev, flipped));
    }

    [Test]
    public void Changing_Prev_Hash_Changes_The_Result()
    {
        var otherPrev = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("x"))).ToLowerInvariant();
        var canon = Encoding.UTF8.GetBytes("canon");
        AuditChainHasher.ComposeHex(Prev, canon)
            .Should().NotBe(AuditChainHasher.ComposeHex(otherPrev, canon));
    }

    [Test]
    public void Genesis_Is_Reproducible_From_Documented_Preimage()
    {
        var expected = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(AuditChainGenesis.Preimage))).ToLowerInvariant();
        AuditChainGenesis.HashHex.Should().Be(expected);
        AuditChainGenesis.HashHex.Should().HaveLength(64);
    }

    [Test]
    public void Malformed_Prev_Hash_Throws()
    {
        var act = () => AuditChainHasher.ComposeHex("too-short", new byte[] { 1 });
        act.Should().Throw<FormatException>();
    }
}
