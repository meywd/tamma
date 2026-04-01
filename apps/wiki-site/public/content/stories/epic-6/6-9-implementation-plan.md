---
title: "Story 6-9: Agent Knowledge Base - Implementation Plan"
sidebar:
  order: 60
---

## Overview

This document outlines the implementation plan for the Agent Knowledge Base system, which stores and serves recommendations, prohibited actions, and learnings to agents. The knowledge base is checked before task execution by both agents and the Scrum Master.

---

## 1. Package Location

### Primary Package: `@tamma/intelligence`

Location: `/packages/intelligence/`

**Rationale:**
- The `intelligence` package is designated for research and agent decision-making capabilities
- Knowledge base aligns with the package's purpose of enhancing agent intelligence
- Existing dependency on `@tamma/shared` and `@tamma/providers` which we'll leverage

### Secondary Integrations:
- `@tamma/shared` - Shared types and interfaces
- `@tamma/api` - REST API endpoints for knowledge management
- `@tamma/dashboard` - UI components (Story 6-6)
- `@tamma/events` - Event emission for knowledge changes

---

## 2. Files to Create/Modify

### 2.1 New Files to Create

#### Core Knowledge Base (`/packages/intelligence/src/knowledge-base/`)

```
packages/intelligence/src/knowledge-base/
  index.ts                      # Public exports
  types.ts                      # Knowledge types and interfaces
  knowledge-service.ts          # Main service implementation
  knowledge-store.ts            # Storage abstraction interface
  stores/
    index.ts                    # Store exports
    in-memory-store.ts          # In-memory implementation (dev/testing)
    database-store.ts           # Database implementation (production)
    file-store.ts               # File-based implementation (local)
  matchers/
    index.ts                    # Matcher exports
    keyword-matcher.ts          # Keyword-based matching
    semantic-matcher.ts         # Embedding-based semantic matching
    pattern-matcher.ts          # Regex pattern matching
    relevance-ranker.ts         # Combines and ranks matches
  checkers/
    index.ts                    # Checker exports
    pre-task-checker.ts         # Pre-task knowledge validation
    prohibition-checker.ts      # Prohibition detection
    recommendation-builder.ts   # Build recommendation context
  capture/
    index.ts                    # Capture exports
    learning-capture.ts         # Auto-capture learnings
    duplicate-detector.ts       # Detect similar learnings
    learning-summarizer.ts      # Summarize learnings for storage
  prompt/
    index.ts                    # Prompt exports
    knowledge-prompt-builder.ts # Build agent prompts with KB context
```

#### Shared Types (`/packages/shared/src/`)

```
packages/shared/src/
  types/
    knowledge.ts                # Knowledge-related types (new file)
    index.ts                    # Update with knowledge exports
```

#### API Endpoints (`/packages/api/src/`)

```
packages/api/src/
  routes/
    knowledge.ts                # Knowledge API routes (new file)
  handlers/
    knowledge-handlers.ts       # Request handlers (new file)
```

#### Configuration (`/packages/shared/src/`)

```
packages/shared/src/
  config/
    knowledge-config.ts         # Knowledge configuration schema (new file)
```

### 2.2 Files to Modify

| File | Modification |
|------|-------------|
| `/packages/intelligence/src/index.ts` | Export knowledge-base module |
| `/packages/intelligence/package.json` | Add vector DB dependencies |
| `/packages/shared/src/types/index.ts` | Export knowledge types |
| `/packages/shared/src/index.ts` | Export knowledge config |
| `/packages/api/src/routes/index.ts` | Register knowledge routes |

---

## 3. Interfaces and Types

### 3.1 Core Types (`/packages/shared/src/types/knowledge.ts`)

