using FluentAssertions;
using NUnit.Framework;
using Tamma.Api.Services.Secrets.Handlers;

namespace Tamma.Api.Tests.Secrets.Handlers;

/// <summary>
/// Story 29-8 AC6 + env-text format edge cases.
/// </summary>
[TestFixture]
public class CranlEnvTextTests
{
    [Test]
    public void Parse_EmptyString_ReturnsEmpty()
    {
        CranlEnvText.Parse(string.Empty).Should().BeEmpty();
    }

    [Test]
    public void Parse_SingleLine_NoTrailingNewline()
    {
        var entries = CranlEnvText.Parse("KEY=value");
        entries.Should().HaveCount(1);
        entries[0].IsPair.Should().BeTrue();
        entries[0].Key.Should().Be("KEY");
        entries[0].Value.Should().Be("value");
    }

    [Test]
    public void Parse_MultipleLines_TrailingNewline()
    {
        var entries = CranlEnvText.Parse("A=1\nB=2\n");
        entries.Should().HaveCount(2);
        entries[0].Key.Should().Be("A");
        entries[1].Key.Should().Be("B");
    }

    [Test]
    public void Parse_ValueContainingEquals_PreservesAll()
    {
        var entries = CranlEnvText.Parse("CONN=host=x;port=5432\n");
        entries.Should().HaveCount(1);
        entries[0].Key.Should().Be("CONN");
        entries[0].Value.Should().Be("host=x;port=5432");
    }

    [Test]
    public void Parse_EmptyLineIsPreserved()
    {
        var entries = CranlEnvText.Parse("A=1\n\nB=2\n");
        entries.Should().HaveCount(3);
        entries[1].IsPair.Should().BeFalse();
    }

    [Test]
    public void Merge_UpdatesExistingKey()
    {
        var parsed = CranlEnvText.Parse("A=1\nB=2\n");
        var merged = CranlEnvText.Merge(parsed, "A", "new");
        merged.Should().HaveCount(2);
        merged[0].Value.Should().Be("new");
        merged[1].Value.Should().Be("2");
    }

    [Test]
    public void Merge_AddsNewKeyAtEnd()
    {
        var parsed = CranlEnvText.Parse("A=1\n");
        var merged = CranlEnvText.Merge(parsed, "B", "2");
        merged.Should().HaveCount(2);
        merged[1].Key.Should().Be("B");
    }

    [Test]
    public void Merge_PreservesOtherKeys()
    {
        var parsed = CranlEnvText.Parse("X=1\nY=2\nZ=3\n");
        var merged = CranlEnvText.Merge(parsed, "Y", "newY");
        var text = CranlEnvText.Serialize(merged);
        text.Should().Be("X=1\nY=newY\nZ=3\n");
    }

    [Test]
    public void Serialize_EmptyList_EmptyString()
    {
        CranlEnvText.Serialize(Array.Empty<EnvEntry>()).Should().BeEmpty();
    }

    [Test]
    public void Serialize_AppendsTrailingNewline()
    {
        var list = new[] { EnvEntry.Pair("A", "1") };
        CranlEnvText.Serialize(list).Should().Be("A=1\n");
    }

    [Test]
    public void DiffKeys_AddedKey()
    {
        var diff = CranlEnvText.DiffKeys("A=1\n", "A=1\nB=2\n");
        diff.Should().ContainSingle().Which.Should().Be("+ B");
    }

    [Test]
    public void DiffKeys_ChangedValue_ShowsTilde()
    {
        var diff = CranlEnvText.DiffKeys("A=1\n", "A=2\n");
        diff.Should().ContainSingle().Which.Should().Be("~ A");
    }

    [Test]
    public void DiffKeys_RemovedKey_ShowsMinus()
    {
        var diff = CranlEnvText.DiffKeys("A=1\nB=2\n", "A=1\n");
        diff.Should().ContainSingle().Which.Should().Be("- B");
    }

    [Test]
    public void DiffKeys_NoValueLeakedIntoOutput()
    {
        // Secret value in env — diff must not contain it.
        var diff = CranlEnvText.DiffKeys("SECRET=old\n", "SECRET=new-super-secret\n");
        string.Join("\n", diff).Should().NotContain("new-super-secret");
        string.Join("\n", diff).Should().NotContain("old");
    }

    [Test]
    public void RoundTrip_PreservesContent()
    {
        var original = "A=1\nB=2\n\n# a comment\nC=three\n";
        var entries = CranlEnvText.Parse(original);
        var serialized = CranlEnvText.Serialize(entries);
        serialized.Should().Be(original);
    }

    [Test]
    public void Parse_HandlesCrlf()
    {
        var entries = CranlEnvText.Parse("A=1\r\nB=2\r\n");
        entries.Should().HaveCount(2);
        entries[0].Value.Should().Be("1");
    }

    [Test]
    public void Merge_EmptyKey_Throws()
    {
        Action act = () => CranlEnvText.Merge(Array.Empty<EnvEntry>(), "", "v");
        act.Should().Throw<ArgumentException>();
    }
}
