import { useEffect, useState } from 'react';
import { Link } from 'react-router';

interface ManifestEntry {
  path: string;
  title: string;
  section: string;
}

interface WorkflowCard {
  name: string;
  slug: string;
  description: string;
  icon: string;
}

// Brief descriptions for each workflow
const workflowMeta: Record<string, { description: string; icon: string }> = {
  'adl-orchestrator': {
    description: 'Top-level orchestration workflow that coordinates the entire autonomous development loop from issue to merge.',
    icon: 'M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15',
  },
  'blocker-diagnosis': {
    description: 'Diagnoses and resolves blockers encountered during autonomous development, including build failures and test errors.',
    icon: 'M12 9v2m0 4h.01m-6.938 4h13.856c1.54 0 2.502-1.667 1.732-2.5L13.732 4c-.77-.833-1.964-.833-2.732 0L4.082 16.5c-.77.833.192 2.5 1.732 2.5z',
  },
  'code-review': {
    description: 'Automated code review workflow that checks style, correctness, security, and provides structured feedback.',
    icon: 'M9 5H7a2 2 0 00-2 2v12a2 2 0 002 2h10a2 2 0 002-2V7a2 2 0 00-2-2h-2M9 5a2 2 0 002 2h2a2 2 0 002-2M9 5a2 2 0 012-2h2a2 2 0 012 2m-6 9l2 2 4-4',
  },
  'context-gathering': {
    description: 'Gathers project context including codebase structure, dependencies, and relevant documentation for issue analysis.',
    icon: 'M19 11H5m14 0a2 2 0 012 2v6a2 2 0 01-2 2H5a2 2 0 01-2-2v-6a2 2 0 012-2m14 0V9a2 2 0 00-2-2M5 11V9a2 2 0 012-2m0 0V5a2 2 0 012-2h6a2 2 0 012 2v2M7 7h10',
  },
  'debugging': {
    description: 'Systematic debugging workflow that analyzes failures, forms hypotheses, and applies targeted fixes.',
    icon: 'M10 20l4-16m4 4l4 4-4 4M6 16l-4-4 4-4',
  },
  'llm-call': {
    description: 'Manages LLM API calls with provider selection, fallback chains, retry logic, and response streaming.',
    icon: 'M8 10h.01M12 10h.01M16 10h.01M9 16H5a2 2 0 01-2-2V6a2 2 0 012-2h14a2 2 0 012 2v8a2 2 0 01-2 2h-5l-5 5v-5z',
  },
  'mentorship': {
    description: '28-state mentorship workflow with 12+ activities for guided learning and autonomous skill development.',
    icon: 'M12 6.253v13m0-13C10.832 5.477 9.246 5 7.5 5S4.168 5.477 3 6.253v13C4.168 18.477 5.754 18 7.5 18s3.332.477 4.5 1.253m0-13C13.168 5.477 14.754 5 16.5 5c1.747 0 3.332.477 4.5 1.253v13C19.832 18.477 18.247 18 16.5 18c-1.746 0-3.332.477-4.5 1.253',
  },
  'single-issue-cycle': {
    description: 'End-to-end workflow for processing a single issue: analysis, planning, coding, testing, review, and PR creation.',
    icon: 'M13 10V3L4 14h7v7l9-11h-7z',
  },
  'tdd-cycle': {
    description: 'Test-driven development cycle: write failing tests, implement code to pass, refactor, and verify coverage.',
    icon: 'M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z',
  },
  'testing': {
    description: 'Comprehensive testing pipeline including unit tests, integration tests, security scans, and coverage analysis.',
    icon: 'M19.428 15.428a2 2 0 00-1.022-.547l-2.387-.477a6 6 0 00-3.86.517l-.318.158a6 6 0 01-3.86.517L6.05 15.21a2 2 0 00-1.806.547M8 4h8l-1 1v5.172a2 2 0 00.586 1.414l5 5c1.26 1.26.367 3.414-1.415 3.414H4.828c-1.782 0-2.674-2.154-1.414-3.414l5-5A2 2 0 009 10.172V5L8 4z',
  },
};

