# Epic Technical Specification: Multi-Judge Evaluation System

**Date:** 2025-11-04  
**Author:** meywd  
**Epic ID:** 5  
**Status:** Draft  
**Project:** AI Benchmarking Test Platform (AIBaaS)

---

## Overview

Epic 5 implements a comprehensive multi-judge evaluation system that combines automated scoring, human expert review, community voting, and AI self-review mechanisms to provide robust, reliable assessment of AI model performance. This epic delivers staff review interfaces, community voting systems, AI self-review capabilities, elite panel review workflows, and multi-judge score aggregation algorithms. The system ensures evaluation fairness, transparency, and consistency while handling edge cases and resolving conflicts between different evaluation methods.

This epic directly addresses the core evaluation requirements from the PRD: multi-judge evaluation (FR-13), human review workflows (FR-14), community assessment mechanisms (FR-15), and comprehensive score aggregation (FR-16). By implementing a layered evaluation approach with multiple independent judges, Epic 5 provides the reliability and validity needed for trustworthy AI benchmarking results that can be used for model selection, research, and publication.

## Objectives and Scope

**In Scope:**

- Story 5.1: Staff Review Interface - Internal expert review workflow
- Story 5.2: Community Voting System - Public community assessment platform
- Story 5.3: AI Self-Review System - Automated AI-based evaluation
- Story 5.4: Elite Panel Review System - Expert panel evaluation
- Story 5.5: Multi-Judge Score Aggregation - Consensus and conflict resolution

**Out of Scope:**

- User interfaces and dashboards (Epic 6)
- Advanced analytics and reporting (Epic 5 enhancements)
- Real-time monitoring and observability (addressed in later epics)
- Benchmark execution engine (Epic 4)

## System Architecture Alignment

Epic 5 implements the evaluation layer that sits on top of the execution engine and provides comprehensive assessment:

### Multi-Judge Architecture

- **Staff Review:** Internal expert evaluation with workflow management
- **Community Voting:** Public community assessment with reputation systems
- **AI Self-Review:** Automated AI-based evaluation and consistency checking
- **Elite Panel:** Expert panel evaluation for high-stakes assessments
- **Aggregation Engine:** Statistical aggregation and conflict resolution

### Evaluation Workflow

- **Assignment System:** Intelligent task assignment to appropriate judges
- **Review Interface:** Specialized interfaces for different judge types
- **Quality Control:** Review quality monitoring and calibration
- **Consensus Building:** Conflict resolution and consensus mechanisms

### Score Management

- **Collection System:** Multi-source score collection and validation
- **Normalization:** Score normalization and bias correction
- **Aggregation:** Statistical aggregation with confidence intervals
- **Transparency:** Complete audit trail and explanation generation

## Detailed Design

### Services and Modules

#### 1. Staff Review Interface (Story 5.1)

**Staff Review Framework:**

```typescript
interface StaffReviewService {
  assignReview(executionId: string, reviewerId: string): Promise<ReviewAssignment>;
  submitReview(review: StaffReview): Promise<void>;
  getReviewQueue(reviewerId: string): Promise<ReviewAssignment[]>;
  getReviewHistory(reviewerId: string, filter?: ReviewHistoryFilter): Promise<StaffReview[]>;
  calibrateReviewer(reviewerId: string, calibrationTasks: string[]): Promise<CalibrationResult>;
  getReviewerStats(reviewerId: string): Promise<ReviewerStats>;
}

interface ReviewAssignment {
  id: string;
  executionId: string;
  reviewerId: string;
  taskId: string;
  assignedAt: Date;
  deadline?: Date;
  priority: ReviewPriority;
  status: AssignmentStatus;
  context: ReviewContext;
}

enum ReviewPriority {
  LOW = 'low',
  NORMAL = 'normal',
  HIGH = 'high',
  URGENT = 'urgent',
}

enum AssignmentStatus {
  ASSIGNED = 'assigned',
  IN_PROGRESS = 'in_progress',
  COMPLETED = 'completed',
  ESCALATED = 'escalated',
  CANCELLED = 'cancelled',
}

interface ReviewContext {
  task: Task;
  execution: TaskExecutionResult;
  previousReviews: StaffReview[];
  evaluationCriteria: EvaluationCriteria[];
  guidelines: ReviewGuidelines;
  calibrationInfo?: CalibrationInfo;
}

interface ReviewGuidelines {
  scoringRubric: ScoringRubric;
  qualityStandards: QualityStandard[];
  commonPitfalls: CommonPitfall[];
  examples: ReviewExample[];
  timeEstimate: number; // minutes
}

interface ScoringRubric {
  criteria: RubricCriterion[];
  scoringScale: ScoringScale;
  weightDistribution: WeightDistribution;
}

interface RubricCriterion {
  id: string;
  name: string;
  description: string;
  weight: number;
  levels: RubricLevel[];
  required: boolean;
}

interface RubricLevel {
  level: number;
  name: string;
  description: string;
  points: number;
  examples: string[];
}

interface ScoringScale {
  type: 'points' | 'percentage' | 'descriptive';
  min: number;
  max: number;
  increments: number;
  labels?: Record<number, string>;
}

interface WeightDistribution {
  criteria: Record<string, number>;
  total: number;
}

interface StaffReview {
  id: string;
  assignmentId: string;
  executionId: string;
  reviewerId: string;
  overallScore: number;
  criterionScores: CriterionReviewScore[];
  feedback: ReviewFeedback;
  timeSpent: number; // minutes
  confidence: number;
  status: ReviewStatus;
  submittedAt: Date;
  metadata: ReviewMetadata;
}

enum ReviewStatus {
  DRAFT = 'draft',
  SUBMITTED = 'submitted',
  APPROVED = 'approved',
  REJECTED = 'rejected',
  NEEDS_REVISION = 'needs_revision',
}

interface CriterionReviewScore {
  criterionId: string;
  score: number;
  maxScore: number;
  weight: number;
  level?: number;
  comments: string;
  evidence: ReviewEvidence[];
  confidence: number;
}

interface ReviewEvidence {
  type: EvidenceType;
  content: string;
  relevance: number;
  timestamp: Date;
  source: string;
}

interface ReviewFeedback {
  strengths: string[];
  weaknesses: string[];
  suggestions: string[];
  overallComments: string;
  actionableFeedback: ActionableFeedback[];
}

interface ActionableFeedback {
  type: 'improvement' | 'clarification' | 'correction';
  priority: 'high' | 'medium' | 'low';
  description: string;
  suggestedAction: string;
}

interface ReviewMetadata {
  reviewDuration: number;
  pauseCount: number;
  revisionCount: number;
  browserInfo?: string;
  ipAddress?: string;
  deviceType?: string;
}

interface ReviewHistoryFilter {
  dateRange?: {
    startDate: Date;
    endDate: Date;
  };
  taskIds?: string[];
  status?: ReviewStatus[];
  scoreRange?: {
    min: number;
    max: number;
  };
  limit?: number;
  offset?: number;
}

interface ReviewerStats {
  reviewerId: string;
  totalReviews: number;
  averageScore: number;
  averageTimePerReview: number;
  completionRate: number;
  qualityScore: number;
  calibrationScore: number;
  specializations: string[];
  recentActivity: ReviewActivity[];
  performanceTrends: PerformanceTrend[];
}

interface CalibrationResult {
  reviewerId: string;
  calibrationScore: number;
  biasAnalysis: BiasAnalysis;
  consistencyScore: number;
  recommendations: string[];
  nextCalibrationDue: Date;
}

interface BiasAnalysis {
  severityBias: number;
  leniencyBias: number;
  centralTendencyBias: number;
  haloEffect: number;
  confirmationBias: number;
}
```

**Staff Review Implementation:**

```typescript
class StaffReviewService implements StaffReviewService {
  constructor(
    private reviewRepository: ReviewRepository,
    private taskRepository: TaskRepository,
    private executionRepository: ExecutionRepository,
    private assignmentEngine: AssignmentEngine,
    private calibrationService: CalibrationService,
    private notificationService: NotificationService,
    private qualityMonitor: QualityMonitor
  ) {}

  async assignReview(executionId: string, reviewerId: string): Promise<ReviewAssignment> {
    // Validate execution and reviewer
    const execution = await this.executionRepository.getExecution(executionId);
    if (!execution) {
      throw new Error(`Execution ${executionId} not found`);
    }

    const reviewer = await this.getReviewer(reviewerId);
    if (!reviewer || !reviewer.isActive) {
      throw new Error(`Reviewer ${reviewerId} not available`);
    }

    // Check for conflicts of interest
    if (await this.hasConflictOfInterest(execution, reviewerId)) {
      throw new Error('Conflict of interest detected');
    }

    // Get review context
    const context = await this.buildReviewContext(execution);

    // Determine priority and deadline
    const priority = this.calculateReviewPriority(execution);
    const deadline = this.calculateDeadline(execution, priority);

    // Create assignment
    const assignment: ReviewAssignment = {
      id: `assignment_${Date.now()}`,
      executionId,
      reviewerId,
      taskId: execution.taskId,
      assignedAt: new Date(),
      deadline,
      priority,
      status: AssignmentStatus.ASSIGNED,
      context,
    };

    await this.reviewRepository.createAssignment(assignment);

    // Send notification
    await this.notificationService.sendReviewAssignment(assignment);

    return assignment;
  }

  async submitReview(review: StaffReview): Promise<void> {
    // Validate review
    await this.validateReview(review);

    // Get assignment
    const assignment = await this.reviewRepository.getAssignment(review.assignmentId);
    if (!assignment) {
      throw new Error('Review assignment not found');
    }

    // Check deadline
    if (assignment.deadline && new Date() > assignment.deadline) {
      await this.handleOverdueSubmission(assignment, review);
    }

    // Calculate quality metrics
    const qualityMetrics = await this.calculateReviewQuality(review, assignment.context);

    // Update review with quality metrics
    review.metadata = {
      ...review.metadata,
      qualityMetrics,
      submittedAt: new Date(),
    };

    // Save review
    await this.reviewRepository.saveReview(review);

    // Update assignment status
    await this.reviewRepository.updateAssignmentStatus(
      review.assignmentId,
      AssignmentStatus.COMPLETED
    );

    // Trigger quality monitoring
    await this.qualityMonitor.analyzeReview(review);

    // Update reviewer stats
    await this.updateReviewerStats(review.reviewerId, review);

    // Check if all reviews are complete for consensus
    await this.checkConsensusReadiness(review.executionId);
  }

  async getReviewQueue(reviewerId: string): Promise<ReviewAssignment[]> {
    return await this.reviewRepository.getReviewerAssignments(reviewerId, [
      AssignmentStatus.ASSIGNED,
      AssignmentStatus.IN_PROGRESS,
    ]);
  }

  async getReviewHistory(reviewerId: string, filter?: ReviewHistoryFilter): Promise<StaffReview[]> {
    return await this.reviewRepository.getReviewerReviews(reviewerId, filter);
  }

  async calibrateReviewer(
    reviewerId: string,
    calibrationTasks: string[]
  ): Promise<CalibrationResult> {
    // Get calibration executions with known ground truth
    const calibrationExecutions = await this.executionRepository.getExecutions(calibrationTasks);

    const reviewerReviews: StaffReview[] = [];
    const groundTruthScores: number[] = [];

    for (const execution of calibrationExecutions) {
      // Get existing review by this reviewer
      const review = await this.reviewRepository.getReviewerExecutionReview(
        reviewerId,
        execution.id
      );

      if (review) {
        reviewerReviews.push(review);
        // Get ground truth score (would come from expert panel or previous consensus)
        const groundTruth = await this.getGroundTruthScore(execution.id);
        groundTruthScores.push(groundTruth);
      }
    }

    // Calculate calibration metrics
    const calibrationScore = this.calculateCalibrationScore(reviewerReviews, groundTruthScores);
    const biasAnalysis = this.analyzeBias(reviewerReviews, groundTruthScores);
    const consistencyScore = this.calculateConsistency(reviewerReviews);

    // Generate recommendations
    const recommendations = this.generateCalibrationRecommendations(
      calibrationScore,
      biasAnalysis,
      consistencyScore
    );

    const result: CalibrationResult = {
      reviewerId,
      calibrationScore,
      biasAnalysis,
      consistencyScore,
      recommendations,
      nextCalibrationDue: this.calculateNextCalibrationDue(calibrationScore),
    };

    // Save calibration result
    await this.calibrationService.saveCalibrationResult(result);

    return result;
  }

  async getReviewerStats(reviewerId: string): Promise<ReviewerStats> {
    const reviews = await this.reviewRepository.getReviewerReviews(reviewerId);
    const assignments = await this.reviewRepository.getReviewerAssignments(reviewerId);

    // Calculate basic stats
    const totalReviews = reviews.length;
    const completedAssignments = assignments.filter(
      (a) => a.status === AssignmentStatus.COMPLETED
    ).length;

    const averageScore =
      totalReviews > 0 ? reviews.reduce((sum, r) => sum + r.overallScore, 0) / totalReviews : 0;

    const averageTimePerReview =
      totalReviews > 0 ? reviews.reduce((sum, r) => sum + r.timeSpent, 0) / totalReviews : 0;

    const completionRate = assignments.length > 0 ? completedAssignments / assignments.length : 0;

    // Get quality and calibration scores
    const qualityScore = await this.qualityMonitor.getReviewerQualityScore(reviewerId);
    const calibrationScore = await this.calibrationService.getReviewerCalibrationScore(reviewerId);

    // Analyze specializations
    const specializations = this.analyzeSpecializations(reviews);

    // Get recent activity
    const recentActivity = await this.getRecentActivity(reviewerId);

    // Calculate performance trends
    const performanceTrends = await this.calculatePerformanceTrends(reviews);

    return {
      reviewerId,
      totalReviews,
      averageScore,
      averageTimePerReview,
      completionRate,
      qualityScore,
      calibrationScore,
      specializations,
      recentActivity,
      performanceTrends,
    };
  }

  private async buildReviewContext(execution: TaskExecutionResult): Promise<ReviewContext> {
    const task = await this.taskRepository.getTask(execution.taskId);
    if (!task) {
      throw new Error(`Task ${execution.taskId} not found`);
    }

    const previousReviews = await this.reviewRepository.getExecutionReviews(execution.id);
    const evaluationCriteria = await this.getEvaluationCriteria(task.id);
    const guidelines = await this.getReviewGuidelines(task);

    return {
      task,
      execution,
      previousReviews,
      evaluationCriteria,
      guidelines,
    };
  }

  private calculateReviewPriority(execution: TaskExecutionResult): ReviewPriority {
    // Factors: task difficulty, model importance, time sensitivity
    const task = await this.taskRepository.getTask(execution.taskId);

    let priorityScore = 0;

    // Task difficulty
    if (task?.difficultyLevel === DifficultyLevel.EXPERT) {
      priorityScore += 3;
    } else if (task?.difficultyLevel === DifficultyLevel.ADVANCED) {
      priorityScore += 2;
    } else if (task?.difficultyLevel === DifficultyLevel.INTERMEDIATE) {
      priorityScore += 1;
    }

    // Model importance (based on usage or strategic value)
    const modelImportance = await this.getModelImportance(execution.modelId);
    priorityScore += modelImportance;

    // Time sensitivity
    const daysSinceExecution = (Date.now() - execution.startTime.getTime()) / (1000 * 60 * 60 * 24);
    if (daysSinceExecution > 7) {
      priorityScore += 2;
    } else if (daysSinceExecution > 3) {
      priorityScore += 1;
    }

    if (priorityScore >= 5) {
      return ReviewPriority.URGENT;
    } else if (priorityScore >= 3) {
      return ReviewPriority.HIGH;
    } else if (priorityScore >= 1) {
      return ReviewPriority.NORMAL;
    }

    return ReviewPriority.LOW;
  }

  private calculateDeadline(execution: TaskExecutionResult, priority: ReviewPriority): Date {
    const baseTime = this.getEstimatedReviewTime(execution);

    let multiplier: number;
    switch (priority) {
      case ReviewPriority.URGENT:
        multiplier = 0.5;
        break;
      case ReviewPriority.HIGH:
        multiplier = 0.75;
        break;
      case ReviewPriority.NORMAL:
        multiplier = 1.0;
        break;
      case ReviewPriority.LOW:
        multiplier = 1.5;
        break;
    }

    const deadlineTime = baseTime * multiplier;
    return new Date(Date.now() + deadlineTime);
  }

  private getEstimatedReviewTime(execution: TaskExecutionResult): number {
    // Base time estimates by task type and complexity
    const task = await this.taskRepository.getTask(execution.taskId);

    const baseTimes: Record<TaskType, number> = {
      [TaskType.CODING]: 15, // 15 minutes
      [TaskType.REASONING]: 10,
      [TaskType.CREATIVE]: 20,
      [TaskType.ANALYSIS]: 12,
      [TaskType.COMPREHENSION]: 8,
      [TaskType.TRANSLATION]: 6,
      [TaskType.SUMMARIZATION]: 5,
      [TaskType.CLASSIFICATION]: 4,
    };

    let baseTime = baseTimes[task?.taskType || TaskType.REASONING] || 10;

    // Adjust for difficulty
    if (task?.difficultyLevel === DifficultyLevel.EXPERT) {
      baseTime *= 2.0;
    } else if (task?.difficultyLevel === DifficultyLevel.ADVANCED) {
      baseTime *= 1.5;
    } else if (task?.difficultyLevel === DifficultyLevel.BEGINNER) {
      baseTime *= 0.75;
    }

    // Adjust for response length
    if (execution.response?.content) {
      const responseLength = execution.response.content.length;
      if (responseLength > 1000) {
        baseTime *= 1.3;
      } else if (responseLength > 500) {
        baseTime *= 1.1;
      }
    }

    return baseTime * 60 * 1000; // Convert to milliseconds
  }

  private async validateReview(review: StaffReview): Promise<void> {
    // Validate required fields
    if (!review.overallScore && review.overallScore !== 0) {
      throw new Error('Overall score is required');
    }

    if (review.overallScore < 0 || review.overallScore > 100) {
      throw new Error('Overall score must be between 0 and 100');
    }

    if (!review.criterionScores || review.criterionScores.length === 0) {
      throw new Error('At least one criterion score is required');
    }

    // Validate criterion scores
    for (const criterionScore of review.criterionScores) {
      if (criterionScore.score < 0 || criterionScore.score > criterionScore.maxScore) {
        throw new Error(`Invalid score for criterion ${criterionScore.criterionId}`);
      }

      if (criterionScore.weight < 0 || criterionScore.weight > 1) {
        throw new Error(`Invalid weight for criterion ${criterionScore.criterionId}`);
      }
    }

    // Validate confidence
    if (review.confidence < 0 || review.confidence > 1) {
      throw new Error('Confidence must be between 0 and 1');
    }

    // Check for completeness
    const assignment = await this.reviewRepository.getAssignment(review.assignmentId);
    const requiredCriteria = assignment.context.evaluationCriteria;

    for (const criterion of requiredCriteria) {
      if (
        criterion.required &&
        !review.criterionScores.find((s) => s.criterionId === criterion.id)
      ) {
        throw new Error(`Required criterion ${criterion.id} is missing`);
      }
    }
  }

  private async calculateReviewQuality(review: StaffReview, context: ReviewContext): Promise<any> {
    // Consistency with previous reviews
    const consistencyScore = await this.calculateConsistencyWithPrevious(
      review,
      context.previousReviews
    );

    // Time appropriateness
    const timeAppropriateness = this.calculateTimeAppropriateness(
      review,
      context.guidelines.timeEstimate
    );

    // Completeness
    const completenessScore = this.calculateCompleteness(review, context.evaluationCriteria);

    // Comment quality
    const commentQuality = await this.analyzeCommentQuality(review.feedback);

    return {
      consistency: consistencyScore,
      timeAppropriateness,
      completeness: completenessScore,
      commentQuality,
      overallQuality:
        (consistencyScore + timeAppropriateness + completenessScore + commentQuality) / 4,
    };
  }

  private calculateConsistencyWithPrevious(
    review: StaffReview,
    previousReviews: StaffReview[]
  ): number {
    if (previousReviews.length === 0) {
      return 1.0; // No previous reviews to compare with
    }

    let totalSimilarity = 0;

    for (const previousReview of previousReviews) {
      const similarity = this.calculateReviewSimilarity(review, previousReview);
      totalSimilarity += similarity;
    }

    return totalSimilarity / previousReviews.length;
  }

  private calculateReviewSimilarity(review1: StaffReview, review2: StaffReview): number {
    // Overall score similarity
    const scoreDiff = Math.abs(review1.overallScore - review2.overallScore);
    const scoreSimilarity = Math.max(0, 1 - scoreDiff / 100);

    // Criterion score similarity
    let criterionSimilarity = 0;
    let commonCriteria = 0;

    for (const score1 of review1.criterionScores) {
      const score2 = review2.criterionScores.find((s) => s.criterionId === score1.criterionId);
      if (score2) {
        commonCriteria++;
        const criterionDiff = Math.abs(score1.score - score2.score);
        const maxScore = Math.max(score1.maxScore, score2.maxScore);
        const criterionSimilarity = Math.max(0, 1 - criterionDiff / maxScore);
        criterionSimilarity += criterionSimilarity;
      }
    }

    const avgCriterionSimilarity = commonCriteria > 0 ? criterionSimilarity / commonCriteria : 0;

    // Weighted average
    return scoreSimilarity * 0.4 + avgCriterionSimilarity * 0.6;
  }

  private calculateTimeAppropriateness(review: StaffReview, estimatedTime: number): number {
    const actualTime = review.timeSpent;

    if (actualTime <= estimatedTime * 1.2) {
      return 1.0; // Within reasonable range
    } else if (actualTime <= estimatedTime * 2.0) {
      return 0.8; // Somewhat over
    } else if (actualTime <= estimatedTime * 3.0) {
      return 0.6; // Significantly over
    } else {
      return 0.4; // Excessively over
    }
  }

  private calculateCompleteness(
    review: StaffReview,
    requiredCriteria: EvaluationCriteria[]
  ): number {
    const providedCriteria = review.criterionScores.map((s) => s.criterionId);
    const requiredCriteriaIds = requiredCriteria.map((c) => c.id);

    const missingCriteria = requiredCriteriaIds.filter((id) => !providedCriteria.includes(id));

    if (missingCriteria.length === 0) {
      return 1.0;
    }

    return Math.max(0, 1 - missingCriteria.length / requiredCriteriaIds.length);
  }

  private async analyzeCommentQuality(feedback: ReviewFeedback): Promise<number> {
    let qualityScore = 0.5; // Base score

    // Check for specific, actionable feedback
    if (feedback.suggestions.length > 0) {
      qualityScore += 0.2;
    }

    // Check for balanced feedback (strengths and weaknesses)
    if (feedback.strengths.length > 0 && feedback.weaknesses.length > 0) {
      qualityScore += 0.1;
    }

    // Check comment length and detail
    const totalCommentLength =
      feedback.overallComments.length +
      feedback.strengths.join(' ').length +
      feedback.weaknesses.join(' ').length +
      feedback.suggestions.join(' ').length;

    if (totalCommentLength > 100) {
      qualityScore += 0.1;
    }

    // Check for constructive language (simplified sentiment analysis)
    const constructiveTerms = ['improve', 'consider', 'suggest', 'recommend', 'enhance'];
    const hasConstructiveLanguage = constructiveTerms.some(
      (term) =>
        feedback.overallComments.toLowerCase().includes(term) ||
        feedback.suggestions.some((s) => s.toLowerCase().includes(term))
    );

    if (hasConstructiveLanguage) {
      qualityScore += 0.1;
    }

    return Math.min(1.0, qualityScore);
  }

  private async hasConflictOfInterest(
    execution: TaskExecutionResult,
    reviewerId: string
  ): Promise<boolean> {
    // Check if reviewer created the task
    const task = await this.taskRepository.getTask(execution.taskId);
    if (task.createdBy === reviewerId) {
      return true;
    }

    // Check if reviewer works for same organization as task creator
    const reviewer = await this.getReviewer(reviewerId);
    if (reviewer.organizationId === task.createdBy) {
      return true;
    }

    // Check if reviewer has recent involvement with similar tasks
    const recentReviews = await this.reviewRepository.getReviewerRecentReviews(reviewerId, 30); // 30 days
    const similarTaskReviews = recentReviews.filter((r) => {
      // Would check for similar task categories, providers, etc.
      return this.areTasksSimilar(r.taskId, execution.taskId);
    });

    return similarTaskReviews.length > 3; // Threshold for potential conflict
  }

  private areTasksSimilar(taskId1: string, taskId2: string): boolean {
    // Simplified similarity check - would be more sophisticated
    return taskId1.substring(0, 8) === taskId2.substring(0, 8);
  }

  private async checkConsensusReadiness(executionId: string): Promise<void> {
    const requiredReviews = await this.getRequiredReviewCount(executionId);
    const completedReviews = await this.reviewRepository.getCompletedReviewCount(executionId);

    if (completedReviews >= requiredReviews) {
      await this.triggerConsensusCalculation(executionId);
    }
  }

  private async triggerConsensusCalculation(executionId: string): Promise<void> {
    // This would trigger the multi-judge score aggregation system
    await this.consensusEngine.calculateConsensus(executionId);
  }

  private analyzeSpecializations(reviews: StaffReview[]): string[] {
    const categoryCounts: Record<string, number> = {};

    for (const review of reviews) {
      // Would get task category from review
      const category = review.metadata?.taskCategory || 'general';
      categoryCounts[category] = (categoryCounts[category] || 0) + 1;
    }

    const totalReviews = reviews.length;
    const specializations: string[] = [];

    for (const [category, count] of Object.entries(categoryCounts)) {
      if (count / totalReviews >= 0.3) {
        // At least 30% in category
        specializations.push(category);
      }
    }

    return specializations;
  }

  private async getRecentActivity(reviewerId: string): Promise<ReviewActivity[]> {
    return await this.reviewRepository.getReviewerRecentActivity(reviewerId, 7); // 7 days
  }

  private async calculatePerformanceTrends(reviews: StaffReview[]): Promise<PerformanceTrend[]> {
    // Group reviews by month and calculate trends
    const monthlyData: Record<string, StaffReview[]> = {};

    for (const review of reviews) {
      const month = review.submittedAt.toISOString().substring(0, 7); // YYYY-MM
      if (!monthlyData[month]) {
        monthlyData[month] = [];
      }
      monthlyData[month].push(review);
    }

    const trends: PerformanceTrend[] = [];
    const months = Object.keys(monthlyData).sort();

    for (let i = 1; i < months.length; i++) {
      const currentMonth = monthlyData[months[i]];
      const previousMonth = monthlyData[months[i - 1]];

      const currentAvg =
        currentMonth.reduce((sum, r) => sum + r.overallScore, 0) / currentMonth.length;
      const previousAvg =
        previousMonth.reduce((sum, r) => sum + r.overallScore, 0) / previousMonth.length;

      trends.push({
        period: months[i],
        averageScore: currentAvg,
        scoreChange: currentAvg - previousAvg,
        reviewCount: currentMonth.length,
        trend: currentAvg > previousAvg ? 'improving' : 'declining',
      });
    }

    return trends;
  }

  private calculateCalibrationScore(
    reviewerReviews: StaffReview[],
    groundTruthScores: number[]
  ): number {
    if (reviewerReviews.length === 0 || groundTruthScores.length === 0) {
      return 0;
    }

    let totalAgreement = 0;
    let count = 0;

    for (let i = 0; i < Math.min(reviewerReviews.length, groundTruthScores.length); i++) {
      const reviewScore = reviewerReviews[i].overallScore;
      const groundTruthScore = groundTruthScores[i];

      // Calculate agreement as inverse of absolute difference
      const difference = Math.abs(reviewScore - groundTruthScore);
      const agreement = Math.max(0, 1 - difference / 100);

      totalAgreement += agreement;
      count++;
    }

    return count > 0 ? totalAgreement / count : 0;
  }

  private analyzeBias(reviewerReviews: StaffReview[], groundTruthScores: number[]): BiasAnalysis {
    // Simplified bias analysis
    const scoreDifferences = reviewerReviews.map(
      (review, index) => review.overallScore - groundTruthScores[index]
    );

    const avgDifference =
      scoreDifferences.reduce((sum, diff) => sum + diff, 0) / scoreDifferences.length;

    return {
      severityBias: 0, // Would need more complex analysis
      leniencyBias: avgDifference > 5 ? avgDifference / 100 : 0,
      centralTendencyBias: 0,
      haloEffect: 0,
      confirmationBias: 0,
    };
  }

  private calculateConsistency(reviewerReviews: StaffReview[]): number {
    if (reviewerReviews.length < 2) {
      return 1.0;
    }

    // Calculate standard deviation of scores
    const scores = reviewerReviews.map((r) => r.overallScore);
    const mean = scores.reduce((sum, score) => sum + score, 0) / scores.length;
    const variance =
      scores.reduce((sum, score) => sum + Math.pow(score - mean, 2), 0) / scores.length;
    const standardDeviation = Math.sqrt(variance);

    // Consistency as inverse of standard deviation
    return Math.max(0, 1 - standardDeviation / 100);
  }

  private generateCalibrationRecommendations(
    calibrationScore: number,
    biasAnalysis: BiasAnalysis,
    consistencyScore: number
  ): string[] {
    const recommendations: string[] = [];

    if (calibrationScore < 0.7) {
      recommendations.push('Review scoring guidelines and rubrics carefully');
      recommendations.push('Consider additional training on evaluation criteria');
    }

    if (biasAnalysis.leniencyBias > 0.2) {
      recommendations.push('Be aware of tendency to score too leniently');
      recommendations.push('Compare your scores with peer reviews regularly');
    }

    if (biasAnalysis.leniencyBias < -0.2) {
      recommendations.push('Be aware of tendency to score too harshly');
      recommendations.push('Focus on positive aspects when appropriate');
    }

    if (consistencyScore < 0.7) {
      recommendations.push('Work on maintaining consistent scoring standards');
      recommendations.push('Use rubrics more systematically');
    }

    return recommendations;
  }

  private calculateNextCalibrationDue(calibrationScore: number): Date {
    let daysUntilNextCalibration: number;

    if (calibrationScore >= 0.9) {
      daysUntilNextCalibration = 90; // 3 months
    } else if (calibrationScore >= 0.8) {
      daysUntilNextCalibration = 60; // 2 months
    } else if (calibrationScore >= 0.7) {
      daysUntilNextCalibration = 30; // 1 month
    } else {
      daysUntilNextCalibration = 14; // 2 weeks
    }

    return new Date(Date.now() + daysUntilNextCalibration * 24 * 60 * 60 * 1000);
  }

  private async handleOverdueSubmission(
    assignment: ReviewAssignment,
    review: StaffReview
  ): Promise<void> {
    // Mark as overdue but accept
    review.metadata = {
      ...review.metadata,
      submittedOverdue: true,
      originalDeadline: assignment.deadline,
      submissionDelay: Date.now() - assignment.deadline!.getTime(),
    };

    // Notify supervisor
    await this.notificationService.sendOverdueSubmissionNotification(assignment, review);
  }

  private async updateReviewerStats(reviewerId: string, review: StaffReview): Promise<void> {
    // Update reviewer statistics in real-time
    await this.reviewRepository.updateReviewerStats(reviewerId, {
      totalReviews: 1,
      totalTimeSpent: review.timeSpent,
      lastReviewDate: review.submittedAt,
    });
  }

  private async getReviewer(reviewerId: string): Promise<Reviewer | null> {
    return await this.reviewRepository.getReviewer(reviewerId);
  }

  private async getEvaluationCriteria(taskId: string): Promise<EvaluationCriteria[]> {
    return await this.taskRepository.getEvaluationCriteria(taskId);
  }

  private async getReviewGuidelines(task: Task): Promise<ReviewGuidelines> {
    // Would fetch guidelines based on task type and category
    return {
      scoringRubric: await this.getScoringRubric(task),
      qualityStandards: await this.getQualityStandards(task),
      commonPitfalls: await this.getCommonPitfalls(task),
      examples: await this.getReviewExamples(task),
      timeEstimate: this.getEstimatedReviewTimeForTask(task),
    };
  }

  private async getRequiredReviewCount(executionId: string): Promise<number> {
    // Would be configurable based on task importance, model, etc.
    return 3; // Default to 3 reviews
  }

  private async getModelImportance(modelId: string): Promise<number> {
    // Would calculate based on usage frequency, strategic value, etc.
    return 1; // Default importance
  }

  private async getGroundTruthScore(executionId: string): Promise<number> {
    // Would get from expert panel or previous high-confidence consensus
    return 75; // Placeholder
  }
}

interface ReviewRepository {
  createAssignment(assignment: ReviewAssignment): Promise<void>;
  getAssignment(assignmentId: string): Promise<ReviewAssignment | null>;
  getReviewerAssignments(
    reviewerId: string,
    statuses?: AssignmentStatus[]
  ): Promise<ReviewAssignment[]>;
  updateAssignmentStatus(assignmentId: string, status: AssignmentStatus): Promise<void>;
  saveReview(review: StaffReview): Promise<void>;
  getReviewerExecutionReview(reviewerId: string, executionId: string): Promise<StaffReview | null>;
  getExecutionReviews(executionId: string): Promise<StaffReview[]>;
  getReviewerReviews(reviewerId: string, filter?: ReviewHistoryFilter): Promise<StaffReview[]>;
  getCompletedReviewCount(executionId: string): Promise<number>;
  getReviewerRecentReviews(reviewerId: string, days: number): Promise<StaffReview[]>;
  getReviewerRecentActivity(reviewerId: string, days: number): Promise<ReviewActivity[]>;
  updateReviewerStats(reviewerId: string, stats: Partial<ReviewerStats>): Promise<void>;
  getReviewer(reviewerId: string): Promise<Reviewer | null>;
}

interface AssignmentEngine {
  assignReview(executionId: string): Promise<string>; // Returns reviewerId
}

interface CalibrationService {
  saveCalibrationResult(result: CalibrationResult): Promise<void>;
  getReviewerCalibrationScore(reviewerId: string): Promise<number>;
}

interface QualityMonitor {
  analyzeReview(review: StaffReview): Promise<void>;
  getReviewerQualityScore(reviewerId: string): Promise<number>;
}

interface Reviewer {
  id: string;
  userId: string;
  organizationId: string;
  isActive: boolean;
  specializations: string[];
  maxAssignments: number;
  currentAssignments: number;
}

interface ReviewActivity {
  date: Date;
  action: string;
  details: string;
}

interface PerformanceTrend {
  period: string;
  averageScore: number;
  scoreChange: number;
  reviewCount: number;
  trend: 'improving' | 'declining' | 'stable';
}
```

