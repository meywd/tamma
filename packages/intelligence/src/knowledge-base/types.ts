/**
 * Knowledge Base Service Types
 *
 * Internal interfaces and types for the knowledge base implementation.
 */

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

// === Configuration Types ===

/**
 * Storage backend configuration
 */
export interface KnowledgeStorageConfig {
  /** Storage type */
  type: 'memory' | 'database' | 'file';
  /** Connection string (for database) */
  connectionString?: string;
  /** File path (for file storage) */
  filePath?: string;
}

/**
 * Learning capture configuration
 */
export interface LearningCaptureConfig {
  /** Auto-capture learnings from successful tasks */
  autoCaptureSuccess: boolean;
  /** Auto-capture learnings from failed tasks */
  autoCaptureFailure: boolean;
  /** Require human approval for auto-captured learnings */
  requireApproval: boolean;
  /** Maximum days to keep pending learnings */
  maxPendingDays: number;
}

/**
 * Matching algorithm configuration
 */
export interface MatchingConfig {
  /** Enable semantic (embedding-based) matching */
  useSemantic: boolean;
  /** Minimum similarity threshold for semantic matches (0-1) */
  semanticThreshold: number;
  /** Boost multiplier for keyword matches */
  keywordBoost: number;
  /** Maximum Levenshtein distance for fuzzy keyword matching */
  maxKeywordDistance: number;
}

/**
 * Pre-task check configuration
 */
export interface PreTaskCheckConfig {
  /** Enable pre-task checking */
  enabled: boolean;
  /** Block task execution on critical prohibition matches */
  blockOnCritical: boolean;
  /** Maximum recommendations to include */
  maxRecommendations: number;
  /** Maximum learnings to include */
  maxLearnings: number;
  /** Maximum warnings to include */
  maxWarnings: number;
}

/**
 * Knowledge retention configuration
 */
export interface RetentionConfig {
  /** Maximum age in days before pruning */
  maxAgeDays: number;
  /** Prune low-priority unused entries */
  pruneLowPriority: boolean;
  /** Minimum applications to keep an entry */
  minApplicationsToKeep: number;
  /** Auto-archive unused entries */
  autoArchiveUnused: boolean;
}

/**
 * Main knowledge base configuration
 */
export interface KnowledgeConfig {
  /** Storage configuration */
  storage: KnowledgeStorageConfig;
  /** Learning capture configuration */
  capture: LearningCaptureConfig;
  /** Matching configuration */
  matching: MatchingConfig;
  /** Pre-task check configuration */
  preTaskCheck: PreTaskCheckConfig;
  /** Retention configuration */
  retention: RetentionConfig;
}

/**
 * Default knowledge configuration
 */
export const DEFAULT_KNOWLEDGE_CONFIG: KnowledgeConfig = {
  storage: {
    type: 'memory',
  },
  capture: {
    autoCaptureSuccess: true,
    autoCaptureFailure: true,
    requireApproval: true,
    maxPendingDays: 30,
  },
  matching: {
    useSemantic: true,
    semanticThreshold: 0.7,
    keywordBoost: 1.5,
    maxKeywordDistance: 2,
  },
  preTaskCheck: {
    enabled: true,
    blockOnCritical: true,
    maxRecommendations: 5,
    maxLearnings: 3,
    maxWarnings: 10,
  },
  retention: {
    maxAgeDays: 365,
    pruneLowPriority: true,
    minApplicationsToKeep: 3,
    autoArchiveUnused: false,
  },
};

// === Context Types ===

/**
 * Task context for pre-task checking
 */
export interface TaskContext {
  /** Task identifier */
  taskId: string;
  /** Task type */
  type: string;
  /** Task description */
  description: string;
  /** Project ID */
  projectId: string;
  /** Agent type performing the task */
  agentType: string;
}

/**
 * Development plan for checking
 */
export interface DevelopmentPlan {
  /** Summary of the plan */
  summary: string;
  /** Approach description */
  approach: string;
  /** Planned file changes */
  fileChanges: Array<{
    path: string;
    action: 'create' | 'modify' | 'delete';
    description: string;
  }>;
  /** Technologies being used */
  technologies?: string[];
}

// === Service Interfaces ===

/**
 * Main knowledge service interface
 */
export interface IKnowledgeService {
  // === Lifecycle ===

  /**
   * Initialize the knowledge service
   */
  initialize(config?: Partial<KnowledgeConfig>): Promise<void>;

  /**
   * Dispose of resources
   */
  dispose(): Promise<void>;

  // === Query ===

  /**
   * Get relevant knowledge for a task
   */
  getRelevantKnowledge(query: KnowledgeQuery): Promise<KnowledgeResult>;

  /**
   * Check knowledge base before task execution
   */
  checkBeforeTask(
    task: TaskContext,
    plan: DevelopmentPlan
  ): Promise<KnowledgeCheckResult>;

  // === CRUD ===

  /**
   * Add a new knowledge entry
   */
  addKnowledge(entry: CreateKnowledgeEntry): Promise<KnowledgeEntry>;

  /**
   * Update an existing knowledge entry
   */
  updateKnowledge(id: string, updates: UpdateKnowledgeEntry): Promise<KnowledgeEntry>;

  /**
   * Delete a knowledge entry
   */
  deleteKnowledge(id: string): Promise<void>;

  /**
   * Get a knowledge entry by ID
   */
  getKnowledge(id: string): Promise<KnowledgeEntry | null>;

  /**
   * List knowledge entries with optional filtering
   */
  listKnowledge(filter?: KnowledgeFilter): Promise<KnowledgeListResult>;

  // === Learning Capture ===

  /**
   * Capture a learning from task outcome
   */
  captureLearning(capture: LearningCapture): Promise<PendingLearning>;

