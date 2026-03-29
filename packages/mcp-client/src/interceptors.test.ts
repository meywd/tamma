/**
 * Unit tests for ToolInterceptorChain and built-in interceptor factories.
 *
 * Tests cover:
 * - addPreInterceptor() and addPostInterceptor()
 * - Empty chain passthrough (no-op)
 * - Single and multiple pre-interceptor piping
 * - Single and multiple post-interceptor piping
 * - Warning accumulation across interceptors
 * - toolName parameter forwarding
 * - Async interceptor awaiting (blocking, not fire-and-forget)
 * - Error isolation per interceptor with fail-open (F09)
 * - Prototype pollution key stripping (F16)
 * - createSanitizationInterceptor (F10)
 * - createUrlValidationInterceptor (F11)
 */

import { describe, it, expect, vi } from 'vitest';
import type { IContentSanitizer } from '@tamma/shared';
import type { ToolResult } from './types.js';
import {
  ToolInterceptorChain,
  createSanitizationInterceptor,
  createUrlValidationInterceptor,
  type PreInterceptor,
  type PostInterceptor,
  type ValidateUrlFn,
} from './interceptors.js';

// --- Helper factories ---

function makeToolResult(overrides: Partial<ToolResult> = {}): ToolResult {
  return {
    success: true,
    content: [{ type: 'text', text: 'hello world' }],
    ...overrides,
  };
}

// --- Tests ---