#### 2. Community Voting System (Story 5.2)

**Community Voting Framework:**

```typescript
interface CommunityVotingService {
  openVoting(executionId: string, votingConfig: VotingConfig): Promise<VotingSession>;
  submitVote(vote: CommunityVote): Promise<void>;
  getVotingSession(sessionId: string): Promise<VotingSession>;
  getVotingResults(sessionId: string): Promise<VotingResults>;
  closeVoting(sessionId: string): Promise<VotingResults>;
  getUserVotingHistory(userId: string, filter?: VotingHistoryFilter): Promise<CommunityVote[]>;
  calculateReputationScore(userId: string): Promise<ReputationScore>;
}

interface VotingSession {
  id: string;
  executionId: string;
  config: VotingConfig;
  status: VotingStatus;
  createdAt: Date;
  closesAt?: Date;
  participantCount: number;
  voteCount: number;
  currentResults?: VotingResults;
  metadata: VotingSessionMetadata;
}

enum VotingStatus {
  OPEN = 'open',
  CLOSED = 'closed',
  CANCELLED = 'cancelled',
  PENDING = 'pending',
}

interface VotingConfig {
  type: VotingType;
  criteria: VotingCriteria[];
  weightDistribution: WeightDistribution;
  eligibility: EligibilityCriteria;
  anonymity: AnonymityConfig;
  quorum: QuorumRequirements;
  antiSpam: AntiSpamConfig;
}

enum VotingType {
  SIMPLE_RATING = 'simple_rating',
  CRITERIA_BASED = 'criteria_based',
  COMPARATIVE = 'comparative',
  APPROVAL = 'approval',
}

interface VotingCriteria {
  id: string;
  name: string;
  description: string;
  type: CriteriaType;
  required: boolean;
  weight: number;
  options?: VotingOption[];
}

enum CriteriaType {
  RATING = 'rating',
  CHOICE = 'choice',
  MULTIPLE_CHOICE = 'multiple_choice',
  TEXT = 'text',
}

interface VotingOption {
  id: string;
  label: string;
  description?: string;
  value?: number;
}

interface EligibilityCriteria {
  minReputation: number;
  requiredVerifications: string[];
  excludeCreators: boolean;
  maxParticipants?: number;
  allowMultipleVotes: boolean;
}

interface AnonymityConfig {
  anonymousVoting: boolean;
  revealResults: boolean;
  publicParticipantList: boolean;
  hideVoteDetails: boolean;
}

interface QuorumRequirements {
  minimumVotes: number;
  minimumParticipation: number; // percentage
  timeLimit?: number; // hours
  autoCloseWhenQuorumMet: boolean;
}

interface AntiSpamConfig {
  cooldownPeriod: number; // hours between votes
  verificationRequired: boolean;
  suspiciousPatternDetection: boolean;
  voteLimitPerUser?: number;
}

interface CommunityVote {
  id: string;
  sessionId: string;
  userId: string;
  criteriaVotes: CriteriaVote[];
  overallRating?: number;
  comments?: string;
  timestamp: Date;
  ipAddress?: string;
  userAgent?: string;
  verified: boolean;
  weight: number;
}

interface CriteriaVote {
  criterionId: string;
  value: any; // Could be number, string, array depending on criteria type
  confidence?: number;
  justification?: string;
}

interface VotingResults {
  sessionId: string;
  totalVotes: number;
  uniqueParticipants: number;
  quorumMet: boolean;
  criteriaResults: CriteriaResult[];
  overallScore: number;
  confidence: number;
  distribution: VoteDistribution;
  metadata: ResultsMetadata;
}

interface CriteriaResult {
  criterionId: string;
  name: string;
  average: number;
  median: number;
  mode?: any;
  distribution: ValueDistribution[];
  participation: number;
  weight: number;
  weightedScore: number;
}

interface ValueDistribution {
  value: any;
  count: number;
  percentage: number;
}

interface VoteDistribution {
  byRating: Record<number, number>;
  byTime: Record<string, number>;
  byUserType: Record<string, number>;
  byReputation: ReputationRange[];
}

interface ReputationRange {
  range: string;
  count: number;
  averageRating: number;
}

interface ReputationScore {
  userId: string;
  overallScore: number;
  components: ReputationComponent[];
  level: ReputationLevel;
  badge: string[];
  history: ReputationHistory[];
  calculatedAt: Date;
}

enum ReputationLevel {
  NEWCOMER = 'newcomer',
  CONTRIBUTOR = 'contribor',
  EXPERT = 'expert',
  MASTER = 'master',
  LEGENDARY = 'legendary',
}

interface ReputationComponent {
  type: ReputationComponentType;
  score: number;
  weight: number;
  details: Record<string, any>;
}

enum ReputationComponentType {
  VOTE_PARTICIPATION = 'vote_participation',
  VOTE_QUALITY = 'vote_quality',
  CONSENSUS_ALIGNMENT = 'consensus_alignment',
  COMMUNITY_CONTRIBUTION = 'community_contribution',
  VERIFICATION_STATUS = 'verification_status',
}

interface ReputationHistory {
  date: Date;
  type: string;
  change: number;
  reason: string;
  context: Record<string, any>;
}

interface VotingHistoryFilter {
  dateRange?: {
    startDate: Date;
    endDate: Date;
  };
  sessionStatus?: VotingStatus[];
  minReputation?: number;
  limit?: number;
  offset?: number;
}

interface VotingSessionMetadata {
  taskInfo: TaskInfo;
  executionInfo: ExecutionInfo;
  moderatorNotes?: string;
  flags: VotingFlag[];
}

interface TaskInfo {
  taskId: string;
  name: string;
  category: string;
  difficulty: string;
  type: string;
}

interface ExecutionInfo {
  executionId: string;
  providerId: string;
  modelId: string;
  responsePreview: string;
  automatedScore?: number;
}

interface VotingFlag {
  type: FlagType;
  reason: string;
  reportedBy: string;
  timestamp: Date;
  resolved: boolean;
}

enum FlagType {
  SPAM = 'spam',
  BIASED = 'biased',
  INAPPROPRIATE = 'inappropriate',
  LOW_QUALITY = 'low_quality',
  MANIPULATION = 'manipulation',
}

interface ResultsMetadata {
  calculationMethod: string;
  confidenceInterval: [number, number];
  statisticalSignificance: boolean;
  outlierVotes: number;
  removedVotes: number;
  processingTime: number;
}
```

**Community Voting Implementation:**

