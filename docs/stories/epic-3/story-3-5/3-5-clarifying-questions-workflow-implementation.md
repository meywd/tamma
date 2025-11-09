# Story 3.5: Clarifying Questions Workflow & Code Quality Gates Implementation

**Epic**: Epic 3 - Quality Gates & Intelligence Layer  
**Status**: Ready for Development  
**Priority**: High

## Acceptance Criteria

### Clarifying Questions Workflow

- [ ] Clarifying questions workflow triggers when ambiguity is detected in requirements
- [ ] System generates intelligent, context-aware questions to resolve ambiguity
- [ ] Questions are prioritized by impact and urgency
- [ ] Questions are routed to appropriate stakeholders based on domain expertise
- [ ] System tracks question status and follows up on unanswered questions
- [ ] Question-answer pairs are stored for future learning and pattern recognition
- [ ] System learns from question patterns to improve future question generation

### Code Quality Gates

- [ ] Quality Metrics: Track code complexity, maintainability, duplication, and technical debt
- [ ] Automated Analysis: Integrate multiple code analysis tools (ESLint, SonarQube, etc.)
- [ ] Quality Thresholds: Enforce minimum quality standards with configurable thresholds
- [ ] Trend Analysis: Track quality metrics over time and identify trends
- [ ] Developer Feedback: Provide actionable feedback and improvement suggestions
- [ ] Integration: Seamlessly integrate with CI/CD pipeline and quality gates system

## Technical Context

### ⚠️ Development Process Reminder

**Before implementing this story, ensure you have:**

1. ✅ Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
2. ✅ Searched `.dev/` directory for related spikes, bugs, findings, and decisions
3. ✅ Reviewed relevant documentation in `docs/` directory
4. ✅ Checked existing code patterns for similar functionality
5. ✅ Planned TDD approach (Red-Green-Refactor cycle)

### Clarifying Questions Workflow Overview

The Clarifying Questions Workflow is responsible for identifying ambiguous requirements, generating intelligent questions to resolve uncertainty, and managing the question-answer lifecycle. It ensures that all requirements are clear, complete, and unambiguous before development begins.

### Core Responsibilities

1. **Ambiguity Detection and Analysis**
   - Identify ambiguous language, missing information, and contradictions
   - Analyze requirement complexity and domain-specific terminology
   - Detect implicit assumptions and unstated constraints
   - Assess the impact of ambiguity on development effort and risk

2. **Intelligent Question Generation**
   - Generate context-aware questions based on ambiguity analysis
   - Prioritize questions by impact, urgency, and dependency
   - Tailor question style and format to stakeholder preferences
   - Group related questions to minimize stakeholder overhead

3. **Stakeholder Management and Routing**
   - Identify appropriate stakeholders based on domain expertise
   - Route questions through preferred communication channels
   - Manage stakeholder availability and response expectations
   - Handle stakeholder conflicts and escalation paths

4. **Question Lifecycle Management**
   - Track question status from creation to resolution
   - Send reminders and follow-ups for unanswered questions
   - Consolidate related answers and identify contradictions
   - Store question-answer pairs for knowledge base and learning

### Implementation Details

#### Clarifying Questions Configuration Schema

```typescript
interface ClarifyingQuestionsConfig {
  // Ambiguity detection rules
  ambiguityDetection: {
    enabled: boolean;
    patterns: AmbiguityPattern[];
    thresholds: {
      minAmbiguityScore: number;
      maxQuestionsPerRequirement: number;
      questionComplexityLimit: number;
    };
    domains: DomainConfig[];
  };

  // Question generation settings
  questionGeneration: {
    templates: QuestionTemplate[];
    styles: QuestionStyle[];
    prioritization: PrioritizationStrategy[];
    grouping: GroupingStrategy;
  };

  // Stakeholder management
  stakeholders: {
    routing: RoutingStrategy[];
    availability: AvailabilityConfig;
    preferences: StakeholderPreferences;
    escalation: EscalationConfig;
  };

  // Question lifecycle
  lifecycle: {
    reminders: ReminderConfig[];
    timeouts: TimeoutConfig[];
    consolidation: ConsolidationConfig;
    storage: StorageConfig;
  };

  // Learning and improvement
  learning: {
    enabled: boolean;
    patternRecognition: boolean;
    feedbackCollection: boolean;
    modelRetraining: RetrainingConfig;
  };
}

interface AmbiguityPattern {
  id: string;
  name: string;
  description: string;
  pattern: RegExp;
  type: 'vague' | 'missing' | 'contradiction' | 'assumption' | 'undefined';
  severity: 'low' | 'medium' | 'high' | 'critical';
  questionTemplates: string[];
  examples: string[];
}

interface QuestionTemplate {
  id: string;
  name: string;
  category: string;
  template: string;
  variables: TemplateVariable[];
  context: QuestionContext;
  style: QuestionStyle;
  priority: number;
}

interface StakeholderProfile {
  id: string;
  name: string;
  role: string;
  domain: string[];
  expertise: ExpertiseArea[];
  preferences: {
    communicationChannels: string[];
    questionStyle: string;
    responseTime: number;
    workingHours: TimeRange;
    timezone: string;
  };
  availability: AvailabilitySchedule;
  contactInfo: ContactInfo;
}
```

#### Clarifying Questions Engine

