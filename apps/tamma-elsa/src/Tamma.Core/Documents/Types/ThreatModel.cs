using System.Text.Json;
using System.Text.Json.Serialization;
using Tamma.Api.Services.Agents;

namespace Tamma.Core.Documents.Types;

/// <summary>
/// The closed STRIDE threat-category vocabulary (Story 41-1b, Design Decision
/// D6). STRIDE is the SHIPPED closed set; a configurable taxonomy is explicitly
/// out of scope until a consumer asks. Out-of-vocab values are violations, never
/// silent clamps.
/// </summary>
public enum StrideCategory
{
    [Wire("spoofing")] Spoofing,
    [Wire("tampering")] Tampering,
    [Wire("repudiation")] Repudiation,
    [Wire("information-disclosure")] InformationDisclosure,
    [Wire("denial-of-service")] DenialOfService,
    [Wire("elevation-of-privilege")] ElevationOfPrivilege,
}

/// <summary>The closed residual-risk vocabulary (Story 41-1b, Design Decision D6).</summary>
public enum RiskLevel
{
    [Wire("low")] Low,
    [Wire("medium")] Medium,
    [Wire("high")] High,
    [Wire("critical")] Critical,
}

/// <summary>One asset at risk in a <see cref="ThreatModel"/> (Story 41-1b).</summary>
public sealed record ThreatModelAsset
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("name")] public string Name { get; init; } = "";
}

/// <summary>
/// One modelled threat (Story 41-1b): bound to a declared asset
/// (<see cref="AssetRef"/>), categorised in the closed STRIDE vocabulary, with a
/// required <see cref="Mitigation"/> and a <see cref="ResidualRisk"/> level.
/// </summary>
public sealed record ThreatModelThreat
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("assetRef")] public string AssetRef { get; init; } = "";
    [JsonPropertyName("category")] public string Category { get; init; } = "";
    [JsonPropertyName("description")] public string Description { get; init; } = "";
    [JsonPropertyName("mitigation")] public string Mitigation { get; init; } = "";
    [JsonPropertyName("residualRisk")] public string ResidualRisk { get; init; } = "";
}

/// <summary>
/// A threat model (Story 41-1b; epic README's new-types table): <c>Findings</c>
/// cite evidence but carry no attack structure — threat modelling needs
/// assets/threats/mitigations, and an unmitigated high-risk threat forces an
/// <see cref="Escalation"/>.
/// </summary>
public sealed record ThreatModel
{
    [JsonPropertyName("assets")] public IReadOnlyList<ThreatModelAsset> Assets { get; init; } = [];
    [JsonPropertyName("threats")] public IReadOnlyList<ThreatModelThreat> Threats { get; init; } = [];

    /// <summary>The escalation statement — REQUIRED when any threat's residual risk is high/critical.</summary>
    [JsonPropertyName("escalation")] public string? Escalation { get; init; }
}

/// <summary>
/// <see cref="IDocumentType"/> for the <c>threat-model</c> document (Story 41-1b
/// AC2): ≥1 asset and ≥1 threat; every threat bound to a declared asset,
/// categorised in the closed STRIDE set, mitigated, and residual-risk-rated; and
/// — the load-bearing rule — a residual high/critical threat with no escalation
/// block is rejected.
/// </summary>
public sealed class ThreatModelDocumentType : IDocumentType
{
    /// <summary>Payload could not be deserialized into the typed shape.</summary>
    public const string MalformedPayload = "MALFORMED_PAYLOAD";

    /// <summary>No assets — a threat model must name what is at risk.</summary>
    public const string NoAssets = "NO_ASSETS";

    /// <summary>No threats — a threat model with no threats models nothing.</summary>
    public const string NoThreats = "NO_THREATS";

    /// <summary>
    /// Two assets share an id — threats bind by <c>assetRef</c>, so a duplicate
    /// asset id makes the binding ambiguous (adversarial review 2026-07-29; the
    /// <c>CRITERION_ID_DUPLICATED</c> naming pattern).
    /// </summary>
    public const string AssetIdDuplicated = "ASSET_ID_DUPLICATED";

