/**
 * Unified Scoring Interface
 *
 * Provides a single entry point for all scenario scorers
 */

export { scoreIssueAnalysis, type IssueAnalysisScore } from './issue-analysis-scorer.js';
export { scoreCodeGeneration, type CodeGenerationScore } from './code-generation-scorer.js';
export { scoreTestGeneration, type TestGenerationScore } from './test-generation-scorer.js';
export { scoreCodeReview, type CodeReviewScore } from './code-review-scorer.js';

export type ScenarioType = 'issue-analysis' | 'code-generation' | 'test-generation' | 'code-review';

export interface UnifiedScore {
  scenario: ScenarioType;
  total: number; // 0-10
  breakdown: Record<string, number>;
  details: string[];
}

export function scoreResponse(scenario: ScenarioType, response: string): UnifiedScore {
  switch (scenario) {
    case 'issue-analysis': {
      const { scoreIssueAnalysis } = require('./issue-analysis-scorer.js');
      const score = scoreIssueAnalysis(response);
      return {
        scenario,
        total: score.total,
        breakdown: score.breakdown,
        details: score.details
      };
    }

    case 'code-generation': {
      const { scoreCodeGeneration } = require('./code-generation-scorer.js');
      const score = scoreCodeGeneration(response);
      return {
        scenario,
        total: score.total,
        breakdown: score.breakdown,
        details: score.details
      };
    }

    case 'test-generation': {
      const { scoreTestGeneration } = require('./test-generation-scorer.js');
      const score = scoreTestGeneration(response);
      return {
        scenario,
        total: score.total,
        breakdown: score.breakdown,
        details: score.details
      };
    }

    case 'code-review': {
      const { scoreCodeReview } = require('./code-review-scorer.js');
      const score = scoreCodeReview(response);
      return {
        scenario,
        total: score.total,
        breakdown: score.breakdown,
        details: score.details
      };
    }

    default:
      throw new Error(`Unknown scenario: ${scenario}`);
  }
}

/**
 * Calculate aggregate score across all scenarios
 */
export function calculateAggregateScore(scores: UnifiedScore[]): {
  overallScore: number;
  scenarioScores: Record<ScenarioType, number>;
  totalTests: number;
} {
  const scenarioScores: Partial<Record<ScenarioType, number>> = {};

  for (const score of scores) {
    scenarioScores[score.scenario] = score.total;
  }

  const totalScore = scores.reduce((sum, score) => sum + score.total, 0);
  const overallScore = scores.length > 0 ? totalScore / scores.length : 0;

  return {
    overallScore: Math.round(overallScore * 10) / 10,
    scenarioScores: scenarioScores as Record<ScenarioType, number>,
    totalTests: scores.length
  };
}
