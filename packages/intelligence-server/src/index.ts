/**
 * @tamma/intelligence-server
 *
 * Entry point for the intelligence sidecar. Re-exports the server factory
 * and service classes so in-process callers (tests, embedded harnesses) can
 * compose them without hitting HTTP.
 */

export { buildServer, registerKbRoutes, startServer } from './server.js';
export { adaptVectorStore, adaptRagPipeline } from './adapters.js';
export {
  buildIntelligenceBundleFromEnv,
  createVectorStoreFromEnv,
  createRagPipelineFromEnv,
  createEmbedderFromEnv,
  buildRagPipeline,
  wrapVectorStore,
  resolveVectorStoreConfig,
  hasVectorStoreEnv,
  EMBEDDER_MISSING_MESSAGE,
} from './env-composition.js';
export { IndexManagementService } from './services/IndexManagementService.js';
export { VectorDbManagementService } from './services/VectorDbManagementService.js';
export { RagManagementService } from './services/RagManagementService.js';
export { McpManagementService } from './services/McpManagementService.js';
export { ContextTestingService } from './services/ContextTestingService.js';
export { AnalyticsService } from './services/AnalyticsService.js';
export type {
  IntelligenceServicesBundle,
  IIndexer,
  IVectorStoreAdapter,
  IRagPipeline,
  IMcpClient,
  IContextAggregatorAdapter,
  ICostTrackerAdapter,
} from './types.js';
