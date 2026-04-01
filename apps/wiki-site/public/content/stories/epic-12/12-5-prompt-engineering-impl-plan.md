---
title: "Story 12-5: Prompt Engineering Framework -- Implementation Plan"
sidebar:
  order: 120
---

## Overview

This plan introduces a prompt template registry, project convention injection, and role-specific system prompt catalog into the existing LLM call pipeline. The implementation is backward compatible -- the existing `ResolveAgentConfigActivity` remains the primary resolution path, and the new framework layers underneath it as an enhanced fallback.

## Architecture

### Current Prompt Resolution Flow

```
LlmCallWorkflow.InitInputs
  -> ResolveAgentConfigActivity
       -> Priority 1: caller systemPromptOverride (sanitized + hardened)
       -> Priority 2: ELSA Agents DB lookup for "tamma-{role}"
       -> Priority 3: GetFallbackPrompt(role) -- 1-2 sentence hardcoded strings
  -> Sets workflow variable: ResolvedSystemPrompt
  -> CallLlmInlineActivity uses ResolvedSystemPrompt
```

### New Prompt Resolution Flow

```
LlmCallWorkflow.InitInputs
  -> ResolveAgentConfigActivity (MODIFIED)
       -> Priority 1: caller systemPromptOverride (unchanged)
       -> Priority 2: ELSA Agents DB lookup (unchanged)
       -> Priority 3: PromptTemplateRegistry.Resolve(role, operation, context)  <-- NEW
            -> Loads versioned template
            -> Injects project conventions via ConventionProvider
            -> Renders {{variable}} placeholders
            -> Applies chain-of-thought scaffolding
       -> Priority 4: GetFallbackPrompt(role) (unchanged, emergency fallback)
  -> Sets: ResolvedSystemPrompt, PromptTemplateVersion  <-- NEW variable
  -> CallLlmInlineActivity uses ResolvedSystemPrompt
```

### Component Diagram

```
┌─────────────────────────────────────────────────────────┐
│                  LlmCallWorkflow                        │
│                                                         │
│  ┌──────────────────────┐    ┌───────────────────────┐  │
│  │ ResolveAgentConfig   │───>│ PromptTemplateRegistry│  │
│  │ Activity             │    │                       │  │
│  │ (DB + fallback)      │    │ - templates by role   │  │
│  └──────────────────────┘    │ - version tracking    │  │
│                              │ - interpolation       │  │
│                              └───────────┬───────────┘  │
│                                          │              │
│                              ┌───────────▼───────────┐  │
│                              │ ConventionProvider     │  │
│                              │                       │  │
│                              │ - reads CLAUDE.md     │  │
│                              │ - categories           │  │
│                              │ - role filtering       │  │
│                              └───────────────────────┘  │
│                                                         │
│  ┌──────────────────────┐                               │
│  │ CallLlmInlineActivity│  (uses resolved prompt)       │
│  └──────────────────────┘                               │
│                                                         │
│  ┌──────────────────────┐                               │
│  │ ContextCompactor     │  (enhanced with priorities)   │
│  │ (Story 12.3)         │                               │
│  └──────────────────────┘                               │
└─────────────────────────────────────────────────────────┘
```

## Phase 1: Prompt Template Registry (P0)

### New Files

#### `Tamma.Activities/LlmCall/Prompts/PromptTemplateRegistry.cs`

```csharp
namespace Tamma.Activities.LlmCall.Prompts;

/// <summary>
/// Stores and resolves versioned prompt templates by (role, operation) tuple.
/// Supports {{variable}} interpolation compatible with the TypeScript AgentPromptRegistry.
///
/// Resolution order:
///   1. templates[(role, operation)] -- most specific
///   2. templates[(role, "*")]       -- role default
///   3. null                         -- caller falls back to DB or hardcoded
/// </summary>
public class PromptTemplateRegistry
{
    // Template storage: key = (role, operation), value = PromptTemplate
    // Uses Dictionary with composite key for O(1) lookup
    private readonly Dictionary<(string Role, string Operation), PromptTemplate> _templates = new();

    /// <summary>Register a template for a specific role+operation.</summary>
    public void Register(string role, string operation, PromptTemplate template);

    /// <summary>Resolve the best template for the given role and operation.</summary>
    public PromptTemplate? Resolve(string role, string operation = "*");

    /// <summary>Render a template with variable substitution and convention injection.</summary>
    public string Render(PromptTemplate template, Dictionary<string, string> variables);

    /// <summary>List all registered template keys (for diagnostics/dashboard).</summary>
    public IReadOnlyList<(string Role, string Operation, string Version)> ListRegistered();
}
```

