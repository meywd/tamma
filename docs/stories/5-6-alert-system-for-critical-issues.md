# Story 5.6: Alert System for Critical Issues

**Epic**: Epic 5 - Observability & Production Readiness  
**Status**: Ready for Dev  
**MVP Priority**: Partial (Core alerting for MVP)  
**Estimated Complexity**: High  
**Target Implementation**: Sprint 5 (MVP Core) + Sprint 6 (Enhanced Features)

## Acceptance Criteria

### Alert Management System

- [ ] **Comprehensive alert management** with rule-based alert generation
- [ ] **Multiple alert severity levels** (info, warning, error, critical) with appropriate escalation
- [ ] **Alert routing** to appropriate channels (email, Slack, PagerDuty, webhook)
- [ ] **Alert acknowledgment** and resolution tracking
- [ ] **Alert suppression** and deduplication to prevent alert fatigue

### Alert Rules Engine

- [ ] **Flexible rule definition** with condition-based triggers
- [ ] **Threshold-based alerts** for metrics and performance indicators
- [ ] **Pattern-based alerts** for event sequences and anomalies
- [ ] **Composite alerts** combining multiple conditions
- [ ] **Time-based alert windows** with cooldown periods

### Notification Channels

- [ ] **Email notifications** with customizable templates and recipient lists
- [ ] **Slack integration** with channel routing and message formatting
- [ ] **PagerDuty integration** for critical incident escalation
- [ ] **Webhook notifications** for custom integrations
- [ ] **In-app notifications** for dashboard users

### Alert Lifecycle Management

- [ ] **Alert creation** with automatic context enrichment
- [ ] **Alert acknowledgment** with user assignment and comments
- [ ] **Alert escalation** based on severity and time thresholds
- [ ] **Alert resolution** with root cause documentation
- [ ] **Alert history** and post-incident analysis

### Alert Analytics

- [ ] **Alert metrics dashboard** showing alert volume, types, and trends
- [ ] **Mean Time to Acknowledge (MTTA)** and **Mean Time to Resolution (MTTR)** tracking
- [ ] **Alert effectiveness analysis** with false positive identification
- [ ] **Escalation pattern analysis** for process improvement
- [ ] **Alert fatigue prevention** with noise reduction metrics

## Technical Context

### Current System State

From `docs/stories/5-1-structured-logging-implementation.md`:

- Structured logging with error and warning events
- Performance metrics and timing information
- System health and status events

From `docs/stories/5-2-metrics-collection-infrastructure.md`:

- Prometheus metrics collection with custom metrics
- System performance indicators
- AI provider and Git platform metrics

From `docs/stories/5-3-real-time-dashboard-system-health.md`:

- Real-time dashboard with WebSocket updates
- System health monitoring capabilities
- User authentication and role-based access

### Alert System Architecture

```typescript
// Alert system architecture
┌─────────────────┐    ┌──────────────────┐    ┌─────────────────┐
│   Alert Rules   │    │   Alert Engine   │    │ Notification    │
│   (Config)      │◄──►│   (Core Logic)   │◄──►│   Channels      │
│                 │    │                  │    │                 │
│ - Rule Definitions│   │ - Evaluation     │    │ - Email         │
│ - Thresholds    │    │ - Correlation    │    │ - Slack         │
│ - Conditions    │    │ - Deduplication  │    │ - PagerDuty     │
│ - Actions       │    │ - Escalation     │    │ - Webhooks      │
└─────────────────┘    └──────────────────┘    └─────────────────┘
         │                       │                       │
         │              ┌──────────────────┐              │
         └──────────────►│  Alert Store     │◄─────────────┘
                        │  (PostgreSQL)    │
                        │                  │
                        │ - Alert Records  │
                        │ - Status History │
                        │ - Acknowledgments│
                        │ - Resolutions    │
                        └──────────────────┘
         │
         ▼
┌─────────────────┐    ┌──────────────────┐    ┌─────────────────┐
│   Data Sources  │    │   Alert UI       │    │   Analytics     │
│                 │    │                  │    │                 │
│ - Metrics       │◄──►│ - Alert List     │◄──►│ - MTTA/MTTR     │
│ - Events        │    │ - Acknowledge    │    │ - Trends        │
│ - Logs          │    │ - Resolve        │    │ - Effectiveness │
│ - Health Checks │    │ - Configure      │    │ - Fatigue       │
└─────────────────┘    └──────────────────┘    └─────────────────┘
```

## Technical Implementation

### 1. Alert System Data Models

#### Alert Types and Interfaces

