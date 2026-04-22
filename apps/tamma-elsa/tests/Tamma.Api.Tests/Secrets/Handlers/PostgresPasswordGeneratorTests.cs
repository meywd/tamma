using System.Text.RegularExpressions;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Secrets.Handlers;

namespace Tamma.Api.Tests.Secrets.Handlers;

/// <summary>
/// Story 29-7 AC2 + AC7 — tests for the password generator +
/// safe-character invariant. The generated password must never contain
/// a single quote, backslash, semicolon, or any non-ASCII character.
/// </summary>
[TestFixture]
public class PostgresPasswordGeneratorTests
{
    private static readonly Regex Safe = new(@"^[A-Za-z0-9!@#$%^&*()_+\-=\[\]{}|:,.<>?]+$");

    [Test]
    public void Generate_DefaultLength_Is64()
    {
        var pw = PostgresPasswordGenerator.Generate();
        pw.Should().HaveLength(64);
    }

    [TestCase(16)]
    [TestCase(32)]
    [TestCase(128)]
    [TestCase(256)]
    public void Generate_Length_IsHonoured(int length)
    {
        PostgresPasswordGenerator.Generate(length).Should().HaveLength(length);
    }

    [TestCase(0)]
    [TestCase(15)]
    [TestCase(257)]
    [TestCase(-1)]
    public void Generate_LengthOutOfRange_Throws(int length)
    {
        Action act = () => PostgresPasswordGenerator.Generate(length);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void Generate_ContainsNoForbiddenCharacters()
    {
        for (var i = 0; i < 500; i++)
        {
            var pw = PostgresPasswordGenerator.Generate();
            pw.Should().NotContain("'");
            pw.Should().NotContain("\\");
            pw.Should().NotContain(";");
            pw.Should().NotContain("\n");
            pw.All(c => c < 128).Should().BeTrue("only ASCII");
        }
    }

    [Test]
    public void Generate_AllCharactersFromSafeAlphabet()
    {
        for (var i = 0; i < 200; i++)
        {
            var pw = PostgresPasswordGenerator.Generate();
            Safe.IsMatch(pw).Should().BeTrue($"pw '{pw}' did not match the safe regex");
        }
    }

    [Test]
    public void IsSafe_RejectsSingleQuote()
    {
        PostgresPasswordGenerator.IsSafe("valid'injection").Should().BeFalse();
    }

    [Test]
    public void IsSafe_RejectsBackslash()
    {
        PostgresPasswordGenerator.IsSafe(@"valid\backslash").Should().BeFalse();
    }

    [Test]
    public void IsSafe_RejectsUnicode()
    {
        PostgresPasswordGenerator.IsSafe("pässword").Should().BeFalse();
    }

    [Test]
    public void IsSafe_AcceptsValidCharset()
    {
        PostgresPasswordGenerator.IsSafe("abcABC123!@#$%^&*()_+-=[]{}|:,.<>?").Should().BeTrue();
    }

    [Test]
    public void IsSafe_RejectsEmpty()
    {
        PostgresPasswordGenerator.IsSafe("").Should().BeFalse();
    }

    [Test]
    public void Generate_ProducesDistinctPasswords()
    {
        var set = new HashSet<string>();
        for (var i = 0; i < 100; i++)
            set.Add(PostgresPasswordGenerator.Generate());
        set.Should().HaveCount(100);
    }
}
