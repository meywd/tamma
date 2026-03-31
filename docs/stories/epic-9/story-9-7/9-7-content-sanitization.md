# Story 7: Content Sanitization

## Goal
Security primitives for sanitizing external content before it reaches LLM prompts. Applied via decorator pattern on any provider.

## Design

**New files in `packages/shared/src/security/`**:

| File | Exports |
|------|---------|
| `content-sanitizer.ts` | `IContentSanitizer` interface + `ContentSanitizer` class with `sanitize()`, `sanitizeOutput()` instance methods |
| `url-validator.ts` | `validateUrl()`, `isPrivateHost()` — generalizes existing `mcp-client/src/security/validator.ts` |
| `action-gating.ts` | `evaluateAction()`, `DEFAULT_BLOCKED_COMMANDS` |
| `secure-fetch.ts` | `secureFetch()` — URL validation + streaming size limit + manual redirect re-validation (max 5 hops) |
| `index.ts` | Barrel export (includes `IContentSanitizer` interface export) |

**`ContentSanitizer` is a CLASS, not free functions**, because the `SecureAgentProvider` decorator holds an instance. An `IContentSanitizer` interface is extracted for DIP consistency with `IProviderHealthTracker` (9-3), `IAgentProviderFactory` (9-4), `IProviderChain` (9-5), `IAgentPromptRegistry` (9-6):

```typescript
export interface IContentSanitizer {
  sanitize(input: string): { result: string; warnings: string[] };
  sanitizeOutput(output: string): { result: string; warnings: string[] };
}

export interface ContentSanitizerOptions {
  /** When false, sanitize() returns input unchanged (still removes null bytes). Default: true */
  enabled?: boolean;
  /** Additional injection patterns beyond built-in defaults (additive, not replacement) */
  extraInjectionPatterns?: readonly string[];
  /** Logger for warnings */
  logger?: ILogger;
}

export class ContentSanitizer implements IContentSanitizer {
  constructor(private readonly options?: ContentSanitizerOptions) {}

  /**
   * Sanitize input content before it reaches the LLM.
   * Strips HTML, removes zero-width characters, detects prompt injection patterns.
   * Never throws — returns warnings for anything suspicious.
   *
   * When options.enabled === false: still removes null bytes (hard safety requirement)
   * but skips HTML stripping and injection detection.
   */
  sanitize(input: string): { result: string; warnings: string[] } {
    const warnings: string[] = [];
    let result = input;

    // Null byte removal is always applied (hard safety requirement)
    result = result.replace(/\0/g, '');

    if (this.options?.enabled === false) {
      return { result, warnings };
    }

    result = this.stripHtml(result);
    result = this.removeZeroWidthChars(result);

    const injectionWarnings = this.detectPromptInjection(result);
    warnings.push(...injectionWarnings);

    return { result, warnings };
  }

  /**
   * Sanitize output content coming back from the LLM.
   * Strips any HTML, removes zero-width chars. Less aggressive than input.
   *
   * When options.enabled === false: still removes null bytes but skips HTML stripping.
   */
  sanitizeOutput(output: string): { result: string; warnings: string[] } {
    const warnings: string[] = [];
    let result = output;

    // Null byte removal is always applied
    result = result.replace(/\0/g, '');

    if (this.options?.enabled === false) {
      return { result, warnings };
    }

    result = this.removeZeroWidthChars(result);
    // Lighter-touch on output: strip HTML tags but preserve code blocks
    result = this.stripHtmlPreserveCode(result);

    return { result, warnings };
  }

  /**
   * Quote-aware state machine: handles `<div title="a>b">content</div>` correctly
   * by tracking single/double quote state inside tag attributes.
   */
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
  private stripHtmlPreserveCode(input: string): string { /* preserve ``` blocks */ }
  /**
   * Removes zero-width and invisible characters (20+ code points):
   * U+200B-U+200F, U+202A-U+202E (bidi overrides — CVE-2021-42574),
   * U+2060-U+2069, U+00AD, U+034F, U+0000, U+2028, U+2029, U+FEFF, U+FFFC
   */
  private removeZeroWidthChars(input: string): string { /* remove 20+ invisible chars */ }
  /**
   * Prompt injection detection with 5 categories:
   * 1. Instruction override: "ignore previous instructions", "disregard above"
   * 2. Role hijacking: "you are now", "act as", "pretend to be"
   * 3. System prompt extraction: "repeat your system prompt", "what are your instructions"
   * 4. Delimiter injection: "```system", "###SYSTEM###", "[INST]"
   * 5. Encoding evasion: detected via Unicode NFKD normalization before matching
   *
   * Note: Patterns are heuristic defense-in-depth, not a guarantee.
   * Applies NFKD normalization before pattern matching.
   * Uses built-in patterns + options.extraInjectionPatterns (additive).
   */
  private detectPromptInjection(input: string): string[] { /* pattern detection */ }
}
```

**`url-validator.ts`**: Generalizes `mcp-client/src/security/validator.ts` (`validateTransportUrl`). For private IP range detection (172.16-31.x.x), uses **numeric octet parsing** instead of regex:

```typescript
/** Module-scope blocked hostname set (avoids per-call allocation) */
const BLOCKED_HOSTS = new Set([
  '0.0.0.0', '[::]', '[::1]', '127.0.0.1', 'localhost',
  'metadata.google.internal',  // GCP metadata endpoint
  'host.docker.internal',      // Docker host access
]);

