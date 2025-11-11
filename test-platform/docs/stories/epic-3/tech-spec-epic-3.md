# Epic Technical Specification: Test Bank Management

**Date:** 2025-11-04  
**Author:** meywd  
**Epic ID:** 3  
**Status:** Draft  
**Project:** AI Benchmarking Test Platform (AIBaaS)

---

## Overview

Epic 3 implements the comprehensive test bank management system that serves as the foundation for all AI benchmarking activities. This epic delivers the task repository with quality assurance mechanisms, contamination prevention systems, and an initial curated test bank covering various AI capabilities. The system ensures test integrity, prevents data leakage between training and evaluation sets, and provides version control for benchmark tasks while maintaining scalability for thousands of test cases across multiple domains.

This epic directly addresses the core benchmarking requirements from the PRD: comprehensive test coverage (FR-5), quality assurance mechanisms (FR-6), contamination prevention (FR-7), and test bank management (FR-8). By implementing a robust task repository with metadata management, quality scoring, and contamination detection, Epic 3 ensures the platform can provide reliable, unbiased benchmarking results that are essential for fair AI model evaluation and comparison.

## Objectives and Scope

**In Scope:**

- Story 3.1: Task Repository Schema & Storage - Database schema for tasks, metadata, and versioning
- Story 3.2: Task Quality Assurance System - Automated quality scoring and validation
- Story 3.3: Contamination Prevention System - Data leakage detection and prevention
- Story 3.4: Initial Test Bank Creation - Curated initial test set across AI capabilities

**Out of Scope:**

- Benchmark execution engine (Epic 4)
- Evaluation and scoring systems (Epic 5)
- User interfaces for test management (Epic 6)
- Advanced analytics and reporting (Epic 5 enhancements)

## System Architecture Alignment

Epic 3 implements the test bank management layer that stores and manages all benchmark tasks:

### Task Repository Architecture

- **Database Schema:** PostgreSQL with JSONB for flexible task metadata
- **Version Control:** Git-like versioning for task evolution
- **Quality Metrics:** Comprehensive quality scoring system
- **Contamination Detection:** Automated similarity analysis and data leakage prevention

### Quality Assurance Framework

- **Automated Validation:** Syntax, semantic, and quality checks
- **Human Review Workflow:** Review queue and approval process
- **Quality Scoring:** Multi-dimensional quality metrics
- **Continuous Improvement:** Feedback loops for quality enhancement

### Contamination Prevention System

- **Similarity Analysis:** Text similarity and semantic matching
- **Training Data Detection:** Cross-reference with known training datasets
- **Temporal Analysis:** Ensure test data wasn't created after model training
- **Isolation Mechanisms:** Separate test and development environments

## Detailed Design

### Services and Modules

#### 1. Task Repository Schema & Storage (Story 3.1)

**Core Database Schema:**

```sql
-- Tasks (Main test cases)
CREATE TABLE tasks (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name VARCHAR(255) NOT NULL,
    description TEXT,
    category VARCHAR(100) NOT NULL,
    subcategory VARCHAR(100),
    difficulty_level VARCHAR(50) NOT NULL,
    task_type VARCHAR(50) NOT NULL,
    content JSONB NOT NULL,
    expected_output JSONB,
    metadata JSONB DEFAULT '{}',
    version INTEGER NOT NULL DEFAULT 1,
    parent_task_id UUID REFERENCES tasks(id),
    status VARCHAR(50) NOT NULL DEFAULT 'draft',
    quality_score DECIMAL(3,2),
    contamination_score DECIMAL(3,2),
    created_by UUID NOT NULL REFERENCES users(id),
    created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
    updated_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
    published_at TIMESTAMP WITH TIME ZONE,
    tags TEXT[],
    INDEX idx_tasks_category (category),
    INDEX idx_tasks_status (status),
    INDEX idx_tasks_quality_score (quality_score),
    INDEX idx_tasks_created_at (created_at),
    INDEX idx_tasks_tags USING GIN (tags)
);

-- Task Versions (Version history)
CREATE TABLE task_versions (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    task_id UUID NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
    version INTEGER NOT NULL,
    content JSONB NOT NULL,
    expected_output JSONB,
    metadata JSONB DEFAULT '{}',
    change_description TEXT,
    created_by UUID NOT NULL REFERENCES users(id),
    created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
    UNIQUE(task_id, version),
    INDEX idx_task_versions_task_id (task_id),
    INDEX idx_task_versions_created_at (created_at)
);

-- Task Quality Metrics
CREATE TABLE task_quality_metrics (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    task_id UUID NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
    metric_type VARCHAR(100) NOT NULL,
    metric_value DECIMAL(5,2) NOT NULL,
    metric_details JSONB,
    calculated_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
    calculated_by UUID REFERENCES users(id),
    INDEX idx_task_quality_metrics_task_id (task_id),
    INDEX idx_task_quality_metrics_type (metric_type)
);

-- Contamination Analysis
CREATE TABLE contamination_analysis (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    task_id UUID NOT NULL REFERENCES tasks(id) ON DELETE CASCADE,
    analysis_type VARCHAR(100) NOT NULL,
    contamination_score DECIMAL(3,2) NOT NULL,
    similar_tasks JSONB,
    training_data_matches JSONB,
    analysis_details JSONB,
    analyzed_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
    analyzed_by UUID REFERENCES users(id),
    INDEX idx_contamination_analysis_task_id (task_id),
    INDEX idx_contamination_analysis_score (contamination_score)
);

-- Task Categories
CREATE TABLE task_categories (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name VARCHAR(100) UNIQUE NOT NULL,
    description TEXT,
    parent_category_id UUID REFERENCES task_categories(id),
    metadata JSONB DEFAULT '{}',
    created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
    INDEX idx_task_categories_parent (parent_category_id)
);

-- Task Collections (Bundled test sets)
CREATE TABLE task_collections (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name VARCHAR(255) NOT NULL,
    description TEXT,
    collection_type VARCHAR(50) NOT NULL,
    task_ids UUID[] NOT NULL,
    metadata JSONB DEFAULT '{}',
    created_by UUID NOT NULL REFERENCES users(id),
    created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
    updated_at TIMESTAMP WITH TIME ZONE DEFAULT NOW(),
    is_public BOOLEAN DEFAULT false,
    INDEX idx_task_collections_type (collection_type),
    INDEX idx_task_collections_public (is_public)
);
```

**Task Data Models:**

```typescript
interface Task {
  id: string;
  name: string;
  description?: string;
  category: string;
  subcategory?: string;
  difficultyLevel: DifficultyLevel;
  taskType: TaskType;
  content: TaskContent;
  expectedOutput?: ExpectedOutput;
  metadata: TaskMetadata;
  version: number;
  parentTaskId?: string;
  status: TaskStatus;
  qualityScore?: number;
  contaminationScore?: number;
  createdBy: string;
  createdAt: Date;
  updatedAt: Date;
  publishedAt?: Date;
  tags: string[];
}

enum DifficultyLevel {
  BEGINNER = 'beginner',
  INTERMEDIATE = 'intermediate',
  ADVANCED = 'advanced',
  EXPERT = 'expert',
}

enum TaskType {
  CODING = 'coding',
  REASONING = 'reasoning',
  CREATIVE = 'creative',
  ANALYSIS = 'analysis',
  COMPREHENSION = 'comprehension',
  TRANSLATION = 'translation',
  SUMMARIZATION = 'summarization',
  CLASSIFICATION = 'classification',
}

enum TaskStatus {
  DRAFT = 'draft',
  REVIEW = 'review',
  APPROVED = 'approved',
  PUBLISHED = 'published',
  DEPRECATED = 'deprecated',
  ARCHIVED = 'archived',
}

interface TaskContent {
  prompt: string;
  context?: string;
  constraints?: string[];
  examples?: TaskExample[];
  resources?: TaskResource[];
  evaluationCriteria?: EvaluationCriteria[];
}

interface TaskExample {
  input: any;
  expectedOutput: any;
  explanation?: string;
}

interface TaskResource {
  type: 'text' | 'image' | 'code' | 'data';
  content: string;
  description?: string;
}

interface EvaluationCriteria {
  name: string;
  description: string;
  weight: number;
  evaluationMethod: 'automatic' | 'manual' | 'hybrid';
}

interface TaskMetadata {
  estimatedTime?: number; // minutes
  requiredSkills?: string[];
  prerequisites?: string[];
  learningObjectives?: string[];
  domain: string;
  language: string;
  complexity: number; // 1-10
  tokens?: {
    prompt: number;
    expected: number;
  };
}

interface ExpectedOutput {
  type: 'text' | 'code' | 'json' | 'number' | 'boolean';
  value?: any;
  format?: string;
  constraints?: OutputConstraint[];
}

interface OutputConstraint {
  type: 'length' | 'format' | 'content' | 'structure';
  rule: string;
  required: boolean;
}
```

**Task Repository Service:**

