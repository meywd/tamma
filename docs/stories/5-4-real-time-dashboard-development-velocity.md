# Story 5.4: Real-Time Dashboard - Development Velocity

**Epic**: Epic 5 - Observability & Production Readiness  
**Status**: Ready for Dev  
**MVP Priority**: Optional (Post-MVP Enhancement)  
**Estimated Complexity**: High  
**Target Implementation**: Sprint 6 (Post-MVP)

## Acceptance Criteria

### Development Velocity Dashboard

- [ ] **Real-time development metrics dashboard** showing team productivity and workflow efficiency
- [ ] **Multiple time range views** (daily, weekly, monthly, sprint-based) for velocity analysis
- [ ] **Team and individual views** with role-based access controls
- [ ] **Trend analysis** with predictive insights and anomaly detection
- [ ] **Comparative analysis** across teams, projects, and time periods

### Core Velocity Metrics

- [ ] **Issue completion rate** with breakdown by priority, complexity, and type
- [ ] **Cycle time analysis** from issue assignment to production deployment
- [ ] **Lead time tracking** from issue creation to resolution
- [ ] **Throughput metrics** showing issues completed per time period
- [ ] **Work in progress (WIP) limits** tracking and violation alerts
- [ ] **Code quality metrics** integration (test coverage, code review time, rework rate)

### Workflow Efficiency Metrics

- [ ] **Autonomous completion rate** percentage of issues completed without human intervention
- [ ] **Escalation frequency** and resolution time analysis
- [ ] **AI provider performance** comparison across different providers and models
- [ ] **Quality gate pass rates** and failure reason analysis
- [ ] **Rework frequency** and root cause analysis
- [ ] **Blocker identification** and resolution time tracking

### Team Performance Analytics

- [ ] **Individual contributor metrics** with privacy controls and team aggregation
- [ ] **Team velocity trends** with sprint-over-sprint comparisons
- [ ] **Capacity planning insights** based on historical velocity data
- [ ] **Skill gap analysis** based on issue type completion patterns
- [ ] **Collaboration metrics** showing cross-team dependencies and handoffs
- [ ] **Knowledge sharing indicators** from documentation and code review patterns

### Predictive Analytics

- [ ] **Velocity forecasting** using historical data and machine learning
- [ ] **Risk prediction** for sprint goals and project timelines
- [ ] **Bottleneck identification** with automated recommendations
- [ ] **Resource optimization suggestions** based on workload distribution
- [ ] **Quality trend prediction** for technical debt accumulation

## Technical Context

### Current System State

From `docs/stories/5-1-structured-logging-implementation.md`:

- Structured logging with workflow events and performance metrics
- Issue lifecycle tracking with detailed timing information
- AI provider interaction logging with cost and performance data

From `docs/stories/5-2-metrics-collection-infrastructure.md`:

- Prometheus metrics for workflow operations and system performance
- Custom metrics for issue processing, AI usage, and quality gates
- Time-series data storage for trend analysis

From `docs/stories/5-3-real-time-dashboard-system-health.md`:

- Dashboard framework with real-time WebSocket updates
- Authentication and authorization system
- Widget-based architecture for extensible visualizations

### Development Velocity Data Architecture

```typescript
// Velocity data flow architecture
┌─────────────────┐    ┌──────────────────┐    ┌─────────────────┐
│   Event Store   │    │  Velocity Engine │    │  Dashboard UI   │
│   (PostgreSQL)  │◄──►│   (Analytics)    │◄──►│   (React SPA)   │
│                 │    │                  │    │                 │
│ - Issue Events  │    │ - Data Aggregation│    │ - Velocity Charts│
│ - Workflow Data │    │ - Trend Analysis │    │ - Team Metrics  │
│ - AI Interactions│   │ - Predictions    │    │ - Predictions   │
│ - Quality Gates │    │ - Anomaly Detection│   │ - Insights     │
└─────────────────┘    └──────────────────┘    └─────────────────┘
         │                       │                       │
         └───────────────────────┼───────────────────────┘
                                 │
                    ┌──────────────────┐
                    │   Time Series    │
                    │   (Prometheus)   │
                    │                  │
                    │ - Metrics Data   │
                    │ - Performance    │
                    │ - System Health  │
                    └──────────────────┘
```

## Technical Implementation

### 1. Velocity Analytics Engine

#### Core Velocity Calculations

```typescript
// packages/velocity/src/types/velocity.types.ts
export interface VelocityMetrics {
  timeRange: TimeRange;
  teamId?: string;
  projectId?: string;
  individualId?: string;

  // Throughput metrics
  issuesCompleted: number;
  issuesCompletedPerDay: number;
  averageIssuesPerSprint: number;

  // Time-based metrics
  averageCycleTime: number; // hours
  averageLeadTime: number; // hours
  cycleTimePercentiles: Percentiles;
  leadTimePercentiles: Percentiles;

  // Quality metrics
  autonomousCompletionRate: number; // percentage
  escalationRate: number; // percentage
  reworkRate: number; // percentage
  qualityGatePassRate: number; // percentage

  // Efficiency metrics
  aiProviderPerformance: AIProviderMetrics[];
  bottleneckAnalysis: BottleneckAnalysis;
  wipLimitViolations: WIPViolation[];

  // Predictive metrics
  velocityForecast: VelocityForecast;
  riskIndicators: RiskIndicator[];
}

export interface TimeRange {
  start: Date;
  end: Date;
  type: 'day' | 'week' | 'month' | 'sprint';
}

export interface Percentiles {
  p50: number;
  p75: number;
  p90: number;
  p95: number;
  p99: number;
}

export interface AIProviderMetrics {
  provider: string;
  model: string;
  usageCount: number;
  averageResponseTime: number; // milliseconds
  successRate: number; // percentage
  costPerIssue: number; // currency
  qualityScore: number; // 1-10 rating
}

export interface BottleneckAnalysis {
  stage: string;
  averageWaitTime: number; // hours
  frequency: number; // occurrences
  impact: 'low' | 'medium' | 'high' | 'critical';
  recommendations: string[];
}

export interface VelocityForecast {
  predictedVelocity: number;
  confidence: number; // percentage
  factors: ForecastFactor[];
  timeToCompletion?: number; // days for current backlog
}

export interface ForecastFactor {
  factor: string;
  impact: number; // -1 to 1
  confidence: number; // percentage
}

export interface RiskIndicator {
  type: 'velocity-decline' | 'quality-degradation' | 'resource-constraint' | 'timeline-risk';
  severity: 'low' | 'medium' | 'high' | 'critical';
  probability: number; // percentage
  impact: string;
  mitigation: string[];
}
```

#### Velocity Analytics Service

