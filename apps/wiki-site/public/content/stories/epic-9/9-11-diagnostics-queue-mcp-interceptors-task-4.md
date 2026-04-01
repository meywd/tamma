---
title: "Task 4: Create Built-in Interceptor Factories"
sidebar:
  order: 90
---

**Story:** 9-11-diagnostics-queue-mcp-interceptors - Diagnostics Queue & MCP Interceptors
**Epic:** 9

## Task Description

Add built-in interceptor factory functions and their supporting interfaces to `packages/mcp-client/src/interceptors.ts`: `createSanitizationInterceptor()` and `createUrlValidationInterceptor()`. Also update `packages/mcp-client/src/index.ts` to export all interceptor types and classes.

## Acceptance Criteria

- `createSanitizationInterceptor(sanitizer: IContentSanitizer)` uses `IContentSanitizer` (I-prefix) from Story 9-7, which returns `{ result: string; warnings: string[] }` -- local `ContentSanitizer` interface removed (F10)
- `createUrlValidationInterceptor(validateUrl)` uses `validateUrl()` from Story 9-7, which returns `{ valid: boolean; warnings: string[] }` -- separate `UrlValidator` interface removed (F11)
- `createSanitizationInterceptor` returns a valid `PostInterceptor` that sanitizes text content in `ToolResult`
- `createUrlValidationInterceptor` returns a valid `PreInterceptor` that validates URL-like values in tool args
- All types, classes, and factories exported from `packages/mcp-client/src/index.ts`

## Implementation Details

### Technical Requirements

- [ ] Import `IContentSanitizer` from Story 9-7 (which returns `{ result: string; warnings: string[] }`) -- do NOT define local `ContentSanitizer` interface (F10)
- [ ] Implement `createSanitizationInterceptor(sanitizer: IContentSanitizer): PostInterceptor` (F10):
  - Returns a `PostInterceptor` function
  - Iterates `result.content` array
  - For each `ToolResultContent` with `type: 'text'`, calls `sanitizer.sanitize(item.text)` which returns `{ result: sanitized, warnings: w }`
  - Collects warnings from each sanitization call
  - Returns a new `ToolResult` with sanitized content (does not mutate input)
  - Returns accumulated warnings array
- [ ] Implement `createUrlValidationInterceptor(validateUrl: (url: string) => { valid: boolean; warnings: string[] }): PreInterceptor` (F11):
  - Returns a `PreInterceptor` function
  - Scans top-level arg values for strings that look like URLs (contain `://` or start with `http`)
  - For each URL-like value, calls `validateUrl(url)` which returns `{ valid, warnings }`
  - Collects warnings from validation
  - If `valid` is `false`, adds additional warning `"URL blocked by policy: {url}"`
  - Does NOT modify args -- only reports warnings (blocking is the caller's responsibility)
  - Returns original args with accumulated warnings
- [ ] Update `packages/mcp-client/src/index.ts` to export:
  - `PreInterceptor` type
  - `PostInterceptor` type
  - `ToolInterceptorChain` class
  - `createSanitizationInterceptor` function
  - `createUrlValidationInterceptor` function

### Files to Modify/Create

- `packages/mcp-client/src/interceptors.ts` -- **MODIFY** -- Add interfaces and factory functions
- `packages/mcp-client/src/index.ts` -- **MODIFY** -- Add interceptor exports

### Dependencies

- [ ] Task 3 must be completed first (interceptors.ts with base types must exist)
- [ ] `packages/mcp-client/src/types.ts` must export `ToolResult` and `ToolResultContent` (already does)

## Testing Strategy

### Unit Tests

- [ ] Test `createSanitizationInterceptor` returns a function matching `PostInterceptor` type
- [ ] Test sanitization interceptor sanitizes text content in a `ToolResult` with text items
- [ ] Test sanitization interceptor does not modify non-text content (e.g., image, resource)
- [ ] Test sanitization interceptor returns new `ToolResult` object (does not mutate input)
- [ ] Test sanitization interceptor adds warning when content was modified
- [ ] Test sanitization interceptor returns empty warnings when no modification needed
- [ ] Test sanitization interceptor handles empty content array
- [ ] Test `createUrlValidationInterceptor` returns a function matching `PreInterceptor` type
- [ ] Test URL validation interceptor adds warning for blocked URL in args
- [ ] Test URL validation interceptor returns no warnings for allowed URLs
- [ ] Test URL validation interceptor ignores non-string arg values
- [ ] Test URL validation interceptor ignores strings that are not URL-like
- [ ] Test URL validation interceptor checks multiple arg values
- [ ] Test URL validation interceptor returns original args (does not modify them)

### Validation Steps

1. [ ] Add interfaces and factories to interceptors.ts
2. [ ] Update index.ts with new exports
3. [ ] Run `pnpm --filter @tamma/mcp-client run typecheck` -- must pass
4. [ ] Write unit tests in `packages/mcp-client/src/interceptors.test.ts` (created in Task 6)
5. [ ] Run `pnpm vitest run packages/mcp-client/src/interceptors`
6. [ ] Verify all interceptor types importable from `@tamma/mcp-client`

## Notes & Considerations

- The `createSanitizationInterceptor` is a **post**-interceptor because it sanitizes the result coming back from the tool, not the arguments going to it.
- The `createUrlValidationInterceptor` is a **pre**-interceptor because it validates arguments before they are sent to the tool.
- The URL validation interceptor only reports warnings and does NOT block execution or modify args. This is a design choice: the caller (or a higher-level policy layer) decides what to do with the warnings. A future enhancement could add a `block: boolean` option.
- The sanitization interceptor must handle all `ToolResultContent` types gracefully: `text`, `image`, and `resource`. Only `text` type has a `text` field to sanitize.
- `IContentSanitizer` (from Story 9-7) returns `{ result: string; warnings: string[] }`, enabling the interceptor to collect sanitization warnings per content item. This replaces the earlier `ContentSanitizer` interface which returned a plain string (F10).
- `validateUrl()` (from Story 9-7) returns `{ valid: boolean; warnings: string[] }`, enabling the interceptor to collect validation warnings. This replaces the earlier `UrlValidator` interface with `isAllowed()` (F11).

## Completion Checklist

- [ ] `IContentSanitizer` imported from Story 9-7 (local `ContentSanitizer` removed) (F10)
- [ ] `validateUrl` function signature used directly (local `UrlValidator` interface removed) (F11)
- [ ] `createSanitizationInterceptor()` implemented using `IContentSanitizer.sanitize()` which returns `{ result, warnings }` (F10)
- [ ] `createUrlValidationInterceptor()` implemented using `validateUrl()` which returns `{ valid, warnings }` (F11)
- [ ] `packages/mcp-client/src/index.ts` updated with all interceptor exports
- [ ] Sanitization interceptor sanitizes text content without mutating input
- [ ] URL validation interceptor reports warnings without modifying args
- [ ] TypeScript strict mode compilation passes
- [ ] All types importable from `@tamma/mcp-client`
