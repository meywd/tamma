# Story 3.6: Ambiguity Scoring Implementation

**Epic**: Epic 3 - Quality Gates & Intelligence Layer  
**Status**: Ready for Development  
**Priority**: High

## Acceptance Criteria

- [ ] Ambiguity scoring system analyzes requirements and assigns quantitative ambiguity scores
- [ ] System identifies different types of ambiguity (vague, missing, contradictory, implicit)
- [ ] Scoring algorithm considers context, domain, and stakeholder impact
- [ ] System provides detailed ambiguity breakdown with specific recommendations
- [ ] Scoring model learns from historical data and stakeholder feedback
- [ ] Ambiguity thresholds trigger appropriate workflows (clarifying questions, research, escalation)
- [ ] System tracks ambiguity trends and provides insights for requirement quality improvement

## Technical Context

### ⚠️ Development Process Reminder

**Before implementing this story, ensure you have:**
1. ✅ Read [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
2. ✅ Searched `.dev/` directory for related spikes, bugs, findings, and decisions
3. ✅ Reviewed relevant documentation in `docs/` directory
4. ✅ Checked existing code patterns for similar functionality
5. ✅ Planned TDD approach (Red-Green-Refactor cycle)


### Ambiguity Scoring Overview

The Ambiguity Scoring system is responsible for quantitatively assessing the clarity and completeness of requirements. It analyzes text for various types of ambiguity, calculates overall ambiguity scores, and provides actionable insights to improve requirement quality.

### Core Responsibilities

1. **Ambiguity Detection and Classification**
   - Identify vague terms, missing information, and contradictions
   - Classify ambiguity by type, severity, and impact
   - Detect implicit assumptions and unstated constraints
   - Analyze context-dependent ambiguity

2. **Quantitative Scoring**
   - Calculate ambiguity scores at multiple levels (word, sentence, requirement)
   - Weight scores based on domain importance and stakeholder impact
   - Aggregate scores into overall requirement clarity metrics
   - Track ambiguity trends over time

3. **Context-Aware Analysis**
   - Consider domain-specific terminology and conventions
   - Analyze stakeholder expertise and communication patterns
   - Evaluate project context and technical complexity
   - Account for organizational language and culture

4. **Learning and Adaptation**
   - Learn from historical ambiguity patterns and resolutions
   - Incorporate stakeholder feedback on scoring accuracy
   - Adapt scoring models based on project-specific characteristics
   - Continuously improve detection algorithms

### Implementation Details

#### Ambiguity Scoring Configuration Schema

```typescript
interface AmbiguityScoringConfig {
  // Detection patterns
  patterns: AmbiguityPattern[];

  // Scoring weights
  weights: ScoringWeights;

  // Context analysis
  context: ContextAnalysisConfig;

  // Thresholds
  thresholds: AmbiguityThresholds;

  // Learning settings
  learning: LearningConfig;

  // Output settings
  output: OutputConfig;
}

interface AmbiguityPattern {
  id: string;
  name: string;
  description: string;
  type: AmbiguityType;
  severity: AmbiguitySeverity;
  pattern: RegExp;
  weight: number;
  context: PatternContext;
  examples: PatternExample[];
  counterexamples: PatternExample[];
  adaptations: DomainAdaptation[];
}

interface ScoringWeights {
  // Ambiguity type weights
  typeWeights: Record<AmbiguityType, number>;

  // Severity weights
  severityWeights: Record<AmbiguitySeverity, number>;

  // Context weights
  contextWeights: {
    domain: number;
    stakeholder: number;
    project: number;
    organizational: number;
  };

  // Position weights
  positionWeights: {
    title: number;
    description: number;
    acceptanceCriteria: number;
    constraints: number;
  };

  // Impact weights
  impactWeights: {
    development: number;
    testing: number;
    deployment: number;
    maintenance: number;
  };
}

interface ContextAnalysisConfig {
  // Domain analysis
  domains: DomainConfig[];

  // Stakeholder analysis
  stakeholders: StakeholderAnalysisConfig;

  // Project context
  project: ProjectContextConfig;

  // Organizational context
  organizational: OrganizationalContextConfig;
}

interface AmbiguityThresholds {
  // Score thresholds
  minimal: number;
  low: number;
  medium: number;
  high: number;
  critical: number;

  // Action thresholds
  clarifyingQuestions: number;
  researchRequired: number;
  escalationRequired: number;

  // Trend thresholds
  trendIncrease: number;
  trendDecrease: number;
  stableThreshold: number;
}
```

#### Ambiguity Scoring Engine

```typescript
class AmbiguityScoringEngine implements IAmbiguityScoringEngine {
  private readonly config: AmbiguityScoringConfig;
  private readonly patternMatcher: IPatternMatcher;
  private readonly contextAnalyzer: IContextAnalyzer;
  private readonly scoreCalculator: IScoreCalculator;
  private readonly learningEngine: ILearningEngine;
  private readonly trendAnalyzer: ITrendAnalyzer;

  async scoreRequirement(requirement: Requirement): Promise<AmbiguityScore> {
    const scoringId = this.generateScoringId();
    const startTime = Date.now();

    try {
      // Emit scoring start event
      await this.eventStore.append({
        type: 'AMBIGUITY_SCORING.STARTED',
        tags: {
          scoringId,
          requirementId: requirement.id,
          issueId: requirement.issueId,
        },
        data: {
          requirement: requirement.title,
          domain: requirement.domain,
        },
      });

      // Analyze context
      const context = await this.contextAnalyzer.analyze(requirement);

      // Detect ambiguities
      const ambiguities = await this.detectAmbiguities(requirement, context);

      // Calculate scores
      const scores = await this.calculateScores(ambiguities, context);

      // Analyze trends
      const trends = await this.analyzeTrends(requirement, scores);

      // Generate recommendations
      const recommendations = await this.generateRecommendations(ambiguities, scores, context);

      // Create final score
      const ambiguityScore: AmbiguityScore = {
        id: scoringId,
        requirementId: requirement.id,
        overallScore: scores.overall,
        categoryScores: scores.categories,
        ambiguities,
        context,
        trends,
        recommendations,
        metadata: {
          scoredAt: new Date().toISOString(),
          duration: Date.now() - startTime,
          version: this.config.version,
          confidence: scores.confidence,
        },
      };

      // Store score for learning
      await this.storeScoreForLearning(ambiguityScore);

      // Emit scoring completion event
      await this.eventStore.append({
        type: 'AMBIGUITY_SCORING.COMPLETED',
        tags: {
          scoringId,
          requirementId: requirement.id,
          issueId: requirement.issueId,
        },
        data: {
          overallScore: ambiguityScore.overallScore,
          ambiguitiesCount: ambiguities.length,
          recommendationsCount: recommendations.length,
          confidence: scores.confidence,
        },
      });

      return ambiguityScore;
    } catch (error) {
      // Emit scoring error event
      await this.eventStore.append({
        type: 'AMBIGUITY_SCORING.ERROR',
        tags: {
          scoringId,
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

  private async detectAmbiguities(
    requirement: Requirement,
    context: AnalysisContext
  ): Promise<DetectedAmbiguity[]> {
    const ambiguities: DetectedAmbiguity[] = [];
    const text = this.extractText(requirement);

    // Apply pattern matching
    for (const pattern of this.config.patterns) {
      const matches = await this.patternMatcher.match(pattern, text, context);

      for (const match of matches) {
        // Calculate base score
        const baseScore = this.calculateBaseScore(pattern, match);

        // Apply context adjustments
        const contextAdjustments = await this.applyContextAdjustments(pattern, match, context);

        // Calculate final score
        const finalScore = baseScore + contextAdjustments.total;

        const ambiguity: DetectedAmbiguity = {
          id: this.generateAmbiguityId(),
          patternId: pattern.id,
          type: pattern.type,
          severity: pattern.severity,
          text: match.text,
          location: match.location,
          score: finalScore,
          baseScore,
          contextAdjustments,
          confidence: this.calculateDetectionConfidence(pattern, match, context),
          impact: await this.assessImpact(pattern, match, context),
          suggestions: await this.generateSuggestions(pattern, match, context),
          metadata: {
            detectedAt: new Date().toISOString(),
            patternVersion: pattern.version,
            contextVersion: context.version,
          },
        };

        ambiguities.push(ambiguity);
      }
    }

    // Detect semantic ambiguities
    const semanticAmbiguities = await this.detectSemanticAmbiguities(text, context);
    ambiguities.push(...semanticAmbiguities);

    // Detect structural ambiguities
    const structuralAmbiguities = await this.detectStructuralAmbiguities(requirement, context);
    ambiguities.push(...structuralAmbiguities);

    // Detect contextual ambiguities
    const contextualAmbiguities = await this.detectContextualAmbiguities(requirement, context);
    ambiguities.push(...contextualAmbiguities);

    // Remove duplicates and sort by score
    return this.deduplicateAndSort(ambiguities);
  }

  private async calculateScores(
    ambiguities: DetectedAmbiguity[],
    context: AnalysisContext
  ): Promise<CalculatedScores> {
    // Calculate category scores
    const categoryScores = await this.calculateCategoryScores(ambiguities, context);

    // Calculate overall score
    const overallScore = await this.calculateOverallScore(categoryScores, context);

    // Calculate confidence
    const confidence = await this.calculateScoringConfidence(ambiguities, context);

    // Calculate distribution
    const distribution = this.calculateScoreDistribution(ambiguities);

    return {
      overall: overallScore,
      categories: categoryScores,
      confidence,
      distribution,
      statistics: this.calculateScoreStatistics(ambiguities),
    };
  }

  private async calculateCategoryScores(
    ambiguities: DetectedAmbiguity[],
    context: AnalysisContext
  ): Promise<CategoryScores> {
    const categories: CategoryScores = {
      vague: 0,
      missing: 0,
      contradictory: 0,
      implicit: 0,
      contextual: 0,
      structural: 0,
    };

    // Group ambiguities by type
    const groupedAmbiguities = this.groupByType(ambiguities);

    for (const [type, typeAmbiguities] of Object.entries(groupedAmbiguities)) {
      const categoryScore = await this.calculateCategoryScore(
        type as AmbiguityType,
        typeAmbiguities,
        context
      );

      categories[type] = categoryScore;
    }

    return categories;
  }

  private async calculateCategoryScore(
    type: AmbiguityType,
    ambiguities: DetectedAmbiguity[],
    context: AnalysisContext
  ): Promise<number> {
    if (ambiguities.length === 0) {
      return 0;
    }

    // Get type weight
    const typeWeight = this.config.weights.typeWeights[type];

    // Calculate weighted average
    const totalWeight = ambiguities.reduce((sum, ambiguity) => {
      const severityWeight = this.config.weights.severityWeights[ambiguity.severity];
      return sum + ambiguity.score * severityWeight;
    }, 0);

    const totalSeverityWeight = ambiguities.reduce(
      (sum, ambiguity) => sum + this.config.weights.severityWeights[ambiguity.severity],
      0
    );

    const baseScore = totalWeight / totalSeverityWeight;

    // Apply context adjustments
    const contextMultiplier = await this.getContextMultiplier(type, context);

    return Math.min(1.0, baseScore * typeWeight * contextMultiplier);
  }

  private async calculateOverallScore(
    categoryScores: CategoryScores,
    context: AnalysisContext
  ): Promise<number> {
    // Calculate weighted sum of category scores
    let totalScore = 0;
    let totalWeight = 0;

    for (const [category, score] of Object.entries(categoryScores)) {
      const weight = this.getCategoryWeight(category as AmbiguityType, context);
      totalScore += score * weight;
      totalWeight += weight;
    }

    const baseScore = totalWeight > 0 ? totalScore / totalWeight : 0;

    // Apply project complexity multiplier
    const complexityMultiplier = this.getComplexityMultiplier(context);

    // Apply stakeholder impact multiplier
    const impactMultiplier = await this.getImpactMultiplier(context);

    const finalScore = baseScore * complexityMultiplier * impactMultiplier;

    return Math.min(1.0, finalScore);
  }

  private async detectSemanticAmbiguities(
    text: string,
    context: AnalysisContext
  ): Promise<DetectedAmbiguity[]> {
    const ambiguities: DetectedAmbiguity[] = [];

    // Use NLP models for semantic analysis
    const semanticAnalysis = await this.semanticAnalyzer.analyze(text, {
      domain: context.domain,
      language: context.language,
      context: context.projectContext,
    });

    // Detect polysemy (words with multiple meanings)
    for (const polysemy of semanticAnalysis.polysemy) {
      const ambiguity: DetectedAmbiguity = {
        id: this.generateAmbiguityId(),
        patternId: 'semantic-polysemy',
        type: 'vague',
        severity: this.assessPolysemySeverity(polysemy),
        text: polysemy.text,
        location: polysemy.location,
        score: this.calculatePolysemyScore(polysemy),
        baseScore: polysemy.confidence,
        contextAdjustments: await this.calculateSemanticContextAdjustments(polysemy, context),
        confidence: polysemy.confidence,
        impact: await this.assessSemanticImpact(polysemy, context),
        suggestions: await this.generateSemanticSuggestions(polysemy, context),
        metadata: {
          detectedAt: new Date().toISOString(),
          semanticType: 'polysemy',
          meanings: polysemy.meanings,
        },
      };

      ambiguities.push(ambiguity);
    }

    // Detect synonym conflicts
    for (const conflict of semanticAnalysis.synonymConflicts) {
      const ambiguity: DetectedAmbiguity = {
        id: this.generateAmbiguityId(),
        patternId: 'semantic-conflict',
        type: 'contradictory',
        severity: 'medium',
        text: conflict.text,
        location: conflict.location,
        score: this.calculateConflictScore(conflict),
        baseScore: conflict.severity,
        contextAdjustments: await this.calculateConflictContextAdjustments(conflict, context),
        confidence: conflict.confidence,
        impact: await this.assessConflictImpact(conflict, context),
        suggestions: await this.generateConflictSuggestions(conflict, context),
        metadata: {
          detectedAt: new Date().toISOString(),
          semanticType: 'synonym_conflict',
          conflictingTerms: conflict.terms,
        },
      };

      ambiguities.push(ambiguity);
    }

    return ambiguities;
  }

  private async detectStructuralAmbiguities(
    requirement: Requirement,
    context: AnalysisContext
  ): Promise<DetectedAmbiguity[]> {
    const ambiguities: DetectedAmbiguity[] = [];

    // Analyze requirement structure
    const structureAnalysis = await this.structureAnalyzer.analyze(requirement);

    // Check for missing sections
    for (const missingSection of structureAnalysis.missingSections) {
      const ambiguity: DetectedAmbiguity = {
        id: this.generateAmbiguityId(),
        patternId: 'structural-missing',
        type: 'missing',
        severity: this.assessMissingSectionSeverity(missingSection),
        text: `Missing ${missingSection.section}`,
        location: { section: missingSection.section, line: 0 },
        score: this.calculateMissingSectionScore(missingSection),
        baseScore: missingSection.importance,
        contextAdjustments: await this.calculateStructuralContextAdjustments(
          missingSection,
          context
        ),
        confidence: missingSection.confidence,
        impact: await this.assessStructuralImpact(missingSection, context),
        suggestions: await this.generateStructuralSuggestions(missingSection, context),
        metadata: {
          detectedAt: new Date().toISOString(),
          structuralType: 'missing_section',
          section: missingSection.section,
          importance: missingSection.importance,
        },
      };

      ambiguities.push(ambiguity);
    }

    // Check for inconsistent structure
    for (const inconsistency of structureAnalysis.inconsistencies) {
      const ambiguity: DetectedAmbiguity = {
        id: this.generateAmbiguityId(),
        patternId: 'structural-inconsistency',
        type: 'contradictory',
        severity: 'medium',
        text: inconsistency.description,
        location: inconsistency.location,
        score: this.calculateInconsistencyScore(inconsistency),
        baseScore: inconsistency.severity,
        contextAdjustments: await this.calculateInconsistencyContextAdjustments(
          inconsistency,
          context
        ),
        confidence: inconsistency.confidence,
        impact: await this.assessInconsistencyImpact(inconsistency, context),
        suggestions: await this.generateInconsistencySuggestions(inconsistency, context),
        metadata: {
          detectedAt: new Date().toISOString(),
          structuralType: 'inconsistency',
          inconsistencyType: inconsistency.type,
        },
      };

      ambiguities.push(ambiguity);
    }

    return ambiguities;
  }

  private async generateRecommendations(
    ambiguities: DetectedAmbiguity[],
    scores: CalculatedScores,
    context: AnalysisContext
  ): Promise<AmbiguityRecommendation[]> {
    const recommendations: AmbiguityRecommendation[] = [];

    // Generate recommendations based on high-scoring ambiguities
    const highImpactAmbiguities = ambiguities
      .filter((a) => a.score >= this.config.thresholds.medium)
      .sort((a, b) => b.score - a.score);

    for (const ambiguity of highImpactAmbiguities) {
      const recommendation = await this.generateAmbiguityRecommendation(ambiguity, context);

      if (recommendation) {
        recommendations.push(recommendation);
      }
    }

    // Generate category-specific recommendations
    for (const [category, score] of Object.entries(scores.categories)) {
      if (score >= this.config.thresholds.medium) {
        const categoryRecommendation = await this.generateCategoryRecommendation(
          category as AmbiguityType,
          score,
          context
        );

        if (categoryRecommendation) {
          recommendations.push(categoryRecommendation);
        }
      }
    }

    // Generate overall recommendations
    if (scores.overall >= this.config.thresholds.high) {
      const overallRecommendations = await this.generateOverallRecommendations(scores, context);
      recommendations.push(...overallRecommendations);
    }

    // Prioritize and limit recommendations
    return recommendations.sort((a, b) => b.priority - a.priority).slice(0, 10); // Top 10 recommendations
  }

  private async generateAmbiguityRecommendation(
    ambiguity: DetectedAmbiguity,
    context: AnalysisContext
  ): Promise<AmbiguityRecommendation | null> {
    // Get recommendation template for ambiguity type
    const template = this.getRecommendationTemplate(ambiguity.type, ambiguity.severity);

    if (!template) {
      return null;
    }

    // Generate specific recommendation
    const recommendation: AmbiguityRecommendation = {
      id: this.generateRecommendationId(),
      ambiguityId: ambiguity.id,
      type: 'specific',
      category: ambiguity.type,
      priority: this.calculateRecommendationPriority(ambiguity),
      title: template.title,
      description: await this.fillTemplate(template.description, { ambiguity, context }),
      actions: await this.generateRecommendationActions(ambiguity, context),
      expectedImpact: this.calculateExpectedImpact(ambiguity),
      effort: this.estimateEffort(ambiguity),
      confidence: ambiguity.confidence,
      metadata: {
        generatedAt: new Date().toISOString(),
        templateId: template.id,
        ambiguityScore: ambiguity.score,
      },
    };

    return recommendation;
  }
}
```

#### Context Analyzer

```typescript
class ContextAnalyzer implements IContextAnalyzer {
  private readonly domainAnalyzer: IDomainAnalyzer;
  private readonly stakeholderAnalyzer: IStakeholderAnalyzer;
  private readonly projectAnalyzer: IProjectAnalyzer;
  private readonly organizationalAnalyzer: IOrganizationalAnalyzer;

  async analyze(requirement: Requirement): Promise<AnalysisContext> {
    // Analyze domain context
    const domainContext = await this.domainAnalyzer.analyze(requirement);

    // Analyze stakeholder context
    const stakeholderContext = await this.stakeholderAnalyzer.analyze(requirement);

    // Analyze project context
    const projectContext = await this.projectAnalyzer.analyze(requirement);

    // Analyze organizational context
    const organizationalContext = await this.organizationalAnalyzer.analyze(requirement);

    // Combine contexts
    const context: AnalysisContext = {
      requirementId: requirement.id,
      domain: domainContext,
      stakeholder: stakeholderContext,
      project: projectContext,
      organizational: organizationalContext,
      language: this.detectLanguage(requirement),
      complexity: this.calculateComplexity(requirement),
      version: this.generateContextVersion(),
    };

    return context;
  }

  private async analyzeDomainContext(requirement: Requirement): Promise<DomainContext> {
    // Identify primary domain
    const primaryDomain = await this.identifyDomain(requirement);

    // Get domain-specific terminology
    const terminology = await this.getDomainTerminology(primaryDomain);

    // Analyze domain conventions
    const conventions = await this.analyzeDomainConventions(primaryDomain);

    // Calculate domain complexity
    const complexity = await this.calculateDomainComplexity(requirement, primaryDomain);

    return {
      primary: primaryDomain,
      secondary: await this.identifySecondaryDomains(requirement),
      terminology,
      conventions,
      complexity,
      expertise: await this.getRequiredExpertise(requirement, primaryDomain),
    };
  }

  private async analyzeStakeholderContext(requirement: Requirement): Promise<StakeholderContext> {
    // Identify stakeholders
    const stakeholders = await this.identifyStakeholders(requirement);

    // Analyze stakeholder expertise
    const expertise = await this.analyzeStakeholderExpertise(stakeholders);

    // Analyze communication patterns
    const communication = await this.analyzeCommunicationPatterns(stakeholders);

    // Calculate stakeholder impact
    const impact = await this.calculateStakeholderImpact(requirement, stakeholders);

    return {
      primary: await this.identifyPrimaryStakeholder(requirement),
      secondary: await this.identifySecondaryStakeholders(requirement),
      expertise,
      communication,
      impact,
      availability: await this.analyzeStakeholderAvailability(stakeholders),
    };
  }

  private async analyzeProjectContext(requirement: Requirement): Promise<ProjectContext> {
    // Get project information
    const project = await this.getProjectInfo(requirement);

    // Analyze project phase
    const phase = await this.analyzeProjectPhase(project);

    // Analyze technical stack
    const techStack = await this.analyzeTechStack(project);

    // Calculate project complexity
    const complexity = await this.calculateProjectComplexity(project);

    return {
      name: project.name,
      phase,
      techStack,
      complexity,
      constraints: await this.getProjectConstraints(project),
      dependencies: await this.getProjectDependencies(project),
      timeline: await this.getProjectTimeline(project),
    };
  }
}
```

#### Learning Engine

```typescript
class AmbiguityLearningEngine implements ILearningEngine {
  private readonly patternLearner: IPatternLearner;
  private readonly scoreOptimizer: IScoreOptimizer;
  private readonly feedbackProcessor: IFeedbackProcessor;

  async learnFromScoring(scoring: AmbiguityScore): Promise<LearningResult> {
    const learningData: LearningData = {
      scoringId: scoring.id,
      requirement: await this.extractRequirementData(scoring),
      ambiguities: scoring.ambiguities,
      scores: scoring,
      context: scoring.context,
      feedback: await this.collectFeedback(scoring),
      outcomes: await this.collectOutcomes(scoring),
    };

    // Update pattern recognition
    const patternUpdates = await this.updatePatterns(learningData);

    // Optimize scoring weights
    const weightUpdates = await this.optimizeWeights(learningData);

    // Update context models
    const contextUpdates = await this.updateContextModels(learningData);

    // Generate improvement suggestions
    const improvements = await this.generateImprovements(learningData);

    const learningResult: LearningResult = {
      scoringId: scoring.id,
      patternsUpdated: patternUpdates,
      weightsUpdated: weightUpdates,
      contextModelsUpdated: contextUpdates,
      improvements,
      confidence: this.calculateLearningConfidence(learningData),
      appliedAt: new Date().toISOString(),
    };

    // Emit learning event
    await this.eventStore.append({
      type: 'AMBIGUITY_SCORING.LEARNING_COMPLETED',
      tags: {
        scoringId: scoring.id,
      },
      data: {
        learningResult,
        patternsCount: patternUpdates.length,
        improvementsCount: improvements.length,
      },
    });

    return learningResult;
  }

  private async optimizeWeights(learningData: LearningData): Promise<WeightUpdate[]> {
    const updates: WeightUpdate[] = [];

    // Analyze weight performance
    const weightPerformance = await this.analyzeWeightPerformance(learningData);

    for (const performance of weightPerformance) {
      if (performance.accuracy < 0.8) {
        // Low accuracy threshold
        const optimizedWeight = await this.optimizeWeight(performance, learningData);

        const update: WeightUpdate = {
          type: performance.type,
          category: performance.category,
          oldValue: performance.currentValue,
          newValue: optimizedWeight.value,
          improvement: optimizedWeight.improvement,
          confidence: optimizedWeight.confidence,
          evidence: performance.evidence,
        };

        updates.push(update);
      }
    }

    return updates;
  }

  private async generateImprovements(learningData: LearningData): Promise<ScoringImprovement[]> {
    const improvements: ScoringImprovement[] = [];

    // Analyze false positives
    const falsePositives = await this.analyzeFalsePositives(learningData);

    for (const falsePositive of falsePositives) {
      const improvement: ScoringImprovement = {
        id: this.generateImprovementId(),
        type: 'false_positive_reduction',
        priority: this.calculateImprovementPriority(falsePositive),
        description: `Reduce false positives for ${falsePositive.pattern}`,
        currentPattern: falsePositive.pattern,
        suggestedChanges: falsePositive.suggestedChanges,
        expectedImpact: falsePositive.expectedImpact,
        evidence: falsePositive.evidence,
      };

      improvements.push(improvement);
    }

    // Analyze false negatives
    const falseNegatives = await this.analyzeFalseNegatives(learningData);

    for (const falseNegative of falseNegatives) {
      const improvement: ScoringImprovement = {
        id: this.generateImprovementId(),
        type: 'false_negative_reduction',
        priority: this.calculateImprovementPriority(falseNegative),
        description: `Reduce false negatives for ${falseNegative.type} ambiguities`,
        currentPattern: falseNegative.currentPattern,
        suggestedChanges: falseNegative.suggestedChanges,
        expectedImpact: falseNegative.expectedImpact,
        evidence: falseNegative.evidence,
      };

      improvements.push(improvement);
    }

    return improvements.sort((a, b) => b.priority - a.priority);
  }
}
```

### Integration Points

#### NLP Service Integration

```typescript
interface NLPServiceIntegration {
  // Semantic analysis
  semanticAnalysis: {
    provider: 'openai' | 'anthropic' | 'local';
    model: string;
    apiKey?: string;
    endpoint?: string;
  };

  // Language detection
  languageDetection: {
    provider: 'google' | 'azure' | 'local';
    model: string;
    confidence: number;
  };

  // Text preprocessing
  preprocessing: {
    tokenization: boolean;
    stemming: boolean;
    lemmatization: boolean;
    stopWordRemoval: boolean;
  };
}
```

#### Knowledge Base Integration

```typescript
interface KnowledgeBaseIntegration {
  // Domain knowledge
  domainKnowledge: {
    provider: 'confluence' | 'notion' | 'custom';
    endpoint: string;
    authentication: Record<string, string>;
  };

  // Historical data
  historicalData: {
    database: string;
    table: string;
    retention: number;
  };

  // Pattern library
  patternLibrary: {
    repository: string;
    versioning: boolean;
    syncInterval: number;
  };
}
```

### Error Handling and Recovery

#### Scoring Error Handling

```typescript
class ScoringErrorHandler {
  async handleScoringError(requirementId: string, error: Error): Promise<ErrorHandlingResult> {
    // Log error
    await this.logger.error('Ambiguity scoring error', {
      requirementId,
      error: error.message,
      stack: error.stack,
    });

    // Classify error type
    const errorType = this.classifyError(error);

    // Determine recovery strategy
    const recoveryStrategy = this.getRecoveryStrategy(errorType);

    switch (recoveryStrategy) {
      case 'retry_with_simplified_analysis':
        return await this.retryWithSimplifiedAnalysis(requirementId, error);

      case 'use_fallback_patterns':
        return await this.useFallbackPatterns(requirementId, error);

      case 'use_cached_score':
        return await this.useCachedScore(requirementId, error);

      case 'assign_default_score':
        return await this.assignDefaultScore(requirementId, error);

      default:
        return await this.handleUnknownError(requirementId, error);
    }
  }

  private async retryWithSimplifiedAnalysis(
    requirementId: string,
    error: Error
  ): Promise<ErrorHandlingResult> {
    // Get requirement
    const requirement = await this.getRequirement(requirementId);

    // Use simplified scoring (only basic patterns)
    const simplifiedScore = await this.calculateSimplifiedScore(requirement);

    return {
      strategy: 'retry_with_simplified_analysis',
      success: true,
      score: simplifiedScore,
      message: 'Used simplified analysis due to error',
      originalError: error.message,
    };
  }
}
```

### Testing Strategy

#### Unit Tests

- Pattern matching algorithms
- Score calculation logic
- Context analysis methods
- Learning algorithms
- Threshold validation

#### Integration Tests

- End-to-end scoring workflows
- NLP service integration
- Knowledge base queries
- Learning data collection
- Feedback processing

#### Performance Tests

- Scoring throughput under load
- Large requirement document handling
- Concurrent scoring operations
- Learning model training time

### Monitoring and Observability

#### Ambiguity Scoring Metrics

```typescript
interface AmbiguityScoringMetrics {
  // Scoring volume
  requirementsScored: Counter;
  ambiguitiesDetected: Counter;
  recommendationsGenerated: Counter;

  // Performance metrics
  scoringDuration: Histogram;
  patternMatchingTime: Histogram;
  contextAnalysisTime: Histogram;

  // Quality metrics
  scoringAccuracy: Gauge;
  falsePositiveRate: Gauge;
  falseNegativeRate: Gauge;

  // Learning metrics
  patternsUpdated: Counter;
  weightsOptimized: Counter;
  improvementsGenerated: Counter;

  // Trend metrics
  ambiguityTrend: Gauge;
  qualityTrend: Gauge;
  improvementRate: Gauge;
}
```

#### Ambiguity Scoring Events

```typescript
// Scoring lifecycle events
AMBIGUITY_SCORING.STARTED;
AMBIGUITY_SCORING.PATTERN_MATCHING;
AMBIGUITY_SCORING.CONTEXT_ANALYSIS;
AMBIGUITY_SCORING.SCORE_CALCULATION;
AMBIGUITY_SCORING.COMPLETED;
AMBIGUITY_SCORING.ERROR;

// Detection events
AMBIGUITY_DETECTED.PATTERN_MATCH;
AMBIGUITY_DETECTED.SEMANTIC_ANALYSIS;
AMBIGUITY_DETECTED.STRUCTURAL_ANALYSIS;
AMBIGUITY_DETECTED.CONTEXTUAL_ANALYSIS;

// Learning events
AMBIGUITY_SCORING.LEARNING_STARTED;
AMBIGUITY_SCORING.PATTERN_UPDATED;
AMBIGUITY_SCORING.WEIGHT_OPTIMIZED;
AMBIGUITY_SCORING.IMPROVEMENT_GENERATED;
AMBIGUITY_SCORING.LEARNING_COMPLETED;
```

### Configuration Examples

#### Ambiguity Scoring Configuration

```yaml
ambiguityScoring:
  patterns:
    - id: 'vague-quantifiers'
      name: 'Vague Quantifiers'
      type: 'vague'
      severity: 'medium'
      pattern: "\\b(some|several|many|few|approximately|about|around|roughly)\\b"
      weight: 0.6
      examples:
        - text: 'some users'
          context: 'user management'
          severity: 'medium'
        - text: 'several options'
          context: 'configuration'
          severity: 'low'
      adaptations:
        - domain: 'technical'
          weight: 0.7
          additionalPatterns: ["\\b(various|multiple|numerous)\\b"]
        - domain: 'business'
          weight: 0.5
          additionalPatterns: ["\\b(a number of|a variety of)\\b"]

    - id: 'missing-metrics'
      name: 'Missing Performance Metrics'
      type: 'missing'
      severity: 'high'
      pattern: "\\b(fast|slow|quick|responsive|scalable|efficient|performant)\\b"
      weight: 0.8
      examples:
        - text: 'fast response time'
          expectedMetric: 'response time < 200ms'
          severity: 'high'
        - text: 'scalable architecture'
          expectedMetric: 'handle 1000+ concurrent users'
          severity: 'high'

    - id: 'subjective-terms'
      name: 'Subjective Quality Terms'
      type: 'vague'
      severity: 'medium'
      pattern: "\\b(good|better|best|poor|bad|excellent|high-quality|low-quality)\\b"
      weight: 0.5
      examples:
        - text: 'good user experience'
          objectiveCriteria: 'user satisfaction score > 4.5/5'
          severity: 'medium'
        - text: 'better performance'
          objectiveCriteria: '20% improvement over baseline'
          severity: 'medium'

  weights:
    typeWeights:
      vague: 0.6
      missing: 0.9
      contradictory: 1.0
      implicit: 0.7
      contextual: 0.5
      structural: 0.8

    severityWeights:
      low: 0.3
      medium: 0.6
      high: 0.8
      critical: 1.0

    contextWeights:
      domain: 0.4
      stakeholder: 0.3
      project: 0.2
      organizational: 0.1

    positionWeights:
      title: 1.0
      description: 0.8
      acceptanceCriteria: 0.9
      constraints: 0.7

    impactWeights:
      development: 0.4
      testing: 0.3
      deployment: 0.2
      maintenance: 0.1

  thresholds:
    minimal: 0.1
    low: 0.3
    medium: 0.5
    high: 0.7
    critical: 0.9

    clarifyingQuestions: 0.4
    researchRequired: 0.6
    escalationRequired: 0.8

    trendIncrease: 0.1
    trendDecrease: -0.1
    stableThreshold: 0.05

  learning:
    enabled: true
    feedbackCollection: true
    patternRecognition: true
    weightOptimization: true
    minDataPoints: 50
    retrainingInterval: 604800000 # 7 days
    validationSplit: 0.2

  output:
    includeDetails: true
    includeSuggestions: true
    includeContext: true
    includeTrends: true
    format: ['json', 'markdown']
    confidenceThreshold: 0.7
```

This implementation provides a comprehensive ambiguity scoring system that can quantitatively assess requirement clarity, detect various types of ambiguity, provide actionable recommendations, and continuously learn from feedback to improve scoring accuracy.

### References

- **🔴 MANDATORY PROCESS:** [BEFORE_YOU_CODE.md](../../BEFORE_YOU_CODE.md)
- **Knowledge Base:** [.dev/README.md](../../.dev/README.md) - Search spikes, bugs, findings, decisions
