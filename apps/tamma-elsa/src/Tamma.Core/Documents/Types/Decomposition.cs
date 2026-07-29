using System.Text.Json;
using System.Text.Json.Serialization;
using Tamma.Api.Services.Agents;

namespace Tamma.Core.Documents.Types;

/// <summary>
/// The canonical complexity vocabulary for a decomposed task (Story 39-3, Design
/// Decision D5 — strict closed label set, reject don't normalize). The legacy
/// <c>SubtaskComplexities.Normalize</c> synonym folding becomes producer-side
/// normalization at 39-13 migration time; this validator accepts ONLY the
/// canonical wires. Shipped as a <c>[Wire]</c> enum per the <c>AgentAction</c>
/// pattern.
/// </summary>
public enum TaskComplexity
{
    [Wire("low")] Low,
    [Wire("medium")] Medium,
    [Wire("high")] High,
}

/// <summary>
/// One decomposed task — an immutable mirror of the legacy
/// <c>Tamma.Activities.Decomposition.Models.Subtask</c> shape (Design Decision D2:
/// wire shape verbatim). Every property carries an explicit
/// <c>[JsonPropertyName]</c> (39-2 D8). New fields are only ever added; the old
/// parser skips unknown fields.
/// </summary>
public sealed record DecompositionTask
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("description")] public string Description { get; init; } = "";
    [JsonPropertyName("acceptanceCriteria")] public string AcceptanceCriteria { get; init; } = "";
    [JsonPropertyName("estimateHours")] public decimal EstimateHours { get; init; }
    [JsonPropertyName("complexity")] public string Complexity { get; init; } = "medium";
    [JsonPropertyName("dependsOn")] public IReadOnlyList<string> DependsOn { get; init; } = [];
}

/// <summary>
/// The structured decomposition of a complex issue: an overview
/// <see cref="Summary"/> (load-bearing — records how the breakdown preserves the
/// parent intent) plus the implementable <see cref="Subtasks"/>. The typed
/// analogue of the legacy <c>IssueDecomposition</c>; consumed downstream by
/// Stories 2-15/2-16 (dependency mapping / sequencing).
/// </summary>
public sealed record Decomposition
{
    [JsonPropertyName("summary")] public string Summary { get; init; } = "";
    [JsonPropertyName("subtasks")] public IReadOnlyList<DecompositionTask> Subtasks { get; init; } = [];
}

/// <summary>
/// <see cref="IDocumentType"/> for the <c>decomposition</c> document (Story 39-3
/// AC2). Enforces unique task ids, no dangling / self / cyclic <c>dependsOn</c>
/// (naming the cycle members), per-task sizing within 2–8h, and a computable
/// prerequisite order (topological order exists). Deliberate tightenings over the
/// fail-closed baseline parser are enumerated in the story completion notes (AC6).
/// </summary>
public sealed class DecompositionDocumentType : IDocumentType
{
    /// <summary>Payload could not be deserialized into the typed shape (type mismatch on the wire).</summary>
    public const string MalformedPayload = "MALFORMED_PAYLOAD";

    /// <summary>Baseline fail-closed: a decomposition with no overview summary is not auditable.</summary>
    public const string MissingSummary = "MISSING_SUMMARY";

    /// <summary>Baseline fail-closed: a decomposition with no usable subtasks decomposed nothing.</summary>
    public const string NoTasks = "NO_TASKS";

    /// <summary>Baseline dropped shell tasks silently — now a loud violation. A task with no id.</summary>
    public const string TaskMissingId = "TASK_MISSING_ID";

    /// <summary>A task with neither a title nor a description is an empty shell.</summary>
    public const string TaskEmptyShell = "TASK_EMPTY_SHELL";

    /// <summary>Baseline kept the first of duplicate ids silently — now a loud violation.</summary>
    public const string DuplicateTaskId = "DUPLICATE_TASK_ID";

    /// <summary>Baseline pruned dangling refs silently — now a loud violation.</summary>
    public const string DanglingDependsOn = "DANGLING_DEPENDS_ON";

    /// <summary>Baseline pruned self-references silently — now a loud violation.</summary>
    public const string SelfDependsOn = "SELF_DEPENDS_ON";

    /// <summary>Cycle in the dependency graph — new (baseline deferred cycle detection to Story 2-15).</summary>
    public const string CyclicDependsOn = "CYCLIC_DEPENDS_ON";

    /// <summary>No topological order exists — the stable signal downstream sequencing (2-15/2-16) keys on.</summary>
    public const string NoPrerequisiteOrder = "NO_PREREQUISITE_ORDER";

    /// <summary>Per-task estimate outside the 2–8h rule — new (baseline only clamped negatives to 0).</summary>
    public const string SizingOutOfRange = "SIZING_OUT_OF_RANGE";

    /// <summary>Complexity outside {low, medium, high} — strict (baseline normalized synonyms). D5.</summary>
    public const string UnknownComplexity = "UNKNOWN_COMPLEXITY";

    private const decimal MinEstimateHours = 2m;
    private const decimal MaxEstimateHours = 8m;

    private static readonly DependencyGraphCodes GraphCodes = new(
        DuplicateId: DuplicateTaskId,
        DanglingDependency: DanglingDependsOn,
        SelfDependency: SelfDependsOn,
        Cyclic: CyclicDependsOn,
        NoPrerequisiteOrder: NoPrerequisiteOrder);

    public string Key => DocumentTypeKey.Decomposition.ToWire();
    public int SchemaVersion => 1;
    public Type PayloadClrType => typeof(Decomposition);

