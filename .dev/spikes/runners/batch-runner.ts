/**
 * Batch Test Runner
 *
 * Runs multiple test iterations across all providers and scenarios for statistical analysis
 */

import { writeFile, mkdir } from 'fs/promises';
import { join } from 'path';
import type { AIProvider, TestResult } from '../providers/base-provider.js';
import { getAvailableProviders } from '../providers/index.js';

export interface BatchConfig {
  iterations?: number; // Number of times to run each test (default: 3)
  scenarios?: string[]; // Scenarios to test (default: all)
  providers?: string[]; // Provider names to test (default: all available)
  delayMs?: number; // Delay between tests to avoid rate limits (default: 2000)
  outputDir?: string; // Directory for results (default: ./results)
  includePaid?: boolean; // Include paid models (default: false, free only)
}

export interface BatchResults {
  config: BatchConfig;
  startTime: string;
  endTime: string;
  durationMs: number;
  results: TestResult[];
  aggregated: AggregatedResults[];
}

export interface AggregatedResults {
  provider: string;
  model: string;
  scenario: string;
  iterations: number;
  scores: {
    mean: number;
    min: number;
    max: number;
    stdDev: number;
    confidence: 'high' | 'medium' | 'low';
  };
  metrics: {
    meanInputTokens: number;
    meanOutputTokens: number;
    meanResponseTimeMs: number;
    totalCost: number;
  };
  individualScores: number[];
}

const SCENARIOS = {
  'issue-analysis': `Analyze the following GitHub issue and provide:
1. A clear breakdown of requirements
2. Any ambiguities that need clarification
3. Scope boundaries (what's included/excluded)

Issue:
Title: Add user authentication with JWT tokens
Description: We need to implement JWT-based authentication for our API. Users should be able to register, login, and receive a JWT token that they can use for authenticated requests. The token should expire after 24 hours.

Provide your analysis in a structured format.`,

  'code-generation': `Write a TypeScript function that validates a JWT token. Requirements:
- Function name: validateJwtToken
- Input: token (string), secret (string)
- Output: decoded payload object or throws error
- Use error handling for invalid/expired tokens
- Include TypeScript types
- No external libraries (use only Node.js built-ins)

Provide only the code, no explanations.`,

  'test-generation': `Generate Vitest unit tests for this TypeScript function:

\`\`\`typescript
function validateJwtToken(token: string, secret: string): Record<string, unknown> {
  if (!token || !secret) {
    throw new Error('Token and secret are required');
  }

  const parts = token.split('.');
  if (parts.length !== 3) {
    throw new Error('Invalid token format');
  }

  try {
    const payload = JSON.parse(Buffer.from(parts[1], 'base64').toString());
    return payload;
  } catch (error) {
    throw new Error('Failed to decode token');
  }
}
\`\`\`

Generate comprehensive tests covering:
- Happy path (valid token)
- Missing token/secret
- Invalid format
- Malformed payload

Use Vitest syntax (describe, it, expect). Provide only the test code.`,

  'code-review': `Review this TypeScript code and identify issues with severity levels (critical/high/medium/low):

\`\`\`typescript
function processUserData(data: any) {
  const user = JSON.parse(data);
  const email = user.email.toLowerCase();

  fetch('https://api.example.com/users', {
    method: 'POST',
    body: JSON.stringify({ email })
  });

  return email;
}
\`\`\`

Provide feedback on:
1. Type safety issues
2. Error handling
3. Security concerns
4. Best practices violations

Format as a list of issues with severity and recommended fixes.`
};

function delay(ms: number): Promise<void> {
  return new Promise(resolve => setTimeout(resolve, ms));
}

function calculateStats(values: number[]): { mean: number; min: number; max: number; stdDev: number; confidence: 'high' | 'medium' | 'low' } {
  const n = values.length;
  if (n === 0) return { mean: 0, min: 0, max: 0, stdDev: 0, confidence: 'low' };

  const mean = values.reduce((sum, v) => sum + v, 0) / n;
  const min = Math.min(...values);
  const max = Math.max(...values);

  const variance = values.reduce((sum, v) => sum + Math.pow(v - mean, 2), 0) / n;
  const stdDev = Math.sqrt(variance);

  const confidence = stdDev < 1.0 ? 'high' : stdDev < 2.0 ? 'medium' : 'low';

  return {
    mean: Math.round(mean * 100) / 100,
    min: Math.round(min * 100) / 100,
    max: Math.round(max * 100) / 100,
    stdDev: Math.round(stdDev * 100) / 100,
    confidence
  };
}

export class BatchRunner {
  private config: Required<BatchConfig>;

  constructor(config: BatchConfig = {}) {
    this.config = {
      iterations: config.iterations || 3,
      scenarios: config.scenarios || Object.keys(SCENARIOS),
      providers: config.providers || [],
      delayMs: config.delayMs || 2000,
      outputDir: config.outputDir || './results',
      includePaid: config.includePaid || false
    };
  }

