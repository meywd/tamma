/**
 * Knowledge Base Routes Registration
 *
 * Registers all knowledge base management API routes with Fastify.
 * Accepts optional real dependencies for wiring to live implementations;
 * services degrade gracefully when dependencies are not provided.
 */

import type { FastifyInstance } from 'fastify';
import type { CodebaseIndexer } from '@tamma/intelligence/indexer';
import type { IVectorStore } from '@tamma/intelligence/vector-store';
import type { RAGPipeline } from '@tamma/intelligence/rag';
import type { ContextAggregator } from '@tamma/intelligence/context';
import type { MCPClient } from '@tamma/mcp-client';
import type { CostTracker } from '@tamma/cost-monitor';
import { IndexManagementService } from '../../services/knowledge-base/IndexManagementService.js';
import { VectorDBManagementService } from '../../services/knowledge-base/VectorDBManagementService.js';
import { RAGManagementService } from '../../services/knowledge-base/RAGManagementService.js';
import { MCPManagementService } from '../../services/knowledge-base/MCPManagementService.js';
import { ContextTestingService } from '../../services/knowledge-base/ContextTestingService.js';
import { AnalyticsService } from '../../services/knowledge-base/AnalyticsService.js';
import { registerIndexRoutes } from './index-routes.js';
import { registerVectorDBRoutes } from './vector-db-routes.js';
import { registerRAGRoutes } from './rag-routes.js';
import { registerMCPRoutes } from './mcp-routes.js';
import { registerContextRoutes } from './context-routes.js';
import { registerAnalyticsRoutes } from './analytics-routes.js';

/** Services container for dependency injection */
export interface KBServices {
  indexService: IndexManagementService;
  vectorDBService: VectorDBManagementService;
  ragService: RAGManagementService;
  mcpService: MCPManagementService;
  contextService: ContextTestingService;
  analyticsService: AnalyticsService;
}

/** Optional real dependencies that can be injected into service creation */
export interface KBDependencies {
  indexer?: CodebaseIndexer;
  vectorStore?: IVectorStore;
  ragPipeline?: RAGPipeline;
  contextAggregator?: ContextAggregator;
  mcpClient?: MCPClient;
  costTracker?: CostTracker;
  /** Project path used by the indexer for indexing operations */
  projectPath?: string;
}

/**
 * Create service instances, optionally wired to real implementations.
 * When a dependency is not provided the service returns empty/zero state.
 */
export function createKBServices(deps: KBDependencies = {}): KBServices {
  return {
    indexService: new IndexManagementService(deps.indexer, deps.projectPath),
    vectorDBService: new VectorDBManagementService(deps.vectorStore),
    ragService: new RAGManagementService(deps.ragPipeline),
    mcpService: new MCPManagementService(deps.mcpClient),
    contextService: new ContextTestingService(deps.contextAggregator),
    analyticsService: new AnalyticsService(deps.costTracker),
  };
}

/**
 * Register all knowledge base routes under /api/knowledge-base
 */
export async function registerKnowledgeBaseRoutes(
  app: FastifyInstance,
  services?: KBServices,
): Promise<void> {
  const svc = services ?? createKBServices();

  await app.register(
    async (instance) => {
      registerIndexRoutes(instance, svc.indexService);
      registerVectorDBRoutes(instance, svc.vectorDBService);
      registerRAGRoutes(instance, svc.ragService);
      registerMCPRoutes(instance, svc.mcpService);
      registerContextRoutes(instance, svc.contextService);
      registerAnalyticsRoutes(instance, svc.analyticsService);
    },
    { prefix: '/api/knowledge-base' },
  );
}
