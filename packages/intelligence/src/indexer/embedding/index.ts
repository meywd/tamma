/**
 * Embedding Module
 *
 * Provides embedding generation capabilities for the codebase indexer.
 *
 * @module @tamma/intelligence/indexer/embedding
 */

export { BaseEmbeddingProvider } from './base-embedding-provider.js';
export { OpenAIEmbeddingProvider } from './openai-embedding-provider.js';
export { CohereEmbeddingProvider } from './cohere-embedding-provider.js';
export { OllamaEmbeddingProvider } from './ollama-embedding-provider.js';
export { MockEmbeddingProvider } from './mock-embedding-provider.js';
export {
  EmbeddingService,
  type EmbeddingServiceConfig,
} from './embedding-service.js';

// Re-export the embedding-related types so consumers (e.g. the intelligence
// sidecar) can depend on this module alone via the `@tamma/intelligence/embedding`
// subpath, WITHOUT importing the full `./indexer` barrel — which transitively
// value-imports `typescript` (a devDependency) through the TypeScript chunker
// and would crash a prod-pruned runtime image at ESM link time.
export type {
  EmbeddingProviderType,
  EmbeddingProviderConfig,
  IEmbeddingProvider,
} from '../types.js';
