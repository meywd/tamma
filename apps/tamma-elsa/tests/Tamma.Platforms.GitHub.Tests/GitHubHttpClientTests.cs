using FluentAssertions;
using NUnit.Framework;

namespace Tamma.Platforms.GitHub.Tests;

/// <summary>
/// Epic 31 P1 stage 2 — GHES-aware endpoint selection + error-code
/// classification pins for the driver-internal HTTP layer.
/// </summary>
[TestFixture]
public sealed class GitHubHttpClientTests
{
    [TestCase("https://api.github.com", "https://api.github.com/graphql")]
    [TestCase("https://api.github.com/", "https://api.github.com/graphql")]
    [TestCase("https://github.acme.corp/api/v3", "https://github.acme.corp/api/graphql")]
    [TestCase("https://github.acme.corp/api/v3/", "https://github.acme.corp/api/graphql")]
    [TestCase("https://github.acme.corp", "https://github.acme.corp/api/graphql")]
    public void ComputeGraphQlUrl_selects_cloud_vs_ghes_shape(string baseUrl, string expected)
    {
        GitHubHttpClient.ComputeGraphQlUrl(baseUrl).Should().Be(expected);
    }

    [TestCase(405, null, "not_mergeable")]
    [TestCase(409, "Merge conflict", "merge_conflict")]
    [TestCase(409, "Head branch was modified. Review and try the merge again.", "merge_conflict")]
    [TestCase(409, "Something else entirely", "conflict")]
    [TestCase(422, "Reference already exists", "already_exists")]
    [TestCase(422, "Validation Failed", "validation_failed")]
    [TestCase(418, "teapot", "418")]
    public void ClassifyClientError_pins_coarse_codes(int status, string? message, string expected)
    {
        GitHubErrorMapper.ClassifyClientError(status, message).Should().Be(expected);
    }

    [Test]
    public void Constructor_requires_minter_in_app_mode()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        Action act = () => new GitHubHttpClient(
            new HttpClient(new FakeHttpMessageHandler()),
            "https://api.github.com",
            new GitHubAuth.App(1, rsa.ExportRSAPrivateKeyPem(), 2),
            minter: null);

        act.Should().Throw<ArgumentException>().WithMessage("*minter*");
    }
}