#### `Tamma.Activities/LlmCall/Prompts/PromptTemplate.cs`

```csharp
namespace Tamma.Activities.LlmCall.Prompts;

/// <summary>
/// A versioned prompt template with structured sections.
/// </summary>
public class PromptTemplate
{
    /// <summary>Semantic version (e.g., "implementer.v3").</summary>
    public string Version { get; init; } = "v1";

    /// <summary>Role identity section (who the LLM is).</summary>
    public string RoleIdentity { get; init; } = "";

    /// <summary>Convention placeholder -- filled by ConventionProvider at render time.</summary>
    public bool InjectConventions { get; init; } = true;

    /// <summary>Convention categories to include (empty = all).</summary>
    public string[] ConventionCategories { get; init; } = Array.Empty<string>();

    /// <summary>Task instruction section with chain-of-thought scaffolding.</summary>
    public string TaskInstructions { get; init; } = "";

    /// <summary>Few-shot examples section (optional).</summary>
    public string? FewShotExamples { get; init; }

    /// <summary>Output format specification.</summary>
    public string? OutputFormat { get; init; }

    /// <summary>Full template text (if provided, overrides structured sections).</summary>
    public string? FullTemplate { get; init; }

    /// <summary>Compose the full system prompt from sections.</summary>
    public string Compose(string? conventions = null)
    {
        if (!string.IsNullOrEmpty(FullTemplate))
        {
            if (InjectConventions && conventions != null)
                return FullTemplate.Replace("{{conventions}}", conventions);
            return FullTemplate;
        }

        var sb = new StringBuilder();
        sb.AppendLine(RoleIdentity);
        if (InjectConventions && !string.IsNullOrEmpty(conventions))
        {
            sb.AppendLine();
            sb.AppendLine("## Project Conventions");
            sb.AppendLine();
            sb.AppendLine(conventions);
        }
        sb.AppendLine();
        sb.AppendLine(TaskInstructions);
        if (!string.IsNullOrEmpty(FewShotExamples))
        {
            sb.AppendLine();
            sb.AppendLine("## Examples");
            sb.AppendLine();
            sb.AppendLine(FewShotExamples);
        }
        if (!string.IsNullOrEmpty(OutputFormat))
        {
            sb.AppendLine();
            sb.AppendLine("## Output Format");
            sb.AppendLine();
            sb.AppendLine(OutputFormat);
        }
        return sb.ToString();
    }
}
```

#### `Tamma.Activities/LlmCall/Prompts/BuiltInPrompts.cs`

Static class containing the 6 role-specific prompt templates as constants. Each template includes the actual prompt text from the story document (implementer, reviewer, mentor, debugger, analyst, tester). These are the **shipped defaults** -- users can override via ELSA Agents DB.

