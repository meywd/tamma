/**
 * Settings Routes
 *
 * Registers all settings-related routes under /api/config and /api/providers.
 *
 * RBAC enforcement:
 *   GET  /api/config/*    → requires 'settings:view' (admin, owner)
 *   PUT  /api/config/*    → requires 'settings:manage' (owner only)
 *   GET  /api/providers/* → requires 'settings:view' (admin, owner)
 */

import type { FastifyInstance } from 'fastify';
import { ConfigService } from '../../services/settings/ConfigService.js';
import { HealthService } from '../../services/settings/HealthService.js';
import { DiagnosticsService } from '../../services/settings/DiagnosticsService.js';
import { registerAgentsRoutes } from './agents-routes.js';
import { registerSecurityRoutes } from './security-routes.js';
import { registerHealthRoutes } from './health-routes.js';
import { registerDiagnosticsRoutes } from './diagnostics-routes.js';
import { registerPromptsRoutes } from './prompts-routes.js';
import { registerProvidersRoutes } from './providers-routes.js';
import { requirePermission } from '../../auth/require-permission.js';

export interface SettingsServices {
  configService: ConfigService;
  healthService: HealthService;
  diagnosticsService: DiagnosticsService;
}

export function createSettingsServices(): SettingsServices {
  return {
    configService: new ConfigService(),
    healthService: new HealthService(),
    diagnosticsService: new DiagnosticsService(),
  };
}

export async function registerSettingsRoutes(
  app: FastifyInstance,
  services?: SettingsServices,
): Promise<void> {
  const svc = services ?? createSettingsServices();

  // /api/config/* routes — admin/owner for GET, owner-only for PUT
  await app.register(
    async (instance) => {
      // Apply RBAC: GET → settings:view, PUT → settings:manage
      instance.addHook('onRequest', async (request, reply) => {
        if (request.method === 'GET') {
          await requirePermission('settings:view')(request, reply);
        } else if (request.method === 'PUT') {
          await requirePermission('settings:manage')(request, reply);
        }
      });

      registerAgentsRoutes(instance, svc.configService);
      registerSecurityRoutes(instance, svc.configService);
      registerPromptsRoutes(instance, svc.configService);
      registerProvidersRoutes(instance, svc.configService);
    },
    { prefix: '/api/config' },
  );

  // /api/providers/* routes — admin/owner for health & diagnostics
  await app.register(
    async (instance) => {
      instance.addHook('onRequest', async (request, reply) => {
        await requirePermission('settings:view')(request, reply);
      });

      registerHealthRoutes(instance, svc.healthService);
      registerDiagnosticsRoutes(instance, svc.diagnosticsService);
    },
    { prefix: '/api/providers' },
  );
}
