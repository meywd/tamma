---
title: "Story 2-17: Implementation Plan -- Workflow Prompt Engineering Overhaul"
sidebar:
  order: 20
---

## Phase 1: Infrastructure (PromptTemplateRegistry + Convention Detection)

### 1.1 Create `PromptTemplateRegistry`

**File:** `apps/tamma-elsa/src/Tamma.Activities/Prompts/PromptTemplateRegistry.cs`

A static registry of versioned prompt templates. Each template uses `{placeholder}` syntax. Activities call `PromptTemplateRegistry.Get("plan-generation", placeholders)` instead of inline string building.

```csharp
namespace Tamma.Activities.Prompts;

public static class PromptTemplateRegistry
{
    private static readonly Dictionary<string, string> Templates = new()
    {
        // Registered per-workflow below
    };

    public static string Get(string templateKey, Dictionary<string, string> placeholders)
    {
        if (!Templates.TryGetValue(templateKey, out var template))
            throw new KeyNotFoundException($"Prompt template '{templateKey}' not found");

        var result = template;
        foreach (var (key, value) in placeholders)
            result = result.Replace($"{{{key}}}", value ?? "");

        return result;
    }

    public static void Register(string key, string template)
        => Templates[key] = template;
}
```

### 1.2 Create `DetectProjectConventionsActivity`

**File:** `apps/tamma-elsa/src/Tamma.Activities/Context/DetectProjectConventionsActivity.cs`

Scans the target repository for convention files and returns a structured summary. Uses GitHub API to check for file existence and fetch content.

**Convention files to detect:**
- `CLAUDE.md` -- primary convention source
- `BEFORE_YOU_CODE.md` -- mandatory process guide
- `.eslintrc.*` / `eslint.config.*` -- lint rules
- `tsconfig.json` / `tsconfig.*.json` -- TypeScript configuration
- `package.json` -- dependencies, scripts, engine constraints
- `.prettierrc*` -- formatting rules
- `pnpm-workspace.yaml` -- monorepo structure
- `.editorconfig` -- editor settings

**Output model:**
```csharp
public class ProjectConventions
{
    public string? ClaudeMdContent { get; set; }
    public string? NamingConventions { get; set; }  // Extracted from CLAUDE.md
    public string? ImportOrder { get; set; }          // Extracted from CLAUDE.md
    public string? ErrorHandlingPattern { get; set; } // Extracted from CLAUDE.md
    public string? TestStrategy { get; set; }         // Extracted from CLAUDE.md
    public string? TechStack { get; set; }            // From package.json
    public bool StrictMode { get; set; }              // From tsconfig.json
    public string? LintRulesSummary { get; set; }     // From eslint config
    public string? MonorepoStructure { get; set; }    // From pnpm-workspace.yaml
    public List<string> DetectedFiles { get; set; } = new();
    public string CompactSummary { get; set; } = "";  // <2000 chars for prompt injection
}
```

The `CompactSummary` field produces a condensed string suitable for injection into any prompt, capped at 2000 characters. Example output:

```
Project: Tamma (TypeScript 5.7+ strict, Node.js 22 LTS, pnpm monorepo)
Stack: Fastify 5.x, PostgreSQL 17, Vitest 3.x, Pino, dayjs
Naming: kebab-case files, PascalCase classes, I-prefix interfaces, camelCase functions
Imports: Node builtins > External deps > Internal @tamma/* > Relative
Errors: TammaError class with code, context, retryable, severity fields
Events: DCB pattern -- all operations MUST emit events via eventStore.append()
Tests: 80% line, 75% branch, 85% function coverage; Vitest 3.x; colocated *.test.ts
State: NEVER mutate -- always spread into new objects
Dates: dayjs UTC, ISO 8601 millisecond precision
```

### 1.3 Create `FetchArchitectureSummaryActivity`

**File:** `apps/tamma-elsa/src/Tamma.Activities/Context/FetchArchitectureSummaryActivity.cs`

Fetches and extracts a compact architecture summary from `docs/architecture.md` or the repository README. Produces a ~1500-char summary covering:
- Package/module layout
- Key interfaces and their contracts
- Data flow patterns
- Integration points

