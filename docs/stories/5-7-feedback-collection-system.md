# Story 5.7: Feedback Collection System

**Epic**: Epic 5 - Observability & Production Readiness  
**Status**: Ready for Dev  
**MVP Priority**: Optional (Post-MVP Enhancement)  
**Estimated Complexity**: Medium  
**Target Implementation**: Sprint 6 (Post-MVP)

## Acceptance Criteria

### Feedback Collection Interface

- [ ] **Multi-channel feedback collection** from in-app prompts, email surveys, and dashboard widgets
- [ ] **Contextual feedback** capture with automatic system state and user context
- [ ] **Feedback categorization** with tags, severity, and feature association
- [ ] **Feedback prioritization** based on impact, frequency, and user value
- [ ] **Anonymous feedback option** for sensitive feedback collection

### Feedback Management System

- [ ] **Feedback dashboard** with filtering, sorting, and search capabilities
- [ ] **Feedback lifecycle management** from collection to resolution
- [ ] **Feedback assignment** to team members for follow-up
- [ ] **Feedback response tracking** with communication history
- [ ] **Feedback analytics** with trend analysis and insights

### Integration with Development Workflow

- [ ] **Automatic issue creation** from high-priority feedback
- [ ] **Feedback-to-story mapping** for product backlog integration
- [ ] **Developer feedback loop** with implementation status updates
- [ ] **Release notification** to feedback providers when issues are resolved
- [ ] **Feedback impact measurement** on user satisfaction and product metrics

### Feedback Analytics and Insights

- [ ] **Sentiment analysis** for feedback tone and emotion detection
- [ ] **Theme extraction** for common feedback patterns and topics
- [ ] **User satisfaction tracking** with Net Promoter Score (NPS) calculation
- [ ] **Feature request analysis** with demand scoring and prioritization
- [ ] **Feedback trend reporting** with time-based analysis and predictions

## Technical Context

### Current System State

From `docs/stories/5-3-real-time-dashboard-system-health.md`:

- Dashboard framework with user authentication and role-based access
- Real-time UI components and WebSocket integration
- User management and session handling

From `docs/stories/5-6-alert-system-for-critical-issues.md`:

- Notification system for user communications
- Alert management with assignment and resolution tracking
- Multi-channel notification delivery

From `docs/stories/2-3-development-plan-generation-with-approval-checkpoint.md`:

- Issue and story management system
- Development workflow with status tracking
- Team assignment and collaboration features

### Feedback System Architecture

```typescript
// Feedback system architecture
┌─────────────────┐    ┌──────────────────┐    ┌─────────────────┐
│   Feedback UI   │    │  Feedback       │    │  Feedback Store │
│   (Components)  │◄──►│  Service        │◄──►│  (PostgreSQL)   │
│                 │    │                  │    │                 │
│ - In-app Forms  │    │ - Collection     │    │ - Feedback      │
│ - Dashboard     │    │ - Processing     │    │ - Responses     │
│ - Surveys       │    │ - Analysis       │    │ - Analytics     │
│ - Analytics     │    │ - Integration    │    │ - Users         │
└─────────────────┘    └──────────────────┘    └─────────────────┘
         │                       │                       │
         │              ┌──────────────────┐              │
         └──────────────►│  Analysis Engine │◄─────────────┘
                        │  (ML/NLP)       │
                        │                  │
                        │ - Sentiment      │
                        │ - Themes         │
                        │ - Classification │
                        │ - Prioritization │
                        └──────────────────┘
         │
         ▼
┌─────────────────┐    ┌──────────────────┐    ┌─────────────────┐
│   External      │    │  Development     │    │  Notification   │
│   Channels      │    │  Workflow        │    │  Service        │
│                 │    │                  │    │                 │
│ - Email         │◄──►│ - Issue Creation │◄──►│ - Updates       │
│ - Slack         │    │ - Story Mapping  │    │ - Notifications │
│ - Web Forms     │    │ - Status Updates │    │ - Surveys       │
│ - API           │    │ - Release Notes  │    │ - Thank You     │
└─────────────────┘    └──────────────────┘    └─────────────────┘
```

## Technical Implementation

### 1. Feedback System Data Models

#### Feedback Types and Interfaces

```typescript
// packages/feedback/src/types/feedback.types.ts
export interface Feedback {
  id: string;
  type: FeedbackType;
  category: FeedbackCategory;
  title: string;
  description: string;

  // User information
  userId?: string;
  userEmail?: string;
  userName?: string;
  anonymous: boolean;

  // Context information
  context: FeedbackContext;
  metadata: FeedbackMetadata;

  // Content analysis
  sentiment?: SentimentAnalysis;
  themes?: string[];
  tags: string[];
  severity: FeedbackSeverity;
  priority: FeedbackPriority;

  // Status and assignment
  status: FeedbackStatus;
  assignedTo?: string;
  assignedTeam?: string;

  // Related entities
  issueId?: string;
  storyId?: string;
  featureId?: string;
  releaseId?: string;

  // Timestamps
  createdAt: string;
  updatedAt: string;
  respondedAt?: string;
  resolvedAt?: string;

  // Response tracking
  responses: FeedbackResponse[];
  satisfactionScore?: number; // 1-5 rating
  wouldRecommend?: number; // NPS 0-10

  // Analytics
  viewCount: number;
  upvoteCount: number;
  duplicateCount: number;
}

export type FeedbackType =
  | 'bug_report'
  | 'feature_request'
  | 'improvement'
  | 'general_feedback'
  | 'complaint'
  | 'compliment'
  | 'question'
  | 'usability_issue';

export type FeedbackCategory =
  | 'ui_ux'
  | 'performance'
  | 'functionality'
  | 'documentation'
  | 'integration'
  | 'security'
  | 'reliability'
  | 'accessibility'
  | 'other';

export type FeedbackSeverity = 'low' | 'medium' | 'high' | 'critical';
export type FeedbackPriority = 'low' | 'medium' | 'high' | 'urgent';
export type FeedbackStatus =
  | 'new'
  | 'under_review'
  | 'acknowledged'
  | 'in_progress'
  | 'resolved'
  | 'closed'
  | 'duplicate'
  | 'not_applicable';

export interface FeedbackContext {
  // System context
  version?: string;
  environment?: string;
  component?: string;
  page?: string;
  feature?: string;

  // User context
  role?: string;
  plan?: string;
  usageLevel?: 'new' | 'occasional' | 'regular' | 'power';

  // Session context
  sessionId?: string;
  action?: string;
  workflow?: string;

  // Technical context
  browser?: string;
  os?: string;
  device?: string;
  screenResolution?: string;

  // Business context
  organization?: string;
  team?: string;
  project?: string;
}

export interface FeedbackMetadata {
  source: FeedbackSource;
  campaign?: string;
  referrer?: string;
  userAgent?: string;
  ipAddress?: string;

  // Processing metadata
  processedAt?: string;
  analyzedAt?: string;
  autoCategorized?: boolean;
  autoPrioritized?: boolean;

  // Quality metrics
  completeness: number; // 0-100
  clarity: number; // 0-100
  actionability: number; // 0-100

  // Duplicate detection
  duplicateOf?: string;
  similarityScore?: number;

  // Analytics
  searchTerms?: string[];
  relatedFeedback?: string[];
}

export type FeedbackSource =
  | 'in_app'
  | 'dashboard'
  | 'email'
  | 'slack'
  | 'web_form'
  | 'api'
  | 'interview'
  | 'survey';

export interface SentimentAnalysis {
  score: number; // -1 to 1 (negative to positive)
  magnitude: number; // 0 to 1 (emotional intensity)
  label: 'negative' | 'neutral' | 'positive';
  confidence: number; // 0 to 1
  emotions?: {
    joy: number;
    anger: number;
    fear: number;
    sadness: number;
    disgust: number;
    surprise: number;
  };
}

export interface FeedbackResponse {
  id: string;
  feedbackId: string;
  type: ResponseType;
  content: string;
  author: string;
  authorRole: string;
  internal: boolean;

  // Timestamps
  createdAt: string;
  updatedAt: string;

  // Response metadata
  template?: string;
  automated: boolean;
  satisfactionRequest?: boolean;
}

export type ResponseType =
  | 'acknowledgment'
  | 'clarification'
  | 'update'
  | 'resolution'
  | 'rejection'
  | 'information'
  | 'survey';

export interface FeedbackSurvey {
  id: string;
  name: string;
  description: string;
  type: SurveyType;
  status: SurveyStatus;

  // Survey configuration
  questions: SurveyQuestion[];
  triggers: SurveyTrigger[];
  targetAudience: SurveyTarget;

  // Timing
  startDate?: string;
  endDate?: string;
  frequency?: SurveyFrequency;

  // Analytics
  responses: number;
  completionRate: number;
  averageTime: number; // minutes

  // Metadata
  createdBy: string;
  createdAt: string;
  updatedAt: string;
}

export type SurveyType =
  | 'satisfaction'
  | 'feature_validation'
  | 'user_research'
  | 'bug_triage'
  | 'product_feedback'
  | 'nps';

export type SurveyStatus = 'draft' | 'active' | 'paused' | 'completed' | 'archived';

export interface SurveyQuestion {
  id: string;
  type: QuestionType;
  text: string;
  description?: string;
  required: boolean;
  options?: string[];
  validation?: QuestionValidation;
  order: number;
}

export type QuestionType =
  | 'text'
  | 'textarea'
  | 'rating'
  | 'scale'
  | 'multiple_choice'
  | 'checkbox'
  | 'dropdown'
  | 'yes_no'
  | 'nps';

export interface QuestionValidation {
  minLength?: number;
  maxLength?: number;
  pattern?: string;
  min?: number;
  max?: number;
}

export interface SurveyTrigger {
  type: TriggerType;
  conditions: TriggerCondition[];
  delay?: number; // seconds
}

export type TriggerType =
  | 'page_visit'
  | 'feature_use'
  | 'time_spent'
  | 'error_occurred'
  | 'workflow_completion'
  | 'manual'
  | 'schedule';

export interface TriggerCondition {
  field: string;
  operator: 'eq' | 'ne' | 'gt' | 'gte' | 'lt' | 'lte' | 'contains' | 'in';
  value: any;
}

export interface SurveyTarget {
  users?: string[];
  roles?: string[];
  organizations?: string[];
  usageLevel?: string[];
  randomSample?: number; // percentage
}

export type SurveyFrequency =
  | 'once'
  | 'daily'
  | 'weekly'
  | 'monthly'
  | 'quarterly';

export interface FeedbackAnalytics {
  timeRange: TimeRange;

  // Volume metrics
  totalFeedback: number;
  feedbackByType: Record<FeedbackType, number>;
  feedbackByCategory: Record<FeedbackCategory, number>;
  feedbackBySource: Record<FeedbackSource, number;

  // Quality metrics
  averageCompleteness: number;
  averageClarity: number;
  averageActionability: number;

  // Sentiment metrics
  averageSentiment: number;
  sentimentDistribution: Record<string, number>;
  npsScore: number;
  satisfactionScore: number;

  // Processing metrics
  responseRate: number;
  resolutionRate: number;
  averageResponseTime: number; // hours
  averageResolutionTime: number; // hours

  // Trends
  trends: FeedbackTrend[];
  themes: ThemeAnalysis[];
  topIssues: TopIssue[];

  // Predictions
  predictions: FeedbackPrediction[];
}

export interface FeedbackTrend {
  period: string;
  total: number;
  positive: number;
  neutral: number;
  negative: number;
  byType: Record<FeedbackType, number>;
}

export interface ThemeAnalysis {
  theme: string;
  frequency: number;
  sentiment: number;
  trend: 'increasing' | 'decreasing' | 'stable';
  relatedFeedback: string[];
  suggestedActions: string[];
}

export interface TopIssue {
  title: string;
  count: number;
  severity: FeedbackSeverity;
  sentiment: number;
  status: FeedbackStatus;
  trending: boolean;
}

export interface FeedbackPrediction {
  type: 'volume' | 'sentiment' | 'theme' | 'issue';
  prediction: any;
  confidence: number;
  timeFrame: string;
  factors: string[];
}

export interface TimeRange {
  start: string;
  end: string;
  relative?: string;
}
```

