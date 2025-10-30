# Story 5.5: Event Trail Exploration UI

**Epic**: Epic 5 - Observability & Production Readiness  
**Status**: Ready for Dev  
**MVP Priority**: Optional (Post-MVP Enhancement)  
**Estimated Complexity**: High  
**Target Implementation**: Sprint 6 (Post-MVP)

## Acceptance Criteria

### Event Trail Interface

- [ ] **Comprehensive event exploration interface** for browsing system events and audit trails
- [ ] **Advanced filtering and search** capabilities across all event attributes
- [ ] **Real-time event streaming** showing live system activity
- [ ] **Timeline visualization** with interactive event sequences
- [ ] **Event detail views** with complete context and metadata

### Search and Discovery

- [ ] **Full-text search** across event types, messages, and metadata
- [ ] **Faceted search** with filters for event type, time range, tags, and attributes
- [ ] **Saved searches** for common investigation patterns
- [ ] **Search syntax support** for complex queries (AND, OR, NOT, wildcards)
- [ ] **Recent searches** history for quick access

### Event Visualization

- [ ] **Interactive timeline view** with zoom and pan capabilities
- [ ] **Event grouping** by issue, workflow, user, or custom attributes
- [ ] **Color-coded events** by type, severity, and status
- [ ] **Event density heat maps** showing activity patterns
- [ ] **Workflow visualization** with step-by-step event sequences

### Investigation Tools

- [ ] **Event correlation** linking related events across workflows
- [ ] **Root cause analysis** tools with event chain tracing
- [ ] **Performance impact analysis** showing event timing relationships
- [ ] **Anomaly detection** highlighting unusual event patterns
- [ ] **Export functionality** for event data and analysis results

### Audit Trail Features

- [ ] **Complete audit trail** viewing with immutable event history
- [ ] **Compliance reporting** for regulatory requirements
- [ ] **Event integrity verification** with cryptographic signatures
- [ ] **Access logging** for who viewed which events
- [ ] **Data retention policies** with automated archival

## Technical Context

### Current System State

From `docs/stories/4-1-event-schema-design.md`:

- Comprehensive event schema with DCB (Dynamic Consistency Boundary) pattern
- Event types: ISSUE._, WORKFLOW._, AI._, CODE._, QUALITY._, SYSTEM._
- Rich metadata with tags, timestamps, and structured data
- Event immutability and audit trail capabilities

From `docs/stories/4-7-event-query-api-for-time-travel.md`:

- RESTful event query API with flexible filtering
- Event projection and aggregation capabilities
- Time-travel functionality for historical state reconstruction
- Performance-optimized event querying with indexing

From `docs/stories/5-3-real-time-dashboard-system-health.md`:

- Dashboard framework with real-time WebSocket updates
- Authentication and authorization system
- Widget-based architecture for extensible interfaces

### Event Trail Architecture

```typescript
// Event trail exploration architecture
┌─────────────────┐    ┌──────────────────┐    ┌─────────────────┐
│   Event Trail   │    │  Event Query     │    │   Event Store   │
│      UI         │◄──►│      Service     │◄──►│  (PostgreSQL)   │
│   (React SPA)   │    │                  │    │                 │
│                 │    │ - Search Engine  │    │ - Events Table  │
│ - Search UI     │    │ - Filtering      │    │ - Indexes       │
│ - Timeline      │    │ - Aggregation    │    │ - Partitions    │
│ - Details       │    │ - Real-time      │    │ - Audit Log     │
│ - Export        │    │ - Correlation    │    │                 │
└─────────────────┘    └──────────────────┘    └─────────────────┘
         │                       │                       │
         │              ┌──────────────────┐              │
         └──────────────►│  Search Index    │◄─────────────┘
                        │  (Elasticsearch)  │
                        │                  │
                        │ - Full-text      │
                        │ - Faceted search │
                        │ - Aggregations   │
                        │ - Real-time      │
                        └──────────────────┘
```

## Technical Implementation

### 1. Event Trail Data Models

#### Event Search and Display Types

```typescript
// packages/event-trail/src/types/event-trail.types.ts
export interface EventSearchRequest {
  query?: string;
  filters: EventFilters;
  timeRange: TimeRange;
  sort: EventSort[];
  limit: number;
  offset: number;
  includeRelated?: boolean;
}

export interface EventFilters {
  eventTypes?: string[];
  tags?: Record<string, string>;
  metadata?: Record<string, any>;
  severity?: EventSeverity[];
  source?: string[];
  userId?: string[];
  issueId?: string[];
  workflowId?: string[];
  customAttributes?: Record<string, FilterValue>;
}

export interface FilterValue {
  operator: 'eq' | 'ne' | 'gt' | 'gte' | 'lt' | 'lte' | 'in' | 'contains' | 'regex';
  value: any;
}

export interface TimeRange {
  start: Date;
  end: Date;
  relative?: string; // "last-1h", "last-24h", "last-7d"
}

export interface EventSort {
  field: string;
  direction: 'asc' | 'desc';
}

export interface EventSearchResult {
  events: Event[];
  total: number;
  aggregations: EventAggregations;
  suggestions?: string[];
  took: number; // milliseconds
}

export interface Event {
  id: string;
  type: string;
  timestamp: string;
  tags: Record<string, string>;
  metadata: {
    workflowVersion: string;
    eventSource: string;
    [key: string]: any;
  };
  data: Record<string, any>;
  severity: EventSeverity;
  source: string;
  relatedEvents?: RelatedEvent[];
  context?: EventContext;
}

export type EventSeverity = 'debug' | 'info' | 'warn' | 'error' | 'critical';

export interface RelatedEvent {
  id: string;
  type: string;
  relationship: 'causes' | 'caused_by' | 'correlates' | 'contains' | 'part_of';
  timestamp: string;
}

export interface EventContext {
  issueId?: string;
  workflowId?: string;
  userId?: string;
  sessionId?: string;
  requestId?: string;
  traceId?: string;
}

export interface EventAggregations {
  byType: Record<string, number>;
  bySeverity: Record<EventSeverity, number>;
  bySource: Record<string, number>;
  byTime: TimeBucket[];
  byUser: Record<string, number>;
  byIssue: Record<string, number>;
}

export interface TimeBucket {
  timestamp: string;
  count: number;
}

export interface EventTimeline {
  events: TimelineEvent[];
  groups: TimelineGroup[];
  metadata: TimelineMetadata;
}

export interface TimelineEvent extends Event {
  x: number; // timeline position
  y: number; // lane position
  group: string;
  duration?: number; // for events with duration
}

export interface TimelineGroup {
  id: string;
  name: string;
  events: TimelineEvent[];
  color?: string;
}

export interface TimelineMetadata {
  startTime: string;
  endTime: string;
  totalEvents: number;
  groups: number;
  zoom: number;
}

export interface EventCorrelation {
  rootEvent: Event;
  relatedEvents: Event[];
  correlationType: 'workflow' | 'issue' | 'user' | 'session' | 'performance';
  confidence: number;
  analysis: CorrelationAnalysis;
}

export interface CorrelationAnalysis {
  patterns: string[];
  anomalies: Anomaly[];
  insights: string[];
  recommendations: string[];
}

export interface Anomaly {
  type: 'timing' | 'frequency' | 'sequence' | 'content';
  severity: 'low' | 'medium' | 'high' | 'critical';
  description: string;
  affectedEvents: string[];
}
```

### 2. Event Trail Service Implementation

#### Event Query and Search Service