describe('ToolInterceptorChain', () => {
  describe('addPreInterceptor', () => {
    it('adds interceptor to the chain', async () => {
      const chain = new ToolInterceptorChain();
      const interceptor: PreInterceptor = async (_toolName, args) => ({
        args: { ...args, added: true },
        warnings: [],
      });

      chain.addPreInterceptor(interceptor);

      const { args } = await chain.runPre('test-tool', { original: true });
      expect(args).toEqual({ original: true, added: true });
    });
  });

  describe('addPostInterceptor', () => {
    it('adds interceptor to the chain', async () => {
      const chain = new ToolInterceptorChain();
      const interceptor: PostInterceptor = async (_toolName, result) => ({
        result: {
          ...result,
          content: [{ type: 'text', text: 'modified' }],
        },
        warnings: [],
      });

      chain.addPostInterceptor(interceptor);

      const { result } = await chain.runPost('test-tool', makeToolResult());
      expect(result.content).toEqual([{ type: 'text', text: 'modified' }]);
    });
  });

  describe('runPre - empty chain', () => {
    it('returns original args with no warnings', async () => {
      const chain = new ToolInterceptorChain();
      const originalArgs = { key: 'value', num: 42 };

      const { args, warnings } = await chain.runPre('test-tool', originalArgs);

      expect(args).toBe(originalArgs); // Same reference -- no modification
      expect(warnings).toEqual([]);
    });
  });

  describe('runPost - empty chain', () => {
    it('returns original result with no warnings', async () => {
      const chain = new ToolInterceptorChain();
      const originalResult = makeToolResult();

      const { result, warnings } = await chain.runPost('test-tool', originalResult);

      expect(result).toBe(originalResult); // Same reference -- no modification
      expect(warnings).toEqual([]);
    });
  });

  describe('runPre - single interceptor', () => {
    it('modifies args and returns warnings', async () => {
      const chain = new ToolInterceptorChain();
      chain.addPreInterceptor(async (_toolName, args) => ({
        args: { ...args, sanitized: true },
        warnings: ['URL was rewritten'],
      }));

      const { args, warnings } = await chain.runPre('test-tool', { url: 'http://example.com' });

      expect(args).toEqual({ url: 'http://example.com', sanitized: true });
      expect(warnings).toEqual(['URL was rewritten']);
    });
  });

  describe('runPost - single interceptor', () => {
    it('modifies result and returns warnings', async () => {
      const chain = new ToolInterceptorChain();
      chain.addPostInterceptor(async (_toolName, result) => ({
        result: {
          ...result,
          content: [{ type: 'text', text: 'sanitized content' }],
        },
        warnings: ['Content was sanitized'],
      }));

      const { result, warnings } = await chain.runPost('test-tool', makeToolResult());

      expect(result.content).toEqual([{ type: 'text', text: 'sanitized content' }]);
      expect(warnings).toEqual(['Content was sanitized']);
    });
  });

  describe('runPre - multiple interceptors', () => {
    it('runs in registration order, each receiving output of previous', async () => {
      const chain = new ToolInterceptorChain();
      const order: number[] = [];

      chain.addPreInterceptor(async (_toolName, args) => {
        order.push(1);
        return {
          args: { ...args, step1: true },
          warnings: ['warning-1'],
        };
      });

      chain.addPreInterceptor(async (_toolName, args) => {
        order.push(2);
        // Verify it receives output of first interceptor
        expect(args).toHaveProperty('step1', true);
        return {
          args: { ...args, step2: true },
          warnings: ['warning-2'],
        };
      });

      chain.addPreInterceptor(async (_toolName, args) => {
        order.push(3);
        // Verify it receives output of second interceptor
        expect(args).toHaveProperty('step1', true);
        expect(args).toHaveProperty('step2', true);
        return {
          args: { ...args, step3: true },
          warnings: ['warning-3'],
        };
      });

      const { args, warnings } = await chain.runPre('test-tool', { original: true });

      expect(order).toEqual([1, 2, 3]);
      expect(args).toEqual({ original: true, step1: true, step2: true, step3: true });
      expect(warnings).toEqual(['warning-1', 'warning-2', 'warning-3']);
    });
  });

  describe('runPost - multiple interceptors', () => {
    it('runs in registration order, each receiving output of previous', async () => {
      const chain = new ToolInterceptorChain();
      const order: number[] = [];

      chain.addPostInterceptor(async (_toolName, result) => {
        order.push(1);
        return {
          result: {
            ...result,
            content: [{ type: 'text' as const, text: `${(result.content[0] as { type: 'text'; text: string }).text}-step1` }],
          },
          warnings: ['post-warning-1'],
        };
      });

      chain.addPostInterceptor(async (_toolName, result) => {
        order.push(2);
        return {
          result: {
            ...result,
            content: [{ type: 'text' as const, text: `${(result.content[0] as { type: 'text'; text: string }).text}-step2` }],
          },
          warnings: ['post-warning-2'],
        };
      });

      const { result, warnings } = await chain.runPost(
        'test-tool',
        makeToolResult({ content: [{ type: 'text', text: 'original' }] }),
      );

      expect(order).toEqual([1, 2]);
      expect(result.content).toEqual([{ type: 'text', text: 'original-step1-step2' }]);
      expect(warnings).toEqual(['post-warning-1', 'post-warning-2']);
    });
  });

  describe('warning accumulation', () => {
    it('warnings from multiple interceptors are accumulated in a flat array', async () => {
      const chain = new ToolInterceptorChain();

      chain.addPreInterceptor(async (_toolName, args) => ({
        args,
        warnings: ['w1', 'w2'],
      }));

      chain.addPreInterceptor(async (_toolName, args) => ({
        args,
        warnings: ['w3'],
      }));

      const { warnings } = await chain.runPre('test-tool', {});

      expect(warnings).toEqual(['w1', 'w2', 'w3']);
    });
  });

  describe('toolName parameter', () => {
    it('pre-interceptor receives correct toolName', async () => {
      const chain = new ToolInterceptorChain();
      const receivedToolNames: string[] = [];

      chain.addPreInterceptor(async (toolName, args) => {
        receivedToolNames.push(toolName);
        return { args, warnings: [] };
      });

      await chain.runPre('my-special-tool', {});

      expect(receivedToolNames).toEqual(['my-special-tool']);
    });

    it('post-interceptor receives correct toolName', async () => {
      const chain = new ToolInterceptorChain();
      const receivedToolNames: string[] = [];

      chain.addPostInterceptor(async (toolName, result) => {
        receivedToolNames.push(toolName);
        return { result, warnings: [] };
      });

      await chain.runPost('my-special-tool', makeToolResult());

      expect(receivedToolNames).toEqual(['my-special-tool']);
    });
  });

  describe('async awaiting', () => {
    it('runPre awaits async interceptors (not fire-and-forget)', async () => {
      const chain = new ToolInterceptorChain();
      let asyncWorkDone = false;

      chain.addPreInterceptor(async (_toolName, args) => {
        // Simulate async work
        await new Promise<void>((resolve) => {
          setTimeout(resolve, 10);
        });
        asyncWorkDone = true;
        return { args: { ...args, asyncDone: true }, warnings: [] };
      });

      const { args } = await chain.runPre('test-tool', {});

      expect(asyncWorkDone).toBe(true);
      expect(args).toHaveProperty('asyncDone', true);
    });

    it('runPost awaits async interceptors (not fire-and-forget)', async () => {
      const chain = new ToolInterceptorChain();
      let asyncWorkDone = false;

      chain.addPostInterceptor(async (_toolName, result) => {
        // Simulate async work
        await new Promise<void>((resolve) => {
          setTimeout(resolve, 10);
        });
        asyncWorkDone = true;
        return {
          result: { ...result, content: [{ type: 'text', text: 'async-result' }] },
          warnings: [],
        };
      });

      const { result } = await chain.runPost('test-tool', makeToolResult());

      expect(asyncWorkDone).toBe(true);
      expect(result.content).toEqual([{ type: 'text', text: 'async-result' }]);
    });
  });

  describe('error isolation (F09)', () => {
    it('runPre catches interceptor error, adds warning, continues with unmodified args', async () => {
      const chain = new ToolInterceptorChain();

      // First interceptor: modifies args
      chain.addPreInterceptor(async (_toolName, args) => ({
        args: { ...args, step1: true },
        warnings: [],
      }));

      // Second interceptor: throws
      chain.addPreInterceptor(async () => {
        throw new Error('interceptor exploded');
      });

      // Third interceptor: should still run with args from first
      chain.addPreInterceptor(async (_toolName, args) => ({
        args: { ...args, step3: true },
        warnings: [],
      }));

      const { args, warnings } = await chain.runPre('test-tool', { original: true });

      // Args should have step1 (from first) but NOT step2 (second threw)
      // Third interceptor receives the args from step1 (not modified by failed step2)
      expect(args).toEqual({ original: true, step1: true, step3: true });
      expect(warnings).toContain('Pre-interceptor failed: interceptor exploded');
    });

    it('runPre catches non-Error thrown values', async () => {
      const chain = new ToolInterceptorChain();

      chain.addPreInterceptor(async () => {
        throw 'string error'; // eslint-disable-line no-throw-literal
      });

      const { warnings } = await chain.runPre('test-tool', {});

      expect(warnings).toContain('Pre-interceptor failed: string error');
    });

    it('runPost catches interceptor error, adds warning, continues with unmodified result', async () => {
      const chain = new ToolInterceptorChain();
      const originalResult = makeToolResult({
        content: [{ type: 'text', text: 'original' }],
      });

      // First interceptor: modifies result
      chain.addPostInterceptor(async (_toolName, result) => ({
        result: {
          ...result,
          content: [{ type: 'text', text: 'modified' }],
        },
        warnings: [],
      }));

      // Second interceptor: throws
      chain.addPostInterceptor(async () => {
        throw new Error('post-interceptor failed');
      });

      // Third interceptor: should still run with result from first
      chain.addPostInterceptor(async (_toolName, result) => ({
        result: {
          ...result,
          content: [
            ...result.content,
            { type: 'text' as const, text: 'appended' },
          ],
        },
        warnings: [],
      }));

      const { result, warnings } = await chain.runPost('test-tool', originalResult);

      // Result should be from first interceptor (modified), then third appends
      // Second interceptor threw, so third receives the result from first
      expect(result.content).toEqual([
        { type: 'text', text: 'modified' },
        { type: 'text', text: 'appended' },
      ]);
      expect(warnings).toContain('Post-interceptor failed: post-interceptor failed');
    });

    it('runPost catches non-Error thrown values', async () => {
      const chain = new ToolInterceptorChain();

      chain.addPostInterceptor(async () => {
        throw 42; // eslint-disable-line no-throw-literal
      });

      const { warnings } = await chain.runPost('test-tool', makeToolResult());

      expect(warnings).toContain('Post-interceptor failed: 42');
    });

    it('error in one pre-interceptor does not skip subsequent interceptors', async () => {
      const chain = new ToolInterceptorChain();
      const callOrder: string[] = [];

      chain.addPreInterceptor(async (_toolName, args) => {
        callOrder.push('first');
        return { args, warnings: [] };
      });

      chain.addPreInterceptor(async () => {
        callOrder.push('second-throws');
        throw new Error('boom');
      });

      chain.addPreInterceptor(async (_toolName, args) => {
        callOrder.push('third');
        return { args, warnings: [] };
      });

      await chain.runPre('test-tool', {});

      expect(callOrder).toEqual(['first', 'second-throws', 'third']);
    });

    it('error in one post-interceptor does not skip subsequent interceptors', async () => {
      const chain = new ToolInterceptorChain();
      const callOrder: string[] = [];

      chain.addPostInterceptor(async (_toolName, result) => {
        callOrder.push('first');
        return { result, warnings: [] };
      });

      chain.addPostInterceptor(async () => {
        callOrder.push('second-throws');
        throw new Error('boom');
      });

      chain.addPostInterceptor(async (_toolName, result) => {
        callOrder.push('third');
        return { result, warnings: [] };
      });

      await chain.runPost('test-tool', makeToolResult());

      expect(callOrder).toEqual(['first', 'second-throws', 'third']);
    });
  });

  describe('prototype pollution protection (F16)', () => {
    it('runPre strips __proto__ key from returned args', async () => {
      const chain = new ToolInterceptorChain();

      chain.addPreInterceptor(async (_toolName, _args) => {
        // Use Object.create(null) to create a truly clean object, then add __proto__ as own prop
        const maliciousArgs = Object.create(null) as Record<string, unknown>;
        maliciousArgs['safe'] = true;
        maliciousArgs['__proto__'] = { polluted: true };
        return { args: maliciousArgs, warnings: [] };
      });

      const { args, warnings } = await chain.runPre('test-tool', {});

      expect(Object.hasOwn(args, '__proto__')).toBe(false);
      expect(args['safe']).toBe(true);
      expect(warnings).toContain('Prototype pollution key "__proto__" removed from interceptor output');
    });

    it('runPre strips constructor key from returned args', async () => {
      const chain = new ToolInterceptorChain();

      chain.addPreInterceptor(async (_toolName, _args) => ({
        args: { safe: true, constructor: 'malicious' } as Record<string, unknown>,
        warnings: [],
      }));

      const { args, warnings } = await chain.runPre('test-tool', {});

      expect(Object.hasOwn(args, 'constructor')).toBe(false);
      expect(args['safe']).toBe(true);
      expect(warnings).toContain('Prototype pollution key "constructor" removed from interceptor output');
    });

    it('runPre strips prototype key from returned args', async () => {
      const chain = new ToolInterceptorChain();

      chain.addPreInterceptor(async (_toolName, _args) => ({
        args: { safe: true, prototype: {} } as Record<string, unknown>,
        warnings: [],
      }));

      const { args, warnings } = await chain.runPre('test-tool', {});

      expect(Object.hasOwn(args, 'prototype')).toBe(false);
      expect(args['safe']).toBe(true);
      expect(warnings).toContain('Prototype pollution key "prototype" removed from interceptor output');
    });

    it('runPre adds warning when prototype pollution key is stripped', async () => {
      const chain = new ToolInterceptorChain();

      chain.addPreInterceptor(async (_toolName, _args) => ({
        args: {
          safe: 'value',
          constructor: 'bad',
          prototype: 'bad',
        } as Record<string, unknown>,
        warnings: ['interceptor-warning'],
      }));

      const { warnings } = await chain.runPre('test-tool', {});

      // Should have: pollution warnings + interceptor's own warning
      expect(warnings).toContain('Prototype pollution key "constructor" removed from interceptor output');
      expect(warnings).toContain('Prototype pollution key "prototype" removed from interceptor output');
      expect(warnings).toContain('interceptor-warning');
    });

    it('runPre does not strip legitimate keys', async () => {
      const chain = new ToolInterceptorChain();

      chain.addPreInterceptor(async (_toolName, _args) => ({
        args: { name: 'test', url: 'http://example.com', count: 5 },
        warnings: [],
      }));

      const { args, warnings } = await chain.runPre('test-tool', {});

      expect(args).toEqual({ name: 'test', url: 'http://example.com', count: 5 });
      expect(warnings).toEqual([]);
    });

    it('pollution warnings appear before interceptor warnings in accumulated array', async () => {
      const chain = new ToolInterceptorChain();

      chain.addPreInterceptor(async (_toolName, _args) => ({
        args: { constructor: 'bad' } as Record<string, unknown>,
        warnings: ['my-warning'],
      }));

      const { warnings } = await chain.runPre('test-tool', {});

      // Pollution warning is added before interceptor's own warnings
      const pollutionIndex = warnings.indexOf('Prototype pollution key "constructor" removed from interceptor output');
      const ownWarningIndex = warnings.indexOf('my-warning');

      expect(pollutionIndex).toBeGreaterThanOrEqual(0);
      expect(ownWarningIndex).toBeGreaterThanOrEqual(0);
      expect(pollutionIndex).toBeLessThan(ownWarningIndex);
    });
  });

  describe('type safety', () => {
    it('PreInterceptor type signature is correct', () => {
      // Compile-time check: this should typecheck
      const fn: PreInterceptor = async (toolName: string, args: Record<string, unknown>) => ({
        args: { ...args, tool: toolName },
        warnings: [],
      });
      expect(typeof fn).toBe('function');
    });

    it('PostInterceptor type signature is correct', () => {
      // Compile-time check: this should typecheck
      const fn: PostInterceptor = async (toolName: string, result: ToolResult) => ({
        result: { ...result, metadata: { latencyMs: 0, serverName: 's', toolName } },
        warnings: [],
      });
      expect(typeof fn).toBe('function');
    });
  });

  describe('isolation between pre and post chains', () => {
    it('pre and post interceptors operate independently', async () => {
      const chain = new ToolInterceptorChain();
      const preCallCount = vi.fn();
      const postCallCount = vi.fn();

      chain.addPreInterceptor(async (_toolName, args) => {
        preCallCount();
        return { args, warnings: ['pre-warning'] };
      });

      chain.addPostInterceptor(async (_toolName, result) => {
        postCallCount();
        return { result, warnings: ['post-warning'] };
      });

      // Running runPre should not invoke post-interceptors
      const preResult = await chain.runPre('test-tool', {});
      expect(preCallCount).toHaveBeenCalledTimes(1);
      expect(postCallCount).not.toHaveBeenCalled();
      expect(preResult.warnings).toEqual(['pre-warning']);

      // Running runPost should not invoke pre-interceptors
      const postResult = await chain.runPost('test-tool', makeToolResult());
      expect(preCallCount).toHaveBeenCalledTimes(1); // still 1
      expect(postCallCount).toHaveBeenCalledTimes(1);
      expect(postResult.warnings).toEqual(['post-warning']);
    });
  });
});

