/**
 * Shared test helpers for settings route tests.
 *
 * The settings routes are guarded by `requirePermission` via an `onRequest`
 * hook. These tests focus on settings-route behavior rather than RBAC, so
 * each test file needs to stub `authUser` as an owner before the routes run.
 * This helper encapsulates that setup so the 5 settings test suites don't
 * duplicate the same boilerplate.
 *
 * Auth enforcement itself is covered by `create-app-admin-auth.test.ts` and
 * the permissions test suite.
 */

import Fastify from 'fastify';
import type { FastifyInstance } from 'fastify';
import { registerSettingsRoutes, type SettingsServices } from './index.js';

/**
 * Build a Fastify app that exposes the settings routes with a stubbed
 * owner-role auth user. The root-scope `onRequest` hook is registered
 * BEFORE `registerSettingsRoutes`, so it propagates into the encapsulated
 * plugin scopes that wrap the routes with `requirePermission`.
 */
export async function buildSettingsTestApp(
  services: SettingsServices,
): Promise<FastifyInstance> {
  const app = Fastify({ logger: false });
  app.decorateRequest('authUser', null);
  app.addHook('onRequest', async (request) => {
    (request as unknown as {
      authUser: { id: string; role: string; username: string };
    }).authUser = { id: 'test-owner', role: 'owner', username: 'test' };
  });
  await registerSettingsRoutes(app, services);
  return app;
}