```typescript
class CommunityVotingService implements CommunityVotingService {
  constructor(
    private votingRepository: VotingRepository,
    private userRepository: UserRepository,
    private reputationService: ReputationService,
    private moderationService: ModerationService,
    private analyticsService: AnalyticsService,
    private notificationService: NotificationService
  ) {}

  async openVoting(executionId: string, votingConfig: VotingConfig): Promise<VotingSession> {
    // Validate execution
    const execution = await this.getExecutionForVoting(executionId);
    if (!execution) {
      throw new Error(`Execution ${executionId} not found or not eligible for voting`);
    }

    // Validate voting config
    await this.validateVotingConfig(votingConfig);

    // Create voting session
    const session: VotingSession = {
      id: `voting_${Date.now()}`,
      executionId,
      config: votingConfig,
      status: VotingStatus.OPEN,
      createdAt: new Date(),
      closesAt: votingConfig.quorum.timeLimit
        ? new Date(Date.now() + votingConfig.quorum.timeLimit * 60 * 60 * 1000)
        : undefined,
      participantCount: 0,
      voteCount: 0,
      metadata: await this.buildVotingSessionMetadata(execution),
    };

    await this.votingRepository.createSession(session);

    // Notify eligible users
    await this.notifyEligibleUsers(session);

    return session;
  }

  async submitVote(vote: CommunityVote): Promise<void> {
    // Validate session
    const session = await this.votingRepository.getSession(vote.sessionId);
    if (!session || session.status !== VotingStatus.OPEN) {
      throw new Error('Voting session is not open');
    }

    // Validate user eligibility
    await this.validateUserEligibility(vote.userId, session);

    // Check for duplicate votes
    const existingVote = await this.votingRepository.getUserVote(vote.sessionId, vote.userId);
    if (existingVote && !session.config.eligibility.allowMultipleVotes) {
      throw new Error('User has already voted in this session');
    }

    // Validate vote content
    await this.validateVoteContent(vote, session.config);

    // Apply anti-spam checks
    await this.performAntiSpamChecks(vote, session);

    // Calculate vote weight based on reputation
    vote.weight = await this.calculateVoteWeight(vote.userId);

    // Save vote
    await this.votingRepository.saveVote(vote);

    // Update session statistics
    await this.updateSessionStatistics(session.id);

    // Check if quorum is met
    await this.checkQuorum(session.id);
  }

  async getVotingSession(sessionId: string): Promise<VotingSession> {
    const session = await this.votingRepository.getSession(sessionId);
    if (!session) {
      throw new Error(`Voting session ${sessionId} not found`);
    }

    // Include current results if session is closed
    if (session.status === VotingStatus.CLOSED) {
      session.currentResults = await this.getVotingResults(sessionId);
    }

    return session;
  }

  async getVotingResults(sessionId: string): Promise<VotingResults> {
    const session = await this.votingRepository.getSession(sessionId);
    if (!session) {
      throw new Error(`Voting session ${sessionId} not found`);
    }

    const votes = await this.votingRepository.getSessionVotes(sessionId);
    const validVotes = votes.filter((v) => v.verified);

    // Calculate results for each criterion
    const criteriaResults: CriteriaResult[] = [];

    for (const criterion of session.config.criteria) {
      const result = await this.calculateCriterionResult(criterion, validVotes);
      criteriaResults.push(result);
    }

    // Calculate overall score
    const overallScore = this.calculateOverallScore(
      criteriaResults,
      session.config.weightDistribution
    );

    // Calculate confidence
    const confidence = this.calculateConfidence(validVotes.length, session.config.quorum);

    // Build distribution analysis
    const distribution = await this.analyzeVoteDistribution(validVotes, session);

    // Check quorum
    const quorumMet = this.checkQuorumRequirements(validVotes, session.config.quorum);

    const results: VotingResults = {
      sessionId,
      totalVotes: validVotes.length,
      uniqueParticipants: new Set(validVotes.map((v) => v.userId)).size,
      quorumMet,
      criteriaResults,
      overallScore,
      confidence,
      distribution,
      metadata: {
        calculationMethod: 'weighted_average',
        confidenceInterval: this.calculateConfidenceInterval(overallScore, validVotes.length),
        statisticalSignificance: validVotes.length >= 30, // Simplified
        outlierVotes: votes.filter((v) => !v.verified).length,
        removedVotes: 0,
        processingTime: Date.now(),
      },
    };

    return results;
  }

  async closeVoting(sessionId: string): Promise<VotingResults> {
    const session = await this.votingRepository.getSession(sessionId);
    if (!session) {
      throw new Error(`Voting session ${sessionId} not found`);
    }

    if (session.status !== VotingStatus.OPEN) {
      throw new Error('Voting session is already closed');
    }

    // Update session status
    await this.votingRepository.updateSessionStatus(sessionId, VotingStatus.CLOSED);

    // Calculate final results
    const results = await this.getVotingResults(sessionId);

    // Update session with results
    await this.votingRepository.updateSessionResults(sessionId, results);

    // Update reputation scores for participants
    await this.updateParticipantReputation(sessionId, results);

    // Notify participants
    await this.notifyVotingClosed(session, results);

    return results;
  }

  async getUserVotingHistory(
    userId: string,
    filter?: VotingHistoryFilter
  ): Promise<CommunityVote[]> {
    return await this.votingRepository.getUserVotes(userId, filter);
  }

  async calculateReputationScore(userId: string): Promise<ReputationScore> {
    const user = await this.getUser(userId);
    if (!user) {
      throw new Error(`User ${userId} not found`);
    }

    // Get user's voting history
    const votes = await this.votingRepository.getUserVotes(userId);

    // Calculate reputation components
    const components: ReputationComponent[] = [];

    // Vote participation component
    const participationScore = this.calculateParticipationScore(votes);
    components.push({
      type: ReputationComponentType.VOTE_PARTICIPATION,
      score: participationScore,
      weight: 0.2,
      details: { totalVotes: votes.length },
    });

    // Vote quality component
    const qualityScore = await this.calculateVoteQualityScore(userId, votes);
    components.push({
      type: ReputationComponentType.VOTE_QUALITY,
      score: qualityScore,
      weight: 0.3,
      details: { averageAlignment: qualityScore },
    });

    // Consensus alignment component
    const alignmentScore = await this.calculateConsensusAlignment(userId, votes);
    components.push({
      type: ReputationComponentType.CONSENSUS_ALIGNMENT,
      score: alignmentScore,
      weight: 0.3,
      details: { consensusRate: alignmentScore },
    });

    // Community contribution component
    const contributionScore = await this.calculateContributionScore(userId);
    components.push({
      type: ReputationComponentType.COMMUNITY_CONTRIBUTION,
      score: contributionScore,
      weight: 0.15,
      details: { contributionScore },
    });

    // Verification status component
    const verificationScore = await this.calculateVerificationScore(userId);
    components.push({
      type: ReputationComponentType.VERIFICATION_STATUS,
      score: verificationScore,
      weight: 0.05,
      details: { verificationLevel: verificationScore },
    });

    // Calculate overall score
    const overallScore = components.reduce(
      (sum, component) => sum + component.score * component.weight,
      0
    );

    // Determine level and badges
    const level = this.determineReputationLevel(overallScore);
    const badges = await this.calculateBadges(userId, components);

    // Get reputation history
    const history = await this.reputationService.getReputationHistory(userId);

    return {
      userId,
      overallScore: Math.round(overallScore),
      components,
      level,
      badges,
      history,
      calculatedAt: new Date(),
    };
  }

  private async validateVotingConfig(config: VotingConfig): Promise<void> {
    // Validate criteria
    if (!config.criteria || config.criteria.length === 0) {
      throw new Error('At least one voting criterion is required');
    }

    let totalWeight = 0;
    for (const criterion of config.criteria) {
      if (!criterion.name || !criterion.type) {
        throw new Error('Each criterion must have name and type');
      }

      totalWeight += criterion.weight;
    }

    if (Math.abs(totalWeight - 1.0) > 0.01) {
      throw new Error('Criterion weights must sum to 1.0');
    }

    // Validate quorum requirements
    if (config.quorum.minimumVotes < 1) {
      throw new Error('Minimum votes must be at least 1');
    }

    if (config.quorum.minimumParticipation <= 0 || config.quorum.minimumParticipation > 1) {
      throw new Error('Minimum participation must be between 0 and 1');
    }
  }

  private async validateUserEligibility(userId: string, session: VotingSession): Promise<void> {
    const user = await this.getUser(userId);
    if (!user) {
      throw new Error('User not found');
    }

    const reputation = await this.calculateReputationScore(userId);

    // Check minimum reputation
    if (reputation.overallScore < session.config.eligibility.minReputation) {
      throw new Error('User reputation is too low to participate');
    }

    // Check required verifications
    for (const verification of session.config.eligibility.requiredVerifications) {
      if (!user.verifications.includes(verification)) {
        throw new Error(`User missing required verification: ${verification}`);
      }
    }

    // Check if user is excluded (creator, etc.)
    if (session.config.eligibility.excludeCreators) {
      const execution = await this.getExecutionForVoting(session.executionId);
      const task = await this.getTask(execution.taskId);

      if (task.createdBy === userId) {
        throw new Error('Task creators cannot vote on their own tasks');
      }
    }
  }

  private async validateVoteContent(vote: CommunityVote, config: VotingConfig): Promise<void> {
    // Validate criteria votes
    for (const criteriaVote of vote.criteriaVotes) {
      const criterion = config.criteria.find((c) => c.id === criteriaVote.criterionId);
      if (!criterion) {
        throw new Error(`Invalid criterion: ${criteriaVote.criterionId}`);
      }

      // Validate based on criterion type
      this.validateCriteriaVote(criteriaVote, criterion);
    }

    // Check for required criteria
    const requiredCriteria = config.criteria.filter((c) => c.required);
    const providedCriteria = vote.criteriaVotes.map((v) => v.criterionId);

    for (const required of requiredCriteria) {
      if (!providedCriteria.includes(required.id)) {
        throw new Error(`Required criterion missing: ${required.id}`);
      }
    }
  }

  private validateCriteriaVote(criteriaVote: CriteriaVote, criterion: VotingCriteria): void {
    switch (criterion.type) {
      case CriteriaType.RATING:
        if (
          typeof criteriaVote.value !== 'number' ||
          criteriaVote.value < 0 ||
          criteriaVote.value > 100
        ) {
          throw new Error('Rating must be a number between 0 and 100');
        }
        break;

      case CriteriaType.CHOICE:
        if (!criterion.options || !criterion.options.find((o) => o.id === criteriaVote.value)) {
          throw new Error('Invalid choice selected');
        }
        break;

      case CriteriaType.MULTIPLE_CHOICE:
        if (!Array.isArray(criteriaVote.value)) {
          throw new Error('Multiple choice must be an array');
        }
        break;

      case CriteriaType.TEXT:
        if (typeof criteriaVote.value !== 'string') {
          throw new Error('Text criteria must be a string');
        }

        if (criteriaVote.value.length > 1000) {
          throw new Error('Text response too long');
        }
        break;
    }
  }

  private async performAntiSpamChecks(vote: CommunityVote, session: VotingSession): Promise<void> {
    const config = session.config.antiSpam;

    // Check cooldown period
    if (config.cooldownPeriod > 0) {
      const recentVotes = await this.votingRepository.getUserRecentVotes(
        vote.userId,
        config.cooldownPeriod
      );

      if (recentVotes.length > 0) {
        throw new Error(`User must wait ${config.cooldownPeriod} hours between votes`);
      }
    }

    // Check vote limit
    if (config.voteLimitPerUser) {
      const totalVotes = await this.votingRepository.getUserVotesCount(vote.userId, session.id);
      if (totalVotes >= config.voteLimitPerUser) {
        throw new Error('User has exceeded maximum votes for this session');
      }
    }

    // Suspicious pattern detection
    if (config.suspiciousPatternDetection) {
      await this.checkSuspiciousPatterns(vote, session);
    }
  }

  private async checkSuspiciousPatterns(
    vote: CommunityVote,
    session: VotingSession
  ): Promise<void> {
    // Check for rapid voting patterns
    const userRecentVotes = await this.votingRepository.getUserRecentVotes(vote.userId, 1); // 1 hour
    if (userRecentVotes.length > 10) {
      // Flag for review
      await this.moderationService.flagSuspiciousActivity({
        userId: vote.userId,
        type: 'rapid_voting',
        sessionId: session.id,
        details: { voteCount: userRecentVotes.length },
      });
    }

    // Check for identical voting patterns
    const identicalVotes = await this.votingRepository.findIdenticalVotes(vote);
    if (identicalVotes.length > 0) {
      await this.moderationService.flagSuspiciousActivity({
        userId: vote.userId,
        type: 'identical_voting',
        sessionId: session.id,
        details: { identicalVoteCount: identicalVotes.length },
      });
    }
  }

  private async calculateVoteWeight(userId: string): Promise<number> {
    const reputation = await this.calculateReputationScore(userId);

    // Base weight on reputation level
    const baseWeights: Record<ReputationLevel, number> = {
      [ReputationLevel.NEWCOMER]: 0.5,
      [ReputationLevel.CONTRIBUTOR]: 0.8,
      [ReputationLevel.EXPERT]: 1.0,
      [ReputationLevel.MASTER]: 1.2,
      [ReputationLevel.LEGENDARY]: 1.5,
    };

    return baseWeights[reputation.level] || 0.5;
  }

  private async calculateCriterionResult(
    criterion: VotingCriteria,
    votes: CommunityVote[]
  ): Promise<CriteriaResult> {
    // Filter votes for this criterion
    const criterionVotes = votes
      .map((v) => v.criteriaVotes.find((cv) => cv.criterionId === criterion.id))
      .filter((cv) => cv !== undefined);

    if (criterionVotes.length === 0) {
      throw new Error(`No votes found for criterion ${criterion.id}`);
    }

    // Calculate weighted values
    const weightedValues = criterionVotes.map((cv) => ({
      value: cv.value,
      weight: cv.weight,
    }));

    // Calculate statistics based on criterion type
    let average: number;
    let median: number;
    let mode: any;
    let distribution: ValueDistribution[];

    switch (criterion.type) {
      case CriteriaType.RATING:
        const ratings = weightedValues.map((wv) => wv.value as number);
        average = this.calculateWeightedAverage(
          ratings,
          weightedValues.map((wv) => wv.weight)
        );
        median = this.calculateMedian(ratings);
        mode = this.calculateMode(ratings);
        distribution = this.calculateRatingDistribution(ratings);
        break;

      case CriteriaType.CHOICE:
        const choices = weightedValues.map((wv) => wv.value);
        average = 0; // Not applicable for choices
        median = 0;
        mode = this.calculateMode(choices);
        distribution = this.calculateChoiceDistribution(choices, criterion.options || []);
        break;

      default:
        // Handle other types
        average = 0;
        median = 0;
        mode = null;
        distribution = [];
    }

    const participation = criterionVotes.length;
    const weightedScore = average * criterion.weight;

    return {
      criterionId: criterion.id,
      name: criterion.name,
      average,
      median,
      mode,
      distribution,
      participation,
      weight: criterion.weight,
      weightedScore,
    };
  }

  private calculateWeightedAverage(values: number[], weights: number[]): number {
    if (values.length === 0 || weights.length === 0) {
      return 0;
    }

    const weightedSum = values.reduce(
      (sum, value, index) => sum + value * (weights[index] || 1),
      0
    );

    const totalWeight = weights.reduce((sum, weight) => sum + weight, 0);

    return totalWeight > 0 ? weightedSum / totalWeight : 0;
  }

  private calculateMedian(values: number[]): number {
    const sorted = [...values].sort((a, b) => a - b);
    const mid = Math.floor(sorted.length / 2);

    if (sorted.length % 2 === 0) {
      return (sorted[mid - 1] + sorted[mid]) / 2;
    } else {
      return sorted[mid];
    }
  }

  private calculateMode(values: any[]): any {
    const frequency: Record<any, number> = {};

    for (const value of values) {
      frequency[value] = (frequency[value] || 0) + 1;
    }

    let maxFrequency = 0;
    let mode = null;

    for (const [value, freq] of Object.entries(frequency)) {
      if (freq > maxFrequency) {
        maxFrequency = freq;
        mode = value;
      }
    }

    return mode;
  }

  private calculateRatingDistribution(ratings: number[]): ValueDistribution[] {
    const distribution: Record<number, number> = {};

    for (const rating of ratings) {
      const bucket = Math.round(rating / 10) * 10; // Group by 10s
      distribution[bucket] = (distribution[bucket] || 0) + 1;
    }

    const total = ratings.length;

    return Object.entries(distribution).map(([value, count]) => ({
      value: parseInt(value),
      count,
      percentage: (count / total) * 100,
    }));
  }

  private calculateChoiceDistribution(
    choices: any[],
    options: VotingOption[]
  ): ValueDistribution[] {
    const distribution: Record<any, number> = {};

    for (const choice of choices) {
      distribution[choice] = (distribution[choice] || 0) + 1;
    }

    const total = choices.length;

    return Object.entries(distribution).map(([value, count]) => ({
      value,
      count,
      percentage: (count / total) * 100,
    }));
  }

  private calculateOverallScore(
    criteriaResults: CriteriaResult[],
    weightDistribution: WeightDistribution
  ): number {
    return criteriaResults.reduce((sum, result) => sum + result.weightedScore, 0);
  }

  private calculateConfidence(voteCount: number, quorum: QuorumRequirements): number {
    // Confidence based on sample size and quorum requirements
    const minimumVotes = quorum.minimumVotes;
    const minimumParticipation = quorum.minimumParticipation;

    if (voteCount >= minimumVotes) {
      return Math.min(1.0, voteCount / (minimumVotes * 2));
    }

    return Math.min(1.0, (voteCount / minimumVotes) * 0.8);
  }

  private async analyzeVoteDistribution(
    votes: CommunityVote[],
    session: VotingSession
  ): Promise<VoteDistribution> {
    // Analyze distribution by various dimensions
    const byRating: Record<number, number> = {};
    const byTime: Record<string, number> = {};
    const byUserType: Record<string, number> = {};
    const reputationRanges: ReputationRange[] = [];

    for (const vote of votes) {
      // By rating (if applicable)
      if (vote.overallRating !== undefined) {
        byRating[vote.overallRating] = (byRating[vote.overallRating] || 0) + 1;
      }

      // By time
      const hour = vote.timestamp.getHours().toString();
      byTime[hour] = (byTime[hour] || 0) + 1;

      // By user type (based on reputation)
      const reputation = await this.calculateReputationScore(vote.userId);
      const userType = reputation.level;
      byUserType[userType] = (byUserType[userType] || 0) + 1;
    }

    // By reputation ranges
    const ranges = [
      { range: '0-20', min: 0, max: 20 },
      { range: '21-40', min: 21, max: 40 },
      { range: '41-60', min: 41, max: 60 },
      { range: '61-80', min: 61, max: 80 },
      { range: '81-100', min: 81, max: 100 },
    ];

    for (const range of ranges) {
      const count = await this.countVotesByReputationRange(votes, range.min, range.max);
      const avgRating = await this.getAverageRatingByReputationRange(votes, range.min, range.max);

      reputationRanges.push({
        range: range.range,
        count,
        averageRating: avgRating,
      });
    }

    return {
      byRating,
      byTime,
      byUserType,
      byReputation: reputationRanges,
    };
  }

  private checkQuorumRequirements(votes: CommunityVote[], quorum: QuorumRequirements): boolean {
    const voteCount = votes.length;
    const participantCount = new Set(votes.map((v) => v.userId)).size;

    const minimumVotesMet = voteCount >= quorum.minimumVotes;
    const minimumParticipationMet = participantCount >= quorum.minimumParticipation * 100; // Convert to count

    return minimumVotesMet && minimumParticipationMet;
  }

  private calculateConfidenceInterval(score: number, sampleSize: number): [number, number] {
    // Simplified confidence interval calculation
    const marginOfError = 1.96 * Math.sqrt((score * (100 - score)) / sampleSize);

    return [Math.max(0, score - marginOfError), Math.min(100, score + marginOfError)];
  }

  private async updateSessionStatistics(sessionId: string): Promise<void> {
    const votes = await this.votingRepository.getSessionVotes(sessionId);
    const participantCount = new Set(votes.map((v) => v.userId)).size;

    await this.votingRepository.updateSessionStats(sessionId, {
      participantCount,
      voteCount: votes.length,
    });
  }

  private async checkQuorum(sessionId: string): Promise<void> {
    const session = await this.votingRepository.getSession(sessionId);
    const votes = await this.votingRepository.getSessionVotes(sessionId);

    if (this.checkQuorumRequirements(votes, session.config.quorum)) {
      if (session.config.quorum.autoCloseWhenQuorumMet) {
        await this.closeVoting(sessionId);
      } else {
        // Notify that quorum is met
        await this.notificationService.notifyQuorumMet(sessionId);
      }
    }
  }

  private async updateParticipantReputation(
    sessionId: string,
    results: VotingResults
  ): Promise<void> {
    const votes = await this.votingRepository.getSessionVotes(sessionId);

    for (const vote of votes) {
      // Calculate how well this user's vote aligned with final results
      const alignmentScore = await this.calculateVoteAlignment(vote, results);

      // Update reputation based on alignment
      await this.reputationService.updateReputation(vote.userId, {
        type: ReputationComponentType.CONSENSUS_ALIGNMENT,
        score: alignmentScore,
        weight: 0.1,
        details: { sessionId, alignmentScore },
      });
    }
  }

  private async calculateVoteAlignment(
    vote: CommunityVote,
    results: VotingResults
  ): Promise<number> {
    let totalAlignment = 0;
    let criteriaCount = 0;

    for (const criteriaResult of results.criteriaResults) {
      const userVote = vote.criteriaVotes.find(
        (cv) => cv.criterionId === criteriaResult.criterionId
      );
      if (userVote) {
        const alignment = this.calculateSingleVoteAlignment(userVote, criteriaResult);
        totalAlignment += alignment;
        criteriaCount++;
      }
    }

    return criteriaCount > 0 ? totalAlignment / criteriaCount : 0;
  }

  private calculateSingleVoteAlignment(userVote: CriteriaVote, result: CriteriaResult): number {
    // Simplified alignment calculation
    if (typeof userVote.value === 'number' && typeof result.average === 'number') {
      const difference = Math.abs(userVote.value - result.average);
      return Math.max(0, 1 - difference / 100);
    }

    return 0.5; // Default for non-numeric comparisons
  }

  // Helper methods for reputation calculation
  private calculateParticipationScore(votes: CommunityVote[]): number {
    // Score based on voting frequency and consistency
    const voteCount = votes.length;

    // Diminishing returns for excessive voting
    if (voteCount <= 10) {
      return voteCount * 2;
    } else if (voteCount <= 50) {
      return 20 + (voteCount - 10) * 1.5;
    } else {
      return 65 + Math.min(voteCount - 50, 50) * 0.5;
    }
  }

  private async calculateVoteQualityScore(userId: string, votes: CommunityVote[]): Promise<number> {
    // Score based on how well user's votes align with final outcomes
    let totalQuality = 0;
    let voteCount = 0;

    for (const vote of votes) {
      const session = await this.votingRepository.getSession(vote.sessionId);
      if (session.status === VotingStatus.CLOSED) {
        const results = await this.getVotingResults(vote.sessionId);
        const alignment = await this.calculateVoteAlignment(vote, results);
        totalQuality += alignment;
        voteCount++;
      }
    }

    return voteCount > 0 ? (totalQuality / voteCount) * 100 : 0;
  }

  private async calculateConsensusAlignment(
    userId: string,
    votes: CommunityVote[]
  ): Promise<number> {
    // Similar to vote quality but with different weighting
    return await this.calculateVoteQualityScore(userId, votes);
  }

  private async calculateContributionScore(userId: string): Promise<number> {
    // Score based on other community contributions
    // This would integrate with other parts of the system
    return 50; // Placeholder
  }

  private async calculateVerificationScore(userId: string): Promise<number> {
    const user = await this.getUser(userId);

    if (!user.verifications || user.verifications.length === 0) {
      return 0;
    }

    // Score based on verification level
    const verificationLevels = ['email', 'phone', 'identity', 'expert'];
    let score = 0;

    for (const verification of verificationLevels) {
      if (user.verifications.includes(verification)) {
        score += 25;
      }
    }

    return Math.min(100, score);
  }

  private determineReputationLevel(score: number): ReputationLevel {
    if (score >= 90) return ReputationLevel.LEGENDARY;
    if (score >= 75) return ReputationLevel.MASTER;
    if (score >= 60) return ReputationLevel.EXPERT;
    if (score >= 40) return ReputationLevel.CONTRIBUTOR;
    if (score >= 20) return ReputationLevel.NEWCOMER;
    return ReputationLevel.NEWCOMER;
  }

  private async calculateBadges(
    userId: string,
    components: ReputationComponent[]
  ): Promise<string[]> {
    const badges: string[] = [];

    // Badge based on vote participation
    const participationComponent = components.find(
      (c) => c.type === ReputationComponentType.VOTE_PARTICIPATION
    );

    if (participationComponent && participationComponent.details.totalVotes >= 100) {
      badges.push('active_voter');
    }

    if (participationComponent && participationComponent.details.totalVotes >= 1000) {
      badges.push('veteran_voter');
    }

    // Badge based on vote quality
    const qualityComponent = components.find(
      (c) => c.type === ReputationComponentType.VOTE_QUALITY
    );

    if (qualityComponent && qualityComponent.details.averageAlignment >= 0.8) {
      badges.push('insightful_voter');
    }

    // Badge based on verification
    const verificationComponent = components.find(
      (c) => c.type === ReputationComponentType.VERIFICATION_STATUS
    );

    if (verificationComponent && verificationComponent.details.verificationLevel >= 75) {
      badges.push('verified_user');
    }

    return badges;
  }

  private async countVotesByReputationRange(
    votes: CommunityVote[],
    minRep: number,
    maxRep: number
  ): Promise<number> {
    let count = 0;

    for (const vote of votes) {
      const reputation = await this.calculateReputationScore(vote.userId);
      if (reputation.overallScore >= minRep && reputation.overallScore <= maxRep) {
        count++;
      }
    }

    return count;
  }

  private async getAverageRatingByReputationRange(
    votes: CommunityVote[],
    minRep: number,
    maxRep: number
  ): Promise<number> {
    let totalRating = 0;
    let count = 0;

    for (const vote of votes) {
      if (vote.overallRating === undefined) continue;

      const reputation = await this.calculateReputationScore(vote.userId);
      if (reputation.overallScore >= minRep && reputation.overallScore <= maxRep) {
        totalRating += vote.overallRating;
        count++;
      }
    }

    return count > 0 ? totalRating / count : 0;
  }

  // Placeholder methods that would be implemented with actual repositories
  private async getExecutionForVoting(executionId: string): Promise<any> {
    return { id: executionId }; // Placeholder
  }

  private async getUser(userId: string): Promise<any> {
    return { id: userId, verifications: [] }; // Placeholder
  }

  private async getTask(taskId: string): Promise<any> {
    return { id: taskId, createdBy: 'user1' }; // Placeholder
  }

  private async buildVotingSessionMetadata(execution: any): Promise<VotingSessionMetadata> {
    return {
      taskInfo: { taskId: execution.taskId, name: 'Sample Task' },
      executionInfo: { executionId: execution.id, providerId: 'provider1' },
      flags: [],
    };
  }

  private async notifyEligibleUsers(session: VotingSession): Promise<void> {
    // Implementation would notify eligible users
  }

  private async notifyVotingClosed(session: VotingSession, results: VotingResults): Promise<void> {
    // Implementation would notify participants
  }

  private async notifyQuorumMet(sessionId: string): Promise<void> {
    // Implementation would notify that quorum is met
  }
}

interface VotingRepository {
  createSession(session: VotingSession): Promise<void>;
  getSession(sessionId: string): Promise<VotingSession | null>;
  updateSessionStatus(sessionId: string, status: VotingStatus): Promise<void>;
  updateSessionResults(sessionId: string, results: VotingResults): Promise<void>;
  updateSessionStats(sessionId: string, stats: any): Promise<void>;
  saveVote(vote: CommunityVote): Promise<void>;
  getSessionVotes(sessionId: string): Promise<CommunityVote[]>;
  getUserVote(sessionId: string, userId: string): Promise<CommunityVote | null>;
  getUserVotes(userId: string, filter?: VotingHistoryFilter): Promise<CommunityVote[]>;
  getUserVotesCount(userId: string, sessionId?: string): Promise<number>;
  getUserRecentVotes(userId: string, hours: number): Promise<CommunityVote[]>;
  findIdenticalVotes(vote: CommunityVote): Promise<CommunityVote[]>;
}

interface UserRepository {
  getUser(userId: string): Promise<any>;
}

interface ReputationService {
  updateReputation(userId: string, update: any): Promise<void>;
  getReputationHistory(userId: string): Promise<ReputationHistory[]>;
}

interface ModerationService {
  flagSuspiciousActivity(flag: any): Promise<void>;
}

interface AnalyticsService {
  // Analytics service methods
}
```

#### 3. AI Self-Review System (Story 5.3)

**AI Self-Review Framework:**

```typescript
interface AISelfReviewService {
  initiateSelfReview(executionId: string, config: AISelfReviewConfig): Promise<AISelfReviewSession>;
  processSelfReview(sessionId: string): Promise<AISelfReviewResult>;
  getSelfReviewSession(sessionId: string): Promise<AISelfReviewSession>;
  validateSelfReview(sessionId: string, humanReview?: StaffReview): Promise<ValidationResult>;
  getSelfReviewHistory(executionId: string): Promise<AISelfReviewResult[]>;
  calibrateAIReviewer(
    providerId: string,
    calibrationData: CalibrationData[]
  ): Promise<CalibrationResult>;
}

interface AISelfReviewSession {
  id: string;
  executionId: string;
  config: AISelfReviewConfig;
  status: AISelfReviewStatus;
  createdAt: Date;
  startedAt?: Date;
  completedAt?: Date;
  providerId: string;
  modelId: string;
  reviewPrompts: ReviewPrompt[];
  currentStep?: number;
  progress: ReviewProgress;
  metadata: AISelfReviewMetadata;
}

enum AISelfReviewStatus {
  PENDING = 'pending',
  IN_PROGRESS = 'in_progress',
  COMPLETED = 'completed',
  FAILED = 'failed',
  VALIDATED = 'validated',
  REJECTED = 'rejected',
}

interface AISelfReviewConfig {
  providerId: string;
  modelId: string;
  reviewStrategy: ReviewStrategy;
  evaluationCriteria: AIEvaluationCriteria[];
  consistencyChecks: ConsistencyCheck[];
  biasDetection: BiasDetectionConfig;
  qualityThresholds: QualityThresholds;
  promptTemplates: PromptTemplate[];
  validationRules: ValidationRule[];
}

enum ReviewStrategy {
  COMPREHENSIVE = 'comprehensive',
  FOCUSED = 'focused',
  COMPARATIVE = 'comparative',
  ITERATIVE = 'iterative',
}

interface AIEvaluationCriteria {
  id: string;
  name: string;
  description: string;
  type: EvaluationType;
  weight: number;
  evaluationMethod: EvaluationMethod;
  scoringFunction: ScoringFunction;
  validationRules: CriterionValidationRule[];
}

enum EvaluationType {
  CORRECTNESS = 'correctness',
  COMPLETENESS = 'completeness',
  CLARITY = 'clarity',
  RELEVANCE = 'relevance',
  COHERENCE = 'coherence',
  CREATIVITY = 'creativity',
  EFFICIENCY = 'efficiency',
  SAFETY = 'safety',
}

enum EvaluationMethod {
  DIRECT_ASSESSMENT = 'direct_assessment',
  COMPARATIVE_ANALYSIS = 'comparative_analysis',
  RUBRIC_BASED = 'rubric_based',
  CHECKLIST_VERIFICATION = 'checklist_verification',
  PEER_COMPARISON = 'peer_comparison',
}

interface ScoringFunction {
  type: ScoringType;
  parameters: Record<string, any>;
  normalization: NormalizationConfig;
  confidenceCalculation: ConfidenceConfig;
}

enum ScoringType {
  NUMERIC_SCALE = 'numeric_scale',
  BINARY_CLASSIFICATION = 'binary_classification',
  MULTI_CLASS = 'multi_class',
  PROBABILITY_DISTRIBUTION = 'probability_distribution',
  CUSTOM_FUNCTION = 'custom_function',
}

interface ConsistencyCheck {
  id: string;
  name: string;
  type: ConsistencyType;
  description: string;
  checkFunction: ConsistencyFunction;
  threshold: number;
  severity: CheckSeverity;
}

enum ConsistencyType {
  INTERNAL_CONSISTENCY = 'internal_consistency',
  EXTERNAL_CONSISTENCY = 'external_consistency',
  TEMPORAL_CONSISTENCY = 'temporal_consistency',
  LOGICAL_CONSISTENCY = 'logical_consistency',
  FACTUAL_CONSISTENCY = 'factual_consistency',
}

enum CheckSeverity {
  LOW = 'low',
  MEDIUM = 'medium',
  HIGH = 'high',
  CRITICAL = 'critical',
}

interface BiasDetectionConfig {
  enabled: boolean;
  biasTypes: BiasType[];
  detectionMethods: BiasDetectionMethod[];
  mitigationStrategies: MitigationStrategy[];
  reportingThreshold: number;
}

enum BiasType {
  GENDER_BIAS = 'gender_bias',
  RACIAL_BIAS = 'racial_bias',
  AGE_BIAS = 'age_bias',
  CULTURAL_BIAS = 'cultural_bias',
  CONFIRMATION_BIAS = 'confirmation_bias',
  ANCHORING_BIAS = 'anchoring_bias',
  AVAILABILITY_BIAS = 'availability_bias',
}

interface BiasDetectionMethod {
  type: BiasDetectionType;
  parameters: Record<string, any>;
  sensitivity: number;
}

enum BiasDetectionType {
  KEYWORD_ANALYSIS = 'keyword_analysis',
  SENTIMENT_ANALYSIS = 'sentiment_analysis',
  ENTITY_ANALYSIS = 'entity_analysis',
  COMPARATIVE_ANALYSIS = 'comparative_analysis',
  STATISTICAL_ANALYSIS = 'statistical_analysis',
}

interface QualityThresholds {
  minimumConfidence: number;
  minimumCompleteness: number;
  maximumAmbiguity: number;
  consistencyThreshold: number;
  biasThreshold: number;
  overallQualityThreshold: number;
}

interface ReviewPrompt {
  id: string;
  type: PromptType;
  template: string;
  variables: PromptVariable[];
  context: PromptContext;
  expectedOutput: ExpectedOutput;
}

enum PromptType {
  EVALUATION = 'evaluation',
  COMPARISON = 'comparison',
  VALIDATION = 'validation',
  CLARIFICATION = 'clarification',
  SUMMARIZATION = 'summarization',
  CRITIQUE = 'critique',
}

interface PromptVariable {
  name: string;
  source: VariableSource;
  transformation?: VariableTransformation;
  validation: VariableValidation;
}

enum VariableSource {
  EXECUTION_RESULT = 'execution_result',
  TASK_DEFINITION = 'task_definition',
  PREVIOUS_REVIEWS = 'previous_reviews',
  GROUND_TRUTH = 'ground_truth',
  EXTERNAL_DATA = 'external_data',
}

interface PromptContext {
  includeTask: boolean;
  includeExecution: boolean;
  includePreviousReviews: boolean;
  includeGuidelines: boolean;
  includeExamples: boolean;
  customContext: Record<string, any>;
}

interface ExpectedOutput {
  format: OutputFormat;
  schema: JSONSchema;
  validationRules: OutputValidationRule[];
}

enum OutputFormat {
  JSON = 'json',
  XML = 'xml',
  PLAIN_TEXT = 'plain_text',
  STRUCTURED_TEXT = 'structured_text',
}

interface ReviewProgress {
  currentStep: number;
  totalSteps: number;
  completedSteps: string[];
  stepResults: StepResult[];
  overallProgress: number;
  estimatedTimeRemaining?: number;
}

interface StepResult {
  stepId: string;
  stepName: string;
  status: StepStatus;
  result?: any;
  confidence: number;
  duration: number;
  errors?: string[];
  warnings?: string[];
}

enum StepStatus {
  PENDING = 'pending',
  IN_PROGRESS = 'in_progress',
  COMPLETED = 'completed',
  FAILED = 'failed',
  SKIPPED = 'skipped',
}

interface AISelfReviewResult {
  id: string;
  sessionId: string;
  executionId: string;
  providerId: string;
  modelId: string;
  overallScore: number;
  criterionScores: AICriterionScore[];
  consistencyResults: ConsistencyResult[];
  biasAnalysis: BiasAnalysisResult;
  qualityMetrics: QualityMetrics;
  confidence: number;
  recommendations: AIRecommendation[];
  validationResults: ValidationResult[];
  metadata: AIResultMetadata;
  createdAt: Date;
}

interface AICriterionScore {
  criterionId: string;
  name: string;
  score: number;
  maxScore: number;
  weight: number;
  confidence: number;
  reasoning: string;
  evidence: AIEvidence[];
  subScores: SubScore[];
}

interface AIEvidence {
  type: EvidenceType;
  content: string;
  relevance: number;
  confidence: number;
  source: string;
  extractionMethod: string;
}

interface SubScore {
  name: string;
  score: number;
  weight: number;
  description: string;
}

interface ConsistencyResult {
  checkId: string;
  name: string;
  passed: boolean;
  score: number;
  details: string;
  recommendations: string[];
  severity: CheckSeverity;
}

interface BiasAnalysisResult {
  overallBiasScore: number;
  detectedBiases: DetectedBias[];
  mitigationSuggestions: MitigationSuggestion[];
  biasFreeScore: number;
  confidence: number;
}

interface DetectedBias {
  type: BiasType;
  severity: number;
  confidence: number;
  evidence: string[];
  affectedCriteria: string[];
  impact: BiasImpact;
}

interface BiasImpact {
  scoreAdjustment: number;
  confidenceAdjustment: number;
  reliabilityAdjustment: number;
}

interface MitigationSuggestion {
  type: BiasType;
  strategy: string;
  description: string;
  effectiveness: number;
  implementation: string;
}

interface QualityMetrics {
  completeness: number;
  clarity: number;
  specificity: number;
  objectivity: number;
  thoroughness: number;
  overallQuality: number;
}

interface AIRecommendation {
  type: RecommendationType;
  priority: RecommendationPriority;
  description: string;
  rationale: string;
  impact: RecommendationImpact;
  implementation: string;
}

enum RecommendationType {
  SCORE_ADJUSTMENT = 'score_adjustment',
  ADDITIONAL_REVIEW = 'additional_review',
  HUMAN_VALIDATION = 'human_validation',
  CRITERIA_REVIEW = 'criteria_review',
  BIAS_MITIGATION = 'bias_mitigation',
  QUALITY_IMPROVEMENT = 'quality_improvement',
}

enum RecommendationPriority {
  LOW = 'low',
  MEDIUM = 'medium',
  HIGH = 'high',
  CRITICAL = 'critical',
}

interface RecommendationImpact {
  scoreChange: number;
  confidenceChange: number;
  reliabilityImprovement: number;
}

interface ValidationResult {
  validatorId: string;
  validatorType: ValidatorType;
  passed: boolean;
  score: number;
  feedback: string;
  suggestions: string[];
  confidence: number;
  validatedAt: Date;
}

enum ValidatorType {
  HUMAN_REVIEWER = 'human_reviewer',
  AI_VALIDATOR = 'ai_validator',
  AUTOMATED_CHECK = 'automated_check',
  PEER_VALIDATION = 'peer_validation',
}

interface AIResultMetadata {
  processingTime: number;
  tokenUsage: TokenUsage;
  modelVersion: string;
  promptVersions: string[];
  calibrationApplied: boolean;
  externalDataSources: string[];
  errors: ProcessingError[];
  warnings: ProcessingWarning[];
}

interface TokenUsage {
  inputTokens: number;
  outputTokens: number;
  totalTokens: number;
  cost: number;
}

interface ProcessingError {
  code: string;
  message: string;
  step: string;
  severity: ErrorSeverity;
  recoverable: boolean;
}

interface ProcessingWarning {
  code: string;
  message: string;
  step: string;
  impact: string;
}

interface CalibrationData {
  executionId: string;
  groundTruthScore: number;
  groundTruthCriteria: Record<string, number>;
  context: CalibrationContext;
}

interface CalibrationContext {
  taskType: string;
  difficulty: string;
  domain: string;
  language: string;
  specialRequirements: string[];
}
```

