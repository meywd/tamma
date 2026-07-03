using Tamma.Core.Audit;
using Tamma.Data.Entities;

namespace Tamma.Data.Audit;

/// <summary>
/// Story 37-2 — maps the EF <see cref="AuditRecord"/> entity onto the pure
/// <see cref="AuditChainRecordView"/> the canonicalizer + verifier operate on.
/// Keeps the hashing determinism defined entirely in <c>Tamma.Core</c> (no EF
/// behaviour in the hash preimage).
/// </summary>
public static class AuditRecordChainMapper
{
    /// <summary>
    /// Project a persisted (or about-to-persist) record into the canonical view
    /// for the given <paramref name="scope"/>. <paramref name="prevHash"/> /
    /// <paramref name="recordHash"/> override the entity's stored values (used at
    /// insert time before they are assigned onto the entity).
    /// </summary>
    public static AuditChainRecordView ToView(
        AuditRecord record, AuditChainScope scope,
        string? prevHash = null, string? recordHash = null) =>
        new()
        {
            Id = record.Id,
            Discriminator = scope.Discriminator,
            TenantId = record.TenantId,
            UserId = record.UserId,
            ActionCode = record.ActionCode,
            Category = record.Category,
            Severity = record.Severity,
            ActorUserId = record.ActorUserId,
            ActorEmailSnapshot = record.ActorEmailSnapshot,
            TargetType = record.TargetType,
            TargetId = record.TargetId,
            Outcome = record.Outcome,
            IpAddress = record.IpAddress,
            UserAgent = record.UserAgent,
            OccurredAt = record.OccurredAt,
            SourceEventId = record.SourceEventId,
            SourceSequenceNumber = record.SourceSequenceNumber,
            PayloadJson = record.PayloadJson,
            ChainSequence = record.ChainSequence ?? 0,
            PrevRecordHash = prevHash ?? record.PrevRecordHash ?? string.Empty,
            RecordHash = recordHash ?? record.RecordHash ?? string.Empty,
        };
}
