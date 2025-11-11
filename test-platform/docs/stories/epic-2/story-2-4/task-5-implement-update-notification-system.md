# Task 5: Implement Update Notification System

## Overview

Implement a real-time notification system for model updates with subscription management, event broadcasting, and robust error handling.

## Objectives

- Create subscription management for model updates with flexible filtering
- Implement real-time notification broadcasting with multiple delivery channels
- Add update filtering and batching capabilities for performance optimization
- Create notification error handling and retry logic with dead letter queue

## Implementation Steps

### Subtask 5.1: Create Subscription Management for Model Updates

**Description**: Implement comprehensive subscription management with filtering, persistence, and lifecycle management.

**Implementation Details**:

1. **Create Subscription Manager**:

```typescript
// packages/providers/src/notifications/subscription-manager.ts
import {
  IModelUpdateSubscriber,
  UpdateSubscription,
  SubscriptionFilter,
  SubscriptionOptions,
  SubscriptionStatus,
  UnsubscribeFunction,
  UpdateEvent,
  ModelUpdateEvent,
  ProviderUpdateEvent,
  CapabilityUpdateEvent,
} from '../interfaces/update-subscriber.interface';

import { EventEmitter } from 'events';
import { v4 as uuidv4 } from 'uuid';

export interface SubscriptionManagerConfig {
  maxSubscriptions: number;
  maxSubscriptionsPerClient: number;
  subscriptionTimeout: number;
  cleanupInterval: number;
  persistenceEnabled: boolean;
  enableMetrics: boolean;
}

export interface SubscriptionMetrics {
  totalSubscriptions: number;
  activeSubscriptions: number;
  subscriptionsByType: Record<string, number>;
  subscriptionsByClient: Record<string, number>;
  averageDeliveryTime: number;
  failedDeliveries: number;
  successfulDeliveries: number;
  lastCleanup: string;
}

export class SubscriptionManager extends EventEmitter implements IModelUpdateSubscriber {
  private subscriptions: Map<string, UpdateSubscription> = new Map();
  private clientSubscriptions: Map<string, Set<string>> = new Map();
  private typeSubscriptions: Map<string, Set<string>> = new Map();
  private config: SubscriptionManagerConfig;
  private metrics: SubscriptionMetrics;
  private cleanupTimer?: NodeJS.Timeout;

  constructor(config: Partial<SubscriptionManagerConfig> = {}) {
    super();
    this.config = this.mergeWithDefaults(config);
    this.metrics = this.initializeMetrics();

    if (this.config.cleanupInterval > 0) {
      this.startCleanupTimer();
    }
  }

  // Core subscription operations
  async subscribe(
    subscription: Omit<UpdateSubscription, 'id' | 'createdAt' | 'status'>
  ): Promise<UnsubscribeFunction> {
    // Validate subscription
    this.validateSubscription(subscription);

    // Check limits
    await this.checkSubscriptionLimits(subscription);

    // Create subscription
    const fullSubscription: UpdateSubscription = {
      id: uuidv4(),
      status: SubscriptionStatus.ACTIVE,
      createdAt: new Date().toISOString(),
      lastActivity: new Date().toISOString(),
      eventCount: 0,
      errorCount: 0,
      ...subscription,
    };

    // Store subscription
    this.subscriptions.set(fullSubscription.id, fullSubscription);
    this.updateIndexes(fullSubscription, 'add');

    // Persist if enabled
    if (this.config.persistenceEnabled) {
      await this.persistSubscription(fullSubscription);
    }

    // Update metrics
    this.updateMetrics('subscription_created');

    this.emit('subscriptionCreated', fullSubscription);

    // Return unsubscribe function
    return this.createUnsubscribeFunction(fullSubscription.id);
  }

  async unsubscribe(subscriptionId: string): Promise<boolean> {
    const subscription = this.subscriptions.get(subscriptionId);
    if (!subscription) {
      return false;
    }

    // Remove from storage
    this.subscriptions.delete(subscriptionId);
    this.updateIndexes(subscription, 'remove');

    // Remove from persistence if enabled
    if (this.config.persistenceEnabled) {
      await this.removePersistedSubscription(subscriptionId);
    }

    // Update metrics
    this.updateMetrics('subscription_removed');

    this.emit('subscriptionRemoved', subscription);

    return true;
  }

  async unsubscribeAll(clientId?: string): Promise<number> {
    let subscriptionsToRemove: string[];

    if (clientId) {
      // Remove all subscriptions for specific client
      const clientSubs = this.clientSubscriptions.get(clientId);
      subscriptionsToRemove = Array.from(clientSubs || []);
    } else {
      // Remove all subscriptions
      subscriptionsToRemove = Array.from(this.subscriptions.keys());
    }

    let removedCount = 0;

    for (const subscriptionId of subscriptionsToRemove) {
      const removed = await this.unsubscribe(subscriptionId);
      if (removed) {
        removedCount++;
      }
    }

    return removedCount;
  }

  // Subscription query operations
  async getSubscription(subscriptionId: string): Promise<UpdateSubscription | undefined> {
    return this.subscriptions.get(subscriptionId);
  }

  async getSubscriptions(filter?: SubscriptionFilter): Promise<UpdateSubscription[]> {
    let subscriptions = Array.from(this.subscriptions.values());

    // Apply filters
    if (filter) {
      subscriptions = this.applySubscriptionFilters(subscriptions, filter);
    }

    return subscriptions;
  }

  async getSubscriptionsByClient(clientId: string): Promise<UpdateSubscription[]> {
    const subscriptionIds = this.clientSubscriptions.get(clientId);
    if (!subscriptionIds) {
      return [];
    }

    return Array.from(subscriptionIds)
      .map((id) => this.subscriptions.get(id))
      .filter(Boolean) as UpdateSubscription[];
  }

  async getSubscriptionsByType(type: string): Promise<UpdateSubscription[]> {
    const subscriptionIds = this.typeSubscriptions.get(type);
    if (!subscriptionIds) {
      return [];
    }

    return Array.from(subscriptionIds)
      .map((id) => this.subscriptions.get(id))
      .filter(Boolean) as UpdateSubscription[];
  }

  // Subscription management operations
  async pauseSubscription(subscriptionId: string): Promise<boolean> {
    const subscription = this.subscriptions.get(subscriptionId);
    if (!subscription || subscription.status === SubscriptionStatus.PAUSED) {
      return false;
    }

    subscription.status = SubscriptionStatus.PAUSED;
    subscription.lastActivity = new Date().toISOString();

    await this.updatePersistedSubscription(subscription);

    this.emit('subscriptionPaused', subscription);
    this.updateMetrics('subscription_paused');

    return true;
  }

  async resumeSubscription(subscriptionId: string): Promise<boolean> {
    const subscription = this.subscriptions.get(subscriptionId);
    if (!subscription || subscription.status !== SubscriptionStatus.PAUSED) {
      return false;
    }

    subscription.status = SubscriptionStatus.ACTIVE;
    subscription.lastActivity = new Date().toISOString();

    await this.updatePersistedSubscription(subscription);

    this.emit('subscriptionResumed', subscription);
    this.updateMetrics('subscription_resumed');

    return true;
  }

  async updateSubscription(
    subscriptionId: string,
    updates: Partial<UpdateSubscription>
  ): Promise<boolean> {
    const subscription = this.subscriptions.get(subscriptionId);
    if (!subscription) {
      return false;
    }

    // Apply updates
    Object.assign(subscription, updates);
    subscription.lastActivity = new Date().toISOString();

    // Re-validate if filters changed
    if (updates.filters) {
      this.validateFilters(updates.filters);
    }

    await this.updatePersistedSubscription(subscription);

    this.emit('subscriptionUpdated', subscription);
    this.updateMetrics('subscription_updated');

    return true;
  }

  // Event publishing (implementation of IModelUpdateSubscriber)
  async publishModelUpdate(event: ModelUpdateEvent): Promise<void> {
    await this.publishEvent('model_updates', event);
  }

  async publishProviderUpdate(event: ProviderUpdateEvent): Promise<void> {
    await this.publishEvent('provider_updates', event);
  }

  async publishCapabilityUpdate(event: CapabilityUpdateEvent): Promise<void> {
    await this.publishEvent('capability_updates', event);
  }

  async publishBatch(events: UpdateEvent[]): Promise<void> {
    const startTime = Date.now();

    try {
      // Group events by type for efficient processing
      const eventsByType = this.groupEventsByType(events);

      for (const [eventType, typeEvents] of Object.entries(eventsByType)) {
        await this.processEventsByType(eventType, typeEvents);
      }

      const duration = Date.now() - startTime;
      this.updateMetrics('batch_published', { eventCount: events.length, duration });

      this.emit('batchPublished', {
        eventCount: events.length,
        duration,
        eventsByType,
      });
    } catch (error) {
      this.updateMetrics('batch_publish_failed', { error: error.message });
      this.emit('batchPublishFailed', { error, events });
      throw error;
    }
  }

  // Metrics and statistics
  async getMetrics(): Promise<SubscriptionMetrics> {
    return { ...this.metrics };
  }

  async getSubscriptionStats(subscriptionId: string): Promise<SubscriptionStats | undefined> {
    const subscription = this.subscriptions.get(subscriptionId);
    if (!subscription) {
      return undefined;
    }

    return {
      id: subscription.id,
      clientId: subscription.clientId,
      type: subscription.type,
      status: subscription.status,
      createdAt: subscription.createdAt,
      lastActivity: subscription.lastActivity,
      eventCount: subscription.eventCount,
      errorCount: subscription.errorCount,
      averageDeliveryTime: subscription.averageDeliveryTime || 0,
      successRate:
        subscription.eventCount > 0
          ? ((subscription.eventCount - subscription.errorCount) / subscription.eventCount) * 100
          : 0,
    };
  }

  async getClientStats(clientId: string): Promise<ClientStats | undefined> {
    const subscriptions = await this.getSubscriptionsByClient(clientId);
    if (subscriptions.length === 0) {
      return undefined;
    }

    const totalEvents = subscriptions.reduce((sum, sub) => sum + sub.eventCount, 0);
    const totalErrors = subscriptions.reduce((sum, sub) => sum + sub.errorCount, 0);
    const avgDeliveryTime =
      subscriptions.reduce((sum, sub) => sum + (sub.averageDeliveryTime || 0), 0) /
      subscriptions.length;

    return {
      clientId,
      subscriptionCount: subscriptions.length,
      activeSubscriptions: subscriptions.filter((sub) => sub.status === SubscriptionStatus.ACTIVE)
        .length,
      totalEvents,
      totalErrors,
      successRate: totalEvents > 0 ? ((totalEvents - totalErrors) / totalEvents) * 100 : 0,
      averageDeliveryTime,
    };
  }

  // Health and maintenance
  async healthCheck(): Promise<SubscriptionManagerHealth> {
    const now = new Date();
    const issues: HealthIssue[] = [];

    // Check subscription limits
    if (this.subscriptions.size > this.config.maxSubscriptions) {
      issues.push({
        type: 'subscription_limit_exceeded',
        severity: 'high',
        message: `Subscription limit exceeded: ${this.subscriptions.size}/${this.config.maxSubscriptions}`,
        timestamp: now.toISOString(),
      });
    }

    // Check for inactive subscriptions
    const inactiveSubscriptions = Array.from(this.subscriptions.values()).filter((sub) => {
      const lastActivity = new Date(sub.lastActivity);
      const inactiveTime = now.getTime() - lastActivity.getTime();
      return inactiveTime > this.config.subscriptionTimeout;
    });

    if (inactiveSubscriptions.length > 0) {
      issues.push({
        type: 'inactive_subscriptions',
        severity: 'medium',
        message: `${inactiveSubscriptions.length} inactive subscriptions found`,
        timestamp: now.toISOString(),
      });
    }

    // Check for error-prone subscriptions
    const errorProneSubscriptions = Array.from(this.subscriptions.values()).filter(
      (sub) => sub.eventCount > 10 && sub.errorCount / sub.eventCount > 0.1
    );

    if (errorProneSubscriptions.length > 0) {
      issues.push({
        type: 'error_prone_subscriptions',
        severity: 'medium',
        message: `${errorProneSubscriptions.length} subscriptions with high error rates`,
        timestamp: now.toISOString(),
      });
    }

    return {
      status: issues.length === 0 ? 'healthy' : 'degraded',
      totalSubscriptions: this.subscriptions.size,
      activeSubscriptions: Array.from(this.subscriptions.values()).filter(
        (sub) => sub.status === SubscriptionStatus.ACTIVE
      ).length,
      issues,
      lastCheck: now.toISOString(),
    };
  }

  async cleanup(): Promise<CleanupResult> {
    const startTime = Date.now();
    let cleanedSubscriptions = 0;
    let cleanedIndexes = 0;

    try {
      // Clean up expired subscriptions
      const now = new Date();
      const expiredSubscriptions = Array.from(this.subscriptions.entries()).filter(
        ([, subscription]) => {
          const lastActivity = new Date(subscription.lastActivity);
          const inactiveTime = now.getTime() - lastActivity.getTime();
          return inactiveTime > this.config.subscriptionTimeout;
        }
      );

      for (const [subscriptionId, subscription] of expiredSubscriptions) {
        this.subscriptions.delete(subscriptionId);
        this.updateIndexes(subscription, 'remove');
        cleanedSubscriptions++;

        if (this.config.persistenceEnabled) {
          await this.removePersistedSubscription(subscriptionId);
        }
      }

      // Clean up orphaned index entries
      cleanedIndexes = this.cleanupIndexes();

      // Update metrics
      this.metrics.lastCleanup = now.toISOString();

      const duration = Date.now() - startTime;

      this.emit('cleanupCompleted', {
        cleanedSubscriptions,
        cleanedIndexes,
        duration,
      });

      return {
        cleanedSubscriptions,
        cleanedIndexes,
        duration,
        timestamp: now.toISOString(),
      };
    } catch (error) {
      this.emit('cleanupFailed', { error });
      throw error;
    }
  }

  // Private helper methods
  private mergeWithDefaults(config: Partial<SubscriptionManagerConfig>): SubscriptionManagerConfig {
    return {
      maxSubscriptions: 10000,
      maxSubscriptionsPerClient: 100,
      subscriptionTimeout: 24 * 60 * 60 * 1000, // 24 hours
      cleanupInterval: 60 * 60 * 1000, // 1 hour
      persistenceEnabled: false,
      enableMetrics: true,
      ...config,
    };
  }

  private initializeMetrics(): SubscriptionMetrics {
    return {
      totalSubscriptions: 0,
      activeSubscriptions: 0,
      subscriptionsByType: {},
      subscriptionsByClient: {},
      averageDeliveryTime: 0,
      failedDeliveries: 0,
      successfulDeliveries: 0,
      lastCleanup: new Date().toISOString(),
    };
  }

  private validateSubscription(
    subscription: Omit<UpdateSubscription, 'id' | 'createdAt' | 'status'>
  ): void {
    if (!subscription.clientId || subscription.clientId.trim() === '') {
      throw new Error('Client ID is required');
    }

    if (!subscription.type || !Object.values(SubscriptionType).includes(subscription.type as any)) {
      throw new Error('Valid subscription type is required');
    }

    if (!subscription.callback || typeof subscription.callback !== 'function') {
      throw new Error('Callback function is required');
    }

    if (subscription.filters) {
      this.validateFilters(subscription.filters);
    }

    if (subscription.options) {
      this.validateOptions(subscription.options);
    }
  }

  private validateFilters(filters: SubscriptionFilter[]): void {
    for (const filter of filters) {
      if (!filter.type || !Object.values(FilterType).includes(filter.type as any)) {
        throw new Error(`Invalid filter type: ${filter.type}`);
      }

      if (!filter.conditions || filter.conditions.length === 0) {
        throw new Error('Filter must have at least one condition');
      }

      for (const condition of filter.conditions) {
        if (
          !condition.field ||
          !condition.operator ||
          !Object.values(FilterOperator).includes(condition.operator as any)
        ) {
          throw new Error(`Invalid filter condition: ${JSON.stringify(condition)}`);
        }
      }
    }
  }

  private validateOptions(options: SubscriptionOptions): void {
    if (options.batchSize && (options.batchSize < 1 || options.batchSize > 1000)) {
      throw new Error('Batch size must be between 1 and 1000');
    }

    if (options.batchTimeout && (options.batchTimeout < 1000 || options.batchTimeout > 300000)) {
      throw new Error('Batch timeout must be between 1 second and 5 minutes');
    }

    if (options.maxRetries && (options.maxRetries < 0 || options.maxRetries > 10)) {
      throw new Error('Max retries must be between 0 and 10');
    }

    if (options.retryDelay && (options.retryDelay < 100 || options.retryDelay > 60000)) {
      throw new Error('Retry delay must be between 100ms and 60 seconds');
    }
  }

  private async checkSubscriptionLimits(
    subscription: Omit<UpdateSubscription, 'id' | 'createdAt' | 'status'>
  ): Promise<void> {
    // Check total subscription limit
    if (this.subscriptions.size >= this.config.maxSubscriptions) {
      throw new Error(`Maximum subscription limit reached: ${this.config.maxSubscriptions}`);
    }

    // Check per-client limit
    const clientSubs = this.clientSubscriptions.get(subscription.clientId);
    if (clientSubs && clientSubs.size >= this.config.maxSubscriptionsPerClient) {
      throw new Error(
        `Maximum subscription limit per client reached: ${this.config.maxSubscriptionsPerClient}`
      );
    }
  }

  private updateIndexes(subscription: UpdateSubscription, operation: 'add' | 'remove'): void {
    const { clientId, type } = subscription;

    // Update client index
    if (operation === 'add') {
      if (!this.clientSubscriptions.has(clientId)) {
        this.clientSubscriptions.set(clientId, new Set());
      }
      this.clientSubscriptions.get(clientId)!.add(subscription.id);
    } else {
      const clientSubs = this.clientSubscriptions.get(clientId);
      if (clientSubs) {
        clientSubs.delete(subscription.id);
        if (clientSubs.size === 0) {
          this.clientSubscriptions.delete(clientId);
        }
      }
    }

    // Update type index
    if (operation === 'add') {
      if (!this.typeSubscriptions.has(type)) {
        this.typeSubscriptions.set(type, new Set());
      }
      this.typeSubscriptions.get(type)!.add(subscription.id);
    } else {
      const typeSubs = this.typeSubscriptions.get(type);
      if (typeSubs) {
        typeSubs.delete(subscription.id);
        if (typeSubs.size === 0) {
          this.typeSubscriptions.delete(type);
        }
      }
    }
  }

  private cleanupIndexes(): number {
    let cleaned = 0;

    // Clean up client index
    for (const [clientId, subscriptionIds] of this.clientSubscriptions) {
      const validIds = new Set<string>();
      for (const subscriptionId of subscriptionIds) {
        if (this.subscriptions.has(subscriptionId)) {
          validIds.add(subscriptionId);
        }
      }

      if (validIds.size !== subscriptionIds.size) {
        this.clientSubscriptions.set(clientId, validIds);
        cleaned += subscriptionIds.size - validIds.size;
      } else if (validIds.size === 0) {
        this.clientSubscriptions.delete(clientId);
        cleaned++;
      }
    }

    // Clean up type index
    for (const [type, subscriptionIds] of this.typeSubscriptions) {
      const validIds = new Set<string>();
      for (const subscriptionId of subscriptionIds) {
        if (this.subscriptions.has(subscriptionId)) {
          validIds.add(subscriptionId);
        }
      }

      if (validIds.size !== subscriptionIds.size) {
        this.typeSubscriptions.set(type, validIds);
        cleaned += subscriptionIds.size - validIds.size;
      } else if (validIds.size === 0) {
        this.typeSubscriptions.delete(type);
        cleaned++;
      }
    }

    return cleaned;
  }

  private createUnsubscribeFunction(subscriptionId: string): UnsubscribeFunction {
    return async () => {
      return this.unsubscribe(subscriptionId);
    };
  }

  private async publishEvent(eventType: string, event: UpdateEvent): Promise<void> {
    const startTime = Date.now();

    try {
      // Get relevant subscriptions
      const relevantSubscriptions = this.getRelevantSubscriptions(eventType, event);

      // Deliver event to subscriptions
      const deliveryPromises = relevantSubscriptions.map((subscription) =>
        this.deliverEvent(subscription, event)
      );

      const results = await Promise.allSettled(deliveryPromises);

      // Process results
      let successfulDeliveries = 0;
      let failedDeliveries = 0;

      for (const result of results) {
        if (result.status === 'fulfilled') {
          successfulDeliveries++;
        } else {
          failedDeliveries++;
        }
      }

      const duration = Date.now() - startTime;

      // Update metrics
      this.updateMetrics('event_published', {
        eventType,
        subscriptionCount: relevantSubscriptions.length,
        successfulDeliveries,
        failedDeliveries,
        duration,
      });

      this.emit('eventPublished', {
        eventType,
        event,
        subscriptionCount: relevantSubscriptions.length,
        successfulDeliveries,
        failedDeliveries,
        duration,
      });
    } catch (error) {
      this.updateMetrics('event_publish_failed', { eventType, error: error.message });
      this.emit('eventPublishFailed', { eventType, event, error });
      throw error;
    }
  }

  private getRelevantSubscriptions(eventType: string, event: UpdateEvent): UpdateSubscription[] {
    const relevantSubscriptions: UpdateSubscription[] = [];

    for (const subscription of this.subscriptions.values()) {
      // Skip inactive subscriptions
      if (subscription.status !== SubscriptionStatus.ACTIVE) {
        continue;
      }

      // Check if subscription type matches
      if (subscription.type !== 'all_updates' && subscription.type !== eventType) {
        continue;
      }

      // Check if filters match
      if (this.matchesFilters(subscription.filters, event)) {
        relevantSubscriptions.push(subscription);
      }
    }

    return relevantSubscriptions;
  }

  private matchesFilters(filters: SubscriptionFilter[], event: UpdateEvent): boolean {
    if (!filters || filters.length === 0) {
      return true;
    }

    for (const filter of filters) {
      if (!this.matchesFilter(filter, event)) {
        return false;
      }
    }

    return true;
  }

  private matchesFilter(filter: SubscriptionFilter, event: UpdateEvent): boolean {
    for (const condition of filter.conditions) {
      if (!this.matchesCondition(condition, event)) {
        return false;
      }
    }

    return filter.operator === 'AND';
  }

  private matchesCondition(condition: FilterCondition, event: UpdateEvent): boolean {
    const fieldValue = this.getFieldValue(event, condition.field);
    const conditionValue = condition.value;

    switch (condition.operator) {
      case FilterOperator.EQUALS:
        return fieldValue === conditionValue;
      case FilterOperator.NOT_EQUALS:
        return fieldValue !== conditionValue;
      case FilterOperator.CONTAINS:
        return typeof fieldValue === 'string' && fieldValue.includes(conditionValue);
      case FilterOperator.NOT_CONTAINS:
        return typeof fieldValue === 'string' && !fieldValue.includes(conditionValue);
      case FilterOperator.STARTS_WITH:
        return typeof fieldValue === 'string' && fieldValue.startsWith(conditionValue);
      case FilterOperator.ENDS_WITH:
        return typeof fieldValue === 'string' && fieldValue.endsWith(conditionValue);
      case FilterOperator.GREATER_THAN:
        return typeof fieldValue === 'number' && fieldValue > conditionValue;
      case FilterOperator.LESS_THAN:
        return typeof fieldValue === 'number' && fieldValue < conditionValue;
      case FilterOperator.IN:
        return Array.isArray(conditionValue) && conditionValue.includes(fieldValue);
      case FilterOperator.NOT_IN:
        return Array.isArray(conditionValue) && !conditionValue.includes(fieldValue);
      case FilterOperator.REGEX:
        return typeof fieldValue === 'string' && new RegExp(conditionValue).test(fieldValue);
      default:
        return false;
    }
  }

  private getFieldValue(event: UpdateEvent, field: string): any {
    switch (field) {
      case 'type':
        return this.getEventType(event);
      case 'modelId':
        return (event as ModelUpdateEvent).modelId;
      case 'providerName':
        return (event as ProviderUpdateEvent).providerName;
      case 'capability':
        return (event as CapabilityUpdateEvent).capability;
      case 'severity':
        return event.severity;
      case 'timestamp':
        return event.timestamp;
      default:
        return undefined;
    }
  }

  private getEventType(event: UpdateEvent): string {
    if ('modelId' in event) return 'model_update';
    if ('providerName' in event) return 'provider_update';
    if ('capability' in event) return 'capability_update';
    return 'unknown';
  }

  private async deliverEvent(subscription: UpdateSubscription, event: UpdateEvent): Promise<void> {
    const startTime = Date.now();

    try {
      // Update subscription activity
      subscription.lastActivity = new Date().toISOString();
      subscription.eventCount++;

      // Call the callback
      await subscription.callback(event);

      // Update subscription metrics
      const deliveryTime = Date.now() - startTime;
      const totalDeliveryTime = subscription.averageDeliveryTime
        ? (subscription.averageDeliveryTime + deliveryTime) / 2
        : deliveryTime;
      subscription.averageDeliveryTime = totalDeliveryTime;

      if (this.config.persistenceEnabled) {
        await this.updatePersistedSubscription(subscription);
      }
    } catch (error) {
      subscription.errorCount++;

      if (this.config.persistenceEnabled) {
        await this.updatePersistedSubscription(subscription);
      }

      throw error;
    }
  }

  private groupEventsByType(events: UpdateEvent[]): Record<string, UpdateEvent[]> {
    const grouped: Record<string, UpdateEvent[]> = {};

    for (const event of events) {
      const eventType = this.getEventType(event);
      if (!grouped[eventType]) {
        grouped[eventType] = [];
      }
      grouped[eventType].push(event);
    }

    return grouped;
  }

  private async processEventsByType(eventType: string, events: UpdateEvent[]): Promise<void> {
    // This could implement type-specific processing optimizations
    // For now, just deliver each event individually
    for (const event of events) {
      await this.publishEvent(eventType, event);
    }
  }

  private updateMetrics(operation: string, data?: any): void {
    if (!this.config.enableMetrics) return;

    switch (operation) {
      case 'subscription_created':
        this.metrics.totalSubscriptions++;
        this.metrics.activeSubscriptions++;
        break;
      case 'subscription_removed':
        this.metrics.totalSubscriptions--;
        if (data?.status === 'active') {
          this.metrics.activeSubscriptions--;
        }
        break;
      case 'subscription_paused':
        this.metrics.activeSubscriptions--;
        break;
      case 'subscription_resumed':
        this.metrics.activeSubscriptions++;
        break;
      case 'event_published':
        if (data?.successfulDeliveries) {
          this.metrics.successfulDeliveries += data.successfulDeliveries;
        }
        if (data?.failedDeliveries) {
          this.metrics.failedDeliveries += data.failedDeliveries;
        }
        break;
    }
  }

  // Persistence methods (placeholders - would be implemented with actual storage)
  private async persistSubscription(subscription: UpdateSubscription): Promise<void> {
    // Implementation would save to database
  }

  private async updatePersistedSubscription(subscription: UpdateSubscription): Promise<void> {
    // Implementation would update in database
  }

  private async removePersistedSubscription(subscriptionId: string): Promise<void> {
    // Implementation would remove from database
  }

  private startCleanupTimer(): void {
    this.cleanupTimer = setInterval(async () => {
      try {
        await this.cleanup();
      } catch (error) {
        this.emit('cleanupError', error);
      }
    }, this.config.cleanupInterval);
  }

  // Cleanup
  async dispose(): Promise<void> {
    if (this.cleanupTimer) {
      clearInterval(this.cleanupTimer);
    }

    this.subscriptions.clear();
    this.clientSubscriptions.clear();
    this.typeSubscriptions.clear();
    this.removeAllListeners();
  }
}

// Supporting interfaces
export interface SubscriptionStats {
  id: string;
  clientId: string;
  type: string;
  status: SubscriptionStatus;
  createdAt: string;
  lastActivity: string;
  eventCount: number;
  errorCount: number;
  averageDeliveryTime: number;
  successRate: number;
}

export interface ClientStats {
  clientId: string;
  subscriptionCount: number;
  activeSubscriptions: number;
  totalEvents: number;
  totalErrors: number;
  successRate: number;
  averageDeliveryTime: number;
}

export interface SubscriptionManagerHealth {
  status: 'healthy' | 'degraded' | 'unhealthy';
  totalSubscriptions: number;
  activeSubscriptions: number;
  issues: HealthIssue[];
  lastCheck: string;
}

export interface HealthIssue {
  type: string;
  severity: 'low' | 'medium' | 'high' | 'critical';
  message: string;
  timestamp: string;
}

export interface CleanupResult {
  cleanedSubscriptions: number;
  cleanedIndexes: number;
  duration: number;
  timestamp: string;
}
```