```typescript
// packages/alerts/src/types/alert.types.ts
export interface Alert {
  id: string;
  name: string;
  description: string;
  severity: AlertSeverity;
  status: AlertStatus;
  source: AlertSource;
  ruleId: string;
  ruleName: string;

  // Timing
  triggeredAt: string;
  acknowledgedAt?: string;
  resolvedAt?: string;
  lastNotifiedAt?: string;

  // Assignment and ownership
  assignedTo?: string;
  acknowledgedBy?: string;
  resolvedBy?: string;

  // Context and data
  context: AlertContext;
  data: Record<string, any>;
  metadata: AlertMetadata;

  // Notification tracking
  notifications: AlertNotification[];
  escalations: AlertEscalation[];

  // Related entities
  relatedAlerts?: string[];
  incidentId?: string;
}

export type AlertSeverity = 'info' | 'warning' | 'error' | 'critical';
export type AlertStatus = 'open' | 'acknowledged' | 'resolved' | 'suppressed';
export type AlertSource = 'metric' | 'event' | 'log' | 'health-check' | 'manual';

export interface AlertContext {
  // System context
  service?: string;
  component?: string;
  environment?: string;
  version?: string;

  // Operational context
  issueId?: string;
  workflowId?: string;
  userId?: string;
  requestId?: string;
  traceId?: string;

  // Infrastructure context
  host?: string;
  region?: string;
  cluster?: string;
  pod?: string;

  // Business context
  team?: string;
  project?: string;
  customer?: string;
}

export interface AlertMetadata {
  fingerprint: string;
  correlationId?: string;
  tags: Record<string, string>;
  labels: Record<string, string>;
  annotations: Record<string, string>;

  // Processing metadata
  evaluationTime: number; // milliseconds
  ruleVersion: string;
  processedAt: string;
}

export interface AlertNotification {
  id: string;
  channel: NotificationChannel;
  status: NotificationStatus;
  sentAt: string;
  deliveredAt?: string;
  error?: string;
  retryCount: number;
  template: string;
  recipient: string;
}

export type NotificationChannel = 'email' | 'slack' | 'pagerduty' | 'webhook' | 'in-app';
export type NotificationStatus = 'pending' | 'sent' | 'delivered' | 'failed' | 'retrying';

export interface AlertEscalation {
  id: string;
  level: number;
  triggeredAt: string;
  channel: NotificationChannel;
  recipient: string;
  reason: string;
  status: 'pending' | 'sent' | 'acknowledged';
}

export interface AlertRule {
  id: string;
  name: string;
  description: string;
  enabled: boolean;
  severity: AlertSeverity;

  // Conditions
  conditions: AlertCondition[];
  conditionLogic: 'and' | 'or' | 'complex';

  // Timing
  for: string; // duration condition (e.g., "5m", "1h")
  cooldown: string; // cooldown period between alerts

  // Actions
  actions: AlertAction[];
  escalationPolicy?: EscalationPolicy;

  // Suppression
  suppressionRules?: SuppressionRule[];

  // Metadata
  labels: Record<string, string>;
  annotations: Record<string, string>;
  tags: string[];

  // Versioning
  version: string;
  createdAt: string;
  updatedAt: string;
  createdBy: string;
}

export interface AlertCondition {
  type: ConditionType;
  operator: ConditionOperator;
  metric?: string;
  query?: string;
  threshold?: number;
  value?: any;
  aggregation?: AggregationType;
  timeWindow?: string;
}

export type ConditionType =
  | 'metric_threshold'
  | 'metric_absence'
  | 'log_pattern'
  | 'event_pattern'
  | 'health_check'
  | 'composite';

export type ConditionOperator =
  | 'eq'
  | 'ne'
  | 'gt'
  | 'gte'
  | 'lt'
  | 'lte'
  | 'contains'
  | 'regex'
  | 'in'
  | 'not_in';

export type AggregationType =
  | 'avg'
  | 'sum'
  | 'min'
  | 'max'
  | 'count'
  | 'rate'
  | 'increase'
  | 'delta';

export interface AlertAction {
  type: ActionType;
  config: Record<string, any>;
  enabled: boolean;
}

export type ActionType =
  | 'notification'
  | 'webhook'
  | 'automation'
  | 'incident_creation'
  | 'suppression';

export interface EscalationPolicy {
  enabled: boolean;
  levels: EscalationLevel[];
}

export interface EscalationLevel {
  level: number;
  delay: string; // time before escalation
  channels: NotificationChannel[];
  recipients: string[];
  conditions?: AlertCondition[];
}

export interface SuppressionRule {
  name: string;
  conditions: AlertCondition[];
  duration: string;
  reason: string;
  createdBy: string;
  createdAt: string;
}

export interface AlertAnalytics {
  timeRange: TimeRange;

  // Volume metrics
  totalAlerts: number;
  alertsBySeverity: Record<AlertSeverity, number>;
  alertsBySource: Record<AlertSource, number>;
  alertsByService: Record<string, number>;

  // Timing metrics
  mtta: number; // Mean Time To Acknowledge (minutes)
  mttr: number; // Mean Time To Resolution (minutes)
  resolutionDistribution: Percentiles;

  // Effectiveness metrics
  falsePositiveRate: number;
  suppressionRate: number;
  escalationRate: number;

  // Fatigue metrics
  alertFrequency: number; // alerts per hour
  noiseScore: number; // 0-100, higher is more noise
  topAlerters: AlertFrequency[];

  // Trends
  trends: AlertTrend[];
  predictions: AlertPrediction[];
}

export interface Percentiles {
  p50: number;
  p75: number;
  p90: number;
  p95: number;
  p99: number;
}

export interface AlertFrequency {
  ruleName: string;
  count: number;
  percentage: number;
}

export interface AlertTrend {
  period: string;
  total: number;
  critical: number;
  error: number;
  warning: number;
  info: number;
}

export interface AlertPrediction {
  type: 'volume' | 'severity' | 'pattern';
  prediction: any;
  confidence: number;
  timeFrame: string;
  factors: string[];
}

export interface TimeRange {
  start: string;
  end: string;
  relative?: string;
}
```

### 2. Alert Engine Implementation

#### Core Alert Processing Engine

