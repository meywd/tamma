/**
 * AI Provider POC Test Script
 *
 * Purpose: Test AI providers using free tier APIs to validate Story 1.0 research findings
 *
 * Usage:
 *   tsx test-providers-poc.ts --provider gemini --scenario issue-analysis
 *   tsx test-providers-poc.ts --provider openai --scenario code-generation
 *
 * Environment Variables:
 *   GOOGLE_AI_API_KEY - Google Gemini API key
 *   OPENAI_API_KEY - OpenAI API key
 *   ANTHROPIC_API_KEY - Anthropic API key
 */

import { writeFile } from 'fs/promises';
import { join } from 'path';

// ============================================================================
// Types
// ============================================================================

interface TestScenario {
  name: string;
  prompt: string;
  expectedOutputType: string;
}

interface TestResult {
  testRun: {
    timestamp: string;
    scenario: string;
    provider: string;
  };
  metrics: {
    inputTokens: number;
    outputTokens: number;
    totalTokens: number;
    responseTimeMs: number;
    estimatedCost: number;
  };
  quality: {
    score: number;
    notes: string;
  };
  response: string;
  rawResponse?: unknown;
}

// ============================================================================
// Test Scenarios
// ============================================================================

const SCENARIOS: Record<string, TestScenario> = {
  'issue-analysis': {
    name: 'Issue Analysis',
    prompt: `Analyze the following GitHub issue and provide:
1. A clear breakdown of requirements
2. Any ambiguities that need clarification
3. Scope boundaries (what's included/excluded)

Issue:
Title: Add user authentication with JWT tokens
Description: We need to implement JWT-based authentication for our API. Users should be able to register, login, and receive a JWT token that they can use for authenticated requests. The token should expire after 24 hours.

Provide your analysis in a structured format.`,
    expectedOutputType: 'structured requirements analysis'
  },

  'code-generation': {
    name: 'Code Generation',
    prompt: `Write a TypeScript function that validates a JWT token. Requirements:
- Function name: validateJwtToken
- Input: token (string), secret (string)
- Output: decoded payload object or throws error
- Use error handling for invalid/expired tokens
- Include TypeScript types
- No external libraries (use only Node.js built-ins)

Provide only the code, no explanations.`,
    expectedOutputType: 'TypeScript code'
  },

  'test-generation': {
    name: 'Test Generation',
    prompt: `Generate Vitest unit tests for this TypeScript function:

\`\`\`typescript
function validateJwtToken(token: string, secret: string): Record<string, unknown> {
  if (!token || !secret) {
    throw new Error('Token and secret are required');
  }

  const parts = token.split('.');
  if (parts.length !== 3) {
    throw new Error('Invalid token format');
  }

  // Simple validation (not production-ready)
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
    expectedOutputType: 'Vitest test code'
  },

  'code-review': {
    name: 'Code Review',
    prompt: `Review this TypeScript code and identify issues with severity levels (critical/high/medium/low):

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

Format as a list of issues with severity and recommended fixes.`,
    expectedOutputType: 'code review findings'
  }
};

// ============================================================================
// Provider Implementations
// ============================================================================

async function testGoogleGemini(scenario: TestScenario): Promise<TestResult> {
  const apiKey = process.env.GOOGLE_AI_API_KEY;
  if (!apiKey) {
    throw new Error('GOOGLE_AI_API_KEY environment variable not set');
  }

  const startTime = Date.now();

  // Google Gemini API endpoint
  const url = `https://generativelanguage.googleapis.com/v1beta/models/gemini-1.5-flash:generateContent?key=${apiKey}`;

  const response = await fetch(url, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json'
    },
    body: JSON.stringify({
      contents: [{
        parts: [{
          text: scenario.prompt
        }]
      }],
      generationConfig: {
        temperature: 0.7,
        maxOutputTokens: 2048
      }
    })
  });

  if (!response.ok) {
    const errorText = await response.text();
    throw new Error(`Gemini API error: ${response.status} ${errorText}`);
  }

  const data = await response.json();
  const responseTimeMs = Date.now() - startTime;

  // Extract response text
  const text = data.candidates?.[0]?.content?.parts?.[0]?.text || '';

  // Extract token counts
  const inputTokens = data.usageMetadata?.promptTokenCount || 0;
  const outputTokens = data.usageMetadata?.candidatesTokenCount || 0;
  const totalTokens = data.usageMetadata?.totalTokenCount || 0;

  // Calculate cost (Gemini Flash pricing: $0.075/MTok input, $0.30/MTok output)
  const estimatedCost = (inputTokens / 1_000_000 * 0.075) + (outputTokens / 1_000_000 * 0.30);

  return {
    testRun: {
      timestamp: new Date().toISOString(),
      scenario: scenario.name,
      provider: 'google-gemini-flash'
    },
    metrics: {
      inputTokens,
      outputTokens,
      totalTokens,
      responseTimeMs,
      estimatedCost
    },
    quality: {
      score: 0, // To be manually assessed
      notes: 'Quality assessment pending manual review'
    },
    response: text,
    rawResponse: data
  };
}

