# AIBaaS Benchmarking Methodology

**Date**: November 2, 2024
**Purpose**: Define HOW we benchmark AI models for code-related tasks
**Status**: 🚧 DRAFT - Updated Architecture

---

## Overview

This document defines the complete methodology for benchmarking AI models across 7 code-related scenarios.

**Core Architecture Principles**:

1. ✅ **DYNAMIC MODEL DISCOVERY** - Models are NEVER hardcoded
   - Enumerate models from all configured providers (could be 1, could be 100)
   - Test ALL discovered models automatically
   - No manual model list maintenance

2. ✅ **TEST BANK SYSTEM** - Pre-built repository of tasks with ground truth solutions
   - Tasks organized by: Language × Scenario × Difficulty
   - Each task includes: Prompt, Solution, Test Suite, Expected Metrics
   - Results saved for historical comparison

3. ✅ **MULTI-LANGUAGE SUPPORT** - 7 mainstream languages from day 1
   - TypeScript, C#, Java, Python, Go, Ruby, Rust
   - Same task types across all languages

4. ✅ **MULTI-JUDGE SCORING** - Multiple validation layers
   - **Human Judges**: Staff scores + User scores
   - **Self-Review**: Each agent reviews its own generated code
   - **Team Review**: Configured LLM agent team provides collective score
   - **Automated**: Compilation, tests, code quality metrics

5. ✅ **RANDOM TASK SELECTION** - Prevent gaming/memorization
   - Pick 3 tasks per run: 1 Easy + 1 Medium + 1 Hard
   - Random selection from test bank
   - Difficulty stratification ensures comprehensive evaluation

---

## Dynamic Model Discovery

**CRITICAL**: Models are NEVER hardcoded in the system.

### Provider Model Enumeration

```typescript
interface ModelDiscoveryService {
  /**
   * Discover ALL available models from ALL configured providers
   * Called at benchmark startup (monthly run)
   */
  discoverModels(): Promise<DiscoveredModel[]>;
}

interface DiscoveredModel {
  id: string;                    // "anthropic/claude-3-5-sonnet"
  provider: string;              // "anthropic", "openai", "google"
  name: string;                  // "Claude 3.5 Sonnet"
  capabilities: string[];        // ["chat", "code", "function-calling"]
  pricingInputPerM: number;      // $3.00 per 1M tokens
  pricingOutputPerM: number;     // $15.00 per 1M tokens
  contextWindow: number;         // 200000 tokens
  trainingCutoff?: string;       // "2024-07-01" for contamination tracking
}
```

### Implementation

```typescript
async function discoverAllModels(): Promise<DiscoveredModel[]> {
  const providers = getConfiguredProviders(); // Cloudflare AI Gateway, Workers AI, etc.
  const allModels: DiscoveredModel[] = [];

  for (const provider of providers) {
    try {
      // Each provider has its own discovery method
      const models = await provider.listModels();

      // Filter to code-capable models only
      const codeModels = models.filter(m =>
        m.capabilities.includes('code') ||
        m.capabilities.includes('chat')
      );

      allModels.push(...codeModels);
    } catch (error) {
      logger.warn(`Failed to discover models from ${provider.name}`, { error });
    }
  }

  logger.info(`Discovered ${allModels.length} models from ${providers.length} providers`);
  return allModels;
}
```

### Provider-Specific Discovery

**Cloudflare AI Gateway** (proxies to external providers):
```typescript
// OpenAI via Gateway
const openaiModels = await gateway.listModels('openai');
// Returns: gpt-4, gpt-4o, gpt-4-turbo, o1-preview, o1-mini, etc.

// Anthropic via Gateway
const anthropicModels = await gateway.listModels('anthropic');
// Returns: claude-3-5-sonnet, claude-3-opus, claude-3-haiku, etc.

// Google via Gateway
const googleModels = await gateway.listModels('google');
// Returns: gemini-2.5-pro, gemini-2.5-flash, etc.
```

