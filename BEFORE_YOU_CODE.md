# Before You Code - Process Guide for AI Agents & Developers

## 🎯 Purpose

This document defines the mandatory process that ALL contributors (human and AI) must follow before writing ANY code for the Tamma project.

## 📋 Pre-Coding Checklist

### 1. ✅ Understand the Context

**Read These Documents (in order):**
1. `CLAUDE.md` - Project guidelines and architecture principles
2. `docs/architecture.md` - Complete technical architecture
3. `docs/epics.md` - Epic breakdown and story overview
4. Relevant story file in `docs/stories/` - Your specific task

**Answer These Questions:**
- What problem am I solving?
- Which epic and story does this belong to?
- What are the acceptance criteria?
- What dependencies does this have on other packages?

### 2. 🔍 Check Existing Documentation

**Before starting, search these locations:**

```bash
# Check if similar work exists
grep -r "your-feature-name" .dev/spikes/
grep -r "your-feature-name" .dev/bugs/
grep -r "your-feature-name" .dev/findings/

# Check if there's a design decision recorded
ls .dev/decisions/ | grep -i "your-feature-area"

# Check existing code patterns
grep -r "similar-pattern" packages/
```

**Look for:**
- ✅ Existing spikes on this topic
- ✅ Known bugs or limitations
- ✅ Design decisions that affect your work
- ✅ Similar implementations in other packages

### 3. 🧪 Spike First (If Needed)

**When to create a spike:**
- Unfamiliar technology or pattern
- Multiple implementation approaches possible
- Performance or security concerns
- Integration with external services

**Spike Process:**
1. Create spike document: `.dev/spikes/YYYY-MM-DD-spike-name.md`
2. Use template: `.dev/templates/spike-template.md`
3. Research and document findings
4. Include code samples and benchmarks
5. Make recommendation
6. Save spike before implementing

### 4. 📝 Document Design Decisions

**When to create a design decision:**
- Choosing between multiple approaches
- Architectural impact
- Changes to existing patterns
- Trade-offs involved

**Process:**
1. Create decision document: `.dev/decisions/YYYY-MM-DD-decision-name.md`
2. Use template: `.dev/templates/decision-template.md`
3. Document context, options, decision, and consequences

### 5. 🏗️ Plan Your Implementation

**Create a mental (or written) checklist:**
- [ ] Which package(s) will I modify?
- [ ] What interfaces do I need to define?
- [ ] What tests do I need to write first? (TDD)
- [ ] What error handling is required?
- [ ] What logging should I add?
- [ ] What events should I emit?

### 6. 🧪 Test-First Development

**ALWAYS write tests BEFORE implementation:**

```bash
# 1. Create test file
packages/your-package/src/your-feature.test.ts

# 2. Write failing test
# 3. Run test (should fail)
pnpm test

# 4. Implement minimal code to pass test
# 5. Run test (should pass)
# 6. Refactor if needed
```

### 7. 🚀 Ready to Code!

**Now you can start coding, following:**
- TypeScript strict mode guidelines
- Naming conventions from CLAUDE.md
- Error handling patterns
- Logging standards
- Event emission requirements

---

## 🔄 Development Process Workflow (MANDATORY)

This is the **MANDATORY** step-by-step process for implementing ANY feature or fix. Follow this religiously.

### Phase 1: 📚 Read & Research

#### Step 1.1: Read Documentation
```bash
# Read in this exact order:
1. CLAUDE.md
2. docs/architecture.md
3. docs/epics.md
4. Your story file: docs/stories/X-Y-story-name.md
```

**Checklist:**
- [ ] Understand the problem you're solving
- [ ] Know which epic/story this belongs to
- [ ] Clear on acceptance criteria
- [ ] Identified all dependencies

#### Step 1.2: Research Existing Knowledge
```bash
# Search for existing research
grep -r "your-feature" .dev/spikes/
grep -r "your-feature" .dev/findings/
grep -r "your-feature" .dev/decisions/

# Check for known issues
grep -r "your-feature" .dev/bugs/
```

**If research exists**: Read it and build upon it
**If no research exists**: Create spike (see Section 3 above)

#### Step 1.3: Analyze Existing Code
```bash
# Find similar patterns in codebase
grep -r "similar-interface" packages/
grep -r "similar-pattern" packages/

# Check how it's done elsewhere
find packages/ -name "*similar-feature*.ts"

# Look at test patterns
find packages/ -name "*.test.ts" | head -5
cat packages/some-package/src/example.test.ts
```