```typescript
// packages/alerts/src/services/alert-engine.service.ts
import { EventEmitter } from 'events';
import { PrometheusService } from '@tamma/observability';
import { EventStore } from '@tamma/events';
import { Logger } from 'pino';
import {
  Alert,
  AlertRule,
  AlertCondition,
  AlertSeverity,
  AlertContext,
  AlertMetadata,
  NotificationChannel,
} from '../types/alert.types';
import { NotificationService } from './notification.service';
import { AlertStore } from './alert.store';

export class AlertEngineService extends EventEmitter {
  private rules: Map<string, AlertRule> = new Map();
  private activeAlerts: Map<string, Alert> = new Map();
  private suppressionRules: Map<string, SuppressionRule> = new Map();
  private evaluationInterval: NodeJS.Timeout;

  constructor(
    private prometheusService: PrometheusService,
    private eventStore: EventStore,
    private notificationService: NotificationService,
    private alertStore: AlertStore,
    private logger: Logger
  ) {
    super();
    this.startEvaluation();
  }

  async loadRules(): Promise<void> {
    try {
      const rules = await this.alertStore.getRules({ enabled: true });
      this.rules.clear();

      rules.forEach((rule) => {
        this.rules.set(rule.id, rule);
      });

      this.logger.info(`Loaded ${rules.length} alert rules`);
    } catch (error) {
      this.logger.error('Failed to load alert rules', { error });
      throw error;
    }
  }

  async loadSuppressionRules(): Promise<void> {
    try {
      const suppressions = await this.alertStore.getSuppressionRules();
      this.suppressionRules.clear();

      suppressions.forEach((suppression) => {
        this.suppressionRules.set(suppression.name, suppression);
      });

      this.logger.info(`Loaded ${suppressions.length} suppression rules`);
    } catch (error) {
      this.logger.error('Failed to load suppression rules', { error });
    }
  }

  async evaluateRules(): Promise<void> {
    const evaluationStart = Date.now();

    try {
      const activeRules = Array.from(this.rules.values());
      const evaluationPromises = activeRules.map((rule) => this.evaluateRule(rule));

      const results = await Promise.allSettled(evaluationPromises);

      const successful = results.filter((r) => r.status === 'fulfilled').length;
      const failed = results.filter((r) => r.status === 'rejected').length;

      if (failed > 0) {
        this.logger.warn('Some rule evaluations failed', { successful, failed });
      }

      this.emit('evaluation-completed', {
        duration: Date.now() - evaluationStart,
        rules: activeRules.length,
        successful,
        failed,
      });
    } catch (error) {
      this.logger.error('Rule evaluation failed', {
        error,
        duration: Date.now() - evaluationStart,
      });
    }
  }

  private async evaluateRule(rule: AlertRule): Promise<void> {
    try {
      const ruleStart = Date.now();

      // Check if rule is in cooldown
      if (await this.isInCooldown(rule)) {
        return;
      }

      // Evaluate all conditions
      const conditionResults = await Promise.all(
        rule.conditions.map((condition) => this.evaluateCondition(condition))
      );

      // Apply condition logic
      const triggered = this.applyConditionLogic(conditionResults, rule.conditionLogic);

      if (triggered) {
        await this.handleTriggeredRule(rule, conditionResults);
      }

      this.logger.debug('Rule evaluated', {
        ruleId: rule.id,
        ruleName: rule.name,
        triggered,
        duration: Date.now() - ruleStart,
      });
    } catch (error) {
      this.logger.error('Rule evaluation failed', {
        ruleId: rule.id,
        ruleName: rule.name,
        error,
      });
    }
  }

  private async evaluateCondition(condition: AlertCondition): Promise<boolean> {
    switch (condition.type) {
      case 'metric_threshold':
        return this.evaluateMetricThreshold(condition);
      case 'metric_absence':
        return this.evaluateMetricAbsence(condition);
      case 'log_pattern':
        return this.evaluateLogPattern(condition);
      case 'event_pattern':
        return this.evaluateEventPattern(condition);
      case 'health_check':
        return this.evaluateHealthCheck(condition);
      case 'composite':
        return this.evaluateCompositeCondition(condition);
      default:
        this.logger.warn('Unknown condition type', { type: condition.type });
        return false;
    }
  }

  private async evaluateMetricThreshold(condition: AlertCondition): Promise<boolean> {
    if (!condition.metric || condition.threshold === undefined) {
      return false;
    }

    try {
      // Build Prometheus query
      let query = condition.metric;

      if (condition.aggregation) {
        query = `${condition.aggregation}(${query})`;
      }

      if (condition.timeWindow) {
        query = `${query}[${condition.timeWindow}]`;
      }

      const result = await this.prometheusService.query(query);

      if (result.data.result.length === 0) {
        return false;
      }

      const value = parseFloat(result.data.result[0].value[1]);

      switch (condition.operator) {
        case 'gt':
          return value > condition.threshold;
        case 'gte':
          return value >= condition.threshold;
        case 'lt':
          return value < condition.threshold;
        case 'lte':
          return value <= condition.threshold;
        case 'eq':
          return value === condition.threshold;
        case 'ne':
          return value !== condition.threshold;
        default:
          return false;
      }
    } catch (error) {
      this.logger.error('Metric threshold evaluation failed', { condition, error });
      return false;
    }
  }

  private async evaluateMetricAbsence(condition: AlertCondition): Promise<boolean> {
    if (!condition.metric) {
      return false;
    }

    try {
      const query = `${condition.metric}${condition.timeWindow || '5m'}`;
      const result = await this.prometheusService.query(query);

      // Alert if no data points found
      return result.data.result.length === 0;
    } catch (error) {
      this.logger.error('Metric absence evaluation failed', { condition, error });
      return false;
    }
  }

  private async evaluateLogPattern(condition: AlertCondition): Promise<boolean> {
    if (!condition.query) {
      return false;
    }

    try {
      // Query logs for pattern matching
      const logQuery = {
        query: condition.query,
        timeRange: condition.timeWindow || '5m',
        limit: 1,
      };

      const logs = await this.eventStore.queryLogs(logQuery);
      return logs.length > 0;
    } catch (error) {
      this.logger.error('Log pattern evaluation failed', { condition, error });
      return false;
    }
  }

  private async evaluateEventPattern(condition: AlertCondition): Promise<boolean> {
    if (!condition.query) {
      return false;
    }

    try {
      // Parse event query
      const eventFilters = this.parseEventQuery(condition.query);
      eventFilters.timestamp = this.getTimeRangeFilter(condition.timeWindow || '5m');

      const events = await this.eventStore.queryEvents({
        filters: eventFilters,
        limit: 1,
      });

      return events.events.length > 0;
    } catch (error) {
      this.logger.error('Event pattern evaluation failed', { condition, error });
      return false;
    }
  }

  private async evaluateHealthCheck(condition: AlertCondition): Promise<boolean> {
    // This would integrate with health check endpoints
    // For now, return false as placeholder
    return false;
  }

  private async evaluateCompositeCondition(condition: AlertCondition): Promise<boolean> {
    // Composite conditions would reference other rules or conditions
    // Implementation depends on specific composite logic requirements
    return false;
  }

  private applyConditionLogic(results: boolean[], logic: string): boolean {
    switch (logic) {
      case 'and':
        return results.every((r) => r);
      case 'or':
        return results.some((r) => r);
      case 'complex':
        // Complex logic would be defined in a separate DSL
        return results.some((r) => r); // Simplified for now
      default:
        return false;
    }
  }

  private async handleTriggeredRule(rule: AlertRule, conditionResults: boolean[]): Promise<void> {
    // Check for suppression
    if (await this.isSuppressed(rule)) {
      this.logger.debug('Alert suppressed', { ruleId: rule.id, ruleName: rule.name });
      return;
    }

    // Create alert
    const alert = await this.createAlert(rule, conditionResults);

    // Check for deduplication
    const existingAlert = await this.findDuplicateAlert(alert);
    if (existingAlert) {
      await this.updateExistingAlert(existingAlert, alert);
      return;
    }

    // Store new alert
    await this.alertStore.createAlert(alert);
    this.activeAlerts.set(alert.id, alert);

    // Execute actions
    await this.executeAlertActions(alert, rule.actions);

    // Emit alert event
    this.emit('alert-created', alert);

    this.logger.info('Alert created', {
      alertId: alert.id,
      ruleName: rule.name,
      severity: alert.severity,
    });
  }

  private async createAlert(rule: AlertRule, conditionResults: boolean[]): Promise<Alert> {
    const alertId = this.generateAlertId();
    const fingerprint = this.generateFingerprint(rule, conditionResults);

    // Extract context from rule evaluation
    const context = await this.extractAlertContext(rule, conditionResults);
    const data = await this.extractAlertData(rule, conditionResults);

    const alert: Alert = {
      id: alertId,
      name: rule.name,
      description: rule.description,
      severity: rule.severity,
      status: 'open',
      source: this.determineAlertSource(rule),
      ruleId: rule.id,
      ruleName: rule.name,
      triggeredAt: new Date().toISOString(),
      context,
      data,
      metadata: {
        fingerprint,
        tags: rule.labels,
        labels: rule.labels,
        annotations: rule.annotations,
        evaluationTime: Date.now(),
        ruleVersion: rule.version,
        processedAt: new Date().toISOString(),
      },
      notifications: [],
      escalations: [],
    };

    return alert;
  }

  private async findDuplicateAlert(alert: Alert): Promise<Alert | null> {
    const existingAlerts = await this.alertStore.getAlerts({
      fingerprint: alert.metadata.fingerprint,
      status: ['open', 'acknowledged'],
    });

    return existingAlerts.length > 0 ? existingAlerts[0] : null;
  }

  private async updateExistingAlert(existingAlert: Alert, newAlert: Alert): Promise<void> {
    // Update existing alert with new information
    existingAlert.triggeredAt = newAlert.triggeredAt;
    existingAlert.data = { ...existingAlert.data, ...newAlert.data };
    existingAlert.metadata.lastEvaluated = newAlert.metadata.processedAt;

    await this.alertStore.updateAlert(existingAlert);

    this.emit('alert-updated', existingAlert);

    this.logger.debug('Alert updated (deduplication)', {
      alertId: existingAlert.id,
      ruleName: existingAlert.ruleName,
    });
  }

  private async executeAlertActions(alert: Alert, actions: AlertAction[]): Promise<void> {
    const enabledActions = actions.filter((action) => action.enabled);

    for (const action of enabledActions) {
      try {
        await this.executeAction(alert, action);
      } catch (error) {
        this.logger.error('Alert action failed', {
          alertId: alert.id,
          actionType: action.type,
          error,
        });
      }
    }
  }

  private async executeAction(alert: Alert, action: AlertAction): Promise<void> {
    switch (action.type) {
      case 'notification':
        await this.executeNotificationAction(alert, action);
        break;
      case 'webhook':
        await this.executeWebhookAction(alert, action);
        break;
      case 'automation':
        await this.executeAutomationAction(alert, action);
        break;
      case 'incident_creation':
        await this.executeIncidentCreationAction(alert, action);
        break;
      case 'suppression':
        await this.executeSuppressionAction(alert, action);
        break;
      default:
        this.logger.warn('Unknown action type', { type: action.type });
    }
  }

  private async executeNotificationAction(alert: Alert, action: AlertAction): Promise<void> {
    const config = action.config as NotificationActionConfig;

    for (const channel of config.channels) {
      const notification = await this.notificationService.sendNotification({
        alertId: alert.id,
        channel,
        recipient: config.recipient,
        template: config.template || 'default',
        data: alert,
      });

      alert.notifications.push(notification);
    }
  }

  private async executeWebhookAction(alert: Alert, action: AlertAction): Promise<void> {
    const config = action.config as WebhookActionConfig;

    const payload = {
      alert,
      timestamp: new Date().toISOString(),
      event: 'alert.triggered',
    };

    const response = await fetch(config.url, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        ...config.headers,
      },
      body: JSON.stringify(payload),
    });

    if (!response.ok) {
      throw new Error(`Webhook failed: ${response.status} ${response.statusText}`);
    }
  }

  private async executeAutomationAction(alert: Alert, action: AlertAction): Promise<void> {
    const config = action.config as AutomationActionConfig;

    // This would integrate with automation systems
    // For now, just log the action
    this.logger.info('Automation action executed', {
      alertId: alert.id,
      automation: config.automation,
      parameters: config.parameters,
    });
  }

  private async executeIncidentCreationAction(alert: Alert, action: AlertAction): Promise<void> {
    const config = action.config as IncidentCreationActionConfig;

    // This would integrate with incident management systems
    const incident = {
      title: `Alert: ${alert.name}`,
      description: alert.description,
      severity: alert.severity,
      alertId: alert.id,
      context: alert.context,
    };

    alert.incidentId = incident.id; // Would be set by incident system

    this.logger.info('Incident created', {
      alertId: alert.id,
      incidentId: alert.incidentId,
    });
  }

  private async executeSuppressionAction(alert: Alert, action: AlertAction): Promise<void> {
    const config = action.config as SuppressionActionConfig;

    const suppressionRule: SuppressionRule = {
      name: `auto-suppression-${alert.id}`,
      conditions: [
        {
          type: 'composite',
          operator: 'eq',
          value: alert.metadata.fingerprint,
        },
      ],
      duration: config.duration,
      reason: config.reason || 'Automatic suppression from alert',
      createdBy: 'system',
      createdAt: new Date().toISOString(),
    };

    await this.alertStore.createSuppressionRule(suppressionRule);
    this.suppressionRules.set(suppressionRule.name, suppressionRule);

    // Update alert status
    alert.status = 'suppressed';
    await this.alertStore.updateAlert(alert);
  }

  private async isInCooldown(rule: AlertRule): Promise<boolean> {
    if (!rule.cooldown) {
      return false;
    }

    const cooldownMs = this.parseDuration(rule.cooldown);
    const cutoffTime = new Date(Date.now() - cooldownMs);

    const recentAlerts = await this.alertStore.getAlerts({
      ruleId: rule.id,
      triggeredAfter: cutoffTime.toISOString(),
      status: ['open', 'acknowledged', 'resolved'],
    });

    return recentAlerts.length > 0;
  }

  private async isSuppressed(rule: AlertRule): Promise<boolean> {
    for (const suppression of this.suppressionRules.values()) {
      if (await this.matchesSuppression(rule, suppression)) {
        return true;
      }
    }
    return false;
  }

  private async matchesSuppression(
    rule: AlertRule,
    suppression: SuppressionRule
  ): Promise<boolean> {
    // Check if suppression is still active
    const suppressionDuration = this.parseDuration(suppression.duration);
    const suppressionEnd = new Date(suppression.createdAt).getTime() + suppressionDuration;

    if (Date.now() > suppressionEnd) {
      // Suppression expired, remove it
      this.suppressionRules.delete(suppression.name);
      await this.alertStore.deleteSuppressionRule(suppression.name);
      return false;
    }

    // Check if rule matches suppression conditions
    const conditionResults = await Promise.all(
      suppression.conditions.map((condition) => this.evaluateCondition(condition))
    );

    return this.applyConditionLogic(conditionResults, 'and');
  }

  private startEvaluation(): void {
    // Evaluate rules every 30 seconds
    this.evaluationInterval = setInterval(() => this.evaluateRules(), 30000);

    this.logger.info('Alert engine started');
  }

  stopEvaluation(): void {
    if (this.evaluationInterval) {
      clearInterval(this.evaluationInterval);
      this.logger.info('Alert engine stopped');
    }
  }

  // Helper methods
  private generateAlertId(): string {
    return `alert-${Date.now()}-${Math.random().toString(36).substr(2, 9)}`;
  }

  private generateFingerprint(rule: AlertRule, conditionResults: boolean[]): string {
    // Create a unique fingerprint for deduplication
    const fingerprintData = {
      ruleId: rule.id,
      conditions: rule.conditions.map((c) => ({
        type: c.type,
        metric: c.metric,
        operator: c.operator,
        threshold: c.threshold,
      })),
      results: conditionResults,
    };

    return require('crypto')
      .createHash('md5')
      .update(JSON.stringify(fingerprintData))
      .digest('hex');
  }

  private determineAlertSource(rule: AlertRule): AlertSource {
    if (rule.conditions.some((c) => c.type.startsWith('metric'))) {
      return 'metric';
    }
    if (rule.conditions.some((c) => c.type.startsWith('log'))) {
      return 'log';
    }
    if (rule.conditions.some((c) => c.type.startsWith('event'))) {
      return 'event';
    }
    if (rule.conditions.some((c) => c.type === 'health_check')) {
      return 'health-check';
    }
    return 'manual';
  }

  private async extractAlertContext(
    rule: AlertRule,
    conditionResults: boolean[]
  ): Promise<AlertContext> {
    // Extract context from rule evaluation
    const context: AlertContext = {};

    // This would be enhanced to extract relevant context from the evaluation
    // For now, return basic context
    return context;
  }

  private async extractAlertData(
    rule: AlertRule,
    conditionResults: boolean[]
  ): Promise<Record<string, any>> {
    return {
      ruleId: rule.id,
      ruleName: rule.name,
      conditionResults,
      evaluationTime: new Date().toISOString(),
    };
  }

  private parseEventQuery(query: string): Record<string, any> {
    // Parse event query string into filters
    // This is a simplified implementation
    try {
      return JSON.parse(query);
    } catch {
      return { type: query };
    }
  }

  private getTimeRangeFilter(timeWindow: string): Record<string, any> {
    const duration = this.parseDuration(timeWindow);
    const startTime = new Date(Date.now() - duration).toISOString();

    return { gte: startTime };
  }

  private parseDuration(duration: string): number {
    // Parse duration strings like "5m", "1h", "30s"
    const match = duration.match(/^(\d+)([smhd])$/);
    if (!match) {
      throw new Error(`Invalid duration format: ${duration}`);
    }

    const [, value, unit] = match;
    const multipliers = { s: 1000, m: 60000, h: 3600000, d: 86400000 };

    return parseInt(value) * multipliers[unit as keyof typeof multipliers];
  }
}

// Action configuration interfaces
interface NotificationActionConfig {
  channels: NotificationChannel[];
  recipient: string;
  template?: string;
}

interface WebhookActionConfig {
  url: string;
  method?: 'POST' | 'PUT' | 'PATCH';
  headers?: Record<string, string>;
  timeout?: number;
}

interface AutomationActionConfig {
  automation: string;
  parameters: Record<string, any>;
}

interface IncidentCreationActionConfig {
  system: string;
  severity?: string;
  assignee?: string;
}

interface SuppressionActionConfig {
  duration: string;
  reason?: string;
}
```