### 2. Feedback Service Implementation

#### Core Feedback Management Service

```typescript
// packages/feedback/src/services/feedback.service.ts
import { EventEmitter } from 'events';
import { Logger } from 'pino';
import {
  Feedback,
  FeedbackType,
  FeedbackCategory,
  FeedbackSeverity,
  FeedbackStatus,
  FeedbackContext,
  FeedbackMetadata,
  FeedbackSource,
  SentimentAnalysis,
  FeedbackResponse,
  FeedbackAnalytics,
} from '../types/feedback.types';
import { FeedbackStore } from './feedback.store';
import { AnalysisEngine } from './analysis.engine';
import { NotificationService } from './notification.service';
import { WorkflowIntegration } from './workflow.integration';

export class FeedbackService extends EventEmitter {
  constructor(
    private feedbackStore: FeedbackStore,
    private analysisEngine: AnalysisEngine,
    private notificationService: NotificationService,
    private workflowIntegration: WorkflowIntegration,
    private logger: Logger
  ) {
    super();
  }

  async submitFeedback(submission: FeedbackSubmission): Promise<Feedback> {
    try {
      // Create feedback object
      const feedback = await this.createFeedback(submission);

      // Analyze feedback content
      await this.analyzeFeedback(feedback);

      // Check for duplicates
      await this.checkForDuplicates(feedback);

      // Auto-prioritize if enabled
      await this.autoPrioritize(feedback);

      // Save feedback
      await this.feedbackStore.createFeedback(feedback);

      // Process workflows
      await this.processFeedbackWorkflows(feedback);

      // Send notifications
      await this.sendFeedbackNotifications(feedback);

      // Emit events
      this.emit('feedback-submitted', feedback);

      this.logger.info('Feedback submitted successfully', {
        feedbackId: feedback.id,
        type: feedback.type,
        category: feedback.category,
        userId: feedback.userId,
      });

      return feedback;
    } catch (error) {
      this.logger.error('Failed to submit feedback', { error, submission });
      throw error;
    }
  }

  async updateFeedback(
    feedbackId: string,
    updates: Partial<Feedback>,
    updatedBy: string
  ): Promise<Feedback> {
    try {
      const existingFeedback = await this.feedbackStore.getFeedback(feedbackId);
      if (!existingFeedback) {
        throw new Error(`Feedback not found: ${feedbackId}`);
      }

      // Apply updates
      const updatedFeedback = {
        ...existingFeedback,
        ...updates,
        updatedAt: new Date().toISOString(),
      };

      // Save updates
      await this.feedbackStore.updateFeedback(updatedFeedback);

      // Process workflow changes
      if (updates.status && updates.status !== existingFeedback.status) {
        await this.processStatusChange(updatedFeedback, existingFeedback.status, updatedBy);
      }

      // Emit events
      this.emit('feedback-updated', updatedFeedback, existingFeedback);

      this.logger.info('Feedback updated successfully', {
        feedbackId,
        updates: Object.keys(updates),
        updatedBy,
      });

      return updatedFeedback;
    } catch (error) {
      this.logger.error('Failed to update feedback', { error, feedbackId, updates });
      throw error;
    }
  }

  async addResponse(
    feedbackId: string,
    response: Omit<FeedbackResponse, 'id' | 'feedbackId' | 'createdAt'>
  ): Promise<FeedbackResponse> {
    try {
      const feedbackResponse: FeedbackResponse = {
        ...response,
        id: this.generateResponseId(),
        feedbackId,
        createdAt: new Date().toISOString(),
      };

      // Save response
      await this.feedbackStore.createResponse(feedbackResponse);

      // Update feedback
      await this.updateFeedback(
        feedbackId,
        {
          respondedAt: feedbackResponse.createdAt,
          responses: [], // Will be loaded from store
        },
        response.author
      );

      // Send notification if it's a public response
      if (!response.internal && feedbackId) {
        await this.sendResponseNotification(feedbackId, feedbackResponse);
      }

      // Emit events
      this.emit('response-added', feedbackResponse);

      this.logger.info('Response added successfully', {
        responseId: feedbackResponse.id,
        feedbackId,
        author: response.author,
      });

      return feedbackResponse;
    } catch (error) {
      this.logger.error('Failed to add response', { error, feedbackId, response });
      throw error;
    }
  }

  async getFeedbackAnalytics(timeRange: TimeRange): Promise<FeedbackAnalytics> {
    try {
      const analytics = await this.feedbackStore.getAnalytics(timeRange);

      // Enhance with analysis engine insights
      const enhancedAnalytics = await this.analysisEngine.enhanceAnalytics(analytics);

      return enhancedAnalytics;
    } catch (error) {
      this.logger.error('Failed to get feedback analytics', { error, timeRange });
      throw error;
    }
  }

  async searchFeedback(query: FeedbackSearchQuery): Promise<FeedbackSearchResult> {
    try {
      return await this.feedbackStore.searchFeedback(query);
    } catch (error) {
      this.logger.error('Failed to search feedback', { error, query });
      throw error;
    }
  }

  private async createFeedback(submission: FeedbackSubmission): Promise<Feedback> {
    const feedbackId = this.generateFeedbackId();

    // Extract context from submission
    const context = await this.extractContext(submission);

    // Create metadata
    const metadata: FeedbackMetadata = {
      source: submission.source,
      campaign: submission.campaign,
      referrer: submission.referrer,
      userAgent: submission.userAgent,
      completeness: this.calculateCompleteness(submission),
      clarity: this.calculateClarity(submission),
      actionability: this.calculateActionability(submission),
    };

    const feedback: Feedback = {
      id: feedbackId,
      type: submission.type,
      category: submission.category || 'other',
      title: submission.title,
      description: submission.description,

      // User information
      userId: submission.userId,
      userEmail: submission.userEmail,
      userName: submission.userName,
      anonymous: submission.anonymous || false,

      // Context and metadata
      context,
      metadata,
      tags: submission.tags || [],
      severity: submission.severity || 'medium',
      priority: submission.priority || 'medium',

      // Status
      status: 'new',

      // Timestamps
      createdAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),

      // Response tracking
      responses: [],

      // Analytics
      viewCount: 0,
      upvoteCount: 0,
      duplicateCount: 0,
    };

    return feedback;
  }

  private async analyzeFeedback(feedback: Feedback): Promise<void> {
    try {
      // Sentiment analysis
      const sentiment = await this.analysisEngine.analyzeSentiment(feedback.description);
      feedback.sentiment = sentiment;

      // Theme extraction
      const themes = await this.analysisEngine.extractThemes(feedback.description);
      feedback.themes = themes;

      // Auto-categorization
      if (!feedback.category || feedback.category === 'other') {
        const suggestedCategory = await this.analysisEngine.categorizeFeedback(feedback);
        feedback.category = suggestedCategory;
        feedback.metadata.autoCategorized = true;
      }

      // Auto-severity detection
      if (feedback.severity === 'medium') {
        const suggestedSeverity = await this.analysisEngine.assessSeverity(feedback);
        feedback.severity = suggestedSeverity;
        feedback.metadata.autoPrioritized = true;
      }

      feedback.metadata.analyzedAt = new Date().toISOString();
    } catch (error) {
      this.logger.warn('Feedback analysis failed', {
        feedbackId: feedback.id,
        error,
      });
    }
  }

  private async checkForDuplicates(feedback: Feedback): Promise<void> {
    try {
      const duplicates = await this.feedbackStore.findSimilarFeedback(feedback);

      if (duplicates.length > 0) {
        const bestMatch = duplicates[0];

        // Mark as duplicate if similarity is high enough
        if (bestMatch.similarityScore > 0.8) {
          feedback.status = 'duplicate';
          feedback.metadata.duplicateOf = bestMatch.feedbackId;
          feedback.metadata.similarityScore = bestMatch.similarityScore;

          // Update original feedback
          await this.feedbackStore.incrementDuplicateCount(bestMatch.feedbackId);
        }
      }
    } catch (error) {
      this.logger.warn('Duplicate check failed', {
        feedbackId: feedback.id,
        error,
      });
    }
  }

  private async autoPrioritize(feedback: Feedback): Promise<void> {
    try {
      const priorityScore = await this.analysisEngine.calculatePriorityScore(feedback);

      // Map score to priority
      if (priorityScore >= 0.8) {
        feedback.priority = 'urgent';
      } else if (priorityScore >= 0.6) {
        feedback.priority = 'high';
      } else if (priorityScore >= 0.4) {
        feedback.priority = 'medium';
      } else {
        feedback.priority = 'low';
      }

      feedback.metadata.autoPrioritized = true;
    } catch (error) {
      this.logger.warn('Auto-prioritization failed', {
        feedbackId: feedback.id,
        error,
      });
    }
  }

  private async processFeedbackWorkflows(feedback: Feedback): Promise<void> {
    try {
      // Auto-assign based on category and type
      const assignment = await this.analysisEngine.suggestAssignment(feedback);
      if (assignment) {
        feedback.assignedTo = assignment.userId;
        feedback.assignedTeam = assignment.teamId;
      }

      // Create issue for bug reports and high-priority items
      if (
        (feedback.type === 'bug_report' || feedback.priority === 'urgent') &&
        feedback.status !== 'duplicate'
      ) {
        const issue = await this.workflowIntegration.createIssue(feedback);
        feedback.issueId = issue.id;
      }

      // Create story for feature requests
      if (feedback.type === 'feature_request' && feedback.status !== 'duplicate') {
        const story = await this.workflowIntegration.createStory(feedback);
        feedback.storyId = story.id;
      }
    } catch (error) {
      this.logger.warn('Workflow processing failed', {
        feedbackId: feedback.id,
        error,
      });
    }
  }

  private async sendFeedbackNotifications(feedback: Feedback): Promise<void> {
    try {
      // Notify assigned user/team
      if (feedback.assignedTo || feedback.assignedTeam) {
        await this.notificationService.sendAssignmentNotification(feedback);
      }

      // Notify product team for feature requests
      if (feedback.type === 'feature_request') {
        await this.notificationService.sendProductNotification(feedback);
      }

      // Notify engineering team for bug reports
      if (feedback.type === 'bug_report') {
        await this.notificationService.sendEngineeringNotification(feedback);
      }

      // Send acknowledgment to user (if not anonymous)
      if (!feedback.anonymous && feedback.userEmail) {
        await this.notificationService.sendAcknowledgment(feedback);
      }
    } catch (error) {
      this.logger.warn('Notification sending failed', {
        feedbackId: feedback.id,
        error,
      });
    }
  }

  private async processStatusChange(
    feedback: Feedback,
    oldStatus: FeedbackStatus,
    updatedBy: string
  ): Promise<void> {
    // Send status change notifications
    await this.notificationService.sendStatusChangeNotification(feedback, oldStatus, updatedBy);

    // Update related entities
    if (feedback.issueId) {
      await this.workflowIntegration.updateIssueStatus(feedback.issueId, feedback.status);
    }

    if (feedback.storyId) {
      await this.workflowIntegration.updateStoryStatus(feedback.storyId, feedback.status);
    }

    // Request satisfaction survey for resolved feedback
    if (feedback.status === 'resolved' && !feedback.anonymous && feedback.userEmail) {
      await this.notificationService.sendSatisfactionSurvey(feedback);
    }
  }

  private async sendResponseNotification(
    feedbackId: string,
    response: FeedbackResponse
  ): Promise<void> {
    const feedback = await this.feedbackStore.getFeedback(feedbackId);
    if (feedback && !feedback.anonymous && feedback.userEmail) {
      await this.notificationService.sendResponseNotification(feedback, response);
    }
  }

  private async extractContext(submission: FeedbackSubmission): Promise<FeedbackContext> {
    const context: FeedbackContext = {};

    // Extract from submission
    if (submission.context) {
      Object.assign(context, submission.context);
    }

    // Extract from user agent
    if (submission.userAgent) {
      const ua = this.parseUserAgent(submission.userAgent);
      context.browser = ua.browser;
      context.os = ua.os;
      context.device = ua.device;
    }

    // Extract from session
    if (submission.sessionId) {
      const sessionContext = await this.getSessionContext(submission.sessionId);
      Object.assign(context, sessionContext);
    }

    return context;
  }

  private calculateCompleteness(submission: FeedbackSubmission): number {
    let score = 0;
    let maxScore = 0;

    // Title (20 points)
    maxScore += 20;
    if (submission.title && submission.title.length >= 10) {
      score += 20;
    }

    // Description (40 points)
    maxScore += 40;
    if (submission.description && submission.description.length >= 50) {
      score += 40;
    } else if (submission.description && submission.description.length >= 20) {
      score += 20;
    }

    // Category (10 points)
    maxScore += 10;
    if (submission.category && submission.category !== 'other') {
      score += 10;
    }

    // Type (10 points)
    maxScore += 10;
    if (submission.type) {
      score += 10;
    }

    // User info (20 points)
    maxScore += 20;
    if (!submission.anonymous && (submission.userId || submission.userEmail)) {
      score += 20;
    }

    return Math.round((score / maxScore) * 100);
  }

  private calculateClarity(submission: FeedbackSubmission): number {
    // Simple clarity calculation based on text quality
    let score = 50; // Base score

    const text = `${submission.title || ''} ${submission.description || ''}`;

    // Length bonus
    if (text.length >= 100) {
      score += 20;
    }

    // Structure bonus (has both title and description)
    if (submission.title && submission.description) {
      score += 15;
    }

    // Specificity bonus (contains specific details)
    const specificKeywords = ['error', 'bug', 'feature', 'improvement', 'suggestion', 'problem'];
    const hasSpecificKeywords = specificKeywords.some((keyword) =>
      text.toLowerCase().includes(keyword)
    );
    if (hasSpecificKeywords) {
      score += 15;
    }

    return Math.min(100, score);
  }

  private calculateActionability(submission: FeedbackSubmission): number {
    let score = 30; // Base score

    const text = `${submission.title || ''} ${submission.description || ''}`.toLowerCase();

    // Action-oriented language
    const actionWords = [
      'should',
      'could',
      'would',
      'need',
      'want',
      'add',
      'fix',
      'improve',
      'change',
    ];
    const actionWordCount = actionWords.filter((word) => text.includes(word)).length;
    score += Math.min(actionWordCount * 10, 40);

    // Specific suggestions
    if (text.includes('example') || text.includes('step') || text.includes('reproduce')) {
      score += 15;
    }

    // Clear problem statement
    if (text.includes('problem') || text.includes('issue') || text.includes('challenge')) {
      score += 15;
    }

    return Math.min(100, score);
  }

  private parseUserAgent(userAgent: string): { browser: string; os: string; device: string } {
    // Simple user agent parsing - in production, use a proper library
    const ua = userAgent.toLowerCase();

    let browser = 'unknown';
    let os = 'unknown';
    let device = 'desktop';

    // Browser detection
    if (ua.includes('chrome')) browser = 'chrome';
    else if (ua.includes('firefox')) browser = 'firefox';
    else if (ua.includes('safari')) browser = 'safari';
    else if (ua.includes('edge')) browser = 'edge';

    // OS detection
    if (ua.includes('windows')) os = 'windows';
    else if (ua.includes('mac')) os = 'macos';
    else if (ua.includes('linux')) os = 'linux';
    else if (ua.includes('android')) os = 'android';
    else if (ua.includes('ios')) os = 'ios';

    // Device detection
    if (ua.includes('mobile') || ua.includes('android') || ua.includes('ios')) {
      device = 'mobile';
    } else if (ua.includes('tablet')) {
      device = 'tablet';
    }

    return { browser, os, device };
  }

  private async getSessionContext(sessionId: string): Promise<Partial<FeedbackContext>> {
    // This would integrate with session management
    // For now, return empty context
    return {};
  }

  // Helper methods
  private generateFeedbackId(): string {
    return `feedback-${Date.now()}-${Math.random().toString(36).substr(2, 9)}`;
  }

  private generateResponseId(): string {
    return `response-${Date.now()}-${Math.random().toString(36).substr(2, 9)}`;
  }
}

// Supporting interfaces
export interface FeedbackSubmission {
  type: FeedbackType;
  category?: FeedbackCategory;
  title: string;
  description: string;

  // User information
  userId?: string;
  userEmail?: string;
  userName?: string;
  anonymous?: boolean;

  // Context
  context?: Partial<FeedbackContext>;

  // Metadata
  source: FeedbackSource;
  campaign?: string;
  referrer?: string;
  userAgent?: string;
  sessionId?: string;

  // Classification
  tags?: string[];
  severity?: FeedbackSeverity;
  priority?: FeedbackPriority;
}

export interface FeedbackSearchQuery {
  text?: string;
  type?: FeedbackType[];
  category?: FeedbackCategory[];
  severity?: FeedbackSeverity[];
  priority?: FeedbackPriority[];
  status?: FeedbackStatus[];
  userId?: string[];
  assignedTo?: string[];
  dateRange?: TimeRange;
  tags?: string[];
  sentiment?: string[];
  limit?: number;
  offset?: number;
  sortBy?: string;
  sortOrder?: 'asc' | 'desc';
}

export interface FeedbackSearchResult {
  feedback: Feedback[];
  total: number;
  facets: {
    types: Record<FeedbackType, number>;
    categories: Record<FeedbackCategory, number>;
    severities: Record<FeedbackSeverity, number>;
    priorities: Record<FeedbackPriority, number>;
    statuses: Record<FeedbackStatus, number>;
  };
  suggestions?: string[];
}
```