**Document findings:**
- What patterns are already in use?
- What conventions exist?
- What can you reuse?
- What should you NOT do (anti-patterns)?

### Phase 2: 📋 Break Down & Plan

#### Step 2.1: Break Feature into Tasks

**Create a task list** (use TodoWrite or markdown):

```markdown
## Feature: [Name]

### Tasks:
1. [ ] Define interfaces/types
2. [ ] Implement core functionality
3. [ ] Add error handling
4. [ ] Add logging/tracing
5. [ ] Emit events
6. [ ] Write tests (TDD)
7. [ ] Integration testing
8. [ ] Documentation
```

#### Step 2.2: Think TDD First

**For EACH task, ask:**
- What behavior do I need to test?
- What edge cases exist?
- What error conditions can occur?
- What should the happy path return?

**Create test outline:**
```typescript
// packages/your-package/src/feature.test.ts

describe('FeatureName', () => {
  describe('happy path', () => {
    it('should handle normal case');
    it('should return expected format');
  });

  describe('edge cases', () => {
    it('should handle empty input');
    it('should handle null/undefined');
    it('should handle boundary values');
  });

  describe('error cases', () => {
    it('should throw on invalid input');
    it('should handle network errors');
    it('should handle timeout');
  });

  describe('integration', () => {
    it('should work with other components');
  });
});
```

### Phase 3: 🧪 TDD Implementation Loop

**For EACH task, follow the Red-Green-Refactor cycle:**

#### Step 3.1: 🔴 RED - Write Failing Test

```bash
# 1. Create/update test file
vim packages/your-package/src/feature.test.ts
```

```typescript
// Write ONE failing test
describe('FeatureName', () => {
  it('should do X', async () => {
    // Arrange
    const input = createTestInput();
    const feature = new FeatureName();

    // Act
    const result = await feature.doSomething(input);

    // Assert
    expect(result).toEqual(expectedOutput);
    expect(result.status).toBe('success');
  });
});
```

```bash
# 2. Run test - MUST FAIL
pnpm test packages/your-package/src/feature.test.ts

# Expected: Test FAILS (good! this proves test works)
```

**If test passes**: Your test is wrong! Fix the test.
**If test fails**: Good! Proceed to GREEN.

#### Step 3.2: 🟢 GREEN - Make Test Pass

```typescript
// Write MINIMAL code to pass test
export class FeatureName {
  async doSomething(input: Input): Promise<Output> {
    // Minimal implementation
    return {
      status: 'success',
      data: processInput(input)
    };
  }
}
```

```bash
# Run test - MUST PASS
pnpm test packages/your-package/src/feature.test.ts

# Expected: Test PASSES
```

**If test fails**: Debug and fix until it passes.
**If test passes**: Good! Proceed to REFACTOR.

#### Step 3.3: 🔵 REFACTOR - Improve Code

**Now improve code quality:**

1. **Add Error Handling:**
```typescript
export class FeatureName {
  async doSomething(input: Input): Promise<Output> {
    try {
      // Validate input
      if (!input) {
        throw new TammaError(
          'INVALID_INPUT',
          'Input is required',
          { input },
          false, // not retryable
          'high' // severity
        );
      }

      // Process
      const result = await processInput(input);

      // Validate output
      if (!result) {
        throw new TammaError(
          'PROCESSING_FAILED',
          'Failed to process input',
          { input },
          true, // retryable
          'medium'
        );
      }

      return {
        status: 'success',
        data: result
      };
    } catch (error) {
      // Log error (see Phase 4)
      logger.error('Feature failed', { error, input });
      throw error;
    }
  }
}
```

2. **Add Logging/Tracing:**
```typescript
export class FeatureName {
  async doSomething(input: Input): Promise<Output> {
    const startTime = Date.now();
    const traceId = generateTraceId();

    logger.info('Feature started', {
      traceId,
      operation: 'doSomething',
      input: sanitizeForLogging(input)
    });

    try {
      const result = await processInput(input);

      const duration = Date.now() - startTime;
      logger.info('Feature succeeded', {
        traceId,
        duration,
        operation: 'doSomething',
        status: 'success'
      });

      return { status: 'success', data: result };
    } catch (error) {
      const duration = Date.now() - startTime;
      logger.error('Feature failed', {
        traceId,
        duration,
        operation: 'doSomething',
        error: {
          message: error.message,
          code: error.code,
          stack: error.stack
        },
        input: sanitizeForLogging(input)
      });

      throw error;
    }
  }
}
```