**Cloudflare Workers AI** (Cloudflare's own models):
```typescript
const workersModels = await workersAI.listModels();
// Returns: llama-3.1-70b, qwen2.5-coder-32b, mistral-7b, etc.
```

### Model Filtering

```typescript
// Only benchmark models with sufficient context window
const benchmarkableModels = allModels.filter(m =>
  m.contextWindow >= 8000 // Need enough context for task + solution
);

// Sort by provider for organized results
const sortedModels = benchmarkableModels.sort((a, b) =>
  a.provider.localeCompare(b.provider)
);
```

**Example Output**:
```
Discovered 47 models:
  - Anthropic: 3 models (Claude 3.5 Sonnet, Claude 3 Opus, Claude 3 Haiku)
  - OpenAI: 6 models (GPT-4, GPT-4o, GPT-4 Turbo, o1-preview, o1-mini, GPT-3.5)
  - Google: 4 models (Gemini 2.5 Pro, Gemini 2.5 Flash, Gemini 1.5 Pro, Gemini 1.5 Flash)
  - Mistral: 3 models (Mistral Large, Mistral Medium, Codestral)
  - Cloudflare Workers AI: 12 models (Llama 3.1 70B, Qwen Coder, etc.)
  - Groq: 5 models (Llama 3 70B, Mixtral, etc.)
  - ... (scales automatically as providers add models)
```

---

## Test Bank Architecture

### Database Schema

```sql
-- Test bank: Pre-built tasks with ground truth solutions
CREATE TABLE test_bank (
  id UUID PRIMARY KEY,

  -- Task metadata
  language VARCHAR(20) NOT NULL,        -- 'typescript', 'python', 'csharp', etc.
  scenario VARCHAR(50) NOT NULL,        -- 'code-generation', 'test-generation', etc.
  difficulty VARCHAR(10) NOT NULL,      -- 'easy', 'medium', 'hard'

  -- Task content
  title VARCHAR(200) NOT NULL,
  description TEXT NOT NULL,            -- Task requirements
  prompt TEXT NOT NULL,                 -- Exact prompt sent to models
  starter_code TEXT,                    -- Optional starter code

  -- Ground truth
  solution TEXT NOT NULL,               -- Reference solution
  test_suite TEXT NOT NULL,             -- Test cases (JSON)
  expected_metrics JSONB,               -- Expected performance metrics

  -- Metadata
  created_at TIMESTAMPTZ DEFAULT NOW(),
  created_by VARCHAR(100),              -- Staff member who created task
  tags TEXT[],                          -- ['sql-injection', 'security', 'authentication']
  source VARCHAR(200),                  -- 'github.com/user/repo/issues/123'

  CONSTRAINT difficulty_check CHECK (difficulty IN ('easy', 'medium', 'hard'))
);

-- Index for fast random selection
CREATE INDEX idx_test_bank_selection ON test_bank(language, scenario, difficulty);

-- Historical results
CREATE TABLE benchmark_results (
  id UUID PRIMARY KEY,
  run_id UUID NOT NULL,                 -- Groups results from same monthly run

  -- Model info (captured at runtime)
  model_id VARCHAR(200) NOT NULL,       -- "anthropic/claude-3-5-sonnet"
  model_provider VARCHAR(100),
  model_name VARCHAR(200),

  -- Task reference
  task_id UUID REFERENCES test_bank(id),

  -- Model response
  generated_code TEXT,
  raw_response TEXT,                    -- Full model response

  -- Automated metrics
  compiles BOOLEAN,
  test_pass_rate DECIMAL(5,2),          -- 0.00 to 100.00
  code_quality_score DECIMAL(4,2),      -- 0.00 to 10.00
  latency_ms INTEGER,
  input_tokens INTEGER,
  output_tokens INTEGER,
  cost_usd DECIMAL(10,8),

  -- Multi-judge scores (0-10 scale)
  staff_score DECIMAL(4,2),             -- Average of staff reviews
  user_score DECIMAL(4,2),              -- Average of user reviews
  self_review_score DECIMAL(4,2),       -- Model reviews its own code
  team_review_score DECIMAL(4,2),       -- LLM agent team review

  -- Final score
  final_score DECIMAL(4,2),             -- Weighted average of all scores

  -- Timestamps
  started_at TIMESTAMPTZ,
  completed_at TIMESTAMPTZ,

  created_at TIMESTAMPTZ DEFAULT NOW()
);

-- Index for historical comparisons
CREATE INDEX idx_results_model_task ON benchmark_results(model_id, task_id);
CREATE INDEX idx_results_run ON benchmark_results(run_id);
```

### Test Bank Organization

**Scale**: **50 tasks per difficulty level** = 150 tasks per scenario per language

```
test_bank/
├── typescript/
│   ├── code-generation/
│   │   ├── easy/
│   │   │   ├── 001-validate-email.json
│   │   │   ├── 002-capitalize-string.json
│   │   │   └── ... (50 easy tasks total)
│   │   ├── medium/
│   │   │   ├── 001-parse-json-safe.json
│   │   │   ├── 002-debounce-function.json
│   │   │   └── ... (50 medium tasks total)
│   │   └── hard/
│   │       ├── 001-async-retry-backoff.json
│   │       ├── 002-lru-cache.json
│   │       └── ... (50 hard tasks total)
│   ├── test-generation/ (150 tasks)
│   ├── code-review/ (150 tasks)
│   ├── refactoring/ (150 tasks)
│   ├── debugging/ (150 tasks)
│   ├── security/ (150 tasks)
│   └── documentation/ (150 tasks)
├── python/ (1,050 tasks)
├── csharp/ (1,050 tasks)
├── java/ (1,050 tasks)
├── go/ (1,050 tasks)
├── ruby/ (1,050 tasks)
└── rust/ (1,050 tasks)
```

**Total Test Bank Size**: 7 languages × 7 scenarios × 150 tasks = **7,350 tasks**

### Example Task JSON

```json
{
  "id": "ts-codegen-easy-001",
  "language": "typescript",
  "scenario": "code-generation",
  "difficulty": "easy",
  "title": "Email Validation Function",
  "description": "Create a function that validates email addresses using regex",
  "prompt": "Write a TypeScript function `validateEmail(email: string): boolean` that:\n- Returns true if email is valid (basic RFC 5322 check)\n- Returns false otherwise\n- Handles edge cases: empty string, whitespace, missing @ symbol\n- Include JSDoc comments",
  "starter_code": null,
  "solution": "/**\n * Validates email addresses using regex\n * @param email - Email string to validate\n * @returns true if valid, false otherwise\n */\nfunction validateEmail(email: string): boolean {\n  if (!email || email.trim().length === 0) return false;\n  const regex = /^[^\\s@]+@[^\\s@]+\\.[^\\s@]+$/;\n  return regex.test(email.trim());\n}",
  "test_suite": {
    "framework": "vitest",
    "tests": [
      { "input": "test@example.com", "expected": true },
      { "input": "invalid-email", "expected": false },
      { "input": "", "expected": false },
      { "input": "  ", "expected": false },
      { "input": "test@", "expected": false },
      { "input": "@example.com", "expected": false },
      { "input": "test @example.com", "expected": false },
      { "input": "test@example", "expected": false },
      { "input": "test+tag@example.com", "expected": true },
      { "input": "test.name@example.co.uk", "expected": true }
    ]
  },
  "expected_metrics": {
    "test_pass_rate": 100,
    "code_quality_min": 7.0,
    "max_lines": 15
  },
  "tags": ["validation", "regex", "string-manipulation"],
  "source": "hand-crafted",
  "created_by": "engineering-team",
  "created_at": "2024-11-02T10:00:00Z"
}
```

---

## Multi-Judge Scoring System

### 4 Scoring Layers

```
Final Score = Weighted Average of:
  1. Automated Score (40%) - Compilation, tests, code quality
  2. Staff Score (25%) - Expert human evaluation
  3. User Score (20%) - Community feedback
  4. Self-Review Score (7.5%) - Model reviews its own code
  5. Team Review Score (7.5%) - LLM agent team evaluation
```

### 1. Automated Score (40%)

```typescript
async function calculateAutomatedScore(result: BenchmarkResult): Promise<number> {
  let score = 0;

  // Compilation (30% of automated = 12% of total)
  if (result.compiles) score += 3.0;

  // Test pass rate (50% of automated = 20% of total)
  score += (result.test_pass_rate / 100) * 5.0;

  // Code quality (20% of automated = 8% of total)
  score += result.code_quality_score * 0.2;

  return score; // Max: 10.0
}
```

### 2. Staff Score (25%)

```typescript
// Staff members review subset of results
interface StaffReview {
  reviewer_id: string;
  benchmark_result_id: string;

  // Evaluation criteria (0-10 each)
  correctness: number;      // Does it solve the problem correctly?
  code_quality: number;     // Is code clean, readable, maintainable?
  best_practices: number;   // Follows language idioms?
  efficiency: number;       // Performance characteristics?

  overall_score: number;    // 0-10
  notes: string;
  reviewed_at: Date;
}

async function getStaffScore(resultId: string): Promise<number> {
  const reviews = await db.query(
    'SELECT overall_score FROM staff_reviews WHERE benchmark_result_id = $1',
    [resultId]
  );

  if (reviews.length === 0) return null; // No reviews yet

  // Average of all staff reviews
  const avgScore = reviews.reduce((sum, r) => sum + r.overall_score, 0) / reviews.length;
  return avgScore;
}
```

### 3. User Score (20%)

```typescript
// Public users can upvote/downvote model responses
interface UserReview {
  user_id: string;
  benchmark_result_id: string;

  vote: 'upvote' | 'downvote';  // Simple thumbs up/down
  comment?: string;

  reviewed_at: Date;
}

async function getUserScore(resultId: string): Promise<number> {
  const votes = await db.query(
    'SELECT vote FROM user_reviews WHERE benchmark_result_id = $1',
    [resultId]
  );

  if (votes.length === 0) return null; // No votes yet

  const upvotes = votes.filter(v => v.vote === 'upvote').length;
  const downvotes = votes.filter(v => v.vote === 'downvote').length;
  const total = votes.length;

  // Convert to 0-10 scale
  const score = (upvotes / total) * 10;
  return score;
}
```

### 4. Self-Review Score (7.5%)

```typescript
// Model reviews its OWN generated code
async function getSelfReviewScore(
  model: string,
  task: Task,
  generatedCode: string
): Promise<number> {

  const selfReviewPrompt = `
You previously generated this code for the following task:

Task: ${task.prompt}

Your Generated Code:
\`\`\`${task.language}
${generatedCode}
\`\`\`

Review your own code objectively:
1. Does it correctly solve the problem?
2. Are there any bugs or edge cases missed?
3. Is the code quality good?
4. Would you improve anything?

Provide a score from 0-10 and brief justification.
Format: {"score": X, "issues": ["..."], "improvements": ["..."]}
`;

  const selfReview = await aiGateway.complete({
    model: model, // SAME model reviews itself
    messages: [{ role: 'user', content: selfReviewPrompt }],
    temperature: 0.3
  });

  const parsed = JSON.parse(selfReview.content);
  return parsed.score;
}
```

### 5. Team Review Score (7.5%)

**Judge Team Configuration** (8 elite models for comprehensive review):

```typescript
const DEFAULT_JUDGE_TEAM: AgentTeamConfig = {
  judges: [
    // Anthropic - Code quality & reasoning
    { model: 'anthropic/claude-opus-4.1', role: 'code-quality-expert', weight: 0.125 },
    { model: 'anthropic/claude-sonnet-4.5', role: 'architecture-expert', weight: 0.125 },

    // OpenAI - General coding & best practices
    { model: 'openai/gpt-5', role: 'best-practices-expert', weight: 0.125 },
    { model: 'openai/codex', role: 'code-generation-expert', weight: 0.125 },

    // DeepSeek - Code reasoning specialist
    { model: 'deepseek/r1', role: 'reasoning-expert', weight: 0.125 },

    // Google - Multi-modal & performance
    { model: 'google/gemini-2.5-pro', role: 'performance-expert', weight: 0.125 },

    // Chinese AI leaders
    { model: 'zhipu/glm-4.6', role: 'security-expert', weight: 0.125 },
    { model: 'moonshot/kimi-k2', role: 'edge-case-expert', weight: 0.125 },
  ]
};
```

**Review Process**:

```typescript
async function getTeamReviewScore(
  task: Task,
  generatedCode: string,
  teamConfig: AgentTeamConfig = DEFAULT_JUDGE_TEAM
): Promise<number> {

  const reviewPromises = teamConfig.judges.map(async (judge) => {
    const reviewPrompt = `
You are a ${judge.role} reviewing code.

Task: ${task.prompt}

Generated Code:
\`\`\`${task.language}
${generatedCode}
\`\`\`

As a ${judge.role}, evaluate this code focusing on your specialty and provide a score from 0-10.
Format: {"score": X, "reasoning": "...", "critical_issues": [...], "suggestions": [...]}
`;

    const review = await aiGateway.complete({
      model: judge.model,
      messages: [{ role: 'user', content: reviewPrompt }],
      temperature: 0.2
    });

    const parsed = JSON.parse(review.content);
    return {
      score: parsed.score,
      weight: judge.weight,
      model: judge.model,
      role: judge.role,
      reasoning: parsed.reasoning
    };
  });

  const reviews = await Promise.all(reviewPromises);

  // Weighted average
  const totalWeight = reviews.reduce((sum, r) => sum + r.weight, 0);
  const weightedSum = reviews.reduce((sum, r) => sum + (r.score * r.weight), 0);

  // Store individual judge scores for transparency
  await storeJudgeReviews(reviews);

  return weightedSum / totalWeight;
}
```

**Judge Team Expansion** (future):
- Add more models as they become available
- Support custom judge teams per language (e.g., Rust experts for Rust code)
- Allow users to configure their own judge teams

### Final Score Calculation

```typescript
async function calculateFinalScore(result: BenchmarkResult): Promise<number> {
  const automated = calculateAutomatedScore(result);          // 0-10
  const staff = await getStaffScore(result.id);               // 0-10 or null
  const user = await getUserScore(result.id);                 // 0-10 or null
  const selfReview = await getSelfReviewScore(...);           // 0-10
  const teamReview = await getTeamReviewScore(...);           // 0-10

  // Weighted average (handle null scores)
  let totalWeight = 0.40; // Automated always available
  let weightedSum = automated * 0.40;

  if (staff !== null) {
    weightedSum += staff * 0.25;
    totalWeight += 0.25;
  }

  if (user !== null) {
    weightedSum += user * 0.20;
    totalWeight += 0.20;
  }

  if (selfReview !== null) {
    weightedSum += selfReview * 0.075;
    totalWeight += 0.075;
  }

  if (teamReview !== null) {
    weightedSum += teamReview * 0.075;
    totalWeight += 0.075;
  }

  // Normalize to 0-10 scale
  const finalScore = (weightedSum / totalWeight) * 10;

  return Math.min(10, Math.max(0, finalScore)); // Clamp to [0, 10]
}
```

---

## Random Task Selection Algorithm

### Strategy: 3 Tasks per Model (1 Easy + 1 Medium + 1 Hard)

```typescript
async function selectRandomTasks(
  language: string,
  scenario: string
): Promise<[Task, Task, Task]> {

  // Pick 1 random EASY task
  const easyTasks = await db.query(
    'SELECT * FROM test_bank WHERE language = $1 AND scenario = $2 AND difficulty = $3',
    [language, scenario, 'easy']
  );
  const easyTask = easyTasks[Math.floor(Math.random() * easyTasks.length)];

  // Pick 1 random MEDIUM task
  const mediumTasks = await db.query(
    'SELECT * FROM test_bank WHERE language = $1 AND scenario = $2 AND difficulty = $3',
    [language, scenario, 'medium']
  );
  const mediumTask = mediumTasks[Math.floor(Math.random() * mediumTasks.length)];

  // Pick 1 random HARD task
  const hardTasks = await db.query(
    'SELECT * FROM test_bank WHERE language = $1 AND scenario = $2 AND difficulty = $3',
    [language, scenario, 'hard']
  );
  const hardTask = hardTasks[Math.floor(Math.random() * hardTasks.length)];

  return [easyTask, mediumTask, hardTask];
}
```

### Full Benchmark Run

```typescript
async function runMonthlyBenchmark() {
  const runId = generateUUID();

  // 1. Discover ALL models dynamically
  const models = await discoverAllModels();
  logger.info(`Benchmarking ${models.length} models`);

  // 2. For each language
  const languages = ['typescript', 'python', 'csharp', 'java', 'go', 'ruby', 'rust'];

  // 3. For each scenario
  const scenarios = [
    'code-generation',
    'test-generation',
    'code-review',
    'refactoring',
    'debugging',
    'security',
    'documentation'
  ];

  // 4. For each model
  for (const model of models) {
    for (const language of languages) {
      for (const scenario of scenarios) {

        // Pick 3 random tasks (easy, medium, hard)
        const [easyTask, mediumTask, hardTask] = await selectRandomTasks(language, scenario);

        // Test model on all 3 tasks
        for (const task of [easyTask, mediumTask, hardTask]) {
          const result = await runBenchmarkTask(runId, model, task);
          await storeBenchmarkResult(result);
        }
      }
    }
  }

  logger.info(`Benchmark run ${runId} completed`);
}
```

### Cost Calculation with Full Configuration

**Benchmark Scale**:
- 100 models (discovered dynamically)
- 7 languages (TypeScript, Python, C#, Java, Go, Ruby, Rust)
- 7 scenarios (code-gen, test-gen, review, refactor, debug, security, docs)
- 3 tasks per scenario (1 easy + 1 medium + 1 hard, randomly selected)
- **Total**: 100 × 7 × 7 × 3 = **14,700 benchmark executions per month**

**API Calls per Benchmark Execution**:
1. Model generation: 1 call
2. Self-review: 1 call (same model reviews itself)
3. Team review: 8 calls (8 judge models)
- **Total**: 10 API calls per execution

**Total Monthly API Calls**: 14,700 × 10 = **147,000 API calls**

---

#### **Unoptimized Cost (Worst Case)**

```
Model generation: 14,700 × $0.10 (avg) = $1,470
Self-review: 14,700 × $0.10 = $1,470
Team review: 14,700 × 8 × $0.05 (judge avg) = $5,880

