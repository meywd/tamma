using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sodium;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Logging;

namespace Tamma.Api.Services.Platforms;

/// <summary>
/// Story 31-8 — GitHub implementation of
/// <see cref="ICiSecretsProvisioner"/>.
///
/// <para>Encrypts every plaintext value with libsodium's
/// <c>crypto_box_seal</c> (X25519 + XSalsa20-Poly1305) before sending
/// it over the wire. The repo / org / environment public key is
/// fetched from the corresponding <c>secrets/public-key</c> endpoint;
/// the encrypted value + key id is then PUT to the
/// <c>secrets/{name}</c> endpoint.</para>
///
/// <para>Capability gating: this provisioner is only attached to a
/// driver when the driver advertises <see cref="PlatformCapability.Secrets"/>
/// + <see cref="PlatformCapability.LibsodiumSecrets"/>. Scope
/// support per the brief:</para>
/// <list type="bullet">
///   <item><see cref="CiSecretScope.Repo"/> ✓</item>
///   <item><see cref="CiSecretScope.Org"/> ✓</item>
///   <item><see cref="CiSecretScope.Environment"/> ✓</item>
///   <item><see cref="CiSecretScope.User"/> ✗ — GitHub has Codespaces
///         personal secrets but not general user-actions secrets;
///         returns <c>scope_not_supported_on_platform</c>.</item>
///   <item><see cref="CiSecretScope.Global"/> ✗ — same.</item>
/// </list>
///
/// <para>Concurrency is capped at 5 parallel writes via
/// <see cref="SemaphoreSlim"/> — matches today's
/// <c>LibsodiumGitHubSecretsProvisioner</c>. Per-target failures do
/// NOT throw; each target gets its own
/// <see cref="CiSecretProvisionResult"/> entry.</para>
/// </summary>
public sealed class GitHubCiSecretsProvisioner : ICiSecretsProvisioner
{
    private const int DefaultMaxConcurrency = 5;

    public PlatformKind Kind => PlatformKind.GitHub;

    private readonly HttpClient _http;
    private readonly ILogger<GitHubCiSecretsProvisioner> _logger;
    private readonly int _maxConcurrency;

