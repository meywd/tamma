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
/// The shipped default threshold: automated iff <c>currentDial &gt;= DefaultMinAutonomy</c>.
/// Range <c>[AutonomyDial.Min, AutonomyDial.AlwaysHuman]</c> — ALWAYS written as
/// <see cref="AutonomyDial"/> named constants, NEVER literals (a literal would
/// not move when the dial does, and would collide with 43-1's drift guard).
/// Shipped values reproduce today's behaviour exactly (epic decision D1): the
/// only <see cref="AutonomyDial.AlwaysHuman"/> member is
/// <c>document-type:design</c>.
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
/// resolve (checked at BuildIndex via the caller-supplied validity set in tests
/// and pinned by <c>ActionDescriptorMetadataTests</c>).
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
    bool Enforceable = true);