```typescript
// --- Knowledge Entry ---

export interface KnowledgeEntry {
  id: string;
  type: KnowledgeType;

  // Content
  title: string;
  description: string;
  details?: string;
  examples?: KnowledgeExample[];

  // Scope
  scope: KnowledgeScope;
  projectId?: string;
  agentTypes?: AgentType[];

  // Matching
  keywords: string[];
  patterns?: string[];          // Regex patterns
  embedding?: number[];         // For semantic search

  // Metadata
  priority: KnowledgePriority;
  source: KnowledgeSource;
  sourceRef?: string;           // PR number, task ID, etc.
  createdAt: Date;
  updatedAt: Date;
  createdBy: string;
  validFrom?: Date;
  validUntil?: Date;
  enabled: boolean;

  // Stats
  timesApplied: number;
  timesHelpful: number;
  lastApplied?: Date;
}

export type KnowledgeType = 'recommendation' | 'prohibition' | 'learning';
export type KnowledgeScope = 'global' | 'project' | 'agent_type';
export type KnowledgeSource = 'manual' | 'task_success' | 'task_failure' | 'code_review' | 'import';
export type KnowledgePriority = 'low' | 'medium' | 'high' | 'critical';

export interface KnowledgeExample {
  scenario: string;
  goodApproach?: string;
  badApproach?: string;
  outcome?: string;
}

// --- Agent Types ---

export type AgentType =
  | 'scrum_master'
  | 'architect'
  | 'researcher'
  | 'analyst'
  | 'planner'
  | 'implementer'
  | 'reviewer'
  | 'tester'
  | 'documenter';

// --- Query Types ---

export interface KnowledgeQuery {
  taskType: string;
  taskDescription: string;
  projectId: string;
  agentType: AgentType;
  filePaths?: string[];
  technologies?: string[];
  types?: KnowledgeType[];
  maxResults?: number;
  minPriority?: KnowledgePriority;
}

export interface KnowledgeResult {
  recommendations: KnowledgeEntry[];
  prohibitions: KnowledgeEntry[];
  learnings: KnowledgeEntry[];
  summary: string;
  criticalWarnings: string[];
}

export interface KnowledgeFilter {
  types?: KnowledgeType[];
  scopes?: KnowledgeScope[];
  projectId?: string;
  agentTypes?: AgentType[];
  source?: KnowledgeSource;
  enabled?: boolean;
  priority?: KnowledgePriority;
  search?: string;
  limit?: number;
  offset?: number;
}

// --- Check Result Types ---

export interface KnowledgeCheckResult {
  canProceed: boolean;
  recommendations: KnowledgeMatch[];
  warnings: KnowledgeMatch[];
  blockers: KnowledgeMatch[];
  learnings: KnowledgeMatch[];
}

export interface KnowledgeMatch {
  knowledge: KnowledgeEntry;
  matchReason: string;
  matchScore: number;
  applicability?: number;
}

// --- Learning Capture Types ---

export interface LearningCapture {
  taskId: string;
  projectId: string;
  outcome: 'success' | 'failure' | 'partial';
  description: string;
  whatWorked?: string;
  whatFailed?: string;
  rootCause?: string;
  suggestedTitle: string;
  suggestedDescription: string;
  suggestedKeywords: string[];
  suggestedPriority: KnowledgePriority;
}

export interface PendingLearning extends LearningCapture {
  id: string;
  capturedAt: Date;
  capturedBy: string;
  status: 'pending' | 'approved' | 'rejected';
  reviewedAt?: Date;
  reviewedBy?: string;
  rejectionReason?: string;
}

// --- Import/Export Types ---

export interface KnowledgeImportResult {
  imported: number;
  skipped: number;
  errors: Array<{ entry: Partial<KnowledgeEntry>; error: string }>;
}
```

### 3.2 Service Interface (`/packages/intelligence/src/knowledge-base/types.ts`)

