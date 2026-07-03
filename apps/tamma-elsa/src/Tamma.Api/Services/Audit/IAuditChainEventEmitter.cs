using Tamma.Core.Audit;

namespace Tamma.Api.Services.Audit;

/// <summary>
/// Story 37-2 (AC9/AC10) — emits <c>AUDIT.CHAIN.*</c> DCB events and raises the
/// critical tamper alert. Plane-routed (tenant vs platform) like the alert
/// pipeline.
/// </summary>
public interface IAuditChainEventEmitter
{
    Task EmitVerifiedAsync(AuditChainScope scope, ChainVerificationResult result, CancellationToken ct);
    Task EmitTamperAsync(AuditChainScope scope, ChainVerificationResult result, CancellationToken ct);
    Task EmitCheckpointedAsync(AuditChainScope scope, long headSequence, int keyVersion, CancellationToken ct);
}
