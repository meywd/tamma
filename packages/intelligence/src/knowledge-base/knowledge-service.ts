/**
 * Knowledge Service
 *
 * Main service implementation for the agent knowledge base.
 */

import { randomUUID } from 'node:crypto';
import type {
  KnowledgeEntry,
  KnowledgeQuery,
  KnowledgeResult,
  KnowledgeFilter,
  KnowledgeCheckResult,
  LearningCapture,
  PendingLearning,
  PendingLearningFilter,
  CreateKnowledgeEntry,
  UpdateKnowledgeEntry,
  KnowledgeListResult,
  KnowledgeImportResult,
} from '@tamma/shared';
import type {
  IKnowledgeService,
  IKnowledgeStore,
  IEmbeddingProvider,
  KnowledgeConfig,
  TaskContext,
  DevelopmentPlan,
  MatchContext,
  MatchResult,
} from './types.js';
import { DEFAULT_KNOWLEDGE_CONFIG } from './types.js';
import { InMemoryKnowledgeStore } from './stores/in-memory-store.js';
import { KeywordMatcher } from './matchers/keyword-matcher.js';
import { PatternMatcher } from './matchers/pattern-matcher.js';
import { SemanticMatcher } from './matchers/semantic-matcher.js';
import { RelevanceRanker, combineMatchResults } from './matchers/relevance-ranker.js';
import { PreTaskChecker } from './checkers/pre-task-checker.js';
import { LearningCaptureService } from './capture/learning-capture.js';

/**
 * Knowledge service implementation
 */
export class KnowledgeService implements IKnowledgeService {
  private config: KnowledgeConfig;
  private store: IKnowledgeStore;
  private embeddingProvider: IEmbeddingProvider | null = null;
  private keywordMatcher: KeywordMatcher;
  private patternMatcher: PatternMatcher;
  private semanticMatcher: SemanticMatcher;
  private relevanceRanker: RelevanceRanker;
  private preTaskChecker: PreTaskChecker;
  private learningCapture: LearningCaptureService;

  constructor(store?: IKnowledgeStore, config?: Partial<KnowledgeConfig>) {
    this.config = { ...DEFAULT_KNOWLEDGE_CONFIG, ...config };
    this.store = store ?? new InMemoryKnowledgeStore();

    // Initialize matchers
    this.keywordMatcher = new KeywordMatcher({
      maxDistance: this.config.matching.maxKeywordDistance,
      scoreBoost: this.config.matching.keywordBoost,
    });
    this.patternMatcher = new PatternMatcher();
    this.semanticMatcher = new SemanticMatcher(undefined, {
      threshold: this.config.matching.semanticThreshold,
    });
    this.relevanceRanker = new RelevanceRanker();

    // Initialize checker and capture
    this.preTaskChecker = new PreTaskChecker(this, this.config.preTaskCheck);
    this.learningCapture = new LearningCaptureService(
      this.store,
      this.config.capture
    );
  }

  // === Lifecycle ===

  async initialize(config?: Partial<KnowledgeConfig>): Promise<void> {
    if (config) {
      this.config = { ...this.config, ...config };
      // Reinitialize components with new config
      this.keywordMatcher = new KeywordMatcher({
        maxDistance: this.config.matching.maxKeywordDistance,
        scoreBoost: this.config.matching.keywordBoost,
      });
      this.semanticMatcher = new SemanticMatcher(this.embeddingProvider ?? undefined, {
        threshold: this.config.matching.semanticThreshold,
      });
      this.preTaskChecker = new PreTaskChecker(this, this.config.preTaskCheck);
      this.learningCapture = new LearningCaptureService(
        this.store,
        this.config.capture
      );
    }
  }

  async dispose(): Promise<void> {
    // No resources to release; provided for interface completeness.
  }

  /**
   * Set the embedding provider for semantic matching
   */
  setEmbeddingProvider(provider: IEmbeddingProvider): void {
    this.embeddingProvider = provider;
    this.semanticMatcher.setEmbeddingProvider(provider);
  }

  // === Query ===

