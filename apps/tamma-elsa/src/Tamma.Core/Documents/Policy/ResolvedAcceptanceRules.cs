using System.Text.Json.Serialization;
using Tamma.Api.Services.Agents;

namespace Tamma.Core.Documents.Policy;

/// <summary>
/// Which layer produced a resolved rules row (Story 39-5 AC4/D2 three-tier
/// resolution): a per-type principal override, a principal base override (the
/// deployment-wide dial), or the shipped static default.
/// </summary>
[JsonConverter(typeof(WireEnumJsonConverter<AcceptanceRulesSource>))]
public enum AcceptanceRulesSource
{
    [Wire("system-default")]    SystemDefault,
    [Wire("principal-default")] PrincipalDefault,
    [Wire("type-override")]     TypeOverride,
}

/// <summary><see cref="AcceptanceRulesSource"/> wire helper.</summary>
public static class AcceptanceRulesSourceExtensions
{
    public static string ToWire(this AcceptanceRulesSource source) => EnumWire<AcceptanceRulesSource>.ToWire(source);
}

/// <summary>
/// The effective <see cref="AcceptanceRules"/> for a principal + document type,
/// carrying its provenance and version (Story 39-5 AC3/AC6). The <c>Version</c>
/// lets 39-6's decision event record which rules version the decision was made
/// under.
/// </summary>
public sealed record ResolvedAcceptanceRules(
    [property: JsonPropertyName("rules")] AcceptanceRules Rules,
    [property: JsonPropertyName("source")] AcceptanceRulesSource Source,
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("documentTypeKey")] string DocumentTypeKey,
    [property: JsonPropertyName("resolvedAt")] DateTimeOffset ResolvedAt)
{
    /// <summary>
    /// TRUE when the winning tier carried a LOWER <see cref="AcceptorRequirement"/>
    /// than this document type's shipped floor and was raised back up to it
    /// (<see cref="AcceptanceFloors.ApplyShippedAcceptorFloor"/>, epic-43 CD-1,
    /// 2026-07-30). It makes the one non-wholesale field in an otherwise
    /// wholesale resolution VISIBLE rather than surprising: <see cref="Source"/>
    /// still names the row that supplied every other field, and this flag says
    /// "…except the acceptor requirement, which the shipped per-type floor
    /// supplied". Additive on the wire; false for every resolution that was not
    /// raised.
    /// </summary>
    [JsonPropertyName("acceptorRequirementFloored")]
    public bool AcceptorRequirementFloored { get; init; }
}
