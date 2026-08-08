using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Sodium;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Logging;

namespace Tamma.Platforms.Gitea;

/// <summary>
/// Story 31-8 — Gitea implementation of <see cref="ICiSecretsProvisioner"/>.
/// Gitea Actions adopted GitHub's wire format for repo + org secrets in
/// 1.21 (libsodium <c>crypto_box_seal</c> + base64-encoded ciphertext).
/// User-scope secrets are Gitea-specific. Environment secrets are not
/// supported on Gitea ≤ 1.25 — return
/// <c>scope_not_supported_on_platform</c>.
///
/// <para>Endpoints (Gitea API v1):</para>
/// <list type="bullet">
///   <item>Repo: <c>PUT /repos/{owner}/{repo}/actions/secrets/{name}</c></item>
///   <item>Org: <c>PUT /orgs/{org}/actions/secrets/{name}</c></item>
///   <item>User: <c>PUT /user/actions/secrets/{name}</c></item>
///   <item>Global: <c>PUT /admin/actions/secrets/{name}</c> (1.25+, requires
///         admin token; surfaced via PUT and let the platform 403 handle
///         the rest).</item>
///   <item>Environment: unsupported.</item>
/// </list>
///
/// <para>Wire shape mirrors GitHub: GET <c>secrets/public-key</c> →
/// encrypt with sealed-box → PUT <c>{ encrypted_value, key_id }</c>.
/// Gitea additionally accepts a plaintext fallback for org/user/global
/// secrets via <c>{ data: plaintext }</c>; we use the libsodium path
/// uniformly because it's the wire format Gitea documents and it
/// matches GitHub.</para>
/// </summary>
public sealed class GiteaCiSecretsProvisioner : ICiSecretsProvisioner
{
    private const int DefaultMaxConcurrency = 5;

    public PlatformKind Kind { get; }

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly Func<HttpRequestMessage, CancellationToken, Task<bool>> _authorizeAsync;
    private readonly ILogger<GiteaCiSecretsProvisioner> _logger;
    private readonly int _maxConcurrency;

