# Story 7-4: Claude Analysis Activity

## User Story

As the **Tamma mentorship engine**, I need AI-powered analysis capabilities using Claude so that I can evaluate junior developer responses, review code quality, diagnose blockers, and generate adaptive guidance throughout the mentorship workflow.

## Description

Implement the `ClaudeAnalysisActivity` ELSA activity that calls the Anthropic Claude API to perform intelligent analysis at multiple points in the mentorship workflow. The activity supports four analysis modes: Assessment (evaluating developer understanding), CodeReview (reviewing submitted code), BlockerDiagnosis (identifying why a developer is stuck), and GuidanceGeneration (producing helpful, skill-level-appropriate guidance).

An existing implementation exists at `apps/tamma-elsa/src/Tamma.Activities/AI/ClaudeAnalysisActivity.cs`. It supports three execution modes: direct Claude API calls, engine callback (delegating to the TypeScript engine's agent toolchain), and mock mode for testing. The implementation includes retry logic with exponential backoff for 429/5xx responses, skill-level-adaptive system prompts, structured JSON output parsing, and fallback handling. This story formalizes the expected behavior, adds acceptance criteria for each analysis type, improves prompt engineering, and ensures comprehensive test coverage.

## Acceptance Criteria

### AC1: Analysis Mode - Assessment
- [ ] Accepts junior developer's response text and story context
- [ ] System prompt instructs Claude to evaluate: correctness of understanding, identification of core requirements, awareness of technical challenges, reasonableness of approach
- [ ] User prompt requests structured JSON response with: `status` (Correct/Partial/Incorrect), `confidence` (0.0-1.0), `understanding_summary`, `gaps` (list), `strengths` (list), `recommended_action`
- [ ] Confidence score accurately reflects the quality of understanding (validated by manual review of sample outputs)
- [ ] Gaps list is specific and actionable (not generic statements)
- [ ] Returns parsed `ClaudeAnalysisOutput` with `AssessmentStatus`, `Confidence`, `Summary`, `Gaps`, `Strengths`, `RecommendedAction`

### AC2: Analysis Mode - Code Review
- [ ] Accepts code content and optional context (story requirements, coding standards)
- [ ] System prompt instructs Claude to evaluate: correctness, best practices, potential bugs, readability, security
- [ ] User prompt requests structured JSON response with: `overall_quality` (Good/Acceptable/NeedsWork), `score` (0-100), `issues` (list with severity/location/issue/suggestion), `positives` (list), `learning_opportunities` (list)
- [ ] Issues are categorized by severity: Critical (must fix), Major (should fix), Minor (nice to fix), Suggestion (optional improvement)
- [ ] Suggestions include specific code examples when applicable
- [ ] Returns parsed `ClaudeAnalysisOutput` with `OverallQuality`, `Score`, `CodeReviewIssues`, `Positives`, `LearningOpportunities`

### AC3: Analysis Mode - Blocker Diagnosis
- [ ] Accepts situation description, recent activity data, and error messages
- [ ] System prompt instructs Claude to categorize blocker type from: RequirementsUnclear, TechnicalKnowledgeGap, EnvironmentIssue, ArchitectureConfusion, TestingChallenge, MotivationIssue, Other
- [ ] User prompt requests structured JSON response with: `blocker_type`, `confidence` (0.0-1.0), `root_cause`, `evidence` (list), `recommended_intervention` (Hint/Guidance/DirectAssistance/Escalation), `immediate_action`
- [ ] Diagnosis considers the developer's skill level (lower-skill developers more likely to have knowledge gap blockers)
- [ ] Returns parsed `ClaudeAnalysisOutput` with `DiagnosedBlockerType`, `Confidence`, `RootCause`, `Evidence`, `RecommendedIntervention`, `ImmediateAction`

### AC4: Analysis Mode - Guidance Generation
- [ ] Accepts the current situation description and developer context
- [ ] System prompt instructs Claude to use the Socratic method when appropriate, adapting to the developer's skill level
- [ ] User prompt requests structured JSON response with: `main_guidance`, `steps` (list), `examples` (list), `questions_to_ask_themselves` (list), `resources` (list), `encouragement`
- [ ] Guidance complexity adapts to skill level:
  - Level 1: Simple terms, thorough explanations, many examples
  - Level 3: Standard technical terminology, moderate detail
  - Level 5: Nuanced improvements, advanced patterns, minimal hand-holding
- [ ] Returns parsed `ClaudeAnalysisOutput` with `MainGuidance`, `Steps`, `Examples`, `SocraticQuestions`, `Resources`, `Encouragement`

### AC5: Skill-Level Adaptive Prompts
- [ ] System prompt includes a skill-level description block that varies from level 1 ("complete beginner, use simple terms") through level 5 ("highly skilled, focus on nuanced improvements")
- [ ] Skill level is clamped to 1-5 range (invalid values are corrected)
- [ ] Prompt adaptation is consistent across all four analysis modes
- [ ] Higher skill levels receive shorter, more technical responses; lower levels receive longer, more explanatory responses

### AC6: Execution Modes
- [ ] **Direct Claude API mode** (default): calls `POST /v1/messages` on the Anthropic API with configured model and max_tokens
- [ ] **Engine callback mode**: when `Engine:CallbackUrl` is configured, delegates analysis to the TypeScript engine's agent toolchain via `POST {callbackUrl}/api/engine/execute-task`
- [ ] **Mock mode**: when `Anthropic:UseMock=true`, returns simulated responses without API calls
- [ ] Mode selection priority: Mock > Callback > Direct API
- [ ] Each mode produces the same output shape (`ClaudeAnalysisOutput`)

### AC7: Retry and Error Handling
- [ ] Retry on HTTP 429 (Too Many Requests), 502, 503, 504 responses
- [ ] Maximum 3 retries (configurable)
- [ ] Retry delay respects `Retry-After` header when present
- [ ] Retry delay uses exponential backoff (5s, 10s, 15s) when no `Retry-After` header
- [ ] Log each retry attempt with status code, delay, and attempt number
- [ ] After max retries, return error output with `FallbackUsed = true`
- [ ] Non-retryable errors (400, 401, 403) fail immediately without retry
- [ ] JSON parse failures return error output with raw response preserved for debugging

### AC8: Response Parsing
- [ ] Parse Claude's JSON response into strongly-typed `ClaudeAnalysisOutput` fields
- [ ] Handle missing JSON fields gracefully (use defaults rather than crash)
- [ ] Handle malformed JSON (return error output with raw response)
- [ ] Validate parsed values are within expected ranges (e.g., confidence 0-1, score 0-100)
- [ ] Log parsing failures with the raw response for debugging

### AC9: Logging and Metrics
- [ ] Log analysis start with: sessionId, analysisType, skillLevel
- [ ] Log analysis completion with: sessionId, confidence, duration
- [ ] Log analysis errors with: sessionId, error details, raw response
- [ ] Record `mentorship_events` table entry with `EventType = AIAnalysis` for each analysis
- [ ] Emit metrics: `mentorship.ai.analysis.duration`, `mentorship.ai.analysis.success`, `mentorship.ai.analysis.fallback`

## Technical Design

### Analysis Activity Interface

```typescript
// TypeScript interface mirrors for the TS engine bridge
export interface ClaudeAnalysisRequest {
  sessionId: string;
  analysisType: AnalysisType;
  content: string;
  context?: string;
  skillLevel: number;
}

export type AnalysisType =
  | 'Assessment'
  | 'CodeReview'
  | 'BlockerDiagnosis'
  | 'GuidanceGeneration';

export interface ClaudeAnalysisOutput {
  success: boolean;
  analysisType: AnalysisType;
  confidence: number;
  rawResponse?: string;
  message?: string;
  fallbackUsed: boolean;

  // Assessment outputs
  assessmentStatus?: string;
  summary?: string;
  gaps: string[];
  strengths: string[];
  recommendedAction?: string;

  // Code Review outputs
  overallQuality?: string;
  score: number;
  codeReviewIssues: CodeReviewIssue[];
  positives: string[];
  learningOpportunities: string[];

  // Blocker Diagnosis outputs
  diagnosedBlockerType?: string;
  rootCause?: string;
  evidence: string[];
  recommendedIntervention?: string;
  immediateAction?: string;

  // Guidance Generation outputs
  mainGuidance?: string;
  steps: string[];
  examples: string[];
  socraticQuestions: string[];
  resources: string[];
  encouragement?: string;
}

export interface CodeReviewIssue {
  severity: 'Critical' | 'Major' | 'Minor' | 'Suggestion';
  location: string;
  issue: string;
  suggestion: string;
}
```

### Claude API Integration

```typescript
export interface IClaudeAnalysisActivity {
  // Main entry point
  analyze(request: ClaudeAnalysisRequest): Promise<ClaudeAnalysisOutput>;

  // Prompt construction
  buildSystemPrompt(analysisType: AnalysisType, skillLevel: number): string;
  buildUserPrompt(analysisType: AnalysisType, content: string, context?: string): string;

  // Response parsing
  parseResponse(rawResponse: string, analysisType: AnalysisType): ClaudeAnalysisOutput;
}

export interface ClaudeApiConfig {
  apiKey?: string;
  model: string;
  maxTokens: number;
  maxRetries: number;
  callbackUrl?: string;
  useMock: boolean;
}
```

## Dependencies

- Story 7-1: State machine core (analysis results drive state transitions)
- Story 7-3: Context Gathering Activity (provides context input for analysis)
- `Tamma.Core.Enums.AssessmentStatus` (already defined)
- `Tamma.Core.Enums.BlockerType` (already defined, 14 types)
- `Tamma.Data.Repositories.IMentorshipSessionRepository` (for event logging)
- Anthropic Claude API (claude-sonnet-4-20250514 default model, configurable)
- `IHttpClientFactory` for HTTP client management

## Testing Strategy

### Unit Tests
- System prompt generation for each analysis type at each skill level (5 levels x 4 types = 20 tests)
- User prompt generation for each analysis type
- Response parsing for well-formed JSON (each analysis type)
- Response parsing for malformed JSON (returns error output)
- Response parsing for missing fields (uses defaults)
- Confidence value clamping (values >1.0 clamped to 1.0, values <0 clamped to 0)
- Score value clamping (values >100 clamped to 100, values <0 clamped to 0)
- Skill level clamping (values outside 1-5 are corrected)
- Mock mode returns valid output for each analysis type
- Mode selection priority (Mock > Callback > Direct)

### Integration Tests
- Direct Claude API call with real API key (requires ANTHROPIC_API_KEY env var)
- Engine callback mode with running TS engine (requires Engine:CallbackUrl)
- Retry behavior on simulated 429 response
- Retry behavior on simulated 503 response
- Non-retryable error (401) fails immediately
- Full assessment analysis with real story context
- Full code review analysis with real code sample
- Database event logging after analysis

### Performance Tests
- Analysis latency <5 seconds for direct Claude API (p95)
- Mock mode latency <10ms
- JSON parsing <5ms for typical response

## Configuration

```yaml
mentorship:
  claude_analysis:
    model: "claude-sonnet-4-20250514"
    max_tokens: 4096
    max_retries: 3
    retry_base_delay_seconds: 5
    timeout_seconds: 30
    mock:
      enabled: false
    callback:
      url: null  # set to TS engine URL for callback mode
    prompts:
      temperature: 0.3  # lower for more consistent analysis
```

## Success Metrics

- Analysis accuracy: AI assessment/review findings validated by human review >80% agreement
- Response parsing success: >99% of Claude responses successfully parsed
- Retry effectiveness: >95% of retried requests eventually succeed
- Mock parity: mock responses structurally valid for all analysis types
- Latency: <5 seconds p95 for direct API, <10 seconds p95 for callback mode
- Fallback rate: <5% of analyses require fallback to mock
- Skill adaptation: responses at skill level 1 are measurably longer/simpler than at level 5

## Logging Requirements

All ELSA activities MUST inject `ILogger<T>` and log at these levels:

- **INFO**: Activity started (with session/issue ID), activity completed (with outcome), state transitions
- **DEBUG**: Input parameters received, intermediate LLM/API call details, decision rationale
- **WARN**: Retryable failures, timeout approaching, degraded quality gate result
- **ERROR**: Unrecoverable failures (with exception), invalid state transition, missing required data
- **Structured context**: Always include `{ sessionId, juniorId, storyId, currentState }` in all log entries
- **Sensitive data**: NEVER log student PII, credentials, or full LLM response content — log token counts and summary only
