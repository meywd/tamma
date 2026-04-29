using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tamma.Activities.SecretsRotation.Contracts;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Models;

namespace Tamma.Api.Services.Platforms;

/// <summary>
/// Story 31-8 — <see cref="IRotationHandler"/> that bridges Epic 29's
/// rotation saga to Epic 31's CI secrets provisioner. Registered with
/// <see cref="System"/> = <c>ci-secrets</c>; the rotation saga
/// resolves it via the keyed
/// <see cref="IRotationHandlerRegistry"/> when a secret's first
/// <c>ConsumerRef.System</c> is <c>ci-secrets</c>.
///
/// <para>Consumer identifier shape (parsed by
/// <see cref="ParseConsumerIdentifier"/>): JSON object describing the
/// CI surfaces to push to. Example:</para>
/// <code>
/// {
///   "secretName": "DATABASE_URL",
///   "scope": "Repo",
///   "targets": [
///     { "kind": "Repo", "owner": "acme", "repo": "app" },
///     { "kind": "Repo", "owner": "acme", "repo": "service" }
///   ],
///   "metadata": { "masked": true, "protected": false }
/// }
/// </code>
///
/// <para>On <see cref="PushAsync"/> the handler:</para>
/// <list type="number">
///   <item>Resolves the tenant's
///         <see cref="IPlatformResolver.ListForTenantAsync"/>.</item>
///   <item>For every installation that advertises
///         <see cref="PlatformCapability.Secrets"/>, calls
///         <see cref="ICiSecretsProvisioner.RotateSecretAsync"/>.</item>
///   <item>Emits a <c>CI_SECRET.PROVISIONED.SUCCESS</c> /
///         <c>CI_SECRET.PROVISIONED.FAILED</c> event per result via
///         the <see cref="IRotationAuditEmitter"/>.</item>
/// </list>
///
/// <para>Idempotency: <c>RotateSecretAsync</c> is itself idempotent
/// (the platforms accept a PUT for the same name + value sequence
/// without producing duplicates). The
/// <see cref="RotationContext.RotationCorrelationId"/> is logged on
/// every audit event so a replayed activity can be traced back.</para>
///
/// <para>Cross-tenant safety: the handler resolves drivers ONLY for
/// <see cref="RotationTarget.TenantId"/>. Platform-scoped secrets
/// (TenantId == null) are explicitly rejected — pushing a
/// platform-scoped secret to per-tenant CI would leak data across
/// tenants. The audit event records the rejection.</para>
/// </summary>
public sealed class CiSecretsRotationHandler : IRotationHandler
{
    public const string SystemKey = "ci-secrets";
    public const string ProvisionedSuccessEvent = "CI_SECRET.PROVISIONED.SUCCESS";
    public const string ProvisionedFailedEvent = "CI_SECRET.PROVISIONED.FAILED";

    public string System => SystemKey;

    private readonly IPlatformResolver _resolver;
    private readonly IRotationAuditEmitter _auditor;
    private readonly ILogger<CiSecretsRotationHandler> _logger;

    public CiSecretsRotationHandler(
        IPlatformResolver resolver,
        IRotationAuditEmitter auditor,
        ILogger<CiSecretsRotationHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(auditor);
        ArgumentNullException.ThrowIfNull(logger);
        _resolver = resolver;
        _auditor = auditor;
        _logger = logger;
    }

