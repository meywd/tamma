using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tamma.Api.Dtos.Tracker;

// ─────────────────────────────────────────────────────────────────────────────
// Story 44-2 AC1/AC3 — the tracker wire contract.
//
// Every property is [JsonPropertyName]d and every VOCABULARY field is a wire
// STRING, never an enum ordinal: the enums (WorkItemKind/WorkItemStatus/
// TriagePriority/TriageIssueType/EstimateScale) carry [Wire] attributes whose
// declaration order is load-bearing elsewhere, and serializing the ordinal
// would make a member insertion a silent wire break.
//
// PATCH bodies use Optional<T> (AC3 / plan D2 — the 43-0 bug class): "absent"
// and "explicitly null" are DIFFERENT instructions and must remain
// distinguishable at the model-binding layer. A defaulted full-body PUT is
// what silently reset acceptorRequirement on every acceptance-rules save; the
// tracker never repeats it.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// Tri-state field wrapper for PATCH bodies (Story 44-2 AC3 / plan D2).
/// <list type="bullet">
/// <item><b>absent</b> — <see cref="IsSet"/> is <c>false</c>: leave the stored
/// column exactly as it is. This is the state a plain nullable property CANNOT
/// express, and its absence is the 43-0 defect.</item>
/// <item><b>present and null</b> — <see cref="IsSet"/> is <c>true</c>,
/// <see cref="Value"/> is <c>null</c>: clear the column.</item>
/// <item><b>present with a value</b> — set the column.</item>
/// </list>
/// <para>The converter carries <c>HandleNull = true</c>. <b>The rationale first
/// written here was wrong and is corrected (review MODERATE-6, 2026-07-29):</b>
/// it claimed that without the override System.Text.Json short-circuits a JSON
/// <c>null</c> to <c>default</c> (= UNSET), turning "clear this field" into
/// "leave it alone". It does not. STJ's default is
/// <c>HandleNullOnRead = !CanBeNull</c>, and <c>Optional&lt;T&gt;</c> is a
/// non-nullable STRUCT, so <c>CanBeNull</c> is false and <c>Read</c> already
/// receives the <c>Null</c> token — the converter would yield
/// <c>IsSet = true, Value = null</c> for an explicit null with or without the
/// override. The BEHAVIOUR the tri-state depends on was never at risk.
/// <c>HandleNullOnWrite</c> is irrelevant here: <c>Optional&lt;T&gt;</c> appears
/// in request records only, never in a response type.</para>
///
/// <para>The override is KEPT deliberately, as explicitness and as defence: it
/// states the requirement at the one place a reader looks, and it pins the
/// behaviour against a future change that makes this type nullable-shaped
/// (a <c>class</c>, or a <c>Nullable</c>-annotated member), at which point
/// <c>CanBeNull</c> flips, the STJ default flips with it, and the tri-state
/// WOULD silently collapse. Cheap insurance against a one-word edit.</para>
/// </summary>
[JsonConverter(typeof(OptionalJsonConverterFactory))]
public readonly struct Optional<T>
{
    private Optional(T? value)
    {
        IsSet = true;
        Value = value;
    }

    /// <summary>True when the field was present in the request body (even as null).</summary>
    public bool IsSet { get; }

    /// <summary>The supplied value; meaningless unless <see cref="IsSet"/>.</summary>
    public T? Value { get; }

    /// <summary>The absent state — the default, so an omitted JSON property lands here.</summary>
    public static Optional<T> Unset => default;

    /// <summary>The present state (value may be null = "clear").</summary>
    public static Optional<T> Set(T? value) => new(value);

    /// <summary>Non-throwing read: false when the caller did not send the field.</summary>
    public bool TryGet(out T? value)
    {
        value = Value;
        return IsSet;
    }

    /// <summary>The supplied value when present, otherwise <paramref name="current"/>.</summary>
    public T? Or(T? current) => IsSet ? Value : current;
}

/// <summary>Factory binding every closed <see cref="Optional{T}"/> to its converter.</summary>
public sealed class OptionalJsonConverterFactory : JsonConverterFactory
{
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType
        && typeToConvert.GetGenericTypeDefinition() == typeof(Optional<>);

