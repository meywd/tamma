using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Documents;

namespace Tamma.Activities.Tests.Documents;

/// <summary>
/// Story 39-13 (D3) — the generic input gate's resume-input read-back must be tolerant of a
/// SERIALIZING workflow runtime (the #15/#437 lesson): a bare <c>is true</c> would silently
/// mis-branch a serialized flag while returning 200. Full truthy/falsy matrix over boxed bool /
/// string / <see cref="JsonElement"/>, missing-key fail-closed, plus bookmark determinism +
/// tenant folding.
/// </summary>
[TestFixture]
public class DocumentInputReadBackTests
{
    private static JsonElement JsonBool(bool value) => JsonDocument.Parse(value ? "true" : "false").RootElement;

    private static IDictionary<string, object> Input(params (string Key, object Value)[] entries)
    {
        var dict = new Dictionary<string, object>();
        foreach (var (key, value) in entries) dict[key] = value;
        return dict;
    }

    [Test]
    public void ReadInput_TruthyRepresentations_ReachReceived()
    {
        WaitForDocumentInputActivity.ReadInput(Input(("Received", true))).Received.Should().BeTrue();
        WaitForDocumentInputActivity.ReadInput(Input(("Received", "true"))).Received.Should().BeTrue();
        WaitForDocumentInputActivity.ReadInput(Input(("Received", "True"))).Received.Should().BeTrue();
        WaitForDocumentInputActivity.ReadInput(Input(("Received", JsonBool(true)))).Received.Should().BeTrue();
    }

    [Test]
    public void ReadInput_FalsyRepresentations_ReachNotReceived()
    {
        WaitForDocumentInputActivity.ReadInput(Input(("Received", false))).Received.Should().BeFalse();
        WaitForDocumentInputActivity.ReadInput(Input(("Received", "false"))).Received.Should().BeFalse();
        WaitForDocumentInputActivity.ReadInput(Input(("Received", JsonBool(false)))).Received.Should().BeFalse();
    }

    [Test]
    public void ReadInput_MissingReceivedKey_FailClosedNotReceived()
    {
        var (received, inputJson) = WaitForDocumentInputActivity.ReadInput(Input(("InputJson", "hi")));
        received.Should().BeFalse("a missing Received flag must fail closed, never a false 'received'");
        inputJson.Should().Be("hi");
    }

    [Test]
    public void ReadInput_JsonElementInput_ReadThrough()
    {
        var (_, inputJson) = WaitForDocumentInputActivity.ReadInput(Input(
            ("Received", JsonBool(true)),
            ("InputJson", (object)JsonDocument.Parse("\"answer text\"").RootElement)));
        inputJson.Should().Be("answer text");
    }

    [Test]
    public void InputBookmarkName_IsDeterministic_AndFoldsTenant()
    {
        var session = Guid.NewGuid();
        var tenantA = Guid.NewGuid().ToString();
        var tenantB = Guid.NewGuid().ToString();

        var a1 = WaitForDocumentInputActivity.InputBookmarkName(tenantA, session);
        var a2 = WaitForDocumentInputActivity.InputBookmarkName(tenantA, session);
        a1.Should().Be(a2);
        a1.Should().StartWith("document-input-").And.Contain(session.ToString());
        a1.Should().NotBe(WaitForDocumentInputActivity.InputBookmarkName(tenantB, session));
    }

    [Test]
    public void InputBookmarkName_NullTenant_UsesStablePlaceholder()
    {
        var session = Guid.NewGuid();
        WaitForDocumentInputActivity.InputBookmarkName(null, session).Should().Be($"document-input-none-{session}");
    }
}
