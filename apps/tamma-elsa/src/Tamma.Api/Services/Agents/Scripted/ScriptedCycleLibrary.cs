using Tamma.Core;
using Tamma.Core.Documents;

namespace Tamma.Api.Services.Agents.Scripted;

/// <summary>
/// The BUILT-IN script for the single-issue cycle (2026-08-13) — the canned
/// responses that let the ACTUAL AdlOrchestrator/SingleIssueCycle workflows
/// drive one seeded issue from selection to merge with no network LLM.
///
/// <para><b>Where the payloads come from.</b> Typed documents are NOT
/// hand-typed here: any call carrying a <c>documentType</c> falls back to that
/// type's own first VALID <see cref="DocumentExample"/> from
/// <see cref="DocumentTypeRegistry"/> — the same payloads the 39-x registry
/// drift tests self-check against each type's validator, so they pass the
/// 39-9 validation ring by construction. Explicit entries below exist only
/// where cross-step coherence or routing matters more than the example's
/// content (the review must APPROVE so the lifecycle routes to accept; the
/// plan's task ids must match the test-spec's task bindings — which the
/// shipped plan/test-spec examples already do: T-1/T-2 ↔ TC-1/TC-2).</para>
///
/// <para><b>Key syntax</b> (shared with the override file):
/// <c>{role}/{action}@{documentType}</c> — most specific; <c>@{documentType}</c>
/// — per-type default; <c>{role}/{action}</c> — free-text cells; <c>*</c> —
/// catch-all (override files only; the built-in library deliberately ships
/// none so an unscripted cell fails LOUD naming its key).</para>
/// </summary>
public static class ScriptedCycleLibrary
{
    /// <summary>
    /// The reviewer reply used for EVERY bare reviewer cell. Deliberately the
    /// LEGACY verdict shape, because it is the one shape BOTH consumers parse:
    /// TaskReviewWorkflow reads <c>verdict</c> directly, and the 39-7
    /// single-reviewer maps it through <c>Review.FromLegacyVerdictJson</c>
    /// onto a valid approving <c>Review</c> (decision=approve, summary from
    /// <c>comments</c>). Pinned by ScriptedCycleScriptValidityTests.
    /// </summary>
    public const string ApproveReviewVerdict =
        """{"verdict":"approve","comments":"Scripted review: no blocking issues found; the artifact satisfies its contract.","suggestedChanges":"","issues":[]}""";

    /// <summary>Deploy/rollback stage reply — DeploymentPipelineWorkflow's
    /// ExtractStageResult is fail-closed and requires an explicit
    /// <c>status:"success"</c>.</summary>
    public const string StageSuccess =
        """{"status":"success","detail":"scripted no-op stage execution"}""";

    /// <summary>PO context summary — ContextGatheringWorkflow's ExtractPO
    /// parses <c>{summary, links}</c> out of the reply.</summary>
    public const string PoSummary =
        """{"summary":"Scripted PO summary: implement the seeded issue exactly as titled; scope is a single small change with tests.","links":[]}""";

    private const string ContextScanFindings =
        "Scripted context scan: repository follows its documented conventions; no blockers, " +
        "no ambiguity; the change is small and self-contained.";

    /// <summary>
    /// The built-in (role/action[@documentType] → response text) map. Keys are
    /// produced by <see cref="ScriptedLlmResponder"/>'s normalizer (lowercase,
    /// trimmed).
    /// </summary>
    public static IReadOnlyDictionary<string, string> Responses { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // ── context gathering (free text; enableTools=true — the scripted
            //    reply ends the turn with no tool calls) ──
            ["developer/context-scan"] = ContextScanFindings,
            ["tester/context-scan"] = ContextScanFindings,
            ["security/context-scan"] = ContextScanFindings,
            ["devops/context-scan"] = ContextScanFindings,
            ["architect/context-scan"] = ContextScanFindings,
            ["product_owner/summarize-stakeholder"] = PoSummary,

            // ── review cells (task review + the 39-7 reviewer panel roster;
            //    one legacy-verdict approval serves both consumers) ──
            ["architect/plan-review"] = ApproveReviewVerdict,
            ["senior_developer/plan-review"] = ApproveReviewVerdict,
            ["security/plan-review-security"] = ApproveReviewVerdict,
            ["developer/review-feasibility"] = ApproveReviewVerdict,
            ["tester/review-testability"] = ApproveReviewVerdict,
            ["devops/review-operability"] = ApproveReviewVerdict,
            ["product_owner/review-scope"] = ApproveReviewVerdict,
            ["tech_writer/review-docs"] = ApproveReviewVerdict,
            ["ux_designer/review-design"] = ApproveReviewVerdict,

            // ── code review + guidance (CodeReviewWorkflow stores the text) ──
            ["senior_developer/code-review"] = ApproveReviewVerdict,
            ["senior_developer/mentor-feedback"] =
                "Scripted mentor feedback: the change is clean; keep tests colocated and prefer small commits.",

            // ── PR description (PullRequestWorkflow captures the body) ──
            ["tech_writer/summarize-changes"] =
                "Scripted PR description: implements the seeded issue via the reviewed plan; " +
                "tests ride the test-spec committed to this branch.",

            // ── deployment pipeline (fail-closed status parsing) ──
            ["devops/deploy"] = StageSuccess,
            ["devops/rollback"] = StageSuccess,

            // ── triage support (the orchestrator's NeedsTriage edge — covered
            //    so a triage dispatch cannot strand the loop; the E2E seeds a
            //    pre-labelled issue so the direct-select path is the one under
            //    test) ──
            ["product_owner/triage-intake"] = ApproveReviewVerdict,

            // ── @review default: the registry's FIRST valid Review example is
            //    a request-changes review (correct for the drift suite, wrong
            //    for an autonomous run — it would loop revise rounds), so the
            //    per-type default is overridden with a canonical APPROVE. The
            //    subject is placeholder data: every consumer (39-7's
            //    MapReviewerReply) overrides it with the caller's authoritative
            //    subject. ──
            ["@review"] =
                """{"subject":{"kind":"document","documentId":"0192a8b0-0000-7abc-8def-00000000e2e0","documentType":"plan"},"decision":"approve","summary":"Scripted review: approved with no blocking issues.","issues":[]}""",
        };

    /// <summary>
    /// The per-document-type fallback: the type's own first VALID example
    /// payload (self-checked against the type's validator by the registry
    /// drift tests). Returns null for an unknown type key or a type shipping
    /// no valid example (both impossible for registered 39-x types — the drift
    /// suite enforces ≥1 valid example per type — but this stays fail-soft so
    /// the responder can compose its own typed missing-cell error).
    /// </summary>
    public static string? DocumentExampleFor(string documentTypeKey)
    {
        if (string.IsNullOrWhiteSpace(documentTypeKey))
        {
            return null;
        }

        IDocumentType type;
        try
        {
            type = DocumentTypeRegistry.Resolve(documentTypeKey.Trim());
        }
        catch (TammaError)
        {
            return null;
        }

        return type.Examples.FirstOrDefault(e => e.IsValid)?.PayloadJson;
    }
}
