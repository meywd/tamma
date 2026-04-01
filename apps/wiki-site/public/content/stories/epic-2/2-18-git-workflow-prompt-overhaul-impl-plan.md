---
title: "Story 2.18: Git Workflow Prompt Overhaul -- Implementation Plan"
sidebar:
  order: 20
---

## Overview

This plan addresses every weakness identified in the audit across 6 workflows and 6 activities. The work is organized into 7 phases, each independently testable and deployable.

---

## Phase 1: Model & Configuration Expansion

### Files to modify

| File | Change |
|------|--------|
| `Tamma.Activities/ADL/Models/AdlModels.cs` | Add new DTOs, enums, expand `ReviewFixItem`, expand `AdlConfig` |
| `Tamma.Core/Interfaces/IIntegrationService.cs` | Add 5 missing methods to `IGitHubIntegrationService` |

### New models in `AdlModels.cs`

```csharp
// ============================================
// Branch naming
// ============================================

public enum BranchType
{
    Feature,
    Bugfix,
    Chore,
    Docs,
    Refactor,
    Test,
    Adl // fallback
}

public class BranchNamingConfig
{
    /// <summary>Pattern with placeholders: {type}, {issue-number}, {issue-title}</summary>
    public string Pattern { get; set; } = "{type}/{issue-number}-{issue-title}";
    public int MaxLength { get; set; } = 80;
    public string ConflictStrategy { get; set; } = "suffix"; // suffix | timestamp | abort

    /// <summary>Map from issue label substring to BranchType</summary>
    public Dictionary<string, BranchType> LabelPrefixMap { get; set; } = new()
    {
        ["bug"] = BranchType.Bugfix,
        ["fix"] = BranchType.Bugfix,
        ["feat"] = BranchType.Feature,
        ["enhancement"] = BranchType.Feature,
        ["chore"] = BranchType.Chore,
        ["docs"] = BranchType.Docs,
        ["documentation"] = BranchType.Docs,
        ["refactor"] = BranchType.Refactor,
        ["test"] = BranchType.Test,
    };
}

// ============================================
// PR description
// ============================================

public class PrDescriptionContext
{
    public int IssueNumber { get; set; }
    public string IssueTitle { get; set; } = "";
    public List<string> IssueLabels { get; set; } = new();
    public string? PlanSummary { get; set; }
    public List<string> PlanSteps { get; set; } = new();
    public List<string> FilesChanged { get; set; } = new();
    public int LinesAdded { get; set; }
    public int LinesRemoved { get; set; }
    public int TestsRun { get; set; }
    public int TestsPassed { get; set; }
    public int TestsFailed { get; set; }
    public double? CoveragePercent { get; set; }
    public bool HasBreakingChanges { get; set; }
}

public class PrCreationConfig
{
    public bool DraftMode { get; set; } = true;
    public string MergeStrategy { get; set; } = "squash"; // squash | merge | rebase
    public bool ConventionalCommitTitle { get; set; } = true;
    public List<string> StaticLabels { get; set; } = new() { "tamma-auto" };
    public bool DeriveLabelsFromIssue { get; set; } = true;
}

// ============================================
// Review classification
// ============================================

public enum ReviewCommentCategory
{
    Bug,
    Style,
    Design,
    Security,
    Nitpick,
    Question,
    Praise
}

public enum ReviewCommentPriority
{
    Critical,  // security, logic bugs
    High,      // functional issues
    Medium,    // style, naming
    Low        // nitpick, praise
}

/// <summary>Extended review fix item with AI classification</summary>
public class ClassifiedReviewComment
{
    public string FilePath { get; set; } = "";
    public int? Line { get; set; }
    public string RawComment { get; set; } = "";
    public ReviewCommentCategory Category { get; set; }
    public ReviewCommentPriority Priority { get; set; }
    public bool IsActionable { get; set; }
    public string? SuggestedFix { get; set; }
    public string? ReviewerIntent { get; set; }
    public string? SurroundingCode { get; set; }
}

public class ClassifiedReviewAnalysis
{
    public int TotalComments { get; set; }
    public int ActionableCount { get; set; }
    public int FilteredCount { get; set; }
    public List<ClassifiedReviewComment> Comments { get; set; } = new();
    public string Summary { get; set; } = "";
    public Dictionary<string, int> CategoryCounts { get; set; } = new();
}

// ============================================
// CI error classification
// ============================================

public enum CiErrorCategory
{
    BuildError,
    TestFailure,
    LintError,
    TypeError,
    Timeout,
    Infrastructure
}

public class ClassifiedCiError
{
    public CiErrorCategory Category { get; set; }
    public string ErrorMessage { get; set; } = "";
    public string? FailingFile { get; set; }
    public string? FailingTest { get; set; }
    public string? StackTraceSnippet { get; set; }
    public string DebugContextMode { get; set; } = "RuntimeError";
}

// ============================================
// Merge configuration
// ============================================

public class MergeConfig
{
    public string Strategy { get; set; } = "squash"; // squash | merge | rebase
    public bool PreMergeConflictCheck { get; set; } = true;
    public bool PreMergeRebase { get; set; } = true;
    public string CommitMessageTemplate { get; set; }
        = "{strategy}({scope}): {title} (#{issue-number})\n\nCloses #{issue-number}\nPR #{pr-number}";
}
```