```typescript
export interface IKnowledgeService {
  // Lifecycle
  initialize(config: KnowledgeConfig): Promise<void>;
  dispose(): Promise<void>;

  // Query
  getRelevantKnowledge(query: KnowledgeQuery): Promise<KnowledgeResult>;
  checkBeforeTask(task: TaskContext, plan: DevelopmentPlan): Promise<KnowledgeCheckResult>;

  // CRUD
  addKnowledge(entry: CreateKnowledgeEntry): Promise<KnowledgeEntry>;
  updateKnowledge(id: string, updates: UpdateKnowledgeEntry): Promise<KnowledgeEntry>;
  deleteKnowledge(id: string): Promise<void>;
  getKnowledge(id: string): Promise<KnowledgeEntry | null>;
  listKnowledge(filter?: KnowledgeFilter): Promise<KnowledgeListResult>;

  // Learning capture
  captureLearning(capture: LearningCapture): Promise<PendingLearning>;
  getPendingLearnings(filter?: PendingLearningFilter): Promise<PendingLearning[]>;
  approveLearning(id: string, edits?: Partial<KnowledgeEntry>): Promise<KnowledgeEntry>;
  rejectLearning(id: string, reason: string): Promise<void>;

  // Feedback
  recordApplication(id: string, taskId: string, helpful: boolean): Promise<void>;

  // Import/Export
  importKnowledge(entries: CreateKnowledgeEntry[]): Promise<KnowledgeImportResult>;
  exportKnowledge(filter?: KnowledgeFilter): Promise<KnowledgeEntry[]>;

  // Maintenance
  refreshEmbeddings(): Promise<void>;
  pruneExpired(): Promise<number>;
}

export interface IKnowledgeStore {
  // CRUD
  create(entry: KnowledgeEntry): Promise<KnowledgeEntry>;
  update(id: string, entry: Partial<KnowledgeEntry>): Promise<KnowledgeEntry>;
  delete(id: string): Promise<void>;
  get(id: string): Promise<KnowledgeEntry | null>;
  list(filter?: KnowledgeFilter): Promise<KnowledgeListResult>;

  // Search
  search(query: KnowledgeStoreQuery): Promise<KnowledgeEntry[]>;
  searchByEmbedding(embedding: number[], options: EmbeddingSearchOptions): Promise<KnowledgeEntry[]>;

  // Pending learnings
  createPending(learning: PendingLearning): Promise<PendingLearning>;
  updatePending(id: string, updates: Partial<PendingLearning>): Promise<PendingLearning>;
  getPending(id: string): Promise<PendingLearning | null>;
  listPending(filter?: PendingLearningFilter): Promise<PendingLearning[]>;
  deletePending(id: string): Promise<void>;

  // Stats
  incrementApplied(id: string): Promise<void>;
  incrementHelpful(id: string, helpful: boolean): Promise<void>;
}

export interface IKnowledgeMatcher {
  match(
    entry: KnowledgeEntry,
    context: MatchContext
  ): Promise<MatchResult | null>;
}

export interface IRelevanceRanker {
  rank(
    entries: KnowledgeEntry[],
    query: KnowledgeQuery,
    matches: Map<string, MatchResult>
  ): Promise<RankedEntry[]>;
}
```

### 3.3 Configuration Types (`/packages/shared/src/config/knowledge-config.ts`)

```typescript
export interface KnowledgeConfig {
  storage: KnowledgeStorageConfig;
  capture: LearningCaptureConfig;
  matching: MatchingConfig;
  preTaskCheck: PreTaskCheckConfig;
  retention: RetentionConfig;
}

export interface KnowledgeStorageConfig {
  type: 'memory' | 'database' | 'file';
  connectionString?: string;   // For database
  filePath?: string;           // For file storage
}

export interface LearningCaptureConfig {
  autoCaptureSuccess: boolean;
  autoCaptureFailure: boolean;
  requireApproval: boolean;
  maxPendingDays: number;
}

export interface MatchingConfig {
  useSemantic: boolean;
  semanticThreshold: number;   // 0-1, default 0.7
  keywordBoost: number;        // Multiplier, default 1.5
  maxKeywordDistance: number;  // Levenshtein distance, default 2
}

export interface PreTaskCheckConfig {
  enabled: boolean;
  blockOnCritical: boolean;
  maxRecommendations: number;
  maxLearnings: number;
  maxWarnings: number;
}

export interface RetentionConfig {
  maxAgeDays: number;
  pruneLowPriority: boolean;
  minApplicationsToKeep: number;
  autoArchiveUnused: boolean;
}
```

---

## 4. Implementation Phases

### Phase 1: Core Infrastructure (Week 1)

**Goal:** Establish base types, interfaces, and in-memory storage

#### Tasks:
1. **Create shared types** (`@tamma/shared`)
   - Define all knowledge-related types
   - Export from package index

2. **Create knowledge store interface** (`@tamma/intelligence`)
   - Define `IKnowledgeStore` interface
   - Implement `InMemoryKnowledgeStore`

3. **Create base service structure**
   - Define `IKnowledgeService` interface
   - Create `KnowledgeService` class skeleton

4. **Add configuration**
   - Define `KnowledgeConfig` schema
   - Integrate with existing config system

