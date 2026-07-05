/**
 * Learning Capture
 *
 * Captures learnings from task outcomes for the knowledge base.
 */

import { randomUUID } from 'node:crypto';
import type {
  LearningCapture,
  PendingLearning,
  KnowledgePriority,
} from '@tamma/shared';
import type { IKnowledgeStore, LearningCaptureConfig } from '../types.js';
import { DuplicateDetector } from './duplicate-detector.js';

/**
 * Task outcome data for learning capture
 */
export interface TaskOutcome {
  /** Task identifier */
  taskId: string;
  /** Project identifier */
  projectId: string;
  /** Task type */
  taskType: string;
  /** Task description */
  taskDescription: string;
  /** Outcome status */
  outcome: 'success' | 'failure' | 'partial';
  /** Output or result */
  output?: string;
  /** Error message (if failed) */
  error?: string;
  /** Duration in milliseconds */
  durationMs: number;
  /** Number of retry attempts */
  retryCount?: number;
  /** Files that were changed */
  changedFiles?: string[];
  /** Technologies involved */
  technologies?: string[];
}

/**
 * Default learning capture configuration
 */
const DEFAULT_CONFIG: LearningCaptureConfig = {
  autoCaptureSuccess: true,
  autoCaptureFailure: true,
  requireApproval: true,
  maxPendingDays: 30,
};

/**
 * Learning capture service
 */
export class LearningCaptureService {
  private store: IKnowledgeStore;
  private duplicateDetector: DuplicateDetector;
  private config: LearningCaptureConfig;

  constructor(
    store: IKnowledgeStore,
    config?: Partial<LearningCaptureConfig>
  ) {
    this.store = store;
    this.config = { ...DEFAULT_CONFIG, ...config };
    this.duplicateDetector = new DuplicateDetector(store);
  }

  /**
   * Capture a learning from task outcome
   */
  async captureFromOutcome(
    outcome: TaskOutcome,
    capturedBy: string
  ): Promise<PendingLearning | null> {
    // Check if we should capture
    if (outcome.outcome === 'success' && !this.config.autoCaptureSuccess) {
      return null;
    }
    if (outcome.outcome === 'failure' && !this.config.autoCaptureFailure) {
      return null;
    }

    // Generate learning capture data
    const capture = this.generateCapture(outcome);

    // Check for duplicates
    const isDuplicate = await this.duplicateDetector.isDuplicate(capture);
    if (isDuplicate) {
      return null;
    }

    // Create pending learning
    const pendingLearning: PendingLearning = {
      ...capture,
      id: randomUUID(),
      capturedAt: new Date(),
      capturedBy,
      status: 'pending',
    };

    return this.store.createPending(pendingLearning);
  }

  /**
   * Capture a learning from explicit input
   */
  async captureExplicit(
    capture: LearningCapture,
    capturedBy: string
  ): Promise<PendingLearning> {
    // Check for duplicates
    const isDuplicate = await this.duplicateDetector.isDuplicate(capture);
    if (isDuplicate) {
      throw new Error('A similar learning already exists');
    }

    const pendingLearning: PendingLearning = {
      ...capture,
      id: randomUUID(),
      capturedAt: new Date(),
      capturedBy,
      status: 'pending',
    };

    return this.store.createPending(pendingLearning);
  }

  /**
   * Generate learning capture from task outcome
   */
  private generateCapture(outcome: TaskOutcome): LearningCapture {
    const keywords = this.extractKeywords(outcome);
    const priority = this.determinePriority(outcome);

    if (outcome.outcome === 'success') {
      return this.generateSuccessCapture(outcome, keywords, priority);
    } else if (outcome.outcome === 'failure') {
      return this.generateFailureCapture(outcome, keywords, priority);
    } else {
      return this.generatePartialCapture(outcome, keywords, priority);
    }
  }

  /**
   * Generate capture for successful outcome
   */
  private generateSuccessCapture(
    outcome: TaskOutcome,
    keywords: string[],
    priority: KnowledgePriority
  ): LearningCapture {
    const title = this.generateTitle(outcome, 'success');
    const description = this.generateDescription(outcome, 'success');
    const whatWorked = this.extractWhatWorked(outcome);

    return {
      taskId: outcome.taskId,
      projectId: outcome.projectId,
      outcome: 'success',
      description: outcome.output ?? 'Task completed successfully',
      ...(whatWorked !== undefined ? { whatWorked } : {}),
      suggestedTitle: title,
      suggestedDescription: description,
      suggestedKeywords: keywords,
      suggestedPriority: priority,
    };
  }

  /**
   * Generate capture for failed outcome
   */
  private generateFailureCapture(
    outcome: TaskOutcome,
    keywords: string[],
    priority: KnowledgePriority
  ): LearningCapture {
    const title = this.generateTitle(outcome, 'failure');
    const description = this.generateDescription(outcome, 'failure');
    const rootCause = this.extractRootCause(outcome);

    return {
      taskId: outcome.taskId,
      projectId: outcome.projectId,
      outcome: 'failure',
      description: outcome.error ?? 'Task failed',
      ...(outcome.error !== undefined ? { whatFailed: outcome.error } : {}),
      ...(rootCause !== undefined ? { rootCause } : {}),
      suggestedTitle: title,
      suggestedDescription: description,
      suggestedKeywords: keywords,
      suggestedPriority: priority,
    };
  }

