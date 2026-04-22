using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Auth;

namespace Tamma.Api.Tests.Auth;

[TestFixture]
public class Base32Tests
{
    [Test]
    public void Encode_EmptyInput_ReturnsEmpty()
    {
        Base32.Encode(ReadOnlySpan<byte>.Empty).Should().Be(string.Empty);
    }

    [Test]
    public void Encode_Decode_RoundTripsAll16ByteUuids()
    {
        for (var i = 0; i < 100; i++)
        {
            var guid = Guid.NewGuid();
            var bytes = guid.ToByteArray();
            var encoded = Base32.Encode(bytes);

            // 16 bytes → 26 base32 chars (no padding).
            encoded.Should().HaveLength(26);
            encoded.Should().MatchRegex("^[A-Z2-7]+$",
                "RFC4648 base32 alphabet, uppercase, no padding");

            var decoded = Base32.TryDecode(encoded);
            decoded.Should().NotBeNull();
            decoded.Should().BeEquivalentTo(bytes);
        }
    }

    [Test]
    public void Decode_IsCaseInsensitive()
    {
        var bytes = Guid.NewGuid().ToByteArray();
        var encoded = Base32.Encode(bytes);

        var lowercase = Base32.TryDecode(encoded.ToLowerInvariant());
        lowercase.Should().BeEquivalentTo(bytes,
            "operators copy keys from terminals — must accept lowercase");
    }

    [Test]
    public void Decode_RejectsInvalidCharacters()
    {
        // '1', '8', '9', '0' are NOT in the RFC4648 base32 alphabet.
        Base32.TryDecode("AAAA1AAA").Should().BeNull();
        Base32.TryDecode("AAAA8AAA").Should().BeNull();
        Base32.TryDecode("AAAA0AAA").Should().BeNull();
        Base32.TryDecode("===PADDING").Should().BeNull();
    }

    [Test]
    public void Decode_RejectsNonZeroPadBits()
    {
        // 'B' (=1) at the very end leaves a non-zero remainder — must reject.
        Base32.TryDecode("AAB").Should().BeNull();
    }
}
