# Epic 11: Security Hardening

## Overview

**Goal**: Harden the ELSA workflow layer against LLM injection attacks by porting the TypeScript security pipeline to C# and wiring sanitization, validation, and fail-closed guards into the LLM call path, tool execution, and prompt resolution activities.

**Value Delivered**:
- All LLM inputs sanitized (null bytes, HTML, zero-width chars, 40+ injection patterns)
- Tool call names validated against an allowlist; arguments schema-checked and size-capped
- LLM outputs sanitized before storage or display
- System prompts hardened against extraction attacks
- Fail-closed guards on circuit breaker and budget checks (errors deny, not allow)
- Provider names validated against a known allowlist
- Error bodies redacted to prevent internal URL and API key leakage
- 8 end-to-end attack simulation tests

## Stories

| Story | Title | Priority | Dependencies | Status |
|-------|-------|----------|-------------|--------|
| 11.1 | ContentSanitizer C# Port | P0 (Critical) | None | Planned |
| 11.2 | LLM Input Sanitization | P0 (Critical) | Story 11.1 | Planned |
| 11.3 | Tool Call Validation | P0 (Critical) | Story 11.1 | Planned |
| 11.4 | Output Sanitization & Prompt Hardening | P1 (High) | Stories 11.2, 11.3 | Planned |
| 11.5 | Fail-Closed Guards & Provider Allowlist | P1 (Medium) | None (parallel with 11.1) | Planned |

## Dependency Graph

```
Story 11.1 (foundation) --> Story 11.2 + Story 11.3 (parallel) --> Story 11.4
Story 11.5 (independent, parallel with Story 11.1) --> (integration tests in 11.4)
```

## Architecture

All new security code lives in `Tamma.Activities/Security/`. Sanitization is injected via DI into existing LLM call activities and workflow prompt builders. No structural workflow changes required.

## Source Plan

`.dev/plans/llm-injection-security-fix.md`

---

**Last Updated**: 2026-03-28
**Epic Owner**: Security Team
