namespace Tamma.Api.Services.Providers;

/// <summary>
/// Abstraction over the upstream provider HTTP call. Exists so
/// <see cref="ProviderSessionService"/> can be unit-tested without a real
/// <see cref="IHttpClientFactory"/>, and so the integration tests can install
/// a <c>MockHttpMessageHandler</c>-backed stub without hitting Anthropic /
/// OpenAI from the test suite.
/// </summary>
public interface IProviderClient
{
    /// <summary>
    /// Dispatch a single provider request and return the normalised result.
    /// Implementations are responsible for selecting the correct named
    /// <see cref="HttpClient"/> from <see cref="IHttpClientFactory"/> based
    /// on <paramref name="provider"/>.
    /// </summary>
    Task<ProviderInvocationResult> InvokeAsync(
        string provider,
        string model,
        ExecuteRequest req,
        CancellationToken ct = default);
}