```typescript
// packages/event-trail/src/services/event-trail.service.ts
import { EventStore } from '@tamma/events';
import { Logger } from 'pino';
import { SearchClient } from './search.client';

export class EventTrailService {
  constructor(
    private eventStore: EventStore,
    private searchClient: SearchClient,
    private logger: Logger
  ) {}

  async searchEvents(request: EventSearchRequest): Promise<EventSearchResult> {
    const startTime = Date.now();

    try {
      // Build search query
      const searchQuery = this.buildSearchQuery(request);

      // Execute search
      const searchResult = await this.searchClient.search(searchQuery);

      // Fetch full event details
      const events = await this.eventStore.getEventsByIds(searchResult.hits.map((hit) => hit.id));

      // Add related events if requested
      if (request.includeRelated) {
        await this.addRelatedEvents(events);
      }

      // Calculate aggregations
      const aggregations = await this.calculateAggregations(request);

      // Generate suggestions if query is empty or has low results
      const suggestions =
        searchResult.hits.length < 5 ? await this.generateSuggestions(request) : undefined;

      return {
        events,
        total: searchResult.total,
        aggregations,
        suggestions,
        took: Date.now() - startTime,
      };
    } catch (error) {
      this.logger.error('Event search failed', { error, request });
      throw error;
    }
  }

  async getEventTimeline(request: EventSearchRequest): Promise<EventTimeline> {
    const events = await this.searchEvents(request);

    // Group events by context
    const groups = this.groupEventsForTimeline(events.events);

    // Calculate timeline positions
    const timelineEvents = this.calculateTimelinePositions(events.events, groups);

    // Generate timeline metadata
    const metadata = this.generateTimelineMetadata(timelineEvents, groups);

    return {
      events: timelineEvents,
      groups,
      metadata,
    };
  }

  async correlateEvents(eventId: string): Promise<EventCorrelation[]> {
    const rootEvent = await this.eventStore.getEvent(eventId);
    if (!rootEvent) {
      throw new Error(`Event not found: ${eventId}`);
    }

    const correlations: EventCorrelation[] = [];

    // Workflow correlation
    if (rootEvent.context?.workflowId) {
      const workflowCorrelation = await this.correlateByWorkflow(rootEvent);
      correlations.push(workflowCorrelation);
    }

    // Issue correlation
    if (rootEvent.context?.issueId) {
      const issueCorrelation = await this.correlateByIssue(rootEvent);
      correlations.push(issueCorrelation);
    }

    // User correlation
    if (rootEvent.context?.userId) {
      const userCorrelation = await this.correlateByUser(rootEvent);
      correlations.push(userCorrelation);
    }

    // Performance correlation
    const performanceCorrelation = await this.correlateByPerformance(rootEvent);
    correlations.push(performanceCorrelation);

    return correlations;
  }

  async detectAnomalies(request: EventSearchRequest): Promise<Anomaly[]> {
    const events = await this.searchEvents(request);

    const anomalies: Anomaly[] = [];

    // Timing anomalies
    const timingAnomalies = await this.detectTimingAnomalies(events.events);
    anomalies.push(...timingAnomalies);

    // Frequency anomalies
    const frequencyAnomalies = await this.detectFrequencyAnomalies(events.events);
    anomalies.push(...frequencyAnomalies);

    // Sequence anomalies
    const sequenceAnomalies = await this.detectSequenceAnomalies(events.events);
    anomalies.push(...sequenceAnomalies);

    // Content anomalies
    const contentAnomalies = await this.detectContentAnomalies(events.events);
    anomalies.push(...contentAnomalies);

    return anomalies.sort((a, b) => {
      const severityOrder = { critical: 4, high: 3, medium: 2, low: 1 };
      return severityOrder[b.severity] - severityOrder[a.severity];
    });
  }

  async exportEvents(
    request: EventSearchRequest,
    format: 'json' | 'csv' | 'xlsx'
  ): Promise<Buffer> {
    const events = await this.searchEvents({
      ...request,
      limit: 10000, // Export limit
    });

    switch (format) {
      case 'json':
        return this.exportAsJSON(events.events);
      case 'csv':
        return this.exportAsCSV(events.events);
      case 'xlsx':
        return this.exportAsXLSX(events.events);
      default:
        throw new Error(`Unsupported export format: ${format}`);
    }
  }

  private buildSearchQuery(request: EventSearchRequest): any {
    const query: any = {
      bool: {
        must: [],
        filter: [],
        should: [],
        must_not: [],
      },
    };

    // Time range filter
    if (request.timeRange) {
      query.bool.filter.push({
        range: {
          timestamp: {
            gte: request.timeRange.start.toISOString(),
            lte: request.timeRange.end.toISOString(),
          },
        },
      });
    }

    // Text query
    if (request.query) {
      query.bool.must.push({
        multi_match: {
          query: request.query,
          fields: ['type^3', 'data.message^2', 'data.description', 'tags.*'],
          fuzziness: 'AUTO',
        },
      });
    }

    // Event type filter
    if (request.filters.eventTypes?.length) {
      query.bool.filter.push({
        terms: { type: request.filters.eventTypes },
      });
    }

    // Severity filter
    if (request.filters.severity?.length) {
      query.bool.filter.push({
        terms: { severity: request.filters.severity },
      });
    }

    // Tag filters
    if (request.filters.tags) {
      Object.entries(request.filters.tags).forEach(([key, value]) => {
        query.bool.filter.push({
          term: { [`tags.${key}`]: value },
        });
      });
    }

    // Custom attribute filters
    if (request.filters.customAttributes) {
      Object.entries(request.filters.customAttributes).forEach(([key, filter]) => {
        const field = `data.${key}`;
        const filterClause: any = { [field]: {} };

        switch (filter.operator) {
          case 'eq':
            filterClause[field][field] = filter.value;
            break;
          case 'ne':
            query.bool.must_not.push({ term: { [field]: filter.value } });
            return;
          case 'gt':
            filterClause[field][field] = { gt: filter.value };
            break;
          case 'gte':
            filterClause[field][field] = { gte: filter.value };
            break;
          case 'lt':
            filterClause[field][field] = { lt: filter.value };
            break;
          case 'lte':
            filterClause[field][field] = { lte: filter.value };
            break;
          case 'in':
            filterClause[field][field] = filter.value;
            break;
          case 'contains':
            filterClause[field][field] = { wildcard: `*${filter.value}*` };
            break;
          case 'regex':
            filterClause[field][field] = { regexp: filter.value };
            break;
        }

        query.bool.filter.push(filterClause);
      });
    }

    // Sort
    if (request.sort?.length) {
      query.sort = request.sort.map((sort) => ({
        [sort.field]: { order: sort.direction },
      }));
    } else {
      query.sort = [{ timestamp: { order: 'desc' } }];
    }

    return {
      query,
      size: request.limit || 100,
      from: request.offset || 0,
      aggs: this.buildAggregations(),
    };
  }

  private buildAggregations(): any {
    return {
      event_types: {
        terms: { field: 'type', size: 50 },
      },
      severity: {
        terms: { field: 'severity', size: 10 },
      },
      source: {
        terms: { field: 'source', size: 20 },
      },
      timeline: {
        date_histogram: {
          field: 'timestamp',
          calendar_interval: '1h',
        },
      },
      users: {
        terms: { field: 'context.userId', size: 20 },
      },
      issues: {
        terms: { field: 'context.issueId', size: 50 },
      },
    };
  }

  private async calculateAggregations(request: EventSearchRequest): Promise<EventAggregations> {
    const searchQuery = this.buildSearchQuery({ ...request, limit: 0 });
    const result = await this.searchClient.search(searchQuery);

    return {
      byType: Object.fromEntries(
        result.aggregations.event_types.buckets.map((bucket: any) => [bucket.key, bucket.doc_count])
      ),
      bySeverity: Object.fromEntries(
        result.aggregations.severity.buckets.map((bucket: any) => [bucket.key, bucket.doc_count])
      ),
      bySource: Object.fromEntries(
        result.aggregations.source.buckets.map((bucket: any) => [bucket.key, bucket.doc_count])
      ),
      byTime: result.aggregations.timeline.buckets.map((bucket: any) => ({
        timestamp: bucket.key_as_string,
        count: bucket.doc_count,
      })),
      byUser: Object.fromEntries(
        result.aggregations.users.buckets.map((bucket: any) => [bucket.key, bucket.doc_count])
      ),
      byIssue: Object.fromEntries(
        result.aggregations.issues.buckets.map((bucket: any) => [bucket.key, bucket.doc_count])
      ),
    };
  }

  private async addRelatedEvents(events: Event[]): Promise<void> {
    for (const event of events) {
      const relatedEvents = await this.findRelatedEvents(event);
      event.relatedEvents = relatedEvents;
    }
  }

  private async findRelatedEvents(event: Event): Promise<RelatedEvent[]> {
    const relatedEvents: RelatedEvent[] = [];

    // Find events in same workflow
    if (event.context?.workflowId) {
      const workflowEvents = await this.eventStore.queryEvents({
        filters: {
          'context.workflowId': event.context.workflowId,
          id: { ne: event.id },
        },
        limit: 10,
      });

      relatedEvents.push(
        ...workflowEvents.events.map((e) => ({
          id: e.id,
          type: e.type,
          relationship: 'part_of' as const,
          timestamp: e.timestamp,
        }))
      );
    }

    // Find events for same issue
    if (event.context?.issueId) {
      const issueEvents = await this.eventStore.queryEvents({
        filters: {
          'context.issueId': event.context.issueId,
          id: { ne: event.id },
        },
        limit: 10,
      });

      relatedEvents.push(
        ...issueEvents.events.map((e) => ({
          id: e.id,
          type: e.type,
          relationship: 'correlates' as const,
          timestamp: e.timestamp,
        }))
      );
    }

    return relatedEvents;
  }

  private groupEventsForTimeline(events: Event[]): TimelineGroup[] {
    const groups = new Map<string, TimelineGroup>();

    events.forEach((event) => {
      let groupKey = 'general';
      let groupName = 'General';

      if (event.context?.workflowId) {
        groupKey = `workflow:${event.context.workflowId}`;
        groupName = `Workflow ${event.context.workflowId}`;
      } else if (event.context?.issueId) {
        groupKey = `issue:${event.context.issueId}`;
        groupName = `Issue ${event.context.issueId}`;
      } else if (event.context?.userId) {
        groupKey = `user:${event.context.userId}`;
        groupName = `User ${event.context.userId}`;
      }

      if (!groups.has(groupKey)) {
        groups.set(groupKey, {
          id: groupKey,
          name: groupName,
          events: [],
          color: this.getGroupColor(groupKey),
        });
      }

      groups.get(groupKey)!.events.push(event as TimelineEvent);
    });

    return Array.from(groups.values());
  }

  private calculateTimelinePositions(events: Event[], groups: TimelineGroup[]): TimelineEvent[] {
    const timelineEvents: TimelineEvent[] = [];
    const startTime = Math.min(...events.map((e) => new Date(e.timestamp).getTime()));
    const endTime = Math.max(...events.map((e) => new Date(e.timestamp).getTime()));
    const timeRange = endTime - startTime;

    groups.forEach((group, groupIndex) => {
      group.events.forEach((event, eventIndex) => {
        const eventTime = new Date(event.timestamp).getTime();
        const x = ((eventTime - startTime) / timeRange) * 100; // percentage
        const y = groupIndex * 50 + (eventIndex % 3) * 15; // lane position

        timelineEvents.push({
          ...event,
          x,
          y,
          group: group.id,
        });
      });
    });

    return timelineEvents;
  }

  private generateTimelineMetadata(
    events: TimelineEvent[],
    groups: TimelineGroup[]
  ): TimelineMetadata {
    const timestamps = events.map((e) => new Date(e.timestamp).getTime());
    const startTime = new Date(Math.min(...timestamps)).toISOString();
    const endTime = new Date(Math.max(...timestamps)).toISOString();

    return {
      startTime,
      endTime,
      totalEvents: events.length,
      groups: groups.length,
      zoom: 1,
    };
  }

  private async correlateByWorkflow(rootEvent: Event): Promise<EventCorrelation> {
    const workflowEvents = await this.eventStore.queryEvents({
      filters: {
        'context.workflowId': rootEvent.context?.workflowId,
      },
      orderBy: 'timestamp',
      order: 'asc',
    });

    const analysis = await this.analyzeWorkflowCorrelation(rootEvent, workflowEvents.events);

    return {
      rootEvent,
      relatedEvents: workflowEvents.events.filter((e) => e.id !== rootEvent.id),
      correlationType: 'workflow',
      confidence: analysis.confidence,
      analysis,
    };
  }

  private async correlateByIssue(rootEvent: Event): Promise<EventCorrelation> {
    const issueEvents = await this.eventStore.queryEvents({
      filters: {
        'context.issueId': rootEvent.context?.issueId,
      },
      orderBy: 'timestamp',
      order: 'asc',
    });

    const analysis = await this.analyzeIssueCorrelation(rootEvent, issueEvents.events);

    return {
      rootEvent,
      relatedEvents: issueEvents.events.filter((e) => e.id !== rootEvent.id),
      correlationType: 'issue',
      confidence: analysis.confidence,
      analysis,
    };
  }

  private async correlateByUser(rootEvent: Event): Promise<EventCorrelation> {
    const userEvents = await this.eventStore.queryEvents({
      filters: {
        'context.userId': rootEvent.context?.userId,
      },
      orderBy: 'timestamp',
      order: 'asc',
    });

    const analysis = await this.analyzeUserCorrelation(rootEvent, userEvents.events);

    return {
      rootEvent,
      relatedEvents: userEvents.events.filter((e) => e.id !== rootEvent.id),
      correlationType: 'user',
      confidence: analysis.confidence,
      analysis,
    };
  }

  private async correlateByPerformance(rootEvent: Event): Promise<EventCorrelation> {
    // Find performance-related events within a time window
    const timeWindow = 5 * 60 * 1000; // 5 minutes
    const eventTime = new Date(rootEvent.timestamp).getTime();

    const performanceEvents = await this.eventStore.queryEvents({
      filters: {
        timestamp: {
          gte: new Date(eventTime - timeWindow).toISOString(),
          lte: new Date(eventTime + timeWindow).toISOString(),
        },
        type: [
          'SYSTEM.PERFORMANCE.SLOWDOWN',
          'SYSTEM.RESOURCE.HIGH',
          'AI.PROVIDER.SLOW_RESPONSE',
          'WORKFLOW.STEP.TIMEOUT',
        ],
      },
      orderBy: 'timestamp',
      order: 'asc',
    });

    const analysis = await this.analyzePerformanceCorrelation(rootEvent, performanceEvents.events);

    return {
      rootEvent,
      relatedEvents: performanceEvents.events,
      correlationType: 'performance',
      confidence: analysis.confidence,
      analysis,
    };
  }

  private async analyzeWorkflowCorrelation(
    rootEvent: Event,
    events: Event[]
  ): Promise<CorrelationAnalysis> {
    const patterns: string[] = [];
    const anomalies: Anomaly[] = [];
    const insights: string[] = [];
    const recommendations: string[] = [];

    // Analyze workflow patterns
    const workflowSteps = events.filter((e) => e.type.startsWith('WORKFLOW.STEP'));
    const failedSteps = workflowSteps.filter((e) => e.type.includes('FAILED'));

    if (failedSteps.length > 0) {
      patterns.push('Workflow contains failed steps');
      insights.push(`${failedSteps.length} steps failed in this workflow`);
      recommendations.push('Review failed steps for common patterns');
    }

    // Analyze timing patterns
    const stepDurations = this.calculateStepDurations(workflowSteps);
    const avgDuration = stepDurations.reduce((sum, d) => sum + d, 0) / stepDurations.length;

    if (avgDuration > 10 * 60 * 1000) {
      // > 10 minutes
      patterns.push('Slow workflow execution');
      insights.push(`Average step duration: ${(avgDuration / 1000 / 60).toFixed(1)} minutes`);
      recommendations.push('Consider optimizing workflow steps');
    }

    return {
      patterns,
      anomalies,
      insights,
      recommendations,
      confidence: this.calculateCorrelationConfidence(rootEvent, events),
    };
  }

  private async analyzeIssueCorrelation(
    rootEvent: Event,
    events: Event[]
  ): Promise<CorrelationAnalysis> {
    // Similar analysis for issue correlation
    return {
      patterns: [],
      anomalies: [],
      insights: [],
      recommendations: [],
      confidence: 0.8,
    };
  }

  private async analyzeUserCorrelation(
    rootEvent: Event,
    events: Event[]
  ): Promise<CorrelationAnalysis> {
    // Similar analysis for user correlation
    return {
      patterns: [],
      anomalies: [],
      insights: [],
      recommendations: [],
      confidence: 0.7,
    };
  }

  private async analyzePerformanceCorrelation(
    rootEvent: Event,
    events: Event[]
  ): Promise<CorrelationAnalysis> {
    // Similar analysis for performance correlation
    return {
      patterns: [],
      anomalies: [],
      insights: [],
      recommendations: [],
      confidence: 0.6,
    };
  }

  private calculateStepDurations(steps: Event[]): number[] {
    const durations: number[] = [];

    for (let i = 1; i < steps.length; i++) {
      const currentStep = steps[i];
      const previousStep = steps[i - 1];

      if (currentStep.context?.workflowId === previousStep.context?.workflowId) {
        const duration =
          new Date(currentStep.timestamp).getTime() - new Date(previousStep.timestamp).getTime();
        durations.push(duration);
      }
    }

    return durations;
  }

  private calculateCorrelationConfidence(rootEvent: Event, events: Event[]): number {
    // Simple confidence calculation based on event relationships
    let confidence = 0.5; // base confidence

    if (events.length > 0) {
      confidence += 0.2;
    }

    if (events.some((e) => e.context?.workflowId === rootEvent.context?.workflowId)) {
      confidence += 0.2;
    }

    if (events.some((e) => e.context?.issueId === rootEvent.context?.issueId)) {
      confidence += 0.1;
    }

    return Math.min(confidence, 1.0);
  }

  private getGroupColor(groupKey: string): string {
    const colors = [
      '#3b82f6',
      '#ef4444',
      '#10b981',
      '#f59e0b',
      '#8b5cf6',
      '#ec4899',
      '#14b8a6',
      '#f97316',
      '#6366f1',
      '#84cc16',
    ];

    const hash = groupKey.split('').reduce((acc, char) => acc + char.charCodeAt(0), 0);
    return colors[hash % colors.length];
  }

  private async detectTimingAnomalies(events: Event[]): Promise<Anomaly[]> {
    const anomalies: Anomaly[] = [];

    // Group events by type and analyze timing patterns
    const eventsByType = new Map<string, Event[]>();

    events.forEach((event) => {
      if (!eventsByType.has(event.type)) {
        eventsByType.set(event.type, []);
      }
      eventsByType.get(event.type)!.push(event);
    });

    for (const [eventType, typeEvents] of eventsByType) {
      if (typeEvents.length < 5) continue; // Need sufficient data

      const timestamps = typeEvents.map((e) => new Date(e.timestamp).getTime());
      const intervals = [];

      for (let i = 1; i < timestamps.length; i++) {
        intervals.push(timestamps[i] - timestamps[i - 1]);
      }

      const avgInterval = intervals.reduce((sum, interval) => sum + interval, 0) / intervals.length;
      const stdDev = Math.sqrt(
        intervals.reduce((sum, interval) => sum + Math.pow(interval - avgInterval, 2), 0) /
          intervals.length
      );

      // Find outliers (more than 2 standard deviations from mean)
      const outliers = intervals.filter(
        (interval) => Math.abs(interval - avgInterval) > 2 * stdDev
      );

      if (outliers.length > 0) {
        anomalies.push({
          type: 'timing',
          severity: outliers.length > intervals.length * 0.2 ? 'high' : 'medium',
          description: `Unusual timing patterns detected for ${eventType} events`,
          affectedEvents: typeEvents.slice(-outliers.length).map((e) => e.id),
        });
      }
    }

    return anomalies;
  }

  private async detectFrequencyAnomalies(events: Event[]): Promise<Anomaly[]> {
    // Implementation for frequency anomaly detection
    return [];
  }

  private async detectSequenceAnomalies(events: Event[]): Promise<Anomaly[]> {
    // Implementation for sequence anomaly detection
    return [];
  }

  private async detectContentAnomalies(events: Event[]): Promise<Anomaly[]> {
    // Implementation for content anomaly detection
    return [];
  }

  private async generateSuggestions(request: EventSearchRequest): Promise<string[]> {
    const suggestions: string[] = [];

    // Suggest common event types
    const popularTypes = await this.getPopularEventTypes();
    suggestions.push(...popularTypes.slice(0, 5));

    // Suggest time-based searches
    suggestions.push('last hour', 'last 24 hours', 'last week');

    // Suggest severity-based searches
    suggestions.push('error events', 'critical events', 'warnings');

    return suggestions;
  }

  private async getPopularEventTypes(): Promise<string[]> {
    const result = await this.searchClient.search({
      size: 0,
      aggs: {
        popular_types: {
          terms: { field: 'type', size: 10 },
        },
      },
    });

    return result.aggregations.popular_types.buckets.map((bucket: any) => bucket.key);
  }

  private exportAsJSON(events: Event[]): Buffer {
    return Buffer.from(JSON.stringify(events, null, 2), 'utf-8');
  }

  private exportAsCSV(events: Event[]): Buffer {
    const headers = [
      'id',
      'type',
      'timestamp',
      'severity',
      'source',
      'issueId',
      'workflowId',
      'userId',
      'message',
    ];

    const rows = events.map((event) => [
      event.id,
      event.type,
      event.timestamp,
      event.severity,
      event.source,
      event.context?.issueId || '',
      event.context?.workflowId || '',
      event.context?.userId || '',
      event.data.message || '',
    ]);

    const csvContent = [headers, ...rows]
      .map((row) => row.map((field) => `"${field}"`).join(','))
      .join('\n');

    return Buffer.from(csvContent, 'utf-8');
  }

  private exportAsXLSX(events: Event[]): Buffer {
    // Implementation would use a library like xlsx
    // For now, return CSV as fallback
    return this.exportAsCSV(events);
  }
}
```