    /// <summary>
    /// Two threats share an id — mitigations and escalations reference threats by
    /// id, so ids must be unique (adversarial review 2026-07-29).
    /// </summary>
    public const string ThreatIdDuplicated = "THREAT_ID_DUPLICATED";

    /// <summary>A threat references no declared asset.</summary>
    public const string ThreatUnknownAsset = "THREAT_UNKNOWN_ASSET";

    /// <summary>A threat's category is missing or outside the closed STRIDE vocabulary.</summary>
    public const string ThreatCategoryOutOfVocabulary = "THREAT_CATEGORY_OUT_OF_VOCABULARY";

    /// <summary>A threat states no mitigation.</summary>
    public const string ThreatMissingMitigation = "THREAT_MISSING_MITIGATION";

    /// <summary>A threat's residual risk is missing or outside the closed vocabulary.</summary>
    public const string ResidualRiskOutOfVocabulary = "RESIDUAL_RISK_OUT_OF_VOCABULARY";

    /// <summary>
    /// The load-bearing rule (epic README: "unmitigated high-risk ⇒ escalation"):
    /// a threat whose residual risk is high/critical with no document-level
    /// escalation statement is rejected.
    /// </summary>
    public const string UnmitigatedHighRiskWithoutEscalation = "UNMITIGATED_HIGH_RISK_WITHOUT_ESCALATION";

    public string Key => DocumentTypeKey.ThreatModel.ToWire();
    public int SchemaVersion => 1;
    public Type PayloadClrType => typeof(ThreatModel);

    public DocumentValidationResult Validate(JsonElement payload) =>
        DocumentPayloadGuard.Run(payload, ValidateCore);

    private DocumentValidationResult ValidateCore(JsonElement payload)
    {
        ThreatModel? doc;
        try
        {
            doc = payload.Deserialize<ThreatModel>(DocumentJson.Options);
        }
        catch (JsonException)
        {
            return DocumentValidationResult.Invalid(new DocumentViolation(
                MalformedPayload, "The payload could not be parsed as a threat-model document."));
        }

        if (doc is null)
            return DocumentValidationResult.Invalid(new DocumentViolation(
                MalformedPayload, "The payload deserialized to null."));

        var violations = new List<DocumentViolation>();

        var assets = doc.Assets ?? [];
        if (assets.Count == 0)
            violations.Add(new DocumentViolation(
                NoAssets, "The model declares no assets — a threat model must name what is at risk."));

        var assetIds = new HashSet<string>(StringComparer.Ordinal);
        var reportedAssetDupes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var asset in assets)
        {
            var id = asset.Id?.Trim();
            if (string.IsNullOrEmpty(id))
                continue;
            if (!assetIds.Add(id) && reportedAssetDupes.Add(id))
                violations.Add(new DocumentViolation(
                    AssetIdDuplicated,
                    $"Asset id '{id}' is declared more than once — threats bind by assetRef, so asset ids must " +
                    "be unique."));
        }

        var threats = doc.Threats ?? [];
        if (threats.Count == 0)
            violations.Add(new DocumentViolation(
                NoThreats, "The model declares no threats — a threat model with no threats models nothing."));

