# Story 5.8: Integration Testing Suite

**Epic**: Epic 5 - Observability & Production Readiness  
**Status**: Drafted  
**MVP Priority**: Critical (MVP Essential)  
**Estimated Complexity**: High  
**Target Implementation**: Sprint 5 (MVP Critical)

## Story

As a **QA engineer**,
I want comprehensive integration tests covering end-to-end autonomous loop scenarios,
so that regressions are caught before production deployment.

## Acceptance Criteria

### Integration Test Framework

- [ ] **Integration tests use real AI provider (mock mode) and mock Git platform API** for realistic testing without external dependencies
- [ ] **Test scenarios cover complete autonomous loop**: happy path (issue → plan → code → PR → merge), build failure with retry, test failure with escalation, ambiguous requirements with clarifying questions
- [ ] **Tests run in CI/CD pipeline on every PR** with automated execution and reporting
- [ ] **Tests validate event sequence correctness** ensuring all workflow steps emit proper events in correct order
- [ ] **Tests validate proper error handling** including retry logic, escalation triggers, and graceful degradation
- [ ] **Tests validate retry limits enforcement** ensuring infinite loops are prevented
- [ ] **Tests validate escalation triggering** when retry limits are exceeded or critical failures occur

### Test Coverage and Performance

- [ ] **Tests complete in <5 minutes for full suite** to maintain CI/CD pipeline efficiency
- [ ] **Test coverage report shows >80% code coverage** across all critical paths
- [ ] **Tests include assertions on event trail contents** verifying all events are captured with correct metadata
- [ ] **Tests validate database state consistency** after each workflow scenario
- [ ] **Tests validate API contract compliance** for all external service integrations

### Test Environment and Data

- [ ] **Isolated test environment** with dedicated test database and temporary workspaces
- [ ] **Test data fixtures** for common scenarios (sample issues, PR templates, test repositories)
- [ ] **Mock service responses** that accurately simulate real AI provider and Git platform behavior
- [ ] **Test cleanup procedures** ensuring no test artifacts pollute the environment

## Tasks / Subtasks

- [ ] **Task 1: Set up integration test framework** (AC: 1, 3)
  - [ ] Configure test environment with Jest/Vitest and test database
  - [ ] Set up mock AI provider service with realistic response patterns
  - [ ] Set up mock Git platform API with webhook simulation
  - [ ] Configure CI/CD pipeline integration with automated test execution

- [ ] **Task 2: Implement happy path test scenario** (AC: 2, 4, 7)
  - [ ] Create test for complete issue → plan → code → PR → merge workflow
  - [ ] Validate event sequence: IssueAssigned, PlanGenerated, CodeCreated, PRCreated, PRMerged
  - [ ] Validate database state changes at each workflow step
  - [ ] Validate final repository state after successful merge

- [ ] **Task 3: Implement failure scenario tests** (AC: 2, 5, 6)
  - [ ] Create test for build failure with retry logic
  - [ ] Validate retry limit enforcement and escalation triggering
  - [ ] Create test for test failure with escalation workflow
  - [ ] Validate error handling and recovery procedures

- [ ] **Task 4: Implement ambiguous requirements test** (AC: 2, 4)
  - [ ] Create test for scenarios requiring clarifying questions
  - [ ] Validate question generation and response handling
  - [ ] Validate workflow continuation after clarification

- [ ] **Task 5: Add event trail validation** (AC: 7)
  - [ ] Implement assertions for event metadata completeness
  - [ ] Validate correlation ID consistency across event sequences
  - [ ] Validate event timestamp ordering and causality

- [ ] **Task 6: Optimize test performance and coverage** (AC: 5, 6)
  - [ ] Implement test parallelization where possible
  - [ ] Optimize test data setup and cleanup procedures
  - [ ] Configure coverage reporting and coverage thresholds
  - [ ] Add performance benchmarks for test suite execution

## Dev Notes

### Current System State

From `docs/stories/5-1-structured-logging-implementation.md`:

- Structured logging with Pino provides correlation IDs for test validation
- Log levels and context capture enable comprehensive test assertions

From `docs/stories/5-2-metrics-collection-infrastructure.md`:

- Prometheus metrics collection provides test validation points
- Metrics endpoints can be queried to verify system behavior

From `docs/stories/4-1-event-schema-design.md`:

- Event schema definitions provide structure for event trail validation
- DCB event sourcing enables complete workflow state reconstruction

From `docs/stories/2-3-development-plan-generation-with-approval-checkpoint.md`:

- Development workflow with approval checkpoints provides test scenarios
- Issue and story management system integration points for testing

