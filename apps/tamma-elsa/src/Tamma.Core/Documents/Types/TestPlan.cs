using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tamma.Core.Documents.Types;

/// <summary>
/// One ranked risk area in a <see cref="TestPlan"/> (Story 41-1b). Risk-area
/// names are free non-empty strings (Design Decision D6 — the vocabulary is
/// deliberately NOT closed); <see cref="Rank"/> is 1-based and unique across the
/// plan.
/// </summary>
public sealed record TestPlanRiskArea
{
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("rank")] public int? Rank { get; init; }
    [JsonPropertyName("rationale")] public string Rationale { get; init; } = "";
}

/// <summary>
/// One strategy line in a <see cref="TestPlan"/> (Story 41-1b): what will be
/// tested and how, mapped to a declared risk area (<see cref="RiskAreaRef"/> by
/// name) with an explicit <see cref="CoverageTarget"/>.
/// </summary>
public sealed record TestPlanStrategyLine
{
    [JsonPropertyName("description")] public string Description { get; init; } = "";
    [JsonPropertyName("coverageTarget")] public string CoverageTarget { get; init; } = "";
    [JsonPropertyName("riskAreaRef")] public string RiskAreaRef { get; init; } = "";
}

/// <summary>
/// A test plan / strategy (Story 41-1b; epic README's new-types table): a
/// <c>TestSpec</c> is executable cases bound to task IDs — this is the STRATEGY
/// above them (scope, risk-based coverage, environments, entry/exit criteria).
/// </summary>
public sealed record TestPlan
{
    [JsonPropertyName("scope")] public string Scope { get; init; } = "";
    [JsonPropertyName("riskAreas")] public IReadOnlyList<TestPlanRiskArea> RiskAreas { get; init; } = [];
    [JsonPropertyName("strategyLines")] public IReadOnlyList<TestPlanStrategyLine> StrategyLines { get; init; } = [];

    /// <summary>Environments the strategy runs against — carry-through, not validated (no rule names them).</summary>
    [JsonPropertyName("environments")] public IReadOnlyList<string> Environments { get; init; } = [];

    [JsonPropertyName("entryCriteria")] public IReadOnlyList<string> EntryCriteria { get; init; } = [];
    [JsonPropertyName("exitCriteria")] public IReadOnlyList<string> ExitCriteria { get; init; } = [];
}

/// <summary>
/// <see cref="IDocumentType"/> for the <c>test-plan</c> document (Story 41-1b
/// AC2): risk areas ranked in a total order, every strategy line mapped to a
/// declared risk area with a coverage target, and entry/exit criteria stated.
/// </summary>
public sealed class TestPlanDocumentType : IDocumentType
{
    /// <summary>Payload could not be deserialized into the typed shape.</summary>
    public const string MalformedPayload = "MALFORMED_PAYLOAD";

    /// <summary>The plan states no scope — a strategy must say what it covers.</summary>
    public const string ScopeMissing = "SCOPE_MISSING";

    /// <summary>No risk areas — a risk-based strategy needs ranked risks.</summary>
    public const string NoRiskAreas = "NO_RISK_AREAS";

    /// <summary>A risk area has no name — strategy lines map by name.</summary>
    public const string RiskAreaNameMissing = "RISK_AREA_NAME_MISSING";

    /// <summary>
    /// Two risk areas share a name — strategy lines map by name, so a duplicate
    /// makes every <c>riskAreaRef</c> to it ambiguous (adversarial review
    /// 2026-07-29; the <c>CRITERION_ID_DUPLICATED</c> naming pattern).
    /// </summary>
    public const string RiskAreaNameDuplicated = "RISK_AREA_NAME_DUPLICATED";

    /// <summary>The risk-area ranks are not a unique, gap-free 1-based total order.</summary>
    public const string RiskRankNotTotalOrder = "RISK_RANK_NOT_TOTAL_ORDER";

    /// <summary>No strategy lines — ranked risks with no strategy cover nothing.</summary>
    public const string NoStrategyLines = "NO_STRATEGY_LINES";

    /// <summary>A strategy line references no declared risk area.</summary>
    public const string StrategyLineUnmappedRiskArea = "STRATEGY_LINE_UNMAPPED_RISK_AREA";

    /// <summary>A strategy line names no coverage target.</summary>
    public const string StrategyLineMissingCoverageTarget = "STRATEGY_LINE_MISSING_COVERAGE_TARGET";