```csharp
namespace Tamma.Activities.LlmCall.Prompts;

/// <summary>
/// Built-in prompt templates for all standard roles.
/// Each template includes:
///   - Role identity and expertise boundaries
///   - Output format specification
///   - Chain-of-thought scaffolding
///   - Convention injection placeholder
///
/// These are registered into PromptTemplateRegistry at startup.
/// Users can override via ELSA Agents DB (higher priority).
/// </summary>
public static class BuiltInPrompts
{
    public static PromptTemplate Implementer { get; } = new()
    {
        Version = "implementer.v1",
        FullTemplate = """
## Role

You are an expert TypeScript developer working on the Tamma project. You write
production-quality code that passes strict TypeScript compilation and all tests.

## Project Conventions

{{conventions}}

## Task

Implement the requested changes following these steps:

<thinking>
1. Analyze the requirements and identify affected files
2. Check for existing patterns in the codebase that should be followed
3. Identify edge cases and error conditions
4. Plan the implementation order (interfaces first, then implementations, then tests)
</thinking>

<plan>
List each file to create or modify with a one-line description of changes.
</plan>

<implementation>
For each file, provide the complete implementation.

Rules:
- Follow the import order: Node.js built-ins, external deps, internal packages, relative
- Use async/await, never .then()/.catch()
- All errors must use the TammaError class with code, message, context, retryable, severity
- Boolean functions must use is/has/should prefix
- Private functions must use _ prefix
- Constants must use SCREAMING_SNAKE_CASE
- Files use kebab-case, test files are colocated as *.test.ts
- Never mutate state -- always create new objects with spread

Output each file as:
```path/to/file.ts
// file contents
```
</implementation>
"""
    };

    public static PromptTemplate Reviewer { get; } = new()
    {
        Version = "reviewer.v1",
        FullTemplate = """
## Role

You are an expert code reviewer specializing in TypeScript, Node.js, and distributed systems.
You identify bugs, security vulnerabilities, performance issues, and convention violations.

## Project Conventions

{{conventions}}

## Task

Review the provided code changes. For each issue found:

<thinking>
1. Read the full diff to understand the change's intent
2. Check each file against project conventions
3. Identify logical errors, edge cases, and missing error handling
4. Check for security issues (credential leaks, injection, unsafe input)
5. Verify test coverage for new/changed code paths
</thinking>

<review>
For each issue, provide:
- **File**: path/to/file.ts
- **Line**: line number or range
- **Severity**: critical | major | minor | style
- **Category**: bug | security | performance | convention | test-coverage
- **Issue**: description of the problem
- **Fix**: specific code change to resolve it

If no issues are found, explicitly state "No issues found" with a brief explanation
of what you verified.
</review>

<summary>
Provide a 1-3 sentence summary of the review.
Decision: APPROVE | REQUEST_CHANGES | COMMENT
</summary>
"""
    };

    public static PromptTemplate Mentor { get; } = new()
    {
        Version = "mentor.v1",
        FullTemplate = """
## Role

You are an experienced software development mentor guiding a developer at skill
level {{skillLevel}}/5 on the Tamma project.

{{mentorApproach}}

## Project Conventions

{{conventions}}

## Task

Guide the developer through implementing: {{taskDescription}}

<thinking>
1. Assess what the developer already understands about this task
2. Identify the key concepts they need to grasp
3. Plan a sequence of questions/hints to guide them
4. Prepare fallback explanations if they get stuck
</thinking>

Respond with your first guidance message. Keep it focused on one concept at a time.
"""
    };

    public static PromptTemplate Debugger { get; } = new()
    {
        Version = "debugger.v1",
        FullTemplate = """
## Role

You are an expert debugger specializing in TypeScript/Node.js applications.
You systematically diagnose issues using evidence-based reasoning.

## Project Conventions

{{conventions}}

## Task

Diagnose and fix the following issue:

{{errorContext}}

<thinking>
1. Parse the error message and stack trace to identify the immediate cause
2. Identify the root cause (not just the symptom)
3. Check if this is a known pattern (configuration error, dependency mismatch, race condition)
4. Determine the minimal fix that addresses the root cause
5. Verify the fix doesn't introduce regressions
</thinking>

<diagnosis>
- **Error**: one-line description
- **Root Cause**: explanation of why this happens
- **Affected Files**: list of files involved
- **Fix Strategy**: approach to resolve
</diagnosis>

<fix>
Provide the exact code changes needed.
</fix>

<verification>
Describe how to verify the fix works (test commands, expected output).
</verification>
"""
    };

    public static PromptTemplate Analyst { get; } = new()
    {
        Version = "analyst.v1",
        FullTemplate = """
## Role

You are a technical analyst for the Tamma project. You assess code quality,
identify patterns, and provide structured evaluations.

## Project Conventions

{{conventions}}

## Task

{{analysisTask}}

<thinking>
1. Identify the scope of analysis (which files, which aspects)
2. Gather relevant context from the codebase
3. Compare against conventions and best practices
4. Quantify findings where possible (number of violations, test coverage %)
</thinking>

<findings>
Present findings as a structured list:

| Category | Finding | Severity | Location |
|----------|---------|----------|----------|
| ... | ... | ... | ... |

</findings>

<recommendations>
Prioritized list of actions, each with:
- What to change
- Why (impact if not changed)
- Estimated effort (small/medium/large)
</recommendations>
"""
    };

    public static PromptTemplate Tester { get; } = new()
    {
        Version = "tester.v1",
        FullTemplate = """
## Role

You are a testing specialist for the Tamma project. You write thorough,
maintainable tests using Vitest 3.x with colocated test files.

## Project Conventions

{{conventions}}

## Task

Write tests for: {{testTarget}}

<thinking>
1. Identify the public API surface to test
2. List happy path scenarios
3. List error/edge case scenarios
4. Identify dependencies that need mocking (use MSW for HTTP, vi.mock for modules)
5. Determine coverage targets (80% line, 75% branch, 85% function)
</thinking>

<test_plan>
List each test case with:
- Description (should read like documentation)
- Category: unit | integration | edge-case | error-handling
- Expected behavior
</test_plan>

<tests>
Write the test file. Rules:
- Use describe/it blocks with descriptive names
- Each test should test ONE thing
- Use vi.mock() factories that are self-contained (hoisted)
- Mock external APIs with MSW
- Assert specific values, not just truthiness
- Test error paths explicitly (expect(...).rejects.toThrow)
- Clean up after each test (afterEach)

File format:
```path/to/file.test.ts
// test contents
```
</tests>
"""
    };

    /// <summary>
    /// Mentor approach text based on skill level.
    /// Used as {{mentorApproach}} variable in the Mentor template.
    /// </summary>
    public static string GetMentorApproach(int skillLevel) => skillLevel switch
    {
        <= 2 => """
## Mentoring Approach (Beginner)
- Explain concepts before asking the developer to implement them
- Provide concrete examples from the project's existing code
- Break tasks into very small steps (1-3 lines of code each)
- Validate understanding after each step with a yes/no question
- If the developer is stuck, provide the solution with explanation
""",
        3 => """
## Mentoring Approach (Intermediate)
- Use Socratic questioning to guide the developer to solutions
- Provide hints rather than direct answers when they're stuck
- Point to relevant files and patterns but don't write the code
- Allow 2 attempts before providing direct guidance
""",
        _ => """
## Mentoring Approach (Advanced)
- Focus on architecture and design decisions rather than implementation details
- Challenge assumptions and ask about trade-offs
- Discuss alternative approaches and their implications
- Only intervene if the developer is going in a fundamentally wrong direction
"""
    };

    /// <summary>Register all built-in templates into the registry at startup.</summary>
    public static void RegisterAll(PromptTemplateRegistry registry)
    {
        registry.Register("implementer", "*", Implementer);
        registry.Register("reviewer", "*", Reviewer);
        registry.Register("mentor", "*", Mentor);
        registry.Register("debugger", "*", Debugger);
        registry.Register("analyst", "*", Analyst);
        registry.Register("tester", "*", Tester);
    }
}
```