### 3. Event Trail UI Components

#### Event Search Interface

```typescript
// packages/event-trail/src/components/EventSearchInterface.tsx
import React, { useState, useCallback } from 'react';
import { Card, CardContent, CardHeader, CardTitle } from '../ui/card';
import { Input } from '../ui/input';
import { Button } from '../ui/button';
import { Badge } from '../ui/badge';
import { DatePicker } from '../ui/date-picker';
import { Select } from '../ui/select';
import { EventFilters, EventSearchRequest } from '../types/event-trail.types';

interface EventSearchInterfaceProps {
  onSearch: (request: EventSearchRequest) => void;
  loading?: boolean;
  suggestions?: string[];
}

export function EventSearchInterface({ onSearch, loading, suggestions }: EventSearchInterfaceProps) {
  const [query, setQuery] = useState('');
  const [filters, setFilters] = useState<EventFilters>({});
  const [timeRange, setTimeRange] = useState({
    start: new Date(Date.now() - 24 * 60 * 60 * 1000), // Last 24 hours
    end: new Date()
  });
  const [showAdvanced, setShowAdvanced] = useState(false);

  const handleSearch = useCallback(() => {
    const searchRequest: EventSearchRequest = {
      query: query.trim() || undefined,
      filters,
      timeRange,
      sort: [{ field: 'timestamp', direction: 'desc' }],
      limit: 100
    };

    onSearch(searchRequest);
  }, [query, filters, timeRange, onSearch]);

  const handleQuickFilter = (filterType: string, value: string) => {
    setFilters(prev => ({
      ...prev,
      [filterType]: [value]
    }));
  };

  const handleTimeRangePreset = (preset: string) => {
    const now = new Date();
    let start: Date;

    switch (preset) {
      case 'last-1h':
        start = new Date(now.getTime() - 60 * 60 * 1000);
        break;
      case 'last-24h':
        start = new Date(now.getTime() - 24 * 60 * 60 * 1000);
        break;
      case 'last-7d':
        start = new Date(now.getTime() - 7 * 24 * 60 * 60 * 1000);
        break;
      case 'last-30d':
        start = new Date(now.getTime() - 30 * 24 * 60 * 60 * 1000);
        break;
      default:
        return;
    }

    setTimeRange({ start, end: now });
  };

  return (
    <Card>
      <CardHeader>
        <CardTitle>Event Search</CardTitle>
      </CardHeader>
      <CardContent className="space-y-4">
        {/* Main Search Bar */}
        <div className="flex gap-2">
          <Input
            placeholder="Search events... (try: error, workflow, issue-123)"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            onKeyPress={(e) => e.key === 'Enter' && handleSearch()}
            className="flex-1"
          />
          <Button onClick={handleSearch} disabled={loading}>
            {loading ? 'Searching...' : 'Search'}
          </Button>
        </div>

        {/* Quick Filters */}
        <div className="flex flex-wrap gap-2">
          <span className="text-sm text-gray-600">Quick filters:</span>
          <Badge variant="outline" className="cursor-pointer" onClick={() => handleQuickFilter('severity', 'error')}>
            Errors
          </Badge>
          <Badge variant="outline" className="cursor-pointer" onClick={() => handleQuickFilter('severity', 'critical')}>
            Critical
          </Badge>
          <Badge variant="outline" className="cursor-pointer" onClick={() => handleQuickFilter('eventTypes', 'WORKFLOW.STEP_COMPLETED')}>
            Workflow Steps
          </Badge>
          <Badge variant="outline" className="cursor-pointer" onClick={() => handleQuickFilter('eventTypes', 'AI.PROVIDER.USED')}>
            AI Usage
          </Badge>
          <Badge variant="outline" className="cursor-pointer" onClick={() => handleQuickFilter('eventTypes', 'ISSUE.COMPLETED')}>
            Completed Issues
          </Badge>
        </div>

        {/* Time Range Selection */}
        <div className="space-y-2">
          <span className="text-sm font-medium">Time Range:</span>
          <div className="flex gap-2">
            <Button
              variant="outline"
              size="sm"
              onClick={() => handleTimeRangePreset('last-1h')}
            >
              Last Hour
            </Button>
            <Button
              variant="outline"
              size="sm"
              onClick={() => handleTimeRangePreset('last-24h')}
            >
              Last 24 Hours
            </Button>
            <Button
              variant="outline"
              size="sm"
              onClick={() => handleTimeRangePreset('last-7d')}
            >
              Last 7 Days
            </Button>
            <Button
              variant="outline"
              size="sm"
              onClick={() => handleTimeRangePreset('last-30d')}
            >
              Last 30 Days
            </Button>
          </div>

          <div className="flex gap-2">
            <DatePicker
              selected={timeRange.start}
              onChange={(date) => date && setTimeRange(prev => ({ ...prev, start: date }))}
              placeholderText="Start date"
            />
            <DatePicker
              selected={timeRange.end}
              onChange={(date) => date && setTimeRange(prev => ({ ...prev, end: date }))}
              placeholderText="End date"
            />
          </div>
        </div>

        {/* Advanced Filters Toggle */}
        <Button
          variant="ghost"
          onClick={() => setShowAdvanced(!showAdvanced)}
          className="w-full justify-start"
        >
          {showAdvanced ? 'Hide' : 'Show'} Advanced Filters
        </Button>

        {/* Advanced Filters */}
        {showAdvanced && (
          <div className="space-y-4 border-t pt-4">
            {/* Event Type Filter */}
            <div>
              <label className="text-sm font-medium">Event Types:</label>
              <Select
                multiple
                value={filters.eventTypes || []}
                onChange={(values) => setFilters(prev => ({ ...prev, eventTypes: values }))}
                placeholder="Select event types..."
              >
                <option value="ISSUE.ASSIGNED">Issue Assigned</option>
                <option value="ISSUE.COMPLETED">Issue Completed</option>
                <option value="WORKFLOW.STEP_COMPLETED">Workflow Step Completed</option>
                <option value="AI.PROVIDER.USED">AI Provider Used</option>
                <option value="CODE.GENERATED">Code Generated</option>
                <option value="QUALITY.GATE.RESULT">Quality Gate Result</option>
                <option value="SYSTEM.STARTED">System Started</option>
                <option value="SYSTEM.ERROR">System Error</option>
              </Select>
            </div>

            {/* Severity Filter */}
            <div>
              <label className="text-sm font-medium">Severity:</label>
              <Select
                multiple
                value={filters.severity || []}
                onChange={(values) => setFilters(prev => ({ ...prev, severity: values }))}
                placeholder="Select severities..."
              >
                <option value="debug">Debug</option>
                <option value="info">Info</option>
                <option value="warn">Warning</option>
                <option value="error">Error</option>
                <option value="critical">Critical</option>
              </Select>
            </div>

            {/* Source Filter */}
            <div>
              <label className="text-sm font-medium">Source:</label>
              <Select
                multiple
                value={filters.source || []}
                onChange={(values) => setFilters(prev => ({ ...prev, source: values }))}
                placeholder="Select sources..."
              >
                <option value="system">System</option>
                <option value="orchestrator">Orchestrator</option>
                <option value="worker">Worker</option>
                <option value="api">API</option>
                <option value="dashboard">Dashboard</option>
              </Select>
            </div>

            {/* Tag Filters */}
            <div>
              <label className="text-sm font-medium">Tags:</label>
              <div className="space-y-2">
                <Input
                  placeholder="Issue ID (e.g., issue-123)"
                  onChange={(e) => {
                    const value = e.target.value.trim();
                    setFilters(prev => ({
                      ...prev,
                      tags: {
                        ...prev.tags,
                        issueId: value || undefined
                      }
                    }));
                  }}
                />
                <Input
                  placeholder="Workflow ID"
                  onChange={(e) => {
                    const value = e.target.value.trim();
                    setFilters(prev => ({
                      ...prev,
                      tags: {
                        ...prev.tags,
                        workflowId: value || undefined
                      }
                    }));
                  }}
                />
                <Input
                  placeholder="User ID"
                  onChange={(e) => {
                    const value = e.target.value.trim();
                    setFilters(prev => ({
                      ...prev,
                      tags: {
                        ...prev.tags,
                        userId: value || undefined
                      }
                    }));
                  }}
                />
              </div>
            </div>
          </div>
        )}

        {/* Search Suggestions */}
        {suggestions && suggestions.length > 0 && (
          <div className="space-y-2">
            <span className="text-sm font-medium">Suggestions:</span>
            <div className="flex flex-wrap gap-2">
              {suggestions.map((suggestion, index) => (
                <Badge
                  key={index}
                  variant="outline"
                  className="cursor-pointer"
                  onClick={() => setQuery(suggestion)}
                >
                  {suggestion}
                </Badge>
              ))}
            </div>
          </div>
        )}
      </CardContent>
    </Card>
  );
}
```

