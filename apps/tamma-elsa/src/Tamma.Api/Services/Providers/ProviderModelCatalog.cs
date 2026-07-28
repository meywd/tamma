using System.Collections.Concurrent;
using System.Text.Json;
using Tamma.Activities.LlmCall.Credentials;
using Tamma.Core;

namespace Tamma.Api.Services.Providers;

/// <summary>One normalized model entry (Story 46-0 AC2). The shape carries
/// deliberately NO capability metadata (context windows, pricing) — epic 46
/// "Out of scope".</summary>
/// <param name="Id">The provider's model id, VERBATIM (gemini's
/// <c>models/gemini-…</c> ids are kept as-is — they are what that chat surface
/// accepts).</param>
/// <param name="DisplayName">From <c>display_name</c> (Anthropic, Together) or
/// <c>name</c> (OpenRouter) when present; null when the id is the name
/// (OpenAI, Groq, DeepSeek).</param>
/// <param name="Deprecated">True only when the provider payload carries a
/// truthy <c>deprecated</c> field — none of the surveyed providers does today;
/// the field exists so the shape doesn't change when one starts.</param>
public sealed record ProviderModelInfo(string Id, string? DisplayName, bool Deprecated);

/// <summary>
/// Fail-soft envelope for a provider's model list (Story 46-0 AC2/AC3, epic
/// D6). Always returned — never an exception: a fresh list, a stale cached
/// list flagged <see cref="Stale"/> with an <see cref="ErrorCode"/>, or an
/// empty list with the <see cref="ErrorCode"/>.
/// </summary>
public sealed record ProviderModelList(
    IReadOnlyList<ProviderModelInfo> Models,
    DateTimeOffset? FetchedAt,
    bool Stale,
    string? ErrorCode);

/// <summary>
/// Story 46-0 — the ONE live model-listing seam both UIs (46-2 admin console,
/// 46-3 customer app) and the admin/tenant model routes bind to. Fetches a
/// provider's model list from the provider's OWN models endpoint through the
/// existing Phase 1 plumbing, normalizes to <see cref="ProviderModelInfo"/>,
/// caches for 5 minutes, and NEVER throws for a known provider (epic D6).
/// </summary>
public interface IProviderModelCatalog
{
    /// <summary>
    /// List the live models for <paramref name="providerKey"/> (canonical key
    /// or alias). <paramref name="tenantId"/> null = platform view (platform
    /// key); non-null = tenant view — the tenant's BYOK key is preferred when
    /// present (epic D5, via <c>IProviderCredentialResolver</c>'s existing
    /// order). The cache is keyed per (provider, tenant) so a
    /// BYOK-entitlement-filtered list can never leak across tenants.
    /// </summary>
    Task<ProviderModelList> ListModelsAsync(
        string providerKey, Guid? tenantId, CancellationToken ct = default);

    /// <summary>
    /// Review F12 — evict the cached model list for
    /// (<paramref name="providerKey"/>, <paramref name="tenantId"/>). Called by
    /// the BYOK credential mutations (register / rotate / delete) alongside
    /// <c>IProviderCredentialResolver.Invalidate</c>: a list fetched under the
    /// OLD credential (e.g. the platform key, or a revoked BYOK key) must not
    /// keep serving for up to <see cref="ProviderModelCatalogService.CacheTtl"/>
    /// after the tenant's credential changed. Accepts aliases; no-op when
    /// nothing is cached for the pair.
    /// </summary>
    void Invalidate(string providerKey, Guid? tenantId);
}