```typescript
// packages/velocity/src/services/velocity-analytics.service.ts
import { EventStore } from '@tamma/events';
import { PrometheusService } from '@tamma/observability';
import { Logger } from 'pino';

export class VelocityAnalyticsService {
  constructor(
    private eventStore: EventStore,
    private prometheusService: PrometheusService,
    private logger: Logger
  ) {}

  async calculateVelocityMetrics(
    timeRange: TimeRange,
    filters: VelocityFilters = {}
  ): Promise<VelocityMetrics> {
    const { start, end } = timeRange;

    // Fetch relevant events
    const events = await this.eventStore.queryEvents({
      filters: {
        type: [
          'ISSUE.ASSIGNED',
          'ISSUE.STARTED',
          'ISSUE.COMPLETED',
          'WORKFLOW.STEP_COMPLETED',
          'AI.PROVIDER.USED',
          'QUALITY.GATE.RESULT',
          'ESCALATION.TRIGGERED',
          'CODE.REWORK.REQUIRED',
        ],
        timestamp: { gte: start.toISOString(), lte: end.toISOString() },
        ...filters,
      },
      orderBy: 'timestamp',
      order: 'asc',
    });

    // Calculate throughput metrics
    const throughputMetrics = await this.calculateThroughputMetrics(events, timeRange);

    // Calculate time-based metrics
    const timeMetrics = await this.calculateTimeMetrics(events);

    // Calculate quality metrics
    const qualityMetrics = await this.calculateQualityMetrics(events);

    // Calculate efficiency metrics
    const efficiencyMetrics = await this.calculateEfficiencyMetrics(events);

    // Generate predictive metrics
    const predictiveMetrics = await this.generatePredictiveMetrics(events, timeRange);

    return {
      timeRange,
      teamId: filters.teamId,
      projectId: filters.projectId,
      individualId: filters.individualId,
      ...throughputMetrics,
      ...timeMetrics,
      ...qualityMetrics,
      ...efficiencyMetrics,
      ...predictiveMetrics,
    };
  }

  private async calculateThroughputMetrics(
    events: any[],
    timeRange: TimeRange
  ): Promise<Partial<VelocityMetrics>> {
    const completedIssues = events.filter((e) => e.type === 'ISSUE.COMPLETED');
    const timeRangeDays =
      (timeRange.end.getTime() - timeRange.start.getTime()) / (1000 * 60 * 60 * 24);

    return {
      issuesCompleted: completedIssues.length,
      issuesCompletedPerDay: completedIssues.length / timeRangeDays,
      averageIssuesPerSprint: await this.calculateAverageIssuesPerSprint(completedIssues),
    };
  }

  private async calculateTimeMetrics(events: any[]): Promise<Partial<VelocityMetrics>> {
    const issueLifecycles = this.groupEventsByIssue(events);
    const cycleTimes: number[] = [];
    const leadTimes: number[] = [];

    for (const [issueId, issueEvents] of Object.entries(issueLifecycles)) {
      const assignedEvent = issueEvents.find((e) => e.type === 'ISSUE.ASSIGNED');
      const startedEvent = issueEvents.find((e) => e.type === 'ISSUE.STARTED');
      const completedEvent = issueEvents.find((e) => e.type === 'ISSUE.COMPLETED');

      if (assignedEvent && completedEvent) {
        const leadTime = this.calculateTimeDifference(
          assignedEvent.timestamp,
          completedEvent.timestamp
        );
        leadTimes.push(leadTime);
      }

      if (startedEvent && completedEvent) {
        const cycleTime = this.calculateTimeDifference(
          startedEvent.timestamp,
          completedEvent.timestamp
        );
        cycleTimes.push(cycleTime);
      }
    }

    return {
      averageCycleTime: this.average(cycleTimes),
      averageLeadTime: this.average(leadTimes),
      cycleTimePercentiles: this.calculatePercentiles(cycleTimes),
      leadTimePercentiles: this.calculatePercentiles(leadTimes),
    };
  }

  private async calculateQualityMetrics(events: any[]): Promise<Partial<VelocityMetrics>> {
    const totalIssues = new Set(
      events.filter((e) => e.type === 'ISSUE.ASSIGNED').map((e) => e.tags.issueId)
    ).size;

    const autonomousCompletions = events.filter(
      (e) => e.type === 'ISSUE.COMPLETED' && e.data.autonomous === true
    ).length;

    const escalations = events.filter((e) => e.type === 'ESCALATION.TRIGGERED').length;
    const reworks = events.filter((e) => e.type === 'CODE.REWORK.REQUIRED').length;

    const qualityGateResults = events.filter((e) => e.type === 'QUALITY.GATE.RESULT');
    const passedGates = qualityGateResults.filter((e) => e.data.status === 'passed').length;

    return {
      autonomousCompletionRate: totalIssues > 0 ? (autonomousCompletions / totalIssues) * 100 : 0,
      escalationRate: totalIssues > 0 ? (escalations / totalIssues) * 100 : 0,
      reworkRate: totalIssues > 0 ? (reworks / totalIssues) * 100 : 0,
      qualityGatePassRate:
        qualityGateResults.length > 0 ? (passedGates / qualityGateResults.length) * 100 : 0,
    };
  }

  private async calculateEfficiencyMetrics(events: any[]): Promise<Partial<VelocityMetrics>> {
    // AI Provider Performance
    const aiUsageEvents = events.filter((e) => e.type === 'AI.PROVIDER.USED');
    const aiProviderMetrics = this.calculateAIProviderMetrics(aiUsageEvents);

    // Bottleneck Analysis
    const workflowSteps = events.filter((e) => e.type === 'WORKFLOW.STEP_COMPLETED');
    const bottleneckAnalysis = await this.analyzeBottlenecks(workflowSteps);

    // WIP Limit Violations
    const wipViolations = await this.analyzeWIPLimitViolations(events);

    return {
      aiProviderPerformance: aiProviderMetrics,
      bottleneckAnalysis,
      wipLimitViolations,
    };
  }

  private async generatePredictiveMetrics(
    events: any[],
    timeRange: TimeRange
  ): Promise<Partial<VelocityMetrics>> {
    // Velocity Forecasting
    const velocityForecast = await this.forecastVelocity(events, timeRange);

    // Risk Analysis
    const riskIndicators = await this.analyzeRisks(events, timeRange);

    return {
      velocityForecast,
      riskIndicators,
    };
  }

  private calculateAIProviderMetrics(aiUsageEvents: any[]): AIProviderMetrics[] {
    const providerStats = new Map<string, any>();

    aiUsageEvents.forEach((event) => {
      const provider = event.tags.provider;
      const model = event.tags.model;
      const key = `${provider}:${model}`;

      if (!providerStats.has(key)) {
        providerStats.set(key, {
          provider,
          model,
          usageCount: 0,
          totalResponseTime: 0,
          successCount: 0,
          totalCost: 0,
          qualityScores: [],
        });
      }

      const stats = providerStats.get(key);
      stats.usageCount++;
      stats.totalResponseTime += event.data.responseTime || 0;
      stats.totalCost += event.data.cost || 0;

      if (event.data.success) {
        stats.successCount++;
      }

      if (event.data.qualityScore) {
        stats.qualityScores.push(event.data.qualityScore);
      }
    });

    return Array.from(providerStats.values()).map((stats) => ({
      provider: stats.provider,
      model: stats.model,
      usageCount: stats.usageCount,
      averageResponseTime: stats.totalResponseTime / stats.usageCount,
      successRate: (stats.successCount / stats.usageCount) * 100,
      costPerIssue: stats.totalCost / stats.usageCount,
      qualityScore: stats.qualityScores.length > 0 ? this.average(stats.qualityScores) : 0,
    }));
  }

  private async analyzeBottlenecks(workflowSteps: any[]): Promise<BottleneckAnalysis> {
    const stepDurations = new Map<string, number[]>();

    // Calculate duration for each workflow step
    for (let i = 1; i < workflowSteps.length; i++) {
      const currentStep = workflowSteps[i];
      const previousStep = workflowSteps[i - 1];

      if (currentStep.tags.issueId === previousStep.tags.issueId) {
        const duration = this.calculateTimeDifference(
          previousStep.timestamp,
          currentStep.timestamp
        );

        const stepName = currentStep.data.step;
        if (!stepDurations.has(stepName)) {
          stepDurations.set(stepName, []);
        }
        stepDurations.get(stepName)!.push(duration);
      }
    }

    // Identify bottlenecks
    const bottlenecks: BottleneckAnalysis[] = [];

    for (const [step, durations] of stepDurations.entries()) {
      const averageDuration = this.average(durations);
      const frequency = durations.length;

      // Determine impact based on duration and frequency
      let impact: 'low' | 'medium' | 'high' | 'critical' = 'low';

      if (averageDuration > 24 * 60 * 60 * 1000) {
        // > 24 hours
        impact = 'critical';
      } else if (averageDuration > 4 * 60 * 60 * 1000) {
        // > 4 hours
        impact = 'high';
      } else if (averageDuration > 60 * 60 * 1000) {
        // > 1 hour
        impact = 'medium';
      }

      if (impact !== 'low') {
        bottlenecks.push({
          stage: step,
          averageWaitTime: averageDuration / (1000 * 60 * 60), // hours
          frequency,
          impact,
          recommendations: this.generateBottleneckRecommendations(step, averageDuration),
        });
      }
    }

    // Return the most critical bottleneck
    return (
      bottlenecks.sort((a, b) => {
        const impactOrder = { critical: 4, high: 3, medium: 2, low: 1 };
        return impactOrder[b.impact] - impactOrder[a.impact];
      })[0] || {
        stage: 'None identified',
        averageWaitTime: 0,
        frequency: 0,
        impact: 'low',
        recommendations: [],
      }
    );
  }

  private async forecastVelocity(events: any[], timeRange: TimeRange): Promise<VelocityForecast> {
    // Get historical velocity data
    const historicalData = await this.getHistoricalVelocityData(events, timeRange);

    // Simple linear regression for forecasting
    const trend = this.calculateLinearTrend(historicalData);
    const seasonalFactors = this.calculateSeasonalFactors(historicalData);

    // Apply trend and seasonal adjustments
    const baseVelocity = this.average(historicalData.slice(-4)); // Last 4 periods
    const trendAdjustment = trend.slope * 1; // Next period
    const seasonalAdjustment = seasonalFactors[new Date().getMonth()] || 1;

    const predictedVelocity = Math.max(0, baseVelocity + trendAdjustment) * seasonalAdjustment;

    // Calculate confidence based on historical variance
    const variance = this.calculateVariance(historicalData);
    const confidence = Math.max(0, Math.min(100, 100 - (variance / baseVelocity) * 100));

    return {
      predictedVelocity,
      confidence,
      factors: [
        {
          factor: 'Historical Trend',
          impact: trend.slope / baseVelocity,
          confidence: trend.rSquared * 100,
        },
        {
          factor: 'Seasonal Pattern',
          impact: seasonalAdjustment - 1,
          confidence: 75,
        },
      ],
    };
  }

  private async analyzeRisks(events: any[], timeRange: TimeRange): Promise<RiskIndicator[]> {
    const risks: RiskIndicator[] = [];

    // Velocity decline risk
    const velocityTrend = await this.analyzeVelocityTrend(events, timeRange);
    if (velocityTrend.declineRate > 0.2) {
      risks.push({
        type: 'velocity-decline',
        severity: velocityTrend.declineRate > 0.4 ? 'high' : 'medium',
        probability: velocityTrend.confidence,
        impact: `Velocity declining by ${(velocityTrend.declineRate * 100).toFixed(1)}% per period`,
        mitigation: [
          'Review workload distribution',
          'Identify and address blockers',
          'Consider resource reallocation',
        ],
      });
    }

    // Quality degradation risk
    const qualityTrend = await this.analyzeQualityTrend(events, timeRange);
    if (qualityTrend.declineRate > 0.15) {
      risks.push({
        type: 'quality-degradation',
        severity: qualityTrend.declineRate > 0.3 ? 'high' : 'medium',
        probability: qualityTrend.confidence,
        impact: `Quality metrics declining by ${(qualityTrend.declineRate * 100).toFixed(1)}% per period`,
        mitigation: [
          'Increase code review coverage',
          'Enhance automated testing',
          'Provide additional training',
        ],
      });
    }

    return risks;
  }

  // Helper methods
  private groupEventsByIssue(events: any[]): Record<string, any[]> {
    return events.reduce(
      (groups, event) => {
        const issueId = event.tags.issueId;
        if (!groups[issueId]) {
          groups[issueId] = [];
        }
        groups[issueId].push(event);
        return groups;
      },
      {} as Record<string, any[]>
    );
  }

  private calculateTimeDifference(start: string, end: string): number {
    return new Date(end).getTime() - new Date(start).getTime();
  }

  private average(numbers: number[]): number {
    return numbers.length > 0 ? numbers.reduce((sum, n) => sum + n, 0) / numbers.length : 0;
  }

  private calculatePercentiles(numbers: number[]): Percentiles {
    if (numbers.length === 0) {
      return { p50: 0, p75: 0, p90: 0, p95: 0, p99: 0 };
    }

    const sorted = numbers.sort((a, b) => a - b);
    return {
      p50: this.getPercentile(sorted, 50),
      p75: this.getPercentile(sorted, 75),
      p90: this.getPercentile(sorted, 90),
      p95: this.getPercentile(sorted, 95),
      p99: this.getPercentile(sorted, 99),
    };
  }

  private getPercentile(sorted: number[], percentile: number): number {
    const index = (percentile / 100) * (sorted.length - 1);
    const lower = Math.floor(index);
    const upper = Math.ceil(index);

    if (lower === upper) {
      return sorted[lower];
    }

    const weight = index - lower;
    return sorted[lower] * (1 - weight) + sorted[upper] * weight;
  }

  private generateBottleneckRecommendations(step: string, duration: number): string[] {
    const recommendations: string[] = [];

    if (step.includes('AI') && duration > 10 * 60 * 1000) {
      // > 10 minutes
      recommendations.push('Consider using faster AI models');
      recommendations.push('Implement AI response caching');
      recommendations.push('Optimize prompt engineering');
    }

    if (step.includes('TEST') && duration > 30 * 60 * 1000) {
      // > 30 minutes
      recommendations.push('Parallelize test execution');
      recommendations.push('Optimize test suite performance');
      recommendations.push('Consider test suite splitting');
    }

    if (step.includes('REVIEW') && duration > 24 * 60 * 60 * 1000) {
      // > 24 hours
      recommendations.push('Implement automated code review');
      recommendations.push('Set up reviewer rotation');
      recommendations.push('Define clear review SLAs');
    }

    return recommendations;
  }

  private async calculateAverageIssuesPerSprint(completedIssues: any[]): Promise<number> {
    // Group completed issues by sprint (assuming 2-week sprints)
    const sprintGroups = new Map<string, number>();

    completedIssues.forEach((issue) => {
      const completedDate = new Date(issue.timestamp);
      const sprintNumber = Math.floor(completedDate.getTime() / (14 * 24 * 60 * 60 * 1000));
      const sprintKey = `sprint-${sprintNumber}`;

      sprintGroups.set(sprintKey, (sprintGroups.get(sprintKey) || 0) + 1);
    });

    const sprintCounts = Array.from(sprintGroups.values());
    return this.average(sprintCounts);
  }

  private async getHistoricalVelocityData(events: any[], timeRange: TimeRange): Promise<number[]> {
    // Group by week for historical analysis
    const weeklyData = new Map<string, number>();

    const completedIssues = events.filter((e) => e.type === 'ISSUE.COMPLETED');

    completedIssues.forEach((issue) => {
      const week = this.getWeekKey(new Date(issue.timestamp));
      weeklyData.set(week, (weeklyData.get(week) || 0) + 1);
    });

    return Array.from(weeklyData.values()).sort();
  }

  private calculateLinearTrend(data: number[]): { slope: number; rSquared: number } {
    if (data.length < 2) {
      return { slope: 0, rSquared: 0 };
    }

    const n = data.length;
    const x = Array.from({ length: n }, (_, i) => i);
    const y = data;

    const sumX = x.reduce((sum, val) => sum + val, 0);
    const sumY = y.reduce((sum, val) => sum + val, 0);
    const sumXY = x.reduce((sum, val, i) => sum + val * y[i], 0);
    const sumXX = x.reduce((sum, val) => sum + val * val, 0);

    const slope = (n * sumXY - sumX * sumY) / (n * sumXX - sumX * sumX);

    // Calculate R-squared
    const meanY = sumY / n;
    const totalSumSquares = y.reduce((sum, val) => sum + Math.pow(val - meanY, 2), 0);
    const residualSumSquares = y.reduce((sum, val, i) => {
      const predicted = slope * i + (sumY - slope * sumX) / n;
      return sum + Math.pow(val - predicted, 2);
    }, 0);

    const rSquared = 1 - residualSumSquares / totalSumSquares;

    return { slope, rSquared };
  }

  private calculateSeasonalFactors(data: number[]): Record<number, number> {
    // Simple seasonal adjustment based on month patterns
    // In a real implementation, this would use more sophisticated time series analysis
    const monthlyFactors: Record<number, number> = {
      0: 0.9, // January - post-holiday slowdown
      1: 0.95, // February
      2: 1.0, // March
      3: 1.05, // April
      4: 1.1, // May - pre-summer ramp up
      5: 1.05, // June
      6: 0.9, // July - summer vacation
      7: 0.85, // August - vacation peak
      8: 1.0, // September - back to school
      9: 1.1, // October - pre-holiday rush
      10: 1.15, // November - holiday prep
      11: 0.95, // December - holiday slowdown
    };

    return monthlyFactors;
  }

  private calculateVariance(data: number[]): number {
    if (data.length === 0) return 0;

    const mean = this.average(data);
    const squaredDiffs = data.map((value) => Math.pow(value - mean, 2));
    return this.average(squaredDiffs);
  }

  private getWeekKey(date: Date): string {
    const year = date.getFullYear();
    const week = Math.floor(
      (date.getTime() - new Date(year, 0, 1).getTime()) / (7 * 24 * 60 * 60 * 1000)
    );
    return `${year}-W${week}`;
  }

  private async analyzeVelocityTrend(
    events: any[],
    timeRange: TimeRange
  ): Promise<{ declineRate: number; confidence: number }> {
    const historicalData = await this.getHistoricalVelocityData(events, timeRange);
    const trend = this.calculateLinearTrend(historicalData);

    const averageVelocity = this.average(historicalData);
    const declineRate = averageVelocity > 0 ? Math.abs(trend.slope) / averageVelocity : 0;

    return {
      declineRate,
      confidence: trend.rSquared * 100,
    };
  }

  private async analyzeQualityTrend(
    events: any[],
    timeRange: TimeRange
  ): Promise<{ declineRate: number; confidence: number }> {
    // Group quality metrics by time period
    const qualityByPeriod = new Map<string, number[]>();

    const qualityEvents = events.filter((e) => e.type === 'QUALITY.GATE.RESULT');

    qualityEvents.forEach((event) => {
      const period = this.getWeekKey(new Date(event.timestamp));
      const score = event.data.status === 'passed' ? 1 : 0;

      if (!qualityByPeriod.has(period)) {
        qualityByPeriod.set(period, []);
      }
      qualityByPeriod.get(period)!.push(score);
    });

    const qualityScores = Array.from(qualityByPeriod.entries())
      .map(([_, scores]) => this.average(scores))
      .sort();

    const trend = this.calculateLinearTrend(qualityScores);
    const declineRate = Math.abs(trend.slope);

    return {
      declineRate,
      confidence: trend.rSquared * 100,
    };
  }

  private async analyzeWIPLimitViolations(events: any[]): Promise<WIPViolation[]> {
    // This would analyze work in progress limit violations
    // Implementation depends on specific WIP limit configuration
    return [];
  }
}
```