**AI Self-Review Implementation:**

````typescript
class AISelfReviewService implements AISelfReviewService {
  constructor(
    private aiProviderRegistry: AIProviderRegistry,
    private selfReviewRepository: AISelfReviewRepository,
    private executionRepository: ExecutionRepository,
    private taskRepository: TaskRepository,
    private calibrationService: AICalibrationService,
    private biasDetectionService: BiasDetectionService,
    private consistencyChecker: ConsistencyChecker,
    private qualityAnalyzer: QualityAnalyzer,
    private promptTemplateEngine: PromptTemplateEngine
  ) {}

  async initiateSelfReview(
    executionId: string,
    config: AISelfReviewConfig
  ): Promise<AISelfReviewSession> {
    // Validate execution
    const execution = await this.executionRepository.getExecution(executionId);
    if (!execution) {
      throw new Error(`Execution ${executionId} not found`);
    }

    // Validate configuration
    await this.validateSelfReviewConfig(config);

    // Get AI provider
    const provider = this.aiProviderRegistry.getProvider(config.providerId);
    if (!provider) {
      throw new Error(`AI provider ${config.providerId} not available`);
    }

    // Build review prompts
    const reviewPrompts = await this.buildReviewPrompts(execution, config);

    // Create session
    const session: AISelfReviewSession = {
      id: `ai_review_${Date.now()}`,
      executionId,
      config,
      status: AISelfReviewStatus.PENDING,
      createdAt: new Date(),
      providerId: config.providerId,
      modelId: config.modelId,
      reviewPrompts,
      progress: {
        currentStep: 0,
        totalSteps: reviewPrompts.length,
        completedSteps: [],
        stepResults: [],
        overallProgress: 0,
      },
      metadata: {
        executionId,
        providerId: config.providerId,
        modelId: config.modelId,
        initiatedBy: 'system',
        calibrationApplied: false,
      },
    };

    await this.selfReviewRepository.createSession(session);

    return session;
  }

  async processSelfReview(sessionId: string): Promise<AISelfReviewResult> {
    const session = await this.selfReviewRepository.getSession(sessionId);
    if (!session) {
      throw new Error(`Self-review session ${sessionId} not found`);
    }

    if (session.status !== AISelfReviewStatus.PENDING) {
      throw new Error(`Session ${sessionId} is not in pending status`);
    }

    // Update session status
    session.status = AISelfReviewStatus.IN_PROGRESS;
    session.startedAt = new Date();
    await this.selfReviewRepository.updateSession(session);

    try {
      // Get execution and task data
      const execution = await this.executionRepository.getExecution(session.executionId);
      const task = await this.taskRepository.getTask(execution.taskId);

      // Get AI provider
      const provider = this.aiProviderRegistry.getProvider(session.providerId);

      // Process each review step
      const stepResults: StepResult[] = [];
      const criterionScores: AICriterionScore[] = [];
      let totalProcessingTime = 0;

      for (let i = 0; i < session.reviewPrompts.length; i++) {
        const prompt = session.reviewPrompts[i];
        session.currentStep = i;

        // Update progress
        session.progress.currentStep = i + 1;
        session.progress.overallProgress = ((i + 1) / session.reviewPrompts.length) * 100;

        const stepResult = await this.processReviewStep(prompt, execution, task, provider, session);

        stepResults.push(stepResult);
        session.progress.stepResults.push(stepResult);
        totalProcessingTime += stepResult.duration;

        // Update session progress
        await this.selfReviewRepository.updateSession(session);

        if (stepResult.status === StepStatus.FAILED) {
          throw new Error(`Review step ${prompt.id} failed: ${stepResult.errors?.join(', ')}`);
        }
      }

      // Calculate criterion scores from step results
      for (const criterion of session.config.evaluationCriteria) {
        const score = await this.calculateCriterionScore(criterion, stepResults, execution, task);
        criterionScores.push(score);
      }

      // Perform consistency checks
      const consistencyResults = await this.performConsistencyChecks(
        session.config.consistencyChecks,
        criterionScores,
        execution,
        task
      );

      // Detect bias
      const biasAnalysis = await this.performBiasDetection(
        session.config.biasDetection,
        criterionScores,
        stepResults,
        execution
      );

      // Analyze quality metrics
      const qualityMetrics = await this.analyzeQuality(
        criterionScores,
        stepResults,
        session.config.qualityThresholds
      );

      // Calculate overall score
      const overallScore = this.calculateOverallScore(criterionScores, session.config);

      // Calculate confidence
      const confidence = this.calculateConfidence(
        criterionScores,
        consistencyResults,
        qualityMetrics
      );

      // Generate recommendations
      const recommendations = await this.generateRecommendations(
        criterionScores,
        consistencyResults,
        biasAnalysis,
        qualityMetrics,
        session.config
      );

      // Create result
      const result: AISelfReviewResult = {
        id: `ai_result_${Date.now()}`,
        sessionId: session.id,
        executionId: session.executionId,
        providerId: session.providerId,
        modelId: session.modelId,
        overallScore,
        criterionScores,
        consistencyResults,
        biasAnalysis,
        qualityMetrics,
        confidence,
        recommendations,
        validationResults: [], // Will be populated during validation
        metadata: {
          processingTime: totalProcessingTime,
          tokenUsage: this.calculateTokenUsage(stepResults),
          modelVersion: session.config.modelId,
          promptVersions: session.reviewPrompts.map((p) => p.id),
          calibrationApplied: await this.isCalibrationApplied(session.providerId),
          externalDataSources: [],
          errors: stepResults
            .flatMap((sr) => sr.errors || [])
            .map((e) => ({
              code: 'STEP_ERROR',
              message: e,
              step: '',
              severity: ErrorSeverity.MEDIUM,
              recoverable: false,
            })),
          warnings: stepResults
            .flatMap((sr) => sr.warnings || [])
            .map((w) => ({
              code: 'STEP_WARNING',
              message: w,
              step: '',
              impact: 'medium',
            })),
        },
        createdAt: new Date(),
      };

      // Update session
      session.status = AISelfReviewStatus.COMPLETED;
      session.completedAt = new Date();
      await this.selfReviewRepository.updateSession(session);

      // Save result
      await this.selfReviewRepository.saveResult(result);

      return result;
    } catch (error) {
      // Update session with error
      session.status = AISelfReviewStatus.FAILED;
      session.metadata.errors = session.metadata.errors || [];
      session.metadata.errors.push({
        code: 'PROCESSING_ERROR',
        message: error.message,
        step: session.currentStep?.toString() || 'unknown',
        severity: ErrorSeverity.HIGH,
        recoverable: false,
      });

      await this.selfReviewRepository.updateSession(session);
      throw error;
    }
  }

  async getSelfReviewSession(sessionId: string): Promise<AISelfReviewSession> {
    const session = await this.selfReviewRepository.getSession(sessionId);
    if (!session) {
      throw new Error(`Self-review session ${sessionId} not found`);
    }

    return session;
  }

  async validateSelfReview(
    sessionId: string,
    humanReview?: StaffReview
  ): Promise<ValidationResult> {
    const result = await this.selfReviewRepository.getSessionResult(sessionId);
    if (!result) {
      throw new Error(`No result found for session ${sessionId}`);
    }

    const session = await this.selfReviewRepository.getSession(sessionId);

    // Perform automated validation
    const automatedValidation = await this.performAutomatedValidation(result, session.config);

    // If human review provided, perform human validation
    let humanValidation: ValidationResult | undefined;
    if (humanReview) {
      humanValidation = await this.performHumanValidation(result, humanReview);
    }

    // Combine validation results
    const combinedValidation = this.combineValidationResults(automatedValidation, humanValidation);

    // Update result with validation
    result.validationResults.push(combinedValidation);
    await this.selfReviewRepository.updateResult(result);

    // Update session status if validation passes
    if (combinedValidation.passed) {
      session.status = AISelfReviewStatus.VALIDATED;
      await this.selfReviewRepository.updateSession(session);
    } else {
      session.status = AISelfReviewStatus.REJECTED;
      await this.selfReviewRepository.updateSession(session);
    }

    return combinedValidation;
  }

  async getSelfReviewHistory(executionId: string): Promise<AISelfReviewResult[]> {
    return await this.selfReviewRepository.getExecutionResults(executionId);
  }

  async calibrateAIReviewer(
    providerId: string,
    calibrationData: CalibrationData[]
  ): Promise<CalibrationResult> {
    // Group calibration data by context
    const contextGroups = this.groupCalibrationDataByContext(calibrationData);

    const calibrationResults: ContextCalibrationResult[] = [];

    for (const [context, data] of Object.entries(contextGroups)) {
      const result = await this.calibrateForContext(providerId, context, data);
      calibrationResults.push(result);
    }

    // Calculate overall calibration score
    const overallScore =
      calibrationResults.reduce((sum, result) => sum + result.calibrationScore, 0) /
      calibrationResults.length;

    // Save calibration results
    await this.calibrationService.saveCalibrationResults(providerId, calibrationResults);

    return {
      providerId,
      overallScore,
      contextResults: calibrationResults,
      calibratedAt: new Date(),
      nextCalibrationDue: this.calculateNextCalibrationDate(overallScore),
    };
  }

  private async validateSelfReviewConfig(config: AISelfReviewConfig): Promise<void> {
    // Validate provider and model
    const provider = this.aiProviderRegistry.getProvider(config.providerId);
    if (!provider) {
      throw new Error(`Provider ${config.providerId} not found`);
    }

    // Validate evaluation criteria
    if (!config.evaluationCriteria || config.evaluationCriteria.length === 0) {
      throw new Error('At least one evaluation criterion is required');
    }

    let totalWeight = 0;
    for (const criterion of config.evaluationCriteria) {
      totalWeight += criterion.weight;
    }

    if (Math.abs(totalWeight - 1.0) > 0.01) {
      throw new Error('Criterion weights must sum to 1.0');
    }

    // Validate quality thresholds
    this.validateQualityThresholds(config.qualityThresholds);

    // Validate prompt templates
    if (!config.promptTemplates || config.promptTemplates.length === 0) {
      throw new Error('At least one prompt template is required');
    }
  }

  private async buildReviewPrompts(
    execution: TaskExecutionResult,
    config: AISelfReviewConfig
  ): Promise<ReviewPrompt[]> {
    const task = await this.taskRepository.getTask(execution.taskId);
    const prompts: ReviewPrompt[] = [];

    for (const template of config.promptTemplates) {
      const prompt = await this.promptTemplateEngine.renderPrompt(template, {
        execution,
        task,
        config,
      });

      prompts.push(prompt);
    }

    return prompts;
  }

  private async processReviewStep(
    prompt: ReviewPrompt,
    execution: TaskExecutionResult,
    task: Task,
    provider: IAIProvider,
    session: AISelfReviewSession
  ): Promise<StepResult> {
    const startTime = Date.now();

    try {
      // Build prompt context
      const context = await this.buildPromptContext(prompt, execution, task, session);

      // Create AI request
      const request: MessageRequest = {
        messages: [
          {
            role: 'system',
            content: prompt.template,
          },
          {
            role: 'user',
            content: JSON.stringify(context),
          },
        ],
        model: session.modelId,
        temperature: 0.3, // Lower temperature for consistent evaluation
        maxTokens: 2000,
      };

      // Execute AI request
      const response = await provider.sendMessage(request);
      const responseText = await this.streamToString(response);

      // Parse response
      const parsedResponse = await this.parseAIResponse(responseText, prompt.expectedOutput);

      // Validate response
      await this.validateStepResponse(parsedResponse, prompt.expectedOutput);

      const duration = Date.now() - startTime;

      return {
        stepId: prompt.id,
        stepName: prompt.type,
        status: StepStatus.COMPLETED,
        result: parsedResponse,
        confidence: this.calculateStepConfidence(parsedResponse),
        duration,
      };
    } catch (error) {
      const duration = Date.now() - startTime;

      return {
        stepId: prompt.id,
        stepName: prompt.type,
        status: StepStatus.FAILED,
        duration,
        errors: [error.message],
      };
    }
  }

  private async buildPromptContext(
    prompt: ReviewPrompt,
    execution: TaskExecutionResult,
    task: Task,
    session: AISelfReviewSession
  ): Promise<Record<string, any>> {
    const context: Record<string, any> = {};

    if (prompt.context.includeTask) {
      context.task = {
        id: task.id,
        name: task.name,
        description: task.description,
        type: task.taskType,
        difficulty: task.difficultyLevel,
        requirements: task.requirements,
        expectedOutput: task.expectedOutput,
      };
    }

    if (prompt.context.includeExecution) {
      context.execution = {
        id: execution.id,
        providerId: execution.providerId,
        modelId: execution.modelId,
        response: execution.response,
        metadata: execution.metadata,
        startTime: execution.startTime,
        endTime: execution.endTime,
        duration: execution.duration,
      };
    }

    if (prompt.context.includePreviousReviews) {
      const previousReviews = await this.getPreviousReviews(execution.id);
      context.previousReviews = previousReviews;
    }

    if (prompt.context.includeGuidelines) {
      context.guidelines = await this.getEvaluationGuidelines(task);
    }

    if (prompt.context.includeExamples) {
      context.examples = await this.getEvaluationExamples(task);
    }

    // Add custom context
    Object.assign(context, prompt.context.customContext);

    // Add evaluation criteria
    context.evaluationCriteria = session.config.evaluationCriteria;

    return context;
  }

  private async streamToString(stream: AsyncIterable<MessageChunk>): Promise<string> {
    const chunks: string[] = [];

    for await (const chunk of stream) {
      chunks.push(chunk.content);
    }

    return chunks.join('');
  }

  private async parseAIResponse(
    responseText: string,
    expectedOutput: ExpectedOutput
  ): Promise<any> {
    switch (expectedOutput.format) {
      case OutputFormat.JSON:
        return JSON.parse(responseText);

      case OutputFormat.XML:
        return this.parseXML(responseText);

      case OutputFormat.PLAIN_TEXT:
        return { text: responseText };

      case OutputFormat.STRUCTURED_TEXT:
        return this.parseStructuredText(responseText);

      default:
        throw new Error(`Unsupported output format: ${expectedOutput.format}`);
    }
  }

  private async validateStepResponse(response: any, expectedOutput: ExpectedOutput): Promise<void> {
    // Validate against schema
    if (expectedOutput.schema) {
      await this.validateAgainstSchema(response, expectedOutput.schema);
    }

    // Apply validation rules
    for (const rule of expectedOutput.validationRules) {
      await this.applyValidationRule(response, rule);
    }
  }

  private calculateStepConfidence(response: any): number {
    // Extract confidence from response if provided
    if (response.confidence !== undefined) {
      return Math.max(0, Math.min(1, response.confidence));
    }

    // Calculate confidence based on response completeness
    const completeness = this.calculateResponseCompleteness(response);
    const specificity = this.calculateResponseSpecificity(response);

    return (completeness + specificity) / 2;
  }

  private async calculateCriterionScore(
    criterion: AIEvaluationCriteria,
    stepResults: StepResult[],
    execution: TaskExecutionResult,
    task: Task
  ): Promise<AICriterionScore> {
    // Extract relevant step results
    const relevantSteps = stepResults.filter(
      (sr) => sr.result && this.isStepRelevantToCriterion(sr, criterion)
    );

    if (relevantSteps.length === 0) {
      throw new Error(`No relevant steps found for criterion ${criterion.id}`);
    }

    // Apply scoring function
    const rawScore = await this.applyScoringFunction(
      criterion.scoringFunction,
      relevantSteps,
      execution,
      task
    );

    // Normalize score
    const normalizedScore = this.normalizeScore(rawScore, criterion.scoringFunction.normalization);

    // Calculate confidence
    const confidence = this.calculateCriterionConfidence(relevantSteps, criterion);

    // Generate reasoning
    const reasoning = await this.generateCriterionReasoning(
      criterion,
      relevantSteps,
      normalizedScore
    );

    // Extract evidence
    const evidence = await this.extractCriterionEvidence(relevantSteps, criterion);

    return {
      criterionId: criterion.id,
      name: criterion.name,
      score: normalizedScore,
      maxScore: 100,
      weight: criterion.weight,
      confidence,
      reasoning,
      evidence,
      subScores: [], // Would be populated for complex criteria
    };
  }

  private async performConsistencyChecks(
    consistencyChecks: ConsistencyCheck[],
    criterionScores: AICriterionScore[],
    execution: TaskExecutionResult,
    task: Task
  ): Promise<ConsistencyResult[]> {
    const results: ConsistencyResult[] = [];

    for (const check of consistencyChecks) {
      const result = await this.consistencyChecker.performCheck(
        check,
        criterionScores,
        execution,
        task
      );

      results.push(result);
    }

    return results;
  }

  private async performBiasDetection(
    biasConfig: BiasDetectionConfig,
    criterionScores: AICriterionScore[],
    stepResults: StepResult[],
    execution: TaskExecutionResult
  ): Promise<BiasAnalysisResult> {
    if (!biasConfig.enabled) {
      return {
        overallBiasScore: 0,
        detectedBiases: [],
        mitigationSuggestions: [],
        biasFreeScore: criterionScores.reduce((sum, cs) => sum + cs.score * cs.weight, 0),
        confidence: 0.8,
      };
    }

    return await this.biasDetectionService.analyzeBias(
      biasConfig,
      criterionScores,
      stepResults,
      execution
    );
  }

  private async analyzeQuality(
    criterionScores: AICriterionScore[],
    stepResults: StepResult[],
    thresholds: QualityThresholds
  ): Promise<QualityMetrics> {
    return await this.qualityAnalyzer.analyzeQuality(criterionScores, stepResults, thresholds);
  }

  private calculateOverallScore(
    criterionScores: AICriterionScore[],
    config: AISelfReviewConfig
  ): number {
    return criterionScores.reduce((sum, score) => sum + score.score * score.weight, 0);
  }

  private calculateConfidence(
    criterionScores: AICriterionScore[],
    consistencyResults: ConsistencyResult[],
    qualityMetrics: QualityMetrics
  ): number {
    // Base confidence from criterion scores
    const criterionConfidence = criterionScores.reduce(
      (sum, score) => sum + score.confidence * score.weight,
      0
    );

    // Adjust for consistency
    const consistencyScore =
      consistencyResults.length > 0
        ? consistencyResults.reduce((sum, result) => sum + (result.passed ? 1 : 0), 0) /
          consistencyResults.length
        : 1.0;

    // Adjust for quality
    const qualityScore = qualityMetrics.overallQuality / 100;

    // Weighted combination
    return criterionConfidence * 0.5 + consistencyScore * 0.3 + qualityScore * 0.2;
  }

  private async generateRecommendations(
    criterionScores: AICriterionScore[],
    consistencyResults: ConsistencyResult[],
    biasAnalysis: BiasAnalysisResult,
    qualityMetrics: QualityMetrics,
    config: AISelfReviewConfig
  ): Promise<AIRecommendation[]> {
    const recommendations: AIRecommendation[] = [];

    // Quality-based recommendations
    if (qualityMetrics.overallQuality < config.qualityThresholds.overallQualityThreshold) {
      recommendations.push({
        type: RecommendationType.QUALITY_IMPROVEMENT,
        priority: RecommendationPriority.HIGH,
        description: 'Overall quality metrics are below threshold',
        rationale: `Quality score: ${qualityMetrics.overallQuality}, threshold: ${config.qualityThresholds.overallQualityThreshold}`,
        impact: {
          scoreChange: 0,
          confidenceChange: -0.2,
          reliabilityImprovement: 0.1,
        },
        implementation: 'Review and improve evaluation criteria and prompts',
      });
    }

    // Consistency-based recommendations
    const failedConsistencyChecks = consistencyResults.filter((r) => !r.passed);
    if (failedConsistencyChecks.length > 0) {
      recommendations.push({
        type: RecommendationType.ADDITIONAL_REVIEW,
        priority: RecommendationPriority.MEDIUM,
        description: 'Some consistency checks failed',
        rationale: `${failedConsistencyChecks.length} consistency checks failed`,
        impact: {
          scoreChange: 0,
          confidenceChange: -0.1,
          reliabilityImprovement: 0.15,
        },
        implementation: 'Perform additional human review to resolve inconsistencies',
      });
    }

    // Bias-based recommendations
    if (biasAnalysis.overallBiasScore > config.qualityThresholds.biasThreshold) {
      recommendations.push({
        type: RecommendationType.BIAS_MITIGATION,
        priority: RecommendationPriority.HIGH,
        description: 'Significant bias detected in evaluation',
        rationale: `Bias score: ${biasAnalysis.overallBiasScore}`,
        impact: {
          scoreChange: -5,
          confidenceChange: -0.3,
          reliabilityImprovement: 0.2,
        },
        implementation: 'Apply bias mitigation strategies and re-evaluate',
      });
    }

    // Confidence-based recommendations
    const avgConfidence =
      criterionScores.reduce((sum, cs) => sum + cs.confidence, 0) / criterionScores.length;
    if (avgConfidence < config.qualityThresholds.minimumConfidence) {
      recommendations.push({
        type: RecommendationType.HUMAN_VALIDATION,
        priority: RecommendationPriority.MEDIUM,
        description: 'Low confidence in evaluation results',
        rationale: `Average confidence: ${avgConfidence}`,
        impact: {
          scoreChange: 0,
          confidenceChange: 0.2,
          reliabilityImprovement: 0.25,
        },
        implementation: 'Require human validation to increase confidence',
      });
    }

    return recommendations;
  }

  // Helper methods (simplified implementations)
  private validateQualityThresholds(thresholds: QualityThresholds): void {
    if (thresholds.minimumConfidence < 0 || thresholds.minimumConfidence > 1) {
      throw new Error('Minimum confidence must be between 0 and 1');
    }
    // Additional validations...
  }

  private async getPreviousReviews(executionId: string): Promise<any[]> {
    // Would fetch previous reviews from repository
    return [];
  }

  private async getEvaluationGuidelines(task: Task): Promise<any> {
    // Would fetch evaluation guidelines
    return {};
  }

  private async getEvaluationExamples(task: Task): Promise<any[]> {
    // Would fetch evaluation examples
    return [];
  }

  private parseXML(xmlText: string): any {
    // XML parsing implementation
    return {};
  }

  private parseStructuredText(text: string): any {
    // Structured text parsing implementation
    return { text };
  }

  private async validateAgainstSchema(data: any, schema: JSONSchema): Promise<void> {
    // Schema validation implementation
  }

  private async applyValidationRule(data: any, rule: OutputValidationRule): Promise<void> {
    // Validation rule implementation
  }

  private calculateResponseCompleteness(response: any): number {
    // Completeness calculation implementation
    return 0.8;
  }

  private calculateResponseSpecificity(response: any): number {
    // Specificity calculation implementation
    return 0.7;
  }

  private isStepRelevantToCriterion(step: StepResult, criterion: AIEvaluationCriteria): boolean {
    // Relevance checking implementation
    return true;
  }

  private async applyScoringFunction(
    scoringFunction: ScoringFunction,
    steps: StepResult[],
    execution: TaskExecutionResult,
    task: Task
  ): Promise<number> {
    // Scoring function implementation
    return 75;
  }

  private normalizeScore(score: number, normalization: NormalizationConfig): number {
    // Score normalization implementation
    return Math.max(0, Math.min(100, score));
  }

  private calculateCriterionConfidence(
    steps: StepResult[],
    criterion: AIEvaluationCriteria
  ): number {
    // Confidence calculation implementation
    return 0.8;
  }

  private async generateCriterionReasoning(
    criterion: AIEvaluationCriteria,
    steps: StepResult[],
    score: number
  ): Promise<string> {
    // Reasoning generation implementation
    return `Criterion ${criterion.name} scored ${score} based on evaluation of ${steps.length} steps.`;
  }

  private async extractCriterionEvidence(
    steps: StepResult[],
    criterion: AIEvaluationCriteria
  ): Promise<AIEvidence[]> {
    // Evidence extraction implementation
    return [];
  }

  private calculateTokenUsage(stepResults: StepResult[]): TokenUsage {
    // Token usage calculation implementation
    return {
      inputTokens: 1000,
      outputTokens: 500,
      totalTokens: 1500,
      cost: 0.01,
    };
  }

  private async isCalibrationApplied(providerId: string): Promise<boolean> {
    // Check if calibration is applied for provider
    return false;
  }

  private async performAutomatedValidation(
    result: AISelfReviewResult,
    config: AISelfReviewConfig
  ): Promise<ValidationResult> {
    // Automated validation implementation
    return {
      validatorId: 'automated_validator',
      validatorType: ValidatorType.AUTOMATED_CHECK,
      passed: true,
      score: 0.9,
      feedback: 'Automated validation passed',
      suggestions: [],
      confidence: 0.95,
      validatedAt: new Date(),
    };
  }

