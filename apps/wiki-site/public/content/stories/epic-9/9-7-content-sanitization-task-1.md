---
title: "Task 1: Implement IContentSanitizer Interface and ContentSanitizer Class"
sidebar:
  order: 90
---

**Story:** 9-7-content-sanitization - Content Sanitization
**Epic:** 9

## Task Description

Create the `IContentSanitizer` interface and `ContentSanitizer` class in `packages/shared/src/security/content-sanitizer.ts`. The interface is extracted for DIP consistency with `IProviderHealthTracker` (9-3), `IAgentProviderFactory` (9-4), `IProviderChain` (9-5), `IAgentPromptRegistry` (9-6). The class provides instance methods for sanitizing input content before it reaches LLM prompts and sanitizing output content returned from the LLM. The class uses a quote-aware state machine for HTML stripping (not simple indexOf, not regex), removal of 20+ invisible Unicode code points, and 5-category prompt injection pattern detection with NFKD normalization. It **never throws** -- suspicious content is reported via a warnings array in the return value.

The class is intentionally a **class** (not free functions) because it holds configuration state (`ContentSanitizerOptions`) and `SecureAgentProvider` holds an instance of it.

## Acceptance Criteria

- `IContentSanitizer` interface exported with `sanitize()` and `sanitizeOutput()` method signatures
- `ContentSanitizerOptions` interface exported with `enabled?`, `extraInjectionPatterns?`, `logger?`
- `ContentSanitizer` is a class implementing `IContentSanitizer` with constructor accepting `ContentSanitizerOptions`
- `sanitize()` returns `{ result: string; warnings: string[] }` -- never throws
- `sanitizeOutput()` returns `{ result: string; warnings: string[] }` -- never throws
- HTML tags stripped from input using quote-aware state machine (handles `<div title="a>b">` correctly, NOT simple indexOf, NOT regex)
- Zero-width and invisible character removal covers at minimum 20+ Unicode code points including bidi overrides (CVE-2021-42574) and soft hyphens
- Prompt injection patterns detected across 5 categories and returned as warning strings
- `detectPromptInjection` applies Unicode NFKD normalization before pattern matching
- `sanitizeOutput()` preserves content within ``` code blocks while stripping HTML outside them
- When `options.enabled === false`, null bytes still removed (hard safety requirement) but HTML stripping and injection detection skipped
- `extraInjectionPatterns` are additive to built-in defaults (not replacement)
- All methods compile under TypeScript strict mode

## Implementation Details

### Technical Requirements

- [ ] Create directory `packages/shared/src/security/`
- [ ] Create `packages/shared/src/security/content-sanitizer.ts`
- [ ] Define `IContentSanitizer` interface:

```typescript
export interface IContentSanitizer {
  sanitize(input: string): { result: string; warnings: string[] };
  sanitizeOutput(output: string): { result: string; warnings: string[] };
}
```

- [ ] Define `ContentSanitizerOptions` interface:

```typescript
export interface ContentSanitizerOptions {
  /** When false, sanitize() returns input unchanged (still removes null bytes). Default: true */
  enabled?: boolean;
  /** Additional injection patterns beyond built-in defaults (additive, not replacement) */
  extraInjectionPatterns?: readonly string[];
  /** Logger for warnings */
  logger?: ILogger;
}
```

- [ ] Implement `ContentSanitizer` class implementing `IContentSanitizer`:

```typescript
export class ContentSanitizer implements IContentSanitizer {
  constructor(private readonly options?: ContentSanitizerOptions) {}

  sanitize(input: string): { result: string; warnings: string[] };
  sanitizeOutput(output: string): { result: string; warnings: string[] };