    /// <summary>
    /// Epic 31 P4 M4 — constructed by the Gitea/Forgejo driver factories per
    /// installation (the P1 absorb pattern): <paramref name="baseUrl"/> is the
    /// instance root and <paramref name="authorizeAsync"/> applies the
    /// driver's credential (bot token / OAuth2 access token) per request,
    /// returning false when no credential could be resolved.
    /// </summary>
    public GiteaCiSecretsProvisioner(
        HttpClient http,
        string baseUrl,
        Func<HttpRequestMessage, CancellationToken, Task<bool>> authorizeAsync,
        ILogger<GiteaCiSecretsProvisioner>? logger = null,
        int maxConcurrency = DefaultMaxConcurrency,
        PlatformKind kind = PlatformKind.Gitea)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentNullException.ThrowIfNull(authorizeAsync);
        _baseUrl = baseUrl.TrimEnd('/');
        _authorizeAsync = authorizeAsync;
        if (kind != PlatformKind.Gitea && kind != PlatformKind.Forgejo)
        {
            throw new ArgumentException(
                "GiteaCiSecretsProvisioner only handles Gitea or Forgejo kinds.",
                nameof(kind));
        }
        _http = http;
        _logger = logger ?? NullLogger<GiteaCiSecretsProvisioner>.Instance;
        _maxConcurrency = maxConcurrency > 0 ? maxConcurrency : DefaultMaxConcurrency;
        Kind = kind;
    }

    /// <summary>Authorize + send (Epic 31 P4 M4).</summary>
    private async Task<HttpResponseMessage> SendAuthorizedAsync(
        HttpRequestMessage req, CancellationToken ct)
    {
        if (!await _authorizeAsync(req, ct).ConfigureAwait(false))
        {
            throw new CredentialUnavailableException();
        }
        return await _http.SendAsync(req, ct).ConfigureAwait(false);
    }

    internal sealed class CredentialUnavailableException : Exception { }

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
        FanOutAsync(scope, targets, secretName, newValue, "rotate", ct);

    public async Task<IReadOnlyList<CiSecretProvisionResult>> DeleteSecretAsync(
        CiSecretScope scope,
        IReadOnlyList<CiSecretTarget> targets,
        string secretName,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretName);
        ArgumentNullException.ThrowIfNull(targets);

        if (targets.Count == 0) return Array.Empty<CiSecretProvisionResult>();

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
                    if (!TryRouteEndpoint(scope, target, secretName,
                            isPublicKey: false, out var endpoint, out var routeError))
                    {
                        results[index] = CiSecretProvisionResult.Failed(
                            Kind, target, routeError!);
                        return;
                    }

                    using var req = new HttpRequestMessage(
                        HttpMethod.Delete, _baseUrl + endpoint);
                    using var resp = await SendAuthorizedAsync(req, ct).ConfigureAwait(false);

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
                    _logger.LogWarning(ex,
                        "Gitea secret delete failed — {Descriptor} secret {SecretName}",
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

    public Task<PlatformResult<IReadOnlyList<CiSecretMetadataItem>>> ListSecretsAsync(
        CiSecretScope scope,
        CiSecretTarget target,
        CancellationToken ct = default)
    {
        // Gitea ≤ 1.25 does not surface a "list user secrets" endpoint;
        // org + repo support a list call but it's not consistent across
        // versions. Return ServiceUnavailable so callers know to fall
        // back to a known-secret-name probe.
        return Task.FromResult(
            PlatformResult<IReadOnlyList<CiSecretMetadataItem>>
                .FromServiceUnavailable());
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

        if (targets.Count == 0) return Array.Empty<CiSecretProvisionResult>();

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
            // Environment scope is not supported on Gitea.
            if (scope == CiSecretScope.Environment)
            {
                return CiSecretProvisionResult.Failed(
                    Kind, target, "scope_not_supported_on_platform");
            }

            // 1. Fetch public key.
            if (!TryRouteEndpoint(scope, target, secretName,
                    isPublicKey: true, out var publicKeyEndpoint, out var routeError))
            {
                return CiSecretProvisionResult.Failed(Kind, target, routeError!);
            }

            using var keyReq = new HttpRequestMessage(
                HttpMethod.Get, _baseUrl + publicKeyEndpoint);
            using var keyResp = await SendAuthorizedAsync(keyReq, ct).ConfigureAwait(false);

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

            // 2. Encrypt sealed-box.
            var encrypted = EncryptSealedBox(publicKeyB64, secretValue.Reveal());

            // 3. PUT.
            if (!TryRouteEndpoint(scope, target, secretName,
                    isPublicKey: false, out var putEndpoint, out routeError))
            {
                return CiSecretProvisionResult.Failed(Kind, target, routeError!);
            }

            var payload = new { encrypted_value = encrypted, key_id = keyId };
            using var content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var putReq = new HttpRequestMessage(HttpMethod.Put, _baseUrl + putEndpoint)
            {
                Content = content,
            };
            using var putResp = await SendAuthorizedAsync(putReq, ct).ConfigureAwait(false);

            if (!putResp.IsSuccessStatusCode)
            {
                return MapHttpFailure(target, putResp);
            }

            _logger.LogInformation(
                "{Kind} secret {Op} succeeded — {Descriptor} secret {SecretName}",
                Kind, opLabel, target.Descriptor(), secretName);

            return CiSecretProvisionResult.Ok(Kind, target);
        }
        catch (OperationCanceledException) { throw; }
        catch (CredentialUnavailableException)
        {
            return CiSecretProvisionResult.Failed(Kind, target, "auth_unavailable");
        }
        catch (Exception ex)
        {
            var safeMessage = SecretLoggingScope.RedactSubstring(
                ex.Message ?? "", secretValue.Reveal());
            _logger.LogWarning(
                "{Kind} secret {Op} failed — {Descriptor} secret {SecretName}: {Message}",
                Kind, opLabel, target.Descriptor(), secretName, safeMessage);
            return CiSecretProvisionResult.Failed(
                Kind, target, $"unknown:{ex.GetType().Name}");
        }
    }

    /// <summary>
    /// Sealed-box encrypt — mirrors the GitHub provisioner's helper.
    /// Gitea uses identical wire format (1.21+).
    /// </summary>
    public static string EncryptSealedBox(string publicKeyBase64, string plaintext)
    {
        var publicKey = Convert.FromBase64String(publicKeyBase64);
        var messageBytes = Encoding.UTF8.GetBytes(plaintext);
        var encrypted = SealedPublicKeyBox.Create(messageBytes, publicKey);
        return Convert.ToBase64String(encrypted);
    }

    private static bool TryRouteEndpoint(
        CiSecretScope scope,
        CiSecretTarget target,
        string secretName,
        bool isPublicKey,
        out string endpoint,
        out string? errorCode)
    {
        endpoint = "";
        errorCode = null;

        // Gitea: Environment unsupported.
        if (scope == CiSecretScope.Environment)
        {
            errorCode = "scope_not_supported_on_platform";
            return false;
        }

        switch ((scope, target))
        {
            case (CiSecretScope.Repo, CiSecretTarget.Repo r):
                endpoint = isPublicKey
                    ? $"/api/v1/repos/{r.Owner}/{r.RepoName}/actions/secrets/public-key"
                    : $"/api/v1/repos/{r.Owner}/{r.RepoName}/actions/secrets/{secretName}";
                return true;
            case (CiSecretScope.Org, CiSecretTarget.Org o):
                endpoint = isPublicKey
                    ? $"/api/v1/orgs/{o.OrgOrGroup}/actions/secrets/public-key"
                    : $"/api/v1/orgs/{o.OrgOrGroup}/actions/secrets/{secretName}";
                return true;
            case (CiSecretScope.User, CiSecretTarget.User _):
                endpoint = isPublicKey
                    ? "/api/v1/user/actions/secrets/public-key"
                    : $"/api/v1/user/actions/secrets/{secretName}";
                return true;
            case (CiSecretScope.Global, CiSecretTarget.Global):
                endpoint = isPublicKey
                    ? "/api/v1/admin/actions/secrets/public-key"
                    : $"/api/v1/admin/actions/secrets/{secretName}";
                return true;
            default:
                errorCode = "scope_target_mismatch";
                return false;
        }
    }

    private CiSecretProvisionResult MapHttpFailure(
        CiSecretTarget target, HttpResponseMessage resp) =>
        CiSecretProvisionResult.FromError(
            Kind, target, HttpStatusToPlatformError(resp));

    private static PlatformError HttpStatusToPlatformError(HttpResponseMessage resp) =>
        resp.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new PlatformError.AuthExpired(),
            HttpStatusCode.Forbidden => new PlatformError.PermissionDenied(),
            HttpStatusCode.NotFound => new PlatformError.NotFound(),
            HttpStatusCode.TooManyRequests => new PlatformError.RateLimited(null),
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout => new PlatformError.ServiceUnavailable(),
            HttpStatusCode.UnprocessableEntity => new PlatformError.InvalidRequest(
                "validation", null),
            _ => new PlatformError.Unknown($"http_{(int)resp.StatusCode}"),
        };
}