  private async performHumanValidation(
    result: AISelfReviewResult,
    humanReview: StaffReview
  ): Promise<ValidationResult> {
    // Human validation implementation
    return {
      validatorId: humanReview.reviewerId,
      validatorType: ValidatorType.HUMAN_REVIEWER,
      passed: Math.abs(result.overallScore - humanReview.overallScore) < 10,
      score: 1 - Math.abs(result.overallScore - humanReview.overallScore) / 100,
      feedback: 'Human validation completed',
      suggestions: [],
      confidence: humanReview.confidence,
      validatedAt: new Date(),
    };
  }

  private combineValidationResults(
    automated: ValidationResult,
    human?: ValidationResult
  ): ValidationResult {
    if (!human) {
      return automated;
    }

    // Combine automated and human validation results
    const combinedScore = (automated.score + human.score) / 2;
    const combinedConfidence = (automated.confidence + human.confidence) / 2;
    const combinedPassed = automated.passed && human.passed;

    return {
      validatorId: 'combined_validator',
      validatorType: ValidatorType.PEER_VALIDATION,
      passed: combinedPassed,
      score: combinedScore,
      feedback: `Combined validation: automated=${automated.score}, human=${human.score}`,
      suggestions: [...automated.suggestions, ...human.suggestions],
      confidence: combinedConfidence,
      validatedAt: new Date(),
    };
  }

  private groupCalibrationDataByContext(
    data: CalibrationData[]
  ): Record<string, CalibrationData[]> {
    // Group calibration data by context
    const groups: Record<string, CalibrationData[]> = {};

    for (const item of data) {
      const contextKey = `${item.context.taskType}_${item.context.difficulty}_${item.context.domain}`;
      if (!groups[contextKey]) {
        groups[contextKey] = [];
      }
      groups[contextKey].push(item);
    }

    return groups;
  }

  private async calibrateForContext(
    providerId: string,
    context: string,
    data: CalibrationData[]
  ): Promise<ContextCalibrationResult> {
    // Context-specific calibration implementation
    return {
      context,
      calibrationScore: 0.85,
      sampleSize: data.length,
      meanError: 5.2,
      standardDeviation: 8.1,
      calibrationFactors: {},
    };
  }

  private calculateNextCalibrationDate(calibrationScore: number): Date {
    let daysUntilNextCalibration: number;

    if (calibrationScore >= 0.9) {
      daysUntilNextCalibration = 90; // 3 months
    } else if (calibrationScore >= 0.8) {
      daysUntilNextCalibration = 60; // 2 months
    } else if (calibrationScore >= 0.7) {
      daysUntilNextCalibration = 30; // 1 month
    } else {
      daysUntilNextCalibration = 14; // 2 weeks
    }

    return new Date(Date.now() + daysUntilNextCalibration * 24 * 60 * 60 * 1000);
  }
}

interface AISelfReviewRepository {
  createSession(session: AISelfReviewSession): Promise<void>;
  getSession(sessionId: string): Promise<AISelfReviewSession | null>;
  updateSession(session: AISelfReviewSession): Promise<void>;
  saveResult(result: AISelfReviewResult): Promise<void>;
  updateResult(result: AISelfReviewResult): Promise<void>;
  getSessionResult(sessionId: string): Promise<AISelfReviewResult | null>;
  getExecutionResults(executionId: string): Promise<AISelfReviewResult[]>;
}

interface AICalibrationService {
  saveCalibrationResults(providerId: string, results: ContextCalibrationResult[]): Promise<void>;
}

interface ContextCalibrationResult {
  context: string;
  calibrationScore: number;
  sampleSize: number;
  meanError: number;
  standardDeviation: number;
  calibrationFactors: Record<string, number>;
}

interface CalibrationResult {
  providerId: string;
  overallScore: number;
  contextResults: ContextCalibrationResult[];
  calibratedAt: Date;
  nextCalibrationDue: Date;
}

// Additional interfaces for completeness
interface JSONSchema {
  type: string;
  properties?: Record<string, any>;
  required?: string[];
  [key: string]: any;
}

interface OutputValidationRule {
  type: string;
  parameters: Record<string, any>;
}

interface NormalizationConfig {
  method: string;
  parameters: Record<string, any>;
}

interface ConfidenceConfig {
  method: string;
  parameters: Record<string, any>;
}

interface ConsistencyFunction {
  type: string;
  parameters: Record<string, any>;
}

interface VariableTransformation {
  type: string;
  parameters: Record<string, any>;
}

interface VariableValidation {
  required: boolean;
  type: string;
  constraints: Record<string, any>;
}

interface MitigationStrategy {
  type: string;
  description: string;
  effectiveness: number;
}

enum ErrorSeverity {
  LOW = 'low',
  MEDIUM = 'medium',
  HIGH = 'high',
  CRITICAL = 'critical',
}

#### 4. Elite Panel Review System (Story 5.4)

**Elite Panel Review Framework:**

```typescript
interface ElitePanelReviewService {
  createElitePanel(executionId: string, config: ElitePanelConfig): Promise<ElitePanel>;
  assignPanelists(panelId: string, panelistIds: string[]): Promise<void>;
  initiatePanelReview(panelId: string): Promise<PanelReviewSession>;
  submitPanelistReview(review: PanelistReview): Promise<void>;
  calculatePanelConsensus(sessionId: string): Promise<PanelConsensusResult>;
  getPanelReviewSession(sessionId: string): Promise<PanelReviewSession>;
  getPanelistProfile(panelistId: string): Promise<PanelistProfile>;
  updatePanelistExpertise(panelistId: string, expertise: ExpertiseUpdate): Promise<void>;
  moderatePanelDiscussion(sessionId: string, moderation: ModerationAction): Promise<void>;
}

interface ElitePanel {
  id: string;
  name: string;
  description: string;
  config: ElitePanelConfig;
  status: PanelStatus;
  panelists: Panelist[];
  createdAt: Date;
  lastActive?: Date;
  statistics: PanelStatistics;
  metadata: PanelMetadata;
}

enum PanelStatus {
  ACTIVE = 'active',
  INACTIVE = 'inactive',
  SUSPENDED = 'suspended',
  ARCHIVED = 'archived',
}

interface ElitePanelConfig {
  panelType: PanelType;
  domain: string;
  specialization: string[];
  requiredExpertise: ExpertiseRequirement[];
  panelSize: PanelSizeConfig;
  reviewProcess: ReviewProcessConfig;
  consensusMethod: ConsensusMethod;
  qualityStandards: QualityStandards;
  compensation: CompensationConfig;
  confidentiality: ConfidentialityConfig;
}

enum PanelType {
  TECHNICAL_EXPERT = 'technical_expert',
  DOMAIN_SPECIALIST = 'domain_specialist',
  ETHICS_REVIEW = 'ethics_review',
  QUALITY_ASSURANCE = 'quality_assurance',
  ACADEMIC_PEER = 'academic_peer',
  INDUSTRY_EXPERT = 'industry_expert',
}

interface ExpertiseRequirement {
  domain: string;
  level: ExpertiseLevel;
  yearsExperience: number;
  qualifications: QualificationRequirement[];
  verified: boolean;
}

enum ExpertiseLevel {
  NOVICE = 'novice',
  INTERMEDIATE = 'intermediate',
  ADVANCED = 'advanced',
  EXPERT = 'expert',
  MASTER = 'master',
}

interface QualificationRequirement {
  type: QualificationType;
  required: boolean;
  verificationMethod: VerificationMethod;
}

enum QualificationType {
  DEGREE = 'degree',
  CERTIFICATION = 'certification',
  PUBLICATION = 'publication',
  PATENT = 'patent',
  WORK_EXPERIENCE = 'work_experience',
  PEER_RECOGNITION = 'peer_recognition',
}

enum VerificationMethod {
  SELF_DECLARED = 'self_declared',
  DOCUMENT_VERIFIED = 'document_verified',
  THIRD_PARTY_VERIFIED = 'third_party_verified',
  PEER_VERIFIED = 'peer_verified',
}

interface PanelSizeConfig {
  minimumPanelists: number;
  maximumPanelists: number;
  idealPanelists: number;
  quorumRequirement: number;
}

interface ReviewProcessConfig {
  phases: ReviewPhase[];
  timeLimits: PhaseTimeLimits;
  collaborationTools: CollaborationTool[];
  decisionMaking: DecisionMakingConfig;
}

interface ReviewPhase {
  id: string;
  name: string;
  description: string;
  type: PhaseType;
  required: boolean;
  order: number;
  parallelAllowed: boolean;
}

enum PhaseType {
  INDIVIDUAL_REVIEW = 'individual_review',
  COLLABORATIVE_DISCUSSION = 'collaborative_discussion',
  CONSENSUS_BUILDING = 'consensus_building',
  FINAL_DELIBERATION = 'final_deliberation',
  APPEAL_REVIEW = 'appeal_review',
}

interface PhaseTimeLimits {
  [phaseId: string]: {
    minimum: number; // hours
    maximum: number; // hours
    recommended: number; // hours
  };
}

interface CollaborationTool {
  type: ToolType;
  enabled: boolean;
  configuration: Record<string, any>;
}

enum ToolType {
  DISCUSSION_FORUM = 'discussion_forum',
  VIDEO_CONFERENCE = 'video_conference',
  SHARED_DOCUMENTS = 'shared_documents',
  VOTING_SYSTEM = 'voting_system',
  ANNOTATION_TOOLS = 'annotation_tools',
  VERSION_CONTROL = 'version_control',
}

interface DecisionMakingConfig {
  method: DecisionMethod;
  votingRules: VotingRules;
  consensusThreshold: number;
  tieBreaking: TieBreakingMethod;
  appealProcess: AppealProcessConfig;
}

enum DecisionMethod {
  MAJORITY_VOTE = 'majority_vote',
  SUPERMAJORITY_VOTE = 'supermajority_vote',
  UNANIMOUS_CONSENSUS = 'unanimous_consensus',
  WEIGHTED_VOTING = 'weighted_voting',
  DELPHI_METHOD = 'delphi_method',
  NOMINAL_GROUP = 'nominal_group',
}

interface VotingRules {
  minimumParticipation: number;
  abstentionHandling: AbstentionHandling;
  voteWeighting: VoteWeighting;
  transparency: VotingTransparency;
}

enum AbstentionHandling {
  COUNT_AS_NO = 'count_as_no',
  COUNT_AS_ABSTAIN = 'count_as_abstain',
  EXCLUDE_FROM_QUORUM = 'exclude_from_quorum',
}

enum VoteWeighting {
  EQUAL = 'equal',
  EXPERTISE_BASED = 'expertise_based',
  REPUTATION_BASED = 'reputation_based',
  HYBRID = 'hybrid',
}

enum VotingTransparency {
  SECRET_BALLOT = 'secret_ballot',
  PUBLIC_VOTE = 'public_vote',
  PUBLISHED_RESULTS = 'published_results',
}

enum TieBreakingMethod {
  CHAIR_DECISION = 'chair_decision',
  SENIORITY_BASED = 'seniority_based',
  EXPERTISE_BASED = 'expertise_based',
  RANDOM_SELECTION = 'random_selection',
  CONTINUE_DISCUSSION = 'continue_discussion',
}

interface AppealProcessConfig {
  allowed: boolean;
  timeLimit: number; // days
  grounds: AppealGround[];
  reviewPanel: string; // panel ID for appeals
}

enum AppealGround {
  PROCEDURAL_ERROR = 'procedural_error',
  NEW_EVIDENCE = 'new_evidence',
  CONFLICT_OF_INTEREST = 'conflict_of_interest',
  BIAS_ALLEGATION = 'bias_allegation',
  TECHNICAL_ERROR = 'technical_error',
}

enum ConsensusMethod {
  DELPHI = 'delphi',
  NOMINAL_GROUP = 'nominal_group',
  CONSENSUS_ORGANIZATION = 'consensus_organization',
  CONDORCET = 'condorcet',
  BORDA_COUNT = 'borda_count',
  WEIGHTED_CONSENSUS = 'weighted_consensus',
}

interface QualityStandards {
  minimumQualityScore: number;
  requiredCriteria: string[];
  documentationRequirements: DocumentationRequirement[];
  peerReviewRequirements: PeerReviewRequirement[];
}

interface DocumentationRequirement {
  type: DocumentationType;
  required: boolean;
  template?: string;
  validationRules: ValidationRule[];
}

enum DocumentationType {
  WRITTEN_REVIEW = 'written_review',
  SCORING_RUBRIC = 'scoring_rubric',
  EVIDENCE_ATTACHMENT = 'evidence_attachment',
  METHODOLOGY_EXPLANATION = 'methodology_explanation',
  CONFLICT_DISCLOSURE = 'conflict_disclosure',
}

interface PeerReviewRequirement {
  minimumReviewers: number;
  reviewerQualification: string;
  reviewMethod: ReviewMethod;
}

enum ReviewMethod {
  DOUBLE_BLIND = 'double_blind',
  SINGLE_BLIND = 'single_blind',
  OPEN_REVIEW = 'open_review',
  POST_PUBLICATION = 'post_publication',
}

interface CompensationConfig {
  method: CompensationMethod;
  rates: CompensationRates;
  bonuses: BonusStructure[];
  paymentTerms: PaymentTerms;
}

enum CompensationMethod {
  FIXED_FEE = 'fixed_fee',
  HOURLY_RATE = 'hourly_rate',
  PER_REVIEW = 'per_review',
  PERFORMANCE_BASED = 'performance_based',
  HYBRID = 'hybrid',
}

interface CompensationRates {
  baseRate: number;
  expertMultiplier: number;
  chairMultiplier: number;
  urgencyMultiplier: number;
}

interface BonusStructure {
  type: BonusType;
  criteria: BonusCriteria;
  amount: number;
}

enum BonusType {
  QUALITY_BONUS = 'quality_bonus',
  TIMELINESS_BONUS = 'timeliness_bonus',
  PARTICIPATION_BONUS = 'participation_bonus',
  CONSENSUS_BONUS = 'consensus_bonus',
}

interface BonusCriteria {
  threshold: number;
  measurement: string;
  timePeriod: number; // days
}

interface PaymentTerms {
  paymentSchedule: PaymentSchedule;
  currency: string;
  taxWithholding: number;
  invoicingRequirements: InvoicingRequirement[];
}

enum PaymentSchedule {
  IMMEDIATE = 'immediate',
  WEEKLY = 'weekly',
  MONTHLY = 'monthly',
  PER_PROJECT = 'per_project',
  MILESTONE_BASED = 'milestone_based',
}

interface InvoicingRequirement {
  type: string;
  required: boolean;
  format: string;
}

interface ConfidentialityConfig {
  level: ConfidentialityLevel;
  ndaRequired: boolean;
  dataHandling: DataHandlingConfig;
  disclosureRestrictions: DisclosureRestriction[];
  penalties: PenaltyConfig;
}

enum ConfidentialityLevel {
  PUBLIC = 'public',
  INTERNAL = 'internal',
  CONFIDENTIAL = 'confidential',
  SECRET = 'secret',
  TOP_SECRET = 'top_secret',
}

interface DataHandlingConfig {
  encryptionRequired: boolean;
  storageLocation: string[];
  accessControls: AccessControl[];
  retentionPeriod: number; // days
}

interface AccessControl {
  type: AccessType;
  permissions: string[];
  authentication: AuthenticationMethod;
}

enum AccessType {
  READ_ONLY = 'read_only',
  READ_WRITE = 'read_write',
  ADMIN = 'admin',
  CUSTOM = 'custom',
}

enum AuthenticationMethod {
  PASSWORD = 'password',
  TWO_FACTOR = 'two_factor',
  CERTIFICATE = 'certificate',
  BIOMETRIC = 'biometric',
}

interface DisclosureRestriction {
  type: DisclosureType;
  duration: number; // days
  exceptions: string[];
}

enum DisclosureType {
  RESULTS = 'results',
  METHODOLOGY = 'methodology',
  PARTICIPANTS = 'participants',
  DISCUSSIONS = 'discussions',
  VOTING_PATTERNS = 'voting_patterns',
}

interface PenaltyConfig {
  breachPenalty: number;
  enforcement: EnforcementMethod;
  disputeResolution: DisputeResolutionMethod;
}

enum EnforcementMethod {
  LEGAL_ACTION = 'legal_action',
  ARBITRATION = 'arbitration',
  MEDIATION = 'mediation',
  FINANCIAL_PENALTY = 'financial_penalty',
}

enum DisputeResolutionMethod {
  INTERNAL_REVIEW = 'internal_review',
  EXTERNAL_ARBITRATION = 'external_arbitration',
  LEGAL_PROCEEDINGS = 'legal_proceedings',
}

interface Panelist {
  id: string;
  userId: string;
  profile: PanelistProfile;
  status: PanelistStatus;
  joinedAt: Date;
  lastActive?: Date;
  assignments: PanelistAssignment[];
  performance: PanelistPerformance;
  compensation: PanelistCompensation;
}

enum PanelistStatus {
  ACTIVE = 'active',
  INACTIVE = 'inactive',
  SUSPENDED = 'suspended',
  ON_LEAVE = 'on_leave',
  UNDER_REVIEW = 'under_review',
}

interface PanelistProfile {
  personalInfo: PersonalInfo;
  expertise: ExpertiseArea[];
  qualifications: Qualification[];
  experience: WorkExperience[];
  publications: Publication[];
  recognitions: Recognition[];
  availability: Availability;
  preferences: PanelistPreferences;
}

interface PersonalInfo {
  name: string;
  title: string;
  organization: string;
  email: string;
  phone?: string;
  timezone: string;
  languages: string[];
}

interface ExpertiseArea {
  domain: string;
  specialization: string;
  level: ExpertiseLevel;
  yearsExperience: number;
  verified: boolean;
  verificationDate?: Date;
  endorsements: Endorsement[];
}

interface Endorsement {
  endorserId: string;
  endorserName: string;
  expertise: string;
  level: ExpertiseLevel;
  date: Date;
  comments?: string;
}

interface Qualification {
  type: QualificationType;
  title: string;
  institution: string;
  date: Date;
  verificationStatus: VerificationStatus;
  documents: Document[];
}

enum VerificationStatus {
  PENDING = 'pending',
  VERIFIED = 'verified',
  REJECTED = 'rejected',
  EXPIRED = 'expired',
}

interface Document {
  id: string;
  type: DocumentType;
  name: string;
  url: string;
  uploadedAt: Date;
  verified: boolean;
}

enum DocumentType {
  DIPLOMA = 'diploma',
  CERTIFICATE = 'certificate',
  TRANSCRIPT = 'transcript',
  RESUME = 'resume',
  PUBLICATION = 'publication',
  PORTFOLIO = 'portfolio',
}

interface WorkExperience {
  organization: string;
  position: string;
  startDate: Date;
  endDate?: Date;
  description: string;
  achievements: string[];
  verificationStatus: VerificationStatus;
}

interface Publication {
  id: string;
  title: string;
  authors: string[];
  journal: string;
  date: Date;
  doi?: string;
  type: PublicationType;
  citations: number;
  verified: boolean;
}

enum PublicationType {
  JOURNAL_ARTICLE = 'journal_article',
  CONFERENCE_PAPER = 'conference_paper',
  BOOK_CHAPTER = 'book_chapter',
  BOOK = 'book',
  PATENT = 'patent',
  TECHNICAL_REPORT = 'technical_report',
}

interface Recognition {
  type: RecognitionType;
  title: string;
  organization: string;
  date: Date;
  description: string;
  verified: boolean;
}

enum RecognitionType {
  AWARD = 'award',
  HONOR = 'honor',
  FELLOWSHIP = 'fellowship',
  GRANT = 'grant',
  PATENT = 'patent',
}

interface Availability {
  hoursPerWeek: number;
  preferredSchedule: SchedulePreference[];
  unavailablePeriods: UnavailablePeriod[];
  emergencyContact: EmergencyContact;
}

interface SchedulePreference {
  dayOfWeek: number;
  startTime: string;
  endTime: string;
  timezone: string;
}

interface UnavailablePeriod {
  startDate: Date;
  endDate: Date;
  reason: string;
}

interface EmergencyContact {
  name: string;
  relationship: string;
  phone: string;
  email: string;
}

interface PanelistPreferences {
  preferredPanelTypes: PanelType[];
  preferredDomains: string[];
  maxConcurrentAssignments: number;
  minimumCompensation: number;
  communicationMethods: CommunicationMethod[];
  specialRequirements: string[];
}

enum CommunicationMethod {
  EMAIL = 'email',
  PHONE = 'phone',
  VIDEO_CALL = 'video_call',
  INSTANT_MESSAGING = 'instant_messaging',
  FORUM = 'forum',
}

interface PanelistAssignment {
  id: string;
  panelId: string;
  sessionId: string;
  role: PanelistRole;
  assignedAt: Date;
  deadline?: Date;
  status: AssignmentStatus;
  workload: AssignmentWorkload;
}

enum PanelistRole {
  REGULAR_PANELIST = 'regular_panelist',
  PANEL_CHAIR = 'panel_chair',
  VICE_CHAIR = 'vice_chair',
  SUBJECT_MATTER_EXPERT = 'subject_matter_expert',
  METHODOLOGY_EXPERT = 'methodology_expert',
  ETHICS_OFFICER = 'ethics_officer',
}

interface AssignmentWorkload {
  estimatedHours: number;
  actualHours?: number;
  complexity: ComplexityLevel;
  urgency: UrgencyLevel;
}

enum ComplexityLevel {
  LOW = 'low',
  MEDIUM = 'medium',
  HIGH = 'high',
  VERY_HIGH = 'very_high',
}

enum UrgencyLevel {
  ROUTINE = 'routine',
  PRIORITY = 'priority',
  URGENT = 'urgent',
  CRITICAL = 'critical',
}

interface PanelistPerformance {
  metrics: PerformanceMetrics;
  reviews: PerformanceReview[];
  ratings: QualityRating[];
  feedback: PerformanceFeedback[];
  trends: PerformanceTrend[];
}

interface PerformanceMetrics {
  totalAssignments: number;
  completedAssignments: number;
  averageQualityScore: number;
  averageTimelinessScore: number;
  averageParticipationScore: number;
  consensusContribution: number;
  peerRating: number;
}

interface PerformanceReview {
  id: string;
  reviewerId: string;
  reviewDate: Date;
  assignmentId: string;
  overallRating: number;
  criteria: CriterionRating[];
  comments: string;
  recommendations: string[];
}

interface CriterionRating {
  criterion: string;
  rating: number;
  weight: number;
  comments?: string;
}

interface QualityRating {
  source: RatingSource;
  rating: number;
  weight: number;
  date: Date;
  context: string;
}

enum RatingSource {
  PEER_REVIEW = 'peer_review',
  CHAIR_EVALUATION = 'chair_evaluation',
    SYSTEM_METRICS = 'system_metrics',
    SELF_ASSESSMENT = 'self_assessment',
    EXTERNAL_VALIDATION = 'external_validation',
}

interface PerformanceFeedback {
  id: string;
  source: string;
  type: FeedbackType;
  content: string;
  date: Date;
  constructive: boolean;
  actionable: boolean;
}

enum FeedbackType {
  PRAISE = 'praise',
  CONSTRUCTIVE_CRITICISM = 'constructive_criticism',
  SUGGESTION = 'suggestion',
  COMPLAINT = 'complaint',
  CONCERN = 'concern',
}

interface PerformanceTrend {
  period: string;
  qualityScore: number;
  timelinessScore: number;
  participationScore: number;
  overallScore: number;
  change: number;
}

interface PanelistCompensation {
  totalEarned: number;
  pendingPayments: number;
  paymentHistory: PaymentRecord[];
  currentRate: number;
  bonusEligibility: BonusEligibility[];
}

interface PaymentRecord {
  id: string;
  amount: number;
  currency: string;
  date: Date;
  method: PaymentMethod;
  status: PaymentStatus;
  invoiceId?: string;
}

enum PaymentMethod {
  BANK_TRANSFER = 'bank_transfer',
  PAYPAL = 'paypal',
  CRYPTO = 'crypto',
  CHECK = 'check',
  CASH = 'cash',
}

enum PaymentStatus {
  PENDING = 'pending',
  PROCESSING = 'processing',
  COMPLETED = 'completed',
  FAILED = 'failed',
  CANCELLED = 'cancelled',
}

interface BonusEligibility {
  type: BonusType;
  eligible: boolean;
  criteria: string;
  currentProgress: number;
  requiredProgress: number;
}

interface PanelStatistics {
  totalReviews: number;
  averageQualityScore: number;
  averageConsensusScore: number;
  averageTimeToConsensus: number;
  panelistSatisfaction: number;
  clientSatisfaction: number;
  utilizationRate: number;
}

interface PanelMetadata {
  createdBy: string;
  version: string;
  tags: string[];
  externalReferences: ExternalReference[];
  auditLog: AuditEntry[];
}

interface ExternalReference {
  type: ReferenceType;
  identifier: string;
  url?: string;
  verified: boolean;
}

enum ReferenceType {
  ACCREDITATION = 'accreditation',
  CERTIFICATION = 'certification',
  PARTNERSHIP = 'partnership',
  PUBLICATION = 'publication',
  REGULATION = 'regulation',
}

interface AuditEntry {
  timestamp: Date;
  userId: string;
  action: string;
  details: string;
  ipAddress?: string;
}

interface PanelReviewSession {
  id: string;
  panelId: string;
  executionId: string;
  config: SessionConfig;
  status: SessionStatus;
  createdAt: Date;
  startedAt?: Date;
  completedAt?: Date;
  panelists: SessionPanelist[];
  phases: SessionPhase[];
  discussions: Discussion[];
  votes: Vote[];
  consensus?: PanelConsensusResult;
  metadata: SessionMetadata;
}

enum SessionStatus {
  PENDING = 'pending',
  IN_PROGRESS = 'in_progress',
  UNDER_REVIEW = 'under_review',
  COMPLETED = 'completed',
  CANCELLED = 'cancelled',
  APPEALED = 'appealed',
}

interface SessionConfig {
  reviewType: ReviewType;
  urgency: UrgencyLevel;
  complexity: ComplexityLevel;
  timeLimits: SessionTimeLimits;
  communicationRules: CommunicationRules;
  decisionRules: DecisionRules;
}

enum ReviewType {
  INITIAL_REVIEW = 'initial_review',
  APPEAL_REVIEW = 'appeal_review',
  QUALITY_ASSURANCE = 'quality_assurance',
  ETHICS_REVIEW = 'ethics_review',
  TECHNICAL_VALIDATION = 'technical_validation',
}

interface SessionTimeLimits {
  totalDuration: number; // hours
  phaseLimits: Record<string, number>;
  extensionAllowed: boolean;
  extensionProcess: string;
}

interface CommunicationRules {
  allowedMethods: CommunicationMethod[];
  responseTime: number; // hours
  confidentialityLevel: ConfidentialityLevel;
  recordingPolicy: RecordingPolicy;
}

enum RecordingPolicy {
  NO_RECORDING = 'no_recording',
  MINIMAL_RECORDING = 'minimal_recording',
  FULL_RECORDING = 'full_recording',
  TRANSCRIPT_ONLY = 'transcript_only',
}

interface DecisionRules {
  consensusMethod: ConsensusMethod;
  votingThreshold: number;
  quorumRequirement: number;
  tieBreakingMethod: TieBreakingMethod;
  appealAllowed: boolean;
}

interface SessionPanelist {
  panelistId: string;
  role: PanelistRole;
  status: PanelistSessionStatus;
  joinedAt?: Date;
  lastActive?: Date;
  contributions: PanelistContribution[];
  compensation: SessionCompensation;
}

enum PanelistSessionStatus {
  INVITED = 'invited',
  ACCEPTED = 'accepted',
  DECLINED = 'declined',
  ACTIVE = 'active',
  INACTIVE = 'inactive',
  WITHDRAWN = 'withdrawn',
}

interface PanelistContribution {
  type: ContributionType;
  timestamp: Date;
  content: string;
  quality: number;
  impact: number;
}

enum ContributionType {
  INITIAL_REVIEW = 'initial_review',
  DISCUSSION_COMMENT = 'discussion_comment',
  QUESTION = 'question',
  ANSWER = 'answer',
  VOTE = 'vote',
  CONSENSUS_BUILDING = 'consensus_building',
}

interface SessionCompensation {
  baseAmount: number;
  bonusAmount: number;
  totalAmount: number;
  currency: string;
  status: CompensationStatus;
}

enum CompensationStatus {
  ESTIMATED = 'estimated',
  APPROVED = 'approved',
  PAID = 'paid',
  DISPUTED = 'disputed',
}

interface SessionPhase {
  id: string;
  name: string;
  type: PhaseType;
  status: PhaseStatus;
  startTime?: Date;
  endTime?: Date;
  participants: string[];
  activities: PhaseActivity[];
  deliverables: PhaseDeliverable[];
}

enum PhaseStatus {
  PENDING = 'pending',
  ACTIVE = 'active',
  COMPLETED = 'completed',
  SKIPPED = 'skipped',
  FAILED = 'failed',
}

interface PhaseActivity {
  id: string;
  type: ActivityType;
  participantId: string;
  timestamp: Date;
  content: string;
  metadata: Record<string, any>;
}

enum ActivityType {
  MESSAGE_POSTED = 'message_posted',
  DOCUMENT_UPLOADED = 'document_uploaded',
  VOTE_CAST = 'vote_cast',
  REVIEW_SUBMITTED = 'review_submitted',
  QUESTION_ASKED = 'question_asked',
  ANSWER_PROVIDED = 'answer_provided',
}

interface PhaseDeliverable {
  id: string;
  type: DeliverableType;
  name: string;
  submittedBy: string;
  submittedAt: Date;
  content: any;
  status: DeliverableStatus;
}

enum DeliverableType {
  WRITTEN_REVIEW = 'written_review',
  SCORE_RUBRIC = 'score_rubric',
  EVIDENCE_PACKAGE = 'evidence_package',
  CONSENSUS_REPORT = 'consensus_report',
  MINORITY_REPORT = 'minority_report',
}

enum DeliverableStatus {
  DRAFT = 'draft',
  SUBMITTED = 'submitted',
  UNDER_REVIEW = 'under_review',
  APPROVED = 'approved',
  REJECTED = 'rejected',
}

interface Discussion {
  id: string;
  phaseId: string;
  topic: string;
  type: DiscussionType;
  status: DiscussionStatus;
  createdBy: string;
  createdAt: Date;
  participants: string[];
  messages: DiscussionMessage[];
  moderation: DiscussionModeration;
}

enum DiscussionType {
  GENERAL_DISCUSSION = 'general_discussion',
  TECHNICAL_DEBATE = 'technical_debate',
  METHODOLOGY_REVIEW = 'methodology_review',
  CONSENSUS_BUILDING = 'consensus_building',
  APPEAL_HEARING = 'appeal_hearing',
}

enum DiscussionStatus {
  ACTIVE = 'active',
  PAUSED = 'paused',
  CLOSED = 'closed',
  ARCHIVED = 'archived',
}

interface DiscussionMessage {
  id: string;
  discussionId: string;
  authorId: string;
  content: string;
  timestamp: Date;
  editedAt?: Date;
  reactions: MessageReaction[];
  moderation: MessageModeration;
}

interface MessageReaction {
  userId: string;
  type: ReactionType;
  timestamp: Date;
}

enum ReactionType {
  AGREE = 'agree',
  DISAGREE = 'disagree',
  LIKE = 'like',
  INSIGHTFUL = 'insightful',
  QUESTION = 'question',
  CLARIFICATION = 'clarification',
}

interface MessageModeration {
  flagged: boolean;
  flagReason?: string;
  flaggedBy?: string;
  flaggedAt?: Date;
  resolved: boolean;
  resolution?: string;
}

interface DiscussionModeration {
  moderated: boolean;
  moderatorId?: string;
  rules: ModerationRule[];
  violations: ModerationViolation[];
}

interface ModerationRule {
  type: ModerationRuleType;
  description: string;
  severity: ModerationSeverity;
  autoAction?: ModerationAction;
}

enum ModerationRuleType {
  NO_PERSONAL_ATTACKS = 'no_personal_attacks',
  STAY_ON_TOPIC = 'stay_on_topic',
  PROFESSIONAL_LANGUAGE = 'professional_language',
  EVIDENCE_BASED = 'evidence_based',
  CONFIDENTIALITY = 'confidentiality',
}

enum ModerationSeverity {
  WARNING = 'warning',
  TEMPORARY_SUSPENSION = 'temporary_suspension',
  PERMANENT_BAN = 'permanent_ban',
}

enum ModerationAction {
  DELETE_MESSAGE = 'delete_message',
  WARN_USER = 'warn_user',
  SUSPEND_USER = 'suspend_user',
  BAN_USER = 'ban_user',
  REPORT_ADMIN = 'report_admin',
}

interface ModerationViolation {
  id: string;
  messageId: string;
  userId: string;
  ruleType: ModerationRuleType;
  severity: ModerationSeverity;
  action: ModerationAction;
  timestamp: Date;
  resolved: boolean;
}

interface Vote {
  id: string;
  sessionId: string;
  phaseId: string;
  voterId: string;
  voteType: VoteType;
  options: VoteOption[];
  timestamp: Date;
  weight: number;
  justification?: string;
  public: boolean;
}

enum VoteType {
  APPROVAL = 'approval',
  RATING = 'rating',
  RANKING = 'ranking',
  CONSENSUS = 'consensus',
  TIE_BREAKER = 'tie_breaker',
}

interface VoteOption {
  optionId: string;
  value: any;
  weight: number;
}

interface PanelConsensusResult {
  sessionId: string;
  consensusMethod: ConsensusMethod;
  achieved: boolean;
  overallScore: number;
  confidence: number;
  agreement: AgreementMetrics;
  scores: ConsensusScore[];
  recommendations: ConsensusRecommendation[];
  minorityReports: MinorityReport[];
  metadata: ConsensusMetadata;
}

interface AgreementMetrics {
  consensusLevel: number;
  participationRate: number;
  agreementIndex: number;
  polarizationIndex: number;
  stabilityIndex: number;
}

interface ConsensusScore {
  criterionId: string;
  name: string;
  consensusScore: number;
  confidence: number;
  distribution: ScoreDistribution;
  outliers: OutlierScore[];
}

interface ScoreDistribution {
  mean: number;
  median: number;
  mode: number;
  standardDeviation: number;
  range: [number, number];
  quartiles: [number, number, number];
}

interface OutlierScore {
  panelistId: string;
  score: number;
  deviation: number;
  justification?: string;
}

interface ConsensusRecommendation {
  type: RecommendationType;
  priority: RecommendationPriority;
  description: string;
  rationale: string;
  support: RecommendationSupport;
  implementation: string;
}

interface RecommendationSupport {
  supporters: string[];
  opponents: string[];
  neutral: string[];
  strength: number;
}

interface MinorityReport {
  id: string;
  authors: string[];
  position: MinorityPosition;
  reasoning: string;
  evidence: string[];
  timestamp: Date;
}

enum MinorityPosition {
  DISSENT = 'dissent',
  CONCURRENCE = 'concurrence',
  ALTERNATIVE_CONSENSUS = 'alternative_consensus',
  PROCEDURAL_OBJECTION = 'procedural_objection',
}

interface ConsensusMetadata {
  processDuration: number;
  totalParticipants: number;
  votingRounds: number;
  discussionMessages: number;
  consensusPath: ConsensusStep[];
  qualityMetrics: ConsensusQualityMetrics;
}

interface ConsensusStep {
  step: number;
  action: string;
  timestamp: Date;
  participants: string[];
  outcome: string;
}

interface ConsensusQualityMetrics {
  thoroughness: number;
  fairness: number;
  transparency: number;
  efficiency: number;
  satisfaction: number;
}

interface PanelistReview {
  id: string;
  sessionId: string;
  panelistId: string;
  phaseId: string;
  reviewType: ReviewType;
  overallScore: number;
  criterionScores: PanelistCriterionScore[];
  narrative: ReviewNarrative;
  evidence: ReviewEvidence[];
  confidence: number;
  timestamp: Date;
  status: ReviewStatus;
}

interface PanelistCriterionScore {
  criterionId: string;
  name: string;
  score: number;
  maxScore: number;
  weight: number;
  comments: string;
  evidence: string[];
  confidence: number;
}

interface ReviewNarrative {
  summary: string;
  strengths: string[];
  weaknesses: string[];
  recommendations: string[];
  concerns: string[];
  overallAssessment: string;
}

interface ReviewEvidence {
  type: EvidenceType;
  content: string;
  source: string;
  relevance: number;
  verification: EvidenceVerification;
}

interface EvidenceVerification {
  verified: boolean;
  verifiedBy?: string;
  verifiedAt?: Date;
  method: VerificationMethod;
}

interface SessionMetadata {
  executionInfo: ExecutionInfo;
  panelInfo: PanelInfo;
  qualityMetrics: SessionQualityMetrics;
  auditTrail: AuditEntry[];
  externalReferences: ExternalReference[];
}

interface ExecutionInfo {
  executionId: string;
  taskInfo: TaskInfo;
  providerInfo: ProviderInfo;
  automatedScore?: number;
  previousReviews: PreviousReviewInfo[];
}

interface TaskInfo {
  taskId: string;
  name: string;
  type: string;
  difficulty: string;
  domain: string;
  requirements: string[];
}

interface ProviderInfo {
  providerId: string;
  modelId: string;
  version: string;
  configuration: Record<string, any>;
}

interface PreviousReviewInfo {
  type: string;
  score: number;
  reviewer: string;
  date: Date;
  confidence: number;
}

interface PanelInfo {
  panelId: string;
  panelName: string;
  panelType: PanelType;
  panelistCount: number;
  expertise: string[];
}

interface SessionQualityMetrics {
  participationRate: number;
  discussionQuality: number;
  evidenceQuality: number;
  consensusQuality: number;
  timeliness: number;
  overallQuality: number;
}

interface ExpertiseUpdate {
  areas: ExpertiseArea[];
  qualifications: Qualification[];
  experience: WorkExperience[];
  publications: Publication[];
  recognitions: Recognition[];
}

interface ModerationAction {
  type: ModerationActionType;
  target: string; // user ID or message ID
  reason: string;
  severity: ModerationSeverity;
  duration?: number; // for suspensions
  performedBy: string;
  timestamp: Date;
}

enum ModerationActionType {
  WARN_USER = 'warn_user',
  DELETE_MESSAGE = 'delete_message',
  SUSPEND_USER = 'suspend_user',
  BAN_USER = 'ban_user',
  CLOSE_DISCUSSION = 'close_discussion',
  PAUSE_SESSION = 'pause_session',
  TERMINATE_SESSION = 'terminate_session',
}

#### 5. Multi-Judge Score Aggregation (Story 5.5)

**Multi-Judge Score Aggregation Framework:**

```typescript
interface MultiJudgeAggregationService {
  aggregateScores(executionId: string, config: AggregationConfig): Promise<AggregatedScore>;
  calculateConsensus(executionId: string, method: ConsensusMethod): Promise<ConsensusResult>;
  resolveConflicts(executionId: string, conflicts: ScoreConflict[]): Promise<ConflictResolution>;
  validateAggregation(executionId: string, result: AggregatedScore): Promise<ValidationResult>;
  getAggregationHistory(executionId: string): Promise<AggregatedScore[]>;
  updateAggregationWeights(executionId: string, weights: WeightUpdate): Promise<void>;
  generateAggregationReport(executionId: string): Promise<AggregationReport>;
}