### 3. Notification Service Implementation

#### Multi-Channel Notification Service

```typescript
// packages/alerts/src/services/notification.service.ts
import { Logger } from 'pino';
import { Alert, AlertNotification, NotificationChannel } from '../types/alert.types';
import { EmailProvider } from '../providers/email.provider';
import { SlackProvider } from '../providers/slack.provider';
import { PagerDutyProvider } from '../providers/pagerduty.provider';
import { WebhookProvider } from '../providers/webhook.provider';

export interface NotificationRequest {
  alertId: string;
  channel: NotificationChannel;
  recipient: string;
  template: string;
  data: Alert;
}

export class NotificationService {
  constructor(
    private emailProvider: EmailProvider,
    private slackProvider: SlackProvider,
    private pagerDutyProvider: PagerDutyProvider,
    private webhookProvider: WebhookProvider,
    private logger: Logger
  ) {}

  async sendNotification(request: NotificationRequest): Promise<AlertNotification> {
    const notificationId = this.generateNotificationId();
    const notification: AlertNotification = {
      id: notificationId,
      channel: request.channel,
      status: 'pending',
      sentAt: new Date().toISOString(),
      retryCount: 0,
      template: request.template,
      recipient: request.recipient,
    };

    try {
      await this.deliverNotification(request, notification);
      notification.status = 'sent';
      notification.deliveredAt = new Date().toISOString();
    } catch (error) {
      notification.status = 'failed';
      notification.error = error instanceof Error ? error.message : 'Unknown error';

      this.logger.error('Notification delivery failed', {
        notificationId,
        channel: request.channel,
        recipient: request.recipient,
        error,
      });

      // Schedule retry if appropriate
      if (this.shouldRetry(notification)) {
        await this.scheduleRetry(request, notification);
      }
    }

    return notification;
  }

  private async deliverNotification(
    request: NotificationRequest,
    notification: AlertNotification
  ): Promise<void> {
    switch (request.channel) {
      case 'email':
        await this.emailProvider.sendEmail({
          to: request.recipient,
          template: request.template,
          data: request.data,
        });
        break;

      case 'slack':
        await this.slackProvider.sendMessage({
          channel: request.recipient,
          template: request.template,
          data: request.data,
        });
        break;

      case 'pagerduty':
        await this.pagerDutyProvider.createIncident({
          recipient: request.recipient,
          template: request.template,
          data: request.data,
        });
        break;

      case 'webhook':
        await this.webhookProvider.sendWebhook({
          url: request.recipient,
          template: request.template,
          data: request.data,
        });
        break;

      case 'in-app':
        // In-app notifications would be handled differently
        break;

      default:
        throw new Error(`Unsupported notification channel: ${request.channel}`);
    }
  }

  private shouldRetry(notification: AlertNotification): boolean {
    const maxRetries = 3;
    return notification.retryCount < maxRetries;
  }

  private async scheduleRetry(
    request: NotificationRequest,
    notification: AlertNotification
  ): Promise<void> {
    notification.retryCount++;
    notification.status = 'retrying';

    // Exponential backoff: 1min, 2min, 4min
    const delay = Math.pow(2, notification.retryCount - 1) * 60000;

    setTimeout(async () => {
      try {
        await this.deliverNotification(request, notification);
        notification.status = 'sent';
        notification.deliveredAt = new Date().toISOString();
      } catch (error) {
        notification.status = 'failed';
        notification.error = error instanceof Error ? error.message : 'Unknown error';

        if (this.shouldRetry(notification)) {
          await this.scheduleRetry(request, notification);
        }
      }
    }, delay);
  }

  private generateNotificationId(): string {
    return `notif-${Date.now()}-${Math.random().toString(36).substr(2, 9)}`;
  }
}
```