    public GitHubCiSecretsProvisioner(
        HttpClient http,
        ILogger<GitHubCiSecretsProvisioner>? logger = null,
        int maxConcurrency = DefaultMaxConcurrency)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _logger = logger ?? NullLogger<GitHubCiSecretsProvisioner>.Instance;
        _maxConcurrency = maxConcurrency > 0 ? maxConcurrency : DefaultMaxConcurrency;
    }

    public Task<IReadOnlyList<CiSecretProvisionResult>> ProvisionSecretAsync(
        CiSecretScope scope,
        IReadOnlyList<CiSecretTarget> targets,
        string secretName,
        RedactedSecret secretValue,
        CiSecretMetadata? metadata = null,
        CancellationToken ct = default) =>
        FanOutAsync(scope, targets, secretName, secretValue, "provision", ct);

    public Task<IReadOnlyList<CiSecretProvisionResult>> RotateSecretAsync(
        CiSecretScope scope,
        IReadOnlyList<CiSecretTarget> targets,
        string secretName,
        RedactedSecret newValue,
        CiSecretMetadata? metadata = null,
        CancellationToken ct = default) =>
        // Rotation = same wire shape as provision; the audit-event
        // type is emitted by the caller (rotation cascade subscriber).
        FanOutAsync(scope, targets, secretName, newValue, "rotate", ct);

    public async Task<IReadOnlyList<CiSecretProvisionResult>> DeleteSecretAsync(
        CiSecretScope scope,
        IReadOnlyList<CiSecretTarget> targets,
        string secretName,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretName);
        ArgumentNullException.ThrowIfNull(targets);

        if (targets.Count == 0)
        {
            return Array.Empty<CiSecretProvisionResult>();
        }

        var results = new CiSecretProvisionResult[targets.Count];
        using var gate = new SemaphoreSlim(_maxConcurrency);
        var tasks = new List<Task>(targets.Count);

        for (int i = 0; i < targets.Count; i++)
        {
            var index = i;
            var target = targets[i];
            tasks.Add(Task.Run(async () =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (!TryRouteSecretsEndpoint(scope, target, secretName,
                            isPublicKey: false, out var endpoint, out var routeError))
                    {
                        results[index] = CiSecretProvisionResult.Failed(
                            Kind, target, routeError!);
                        return;
                    }

                    using var req = new HttpRequestMessage(
                        HttpMethod.Delete, endpoint);
                    using var resp = await _http
                        .SendAsync(req, ct).ConfigureAwait(false);

                    // 204 + 404 both treated as success (idempotent delete).
                    if (resp.StatusCode == HttpStatusCode.NoContent
                        || resp.StatusCode == HttpStatusCode.NotFound)
                    {
                        results[index] = CiSecretProvisionResult.Ok(Kind, target);
                        return;
                    }

                    results[index] = MapHttpFailure(target, resp);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "GitHub secret delete failed for {Descriptor} secret {SecretName}",
                        target.Descriptor(), secretName);
                    results[index] = CiSecretProvisionResult.Failed(
                        Kind, target, $"unknown:{ex.GetType().Name}");
                }
                finally
                {
                    gate.Release();
                }
            }, ct));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return results;
    }

    public async Task<PlatformResult<IReadOnlyList<CiSecretMetadataItem>>> ListSecretsAsync(
        CiSecretScope scope,
        CiSecretTarget target,
        CancellationToken ct = default)
    {
        if (!TryRouteListEndpoint(scope, target, out var endpoint, out var routeError))
        {
            return routeError == "scope_not_supported_on_platform"
                ? new PlatformResult<IReadOnlyList<CiSecretMetadataItem>>.Failed(
                    new PlatformError.InvalidRequest(routeError, null))
                : new PlatformResult<IReadOnlyList<CiSecretMetadataItem>>.Failed(
                    new PlatformError.InvalidRequest(routeError ?? "unknown", null));
        }

        try
        {
            using var resp = await _http
                .GetAsync(endpoint, ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                return new PlatformResult<IReadOnlyList<CiSecretMetadataItem>>.Failed(
                    HttpStatusToPlatformError(resp));
            }

            var stream = await resp.Content
                .ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument
                .ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            var items = new List<CiSecretMetadataItem>();
            if (doc.RootElement.TryGetProperty("secrets", out var arr)
                && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in arr.EnumerateArray())
                {
                    var name = element.GetProperty("name").GetString() ?? "";
                    DateTimeOffset? updated = null;
                    if (element.TryGetProperty("updated_at", out var uat)
                        && uat.ValueKind == JsonValueKind.String
                        && DateTimeOffset.TryParse(uat.GetString(), out var parsed))
                    {
                        updated = parsed;
                    }
                    items.Add(new CiSecretMetadataItem(
                        Name: name,
                        Scope: scope,
                        TargetDescriptor: target.Descriptor(),
                        UpdatedAtUtc: updated));
                }
            }

            return PlatformResult<IReadOnlyList<CiSecretMetadataItem>>
                .FromOk(items);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "GitHub list secrets failed for {Descriptor}", target.Descriptor());
            return new PlatformResult<IReadOnlyList<CiSecretMetadataItem>>.Failed(
                new PlatformError.Unknown(ex.GetType().Name));
        }
    }

    // ─────────────────────────────────────────────────────────────────

    private async Task<IReadOnlyList<CiSecretProvisionResult>> FanOutAsync(
        CiSecretScope scope,
        IReadOnlyList<CiSecretTarget> targets,
        string secretName,
        RedactedSecret secretValue,
        string opLabel,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretName);
        ArgumentNullException.ThrowIfNull(targets);

        if (targets.Count == 0)
        {
            return Array.Empty<CiSecretProvisionResult>();
        }

        var results = new CiSecretProvisionResult[targets.Count];
        using var gate = new SemaphoreSlim(_maxConcurrency);
        var tasks = new List<Task>(targets.Count);

        for (int i = 0; i < targets.Count; i++)
        {
            var index = i;
            var target = targets[i];
            tasks.Add(Task.Run(async () =>
            {
                await gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    results[index] = await WriteOneAsync(
                        scope, target, secretName, secretValue, opLabel, ct)
                        .ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }
            }, ct));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return results;
    }

    private async Task<CiSecretProvisionResult> WriteOneAsync(
        CiSecretScope scope,
        CiSecretTarget target,
        string secretName,
        RedactedSecret secretValue,
        string opLabel,
        CancellationToken ct)
    {
        try
        {
            // 1. Resolve the public-key endpoint for the scope+target.
            if (!TryRouteSecretsEndpoint(scope, target, secretName,
                    isPublicKey: true, out var publicKeyEndpoint, out var routeError))
            {
                return CiSecretProvisionResult.Failed(Kind, target, routeError!);
            }

            // 2. Fetch the public key.
            using var keyReq = new HttpRequestMessage(
                HttpMethod.Get, publicKeyEndpoint);
            using var keyResp = await _http
                .SendAsync(keyReq, ct).ConfigureAwait(false);

            if (!keyResp.IsSuccessStatusCode)
            {
                return MapHttpFailure(target, keyResp);
            }

            var keyJson = await keyResp.Content
                .ReadAsStringAsync(ct).ConfigureAwait(false);
            using var keyDoc = JsonDocument.Parse(keyJson);
            var publicKeyB64 = keyDoc.RootElement
                .GetProperty("key").GetString() ?? "";
            var keyId = keyDoc.RootElement
                .GetProperty("key_id").GetString() ?? "";

            if (string.IsNullOrEmpty(publicKeyB64) || string.IsNullOrEmpty(keyId))
            {
                return CiSecretProvisionResult.Failed(
                    Kind, target, "invalid_request:malformed_public_key");
            }

            // 3. Encrypt with libsodium sealed-box.
            var encrypted = EncryptSealedBox(publicKeyB64, secretValue.Reveal());

            // 4. PUT the encrypted value.
            if (!TryRouteSecretsEndpoint(scope, target, secretName,
                    isPublicKey: false, out var putEndpoint, out routeError))
            {
                return CiSecretProvisionResult.Failed(Kind, target, routeError!);
            }

            var payload = new
            {
                encrypted_value = encrypted,
                key_id = keyId,
            };
            using var content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var putReq = new HttpRequestMessage(HttpMethod.Put, putEndpoint)
            {
                Content = content,
            };
            using var putResp = await _http
                .SendAsync(putReq, ct).ConfigureAwait(false);

            if (!putResp.IsSuccessStatusCode)
            {
                return MapHttpFailure(target, putResp);
            }

            _logger.LogInformation(
                "GitHub secret {Op} succeeded — {Descriptor} secret {SecretName}",
                opLabel, target.Descriptor(), secretName);

            return CiSecretProvisionResult.Ok(Kind, target);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Defence: scrub the secret value from the exception
            // surface in case some library echoed it back. The
            // RedactedSecret wrapper protects against direct logging;
            // this guards against indirect leaks.
            var safeMessage = SecretLoggingScope.RedactSubstring(
                ex.Message ?? "", secretValue.Reveal());

            _logger.LogWarning(
                "GitHub secret {Op} failed — {Descriptor} secret {SecretName}: {Message}",
                opLabel, target.Descriptor(), secretName, safeMessage);

            return CiSecretProvisionResult.Failed(
                Kind, target, $"unknown:{ex.GetType().Name}");
        }
    }

    /// <summary>
    /// Sealed-box encrypt <paramref name="plaintext"/> with a standard
    /// base64-encoded X25519 public key (GitHub's wire format). Returns
    /// a standard base64 ciphertext, which GitHub expects in the
    /// <c>encrypted_value</c> field.
    /// </summary>
    internal static string EncryptSealedBox(string publicKeyBase64, string plaintext)
    {
        var publicKey = Convert.FromBase64String(publicKeyBase64);
        var messageBytes = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = SealedPublicKeyBox.Create(messageBytes, publicKey);
        return Convert.ToBase64String(encrypted);
    }

    private static bool TryRouteSecretsEndpoint(
        CiSecretScope scope,
        CiSecretTarget target,
        string secretName,
        bool isPublicKey,
        out string endpoint,
        out string? errorCode)
    {
        endpoint = "";
        errorCode = null;

        // GitHub: User and Global scopes are not supported.
        if (scope == CiSecretScope.User || scope == CiSecretScope.Global)
        {
            errorCode = "scope_not_supported_on_platform";
            return false;
        }

        switch ((scope, target))
        {
            case (CiSecretScope.Repo, CiSecretTarget.Repo r):
                endpoint = isPublicKey
                    ? $"/repos/{r.Owner}/{r.RepoName}/actions/secrets/public-key"
                    : $"/repos/{r.Owner}/{r.RepoName}/actions/secrets/{secretName}";
                return true;

            case (CiSecretScope.Org, CiSecretTarget.Org o):
                endpoint = isPublicKey
                    ? $"/orgs/{o.OrgOrGroup}/actions/secrets/public-key"
                    : $"/orgs/{o.OrgOrGroup}/actions/secrets/{secretName}";
                return true;

            case (CiSecretScope.Environment, CiSecretTarget.Environment e):
                endpoint = isPublicKey
                    ? $"/repos/{e.Owner}/{e.RepoName}/environments/{e.EnvironmentName}/secrets/public-key"
                    : $"/repos/{e.Owner}/{e.RepoName}/environments/{e.EnvironmentName}/secrets/{secretName}";
                return true;

            default:
                errorCode = "scope_target_mismatch";
                return false;
        }
    }

    private static bool TryRouteListEndpoint(
        CiSecretScope scope,
        CiSecretTarget target,
        out string endpoint,
        out string? errorCode)
    {
        endpoint = "";
        errorCode = null;

        if (scope == CiSecretScope.User || scope == CiSecretScope.Global)
        {
            errorCode = "scope_not_supported_on_platform";
            return false;
        }

        switch ((scope, target))
        {
            case (CiSecretScope.Repo, CiSecretTarget.Repo r):
                endpoint = $"/repos/{r.Owner}/{r.RepoName}/actions/secrets";
                return true;
            case (CiSecretScope.Org, CiSecretTarget.Org o):
                endpoint = $"/orgs/{o.OrgOrGroup}/actions/secrets";
                return true;
            case (CiSecretScope.Environment, CiSecretTarget.Environment e):
                endpoint = $"/repos/{e.Owner}/{e.RepoName}/environments/{e.EnvironmentName}/secrets";
                return true;
            default:
                errorCode = "scope_target_mismatch";
                return false;
        }
    }

    private static CiSecretProvisionResult MapHttpFailure(
        CiSecretTarget target, HttpResponseMessage resp)
    {
        var error = HttpStatusToPlatformError(resp);
        return CiSecretProvisionResult.FromError(PlatformKind.GitHub, target, error);
    }

    private static PlatformError HttpStatusToPlatformError(HttpResponseMessage resp) =>
        resp.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new PlatformError.AuthExpired(),
            HttpStatusCode.Forbidden => new PlatformError.PermissionDenied(),
            HttpStatusCode.NotFound => new PlatformError.NotFound(),
            HttpStatusCode.TooManyRequests => new PlatformError.RateLimited(
                TryParseRetryAfter(resp)),
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout => new PlatformError.ServiceUnavailable(),
            HttpStatusCode.UnprocessableEntity => new PlatformError.InvalidRequest(
                "validation", null),
            _ => new PlatformError.Unknown($"http_{(int)resp.StatusCode}"),
        };

    private static TimeSpan? TryParseRetryAfter(HttpResponseMessage resp)
    {
        if (resp.Headers.RetryAfter?.Delta is TimeSpan delta)
        {
            return delta;
        }
        if (resp.Headers.RetryAfter?.Date is DateTimeOffset date)
        {
            var diff = date - DateTimeOffset.UtcNow;
            return diff > TimeSpan.Zero ? diff : null;
        }
        return null;
    }
}