### Subtask 5.2: Implement Real-time Notification Broadcasting

**Description**: Create high-performance event broadcasting system with multiple delivery channels and connection management.

**Implementation Details**:

1. **Create Notification Broadcaster**:

```typescript
// packages/providers/src/notifications/notification-broadcaster.ts
import { EventEmitter } from 'events';
import { WebSocket, WebSocketServer } from 'ws';
import { Server as HTTPServer } from 'http';
import { UpdateEvent } from '../interfaces/update-subscriber.interface';

export interface BroadcasterConfig {
  enableWebSocket: boolean;
  enableServerSentEvents: boolean;
  enableHTTPCallbacks: boolean;
  webSocketPort?: number;
  webSocketPath?: string;
  maxConnections: number;
  heartbeatInterval: number;
  messageBufferSize: number;
  enableCompression: boolean;
  enableAuthentication: boolean;
  authenticationSecret?: string;
}

export interface ConnectionInfo {
  id: string;
  type: 'websocket' | 'sse' | 'http_callback';
  clientId?: string;
  subscriptionIds: Set<string>;
  connectedAt: string;
  lastActivity: string;
  ipAddress: string;
  userAgent?: string;
  isAuthenticated: boolean;
}

export interface BroadcastMessage {
  id: string;
  type: string;
  event: UpdateEvent;
  timestamp: string;
  compressed?: boolean;
}

export interface DeliveryResult {
  connectionId: string;
  success: boolean;
  error?: string;
  deliveryTime: number;
}

export class NotificationBroadcaster extends EventEmitter {
  private config: BroadcasterConfig;
  private connections: Map<string, ConnectionInfo> = new Map();
  private wsServer?: WebSocketServer;
  private httpServer?: HTTPServer;
  private messageBuffer: Map<string, BroadcastMessage[]> = new Map();
  private heartbeatTimer?: NodeJS.Timeout;

  constructor(config: Partial<BroadcasterConfig> = {}) {
    super();
    this.config = this.mergeWithDefaults(config);

    if (this.config.enableWebSocket) {
      this.initializeWebSocket();
    }

    if (this.config.heartbeatInterval > 0) {
      this.startHeartbeat();
    }
  }

  // Broadcasting operations
  async broadcast(event: UpdateEvent, subscriptionIds?: string[]): Promise<BroadcastResult> {
    const startTime = Date.now();
    const messageId = this.generateMessageId();

    try {
      const message: BroadcastMessage = {
        id: messageId,
        type: this.getEventType(event),
        event,
        timestamp: new Date().toISOString()
      };

      // Get target connections
      const targetConnections = this.getTargetConnections(subscriptionIds);

      if (targetConnections.length === 0) {
        return {
          messageId,
          success: true,
          deliveredConnections: 0,
          failedConnections: 0,
          totalConnections: this.connections.size,
          deliveryTime: Date.now() - startTime
        };
      }

      // Broadcast to connections
      const deliveryPromises = targetConnections.map(connection =>
        this.deliverToConnection(connection, message)
      );

      const results = await Promise.allSettled(deliveryPromises);

      // Process results
      let deliveredConnections = 0;
      let failedConnections = 0;
      const errors: string[] = [];

      for (let i = 0; i < results.length; i++) {
        const result = results[i];
        const connectionId = targetConnections[i].id;

        if (result.status === 'fulfilled') {
          deliveredConnections++;
        } else {
          failedConnections++;
          const error = result.reason?.message || 'Unknown error';
          errors.push(`Connection ${connectionId}: ${error}`);

          // Remove failed connection
          this.removeConnection(connectionId);
        }
      }

      const deliveryTime = Date.now() - startTime;

      const broadcastResult: BroadcastResult = {
        messageId,
        success: failedConnections === 0,
        deliveredConnections,
        failedConnections,
        totalConnections: this.connections.size,
        deliveryTime,
        errors: errors.length > 0 ? errors : undefined
      };

      this.emit('broadcastCompleted', broadcastResult);
      return broadcastResult;

    } catch (error) {
      const deliveryTime = Date.now() - startTime;

      const broadcastResult: BroadcastResult = {
        messageId,
        success: false,
        deliveredConnections: 0,
        failedConnections: 0,
        totalConnections: this.connections.size,
        deliveryTime,
        errors: [error.message]
      };

      this.emit('broadcastFailed', { error, messageId });
      return broadcastResult;
    }
  }

  async broadcastToClient(clientId: string, event: UpdateEvent): Promise<boolean> {
    const clientConnections = Array.from(this.connections.values())
      .filter(conn => conn.clientId === clientId);

    if (clientConnections.length === 0) {
      return false;
    }

    const messageId = this.generateMessageId();
    const message: BroadcastMessage = {
      id: messageId,
      type: this.getEventType(event),
      event,
      timestamp: new Date().toISOString()
    };

    try {
      const deliveryPromises = clientConnections.map(connection =>
        this.deliverToConnection(connection, message)
      );

      const results = await Promise.allSettled(deliveryPromises);

      // Check if all deliveries succeeded
      const allSuccessful = results.every(result => result.status === 'fulfilled');

      if (allSuccessful) {
        this.emit('clientBroadcastCompleted', { clientId, messageId, event });
        return true;
      } else {
        // Remove failed connections
        for (let i = 0; i < results.length; i++) {
          if (results[i].status === 'rejected') {
            this.removeConnection(clientConnections[i].id);
          }
        }

        this.emit('clientBroadcastFailed', { clientId, messageId, event });
        return false;
      }

    } catch (error) {
      this.emit('clientBroadcastFailed', { clientId, messageId, event, error });
      return false;
    }
  }

  // Connection management
  addConnection(connectionInfo: Omit<ConnectionInfo, 'id' | 'connectedAt'>): string {
    // Check connection limits
    if (this.connections.size >= this.config.maxConnections) {
      throw new Error('Maximum connection limit reached');
    }

    const connectionId = this.generateConnectionId();
    const fullConnectionInfo: ConnectionInfo = {
      id: connectionId,
      connectedAt: new Date().toISOString(),
      lastActivity: new Date().toISOString(),
      ...connectionInfo
    };

    this.connections.set(connectionId, fullConnectionInfo);

    this.emit('connectionAdded', fullConnectionInfo);
    return connectionId;
  }

  removeConnection(connectionId: string): boolean {
    const connection = this.connections.get(connectionId);
    if (!connection) {
      return false;
    }

    this.connections.delete(connectionId);

    this.emit('connectionRemoved', connection);
    return true;
  }

  getConnection(connectionId: string): ConnectionInfo | undefined {
    return this.connections.get(connectionId);
  }

  getAllConnections(): ConnectionInfo[] {
    return Array.from(this.connections.values());
  }

  getConnectionsByClient(clientId: string): ConnectionInfo[] {
    return Array.from(this.connections.values())
      .filter(conn => conn.clientId === clientId);
  }

  // Statistics and monitoring
  getStats(): BroadcasterStats {
    const now = new Date();
    const connections = Array.from(this.connections.values());

    return {
      totalConnections: this.connections.size,
      activeConnections: connections.filter(conn =>
        now.getTime() - new Date(conn.lastActivity).getTime() < 30000 // Active in last 30 seconds
      ).length,
      connectionsByType: this.getConnectionsByType(connections),
      connectionsByClient: this.getConnectionsByClient(connections),
      averageConnectionDuration: this.getAverageConnectionDuration(connections),
      messageBufferSize: this.getTotalBufferSize(),
      uptime: this.getUptime()
    };
  }

  // Private helper methods
  private mergeWithDefaults(config: Partial<BroadcasterConfig>): BroadcasterConfig {
    return {
      enableWebSocket: true,
      enableServerSentEvents: true,
      enableHTTPCallbacks: false,
      webSocketPort: 8080,
      webSocketPath: '/ws',
      maxConnections: 10000,
      heartbeatInterval: 30000, // 30 seconds
      messageBufferSize: 1000,
      enableCompression: false,
      enableAuthentication: false,
      ...config
    };
  }

  private initializeWebSocket(): void {
    this.wsServer = new WebSocketServer({
      port: this.config.webSocketPort,
      path: this.config.webSocketPath,
      verifyClient: this.config.enableAuthentication ? this.verifyClient : undefined
    });

    this.wsServer.on('connection', (ws, request) => {
      this.handleWebSocketConnection(ws, request);
    });

    this.wsServer.on('error', (error) => {
      this.emit('serverError', error);
    });

    this.wsServer.on('listening', () => {
      this.emit('serverStarted', {
        type: 'websocket',
        port: this.config.webSocketPort,
        path: this.config.webSocketPath
      });
    });
  }

  private handleWebSocketConnection(ws: WebSocket, request: any): void {
    try {
      const connectionId = this.addConnection({
        type: 'websocket',
        ipAddress: request.socket.remoteAddress,
        userAgent: request.headers['user-agent'],
        isAuthenticated: !this.config.enableAuthentication || request.authenticated
      });

      // Set up WebSocket event handlers
      ws.on('message', (data) => {
        this.handleWebSocketMessage(connectionId, data);
      });

      ws.on('close', () => {
        this.removeConnection(connectionId);
      });

      ws.on('error', (error) => {
        this.emit('connectionError', { connectionId, error });
        this.removeConnection(connectionId);
      });

      ws.on('pong', () => {
        this.updateConnectionActivity(connectionId);
      });

      // Send welcome message
      this.sendToWebSocket(connectionId, {
        type: 'connected',
        connectionId,
        timestamp: new Date().toISOString()
      });

    } catch (error) {
      this.emit('connectionError', { error, request });
    }
  }

  private handleWebSocketMessage(connectionId: string, data: any): void {
    try {
      const message = typeof data === 'string' ? JSON.parse(data) : data;

      this.updateConnectionActivity(connectionId);

      // Handle different message types
      switch (message.type) {
        case 'subscribe':
          this.handleSubscriptionMessage(connectionId, message);
          break;
        case 'unsubscribe':
          this.handleUnsubscriptionMessage(connectionId, message);
          break;
        case 'ping':
          this.sendToWebSocket(connectionId, { type: 'pong', timestamp: new Date().toISOString() });
          break;
        case 'authenticate':
          this.handleAuthenticationMessage(connectionId, message);
          break;
        default:
          this.emit('unknownMessage', { connectionId, message });
      }

    } catch (error) {
      this.emit('messageError', { connectionId, error, data });
    }
  }

  private handleSubscriptionMessage(connectionId: string, message: any): void {
    const connection = this.connections.get(connectionId);
    if (!connection) return;

    // Add subscription IDs to connection
    if (message.subscriptionIds && Array.isArray(message.subscriptionIds)) {
      for (const subscriptionId of message.subscriptionIds) {
        connection.subscriptionIds.add(subscriptionId);
      }
    }

    this.sendToWebSocket(connectionId, {
      type: 'subscribed',
      subscriptionIds: Array.from(connection.subscriptionIds),
      timestamp: new Date().toISOString()
    });
  }

  private handleUnsubscriptionMessage(connectionId: string, message: any): void {
    const connection = this.connections.get(connectionId);
    if (!connection) return;

    // Remove subscription IDs from connection
    if (message.subscriptionIds && Array.isArray(message.subscriptionIds)) {
      for (const subscriptionId of message.subscriptionIds) {
        connection.subscriptionIds.delete(subscriptionId);
      }
    }

    this.sendToWebSocket(connectionId, {
      type: 'unsubscribed',
      subscriptionIds: Array.from(connection.subscriptionIds),
      timestamp: new Date().toISOString()
    });
  }

  private handleAuthenticationMessage(connectionId: string, message: any): void {
    if (!this.config.enableAuthentication || !this.config.authenticationSecret) {
      return;
    }

    const connection = this.connections.get(connectionId);
    if (!connection) return;

    // Verify authentication token
    const isValid = this.verifyAuthToken(message.token);

    if (isValid) {
      connection.isAuthenticated = true;
      this.sendToWebSocket(connectionId, {
        type: 'authenticated',
        success: true,
        timestamp: new Date().toISOString()
      });
    } else {
      this.sendToWebSocket(connectionId, {
        type: 'authenticated',
        success: false,
        error: 'Invalid authentication token',
        timestamp: new Date().toISOString()
      });

      // Close connection after failed authentication
      setTimeout(() => {
        this.removeConnection(connectionId);
      }, 1000);
    }
  }

  private verifyAuthToken(token: string): boolean {
    // Simple token verification - in production, use proper JWT verification
    return token === this.config.authenticationSecret;
  }

  private verifyClient(info: any): (result: boolean, code?: number, message?: string, headers?: any) => {
    if (!this.config.enableAuthentication) {
      return true;
    }

    const token = this.extractTokenFromRequest(info);
    const isValid = this.verifyAuthToken(token);

    return isValid;
  }

  private extractTokenFromRequest(request: any): string | null {
    // Extract token from Authorization header or query parameter
    const authHeader = request.headers.authorization;
    if (authHeader && authHeader.startsWith('Bearer ')) {
      return authHeader.substring(7);
    }

    const url = new URL(request.url || '', `http://${request.headers.host}`);
    return url.searchParams.get('token');
  }

  private async deliverToConnection(connection: ConnectionInfo, message: BroadcastMessage): Promise<DeliveryResult> {
    const startTime = Date.now();

    try {
      switch (connection.type) {
        case 'websocket':
          await this.sendToWebSocket(connection.id, message);
          break;
        case 'sse':
          await this.sendToSSE(connection, message);
          break;
        case 'http_callback':
          await this.sendToHTTPCallback(connection, message);
          break;
        default:
          throw new Error(`Unknown connection type: ${connection.type}`);
      }

      return {
        connectionId: connection.id,
        success: true,
        deliveryTime: Date.now() - startTime
      };

    } catch (error) {
      return {
        connectionId: connection.id,
        success: false,
        error: error.message,
        deliveryTime: Date.now() - startTime
      };
    }
  }

  private async sendToWebSocket(connectionId: string, message: any): Promise<void> {
    const connection = this.connections.get(connectionId);
    if (!connection || connection.type !== 'websocket') {
      return;
    }

    const ws = this.getWebSocketConnection(connectionId);
    if (!ws) {
      return;
    }

    const messageString = JSON.stringify(message);

    if (ws.readyState === WebSocket.OPEN) {
      ws.send(messageString);
    } else {
      throw new Error('WebSocket is not open');
    }
  }

  private async sendToSSE(connection: ConnectionInfo, message: BroadcastMessage): Promise<void> {
    // SSE implementation would go here
    // For now, just log
    console.log(`SSE delivery to ${connection.id}:`, message);
  }

  private async sendToHTTPCallback(connection: ConnectionInfo, message: BroadcastMessage): Promise<void> {
    // HTTP callback implementation would go here
    // For now, just log
    console.log(`HTTP callback delivery to ${connection.id}:`, message);
  }

  private getWebSocketConnection(connectionId: string): WebSocket | null {
    // This would need to track WebSocket instances
    // For now, return null as placeholder
    return null;
  }

  private getTargetConnections(subscriptionIds?: string[]): ConnectionInfo[] {
    if (!subscriptionIds || subscriptionIds.length === 0) {
      return Array.from(this.connections.values());
    }

    return Array.from(this.connections.values())
      .filter(connection =>
        Array.from(connection.subscriptionIds).some(subId => subscriptionIds.includes(subId))
      );
  }

  private getEventType(event: UpdateEvent): string {
    if ('modelId' in event) return 'model_update';
    if ('providerName' in event) return 'provider_update';
    if ('capability' in event) return 'capability_update';
    return 'unknown';
  }

  private updateConnectionActivity(connectionId: string): void {
    const connection = this.connections.get(connectionId);
    if (connection) {
      connection.lastActivity = new Date().toISOString();
    }
  }

  private generateMessageId(): string {
    return `msg_${Date.now()}_${Math.random().toString(36).substr(2, 9)}`;
  }

  private generateConnectionId(): string {
    return `conn_${Date.now()}_${Math.random().toString(36).substr(2, 9)}`;
  }

  private getConnectionsByType(connections: ConnectionInfo[]): Record<string, number> {
    const types: Record<string, number> = {};

    for (const connection of connections) {
      types[connection.type] = (types[connection.type] || 0) + 1;
    }

    return types;
  }

  private getConnectionsByClient(connections: ConnectionInfo[]): Record<string, number> {
    const clients: Record<string, number> = {};

    for (const connection of connections) {
      if (connection.clientId) {
        clients[connection.clientId] = (clients[connection.clientId] || 0) + 1;
      }
    }

    return clients;
  }

  private getAverageConnectionDuration(connections: ConnectionInfo[]): number {
    if (connections.length === 0) return 0;

    const now = Date.now();
    const totalDuration = connections.reduce((sum, conn) => {
      return sum + (now - new Date(conn.connectedAt).getTime());
    }, 0);

    return totalDuration / connections.length;
  }

  private getTotalBufferSize(): number {
    return Array.from(this.messageBuffer.values())
      .reduce((sum, messages) => sum + messages.length, 0);
  }

  private getUptime(): number {
    // This would track server start time
    return Date.now() - (Date.now() - 1000); // Placeholder
  }

  private startHeartbeat(): void {
    this.heartbeatTimer = setInterval(() => {
      this.sendHeartbeat();
    }, this.config.heartbeatInterval);
  }

  private sendHeartbeat(): void {
    const heartbeatMessage = {
      type: 'heartbeat',
      timestamp: new Date().toISOString(),
      stats: this.getStats()
    };

    for (const connection of this.connections.values()) {
      if (connection.type === 'websocket') {
        this.sendToWebSocket(connection.id, heartbeatMessage).catch(error => {
          this.emit('heartbeatError', { connectionId: connection.id, error });
        });
      }
    }
  }

  // Cleanup
  async dispose(): Promise<void> {
    if (this.heartbeatTimer) {
      clearInterval(this.heartbeatTimer);
    }

    if (this.wsServer) {
      this.wsServer.close();
    }

    if (this.httpServer) {
      this.httpServer.close();
    }

    this.connections.clear();
    this.messageBuffer.clear();
    this.removeAllListeners();
  }
}

