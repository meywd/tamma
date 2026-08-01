using System.Text;
using System.Text.Json;

namespace Tamma.ElsaServer.Workflows.Helpers;

/// <summary>
/// Story 41-3 (D2/D3) — the PURE, Elsa-free decision core of the <c>backlog-prioritization</c>
/// binding, and the OWNER of the set-scoped lineage-anchor contract that 41-4 (roadmap) and 41-6
/// (sprint planning) consume by name. Same posture as <see cref="CreationBindingHelper"/> and
/// <see cref="AcceptanceCriteriaBindingHelper"/>: every function is TOTAL and FAIL-CLOSED — a
/// null / malformed / hostile input yields the conservative projection, never a throw out of a
/// routing lambda and never a fabricated success.
///
/// <para><b>Why the anchor exists (D2).</b> A <c>BacklogOrdering</c> is not about one issue — it
/// ranks a SET. But <c>DocumentInstance.IssueId</c> is a required non-null string and the ONLY
/// read key the 39-11 store exposes (<c>IDocumentInstanceRepository</c> has
/// <c>GetByIdAsync</c> / <c>ListByIssueAsync</c> / <c>GetLatestAcceptedAsync</c> — no by-type,
/// by-set or by-repository query). So the ordering is written under a DETERMINISTIC synthetic
/// anchor computed from inputs alone, which is what lets 41-6 and 41-4 recompute the same string
/// and read the accepted ordering back through the existing seam with no new repository method.
/// This generalises <c>TaskCreationWorkflow</c>'s producer-scoped issue id
/// (<c>{issueId}#task-creation</c>, 39-15 D2) from "isolate two producers of one type" to
/// "anchor a document that is not issue-scoped at all". FILED to 39-11: the honest fix is a
/// by-type / by-repository read; until then this helper is the ONE place the string is built.</para>
///
/// <para><b>This story AUTHORS the segment normaliser</b> (story Amendment A3). Both sibling
/// plans said the anchor would be "folded through <see cref="CreationBindingHelper.ScopeIssueId"/>'s
/// normalisation" — there is no such normalisation: <c>ScopeIssueId</c> is pure concatenation
/// (<c>$"{baseIssueId}#{producer}"</c>) and nothing in that file trims, lowercases or escapes
/// anything. <see cref="NormalizeSegment"/> is therefore new, and it is deliberately a
/// SEPARATELY CALLABLE <c>public static</c> member rather than a private helper or an inline
/// lambda: 41-4's <c>RoadmapBindingHelper.BuildAnchor</c> and 41-6's
/// <c>SprintBindingHelper.BuildAnchor</c> both state that they delegate to "the same segment
/// transform", and their helper tests assert agreement with it. A private transform would force
/// them to copy it, and the "provably consistent" claim would become two divergent copies.</para>
/// </summary>
public static class BacklogBindingHelper
{
    // ====================================================================
    // The anchor contract (D2) — shared with 41-4 and 41-6
    // ====================================================================

    /// <summary>The family prefix of a backlog-ordering lineage anchor.</summary>
    public const string AnchorPrefix = "backlog";

    /// <summary>
    /// The anchor's segment delimiter. <see cref="NormalizeSegment"/> guarantees no segment can
    /// contain it, so a 3-segment <c>backlog:</c> anchor can never be FORGED from a 2-segment key
    /// minted elsewhere in the same colon-delimited shape — the namespace is not naturally
    /// disjoint: <c>TriageItemCycleHelper.DeriveItemKey</c> emits <c>{repo}:{source}:{title}</c>
    /// and <c>{repo}:{source}</c>.
    /// </summary>
    public const char AnchorDelimiter = ':';

    /// <summary>The stand-in for a segment that normalises to nothing — keeps the anchor total and 3-segment.</summary>
    public const string EmptySegmentPlaceholder = "_";

    /// <summary>Per-segment length cap, so a hostile input cannot mint an unbounded anchor.</summary>
    public const int MaxSegmentLength = 128;

