using System.Text.Json;
using System.Text.Json.Serialization;
using Tamma.Api.Services.Agents;

namespace Tamma.Core.Documents.Types;

/// <summary>
/// One committed sprint item (Story 41-1b): an issue committed into the time-box
/// with an owning role (an <see cref="AgentRole"/> wire string) and a numeric
/// <see cref="Estimate"/> in the same unit as the sprint's capacity.
/// </summary>
public sealed record SprintCommittedItem
{
    [JsonPropertyName("issueId")] public string IssueId { get; init; } = "";
    [JsonPropertyName("ownerRole")] public string OwnerRole { get; init; } = "";
    [JsonPropertyName("estimate")] public decimal? Estimate { get; init; }
}

/// <summary>
/// One carry-over item (Story 41-1b): work entering the sprint unfinished from a
/// previous one — flagged explicitly, with the reason stated.
/// </summary>
public sealed record SprintCarryOverItem
{
    [JsonPropertyName("issueId")] public string IssueId { get; init; } = "";
    [JsonPropertyName("reason")] public string Reason { get; init; } = "";
}

/// <summary>
/// A sprint plan (Story 41-1b; epic README's new-types table): a <c>Plan</c> maps
/// tasks-to-files for ONE issue — a sprint commits a CAPACITY-BOUNDED set of
/// issues to a time-box, every committed item owned and estimated, carry-over
/// flagged.
/// </summary>
public sealed record SprintPlan
{
    [JsonPropertyName("sprintId")] public string SprintId { get; init; } = "";
    [JsonPropertyName("capacity")] public decimal? Capacity { get; init; }
    [JsonPropertyName("committed")] public IReadOnlyList<SprintCommittedItem> Committed { get; init; } = [];
    [JsonPropertyName("carryOver")] public IReadOnlyList<SprintCarryOverItem> CarryOver { get; init; } = [];
}

/// <summary>
/// <see cref="IDocumentType"/> for the <c>sprint-plan</c> document (Story 41-1b
/// AC2, Design Decision D7): the committed estimates must not exceed the stated
/// capacity (arithmetic, not schema — the message names both numbers), every
/// committed item has an owner role (validated against the <see cref="AgentRole"/>
/// taxonomy) and a positive estimate, and every carry-over item is flagged with a
/// reason.
/// </summary>
public sealed class SprintPlanDocumentType : IDocumentType
{
    /// <summary>Payload could not be deserialized into the typed shape.</summary>
    public const string MalformedPayload = "MALFORMED_PAYLOAD";

    /// <summary>The plan names no sprintId — a commitment must name its time-box.</summary>
    public const string SprintIdMissing = "SPRINT_ID_MISSING";

    /// <summary>The capacity is missing or not a positive number.</summary>
    public const string CapacityInvalid = "CAPACITY_INVALID";

    /// <summary>No committed items — an empty sprint commits nothing.</summary>
    public const string NoCommittedItems = "NO_COMMITTED_ITEMS";

    /// <summary>A committed item names no issue.</summary>
    public const string CommittedItemMissingIssueId = "COMMITTED_ITEM_MISSING_ISSUE_ID";

    /// <summary>A committed item names no owner role.</summary>
    public const string CommittedItemMissingOwnerRole = "COMMITTED_ITEM_MISSING_OWNER_ROLE";

    /// <summary>A committed item's owner role is not a known <see cref="AgentRole"/>.</summary>
    public const string OwnerRoleUnknown = "OWNER_ROLE_UNKNOWN";

    /// <summary>A committed item has no positive estimate.</summary>
    public const string CommittedItemMissingEstimate = "COMMITTED_ITEM_MISSING_ESTIMATE";

    /// <summary>The committed estimates sum past the stated capacity (message names sum and capacity).</summary>
    public const string CommitmentExceedsCapacity = "COMMITMENT_EXCEEDS_CAPACITY";

    /// <summary>A carry-over entry is not properly flagged (missing issueId and/or reason).</summary>
    public const string CarryoverNotFlagged = "CARRYOVER_NOT_FLAGGED";

    public string Key => DocumentTypeKey.SprintPlan.ToWire();
    public int SchemaVersion => 1;
    public Type PayloadClrType => typeof(SprintPlan);

    public DocumentValidationResult Validate(JsonElement payload) =>
        DocumentPayloadGuard.Run(payload, ValidateCore);

