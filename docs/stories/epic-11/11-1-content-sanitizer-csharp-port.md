# Story 11.1: ContentSanitizer C# Port

Status: ready-for-dev

## Story

As a **security engineer**,
I want a C# `ContentSanitizer` and `ErrorRedactor` ported from the existing TypeScript security pipeline,
so that the ELSA workflow layer has foundational sanitization primitives that all downstream stories can depend on.

## Acceptance Criteria

1. `IContentSanitizer` interface exists with `SanitizeInput(string)` and `SanitizeOutput(string)` methods
2. `ContentSanitizer` implementation strips null bytes, HTML tags, zero-width characters, and normalizes Unicode (NFKD)
3. `SanitizeInput()` detects and neutralizes 40+ prompt injection patterns (role hijacking, system prompt extraction, delimiter injection, encoding attacks)
4. `SanitizeOutput()` strips null bytes and zero-width characters, strips HTML while preserving code blocks
5. `IErrorRedactor` interface exists with `Redact(string)` method
6. `ErrorRedactor` strips API keys (Bearer tokens, sk-*, key-*, base64 blobs), internal URLs, and stack traces from error bodies
7. Both services registered in DI container via `Program.cs`
8. 30+ unit tests covering all sanitization patterns, edge cases, and bypass attempts
9. Sanitization is idempotent (sanitizing already-sanitized input produces the same output)
10. Performance: sanitization of a 10KB input completes in under 5ms

## Technical Context

### Source Implementation

The TypeScript implementation lives in `packages/shared/src/security/content-sanitizer.ts`. The C# port must achieve feature parity with the following categories:

**Input Sanitization Patterns (40+):**
- Null byte removal (`\0`, `%00`)
- HTML tag stripping (all tags, preserving text content)
- Zero-width character removal (U+200B, U+200C, U+200D, U+FEFF, U+00AD)
- Unicode NFKD normalization (prevents homoglyph attacks)
- Role hijacking: `"ignore previous instructions"`, `"you are now"`, `"new system prompt"`
- System prompt extraction: `"repeat your instructions"`, `"show me your system prompt"`, `"what are your rules"`
- Delimiter injection: `<|endoftext|>`, `<|im_start|>`, `[INST]`, `<<SYS>>`
- Encoding attacks: Base64-encoded payloads, URL-encoded sequences, Unicode escape sequences
- Markdown injection: invisible link payloads, image tag exfiltration attempts

**Output Sanitization:**
- Same null byte and zero-width removal as input
- HTML stripping that preserves content inside `<code>` and `<pre>` blocks
- No injection pattern detection (output is LLM-generated, not adversarial)

**Error Redaction Patterns:**
- Bearer tokens: `Bearer [A-Za-z0-9._-]+`
- OpenAI keys: `sk-[A-Za-z0-9]{20,}`
- Anthropic keys: `sk-ant-[A-Za-z0-9-]+`
- Generic keys: `key-[A-Za-z0-9]+`
- Internal URLs: `https?://(?:localhost|127\.0\.0\.1|10\.\d+|172\.(?:1[6-9]|2\d|3[01])|192\.168)[^\s]*`
- Stack traces: multi-line `at Namespace.Class.Method` blocks

### Files to Create

- `apps/tamma-elsa/src/Tamma.Activities/Security/IContentSanitizer.cs`
- `apps/tamma-elsa/src/Tamma.Activities/Security/ContentSanitizer.cs`
- `apps/tamma-elsa/src/Tamma.Activities/Security/IErrorRedactor.cs`
- `apps/tamma-elsa/src/Tamma.Activities/Security/ErrorRedactor.cs`

### Files to Modify

- `apps/tamma-elsa/src/Tamma.ElsaServer/Program.cs` (DI registration)

## Implementation Notes

1. Use `System.Text.RegularExpressions.Regex` with compiled patterns for performance. Pre-compile all 40+ patterns as `static readonly Regex` fields.
2. For Unicode normalization, use `string.Normalize(NormalizationForm.FormKD)`.
3. For HTML stripping, use a regex approach (not a full HTML parser) since inputs are plain text with potential injection, not valid HTML. Preserve text between `<code>` and `<pre>` tags by replacing them with placeholders before stripping.
4. The TypeScript implementation uses a pattern-matching approach where each injection pattern has a name, regex, and replacement. Port this as a `List<SanitizationRule>` with `Name`, `Pattern`, `Replacement` fields.
5. All methods must be thread-safe (no mutable state, compiled regexes are thread-safe).
6. Log at DEBUG level when any sanitization pattern matches, including the pattern name (never log the matched content itself).

## Testing Strategy

- **Unit tests** (30+): One test per injection pattern category, edge cases (empty string, null, whitespace-only, very long input, already-sanitized input), bypass attempts (double encoding, mixed encoding, split injection across multiple lines)
- **Performance tests**: Verify 10KB input sanitization completes in under 5ms (use `Stopwatch` in test, assert elapsed)
- **Idempotency tests**: `Sanitize(Sanitize(input)) == Sanitize(input)` for all test inputs
- **Error redaction tests**: API key patterns, internal URL patterns, stack trace removal, mixed content preservation
- **Test file**: `apps/tamma-elsa/tests/Tamma.Activities.Tests/Security/ContentSanitizerTests.cs`
- **Test file**: `apps/tamma-elsa/tests/Tamma.Activities.Tests/Security/ErrorRedactorTests.cs`

## Dependencies

- **None** (foundational story for Epic 11)

## Estimated Effort

2-3 days

## Change Log

| Date | Version | Changes | Author |
|------|---------|---------|--------|
| 2026-03-28 | 1.0 | Initial story creation from `.dev/plans/llm-injection-security-fix.md` Phase 1 | Architecture Team |
