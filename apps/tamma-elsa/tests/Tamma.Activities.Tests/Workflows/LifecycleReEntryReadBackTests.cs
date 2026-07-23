using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Core.Documents;
using Tamma.Core.Documents.Resume;
using Tamma.ElsaServer.Workflows.Helpers;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Story 39-10 (AC4 matrix clause, D10) — the re-entry read-back must be tolerant of a
/// SERIALIZING runtime (the #15/#437 lesson), mirroring
/// <c>ClarifyResumeReadBackTests</c>: the position payload arrives as a boxed
/// <see cref="string"/> OR a <see cref="JsonElement"/>, and every boolean flag arrives
/// as a boxed bool / <c>"true"</c>/<c>"True"</c> / <see cref="JsonElement"/>, truthy AND
/// falsy. A missing payload fail-closes to a fresh Produce.
/// </summary>
[TestFixture]
public class LifecycleReEntryReadBackTests
{
    private const string Type = "decomposition";
    private static readonly Guid Doc = Guid.Parse("0192a8b0-1111-7abc-8def-000000000001");

    private static string PositionJson(LifecycleResumeStage stage) =>
        JsonSerializer.Serialize(new LifecycleResumePosition
        {
            DocumentTypeKey = Type,
            ResumeAt = stage,
            ExistingDocumentId = Doc,
            ExistingRevision = 0,
            Basis = "test",
        }, DocumentJson.Options);

    private static JsonElement JsonBool(bool value) => JsonDocument.Parse(value ? "true" : "false").RootElement;

    private static IDictionary<string, object> Input(params (string Key, object Value)[] entries)
    {
        var d = new Dictionary<string, object>();
        foreach (var (k, v) in entries) d[k] = v;
        return d;
    }

    // ── position payload as string AND JsonElement ─────────────────────

    [Test]
    public void ReadPosition_StringPayload_Parses()
    {
        var result = DocumentLifecycleHelper.ReadReEntryPosition(
            Input(("PositionJson", PositionJson(LifecycleResumeStage.Review))));
        result.Position.Should().NotBeNull();
        result.Stage.Should().Be(LifecycleResumeStage.Review);
        result.SkipProduce.Should().BeTrue();
        result.SkipReview.Should().BeFalse();
    }

    [Test]
    public void ReadPosition_JsonElementPayload_Parses()
    {
        var element = (object)JsonDocument.Parse(PositionJson(LifecycleResumeStage.Complete)).RootElement;
        var result = DocumentLifecycleHelper.ReadReEntryPosition(Input(("PositionJson", element)));
        result.Position.Should().NotBeNull();
        result.Stage.Should().Be(LifecycleResumeStage.Complete);
        result.ShortCircuitAccepted.Should().BeTrue();
    }

    [Test]
    public void ReadPosition_JsonElementStringPayload_Parses()
    {
        // A serializing runtime may hand the JSON back as a JSON *string* element.
        var asJsonString = (object)JsonDocument
            .Parse(JsonSerializer.Serialize(PositionJson(LifecycleResumeStage.Accept))).RootElement;
        var result = DocumentLifecycleHelper.ReadReEntryPosition(Input(("PositionJson", asJsonString)));
        result.Position.Should().NotBeNull();
        result.Stage.Should().Be(LifecycleResumeStage.Accept);
    }

    [Test]
    public void ReadPosition_MissingPayload_FailsClosedToFreshProduce()
    {
        var result = DocumentLifecycleHelper.ReadReEntryPosition(Input(("Other", "x")));
        result.Position.Should().BeNull();
        result.Stage.Should().Be(LifecycleResumeStage.Produce);
        result.SkipProduce.Should().BeFalse();
    }

    [Test]
    public void ReadPosition_GarbagePayload_FailsClosedToFreshProduce()
    {
        DocumentLifecycleHelper.ReadReEntryPosition(Input(("PositionJson", "not json")))
            .Stage.Should().Be(LifecycleResumeStage.Produce);
    }

    // ── boolean flags coerced tolerant (no PositionJson → explicit flags) ──

    [Test]
    public void ReadFlags_BoxedBoolTrue_Coerces()
    {
        var r = DocumentLifecycleHelper.ReadReEntryPosition(
            Input(("SkipProduce", true), ("SkipReview", true), ("ShortCircuit", true)));
        r.SkipProduce.Should().BeTrue();
        r.SkipReview.Should().BeTrue();
        r.ShortCircuitAccepted.Should().BeTrue();
    }

    [Test]
    public void ReadFlags_StringTrue_Coerces()
    {
        DocumentLifecycleHelper.ReadReEntryPosition(Input(("SkipProduce", "true"))).SkipProduce.Should().BeTrue();
        DocumentLifecycleHelper.ReadReEntryPosition(Input(("SkipProduce", "True"))).SkipProduce.Should().BeTrue();
    }

    [Test]
    public void ReadFlags_JsonElementTrue_Coerces()
    {
        DocumentLifecycleHelper.ReadReEntryPosition(Input(("SkipProduce", JsonBool(true))))
            .SkipProduce.Should().BeTrue();
    }

    [Test]
    public void ReadFlags_FalseRepresentations_Coerce()
    {
        DocumentLifecycleHelper.ReadReEntryPosition(Input(("SkipProduce", false))).SkipProduce.Should().BeFalse();
        DocumentLifecycleHelper.ReadReEntryPosition(Input(("SkipProduce", "false"))).SkipProduce.Should().BeFalse();
        DocumentLifecycleHelper.ReadReEntryPosition(Input(("SkipProduce", JsonBool(false)))).SkipProduce.Should().BeFalse();
    }

    [Test]
    public void ReadPosition_NullInput_IsFresh()
    {
        DocumentLifecycleHelper.ReadReEntryPosition(null).Stage.Should().Be(LifecycleResumeStage.Produce);
    }
}