// --- Factory function tests ---

/**
 * Creates a mock IContentSanitizer.
 * By default, returns input unchanged with no warnings.
 */
function createMockSanitizer(
  overrides?: Partial<IContentSanitizer>,
): IContentSanitizer {
  return {
    sanitize: overrides?.sanitize ?? ((input: string) => ({ result: input, warnings: [] })),
    sanitizeOutput: overrides?.sanitizeOutput ?? ((output: string) => ({ result: output, warnings: [] })),
  };
}

/**
 * Creates a mock validateUrl function.
 * By default, all URLs are valid with no warnings.
 */
function createMockValidateUrl(
  override?: ValidateUrlFn,
): ValidateUrlFn {
  return override ?? ((_url: string) => ({ valid: true, warnings: [] }));
}

describe('createSanitizationInterceptor', () => {
  it('returns a function matching PostInterceptor type', () => {
    const sanitizer = createMockSanitizer();
    const interceptor = createSanitizationInterceptor(sanitizer);

    expect(typeof interceptor).toBe('function');
  });

  it('sanitizes text content in a ToolResult with text items', async () => {
    const sanitizer = createMockSanitizer({
      sanitizeOutput: (input: string) => ({
        result: input.replace('<script>alert("xss")</script>', ''),
        warnings: [],
      }),
    });
    const interceptor = createSanitizationInterceptor(sanitizer);

    const result = makeToolResult({
      content: [
        { type: 'text', text: 'safe text <script>alert("xss")</script>' },
      ],
    });

    const { result: sanitized } = await interceptor('test-tool', result);

    expect(sanitized.content).toEqual([
      { type: 'text', text: 'safe text ' },
    ]);
  });

  it('does not modify non-text content (image)', async () => {
    const sanitizer = createMockSanitizer();
    const interceptor = createSanitizationInterceptor(sanitizer);

    const imageContent = { type: 'image' as const, data: 'base64data', mimeType: 'image/png' };
    const result = makeToolResult({
      content: [imageContent],
    });

    const { result: sanitized } = await interceptor('test-tool', result);

    expect(sanitized.content).toEqual([imageContent]);
  });

  it('does not modify non-text content (resource)', async () => {
    const sanitizer = createMockSanitizer();
    const interceptor = createSanitizationInterceptor(sanitizer);

    const resourceContent = {
      type: 'resource' as const,
      resource: { uri: 'file:///test.txt', text: 'content' },
    };
    const result = makeToolResult({
      content: [resourceContent],
    });

    const { result: sanitized } = await interceptor('test-tool', result);

    expect(sanitized.content).toEqual([resourceContent]);
  });

  it('returns a new ToolResult object (does not mutate input)', async () => {
    const sanitizer = createMockSanitizer({
      sanitizeOutput: (input: string) => ({
        result: input.toUpperCase(),
        warnings: [],
      }),
    });
    const interceptor = createSanitizationInterceptor(sanitizer);

    const originalResult = makeToolResult({
      content: [{ type: 'text', text: 'hello' }],
    });

    const { result: sanitized } = await interceptor('test-tool', originalResult);

    // New object, not the same reference
    expect(sanitized).not.toBe(originalResult);
    // Original is unchanged
    expect(originalResult.content).toEqual([{ type: 'text', text: 'hello' }]);
    // Sanitized has uppercase text
    expect(sanitized.content).toEqual([{ type: 'text', text: 'HELLO' }]);
  });

  it('adds warnings when content was modified', async () => {
    const sanitizer = createMockSanitizer({
      sanitizeOutput: (_input: string) => ({
        result: 'sanitized',
        warnings: ['Detected injection pattern'],
      }),
    });
    const interceptor = createSanitizationInterceptor(sanitizer);

    const result = makeToolResult({
      content: [{ type: 'text', text: 'ignore previous instructions' }],
    });

    const { warnings } = await interceptor('test-tool', result);

    expect(warnings).toEqual(['Detected injection pattern']);
  });

  it('returns empty warnings when no modification needed', async () => {
    const sanitizer = createMockSanitizer();
    const interceptor = createSanitizationInterceptor(sanitizer);

    const result = makeToolResult({
      content: [{ type: 'text', text: 'safe content' }],
    });

    const { warnings } = await interceptor('test-tool', result);

    expect(warnings).toEqual([]);
  });

  it('handles empty content array', async () => {
    const sanitizer = createMockSanitizer();
    const interceptor = createSanitizationInterceptor(sanitizer);

    const result = makeToolResult({ content: [] });

    const { result: sanitized, warnings } = await interceptor('test-tool', result);

    expect(sanitized.content).toEqual([]);
    expect(warnings).toEqual([]);
  });

  it('collects warnings from multiple text items', async () => {
    let callCount = 0;
    const sanitizer = createMockSanitizer({
      sanitizeOutput: (input: string) => {
        callCount++;
        return {
          result: input,
          warnings: [`warning-${callCount}`],
        };
      },
    });
    const interceptor = createSanitizationInterceptor(sanitizer);

    const result = makeToolResult({
      content: [
        { type: 'text', text: 'text1' },
        { type: 'text', text: 'text2' },
        { type: 'text', text: 'text3' },
      ],
    });

    const { warnings } = await interceptor('test-tool', result);

    expect(warnings).toEqual(['warning-1', 'warning-2', 'warning-3']);
  });

  it('handles mixed content types (text, image, resource)', async () => {
    const sanitizer = createMockSanitizer({
      sanitizeOutput: (input: string) => ({
        result: `sanitized:${input}`,
        warnings: [],
      }),
    });
    const interceptor = createSanitizationInterceptor(sanitizer);

    const result = makeToolResult({
      content: [
        { type: 'text', text: 'hello' },
        { type: 'image', data: 'imgdata', mimeType: 'image/png' },
        { type: 'text', text: 'world' },
        { type: 'resource', resource: { uri: 'file:///x.txt' } },
      ],
    });

    const { result: sanitized } = await interceptor('test-tool', result);

    expect(sanitized.content).toEqual([
      { type: 'text', text: 'sanitized:hello' },
      { type: 'image', data: 'imgdata', mimeType: 'image/png' },
      { type: 'text', text: 'sanitized:world' },
      { type: 'resource', resource: { uri: 'file:///x.txt' } },
    ]);
  });

  it('preserves other ToolResult fields (success, error, metadata)', async () => {
    const sanitizer = createMockSanitizer();
    const interceptor = createSanitizationInterceptor(sanitizer);

    const result: ToolResult = {
      success: false,
      content: [{ type: 'text', text: 'error output' }],
      error: 'something went wrong',
      metadata: { latencyMs: 42, serverName: 'test-server', toolName: 'test-tool' },
    };

    const { result: sanitized } = await interceptor('test-tool', result);

    expect(sanitized.success).toBe(false);
    expect(sanitized.error).toBe('something went wrong');
    expect(sanitized.metadata).toEqual({
      latencyMs: 42,
      serverName: 'test-server',
      toolName: 'test-tool',
    });
  });

  it('integrates with ToolInterceptorChain as a post-interceptor', async () => {
    const sanitizer = createMockSanitizer({
      sanitizeOutput: (input: string) => ({
        result: input.replace('bad', '***'),
        warnings: ['Content was sanitized'],
      }),
    });

    const chain = new ToolInterceptorChain();
    chain.addPostInterceptor(createSanitizationInterceptor(sanitizer));

    const result = makeToolResult({
      content: [{ type: 'text', text: 'this has bad content' }],
    });

    const { result: sanitized, warnings } = await chain.runPost('test-tool', result);

    expect(sanitized.content).toEqual([{ type: 'text', text: 'this has *** content' }]);
    expect(warnings).toEqual(['Content was sanitized']);
  });
});