interface AggregationConfig {
  judgeTypes: JudgeTypeConfig[];
  weightingMethod: WeightingMethod;
  aggregationMethod: AggregationMethod;
  consensusMethod: ConsensusMethod;
  conflictResolution: ConflictResolutionStrategy;
  qualityThresholds: AggregationQualityThresholds;
  outlierDetection: OutlierDetectionConfig;
  confidenceCalculation: ConfidenceCalculationConfig;
}

interface JudgeTypeConfig {
  type: JudgeType;
  enabled: boolean;
  weight: number;
  minimumCount: number;
  maximumCount?: number;
  qualityThreshold: number;
  expertiseRequirements: ExpertiseRequirement[];
}

enum JudgeType {
  STAFF_REVIEWER = 'staff_reviewer',
  COMMUNITY_VOTER = 'community_voter',
  AI_SELF_REVIEW = 'ai_self_review',
  ELITE_PANELIST = 'elite_panelist',
  AUTOMATED_SCORER = 'automated_scorer',
  EXTERNAL_EXPERT = 'external_expert',
}

enum WeightingMethod {
  EQUAL_WEIGHTS = 'equal_weights',
  EXPERTISE_BASED = 'expertise_based',
  REPUTATION_BASED = 'reputation_based',
  QUALITY_BASED = 'quality_based',
  PERFORMANCE_BASED = 'performance_based',
  HYBRID = 'hybrid',
  CUSTOM = 'custom',
}

enum AggregationMethod {
  WEIGHTED_AVERAGE = 'weighted_average',
  MEDIAN = 'median',
  BAYESIAN_AGGREGATION = 'bayesian_aggregation',
  DEMPSTER_SHAFER = 'dempster_shafer',
  CONDORCET = 'condorcet',
  BORDA_COUNT = 'borda_count',
  KEMENY_YOUNG = 'kemeny_young',
  RANKED_PAIRS = 'ranked_pairs',
}

enum ConflictResolutionStrategy {
  MAJORITY_RULE = 'majority_rule',
  EXPERT_OVERRIDE = 'expert_override',
  QUALITY_WEIGHTED = 'quality_weighted',
  DELIBERATION_REQUIRED = 'deliberation_required',
  AUTOMATED_RESOLUTION = 'automated_resolution',
  ESCALATION_TO_HIGHER_AUTHORITY = 'escalation_to_higher_authority',
}

interface AggregationQualityThresholds {
  minimumJudgeCount: number;
  minimumQualityScore: number;
  maximumVariance: number;
  minimumConfidence: number;
  consensusThreshold: number;
  outlierThreshold: number;
}

interface OutlierDetectionConfig {
  enabled: boolean;
  method: OutlierDetectionMethod;
  threshold: number;
  action: OutlierAction;
  investigationRequired: boolean;
}

enum OutlierDetectionMethod {
  STANDARD_DEVIATION = 'standard_deviation',
  INTERQUARTILE_RANGE = 'interquartile_range',
  ISOLATION_FOREST = 'isolation_forest',
  LOCAL_OUTLIER_FACTOR = 'local_outlier_factor',
  DBSCAN = 'dbscan',
  Z_SCORE = 'z_score',
  MODIFIED_Z_SCORE = 'modified_z_score',
}

enum OutlierAction {
  EXCLUDE = 'exclude',
  DOWNGRADE = 'downgrade',
  FLAG_FOR_REVIEW = 'flag_for_review',
  INVESTIGATE = 'investigate',
  KEEP = 'keep',
}

interface ConfidenceCalculationConfig {
  method: ConfidenceMethod;
  factors: ConfidenceFactor[];
  weighting: ConfidenceWeighting;
  thresholds: ConfidenceThresholds;
}

enum ConfidenceMethod {
  STANDARD_ERROR = 'standard_error',
  BOOTSTRAP = 'bootstrap',
  BAYESIAN = 'bayesian',
  JENSEN_SHANNON = 'jensen_shannon',
  VARIANCE_BASED = 'variance_based',
  AGREEMENT_BASED = 'agreement_based',
}

interface ConfidenceFactor {
  type: ConfidenceFactorType;
  weight: number;
  calculation: FactorCalculation;
}

enum ConfidenceFactorType {
  JUDGE_COUNT = 'judge_count',
  JUDGE_QUALITY = 'judge_quality',
  SCORE_VARIANCE = 'score_variance',
  CONSENSUS_LEVEL = 'consensus_level',
  EXPERTISE_ALIGNMENT = 'expertise_alignment',
  HISTORICAL_ACCURACY = 'historical_accuracy',
}

interface FactorCalculation {
  method: string;
  parameters: Record<string, any>;
}

interface ConfidenceWeighting {
  linear: boolean;
  normalization: NormalizationMethod;
  aggregation: WeightAggregation;
}

enum NormalizationMethod {
  MIN_MAX = 'min_max',
  Z_SCORE = 'z_score',
  ROBUST_SCALING = 'robust_scaling',
  UNIT_VECTOR = 'unit_vector',
}

enum WeightAggregation {
  SUM = 'sum',
  AVERAGE = 'average',
  WEIGHTED_SUM = 'weighted_sum',
  MULTIPLICATIVE = 'multiplicative',
}

interface ConfidenceThresholds {
  minimum: number;
  low: number;
  medium: number;
  high: number;
  veryHigh: number;
}

interface AggregatedScore {
  id: string;
  executionId: string;
  config: AggregationConfig;
  overallScore: number;
  criterionScores: AggregatedCriterionScore[];
  confidence: ConfidenceMetrics;
  consensus: ConsensusMetrics;
  quality: AggregationQualityMetrics;
  judges: JudgeContribution[];
  conflicts: ScoreConflict[];
  outliers: OutlierScore[];
  metadata: AggregationMetadata;
  createdAt: Date;
  version: number;
}

interface AggregatedCriterionScore {
  criterionId: string;
  name: string;
  aggregatedScore: number;
  confidence: number;
  weight: number;
  distribution: ScoreDistribution;
  contributions: CriterionContribution[];
  conflicts: CriterionConflict[];
  outliers: CriterionOutlier[];
}

interface ScoreDistribution {
  mean: number;
  median: number;
  mode: number;
  standardDeviation: number;
  variance: number;
  range: [number, number];
  quartiles: [number, number, number];
  skewness: number;
  kurtosis: number;
  histogram: HistogramBin[];
}

interface HistogramBin {
  range: [number, number];
  count: number;
  percentage: number;
}

interface CriterionContribution {
  judgeId: string;
  judgeType: JudgeType;
  score: number;
  weight: number;
  contribution: number;
  quality: number;
  confidence: number;
}

interface CriterionConflict {
  type: ConflictType;
  severity: ConflictSeverity;
  description: string;
  involvedJudges: string[];
  resolution?: ConflictResolution;
}

enum ConflictType {
  SCORE_DISPARITY = 'score_disparity',
  CRITERION_MISALIGNMENT = 'criterion_misalignment',
  EXPERTISE_DISAGREEMENT = 'expertise_disagreement',
  BIAS_CONFLICT = 'bias_conflict',
  METHODOLOGICAL_DIFFERENCE = 'methodological_difference',
}

enum ConflictSeverity {
  LOW = 'low',
  MEDIUM = 'medium',
  HIGH = 'high',
  CRITICAL = 'critical',
}

interface ConflictResolution {
  method: ConflictResolutionStrategy;
  resolvedBy: string;
  resolution: string;
  timestamp: Date;
  confidence: number;
}

interface CriterionOutlier {
  judgeId: string;
  score: number;
  deviation: number;
  method: OutlierDetectionMethod;
  action: OutlierAction;
  justification: string;
}

interface ConfidenceMetrics {
  overall: number;
  byCriterion: Record<string, number>;
  byJudgeType: Record<JudgeType, number>;
  factors: ConfidenceFactorResult[];
  intervals: ConfidenceInterval[];
  stability: ConfidenceStability;
}

interface ConfidenceFactorResult {
  type: ConfidenceFactorType;
  value: number;
  weight: number;
  contribution: number;
  calculation: string;
}

interface ConfidenceInterval {
  level: number; // 0.90, 0.95, 0.99
  lower: number;
  upper: number;
  method: string;
}

interface ConfidenceStability {
  sensitivity: number;
  robustness: number;
  consistency: number;
  reliability: number;
}

interface ConsensusMetrics {
  level: number;
  strength: number;
  participation: number;
  agreement: AgreementMetrics;
  polarization: PolarizationMetrics;
  convergence: ConvergenceMetrics;
}

interface AgreementMetrics {
  overallAgreement: number;
  pairwiseAgreement: number;
  criterionAgreement: Record<string, number>;
  judgeTypeAgreement: Record<JudgeType, number>;
  agreementMatrix: AgreementMatrix;
}

interface AgreementMatrix {
  matrix: number[][];
  judges: string[];
  averageAgreement: number;
}

interface PolarizationMetrics {
  index: number;
  clusters: PolarizationCluster[];
  extremity: number;
  fragmentation: number;
}

interface PolarizationCluster {
  id: string;
  center: number;
  members: string[];
  cohesion: number;
  separation: number;
}

interface ConvergenceMetrics {
  iterations: number;
  finalScore: number;
  initialScore: number;
  convergenceRate: number;
  stability: number;
}

interface AggregationQualityMetrics {
  completeness: number;
  consistency: number;
  reliability: number;
  validity: number;
  fairness: number;
  transparency: number;
}

interface JudgeContribution {
  judgeId: string;
  judgeType: JudgeType;
  weight: number;
  contribution: number;
  quality: number;
  influence: number;
  alignment: number;
  expertise: ExpertiseAlignment;
  performance: JudgePerformance;
}

interface ExpertiseAlignment {
  domainAlignment: number;
  taskAlignment: number;
  criterionAlignment: Record<string, number>;
  overallAlignment: number;
}

interface JudgePerformance {
  accuracy: number;
  consistency: number;
  timeliness: number;
  participation: number;
  quality: number;
}

interface ScoreConflict {
  id: string;
  type: ConflictType;
  severity: ConflictSeverity;
  description: string;
  involvedJudges: ConflictParticipant[];
  criteria: string[];
  scores: ConflictScore[];
  detection: ConflictDetection;
  resolution?: ConflictResolution;
  impact: ConflictImpact;
}

interface ConflictParticipant {
  judgeId: string;
  judgeType: JudgeType;
  position: ConflictPosition;
  justification?: string;
}

enum ConflictPosition {
  HIGHER = 'higher',
  LOWER = 'lower',
  NEUTRAL = 'neutral',
}

interface ConflictScore {
  judgeId: string;
  criterionId: string;
  score: number;
  deviation: number;
  percentile: number;
}

interface ConflictDetection {
  method: ConflictDetectionMethod;
  threshold: number;
  detectedAt: Date;
  detectedBy: string;
  confidence: number;
}

enum ConflictDetectionMethod {
  STATISTICAL_OUTLIER = 'statistical_outlier',
  VARIANCE_THRESHOLD = 'variance_threshold',
  EXPERTISE_DISPARITY = 'expertise_disparity',
  BIAS_DETECTION = 'bias_detection',
  PATTERN_ANALYSIS = 'pattern_analysis',
}

interface ConflictImpact {
  scoreImpact: number;
  confidenceImpact: number;
  reliabilityImpact: number;
  fairnessImpact: number;
  overallImpact: number;
}

interface OutlierScore {
  judgeId: string;
  judgeType: JudgeType;
  score: number;
  deviation: number;
  method: OutlierDetectionMethod;
  threshold: number;
  action: OutlierAction;
  justification: string;
  impact: OutlierImpact;
}

interface OutlierImpact {
  scoreChange: number;
  confidenceChange: number;
  reliabilityChange: number;
  justification: string;
}

interface AggregationMetadata {
  processingTime: number;
  judgeCount: number;
  criterionCount: number;
  aggregationSteps: AggregationStep[];
  qualityChecks: QualityCheck[];
  auditTrail: AuditEntry[];
  version: string;
  configuration: AggregationConfig;
}

interface AggregationStep {
  step: number;
  name: string;
  method: string;
  input: any;
  output: any;
  duration: number;
  success: boolean;
  errors?: string[];
}

interface QualityCheck {
  type: QualityCheckType;
  passed: boolean;
  score: number;
  threshold: number;
  details: string;
  recommendations: string[];
}

enum QualityCheckType {
  COMPLETENESS = 'completeness',
  CONSISTENCY = 'consistency',
  RELIABILITY = 'reliability',
  VALIDITY = 'validity',
  FAIRNESS = 'fairness',
  TRANSPARENCY = 'transparency',
}

interface ConsensusResult {
  executionId: string;
  method: ConsensusMethod;
  achieved: boolean;
  consensusScore: number;
  confidence: number;
  iterations: ConsensusIteration[];
  finalState: ConsensusState;
  quality: ConsensusQuality;
  metadata: ConsensusMetadata;
}

interface ConsensusIteration {
  iteration: number;
  method: string;
  inputState: ConsensusState;
  outputState: ConsensusState;
  changes: StateChange[];
  quality: number;
  convergence: number;
  duration: number;
}

interface ConsensusState {
  scores: JudgeScore[];
  positions: JudgePosition[];
  clusters: ConsensusCluster[];
  metrics: StateMetrics;
}

interface JudgeScore {
  judgeId: string;
  judgeType: JudgeType;
  overallScore: number;
  criterionScores: Record<string, number>;
  confidence: number;
  weight: number;
}

interface JudgePosition {
  judgeId: string;
  position: number;
  clusterId?: string;
  flexibility: number;
  influence: number;
}

interface ConsensusCluster {
  id: string;
  center: number;
  members: string[];
  cohesion: number;
  weight: number;
  representative: string;
}

interface StateMetrics {
  agreement: number;
  polarization: number;
  dispersion: number;
  convergence: number;
  stability: number;
}