// Supporting interfaces
export interface BroadcastResult {
  messageId: string;
  success: boolean;
  deliveredConnections: number;
  failedConnections: number;
  totalConnections: number;
  deliveryTime: number;
  errors?: string[];
}

export interface BroadcasterStats {
  totalConnections: number;
  activeConnections: number;
  connectionsByType: Record<string, number>;
  connectionsByClient: Record<string, number>;
  averageConnectionDuration: number;
  messageBufferSize: number;
  uptime: number;
}
```

### Subtask 5.3: Add Update Filtering and Batching Capabilities

**Description**: Implement intelligent event filtering, message batching, and performance optimization for high-volume scenarios.

**Implementation Details**:

1. **Create Event Filter and Batcher**:

```typescript
// packages/providers/src/notifications/event-filter-batcher.ts
import { UpdateEvent, SubscriptionFilter } from '../interfaces/update-subscriber.interface';
import { EventEmitter } from 'events';

export interface FilterBatcherConfig {
  maxBatchSize: number;
  maxBatchWaitTime: number;
  enableDeduplication: boolean;
  deduplicationWindow: number;
  enableCompression: boolean;
  compressionThreshold: number;
  enablePriorityQueuing: boolean;
  maxQueueSize: number;
}

export interface BatchedEvent {
  id: string;
  events: UpdateEvent[];
  subscriptionIds: Set<string>;
  createdAt: string;
  priority: 'low' | 'normal' | 'high' | 'critical';
  size: number;
  compressed?: boolean;
}

