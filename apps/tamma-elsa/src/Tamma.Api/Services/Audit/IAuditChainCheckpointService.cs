using Tamma.Core.Audit;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Audit;

/// <summary>
/// Story 37-2 (AC5/AC6) — writes signed chain checkpoints (on demand + scheduled).
/// </summary>
public interface IAuditChainCheckpointService
{
    /// <summary>
    /// Write one signed checkpoint anchoring the current head of
    /// <paramref name="scope"/>. Returns null (no-op) when the chain is empty.
    /// </summary>
    Task<AuditChainCheckpoint?> WriteCheckpointAsync(
        AuditChainScope scope, CancellationToken ct = default);

    /// <summary>
    /// Write one checkpoint for the platform chain and one per active tenant
    /// chain. Failure-isolated per scope. Returns the number of checkpoints
    /// written.
    /// </summary>
    Task<int> WriteAllActiveScopesAsync(CancellationToken ct = default);
}
