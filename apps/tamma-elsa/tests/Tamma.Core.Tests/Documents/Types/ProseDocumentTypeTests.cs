using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Types;

namespace Tamma.Core.Tests.Documents.Types;

/// <summary>
/// Story 41-1c AC2/AC4 — <see cref="ProseDocumentType"/> validation, pinned in
/// BOTH directions: arbitrary non-empty markdown VALIDATES (no forced structure
/// is a tested property, not a slogan) and an empty/whitespace body, a missing
/// tag, or an out-of-vocabulary tag each fails with its own named code — no
/// silent normalisation, no default.
/// </summary>
[TestFixture]
public class ProseDocumentTypeTests
{
    private static readonly ProseDocumentType Type = new();

    private static DocumentValidationResult Validate(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Type.Validate(doc.RootElement);
    }

    private static string Payload(
        string kind = "adr", string audience = "engineering",
        string title = "ADR-001: A decision", string body = "## Context\nWords.")
        => JsonSerializer.Serialize(new { kind, audience, title, body });

    // ── AC2, direction 1: prose is NOT schema-checked ────────────────────────

    [Test]
    public void Body_with_headings_in_scrambled_order_validates()
    {
        var r = Validate(Payload(body: "## Consequences\nc\n\n## Decision\nd\n\n## Context\nctx"));
        r.IsValid.Should().BeTrue("heading order is the author's business, never the validator's");
    }

    [Test]
    public void Body_with_no_headings_at_all_validates()
    {
        Validate(Payload(body: "Just three paragraphs of plain prose, no headings anywhere."))
            .IsValid.Should().BeTrue("a body without headings is still prose");
    }

    [Test]
    public void Body_of_a_single_word_validates()
    {
        Validate(Payload(body: "Done.")).IsValid.Should().BeTrue("length is not a rule");
    }

    // ── AC2, direction 2: an empty body is rejected with a named code ────────

    [TestCase("")]
    [TestCase("   \n\t  ")]
    public void Empty_or_whitespace_only_body_fails_with_PROSE_BODY_EMPTY(string body)
    {
        var r = Validate(Payload(body: body));
        r.IsValid.Should().BeFalse();
        r.Violations.Select(v => v.Code).Should().Equal(ProseDocumentType.BodyEmpty);
    }

    // ── AC4: out-of-vocabulary values fail loud, each with its own code ──────

    [Test]
    public void Unknown_audience_fails_with_exactly_PROSE_AUDIENCE_OUT_OF_VOCABULARY()
    {
        var r = Validate(Payload(audience: "marketing"));
        r.IsValid.Should().BeFalse();
        r.Violations.Select(v => v.Code).Should().Equal(ProseDocumentType.AudienceOutOfVocabulary);
        r.Violations[0].Message.Should().Contain("marketing", "the violation names the offending value");
    }

    [Test]
    public void Unknown_kind_fails_with_exactly_PROSE_KIND_OUT_OF_VOCABULARY()
    {
        var r = Validate(Payload(kind: "memo"));
        r.IsValid.Should().BeFalse();
        r.Violations.Select(v => v.Code).Should().Equal(ProseDocumentType.KindOutOfVocabulary);
        r.Violations[0].Message.Should().Contain("memo");
    }

    [Test]
    public void Unknown_kind_and_audience_fail_with_both_distinct_codes()
    {
        var r = Validate(Payload(kind: "memo", audience: "marketing"));
        r.IsValid.Should().BeFalse();
        r.Violations.Select(v => v.Code).Should().BeEquivalentTo(new[]
        {
            ProseDocumentType.KindOutOfVocabulary,
            ProseDocumentType.AudienceOutOfVocabulary,
        });
    }

    [Test]
    public void Vocabulary_is_case_sensitive_no_silent_normalisation()
    {
        // "ADR" is not "adr" — non-canonical casing is rejected, never folded.
        Validate(Payload(kind: "ADR")).Violations.Select(v => v.Code)
            .Should().Equal(ProseDocumentType.KindOutOfVocabulary);
        Validate(Payload(audience: "Engineering")).Violations.Select(v => v.Code)
            .Should().Equal(ProseDocumentType.AudienceOutOfVocabulary);
    }

    // ── The _MISSING trio ────────────────────────────────────────────────────

    [Test]
    public void Missing_kind_fails_with_PROSE_KIND_MISSING()
    {
        var r = Validate("""{ "audience": "engineering", "title": "t", "body": "b" }""");
        r.Violations.Select(v => v.Code).Should().Equal(ProseDocumentType.KindMissing);
    }

    [Test]
    public void Missing_audience_fails_with_PROSE_AUDIENCE_MISSING()
    {
        // AC7's write guard (D8): a prose document without an audience cannot be
        // written — the repository's write door re-validates through this rule.
        var r = Validate("""{ "kind": "adr", "title": "t", "body": "b" }""");
        r.Violations.Select(v => v.Code).Should().Equal(ProseDocumentType.AudienceMissing);
    }

    [Test]
    public void Missing_title_fails_with_PROSE_TITLE_MISSING()
    {
        var r = Validate(Payload(title: ""));
        r.Violations.Select(v => v.Code).Should().Equal(ProseDocumentType.TitleMissing);
    }

    [Test]
    public void Non_object_payload_fails_with_MALFORMED_PAYLOAD()
    {
        var r = Validate("""[ "prose" ]""");
        r.IsValid.Should().BeFalse();
        r.Violations.Select(v => v.Code).Should().Equal(ProseDocumentType.MalformedPayload);
    }
}
