using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Clarify;

namespace Tamma.Activities.Tests.Clarify;

/// <summary>
/// Story 3.5 — the clarify workflow's resume callback must read its control-flow boolean
/// (<c>Answered</c>) tolerant of a SERIALIZING workflow runtime (the #15/#437 lesson): the
/// in-process runtime keeps the resumed value a boxed <see cref="bool"/>, but a distributed
/// dispatcher round-trips it to a <see cref="string"/> or a <see cref="JsonElement"/>. A bare
/// <c>is true</c> pattern only matches the boxed-bool path — under serialization it silently
/// evaluates <c>false</c>, returning HTTP 200 while taking the WRONG branch. These tests also
/// cover the fail-closed <see cref="ClarifyParsing"/> helpers and the canonical bookmark-name
/// builder (suspend/resume parity + tenant folding).
/// </summary>
[TestFixture]
public class ClarifyResumeReadBackTests
{
    private static JsonElement JsonBool(bool value) => JsonDocument.Parse(value ? "true" : "false").RootElement;

    private static IDictionary<string, object> Input(params (string Key, object Value)[] entries)
    {
        var dict = new Dictionary<string, object>();
        foreach (var (key, value) in entries)
            dict[key] = value;
        return dict;
    }

    // ── ReadAnswers — Answered coercion tolerant of serialization ──────────

    [Test]
    public void ReadAnswers_BoxedBoolTrue_ReachesAnswered()
    {
        var (answered, answers) = WaitForClarifyingAnswersActivity.ReadAnswers(
            Input(("Answered", true), ("Answers", "use OAuth2")));
        answered.Should().BeTrue();
        answers.Should().Be("use OAuth2");
    }

    [Test]
    public void ReadAnswers_StringTrue_ReachesAnswered()
    {
        WaitForClarifyingAnswersActivity.ReadAnswers(Input(("Answered", "true"))).Answered.Should().BeTrue();
        WaitForClarifyingAnswersActivity.ReadAnswers(Input(("Answered", "True"))).Answered.Should().BeTrue();
    }

    [Test]
    public void ReadAnswers_JsonElementTrue_ReachesAnswered()
    {
        WaitForClarifyingAnswersActivity.ReadAnswers(Input(("Answered", JsonBool(true)))).Answered.Should().BeTrue();
    }

    [Test]
    public void ReadAnswers_FalseRepresentations_ReachNotAnswered()
    {
        WaitForClarifyingAnswersActivity.ReadAnswers(Input(("Answered", false))).Answered.Should().BeFalse();
        WaitForClarifyingAnswersActivity.ReadAnswers(Input(("Answered", "false"))).Answered.Should().BeFalse();
        WaitForClarifyingAnswersActivity.ReadAnswers(Input(("Answered", JsonBool(false)))).Answered.Should().BeFalse();
    }

    [Test]
    public void ReadAnswers_MissingKey_ReachesNotAnswered()
    {
        var (answered, answers) = WaitForClarifyingAnswersActivity.ReadAnswers(Input(("Answers", "n/a")));
        answered.Should().BeFalse();
        answers.Should().Be("n/a");
    }

    [Test]
    public void ReadAnswers_JsonElementAnswers_ReadThrough()
    {
        var input = Input(
            ("Answered", JsonBool(true)),
            ("Answers", (object)JsonDocument.Parse("\"the timeout is 30s\"").RootElement));
        var (answered, answers) = WaitForClarifyingAnswersActivity.ReadAnswers(input);
        answered.Should().BeTrue();
        answers.Should().Be("the timeout is 30s");
    }

    // ── Canonical bookmark name — suspend/resume parity + tenant folding ───

    [Test]
    public void AnswersBookmarkName_IsDeterministic_AndFoldsTenant()
    {
        var session = Guid.NewGuid();
        var tenantA = Guid.NewGuid().ToString();
        var tenantB = Guid.NewGuid().ToString();

        var a1 = WaitForClarifyingAnswersActivity.AnswersBookmarkName(tenantA, session);
        var a2 = WaitForClarifyingAnswersActivity.AnswersBookmarkName(tenantA, session);
        var b1 = WaitForClarifyingAnswersActivity.AnswersBookmarkName(tenantB, session);

        a1.Should().Be(a2, "the builder must be deterministic so suspend + resume names match byte-for-byte");
        a1.Should().StartWith("clarify-answers-");
        a1.Should().Contain(session.ToString());
        a1.Should().NotBe(b1,
            "folding the tenant into the name is the IDOR guard — a different tenant yields a " +
            "different bookmark so a cross-tenant resume can never resolve this gate");
    }

    [Test]
    public void AnswersBookmarkName_NullTenant_UsesStablePlaceholder()
    {
        var session = Guid.NewGuid();
        WaitForClarifyingAnswersActivity.AnswersBookmarkName(null, session)
            .Should().Be($"clarify-answers-none-{session}");
    }

    // ── ClarifyParsing.ParseQuestions — tolerant + fail-closed ─────────────

    [Test]
    public void ParseQuestions_BareJsonArray_Parses()
    {
        var qs = ClarifyParsing.ParseQuestions("Here you go: [\"What DB?\", \"What SLA?\"] done");
        qs.Should().Equal("What DB?", "What SLA?");
    }

    [Test]
    public void ParseQuestions_QuestionsObject_Parses()
    {
        var qs = ClarifyParsing.ParseQuestions("{\"questions\":[\"Q1\",\"Q2\",\"Q3\"]}");
        qs.Should().Equal("Q1", "Q2", "Q3");
    }

    [Test]
    public void ParseQuestions_ClarifyingQuestionsObject_Parses()
    {
        var qs = ClarifyParsing.ParseQuestions("{\"clarifyingQuestions\":[\"Only one\"]}");
        qs.Should().Equal("Only one");
    }

    [Test]
    public void ParseQuestions_Unparseable_IsEmpty_FailClosed()
    {
        ClarifyParsing.ParseQuestions("no json here").Should().BeEmpty();
        ClarifyParsing.ParseQuestions("").Should().BeEmpty();
        ClarifyParsing.ParseQuestions(null).Should().BeEmpty();
        ClarifyParsing.ParseQuestions("[]").Should().BeEmpty();
    }

    // ── ClarifyParsing.ParseClarification — required field + fail-closed ───

    [Test]
    public void ParseClarification_FullObject_Parses()
    {
        var result = ClarifyParsing.ParseClarification(
            "{\"clarifiedRequirement\":\"Use PostgreSQL 17\",\"remainingAmbiguities\":[\"none\"],\"resolved\":true}");
        result.Should().NotBeNull();
        result!.ClarifiedRequirement.Should().Be("Use PostgreSQL 17");
        result.RemainingAmbiguities.Should().Equal("none");
        result.Resolved.Should().BeTrue();
    }

    [Test]
    public void ParseClarification_MissingRequirement_IsNull_FailClosed()
    {
        ClarifyParsing.ParseClarification("{\"resolved\":true}").Should().BeNull();
        ClarifyParsing.ParseClarification("{\"clarifiedRequirement\":\"\"}").Should().BeNull();
        ClarifyParsing.ParseClarification("not json").Should().BeNull();
        ClarifyParsing.ParseClarification(null).Should().BeNull();
    }
}
