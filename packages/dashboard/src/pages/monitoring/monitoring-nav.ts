/**
 * Monitoring navigation manifest (Story 23-12).
 *
 * Single source of truth for the sidebar's "Monitoring" group, the overview
 * landing page, and the route table. Order matches AC1 in the story.
 */

export interface MonitoringNavItem {
  /** Route path. */
  to: string;
  /** Sidebar / card label. */
  label: string;
  /** One-line description shown on the overview page. */
  description: string;
  /** The Epic-23 story that fills this page in. */
  story: string;
  /** localStorage key for the page's auto-refresh preference. */
  storageKey: string;
}

export const MONITORING_NAV_ITEMS: readonly MonitoringNavItem[] = [
  {
    to: '/monitoring/health',
    label: 'System Health',
    description: 'Service health, dependency graph, error and request rates.',
    story: 'Story 23-1',
    storageKey: 'tamma.monitoring.health.autoRefresh',
  },
  {
    to: '/monitoring/agents',
    label: 'Agent Monitor',
    description: 'Agent roles, provider chains, cost and live status.',
    story: 'Story 23-2',
    storageKey: 'tamma.monitoring.agents.autoRefresh',
  },
  {
    to: '/monitoring/events',
    label: 'Event Explorer',
    description: 'Search, filter, timeline, replay and export event-store events.',
    story: 'Story 23-3',
    storageKey: 'tamma.monitoring.events.autoRefresh',
  },
  {
    to: '/monitoring/workflows',
    label: 'Workflows',
    description: 'Active workflows, Gantt timeline and queue depth.',
    story: 'Story 23-5',
    storageKey: 'tamma.monitoring.workflows.autoRefresh',
  },
  {
    to: '/monitoring/providers',
    label: 'Providers',
    description: 'Latency histograms, token analytics and error classification.',
    story: 'Story 23-6',
    storageKey: 'tamma.monitoring.providers.autoRefresh',
  },
  {
    to: '/monitoring/logs',
    label: 'Logs',
    description: 'Live tail, full-text search, saved searches and alerts.',
    story: 'Story 23-7',
    storageKey: 'tamma.monitoring.logs.autoRefresh',
  },
  {
    to: '/monitoring/infrastructure',
    label: 'Infrastructure',
    description: 'PostgreSQL, RabbitMQ, ChromaDB, OpenSearch and Docker health.',
    story: 'Story 23-8',
    storageKey: 'tamma.monitoring.infrastructure.autoRefresh',
  },
  {
    to: '/monitoring/knowledge-base',
    label: 'Knowledge Base',
    description: 'Vector DB, embeddings, RAG health and MCP connections.',
    story: 'Story 23-9',
    storageKey: 'tamma.monitoring.knowledge-base.autoRefresh',
  },
  {
    to: '/monitoring/config',
    label: 'Config Audit',
    description: 'Config sources, validation, diff and change history.',
    story: 'Story 23-4',
    storageKey: 'tamma.monitoring.config.autoRefresh',
  },
  {
    to: '/monitoring/security',
    label: 'Security Audit',
    description: 'Login attempts, sessions, permissions and rate limits.',
    story: 'Story 23-10',
    storageKey: 'tamma.monitoring.security.autoRefresh',
  },
];

/** Look up a monitoring nav item by its route path. Throws if unknown. */
export function getMonitoringNavItem(to: string): MonitoringNavItem {
  const item = MONITORING_NAV_ITEMS.find((i) => i.to === to);
  if (!item) throw new Error(`Unknown monitoring nav item: ${to}`);
  return item;
}
