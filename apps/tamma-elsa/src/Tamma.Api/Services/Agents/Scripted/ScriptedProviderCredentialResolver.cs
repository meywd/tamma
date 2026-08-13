using Tamma.Activities.LlmCall.Credentials;
using Tamma.Activities.Security;

namespace Tamma.Api.Services.Agents.Scripted;

/// <summary>
/// Credential decorator for the opt-in scripted provider (2026-08-13):
/// "scripted" needs NO key (the responder is in-process), so the decorator
/// answers a fixed placeholder credential for it and delegates every other
/// provider to the real BYOK→platform resolver untouched. Registered ONLY by
/// <c>AddScriptedLlmProvider</c> (flag on + non-production host) — a normal
/// deployment never has it in the container, so the fail-closed
/// PROVIDER_CREDENTIAL_UNAVAILABLE posture for unknown providers is unchanged.
/// </summary>
public sealed class ScriptedProviderCredentialResolver : IProviderCredentialResolver
{
    /// <summary>Placeholder key value — never used on any wire.</summary>
    public const string PlaceholderKey = "scripted-no-key";

    private readonly IProviderCredentialResolver _inner;

    public ScriptedProviderCredentialResolver(IProviderCredentialResolver inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public Task<ProviderCredential> ResolveAsync(
        Guid? tenantId, string providerName, CancellationToken ct = default)
    {
        if (IsScripted(providerName))
        {
            return Task.FromResult(new ProviderCredential(
                PlaceholderKey,
                CredentialSource.Platform,
                $"scripted:{ScriptedProviderPosture.ProviderKey}",
                VersionNumber: null));
        }

        return _inner.ResolveAsync(tenantId, providerName, ct);
    }

    public void Invalidate(Guid? tenantId, string providerName)
    {
        if (!IsScripted(providerName))
        {
            _inner.Invalidate(tenantId, providerName);
        }
    }

    private static bool IsScripted(string? providerName) =>
        !string.IsNullOrWhiteSpace(providerName)
        && string.Equals(providerName.Trim(), ScriptedProviderPosture.ProviderKey,
            StringComparison.OrdinalIgnoreCase);
}
