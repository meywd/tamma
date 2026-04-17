using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Tamma.Api.Extensions;
using Tamma.Api.Services.Email;
using Tamma.Data;

namespace Tamma.Api.Tests.Email;

/// <summary>
/// Story 18-6: the password-reset request endpoint must send an email to
/// registered users while maintaining anti-enumeration behaviour
/// (identical 200 response + NO email) for unknown addresses.
/// </summary>
[TestFixture]
public class PasswordResetEmailIntegrationTests
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
                services.AddSingleton<IEmailService>(_inbox);
            });
        }).CreateClient();
    }

    [Test]
    public async Task PasswordResetRequest_ExistingEmail_SendsResetEmail()
    {
        using var client = CreateClient();

        var register = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email = "pwreset@example.com",
            password = "Sup3rSecure!",
        });
        register.StatusCode.Should().Be(HttpStatusCode.Created);
        _inbox.SentMessages.Clear();

        var response = await client.PostAsJsonAsync("/api/v1/auth/password-reset/request", new
        {
            email = "pwreset@example.com"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _inbox.SentMessages.Should().ContainSingle();
        var email = _inbox.SentMessages[0];
        email.To.Should().Be("pwreset@example.com");
        email.Subject.ToLowerInvariant().Should().Contain("reset");
        email.Html.Should().Contain($"{DashboardUrl}/reset-password?token=");
        email.Text.Should().Contain($"{DashboardUrl}/reset-password?token=");
    }

    [Test]
    public async Task PasswordResetRequest_PersistsHashedTokenInRepository()
    {
        using var client = CreateClient();

        await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email = "pwpersist@example.com",
            password = "Sup3rSecure!",
        });
        _inbox.SentMessages.Clear();

        var response = await client.PostAsJsonAsync("/api/v1/auth/password-reset/request", new
        {
            email = "pwpersist@example.com"
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Assert hashed token persisted with a future expiry.
        using var scope = ApiTestFixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TammaDbContext>();
        var tokens = await db.PasswordResetTokens.ToListAsync();

        tokens.Should().ContainSingle();
        tokens[0].TokenHash.Should().NotBeNullOrWhiteSpace();
        tokens[0].TokenHash.Length.Should().BeGreaterThan(32,
            "we persist a SHA-256 hex hash, not the raw token");
        tokens[0].ExpiresAt.Should().BeAfter(DateTime.UtcNow);
        tokens[0].ConsumedAt.Should().BeNull();
    }

    [Test]
    public async Task PasswordResetRequest_UnknownEmail_AntiEnumeration()
    {
        using var client = CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/password-reset/request", new
        {
            email = "nobody@example.com"
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        _inbox.SentMessages.Should().BeEmpty(
            "unknown emails must not receive reset messages (anti-enumeration)");

        using var scope = ApiTestFixture.Factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TammaDbContext>();
        var tokens = await db.PasswordResetTokens.ToListAsync();
        tokens.Should().BeEmpty("no reset token should be persisted for unknown emails");
    }

    [Test]
    public async Task PasswordResetRequest_MissingEmail_Returns400()
    {
        using var client = CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/auth/password-reset/request", new
        {
            email = ""
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        _inbox.SentMessages.Should().BeEmpty();
    }
}
