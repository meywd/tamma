# Epic 11: Security Hardening

**Status:** Done
**Stories:** 5 (11-1 through 11-5)

## Overview

Epic 11 implements security hardening for the ELSA workflow engine, porting the TypeScript `ContentSanitizer` to C# and adding defense-in-depth at every boundary: LLM input sanitization, tool call validation, output sanitization with prompt hardening, and fail-closed guards with provider allowlists.

## Goals

1. Port ContentSanitizer to C# for ELSA workflows
2. Sanitize all inputs to LLM providers
3. Validate tool calls against schemas and allowlists
4. Sanitize outputs and harden prompts against injection
5. Implement fail-closed guards and provider allowlists

## Stories

| Story | Title | Status |
|-------|-------|--------|
| 11-1 | ContentSanitizer C# Port | Done |
| 11-2 | LLM Input Sanitization | Done |
| 11-3 | Tool Call Validation | Done |
| 11-4 | Output Sanitization & Prompt Hardening | Done |
| 11-5 | Fail-Closed Guards & Provider Allowlist | Done |

## Key Technical Details

- **ContentSanitizer**: C# port of the TypeScript sanitizer, integrated into ELSA activities
- **LLM Input Sanitization**: All prompts sanitized before sending to AI providers
- **Tool Call Validation**: Tool calls validated against registered schemas; unknown tools rejected
- **Output Sanitization**: AI responses sanitized before being used in code generation or Git operations
- **Fail-Closed Guards**: If sanitization fails, the operation is blocked (not silently allowed)
- **Provider Allowlist**: Only explicitly allowed providers can be used in production

## Dependencies

| Dependency | Epic | Reason |
|-----------|------|--------|
| Content Sanitization (TS) | Epic 9 | Original TypeScript implementation ported to C# |
| ELSA Workflows | Epic 7 | Security integrated into ELSA activities |
| AI Providers | Epic 1 | Provider allowlist and input/output sanitization |

## Related Epics

This epic is part of the ELSA workflow engine group (Epics 11-14). See also:
- [Epic 12: Agentic Tool Loop](Epics/Epic-12-Tool-Loop)
- [Epic 13: Workflow Decomposition](Epics/Epic-13-Workflow-Decomposition)
- [Epic 14: Custom ELSA Studio](Epics/Epic-14-ELSA-Studio)
- [Combined page: Epics 11-14](Epics/Epic-11-14-ELSA)

## Story Files

[Story documents on GitHub](https://github.com/meywd/tamma/tree/main/docs/stories/epic-11)
