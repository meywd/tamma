using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Tamma.Api.Services.OAuth;

/// <summary>
/// Wraps the two GitHub HTTP calls needed by the OAuth callback:
/// <list type="number">
///   <item>POST <c>https://github.com/login/oauth/access_token</c> — exchange
///         the authorization code for a user access token.</item>
///   <item>GET <c>https://api.github.com/user</c> — fetch the authenticated
///         user's profile (id, login, email, name).</item>
/// </list>
/// </summary>
public interface IGitHubOAuthService
{
    Task<string?> ExchangeCodeForTokenAsync(string code, CancellationToken ct = default);
    Task<GitHubUserProfile?> GetUserProfileAsync(string accessToken, CancellationToken ct = default);
}

public sealed record GitHubUserProfile(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("login")] string Login,
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("avatar_url")] string? AvatarUrl);

public class GitHubOAuthService(
    IHttpClientFactory httpClientFactory,
    IConfiguration config,
    ILogger<GitHubOAuthService> logger) : IGitHubOAuthService
{
    public async Task<string?> ExchangeCodeForTokenAsync(string code, CancellationToken ct = default)
    {
        var clientId = config["GitHub:ClientId"];
        var clientSecret = config["GitHub:ClientSecret"];
        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
        {
            logger.LogError("GitHub OAuth is not fully configured (ClientId/ClientSecret missing)");
            return null;
        }

        var client = httpClientFactory.CreateClient("github-oauth");
        try
        {
            var response = await client.PostAsJsonAsync(
                "https://github.com/login/oauth/access_token",
                new { client_id = clientId, client_secret = clientSecret, code },
                ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("GitHub token exchange returned {Status}", response.StatusCode);
                return null;
            }
            var payload = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken: ct);
            if (payload?.AccessToken is null)
            {
                logger.LogWarning("GitHub token exchange succeeded but returned no access_token");
                return null;
            }
            return payload.AccessToken;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GitHub token exchange threw");
            return null;
        }
    }

    public async Task<GitHubUserProfile?> GetUserProfileAsync(string accessToken, CancellationToken ct = default)
    {
        var client = httpClientFactory.CreateClient("github-oauth");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
            req.Headers.Add("Accept", "application/vnd.github+json");
            using var response = await client.SendAsync(req, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("GitHub /user returned {Status}", response.StatusCode);
                return null;
            }
            return await response.Content.ReadFromJsonAsync<GitHubUserProfile>(cancellationToken: ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GitHub user fetch threw");
            return null;
        }
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("error")] string? Error);
}
