using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.TenantLifecycle;

namespace Tamma.Activities.Tests.TenantLifecycle;

/// <summary>
/// Story 28-5 — asserts the role-password generator's safety properties:
/// fixed length, alphabet, no quote/backslash/semicolon, and entropy
/// (no two consecutive runs match — accidental seeding bug detector).
/// </summary>
[TestFixture]
public class CreateTenantRoleActivityPasswordTests
{
    [Test]
    public void GenerateStrongPassword_Is32CharsLong()
    {
        var p = CreateTenantRoleActivity.GenerateStrongPassword();
        p.Should().HaveLength(32);
    }

    [Test]
    public void GenerateStrongPassword_HasNoQuoteBackslashOrSemicolon()
    {
        for (var i = 0; i < 32; i++)
        {
            var p = CreateTenantRoleActivity.GenerateStrongPassword();
            p.Should().NotContain("'");
            p.Should().NotContain("\\");
            p.Should().NotContain(";");
        }
    }

    [Test]
    public void GenerateStrongPassword_UsesOnlyAllowedAlphabet()
    {
        const string alphabet =
            "ABCDEFGHJKLMNPQRSTUVWXYZ" + "abcdefghijkmnopqrstuvwxyz" + "23456789" + "!@#%^*_-";
        var p = CreateTenantRoleActivity.GenerateStrongPassword();
        foreach (var ch in p)
            alphabet.Should().Contain(ch.ToString(),
                $"character '{ch}' was not in the allowed alphabet");
    }

    [Test]
    public void GenerateStrongPassword_TwoConsecutiveRunsDiffer()
    {
        // Cheap entropy smoke test — a misconfigured RNG seed would
        // produce identical output here.
        var a = CreateTenantRoleActivity.GenerateStrongPassword();
        var b = CreateTenantRoleActivity.GenerateStrongPassword();
        a.Should().NotBe(b);
    }
}