```typescript
class ClarifyingQuestionsEngine implements IClarifyingQuestionsEngine {
  private readonly config: ClarifyingQuestionsConfig;
  private readonly ambiguityDetector: IAmbiguityDetector;
  private readonly questionGenerator: IQuestionGenerator;
  private readonly stakeholderManager: IStakeholderManager;
  private readonly questionTracker: IQuestionTracker;
  private readonly knowledgeBase: IKnowledgeBase;
  private readonly learningEngine: ILearningEngine;

  async processRequirement(requirement: Requirement): Promise<ClarifyingQuestionsResult> {
    const sessionId = this.generateSessionId();
    const startTime = Date.now();

    try {
      // Emit processing start event
      await this.eventStore.append({
        type: 'CLARIFYING_QUESTIONS.STARTED',
        tags: {
          sessionId,
          requirementId: requirement.id,
          issueId: requirement.issueId,
        },
        data: {
          requirement: requirement.title,
          complexity: requirement.complexity,
        },
      });

      // Detect ambiguities
      const ambiguityAnalysis = await this.detectAmbiguities(requirement);

      if (ambiguityAnalysis.ambiguities.length === 0) {
        // No ambiguities detected
        return {
          sessionId,
          requirementId: requirement.id,
          questions: [],
          status: 'no_ambiguity',
          confidence: 0.95,
          duration: Date.now() - startTime,
        };
      }

      // Generate clarifying questions
      const questions = await this.generateQuestions(requirement, ambiguityAnalysis);

      // Prioritize and group questions
      const prioritizedQuestions = await this.prioritizeQuestions(questions, requirement);
      const groupedQuestions = await this.groupQuestions(prioritizedQuestions);

      // Route questions to stakeholders
      const routingPlan = await this.createRoutingPlan(groupedQuestions, requirement);

      // Execute routing plan
      const routingResults = await this.executeRoutingPlan(routingPlan, sessionId);

      // Start tracking questions
      await this.questionTracker.startTracking(sessionId, groupedQuestions, routingResults);

      // Emit processing completion event
      await this.eventStore.append({
        type: 'CLARIFYING_QUESTIONS.GENERATED',
        tags: {
          sessionId,
          requirementId: requirement.id,
          issueId: requirement.issueId,
        },
        data: {
          ambiguitiesDetected: ambiguityAnalysis.ambiguities.length,
          questionsGenerated: questions.length,
          questionsRouted: routingResults.length,
          confidence: ambiguityAnalysis.overallConfidence,
        },
      });

      return {
        sessionId,
        requirementId: requirement.id,
        questions: groupedQuestions,
        routing: routingResults,
        status: 'questions_generated',
        confidence: ambiguityAnalysis.overallConfidence,
        ambiguityAnalysis,
        duration: Date.now() - startTime,
      };
    } catch (error) {
      // Emit processing error event
      await this.eventStore.append({
        type: 'CLARIFYING_QUESTIONS.ERROR',
        tags: {
          sessionId,
          requirementId: requirement.id,
          issueId: requirement.issueId,
        },
        data: {
          error: error.message,
          duration: Date.now() - startTime,
        },
      });

      throw error;
    }
  }

  private async detectAmbiguities(requirement: Requirement): Promise<AmbiguityAnalysis> {
    const ambiguities: Ambiguity[] = [];
    const text = `${requirement.title} ${requirement.description}`;

    // Apply ambiguity detection patterns
    for (const pattern of this.config.ambiguityDetection.patterns) {
      const matches = text.match(pattern.pattern);

      if (matches) {
        for (const match of matches) {
          const ambiguity: Ambiguity = {
            id: this.generateAmbiguityId(),
            type: pattern.type,
            severity: pattern.severity,
            patternId: pattern.id,
            text: match,
            context: this.extractContext(text, match),
            location: this.findLocation(requirement, match),
            confidence: this.calculateAmbiguityConfidence(match, pattern),
            impact: this.assessAmbiguityImpact(match, pattern, requirement),
            suggestedQuestions: pattern.questionTemplates,
          };

          ambiguities.push(ambiguity);
        }
      }
    }

    // Detect missing information
    const missingInfo = await this.detectMissingInformation(requirement);
    ambiguities.push(...missingInfo);

    // Detect contradictions
    const contradictions = await this.detectContradictions(requirement);
    ambiguities.push(...contradictions);

    // Detect implicit assumptions
    const assumptions = await this.detectImplicitAssumptions(requirement);
    ambiguities.push(...assumptions);

    // Remove duplicates and calculate overall confidence
    const uniqueAmbiguities = this.removeDuplicateAmbiguities(ambiguities);
    const overallConfidence = this.calculateOverallConfidence(uniqueAmbiguities);

    return {
      ambiguities: uniqueAmbiguities,
      overallConfidence,
      totalAmbiguityScore: this.calculateTotalAmbiguityScore(uniqueAmbiguities),
      distribution: this.calculateAmbiguityDistribution(uniqueAmbiguities),
    };
  }

  private async generateQuestions(
    requirement: Requirement,
    ambiguityAnalysis: AmbiguityAnalysis
  ): Promise<GeneratedQuestion[]> {
    const questions: GeneratedQuestion[] = [];

    for (const ambiguity of ambiguityAnalysis.ambiguities) {
      // Select appropriate question templates
      const templates = await this.selectQuestionTemplates(ambiguity, requirement);

      for (const template of templates) {
        // Generate question from template
        const question = await this.generateQuestionFromTemplate(template, ambiguity, requirement);

        // Enhance question with context
        const enhancedQuestion = await this.enhanceQuestionWithContext(
          question,
          ambiguity,
          requirement
        );

        questions.push(enhancedQuestion);
      }
    }

    // Generate domain-specific questions
    const domainQuestions = await this.generateDomainSpecificQuestions(
      requirement,
      ambiguityAnalysis
    );
    questions.push(...domainQuestions);

    // Generate clarification questions for complex requirements
    if (requirement.complexity >= 0.7) {
      const clarificationQuestions = await this.generateClarificationQuestions(requirement);
      questions.push(...clarificationQuestions);
    }

    return questions;
  }

  private async generateQuestionFromTemplate(
    template: QuestionTemplate,
    ambiguity: Ambiguity,
    requirement: Requirement
  ): Promise<GeneratedQuestion> {
    // Extract variables from template
    const variables = await this.extractTemplateVariables(template, ambiguity, requirement);

    // Fill template with variables
    let questionText = template.template;
    for (const [key, value] of Object.entries(variables)) {
      questionText = questionText.replace(new RegExp(`\\{${key}\\}`, 'g'), value);
    }

    // Generate question metadata
    const question: GeneratedQuestion = {
      id: this.generateQuestionId(),
      text: questionText,
      type: template.category,
      style: template.style,
      priority: template.priority,
      ambiguityId: ambiguity.id,
      requirementId: requirement.id,
      context: {
        ambiguity: ambiguity.text,
        requirement: requirement.title,
        domain: requirement.domain,
        complexity: requirement.complexity,
      },
      metadata: {
        templateId: template.id,
        variables,
        generatedAt: new Date().toISOString(),
        confidence: this.calculateQuestionConfidence(template, ambiguity),
      },
      status: 'generated',
    };

    return question;
  }

  private async prioritizeQuestions(
    questions: GeneratedQuestion[],
    requirement: Requirement
  ): Promise<PrioritizedQuestion[]> {
    const prioritizedQuestions: PrioritizedQuestion[] = [];

    for (const question of questions) {
      // Calculate priority score
      const priorityScore = await this.calculatePriorityScore(question, requirement);

      // Determine urgency
      const urgency = await this.determineUrgency(question, requirement);

      // Assess impact
      const impact = await this.assessQuestionImpact(question, requirement);

      const prioritizedQuestion: PrioritizedQuestion = {
        ...question,
        priorityScore,
        urgency,
        impact,
        dependencies: await this.identifyQuestionDependencies(question, questions),
        estimatedResponseTime: await this.estimateResponseTime(question),
      };

      prioritizedQuestions.push(prioritizedQuestion);
    }

    // Sort by priority score (highest first)
    return prioritizedQuestions.sort((a, b) => b.priorityScore - a.priorityScore);
  }

  private async groupQuestions(questions: PrioritizedQuestion[]): Promise<QuestionGroup[]> {
    const groups: QuestionGroup[] = [];
    const ungroupedQuestions = [...questions];

    // Apply grouping strategies
    for (const strategy of this.config.questionGeneration.grouping.strategies) {
      const groupResult = await this.applyGroupingStrategy(strategy, ungroupedQuestions);

      for (const group of groupResult.groups) {
        const questionGroup: QuestionGroup = {
          id: this.generateGroupId(),
          name: group.name,
          description: group.description,
          questions: group.questions,
          priority: Math.max(...group.questions.map((q) => q.priorityScore)),
          estimatedTime: group.questions.reduce(
            (sum, q) => sum + (q.estimatedResponseTime || 0),
            0
          ),
          metadata: {
            groupingStrategy: strategy.name,
            groupingReason: group.reason,
            createdAt: new Date().toISOString(),
          },
        };

        groups.push(questionGroup);

        // Remove grouped questions from ungrouped list
        for (const question of group.questions) {
          const index = ungroupedQuestions.findIndex((q) => q.id === question.id);
          if (index !== -1) {
            ungroupedQuestions.splice(index, 1);
          }
        }
      }
    }

    // Create group for remaining ungrouped questions
    if (ungroupedQuestions.length > 0) {
      const remainingGroup: QuestionGroup = {
        id: this.generateGroupId(),
        name: 'Additional Questions',
        description: 'Questions that did not fit into specific groups',
        questions: ungroupedQuestions,
        priority: Math.max(...ungroupedQuestions.map((q) => q.priorityScore)),
        estimatedTime: ungroupedQuestions.reduce(
          (sum, q) => sum + (q.estimatedResponseTime || 0),
          0
        ),
        metadata: {
          groupingStrategy: 'remaining',
          groupingReason: 'ungrouped',
          createdAt: new Date().toISOString(),
        },
      };

      groups.push(remainingGroup);
    }

    return groups;
  }

  private async createRoutingPlan(
    questionGroups: QuestionGroup[],
    requirement: Requirement
  ): Promise<RoutingPlan> {
    const routingPlan: RoutingPlan = {
      id: this.generateRoutingPlanId(),
      requirementId: requirement.id,
      routes: [],
    };

    for (const group of questionGroups) {
      // Identify appropriate stakeholders
      const stakeholders = await this.identifyStakeholders(group, requirement);

      // Create routes for each stakeholder
      for (const stakeholder of stakeholders) {
        const route: RoutingRoute = {
          id: this.generateRouteId(),
          groupId: group.id,
          stakeholderId: stakeholder.id,
          stakeholder,
          channel: this.selectOptimalChannel(stakeholder, group),
          priority: group.priority,
          scheduledTime: this.calculateScheduledTime(stakeholder, group),
          estimatedResponseTime: group.estimatedTime,
          followUpPlan: this.createFollowUpPlan(stakeholder, group),
        };

        routingPlan.routes.push(route);
      }
    }

    return routingPlan;
  }

  private async executeRoutingPlan(
    routingPlan: RoutingPlan,
    sessionId: string
  ): Promise<RoutingResult[]> {
    const results: RoutingResult[] = [];

    for (const route of routingPlan.routes) {
      try {
        // Check stakeholder availability
        const isAvailable = await this.checkStakeholderAvailability(route.stakeholder);

        if (!isAvailable) {
          // Schedule for later or find alternative
          const alternativeRoute = await this.findAlternativeStakeholder(route);

          if (alternativeRoute) {
            route.stakeholder = alternativeRoute.stakeholder;
            route.channel = this.selectOptimalChannel(
              alternativeRoute.stakeholder,
              routingPlan.routes.find((r) => r.groupId === route.groupId)!
            );
          } else {
            // Schedule for when stakeholder is available
            route.scheduledTime = this.getNextAvailableTime(route.stakeholder);
          }
        }

        // Send questions through selected channel
        const sendResult = await this.sendQuestions(route, sessionId);

        const routingResult: RoutingResult = {
          routeId: route.id,
          groupId: route.groupId,
          stakeholderId: route.stakeholderId,
          channel: route.channel,
          status: sendResult.success ? 'sent' : 'failed',
          sentAt: new Date().toISOString(),
          scheduledTime: route.scheduledTime,
          questionsSent: route.questions?.length || 0,
          messageId: sendResult.messageId,
          error: sendResult.error,
        };

        results.push(routingResult);

        // Emit routing event
        await this.eventStore.append({
          type: sendResult.success
            ? 'CLARIFYING_QUESTIONS.ROUTED'
            : 'CLARIFYING_QUESTIONS.ROUTING_FAILED',
          tags: {
            sessionId,
            routeId: route.id,
            stakeholderId: route.stakeholderId,
            channel: route.channel,
          },
          data: {
            routingResult,
            questionsCount: routingResult.questionsSent,
          },
        });
      } catch (error) {
        const routingResult: RoutingResult = {
          routeId: route.id,
          groupId: route.groupId,
          stakeholderId: route.stakeholderId,
          channel: route.channel,
          status: 'error',
          error: error.message,
          sentAt: new Date().toISOString(),
        };

        results.push(routingResult);
      }
    }

    return results;
  }
}
```