    /// <summary>Entry criteria missing — the plan must state when testing may start.</summary>
    public const string EntryCriteriaMissing = "ENTRY_CRITERIA_MISSING";

    /// <summary>Exit criteria missing — the plan must state when testing is done.</summary>
    public const string ExitCriteriaMissing = "EXIT_CRITERIA_MISSING";

    public string Key => DocumentTypeKey.TestPlan.ToWire();
    public int SchemaVersion => 1;
    public Type PayloadClrType => typeof(TestPlan);

    public DocumentValidationResult Validate(JsonElement payload)
    {
        TestPlan? doc;
        try
        {
            doc = payload.Deserialize<TestPlan>(DocumentJson.Options);
        }
        catch (JsonException)
        {
            return DocumentValidationResult.Invalid(new DocumentViolation(
                MalformedPayload, "The payload could not be parsed as a test-plan document."));
        }

        if (doc is null)
            return DocumentValidationResult.Invalid(new DocumentViolation(
                MalformedPayload, "The payload deserialized to null."));

        var violations = new List<DocumentViolation>();

        if (string.IsNullOrWhiteSpace(doc.Scope))
            violations.Add(new DocumentViolation(
                ScopeMissing, "The plan states no scope — a strategy must say what it covers (and what it does not)."));

        var riskAreas = doc.RiskAreas ?? [];
        if (riskAreas.Count == 0)
            violations.Add(new DocumentViolation(
                NoRiskAreas, "The plan declares no risk areas — a risk-based strategy needs ranked risks."));

        var declaredNames = new HashSet<string>(StringComparer.Ordinal);
        var reportedDupeNames = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var area in riskAreas)
        {
            index++;
            var name = area.Name?.Trim() ?? "";
            if (name.Length == 0)
                violations.Add(new DocumentViolation(
                    RiskAreaNameMissing, $"Risk area #{index} has no name — strategy lines map to risk areas by name."));
            else if (!declaredNames.Add(name) && reportedDupeNames.Add(name))
                violations.Add(new DocumentViolation(
                    RiskAreaNameDuplicated,
                    $"Risk area name '{name}' is declared more than once — strategy lines map by name, so a " +
                    "duplicate makes every riskAreaRef to it ambiguous."));
        }

        AddRankViolations(riskAreas, violations);

        var lines = doc.StrategyLines ?? [];
        if (lines.Count == 0)
            violations.Add(new DocumentViolation(
                NoStrategyLines, "The plan has no strategy lines — ranked risks with no strategy cover nothing."));

        index = 0;
        foreach (var line in lines)
        {
            index++;
            var label = string.IsNullOrWhiteSpace(line.Description) ? $"#{index}" : $"'{line.Description}'";

            var riskRef = line.RiskAreaRef?.Trim() ?? "";
            if (riskRef.Length == 0 || !declaredNames.Contains(riskRef))
                violations.Add(new DocumentViolation(
                    StrategyLineUnmappedRiskArea,
                    $"Strategy line {label} references risk area '{riskRef}', which is not declared in riskAreas — " +
                    "every line must map to a declared risk."));

            if (string.IsNullOrWhiteSpace(line.CoverageTarget))
                violations.Add(new DocumentViolation(
                    StrategyLineMissingCoverageTarget,
                    $"Strategy line {label} names no coverage target — every line must state the coverage it achieves."));
        }

        if ((doc.EntryCriteria ?? []).All(string.IsNullOrWhiteSpace))
            violations.Add(new DocumentViolation(
                EntryCriteriaMissing, "The plan states no entry criteria — it must say when testing may start."));

        if ((doc.ExitCriteria ?? []).All(string.IsNullOrWhiteSpace))
            violations.Add(new DocumentViolation(
                ExitCriteriaMissing, "The plan states no exit criteria — it must say when testing is done."));

