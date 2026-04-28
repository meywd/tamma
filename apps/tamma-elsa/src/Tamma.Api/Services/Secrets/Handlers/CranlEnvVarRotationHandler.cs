using Microsoft.Extensions.Logging;
using Tamma.Activities.SecretsRotation.Contracts;
using Tamma.Api.Services.Provisioning.Cranl;

namespace Tamma.Api.Services.Secrets.Handlers;

/// <summary>
/// Story 29-8 — rotates a single env-var on a Cranl application.
/// Fetches the current env text, merges the new value, PUTs it back,
/// triggers a lifecycle action (<c>reload</c> by default,
/// <c>deploy</c> when the secret opts into redeploy-on-rotate), then
/// polls app status back to <c>running</c>. Rollback re-PUTs the
/// previous env text.
///
/// <para>Idempotency comes from two directions:</para>
/// <list type="bullet">
///   <item><description>The rotation workflow (29-6) is idempotent on
///     <c>rotationCorrelationId</c>, so a replayed push/rollback lands
///     on the same secret version.</description></item>
///   <item><description>Cranl's PUT env is a full-replace — two
///     successive PUTs with the same body are equivalent to one.
///     The reload is idempotent on its own semantics (a second
///     reload while the app is already restarting is a no-op on
///     Cranl).</description></item>
/// </list>
///
/// <para>Log hygiene: the full env text is never logged. The only
/// observability surface is the key-diff produced by
/// <see cref="CranlEnvText.DiffKeys"/> — operators see
/// <c>~ TAMMA_SHARED_SECRET</c> in the audit feed rather than the
/// value (Story 29-8 AC6).</para>
/// </summary>
public sealed class CranlEnvVarRotationHandler : IRotationHandler
{
    public string System => "cranl";

    private readonly ICranlApiClient _cranl;
    private readonly ISecretRotationGateway _gateway;
    private readonly ILogger<CranlEnvVarRotationHandler> _logger;

    // 29-8 AC7 — retry schedule for Cranl 5xx on push/reload.
    public static readonly IReadOnlyList<TimeSpan> DefaultRetryDelays = new[]
    {
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(90),
    };

    public CranlEnvVarRotationHandler(
        ICranlApiClient cranl,
        ISecretRotationGateway gateway,
        ILogger<CranlEnvVarRotationHandler> logger)
    {
        _cranl = cranl;
        _gateway = gateway;
        _logger = logger;
    }

    /// <summary>Override for tests; defaults to <see cref="DefaultRetryDelays"/>.</summary>
    public IReadOnlyList<TimeSpan> RetryDelays { get; set; } = DefaultRetryDelays;

    public async Task PushAsync(
        RotationTarget target,
        string newPlaintext,
        RotationContext ctx,
        CancellationToken ct)
    {
        var parsed = CranlConsumerIdentifier.Parse(target.ConsumerIdentifier);

        if (ctx.DryRun)
        {
            _logger.LogInformation(
                "[dry-run] Cranl env push would update '{Key}' on app {AppId} (rotation={Correlation})",
                parsed.EnvVarName, parsed.AppId, ctx.RotationCorrelationId);
            return;
        }

        var currentText = await FetchEnvWithRetryAsync(parsed.AppId, ct).ConfigureAwait(false);
        var parsedEntries = CranlEnvText.Parse(currentText);
        var merged = CranlEnvText.Merge(parsedEntries, parsed.EnvVarName, newPlaintext);
        var mergedText = CranlEnvText.Serialize(merged);

        // Key-only diff — never log the value.
        var diff = CranlEnvText.DiffKeys(currentText, mergedText);
        _logger.LogInformation(
            "Cranl env rotation app={AppId} rotation={Correlation} diff={Diff}",
            parsed.AppId, ctx.RotationCorrelationId, string.Join(",", diff));

        await PutEnvWithRetryAsync(parsed.AppId, mergedText, ct).ConfigureAwait(false);

        var mode = ctx.GetOption("CranlMode", "reload");
        if (string.Equals(mode, "redeploy", StringComparison.OrdinalIgnoreCase))
        {
            await DeployWithRetryAsync(parsed.AppId, ct).ConfigureAwait(false);
        }
        else
        {
            await ReloadWithRetryAsync(parsed.AppId, ct).ConfigureAwait(false);
        }
    }