```typescript
interface TaskRepository {
  // CRUD operations
  createTask(task: CreateTaskRequest): Promise<Task>;
  getTask(taskId: string): Promise<Task | null>;
  updateTask(taskId: string, updates: UpdateTaskRequest): Promise<Task>;
  deleteTask(taskId: string): Promise<void>;

  // Versioning
  createTaskVersion(taskId: string, changes: TaskChanges): Promise<Task>;
  getTaskVersions(taskId: string): Promise<TaskVersion[]>;
  revertTask(taskId: string, version: number): Promise<Task>;

  // Search and filtering
  searchTasks(query: TaskSearchQuery): Promise<TaskSearchResult>;
  getTasksByCategory(category: string): Promise<Task[]>;
  getTasksByDifficulty(difficulty: DifficultyLevel): Promise<Task[]>;
  getTasksByType(type: TaskType): Promise<Task[]>;

  // Collections
  createCollection(collection: CreateCollectionRequest): Promise<TaskCollection>;
  getCollection(collectionId: string): Promise<TaskCollection | null>;
  addTasksToCollection(collectionId: string, taskIds: string[]): Promise<void>;
  removeTasksFromCollection(collectionId: string, taskIds: string[]): Promise<void>;
}

class TaskRepositoryService implements TaskRepository {
  constructor(
    private db: Database,
    private eventBus: EventBus,
    private auditLogger: AuditLogger
  ) {}

  async createTask(request: CreateTaskRequest): Promise<Task> {
    const task = await this.db.transaction(async (tx) => {
      // Create main task record
      const task = await this.insertTask(tx, request);

      // Create initial version
      await this.createTaskVersion(tx, task.id, 1, request.content);

      // Initialize quality metrics
      await this.initializeQualityMetrics(tx, task.id);

      return task;
    });

    // Emit events
    await this.eventBus.emit('task.created', { taskId: task.id });
    await this.auditLogger.log('TASK_CREATED', { taskId: task.id, userId: request.createdBy });

    return task;
  }

  async searchTasks(query: TaskSearchQuery): Promise<TaskSearchResult> {
    const {
      text,
      category,
      difficulty,
      type,
      tags,
      qualityScore,
      status,
      page = 1,
      limit = 20,
    } = query;

    let sql = `
      SELECT t.*, 
             ts_rank(search_vector, plainto_tsquery($1)) as relevance
      FROM tasks t
      WHERE 1=1
    `;
    const params: any[] = [text || ''];
    let paramIndex = 2;

    if (category) {
      sql += ` AND t.category = $${paramIndex++}`;
      params.push(category);
    }

    if (difficulty) {
      sql += ` AND t.difficulty_level = $${paramIndex++}`;
      params.push(difficulty);
    }

    if (type) {
      sql += ` AND t.task_type = $${paramIndex++}`;
      params.push(type);
    }

    if (tags && tags.length > 0) {
      sql += ` AND t.tags && $${paramIndex++}`;
      params.push(tags);
    }

    if (qualityScore) {
      sql += ` AND t.quality_score >= $${paramIndex++}`;
      params.push(qualityScore);
    }

    if (status) {
      sql += ` AND t.status = $${paramIndex++}`;
      params.push(status);
    }

    if (text) {
      sql += ` AND search_vector @@ plainto_tsquery($1)`;
    }

    sql += ` ORDER BY relevance DESC, t.created_at DESC`;

    const offset = (page - 1) * limit;
    sql += ` LIMIT $${paramIndex++} OFFSET $${paramIndex++}`;
    params.push(limit, offset);

    const tasks = await this.db.query(sql, params);
    const totalCount = await this.getSearchCount(query);

    return {
      tasks,
      pagination: {
        page,
        limit,
        total: totalCount,
        totalPages: Math.ceil(totalCount / limit),
      },
    };
  }

  private async initializeQualityMetrics(tx: any, taskId: string): Promise<void> {
    const initialMetrics = [
      { type: 'completeness', value: 0 },
      { type: 'clarity', value: 0 },
      { type: 'difficulty_accuracy', value: 0 },
      { type: 'uniqueness', value: 0 },
    ];

    for (const metric of initialMetrics) {
      await tx.query(
        `
        INSERT INTO task_quality_metrics (task_id, metric_type, metric_value, calculated_at)
        VALUES ($1, $2, $3, NOW())
      `,
        [taskId, metric.type, metric.value]
      );
    }
  }
}

interface TaskSearchQuery {
  text?: string;
  category?: string;
  difficulty?: DifficultyLevel;
  type?: TaskType;
  tags?: string[];
  qualityScore?: number;
  status?: TaskStatus;
  page?: number;
  limit?: number;
}

interface TaskSearchResult {
  tasks: Task[];
  pagination: {
    page: number;
    limit: number;
    total: number;
    totalPages: number;
  };
}
```

#### 2. Task Quality Assurance System (Story 3.2)

**Quality Metrics Framework:**

```typescript
interface QualityMetric {
  type: QualityMetricType;
  name: string;
  description: string;
  weight: number;
  calculator: QualityCalculator;
  threshold: {
    minimum: number;
    target: number;
    maximum: number;
  };
}

enum QualityMetricType {
  COMPLETENESS = 'completeness',
  CLARITY = 'clarity',
  DIFFICULTY_ACCURACY = 'difficulty_accuracy',
  UNIQUENESS = 'uniqueness',
  FEASIBILITY = 'feasibility',
  OBJECTIVITY = 'objectivity',
  REPRODUCIBILITY = 'reproducibility',
}

interface QualityCalculator {
  calculate(task: Task): Promise<QualityScore>;
  validate(task: Task): Promise<ValidationResult>;
}

interface QualityScore {
  value: number; // 0-100
  details: QualityScoreDetails;
  confidence: number; // 0-1
  recommendations: string[];
}

interface QualityScoreDetails {
  subScores: Record<string, number>;
  issues: QualityIssue[];
  strengths: string[];
}

interface QualityIssue {
  type: string;
  severity: 'low' | 'medium' | 'high' | 'critical';
  description: string;
  suggestion?: string;
  location?: string;
}

interface ValidationResult {
  isValid: boolean;
  errors: ValidationError[];
  warnings: ValidationWarning[];
}

interface ValidationError {
  field: string;
  message: string;
  code: string;
}

interface ValidationWarning {
  field: string;
  message: string;
  code: string;
}
```

**Quality Calculators Implementation:**

```typescript
class CompletenessCalculator implements QualityCalculator {
  async calculate(task: Task): Promise<QualityScore> {
    const checks = [
      { field: 'name', present: !!task.name, weight: 0.1 },
      { field: 'description', present: !!task.description, weight: 0.15 },
      { field: 'content.prompt', present: !!task.content.prompt, weight: 0.25 },
      { field: 'expectedOutput', present: !!task.expectedOutput, weight: 0.2 },
      {
        field: 'evaluationCriteria',
        present: !!task.content.evaluationCriteria?.length,
        weight: 0.15,
      },
      { field: 'examples', present: !!task.content.examples?.length, weight: 0.1 },
      { field: 'metadata.difficulty', present: !!task.metadata.difficulty, weight: 0.05 },
    ];

    let totalScore = 0;
    const issues: QualityIssue[] = [];

    for (const check of checks) {
      if (check.present) {
        totalScore += check.weight * 100;
      } else {
        issues.push({
          type: 'missing_field',
          severity: 'medium',
          description: `Missing required field: ${check.field}`,
          suggestion: `Add content for ${check.field} to improve completeness`,
        });
      }
    }

    return {
      value: Math.round(totalScore),
      details: {
        subScores: Object.fromEntries(checks.map((c) => [c.field, c.present ? 100 : 0])),
        issues,
        strengths: checks.filter((c) => c.present).map((c) => `Complete ${c.field}`),
      },
      confidence: 0.9,
      recommendations: issues.map((i) => i.suggestion).filter(Boolean) as string[],
    };
  }

  async validate(task: Task): Promise<ValidationResult> {
    const errors: ValidationError[] = [];
    const warnings: ValidationWarning[] = [];

    if (!task.name) {
      errors.push({
        field: 'name',
        message: 'Task name is required',
        code: 'REQUIRED_FIELD',
      });
    }

    if (!task.content.prompt) {
      errors.push({
        field: 'content.prompt',
        message: 'Task prompt is required',
        code: 'REQUIRED_FIELD',
      });
    }

    if (!task.expectedOutput && task.taskType !== TaskType.CREATIVE) {
      warnings.push({
        field: 'expectedOutput',
        message: 'Expected output is recommended for non-creative tasks',
        code: 'RECOMMENDED_FIELD',
      });
    }

    return {
      isValid: errors.length === 0,
      errors,
      warnings,
    };
  }
}

class ClarityCalculator implements QualityCalculator {
  async calculate(task: Task): Promise<QualityScore> {
    const prompt = task.content.prompt;

    // Text analysis metrics
    const readabilityScore = this.calculateReadability(prompt);
    const ambiguityScore = this.calculateAmbiguity(prompt);
    const specificityScore = this.calculateSpecificity(prompt);
    const structureScore = this.calculateStructure(prompt);

    const overallScore =
      readabilityScore * 0.3 +
      (100 - ambiguityScore) * 0.25 +
      specificityScore * 0.25 +
      structureScore * 0.2;

    const issues: QualityIssue[] = [];

    if (readabilityScore < 70) {
      issues.push({
        type: 'readability',
        severity: 'medium',
        description: 'Task prompt may be difficult to read',
        suggestion: 'Simplify language and break down complex sentences',
      });
    }

    if (ambiguityScore > 30) {
      issues.push({
        type: 'ambiguity',
        severity: 'high',
        description: 'Task prompt contains ambiguous terms',
        suggestion: 'Clarify ambiguous terms and provide specific examples',
      });
    }

    if (specificityScore < 60) {
      issues.push({
        type: 'specificity',
        severity: 'medium',
        description: 'Task prompt lacks specific instructions',
        suggestion: 'Add more specific details about expected output format',
      });
    }

    return {
      value: Math.round(overallScore),
      details: {
        subScores: {
          readability: readabilityScore,
          ambiguity: 100 - ambiguityScore,
          specificity: specificityScore,
          structure: structureScore,
        },
        issues,
        strengths: this.identifyStrengths(prompt),
      },
      confidence: 0.8,
      recommendations: issues.map((i) => i.suggestion).filter(Boolean) as string[],
    };
  }

  private calculateReadability(text: string): number {
    // Simplified Flesch Reading Ease calculation
    const sentences = text.split(/[.!?]+/).length;
    const words = text.split(/\s+/).length;
    const syllables = this.countSyllables(text);

    if (sentences === 0 || words === 0) return 0;

    const avgSentenceLength = words / sentences;
    const avgSyllablesPerWord = syllables / words;

    const fleschScore = 206.835 - 1.015 * avgSentenceLength - 84.6 * avgSyllablesPerWord;
    return Math.max(0, Math.min(100, fleschScore));
  }

  private calculateAmbiguity(text: string): number {
    const ambiguousWords = [
      'maybe',
      'perhaps',
      'possibly',
      'might',
      'could',
      'should',
      'some',
      'few',
      'many',
      'several',
      'often',
      'sometimes',
      'good',
      'bad',
      'nice',
      'interesting',
      'appropriate',
    ];

    const words = text.toLowerCase().split(/\s+/);
    const ambiguousCount = words.filter((word) => ambiguousWords.includes(word)).length;

    return (ambiguousCount / words.length) * 100;
  }

  private calculateSpecificity(text: string): number {
    // Check for specific instructions, examples, constraints
    let specificityScore = 50; // Base score

    if (text.includes('example') || text.includes('for instance')) {
      specificityScore += 15;
    }

    if (text.includes('format') || text.includes('structure')) {
      specificityScore += 15;
    }

    if (text.includes('must') || text.includes('should') || text.includes('require')) {
      specificityScore += 10;
    }

    if (/\d+/.test(text)) {
      // Contains numbers
      specificityScore += 10;
    }

    return Math.min(100, specificityScore);
  }

  private calculateStructure(text: string): number {
    let structureScore = 0;

    // Has clear introduction
    if (text.length > 50) structureScore += 20;

    // Has bullet points or numbered lists
    if (/[•\-\*]\s|\d+\./.test(text)) structureScore += 30;

    // Has clear sections
    if (text.includes('\n\n')) structureScore += 25;

    // Has conclusion or summary
    if (text.includes('finally') || text.includes('in conclusion')) structureScore += 25;

    return Math.min(100, structureScore);
  }

  private countSyllables(text: string): number {
    const words = text.toLowerCase().split(/\s+/);
    let syllableCount = 0;

    for (const word of words) {
      const cleanWord = word.replace(/[^a-z]/g, '');
      if (cleanWord.length === 0) continue;

      // Simple syllable counting heuristic
      const vowelGroups = cleanWord.match(/[aeiouy]+/g);
      syllableCount += vowelGroups ? vowelGroups.length : 1;
    }

    return syllableCount;
  }

  private identifyStrengths(text: string): string[] {
    const strengths: string[] = [];

    if (text.length > 100) {
      strengths.push('Comprehensive task description');
    }

    if (/[•\-\*]\s|\d+\./.test(text)) {
      strengths.push('Well-structured with lists');
    }

    if (text.includes('example') || text.includes('for instance')) {
      strengths.push('Includes helpful examples');
    }

    if (text.includes('format') || text.includes('structure')) {
      strengths.push('Specifies output format');
    }

    return strengths;
  }
}

class QualityAssuranceService {
  private calculators: Map<QualityMetricType, QualityCalculator>;

  constructor() {
    this.calculators = new Map([
      [QualityMetricType.COMPLETENESS, new CompletenessCalculator()],
      [QualityMetricType.CLARITY, new ClarityCalculator()],
      [QualityMetricType.DIFFICULTY_ACCURACY, new DifficultyAccuracyCalculator()],
      [QualityMetricType.UNIQUENESS, new UniquenessCalculator()],
      [QualityMetricType.FEASIBILITY, new FeasibilityCalculator()],
    ]);
  }

  async assessTaskQuality(task: Task): Promise<QualityAssessment> {
    const assessments: QualityScore[] = [];
    const validations: ValidationResult[] = [];

    for (const [type, calculator] of this.calculators) {
      try {
        const score = await calculator.calculate(task);
        const validation = await calculator.validate(task);

        assessments.push(score);
        validations.push(validation);
      } catch (error) {
        console.error(`Error calculating ${type} quality:`, error);
      }
    }

    const overallScore = this.calculateOverallScore(assessments);
    const overallValidation = this.combineValidations(validations);

    return {
      taskId: task.id,
      overallScore,
      overallValidation,
      detailedScores: assessments,
      assessedAt: new Date(),
      recommendations: this.generateRecommendations(assessments, overallValidation),
    };
  }

  private calculateOverallScore(assessments: QualityScore[]): number {
    if (assessments.length === 0) return 0;

    const totalScore = assessments.reduce((sum, assessment) => sum + assessment.value, 0);
    return Math.round(totalScore / assessments.length);
  }

  private combineValidations(validations: ValidationResult[]): ValidationResult {
    const allErrors = validations.flatMap((v) => v.errors);
    const allWarnings = validations.flatMap((v) => v.warnings);

    return {
      isValid: allErrors.length === 0,
      errors: allErrors,
      warnings: allWarnings,
    };
  }

  private generateRecommendations(
    assessments: QualityScore[],
    validation: ValidationResult
  ): string[] {
    const recommendations: string[] = [];

    // Add recommendations from quality assessments
    for (const assessment of assessments) {
      recommendations.push(...assessment.recommendations);
    }

    // Add recommendations from validation
    for (const error of validation.errors) {
      recommendations.push(`Fix error: ${error.message}`);
    }

    for (const warning of validation.warnings) {
      recommendations.push(`Consider: ${warning.message}`);
    }

    // Remove duplicates and limit to top 10
    return [...new Set(recommendations)].slice(0, 10);
  }
}

interface QualityAssessment {
  taskId: string;
  overallScore: number;
  overallValidation: ValidationResult;
  detailedScores: QualityScore[];
  assessedAt: Date;
  recommendations: string[];
}
```

