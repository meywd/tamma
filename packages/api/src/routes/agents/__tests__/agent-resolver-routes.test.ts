/**
 * Tests for the Agent Resolver API routes.
 *
 * Story 9-8: Unified Agent Resolver API
 *
 * Tests:
 *   GET  /api/v1/agents/:role/resolve
 *   POST /api/v1/agents/resolve-for-phase
 */

import { describe, it, expect, beforeEach } from 'vitest';
import Fastify from 'fastify';
import type { FastifyInstance } from 'fastify';
import { AgentResolverService } from '../../../services/agent-resolver.js';
import { InMemoryAgentConfigStore } from '../../../persistence/agent-config-store.js';
import type { AgentConfigDocument } from '../../../persistence/agent-config-store.js';
import { InMemoryHealthStore } from '../../../services/health-store.js';
import { InMemoryPromptStore } from '../../../services/in-memory-prompt-store.js';
import { InMemorySanitizationStore } from '../../../services/sanitization-store.js';
import { registerAgentResolverRoutes } from '../agent-resolver-routes.js';
import type { AgentType } from '@tamma/shared';

// ---------------------------------------------------------------------------
// Test helpers
// ---------------------------------------------------------------------------

const TEST_ACCOUNT = '00000000-0000-0000-0000-000000000000';

function createAgentDoc(): AgentConfigDocument {
  return {
    agents: {
      defaults: {
        providerChain: [
          { provider: 'claude-code', model: 'claude-sonnet-4' },
          { provider: 'openrouter', model: 'z-ai/z1-mini' },
        ],
        allowedTools: ['Read', 'Write', 'Bash'],
        maxBudgetUsd: 5.0,
        permissionMode: 'default' as const,
      },
      roles: {
        implementer: {
          providerChain: [{ provider: 'opencode' }],
          allowedTools: ['Read', 'Write'],
          maxBudgetUsd: 8.0,
        },
      },
      phaseRoleMap: {
        CODE_GENERATION: 'implementer' as AgentType,
      },
    },
    security: {
      sanitizeContent: true,
      validateUrls: true,
      gateActions: false,
      maxFetchSizeBytes: 10_485_760,
    },
  };
}

async function buildApp(): Promise<FastifyInstance> {
  const configStore = new InMemoryAgentConfigStore();
  await configStore.upsert(TEST_ACCOUNT, createAgentDoc());

  const healthStore = new InMemoryHealthStore();
  const promptStore = new InMemoryPromptStore({ skipDefaults: true });
  const sanitizationStore = new InMemorySanitizationStore();

  const resolverService = new AgentResolverService({
    configStore,
    healthStore,
    promptStore,
    sanitizationStore,
  });

  const app = Fastify();
  await app.register(
    async (instance) => {
      await registerAgentResolverRoutes(instance, { resolverService });
    },
    { prefix: '/api/v1/agents' },
  );
  await app.ready();

  return app;
}

// ---------------------------------------------------------------------------
// Tests
// ---------------------------------------------------------------------------

