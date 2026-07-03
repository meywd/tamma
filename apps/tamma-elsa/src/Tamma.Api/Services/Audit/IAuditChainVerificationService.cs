using Tamma.Core.Audit;

namespace Tamma.Api.Services.Audit;

/// <summary>
/// Story 37-2 — verifies a scope's chain AND emits the resulting DCB event +
/// critical tamper alert. The single request-facing verification seam.
/// </summary>
public interface IAuditChainVerificationService
{
    Task<ChainVerificationResult> VerifyAsync(
        AuditChainScope scope, long? from, long? to, CancellationToken ct = default);
}
