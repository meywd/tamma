# Story 5.3: Real-Time Dashboard - System Health

**Epic**: Epic 5 - Observability & Production Readiness  
**Status**: Ready for Dev  
**MVP Priority**: Optional (Post-MVP Enhancement)  
**Estimated Complexity**: High  
**Target Implementation**: Sprint 6 (Post-MVP)

## Acceptance Criteria

### System Health Dashboard

- [ ] **Real-time system metrics dashboard** accessible via web interface
- [ ] **Live updating** displays current system status without page refresh
- [ ] **Multiple views** for different user roles (admin, developer, operator)
- [ ] **Responsive design** works on desktop, tablet, and mobile devices
- [ ] **Authentication** restricts access to authorized users only

### Core Health Metrics

- [ ] **System uptime** with service availability percentage
- [ ] **Active workflows** showing running, completed, and failed counts
- [ ] **Resource utilization** (CPU, memory, disk, network) per service
- [ ] **API response times** with p50, p95, p99 percentiles
- [ ] **Error rates** by service and error type
- [ ] **Database health** including connection pool status and query performance
- [ ] **External service status** (AI providers, Git platforms)

### Alerting Integration

- [ ] **Active alerts panel** showing current critical and warning alerts
- [ ] **Alert history** with resolution status and time to resolution
- [ ] **Alert acknowledgment** functionality for operators
- [ ] **Alert escalation** status and current assignee

### Performance Visualization

- [ ] **Time-series charts** for metrics over time (last hour, 24h, 7d, 30d)
- [ ] **Heat maps** for system activity patterns
- [ ] **Service dependency graph** showing system architecture
- [ ] **Resource usage trends** with capacity planning indicators
- [ ] **Performance baselines** with deviation highlighting

### Interactive Features

- [ ] **Drill-down capability** from high-level metrics to detailed data
- [ ] **Service-specific views** for individual component health
- [ ] **Workflow tracking** with real-time progress updates
- [ ] **Log correlation** linking metrics to relevant log entries
- [ ] **Export functionality** for charts and data (PNG, CSV, JSON)

## Technical Context

### Current System State

From `docs/stories/5-1-structured-logging-implementation.md`:

- Structured logging with Pino implemented
- Log levels: DEBUG, INFO, WARN, ERROR with structured context
- Sensitive data redaction in place
- Performance logging with operation timing

From `docs/stories/5-2-metrics-collection-infrastructure.md`:

- Prometheus metrics infrastructure implemented
- Counter, gauge, histogram metric types available
- Custom metrics for workflows, AI usage, Git operations
- Metrics endpoint at `/metrics` for Prometheus scraping

### Dashboard Architecture

The system health dashboard will be built as a React-based web application that consumes metrics and logs from multiple sources:

```typescript
// Dashboard architecture
┌─────────────────┐    ┌──────────────────┐    ┌─────────────────┐
│   Dashboard UI  │    │  Dashboard API   │    │   Data Sources  │
│   (React SPA)   │◄──►│   (Fastify)      │◄──►│                 │
│                 │    │                  │    │ ┌─────────────┐ │
│ - Real-time     │    │ - WebSocket      │    │ │ Prometheus  │ │
│   updates       │    │ - REST API       │    │ │ Metrics     │ │
│ - Charts        │    │ - Authentication │    │ └─────────────┘ │
│ - Alerts        │    │ - Authorization  │    │ ┌─────────────┐ │
│ - Drill-downs   │    │ - Data aggregation│    │ │ Pino Logs  │ │
└─────────────────┘    └──────────────────┘    │ │ (JSON files)│ │
                                                │ └─────────────┘ │
                                                │ ┌─────────────┐ │
                                                │ │ Event Store │ │
                                                │ │ (PostgreSQL)│ │
                                                │ └─────────────┘ │
                                                └─────────────────┘
```

## Technical Implementation

### 1. Dashboard Frontend Architecture

#### Core Dashboard Structure

```typescript
// packages/dashboard/src/types/dashboard.types.ts
export interface DashboardConfig {
  refreshInterval: number; // milliseconds
  dataRetention: number; // days
  alertThresholds: AlertThresholds;
  views: DashboardView[];
}

export interface DashboardView {
  id: string;
  name: string;
  description: string;
  role: 'admin' | 'developer' | 'operator' | 'readonly';
  widgets: Widget[];
  layout: LayoutConfig;
}

export interface Widget {
  id: string;
  type: WidgetType;
  title: string;
  dataSource: DataSource;
  config: WidgetConfig;
  position: Position;
  size: Size;
}

export type WidgetType =
  | 'metric-chart'
  | 'system-status'
  | 'alert-panel'
  | 'service-health'
  | 'workflow-tracker'
  | 'resource-usage'
  | 'performance-trends'
  | 'log-viewer';

export interface DataSource {
  type: 'prometheus' | 'logs' | 'events' | 'api';
  endpoint: string;
  query: string;
  refreshInterval?: number;
}
```

#### Real-time Data Management