    /// <summary>
    /// The shared segment transform (story AC6). PUBLIC and separately callable BY DESIGN — see
    /// the class remarks. Deterministic and total:
    /// <list type="bullet">
    ///   <item>null / empty / all-hostile input ⇒ <see cref="EmptySegmentPlaceholder"/>;</item>
    ///   <item>lower-cased with the invariant culture (so <c>MeyWd/Tamma</c> and
    ///         <c>meywd/tamma</c> anchor to the same document);</item>
    ///   <item>every character outside <c>[a-z0-9._-]</c> — which includes
    ///         <see cref="AnchorDelimiter"/>, <c>#</c>, whitespace and every control character —
    ///         is replaced by <c>-</c>, so a segment can never carry a delimiter;</item>
    ///   <item>runs of <c>-</c> collapse to one and leading/trailing <c>-</c> are trimmed;</item>
    ///   <item>the result is truncated to <see cref="MaxSegmentLength"/>.</item>
    /// </list>
    /// </summary>
    public static string NormalizeSegment(string? segment)
    {
        if (string.IsNullOrWhiteSpace(segment))
            return EmptySegmentPlaceholder;

        var sb = new StringBuilder(segment!.Length);
        var lastWasDash = false;
        foreach (var raw in segment)
        {
            var c = char.ToLowerInvariant(raw);
            var keep = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '.' || c == '_' || c == '-';

            if (keep && c != '-')
            {
                sb.Append(c);
                lastWasDash = false;
                continue;
            }

            // '-' itself and every replaced character collapse into a single dash.
            if (lastWasDash)
                continue;
            if (sb.Length > 0)
                sb.Append('-');
            lastWasDash = true;
        }

        var normalized = sb.ToString().Trim('-');
        if (normalized.Length > MaxSegmentLength)
            normalized = normalized[..MaxSegmentLength].TrimEnd('-');

        return normalized.Length == 0 ? EmptySegmentPlaceholder : normalized;
    }

    /// <summary>
    /// D2 / story AC6 — the set-scoped lineage anchor:
    /// <c>backlog:{normalized repository}:{normalized backlogScope}</c>.
    /// Deterministic (same inputs twice ⇒ byte-identical), total (null / empty / hostile input
    /// never throws), and composed from <see cref="NormalizeSegment"/> so no segment can carry
    /// <see cref="AnchorDelimiter"/>. 41-6 and 41-4 CALL this — they must never re-derive it.
    /// </summary>
    public static string BuildAnchor(string? repository, string? backlogScope)
        => string.Concat(
            AnchorPrefix, AnchorDelimiter,
            NormalizeSegment(repository), AnchorDelimiter,
            NormalizeSegment(backlogScope));

    // ====================================================================
    // Item-set parsing (D3)
    // ====================================================================

    /// <summary>
    /// One candidate backlog item, as parsed from the caller-supplied set.
    ///
    /// <para><see cref="ItemId"/> is the identity the MODEL ranks and the accepted
    /// <c>BacklogOrdering.items[].itemId</c> echoes back verbatim — the binding passes the
    /// caller's value through untouched and deliberately does NOT pin what it means (44-3's
    /// open Cross-Story Contract C2). <see cref="ItemIssueId"/> is a DIFFERENT field: it is the
    /// 39-11 STORE READ KEY for the per-item evidence reads, and it must be in
    /// <c>CreationBindingHelper.DeriveIssueId</c> form (<c>{repository}#{issueNumber}</c>) or the
    /// landed triage producers' anchors cannot be hit at all (story AC2).</para>
    /// </summary>
    public sealed record BacklogItemRef(string ItemId, string ItemIssueId, string Title, string Summary);

    /// <summary>D3's default cap on the number of per-item evidence reads a single run performs.</summary>
    public const int MaxEvidenceReads = 50;

    /// <summary>
    /// The composed evidence value's hard budget, kept well BELOW
    /// <c>PromptStoreService.MaxVariableValueLength</c> (100 000,
    /// <c>Tamma.Api/Services/PromptStore/PromptStoreService.cs:96</c>). A longer value is treated
    /// as UNRESOLVED by <c>PromptStoreService.Render</c>, which then ships the literal
    /// <c>{{evidence}}</c> in the prompt — a silently broken produce. The headroom is not
    /// decoration: the lifecycle APPENDS repair/revise notes into this same declared carrier
    /// (<c>DocumentLifecycleHelper.BuildRevisionVariables</c> → <c>AppendToFeedbackVariables</c>),
    /// so the accumulator must leave room for them. Tamma.ElsaServer does not reference Tamma.Api,
    /// so the relationship is pinned by test rather than by a compile-time reference.
    /// </summary>
    public const int MaxEvidenceLength = 60_000;