### 4. Alert UI Components

#### Alert Management Interface

```typescript
// packages/alerts/src/components/AlertManagementInterface.tsx
import React, { useState, useEffect } from 'react';
import { Card, CardContent, CardHeader, CardTitle } from '../ui/card';
import { Button } from '../ui/button';
import { Badge } from '../ui/badge';
import { Input } from '../ui/input';
import { Select } from '../ui/select';
import { Alert, AlertSeverity, AlertStatus } from '../types/alert.types';

interface AlertManagementInterfaceProps {
  alerts: Alert[];
  onAcknowledge: (alertId: string, comment: string) => void;
  onResolve: (alertId: string, comment: string) => void;
  onAssign: (alertId: string, assignee: string) => void;
  onSuppress: (alertId: string, duration: string, reason: string) => void;
}

export function AlertManagementInterface({
  alerts,
  onAcknowledge,
  onResolve,
  onAssign,
  onSuppress
}: AlertManagementInterfaceProps) {
  const [selectedAlerts, setSelectedAlerts] = useState<string[]>([]);
  const [filter, setFilter] = useState({
    severity: [] as AlertSeverity[],
    status: [] as AlertStatus[],
    search: ''
  });
  const [showActions, setShowActions] = useState(false);

  const filteredAlerts = alerts.filter(alert => {
    if (filter.severity.length > 0 && !filter.severity.includes(alert.severity)) {
      return false;
    }
    if (filter.status.length > 0 && !filter.status.includes(alert.status)) {
      return false;
    }
    if (filter.search && !alert.name.toLowerCase().includes(filter.search.toLowerCase())) {
      return false;
    }
    return true;
  });

  const handleBulkAcknowledge = () => {
    const comment = prompt('Enter acknowledgment comment:');
    if (comment) {
      selectedAlerts.forEach(alertId => {
        onAcknowledge(alertId, comment);
      });
      setSelectedAlerts([]);
    }
  };

  const handleBulkResolve = () => {
    const comment = prompt('Enter resolution comment:');
    if (comment) {
      selectedAlerts.forEach(alertId => {
        onResolve(alertId, comment);
      });
      setSelectedAlerts([]);
    }
  };

  const getSeverityColor = (severity: AlertSeverity) => {
    switch (severity) {
      case 'critical': return 'bg-red-500';
      case 'error': return 'bg-red-400';
      case 'warning': return 'bg-yellow-500';
      case 'info': return 'bg-blue-500';
      default: return 'bg-gray-500';
    }
  };

  const getStatusColor = (status: AlertStatus) => {
    switch (status) {
      case 'open': return 'bg-red-100 text-red-800';
      case 'acknowledged': return 'bg-yellow-100 text-yellow-800';
      case 'resolved': return 'bg-green-100 text-green-800';
      case 'suppressed': return 'bg-gray-100 text-gray-800';
      default: return 'bg-gray-100 text-gray-800';
    }
  };

  return (
    <div className="space-y-4">
      {/* Filters and Controls */}
      <Card>
        <CardHeader>
          <CardTitle className="flex items-center justify-between">
            Alert Management
            <div className="flex gap-2">
              <Button
                variant="outline"
                onClick={() => setShowActions(!showActions)}
                disabled={selectedAlerts.length === 0}
              >
                Actions ({selectedAlerts.length})
              </Button>
            </div>
          </CardTitle>
        </CardHeader>
        <CardContent>
          <div className="flex gap-4 mb-4">
            <Input
              placeholder="Search alerts..."
              value={filter.search}
              onChange={(e) => setFilter(prev => ({ ...prev, search: e.target.value }))}
              className="flex-1"
            />

            <Select
              multiple
              value={filter.severity}
              onChange={(values) => setFilter(prev => ({ ...prev, severity: values }))}
              placeholder="Severity"
            >
              <option value="critical">Critical</option>
              <option value="error">Error</option>
              <option value="warning">Warning</option>
              <option value="info">Info</option>
            </Select>

            <Select
              multiple
              value={filter.status}
              onChange={(values) => setFilter(prev => ({ ...prev, status: values }))}
              placeholder="Status"
            >
              <option value="open">Open</option>
              <option value="acknowledged">Acknowledged</option>
              <option value="resolved">Resolved</option>
              <option value="suppressed">Suppressed</option>
            </Select>
          </div>

          {/* Bulk Actions */}
          {showActions && selectedAlerts.length > 0 && (
            <div className="border rounded-lg p-4 mb-4 bg-gray-50">
              <h4 className="font-medium mb-2">Bulk Actions</h4>
              <div className="flex gap-2">
                <Button onClick={handleBulkAcknowledge} size="sm">
                  Acknowledge Selected
                </Button>
                <Button onClick={handleBulkResolve} size="sm" variant="outline">
                  Resolve Selected
                </Button>
                <Button
                  onClick={() => {
                    const duration = prompt('Enter suppression duration (e.g., 1h, 30m):');
                    const reason = prompt('Enter suppression reason:');
                    if (duration && reason) {
                      selectedAlerts.forEach(alertId => {
                        onSuppress(alertId, duration, reason);
                      });
                      setSelectedAlerts([]);
                    }
                  }}
                  size="sm"
                  variant="outline"
                >
                  Suppress Selected
                </Button>
              </div>
            </div>
          )}
        </CardContent>
      </Card>

      {/* Alerts List */}
      <Card>
        <CardHeader>
          <CardTitle>
            Alerts ({filteredAlerts.length})
          </CardTitle>
        </CardHeader>
        <CardContent>
          <div className="space-y-2">
            {filteredAlerts.map(alert => (
              <AlertRow
                key={alert.id}
                alert={alert}
                selected={selectedAlerts.includes(alert.id)}
                onSelect={(selected) => {
                  if (selected) {
                    setSelectedAlerts(prev => [...prev, alert.id]);
                  } else {
                    setSelectedAlerts(prev => prev.filter(id => id !== alert.id));
                  }
                }}
                onAcknowledge={onAcknowledge}
                onResolve={onResolve}
                onAssign={onAssign}
                onSuppress={onSuppress}
              />
            ))}
          </div>
        </CardContent>
      </Card>
    </div>
  );
}

interface AlertRowProps {
  alert: Alert;
  selected: boolean;
  onSelect: (selected: boolean) => void;
  onAcknowledge: (alertId: string, comment: string) => void;
  onResolve: (alertId: string, comment: string) => void;
  onAssign: (alertId: string, assignee: string) => void;
  onSuppress: (alertId: string, duration: string, reason: string) => void;
}

function AlertRow({
  alert,
  selected,
  onSelect,
  onAcknowledge,
  onResolve,
  onAssign,
  onSuppress
}: AlertRowProps) {
  const [showDetails, setShowDetails] = useState(false);

  const getSeverityColor = (severity: AlertSeverity) => {
    switch (severity) {
      case 'critical': return 'bg-red-500';
      case 'error': return 'bg-red-400';
      case 'warning': return 'bg-yellow-500';
      case 'info': return 'bg-blue-500';
      default: return 'bg-gray-500';
    }
  };

  const getStatusColor = (status: AlertStatus) => {
    switch (status) {
      case 'open': return 'bg-red-100 text-red-800';
      case 'acknowledged': return 'bg-yellow-100 text-yellow-800';
      case 'resolved': return 'bg-green-100 text-green-800';
      case 'suppressed': return 'bg-gray-100 text-gray-800';
      default: return 'bg-gray-100 text-gray-800';
    }
  };

  return (
    <div className="border rounded-lg p-4 space-y-2">
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-3">
          <input
            type="checkbox"
            checked={selected}
            onChange={(e) => onSelect(e.target.checked)}
          />

          <div className={`w-3 h-3 rounded-full ${getSeverityColor(alert.severity)}`}></div>

          <div>
            <h4 className="font-medium">{alert.name}</h4>
            <p className="text-sm text-gray-600">{alert.description}</p>
          </div>
        </div>

        <div className="flex items-center gap-2">
          <Badge className={getStatusColor(alert.status)}>
            {alert.status}
          </Badge>

          <Badge variant="outline">
            {alert.severity}
          </Badge>

          <span className="text-sm text-gray-500">
            {new Date(alert.triggeredAt).toLocaleString()}
          </span>

          <Button
            variant="ghost"
            size="sm"
            onClick={() => setShowDetails(!showDetails)}
          >
            {showDetails ? 'Hide' : 'Show'} Details
          </Button>
        </div>
      </div>

      {/* Quick Actions */}
      <div className="flex gap-2">
        {alert.status === 'open' && (
          <>
            <Button
              size="sm"
              onClick={() => {
                const comment = prompt('Enter acknowledgment comment:');
                if (comment) onAcknowledge(alert.id, comment);
              }}
            >
              Acknowledge
            </Button>
            <Button
              size="sm"
              variant="outline"
              onClick={() => {
                const assignee = prompt('Enter assignee:');
                if (assignee) onAssign(alert.id, assignee);
              }}
            >
              Assign
            </Button>
          </>
        )}

        {(alert.status === 'open' || alert.status === 'acknowledged') && (
          <Button
            size="sm"
            variant="outline"
            onClick={() => {
              const comment = prompt('Enter resolution comment:');
              if (comment) onResolve(alert.id, comment);
            }}
          >
            Resolve
          </Button>
        )}

        <Button
          size="sm"
          variant="outline"
          onClick={() => {
            const duration = prompt('Enter suppression duration (e.g., 1h, 30m):');
            const reason = prompt('Enter suppression reason:');
            if (duration && reason) onSuppress(alert.id, duration, reason);
          }}
        >
          Suppress
        </Button>
      </div>

      {/* Detailed Information */}
      {showDetails && (
        <div className="border-t pt-4 space-y-3">
          <div className="grid grid-cols-2 gap-4 text-sm">
            <div>
              <span className="font-medium">Rule:</span>
              <span className="ml-2">{alert.ruleName}</span>
            </div>
            <div>
              <span className="font-medium">Source:</span>
              <span className="ml-2">{alert.source}</span>
            </div>
            <div>
              <span className="font-medium">Service:</span>
              <span className="ml-2">{alert.context.service || 'N/A'}</span>
            </div>
            <div>
              <span className="font-medium">Assigned To:</span>
              <span className="ml-2">{alert.assignedTo || 'Unassigned'}</span>
            </div>
          </div>

          {/* Context Information */}
          {Object.keys(alert.context).length > 0 && (
            <div>
              <h5 className="font-medium mb-2">Context:</h5>
              <div className="bg-gray-50 p-3 rounded text-sm">
                {Object.entries(alert.context).map(([key, value]) => (
                  <div key={key}>
                    <span className="font-medium">{key}:</span>
                    <span className="ml-2">{value}</span>
                  </div>
                ))}
              </div>
            </div>
          )}

          {/* Notifications */}
          {alert.notifications.length > 0 && (
            <div>
              <h5 className="font-medium mb-2">Notifications:</h5>
              <div className="space-y-1">
                {alert.notifications.map(notification => (
                  <div key={notification.id} className="text-sm">
                    <span className="font-medium">{notification.channel}</span>
                    <span className="ml-2">→ {notification.recipient}</span>
                    <Badge
                      variant={notification.status === 'sent' ? 'default' : 'destructive'}
                      className="ml-2"
                    >
                      {notification.status}
                    </Badge>
                    <span className="ml-2 text-gray-500">
                      {new Date(notification.sentAt).toLocaleString()}
                    </span>
                  </div>
                ))}
              </div>
            </div>
          )}
        </div>
      )}
    </div>
  );
}
```

