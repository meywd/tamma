using System.Text.Json;
using FluentAssertions;
using NUnit.Framework;
using Tamma.Activities.ADL;
using Tamma.ElsaServer.Workflows.Helpers;

namespace Tamma.Activities.Tests.Workflows;

/// <summary>
/// Completeness audit 2026-06-22 (<c>TriageItemCycle.md</c>) — pure-function coverage
/// for the cycle build-out helpers. These are the levers behind the cycle's fail-closed
/// guarantees:
/// <list type="bullet">
///   <item><description>#1 — <see cref="TriageItemCycleHelper.IsDecisionApplicable"/> is
///     the apply gate. A failed PO call / <c>llm-failed</c> / <c>unparsed</c> /
///     <c>skipped</c> / empty decision is NOT applicable (no labelling off garbage).</description></item>
///   <item><description>#7 — labels validated against the canonical vocab; comment
///     rendered deterministically from the parsed decision (AC5 markdown table).</description></item>
///   <item><description>#5 — per-item outcome serialization.</description></item>
/// </list>
/// </summary>
[TestFixture]
public class TriageItemCycleHelperTests
{
    // ================================================================
    // #1 — decision-OK gate (the headline fail-closed guard)
    // ================================================================

    [Test]
    public void IsDecisionApplicable_OkStatusAndCallSucceeded_IsApplicable()
    {
        var ok = TriageItemCycleHelper.ParseDecision(
            """{"status":"ok","priority":"high","type":"bug","labels":["bug"],"comment":"x"}""");
        TriageItemCycleHelper.IsDecisionApplicable(true, ok).Should().BeTrue();
    }

    [Test]
    public void IsDecisionApplicable_CallFailed_IsNotApplicable_EvenIfStatusOk()
    {
        // A faulted PO dispatch leaves callSucceeded == false. Even if some stale/empty
        // decision JSON parses to "ok", the absence of a successful call must block apply.
        var ok = TriageItemCycleHelper.ParseDecision("""{"status":"ok","type":"bug"}""");
        TriageItemCycleHelper.IsDecisionApplicable(false, ok).Should().BeFalse(
            "a faulted PO sub-workflow (no callSucceeded) must NOT lead to labelling");
    }

