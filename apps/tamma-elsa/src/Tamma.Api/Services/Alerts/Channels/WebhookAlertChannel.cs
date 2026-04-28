using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tamma.Api.Services.Secrets;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Alerts.Channels;

/// <summary>
/// Story 1.5-37 (Wave C.1) — generic HTTP webhook delivery. POSTs
/// a JSON body <c>{ alert, deliveredAt }</c> to
/// <c>channel.Config.url</c>, signed with an HMAC-SHA256 header.
///
/// <para>Headers on every request:
/// <list type="bullet">
///   <item><description><c>Content-Type: application/json</c></description></item>
///   <item><description><c>X-Tamma-Alert-Id</c> — the alert id (string GUID).
///     Clients use this for their own dedup.</description></item>
///   <item><description><c>X-Tamma-Signature</c> — <c>sha256=&lt;hex&gt;</c>,
///     HMAC-SHA256 of the request body with the shared secret resolved from
///     <see cref="AlertChannel.CredentialsSecretId"/>.</description></item>
/// </list>
/// </para>
///
/// <para>The HMAC shared secret lives in the Story 29-1 secret store;
/// <see cref="AlertChannel.Config"/> only carries the target URL + optional
/// severity filter. Clients MUST verify the signature before trusting the
/// payload — the format matches the GitHub-style webhook convention.</para>
/// </summary>
public sealed class WebhookAlertChannel : IAlertChannel
{
    public string ChannelType => AlertChannelType.Webhook;

    private readonly IHttpClientFactory _httpFactory;
    private readonly IAlertChannelSecretReader _secrets;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<WebhookAlertChannel> _logger;

    public WebhookAlertChannel(
        IHttpClientFactory httpFactory,
        IAlertChannelSecretReader secrets,
        TimeProvider timeProvider,
        ILogger<WebhookAlertChannel> logger)
    {
        ArgumentNullException.ThrowIfNull(httpFactory);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _httpFactory = httpFactory;
        _secrets = secrets;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<DeliveryResult> SendAsync(
        Alert alert, AlertChannel channel, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(alert);
        ArgumentNullException.ThrowIfNull(channel);

        string? url;
        try
        {
            url = ExtractUrl(channel.Config);
        }
        catch (Exception ex)
        {
            return new DeliveryResult(
                Success: false,
                Error: $"Channel config parse failed: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(url))
        {
            return new DeliveryResult(
                Success: false,
                Error: "Webhook channel config missing 'url'.");
        }

        if (channel.CredentialsSecretId is null)
        {
            return new DeliveryResult(
                Success: false,
                Error: "Webhook channel missing CredentialsSecretId " +
                       "(HMAC shared secret must live in the secret store).");
        }

        string? sharedSecret;
        try
        {
            sharedSecret = await _secrets
                .GetPlaintextAsync(channel.CredentialsSecretId.Value, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new DeliveryResult(
                Success: false,
                Error: $"Secret read failed: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(sharedSecret))
        {
            return new DeliveryResult(
                Success: false,
                Error: "Secret store returned empty shared secret.");
        }

        var now = _timeProvider.GetUtcNow();
        var body = JsonSerializer.Serialize(new
        {
            alert = new
            {
                id = alert.Id,
                severity = alert.Severity,
                title = alert.Title,
                description = alert.Description,
                correlationId = alert.CorrelationId,
                tenantId = alert.TenantId,
                metadata = ParseMetadata(alert.Metadata),
                createdAt = alert.CreatedAt,
            },
            deliveredAt = now.ToString("O"),
        });

        var signature = ComputeSignature(body, sharedSecret);

        using var client = _httpFactory.CreateClient(SlackAlertChannel.HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8),
        };
        request.Content.Headers.ContentType =
            new MediaTypeHeaderValue("application/json");
        request.Headers.TryAddWithoutValidation(
            "X-Tamma-Alert-Id", alert.Id.ToString("D"));
        request.Headers.TryAddWithoutValidation(
            "X-Tamma-Signature", $"sha256={signature}");

        try
        {
            using var response = await client
                .SendAsync(request, ct)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
                return new DeliveryResult(Success: true, Error: null);

            var respBody = string.Empty;
            try
            {
                respBody = await response.Content
                    .ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                // Response body is optional for audit; ignore read failures.
            }
            return new DeliveryResult(
                Success: false,
                Error: $"Webhook returned {(int)response.StatusCode}: " +
                       $"{Truncate(respBody, 512)}");
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new DeliveryResult(
                Success: false,
                Error: $"HTTP call failed: {ex.Message}");
        }
    }

    internal static string ComputeSignature(string body, string sharedSecret)
    {
        var key = Encoding.UTF8.GetBytes(sharedSecret);
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var hash = HMACSHA256.HashData(key, bodyBytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? ExtractUrl(string configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson) || configJson == "{}")
            return null;
        using var doc = JsonDocument.Parse(configJson);
        return doc.RootElement.TryGetProperty("url", out var urlEl)
            ? urlEl.GetString()
            : null;
    }

    private static object? ParseMetadata(string metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson) || metadataJson == "{}")
            return null;
        try
        {
            using var doc = JsonDocument.Parse(metadataJson);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max];
}
