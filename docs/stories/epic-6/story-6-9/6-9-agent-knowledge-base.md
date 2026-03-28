# Story 6-9: Agent Knowledge Base (Recommendations, Prohibited Actions, Learnings)

## User Story

As an **agent**, I need access to a knowledge base of recommendations, prohibited actions, and learnings so that I can make better decisions and avoid repeating mistakes.

## Description

Implement a knowledge base system that stores and serves three types of knowledge to agents: recommendations (best practices), prohibited actions (things to avoid), and learnings (insights from past tasks). This knowledge is checked before task execution by both the agent and its manager (Scrum Master).

## Acceptance Criteria

### AC1: Knowledge Categories
- [ ] **Recommendations**: Best practices, preferred approaches, coding standards
- [ ] **Prohibited Actions**: Actions to avoid, known problematic patterns
- [ ] **Learnings**: Insights from past successes and failures

### AC2: Knowledge Scope
- [ ] Global knowledge (applies to all projects)
- [ ] Per-project knowledge (project-specific)
- [ ] Per-agent-type knowledge (role-specific)
- [ ] Temporal knowledge (valid for time period)

### AC3: Knowledge Sources
- [ ] Manually curated by administrators
- [ ] Extracted from successful task completions
- [ ] Extracted from failures and retries
- [ ] Imported from external sources
- [ ] Generated from code review feedback

### AC4: Pre-Task Checking
- [ ] Agent queries relevant knowledge before starting task
- [ ] Scrum Master validates task plan against knowledge
- [ ] Warnings for potential violations
- [ ] Suggestions based on recommendations
- [ ] Block task if critical prohibition matched

### AC5: Knowledge Application
- [ ] Relevance matching (semantic + keyword)
- [ ] Context-aware filtering
- [ ] Priority ranking
- [ ] Token-efficient summarization

### AC6: Learning Capture
- [ ] Auto-capture learnings from completed tasks
- [ ] Capture from failed tasks (what went wrong)
- [ ] Capture from code review feedback
- [ ] Human curation/approval of auto-captured learnings
- [ ] Duplicate/similar learning detection

### AC7: Knowledge Management UI
- [ ] View all knowledge entries
- [ ] Add/edit/delete entries
- [ ] Search and filter
- [ ] Review pending learnings
- [ ] Import/export knowledge

## Technical Design

### Knowledge Schema

```typescript
interface KnowledgeEntry {
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
  patterns?: string[];        // Regex patterns for matching
  embedding?: number[];       // For semantic search

  // Metadata
  priority: 'low' | 'medium' | 'high' | 'critical';
  source: KnowledgeSource;
  sourceRef?: string;         // PR number, task ID, etc.
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

type KnowledgeType = 'recommendation' | 'prohibition' | 'learning';
type KnowledgeScope = 'global' | 'project' | 'agent_type';
type KnowledgeSource = 'manual' | 'task_success' | 'task_failure' | 'code_review' | 'import';

interface KnowledgeExample {
  scenario: string;
  goodApproach?: string;
  badApproach?: string;
  outcome?: string;
}
```

### Knowledge Examples

```typescript
const exampleKnowledge: KnowledgeEntry[] = [
  // Recommendation
  {
    id: 'rec-001',
    type: 'recommendation',
    title: 'Use TypeScript strict mode',
    description: 'Always enable strict mode in TypeScript projects for better type safety',
    details: 'Set "strict": true in tsconfig.json. This catches many common errors at compile time.',
    keywords: ['typescript', 'tsconfig', 'strict', 'type safety'],
    scope: 'global',
    priority: 'high',
    source: 'manual',
  },

  // Prohibition
  {
    id: 'pro-001',
    type: 'prohibition',
    title: 'Never commit .env files',
    description: 'Environment files contain secrets and must never be committed to git',
    details: 'Always add .env* to .gitignore. Use environment variables or secret managers for sensitive data.',
    keywords: ['env', 'secrets', 'git', 'security'],
    patterns: ['\\.env', 'secrets\\.', 'credentials\\.'],
    scope: 'global',
    priority: 'critical',
    source: 'manual',
  },

  // Prohibition (project-specific)
  {
    id: 'pro-002',
    type: 'prohibition',
    title: 'Do not modify legacy auth module',
    description: 'The legacy auth module is deprecated and scheduled for removal. All changes should go to the new auth system.',
    keywords: ['auth', 'legacy', 'authentication'],
    patterns: ['src/legacy/auth'],
    scope: 'project',
    projectId: 'project-a',
    priority: 'high',
    source: 'manual',
  },

  // Learning (from past task)
  {
    id: 'learn-001',
    type: 'learning',
    title: 'Rate limiting needs exponential backoff',
    description: 'When implementing API rate limiting, simple retry delays are not sufficient',
    details: 'Learned from PR #234: Fixed retry logic with exponential backoff and jitter. Without jitter, multiple clients can synchronize and cause thundering herd.',
    examples: [{
      scenario: 'API returns 429 Too Many Requests',
      badApproach: 'Wait fixed 1 second and retry',
      goodApproach: 'Exponential backoff: 1s, 2s, 4s with ±25% jitter',
      outcome: 'Reduced retry storms by 90%',
    }],
    keywords: ['rate limit', 'retry', 'backoff', 'api'],
    scope: 'global',
    priority: 'medium',
    source: 'task_success',
    sourceRef: 'PR #234',
  },
];
```

