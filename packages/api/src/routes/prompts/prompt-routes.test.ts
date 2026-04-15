/**
 * Tests for Prompt Registry API Routes
 *
 * Tests tenant-scoped and system default routes using Fastify's
 * inject() method for lightweight HTTP testing without network.
 *
 * Story 12-5: Prompt Engineering Framework
 * Story 27-3: Prompt Store API Endpoints
 */

import { describe, it, expect, beforeEach, afterEach } from 'vitest';
import Fastify, { type FastifyInstance } from 'fastify';
import { registerPromptRoutes } from './prompt-routes.js';
import { InMemoryPromptStore } from '../../services/in-memory-prompt-store.js';
import type { IPromptStore } from '../../services/prompt-store.js';

describe('Prompt Registry Routes (Tenant-Scoped)', () => {
  let app: FastifyInstance;
  let store: IPromptStore;

  beforeEach(async () => {
    store = new InMemoryPromptStore({ skipDefaults: true });

    app = Fastify({ logger: false });
    await registerPromptRoutes(app, store);
    await app.ready();
  });

  afterEach(async () => {
    await app.close();
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

    it('should return system defaults when no tenant header', async () => {
      await store.upsert(null, 'developer', 'plan', { template: 'system plan {{x}}' });

      const res = await app.inject({
        method: 'GET',
        url: '/api/prompts',
      });

      expect(res.statusCode).toBe(200);
      const body = res.json();
      expect(body.total).toBe(1);
      expect(body.templates[0].role).toBe('developer');
    });

    it('should return merged list when tenant header is provided', async () => {
      // Create system default and tenant override
      await store.upsert(null, 'developer', 'plan', { template: 'system plan' });
      await store.upsert('tenant-1', 'developer', 'plan', { template: 'tenant plan' });
      await store.upsert(null, 'tester', 'write-tests', { template: 'system tests' });

      const res = await app.inject({
        method: 'GET',
        url: '/api/prompts',
        headers: { 'x-tenant-id': 'tenant-1' },
      });

      expect(res.statusCode).toBe(200);
      const body = res.json();
      expect(body.total).toBe(2); // developer/plan (overridden) + tester/write-tests (system)
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

    it('should return system default when no tenant override', async () => {
      await store.upsert(null, 'developer', 'plan', {
        template: 'System plan {{task}}',
        systemPrompt: 'You are a planner.',
        enableTools: true,
        maxTokens: 8192,
      });

      const res = await app.inject({
        method: 'GET',
        url: '/api/prompts/developer/plan',
        headers: { 'x-tenant-id': 'tenant-1' },
      });

      expect(res.statusCode).toBe(200);
      const body = res.json();
      expect(body.template).toBe('System plan {{task}}');
    });

    it('should return tenant override when it exists', async () => {
      await store.upsert(null, 'developer', 'plan', { template: 'System plan' });
      await store.upsert('tenant-1', 'developer', 'plan', { template: 'Tenant plan' });

      const res = await app.inject({
        method: 'GET',
        url: '/api/prompts/developer/plan',
        headers: { 'x-tenant-id': 'tenant-1' },
      });

      expect(res.statusCode).toBe(200);
      expect(res.json().template).toBe('Tenant plan');
    });

    it('should return system default when no tenant header', async () => {
      await store.upsert(null, 'developer', 'plan', { template: 'System plan' });

      const res = await app.inject({
        method: 'GET',
        url: '/api/prompts/developer/plan',
      });

      expect(res.statusCode).toBe(200);
      expect(res.json().template).toBe('System plan');
    });
  });

  // -----------------------------------------------------------------------
  // PUT /api/prompts/:role/:action
  // -----------------------------------------------------------------------

  describe('PUT /api/prompts/:role/:action', () => {
    it('should create a tenant override when tenant header provided', async () => {
      const res = await app.inject({
        method: 'PUT',
        url: '/api/prompts/developer/implement',
        headers: { 'x-tenant-id': 'tenant-1' },
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
    });

    it('should create system default when no tenant header', async () => {
      const res = await app.inject({
        method: 'PUT',
        url: '/api/prompts/developer/plan',
        payload: { template: 'System plan {{x}}' },
      });

      expect(res.statusCode).toBe(200);
      expect(res.json().version).toBe(1);
    });

    it('should bump version on update', async () => {
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
  // DELETE /api/prompts/:role/:action
  // -----------------------------------------------------------------------

  describe('DELETE /api/prompts/:role/:action', () => {
    it('should delete tenant override and return 204', async () => {
      await store.upsert('tenant-1', 'developer', 'plan', { template: 'Tenant plan' });

      const res = await app.inject({
        method: 'DELETE',
        url: '/api/prompts/developer/plan',
        headers: { 'x-tenant-id': 'tenant-1' },
      });

      expect(res.statusCode).toBe(204);
    });

    it('should return 404 when no tenant override exists', async () => {
      const res = await app.inject({
        method: 'DELETE',
        url: '/api/prompts/developer/plan',
        headers: { 'x-tenant-id': 'tenant-1' },
      });

      expect(res.statusCode).toBe(404);
    });

    it('should return 400 when no tenant context (cannot delete system default)', async () => {
      const res = await app.inject({
        method: 'DELETE',
        url: '/api/prompts/developer/plan',
      });

      expect(res.statusCode).toBe(400);
      expect(res.json().error).toContain('system default');
    });

    it('should fallback to system default after deletion', async () => {
      await store.upsert(null, 'developer', 'plan', { template: 'System plan' });
      await store.upsert('tenant-1', 'developer', 'plan', { template: 'Tenant plan' });

      // Verify tenant override is served
      let res = await app.inject({
        method: 'GET',
        url: '/api/prompts/developer/plan',
        headers: { 'x-tenant-id': 'tenant-1' },
      });
      expect(res.json().template).toBe('Tenant plan');

      // Delete tenant override
      await app.inject({
        method: 'DELETE',
        url: '/api/prompts/developer/plan',
        headers: { 'x-tenant-id': 'tenant-1' },
      });

      // Now should fall back to system default
      res = await app.inject({
        method: 'GET',
        url: '/api/prompts/developer/plan',
        headers: { 'x-tenant-id': 'tenant-1' },
      });
      expect(res.json().template).toBe('System plan');
    });
  });

  // -----------------------------------------------------------------------
  // POST /api/prompts/:role/:action/render
  // -----------------------------------------------------------------------

  describe('POST /api/prompts/:role/:action/render', () => {
    it('should render template with provided variables', async () => {
      await store.upsert(null, 'developer', 'implement', {
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
      expect(body.unresolvedVariables).toEqual([]);
    });

    it('should use tenant-scoped resolution with X-Tenant-Id header', async () => {
      await store.upsert(null, 'developer', 'implement', {
        template: 'System: {{name}}',
      });
      await store.upsert('tenant-1', 'developer', 'implement', {
        template: 'Tenant: {{name}}',
      });

      const res = await app.inject({
        method: 'POST',
        url: '/api/prompts/developer/implement/render',
        headers: { 'x-tenant-id': 'tenant-1' },
        payload: { variables: { name: 'test' } },
      });

      expect(res.statusCode).toBe(200);
      expect(res.json().renderedTemplate).toBe('Tenant: test');
    });

    it('should use tenantId query parameter as fallback', async () => {
      await store.upsert(null, 'developer', 'implement', {
        template: 'System: {{name}}',
      });
      await store.upsert('tenant-2', 'developer', 'implement', {
        template: 'Tenant2: {{name}}',
      });

      const res = await app.inject({
        method: 'POST',
        url: '/api/prompts/developer/implement/render?tenantId=tenant-2',
        payload: { variables: { name: 'test' } },
      });

      expect(res.statusCode).toBe(200);
      expect(res.json().renderedTemplate).toBe('Tenant2: test');
    });

    it('should report unresolved variables', async () => {
      await store.upsert(null, 'developer', 'plan', {
        template: 'Plan {{task}} by {{deadline}} for {{team}}.',
      });

      const res = await app.inject({
        method: 'POST',
        url: '/api/prompts/developer/plan/render',
        payload: { variables: { task: 'refactor' } },
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
      await store.upsert(null, 'developer', 'plan', {
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
      await store.upsert(null, 'developer', 'plan', {
        template: 'Plan: {{task}}',
      });

      const res = await app.inject({
        method: 'POST',
        url: '/api/prompts/developer/plan/render',
        payload: { variables: { task: '{{secret}}' } },
      });

      expect(res.statusCode).toBe(200);
      expect(res.json().renderedTemplate).toBe('Plan: {{secret}}');
    });

    it('should include enableTools and maxTokens in response', async () => {
      await store.upsert(null, 'developer', 'plan', {
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
  // System default routes
  // -----------------------------------------------------------------------

  describe('GET /api/prompts/system', () => {
    it('should return system defaults only', async () => {
      await store.upsert(null, 'developer', 'plan', { template: 'system' });
      await store.upsert('tenant-1', 'developer', 'plan', { template: 'tenant' });

      const res = await app.inject({
        method: 'GET',
        url: '/api/prompts/system',
      });

      expect(res.statusCode).toBe(200);
      const body = res.json();
      expect(body.total).toBe(1); // only system default
    });
  });

  describe('GET /api/prompts/system/:role/:action', () => {
    it('should return specific system default', async () => {
      await store.upsert(null, 'developer', 'plan', { template: 'system plan' });

      const res = await app.inject({
        method: 'GET',
        url: '/api/prompts/system/developer/plan',
      });

      expect(res.statusCode).toBe(200);
      expect(res.json().template).toBe('system plan');
    });

    it('should return 404 for non-existent system default', async () => {
      const res = await app.inject({
        method: 'GET',
        url: '/api/prompts/system/developer/nonexistent',
      });

      expect(res.statusCode).toBe(404);
    });
  });

  describe('PUT /api/prompts/system/:role/:action', () => {
    it('should update system default (no auth = dev mode = allowed)', async () => {
      const res = await app.inject({
        method: 'PUT',
        url: '/api/prompts/system/developer/plan',
        payload: { template: 'Updated system plan {{x}}' },
      });

      expect(res.statusCode).toBe(200);
      expect(res.json().template).toBe('Updated system plan {{x}}');
    });

    it('should reject non-platform-admin when auth is configured', async () => {
      // Create app with auth decoration (simulating an authenticated non-admin user)
      const authApp = Fastify({ logger: false });
      authApp.decorateRequest('authUser', null);
      authApp.addHook('onRequest', async (request) => {
        (request as typeof request & { authUser: { id: string; role: string } }).authUser = {
          id: 'user-1',
          role: 'member',
        };
      });
      await registerPromptRoutes(authApp, store);
      await authApp.ready();

      const res = await authApp.inject({
        method: 'PUT',
        url: '/api/prompts/system/developer/plan',
        payload: { template: 'evil plan' },
      });

      expect(res.statusCode).toBe(403);
      expect(res.json().error).toContain('platform administrators');

      await authApp.close();
    });

    it('should allow platform admin (owner role)', async () => {
      const authApp = Fastify({ logger: false });
      authApp.decorateRequest('authUser', null);
      authApp.addHook('onRequest', async (request) => {
        (request as typeof request & { authUser: { id: string; role: string } }).authUser = {
          id: 'admin-1',
          role: 'owner',
        };
      });
      await registerPromptRoutes(authApp, store);
      await authApp.ready();

      const res = await authApp.inject({
        method: 'PUT',
        url: '/api/prompts/system/developer/plan',
        payload: { template: 'Admin updated plan' },
      });

      expect(res.statusCode).toBe(200);
      expect(res.json().template).toBe('Admin updated plan');

      await authApp.close();
    });

    it('should return 400 for invalid body', async () => {
      const res = await app.inject({
        method: 'PUT',
        url: '/api/prompts/system/developer/plan',
        payload: { template: '' },
      });

      expect(res.statusCode).toBe(400);
    });
  });

  describe('DELETE /api/prompts/system/:role/:action', () => {
    it('should reset system default to hardcoded (no auth = dev mode = allowed)', async () => {
      // Use a store with defaults to have something to reset
      const defaultStore = new InMemoryPromptStore({ skipDefaults: false });
      const resetApp = Fastify({ logger: false });
      await registerPromptRoutes(resetApp, defaultStore);
      await resetApp.ready();

      // Modify the system default
      await defaultStore.upsert(null, 'developer', 'context-scan', {
        template: 'Custom template',
      });

      // Reset
      const res = await resetApp.inject({
        method: 'DELETE',
        url: '/api/prompts/system/developer/context-scan',
      });

      expect(res.statusCode).toBe(200);
      // Should have been restored to hardcoded default
      expect(res.json().template).toContain('{{workItemJson}}');

      await resetApp.close();
    });

    it('should return 404 for role/action with no hardcoded default', async () => {
      const res = await app.inject({
        method: 'DELETE',
        url: '/api/prompts/system/custom-role/custom-action',
      });

      expect(res.statusCode).toBe(404);
    });
  });

  // -----------------------------------------------------------------------
  // Default prompts integration
  // -----------------------------------------------------------------------

  describe('Default prompts integration', () => {
    let appWithDefaults: FastifyInstance;

    beforeEach(async () => {
      const defaultStore = new InMemoryPromptStore({ skipDefaults: false });

      appWithDefaults = Fastify({ logger: false });
      await registerPromptRoutes(appWithDefaults, defaultStore);
      await appWithDefaults.ready();
    });

    afterEach(async () => {
      await appWithDefaults.close();
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

  // -----------------------------------------------------------------------
  // Route parameter conflict: "system" vs ":role"
  // -----------------------------------------------------------------------

  describe('Route parameter conflict resolution', () => {
    it('GET /api/prompts/system should list system defaults, not match :role=system', async () => {
      await store.upsert(null, 'developer', 'plan', { template: 'system plan' });

      const res = await app.inject({
        method: 'GET',
        url: '/api/prompts/system',
      });

      expect(res.statusCode).toBe(200);
      expect(res.json().templates).toBeDefined();
    });

    it('GET /api/prompts/system/developer/plan should get system default', async () => {
      await store.upsert(null, 'developer', 'plan', { template: 'system plan' });

      const res = await app.inject({
        method: 'GET',
        url: '/api/prompts/system/developer/plan',
      });

      expect(res.statusCode).toBe(200);
      expect(res.json().template).toBe('system plan');
    });
  });

  // -----------------------------------------------------------------------
  // Tenant override lifecycle
  // -----------------------------------------------------------------------

  describe('Tenant override lifecycle', () => {
    it('should support full create, read, update, delete, fallback cycle', async () => {
      // 1. Create system default
      await store.upsert(null, 'developer', 'plan', { template: 'System plan' });

      // 2. Read (no tenant) → system default
      let res = await app.inject({
        method: 'GET',
        url: '/api/prompts/developer/plan',
      });
      expect(res.json().template).toBe('System plan');

      // 3. Create tenant override
      res = await app.inject({
        method: 'PUT',
        url: '/api/prompts/developer/plan',
        headers: { 'x-tenant-id': 'tenant-1' },
        payload: { template: 'Tenant v1' },
      });
      expect(res.statusCode).toBe(200);
      expect(res.json().version).toBe(1);

      // 4. Read (tenant) → tenant override
      res = await app.inject({
        method: 'GET',
        url: '/api/prompts/developer/plan',
        headers: { 'x-tenant-id': 'tenant-1' },
      });
      expect(res.json().template).toBe('Tenant v1');

      // 5. Update tenant override
      res = await app.inject({
        method: 'PUT',
        url: '/api/prompts/developer/plan',
        headers: { 'x-tenant-id': 'tenant-1' },
        payload: { template: 'Tenant v2' },
      });
      expect(res.json().version).toBe(2);

      // 6. Delete tenant override
      res = await app.inject({
        method: 'DELETE',
        url: '/api/prompts/developer/plan',
        headers: { 'x-tenant-id': 'tenant-1' },
      });
      expect(res.statusCode).toBe(204);

      // 7. Read (tenant) → falls back to system default
      res = await app.inject({
        method: 'GET',
        url: '/api/prompts/developer/plan',
        headers: { 'x-tenant-id': 'tenant-1' },
      });
      expect(res.json().template).toBe('System plan');
    });
  });
});
