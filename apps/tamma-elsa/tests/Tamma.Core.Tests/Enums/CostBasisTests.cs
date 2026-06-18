using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Enums;

namespace Tamma.Core.Tests.Enums;

/// <summary>
/// Story 36-1 (AC3) — pins the two-member shape of <see cref="CostBasis"/>
/// and the lowercase <c>byok</c>/<c>platform</c> text form the analytics EF
/// model persists (via <c>HasConversion</c>). The persisted string is the
/// stable wire/DB discriminator shared by every Epic 36 dimension query — a
/// guard against ordinal drift and against adding a member without a
/// matching lowercase persistence form.
/// </summary>
[TestFixture]
public class CostBasisTests
{
    [Test]
    public void Has_Exactly_TwoMembers_Byok_And_Platform()
    {
        Enum.GetNames<CostBasis>().Should().BeEquivalentTo(new[] { "Byok", "Platform" });
    }

    [TestCase(CostBasis.Byok, 0)]
    [TestCase(CostBasis.Platform, 1)]
    public void Members_Pin_Their_Ordinals(CostBasis value, int ordinal)
    {
        ((int)value).Should().Be(ordinal,
            "the ordinal is part of the persisted contract — drift would silently "
            + "re-map historical analytics rows");
    }

    [TestCase(CostBasis.Byok, "byok")]
    [TestCase(CostBasis.Platform, "platform")]
    public void LowercaseName_Matches_PersistedTextForm(CostBasis value, string expected)
    {
        // The analytics model config persists CostBasis as lowercase text
        // (HasConversion). This pins the expectation the config enforces so
        // ad-hoc SQL and InMemory/Npgsql parity stay readable.
        Enum.GetName(value)!.ToLowerInvariant().Should().Be(expected);
    }
}