    public async Task PushAsync(
        RotationTarget target,
        string newPlaintext,
        RotationContext ctx,
        CancellationToken ct)
    {
        if (target.TenantId is null)
        {
            _logger.LogWarning(
                "Refusing to push platform-scoped secret {SecretId} to CI: " +
                "ci-secrets handler requires a tenant id (cross-tenant leak prevention)",
                target.SecretId);
            throw new InvalidOperationException(
                "CiSecretsRotationHandler refuses to push a platform-scoped " +
                "secret to per-tenant CI. Configure tenant-scoped secrets only.");
        }

        var spec = ParseConsumerIdentifier(target.ConsumerIdentifier);

        if (ctx.DryRun)
        {
            _logger.LogInformation(
                "[dry-run] ci-secrets PUSH {SecretName} → {TargetCount} targets, " +
                "rotation={Correlation}",
                spec.SecretName, spec.Targets.Count, ctx.RotationCorrelationId);
            return;
        }

        // Enumerate every installation for the tenant, fan out to the
        // ones that have CI secrets capability. Multiple platforms
        // (e.g. tenant has BOTH a GitHub install AND a GitLab install)
        // each get the rotation pushed independently — that's the
        // whole point of this handler.
        var installations = await _resolver
            .ListForTenantAsync(target.TenantId.Value, ct).ConfigureAwait(false);

        if (installations.Count == 0)
        {
            _logger.LogInformation(
                "Tenant {TenantId} has no platform installations — " +
                "ci-secrets push is a no-op for secret {SecretId}",
                target.TenantId, target.SecretId);
            return;
        }

        var totalSuccess = 0;
        var totalFailed = 0;
        foreach (var installation in installations)
        {
            var driver = await _resolver
                .ResolveForTenantAsync(target.TenantId.Value, installation.Kind, ct)
                .ConfigureAwait(false);
            if (driver is null) continue;

            // Capability gate: only platforms advertising Secrets are
            // candidates. The provisioner additionally enforces this
            // per-target, but skipping early avoids the network call.
            if (!driver.Capabilities.Contains(PlatformCapability.Secrets)
                || driver.CiSecrets is null)
            {
                _logger.LogDebug(
                    "Skipping installation {InstallationId} ({Kind}) — " +
                    "no Secrets capability",
                    installation.Id, installation.Kind);
                continue;
            }

            IReadOnlyList<CiSecretProvisionResult> results;
            try
            {
                results = await driver.CiSecrets
                    .RotateSecretAsync(
                        spec.Scope,
                        spec.Targets,
                        spec.SecretName,
                        new RedactedSecret(newPlaintext),
                        spec.Metadata,
                        ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "ci-secrets rotation threw for tenant {TenantId} kind {Kind} " +
                    "secret {SecretName}; recording as failed",
                    target.TenantId, installation.Kind, spec.SecretName);
                await EmitAsync(
                    target, installation, ctx,
                    success: false,
                    descriptor: "<exception>",
                    error: $"unknown:{ex.GetType().Name}",
                    ct).ConfigureAwait(false);
                totalFailed++;
                continue;
            }

            foreach (var r in results)
            {
                if (r.Success) totalSuccess++; else totalFailed++;
                await EmitAsync(
                    target, installation, ctx,
                    success: r.Success,
                    descriptor: r.TargetDescriptor,
                    error: r.Error,
                    ct).ConfigureAwait(false);
            }
        }

        _logger.LogInformation(
            "ci-secrets rotation complete for secret {SecretId} tenant {TenantId} " +
            "rotation={Correlation}: {SuccessCount} success, {FailedCount} failed",
            target.SecretId, target.TenantId, ctx.RotationCorrelationId,
            totalSuccess, totalFailed);

        // Failure semantics: if EVERY result failed, throw so the saga
        // moves to the rollback path. Partial success is not retried —
        // the saga's PROBE step decides whether to compensate.
        if (totalFailed > 0 && totalSuccess == 0)
        {
            throw new InvalidOperationException(
                $"ci-secrets rotation: 0/{totalFailed} targets succeeded for " +
                $"secret {target.SecretId} tenant {target.TenantId}");
        }
    }

    public Task<ProbeResult> ProbeAsync(
        RotationTarget target,
        RotationContext ctx,
        CancellationToken ct)
    {
        // Probe path: the platform's API has no "verify a secret matches
        // a known plaintext" call (intentionally — secrets are write-only).
        // Treat the push success as the probe success; the saga's
        // PROBE step is therefore a no-op for ci-secrets consumers.
        // A future enhancement could trigger a no-op CI run that echoes
        // a hash of the secret to confirm propagation, but that's out
        // of scope here.
        return Task.FromResult(ProbeResult.Healthy(durationMs: 0));
    }

    public async Task RollbackAsync(
        RotationTarget target,
        string newPlaintext,
        RotationContext ctx,
        CancellationToken ct)
    {
        if (target.TenantId is null) return;

        var spec = ParseConsumerIdentifier(target.ConsumerIdentifier);

        // Rollback = re-push the OLD value (which the saga supplied as
        // newPlaintext on this call — see RollbackPushActivity contract).
        var installations = await _resolver
            .ListForTenantAsync(target.TenantId.Value, ct).ConfigureAwait(false);
        foreach (var installation in installations)
        {
            var driver = await _resolver
                .ResolveForTenantAsync(target.TenantId.Value, installation.Kind, ct)
                .ConfigureAwait(false);
            if (driver?.CiSecrets is null) continue;
            if (!driver.Capabilities.Contains(PlatformCapability.Secrets)) continue;

            try
            {
                await driver.CiSecrets
                    .ProvisionSecretAsync(
                        spec.Scope,
                        spec.Targets,
                        spec.SecretName,
                        new RedactedSecret(newPlaintext),
                        spec.Metadata,
                        ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "ci-secrets rollback failed for tenant {TenantId} kind {Kind}",
                    target.TenantId, installation.Kind);
            }
        }
    }

