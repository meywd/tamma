---
title: "Story 2-17: Workflow Prompt Engineering Overhaul"
sidebar:
  order: 20
---

## Summary

All Elsa workflows that send prompts to LLMs suffer from generic, context-poor prompts that produce mediocre results. This story upgrades every LLM-facing prompt across Code Review, Context Gathering, Plan Generation, Assessment, Blocker Diagnosis, and Issue Selection workflows to be project-aware, convention-respecting, and architecturally grounded.

## Problem Statement

An audit of every activity that constructs or sends prompts to LLMs reveals the following systemic weaknesses:

### 1. Plan Generation (`PlanGenerationWorkflow` / `BuildPlanPrompt`)

**Current prompt (verbatim):**
```
Generate a detailed implementation plan for the following GitHub issue:

**Title:** {safeTitle}
**Description:** {safeBody}

**Context:** {safeContext}
**Previous Feedback:** {safeFeedback}

Respond with a JSON object containing: summary, steps (array),
filesToModify (array), filesToCreate (array), testStrategy, estimatedComplexity.
```

**Problems:**
- No reference to the project's architecture (`docs/architecture.md`), tech stack, or CLAUDE.md conventions.
- Does not instruct the LLM to check for existing patterns, interfaces, or similar implementations in the codebase.
- Does not ask for dependency analysis -- which existing modules are affected.
- Does not ask for risk assessment or breaking-change analysis.
- Output schema is too loose: no typing guidance for `estimatedComplexity`, no acceptance-criteria mapping.
- No instruction to follow naming conventions (kebab-case files, `I`-prefix interfaces, etc.).

### 2. Code Review (`ClaudeAnalysisActivity.GetUserPrompt` / `DeliverGuidanceActivity`)

**Current code review prompt (verbatim from `ClaudeAnalysisActivity`):**
```
Please review this code:

```
{content}
```

Provide your review in the following JSON format:
{
    "overall_quality": "Good|Acceptable|NeedsWork",
    "score": 0-100,
    "issues": [...],
    "positives": [...],
    "learning_opportunities": [...]
}
```

**Problems:**
- No reference to the project's CLAUDE.md, lint rules, or coding conventions.
- No mention of TypeScript strict mode requirements, import order, naming patterns.
- No DCB event sourcing awareness: does not check if the code emits events for audit trail.
- No security-first validation: does not check for credential leaks, input sanitization, or TLS usage.
- Does not reference existing test patterns or coverage requirements.
- `DeliverGuidanceActivity.GenerateGuidanceForComment` uses hardcoded keyword matching instead of LLM analysis -- produces generic boilerplate guidance like "Add null validation before using this value."

### 3. Context Gathering (`ContextGatheringWorkflow` / `ContextGatheringActivity` / `FetchSimilarPatternsActivity`)

**Problems:**
- `FetchSimilarPatternsActivity.DiscoverPatternsAsync` is entirely simulated -- returns hardcoded fake patterns like "Controller Pattern" in `src/Controllers/ExampleController.cs` regardless of the actual project.
- `ContextGatheringActivity.GatherFileContents` returns simulated content: `"// Content of {filePath}\n// (In production, actual file content would be here)"`.
- `ContextGatheringActivity.GatherProjectStructure` returns hardcoded fake structure.
- `ContextGatheringActivity.GatherSimilarPatterns` returns three hardcoded fake patterns.
- No CLAUDE.md or convention-file detection -- the context sent to LLMs never includes the project's style guide.
- No relevance scoring beyond purpose-based priority in `AssembleContextActivity`.
- `ApplyBudgetActivity` trims by priority correctly but has no semantic relevance scoring (a 10-line utility file gets the same treatment as a 500-line core module).

### 4. Assessment (`AssessmentWorkflow` / `GenerateQuestionsActivity` / `AnalyzeResponseActivity`)

**Problems:**
- `GenerateQuestionsActivity.BuildQuestions` generates completely static, non-contextual questions. The `storyContext` parameter is received but never used in question generation.
- Questions like "In your own words, describe what this story requires you to build" are generic. They should reference specific acceptance criteria, specific files to modify, and specific patterns to follow from the project.
- `AnalyzeResponseActivity.PerformAnalysis` uses a naive heuristic (response length + keyword counting) instead of LLM-based analysis. A response of 200+ chars per question gets `confidence=0.8` regardless of accuracy.
- The `storeContextResult` in `AssessmentWorkflow` does not actually capture the context-gathering output: `$"Assessment context for story {storyId} gathered via ContextGathering workflow"` -- a placeholder string.

### 5. Issue Selection (`IssueSelectionWorkflow` / `SelectIssueActivity`)

**Problems:**
- Selects `FirstOrDefault` unassigned issue with no consideration of:
  - Issue complexity (no labels or body analysis)
  - Dependency ordering (issue A may depend on issue B)
  - Team capacity or current workload
  - Priority labels or milestones
  - Estimated effort vs. available time window
- No LLM-assisted triage for ambiguous issues.

### 6. Blocker Diagnosis (`BlockerDiagnosisWorkflow` / `BuildDiagnosisPrompt`)

**Current diagnosis prompt (from `BlockerDiagnosisWorkflow.BuildDiagnosisPrompt`):**
```
Diagnose what is blocking this junior developer (skill level {skillLevel}/5).

Git Activity: {git.RecentCommitCount} recent commits, ...
CI Status: Build={ci.BuildStatus}, ...
...

Classify into one of: ConceptualMisunderstanding, TechnicalKnowledgeGap, ...

Return JSON with: blocker_type, confidence (0-1), root_cause, evidence[], recommended_approach
```