#### Event Timeline Visualization

```typescript
// packages/event-trail/src/components/EventTimeline.tsx
import React, { useRef, useEffect, useState } from 'react';
import { Card, CardContent, CardHeader, CardTitle } from '../ui/card';
import { Button } from '../ui/button';
import { EventTimeline as EventTimelineType, TimelineEvent } from '../types/event-trail.types';

interface EventTimelineProps {
  timeline: EventTimelineType;
  onEventClick?: (event: TimelineEvent) => void;
  onGroupClick?: (groupId: string) => void;
}

export function EventTimeline({ timeline, onEventClick, onGroupClick }: EventTimelineProps) {
  const svgRef = useRef<SVGSVGElement>(null);
  const [zoom, setZoom] = useState(1);
  const [pan, setPan] = useState({ x: 0, y: 0 });
  const [selectedEvent, setSelectedEvent] = useState<string | null>(null);

  const width = 1200;
  const height = 400;
  const timelineHeight = 60;
  const groupSpacing = 80;

  useEffect(() => {
    // Draw timeline
    if (!svgRef.current) return;

    const svg = svgRef.current;
    svg.innerHTML = '';

    // Draw timeline axis
    const axis = document.createElementNS('http://www.w3.org/2000/svg', 'line');
    axis.setAttribute('x1', '50');
    axis.setAttribute('y1', '30');
    axis.setAttribute('x2', String(width - 50));
    axis.setAttribute('y2', '30');
    axis.setAttribute('stroke', '#e5e7eb');
    axis.setAttribute('stroke-width', '2');
    svg.appendChild(axis);

    // Draw time markers
    const startTime = new Date(timeline.metadata.startTime).getTime();
    const endTime = new Date(timeline.metadata.endTime).getTime();
    const timeRange = endTime - startTime;

    for (let i = 0; i <= 10; i++) {
      const x = 50 + (width - 100) * (i / 10);
      const time = startTime + (timeRange * i / 10);

      const marker = document.createElementNS('http://www.w3.org/2000/svg', 'line');
      marker.setAttribute('x1', String(x));
      marker.setAttribute('y1', '25');
      marker.setAttribute('x2', String(x));
      marker.setAttribute('y2', '35');
      marker.setAttribute('stroke', '#9ca3af');
      marker.setAttribute('stroke-width', '1');
      svg.appendChild(marker);

      const label = document.createElementNS('http://www.w3.org/2000/svg', 'text');
      label.setAttribute('x', String(x));
      label.setAttribute('y', '20');
      label.setAttribute('text-anchor', 'middle');
      label.setAttribute('font-size', '12');
      label.setAttribute('fill', '#6b7280');
      label.textContent = new Date(time).toLocaleTimeString();
      svg.appendChild(label);
    }

    // Draw event groups
    timeline.groups.forEach((group, groupIndex) => {
      const groupY = 60 + groupIndex * groupSpacing;

      // Group label
      const groupLabel = document.createElementNS('http://www.w3.org/2000/svg', 'text');
      groupLabel.setAttribute('x', '10');
      groupLabel.setAttribute('y', String(groupY + 15));
      groupLabel.setAttribute('font-size', '12');
      groupLabel.setAttribute('fill', '#374151');
      groupLabel.setAttribute('font-weight', 'bold');
      groupLabel.textContent = group.name;
      groupLabel.style.cursor = 'pointer';
      groupLabel.addEventListener('click', () => onGroupClick?.(group.id));
      svg.appendChild(groupLabel);

      // Group lane
      const lane = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
      lane.setAttribute('x', '50');
      lane.setAttribute('y', String(groupY));
      lane.setAttribute('width', String(width - 100));
      lane.setAttribute('height', '30');
      lane.setAttribute('fill', '#f9fafb');
      lane.setAttribute('stroke', '#e5e7eb');
      lane.setAttribute('stroke-width', '1');
      svg.appendChild(lane);

      // Events in group
      group.events.forEach(event => {
        const eventX = 50 + (width - 100) * (event.x / 100);
        const eventY = groupY + 15;

        // Event circle
        const circle = document.createElementNS('http://www.w3.org/2000/svg', 'circle');
        circle.setAttribute('cx', String(eventX));
        circle.setAttribute('cy', String(eventY));
        circle.setAttribute('r', '6');
        circle.setAttribute('fill', this.getEventColor(event));
        circle.setAttribute('stroke', selectedEvent === event.id ? '#1f2937' : '#fff');
        circle.setAttribute('stroke-width', selectedEvent === event.id ? '3' : '2');
        circle.style.cursor = 'pointer';
        circle.addEventListener('click', () => {
          setSelectedEvent(event.id);
          onEventClick?.(event);
        });
        svg.appendChild(circle);

        // Event tooltip
        const title = document.createElementNS('http://www.w3.org/2000/svg', 'title');
        title.textContent = `${event.type}\n${new Date(event.timestamp).toLocaleString()}`;
        circle.appendChild(title);

        // Event duration bar (if applicable)
        if (event.duration) {
          const durationWidth = (event.duration / timeRange) * (width - 100);
          const bar = document.createElementNS('http://www.w3.org/2000/svg', 'rect');
          bar.setAttribute('x', String(eventX));
          bar.setAttribute('y', String(eventY - 2));
          bar.setAttribute('width', String(durationWidth));
          bar.setAttribute('height', '4');
          bar.setAttribute('fill', this.getEventColor(event));
          bar.setAttribute('opacity', '0.3');
          svg.appendChild(bar);
        }
      });
    });

  }, [timeline, zoom, pan, selectedEvent, onEventClick, onGroupClick]);

  const getEventColor = (event: TimelineEvent): string => {
    const colors = {
      debug: '#9ca3af',
      info: '#3b82f6',
      warn: '#f59e0b',
      error: '#ef4444',
      critical: '#dc2626'
    };
    return colors[event.severity] || '#6b7280';
  };

  const handleZoomIn = () => setZoom(prev => Math.min(prev * 1.2, 5));
  const handleZoomOut = () => setZoom(prev => Math.max(prev / 1.2, 0.5));
  const handleReset = () => {
    setZoom(1);
    setPan({ x: 0, y: 0 });
    setSelectedEvent(null);
  };

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center justify-between">
          Event Timeline
          <div className="flex gap-2">
            <Button variant="outline" size="sm" onClick={handleZoomOut}>
              Zoom Out
            </Button>
            <Button variant="outline" size="sm" onClick={handleZoomIn}>
              Zoom In
            </Button>
            <Button variant="outline" size="sm" onClick={handleReset}>
              Reset
            </Button>
          </div>
        </CardTitle>
      </CardHeader>
      <CardContent>
        <div className="overflow-auto">
          <svg
            ref={svgRef}
            width={width}
            height={60 + timeline.groups.length * groupSpacing}
            style={{ border: '1px solid #e5e7eb', borderRadius: '4px' }}
          />
        </div>

        {/* Timeline Legend */}
        <div className="mt-4 flex flex-wrap gap-4 text-sm">
          <div className="flex items-center gap-2">
            <div className="w-3 h-3 rounded-full bg-gray-400"></div>
            <span>Debug</span>
          </div>
          <div className="flex items-center gap-2">
            <div className="w-3 h-3 rounded-full bg-blue-500"></div>
            <span>Info</span>
          </div>
          <div className="flex items-center gap-2">
            <div className="w-3 h-3 rounded-full bg-yellow-500"></div>
            <span>Warning</span>
          </div>
          <div className="flex items-center gap-2">
            <div className="w-3 h-3 rounded-full bg-red-500"></div>
            <span>Error</span>
          </div>
          <div className="flex items-center gap-2">
            <div className="w-3 h-3 rounded-full bg-red-700"></div>
            <span>Critical</span>
          </div>
        </div>

        {/* Timeline Stats */}
        <div className="mt-4 grid grid-cols-4 gap-4 text-sm">
          <div>
            <span className="font-medium">Total Events:</span>
            <span className="ml-2">{timeline.metadata.totalEvents}</span>
          </div>
          <div>
            <span className="font-medium">Groups:</span>
            <span className="ml-2">{timeline.metadata.groups}</span>
          </div>
          <div>
            <span className="font-medium">Time Range:</span>
            <span className="ml-2">
              {new Date(timeline.metadata.startTime).toLocaleString()} - {new Date(timeline.metadata.endTime).toLocaleString()}
            </span>
          </div>
          <div>
            <span className="font-medium">Duration:</span>
            <span className="ml-2">
              {Math.round((new Date(timeline.metadata.endTime).getTime() - new Date(timeline.metadata.startTime).getTime()) / (1000 * 60 * 60))} hours
            </span>
          </div>
        </div>
      </CardContent>
    </Card>
  );
}
```