### Expand `AdlConfig`

```csharp
public class AdlConfig
{
    // ... existing fields ...

    public BranchNamingConfig BranchNaming { get; set; } = new();
    public PrCreationConfig PrCreation { get; set; } = new();
    public MergeConfig Merge { get; set; } = new();
}
```

### New methods on `IGitHubIntegrationService`

```csharp
/// <summary>Get diff stats for a pull request</summary>
Task<IntegrationResult<PrDiffStats>> GetPullRequestDiffStatsAsync(
    string repository, int pullRequestNumber);

/// <summary>Update PR (title, body, draft status)</summary>
Task<IntegrationResult<bool>> UpdatePullRequestAsync(
    string repository, int pullRequestNumber, UpdatePullRequestRequest update);

/// <summary>Check if PR is mergeable (no conflicts)</summary>
Task<IntegrationResult<MergeabilityStatus>> CheckMergeabilityAsync(
    string repository, int pullRequestNumber);

/// <summary>Merge with explicit strategy and commit message</summary>
Task<IntegrationResult<GitHubMergeResult>> MergeGitHubPullRequestAsync(
    string repository, int pullRequestNumber, string strategy, string? commitMessage);

/// <summary>Update branch (rebase or merge base into head)</summary>
Task<IntegrationResult<bool>> UpdateBranchAsync(
    string repository, int pullRequestNumber);
```

New supporting models:

```csharp
public class PrDiffStats
{
    public int FilesChanged { get; set; }
    public int Additions { get; set; }
    public int Deletions { get; set; }
    public List<string> ChangedFiles { get; set; } = new();
}

public class UpdatePullRequestRequest
{
    public string? Title { get; set; }
    public string? Body { get; set; }
    public bool? Draft { get; set; }
    public List<string>? Labels { get; set; }
}

public class MergeabilityStatus
{
    public bool Mergeable { get; set; }
    public bool HasConflicts { get; set; }
    public string? BehindBy { get; set; }
}
```

---

## Phase 2: Intelligent Branch Creation

### Files to modify

| File | Change |
|------|--------|
| `Tamma.Activities/ADL/CreateBranchActivity.cs` | Issue-type-aware naming, conflict detection, baseBranch support |
| `Tamma.ElsaServer/Workflows/BranchCreationWorkflow.cs` | Add `baseBranch` and `issueLabels` inputs |
| `Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs` | Pass `baseBranch` and `issueLabels` to branch-creation dispatch |

### Updated `CreateBranchActivity` logic

Replace the hardcoded naming with:

```csharp
protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
{
    var repository = Repository.Get(context);
    var issueNumber = IssueNumber.Get(context);
    var issueTitle = IssueTitle.Get(context) ?? "";
    var baseBranch = BaseBranch.Get(context) ?? "main";
    var issueLabelsRaw = IssueLabels.Get(context) ?? "";
    var issueLabels = string.IsNullOrEmpty(issueLabelsRaw)
        ? Array.Empty<string>()
        : issueLabelsRaw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    var config = new BranchNamingConfig(); // TODO: load from AdlConfig
    var branchType = ResolveBranchType(issueLabels, config.LabelPrefixMap);
    var branchName = GenerateBranchName(branchType, issueNumber, issueTitle, config);

    // Conflict detection
    branchName = await ResolveConflicts(repository, branchName, config.ConflictStrategy);

    try
    {
        var result = await _github!.CreateGitHubBranchAsync(repository, branchName);
        // ... (rest unchanged, but pass baseBranch if the API supports it)
    }
    // ...
}

private static BranchType ResolveBranchType(
    string[] labels, Dictionary<string, BranchType> map)
{
    foreach (var label in labels)
    {
        var lower = label.ToLowerInvariant();
        foreach (var (key, type) in map)
        {
            if (lower.Contains(key))
                return type;
        }
    }
    return BranchType.Adl;
}

private static string BranchTypePrefix(BranchType type) => type switch
{
    BranchType.Feature => "feat",
    BranchType.Bugfix => "fix",
    BranchType.Chore => "chore",
    BranchType.Docs => "docs",
    BranchType.Refactor => "refactor",
    BranchType.Test => "test",
    BranchType.Adl => "adl",
    _ => "adl"
};

private static string GenerateBranchName(
    BranchType type, int issueNumber, string title, BranchNamingConfig config)
{
    var sanitized = SanitizeBranchName(title);
    var name = config.Pattern
        .Replace("{type}", BranchTypePrefix(type))
        .Replace("{issue-number}", issueNumber.ToString())
        .Replace("{issue-title}", sanitized);

    if (name.Length > config.MaxLength)
        name = TruncateAtWordBoundary(name, config.MaxLength);

    return name;
}

private static string TruncateAtWordBoundary(string name, int maxLength)
{
    if (name.Length <= maxLength) return name;
    var truncated = name[..maxLength];
    var lastHyphen = truncated.LastIndexOf('-');
    if (lastHyphen > truncated.Length * 0.6)
        return truncated[..lastHyphen];
    return truncated.TrimEnd('-');
}

private async Task<string> ResolveConflicts(
    string repository, string branchName, string strategy)
{
    // Check if branch already exists by attempting to get it
    // If CreateGitHubBranchAsync returns failure with "already exists",
    // apply conflict strategy
    // For now, proactive check is preferred:
    try
    {
        var existing = await _github!.GetGitHubCommitsAsync(repository, branchName);
        if (existing.Success)
        {
            // Branch exists -- apply strategy
            return strategy switch
            {
                "timestamp" => $"{branchName}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}",
                "abort" => throw new InvalidOperationException(
                    $"Branch '{branchName}' already exists (strategy: abort)"),
                _ => await FindAvailableSuffix(repository, branchName) // "suffix" default
            };
        }
    }
    catch (InvalidOperationException) { throw; }
    catch
    {
        // Branch does not exist or API error -- proceed with original name
    }
    return branchName;
}

private async Task<string> FindAvailableSuffix(string repository, string baseName)
{
    for (int i = 2; i <= 20; i++)
    {
        var candidate = $"{baseName}-{i}";
        var check = await _github!.GetGitHubCommitsAsync(repository, candidate);
        if (!check.Success) return candidate;
    }
    return $"{baseName}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}"; // fallback
}
```