---

## Phase 2: Plan Generation Prompt Overhaul

### 2.1 Files to Modify

- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/PlanGenerationWorkflow.cs`

### 2.2 Before (current `BuildPlanPrompt`)

```csharp
private static string BuildPlanPrompt(string title, string body, string context, string feedback)
{
    var prompt = $"Generate a detailed implementation plan for the following GitHub issue:\n\n" +
                 $"**Title:** {safeTitle}\n" +
                 $"**Description:** {safeBody}\n\n";
    if (!string.IsNullOrEmpty(safeContext))
        prompt += $"**Context:** {safeContext}\n\n";
    if (!string.IsNullOrEmpty(safeFeedback))
        prompt += $"**Previous Feedback:** {safeFeedback}\n\n";
    prompt += "Respond with a JSON object containing: summary, steps (array), " +
              "filesToModify (array), filesToCreate (array), testStrategy, estimatedComplexity.";
    return prompt;
}
```

### 2.3 After (new prompt template)

**Template key:** `plan-generation`

```
You are a senior software architect planning implementation for the Tamma project.

## Project Context
{projectConventions}

## Architecture Summary
{architectureSummary}

## Existing Patterns
The following similar implementations exist in the codebase and should be used as reference:
{similarPatterns}

## Issue to Plan
**Title:** {issueTitle}
**Description:** {issueBody}

## Gathered Context
{contextJson}

{feedbackSection}

## Instructions

1. Analyze the issue requirements against the existing architecture.
2. Identify which existing modules, interfaces, and patterns are affected.
3. Check for breaking changes: will this modify any public interface contracts?
4. Design the implementation to follow project conventions:
   - File names: kebab-case (e.g., `event-store.ts`)
   - Interfaces: `I` prefix (e.g., `IEventStore`)
   - All operations must emit DCB events via `eventStore.append()`
   - TypeScript strict mode -- no `any`, no implicit returns
   - Error handling via `TammaError` with code, context, retryable, severity
   - Tests colocated as `*.test.ts`, Vitest 3.x
5. Plan the test strategy: what to test, coverage targets, mock strategy.
6. Assess risks: breaking changes, performance impact, security concerns.

## Required Output (JSON)

```json
{
  "summary": "1-2 sentence summary of the plan",
  "steps": [
    {
      "order": 1,
      "description": "What to do",
      "files": ["path/to/file.ts"],
      "rationale": "Why this step",
      "acceptanceCriteriaMapped": ["AC-1", "AC-3"]
    }
  ],
  "filesToModify": ["existing/file.ts"],
  "filesToCreate": ["new/file.ts"],
  "interfacesAffected": ["IEventStore", "IAIProvider"],
  "breakingChanges": [],
  "testStrategy": {
    "unitTests": ["what to unit test"],
    "integrationTests": ["what to integration test"],
    "coverageTarget": "80% line, 75% branch",
    "mockStrategy": "MSW for external APIs, in-memory for DB"
  },
  "estimatedComplexity": "low|medium|high|critical",
  "estimatedHours": 8,
  "risks": [
    {
      "description": "What could go wrong",
      "mitigation": "How to prevent it",
      "severity": "low|medium|high"
    }
  ],
  "dependencies": ["story-2-15 must be complete first"]
}
```

### 2.4 Workflow Changes

Add `DetectProjectConventionsActivity` and `FetchArchitectureSummaryActivity` as pre-steps before the LLM dispatch. Pass their outputs into `BuildPlanPrompt` as additional parameters.

```csharp
// Before LLM dispatch, add:
var detectConventions = new DetectProjectConventionsActivity { ... };
var fetchArchitecture = new FetchArchitectureSummaryActivity { ... };
// Wire outputs into BuildPlanPrompt parameters
```

---

## Phase 3: Code Review Prompt Overhaul

### 3.1 Files to Modify

- `apps/tamma-elsa/src/Tamma.Activities/AI/ClaudeAnalysisActivity.cs` (system + user prompts)
- `apps/tamma-elsa/src/Tamma.Activities/Review/DeliverGuidanceActivity.cs` (replace keyword matching)

