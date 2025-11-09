# Story 3.5: Clarifying Questions Workflow Implementation

**Epic**: Epic 3 - Quality Gates & Intelligence Layer  
**Status**: Ready for Development  
**Priority**: High

## Acceptance Criteria

- [ ] Clarifying questions workflow triggers when ambiguity is detected in requirements
- [ ] System generates intelligent, context-aware questions to resolve ambiguity
- [ ] Questions are prioritized by impact and urgency
- [ ] Questions are routed to appropriate stakeholders based on domain expertise
- [ ] System tracks question status and follows up on unanswered questions
- [ ] Question-answer pairs are stored for future learning and pattern recognition
- [ ] System learns from question patterns to improve future question generation

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

### References

- **🔴 MANDATORY PROCESS:** [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
- **Knowledge Base:** [.dev/README.md](../../.dev/README.md) - Search spikes, bugs, findings, decisions