#### 3. Contamination Prevention System (Story 3.3)

**Contamination Detection Framework:**

```typescript
interface ContaminationAnalyzer {
  analyze(task: Task): Promise<ContaminationAnalysis>;
  getSimilarTasks(task: Task, threshold: number): Promise<SimilarTask[]>;
  checkTrainingDataOverlap(task: Task): Promise<TrainingDataOverlap>;
}

interface ContaminationAnalysis {
  taskId: string;
  overallRisk: ContaminationRisk;
  similarityAnalysis: SimilarityAnalysis;
  trainingDataAnalysis: TrainingDataAnalysis;
  temporalAnalysis: TemporalAnalysis;
  recommendations: string[];
  analyzedAt: Date;
}

enum ContaminationRisk {
  LOW = 'low',
  MEDIUM = 'medium',
  HIGH = 'high',
  CRITICAL = 'critical',
}

interface SimilarityAnalysis {
  overallSimilarity: number;
  similarTasks: SimilarTask[];
  duplicateClusters: DuplicateCluster[];
  potentialPlagiarism: PlagiarismIndicator[];
}

interface SimilarTask {
  taskId: string;
  similarity: number;
  similarityType: SimilarityType;
  overlappingContent: string[];
  metadata: {
    category: string;
    difficulty: string;
    createdBy: string;
    createdAt: Date;
  };
}

enum SimilarityType {
  EXACT_MATCH = 'exact_match',
  HIGH_SIMILARITY = 'high_similarity',
  MODERATE_SIMILARITY = 'moderate_similarity',
  SEMANTIC_SIMILARITY = 'semantic_similarity',
}

interface DuplicateCluster {
  clusterId: string;
  tasks: string[];
  averageSimilarity: number;
  representativeTask: string;
}

interface PlagiarismIndicator {
  sourceTaskId: string;
  confidence: number;
  matchingSegments: TextSegment[];
}

interface TextSegment {
  text: string;
  startPosition: number;
  endPosition: number;
  similarity: number;
}

interface TrainingDataAnalysis {
  knownDatasets: DatasetOverlap[];
  potentialLeaks: PotentialLeak[];
  riskScore: number;
}

interface DatasetOverlap {
  datasetName: string;
  overlapType: OverlapType;
  confidence: number;
  matchingContent: string[];
}

enum OverlapType {
  EXACT_MATCH = 'exact_match',
  PARAPHRASE = 'paraphrase',
  CONCEPT_SIMILARITY = 'concept_similarity',
}

interface PotentialLeak {
  source: string;
  leakType: LeakType;
  confidence: number;
  evidence: string[];
}

enum LeakType {
  TRAINING_DATA = 'training_data',
  BENCHMARK_DATA = 'benchmark_data',
  EVALUATION_SET = 'evaluation_set',
}

interface TemporalAnalysis {
  taskCreationDate: Date;
  modelTrainingCutoff?: Date;
  temporalRisk: TemporalRisk;
  recommendations: string[];
}

enum TemporalRisk {
  SAFE = 'safe',
  CAUTION = 'caution',
  RISKY = 'risky',
}
```

**Contamination Detection Implementation:**

