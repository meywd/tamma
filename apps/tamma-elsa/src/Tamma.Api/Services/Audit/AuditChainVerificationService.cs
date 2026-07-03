using Microsoft.Extensions.Logging;
using Tamma.Core.Audit;

namespace Tamma.Api.Services.Audit;

/// <summary>
/// Story 37-2 (AC4/AC8/AC9/AC10) — the request-facing verification entry point.
/// Runs the pure <see cref="IAuditChainVerifier"/> over a scope's chain, then
/// emits <c>AUDIT.CHAIN.VERIFIED</c> or <c>AUDIT.CHAIN.TAMPER_DETECTED</c> (the
/// latter also raising a critical alert). Endpoints call this so the emit +
/// alert side-effects happen in exactly one place.
/// </summary>
public sealed class AuditChainVerificationService : IAuditChainVerificationService
{
    private readonly IAuditChainVerifier _verifier;
    private readonly IAuditChainEventEmitter _emitter;
    private readonly ILogger<AuditChainVerificationService> _logger;

    public AuditChainVerificationService(
        IAuditChainVerifier verifier,
        IAuditChainEventEmitter emitter,
        ILogger<AuditChainVerificationService> logger)
    {
        _verifier = verifier ?? throw new ArgumentNullException(nameof(verifier));
        _emitter = emitter ?? throw new ArgumentNullException(nameof(emitter));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ChainVerificationResult> VerifyAsync(
        AuditChainScope scope, long? from, long? to, CancellationToken ct = default)
    {
        var result = await _verifier.VerifyAsync(scope, from, to, ct).ConfigureAwait(false);

        if (result.Status == ChainVerificationStatus.Tampered)
        {
            _logger.LogError(
                "audit.chain.tamper_detected scope={Scope} tenantId={TenantId} "
                + "reason={Reason} chainSequence={ChainSequence}",
                scope.Discriminator, scope.TenantId,
                result.FirstBrokenLink?.Reason, result.FirstBrokenLink?.ChainSequence);
            await _emitter.EmitTamperAsync(scope, result, ct).ConfigureAwait(false);
        }
        else
        {
            _logger.LogInformation(
                "audit.chain.verified scope={Scope} tenantId={TenantId} "
                + "recordsVerified={Records} headSequence={Head}",
                scope.Discriminator, scope.TenantId, result.RecordsVerified, result.LastSequence);
            await _emitter.EmitVerifiedAsync(scope, result, ct).ConfigureAwait(false);
        }

        return result;
    }
}