/// <inheritdoc />
/// <remarks>
/// <para><b>Fetch composition (46-0 plan D3):</b> copies
/// <c>InlineToolLoopRunner</c>'s proven per-call pattern — the request rides
/// the deliberately-UNCONFIGURED runner client
/// (<c>InlineToolLoopRunner.RunnerHttpClientName</c>) with an absolute URL
/// from <see cref="ProviderCatalog.CombineUrl"/> and headers applied per call
/// from the descriptor (<c>AuthScheme</c> + version header) + the resolved
/// credential. It never rides the config-key-baked named clients — the
/// platform key increasingly lives in the secret cabinet, not config, and a
/// tenant-scoped fetch must not silently use the platform config key when
/// BYOK resolution succeeded. The BASE URL still honours the named client's
/// <c>BaseAddress</c> (where <c>{Section}:BaseUrl</c> overrides land),
/// falling back to the descriptor default — the same effective base
/// resolution as <c>HttpProviderClient.InvokeAsync</c>.</para>
/// <para><b>Credential safety (AC7):</b> the resolved key is applied to the
/// outbound request headers and immediately discarded. It is never logged
/// (logs carry provider key, status code and duration only), never cached,
/// and never present on any DTO this service returns.</para>
/// </remarks>
public sealed class ProviderModelCatalogService : IProviderModelCatalog
{
    /// <summary>5-minute cache TTL (AC3).</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);