**Deliverables:**
- Types exported from `@tamma/shared`
- In-memory store for development/testing
- Service skeleton with lifecycle methods

### Phase 2: Matching System (Week 2)

**Goal:** Implement knowledge retrieval and matching logic

#### Tasks:
1. **Keyword matcher**
   - Implement keyword-based matching
   - Support fuzzy matching (Levenshtein distance)
   - Handle synonyms and stemming

2. **Pattern matcher**
   - Implement regex pattern matching against file paths
   - Match patterns against plan descriptions

3. **Semantic matcher** (depends on Story 6-2)
   - Integrate with vector database
   - Generate embeddings for knowledge entries
   - Implement similarity search

4. **Relevance ranker**
   - Combine match scores from all matchers
   - Implement priority boosting
   - Apply recency weighting

**Deliverables:**
- All matchers implemented and tested
- Relevance ranking algorithm
- Integration with vector database (if available)

### Phase 3: Pre-Task Checking (Week 3)

**Goal:** Implement the pre-task validation workflow

#### Tasks:
1. **Pre-task checker**
   - Query relevant knowledge based on task context
   - Check prohibitions against plan
   - Generate warnings and blockers

2. **Prohibition checker**
   - Detailed prohibition matching
   - Pattern matching against file changes
   - Keyword matching against approach

3. **Recommendation builder**
   - Filter and rank recommendations
   - Build context-aware suggestions
   - Token-efficient summarization

4. **Prompt builder**
   - Build augmented prompts with KB context
   - Format blockers, warnings, recommendations
   - Include relevant learnings

**Deliverables:**
- `checkBeforeTask()` fully functional
- `buildAgentPrompt()` for knowledge-augmented prompts
- Integration point for Scrum Master (Story 6-10)

### Phase 4: Learning Capture (Week 4)

**Goal:** Implement automatic and manual learning capture

#### Tasks:
1. **Learning capture service**
   - Capture learnings from task outcomes
   - Extract structured data from results
   - Generate suggested entries

2. **Duplicate detector**
   - Detect similar existing learnings
   - Use semantic similarity
   - Merge or flag duplicates

3. **Learning summarizer**
   - Condense task outcomes into learnings
   - Extract key insights
   - Generate keywords automatically

4. **Approval workflow**
   - Pending learnings queue
   - Approval/rejection with edits
   - Promote to knowledge base

**Deliverables:**
- Auto-capture from task success/failure
- Duplicate detection
- Pending queue management

### Phase 5: Persistence & Production (Week 5)

**Goal:** Production-ready storage and API

#### Tasks:
1. **Database store implementation**
   - PostgreSQL schema design
   - Implement `DatabaseKnowledgeStore`
   - Migration scripts

2. **File store implementation**
   - JSON/YAML file format
   - File-based persistence for simple deployments

3. **API endpoints**
   - REST API for knowledge CRUD
   - Pending learnings management
   - Import/export endpoints

4. **Integration tests**
   - Full workflow tests
   - Performance benchmarks
   - Recovery testing

**Deliverables:**
- Production storage implementations
- API routes registered
- Integration test suite

### Phase 6: Polish & Integration (Week 6)

**Goal:** Final integration and optimization

#### Tasks:
1. **Scrum Master integration** (Story 6-10)
   - Wire up pre-task checking
   - Learning capture hooks
   - Knowledge-augmented prompts

2. **Performance optimization**
   - Query caching
   - Embedding batch processing
   - Index optimization

3. **Observability**
   - Metrics for matches, applications
   - Logging for debugging
   - Tracing for slow queries

4. **Documentation**
   - API documentation
   - Configuration guide
   - Best practices

**Deliverables:**
- Full integration with Scrum Master loop
- Performance targets met
- Complete documentation

---

## 5. Dependencies

### Internal Dependencies

| Dependency | Package | Status | Required By |
|------------|---------|--------|-------------|
| Vector Database | Story 6-2 | Planned | Phase 2 (semantic matching) |
| LLM Provider | `@tamma/providers` | Exists | Phase 4 (learning summarization) |
| Event Store | `@tamma/shared` | Exists | Phase 4 (event capture) |
| Scrum Master Loop | Story 6-10 | Planned | Phase 6 (integration) |
| API Router | `@tamma/api` | Exists | Phase 5 (REST endpoints) |