Total: $8,820/month for 100 models
```

---

#### **Optimized Cost (Realistic)**

**Optimizations Applied**:
1. ✅ **Cheaper models dominate** (60% are Gemini Flash/Llama/Qwen = $0.001/task)
2. ✅ **Cache team reviews** (same code from multiple models = 30% savings)
3. ✅ **Sample team review** (run 8-judge panel on top 20% + random 10% = 70% savings)
4. ✅ **Batch API calls** (reduce overhead)

**Optimized Breakdown**:
```
Model Generation:
  - Expensive (GPT-4, Claude, 10%): 1,470 × $0.10 = $147
  - Mid-tier (GPT-4o, Gemini Pro, 30%): 4,410 × $0.01 = $44
  - Cheap (Gemini Flash, Llama, 60%): 8,820 × $0.001 = $9
  Subtotal: $200

Self-Review (same distribution):
  Subtotal: $200

Team Review (only on 30% of results):
  - 30% sample: 4,410 results × 8 judges × $0.02 (optimized) = $706
  - Cache savings (30%): -$212
  Subtotal: $494

Total Optimized: ~$894/month for 100 models
```

---

#### **MVP Cost (Smaller Scale)**

**If starting with fewer models** (e.g., 20 models instead of 100):

```
20 models × 7 languages × 7 scenarios × 3 tasks = 2,940 executions
2,940 × 10 API calls = 29,400 total calls