    /// <summary>
    /// Parse the caller-supplied candidate set. Accepts a JSON array of objects carrying any of
    /// <c>itemId</c> / <c>issueId</c> / <c>issueNumber</c> / <c>title</c> / <c>summary</c>.
    /// <see cref="BacklogItemRef.ItemIssueId"/> is the explicit <c>issueId</c> when present, else
    /// <c>CreationBindingHelper.DeriveIssueId(repository, issueNumber)</c>;
    /// <see cref="BacklogItemRef.ItemId"/> is the explicit <c>itemId</c> when present, else the
    /// derived issue id (so an ordering always references something).
    ///
    /// <para>TOTAL: malformed JSON, a non-array root, or a non-object element yields an EMPTY
    /// list rather than a throw — a backlog with no parsable items still runs the lifecycle and
    /// gets a typed validation failure (<c>NO_ITEMS</c>), never an engine fault. At most
    /// <paramref name="cap"/> items are returned (D3's bounded-read discipline).</para>
    /// </summary>
    public static IReadOnlyList<BacklogItemRef> ParseItems(
        string? itemsJson, string? repository, int cap = MaxEvidenceReads)
    {
        var items = new List<BacklogItemRef>();
        if (cap <= 0 || string.IsNullOrWhiteSpace(itemsJson))
            return items;

        try
        {
            using var doc = JsonDocument.Parse(itemsJson!);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array)
                return items;

            foreach (var element in root.EnumerateArray())
            {
                if (items.Count >= cap)
                    break;
                if (element.ValueKind != JsonValueKind.Object)
                    continue;

                var issueId = ReadString(element, "issueId");
                if (issueId.Length == 0)
                {
                    var number = ReadInt(element, "issueNumber");
                    if (number > 0)
                        issueId = CreationBindingHelper.DeriveIssueId(repository, number);
                }

                var itemId = ReadString(element, "itemId");
                if (itemId.Length == 0)
                    itemId = issueId;
                if (itemId.Length == 0)
                    continue; // an entry that identifies nothing cannot be ranked or read.

                items.Add(new BacklogItemRef(
                    itemId, issueId, ReadString(element, "title"), ReadString(element, "summary")));
            }
        }
        catch (JsonException)
        {
            return [];
        }

