using System.Text.Json.Serialization;
using Tamma.Api.Services.Agents;

namespace Tamma.Core.Documents.Policy;

/// <summary>
/// The orchestrator's routing decision (Story 39-5 AC2): decide the acceptance
/// itself, or assign it to a tenant ROLE (never an exact user — settled design
/// review 2026-07-21; 39-20 resolves the role's audience). Serialized
/// polymorphically with a <c>kind</c> discriminator through
/// <see cref="AcceptanceRulesJson.Options"/>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(DecideSelf), "decide-self")]
[JsonDerivedType(typeof(AssignToRole), "assign-to-role")]
public abstract record AcceptanceRouting
{
    /// <summary>The orchestrator answers the <see cref="AcceptanceDecision"/> itself.</summary>
    public sealed record DecideSelf : AcceptanceRouting;

    /// <summary>
    /// Address the decision to a tenant role. <see cref="RoleWire"/> is an
    /// <see cref="AgentRole"/> wire string; <see cref="Basis"/> records the
    /// visibility rule 39-20 intersects the role-holders with.
    /// </summary>
    public sealed record AssignToRole(
        [property: JsonPropertyName("roleWire")] string RoleWire,
        [property: JsonPropertyName("basis")] AssignmentBasis Basis) : AcceptanceRouting;
}

/// <summary>
/// The visibility rule an assignment is intersected with (Story 39-5 AC2; 39-20
/// consumes it): the issue initiator, or anyone with repo access.
/// </summary>
[JsonConverter(typeof(WireEnumJsonConverter<AssignmentBasis>))]
public enum AssignmentBasis
{
    [Wire("initiator")]  Initiator,
    [Wire("repo-access")] RepoAccess,
}

/// <summary><see cref="AssignmentBasis"/> wire helper.</summary>
public static class AssignmentBasisExtensions
{
    public static string ToWire(this AssignmentBasis basis) => EnumWire<AssignmentBasis>.ToWire(basis);
}