## Testing Strategy

### Event Trail Service Testing

```typescript
// packages/event-trail/src/services/__tests__/event-trail.service.test.ts
import { EventTrailService } from '../event-trail.service';
import { EventStore } from '@tamma/events';
import { SearchClient } from '../search.client';

describe('EventTrailService', () => {
  let eventTrailService: EventTrailService;
  let mockEventStore: jest.Mocked<EventStore>;
  let mockSearchClient: jest.Mocked<SearchClient>;

  beforeEach(() => {
    mockEventStore = {
      queryEvents: jest.fn(),
      getEvent: jest.fn(),
      getEventsByIds: jest.fn(),
    } as any;

    mockSearchClient = {
      search: jest.fn(),
    } as any;

    eventTrailService = new EventTrailService(mockEventStore, mockSearchClient, {} as any);
  });

  describe('searchEvents', () => {
    it('builds search query correctly', async () => {
      const request = {
        query: 'error',
        filters: {
          eventTypes: ['ISSUE.COMPLETED'],
          severity: ['error'],
        },
        timeRange: {
          start: new Date('2025-01-01'),
          end: new Date('2025-01-02'),
        },
        sort: [{ field: 'timestamp', direction: 'desc' }],
        limit: 100,
      };

      const mockSearchResult = {
        hits: [{ id: 'event-1' }, { id: 'event-2' }],
        total: 2,
        aggregations: {
          event_types: { buckets: [] },
          severity: { buckets: [] },
          source: { buckets: [] },
          timeline: { buckets: [] },
          users: { buckets: [] },
          issues: { buckets: [] },
        },
      };

      mockSearchClient.search.mockResolvedValue(mockSearchResult);
      mockEventStore.getEventsByIds.mockResolvedValue([
        { id: 'event-1', type: 'ISSUE.COMPLETED' },
        { id: 'event-2', type: 'ISSUE.COMPLETED' },
      ]);

      const result = await eventTrailService.searchEvents(request);

      expect(mockSearchClient.search).toHaveBeenCalledWith(
        expect.objectContaining({
          query: expect.objectContaining({
            bool: expect.objectContaining({
              must: expect.arrayContaining([
                expect.objectContaining({
                  multi_match: expect.objectContaining({
                    query: 'error',
                  }),
                }),
              ]),
              filter: expect.arrayContaining([
                expect.objectContaining({
                  range: expect.objectContaining({
                    timestamp: expect.objectContaining({
                      gte: '2025-01-01T00:00:00.000Z',
                      lte: '2025-01-02T00:00:00.000Z',
                    }),
                  }),
                }),
                expect.objectContaining({
                  terms: { type: ['ISSUE.COMPLETED'] },
                }),
                expect.objectContaining({
                  terms: { severity: ['error'] },
                }),
              ]),
            }),
          }),
          size: 100,
          from: 0,
        })
      );

      expect(result.events).toHaveLength(2);
      expect(result.total).toBe(2);
    });
  });

  describe('getEventTimeline', () => {
    it('groups events correctly for timeline', async () => {
      const mockEvents = [
        {
          id: 'event-1',
          type: 'ISSUE.ASSIGNED',
          timestamp: '2025-01-01T10:00:00Z',
          context: { workflowId: 'workflow-1' },
        },
        {
          id: 'event-2',
          type: 'WORKFLOW.STEP_COMPLETED',
          timestamp: '2025-01-01T10:05:00Z',
          context: { workflowId: 'workflow-1' },
        },
        {
          id: 'event-3',
          type: 'ISSUE.ASSIGNED',
          timestamp: '2025-01-01T10:10:00Z',
          context: { issueId: 'issue-1' },
        },
      ];

      mockSearchClient.search.mockResolvedValue({
        hits: mockEvents.map((e) => ({ id: e.id })),
        total: 3,
        aggregations: {},
      });
      mockEventStore.getEventsByIds.mockResolvedValue(mockEvents);

      const request = {
        filters: {},
        timeRange: {
          start: new Date('2025-01-01'),
          end: new Date('2025-01-02'),
        },
        sort: [{ field: 'timestamp', direction: 'asc' }],
        limit: 100,
      };

      const timeline = await eventTrailService.getEventTimeline(request);

      expect(timeline.groups).toHaveLength(2);
      expect(timeline.groups[0].name).toBe('Workflow workflow-1');
      expect(timeline.groups[0].events).toHaveLength(2);
      expect(timeline.groups[1].name).toBe('Issue issue-1');
      expect(timeline.groups[1].events).toHaveLength(1);
    });
  });

  describe('correlateEvents', () => {
    it('finds workflow correlations', async () => {
      const rootEvent = {
        id: 'event-1',
        type: 'WORKFLOW.STEP_COMPLETED',
        context: { workflowId: 'workflow-1' },
      };

      const workflowEvents = [
        rootEvent,
        { id: 'event-2', type: 'WORKFLOW.STEP_STARTED', context: { workflowId: 'workflow-1' } },
        { id: 'event-3', type: 'ISSUE.ASSIGNED', context: { workflowId: 'workflow-1' } },
      ];

      mockEventStore.getEvent.mockResolvedValue(rootEvent);
      mockEventStore.queryEvents.mockResolvedValue({ events: workflowEvents });

      const correlations = await eventTrailService.correlateEvents('event-1');

      expect(correlations).toHaveLength(4); // workflow, issue, user, performance
      const workflowCorrelation = correlations.find((c) => c.correlationType === 'workflow');
      expect(workflowCorrelation).toBeDefined();
      expect(workflowCorrelation.relatedEvents).toHaveLength(2);
    });
  });
});
```

