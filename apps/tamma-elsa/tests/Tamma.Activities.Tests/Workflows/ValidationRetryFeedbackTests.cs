using System.Reflection;
using Elsa.Expressions.Models;
using Elsa.Workflows;
using Elsa.Workflows.Memory;
using Elsa.Workflows.Runtime.Activities;
using FluentAssertions;
using NUnit.Framework;
using Tamma.ElsaServer.Workflows;
using Tamma.ElsaServer.Workflows.Helpers;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Regression tests for the validate→retry feedback bug: PlanGeneration,
/// TaskCreation, and TestCaseCreation used to pass validation errors to the
/// llm-call dispatch under a <c>validationErrors</c> key that NO template
/// declares — <c>PromptStoreService.Render</c> substitutes only declared
/// {{placeholders}}, so the feedback was silently dropped and every retry
/// re-prompted blind.
///
/// The fix merges the errors INTO a variable the target template actually
/// declares (Plan family → <c>contextFindings</c>; WriteTests →
/// <c>testTarget</c>), as a clearly-delimited block. These tests materialise
/// each workflow's dispatch Input delegate against a minimal expression
/// context (the TaxonomyDriftBuildTests / UpdateIssueStatusWorkflowTests
/// idiom) and assert:
///  - retry dispatch: error text present under the DECLARED key, verbatim;
///  - no dead <c>validationErrors</c> key remains;
///  - first attempt: variables unchanged from before the fix (minus the dead key).
/// </summary>
[TestFixture]
public class ValidationRetryFeedbackTests
{
    private const string SampleErrors = "Missing 'tasks' or 'steps'; Missing file map";

    // ====================================================================
    // ValidationFeedbackHelper — pure formatting
    // ====================================================================

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void AppendFeedback_NoErrors_ReturnsBaseUnchanged(string? errors)
    {
        ValidationFeedbackHelper.AppendFeedback("base context", errors)
            .Should().Be("base context",
                "first attempt (no validation errors) must leave the declared variable's value untouched");
    }

    [Test]
    public void AppendFeedback_NoErrors_NullBase_ReturnsEmptyString()
    {
        ValidationFeedbackHelper.AppendFeedback(null, null).Should().BeEmpty();
    }

    [Test]
    public void AppendFeedback_WithErrors_AppendsDelimitedBulletBlock()
    {
        var merged = ValidationFeedbackHelper.AppendFeedback("PO summary text", SampleErrors);

        merged.Should().Be(
            "PO summary text\n\n" +
            ValidationFeedbackHelper.FeedbackHeader + "\n" +
            "- Missing 'tasks' or 'steps'\n" +
            "- Missing file map");
    }

    [Test]
    public void AppendFeedback_EmptyBase_ReturnsBlockWithoutLeadingBlankLines()
    {
        var merged = ValidationFeedbackHelper.AppendFeedback(null, "Empty plan");

        merged.Should().Be(
            ValidationFeedbackHelper.FeedbackHeader + "\n- Empty plan",
            "when the carrier variable has no base content the block must not start with blank lines");
    }

    [Test]
    public void AppendFeedback_ValidatorErrors_FlowVerbatim()
    {
        // The validate step joins individual errors with "; "; the helper must unpack
        // each into its own bullet verbatim. (The PlanGeneration retry-loop cases were
        // retired in Story 39-14 — the lifecycle now owns validate → repair/revise; the
        // render-drop contract stays pinned on ValidationFeedbackHelper here.)
        const string errors = "Missing 'tasks' or 'steps'; Missing file map";
        var merged = ValidationFeedbackHelper.AppendFeedback("ctx", errors);

        merged.Should().Contain("- Missing 'tasks' or 'steps'",
            "validator error strings must flow through verbatim, one bullet each");
        merged.Should().Contain("- Missing file map");
    }

    // ====================================================================
    // Story 39-14/39-15 — the bespoke validate→retry loops of PlanGeneration,
    // TaskCreation, and TestCaseCreation are all RETIRED. Their carrier-merge
    // contract now lives inside the document-lifecycle binding (feedbackVariableName
    // → contextFindings / testTarget); the render-drop lesson stays pinned on
    // ValidationFeedbackHelper (above) which 39-6 D11 still consumes. The workflow
    // dispatch-materialisation sections (GenerateTasks/GenerateTests) were removed
    // with those nodes — the bindings are covered by TaskCreationWorkflowStructureTests
    // / TestCaseCreationWorkflowStructureTests.
    // ====================================================================
}