From `docs/stories/1-8-hybrid-orchestrator-worker-architecture-design.md`:

- Orchestrator-worker architecture provides test isolation boundaries
- Service interfaces enable mock implementation for testing

### Integration Testing Architecture

```typescript
// Integration testing architecture
┌─────────────────┐    ┌──────────────────┐    ┌─────────────────┐
│   Test Runner   │    │  Test           │    │  Mock Services  │
│   (Jest/Vitest) │◄──►│  Framework      │◄──►│  (AI/Git)       │
│                 │    │                  │    │                 │
│ - Test Suites   │    │ - Fixtures       │    │ - AI Responses  │
│ - Assertions    │    │ - Assertions     │    │ - Git API       │
│ - Cleanup       │    │ - Validation     │    │ - Webhooks      │
└─────────────────┘    └──────────────────┘    └─────────────────┘
          │                       │                       │
          │              ┌──────────────────┐              │
          └──────────────►│  Test Database  │◄─────────────┘
                         │  (PostgreSQL)   │
                         │                  │
                         │ - Test Data      │
                         │ - State Snapshots│
                         │ - Event Trail    │
                         └──────────────────┘
          │
          ▼
┌─────────────────┐    ┌──────────────────┐    ┌─────────────────┐
│   Event Trail   │    │  System Under   │    │  Validation     │
│   Validation    │    │  Test (SUT)     │    │  Assertions     │
│                 │    │                  │    │                 │
│ - Event Order   │    │ - Orchestrator   │    │ - Event Counts  │
│ - Metadata     │    │ - Workers        │    │ - State Changes │
│ - Correlations  │    │ - Gates          │    │ - API Calls     │
└─────────────────┘    └──────────────────┘    └─────────────────┘
```

### Technical Implementation

#### 1. Test Framework Setup

```typescript
// packages/integration-tests/src/framework/test-framework.ts
import { TestContainer } from 'testcontainers';
import { Logger } from 'pino';

export interface TestEnvironment {
  database: TestContainer;
  mockServices: MockServices;
  logger: Logger;
  testId: string;
}

export interface MockServices {
  aiProvider: MockAIProvider;
  gitPlatform: MockGitPlatform;
  notificationService: MockNotificationService;
}

export class IntegrationTestFramework {
  private environments: Map<string, TestEnvironment> = new Map();

  async setupTest(testName: string): Promise<TestEnvironment> {
    const testId = `test-${Date.now()}-${Math.random().toString(36).substr(2, 9)}`;

    // Start test database
    const database = await this.startTestDatabase();

    // Initialize mock services
    const mockServices = await this.setupMockServices();

    // Create test logger
    const logger = this.createTestLogger(testId);

    const environment: TestEnvironment = {
      database,
      mockServices,
      logger,
      testId,
    };

    this.environments.set(testId, environment);
    return environment;
  }

  async cleanupTest(testId: string): Promise<void> {
    const env = this.environments.get(testId);
    if (!env) return;

    // Stop database container
    await env.database.stop();

    // Cleanup mock services
    await this.cleanupMockServices(env.mockServices);

    this.environments.delete(testId);
  }

  private async startTestDatabase(): Promise<TestContainer> {
    // Start PostgreSQL container with test schema
    const container = await new TestContainer()
      .withImage('postgres:17')
      .withEnvironment({
        POSTGRES_DB: 'tamma_test',
        POSTGRES_USER: 'test',
        POSTGRES_PASSWORD: 'test',
      })
      .withExposedPorts(5432)
      .start();

    // Run migrations
    await this.runDatabaseMigrations(container);

    return container;
  }

  private async setupMockServices(): Promise<MockServices> {
    return {
      aiProvider: new MockAIProvider(),
      gitPlatform: new MockGitPlatform(),
      notificationService: new MockNotificationService(),
    };
  }

  private createTestLogger(testId: string): Logger {
    return pino({
      level: 'debug',
      base: { testId, service: 'integration-test' },
    });
  }
}
```

#### 2. Mock AI Provider

````typescript
// packages/integration-tests/src/mocks/mock-ai-provider.ts
import { EventEmitter } from 'events';
import { IAIProvider, MessageRequest, MessageChunk } from '@tamma/providers';

export class MockAIProvider extends EventEmitter implements IAIProvider {
  private responses: Map<string, MockResponse[]> = new Map();
  private callHistory: MessageRequest[] = [];

  async initialize(config: any): Promise<void> {
    // Initialize mock with predefined responses
    this.setupDefaultResponses();
  }

