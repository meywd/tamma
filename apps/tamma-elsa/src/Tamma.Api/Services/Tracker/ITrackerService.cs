using Tamma.Api.Dtos.Tracker;
using Tamma.Data.Entities;

namespace Tamma.Api.Services.Tracker;

/// <summary>
/// Resolved tracker preferences (Story 44-2 AC8): the stored principal row when
/// one exists, otherwise the shipped <see cref="TrackerPreferenceDefaults"/> —
/// the <c>AcceptanceRulesService</c> "override → default" posture, carrying its
/// provenance on the wire so a caller can tell a saved choice from a default.
/// </summary>
public sealed record ResolvedTrackerPreferences(
    Guid? DefaultProjectId,
    string? DefaultKind,
    string? BoardGroupBy,
    bool IsOverride,
    int Version,
    DateTime? UpdatedAt);

/// <summary>The shipped preference defaults — the safety net under every principal.</summary>
public static class TrackerPreferenceDefaults
{
    /// <summary>No default project: the caller picks, and "unset" is honest.</summary>
    public static readonly Guid? DefaultProjectId;

    /// <summary>
    /// <c>task</c> — the create-form default kind. Named here, once, rather
    /// than in a form: <c>epic</c> would make every quick-create a container
    /// and <c>spike</c> is the rarest member.
    /// </summary>
    public const string DefaultKind = "task";

    /// <summary>
    /// <c>status</c> — the board grouping the epic README §7 names
    /// (<c>GET /api/work-items?projectId=…&amp;groupBy=status</c>). Free text by
    /// design (44-6 owns the UI vocabulary), so it is NOT parsed here.
    /// </summary>
    public const string BoardGroupBy = "status";
}

/// <summary>
/// Result of one work-item list query — the page plus the honest visibility
/// discriminator (AC7).
/// </summary>
public sealed record WorkItemPage(
    IReadOnlyList<WorkItemEntity> Items,
    string? NextCursor,
    string VisibilityMode);

/// <summary>
/// Story 44-2 — the tracker's application service: vocabulary validation at the
/// wire boundary (fail-loud typed <c>TammaError</c>, never a DB CHECK
/// surprise), the tri-state PATCH application (AC3), estimate/scale coherence
/// (44-0's <c>EstimateScale.AllowsEstimate</c>, called here — the story owns the
/// boundary call, not the rule), <c>If-Match</c>/<c>Version</c> optimistic
/// concurrency (AC9) and keyset paging (plan D7).
///
/// <para><b>Only preferences carry a mode split</b> (AC5 / plan D4 / epic D6).
/// Work items, projects and iterations are tenant-schema CONTENT — the tenant is
/// already resolved by the connection, so a <c>userId</c> scoping parameter
/// would be a second ownership plane with no reader. The preference methods are
/// the ONLY ones taking <c>userId</c>/<c>tenantId</c>, and they come in parallel
/// never-joined pairs (<c>…Async(Guid? userId)</c> /
/// <c>…ForTenantAsync(Guid tenantId)</c>) exactly as
/// <c>IAcceptanceRulesRepository</c> documents. <c>TrackerOwnershipContractTests</c>
/// pins this by reflection so a later "for symmetry" refactor fails the build.</para>
///
/// <para><b>Events:</b> none emitted here. Story 44-5 adds emission INSIDE the
/// implementation, so no endpoint or DTO changes when it lands (plan "Events").</para>
/// </summary>
public interface ITrackerService
{
    // ───────────────────────── Projects (no mode split) ─────────────────────

    Task<IReadOnlyList<ProjectEntity>> ListProjectsAsync(bool includeArchived);

    Task<ProjectEntity?> GetProjectAsync(Guid projectId);

    /// <summary>
    /// Create a project. <paramref name="createdByUserId"/> stamps AUTHORSHIP —
    /// it is not a scoping key (see the type doc).
    /// </summary>
    Task<ProjectEntity> CreateProjectAsync(CreateProjectRequest request, Guid? createdByUserId);

    /// <summary>
    /// Apply a tri-state PATCH. Fields the caller did not send are left byte-
    /// unchanged (AC3). Returns null when no such project exists.
    /// </summary>
    /// <exception cref="Tamma.Core.TammaError">
    /// <c>TRACKER.CONCURRENCY_CONFLICT</c> when <paramref name="ifMatchVersion"/>
    /// does not match the stored <c>Version</c>.
    /// </exception>
    Task<ProjectEntity?> PatchProjectAsync(Guid projectId, PatchProjectRequest request, int? ifMatchVersion);

    /// <summary>Delete a project; false when absent. Work items block it (409 at the endpoint).</summary>
    Task<bool> DeleteProjectAsync(Guid projectId, int? ifMatchVersion);

    /// <summary>Ids of the work items that block a project delete (empty = deletable).</summary>
    Task<IReadOnlyList<WorkItemEntity>> ProjectWorkItemsAsync(Guid projectId, int limit);