```typescript
// packages/dashboard/src/hooks/useRealTimeData.ts
import { useEffect, useState, useCallback } from 'react';
import { useWebSocket } from './useWebSocket';

export function useRealTimeData<T>(
  dataSource: DataSource,
  initialData?: T
): {
  data: T | null;
  loading: boolean;
  error: string | null;
  lastUpdate: Date | null;
} {
  const [data, setData] = useState<T | null>(initialData || null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [lastUpdate, setLastUpdate] = useState<Date | null>(null);

  const { socket, connected } = useWebSocket('/api/dashboard/ws');

  const fetchData = useCallback(async () => {
    try {
      setLoading(true);
      setError(null);

      let response: Response;

      switch (dataSource.type) {
        case 'prometheus':
          response = await fetch(`/api/dashboard/metrics`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ query: dataSource.query }),
          });
          break;

        case 'logs':
          response = await fetch(`/api/dashboard/logs`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
              query: dataSource.query,
              limit: 100,
              timeRange: '1h',
            }),
          });
          break;

        case 'events':
          response = await fetch(`/api/dashboard/events`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
              filters: JSON.parse(dataSource.query || '{}'),
              limit: 50,
            }),
          });
          break;

        default:
          throw new Error(`Unsupported data source type: ${dataSource.type}`);
      }

      if (!response.ok) {
        throw new Error(`HTTP ${response.status}: ${response.statusText}`);
      }

      const result = await response.json();
      setData(result.data);
      setLastUpdate(new Date());
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Unknown error');
    } finally {
      setLoading(false);
    }
  }, [dataSource]);

  // WebSocket updates
  useEffect(() => {
    if (!connected || !socket) return;

    const handleMessage = (event: MessageEvent) => {
      try {
        const message = JSON.parse(event.data);

        if (message.type === 'data-update' && message.source === dataSource.type) {
          setData(message.data);
          setLastUpdate(new Date());
        }
      } catch (err) {
        console.error('Failed to parse WebSocket message:', err);
      }
    };

    socket.addEventListener('message', handleMessage);
    return () => socket.removeEventListener('message', handleMessage);
  }, [connected, socket, dataSource.type]);

  // Initial fetch and periodic refresh
  useEffect(() => {
    fetchData();

    const interval = setInterval(fetchData, dataSource.refreshInterval || 30000);
    return () => clearInterval(interval);
  }, [fetchData, dataSource.refreshInterval]);

  return { data, loading, error, lastUpdate };
}
```

#### Widget Components

```typescript
// packages/dashboard/src/components/widgets/SystemStatusWidget.tsx
import React from 'react';
import { Card, CardContent, CardHeader, CardTitle } from '../ui/card';
import { Badge } from '../ui/badge';
import { useRealTimeData } from '../../hooks/useRealTimeData';
import { SystemStatus } from '../../types/dashboard.types';

export function SystemStatusWidget({ config }: { config: any }) {
  const { data, loading, error, lastUpdate } = useRealTimeData<SystemStatus>({
    type: 'api',
    endpoint: '/api/dashboard/system-status',
    query: ''
  });

  if (loading) return <div>Loading system status...</div>;
  if (error) return <div>Error: {error}</div>;
  if (!data) return <div>No data available</div>;

  const getStatusColor = (status: string) => {
    switch (status) {
      case 'healthy': return 'bg-green-500';
      case 'degraded': return 'bg-yellow-500';
      case 'unhealthy': return 'bg-red-500';
      default: return 'bg-gray-500';
    }
  };

  return (
    <Card>
      <CardHeader>
        <CardTitle className="flex items-center justify-between">
          System Status
          <Badge variant="outline" className={getStatusColor(data.overallStatus)}>
            {data.overallStatus.toUpperCase()}
          </Badge>
        </CardTitle>
      </CardHeader>
      <CardContent>
        <div className="grid grid-cols-2 gap-4">
          <div>
            <h4 className="font-semibold">Services</h4>
            {data.services.map(service => (
              <div key={service.name} className="flex justify-between items-center py-1">
                <span className="text-sm">{service.name}</span>
                <Badge variant="outline" className={getStatusColor(service.status)}>
                  {service.status}
                </Badge>
              </div>
            ))}
          </div>

          <div>
            <h4 className="font-semibold">System Metrics</h4>
            <div className="space-y-1 text-sm">
              <div className="flex justify-between">
                <span>Uptime:</span>
                <span>{data.uptime}</span>
              </div>
              <div className="flex justify-between">
                <span>Active Workflows:</span>
                <span>{data.activeWorkflows}</span>
              </div>
              <div className="flex justify-between">
                <span>CPU Usage:</span>
                <span>{data.cpuUsage}%</span>
              </div>
              <div className="flex justify-between">
                <span>Memory Usage:</span>
                <span>{data.memoryUsage}%</span>
              </div>
            </div>
          </div>
        </div>

        {lastUpdate && (
          <div className="text-xs text-gray-500 mt-2">
            Last updated: {lastUpdate.toLocaleTimeString()}
          </div>
        )}
      </CardContent>
    </Card>
  );
}
```

### 2. Dashboard Backend API

#### Fastify Dashboard Routes