export interface FilterContext {
  subscriptionId: string;
  filters: SubscriptionFilter[];
  eventType: string;
}

export class EventFilterBatcher extends EventEmitter {
  private config: FilterBatcherConfig;
  private eventQueue: Map<string, BatchedEvent> = new Map();
  private deduplicationCache: Map<string, number> = new Map();
  private batchTimers: Map<string, NodeJS.Timeout> = new Map();
  private priorityQueues: Map<string, UpdateEvent[]> = new Map();

  constructor(config: Partial<FilterBatcherConfig> = {}) {
    super();
    this.config = this.mergeWithDefaults(config);
  }

  // Event processing
  async processEvent(event: UpdateEvent, subscriptionIds: Set<string>): Promise<void> {
    // Deduplication check
    if (this.config.enableDeduplication && this.isDuplicate(event)) {
      this.emit('eventDeduplicated', { event, subscriptionIds });
      return;
    }

    // Filter events for each subscription
    const filteredEvents = new Map<string, UpdateEvent>();

    for (const subscriptionId of subscriptionIds) {
      const subscription = await this.getSubscription(subscriptionId);
      if (!subscription) continue;

      if (this.passesFilters(event, subscription.filters, subscription.type)) {
        filteredEvents.set(subscriptionId, event);
      }
    }

    if (filteredEvents.size === 0) {
      return;
    }

    // Add to appropriate priority queue
    const priority = this.determinePriority(event);
    const queueKey = this.getQueueKey(priority);

    if (!this.priorityQueues.has(queueKey)) {
      this.priorityQueues.set(queueKey, []);
    }

    this.priorityQueues.get(queueKey)!.push(event);

    // Trigger batch processing if needed
    this.triggerBatchProcessing(priority);
  }