  /**
   * Get pending learnings awaiting approval
   */
  getPendingLearnings(filter?: PendingLearningFilter): Promise<PendingLearning[]>;

  /**
   * Approve a pending learning and add it to the knowledge base
   */
  approveLearning(
    id: string,
    edits?: Partial<KnowledgeEntry>
  ): Promise<KnowledgeEntry>;

  /**
   * Reject a pending learning
   */
  rejectLearning(id: string, reason: string): Promise<void>;

  // === Feedback ===

  /**
   * Record that knowledge was applied to a task
   */
  recordApplication(
    knowledgeId: string,
    taskId: string,
    helpful: boolean
  ): Promise<void>;

  // === Import/Export ===

  /**
   * Import knowledge entries
   */
  importKnowledge(entries: CreateKnowledgeEntry[]): Promise<KnowledgeImportResult>;

  /**
   * Export knowledge entries
   */
  exportKnowledge(filter?: KnowledgeFilter): Promise<KnowledgeEntry[]>;

  // === Maintenance ===

  /**
   * Refresh embeddings for entries that need them
   */
  refreshEmbeddings(): Promise<void>;

  /**
   * Prune expired or unused entries
   */
  pruneExpired(): Promise<number>;
}

// === Store Interface ===

/**
 * Store query options
 */
export interface KnowledgeStoreQuery {
  /** Text search */
  search?: string;
  /** Filter criteria */
  filter?: KnowledgeFilter;
  /** Sort field */
  sortBy?: keyof KnowledgeEntry;
  /** Sort direction */
  sortOrder?: 'asc' | 'desc';
}

/**
 * Embedding search options
 */
export interface EmbeddingSearchOptions {
  /** Number of results */
  topK: number;
  /** Minimum similarity threshold */
  threshold?: number;
  /** Additional filter */
  filter?: KnowledgeFilter;
}

/**
 * Knowledge store interface (storage abstraction)
 */
export interface IKnowledgeStore {
  // === CRUD ===

  /**
   * Create a new knowledge entry
   */
  create(entry: KnowledgeEntry): Promise<KnowledgeEntry>;

  /**
   * Update an existing entry
   */
  update(id: string, entry: Partial<KnowledgeEntry>): Promise<KnowledgeEntry>;

  /**
   * Delete an entry
   */
  delete(id: string): Promise<void>;

  /**
   * Get an entry by ID
   */
  get(id: string): Promise<KnowledgeEntry | null>;

  /**
   * List entries with filtering
   */
  list(filter?: KnowledgeFilter): Promise<KnowledgeListResult>;

  // === Search ===

  /**
   * Search entries by text
   */
  search(query: KnowledgeStoreQuery): Promise<KnowledgeEntry[]>;

  /**
   * Search entries by embedding similarity
   */
  searchByEmbedding(
    embedding: number[],
    options: EmbeddingSearchOptions
  ): Promise<Array<{ entry: KnowledgeEntry; score: number }>>;

  // === Pending Learnings ===

  /**
   * Create a pending learning
   */
  createPending(learning: PendingLearning): Promise<PendingLearning>;

  /**
   * Update a pending learning
   */
  updatePending(
    id: string,
    updates: Partial<PendingLearning>
  ): Promise<PendingLearning>;

  /**
   * Get a pending learning by ID
   */
  getPending(id: string): Promise<PendingLearning | null>;

  /**
   * List pending learnings
   */
  listPending(filter?: PendingLearningFilter): Promise<PendingLearning[]>;

  /**
   * Delete a pending learning
   */
  deletePending(id: string): Promise<void>;

  // === Stats ===

  /**
   * Increment times applied counter
   */
  incrementApplied(id: string): Promise<void>;

  /**
   * Record helpfulness feedback
   */
  recordHelpfulness(id: string, helpful: boolean): Promise<void>;
}

// === Matcher Interface ===

/**
 * Context for matching
 */
export interface MatchContext {
  /** Task description */
  taskDescription: string;
  /** File paths involved */
  filePaths?: string[] | undefined;
  /** Technologies involved */
  technologies?: string[] | undefined;
  /** Task plan approach */
  planApproach?: string;
}

/**
 * Result of a match operation
 */
export interface MatchResult {
  /** Whether there is a match */
  matched: boolean;
  /** Match score (0-1) */
  score: number;
  /** Reason for the match */
  reason: string;
  /** Match type (keyword, pattern, semantic) */
  matchType: 'keyword' | 'pattern' | 'semantic' | 'combined';
}

/**
 * Knowledge matcher interface
 */
export interface IKnowledgeMatcher {
  /**
   * Check if an entry matches the given context
   */
  match(entry: KnowledgeEntry, context: MatchContext): Promise<MatchResult | null>;
}

// === Ranker Interface ===

/**
 * Ranked knowledge entry
 */
export interface RankedEntry {
  /** The knowledge entry */
  entry: KnowledgeEntry;
  /** Overall relevance score */
  score: number;
  /** Match details */
  matchResult: MatchResult;
}

/**
 * Relevance ranker interface
 */
export interface IRelevanceRanker {
  /**
   * Rank entries by relevance to query
   */
  rank(
    entries: KnowledgeEntry[],
    query: KnowledgeQuery,
    matches: Map<string, MatchResult>
  ): Promise<RankedEntry[]>;
}

// === Embedding Provider Interface ===

/**
 * Embedding provider interface
 */
export interface IEmbeddingProvider {
  /**
   * Generate embedding for text
   */
  embed(text: string): Promise<number[]>;

  /**
   * Generate embeddings for multiple texts
   */
  embedBatch(texts: string[]): Promise<number[][]>;

  /**
   * Get the embedding dimensions
   */
  getDimensions(): number;
}
