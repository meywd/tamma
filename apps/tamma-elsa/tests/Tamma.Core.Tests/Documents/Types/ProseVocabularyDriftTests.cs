using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Documents.Types;

namespace Tamma.Core.Tests.Documents.Types;

/// <summary>
/// Story 41-1c D3 — drift tests for the two prose vocabularies (the
/// <c>AgentRoleTests</c> / <c>DocumentTypeKeyTests</c> shape): count pins,
/// per-member wire spelling, round-trip, unique wires, and ordinal
/// case-sensitivity (non-canonical casing is rejected, never folded).
/// </summary>
[TestFixture]
public class ProseVocabularyDriftTests
{
    [Test]
    public void ProseAudience_has_exactly_six_members() =>
        // Seeded from the actual consumers (41-1c D3): engineering (41-9, 41-22),
        // developer (41-24 changelog, 41-25 api-docs), user (41-24 release notes,
        // 41-25 user-docs), ops (41-26), stakeholder (41-4, 41-5), team (41-8).
        // Growing this set is a conscious edit here AND in the ProseDocumentType
        // contract text.
        Enum.GetValues<ProseAudience>().Should().HaveCount(6);

    [Test]
    public void ProseKind_has_exactly_ten_members() =>
        // One member per prose-family document (41-1c D3). The kind names WHAT
        // the prose is; it never implies a validated body structure.
        Enum.GetValues<ProseKind>().Should().HaveCount(10);

    [TestCase(ProseAudience.Engineering, "engineering")]
    [TestCase(ProseAudience.Developer, "developer")]
    [TestCase(ProseAudience.User, "user")]
    [TestCase(ProseAudience.Ops, "ops")]
    [TestCase(ProseAudience.Stakeholder, "stakeholder")]
    [TestCase(ProseAudience.Team, "team")]
    public void Audience_ToWire_returns_canonical_string(ProseAudience value, string wire) =>
        value.ToWire().Should().Be(wire);

    [TestCase(ProseKind.Adr, "adr")]
    [TestCase(ProseKind.Postmortem, "postmortem")]
    [TestCase(ProseKind.ReleaseNotes, "release-notes")]
    [TestCase(ProseKind.Changelog, "changelog")]
    [TestCase(ProseKind.UserDocs, "user-docs")]
    [TestCase(ProseKind.ApiDocs, "api-docs")]
    [TestCase(ProseKind.Runbook, "runbook")]
    [TestCase(ProseKind.Roadmap, "roadmap")]
    [TestCase(ProseKind.StatusUpdate, "status-update")]
    [TestCase(ProseKind.RetroNarrative, "retro-narrative")]
    public void Kind_ToWire_returns_canonical_string(ProseKind value, string wire) =>
        value.ToWire().Should().Be(wire);

    [Test]
    public void Audience_roundtrip_holds_for_every_member()
    {
        foreach (var value in Enum.GetValues<ProseAudience>())
        {
            ProseAudienceExtensions.TryParse(value.ToWire(), out var parsed).Should().BeTrue();
            parsed.Should().Be(value);
        }
    }

    [Test]
    public void Kind_roundtrip_holds_for_every_member()
    {
        foreach (var value in Enum.GetValues<ProseKind>())
        {
            ProseKindExtensions.TryParse(value.ToWire(), out var parsed).Should().BeTrue();
            parsed.Should().Be(value);
        }
    }

    [Test]
    public void Wires_are_unique_within_each_vocabulary()
    {
        Enum.GetValues<ProseAudience>().Select(a => a.ToWire()).Should().OnlyHaveUniqueItems();
        Enum.GetValues<ProseKind>().Select(k => k.ToWire()).Should().OnlyHaveUniqueItems();
    }

    [Test]
    public void Parsing_is_ordinal_case_sensitive()
    {
        ProseKindExtensions.TryParse("ADR", out _).Should().BeFalse(
            "non-canonical casing is rejected, not silently accepted");
        ProseAudienceExtensions.TryParse("Engineering", out _).Should().BeFalse();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void Null_or_whitespace_never_parses(string? input)
    {
        ProseKindExtensions.TryParse(input, out _).Should().BeFalse();
        ProseAudienceExtensions.TryParse(input, out _).Should().BeFalse();
    }
}
