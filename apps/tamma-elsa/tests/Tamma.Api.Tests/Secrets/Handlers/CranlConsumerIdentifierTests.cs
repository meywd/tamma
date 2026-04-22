using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Secrets.Handlers;

namespace Tamma.Api.Tests.Secrets.Handlers;

/// <summary>
/// Story 29-8 — Cranl consumer identifier parser.
/// </summary>
[TestFixture]
public class CranlConsumerIdentifierTests
{
    [Test]
    public void Parse_AppAndEnv()
    {
        var id = CranlConsumerIdentifier.Parse("app=app_abc;env=DATABASE_URL");
        id.AppId.Should().Be("app_abc");
        id.EnvVarName.Should().Be("DATABASE_URL");
    }

    [Test]
    public void Parse_ApplicationAndNameAliases()
    {
        var id = CranlConsumerIdentifier.Parse("application=app_1;name=TOKEN");
        id.AppId.Should().Be("app_1");
        id.EnvVarName.Should().Be("TOKEN");
    }

    [Test]
    public void Parse_MissingApp_Throws()
    {
        Action act = () => CranlConsumerIdentifier.Parse("env=X");
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void Parse_MissingEnv_Throws()
    {
        Action act = () => CranlConsumerIdentifier.Parse("app=x");
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void Parse_ExtraKeys_Tolerated()
    {
        var id = CranlConsumerIdentifier.Parse("app=a;env=E;extra=x");
        id.AppId.Should().Be("a");
    }

    [Test]
    public void Parse_EmptyString_Throws()
    {
        Action act = () => CranlConsumerIdentifier.Parse("");
        act.Should().Throw<ArgumentException>();
    }
}