/**
 * Check if a hostname is a private/reserved IP address.
 * Uses numeric octet parsing (not regex) for RFC 1918 ranges.
 * Handles IPv6-mapped IPv4 (::ffff:x.x.x.x), bracketed IPv6,
 * and IPv6 private ranges (fc00::/7, fe80::/10).
 */
export function isPrivateHost(hostname: string): boolean {
  // Fast path: known blocked literals
  if (BLOCKED_HOSTS.has(hostname)) return true;

  // Handle bracketed IPv6 (URL notation)
  if (hostname.startsWith('[') && hostname.endsWith(']')) {
    const inner = hostname.slice(1, -1);
    if (inner === '::1' || inner === '::') return true;
    // IPv4-mapped IPv6 (::ffff:x.x.x.x)
    const v4MappedPrefix = '::ffff:';
    if (inner.toLowerCase().startsWith(v4MappedPrefix)) {
      return isPrivateHost(inner.slice(v4MappedPrefix.length));
    }
    // fc00::/7 (unique local) and fe80::/10 (link-local)
    const lc = inner.toLowerCase();
    if (lc.startsWith('fc') || lc.startsWith('fd') || lc.startsWith('fe80')) return true;
    return false;
  }

  // Parse IPv4 octets as integers for range comparison
  const parts = hostname.split('.');
  if (parts.length !== 4) return false;

  const octets = parts.map(p => {
    const n = parseInt(p, 10);
    return Number.isNaN(n) || n < 0 || n > 255 ? -1 : n;
  });
  if (octets.some(o => o === -1)) return false;

  const [a, b] = octets;

  // 0.0.0.0/8 (this network)
  if (a === 0) return true;
  // 10.0.0.0/8
  if (a === 10) return true;
  // 172.16.0.0/12
  if (a === 172 && b >= 16 && b <= 31) return true;
  // 192.168.0.0/16
  if (a === 192 && b === 168) return true;
  // 127.0.0.0/8 (loopback range)
  if (a === 127) return true;
  // 169.254.0.0/16 (link-local)
  if (a === 169 && b === 254) return true;

  return false;
}

export function validateUrl(url: string): { valid: boolean; warnings: string[] } {
  const warnings: string[] = [];
  try {
    const parsed = new URL(url);
    const allowedProtocols = ['http:', 'https:', 'ws:', 'wss:'];
    if (!allowedProtocols.includes(parsed.protocol)) {
      return { valid: false, warnings: [`Blocked protocol: ${parsed.protocol}`] };
    }
    if (isPrivateHost(parsed.hostname)) {
      return { valid: false, warnings: [`Blocked private host: ${parsed.hostname}`] };
    }
    return { valid: true, warnings };
  } catch {
    return { valid: false, warnings: [`Invalid URL: ${url.slice(0, 200)}`] };
  }
}
```

**Migration note**: The new `url-validator.ts` in `@tamma/shared` generalizes `packages/mcp-client/src/security/validator.ts` (`validateTransportUrl`, `isPrivateHost` logic). After this story, `mcp-client` should be updated to import `validateUrl` and `isPrivateHost` from `@tamma/shared/security` and delete its local `validateTransportUrl` / inline `isPrivateIP` logic. This migration is tracked separately and not done in this story.

**Decorator: `packages/providers/src/secure-agent-provider.ts`**

Wraps ANY `IAgentProvider`. Accepts `IContentSanitizer` (interface, not concrete class) for DIP:
```typescript
export class SecureAgentProvider implements IAgentProvider {
  constructor(
    private inner: IAgentProvider,
    private sanitizer: IContentSanitizer,
    private logger?: ILogger,
  ) {}

  async executeTask(config: AgentTaskConfig, onProgress?: AgentProgressCallback): Promise<AgentTaskResult> {
    // Pre: sanitize config.prompt
    const { result: sanitizedPrompt, warnings } = this.sanitizer.sanitize(config.prompt);
    warnings.forEach(w => this.logger?.warn('Sanitization warning', { warning: w }));

    const sanitizedConfig = { ...config, prompt: sanitizedPrompt };
    const taskResult = await this.inner.executeTask(sanitizedConfig, onProgress);

    // Post: sanitize output
    const { result: sanitizedOutput } = this.sanitizer.sanitizeOutput(taskResult.output);

    // Post: sanitize error if present
    const sanitizedError = taskResult.error
      ? this.sanitizer.sanitizeOutput(taskResult.error).result
      : taskResult.error;

    return { ...taskResult, output: sanitizedOutput, error: sanitizedError };
  }

