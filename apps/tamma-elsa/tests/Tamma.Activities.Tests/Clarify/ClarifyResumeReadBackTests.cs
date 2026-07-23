using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Documents;
using Tamma.ElsaServer.Endpoints;

namespace Tamma.Activities.Tests.Clarify;

/// <summary>
/// Story 39-13 (D3) — retargeted from the legacy <c>WaitForClarifyingAnswersActivity</c>: the
/// clarify wait-for-answers now rides the generic <see cref="WaitForDocumentInputActivity"/>
/// input gate, and <c>ClarifyResumeEndpoint</c> is a thin adapter onto it. The
/// serialization-tolerance matrix (the #15/#437 lesson: never a bare <c>is true</c> on resume
/// input) targets <see cref="WaitForDocumentInputActivity.ReadInput"/>; the bookmark-parity
/// half asserts the adapter computes the SAME canonical input-bookmark name the gate suspends
/// on. The <c>ClarifyParsing</c> rows retired with the parser (39-13 D9).
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

    // ── ReadInput — Received coercion tolerant of serialization ──────────

    [Test]
    public void ReadInput_BoxedBoolTrue_ReachesReceived()
    {
        var (received, inputJson) = WaitForDocumentInputActivity.ReadInput(
            Input(("Received", true), ("InputJson", "use OAuth2")));
        received.Should().BeTrue();
        inputJson.Should().Be("use OAuth2");
    }

    [Test]
    public void ReadInput_StringTrue_ReachesReceived()
    {
        WaitForDocumentInputActivity.ReadInput(Input(("Received", "true"))).Received.Should().BeTrue();
        WaitForDocumentInputActivity.ReadInput(Input(("Received", "True"))).Received.Should().BeTrue();
    }

    [Test]
    public void ReadInput_JsonElementTrue_ReachesReceived()
    {
        WaitForDocumentInputActivity.ReadInput(Input(("Received", JsonBool(true)))).Received.Should().BeTrue();
    }

    [Test]
    public void ReadInput_FalseRepresentations_ReachNotReceived()
    {
        WaitForDocumentInputActivity.ReadInput(Input(("Received", false))).Received.Should().BeFalse();
        WaitForDocumentInputActivity.ReadInput(Input(("Received", "false"))).Received.Should().BeFalse();
        WaitForDocumentInputActivity.ReadInput(Input(("Received", JsonBool(false)))).Received.Should().BeFalse();
    }

    [Test]
    public void ReadInput_MissingKey_ReachesNotReceived()
    {
        var (received, inputJson) = WaitForDocumentInputActivity.ReadInput(Input(("InputJson", "n/a")));
        received.Should().BeFalse();
        inputJson.Should().Be("n/a");
    }

    [Test]
    public void ReadInput_JsonElementInput_ReadThrough()
    {
        var input = Input(
            ("Received", JsonBool(true)),
            ("InputJson", (object)JsonDocument.Parse("\"the timeout is 30s\"").RootElement));
        var (received, inputJson) = WaitForDocumentInputActivity.ReadInput(input);
        received.Should().BeTrue();
        inputJson.Should().Be("the timeout is 30s");
    }

    // ── Bookmark parity — the adapter resolves the generic input gate ──────

    [Test]
    public void Adapter_BookmarkName_MatchesTheGenericInputGate_AndFoldsTenant()
    {
        var session = Guid.NewGuid();
        var tenantA = Guid.NewGuid().ToString();
        var tenantB = Guid.NewGuid().ToString();

        var adapterName = ClarifyResumeEndpoint.BookmarkName(new(session, tenantA, "answers", null));
        var gateName = WaitForDocumentInputActivity.InputBookmarkName(tenantA, session);

        adapterName.Should().Be(gateName, "the adapter must compute the SAME name the gate suspends on");
        adapterName.Should().StartWith("document-input-");
        adapterName.Should().Contain(session.ToString());
        adapterName.Should().NotBe(WaitForDocumentInputActivity.InputBookmarkName(tenantB, session),
            "folding the tenant is the IDOR guard — a different tenant yields a different bookmark");
    }

    [Test]
    public void InputBookmarkName_NullTenant_UsesStablePlaceholder()
    {
        var session = Guid.NewGuid();
        WaitForDocumentInputActivity.InputBookmarkName(null, session)
            .Should().Be($"document-input-none-{session}");
    }
}