## Testing Strategy

### Alert Engine Testing

```typescript
// packages/alerts/src/services/__tests__/alert-engine.service.test.ts
import { AlertEngineService } from '../alert-engine.service';
import { PrometheusService } from '@tamma/observability';
import { EventStore } from '@tamma/events';
import { NotificationService } from '../notification.service';
import { AlertStore } from '../alert.store';

describe('AlertEngineService', () => {
  let alertEngine: AlertEngineService;
  let mockPrometheusService: jest.Mocked<PrometheusService>;
  let mockEventStore: jest.Mocked<EventStore>;
  let mockNotificationService: jest.Mocked<NotificationService>;
  let mockAlertStore: jest.Mocked<AlertStore>;

  beforeEach(() => {
    mockPrometheusService = {
      query: jest.fn(),
    } as any;

    mockEventStore = {
      queryEvents: jest.fn(),
      queryLogs: jest.fn(),
    } as any;

    mockNotificationService = {
      sendNotification: jest.fn(),
    } as any;

    mockAlertStore = {
      getRules: jest.fn(),
      getAlerts: jest.fn(),
      createAlert: jest.fn(),
      updateAlert: jest.fn(),
    } as any;

    alertEngine = new AlertEngineService(
      mockPrometheusService,
      mockEventStore,
      mockNotificationService,
      mockAlertStore,
      {} as any
    );
  });

  describe('evaluateMetricThreshold', () => {
    it('triggers alert when metric exceeds threshold', async () => {
      const rule = {
        id: 'rule-1',
        name: 'High CPU Usage',
        conditions: [
          {
            type: 'metric_threshold',
            metric: 'cpu_usage',
            operator: 'gt',
            threshold: 80,
          },
        ],
        actions: [
          {
            type: 'notification',
            config: { channels: ['email'], recipient: 'admin@example.com' },
            enabled: true,
          },
        ],
        severity: 'warning',
        cooldown: '5m',
      };

      mockPrometheusService.query.mockResolvedValue({
        data: {
          result: [{ value: ['1', '85'] }], // CPU at 85%
        },
      });

      mockAlertStore.getRules.mockResolvedValue([rule]);
      mockAlertStore.getAlerts.mockResolvedValue([]);

      await alertEngine.loadRules();
      await alertEngine.evaluateRules();

      expect(mockAlertStore.createAlert).toHaveBeenCalled();
      expect(mockNotificationService.sendNotification).toHaveBeenCalled();
    });

    it('does not trigger alert when metric is below threshold', async () => {
      const rule = {
        id: 'rule-1',
        name: 'High CPU Usage',
        conditions: [
          {
            type: 'metric_threshold',
            metric: 'cpu_usage',
            operator: 'gt',
            threshold: 80,
          },
        ],
        actions: [],
        severity: 'warning',
        cooldown: '5m',
      };

      mockPrometheusService.query.mockResolvedValue({
        data: {
          result: [{ value: ['1', '75'] }], // CPU at 75%
        },
      });

      mockAlertStore.getRules.mockResolvedValue([rule]);

      await alertEngine.loadRules();
      await alertEngine.evaluateRules();

      expect(mockAlertStore.createAlert).not.toHaveBeenCalled();
    });
  });

  describe('deduplication', () => {
    it('deduplicates alerts with same fingerprint', async () => {
      const rule = {
        id: 'rule-1',
        name: 'High CPU Usage',
        conditions: [
          {
            type: 'metric_threshold',
            metric: 'cpu_usage',
            operator: 'gt',
            threshold: 80,
          },
        ],
        actions: [],
        severity: 'warning',
        cooldown: '5m',
      };

      const existingAlert = {
        id: 'alert-1',
        fingerprint: 'same-fingerprint',
        status: 'open',
      };

      mockPrometheusService.query.mockResolvedValue({
        data: {
          result: [{ value: ['1', '85'] }],
        },
      });

      mockAlertStore.getRules.mockResolvedValue([rule]);
      mockAlertStore.getAlerts.mockResolvedValue([existingAlert]);

      await alertEngine.loadRules();
      await alertEngine.evaluateRules();

      expect(mockAlertStore.createAlert).not.toHaveBeenCalled();
      expect(mockAlertStore.updateAlert).toHaveBeenCalled();
    });
  });

  describe('cooldown', () => {
    it('respects cooldown period', async () => {
      const rule = {
        id: 'rule-1',
        name: 'High CPU Usage',
        conditions: [
          {
            type: 'metric_threshold',
            metric: 'cpu_usage',
            operator: 'gt',
            threshold: 80,
          },
        ],
        actions: [],
        severity: 'warning',
        cooldown: '5m',
      };

      const recentAlert = {
        id: 'alert-1',
        ruleId: 'rule-1',
        triggeredAt: new Date(Date.now() - 2 * 60 * 1000).toISOString(), // 2 minutes ago
        status: 'resolved',
      };

      mockPrometheusService.query.mockResolvedValue({
        data: {
          result: [{ value: ['1', '85'] }],
        },
      });

      mockAlertStore.getRules.mockResolvedValue([rule]);
      mockAlertStore.getAlerts.mockResolvedValue([recentAlert]);

      await alertEngine.loadRules();
      await alertEngine.evaluateRules();

      expect(mockAlertStore.createAlert).not.toHaveBeenCalled();
    });
  });
});
```

