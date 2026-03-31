---
title: "Task 3: Implement render() with {{variable}} Interpolation and Size Limits"
sidebar:
  order: 90
---

**Story:** 9-6-agent-prompt-registry - Agent Prompt Registry
**Epic:** 9

## Task Description

Implement the `render()` public method and the private `interpolate()` helper on `AgentPromptRegistry`. The `render()` method is the primary public API: it resolves the template for a given role and provider, then replaces `{{variable}}` placeholders with provided values. Interpolation uses `split+join` instead of regex for safety against ReDoS on untrusted template content. Size limits (`MAX_VAR_VALUE_LENGTH`, `MAX_TEMPLATE_LENGTH`) are enforced with warning logs.

## Acceptance Criteria

- `render(role, providerName, vars)` resolves the template via `resolveTemplate()` and returns the interpolated result
- `render()` defaults `vars` to `{}` when not provided
- Private `interpolate()` replaces `{{key}}` placeholders using `split('{{key}}').join(value)` -- NOT regex
- Unreferenced `{{placeholders}}` are left unchanged in the output (not removed, not throwing -- safe default)
- Unmatched `{{placeholders}}` in templates are left as-is in the rendered output
- Values containing regex special characters (e.g., `$`, `^`, `.`, `*`) are substituted literally
- Variables whose values exceed `MAX_VAR_VALUE_LENGTH` (100KB) are skipped with a warning log
- Rendered templates exceeding `MAX_TEMPLATE_LENGTH` (1MB) are truncated with a warning log
- Variables are applied iteratively in `Object.entries()` order; if variable A's replacement contains `{{B}}` and B is also provided, B WILL be expanded within A's value (documented behavior)
- Complete test file `packages/providers/src/agent-prompt-registry.test.ts` covers all render scenarios

## Implementation Details

### Technical Requirements

- [ ] Implement `render()` method on `AgentPromptRegistry`:
  ```typescript
  render(
    role: AgentType,
    providerName: string,
    vars: Record<string, string> = {},
  ): string {
    const template = this.resolveTemplate(role, providerName);
    return this.interpolate(template, vars);
  }
  ```
- [ ] Implement private `interpolate()` method with size limit enforcement:
  ```typescript
  /**
   * Replace `{{key}}` placeholders with values from vars.
   *
   * Variables are applied iteratively in Object.entries() order using
   * split+join. If variable A's replacement contains {{B}} and B is also
   * provided, B WILL be expanded within A's value. This is by design --
   * callers should sanitize values if this is undesirable.
   *
   * Variables whose values exceed MAX_VAR_VALUE_LENGTH are skipped with
   * a warning log.
   */
  private interpolate(template: string, vars: Record<string, string>): string {
    let result = template;
    for (const [key, value] of Object.entries(vars)) {
      if (value.length > MAX_VAR_VALUE_LENGTH) {
        this.logger?.warn('Skipping variable exceeding MAX_VAR_VALUE_LENGTH', {
          key,
          valueLength: value.length,
          limit: MAX_VAR_VALUE_LENGTH,
        });
        continue;
      }
      // Use split+join instead of regex for safety on untrusted template content
      result = result.split(`{{${key}}}`).join(value);
    }
    // Truncate if rendered template exceeds MAX_TEMPLATE_LENGTH
    if (result.length > MAX_TEMPLATE_LENGTH) {
      this.logger?.warn('Rendered template exceeds MAX_TEMPLATE_LENGTH, truncating', {
        length: result.length,
        limit: MAX_TEMPLATE_LENGTH,
      });
      result = result.slice(0, MAX_TEMPLATE_LENGTH);
    }
    return result;
  }
  ```
- [ ] Ensure `vars` parameter defaults to `{}` in the `render()` signature

### Files to Modify/Create

- `packages/providers/src/agent-prompt-registry.ts` -- **MODIFY** -- Add `render()` and `interpolate()` methods
- `packages/providers/src/agent-prompt-registry.test.ts` -- **CREATE/MODIFY** -- Complete test suite

### Dependencies

- [ ] Task 1: BUILTIN_TEMPLATES (frozen, 9 roles), GENERIC_FALLBACK, MAX_TEMPLATE_LENGTH, MAX_VAR_VALUE_LENGTH, FORBIDDEN_KEYS, IAgentPromptRegistry, AgentPromptRegistryOptions
- [ ] Task 2: `resolveTemplate()` and `registerBuiltin()` methods with security guards

## Testing Strategy

### Unit Tests

**Basic interpolation:**
- [ ] `render('architect', 'claude-code', { context: 'Issue description here' })` replaces `{{context}}` in the architect built-in template
- [ ] `render('implementer', 'claude-code', { issueNumber: '42' })` replaces `{{issueNumber}}` in the implementer built-in template

