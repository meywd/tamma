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
    [property: JsonPropertyName("resolvedAt")] DateTimeOffset ResolvedAt);
