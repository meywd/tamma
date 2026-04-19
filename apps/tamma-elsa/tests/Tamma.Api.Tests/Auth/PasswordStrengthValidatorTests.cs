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

    [TestCase("password123")]
    [TestCase("Password123")]
    [TestCase("admin123")]
    public void CommonPassword_Rejected(string pwd)
    {
        var r = PasswordStrengthValidator.Validate(pwd);
        r.Valid.Should().BeFalse();
        r.Errors.Should().Contain(e => e.Contains("too common"));
    }
}