    /// <inheritdoc />
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(
            typeof(OptionalJsonConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;
}

/// <summary>Reads/writes one <see cref="Optional{T}"/>; see the type doc for HandleNull.</summary>
internal sealed class OptionalJsonConverter<T> : JsonConverter<Optional<T>>
{
    /// <summary>
    /// A JSON <c>null</c> is the "clear this field" instruction and must reach
    /// <see cref="Read"/> rather than being folded into <c>default</c>
    /// (= unset). Because <see cref="Optional{T}"/> is a non-nullable struct,
    /// STJ's own default (<c>HandleNullOnRead = !CanBeNull</c>) ALREADY delivers
    /// the null token — see the <see cref="Optional{T}"/> doc for why this
    /// override is nevertheless kept rather than removed as redundant.
    /// </summary>
    public override bool HandleNull => true;

    public override Optional<T> Read(
        ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Optional<T>.Set(JsonSerializer.Deserialize<T>(ref reader, options));

    public override void Write(Utf8JsonWriter writer, Optional<T> value, JsonSerializerOptions options)
    {
        if (!value.IsSet || value.Value is null)
        {
            writer.WriteNullValue();
            return;
        }
        JsonSerializer.Serialize(writer, value.Value, options);
    }
}

// ── Projects ────────────────────────────────────────────────────────────────

/// <summary>A project as returned by <c>/api/projects</c>.</summary>
public sealed record ProjectResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("repositoryId")] Guid? RepositoryId,
    // <c>EstimateScale</c> wire string.
    [property: JsonPropertyName("estimateScale")] string EstimateScale,
    [property: JsonPropertyName("nextNumber")] int NextNumber,
    [property: JsonPropertyName("archivedAt")] DateTime? ArchivedAt,
    [property: JsonPropertyName("createdByUserId")] Guid? CreatedByUserId,
    [property: JsonPropertyName("createdAt")] DateTime CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTime UpdatedAt,
    // Optimistic-concurrency token; also the response <c>ETag</c> (AC9).
    [property: JsonPropertyName("version")] int Version);

/// <summary>
/// POST /api/projects body. <c>key</c> is validated by
/// <c>WorkItemRef.IsValidProjectKey</c> and is FROZEN once minted.
/// <c>estimateScale</c> is the one create-time default (<c>not_used</c>, the
/// shipped column default) — a create has no prior value to reset, so this is
/// not the 43-0 defaulting bug; PATCH never defaults.
/// </summary>
public sealed record CreateProjectRequest(
    [property: JsonPropertyName("key")] string? Key,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("repositoryId")] Guid? RepositoryId,
    [property: JsonPropertyName("estimateScale")] string? EstimateScale);

/// <summary>
/// PATCH /api/projects/{projectId} body — tri-state per field (AC3).
/// <c>key</c> is deliberately absent: the key prefix is frozen and a rename is
/// the per-item <c>IWorkItemRepository.RekeyAsync</c> seam.
/// </summary>
public sealed class PatchProjectRequest
{
    [JsonPropertyName("name")] public Optional<string> Name { get; init; }
    [JsonPropertyName("description")] public Optional<string> Description { get; init; }
    [JsonPropertyName("repositoryId")] public Optional<Guid?> RepositoryId { get; init; }
    [JsonPropertyName("estimateScale")] public Optional<string> EstimateScale { get; init; }

    /// <summary>true archives (stamps <c>ArchivedAt</c>), false un-archives.</summary>
    [JsonPropertyName("archived")] public Optional<bool?> Archived { get; init; }
}

// ── Work items ──────────────────────────────────────────────────────────────

/// <summary>A work item as returned by <c>/api/work-items</c>.</summary>
public sealed record WorkItemResponse(
    [property: JsonPropertyName("id")] Guid Id,
    [property: JsonPropertyName("projectId")] Guid ProjectId,
    // The frozen wire key (<c>TAM-142</c>) — never re-minted on a move.
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("previousKeys")] IReadOnlyList<string> PreviousKeys,
    [property: JsonPropertyName("number")] int Number,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("status")] string Status,
    // Derived once, by <c>WorkItemStatusCategoryExtensions.Category</c>.
    [property: JsonPropertyName("statusCategory")] string StatusCategory,
    // Nullable end-to-end: null is "unprioritised", NOT <c>normal</c> (44-0 AC11).
    [property: JsonPropertyName("priority")] string? Priority,
    [property: JsonPropertyName("issueType")] string? IssueType,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("parentId")] Guid? ParentId,
    [property: JsonPropertyName("iterationId")] Guid? IterationId,
    [property: JsonPropertyName("rank")] string Rank,
    [property: JsonPropertyName("siblingRank")] string SiblingRank,
    [property: JsonPropertyName("assigneeUserId")] Guid? AssigneeUserId,
    [property: JsonPropertyName("createdByUserId")] Guid? CreatedByUserId,
    [property: JsonPropertyName("estimate")] decimal? Estimate,
    [property: JsonPropertyName("externalRef")] JsonElement? ExternalRef,
    [property: JsonPropertyName("createdAt")] DateTime CreatedAt,
    [property: JsonPropertyName("updatedAt")] DateTime UpdatedAt,
    [property: JsonPropertyName("closedAt")] DateTime? ClosedAt,
    [property: JsonPropertyName("version")] int Version);

/// <summary>
/// POST /api/work-items body. <c>status</c> is optional and lands on
/// <c>backlog</c> when omitted (the documented create-time default; a create
/// has no prior value). <c>priority</c> omitted stores <c>null</c> —
/// deliberately NOT <c>normal</c> (44-0 AC11 / epic D10).
/// </summary>
public sealed record CreateWorkItemRequest(
    [property: JsonPropertyName("projectId")] Guid? ProjectId,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("kind")] string? Kind,
    [property: JsonPropertyName("status")] string? Status,
    [property: JsonPropertyName("priority")] string? Priority,
    [property: JsonPropertyName("issueType")] string? IssueType,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("parentId")] Guid? ParentId,
    [property: JsonPropertyName("iterationId")] Guid? IterationId,
    [property: JsonPropertyName("assigneeUserId")] Guid? AssigneeUserId,
    [property: JsonPropertyName("estimate")] decimal? Estimate,
    [property: JsonPropertyName("externalRef")] JsonElement? ExternalRef);