### 3.2 Before (`ClaudeAnalysisActivity.GetSystemPrompt` for CodeReview)

```csharp
AI.AnalysisType.CodeReview => @"
You are reviewing code submitted by a junior developer.
Provide constructive feedback that helps them learn.
Focus on:
- Code correctness and logic
- Best practices and patterns
- Potential bugs or edge cases
- Code readability and maintainability
- Security considerations",
```

### 3.3 After (new system prompt for CodeReview)

**Template key:** `code-review-system`

```
You are a senior code reviewer for the Tamma project, an AI-powered autonomous development orchestration platform.

## Project Conventions (MUST enforce)

### Naming
- Files: kebab-case (`event-store.ts`, `plugin-manager.ts`)
- Test files: `*.test.ts` colocated with source
- Interfaces: `I` prefix (`IPluginManifest`, `IEventStore`)
- Classes: PascalCase
- Functions: camelCase, boolean functions use is/has/should prefix
- Constants: SCREAMING_SNAKE_CASE
- Private functions: `_` prefix

### TypeScript Strict Mode
- `strict: true`, `noImplicitAny: true`, `noImplicitReturns: true`
- `exactOptionalPropertyTypes: true` -- cannot assign `undefined` to optional props
- `noUncheckedIndexedAccess: true` -- indexed access returns `T | undefined`

### Import Order (enforce this exact order)
1. Node.js built-ins (`import { readFile } from 'fs/promises'`)
2. External dependencies (`import dayjs from 'dayjs'`)
3. Internal packages (`import type { IEventStore } from '@tamma/shared/contracts'`)
4. Relative imports (`import { PluginManager } from '../plugin-manager'`)

### Async/Error Handling
- ALWAYS async/await, NEVER .then()/.catch()
- Use `TammaError` with code, message, context, retryable, severity
- All async operations must implement retry with exponential backoff
- NEVER mutate state -- always create new objects with spread

### Event Sourcing (DCB Pattern)
- All operations MUST emit events via `eventStore.append()`
- Event types follow `AGGREGATE.ACTION.STATUS` pattern
- Events include tags (issueId, prId, userId, mode, provider)

### Security
- NEVER log API keys, tokens, or passwords
- All inputs must be validated and sanitized
- Credential storage uses OS-specific secure storage
- All API calls over HTTPS/TLS 1.3+

### Testing
- Coverage: 80% line, 75% branch, 85% function
- Critical paths: 100% coverage
- Mock external APIs with MSW
- Vitest 3.x, colocated `*.test.ts`

### Dates
- ALWAYS dayjs UTC with ISO 8601 millisecond precision

## Review Guidelines
- Be constructive. Explain WHY something should change, not just WHAT.
- Severity levels: Critical (blocks merge), Major (should fix), Minor (nice to have), Suggestion (optional improvement).
- For each issue, provide a concrete fix or code example.
- Acknowledge good patterns the developer used.
- Adapt feedback complexity to skill level {skillLevel}/5.
```

### 3.4 New user prompt for CodeReview

**Template key:** `code-review-user`

```
Review the following code changes against the project conventions above.

## Code Under Review
```
{codeContent}
```

## Story Context
{storyContext}

## Files Changed
{filesChanged}

## Additional Context
{additionalContext}

## Output Format (JSON)

```json
{
  "overall_quality": "Good|Acceptable|NeedsWork",
  "score": 0-100,
  "conventionViolations": [
    {
      "rule": "naming-convention",
      "location": "file.ts:42",
      "violation": "Function `getData` should use descriptive name",
      "fix": "Rename to `fetchUserProfile`"
    }
  ],
  "issues": [
    {
      "severity": "Critical|Major|Minor|Suggestion",
      "location": "file.ts:42",
      "issue": "What is wrong",
      "suggestion": "How to fix it",
      "codeExample": "// corrected code"
    }
  ],
  "missingEventEmission": ["List of operations that should emit DCB events but don't"],
  "securityConcerns": ["Any credential leaks, unsanitized inputs, etc."],
  "testGaps": ["Operations lacking test coverage"],
  "positives": ["Good patterns used"],
  "learning_opportunities": ["Concepts to study"]
}
```

### 3.5 Replace `DeliverGuidanceActivity.GenerateGuidanceForComment`

**Before:** Hardcoded keyword matching (`bodyLower.Contains("null check")` -> static string).

**After:** Dispatch to LLM Call sub-workflow with this prompt:

**Template key:** `fix-guidance`

```
You are a mentoring code reviewer. Generate specific, actionable fix guidance for this review comment.