3. **Add Event Emission:**
```typescript
export class FeatureName {
  constructor(
    private eventStore: IEventStore,
    private logger: ILogger
  ) {}

  async doSomething(input: Input): Promise<Output> {
    const traceId = generateTraceId();

    try {
      const result = await processInput(input);

      // Emit success event
      await this.eventStore.append({
        type: 'FEATURE.EXECUTED.SUCCESS',
        tags: {
          traceId,
          userId: input.userId,
          operation: 'doSomething'
        },
        metadata: {
          workflowVersion: '1.0.0',
          eventSource: 'system'
        },
        data: {
          duration: result.duration,
          itemsProcessed: result.count
        }
      });

      return { status: 'success', data: result };
    } catch (error) {
      // Emit failure event
      await this.eventStore.append({
        type: 'FEATURE.EXECUTED.FAILED',
        tags: {
          traceId,
          userId: input.userId,
          operation: 'doSomething'
        },
        metadata: {
          workflowVersion: '1.0.0',
          eventSource: 'system'
        },
        data: {
          errorCode: error.code,
          errorMessage: error.message,
          retryable: error.retryable
        }
      });

      throw error;
    }
  }
}
```

```bash
# Run tests after refactoring
pnpm test packages/your-package/src/feature.test.ts

# Expected: Tests still PASS
```

**If tests fail after refactor**: You broke something! Revert and refactor more carefully.

#### Step 3.4: Repeat for Each Test Case

```bash
# Repeat RED-GREEN-REFACTOR for:
1. Happy path tests
2. Edge case tests
3. Error handling tests
4. Integration tests
```

### Phase 4: 📊 Logging & Tracing Requirements

#### Logging Levels

**Use appropriate log levels:**

```typescript
// TRACE - Function-level execution flow (for AI agents & deep debugging)
logger.trace('→ ENTER function', { function: 'processData', params: { id, options } });
logger.trace('← EXIT function', { function: 'processData', result, duration: 5 });

// DEBUG - Verbose details for development
logger.debug('Processing item', { itemId, details });

// INFO - Key milestones and success paths
logger.info('User authenticated', { userId, method: 'oauth' });

// WARN - Recoverable issues, degraded mode
logger.warn('API rate limit approaching', {
  current: 980,
  limit: 1000,
  resetAt: timestamp
});

// ERROR - Failures requiring attention
logger.error('Database connection failed', {
  error,
  retryAttempt: 3,
  maxRetries: 5
});
```

**Log Level Hierarchy** (most verbose → least verbose):
```
TRACE → DEBUG → INFO → WARN → ERROR
```

**When to use each level:**
- **TRACE**: Function entry/exit, parameter values, return values, execution path
- **DEBUG**: Step-by-step logic, variable states, internal operations
- **INFO**: User actions, workflow milestones, successful operations
- **WARN**: Degraded mode, retries, approaching limits
- **ERROR**: Failures, exceptions, critical issues

#### Structured Logging Format

**ALWAYS use structured JSON:**

```typescript
// ❌ BAD: String interpolation
logger.info(`User ${userId} performed action ${action}`);

// ✅ GOOD: Structured data
logger.info('User action performed', {
  userId,
  action,
  timestamp: dayjs.utc().toISOString(),
  traceId,
  metadata: {
    ipAddress: req.ip,
    userAgent: req.headers['user-agent']
  }
});
```

#### Trace ID Pattern

**Every operation needs a trace ID:**

```typescript
import { v4 as uuidv4 } from 'uuid';

function generateTraceId(): string {
  return `trace-${uuidv4()}`;
}

// Use trace ID consistently
const traceId = generateTraceId();

logger.info('Operation started', { traceId, operation: 'createUser' });
// ... do work ...
logger.info('Operation completed', { traceId, duration: 123 });
```

#### Log Destinations

**Logs must go to files AND/OR cloud:**