        return violations.Count == 0
            ? DocumentValidationResult.Valid()
            : DocumentValidationResult.Invalid(violations.ToArray());
    }

    /// <summary>
    /// The risk ranking must be a unique, gap-free, 1-based total order. All rank
    /// defects (missing, tied, gapped) report the single
    /// <see cref="RiskRankNotTotalOrder"/> code — one rule, one code (D7).
    /// </summary>
    private static void AddRankViolations(IReadOnlyList<TestPlanRiskArea> riskAreas, List<DocumentViolation> violations)
    {
        if (riskAreas.Count == 0)
            return;

        var ranks = new List<int>();
        var broken = false;
        var index = 0;
        foreach (var area in riskAreas)
        {
            index++;
            var label = string.IsNullOrWhiteSpace(area.Name) ? $"#{index}" : $"'{area.Name}'";
            if (area.Rank is not { } rank)
            {
                broken = true;
                violations.Add(new DocumentViolation(
                    RiskRankNotTotalOrder, $"Risk area {label} has no rank — every risk area must be ranked."));
            }
            else
            {
                ranks.Add(rank);
            }
        }

        if (broken)
            return;

        var distinct = ranks.ToHashSet();
        var expected = Enumerable.Range(1, riskAreas.Count).ToHashSet();
        if (distinct.Count != ranks.Count || !expected.SetEquals(distinct))
            violations.Add(new DocumentViolation(
                RiskRankNotTotalOrder,
                $"The risk ranks [{string.Join(", ", ranks.OrderBy(r => r))}] are not the unique, gap-free " +
                $"1..{riskAreas.Count} sequence — risks must be ranked in a total order."));
    }

    public string RenderContract() => Contract;

    public IReadOnlyList<DocumentExample> Examples => s_examples;

    // ── Contract + examples ──────────────────────────────────────────────────
    // Producing cell (41-1b D4): (tester, plan-test-strategy).
    // The cell is NOT bound in ContractBindingTests yet (no compiled dispatch site
    // exists until 41-13 lands its workflow — the stale-Bindings guard forbids an
    // early entry); the intended tokens below are pinned Core-side by
    // RenderContractTokenTests so 41-13 binds against a stable contract. The
    // shipped plan-test-strategy.md still instructs the legacy plan wire — it is
    // baselined in TemplateExampleConformanceTests.KnownNonConformingTemplates
    // (owned by 41-13, which rewrites it when it binds).
    private const string Contract =
        """
        Return ONLY a JSON object of this shape:
        {
          "scope": "what this strategy covers and what it excludes",
          "riskAreas": [
            { "name": "concurrency", "rank": 1, "rationale": "why this risk ranks here" }
          ],
          "strategyLines": [
            {
              "description": "what will be tested and how",
              "coverageTarget": "the coverage this line achieves",
              "riskAreaRef": "concurrency"
            }
          ],
          "environments": ["local", "ci"],
          "entryCriteria": ["what must hold before testing starts"],
          "exitCriteria": ["what must hold for testing to be done"]
        }
        Rules: state the "scope"; rank every risk area in a unique, gap-free 1..N order;
        every strategy line must reference a declared risk area by name and state a
        "coverageTarget"; state at least one entry criterion and one exit criterion.
        """;

    private static readonly IReadOnlyList<DocumentExample> s_examples = new[]
    {
        new DocumentExample(
            "valid-two-risk-strategy",
            true,
            """
            {
              "scope": "The tenant rate-limiter: middleware, counters, headers. UI is out of scope.",
              "riskAreas": [
                { "name": "concurrency", "rank": 1, "rationale": "Counters are shared across requests." },
                { "name": "config", "rank": 2, "rationale": "Per-tenant limits are operator-supplied." }
              ],
              "strategyLines": [
                { "description": "Parallel-request integration tests over one tenant", "coverageTarget": "all limiter branches", "riskAreaRef": "concurrency" },
                { "description": "Property tests over limit configs", "coverageTarget": "config parse + clamp paths", "riskAreaRef": "config" }
              ],
              "environments": ["ci"],
              "entryCriteria": ["limiter merged behind a flag"],
              "exitCriteria": ["all lines green two consecutive CI runs"]
            }
            """),
        new DocumentExample(
            "invalid-unmapped-line-and-no-exit",
            false,
            """
            {
              "scope": "The tenant rate-limiter.",
              "riskAreas": [
                { "name": "concurrency", "rank": 1, "rationale": "Shared counters." }
              ],
              "strategyLines": [
                { "description": "Test the settings screen", "coverageTarget": "smoke", "riskAreaRef": "ui" }
              ],
              "environments": [],
              "entryCriteria": ["limiter merged"],
              "exitCriteria": []
            }
            """,
            new[] { StrategyLineUnmappedRiskArea, ExitCriteriaMissing }),
    };
}