```typescript
class ContaminationDetectionService implements ContaminationAnalyzer {
  constructor(
    private taskRepository: TaskRepository,
    private textSimilarity: TextSimilarityService,
    private semanticSearch: SemanticSearchService,
    private trainingDataDB: TrainingDataDatabase,
    private vectorStore: VectorStore
  ) {}

  async analyze(task: Task): Promise<ContaminationAnalysis> {
    const [similarityAnalysis, trainingDataAnalysis, temporalAnalysis] = await Promise.all([
      this.analyzeSimilarity(task),
      this.analyzeTrainingData(task),
      this.analyzeTemporal(task),
    ]);

    const overallRisk = this.calculateOverallRisk(
      similarityAnalysis,
      trainingDataAnalysis,
      temporalAnalysis
    );

    const recommendations = this.generateRecommendations(
      overallRisk,
      similarityAnalysis,
      trainingDataAnalysis,
      temporalAnalysis
    );

    return {
      taskId: task.id,
      overallRisk,
      similarityAnalysis,
      trainingDataAnalysis,
      temporalAnalysis,
      recommendations,
      analyzedAt: new Date(),
    };
  }

  async getSimilarTasks(task: Task, threshold: number): Promise<SimilarTask[]> {
    // Get all existing tasks
    const allTasks = await this.taskRepository.searchTasks({
      limit: 10000, // Get all tasks for comparison
    });

    const similarTasks: SimilarTask[] = [];

    for (const existingTask of allTasks.tasks) {
      if (existingTask.id === task.id) continue;

      const similarity = await this.calculateTaskSimilarity(task, existingTask);

      if (similarity.overall >= threshold) {
        similarTasks.push({
          taskId: existingTask.id,
          similarity: similarity.overall,
          similarityType: similarity.type,
          overlappingContent: similarity.overlappingContent,
          metadata: {
            category: existingTask.category,
            difficulty: existingTask.difficultyLevel,
            createdBy: existingTask.createdBy,
            createdAt: existingTask.createdAt,
          },
        });
      }
    }

    return similarTasks.sort((a, b) => b.similarity - a.similarity);
  }

  async checkTrainingDataOverlap(task: Task): Promise<TrainingDataOverlap> {
    const overlaps: DatasetOverlap[] = [];

    // Check against known training datasets
    const knownDatasets = await this.trainingDataDB.getKnownDatasets();

    for (const dataset of knownDatasets) {
      const overlap = await this.checkDatasetOverlap(task, dataset);
      if (overlap.overlapType !== OverlapType.CONCEPT_SIMILARITY || overlap.confidence > 0.8) {
        overlaps.push(overlap);
      }
    }

    // Check for potential leaks
    const potentialLeaks = await this.detectPotentialLeaks(task);

    const riskScore = this.calculateTrainingDataRisk(overlaps, potentialLeaks);

    return {
      knownDatasets: overlaps,
      potentialLeaks,
      riskScore,
    };
  }

  private async analyzeSimilarity(task: Task): Promise<SimilarityAnalysis> {
    const similarTasks = await this.getSimilarTasks(task, 0.3);
    const duplicateClusters = this.identifyDuplicateClusters(similarTasks);
    const potentialPlagiarism = this.detectPlagiarism(task, similarTasks);

    const overallSimilarity = this.calculateOverallSimilarity(similarTasks);

    return {
      overallSimilarity,
      similarTasks,
      duplicateClusters,
      potentialPlagiarism,
    };
  }

  private async analyzeTrainingData(task: Task): Promise<TrainingDataAnalysis> {
    const overlap = await this.checkTrainingDataOverlap(task);

    return {
      knownDatasets: overlap.knownDatasets,
      potentialLeaks: overlap.potentialLeaks,
      riskScore: overlap.riskScore,
    };
  }

  private async analyzeTemporal(task: Task): Promise<TemporalAnalysis> {
    const taskCreationDate = task.createdAt;
    const modelTrainingCutoff = await this.getModelTrainingCutoff();

    let temporalRisk: TemporalRisk;
    const recommendations: string[] = [];

    if (!modelTrainingCutoff) {
      temporalRisk = TemporalRisk.CAUTION;
      recommendations.push('Model training cutoff date unknown - verify with model provider');
    } else if (taskCreationDate <= modelTrainingCutoff) {
      temporalRisk = TemporalRisk.RISKY;
      recommendations.push('Task created before model training cutoff - high contamination risk');
    } else {
      const daysDifference = this.daysBetween(taskCreationDate, modelTrainingCutoff);
      if (daysDifference < 30) {
        temporalRisk = TemporalRisk.CAUTION;
        recommendations.push('Task created shortly after training cutoff - moderate risk');
      } else {
        temporalRisk = TemporalRisk.SAFE;
        recommendations.push('Task created well after training cutoff - low temporal risk');
      }
    }

    return {
      taskCreationDate,
      modelTrainingCutoff,
      temporalRisk,
      recommendations,
    };
  }

  private async calculateTaskSimilarity(task1: Task, task2: Task): Promise<TaskSimilarity> {
    const text1 = task1.content.prompt;
    const text2 = task2.content.prompt;

    // Text similarity
    const textSimilarity = await this.textSimilarity.calculate(text1, text2);

    // Semantic similarity
    const semanticSimilarity = await this.semanticSearch.calculateSimilarity(text1, text2);

    // Structure similarity
    const structureSimilarity = this.calculateStructureSimilarity(task1, task2);

    // Overall similarity (weighted average)
    const overall = textSimilarity * 0.4 + semanticSimilarity * 0.4 + structureSimilarity * 0.2;

    let similarityType: SimilarityType;
    if (overall >= 0.9) {
      similarityType = SimilarityType.EXACT_MATCH;
    } else if (overall >= 0.7) {
      similarityType = SimilarityType.HIGH_SIMILARITY;
    } else if (overall >= 0.5) {
      similarityType = SimilarityType.MODERATE_SIMILARITY;
    } else {
      similarityType = SimilarityType.SEMANTIC_SIMILARITY;
    }

    const overlappingContent = this.findOverlappingContent(text1, text2);

    return {
      overall,
      type: similarityType,
      overlappingContent,
    };
  }

  private calculateStructureSimilarity(task1: Task, task2: Task): number {
    let similarity = 0;
    let factors = 0;

    // Category similarity
    if (task1.category === task2.category) {
      similarity += 1;
    }
    factors++;

    // Task type similarity
    if (task1.taskType === task2.taskType) {
      similarity += 1;
    }
    factors++;

    // Difficulty similarity
    if (task1.difficultyLevel === task2.difficultyLevel) {
      similarity += 1;
    }
    factors++;

    // Tags similarity
    const tags1 = new Set(task1.tags);
    const tags2 = new Set(task2.tags);
    const intersection = new Set([...tags1].filter((tag) => tags2.has(tag)));
    const union = new Set([...tags1, ...tags2]);
    const tagSimilarity = intersection.size / union.size;

    similarity += tagSimilarity;
    factors++;

    return similarity / factors;
  }

  private findOverlappingContent(text1: string, text2: string): string[] {
    const overlapping: string[] = [];

    // Find common sentences
    const sentences1 = text1
      .split(/[.!?]+/)
      .map((s) => s.trim())
      .filter((s) => s.length > 10);
    const sentences2 = text2
      .split(/[.!?]+/)
      .map((s) => s.trim())
      .filter((s) => s.length > 10);

    for (const sentence1 of sentences1) {
      for (const sentence2 of sentences2) {
        const similarity = this.textSimilarity.calculate(sentence1, sentence2);
        if (similarity > 0.8) {
          overlapping.push(sentence1);
          break;
        }
      }
    }

    return overlapping;
  }

  private identifyDuplicateClusters(similarTasks: SimilarTask[]): DuplicateCluster[] {
    const clusters: DuplicateCluster[] = [];
    const processed = new Set<string>();

    for (const task of similarTasks) {
      if (processed.has(task.taskId)) continue;

      // Find all tasks similar to this one
      const cluster = [task.taskId];
      processed.add(task.taskId);

      for (const otherTask of similarTasks) {
        if (processed.has(otherTask.taskId)) continue;

        if (task.similarity > 0.8 && otherTask.similarity > 0.8) {
          cluster.push(otherTask.taskId);
          processed.add(otherTask.taskId);
        }
      }

      if (cluster.length > 1) {
        const averageSimilarity =
          cluster.reduce((sum, taskId) => {
            const similarTask = similarTasks.find((t) => t.taskId === taskId);
            return sum + (similarTask?.similarity || 0);
          }, 0) / cluster.length;

        clusters.push({
          clusterId: `cluster_${clusters.length + 1}`,
          tasks: cluster,
          averageSimilarity,
          representativeTask: cluster[0],
        });
      }
    }

    return clusters;
  }

  private detectPlagiarism(task: Task, similarTasks: SimilarTask[]): PlagiarismIndicator[] {
    const plagiarism: PlagiarismIndicator[] = [];

    for (const similarTask of similarTasks) {
      if (similarTask.similarity > 0.85) {
        const matchingSegments = await this.findMatchingSegments(
          task.content.prompt,
          similarTask.taskId
        );

        plagiarism.push({
          sourceTaskId: similarTask.taskId,
          confidence: similarTask.similarity,
          matchingSegments,
        });
      }
    }

    return plagiarism;
  }

  private async findMatchingSegments(text: string, sourceTaskId: string): Promise<TextSegment[]> {
    const sourceTask = await this.taskRepository.getTask(sourceTaskId);
    if (!sourceTask) return [];

    const sourceText = sourceTask.content.prompt;
    const segments: TextSegment[] = [];

    // Simple sliding window approach for finding matching segments
    const windowSize = 20; // words
    const textWords = text.split(/\s+/);
    const sourceWords = sourceText.split(/\s+/);

    for (let i = 0; i <= textWords.length - windowSize; i++) {
      const window = textWords.slice(i, i + windowSize).join(' ');

      for (let j = 0; j <= sourceWords.length - windowSize; j++) {
        const sourceWindow = sourceWords.slice(j, j + windowSize).join(' ');

        const similarity = await this.textSimilarity.calculate(window, sourceWindow);
        if (similarity > 0.9) {
          segments.push({
            text: window,
            startPosition: i,
            endPosition: i + windowSize,
            similarity,
          });
          break; // Move to next window in text
        }
      }
    }

    return segments;
  }

  private calculateOverallRisk(
    similarityAnalysis: SimilarityAnalysis,
    trainingDataAnalysis: TrainingDataAnalysis,
    temporalAnalysis: TemporalAnalysis
  ): ContaminationRisk {
    let riskScore = 0;

    // Similarity risk (0-40 points)
    if (similarityAnalysis.overallSimilarity > 0.8) {
      riskScore += 40;
    } else if (similarityAnalysis.overallSimilarity > 0.6) {
      riskScore += 25;
    } else if (similarityAnalysis.overallSimilarity > 0.4) {
      riskScore += 10;
    }

    // Training data risk (0-40 points)
    riskScore += trainingDataAnalysis.riskScore * 0.4;

    // Temporal risk (0-20 points)
    if (temporalAnalysis.temporalRisk === TemporalRisk.RISKY) {
      riskScore += 20;
    } else if (temporalAnalysis.temporalRisk === TemporalRisk.CAUTION) {
      riskScore += 10;
    }

    if (riskScore >= 80) {
      return ContaminationRisk.CRITICAL;
    } else if (riskScore >= 60) {
      return ContaminationRisk.HIGH;
    } else if (riskScore >= 40) {
      return ContaminationRisk.MEDIUM;
    } else {
      return ContaminationRisk.LOW;
    }
  }

  private generateRecommendations(
    overallRisk: ContaminationRisk,
    similarityAnalysis: SimilarityAnalysis,
    trainingDataAnalysis: TrainingDataAnalysis,
    temporalAnalysis: TemporalAnalysis
  ): string[] {
    const recommendations: string[] = [];

    if (overallRisk === ContaminationRisk.CRITICAL) {
      recommendations.push('CRITICAL: Do not use this task without significant revision');
    }

    if (similarityAnalysis.overallSimilarity > 0.7) {
      recommendations.push('High similarity detected - consider modifying or removing this task');
    }

    if (trainingDataAnalysis.riskScore > 50) {
      recommendations.push('Potential training data overlap - verify with model providers');
    }

    if (temporalAnalysis.temporalRisk === TemporalRisk.RISKY) {
      recommendations.push('Task created before model training cutoff - high contamination risk');
    }

    recommendations.push(...temporalAnalysis.recommendations);

    return recommendations;
  }

  private daysBetween(date1: Date, date2: Date): number {
    const oneDay = 24 * 60 * 60 * 1000;
    return Math.round(Math.abs((date1.getTime() - date2.getTime()) / oneDay));
  }

  private async getModelTrainingCutoff(): Promise<Date | undefined> {
    // This would typically come from model metadata or provider information
    // For now, return a placeholder
    return undefined;
  }

  private calculateTrainingDataRisk(
    overlaps: DatasetOverlap[],
    potentialLeaks: PotentialLeak[]
  ): number {
    let riskScore = 0;

    for (const overlap of overlaps) {
      if (overlap.overlapType === OverlapType.EXACT_MATCH) {
        riskScore += 30;
      } else if (overlap.overlapType === OverlapType.PARAPHRASE) {
        riskScore += 20;
      } else if (overlap.confidence > 0.8) {
        riskScore += 10;
      }
    }

    for (const leak of potentialLeaks) {
      if (leak.confidence > 0.8) {
        riskScore += 25;
      } else if (leak.confidence > 0.6) {
        riskScore += 15;
      }
    }

    return Math.min(100, riskScore);
  }

  private async checkDatasetOverlap(task: Task, dataset: Dataset): Promise<DatasetOverlap> {
    const taskText = task.content.prompt;
    const overlap = await this.textSimilarity.findOverlap(taskText, dataset.content);

    return {
      datasetName: dataset.name,
      overlapType: overlap.type,
      confidence: overlap.confidence,
      matchingContent: overlap.segments,
    };
  }

  private async detectPotentialLeaks(task: Task): Promise<PotentialLeak[]> {
    const leaks: PotentialLeak[] = [];

    // Check for common training data patterns
    const taskText = task.content.prompt.toLowerCase();

    // Common benchmark patterns
    if (taskText.includes('mmlu') || taskText.includes('hellaswag')) {
      leaks.push({
        source: 'Common Benchmark Dataset',
        leakType: LeakType.BENCHMARK_DATA,
        confidence: 0.7,
        evidence: ['Contains references to known benchmark datasets'],
      });
    }

    // Code repository patterns
    if (taskText.includes('github.com') || taskText.includes('stackoverflow')) {
      leaks.push({
        source: 'Public Code Repository',
        leakType: LeakType.TRAINING_DATA,
        confidence: 0.6,
        evidence: ['Contains references to public code repositories'],
      });
    }

    return leaks;
  }
}

interface TaskSimilarity {
  overall: number;
  type: SimilarityType;
  overlappingContent: string[];
}

interface Dataset {
  name: string;
  content: string;
  type: string;
}
```