### UI Component Testing

```typescript
// packages/event-trail/src/components/__tests__/EventSearchInterface.test.tsx
import React from 'react';
import { render, screen, fireEvent, waitFor } from '@testing-library/react';
import { EventSearchInterface } from '../EventSearchInterface';

describe('EventSearchInterface', () => {
  const mockOnSearch = jest.fn();

  beforeEach(() => {
    jest.clearAllMocks();
  });

  it('renders search interface correctly', () => {
    render(<EventSearchInterface onSearch={mockOnSearch} />);

    expect(screen.getByPlaceholderText('Search events...')).toBeInTheDocument();
    expect(screen.getByText('Quick filters:')).toBeInTheDocument();
    expect(screen.getByText('Time Range:')).toBeInTheDocument();
    expect(screen.getByText('Show Advanced Filters')).toBeInTheDocument();
  });

  it('handles search submission', async () => {
    render(<EventSearchInterface onSearch={mockOnSearch} />);

    const searchInput = screen.getByPlaceholderText('Search events...');
    const searchButton = screen.getByText('Search');

    fireEvent.change(searchInput, { target: { value: 'error events' } });
    fireEvent.click(searchButton);

    await waitFor(() => {
      expect(mockOnSearch).toHaveBeenCalledWith(
        expect.objectContaining({
          query: 'error events',
          filters: {},
          limit: 100
        })
      );
    });
  });

  it('applies quick filters correctly', async () => {
    render(<EventSearchInterface onSearch={mockOnSearch} />);

    const errorsFilter = screen.getByText('Errors');
    fireEvent.click(errorsFilter);

    const searchButton = screen.getByText('Search');
    fireEvent.click(searchButton);

    await waitFor(() => {
      expect(mockOnSearch).toHaveBeenCalledWith(
        expect.objectContaining({
          filters: expect.objectContaining({
            severity: ['error']
          })
        })
      );
    });
  });

  it('handles time range presets', async () => {
    render(<EventSearchInterface onSearch={mockOnSearch} />);

    const lastHourButton = screen.getByText('Last Hour');
    fireEvent.click(lastHourButton);

    const searchButton = screen.getByText('Search');
    fireEvent.click(searchButton);

    await waitFor(() => {
      expect(mockOnSearch).toHaveBeenCalledWith(
        expect.objectContaining({
          timeRange: expect.objectContaining({
            start: expect.any(Date),
            end: expect.any(Date)
          })
        })
      );
    });
  });

  it('shows advanced filters when toggled', () => {
    render(<EventSearchInterface onSearch={mockOnSearch} />);

    expect(screen.queryByText('Event Types:')).not.toBeInTheDocument();

    const toggleButton = screen.getByText('Show Advanced Filters');
    fireEvent.click(toggleButton);

    expect(screen.getByText('Event Types:')).toBeInTheDocument();
    expect(screen.getByText('Severity:')).toBeInTheDocument();
    expect(screen.getByText('Source:')).toBeInTheDocument();
    expect(screen.getByText('Tags:')).toBeInTheDocument();
  });
});
```