New inputs to add to the activity:

```csharp
[Input(Description = "Base branch to create from (default: main)")]
public Input<string> BaseBranch { get; set; } = new("main");

[Input(Description = "Comma-separated issue labels for branch type derivation")]
public Input<string> IssueLabels { get; set; } = new("");
```

### Workflow changes

`BranchCreationWorkflow.cs` -- add `baseBranch` and `issueLabels` inputs to the `CreateBranchActivity` construction.

`SingleIssueCycleWorkflow.cs` -- pass `baseBranch` and issue labels to the branch-creation dispatch:

```csharp
["baseBranch"] = baseBranch.Get(ctx),
["issueLabels"] = string.Join(",", /* extract labels from issueJson or issueResult */)
```

---

## Phase 3: Structured PR Description

### Files to modify

| File | Change |
|------|--------|
| `Tamma.Activities/ADL/CreatePullRequestActivity.cs` | New `BuildStructuredPrBody`, conventional-commit title, draft mode, diff stats, test results |
| `Tamma.ElsaServer/Workflows/PullRequestWorkflow.cs` | Add `testResultsJson`, `diffStatsJson`, `issueLabels` inputs |
| `Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs` | Pass test results and labels to PR workflow |

### New activity inputs

```csharp
[Input(Description = "Test results JSON from CI pipeline")]
public Input<string?> TestResultsJson { get; set; } = default!;

[Input(Description = "Diff stats JSON")]
public Input<string?> DiffStatsJson { get; set; } = default!;

[Input(Description = "Comma-separated issue labels")]
public Input<string?> IssueLabels { get; set; } = default!;

[Input(Description = "Create as draft PR")]
public Input<bool> DraftMode { get; set; } = new(true);
```

### PR Body Template

Replace `BuildPrBody` with `BuildStructuredPrBody`:

```csharp
private static string BuildStructuredPrBody(PrDescriptionContext ctx)
{
    var sb = new StringBuilder();

    // Summary
    sb.AppendLine("## Summary");
    sb.AppendLine();
    sb.AppendLine($"Closes #{ctx.IssueNumber}");
    sb.AppendLine();
    if (!string.IsNullOrEmpty(ctx.PlanSummary))
    {
        sb.AppendLine(ctx.PlanSummary);
        sb.AppendLine();
    }

    // Changes Made
    sb.AppendLine("## Changes Made");
    sb.AppendLine();
    if (ctx.FilesChanged.Count > 0)
    {
        sb.AppendLine($"**{ctx.FilesChanged.Count}** files changed " +
            $"(**+{ctx.LinesAdded}** / **-{ctx.LinesRemoved}**)");
        sb.AppendLine();
        foreach (var file in ctx.FilesChanged.Take(30))
            sb.AppendLine($"- `{file}`");
        if (ctx.FilesChanged.Count > 30)
            sb.AppendLine($"- ... and {ctx.FilesChanged.Count - 30} more");
        sb.AppendLine();
    }

    // Implementation Plan
    if (ctx.PlanSteps.Count > 0)
    {
        sb.AppendLine("## Implementation Plan");
        sb.AppendLine();
        for (int i = 0; i < ctx.PlanSteps.Count; i++)
            sb.AppendLine($"{i + 1}. {ctx.PlanSteps[i]}");
        sb.AppendLine();
    }

    // Test Results
    sb.AppendLine("## Test Results");
    sb.AppendLine();
    if (ctx.TestsRun > 0)
    {
        sb.AppendLine($"| Metric | Value |");
        sb.AppendLine($"|--------|-------|");
        sb.AppendLine($"| Tests run | {ctx.TestsRun} |");
        sb.AppendLine($"| Passed | {ctx.TestsPassed} |");
        sb.AppendLine($"| Failed | {ctx.TestsFailed} |");
        if (ctx.CoveragePercent.HasValue)
            sb.AppendLine($"| Coverage | {ctx.CoveragePercent:F1}% |");
        sb.AppendLine();
    }
    else
    {
        sb.AppendLine("_Test results pending CI pipeline._");
        sb.AppendLine();
    }

    // Breaking Changes
    sb.AppendLine("## Breaking Changes");
    sb.AppendLine();
    sb.AppendLine(ctx.HasBreakingChanges
        ? "> **WARNING**: This PR contains breaking changes. Review carefully."
        : "None.");
    sb.AppendLine();

    // Checklist
    sb.AppendLine("## Pre-Merge Checklist");
    sb.AppendLine();
    sb.AppendLine("- [ ] Code follows project conventions");
    sb.AppendLine("- [ ] Tests pass and coverage is acceptable");
    sb.AppendLine("- [ ] No security issues introduced");
    sb.AppendLine("- [ ] Breaking changes documented (if any)");
    sb.AppendLine();

    // Footer
    sb.AppendLine("---");
    sb.AppendLine("_Generated by [Tamma ADL](https://github.com/meywd/tamma)_");

    return sb.ToString();
}
```