  async run(): Promise<BatchResults> {
    const startTime = Date.now();
    const results: TestResult[] = [];

    // Get available providers
    const allProviders = await getAvailableProviders();
    const providers = this.config.providers.length > 0
      ? allProviders.filter(p => this.config.providers.some(name =>
          p.name.toLowerCase().includes(name.toLowerCase())
        ))
      : allProviders;

    if (providers.length === 0) {
      throw new Error('No available providers found. Please set API keys or install Ollama.');
    }

    console.log(`\n${'='.repeat(80)}`);
    console.log(`Starting Batch Test Run`);
    console.log(`${'='.repeat(80)}`);
    console.log(`Providers: ${providers.map(p => p.name).join(', ')}`);
    console.log(`Scenarios: ${this.config.scenarios.join(', ')}`);
    console.log(`Iterations: ${this.config.iterations} per model/scenario`);
    console.log(`\nDiscovering available models...`);

    // Fetch models dynamically for each provider
    const providerModels = new Map<string, string[]>();
    let totalModelCount = 0;

    for (const provider of providers) {
      console.log(`  ${provider.name}: Fetching models...`);
      const models = await provider.getModels(this.config.includePaid);
      providerModels.set(provider.name, models);

      for (const model of models) {
        console.log(`    - ${model}`);
        totalModelCount++;
      }
    }

    const totalTests = totalModelCount * this.config.scenarios.length * this.config.iterations;
    console.log(`\nTotal: ${totalModelCount} models × ${this.config.scenarios.length} scenarios × ${this.config.iterations} iterations = ${totalTests} tests`);
    console.log(`${'='.repeat(80)}\n`);

    let testCount = 0;

    // Run tests
    for (const provider of providers) {
      const models = providerModels.get(provider.name) || [];
      for (const model of models) { // Test ALL discovered models per provider
        for (const scenario of this.config.scenarios) {
          const prompt = SCENARIOS[scenario as keyof typeof SCENARIOS];
          if (!prompt) continue;

          for (let i = 0; i < this.config.iterations; i++) {
            testCount++;
            console.log(`[${testCount}/${totalTests}] Testing ${provider.name} (${model}) - ${scenario} (iteration ${i + 1}/${this.config.iterations})`);

            try {
              const result = await provider.test(scenario, prompt, { model });
              results.push(result);

              console.log(`  ✓ Score: ${result.score.total}/10, Tokens: ${result.metrics.totalTokens}, Time: ${result.metrics.responseTimeMs}ms`);

              // Delay to avoid rate limits
              if (testCount < totalTests) {
                await delay(this.config.delayMs);
              }
            } catch (error) {
              console.error(`  ✗ Error: ${error instanceof Error ? error.message : String(error)}`);
            }
          }
        }
      }
    }

    const endTime = Date.now();

    // Aggregate results
    const aggregated = this.aggregateResults(results);

    // Save results
    await this.saveResults({
      config: this.config,
      startTime: new Date(startTime).toISOString(),
      endTime: new Date(endTime).toISOString(),
      durationMs: endTime - startTime,
      results,
      aggregated
    });

    return {
      config: this.config,
      startTime: new Date(startTime).toISOString(),
      endTime: new Date(endTime).toISOString(),
      durationMs: endTime - startTime,
      results,
      aggregated
    };
  }

  private aggregateResults(results: TestResult[]): AggregatedResults[] {
    const grouped = new Map<string, TestResult[]>();

    // Group by provider-model-scenario
    for (const result of results) {
      const key = `${result.provider}|${result.model}|${result.scenario}`;
      if (!grouped.has(key)) {
        grouped.set(key, []);
      }
      grouped.get(key)!.push(result);
    }

    // Calculate aggregates
    const aggregated: AggregatedResults[] = [];

    for (const [key, group] of grouped.entries()) {
      const [provider, model, scenario] = key.split('|');

      const scores = group.map(r => r.score.total);
      const inputTokens = group.map(r => r.metrics.inputTokens);
      const outputTokens = group.map(r => r.metrics.outputTokens);
      const responseTimes = group.map(r => r.metrics.responseTimeMs);
      const totalCost = group.reduce((sum, r) => sum + r.metrics.estimatedCost, 0);

      aggregated.push({
        provider,
        model,
        scenario,
        iterations: group.length,
        scores: calculateStats(scores),
        metrics: {
          meanInputTokens: Math.round(inputTokens.reduce((sum, v) => sum + v, 0) / inputTokens.length),
          meanOutputTokens: Math.round(outputTokens.reduce((sum, v) => sum + v, 0) / outputTokens.length),
          meanResponseTimeMs: Math.round(responseTimes.reduce((sum, v) => sum + v, 0) / responseTimes.length),
          totalCost: Math.round(totalCost * 1000000) / 1000000
        },
        individualScores: scores
      });
    }

    return aggregated.sort((a, b) => b.scores.mean - a.scores.mean);
  }

  private async saveResults(results: BatchResults): Promise<void> {
    // Create output directory
    await mkdir(this.config.outputDir, { recursive: true });

    // Save full results
    const timestamp = new Date().toISOString().replace(/[:.]/g, '-');
    const filename = `batch-results-${timestamp}.json`;
    const filepath = join(this.config.outputDir, filename);

    await writeFile(filepath, JSON.stringify(results, null, 2));

    console.log(`\n✓ Results saved to: ${filename}`);
  }
}