  private stripHtml(input: string): string;
  private stripHtmlPreserveCode(input: string): string;
  private removeZeroWidthChars(input: string): string;
  private detectPromptInjection(input: string): string[];
}
```

- [ ] `sanitize()` pipeline: null byte removal (always) -> if enabled: stripHtml -> removeZeroWidthChars -> detectPromptInjection (collect warnings)
- [ ] `sanitizeOutput()` pipeline: null byte removal (always) -> if enabled: removeZeroWidthChars -> stripHtmlPreserveCode (lighter-touch on output)
- [ ] When `options.enabled === false`: still remove null bytes (hard safety requirement) but skip HTML stripping and injection detection
- [ ] `stripHtml()` must use a **quote-aware state machine** (NOT simple indexOf, NOT regex):

```typescript
private stripHtml(input: string): string {
  let result = '';
  let i = 0;
  while (i < input.length) {
    const start = input.indexOf('<', i);
    if (start === -1) { result += input.slice(i); break; }
    result += input.slice(i, start);
    // Find closing >, respecting quoted attributes
    let j = start + 1;
    let inSingle = false, inDouble = false;
    while (j < input.length) {
      const ch = input[j];
      if (ch === '"' && !inSingle) inDouble = !inDouble;
      else if (ch === "'" && !inDouble) inSingle = !inSingle;
      else if (ch === '>' && !inSingle && !inDouble) break;
      j++;
    }
    // Handle unclosed tag: if no > found, strip from < to end
    i = j < input.length ? j + 1 : input.length;
  }
  return result;
}
```

- [ ] `stripHtmlPreserveCode()` must split on triple-backtick boundaries, only strip HTML in non-code segments
- [ ] `removeZeroWidthChars()` must remove at minimum 20+ invisible Unicode code points:
  - U+200B (zero-width space), U+200C (zero-width non-joiner), U+200D (zero-width joiner), U+FEFF (BOM)
  - U+200E, U+200F (directional marks)
  - U+202A-U+202E (bidi overrides -- trojan-source CVE-2021-42574)
  - U+2060-U+2069 (word joiner, bidi isolates)
  - U+00AD (soft hyphen)
  - U+034F (combining grapheme joiner)
  - U+0000 (null byte)
  - U+2028, U+2029 (line/paragraph separators)
  - U+FFFC (object replacement character)
- [ ] `detectPromptInjection()` must detect 5 categories of patterns:
  1. **Instruction override**: "ignore previous instructions", "disregard above", "forget your instructions"
  2. **Role hijacking**: "you are now", "act as", "pretend to be"
  3. **System prompt extraction**: "repeat your system prompt", "what are your instructions"
  4. **Delimiter injection**: "```system", "###SYSTEM###", "[INST]"
  5. **Encoding evasion**: detected via Unicode NFKD normalization before pattern matching
  - Apply Unicode NFKD normalization before pattern matching
  - Use built-in patterns + `options.extraInjectionPatterns` (additive, not replacement)
  - Return array of warning strings describing each detected pattern
  - Note: patterns are heuristic defense-in-depth, not a guarantee. Document this.

### Files to Modify/Create

- `packages/shared/src/security/content-sanitizer.ts` -- **CREATE** -- ContentSanitizer class

### Dependencies

- None (this is a standalone module with no external dependencies)

## Testing Strategy

### Unit Tests

#### Interface and constructor tests
- [ ] Test `IContentSanitizer` interface is exported
- [ ] Test `ContentSanitizer` implements `IContentSanitizer`
- [ ] Test `ContentSanitizer` accepts `ContentSanitizerOptions` in constructor
- [ ] Test `ContentSanitizer` works with no options (defaults)
- [ ] Test `ContentSanitizer` with `enabled: false` still removes null bytes from `sanitize()`
- [ ] Test `ContentSanitizer` with `enabled: false` still removes null bytes from `sanitizeOutput()`
- [ ] Test `ContentSanitizer` with `enabled: false` skips HTML stripping in `sanitize()`
- [ ] Test `ContentSanitizer` with `enabled: false` skips injection detection in `sanitize()`
- [ ] Test `ContentSanitizer` with `extraInjectionPatterns` extends built-in defaults (additive)

#### HTML stripping tests (quote-aware state machine)
- [ ] Test `sanitize()` strips `<script>alert('xss')</script>` to `alert('xss')`
- [ ] Test `sanitize()` strips `<img src=x onerror=alert(1)>` completely
- [ ] Test `sanitize()` strips `<div><b>bold</b></div>` to `bold`
- [ ] Test `sanitize()` handles nested tags
- [ ] Test `sanitize()` handles self-closing tags (`<br/>`, `<hr />`)
- [ ] Test `sanitize()` handles malformed HTML (unclosed `<div` without `>`)
- [ ] Test `sanitize()` handles quoted `>` in attributes: `<div title="a>b">content</div>` preserves "content"
- [ ] Test `sanitize()` handles unclosed tags at end of input

#### Zero-width character tests (20+ code points)
- [ ] Test `sanitize()` removes zero-width space U+200B from text
- [ ] Test `sanitize()` removes zero-width joiner U+200D from text
- [ ] Test `sanitize()` removes BOM U+FEFF from text
- [ ] Test `sanitize()` removes directional marks U+200E, U+200F
- [ ] Test `sanitize()` removes bidi overrides U+202A-U+202E (CVE-2021-42574)
- [ ] Test `sanitize()` removes word joiner U+2060 and bidi isolates U+2066-U+2069
- [ ] Test `sanitize()` removes soft hyphen U+00AD
- [ ] Test `sanitize()` removes combining grapheme joiner U+034F
- [ ] Test `sanitize()` removes null byte U+0000
- [ ] Test `sanitize()` removes line/paragraph separators U+2028, U+2029
- [ ] Test `sanitize()` removes object replacement character U+FFFC