### 2. Velocity Dashboard Components

#### Velocity Overview Widget

```typescript
// packages/dashboard/src/components/widgets/VelocityOverviewWidget.tsx
import React from 'react';
import { Card, CardContent, CardHeader, CardTitle } from '../ui/card';
import { Badge } from '../ui/badge';
import { Progress } from '../ui/progress';
import { useRealTimeData } from '../../hooks/useRealTimeData';
import { VelocityMetrics } from '../../types/velocity.types';

export function VelocityOverviewWidget({ config }: { config: any }) {
  const { data, loading, error, lastUpdate } = useRealTimeData<VelocityMetrics>({
    type: 'velocity',
    endpoint: '/api/dashboard/velocity/overview',
    query: JSON.stringify(config.filters || {}),
    refreshInterval: 60000 // 1 minute
  });

  if (loading) return <div>Loading velocity metrics...</div>;
  if (error) return <div>Error: {error}</div>;
  if (!data) return <div>No velocity data available</div>;

  const getTrendIndicator = (current: number, previous: number) => {
    const change = ((current - previous) / previous) * 100;
    if (Math.abs(change) < 1) return null;

    return (
      <Badge variant={change > 0 ? 'default' : 'destructive'} className="text-xs">
        {change > 0 ? '↑' : '↓'} {Math.abs(change).toFixed(1)}%
      </Badge>
    );
  };

  const getQualityColor = (rate: number) => {
    if (rate >= 90) return 'text-green-600';
    if (rate >= 75) return 'text-yellow-600';
    return 'text-red-600';
  };

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center justify-between">
          Development Velocity Overview
          {lastUpdate && (
            <span className="text-xs text-gray-500">
              Updated: {lastUpdate.toLocaleTimeString()}
            </span>
          )}
        </CardTitle>
      </CardHeader>
      <CardContent>
        <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
          {/* Throughput Metrics */}
          <div className="space-y-2">
            <h4 className="font-semibold text-sm">Throughput</h4>
            <div className="space-y-1">
              <div className="flex justify-between items-center">
                <span className="text-xs">Issues Completed</span>
                <span className="font-mono text-sm">{data.issuesCompleted}</span>
              </div>
              <div className="flex justify-between items-center">
                <span className="text-xs">Per Day</span>
                <span className="font-mono text-sm">{data.issuesCompletedPerDay.toFixed(1)}</span>
              </div>
              <div className="flex justify-between items-center">
                <span className="text-xs">Per Sprint</span>
                <span className="font-mono text-sm">{data.averageIssuesPerSprint.toFixed(1)}</span>
              </div>
            </div>
          </div>

          {/* Time Metrics */}
          <div className="space-y-2">
            <h4 className="font-semibold text-sm">Cycle Time</h4>
            <div className="space-y-1">
              <div className="flex justify-between items-center">
                <span className="text-xs">Average</span>
                <span className="font-mono text-sm">{(data.averageCycleTime / 24).toFixed(1)}d</span>
              </div>
              <div className="flex justify-between items-center">
                <span className="text-xs">P95</span>
                <span className="font-mono text-sm">{(data.cycleTimePercentiles.p95 / 24).toFixed(1)}d</span>
              </div>
              <div className="flex justify-between items-center">
                <span className="text-xs">Lead Time</span>
                <span className="font-mono text-sm">{(data.averageLeadTime / 24).toFixed(1)}d</span>
              </div>
            </div>
          </div>

          {/* Quality Metrics */}
          <div className="space-y-2">
            <h4 className="font-semibold text-sm">Quality</h4>
            <div className="space-y-2">
              <div>
                <div className="flex justify-between items-center mb-1">
                  <span className="text-xs">Autonomous</span>
                  <span className={`font-mono text-xs ${getQualityColor(data.autonomousCompletionRate)}`}>
                    {data.autonomousCompletionRate.toFixed(1)}%
                  </span>
                </div>
                <Progress value={data.autonomousCompletionRate} className="h-1" />
              </div>
              <div>
                <div className="flex justify-between items-center mb-1">
                  <span className="text-xs">Quality Gates</span>
                  <span className={`font-mono text-xs ${getQualityColor(data.qualityGatePassRate)}`}>
                    {data.qualityGatePassRate.toFixed(1)}%
                  </span>
                </div>
                <Progress value={data.qualityGatePassRate} className="h-1" />
              </div>
              <div className="flex justify-between items-center">
                <span className="text-xs">Rework Rate</span>
                <span className="font-mono text-xs text-red-600">
                  {data.reworkRate.toFixed(1)}%
                </span>
              </div>
            </div>
          </div>

          {/* Predictive Metrics */}
          <div className="space-y-2">
            <h4 className="font-semibold text-sm">Forecast</h4>
            <div className="space-y-1">
              <div className="flex justify-between items-center">
                <span className="text-xs">Next Sprint</span>
                <span className="font-mono text-sm">{data.velocityForecast.predictedVelocity.toFixed(1)}</span>
              </div>
              <div className="flex justify-between items-center">
                <span className="text-xs">Confidence</span>
                <span className="font-mono text-sm">{data.velocityForecast.confidence.toFixed(0)}%</span>
              </div>
              <div className="flex justify-between items-center">
                <span className="text-xs">Risks</span>
                <Badge variant={data.riskIndicators.length > 0 ? 'destructive' : 'default'} className="text-xs">
                  {data.riskIndicators.length}
                </Badge>
              </div>
            </div>
          </div>
        </div>

        {/* Risk Indicators */}
        {data.riskIndicators.length > 0 && (
          <div className="mt-4 pt-4 border-t">
            <h4 className="font-semibold text-sm mb-2">Risk Indicators</h4>
            <div className="space-y-2">
              {data.riskIndicators.slice(0, 3).map((risk, index) => (
                <div key={index} className="flex items-center justify-between p-2 bg-gray-50 rounded">
                  <div className="flex-1">
                    <div className="flex items-center gap-2">
                      <Badge variant={risk.severity === 'critical' ? 'destructive' : 'outline'} className="text-xs">
                        {risk.severity}
                      </Badge>
                      <span className="text-xs font-medium">{risk.type.replace('-', ' ')}</span>
                    </div>
                    <p className="text-xs text-gray-600 mt-1">{risk.impact}</p>
                  </div>
                  <div className="text-xs text-gray-500">
                    {risk.probability.toFixed(0)}% prob.
                  </div>
                </div>
              ))}
            </div>
          </div>
        )}
      </CardContent>
    </Card>
  );
}
```