async function testOpenAI(scenario: TestScenario): Promise<TestResult> {
  const apiKey = process.env.OPENAI_API_KEY;
  if (!apiKey) {
    throw new Error('OPENAI_API_KEY environment variable not set');
  }

  const startTime = Date.now();

  const response = await fetch('https://api.openai.com/v1/chat/completions', {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      'Authorization': `Bearer ${apiKey}`
    },
    body: JSON.stringify({
      model: 'gpt-4o-mini',
      messages: [{
        role: 'user',
        content: scenario.prompt
      }],
      temperature: 0.7,
      max_tokens: 2048
    })
  });

  if (!response.ok) {
    const errorText = await response.text();
    throw new Error(`OpenAI API error: ${response.status} ${errorText}`);
  }

  const data = await response.json();
  const responseTimeMs = Date.now() - startTime;

  const text = data.choices?.[0]?.message?.content || '';
  const inputTokens = data.usage?.prompt_tokens || 0;
  const outputTokens = data.usage?.completion_tokens || 0;
  const totalTokens = data.usage?.total_tokens || 0;

  // GPT-4o-mini pricing: $0.15/MTok input, $0.60/MTok output
  const estimatedCost = (inputTokens / 1_000_000 * 0.15) + (outputTokens / 1_000_000 * 0.60);

  return {
    testRun: {
      timestamp: new Date().toISOString(),
      scenario: scenario.name,
      provider: 'openai-gpt-4o-mini'
    },
    metrics: {
      inputTokens,
      outputTokens,
      totalTokens,
      responseTimeMs,
      estimatedCost
    },
    quality: {
      score: 0,
      notes: 'Quality assessment pending manual review'
    },
    response: text,
    rawResponse: data
  };
}

async function testAnthropic(scenario: TestScenario): Promise<TestResult> {
  const apiKey = process.env.ANTHROPIC_API_KEY;
  if (!apiKey) {
    throw new Error('ANTHROPIC_API_KEY environment variable not set');
  }

  const startTime = Date.now();

  const response = await fetch('https://api.anthropic.com/v1/messages', {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      'x-api-key': apiKey,
      'anthropic-version': '2023-06-01'
    },
    body: JSON.stringify({
      model: 'claude-3-haiku-20240307',
      max_tokens: 2048,
      messages: [{
        role: 'user',
        content: scenario.prompt
      }]
    })
  });

  if (!response.ok) {
    const errorText = await response.text();
    throw new Error(`Anthropic API error: ${response.status} ${errorText}`);
  }

  const data = await response.json();
  const responseTimeMs = Date.now() - startTime;

  const text = data.content?.[0]?.text || '';
  const inputTokens = data.usage?.input_tokens || 0;
  const outputTokens = data.usage?.output_tokens || 0;
  const totalTokens = inputTokens + outputTokens;

  // Claude 3 Haiku pricing: $0.25/MTok input, $1.25/MTok output
  const estimatedCost = (inputTokens / 1_000_000 * 0.25) + (outputTokens / 1_000_000 * 1.25);

  return {
    testRun: {
      timestamp: new Date().toISOString(),
      scenario: scenario.name,
      provider: 'anthropic-claude-haiku'
    },
    metrics: {
      inputTokens,
      outputTokens,
      totalTokens,
      responseTimeMs,
      estimatedCost
    },
    quality: {
      score: 0,
      notes: 'Quality assessment pending manual review'
    },
    response: text,
    rawResponse: data
  };
}