  async getRelevantKnowledge(query: KnowledgeQuery): Promise<KnowledgeResult> {
    // Build filter for applicable entries
    const baseFilter: KnowledgeFilter = {
      enabled: true,
      ...(query.minPriority !== undefined
        ? { priority: query.minPriority }
        : {}),
    };

    // Get all potentially relevant entries
    const { entries } = await this.store.list({
      ...baseFilter,
      limit: 1000, // Get enough for filtering
    });

    // Filter by scope and agent type
    const applicableEntries = entries.filter((entry) => {
      // Check scope
      if (entry.scope === 'project' && entry.projectId !== query.projectId) {
        return false;
      }
      if (
        entry.scope === 'agent_type' &&
        entry.agentTypes &&
        !entry.agentTypes.includes(query.agentType)
      ) {
        return false;
      }
      // Check validity period
      const now = new Date();
      if (entry.validFrom && entry.validFrom > now) {
        return false;
      }
      if (entry.validUntil && entry.validUntil < now) {
        return false;
      }
      return true;
    });

    // Build match context
    const matchContext: MatchContext = {
      taskDescription: query.taskDescription,
      filePaths: query.filePaths,
      technologies: query.technologies,
    };

    // Match entries
    const matchedEntries: Map<string, MatchResult> = new Map();

    for (const entry of applicableEntries) {
      const results: MatchResult[] = [];

      // Keyword matching
      const keywordMatch = await this.keywordMatcher.match(entry, matchContext);
      if (keywordMatch) {
        results.push(keywordMatch);
      }

      // Pattern matching (for entries with patterns)
      if (entry.patterns && entry.patterns.length > 0) {
        const patternMatch = await this.patternMatcher.match(entry, matchContext);
        if (patternMatch) {
          results.push(patternMatch);
        }
      }

      // Semantic matching (if enabled and available)
      if (
        this.config.matching.useSemantic &&
        entry.embedding &&
        this.embeddingProvider
      ) {
        const semanticMatch = await this.semanticMatcher.match(entry, matchContext);
        if (semanticMatch) {
          results.push(semanticMatch);
        }
      }

      // Combine results
      if (results.length > 0) {
        const combined = combineMatchResults(results);
        if (combined) {
          matchedEntries.set(entry.id, combined);
        }
      }
    }

    // Get matched entries
    const matchedList = applicableEntries.filter((e) => matchedEntries.has(e.id));

    // Rank entries
    const ranked = await this.relevanceRanker.rank(
      matchedList,
      query,
      matchedEntries
    );

    // Separate by type
    const recommendations = ranked
      .filter((r) => r.entry.type === 'recommendation')
      .slice(0, query.maxResults ?? 10);
    const prohibitions = ranked
      .filter((r) => r.entry.type === 'prohibition')
      .slice(0, query.maxResults ?? 10);
    const learnings = ranked
      .filter((r) => r.entry.type === 'learning')
      .slice(0, query.maxResults ?? 10);

    // Build summary
    const summary = this.buildSummary(recommendations, prohibitions, learnings);

    // Build critical warnings
    const criticalWarnings = prohibitions
      .filter((p) => p.entry.priority === 'critical')
      .map(
        (p) =>
          `CRITICAL: ${p.entry.title} - ${p.entry.description} (${p.matchResult.reason})`
      );

    return {
      recommendations: recommendations.map((r) => r.entry),
      prohibitions: prohibitions.map((p) => p.entry),
      learnings: learnings.map((l) => l.entry),
      summary,
      criticalWarnings,
    };
  }

  async checkBeforeTask(
    task: TaskContext,
    plan: DevelopmentPlan
  ): Promise<KnowledgeCheckResult> {
    return this.preTaskChecker.checkBeforeTask(task, plan);
  }

  // === CRUD ===

  async addKnowledge(entry: CreateKnowledgeEntry): Promise<KnowledgeEntry> {
    const now = new Date();
    const newEntry: KnowledgeEntry = {
      ...entry,
      id: randomUUID(),
      createdAt: now,
      updatedAt: now,
      timesApplied: 0,
      timesHelpful: 0,
    };

    // Generate embedding if provider available
    if (this.embeddingProvider && this.config.matching.useSemantic) {
      try {
        const text = `${newEntry.title} ${newEntry.description} ${newEntry.keywords.join(' ')}`;
        newEntry.embedding = await this.embeddingProvider.embed(text);
      } catch (error) {
        console.warn('[KnowledgeService] addKnowledge embedding failed:', error instanceof Error ? error.message : String(error));
      }
    }

    return this.store.create(newEntry);
  }