#### AI Provider Performance Widget

```typescript
// packages/dashboard/src/components/widgets/AIProviderPerformanceWidget.tsx
import React from 'react';
import { Card, CardContent, CardHeader, CardTitle } from '../ui/card';
import { Badge } from '../ui/badge';
import { Progress } from '../ui/progress';
import { useRealTimeData } from '../../hooks/useRealTimeData';
import { AIProviderMetrics } from '../../types/velocity.types';

export function AIProviderPerformanceWidget({ config }: { config: any }) {
  const { data, loading, error } = useRealTimeData<AIProviderMetrics[]>({
    type: 'velocity',
    endpoint: '/api/dashboard/velocity/ai-performance',
    query: JSON.stringify(config.filters || {}),
    refreshInterval: 300000 // 5 minutes
  });

  if (loading) return <div>Loading AI provider metrics...</div>;
  if (error) return <div>Error: {error}</div>;
  if (!data || data.length === 0) return <div>No AI provider data available</div>;

  const getPerformanceColor = (score: number) => {
    if (score >= 8) return 'text-green-600';
    if (score >= 6) return 'text-yellow-600';
    return 'text-red-600';
  };

  const getCostEfficiency = (cost: number, usage: number) => {
    return usage > 0 ? cost / usage : 0;
  };

  return (
    <Card>
      <CardHeader>
        <CardTitle>AI Provider Performance</CardTitle>
      </CardHeader>
      <CardContent>
        <div className="space-y-4">
          {data.map((provider, index) => (
            <div key={index} className="border rounded-lg p-4">
              <div className="flex items-center justify-between mb-3">
                <div>
                  <h4 className="font-semibold">{provider.provider}</h4>
                  <p className="text-sm text-gray-600">{provider.model}</p>
                </div>
                <div className="text-right">
                  <div className="text-sm font-medium">
                    {provider.usageCount} uses
                  </div>
                  <div className={`text-xs ${getPerformanceColor(provider.qualityScore)}`}>
                    Quality: {provider.qualityScore.toFixed(1)}/10
                  </div>
                </div>
              </div>

              <div className="grid grid-cols-2 md:grid-cols-4 gap-4 text-sm">
                <div>
                  <div className="text-gray-600 text-xs">Response Time</div>
                  <div className="font-mono">
                    {(provider.averageResponseTime / 1000).toFixed(1)}s
                  </div>
                </div>

                <div>
                  <div className="text-gray-600 text-xs">Success Rate</div>
                  <div className="font-mono">
                    {provider.successRate.toFixed(1)}%
                  </div>
                  <Progress value={provider.successRate} className="h-1 mt-1" />
                </div>

                <div>
                  <div className="text-gray-600 text-xs">Cost per Issue</div>
                  <div className="font-mono">
                    ${provider.costPerIssue.toFixed(3)}
                  </div>
                </div>

                <div>
                  <div className="text-gray-600 text-xs">Efficiency</div>
                  <Badge
                    variant={provider.qualityScore >= 8 ? 'default' : 'outline'}
                    className="text-xs"
                  >
                    {provider.qualityScore >= 8 ? 'High' :
                     provider.qualityScore >= 6 ? 'Medium' : 'Low'}
                  </Badge>
                </div>
              </div>

              {/* Performance Indicators */}
              <div className="mt-3 flex flex-wrap gap-2">
                {provider.averageResponseTime < 5000 && (
                  <Badge variant="default" className="text-xs">Fast Response</Badge>
                )}
                {provider.successRate >= 95 && (
                  <Badge variant="default" className="text-xs">Reliable</Badge>
                )}
                {provider.costPerIssue < 0.01 && (
                  <Badge variant="default" className="text-xs">Cost Effective</Badge>
                )}
                {provider.qualityScore >= 8 && (
                  <Badge variant="default" className="text-xs">High Quality</Badge>
                )}
              </div>
            </div>
          ))}
        </div>

        {/* Summary Insights */}
        <div className="mt-4 pt-4 border-t">
          <h4 className="font-semibold text-sm mb-2">Key Insights</h4>
          <div className="space-y-1 text-xs text-gray-600">
            {data.length > 1 && (
              <div>
                • Best performer: <strong>{data.reduce((best, current) =>
                  current.qualityScore > best.qualityScore ? current : best
                ).provider}</strong>
              </div>
            )}
            <div>
              • Average response time: <strong>
                {(data.reduce((sum, p) => sum + p.averageResponseTime, 0) / data.length / 1000).toFixed(1)}s
              </strong>
            </div>
            <div>
              • Total cost: <strong>
                ${data.reduce((sum, p) => sum + (p.costPerIssue * p.usageCount), 0).toFixed(2)}
              </strong>
            </div>
          </div>
        </div>
      </CardContent>
    </Card>
  );
}
```

