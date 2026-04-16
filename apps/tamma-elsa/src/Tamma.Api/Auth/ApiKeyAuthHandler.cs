using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Tamma.Data.Repositories;

namespace Tamma.Api.Auth;

public class ApiKeyAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IServiceProvider serviceProvider)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var authHeader))
            return AuthenticateResult.NoResult();

        var headerValue = authHeader.ToString();
        if (!headerValue.StartsWith("ApiKey ", StringComparison.OrdinalIgnoreCase))
            return AuthenticateResult.NoResult();

        var rawKey = headerValue["ApiKey ".Length..].Trim();
        if (string.IsNullOrEmpty(rawKey))
            return AuthenticateResult.Fail("Empty API key");

        var keyHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawKey))).ToLowerInvariant();

        using var scope = serviceProvider.CreateScope();
        var apiKeyRepo = scope.ServiceProvider.GetRequiredService<IApiKeyRepository>();
        var apiKey = await apiKeyRepo.GetByHashAsync(keyHash);

        if (apiKey is null)
            return AuthenticateResult.Fail("Invalid API key");

        if (apiKey.RevokedAt is not null)
            return AuthenticateResult.Fail("API key has been revoked");

        // Update last used
        await apiKeyRepo.UpdateLastUsedAsync(apiKey.Id);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, apiKey.OwnerId),
            new("scope", apiKey.Scope),
            new("key_id", apiKey.Id.ToString()),
        };

        if (apiKey.TenantId.HasValue)
            claims.Add(new Claim("tid", apiKey.TenantId.Value.ToString()));

        foreach (var perm in apiKey.Permissions)
            claims.Add(new Claim("permission", perm));

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);
        return AuthenticateResult.Success(ticket);
    }
}
