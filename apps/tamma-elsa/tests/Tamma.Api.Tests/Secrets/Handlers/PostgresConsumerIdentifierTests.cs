using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Secrets.Handlers;

namespace Tamma.Api.Tests.Secrets.Handlers;

/// <summary>
/// Story 29-7 — consumer identifier parser. The rotation workflow
/// passes the first <c>ConsumerRef.Identifier</c> string to the
/// handler; the handler needs to extract role + db.
/// </summary>
[TestFixture]
public class PostgresConsumerIdentifierTests
{
    [Test]
    public void Parse_RoleAndDb()
    {
        var id = PostgresConsumerIdentifier.Parse("role=tamma_app;db=tamma_control");
        id.Role.Should().Be("tamma_app");
        id.Db.Should().Be("tamma_control");
    }

    [Test]
    public void Parse_RoleOnly_DbNull()
    {
        var id = PostgresConsumerIdentifier.Parse("role=tamma_app");
        id.Role.Should().Be("tamma_app");
        id.Db.Should().BeNull();
    }

    [Test]
    public void Parse_ExtraWhitespace_Tolerated()
    {
        var id = PostgresConsumerIdentifier.Parse(" role =  tamma_app ; db = mydb ");
        id.Role.Should().Be("tamma_app");
        id.Db.Should().Be("mydb");
    }

    [Test]
    public void Parse_DatabaseAlias()
    {
        var id = PostgresConsumerIdentifier.Parse("role=x;database=y");
        id.Db.Should().Be("y");
    }

    [Test]
    public void Parse_UnknownKey_Ignored()
    {
        var id = PostgresConsumerIdentifier.Parse("role=x;foo=bar");
        id.Role.Should().Be("x");
        id.Db.Should().BeNull();
    }

    [Test]
    public void Parse_NoRole_Throws()
    {
        Action act = () => PostgresConsumerIdentifier.Parse("db=x");
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void Parse_EmptyString_Throws()
    {
        Action act = () => PostgresConsumerIdentifier.Parse("");
        act.Should().Throw<ArgumentException>();
    }
}