Optimized cost: ~$179/month for 20 models
```

**Progressive Scaling**:
```
Month 1-3: 20 models → $179/month
Month 4-6: 50 models → $447/month
Month 7+: 100 models → $894/month
```

---

## 7 Benchmark Scenarios

### 1. Code Generation

**Task**: Generate a complete function/module from natural language requirements.

**Example Prompt**:
```
Write a TypeScript function `validateEmail(email: string): boolean` that:
- Returns true if email is valid (RFC 5322 compliant)
- Returns false otherwise
- Handles edge cases: empty string, whitespace, special characters
- Include JSDoc comments
- Use modern TypeScript (strict mode)
```

**Evaluation Method**: **Automated**
1. **Compiles?** (tsc --strict) → 30% of score
2. **Tests pass?** (10 test cases covering edge cases) → 50% of score
3. **Code quality?** (ESLint score, cyclomatic complexity) → 20% of score

**Metrics Tracked**:
- ✅ Compilation success rate
- ✅ Test pass rate (% of tests passing)
- ✅ Code quality score (0-10)
- ✅ Cost per task ($)
- ✅ Latency (ms)

---

### 2. Test Generation

**Task**: Generate unit tests for existing code.

**Example Prompt**:
```
Generate comprehensive unit tests for this TypeScript function:

\`\`\`typescript
function calculateDiscount(price: number, couponCode: string): number {
  if (price <= 0) throw new Error('Invalid price');
  if (couponCode === 'SAVE10') return price * 0.9;
  if (couponCode === 'SAVE20') return price * 0.8;
  return price;
}
\`\`\`

Requirements:
- Use Vitest
- Cover all branches
- Test edge cases (negative price, empty coupon, invalid coupon)
- Include describe/it blocks
```

**Evaluation Method**: **Automated**
1. **Tests compile?** → 20% of score
2. **Tests run?** (no errors) → 30% of score
3. **Code coverage?** (% branches covered) → 40% of score
4. **Tests pass?** (when run against original function) → 10% of score

**Metrics Tracked**:
- ✅ Test compilation success
- ✅ Code coverage achieved (%)
- ✅ Number of test cases generated
- ✅ Cost per task ($)
- ✅ Latency (ms)

---

### 3. Code Review

**Task**: Review code for bugs, security issues, performance problems, and style violations.

**Example Prompt**:
```
Review this TypeScript code for bugs, security issues, and performance problems:

\`\`\`typescript
async function getUserData(userId: string) {
  const query = `SELECT * FROM users WHERE id = '${userId}'`;
  const result = await db.execute(query);
  return result[0];
}
\`\`\`

Provide feedback on:
1. Security vulnerabilities
2. Bugs or logic errors
3. Performance issues
4. Code style/best practices
```