    /// <summary>
    /// Parse the <c>ConsumerRef.Identifier</c> JSON blob into a
    /// strongly-typed spec. Internal so tests can exercise it directly.
    /// </summary>
    internal static CiSecretConsumerSpec ParseConsumerIdentifier(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var secretName = root.GetProperty("secretName").GetString()
            ?? throw new InvalidOperationException(
                "ci-secrets consumer identifier missing 'secretName'");

        var scopeStr = root.GetProperty("scope").GetString() ?? "Repo";
        if (!Enum.TryParse<CiSecretScope>(scopeStr, ignoreCase: true, out var scope))
        {
            throw new InvalidOperationException(
                $"ci-secrets consumer identifier has invalid scope '{scopeStr}'");
        }

        var targetsJson = root.GetProperty("targets");
        var targets = new List<CiSecretTarget>();
        foreach (var t in targetsJson.EnumerateArray())
        {
            var kind = t.GetProperty("kind").GetString() ?? "Repo";
            CiSecretTarget tgt = kind.ToLowerInvariant() switch
            {
                "repo" => new CiSecretTarget.Repo(
                    t.GetProperty("owner").GetString() ?? "",
                    t.GetProperty("repo").GetString() ?? ""),
                "org" => new CiSecretTarget.Org(
                    t.GetProperty("orgOrGroup").GetString() ?? ""),
                "user" => new CiSecretTarget.User(
                    t.GetProperty("userLogin").GetString() ?? ""),
                "global" => new CiSecretTarget.Global(),
                "environment" => new CiSecretTarget.Environment(
                    t.GetProperty("owner").GetString() ?? "",
                    t.GetProperty("repo").GetString() ?? "",
                    t.GetProperty("environmentName").GetString() ?? ""),
                _ => throw new InvalidOperationException(
                    $"ci-secrets consumer identifier has unknown target kind '{kind}'"),
            };
            targets.Add(tgt);
        }

        CiSecretMetadata metadata = CiSecretMetadata.Default;
        if (root.TryGetProperty("metadata", out var m) && m.ValueKind == JsonValueKind.Object)
        {
            var protectedFlag = m.TryGetProperty("protected", out var p)
                && p.ValueKind == JsonValueKind.True;
            var maskedFlag = m.TryGetProperty("masked", out var mk)
                && mk.ValueKind == JsonValueKind.True;
            string? envScope = null;
            if (m.TryGetProperty("environmentScope", out var es)
                && es.ValueKind == JsonValueKind.String)
            {
                envScope = es.GetString();
            }
            string varType = "env_var";
            if (m.TryGetProperty("variableType", out var vt)
                && vt.ValueKind == JsonValueKind.String)
            {
                varType = vt.GetString() ?? "env_var";
            }
            metadata = new CiSecretMetadata(protectedFlag, maskedFlag, envScope, varType);
        }

        return new CiSecretConsumerSpec(secretName, scope, targets, metadata);
    }

    private async Task EmitAsync(
        RotationTarget target,
        PlatformInstallation installation,
        RotationContext ctx,
        bool success,
        string descriptor,
        string? error,
        CancellationToken ct)
    {
        var data = new Dictionary<string, object?>
        {
            ["platformKind"] = installation.Kind.ToString(),
            ["installationId"] = installation.Id,
            ["targetDescriptor"] = descriptor,
            ["error"] = error,
        };
        var evt = RotationAuditEvent.Create(
            eventType: success ? ProvisionedSuccessEvent : ProvisionedFailedEvent,
            secretId: target.SecretId,
            tenantId: target.TenantId,
            rotationCorrelationId: ctx.RotationCorrelationId,
            versionNumber: target.NewVersionNumber,
            detail: error,
            data: data);
        await _auditor.EmitAsync(evt, ct).ConfigureAwait(false);
    }
}

/// <summary>
/// Story 31-8 — internal parsed shape of a ci-secrets consumer spec.
/// </summary>
internal sealed record CiSecretConsumerSpec(
    string SecretName,
    CiSecretScope Scope,
    IReadOnlyList<CiSecretTarget> Targets,
    CiSecretMetadata Metadata);