#### Question Tracker

```typescript
class QuestionTracker implements IQuestionTracker {
  private readonly activeSessions: Map<string, QuestionSession>;
  private readonly reminderScheduler: IReminderScheduler;
  private readonly consolidationEngine: IConsolidationEngine;

  async startTracking(
    sessionId: string,
    questionGroups: QuestionGroup[],
    routingResults: RoutingResult[]
  ): Promise<void> {
    const session: QuestionSession = {
      id: sessionId,
      status: 'active',
      questionGroups,
      routingResults,
      responses: [],
      createdAt: new Date().toISOString(),
      lastActivity: new Date().toISOString(),
      deadlines: this.calculateDeadlines(questionGroups, routingResults),
    };

    this.activeSessions.set(sessionId, session);

    // Schedule reminders
    await this.scheduleReminders(session);

    // Schedule consolidation
    await this.scheduleConsolidation(session);

    // Emit tracking start event
    await this.eventStore.append({
      type: 'CLARIFYING_QUESTIONS.TRACKING_STARTED',
      tags: {
        sessionId,
      },
      data: {
        questionGroups: questionGroups.length,
        routes: routingResults.length,
        deadlines: session.deadlines.length,
      },
    });
  }

  async recordResponse(
    sessionId: string,
    response: QuestionResponse
  ): Promise<ResponseProcessingResult> {
    const session = this.activeSessions.get(sessionId);

    if (!session) {
      throw new Error(`Session not found: ${sessionId}`);
    }

    // Process response
    const processedResponse = await this.processResponse(response, session);

    // Add to session
    session.responses.push(processedResponse);
    session.lastActivity = new Date().toISOString();

    // Update question status
    await this.updateQuestionStatus(session, processedResponse);

    // Check if all questions are answered
    const completionStatus = await this.checkCompletionStatus(session);

    if (completionStatus.completed) {
      await this.completeSession(session, completionStatus);
    } else {
      // Update reminders and consolidation
      await this.updateReminders(session);
      await this.updateConsolidation(session);
    }

    // Emit response event
    await this.eventStore.append({
      type: 'CLARIFYING_QUESTIONS.RESPONSE_RECEIVED',
      tags: {
        sessionId,
        questionId: processedResponse.questionId,
        stakeholderId: processedResponse.stakeholderId,
      },
      data: {
        response: processedResponse,
        sessionProgress: completionStatus,
      },
    });

    return {
      success: true,
      sessionId,
      responseId: processedResponse.id,
      sessionStatus: session.status,
      progress: completionStatus,
    };
  }

  private async processResponse(
    response: QuestionResponse,
    session: QuestionSession
  ): Promise<ProcessedResponse> {
    // Validate response
    const validation = await this.validateResponse(response, session);

    if (!validation.valid) {
      throw new Error(`Invalid response: ${validation.errors.join(', ')}`);
    }

    // Analyze response content
    const analysis = await this.analyzeResponseContent(response);

    // Check for contradictions with existing responses
    const contradictions = await this.checkContradictions(response, session.responses);

    // Extract action items and decisions
    const actionItems = await this.extractActionItems(response);
    const decisions = await this.extractDecisions(response);

    const processedResponse: ProcessedResponse = {
      id: this.generateResponseId(),
      questionId: response.questionId,
      stakeholderId: response.stakeholderId,
      content: response.content,
      attachments: response.attachments || [],
      confidence: analysis.confidence,
      clarity: analysis.clarity,
      completeness: analysis.completeness,
      contradictions,
      actionItems,
      decisions,
      metadata: {
        receivedAt: new Date().toISOString(),
        processingTime: analysis.processingTime,
        language: analysis.language,
        sentiment: analysis.sentiment,
      },
    };

    return processedResponse;
  }

  private async checkCompletionStatus(session: QuestionSession): Promise<CompletionStatus> {
    const allQuestions = session.questionGroups.flatMap((g) => g.questions);
    const answeredQuestions = new Set(session.responses.map((r) => r.questionId));

    const unansweredQuestions = allQuestions.filter((q) => !answeredQuestions.has(q.id));
    const overdueQuestions = unansweredQuestions.filter((q) => this.isQuestionOverdue(q, session));

    const totalQuestions = allQuestions.length;
    const answeredCount = answeredQuestions.size;
    const overdueCount = overdueQuestions.length;

    const completionPercentage = totalQuestions > 0 ? (answeredCount / totalQuestions) * 100 : 0;

    // Determine if session is complete
    let completed = false;
    let completionReason: string | undefined;

    if (answeredCount === totalQuestions) {
      completed = true;
      completionReason = 'all_questions_answered';
    } else if (overdueCount > 0 && completionPercentage >= 80) {
      completed = true;
      completionReason = 'sufficient_responses_with_overdue';
    } else if (session.deadlines.some((d) => d.type === 'hard_deadline' && d.time < new Date())) {
      completed = true;
      completionReason = 'hard_deadline_reached';
    }

    return {
      completed,
      completionReason,
      totalQuestions,
      answeredCount,
      unansweredCount: totalQuestions - answeredCount,
      overdueCount,
      completionPercentage,
      estimatedCompletionTime: this.estimateCompletionTime(session),
    };
  }

  private async completeSession(
    session: QuestionSession,
    completionStatus: CompletionStatus
  ): Promise<void> {
    // Consolidate all responses
    const consolidation = await this.consolidationEngine.consolidate(session);

    // Generate final summary
    const summary = await this.generateSessionSummary(session, consolidation);

    // Store in knowledge base
    await this.storeInKnowledgeBase(session, consolidation, summary);

    // Update session status
    session.status = 'completed';
    session.completedAt = new Date().toISOString();
    session.completionStatus = completionStatus;
    session.consolidation = consolidation;
    session.summary = summary;

    // Send completion notifications
    await this.sendCompletionNotifications(session, summary);

    // Clean up reminders and schedules
    await this.cleanupSession(session);

    // Emit completion event
    await this.eventStore.append({
      type: 'CLARIFYING_QUESTIONS.SESSION_COMPLETED',
      tags: {
        sessionId,
        completionReason: completionStatus.completionReason!,
      },
      data: {
        session,
        consolidation,
        summary,
        duration: Date.now() - new Date(session.createdAt).getTime(),
      },
    });
  }
}
```

