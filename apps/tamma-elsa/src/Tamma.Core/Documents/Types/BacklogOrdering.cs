using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tamma.Core.Documents.Types;

/// <summary>
/// One ranked backlog item (Story 41-1b). <see cref="Rank"/> is 1-based and
/// unique across the document (a total order); <see cref="Rationale"/> justifies
/// the placement; <see cref="Value"/> and <see cref="Effort"/> are free non-empty
/// estimate strings (Design Decision D6 — estimate units differ per team, so the
/// vocabulary is deliberately NOT closed).
/// </summary>
public sealed record BacklogItem
{
    [JsonPropertyName("itemId")] public string ItemId { get; init; } = "";
    [JsonPropertyName("rank")] public int? Rank { get; init; }
    [JsonPropertyName("rationale")] public string Rationale { get; init; } = "";
    [JsonPropertyName("value")] public string Value { get; init; } = "";
    [JsonPropertyName("effort")] public string Effort { get; init; } = "";
}

/// <summary>
/// A ranked backlog ordering (Story 41-1b; epic README's new-types table): a
/// <c>TriageDecision</c> classifies ONE item — this ranks a SET, with a rationale
/// and value/effort estimate per item and no ties.
/// </summary>
public sealed record BacklogOrdering
{
    [JsonPropertyName("items")] public IReadOnlyList<BacklogItem> Items { get; init; } = [];
}

/// <summary>
/// <see cref="IDocumentType"/> for the <c>backlog-ordering</c> document
/// (Story 41-1b AC2, Design Decision D7): the ranks form a total order (1..N, no
/// ties, no gaps — arithmetic, not schema), and every item carries a rationale
/// plus value/effort estimates.
/// </summary>
public sealed class BacklogOrderingDocumentType : IDocumentType
{
    /// <summary>Payload could not be deserialized into the typed shape.</summary>
    public const string MalformedPayload = "MALFORMED_PAYLOAD";

    /// <summary>No items — an empty ordering orders nothing.</summary>
    public const string NoItems = "NO_ITEMS";

    /// <summary>An item names no itemId — the ordering must reference real backlog items.</summary>
    public const string ItemIdMissing = "ITEM_ID_MISSING";

    /// <summary>
    /// Two entries reference the same itemId — a total order over the referenced
    /// item set assigns each item exactly one position (adversarial review
    /// 2026-07-29; the <c>CRITERION_ID_DUPLICATED</c> naming pattern).
    /// </summary>
    public const string ItemIdDuplicated = "ITEM_ID_DUPLICATED";

    /// <summary>Two items share the same rank (a tie) — the message names both item ids and the rank.</summary>
    public const string RankDuplicated = "RANK_DUPLICATED";

    /// <summary>The ranks are not a 1-based gap-free total order over the item set.</summary>
    public const string RankNotTotalOrder = "RANK_NOT_TOTAL_ORDER";

    /// <summary>An item has no rationale — every placement must be justified.</summary>
    public const string ItemMissingRationale = "ITEM_MISSING_RATIONALE";

    /// <summary>An item is missing its value and/or effort estimate.</summary>
    public const string ItemMissingEstimate = "ITEM_MISSING_ESTIMATE";

    public string Key => DocumentTypeKey.BacklogOrdering.ToWire();
    public int SchemaVersion => 1;
    public Type PayloadClrType => typeof(BacklogOrdering);

    public DocumentValidationResult Validate(JsonElement payload) =>
        DocumentPayloadGuard.Run(payload, ValidateCore);

    private DocumentValidationResult ValidateCore(JsonElement payload)
    {
        BacklogOrdering? doc;
        try
        {
            doc = payload.Deserialize<BacklogOrdering>(DocumentJson.Options);
        }
        catch (JsonException)
        {
            return DocumentValidationResult.Invalid(new DocumentViolation(
                MalformedPayload, "The payload could not be parsed as a backlog-ordering document."));
        }

        if (doc is null)
            return DocumentValidationResult.Invalid(new DocumentViolation(
                MalformedPayload, "The payload deserialized to null."));

        var violations = new List<DocumentViolation>();

        var items = doc.Items ?? [];
        if (items.Count == 0)
            violations.Add(new DocumentViolation(
                NoItems, "The ordering has no items — an empty ordering orders nothing."));

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var reportedDupes = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var item in items)
        {
            index++;
            var id = item.ItemId?.Trim() ?? "";
            var label = id.Length == 0 ? $"#{index}" : $"'{id}'";

            if (id.Length == 0)
                violations.Add(new DocumentViolation(
                    ItemIdMissing, $"Item {label} names no itemId — every entry must reference a real backlog item."));
            else if (!seenIds.Add(id) && reportedDupes.Add(id))
                violations.Add(new DocumentViolation(
                    ItemIdDuplicated,
                    $"Item id '{id}' appears more than once — the ordering is a total order over the referenced " +
                    "item set, so each item holds exactly one position."));

            if (string.IsNullOrWhiteSpace(item.Rationale))
                violations.Add(new DocumentViolation(
                    ItemMissingRationale, $"Item {label} has no rationale — every placement must be justified."));

            if (string.IsNullOrWhiteSpace(item.Value) || string.IsNullOrWhiteSpace(item.Effort))
                violations.Add(new DocumentViolation(
                    ItemMissingEstimate,
                    $"Item {label} is missing its value and/or effort estimate — both are required per item."));
        }

