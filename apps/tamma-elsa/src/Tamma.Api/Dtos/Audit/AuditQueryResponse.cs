namespace Tamma.Api.Dtos.Audit;

/// <summary>
/// Story 37-3 — the audit query envelope. <see cref="NextCursor"/> is the
/// opaque keyset cursor for the following page (<c>null</c> on the last page,
/// AC5). <see cref="Total"/> is an ESTIMATE — a capped exact count (exact up to
/// <see cref="CountCap"/>, then reported as the cap) so paging is never gated on
/// a full <c>COUNT(*)</c> over millions of rows (AC9). When
/// <see cref="TotalIsCapped"/> is true, the true total is "<see cref="Total"/>+".
/// </summary>
/// <param name="Records">The page of curated audit rows, most-recent first.</param>
/// <param name="NextCursor">Opaque cursor for the next page, or null on the last page.</param>
/// <param name="Total">Estimated count of rows matching the filters (capped).</param>
/// <param name="TotalIsCapped">True when <see cref="Total"/> hit the count cap (true total is higher).</param>
public sealed record AuditQueryResponse(
    IReadOnlyList<AuditRecordResponse> Records,
    string? NextCursor,
    int Total,
    bool TotalIsCapped)
{
    /// <summary>Exact-count cap — beyond this the total is reported as "<c>N+</c>".</summary>
    public const int CountCap = 10_000;
}