#### Learning Engine

```typescript
class LearningEngine implements ILearningEngine {
  private readonly patternRecognizer: IPatternRecognizer;
  private readonly feedbackCollector: IFeedbackCollector;
  private readonly modelTrainer: IModelTrainer;

  async learnFromSession(session: QuestionSession): Promise<LearningResult> {
    const learningData: LearningData = {
      sessionId: session.id,
      requirement: await this.extractRequirementContext(session),
      ambiguities: await this.extractAmbiguityPatterns(session),
      questions: await this.extractQuestionPatterns(session),
      responses: await this.extractResponsePatterns(session),
      outcomes: await this.extractOutcomes(session),
      feedback: await this.collectFeedback(session),
    };

    // Update pattern recognition models
    const patternUpdates = await this.updatePatterns(learningData);

    // Update question generation models
    const questionModelUpdates = await this.updateQuestionModels(learningData);

    // Update stakeholder routing models
    const routingModelUpdates = await this.updateRoutingModels(learningData);

    // Generate improvement suggestions
    const improvements = await this.generateImprovements(learningData);

    const learningResult: LearningResult = {
      sessionId: session.id,
      patternsUpdated: patternUpdates,
      modelsUpdated: [...questionModelUpdates, ...routingModelUpdates],
      improvements,
      confidence: this.calculateLearningConfidence(learningData),
      appliedAt: new Date().toISOString(),
    };

    // Emit learning event
    await this.eventStore.append({
      type: 'CLARIFYING_QUESTIONS.LEARNING_COMPLETED',
      tags: {
        sessionId: session.id,
      },
      data: {
        learningResult,
        patternsCount: patternUpdates.length,
        modelsUpdated: learningResult.modelsUpdated.length,
      },
    });

    return learningResult;
  }

  private async updatePatterns(learningData: LearningData): Promise<PatternUpdate[]> {
    const updates: PatternUpdate[] = [];

    // Analyze ambiguity patterns
    for (const ambiguity of learningData.ambiguities) {
      const patternUpdate = await this.patternRecognizer.updateAmbiguityPattern(ambiguity);

      if (patternUpdate.updated) {
        updates.push(patternUpdate);
      }
    }

    // Analyze question patterns
    for (const question of learningData.questions) {
      const patternUpdate = await this.patternRecognizer.updateQuestionPattern(question);

      if (patternUpdate.updated) {
        updates.push(patternUpdate);
      }
    }

    // Analyze response patterns
    for (const response of learningData.responses) {
      const patternUpdate = await this.patternRecognizer.updateResponsePattern(response);

      if (patternUpdate.updated) {
        updates.push(patternUpdate);
      }
    }

    return updates;
  }

  private async generateImprovements(learningData: LearningData): Promise<ImprovementSuggestion[]> {
    const improvements: ImprovementSuggestion[] = [];

    // Analyze question effectiveness
    const questionEffectiveness = await this.analyzeQuestionEffectiveness(learningData);

    for (const analysis of questionEffectiveness) {
      if (analysis.effectiveness < 0.7) {
        // Low effectiveness threshold
        const improvement: ImprovementSuggestion = {
          id: this.generateImprovementId(),
          type: 'question_template',
          priority: this.calculateImprovementPriority(analysis),
          description: `Improve question template for ${analysis.questionType}`,
          currentTemplate: analysis.template,
          suggestedChanges: analysis.suggestedChanges,
          expectedImpact: analysis.expectedImpact,
          evidence: analysis.evidence,
        };

        improvements.push(improvement);
      }
    }

    // Analyze routing effectiveness
    const routingEffectiveness = await this.analyzeRoutingEffectiveness(learningData);

    for (const analysis of routingEffectiveness) {
      if (analysis.effectiveness < 0.6) {
        // Low routing effectiveness threshold
        const improvement: ImprovementSuggestion = {
          id: this.generateImprovementId(),
          type: 'stakeholder_routing',
          priority: this.calculateImprovementPriority(analysis),
          description: `Improve routing for ${analysis.domain} domain questions`,
          currentRouting: analysis.currentRouting,
          suggestedChanges: analysis.suggestedChanges,
          expectedImpact: analysis.expectedImpact,
          evidence: analysis.evidence,
        };

        improvements.push(improvement);
      }
    }

    // Analyze timing and reminder effectiveness
    const timingEffectiveness = await this.analyzeTimingEffectiveness(learningData);

    for (const analysis of timingEffectiveness) {
      if (analysis.effectiveness < 0.8) {
        // High threshold for timing
        const improvement: ImprovementSuggestion = {
          id: this.generateImprovementId(),
          type: 'timing_reminder',
          priority: this.calculateImprovementPriority(analysis),
          description: `Optimize ${analysis.timingType} timing`,
          currentTiming: analysis.currentTiming,
          suggestedChanges: analysis.suggestedChanges,
          expectedImpact: analysis.expectedImpact,
          evidence: analysis.evidence,
        };

        improvements.push(improvement);
      }
    }

    return improvements.sort((a, b) => b.priority - a.priority);
  }
}
```