/// <summary>
/// Forgejo retains Gitea's API contract — see Story 31-5's compat
/// matrix. The provisioner is a thin wrapper that stamps the
/// <see cref="PlatformKind.Forgejo"/> kind on results.
/// </summary>
public sealed class ForgejoCiSecretsProvisioner : ICiSecretsProvisioner
{
    private readonly GiteaCiSecretsProvisioner _inner;
    public PlatformKind Kind => PlatformKind.Forgejo;

    public ForgejoCiSecretsProvisioner(
        HttpClient http,
        string baseUrl,
        Func<HttpRequestMessage, CancellationToken, Task<bool>> authorizeAsync,
        ILogger<GiteaCiSecretsProvisioner>? logger = null,
        int maxConcurrency = 5)
    {
        _inner = new GiteaCiSecretsProvisioner(
            http, baseUrl, authorizeAsync, logger, maxConcurrency, PlatformKind.Forgejo);
    }

    public Task<IReadOnlyList<CiSecretProvisionResult>> ProvisionSecretAsync(
        CiSecretScope scope,
        IReadOnlyList<CiSecretTarget> targets,
        string secretName,
        RedactedSecret secretValue,
        CiSecretMetadata? metadata = null,
        CancellationToken ct = default) =>
        _inner.ProvisionSecretAsync(scope, targets, secretName, secretValue, metadata, ct);

    public Task<IReadOnlyList<CiSecretProvisionResult>> RotateSecretAsync(
        CiSecretScope scope,
        IReadOnlyList<CiSecretTarget> targets,
        string secretName,
        RedactedSecret newValue,
        CiSecretMetadata? metadata = null,
        CancellationToken ct = default) =>
        _inner.RotateSecretAsync(scope, targets, secretName, newValue, metadata, ct);

    public Task<IReadOnlyList<CiSecretProvisionResult>> DeleteSecretAsync(
        CiSecretScope scope,
        IReadOnlyList<CiSecretTarget> targets,
        string secretName,
        CancellationToken ct = default) =>
        _inner.DeleteSecretAsync(scope, targets, secretName, ct);

    public Task<PlatformResult<IReadOnlyList<CiSecretMetadataItem>>> ListSecretsAsync(
        CiSecretScope scope,
        CiSecretTarget target,
        CancellationToken ct = default) =>
        _inner.ListSecretsAsync(scope, target, ct);
}
