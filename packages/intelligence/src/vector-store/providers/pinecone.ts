/**
 * Pinecone Vector Store Adapter (Stub)
 *
 * Placeholder implementation for Pinecone vector database. The constructor
 * intentionally throws so misconfigured deployments fail fast at boot rather
 * than at first query.
 */

import type {
  VectorStoreConfig,
  CollectionOptions,
  CollectionStats,
  VectorDocument,
  MetadataFilter,
  SearchQuery,
  HybridSearchQuery,
  MMRSearchQuery,
  SearchResult,
} from '../interfaces.js';
import { BaseVectorStore } from '../base-vector-store.js';
import { ProviderNotImplementedError } from '../errors.js';

const PINECONE_STUB_MESSAGE =
  'Pinecone provider is a stub; only chromadb and pgvector are production-ready in this Tamma version. Configure VECTOR_STORE_PROVIDER=chromadb or pgvector as a fallback.';

/**
 * Pinecone Vector Store stub implementation.
 *
 * @deprecated This is a STUB and is not production-ready in this Tamma version.
 * Construction always throws {@link ProviderNotImplementedError}. Configure
 * `VECTOR_STORE_PROVIDER=chromadb` or `VECTOR_STORE_PROVIDER=pgvector` instead
 * — those are the only fully implemented backends today.
 *
 * To deliver this provider:
 * 1. Install the Pinecone client: `npm install @pinecone-database/pinecone`
 * 2. Replace the constructor body with real initialization
 * 3. Implement all abstract `do*` methods below
 *
 * @see https://www.pinecone.io/docs/
 */
export class PineconeVectorStore extends BaseVectorStore {
  constructor(config: VectorStoreConfig) {
    super('pinecone', config);
    throw new ProviderNotImplementedError('pinecone', undefined, {
      context: { message: PINECONE_STUB_MESSAGE },
    });
  }

  /**
   * Centralized stub failure. The do* overrides below all delegate here so the
   * file satisfies the abstract `BaseVectorStore` contract at compile time
   * without per-method boilerplate. These methods are unreachable at runtime
   * because the constructor always throws.
   */
  private _stub(): never {
    throw new ProviderNotImplementedError('pinecone', undefined, {
      context: { message: PINECONE_STUB_MESSAGE },
    });
  }

  protected override async doInitialize(): Promise<void> { this._stub(); }
  protected override async doDispose(): Promise<void> { this._stub(); }
  protected override async doHealthCheck(): Promise<Record<string, unknown>> { this._stub(); }
  protected override async doCreateCollection(_name: string, _options?: CollectionOptions): Promise<void> { this._stub(); }
  protected override async doDeleteCollection(_name: string): Promise<void> { this._stub(); }
  protected override async doListCollections(): Promise<string[]> { this._stub(); }
  protected override async doGetCollectionStats(_name: string): Promise<CollectionStats> { this._stub(); }
  protected override async doCollectionExists(_name: string): Promise<boolean> { this._stub(); }
  protected override async doUpsert(_collection: string, _documents: VectorDocument[]): Promise<void> { this._stub(); }
  protected override async doDelete(_collection: string, _ids: string[]): Promise<void> { this._stub(); }
  protected override async doGet(_collection: string, _ids: string[]): Promise<VectorDocument[]> { this._stub(); }
  protected override async doCount(_collection: string, _filter?: MetadataFilter): Promise<number> { this._stub(); }
  protected override async doSearch(_collection: string, _query: SearchQuery): Promise<SearchResult[]> { this._stub(); }
  protected override async doHybridSearch(_collection: string, _query: HybridSearchQuery): Promise<SearchResult[]> { this._stub(); }
  protected override async doMMRSearch(_collection: string, _query: MMRSearchQuery): Promise<SearchResult[]> { this._stub(); }
  protected override async doOptimize(_collection: string): Promise<void> { this._stub(); }
  protected override async doVacuum(_collection: string): Promise<void> { this._stub(); }
}