interface StateChange {
  type: ChangeType;
  judgeId: string;
  oldValue: any;
  newValue: any;
  reason: string;
  confidence: number;
}

enum ChangeType {
  SCORE_ADJUSTMENT = 'score_adjustment',
  WEIGHT_CHANGE = 'weight_change',
  POSITION_SHIFT = 'position_shift',
  CLUSTER_CHANGE = 'cluster_change',
}

interface ConsensusQuality {
  participation: number;
  representation: number;
  fairness: number;
  efficiency: number;
  stability: number;
  legitimacy: number;
}

interface ConsensusMetadata {
  totalIterations: number;
  convergenceTime: number;
  finalAgreement: number;
  methodParameters: Record<string, any>;
  qualityMetrics: ConsensusQualityMetrics;
}

interface ConsensusQualityMetrics {
  thoroughness: number;
  inclusiveness: number;
  deliberation: number;
  transparency: number;
  accountability: number;
}

interface ConflictResolution {
  conflictId: string;
  resolution: ConflictResolutionStrategy;
  resolvedBy: string;
  result: ResolutionResult;
  process: ResolutionProcess;
  impact: ResolutionImpact;
  timestamp: Date;
}

interface ResolutionResult {
  finalScores: Record<string, number>;
  adjustments: ScoreAdjustment[];
  explanations: ResolutionExplanation[];
  confidence: number;
  satisfaction: ResolutionSatisfaction;
}

interface ScoreAdjustment {
  judgeId: string;
  criterionId: string;
  originalScore: number;
  adjustedScore: number;
  adjustment: number;
  reason: string;
  method: string;
}

interface ResolutionExplanation {
  type: ExplanationType;
  content: string;
  evidence: string[];
  confidence: number;
}

enum ExplanationType {
  STATISTICAL = 'statistical',
  EXPERTISE_BASED = 'expertise_based',
  METHODOLOGICAL = 'methodological',
  PROCEDURAL = 'procedural',
  CONTEXTUAL = 'contextual',
}

interface ResolutionSatisfaction {
  overall: number;
  byJudge: Record<string, number>;
  byJudgeType: Record<JudgeType, number>;
  concerns: SatisfactionConcern[];
}

interface SatisfactionConcern {
  judgeId: string;
  concern: string;
  severity: ConcernSeverity;
  resolution?: string;
}

enum ConcernSeverity {
  LOW = 'low',
  MEDIUM = 'medium',
  HIGH = 'high',
  CRITICAL = 'critical',
}

interface ResolutionProcess {
  steps: ResolutionStep[];
  duration: number;
  participants: string[];
  methods: string[];
  fairness: ProcessFairness;
}

interface ResolutionStep {
  step: number;
  action: string;
  participant: string;
  duration: number;
  outcome: string;
  satisfaction: number;
}

interface ProcessFairness {
  proceduralJustice: number;
  distributiveJustice: number;
  interactionalJustice: number;
  transparency: number;
  consistency: number;
}

interface ResolutionImpact {
  scoreChanges: number;
  confidenceChanges: number;
  reliabilityChanges: number;
  relationshipImpact: number;
  futureImplications: string[];
}

interface ValidationResult {
  aggregationId: string;
  passed: boolean;
  overallScore: number;
  checks: ValidationCheck[];
  recommendations: ValidationRecommendation[];
  qualityScore: number;
  confidence: number;
  validatedAt: Date;
  validator: string;
}

interface ValidationCheck {
  type: ValidationCheckType;
  passed: boolean;
  score: number;
  threshold: number;
  details: string;
  evidence: string[];
  impact: string;
}

enum ValidationCheckType {
  STATISTICAL_VALIDITY = 'statistical_validity',
  METHODOLOGICAL_CORRECTNESS = 'methodological_correctness',
  FAIRNESS_ASSESSMENT = 'fairness_assessment',
  RELIABILITY_CHECK = 'reliability_check',
  TRANSPARENCY_EVALUATION = 'transparency_evaluation',
  COMPLETENESS_CHECK = 'completeness_check',
}

interface ValidationRecommendation {
  type: RecommendationType;
  priority: RecommendationPriority;
  description: string;
  rationale: string;
  implementation: string;
  expectedImpact: string;
}

interface WeightUpdate {
  aggregationId: string;
  updates: WeightUpdateItem[];
  reason: string;
  impact: WeightUpdateImpact;
  approvedBy: string;
  timestamp: Date;
}

interface WeightUpdateItem {
  judgeType: JudgeType;
  criterionId?: string;
  oldWeight: number;
  newWeight: number;
  change: number;
  justification: string;
}

interface WeightUpdateImpact {
  scoreChanges: Record<string, number>;
  confidenceChanges: Record<string, number>;
  qualityChanges: Record<string, number>;
  overallImpact: number;
}

interface AggregationReport {
  aggregationId: string;
  executionId: string;
  summary: ReportSummary;
  methodology: ReportMethodology;
  results: ReportResults;
  quality: ReportQuality;
  analysis: ReportAnalysis;
  recommendations: ReportRecommendation[];
  appendices: ReportAppendix[];
  generatedAt: Date;
  version: string;
}

interface ReportSummary {
  overallScore: number;
  confidence: number;
  judgeCount: number;
  consensusLevel: number;
  qualityScore: number;
  keyFindings: string[];
  executiveSummary: string;
}

interface ReportMethodology {
  aggregationMethod: AggregationMethod;
  weightingMethod: WeightingMethod;
  consensusMethod: ConsensusMethod;
  conflictResolution: ConflictResolutionStrategy;
  qualityThresholds: AggregationQualityThresholds;
  assumptions: string[];
  limitations: string[];
}

interface ReportResults {
  overallResults: OverallResults;
  criterionResults: CriterionResults[];
  judgeResults: JudgeResults[];
  consensusResults: ConsensusResults;
  conflictResults: ConflictResults[];
}

interface OverallResults {
  score: number;
  confidence: ConfidenceInterval[];
  distribution: ScoreDistribution;
  quality: AggregationQualityMetrics;
  trends: ScoreTrend[];
}

interface CriterionResults {
  criterionId: string;
  name: string;
  score: number;
  confidence: number;
  distribution: ScoreDistribution;
  contributions: CriterionContribution[];
  conflicts: CriterionConflict[];
}

interface JudgeResults {
  judgeId: string;
  judgeType: JudgeType;
  contribution: JudgeContribution;
  performance: JudgePerformance;
  alignment: ExpertiseAlignment;
  satisfaction: number;
}

interface ConsensusResults {
  achieved: boolean;
  level: number;
  strength: number;
  process: ConsensusProcess;
  quality: ConsensusQuality;
}

interface ConsensusProcess {
  iterations: number;
  duration: number;
  methods: string[];
  participation: number;
  convergence: number;
}

interface ConflictResults {
  totalConflicts: number;
  resolvedConflicts: number;
  unresolvedConflicts: number;
  conflictTypes: Record<ConflictType, number>;
  resolutionMethods: Record<ConflictResolutionStrategy, number>;
  satisfaction: number;
}

interface ReportQuality {
  overallQuality: number;
  completeness: number;
  consistency: number;
  reliability: number;
  validity: number;
  fairness: number;
  transparency: number;
  qualityChecks: QualityCheck[];
}

interface ReportAnalysis {
  statisticalAnalysis: StatisticalAnalysis;
  sensitivityAnalysis: SensitivityAnalysis;
  robustnessAnalysis: RobustnessAnalysis;
  biasAnalysis: BiasAnalysis;
  comparativeAnalysis: ComparativeAnalysis;
}

interface StatisticalAnalysis {
  descriptive: DescriptiveStatistics;
  inferential: InferentialStatistics;
  correlations: CorrelationAnalysis;
  significance: SignificanceTest[];
}

interface DescriptiveStatistics {
  mean: number;
  median: number;
  mode: number;
  standardDeviation: number;
  variance: number;
  skewness: number;
  kurtosis: number;
  range: [number, number];
  quartiles: [number, number, number];
}

interface InferentialStatistics {
  confidenceIntervals: ConfidenceInterval[];
  hypothesisTests: HypothesisTest[];
  effectSizes: EffectSize[];
}

interface HypothesisTest {
  test: string;
  statistic: number;
  pValue: number;
  significance: boolean;
  interpretation: string;
}

interface EffectSize {
  type: string;
  value: number;
  magnitude: EffectMagnitude;
  interpretation: string;
}

enum EffectMagnitude {
  NEGLIGIBLE = 'negligible',
  SMALL = 'small',
  MEDIUM = 'medium',
  LARGE = 'large',
  VERY_LARGE = 'very_large',
}

interface CorrelationAnalysis {
  correlations: Correlation[];
  patterns: CorrelationPattern[];
  clusters: CorrelationCluster[];
}

interface Correlation {
  variable1: string;
  variable2: string;
  coefficient: number;
  significance: number;
  strength: CorrelationStrength;
}

enum CorrelationStrength {
  VERY_WEAK = 'very_weak',
  WEAK = 'weak',
  MODERATE = 'moderate',
  STRONG = 'strong',
  VERY_STRONG = 'very_strong',
}

interface CorrelationPattern {
  description: string;
  variables: string[];
  pattern: string;
  significance: number;
}

interface CorrelationCluster {
  id: string;
  variables: string[];
  cohesion: number;
  interpretation: string;
}

interface SignificanceTest {
  test: string;
  statistic: number;
  pValue: number;
  alpha: number;
  significant: boolean;
  interpretation: string;
}

interface SensitivityAnalysis {
  parameters: SensitivityParameter[];
  scenarios: SensitivityScenario[];
  robustness: RobustnessMetrics;
}

interface SensitivityParameter {
  name: string;
  baseline: number;
  range: [number, number];
  impact: number;
  sensitivity: SensitivityLevel;
}

enum SensitivityLevel {
  LOW = 'low',
  MEDIUM = 'medium',
  HIGH = 'high',
  VERY_HIGH = 'very_high',
}

interface SensitivityScenario {
  name: string;
  parameters: Record<string, number>;
  result: ScenarioResult;
  impact: ScenarioImpact;
}

interface ScenarioResult {
  score: number;
  confidence: number;
  quality: number;
  changes: ResultChange[];
}

interface ResultChange {
  metric: string;
  absoluteChange: number;
  relativeChange: number;
  significance: boolean;
}

interface ScenarioImpact {
  overallImpact: number;
  criticalFactors: string[];
  recommendations: string[];
}

interface RobustnessMetrics {
  stability: number;
  consistency: number;
  reliability: number;
  resilience: number;
}

interface RobustnessAnalysis {
  methods: RobustnessMethod[];
  results: RobustnessResult[];
  comparison: RobustnessComparison;
}

interface RobustnessMethod {
  name: string;
  description: string;
  parameters: Record<string, any>;
  assumptions: string[];
}

interface RobustnessResult {
  method: string;
  score: number;
  deviation: number;
  confidence: number;
  robustness: RobustnessLevel;
}

enum RobustnessLevel {
  VERY_LOW = 'very_low',
  LOW = 'low',
  MEDIUM = 'medium',
  HIGH = 'high',
  VERY_HIGH = 'very_high',
}

interface RobustnessComparison {
  agreement: number;
  variance: number;
  consensus: number;
  recommendation: string;
}

interface BiasAnalysis {
  detectedBiases: DetectedBias[];
  biasMetrics: BiasMetrics;
  mitigation: BiasMitigation;
  impact: BiasImpact;
}

interface BiasMetrics {
  overallBias: number;
  biasByType: Record<BiasType, number>;
  biasByJudge: Record<string, number>;
  biasByCriterion: Record<string, number>;
  biasTrends: BiasTrend[];
}

interface BiasTrend {
  period: string;
  biasScore: number;
  change: number;
  significance: boolean;
}

interface BiasMitigation {
  applied: BiasMitigationStrategy[];
  effectiveness: number;
  recommendations: BiasMitigationRecommendation[];
}

interface BiasMitigationStrategy {
  type: BiasType;
  method: string;
  effectiveness: number;
  implementation: string;
}

interface BiasMitigationRecommendation {
  biasType: BiasType;
  strategy: string;
  priority: RecommendationPriority;
  expectedImpact: number;
  implementation: string;
}

interface ComparativeAnalysis {
  benchmarks: Benchmark[];
  comparisons: Comparison[];
  rankings: Ranking[];
  insights: ComparativeInsight[];
}

interface Benchmark {
  name: string;
  source: string;
  score: number;
  confidence: number;
  quality: number;
  methodology: string;
}

interface Comparison {
  metric: string;
  ourValue: number;
  benchmarkValue: number;
  difference: number;
  significance: boolean;
  interpretation: string;
}

interface Ranking {
  position: number;
  total: number;
  percentile: number;
  category: string;
  score: number;
}

interface ComparativeInsight {
  observation: string;
  significance: InsightSignificance;
  implications: string[];
  recommendations: string[];
}

enum InsightSignificance {
  CRITICAL = 'critical',
  IMPORTANT = 'important',
  INTERESTING = 'interesting',
  MARGINAL = 'marginal',
}

interface ReportRecommendation {
  type: RecommendationType;
  priority: RecommendationPriority;
  title: string;
  description: string;
  rationale: string;
  implementation: string;
  expectedImpact: string;
  timeline: string;
  resources: ResourceRequirement[];
}

interface ResourceRequirement {
  type: ResourceType;
  amount: number;
  unit: string;
  cost?: number;
  availability: string;
}

enum ResourceType {
  PERSONNEL = 'personnel',
  TIME = 'time',
  COMPUTING = 'computing',
  DATA = 'data',
  EXPERTISE = 'expertise',
  BUDGET = 'budget',
}

interface ReportAppendix {
  title: string;
  type: AppendixType;
  content: any;
  format: string;
  size: number;
}

enum AppendixType {
  DETAILED_DATA = 'detailed_data',
  METHODOLOGY_DETAILS = 'methodology_details',
  RAW_RESULTS = 'raw_results',
  STATISTICAL_TABLES = 'statistical_tables',
  GRAPHS_CHARTS = 'graphs_charts',
  TECHNICAL_DOCUMENTATION = 'technical_documentation',
}

interface ScoreTrend {
  period: string;
  score: number;
  change: number;
  trend: TrendDirection;
  significance: boolean;
}

enum TrendDirection {
  IMPROVING = 'improving',
  DECLINING = 'declining',
  STABLE = 'stable',
  VOLATILE = 'volatile',
}
```

**Multi-Judge Score Aggregation Implementation:**

```typescript
class MultiJudgeAggregationService implements MultiJudgeAggregationService {
  constructor(
    private aggregationRepository: AggregationRepository,
    private judgeRepository: JudgeRepository,
    private executionRepository: ExecutionRepository,
    private weightingService: WeightingService,
    private consensusService: ConsensusService,
    private conflictResolutionService: ConflictResolutionService,
    private qualityAssuranceService: QualityAssuranceService,
    private outlierDetectionService: OutlierDetectionService,
    private confidenceCalculationService: ConfidenceCalculationService,
    private reportingService: ReportingService
  ) {}

  async aggregateScores(executionId: string, config: AggregationConfig): Promise<AggregatedScore> {
    // Validate execution
    const execution = await this.executionRepository.getExecution(executionId);
    if (!execution) {
      throw new Error(`Execution ${executionId} not found`);
    }

    // Get all judge scores for execution
    const judgeScores = await this.getJudgeScores(executionId, config);

    // Validate minimum requirements
    this.validateMinimumRequirements(judgeScores, config);

    // Calculate weights for each judge
    const judgeWeights = await this.calculateJudgeWeights(judgeScores, config);

    // Detect and handle outliers
    const outlierAnalysis = await this.detectOutliers(judgeScores, config.outlierDetection);
    const filteredScores = this.applyOutlierActions(judgeScores, outlierAnalysis);

    // Aggregate scores by criterion
    const criterionScores = await this.aggregateCriterionScores(
      filteredScores,
      judgeWeights,
      config
    );

    // Calculate overall score
    const overallScore = this.calculateOverallScore(criterionScores, config);

    // Calculate confidence metrics
    const confidence = await this.calculateConfidence(
      filteredScores,
      criterionScores,
      config.confidenceCalculation
    );

    // Calculate consensus metrics
    const consensus = await this.calculateConsensusMetrics(filteredScores, config);

    // Detect conflicts
    const conflicts = await this.detectConflicts(filteredScores, config);

    // Calculate quality metrics
    const quality = await this.calculateQualityMetrics(
      filteredScores,
      criterionScores,
      config
    );

    // Create judge contributions
    const judgeContributions = await this.calculateJudgeContributions(
      filteredScores,
      judgeWeights,
      criterionScores
    );

    // Create aggregated score
    const aggregatedScore: AggregatedScore = {
      id: `aggregation_${Date.now()}`,
      executionId,
      config,
      overallScore,
      criterionScores,
      confidence,
      consensus,
      quality,
      judges: judgeContributions,
      conflicts,
      outliers: outlierAnalysis.outliers,
      metadata: {
        processingTime: 0, // Will be calculated
        judgeCount: filteredScores.length,
        criterionCount: criterionScores.length,
        aggregationSteps: [],
        qualityChecks: [],
        auditTrail: [],
        version: '1.0',
        configuration: config,
      },
      createdAt: new Date(),
      version: 1,
    };

    // Save aggregation
    await this.aggregationRepository.saveAggregation(aggregatedScore);

    return aggregatedScore;
  }

  async calculateConsensus(executionId: string, method: ConsensusMethod): Promise<ConsensusResult> {
    const judgeScores = await this.getJudgeScores(executionId);

    return await this.consensusService.calculateConsensus(judgeScores, method);
  }

  async resolveConflicts(
    executionId: string,
    conflicts: ScoreConflict[]
  ): Promise<ConflictResolution> {
    const resolutions: ConflictResolution[] = [];

    for (const conflict of conflicts) {
      const resolution = await this.conflictResolutionService.resolveConflict(conflict);
      resolutions.push(resolution);
    }

    // Combine resolutions if needed
    return this.combineResolutions(resolutions);
  }

  async validateAggregation(
    executionId: string,
    result: AggregatedScore
  ): Promise<ValidationResult> {
    return await this.qualityAssuranceService.validateAggregation(result);
  }

  async getAggregationHistory(executionId: string): Promise<AggregatedScore[]> {
    return await this.aggregationRepository.getExecutionAggregations(executionId);
  }

  async updateAggregationWeights(
    executionId: string,
    weights: WeightUpdate
  ): Promise<void> {
    // Validate weight update
    await this.validateWeightUpdate(weights);

    // Apply weight updates
    await this.aggregationRepository.updateWeights(executionId, weights);

    // Recalculate aggregation if needed
    if (weights.recalculate) {
      const config = await this.getAggregationConfig(executionId);
      await this.aggregateScores(executionId, config);
    }
  }

  async generateAggregationReport(executionId: string): Promise<AggregationReport> {
    const aggregation = await this.aggregationRepository.getLatestAggregation(executionId);
    if (!aggregation) {
      throw new Error(`No aggregation found for execution ${executionId}`);
    }

    return await this.reportingService.generateReport(aggregation);
  }

  private async getJudgeScores(
    executionId: string,
    config: AggregationConfig
  ): Promise<JudgeScore[]> {
    const scores: JudgeScore[] = [];

    for (const judgeType of config.judgeTypes) {
      if (!judgeType.enabled) continue;

      const typeScores = await this.judgeRepository.getJudgeScoresByType(
        executionId,
        judgeType.type
      );

      // Filter by quality threshold
      const qualifiedScores = typeScores.filter(score =>
        score.quality >= judgeType.qualityThreshold
      );

      // Apply count limits
      const limitedScores = this.applyCountLimits(
        qualifiedScores,
        judgeType.minimumCount,
        judgeType.maximumCount
      );

      scores.push(...limitedScores);
    }

    return scores;
  }

  private validateMinimumRequirements(
    scores: JudgeScore[],
    config: AggregationConfig
  ): void {
    const totalJudges = scores.length;
    const judgeTypeCounts = new Map<JudgeType, number>();

    for (const score of scores) {
      judgeTypeCounts.set(
        score.judgeType,
        (judgeTypeCounts.get(score.judgeType) || 0) + 1
      );
    }

    // Check total minimum
    if (totalJudges < config.qualityThresholds.minimumJudgeCount) {
      throw new Error(
        `Insufficient judges: ${totalJudges} < ${config.qualityThresholds.minimumJudgeCount}`
      );
    }

    // Check per-type minimums
    for (const judgeTypeConfig of config.judgeTypes) {
      if (!judgeTypeConfig.enabled) continue;

      const count = judgeTypeCounts.get(judgeTypeConfig.type) || 0;
      if (count < judgeTypeConfig.minimumCount) {
        throw new Error(
          `Insufficient ${judgeTypeConfig.type} judges: ${count} < ${judgeTypeConfig.minimumCount}`
        );
      }
    }
  }

  private async calculateJudgeWeights(
    scores: JudgeScore[],
    config: AggregationConfig
  ): Promise<Map<string, number>> {
    const weights = new Map<string, number>();

    for (const score of scores) {
      const weight = await this.weightingService.calculateWeight(
        score,
        config.weightingMethod
      );
      weights.set(score.judgeId, weight);
    }

    return weights;
  }

  private async detectOutliers(
    scores: JudgeScore[],
    config: OutlierDetectionConfig
  ): Promise<OutlierAnalysis> {
    if (!config.enabled) {
      return {
        outliers: [],
        method: config.method,
        threshold: config.threshold,
        detected: 0,
      };
    }

    return await this.outlierDetectionService.detectOutliers(scores, config);
  }

  private applyOutlierActions(
    scores: JudgeScore[],
    outlierAnalysis: OutlierAnalysis
  ): JudgeScore[] {
    const outlierIds = new Set(outlierAnalysis.outliers.map(o => o.judgeId));

    return scores.filter(score => {
      const isOutlier = outlierIds.has(score.judgeId);
      const outlier = outlierAnalysis.outliers.find(o => o.judgeId === score.judgeId);

      if (isOutlier && outlier) {
        return outlier.action !== OutlierAction.EXCLUDE;
      }

      return true;
    });
  }

  private async aggregateCriterionScores(
    scores: JudgeScore[],
    weights: Map<string, number>,
    config: AggregationConfig
  ): Promise<AggregatedCriterionScore[]> {
    const criterionMap = new Map<string, JudgeScore[]>();

    // Group scores by criterion
    for (const score of scores) {
      for (const [criterionId, criterionScore] of Object.entries(score.criterionScores)) {
        if (!criterionMap.has(criterionId)) {
          criterionMap.set(criterionId, []);
        }
        criterionMap.get(criterionId)!.push({
          ...score,
          criterionScore,
          criterionId,
        });
      }
    }

    const aggregatedScores: AggregatedCriterionScore[] = [];

    for (const [criterionId, criterionScores] of criterionMap) {
      const aggregatedScore = await this.aggregateSingleCriterion(
        criterionId,
        criterionScores,
        weights,
        config
      );
      aggregatedScores.push(aggregatedScore);
    }

    return aggregatedScores;
  }

  private async aggregateSingleCriterion(
    criterionId: string,
    scores: JudgeScore[],
    weights: Map<string, number>,
    config: AggregationConfig
  ): Promise<AggregatedCriterionScore> {
    // Apply aggregation method
    let aggregatedScore: number;
    let distribution: ScoreDistribution;

    switch (config.aggregationMethod) {
      case AggregationMethod.WEIGHTED_AVERAGE:
        ({ score: aggregatedScore, distribution } = this.calculateWeightedAverage(
          scores,
          weights
        ));
        break;

      case AggregationMethod.MEDIAN:
        ({ score: aggregatedScore, distribution } = this.calculateMedian(scores));
        break;

      case AggregationMethod.BAYESIAN_AGGREGATION:
        ({ score: aggregatedScore, distribution } = await this.calculateBayesianAggregation(
          scores,
          weights,
          config
        ));
        break;

      default:
        throw new Error(`Unsupported aggregation method: ${config.aggregationMethod}`);
    }

    // Calculate contributions
    const contributions = this.calculateCriterionContributions(scores, weights, aggregatedScore);

    // Detect conflicts
    const conflicts = await this.detectCriterionConflicts(scores, config);

    // Detect outliers
    const outliers = this.detectCriterionOutliers(scores, config);

    return {
      criterionId,
      name: criterionId, // Would fetch from criterion repository
      aggregatedScore,
      confidence: this.calculateCriterionConfidence(scores, distribution),
      weight: this.calculateCriterionWeight(scores, config),
      distribution,
      contributions,
      conflicts,
      outliers,
    };
  }

  private calculateWeightedAverage(
    scores: JudgeScore[],
    weights: Map<string, number>
  ): { score: number; distribution: ScoreDistribution } {
    let weightedSum = 0;
    let totalWeight = 0;
    const values: number[] = [];

    for (const score of scores) {
      const weight = weights.get(score.judgeId) || 1;
      const value = score.criterionScore;

      weightedSum += value * weight;
      totalWeight += weight;
      values.push(value);
    }

    const average = totalWeight > 0 ? weightedSum / totalWeight : 0;
    const distribution = this.calculateDistribution(values);

    return { score: average, distribution };
  }

  private calculateMedian(scores: JudgeScore[]): { score: number; distribution: ScoreDistribution } {
    const values = scores.map(s => s.criterionScore).sort((a, b) => a - b);
    const median = this.calculateMedianValue(values);
    const distribution = this.calculateDistribution(values);

    return { score: median, distribution };
  }

  private async calculateBayesianAggregation(
    scores: JudgeScore[],
    weights: Map<string, number>,
    config: AggregationConfig
  ): Promise<{ score: number; distribution: ScoreDistribution }> {
    // Simplified Bayesian aggregation
    // In practice, this would use proper Bayesian methods

    const values = scores.map(s => s.criterionScore);
    const weightsArray = scores.map(s => weights.get(s.judgeId) || 1);

    // Use weighted average as prior mean
    const priorMean = this.calculateWeightedAverage(scores, weights).score;

    // Calculate posterior (simplified)
    const posteriorMean = priorMean; // Would be properly calculated
    const distribution = this.calculateDistribution(values);

    return { score: posteriorMean, distribution };
  }