### UI Component Testing

```typescript
// packages/alerts/src/components/__tests__/AlertManagementInterface.test.tsx
import React from 'react';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { AlertManagementInterface } from '../AlertManagementInterface';
import { Alert, AlertSeverity, AlertStatus } from '../../types/alert.types';

describe('AlertManagementInterface', () => {
  const mockOnAcknowledge = jest.fn();
  const mockOnResolve = jest.fn();
  const mockOnAssign = jest.fn();
  const mockOnSuppress = jest.fn();

  const mockAlerts: Alert[] = [
    {
      id: 'alert-1',
      name: 'High CPU Usage',
      description: 'CPU usage is above 80%',
      severity: 'warning',
      status: 'open',
      source: 'metric',
      ruleId: 'rule-1',
      ruleName: 'CPU Alert',
      triggeredAt: '2025-01-01T10:00:00Z',
      context: { service: 'api' },
      data: {},
      metadata: {
        fingerprint: 'fp-1',
        tags: {},
        labels: {},
        annotations: {},
        evaluationTime: 100,
        ruleVersion: '1.0',
        processedAt: '2025-01-01T10:00:00Z'
      },
      notifications: [],
      escalations: []
    },
    {
      id: 'alert-2',
      name: 'Database Connection Failed',
      description: 'Unable to connect to database',
      severity: 'critical',
      status: 'acknowledged',
      source: 'health-check',
      ruleId: 'rule-2',
      ruleName: 'Database Health',
      triggeredAt: '2025-01-01T09:30:00Z',
      acknowledgedAt: '2025-01-01T09:45:00Z',
      context: { service: 'database' },
      data: {},
      metadata: {
        fingerprint: 'fp-2',
        tags: {},
        labels: {},
        annotations: {},
        evaluationTime: 50,
        ruleVersion: '1.0',
        processedAt: '2025-01-01T09:30:00Z'
      },
      notifications: [],
      escalations: []
    }
  ];

  beforeEach(() => {
    jest.clearAllMocks();
  });

  it('renders alerts correctly', () => {
    render(
      <AlertManagementInterface
        alerts={mockAlerts}
        onAcknowledge={mockOnAcknowledge}
        onResolve={mockOnResolve}
        onAssign={mockOnAssign}
        onSuppress={mockOnSuppress}
      />
    );

    expect(screen.getByText('Alert Management')).toBeInTheDocument();
    expect(screen.getByText('Alerts (2)')).toBeInTheDocument();
    expect(screen.getByText('High CPU Usage')).toBeInTheDocument();
    expect(screen.getByText('Database Connection Failed')).toBeInTheDocument();
  });

  it('filters alerts by severity', () => {
    render(
      <AlertManagementInterface
        alerts={mockAlerts}
        onAcknowledge={mockOnAcknowledge}
        onResolve={mockOnResolve}
        onAssign={mockOnAssign}
        onSuppress={mockOnSuppress}
      />
    );

    const severitySelect = screen.getByPlaceholderText('Severity');
    fireEvent.change(severitySelect, { target: { value: 'critical' } });

    expect(screen.getByText('Alerts (1)')).toBeInTheDocument();
    expect(screen.getByText('Database Connection Failed')).toBeInTheDocument();
    expect(screen.queryByText('High CPU Usage')).not.toBeInTheDocument();
  });

  it('handles alert acknowledgment', async () => {
    render(
      <AlertManagementInterface
        alerts={mockAlerts}
        onAcknowledge={mockOnAcknowledge}
        onResolve={mockOnResolve}
        onAssign={mockOnAssign}
        onSuppress={mockOnSuppress}
      />
    );

    const acknowledgeButton = screen.getAllByText('Acknowledge')[0];
    fireEvent.click(acknowledgeButton);

    const commentInput = screen.getByDisplayValue(''); // Prompt input
    fireEvent.change(commentInput, { target: { value: 'Acknowledging alert' } });

    // Simulate prompt confirmation
    window.prompt = jest.fn(() => 'Acknowledging alert');
    fireEvent.click(acknowledgeButton);

    await waitFor(() => {
      expect(mockOnAcknowledge).toHaveBeenCalledWith('alert-1', 'Acknowledging alert');
    });
  });

  it('handles bulk actions', async () => {
    render(
      <AlertManagementInterface
        alerts={mockAlerts}
        onAcknowledge={mockOnAcknowledge}
        onResolve={mockOnResolve}
        onAssign={mockOnAssign}
        onSuppress={mockOnSuppress}
      />
    );

    // Select alerts
    const checkboxes = screen.getAllByRole('checkbox');
    fireEvent.click(checkboxes[1]); // First alert checkbox
    fireEvent.click(checkboxes[2]); // Second alert checkbox

    // Show bulk actions
    const actionsButton = screen.getByText('Actions (2)');
    fireEvent.click(actionsButton);

    // Bulk acknowledge
    const bulkAcknowledge = screen.getByText('Acknowledge Selected');
    window.prompt = jest.fn(() => 'Bulk acknowledgment');
    fireEvent.click(bulkAcknowledge);

    await waitFor(() => {
      expect(mockOnAcknowledge).toHaveBeenCalledTimes(2);
      expect(mockOnAcknowledge).toHaveBeenCalledWith('alert-1', 'Bulk acknowledgment');
      expect(mockOnAcknowledge).toHaveBeenCalledWith('alert-2', 'Bulk acknowledgment');
    });
  });
});
```