  // Batch management
  async createBatch(subscriptionId: string, priority: string): Promise<BatchedEvent | null> {
    const queueKey = this.getQueueKey(priority);
    const queue = this.priorityQueues.get(queueKey) || [];

    if (queue.length === 0) {
      return null;
    }

    // Take events from queue
    const events = queue.splice(0, Math.min(this.config.maxBatchSize, queue.length));

    if (events.length === 0) {
      return null;
    }

    const batchId = this.generateBatchId();
    const batch: BatchedEvent = {
      id: batchId,
      events,
      subscriptionIds: new Set([subscriptionId]),
      createdAt: new Date().toISOString(),
      priority: priority as any,
      size: this.calculateBatchSize(events),
      compressed: false,
    };

    // Compress if needed
    if (this.config.enableCompression && batch.size > this.config.compressionThreshold) {
      batch.compressed = true;
      batch.events = await this.compressEvents(events);
    }

    this.eventQueue.set(batchId, batch);
    return batch;
  }

  async processBatch(batchId: string): Promise<void> {
    const batch = this.eventQueue.get(batchId);
    if (!batch) {
      return;
    }

    try {
      // Decompress if needed
      let events = batch.events;
      if (batch.compressed) {
        events = await this.decompressEvents(batch.events);
      }

      // Process batch
      await this.deliverBatch(batch);

      // Clean up
      this.eventQueue.delete(batchId);

      this.emit('batchProcessed', {
        batchId,
        eventsProcessed: events.length,
        subscriptionIds: Array.from(batch.subscriptionIds),
        priority: batch.priority,
        processingTime: Date.now() - new Date(batch.createdAt).getTime(),
      });
    } catch (error) {
      this.emit('batchProcessingFailed', {
        batchId,
        error,
        batch,
      });

      // Retry logic could be implemented here
      this.retryBatch(batchId);
    }
  }

  // Filtering logic
  private passesFilters(
    event: UpdateEvent,
    filters: SubscriptionFilter[],
    eventType: string
  ): boolean {
    if (!filters || filters.length === 0) {
      return true;
    }

    for (const filter of filters) {
      if (!this.matchesFilter(filter, event, eventType)) {
        return false;
      }
    }

    return filter.operator === 'OR' || this.allFiltersMatch(filters, event, eventType);
  }

  private matchesFilter(
    filter: SubscriptionFilter,
    event: UpdateEvent,
    eventType: string
  ): boolean {
    for (const condition of filter.conditions) {
      if (!this.evaluateCondition(condition, event, eventType)) {
        return false;
      }
    }

    return true;
  }

  private allFiltersMatch(
    filters: SubscriptionFilter[],
    event: UpdateEvent,
    eventType: string
  ): boolean {
    for (const filter of filters) {
      if (!this.matchesFilter(filter, event, eventType)) {
        return false;
      }
    }

    return true;
  }

  private evaluateCondition(condition: any, event: UpdateEvent, eventType: string): boolean {
    const fieldValue = this.getFieldValue(event, condition.field);
    const conditionValue = condition.value;

    switch (condition.operator) {
      case 'equals':
        return fieldValue === conditionValue;
      case 'not_equals':
        return fieldValue !== conditionValue;
      case 'contains':
        return typeof fieldValue === 'string' && fieldValue.includes(conditionValue);
      case 'not_contains':
        return typeof fieldValue === 'string' && !fieldValue.includes(conditionValue);
      case 'starts_with':
        return typeof fieldValue === 'string' && fieldValue.startsWith(conditionValue);
      case 'ends_with':
        return typeof fieldValue === 'string' && fieldValue.endsWith(conditionValue);
      case 'greater_than':
        return typeof fieldValue === 'number' && fieldValue > conditionValue;
      case 'less_than':
        return typeof fieldValue === 'number' && fieldValue < conditionValue;
      case 'in':
        return Array.isArray(conditionValue) && conditionValue.includes(fieldValue);
      case 'not_in':
        return Array.isArray(conditionValue) && !conditionValue.includes(fieldValue);
      case 'regex':
        return typeof fieldValue === 'string' && new RegExp(conditionValue).test(fieldValue);
      default:
        return false;
    }
  }

