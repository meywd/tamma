using System.Net.Http.Json;
using System.Text.Json;
using Tamma.Api.Services.Secrets;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Alerts.Channels;

/// <summary>
/// Story 1.5-37 (Wave C.1) — Slack webhook delivery. POSTs a
/// <c>{text, blocks[]}</c> payload to the channel's webhook URL.
///
/// <para>The webhook URL is stored in the Story 29-1 secret store
/// and referenced via <see cref="AlertChannel.CredentialsSecretId"/>;
/// it never appears in <see cref="AlertChannel.Config"/>. Severity
/// drives the attachment colour: critical = red, warning = orange,
/// info = blue.</para>
///
/// <para>HTTP: uses the <c>AlertChannelHttp</c> named client with a
/// 5-second timeout and 1 built-in retry on transient network errors.
/// 4xx responses are terminal (bad webhook URL / disabled integration)
/// and surface as a <see cref="DeliveryResult"/> failure without a
/// retry.</para>
/// </summary>
public sealed class SlackAlertChannel : IAlertChannel
{
    public const string HttpClientName = "AlertChannelHttp";

    public string ChannelType => AlertChannelType.Slack;

    private readonly IHttpClientFactory _httpFactory;
    private readonly IAlertChannelSecretReader _secrets;
    private readonly ILogger<SlackAlertChannel> _logger;

    public SlackAlertChannel(
        IHttpClientFactory httpFactory,
        IAlertChannelSecretReader secrets,
        ILogger<SlackAlertChannel> logger)
    {
        ArgumentNullException.ThrowIfNull(httpFactory);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(logger);
        _httpFactory = httpFactory;
        _secrets = secrets;
        _logger = logger;
    }

    public async Task<DeliveryResult> SendAsync(
        Alert alert, AlertChannel channel, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(alert);
        ArgumentNullException.ThrowIfNull(channel);

        if (channel.CredentialsSecretId is null)
        {
            return new DeliveryResult(
                Success: false,
                Error: "Slack channel missing CredentialsSecretId " +
                       "(webhook URL must live in the secret store).");
        }

        string? webhookUrl;
        try
        {
            webhookUrl = await _secrets
                .GetPlaintextAsync(channel.CredentialsSecretId.Value, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new DeliveryResult(
                Success: false,
                Error: $"Secret read failed: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            return new DeliveryResult(
                Success: false,
                Error: "Secret store returned empty webhook URL.");
        }

        var payload = new
        {
            text = $"[{alert.Severity.ToUpperInvariant()}] {alert.Title}",
            attachments = new[]
            {
                new
                {
                    color = SeverityColor(alert.Severity),
                    title = alert.Title,
                    text = alert.Description,
                    fields = new[]
                    {
                        new { title = "Severity", value = alert.Severity, @short = true },
                        new { title = "Alert ID", value = alert.Id.ToString("D"), @short = true },
                        new { title = "Correlation", value = alert.CorrelationId ?? "(none)", @short = true },
                        new { title = "Raised (UTC)", value = alert.CreatedAt.ToString("O"), @short = true },
                    },
                },
            },
        };

        using var client = _httpFactory.CreateClient(HttpClientName);
        try
        {
            using var response = await client
                .PostAsJsonAsync(webhookUrl, payload, ct)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
                return new DeliveryResult(Success: true, Error: null);

            var body = string.Empty;
            try
            {
                body = await response.Content
                    .ReadAsStringAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                // The Slack webhook body is advisory-only; swallow
                // any read failure and surface the status alone.
            }
            return new DeliveryResult(
                Success: false,
                Error: $"Slack webhook returned {(int)response.StatusCode}: " +
                       $"{Truncate(body, 512)}");
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

    private static string SeverityColor(string severity) => severity switch
    {
        AlertSeverity.Critical => "#c0392b",
        AlertSeverity.Warning => "#d35400",
        _ => "#2980b9",
    };

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max];
}
