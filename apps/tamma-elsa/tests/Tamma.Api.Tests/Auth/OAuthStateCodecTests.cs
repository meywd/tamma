using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Auth;

namespace Tamma.Api.Tests.Auth;

[TestFixture]
public class OAuthStateCodecTests
{
    [Test]
    public void RoundTrip_PreservesAllFields()
    {
        var p = new OAuthStatePayload(
            Rd: "https://app.tamma.dev/dashboard",
            Invite: "abc123",
            Csrf: "deadbeef");
        var encoded = OAuthStateCodec.Encode(p);
        var decoded = OAuthStateCodec.TryDecode(encoded);

        decoded.Should().NotBeNull();
        decoded!.Rd.Should().Be(p.Rd);
        decoded.Invite.Should().Be(p.Invite);
        decoded.Csrf.Should().Be(p.Csrf);
    }

    [Test]
    public void Decode_Garbage_ReturnsNull()
    {
        OAuthStateCodec.TryDecode("not-base64").Should().BeNull();
        OAuthStateCodec.TryDecode("").Should().BeNull();
    }

    [Test]
    public void OmittedFields_AreNull()
    {
        var encoded = OAuthStateCodec.Encode(new OAuthStatePayload(null, null, "csrf"));
        var decoded = OAuthStateCodec.TryDecode(encoded);
        decoded!.Rd.Should().BeNull();
        decoded.Invite.Should().BeNull();
        decoded.Csrf.Should().Be("csrf");
    }
}

[TestFixture]
public class RedirectUrlSanitizerTests
{
    [Test]
    public void RelativePath_Preserved()
    {
        RedirectUrlSanitizer.Sanitize("/dashboard", "tamma.dev").Should().Be("/dashboard");
    }

    [Test]
    public void AllowedDomain_Preserved()
    {
        var r = RedirectUrlSanitizer.Sanitize("https://app.tamma.dev/foo", "tamma.dev");
        r.Should().NotBeNull();
        r!.Should().StartWith("https://app.tamma.dev/foo");
    }

    [Test]
    public void OffDomain_Rejected()
    {
        RedirectUrlSanitizer.Sanitize("https://evil.com/", "tamma.dev").Should().BeNull();
    }

    [Test]
    public void HttpScheme_Rejected()
    {
        RedirectUrlSanitizer.Sanitize("http://app.tamma.dev/", "tamma.dev").Should().BeNull();
    }

    [Test]
    public void ProtocolRelative_Rejected()
    {
        RedirectUrlSanitizer.Sanitize("//evil.com/path", "tamma.dev").Should().BeNull();
    }

    [Test]
    public void NullOrEmpty_Rejected()
    {
        RedirectUrlSanitizer.Sanitize(null, "tamma.dev").Should().BeNull();
        RedirectUrlSanitizer.Sanitize("", "tamma.dev").Should().BeNull();
    }
}