  async updateKnowledge(
    id: string,
    updates: UpdateKnowledgeEntry
  ): Promise<KnowledgeEntry> {
    const existing = await this.store.get(id);
    if (!existing) {
      throw new Error(`Knowledge entry not found: ${id}`);
    }

    // Regenerate embedding if content changed
    if (
      this.embeddingProvider &&
      this.config.matching.useSemantic &&
      (updates.title || updates.description || updates.keywords)
    ) {
      try {
        const title = updates.title ?? existing.title;
        const description = updates.description ?? existing.description;
        const keywords = updates.keywords ?? existing.keywords;
        const text = `${title} ${description} ${keywords.join(' ')}`;
        updates.embedding = await this.embeddingProvider.embed(text);
      } catch (error) {
        console.warn('[KnowledgeService] updateKnowledge embedding failed:', error instanceof Error ? error.message : String(error));
      }
    }

    return this.store.update(id, {
      ...updates,
      updatedAt: new Date(),
    });
  }

  async deleteKnowledge(id: string): Promise<void> {
    return this.store.delete(id);
  }

  async getKnowledge(id: string): Promise<KnowledgeEntry | null> {
    return this.store.get(id);
  }

  async listKnowledge(filter?: KnowledgeFilter): Promise<KnowledgeListResult> {
    return this.store.list(filter);
  }

  // === Learning Capture ===

  async captureLearning(capture: LearningCapture): Promise<PendingLearning> {
    return this.learningCapture.captureExplicit(capture, 'system');
  }

  async getPendingLearnings(
    filter?: PendingLearningFilter
  ): Promise<PendingLearning[]> {
    return this.store.listPending({
      ...filter,
      status: filter?.status ?? 'pending',
    });
  }

  async approveLearning(
    id: string,
    edits?: Partial<KnowledgeEntry>
  ): Promise<KnowledgeEntry> {
    const pending = await this.store.getPending(id);
    if (!pending) {
      throw new Error(`Pending learning not found: ${id}`);
    }

    if (pending.status !== 'pending') {
      throw new Error(`Pending learning already ${pending.status}`);
    }

    // Create knowledge entry from pending learning
    const now = new Date();
    const details = pending.whatWorked || pending.whatFailed || pending.rootCause;
    const entry: KnowledgeEntry = {
      id: randomUUID(),
      type: 'learning',
      title: edits?.title ?? pending.suggestedTitle,
      description: edits?.description ?? pending.suggestedDescription,
      ...(details !== undefined ? { details } : {}),
      scope: 'global',
      projectId: pending.projectId,
      keywords: edits?.keywords ?? pending.suggestedKeywords,
      priority: edits?.priority ?? pending.suggestedPriority,
      source:
        pending.outcome === 'success'
          ? 'task_success'
          : pending.outcome === 'failure'
            ? 'task_failure'
            : 'task_success',
      sourceRef: pending.taskId,
      createdAt: now,
      updatedAt: now,
      createdBy: pending.capturedBy,
      enabled: true,
      timesApplied: 0,
      timesHelpful: 0,
      ...edits,
    };

    // Generate embedding
    if (this.embeddingProvider && this.config.matching.useSemantic) {
      try {
        const text = `${entry.title} ${entry.description} ${entry.keywords.join(' ')}`;
        entry.embedding = await this.embeddingProvider.embed(text);
      } catch (error) {
        console.warn('[KnowledgeService] approveLearning embedding failed:', error instanceof Error ? error.message : String(error));
      }
    }

    // Create entry
    const created = await this.store.create(entry);

    // Update pending status
    await this.store.updatePending(id, {
      status: 'approved',
      reviewedAt: now,
      reviewedBy: 'system',
    });

    return created;
  }

  async rejectLearning(id: string, reason: string): Promise<void> {
    const pending = await this.store.getPending(id);
    if (!pending) {
      throw new Error(`Pending learning not found: ${id}`);
    }

    if (pending.status !== 'pending') {
      throw new Error(`Pending learning already ${pending.status}`);
    }

    await this.store.updatePending(id, {
      status: 'rejected',
      reviewedAt: new Date(),
      reviewedBy: 'system',
      rejectionReason: reason,
    });
  }

  // === Feedback ===

