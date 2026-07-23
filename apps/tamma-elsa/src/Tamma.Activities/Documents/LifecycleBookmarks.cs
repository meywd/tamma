using Tamma.Activities.ADL;

namespace Tamma.Activities.Documents;

/// <summary>
/// Story 39-10 (AC4, Design Decisions D2/D4) — the ONE canonical, tenant-folded
/// bookmark-name builder every lifecycle suspend point routes through, generalizing
/// the proven Clarify/Design/Merge per-workflow builders into a single reuse.
///
/// <para><b>One core, two sanctioned key shapes (D2).</b> <see cref="Compose"/> is
/// the single composition core — <c>{gate}-{norm(tenant)}-{norm(seg)}…</c> with every
/// segment (the tenant included) run through the SAME
/// <see cref="WaitForMergeApprovalActivity.NormalizeSegment"/> transform used by the
/// merge/design gates, so suspend and resume compute byte-identical names and a
/// cross-tenant resume can never resolve another tenant's bookmark (IDOR guard). Two
/// typed wrappers sit on top:
/// <list type="bullet">
///   <item><see cref="ForStageGate"/> — the AC4 domain-keyed shape
///   <c>(tenantId, issueId, documentType, gate)</c>, for lifecycle stage gates that
///   must be recomputable from durable domain coordinates alone.</item>
///   <item><see cref="ForDecisionSession"/> — the 39-8 accept-gate shape
///   <c>(tenantId, sessionId)</c> where the 128-bit session id carries the
///   unguessability. It is byte-identical to
///   <see cref="WaitForDocumentDecisionActivity.DecisionBookmarkName"/> (which now
///   delegates here). Determinism still holds: the session id is persisted in
///   <c>LifecycleState</c> and on <c>APPROVAL.REQUESTED</c>, so a post-deploy resume
///   recomputes the same name from durable state.</item>
/// </list></para>
/// </summary>
public static class LifecycleBookmarks
{
    /// <summary>
    /// The composition core (D2). Prefixes the <paramref name="gate"/> then appends
    /// the tenant segment and every remaining <paramref name="segments"/> entry, each
    /// normalized via <see cref="WaitForMergeApprovalActivity.NormalizeSegment"/> so
    /// hostile characters can't break the delimiter scheme or smuggle a collision.
    /// </summary>
    public static string Compose(string gate, string? tenantId, params string[] segments)
    {
        var parts = new List<string>(segments.Length + 2)
        {
            gate,
            WaitForMergeApprovalActivity.NormalizeSegment(tenantId),
        };
        foreach (var segment in segments)
            parts.Add(WaitForMergeApprovalActivity.NormalizeSegment(segment));
        return string.Join("-", parts);
    }

    /// <summary>
    /// The AC4 domain-keyed stage-gate name: same
    /// <c>(tenantId, issueId, documentTypeKey, gate)</c> → same name on suspend and
    /// resume, recomputable from domain coordinates alone.
    /// </summary>
    public static string ForStageGate(string? tenantId, string issueId, string documentTypeKey, string gate)
        => Compose(gate, tenantId, issueId, documentTypeKey);

    /// <summary>
    /// The 39-8 accept-gate name, byte-identical to the pre-39-10
    /// <c>document-decision-{norm(tenant)}-{session}</c> form. NOTE: the session id is
    /// appended RAW (not through <see cref="WaitForMergeApprovalActivity.NormalizeSegment"/>)
    /// so the output matches 39-8's original builder exactly — a Guid's canonical
    /// string form is already delimiter-safe and unguessable, and the byte-parity pin
    /// guards this.
    /// </summary>
    public static string ForDecisionSession(string? tenantId, Guid sessionId)
    {
        var tenant = WaitForMergeApprovalActivity.NormalizeSegment(tenantId);
        return $"document-decision-{tenant}-{sessionId}";
    }

    /// <summary>
    /// Story 39-10 (D4) — the CANONICAL-gate registry as production data: each
    /// sanctioned suspend-activity <see cref="Type"/> mapped to its gate prefix. The
    /// structural build gate walks built graphs for activity nodes whose type is in
    /// this dictionary — so "non-canonical bookmark name" becomes "suspend activity
    /// type not in the registry", with no string-matching of names at test time.
    /// Seeded with 39-8's <see cref="WaitForDocumentDecisionActivity"/>; the legacy
    /// Clarify/Design/Merge waits stay OUT (their workflows are allowlisted, and
    /// 39-13+ migrations retire them).
    /// </summary>
    public static IReadOnlyDictionary<Type, string> CanonicalSuspendActivities { get; } =
        new Dictionary<Type, string>
        {
            [typeof(WaitForDocumentDecisionActivity)] = "document-decision",
        };
}
