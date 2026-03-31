---
title: "Story 7-2: Skill Assessment Activity"
sidebar:
  order: 70
---

## User Story

As the **Tamma mentorship engine**, I need to assess a junior developer's understanding of story requirements and their skill level so that the workflow can adapt its guidance, plan complexity, and timeout thresholds to the individual developer.

## Description

Implement the `AssessJuniorCapabilityActivity` ELSA activity that evaluates a junior developer's comprehension of a story's requirements and their general skill level. The activity sends assessment questions (via Slack, email, or API), collects responses, and uses Claude AI to analyze whether the developer has correct, partial, or incorrect understanding. Based on the outcome, the state machine transitions to `PLAN_DECOMPOSITION` (correct), `CLARIFY_REQUIREMENTS` (partial), or `RE_EXPLAIN_STORY` (incorrect).

An existing implementation exists at `apps/tamma-elsa/src/Tamma.Activities/Mentorship/AssessJuniorCapabilityActivity.cs`. It currently uses simulated assessment results based on skill level vs. story complexity. This story requires replacing the simulation with real response collection and AI-powered analysis, while keeping the simulation available as a configurable mock mode for testing.

## Acceptance Criteria

### AC1: Assessment Question Generation
- [ ] Generate assessment questions dynamically based on story complexity (1-5 scale)
- [ ] Low complexity (1-2): 2 questions focused on "what" needs to be built
- [ ] Medium complexity (3): 4 questions including "what" and "how"
- [ ] High complexity (4-5): 6 questions including "what", "how", edge cases, and technology choices
- [ ] Questions reference the story's title, description, and acceptance criteria
- [ ] Question bank is configurable and extensible (not hardcoded strings)

### AC2: Multi-Channel Delivery
- [ ] Send assessment questions via Slack DM when `SlackId` is configured on the junior profile
- [ ] Send assessment questions via email when `Email` is configured
- [ ] Send assessment questions via REST API endpoint for programmatic integrations
- [ ] Message formatting is channel-appropriate (Markdown for Slack, HTML for email, JSON for API)
- [ ] Fallback to API-only delivery if no communication channel is configured

### AC3: Response Collection
- [ ] Wait for junior's response with configurable timeout (default: 5 minutes)
- [ ] Support collecting responses via Slack message reply
- [ ] Support collecting responses via API POST endpoint
- [ ] Partial responses are accepted (not all questions need answers)
- [ ] Response is timestamped and stored with the session

### AC4: AI-Powered Analysis
- [ ] Send the junior's response and story context to Claude via `ClaudeAnalysisActivity` (Story 7-4) with `AnalysisType.Assessment`
- [ ] Claude evaluates: correctness of understanding, identification of core requirements, awareness of technical challenges, reasonableness of approach
- [ ] Analysis returns a structured result: `AssessmentStatus` (Correct/Partial/Incorrect/Timeout/Error), `Confidence` (0.0-1.0), `Gaps` (list of knowledge gaps), `Strengths` (list of positive aspects)
- [ ] Confidence threshold for "Correct" is configurable (default: 0.7)
- [ ] Confidence threshold for "Partial" is configurable (default: 0.4)
- [ ] Below "Partial" threshold is "Incorrect"

### AC5: State Transition Outcomes
- [ ] `AssessmentStatus.Correct` -> transition to `PLAN_DECOMPOSITION`
- [ ] `AssessmentStatus.Partial` -> transition to `CLARIFY_REQUIREMENTS`
- [ ] `AssessmentStatus.Incorrect` -> transition to `RE_EXPLAIN_STORY`
- [ ] `AssessmentStatus.Timeout` -> transition to `DIAGNOSE_BLOCKER`
- [ ] `AssessmentStatus.Error` -> transition to `FAILED` (with error details)
- [ ] Each outcome updates the session state and logs the transition event

### AC6: Skill Level Tracking
- [ ] Track the junior's cumulative assessment scores across sessions
- [ ] Update the junior's `SkillLevel` (1-5) based on rolling assessment performance
- [ ] Skill level influences: question complexity, timeout durations, guidance verbosity
- [ ] Skill improvements are detected and logged (e.g., "Junior moved from level 2 to level 3")

### AC7: Mock/Simulation Mode
- [ ] When `UseMock=true` in configuration, use the existing simulation logic (skill level vs. complexity probability)
- [ ] Mock mode produces deterministic results when a seed is provided (for testing)
- [ ] Mock mode logs that simulation was used (not real assessment)

### AC8: Error Handling
- [ ] Handle Slack API failures gracefully (fall back to API-only)
- [ ] Handle Claude API failures gracefully (fall back to simulation with warning)
- [ ] Handle database failures with retry (3 attempts, exponential backoff)
- [ ] All errors are logged with session context for debugging

## Technical Design

### Assessment Pipeline