```typescript
// packages/dashboard/src/api/dashboard.routes.ts
import { FastifyInstance, FastifyRequest } from 'fastify';
import { WebSocket } from '@fastify/websocket';
import { DashboardService } from '../services/dashboard.service';
import { authenticateToken } from '../middleware/auth';

export async function dashboardRoutes(fastify: FastifyInstance) {
  const dashboardService = new DashboardService();

  // WebSocket connection for real-time updates
  fastify.get('/ws', { websocket: true }, async (connection: WebSocket, req) => {
    try {
      // Authenticate WebSocket connection
      const token = req.headers.authorization?.replace('Bearer ', '');
      const user = await authenticateToken(token);

      if (!user) {
        connection.close(1008, 'Unauthorized');
        return;
      }

      // Subscribe to real-time updates
      const subscription = await dashboardService.subscribeToUpdates(user, {
        onMessage: (data) => {
          connection.send(JSON.stringify(data));
        },
        onError: (error) => {
          console.error('WebSocket error:', error);
        },
      });

      connection.socket.on('close', () => {
        subscription.unsubscribe();
      });
    } catch (error) {
      connection.close(1011, 'Internal server error');
    }
  });

  // System status endpoint
  fastify.post(
    '/system-status',
    {
      preHandler: [authenticateToken],
    },
    async (request: FastifyRequest) => {
      return dashboardService.getSystemStatus();
    }
  );

  // Metrics query endpoint
  fastify.post(
    '/metrics',
    {
      preHandler: [authenticateToken],
    },
    async (request: FastifyRequest<{ Body: { query: string } }>) => {
      const { query } = request.body;
      return dashboardService.queryMetrics(query);
    }
  );

  // Logs query endpoint
  fastify.post(
    '/logs',
    {
      preHandler: [authenticateToken],
    },
    async (
      request: FastifyRequest<{
        Body: { query: string; limit: number; timeRange: string };
      }>
    ) => {
      const { query, limit, timeRange } = request.body;
      return dashboardService.queryLogs(query, limit, timeRange);
    }
  );

  // Events query endpoint
  fastify.post(
    '/events',
    {
      preHandler: [authenticateToken],
    },
    async (
      request: FastifyRequest<{
        Body: { filters: Record<string, any>; limit: number };
      }>
    ) => {
      const { filters, limit } = request.body;
      return dashboardService.queryEvents(filters, limit);
    }
  );

  // Alert management endpoints
  fastify.get(
    '/alerts',
    {
      preHandler: [authenticateToken],
    },
    async () => {
      return dashboardService.getActiveAlerts();
    }
  );

  fastify.post(
    '/alerts/:alertId/acknowledge',
    {
      preHandler: [authenticateToken],
    },
    async (request: FastifyRequest<{ Params: { alertId: string } }>) => {
      const { alertId } = request.params;
      return dashboardService.acknowledgeAlert(alertId, request.user);
    }
  );
}
```

#### Dashboard Service Implementation

