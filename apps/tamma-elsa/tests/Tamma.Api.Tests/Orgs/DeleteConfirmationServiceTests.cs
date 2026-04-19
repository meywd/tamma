using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Tamma.Api.Auth;

namespace Tamma.Api.Tests.Orgs;

[TestFixture]
public class DeleteConfirmationServiceTests
{
    private DeleteConfirmationService _service = null!;
    private static readonly Guid Tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid User = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [SetUp]
    public void Setup()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:Secret"] = "test-secret-at-least-32-characters-long-x",
            })
            .Build();
        _service = new DeleteConfirmationService(config);
    }

    [Test]
    public void Generate_ReturnsTokenAndExpiry()
    {
        var conf = _service.Generate(Tenant, User);
        conf.Token.Should().NotBeNullOrEmpty();
        conf.Token.Should().Contain(".");
        conf.ExpiresAt.Should().BeAfter(DateTime.UtcNow);
        conf.ExpiresAt.Should().BeBefore(DateTime.UtcNow.AddMinutes(11));
    }

    [Test]
    public void Verify_AcceptsFreshlyGeneratedToken()
    {
        var conf = _service.Generate(Tenant, User);
        _service.Verify(conf.Token, Tenant, User).Should().BeTrue();
    }

    [Test]
    public void Verify_RejectsTokenForDifferentTenant()
    {
        var conf = _service.Generate(Tenant, User);
        var otherTenant = Guid.Parse("33333333-3333-3333-3333-333333333333");
        _service.Verify(conf.Token, otherTenant, User).Should().BeFalse();
    }

    [Test]
    public void Verify_RejectsTokenForDifferentUser()
    {
        var conf = _service.Generate(Tenant, User);
        var otherUser = Guid.Parse("44444444-4444-4444-4444-444444444444");
        _service.Verify(conf.Token, Tenant, otherUser).Should().BeFalse();
    }

    [Test]
    public void Verify_RejectsMalformedToken()
    {
        _service.Verify("not-a-token", Tenant, User).Should().BeFalse();
        _service.Verify("", Tenant, User).Should().BeFalse();
        _service.Verify(null, Tenant, User).Should().BeFalse();
        _service.Verify(".", Tenant, User).Should().BeFalse();
    }

    [Test]
    public void Verify_RejectsTokenWithFlippedHmac()
    {
        var conf = _service.Generate(Tenant, User);
        var dot = conf.Token.IndexOf('.');
        // Replace last hex char to simulate corruption.
        var corrupted = conf.Token[..^1] + (conf.Token[^1] == 'a' ? 'b' : 'a');
        _service.Verify(corrupted, Tenant, User).Should().BeFalse();
    }

    [Test]
    public void Verify_RejectsExpiredToken()
    {
        // Build an "old" token by hand: same payload, issuedAt far in the past.
        var oldIssuedAt = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds();
        var payload = $"{Tenant:D}:{User:D}:{oldIssuedAt}";
        var hmac = System.Convert.ToHexString(
            System.Security.Cryptography.HMACSHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes("test-secret-at-least-32-characters-long-x"),
                System.Text.Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        var token = $"{oldIssuedAt}.{hmac}";

        _service.Verify(token, Tenant, User).Should().BeFalse();
    }
}
