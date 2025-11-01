/**
 * Comparison Reporter
 *
 * Generates markdown comparison tables and analysis from batch results
 */

import { writeFile } from 'fs/promises';
import { join } from 'path';
import type { BatchResults, AggregatedResults } from '../runners/batch-runner.js';

export interface ReportConfig {
  outputDir?: string;
  includeRawData?: boolean;
  sortBy?: 'score' | 'cost' | 'speed';
}

export class ComparisonReporter {
  private config: Required<ReportConfig>;

  constructor(config: ReportConfig = {}) {
    this.config = {
      outputDir: config.outputDir || './results',
      includeRawData: config.includeRawData ?? false,
      sortBy: config.sortBy || 'score'
    };
  }

  async generate(results: BatchResults): Promise<void> {
    const sections: string[] = [];

    // Title and metadata
    sections.push('# AI Provider Benchmark Results\n');
    sections.push(`**Test Date**: ${results.startTime}`);
    sections.push(`**Duration**: ${Math.round(results.durationMs / 1000)}s`);
    sections.push(`**Total Tests**: ${results.results.length}`);
    sections.push(`**Iterations per Test**: ${results.config.iterations}`);
    sections.push('\n---\n');

    // Executive Summary
    sections.push(this.generateExecutiveSummary(results));

    // Overall Rankings
    sections.push(this.generateOverallRankings(results));

    // Per-Scenario Analysis
    sections.push(this.generateScenarioAnalysis(results));

    // Cost Analysis
    sections.push(this.generateCostAnalysis(results));

    // Performance Analysis
    sections.push(this.generatePerformanceAnalysis(results));

    // Recommendations
    sections.push(this.generateRecommendations(results));

    // Full Data Table
    if (this.config.includeRawData) {
      sections.push(this.generateFullDataTable(results));
    }

    const markdown = sections.join('\n');

    // Save report
    const timestamp = new Date(results.startTime).toISOString().replace(/[:.]/g, '-');
    const filename = `benchmark-report-${timestamp}.md`;
    const filepath = join(this.config.outputDir, filename);

    await writeFile(filepath, markdown);

    console.log(`\n✓ Comparison report saved to: ${filename}`);
  }

  private generateExecutiveSummary(results: BatchResults): string {
    const sections: string[] = [];

    sections.push('## Executive Summary\n');

    // Find best performers
    const bestOverall = results.aggregated[0];
    const cheapest = [...results.aggregated].sort((a, b) => a.metrics.totalCost - b.metrics.totalCost)[0];
    const fastest = [...results.aggregated].sort((a, b) => a.metrics.meanResponseTimeMs - b.metrics.meanResponseTimeMs)[0];

    sections.push(`### Best Overall Quality`);
    sections.push(`**${bestOverall.provider}** (${bestOverall.model}) - ${bestOverall.scores.mean}/10 average score`);
    sections.push(`- Confidence: ${bestOverall.scores.confidence}`);
    sections.push(`- Range: ${bestOverall.scores.min}-${bestOverall.scores.max}`);
    sections.push('');

    sections.push(`### Most Cost-Effective`);
    sections.push(`**${cheapest.provider}** (${cheapest.model}) - $${cheapest.metrics.totalCost.toFixed(6)} total cost`);
    sections.push('');

    sections.push(`### Fastest Response`);
    sections.push(`**${fastest.provider}** (${fastest.model}) - ${fastest.metrics.meanResponseTimeMs}ms average`);
    sections.push('');

    return sections.join('\n');
  }

  private generateOverallRankings(results: BatchResults): string {
    const sections: string[] = [];

    sections.push('## Overall Rankings\n');
    sections.push('Ranked by mean quality score across all scenarios:\n');

    sections.push('| Rank | Provider | Model | Score | Confidence | Cost | Speed (ms) |');
    sections.push('|------|----------|-------|-------|------------|------|------------|');

    let rank = 1;
    for (const result of results.aggregated.slice(0, 10)) {
      sections.push(
        `| ${rank} | ${result.provider} | ${result.model} | ` +
        `${result.scores.mean}/10 (±${result.scores.stdDev}) | ${result.scores.confidence} | ` +
        `$${result.metrics.totalCost.toFixed(6)} | ${result.metrics.meanResponseTimeMs}ms |`
      );
      rank++;
    }

    sections.push('');
    return sections.join('\n');
  }

  private generateScenarioAnalysis(results: BatchResults): string {
    const sections: string[] = [];

    sections.push('## Per-Scenario Analysis\n');

    const scenarios = [...new Set(results.aggregated.map(r => r.scenario))];

    for (const scenario of scenarios) {
      const scenarioResults = results.aggregated
        .filter(r => r.scenario === scenario)
        .sort((a, b) => b.scores.mean - a.scores.mean);

      sections.push(`### ${scenario.charAt(0).toUpperCase() + scenario.slice(1).replace('-', ' ')}\n`);

      sections.push('| Provider | Model | Score | Tokens (in/out) | Cost | Time |');
      sections.push('|----------|-------|-------|-----------------|------|------|');

      for (const result of scenarioResults.slice(0, 5)) {
        sections.push(
          `| ${result.provider} | ${result.model} | ${result.scores.mean}/10 | ` +
          `${result.metrics.meanInputTokens}/${result.metrics.meanOutputTokens} | ` +
          `$${result.metrics.totalCost.toFixed(6)} | ${result.metrics.meanResponseTimeMs}ms |`
        );
      }

      sections.push('');
    }

    return sections.join('\n');
  }

