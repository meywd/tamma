using System.Text.Json;
using Tamma.Api.Services.Agents;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Policy;
using Tamma.Core.Documents.Types;

namespace Tamma.ElsaServer.Workflows.Helpers;

/// <summary>
/// Story 39-15 (D5/D6/D9) — the PURE, Elsa-free decision core of the triage family's
/// two lifecycle bindings (the <c>triage-context-gathering</c> Findings binding and the
/// <c>triage-po-decision</c> TriageDecision binding). Mirrors the sibling binding helpers
/// (<see cref="PlanBindingHelper"/> / <see cref="CreationBindingHelper"/> /
/// <see cref="DiagnosisBindingHelper"/>): every function is TOTAL and FAIL-CLOSED — an
/// unreadable / missing body yields a conservative fallback (never a fabricated clean
/// decision), and no function throws out of a routing lambda.
///
/// <para>The typed lifecycle-exit read is SHARED via
/// <see cref="LifecycleBindingHelper.ReadLifecycleResult"/> (not duplicated here); this
/// helper carries only the triage-family-specific pieces: the legacy <c>decisionJson</c>
/// projection (the wire <see cref="TriagePoDecisionHelper"/> round-trips), the panel
/// event-mirror counts (D6), the behavior-preserving default triage acceptance rules
/// (panel roster + quorum 2 + a needs-human always-escalate class), and the failure-detail
/// wire.</para>
/// </summary>
public static class TriageBindingHelper
{
    /// <summary>The <c>triage-decision</c> document-type key (39-4).</summary>
    public const string TriageDecisionDocumentType = "triage-decision";

    /// <summary>The <c>findings</c> document-type key the triage-context binding produces (39-13 recipe).</summary>
    public const string FindingsDocumentType = "findings";

    /// <summary>Legacy <c>contextStatus</c> — usable context gathered (the cycle runs the decision).</summary>
    public const string ContextStatusOk = "ok";

    /// <summary>Legacy <c>contextStatus</c> — no context gathered (the cycle skips).</summary>
    public const string ContextStatusFailed = "failed";

    // ------------------------------------------------------------------
    // decisionJson projection (D9)
    // ------------------------------------------------------------------

    /// <summary>
    /// Project an accepted <see cref="TriageDecision"/> document body (the terminal
    /// lifecycle revision payload) onto the legacy <c>decisionJson</c> wire the cycle +
    /// <c>ApplyTriageResultActivity</c> read: <c>status="ok"</c> plus
    /// priority/type/complexity/automation/labels/comment. The accepted TriageDecision
    /// serializes to the exact shape <see cref="TriagePoDecisionHelper.ParseDecision"/>
    /// round-trips clean (StatusOk, zero clamps — 39-4 D6's pin). Fail-closed: an
    /// empty / unreadable body yields an honest <c>unparsed</c> needs-human decision,
    /// NEVER a fabricated clean classification.
    /// </summary>
    public static string ProjectLegacyDecisionJson(string? documentJson)
    {
        if (string.IsNullOrWhiteSpace(documentJson))
            return TriagePoDecisionHelper.Serialize(TriagePoDecisionHelper.ParseDecision(null));
        try
        {
            var decision = JsonSerializer.Deserialize<TriageDecision>(documentJson!, DocumentJson.Options);
            // A null OR field-incomplete body (e.g. "{}") is NOT a usable accepted decision — fall
            // closed to the honest unparsed needs-human decision, never a blank clean classification.
            if (decision is null
                || string.IsNullOrWhiteSpace(decision.Priority)
                || string.IsNullOrWhiteSpace(decision.Type)
                || string.IsNullOrWhiteSpace(decision.Complexity)
                || string.IsNullOrWhiteSpace(decision.Automation))
                return TriagePoDecisionHelper.Serialize(TriagePoDecisionHelper.ParseDecision(null));

            var dict = new Dictionary<string, object?>
            {
                ["status"] = TriagePoDecisionHelper.StatusOk,
                ["priority"] = decision.Priority,
                ["type"] = decision.Type,
                ["complexity"] = decision.Complexity,
                ["automation"] = decision.Automation,
                ["labels"] = decision.Labels ?? Array.Empty<string>(),
                ["comment"] = decision.Comment ?? "",
            };
            if (!string.IsNullOrWhiteSpace(decision.Reasoning))
                dict["reasoning"] = decision.Reasoning;
            return JsonSerializer.Serialize(dict);
        }
        catch (JsonException)
        {
            return TriagePoDecisionHelper.Serialize(TriagePoDecisionHelper.ParseDecision(null));
        }
    }

    // ------------------------------------------------------------------
    // Panel event mirror (D6)
    // ------------------------------------------------------------------

    /// <summary>The panel event-mirror counts surfaced at the REVIEW boundary (D6).</summary>
    public sealed record PanelMirror(int MemberCount, int SucceededCount, string FailedRolesJson);

