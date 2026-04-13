/**
 * Tests for the /api/v1/agents/config routes.
 *
 * Uses InMemoryAgentConfigStore — no real database required.
 */

import { describe, it, expect, beforeEach } from 'vitest';
import Fastify from 'fastify';
import type { FastifyInstance } from 'fastify';
import { InMemoryAgentConfigStore } from '../../../persistence/agent-config-store.js';
import type { IAgentConfigStore, AgentConfigDocument } from '../../../persistence/agent-config-store.js';
import { registerAgentConfigRoutes } from '../agent-config-routes.js';

describe('Agent Config Routes', () => {
  let app: FastifyInstance;
  let store: IAgentConfigStore;

  beforeEach(async () => {
    store = new InMemoryAgentConfigStore();

    app = Fastify();
    await app.register(async (instance) => {
      await registerAgentConfigRoutes(instance, { store });
    }, { prefix: '/api/v1/agents' });
    await app.ready();
  });

  // -----------------------------------------------------------------------
  // GET /api/v1/agents/config
  // -----------------------------------------------------------------------

  describe('GET /api/v1/agents/config', () => {
    it('returns system default config for unknown account', async () => {
      const response = await app.inject({
        method: 'GET',
        url: '/api/v1/agents/config',
      });

      expect(response.statusCode).toBe(200);
      const body = response.json();
      expect(body.source).toBe('system');
      expect(body.config).toBeDefined();
      expect(body.config.defaults.providerChain).toHaveLength(1);
      expect(body.config.defaults.providerChain[0].provider).toBe('claude-code');
      expect(body.security).toBeDefined();
      expect(body.version).toBe(1);
    });

    it('returns account config after PUT', async () => {
      // First PUT
      await app.inject({
        method: 'PUT',
        url: '/api/v1/agents/config',
        headers: { 'content-type': 'application/json' },
        payload: {
          config: {
            defaults: {
              providerChain: [{ provider: 'openrouter', model: 'z-ai/z1-mini' }],
              maxBudgetUsd: 10.0,
            },
          },
        },
      });

      const response = await app.inject({
        method: 'GET',
        url: '/api/v1/agents/config',
      });

      expect(response.statusCode).toBe(200);
      const body = response.json();
      // Since no authUser is set, account defaults to DEFAULT_ACCOUNT_ID
      expect(body.config.defaults.providerChain[0].provider).toBe('openrouter');
      expect(body.config.defaults.maxBudgetUsd).toBe(10.0);
    });
  });

  // -----------------------------------------------------------------------
  // PUT /api/v1/agents/config
  // -----------------------------------------------------------------------

  describe('PUT /api/v1/agents/config', () => {
    it('upserts with valid config', async () => {
      const response = await app.inject({
        method: 'PUT',
        url: '/api/v1/agents/config',
        headers: { 'content-type': 'application/json' },
        payload: {
          config: {
            defaults: {
              providerChain: [{ provider: 'zen-mcp' }],
            },
          },
        },
      });

      expect(response.statusCode).toBe(200);
      const body = response.json();
      expect(body.config.defaults.providerChain[0].provider).toBe('zen-mcp');
      expect(body.version).toBe(1);
    });

    it('upserts with security only', async () => {
      const response = await app.inject({
        method: 'PUT',
        url: '/api/v1/agents/config',
        headers: { 'content-type': 'application/json' },
        payload: {
          security: {
            sanitizeContent: false,
            validateUrls: false,
            gateActions: true,
            maxFetchSizeBytes: 1_048_576,
          },
        },
      });

      expect(response.statusCode).toBe(200);
      const body = response.json();
      expect(body.security.sanitizeContent).toBe(false);
      expect(body.security.gateActions).toBe(true);
      // Config should inherit from existing resolved value
      expect(body.config.defaults.providerChain).toBeDefined();
    });

    it('increments version on subsequent PUTs', async () => {
      const payload = {
        config: {
          defaults: { providerChain: [{ provider: 'opencode' }] },
        },
      };

      const first = await app.inject({
        method: 'PUT',
        url: '/api/v1/agents/config',
        headers: { 'content-type': 'application/json' },
        payload,
      });
      expect(first.json().version).toBe(1);

      const second = await app.inject({
        method: 'PUT',
        url: '/api/v1/agents/config',
        headers: { 'content-type': 'application/json' },
        payload,
      });
      expect(second.json().version).toBe(2);
    });

    it('returns 400 for empty body', async () => {
      const response = await app.inject({
        method: 'PUT',
        url: '/api/v1/agents/config',
        headers: { 'content-type': 'application/json' },
        payload: {},
      });

      expect(response.statusCode).toBe(400);
      expect(response.json().error).toContain('at least one of');
    });

    it('returns 400 for invalid provider name', async () => {
      const response = await app.inject({
        method: 'PUT',
        url: '/api/v1/agents/config',
        headers: { 'content-type': 'application/json' },
        payload: {
          config: {
            defaults: {
              providerChain: [{ provider: '__proto__' }],
            },
          },
        },
      });

      expect(response.statusCode).toBe(400);
      const body = response.json();
      expect(body.errors).toBeDefined();
      expect(body.errors.length).toBeGreaterThan(0);
      expect(body.errors[0]).toContain('forbidden');
    });

    it('returns 400 for empty provider chain', async () => {
      const response = await app.inject({
        method: 'PUT',
        url: '/api/v1/agents/config',
        headers: { 'content-type': 'application/json' },
        payload: {
          config: {
            defaults: {
              providerChain: [],
            },
          },
        },
      });

      expect(response.statusCode).toBe(400);
      const body = response.json();
      expect(body.errors.length).toBeGreaterThan(0);
      expect(body.errors[0]).toContain('providerChain');
    });

    it('returns 400 for invalid maxBudgetUsd', async () => {
      const response = await app.inject({
        method: 'PUT',
        url: '/api/v1/agents/config',
        headers: { 'content-type': 'application/json' },
        payload: {
          config: {
            defaults: {
              providerChain: [{ provider: 'claude-code' }],
              maxBudgetUsd: 999,
            },
          },
        },
      });

      expect(response.statusCode).toBe(400);
      const body = response.json();
      expect(body.errors.length).toBeGreaterThan(0);
    });

    it('returns 400 for invalid security maxFetchSizeBytes', async () => {
      const response = await app.inject({
        method: 'PUT',
        url: '/api/v1/agents/config',
        headers: { 'content-type': 'application/json' },
        payload: {
          security: { maxFetchSizeBytes: -1 },
        },
      });

      expect(response.statusCode).toBe(400);
      const body = response.json();
      expect(body.errors.length).toBeGreaterThan(0);
    });

    it('returns 400 for invalid blocked command pattern', async () => {
      const response = await app.inject({
        method: 'PUT',
        url: '/api/v1/agents/config',
        headers: { 'content-type': 'application/json' },
        payload: {
          security: { blockedCommandPatterns: ['[invalid'] },
        },
      });

      expect(response.statusCode).toBe(400);
      const body = response.json();
      expect(body.errors.length).toBeGreaterThan(0);
    });
  });

  // -----------------------------------------------------------------------
  // POST /api/v1/agents/config/validate
  // -----------------------------------------------------------------------

  describe('POST /api/v1/agents/config/validate', () => {
    it('returns valid for good config', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/v1/agents/config/validate',
        headers: { 'content-type': 'application/json' },
        payload: {
          config: {
            defaults: {
              providerChain: [{ provider: 'claude-code' }],
              maxBudgetUsd: 5.0,
            },
          },
          security: {
            sanitizeContent: true,
            validateUrls: true,
            maxFetchSizeBytes: 1_048_576,
          },
        },
      });

      expect(response.statusCode).toBe(200);
      const body = response.json();
      expect(body.valid).toBe(true);
      expect(body.errors).toEqual([]);
    });

    it('returns valid for empty security config', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/v1/agents/config/validate',
        headers: { 'content-type': 'application/json' },
        payload: {
          config: {
            defaults: {
              providerChain: [{ provider: 'opencode' }],
            },
          },
          security: {},
        },
      });

      expect(response.statusCode).toBe(200);
      expect(response.json().valid).toBe(true);
    });

    it('returns invalid for bad config', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/v1/agents/config/validate',
        headers: { 'content-type': 'application/json' },
        payload: {
          config: {
            defaults: {
              providerChain: [],
            },
          },
        },
      });

      expect(response.statusCode).toBe(200);
      const body = response.json();
      expect(body.valid).toBe(false);
      expect(body.errors.length).toBeGreaterThan(0);
    });

    it('returns invalid for bad security config', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/v1/agents/config/validate',
        headers: { 'content-type': 'application/json' },
        payload: {
          security: { maxFetchSizeBytes: -1 },
        },
      });

      expect(response.statusCode).toBe(200);
      const body = response.json();
      expect(body.valid).toBe(false);
      expect(body.errors.length).toBeGreaterThan(0);
    });

    it('returns multiple errors when both config and security are invalid', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/v1/agents/config/validate',
        headers: { 'content-type': 'application/json' },
        payload: {
          config: {
            defaults: {
              providerChain: [],
            },
          },
          security: { maxFetchSizeBytes: -1 },
        },
      });

      expect(response.statusCode).toBe(200);
      const body = response.json();
      expect(body.valid).toBe(false);
      expect(body.errors.length).toBe(2);
    });

    it('returns 400 for empty body', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/v1/agents/config/validate',
        headers: { 'content-type': 'application/json' },
        payload: {},
      });

      expect(response.statusCode).toBe(400);
    });

    it('validates role overrides', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/v1/agents/config/validate',
        headers: { 'content-type': 'application/json' },
        payload: {
          config: {
            defaults: {
              providerChain: [{ provider: 'claude-code' }],
            },
            roles: {
              architect: {
                providerChain: [{ provider: 'INVALID-UPPERCASE' }],
              },
            },
          },
        },
      });

      expect(response.statusCode).toBe(200);
      const body = response.json();
      expect(body.valid).toBe(false);
      expect(body.errors.length).toBeGreaterThan(0);
    });

    it('validates phaseRoleMap config is accepted', async () => {
      const response = await app.inject({
        method: 'POST',
        url: '/api/v1/agents/config/validate',
        headers: { 'content-type': 'application/json' },
        payload: {
          config: {
            defaults: {
              providerChain: [{ provider: 'claude-code' }],
            },
            phaseRoleMap: {
              CODE_GENERATION: 'architect',
            },
          },
        },
      });

      expect(response.statusCode).toBe(200);
      expect(response.json().valid).toBe(true);
    });
  });
});
