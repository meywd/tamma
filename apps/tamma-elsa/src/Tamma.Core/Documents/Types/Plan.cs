using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tamma.Core.Documents.Types;

/// <summary>
/// One task in a <see cref="Plan"/> (Story 39-4, Design Decision D5). Each task
/// names the <see cref="Files"/> it touches (the per-task file map AC4 requires),
/// its <see cref="DependsOn"/> prerequisites (resolvable within the plan), and its
/// <see cref="Testing"/> approach (non-empty per task).
/// </summary>
public sealed record PlanTask
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("description")] public string Description { get; init; } = "";
    [JsonPropertyName("files")] public IReadOnlyList<string> Files { get; init; } = [];
    [JsonPropertyName("dependsOn")] public IReadOnlyList<string> DependsOn { get; init; } = [];
    [JsonPropertyName("testing")] public string Testing { get; init; } = "";
}

/// <summary>
/// An implementation plan: an ordered set of <see cref="Tasks"/>, each with a file
/// map / dependencies / testing. A root-level <see cref="Files"/> list is preserved
/// verbatim for the transition window (Design Decision D5) — <c>PlanValidationHelper.ValidatePlan</c>
/// requires a root <c>tasks|steps</c> AND a root <c>fileMap|files|filesToModify</c>,
/// so round-trip (AC8) re-serializes to JSON the old checker still passes. Validator
/// rules run on <see cref="Tasks"/> only; the root list is carry-through.
/// </summary>
public sealed record Plan
{
    [JsonPropertyName("tasks")] public IReadOnlyList<PlanTask> Tasks { get; init; } = [];

    /// <summary>Root-level file list preserved verbatim (D5 carry-through); not validated.</summary>
    [JsonPropertyName("files")] public IReadOnlyList<string>? Files { get; init; }
}

/// <summary>
/// <see cref="IDocumentType"/> for the <c>plan</c> document (Story 39-4 AC4).
/// Enforces a per-task file map, a per-task testing approach, and resolvable task
/// dependencies (no dangling / self / cyclic references, a topological order exists),
/// reusing the shared <see cref="DependencyGraphCheck"/> (D9). Subsumes what
/// <c>PlanValidationHelper.ValidatePlan</c> deterministically checks today.
/// </summary>
public sealed class PlanDocumentType : IDocumentType
{
    /// <summary>Payload could not be deserialized into the typed shape.</summary>
    public const string MalformedPayload = "MALFORMED_PAYLOAD";

    /// <summary>No tasks — subsumes ValidatePlan's "Empty plan" / "Missing 'tasks' or 'steps'".</summary>
    public const string EmptyPlan = "EMPTY_PLAN";

    /// <summary>A task names no files — subsumes ValidatePlan's "Missing file map", now per-task.</summary>
    public const string TaskMissingFileMap = "TASK_MISSING_FILE_MAP";

    /// <summary>A task states no testing approach (AC4 — testing stated per task).</summary>
    public const string TaskMissingTesting = "TASK_MISSING_TESTING";

    /// <summary>Two tasks share an id — ids must be unique so dependencies are unambiguous.</summary>
    public const string DuplicateTaskId = "DUPLICATE_TASK_ID";

    /// <summary>A task depends on an id that is not a task in this plan.</summary>
    public const string DanglingDependsOn = "DANGLING_DEPENDS_ON";

    /// <summary>A task depends on itself.</summary>
    public const string SelfDependsOn = "SELF_DEPENDS_ON";

    /// <summary>A cycle in the task dependency graph (message names the cycle path).</summary>
    public const string CyclicDependsOn = "CYCLIC_DEPENDS_ON";

    /// <summary>No topological order exists (AC4 — its own code) — the stable downstream-sequencing signal.</summary>
    public const string NoTopologicalOrder = "NO_TOPOLOGICAL_ORDER";

    private static readonly DependencyGraphCodes GraphCodes = new(
        DuplicateId: DuplicateTaskId,
        DanglingDependency: DanglingDependsOn,
        SelfDependency: SelfDependsOn,
        Cyclic: CyclicDependsOn,
        NoPrerequisiteOrder: NoTopologicalOrder);

    public string Key => DocumentTypeKey.Plan.ToWire();
    public int SchemaVersion => 1;
    public Type PayloadClrType => typeof(Plan);

    public DocumentValidationResult Validate(JsonElement payload)
    {
        Plan? doc;
        try
        {
            doc = payload.Deserialize<Plan>(DocumentJson.Options);
        }
        catch (JsonException)
        {
            return DocumentValidationResult.Invalid(new DocumentViolation(
                MalformedPayload, "The payload could not be parsed as a plan document."));
        }

        if (doc is null)
            return DocumentValidationResult.Invalid(new DocumentViolation(
                MalformedPayload, "The payload deserialized to null."));

        var violations = new List<DocumentViolation>();

        var tasks = doc.Tasks ?? [];
        if (tasks.Count == 0)
            violations.Add(new DocumentViolation(
                EmptyPlan, "The plan has no tasks — an empty plan plans nothing."));

        foreach (var task in tasks)
        {
            var id = task.Id?.Trim() ?? "";
            var label = string.IsNullOrWhiteSpace(id) ? "(no id)" : $"'{id}'";

            if ((task.Files ?? []).All(string.IsNullOrWhiteSpace))
                violations.Add(new DocumentViolation(
                    TaskMissingFileMap,
                    $"Task {label} names no files — every task must map the files it touches."));

            if (string.IsNullOrWhiteSpace(task.Testing))
                violations.Add(new DocumentViolation(
                    TaskMissingTesting,
                    $"Task {label} states no testing approach — every task must say how it is tested."));
        }

        var graphNodes = tasks
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
    // The (architect, plan-system-design) cell is bound in ContractBindingTests to
    // PlanValidationHelper.ValidatePlan, whose shipped template pins "tasks" + "files"
    // — both appear below so 39-16 can regenerate the cell from this renderer without
    // breaking the CI-enforced binding.
    private const string Contract =
        """
        Return ONLY a JSON object of this shape:
        {
          "tasks": [
            {
              "id": "T-1",
              "description": "what the task delivers",
              "files": ["src/Foo.cs"],
              "dependsOn": ["T-2"],
              "testing": "how this task is tested"
            }
          ],
          "files": ["src/Foo.cs"]
        }
        Rules: every task needs a unique "id", a non-empty "files" map, and a non-empty
        "testing" approach; "dependsOn" may only reference ids that exist in this plan,
        with no self-dependency and no cycle (a topological order must exist).
        """;

    private static readonly IReadOnlyList<DocumentExample> s_examples = new[]
    {
        new DocumentExample(
            "valid-two-task-plan",
            true,
            """
            {
              "tasks": [
                { "id": "T-1", "description": "Add users table", "files": ["db/001_users.sql"], "dependsOn": [], "testing": "migration applies cleanly" },
                { "id": "T-2", "description": "Login endpoint", "files": ["src/Login.cs"], "dependsOn": ["T-1"], "testing": "200 + token integration test" }
              ],
              "files": ["db/001_users.sql", "src/Login.cs"]
            }
            """),
        new DocumentExample(
            "invalid-cyclic-dependencies",
            false,
            """
            {
              "tasks": [
                { "id": "T-1", "description": "A", "files": ["a.cs"], "dependsOn": ["T-2"], "testing": "unit" },
                { "id": "T-2", "description": "B", "files": ["b.cs"], "dependsOn": ["T-1"], "testing": "unit" }
              ],
              "files": ["a.cs", "b.cs"]
            }
            """,
            new[] { CyclicDependsOn, NoTopologicalOrder }),
    };
}
