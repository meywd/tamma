using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Validation;

namespace Tamma.Api.Tests.Orgs;

[TestFixture]
public class SlugValidationTests
{
    [TestCase("acme")]
    [TestCase("acme-corp")]
    [TestCase("a1b2c3")]
    [TestCase("abc")]
    public void IsValidSlug_AcceptsLegitimate(string slug) =>
        SlugValidation.IsValidSlug(slug).Should().BeTrue();

    [TestCase("ab")]                             // too short
    [TestCase("a1234567890123456789012345678901234567890")] // 41 chars
    [TestCase("-acme")]                          // leading hyphen
    [TestCase("acme-")]                          // trailing hyphen
    [TestCase("Acme")]                           // uppercase
    [TestCase("my.org")]                         // invalid char
    [TestCase("")]
    [TestCase(null)]
    public void IsValidSlug_RejectsInvalid(string? slug) =>
        SlugValidation.IsValidSlug(slug).Should().BeFalse();

    [TestCase("admin")]
    [TestCase("api")]
    [TestCase("auth")]
    [TestCase("settings")]
    [TestCase("app")]
    [TestCase("www")]
    public void IsReservedSlug_FlagsReservedLabels(string slug) =>
        SlugValidation.IsReservedSlug(slug).Should().BeTrue();

    [TestCase("acme-corp")]
    [TestCase("personal-x")]
    [TestCase(null)]
    public void IsReservedSlug_AllowsNonReserved(string? slug) =>
        SlugValidation.IsReservedSlug(slug).Should().BeFalse();

    [TestCase("Ab", true)]
    [TestCase("A", false)]
    [TestCase("", false)]
    [TestCase(null, false)]
    public void IsValidName_LengthChecks(string? name, bool expected) =>
        SlugValidation.IsValidName(name).Should().Be(expected);

    [Test]
    public void IsValidName_RejectsLongerThan100() =>
        SlugValidation.IsValidName(new string('A', 101)).Should().BeFalse();

    [Test]
    public void IsValidName_AcceptsExactly100() =>
        SlugValidation.IsValidName(new string('A', 100)).Should().BeTrue();

    [Test]
    public void IsValidName_TreatsTrimmedLengthAsSemantic() =>
        SlugValidation.IsValidName("   A   ").Should().BeFalse();
}