  /**
   * Generate capture for partial outcome
   */
  private generatePartialCapture(
    outcome: TaskOutcome,
    keywords: string[],
    priority: KnowledgePriority
  ): LearningCapture {
    const title = this.generateTitle(outcome, 'partial');
    const description = this.generateDescription(outcome, 'partial');
    const whatWorked = this.extractWhatWorked(outcome);

    return {
      taskId: outcome.taskId,
      projectId: outcome.projectId,
      outcome: 'partial',
      description: outcome.output ?? 'Task partially completed',
      ...(whatWorked !== undefined ? { whatWorked } : {}),
      ...(outcome.error !== undefined ? { whatFailed: outcome.error } : {}),
      suggestedTitle: title,
      suggestedDescription: description,
      suggestedKeywords: keywords,
      suggestedPriority: priority,
    };
  }

  /**
   * Generate title for learning
   */
  private generateTitle(
    outcome: TaskOutcome,
    type: 'success' | 'failure' | 'partial'
  ): string {
    const taskTypeShort = outcome.taskType.replace(/_/g, ' ');

    switch (type) {
      case 'success':
        return `Successful approach: ${taskTypeShort}`;
      case 'failure':
        return `Issue encountered: ${taskTypeShort}`;
      case 'partial':
        return `Partial completion: ${taskTypeShort}`;
    }
  }

  /**
   * Generate description for learning
   */
  private generateDescription(
    outcome: TaskOutcome,
    type: 'success' | 'failure' | 'partial'
  ): string {
    const taskDesc = outcome.taskDescription.substring(0, 100);

    switch (type) {
      case 'success':
        return `When working on "${taskDesc}", the approach taken was effective${
          outcome.durationMs < 60000 ? ' and efficient' : ''
        }.`;
      case 'failure':
        return `When attempting "${taskDesc}", the following issue was encountered: ${
          outcome.error?.substring(0, 200) ?? 'unknown error'
        }.`;
      case 'partial':
        return `When working on "${taskDesc}", the task was only partially completed.`;
    }
  }

  /**
   * Extract keywords from task outcome
   */
  private extractKeywords(outcome: TaskOutcome): string[] {
    const keywords = new Set<string>();

    // Add task type
    keywords.add(outcome.taskType.toLowerCase());

    // Add technologies
    if (outcome.technologies) {
      for (const tech of outcome.technologies) {
        keywords.add(tech.toLowerCase());
      }
    }

    // Extract from file paths
    if (outcome.changedFiles) {
      for (const file of outcome.changedFiles) {
        const ext = file.split('.').pop()?.toLowerCase();
        if (ext) {
          keywords.add(ext);
        }
        // Extract meaningful directory names
        const parts = file.split('/').filter((p) => p.length > 2);
        for (const part of parts.slice(-3)) {
          if (!part.includes('.')) {
            keywords.add(part.toLowerCase());
          }
        }
      }
    }

    // Extract from description (simple word extraction)
    const words = outcome.taskDescription
      .toLowerCase()
      .replace(/[^a-z0-9\s]/g, ' ')
      .split(/\s+/)
      .filter((w) => w.length > 3 && w.length < 20);

    // Add most common meaningful words (up to 5)
    const wordCounts = new Map<string, number>();
    for (const word of words) {
      wordCounts.set(word, (wordCounts.get(word) ?? 0) + 1);
    }
    const sortedWords = Array.from(wordCounts.entries())
      .sort((a, b) => b[1] - a[1])
      .slice(0, 5)
      .map(([word]) => word);
    for (const word of sortedWords) {
      keywords.add(word);
    }

    return Array.from(keywords).slice(0, 15);
  }

  /**
   * Determine priority based on outcome
   */
  private determinePriority(outcome: TaskOutcome): KnowledgePriority {
    // Failures with retries are higher priority
    if (outcome.outcome === 'failure') {
      if (outcome.retryCount && outcome.retryCount > 2) {
        return 'high';
      }
      return 'medium';
    }

    // Very fast successes might indicate good patterns
    if (outcome.outcome === 'success' && outcome.durationMs < 30000) {
      return 'medium';
    }

    return 'low';
  }

  /**
   * Extract what worked from successful outcome
   */
  private extractWhatWorked(outcome: TaskOutcome): string | undefined {
    if (outcome.outcome !== 'success' && outcome.outcome !== 'partial') {
      return undefined;
    }

    // Simple extraction - in production, this could use LLM
    if (outcome.output && outcome.output.length > 0) {
      return outcome.output.substring(0, 500);
    }

    return undefined;
  }

  /**
   * Extract root cause from failed outcome
   */
  private extractRootCause(outcome: TaskOutcome): string | undefined {
    if (!outcome.error) {
      return undefined;
    }

    // Simple extraction - in production, this could use LLM
    // Look for common error patterns
    const error = outcome.error.toLowerCase();

    if (error.includes('timeout')) {
      return 'Operation timed out';
    }
    if (error.includes('permission') || error.includes('access denied')) {
      return 'Permission or access issue';
    }
    if (error.includes('not found')) {
      return 'Resource not found';
    }
    if (error.includes('syntax') || error.includes('parse')) {
      return 'Syntax or parsing error';
    }
    if (error.includes('memory') || error.includes('oom')) {
      return 'Memory exhaustion';
    }
    if (error.includes('network') || error.includes('connection')) {
      return 'Network or connection issue';
    }

    return outcome.error.substring(0, 200);
  }

  /**
   * Update configuration
   */
  setConfig(config: Partial<LearningCaptureConfig>): void {
    this.config = { ...this.config, ...config };
  }

  /**
   * Get current configuration
   */
  getConfig(): LearningCaptureConfig {
    return { ...this.config };
  }
}
