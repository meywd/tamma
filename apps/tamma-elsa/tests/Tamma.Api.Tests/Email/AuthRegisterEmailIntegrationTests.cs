using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Extensions;
using Tamma.Api.Services.Email;

namespace Tamma.Api.Tests.Email;

/// <summary>
/// Story 18-1 end-to-end: registering a user must trigger a verification
/// email containing the token-bearing link. Uses the real HTTP pipeline +
/// Postgres, but swaps in an <see cref="InMemoryEmailService"/> as a
/// singleton so the test can introspect what was sent.
/// </summary>
[TestFixture]
public class AuthRegisterEmailIntegrationTests
{
    private const string DashboardUrl = "https://dash.test.tamma.dev";
    private InMemoryEmailService _inbox = null!;

    [SetUp]
    public async Task SetUp()
    {
        await ApiTestFixture.ResetDatabaseAsync();
        _inbox = new InMemoryEmailService();
    }

    private HttpClient CreateClient()
    {
        return ApiTestFixture.Factory.WithWebHostBuilder(b =>
        {
            b.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Dashboard:Url"] = DashboardUrl,
                });
            });
            b.ConfigureServices(services =>
            {
                services.AddEmailServices();
                // Replace the in-memory default with OUR instance so assertions
                // can inspect the sent-mail queue.
                services.AddSingleton<IEmailService>(_inbox);
            });
        }).CreateClient();
    }

    [Test]
    public async Task Register_SendsVerificationEmailToNewUser()
    {
        using var client = CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email = "newuser@example.com",
            password = "Sup3rSecure!",
            displayName = "New User"
        });

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        _inbox.SentMessages.Should().ContainSingle(
            "registration must trigger exactly one verification email");
        var email = _inbox.SentMessages[0];
        email.To.Should().Be("newuser@example.com");
        email.Subject.ToLowerInvariant().Should().Contain("verify");
        // Link uses configured Dashboard:Url and carries a token query param.
        email.Html.Should().Contain($"{DashboardUrl}/verify?token=");
        email.Text.Should().Contain($"{DashboardUrl}/verify?token=");
    }

    [Test]
    public async Task Register_DuplicateEmail_DoesNotSendSecondEmail()
    {
        using var client = CreateClient();

        var first = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email = "dupe@example.com",
            password = "Sup3rSecure!",
        });
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var second = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email = "dupe@example.com",
            password = "Sup3rSecure!",
        });
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);

        _inbox.SentMessages.Should().ContainSingle(
            "only the first, successful registration should email the user");
    }

    [Test]
    public async Task ResendVerification_ExistingUser_SendsEmail()
    {
        using var client = CreateClient();

        var register = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email = "resend@example.com",
            password = "Sup3rSecure!",
        });
        register.StatusCode.Should().Be(HttpStatusCode.Created);
        _inbox.SentMessages.Should().HaveCount(1);

        var response = await client.PostAsJsonAsync("/api/v1/auth/resend-verification", new
        {
            email = "resend@example.com"
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        _inbox.SentMessages.Should().HaveCount(2,
            "resend should queue a fresh verification email");
        _inbox.SentMessages[1].To.Should().Be("resend@example.com");
        _inbox.SentMessages[1].Subject.ToLowerInvariant().Should().Contain("verify");
    }

    [Test]
    public async Task ResendVerification_UnknownEmail_AntiEnumeration()
    {
        using var client = CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/resend-verification", new
        {
            email = "ghost@example.com"
        });

        // Anti-enumeration: 200 whether or not the email exists, and NO email
        // is sent to unknown addresses.
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _inbox.SentMessages.Should().BeEmpty(
            "unknown emails must not receive verification messages");
    }
}
