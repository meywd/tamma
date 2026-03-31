import { Link } from 'react-router';

const quickLinks = [
  { to: '/roadmap', title: 'Roadmap', desc: 'All 26 epics with timeline and status', icon: '🗺️' },
  { to: '/architecture', title: 'Architecture', desc: 'System design and tech stack', icon: '🏗️' },
  { to: '/epics', title: 'Epics', desc: '26 epics organized by phase', icon: '📋' },
  { to: '/workflows', title: 'Workflows', desc: '20 ELSA workflows with flow diagrams', icon: '⚡' },
  { to: '/stories', title: 'Stories', desc: '220+ stories across all epics', icon: '📖' },
  { to: '/contributing', title: 'Contributing', desc: 'How to contribute to Tamma', icon: '🤝' },
];

const stats = [
  { label: 'Epics', value: '26' },
  { label: 'Stories Done', value: '115' },
  { label: 'In Progress', value: '20' },
  { label: 'Workflows', value: '20' },
];

const completedEpics = [
  { num: 6, name: 'Context & Knowledge', desc: 'RAG, vector DB, MCP, cost monitoring' },
  { num: 7, name: 'Mentorship', desc: '28-state workflow, 12+ ELSA activities' },
  { num: 8, name: 'Distribution', desc: 'Docker, CI/CD, npm, Homebrew, binaries' },
  { num: 9, name: 'Agent Orchestration', desc: 'Provider chains, circuit breakers, diagnostics' },
  { num: 11, name: 'Security', desc: 'Content sanitization, tool validation, action gating' },
  { num: 12, name: 'Tool Loop', desc: 'Agentic tool execution, context compaction' },
  { num: 13, name: 'Workflow Decomposition', desc: 'TDD/CI retry sub-workflows' },
  { num: 14, name: 'ELSA Studio', desc: 'Custom Blazor WASM studio with branding' },
  { num: 15, name: 'Log Aggregation', desc: 'OpenSearch integration' },
  { num: 16, name: 'Auth & Admin', desc: 'OAuth SSO, RBAC, admin dashboard' },
];

export default function HomePage() {
  return (
    <div className="space-y-12">
      {/* Hero */}
      <div>
        <h1 className="text-3xl font-bold text-white tracking-tight">Tamma Documentation</h1>
        <p className="mt-3 text-zinc-400 text-lg leading-relaxed max-w-2xl">
          The autonomous development platform that maintains its own codebase.
          70%+ autonomous completion across 8+ AI providers and 7 Git platforms.
        </p>
      </div>

      {/* Stats */}
      <div className="grid grid-cols-2 sm:grid-cols-4 gap-4">
        {stats.map((s) => (
          <div key={s.label} className="bg-zinc-900/50 border border-zinc-800 rounded-xl p-4 text-center">
            <div className="text-2xl font-bold text-white">{s.value}</div>
            <div className="text-xs text-zinc-500 mt-1 uppercase tracking-wider">{s.label}</div>
          </div>
        ))}
      </div>

      {/* Quick Links */}
      <div>
        <h2 className="text-sm font-semibold text-zinc-500 uppercase tracking-wider mb-4">Quick Links</h2>
        <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-3">
          {quickLinks.map((link) => (
            <Link
              key={link.to}
              to={link.to}
              className="group flex items-start gap-3 p-4 bg-zinc-900/30 border border-zinc-800/60 rounded-xl hover:bg-zinc-800/40 hover:border-zinc-700 transition-all"
            >
              <span className="text-xl mt-0.5">{link.icon}</span>
              <div>
                <div className="text-sm font-medium text-zinc-200 group-hover:text-white transition-colors">
                  {link.title}
                </div>
                <div className="text-xs text-zinc-500 mt-0.5">{link.desc}</div>
              </div>
            </Link>
          ))}
        </div>
      </div>

      {/* Key Features */}
      <div>
        <h2 className="text-sm font-semibold text-zinc-500 uppercase tracking-wider mb-4">Key Features</h2>
        <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
          {[
            ['Autonomous Dev Loop', '14-step pipeline from issue to merged PR'],
            ['Multi-Provider AI', '8+ providers with fallback chains and circuit breakers'],
            ['Multi-Platform Git', 'GitHub, GitLab, Gitea, Forgejo, Bitbucket, Azure DevOps'],
            ['ELSA Workflow Engine', 'Visual, composable, pausable/resumable workflows'],
            ['Defense-in-Depth Security', 'Content sanitization, URL validation, action gating'],
            ['Self-Maintenance', 'Tamma maintains its own codebase (MVP goal)'],
          ].map(([title, desc]) => (
            <div key={title} className="flex items-start gap-3 p-3 rounded-lg">
              <div className="w-1.5 h-1.5 rounded-full bg-blue-500 mt-2 shrink-0" />
              <div>
                <div className="text-sm font-medium text-zinc-300">{title}</div>
                <div className="text-xs text-zinc-500 mt-0.5">{desc}</div>
              </div>
            </div>
          ))}
        </div>
      </div>

      {/* Completed Epics */}
      <div>
        <h2 className="text-sm font-semibold text-zinc-500 uppercase tracking-wider mb-4">
          Completed Epics ({completedEpics.length})
        </h2>
        <div className="space-y-1">
          {completedEpics.map((epic) => (
            <Link
              key={epic.num}
              to={`/epics/${epic.num}-${epic.name.toLowerCase().replace(/\s+/g, '-').replace(/[&]/g, '')}`}
              className="flex items-center gap-3 px-3 py-2.5 rounded-lg hover:bg-zinc-800/40 transition-colors group"
            >
              <span className="text-xs font-mono text-zinc-600 w-6 text-right">{epic.num}</span>
              <span className="w-2 h-2 rounded-full bg-emerald-500/80" />
              <span className="text-sm text-zinc-300 group-hover:text-white transition-colors">{epic.name}</span>
              <span className="text-xs text-zinc-600 ml-auto hidden sm:block">{epic.desc}</span>
            </Link>
          ))}
        </div>
      </div>

      {/* Near Complete */}
      <div>
        <h2 className="text-sm font-semibold text-zinc-500 uppercase tracking-wider mb-4">Near Complete</h2>
        <div className="space-y-1">
          {[
            { to: '/epics/1-foundation', name: 'Epic 1: Foundation', done: '11/15', remaining: '2 in progress, 3 ready' },
            { to: '/epics/1-5-infrastructure', name: 'Epic 1.5: Infrastructure', done: '9/10', remaining: 'K8s in progress' },
            { to: '/epics/2-autonomous-loop', name: 'Epic 2: Autonomous Loop', done: '13/16', remaining: '3 ready for dev' },
          ].map((e) => (
            <Link
              key={e.to}
              to={e.to}
              className="flex items-center gap-3 px-3 py-2.5 rounded-lg hover:bg-zinc-800/40 transition-colors group"
            >
              <span className="w-2 h-2 rounded-full bg-amber-500/80" />
              <span className="text-sm text-zinc-300 group-hover:text-white transition-colors">{e.name}</span>
              <span className="text-xs font-mono text-zinc-500 ml-auto">{e.done}</span>
              <span className="text-xs text-zinc-600 hidden sm:block">{e.remaining}</span>
            </Link>
          ))}
        </div>
      </div>

      {/* Footer */}
      <div className="border-t border-zinc-800 pt-6 text-xs text-zinc-600">
        Last updated: 2026-03-31 · <a href="https://github.com/meywd/tamma" className="text-zinc-500 hover:text-zinc-400">GitHub</a>
      </div>
    </div>
  );
}