#### 4. Initial Test Bank Creation (Story 3.4)

**Test Bank Curation Framework:**

```typescript
interface TestBankCurator {
  createInitialTestBank(): Promise<TestBank>;
  importTasksFromSource(source: TaskSource): Promise<ImportResult>;
  validateTestBank(testBank: TestBank): Promise<ValidationReport>;
  exportTestBank(testBank: TestBank, format: ExportFormat): Promise<ExportResult>;
}

interface TestBank {
  id: string;
  name: string;
  description: string;
  version: string;
  tasks: Task[];
  metadata: TestBankMetadata;
  statistics: TestBankStatistics;
  createdAt: Date;
  updatedAt: Date;
}

interface TestBankMetadata {
  domains: string[];
  languages: string[];
  difficultyDistribution: Record<DifficultyLevel, number>;
  typeDistribution: Record<TaskType, number>;
  totalEstimatedTime: number; // minutes
  averageQualityScore: number;
  contaminationRisk: ContaminationRisk;
}

interface TestBankStatistics {
  totalTasks: number;
  tasksByCategory: Record<string, number>;
  tasksByDifficulty: Record<DifficultyLevel, number>;
  tasksByType: Record<TaskType, number>;
  averageTokens: {
    prompt: number;
    expected: number;
  };
  qualityDistribution: {
    excellent: number; // 90-100
    good: number; // 70-89
    fair: number; // 50-69
    poor: number; // <50
  };
}

interface TaskSource {
  type: SourceType;
  location: string;
  format: SourceFormat;
  metadata: Record<string, any>;
}

enum SourceType {
  FILE = 'file',
  URL = 'url',
  DATABASE = 'database',
  API = 'api',
}

enum SourceFormat {
  JSON = 'json',
  CSV = 'csv',
  XML = 'xml',
  YAML = 'yaml',
  CUSTOM = 'custom',
}

interface ImportResult {
  success: boolean;
  tasksImported: number;
  tasksSkipped: number;
  errors: ImportError[];
  warnings: ImportWarning[];
  importedTasks: Task[];
}

interface ImportError {
  line?: number;
  field?: string;
  message: string;
  severity: 'error' | 'warning';
}

interface ImportWarning {
  line?: number;
  field?: string;
  message: string;
  suggestion?: string;
}

interface ValidationReport {
  isValid: boolean;
  errors: ValidationError[];
  warnings: ValidationWarning[];
  statistics: ValidationStatistics;
}

interface ValidationStatistics {
  totalTasks: number;
  validTasks: number;
  invalidTasks: number;
  tasksWithWarnings: number;
  averageQualityScore: number;
  contaminationRiskDistribution: Record<ContaminationRisk, number>;
}

class InitialTestBankCreator implements TestBankCurator {
  constructor(
    private taskRepository: TaskRepository,
    private qualityService: QualityAssuranceService,
    private contaminationService: ContaminationDetectionService,
    private categoryService: CategoryService
  ) {}

  async createInitialTestBank(): Promise<TestBank> {
    const initialTasks = await this.generateInitialTasks();

    // Process each task
    const processedTasks: Task[] = [];

    for (const taskData of initialTasks) {
      try {
        // Create task
        const task = await this.taskRepository.createTask(taskData);

        // Assess quality
        const qualityAssessment = await this.qualityService.assessTaskQuality(task);

        // Check contamination
        const contaminationAnalysis = await this.contaminationService.analyze(task);

        // Update task with assessment results
        const updatedTask = await this.taskRepository.updateTask(task.id, {
          qualityScore: qualityAssessment.overallScore,
          contaminationScore: this.calculateContaminationScore(contaminationAnalysis),
          status: this.determineTaskStatus(qualityAssessment, contaminationAnalysis),
        });

        processedTasks.push(updatedTask);
      } catch (error) {
        console.error(`Error processing task ${taskData.name}:`, error);
      }
    }

    // Create test bank
    const testBank = await this.createTestBank(processedTasks);

    return testBank;
  }

  private async generateInitialTasks(): Promise<CreateTaskRequest[]> {
    const tasks: CreateTaskRequest[] = [];

    // Coding tasks
    tasks.push(...this.generateCodingTasks());

    // Reasoning tasks
    tasks.push(...this.generateReasoningTasks());

    // Creative tasks
    tasks.push(...this.generateCreativeTasks());

    // Analysis tasks
    tasks.push(...this.generateAnalysisTasks());

    // Comprehension tasks
    tasks.push(...this.generateComprehensionTasks());

    return tasks;
  }

  private generateCodingTasks(): CreateTaskRequest[] {
    return [
      {
        name: 'Binary Search Implementation',
        description: 'Implement a binary search algorithm',
        category: 'algorithms',
        subcategory: 'searching',
        difficultyLevel: DifficultyLevel.INTERMEDIATE,
        taskType: TaskType.CODING,
        content: {
          prompt: `Write a function that implements binary search algorithm. The function should take a sorted array of integers and a target value, and return the index of the target if found, or -1 if not found.

Requirements:
- Function signature: binary_search(arr: List[int], target: int) -> int
- Time complexity: O(log n)
- Handle edge cases (empty array, duplicate elements)
- Include proper error handling
- Add unit tests

Example:
Input: [1, 3, 5, 7, 9], target = 5
Output: 2

Input: [1, 3, 5, 7, 9], target = 4
Output: -1`,
          constraints: [
            'Must use binary search algorithm',
            'Time complexity must be O(log n)',
            'Include comprehensive error handling',
          ],
          examples: [
            {
              input: { arr: [1, 3, 5, 7, 9], target: 5 },
              expectedOutput: 2,
              explanation: 'Target 5 found at index 2',
            },
            {
              input: { arr: [1, 3, 5, 7, 9], target: 4 },
              expectedOutput: -1,
              explanation: 'Target 4 not found in array',
            },
          ],
          evaluationCriteria: [
            {
              name: 'Correctness',
              description: 'Algorithm correctly finds target or returns -1',
              weight: 0.4,
              evaluationMethod: 'automatic',
            },
            {
              name: 'Time Complexity',
              description: 'Implementation meets O(log n) complexity',
              weight: 0.3,
              evaluationMethod: 'automatic',
            },
            {
              name: 'Code Quality',
              description: 'Clean, readable code with proper documentation',
              weight: 0.2,
              evaluationMethod: 'manual',
            },
            {
              name: 'Test Coverage',
              description: 'Comprehensive unit tests included',
              weight: 0.1,
              evaluationMethod: 'automatic',
            },
          ],
        },
        expectedOutput: {
          type: 'code',
          format: 'python',
          constraints: [
            { type: 'function', rule: 'binary_search', required: true },
            { type: 'complexity', rule: 'O(log n)', required: true },
          ],
        },
        metadata: {
          estimatedTime: 30,
          requiredSkills: ['algorithms', 'python', 'testing'],
          prerequisites: ['basic programming', 'arrays'],
          learningObjectives: ['binary search', 'divide and conquer', 'algorithm analysis'],
          domain: 'computer_science',
          language: 'python',
          complexity: 5,
          tokens: {
            prompt: 250,
            expected: 150,
          },
        },
        tags: ['algorithms', 'searching', 'binary-search', 'python'],
        createdBy: 'system',
      },
      {
        name: 'Linked List Reversal',
        description: 'Reverse a singly linked list',
        category: 'data_structures',
        subcategory: 'linked_lists',
        difficultyLevel: DifficultyLevel.INTERMEDIATE,
        taskType: TaskType.CODING,
        content: {
          prompt: `Implement a function to reverse a singly linked list. The function should take the head of a linked list and return the new head after reversal.

Requirements:
- Function signature: reverse_linked_list(head: ListNode) -> ListNode
- Space complexity: O(1)
- Handle edge cases (empty list, single node)
- Include proper error handling
- Add unit tests