### Registration in DI

In `Tamma.ElsaServer/Program.cs` (or the relevant service registration file):

```csharp
// Register prompt template registry as singleton
var promptRegistry = new PromptTemplateRegistry();
BuiltInPrompts.RegisterAll(promptRegistry);
services.AddSingleton(promptRegistry);

// Register convention provider
services.AddSingleton<ConventionProvider>();
```

## Phase 2: Convention Provider (P0)

### New Files

#### `Tamma.Activities/LlmCall/Prompts/ConventionProvider.cs`

```csharp
namespace Tamma.Activities.LlmCall.Prompts;

/// <summary>
/// Reads project conventions from a configurable source and formats them for prompt injection.
///
/// Convention sources (checked in order):
///   1. Configuration key "Tamma:ConventionsPath" pointing to a YAML/MD file
///   2. ".tamma/conventions.yaml" in the repository root
///   3. "CLAUDE.md" in the repository root (parsed for conventions section)
///   4. Built-in defaults from the Tamma project's own CLAUDE.md
///
/// Conventions are categorized and can be filtered by role. For example,
/// a code reviewer gets naming + error handling + security conventions,
/// while a tester gets testing + mocking conventions.
/// </summary>
public class ConventionProvider
{
    /// <summary>
    /// Get formatted convention text for a specific role.
    /// Only includes categories relevant to the role.
    /// </summary>
    public string GetConventionsForRole(string role, string[]? categories = null);

    /// <summary>
    /// Get all conventions as structured text.
    /// </summary>
    public string GetAllConventions();
}
```

#### Role-to-Convention Category Mapping

```csharp
private static readonly Dictionary<string, string[]> RoleConventionCategories = new()
{
    ["implementer"] = new[] { "naming", "errorHandling", "imports", "state", "dateTime", "logging" },
    ["reviewer"]    = new[] { "naming", "errorHandling", "imports", "testing", "state", "logging" },
    ["tester"]      = new[] { "testing", "naming", "imports" },
    ["mentor"]      = new[] { "naming", "errorHandling", "testing", "state" },
    ["analyst"]     = new[] { "naming", "errorHandling", "testing", "logging" },
    ["debugger"]    = new[] { "errorHandling", "logging", "imports" },
};
```

#### Built-In Convention Defaults

Extracted from the project's `CLAUDE.md`:

```csharp
private static readonly Dictionary<string, string> BuiltInConventions = new()
{
    ["naming"] = """
    ### Naming Conventions
    - Files & directories: kebab-case (event-store.ts, plugin-manager.ts)
    - Test files: *.test.ts (colocated with source)
    - Type definitions: *.types.ts
    - Interfaces: I prefix (IPluginManifest, IEventStore)
    - Classes: PascalCase (PluginManager, EventStore)
    - Functions: camelCase (evaluateCondition(), appendEvent())
    - Boolean functions: is/has/should prefix (isRetryable(), hasCapability())
    - Private functions: _ prefix (_validateSchema())
    - Constants: SCREAMING_SNAKE_CASE (MAX_RETRY_ATTEMPTS, DEFAULT_TIMEOUT_MS)
    """,

    ["errorHandling"] = """
    ### Error Handling
    - Use TammaError class with: code, message, context, retryable, severity
    - Async/await only -- NEVER use .then()/.catch()
    - All async operations must use retryWithBackoff pattern
    - Wrap external API calls in try/catch with structured error context
    """,

    ["imports"] = """
    ### Import Order (enforced)
    1. Node.js built-ins (import { readFile } from 'fs/promises')
    2. External dependencies (import dayjs from 'dayjs')
    3. Internal packages (import type { IEventStore } from '@tamma/shared/contracts')
    4. Relative imports (import { PluginManager } from '../plugin-manager')
    - All imports use .js extension (ESM)
    """,

    ["testing"] = """
    ### Testing
    - Framework: Vitest 3.x (NOT Jest)
    - Test files: colocated *.test.ts
    - Coverage targets: 80% line, 75% branch, 85% function
    - Critical paths (error handling, retry logic): 100% coverage
    - Mock HTTP: MSW (Mock Service Worker)
    - Mock modules: vi.mock() -- factory must be self-contained (hoisted)
    - Mock database: in-memory SQLite
    """,

    ["state"] = """
    ### State Management
    - NEVER mutate state -- always create new objects with spread
    - All dates use dayjs.utc().toISOString() (ISO 8601 with millisecond precision)
    """,

    ["logging"] = """
    ### Logging (Pino)
    - Structured JSON format with: level, time, service, msg
    - Log levels: DEBUG (dev details), INFO (milestones), WARN (recoverable), ERROR (failures)
    - NEVER log API keys, tokens, passwords, or other credentials
    - Always include correlation IDs (issueId, workflowId, sessionId)
    """,

    ["dateTime"] = """
    ### Date/Time
    - Library: dayjs with utc plugin (NOT moment, NOT Date)
    - Format: ISO 8601 with millisecond precision
    - Always use UTC: dayjs.utc().toISOString()
    """,
};
```

## Phase 3: Integration into ResolveAgentConfigActivity (P0)

### Modified Files

#### `Tamma.Activities/LlmCall/ResolveAgentConfigActivity.cs`

Add `PromptTemplateRegistry` and `ConventionProvider` as constructor dependencies. Insert a new resolution step between DB lookup (Priority 2) and hardcoded fallback (Priority 3):

```csharp
// Priority 2.5: Template Registry with convention injection  <-- NEW
try
{
    var promptRegistry = context.GetService<PromptTemplateRegistry>();
    var conventionProvider = context.GetService<ConventionProvider>();

    if (promptRegistry != null)
    {
        var operation = AgentOperationProp?.Get(context) ?? "*";
        var template = promptRegistry.Resolve(role, operation);

        if (template != null)
        {
            var conventions = conventionProvider?.GetConventionsForRole(role) ?? "";
            var variables = BuildVariables(context, role, conventions);
            var rendered = promptRegistry.Render(template, variables);

            context.SetVariable("ResolvedSystemPrompt", PromptHardening.Harden(rendered));
            context.SetVariable("PromptTemplateVersion", template.Version);

            logger.LogInformation(
                "Resolved prompt from template registry: role={Role}, operation={Operation}, version={Version}",
                role, operation, template.Version);
            return;
        }
    }
}
catch (Exception ex)
{
    logger.LogWarning(ex, "Failed to resolve prompt from template registry for role '{Role}'", role);
}

// Priority 3: Hardcoded fallback (unchanged)
```

#### New Input Property

Add an optional `AgentOperationProp` input to `ResolveAgentConfigActivity`:

```csharp
[Input(Description = "Operation name for template resolution (e.g. 'plan_generation', 'code_review')")]
public Input<string?> AgentOperationProp { get; set; } = default!;
```