```typescript
// packages/observability/src/logger.ts
import pino from 'pino';

const logger = pino({
  level: process.env.LOG_LEVEL || 'info',
  transport: {
    targets: [
      // Console (development with TRACE)
      {
        target: 'pino-pretty',
        options: {
          colorize: true,
          translateTime: 'HH:MM:ss Z',
          ignore: 'pid,hostname'
        },
        level: process.env.NODE_ENV === 'development' ? 'trace' : 'debug'
      },
      // File - Trace logs (development only)
      {
        target: 'pino/file',
        options: {
          destination: './logs/trace.log',
          mkdir: true
        },
        level: 'trace'
      },
      // File - Application logs (all environments)
      {
        target: 'pino/file',
        options: {
          destination: './logs/app.log',
          mkdir: true
        },
        level: 'info'
      },
      // Cloud (optional - AWS CloudWatch, Datadog, etc.)
      {
        target: 'pino-cloudwatch',
        options: {
          logGroupName: '/tamma/production',
          logStreamName: process.env.INSTANCE_ID
        },
        level: 'warn'
      }
    ]
  }
});
```

**Note**: TRACE logs go to separate `trace.log` file to avoid cluttering main logs.

#### Function-Level Tracing (MANDATORY)

**EVERY function MUST have TRACE logs for entry/exit:**

```typescript
// Helper function for automatic function tracing
function tracedFunction<T extends (...args: any[]) => any>(
  fn: T,
  functionName: string
): T {
  return (async (...args: Parameters<T>): Promise<ReturnType<T>> => {
    const startTime = Date.now();
    const traceId = generateTraceId();

    // TRACE: Function entry
    logger.trace('→ ENTER', {
      traceId,
      function: functionName,
      params: sanitizeForLogging(args),
      timestamp: dayjs.utc().toISOString()
    });

    try {
      const result = await fn(...args);
      const duration = Date.now() - startTime;

      // TRACE: Function exit (success)
      logger.trace('← EXIT', {
        traceId,
        function: functionName,
        result: sanitizeForLogging(result),
        duration,
        status: 'success'
      });

      return result;
    } catch (error) {
      const duration = Date.now() - startTime;

      // TRACE: Function exit (error)
      logger.trace('← EXIT', {
        traceId,
        function: functionName,
        error: {
          message: error.message,
          code: error.code
        },
        duration,
        status: 'error'
      });

      throw error;
    }
  }) as T;
}
```

**Manual Function Tracing Pattern:**

```typescript
async function processData(input: Input): Promise<Output> {
  const traceId = generateTraceId();
  const startTime = Date.now();

  // TRACE: Function entry
  logger.trace('→ ENTER processData', {
    traceId,
    function: 'processData',
    params: { input: sanitizeForLogging(input) }
  });

  try {
    // Your function logic here
    const result = await doWork(input);

    const duration = Date.now() - startTime;

    // TRACE: Function exit
    logger.trace('← EXIT processData', {
      traceId,
      function: 'processData',
      result: sanitizeForLogging(result),
      duration,
      status: 'success'
    });

    return result;
  } catch (error) {
    const duration = Date.now() - startTime;

    // TRACE: Function exit with error
    logger.trace('← EXIT processData', {
      traceId,
      function: 'processData',
      error: { message: error.message, code: error.code },
      duration,
      status: 'error'
    });

    throw error;
  }
}
```

#### Debug Tracing (High-Level Flow)

**DEBUG shows step-by-step progress:**

```typescript
async function complexOperation(input: Input): Promise<Output> {
  const traceId = generateTraceId();

  logger.info('🟢 START: Complex operation', { traceId, input });

  try {
    // Step 1
    logger.debug('📍 STEP 1: Validate input', { traceId });
    const validated = await validateInput(input);
    logger.debug('✅ STEP 1: Complete', { traceId, validated });

    // Step 2
    logger.debug('📍 STEP 2: Process data', { traceId });
    const processed = await processData(validated);
    logger.debug('✅ STEP 2: Complete', { traceId, processed });

    // Step 3
    logger.debug('📍 STEP 3: Save results', { traceId });
    const saved = await saveResults(processed);
    logger.debug('✅ STEP 3: Complete', { traceId, saved });

    logger.info('🟢 SUCCESS: Complex operation', {
      traceId,
      duration: calculateDuration(),
      result: saved
    });

    return saved;
  } catch (error) {
    logger.error('🔴 FAILED: Complex operation', {
      traceId,
      error,
      failedAt: error.step || 'unknown',
      duration: calculateDuration()
    });

    throw error;
  }
}
```

#### Complete Example with TRACE + DEBUG

**Shows complete execution flow for AI agents:**

