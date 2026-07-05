/**
 * In-Memory Knowledge Store
 *
 * Simple in-memory implementation of the knowledge store for development and testing.
 */

import type {
  KnowledgeEntry,
  KnowledgeFilter,
  KnowledgeListResult,
  PendingLearning,
  PendingLearningFilter,
  KnowledgePriority,
} from '@tamma/shared';
import type {
  IKnowledgeStore,
  KnowledgeStoreQuery,
  EmbeddingSearchOptions,
} from '../types.js';

/**
 * Priority to numeric value mapping for comparison
 */
const PRIORITY_VALUES: Record<KnowledgePriority, number> = {
  low: 1,
  medium: 2,
  high: 3,
  critical: 4,
};

/**
 * Calculate cosine similarity between two vectors
 */
function cosineSimilarity(a: number[], b: number[]): number {
  if (a.length !== b.length) {
    return 0;
  }

  let dotProduct = 0;
  let normA = 0;
  let normB = 0;

  for (let i = 0; i < a.length; i++) {
    dotProduct += a[i]! * b[i]!;
    normA += a[i]! * a[i]!;
    normB += b[i]! * b[i]!;
  }

  if (normA === 0 || normB === 0) {
    return 0;
  }

  return dotProduct / (Math.sqrt(normA) * Math.sqrt(normB));
}

/**
 * In-memory implementation of the knowledge store
 */
export class InMemoryKnowledgeStore implements IKnowledgeStore {
  private entries: Map<string, KnowledgeEntry> = new Map();
  private pendingLearnings: Map<string, PendingLearning> = new Map();

  // === CRUD ===

  async create(entry: KnowledgeEntry): Promise<KnowledgeEntry> {
    const stored = { ...entry };
    this.entries.set(entry.id, stored);
    return stored;
  }

  async update(
    id: string,
    updates: Partial<KnowledgeEntry>
  ): Promise<KnowledgeEntry> {
    const existing = this.entries.get(id);
    if (!existing) {
      throw new Error(`Knowledge entry not found: ${id}`);
    }

    const now = new Date();
    // Ensure updatedAt is strictly after the previous value
    if (now.getTime() <= existing.updatedAt.getTime()) {
      now.setTime(existing.updatedAt.getTime() + 1);
    }
    const updated: KnowledgeEntry = {
      ...existing,
      ...updates,
      id, // Prevent ID change
      updatedAt: now,
    };

    this.entries.set(id, updated);
    return updated;
  }

  async delete(id: string): Promise<void> {
    if (!this.entries.has(id)) {
      throw new Error(`Knowledge entry not found: ${id}`);
    }
    this.entries.delete(id);
  }

  async get(id: string): Promise<KnowledgeEntry | null> {
    return this.entries.get(id) ?? null;
  }

  async list(filter?: KnowledgeFilter): Promise<KnowledgeListResult> {
    let entries = Array.from(this.entries.values());

    // Apply filters
    entries = this.applyFilters(entries, filter);

    // Get total before pagination
    const total = entries.length;

    // Apply pagination
    const offset = filter?.offset ?? 0;
    const limit = filter?.limit ?? 100;
    entries = entries.slice(offset, offset + limit);

    return {
      entries,
      total,
      hasMore: offset + entries.length < total,
    };
  }

  // === Search ===

  async search(query: KnowledgeStoreQuery): Promise<KnowledgeEntry[]> {
    let entries = Array.from(this.entries.values());

    // Apply filter
    if (query.filter) {
      entries = this.applyFilters(entries, query.filter);
    }

    // Apply text search
    if (query.search) {
      const searchLower = query.search.toLowerCase();
      entries = entries.filter(
        (e) =>
          e.title.toLowerCase().includes(searchLower) ||
          e.description.toLowerCase().includes(searchLower) ||
          e.keywords.some((k) => k.toLowerCase().includes(searchLower))
      );
    }

    // Apply sorting
    if (query.sortBy) {
      entries = this.sortEntries(entries, query.sortBy, query.sortOrder ?? 'desc');
    }

    return entries;
  }

  async searchByEmbedding(
    embedding: number[],
    options: EmbeddingSearchOptions
  ): Promise<Array<{ entry: KnowledgeEntry; score: number }>> {
    let entries = Array.from(this.entries.values());

    // Apply filter if provided
    if (options.filter) {
      entries = this.applyFilters(entries, options.filter);
    }

    // Filter to entries with embeddings
    entries = entries.filter((e) => e.embedding && e.embedding.length > 0);

    // Calculate similarity scores
    const scored = entries.map((entry) => ({
      entry,
      score: cosineSimilarity(embedding, entry.embedding!),
    }));

    // Filter by threshold
    const threshold = options.threshold ?? 0;
    const filtered = scored.filter((s) => s.score >= threshold);

    // Sort by score descending
    filtered.sort((a, b) => b.score - a.score);

    // Return top K
    return filtered.slice(0, options.topK);
  }

  // === Pending Learnings ===

  async createPending(learning: PendingLearning): Promise<PendingLearning> {
    const stored = { ...learning };
    this.pendingLearnings.set(learning.id, stored);
    return stored;
  }

  async updatePending(
    id: string,
    updates: Partial<PendingLearning>
  ): Promise<PendingLearning> {
    const existing = this.pendingLearnings.get(id);
    if (!existing) {
      throw new Error(`Pending learning not found: ${id}`);
    }

    const updated: PendingLearning = {
      ...existing,
      ...updates,
      id, // Prevent ID change
    };

    this.pendingLearnings.set(id, updated);
    return updated;
  }