**Evaluation Method**: **LLM-as-Judge + Ground Truth**
1. **Identifies SQL injection?** (ground truth) → 40% of score
2. **Identifies error handling issues?** (ground truth) → 20% of score
3. **Suggests valid fixes?** (LLM judge evaluates quality) → 30% of score
4. **Code style feedback quality?** (LLM judge) → 10% of score

**Ground Truth Issues** (for example above):
- ❌ SQL injection vulnerability (using string interpolation)
- ❌ No error handling (db.execute can throw)
- ❌ Assumes result[0] exists (could be undefined)
- ⚠️ No input validation (userId could be malicious)

**Metrics Tracked**:
- ✅ Critical issues detected (%)
- ✅ False positive rate (%)
- ✅ Fix quality score (0-10)
- ✅ Cost per task ($)
- ✅ Latency (ms)

---

### 4. Refactoring

**Task**: Refactor legacy code to modern patterns/best practices.

**Example Prompt**:
```
Refactor this JavaScript code to modern TypeScript with best practices:

\`\`\`javascript
function processUsers(users) {
  var result = [];
  for (var i = 0; i < users.length; i++) {
    if (users[i].age >= 18) {
      result.push({
        name: users[i].name,
        email: users[i].email
      });
    }
  }
  return result;
}
\`\`\`

Requirements:
- Use TypeScript with interfaces
- Use modern ES6+ features (const/let, arrow functions, array methods)
- Add proper types
- Improve readability
```

**Evaluation Method**: **Automated + LLM-as-Judge**
1. **Compiles with TypeScript strict mode?** → 30% of score
2. **Uses modern features?** (const/let, arrow functions, filter/map) → 20% of score
3. **Functionality preserved?** (tests pass for original test cases) → 30% of score
4. **Code quality improvement?** (LLM judge compares before/after) → 20% of score

**Metrics Tracked**:
- ✅ Compilation success
- ✅ Test pass rate (regression check)
- ✅ Code quality improvement score
- ✅ Cost per task ($)
- ✅ Latency (ms)

---

### 5. Debugging

**Task**: Identify and fix bugs in failing code.

**Example Prompt**:
```
This function should return the sum of even numbers in an array, but tests are failing:

\`\`\`typescript
function sumEvenNumbers(numbers: number[]): number {
  let sum = 0;
  for (let i = 0; i <= numbers.length; i++) {
    if (numbers[i] % 2 === 0) {
      sum += numbers[i];
    }
  }
  return sum;
}
\`\`\`

Failing test:
\`\`\`typescript
expect(sumEvenNumbers([1, 2, 3, 4])).toBe(6); // Expected 6, got NaN
\`\`\`

Fix the bug and explain what was wrong.
```

**Ground Truth Bug**: `i <= numbers.length` should be `i < numbers.length` (off-by-one error causes undefined access)

**Evaluation Method**: **Automated**
1. **Identifies correct bug?** → 40% of score
2. **Provides correct fix?** (tests now pass) → 40% of score
3. **Explains bug clearly?** (LLM judge evaluates explanation) → 20% of score

**Metrics Tracked**:
- ✅ Bug identification accuracy
- ✅ Fix correctness (tests pass)
- ✅ Explanation quality score
- ✅ Cost per task ($)
- ✅ Latency (ms)

---

### 6. Security Scanning

**Task**: Identify security vulnerabilities in code.

**Example Prompt**:
```
Scan this Express.js route for security vulnerabilities:

\`\`\`typescript
app.post('/upload', (req, res) => {
  const filename = req.body.filename;
  const content = req.body.content;

  fs.writeFileSync(`./uploads/${filename}`, content);
  res.send('File uploaded');
});
\`\`\`

List all security issues found with severity (Critical/High/Medium/Low).
```

**Ground Truth Vulnerabilities**:
- 🔴 **Critical**: Path traversal (filename could be `../../etc/passwd`)
- 🔴 **Critical**: No file type validation (could upload malicious scripts)
- 🟠 **High**: No authentication/authorization
- 🟠 **High**: No input sanitization
- 🟡 **Medium**: No rate limiting (DoS risk)

**Evaluation Method**: **Ground Truth Matching**
1. **Critical issues found** (2/2) → 50% of score
2. **High issues found** (2/2) → 30% of score
3. **Medium issues found** (1/1) → 10% of score
4. **False positives** (penalty: -5% per false positive) → up to -20%
5. **Remediation advice quality** (LLM judge) → 10% of score

**Metrics Tracked**:
- ✅ Critical vulnerabilities detected (%)
- ✅ False positive rate (%)
- ✅ Remediation quality score
- ✅ Cost per task ($)
- ✅ Latency (ms)

---

### 7. Documentation Generation

**Task**: Generate comprehensive documentation for code.

