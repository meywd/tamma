using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Email;

/// <summary>
/// HTTP-synchronous <see cref="IEmailService"/> that delivers via Resend's
/// API (<c>POST https://api.resend.com/emails</c>).
///
/// <para>
/// Flow:
/// <list type="number">
///   <item><description>Generate a transaction id.</description></item>
///   <item><description>Emit <see cref="EmailEventTypes.Queued"/>.</description></item>
///   <item><description>POST to Resend using the named <c>"resend"</c>
///     HttpClient.</description></item>
///   <item><description>On 2xx: emit <see cref="EmailEventTypes.Sent"/> and
///     return the txn id.</description></item>
///   <item><description>On any error: log the txn id (no recipient), emit
///     <see cref="EmailEventTypes.Failed"/>, and still return the txn id.
///     <b>Never throws</b> for transport failures — callers rely on that.</description></item>
/// </list>
/// </para>
///
/// <para>
/// Unlike <see cref="SmtpEmailService"/>, this provider does not touch the
/// outbox table. If Resend is down, the message is lost at the provider hop;
/// ops detect this via the <c>EMAIL.SENT.FAILED</c> event stream.
/// </para>
/// </summary>
public sealed class ResendEmailService : IEmailService
{
    private const string ResendClientName = "resend";
    private const string ResendEndpoint = "emails";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IEventRepository _events;
    private readonly ITenantContext _tenantContext;
    private readonly IConfiguration _config;
    private readonly ILogger<ResendEmailService> _logger;

    public ResendEmailService(
        IHttpClientFactory httpClientFactory,
        IEventRepository events,
        ITenantContext tenantContext,
        IConfiguration config,
        ILogger<ResendEmailService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _events = events;
        _tenantContext = tenantContext;
        _config = config;
        _logger = logger;
    }

    public async Task<Guid> SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var txnId = Guid.NewGuid();
        var tenantId = message.TenantId ?? _tenantContext.TenantId;
        var template = message.Template ?? "unknown";

        await SafeEmitAsync(EmailEventTypes.Queued, txnId, template, tenantId, message.UserId,
            extraData: null);

        var apiKey = _config["Email:Resend:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogError(
                "Resend delivery failed txn={TxnId}: Email:Resend:ApiKey not configured", txnId);
            await SafeEmitAsync(EmailEventTypes.Failed, txnId, template, tenantId, message.UserId,
                extraData: new Dictionary<string, object?>
                {
                    ["provider"] = "resend",
                    ["error_class"] = "ConfigurationMissing",
                });
            return txnId;
        }

        var fromAddress = message.From
            ?? _config["Email:From"]
            ?? throw new InvalidOperationException(
                "Either EmailMessage.From or Email:From configuration must be provided.");

        var payload = new ResendRequest(
            From: fromAddress,
            To: new[] { message.To },
            Subject: message.Subject,
            Html: message.Html,
            Text: message.Text);

        var client = _httpClientFactory.CreateClient(ResendClientName);
        if (client.BaseAddress is null)
        {
            client.BaseAddress = new Uri("https://api.resend.com/");
        }
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", apiKey);

        try
        {
            using var response = await client.PostAsJsonAsync(ResendEndpoint, payload, ct)
                .ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                await SafeEmitAsync(EmailEventTypes.Sent, txnId, template, tenantId, message.UserId,
                    extraData: new Dictionary<string, object?>
                    {
                        ["provider"] = "resend",
                        ["http_status"] = (int)response.StatusCode,
                    });
                _logger.LogInformation(
                    "Resend delivery ok txn={TxnId} status={Status}",
                    txnId, (int)response.StatusCode);
                return txnId;
            }

            // Status is a numeric — not tainted, safe to log.
            _logger.LogError(
                "Resend delivery failed txn={TxnId} status={Status}",
                txnId, (int)response.StatusCode);

            await SafeEmitAsync(EmailEventTypes.Failed, txnId, template, tenantId, message.UserId,
                extraData: new Dictionary<string, object?>
                {
                    ["provider"] = "resend",
                    ["error_class"] = "HttpStatusError",
                    ["http_status"] = (int)response.StatusCode,
                });
            return txnId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Resend delivery failed txn={TxnId}", txnId);
            await SafeEmitAsync(EmailEventTypes.Failed, txnId, template, tenantId, message.UserId,
                extraData: new Dictionary<string, object?>
                {
                    ["provider"] = "resend",
                    ["error_class"] = ex.GetType().FullName,
                });
            return txnId;
        }
    }

    private async Task SafeEmitAsync(
        string type,
        Guid txnId,
        string template,
        Guid? tenantId,
        Guid? userId,
        IReadOnlyDictionary<string, object?>? extraData)
    {
        try
        {
            var tags = new Dictionary<string, string?>
            {
                ["txn_id"] = txnId.ToString(),
                ["template"] = template,
                ["tenant_id"] = tenantId?.ToString(),
                ["user_id"] = userId?.ToString(),
            };
            var data = new Dictionary<string, object?> { ["provider"] = "resend" };
            if (extraData is not null)
            {
                foreach (var kv in extraData)
                    data[kv.Key] = kv.Value;
            }

            await _events.AppendAsync(new DomainEvent
            {
                Type = type,
                TenantId = tenantId,
                Tags = JsonSerializer.Serialize(tags),
                Metadata = """{"eventSource":"system"}""",
                Data = JsonSerializer.Serialize(data),
            });
        }
        catch (Exception ex)
        {
            // Last-resort: we really don't want event-store failures to break
            // the caller. Txn id + type only — no PII.
            _logger.LogWarning(ex,
                "Email event emission failed txn={TxnId} type={Type}", txnId, type);
        }
    }

    // Serialized to Resend JSON body. Property names match Resend's API
    // exactly (System.Text.Json lowercases via JsonPropertyName).
    private sealed record ResendRequest(
        [property: System.Text.Json.Serialization.JsonPropertyName("from")] string From,
        [property: System.Text.Json.Serialization.JsonPropertyName("to")] string[] To,
        [property: System.Text.Json.Serialization.JsonPropertyName("subject")] string Subject,
        [property: System.Text.Json.Serialization.JsonPropertyName("html")] string Html,
        [property: System.Text.Json.Serialization.JsonPropertyName("text")] string Text);
}