  async sendMessage(request: MessageRequest): Promise<AsyncIterable<MessageChunk>> {
    this.callHistory.push(request);

    const responseKey = this.generateResponseKey(request);
    const responses = this.responses.get(responseKey) || this.getDefaultResponse();

    async function* generateResponse() {
      for (const chunk of responses) {
        yield chunk;
        await new Promise((resolve) => setTimeout(resolve, chunk.delay || 100));
      }
    }

    return generateResponse();
  }

  getCapabilities(): any {
    return {
      maxTokens: 100000,
      supportedModels: ['claude-3-5-sonnet-20241022'],
      streaming: true,
    };
  }

  async dispose(): Promise<void> {
    this.callHistory = [];
    this.responses.clear();
  }

  // Test helper methods
  setResponse(key: string, responses: MockResponse[]): void {
    this.responses.set(key, responses);
  }

  getCallHistory(): MessageRequest[] {
    return [...this.callHistory];
  }

  clearCallHistory(): void {
    this.callHistory = [];
  }

  private setupDefaultResponses(): void {
    // Happy path responses
    this.setResponse('plan-generation', [
      { content: '{"status": "success", "plan": [...]}', delay: 500 },
    ]);

    this.setResponse('code-generation', [
      { content: '```typescript\n// Generated code\n```', delay: 1000 },
    ]);

    // Failure scenarios
    this.setResponse('build-failure', [
      { content: '{"status": "error", "message": "Build failed"}', delay: 300 },
    ]);

    this.setResponse('ambiguous-requirements', [
      { content: '{"status": "clarification_needed", "questions": [...]}', delay: 400 },
    ]);
  }

  private generateResponseKey(request: MessageRequest): string {
    // Generate key based on request content for mock response matching
    const content = request.messages
      .map((m) => m.content)
      .join(' ')
      .toLowerCase();

    if (content.includes('plan') || content.includes('development plan')) {
      return 'plan-generation';
    } else if (content.includes('code') || content.includes('implement')) {
      return 'code-generation';
    } else if (content.includes('build') || content.includes('compile')) {
      return 'build-failure';
    } else if (content.includes('unclear') || content.includes('ambiguous')) {
      return 'ambiguous-requirements';
    }

    return 'default';
  }

  private getDefaultResponse(): MockResponse[] {
    return [{ content: '{"status": "success", "response": "Mock response"}', delay: 200 }];
  }
}

interface MockResponse {
  content: string;
  delay?: number;
}
````

#### 3. Test Scenarios

```typescript
// packages/integration-tests/src/scenarios/happy-path.test.ts
import { IntegrationTestFramework } from '../framework/test-framework';
import { TestEnvironment } from '../framework/test-framework';

describe('Happy Path Integration Tests', () => {
  let framework: IntegrationTestFramework;
  let env: TestEnvironment;

  beforeAll(async () => {
    framework = new IntegrationTestFramework();
  });

  beforeEach(async () => {
    env = await framework.setupTest('happy-path');
  });

  afterEach(async () => {
    await framework.cleanupTest(env.testId);
  });

  afterAll(async () => {
    await framework.dispose();
  });

  it('should complete full autonomous loop successfully', async () => {
    // Arrange
    const testIssue = {
      id: 'test-issue-1',
      title: 'Add user authentication feature',
      description: 'Implement JWT-based authentication with login/logout',
      labels: ['feature', 'authentication'],
    };

    // Act
    const result = await env.mockServices.orchestrator.processIssue(testIssue);

    // Assert
    expect(result.status).toBe('success');
    expect(result.prUrl).toBeDefined();
    expect(result.mergeCommit).toBeDefined();

    // Validate event sequence
    const events = await env.database.queryEvents(env.testId);
    expect(events).toHaveLength(5); // IssueAssigned, PlanGenerated, CodeCreated, PRCreated, PRMerged
    expect(events[0].type).toBe('ISSUE.ASSIGNED.SUCCESS');
    expect(events[1].type).toBe('PLAN.GENERATED.SUCCESS');
    expect(events[2].type).toBe('CODE.GENERATED.SUCCESS');
    expect(events[3].type).toBe('PR.CREATED.SUCCESS');
    expect(events[4].type).toBe('PR.MERGED.SUCCESS');

    // Validate correlation ID consistency
    const correlationIds = events.map((e) => e.tags.correlationId);
    expect(new Set(correlationIds)).toHaveLength(1); // All same correlation ID

    // Validate final repository state
    const repoState = await env.mockServices.gitPlatform.getRepositoryState();
    expect(repoState.branches).toContain('feature/user-authentication');
    expect(repoState.commits).toHaveLength(1);
    expect(repoState.files).toContain('src/auth/jwt-auth.ts');
  });

  it('should handle multiple issues in sequence', async () => {
    // Arrange
    const issues = [
      { id: 'test-issue-1', title: 'Add login page', description: 'Create login UI' },
      { id: 'test-issue-2', title: 'Add logout functionality', description: 'Implement logout' },
    ];

    // Act
    const results = [];
    for (const issue of issues) {
      const result = await env.mockServices.orchestrator.processIssue(issue);
      results.push(result);
    }

    // Assert
    expect(results).toHaveLength(2);
    results.forEach((result) => {
      expect(result.status).toBe('success');
      expect(result.prUrl).toBeDefined();
    });

    // Validate event trails for both issues
    const events = await env.database.queryEvents(env.testId);
    expect(events).toHaveLength(10); // 5 events per issue

    // Validate correlation ID separation
    const correlationIds = events.map((e) => e.tags.correlationId);
    expect(new Set(correlationIds)).toHaveLength(2); // Two different correlation IDs
  });
});
```