    public DocumentValidationResult Validate(JsonElement payload) =>
        DocumentPayloadGuard.Run(payload, ValidateCore);

    private DocumentValidationResult ValidateCore(JsonElement payload)
    {
        Decomposition? doc;
        try
        {
            doc = payload.Deserialize<Decomposition>(DocumentJson.Options);
        }
        catch (JsonException)
        {
            return DocumentValidationResult.Invalid(new DocumentViolation(
                MalformedPayload, "The payload could not be parsed as a decomposition document."));
        }

        if (doc is null)
            return DocumentValidationResult.Invalid(new DocumentViolation(
                MalformedPayload, "The payload deserialized to null."));

        var violations = new List<DocumentViolation>();

        if (string.IsNullOrWhiteSpace(doc.Summary))
            violations.Add(new DocumentViolation(
                MissingSummary,
                "The decomposition has no summary — the overview that records how the breakdown " +
                "preserves the parent issue's intent is required."));

        var subtasks = doc.Subtasks ?? [];
        if (subtasks.Count == 0)
            violations.Add(new DocumentViolation(
                NoTasks, "The decomposition has no subtasks — nothing was actually decomposed."));

        foreach (var task in subtasks)
        {
            var id = task.Id?.Trim() ?? "";
            var label = string.IsNullOrWhiteSpace(id) ? "(no id)" : $"'{id}'";

            if (string.IsNullOrWhiteSpace(id))
                violations.Add(new DocumentViolation(
                    TaskMissingId,
                    "A subtask has no id — dependencies reference tasks by id, so every task must carry one."));

            if (string.IsNullOrWhiteSpace(task.Title) && string.IsNullOrWhiteSpace(task.Description))
                violations.Add(new DocumentViolation(
                    TaskEmptyShell,
                    $"Subtask {label} has neither a title nor a description — it is an empty shell."));

            if (task.EstimateHours < MinEstimateHours || task.EstimateHours > MaxEstimateHours)
                violations.Add(new DocumentViolation(
                    SizingOutOfRange,
                    $"Subtask {label} is estimated at {task.EstimateHours}h — each task must be sized " +
                    $"within {MinEstimateHours}–{MaxEstimateHours}h inclusive."));

            if (!EnumWire<TaskComplexity>.TryParse(task.Complexity ?? "", out _))
                violations.Add(new DocumentViolation(
                    UnknownComplexity,
                    $"Subtask {label} has complexity '{task.Complexity}', which is not one of low, medium, high."));
        }

        var graphNodes = subtasks
            .Where(t => !string.IsNullOrWhiteSpace(t.Id))
            .Select(t => (Id: t.Id.Trim(), DependsOn: t.DependsOn ?? (IReadOnlyList<string>)[]))
            .ToList();
        violations.AddRange(DependencyGraphCheck.Check(graphNodes, GraphCodes));

        return violations.Count == 0
            ? DocumentValidationResult.Valid()
            : DocumentValidationResult.Invalid(violations.ToArray());
    }

    public string RenderContract() => Contract;

    public IReadOnlyList<DocumentExample> Examples => s_examples;

    // ── Contract + examples ──────────────────────────────────────────────────
    // The quoted tokens below are pinned by ContractBindingTests.Bindings for the
    // (senior_developer, decompose-issue) cell → DecompositionParsing.ParseDecomposition
    // (9 tokens): "summary", "subtasks", "id", "title", "description",
    // "acceptanceCriteria", "estimateHours", "complexity", "dependsOn".
    private const string Contract =
        """
        Return ONLY a JSON object of this shape:
        {
          "summary": "how the breakdown preserves the parent issue's intent",
          "subtasks": [
            {
              "id": "ST-1",
              "title": "short task title",
              "description": "what the task delivers",
              "acceptanceCriteria": "the definition of done",
              "estimateHours": 4,
              "complexity": "low | medium | high",
              "dependsOn": ["ST-2"]
            }
          ]
        }
        Rules: every subtask needs a unique "id"; each "estimateHours" must be within 2–8h
        inclusive; "complexity" must be one of low, medium, high; "dependsOn" may only
        reference ids that exist in this document, with no self-dependency and no cycle.
        """;

    private static readonly IReadOnlyList<DocumentExample> s_examples = new[]
    {
        new DocumentExample(
            "valid-two-task-chain",
            true,
            """
            {
              "summary": "Split rate limiting into middleware then config, preserving per-tenant protection.",
              "subtasks": [
                { "id": "ST-1", "title": "Token-bucket middleware", "description": "Limiter keyed by tenant id", "acceptanceCriteria": "over-limit requests get 429", "estimateHours": 6, "complexity": "medium", "dependsOn": [] },
                { "id": "ST-2", "title": "Per-tenant config", "description": "Read the limit from tenant config", "acceptanceCriteria": "limit is configurable", "estimateHours": 4, "complexity": "low", "dependsOn": ["ST-1"] }
              ]
            }
            """),
        new DocumentExample(
            "invalid-cycle-and-sizing",
            false,
            """
            {
              "summary": "Two mutually dependent tasks, one mis-sized.",
              "subtasks": [
                { "id": "ST-1", "title": "A", "description": "a", "estimateHours": 6, "complexity": "low", "dependsOn": ["ST-2"] },
                { "id": "ST-2", "title": "B", "description": "b", "estimateHours": 1, "complexity": "low", "dependsOn": ["ST-1"] }
              ]
            }
            """,
            new[] { SizingOutOfRange, CyclicDependsOn, NoPrerequisiteOrder }),
    };
}
