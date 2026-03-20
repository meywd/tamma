/**
 * Cost Tracker Service
 * Main orchestrating service for LLM cost monitoring
 */

import type {
  ICostTracker,
  ICostStorage,
  UsageRecord,
  UsageRecordInput,
  UsageFilter,
  UsageAggregate,
  GroupByDimension,
  UsageLimit,
  UsageLimitInput,
  LimitContext,
  LimitCheckResult,
  CostAlert,
  AlertStatus,
  AlertSeverity,
  ReportSchedule,
  ReportOptions,
  CostReport,
  CostEstimateRequest,
  CostEstimate,
  PricingConfig,
  AlertChannelConfig,
} from './types.js';
import { CostCalculator } from './cost-calculator.js';
import { UsageTracker } from './usage-tracker.js';
import { LimitManager } from './limit-manager.js';
import { AlertManager } from './alert-manager.js';
import { ReportGenerator } from './report-generator.js';
import { InMemoryStore } from './storage/in-memory-store.js';

/**
 * Options for the Cost Tracker
 */
export interface CostTrackerOptions {
  storage?: ICostStorage;
  pricingConfig?: Partial<PricingConfig>;
  alertChannels?: AlertChannelConfig[];
}

/**
 * Cost Tracker - Main service for LLM cost monitoring and reporting
 *
 * This service orchestrates:
 * - Usage tracking and cost calculation
 * - Budget limits and enforcement
 * - Alerts and notifications
 * - Report generation
 */
export class CostTracker implements ICostTracker {
  private readonly storage: ICostStorage;
  private readonly calculator: CostCalculator;
  private readonly usageTracker: UsageTracker;
  private readonly limitManager: LimitManager;
  private readonly alertManager: AlertManager;
  private readonly reportGenerator: ReportGenerator;

  constructor(options: CostTrackerOptions = {}) {
    // Initialize storage
    this.storage = options.storage ?? new InMemoryStore();

    // Initialize calculator
    this.calculator = new CostCalculator(options.pricingConfig);

    // Initialize usage tracker
    this.usageTracker = new UsageTracker({
      storage: this.storage,
      calculator: this.calculator,
    });

    // Initialize alert manager
    const alertManagerOptions: { storage: ICostStorage; channels?: AlertChannelConfig[] } = {
      storage: this.storage,
    };
    if (options.alertChannels) {
      alertManagerOptions.channels = options.alertChannels;
    }
    this.alertManager = new AlertManager(alertManagerOptions);

    // Initialize limit manager
    this.limitManager = new LimitManager({
      storage: this.storage,
      usageTracker: this.usageTracker,
      onAlert: (alert) => {
        // Forward alerts to alert manager for delivery
        const createParams: {
          type: typeof alert.type;
          severity: typeof alert.severity;
          scope: typeof alert.scope;
          scopeId?: string;
          message: string;
          currentValue: number;
          threshold: number;
        } = {
          type: alert.type,
          severity: alert.severity,
          scope: alert.scope,
          message: alert.message,
          currentValue: alert.currentValue,
          threshold: alert.threshold,
        };
        if (alert.scopeId) {
          createParams.scopeId = alert.scopeId;
        }
        this.alertManager.createAlert(createParams).catch((error: unknown) => {
          console.warn('[CostTracker] Failed to create alert:', error instanceof Error ? error.message : String(error));
        });
      },
    });

    // Initialize report generator
    this.reportGenerator = new ReportGenerator({
      storage: this.storage,
    });
  }

  // --- Tracking ---

  async recordUsage(usage: UsageRecordInput): Promise<UsageRecord> {
    return this.usageTracker.recordUsage(usage);
  }

  // --- Queries ---

  async getUsage(filter: UsageFilter): Promise<UsageRecord[]> {
    return this.usageTracker.getUsage(filter);
  }

  async getAggregate(
    filter: UsageFilter,
    groupBy: GroupByDimension[]
  ): Promise<UsageAggregate[]> {
    return this.usageTracker.getAggregate(filter, groupBy);
  }

  // --- Limits ---

  async checkLimit(context: LimitContext): Promise<LimitCheckResult> {
    return this.limitManager.checkLimit(context);
  }

  async setLimit(input: UsageLimitInput): Promise<UsageLimit> {
    return this.limitManager.setLimit(input);
  }