  private getFieldValue(event: UpdateEvent, field: string): any {
    switch (field) {
      case 'type':
        return this.getEventType(event);
      case 'modelId':
        return (event as any).modelId;
      case 'providerName':
        return (event as any).providerName;
      case 'capability':
        return (event as any).capability;
      case 'severity':
        return event.severity;
      case 'timestamp':
        return event.timestamp;
      default:
        return (event as any)[field];
    }
  }

  private getEventType(event: UpdateEvent): string {
    if ('modelId' in event) return 'model_update';
    if ('providerName' in event) return 'provider_update';
    if ('capability' in event) return 'capability_update';
    return 'unknown';
  }

  // Priority and queuing
  private determinePriority(event: UpdateEvent): string {
    // Determine priority based on event properties
    if (event.severity === 'critical') return 'critical';
    if (event.severity === 'high') return 'high';
    if (event.type === 'model_update') return 'normal';
    return 'low';
  }

  private getQueueKey(priority: string): string {
    return `queue_${priority}`;
  }

  private triggerBatchProcessing(priority: string): void {
    const queueKey = this.getQueueKey(priority);
    const queue = this.priorityQueues.get(queueKey) || [];

    // Check if we should process the batch now
    const shouldProcess =
      queue.length >= this.config.maxBatchSize || this.shouldProcessOldestBatch(queueKey);

    if (shouldProcess) {
      this.scheduleBatchProcessing(queueKey);
    }
  }

  private shouldProcessOldestBatch(queueKey: string): boolean {
    const timer = this.batchTimers.get(queueKey);
    return !timer; // No timer means we should process immediately
  }

  private scheduleBatchProcessing(queueKey: string): void {
    // Clear existing timer
    const existingTimer = this.batchTimers.get(queueKey);
    if (existingTimer) {
      clearTimeout(existingTimer);
    }

    // Schedule new timer
    const timer = setTimeout(async () => {
      this.batchTimers.delete(queueKey);
      await this.processNextBatch(queueKey);
    }, this.config.maxBatchWaitTime);

    this.batchTimers.set(queueKey, timer);
  }

  private async processNextBatch(queueKey: string): Promise<void> {
    const queue = this.priorityQueues.get(queueKey) || [];
    if (queue.length === 0) {
      return;
    }

    // Get subscription ID (simplified - in reality, this would be more sophisticated)
    const subscriptionId = 'default'; // This would come from subscription context

    const batch = await this.createBatch(subscriptionId, queueKey.split('_')[1]);
    if (batch) {
      await this.processBatch(batch.id);
    }
  }

  // Deduplication
  private isDuplicate(event: UpdateEvent): boolean {
    const eventKey = this.generateEventKey(event);
    const now = Date.now();
    const lastSeen = this.deduplicationCache.get(eventKey);

    if (lastSeen && now - lastSeen < this.config.deduplicationWindow) {
      return true;
    }

    this.deduplicationCache.set(eventKey, now);
    return false;
  }

  private generateEventKey(event: UpdateEvent): string {
    // Create a unique key for the event based on its content
    const keyData = {
      type: this.getEventType(event),
      modelId: (event as any).modelId,
      providerName: (event as any).providerName,
      capability: (event as any).capability,
      timestamp: event.timestamp,
    };

    return Buffer.from(JSON.stringify(keyData)).toString('base64');
  }

  // Compression
  private async compressEvents(events: UpdateEvent[]): Promise<any> {
    // Simple compression implementation - in reality, use proper compression
    return {
      compressed: true,
      originalSize: JSON.stringify(events).length,
      data: events, // Placeholder - would be compressed
    };
  }

  private async decompressEvents(compressedData: any): Promise<UpdateEvent[]> {
    // Simple decompression implementation - in reality, use proper decompression
    if (compressedData.compressed) {
      return compressedData.data; // Placeholder - would be decompressed
    }
    return compressedData;
  }

  // Batch size calculation
  private calculateBatchSize(events: UpdateEvent[]): number {
    return JSON.stringify(events).length;
  }

  // Batch delivery
  private async deliverBatch(batch: BatchedEvent): Promise<void> {
    // This would deliver the batch to subscribers
    // Implementation depends on the delivery mechanism

    for (const subscriptionId of batch.subscriptionIds) {
      // Deliver to each subscription
      for (const event of batch.events) {
        // Deliver event to subscription
        this.emit('eventDelivered', {
          subscriptionId,
          event,
          batchId: batch.id,
        });
      }
    }
  }

  private async retryBatch(batchId: string): Promise<void> {
    const batch = this.eventQueue.get(batchId);
    if (!batch) {
      return;
    }

    // Implement retry logic with exponential backoff
    const retryDelay = Math.min(1000 * Math.pow(2, batch.retryCount || 0), 30000);

    setTimeout(async () => {
      try {
        await this.deliverBatch(batch);
        this.eventQueue.delete(batchId);
      } catch (error) {
        // Increment retry count and try again
        batch.retryCount = (batch.retryCount || 0) + 1;
        this.eventQueue.set(batchId, batch);

        if (batch.retryCount < 3) {
          // Max 3 retries
          await this.retryBatch(batchId);
        } else {
          this.emit('batchRetryFailed', { batchId, error });
          this.eventQueue.delete(batchId);
        }
      }
    }, retryDelay);
  }

  // Utility methods
  private generateBatchId(): string {
    return `batch_${Date.now()}_${Math.random().toString(36).substr(2, 9)}`;
  }

  private async getSubscription(subscriptionId: string): Promise<any> {
    // This would get subscription details from subscription manager
    // For now, return a mock subscription
    return {
      id: subscriptionId,
      filters: [],
      type: 'all_updates',
    };
  }

  // Statistics and monitoring
  getStats(): FilterBatcherStats {
    const queueSizes = new Map<string, number>();

    for (const [queueKey, queue] of this.priorityQueues) {
      queueSizes.set(queueKey, queue.length);
    }

    return {
      totalQueuedEvents: Array.from(this.priorityQueues.values()).reduce(
        (sum, queue) => sum + queue.length,
        0
      ),
      queueSizesByPriority: Object.fromEntries(queueSizes),
      totalBatches: this.eventQueue.size,
      deduplicationCacheSize: this.deduplicationCache.size,
      averageBatchSize: this.calculateAverageBatchSize(),
      compressionRatio: this.calculateCompressionRatio(),
    };
  }

  private calculateAverageBatchSize(): number {
    const batches = Array.from(this.eventQueue.values());
    if (batches.length === 0) return 0;

    const totalSize = batches.reduce((sum, batch) => sum + batch.size, 0);
    return totalSize / batches.length;
  }

  private calculateCompressionRatio(): number {
    const batches = Array.from(this.eventQueue.values());
    const compressedBatches = batches.filter((batch) => batch.compressed);

    if (compressedBatches.length === 0) return 0;

    const totalCompressedSize = compressedBatches.reduce((sum, batch) => {
      return sum + (batch as any).originalSize || 0;
    }, 0);

    const totalOriginalSize = compressedBatches.reduce((sum, batch) => {
      return sum + this.calculateBatchSize(batch.events);
    }, 0);

    return totalOriginalSize > 0 ? totalCompressedSize / totalOriginalSize : 0;
  }

  // Cleanup
  async cleanup(): Promise<CleanupResult> {
    const startTime = Date.now();
    let cleanedBatches = 0;
    let cleanedDeduplication = 0;
    let cleanedTimers = 0;

    try {
      // Clean up old batches
      const now = Date.now();
      const maxAge = 5 * 60 * 1000; // 5 minutes

      for (const [batchId, batch] of this.eventQueue.entries()) {
        const age = now - new Date(batch.createdAt).getTime();
        if (age > maxAge) {
          this.eventQueue.delete(batchId);
          cleanedBatches++;
        }
      }

      // Clean up deduplication cache
      const cutoffTime = now - this.config.deduplicationWindow;
      for (const [key, timestamp] of this.deduplicationCache.entries()) {
        if (timestamp < cutoffTime) {
          this.deduplicationCache.delete(key);
          cleanedDeduplication++;
        }
      }

      // Clear expired timers
      for (const [queueKey, timer] of this.batchTimers.entries()) {
        clearTimeout(timer);
        cleanedTimers++;
      }
      this.batchTimers.clear();

      // Restart necessary timers
      for (const [queueKey, queue] of this.priorityQueues.entries()) {
        if (queue.length > 0) {
          this.triggerBatchProcessing(queueKey);
        }
      }

      const duration = Date.now() - startTime;

      this.emit('cleanupCompleted', {
        cleanedBatches,
        cleanedDeduplication,
        cleanedTimers,
        duration,
      });

      return {
        cleanedBatches,
        cleanedDeduplication,
        cleanedTimers,
        duration,
        timestamp: new Date().toISOString(),
      };
    } catch (error) {
      this.emit('cleanupFailed', error);
      throw error;
    }
  }

  // Dispose
  dispose(): void {
    // Clear all timers
    for (const timer of this.batchTimers.values()) {
      clearTimeout(timer);
    }
    this.batchTimers.clear();

    // Clear all data structures
    this.eventQueue.clear();
    this.deduplicationCache.clear();
    this.priorityQueues.clear();
    this.removeAllListeners();
  }

  private mergeWithDefaults(config: Partial<FilterBatcherConfig>): FilterBatcherConfig {
    return {
      maxBatchSize: 100,
      maxBatchWaitTime: 5000, // 5 seconds
      enableDeduplication: true,
      deduplicationWindow: 30000, // 30 seconds
      enableCompression: false,
      compressionThreshold: 1024, // 1KB
      enablePriorityQueuing: true,
      maxQueueSize: 10000,
      ...config,
    };
  }
}

// Supporting interfaces
export interface FilterBatcherStats {
  totalQueuedEvents: number;
  queueSizesByPriority: Record<string, number>;
  totalBatches: number;
  deduplicationCacheSize: number;
  averageBatchSize: number;
  compressionRatio: number;
}

export interface CleanupResult {
  cleanedBatches: number;
  cleanedDeduplication: number;
  cleanedTimers: number;
  duration: number;
  timestamp: string;
}
```

### Subtask 5.4: Create Notification Error Handling and Retry Logic

**Description**: Implement robust error handling with retry mechanisms, dead letter queue, and circuit breaker patterns.

**Implementation Details**:

1. **Create Error Handler and Retry Manager**:

```typescript
// packages/providers/src/notifications/error-handler-retry.ts
import { EventEmitter } from 'events';
import { UpdateEvent, DeliveryResult } from '../interfaces/update-subscriber.interface';

export interface ErrorHandlerConfig {
  maxRetries: number;
  baseDelay: number;
  maxDelay: number;
  backoffMultiplier: number;
  jitter: boolean;
  deadLetterQueueMaxSize: number;
  circuitBreakerThreshold: number;
  circuitBreakerTimeout: number;
  enableMetrics: boolean;
}

export interface RetryAttempt {
  id: string;
  subscriptionId: string;
  event: UpdateEvent;
  attempt: number;
  maxAttempts: number;
  delay: number;
  error?: Error;
  timestamp: string;
}

