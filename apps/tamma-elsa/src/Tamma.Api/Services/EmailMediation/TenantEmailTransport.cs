using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Tamma.Api.Services.Email;
using Tamma.Api.Services.Integrations;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.EmailMediation;

/// <summary>
/// Default <see cref="ITenantEmailTransport"/> — delivers a SaaS tenant's message
/// through the tenant's OWN transport (their Resend key over HTTP, or their SMTP
/// relay via <see cref="ITenantSmtpTransport"/>), emitting the EMAIL.* DCB audit.
/// The tenant transport secret is request-scoped (from the resolved bundle) and is
/// NEVER logged or echoed.
/// </summary>
public sealed class TenantEmailTransport : ITenantEmailTransport
{
    private const string ResendClientName = "resend";
    private static readonly Uri ResendBaseAddress = new("https://api.resend.com/");
    private const string ResendEndpoint = "emails";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ITenantSmtpTransport _smtp;
    private readonly IEventRepository _events;
    private readonly ILogger<TenantEmailTransport> _logger;

    public TenantEmailTransport(
        IHttpClientFactory httpClientFactory,
        ITenantSmtpTransport smtp,
        IEventRepository events,
        ILogger<TenantEmailTransport> logger)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _smtp = smtp ?? throw new ArgumentNullException(nameof(smtp));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<Guid> SendAsync(EmailCredential credential, EmailMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentNullException.ThrowIfNull(message);

        var txnId = Guid.NewGuid();
        var tenantId = message.TenantId;
        var template = message.Template ?? "unknown";

        await SafeEmitAsync(EmailEventTypes.Queued, txnId, template, tenantId, message.UserId, extraData: null)
            .ConfigureAwait(false);

        try
        {
            if (credential.Transport == EmailCredential.TransportResend)
            {
                await SendViaResendAsync(txnId, credential, message, template, tenantId, ct).ConfigureAwait(false);
            }
            else
            {
                await _smtp.SendAsync(credential, message, ct).ConfigureAwait(false);
                await SafeEmitAsync(EmailEventTypes.Sent, txnId, template, tenantId, message.UserId,
                    extraData: new Dictionary<string, object?> { ["provider"] = "smtp" }).ConfigureAwait(false);
                _logger.LogInformation("Tenant SMTP delivery ok txn={TxnId}", txnId);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // NEVER log recipient / subject / body / host — only txn id + provider.
            _logger.LogError(ex, "Tenant email delivery failed txn={TxnId} transport={Transport}",
                txnId, credential.Transport);
            await SafeEmitAsync(EmailEventTypes.Failed, txnId, template, tenantId, message.UserId,
                extraData: new Dictionary<string, object?>
                {
                    ["provider"] = credential.Transport,
                    ["error_class"] = ex.GetType().FullName,
                }).ConfigureAwait(false);
        }

        return txnId;
    }

    private async Task SendViaResendAsync(
        Guid txnId, EmailCredential credential, EmailMessage message, string template,
        Guid? tenantId, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient(ResendClientName);
        var baseAddress = client.BaseAddress ?? ResendBaseAddress;

        var payload = new ResendRequest(
            From: credential.From,
            To: new[] { message.To },
            Subject: message.Subject,
            Html: message.Html,
            Text: message.Text);

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseAddress, ResendEndpoint))
        {
            Content = JsonContent.Create(payload),
        };
        // Per-request auth with the TENANT'S key — never mutate the shared named
        // client's DefaultRequestHeaders (that would leak one tenant's key onto
        // another tenant's request on the pooled handler).
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.ResendApiKey);

        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            await SafeEmitAsync(EmailEventTypes.Sent, txnId, template, tenantId, message.UserId,
                extraData: new Dictionary<string, object?>
                {
                    ["provider"] = "resend",
                    ["http_status"] = (int)response.StatusCode,
                }).ConfigureAwait(false);
            _logger.LogInformation("Tenant Resend delivery ok txn={TxnId} status={Status}",
                txnId, (int)response.StatusCode);
            return;
        }

        _logger.LogError("Tenant Resend delivery failed txn={TxnId} status={Status}",
            txnId, (int)response.StatusCode);
        await SafeEmitAsync(EmailEventTypes.Failed, txnId, template, tenantId, message.UserId,
            extraData: new Dictionary<string, object?>
            {
                ["provider"] = "resend",
                ["error_class"] = "HttpStatusError",
                ["http_status"] = (int)response.StatusCode,
            }).ConfigureAwait(false);
    }

    private async Task SafeEmitAsync(
        string type, Guid txnId, string template, Guid? tenantId, Guid? userId,
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
                ["mode"] = "byok",
            };
            var data = new Dictionary<string, object?>();
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
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Event-store failures must never break the send. Txn id + type only — no PII.
            _logger.LogWarning(ex, "Tenant email event emission failed txn={TxnId} type={Type}", txnId, type);
        }
    }

    private sealed record ResendRequest(
        [property: JsonPropertyName("from")] string From,
        [property: JsonPropertyName("to")] string[] To,
        [property: JsonPropertyName("subject")] string Subject,
        [property: JsonPropertyName("html")] string Html,
        [property: JsonPropertyName("text")] string Text);
}
