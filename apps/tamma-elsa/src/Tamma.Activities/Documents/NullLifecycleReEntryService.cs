using Tamma.Core.Documents;
using Tamma.Core.Documents.Resume;

namespace Tamma.Activities.Documents;

/// <summary>
/// Story 39-10 (Design Decision D7) — the SAFE seam: always reports a fresh
/// <see cref="LifecycleResumeStage.Produce"/> position and no existing body, so the
/// lifecycle runs exactly as it did before re-entry existed (today's behaviour, zero
/// risk). It is the config-flag fallback: a bad latest-accepted read can be disabled
/// by swapping the DI registration to this Null seam WITHOUT touching any lifecycle
/// code (the risk-note mitigation). The REAL <see cref="LifecycleReEntryService"/> is
/// the default now that 39-11 has landed.
/// </summary>
public sealed class NullLifecycleReEntryService : ILifecycleReEntryService
{
    public Task<LifecycleResumePosition> ReconstructAsync(
        Guid? tenantId, string issueId, string documentTypeKey, CancellationToken ct)
        => Task.FromResult(LifecycleResumePosition.Fresh(
            documentTypeKey, "Re-entry disabled (NullLifecycleReEntryService); running fresh."));

    public Task<DocumentEnvelope?> GetDocumentBodyAsync(
        Guid? tenantId, Guid documentId, CancellationToken ct)
        => Task.FromResult<DocumentEnvelope?>(null);
}