describe('Agent Resolver Routes', () => {
  let app: FastifyInstance;

  beforeEach(async () => {
    app = await buildApp();
  });

  // -----------------------------------------------------------------------
  // GET /api/v1/agents/:role/resolve
  // -----------------------------------------------------------------------

  describe('GET /api/v1/agents/:role/resolve', () => {
    it('resolves agent config for a role (defaults)', async () => {
      const response = await app.inject({
        method: 'GET',
        url: '/api/v1/agents/architect/resolve',
      });

      expect(response.statusCode).toBe(200);
      const body = response.json();
      expect(body.role).toBe('architect');
      expect(body.provider).toBeDefined();
      expect(body.provider.name).toBe('claude-code');
      expect(body.provider.model).toBe('claude-sonnet-4');
      expect(body.taskConfig).toBeDefined();
      expect(body.taskConfig.allowedTools).toEqual(['Read', 'Write', 'Bash']);
      expect(body.taskConfig.maxBudgetUsd).toBe(5.0);
      expect(body.taskConfig.permissionMode).toBe('default');
      expect(body.systemPrompt).toBeDefined();
      expect(typeof body.sanitizationEnabled).toBe('boolean');
      expect(Array.isArray(body.chainEntries)).toBe(true);
      expect(body.chainEntries).toHaveLength(2);
    });

    it('resolves agent config for a role-specific chain', async () => {
      const response = await app.inject({
        method: 'GET',
        url: '/api/v1/agents/implementer/resolve',
      });

      expect(response.statusCode).toBe(200);
      const body = response.json();
      expect(body.role).toBe('implementer');
      expect(body.provider.name).toBe('opencode');
      expect(body.taskConfig.allowedTools).toEqual(['Read', 'Write']);
      expect(body.taskConfig.maxBudgetUsd).toBe(8.0);
      expect(body.chainEntries).toHaveLength(1);
    });

    it('accepts query params (projectId, engineId)', async () => {
      const response = await app.inject({
        method: 'GET',
        url: '/api/v1/agents/architect/resolve?projectId=proj-1&engineId=eng-1',
      });

      expect(response.statusCode).toBe(200);
      expect(response.json().role).toBe('architect');
    });

    it('returns 400 for forbidden role name (__proto__)', async () => {
      const response = await app.inject({
        method: 'GET',
        url: '/api/v1/agents/__proto__/resolve',
      });

      expect(response.statusCode).toBe(400);
      expect(response.json().error).toContain('Forbidden');
    });

    it('returns 400 for forbidden role name (constructor)', async () => {
      const response = await app.inject({
        method: 'GET',
        url: '/api/v1/agents/constructor/resolve',
      });

      expect(response.statusCode).toBe(400);
      expect(response.json().error).toContain('Forbidden');
    });

    it('resolves unknown role with defaults', async () => {
      const response = await app.inject({
        method: 'GET',
        url: '/api/v1/agents/custom_role/resolve',
      });

      expect(response.statusCode).toBe(200);
      const body = response.json();
      expect(body.role).toBe('custom_role');
      // Should fall back to default provider chain
      expect(body.provider.name).toBe('claude-code');
    });
  });

  // -----------------------------------------------------------------------
  // POST /api/v1/agents/resolve-for-phase
  // -----------------------------------------------------------------------

  describe('POST /api/v1/agents/resolve-for-phase', () => {
    it('maps phase to role and resolves', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/v1/agents/resolve-for-phase',
        headers: { 'content-type': 'application/json' },
        payload: { phase: 'CODE_GENERATION' },
      });

      expect(response.statusCode).toBe(200);
      const body = response.json();
      expect(body.phase).toBe('CODE_GENERATION');
      expect(body.role).toBe('implementer');
      expect(body.provider.name).toBe('opencode');
      expect(body.taskConfig).toBeDefined();
      expect(body.chainEntries).toBeDefined();
    });

    it('uses default phase-role map for unmapped phases', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/v1/agents/resolve-for-phase',
        headers: { 'content-type': 'application/json' },
        payload: { phase: 'ISSUE_SELECTION' },
      });

      expect(response.statusCode).toBe(200);
      const body = response.json();
      expect(body.phase).toBe('ISSUE_SELECTION');
      expect(body.role).toBe('scrum_master');
    });

    it('accepts projectId and engineId in body', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/v1/agents/resolve-for-phase',
        headers: { 'content-type': 'application/json' },
        payload: {
          phase: 'CODE_GENERATION',
          projectId: 'proj-1',
          engineId: 'eng-1',
        },
      });

      expect(response.statusCode).toBe(200);
      expect(response.json().role).toBe('implementer');
    });

    it('applies task overrides with clamping', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/v1/agents/resolve-for-phase',
        headers: { 'content-type': 'application/json' },
        payload: {
          phase: 'CODE_GENERATION',
          taskOverrides: {
            maxBudgetUsd: 50.0,
            allowedTools: ['Read', 'Exec'],
          },
        },
      });

      expect(response.statusCode).toBe(200);
      const body = response.json();
      // Budget clamped to 8.0 (implementer ceiling)
      expect(body.taskConfig.maxBudgetUsd).toBe(8.0);
      // Tools intersected: implementer has ['Read', 'Write'], override has ['Read', 'Exec']
      expect(body.taskConfig.allowedTools).toEqual(['Read']);
    });

    it('returns 400 for missing phase', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/v1/agents/resolve-for-phase',
        headers: { 'content-type': 'application/json' },
        payload: {},
      });

      expect(response.statusCode).toBe(400);
      expect(response.json().error).toContain('phase');
    });

    it('returns 400 for invalid phase', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/v1/agents/resolve-for-phase',
        headers: { 'content-type': 'application/json' },
        payload: { phase: 'INVALID_PHASE' },
      });

      expect(response.statusCode).toBe(400);
      expect(response.json().error).toContain('Invalid workflow phase');
    });

    it('returns 400 for empty body', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/v1/agents/resolve-for-phase',
        headers: { 'content-type': 'application/json' },
        payload: null,
      });

      expect(response.statusCode).toBe(400);
    });

    it('maps all 8 phases correctly', async () => {
      const expectedMappings: Record<string, string> = {
        ISSUE_SELECTION: 'scrum_master',
        CONTEXT_ANALYSIS: 'analyst',
        PLAN_GENERATION: 'architect',
        CODE_GENERATION: 'implementer', // custom mapping
        PR_CREATION: 'implementer',
        CODE_REVIEW: 'reviewer',
        TEST_EXECUTION: 'tester',
        STATUS_MONITORING: 'scrum_master',
      };

      for (const [phase, expectedRole] of Object.entries(expectedMappings)) {
        const response = await app.inject({
          method: 'POST',
          url: '/api/v1/agents/resolve-for-phase',
          headers: { 'content-type': 'application/json' },
          payload: { phase },
        });

        expect(response.statusCode).toBe(200);
        const body = response.json();
        expect(body.phase).toBe(phase);
        expect(body.role).toBe(expectedRole);
      }
    });
  });

  // -----------------------------------------------------------------------
  // Response shape validation
  // -----------------------------------------------------------------------

  describe('response shape', () => {
    it('GET /:role/resolve returns all expected fields', async () => {
      const response = await app.inject({
        method: 'GET',
        url: '/api/v1/agents/architect/resolve',
      });

      const body = response.json();
      expect(body).toHaveProperty('role');
      expect(body).toHaveProperty('provider');
      expect(body).toHaveProperty('provider.name');
      expect(body).toHaveProperty('provider.model');
      expect(body).toHaveProperty('taskConfig');
      expect(body).toHaveProperty('taskConfig.allowedTools');
      expect(body).toHaveProperty('taskConfig.maxBudgetUsd');
      expect(body).toHaveProperty('taskConfig.permissionMode');
      expect(body).toHaveProperty('systemPrompt');
      expect(body).toHaveProperty('sanitizationEnabled');
      expect(body).toHaveProperty('chainEntries');
    });

    it('POST /resolve-for-phase includes phase and role in response', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/v1/agents/resolve-for-phase',
        headers: { 'content-type': 'application/json' },
        payload: { phase: 'PLAN_GENERATION' },
      });

      const body = response.json();
      expect(body).toHaveProperty('phase', 'PLAN_GENERATION');
      expect(body).toHaveProperty('role', 'architect');
      expect(body).toHaveProperty('provider');
      expect(body).toHaveProperty('taskConfig');
      expect(body).toHaveProperty('systemPrompt');
      expect(body).toHaveProperty('sanitizationEnabled');
      expect(body).toHaveProperty('chainEntries');
    });

    it('chain entries have correct shape', async () => {
      const response = await app.inject({
        method: 'GET',
        url: '/api/v1/agents/architect/resolve',
      });

      const body = response.json();
      for (const entry of body.chainEntries) {
        expect(entry).toHaveProperty('provider');
        expect(entry).toHaveProperty('model');
        expect(entry).toHaveProperty('healthy');
        expect(entry).toHaveProperty('circuitOpen');
        expect(typeof entry.provider).toBe('string');
        expect(typeof entry.model).toBe('string');
        expect(typeof entry.healthy).toBe('boolean');
        expect(typeof entry.circuitOpen).toBe('boolean');
      }
    });
  });
});