### External Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| `zod` | ^3.x | Schema validation |
| `pg` | ^8.x | PostgreSQL client (database store) |
| `fast-levenshtein` | ^3.x | Fuzzy string matching |
| `uuid` | ^9.x | ID generation |

### Optional Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| `@chromadb/chromadb` | ^1.x | Vector search (if Story 6-2 uses ChromaDB) |
| `natural` | ^6.x | NLP for stemming/synonyms |

---

## 6. Testing Strategy

### 6.1 Unit Tests

Location: `/packages/intelligence/src/knowledge-base/__tests__/`

#### Test Files:

```
__tests__/
  knowledge-service.test.ts
  stores/
    in-memory-store.test.ts
    database-store.test.ts
    file-store.test.ts
  matchers/
    keyword-matcher.test.ts
    semantic-matcher.test.ts
    pattern-matcher.test.ts
    relevance-ranker.test.ts
  checkers/
    pre-task-checker.test.ts
    prohibition-checker.test.ts
  capture/
    learning-capture.test.ts
    duplicate-detector.test.ts
```

#### Test Categories:

**Knowledge Store Tests:**
- CRUD operations (create, read, update, delete)
- Filter queries (by type, scope, priority)
- Pagination (limit, offset)
- Edge cases (not found, duplicates)

**Matcher Tests:**
- Keyword matching (exact, fuzzy, case-insensitive)
- Pattern matching (regex against paths)
- Semantic matching (embedding similarity)
- No-match scenarios

**Checker Tests:**
- Prohibition detection (critical blocks)
- Warning generation (non-critical)
- Recommendation ranking
- Empty knowledge base handling

**Learning Capture Tests:**
- Success capture
- Failure capture
- Duplicate detection
- Approval/rejection workflow

### 6.2 Integration Tests

Location: `/packages/intelligence/src/knowledge-base/__tests__/integration/`

```
integration/
  full-workflow.test.ts         # End-to-end knowledge lifecycle
  pre-task-checking.test.ts     # Full pre-task check flow
  learning-workflow.test.ts     # Capture to approval to KB
  persistence.test.ts           # Store persistence/recovery
```

**Test Scenarios:**

1. **Full Knowledge Lifecycle:**
   - Create entry -> query -> match -> apply -> feedback

2. **Pre-Task Checking Flow:**
   - Task + Plan -> Check KB -> Blockers/Warnings/Recommendations

3. **Learning Workflow:**
   - Task completion -> Capture -> Review -> Approve -> KB entry

4. **Recovery:**
   - Create entries -> Restart -> Verify persistence

### 6.3 Performance Benchmarks

Location: `/packages/intelligence/src/knowledge-base/__tests__/benchmarks/`

**Targets:**
- Query latency: < 50ms (100 entries)
- Query latency: < 200ms (10,000 entries)
- Pre-task check: < 500ms (including semantic search)
- Embedding generation: < 1s per entry

### 6.4 Test Fixtures

Location: `/packages/intelligence/src/knowledge-base/__tests__/fixtures/`

```
fixtures/
  knowledge-entries.ts          # Sample knowledge entries
  tasks.ts                      # Sample task contexts
  plans.ts                      # Sample development plans
  captures.ts                   # Sample learning captures
```

---

## 7. Configuration

### 7.1 Default Configuration

```yaml
# /config/knowledge.yaml
knowledge:
  storage:
    type: database              # memory | database | file
    connectionString: ${DATABASE_URL}
    filePath: ./data/knowledge  # For file storage

  capture:
    autoCaptureSuccess: true
    autoCaptureFailure: true
    requireApproval: true
    maxPendingDays: 30

  matching:
    useSemantic: true
    semanticThreshold: 0.7
    keywordBoost: 1.5
    maxKeywordDistance: 2

  preTaskCheck:
    enabled: true
    blockOnCritical: true
    maxRecommendations: 5
    maxLearnings: 3
    maxWarnings: 10

  retention:
    maxAgeDays: 365
    pruneLowPriority: true
    minApplicationsToKeep: 3
    autoArchiveUnused: false
```

### 7.2 Environment Variables