#### Variable Builder

```csharp
private Dictionary<string, string> BuildVariables(
    ActivityExecutionContext context, string role, string conventions)
{
    var vars = new Dictionary<string, string>
    {
        ["conventions"] = conventions,
        ["role"] = role,
    };

    // Skill level (from workflow variable or default 3)
    var skillLevel = context.GetVariable<int?>("SkillLevel") ?? 3;
    vars["skillLevel"] = skillLevel.ToString();

    // Mentor approach (only relevant for mentor role)
    if (role.Equals("mentor", StringComparison.OrdinalIgnoreCase))
    {
        vars["mentorApproach"] = BuiltInPrompts.GetMentorApproach(skillLevel);
    }

    return vars;
}
```

### Modified: LlmCallWorkflow.cs

Add the `PromptTemplateVersion` to the output variables so it appears in workflow traces:

```csharp
// New variable
var promptTemplateVersionVar = builder.WithVariable<string>("PromptTemplateVersion", "");

// In SetOutputs, add:
WithLabel(new SetOutput
{
    Id = "OutputPromptVersion",
    Name = "Output: promptTemplateVersion",
    OutputName = new("promptTemplateVersion"),
    OutputValue = new(context => (object)(promptTemplateVersionVar.Get(context) ?? ""))
}, "Output: promptTemplateVersion"),
```

### Modified: LlmCallWorkflowOutput Model

Add `PromptTemplateVersion` property:

```csharp
public string? PromptTemplateVersion { get; set; }
```

## Phase 4: Enhanced Context Compaction (P1)

### Modified Files

#### `Tamma.Activities/LlmCall/Models/ConversationMessage.cs`

Add priority field:

```csharp
/// <summary>
/// Priority for context compaction. Higher priority messages are preserved longer.
/// </summary>
public MessagePriority Priority { get; set; } = MessagePriority.Normal;

public enum MessagePriority
{
    /// <summary>Never summarize (system prompt, critical error output).</summary>
    Critical = 0,
    /// <summary>Preserve if possible (test results, key decisions).</summary>
    Important = 1,
    /// <summary>Default priority.</summary>
    Normal = 2,
    /// <summary>Summarize first (verbose file contents, long logs).</summary>
    Low = 3,
}
```

#### `Tamma.Activities/LlmCall/Tools/ContextCompactor.cs`

Modify `CompactIfNeeded` to respect message priorities:

1. When selecting messages to summarize, sort by priority (Low first, then Normal)
2. Never summarize Critical messages
3. Include the current role in the summarization prompt so the LLM knows what information to preserve

Change the summarization prompt to be role-aware:

```csharp
internal static string BuildSummarizationPrompt(
    List<ConversationMessage> messages,
    string? currentRole = null)   // <-- NEW parameter
{
    var sb = new StringBuilder();
    sb.AppendLine("Summarize the following conversation history between an AI assistant and its tools.");
    sb.AppendLine("Preserve all key information: what was asked, what files were read/written, what commands");
    sb.AppendLine("were run, what errors occurred, and what decisions were made. Be concise but complete.");

    if (!string.IsNullOrEmpty(currentRole))
    {
        sb.AppendLine();
        sb.AppendLine($"The current task role is '{currentRole}'. Prioritize preserving information");
        sb.AppendLine("that is most relevant to this role's work.");
    }
    // ... rest of method unchanged
}
```

#### Tool Output Priority Assignment

When the agentic tool loop adds tool results to the conversation, assign priorities:

```csharp
// In CallLlmInlineActivity.AgenticToolLoop, when adding tool results:
var priority = toolCall.ToolName switch
{
    "run_tests" => MessagePriority.Important,   // Test results are critical context
    "file_read" when result.Output?.Length > 5000 => MessagePriority.Low,  // Long file reads
    "shell_execute" when result.Output?.Length > 3000 => MessagePriority.Low,  // Verbose CLI output
    _ => MessagePriority.Normal,
};

messages.Add(new ConversationMessage
{
    Role = "tool",
    Content = toolOutput,
    ToolCallId = toolCall.Id,
    ToolName = toolCall.ToolName,
    Priority = priority,    // <-- NEW
});
```

## Phase 5: Prompt Versioning (P1)

### Approach

Each `PromptTemplate` already has a `Version` property. The version flows through the pipeline:

1. `PromptTemplateRegistry.Resolve()` returns the template with its version
2. `ResolveAgentConfigActivity` sets `PromptTemplateVersion` workflow variable
3. `LlmCallWorkflow` includes `promptTemplateVersion` in the output
4. `LlmCallWorkflowOutput` carries `PromptTemplateVersion` for storage/display

No additional infrastructure needed -- the version is a string that can be pinned in the ELSA Agents DB via the `ResponseFormat` JSON field (already used for `ProviderChain`):

```json
{
  "providerChain": ["anthropic", "openai"],
  "promptTemplateVersion": "implementer.v2"
}
```

When `AgentCustomSettings` has a `PromptTemplateVersion`, the registry resolves that specific version instead of the latest.

## Phase 6: Few-Shot Examples (P1)

### New Files

#### `Tamma.Activities/LlmCall/Prompts/FewShotProvider.cs`

```csharp
namespace Tamma.Activities.LlmCall.Prompts;

/// <summary>
/// Provides few-shot examples from successful past LLM calls.
///
/// Examples are stored in a simple file-based store:
///   .tamma/examples/{role}/{operation}.json
///
/// Each example is a (input, output) pair with metadata:
///   - role, operation, version
///   - token count (to respect context limits)
///   - success indicator (only successful calls become examples)
///
/// Selection strategy:
///   1. Filter by role + operation
///   2. Sort by recency
///   3. Pick top N examples that fit within the token budget
/// </summary>
public class FewShotProvider
{
    public const int DefaultMaxExamples = 2;
    public const int DefaultMaxTokensPerExample = 2000;

    /// <summary>
    /// Get few-shot examples formatted for prompt injection.
    /// Returns empty string if no examples are available.
    /// </summary>
    public string GetExamples(
        string role,
        string operation,
        int maxExamples = DefaultMaxExamples,
        int maxTokensBudget = DefaultMaxTokensPerExample * DefaultMaxExamples);
}
```

This is a simple implementation -- examples are curated files, not automatically generated from past runs. Automatic example collection is a future story.

## Files to Modify (Summary)

### New Files (6)

| File | Purpose |
|------|---------|
| `Tamma.Activities/LlmCall/Prompts/PromptTemplateRegistry.cs` | Template storage and resolution |
| `Tamma.Activities/LlmCall/Prompts/PromptTemplate.cs` | Template data model |
| `Tamma.Activities/LlmCall/Prompts/BuiltInPrompts.cs` | 6 role-specific prompt templates |
| `Tamma.Activities/LlmCall/Prompts/ConventionProvider.cs` | Project convention loading and formatting |
| `Tamma.Activities/LlmCall/Prompts/FewShotProvider.cs` | Few-shot example selection |
| `Tamma.Activities.Tests/LlmCall/Prompts/PromptTemplateRegistryTests.cs` | Unit tests |

### Modified Files (6)

| File | Change |
|------|--------|
| `Tamma.Activities/LlmCall/ResolveAgentConfigActivity.cs` | Add template registry as Priority 2.5 fallback; add AgentOperationProp input; add PromptTemplateVersion output |
| `Tamma.Activities/LlmCall/Models/LlmCallWorkflowOutput.cs` | Add PromptTemplateVersion property |
| `Tamma.Activities/LlmCall/Models/ConversationMessage.cs` | Add Priority field |
| `Tamma.Activities/LlmCall/Tools/ContextCompactor.cs` | Priority-aware summarization; role-aware summarization prompt |
| `Tamma.ElsaServer/Workflows/LlmCallWorkflow.cs` | Add PromptTemplateVersion variable and output |
| `Tamma.ElsaServer/Program.cs` (or DI registration) | Register PromptTemplateRegistry, ConventionProvider, FewShotProvider |

### Not Modified (preserved as-is)

| File | Reason |
|------|--------|
| `CallLlmInlineActivity.cs` | No changes needed -- it consumes `ResolvedSystemPrompt` which is now richer |
| `CallLlmActivity.cs` | No changes needed -- same reason |
| `ResolveLlmPromptActivity.cs` | Deprecated path -- still works for callers using it directly |
| `PromptHardening.cs` | No changes needed -- hardening is applied after template rendering |
| `AdlOrchestratorWorkflow.cs` | No changes needed -- operates at a higher level |

## Documented Flow Issues

### 1. SingleIssueCycleWorkflow: CI Retry Counter Bug

**Location**: `SingleIssueCycleWorkflow.cs`, lines 349-351

