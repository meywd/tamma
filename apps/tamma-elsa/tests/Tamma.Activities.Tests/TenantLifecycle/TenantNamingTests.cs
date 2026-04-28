using FluentAssertions;
using NUnit.Framework;
using Tamma.Data.Pooling;

namespace Tamma.Activities.Tests.TenantLifecycle;

/// <summary>
/// Story 28-5 — naming helper. Asserts deterministic + length-bounded
/// identifiers, identifier quoting, and double-quote escaping.
/// </summary>
[TestFixture]
public class TenantNamingTests
{
    private static readonly Guid SampleTenant =
        new("c0ffee00-1234-5678-9abc-def012345678");

    [Test]
    public void HexOf_StripsHyphens_AndIsLowercaseHex()
    {
        TenantNaming.HexOf(SampleTenant).Should().Be("c0ffee00123456789abcdef012345678");
    }

    [Test]
    public void RoleName_HasFixedPrefix()
    {
        var role = TenantNaming.RoleName(SampleTenant);
        role.Should().StartWith(TenantNaming.Prefix);
        role.Should().Be("tamma_tenant_c0ffee00123456789abcdef012345678");
    }

    [Test]
    public void DatabaseName_MatchesRoleName()
    {
        TenantNaming.DatabaseName(SampleTenant).Should().Be(TenantNaming.RoleName(SampleTenant));
    }

    [Test]
    public void ElsaDatabaseName_AppendsSuffix()
    {
        var name = TenantNaming.ElsaDatabaseName(SampleTenant);
        name.Should().EndWith(TenantNaming.ElsaSuffix);
        name.Length.Should().BeLessThan(64,
            "Postgres identifiers are capped at 63 bytes");
    }

    [Test]
    public void Quote_WrapsInDoubleQuotes()
    {
        TenantNaming.Quote("foo").Should().Be("\"foo\"");
    }

    [Test]
    public void Quote_EscapesInternalDoubleQuotes()
    {
        TenantNaming.Quote("a\"b").Should().Be("\"a\"\"b\"");
    }

    [Test]
    public void Quote_RejectsEmpty()
    {
        var act = () => TenantNaming.Quote("");
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void RoleName_FitsPostgresIdentifierLimit()
    {
        var role = TenantNaming.RoleName(Guid.NewGuid());
        // 13 (prefix) + 32 (hex) = 45 bytes — well under 63.
        role.Length.Should().BeLessThan(64);
    }
}