| Variable | Description | Default |
|----------|-------------|---------|
| `KNOWLEDGE_STORAGE_TYPE` | Storage backend | `database` |
| `KNOWLEDGE_SEMANTIC_ENABLED` | Enable semantic search | `true` |
| `KNOWLEDGE_AUTO_CAPTURE` | Auto-capture learnings | `true` |
| `KNOWLEDGE_REQUIRE_APPROVAL` | Require learning approval | `true` |

### 7.3 Per-Project Override

Projects can override global knowledge settings:

```yaml
# /projects/{project-id}/tamma.yaml
knowledge:
  preTaskCheck:
    blockOnCritical: true       # Override global setting
  capture:
    autoCaptureSuccess: false   # Disable for this project
```

---

## 8. Database Schema

### 8.1 Tables

```sql
-- Knowledge entries
CREATE TABLE knowledge_entries (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  type VARCHAR(20) NOT NULL,
  title VARCHAR(255) NOT NULL,
  description TEXT NOT NULL,
  details TEXT,
  examples JSONB,
  scope VARCHAR(20) NOT NULL,
  project_id VARCHAR(100),
  agent_types VARCHAR(50)[],
  keywords VARCHAR(100)[] NOT NULL,
  patterns VARCHAR(500)[],
  embedding vector(1536),
  priority VARCHAR(20) NOT NULL DEFAULT 'medium',
  source VARCHAR(20) NOT NULL,
  source_ref VARCHAR(255),
  created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
  updated_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
  created_by VARCHAR(100) NOT NULL,
  valid_from TIMESTAMP WITH TIME ZONE,
  valid_until TIMESTAMP WITH TIME ZONE,
  enabled BOOLEAN DEFAULT TRUE,
  times_applied INTEGER DEFAULT 0,
  times_helpful INTEGER DEFAULT 0,
  last_applied TIMESTAMP WITH TIME ZONE,

  CONSTRAINT valid_type CHECK (type IN ('recommendation', 'prohibition', 'learning')),
  CONSTRAINT valid_scope CHECK (scope IN ('global', 'project', 'agent_type')),
  CONSTRAINT valid_priority CHECK (priority IN ('low', 'medium', 'high', 'critical'))
);

-- Indexes
CREATE INDEX idx_knowledge_type ON knowledge_entries(type);
CREATE INDEX idx_knowledge_scope ON knowledge_entries(scope);
CREATE INDEX idx_knowledge_project ON knowledge_entries(project_id);
CREATE INDEX idx_knowledge_enabled ON knowledge_entries(enabled);
CREATE INDEX idx_knowledge_priority ON knowledge_entries(priority);
CREATE INDEX idx_knowledge_embedding ON knowledge_entries
  USING hnsw (embedding vector_cosine_ops) WITH (m = 16, ef_construction = 64);

-- Full-text search index
CREATE INDEX idx_knowledge_search ON knowledge_entries
  USING GIN (to_tsvector('english', title || ' ' || description));

-- Pending learnings
CREATE TABLE pending_learnings (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  task_id VARCHAR(100) NOT NULL,
  project_id VARCHAR(100) NOT NULL,
  outcome VARCHAR(20) NOT NULL,
  description TEXT NOT NULL,
  what_worked TEXT,
  what_failed TEXT,
  root_cause TEXT,
  suggested_title VARCHAR(255) NOT NULL,
  suggested_description TEXT NOT NULL,
  suggested_keywords VARCHAR(100)[] NOT NULL,
  suggested_priority VARCHAR(20) NOT NULL DEFAULT 'medium',
  status VARCHAR(20) DEFAULT 'pending',
  captured_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
  captured_by VARCHAR(100) NOT NULL,
  reviewed_at TIMESTAMP WITH TIME ZONE,
  reviewed_by VARCHAR(100),
  rejection_reason TEXT,

  CONSTRAINT valid_outcome CHECK (outcome IN ('success', 'failure', 'partial')),
  CONSTRAINT valid_status CHECK (status IN ('pending', 'approved', 'rejected'))
);

CREATE INDEX idx_pending_status ON pending_learnings(status);
CREATE INDEX idx_pending_project ON pending_learnings(project_id);

-- Application tracking
CREATE TABLE knowledge_applications (
  id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
  knowledge_id UUID REFERENCES knowledge_entries(id) ON DELETE CASCADE,
  task_id VARCHAR(100) NOT NULL,
  applied_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
  helpful BOOLEAN
);

CREATE INDEX idx_applications_knowledge ON knowledge_applications(knowledge_id);
```