Example:
Input: 1 -> 2 -> 3 -> 4 -> 5 -> None
Output: 5 -> 4 -> 3 -> 2 -> 1 -> None

ListNode class definition:
class ListNode:
    def __init__(self, val=0, next=None):
        self.val = val
        self.next = next`,
          constraints: [
            'Must use iterative approach',
            'Space complexity must be O(1)',
            'Handle all edge cases properly',
          ],
          examples: [
            {
              input: { head: '1->2->3->4->5' },
              expectedOutput: '5->4->3->2->1',
              explanation: 'List reversed successfully',
            },
          ],
          evaluationCriteria: [
            {
              name: 'Correctness',
              description: 'List correctly reversed',
              weight: 0.4,
              evaluationMethod: 'automatic',
            },
            {
              name: 'Space Complexity',
              description: 'O(1) space complexity achieved',
              weight: 0.3,
              evaluationMethod: 'automatic',
            },
            {
              name: 'Edge Case Handling',
              description: 'Proper handling of empty and single-node lists',
              weight: 0.2,
              evaluationMethod: 'automatic',
            },
            {
              name: 'Code Quality',
              description: 'Clean, well-documented code',
              weight: 0.1,
              evaluationMethod: 'manual',
            },
          ],
        },
        expectedOutput: {
          type: 'code',
          format: 'python',
          constraints: [
            { type: 'function', rule: 'reverse_linked_list', required: true },
            { type: 'complexity', rule: 'O(1) space', required: true },
          ],
        },
        metadata: {
          estimatedTime: 25,
          requiredSkills: ['linked_lists', 'pointers', 'algorithms'],
          prerequisites: ['basic data structures', 'pointers'],
          learningObjectives: [
            'linked list manipulation',
            'in-place algorithms',
            'pointer operations',
          ],
          domain: 'computer_science',
          language: 'python',
          complexity: 5,
          tokens: {
            prompt: 280,
            expected: 120,
          },
        },
        tags: ['linked-lists', 'algorithms', 'pointers', 'data-structures'],
        createdBy: 'system',
      },
    ];
  }

  private generateReasoningTasks(): CreateTaskRequest[] {
    return [
      {
        name: 'Logical Deduction Puzzle',
        description: 'Solve a logical deduction puzzle',
        category: 'reasoning',
        subcategory: 'logical_deduction',
        difficultyLevel: DifficultyLevel.ADVANCED,
        taskType: TaskType.REASONING,
        content: {
          prompt: `Five friends (Alice, Bob, Carol, David, Eve) each have a different favorite color (red, blue, green, yellow, purple) and a different favorite animal (dog, cat, bird, fish, rabbit). Use the following clues to determine who likes which color and animal:

Clues:
1. Alice's favorite color is not red or blue
2. The person who likes dogs also likes blue
3. Bob's favorite animal is not a dog or cat
4. The person who likes green also likes birds
5. Carol's favorite color is yellow
6. David does not like fish or rabbits
7. The person who likes purple also likes cats
8. Eve's favorite animal is a bird
9. The person who likes red does not like fish
10. Bob's favorite color is not green or yellow

Provide your answer as a table showing each person, their favorite color, and their favorite animal. Also explain your reasoning step by step.`,
          constraints: [
            'Must use logical deduction',
            'Must explain reasoning process',
            'Must provide final answer in table format',
          ],
          evaluationCriteria: [
            {
              name: 'Correct Answer',
              description: 'Correct matching of people, colors, and animals',
              weight: 0.5,
              evaluationMethod: 'automatic',
            },
            {
              name: 'Logical Reasoning',
              description: 'Clear, step-by-step logical deduction',
              weight: 0.3,
              evaluationMethod: 'manual',
            },
            {
              name: 'Completeness',
              description: 'All constraints addressed and explained',
              weight: 0.2,
              evaluationMethod: 'manual',
            },
          ],
        },
        expectedOutput: {
          type: 'json',
          format: 'structured answer with reasoning',
          constraints: [
            { type: 'content', rule: 'table format', required: true },
            { type: 'content', rule: 'step-by-step reasoning', required: true },
          ],
        },
        metadata: {
          estimatedTime: 20,
          requiredSkills: ['logical_reasoning', 'deduction', 'problem_solving'],
          prerequisites: ['basic logic'],
          learningObjectives: [
            'logical deduction',
            'constraint satisfaction',
            'systematic reasoning',
          ],
          domain: 'logic',
          language: 'english',
          complexity: 7,
          tokens: {
            prompt: 300,
            expected: 400,
          },
        },
        tags: ['logic', 'deduction', 'reasoning', 'puzzle'],
        createdBy: 'system',
      },
    ];
  }

  private generateCreativeTasks(): CreateTaskRequest[] {
    return [
      {
        name: 'Creative Story Writing',
        description: 'Write a short story with specific constraints',
        category: 'creative',
        subcategory: 'storytelling',
        difficultyLevel: DifficultyLevel.INTERMEDIATE,
        taskType: TaskType.CREATIVE,
        content: {
          prompt: `Write a short story (500-800 words) that incorporates all of the following elements:

Required Elements:
1. A mysterious antique shop
2. A time-traveling pocket watch
3. A character who can speak to animals
4. A hidden message written in invisible ink
5. An unexpected plot twist

Story Requirements:
- Engaging narrative with clear beginning, middle, and end
- Well-developed main character with motivations
- Vivid descriptions and sensory details
- Natural dialogue that reveals character
- Satisfying resolution that ties all elements together

Additional Guidelines:
- Avoid clichés and predictable plot developments
- Show, don't tell - use actions and descriptions rather than exposition
- Maintain consistent tone and style throughout
- Ensure the time-travel element has clear rules and limitations`,
          constraints: [
            'Word count: 500-800 words',
            'Must include all 5 required elements',
            'Must have clear plot structure',
            'Must be original and creative',
          ],
          evaluationCriteria: [
            {
              name: 'Creativity',
              description: 'Original ideas and unique storytelling approach',
              weight: 0.3,
              evaluationMethod: 'manual',
            },
            {
              name: 'Story Structure',
              description: 'Well-organized plot with proper pacing',
              weight: 0.25,
              evaluationMethod: 'manual',
            },
            {
              name: 'Character Development',
              description: 'Believable, well-developed characters',
              weight: 0.2,
              evaluationMethod: 'manual',
            },
            {
              name: 'Writing Quality',
              description: 'Clear, engaging prose with good mechanics',
              weight: 0.15,
              evaluationMethod: 'manual',
            },
            {
              name: 'Element Integration',
              description: 'All required elements naturally integrated',
              weight: 0.1,
              evaluationMethod: 'automatic',
            },
          ],
        },
        expectedOutput: {
          type: 'text',
          format: 'short story',
          constraints: [
            { type: 'length', rule: '500-800 words', required: true },
            { type: 'content', rule: 'all 5 elements included', required: true },
          ],
        },
        metadata: {
          estimatedTime: 45,
          requiredSkills: ['creative_writing', 'storytelling', 'imagination'],
          prerequisites: ['basic writing skills'],
          learningObjectives: [
            'creative writing',
            'plot development',
            'character creation',
            'descriptive writing',
          ],
          domain: 'creative_arts',
          language: 'english',
          complexity: 6,
          tokens: {
            prompt: 350,
            expected: 600,
          },
        },
        tags: ['creative', 'storytelling', 'writing', 'imagination'],
        createdBy: 'system',
      },
    ];
  }

  private generateAnalysisTasks(): CreateTaskRequest[] {
    return [
      {
        name: 'Data Analysis and Interpretation',
        description: 'Analyze a dataset and provide insights',
        category: 'analysis',
        subcategory: 'data_analysis',
        difficultyLevel: DifficultyLevel.ADVANCED,
        taskType: TaskType.ANALYSIS,
        content: {
          prompt: `You are given the following dataset showing monthly sales data for a retail store:

Dataset:
Month,Product_Category,Sales_Units,Revenue,Customer_Count
January,Electronics,450,225000,320
January,Clothing,780,156000,650
January,Home,320,96000,280
February,Electronics,520,260000,380
February,Clothing,920,184000,780
February,Home,380,114000,320
March,Electronics,480,240000,350
March,Clothing,850,170000,720
March,Home,410,123000,340

Tasks:
1. Calculate the total revenue and total units sold for each month
2. Determine which product category has the highest average revenue per unit
3. Calculate the month-over-month growth rate for each category
4. Identify any seasonal patterns or trends
5. Provide recommendations for inventory management based on your analysis

Requirements:
- Show all calculations clearly
- Provide visualizations (describe what charts you would create)
- Explain your reasoning for each recommendation
- Consider both statistical significance and practical business implications`,
          constraints: [
            'Must show all calculations',
            'Must provide business recommendations',
            'Must identify trends and patterns',
          ],
          examples: [
            {
              input: { dataset: 'provided above' },
              expectedOutput: 'comprehensive analysis with calculations and recommendations',
              explanation: 'Complete data analysis with business insights',
            },
          ],
          evaluationCriteria: [
            {
              name: 'Calculation Accuracy',
              description: 'All calculations are correct',
              weight: 0.3,
              evaluationMethod: 'automatic',
            },
            {
              name: 'Analysis Depth',
              description: 'Thorough analysis with multiple insights',
              weight: 0.25,
              evaluationMethod: 'manual',
            },
            {
              name: 'Business Relevance',
              description: 'Practical, actionable recommendations',
              weight: 0.25,
              evaluationMethod: 'manual',
            },
            {
              name: 'Clarity',
              description: 'Clear presentation of findings',
              weight: 0.2,
              evaluationMethod: 'manual',
            },
          ],
        },
        expectedOutput: {
          type: 'json',
          format: 'structured analysis with calculations',
          constraints: [
            { type: 'content', rule: 'calculations shown', required: true },
            { type: 'content', rule: 'recommendations provided', required: true },
          ],
        },
        metadata: {
          estimatedTime: 35,
          requiredSkills: ['data_analysis', 'statistics', 'business_acumen'],
          prerequisites: ['basic statistics', 'excel/spreadsheet skills'],
          learningObjectives: [
            'data analysis',
            'business intelligence',
            'statistical calculations',
            'trend analysis',
          ],
          domain: 'business',
          language: 'english',
          complexity: 7,
          tokens: {
            prompt: 400,
            expected: 500,
          },
        },
        tags: ['data-analysis', 'statistics', 'business', 'excel'],
        createdBy: 'system',
      },
    ];
  }

  private generateComprehensionTasks(): CreateTaskRequest[] {
    return [
      {
        name: 'Technical Document Comprehension',
        description: 'Read and understand a technical document',
        category: 'comprehension',
        subcategory: 'technical_reading',
        difficultyLevel: DifficultyLevel.INTERMEDIATE,
        taskType: TaskType.COMPREHENSION,
        content: {
          prompt: `Read the following technical documentation about a REST API and answer the questions below:

API Documentation:
========================================
User Management API v2.1

Authentication:
All API requests must include an Authorization header with a Bearer token:
Authorization: Bearer <your_api_key>

Endpoints:

1. GET /api/v2/users
   Description: Retrieve a list of all users
   Parameters:
   - page (optional): Page number for pagination (default: 1)
   - limit (optional): Number of users per page (default: 20, max: 100)
   - status (optional): Filter by user status (active, inactive, suspended)
   Response: Array of user objects

2. POST /api/v2/users
   Description: Create a new user
   Request Body:
   {
     "email": "user@example.com",
     "firstName": "John",
     "lastName": "Doe",
     "role": "user"
   }
   Response: Created user object with ID

3. GET /api/v2/users/{id}
   Description: Retrieve a specific user by ID
   Response: User object

4. PUT /api/v2/users/{id}
   Description: Update user information
   Request Body: Same as POST (all fields optional)
   Response: Updated user object

5. DELETE /api/v2/users/{id}
   Description: Delete a user
   Response: 204 No Content

Rate Limiting:
- 100 requests per minute per API key
- 1000 requests per hour per API key

Error Responses:
- 400: Bad Request (invalid parameters)
- 401: Unauthorized (invalid or missing API key)
- 404: Not Found (user doesn't exist)
- 429: Too Many Requests (rate limit exceeded)
- 500: Internal Server Error
========================================

Questions:
1. How would you retrieve the first 10 active users?
2. What HTTP status code indicates rate limiting?
3. Explain the difference between GET /api/v2/users and GET /api/v2/users/{id}
4. How would you handle a 401 error response?
5. What is the maximum number of users you can retrieve in a single request?

Provide specific API calls and explain your reasoning for each answer.`,
          constraints: [
            'Must reference specific API documentation',
            'Must provide exact API calls where requested',
            'Must explain reasoning clearly',
          ],
          evaluationCriteria: [
            {
              name: 'Accuracy',
              description: 'Correct understanding of API documentation',
              weight: 0.4,
              evaluationMethod: 'automatic',
            },
            {
              name: 'Specificity',
              description: 'Provides specific, actionable answers',
              weight: 0.3,
              evaluationMethod: 'manual',
            },
            {
              name: 'Clarity',
              description: 'Clear, well-explained answers',
              weight: 0.2,
              evaluationMethod: 'manual',
            },
            {
              name: 'Completeness',
              description: 'All questions answered thoroughly',
              weight: 0.1,
              evaluationMethod: 'automatic',
            },
          ],
        },
        expectedOutput: {
          type: 'json',
          format: 'structured answers with explanations',
          constraints: [
            { type: 'content', rule: 'all 5 questions answered', required: true },
            { type: 'content', rule: 'specific API calls provided', required: true },
          ],
        },
        metadata: {
          estimatedTime: 25,
          requiredSkills: ['technical_reading', 'api_understanding', 'comprehension'],
          prerequisites: ['basic HTTP knowledge', 'REST API concepts'],
          learningObjectives: [
            'API documentation comprehension',
            'technical reading',
            'HTTP methods',
          ],
          domain: 'technology',
          language: 'english',
          complexity: 5,
          tokens: {
            prompt: 600,
            expected: 400,
          },
        },
        tags: ['comprehension', 'api', 'technical', 'documentation'],
        createdBy: 'system',
      },
    ];
  }

  private async createTestBank(tasks: Task[]): Promise<TestBank> {
    const statistics = this.calculateStatistics(tasks);
    const metadata = this.generateMetadata(tasks, statistics);

    return {
      id: `testbank_${Date.now()}`,
      name: 'Initial AI Benchmarking Test Bank',
      description: 'Comprehensive initial test bank covering multiple AI capabilities',
      version: '1.0.0',
      tasks,
      metadata,
      statistics,
      createdAt: new Date(),
      updatedAt: new Date(),
    };
  }

  private calculateStatistics(tasks: Task[]): TestBankStatistics {
    const stats: TestBankStatistics = {
      totalTasks: tasks.length,
      tasksByCategory: {},
      tasksByDifficulty: {} as Record<DifficultyLevel, number>,
      tasksByType: {} as Record<TaskType, number>,
      averageTokens: {
        prompt: 0,
        expected: 0,
      },
      qualityDistribution: {
        excellent: 0,
        good: 0,
        fair: 0,
        poor: 0,
      },
    };

    let totalPromptTokens = 0;
    let totalExpectedTokens = 0;

    for (const task of tasks) {
      // Category distribution
      stats.tasksByCategory[task.category] = (stats.tasksByCategory[task.category] || 0) + 1;

      // Difficulty distribution
      stats.tasksByDifficulty[task.difficultyLevel] =
        (stats.tasksByDifficulty[task.difficultyLevel] || 0) + 1;

      // Type distribution
      stats.tasksByType[task.taskType] = (stats.tasksByType[task.taskType] || 0) + 1;

      // Token counts
      if (task.metadata.tokens) {
        totalPromptTokens += task.metadata.tokens.prompt || 0;
        totalExpectedTokens += task.metadata.tokens.expected || 0;
      }

      // Quality distribution
      if (task.qualityScore) {
        if (task.qualityScore >= 90) {
          stats.qualityDistribution.excellent++;
        } else if (task.qualityScore >= 70) {
          stats.qualityDistribution.good++;
        } else if (task.qualityScore >= 50) {
          stats.qualityDistribution.fair++;
        } else {
          stats.qualityDistribution.poor++;
        }
      }
    }

    stats.averageTokens.prompt = Math.round(totalPromptTokens / tasks.length);
    stats.averageTokens.expected = Math.round(totalExpectedTokens / tasks.length);

    return stats;
  }

  private generateMetadata(tasks: Task[], statistics: TestBankStatistics): TestBankMetadata {
    const domains = [...new Set(tasks.map((t) => t.metadata.domain))];
    const languages = [...new Set(tasks.map((t) => t.metadata.language))];

    const totalEstimatedTime = tasks.reduce(
      (sum, task) => sum + (task.metadata.estimatedTime || 0),
      0
    );
    const averageQualityScore =
      tasks.reduce((sum, task) => sum + (task.qualityScore || 0), 0) / tasks.length;

    // Determine overall contamination risk
    const contaminationScores = tasks.map((t) => t.contaminationScore || 0);
    const avgContaminationScore =
      contaminationScores.reduce((a, b) => a + b, 0) / contaminationScores.length;

    let contaminationRisk: ContaminationRisk;
    if (avgContaminationScore >= 80) {
      contaminationRisk = ContaminationRisk.CRITICAL;
    } else if (avgContaminationScore >= 60) {
      contaminationRisk = ContaminationRisk.HIGH;
    } else if (avgContaminationScore >= 40) {
      contaminationRisk = ContaminationRisk.MEDIUM;
    } else {
      contaminationRisk = ContaminationRisk.LOW;
    }

    return {
      domains,
      languages,
      difficultyDistribution: statistics.tasksByDifficulty,
      typeDistribution: statistics.tasksByType,
      totalEstimatedTime,
      averageQualityScore: Math.round(averageQualityScore),
      contaminationRisk,
    };
  }

  private calculateContaminationScore(analysis: ContaminationAnalysis): number {
    let score = 0;

    // Similarity score (0-40 points)
    score += (1 - analysis.similarityAnalysis.overallSimilarity) * 40;

    // Training data score (0-40 points)
    score += (1 - analysis.trainingDataAnalysis.riskScore / 100) * 40;

    // Temporal score (0-20 points)
    if (analysis.temporalAnalysis.temporalRisk === TemporalRisk.SAFE) {
      score += 20;
    } else if (analysis.temporalAnalysis.temporalRisk === TemporalRisk.CAUTION) {
      score += 10;
    }

    return Math.round(score);
  }

  private determineTaskStatus(
    qualityAssessment: QualityAssessment,
    contaminationAnalysis: ContaminationAnalysis
  ): TaskStatus {
    if (qualityAssessment.overallValidation.errors.length > 0) {
      return TaskStatus.DRAFT;
    }

    if (contaminationAnalysis.overallRisk === ContaminationRisk.CRITICAL) {
      return TaskStatus.DRAFT;
    }

    if (
      qualityAssessment.overallScore < 50 ||
      contaminationAnalysis.overallRisk === ContaminationRisk.HIGH
    ) {
      return TaskStatus.REVIEW;
    }

    if (
      qualityAssessment.overallScore < 70 ||
      contaminationAnalysis.overallRisk === ContaminationRisk.MEDIUM
    ) {
      return TaskStatus.APPROVED;
    }

    return TaskStatus.PUBLISHED;
  }
}

interface CreateTaskRequest {
  name: string;
  description?: string;
  category: string;
  subcategory?: string;
  difficultyLevel: DifficultyLevel;
  taskType: TaskType;
  content: TaskContent;
  expectedOutput?: ExpectedOutput;
  metadata: TaskMetadata;
  tags: string[];
  createdBy: string;
}
```

## Technology Stack

### Core Technologies

- **Database:** PostgreSQL 17 with JSONB for flexible task storage
- **Text Processing:** Natural language processing libraries for similarity analysis
- **Vector Search:** Embedding-based semantic similarity
- **Caching:** Redis for quality metrics and contamination results
- **File Storage:** Object storage for task resources and attachments

### AI/ML Technologies

- **Text Similarity:** Cosine similarity, Jaccard similarity, Levenshtein distance
- **Semantic Search:** Sentence transformers for embedding-based similarity
- **Quality Assessment:** NLP models for text analysis and scoring
- **Contamination Detection:** Plagiarism detection algorithms

### Development Tools

- **Language:** TypeScript 5.7+ (strict mode)
- **Testing:** Vitest with comprehensive test coverage
- **Documentation:** OpenAPI specifications for all endpoints
- **Monitoring:** Custom metrics for quality and contamination analysis

## Data Models

### Core Entities

```typescript
interface Task {
  id: string;
  name: string;
  description?: string;
  category: string;
  subcategory?: string;
  difficultyLevel: DifficultyLevel;
  taskType: TaskType;
  content: TaskContent;
  expectedOutput?: ExpectedOutput;
  metadata: TaskMetadata;
  version: number;
  parentTaskId?: string;
  status: TaskStatus;
  qualityScore?: number;
  contaminationScore?: number;
  createdBy: string;
  createdAt: Date;
  updatedAt: Date;
  publishedAt?: Date;
  tags: string[];
}

interface QualityAssessment {
  taskId: string;
  overallScore: number;
  overallValidation: ValidationResult;
  detailedScores: QualityScore[];
  assessedAt: Date;
  recommendations: string[];
}

interface ContaminationAnalysis {
  taskId: string;
  overallRisk: ContaminationRisk;
  similarityAnalysis: SimilarityAnalysis;
  trainingDataAnalysis: TrainingDataAnalysis;
  temporalAnalysis: TemporalAnalysis;
  recommendations: string[];
  analyzedAt: Date;
}

interface TestBank {
  id: string;
  name: string;
  description: string;
  version: string;
  tasks: Task[];
  metadata: TestBankMetadata;
  statistics: TestBankStatistics;
  createdAt: Date;
  updatedAt: Date;
}
```

## API Specifications

### Task Management Endpoints

```yaml
/tasks:
  get:
    summary: Search and filter tasks
    security:
      - bearerAuth: []
    parameters:
      - name: q
        in: query
        schema:
          type: string
        description: Search query
      - name: category
        in: query
        schema:
          type: string
        description: Filter by category
      - name: difficulty
        in: query
        schema:
          type: string
          enum: [beginner, intermediate, advanced, expert]
        description: Filter by difficulty level
      - name: type
        in: query
        schema:
          type: string
        description: Filter by task type
      - name: qualityScore
        in: query
        schema:
          type: number
          minimum: 0
          maximum: 100
        description: Minimum quality score
      - name: page
        in: query
        schema:
          type: integer
          default: 1
        description: Page number
      - name: limit
        in: query
        schema:
          type: integer
          default: 20
          maximum: 100
        description: Results per page
    responses:
      200:
        description: Tasks retrieved successfully
        content:
          application/json:
            schema:
              type: object
              properties:
                data:
                  type: array
                  items:
                    $ref: '#/components/schemas/Task'
                pagination:
                  $ref: '#/components/schemas/Pagination'

  post:
    summary: Create new task
    security:
      - bearerAuth: []
    requestBody:
      required: true
      content:
        application/json:
          schema:
            $ref: '#/components/schemas/CreateTaskRequest'
    responses:
      201:
        description: Task created successfully
        content:
          application/json:
            schema:
              $ref: '#/components/schemas/Task'

/tasks/{taskId}:
  get:
    summary: Get task details
    security:
      - bearerAuth: []
    parameters:
      - name: taskId
        in: path
        required: true
        schema:
          type: string
    responses:
      200:
        description: Task retrieved successfully
        content:
          application/json:
            schema:
              $ref: '#/components/schemas/Task'

  put:
    summary: Update task
    security:
      - bearerAuth: []
    parameters:
      - name: taskId
        in: path
        required: true
        schema:
          type: string
    requestBody:
      required: true
      content:
        application/json:
          schema:
            $ref: '#/components/schemas/UpdateTaskRequest'
    responses:
      200:
        description: Task updated successfully
        content:
          application/json:
            schema:
              $ref: '#/components/schemas/Task'

  delete:
    summary: Delete task
    security:
      - bearerAuth: []
    parameters:
      - name: taskId
        in: path
        required: true
        schema:
          type: string
    responses:
      204:
        description: Task deleted successfully

/tasks/{taskId}/quality:
  post:
    summary: Assess task quality
    security:
      - bearerAuth: []
    parameters:
      - name: taskId
        in: path
        required: true
        schema:
          type: string
    responses:
      200:
        description: Quality assessment completed
        content:
          application/json:
            schema:
              $ref: '#/components/schemas/QualityAssessment'

/tasks/{taskId}/contamination:
  post:
    summary: Analyze task contamination
    security:
      - bearerAuth: []
    parameters:
      - name: taskId
        in: path
        required: true
        schema:
          type: string
    responses:
      200:
        description: Contamination analysis completed
        content:
          application/json:
            schema:
              $ref: '#/components/schemas/ContaminationAnalysis'

/tasks/{taskId}/versions:
  get:
    summary: Get task version history
    security:
      - bearerAuth: []
    parameters:
      - name: taskId
        in: path
        required: true
        schema:
          type: string
    responses:
      200:
        description: Task versions retrieved successfully
        content:
          application/json:
            schema:
              type: array
              items:
                $ref: '#/components/schemas/TaskVersion'
```

### Test Bank Endpoints

```yaml
/testbanks:
  get:
    summary: List test banks
    security:
      - bearerAuth: []
    responses:
      200:
        description: Test banks retrieved successfully
        content:
          application/json:
            schema:
              type: array
              items:
                $ref: '#/components/schemas/TestBank'

  post:
    summary: Create test bank
    security:
      - bearerAuth: []
    requestBody:
      required: true
      content:
        application/json:
          schema:
            $ref: '#/components/schemas/CreateTestBankRequest'
    responses:
      201:
        description: Test bank created successfully
        content:
          application/json:
            schema:
              $ref: '#/components/schemas/TestBank'

/testbanks/{testBankId}:
  get:
    summary: Get test bank details
    security:
      - bearerAuth: []
    parameters:
      - name: testBankId
        in: path
        required: true
        schema:
          type: string
    responses:
      200:
        description: Test bank retrieved successfully
        content:
          application/json:
            schema:
              $ref: '#/components/schemas/TestBank'

/testbanks/{testBankId}/export:
  post:
    summary: Export test bank
    security:
      - bearerAuth: []
    parameters:
      - name: testBankId
        in: path
        required: true
        schema:
          type: string
      - name: format
        in: query
        schema:
          type: string
          enum: [json, csv, yaml]
        description: Export format
    responses:
      200:
        description: Test bank exported successfully
        content:
          application/octet-stream:
            schema:
              type: string
              format: binary
```

## Performance Requirements

### Quality Assessment Performance

- **Task Quality Analysis:** 95th percentile < 5 seconds per task
- **Batch Quality Assessment:** 100 tasks in < 2 minutes
- **Quality Metric Calculation:** Individual metrics < 1 second each
- **Quality Cache:** Cache quality scores for 24 hours

### Contamination Detection Performance

- **Similarity Analysis:** 95th percentile < 3 seconds per task comparison
- **Batch Contamination Check:** 100 tasks against 10,000 existing tasks < 10 minutes
- **Training Data Overlap:** 95th percentile < 2 seconds per dataset
- **Contamination Cache:** Cache results for 7 days

### Search and Retrieval Performance

- **Task Search:** 95th percentile < 500ms for typical queries
- **Full-Text Search:** 95th percentile < 1 second for complex queries
- **Faceted Search:** 95th percentile < 750ms with filters
- **Export Operations:** 1000 tasks exported in < 30 seconds

## Testing Strategy

### Unit Tests

- **Task Repository Tests:** CRUD operations, versioning, search
- **Quality Calculator Tests:** Each quality metric calculation
- **Contamination Analyzer Tests:** Similarity, overlap detection
- **Test Bank Tests:** Creation, statistics, export functionality

### Integration Tests

- **End-to-End Task Creation:** Complete workflow from creation to publication
- **Quality Assessment Integration:** Quality service with repository
- **Contamination Detection Integration:** Contamination service with external data sources
- **Search Integration:** Search functionality with database indexes

### Performance Tests

- **Quality Assessment Load:** 1000 concurrent quality assessments
- **Contamination Detection Load:** Large-scale similarity analysis
- **Search Performance:** Complex search queries under load
- **Export Performance:** Large test bank exports

### Data Quality Tests

- **Quality Metric Accuracy:** Validate quality scores against human ratings
- **Contamination Detection Accuracy:** Test against known contaminated/clean tasks
- **Search Relevance:** Validate search result relevance
- **Data Integrity:** Ensure data consistency across operations

## Security Considerations

### Data Protection

- **Task Content Encryption:** Encrypt sensitive task content at rest
- **Access Control:** Role-based access to task creation and modification
- **Audit Logging:** Complete audit trail for all task operations
- **Data Retention:** Configurable retention policies for task data

### Intellectual Property Protection

- **Plagiarism Detection:** Automated detection of copied content
- **License Management:** Track and enforce content licensing
- **Attribution Tracking:** Maintain attribution for sourced content
- **Content Watermarking:** Optional watermarking for premium content

### Input Validation

- **Content Sanitization:** Sanitize all user-provided content
- **Schema Validation:** Validate all task data against schemas
- **File Upload Security:** Scan uploaded files for malware
- **Size Limits:** Enforce reasonable size limits for task content

## Monitoring and Observability

### Quality Metrics

- **Quality Score Distribution:** Monitor quality score trends
- **Assessment Performance:** Track quality assessment processing times
- **Quality Improvement:** Monitor quality improvements over time
- **Human Review Rates:** Track human review intervention rates

### Contamination Monitoring

- **Contamination Risk Distribution:** Monitor contamination risk levels
- **Similarity Analysis Performance:** Track similarity processing times
- **False Positive Rates**: Monitor contamination detection accuracy
- **Training Data Overlap**: Track new training data overlaps

### System Performance

- **Database Performance:** Query performance and connection pool metrics
- **Search Performance:** Search query performance and relevance
- **Cache Performance:** Cache hit rates and effectiveness
- **API Performance**: Response times and error rates

---

**Next Steps:**

1. Review and approve this technical specification
2. Set up development environment with required dependencies
3. Begin Story 3.1 implementation (Task Repository Schema & Storage)
4. Create comprehensive test suites for all components
5. Establish quality benchmarks and contamination detection thresholds