    /// <summary>
    /// Read the panel member / failed counts for the <c>TRIAGE.PANEL.*</c> mirrors from the
    /// lifecycle result's lineage. The generic lifecycle result does not surface per-member
    /// panel data, so this PREFERS any <c>panelMemberCount</c> / <c>panelSucceededCount</c> /
    /// <c>panelFailedRoles</c> keys the review boundary may carry and otherwise derives a
    /// conservative mirror from the accept signal + the roster size (accepted ⇒ full roster
    /// usable, non-accept ⇒ zero usable). Fail-closed — never throws.
    /// </summary>
    public static PanelMirror ReadPanelMirror(string? lifecycleResultJson, bool accepted, int rosterSize)
    {
        var member = rosterSize;
        var succeeded = accepted ? rosterSize : 0;
        var failed = "[]";

        if (!string.IsNullOrWhiteSpace(lifecycleResultJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(lifecycleResultJson!);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("panelMemberCount", out var mc) && mc.ValueKind == JsonValueKind.Number && mc.TryGetInt32(out var m))
                        member = m;
                    if (root.TryGetProperty("panelSucceededCount", out var sc) && sc.ValueKind == JsonValueKind.Number && sc.TryGetInt32(out var s))
                        succeeded = s;
                    if (root.TryGetProperty("panelFailedRoles", out var fr) && fr.ValueKind == JsonValueKind.Array)
                        failed = fr.GetRawText();
                }
            }
            catch (JsonException)
            {
                // fall through to the conservative derived mirror
            }
        }

        return new PanelMirror(member, succeeded, failed);
    }

    // ------------------------------------------------------------------
    // Default acceptance rules (39-5 mechanism)
    // ------------------------------------------------------------------

    /// <summary>
    /// The behavior-preserving default acceptance rules for the <c>triage-decision</c> type
    /// (D5), used ONLY when the caller passes no explicit <c>acceptanceRulesJson</c>. Ships
    /// the four triage roles as the REVIEW panel roster (the retired
    /// <c>TriagePanelReviewWorkflow</c>'s roster), quorum 2 (today's
    /// <c>TriageEvents.DefaultQuorum</c>) with a MAJORITY rule, and a
    /// <c>needs-human</c>-class always-escalate entry (the automation vocabulary member — a
    /// needs-human draft always escalates to a human). Validated fail-loud (an invalid
    /// default refuses to build).
    /// </summary>
    public static string DefaultTriageRulesJson()
    {
        var roster = ReviewerSelectionHelper.TriagePanelRoster.Select(r => r.ToWire()).ToArray();
        var rules = (AcceptanceDefaults.Rules with
        {
            AlwaysEscalate = new[]
            {
                new EscalationClass(EscalationClassKind.AgentAction, AgentAction.TriageIntake.ToWire()),
            },
            ReviewerSelection = new ReviewerSelection(
                Mode: ReviewerMode.Panel,
                ReviewerRole: null,
                PanelRoles: roster,
                Quorum: 2,
                DecisionRule: ReviewDecisionRule.Majority),
        }).Validate();
        return AcceptanceRulesJson.Serialize(rules);
    }

    // ------------------------------------------------------------------
    // Failure detail
    // ------------------------------------------------------------------

    /// <summary>
    /// The failure detail for a non-accepted triage exit — names the lifecycle status and the
    /// typed outcome wire so audit points at a typed escalation, never a dead terminal.
    /// </summary>
    public static string BuildFailureDetail(LifecycleBindingHelper.LifecycleExit exit)
        => string.IsNullOrWhiteSpace(exit.Outcome)
            ? $"Triage lifecycle exited '{exit.Status}' without acceptance."
            : $"Triage lifecycle exited '{exit.Status}' with outcome '{exit.Outcome}'.";

    // ------------------------------------------------------------------
    // Shared item-number parse (relocated from the deleted TriagePanelAggregationHelper)
    // ------------------------------------------------------------------

    /// <summary>
    /// Best-effort parse of the triage item number out of <c>itemJson</c> for event tags.
    /// Returns 0 (unknown item) on blank / unparseable / number-less input rather than
    /// throwing. Relocated here from the deleted <c>TriagePanelAggregationHelper</c> so the
    /// triage bindings + cycle parse the item number identically.
    /// </summary>
    public static int ParseItemNumber(string? itemJson)
    {
        if (string.IsNullOrWhiteSpace(itemJson)) return 0;
        try
        {
            using var doc = JsonDocument.Parse(itemJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("number", out var n) &&
                n.ValueKind == JsonValueKind.Number &&
                n.TryGetInt32(out var v))
            {
                return v;
            }
        }
        catch (JsonException) { /* unknown item */ }
        return 0;
    }
}