**Problems:**
- No project context: the LLM does not know what the project is, what tech stack it uses, or what the developer is trying to build.
- No previous resolution history: does not tell the LLM what hints/guidance were already given.
- Hint/Guidance/Assistance LLM prompts (in `BuildHintLevel`, `BuildGuidanceLevel`, `BuildAssistanceLevel`) are extremely terse, e.g.: `"Provide Socratic hints for: {rootCauseHypothesis}. Blocker type: {blockerType}. Use guiding questions, not direct answers."` -- no project context, no code context, no relevant files.

### 7. Debug Diagnosis (`AIDiagnosisActivity.BuildDiagnosisPrompt`)

**Problems:**
- System prompt is generic: `"You are an expert debugging specialist."` -- no project context.
- Does not reference project architecture, naming conventions, or common error patterns.
- No instruction to consider the project's error handling patterns (`TammaError`, `createProviderError`).

## Acceptance Criteria

1. **Plan Generation** prompt includes: repository architecture summary, tech stack, naming conventions, existing similar implementations, dependency graph of affected modules, risk assessment instruction, and structured output schema with field-level type constraints.

2. **Code Review** prompt includes: CLAUDE.md conventions (naming, imports, strict mode), event sourcing requirements, security checklist (credential handling, input validation, TLS), test coverage targets. `DeliverGuidanceActivity` delegates to LLM for guidance generation instead of keyword matching.

3. **Context Gathering** activities are wired to real APIs (not simulated). A new `DetectProjectConventionsActivity` scans for CLAUDE.md, .eslintrc, tsconfig.json, and similar files to inject convention context into all downstream prompts.

4. **Assessment** questions are generated by the LLM using story-specific context (acceptance criteria, relevant files, architecture patterns). `AnalyzeResponseActivity` delegates to the LLM for semantic analysis instead of heuristic word counting.

5. **Issue Selection** considers issue complexity (label analysis), dependency ordering (mentions of other issues), and priority. Optionally uses LLM triage for ambiguous issues.

6. **Blocker Diagnosis** prompts include: what the developer was working on (story context), what the project is (architecture summary), what has already been tried (resolution history), and relevant code/file context. Progressive resolution prompts (Hint/Guidance/Assistance) include relevant code snippets and project patterns.

7. **All prompts** follow a consistent template structure:
   - Role/persona definition with project-specific context
   - Task description with clear constraints
   - Input data with type annotations
   - Output schema with field-level validation rules
   - Examples where ambiguity exists

8. Existing tests continue to pass. New tests cover prompt construction for each workflow.

## Technical Design

### New Activities Required

| Activity | Purpose | Location |
|----------|---------|----------|
| `DetectProjectConventionsActivity` | Scan repo for CLAUDE.md, .eslintrc, tsconfig.json, package.json | `Tamma.Activities/Context/` |
| `FetchArchitectureSummaryActivity` | Extract key patterns from docs/architecture.md or README.md | `Tamma.Activities/Context/` |
| `AnalyzeIssueDependenciesActivity` | Parse issue body/comments for dependency references (#123) | `Tamma.Activities/ADL/` |
| `ScoreIssueComplexityActivity` | Score issue complexity from labels, body length, file mentions | `Tamma.Activities/ADL/` |

### Modified Activities

| Activity | Change |
|----------|--------|
| `PlanGenerationWorkflow.BuildPlanPrompt` | Complete rewrite with architecture-aware prompt |
| `ClaudeAnalysisActivity.GetSystemPrompt` | Add project conventions section per analysis type |
| `ClaudeAnalysisActivity.GetUserPrompt` | Add convention references for CodeReview type |
| `DeliverGuidanceActivity.GenerateGuidanceForComment` | Replace keyword matching with LLM call via DispatchWorkflow |
| `GenerateQuestionsActivity.BuildQuestions` | Replace static questions with LLM-generated context-specific questions |
| `AnalyzeResponseActivity.PerformAnalysis` | Replace heuristic with LLM-based semantic analysis |
| `SelectIssueActivity.ExecuteAsync` | Add complexity scoring and dependency ordering |
| `BlockerDiagnosisWorkflow.BuildDiagnosisPrompt` | Add story context, architecture context, resolution history |
| `BlockerDiagnosisWorkflow.BuildHintLevel` | Enrich hint prompt with relevant code and patterns |
| `BlockerDiagnosisWorkflow.BuildGuidanceLevel` | Enrich guidance prompt with step-by-step context |
| `BlockerDiagnosisWorkflow.BuildAssistanceLevel` | Enrich assistance prompt with working code examples from the project |
| `FetchSimilarPatternsActivity.DiscoverPatternsAsync` | Wire to real code search (GitHub API or local AST) |
| `ContextGatheringActivity` | Add convention detection step; wire simulated methods to real APIs |
| `AIDiagnosisActivity.BuildDiagnosisPrompt` | Add project architecture and error pattern context |
| `ResolveLlmPromptActivity` | Add role-specific convention context injection |

### Prompt Template Registry

Create a `PromptTemplateRegistry` to centralize all prompt templates. Each template is a string with named placeholders. This eliminates scattered string concatenation across activities and enables:
- A/B testing of prompt variants
- Version tracking of prompts
- Centralized prompt review

Location: `Tamma.Activities/Prompts/PromptTemplateRegistry.cs`

## Story Points: 13

## Dependencies

- Story 7-1F (Context Gathering) -- must be functional
- Story 7-1B (LLM Call sub-workflow) -- must be functional
- GitHub API integration for real file content and code search

## Risks

- Prompt engineering is iterative -- first versions will need tuning based on output quality.
- Larger prompts increase token costs. Budget trimming in `ApplyBudgetActivity` may need adjustment.
- Convention detection requires file system or API access to scan the target repository.
