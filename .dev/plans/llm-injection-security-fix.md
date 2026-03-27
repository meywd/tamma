# Plan: LLM Injection Security Hardening

## Summary
Port TypeScript security pipeline to C# ELSA layer. 9 vulnerabilities, 8 phases, ~10-13 days.

## Phase 1: C# ContentSanitizer + ErrorRedactor (CRITICAL, 2-3 days)
- New: `Tamma.Activities/Security/IContentSanitizer.cs`, `ContentSanitizer.cs`
- Port from `packages/shared/src/security/content-sanitizer.ts`
- SanitizeInput(): null bytes, HTML stripping, zero-width chars, NFKD normalization, 40+ injection patterns
- SanitizeOutput(): null bytes, zero-width chars, HTML stripping preserving code blocks
- New: `ErrorRedactor.cs` — strip API keys, internal URLs from error bodies
- DI registration in Program.cs
- 30+ tests

## Phase 2: Input Sanitization (CRITICAL, 2-3 days)
- Inject IContentSanitizer into CallLlmInlineActivity + CallLlmActivity
- Sanitize system+user prompts before API call
- Sanitize in workflow prompt builders: PlanGenerationWorkflow.BuildPlanPrompt(), BlockerDiagnosisWorkflow.BuildDiagnosisPrompt(), ReviewFixWorkflow, DebuggingWorkflow
- Static helper: SecurityHelpers.SanitizeForPrompt() for workflow lambda contexts
- 15+ tests

## Phase 3: Tool Call Validation (CRITICAL, 2 days)
- New: `IToolCallValidator.cs`, `ToolCallValidator.cs`
- Allowlist: tool name must match sent tools
- Name format: `^[a-zA-Z0-9_-]{1,64}$`
- Argument schema validation + size limit (100KB)
- Sanitize string-valued arguments
- New: `ActionGate.cs` — blocked command patterns for shell tools
- 20+ tests

## Phase 4: Output Sanitization (HIGH, 1 day)
- Sanitize NormalizedLlmResponse.ResponseText via SanitizeOutput()
- Redact error bodies with ErrorRedactor before storing in diagnostics
- 8+ tests

## Phase 5: System Prompt Hardening (HIGH, 1 day)
- New: `PromptHardening.cs` — anti-extraction preamble constant
- Apply Harden() in ResolveAgentConfigActivity (3 paths) + ResolveLlmPromptActivity (6 levels)
- Sanitize systemPromptOverride input
- 10+ tests

## Phase 6: Fail-Closed Guards (MEDIUM, 1 day)
- LlmCallWorkflow.IsCircuitBreakerOpen() catch → return true (was false)
- LlmCallWorkflow.IsBudgetExhausted() catch → return true (was false)
- Same for CheckCircuitBreakerActivity + CheckBudgetActivity
- 8+ tests

## Phase 7: Provider Name Allowlist (MEDIUM, 0.5 days)
- New: `ProviderAllowlist.cs` — HashSet of known providers
- Filter in ResolveChain step + LoadProviderConfig()
- 6+ tests

## Phase 8: Integration Testing (2 days)
- End-to-end injection scenarios (8 attack simulations)
- Regression tests for normal operation

## Dependency Graph
```
Phase 1 (foundation) ──→ Phase 2 + Phase 3 (parallel) ──→ Phase 4 + Phase 5 ──→ Phase 8
Phase 6 + Phase 7 (independent, parallel with Phase 1) ──→ Phase 8
```

## New Files: 16 (10 source + 6 test files)
## Modified Files: 12