### 3. Velocity API Endpoints

#### Velocity Routes

```typescript
// packages/velocity/src/api/velocity.routes.ts
import { FastifyInstance, FastifyRequest } from 'fastify';
import { VelocityAnalyticsService } from '../services/velocity-analytics.service';
import { authenticateToken, requireRole } from '../middleware/auth';

export async function velocityRoutes(fastify: FastifyInstance) {
  const velocityService = new VelocityAnalyticsService(
    fastify.eventStore,
    fastify.prometheusService,
    fastify.log
  );

  // Velocity overview
  fastify.post(
    '/overview',
    {
      preHandler: [authenticateToken, requireRole('developer')],
    },
    async (request: FastifyRequest<{ Body: VelocityRequest }>) => {
      const { timeRange, filters } = request.body;
      return velocityService.calculateVelocityMetrics(timeRange, filters);
    }
  );

  // AI provider performance
  fastify.post(
    '/ai-performance',
    {
      preHandler: [authenticateToken, requireRole('developer')],
    },
    async (request: FastifyRequest<{ Body: VelocityRequest }>) => {
      const { timeRange, filters } = request.body;
      const metrics = await velocityService.calculateVelocityMetrics(timeRange, filters);
      return metrics.aiProviderPerformance;
    }
  );

  // Bottleneck analysis
  fastify.post(
    '/bottlenecks',
    {
      preHandler: [authenticateToken, requireRole('developer')],
    },
    async (request: FastifyRequest<{ Body: VelocityRequest }>) => {
      const { timeRange, filters } = request.body;
      const metrics = await velocityService.calculateVelocityMetrics(timeRange, filters);
      return metrics.bottleneckAnalysis;
    }
  );

  // Velocity forecasting
  fastify.post(
    '/forecast',
    {
      preHandler: [authenticateToken, requireRole('developer')],
    },
    async (request: FastifyRequest<{ Body: VelocityRequest }>) => {
      const { timeRange, filters } = request.body;
      const metrics = await velocityService.calculateVelocityMetrics(timeRange, filters);
      return metrics.velocityForecast;
    }
  );

  // Risk analysis
  fastify.post(
    '/risks',
    {
      preHandler: [authenticateToken, requireRole('developer')],
    },
    async (request: FastifyRequest<{ Body: VelocityRequest }>) => {
      const { timeRange, filters } = request.body;
      const metrics = await velocityService.calculateVelocityMetrics(timeRange, filters);
      return metrics.riskIndicators;
    }
  );

  // Team comparison
  fastify.post(
    '/team-comparison',
    {
      preHandler: [authenticateToken, requireRole('manager')],
    },
    async (request: FastifyRequest<{ Body: TeamComparisonRequest }>) => {
      const { timeRange, teamIds } = request.body;

      const teamMetrics = await Promise.all(
        teamIds.map(async (teamId) => {
          const metrics = await velocityService.calculateVelocityMetrics(timeRange, { teamId });
          return { teamId, metrics };
        })
      );

      return {
        teams: teamMetrics,
        comparison: velocityService.compareTeams(teamMetrics),
      };
    }
  );
}

interface VelocityRequest {
  timeRange: TimeRange;
  filters?: VelocityFilters;
}

interface TeamComparisonRequest {
  timeRange: TimeRange;
  teamIds: string[];
}

interface TimeRange {
  start: string;
  end: string;
  type: 'day' | 'week' | 'month' | 'sprint';
}

interface VelocityFilters {
  teamId?: string;
  projectId?: string;
  individualId?: string;
  issueType?: string;
  priority?: string;
}
```