## Performance Requirements

### Search Performance

- **Query Response Time**: 95th percentile under 2 seconds for complex queries
- **Simple Queries**: 95th percentile under 500ms for basic text searches
- **Index Performance**: All search queries use appropriate indexes
- **Concurrent Users**: Support 100 concurrent users searching events
- **Result Limits**: Pagination with efficient cursor-based navigation

### Timeline Performance

- **Timeline Rendering**: 1000+ events rendered in under 1 second
- **Zoom/Pan Performance**: Smooth 60fps interactions
- **Real-time Updates**: New events appear in timeline within 1 second
- **Memory Usage**: Timeline memory usage under 100MB for 10k events
- **Group Calculations**: Event grouping completed in under 500ms

### Export Performance

- **JSON Export**: 10k events exported in under 5 seconds
- **CSV Export**: 10k events exported in under 10 seconds
- **XLSX Export**: 10k events exported in under 15 seconds
- **Memory Usage**: Export memory usage under 500MB
- **File Sizes**: Reasonable file sizes with compression

## Security Considerations

### Access Control

- **Event Access**: Role-based access to sensitive event data
- **Search Restrictions**: Users can only search events they have permission to view
- **Export Controls**: Sensitive event export requires additional authorization
- **Audit Logging**: All event searches and exports logged for compliance

