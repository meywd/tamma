using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.Core;
using Tamma.Activities.TDD;

namespace Tamma.Activities.Tests.TDD;

/// <summary>
/// Fence-fragility regression tests: the TDD activity response parsers used to feed
/// the WHOLE LLM reply to <c>JsonSerializer</c> and threw on markdown-fenced
/// (<c>```json ... ```</c>) or prose-wrapped replies, turning perfectly valid output
/// into a parse failure. They now slice the embedded JSON object via the shared
/// <see cref="JsonSlice"/> helper (the same first-'{'-to-last-'}' idiom the
/// *Parsing.cs classes use) before deserializing. Pure garbage must still fail
/// exactly as before (Success=false / HasSuggestions=false — never fabricated data).
/// </summary>
[TestFixture]
public class ResponseParsingTests
{
    // ================================================================
    // JsonSlice — the shared helper
    // ================================================================

    [Test]
    public void JsonSlice_FencedJson_ReturnsObjectSlice()
    {
        JsonSlice.ExtractObject("```json\n{\"a\": 1}\n```").Should().Be("{\"a\": 1}");
    }

    [Test]
    public void JsonSlice_ProseWrappedJson_ReturnsObjectSlice()
    {
        JsonSlice.ExtractObject("Here you go: {\"a\": 1} — hope that helps!").Should().Be("{\"a\": 1}");
    }

    [Test]
    public void JsonSlice_NoBracesOrEmpty_ReturnsNull()
    {
        JsonSlice.ExtractObject("no json here").Should().BeNull();
        JsonSlice.ExtractObject("").Should().BeNull();
        JsonSlice.ExtractObject(null).Should().BeNull();
        JsonSlice.ExtractObject("} backwards {").Should().BeNull();
    }

    // ================================================================
    // WriteTestsActivity.ParseTestGenerationResponse
    // ================================================================

    private const string TestGenJson = """
    {"testCode": "describe('x', () => {});", "testFiles": ["src/x.test.ts"], "testCount": 2}
    """;

    [Test]
    public void ParseTestGeneration_FencedJson_Parses()
    {
        var result = WriteTestsActivity.ParseTestGenerationResponse(
            $"```json\n{TestGenJson}\n```", new List<string>());

        result.Success.Should().BeTrue();
        result.TestCode.Should().Contain("describe");
        result.TestFiles.Should().ContainSingle().Which.Should().Be("src/x.test.ts");
        result.TestCount.Should().Be(2);
    }

    [Test]
    public void ParseTestGeneration_ProseWrappedJson_Parses()
    {
        var result = WriteTestsActivity.ParseTestGenerationResponse(
            $"Here are the tests:\n{TestGenJson}\nLet me know if you need more.", new List<string>());

        result.Success.Should().BeTrue();
        result.TestCount.Should().Be(2);
    }

    [Test]
    public void ParseTestGeneration_BareJson_StillParses()
    {
        var result = WriteTestsActivity.ParseTestGenerationResponse(TestGenJson, new List<string>());

        result.Success.Should().BeTrue();
    }

    [Test]
    public void ParseTestGeneration_Garbage_StillFailsClosed()
    {
        var result = WriteTestsActivity.ParseTestGenerationResponse(
            "Sorry, I can't produce tests for that.", new List<string>());

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Failed to parse test generation response");
    }

    // ================================================================
    // WriteImplementationActivity.ParseImplementationResponse
    // ================================================================

    private const string ImplJson = """
    {"implementationCode": "export function f() { return 1; }", "implementationFiles": ["src/f.ts"]}
    """;

    [Test]
    public void ParseImplementation_FencedJson_Parses()
    {
        var result = WriteImplementationActivity.ParseImplementationResponse(
            $"```json\n{ImplJson}\n```");

        result.Success.Should().BeTrue();
        result.ImplementationCode.Should().Contain("export function f");
        result.ImplementationFiles.Should().ContainSingle().Which.Should().Be("src/f.ts");
    }

    [Test]
    public void ParseImplementation_ProseWrappedJson_Parses()
    {
        var result = WriteImplementationActivity.ParseImplementationResponse(
            $"Implementation below.\n{ImplJson}\nDone.");

        result.Success.Should().BeTrue();
    }

    [Test]
    public void ParseImplementation_Garbage_StillFailsClosed()
    {
        var result = WriteImplementationActivity.ParseImplementationResponse("not json");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Failed to parse implementation response");
    }

    // ================================================================
    // AnalyzeCodeActivity.ParseAnalysisResponse
    // ================================================================

    private const string AnalysisJson = """
    {
      "hasSuggestions": true,
      "confidence": 0.8,
      "suggestions": [
        {"description": "Extract helper", "category": "duplication", "confidence": 0.9, "filePath": "src/a.ts"}
      ]
    }
    """;

    [Test]
    public void ParseAnalysis_FencedJson_Parses()
    {
        var result = AnalyzeCodeActivity.ParseAnalysisResponse(
            $"```json\n{AnalysisJson}\n```", confidenceThreshold: 0.7);

        result.HasSuggestions.Should().BeTrue();
        result.Suggestions.Should().ContainSingle().Which.Description.Should().Be("Extract helper");
    }

    [Test]
    public void ParseAnalysis_ProseWrappedJson_Parses()
    {
        var result = AnalyzeCodeActivity.ParseAnalysisResponse(
            $"My analysis: {AnalysisJson} — end of analysis.", confidenceThreshold: 0.7);

        result.HasSuggestions.Should().BeTrue();
    }

    [Test]
    public void ParseAnalysis_Garbage_StillFailsClosed()
    {
        var result = AnalyzeCodeActivity.ParseAnalysisResponse("total garbage", confidenceThreshold: 0.7);

        result.HasSuggestions.Should().BeFalse();
        result.Confidence.Should().Be(0);
        result.Suggestions.Should().BeEmpty();
    }
}
