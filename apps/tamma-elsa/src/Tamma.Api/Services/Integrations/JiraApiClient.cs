using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
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
/// </summary>
public sealed class JiraApiClient : IJiraApiClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<JiraApiClient> _logger;

    public JiraApiClient(
        IHttpClientFactory httpClientFactory,
        ILogger<JiraApiClient> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IntegrationResult<JiraTicket?>> GetTicketAsync(
        JiraCredential credential, string ticketId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        try
        {
            var httpClient = _httpClientFactory.CreateClient();
            Authorize(httpClient, credential);

            var response = await httpClient
                .GetAsync($"{TrimBase(credential.BaseUrl)}/rest/api/3/issue/{ticketId}", ct)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return IntegrationResult<JiraTicket?>.Fail("not found");
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
        try
        {
            var httpClient = _httpClientFactory.CreateClient();
            Authorize(httpClient, credential);

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
                var commentResponse = await httpClient
                    .PostAsJsonAsync($"{TrimBase(credential.BaseUrl)}/rest/api/3/issue/{ticketId}/comment", commentPayload, ct)
                    .ConfigureAwait(false);
                if (commentResponse.StatusCode == HttpStatusCode.NotFound)
                {
                    return IntegrationResult<JiraTicketResult>.Fail("not found");
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

    private static void Authorize(HttpClient httpClient, JiraCredential credential)
    {
        var authBytes = Encoding.UTF8.GetBytes($"{credential.Email}:{credential.ApiToken}");
        httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));
    }

    private static string TrimBase(string baseUrl) => baseUrl.TrimEnd('/');
}