**Multiple variables:**
- [ ] Template with multiple `{{var}}` placeholders -- all are replaced in a single call
- [ ] Template with the same `{{var}}` appearing twice -- both occurrences are replaced

**Empty and missing vars:**
- [ ] `render('architect', 'claude-code')` (no vars argument) leaves `{{context}}` as-is in the output
- [ ] `render('architect', 'claude-code', {})` (empty vars object) leaves `{{context}}` as-is
- [ ] `render('reviewer', 'claude-code', { unused: 'value' })` -- unused vars are ignored, template unchanged

**Edge cases with values:**
- [ ] Value contains regex special characters: `{ context: 'foo$bar^baz.*' }` -- substituted literally, no regex interpretation
- [ ] Value contains `{{otherPlaceholder}}` syntax: `{ context: 'see {{details}}' }` -- iterative expansion behavior: if `details` is also in vars and processed after `context`, `{{details}}` WILL be expanded (documented behavior)
- [ ] Value is an empty string: `{ context: '' }` -- placeholder is replaced with empty string
- [ ] Key contains special characters: ignored gracefully (no crash)

**Size limit enforcement:**
- [ ] Variable value exceeding `MAX_VAR_VALUE_LENGTH` (100KB) is skipped with warning log
- [ ] Rendered template exceeding `MAX_TEMPLATE_LENGTH` (1MB) is truncated with warning log
- [ ] Multiple variables where one exceeds limit -- only the oversized one is skipped, others are applied normally

**Integration with resolution chain:**
- [ ] `render()` uses a per-provider-per-role template (level 1) when configured, and interpolates vars into it
- [ ] `render()` uses `GENERIC_FALLBACK` (level 6) when nothing is configured, and vars have no effect (no placeholders in fallback)

**Full end-to-end scenario:**
- [ ] Construct `AgentPromptRegistry` with a realistic `AgentsConfig`, call `render()` for different roles and providers, verify correct template selection and variable substitution

### Validation Steps

1. [ ] Implement `render()` and `interpolate()` methods
2. [ ] Run `pnpm --filter @tamma/providers run typecheck` -- must pass
3. [ ] Write complete test file at `packages/providers/src/agent-prompt-registry.test.ts`
4. [ ] Run `pnpm vitest run packages/providers/src/agent-prompt-registry` -- all tests pass
5. [ ] Verify no regex usage in interpolation (grep for `/new RegExp/` or `/\.replace\(/` in the file)

## Notes & Considerations

- `split+join` is used instead of regex because:
  - Regex `String.prototype.replace()` interprets `$` patterns in replacement strings (`$1`, `$&`, etc.)
  - Regex with user-provided patterns risks ReDoS (Regular Expression Denial of Service)
  - `split+join` is simpler, safer, and handles all edge cases correctly
- **Iterative expansion behavior**: Variables are applied iteratively in `Object.entries()` order. If variable A's replacement contains `{{B}}` and B is also provided, B WILL be expanded within A's value. This is by design -- the caller (engine) controls all variable values, not end users. Callers should sanitize values if indirect expansion is undesirable.
- **Size limits**: `MAX_VAR_VALUE_LENGTH` (100KB) prevents a single variable from consuming excessive memory during split+join. `MAX_TEMPLATE_LENGTH` (1MB) prevents the rendered output from growing unbounded.
- The `vars` parameter uses `Record<string, string>` (values are always strings). Numeric or boolean values must be converted to strings by the caller (the engine).
- The `render()` method is the primary public API that downstream consumers (like `RoleBasedAgentResolver` in Story 9-8) will call. `resolveTemplate()` is also public for cases where the caller wants the raw template without interpolation.

## Completion Checklist

- [ ] `render()` method implemented
- [ ] `interpolate()` private method implemented using split+join with size limit enforcement
- [ ] `vars` defaults to `{}` when not provided
- [ ] Variables exceeding `MAX_VAR_VALUE_LENGTH` (100KB) are skipped with warning log
- [ ] Rendered templates exceeding `MAX_TEMPLATE_LENGTH` (1MB) are truncated with warning log
- [ ] Iterative expansion behavior documented in code comments
- [ ] Complete test file created at `packages/providers/src/agent-prompt-registry.test.ts`
- [ ] Tests cover: basic interpolation, multiple vars, empty vars, unreplaced placeholders, regex-safe values, iterative expansion behavior
- [ ] Tests cover: size limit enforcement (var value limit, template length limit)
- [ ] Tests cover: integration with resolution chain (render uses resolved template)
- [ ] All tests passing
- [ ] TypeScript strict mode compilation passes
- [ ] No regex usage in interpolation logic
