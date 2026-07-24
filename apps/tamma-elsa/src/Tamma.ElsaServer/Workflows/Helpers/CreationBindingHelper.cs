using System.Text.Json;

namespace Tamma.ElsaServer.Workflows.Helpers;

/// <summary>
/// Story 39-15 (D2/D3) — the PURE, Elsa-free decision core of the two creation-family
/// lifecycle bindings: the <c>task-creation</c> binding (produces a task-breakdown
/// <see cref="Tamma.Core.Documents.Types.Plan"/>) and the <c>test-case-creation</c> binding
/// (produces a <see cref="Tamma.Core.Documents.Types.TestSpec"/>). Mirrors the
/// <see cref="PlanBindingHelper"/> / <see cref="AssessmentBindingHelper"/> posture: every
/// function is TOTAL and FAIL-CLOSED — an unreadable / missing body yields the conservative
/// empty-array projection (<c>"[]"</c>), never a fabricated success and never a throw out of a
/// routing lambda.
///
/// <para>The legacy outputs (<c>tasksJson</c> / <c>testCasesJson</c>) are BARE JSON arrays
/// projected from the accepted document body so the frozen SingleIssueCycle tasks-gate
/// (a bare-array read) and the empty-tasks failure edge fire unchanged (D2).</para>
/// </summary>
public static class CreationBindingHelper
{
    /// <summary>
    /// Project the bare <c>tasks</c> JSON array raw text from an accepted task-breakdown
    /// <c>plan</c> body. A body that is already an array is returned verbatim; an object with a
    /// <c>tasks</c> array yields that array's raw text. Fail-closed <c>"[]"</c> on empty /
    /// unreadable / shapeless input (the parent's empty-tasks failure edge then fires, D2).
    /// </summary>
    public static string ProjectTasksArray(string? planDocumentJson)
        => ProjectArray(planDocumentJson, "tasks");

    /// <summary>
    /// Project the bare <c>testCases</c> JSON array raw text from an accepted <c>test-spec</c>
    /// body (accepts the legacy <c>tests</c> alias). Fail-closed <c>"[]"</c> on empty /
    /// unreadable / shapeless input.
    /// </summary>
    public static string ProjectTestCasesArray(string? testSpecJson)
        => ProjectArray(testSpecJson, "testCases", "tests");

    /// <summary>
    /// Build the <c>validationContextJson</c> the <c>test-case-creation</c> binding forwards to
    /// the lifecycle for the D3 task-ID cross-document check. Accepts EITHER a consumed
    /// task-breakdown <c>plan</c> body (<c>{ "tasks": [...] }</c>) OR the bare <c>tasks</c> array
    /// (the runtime <c>tasksJson</c> carrier the parent passes) — the bare array is wrapped into a
    /// plan object so <c>TestSpecDocumentType.ValidateWithContext</c> can read its task ids. An
    /// empty / unreadable / task-less body yields <c>""</c> (the cross-document rule then cannot
    /// fire — payload-only validation, never a throw).
    /// </summary>
    public static string BuildTaskIdContext(string? planOrTasksJson)
    {
        if (string.IsNullOrWhiteSpace(planOrTasksJson))
            return "";
        try
        {
            using var doc = JsonDocument.Parse(planOrTasksJson!);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
            {
                if (root.GetArrayLength() == 0)
                    return "";
                return $"{{\"tasks\":{root.GetRawText()}}}";
            }

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("tasks", out var tasks) &&
                tasks.ValueKind == JsonValueKind.Array && tasks.GetArrayLength() > 0)
                return root.GetRawText();

            return "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    /// <summary>
    /// The issue-identity anchor (mirrors <see cref="PlanBindingHelper.DeriveIssueId"/>):
    /// <c>"{repository}#{issueNumber}"</c>.
    /// </summary>
    public static string DeriveIssueId(string? repository, int issueNumber)
        => $"{repository ?? string.Empty}#{issueNumber}";

    /// <summary>
    /// Story 39-15 (D2) — the producer-scoped resume anchor that disambiguates the
    /// TWO-PLANS-PER-ISSUE collision. Both <c>plan-generation</c> (system plan) and
    /// <c>task-creation</c> (task breakdown) produce documentType <c>plan</c> under the same
    /// issue; the 39-11 latest-accepted / re-entry read scopes by <c>(issueId, documentType)</c>
    /// only — it has NO producer filter (filed to 39-11) — so a task-creation lifecycle keyed on
    /// the bare issue id would re-enter on the accepted SYSTEM plan and short-circuit without ever
    /// producing the task breakdown. Scoping the task-creation lifecycle's issue id with a
    /// <c>#{producer}</c> suffix isolates its accepted-doc + event slice from the system plan's
    /// WITHOUT forking the type (D2's "don't fork the type" discipline). Types with a UNIQUE key
    /// per issue (test-spec, diagnosis, triage-decision, findings-per-item) do NOT need this.
    /// </summary>
    public static string ScopeIssueId(string? baseIssueId, string producer)
        => $"{baseIssueId ?? string.Empty}#{producer}";

    /// <summary>
    /// The failure detail for a non-accepted creation exit — names the lifecycle status and the
    /// typed outcome wire so the compat <c>error</c> output points at a typed escalation
    /// (<c>validation-exhausted</c> / <c>rounds-exhausted</c> / <c>review-undecidable</c>), never
    /// a dead terminal.
    /// </summary>
    public static string BuildFailureDetail(LifecycleBindingHelper.LifecycleExit exit)
        => string.IsNullOrWhiteSpace(exit.Outcome)
            ? $"Creation lifecycle exited '{exit.Status}' without acceptance."
            : $"Creation lifecycle exited '{exit.Status}' with outcome '{exit.Outcome}'.";

    private static string ProjectArray(string? documentJson, params string[] propertyNames)
    {
        if (string.IsNullOrWhiteSpace(documentJson))
            return "[]";
        try
        {
            using var doc = JsonDocument.Parse(documentJson!);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
                return root.GetArrayLength() == 0 ? "[]" : root.GetRawText();

            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var name in propertyNames)
                {
                    if (root.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
                        return arr.GetArrayLength() == 0 ? "[]" : arr.GetRawText();
                }
            }

            return "[]";
        }
        catch (JsonException)
        {
            return "[]";
        }
    }
}
