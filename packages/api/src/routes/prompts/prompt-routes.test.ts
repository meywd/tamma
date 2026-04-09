/**
 * Tests for Prompt Registry API Routes
 *
 * Tests the GET, PUT, POST /api/prompts endpoints using Fastify's
 * inject() method for lightweight HTTP testing without network.
 *
 * Story 12-5: Prompt Engineering Framework
 */

import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import Fastify, { type FastifyInstance } from 'fastify';
import { rm } from 'node:fs/promises';
import { registerPromptRoutes } from './prompt-routes.js';
import { PromptStore } from '../../services/prompt-store.js';

const TEST_FILE_PATH = '/tmp/tamma-test-prompt-routes.json';

describe('Prompt Registry Routes', () => {
  let app: FastifyInstance;
  let store: PromptStore;

  beforeEach(async () => {
    try {
      await rm(TEST_FILE_PATH, { force: true });
    } catch {
      // ignore
    }

    store = new PromptStore({
      filePath: TEST_FILE_PATH,
      skipDefaults: true,
    });

    app = Fastify({ logger: false });
    await registerPromptRoutes(app, store);
    await app.ready();
  });

  afterEach(async () => {
    await app.close();
    try {
      await rm(TEST_FILE_PATH, { force: true });
    } catch {
      // ignore
    }
  });

  // -----------------------------------------------------------------------
  // GET /api/prompts
  // -----------------------------------------------------------------------

  describe('GET /api/prompts', () => {
    it('should return empty list when no templates exist', async () => {
      const res = await app.inject({
        method: 'GET',
        url: '/api/prompts',
      });

      expect(res.statusCode).toBe(200);
      const body = res.json();
      expect(body.templates).toEqual([]);
      expect(body.total).toBe(0);
    });

    it('should return all templates as summaries', async () => {
      await store.upsert('developer', 'plan', { template: 'plan {{x}}' });
      await store.upsert('tester', 'write-tests', { template: 'test {{y}}' });

      const res = await app.inject({
        method: 'GET',
        url: '/api/prompts',
      });

      expect(res.statusCode).toBe(200);
      const body = res.json();
      expect(body.total).toBe(2);
      expect(body.templates[0].role).toBe('developer');
      expect(body.templates[0].action).toBe('plan');
      expect(body.templates[1].role).toBe('tester');
      expect(body.templates[1].action).toBe('write-tests');
    });
  });

  // -----------------------------------------------------------------------
  // GET /api/prompts/:role/:action
  // -----------------------------------------------------------------------

  describe('GET /api/prompts/:role/:action', () => {
    it('should return 404 for non-existent template', async () => {
      const res = await app.inject({
        method: 'GET',
        url: '/api/prompts/developer/nonexistent',
      });

      expect(res.statusCode).toBe(404);
      expect(res.json().error).toContain('not found');
    });

    it('should return template with full details', async () => {
      await store.upsert('developer', 'plan', {
        template: 'Plan {{task}}',
        systemPrompt: 'You are a planner.',
        enableTools: true,
        maxTokens: 8192,
      });

      const res = await app.inject({
        method: 'GET',
        url: '/api/prompts/developer/plan',
      });

      expect(res.statusCode).toBe(200);
      const body = res.json();
      expect(body.role).toBe('developer');
      expect(body.action).toBe('plan');
      expect(body.version).toBe(1);
      expect(body.template).toBe('Plan {{task}}');
      expect(body.systemPrompt).toBe('You are a planner.');
      expect(body.enableTools).toBe(true);
      expect(body.maxTokens).toBe(8192);
      expect(body.variables).toEqual(['task']);
      expect(body.createdAt).toBeDefined();
      expect(body.updatedAt).toBeDefined();
    });
  });

  // -----------------------------------------------------------------------
  // PUT /api/prompts/:role/:action
  // -----------------------------------------------------------------------

  describe('PUT /api/prompts/:role/:action', () => {
    it('should create a new template', async () => {
      const res = await app.inject({
        method: 'PUT',
        url: '/api/prompts/developer/implement',
        payload: {
          template: 'Implement {{feature}} in {{lang}}',
          systemPrompt: 'You are an implementer.',
          enableTools: true,
          maxTokens: 16384,
        },
      });

      expect(res.statusCode).toBe(200);
      const body = res.json();
      expect(body.role).toBe('developer');
      expect(body.action).toBe('implement');
      expect(body.version).toBe(1);
      expect(body.variables).toContain('feature');
      expect(body.variables).toContain('lang');
    });

    it('should update an existing template and bump version', async () => {
      await app.inject({
        method: 'PUT',
        url: '/api/prompts/developer/plan',
        payload: { template: 'v1 {{x}}' },
      });

      const res = await app.inject({
        method: 'PUT',
        url: '/api/prompts/developer/plan',
        payload: { template: 'v2 {{x}} {{y}}' },
      });

      expect(res.statusCode).toBe(200);
      expect(res.json().version).toBe(2);
      expect(res.json().template).toBe('v2 {{x}} {{y}}');
    });

    it('should return 400 for missing template field', async () => {
      const res = await app.inject({
        method: 'PUT',
        url: '/api/prompts/developer/plan',
        payload: { systemPrompt: 'no template' },
      });

      expect(res.statusCode).toBe(400);
      expect(res.json().error).toContain('template');
    });

    it('should return 400 for empty template string', async () => {
      const res = await app.inject({
        method: 'PUT',
        url: '/api/prompts/developer/plan',
        payload: { template: '' },
      });

      expect(res.statusCode).toBe(400);
      expect(res.json().error).toContain('template');
    });

    it('should return 400 for invalid maxTokens', async () => {
      const res = await app.inject({
        method: 'PUT',
        url: '/api/prompts/developer/plan',
        payload: { template: 'ok', maxTokens: -1 },
      });

      expect(res.statusCode).toBe(400);
      expect(res.json().error).toContain('maxTokens');
    });

    it('should return 400 for forbidden role name', async () => {
      const res = await app.inject({
        method: 'PUT',
        url: '/api/prompts/__proto__/plan',
        payload: { template: 'evil' },
      });

      expect(res.statusCode).toBe(400);
      expect(res.json().error).toContain('Forbidden');
    });

    it('should accept custom variables list', async () => {
      const res = await app.inject({
        method: 'PUT',
        url: '/api/prompts/developer/plan',
        payload: {
          template: 'Plan {{x}}',
          variables: ['x', 'y', 'z'],
        },
      });

      expect(res.statusCode).toBe(200);
      expect(res.json().variables).toEqual(['x', 'y', 'z']);
    });
  });

  // -----------------------------------------------------------------------
  // POST /api/prompts/:role/:action/render
  // -----------------------------------------------------------------------

  describe('POST /api/prompts/:role/:action/render', () => {
    it('should render template with provided variables', async () => {
      await store.upsert('developer', 'implement', {
        template: 'Implement {{feature}} using {{framework}}.',
        systemPrompt: 'You are a {{role}} developer.',
      });

      const res = await app.inject({
        method: 'POST',
        url: '/api/prompts/developer/implement/render',
        payload: {
          variables: {
            feature: 'authentication',
            framework: 'Fastify',
            role: 'senior',
          },
        },
      });

      expect(res.statusCode).toBe(200);
      const body = res.json();
      expect(body.renderedTemplate).toBe('Implement authentication using Fastify.');
      expect(body.renderedSystemPrompt).toBe('You are a senior developer.');
      expect(body.version).toBe(1);
      expect(body.unresolvedVariables).toEqual([]);
    });

    it('should report unresolved variables', async () => {
      await store.upsert('developer', 'plan', {
        template: 'Plan {{task}} by {{deadline}} for {{team}}.',
      });

      const res = await app.inject({
        method: 'POST',
        url: '/api/prompts/developer/plan/render',
        payload: {
          variables: { task: 'refactor' },
        },
      });

      expect(res.statusCode).toBe(200);
      const body = res.json();
      expect(body.renderedTemplate).toBe('Plan refactor by {{deadline}} for {{team}}.');
      expect(body.unresolvedVariables).toContain('deadline');
      expect(body.unresolvedVariables).toContain('team');
    });

    it('should return 404 for non-existent template', async () => {
      const res = await app.inject({
        method: 'POST',
        url: '/api/prompts/developer/nonexistent/render',
        payload: { variables: {} },
      });

      expect(res.statusCode).toBe(404);
    });

    it('should return 400 when variables is missing', async () => {
      const res = await app.inject({
        method: 'POST',
        url: '/api/prompts/developer/plan/render',
        payload: {},
      });

      expect(res.statusCode).toBe(400);
      expect(res.json().error).toContain('variables');
    });

    it('should return 400 when variables is not an object', async () => {
      const res = await app.inject({
        method: 'POST',
        url: '/api/prompts/developer/plan/render',
        payload: { variables: 'not-an-object' },
      });

      expect(res.statusCode).toBe(400);
    });

    it('should return 400 when variable value is not a string', async () => {
      await store.upsert('developer', 'plan', {
        template: 'Plan {{x}}',
      });

      const res = await app.inject({
        method: 'POST',
        url: '/api/prompts/developer/plan/render',
        payload: { variables: { x: 123 } },
      });

      expect(res.statusCode).toBe(400);
      expect(res.json().error).toContain('string value');
    });

    it('should not recursively expand variables', async () => {
      await store.upsert('developer', 'plan', {
        template: 'Plan: {{task}}',
      });

      const res = await app.inject({
        method: 'POST',
        url: '/api/prompts/developer/plan/render',
        payload: {
          variables: { task: '{{secret}}' },
        },
      });

      expect(res.statusCode).toBe(200);
      expect(res.json().renderedTemplate).toBe('Plan: {{secret}}');
    });

    it('should include enableTools and maxTokens in response', async () => {
      await store.upsert('developer', 'plan', {
        template: 'Plan',
        enableTools: true,
        maxTokens: 16384,
      });

      const res = await app.inject({
        method: 'POST',
        url: '/api/prompts/developer/plan/render',
        payload: { variables: {} },
      });

      expect(res.statusCode).toBe(200);
      expect(res.json().enableTools).toBe(true);
      expect(res.json().maxTokens).toBe(16384);
    });
  });

  // -----------------------------------------------------------------------
  // Default prompts integration
  // -----------------------------------------------------------------------

  describe('Default prompts integration', () => {
    let appWithDefaults: FastifyInstance;

    beforeEach(async () => {
      const defaultStore = new PromptStore({
        filePath: '/tmp/tamma-test-prompt-routes-defaults.json',
        skipDefaults: false,
      });

      appWithDefaults = Fastify({ logger: false });
      await registerPromptRoutes(appWithDefaults, defaultStore);
      await appWithDefaults.ready();
    });

    afterEach(async () => {
      await appWithDefaults.close();
      try {
        await rm('/tmp/tamma-test-prompt-routes-defaults.json', { force: true });
      } catch {
        // ignore
      }
    });

    it('should serve default prompts for all role+action pairs', async () => {
      const res = await appWithDefaults.inject({
        method: 'GET',
        url: '/api/prompts',
      });

      expect(res.statusCode).toBe(200);
      expect(res.json().total).toBe(80); // 8 roles * 10 actions
    });

    it('should serve a specific default prompt', async () => {
      const res = await appWithDefaults.inject({
        method: 'GET',
        url: '/api/prompts/developer/context-scan',
      });

      expect(res.statusCode).toBe(200);
      const body = res.json();
      expect(body.role).toBe('developer');
      expect(body.action).toBe('context-scan');
      expect(body.version).toBe(1);
      expect(body.template).toContain('{{workItemJson}}');
      expect(body.enableTools).toBe(true);
    });

    it('should render a default prompt with variables', async () => {
      const res = await appWithDefaults.inject({
        method: 'POST',
        url: '/api/prompts/developer/context-scan/render',
        payload: {
          variables: {
            role: 'developer',
            workItemType: 'feature',
            workItemJson: '{"title": "Add login"}',
            previousFindings: 'None',
          },
        },
      });

      expect(res.statusCode).toBe(200);
      const body = res.json();
      expect(body.renderedTemplate).toContain('feature');
      expect(body.renderedTemplate).toContain('Add login');
      expect(body.renderedTemplate).not.toContain('{{workItemType}}');
    });
  });
});
