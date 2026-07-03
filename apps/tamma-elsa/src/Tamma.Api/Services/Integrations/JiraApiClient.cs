using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Tamma.Core.Interfaces;

namespace Tamma.Api.Services.Integrations;

/// <summary>
/// Default <see cref="IJiraApiClient"/> — performs the JIRA REST v3 calls with a
/// per-request <see cref="JiraCredential"/> (basic auth = <c>email:apiToken</c>,
/// base URL from the credential). Mirrors the HTTP shape of the legacy
/// <c>JiraIntegrationService</c> but with the credential threaded in per call
/// instead of read from global config, so a per-tenant BYOK bundle drives the
/// request. Never logs the token; a 404 maps to a typed "not found" failure.
///
/// <para><b>SSRF hardening.</b> The tenant-supplied <c>baseUrl</c> is re-validated
/// at USE time via <see cref="JiraBaseUrlGuard"/> (https-only + private-range
/// rejection + optional <c>Jira:AllowedHostSuffixes</c> allowlist), the
/// <c>ticketId</c> is validated as a safe JIRA key/id (no <c>../</c> path
/// traversal), the request rides the named <c>"jira"</c> client whose handler does
/// NOT auto-follow redirects (a 3xx is refused, not chased into a private address),
/// and that handler's connect callback re-checks the resolved address at connect
/// time (anti-rebinding). Write-time validation on the credential endpoint is the
/// first layer; this is defense in depth.</para>
/// </summary>
public sealed class JiraApiClient : IJiraApiClient
{
    /// <summary>The named client Program.cs configures with
    /// <c>AllowAutoRedirect=false</c> + <see cref="JiraBaseUrlGuard.SafeConnectAsync"/>.</summary>
    public const string HttpClientName = "jira";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<JiraApiClient> _logger;

    public JiraApiClient(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<JiraApiClient> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IntegrationResult<JiraTicket?>> GetTicketAsync(
        JiraCredential credential, string ticketId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credential);

        var guard = await GuardAsync(credential, ticketId, ct).ConfigureAwait(false);
        if (guard is not null)
        {
            return IntegrationResult<JiraTicket?>.Fail(guard);
        }

        try
        {
            var httpClient = _httpClientFactory.CreateClient(HttpClientName);

            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"{TrimBase(credential.BaseUrl)}/rest/api/3/issue/{ticketId}");
            Authorize(request, credential);
            using var response = await httpClient.SendAsync(request, ct).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return IntegrationResult<JiraTicket?>.Fail("not found");
            }
            if (IsRedirect(response))
            {
                return IntegrationResult<JiraTicket?>.Fail("refused redirect");
            }
            response.EnsureSuccessStatusCode();

            var data = await response.Content.ReadFromJsonAsync<JsonElement>(ct).ConfigureAwait(false);
            var fields = data.GetProperty("fields");

            var ticket = new JiraTicket
            {
                Id = data.GetProperty("id").GetString() ?? string.Empty,
                Key = data.GetProperty("key").GetString() ?? ticketId,
                Summary = fields.GetProperty("summary").GetString() ?? string.Empty,
                Description = fields.TryGetProperty("description", out var desc) && desc.ValueKind != JsonValueKind.Null
                    ? desc.ToString() : string.Empty,
                Status = fields.GetProperty("status").GetProperty("name").GetString() ?? string.Empty,
                Priority = fields.TryGetProperty("priority", out var pri) && pri.ValueKind != JsonValueKind.Null
                    ? pri.GetProperty("name").GetString() ?? string.Empty : string.Empty,
            };
            return IntegrationResult<JiraTicket?>.Ok(ticket);
        }
        catch (Exception ex)
        {
            // Ticket id is not sensitive; the token is NEVER in the message.
            _logger.LogError(ex, "JIRA get-ticket failed for {TicketId}", ticketId);
            return IntegrationResult<JiraTicket?>.Fail(ex.Message);
        }
    }

    public async Task<IntegrationResult<JiraTicketResult>> UpdateTicketAsync(
        JiraCredential credential, string ticketId, JiraTicketUpdate update,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(update);

        var guard = await GuardAsync(credential, ticketId, ct).ConfigureAwait(false);
        if (guard is not null)
        {
            return IntegrationResult<JiraTicketResult>.Fail(guard);
        }

        try
        {
            var httpClient = _httpClientFactory.CreateClient(HttpClientName);

            if (!string.IsNullOrEmpty(update.Comment))
            {
                var commentPayload = new
                {
                    body = new
                    {
                        type = "doc",
                        version = 1,
                        content = new[]
                        {
                            new
                            {
                                type = "paragraph",
                                content = new object[]
                                {
                                    new { type = "text", text = update.Comment },
                                },
                            },
                        },
                    },
                };
                using var request = new HttpRequestMessage(
                    HttpMethod.Post, $"{TrimBase(credential.BaseUrl)}/rest/api/3/issue/{ticketId}/comment")
                {
                    Content = JsonContent.Create(commentPayload),
                };
                Authorize(request, credential);
                using var commentResponse = await httpClient.SendAsync(request, ct).ConfigureAwait(false);
                if (commentResponse.StatusCode == HttpStatusCode.NotFound)
                {
                    return IntegrationResult<JiraTicketResult>.Fail("not found");
                }
                if (IsRedirect(commentResponse))
                {
                    return IntegrationResult<JiraTicketResult>.Fail("refused redirect");
                }
                commentResponse.EnsureSuccessStatusCode();
            }

            return IntegrationResult<JiraTicketResult>.Ok(
                new JiraTicketResult { Success = true, TicketKey = ticketId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JIRA update-ticket failed for {TicketId}", ticketId);
            return IntegrationResult<JiraTicketResult>.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Use-time SSRF gate. Returns a failure reason string when the ticket id or
    /// base URL is rejected (so the HTTP call is NEVER made), or null when both
    /// pass. Runs before every request as defense in depth.
    /// </summary>
    private async Task<string?> GuardAsync(JiraCredential credential, string ticketId, CancellationToken ct)
    {
        if (!JiraBaseUrlGuard.IsValidTicketId(ticketId))
        {
            _logger.LogWarning("JIRA request refused: invalid ticket id shape.");
            return "invalid ticket id";
        }

        var validation = await JiraBaseUrlGuard
            .ValidateAsync(credential.BaseUrl, AllowedHostSuffixes(), dnsResolve: null, ct)
            .ConfigureAwait(false);
        if (!validation.IsValid)
        {
            _logger.LogWarning("JIRA request refused: baseUrl blocked ({Code}).", validation.ErrorCode);
            return validation.ErrorDetail ?? "invalid base url";
        }

        return null;
    }

    private IReadOnlyList<string>? AllowedHostSuffixes()
    {
        var raw = _configuration["Jira:AllowedHostSuffixes"];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool IsRedirect(HttpResponseMessage response) =>
        (int)response.StatusCode is >= 300 and < 400;

    private static void Authorize(HttpRequestMessage request, JiraCredential credential)
    {
        var authBytes = Encoding.UTF8.GetBytes($"{credential.Email}:{credential.ApiToken}");
        request.Headers.Authorization =
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));
    }

    private static string TrimBase(string baseUrl) => baseUrl.TrimEnd('/');
}
