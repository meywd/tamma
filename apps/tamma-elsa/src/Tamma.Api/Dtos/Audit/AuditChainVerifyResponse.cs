using Tamma.Core.Audit;

namespace Tamma.Api.Dtos.Audit;

/// <summary>
/// Story 37-2 (AC8) — the wire shape of a chain-verify response:
/// <c>{ status, firstBrokenLink?, lastCheckpoint }</c>. No record payload
/// contents are exposed — only coordinates + hashes.
/// </summary>
public sealed record AuditChainVerifyResponse(
    string Status,
    string Scope,
    Guid? TenantId,
    long HeadSequence,
    long RecordsVerified,
    AuditChainBrokenLinkDto? FirstBrokenLink,
    AuditChainCheckpointDto? LastCheckpoint)
{
    public static AuditChainVerifyResponse From(AuditChainScope scope, ChainVerificationResult r)
    {
        var link = r.FirstBrokenLink is null
            ? null
            : new AuditChainBrokenLinkDto(
                r.FirstBrokenLink.RecordId,
                r.FirstBrokenLink.ChainSequence,
                r.FirstBrokenLink.Reason.ToString());

        var cp = r.LastCheckpoint is null
            ? null
            : new AuditChainCheckpointDto(
                r.LastCheckpoint.Id,
                r.LastCheckpoint.HeadSequence,
                r.LastCheckpoint.HeadHash,
                r.LastCheckpoint.SignedAt,
                r.LastCheckpoint.KeyVersion);

        return new AuditChainVerifyResponse(
            Status: r.Status == ChainVerificationStatus.Ok ? "ok" : "tampered",
            Scope: scope.Discriminator,
            TenantId: scope.TenantId,
            HeadSequence: r.LastSequence,
            RecordsVerified: r.RecordsVerified,
            FirstBrokenLink: link,
            LastCheckpoint: cp);
    }
}

public sealed record AuditChainBrokenLinkDto(Guid? RecordId, long ChainSequence, string Reason);

public sealed record AuditChainCheckpointDto(
    Guid Id, long HeadSequence, string HeadHash, DateTime SignedAt, int KeyVersion);
