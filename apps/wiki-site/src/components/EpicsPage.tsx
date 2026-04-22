import { useEffect, useState } from 'react';
import { Link } from 'react-router';

interface ManifestEntry {
  path: string;
  title: string;
  section: string;
}

interface EpicCard {
  number: string;
  name: string;
  slug: string;
  description: string;
  status: 'done' | 'in-progress' | 'drafted';
  storyCount: number;
}

// Map epic numbers to descriptions (short summaries for each epic)
const epicDescriptions: Record<string, string> = {
  '1': 'Multi-provider AI abstraction, multi-platform Git integration, and hybrid architecture',
  '1.5': 'Docker packaging, CI/CD pipelines, npm publishing, binary releases',
  '2': '14-step autonomous pipeline from issue assignment to merged PR',
  '3': 'Build verification, test execution, security scanning, and code quality gates',
  '4': 'DCB event sourcing with JSONB tags for complete audit trail',
  '5': 'Structured logging, metrics, real-time dashboards, and alerting',
  '6': 'RAG pipeline, vector database, MCP integration, and cost monitoring',
  '7': '28-state mentorship workflow with 12+ ELSA activities',
  '8': 'Docker, CI/CD, npm, Homebrew, and cross-platform binary distribution',
  '9': 'Provider chains, circuit breakers, agent diagnostics, and config management',
  '10': 'ELSA-based workflow engine replacing hardcoded orchestration logic',
  '11': 'Content sanitization, tool validation, URL filtering, and action gating',
  '12': 'Agentic tool execution loop with context compaction and streaming',
  '13': 'TDD cycle, CI retry, and debugging sub-workflows',
  '14': 'Custom Blazor WASM studio with Tamma branding',
  '15': 'OpenSearch integration for centralized log aggregation',
  '16': 'OAuth SSO, RBAC, admin dashboard, and user management',
  '17': 'Tenant isolation, data partitioning, and resource quotas',
  '18': 'User registration, profile management, and API key provisioning',
  '19': 'GitHub App installation, webhook dispatch, and agent routing',
  '20': 'Stripe integration, usage metering, plan management, and invoicing',
  '21': 'Marketing site, user dashboard, onboarding, and analytics',
  '22': 'Standalone CLI mode preservation alongside SaaS deployment',
  '23': 'System health monitoring, performance dashboards, and alerting',
  '24': 'WebRTC-based voice interface for orchestrator interaction',
  '25': 'Documentation site with synced wiki content and designed pages',
  '11-14': 'Consolidated ELSA hardening across security, tool loop, decomposition, and studio',
};

// Epic status classification
// Updated 2026-04-21 — Epic 19 complete; Epic 12 partial; Epic 22 absorbed by 19;
// Epic 27/28/29/30/31/33 newly scoped (drafted); Epic 33 deferred.
const completedEpics = new Set([
  '7', '8', '9', '11', '13', '14', '15', '16', '19', '25',
]);
const inProgressEpics = new Set(['1', '1.5', '2', '3', '4', '5', '6', '12', '17', '18', '21', '22', '23', '24', '26']);

function parseEpicNumber(title: string): string {
  // "Epic 1: Foundation" -> "1"
  // "Epic 1.5: Infrastructure" -> "1.5"
  // "Epics 11-14: ELSA" -> "11-14"
  const match = title.match(/Epics?\s+([\d.]+(?:-[\d.]+)?)/);
  return match ? match[1] : '';
}

function parseEpicName(title: string): string {
  // "Epic 1: Foundation & Core Infrastructure" -> "Foundation & Core Infrastructure"
  const match = title.match(/:\s*(.+)/);
  return match ? match[1].replace(/\s*--\s*/g, ' \u2014 ') : title;
}

function getEpicSectionKey(epicNumber: string): string {
  // "1" -> "Epic 1", "1.5" -> "Epic 1-5", "11-14" -> "Epic 11-14" (but this one is not in stories)
  if (epicNumber === '1.5') return 'Epic 1-5';
  return `Epic ${epicNumber}`;
}

function numericSort(a: string, b: string): number {
  const na = parseFloat(a.replace('-', '.'));
  const nb = parseFloat(b.replace('-', '.'));
  return na - nb;
}

const statusConfig = {
  done: {
    label: 'Completed',
    dotClass: 'bg-emerald-500',
    badgeClass: 'bg-emerald-500/10 text-emerald-400 border-emerald-500/20',
  },
  'in-progress': {
    label: 'In Progress',
    dotClass: 'bg-amber-500',
    badgeClass: 'bg-amber-500/10 text-amber-400 border-amber-500/20',
  },
  drafted: {
    label: 'Planned',
    dotClass: 'bg-zinc-600',
    badgeClass: 'bg-zinc-500/10 text-zinc-400 border-zinc-500/20',
  },
};