export interface DeadLetterMessage {
  id: string;
  subscriptionId: string;
  event: UpdateEvent;
  error: Error;
  attempts: number;
  finalError: Error;
  timestamp: string;
  reason: string;
}

export interface CircuitBreakerState {
  isOpen: boolean;
  failureCount: number;
  lastFailureTime: number;
  nextAttemptTime: number;
  state: 'closed' | 'open' | 'half-open';
}

export class ErrorHandlerRetryManager extends EventEmitter {
  private config: ErrorHandlerConfig;
  private retryAttempts: Map<string, RetryAttempt> = new Map();
  private deadLetterQueue: DeadLetterMessage[] = [];
  private circuitBreaker: CircuitBreakerState;
  private metrics: ErrorHandlerMetrics;

  constructor(config: Partial<ErrorHandlerConfig> = {}) {
    super();
    this.config = this.mergeWithDefaults(config);
    this.circuitBreaker = this.initializeCircuitBreaker();
    this.metrics = this.initializeMetrics();
  }

  // Error handling and retry logic
  async handleDeliveryError(
    subscriptionId: string,
    event: UpdateEvent,
    error: Error,
    attempt: number
  ): Promise<DeliveryResult> {
    const retryAttemptId = this.generateRetryAttemptId();

    try {
      // Determine if we should retry
      const shouldRetry = this.shouldRetry(error, attempt);

      if (shouldRetry) {
        // Create retry attempt
        const retryAttempt: RetryAttempt = {
          id: retryAttemptId,
          subscriptionId,
          event,
          attempt: attempt + 1,
          maxAttempts: this.config.maxRetries,
          delay: this.calculateRetryDelay(attempt),
          error,
          timestamp: new Date().toISOString(),
        };

        this.retryAttempts.set(retryAttemptId, retryAttempt);

        // Update metrics
        this.updateMetrics('retry_attempted', { errorType: error.name, attempt: attempt + 1 });

        // Schedule retry
        await this.scheduleRetry(retryAttempt);

        // Return pending result
        return {
          connectionId: subscriptionId,
          success: false,
          error: error.message,
          deliveryTime: 0,
          retryScheduled: true,
          retryAttemptId,
        };
      } else {
        // Don't retry - add to dead letter queue
        await this.addToDeadLetterQueue(subscriptionId, event, error, attempt);

        // Update metrics
        this.updateMetrics('delivery_failed', {
          errorType: error.name,
          finalAttempt: attempt,
          reason: 'non_retryable',
        });

        // Check circuit breaker
        this.checkCircuitBreaker();

        return {
          connectionId: subscriptionId,
          success: false,
          error: error.message,
          deliveryTime: 0,
          addedToDeadLetterQueue: true,
        };
      }
    } catch (handlingError) {
      this.updateMetrics('handling_error', {
        originalError: error.message,
        handlingError: handlingError.message,
      });

      return {
        connectionId: subscriptionId,
        success: false,
        error: `Handling error: ${handlingError.message}`,
        deliveryTime: 0,
      };
    }
  }

  async retryDelivery(retryAttemptId: string): Promise<DeliveryResult> {
    const retryAttempt = this.retryAttempts.get(retryAttemptId);
    if (!retryAttempt) {
      throw new Error(`Retry attempt not found: ${retryAttemptId}`);
    }

    try {
      // Check circuit breaker
      if (this.circuitBreaker.isOpen) {
        return {
          connectionId: retryAttempt.subscriptionId,
          success: false,
          error: 'Circuit breaker is open',
          deliveryTime: 0,
          circuitBreakerOpen: true,
        };
      }

      // Wait for retry delay
      await this.sleep(retryAttempt.delay);

      // Attempt delivery
      const startTime = Date.now();

      // This would call the actual delivery mechanism
      const success = await this.attemptDelivery(retryAttempt);
      const deliveryTime = Date.now() - startTime;

      if (success) {
        // Success - remove retry attempt
        this.retryAttempts.delete(retryAttemptId);

        // Update metrics
        this.updateMetrics('retry_succeeded', {
          retryAttemptId,
          totalAttempts: retryAttempt.attempt,
          deliveryTime,
        });

        // Reset circuit breaker on success
        this.resetCircuitBreaker();

        return {
          connectionId: retryAttempt.subscriptionId,
          success: true,
          deliveryTime,
        };
      } else {
        // Failed - check if we should retry again
        const newAttempt = retryAttempt.attempt + 1;

        if (newAttempt <= retryAttempt.maxAttempts) {
          // Update retry attempt
          retryAttempt.attempt = newAttempt;
          retryAttempt.delay = this.calculateRetryDelay(newAttempt);
          retryAttempt.timestamp = new Date().toISOString();

          // Schedule next retry
          await this.scheduleRetry(retryAttempt);

          return {
            connectionId: retryAttempt.subscriptionId,
            success: false,
            error: 'Delivery failed, retry scheduled',
            deliveryTime,
            retryScheduled: true,
            retryAttemptId,
          };
        } else {
          // Max retries reached - add to dead letter queue
          await this.addToDeadLetterQueue(
            retryAttempt.subscriptionId,
            retryAttempt.event,
            new Error('Max retries exceeded'),
            retryAttempt.attempt
          );

          // Update metrics
          this.updateMetrics('max_retries_reached', {
            retryAttemptId,
            totalAttempts: retryAttempt.attempt,
          });

          // Check circuit breaker
          this.checkCircuitBreaker();

          return {
            connectionId: retryAttempt.subscriptionId,
            success: false,
            error: 'Max retries reached',
            deliveryTime,
            addedToDeadLetterQueue: true,
          };
        }
      }
    } catch (error) {
      this.updateMetrics('retry_error', {
        retryAttemptId,
        error: error.message,
      });

      return {
        connectionId: retryAttempt.subscriptionId,
        success: false,
        error: `Retry error: ${error.message}`,
        deliveryTime,
      };
    }
  }

  // Dead letter queue management
  async addToDeadLetterQueue(
    subscriptionId: string,
    event: UpdateEvent,
    error: Error,
    attempt: number
  ): Promise<void> {
    const deadLetterMessage: DeadLetterMessage = {
      id: this.generateDeadLetterId(),
      subscriptionId,
      event,
      error,
      attempts: attempt,
      finalError: error,
      timestamp: new Date().toISOString(),
      reason: this.determineDeadLetterReason(error, attempt),
    };

    this.deadLetterQueue.push(deadLetterMessage);

    // Trim queue if it exceeds max size
    if (this.deadLetterQueue.length > this.config.deadLetterQueueMaxSize) {
      const removed = this.deadLetterQueue.splice(
        0,
        this.deadLetterQueue.length - this.config.deadLetterQueueMaxSize
      );

      this.updateMetrics('dead_letter_queue_trimmed', { removedCount: removed.length });
    }

    // Update metrics
    this.updateMetrics('dead_letter_added', {
      subscriptionId,
      errorType: error.name,
      queueSize: this.deadLetterQueue.length,
    });

    this.emit('deadLetterAdded', deadLetterMessage);
  }

  getDeadLetterQueue(limit?: number, offset?: number): DeadLetterMessage[] {
    let queue = [...this.deadLetterQueue];

    // Apply offset
    if (offset) {
      queue = queue.slice(offset);
    }

    // Apply limit
    if (limit) {
      queue = queue.slice(0, limit);
    }

    // Return in reverse order (newest first)
    return queue.reverse();
  }

  async retryDeadLetter(messageId: string): Promise<boolean> {
    const messageIndex = this.deadLetterQueue.findIndex((msg) => msg.id === messageId);
    if (messageIndex === -1) {
      return false;
    }

    const message = this.deadLetterQueue[messageIndex];

    // Remove from dead letter queue
    this.deadLetterQueue.splice(messageIndex, 1);

    try {
      // Reset circuit breaker for retry
      this.resetCircuitBreaker();

      // Attempt delivery
      const success = await this.attemptDelivery({
        id: this.generateRetryAttemptId(),
        subscriptionId: message.subscriptionId,
        event: message.event,
        attempt: 1,
        maxAttempts: this.config.maxRetries,
        delay: this.config.baseDelay,
        error: message.finalError,
        timestamp: new Date().toISOString(),
      });

      if (success) {
        this.updateMetrics('dead_letter_retry_succeeded', { messageId });
        this.emit('deadLetterRetrySucceeded', message);
        return true;
      } else {
        // Failed again - add back to dead letter queue
        await this.addToDeadLetterQueue(
          message.subscriptionId,
          message.event,
          new Error('Dead letter retry failed'),
          1
        );

        this.updateMetrics('dead_letter_retry_failed', { messageId });
        this.emit('deadLetterRetryFailed', message);
        return false;
      }
    } catch (error) {
      this.updateMetrics('dead_letter_retry_error', { messageId, error: error.message });
      this.emit('deadLetterRetryError', { message, error });
      return false;
    }
  }

  // Circuit breaker management
  private checkCircuitBreaker(): void {
    this.circuitBreaker.failureCount++;
    this.circuitBreaker.lastFailureTime = Date.now();

    if (this.circuitBreaker.failureCount >= this.config.circuitBreakerThreshold) {
      this.openCircuitBreaker();
    }
  }

  private openCircuitBreaker(): void {
    this.circuitBreaker.isOpen = true;
    this.circuitBreaker.state = 'open';
    this.circuitBreaker.nextAttemptTime = Date.now() + this.config.circuitBreakerTimeout;

    this.updateMetrics('circuit_breaker_opened');
    this.emit('circuitBreakerOpened', this.circuitBreaker);
  }

  private resetCircuitBreaker(): void {
    this.circuitBreaker.isOpen = false;
    this.circuitBreaker.state = 'closed';
    this.circuitBreaker.failureCount = 0;
    this.circuitBreaker.lastFailureTime = 0;
    this.circuitBreaker.nextAttemptTime = 0;

    this.updateMetrics('circuit_breaker_reset');
    this.emit('circuitBreakerReset', this.circuitBreaker);
  }

  private closeCircuitBreaker(): void {
    if (this.circuitBreaker.state === 'open') {
      this.circuitBreaker.state = 'half-open';
      this.circuitBreaker.nextAttemptTime = Date.now() + this.config.circuitBreakerTimeout / 2;

      this.updateMetrics('circuit_breaker_half_opened');
      this.emit('circuitBreakerHalfOpened', this.circuitBreaker);
    }
  }

  // Metrics and statistics
  getMetrics(): ErrorHandlerMetrics {
    return { ...this.metrics };
  }

  getRetryAttempts(subscriptionId?: string): RetryAttempt[] {
    let attempts = Array.from(this.retryAttempts.values());

    if (subscriptionId) {
      attempts = attempts.filter((attempt) => attempt.subscriptionId === subscriptionId);
    }

    return attempts.sort(
      (a, b) => new Date(b.timestamp).getTime() - new Date(a.timestamp).getTime()
    );
  }

  getCircuitBreakerState(): CircuitBreakerState {
    return { ...this.circuitBreaker };
  }