#### Prompt injection tests (5 categories)
- [ ] Test `sanitize()` returns warning for "ignore previous instructions" (instruction override)
- [ ] Test `sanitize()` returns warning for "disregard above" (instruction override)
- [ ] Test `sanitize()` returns warning for "you are now a helpful assistant" (role hijacking)
- [ ] Test `sanitize()` returns warning for "act as" (role hijacking)
- [ ] Test `sanitize()` returns warning for "repeat your system prompt" (system prompt extraction)
- [ ] Test `sanitize()` returns warning for "```system" (delimiter injection)
- [ ] Test `sanitize()` returns warning for "###SYSTEM###" (delimiter injection)
- [ ] Test `sanitize()` detects encoding evasion via NFKD normalization
- [ ] Test `sanitize()` returns warning for "SYSTEM: override mode"
- [ ] Test `sanitize()` returns empty warnings for benign input
- [ ] Test `sanitize()` never throws on any input (empty string, null-like, huge string)

#### Output sanitization tests
- [ ] Test `sanitizeOutput()` preserves code within ``` blocks
- [ ] Test `sanitizeOutput()` strips HTML tags outside ``` blocks
- [ ] Test `sanitizeOutput()` removes zero-width characters
- [ ] Test `sanitizeOutput()` handles output with no code blocks (strips HTML everywhere)
- [ ] Test `sanitizeOutput()` handles output that is entirely a code block

### Validation Steps

1. [ ] Create the security directory and content-sanitizer.ts file
2. [ ] Implement all class methods
3. [ ] Write unit tests in `packages/shared/src/security/content-sanitizer.test.ts`
4. [ ] Run `pnpm --filter @tamma/shared run typecheck` -- must pass
5. [ ] Run `pnpm vitest run packages/shared/src/security/content-sanitizer`

## Notes & Considerations

- The quote-aware state machine for HTML stripping is more robust than simple `indexOf('>')` because it correctly handles HTML attributes with `>` characters inside quotes (e.g., `<div title="a>b">`). Simple indexOf would incorrectly treat the `>` inside the attribute as the tag close.
- The prompt injection detection is pattern-based heuristics, not a guarantee. The warnings inform the caller; they do not block execution. This is defense-in-depth. Document this explicitly in code comments.
- Unicode NFKD normalization before pattern matching prevents attackers from using compatibility characters (e.g., fullwidth Latin letters) to bypass detection.
- `sanitizeOutput()` is deliberately lighter-touch than `sanitize()` -- it preserves code blocks because LLM output often contains legitimate code.
- The `ContentSanitizerOptions.enabled` flag allows callers to disable most sanitization (e.g., when `SecurityConfig.sanitizeContent === false`) while still enforcing the hard safety requirement of null byte removal.
- The `extraInjectionPatterns` option is additive to built-in defaults, not a replacement. This ensures built-in protections are always present.
- Keep the implementation self-contained with zero external dependencies (except `ILogger` type from `@tamma/shared`). This module must be importable from `@tamma/shared` without pulling in additional packages.
- Use TypeScript `private` keyword for helper methods.

## Completion Checklist

- [ ] `packages/shared/src/security/content-sanitizer.ts` created
- [ ] `IContentSanitizer` interface exported with `sanitize()` and `sanitizeOutput()` methods
- [ ] `ContentSanitizerOptions` interface exported with `enabled?`, `extraInjectionPatterns?`, `logger?`
- [ ] `ContentSanitizer` implements `IContentSanitizer` and accepts `ContentSanitizerOptions` in constructor
- [ ] `stripHtml()` uses quote-aware state machine (handles quoted `>` in attributes)
- [ ] `removeZeroWidthChars()` handles 20+ invisible Unicode code points (U+200B-F, U+202A-E, U+2060-9, U+00AD, U+034F, U+0000, U+2028-9, U+FEFF, U+FFFC)
- [ ] `detectPromptInjection()` covers 5 categories with NFKD normalization
- [ ] `extraInjectionPatterns` additive to built-in defaults
- [ ] `sanitize()` and `sanitizeOutput()` never throw
- [ ] When `enabled === false`, null bytes still removed but other sanitization skipped
- [ ] `sanitizeOutput()` preserves ``` code blocks
- [ ] Unit tests written and passing
- [ ] TypeScript strict mode compilation passes