  private generateCostAnalysis(results: BatchResults): string {
    const sections: string[] = [];

    sections.push('## Cost Analysis\n');

    const costRanked = [...results.aggregated].sort((a, b) => a.metrics.totalCost - b.metrics.totalCost);

    sections.push('### Most Cost-Effective (by total cost for test suite)\n');

    sections.push('| Provider | Model | Total Cost | Cost per Test | Score | Cost/Quality Ratio |');
    sections.push('|----------|-------|------------|---------------|-------|-------------------|');

    for (const result of costRanked.slice(0, 10)) {
      const costPerTest = result.metrics.totalCost / result.iterations;
      const costQualityRatio = result.scores.mean > 0 ? result.metrics.totalCost / result.scores.mean : 0;

      sections.push(
        `| ${result.provider} | ${result.model} | $${result.metrics.totalCost.toFixed(6)} | ` +
        `$${costPerTest.toFixed(6)} | ${result.scores.mean}/10 | $${costQualityRatio.toFixed(6)} |`
      );
    }

    sections.push('');
    sections.push('*Lower Cost/Quality Ratio is better*\n');

    return sections.join('\n');
  }

  private generatePerformanceAnalysis(results: BatchResults): string {
    const sections: string[] = [];

    sections.push('## Performance Analysis\n');

    const speedRanked = [...results.aggregated].sort((a, b) => a.metrics.meanResponseTimeMs - b.metrics.meanResponseTimeMs);

    sections.push('### Fastest Providers\n');

    sections.push('| Provider | Model | Avg Response Time | Score | Efficiency Score |');
    sections.push('|----------|-------|-------------------|-------|------------------|');

    for (const result of speedRanked.slice(0, 10)) {
      // Efficiency = quality score / response time (higher is better)
      const efficiency = (result.scores.mean / (result.metrics.meanResponseTimeMs / 1000)).toFixed(2);

      sections.push(
        `| ${result.provider} | ${result.model} | ${result.metrics.meanResponseTimeMs}ms | ` +
        `${result.scores.mean}/10 | ${efficiency} pts/sec |`
      );
    }

    sections.push('');
    sections.push('*Efficiency Score = Quality Score / Response Time (seconds)*\n');

    return sections.join('\n');
  }

  private generateRecommendations(results: BatchResults): string {
    const sections: string[] = [];

    sections.push('## Recommendations\n');

    const bestQuality = results.aggregated[0];
    const cheapest = [...results.aggregated].sort((a, b) => a.metrics.totalCost - b.metrics.totalCost)[0];
    const fastest = [...results.aggregated].sort((a, b) => a.metrics.meanResponseTimeMs - b.metrics.meanResponseTimeMs)[0];

    // Best value (balance of cost and quality)
    const valueRanked = [...results.aggregated].map(r => ({
      ...r,
      value: r.metrics.totalCost > 0 ? r.scores.mean / (r.metrics.totalCost * 1000000) : r.scores.mean * 1000
    })).sort((a, b) => b.value - a.value);
    const bestValue = valueRanked[0];

    sections.push('### For Production Use\n');
    sections.push(`**${bestQuality.provider}** (${bestQuality.model})`);
    sections.push(`- Highest quality scores (${bestQuality.scores.mean}/10)`);
    sections.push(`- ${bestQuality.scores.confidence} confidence in consistency`);
    sections.push(`- Cost: $${bestQuality.metrics.totalCost.toFixed(6)} per test suite`);
    sections.push('');

    sections.push('### For Cost-Sensitive Applications\n');
    sections.push(`**${cheapest.provider}** (${cheapest.model})`);
    sections.push(`- Lowest cost ($${cheapest.metrics.totalCost.toFixed(6)} per test suite)`);
    sections.push(`- Quality score: ${cheapest.scores.mean}/10`);
    sections.push(`- ${cheapest.metrics.totalCost === 0 ? 'Free/Local execution' : 'Best cost/quality ratio'}`);
    sections.push('');

    sections.push('### For Low-Latency Applications\n');
    sections.push(`**${fastest.provider}** (${fastest.model})`);
    sections.push(`- Fastest response (${fastest.metrics.meanResponseTimeMs}ms average)`);
    sections.push(`- Quality score: ${fastest.scores.mean}/10`);
    sections.push('');

    sections.push('### Best Overall Value\n');
    sections.push(`**${bestValue.provider}** (${bestValue.model})`);
    sections.push(`- Optimal balance of cost, quality, and speed`);
    sections.push(`- Score: ${bestValue.scores.mean}/10`);
    sections.push(`- Cost: ${bestValue.metrics.totalCost === 0 ? 'Free' : `$${bestValue.metrics.totalCost.toFixed(6)}`}`);
    sections.push(`- Speed: ${bestValue.metrics.meanResponseTimeMs}ms`);
    sections.push('');

    return sections.join('\n');
  }

  private generateFullDataTable(results: BatchResults): string {
    const sections: string[] = [];

    sections.push('## Full Test Data\n');

    sections.push('| Provider | Model | Scenario | Iteration | Score | Input | Output | Time | Cost |');
    sections.push('|----------|-------|----------|-----------|-------|-------|--------|------|------|');

    for (const result of results.results) {
      sections.push(
        `| ${result.provider} | ${result.model} | ${result.scenario} | - | ` +
        `${result.score.total}/10 | ${result.metrics.inputTokens} | ${result.metrics.outputTokens} | ` +
        `${result.metrics.responseTimeMs}ms | $${result.metrics.estimatedCost.toFixed(6)} |`
      );
    }

    sections.push('');
    return sections.join('\n');
  }
}