## Review Comment
**File:** {filePath} (line {lineNumber})
**Severity:** {severity}
**Comment:** {commentBody}

## Developer Skill Level: {skillLevel}/5

## Project Conventions
{projectConventionsCompact}

## Instructions
1. Explain WHAT needs to change and WHY (adapt detail level to skill level).
2. Provide a concrete code example showing the fix.
3. Reference relevant project conventions or patterns.
4. For skill levels 1-2, include step-by-step instructions.
5. For skill levels 4-5, focus on the reasoning and trade-offs.

## Output Format (JSON)
```json
{
  "guidance": "Clear explanation of what to fix and why",
  "codeExample": "// The corrected code",
  "conventionReference": "Which convention this relates to (if any)",
  "learnMore": "Brief pointer to relevant documentation or concept"
}
```
```

---

## Phase 4: Assessment Prompt Overhaul

### 4.1 Files to Modify

- `apps/tamma-elsa/src/Tamma.Activities/Assessment/GenerateQuestionsActivity.cs`
- `apps/tamma-elsa/src/Tamma.Activities/Assessment/AnalyzeResponseActivity.cs`
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/AssessmentWorkflow.cs` (fix `storeContextResult`)

### 4.2 Before (`GenerateQuestionsActivity.GetSkillLevelQuestions`)

Returns static questions like:
```
"In your own words, describe what this story requires you to build and why it matters."
"What are the key acceptance criteria, and how will you verify each one is met?"
```

Story context is received but never used.

### 4.3 After

Replace `BuildQuestions` with an LLM call using this prompt:

**Template key:** `assessment-questions`

```
You are assessing a junior developer's understanding of a story before they begin implementation.

## Story Details
**Title:** {storyTitle}
**Description:** {storyDescription}

## Acceptance Criteria
{acceptanceCriteria}

## Relevant Files
{relevantFiles}

## Architecture Context
{architectureContext}

## Developer Skill Level: {skillLevel}/5
## Question Count: {questionCount}
## Is Retry: {isRetry}

{previousAttemptSection}

## Instructions

Generate {questionCount} assessment questions that:

1. **Test understanding of specific requirements** -- reference concrete acceptance criteria by number.
2. **Probe technical approach** -- ask about specific files they will need to modify and what patterns to follow.
3. **Check awareness of edge cases** -- based on the actual story requirements, not generic edge cases.
4. **Verify awareness of project conventions** -- ask about naming, error handling, or event emission requirements relevant to this story.
5. For skill levels 1-2: focus on comprehension ("What does AC-3 mean in practice?").
6. For skill levels 3-5: include design questions ("How would you handle the error case in the provider interface?").
7. If this is a retry: focus questions on the gaps identified in the previous attempt. Do NOT repeat questions the developer already answered correctly.

## Output Format (JSON)
```json
{
  "questions": [
    {
      "text": "The question text",
      "targetedAC": "AC-2",
      "difficulty": "comprehension|application|analysis",
      "gapTargeted": "previous gap being re-assessed (null if first attempt)"
    }
  ],
  "contextSummary": "Brief summary of what the story is about for the assessment record"
}
```
```

### 4.4 Replace `AnalyzeResponseActivity.PerformAnalysis`

**Before:** Heuristic based on `responseLength / questionCount` and keyword counting.

**After:** LLM call using this prompt:

**Template key:** `assessment-analysis`