```typescript
// packages/dashboard/src/services/dashboard.service.ts
import { PrometheusService } from '@tamma/observability';
import { EventStore } from '@tamma/events';
import { Logger } from 'pino';

export class DashboardService {
  constructor(
    private prometheusService: PrometheusService,
    private eventStore: EventStore,
    private logger: Logger
  ) {}

  async getSystemStatus(): Promise<SystemStatus> {
    const now = Date.now();

    // Get service health from Prometheus
    const serviceQueries = {
      orchestrator: 'up{job="orchestrator"}',
      workers: 'up{job="workers"}',
      api: 'up{job="api"}',
      dashboard: 'up{job="dashboard"}',
    };

    const services = await Promise.all(
      Object.entries(serviceQueries).map(async ([name, query]) => {
        const result = await this.prometheusService.query(query);
        const isUp = result.data.result.length > 0 && result.data.result[0].value[1] === '1';

        return {
          name,
          status: isUp ? 'healthy' : 'unhealthy',
          lastCheck: new Date().toISOString(),
        };
      })
    );

    // Get system metrics
    const [uptime, activeWorkflows, cpuUsage, memoryUsage] = await Promise.all([
      this.getSystemUptime(),
      this.getActiveWorkflows(),
      this.getCpuUsage(),
      this.getMemoryUsage(),
    ]);

    // Determine overall status
    const unhealthyServices = services.filter((s) => s.status === 'unhealthy');
    const overallStatus =
      unhealthyServices.length === 0
        ? 'healthy'
        : unhealthyServices.length < services.length / 2
          ? 'degraded'
          : 'unhealthy';

    return {
      overallStatus,
      services,
      uptime,
      activeWorkflows,
      cpuUsage,
      memoryUsage,
      timestamp: new Date(now).toISOString(),
    };
  }

  async queryMetrics(query: string): Promise<any> {
    return this.prometheusService.query(query);
  }

  async queryLogs(query: string, limit: number, timeRange: string): Promise<any> {
    // Parse time range
    const endTime = Date.now();
    const startTime = endTime - this.parseTimeRange(timeRange);

    // Query logs from log files or log aggregation system
    const logs = await this.searchLogs({
      query,
      startTime,
      endTime,
      limit,
    });

    return {
      logs,
      total: logs.length,
      timeRange,
      query,
    };
  }

  async queryEvents(filters: Record<string, any>, limit: number): Promise<any> {
    const events = await this.eventStore.queryEvents({
      filters,
      limit,
      orderBy: 'timestamp',
      order: 'desc',
    });

    return {
      events,
      total: events.length,
      filters,
    };
  }

  async getActiveAlerts(): Promise<Alert[]> {
    // Query active alerts from alerting system
    return this.eventStore.queryEvents({
      filters: {
        type: 'ALERT.FIRED',
        resolved: false,
      },
      limit: 100,
    });
  }

  async acknowledgeAlert(alertId: string, user: any): Promise<void> {
    await this.eventStore.append({
      type: 'ALERT.ACKNOWLEDGED',
      tags: {
        alertId,
        userId: user.id,
      },
      data: {
        acknowledgedBy: user.id,
        acknowledgedAt: new Date().toISOString(),
      },
    });
  }

  async subscribeToUpdates(
    user: any,
    handlers: {
      onMessage: (data: any) => void;
      onError: (error: Error) => void;
    }
  ): Promise<{ unsubscribe: () => void }> {
    // Subscribe to real-time updates from various sources
    const subscriptions = [];

    // Subscribe to metrics updates
    const metricsSubscription = this.prometheusService.subscribe((data) => {
      handlers.onMessage({
        type: 'data-update',
        source: 'prometheus',
        data,
      });
    });
    subscriptions.push(metricsSubscription);

    // Subscribe to event updates
    const eventSubscription = this.eventStore.subscribe((event) => {
      handlers.onMessage({
        type: 'data-update',
        source: 'events',
        data: event,
      });
    });
    subscriptions.push(eventSubscription);

    return {
      unsubscribe: () => {
        subscriptions.forEach((sub) => sub.unsubscribe());
      },
    };
  }

  private async getSystemUptime(): Promise<string> {
    const startEvent = await this.eventStore.queryEvents({
      filters: {
        type: 'SYSTEM.STARTED',
      },
      limit: 1,
      orderBy: 'timestamp',
      order: 'desc',
    });

    if (startEvent.events.length === 0) {
      return 'Unknown';
    }

    const startTime = new Date(startEvent.events[0].timestamp);
    const uptime = Date.now() - startTime.getTime();

    return this.formatDuration(uptime);
  }

  private async getActiveWorkflows(): Promise<number> {
    const result = await this.prometheusService.query('sum(tamma_workflows_active_total)');

    return parseFloat(result.data.result[0]?.value[1] || '0');
  }

  private async getCpuUsage(): Promise<number> {
    const result = await this.prometheusService.query(
      'avg by (instance) (100 - (avg by (instance) (irate(node_cpu_seconds_total{mode="idle"}[5m])) * 100))'
    );

    return parseFloat(result.data.result[0]?.value[1] || '0');
  }

  private async getMemoryUsage(): Promise<number> {
    const result = await this.prometheusService.query(
      '(1 - (node_memory_MemAvailable_bytes / node_memory_MemTotal_bytes)) * 100'
    );

    return parseFloat(result.data.result[0]?.value[1] || '0');
  }

  private parseTimeRange(timeRange: string): number {
    const units: Record<string, number> = {
      s: 1000,
      m: 60 * 1000,
      h: 60 * 60 * 1000,
      d: 24 * 60 * 60 * 1000,
    };

    const match = timeRange.match(/^(\d+)([smhd])$/);
    if (!match) {
      throw new Error(`Invalid time range format: ${timeRange}`);
    }

    const [, value, unit] = match;
    return parseInt(value) * units[unit];
  }

  private formatDuration(milliseconds: number): string {
    const seconds = Math.floor(milliseconds / 1000);
    const minutes = Math.floor(seconds / 60);
    const hours = Math.floor(minutes / 60);
    const days = Math.floor(hours / 24);

    if (days > 0) {
      return `${days}d ${hours % 24}h ${minutes % 60}m`;
    } else if (hours > 0) {
      return `${hours}h ${minutes % 60}m`;
    } else if (minutes > 0) {
      return `${minutes}m ${seconds % 60}s`;
    } else {
      return `${seconds}s`;
    }
  }
}
```

### 3. Authentication & Authorization

#### JWT-based Authentication

