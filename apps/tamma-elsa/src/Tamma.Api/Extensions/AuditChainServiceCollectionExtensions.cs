using Microsoft.Extensions.DependencyInjection.Extensions;
using Tamma.Api.Services.Audit;
using Tamma.Core.Audit;

namespace Tamma.Api.Extensions;

/// <summary>
/// Story 37-2 — DI registration for the tamper-evident audit hash-chain: the
/// pure verifier, the record/checkpoint read seams, the cabinet-backed signer,
/// the checkpoint writer, the verification service, and the event emitter.
/// Single entry-point (mirrors <c>AddTammaAuditProjection</c>). Idempotent.
///
/// <para>All services are request-scoped: they hang off the scoped
/// <c>ControlPlaneDbContext</c> and the per-tenant context factory. The
/// scheduler/workflow (<c>Tamma.ElsaServer</c>) creates its own DI scope per
/// tick, exactly like the projector host.</para>
/// </summary>
public static class AuditChainServiceCollectionExtensions
{
    public static IServiceCollection AddTammaAuditChain(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);

        // Read seams over the correct physical store (CP vs tenant schema).
        services.TryAddScoped<IAuditChainRecordSource, AuditChainRecordSource>();
        services.TryAddScoped<IAuditChainCheckpointGateway, AuditChainCheckpointGateway>();

        // Cabinet-backed HMAC signer (fail-closed).
        services.TryAddScoped<IAuditChainSigner, SecretCabinetAuditChainSigner>();

        // Pure verifier + the request-facing service that emits events/alerts.
        services.TryAddScoped<IAuditChainVerifier, AuditChainVerifier>();
        services.TryAddScoped<IAuditChainEventEmitter, AuditChainEventEmitter>();
        services.TryAddScoped<IAuditChainVerificationService, AuditChainVerificationService>();

        // Checkpoint writer (on demand + scheduled).
        services.TryAddScoped<IAuditChainCheckpointService, AuditChainCheckpointService>();

        // Scheduled checkpoint host — opt-in (RunOnStartup defaults false), so it
        // stays inert during tests / un-opted deployments (mirrors the projector).
        services.TryAddSingleton<AuditChainCheckpointOptions>();
        services.AddHostedService<AuditChainCheckpointScheduler>();

        return services;
    }
}
