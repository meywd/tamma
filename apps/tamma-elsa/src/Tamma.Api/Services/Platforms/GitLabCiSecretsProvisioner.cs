using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Tamma.Platforms.Abstractions;
using Tamma.Platforms.Abstractions.Logging;

namespace Tamma.Api.Services.Platforms;

/// <summary>
/// Story 31-8 — GitLab implementation of
/// <see cref="ICiSecretsProvisioner"/>. GitLab encrypts variables at
/// rest itself; the wire format is plaintext over HTTPS — no
/// client-side libsodium step.
///
/// <para>Endpoints:</para>
/// <list type="bullet">
///   <item><see cref="CiSecretScope.Repo"/> →
///         <c>POST /api/v4/projects/{pid}/variables</c></item>
///   <item><see cref="CiSecretScope.Org"/> (mapped to GitLab group) →
///         <c>POST /api/v4/groups/{gid}/variables</c></item>
///   <item><see cref="CiSecretScope.Environment"/> → same as Repo,
///         with non-null <c>environment_scope</c>.</item>
///   <item><see cref="CiSecretScope.User"/> /
///         <see cref="CiSecretScope.Global"/> →
///         <c>scope_not_supported_on_platform</c>.</item>
/// </list>
///
/// <para>Honoured metadata fields:</para>
/// <list type="bullet">
///   <item><c>protected</c> — only exposed to runs on protected refs.</item>
///   <item><c>masked</c> — server-side log scrubbing. GitLab enforces
///         strict character-set rules on masked values; we pre-validate
///         via <see cref="MaskedVariableValidator"/> so a known-bad
///         value fails fast with <c>masked_value_invalid:&lt;rule&gt;</c>
///         rather than waiting for GitLab's 400.</item>
///   <item><c>environment_scope</c> — wildcard match for the
///         deploy-env binding.</item>
///   <item><c>variable_type</c> — <c>env_var</c> (default) or <c>file</c>.</item>
/// </list>
/// </summary>
public sealed class GitLabCiSecretsProvisioner : ICiSecretsProvisioner
{
    private const int DefaultMaxConcurrency = 5;

    public PlatformKind Kind => PlatformKind.GitLab;

    private readonly HttpClient _http;
    private readonly ILogger<GitLabCiSecretsProvisioner> _logger;
    private readonly int _maxConcurrency;

    public GitLabCiSecretsProvisioner(
        HttpClient http,
        ILogger<GitLabCiSecretsProvisioner>? logger = null,
        int maxConcurrency = DefaultMaxConcurrency)
    {
        ArgumentNullException.ThrowIfNull(http);
        _http = http;
        _logger = logger ?? NullLogger<GitLabCiSecretsProvisioner>.Instance;
        _maxConcurrency = maxConcurrency > 0 ? maxConcurrency : DefaultMaxConcurrency;
    }

    public Task<IReadOnlyList<CiSecretProvisionResult>> ProvisionSecretAsync(
        CiSecretScope scope,
        IReadOnlyList<CiSecretTarget> targets,
        string secretName,
        RedactedSecret secretValue,
        CiSecretMetadata? metadata = null,
        CancellationToken ct = default) =>
        FanOutAsync(scope, targets, secretName, secretValue,
            metadata ?? CiSecretMetadata.Default, isCreate: true, ct);