describe('createUrlValidationInterceptor', () => {
  it('returns a function matching PreInterceptor type', () => {
    const validateUrl = createMockValidateUrl();
    const interceptor = createUrlValidationInterceptor(validateUrl);

    expect(typeof interceptor).toBe('function');
  });

  it('adds warning for blocked URL in args', async () => {
    const validateUrl = createMockValidateUrl((url: string) => {
      if (url.includes('localhost')) {
        return { valid: false, warnings: ['Blocked private host: localhost'] };
      }
      return { valid: true, warnings: [] };
    });
    const interceptor = createUrlValidationInterceptor(validateUrl);

    const { warnings } = await interceptor('test-tool', {
      url: 'http://localhost:8080/api',
    });

    expect(warnings).toContain('Blocked private host: localhost');
    expect(warnings).toContain('URL blocked by policy: http://localhost:8080/api');
  });

  it('returns no warnings for allowed URLs', async () => {
    const validateUrl = createMockValidateUrl();
    const interceptor = createUrlValidationInterceptor(validateUrl);

    const { warnings } = await interceptor('test-tool', {
      url: 'https://api.example.com/data',
    });

    expect(warnings).toEqual([]);
  });

  it('ignores non-string arg values', async () => {
    const validateUrl = vi.fn(createMockValidateUrl());
    const interceptor = createUrlValidationInterceptor(validateUrl);

    const { warnings } = await interceptor('test-tool', {
      count: 42,
      enabled: true,
      config: { nested: 'http://example.com' },
      items: ['http://example.com'],
    });

    // validateUrl should not have been called for any non-string values
    expect(validateUrl).not.toHaveBeenCalled();
    expect(warnings).toEqual([]);
  });

  it('ignores strings that are not URL-like', async () => {
    const validateUrl = vi.fn(createMockValidateUrl());
    const interceptor = createUrlValidationInterceptor(validateUrl);

    const { warnings } = await interceptor('test-tool', {
      name: 'John Doe',
      path: '/usr/local/bin',
      message: 'Hello world',
      query: 'SELECT * FROM users',
    });

    // No strings contain :// or start with http
    expect(validateUrl).not.toHaveBeenCalled();
    expect(warnings).toEqual([]);
  });

  it('checks multiple arg values', async () => {
    // Use an exact-match blocklist to avoid incomplete substring checks
    // (CodeQL js/incomplete-url-substring-sanitization)
    const blockedDomains = new Set(['evil.com']);
    const validateUrl = vi.fn(createMockValidateUrl((url: string) => {
      try {
        const parsed = new URL(url);
        // Split hostname into labels and check if the registrable domain
        // (last two labels) is in the blocklist
        const labels = parsed.hostname.split('.');
        const registrable = labels.slice(-2).join('.');
        if (blockedDomains.has(parsed.hostname) || blockedDomains.has(registrable)) {
          return { valid: false, warnings: ['Suspicious domain'] };
        }
      } catch {
        // Not a valid URL, allow through
      }
      return { valid: true, warnings: [] };
    }));
    const interceptor = createUrlValidationInterceptor(validateUrl);

    const { warnings } = await interceptor('test-tool', {
      sourceUrl: 'https://api.example.com/data',
      targetUrl: 'https://evil.com/steal',
      name: 'test',
    });

    // Both URLs should have been checked
    expect(validateUrl).toHaveBeenCalledTimes(2);
    expect(warnings).toContain('Suspicious domain');
    expect(warnings).toContain('URL blocked by policy: https://evil.com/steal');
  });

  it('replaces blocked URLs in args with placeholder (fail-closed)', async () => {
    const validateUrl = createMockValidateUrl((_url: string) => ({
      valid: false,
      warnings: ['blocked'],
    }));
    const interceptor = createUrlValidationInterceptor(validateUrl);

    const originalArgs = {
      url: 'http://blocked.com/path',
      name: 'test',
    };

    const { args } = await interceptor('test-tool', originalArgs);

    // Blocked URLs are replaced with a safe placeholder
    expect(args).not.toBe(originalArgs);
    expect(args).toEqual({
      url: '[URL_BLOCKED_BY_POLICY]',
      name: 'test',
    });
    // Original args not mutated
    expect(originalArgs.url).toBe('http://blocked.com/path');
  });

  it('detects URL-like strings containing ://', async () => {
    const validateUrl = vi.fn(createMockValidateUrl());
    const interceptor = createUrlValidationInterceptor(validateUrl);

    await interceptor('test-tool', {
      endpoint: 'ftp://files.example.com/data',
    });

    // ftp:// contains :// so it should be checked
    expect(validateUrl).toHaveBeenCalledWith('ftp://files.example.com/data');
  });

  it('detects URL-like strings starting with http', async () => {
    const validateUrl = vi.fn(createMockValidateUrl());
    const interceptor = createUrlValidationInterceptor(validateUrl);

    await interceptor('test-tool', {
      link: 'https://example.com',
      anotherLink: 'http://example.com',
    });

    expect(validateUrl).toHaveBeenCalledTimes(2);
  });

  it('collects warnings from validateUrl for each URL', async () => {
    const validateUrl = createMockValidateUrl((_url: string) => ({
      valid: true,
      warnings: ['URL has unusual characters'],
    }));
    const interceptor = createUrlValidationInterceptor(validateUrl);

    const { warnings } = await interceptor('test-tool', {
      url1: 'https://example.com/path?q=test%00null',
      url2: 'https://example.org/other%00',
    });

    // Two warnings from validateUrl, no "blocked" warnings since valid=true
    expect(warnings).toEqual([
      'URL has unusual characters',
      'URL has unusual characters',
    ]);
  });

  it('integrates with ToolInterceptorChain as a pre-interceptor', async () => {
    const validateUrl = createMockValidateUrl((url: string) => {
      if (url.includes('127.0.0.1')) {
        return { valid: false, warnings: ['Blocked private host: 127.0.0.1'] };
      }
      return { valid: true, warnings: [] };
    });

    const chain = new ToolInterceptorChain();
    chain.addPreInterceptor(createUrlValidationInterceptor(validateUrl));

    const { args, warnings } = await chain.runPre('test-tool', {
      url: 'http://127.0.0.1:3000/api',
      name: 'test',
    });

    // Blocked URL should be replaced (fail-closed)
    expect(args).toEqual({ url: '[URL_BLOCKED_BY_POLICY]', name: 'test' });
    // Warnings should contain validation info
    expect(warnings).toContain('Blocked private host: 127.0.0.1');
    expect(warnings).toContain('URL blocked by policy: http://127.0.0.1:3000/api');
  });

  it('handles args with no URL-like values', async () => {
    const validateUrl = vi.fn(createMockValidateUrl());
    const interceptor = createUrlValidationInterceptor(validateUrl);

    const { args, warnings } = await interceptor('test-tool', {
      query: 'SELECT * FROM users',
      limit: 100,
    });

    expect(validateUrl).not.toHaveBeenCalled();
    expect(warnings).toEqual([]);
    expect(args).toEqual({ query: 'SELECT * FROM users', limit: 100 });
  });

  it('handles empty args object', async () => {
    const validateUrl = vi.fn(createMockValidateUrl());
    const interceptor = createUrlValidationInterceptor(validateUrl);

    const { args, warnings } = await interceptor('test-tool', {});

    expect(validateUrl).not.toHaveBeenCalled();
    expect(warnings).toEqual([]);
    expect(args).toEqual({});
  });
});