## Performance Requirements

### Alert Processing

- **Evaluation Latency**: 95th percentile rule evaluation under 5 seconds
- **Alert Creation**: New alerts created and stored within 1 second
- **Notification Delivery**: 95th percentile notification delivery under 30 seconds
- **Deduplication**: Duplicate detection completed within 500ms
- **Concurrent Rules**: Support 1000+ concurrent rule evaluations

### System Performance

- **Memory Usage**: Alert engine memory usage under 512MB
- **CPU Usage**: Rule evaluation CPU usage under 50%
- **Database Performance**: Alert queries complete in under 100ms
- **Notification Throughput**: 1000+ notifications per minute
- **Storage Efficiency**: Compressed alert storage with 60%+ space savings

### Scalability

- **Rule Capacity**: Support 10,000+ alert rules
- **Alert Volume**: Handle 100,000+ alerts per day
- **Concurrent Users**: Support 100+ concurrent users in alert UI
- **Notification Channels**: Support 50+ concurrent notification channels
- **Horizontal Scaling**: Multi-instance alert engine deployment

## Security Considerations

### Access Control

- **Role-based Access**: Different access levels for alert management
- **Alert Privacy**: Sensitive alert data protected by access controls
- **Notification Security**: Secure delivery of sensitive notifications
- **Audit Logging**: All alert actions logged for compliance

### Data Protection

- **PII Protection**: Personal information redacted from alerts
- **Encryption**: Alert data encrypted at rest and in transit
- **Retention Policies**: Configurable alert retention and archival
- **Data Minimization**: Only collect necessary alert data

## Success Metrics

### Operational Excellence

- **MTTA**: Mean Time To Acknowledge under 15 minutes
- **MTTR**: Mean Time To Resolution under 2 hours
- **False Positive Rate**: False positive rate under 10%
- **Alert Fatigue**: Noise score under 30 (0-100 scale)

### System Performance

- **Evaluation Speed**: 95th percentile rule evaluation under 5 seconds
- **Notification Reliability**: 99.9% notification delivery success rate
- **System Availability**: 99.9% uptime for alert system
- **User Satisfaction**: Net Promoter Score (NPS) above 40

### Business Impact

- **Incident Response**: 50%+ reduction in incident response time
- **Service Availability**: 99.95%+ service availability with proactive alerting
- **Operational Efficiency**: 40%+ reduction in manual monitoring effort
- **Cost Savings**: 30%+ reduction in incident-related costs

## Rollout Plan

### Phase 1: Core Alerting (MVP - Sprint 5)

1. **Basic Alert Engine**
   - Rule evaluation engine
   - Metric threshold alerts
   - Email notifications
   - Basic alert UI

2. **Essential Features**
   - Alert acknowledgment and resolution
   - Simple alert rules
   - Basic filtering and search
   - Alert history tracking

### Phase 2: Enhanced Features (Sprint 6)

1. **Advanced Alerting**
   - Multiple notification channels (Slack, PagerDuty)
   - Alert escalation policies
   - Alert suppression and deduplication
   - Composite alert rules

2. **Analytics and Reporting**
   - Alert metrics dashboard
   - MTTA/MTTR tracking
   - Alert effectiveness analysis
   - Trend analysis and predictions

### Phase 3: Optimization (Post-MVP)

1. **Performance Optimization**
   - Rule evaluation optimization
   - Notification delivery optimization
   - Database performance tuning
   - Caching implementation

2. **Advanced Features**
   - Machine learning for anomaly detection
   - Predictive alerting
   - Advanced correlation analysis
   - Automated incident response

## Dependencies

### Internal Dependencies

- **Epic 5.1**: Structured Logging (provides log events)
- **Epic 5.2**: Metrics Collection (provides metric data)
- **Epic 5.3**: Dashboard Framework (provides UI components)
- **Epic 4**: Event Sourcing (provides event data)

### External Dependencies

- **Prometheus**: Metrics collection and querying
- **PostgreSQL**: Alert storage and management
- **Node.js**: Alert engine runtime
- **React**: Alert management UI
- **Email Service**: Email notifications (SendGrid, AWS SES)

### Infrastructure Dependencies

- **Kubernetes**: Scalable deployment platform
- **Load Balancer**: Traffic distribution
- **Monitoring**: System health monitoring
- **Storage**: Sufficient storage for alert data

---

**Story Status**: Ready for Development  
**Implementation Priority**: Partial (Core for MVP, Enhanced for Post-MVP)  
**Target Completion**: Sprint 5 (MVP Core) + Sprint 6 (Enhanced Features)  
**Dependencies**: Epic 5.1, Epic 5.2, Epic 5.3, Epic 4