```
You are evaluating a junior developer's responses to assessment questions about a story.

## Story Context
{storyContext}

## Acceptance Criteria
{acceptanceCriteria}

## Questions Asked
{questionsJson}

## Developer's Response
{juniorResponse}

## Developer Skill Level: {skillLevel}/5

## Instructions

Evaluate each response against the actual story requirements:

1. Does the developer correctly understand what needs to be built?
2. Do they reference the correct acceptance criteria?
3. Do they identify the right files, interfaces, or patterns?
4. Are there factual errors or misunderstandings?
5. Is their proposed approach technically sound?

Be encouraging but honest. Do not inflate confidence for vague or generic responses.
A response that says "I would implement the feature following best practices" without specifics scores LOW.
A response that says "I would modify `event-store.ts` to add a new event type `CODE.REVIEWED.SUCCESS` with tags for prId and reviewerId" scores HIGH.

## Output Format (JSON)
```json
{
  "status": "Correct|Partial|Incorrect",
  "confidence": 0.0-1.0,
  "understanding_summary": "What the developer understood correctly",
  "gaps": ["Specific knowledge gaps identified"],
  "strengths": ["Specific strengths demonstrated"],
  "perQuestionAnalysis": [
    {
      "questionIndex": 0,
      "score": "correct|partial|incorrect",
      "rationale": "Why this score"
    }
  ],
  "rationale": "Overall assessment rationale"
}
```
```

### 4.5 Fix `AssessmentWorkflow.storeContextResult`

**Before:**
```csharp
Value = new(ctx => {
    return $"Assessment context for story {storyId.Get(ctx)} gathered via ContextGathering workflow";
})
```

**After:** Extract the actual `contextJson` output from the dispatched context-gathering workflow and store it in the `storyContext` variable.

---

## Phase 5: Issue Selection Improvements

### 5.1 Files to Modify

- `apps/tamma-elsa/src/Tamma.Activities/ADL/SelectIssueActivity.cs`
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/IssueSelectionWorkflow.cs`

### 5.2 New Activities

#### `ScoreIssueComplexityActivity`

**File:** `apps/tamma-elsa/src/Tamma.Activities/ADL/ScoreIssueComplexityActivity.cs`

Scores each candidate issue 1-10 based on:
- Label analysis: `complexity:high`, `size:large`, `breaking-change` increase score
- Body length: longer issues tend to be more complex
- File mentions in body: more files = more complex
- Dependency mentions: references to other issues (`#123`, `depends on`) increase score

#### `AnalyzeIssueDependenciesActivity`

**File:** `apps/tamma-elsa/src/Tamma.Activities/ADL/AnalyzeIssueDependenciesActivity.cs`

Parses issue body and comments for dependency patterns:
- `depends on #123`, `blocked by #456`, `after #789`
- Checks if referenced issues are closed (dependency satisfied) or open (dependency not met)

### 5.3 Before (`SelectIssueActivity`)

```csharp
var issue = result.Data?.FirstOrDefault(i => string.IsNullOrEmpty(i.Assignee));
```

### 5.4 After

```csharp
// 1. Filter to unassigned issues
var candidates = result.Data?.Where(i => string.IsNullOrEmpty(i.Assignee)).ToList();

// 2. Score complexity
var scored = candidates.Select(i => new {
    Issue = i,
    Complexity = ScoreComplexity(i),
    Dependencies = AnalyzeDependencies(i, result.Data),
    Priority = GetPriorityFromLabels(i.Labels)
}).ToList();

// 3. Filter out issues with unmet dependencies
var eligible = scored.Where(s => s.Dependencies.AllMet).ToList();

// 4. Sort by: priority DESC, then complexity ASC (pick easiest high-priority first)
var selected = eligible
    .OrderByDescending(s => s.Priority)
    .ThenBy(s => s.Complexity)
    .FirstOrDefault();
```

---

## Phase 6: Blocker Diagnosis Prompt Overhaul

### 6.1 Files to Modify

- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/BlockerDiagnosisWorkflow.cs`

### 6.2 Before (`BuildDiagnosisPrompt`)

```
Diagnose what is blocking this junior developer (skill level {skillLevel}/5).

Git Activity: ...
CI Status: ...
...

Classify into one of: ConceptualMisunderstanding, ...

