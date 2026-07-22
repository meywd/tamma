using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.LlmCall.Tools;
using Tamma.Api.Services.Agents;
using Tamma.Core.Documents;

namespace Tamma.Api.Tests.Agents;

/// <summary>
/// Story 39-9 (AC8) — the deterministic, safe, golden-pinned repair message. Same
/// violations ⇒ byte-identical message; violations appear verbatim; only validator
/// output + fixed instruction text (never a provider error body — the composer takes
/// only <see cref="DocumentViolation"/>s, enforced at compile time); a secret-shaped
/// token in a violation message is redacted through the runner's redaction seam.
/// </summary>
[TestFixture]
public class RepairMessageComposerTests
{
    private static IReadOnlyList<DocumentViolation> TwoViolations() => new[]
    {
        new DocumentViolation("DANGLING_DEPENDS_ON", "Task 'T3' depends on undeclared 'T9'."),
        new DocumentViolation("MISSING_FIELD", "Field 'acceptanceCriteria' is required but was not present."),
    };

    [Test]
    public void Compose_IsDeterministic_ByteIdenticalAcrossCalls()
    {
        var first = RepairMessageComposer.Compose(TwoViolations());
        var second = RepairMessageComposer.Compose(TwoViolations());

        second.Should().Be(first, "same violations in ⇒ byte-identical message out");
    }

    [Test]
    public void Compose_GoldenTemplate_ForTwoViolations()
    {
        var message = RepairMessageComposer.Compose(TwoViolations());

        const string expected =
            "The document you produced did not pass validation. The following problems were found:\n" +
            "- [DANGLING_DEPENDS_ON] Task 'T3' depends on undeclared 'T9'.\n" +
            "- [MISSING_FIELD] Field 'acceptanceCriteria' is required but was not present.\n" +
            "\n" +
            "Fix every problem listed above and re-emit the COMPLETE corrected document. " +
            "Output only the corrected document — do not include explanations, apologies, or commentary.";

        message.Should().Be(expected, "the template is golden-pinned — any drift is a conscious edit");
    }

    [Test]
    public void Compose_EmitsEveryViolationVerbatim_InInputOrder()
    {
        var message = RepairMessageComposer.Compose(TwoViolations());

        message.Should().Contain("- [DANGLING_DEPENDS_ON] Task 'T3' depends on undeclared 'T9'.");
        message.Should().Contain("- [MISSING_FIELD] Field 'acceptanceCriteria' is required but was not present.");
        message.IndexOf("DANGLING_DEPENDS_ON", StringComparison.Ordinal)
            .Should().BeLessThan(message.IndexOf("MISSING_FIELD", StringComparison.Ordinal),
                "input order is preserved");
    }

    [Test]
    public void ComposeThenRedact_ScrubsSecretShapedTokenFromViolationMessage()
    {
        // A violation message can quote model output that embeds a secret-shaped token.
        // The runner appends the composed message through ToolOutputHelper.RedactSecrets (D9).
        var violations = new[]
        {
            new DocumentViolation(
                "BAD_VALUE",
                "The field 'apiKey' contained sk-abcdefghijklmnopqrstuvwxyz012345 which is invalid."),
        };

        var redacted = ToolOutputHelper.RedactSecrets(RepairMessageComposer.Compose(violations));

        redacted.Should().NotContain("sk-abcdefghijklmnopqrstuvwxyz012345");
        redacted.Should().Contain("[REDACTED]");
        redacted.Should().Contain("[BAD_VALUE]", "the violation code survives redaction");
    }
}