  async isAvailable(): Promise<boolean> { return this.inner.isAvailable(); }
  async dispose(): Promise<void> { return this.inner.dispose(); }
}
```

**Sanitized fields**:
- Sanitized: `config.prompt` (input), `taskResult.output` (output), `taskResult.error` (output)
- NOT sanitized (by design): `config.cwd`, `config.allowedTools`, `config.permissionMode` — these are controlled by the resolver config, not external input

## Files
- CREATE `packages/shared/src/security/content-sanitizer.ts` (class with instance methods)
- CREATE `packages/shared/src/security/url-validator.ts` (numeric octet parsing for private IPs)
- CREATE `packages/shared/src/security/action-gating.ts`
- CREATE `packages/shared/src/security/secure-fetch.ts`
- CREATE `packages/shared/src/security/index.ts`
- CREATE tests for each
- CREATE `packages/providers/src/secure-agent-provider.ts`
- CREATE `packages/providers/src/secure-agent-provider.test.ts`
- MODIFY `packages/shared/src/index.ts` — export security barrel

## Verify
- Test: HTML stripped using quote-aware state machine (handles `<div title="a>b">` correctly)
- Test: zero-width and invisible character removal covers 20+ Unicode code points including bidi overrides and soft hyphens
- Test: prompt injection detection covers 5 categories: instruction override, role hijacking, system prompt extraction, delimiter injection, encoding evasion
- Test: `detectPromptInjection` applies Unicode NFKD normalization before pattern matching
- Test: private IPs rejected via numeric octet parsing (0.x, 10.x, 172.16-31.x, 192.168.x, 127.x, 169.254.x)
- Test: `isPrivateHost` handles IPv6-mapped IPv4 (`[::ffff:127.0.0.1]`), bracketed IPv6 (`[::1]`), and IPv6 private ranges (fc00::/7, fe80::/10)
- Test: `isPrivateHost` blocks 0.0.0.0/8 range (all `0.x.x.x` addresses)
- Test: `isPrivateHost` blocks cloud metadata hostnames (`metadata.google.internal`, `host.docker.internal`)
- Test: `isPrivateHost` does NOT use regex — uses integer comparison
- Test: `BLOCKED_HOSTS` is at module scope (not re-allocated per call)
- Test: non-IPv4 hostnames (e.g. `evil.com`) pass through to DNS (not blocked)
- Test: `IContentSanitizer` interface is exported and `ContentSanitizer` implements it
- Test: `ContentSanitizer` accepts `ContentSanitizerOptions` in constructor
- Test: `ContentSanitizer` is a class with `sanitize()` and `sanitizeOutput()` instance methods
- Test: when `options.enabled === false`, null bytes still removed but HTML stripping and injection detection skipped
- Test: `SecureAgentProvider` accepts `IContentSanitizer` (interface, not concrete class)
- Test: `SecureAgentProvider` wraps any `IAgentProvider` generically
- Test: output sanitized after task completion
- Test: `SecureAgentProvider` sanitizes `taskResult.error` in addition to `taskResult.output`
- Test: `SecureAgentProvider` delegates `isAvailable()` and `dispose()` to inner
- Test: `evaluateAction` uses case-insensitive substring matching (not regex)
- Test: `evaluateAction` with `ActionGateOptions.extraPatterns` extends defaults; `replaceDefaults` replaces them
- Test: `evaluateAction` normalizes whitespace before matching
- Test: `evaluateAction` blocks shell metacharacters (`| sh`, `| bash`, `$(`, backtick)
- Test: `evaluateAction` reason does not reveal which pattern matched (says "Command blocked by security policy")
- Test: `validateUrl` truncates URL in error messages to max 200 chars
- Test: `secureFetch` uses `redirect: 'manual'` and re-validates Location header (max 5 redirect hops)
- Test: `secureFetch` reads body via ReadableStream with running byte counter, aborts via AbortController on size exceed
- Test: `secureFetch` checks Content-Type allowlist before reading body
- Test: redirect-to-private-IP blocked in `secureFetch`

## Logging Requirements

All provider-layer modules MUST use `ILogger` from `@tamma/shared/contracts` (not `console.log`).

- **INFO**: Provider initialization, config loaded, provider selected, chain fallback triggered
- **DEBUG**: Request parameters (redact API keys), response metadata, cache hits
- **WARN**: Provider degraded, rate limited, circuit breaker tripped, fallback to next provider
- **ERROR**: Provider call failed, config validation error, all providers exhausted
- **Structured context**: Always include `{ provider, model, issueId, duration }` where applicable
- **Credential safety**: NEVER log API keys, tokens, or secrets — log provider name and endpoint only
