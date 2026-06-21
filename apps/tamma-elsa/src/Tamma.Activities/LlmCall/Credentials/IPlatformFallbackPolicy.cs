namespace Tamma.Activities.LlmCall.Credentials;

/// <summary>
/// Decides whether the platform-provided key may be used as a fallback when a
/// tenant has no BYOK key (Story 32-3 AC6.1). The plan-level / per-provider
/// gating that Epics 34/35 will drive plugs in here without changing the
/// resolver.
///
/// <para>v1 default (<c>ConfigPlatformFallbackPolicy</c>): single-user ⇒ always
/// allowed; SaaS ⇒ allowed unless explicitly disabled by config.</para>
/// </summary>
public interface IPlatformFallbackPolicy
{
    /// <summary>
    /// True when the platform key may be used for <c>(tenantId, providerName)</c>.
    /// <paramref name="tenantId"/> == null ⇒ single-user / platform scope.
    /// </summary>
    bool IsPlatformFallbackAllowed(Guid? tenantId, string providerName);
}
