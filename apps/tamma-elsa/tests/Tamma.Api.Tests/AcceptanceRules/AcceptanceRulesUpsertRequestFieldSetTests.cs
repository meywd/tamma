using System.Reflection;
using System.Text.Json.Serialization;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Dtos.AcceptanceRules;

namespace Tamma.Api.Tests.AcceptanceRules;

/// <summary>
/// Story 43-0 AC5 (D3) — the field-completeness tripwire. The dashboard's
/// <c>interface AcceptanceRules</c> is hand-maintained (this repo deliberately does
/// not generate TS types — see <c>ConventionSeedDriftTests</c>), so a DTO field the
/// client never learned about is invisible to <c>tsc</c>: that is exactly how
/// <c>acceptorRequirement</c> came to be omitted from every PUT the admin dialog
/// sent, and how every save silently reset a document type's human-acceptor floor.
///
/// <para>This pin cannot prove the two languages agree. It makes the C# side
/// impossible to change SILENTLY: adding or removing a wire property fails here,
/// with a message naming the two TypeScript files to update.</para>
/// </summary>
[TestFixture]
public class AcceptanceRulesUpsertRequestFieldSetTests
{
    /// <summary>
    /// The exact wire property set of <see cref="AcceptanceRulesUpsertRequest"/>.
    /// Ordered as declared, so the failure diff reads like the record.
    /// </summary>
    private static readonly string[] ExpectedWireProperties =
    {
        "autonomyLevel",
        "maxRevisionRounds",
        "maxValidationRepairAttempts",
        "ambiguityEscalationThreshold",
        "alwaysEscalate",
        "reviewerSelection",
        "decisionGuidance",
        "routingGuidance",
        "acceptorRequirement",
    };

    private const string Pointer =
        "AcceptanceRulesUpsertRequest gained or lost a wire field. Update "
        + "`packages/dashboard/src/services/admin/acceptance-rules-api-client.ts` "
        + "(`interface AcceptanceRules`) AND "
        + "`packages/dashboard/src/components/acceptance-rules/RulesEditDialog.tsx` "
        + "(the `body` memo + its dependency array), then update this pin. "
        + "A field the dialog does not send is a field the API can silently reset "
        + "— that is the Story 43-0 bug.";

    [Test]
    public void Wire_property_set_is_pinned()
    {
        var actual = PrimaryConstructorWireNames();
        actual.Should().Equal(ExpectedWireProperties, Pointer);
    }

    /// <summary>
    /// Every parameter carries an explicit <c>[JsonPropertyName]</c> (39-2 D8): a
    /// field relying on the serializer's naming policy would be a second silent
    /// wire-contract source.
    /// </summary>
    [Test]
    public void Every_parameter_declares_an_explicit_JsonPropertyName()
    {
        foreach (var p in PrimaryConstructor().GetParameters())
        {
            WireName(p).Should().NotBeNullOrWhiteSpace(
                $"parameter '{p.Name}' must declare [property: JsonPropertyName(...)]");
        }
    }

    /// <summary>
    /// The optional member is NULLABLE, so "absent" is representable and distinct
    /// from any legal value. Re-introducing a non-nullable
    /// <c>= AcceptorRequirement.Any</c> default here re-introduces the 43-0 bug:
    /// the binder would once again invent a policy value the caller never sent.
    /// </summary>
    [Test]
    public void AcceptorRequirement_is_nullable_so_absent_is_not_a_value()
    {
        var p = PrimaryConstructor().GetParameters()
            .Single(x => WireName(x) == "acceptorRequirement");

        Nullable.GetUnderlyingType(p.ParameterType).Should().NotBeNull(
            "an omitted acceptorRequirement must bind to null ('the caller did not "
            + "say'), never to a defaulted enum value — Story 43-0");
    }

    private static ConstructorInfo PrimaryConstructor() =>
        typeof(AcceptanceRulesUpsertRequest)
            .GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .First();

    private static string[] PrimaryConstructorWireNames() =>
        PrimaryConstructor()
            .GetParameters()
            .Select(p => WireName(p)
                ?? throw new InvalidOperationException(
                    $"Parameter '{p.Name}' has no [JsonPropertyName]. {Pointer}"))
            .ToArray();

    /// <summary>
    /// The wire name a positional record parameter serializes under. The record
    /// uses <c>[property: JsonPropertyName(...)]</c>, so the attribute lands on the
    /// generated PROPERTY, not on the constructor parameter — read it there, keeping
    /// the constructor's declaration order.
    /// </summary>
    private static string? WireName(ParameterInfo p)
    {
        var prop = typeof(AcceptanceRulesUpsertRequest).GetProperty(
            p.Name!,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        return prop?.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name
            ?? p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name;
    }
}