### Knowledge Service

```typescript
interface IKnowledgeService {
  // Query
  getRelevantKnowledge(query: KnowledgeQuery): Promise<KnowledgeResult>;

  // Management
  addKnowledge(entry: Omit<KnowledgeEntry, 'id' | 'createdAt' | 'updatedAt'>): Promise<KnowledgeEntry>;
  updateKnowledge(id: string, updates: Partial<KnowledgeEntry>): Promise<KnowledgeEntry>;
  deleteKnowledge(id: string): Promise<void>;
  listKnowledge(filter?: KnowledgeFilter): Promise<KnowledgeEntry[]>;

  // Learning capture
  captureLearning(capture: LearningCapture): Promise<KnowledgeEntry>;
  getPendingLearnings(): Promise<PendingLearning[]>;
  approveLearning(id: string, edits?: Partial<KnowledgeEntry>): Promise<KnowledgeEntry>;
  rejectLearning(id: string, reason: string): Promise<void>;

  // Feedback
  recordApplication(id: string, taskId: string, helpful: boolean): Promise<void>;

  // Import/Export
  importKnowledge(entries: KnowledgeEntry[]): Promise<ImportResult>;
  exportKnowledge(filter?: KnowledgeFilter): Promise<KnowledgeEntry[]>;
}

interface KnowledgeQuery {
  // What we're doing
  taskType: TaskType;
  taskDescription: string;

  // Context
  projectId: string;
  agentType: AgentType;
  filePaths?: string[];
  technologies?: string[];

  // Options
  types?: KnowledgeType[];
  maxResults?: number;
  minPriority?: Priority;
}

interface KnowledgeResult {
  recommendations: KnowledgeEntry[];
  prohibitions: KnowledgeEntry[];
  learnings: KnowledgeEntry[];

  // Formatted for agent consumption
  summary: string;
  criticalWarnings: string[];
}

interface LearningCapture {
  taskId: string;
  projectId: string;
  outcome: 'success' | 'failure' | 'partial';

  // What happened
  description: string;
  whatWorked?: string;
  whatFailed?: string;
  rootCause?: string;

  // Suggested learning
  suggestedTitle: string;
  suggestedDescription: string;
  suggestedKeywords: string[];
  suggestedPriority: Priority;
}
```

### Pre-Task Knowledge Check

```typescript
class TaskKnowledgeChecker {
  private knowledgeService: IKnowledgeService;

  async checkBeforeTask(
    task: TaskContext,
    plan: DevelopmentPlan
  ): Promise<KnowledgeCheckResult> {
    // Query relevant knowledge
    const knowledge = await this.knowledgeService.getRelevantKnowledge({
      taskType: task.type,
      taskDescription: task.description,
      projectId: task.projectId,
      agentType: task.agentType,
      filePaths: plan.fileChanges.map(f => f.path),
      technologies: this.extractTechnologies(plan),
    });

    const result: KnowledgeCheckResult = {
      canProceed: true,
      recommendations: [],
      warnings: [],
      blockers: [],
    };

    // Check prohibitions
    for (const prohibition of knowledge.prohibitions) {
      const match = this.checkProhibitionMatch(prohibition, plan);
      if (match) {
        if (prohibition.priority === 'critical') {
          result.canProceed = false;
          result.blockers.push({
            knowledge: prohibition,
            matchReason: match.reason,
          });
        } else {
          result.warnings.push({
            knowledge: prohibition,
            matchReason: match.reason,
          });
        }
      }
    }

    // Add recommendations
    for (const rec of knowledge.recommendations) {
      result.recommendations.push({
        knowledge: rec,
        applicability: this.assessApplicability(rec, plan),
      });
    }

    // Add relevant learnings
    result.learnings = knowledge.learnings.map(l => ({
      knowledge: l,
      relevance: this.assessRelevance(l, plan),
    }));

    return result;
  }

  private checkProhibitionMatch(
    prohibition: KnowledgeEntry,
    plan: DevelopmentPlan
  ): { reason: string } | null {
    // Check patterns against file paths
    if (prohibition.patterns) {
      for (const pattern of prohibition.patterns) {
        const regex = new RegExp(pattern);
        for (const file of plan.fileChanges) {
          if (regex.test(file.path)) {
            return { reason: `File ${file.path} matches prohibited pattern ${pattern}` };
          }
        }
      }
    }

    // Check keywords in plan description
    for (const keyword of prohibition.keywords) {
      if (plan.approach.toLowerCase().includes(keyword.toLowerCase())) {
        return { reason: `Plan mentions prohibited keyword: ${keyword}` };
      }
    }

    return null;
  }
}
```