```typescript
// Example: User authentication flow with full tracing

async function authenticateUser(
  username: string,
  password: string
): Promise<AuthResult> {
  const traceId = generateTraceId();
  const startTime = Date.now();

  // TRACE: Function entry
  logger.trace('→ ENTER authenticateUser', {
    traceId,
    function: 'authenticateUser',
    params: { username, password: '***REDACTED***' }
  });

  logger.info('🟢 START: User authentication', { traceId, username });

  try {
    // Step 1: Validate credentials
    logger.debug('📍 STEP 1: Validate credentials format', { traceId });
    logger.trace('→ ENTER validateCredentials', {
      traceId,
      function: 'validateCredentials',
      params: { username }
    });

    const isValid = await validateCredentials(username, password);

    logger.trace('← EXIT validateCredentials', {
      traceId,
      function: 'validateCredentials',
      result: { isValid },
      duration: 5
    });

    if (!isValid) {
      throw new TammaError('INVALID_CREDENTIALS', 'Invalid username or password');
    }
    logger.debug('✅ STEP 1: Credentials valid', { traceId });

    // Step 2: Check user exists
    logger.debug('📍 STEP 2: Lookup user in database', { traceId });
    logger.trace('→ ENTER findUserByUsername', {
      traceId,
      function: 'findUserByUsername',
      params: { username }
    });

    const user = await findUserByUsername(username);

    logger.trace('← EXIT findUserByUsername', {
      traceId,
      function: 'findUserByUsername',
      result: { userId: user.id, found: true },
      duration: 12
    });

    logger.debug('✅ STEP 2: User found', { traceId, userId: user.id });

    // Step 3: Verify password
    logger.debug('📍 STEP 3: Verify password hash', { traceId });
    logger.trace('→ ENTER verifyPassword', {
      traceId,
      function: 'verifyPassword',
      params: { userId: user.id }
    });

    const passwordMatch = await verifyPassword(password, user.passwordHash);

    logger.trace('← EXIT verifyPassword', {
      traceId,
      function: 'verifyPassword',
      result: { match: passwordMatch },
      duration: 45
    });

    if (!passwordMatch) {
      throw new TammaError('INVALID_PASSWORD', 'Password does not match');
    }
    logger.debug('✅ STEP 3: Password verified', { traceId });

    // Step 4: Generate session token
    logger.debug('📍 STEP 4: Generate session token', { traceId });
    logger.trace('→ ENTER generateSessionToken', {
      traceId,
      function: 'generateSessionToken',
      params: { userId: user.id }
    });

    const token = await generateSessionToken(user.id);

    logger.trace('← EXIT generateSessionToken', {
      traceId,
      function: 'generateSessionToken',
      result: { tokenGenerated: true },
      duration: 8
    });

    logger.debug('✅ STEP 4: Token generated', { traceId });

    const duration = Date.now() - startTime;
    const result = { user, token, authenticated: true };

    logger.info('🟢 SUCCESS: User authenticated', {
      traceId,
      userId: user.id,
      duration
    });

    // TRACE: Function exit
    logger.trace('← EXIT authenticateUser', {
      traceId,
      function: 'authenticateUser',
      result: { authenticated: true, userId: user.id },
      duration,
      status: 'success'
    });

    return result;
  } catch (error) {
    const duration = Date.now() - startTime;

    logger.error('🔴 FAILED: User authentication', {
      traceId,
      error,
      username,
      duration
    });

    // TRACE: Function exit with error
    logger.trace('← EXIT authenticateUser', {
      traceId,
      function: 'authenticateUser',
      error: { message: error.message, code: error.code },
      duration,
      status: 'error'
    });

    throw error;
  }
}
```

**TRACE Output Example:**

