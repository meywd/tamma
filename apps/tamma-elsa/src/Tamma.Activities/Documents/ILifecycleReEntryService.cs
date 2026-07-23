using Tamma.Core.Documents;
using Tamma.Core.Documents.Resume;

namespace Tamma.Activities.Documents;

/// <summary>
/// Story 39-10 (AC5, Design Decision D1) — the I/O half of re-entry: reconstructs a
/// lifecycle's resume position for an issue+type from durable truth (the 39-11
/// latest-accepted read + the 4-7 DCB event query), delegating the actual fold to the
/// pure <see cref="LifecycleResumeCalculator"/>. It reads the document store and event
/// stream IN-PROCESS (never HTTP) and NEVER inspects Elsa instance internals — Elsa
/// state is an optimization, the store + events are the truth.
/// </summary>
public interface ILifecycleReEntryService
{
    /// <summary>
    /// Reconstruct the typed resume position for <paramref name="documentTypeKey"/> on
    /// <paramref name="issueId"/>. <paramref name="tenantId"/> may be null in single-user
    /// mode (the ambient tenant is used).
    /// </summary>
    Task<LifecycleResumePosition> ReconstructAsync(
        Guid? tenantId, string issueId, string documentTypeKey, CancellationToken ct);

    /// <summary>
    /// Fetch the persisted document body for <paramref name="documentId"/> as a
    /// reconstructed envelope — the guard path threads it into the workflow when a
    /// re-entry SKIPS produce (Review/Accept/Complete) so the existing revision is
    /// reviewed/accepted rather than re-produced. Null when the row is missing/foreign.
    /// </summary>
    Task<DocumentEnvelope?> GetDocumentBodyAsync(
        Guid? tenantId, Guid documentId, CancellationToken ct);
}