### Knowledge-Augmented Agent Prompt

```typescript
function buildAgentPrompt(
  task: TaskContext,
  plan: DevelopmentPlan,
  knowledgeCheck: KnowledgeCheckResult
): string {
  let prompt = `## Task\n${task.description}\n\n`;

  prompt += `## Plan\n${plan.approach}\n\n`;

  // Add knowledge context
  if (knowledgeCheck.blockers.length > 0) {
    prompt += `## CRITICAL BLOCKERS - DO NOT PROCEED\n`;
    for (const blocker of knowledgeCheck.blockers) {
      prompt += `- ${blocker.knowledge.title}: ${blocker.knowledge.description}\n`;
      prompt += `  Reason: ${blocker.matchReason}\n`;
    }
    prompt += `\n`;
  }

  if (knowledgeCheck.warnings.length > 0) {
    prompt += `## Warnings - Proceed with Caution\n`;
    for (const warning of knowledgeCheck.warnings) {
      prompt += `- ${warning.knowledge.title}: ${warning.knowledge.description}\n`;
    }
    prompt += `\n`;
  }

  if (knowledgeCheck.recommendations.length > 0) {
    prompt += `## Recommendations\n`;
    for (const rec of knowledgeCheck.recommendations) {
      prompt += `- ${rec.knowledge.title}: ${rec.knowledge.description}\n`;
    }
    prompt += `\n`;
  }

  if (knowledgeCheck.learnings.length > 0) {
    prompt += `## Relevant Learnings from Past Tasks\n`;
    for (const learning of knowledgeCheck.learnings.slice(0, 3)) {
      prompt += `- ${learning.knowledge.title}: ${learning.knowledge.description}\n`;
      if (learning.knowledge.examples?.[0]) {
        const ex = learning.knowledge.examples[0];
        prompt += `  Good approach: ${ex.goodApproach}\n`;
      }
    }
    prompt += `\n`;
  }

  return prompt;
}
```

## Configuration

```yaml
knowledge:
  storage:
    type: database  # database | file
    path: ./knowledge  # For file storage

  capture:
    auto_capture_success: true
    auto_capture_failure: true
    require_approval: true  # For auto-captured learnings

  matching:
    use_semantic: true
    semantic_threshold: 0.7
    keyword_boost: 1.5

  pre_task_check:
    enabled: true
    block_on_critical: true
    max_recommendations: 5
    max_learnings: 3

  retention:
    max_age_days: 365
    prune_low_priority: true
    min_applications_to_keep: 3
```

## Dependencies

- Vector database (Story 6-2) for semantic search
- Event store for learning capture
- Scrum Master for approval workflow

## Testing Strategy

### Unit Tests
- Knowledge matching logic
- Prohibition detection
- Learning capture
- Knowledge ranking

### Integration Tests
- Pre-task checking workflow
- Scrum Master approval
- Knowledge persistence

## Success Metrics

- Prohibition violation rate < 1%
- Learning capture rate > 80% of tasks
- Knowledge-applied tasks success rate +15%
- User-reported knowledge helpfulness > 4/5

## Logging Requirements

All intelligence modules MUST accept `ILogger` from `@tamma/shared/contracts` via constructor injection.

- **INFO**: Index scan started/completed (file count, duration), vector search executed (query, result count), RAG pipeline invoked, knowledge base query matched
- **DEBUG**: Chunk boundaries, embedding dimensions, similarity scores, cache hit/miss, budget allocation
- **WARN**: Embedding API rate limited, vector store connection degraded, stale index detected, budget threshold approaching
- **ERROR**: Embedding generation failed, vector store unreachable, index corruption detected, MCP tool call failed
- **Structured context**: Always include `{ repository, indexId, queryId, sourceType }` where applicable
- **Performance**: Log duration for all external API calls (embedding, vector search, MCP tool invocations)
- **Cost tracking**: Log token counts for embedding operations to support LLM cost monitoring (Story 6-7)