  private calculateDistribution(values: number[]): ScoreDistribution {
    if (values.length === 0) {
      return {
        mean: 0,
        median: 0,
        mode: 0,
        standardDeviation: 0,
        variance: 0,
        range: [0, 0],
        quartiles: [0, 0, 0],
        skewness: 0,
        kurtosis: 0,
        histogram: [],
      };
    }

    const sorted = [...values].sort((a, b) => a - b);
    const mean = values.reduce((sum, val) => sum + val, 0) / values.length;
    const median = this.calculateMedianValue(sorted);
    const mode = this.calculateMode(values);

    const variance = values.reduce((sum, val) => sum + Math.pow(val - mean, 2), 0) / values.length;
    const standardDeviation = Math.sqrt(variance);

    const range: [number, number] = [sorted[0], sorted[sorted.length - 1]];
    const quartiles = this.calculateQuartiles(sorted);

    const skewness = this.calculateSkewness(values, mean, standardDeviation);
    const kurtosis = this.calculateKurtosis(values, mean, standardDeviation);

    const histogram = this.calculateHistogram(values);

    return {
      mean,
      median,
      mode,
      standardDeviation,
      variance,
      range,
      quartiles,
      skewness,
      kurtosis,
      histogram,
    };
  }

  private calculateMedianValue(sortedValues: number[]): number {
    const mid = Math.floor(sortedValues.length / 2);

    if (sortedValues.length % 2 === 0) {
      return (sortedValues[mid - 1] + sortedValues[mid]) / 2;
    } else {
      return sortedValues[mid];
    }
  }

  private calculateMode(values: number[]): number {
    const frequency: Record<number, number> = {};

    for (const value of values) {
      frequency[value] = (frequency[value] || 0) + 1;
    }

    let maxFrequency = 0;
    let mode = values[0];

    for (const [value, freq] of Object.entries(frequency)) {
      if (freq > maxFrequency) {
        maxFrequency = freq;
        mode = parseFloat(value);
      }
    }

    return mode;
  }

  private calculateQuartiles(sortedValues: number[]): [number, number, number] {
    const q1Index = Math.floor(sortedValues.length * 0.25);
    const q2Index = Math.floor(sortedValues.length * 0.5);
    const q3Index = Math.floor(sortedValues.length * 0.75);

    return [
      sortedValues[q1Index],
      sortedValues[q2Index],
      sortedValues[q3Index],
    ];
  }

  private calculateSkewness(values: number[], mean: number, stdDev: number): number {
    if (stdDev === 0) return 0;

    const n = values.length;
    const sum = values.reduce((acc, val) => acc + Math.pow((val - mean) / stdDev, 3), 0);

    return (n / ((n - 1) * (n - 2))) * sum;
  }

  private calculateKurtosis(values: number[], mean: number, stdDev: number): number {
    if (stdDev === 0) return 0;

    const n = values.length;
    const sum = values.reduce((acc, val) => acc + Math.pow((val - mean) / stdDev, 4), 0);

    return (n * (n + 1) / ((n - 1) * (n - 2) * (n - 3))) * sum -
           (3 * Math.pow(n - 1, 2) / ((n - 2) * (n - 3)));
  }

  private calculateHistogram(values: number[], bins: number = 10): HistogramBin[] {
    if (values.length === 0) return [];

    const min = Math.min(...values);
    const max = Math.max(...values);
    const binWidth = (max - min) / bins;

    const histogram: HistogramBin[] = [];

    for (let i = 0; i < bins; i++) {
      const binStart = min + i * binWidth;
      const binEnd = binStart + binWidth;

      const count = values.filter(val =>
        val >= binStart && (i === bins - 1 ? val <= binEnd : val < binEnd)
      ).length;

      histogram.push({
        range: [binStart, binEnd],
        count,
        percentage: (count / values.length) * 100,
      });
    }

    return histogram;
  }

  private calculateCriterionContributions(
    scores: JudgeScore[],
    weights: Map<string, number>,
    aggregatedScore: number
  ): CriterionContribution[] {
    return scores.map(score => {
      const weight = weights.get(score.judgeId) || 1;
      const contribution = (score.criterionScore * weight) / scores.reduce((sum, s) =>
        sum + (weights.get(s.judgeId) || 1), 0
      );

      return {
        judgeId: score.judgeId,
        judgeType: score.judgeType,
        score: score.criterionScore,
        weight,
        contribution,
        quality: score.quality,
        confidence: score.confidence,
      };
    });
  }

  private async detectCriterionConflicts(
    scores: JudgeScore[],
    config: AggregationConfig
  ): Promise<CriterionConflict[]> {
    // Simplified conflict detection
    const conflicts: CriterionConflict[] = [];

    if (scores.length < 2) return conflicts;

    const values = scores.map(s => s.criterionScore);
    const mean = values.reduce((sum, val) => sum + val, 0) / values.length;
    const stdDev = Math.sqrt(values.reduce((sum, val) => sum + Math.pow(val - mean, 2), 0) / values.length);

    // Check for outliers (potential conflicts)
    for (const score of scores) {
      const zScore = Math.abs((score.criterionScore - mean) / stdDev);

      if (zScore > 2) { // More than 2 standard deviations
        conflicts.push({
          type: ConflictType.SCORE_DISPARITY,
          severity: zScore > 3 ? ConflictSeverity.HIGH : ConflictSeverity.MEDIUM,
          description: `Score ${score.criterionScore} is ${zScore.toFixed(2)} standard deviations from mean`,
          involvedJudges: [score.judgeId],
          resolution: undefined,
        });
      }
    }

    return conflicts;
  }

  private detectCriterionOutliers(
    scores: JudgeScore[],
    config: AggregationConfig
  ): CriterionOutlier[] {
    // Simplified outlier detection using z-score
    const outliers: CriterionOutlier[] = [];

    if (scores.length < 3) return outliers;

    const values = scores.map(s => s.criterionScore);
    const mean = values.reduce((sum, val) => sum + val, 0) / values.length;
    const stdDev = Math.sqrt(values.reduce((sum, val) => sum + Math.pow(val - mean, 2), 0) / values.length);

    for (const score of scores) {
      const zScore = Math.abs((score.criterionScore - mean) / stdDev);

      if (zScore > config.outlierDetection.threshold) {
        outliers.push({
          judgeId: score.judgeId,
          score: score.criterionScore,
          deviation: zScore,
          method: config.outlierDetection.method,
          threshold: config.outlierDetection.threshold,
          action: config.outlierDetection.action,
          justification: `Z-score of ${zScore.toFixed(2)} exceeds threshold ${config.outlierDetection.threshold}`,
        });
      }
    }

    return outliers;
  }

  private calculateCriterionConfidence(
    scores: JudgeScore[],
    distribution: ScoreDistribution
  ): number {
    if (scores.length === 0) return 0;

    // Base confidence on sample size and variance
    const sampleSizeFactor = Math.min(1, scores.length / 10); // Approaches 1 at 10 samples
    const varianceFactor = Math.max(0, 1 - (distribution.standardDeviation / 50)); // Lower variance = higher confidence

    // Weight by individual judge confidences
    const avgJudgeConfidence = scores.reduce((sum, score) => sum + score.confidence, 0) / scores.length;

    return (sampleSizeFactor * 0.4 + varianceFactor * 0.3 + avgJudgeConfidence * 0.3);
  }

  private calculateCriterionWeight(scores: JudgeScore[], config: AggregationConfig): number {
    // Would fetch criterion weight from configuration
    return 1.0 / scores.length; // Equal weight for simplicity
  }

  private calculateOverallScore(
    criterionScores: AggregatedCriterionScore[],
    config: AggregationConfig
  ): number {
    return criterionScores.reduce((sum, score) => sum + score.aggregatedScore * score.weight, 0);
  }

  private async calculateConfidence(
    scores: JudgeScore[],
    criterionScores: AggregatedCriterionScore[],
    config: ConfidenceCalculationConfig
  ): Promise<ConfidenceMetrics> {
    return await this.confidenceCalculationService.calculateConfidence(
      scores,
      criterionScores,
      config
    );
  }

  private async calculateConsensusMetrics(
    scores: JudgeScore[],
    config: AggregationConfig
  ): Promise<ConsensusMetrics> {
    const consensusResult = await this.consensusService.calculateConsensus(scores, config.consensusMethod);

    return {
      level: consensusResult.consensusScore,
      strength: consensusResult.confidence,
      participation: scores.length,
      agreement: this.calculateAgreementMetrics(scores),
      polarization: this.calculatePolarizationMetrics(scores),
      convergence: this.calculateConvergenceMetrics(consensusResult),
    };
  }

  private calculateAgreementMetrics(scores: JudgeScore[]): AgreementMetrics {
    // Simplified agreement calculation
    const overallAgreement = this.calculatePairwiseAgreement(scores);
    const criterionAgreement: Record<string, number> = {};
    const judgeTypeAgreement: Record<JudgeType, number> = {};

    // Calculate agreement by criterion
    const criteria = new Set<string>();
    for (const score of scores) {
      Object.keys(score.criterionScores).forEach(criterion => criteria.add(criterion));
    }

    for (const criterion of criteria) {
      const criterionScores = scores
        .map(s => ({ judgeId: s.judgeId, score: s.criterionScores[criterion] || 0 }))
        .filter(s => s.score > 0);

      criterionAgreement[criterion] = this.calculatePairwiseAgreement(criterionScores as any);
    }

    // Calculate agreement by judge type
    const judgeTypes = new Set(scores.map(s => s.judgeType));
    for (const judgeType of judgeTypes) {
      const typeScores = scores.filter(s => s.judgeType === judgeType);
      judgeTypeAgreement[judgeType] = this.calculatePairwiseAgreement(typeScores);
    }

    return {
      overallAgreement,
      pairwiseAgreement: overallAgreement,
      criterionAgreement,
      judgeTypeAgreement,
      agreementMatrix: this.calculateAgreementMatrix(scores),
    };
  }

  private calculatePairwiseAgreement(scores: JudgeScore[]): number {
    if (scores.length < 2) return 1;

    let totalAgreement = 0;
    let comparisons = 0;

    for (let i = 0; i < scores.length; i++) {
      for (let j = i + 1; j < scores.length; j++) {
        const score1 = scores[i].overallScore;
        const score2 = scores[j].overallScore;
        const agreement = 1 - Math.abs(score1 - score2) / 100;
        totalAgreement += agreement;
        comparisons++;
      }
    }

    return comparisons > 0 ? totalAgreement / comparisons : 0;
  }

  private calculateAgreementMatrix(scores: JudgeScore[]): AgreementMatrix {
    const n = scores.length;
    const matrix: number[][] = Array(n).fill(null).map(() => Array(n).fill(0));

    for (let i = 0; i < n; i++) {
      for (let j = 0; j < n; j++) {
        if (i === j) {
          matrix[i][j] = 1;
        } else {
          const score1 = scores[i].overallScore;
          const score2 = scores[j].overallScore;
          matrix[i][j] = 1 - Math.abs(score1 - score2) / 100;
        }
      }
    }

    const averageAgreement = this.calculatePairwiseAgreement(scores);

    return {
      matrix,
      judges: scores.map(s => s.judgeId),
      averageAgreement,
    };
  }

  private calculatePolarizationMetrics(scores: JudgeScore[]): PolarizationMetrics {
    // Simplified polarization calculation
    const values = scores.map(s => s.overallScore);
    const mean = values.reduce((sum, val) => sum + val, 0) / values.length;
    const variance = values.reduce((sum, val) => sum + Math.pow(val - mean, 2), 0) / values.length;

    // Simple polarization index based on variance
    const index = Math.min(1, variance / 625); // Normalize by max possible variance (25^2)

    return {
      index,
      clusters: [], // Would implement clustering
      extremity: this.calculateExtremity(values),
      fragmentation: this.calculateFragmentation(values),
    };
  }

  private calculateExtremity(values: number[]): number {
    const mean = values.reduce((sum, val) => sum + val, 0) / values.length;
    const avgDeviation = values.reduce((sum, val) => sum + Math.abs(val - mean), 0) / values.length;
    return avgDeviation / 50; // Normalize by max possible deviation
  }

  private calculateFragmentation(values: number[]): number {
    // Simplified fragmentation based on range
    const min = Math.min(...values);
    const max = Math.max(...values);
    return (max - min) / 100; // Normalize by max possible range
  }

  private calculateConvergenceMetrics(consensusResult: ConsensusResult): ConvergenceMetrics {
    return {
      iterations: consensusResult.iterations.length,
      finalScore: consensusResult.consensusScore,
      initialScore: consensusResult.iterations[0]?.inputState.metrics.agreement || 0,
      convergenceRate: this.calculateConvergenceRate(consensusResult.iterations),
      stability: consensusResult.quality.stability,
    };
  }

  private calculateConvergenceRate(iterations: ConsensusIteration[]): number {
    if (iterations.length < 2) return 1;

    const initialAgreement = iterations[0].outputState.metrics.agreement;
    const finalAgreement = iterations[iterations.length - 1].outputState.metrics.agreement;

    return initialAgreement > 0 ? finalAgreement / initialAgreement : 1;
  }

  private async detectConflicts(
    scores: JudgeScore[],
    config: AggregationConfig
  ): Promise<ScoreConflict[]> {
    // Simplified conflict detection
    const conflicts: ScoreConflict[] = [];

    // Check for score disparities
    const values = scores.map(s => s.overallScore);
    const mean = values.reduce((sum, val) => sum + val, 0) / values.length;
    const stdDev = Math.sqrt(values.reduce((sum, val) => sum + Math.pow(val - mean, 2), 0) / values.length);

    for (const score of scores) {
      const zScore = Math.abs((score.overallScore - mean) / stdDev);

      if (zScore > 2.5) {
        conflicts.push({
          id: `conflict_${Date.now()}_${score.judgeId}`,
          type: ConflictType.SCORE_DISPARITY,
          severity: zScore > 3.5 ? ConflictSeverity.CRITICAL : ConflictSeverity.HIGH,
          description: `Judge ${score.judgeId} score ${score.overallScore} is ${zScore.toFixed(2)} standard deviations from mean`,
          involvedJudges: [{
            judgeId: score.judgeId,
            judgeType: score.judgeType,
            position: score.overallScore > mean ? ConflictPosition.HIGHER : ConflictPosition.LOWER,
          }],
          criteria: Object.keys(score.criterionScores),
          scores: [{
            judgeId: score.judgeId,
            criterionId: 'overall',
            score: score.overallScore,
            deviation: zScore,
            percentile: this.calculatePercentile(score.overallScore, values),
          }],
          detection: {
            method: ConflictDetectionMethod.STATISTICAL_OUTLIER,
            threshold: 2.5,
            detectedAt: new Date(),
            detectedBy: 'system',
            confidence: Math.min(1, zScore / 3),
          },
          impact: {
            scoreImpact: Math.abs(score.overallScore - mean),
            confidenceImpact: zScore * 0.1,
            reliabilityImpact: zScore * 0.05,
            fairnessImpact: zScore * 0.02,
            overallImpact: zScore * 0.1,
          },
        });
      }
    }

    return conflicts;
  }

  private calculatePercentile(value: number, values: number[]): number {
    const sorted = [...values].sort((a, b) => a - b);
    const index = sorted.indexOf(value);
    return (index / (sorted.length - 1)) * 100;
  }

  private async calculateQualityMetrics(
    scores: JudgeScore[],
    criterionScores: AggregatedCriterionScore[],
    config: AggregationConfig
  ): Promise<AggregationQualityMetrics> {
    return await this.qualityAssuranceService.calculateQualityMetrics(
      scores,
      criterionScores,
      config
    );
  }

  private async calculateJudgeContributions(
    scores: JudgeScore[],
    weights: Map<string, number>,
    criterionScores: AggregatedCriterionScore[]
  ): Promise<JudgeContribution[]> {
    const contributions: JudgeContribution[] = [];

    for (const score of scores) {
      const weight = weights.get(score.judgeId) || 1;
      const contribution = await this.calculateSingleJudgeContribution(
        score,
        weight,
        criterionScores
      );
      contributions.push(contribution);
    }

    return contributions;
  }

  private async calculateSingleJudgeContribution(
    score: JudgeScore,
    weight: number,
    criterionScores: AggregatedCriterionScore[]
  ): Promise<JudgeContribution> {
    // Calculate contribution to overall score
    const totalWeight = Array.from(weight.values()).reduce((sum, w) => sum + w, 0);
    const contribution = weight / totalWeight;

    // Calculate influence based on weight and quality
    const influence = contribution * score.quality;

    // Calculate alignment with aggregated scores
    const alignment = this.calculateJudgeAlignment(score, criterionScores);

    return {
      judgeId: score.judgeId,
      judgeType: score.judgeType,
      weight,
      contribution,
      quality: score.quality,
      influence,
      alignment,
      expertise: {
        domainAlignment: 0.8, // Would calculate based on expertise
        taskAlignment: 0.8,
        criterionAlignment: {},
        overallAlignment: 0.8,
      },
      performance: {
        accuracy: 0.8,
        consistency: 0.8,
        timeliness: 0.8,
        participation: 0.8,
        quality: score.quality,
      },
    };
  }

  private calculateJudgeAlignment(
    score: JudgeScore,
    criterionScores: AggregatedCriterionScore[]
  ): ExpertiseAlignment {
    const criterionAlignment: Record<string, number> = {};
    let totalAlignment = 0;
    let criterionCount = 0;

    for (const criterionScore of criterionScores) {
      const judgeScore = score.criterionScores[criterionScore.criterionId] || 0;
      const alignment = 1 - Math.abs(judgeScore - criterionScore.aggregatedScore) / 100;

      criterionAlignment[criterionScore.criterionId] = alignment;
      totalAlignment += alignment;
      criterionCount++;
    }

    const overallAlignment = criterionCount > 0 ? totalAlignment / criterionCount : 0;

    return {
      domainAlignment: overallAlignment, // Simplified
      taskAlignment: overallAlignment,
      criterionAlignment,
      overallAlignment,
    };
  }

  private applyCountLimits(
    scores: JudgeScore[],
    minimum: number,
    maximum?: number
  ): JudgeScore[] {
    if (scores.length < minimum) {
      throw new Error(`Insufficient scores: ${scores.length} < ${minimum}`);
    }

    if (maximum && scores.length > maximum) {
      // Sort by quality and take top scores
      return scores
        .sort((a, b) => b.quality - a.quality)
        .slice(0, maximum);
    }

    return scores;
  }

  private async validateWeightUpdate(weights: WeightUpdate): Promise<void> {
    // Validate weight update logic
    let totalChange = 0;

    for (const update of weights.updates) {
      if (update.newWeight < 0 || update.newWeight > 1) {
        throw new Error(`Invalid weight: ${update.newWeight} must be between 0 and 1`);
      }

      totalChange += Math.abs(update.change);
    }

    if (totalChange > 0.5) {
      throw new Error(`Total weight change too large: ${totalChange} > 0.5`);
    }
  }

  private async getAggregationConfig(executionId: string): Promise<AggregationConfig> {
    // Would fetch from repository or use default
    return {
      judgeTypes: [
        { type: JudgeType.STAFF_REVIEWER, enabled: true, weight: 0.3, minimumCount: 1, qualityThreshold: 0.7, expertiseRequirements: [] },
        { type: JudgeType.COMMUNITY_VOTER, enabled: true, weight: 0.2, minimumCount: 3, qualityThreshold: 0.6, expertiseRequirements: [] },
        { type: JudgeType.AI_SELF_REVIEW, enabled: true, weight: 0.2, minimumCount: 1, qualityThreshold: 0.8, expertiseRequirements: [] },
        { type: JudgeType.ELITE_PANELIST, enabled: false, weight: 0.3, minimumCount: 0, qualityThreshold: 0.9, expertiseRequirements: [] },
      ],
      weightingMethod: WeightingMethod.QUALITY_BASED,
      aggregationMethod: AggregationMethod.WEIGHTED_AVERAGE,
      consensusMethod: ConsensusMethod.WEIGHTED_CONSENSUS,
      conflictResolution: ConflictResolutionStrategy.QUALITY_WEIGHTED,
      qualityThresholds: {
        minimumJudgeCount: 3,
        minimumQualityScore: 0.6,
        maximumVariance: 400,
        minimumConfidence: 0.7,
        consensusThreshold: 0.7,
        outlierThreshold: 2.0,
      },
      outlierDetection: {
        enabled: true,
        method: OutlierDetectionMethod.Z_SCORE,
        threshold: 2.0,
        action: OutlierAction.FLAG_FOR_REVIEW,
        investigationRequired: true,
      },
      confidenceCalculation: {
        method: ConfidenceMethod.VARIANCE_BASED,
        factors: [
          { type: ConfidenceFactorType.JUDGE_COUNT, weight: 0.3, calculation: { method: 'linear', parameters: {} } },
          { type: ConfidenceFactorType.JUDGE_QUALITY, weight: 0.3, calculation: { method: 'weighted_average', parameters: {} } },
          { type: ConfidenceFactorType.SCORE_VARIANCE, weight: 0.2, calculation: { method: 'inverse', parameters: {} } },
          { type: ConfidenceFactorType.CONSENSUS_LEVEL, weight: 0.2, calculation: { method: 'direct', parameters: {} } },
        ],
        weighting: { linear: true, normalization: NormalizationMethod.MIN_MAX, aggregation: WeightAggregation.WEIGHTED_SUM },
        thresholds: { minimum: 0.3, low: 0.5, medium: 0.7, high: 0.8, veryHigh: 0.9 },
      },
    };
  }

  private combineResolutions(resolutions: ConflictResolution[]): ConflictResolution {
    // Simplified combination of multiple resolutions
    if (resolutions.length === 0) {
      throw new Error('No resolutions to combine');
    }

    if (resolutions.length === 1) {
      return resolutions[0];
    }

    // Combine results
    const combinedResult: ResolutionResult = {
      finalScores: {},
      adjustments: [],
      explanations: [],
      confidence: 0,
      satisfaction: { overall: 0, byJudge: {}, byJudgeType: {}, concerns: [] },
    };

    let totalConfidence = 0;
    let totalSatisfaction = 0;

    for (const resolution of resolutions) {
      Object.assign(combinedResult.finalScores, resolution.result.finalScores);
      combinedResult.adjustments.push(...resolution.result.adjustments);
      combinedResult.explanations.push(...resolution.result.explanations);
      totalConfidence += resolution.result.confidence;
      totalSatisfaction += resolution.result.satisfaction.overall;
    }

    combinedResult.confidence = totalConfidence / resolutions.length;
    combinedResult.satisfaction.overall = totalSatisfaction / resolutions.length;

    return {
      conflictId: 'combined',
      resolution: ConflictResolutionStrategy.AUTOMATED_RESOLUTION,
      resolvedBy: 'system',
      result: combinedResult,
      process: {
        steps: [],
        duration: 0,
        participants: [],
        methods: resolutions.map(r => r.resolution),
        fairness: {
          proceduralJustice: 0.8,
          distributiveJustice: 0.8,
          interactionalJustice: 0.8,
          transparency: 0.8,
          consistency: 0.8,
        },
      },
      impact: {
        scoreChanges: 0,
        confidenceChanges: 0,
        reliabilityChanges: 0,
        relationshipImpact: 0,
        futureImplications: [],
      },
      timestamp: new Date(),
    };
  }
}

// Additional interfaces for completeness
interface JudgeScore {
  judgeId: string;
  judgeType: JudgeType;
  overallScore: number;
  criterionScores: Record<string, number>;
  confidence: number;
  quality: number;
  weight?: number;
  criterionScore?: number;
  criterionId?: string;
}

interface OutlierAnalysis {
  outliers: OutlierScore[];
  method: OutlierDetectionMethod;
  threshold: number;
  detected: number;
}

interface AggregationRepository {
  saveAggregation(aggregation: AggregatedScore): Promise<void>;
  getLatestAggregation(executionId: string): Promise<AggregatedScore | null>;
  getExecutionAggregations(executionId: string): Promise<AggregatedScore[]>;
  updateWeights(executionId: string, weights: WeightUpdate): Promise<void>;
}

interface JudgeRepository {
  getJudgeScoresByType(executionId: string, judgeType: JudgeType): Promise<JudgeScore[]>;
}

interface WeightingService {
  calculateWeight(score: JudgeScore, method: WeightingMethod): Promise<number>;
}

interface ConsensusService {
  calculateConsensus(scores: JudgeScore[], method: ConsensusMethod): Promise<ConsensusResult>;
}

interface ConflictResolutionService {
  resolveConflict(conflict: ScoreConflict): Promise<ConflictResolution>;
}

interface QualityAssuranceService {
  validateAggregation(aggregation: AggregatedScore): Promise<ValidationResult>;
  calculateQualityMetrics(
    scores: JudgeScore[],
    criterionScores: AggregatedCriterionScore[],
    config: AggregationConfig
  ): Promise<AggregationQualityMetrics>;
}

interface OutlierDetectionService {
  detectOutliers(scores: JudgeScore[], config: OutlierDetectionConfig): Promise<OutlierAnalysis>;
}

interface ConfidenceCalculationService {
  calculateConfidence(
    scores: JudgeScore[],
    criterionScores: AggregatedCriterionScore[],
    config: ConfidenceCalculationConfig
  ): Promise<ConfidenceMetrics>;
}

interface ReportingService {
  generateReport(aggregation: AggregatedScore): Promise<AggregationReport>;
}
```

---

## Implementation Summary

**Epic 5: Multi-Judge Evaluation System** provides a comprehensive framework for combining multiple evaluation methods to ensure robust, reliable, and fair assessment of AI model performance. The system implements:

### Key Components

1. **Staff Review Interface** - Professional evaluation workflow with assignment management, quality monitoring, and calibration systems
2. **Community Voting System** - Public participation with reputation systems, anti-spam measures, and weighted voting
3. **AI Self-Review System** - Automated evaluation using AI models with bias detection and consistency checks
4. **Elite Panel Review System** - Expert panel management with sophisticated deliberation and consensus processes
5. **Multi-Judge Score Aggregation** - Advanced statistical methods for combining scores with conflict resolution and quality assurance

### Technical Features

- **Statistical Rigor**: Multiple aggregation methods (weighted average, Bayesian, Dempster-Shafer, Condorcet)
- **Quality Assurance**: Comprehensive validation, outlier detection, and confidence calculation
- **Conflict Resolution**: Automated and manual resolution strategies with fairness metrics
- **Bias Mitigation**: Multi-layered bias detection and mitigation across all evaluation components
- **Transparency**: Complete audit trails, explanation generation, and detailed reporting
- **Scalability**: Efficient processing for large-scale evaluations with configurable quality thresholds

### Integration Points

The system integrates seamlessly with:
- **Epic 4**: Benchmark execution engine for evaluation data
- **Epic 2**: AI provider integration for self-review capabilities
- **Epic 3**: Test bank management for evaluation criteria
- **Epic 1**: Foundation infrastructure for data management

This comprehensive multi-judge system ensures reliable, fair, and transparent evaluation of AI models, providing the confidence needed for research publication, model selection, and deployment decisions.
````

```

```