### Integration Points

#### Communication Channel Integration

```typescript
interface CommunicationChannelIntegration {
  // Email
  email: {
    smtp: {
      host: string;
      port: number;
      secure: boolean;
      auth: Record<string, string>;
    };
    templates: {
      questions: string;
      reminder: string;
      consolidation: string;
    };
  };

  // Slack
  slack: {
    botToken: string;
    channels: Record<string, string>;
    messageFormat: 'block_kit' | 'plain_text';
  };

  // Microsoft Teams
  teams: {
    webhookUrl: string;
    adaptiveCards: boolean;
    mentionSupport: boolean;
  };

  // Jira
  jira: {
    url: string;
    username: string;
    apiToken: string;
    project: string;
    issueType: string;
  };
}
```

#### Stakeholder Management Integration

```typescript
interface StakeholderManagementIntegration {
  // HR System
  hrSystem: {
    apiEndpoint: string;
    authentication: Record<string, string>;
    fields: {
      role: string;
      department: string;
      expertise: string;
      manager: string;
    };
  };

  // Active Directory/LDAP
  directory: {
    url: string;
    baseDN: string;
    bindDN: string;
    bindPassword: string;
    attributes: string[];
  };

  // Custom stakeholder management
  custom: {
    apiEndpoint: string;
    authentication: Record<string, string>;
    schema: Record<string, any>;
  };
}
```

### Error Handling and Recovery

#### Question Processing Error Handling

```typescript
class QuestionErrorHandler {
  async handleQuestionError(
    sessionId: string,
    questionId: string,
    error: Error
  ): Promise<ErrorHandlingResult> {
    // Log error
    await this.logger.error('Question processing error', {
      sessionId,
      questionId,
      error: error.message,
      stack: error.stack,
    });

    // Classify error type
    const errorType = this.classifyError(error);

    // Determine recovery strategy
    const recoveryStrategy = this.getRecoveryStrategy(errorType);

    switch (recoveryStrategy) {
      case 'retry_question':
        return await this.retryQuestion(sessionId, questionId, error);

      case 'rephrase_question':
        return await this.rephraseQuestion(sessionId, questionId, error);

      case 'escalate_to_manager':
        return await this.escalateToManager(sessionId, questionId, error);

      case 'skip_question':
        return await this.skipQuestion(sessionId, questionId, error);

      default:
        return await this.handleUnknownError(sessionId, questionId, error);
    }
  }

  private async rephraseQuestion(
    sessionId: string,
    questionId: string,
    error: Error
  ): Promise<ErrorHandlingResult> {
    // Get original question
    const originalQuestion = await this.getQuestion(sessionId, questionId);

    // Analyze error to understand what went wrong
    const errorAnalysis = await this.analyzeError(error, originalQuestion);

    // Generate rephrased question
    const rephrasedQuestion = await this.rephraseQuestion(originalQuestion, errorAnalysis);

    // Update question in session
    await this.updateQuestion(sessionId, questionId, rephrasedQuestion);

    // Re-route the rephrased question
    const routingResult = await this.rerouteQuestion(sessionId, questionId);

    return {
      strategy: 'rephrase_question',
      success: routingResult.success,
      newQuestionId: rephrasedQuestion.id,
      routingResult,
      message: `Question rephrased and re-routed due to: ${error.message}`,
    };
  }
}
```

### Testing Strategy

#### Unit Tests

- Ambiguity detection algorithms
- Question generation from templates
- Stakeholder routing logic
- Response processing and validation
- Learning pattern recognition

#### Integration Tests

- End-to-end question workflows
- Multi-channel communication
- Stakeholder availability checking
- Response consolidation
- Knowledge base integration

#### Performance Tests

- Question generation performance
- Concurrent session handling
- Response processing throughput
- Learning model training time

### Monitoring and Observability

#### Clarifying Questions Metrics

```typescript
interface ClarifyingQuestionsMetrics {
  // Question volume
  questionsGenerated: Counter;
  questionsSent: Counter;
  responsesReceived: Counter;

  // Time metrics
  questionGenerationTime: Histogram;
  responseTime: Histogram;
  sessionDuration: Histogram;

  // Quality metrics
  questionEffectiveness: Gauge;
  responseQuality: Gauge;
  sessionCompletionRate: Gauge;

  // Stakeholder metrics
  stakeholderResponseRate: Gauge;
  routingAccuracy: Gauge;
  escalationRate: Counter;

  // Learning metrics
  patternsRecognized: Counter;
  modelsUpdated: Counter;
  improvementsGenerated: Counter;
}
```

#### Clarifying Questions Events

```typescript
// Question lifecycle events
CLARIFYING_QUESTIONS.STARTED;
CLARIFYING_QUESTIONS.AMBIGUITY_DETECTED;
CLARIFYING_QUESTIONS.GENERATED;
CLARIFYING_QUESTIONS.ROUTED;
CLARIFYING_QUESTIONS.RESPONSE_RECEIVED;
CLARIFYING_QUESTIONS.SESSION_COMPLETED;

// Quality events
CLARIFYING_QUESTIONS.EFFECTIVENESS_MEASURED;
CLARIFYING_QUESTIONS.QUALITY_VALIDATED;
CLARIFYING_QUESTIONS.IMPROVEMENT_SUGGESTED;

// Learning events
CLARIFYING_QUESTIONS.PATTERN_RECOGNIZED;
CLARIFYING_QUESTIONS.MODEL_UPDATED;
CLARIFYING_QUESTIONS.LEARNING_COMPLETED;

// Stakeholder events
CLARIFYING_QUESTIONS.STAKEHOLDER_IDENTIFIED;
CLARIFYING_QUESTIONS.ROUTING_EXECUTED;
CLARIFYING_QUESTIONS.REMINDER_SENT;
CLARIFYING_QUESTIONS.ESCALATION_TRIGGERED;
```

### Configuration Examples

#### Clarifying Questions Configuration