    // ──────────────────────── Work items (no mode split) ────────────────────

    Task<WorkItemEntity?> GetWorkItemAsync(Guid id);

    /// <summary>Resolve a wire key (<c>TAM-142</c>) — current-or-previous (44-0 AC8).</summary>
    Task<WorkItemEntity?> GetWorkItemByKeyAsync(string key);

    /// <summary>
    /// Filtered, keyset-paged list. <paramref name="viewerUserId"/> is the
    /// VISIBILITY subject (AC7), not an ownership plane: it is used only to ask
    /// a REAL <c>ITaskAudienceResolver</c> whether the viewer may see each row,
    /// and is ignored entirely while the shipped no-op stub is registered.
    /// </summary>
    Task<WorkItemPage> ListWorkItemsAsync(WorkItemListQuery query, Guid? viewerUserId);

    Task<WorkItemEntity> CreateWorkItemAsync(CreateWorkItemRequest request, Guid? createdByUserId);

    Task<WorkItemEntity?> PatchWorkItemAsync(Guid id, PatchWorkItemRequest request, int? ifMatchVersion);

    Task<WorkItemEntity?> SetWorkItemStatusAsync(Guid id, string statusWire, int? ifMatchVersion);

    Task<WorkItemEntity?> AssignWorkItemAsync(Guid id, Guid? assigneeUserId, int? ifMatchVersion);

    Task<bool> DeleteWorkItemAsync(Guid id, int? ifMatchVersion);

    /// <summary>Children of a work item — the 409 body of a blocked delete (plan D11).</summary>
    Task<IReadOnlyList<WorkItemEntity>> ChildrenAsync(Guid id, int limit);

    // ─────── Preferences — the ONE paired, never-joined surface (AC5/AC8) ────

    Task<ResolvedTrackerPreferences> GetPreferencesAsync(Guid? userId);

    Task<ResolvedTrackerPreferences> GetPreferencesForTenantAsync(Guid tenantId);

    /// <param name="ifMatchVersion">
    /// The caller's <c>If-Match</c> precondition, carried all the way into the
    /// repository so it is applied ATOMICALLY with the write (44-2 review
    /// 2026-07-29 — a handler-level read-then-compare left a real lost-update
    /// window).
    /// </param>
    Task<ResolvedTrackerPreferences> UpsertPreferencesAsync(
        Guid? userId, UpsertTrackerPreferencesRequest request, Guid? actingUserId,
        int? ifMatchVersion = null);

    /// <param name="ifMatchVersion">See <see cref="UpsertPreferencesAsync"/>.</param>
    Task<ResolvedTrackerPreferences> UpsertPreferencesForTenantAsync(
        Guid tenantId, UpsertTrackerPreferencesRequest request, Guid? actingUserId,
        int? ifMatchVersion = null);

    /// <param name="ifMatchVersion">
    /// See <see cref="UpsertPreferencesAsync"/>. AC9 says EVERY mutation, and a
    /// delete is one: resetting the override to the shipped defaults discards
    /// whatever a concurrent editor just saved, so the same precondition is
    /// available here (44-2 conformance round 2026-07-29 — this route was the
    /// one carve-out, and a carve-out is worse than consistency). Absent =
    /// unconditional delete, exactly as elsewhere.
    /// </param>
    Task<bool> DeletePreferencesAsync(Guid? userId, int? ifMatchVersion = null);

    /// <param name="ifMatchVersion">See <see cref="DeletePreferencesAsync"/>.</param>
    Task<bool> DeletePreferencesForTenantAsync(Guid tenantId, int? ifMatchVersion = null);
}

/// <summary>
/// The wire-side work-item filter (Story 44-2 technical notes). Maps onto
/// 44-1's <c>WorkItemQuery</c>; ordering is always <c>(Rank, Key)</c> and
/// paging is KEYSET only — offset paging over a constantly-reordered board
/// duplicates and skips rows under concurrent writes, intermittently and
/// unreproducibly (plan D7).
/// </summary>
public sealed record WorkItemListQuery
{
    public Guid? ProjectId { get; init; }
    public IReadOnlyCollection<string>? Statuses { get; init; }
    public IReadOnlyCollection<string>? Kinds { get; init; }
    public Guid? AssigneeUserId { get; init; }
    public Guid? IterationId { get; init; }
    public Guid? ParentId { get; init; }
    public bool TopLevelOnly { get; init; }

    /// <summary>true = only items carrying an <c>ExternalRefJson</c>; false = only native.</summary>
    public bool? ExternalLinked { get; init; }

    public string? TitleContains { get; init; }

    /// <summary>Opaque keyset cursor from the previous page's <c>nextCursor</c>.</summary>
    public string? Cursor { get; init; }

    public int Limit { get; init; } = 100;
}
