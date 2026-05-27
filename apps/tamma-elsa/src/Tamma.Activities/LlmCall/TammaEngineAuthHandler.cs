using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;

namespace Tamma.Activities.LlmCall;

/// <summary>
/// <para>
/// Engine → API authentication seam. A <see cref="DelegatingHandler"/> that
/// stamps an <c>Authorization: Bearer &lt;token&gt;</c> header onto every
/// outgoing request issued by the named <c>"tamma-engine"</c> HttpClient.
/// </para>
///
/// <para>
/// <b>Why a DelegatingHandler instead of <see cref="HttpClient.DefaultRequestHeaders"/>?</b>
/// Activities pass the HttpClient through to static helpers
/// (<c>ResolveConventionsActivity.CallResolveAsync</c>,
/// <c>ResolvePromptFromRegistryActivity.CallResolveAsync</c>) that construct
/// their own <see cref="HttpRequestMessage"/> and call <c>SendAsync</c>. A
/// handler is the only auth seam that survives that flow — defaults set on
/// the client get carried, but a handler chain is the canonical "every
/// outgoing request" hook in <c>HttpClientFactory</c>.
/// </para>
///
/// <para>
/// <b>Configuration.</b> Reads <c>Tamma:ApiToken</c> from <see cref="IConfiguration"/>
/// (falls back to the <c>TAMMA_API_TOKEN</c> env var via the standard
/// configuration provider chain — same key + fallback as
/// <see cref="TammaApiClient"/>, so dev/prod token setup is identical for both
/// the Story 9-11 API client and the Story 27-13/27-18 resolve activities).
/// </para>
///
/// <para>
/// <b>Behaviour.</b>
/// <list type="bullet">
///   <item>Token configured → adds <c>Authorization: Bearer &lt;token&gt;</c>
///     on every outgoing request UNLESS the caller already set an
///     <see cref="HttpRequestMessage.Headers"/>.Authorization (don't clobber
///     an explicit caller override).</item>
///   <item>Token NOT configured (dev mode / local-only) → handler is a
///     no-op. The API's <c>AllowAnonymousHandler</c> short-circuits in
///     Development so this still works without breaking local flows.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Production-blocker fix.</b> Before this seam, the resolve activities
/// hit the API endpoints anonymously, which returns 401 in production. The
/// activities then mapped 401 → <c>NO_ROW</c> (non-retryable) and permanently
/// failed the workflow before any LLM ran. With the handler wired into the
/// <c>"tamma-engine"</c> named client and the named client substituted for
/// the previous plain <c>CreateClient()</c> call, the activity's outgoing
/// POST carries the Bearer token and the API accepts it via the platform's
/// JwtBearer / ApiKey auth chain.
/// </para>
/// </summary>
public sealed class TammaEngineAuthHandler : DelegatingHandler
{
    private readonly string? _token;

    public TammaEngineAuthHandler(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        // Mirror TammaApiClient's exact config key + env-var fallback so
        // dev/prod token setup is a single switch for both the API client
        // and the resolve activities.
        _token = configuration["Tamma:ApiToken"]
                 ?? Environment.GetEnvironmentVariable("TAMMA_API_TOKEN");
    }

    /// <summary>
    /// Test hook — exposes the resolved token so the integration test for
    /// "header present when token configured" can assert symmetry without
    /// reflection.
    /// </summary>
    internal bool HasToken => !string.IsNullOrWhiteSpace(_token);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_token) &&
            request.Headers.Authorization is null)
        {
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", _token);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
