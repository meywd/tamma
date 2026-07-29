using System.Text;
using System.Text.Json;
using Tamma.Api.Dtos.Tracker;
using Tamma.Api.Services.Access;
using Tamma.Api.Services.PromptStore;
using Tamma.Core;
using Tamma.Core.Tracking;
using Tamma.Data;
using Tamma.Data.Entities;
using Tamma.Data.Repositories;

namespace Tamma.Api.Services.Tracker;

/// <inheritdoc />
public sealed class TrackerService(
    IProjectRepository projects,
    IWorkItemRepository workItems,
    ITrackerPreferenceRepository preferences,
    ITaskAudienceResolver audienceResolver,
    ITammaModeProvider modeProvider,
    ITenantContext tenantContext) : ITrackerService
{
    /// <summary>Separator inside the opaque keyset cursor (never a wire character).</summary>
    private const char CursorSeparator = '\u001F';

    /// <summary>AC7's discriminator when no per-user narrowing was applied.</summary>
    public const string VisibilityTenant = "tenant";

    /// <summary>AC7's discriminator when a real resolver filtered the page.</summary>
    public const string VisibilityPerUser = "per-user";


    /// <summary>
    /// Fail-loud tenant guard. Every tracker table is tenant-schema resident
    /// (epic D5), so a request that reached a handler without a resolvable
    /// tenant cannot be served — and must not surface as an unhandled
    /// <c>InvalidOperationException</c> from deep inside a repository. 409, not
    /// 500: the caller is authenticated and the request is well-formed; the
    /// server has no tenant to answer for (the ActionPolicyEndpoints
    /// PRINCIPAL_UNRESOLVED posture).
    /// </summary>
    private void EnsureTenant()
    {
        if (tenantContext.TenantId is Guid id && id != Guid.Empty)
            return;
        throw new TammaError(
            "TRACKER.TENANT_UNRESOLVED",
            "No tenant context — every tracker table is tenant-schema resident, so this "
            + "request cannot be served. Sign in against a provisioned tenant and retry.",
            retryable: false,
            severity: TammaErrorSeverity.Medium);
    }

    // ═══════════════════════════ Projects ═══════════════════════════════════

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProjectEntity>> ListProjectsAsync(bool includeArchived)
    {
        EnsureTenant();
        return await projects.ListAsync(includeArchived);
    }

    /// <inheritdoc />
    public Task<ProjectEntity?> GetProjectAsync(Guid projectId)
    {
        EnsureTenant();
        return projects.GetAsync(projectId);
    }

    /// <inheritdoc />
    public async Task<ProjectEntity> CreateProjectAsync(CreateProjectRequest request, Guid? createdByUserId)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureTenant();
        RequireField(request.Key, "key");
        RequireField(request.Name, "name");

        // Fail-loud, ordinal, non-normalizing (44-0's posture): an invalid key
        // is rejected naming the accepted shape, never lower-cased into one.
        if (!WorkItemRef.IsValidProjectKey(request.Key))
            throw InvalidProjectKey(request.Key!);

        // The ONE create-time default (documented on the DTO): a create has no
        // prior value to reset, so this is not the 43-0 defaulting class.
        var scale = request.EstimateScale ?? EstimateScale.NotUsed.ToWire();
        _ = ParseEstimateScale(scale);

        return await projects.CreateAsync(new ProjectEntity
        {
            Key = request.Key!,
            Name = request.Name!,
            Description = request.Description,
            RepositoryId = request.RepositoryId,
            EstimateScale = scale,
            CreatedByUserId = createdByUserId,
        });
    }

    /// <inheritdoc />
    public async Task<ProjectEntity?> PatchProjectAsync(
        Guid projectId, PatchProjectRequest request, int? ifMatchVersion)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureTenant();
        var existing = await projects.GetAsync(projectId);
        if (existing is null)
            return null;
        RequireVersion(existing.Version, ifMatchVersion, "project", projectId);

        // ── The 43-0 regression guard, expressed as code (AC3) ──
        // Every assignment reads `existing.X` as its fallback, so a field the
        // caller did not send is written back byte-identical. Nothing here is
        // ever defaulted from a shipped constant.
        if (request.Name.TryGet(out var name))
        {
            RequireField(name, "name"); // a present-but-null name is a 400, not a wipe
            existing.Name = name!;
        }
        if (request.Description.TryGet(out var description))
            existing.Description = description;
        if (request.RepositoryId.TryGet(out var repositoryId))
            existing.RepositoryId = repositoryId;
        if (request.EstimateScale.TryGet(out var estimateScale))
        {
            RequireField(estimateScale, "estimateScale");
            var scale = ParseEstimateScale(estimateScale!);
            // Review MINOR-9 (2026-07-29): coherence was enforced on the WORK-ITEM
            // write only, so an admin could flip a project holding estimated items
            // to `not_used` and leave behind stored estimates no work-item write
            // could ever have produced — the same representable-but-meaningless
            // state RequireEstimateCoherence exists to refuse, entered through the
            // other door. The rule is the same one (44-0's
            // EstimateScale.AllowsEstimate); this is the second call site.
            if (!scale.AllowsEstimate(1m))
                await RequireNoEstimatedItemsAsync(existing, estimateScale!);
            existing.EstimateScale = estimateScale!;
        }
        if (request.Archived.TryGet(out var archived))
        {
            RequireField(archived, "archived");
            existing.ArchivedAt = archived == true ? (existing.ArchivedAt ?? DateTime.UtcNow) : null;
        }

        // The precondition rides INTO the repository (44-2 review 2026-07-29):
        // RequireVersion above checks OUR read, but the repository re-reads in a
        // fresh context, so the check alone is not atomic with the write.
        return await projects.UpdateAsync(existing, ifMatchVersion);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteProjectAsync(Guid projectId, int? ifMatchVersion)
    {
        EnsureTenant();
        var existing = await projects.GetAsync(projectId);
        if (existing is null)
            return false;
        RequireVersion(existing.Version, ifMatchVersion, "project", projectId);
        return await projects.DeleteAsync(projectId, ifMatchVersion);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WorkItemEntity>> ProjectWorkItemsAsync(Guid projectId, int limit)
    {
        EnsureTenant();
        return await workItems.ListAsync(new WorkItemQuery { ProjectId = projectId, Limit = limit });
    }

    // ═══════════════════════════ Work items ═════════════════════════════════

    /// <inheritdoc />
    public Task<WorkItemEntity?> GetWorkItemAsync(Guid id)
    {
        EnsureTenant();
        return workItems.GetAsync(id);
    }

    /// <inheritdoc />
    public Task<WorkItemEntity?> GetWorkItemByKeyAsync(string key)
    {
        EnsureTenant();
        return workItems.GetByKeyAsync(key);
    }

    /// <inheritdoc />
    public async Task<WorkItemPage> ListWorkItemsAsync(WorkItemListQuery query, Guid? viewerUserId)
    {
        ArgumentNullException.ThrowIfNull(query);
        EnsureTenant();

        var (afterRank, afterKey) = DecodeCursor(query.Cursor);
        var limit = query.Limit is > 0 and <= 500 ? query.Limit : 100;

        // Vocabulary filters are parsed, not passed through: a typo'd status in
        // a query string would otherwise silently return an empty board.
        foreach (var status in query.Statuses ?? [])
            _ = ParseStatus(status);
        foreach (var kind in query.Kinds ?? [])
            _ = ParseKind(kind);

        var page = await workItems.ListAsync(new WorkItemQuery
        {
            ProjectId = query.ProjectId,
            Statuses = query.Statuses,
            Kinds = query.Kinds,
            AssigneeUserId = query.AssigneeUserId,
            IterationId = query.IterationId,
            ParentId = query.ParentId,
            TopLevelOnly = query.TopLevelOnly,
            ExternalLinked = query.ExternalLinked,
            TitleContains = query.TitleContains,
            AfterRank = afterRank,
            AfterKey = afterKey,
            Limit = limit,
        });

        // The cursor is computed from the LAST FETCHED row, before any
        // visibility narrowing — otherwise a fully-filtered page would return a
        // null cursor and truncate the caller's iteration at an arbitrary point.
        var nextCursor = page.Count == limit
            ? EncodeCursor(page[^1].Rank, page[^1].Key)
            : null;

        // ── AC7 / plan D6 — capability check, NOT a mode check or a flag ──
        // The shipped ITaskAudienceResolver is InitiatorOnlyTaskAudienceResolver,
        // which keys entirely on TaskRef.InitiatorUserId; applying CanSeeAsync
        // through it filters EVERY item out of EVERY list, and an empty backlog
        // reads as data loss. So: while the known stub is registered, the list
        // stays tenant-scoped and SAYS SO on the wire. The check is on the
        // registered TYPE, so it self-clears the moment Story 39-20 swaps the DI
        // registration — no code change here.
        if (IsStubResolver || modeProvider.Mode != TammaMode.SaaS || viewerUserId is not Guid viewer)
            return new WorkItemPage(page, nextCursor, VisibilityTenant);

        var tenantId = tenantContext.TenantId ?? Guid.Empty;
        var visible = new List<WorkItemEntity>(page.Count);
        foreach (var item in page)
        {
            // KNOWN GAP, recorded not fixed (review MINOR-8, 2026-07-29): the
            // TaskRef is keyed on the CREATOR. TaskRef carries exactly one
            // principal axis (InitiatorUserId) and Story 39-20 owns that shape,
            // so there is no assignee axis to add here and passing an assignee
            // AS the initiator would lie to the resolver. Consequence the day
            // 39-20's real resolver replaces the stub: an item ASSIGNED TO the
            // viewer but created by someone else is filtered OUT of that
            // viewer's own list. Dormant today — the branch is unreachable while
            // IsStubResolver is true. 39-20 must widen TaskRef (or add an
            // assignee-aware overload) at the same time it swaps the DI
            // registration; pinned by
            // Visibility_is_keyed_on_the_creator_not_the_assignee.
            var task = new TaskRef(tenantId, item.CreatedByUserId, null, item.Key);
            if (await audienceResolver.CanSeeAsync(viewer, task))
                visible.Add(item);
        }
        return new WorkItemPage(visible, nextCursor, VisibilityPerUser);
    }

    /// <summary>
    /// Whether the registered resolver is the shipped fail-closed no-op stub
    /// (Story 39-18 D9). A type check, not a feature flag — 39-20's DI swap
    /// flips every dependent branch with no edit here.
    /// </summary>
    public bool IsStubResolver => audienceResolver is InitiatorOnlyTaskAudienceResolver;

    /// <inheritdoc />
    public async Task<WorkItemEntity> CreateWorkItemAsync(
        CreateWorkItemRequest request, Guid? createdByUserId)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureTenant();
        if (request.ProjectId is not Guid projectId || projectId == Guid.Empty)
            throw MissingField("projectId");
        RequireField(request.Title, "title");
        RequireField(request.Kind, "kind");

        _ = ParseKind(request.Kind!);
        // Omitted status lands on `backlog` — documented on the DTO. Present
        // junk is rejected ordinally, never coerced.
        var status = request.Status ?? WorkItemStatus.Backlog.ToWire();
        _ = ParseStatus(status);

        var project = await projects.GetAsync(projectId) ?? throw ProjectNotFound(projectId);
        RequireEstimateCoherence(project, request.Estimate);

        return await workItems.CreateAsync(new WorkItemEntity
        {
            ProjectId = projectId,
            Kind = request.Kind!,
            Status = status,
            // Nullable end-to-end: an absent priority stores null
            // ("nobody prioritised this"), NOT `normal` (44-0 AC11).
            Priority = request.Priority,
            IssueType = request.IssueType,
            Title = request.Title!,
            Description = request.Description,
            ParentId = request.ParentId,
            IterationId = request.IterationId,
            AssigneeUserId = request.AssigneeUserId,
            CreatedByUserId = createdByUserId,
            Estimate = request.Estimate,
            ExternalRefJson = request.ExternalRef?.GetRawText(),
        });
    }

    /// <inheritdoc />
    public async Task<WorkItemEntity?> PatchWorkItemAsync(
        Guid id, PatchWorkItemRequest request, int? ifMatchVersion)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureTenant();
        var existing = await workItems.GetAsync(id);
        if (existing is null)
            return null;
        RequireVersion(existing.Version, ifMatchVersion, "work item", id);

        // ── The 43-0 regression guard (AC3) — see PatchProjectAsync ──
        if (request.Title.TryGet(out var title))
        {
            RequireField(title, "title");
            existing.Title = title!;
        }
        if (request.Description.TryGet(out var description))
            existing.Description = description;
        if (request.Kind.TryGet(out var kind))
        {
            RequireField(kind, "kind");
            _ = ParseKind(kind!);
            existing.Kind = kind!;
        }
        if (request.Priority.TryGet(out var priority))
            existing.Priority = priority; // null clears; the repo canonicalises aliases
        if (request.IssueType.TryGet(out var issueType))
            existing.IssueType = issueType;
        if (request.IterationId.TryGet(out var iterationId))
            existing.IterationId = iterationId;
        if (request.AssigneeUserId.TryGet(out var assigneeUserId))
            existing.AssigneeUserId = assigneeUserId;
        if (request.Estimate.TryGet(out var estimate))
        {
            var project = await projects.GetAsync(existing.ProjectId)
                ?? throw ProjectNotFound(existing.ProjectId);
            RequireEstimateCoherence(project, estimate);
            existing.Estimate = estimate;
        }
        if (request.ExternalRef.TryGet(out var externalRef))
            existing.ExternalRefJson = externalRef?.GetRawText();

        // See PatchProjectAsync — the precondition must be atomic with the
        // write, not merely checked against this method's own read.
        return await workItems.UpdateAsync(existing, ifMatchVersion);
    }

    /// <inheritdoc />
    public async Task<WorkItemEntity?> SetWorkItemStatusAsync(Guid id, string statusWire, int? ifMatchVersion)
    {
        EnsureTenant();
        RequireField(statusWire, "status");
        _ = ParseStatus(statusWire);

        var existing = await workItems.GetAsync(id);
        if (existing is null)
            return null;
        RequireVersion(existing.Version, ifMatchVersion, "work item", id);
        return await workItems.SetStatusAsync(id, statusWire, ifMatchVersion);
    }

    /// <inheritdoc />
    public async Task<WorkItemEntity?> AssignWorkItemAsync(Guid id, Guid? assigneeUserId, int? ifMatchVersion)
    {
        EnsureTenant();
        var existing = await workItems.GetAsync(id);
        if (existing is null)
            return null;
        RequireVersion(existing.Version, ifMatchVersion, "work item", id);

        // Assignment rides UpdateAsync — the single write seam that bumps
        // Version and leaves the frozen/owned-elsewhere columns untouched.
        existing.AssigneeUserId = assigneeUserId;
        return await workItems.UpdateAsync(existing, ifMatchVersion);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteWorkItemAsync(Guid id, int? ifMatchVersion)
    {
        EnsureTenant();
        var existing = await workItems.GetAsync(id);
        if (existing is null)
            return false;
        RequireVersion(existing.Version, ifMatchVersion, "work item", id);
        return await workItems.DeleteAsync(id, ifMatchVersion);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<WorkItemEntity>> ChildrenAsync(Guid id, int limit)
    {
        EnsureTenant();
        return await workItems.ListAsync(new WorkItemQuery { ParentId = id, Limit = limit });
    }

    // ═══════════════════════════ Preferences ════════════════════════════════
    // The ONE paired, never-joined surface (AC5/AC8). Each method touches
    // exactly one plane; nothing here reads both.

    /// <inheritdoc />
    public async Task<ResolvedTrackerPreferences> GetPreferencesAsync(Guid? userId)
    {
        EnsureTenant();
        return Resolve(await preferences.GetAsync(userId));
    }

    /// <inheritdoc />
    public async Task<ResolvedTrackerPreferences> GetPreferencesForTenantAsync(Guid tenantId)
    {
        EnsureTenant();
        return Resolve(await preferences.GetByTenantAsync(tenantId));
    }

    /// <inheritdoc />
    public async Task<ResolvedTrackerPreferences> UpsertPreferencesAsync(
        Guid? userId, UpsertTrackerPreferencesRequest request, Guid? actingUserId,
        int? ifMatchVersion = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureTenant();
        ValidatePreferences(request);
        var (entity, _) = await preferences.UpsertAsync(new TrackerPreference
        {
            UserId = userId,
            TenantId = null,
            DefaultProjectId = request.DefaultProjectId,
            DefaultKind = request.DefaultKind,
            BoardGroupBy = request.BoardGroupBy,
        }, actingUserId, ifMatchVersion);
        return Resolve(entity);
    }

    /// <inheritdoc />
    public async Task<ResolvedTrackerPreferences> UpsertPreferencesForTenantAsync(
        Guid tenantId, UpsertTrackerPreferencesRequest request, Guid? actingUserId,
        int? ifMatchVersion = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        EnsureTenant();
        ValidatePreferences(request);
        var (entity, _) = await preferences.UpsertForTenantAsync(new TrackerPreference
        {
            UserId = null,
            TenantId = tenantId,
            DefaultProjectId = request.DefaultProjectId,
            DefaultKind = request.DefaultKind,
            BoardGroupBy = request.BoardGroupBy,
        }, actingUserId, ifMatchVersion);
        return Resolve(entity);
    }

    /// <inheritdoc />
    public async Task<bool> DeletePreferencesAsync(Guid? userId, int? ifMatchVersion = null)
    {
        EnsureTenant();
        // The eager check names the ACTUAL version in the 409 (the atomic guard
        // in the repository can only say "you lost"); `ifMatchVersion` still
        // rides down, because these are two different DbContexts and only the
        // pinned token makes the precondition atomic with the DELETE. Same
        // shape as DeleteProjectAsync / DeleteWorkItemAsync.
        var existing = await preferences.GetAsync(userId);
        if (existing is null)
            return false;
        RequireVersion(existing.Version, ifMatchVersion, "tracker preferences", userId ?? Guid.Empty);
        return await preferences.DeleteAsync(userId, ifMatchVersion);
    }

    /// <inheritdoc />
    public async Task<bool> DeletePreferencesForTenantAsync(Guid tenantId, int? ifMatchVersion = null)
    {
        EnsureTenant();
        var existing = await preferences.GetByTenantAsync(tenantId);
        if (existing is null)
            return false;
        RequireVersion(existing.Version, ifMatchVersion, "tracker preferences", tenantId);
        return await preferences.DeleteByTenantAsync(tenantId, ifMatchVersion);
    }

    // ═══════════════════════════ Helpers ════════════════════════════════════

    /// <summary>Opaque (base64url) so the tuple shape can change without a wire break.</summary>
    public static string EncodeCursor(string rank, string key) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{rank}{CursorSeparator}{key}"))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>
    /// Decode a cursor. A malformed cursor is a caller error and fails loud —
    /// silently restarting at page 1 would duplicate every row already seen.
    /// </summary>
    public static (string? Rank, string? Key) DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
            return (null, null);
        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - (padded.Length % 4)) % 4);
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(padded))
                .Split(CursorSeparator, 2);
            if (parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0)
                return (parts[0], parts[1]);
        }
        catch (FormatException)
        {
            // fall through to the typed error
        }
        throw new TammaError(
            "TRACKER.INVALID_CURSOR",
            "The supplied cursor is not a cursor this API issued. Pass back the "
            + "`nextCursor` value verbatim, or omit it to start at the first page.",
            new Dictionary<string, object?> { ["cursor"] = cursor },
            retryable: false,
            severity: TammaErrorSeverity.Low);
    }

    private static ResolvedTrackerPreferences Resolve(TrackerPreference? row) => row is null
        ? new ResolvedTrackerPreferences(
            TrackerPreferenceDefaults.DefaultProjectId,
            TrackerPreferenceDefaults.DefaultKind,
            TrackerPreferenceDefaults.BoardGroupBy,
            IsOverride: false,
            Version: 0,
            UpdatedAt: null)
        : new ResolvedTrackerPreferences(
            row.DefaultProjectId,
            row.DefaultKind,
            row.BoardGroupBy,
            IsOverride: true,
            row.Version,
            row.UpdatedAt);

    private static void ValidatePreferences(UpsertTrackerPreferencesRequest request)
    {
        // DefaultKind is a closed vocabulary; BoardGroupBy is freeform by
        // design (44-1 AC6 — 44-6 owns the UI vocabulary).
        if (request.DefaultKind is not null)
            _ = ParseKind(request.DefaultKind);
    }

    /// <summary>
    /// 44-0's <c>EstimateScale.AllowsEstimate</c> called at the API boundary
    /// (story amendment 2026-07-28): storing an estimate under a
    /// <c>not_used</c>-scale project is representable and meaningless, the same
    /// defect class as <c>(Kind=Bug, Type=Feature)</c>. The rule lives in Core;
    /// this story owns calling it.
    /// </summary>
    private static void RequireEstimateCoherence(ProjectEntity project, decimal? estimate)
    {
        var scale = ParseEstimateScale(project.EstimateScale);
        if (scale.AllowsEstimate(estimate))
            return;
        throw new TammaError(
            "TRACKER.ESTIMATE_NOT_ALLOWED",
            $"Project '{project.Key}' has estimateScale '{project.EstimateScale}', so its work "
            + "items must not carry an estimate. Set the project's estimateScale first "
            + $"(one of: {string.Join(", ", Enum.GetValues<EstimateScale>().Select(s => s.ToWire()))}).",
            new Dictionary<string, object?>
            {
                ["projectId"] = project.Id,
                ["estimateScale"] = project.EstimateScale,
                ["estimate"] = estimate,
            },
            retryable: false,
            severity: TammaErrorSeverity.Medium);
    }

    /// <summary>
    /// The PROJECT-side half of estimate/scale coherence (review MINOR-9,
    /// 2026-07-29). <see cref="RequireEstimateCoherence"/> refuses an estimate
    /// under a <c>not_used</c> project; this refuses turning a project
    /// <c>not_used</c> while estimated items still hang off it. Without both,
    /// the invariant is enforceable in one direction only and the incoherent
    /// state is reachable through the project write. The 409 names how many
    /// items block it — a bare refusal forces the admin to go hunting.
    /// </summary>
    private async Task RequireNoEstimatedItemsAsync(ProjectEntity project, string targetScale)
    {
        var estimated = await workItems.ListAsync(new WorkItemQuery
        {
            ProjectId = project.Id,
            HasEstimate = true,
            Limit = EstimatedItemProbeLimit,
        });
        if (estimated.Count == 0)
            return;
        throw new TammaError(
            "TRACKER.ESTIMATE_NOT_ALLOWED",
            $"Project '{project.Key}' still holds {estimated.Count}"
            + (estimated.Count == EstimatedItemProbeLimit ? "+" : string.Empty)
            + $" work item(s) carrying an estimate, so its estimateScale cannot be set to "
            + $"'{targetScale}'. Clear those estimates first — storing an estimate under a "
            + "not_used scale is representable and meaningless, and the work-item write "
            + "already refuses it.",
            new Dictionary<string, object?>
            {
                ["projectId"] = project.Id,
                ["estimateScale"] = targetScale,
                ["blockingItems"] = estimated.Select(w => w.Key).ToList(),
            },
            retryable: false,
            severity: TammaErrorSeverity.Medium);
    }

    /// <summary>Cap on the estimated-item probe behind the project-scale guard.</summary>
    private const int EstimatedItemProbeLimit = 50;

    /// <summary>
    /// Ordinal, case-sensitive kind parse with the ONE extra affordance the
    /// story asks for (plan D9): <c>bug</c>/<c>chore</c> are
    /// <c>TriageIssueType</c> members, not kinds, and a caller sending them is
    /// pointed at the <c>issueType</c> field rather than left staring at a
    /// four-member list that does not contain the word they used.
    /// </summary>
    private static WorkItemKind ParseKind(string wire)
    {
        if (WorkItemKindExtensions.TryParse(wire, out var kind))
            return kind;
        if (TrackerPriority.AcceptedTypeWires.Contains(wire))
        {
            throw new TammaError(
                "TRACKER.UNKNOWN_KIND",
                $"kind '{wire}' is not valid; accepted: "
                + string.Join(", ", Enum.GetValues<WorkItemKind>().Select(k => k.ToWire()))
                + $". '{wire}' is an ISSUE TYPE, not a hierarchy kind — send it as `issueType` "
                + "(44-0 AC1: kind answers what may contain what; type answers what sort of thing it is).",
                new Dictionary<string, object?> { ["input"] = wire, ["field"] = "kind" },
                retryable: false,
                severity: TammaErrorSeverity.Medium);
        }
        // The shipped Core parser owns the plain message (TRACKER.UNKNOWN_KIND).
        return WorkItemKindExtensions.Parse(wire);
    }

    private static WorkItemStatus ParseStatus(string wire) => WorkItemStatusExtensions.Parse(wire);

    private static EstimateScale ParseEstimateScale(string wire) => EstimateScaleExtensions.Parse(wire);

    /// <summary>
    /// AC9 — the cross-request lost-update guard. <c>If-Match</c> is optional
    /// (a caller may opt out), but when supplied a stale version is a 409, never
    /// a silent overwrite. Work items additionally carry an EF concurrency token
    /// on <c>Version</c> (44-1), which closes the narrower in-repository window.
    /// </summary>
    private static void RequireVersion(int actual, int? ifMatchVersion, string noun, Guid id)
    {
        if (ifMatchVersion is not int expected || expected == actual)
            return;
        throw new TammaError(
            "TRACKER.CONCURRENCY_CONFLICT",
            $"The {noun} '{id}' is at version {actual}, but If-Match asked for {expected}. "
            + "Another writer changed it; re-read the resource and retry against its current state.",
            new Dictionary<string, object?>
            {
                ["id"] = id,
                ["expectedVersion"] = expected,
                ["actualVersion"] = actual,
            },
            retryable: true,
            severity: TammaErrorSeverity.Medium);
    }

    private static void RequireField(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw MissingField(field);
    }

    private static void RequireField(bool? value, string field)
    {
        if (value is null)
            throw MissingField(field);
    }

    private static TammaError MissingField(string field) => new(
        "TRACKER.MISSING_FIELD",
        $"Body field '{field}' is required and was not supplied (or was explicitly null). "
        + "A missing field is never defaulted — Story 44-2 AC3 / the 43-0 bug class.",
        new Dictionary<string, object?> { ["field"] = field },
        retryable: false,
        severity: TammaErrorSeverity.Low);

    private static TammaError ProjectNotFound(Guid projectId) => new(
        "TRACKER.PROJECT_NOT_FOUND",
        $"Project '{projectId}' does not exist in this tenant.",
        new Dictionary<string, object?> { ["projectId"] = projectId },
        retryable: false,
        severity: TammaErrorSeverity.Medium);

    private static TammaError InvalidProjectKey(string key) => new(
        "TRACKER.INVALID_WORK_ITEM_KEY",
        $"Invalid project key: '{key}'. A project key is 2-10 characters, upper-case A-Z0-9, "
        + "starting with a letter (^[A-Z][A-Z0-9]{1,9}$). Keys are never normalized — fix the input.",
        new Dictionary<string, object?> { ["projectKey"] = key },
        retryable: false,
        severity: TammaErrorSeverity.Medium);

    /// <summary>Re-parse a stored jsonb string back onto the wire as JSON, not as a string.</summary>
    public static JsonElement? ParseExternalRef(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
