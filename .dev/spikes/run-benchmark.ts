#!/usr/bin/env tsx
/**
 * AI Provider Benchmark CLI
 *
 * Comprehensive test bed for evaluating AI providers across standardized scenarios
 *
 * Usage:
 *   tsx run-benchmark.ts                          # Run full benchmark (all providers, 3 iterations)
 *   tsx run-benchmark.ts --providers gemini,openai   # Test specific providers
 *   tsx run-benchmark.ts --scenarios code-generation # Test specific scenario
 *   tsx run-benchmark.ts --iterations 5           # Run 5 iterations per test
 *   tsx run-benchmark.ts --quick                  # Quick test (1 iteration, free providers only)
 */

import { BatchRunner } from './runners/batch-runner.js';
import { ComparisonReporter } from './reporters/comparison-reporter.js';

interface CliArgs {
  providers?: string[];
  scenarios?: string[];
  iterations?: number;
  quick?: boolean;
  help?: boolean;
}

function parseArgs(argv: string[]): CliArgs {
  const args: CliArgs = {};

  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i];

    if (arg === '--help' || arg === '-h') {
      args.help = true;
    } else if (arg === '--quick') {
      args.quick = true;
    } else if (arg === '--providers' && argv[i + 1]) {
      args.providers = argv[i + 1].split(',').map(s => s.trim());
      i++;
    } else if (arg === '--scenarios' && argv[i + 1]) {
      args.scenarios = argv[i + 1].split(',').map(s => s.trim());
      i++;
    } else if (arg === '--iterations' && argv[i + 1]) {
      args.iterations = parseInt(argv[i + 1], 10);
      i++;
    }
  }

  return args;
}

function showHelp() {
  console.log(`
AI Provider Benchmark Suite
============================

A comprehensive test bed for evaluating AI providers across standardized scenarios.

Usage:
  tsx run-benchmark.ts [options]

Options:
  --providers <names>     Comma-separated list of providers to test
                          Example: --providers gemini,openai,anthropic
                          Available: gemini, openai, anthropic, openrouter, ollama

  --scenarios <names>     Comma-separated list of scenarios to test
                          Example: --scenarios code-generation,test-generation
                          Available: issue-analysis, code-generation, test-generation, code-review

  --iterations <n>        Number of test iterations per provider/scenario (default: 3)
                          Example: --iterations 5

  --quick                 Quick test mode: 1 iteration, free providers only
                          (gemini, openrouter, ollama)

  --help, -h              Show this help message

Examples:
  # Full benchmark with all available providers
  tsx run-benchmark.ts

  # Quick test with free providers only
  tsx run-benchmark.ts --quick

  # Test specific providers
  tsx run-benchmark.ts --providers gemini,openai

  # Test specific scenario
  tsx run-benchmark.ts --scenarios code-generation --iterations 5

  # Test Ollama only (local models)
  tsx run-benchmark.ts --providers ollama

Environment Variables:
  GOOGLE_AI_API_KEY       Google Gemini API key (get from https://aistudio.google.com/app/apikey)
  OPENAI_API_KEY          OpenAI API key (get from https://platform.openai.com/api-keys)
  ANTHROPIC_API_KEY       Anthropic API key (get from https://console.anthropic.com/)
  OPENROUTER_API_KEY      OpenRouter API key (get from https://openrouter.ai/)
  OLLAMA_ENABLED          Set to 'true' to enable Ollama (requires local installation)

Output:
  - results/batch-results-<timestamp>.json   - Full test results in JSON
  - results/benchmark-report-<timestamp>.md  - Comparison report in Markdown

Tips:
  - Start with --quick to test setup before running full benchmark
  - Use --providers ollama to test locally without API costs
  - Set API keys in .env file (see .env.example)
  - Allow 2-5 minutes per provider (rate limiting delays)
`);
}

async function main() {
  const args = parseArgs(process.argv.slice(2));

  if (args.help) {
    showHelp();
    return;
  }

  console.log(`
╔════════════════════════════════════════════════════════════════════════════╗
║                   AI Provider Benchmark Suite                              ║
║                                                                            ║
║  Tests multiple AI providers across standardized scenarios with           ║
║  automated quality scoring and statistical analysis.                      ║
╚════════════════════════════════════════════════════════════════════════════╝
`);

  // Quick mode overrides
  if (args.quick) {
    console.log('Running in QUICK mode: 1 iteration, free providers only\n');
    args.iterations = 1;
    args.providers = args.providers || ['gemini', 'openrouter', 'ollama'];
  }

  // Create batch runner
  const runner = new BatchRunner({
    iterations: args.iterations,
    scenarios: args.scenarios,
    providers: args.providers,
    delayMs: args.quick ? 1000 : 2000,
    outputDir: './results'
  });

  try {
    // Run tests
    const results = await runner.run();

    console.log(`\n${'='.repeat(80)}`);
    console.log(`Batch Test Complete!`);
    console.log(`${'='.repeat(80)}`);
    console.log(`Total tests: ${results.results.length}`);
    console.log(`Duration: ${Math.round(results.durationMs / 1000)}s`);
    console.log(`${'='.repeat(80)}\n`);

    // Generate comparison report
    console.log('Generating comparison report...');
    const reporter = new ComparisonReporter({
      outputDir: './results',
      includeRawData: !args.quick,
      sortBy: 'score'
    });

    await reporter.generate(results);

    // Show quick summary
    console.log('\n📊 Quick Summary:');
    console.log(`   Best Overall: ${results.aggregated[0].provider} (${results.aggregated[0].model}) - ${results.aggregated[0].scores.mean}/10`);

    const cheapest = [...results.aggregated].sort((a, b) => a.metrics.totalCost - b.metrics.totalCost)[0];
    console.log(`   Most Cost-Effective: ${cheapest.provider} (${cheapest.model}) - $${cheapest.metrics.totalCost.toFixed(6)}`);

    const fastest = [...results.aggregated].sort((a, b) => a.metrics.meanResponseTimeMs - b.metrics.meanResponseTimeMs)[0];
    console.log(`   Fastest: ${fastest.provider} (${fastest.model}) - ${fastest.metrics.meanResponseTimeMs}ms`);

    console.log('\n✅ Benchmark complete! Check the results/ directory for detailed reports.\n');

  } catch (error) {
    console.error('\n❌ Benchmark failed:');
    console.error(error instanceof Error ? error.message : String(error));
    console.error('\nTips:');
    console.error('  - Make sure API keys are set in environment variables or .env file');
    console.error('  - For Ollama, make sure it\'s running: ollama serve');
    console.error('  - Try --quick mode to test with free providers only');
    console.error('  - Run with --help for more information\n');
    process.exit(1);
  }
}

// Run if executed directly
if (import.meta.url === `file://${process.argv[1]}`) {
  main();
}