/// <summary>
/// PATCH /api/work-items/{id} body — tri-state per field (AC3).
/// <c>status</c> (POST …/status), <c>rank</c>/<c>siblingRank</c> and
/// <c>parentId</c> (44-3), <c>key</c>/<c>number</c> (frozen) and
/// <c>projectId</c> are deliberately NOT patchable here.
/// </summary>
public sealed class PatchWorkItemRequest
{
    [JsonPropertyName("title")] public Optional<string> Title { get; init; }
    [JsonPropertyName("description")] public Optional<string> Description { get; init; }
    [JsonPropertyName("kind")] public Optional<string> Kind { get; init; }
    [JsonPropertyName("priority")] public Optional<string> Priority { get; init; }
    [JsonPropertyName("issueType")] public Optional<string> IssueType { get; init; }
    [JsonPropertyName("iterationId")] public Optional<Guid?> IterationId { get; init; }
    [JsonPropertyName("assigneeUserId")] public Optional<Guid?> AssigneeUserId { get; init; }
    [JsonPropertyName("estimate")] public Optional<decimal?> Estimate { get; init; }
    [JsonPropertyName("externalRef")] public Optional<JsonElement?> ExternalRef { get; init; }
}

/// <summary>
/// POST /api/work-items/{id}/assign body — one required-nullable field: an
/// absent <c>assigneeUserId</c> is a 400 (never a silent unassign), an explicit
/// null unassigns.
/// </summary>
public sealed class AssignRequest
{
    [JsonPropertyName("assigneeUserId")] public Optional<Guid?> AssigneeUserId { get; init; }
}

/// <summary>POST /api/work-items/{id}/status body — the wire status string.</summary>
public sealed record SetStatusRequest(
    [property: JsonPropertyName("status")] string? Status);

/// <summary>
/// GET /api/work-items envelope. <see cref="VisibilityMode"/> is AC7's honesty
/// discriminator: <c>tenant</c> means no per-user narrowing was applied (the
/// shipped <c>ITaskAudienceResolver</c> stub is a total no-op, so applying it
/// would empty every backlog); <c>per-user</c> means a real resolver filtered.
/// </summary>
public sealed record WorkItemListResponse(
    [property: JsonPropertyName("items")] IReadOnlyList<WorkItemResponse> Items,
    // Opaque keyset cursor for the next page; null at the end.
    [property: JsonPropertyName("nextCursor")] string? NextCursor,
    [property: JsonPropertyName("visibilityMode")] string VisibilityMode);

/// <summary>One assignable user.</summary>
public sealed record AssignableMemberResponse(
    [property: JsonPropertyName("userId")] Guid UserId,
    [property: JsonPropertyName("role")] string Role);

/// <summary>
/// GET /api/work-items/assignable envelope. <see cref="Source"/> is AC6's
/// discriminator — <c>audience-resolver</c> | <c>tenant-membership</c> |
/// <c>single-user</c> — so the UI can say which set it is showing instead of
/// rendering an unexplained (or, naively, empty) picker.
/// </summary>
public sealed record AssignableResponse(
    [property: JsonPropertyName("members")] IReadOnlyList<AssignableMemberResponse> Members,
    [property: JsonPropertyName("source")] string Source);

// ── Preferences ─────────────────────────────────────────────────────────────

/// <summary>
/// GET/PUT /api/tracker/preferences response. <see cref="Source"/> is
/// <c>principal-override</c> when a stored row answered and
/// <c>system-default</c> when the shipped defaults did (AC8's
/// override → default resolution, the <c>AcceptanceRulesService</c> posture).
/// </summary>
public sealed record TrackerPreferencesResponse(
    [property: JsonPropertyName("defaultProjectId")] Guid? DefaultProjectId,
    [property: JsonPropertyName("defaultKind")] string? DefaultKind,
    [property: JsonPropertyName("boardGroupBy")] string? BoardGroupBy,
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("updatedAt")] DateTime? UpdatedAt);

/// <summary>
/// PUT /api/tracker/preferences body. This is the ONE surviving PUT (plan D2):
/// the preference row genuinely IS the whole resource — three fields, all
/// nullable, all replaced — so there is no "absent means unchanged" ambiguity
/// to protect against.
/// </summary>
public sealed record UpsertTrackerPreferencesRequest(
    [property: JsonPropertyName("defaultProjectId")] Guid? DefaultProjectId,
    [property: JsonPropertyName("defaultKind")] string? DefaultKind,
    [property: JsonPropertyName("boardGroupBy")] string? BoardGroupBy);