### Conventional-Commit Title

```csharp
private static string BuildConventionalTitle(
    int issueNumber, string issueTitle, string[] issueLabels)
{
    var type = "feat"; // default
    foreach (var label in issueLabels.Select(l => l.ToLowerInvariant()))
    {
        if (label.Contains("bug") || label.Contains("fix")) { type = "fix"; break; }
        if (label.Contains("chore")) { type = "chore"; break; }
        if (label.Contains("docs")) { type = "docs"; break; }
        if (label.Contains("refactor")) { type = "refactor"; break; }
        if (label.Contains("test")) { type = "test"; break; }
    }

    // Extract scope from title if it looks like "component: description"
    var scope = "";
    var title = issueTitle;
    var colonIdx = issueTitle.IndexOf(':');
    if (colonIdx > 0 && colonIdx < 30)
    {
        scope = $"({issueTitle[..colonIdx].Trim().ToLowerInvariant()})";
        title = issueTitle[(colonIdx + 1)..].Trim();
    }

    // Lowercase first char of title for conventional-commit style
    if (title.Length > 0)
        title = char.ToLowerInvariant(title[0]) + title[1..];

    var result = $"{type}{scope}: {title} (#{issueNumber})";
    return result.Length > 250 ? result[..247] + "..." : result;
}
```

### `PrDescriptionContext` population in the activity

```csharp
protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
{
    // ... get inputs ...

    // Parse plan JSON to extract summary and steps
    AdlPlan? plan = null;
    if (!string.IsNullOrEmpty(planJson))
    {
        try { plan = JsonSerializer.Deserialize<AdlPlan>(planJson); }
        catch { _logger?.LogWarning("Failed to parse plan JSON for PR body"); }
    }

    // Parse diff stats if available
    PrDiffStats? diffStats = null;
    var diffStatsJson = DiffStatsJson.Get(context);
    if (!string.IsNullOrEmpty(diffStatsJson))
    {
        try { diffStats = JsonSerializer.Deserialize<PrDiffStats>(diffStatsJson); }
        catch { /* ignore */ }
    }

    // Parse test results if available
    // ... similar pattern ...

    var descCtx = new PrDescriptionContext
    {
        IssueNumber = issueNumber,
        IssueTitle = issueTitle,
        IssueLabels = issueLabelsArr.ToList(),
        PlanSummary = plan?.Summary,
        PlanSteps = plan?.Steps ?? new(),
        FilesChanged = diffStats?.ChangedFiles ?? plan?.FilesToModify ?? new(),
        LinesAdded = diffStats?.Additions ?? 0,
        LinesRemoved = diffStats?.Deletions ?? 0,
        // Test results populated from testResultsJson
    };

    var title = BuildConventionalTitle(issueNumber, issueTitle, issueLabelsArr);
    var body = BuildStructuredPrBody(descCtx);

    var request = new CreatePullRequestRequest
    {
        Title = title,
        Body = body,
        Head = branchName,
        Base = baseBranch,
        Labels = DeriveLabels(issueLabelsArr)
    };

    // ... create PR (as draft if configured) ...
}
```

---

## Phase 4: AI-Powered Review Comment Classification

### Files to modify

| File | Change |
|------|--------|
| `Tamma.Activities/ADL/AnalyzeReviewActivity.cs` | AI-powered classification, filtering |
| `Tamma.ElsaServer/Workflows/ReviewFixWorkflow.cs` | Pass classified analysis to fix prompt |

### New activity: classification via LLM dispatch

The `AnalyzeReviewActivity` will dispatch to the `llm-call` workflow for classification. However, since the activity cannot dispatch workflows (it is a leaf activity), we restructure the `ReviewFixWorkflow` to add a classification step before the fix-generation step.

Alternative (simpler): Use `CallLlmInlineActivity` directly in the review analysis activity.

Chosen approach: Keep `AnalyzeReviewActivity` as the GitHub API fetch, then add a **new activity** `ClassifyReviewCommentsActivity` that calls the LLM for classification.

### New file: `Tamma.Activities/ADL/ClassifyReviewCommentsActivity.cs`

