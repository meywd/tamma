using System.Net.Http.Json;
using Tamma.Api.Services.Secrets;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Alerts.Channels;

/// <summary>
/// Story 1.5-37 (Wave C.1) — PagerDuty Events v2 API delivery.
/// POSTs <c>{ routing_key, event_action, dedup_key, payload }</c> to
/// <c>https://events.pagerduty.com/v2/enqueue</c>.
///
/// <para>The <c>routing_key</c> is stored in the Story 29-1 secret
/// store and referenced via <see cref="AlertChannel.CredentialsSecretId"/>.
/// The <c>dedup_key</c> is the alert id so PagerDuty de-duplicates
/// re-deliveries of the same alert (critical if the dispatcher
/// retries a transient-failure delivery — PagerDuty must not page
/// twice).</para>
///
/// <para>Severity maps 1:1 to PagerDuty's <c>info</c>, <c>warning</c>,
/// <c>critical</c>; Tamma's three values line up directly so there's
/// no lookup table.</para>
/// </summary>
public sealed class PagerDutyAlertChannel : IAlertChannel
{
    internal const string EventsApiUrl = "https://events.pagerduty.com/v2/enqueue";

    public string ChannelType => AlertChannelType.PagerDuty;

    private readonly IHttpClientFactory _httpFactory;
    private readonly IAlertChannelSecretReader _secrets;
    private readonly ILogger<PagerDutyAlertChannel> _logger;

    public PagerDutyAlertChannel(
        IHttpClientFactory httpFactory,
        IAlertChannelSecretReader secrets,
        ILogger<PagerDutyAlertChannel> logger)
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
                Error: "PagerDuty channel missing CredentialsSecretId " +
                       "(routing_key must live in the secret store).");
        }

        string? routingKey;
        try
        {
            routingKey = await _secrets
                .GetPlaintextAsync(channel.CredentialsSecretId.Value, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new DeliveryResult(
                Success: false,
                Error: $"Secret read failed: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(routingKey))
        {
            return new DeliveryResult(
                Success: false,
                Error: "Secret store returned empty routing_key.");
        }

        var payload = new
        {
            routing_key = routingKey,
            event_action = "trigger",
            dedup_key = alert.Id.ToString("D"),
            payload = new
            {
                summary = alert.Title,
                severity = alert.Severity, // info/warning/critical — PD accepts these literally
                source = "tamma",
                component = "alert-system",
                custom_details = new
                {
                    description = alert.Description,
                    correlationId = alert.CorrelationId,
                    tenantId = alert.TenantId?.ToString("D"),
                    raisedAt = alert.CreatedAt.ToString("O"),
                },
            },
        };

        using var client = _httpFactory.CreateClient(SlackAlertChannel.HttpClientName);
        try
        {
            using var response = await client
                .PostAsJsonAsync(EventsApiUrl, payload, ct)
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
                // PD may respond with chunked JSON describing the
                // reject reason; body read failure is non-fatal.
            }
            return new DeliveryResult(
                Success: false,
                Error: $"PagerDuty returned {(int)response.StatusCode}: " +
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

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max];
}