```yaml
clarifyingQuestions:
  ambiguityDetection:
    enabled: true
    patterns:
      - id: 'vague-terms'
        name: 'Vague Terms'
        pattern: "\\b(some|several|many|few|approximately|about|around)\\b"
        type: 'vague'
        severity: 'medium'
        questionTemplates:
          - "Could you specify the exact {term} instead of '{value}'?"
          - "What is the precise {term} you're referring to?"
        examples:
          - 'some users'
          - 'several options'
          - 'approximately 100'

      - id: 'missing-metrics'
        name: 'Missing Metrics'
        pattern: "\\b(fast|slow|quick|responsive|scalable|efficient)\\b"
        type: 'missing'
        severity: 'high'
        questionTemplates:
          - "What specific metrics define '{value}' in this context?"
          - 'Can you provide quantitative requirements for {value}?'
        examples:
          - 'fast response time'
          - 'scalable architecture'
          - 'efficient processing'

    thresholds:
      minAmbiguityScore: 0.3
      maxQuestionsPerRequirement: 10
      questionComplexityLimit: 0.8

  questionGeneration:
    templates:
      - id: 'clarification-template'
        name: 'Standard Clarification'
        category: 'clarification'
        template: 'Regarding {requirement}, could you clarify {ambiguity}?'
        variables:
          - name: 'requirement'
            type: 'reference'
            source: 'requirement.title'
          - name: 'ambiguity'
            type: 'reference'
            source: 'ambiguity.text'
        style:
          formality: 'professional'
          length: 'medium'
          tone: 'neutral'
        priority: 5

    styles:
      - name: 'professional'
        formality: 'high'
        tone: 'neutral'
        structure: 'formal'
      - name: 'casual'
        formality: 'low'
        tone: 'friendly'
        structure: 'conversational'
      - name: 'technical'
        formality: 'medium'
        tone: 'analytical'
        structure: 'structured'

    prioritization:
      - name: 'impact-based'
        weight: 0.4
        factors: ['development_impact', 'risk_level', 'dependency_count']
      - name: 'urgency-based'
        weight: 0.3
        factors: ['timeline_pressure', 'blocker_status', 'stakeholder_availability']
      - name: 'complexity-based'
        weight: 0.3
        factors: ['technical_complexity', 'domain_knowledge_required', 'coordination_needed']

    grouping:
      strategies:
        - name: 'domain-based'
          description: 'Group questions by technical domain'
          enabled: true
          priority: 1
        - name: 'stakeholder-based'
          description: 'Group questions by stakeholder expertise'
          enabled: true
          priority: 2
        - name: 'dependency-based'
          description: 'Group questions by logical dependencies'
          enabled: true
          priority: 3

  stakeholders:
    routing:
      - name: 'expertise-matching'
        description: 'Route to stakeholders with relevant domain expertise'
        enabled: true
        weight: 0.5
      - name: 'role-based'
        description: 'Route based on organizational role and responsibility'
        enabled: true
        weight: 0.3
      - name: 'availability-based'
        description: 'Route to available stakeholders first'
        enabled: true
        weight: 0.2

    availability:
      businessHours: '09:00-17:00'
      timezone: 'UTC'
      maxConcurrentQuestions: 5
      responseTimeSLA:
        high: 3600000 # 1 hour
        medium: 86400000 # 24 hours
        low: 604800000 # 7 days

    escalation:
      levels:
        - level: 1
          delay: 86400000 # 24 hours
          escalateTo: 'manager'
        - level: 2
          delay: 172800000 # 48 hours
          escalateTo: 'department_head'
        - level: 3
          delay: 345600000 # 96 hours
          escalateTo: 'project_sponsor'

  lifecycle:
    reminders:
      - type: 'gentle_reminder'
        delay: 43200000 # 12 hours
        template: 'gentle_reminder'
      - type: 'urgent_reminder'
        delay: 86400000 # 24 hours
        template: 'urgent_reminder'
      - type: 'escalation_notice'
        delay: 172800000 # 48 hours
        template: 'escalation_notice'

    timeouts:
      softDeadline: 604800000 # 7 days
      hardDeadline: 1209600000 # 14 days
      autoConsolidation: true

    consolidation:
      enabled: true
      schedule: '0 9 * * *' # Daily at 9 AM
      contradictionDetection: true
      actionItemExtraction: true

    storage:
      knowledgeBase: true
      retentionPeriod: 7776000000 # 90 days
      anonymization: true

  learning:
    enabled: true
    patternRecognition: true
    feedbackCollection: true
    modelRetraining:
      schedule: '0 2 * * 0' # Weekly on Sunday at 2 AM
      minDataPoints: 100
      validationSplit: 0.2
```

This implementation provides a comprehensive clarifying questions workflow that can intelligently detect ambiguity, generate context-aware questions, route them to appropriate stakeholders, and learn from the interactions to continuously improve the process.

## Code Quality Gates Implementation

### Overview

Implement comprehensive code quality gates that enforce coding standards, best practices, and maintainability requirements across the Tamma platform. These gates will analyze code quality metrics and provide actionable feedback to developers.

### Code Quality Analyzer

**File**: `packages/gates/src/quality/quality-analyzer.ts`

```typescript
interface QualityAnalyzer {
  name: string;
  version: string;
  type: 'linting' | 'complexity' | 'duplication' | 'maintainability' | 'security';
  capabilities: AnalyzerCapabilities;
  config: AnalyzerConfig;
}

interface AnalyzerCapabilities {
  languages: string[];
  frameworks: string[];
  metrics: QualityMetric[];
  outputFormats: string[];
  performance: {
    maxAnalysisTime: number;
    maxFileSize: number;
    maxFiles: number;
  };
}

class CodeQualityAnalyzer {
  private analyzers = new Map<string, QualityAnalyzer>();
  private analysisQueue: PriorityQueue<QualityAnalysisRequest>;
  private activeAnalyses = new Map<string, QualityAnalysisContext>();
  private metricRegistry: QualityMetricRegistry;

  constructor(
    private config: QualityConfig,
    private logger: Logger,
    private eventBus: EventBus
  ) {
    this.analysisQueue = new PriorityQueue((a, b) => a.deadline.getTime() - b.deadline.getTime());
    this.metricRegistry = new QualityMetricRegistry();
    this.initializeAnalyzers();
    this.startAnalysisProcessor();
  }

  async submitAnalysis(request: QualityAnalysisRequest): Promise<string> {
    const analysisId = generateId();

    // Validate analysis request
    await this.validateAnalysisRequest(request);

    // Create analysis context
    const context: QualityAnalysisContext = {
      id: analysisId,
      request,
      status: 'queued',
      createdAt: new Date(),
      results: new Map(),
    };

    this.activeAnalyses.set(analysisId, context);
    this.analysisQueue.enqueue(request);

    await this.eventBus.emit('quality.analysis.submitted', {
      analysisId,
      projectId: request.projectId,
      buildId: request.buildId,
      analyzers: request.analyzers,
    });

    return analysisId;
  }

  private async executeAnalysis(request: QualityAnalysisRequest): Promise<void> {
    const context = this.activeAnalyses.get(
      Array.from(this.activeAnalyses.values()).find((ctx) => ctx.request === request)!.id
    )!;

    context.status = 'running';
    context.startedAt = new Date();

    await this.eventBus.emit('quality.analysis.started', {
      analysisId: context.id,
      projectId: request.projectId,
      buildId: request.buildId,
    });

    try {
      // Execute analyzers in parallel based on priority
      const analysisPromises = request.analyzers.map((analyzerName) =>
        this.executeAnalyzer(context.id, analyzerName, request)
      );

      const results = await Promise.allSettled(analysisPromises);

      // Process results
      for (let i = 0; i < results.length; i++) {
        const analyzerName = request.analyzers[i];
        const result = results[i];

        if (result.status === 'fulfilled') {
          context.results.set(analyzerName, result.value);
        } else {
          context.results.set(analyzerName, {
            analyzer: analyzerName,
            success: false,
            error: result.reason.message,
            metrics: [],
            issues: [],
            score: 0,
            recommendations: [],
          });
        }
      }

      // Apply quality thresholds
      const thresholdResult = await this.applyThresholds(
        request.thresholds,
        Array.from(context.results.values())
      );

      context.status = 'completed';
      context.completedAt = new Date();
      context.thresholdResult = thresholdResult;

      await this.eventBus.emit('quality.analysis.completed', {
        analysisId: context.id,
        projectId: request.projectId,
        buildId: request.buildId,
        score: thresholdResult.overallScore,
        passed: thresholdResult.passed,
        duration: context.completedAt.getTime() - context.startedAt!.getTime(),
      });
    } catch (error) {
      context.status = 'failed';
      context.error = error.message;
      context.completedAt = new Date();

      await this.eventBus.emit('quality.analysis.failed', {
        analysisId: context.id,
        projectId: request.projectId,
        buildId: request.buildId,
        error: error.message,
      });
    }
  }
}
```