```typescript
// packages/dashboard/src/middleware/auth.ts
import jwt from 'jsonwebtoken';
import { FastifyRequest } from 'fastify';

const JWT_SECRET = process.env.DASHBOARD_JWT_SECRET || 'default-secret';

export interface DashboardUser {
  id: string;
  username: string;
  email: string;
  role: 'admin' | 'developer' | 'operator' | 'readonly';
  permissions: string[];
}

export async function authenticateToken(token?: string): Promise<DashboardUser | null> {
  if (!token) {
    return null;
  }

  try {
    const decoded = jwt.verify(token, JWT_SECRET) as any;
    return {
      id: decoded.sub,
      username: decoded.username,
      email: decoded.email,
      role: decoded.role,
      permissions: decoded.permissions || [],
    };
  } catch (error) {
    return null;
  }
}

export function requireRole(requiredRole: string) {
  return async (request: FastifyRequest) => {
    const user = request.user as DashboardUser;

    if (!user) {
      throw new Error('Authentication required');
    }

    const roleHierarchy = {
      readonly: 0,
      operator: 1,
      developer: 2,
      admin: 3,
    };

    const userLevel = roleHierarchy[user.role] || 0;
    const requiredLevel = roleHierarchy[requiredRole] || 0;

    if (userLevel < requiredLevel) {
      throw new Error(`Insufficient privileges. Required: ${requiredRole}`);
    }

    return user;
  };
}
```

### 4. Performance Optimization

#### Data Caching Strategy

```typescript
// packages/dashboard/src/services/cache.service.ts
import NodeCache from 'node-cache';

export class CacheService {
  private cache: NodeCache;
  private metricsCache: NodeCache;
  private logsCache: NodeCache;

  constructor() {
    // General cache - 5 minute TTL
    this.cache = new NodeCache({ stdTTL: 300 });

    // Metrics cache - 30 second TTL
    this.metricsCache = new NodeCache({ stdTTL: 30 });

    // Logs cache - 2 minute TTL
    this.logsCache = new NodeCache({ stdTTL: 120 });
  }

  async get<T>(key: string): Promise<T | null> {
    return this.cache.get<T>(key) || null;
  }

  async set<T>(key: string, value: T, ttl?: number): Promise<void> {
    this.cache.set(key, value, ttl);
  }

  async getMetrics<T>(key: string): Promise<T | null> {
    return this.metricsCache.get<T>(key) || null;
  }

  async setMetrics<T>(key: string, value: T): Promise<void> {
    this.metricsCache.set(key, value);
  }

  async getLogs<T>(key: string): Promise<T | null> {
    return this.logsCache.get<T>(key) || null;
  }

  async setLogs<T>(key: string, value: T): Promise<void> {
    this.logsCache.set(key, value);
  }

  invalidate(pattern: string): void {
    const keys = this.cache.keys().filter((key) => key.includes(pattern));
    this.cache.del(keys);
  }
}
```

## Testing Strategy

### Frontend Testing

```typescript
// packages/dashboard/src/components/__tests__/SystemStatusWidget.test.tsx
import React from 'react';
import { render, screen, waitFor } from '@testing-library/react';
import { SystemStatusWidget } from '../widgets/SystemStatusWidget';

// Mock the real-time data hook
jest.mock('../../hooks/useRealTimeData', () => ({
  useRealTimeData: jest.fn()
}));

import { useRealTimeData } from '../../hooks/useRealTimeData';

describe('SystemStatusWidget', () => {
  beforeEach(() => {
    jest.clearAllMocks();
  });

  it('displays loading state', () => {
    (useRealTimeData as jest.Mock).mockReturnValue({
      data: null,
      loading: true,
      error: null,
      lastUpdate: null
    });

    render(<SystemStatusWidget config={{}} />);
    expect(screen.getByText('Loading system status...')).toBeInTheDocument();
  });

  it('displays system status when data is available', async () => {
    const mockData = {
      overallStatus: 'healthy',
      services: [
        { name: 'orchestrator', status: 'healthy' },
        { name: 'workers', status: 'healthy' }
      ],
      uptime: '2d 5h 30m',
      activeWorkflows: 3,
      cpuUsage: 45.2,
      memoryUsage: 67.8
    };

    (useRealTimeData as jest.Mock).mockReturnValue({
      data: mockData,
      loading: false,
      error: null,
      lastUpdate: new Date()
    });

    render(<SystemStatusWidget config={{}} />);

    await waitFor(() => {
      expect(screen.getByText('HEALTHY')).toBeInTheDocument();
      expect(screen.getByText('orchestrator')).toBeInTheDocument();
      expect(screen.getByText('2d 5h 30m')).toBeInTheDocument();
      expect(screen.getByText('3')).toBeInTheDocument();
    });
  });

  it('displays error state', () => {
    (useRealTimeData as jest.Mock).mockReturnValue({
      data: null,
      loading: false,
      error: 'Failed to fetch data',
      lastUpdate: null
    });

    render(<SystemStatusWidget config={{}} />);
    expect(screen.getByText('Error: Failed to fetch data')).toBeInTheDocument();
  });
});
```

### Backend Testing