### 3. Feedback UI Components

#### Feedback Collection Widget

```typescript
// packages/feedback/src/components/FeedbackWidget.tsx
import React, { useState } from 'react';
import { Card, CardContent, CardHeader, CardTitle } from '../ui/card';
import { Button } from '../ui/button';
import { Input } from '../ui/input';
import { Textarea } from '../ui/textarea';
import { Select } from '../ui/select';
import { Badge } from '../ui/badge';
import { FeedbackType, FeedbackCategory, FeedbackSeverity } from '../types/feedback.types';

interface FeedbackWidgetProps {
  onSubmit: (feedback: FeedbackSubmission) => Promise<void>;
  initialType?: FeedbackType;
  initialCategory?: FeedbackCategory;
  context?: any;
  showTitle?: boolean;
  compact?: boolean;
}

export function FeedbackWidget({
  onSubmit,
  initialType = 'general_feedback',
  initialCategory,
  context,
  showTitle = true,
  compact = false
}: FeedbackWidgetProps) {
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [submitted, setSubmitted] = useState(false);

  const [formData, setFormData] = useState({
    type: initialType,
    category: initialCategory || 'other',
    title: '',
    description: '',
    severity: 'medium' as FeedbackSeverity,
    anonymous: false,
    tags: [] as string[]
  });

  const [errors, setErrors] = useState<Record<string, string>>({});

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();

    // Validation
    const newErrors: Record<string, string> = {};

    if (!formData.title.trim()) {
      newErrors.title = 'Title is required';
    }

    if (!formData.description.trim()) {
      newErrors.description = 'Description is required';
    } else if (formData.description.length < 20) {
      newErrors.description = 'Please provide more details (at least 20 characters)';
    }

    if (Object.keys(newErrors).length > 0) {
      setErrors(newErrors);
      return;
    }

    setIsSubmitting(true);
    setErrors({});

    try {
      const submission: FeedbackSubmission = {
        type: formData.type,
        category: formData.category,
        title: formData.title.trim(),
        description: formData.description.trim(),
        severity: formData.severity,
        anonymous: formData.anonymous,
        tags: formData.tags,
        source: 'in_app',
        context
      };

      await onSubmit(submission);
      setSubmitted(true);

      // Reset form after successful submission
      setTimeout(() => {
        setSubmitted(false);
        setFormData({
          type: initialType,
          category: initialCategory || 'other',
          title: '',
          description: '',
          severity: 'medium',
          anonymous: false,
          tags: []
        });
      }, 3000);

    } catch (error) {
      setErrors({ submit: 'Failed to submit feedback. Please try again.' });
    } finally {
      setIsSubmitting(false);
    }
  };

  const getTypeIcon = (type: FeedbackType) => {
    const icons = {
      bug_report: '🐛',
      feature_request: '💡',
      improvement: '⚡',
      general_feedback: '💬',
      complaint: '😞',
      compliment: '👍',
      question: '❓',
      usability_issue: '🎨'
    };
    return icons[type] || '💬';
  };

  const getTypeDescription = (type: FeedbackType) => {
    const descriptions = {
      bug_report: 'Report a problem or unexpected behavior',
      feature_request: 'Suggest a new feature or enhancement',
      improvement: 'Suggest improvements to existing features',
      general_feedback: 'Share your general thoughts and opinions',
      complaint: 'Express dissatisfaction or concerns',
      compliment: 'Share positive experiences and appreciation',
      question: 'Ask questions about the product',
      usability_issue: 'Report usability or accessibility problems'
    };
    return descriptions[type] || 'Share your feedback';
  };

  if (submitted) {
    return (
      <Card>
        <CardContent className="p-6 text-center">
          <div className="text-4xl mb-4">✅</div>
          <h3 className="text-lg font-semibold mb-2">Thank You!</h3>
          <p className="text-gray-600">
            Your feedback has been submitted successfully. We appreciate your input!
          </p>
        </CardContent>
      </Card>
    );
  }

  return (
    <Card>
      {showTitle && (
        <CardHeader>
          <CardTitle className="flex items-center gap-2">
            <span>Share Your Feedback</span>
            <Badge variant="outline" className="text-xs">
              We value your input
            </Badge>
          </CardTitle>
        </CardHeader>
      )}

      <CardContent className={compact ? 'p-4' : 'p-6'}>
        <form onSubmit={handleSubmit} className="space-y-4">
          {/* Feedback Type */}
          <div>
            <label className="block text-sm font-medium mb-2">
              What type of feedback would you like to share?
            </label>
            <Select
              value={formData.type}
              onChange={(value) => setFormData(prev => ({ ...prev, type: value as FeedbackType }))}
              className="w-full"
            >
              <option value="bug_report">🐛 Bug Report</option>
              <option value="feature_request">💡 Feature Request</option>
              <option value="improvement">⚡ Improvement</option>
              <option value="general_feedback">💬 General Feedback</option>
              <option value="complaint">😞 Complaint</option>
              <option value="compliment">👍 Compliment</option>
              <option value="question">❓ Question</option>
              <option value="usability_issue">🎨 Usability Issue</option>
            </Select>
            <p className="text-xs text-gray-500 mt-1">
              {getTypeDescription(formData.type)}
            </p>
          </div>

          {/* Category */}
          <div>
            <label className="block text-sm font-medium mb-2">
              Category
            </label>
            <Select
              value={formData.category}
              onChange={(value) => setFormData(prev => ({ ...prev, category: value as FeedbackCategory }))}
              className="w-full"
            >
              <option value="ui_ux">UI/UX</option>
              <option value="performance">Performance</option>
              <option value="functionality">Functionality</option>
              <option value="documentation">Documentation</option>
              <option value="integration">Integration</option>
              <option value="security">Security</option>
              <option value="reliability">Reliability</option>
              <option value="accessibility">Accessibility</option>
              <option value="other">Other</option>
            </Select>
          </div>

          {/* Title */}
          <div>
            <label className="block text-sm font-medium mb-2">
              Title *
            </label>
            <Input
              value={formData.title}
              onChange={(e) => setFormData(prev => ({ ...prev, title: e.target.value }))}
              placeholder="Brief summary of your feedback"
              className={errors.title ? 'border-red-500' : ''}
            />
            {errors.title && (
              <p className="text-red-500 text-sm mt-1">{errors.title}</p>
            )}
          </div>

          {/* Description */}
          <div>
            <label className="block text-sm font-medium mb-2">
              Description *
            </label>
            <Textarea
              value={formData.description}
              onChange={(e) => setFormData(prev => ({ ...prev, description: e.target.value }))}
              placeholder="Please provide detailed information about your feedback..."
              rows={compact ? 4 : 6}
              className={errors.description ? 'border-red-500' : ''}
            />
            {errors.description && (
              <p className="text-red-500 text-sm mt-1">{errors.description}</p>
            )}
            <p className="text-xs text-gray-500 mt-1">
              {formData.description.length}/500 characters
            </p>
          </div>

          {/* Severity */}
          {(formData.type === 'bug_report' || formData.type === 'usability_issue') && (
            <div>
              <label className="block text-sm font-medium mb-2">
                How severe is this issue?
              </label>
              <Select
                value={formData.severity}
                onChange={(value) => setFormData(prev => ({ ...prev, severity: value as FeedbackSeverity }))}
                className="w-full"
              >
                <option value="low">Low - Minor inconvenience</option>
                <option value="medium">Medium - Affects usability</option>
                <option value="high">High - Major impact on workflow</option>
                <option value="critical">Critical - Blocks essential functionality</option>
              </Select>
            </div>
          )}

          {/* Anonymous Option */}
          <div className="flex items-center gap-2">
            <input
              type="checkbox"
              id="anonymous"
              checked={formData.anonymous}
              onChange={(e) => setFormData(prev => ({ ...prev, anonymous: e.target.checked }))}
              className="rounded"
            />
            <label htmlFor="anonymous" className="text-sm">
              Submit feedback anonymously
            </label>
          </div>

          {/* Error Message */}
          {errors.submit && (
            <div className="bg-red-50 border border-red-200 rounded-md p-3">
              <p className="text-red-700 text-sm">{errors.submit}</p>
            </div>
          )}

          {/* Submit Button */}
          <Button
            type="submit"
            disabled={isSubmitting}
            className="w-full"
          >
            {isSubmitting ? 'Submitting...' : 'Submit Feedback'}
          </Button>

          {/* Help Text */}
          <p className="text-xs text-gray-500 text-center">
            Your feedback helps us improve Tamma. We read every submission carefully.
          </p>
        </form>
      </CardContent>
    </Card>
  );
}
```