```
[12:34:56.001] TRACE → ENTER authenticateUser | traceId=trace-abc123 | params={"username":"john","password":"***REDACTED***"}
[12:34:56.002] INFO  🟢 START: User authentication | traceId=trace-abc123
[12:34:56.003] DEBUG 📍 STEP 1: Validate credentials format | traceId=trace-abc123
[12:34:56.003] TRACE → ENTER validateCredentials | traceId=trace-abc123
[12:34:56.008] TRACE ← EXIT validateCredentials | result={"isValid":true} | duration=5ms
[12:34:56.008] DEBUG ✅ STEP 1: Credentials valid | traceId=trace-abc123
[12:34:56.009] DEBUG 📍 STEP 2: Lookup user in database | traceId=trace-abc123
[12:34:56.009] TRACE → ENTER findUserByUsername | params={"username":"john"}
[12:34:56.021] TRACE ← EXIT findUserByUsername | result={"userId":"user-123","found":true} | duration=12ms
[12:34:56.021] DEBUG ✅ STEP 2: User found | userId=user-123
[12:34:56.022] DEBUG 📍 STEP 3: Verify password hash | traceId=trace-abc123
[12:34:56.022] TRACE → ENTER verifyPassword | params={"userId":"user-123"}
[12:34:56.067] TRACE ← EXIT verifyPassword | result={"match":true} | duration=45ms
[12:34:56.067] DEBUG ✅ STEP 3: Password verified | traceId=trace-abc123
[12:34:56.068] DEBUG 📍 STEP 4: Generate session token | traceId=trace-abc123
[12:34:56.068] TRACE → ENTER generateSessionToken | params={"userId":"user-123"}
[12:34:56.076] TRACE ← EXIT generateSessionToken | result={"tokenGenerated":true} | duration=8ms
[12:34:56.076] DEBUG ✅ STEP 4: Token generated | traceId=trace-abc123
[12:34:56.077] INFO  🟢 SUCCESS: User authenticated | userId=user-123 | duration=76ms
[12:34:56.077] TRACE ← EXIT authenticateUser | result={"authenticated":true,"userId":"user-123"} | duration=76ms | status=success
```

**Benefits of TRACE logs:**
- 🤖 AI agents can follow exact execution path
- 🔍 See which function called which function
- ⏱️ Measure performance of each function
- 📊 Understand parameter flow through the system
- 🐛 Debug issues by seeing complete call stack
- 📈 Identify bottlenecks (slow functions)

### Phase 5: ✅ Quality Gates

#### Gate 1: Build Must Succeed

```bash
# Build the package
pnpm build --filter @tamma/your-package

# Expected: Build succeeds with NO errors
# If build fails: Fix TypeScript errors, then retry
```

**Build Failure Protocol:**
1. Read error messages carefully
2. Fix TypeScript strict mode violations
3. Fix import/export issues
4. Retry build
5. If still failing after 3 attempts: Create bug report and alert dev

#### Gate 2: 100% Test Coverage Required

```bash
# Run tests with coverage
pnpm test:coverage --filter @tamma/your-package

# Check coverage report
open coverage/index.html
```

**Coverage Requirements:**
- Line Coverage: **100%** (no exceptions)
- Branch Coverage: **100%** (test all if/else paths)
- Function Coverage: **100%** (test all functions)
- Statement Coverage: **100%** (test all statements)

**Coverage Failure Protocol:**

```typescript
// ❌ FAILING: Uncovered code
function processData(input: string): string {
  if (input) {
    return input.toUpperCase(); // ✅ Covered
  }
  return ''; // ❌ NOT COVERED - need test for this!
}

// ✅ PASSING: All branches covered
describe('processData', () => {
  it('should uppercase when input provided', () => {
    expect(processData('hello')).toBe('HELLO');
  });

  it('should return empty string when no input', () => {
    expect(processData('')).toBe('');
  });
});
```

#### Gate 3: Integration Tests

```bash
# Run integration tests (requires real services)
pnpm test:integration --filter @tamma/your-package

# Expected: All integration tests pass
```

**Integration Test Example:**

```typescript
// feature.integration.test.ts
describe('Feature Integration', () => {
  it('should work with real database', async () => {
    const db = await createTestDatabase();
    const feature = new Feature(db);

    const result = await feature.doSomething({ data: 'test' });

    expect(result.status).toBe('success');
    expect(await db.query('SELECT * FROM results')).toHaveLength(1);

    await db.cleanup();
  });

  it('should work with real AI provider', async () => {
    const provider = new AnthropicProvider({
      apiKey: process.env.ANTHROPIC_API_KEY_TEST
    });

    const result = await provider.sendMessage({ content: 'test' });

    expect(result).toBeDefined();
  });
});
```

### Phase 6: 🚨 Failure Handling

#### Automatic Retry Logic

```typescript
async function runTestsWithRetry(
  maxAttempts: number = 3
): Promise<TestResult> {
  let attempt = 0;

  while (attempt < maxAttempts) {
    attempt++;

    logger.info('Running tests', { attempt, maxAttempts });

    try {
      const result = await runTests();

      if (result.success) {
        logger.info('Tests passed', { attempt });
        return result;
      }

      logger.warn('Tests failed, retrying', {
        attempt,
        failures: result.failures
      });
    } catch (error) {
      logger.error('Test execution error', { attempt, error });

      if (attempt >= maxAttempts) {
        // Alert developer
        await alertDeveloper({
          type: 'TEST_FAILURE',
          message: 'Tests failed after maximum retries',
          attempts: maxAttempts,
          error
        });

        throw new TammaError(
          'TEST_EXECUTION_FAILED',
          `Tests failed after ${maxAttempts} attempts`,
          { error, attempts: maxAttempts },
          false,
          'critical'
        );
      }

      // Wait before retry
      await sleep(1000 * attempt);
    }
  }

  throw new Error('Unreachable');
}
```