    public Task<IReadOnlyList<CiSecretProvisionResult>> RotateSecretAsync(
        CiSecretScope scope,
        IReadOnlyList<CiSecretTarget> targets,
        string secretName,
        RedactedSecret newValue,
        CiSecretMetadata? metadata = null,
        CancellationToken ct = default) =>
        FanOutAsync(scope, targets, secretName, newValue,
            metadata ?? CiSecretMetadata.Default, isCreate: false, ct);

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
                            isCollection: false, out var endpoint, out var routeError))
                    {
                        results[index] = CiSecretProvisionResult.Failed(
                            Kind, target, routeError!);
                        return;
                    }

                    using var req = new HttpRequestMessage(
                        HttpMethod.Delete, endpoint);
                    using var resp = await _http
                        .SendAsync(req, ct).ConfigureAwait(false);

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
                        "GitLab variable delete failed — {Descriptor} key {Key}",
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
        if (!TryRouteEndpoint(scope, target, secretName: "",
                isCollection: true, out var endpoint, out var routeError))
        {
            return new PlatformResult<IReadOnlyList<CiSecretMetadataItem>>.Failed(
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

            await using var stream = await resp.Content
                .ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument
                .ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

            var items = new List<CiSecretMetadataItem>();
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in doc.RootElement.EnumerateArray())
                {
                    var key = element.TryGetProperty("key", out var k)
                        ? k.GetString() ?? "" : "";
                    items.Add(new CiSecretMetadataItem(
                        Name: key,
                        Scope: scope,
                        TargetDescriptor: target.Descriptor(),
                        UpdatedAtUtc: null));
                }
            }
            return PlatformResult<IReadOnlyList<CiSecretMetadataItem>>.FromOk(items);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "GitLab list variables failed — {Descriptor}", target.Descriptor());
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
        CiSecretMetadata metadata,
        bool isCreate,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secretName);
        ArgumentNullException.ThrowIfNull(targets);

        if (targets.Count == 0) return Array.Empty<CiSecretProvisionResult>();

        // Pre-validate masked-value rules ONCE per call (the value
        // is the same for every target). Saves N round-trips when
        // the value is bad.
        if (metadata.Masked)
        {
            var validation = MaskedVariableValidator.Validate(secretValue.Reveal());
            if (validation is not null)
            {
                var pre = new CiSecretProvisionResult[targets.Count];
                for (int i = 0; i < targets.Count; i++)
                {
                    pre[i] = CiSecretProvisionResult.Failed(
                        Kind, targets[i], validation);
                }
                return pre;
            }
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
                        scope, target, secretName, secretValue,
                        metadata, isCreate, ct).ConfigureAwait(false);
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
        CiSecretMetadata metadata,
        bool isCreate,
        CancellationToken ct)
    {
        try
        {
            // Endpoint differs for create (POST collection) vs update
            // (PUT individual).
            if (!TryRouteEndpoint(scope, target, secretName,
                    isCollection: isCreate, out var endpoint, out var routeError))
            {
                return CiSecretProvisionResult.Failed(Kind, target, routeError!);
            }

            // Derive environment_scope from the target if not explicitly set.
            string? envScope = metadata.EnvironmentScope;
            if (envScope is null && target is CiSecretTarget.Environment env)
            {
                envScope = env.EnvironmentName;
            }

            var payload = new Dictionary<string, object?>
            {
                ["key"] = secretName,
                ["value"] = secretValue.Reveal(),
                ["protected"] = metadata.Protected,
                ["masked"] = metadata.Masked,
                ["variable_type"] = metadata.VariableType,
            };
            if (!string.IsNullOrEmpty(envScope))
            {
                payload["environment_scope"] = envScope;
            }

            using var content = new StringContent(
                JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var req = new HttpRequestMessage(
                isCreate ? HttpMethod.Post : HttpMethod.Put, endpoint)
            {
                Content = content,
            };
            using var resp = await _http
                .SendAsync(req, ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                return MapHttpFailure(target, resp);
            }

            _logger.LogInformation(
                "GitLab variable {Op} succeeded — {Descriptor} key {Key}",
                isCreate ? "create" : "update",
                target.Descriptor(), secretName);

            return CiSecretProvisionResult.Ok(Kind, target);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            var safeMessage = SecretLoggingScope.RedactSubstring(
                ex.Message ?? "", secretValue.Reveal());
            _logger.LogWarning(
                "GitLab variable write failed — {Descriptor} key {Key}: {Message}",
                target.Descriptor(), secretName, safeMessage);
            return CiSecretProvisionResult.Failed(
                Kind, target, $"unknown:{ex.GetType().Name}");
        }
    }

    private static bool TryRouteEndpoint(
        CiSecretScope scope,
        CiSecretTarget target,
        string secretName,
        bool isCollection,
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
            case (CiSecretScope.Environment, CiSecretTarget.Environment _) when target is CiSecretTarget.Environment:
                if (target is CiSecretTarget.Repo repo)
                {
                    var pid = Uri.EscapeDataString($"{repo.Owner}/{repo.RepoName}");
                    endpoint = isCollection
                        ? $"/api/v4/projects/{pid}/variables"
                        : $"/api/v4/projects/{pid}/variables/{secretName}";
                    return true;
                }
                if (target is CiSecretTarget.Environment env)
                {
                    var pid2 = Uri.EscapeDataString($"{env.Owner}/{env.RepoName}");
                    endpoint = isCollection
                        ? $"/api/v4/projects/{pid2}/variables"
                        : $"/api/v4/projects/{pid2}/variables/{secretName}";
                    return true;
                }
                errorCode = "scope_target_mismatch";
                return false;

            case (CiSecretScope.Org, CiSecretTarget.Org o):
                var gid = Uri.EscapeDataString(o.OrgOrGroup);
                endpoint = isCollection
                    ? $"/api/v4/groups/{gid}/variables"
                    : $"/api/v4/groups/{gid}/variables/{secretName}";
                return true;

            default:
                errorCode = "scope_target_mismatch";
                return false;
        }
    }

    private static CiSecretProvisionResult MapHttpFailure(
        CiSecretTarget target, HttpResponseMessage resp) =>
        CiSecretProvisionResult.FromError(
            PlatformKind.GitLab, target, HttpStatusToPlatformError(resp));

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
            HttpStatusCode.BadRequest => new PlatformError.InvalidRequest(
                "validation", null),
            _ => new PlatformError.Unknown($"http_{(int)resp.StatusCode}"),
        };
}

/// <summary>
/// Story 31-8 — client-side enforcement of GitLab's
/// <a href="https://docs.gitlab.com/ee/ci/variables/#mask-a-cicd-variable">
/// masked-variable rules</a>. Saves the round-trip when the value is
/// known to fail server-side validation.
///
/// <para>Rules:</para>
/// <list type="bullet">
///   <item>Length must be at least 8 characters.</item>
///   <item>Must NOT contain newlines.</item>
///   <item>Must contain only base64-friendly characters: <c>A-Z</c>,
///         <c>a-z</c>, <c>0-9</c>, plus <c>+</c>, <c>/</c>, <c>=</c>,
///         <c>@</c>, <c>:</c>, <c>.</c>, <c>~</c>, <c>_</c>, <c>-</c>.</item>
/// </list>
/// </summary>
public static class MaskedVariableValidator
{
    private static readonly Regex AllowedCharsetPattern =
        new(@"^[A-Za-z0-9+/=@:.~_\-]+$", RegexOptions.Compiled);

    /// <summary>
    /// Returns null when the value is acceptable, or a stable
    /// <c>"masked_value_invalid:&lt;rule&gt;"</c> code on rejection.
    /// </summary>
    public static string? Validate(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "masked_value_invalid:length";
        }
        if (value.Length < 8)
        {
            return "masked_value_invalid:length";
        }
        if (value.Contains('\n') || value.Contains('\r'))
        {
            return "masked_value_invalid:newlines";
        }
        if (!AllowedCharsetPattern.IsMatch(value))
        {
            return "masked_value_invalid:charset";
        }
        return null;
    }
}
