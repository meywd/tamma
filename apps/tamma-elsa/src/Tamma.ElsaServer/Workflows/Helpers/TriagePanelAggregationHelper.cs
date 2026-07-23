using System.Text.Json;

namespace Tamma.ElsaServer.Workflows.Helpers;

/// <summary>
/// Pure, fail-closed aggregation for the 4-role triage panel (no Elsa runtime
/// dependency). This is the lever that fixes the headline build-out bug: a
/// failed/empty per-role review is NEVER silently coalesced to a <c>{}</c>
/// participant. Each role carries a <c>status ∈ {"ok","failed"}</c>; the panel
/// exposes <c>succeededCount</c> / <c>failedRoles</c> / <c>panelStatus</c> so a
/// degraded or wholly-failed panel is a loud signal the PO and audit trail can
/// see — never a false "successful review".
///
/// <para>Mirrors the intended bar (the same deterministic posture 39-7's
/// <see cref="ReviewPanelAggregation"/> now carries for the plan-review family):
/// deterministic parse independent of LLM prose, structured fields surfaced for
/// the decider.</para>
/// </summary>
public static class TriagePanelAggregationHelper
{
    /// <summary>Panel reached quorum and every role succeeded.</summary>
    public const string StatusOk = "ok";

    /// <summary>Panel reached quorum but at least one role failed (degraded).</summary>
    public const string StatusPartial = "partial";

    /// <summary>Panel did not reach quorum (too few usable reviews) — fail-closed.</summary>
    public const string StatusFailed = "failed";

    /// <summary>
    /// A single panellist's outcome: whether it produced a usable assessment,
    /// plus the parsed structured fields (verdict / severity / suggestedLabels /
    /// notes) and the raw assessment kept alongside for audit.
    /// </summary>
    public sealed record RoleReview(
        string Role,
        bool Ok,
        string Verdict,
        string Severity,
        IReadOnlyList<string> SuggestedLabels,
        string Notes,
        string RawAssessment);

    /// <summary>
    /// The aggregated panel outcome: the per-role reviews, roster-health counts,
    /// the ordered list of roles that failed, and the overall panel status.
    /// </summary>
    public sealed record PanelResult(
        IReadOnlyList<RoleReview> Reviews,
        int ReviewCount,
        int SucceededCount,
        IReadOnlyList<string> FailedRoles,
        string PanelStatus);