#### Alert Developer Protocol

```typescript
async function alertDeveloper(alert: Alert): Promise<void> {
  const message = `
🚨 ALERT: ${alert.type}

Message: ${alert.message}
Attempts: ${alert.attempts}
Error: ${alert.error?.message}
Stack: ${alert.error?.stack}

Trace ID: ${alert.traceId}
Timestamp: ${dayjs.utc().toISOString()}

Please investigate immediately.
  `;

  // Log to file
  logger.error('Developer alert', alert);

  // Send notification (email, Slack, PagerDuty, etc.)
  await notificationService.send({
    channel: 'critical-alerts',
    message,
    priority: 'high'
  });

  // Create GitHub issue automatically
  await github.issues.create({
    title: `🚨 ${alert.type}: ${alert.message}`,
    body: message,
    labels: ['bug', 'critical', 'auto-generated']
  });
}
```

### Phase 7: 📝 Final Checklist

**Before marking task complete:**

- [ ] ✅ All tests pass (100% coverage)
- [ ] ✅ Build succeeds with no errors
- [ ] ✅ Integration tests pass
- [ ] ✅ Error handling implemented
- [ ] ✅ Logging/tracing added
- [ ] ✅ Events emitted for audit trail
- [ ] ✅ Code follows TypeScript strict mode
- [ ] ✅ Code follows naming conventions
- [ ] ✅ Documentation updated
- [ ] ✅ Findings document created (if applicable)
- [ ] ✅ No console.log statements (use logger)
- [ ] ✅ No commented-out code
- [ ] ✅ No TODO comments without GitHub issues

### Quick Reference: TDD Cycle

```
┌─────────────────────────────────────┐
│  🔴 RED: Write failing test         │
│  ↓                                  │
│  🟢 GREEN: Make test pass           │
│  ↓                                  │
│  🔵 REFACTOR: Improve code          │
│  ↓                                  │
│  ✅ Verify tests still pass         │
│  ↓                                  │
│  🔁 Repeat for next test            │
└─────────────────────────────────────┘
```

---

## 🐛 Found a Bug?

**STOP and document it first:**

```bash
# Create bug report
.dev/bugs/YYYY-MM-DD-bug-name.md
```

**Use template:** `.dev/templates/bug-template.md`

**Then:**
- Add bug to GitHub Issues
- Link bug report to issue
- Fix the bug
- Update bug report with resolution

## 💡 Discovered Something Important?

**Document your findings:**

```bash
# Create finding document
.dev/findings/YYYY-MM-DD-finding-name.md
```

**Types of findings:**
- ⚠️ Pitfalls - Things that don't work or cause issues
- 🚨 Known Issues - Bugs or limitations we're aware of
- 📚 Lessons Learned - What worked well or poorly
- 🔍 Best Practices - Patterns that should be followed
- ⚡ Performance Notes - Optimization discoveries

## 🚫 Anti-Patterns (DON'T DO THIS)

### ❌ Coding First, Thinking Later
```bash
# WRONG: Jump straight into coding
echo "export function foo() {}" > packages/shared/src/foo.ts
```

### ❌ Ignoring Existing Documentation
```bash
# WRONG: Not checking if someone already researched this
grep -r "my-feature" .dev/  # <- You should do this FIRST!
```

### ❌ No Tests
```bash
# WRONG: Implementing without tests
# RIGHT: Write test first, then implementation
```

### ❌ No Error Handling
```typescript
// WRONG: No error handling
async function fetchUser(id: string) {
  const user = await db.query('SELECT * FROM users WHERE id = ?', [id]);
  return user;
}

// RIGHT: Proper error handling with logging
async function fetchUser(id: string): Promise<User> {
  try {
    const user = await db.query('SELECT * FROM users WHERE id = ?', [id]);
    if (!user) {
      throw new TammaError('USER_NOT_FOUND', 'User not found', { userId: id });
    }
    return user;
  } catch (error) {
    logger.error('Failed to fetch user', { error, userId: id });
    throw error;
  }
}
```