#### Feedback Management Dashboard

```typescript
// packages/feedback/src/components/FeedbackDashboard.tsx
import React, { useState, useEffect } from 'react';
import { Card, CardContent, CardHeader, CardTitle } from '../ui/card';
import { Button } from '../ui/button';
import { Input } from '../ui/input';
import { Select } from '../ui/select';
import { Badge } from '../ui/badge';
import { Feedback, FeedbackType, FeedbackStatus, FeedbackSeverity } from '../types/feedback.types';

interface FeedbackDashboardProps {
  feedback: Feedback[];
  onFeedbackUpdate: (feedbackId: string, updates: Partial<Feedback>) => void;
  onAddResponse: (feedbackId: string, response: any) => void;
}

export function FeedbackDashboard({
  feedback,
  onFeedbackUpdate,
  onAddResponse
}: FeedbackDashboardProps) {
  const [selectedFeedback, setSelectedFeedback] = useState<Feedback | null>(null);
  const [filters, setFilters] = useState({
    type: [] as FeedbackType[],
    status: [] as FeedbackStatus[],
    severity: [] as FeedbackSeverity[],
    search: ''
  });
  const [showResponseForm, setShowResponseForm] = useState(false);

  const filteredFeedback = feedback.filter(item => {
    if (filters.type.length > 0 && !filters.type.includes(item.type)) {
      return false;
    }
    if (filters.status.length > 0 && !filters.status.includes(item.status)) {
      return false;
    }
    if (filters.severity.length > 0 && !filters.severity.includes(item.severity)) {
      return false;
    }
    if (filters.search && !item.title.toLowerCase().includes(filters.search.toLowerCase()) &&
        !item.description.toLowerCase().includes(filters.search.toLowerCase())) {
      return false;
    }
    return true;
  });

  const getStatusColor = (status: FeedbackStatus) => {
    switch (status) {
      case 'new': return 'bg-blue-100 text-blue-800';
      case 'under_review': return 'bg-yellow-100 text-yellow-800';
      case 'acknowledged': return 'bg-green-100 text-green-800';
      case 'in_progress': return 'bg-purple-100 text-purple-800';
      case 'resolved': return 'bg-gray-100 text-gray-800';
      case 'closed': return 'bg-gray-100 text-gray-800';
      case 'duplicate': return 'bg-orange-100 text-orange-800';
      default: return 'bg-gray-100 text-gray-800';
    }
  };

  const getSeverityColor = (severity: FeedbackSeverity) => {
    switch (severity) {
      case 'critical': return 'bg-red-500';
      case 'high': return 'bg-red-400';
      case 'medium': return 'bg-yellow-500';
      case 'low': return 'bg-green-500';
      default: return 'bg-gray-500';
    }
  };

  const getTypeIcon = (type: FeedbackType) => {
    const icons = {
      bug_report: '🐛',
      feature_request: '💡',
      improvement: '⚡',
      general_feedback: '💬',
      complaint: '😞',
      compliment: '👍',
      question: '❓',
      usability_issue: '🎨'
    };
    return icons[type] || '💬';
  };

  const handleStatusChange = (feedbackId: string, newStatus: FeedbackStatus) => {
    onFeedbackUpdate(feedbackId, { status: newStatus });
  };

  const handleAssignment = (feedbackId: string, assignee: string) => {
    onFeedbackUpdate(feedbackId, { assignedTo: assignee });
  };

  return (
    <div className="space-y-6">
      {/* Filters */}
      <Card>
        <CardHeader>
          <CardTitle>Feedback Management</CardTitle>
        </CardHeader>
        <CardContent>
          <div className="flex gap-4 mb-4">
            <Input
              placeholder="Search feedback..."
              value={filters.search}
              onChange={(e) => setFilters(prev => ({ ...prev, search: e.target.value }))}
              className="flex-1"
            />

            <Select
              multiple
              value={filters.type}
              onChange={(values) => setFilters(prev => ({ ...prev, type: values as FeedbackType[] }))}
              placeholder="Type"
            >
              <option value="bug_report">Bug Report</option>
              <option value="feature_request">Feature Request</option>
              <option value="improvement">Improvement</option>
              <option value="general_feedback">General Feedback</option>
            </Select>

            <Select
              multiple
              value={filters.status}
              onChange={(values) => setFilters(prev => ({ ...prev, status: values as FeedbackStatus[] }))}
              placeholder="Status"
            >
              <option value="new">New</option>
              <option value="under_review">Under Review</option>
              <option value="acknowledged">Acknowledged</option>
              <option value="in_progress">In Progress</option>
              <option value="resolved">Resolved</option>
            </Select>

            <Select
              multiple
              value={filters.severity}
              onChange={(values) => setFilters(prev => ({ ...prev, severity: values as FeedbackSeverity[] }))}
              placeholder="Severity"
            >
              <option value="critical">Critical</option>
              <option value="high">High</option>
              <option value="medium">Medium</option>
              <option value="low">Low</option>
            </Select>
          </div>

          <div className="text-sm text-gray-600">
            Showing {filteredFeedback.length} of {feedback.length} items
          </div>
        </CardContent>
      </Card>

      {/* Feedback List */}
      <div className="grid grid-cols-1 lg:grid-cols-2 gap-6">
        {/* Feedback Items */}
        <Card>
          <CardHeader>
            <CardTitle>Feedback Items</CardTitle>
          </CardHeader>
          <CardContent>
            <div className="space-y-3">
              {filteredFeedback.map(item => (
                <div
                  key={item.id}
                  className={`border rounded-lg p-4 cursor-pointer transition-colors ${
                    selectedFeedback?.id === item.id ? 'border-blue-500 bg-blue-50' : 'hover:bg-gray-50'
                  }`}
                  onClick={() => setSelectedFeedback(item)}
                >
                  <div className="flex items-start justify-between mb-2">
                    <div className="flex items-center gap-2">
                      <span className="text-lg">{getTypeIcon(item.type)}</span>
                      <h4 className="font-medium">{item.title}</h4>
                    </div>
                    <div className={`w-3 h-3 rounded-full ${getSeverityColor(item.severity)}`}></div>
                  </div>

                  <p className="text-sm text-gray-600 mb-3 line-clamp-2">
                    {item.description}
                  </p>

                  <div className="flex items-center justify-between">
                    <div className="flex gap-2">
                      <Badge className={getStatusColor(item.status)}>
                        {item.status.replace('_', ' ')}
                      </Badge>
                      <Badge variant="outline">
                        {item.type.replace('_', ' ')}
                      </Badge>
                    </div>

                    <div className="text-xs text-gray-500">
                      {new Date(item.createdAt).toLocaleDateString()}
                    </div>
                  </div>

                  {/* Metadata */}
                  <div className="mt-3 flex items-center gap-4 text-xs text-gray-500">
                    {item.assignedTo && (
                      <span>Assigned to: {item.assignedTo}</span>
                    )}
                    {item.responses.length > 0 && (
                      <span>{item.responses.length} responses</span>
                    )}
                    {item.upvoteCount > 0 && (
                      <span>👍 {item.upvoteCount}</span>
                    )}
                  </div>
                </div>
              ))}
            </div>
          </CardContent>
        </Card>

        {/* Feedback Details */}
        {selectedFeedback && (
          <Card>
            <CardHeader>
              <CardTitle className="flex items-center justify-between">
                <span>Feedback Details</span>
                <div className="flex gap-2">
                  <Button
                    variant="outline"
                    size="sm"
                    onClick={() => setShowResponseForm(!showResponseForm)}
                  >
                    Add Response
                  </Button>
                  <Select
                    value={selectedFeedback.status}
                    onChange={(value) => handleStatusChange(selectedFeedback.id, value as FeedbackStatus)}
                    size="sm"
                  >
                    <option value="new">New</option>
                    <option value="under_review">Under Review</option>
                    <option value="acknowledged">Acknowledged</option>
                    <option value="in_progress">In Progress</option>
                    <option value="resolved">Resolved</option>
                    <option value="closed">Closed</option>
                    <option value="duplicate">Duplicate</option>
                  </Select>
                </div>
              </CardTitle>
            </CardHeader>
            <CardContent>
              <div className="space-y-4">
                {/* Header Information */}
                <div>
                  <div className="flex items-center gap-2 mb-2">
                    <span className="text-xl">{getTypeIcon(selectedFeedback.type)}</span>
                    <h3 className="text-lg font-semibold">{selectedFeedback.title}</h3>
                  </div>

                  <div className="flex gap-2 mb-3">
                    <Badge className={getStatusColor(selectedFeedback.status)}>
                      {selectedFeedback.status.replace('_', ' ')}
                    </Badge>
                    <Badge variant="outline">
                      {selectedFeedback.type.replace('_', ' ')}
                    </Badge>
                    <Badge variant="outline">
                      {selectedFeedback.category.replace('_', ' ')}
                    </Badge>
                  </div>
                </div>

                {/* Description */}
                <div>
                  <h4 className="font-medium mb-2">Description</h4>
                  <p className="text-gray-700 whitespace-pre-wrap">
                    {selectedFeedback.description}
                  </p>
                </div>

                {/* Context Information */}
                <div>
                  <h4 className="font-medium mb-2">Context</h4>
                  <div className="bg-gray-50 p-3 rounded text-sm">
                    <div className="grid grid-cols-2 gap-2">
                      <div>
                        <span className="font-medium">Source:</span>
                        <span className="ml-2">{selectedFeedback.metadata.source}</span>
                      </div>
                      <div>
                        <span className="font-medium">Severity:</span>
                        <span className="ml-2">{selectedFeedback.severity}</span>
                      </div>
                      <div>
                        <span className="font-medium">Priority:</span>
                        <span className="ml-2">{selectedFeedback.priority}</span>
                      </div>
                      <div>
                        <span className="font-medium">Created:</span>
                        <span className="ml-2">{new Date(selectedFeedback.createdAt).toLocaleString()}</span>
                      </div>
                    </div>

                    {selectedFeedback.context && Object.keys(selectedFeedback.context).length > 0 && (
                      <div className="mt-3 pt-3 border-t">
                        <div className="font-medium mb-2">Technical Context:</div>
                        {Object.entries(selectedFeedback.context).map(([key, value]) => (
                          <div key={key}>
                            <span className="font-medium">{key}:</span>
                            <span className="ml-2">{value}</span>
                          </div>
                        ))}
                      </div>
                    )}
                  </div>
                </div>

                {/* Analysis Results */}
                {selectedFeedback.sentiment && (
                  <div>
                    <h4 className="font-medium mb-2">Analysis</h4>
                    <div className="bg-gray-50 p-3 rounded text-sm">
                      <div>
                        <span className="font-medium">Sentiment:</span>
                        <span className="ml-2 capitalize">{selectedFeedback.sentiment.label}</span>
                        <span className="ml-2 text-gray-500">
                          ({(selectedFeedback.sentiment.score * 100).toFixed(0)}% confidence)
                        </span>
                      </div>
                      {selectedFeedback.themes && selectedFeedback.themes.length > 0 && (
                        <div className="mt-2">
                          <span className="font-medium">Themes:</span>
                          <div className="flex flex-wrap gap-1 mt-1">
                            {selectedFeedback.themes.map((theme, index) => (
                              <Badge key={index} variant="outline" className="text-xs">
                                {theme}
                              </Badge>
                            ))}
                          </div>
                        </div>
                      )}
                    </div>
                  </div>
                )}

                {/* Responses */}
                {selectedFeedback.responses.length > 0 && (
                  <div>
                    <h4 className="font-medium mb-2">Responses</h4>
                    <div className="space-y-3">
                      {selectedFeedback.responses.map(response => (
                        <div key={response.id} className="border rounded p-3">
                          <div className="flex items-center justify-between mb-2">
                            <div className="flex items-center gap-2">
                              <span className="font-medium">{response.author}</span>
                              <Badge variant="outline" className="text-xs">
                                {response.authorRole}
                              </Badge>
                              {response.automated && (
                                <Badge variant="outline" className="text-xs">
                                  Automated
                                </Badge>
                              )}
                            </div>
                            <span className="text-xs text-gray-500">
                              {new Date(response.createdAt).toLocaleString()}
                            </span>
                          </div>
                          <p className="text-sm text-gray-700">{response.content}</p>
                        </div>
                      ))}
                    </div>
                  </div>
                )}

                {/* Response Form */}
                {showResponseForm && (
                  <ResponseForm
                    feedbackId={selectedFeedback.id}
                    onSubmit={(response) => {
                      onAddResponse(selectedFeedback.id, response);
                      setShowResponseForm(false);
                    }}
                    onCancel={() => setShowResponseForm(false)}
                  />
                )}
              </div>
            </CardContent>
          </Card>
        )}
      </div>
    </div>
  );
}

// Response Form Component
interface ResponseFormProps {
  feedbackId: string;
  onSubmit: (response: any) => void;
  onCancel: () => void;
}

function ResponseForm({ feedbackId, onSubmit, onCancel }: ResponseFormProps) {
  const [formData, setFormData] = useState({
    type: 'acknowledgment' as const,
    content: '',
    internal: false
  });

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault();
    onSubmit(formData);
  };

  return (
    <div className="border rounded-lg p-4 bg-gray-50">
      <h4 className="font-medium mb-3">Add Response</h4>
      <form onSubmit={handleSubmit} className="space-y-3">
        <div>
          <label className="block text-sm font-medium mb-1">Response Type</label>
          <Select
            value={formData.type}
            onChange={(value) => setFormData(prev => ({ ...prev, type: value as any }))}
          >
            <option value="acknowledgment">Acknowledgment</option>
            <option value="clarification">Clarification</option>
            <option value="update">Status Update</option>
            <option value="resolution">Resolution</option>
            <option value="information">Information</option>
          </Select>
        </div>

        <div>
          <label className="block text-sm font-medium mb-1">Response</label>
          <textarea
            value={formData.content}
            onChange={(e) => setFormData(prev => ({ ...prev, content: e.target.value }))}
            className="w-full p-2 border rounded"
            rows={4}
            placeholder="Enter your response..."
            required
          />
        </div>

        <div className="flex items-center gap-2">
          <input
            type="checkbox"
            id="internal"
            checked={formData.internal}
            onChange={(e) => setFormData(prev => ({ ...prev, internal: e.target.checked }))}
          />
          <label htmlFor="internal" className="text-sm">
            Internal response (not visible to user)
          </label>
        </div>

        <div className="flex gap-2">
          <Button type="submit">Submit Response</Button>
          <Button type="button" variant="outline" onClick={onCancel}>
            Cancel
          </Button>
        </div>
      </form>
    </div>
  );
}
```