export default function WorkflowsPage() {
  const [workflows, setWorkflows] = useState<WorkflowCard[]>([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    document.title = 'Workflows \u2014 Tamma Docs';
    fetch('/content/manifest.json')
      .then((r) => r.json())
      .then((data: ManifestEntry[]) => {
        const workflowEntries = data.filter((e) => e.section === 'Workflows');
        const cards: WorkflowCard[] = workflowEntries.map((entry) => {
          const slug = entry.path.replace('/workflows/', '');
          const meta = workflowMeta[slug];
          const name = entry.title.replace(/^Workflow:\s*/, '');
          return {
            name,
            slug: entry.path,
            description: meta?.description || name,
            icon: meta?.icon || 'M13 10V3L4 14h7v7l9-11h-7z',
          };
        });

        // Sort alphabetically
        cards.sort((a, b) => a.name.localeCompare(b.name));
        setWorkflows(cards);
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
              <div key={i} className="h-32 bg-zinc-900/50 rounded-xl border border-zinc-800" />
            ))}
          </div>
        </div>
      </div>
    );
  }

  return (
    <div className="space-y-10">
      {/* Header */}
      <div>
        <div className="flex items-center gap-2 text-sm text-zinc-500 mb-4">
          <Link to="/" className="hover:text-zinc-300 transition-colors">Home</Link>
          <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
            <path strokeLinecap="round" strokeLinejoin="round" d="M9 5l7 7-7 7" />
          </svg>
          <span className="text-zinc-400">Workflows</span>
        </div>
        <h1 className="text-3xl font-bold text-white tracking-tight">Workflows</h1>
        <p className="mt-2 text-zinc-400 text-[15px] leading-relaxed max-w-2xl">
          {workflows.length} ELSA-based workflows powering Tamma's autonomous development engine.
          Each workflow is composable, pausable, and resumable.
        </p>
      </div>

      {/* Workflow grid */}
      <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
        {workflows.map((workflow) => (
          <Link
            key={workflow.slug}
            to={workflow.slug}
            className="group block bg-zinc-900/50 border border-zinc-800 rounded-xl p-5 hover:border-zinc-700 hover:bg-zinc-900/80 transition-all"
          >
            <div className="flex items-start gap-3.5">
              <div className="w-9 h-9 rounded-lg bg-blue-500/10 border border-blue-500/20 flex items-center justify-center shrink-0 mt-0.5">
                <svg className="w-4.5 h-4.5 text-blue-400" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1.5}>
                  <path strokeLinecap="round" strokeLinejoin="round" d={workflow.icon} />
                </svg>
              </div>
              <div className="min-w-0">
                <h3 className="text-[15px] font-medium text-zinc-200 group-hover:text-white transition-colors mb-1.5">
                  {workflow.name}
                </h3>
                <p className="text-[13px] text-zinc-500 leading-relaxed line-clamp-2">
                  {workflow.description}
                </p>
              </div>
            </div>
          </Link>
        ))}
      </div>

      {/* Architecture note */}
      <div className="bg-zinc-900/30 border border-zinc-800/60 rounded-xl p-5">
        <h3 className="text-sm font-medium text-zinc-300 mb-2">Workflow Architecture</h3>
        <p className="text-[13px] text-zinc-500 leading-relaxed">
          All workflows run on the ELSA workflow engine with support for long-running activities,
          compensation handlers, and checkpoint-based state persistence. Workflows emit events
          to the DCB event store for complete auditability and time-travel debugging.
        </p>
        <Link
          to="/architecture"
          className="inline-flex items-center gap-1.5 mt-3 text-[13px] text-blue-400 hover:text-blue-300 transition-colors"
        >
          View Architecture
          <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
            <path strokeLinecap="round" strokeLinejoin="round" d="M9 5l7 7-7 7" />
          </svg>
        </Link>
      </div>
    </div>
  );
}