```typescript
// packages/dashboard/src/services/__tests__/dashboard.service.test.ts
import { DashboardService } from '../dashboard.service';
import { PrometheusService } from '@tamma/observability';
import { EventStore } from '@tamma/events';

describe('DashboardService', () => {
  let dashboardService: DashboardService;
  let mockPrometheusService: jest.Mocked<PrometheusService>;
  let mockEventStore: jest.Mocked<EventStore>;

  beforeEach(() => {
    mockPrometheusService = {
      query: jest.fn(),
      subscribe: jest.fn(),
    } as any;

    mockEventStore = {
      queryEvents: jest.fn(),
      subscribe: jest.fn(),
    } as any;

    dashboardService = new DashboardService(mockPrometheusService, mockEventStore, {} as any);
  });

  describe('getSystemStatus', () => {
    it('returns healthy status when all services are up', async () => {
      mockPrometheusService.query.mockImplementation((query: string) => {
        if (query.includes('up{')) {
          return Promise.resolve({
            data: {
              result: [{ value: ['1', '1'] }],
            },
          });
        }
        return Promise.resolve({ data: { result: [] } });
      });

      mockEventStore.queryEvents.mockResolvedValue({
        events: [
          {
            type: 'SYSTEM.STARTED',
            timestamp: new Date(Date.now() - 86400000).toISOString(),
          },
        ],
      });

      const status = await dashboardService.getSystemStatus();

      expect(status.overallStatus).toBe('healthy');
      expect(status.services).toHaveLength(4);
      expect(status.services.every((s) => s.status === 'healthy')).toBe(true);
    });

    it('returns degraded status when some services are down', async () => {
      mockPrometheusService.query.mockImplementation((query: string) => {
        if (query.includes('orchestrator')) {
          return Promise.resolve({
            data: { result: [] }, // Service down
          });
        }
        return Promise.resolve({
          data: { result: [{ value: ['1', '1'] }] },
        });
      });

      mockEventStore.queryEvents.mockResolvedValue({
        events: [
          {
            type: 'SYSTEM.STARTED',
            timestamp: new Date().toISOString(),
          },
        ],
      });

      const status = await dashboardService.getSystemStatus();

      expect(status.overallStatus).toBe('degraded');
      expect(status.services.find((s) => s.name === 'orchestrator')?.status).toBe('unhealthy');
    });
  });
});
```

### Integration Testing

```typescript
// packages/dashboard/src/__tests__/dashboard.integration.test.ts
import request from 'supertest';
import { FastifyInstance } from 'fastify';
import { buildApp } from '../app';

describe('Dashboard Integration', () => {
  let app: FastifyInstance;

  beforeAll(async () => {
    app = await buildApp();
  });

  afterAll(async () => {
    await app.close();
  });

  describe('GET /api/dashboard/system-status', () => {
    it('requires authentication', async () => {
      const response = await request(app.server).post('/api/dashboard/system-status').expect(401);

      expect(response.body.error).toContain('Unauthorized');
    });

    it('returns system status with valid token', async () => {
      const token = generateTestToken({ role: 'admin' });

      const response = await request(app.server)
        .post('/api/dashboard/system-status')
        .set('Authorization', `Bearer ${token}`)
        .expect(200);

      expect(response.body).toHaveProperty('overallStatus');
      expect(response.body).toHaveProperty('services');
      expect(response.body).toHaveProperty('uptime');
      expect(response.body).toHaveProperty('activeWorkflows');
    });
  });

  describe('WebSocket connection', () => {
    it('establishes WebSocket connection with authentication', async () => {
      const token = generateTestToken({ role: 'operator' });
      const ws = new WebSocket(`ws://localhost:3000/api/dashboard/ws?token=${token}`);

      await new Promise((resolve, reject) => {
        ws.onopen = resolve;
        ws.onerror = reject;

        setTimeout(() => reject(new Error('Connection timeout')), 5000);
      });

      ws.close();
    });
  });
});
```

## Performance Requirements

### Frontend Performance

- **Initial Load**: Dashboard fully interactive within 3 seconds
- **Widget Rendering**: Individual widgets render within 500ms
- **Data Updates**: Real-time updates processed within 100ms
- **Memory Usage**: Dashboard memory usage under 100MB
- **Bundle Size**: JavaScript bundle under 2MB (gzipped)

### Backend Performance

- **API Response Time**: 95th percentile under 500ms
- **WebSocket Latency**: Message delivery under 50ms
- **Concurrent Users**: Support 100 concurrent dashboard users
- **Data Freshness**: Metrics data no older than 30 seconds
- **Cache Hit Rate**: 80%+ cache hit rate for frequently accessed data

### Resource Utilization

- **CPU Usage**: Dashboard service under 50% CPU load
- **Memory Usage**: Under 512MB RAM for dashboard service
- **Network Bandwidth**: Under 1MB/s per active user
- **Database Load**: Minimal impact on primary database performance

## Security Considerations

### Authentication & Authorization

- **JWT Tokens**: Short-lived tokens (15 minutes) with refresh tokens
- **Role-based Access**: Different dashboard views based on user roles
- **Session Management**: Secure session handling with proper logout
- **API Rate Limiting**: Prevent abuse of dashboard APIs

### Data Protection

- **Sensitive Data**: No sensitive information displayed in dashboard
- **Input Validation**: All user inputs validated and sanitized
- **XSS Prevention**: Proper output encoding and CSP headers
- **CSRF Protection**: Anti-CSRF tokens for state-changing operations

### Network Security

- **HTTPS Only**: All dashboard communication over HTTPS
- **WebSocket Security**: Secure WebSocket connections (WSS)
- **CORS Configuration**: Proper CORS settings for API access
- **API Authentication**: All API endpoints require authentication

## Monitoring & Alerting

### Dashboard Health Monitoring

```typescript
// Dashboard-specific metrics
const dashboardMetrics = {
  // User engagement
  dashboardActiveUsers: new Gauge({
    name: 'dashboard_active_users_total',
    help: 'Number of active dashboard users',
  }),

  dashboardPageViews: new Counter({
    name: 'dashboard_page_views_total',
    help: 'Total number of dashboard page views',
  }),

  // Performance metrics
  dashboardLoadTime: new Histogram({
    name: 'dashboard_load_time_seconds',
    help: 'Dashboard load time in seconds',
    buckets: [0.1, 0.5, 1, 2, 5, 10],
  }),

  // WebSocket metrics
  dashboardWebSocketConnections: new Gauge({
    name: 'dashboard_websocket_connections_total',
    help: 'Number of active WebSocket connections',
  }),

  // Error metrics
  dashboardErrors: new Counter({
    name: 'dashboard_errors_total',
    help: 'Total number of dashboard errors',
    labelNames: ['type', 'component'],
  }),
};
```

### Automated Health Checks

```typescript
// packages/dashboard/src/health/health-checker.ts
export class DashboardHealthChecker {
  async performHealthCheck(): Promise<HealthCheckResult> {
    const checks = await Promise.allSettled([
      this.checkDatabaseConnection(),
      this.checkPrometheusConnection(),
      this.checkEventStoreConnection(),
      this.checkWebSocketServer(),
      this.checkMemoryUsage(),
      this.checkDiskSpace(),
    ]);

    const results = checks.map((check, index) => ({
      name: this.getCheckName(index),
      status: check.status === 'fulfilled' ? 'healthy' : 'unhealthy',
      message: check.status === 'fulfilled' ? 'OK' : check.reason?.message,
    }));

    const overallStatus = results.every((r) => r.status === 'healthy') ? 'healthy' : 'unhealthy';

    return {
      status: overallStatus,
      checks: results,
      timestamp: new Date().toISOString(),
    };
  }