Return JSON with: blocker_type, confidence (0-1), root_cause, evidence[], recommended_approach
```

### 6.3 After

**Template key:** `blocker-diagnosis`

```
You are a senior developer mentor diagnosing why a junior developer is stuck.

## Project
{projectConventionsCompact}

## Story Being Worked On
**Title:** {storyTitle}
**Description:** {storyDescription}
**Files Expected to Change:** {expectedFiles}

## Developer Profile
- Skill level: {skillLevel}/5
- Previous blockers in this session: {previousBlockerHistory}

## Collected Signals

### Git Activity
{gitSignalData}

### CI/Build Status
{ciSignalData}

### Inactivity
{inactivitySignalData}

### Communication
{communicationSignalData}

## Additional Context
{blockerContext}

## Resolution History (what has already been tried)
{resolutionHistory}

## Instructions

1. Analyze ALL signals together -- don't fixate on one signal.
2. Consider the story requirements: is the developer stuck on something specific to this story, or a general skill gap?
3. Consider the skill level: a level-1 developer struggling with async/await is expected; a level-4 developer struggling with it suggests a deeper issue.
4. If CI is failing, look at the specific error -- is it a syntax issue, a logic bug, or an environment problem?
5. If the developer has been inactive, consider whether they asked questions (communication signal) or went silent.
6. Do NOT suggest approaches already tried in the resolution history.

## Blocker Categories
- ConceptualMisunderstanding: doesn't understand the requirement itself
- TechnicalKnowledgeGap: understands the requirement but lacks the technical skill
- EnvironmentIssue: tooling, build, or environment problem
- DesignDecisionParalysis: can't decide on the right approach
- DebuggingStuck: can't find or fix a specific bug
- IntegrationIssue: components don't work together
- ExternalDependency: blocked by external team/API/service
- PersonalBlocker: motivation, distraction, or capacity issue

## Output Format (JSON)
```json
{
  "blocker_type": "one of the categories above",
  "confidence": 0.0-1.0,
  "root_cause": "specific root cause hypothesis",
  "evidence": ["observation 1", "observation 2"],
  "recommended_approach": "Hint|Guidance|Assistance|Escalation",
  "immediate_action": "what to do right now",
  "relevant_files": ["files the developer should look at"],
  "relevant_documentation": ["docs or patterns to reference"]
}
```
```

### 6.4 Enriched Progressive Resolution Prompts

#### Hint Level (Socratic)

**Template key:** `blocker-hint`

```
You are a mentor using the Socratic method to guide a junior developer (skill level {skillLevel}/5) past a blocker.

## Blocker Diagnosis
- Type: {blockerType}
- Root cause: {rootCauseHypothesis}

## Story Context
{storyTitle}: {storyDescription}

## Relevant Code
{relevantCodeSnippets}

## Project Patterns to Reference
{similarPatterns}

## Instructions
1. Do NOT give the answer directly.
2. Ask 2-3 guiding questions that lead the developer to discover the issue.
3. Reference specific code or patterns they should look at.
4. Each question should narrow the problem space.
5. End with an encouraging note about their progress.

Example good hint: "Looking at `event-store.ts` line 42, what type does `appendEvent` expect for the `tags` field? Now compare that with what you're passing on line 67 of your new code..."
Example bad hint: "You have a type error. Fix it."

## Output Format (JSON)
```json
{
  "hints": [
    {
      "question": "The guiding question",
      "pointsTo": "What this question leads them to discover"
    }
  ],
  "filesRecommended": ["files to look at"],
  "encouragement": "motivating message"
}
```
```

#### Guidance Level (Direct)

**Template key:** `blocker-guidance`

```
You are providing direct guidance to a junior developer (skill level {skillLevel}/5) who is stuck.

## Blocker Diagnosis
- Type: {blockerType}
- Root cause: {rootCauseHypothesis}

## Story Context
{storyTitle}: {storyDescription}

## Relevant Code
{relevantCodeSnippets}

## Project Conventions
{projectConventionsCompact}

## What Was Already Tried
Hint-level guidance was provided but the developer did not make progress.
Previous hints: {previousHints}