```typescript
// TypeScript interface mirrors for the TS engine bridge
export interface AssessmentRequest {
  sessionId: string;
  storyId: string;
  juniorId: string;
  storyTitle: string;
  storyDescription: string;
  acceptanceCriteria: string[];
  complexity: number;
  questions: AssessmentQuestion[];
  timeoutMinutes: number;
  deliveryChannels: DeliveryChannel[];
}

export interface AssessmentQuestion {
  id: string;
  text: string;
  category: 'understanding' | 'approach' | 'challenges' | 'technology' | 'edge_cases';
  required: boolean;
  expectedKeywords?: string[];
}

export type DeliveryChannel = 'slack' | 'email' | 'api';

export interface AssessmentResponse {
  sessionId: string;
  juniorId: string;
  answers: QuestionAnswer[];
  responseTimeMs: number;
  channel: DeliveryChannel;
  timestamp: Date;
}

export interface QuestionAnswer {
  questionId: string;
  answer: string;
  confidence?: number;
}

export interface AssessmentResult {
  status: 'Correct' | 'Partial' | 'Incorrect' | 'Timeout' | 'Error';
  confidence: number;
  nextState: MentorshipState;
  gaps: string[];
  strengths: string[];
  message: string;
  analysisDetails?: {
    understandingSummary: string;
    recommendedAction: string;
    aiModelUsed: string;
  };
}

export interface JuniorProfile {
  id: string;
  name: string;
  email?: string;
  slackId?: string;
  githubUsername?: string;
  skillLevel: number;        // 1-5
  assessmentHistory: {
    date: Date;
    score: number;
    storyComplexity: number;
  }[];
  learningPatterns: string[];
}
```

### Assessment Activity Interface

```typescript
export interface ISkillAssessmentActivity {
  // Execute assessment
  assess(request: AssessmentRequest): Promise<AssessmentResult>;

  // Question generation
  generateQuestions(
    story: { title: string; description: string; complexity: number; acceptanceCriteria: string[] },
    skillLevel: number
  ): AssessmentQuestion[];

  // Response analysis
  analyzeResponse(
    response: AssessmentResponse,
    storyContext: string,
    skillLevel: number
  ): Promise<AssessmentResult>;

  // Skill tracking
  updateSkillLevel(juniorId: string, assessmentScore: number): Promise<number>;
}
```

## Dependencies

- Story 7-1: State machine core (transition definitions for assessment outcomes)
- Story 7-4: Claude Analysis Activity (AI-powered response analysis)
- `Tamma.Core.Enums.AssessmentStatus` (already defined: Correct, Partial, Incorrect, Timeout, Error)
- `Tamma.Core.Enums.ConfidenceLevel` (already defined: VeryLow through VeryHigh)
- `Tamma.Core.Entities.JuniorDeveloper` (already defined with SkillLevel)
- `Tamma.Core.Interfaces.IIntegrationService` (already defined for Slack/email delivery)
- `Tamma.Data.Repositories.IMentorshipSessionRepository` (already defined)

## Testing Strategy

### Unit Tests
- Question generation produces correct number of questions per complexity level
- Question generation includes expected categories per complexity level
- Assessment with confidence >= 0.7 returns Correct status
- Assessment with confidence 0.4-0.7 returns Partial status
- Assessment with confidence < 0.4 returns Incorrect status
- Timeout returns Timeout status and transitions to DIAGNOSE_BLOCKER
- Missing story returns Error status
- Missing junior profile returns Error status
- Mock mode returns deterministic results with seed
- Skill level update correctly adjusts based on rolling average

### Integration Tests
- Full assessment flow: send questions via API -> collect response -> analyze -> return result
- Slack delivery integration (requires SLACK_TOKEN env var)
- Claude analysis integration (requires ANTHROPIC_API_KEY env var)
- Database persistence of assessment events and results
- Multiple assessments for the same junior update skill level correctly

### Edge Case Tests
- Empty response from junior (all questions unanswered)
- Very long response (>10,000 characters)
- Non-English response
- Junior responds after timeout
- Concurrent assessments for the same junior on different stories

## Configuration

```yaml
mentorship:
  assessment:
    timeout_minutes: 5
    confidence_thresholds:
      correct: 0.7
      partial: 0.4
    max_questions:
      low_complexity: 2
      medium_complexity: 4
      high_complexity: 6
    delivery:
      prefer_slack: true
      fallback_to_api: true
    mock:
      enabled: false
      seed: null  # null = random, integer = deterministic
    skill_tracking:
      rolling_window: 10  # number of assessments to consider
      level_up_threshold: 0.8  # avg score needed to level up
      level_down_threshold: 0.3  # avg score that triggers level down
```

## Success Metrics

- Assessment accuracy: AI assessment agrees with human evaluation >80%
- Question relevance: >90% of generated questions rated relevant to the story
- Response collection: >95% of responses collected within timeout window
- Skill level accuracy: junior's tracked skill level correlates with actual performance
- Mock/real parity: mock mode produces statistically similar distribution to real assessments
- Delivery success rate: >99% of assessment messages delivered to at least one channel

## Logging Requirements

All ELSA activities MUST inject `ILogger<T>` and log at these levels:

- **INFO**: Activity started (with session/issue ID), activity completed (with outcome), state transitions
- **DEBUG**: Input parameters received, intermediate LLM/API call details, decision rationale
- **WARN**: Retryable failures, timeout approaching, degraded quality gate result
- **ERROR**: Unrecoverable failures (with exception), invalid state transition, missing required data
- **Structured context**: Always include `{ sessionId, juniorId, storyId, currentState }` in all log entries
- **Sensitive data**: NEVER log student PII, credentials, or full LLM response content — log token counts and summary only
