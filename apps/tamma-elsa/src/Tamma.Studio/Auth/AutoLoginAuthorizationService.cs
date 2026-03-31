using Elsa.Studio.Login.Contracts;
using Elsa.Studio.Login.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using System.Net.Http.Json;

namespace Tamma.Studio.Auth;

/// <summary>
/// Replaces the default ELSA Identity authorization service that redirects to /login.
/// Instead, auto-logs in with the configured admin credentials and stores the tokens,
/// so the ELSA Studio login page never appears.
///
/// This is safe because nginx already gates access to elsa.tamma.dev — only
/// authenticated admin/owner users with a valid tamma_session JWT can reach the Studio.
/// </summary>
public class AutoLoginAuthorizationService : IAuthorizationService
{
    private readonly IJwtAccessor _jwtAccessor;
    private readonly NavigationManager _navigationManager;
    private readonly AuthenticationStateProvider _authStateProvider;
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private static bool _loginInProgress;

    public AutoLoginAuthorizationService(
        IJwtAccessor jwtAccessor,
        NavigationManager navigationManager,
        AuthenticationStateProvider authStateProvider,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration)
    {
        _jwtAccessor = jwtAccessor;
        _navigationManager = navigationManager;
        _authStateProvider = authStateProvider;
        _httpClient = httpClientFactory.CreateClient("ElsaAutoLogin");
        _configuration = configuration;
    }

    public async Task RedirectToAuthorizationServer()
    {
        // Guard against re-entrant calls (Blazor can trigger this multiple times
        // during initial auth state resolution)
        if (_loginInProgress) return;
        _loginInProgress = true;

        try
        {
            var username = _configuration["AutoLogin:Username"] ?? "admin";
            var password = _configuration["AutoLogin:Password"] ?? "password";

            var response = await _httpClient.PostAsJsonAsync(
                "identity/login",
                new { username, password });

            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content
                    .ReadFromJsonAsync<LoginResult>();

                if (result is { IsAuthenticated: true, AccessToken: not null, RefreshToken: not null })
                {
                    await _jwtAccessor.WriteTokenAsync("AccessToken", result.AccessToken);
                    await _jwtAccessor.WriteTokenAsync("RefreshToken", result.RefreshToken);

                    if (_authStateProvider is AccessTokenAuthenticationStateProvider provider)
                    {
                        provider.NotifyAuthenticationStateChanged();
                    }

                    _navigationManager.NavigateTo("", forceLoad: true);
                    return;
                }
            }

            // Fallback: if auto-login fails, go to the ELSA login page
            _navigationManager.NavigateTo("login", forceLoad: true);
        }
        catch
        {
            // Network error or ELSA server down — fall back to login page
            _navigationManager.NavigateTo("login", forceLoad: true);
        }
        finally
        {
            _loginInProgress = false;
        }
    }

    public Task ReceiveAuthorizationCode(string code, string? state,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    private record LoginResult(
        bool IsAuthenticated,
        string? AccessToken,
        string? RefreshToken);
}