  async recordApplication(
    knowledgeId: string,
    _taskId: string,
    helpful: boolean
  ): Promise<void> {
    await this.store.incrementApplied(knowledgeId);
    await this.store.recordHelpfulness(knowledgeId, helpful);
  }

  // === Import/Export ===

  async importKnowledge(
    entries: CreateKnowledgeEntry[]
  ): Promise<KnowledgeImportResult> {
    const result: KnowledgeImportResult = {
      imported: 0,
      skipped: 0,
      errors: [],
    };

    for (const entry of entries) {
      try {
        // Check for duplicates by title
        const existing = await this.store.search({
          search: entry.title,
          filter: { types: [entry.type] },
        });

        const duplicate = existing.find(
          (e) => e.title.toLowerCase() === entry.title.toLowerCase()
        );

        if (duplicate) {
          result.skipped++;
          continue;
        }

        await this.addKnowledge(entry);
        result.imported++;
      } catch (error) {
        result.errors.push({
          entry,
          error: error instanceof Error ? error.message : 'Unknown error',
        });
      }
    }

    return result;
  }

  async exportKnowledge(filter?: KnowledgeFilter): Promise<KnowledgeEntry[]> {
    const { entries } = await this.store.list({
      ...filter,
      limit: 10000,
    });
    return entries;
  }

  // === Maintenance ===

  async refreshEmbeddings(): Promise<void> {
    if (!this.embeddingProvider) {
      return;
    }

    const { entries } = await this.store.list({ limit: 10000 });

    for (const entry of entries) {
      if (!entry.embedding || entry.embedding.length === 0) {
        try {
          const text = `${entry.title} ${entry.description} ${entry.keywords.join(' ')}`;
          const embedding = await this.embeddingProvider.embed(text);
          await this.store.update(entry.id, { embedding });
        } catch (error) {
          console.warn(`[KnowledgeService] refreshEmbeddings failed for entry ${entry.id}:`, error instanceof Error ? error.message : String(error));
        }
      }
    }
  }

  async pruneExpired(): Promise<number> {
    const { entries } = await this.store.list({ limit: 10000 });
    const now = new Date();
    let pruned = 0;

    for (const entry of entries) {
      let shouldPrune = false;

      // Check validity period
      if (entry.validUntil && entry.validUntil < now) {
        shouldPrune = true;
      }

      // Check age
      if (this.config.retention.maxAgeDays > 0) {
        const ageMs = now.getTime() - entry.createdAt.getTime();
        const ageDays = ageMs / (1000 * 60 * 60 * 24);
        if (ageDays > this.config.retention.maxAgeDays) {
          // Only prune if not heavily used
          if (
            entry.timesApplied < this.config.retention.minApplicationsToKeep
          ) {
            shouldPrune = true;
          }
        }
      }

      // Prune low priority unused entries
      if (
        this.config.retention.pruneLowPriority &&
        entry.priority === 'low' &&
        entry.timesApplied === 0
      ) {
        const ageMs = now.getTime() - entry.createdAt.getTime();
        const ageDays = ageMs / (1000 * 60 * 60 * 24);
        if (ageDays > 30) {
          shouldPrune = true;
        }
      }

      if (shouldPrune) {
        await this.store.delete(entry.id);
        pruned++;
      }
    }

    return pruned;
  }

  // === Private Methods ===

  private buildSummary(
    recommendations: Array<{ entry: KnowledgeEntry }>,
    prohibitions: Array<{ entry: KnowledgeEntry }>,
    learnings: Array<{ entry: KnowledgeEntry }>
  ): string {
    const parts: string[] = [];

    if (prohibitions.length > 0) {
      const critical = prohibitions.filter(
        (p) => p.entry.priority === 'critical'
      ).length;
      if (critical > 0) {
        parts.push(`${critical} critical prohibition(s) to review`);
      }
      const warnings = prohibitions.length - critical;
      if (warnings > 0) {
        parts.push(`${warnings} warning(s)`);
      }
    }

    if (recommendations.length > 0) {
      parts.push(`${recommendations.length} recommendation(s) available`);
    }

    if (learnings.length > 0) {
      parts.push(`${learnings.length} relevant learning(s) from past tasks`);
    }

    if (parts.length === 0) {
      return 'No specific knowledge applicable to this task.';
    }

    return parts.join(', ') + '.';
  }
}