    private DocumentValidationResult ValidateCore(JsonElement payload)
    {
        SprintPlan? doc;
        try
        {
            doc = payload.Deserialize<SprintPlan>(DocumentJson.Options);
        }
        catch (JsonException)
        {
            return DocumentValidationResult.Invalid(new DocumentViolation(
                MalformedPayload, "The payload could not be parsed as a sprint-plan document."));
        }

        if (doc is null)
            return DocumentValidationResult.Invalid(new DocumentViolation(
                MalformedPayload, "The payload deserialized to null."));

        var violations = new List<DocumentViolation>();

        if (string.IsNullOrWhiteSpace(doc.SprintId))
            violations.Add(new DocumentViolation(
                SprintIdMissing, "The plan names no sprintId — a commitment must name its time-box."));

        var capacityValid = doc.Capacity is > 0;
        if (!capacityValid)
            violations.Add(new DocumentViolation(
                CapacityInvalid,
                $"The stated capacity '{doc.Capacity?.ToString() ?? "(none)"}' is not a positive number — a " +
                "capacity-bounded commitment needs a real capacity."));

        var committed = doc.Committed ?? [];
        if (committed.Count == 0)
            violations.Add(new DocumentViolation(
                NoCommittedItems, "The plan commits no items — an empty sprint commits nothing."));

        decimal committedSum = 0;
        var allEstimatesPresent = true;
        var index = 0;
        foreach (var item in committed)
        {
            index++;
            var label = string.IsNullOrWhiteSpace(item.IssueId) ? $"#{index}" : $"'{item.IssueId}'";

            if (string.IsNullOrWhiteSpace(item.IssueId))
                violations.Add(new DocumentViolation(
                    CommittedItemMissingIssueId, $"Committed item {label} names no issueId."));

            var ownerRole = item.OwnerRole?.Trim() ?? "";
            if (ownerRole.Length == 0)
            {
                violations.Add(new DocumentViolation(
                    CommittedItemMissingOwnerRole,
                    $"Committed item {label} names no owner role — every committed item must be owned."));
            }
            else
            {
                try
                {
                    _ = AgentRoleExtensions.Parse(ownerRole);
                }
                catch (ArgumentException)
                {
                    violations.Add(new DocumentViolation(
                        OwnerRoleUnknown,
                        $"Committed item {label} names owner role '{ownerRole}', which is not a known agent role."));
                }
            }

            if (item.Estimate is not > 0)
            {
                allEstimatesPresent = false;
                violations.Add(new DocumentViolation(
                    CommittedItemMissingEstimate,
                    $"Committed item {label} has no positive estimate — a capacity check needs every item sized."));
            }
            else
            {
                committedSum += item.Estimate.Value;
            }
        }

        // The capacity arithmetic (D7) fires only when it is decidable: a valid
        // capacity and a fully-estimated committed set — the missing pieces are
        // already their own violations above.
        if (capacityValid && allEstimatesPresent && committed.Count > 0 && committedSum > doc.Capacity!.Value)
            violations.Add(new DocumentViolation(
                CommitmentExceedsCapacity,
                $"The committed estimates sum to {committedSum}, exceeding the stated capacity {doc.Capacity.Value} — " +
                "a sprint commitment must fit its capacity."));

        index = 0;
        foreach (var carry in doc.CarryOver ?? [])
        {
            index++;
            var label = string.IsNullOrWhiteSpace(carry.IssueId) ? $"#{index}" : $"'{carry.IssueId}'";
            if (string.IsNullOrWhiteSpace(carry.IssueId) || string.IsNullOrWhiteSpace(carry.Reason))
                violations.Add(new DocumentViolation(
                    CarryoverNotFlagged,
                    $"Carry-over entry {label} is not properly flagged — every carry-over must name its issueId " +
                    "and the reason it did not finish."));
        }

        return violations.Count == 0
            ? DocumentValidationResult.Valid()
            : DocumentValidationResult.Invalid(violations.ToArray());
    }

    public string RenderContract() => Contract;

    public IReadOnlyList<DocumentExample> Examples => s_examples;

    // ── Contract + examples ──────────────────────────────────────────────────
    // Producing cell (41-1b D4): (scrum_master, plan-sprint) — the role and its
    // prompt template are 41-1a scope (another lane); until they land, this cell
    // exists only as the documented intent here. The cell is NOT bound in
    // ContractBindingTests (no compiled dispatch site exists until 41-6 lands its
    // workflow — the stale-Bindings guard forbids an early entry); the intended
    // tokens below are pinned Core-side by RenderContractTokenTests.
    private const string Contract =
        """
        Return ONLY a JSON object of this shape:
        {
          "sprintId": "sprint-2026-08-A",
          "capacity": 20,
          "committed": [
            { "issueId": "issue-7", "ownerRole": "developer", "estimate": 5 }
          ],
          "carryOver": [
            { "issueId": "issue-3", "reason": "why this item did not finish last sprint" }
          ]
        }
        Rules: name the "sprintId" and a positive "capacity"; commit at least one item;
        every committed item names an "issueId", an "ownerRole" from the agent-role
        taxonomy, and a positive "estimate"; the committed estimates must sum to no more
        than the capacity; every carry-over entry states its "reason".
        """;

    private static readonly IReadOnlyList<DocumentExample> s_examples = new[]
    {
        new DocumentExample(
            "valid-capacity-bounded-sprint",
            true,
            """
            {
              "sprintId": "sprint-2026-08-A",
              "capacity": 10,
              "committed": [
                { "issueId": "issue-7", "ownerRole": "developer", "estimate": 5 },
                { "issueId": "issue-9", "ownerRole": "tester", "estimate": 3 }
              ],
              "carryOver": [
                { "issueId": "issue-3", "reason": "Blocked on the provider API key rotation." }
              ]
            }
            """),
        new DocumentExample(
            "invalid-overcommitted-sprint",
            false,
            """
            {
              "sprintId": "sprint-2026-08-A",
              "capacity": 5,
              "committed": [
                { "issueId": "issue-7", "ownerRole": "developer", "estimate": 4 },
                { "issueId": "issue-9", "ownerRole": "tester", "estimate": 3 }
              ],
              "carryOver": []
            }
            """,
            new[] { CommitmentExceedsCapacity }),
    };
}