  private async checkDatabaseConnection(): Promise<void> {
    // Check database connectivity
  }

  private async checkPrometheusConnection(): Promise<void> {
    // Check Prometheus connectivity
  }

  private async checkEventStoreConnection(): Promise<void> {
    // Check event store connectivity
  }

  private async checkWebSocketServer(): Promise<void> {
    // Check WebSocket server health
  }

  private async checkMemoryUsage(): Promise<void> {
    const usage = process.memoryUsage();
    const threshold = 512 * 1024 * 1024; // 512MB

    if (usage.heapUsed > threshold) {
      throw new Error(`Memory usage too high: ${usage.heapUsed} bytes`);
    }
  }

  private async checkDiskSpace(): Promise<void> {
    // Check available disk space
  }
}
```

## Deployment Configuration

### Docker Configuration

```dockerfile
# packages/dashboard/Dockerfile
FROM node:22-alpine AS builder

WORKDIR /app
COPY package*.json ./
RUN npm ci --only=production

COPY . .
RUN npm run build

FROM node:22-alpine AS runner

WORKDIR /app
COPY --from=builder /app/dist ./dist
COPY --from=builder /app/node_modules ./node_modules
COPY --from=builder /app/package.json ./package.json

EXPOSE 3000

ENV NODE_ENV=production
ENV PORT=3000

USER node

CMD ["node", "dist/index.js"]
```

### Kubernetes Deployment

```yaml
# k8s/dashboard-deployment.yaml
apiVersion: apps/v1
kind: Deployment
metadata:
  name: tamma-dashboard
  labels:
    app: tamma-dashboard
spec:
  replicas: 2
  selector:
    matchLabels:
      app: tamma-dashboard
  template:
    metadata:
      labels:
        app: tamma-dashboard
    spec:
      containers:
        - name: dashboard
          image: tamma/dashboard:latest
          ports:
            - containerPort: 3000
          env:
            - name: NODE_ENV
              value: 'production'
            - name: PORT
              value: '3000'
            - name: DASHBOARD_JWT_SECRET
              valueFrom:
                secretKeyRef:
                  name: tamma-secrets
                  key: dashboard-jwt-secret
            - name: PROMETHEUS_URL
              value: 'http://prometheus:9090'
            - name: DATABASE_URL
              valueFrom:
                secretKeyRef:
                  name: tamma-secrets
                  key: database-url
          resources:
            requests:
              memory: '256Mi'
              cpu: '250m'
            limits:
              memory: '512Mi'
              cpu: '500m'
          livenessProbe:
            httpGet:
              path: /health
              port: 3000
            initialDelaySeconds: 30
            periodSeconds: 10
          readinessProbe:
            httpGet:
              path: /ready
              port: 3000
            initialDelaySeconds: 5
            periodSeconds: 5
---
apiVersion: v1
kind: Service
metadata:
  name: tamma-dashboard-service
