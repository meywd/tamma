using System.Security.Cryptography;
using FluentAssertions;
using NUnit.Framework;

namespace Tamma.Platforms.GitHub.Tests;

/// <summary>
/// Epic 31 P1 stage 2 — credential parse-shape tests for
/// <see cref="GitHubAuth.Parse"/> (the wire format the factory
/// documents for PlatformConnectService-stored plaintext).
/// </summary>
[TestFixture]
public sealed class GitHubAuthTests
{
    private static string TestRsaPem()
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportRSAPrivateKeyPem();
    }

    [Test]
    public void Raw_token_string_parses_as_Pat()
    {
        var auth = GitHubAuth.Parse("ghp_abc123");
        auth.Should().BeOfType<GitHubAuth.Pat>().Which.Token.Should().Be("ghp_abc123");
    }

    [Test]
    public void Json_pat_wrapper_parses_as_Pat()
    {
        var auth = GitHubAuth.Parse("""{ "kind": "pat", "token": "ghp_abc" }""");
        auth.Should().BeOfType<GitHubAuth.Pat>().Which.Token.Should().Be("ghp_abc");
    }

    [Test]
    public void Json_app_shape_parses_as_App_with_installation_id()
    {
        var pem = TestRsaPem();
        var auth = GitHubAuth.Parse(System.Text.Json.JsonSerializer.Serialize(new
        {
            kind = "app",
            appId = 123,
            privateKeyPem = pem,
            installationId = 456,
        }));

        var app = auth.Should().BeOfType<GitHubAuth.App>().Subject;
        app.AppId.Should().Be(123);
        app.PrivateKeyPem.Should().Be(pem);
        app.InstallationId.Should().Be(456);
    }

    [Test]
    public void Json_app_shape_without_installation_id_defers_to_the_row()
    {
        var auth = GitHubAuth.Parse(System.Text.Json.JsonSerializer.Serialize(new
        {
            kind = "app",
            appId = 123,
            privateKeyPem = TestRsaPem(),
        }));

        auth.Should().BeOfType<GitHubAuth.App>().Which.InstallationId.Should().BeNull();
    }

    [Test]
    public void App_shape_accepts_string_numeric_ids()
    {
        var auth = GitHubAuth.Parse(System.Text.Json.JsonSerializer.Serialize(new
        {
            kind = "app",
            appId = "123",
            privateKeyPem = TestRsaPem(),
            installationId = "456",
        }));

        var app = auth.Should().BeOfType<GitHubAuth.App>().Subject;
        app.AppId.Should().Be(123);
        app.InstallationId.Should().Be(456);
    }

    [Test]
    public void Unrecognized_kind_throws()
    {
        Action act = () => GitHubAuth.Parse("""{ "kind": "oauth2" }""");
        act.Should().Throw<ArgumentException>().WithMessage("*unrecognized*");
    }

    [Test]
    public void Malformed_json_throws()
    {
        Action act = () => GitHubAuth.Parse("{ nope");
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void App_shape_missing_key_material_throws()
    {
        Action act = () => GitHubAuth.Parse("""{ "kind": "app", "appId": 5 }""");
        act.Should().Throw<ArgumentException>().WithMessage("*privateKeyPem*");
    }

    [Test]
    public void ToString_never_leaks_secrets()
    {
        GitHubAuth.Parse("ghp_secret").ToString().Should().NotContain("secret");
        GitHubAuth.Parse(System.Text.Json.JsonSerializer.Serialize(new
        {
            kind = "app",
            appId = 1,
            privateKeyPem = TestRsaPem(),
            installationId = 2,
        })).ToString().Should().NotContain("PRIVATE KEY");
    }
}