## Testing Strategy

### Feedback Service Testing

```typescript
// packages/feedback/src/services/__tests__/feedback.service.test.ts
import { FeedbackService } from '../feedback.service';
import { FeedbackStore } from '../feedback.store';
import { AnalysisEngine } from '../analysis.engine';
import { NotificationService } from '../notification.service';
import { WorkflowIntegration } from '../workflow.integration';

describe('FeedbackService', () => {
  let feedbackService: FeedbackService;
  let mockFeedbackStore: jest.Mocked<FeedbackStore>;
  let mockAnalysisEngine: jest.Mocked<AnalysisEngine>;
  let mockNotificationService: jest.Mocked<NotificationService>;
  let mockWorkflowIntegration: jest.Mocked<WorkflowIntegration>;

  beforeEach(() => {
    mockFeedbackStore = {
      createFeedback: jest.fn(),
      getFeedback: jest.fn(),
      updateFeedback: jest.fn(),
      findSimilarFeedback: jest.fn(),
      createResponse: jest.fn(),
      getAnalytics: jest.fn(),
      searchFeedback: jest.fn(),
    } as any;

    mockAnalysisEngine = {
      analyzeSentiment: jest.fn(),
      extractThemes: jest.fn(),
      categorizeFeedback: jest.fn(),
      assessSeverity: jest.fn(),
      calculatePriorityScore: jest.fn(),
      suggestAssignment: jest.fn(),
      enhanceAnalytics: jest.fn(),
    } as any;

    mockNotificationService = {
      sendAssignmentNotification: jest.fn(),
      sendProductNotification: jest.fn(),
      sendEngineeringNotification: jest.fn(),
      sendAcknowledgment: jest.fn(),
      sendStatusChangeNotification: jest.fn(),
      sendResponseNotification: jest.fn(),
      sendSatisfactionSurvey: jest.fn(),
    } as any;

    mockWorkflowIntegration = {
      createIssue: jest.fn(),
      createStory: jest.fn(),
      updateIssueStatus: jest.fn(),
      updateStoryStatus: jest.fn(),
    } as any;

    feedbackService = new FeedbackService(
      mockFeedbackStore,
      mockAnalysisEngine,
      mockNotificationService,
      mockWorkflowIntegration,
      {} as any
    );
  });

  describe('submitFeedback', () => {
    it('creates and processes feedback successfully', async () => {
      const submission = {
        type: 'bug_report' as const,
        title: 'Login button not working',
        description: 'The login button is not responding when clicked',
        source: 'in_app' as const,
        userId: 'user-123',
      };

      const expectedFeedback = expect.objectContaining({
        type: 'bug_report',
        title: 'Login button not working',
        description: 'The login button is not responding when clicked',
        userId: 'user-123',
        status: 'new',
      });

      mockAnalysisEngine.analyzeSentiment.mockResolvedValue({
        score: -0.3,
        label: 'negative',
        confidence: 0.8,
      });

      mockAnalysisEngine.extractThemes.mockResolvedValue(['login', 'button', 'bug']);
      mockAnalysisEngine.calculatePriorityScore.mockResolvedValue(0.7);
      mockAnalysisEngine.suggestAssignment.mockResolvedValue({
        userId: 'dev-456',
        teamId: 'frontend',
      });

      mockWorkflowIntegration.createIssue.mockResolvedValue({ id: 'issue-789' });
      mockFeedbackStore.findSimilarFeedback.mockResolvedValue([]);

      const result = await feedbackService.submitFeedback(submission);

      expect(mockFeedbackStore.createFeedback).toHaveBeenCalledWith(expectedFeedback);
      expect(mockAnalysisEngine.analyzeSentiment).toHaveBeenCalled();
      expect(mockWorkflowIntegration.createIssue).toHaveBeenCalled();
      expect(mockNotificationService.sendAcknowledgment).toHaveBeenCalled();
      expect(result).toMatchObject(expectedFeedback);
    });

    it('detects and marks duplicates', async () => {
      const submission = {
        type: 'bug_report' as const,
        title: 'Login button not working',
        description: 'The login button is not responding when clicked',
        source: 'in_app' as const,
      };

      const duplicateFeedback = {
        feedbackId: 'feedback-123',
        similarityScore: 0.9,
      };

      mockAnalysisEngine.analyzeSentiment.mockResolvedValue({
        score: -0.3,
        label: 'negative',
        confidence: 0.8,
      });

      mockFeedbackStore.findSimilarFeedback.mockResolvedValue([duplicateFeedback]);
      mockFeedbackStore.incrementDuplicateCount.mockResolvedValue();

      const result = await feedbackService.submitFeedback(submission);

      expect(result.status).toBe('duplicate');
      expect(result.metadata.duplicateOf).toBe('feedback-123');
      expect(result.metadata.similarityScore).toBe(0.9);
      expect(mockFeedbackStore.incrementDuplicateCount).toHaveBeenCalledWith('feedback-123');
    });
  });

  describe('updateFeedback', () => {
    it('updates feedback and processes status changes', async () => {
      const existingFeedback = {
        id: 'feedback-123',
        title: 'Original title',
        status: 'new' as const,
      };

      const updates = {
        title: 'Updated title',
        status: 'acknowledged' as const,
      };

      mockFeedbackStore.getFeedback.mockResolvedValue(existingFeedback);
      mockFeedbackStore.updateFeedback.mockResolvedValue();

      const result = await feedbackService.updateFeedback('feedback-123', updates, 'user-456');

      expect(mockFeedbackStore.updateFeedback).toHaveBeenCalledWith(
        expect.objectContaining({
          id: 'feedback-123',
          title: 'Updated title',
          status: 'acknowledged',
          updatedAt: expect.any(String),
        })
      );

      expect(mockNotificationService.sendStatusChangeNotification).toHaveBeenCalledWith(
        expect.objectContaining({ status: 'acknowledged' }),
        'new',
        'user-456'
      );
    });
  });

  describe('addResponse', () => {
    it('adds response and sends notification', async () => {
      const response = {
        type: 'acknowledgment' as const,
        content: 'We are looking into this issue',
        author: 'support-agent',
        authorRole: 'Support',
        internal: false,
      };

      const feedback = {
        id: 'feedback-123',
        userEmail: 'user@example.com',
        anonymous: false,
      };

      mockFeedbackStore.getFeedback.mockResolvedValue(feedback);
      mockFeedbackStore.createResponse.mockResolvedValue();
      mockFeedbackStore.updateFeedback.mockResolvedValue();

      const result = await feedbackService.addResponse('feedback-123', response);

      expect(mockFeedbackStore.createResponse).toHaveBeenCalledWith(
        expect.objectContaining({
          feedbackId: 'feedback-123',
          type: 'acknowledgment',
          content: 'We are looking into this issue',
          author: 'support-agent',
        })
      );

      expect(mockNotificationService.sendResponseNotification).toHaveBeenCalled();
    });
  });
});
```

