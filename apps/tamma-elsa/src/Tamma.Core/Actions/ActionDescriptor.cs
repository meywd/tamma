using Tamma.Core.Documents.Policy;

namespace Tamma.Core.Actions;

/// <summary>
/// One catalogued action (Story 43-2 AC9): the compile-time declaration the
/// admin surface renders, the resolver defaults from, and the gate looks up.
/// Immutable, code-resident, PLATFORM-scoped vocabulary — a tenant admin never
/// adds, removes or renames an action (epic README open question 4's scoping
/// correction); only <c>action_assignments</c> rows (43-5) are per-principal.
/// </summary>
/// <param name="Key">The composite catalog address.</param>
/// <param name="Group">The one <see cref="ActionGroup"/> this action belongs to — a strict partition (43-3 AC1).</param>
/// <param name="Risk">Coarse risk class; orthogonal to <paramref name="Group"/>.</param>
/// <param name="Reversible">Whether the completed action can be undone afterwards.</param>
/// <param name="Title">One-line UI title (43-7 renders it; empty is a boot failure).</param>
/// <param name="Summary">One-line UI summary (empty is a boot failure).</param>
/// <param name="DefaultMinAutonomy">
/// The shipped zone level: a DIAL-GOVERNED action is automated iff
/// <c>currentDial &gt;= DefaultMinAutonomy</c>. Range <c>[AutonomyDial.Min, AutonomyDial.Max]</c>
/// — a LEVEL, not a threshold; <see cref="AutonomyDial.AlwaysHuman"/> is NOT a
/// legal descriptor value any more (Story 43-11 M6). Written as a plain integer
/// literal per action (Story 43-11 D2 — a level of 45 means forty-five and must
/// NOT move when <see cref="AutonomyDial.Min"/> moves), pinned by
/// <c>ActionCatalogLevelTests</c>. For a MACHINERY row (<see cref="IsMachinery"/>)
/// this field is NOT-APPLICABLE: the evaluator short-circuits before the dial
/// comparison, so the value (left at <see cref="AutonomyDial.Min"/>) is inert.
/// </param>
/// <param name="SiteKey">
/// The performing site in code (route + method, executor class, hosted-service
/// class, task-type constant). Reflection sweeps match on it; uniqueness is
/// enforced within the <c>effect</c>/<c>automation</c>/<c>platform-task</c>
/// namespaces (tool exempt: the two <c>git_operations.*</c> members share one
/// executor; agent-action/document-type exempt: registry-declared vocabularies
/// share their registry site).
/// </param>
/// <param name="SensitiveActionCode">
/// Optional join into <c>SensitiveActionCatalog.ByCode</c> (43-2 D12): the
/// compliance catalog stays the compliance artifact, this catalog stays the
/// authorization artifact, joined where a join exists. Every non-null value must
/// resolve: <c>ActionCatalog.BuildIndex</c> checks it against a caller-supplied
/// validity set (code <c>ACTION.CATALOG.UNKNOWN_SENSITIVE_CODE</c>) —
/// <c>ActionCatalog.Validate()</c> supplies the real
/// <c>SensitiveActionCatalog.ByCode</c> key set at boot, the red-rehearsal in
/// <c>ActionCatalogBuildIndexTests</c> supplies its own, and the join is
/// additionally pinned by <c>ActionDescriptorMetadataTests</c>. The static-init
/// path deliberately skips it (no Audit-plane dependency at type init).
/// </param>
/// <param name="EscalatableToHuman">
/// Whether a denial can suspend for a person. <c>false</c> for every
/// <c>automation:*</c> member (a sweeper cannot wait — Seam D is deny-only, and
/// the 43-6 API rejects mid-range thresholds on non-escalatable members).
/// </param>
/// <param name="Enforceable">
/// Whether the gate may ever block on this member. <c>false</c> ONLY for
/// <c>effect:secret.reveal</c> (epic README open question 2, ANSWERED
/// 2026-07-25: reading a secret never requires a human — the catalog row is
/// informational, and no admin-raised threshold on it may ever be enforced).
/// </param>
/// <param name="IsMachinery">
/// Story 43-13 (43-11 Amendment 4 + the caller-kind re-audit's machinery
/// inventory): TRUE for the 42 rows that are deterministic machinery — all 29
/// <c>automation:*</c>, all 8 <c>platform-task:*</c>, and the 5 plumbing-only
/// effects (<c>engine.events.append</c>, <c>engine.platform-events.append</c>,
/// <c>engine.document.persist</c>, <c>engine.document.set-status</c>,
/// <c>secret.reveal</c>). These rows keep key/group/risk/site for audit and
/// drift but carry NO level semantics: the evaluator never resolves them through
/// the dial (terminal <c>ReasonMachineryNotDialGoverned</c>), and the 43-6 API
/// rejects threshold writes on them. <c>enabled = false</c>, role restrictions
/// and the fail-closed unreadable-policy posture still apply.
/// </param>
public sealed record ActionDescriptor(
    ActionKey Key,
    ActionGroup Group,
    ActionRisk Risk,
    bool Reversible,
    string Title,
    string Summary,
    int DefaultMinAutonomy,
    string SiteKey,
    string? SensitiveActionCode = null,
    bool EscalatableToHuman = true,
    bool Enforceable = true,
    bool IsMachinery = false);