        AddRankViolations(items, violations);

        return violations.Count == 0
            ? DocumentValidationResult.Valid()
            : DocumentValidationResult.Invalid(violations.ToArray());
    }

    /// <summary>
    /// D7 — the arithmetic ranking rules. Ties report <see cref="RankDuplicated"/>
    /// (naming both item ids and the rank) and suppress the total-order check —
    /// a tie already breaks the order, and one minimal fixture per rule must be
    /// able to isolate a single code (the registry's exact-codes discipline).
    /// </summary>
    private static void AddRankViolations(IReadOnlyList<BacklogItem> items, List<DocumentViolation> violations)
    {
        if (items.Count == 0)
            return;

        var byRank = new Dictionary<int, string>();
        var tied = false;
        var missing = false;
        var index = 0;
        foreach (var item in items)
        {
            index++;
            var label = string.IsNullOrWhiteSpace(item.ItemId) ? $"#{index}" : $"'{item.ItemId}'";

            if (item.Rank is not { } rank)
            {
                missing = true;
                violations.Add(new DocumentViolation(
                    RankNotTotalOrder, $"Item {label} has no rank — every item must occupy exactly one position."));
                continue;
            }

            if (byRank.TryGetValue(rank, out var other))
            {
                tied = true;
                violations.Add(new DocumentViolation(
                    RankDuplicated,
                    $"Items {other} and {label} both hold rank {rank} — no ties: the ordering must be total."));
            }
            else
            {
                byRank[rank] = label;
            }
        }

        if (tied || missing)
            return; // a tie / missing rank already breaks the order — do not double-report.

        var expected = Enumerable.Range(1, items.Count).ToHashSet();
        if (!expected.SetEquals(byRank.Keys))
            violations.Add(new DocumentViolation(
                RankNotTotalOrder,
                $"The ranks [{string.Join(", ", byRank.Keys.OrderBy(r => r))}] are not the gap-free 1..{items.Count} " +
                "sequence — the ordering must be a 1-based total order."));
    }

    public string RenderContract() => Contract;

    public IReadOnlyList<DocumentExample> Examples => s_examples;

    // ── Contract + examples ──────────────────────────────────────────────────
    // Producing cell (41-1b D4): (product_owner, prioritize-backlog).
    // The cell is NOT bound in ContractBindingTests yet (no compiled dispatch site
    // exists until 41-3 lands its workflow — the stale-Bindings guard forbids an
    // early entry); the intended tokens below are pinned Core-side by
    // RenderContractTokenTests so 41-3 binds against a stable contract.
    private const string Contract =
        """
        Return ONLY a JSON object of this shape:
        {
          "items": [
            {
              "itemId": "issue-7",
              "rank": 1,
              "rationale": "why this item sits at this rank",
              "value": "the value estimate (your team's units)",
              "effort": "the effort estimate (your team's units)"
            }
          ]
        }
        Rules: rank the WHOLE referenced set — ranks must be the unique, gap-free 1..N
        sequence (no ties); every item states a "rationale" and both a "value" and an
        "effort" estimate.
        """;

    private static readonly IReadOnlyList<DocumentExample> s_examples = new[]
    {
        new DocumentExample(
            "valid-three-item-ordering",
            true,
            """
            {
              "items": [
                { "itemId": "issue-7", "rank": 1, "rationale": "Unblocks two teams; small effort.", "value": "high", "effort": "1d" },
                { "itemId": "issue-9", "rank": 2, "rationale": "Customer-visible defect.", "value": "medium", "effort": "2d" },
                { "itemId": "issue-3", "rank": 3, "rationale": "Nice-to-have polish.", "value": "low", "effort": "3d" }
              ]
            }
            """),
        new DocumentExample(
            "invalid-tied-ranks",
            false,
            """
            {
              "items": [
                { "itemId": "issue-7", "rank": 1, "rationale": "First.", "value": "high", "effort": "1d" },
                { "itemId": "issue-9", "rank": 1, "rationale": "Also first.", "value": "medium", "effort": "2d" }
              ]
            }
            """,
            new[] { RankDuplicated }),
    };
}