### UI Component Testing

```typescript
// packages/feedback/src/components/__tests__/FeedbackWidget.test.tsx
import React from 'react';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { FeedbackWidget } from '../FeedbackWidget';

describe('FeedbackWidget', () => {
  const mockOnSubmit = jest.fn();

  beforeEach(() => {
    jest.clearAllMocks();
  });

  it('renders feedback form correctly', () => {
    render(<FeedbackWidget onSubmit={mockOnSubmit} />);

    expect(screen.getByText('Share Your Feedback')).toBeInTheDocument();
    expect(screen.getByLabelText('What type of feedback would you like to share?')).toBeInTheDocument();
    expect(screen.getByLabelText('Title *')).toBeInTheDocument();
    expect(screen.getByLabelText('Description *')).toBeInTheDocument();
    expect(screen.getByText('Submit Feedback')).toBeInTheDocument();
  });

  it('validates required fields', async () => {
    render(<FeedbackWidget onSubmit={mockOnSubmit} />);

    const submitButton = screen.getByText('Submit Feedback');
    fireEvent.click(submitButton);

    await waitFor(() => {
      expect(screen.getByText('Title is required')).toBeInTheDocument();
      expect(screen.getByText('Description is required')).toBeInTheDocument();
    });

    expect(mockOnSubmit).not.toHaveBeenCalled();
  });

  it('submits feedback successfully', async () => {
    render(<FeedbackWidget onSubmit={mockOnSubmit} />);

    // Fill form
    fireEvent.change(screen.getByLabelText('Title *'), {
      target: { value: 'Test feedback title' }
    });
    fireEvent.change(screen.getByLabelText('Description *'), {
      target: { value: 'This is a detailed description of the feedback that is at least 20 characters long.' }
    });

    // Submit form
    const submitButton = screen.getByText('Submit Feedback');
    fireEvent.click(submitButton);

    await waitFor(() => {
      expect(mockOnSubmit).toHaveBeenCalledWith(
        expect.objectContaining({
          type: 'general_feedback',
          title: 'Test feedback title',
          description: 'This is a detailed description of the feedback that is at least 20 characters long.',
          source: 'in_app'
        })
      );
    });
  });

  it('shows success message after submission', async () => {
    mockOnSubmit.mockResolvedValue(undefined);

    render(<FeedbackWidget onSubmit={mockOnSubmit} />);

    // Fill and submit form
    fireEvent.change(screen.getByLabelText('Title *'), {
      target: { value: 'Test feedback' }
    });
    fireEvent.change(screen.getByLabelText('Description *'), {
      target: { value: 'This is a detailed description of the feedback.' }
    });

    fireEvent.click(screen.getByText('Submit Feedback'));

    await waitFor(() => {
      expect(screen.getByText('Thank You!')).toBeInTheDocument();
      expect(screen.getByText('Your feedback has been submitted successfully. We appreciate your input!')).toBeInTheDocument();
    });
  });

  it('handles different feedback types', () => {
    render(<FeedbackWidget onSubmit={mockOnSubmit} initialType="bug_report" />);

    const typeSelect = screen.getByLabelText('What type of feedback would you like to share?');
    expect(typeSelect).toHaveValue('bug_report');

    // Check if severity field appears for bug reports
    expect(screen.getByText('How severe is this issue?')).toBeInTheDocument();
  });
});
```