**Issue**: The `ciRetryCount` variable is passed to the `ci-with-debug-retry` sub-workflow and its returned value is preserved across re-entries. When the workflow loops back to CI from `reviewFixCheck` (line 682: `HasComments=True -> dispatchCiRetry`) or from `mergeApproval` (line 689: `testDecision=True -> dispatchCiRetry`), the counter is not reset. This means the CI retry budget from the first pass is partially consumed, potentially causing the second CI run to fail prematurely.

**Impact**: After a review-fix cycle that pushes new commits, the CI pipeline has fewer retries available than it should (e.g., 1 remaining instead of 3).

**Fix**: Add a `SetVariable` node to reset `ciRetryCount = 0` before both re-entry paths to `dispatchCiRetry`. This requires two new connections:
- `HasReviewComments=True -> ResetCiRetryCount -> dispatchCiRetry`
- `testDecision=True -> ResetCiRetryCount2 -> dispatchCiRetry`

### 2. MentorshipWorkflow: Skill Level Never Updated

**Location**: `MentorshipWorkflow.cs`, sub-workflow dispatch blocks

**Issue**: All sub-workflow dispatches hardcode `["skillLevel"] = 3`:
- Testing (line 360): `["SkillLevel"] = 3`
- Assessment (line 393): `["skillLevel"] = 3`
- Blocker Diagnosis (line 410): `["skillLevel"] = 3`
- TDD (line 429): `["skillLevel"] = 3`
- Debugging (line 447): `["skillLevel"] = 3`

The `assessJunior` activity produces outcomes (Correct/Partial/Incorrect) that should update the skill level, but no mechanism stores the assessed level back into a workflow variable for use by downstream dispatches.

**Fix**: Add a `skillLevel` workflow variable. After `assessJunior` produces a result, extract the assessed skill level from the assessment output and update the variable. All sub-workflow dispatches should read from this variable instead of using `3`.

### 3. MentorshipWorkflow: LLM Call Uses Static Prompt

**Location**: `MentorshipWorkflow.cs`, lines 319-332

**Issue**: The `DispatchLlmCall` always sends the same static inputs:
```csharp
["agentRole"] = "mentor",
["taskPrompt"] = "Generate plan decomposition",
```

This is a single LLM call dispatch used after assessment succeeds (connection 10: `Correct -> llmCallWorkflow`). The prompt should be dynamically constructed based on the issue context, assessment results, and current plan iteration.

**Fix**: Replace the static `taskPrompt` with a dynamic construction that includes the issue description, context gathered, and assessment results. This should use the new `PromptTemplateRegistry` to resolve the appropriate template for plan generation.

### 4. ADL Orchestrator: No Error Handling for Sub-Workflow Failures

**Location**: `AdlOrchestratorWorkflow.cs`

**Issue**: The `DispatchIssueCycle` dispatch (line 118) waits for completion and parses the result, but the only exit conditions checked are `"success"` (increment counter) and `"noIssues"` (stop loop). If the cycle exits with `"error"`, `"tddFailed"`, `"ciFailed"`, or `"mergeFailed"`, the orchestrator treats it as "issues exist, keep looping" and starts the cooldown. This means a systematic failure (e.g., GitHub API down) causes infinite retries with only a 10-second cooldown.

**Impact**: Resource waste and potential rate limit exhaustion when underlying infrastructure is broken.

**Recommendation**: Add exit conditions for consecutive failures. If N consecutive cycles exit with the same error reason, the orchestrator should stop and report the pattern rather than retrying indefinitely. This is a separate story but worth tracking.

## Testing Strategy

### Unit Tests

1. **PromptTemplateRegistry**: registration, resolution order, interpolation, missing variables
2. **ConventionProvider**: category filtering, role mapping, fallback to built-in defaults
3. **BuiltInPrompts**: each template composes correctly, mentor approach varies by skill level
4. **ContextCompactor** (enhanced): priority-based message selection, role-aware summarization

### Integration Tests

1. **ResolveAgentConfigActivity with registry**: verify the Priority 2.5 path works end-to-end
2. **Full LlmCallWorkflow with template**: verify PromptTemplateVersion appears in output
3. **Convention injection**: verify conventions are present in the resolved system prompt

### Prompt Quality Tests (Manual)

1. Send the same task to the LLM with old prompts and new prompts
2. Compare output quality (format adherence, convention compliance, reasoning depth)
3. Document results in `.dev/findings/prompt-quality-comparison.md`

---

**Last Updated**: 2026-03-31
**Story**: 12-5
**Phase**: Implementation Planning
**Status**: Ready for Implementation