    /// <summary>Per-fetch timeout (AC2: ≤ 10 s).</summary>
    public static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 46-0 plan D4 — providers whose list endpoint is callable WITHOUT a
    /// credential: OpenRouter's models API is public (survey 2026-07-27), and
    /// the local-server family (Ollama's OpenAI-compat layer, LM Studio) has
    /// no auth at all. For these, a PROVIDER_CREDENTIAL_UNAVAILABLE from the
    /// resolver downgrades to an unauthenticated fetch attempt. Every other
    /// provider short-circuits to the fail-soft envelope instead — an
    /// unauthenticated call would 401 and burn the 10 s timeout.
    /// </summary>
    internal static readonly IReadOnlySet<string> KeyOptionalProviders =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "openrouter", "local-llm", "ollama", "lmstudio",
        };

    /// <summary>Stable machine-readable error codes for the envelope.</summary>
    internal static class ErrorCodes
    {
        public const string ModelsNotSupported = "models_not_supported";
        public const string CredentialUnavailable = "credential_unavailable";
        public const string BaseUrlMissing = "base_url_missing";
        public const string FetchFailed = "fetch_failed";
        public const string ParseFailed = "parse_failed";
    }

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IProviderCredentialResolver _credentials;
    private readonly ILogger<ProviderModelCatalogService> _logger;
    private readonly TimeProvider _timeProvider;

    private readonly ConcurrentDictionary<(string Provider, Guid? TenantId), CacheEntry> _cache = new();

    public ProviderModelCatalogService(
        IHttpClientFactory httpClientFactory,
        IProviderCredentialResolver credentials,
        ILogger<ProviderModelCatalogService> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClientFactory = httpClientFactory;
        _credentials = credentials;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<ProviderModelList> ListModelsAsync(
        string providerKey, Guid? tenantId, CancellationToken ct = default)
    {
        var descriptor = ProviderCatalog.Resolve(providerKey);
        if (descriptor is null || descriptor.ModelsEndpointPath is null)
        {
            // Unknown / non-HTTP / unlistable (z-ai, azure-openai,
            // github-copilot) — the UIs fall back to free text (epic D4).
            return new ProviderModelList(
                Array.Empty<ProviderModelInfo>(), FetchedAt: null,
                Stale: false, ErrorCode: ErrorCodes.ModelsNotSupported);
        }

        var cacheKey = (descriptor.Key, tenantId);
        var now = _timeProvider.GetUtcNow();
        if (_cache.TryGetValue(cacheKey, out var cached) && now - cached.FetchedAt < CacheTtl)
        {
            return new ProviderModelList(cached.Models, cached.FetchedAt, Stale: false, ErrorCode: null);
        }

        // 1) Credential — BYOK-preferred for tenant callers, platform for null
        //    (epic D5; the resolver already implements exactly that order).
        string? apiKey = null;
        try
        {
            var cred = await _credentials.ResolveAsync(tenantId, descriptor.Key, ct)
                .ConfigureAwait(false);
            apiKey = cred.ApiKey; // header-only; discarded below
        }
        catch (OperationCanceledException) { throw; }
        catch (TammaError ex) when (ex.Code == "PROVIDER_CREDENTIAL_UNAVAILABLE")
        {
            if (!KeyOptionalProviders.Contains(descriptor.Key))
            {
                // Fail-soft, no HTTP attempt (plan D4) — but serve the last
                // known-good list when one exists.
                return StaleOrEmpty(cacheKey, ErrorCodes.CredentialUnavailable);
            }
            // Key-optional (openrouter / local family): unauthenticated fetch.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Credential resolution failed for provider {Provider}; serving fail-soft models envelope.",
                descriptor.Key);
            if (!KeyOptionalProviders.Contains(descriptor.Key))
            {
                return StaleOrEmpty(cacheKey, ErrorCodes.CredentialUnavailable);
            }
        }

        // 2) Effective base URL — named client BaseAddress (config override
        //    landing spot) else descriptor default; same resolution as
        //    HttpProviderClient.InvokeAsync.
        var namedClient = _httpClientFactory.CreateClient(descriptor.HttpClientName);
        var baseUrl = namedClient.BaseAddress?.ToString();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            baseUrl = descriptor.DefaultBaseUrl;
        }
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return StaleOrEmpty(cacheKey, ErrorCodes.BaseUrlMissing);
        }

        var path = ProviderCatalog.ModelsPathForBase(descriptor, baseUrl);
        if (path is null)
        {
            return new ProviderModelList(
                Array.Empty<ProviderModelInfo>(), FetchedAt: null,
                Stale: false, ErrorCode: ErrorCodes.ModelsNotSupported);
        }
        var requestUri = ProviderCatalog.CombineUrl(baseUrl, path);

        // 3) Fetch on the UNCONFIGURED runner-style client, headers per call.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var httpClient = _httpClientFactory.CreateClient(
                Agents.InlineToolLoopRunner.RunnerHttpClientName);

            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                switch (descriptor.AuthScheme)
                {
                    case ProviderAuthScheme.AnthropicApiKey:
                        request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
                        break;
                    default:
                        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
                        break;
                }
            }
            if (descriptor.VersionHeaderName is not null)
            {
                request.Headers.TryAddWithoutValidation(
                    descriptor.VersionHeaderName, descriptor.VersionHeaderValue);
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(FetchTimeout);

            using var response = await httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
                .ConfigureAwait(false);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Models fetch for {Provider} returned HTTP {Status} in {Elapsed}ms.",
                    descriptor.Key, (int)response.StatusCode, sw.ElapsedMilliseconds);
                return StaleOrEmpty(cacheKey, ErrorCodes.FetchFailed);
            }

            using var body = await response.Content
                .ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
            using var doc = await JsonDocument
                .ParseAsync(body, cancellationToken: timeoutCts.Token).ConfigureAwait(false);

            var models = ParseModels(doc.RootElement);
            _logger.LogInformation(
                "Models fetch for {Provider}: {Count} models in {Elapsed}ms.",
                descriptor.Key, models.Count, sw.ElapsedMilliseconds);

            var fetchedAt = _timeProvider.GetUtcNow();
            _cache[cacheKey] = new CacheEntry(models, fetchedAt);
            EvictAncientEntries(fetchedAt);
            return new ProviderModelList(models, fetchedAt, Stale: false, ErrorCode: null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            _logger.LogWarning(
                "Models fetch for {Provider} timed out after {Elapsed}ms.",
                descriptor.Key, sw.ElapsedMilliseconds);
            return StaleOrEmpty(cacheKey, ErrorCodes.FetchFailed);
        }
        catch (OperationCanceledException) { throw; }
        catch (JsonException ex)
        {
            sw.Stop();
            _logger.LogWarning(ex,
                "Models fetch for {Provider} returned an unparseable body ({Elapsed}ms).",
                descriptor.Key, sw.ElapsedMilliseconds);
            return StaleOrEmpty(cacheKey, ErrorCodes.ParseFailed);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex,
                "Models fetch for {Provider} failed after {Elapsed}ms.",
                descriptor.Key, sw.ElapsedMilliseconds);
            return StaleOrEmpty(cacheKey, ErrorCodes.FetchFailed);
        }
    }

    /// <summary>
    /// AC2 — the parser owns exactly TWO envelope shapes (survey 2026-07-27):
    /// a root object with a <c>data</c> array (everything else) and a root
    /// BARE array (Together). Entries without a string <c>id</c> are skipped,
    /// not fatal. Display name from <c>display_name</c> (Anthropic, Together)
    /// ?? <c>name</c> (OpenRouter); <c>deprecated</c> read when present.
    /// </summary>
    internal static IReadOnlyList<ProviderModelInfo> ParseModels(JsonElement root)
    {
        JsonElement array;
        if (root.ValueKind == JsonValueKind.Array)
        {
            array = root; // Together's bare-array shape
        }
        else if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("data", out var data)
            && data.ValueKind == JsonValueKind.Array)
        {
            array = data;
        }
        else
        {
            return Array.Empty<ProviderModelInfo>();
        }

        var models = new List<ProviderModelInfo>();
        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (!entry.TryGetProperty("id", out var idEl)
                || idEl.ValueKind != JsonValueKind.String)
            {
                continue; // no string id — skipped, never fatal
            }
            var id = idEl.GetString();
            if (string.IsNullOrWhiteSpace(id)) continue;

            string? displayName = null;
            if (entry.TryGetProperty("display_name", out var dn)
                && dn.ValueKind == JsonValueKind.String)
            {
                displayName = dn.GetString();
            }
            else if (entry.TryGetProperty("name", out var n)
                && n.ValueKind == JsonValueKind.String)
            {
                displayName = n.GetString();
            }

            var deprecated = entry.TryGetProperty("deprecated", out var dep)
                && dep.ValueKind == JsonValueKind.True;

            models.Add(new ProviderModelInfo(id!, displayName, deprecated));
        }

        return models;
    }

    /// <inheritdoc />
    public void Invalidate(string providerKey, Guid? tenantId)
    {
        // Alias-normalize the same way ListModelsAsync keys its cache, so
        // "kimi" evicts the moonshot entry. An unknown key normalizes to its
        // trimmed spelling — TryRemove is then a harmless no-op.
        var canonical = ProviderCatalog.Resolve(providerKey)?.Key
            ?? (providerKey ?? string.Empty).Trim().ToLowerInvariant();
        _cache.TryRemove((canonical, tenantId), out _);
    }

    /// <summary>Fail-soft: serve the last-known-good list flagged stale when
    /// one exists for this (provider, tenant) key; otherwise an empty list.
    /// Both carry the error code; both are HTTP-200 material (epic D6).</summary>
    private ProviderModelList StaleOrEmpty(
        (string Provider, Guid? TenantId) cacheKey, string errorCode)
    {
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            return new ProviderModelList(cached.Models, cached.FetchedAt, Stale: true, errorCode);
        }
        return new ProviderModelList(
            Array.Empty<ProviderModelInfo>(), FetchedAt: null, Stale: false, errorCode);
    }

    /// <summary>Unbounded growth is not a real concern at (15 providers ×
    /// tenants-that-open-the-page), but evict entries older than 24 h during
    /// writes anyway (46-0 plan D5).</summary>
    private void EvictAncientEntries(DateTimeOffset now)
    {
        foreach (var (key, entry) in _cache)
        {
            if (now - entry.FetchedAt > TimeSpan.FromHours(24))
            {
                _cache.TryRemove(key, out _);
            }
        }
    }

    private sealed record CacheEntry(
        IReadOnlyList<ProviderModelInfo> Models, DateTimeOffset FetchedAt);
}