## Testing Strategy

### Velocity Analytics Testing

```typescript
// packages/velocity/src/services/__tests__/velocity-analytics.service.test.ts
import { VelocityAnalyticsService } from '../velocity-analytics.service';
import { EventStore } from '@tamma/events';
import { PrometheusService } from '@tamma/observability';

describe('VelocityAnalyticsService', () => {
  let velocityService: VelocityAnalyticsService;
  let mockEventStore: jest.Mocked<EventStore>;
  let mockPrometheusService: jest.Mocked<PrometheusService>;

  beforeEach(() => {
    mockEventStore = {
      queryEvents: jest.fn(),
    } as any;

    mockPrometheusService = {
      query: jest.fn(),
    } as any;

    velocityService = new VelocityAnalyticsService(
      mockEventStore,
      mockPrometheusService,
      {} as any
    );
  });

  describe('calculateVelocityMetrics', () => {
    it('calculates throughput metrics correctly', async () => {
      const mockEvents = [
        { type: 'ISSUE.ASSIGNED', tags: { issueId: '1' }, timestamp: '2025-01-01T00:00:00Z' },
        { type: 'ISSUE.COMPLETED', tags: { issueId: '1' }, timestamp: '2025-01-02T00:00:00Z' },
        { type: 'ISSUE.ASSIGNED', tags: { issueId: '2' }, timestamp: '2025-01-01T00:00:00Z' },
        { type: 'ISSUE.COMPLETED', tags: { issueId: '2' }, timestamp: '2025-01-03T00:00:00Z' },
      ];

      mockEventStore.queryEvents.mockResolvedValue({ events: mockEvents });

      const timeRange = {
        start: new Date('2025-01-01'),
        end: new Date('2025-01-07'),
        type: 'week' as const,
      };

      const metrics = await velocityService.calculateVelocityMetrics(timeRange);

      expect(metrics.issuesCompleted).toBe(2);
      expect(metrics.issuesCompletedPerDay).toBeCloseTo(2 / 7, 2);
    });

    it('calculates cycle time correctly', async () => {
      const mockEvents = [
        { type: 'ISSUE.STARTED', tags: { issueId: '1' }, timestamp: '2025-01-01T00:00:00Z' },
        { type: 'ISSUE.COMPLETED', tags: { issueId: '1' }, timestamp: '2025-01-02T12:00:00Z' },
        { type: 'ISSUE.STARTED', tags: { issueId: '2' }, timestamp: '2025-01-01T00:00:00Z' },
        { type: 'ISSUE.COMPLETED', tags: { issueId: '2' }, timestamp: '2025-01-01T06:00:00Z' },
      ];

      mockEventStore.queryEvents.mockResolvedValue({ events: mockEvents });

      const timeRange = {
        start: new Date('2025-01-01'),
        end: new Date('2025-01-07'),
        type: 'week' as const,
      };

      const metrics = await velocityService.calculateVelocityMetrics(timeRange);

      // Issue 1: 36 hours, Issue 2: 6 hours -> Average: 21 hours
      expect(metrics.averageCycleTime).toBeCloseTo(21 * 60 * 60 * 1000, 0);
    });

    it('calculates quality metrics correctly', async () => {
      const mockEvents = [
        { type: 'ISSUE.ASSIGNED', tags: { issueId: '1' }, timestamp: '2025-01-01T00:00:00Z' },
        {
          type: 'ISSUE.COMPLETED',
          tags: { issueId: '1' },
          data: { autonomous: true },
          timestamp: '2025-01-02T00:00:00Z',
        },
        { type: 'ISSUE.ASSIGNED', tags: { issueId: '2' }, timestamp: '2025-01-01T00:00:00Z' },
        {
          type: 'ISSUE.COMPLETED',
          tags: { issueId: '2' },
          data: { autonomous: false },
          timestamp: '2025-01-03T00:00:00Z',
        },
        { type: 'ESCALATION.TRIGGERED', tags: { issueId: '2' }, timestamp: '2025-01-02T00:00:00Z' },
        {
          type: 'QUALITY.GATE.RESULT',
          data: { status: 'passed' },
          timestamp: '2025-01-02T00:00:00Z',
        },
        {
          type: 'QUALITY.GATE.RESULT',
          data: { status: 'failed' },
          timestamp: '2025-01-03T00:00:00Z',
        },
      ];

      mockEventStore.queryEvents.mockResolvedValue({ events: mockEvents });

      const timeRange = {
        start: new Date('2025-01-01'),
        end: new Date('2025-01-07'),
        type: 'week' as const,
      };

      const metrics = await velocityService.calculateVelocityMetrics(timeRange);

      expect(metrics.autonomousCompletionRate).toBe(50); // 1 out of 2 issues
      expect(metrics.escalationRate).toBe(50); // 1 out of 2 issues
      expect(metrics.qualityGatePassRate).toBe(50); // 1 out of 2 gates passed
    });
  });

  describe('AI provider performance analysis', () => {
    it('calculates AI provider metrics correctly', async () => {
      const mockEvents = [
        {
          type: 'AI.PROVIDER.USED',
          tags: { provider: 'anthropic', model: 'claude-3' },
          data: {
            responseTime: 2000,
            success: true,
            cost: 0.01,
            qualityScore: 8,
          },
          timestamp: '2025-01-01T00:00:00Z',
        },
        {
          type: 'AI.PROVIDER.USED',
          tags: { provider: 'anthropic', model: 'claude-3' },
          data: {
            responseTime: 3000,
            success: true,
            cost: 0.01,
            qualityScore: 7,
          },
          timestamp: '2025-01-01T01:00:00Z',
        },
      ];

      mockEventStore.queryEvents.mockResolvedValue({ events: mockEvents });

      const timeRange = {
        start: new Date('2025-01-01'),
        end: new Date('2025-01-07'),
        type: 'week' as const,
      };

      const metrics = await velocityService.calculateVelocityMetrics(timeRange);

      expect(metrics.aiProviderPerformance).toHaveLength(1);
      const provider = metrics.aiProviderPerformance[0];

      expect(provider.provider).toBe('anthropic');
      expect(provider.model).toBe('claude-3');
      expect(provider.usageCount).toBe(2);
      expect(provider.averageResponseTime).toBe(2500);
      expect(provider.successRate).toBe(100);
      expect(provider.costPerIssue).toBe(0.01);
      expect(provider.qualityScore).toBe(7.5);
    });
  });
});
```