## Performance Requirements

### Feedback Processing

- **Submission Response Time**: 95th percentile under 2 seconds
- **Analysis Processing**: Sentiment and theme analysis under 5 seconds
- **Duplicate Detection**: Similarity search completed in under 1 second
- **Search Performance**: Feedback search results in under 1 second
- **Dashboard Loading**: Feedback dashboard loads in under 3 seconds

### System Performance

- **Memory Usage**: Feedback service memory usage under 512MB
- **Database Performance**: All feedback queries complete in under 500ms
- **Analysis Engine**: NLP processing throughput of 100 feedback items per minute
- **Notification Delivery**: 95th percentile notification delivery under 30 seconds
- **Concurrent Users**: Support 100+ concurrent users in feedback dashboard

### Scalability

- **Feedback Volume**: Handle 10,000+ feedback submissions per day
- **Storage Capacity**: Support millions of feedback records with efficient indexing
- **Analysis Throughput**: Process 1000+ feedback items per hour for analysis
- **Search Performance**: Maintain sub-second search with 1M+ records
- **Notification Throughput**: Send 10,000+ notifications per hour

## Security Considerations

### Data Privacy

- **Anonymous Feedback**: Proper handling of anonymous feedback submissions
- **PII Protection**: Automatic redaction of personal information from feedback
- **Data Retention**: Configurable retention policies for feedback data
- **Consent Management**: Clear consent for data processing and communications