        var escalationStated = !string.IsNullOrWhiteSpace(doc.Escalation);
        var highRiskReported = false;
        var seenThreatIds = new HashSet<string>(StringComparer.Ordinal);
        var reportedThreatDupes = new HashSet<string>(StringComparer.Ordinal);
        var index = 0;
        foreach (var threat in threats)
        {
            index++;
            var threatId = threat.Id?.Trim() ?? "";
            var label = threatId.Length == 0 ? $"#{index}" : $"'{threatId}'";

            if (threatId.Length > 0 && !seenThreatIds.Add(threatId) && reportedThreatDupes.Add(threatId))
                violations.Add(new DocumentViolation(
                    ThreatIdDuplicated,
                    $"Threat id '{threatId}' is used more than once — threat ids must be unique."));

            var assetRef = threat.AssetRef?.Trim() ?? "";
            if (assetRef.Length == 0 || !assetIds.Contains(assetRef))
                violations.Add(new DocumentViolation(
                    ThreatUnknownAsset,
                    $"Threat {label} references asset '{assetRef}', which is not declared in assets — every threat " +
                    "must name the asset it endangers."));

            if (!EnumWire<StrideCategory>.TryParse(threat.Category ?? "", out _))
                violations.Add(new DocumentViolation(
                    ThreatCategoryOutOfVocabulary,
                    $"Threat {label} has category '{threat.Category}' — it must be one of: spoofing, tampering, " +
                    "repudiation, information-disclosure, denial-of-service, elevation-of-privilege."));

            if (string.IsNullOrWhiteSpace(threat.Mitigation))
                violations.Add(new DocumentViolation(
                    ThreatMissingMitigation,
                    $"Threat {label} states no mitigation — every threat must say how it is mitigated."));

            if (!EnumWire<RiskLevel>.TryParse(threat.ResidualRisk ?? "", out var residual))
            {
                violations.Add(new DocumentViolation(
                    ResidualRiskOutOfVocabulary,
                    $"Threat {label} has residualRisk '{threat.ResidualRisk}' — it must be one of: low, medium, " +
                    "high, critical."));
            }
            else if (residual is RiskLevel.High or RiskLevel.Critical && !escalationStated && !highRiskReported)
            {
                highRiskReported = true;
                violations.Add(new DocumentViolation(
                    UnmitigatedHighRiskWithoutEscalation,
                    $"Threat {label} carries residual risk '{EnumWire<RiskLevel>.ToWire(residual)}' but the model " +
                    "states no escalation — an unmitigated high-risk threat must be escalated, not filed."));
            }
        }

        return violations.Count == 0
            ? DocumentValidationResult.Valid()
            : DocumentValidationResult.Invalid(violations.ToArray());
    }

    public string RenderContract() => Contract;

    public IReadOnlyList<DocumentExample> Examples => s_examples;

    // ── Contract + examples ──────────────────────────────────────────────────
    // Producing cell (41-1b D4): (security, threat-model).
    // The cell is NOT bound in ContractBindingTests yet (no compiled dispatch site
    // exists until 41-19 lands its workflow — the stale-Bindings guard forbids an
    // early entry); the intended tokens below are pinned Core-side by
    // RenderContractTokenTests so 41-19 binds against a stable contract.
    private const string Contract =
        """
        Return ONLY a JSON object of this shape:
        {
          "assets": [
            { "id": "A1", "name": "the asset at risk" }
          ],
          "threats": [
            {
              "id": "T1",
              "assetRef": "A1",
              "category": "spoofing | tampering | repudiation | information-disclosure | denial-of-service | elevation-of-privilege",
              "description": "how the attack works",
              "mitigation": "how the threat is mitigated",
              "residualRisk": "low | medium | high | critical"
            }
          ],
          "escalation": "REQUIRED when any residualRisk is high or critical: who is escalated to and why"
        }
        Rules: declare at least one asset and one threat; every threat must reference a
        declared asset, use a STRIDE "category", state a "mitigation", and rate its
        "residualRisk"; any high/critical residual risk REQUIRES the "escalation" statement.
        """;

    private static readonly IReadOnlyList<DocumentExample> s_examples = new[]
    {
        new DocumentExample(
            "valid-mitigated-model",
            true,
            """
            {
              "assets": [
                { "id": "A1", "name": "tenant connection strings" }
              ],
              "threats": [
                {
                  "id": "T1",
                  "assetRef": "A1",
                  "category": "information-disclosure",
                  "description": "A log statement could leak a decrypted connection string.",
                  "mitigation": "Connection strings are AES-GCM encrypted at rest and redacted by the log sanitizer.",
                  "residualRisk": "low"
                }
              ]
            }
            """),
        new DocumentExample(
            "invalid-high-risk-without-escalation",
            false,
            """
            {
              "assets": [
                { "id": "A1", "name": "tenant connection strings" }
              ],
              "threats": [
                {
                  "id": "T1",
                  "assetRef": "A1",
                  "category": "elevation-of-privilege",
                  "description": "A pooled role could read a sibling tenant's schema.",
                  "mitigation": "Search-path scoping only; no per-role REVOKE yet.",
                  "residualRisk": "high"
                }
              ]
            }
            """,
            new[] { UnmitigatedHighRiskWithoutEscalation }),
    };
}
