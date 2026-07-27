/**
 * Vector DB Hook
 *
 * React hook for vector database management operations.
 */

import { useState, useCallback } from 'react';
import type {
  CollectionInfo,
  CollectionStatsInfo,
  VectorSearchResult,
  StorageUsage,
} from '@tamma/shared';
import { vectorDBApi } from '../../services/knowledge-base/api-client.js';

export interface UseVectorDBReturn {
  collections: CollectionInfo[];
  selectedStats: CollectionStatsInfo | null;
  searchResults: VectorSearchResult[];
  storageUsage: StorageUsage | null;
  loading: boolean;
  error: string | null;
  loadCollections: () => Promise<void>;
  loadCollectionStats: (name: string) => Promise<void>;
  createCollection: (name: string, dimensions?: number) => Promise<void>;
  deleteCollection: (name: string) => Promise<void>;
  search: (collection: string, query: string, topK?: number) => Promise<void>;
  loadStorageUsage: () => Promise<void>;
}

export function useVectorDB(): UseVectorDBReturn {
  const [collections, setCollections] = useState<CollectionInfo[]>([]);
  const [selectedStats, setSelectedStats] = useState<CollectionStatsInfo | null>(null);
  const [searchResults, setSearchResults] = useState<VectorSearchResult[]>([]);
  const [storageUsage, setStorageUsage] = useState<StorageUsage | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const loadCollections = useCallback(async () => {
    setLoading(true);
    try {
      const data = await vectorDBApi.listCollections();
      setCollections(data);
      setError(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load collections');
    } finally {
      setLoading(false);
    }
  }, []);

  const loadCollectionStats = useCallback(async (name: string) => {
    try {
      const data = await vectorDBApi.getCollectionStats(name);
      setSelectedStats(data);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load stats');
    }
  }, []);

  const createCollection = useCallback(async (name: string, dimensions?: number) => {
    try {
      await vectorDBApi.createCollection({
        name,
        ...(dimensions !== undefined ? { dimensions } : {}),
      });
      await loadCollections();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to create collection');
    }
  }, [loadCollections]);

  const deleteCollection = useCallback(async (name: string) => {
    try {
      await vectorDBApi.deleteCollection(name);
      await loadCollections();
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to delete collection');
    }
  }, [loadCollections]);

  const search = useCallback(async (collection: string, query: string, topK = 10) => {
    setLoading(true);
    try {
      const results = await vectorDBApi.search({ collection, query, topK });
      setSearchResults(results);
      setError(null);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Search failed');
    } finally {
      setLoading(false);
    }
  }, []);

  const loadStorageUsage = useCallback(async () => {
    try {
      const data = await vectorDBApi.getStorageUsage();
      setStorageUsage(data);
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Failed to load storage usage');
    }
  }, []);

  return {
    collections,
    selectedStats,
    searchResults,
    storageUsage,
    loading,
    error,
    loadCollections,
    loadCollectionStats,
    createCollection,
    deleteCollection,
    search,
    loadStorageUsage,
  };
}