```csharp
[Activity(
    "Tamma.ADL",
    "Classify Review Comments",
    "Use AI to classify review comments by category, priority, and actionability",
    Kind = ActivityKind.Task
)]
[FlowNode("Done", "Error")]
public class ClassifyReviewCommentsActivity : Activity
{
    [Input(Description = "Raw review comments JSON from AnalyzeReviewActivity")]
    public Input<string> RawCommentsJson { get; set; } = default!;

    [Output(Description = "Classified review analysis JSON")]
    public Output<string?> ClassifiedAnalysisJson { get; set; } = default!;

    [Output(Description = "Number of actionable comments")]
    public Output<int> ActionableCount { get; set; } = default!;

    // ... constructor with ILogger, IAIProvider or via LLM dispatch ...
}
```

### Classification Prompt Template

```
You are a code review analyst. Classify each review comment into exactly one category and assign a priority.

Categories:
- bug: The reviewer identified a logic error, incorrect behavior, or missing error handling
- security: The reviewer identified a security vulnerability, credential leak, or injection risk
- design: The reviewer suggests a different architecture, abstraction, or API design
- style: The reviewer suggests naming changes, formatting, or code organization improvements
- nitpick: Minor preference that does not affect correctness or readability
- question: The reviewer is asking a question, not requesting a change
- praise: The reviewer is expressing approval or positive feedback

Priorities:
- critical: security issues, data loss risks, logic bugs that cause incorrect output
- high: functional issues that affect behavior, missing error handling, race conditions
- medium: style issues, naming improvements, code organization
- low: nitpicks, questions, praise

For each comment, also determine:
- is_actionable: true if the comment requests a code change; false for questions, praise, or acknowledgments
- suggested_fix: A brief description of the fix needed (1-2 sentences). Leave empty for non-actionable.
- reviewer_intent: What the reviewer actually wants to happen (1 sentence).

Input comments:
{comments_json}

Respond in JSON format:
{
  "classifications": [
    {
      "index": 0,
      "category": "bug",
      "priority": "high",
      "is_actionable": true,
      "suggested_fix": "Add null check before accessing user.name",
      "reviewer_intent": "Prevent NullReferenceException when user is not found"
    }
  ]
}
```

### Updated `ReviewFixWorkflow` flow

```
Analyze (fetch comments from GitHub)
  -> Classify (LLM call to classify comments)
  -> HasActionable?
    YES -> GenerateFixes (LLM call with classified + contextualized prompt)
      -> ApplyFixes (actually apply, not stub)
      -> VerifyFixes (type-check, lint -- new activity)
      -> UpdateCodeIndex
      -> OutputSuccess
    NO  -> OutputSuccess (skip fix loop)
```

---

## Phase 5: Contextualized Fix-Generation Prompt

### Files to modify

| File | Change |
|------|--------|
| `Tamma.ElsaServer/Workflows/ReviewFixWorkflow.cs` | Restructure flow, add classify + verify steps |
| `Tamma.Activities/ADL/ApplyReviewFixesActivity.cs` | Wire to actual file operations (not stub) |

### Fix-Generation Prompt Template

Replace the current bare prompt:

```
You are an expert code reviewer fix implementer. You will receive a list of classified review comments with their surrounding code context. Apply the requested fixes precisely.

Rules:
1. Only modify code that the reviewer specifically asked to change.
2. Do NOT refactor unrelated code.
3. For each fix, produce a git-compatible diff or file edit.
4. If a comment is marked as not actionable, skip it entirely.
5. Prioritize critical and high-priority fixes first.
6. Each fix should be atomic -- one logical change per fix.

Review comments to address (ordered by priority):

{classified_comments}

For each actionable comment:
- File: {file_path}
- Line: {line_number}
- Category: {category} (Priority: {priority})
- Reviewer said: "{raw_comment}"
- Reviewer intent: "{reviewer_intent}"
- Suggested fix: "{suggested_fix}"
- Surrounding code (lines {start_line}-{end_line}):
```{language}
{surrounding_code}
```

Apply all actionable fixes. For each fix applied, output:
1. The file path
2. A short commit message fragment (e.g., "add null check for user lookup")
3. The replacement code

Format as JSON:
{
  "fixes": [
    {
      "file": "src/auth/handler.ts",
      "commit_fragment": "add null check for user lookup",
      "original_code": "const name = user.name;",
      "replacement_code": "const name = user?.name ?? 'unknown';",
      "addresses_comment_index": 0
    }
  ],
  "skipped": [
    {
      "comment_index": 3,
      "reason": "Non-actionable praise comment"
    }
  ]
}
```

### Surrounding code retrieval

The prompt needs surrounding code context. This requires reading the file content around the commented line. Two options:

1. **Fetch from GitHub API**: Use `GetGitHubFileChangesAsync` or a new raw-content endpoint
2. **Use code index**: The KB API already indexes repository files

Recommendation: Add a lightweight code-fetch step using the GitHub API's file-content endpoint. Add to `IGitHubIntegrationService`:

```csharp
Task<IntegrationResult<string>> GetFileContentAsync(
    string repository, string branch, string path, int? startLine, int? endLine);
```

This is called in the `ClassifyReviewCommentsActivity` to enrich each comment with surrounding code before sending to the LLM.

---

## Phase 6: Merge Strategy & Conflict Resolution

### Files to modify

| File | Change |
|------|--------|
| `Tamma.Activities/ADL/MergePullRequestActivity.cs` | Strategy selection, pre-merge check, rebase, commit message |
| `Tamma.ElsaServer/Workflows/MergeWorkflow.cs` | Add pre-merge conflict check step, pass strategy |
| `Tamma.ElsaServer/Workflows/SingleIssueCycleWorkflow.cs` | Reset `ciRetryCount` on review-fix re-entry |