export default function EpicsPage() {
  const [epics, setEpics] = useState<EpicCard[]>([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    document.title = 'Epics \u2014 Tamma Docs';
    fetch('/content/manifest.json')
      .then((r) => r.json())
      .then((data: ManifestEntry[]) => {
        // Count stories per epic section
        const storyCounts = new Map<string, number>();
        for (const entry of data) {
          if (entry.section.startsWith('Epic ')) {
            const current = storyCounts.get(entry.section) || 0;
            storyCounts.set(entry.section, current + 1);
          }
        }

        // Build epic cards from Epics section
        // Filter out non-epic pages (like the audit file) — must have a parsable epic number.
        const epicEntries = data.filter((e) => e.section === 'Epics' && parseEpicNumber(e.title));
        const cards: EpicCard[] = epicEntries.map((entry) => {
          const num = parseEpicNumber(entry.title);
          const name = parseEpicName(entry.title);
          const sectionKey = getEpicSectionKey(num);
          const storyCount = storyCounts.get(sectionKey) || 0;
          const status: 'done' | 'in-progress' | 'drafted' = completedEpics.has(num)
            ? 'done'
            : inProgressEpics.has(num)
              ? 'in-progress'
              : 'drafted';

          return {
            number: num,
            name,
            slug: entry.path,
            description: epicDescriptions[num] || name,
            status,
            storyCount,
          };
        });

        // Sort numerically
        cards.sort((a, b) => numericSort(a.number, b.number));
        setEpics(cards);
        setLoading(false);
      })
      .catch(() => setLoading(false));
  }, []);

  if (loading) {
    return (
      <div className="space-y-6">
        <div className="animate-pulse">
          <div className="h-8 bg-zinc-800/50 rounded w-48 mb-2" />
          <div className="h-4 bg-zinc-800/30 rounded w-96 mb-8" />
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
            {Array.from({ length: 6 }).map((_, i) => (
              <div key={i} className="h-36 bg-zinc-900/50 rounded-xl border border-zinc-800" />
            ))}
          </div>
        </div>
      </div>
    );
  }

  const grouped = {
    done: epics.filter((e) => e.status === 'done'),
    'in-progress': epics.filter((e) => e.status === 'in-progress'),
    drafted: epics.filter((e) => e.status === 'drafted'),
  };

  return (
    <div className="space-y-10">
      {/* Header */}
      <div>
        <div className="flex items-center gap-2 text-sm text-zinc-500 mb-4">
          <Link to="/" className="hover:text-zinc-300 transition-colors">Home</Link>
          <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
            <path strokeLinecap="round" strokeLinejoin="round" d="M9 5l7 7-7 7" />
          </svg>
          <span className="text-zinc-400">Epics</span>
        </div>
        <h1 className="text-3xl font-bold text-white tracking-tight">Epics</h1>
        <p className="mt-2 text-zinc-400 text-[15px] leading-relaxed max-w-2xl">
          {epics.length} epics spanning the complete Tamma platform, from foundation infrastructure to SaaS deployment.
        </p>
      </div>

      {/* Summary stats */}
      <div className="grid grid-cols-3 gap-3">
        {(['done', 'in-progress', 'drafted'] as const).map((status) => {
          const config = statusConfig[status];
          return (
            <div key={status} className="bg-zinc-900/50 border border-zinc-800 rounded-xl p-4 text-center">
              <div className="flex items-center justify-center gap-2 mb-1">
                <span className={`w-2 h-2 rounded-full ${config.dotClass}`} />
                <span className="text-2xl font-bold text-white">{grouped[status].length}</span>
              </div>
              <div className="text-xs text-zinc-500 uppercase tracking-wider">{config.label}</div>
            </div>
          );
        })}
      </div>

      {/* Completed Section */}
      {grouped.done.length > 0 && (
        <EpicSection title="Completed" status="done" epics={grouped.done} />
      )}

      {/* In Progress Section */}
      {grouped['in-progress'].length > 0 && (
        <EpicSection title="In Progress" status="in-progress" epics={grouped['in-progress']} />
      )}

      {/* Planned Section */}
      {grouped.drafted.length > 0 && (
        <EpicSection title="Planned" status="drafted" epics={grouped.drafted} />
      )}
    </div>
  );
}

function EpicSection({
  title,
  status,
  epics,
}: {
  title: string;
  status: 'done' | 'in-progress' | 'drafted';
  epics: EpicCard[];
}) {
  const config = statusConfig[status];

  return (
    <div>
      <div className="flex items-center gap-2 mb-4">
        <span className={`w-2 h-2 rounded-full ${config.dotClass}`} />
        <h2 className="text-sm font-semibold text-zinc-500 uppercase tracking-wider">
          {title} ({epics.length})
        </h2>
      </div>
      <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
        {epics.map((epic) => (
          <EpicCardComponent key={epic.slug} epic={epic} />
        ))}
      </div>
    </div>
  );
}

function EpicCardComponent({ epic }: { epic: EpicCard }) {
  const config = statusConfig[epic.status];

  return (
    <Link
      to={epic.slug}
      className="group block bg-zinc-900/50 border border-zinc-800 rounded-xl p-5 hover:border-zinc-700 hover:bg-zinc-900/80 transition-all"
    >
      <div className="flex items-start justify-between gap-3 mb-3">
        <div className="flex items-center gap-2.5">
          <span className="text-xs font-mono text-zinc-600 bg-zinc-800/60 px-2 py-0.5 rounded">
            {epic.number}
          </span>
          <h3 className="text-[15px] font-medium text-zinc-200 group-hover:text-white transition-colors leading-snug">
            {epic.name}
          </h3>
        </div>
      </div>

      <p className="text-[13px] text-zinc-500 leading-relaxed mb-4 line-clamp-2">
        {epic.description}
      </p>

      <div className="flex items-center justify-between">
        <span className={`inline-flex items-center gap-1.5 text-[11px] font-medium px-2 py-0.5 rounded-full border ${config.badgeClass}`}>
          <span className={`w-1.5 h-1.5 rounded-full ${config.dotClass}`} />
          {config.label}
        </span>
        {epic.storyCount > 0 && (
          <span className="text-[11px] text-zinc-600">
            {epic.storyCount} {epic.storyCount === 1 ? 'story' : 'stories'}
          </span>
        )}
      </div>
    </Link>
  );
}