  // Private helper methods
  private mergeWithDefaults(config: Partial<ErrorHandlerConfig>): ErrorHandlerConfig {
    return {
      maxRetries: 3,
      baseDelay: 1000,
      maxDelay: 30000,
      backoffMultiplier: 2,
      jitter: true,
      deadLetterQueueMaxSize: 1000,
      circuitBreakerThreshold: 5,
      circuitBreakerTimeout: 60000, // 1 minute
      enableMetrics: true,
      ...config,
    };
  }

  private initializeCircuitBreaker(): CircuitBreakerState {
    return {
      isOpen: false,
      failureCount: 0,
      lastFailureTime: 0,
      nextAttemptTime: 0,
      state: 'closed',
    };
  }

  private initializeMetrics(): ErrorHandlerMetrics {
    return {
      totalRetries: 0,
      successfulRetries: 0,
      failedRetries: 0,
      deadLetterMessages: 0,
      circuitBreakerOpenings: 0,
      averageRetryDelay: 0,
      errorTypes: {},
      lastReset: new Date().toISOString(),
    };
  }

  private shouldRetry(error: Error, attempt: number): boolean {
    // Don't retry certain error types
    const nonRetryableErrors = [
      'ValidationError',
      'AuthenticationError',
      'AuthorizationError',
      'ForbiddenError',
    ];

    if (nonRetryableErrors.some((errorType) => error.name === errorType)) {
      return false;
    }

    // Don't retry if we've reached max attempts
    if (attempt >= this.config.maxRetries) {
      return false;
    }

    // Don't retry certain error messages
    const nonRetryableMessages = [
      'Subscription not found',
      'Invalid subscription',
      'Rate limit exceeded',
    ];

    if (nonRetryableMessages.some((message) => error.message.includes(message))) {
      return false;
    }

    return true;
  }

  private calculateRetryDelay(attempt: number): number {
    let delay = this.config.baseDelay * Math.pow(this.config.backoffMultiplier, attempt - 1);
    delay = Math.min(delay, this.config.maxDelay);

    // Add jitter if enabled
    if (this.config.jitter) {
      const jitter = Math.random() * 0.1 * delay;
      delay += jitter;
    }

    return Math.floor(delay);
  }

  private determineDeadLetterReason(error: Error, attempt: number): string {
    if (attempt >= this.config.maxRetries) {
      return 'max_retries_exceeded';
    }

    if (error.name === 'ValidationError') {
      return 'validation_error';
    }

    if (error.name === 'AuthenticationError') {
      return 'authentication_error';
    }

    if (error.message.includes('timeout')) {
      return 'timeout';
    }

    return 'unknown_error';
  }

  private async scheduleRetry(retryAttempt: RetryAttempt): Promise<void> {
    setTimeout(async () => {
      try {
        await this.retryDelivery(retryAttempt.id);
      } catch (error) {
        this.emit('retrySchedulingError', { retryAttemptId: retryAttempt.id, error });
      }
    }, retryAttempt.delay);
  }

  private async attemptDelivery(retryAttempt: RetryAttempt): Promise<boolean> {
    // This would call the actual delivery mechanism
    // For now, simulate with some probability of success
    const success = Math.random() > 0.7; // 70% success rate

    if (success) {
      this.emit('deliveryAttempted', { retryAttemptId: retryAttempt.id, success: true });
    } else {
      this.emit('deliveryAttempted', {
        retryAttemptId: retryAttempt.id,
        success: false,
        error: new Error('Simulated delivery failure'),
      });
    }

    return success;
  }

  private generateRetryAttemptId(): string {
    return `retry_${Date.now()}_${Math.random().toString(36).substr(2, 9)}`;
  }

  private generateDeadLetterId(): string {
    return `dlq_${Date.now()}_${Math.random().toString(36).substr(2, 9)}`;
  }

  private sleep(ms: number): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, ms));
  }

  private updateMetrics(operation: string, data?: any): void {
    if (!this.config.enableMetrics) return;

    switch (operation) {
      case 'retry_attempted':
        this.metrics.totalRetries++;
        break;
      case 'retry_succeeded':
        this.metrics.successfulRetries++;
        break;
      case 'retry_failed':
        this.metrics.failedRetries++;
        break;
      case 'dead_letter_added':
        this.metrics.deadLetterMessages++;
        if (data?.errorType) {
          this.metrics.errorTypes[data.errorType] =
            (this.metrics.errorTypes[data.errorType] || 0) + 1;
        }
        break;
      case 'circuit_breaker_opened':
        this.metrics.circuitBreakerOpenings++;
        break;
      case 'circuit_breaker_reset':
        this.metrics.lastReset = new Date().toISOString();
        break;
    }

    this.metrics.averageRetryDelay = this.calculateAverageRetryDelay();
  }

  private calculateAverageRetryDelay(): number {
    // This would calculate the average retry delay from all retry attempts
    // For now, return a placeholder
    return this.config.baseDelay * 1.5; // Placeholder
  }

  // Cleanup
  async dispose(): Promise<void> {
    // Clear all data structures
    this.retryAttempts.clear();
    this.deadLetterQueue = [];

    // Clear any pending retries
    for (const retryAttempt of this.retryAttempts.values()) {
      if (retryAttempt.timeoutId) {
        clearTimeout(retryAttempt.timeoutId);
      }
    }

    this.removeAllListeners();
  }
}

// Supporting interfaces
export interface ErrorHandlerMetrics {
  totalRetries: number;
  successfulRetries: number;
  failedRetries: number;
  deadLetterMessages: number;
  circuitBreakerOpenings: number;
  averageRetryDelay: number;
  errorTypes: Record<string, number>;
  lastReset: string;
}
```

## Files to Create

1. **Subscription Management**:
   - `packages/providers/src/notifications/subscription-manager.ts`

2. **Notification Broadcasting**:
   - `packages/providers/src/notifications/notification-broadcaster.ts`

3. **Filtering and Batching**:
   - `packages/providers/src/notifications/event-filter-batcher.ts`

4. **Error Handling and Retry**:
   - `packages/providers/src/notifications/error-handler-retry.ts`

5. **Updated Files**:
   - `packages/providers/src/index.ts` (export new classes)

## Testing Requirements

### Unit Tests

```typescript
// packages/providers/src/__tests__/notifications/subscription-manager.test.ts
describe('SubscriptionManager', () => {
  let manager: SubscriptionManager;

  beforeEach(() => {
    manager = new SubscriptionManager({
      maxSubscriptions: 100,
      maxSubscriptionsPerClient: 10,
      persistenceEnabled: false,
    });
  });

  describe('subscription management', () => {
    it('should create subscription successfully', async () => {
      const subscription = {
        clientId: 'test-client',
        type: 'model_updates',
        callback: jest.fn(),
        filters: [],
      };

      const unsubscribe = await manager.subscribe(subscription);

      expect(unsubscribe).toBeInstanceOf(Function);

      const subscriptions = await manager.getSubscriptions();
      expect(subscriptions).toHaveLength(1);
      expect(subscriptions[0].clientId).toBe('test-client');
    });

    it('should enforce subscription limits', async () => {
      const subscription = {
        clientId: 'test-client',
        type: 'model_updates',
        callback: jest.fn(),
        filters: [],
      };

      // Create max subscriptions for client
      for (let i = 0; i < 10; i++) {
        await manager.subscribe({
          ...subscription,
          subscriptionId: `sub-${i}`,
        });
      }

      // Next one should fail
      await expect(manager.subscribe(subscription)).rejects.toThrow(
        'Maximum subscription limit per client reached'
      );
    });
  });
});
```

### Integration Tests

```typescript
// packages/providers/src/__tests__/integrations/notification-system-integration.test.ts
describe('Notification System Integration', () => {
  let subscriptionManager: SubscriptionManager;
  let broadcaster: NotificationBroadcaster;

  beforeEach(async () => {
    subscriptionManager = new SubscriptionManager();
    broadcaster = new NotificationBroadcaster({
      enableWebSocket: false, // Disable for testing
    });
  });

  it('should deliver events to subscribers', async () => {
    const receivedEvents: any[] = [];

    const subscription = await subscriptionManager.subscribe({
      clientId: 'test-client',
      type: 'model_updates',
      callback: (event) => {
        receivedEvents.push(event);
      },
      filters: [],
    });

    const event = {
      type: 'model_update',
      modelId: 'test-model',
      providerName: 'test-provider',
      timestamp: new Date().toISOString(),
    } as any;

    await subscriptionManager.publishModelUpdate(event);

    // Wait for async processing
    await new Promise((resolve) => setTimeout(resolve, 100));

    expect(receivedEvents).toHaveLength(1);
    expect(receivedEvents[0]).toEqual(event);
  });
});
```

## Security Considerations

1. **Subscription Security**:
   - Validate subscription requests to prevent injection attacks
   - Implement rate limiting for subscription creation
   - Authenticate WebSocket connections if enabled
   - Sanitize event data before delivery

2. **Broadcasting Security**:
   - Implement connection limits to prevent DoS attacks
   - Validate message formats and sizes
   - Use secure WebSocket protocols (WSS)
   - Implement proper CORS headers

3. **Data Privacy**:
   - Sanitize event data before logging
   - Implement data retention policies for dead letter queue
   - Encrypt sensitive event information if needed

## Dependencies

### New Dependencies

```json
{
  "ws": "^8.14.2",
  "uuid": "^9.0.1"
}
```

### Dev Dependencies

```json
{
  "@types/ws": "^8.5.5",
  "@types/uuid": "^9.0.7"
}
```

## Monitoring and Observability

1. **Metrics to Track**:
   - Subscription creation/removal rates
   - Event delivery success/failure rates
   - Connection counts and duration
   - Queue sizes and processing times
   - Circuit breaker state changes

2. **Logging**:
   - All subscription operations with client context
   - Event delivery attempts with detailed error information
   - Dead letter queue operations
   - Circuit breaker state transitions

3. **Alerts**:
   - High subscription failure rates
   - Circuit breaker activations
   - Dead letter queue overflow
   - Connection limit breaches
   - Authentication failures

## Acceptance Criteria

1. ✅ **Subscription Management**: Complete subscription lifecycle management
2. ✅ **Real-time Broadcasting**: High-performance event broadcasting
3. ✅ **Filtering and Batching**: Intelligent event filtering and batching
4. ✅ **Error Handling**: Robust error handling with retry logic
5. ✅ **Dead Letter Queue**: Failed event handling with retry capability
6. ✅ **Circuit Breaker**: Protection against cascading failures
7. ✅ **Performance**: Optimized for high-volume scenarios
8. ✅ **Testing**: Comprehensive test coverage
9. ✅ **Security**: Secure handling of all operations
10. ✅ **Monitoring**: Complete observability and alerting

## Success Metrics

- Subscription creation success rate > 99%
- Event delivery success rate > 95%
- Average event delivery latency < 100ms
- Dead letter queue processing rate > 90%
- Circuit breaker false positive rate < 1%
- Zero security incidents in notification handling
- Complete audit trail for all operations