### Linting Analyzer Implementation

**File**: `packages/gates/src/quality/analyzers/linting-analyzer.ts`

```typescript
class LintingAnalyzerImpl {
  private eslintEngine: ESLint;
  private stylelintEngine: any;

  constructor(
    private config: LintingConfig,
    private logger: Logger
  ) {
    this.initializeEngines();
  }

  async analyze(sourcePath: string): Promise<LintingResult> {
    const files = await this.findLintableFiles(sourcePath);
    const results: LintingFileResult[] = [];

    for (const file of files) {
      const fileResult = await this.analyzeFile(file);
      results.push(fileResult);
    }

    return this.aggregateLintingResults(results);
  }

  private async analyzeJavaScriptFile(filePath: string): Promise<LintingFileResult> {
    const results = await this.eslintEngine.lintFiles([filePath]);
    const issues: QualityIssue[] = [];

    for (const result of results) {
      for (const message of result.messages) {
        issues.push({
          id: generateId(),
          analyzer: 'eslint',
          type: 'linting',
          severity: this.mapESLintSeverity(message.severity),
          category: this.categorizeESLintRule(message.ruleId || ''),
          title: message.message,
          description: this.getRuleDescription(message.ruleId || ''),
          file: filePath,
          line: message.line || 0,
          column: message.column || 0,
          code: await this.extractCodeLine(filePath, message.line || 0),
          rule: message.ruleId || '',
          fixable: message.fix !== undefined,
          suggestions: message.suggestions || [],
          debtMinutes: this.calculateDebtMinutes(message.ruleId || '', message.severity),
          metadata: {
            ruleId: message.ruleId,
            source: 'eslint',
          },
        });
      }
    }

    // Calculate file metrics
    const metrics = await this.calculateFileMetrics(filePath);

    return {
      file: filePath,
      issues,
      metrics,
    };
  }

  private initializeEngines(): void {
    // Initialize ESLint
    this.eslintEngine = new ESLint({
      baseConfig: this.createESLintConfig(),
      extensions: this.config.extensions,
      ignorePatterns: this.config.ignorePatterns,
      useEslintrc: false,
      overrideConfig: this.parseESLintRules(),
    });

    // Initialize Stylelint
    this.stylelintEngine = stylelint.create({
      config: this.createStylelintConfig(),
      files: this.config.extensions.join(','),
    });
  }

  private createESLintConfig(): ESLint.ConfigData {
    return {
      env: {
        browser: true,
        es2022: true,
        node: true,
      },
      extends: [
        'eslint:recommended',
        '@typescript-eslint/recommended',
        '@typescript-eslint/recommended-requiring-type-checking',
      ],
      parser: '@typescript-eslint/parser',
      parserOptions: {
        ecmaVersion: 'latest',
        sourceType: 'module',
        project: './tsconfig.json',
      },
      plugins: ['@typescript-eslint', 'import', 'security'],
      rules: this.parseESLintRules(),
    };
  }
}
```

### Quality Metrics Registry

**File**: `packages/gates/src/quality/metrics-registry.ts`

```typescript
class QualityMetricRegistry {
  private metrics = new Map<string, QualityMetricDefinition>();
  private calculators = new Map<string, MetricCalculator>();

  constructor() {
    this.initializeDefaultMetrics();
    this.initializeCalculators();
  }

  private initializeDefaultMetrics(): void {
    // Code complexity metrics
    this.addMetric({
      name: 'cyclomatic-complexity',
      type: 'complexity',
      category: 'complexity',
      unit: 'count',
      description: 'Cyclomatic complexity of code',
      calculation: {
        method: 'custom',
        customFunction: 'calculateCyclomaticComplexity',
      },
      thresholds: {
        max: 10,
        target: 5,
        severity: 'warning',
      },
      aggregation: {
        type: 'average',
      },
    });

    // Maintainability metrics
    this.addMetric({
      name: 'maintainability-index',
      type: 'maintainability',
      category: 'maintainability',
      unit: 'score',
      description: 'Maintainability index (0-100)',
      calculation: {
        method: 'custom',
        customFunction: 'calculateMaintainabilityIndex',
      },
      thresholds: {
        min: 70,
        target: 85,
        severity: 'warning',
      },
      aggregation: {
        type: 'average',
      },
    });

    // Duplication metrics
    this.addMetric({
      name: 'code-duplication-percentage',
      type: 'duplication',
      category: 'duplication',
      unit: 'percent',
      description: 'Percentage of duplicated code',
      calculation: {
        method: 'ratio',
        parameters: {
          numerator: 'duplicated-lines',
          denominator: 'total-lines',
        },
      },
      thresholds: {
        max: 5,
        target: 3,
        severity: 'warning',
      },
      aggregation: {
        type: 'average',
      },
    });

    // Coverage metrics
    this.addMetric({
      name: 'test-coverage',
      type: 'coverage',
      category: 'coverage',
      unit: 'percent',
      description: 'Test coverage percentage',
      calculation: {
        method: 'custom',
        customFunction: 'calculateTestCoverage',
      },
      thresholds: {
        min: 80,
        target: 90,
        severity: 'error',
      },
      aggregation: {
        type: 'average',
      },
    });
  }
}
```

### Database Schema

**File**: `packages/gates/src/migrations/006_code_quality.sql`

```sql
-- Quality analysis tracking
CREATE TABLE quality_analyses (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    project_id UUID NOT NULL,
    build_id UUID NOT NULL REFERENCES builds(id),
    status VARCHAR(20) NOT NULL DEFAULT 'pending',
    analyzers TEXT[] NOT NULL,
    overall_score INTEGER,
    passed BOOLEAN,
    started_at TIMESTAMP WITH TIME ZONE,
    completed_at TIMESTAMP WITH TIME ZONE,
    deadline TIMESTAMP WITH TIME ZONE,
    error_message TEXT,
    metadata JSONB DEFAULT '{}'
);

-- Quality metrics
CREATE TABLE quality_metrics (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    analysis_id UUID NOT NULL REFERENCES quality_analyses(id),
    name VARCHAR(100) NOT NULL,
    type VARCHAR(20) NOT NULL,
    category VARCHAR(50) NOT NULL,
    value NUMERIC NOT NULL,
    unit VARCHAR(20) NOT NULL,
    threshold_min NUMERIC,
    threshold_max NUMERIC,
    threshold_target NUMERIC,
    passed BOOLEAN NOT NULL,
    created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW()
);

-- Quality issues
CREATE TABLE quality_issues (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    analysis_id UUID NOT NULL REFERENCES quality_analyses(id),
    analyzer VARCHAR(50) NOT NULL,
    type VARCHAR(20) NOT NULL,
    severity VARCHAR(20) NOT NULL,
    category VARCHAR(50) NOT NULL,
    title TEXT NOT NULL,
    description TEXT,
    file_path TEXT,
    line_number INTEGER,
    column_number INTEGER,
    code_snippet TEXT,
    rule VARCHAR(100),
    fixable BOOLEAN DEFAULT false,
    debt_minutes INTEGER,
    metadata JSONB DEFAULT '{}',
    created_at TIMESTAMP WITH TIME ZONE DEFAULT NOW()
);
```