### Access Control

- **Role-based Access**: Different access levels for feedback management
- **Feedback Privacy**: Users can only view their own feedback unless authorized
- **Response Permissions**: Controlled access to response functionality
- **Audit Logging**: All feedback actions logged for compliance

## Success Metrics

### User Engagement

- **Feedback Submission Rate**: 5%+ of active users submit feedback monthly
- **Response Rate**: 80%+ of feedback receives response within 48 hours
- **User Satisfaction**: 4.0+ average satisfaction score (1-5 scale)
- **Net Promoter Score**: 40+ NPS for feedback system

### Operational Excellence

- **Response Time**: 90% of feedback acknowledged within 24 hours
- **Resolution Time**: 70% of feedback resolved within 7 days
- **Analysis Accuracy**: 85%+ accuracy in automated sentiment and theme analysis
- **Duplicate Detection**: 90%+ accuracy in duplicate feedback detection

### Business Impact

- **Product Improvement**: 60%+ of feature requests implemented or planned
- **Bug Resolution**: 80%+ of reported bugs resolved in next release
- **User Retention**: 10%+ improvement in user retention for engaged feedback users
- **Product Quality**: 30%+ reduction in user-reported issues over time

## Rollout Plan

### Phase 1: Core Feedback Collection (Week 1-2)

1. **Basic Feedback System**
   - Feedback submission forms
   - Basic feedback storage and retrieval
   - Simple feedback dashboard
   - Email notifications for new feedback

2. **Essential Features**
   - Feedback categorization and tagging
   - Basic sentiment analysis
   - Response management
   - User acknowledgment emails

### Phase 2: Advanced Features (Week 3-4)

1. **Enhanced Analysis**
   - Advanced sentiment and theme analysis
   - Duplicate detection
   - Auto-prioritization
   - Assignment suggestions

2. **Workflow Integration**
   - Automatic issue creation
   - Story mapping for feature requests
   - Status synchronization
   - Release notifications

### Phase 3: Analytics and Optimization (Week 5-6)

1. **Analytics Dashboard**
   - Feedback analytics and insights
   - Trend analysis and reporting
   - NPS calculation and tracking
   - Theme extraction and visualization

2. **Advanced Features**
   - Survey system implementation
   - Multi-channel feedback collection
   - Advanced filtering and search
   - Feedback export capabilities

### Phase 4: Enhancement and Optimization (Week 7-8)

1. **Performance Optimization**
   - Database query optimization
   - Analysis engine performance tuning
   - Caching implementation
   - Search performance optimization

2. **User Experience Enhancement**
   - UI/UX improvements
   - Mobile responsiveness
   - Accessibility enhancements
   - User feedback integration

## Dependencies

### Internal Dependencies

- **Epic 5.3**: Dashboard Framework (provides UI components)
- **Epic 5.6**: Alert System (provides notification capabilities)
- **Epic 2**: Development Workflow (provides issue/story integration)
- **Epic 4**: Event Sourcing (provides audit trail)

### External Dependencies

- **React 18+**: Frontend framework for feedback UI
- **Node.js**: Backend service for feedback processing
- **PostgreSQL**: Feedback storage and management
- **NLP Libraries**: Sentiment analysis and theme extraction
- **Email Service**: Notification delivery (SendGrid, AWS SES)

### Infrastructure Dependencies

- **Kubernetes**: Scalable deployment platform
- **Load Balancer**: Traffic distribution
- **Monitoring**: System health and performance monitoring
- **Storage**: Sufficient storage for feedback data and analysis

---

**Story Status**: Ready for Development  
**Implementation Priority**: Optional (Post-MVP Enhancement)  
**Target Completion**: Sprint 6 (Post-MVP)  
**Dependencies**: Epic 5.3, Epic 5.6, Epic 2, Epic 4