### Dashboard Component Testing

```typescript
// packages/dashboard/src/components/widgets/__tests__/VelocityOverviewWidget.test.tsx
import React from 'react';
import { render, screen, waitFor } from '@testing-library/react';
import { VelocityOverviewWidget } from '../VelocityOverviewWidget';

jest.mock('../../../hooks/useRealTimeData', () => ({
  useRealTimeData: jest.fn()
}));

import { useRealTimeData } from '../../../hooks/useRealTimeData';

describe('VelocityOverviewWidget', () => {
  beforeEach(() => {
    jest.clearAllMocks();
  });

  it('displays velocity metrics correctly', async () => {
    const mockData = {
      issuesCompleted: 25,
      issuesCompletedPerDay: 3.5,
      averageIssuesPerSprint: 17.5,
      averageCycleTime: 48 * 60 * 60 * 1000, // 48 hours
      cycleTimePercentiles: {
        p50: 36 * 60 * 60 * 1000,
        p95: 72 * 60 * 60 * 1000
      },
      averageLeadTime: 72 * 60 * 60 * 1000, // 72 hours
      autonomousCompletionRate: 85,
      qualityGatePassRate: 92,
      reworkRate: 8,
      velocityForecast: {
        predictedVelocity: 18,
        confidence: 78
      },
      riskIndicators: [
        {
          type: 'velocity-decline',
          severity: 'medium',
          probability: 65,
          impact: 'Velocity declining by 15% per period'
        }
      ]
    };

    (useRealTimeData as jest.Mock).mockReturnValue({
      data: mockData,
      loading: false,
      error: null,
      lastUpdate: new Date()
    });

    render(<VelocityOverviewWidget config={{}} />);

    await waitFor(() => {
      expect(screen.getByText('25')).toBeInTheDocument();
      expect(screen.getByText('3.5')).toBeInTheDocument();
      expect(screen.getByText('17.5')).toBeInTheDocument();
      expect(screen.getByText('2.0d')).toBeInTheDocument(); // 48 hours
      expect(screen.getByText('3.0d')).toBeInTheDocument(); // 72 hours
      expect(screen.getByText('85.0%')).toBeInTheDocument();
      expect(screen.getByText('92.0%')).toBeInTheDocument();
      expect(screen.getByText('8.0%')).toBeInTheDocument();
      expect(screen.getByText('18.0')).toBeInTheDocument();
      expect(screen.getByText('78%')).toBeInTheDocument();
    });
  });

  it('displays risk indicators when present', async () => {
    const mockData = {
      issuesCompleted: 10,
      issuesCompletedPerDay: 1.4,
      averageIssuesPerSprint: 7,
      averageCycleTime: 24 * 60 * 60 * 1000,
      cycleTimePercentiles: { p50: 0, p75: 0, p90: 0, p95: 0, p99: 0 },
      averageLeadTime: 48 * 60 * 60 * 1000,
      autonomousCompletionRate: 60,
      qualityGatePassRate: 75,
      reworkRate: 15,
      velocityForecast: {
        predictedVelocity: 6,
        confidence: 65
      },
      riskIndicators: [
        {
          type: 'velocity-decline',
          severity: 'high',
          probability: 80,
          impact: 'Velocity declining by 25% per period'
        },
        {
          type: 'quality-degradation',
          severity: 'medium',
          probability: 60,
          impact: 'Quality metrics declining by 20% per period'
        }
      ]
    };

    (useRealTimeData as jest.Mock).mockReturnValue({
      data: mockData,
      loading: false,
      error: null,
      lastUpdate: new Date()
    });

    render(<VelocityOverviewWidget config={{}} />);

    await waitFor(() => {
      expect(screen.getByText('Risk Indicators')).toBeInTheDocument();
      expect(screen.getByText('velocity decline')).toBeInTheDocument();
      expect(screen.getByText('quality degradation')).toBeInTheDocument();
      expect(screen.getByText('80% prob.')).toBeInTheDocument();
      expect(screen.getByText('60% prob.')).toBeInTheDocument();
    });
  });
});
```