  async updateLimit(id: string, updates: Partial<UsageLimit>): Promise<UsageLimit> {
    return this.limitManager.updateLimit(id, updates);
  }

  async deleteLimit(id: string): Promise<void> {
    return this.limitManager.deleteLimit(id);
  }

  async getLimits(): Promise<UsageLimit[]> {
    return this.limitManager.getLimits();
  }

  // --- Alerts ---

  async getAlerts(filter?: { status?: AlertStatus; severity?: AlertSeverity }): Promise<CostAlert[]> {
    return this.alertManager.getAlerts(filter);
  }

  async acknowledgeAlert(alertId: string, acknowledgedBy: string): Promise<void> {
    return this.alertManager.acknowledgeAlert(alertId, acknowledgedBy);
  }

  async resolveAlert(alertId: string): Promise<void> {
    return this.alertManager.resolveAlert(alertId);
  }

  // --- Reports ---

  async generateReport(options: ReportOptions): Promise<CostReport> {
    return this.reportGenerator.generateReport(options);
  }

  async scheduleReport(schedule: Omit<ReportSchedule, 'id'>): Promise<ReportSchedule> {
    return this.reportGenerator.scheduleReport(schedule);
  }

  async getScheduledReports(): Promise<ReportSchedule[]> {
    return this.reportGenerator.getScheduledReports();
  }

  async deleteScheduledReport(id: string): Promise<void> {
    return this.reportGenerator.deleteScheduledReport(id);
  }

  // --- Estimation ---

  async estimateCost(request: CostEstimateRequest): Promise<CostEstimate> {
    return this.calculator.estimate(request);
  }

  // --- Configuration ---

  async updatePricing(config: Partial<PricingConfig>): Promise<void> {
    this.calculator.updatePricing(config);
  }

  async getPricing(): Promise<PricingConfig> {
    return this.calculator.getPricing();
  }

  // --- Convenience Methods ---

  /**
   * Get total cost for a time period
   */
  async getTotalCost(filter: UsageFilter = {}): Promise<number> {
    return this.usageTracker.getTotalCost(filter);
  }

  /**
   * Get project summary
   */
  async getProjectSummary(
    projectId: string,
    startDate?: Date,
    endDate?: Date
  ): Promise<{
    totalCostUsd: number;
    totalCalls: number;
    totalInputTokens: number;
    totalOutputTokens: number;
    byModel: UsageAggregate[];
    byAgentType: UsageAggregate[];
  }> {
    return this.usageTracker.getProjectSummary(projectId, startDate, endDate);
  }

  /**
   * Get usage for current period
   */
  async getCurrentPeriodUsage(
    period: 'daily' | 'weekly' | 'monthly',
    projectId?: string
  ): Promise<number> {
    return this.usageTracker.getCurrentPeriodUsage(period, projectId);
  }

  /**
   * Get alert summary
   */
  async getAlertSummary(): Promise<{
    active: number;
    acknowledged: number;
    resolved: number;
    bySeverity: Record<AlertSeverity, number>;
  }> {
    return this.alertManager.getAlertSummary();
  }

  /**
   * Format report as CSV
   */
  formatReportAsCsv(report: CostReport): string {
    return this.reportGenerator.formatAsCsv(report);
  }

  /**
   * Format report as JSON
   */
  formatReportAsJson(report: CostReport): string {
    return this.reportGenerator.formatAsJson(report);
  }

  // --- Component Access ---

  /**
   * Get the cost calculator
   */
  getCalculator(): CostCalculator {
    return this.calculator;
  }

  /**
   * Get the usage tracker
   */
  getUsageTracker(): UsageTracker {
    return this.usageTracker;
  }

  /**
   * Get the limit manager
   */
  getLimitManager(): LimitManager {
    return this.limitManager;
  }

  /**
   * Get the alert manager
   */
  getAlertManager(): AlertManager {
    return this.alertManager;
  }

  /**
   * Get the report generator
   */
  getReportGenerator(): ReportGenerator {
    return this.reportGenerator;
  }

  // --- Lifecycle ---

  async dispose(): Promise<void> {
    await this.storage.flush();
  }
}

/**
 * Create a cost tracker with default settings
 */
export function createCostTracker(options?: CostTrackerOptions): CostTracker {
  return new CostTracker(options);
}
