using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.ElsaServer.Workflows;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Hardening 2026-07-03 (follow-up to #15) — per-site coverage that the dispatched-result
/// boolean reads flagged by the adversarial review now tolerate a serialized
/// <see cref="JsonElement"/> flag (the switch-expression <c>_ =&gt; false</c> arm used to
/// drop it to <c>false</c>, silently mis-branching a merge/assessment/tdd/review success).
///
/// <para>These exercise the two reads that already expose a pure boundary —
/// <see cref="AssessmentWorkflow.ReadSuccessFlag"/> and <see cref="TddWorkflow.ExtractPassed"/>.
/// The JsonElement-true case fails before the read-back fix and passes after; boxed-bool
/// and string behaviour is unchanged (the coercion is fail-closed on unknown shapes). The
/// remaining inline sibling sites (MergeApproval / ReviewFix <c>ExtractGenerateSuccess</c>,
/// WaitForCIResults <c>BuildPassed</c>) delegate to the same shared
/// <see cref="Tamma.Activities.ResumeInput.AsBool"/> covered by ResumeInputTests.</para>
/// </summary>
[TestFixture]
public class ResumeReadTolerantSiblingsTests
{
    private static JsonElement JsonBool(bool value) => JsonDocument.Parse(value ? "true" : "false").RootElement;

    private static Dictionary<string, object> Result(string key, object value)
        => new() { [key] = value };

    // ---------------------------------------------------------------------
    // AssessmentWorkflow.ReadSuccessFlag — dispatched `success` flag
    // ---------------------------------------------------------------------

    [Test]
    public void ReadSuccessFlag_BoxedBool_ReadsThrough()
    {
        AssessmentWorkflow.ReadSuccessFlag(Result("success", true)).Should().BeTrue();
        AssessmentWorkflow.ReadSuccessFlag(Result("success", false)).Should().BeFalse();
    }

    [Test]
    public void ReadSuccessFlag_String_IsTolerant()
    {
        AssessmentWorkflow.ReadSuccessFlag(Result("success", "true")).Should().BeTrue();
        AssessmentWorkflow.ReadSuccessFlag(Result("success", "false")).Should().BeFalse();
    }

    [Test]
    public void ReadSuccessFlag_JsonElement_IsTolerant()
    {
        // Fails before the read-back fix (the `_ => false` arm dropped the JsonElement).
        AssessmentWorkflow.ReadSuccessFlag(Result("success", JsonBool(true))).Should().BeTrue();
        AssessmentWorkflow.ReadSuccessFlag(Result("success", JsonBool(false))).Should().BeFalse();
    }

    [Test]
    public void ReadSuccessFlag_MissingOrNull_IsFalse()
    {
        AssessmentWorkflow.ReadSuccessFlag(Result("other", true)).Should().BeFalse();
        AssessmentWorkflow.ReadSuccessFlag(null).Should().BeFalse();
    }

    // ---------------------------------------------------------------------
    // TddWorkflow.ExtractPassed — dispatched testing-pipeline `passed` flag
    // ---------------------------------------------------------------------

    [Test]
    public void ExtractPassed_BoxedBool_ReadsThrough()
    {
        TddWorkflow.ExtractPassed(Result("passed", true)).Should().BeTrue();
        TddWorkflow.ExtractPassed(Result("passed", false)).Should().BeFalse();
    }

    [Test]
    public void ExtractPassed_String_IsTolerant()
    {
        TddWorkflow.ExtractPassed(Result("passed", "true")).Should().BeTrue();
        TddWorkflow.ExtractPassed(Result("passed", "false")).Should().BeFalse();
    }

    [Test]
    public void ExtractPassed_JsonElement_IsTolerant()
    {
        // Fails before the read-back fix (the boxed-bool/string `if`s dropped the JsonElement).
        TddWorkflow.ExtractPassed(Result("passed", JsonBool(true))).Should().BeTrue();
        TddWorkflow.ExtractPassed(Result("passed", JsonBool(false))).Should().BeFalse();
    }

    [Test]
    public void ExtractPassed_MissingOrNull_IsFalse()
    {
        TddWorkflow.ExtractPassed(Result("other", true)).Should().BeFalse();
        TddWorkflow.ExtractPassed(null).Should().BeFalse();
    }
}
