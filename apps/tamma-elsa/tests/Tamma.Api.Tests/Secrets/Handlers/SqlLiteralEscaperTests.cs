using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Secrets.Handlers;

namespace Tamma.Api.Tests.Secrets.Handlers;

/// <summary>
/// Story 29-7 AC2 — belt-and-braces SQL-literal escaping.
/// </summary>
[TestFixture]
public class SqlLiteralEscaperTests
{
    [Test]
    public void Escape_SafePassword_ReturnsUnchanged()
    {
        var pw = PostgresPasswordGenerator.Generate();
        SqlLiteralEscaper.Escape(pw).Should().Be(pw);
    }

    [Test]
    public void Escape_UnsafeInput_Throws()
    {
        Action act = () => SqlLiteralEscaper.Escape("bad'quote");
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void EscapeIdentifier_DoubleQuote_IsDoubled()
    {
        SqlLiteralEscaper.EscapeIdentifier("weird\"role").Should().Be("weird\"\"role");
    }

    [Test]
    public void EscapeIdentifier_EmptyInput_Throws()
    {
        Action act = () => SqlLiteralEscaper.EscapeIdentifier("");
        act.Should().Throw<ArgumentException>();
    }

    [Test]
    public void EscapeIdentifier_PlainName_Unchanged()
    {
        SqlLiteralEscaper.EscapeIdentifier("tamma_app").Should().Be("tamma_app");
    }
}