// ============================================================================
// Main Runner
// ============================================================================

async function runTest(providerName: string, scenarioName: string): Promise<void> {
  const scenario = SCENARIOS[scenarioName];
  if (!scenario) {
    throw new Error(`Unknown scenario: ${scenarioName}. Available: ${Object.keys(SCENARIOS).join(', ')}`);
  }

  console.log(`\n${'='.repeat(80)}`);
  console.log(`Running test: ${scenario.name} with ${providerName}`);
  console.log(`${'='.repeat(80)}\n`);

  let result: TestResult;

  switch (providerName) {
    case 'gemini':
      result = await testGoogleGemini(scenario);
      break;
    case 'openai':
      result = await testOpenAI(scenario);
      break;
    case 'anthropic':
      result = await testAnthropic(scenario);
      break;
    default:
      throw new Error(`Unknown provider: ${providerName}. Available: gemini, openai, anthropic`);
  }

  // Display results
  console.log('Results:');
  console.log(`  Provider: ${result.testRun.provider}`);
  console.log(`  Scenario: ${result.testRun.scenario}`);
  console.log(`  Timestamp: ${result.testRun.timestamp}`);
  console.log(`\nMetrics:`);
  console.log(`  Input Tokens: ${result.metrics.inputTokens}`);
  console.log(`  Output Tokens: ${result.metrics.outputTokens}`);
  console.log(`  Total Tokens: ${result.metrics.totalTokens}`);
  console.log(`  Response Time: ${result.metrics.responseTimeMs}ms`);
  console.log(`  Estimated Cost: $${result.metrics.estimatedCost.toFixed(6)}`);
  console.log(`\nResponse Preview (first 500 chars):`);
  console.log(result.response.substring(0, 500));
  if (result.response.length > 500) {
    console.log(`\n... (${result.response.length - 500} more characters)`);
  }

  // Save results to file
  const timestamp = new Date().toISOString().replace(/[:.]/g, '-');
  const filename = `result-${providerName}-${scenarioName}-${timestamp}.json`;
  const filepath = join(__dirname, 'results', filename);

  await writeFile(filepath, JSON.stringify(result, null, 2));
  console.log(`\n✓ Results saved to: ${filename}`);
}

// ============================================================================
// CLI Entry Point
// ============================================================================

async function main() {
  const args = process.argv.slice(2);

  // Parse arguments
  let provider = 'gemini'; // default
  let scenario = 'issue-analysis'; // default

  for (let i = 0; i < args.length; i++) {
    if (args[i] === '--provider' && args[i + 1]) {
      provider = args[i + 1];
      i++;
    } else if (args[i] === '--scenario' && args[i + 1]) {
      scenario = args[i + 1];
      i++;
    } else if (args[i] === '--help' || args[i] === '-h') {
      console.log(`
AI Provider POC Test Script

Usage:
  tsx test-providers-poc.ts [options]

Options:
  --provider <name>    Provider to test (gemini, openai, anthropic)
  --scenario <name>    Scenario to test (issue-analysis, code-generation, test-generation, code-review)
  --help, -h           Show this help message

Examples:
  tsx test-providers-poc.ts --provider gemini --scenario issue-analysis
  tsx test-providers-poc.ts --provider openai --scenario code-generation

Environment Variables:
  GOOGLE_AI_API_KEY    Google Gemini API key
  OPENAI_API_KEY       OpenAI API key
  ANTHROPIC_API_KEY    Anthropic API key
`);
      return;
    }
  }

  try {
    await runTest(provider, scenario);
  } catch (error) {
    console.error('\n❌ Error:', error instanceof Error ? error.message : String(error));
    process.exit(1);
  }
}

// Run if executed directly
if (import.meta.url === `file://${process.argv[1]}`) {
  main();
}