    [TestCase("llm-failed")]
    [TestCase("unparsed")]
    [TestCase("skipped")]
    [TestCase("")]
    [TestCase("anything-else")]
    public void IsDecisionApplicable_NonOkStatus_IsNotApplicable(string status)
    {
        var d = TriageItemCycleHelper.ParseDecision($$"""{"status":"{{status}}","type":"bug"}""");
        TriageItemCycleHelper.IsDecisionApplicable(true, d).Should().BeFalse(
            $"status '{status}' is not a usable PO decision — apply must be skipped");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    [TestCase("not json")]
    [TestCase("[1,2,3]")]
    public void IsDecisionApplicable_MissingOrUnparseableJson_IsNotApplicable(string? json)
    {
        TriageItemCycleHelper.IsDecisionApplicable(true, json).Should().BeFalse(
            "a missing/unparseable decision must never be applied");
    }

    [Test]
    public void IsDecisionApplicable_StringOverload_ReadsStatusFromJson()
    {
        TriageItemCycleHelper.IsDecisionApplicable(true, """{"status":"ok","type":"bug"}""")
            .Should().BeTrue();
        TriageItemCycleHelper.IsDecisionApplicable(true, """{"status":"llm-failed"}""")
            .Should().BeFalse();
    }

    // ================================================================
    // #7 — label validation against the canonical vocab
    // ================================================================

    [Test]
    public void ValidateLabels_KeepsCanonical_DropsUnknown()
    {
        var kept = TriageItemCycleHelper.ValidateLabels(
            new[] { "bug", "priority-high", "made-up-label", "tamma-auto", "rm -rf" },
            out var dropped);

        kept.Should().BeEquivalentTo(new[] { "bug", "priority-high", "tamma-auto" });
        dropped.Should().BeEquivalentTo(new[] { "made-up-label", "rm -rf" });
    }

    [Test]
    public void ValidateLabels_NullOrEmpty_ReturnsEmpty_NoThrow()
    {
        TriageItemCycleHelper.ValidateLabels(null, out var dropped).Should().BeEmpty();
        dropped.Should().BeEmpty();
    }

    [Test]
    public void ValidateLabels_DeduplicatesCaseInsensitively_PreservesOrder()
    {
        var kept = TriageItemCycleHelper.ValidateLabels(
            new[] { "bug", "BUG", "feature", "bug" }, out _);
        kept.Should().ContainInOrder("bug", "feature");
        kept.Should().HaveCount(2);
    }

    // ================================================================
    // #7 — deterministic AC5 comment render from the parsed decision
    // ================================================================

    [Test]
    public void RenderComment_RendersMarkdownTable_FromParsedFields_NotRawProse()
    {
        var d = TriageItemCycleHelper.ParseDecision(
            """{"status":"ok","priority":"high","type":"bug","complexity":"simple","automation":"tamma-auto","comment":"NPE at startup"}""");

        var md = TriageItemCycleHelper.RenderComment(d);

        md.Should().Contain("| Field | Value |");
        md.Should().Contain("| Type | bug |");
        md.Should().Contain("| Priority | high |");
        md.Should().Contain("| Complexity | simple |");
        md.Should().Contain("| Automation | tamma-auto |");
        // the PO rationale is preserved as notes, below the canonical table
        md.Should().Contain("NPE at startup");
    }

    [Test]
    public void RenderComment_MissingFields_UsesSafeDefaults_NeverBlankCells()
    {
        var d = TriageItemCycleHelper.ParseDecision("""{"status":"ok"}""");
        var md = TriageItemCycleHelper.RenderComment(d);

        md.Should().Contain($"| Type | {TriagePoDecisionHelper.DefaultType} |");
        md.Should().Contain($"| Priority | {TriagePoDecisionHelper.DefaultPriority} |");
        md.Should().Contain($"| Automation | {TriagePoDecisionHelper.DefaultAutomation} |");
    }

    // ================================================================
    // itemKey derivation
    // ================================================================

    [Test]
    public void DeriveItemKey_Issue_UsesRepoAndNumber()
    {
        TriageItemCycleHelper.DeriveItemKey("owner/repo", """{"number":42,"title":"x"}""")
            .Should().Be("owner/repo#42");
    }

    [Test]
    public void DeriveItemKey_AlertNoNumber_UsesSourceAndTitle()
    {
        TriageItemCycleHelper.DeriveItemKey("owner/repo", """{"source":"dependabot","title":"CVE-2025-1"}""")
            .Should().Be("owner/repo:dependabot:CVE-2025-1");
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("not json")]
    public void DeriveItemKey_MissingOrUnparseable_IsStable_NoThrow(string? json)
    {
        TriageItemCycleHelper.DeriveItemKey("owner/repo", json).Should().Be("owner/repo:unknown");
    }

    [Test]
    public void ReadItemSource_ReadsSource_ThenType_ElseEmpty()
    {
        TriageItemCycleHelper.ReadItemSource("""{"source":"codeql"}""").Should().Be("codeql");
        TriageItemCycleHelper.ReadItemSource("""{"type":"issue"}""").Should().Be("issue");
        TriageItemCycleHelper.ReadItemSource("{}").Should().Be("");
        TriageItemCycleHelper.ReadItemSource(null).Should().Be("");
    }

    // ================================================================
    // #5 — per-item outcome serialization
    // ================================================================

    [Test]
    public void BuildItemResult_Triaged_HasNoError()
    {
        var json = TriageItemCycleHelper.BuildItemResult("owner/repo#1", TriageCycleEvents.OutcomeTriaged, "ok", null);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("itemKey").GetString().Should().Be("owner/repo#1");
        root.GetProperty("outcome").GetString().Should().Be("triaged");
        root.GetProperty("decisionStatus").GetString().Should().Be("ok");
        root.TryGetProperty("error", out _).Should().BeFalse("a triaged item carries no error");
    }

    [Test]
    public void BuildItemResult_Failed_CarriesError()
    {
        var json = TriageItemCycleHelper.BuildItemResult(
            "owner/repo#2", TriageCycleEvents.OutcomeFailed, "llm-failed", "decisionUnusable:llm-failed");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("outcome").GetString().Should().Be("failed");
        root.GetProperty("error").GetString().Should().Contain("decisionUnusable");
    }
}