### Configuration

**File**: `packages/gates/src/config/quality.config.ts`

```typescript
export const defaultQualityConfig: QualityConfig = {
  analyzers: {
    linting: {
      enabled: true,
      tool: 'eslint',
      rules: [
        {
          name: '@typescript-eslint/no-unused-vars',
          severity: 'error',
          description: 'Disallow unused variables',
        },
        {
          name: '@typescript-eslint/explicit-function-return-type',
          severity: 'warn',
          description: 'Require explicit return types',
        },
        {
          name: 'complexity',
          severity: 'warn',
          description: 'Enforce complexity limits',
          options: { max: 10 },
        },
      ],
      extensions: ['.js', '.jsx', '.ts', '.tsx'],
      ignorePatterns: ['node_modules/**', 'dist/**', 'build/**', '**/*.d.ts'],
    },
    complexity: {
      enabled: true,
      tool: 'complexity-report',
      metrics: ['cyclomatic', 'cognitive', 'halstead'],
      thresholds: {
        cyclomatic: 10,
        cognitive: 15,
      },
    },
    duplication: {
      enabled: true,
      tool: 'jscpd',
      minLines: 5,
      minTokens: 50,
      ignorePatterns: ['**/*.test.*', '**/*.spec.*', '**/node_modules/**'],
    },
    maintainability: {
      enabled: true,
      tool: 'plato',
      metrics: ['maintainability-index', 'loc', 'effort'],
      thresholds: {
        maintainabilityIndex: 70,
      },
    },
  },
  thresholds: {
    overall: {
      minScore: 80,
      maxIssues: 50,
      maxCriticalIssues: 0,
      maxTechnicalDebt: 120,
    },
    metrics: {
      'cyclomatic-complexity': { max: 10, target: 5, severity: 'warning' },
      'maintainability-index': { min: 70, target: 85, severity: 'warning' },
      'code-duplication-percentage': { max: 5, target: 3, severity: 'warning' },
      'test-coverage': { min: 80, target: 90, severity: 'error' },
    },
    custom: [],
  },
  reporting: {
    enabled: true,
    formats: ['json', 'html'],
    includeMetrics: true,
    includeIssues: true,
    includeTrends: true,
    includeRecommendations: true,
    retentionDays: 90,
  },
  trends: {
    enabled: true,
    calculationPeriod: 'daily',
    baselinePeriod: 30,
    trendThreshold: 5,
    metrics: ['maintainability-index', 'code-duplication-percentage', 'test-coverage'],
  },
};
```

### Testing Strategy

#### Unit Tests

```typescript
describe('CodeQualityAnalyzer', () => {
  let qualityAnalyzer: CodeQualityAnalyzer;
  let mockEventBus: jest.Mocked<EventBus>;

  beforeEach(() => {
    mockEventBus = createMockEventBus();
    qualityAnalyzer = new CodeQualityAnalyzer(defaultQualityConfig, mockLogger, mockEventBus);
  });

  describe('submitAnalysis', () => {
    it('should submit quality analysis and return analysis ID', async () => {
      const request: QualityAnalysisRequest = {
        projectId: 'project-1',
        buildId: 'build-1',
        context: {
          sourcePath: '/tmp/test-project',
          buildArtifacts: [],
          changedFiles: ['src/test.ts'],
          branch: 'feature/test',
          commit: 'abc123',
        },
        analyzers: ['linting-eslint'],
        thresholds: defaultQualityConfig.thresholds,
        deadline: new Date(Date.now() + 3600000),
      };

      const analysisId = await qualityAnalyzer.submitAnalysis(request);

      expect(analysisId).toBeDefined();
      expect(mockEventBus.emit).toHaveBeenCalledWith(
        'quality.analysis.submitted',
        expect.any(Object)
      );
    });
  });
});
```

### Integration Points

#### Quality Gates Integration

```typescript
interface QualityGateIntegration {
  // CI/CD Pipeline Integration
  pipeline: {
    preCommit: boolean;
    prePush: boolean;
    pullRequest: boolean;
    merge: boolean;
    release: boolean;
  };

  // Build System Integration
  build: {
    parallel: boolean;
    timeout: number;
    retryAttempts: number;
    failFast: boolean;
  };

  // Notification Integration
  notifications: {
    slack: SlackConfig;
    email: EmailConfig;
    teams: TeamsConfig;
    jira: JiraConfig;
  };

  // Metrics Integration
  metrics: {
    prometheus: PrometheusConfig;
    grafana: GrafanaConfig;
    datadog: DatadogConfig;
  };
}
```

### Error Handling and Recovery

#### Quality Analysis Error Handling

```typescript
class QualityErrorHandler {
  async handleAnalysisError(
    analysisId: string,
    analyzerName: string,
    error: Error
  ): Promise<ErrorHandlingResult> {
    // Log error
    await this.logger.error('Quality analysis error', {
      analysisId,
      analyzer: analyzerName,
      error: error.message,
      stack: error.stack,
    });

    // Classify error type
    const errorType = this.classifyError(error);

    // Determine recovery strategy
    const recoveryStrategy = this.getRecoveryStrategy(errorType);

    switch (recoveryStrategy) {
      case 'retry_analyzer':
        return await this.retryAnalyzer(analysisId, analyzerName, error);

      case 'skip_analyzer':
        return await this.skipAnalyzer(analysisId, analyzerName, error);

      case 'fail_analysis':
        return await this.failAnalysis(analysisId, analyzerName, error);

      default:
        return await this.handleUnknownError(analysisId, analyzerName, error);
    }
  }
}
```

### Monitoring and Observability

#### Quality Gates Metrics

```typescript
interface QualityGatesMetrics {
  // Analysis volume
  analysesSubmitted: Counter;
  analysesCompleted: Counter;
  analysesFailed: Counter;

  // Time metrics
  analysisTime: Histogram;
  analyzerTime: Histogram;
  queueTime: Histogram;

  // Quality metrics
  qualityScore: Gauge;
  issuesFound: Counter;
  thresholdsViolated: Counter;

  // Performance metrics
  analyzerPerformance: Histogram;
  queueDepth: Gauge;
  concurrentAnalyses: Gauge;
}
```

#### Quality Gates Events

```typescript
// Analysis lifecycle events
QUALITY_ANALYSIS.SUBMITTED;
QUALITY_ANALYSIS.STARTED;
QUALITY_ANALYSIS.COMPLETED;
QUALITY_ANALYSIS.FAILED;

// Quality events
QUALITY.THRESHOLD_VIOLATED;
QUALITY.SCORE_CALCULATED;
QUALITY.ISSUES_DETECTED;
QUALITY.RECOMMENDATIONS_GENERATED;

// Trend events
QUALITY.TREND_CALCULATED;
QUALITY.BASELINE_ESTABLISHED;
QUALITY.DEGRADATION_DETECTED;
QUALITY.IMPROVEMENT_DETECTED;
```

This consolidated implementation provides both comprehensive clarifying questions workflow and code quality gates in a single unified story, enabling intelligent requirement clarification while enforcing high code quality standards across the Tamma platform.

### References

- **🔴 MANDATORY PROCESS:** [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
- **Knowledge Base:** [.dev/README.md](../../.dev/README.md) - Search spikes, bugs, findings, decisions