**Example Prompt**:
```
Generate comprehensive documentation for this TypeScript class:

\`\`\`typescript
class UserRepository {
  constructor(private db: Database) {}

  async findById(id: string): Promise<User | null> {
    return this.db.query('SELECT * FROM users WHERE id = ?', [id]);
  }

  async create(user: CreateUserDto): Promise<User> {
    const result = await this.db.insert('users', user);
    return { id: result.insertId, ...user };
  }
}
\`\`\`

Include:
- Class overview
- Constructor parameters
- Method descriptions with @param, @returns, @throws
- Usage examples
```

**Evaluation Method**: **LLM-as-Judge**
1. **Completeness** (documents all methods/params) → 30% of score
2. **Accuracy** (correct types, correct behavior description) → 30% of score
3. **Clarity** (clear explanations, good examples) → 20% of score
4. **Format** (valid JSDoc/TSDoc syntax) → 20% of score

**Metrics Tracked**:
- ✅ Completeness score (0-10)
- ✅ Accuracy score (0-10)
- ✅ Clarity score (0-10)
- ✅ Cost per task ($)
- ✅ Latency (ms)

---

## Evaluation Pipeline

### Step 1: Run Benchmark Task

```typescript
async function runBenchmark(model: string, scenario: string, taskId: string) {
  const task = getTask(scenario, taskId); // Get predefined task

  const startTime = Date.now();
  const startTokens = await getTokenCount(task.prompt);

  // Call AI provider via Cloudflare AI Gateway
  const response = await aiGateway.complete({
    model: model,
    messages: [{ role: 'user', content: task.prompt }],
    temperature: 0.2, // Low temperature for consistency
    max_tokens: 2048
  });

  const endTime = Date.now();
  const latency = endTime - startTime;

  return {
    model,
    scenario,
    taskId,
    response: response.content,
    latency,
    inputTokens: response.usage.input_tokens,
    outputTokens: response.usage.output_tokens,
    cost: calculateCost(model, response.usage)
  };
}
```

### Step 2: Automated Evaluation

```typescript
async function evaluateResponse(result: BenchmarkResult) {
  switch (result.scenario) {
    case 'code-generation':
      return evaluateCodeGeneration(result);
    case 'test-generation':
      return evaluateTestGeneration(result);
    case 'code-review':
      return evaluateCodeReview(result);
    // ... other scenarios
  }
}

async function evaluateCodeGeneration(result: BenchmarkResult) {
  // 1. Extract code from response
  const code = extractCode(result.response);

  // 2. Check if it compiles
  const compiles = await checkCompilation(code);
  if (!compiles) return { score: 0, reason: 'Code does not compile' };

  // 3. Run tests
  const testResults = await runTests(code, result.taskId);
  const testPassRate = testResults.passed / testResults.total;

  // 4. Check code quality
  const qualityScore = await checkCodeQuality(code);

  // 5. Calculate final score
  return {
    score: (compiles ? 30 : 0) + (testPassRate * 50) + (qualityScore * 20),
    compiles,
    testPassRate,
    qualityScore
  };
}
```

### Step 3: LLM-as-Judge (for subjective metrics)

```typescript
async function llmJudge(task: string, response: string, criteria: string) {
  const judgePrompt = `
You are evaluating an AI model's response to a coding task.

Task: ${task}

AI Response: ${response}

Evaluation Criteria: ${criteria}

Provide a score from 0-10 and brief justification.
Format: {"score": X, "reasoning": "..."}
`;

  const judgment = await aiGateway.complete({
    model: 'gpt-4o', // Use consistent judge model
    messages: [{ role: 'user', content: judgePrompt }],
    temperature: 0.1 // Very low temp for consistency
  });

  return JSON.parse(judgment.content);
}
```

### Step 4: Store Results

```typescript
async function storeBenchmarkResults(result: BenchmarkResult, evaluation: Evaluation) {
  await db.insert('benchmark_runs', {
    timestamp: new Date(),
    model_id: result.model,
    scenario: result.scenario,
    task_id: result.taskId,
    score: evaluation.score,
    latency_ms: result.latency,
    input_tokens: result.inputTokens,
    output_tokens: result.outputTokens,
    cost_usd: result.cost,
    metadata: {
      compiles: evaluation.compiles,
      test_pass_rate: evaluation.testPassRate,
      quality_score: evaluation.qualityScore
    }
  });
}
```

---

## Test Case Design

### Contamination Prevention (from LiveBench research)

**Problem**: Models may have seen benchmark tasks during training.

**Solution**: Use **post-release code** from GitHub/GitLab issues created AFTER model's training cutoff date.

**Example**:
```typescript
// For GPT-4 (cutoff: April 2023), use GitHub issues from May 2023+
// For Claude 3.5 (cutoff: July 2024), use issues from August 2024+

async function getContaminationFreeTask(model: string, scenario: string) {
  const modelCutoffDate = MODEL_CUTOFF_DATES[model]; // e.g., '2024-07-01'

  // Fetch GitHub issue created after cutoff
  const issue = await github.searchIssues({
    language: 'typescript',
    labels: scenario,
    created: `>${modelCutoffDate}`,
    state: 'closed' // Use solved issues for ground truth
  });

  return {
    prompt: issue.body,
    groundTruth: issue.solution // From merged PR
  };
}
```

### Test Case Rotation (from SimpleBench research)

**Problem**: Models could memorize public test cases.

**Solution**:
- **80% private test cases** (never published)
- **20% public test cases** (for transparency)
- **Quarterly rotation** of public cases

---

## Scoring Formula