### Updated `MergeWorkflow` flow

```
CheckMergeability
  -> Mergeable?
    YES -> MergePR (with strategy and commit message)
    NO  -> HasConflicts?
      YES -> UpdateBranch (rebase/merge base)
        -> Re-check mergeability
          -> Mergeable? YES -> MergePR / NO -> OutputError
      NO  -> OutputError (other blocker)
```

### New activities needed

**`CheckMergeabilityActivity.cs`**:

```csharp
[Activity("Tamma.ADL", "Check Mergeability",
    "Check if PR can be merged without conflicts")]
[FlowNode("Mergeable", "Conflicts", "Error")]
public class CheckMergeabilityActivity : Activity
{
    [Input] public Input<string> Repository { get; set; } = default!;
    [Input] public Input<int> PrNumber { get; set; } = default!;
    [Output] public Output<bool> IsMergeable { get; set; } = default!;
    [Output] public Output<bool> HasConflicts { get; set; } = default!;

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var result = await _github!.CheckMergeabilityAsync(
            Repository.Get(context), PrNumber.Get(context));

        if (!result.Success)
        {
            await context.CompleteActivityWithOutcomesAsync("Error");
            return;
        }

        IsMergeable.Set(context, result.Data!.Mergeable);
        HasConflicts.Set(context, result.Data.HasConflicts);

        var outcome = result.Data.Mergeable ? "Mergeable"
            : result.Data.HasConflicts ? "Conflicts"
            : "Error";
        await context.CompleteActivityWithOutcomesAsync(outcome);
    }
}
```

**`UpdateBranchActivity.cs`**:

```csharp
[Activity("Tamma.ADL", "Update Branch",
    "Rebase or merge base branch into feature branch")]
[FlowNode("Updated", "Error")]
public class UpdateBranchActivity : Activity
{
    [Input] public Input<string> Repository { get; set; } = default!;
    [Input] public Input<int> PrNumber { get; set; } = default!;
    [Output] public Output<bool> Updated { get; set; } = default!;

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var result = await _github!.UpdateBranchAsync(
            Repository.Get(context), PrNumber.Get(context));

        Updated.Set(context, result.Success);
        var outcome = result.Success ? "Updated" : "Error";
        await context.CompleteActivityWithOutcomesAsync(outcome);
    }
}
```

### Updated `MergePullRequestActivity`

Add new inputs:

```csharp
[Input(Description = "Merge strategy: squash, merge, or rebase")]
public Input<string> MergeStrategy { get; set; } = new("squash");

[Input(Description = "Custom merge commit message")]
public Input<string?> CommitMessage { get; set; } = default!;
```

Use the new overloaded `MergeGitHubPullRequestAsync` with strategy and commit message.

### Merge commit message template

```csharp
private static string BuildMergeCommitMessage(
    string strategy, int issueNumber, int prNumber, string issueTitle)
{
    var type = strategy == "squash" ? "feat" : "merge";
    return $"{type}: {issueTitle} (#{issueNumber})\n\n" +
           $"Closes #{issueNumber}\n" +
           $"PR #{prNumber}\n\n" +
           $"Co-authored-by: Tamma ADL <tamma-bot@users.noreply.github.com>";
}
```

### Fix: ciRetryCount reset

In `SingleIssueCycleWorkflow.cs`, after the `reviewFixCheck` returns with `hasComments = true` and before looping back to `dispatchCiRetry`, insert a `SetVariable` that resets `ciRetryCount` to 0:

```csharp
var resetCiRetryCount = new SetVariable
{
    Id = "ResetCiRetryCount",
    Name = "Reset CI Retry Count",
    Variable = ciRetryCount,
    Value = new Input<object?>(_ => (object)0)
};
resetCiRetryCount.SetDisplayText("Reset CI Retry Count");
```

Wire it into the flowchart:
```
hasReviewComments == True -> resetCiRetryCount -> dispatchCiRetry
```

---

## Phase 7: CI Error Categorization

### Files to modify

| File | Change |
|------|--------|
| `Tamma.ElsaServer/Workflows/CiWithDebugRetryWorkflow.cs` | Categorize error before dispatching to debugging |

### New activity: `ClassifyCiErrorActivity.cs`