    public async Task<ProbeResult> ProbeAsync(
        RotationTarget target,
        RotationContext ctx,
        CancellationToken ct)
    {
        var parsed = CranlConsumerIdentifier.Parse(target.ConsumerIdentifier);
        var timeoutSeconds = int.TryParse(ctx.GetOption("ProbeTimeoutSeconds", "300"), out var t) ? t : 300;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        var started = DateTimeOffset.UtcNow;

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var app = await _cranl.GetApplicationAsync(parsed.AppId, ct).ConfigureAwait(false);
                if (string.Equals(app.Status, "running", StringComparison.OrdinalIgnoreCase))
                {
                    var ms = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds;
                    return ProbeResult.Healthy(ms);
                }
                if (string.Equals(app.Status, "error", StringComparison.OrdinalIgnoreCase))
                {
                    var ms = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds;
                    return ProbeResult.Unhealthy("cranl_status_error", ms);
                }
            }
            catch (CranlApiException ex) when ((int)ex.StatusCode == 429)
            {
                _logger.LogWarning("Cranl rate limit during probe — backing off.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cranl probe threw; will retry.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
        }

        var elapsedMs = (long)(DateTimeOffset.UtcNow - started).TotalMilliseconds;
        return ProbeResult.Unhealthy("probe_timeout", elapsedMs);
    }

    public async Task RollbackAsync(
        RotationTarget target,
        string newPlaintext,
        RotationContext ctx,
        CancellationToken ct)
    {
        var parsed = CranlConsumerIdentifier.Parse(target.ConsumerIdentifier);

        string? previousPlaintext = null;
        if (target.PreviousVersionNumber > 0)
        {
            previousPlaintext = await _gateway.GetVersionPlaintextAsync(
                    target.SecretId, target.PreviousVersionNumber, ct)
                .ConfigureAwait(false);
        }

        var currentText = await _cranl.GetEnvironmentAsync(parsed.AppId, ct).ConfigureAwait(false);
        var entries = CranlEnvText.Parse(currentText);
        IReadOnlyList<EnvEntry> restored;
        if (previousPlaintext is not null)
        {
            restored = CranlEnvText.Merge(entries, parsed.EnvVarName, previousPlaintext);
        }
        else
        {
            // No previous value — remove the var so the app doesn't keep
            // running with the compromised/broken value.
            restored = entries.Where(e => !(e.IsPair && e.Key == parsed.EnvVarName)).ToList();
        }
        var restoredText = CranlEnvText.Serialize(restored);

        var diff = CranlEnvText.DiffKeys(currentText, restoredText);
        _logger.LogInformation(
            "Cranl env rollback app={AppId} rotation={Correlation} diff={Diff}",
            parsed.AppId, ctx.RotationCorrelationId, string.Join(",", diff));

        await _cranl.PutEnvironmentAsync(parsed.AppId, restoredText, ct).ConfigureAwait(false);

        var mode = ctx.GetOption("CranlMode", "reload");
        if (string.Equals(mode, "redeploy", StringComparison.OrdinalIgnoreCase))
            await _cranl.DeployApplicationAsync(parsed.AppId, ct).ConfigureAwait(false);
        else
            await _cranl.ApplicationLifecycleAsync(parsed.AppId, "reload", ct).ConfigureAwait(false);
    }

    private async Task<string> FetchEnvWithRetryAsync(string appId, CancellationToken ct) =>
        await RetryAsync(() => _cranl.GetEnvironmentAsync(appId, ct)).ConfigureAwait(false);

    private async Task PutEnvWithRetryAsync(string appId, string envText, CancellationToken ct) =>
        await RetryAsync(async () =>
        {
            await _cranl.PutEnvironmentAsync(appId, envText, ct).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);

    private async Task ReloadWithRetryAsync(string appId, CancellationToken ct) =>
        await RetryAsync(async () =>
        {
            await _cranl.ApplicationLifecycleAsync(appId, "reload", ct).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);

    private async Task DeployWithRetryAsync(string appId, CancellationToken ct) =>
        await RetryAsync(async () =>
        {
            await _cranl.DeployApplicationAsync(appId, ct).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);

    private async Task<T> RetryAsync<T>(Func<Task<T>> action)
    {
        Exception? last = null;
        for (var attempt = 0; attempt <= RetryDelays.Count; attempt++)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (CranlApiException ex) when (IsRetryable(ex))
            {
                last = ex;
                if (attempt >= RetryDelays.Count) break;
                await Task.Delay(RetryDelays[attempt]).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                last = ex;
                if (attempt >= RetryDelays.Count) break;
                await Task.Delay(RetryDelays[attempt]).ConfigureAwait(false);
            }
        }
        throw last ?? new InvalidOperationException("Cranl call failed for unknown reasons.");
    }

    private static bool IsRetryable(CranlApiException ex) =>
        (int)ex.StatusCode is >= 500 and < 600 || (int)ex.StatusCode == 429;
}