### Per-Task Score (0-10)

```
Total Score = (Quality × 0.6) + (Performance × 0.2) + (Cost × 0.2)

Where:
- Quality = Automated evaluation (tests pass, compiles, etc.)
- Performance = Latency score (normalized 0-10)
- Cost = Cost score (normalized 0-10, lower cost = higher score)
```

### Aggregate Model Score

```
Model Score = Average(all task scores across all scenarios)

Example:
GPT-4:
  - Code Generation: 8.5/10
  - Test Generation: 7.8/10
  - Code Review: 9.2/10
  - Refactoring: 8.1/10
  - Debugging: 8.9/10
  - Security: 9.5/10
  - Documentation: 7.5/10

  Overall: (8.5 + 7.8 + 9.2 + 8.1 + 8.9 + 9.5 + 7.5) / 7 = 8.5/10
```

---

## Summary: Key Architecture Decisions ✅

### **1. Dynamic Model Discovery**
- ✅ Models NEVER hardcoded
- ✅ Auto-discover from all configured providers
- ✅ Scales from 1 to 100+ models automatically

### **2. Test Bank**
- ✅ **7,350 total tasks** (7 languages × 7 scenarios × 150 tasks)
- ✅ **50 tasks per difficulty level** (easy/medium/hard)
- ✅ Pre-built with ground truth solutions
- ✅ Random selection prevents gaming

### **3. Language Support**
- ✅ **7 languages from day 1**: TypeScript, C#, Java, Python, Go, Ruby, Rust
- ✅ Same scenarios across all languages
- ✅ Language-specific test suites

### **4. Multi-Judge Scoring**
- ✅ **40%** - Automated (compilation, tests, code quality)
- ✅ **25%** - Staff reviews (expert humans)
- ✅ **20%** - User votes (community)
- ✅ **7.5%** - Self-review (model reviews own code)
- ✅ **7.5%** - Team review (8-judge panel)

### **5. Judge Team (8 Elite Models)**
- ✅ Claude Opus 4.1 (code quality)
- ✅ Claude Sonnet 4.5 (architecture)
- ✅ GPT-5 (best practices)
- ✅ Codex (code generation)
- ✅ DeepSeek R1 (reasoning)
- ✅ Gemini 2.5 Pro (performance)
- ✅ GLM 4.6 (security)
- ✅ Kimi K2 (edge cases)

### **6. Benchmark Frequency**
- ✅ **Monthly runs** (can scale to weekly/daily)
- ✅ Random task selection (3 per scenario: easy/medium/hard)
- ✅ Historical comparison (track model improvements over time)

### **7. Cost Projections**
```
MVP (20 models):     $179/month
Growth (50 models):  $447/month
Scale (100 models):  $894/month
```

---

## Implementation Roadmap

### **Phase 1: Test Bank Creation** (Weeks 1-4)
1. ✅ Create 50 easy tasks per scenario per language (2,450 tasks)
2. ✅ Create 50 medium tasks per scenario per language (2,450 tasks)
3. ✅ Create 50 hard tasks per scenario per language (2,450 tasks)
4. ✅ Validate all tasks compile and tests pass
5. ✅ Store in database with metadata

### **Phase 2: Evaluation Pipeline** (Weeks 5-8)
1. ✅ Build model discovery service (enumerate providers)
2. ✅ Build task execution engine (run code, capture output)
3. ✅ Build automated scoring (compilation, tests, quality)
4. ✅ Integrate self-review and team review
5. ✅ Build result storage (TimescaleDB)

### **Phase 3: Pilot Benchmark** (Week 9)
1. ✅ Run first monthly benchmark (10-20 models)
2. ✅ Validate scoring accuracy
3. ✅ Compare automated vs human scores
4. ✅ Identify issues and edge cases

### **Phase 4: Production Launch** (Week 10+)
1. ✅ Scale to 50+ models
2. ✅ Enable staff review interface
3. ✅ Enable user voting
4. ✅ Publish public leaderboard
5. ✅ Monitor costs and optimize

---

## Next Immediate Actions

### **Task Bank Creation Priority**
Start with **TypeScript + Python** (most popular):
- 2 languages × 7 scenarios × 150 tasks = **2,100 tasks**
- Estimated effort: 2 weeks (with team)
- Cost: $0 (staff time only)

**Task Creation Process**:
1. Brainstorm 50 task ideas per scenario
2. Write prompts and solutions
3. Create test suites (automated validation)
4. Peer review for quality
5. Store in database

### **Judge Team Setup**
Configure access to 8 judge models:
- ✅ Anthropic (Claude Opus 4.1, Sonnet 4.5)
- ✅ OpenAI (GPT-5, Codex)
- ✅ DeepSeek (R1)
- ✅ Google (Gemini 2.5 Pro)
- ✅ Zhipu (GLM 4.6)
- ✅ Moonshot (Kimi K2)

### **Infrastructure Setup**
- ✅ Cloudflare AI Gateway account
- ✅ Provider API keys (8 providers)
- ✅ PostgreSQL + TimescaleDB
- ✅ BullMQ for job queue
- ✅ Monitoring (costs, latency, errors)

---

**Status**: ✅ **ARCHITECTURE FINALIZED - Ready for Implementation**

**Next Step**: Begin Task Bank creation (TypeScript + Python first)
