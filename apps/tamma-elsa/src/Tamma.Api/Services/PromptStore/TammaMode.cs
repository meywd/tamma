using Microsoft.Extensions.Configuration;

namespace Tamma.Api.Services.PromptStore;

/// <summary>
/// Operating mode of the running Tamma process. Set once at startup based
/// on configuration; do NOT vary per-request.
///
/// <para>Per CLAUDE.md "Operating Modes": a deployment is either single-user
/// OR SaaS — not both. Mode determines who is the principal (owns settings,
/// prompts, providers, secrets) and which scoping model tenant-aware
/// services (PromptStoreService, ProviderConfigService, ...) read+write.</para>
/// </summary>
public enum TammaMode
{
    /// <summary>
    /// Self-hosted single-user mode. The sole user owns everything.
    /// Process entry: <c>tamma start</c> (engine) or <c>tamma server</c>
    /// (HTTP). RBAC absent; the user has full control.
    /// </summary>
    SingleUser,

    /// <summary>
    /// SaaS / GitHub-App mode. The principal is the tenant (org). RBAC
    /// applies (<c>tenant_owner</c> / <c>tenant_admin</c> / <c>member</c>).
    /// Process entry: <c>tamma api</c>.
    /// </summary>
    SaaS,
}

/// <summary>
/// Resolves the process-wide <see cref="TammaMode"/> from configuration.
///
/// <para>Detection rules (CLAUDE.md "Operating Modes / Mode detection"):</para>
/// <list type="bullet">
///   <item>Explicit <c>Tamma:Mode=saas</c> or <c>=single-user</c> wins.</item>
///   <item>Otherwise, presence of <c>Tamma:TenantSharedSecret</c> OR
///     <c>ConnectionStrings:ControlPlane</c> signals SaaS.</item>
///   <item>Default: <see cref="TammaMode.SingleUser"/> — the most permissive
///     fallback for self-hosted deployments.</item>
/// </list>
///
/// <para>Registered as a singleton in <c>Program.cs</c>; readers hold an
/// <see cref="ITammaModeProvider"/> and call <see cref="ITammaModeProvider.Mode"/>
/// once per call. The instance returns the same value for the lifetime of
/// the process.</para>
/// </summary>
public interface ITammaModeProvider
{
    TammaMode Mode { get; }
}

/// <inheritdoc />
public sealed class TammaModeProvider : ITammaModeProvider
{
    public TammaMode Mode { get; }

    public TammaModeProvider(IConfiguration configuration)
    {
        Mode = Resolve(configuration);
    }

    /// <summary>
    /// Pure helper exposed for tests. Mirrors the constructor's logic
    /// without an <see cref="IConfiguration"/> dependency.
    /// </summary>
    public static TammaMode Resolve(IConfiguration configuration)
    {
        var explicitMode = configuration["Tamma:Mode"];
        if (!string.IsNullOrWhiteSpace(explicitMode))
        {
            return explicitMode.Trim().ToLowerInvariant() switch
            {
                "saas" => TammaMode.SaaS,
                "single-user" or "singleuser" or "single_user" => TammaMode.SingleUser,
                _ => throw new InvalidOperationException(
                    $"Tamma:Mode='{explicitMode}' is not recognised. " +
                    "Use 'saas' or 'single-user'."),
            };
        }

        // Inferred from SaaS-only config presence — both the tenant shared
        // HMAC secret (used by per-tenant engines to authenticate to the
        // central API) and the control-plane connection string (per-request
        // tenant DB routing) are SaaS-only.
        var hasSharedSecret = !string.IsNullOrWhiteSpace(
            configuration["Tamma:TenantSharedSecret"]);
        var hasControlPlane = !string.IsNullOrWhiteSpace(
            configuration.GetConnectionString("ControlPlane"));
        if (hasSharedSecret || hasControlPlane)
        {
            return TammaMode.SaaS;
        }

        return TammaMode.SingleUser;
    }
}