  async getPending(id: string): Promise<PendingLearning | null> {
    return this.pendingLearnings.get(id) ?? null;
  }

  async listPending(filter?: PendingLearningFilter): Promise<PendingLearning[]> {
    let learnings = Array.from(this.pendingLearnings.values());

    // Apply filters
    if (filter?.status) {
      learnings = learnings.filter((l) => l.status === filter.status);
    }
    if (filter?.projectId) {
      learnings = learnings.filter((l) => l.projectId === filter.projectId);
    }
    if (filter?.outcome) {
      learnings = learnings.filter((l) => l.outcome === filter.outcome);
    }

    // Sort by captured date (newest first)
    learnings.sort(
      (a, b) => b.capturedAt.getTime() - a.capturedAt.getTime()
    );

    // Apply pagination
    const offset = filter?.offset ?? 0;
    const limit = filter?.limit ?? 100;
    return learnings.slice(offset, offset + limit);
  }

  async deletePending(id: string): Promise<void> {
    if (!this.pendingLearnings.has(id)) {
      throw new Error(`Pending learning not found: ${id}`);
    }
    this.pendingLearnings.delete(id);
  }

  // === Stats ===

  async incrementApplied(id: string): Promise<void> {
    const entry = this.entries.get(id);
    if (!entry) {
      throw new Error(`Knowledge entry not found: ${id}`);
    }

    entry.timesApplied += 1;
    entry.lastApplied = new Date();
  }

  async recordHelpfulness(id: string, helpful: boolean): Promise<void> {
    const entry = this.entries.get(id);
    if (!entry) {
      throw new Error(`Knowledge entry not found: ${id}`);
    }

    if (helpful) {
      entry.timesHelpful += 1;
    }
  }

  // === Helper Methods ===

  /**
   * Apply filters to entries
   */
  private applyFilters(
    entries: KnowledgeEntry[],
    filter?: KnowledgeFilter
  ): KnowledgeEntry[] {
    if (!filter) {
      return entries;
    }

    return entries.filter((entry) => {
      // Filter by types
      if (filter.types && filter.types.length > 0) {
        if (!filter.types.includes(entry.type)) {
          return false;
        }
      }

      // Filter by scopes
      if (filter.scopes && filter.scopes.length > 0) {
        if (!filter.scopes.includes(entry.scope)) {
          return false;
        }
      }

      // Filter by project
      if (filter.projectId !== undefined) {
        if (entry.scope === 'project' && entry.projectId !== filter.projectId) {
          return false;
        }
      }

      // Filter by agent types
      if (filter.agentTypes && filter.agentTypes.length > 0) {
        if (
          entry.agentTypes &&
          !entry.agentTypes.some((at) => filter.agentTypes!.includes(at))
        ) {
          return false;
        }
      }

      // Filter by source
      if (filter.source !== undefined) {
        if (entry.source !== filter.source) {
          return false;
        }
      }

      // Filter by enabled
      if (filter.enabled !== undefined) {
        if (entry.enabled !== filter.enabled) {
          return false;
        }
      }

      // Filter by priority
      if (filter.priority !== undefined) {
        if (
          PRIORITY_VALUES[entry.priority] < PRIORITY_VALUES[filter.priority]
        ) {
          return false;
        }
      }

      // Filter by search text
      if (filter.search) {
        const searchLower = filter.search.toLowerCase();
        const matches =
          entry.title.toLowerCase().includes(searchLower) ||
          entry.description.toLowerCase().includes(searchLower) ||
          entry.keywords.some((k) => k.toLowerCase().includes(searchLower));
        if (!matches) {
          return false;
        }
      }

      // Filter by validity period
      const now = new Date();
      if (entry.validFrom && entry.validFrom > now) {
        return false;
      }
      if (entry.validUntil && entry.validUntil < now) {
        return false;
      }

      return true;
    });
  }

  /**
   * Sort entries by field
   */
  private sortEntries(
    entries: KnowledgeEntry[],
    sortBy: keyof KnowledgeEntry,
    sortOrder: 'asc' | 'desc'
  ): KnowledgeEntry[] {
    return entries.sort((a, b) => {
      const aVal = a[sortBy];
      const bVal = b[sortBy];

      let comparison = 0;

      if (aVal === undefined && bVal === undefined) {
        comparison = 0;
      } else if (aVal === undefined) {
        comparison = 1;
      } else if (bVal === undefined) {
        comparison = -1;
      } else if (aVal instanceof Date && bVal instanceof Date) {
        comparison = aVal.getTime() - bVal.getTime();
      } else if (typeof aVal === 'string' && typeof bVal === 'string') {
        comparison = aVal.localeCompare(bVal);
      } else if (typeof aVal === 'number' && typeof bVal === 'number') {
        comparison = aVal - bVal;
      } else if (typeof aVal === 'boolean' && typeof bVal === 'boolean') {
        comparison = aVal === bVal ? 0 : aVal ? 1 : -1;
      }

      return sortOrder === 'asc' ? comparison : -comparison;
    });
  }

  // === Testing Helpers ===

  /**
   * Clear all data (for testing)
   */
  clear(): void {
    this.entries.clear();
    this.pendingLearnings.clear();
  }

  /**
   * Get entry count (for testing)
   */
  getEntryCount(): number {
    return this.entries.size;
  }

  /**
   * Get pending count (for testing)
   */
  getPendingCount(): number {
    return this.pendingLearnings.size;
  }
}