## Instructions
1. Explain the problem clearly and directly.
2. Provide step-by-step instructions to resolve it.
3. Reference specific files and line numbers.
4. Show what the correct pattern looks like in this project.
5. For skill levels 1-2: be very detailed, explain each step.
6. For skill levels 4-5: be concise, focus on the key insight.

## Output Format (JSON)
```json
{
  "explanation": "Clear explanation of the problem",
  "steps": [
    {
      "order": 1,
      "instruction": "What to do",
      "file": "which file",
      "codeSnippet": "relevant code to write or modify"
    }
  ],
  "patternReference": "Which project pattern to follow",
  "commonMistake": "What mistake to avoid"
}
```
```

#### Assistance Level (Code Examples)

**Template key:** `blocker-assistance`

```
You are providing direct code assistance to a junior developer (skill level {skillLevel}/5) who remains stuck after receiving hints and guidance.

## Blocker Diagnosis
- Type: {blockerType}
- Root cause: {rootCauseHypothesis}

## Story Context
{storyTitle}: {storyDescription}

## Current Code (developer's attempt)
{currentCode}

## Project Conventions
{projectConventionsCompact}

## What Was Already Tried
- Hint: {previousHints}
- Guidance: {previousGuidance}

## Instructions
1. Provide a WORKING code example that solves the immediate problem.
2. The example MUST follow project conventions (naming, imports, error handling, event emission).
3. Include inline comments explaining WHY each part is written this way.
4. Show only the minimum code needed -- do not rewrite the entire file.
5. If tests are needed, show a test example too.

## Output Format (JSON)
```json
{
  "codeExample": "// The working code with comments",
  "testExample": "// A test for the code (if applicable)",
  "explanation": "Why this solution works",
  "nextSteps": ["What to do after applying this fix"]
}
```
```

---

## Phase 7: Debug Diagnosis Prompt Overhaul

### 7.1 Files to Modify

- `apps/tamma-elsa/src/Tamma.Activities/Debug/AIDiagnosisActivity.cs`

### 7.2 Before

```csharp
sb.AppendLine("You are a debugging specialist (role: debugger). Analyze the following context and generate ranked root cause hypotheses.");
```

### 7.3 After

**Template key:** `debug-diagnosis`

```
You are a debugging specialist analyzing a failure in the Tamma project.

## Project Context
{projectConventionsCompact}

## Common Error Patterns in This Project
- TammaError with code/context/retryable/severity fields
- Provider errors use `createProviderError(code, message, retryable, severity)`
- Event sourcing failures surface as missing or malformed DCB events
- TypeScript strict mode errors: `exactOptionalPropertyTypes`, `noUncheckedIndexedAccess`

## Debug Mode: {mode}

## Error Messages / Stack Traces
{errorContext}

## Relevant Code
{codeContext}

## Git History (recent changes that may have introduced the bug)
{gitContext}

## Test Results
{testContext}

{reproductionSection}

{previousAttemptsSection}

## Instructions
1. Cross-reference error messages with the code context to identify the exact failure point.
2. Check git history for recent changes that could have introduced the issue.
3. Consider TypeScript strict mode gotchas (see project patterns above).
4. If tests are failing, analyze the test expectations vs. actual behavior.
5. Rank hypotheses by confidence. The top hypothesis should be the most actionable.
6. For each hypothesis, specify the EXACT file and approximate line to investigate.
7. If previous attempts failed, explain why they didn't work and what's different about the new hypothesis.