```csharp
[Activity("Tamma.ADL", "Classify CI Error",
    "Categorize CI failure into build/test/lint/type/timeout/infrastructure")]
[FlowNode("Done", "Error")]
public class ClassifyCiErrorActivity : Activity
{
    [Input(Description = "Raw error output from CI pipeline")]
    public Input<string> ErrorOutput { get; set; } = default!;

    [Output(Description = "Classified error JSON")]
    public Output<string?> ClassifiedErrorJson { get; set; } = default!;

    [Output(Description = "Debug context mode for debugging workflow")]
    public Output<string> DebugContextMode { get; set; } = default!;

    protected override async ValueTask ExecuteAsync(ActivityExecutionContext context)
    {
        var errorOutput = ErrorOutput.Get(context);

        var classified = ClassifyError(errorOutput);

        ClassifiedErrorJson.Set(context, JsonSerializer.Serialize(classified));
        DebugContextMode.Set(context, classified.DebugContextMode);

        await context.CompleteActivityWithOutcomesAsync("Done");
    }

    private static ClassifiedCiError ClassifyError(string errorOutput)
    {
        var lower = errorOutput.ToLowerInvariant();
        var classified = new ClassifiedCiError { ErrorMessage = errorOutput };

        // Pattern matching for error categorization
        if (ContainsAny(lower, "tsc", "ts2", "type error", "cannot find name",
            "property does not exist", "is not assignable"))
        {
            classified.Category = CiErrorCategory.TypeError;
            classified.DebugContextMode = "TypeError";
        }
        else if (ContainsAny(lower, "eslint", "prettier", "lint",
            "no-unused-vars", "indent", "semicolon"))
        {
            classified.Category = CiErrorCategory.LintError;
            classified.DebugContextMode = "LintError";
        }
        else if (ContainsAny(lower, "esbuild", "compilation", "syntax error",
            "unexpected token", "module not found", "cannot resolve"))
        {
            classified.Category = CiErrorCategory.BuildError;
            classified.DebugContextMode = "BuildError";
        }
        else if (ContainsAny(lower, "timeout", "timed out", "deadline exceeded",
            "econnrefused", "enotfound", "dns"))
        {
            classified.Category = CiErrorCategory.Timeout;
            classified.DebugContextMode = "Infrastructure";
        }
        else if (ContainsAny(lower, "rate limit", "quota", "disk space",
            "out of memory", "oom", "runner", "container"))
        {
            classified.Category = CiErrorCategory.Infrastructure;
            classified.DebugContextMode = "Infrastructure";
        }
        else if (ContainsAny(lower, "assert", "expect", "test failed",
            "vitest", "jest", "describe", "it("))
        {
            classified.Category = CiErrorCategory.TestFailure;
            classified.DebugContextMode = "TestFailure";
        }
        else
        {
            classified.Category = CiErrorCategory.TestFailure; // default
            classified.DebugContextMode = "RuntimeError";
        }

        // Extract failing file from common patterns
        classified.FailingFile = ExtractFailingFile(errorOutput);
        classified.FailingTest = ExtractFailingTest(errorOutput);
        classified.StackTraceSnippet = ExtractStackTrace(errorOutput, maxLines: 10);

        return classified;
    }

    private static bool ContainsAny(string text, params string[] patterns)
        => patterns.Any(p => text.Contains(p));

    private static string? ExtractFailingFile(string output)
    {
        // Match patterns like "src/foo/bar.ts(12,5):" or "Error in src/foo/bar.ts"
        var match = Regex.Match(output, @"((?:src|packages|apps)/[^\s:()]+\.(?:ts|js|cs|py))");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? ExtractFailingTest(string output)
    {
        // Match patterns like "FAIL src/foo.test.ts > describe > test name"
        var match = Regex.Match(output, @"FAIL\s+\S+\s+>\s+(.+)$", RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string? ExtractStackTrace(string output, int maxLines)
    {
        var lines = output.Split('\n');
        var stackStart = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith("at ") ||
                lines[i].TrimStart().StartsWith("Error:"))
            {
                stackStart = i;
                break;
            }
        }
        if (stackStart < 0) return null;
        return string.Join("\n", lines.Skip(stackStart).Take(maxLines));
    }
}
```

### Updated `CiWithDebugRetryWorkflow` flow

Insert `ClassifyCiErrorActivity` between the failed-tests decision and the debugging dispatch:

```
testsPassed == False -> ciRetryGuard
  -> retries remaining -> incrementCiRetry -> classifyCiError -> dispatchCiDebugging (with classified context)
```

Update `dispatchCiDebugging` inputs to use classified output:

```csharp
["debugContextMode"] = classifiedDebugMode.Get(ctx), // instead of hardcoded "RuntimeError"
["errorOutput"] = classifiedErrorJson.Get(ctx),       // structured, not raw string
["failingFile"] = classifiedFailingFile.Get(ctx),
["failingTest"] = classifiedFailingTest.Get(ctx),
```

---

## Updated Workflow Flow Diagrams

### Branch Creation (Phase 2)

```
CreateBranch(with type-aware naming + conflict resolution)
  -> SetSuccess
  -> OutputSuccess
  -> OutputBranchName
```

No structural workflow change -- all logic is internal to the activity.

### PR Creation (Phase 3)

```
CreatePR(structured body, conventional title, draft mode)
  -> OutputSuccess
  -> OutputPrNumber
  -> OutputPrUrl
```

If draft mode is enabled, a future step in the parent workflow marks the PR ready after CI passes. This is handled by adding `MarkPrReadyActivity` after CI passes in `SingleIssueCycleWorkflow`.

### Review Fix (Phase 4+5)

```
AnalyzeReview (fetch comments from GitHub)
  -> ClassifyComments (LLM call for classification + context enrichment)
  -> HasActionable?
    YES -> GenerateFixes (LLM call with contextualized prompt)
      -> ApplyFixes (actually apply changes, not stub)
      -> VerifyFixes (run type-check/lint via testing-pipeline)
      -> UpdateCodeIndex
      -> OutputSuccess
    NO  -> OutputSuccess
```