spec:
  selector:
    app: tamma-dashboard
  ports:
    - protocol: TCP
      port: 80
      targetPort: 3000
  type: LoadBalancer
```

## Success Metrics

### User Experience Metrics

- **Dashboard Load Time**: 95th percentile under 3 seconds
- **User Engagement**: Average session duration over 5 minutes
- **Error Rate**: Dashboard errors under 0.1% of requests
- **Adoption Rate**: 80%+ of team members using dashboard weekly

### System Performance Metrics

- **API Response Time**: 95th percentile under 500ms
- **WebSocket Latency**: Message delivery under 50ms
- **Resource Efficiency**: CPU usage under 50%, memory under 512MB
- **Availability**: 99.9% uptime for dashboard service

### Business Impact Metrics

- **Issue Detection Time**: 50% reduction in time to detect system issues
- **Resolution Time**: 30% reduction in issue resolution time
- **System Visibility**: 100% visibility into system health and performance
- **Operational Efficiency**: 40% reduction in manual monitoring effort

## Rollout Plan

### Phase 1: Core Dashboard (Week 1-2)

1. **Basic Dashboard Framework**
   - React application setup with routing
   - Authentication system implementation
   - Basic widget system and layout engine
   - WebSocket connection for real-time updates

2. **Essential Widgets**
   - System status widget
   - Basic metrics charts
   - Alert panel
   - Service health overview

### Phase 2: Advanced Features (Week 3-4)

1. **Enhanced Visualizations**
   - Time-series charts with multiple metrics
   - Heat maps for system activity
   - Service dependency graph
   - Performance trend analysis

2. **Interactive Features**
   - Drill-down capabilities
   - Log correlation
   - Alert acknowledgment
   - Data export functionality

### Phase 3: Production Readiness (Week 5-6)

1. **Performance Optimization**
   - Caching implementation
   - Bundle size optimization
   - Database query optimization
   - WebSocket connection pooling

2. **Security & Compliance**
   - Security audit and penetration testing
   - Role-based access control refinement
   - Audit logging for dashboard access
   - Compliance validation

### Phase 4: Monitoring & Enhancement (Week 7-8)

1. **Dashboard Monitoring**
   - Performance metrics collection
   - Error tracking and alerting
   - User analytics and usage tracking
   - Automated health checks

2. **User Feedback Integration**
   - User feedback collection system
   - A/B testing for new features
   - Usage pattern analysis
   - Continuous improvement based on feedback

## Dependencies

### Internal Dependencies

- **Epic 5.1**: Structured Logging Implementation (provides log data)
- **Epic 5.2**: Metrics Collection Infrastructure (provides metrics data)
- **Epic 4**: Event Sourcing & Audit Trail (provides event data)
- **Epic 1**: Multi-Provider AI Abstraction (provides AI provider metrics)
- **Epic 2**: Autonomous Development Workflow (provides workflow metrics)

### External Dependencies

- **React 18+**: Frontend framework for dashboard UI
- **TypeScript**: Type-safe JavaScript development
- **Fastify**: Backend API framework
- **WebSocket**: Real-time communication protocol
- **Chart.js/Recharts**: Data visualization library
- **Node Cache**: In-memory caching solution
- **JWT**: Authentication token management

### Infrastructure Dependencies

- **Prometheus**: Metrics collection and storage
- **PostgreSQL**: Event store and configuration data
- **Kubernetes**: Container orchestration platform
- **Nginx/Ingress**: Load balancing and SSL termination
- **Redis**: Session storage and caching (optional)

## Risks and Mitigations

### Technical Risks

- **Performance Bottlenecks**: Real-time updates causing high CPU/memory usage
  - _Mitigation_: Implement efficient caching, use WebSocket connection pooling, optimize data queries
- **Scalability Issues**: Dashboard not handling concurrent users
  - _Mitigation_: Horizontal scaling, load testing, performance optimization
- **Data Consistency**: Inconsistent data across different widgets
  - _Mitigation_: Implement data synchronization, use consistent time windows, cache invalidation strategies

### User Experience Risks

- **Complex Interface**: Dashboard too complex for non-technical users
  - _Mitigation_: User-centered design, role-based views, progressive disclosure of information
- **Information Overload**: Too much data causing decision paralysis
  - _Mitigation_: Smart defaults, customizable views, alert prioritization
- **Slow Performance**: Dashboard loading slowly causing user frustration
  - _Mitigation_: Performance optimization, lazy loading, progressive rendering

### Security Risks

- **Unauthorized Access**: Sensitive system information exposed
  - _Mitigation_: Strong authentication, role-based access control, audit logging
- **Data Leakage**: System metrics exposing sensitive information
  - _Mitigation_: Data sanitization, access controls, encryption in transit
- **Session Hijacking**: WebSocket sessions being hijacked
  - _Mitigation_: Secure WebSocket connections, token validation, session timeout

---

**Story Status**: Ready for Development  
**Implementation Priority**: Optional (Post-MVP Enhancement)  
**Target Completion**: Sprint 6 (Post-MVP)  
**Dependencies**: Epic 5.1, Epic 5.2, Epic 4 (Event Sourcing)
