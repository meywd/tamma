/**
 * Settings Routes
 *
 * Registers all settings-related routes under /api/config and /api/providers.
 *
 * RBAC enforcement:
 *   GET  /api/config/*    -> requires 'settings:view' (admin, owner)
 *   PUT  /api/config/*    -> requires 'settings:manage' (owner only)
 *   GET  /api/providers/* -> requires 'settings:view' (admin, owner)
 */

import type { FastifyInstance } from 'fastify';
import { ConfigService } from '../../services/settings/ConfigService.js';
import { HealthService } from '../../services/settings/HealthService.js';
import { DiagnosticsService } from '../../services/settings/DiagnosticsService.js';
import { registerAgentsRoutes } from './agents-routes.js';
import { registerSecurityRoutes } from './security-routes.js';
import { registerHealthRoutes } from './health-routes.js';
import { registerDiagnosticsRoutes } from './diagnostics-routes.js';
import { registerDiagnosticsIngestRoutes } from './diagnostics-ingest-routes.js';
import { registerPromptsRoutes } from './prompts-routes.js';
import { registerProvidersRoutes } from './providers-routes.js';
import { registerProviderFactoryRoutes } from './providers-factory-routes.js';
import { requirePermission } from '../../auth/require-permission.js';
import type { IDiagnosticsStore } from '../../services/diagnostics-store.js';
import type { IHealthStore } from '../../services/health-store.js';
import type { IProviderSessionService } from '../../services/provider-session.js';
import type { ISanitizationStore } from '../../services/sanitization-store.js';

export interface SettingsServices {
  configService: ConfigService;
  healthService: HealthService;
  diagnosticsService: DiagnosticsService;
  /** Story 9-2: Persistent diagnostics store (optional for backward compat). */
  diagnosticsStore?: IDiagnosticsStore;
  /** Story 9-3: Persistent health store (optional for backward compat). */
  healthStore?: IHealthStore;
  /** Story 9-4: Provider session service (optional for backward compat). */
  providerSessionService?: IProviderSessionService;
  /** Story 9-7: Sanitization store (optional for backward compat). */
  sanitizationStore?: ISanitizationStore;
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

  // /api/config/* routes -- admin/owner for GET, owner-only for PUT
  await app.register(
    async (instance) => {
      // Apply RBAC: GET -> settings:view, PUT -> settings:manage
      instance.addHook('onRequest', async (request, reply) => {
        if (request.method === 'GET') {
          await requirePermission('settings:view')(request, reply);
        } else if (request.method === 'PUT' || request.method === 'POST') {
          await requirePermission('settings:manage')(request, reply);
        }
      });

      registerAgentsRoutes(instance, svc.configService);
      registerSecurityRoutes(instance, svc.configService, svc.sanitizationStore);
      registerPromptsRoutes(instance, svc.configService);
      registerProvidersRoutes(instance, svc.configService);
    },
    { prefix: '/api/config' },
  );

  // /api/providers/* routes -- admin/owner for health & diagnostics
  await app.register(
    async (instance) => {
      instance.addHook('onRequest', async (request, reply) => {
        await requirePermission('settings:view')(request, reply);
      });

      registerHealthRoutes(instance, svc.healthService, svc.healthStore);
      registerDiagnosticsRoutes(instance, svc.diagnosticsService, svc.diagnosticsStore);
      registerDiagnosticsIngestRoutes(instance, svc.diagnosticsStore ?? {
        insert: async () => 0,
        query: async () => ({ items: [], total: 0 }),
        report: async () => [],
        getBudget: async () => ({ spent: 0, limit: 0, remaining: 0, percentUsed: 0 }),
      });

      // Story 9-4: Provider factory routes
      if (svc.providerSessionService) {
        registerProviderFactoryRoutes(instance, svc.providerSessionService);
      }
    },
    { prefix: '/api/providers' },
  );
}