### 8.2 Migrations

Location: `/packages/intelligence/src/knowledge-base/migrations/`

```
migrations/
  001_create_knowledge_tables.sql
  002_add_embedding_index.sql
  003_add_application_tracking.sql
```

---

## 9. API Endpoints

### 9.1 Knowledge Management

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/api/knowledge` | List knowledge entries |
| `POST` | `/api/knowledge` | Create knowledge entry |
| `GET` | `/api/knowledge/:id` | Get single entry |
| `PATCH` | `/api/knowledge/:id` | Update entry |
| `DELETE` | `/api/knowledge/:id` | Delete entry |
| `POST` | `/api/knowledge/query` | Query relevant knowledge |
| `POST` | `/api/knowledge/check` | Pre-task check |

### 9.2 Learning Management

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/api/knowledge/pending` | List pending learnings |
| `POST` | `/api/knowledge/pending/:id/approve` | Approve learning |
| `POST` | `/api/knowledge/pending/:id/reject` | Reject learning |

### 9.3 Import/Export

| Method | Endpoint | Description |
|--------|----------|-------------|
| `POST` | `/api/knowledge/import` | Import entries |
| `GET` | `/api/knowledge/export` | Export entries |

### 9.4 Feedback

| Method | Endpoint | Description |
|--------|----------|-------------|
| `POST` | `/api/knowledge/:id/applied` | Record application |
| `POST` | `/api/knowledge/:id/feedback` | Submit helpfulness feedback |

---

## 10. Success Criteria

### Functional Requirements

- [ ] CRUD operations for all knowledge types work correctly
- [ ] Keyword matching finds relevant entries
- [ ] Pattern matching detects prohibited file paths
- [ ] Semantic matching returns similar entries (when vector DB available)
- [ ] Pre-task checking blocks on critical prohibitions
- [ ] Pre-task checking warns on non-critical matches
- [ ] Learning capture extracts insights from task outcomes
- [ ] Duplicate detection prevents redundant entries
- [ ] Approval workflow promotes learnings to KB
- [ ] Application tracking records usage statistics

### Performance Requirements

- [ ] Query latency < 200ms (p95)
- [ ] Pre-task check < 500ms (p95)
- [ ] Supports 10,000+ knowledge entries
- [ ] Embedding generation < 1s per entry

### Quality Requirements

- [ ] Unit test coverage > 80%
- [ ] Integration tests pass
- [ ] No critical bugs
- [ ] API documentation complete

---

## 11. Risks and Mitigations

| Risk | Impact | Likelihood | Mitigation |
|------|--------|------------|------------|
| Vector DB not ready (Story 6-2) | Semantic search unavailable | Medium | Fallback to keyword-only matching |
| Performance issues with large KB | Slow queries | Low | Index optimization, caching |
| False positives in prohibition matching | Unnecessary blocks | Medium | Tunable thresholds, user overrides |
| Duplicate learnings flood system | Noise in KB | Medium | Aggressive duplicate detection |
| Learning quality issues | Low-value entries | High | Require human approval |

---

## 12. Open Questions

1. **Embedding model:** Which embedding model to use for semantic search?
   - Options: OpenAI ada-002, Cohere embed, local models
   - Depends on Story 6-2 decisions

2. **Multi-tenant support:** How to handle knowledge isolation in multi-tenant deployments?
   - Proposal: Project-scoped entries with global fallback

3. **Learning approval UX:** How should pending learnings be presented for review?
   - Depends on Story 6-6 (Knowledge Base UI) design

4. **Import format:** What formats should import/export support?
   - Proposal: JSON and YAML initially

---

## 13. References

- [Story 6-9 Requirements](/docs/stories/epic-6/story-6-9/6-9-agent-knowledge-base.md)
- [Story 6-2 Vector Database](/docs/stories/epic-6/story-6-2/6-2-vector-database-integration.md)
- [Story 6-10 Scrum Master Loop](/docs/stories/epic-6/story-6-10/6-10-scrum-master-task-loop.md)
- [Engine Architecture](/docs/architecture/engine-flow.md)
- [Epic 6 Overview](/docs/stories/epic-6/README.md)