### Data Privacy

- **PII Protection**: Personal information redacted from search results
- **Data Retention**: Configurable retention policies for event data
- **Encryption**: Event data encrypted at rest and in transit
- **Anonymization**: User data anonymized in long-term storage

## Success Metrics

### User Experience

- **Search Success Rate**: 95%+ of searches return relevant results
- **Search Speed**: 90%+ of searches complete within 2 seconds
- **User Satisfaction**: Net Promoter Score (NPS) above 50
- **Feature Adoption**: 80%+ of users use advanced search features

### System Performance

- **Query Performance**: 95th percentile query time under 2 seconds
- **System Availability**: 99.9% uptime for event trail service
- **Data Freshness**: Real-time events appear within 1 second
- **Storage Efficiency**: Compressed event storage with 70%+ space savings

### Business Impact

- **Investigation Efficiency**: 50%+ reduction in time to investigate issues
- **Audit Compliance**: 100% audit trail coverage for compliance requirements
- **Root Cause Analysis**: 40%+ improvement in root cause identification
- **Knowledge Discovery**: 60%+ increase in pattern recognition across events

## Rollout Plan

### Phase 1: Core Search (Week 1-2)

1. **Search Infrastructure**
   - Elasticsearch setup and configuration
   - Event indexing pipeline
   - Basic search API implementation
   - Search performance optimization

2. **Basic UI Components**
   - Search interface component
   - Event list display
   - Basic filtering options
   - Simple pagination

### Phase 2: Advanced Features (Week 3-4)

1. **Advanced Search**
   - Faceted search implementation
   - Complex query syntax support
   - Saved searches functionality
   - Search suggestions and autocomplete

2. **Timeline Visualization**
   - Interactive timeline component
   - Event grouping and clustering
   - Zoom and pan functionality
   - Real-time event streaming

### Phase 3: Investigation Tools (Week 5-6)

1. **Correlation Analysis**
   - Event correlation engine
   - Root cause analysis tools
   - Anomaly detection algorithms
   - Pattern recognition features

2. **Export and Reporting**
   - Multiple export formats
   - Custom report generation
   - Compliance reporting
   - Scheduled report delivery

### Phase 4: Optimization & Enhancement (Week 7-8)

1. **Performance Optimization**
   - Query optimization
   - Caching implementation
   - Index tuning
   - Memory usage optimization

2. **User Experience Enhancement**
   - UI/UX refinements
   - Mobile responsiveness
   - Accessibility improvements
   - User feedback integration

## Dependencies

### Internal Dependencies

- **Epic 4.1**: Event Schema Design (provides event structure)
- **Epic 4.7**: Event Query API (provides query infrastructure)
- **Epic 5.1**: Structured Logging (provides event data)
- **Epic 5.3**: Dashboard Framework (provides UI components)

### External Dependencies

- **Elasticsearch**: Search engine for full-text and faceted search
- **React 18+**: Frontend framework for event trail UI
- **D3.js**: Data visualization for timeline components
- **Node.js**: Backend service for search and correlation
- **PostgreSQL**: Primary event store and metadata

### Infrastructure Dependencies

- **Kubernetes**: Scalable deployment platform
- **Load Balancer**: Traffic distribution for search service
- **Monitoring**: System health and performance monitoring
- **Storage**: Sufficient storage for event data and indexes

---

**Story Status**: Ready for Development  
**Implementation Priority**: Optional (Post-MVP Enhancement)  
**Target Completion**: Sprint 6 (Post-MVP)  
**Dependencies**: Epic 4.1, Epic 4.7, Epic 5.1, Epic 5.3
