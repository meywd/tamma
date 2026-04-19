using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Auth;

namespace Tamma.Api.Tests.Auth;

[TestFixture]
public class PasswordStrengthValidatorTests
{
    [Test]
    public void StrongPassword_Accepted()
    {
        PasswordStrengthValidator.Validate("CorrectHorse1").Valid.Should().BeTrue();
    }

    [Test]
    public void TooShort_Rejected()
    {
        var r = PasswordStrengthValidator.Validate("Aa1");
        r.Valid.Should().BeFalse();
        r.Errors.Should().Contain(e => e.Contains("at least"));
    }

    [Test]
    public void TooLong_Rejected()
    {
        var pwd = new string('A', 200) + "1a";
        var r = PasswordStrengthValidator.Validate(pwd);
        r.Valid.Should().BeFalse();
        r.Errors.Should().Contain(e => e.Contains("at most"));
    }

    [Test]
    public void MissingUppercase_Rejected()
    {
        var r = PasswordStrengthValidator.Validate("lowercase1");
        r.Valid.Should().BeFalse();
        r.Errors.Should().Contain(e => e.Contains("uppercase"));
    }

    [Test]
    public void MissingLowercase_Rejected()
    {
        var r = PasswordStrengthValidator.Validate("UPPERCASE1");
        r.Valid.Should().BeFalse();
        r.Errors.Should().Contain(e => e.Contains("lowercase"));
    }

    [Test]
    public void MissingDigit_Rejected()
    {
        var r = PasswordStrengthValidator.Validate("MissingDigit");
        r.Valid.Should().BeFalse();
        r.Errors.Should().Contain(e => e.Contains("digit"));
    }

    // Representative entries across the top-1000 list. The upstream SecLists
    // ranking is observed-frequency, so highly-rated dictionary words appear
    // but some plausible-sounding variants (e.g. "password123") are not in
    // the actual list. Each TestCase below is a real entry; the mixed-case
    // variants exercise case-insensitive lookup.
    [TestCase("password")]      // #2 in SecLists top-1000
    [TestCase("password1")]     // #308
    [TestCase("qwerty")]        // #4
    [TestCase("qwertyuiop")]    // #21
    [TestCase("letmein")]       // #16
    [TestCase("Letmein")]       // case-insensitive
    [TestCase("LETMEIN")]       // case-insensitive
    [TestCase("iloveyou")]      // entry further down the list
    [TestCase("trustno1")]      // #37 area
    public void CommonPassword_Rejected(string pwd)
    {
        var r = PasswordStrengthValidator.Validate(pwd);
        r.Valid.Should().BeFalse();
        r.Errors.Should().Contain(e => e.Contains("too common"));
    }

    [Test]
    public void CommonPasswordList_LoadsTopThousand()
    {
        // Audit finding auth/013: the embedded SecLists top-1000 file must
        // load at least 950 entries (blank lines + any dedup on case are
        // tolerated; the source ships with ~1000 unique case-insensitive
        // rows).
        PasswordStrengthValidator.CommonPasswordCount.Should().BeGreaterThanOrEqualTo(950);
    }
}