    /// <summary>
    /// Classify a single role's extracted review. A role is <b>failed</b> when its
    /// review variable is null/blank, the literal <c>"{}"</c>, or unparseable —
    /// i.e. the <c>llm-call</c> yielded no usable assessment. A role is <b>ok</b>
    /// when it parsed to a non-empty JSON object. Structured fields are best-effort
    /// (verdict / severity / suggestedLabels / notes); their absence does NOT make
    /// a role failed (a usable free-form assessment still counts).
    /// </summary>
    public static RoleReview ClassifyRole(string role, string? reviewJson)
    {
        var raw = reviewJson ?? "";

        if (string.IsNullOrWhiteSpace(raw) || raw.Trim() == "{}")
        {
            return new RoleReview(role, false, "", "", Array.Empty<string>(), "", raw);
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;

            // An empty object {} (no properties) is not a usable assessment.
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new RoleReview(role, false, "", "", Array.Empty<string>(), "", raw);
            }

            var hasAny = false;
            foreach (var _ in root.EnumerateObject()) { hasAny = true; break; }
            if (!hasAny)
            {
                return new RoleReview(role, false, "", "", Array.Empty<string>(), "", raw);
            }

            var verdict = TryGetString(root, "verdict");
            var severity = TryGetString(root, "severity");
            var notes = TryGetString(root, "notes");
            var suggestedLabels = TryGetStringList(root, "suggestedLabels");

            return new RoleReview(role, true, verdict, severity, suggestedLabels, notes, raw);
        }
        catch
        {
            // Unparseable → not a usable assessment (fail-closed, never a {} pass).
            return new RoleReview(role, false, "", "", Array.Empty<string>(), "", raw);
        }
    }

    /// <summary>
    /// Aggregate a per-role roster into a panel result. The <paramref name="reviews"/>
    /// dictionary maps role → that role's raw extracted review JSON, in roster
    /// order given by <paramref name="roles"/>. The panel status is:
    /// <list type="bullet">
    ///   <item><description><c>ok</c> — every role produced a usable assessment;</description></item>
    ///   <item><description><c>partial</c> — <c>succeededCount &gt;= quorum</c> but
    ///     at least one role failed (degraded, still usable);</description></item>
    ///   <item><description><c>failed</c> — <c>succeededCount &lt; quorum</c>
    ///     (fail-closed — the parent cycle must NOT apply labels off this).</description></item>
    /// </list>
    /// </summary>
    public static PanelResult Aggregate(
        IReadOnlyList<string> roles,
        IReadOnlyDictionary<string, string?> reviews,
        int quorum)
    {
        var classified = new List<RoleReview>(roles.Count);
        var failedRoles = new List<string>();
        var succeeded = 0;

        foreach (var role in roles)
        {
            reviews.TryGetValue(role, out var reviewJson);
            var rr = ClassifyRole(role, reviewJson);
            classified.Add(rr);
            if (rr.Ok) succeeded++;
            else failedRoles.Add(role);
        }

        var effectiveQuorum = quorum < 1 ? 1 : quorum;
        string status;
        if (succeeded < effectiveQuorum)
            status = StatusFailed;
        else if (failedRoles.Count == 0)
            status = StatusOk;
        else
            status = StatusPartial;

        return new PanelResult(classified, classified.Count, succeeded, failedRoles, status);
    }

    /// <summary>
    /// Serialize a <see cref="PanelResult"/> into the <c>panelResultJson</c> output
    /// contract the PO decision consumes:
    /// <c>{ reviews:[{role,status,verdict,severity,suggestedLabels,notes,assessment}],
    /// reviewCount, succeededCount, failedRoles:[...], panelStatus }</c>.
    /// Failed roles are present in the roster with <c>status="failed"</c> and an
    /// empty assessment — they are NOT dropped (the PO sees the full roster) and
    /// NOT recorded as a <c>{}</c> success.
    /// </summary>
    public static string Serialize(PanelResult result)
    {
        var reviews = new List<object>(result.Reviews.Count);
        foreach (var r in result.Reviews)
        {
            reviews.Add(new Dictionary<string, object?>
            {
                ["role"] = r.Role,
                ["status"] = r.Ok ? "ok" : "failed",
                ["verdict"] = r.Verdict,
                ["severity"] = r.Severity,
                ["suggestedLabels"] = r.SuggestedLabels,
                ["notes"] = r.Notes,
                ["assessment"] = r.Ok ? r.RawAssessment : "",
            });
        }

        return JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["reviews"] = reviews,
            ["reviewCount"] = result.ReviewCount,
            ["succeededCount"] = result.SucceededCount,
            ["failedRoles"] = result.FailedRoles,
            ["panelStatus"] = result.PanelStatus,
        });
    }

    /// <summary>
    /// Map a panel status onto its terminal DCB event type. Kept here so the
    /// status→event mapping lives next to the status definitions.
    /// </summary>
    public static string EventTypeForStatus(string panelStatus) => panelStatus switch
    {
        StatusOk => Tamma.Activities.ADL.TriageEvents.PanelCompleted,
        StatusPartial => Tamma.Activities.ADL.TriageEvents.PanelPartial,
        _ => Tamma.Activities.ADL.TriageEvents.PanelFailed,
    };

    /// <summary>
    /// Best-effort parse of the triage item number out of <c>itemJson</c> (the
    /// <c>number</c> field). Returns 0 when absent / unparseable — the store key
    /// and event tags then read as "unknown item" rather than throwing.
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
        catch { /* unknown item */ }
        return 0;
    }

    private static string TryGetString(JsonElement root, string prop)
    {
        if (root.TryGetProperty(prop, out var v))
        {
            return v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? ""
                : v.GetRawText();
        }
        return "";
    }

    private static IReadOnlyList<string> TryGetStringList(JsonElement root, string prop)
    {
        if (root.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Array)
        {
            var list = new List<string>();
            foreach (var item in v.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                    list.Add(item.GetString() ?? "");
                else
                    list.Add(item.GetRawText());
            }
            return list;
        }
        return Array.Empty<string>();
    }
}
