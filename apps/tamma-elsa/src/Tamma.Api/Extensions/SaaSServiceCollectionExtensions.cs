using Tamma.Api.Services.SaaS;

namespace Tamma.Api.Extensions;

/// <summary>
/// DI registration for the SaaS-lane services ported from the deleted TS
/// <c>packages/api/src/routes/saas/*</c> modules.
/// </summary>
/// <remarks>
/// All three services are scoped because they fan out to scoped EF-backed
/// repositories. <see cref="ILlmProxyService"/> additionally depends on the
/// singleton <see cref="Tamma.Api.Services.Diagnostics.IDiagnosticsService"/>,
/// which is registered separately via <c>AddDiagnosticsServices</c>.
/// </remarks>
public static class SaaSServiceCollectionExtensions
{
    /// <summary>
    /// Register the SaaS services (<see cref="IApiKeyRotationService"/>,
    /// <see cref="ILlmProxyService"/>, <see cref="IWorkflowLifecycleService"/>).
    /// </summary>
    public static IServiceCollection AddSaaSServices(this IServiceCollection services)
    {
        services.AddScoped<IApiKeyRotationService, ApiKeyRotationService>();
        services.AddScoped<ILlmProxyService, LlmProxyService>();
        services.AddScoped<IWorkflowLifecycleService, WorkflowLifecycleService>();
        return services;
    }
}
