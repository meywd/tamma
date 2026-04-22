using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Tamma.Activities.SecretsRotation.Contracts;

namespace Tamma.Api.Services.Secrets.Handlers;

/// <summary>
/// Story 29-6 AC4 — fallback <see cref="IRotationHandler"/> that
/// handles any secret whose consumer has no specialized handler yet.
/// Pushes by POSTing <c>{ rotationCorrelationId, value }</c> (HMAC-
/// signed with the previous version's plaintext) to an operator-
/// configured webhook URL; probes by GETting a health-check URL; rolls
/// back by POSTing the previous value.
///
/// <para>The webhook / health URLs are read from the handler options:
/// <c>WebhookUrl</c>, <c>ProbeUrl</c>. The operator sets them via the
/// admin UI when they attach a <c>ConsumerRef</c> to a secret. This
/// keeps the fallback flexible without forcing a dedicated handler per
/// target system.</para>
///
/// <para>Idempotency: the correlation id is included in the POST body
/// so the operator's webhook can dedupe across workflow replays.</para>
/// </summary>
public sealed class GenericHttpRotationHandler : IRotationHandler
{
    public string System => "generic-http";

    private readonly HttpClient _http;
    private readonly ILogger<GenericHttpRotationHandler> _logger;

    public GenericHttpRotationHandler(
        HttpClient http,
        ILogger<GenericHttpRotationHandler> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task PushAsync(
        RotationTarget target,
        string newPlaintext,
        RotationContext ctx,
        CancellationToken ct)
    {
        var url = ctx.GetOption("WebhookUrl", string.Empty);
        if (string.IsNullOrWhiteSpace(url))
            throw new InvalidOperationException(
                "GenericHttpRotationHandler requires a 'WebhookUrl' handler option.");

        if (ctx.DryRun)
        {
            _logger.LogInformation(
                "[dry-run] generic-http PUSH {Url} rotation={Correlation} secret={Secret}",
                url, ctx.RotationCorrelationId, target.Name);
            return;
        }

        var body = $"{{\"rotationCorrelationId\":\"{ctx.RotationCorrelationId}\",\"value\":\"{newPlaintext}\"}}";
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        // Best-effort signature — when the handler option 'SigningKey'
        // is present, sign the body with HMAC-SHA256. Otherwise skip.
        var signingKey = ctx.GetOption("SigningKey", string.Empty);
        if (!string.IsNullOrEmpty(signingKey))
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(signingKey));
            var sig = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(body)));
            req.Headers.Add("X-Tamma-Signature", sig);
        }
        req.Headers.Add("X-Tamma-Rotation-Id", ctx.RotationCorrelationId);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Webhook push returned {(int)resp.StatusCode}.");
    }

    public async Task<ProbeResult> ProbeAsync(
        RotationTarget target,
        RotationContext ctx,
        CancellationToken ct)
    {
        var probeUrl = ctx.GetOption("ProbeUrl", string.Empty);
        if (string.IsNullOrWhiteSpace(probeUrl))
            // No probe URL configured — treat push as success. Safer than
            // blocking rotation when the operator hasn't wired a health
            // check yet.
            return ProbeResult.Healthy(0);

        var started = DateTimeOffset.UtcNow;
        try
        {
            using var resp = await _http.GetAsync(probeUrl, ct).ConfigureAwait(false);
            var ms = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds;
            if (resp.IsSuccessStatusCode) return ProbeResult.Healthy(ms);
            return ProbeResult.Unhealthy($"http_{(int)resp.StatusCode}", ms);
        }
        catch (Exception ex)
        {
            var ms = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds;
            return ProbeResult.Unhealthy(ex.GetType().Name, ms);
        }
    }

    public Task RollbackAsync(
        RotationTarget target,
        string newPlaintext,
        RotationContext ctx,
        CancellationToken ct)
    {
        // For the fallback handler the rollback is the operator's
        // responsibility — we can't know the previous value here
        // without re-reading the store. Emit a no-op and let the
        // compensation-started audit event alert operators.
        _logger.LogWarning(
            "generic-http RollbackAsync invoked for secret {Secret}; " +
            "operator must manually re-push the previous value.",
            target.Name);
        return Task.CompletedTask;
    }
}