## Performance Requirements

### Analytics Performance

- **Query Response Time**: Velocity calculations under 2 seconds for 30-day ranges
- **Real-time Updates**: Dashboard data refresh within 5 seconds
- **Concurrent Users**: Support 50 concurrent users viewing velocity data
- **Data Processing**: Event stream processing under 100ms per 1000 events
- **Cache Hit Rate**: 90%+ cache hit rate for frequently accessed metrics

### Dashboard Performance

- **Initial Load**: Velocity dashboard fully interactive within 4 seconds
- **Widget Rendering**: Individual velocity widgets render within 1 second
- **Chart Performance**: Charts with 1000+ data points render within 500ms
- **Memory Usage**: Dashboard memory usage under 150MB with velocity data
- **Network Usage**: Under 2MB data transfer per dashboard session

### Analytics Engine Performance

- **Event Processing**: 10,000+ events processed per second
- **Metric Calculation**: All velocity metrics calculated in under 5 seconds
- **Forecast Generation**: Velocity forecasts generated in under 3 seconds
- **Risk Analysis**: Risk indicators calculated in under 2 seconds
- **Database Queries**: All queries complete in under 1 second

## Security Considerations

### Data Privacy

- **Individual Metrics**: Personal performance data anonymized for team views
- **Access Controls**: Role-based access to sensitive velocity data
- **Data Retention**: Historical data retention policies compliant with privacy regulations
- **Audit Logging**: All velocity data access logged for compliance

### Performance Data Protection

- **Sensitive Metrics**: Cost and performance data protected by role-based access
- **Data Aggregation**: Individual data aggregated to prevent identification
- **Export Controls**: Sensitive velocity data export restricted to authorized users
- **API Security**: All velocity API endpoints require authentication and authorization

## Success Metrics

### User Adoption

- **Dashboard Usage**: 70%+ of team members accessing velocity dashboard weekly
- **Session Duration**: Average session duration over 8 minutes
- **Feature Adoption**: 80%+ of users utilizing forecasting and risk analysis features
- **User Satisfaction**: Net Promoter Score (NPS) above 40 for velocity dashboard

### Business Impact

- **Velocity Improvement**: 15%+ increase in team velocity within 3 months
- **Quality Improvement**: 20%+ reduction in rework rate
- **Efficiency Gains**: 25%+ improvement in autonomous completion rate
- **Risk Reduction**: 50%+ reduction in surprise timeline delays

### Technical Performance

- **System Performance**: All velocity calculations under 2 seconds
- **Data Accuracy**: 99.9%+ accuracy in velocity metrics
- **Uptime**: 99.9% availability for velocity dashboard
- **User Experience**: 95th percentile page load time under 3 seconds

## Rollout Plan

### Phase 1: Core Velocity Metrics (Week 1-2)

1. **Analytics Engine Development**
   - Velocity calculation service implementation
   - Basic throughput and time metrics
   - Quality metrics calculation
   - Integration with event store

2. **Essential Dashboard Widgets**
   - Velocity overview widget
   - Basic throughput charts
   - Quality metrics display
   - Real-time data updates

### Phase 2: Advanced Analytics (Week 3-4)

1. **Enhanced Analytics**
   - AI provider performance analysis
   - Bottleneck identification
   - Predictive velocity forecasting
   - Risk analysis and indicators

2. **Advanced Visualizations**
   - Trend analysis charts
   - Team comparison views
   - Performance heat maps
   - Interactive drill-downs

### Phase 3: Team Features (Week 5-6)

1. **Team Analytics**
   - Team comparison functionality
   - Individual performance tracking
   - Capacity planning tools
   - Collaboration metrics

2. **Advanced Features**
   - Custom report generation
   - Data export functionality
   - Alert configuration
   - Mobile responsiveness

### Phase 4: Optimization & Enhancement (Week 7-8)

1. **Performance Optimization**
   - Query optimization
   - Caching implementation
   - Real-time update optimization
   - Memory usage optimization

2. **User Experience Enhancement**
   - User feedback integration
   - UI/UX refinements
   - Accessibility improvements
   - Documentation and training

## Dependencies

### Internal Dependencies

- **Epic 5.1**: Structured Logging Implementation (provides event data)
- **Epic 5.2**: Metrics Collection Infrastructure (provides metrics data)
- **Epic 5.3**: Real-Time Dashboard - System Health (provides dashboard framework)
- **Epic 4**: Event Sourcing & Audit Trail (provides historical event data)
- **Epic 2**: Autonomous Development Workflow (provides workflow events)

### External Dependencies

- **React 18+**: Frontend framework for velocity dashboard
- **Chart.js/Recharts**: Data visualization for velocity charts
- **Node.js**: Backend analytics engine
- **PostgreSQL**: Event store for velocity calculations
- **Redis**: Caching for frequently accessed metrics

### Infrastructure Dependencies

- **Prometheus**: Metrics collection and storage
- **Kubernetes**: Scalable deployment platform
- **Load Balancer**: Traffic distribution for dashboard
- **Monitoring**: System health and performance monitoring

---

**Story Status**: Ready for Development  
**Implementation Priority**: Optional (Post-MVP Enhancement)  
**Target Completion**: Sprint 6 (Post-MVP)  
**Dependencies**: Epic 5.1, Epic 5.2, Epic 5.3, Epic 4 (Event Sourcing)