## Output Format (JSON)
```json
{
  "analysis_summary": "Brief summary of the analysis",
  "hypotheses": [
    {
      "rank": 1,
      "description": "Specific root cause description",
      "confidence": 0.85,
      "suggested_fix": "Exact steps to fix",
      "affected_files": ["src/specific-file.ts"],
      "investigation_steps": ["Step 1: check line X", "Step 2: verify Y"]
    }
  ]
}
```
```

---

## Phase 8: Context Gathering Improvements

### 8.1 Files to Modify

- `apps/tamma-elsa/src/Tamma.Activities/Context/FetchSimilarPatternsActivity.cs`
- `apps/tamma-elsa/src/Tamma.Activities/AI/ContextGatheringActivity.cs`
- `apps/tamma-elsa/src/Tamma.ElsaServer/Workflows/ContextGatheringWorkflow.cs`

### 8.2 Changes

1. **`FetchSimilarPatternsActivity`**: Replace `DiscoverPatternsAsync` simulation with real GitHub code search API call. Use the `GET /search/code` endpoint to find files matching keywords from the story title and description. Fall back to the simulated patterns only when the API is unavailable.

2. **`ContextGatheringActivity`**: Replace simulated `GatherFileContents`, `GatherProjectStructure`, and `GatherSimilarPatterns` with real GitHub API calls via `IIntegrationService`. These methods already call `_integrationService` but the actual implementations return mocks.

3. **`ContextGatheringWorkflow`**: Add `DetectProjectConventionsActivity` as a Phase 1 parallel fetch alongside the existing fetches. Store the result in a new `conventionsResult` variable. Pass conventions into `AssembleContextActivity` as a new high-priority section.

### 8.3 Semantic Relevance Scoring

Add a relevance scoring step to `FetchFileContentsActivity` that ranks files by:
- Direct mention in story title/description (high)
- Modified in recent commits for this story (high)
- Same directory as directly mentioned files (medium)
- Similar naming pattern (medium)
- Generic utility/config files (low)

This score feeds into `ApplyBudgetActivity` so that when trimming file contents, the least-relevant files are removed first (which it already does via `RelevanceScore`, but currently all scores are default).

---

## Phase 9: Integration into ResolveLlmPromptActivity

### 9.1 Files to Modify

- `apps/tamma-elsa/src/Tamma.Activities/LlmCall/ResolveLlmPromptActivity.cs`

### 9.2 Changes

Add a new resolution level (Level 0.5): **PromptTemplateRegistry**. If a template exists for the combination of `role + operationName`, use it. This sits above the config hierarchy and allows prompt templates to be maintained in code with version control.

```csharp
// NEW: Level 0: Template registry (if available)
var templateKey = $"{role}:{operationName}";
if (PromptTemplateRegistry.TryGet(templateKey, out var template))
    return (template, PromptResolutionLevel.TemplateRegistry, templateKey);
```

---

## Testing Plan

### Unit Tests

| Test | File |
|------|------|
| `PromptTemplateRegistry.Get` returns correct template with placeholders | `Tamma.Activities.Tests/Prompts/PromptTemplateRegistryTests.cs` |
| `DetectProjectConventionsActivity` returns conventions from CLAUDE.md | `Tamma.Activities.Tests/Context/DetectProjectConventionsActivityTests.cs` |
| `BuildPlanPrompt` new version includes architecture summary | `Tamma.Activities.Tests/ADL/PlanGenerationPromptTests.cs` |
| `GetSystemPrompt(CodeReview)` includes naming conventions | `Tamma.Activities.Tests/AI/ClaudeAnalysisPromptTests.cs` |
| `ScoreIssueComplexityActivity` scores high for complex labels | `Tamma.Activities.Tests/ADL/ScoreIssueComplexityTests.cs` |
| `AnalyzeIssueDependenciesActivity` detects `depends on #123` | `Tamma.Activities.Tests/ADL/AnalyzeIssueDependenciesTests.cs` |
| Blocker diagnosis prompt includes story context | `Tamma.Activities.Tests/Blocker/BlockerDiagnosisPromptTests.cs` |
| Assessment questions reference specific acceptance criteria | `Tamma.Activities.Tests/Assessment/GenerateQuestionsPromptTests.cs` |

### Integration Tests

- End-to-end plan generation with real LLM (test credentials)
- Code review with project convention validation
- Assessment question generation for a real story

---

## Migration Notes

- All existing prompt paths continue to work via the fallback hierarchy in `ResolveLlmPromptActivity`.
- The `PromptTemplateRegistry` is additive -- it does not remove existing config-based prompts.
- Simulated/mock implementations in activities are preserved behind feature flags for testing.
- Token budget impact: the enriched prompts are approximately 2x larger. The `ApplyBudgetActivity` context cap may need adjustment from 50,000 to 80,000 characters for plan generation.
