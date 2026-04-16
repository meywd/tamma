using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Tamma.Api.Tests.Infrastructure;

/// <summary>
/// Stub authentication handler used by integration tests. Attaches a stable
/// <see cref="ClaimTypes.NameIdentifier"/> claim to every request using the
/// fixture's <see cref="ApiTestFixture.TestUserId"/> so prompt-endpoint tests
/// can exercise per-user override semantics without a real JWT pipeline.
/// </summary>
public sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string Scheme = "Test";

    private readonly ApiTestFixture _fixture;

    public TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ApiTestFixture fixture)
        : base(options, logger, encoder)
    {
        _fixture = fixture;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, _fixture.TestUserId.ToString()),
            new Claim(ClaimTypes.Name, "test-user"),
        };
        var identity = new ClaimsIdentity(claims, Scheme);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