        return items;
    }

    /// <summary>
    /// Story AC2 — whether an item id can be used as a 39-11 evidence read key at all. The landed
    /// triage producers anchor on <c>CreationBindingHelper.DeriveIssueId</c>'s
    /// <c>{repository}#{issueNumber}</c> form (<c>TriagePODecisionWorkflow:105-108</c>,
    /// <c>TriageItemCycleHelper.DeriveItemKey:85-86</c>), so an id in ANY other form cannot hit
    /// them. Such an item is recorded as an evidence MISS
    /// (<see cref="SeedEvidence"/>) rather than silently treated as "this item has no evidence".
    /// </summary>
    public static bool IsAnchorableIssueId(string? itemIssueId)
    {
        if (string.IsNullOrWhiteSpace(itemIssueId))
            return false;

        var hash = itemIssueId!.LastIndexOf('#');
        if (hash <= 0 || hash == itemIssueId.Length - 1)
            return false;

        var repo = itemIssueId[..hash];
        var number = itemIssueId[(hash + 1)..];
        return repo.Trim().Length > 0
            && int.TryParse(number, out var parsed)
            && parsed > 0;
    }

    /// <summary>
    /// The ordered, de-duplicated list of item issue ids the evidence <c>ForEach</c> iterates —
    /// the anchorable ones only. Items whose id is not in
    /// <c>CreationBindingHelper.DeriveIssueId</c> form are excluded here and recorded by
    /// <see cref="SeedEvidence"/>, so the loop never performs a read that structurally cannot hit
    /// a landed producer.
    /// </summary>
    public static IReadOnlyList<string> SelectEvidenceAnchors(IEnumerable<BacklogItemRef>? items)
    {
        var anchors = new List<string>();
        if (items is null)
            return anchors;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            if (!IsAnchorableIssueId(item?.ItemIssueId))
                continue;
            if (seen.Add(item!.ItemIssueId))
                anchors.Add(item.ItemIssueId);
        }
        return anchors;
    }

    /// <summary>The heading every evidence block carries, so the accumulator's shape is testable.</summary>
    public const string EvidenceBlockHeading = "### evidence";

    /// <summary>The heading a recorded evidence MISS carries.</summary>
    public const string EvidenceMissHeading = "### evidence-miss";

    /// <summary>
    /// Story AC2 — seed the accumulator with the items whose id cannot be used as a store read
    /// key, so "we could not look" is DISTINGUISHABLE from "we looked and found nothing". Returns
    /// <c>""</c> when every item is anchorable (the common case), so a well-formed run's evidence
    /// carrier is byte-identical to one built without this step.
    /// </summary>
    public static string SeedEvidence(IEnumerable<BacklogItemRef>? items)
    {
        if (items is null)
            return "";

        var sb = new StringBuilder();
        foreach (var item in items)
        {
            if (item is null || IsAnchorableIssueId(item.ItemIssueId))
                continue;

            var id = string.IsNullOrWhiteSpace(item.ItemIssueId) ? item.ItemId : item.ItemIssueId;
            if (sb.Length > 0)
                sb.Append("\n\n");
            sb.Append(EvidenceMissHeading).Append(" for item '").Append(id).Append("'\n");
            sb.Append("No upstream triage evidence could be read: the item id is not in ");
            sb.Append("'{repository}#{issueNumber}' form, which is the anchor the landed triage ");
            sb.Append("producers write under. Rank this item from its title and summary alone.");

            if (sb.Length > MaxEvidenceLength)
                return Truncate(sb.ToString());
        }
        return sb.ToString();
    }

    /// <summary>
    /// D3 — the bounded evidence accumulator. Appends ONE labelled block naming the exact ANCHOR
    /// the document was read at, so the two <c>findings</c> producers
    /// (<c>ResearchWorkflow</c> at the bare item id, <c>TriageContextGatheringWorkflow</c> at
    /// <c>{itemIssueId}#triage-context</c>) are distinguishable in the composed carrier instead
    /// of one being silently presented as "the findings" (story Amendment A1).
    ///
    /// <para>Absent / unreadable input is a NO-OP that returns the accumulator unchanged
    /// (fail-closed: absence of evidence is never fatal), and the result never exceeds
    /// <see cref="MaxEvidenceLength"/>.</para>
    /// </summary>
    public static string AppendEvidence(
        string? evidenceSoFar, string? anchor, string? documentType, string? documentJson)
    {
        var soFar = evidenceSoFar ?? "";
        var body = Normalize(documentJson);
        if (body.Length == 0 || string.IsNullOrWhiteSpace(anchor) || string.IsNullOrWhiteSpace(documentType))
            return Truncate(soFar);

        var block = new StringBuilder();
        block.Append(EvidenceBlockHeading).Append(": ").Append(documentType!.Trim())
             .Append(" @ ").Append(anchor!.Trim()).Append('\n').Append(body);

        var combined = soFar.Length == 0 ? block.ToString() : soFar + "\n\n" + block;
        if (combined.Length <= MaxEvidenceLength)
            return combined;

        // Budget exhausted: keep what is already composed rather than shipping a value the
        // renderer will drop wholesale as unresolved.
        return Truncate(soFar);
    }

    // ====================================================================
    // Producer input + accepted-ordering projection
    // ====================================================================

    /// <summary>
    /// The normalised candidate set handed to the producer as the DECLARED <c>itemsJson</c>
    /// variable: one object per item carrying the <c>itemId</c> the ordering must echo back,
    /// plus the title/summary that let it be ranked when no upstream evidence exists.
    /// Always well-formed JSON (<c>"[]"</c> when there is nothing to rank).
    /// </summary>
    public static string BuildItemsForProducer(IEnumerable<BacklogItemRef>? items)
    {
        if (items is null)
            return "[]";

        var projected = items
            .Where(i => i is not null)
            .Select(i => new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["itemId"] = i!.ItemId,
                ["issueId"] = i.ItemIssueId,
                ["title"] = i.Title,
                ["summary"] = i.Summary,
            })
            .ToList();

        return projected.Count == 0 ? "[]" : JsonSerializer.Serialize(projected);
    }

    /// <summary>
    /// Project the bare <c>items</c> JSON array raw text from an accepted <c>backlog-ordering</c>
    /// body — the ordering 41-6's sprint planning reads. A body that is already an array is
    /// returned verbatim. Fail-closed <c>"[]"</c> on empty / unreadable / shapeless input.
    /// </summary>
    public static string ProjectOrdering(string? documentJson)
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
                root.TryGetProperty("items", out var items) &&
                items.ValueKind == JsonValueKind.Array)
                return items.GetArrayLength() == 0 ? "[]" : items.GetRawText();

            return "[]";
        }
        catch (JsonException)
        {
            return "[]";
        }
    }

    /// <summary>The number of ranked entries in an accepted ordering body. Fail-closed <c>0</c>.</summary>
    public static int CountOrderedItems(string? documentJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(ProjectOrdering(documentJson));
            return doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.GetArrayLength() : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }

    /// <summary>
    /// The failure detail for a non-accepted grooming exit — delegates to
    /// <see cref="CreationBindingHelper.BuildFailureDetail"/> so every binding's
    /// <c>*.FAILED</c> detail names the typed outcome wire in the same words.
    /// </summary>
    public static string BuildFailureDetail(LifecycleBindingHelper.LifecycleExit exit)
        => CreationBindingHelper.BuildFailureDetail(exit);

    // ====================================================================
    // Internals
    // ====================================================================

    private static string Truncate(string value)
        => value.Length <= MaxEvidenceLength ? value : value[..MaxEvidenceLength];

    private static string Normalize(string? json)
    {
        var trimmed = json?.Trim() ?? "";
        // The 39-14 read seam reports "not found" as an empty carrier ("{}"); treat it as absent
        // rather than pasting a meaningless brace pair into the producer's context.
        return trimmed is "" or "{}" or "[]" or "null" ? "" : trimmed;
    }

    private static string ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? ""
            : "";

    private static int ReadInt(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
            return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            return number;
        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var parsed))
            return parsed;
        return 0;
    }
}