#### 4. Failure Scenario Tests

````typescript
// packages/integration-tests/src/scenarios/failure-scenarios.test.ts
import { IntegrationTestFramework } from '../framework/test-framework';

describe('Failure Scenario Integration Tests', () => {
  let framework: IntegrationTestFramework;
  let env: TestEnvironment;

  beforeEach(async () => {
    env = await framework.setupTest('failure-scenarios');
  });

  afterEach(async () => {
    await framework.cleanupTest(env.testId);
  });

  it('should handle build failure with retry and escalation', async () => {
    // Arrange
    env.mockServices.aiProvider.setResponse('code-generation', [
      { content: '```typescript\n// Code with syntax error\n```', delay: 1000 },
    ]);

    const testIssue = {
      id: 'test-build-failure',
      title: 'Fix syntax error',
      description: 'Code has syntax error that needs fixing',
    };

    // Act
    const result = await env.mockServices.orchestrator.processIssue(testIssue);

    // Assert
    expect(result.status).toBe('escalated');
    expect(result.escalationReason).toContain('Build failed after 3 retries');

    // Validate retry events
    const events = await env.database.queryEvents(env.testId);
    const buildEvents = events.filter((e) => e.type.includes('BUILD'));
    expect(buildEvents).toHaveLength(3); // 3 retry attempts

    // Validate escalation event
    const escalationEvents = events.filter((e) => e.type.includes('ESCALATION'));
    expect(escalationEvents).toHaveLength(1);
    expect(escalationEvents[0].type).toBe('ESCALATION.TRIGGERED.SUCCESS');
  });

  it('should handle test failure with escalation', async () => {
    // Arrange
    env.mockServices.aiProvider.setResponse('code-generation', [
      { content: '```typescript\n// Code that fails tests\n```', delay: 1000 },
    ]);

    const testIssue = {
      id: 'test-test-failure',
      title: 'Fix failing test',
      description: 'Tests are failing and need to be fixed',
    };

    // Act
    const result = await env.mockServices.orchestrator.processIssue(testIssue);

    // Assert
    expect(result.status).toBe('escalated');
    expect(result.escalationReason).toContain('Test failure after code generation');

    // Validate test execution events
    const events = await env.database.queryEvents(env.testId);
    const testEvents = events.filter((e) => e.type.includes('TEST'));
    expect(testEvents.length).toBeGreaterThan(0);

    // Validate escalation includes test failure details
    const escalationEvents = events.filter((e) => e.type.includes('ESCALATION'));
    expect(escalationEvents[0].data.failureReason).toContain('test');
  });
});
````

### Project Structure Notes

- Integration tests in `packages/integration-tests/src/` following monorepo structure
- Test framework reuses existing packages: `@tamma/orchestrator`, `@tamma/providers`, `@tamma/platforms`
- Mock services implement same interfaces as real services for consistency
- Test database uses PostgreSQL with same schema as production
- CI/CD integration in `.github/workflows/integration-tests.yml`

### References

- [Source: docs/tech-spec-epic-5.md#Integration-Testing-Suite]
- [Source: docs/epics.md#Story-5.8-Integration-Testing-Suite]
- [Source: docs/architecture.md#Testing-Strategy]
- [Source: docs/stories/5-1-structured-logging-implementation.md]
- [Source: docs/stories/5-2-metrics-collection-infrastructure.md]
- [Source: docs/stories/4-1-event-schema-design.md]

## Dev Agent Record

### Context Reference

<!-- Path(s) to story context XML will be added here by context workflow -->

### Agent Model Used

claude-3-5-sonnet-20241022

### Debug Log References

### Completion Notes List

### File List
