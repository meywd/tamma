/**
 * Settings Routes
 *
 * Registers all settings-related routes under /api/config and /api/providers.
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

  // /api/config/* routes
  await app.register(
    async (instance) => {
      registerAgentsRoutes(instance, svc.configService);
      registerSecurityRoutes(instance, svc.configService);
      registerPromptsRoutes(instance, svc.configService);
      registerProvidersRoutes(instance, svc.configService);
    },
    { prefix: '/api/config' },
  );

  // /api/providers/* routes
  await app.register(
    async (instance) => {
      registerHealthRoutes(instance, svc.healthService);
      registerDiagnosticsRoutes(instance, svc.diagnosticsService);
    },
    { prefix: '/api/providers' },
  );
}