### ❌ No Event Emission
```typescript
// WRONG: Not emitting events for audit trail
async function createPR(data: PRData) {
  return await github.createPullRequest(data);
}

// RIGHT: Emit events for DCB event sourcing
async function createPR(data: PRData) {
  try {
    const pr = await github.createPullRequest(data);
    await eventStore.append({
      type: 'PR.CREATED.SUCCESS',
      tags: { issueId: data.issueId, prId: pr.id },
      metadata: { workflowVersion: '1.0.0', eventSource: 'system' },
      data: { prUrl: pr.url, branch: data.branch }
    });
    return pr;
  } catch (error) {
    await eventStore.append({
      type: 'PR.CREATED.FAILED',
      tags: { issueId: data.issueId },
      metadata: { workflowVersion: '1.0.0', eventSource: 'system' },
      data: { error: error.message }
    });
    throw error;
  }
}
```

## 📁 Folder Structure Reference

```
.dev/
├── spikes/                          # Research and prototyping
│   ├── 2025-10-29-ai-provider-comparison.md
│   └── 2025-10-30-event-store-schema.md
├── bugs/                            # Bug reports and resolutions
│   ├── 2025-10-29-postgres-connection-leak.md
│   └── 2025-10-30-cli-crash-on-ctrl-c.md
├── findings/                        # Discoveries and learnings
│   ├── 2025-10-29-pitfall-anthropic-rate-limits.md
│   ├── 2025-10-30-best-practice-fastify-plugins.md
│   └── 2025-10-31-limitation-github-api-pagination.md
├── decisions/                       # Design decisions (ADRs)
│   ├── 2025-10-29-decision-use-pino-for-logging.md
│   └── 2025-10-30-decision-vitest-over-jest.md
└── templates/                       # Document templates
    ├── spike-template.md
    ├── bug-template.md
    ├── finding-template.md
    └── decision-template.md
```

## 🤖 AI Agent Specific Guidelines

### For Claude Code / GitHub Copilot / Cursor / Continue / etc.

**Before generating ANY code:**
1. Read this file (`BEFORE_YOU_CODE.md`)
2. Check `.dev/` folders for relevant context
3. Follow the checklist above
4. Document your findings as you go
5. Create spike if uncertain

**When stuck:**
1. Create a spike document
2. Research the problem
3. Document findings
4. Ask for human review if needed

**After coding:**
1. Update relevant documentation
2. Create findings document if you learned something
3. Link to related spikes/bugs/decisions

## 📊 Success Metrics

**You're doing it right if:**
- ✅ You have test coverage BEFORE implementing
- ✅ You've checked existing documentation
- ✅ You've documented your decisions
- ✅ Your code follows project patterns
- ✅ You've emitted events for audit trail
- ✅ You've handled errors properly
- ✅ You've added structured logging

**You're doing it wrong if:**
- ❌ You wrote code without tests
- ❌ You didn't check for existing spikes/bugs
- ❌ You made architectural decisions without documentation
- ❌ Your code doesn't follow TypeScript strict mode
- ❌ You didn't emit events for significant actions
- ❌ You have no error handling

## 🎓 Learning Resources

### Internal Documentation
- `CLAUDE.md` - Project guidelines
- `docs/architecture.md` - System architecture
- `docs/tech-spec-epic-*.md` - Technical specifications
- `docs/stories/` - Story implementation guides
- `.dev/spikes/` - Research findings
- `.dev/findings/` - Lessons learned

### External Resources
- [TypeScript Strict Mode](https://www.typescriptlang.org/tsconfig#strict)
- [Test-Driven Development](https://martinfowler.com/bliki/TestDrivenDevelopment.html)
- [Event Sourcing](https://martinfowler.com/eaaDev/EventSourcing.html)
- [Pino Logging](https://getpino.io/)

## 💬 Questions?

**If you're unsure:**
1. Check `.dev/` folders first
2. Create a spike to research
3. Document your findings
4. Ask in GitHub Discussions
5. Tag maintainers if urgent

## 🔄 Process Evolution

This process is a living document. If you find ways to improve it:
1. Document your suggestion in `.dev/findings/`
2. Open a GitHub Discussion
3. Submit a PR to update this file

---

**Remember: Code is cheap, understanding is expensive. Invest time upfront to save time later.**

**Last Updated**: October 29, 2025
