using System.Text;
using System.Text.Json;

namespace Tamma.ElsaServer.Workflows.Helpers;

/// <summary>
/// Story 41-2 (D3/D4) — the PURE, Elsa-free decision core of the
/// <c>acceptance-criteria-authoring</c> binding. Same posture as
/// <see cref="CreationBindingHelper"/>: every function is TOTAL and FAIL-CLOSED — an
/// unreadable / missing body yields the conservative projection, never a fabricated success and
/// never a throw out of a routing lambda.
///
/// <para><see cref="LifecycleBindingHelper.ReadLifecycleResult"/> /
/// <see cref="LifecycleBindingHelper.IsAccepted"/> and
/// <see cref="CreationBindingHelper.BuildFailureDetail"/> are reused verbatim — this helper adds
/// only what the acceptance-criteria binding needs on top.</para>
/// </summary>
public static class AcceptanceCriteriaBindingHelper
{
    /// <summary>
    /// D3 — compose the DECLARED <c>contextFindings</c> carrier from the consumed upstream
    /// documents. <c>define-acceptance-criteria.md</c> declares
    /// <c>role, workItemJson, contextFindings, conventions</c>, so a producer variable the front
    /// matter does not declare is silently dropped at render (the 39-15 render-drop lesson):
    /// both consumed bodies ride the ONE declared carrier, each under a labelled heading so the
    /// model can tell them apart. Neither present ⇒ <c>""</c> (acceptance criteria are authorable
    /// from the issue alone — contrast 41-6's BacklogOrdering prerequisite).
    /// </summary>
    public static string BuildContextFindings(string? clarificationJson, string? findingsJson)
    {
        var clarification = Normalize(clarificationJson);
        var findings = Normalize(findingsJson);

        if (clarification.Length == 0 && findings.Length == 0)
            return "";

        var sb = new StringBuilder();
        if (clarification.Length > 0)
        {
            sb.Append("## Accepted Clarification\n");
            sb.Append(clarification);
        }
        if (findings.Length > 0)
        {
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append("## Accepted Findings\n");
            sb.Append(findings);
        }
        return sb.ToString();
    }

    /// <summary>
    /// D4 — single-parent lineage. <c>DocumentInstance</c> carries ONE
    /// <c>ParentDocumentId</c>, so the parent is the accepted <c>clarification</c> when one
    /// exists (it is the closer ancestor — it resolves the ambiguity the criteria encode), else
    /// the accepted <c>findings</c>, else <c>""</c>. The other consumed ids ride the
    /// <c>ACCEPTANCE_CRITERIA.DRAFTED</c> payload (<see cref="BuildConsumedIdsJson"/>) so the
    /// full consumes-set stays reachable from the DCB stream.
    /// </summary>
    public static string ChooseParentDocumentId(string? clarificationDocumentId, string? findingsDocumentId)
    {
        var clarification = clarificationDocumentId?.Trim() ?? "";
        if (clarification.Length > 0)
            return clarification;
        return findingsDocumentId?.Trim() ?? "";
    }

    /// <summary>
    /// The consumed-document id set for the <c>ACCEPTANCE_CRITERIA.DRAFTED</c> payload — the
    /// D4 record of every consumed edge the single <c>ParentDocumentId</c> slot cannot express.
    /// Always a well-formed JSON object; absent ids are simply omitted.
    /// </summary>
    public static string BuildConsumedIdsJson(string? clarificationDocumentId, string? findingsDocumentId)
    {
        var consumed = new Dictionary<string, string>(StringComparer.Ordinal);
        var clarification = clarificationDocumentId?.Trim() ?? "";
        var findings = findingsDocumentId?.Trim() ?? "";
        if (clarification.Length > 0) consumed["clarification"] = clarification;
        if (findings.Length > 0) consumed["findings"] = findings;
        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["consumedDocumentIds"] = consumed,
        });
    }

    /// <summary>
    /// Project the bare <c>criteria</c> JSON array raw text from an accepted
    /// <c>acceptance-criteria</c> body. A body that is already an array is returned verbatim.
    /// Fail-closed <c>"[]"</c> on empty / unreadable / shapeless input — 41-15's consumer read
    /// then sees "no criteria", never a throw.
    /// </summary>
    public static string ProjectCriteria(string? documentJson)
    {
        if (string.IsNullOrWhiteSpace(documentJson))
            return "[]";
        try
        {
            using var doc = JsonDocument.Parse(documentJson!);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Array)
                return root.GetArrayLength() == 0 ? "[]" : root.GetRawText();

            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("criteria", out var criteria) &&
                criteria.ValueKind == JsonValueKind.Array)
                return criteria.GetArrayLength() == 0 ? "[]" : criteria.GetRawText();

            return "[]";
        }
        catch (JsonException)
        {
            return "[]";
        }
    }

    /// <summary>
    /// The number of criteria in an accepted body — the <c>ACCEPTANCE_CRITERIA.DRAFTED</c>
    /// payload's <c>criteriaCount</c>. Fail-closed <c>0</c>.
    /// </summary>
    public static int CountCriteria(string? documentJson)
    {
        var projected = ProjectCriteria(documentJson);
        try
        {
            using var doc = JsonDocument.Parse(projected);
            return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    private static string Normalize(string? json)
    {
        var trimmed = json?.Trim() ?? "";
        // The 39-14 read seam reports "not found" as an empty carrier ("{}"); treat it as absent
        // rather than pasting a meaningless brace pair into the producer's context.
        return trimmed is "" or "{}" or "[]" or "null" ? "" : trimmed;
    }
}