### Merge (Phase 6)

```
CheckMergeability
  -> Mergeable?
    YES -> MergePR(strategy, commit message)
      -> CloseIssue
      -> DeleteBranch
      -> OutputSuccess
    NO  -> HasConflicts?
      YES -> UpdateBranch(rebase)
        -> CheckMergeability (retry)
          -> Mergeable? YES -> MergePR / NO -> OutputError
      NO  -> OutputError
```

### CI Debug Retry (Phase 7)

```
TestingPipeline -> TestsPassed?
  YES -> FinishPass
  NO  -> CiRetryGuard (<3)?
    NO  -> FinishFail
    YES -> IncrementRetry
      -> ClassifyCiError (NEW: categorize error)
      -> DispatchDebugging (with classified context)
      -> Loop to TestingPipeline
```

### SingleIssueCycle (Phase 6 fix)

```
... -> ReviewFixCheck -> HasComments?
  YES -> ResetCiRetryCount (NEW) -> DispatchCiRetry -> ...
  NO  -> MergeApproval -> ...
```

---

## New Activities Summary

| Activity | File | Phase |
|----------|------|-------|
| `ClassifyReviewCommentsActivity` | `Tamma.Activities/ADL/ClassifyReviewCommentsActivity.cs` | 4 |
| `ClassifyCiErrorActivity` | `Tamma.Activities/ADL/ClassifyCiErrorActivity.cs` | 7 |
| `CheckMergeabilityActivity` | `Tamma.Activities/ADL/CheckMergeabilityActivity.cs` | 6 |
| `UpdateBranchActivity` | `Tamma.Activities/ADL/UpdateBranchActivity.cs` | 6 |
| `MarkPrReadyActivity` | `Tamma.Activities/ADL/MarkPrReadyActivity.cs` | 3 |

---

## Files Modified Summary

| Phase | Files |
|-------|-------|
| 1 | `AdlModels.cs`, `IIntegrationService.cs` |
| 2 | `CreateBranchActivity.cs`, `BranchCreationWorkflow.cs`, `SingleIssueCycleWorkflow.cs` |
| 3 | `CreatePullRequestActivity.cs`, `PullRequestWorkflow.cs`, `SingleIssueCycleWorkflow.cs` |
| 4 | `AnalyzeReviewActivity.cs`, `ReviewFixWorkflow.cs`, NEW: `ClassifyReviewCommentsActivity.cs` |
| 5 | `ReviewFixWorkflow.cs`, `ApplyReviewFixesActivity.cs` |
| 6 | `MergePullRequestActivity.cs`, `MergeWorkflow.cs`, `SingleIssueCycleWorkflow.cs`, NEW: `CheckMergeabilityActivity.cs`, NEW: `UpdateBranchActivity.cs` |
| 7 | `CiWithDebugRetryWorkflow.cs`, NEW: `ClassifyCiErrorActivity.cs` |

---

## Risks & Mitigations

| Risk | Mitigation |
|------|------------|
| LLM classification prompt adds latency (5-15s per review cycle) | Keep prompt concise; classification results are cached for the session |
| New GitHub API methods may not exist on all platforms | Use `IntegrationResult.Fail()` gracefully; fall back to current behavior |
| Branch conflict detection via `GetGitHubCommitsAsync` is indirect | Add a dedicated `BranchExistsAsync` method if API supports it |
| `ApplyReviewFixesActivity` is currently a stub | Phase 5 must fully implement this; block merge on incomplete implementation |
| ciRetryCount reset changes existing behavior | Documented as bug fix; existing behavior is acknowledged as broken |

---

## Testing Checklist

- [ ] Branch naming: 7 label-to-prefix mappings produce correct prefixes
- [ ] Branch naming: conflict resolution with suffix strategy
- [ ] Branch naming: conflict resolution with timestamp strategy
- [ ] Branch naming: conflict resolution with abort strategy
- [ ] Branch naming: truncation at word boundary
- [ ] Branch naming: empty title, unicode title, very long title
- [ ] PR body: structured output with all sections populated
- [ ] PR body: structured output with missing test results (pending)
- [ ] PR body: structured output with missing plan (minimal body)
- [ ] PR title: conventional-commit format from issue labels
- [ ] PR title: scope extraction from issue title
- [ ] Review classification: bug comment classified as `bug/high`
- [ ] Review classification: "nice work!" classified as `praise/low`, filtered
- [ ] Review classification: security comment classified as `security/critical`
- [ ] Review classification: question classified as `question/low`, filtered
- [ ] Fix prompt: includes surrounding code context
- [ ] Fix prompt: ordered by priority (critical first)
- [ ] Merge: squash strategy passes correct API parameter
- [ ] Merge: conflict detected, rebase attempted, merge succeeds
- [ ] Merge: conflict detected, rebase fails, error output
- [ ] Merge: commit message includes issue number and PR title
- [ ] CI: build error correctly categorized as `BuildError`
- [ ] CI: test failure correctly categorized as `TestFailure`
- [ ] CI: timeout correctly categorized as `Timeout`
- [ ] CI: lint error correctly categorized as `LintError`
- [ ] CI: classified debugContextMode passed to debugging workflow
- [ ] ciRetryCount resets to 0 on review-fix re-entry
