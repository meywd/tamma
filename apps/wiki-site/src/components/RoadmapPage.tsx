import { useEffect, useState, useMemo, useCallback } from 'react';
import { Link } from 'react-router';
import Markdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import rehypeRaw from 'rehype-raw';
import InlineMarkdown from './InlineMarkdown';

// --- Types ---

interface EpicOverviewRow {
  number: string;
  name: string;
  stories: string;
  done: string;
  status: string;
}

interface ParsedEpicDetail {
  number: string;
  name: string;
  goal: string;
  status: string;
  deliverables: string[];
  storiesRange: string;
  remaining: string;
  link: string;
  rawContent: string;
  postCompletion: string[];
}

interface TimelinePhase {
  name: string;
  status: 'completed' | 'active' | 'planned';
  epics: PhaseEpic[];
}

interface PhaseEpic {
  number: string;
  name: string;
  status: string;
}

interface SuccessMetric {
  label: string;
  value: string;
  description: string;
}

// --- Parsing helpers ---

function parseEpicOverviewTable(markdown: string): EpicOverviewRow[] {
  const rows: EpicOverviewRow[] = [];
  const tableMatch = markdown.match(
    /\| Epic \| Name \| Stories \| Done \| Status \|[\s\S]*?(?=\n---|\n##|\n$)/
  );
  if (!tableMatch) return rows;

  const lines = tableMatch[0].split('\n').filter((l) => l.trim().startsWith('|'));
  // Skip header and separator
  for (let i = 2; i < lines.length; i++) {
    const cells = lines[i]
      .split('|')
      .slice(1, -1)
      .map((c) => c.trim());
    if (cells.length >= 5) {
      const numMatch = cells[0].replace(/\*\*/g, '').match(/Epic\s+([\d.]+)/);
      rows.push({
        number: numMatch ? numMatch[1] : cells[0].replace(/\*\*/g, ''),
        name: cells[1],
        stories: cells[2],
        done: cells[3],
        status: cells[4],
      });
    }
  }
  return rows;
}

function findSectionContext(markdown: string, position: number): string {
  // Walk backwards from position to find the nearest ## heading
  const before = markdown.substring(0, position);
  // Find the LAST ## heading before this position
  const allH2 = [...before.matchAll(/^## (.+)$/gm)];
  if (allH2.length > 0) {
    return allH2[allH2.length - 1][1].trim().toLowerCase();
  }
  return '';
}

function inferStatusFromSection(sectionHeading: string): string {
  if (sectionHeading.includes('completed') || sectionHeading.includes('near complete')) {
    return 'Completed';
  }
  if (sectionHeading.includes('active') || sectionHeading.includes('in-progress') || sectionHeading.includes('in progress')) {
    return 'In Progress';
  }
  if (sectionHeading.includes('planned')) {
    return 'Planned';
  }
  return '';
}

function parseEpicDetails(markdown: string): ParsedEpicDetail[] {
  const details: ParsedEpicDetail[] = [];
  const epicRegex =
    /### Epic ([\d.]+):\s*(.+?)\n([\s\S]*?)(?=\n### Epic [\d.]|---\s*\n## |## Timeline|## Key Success|$)/g;

  let match;
  while ((match = epicRegex.exec(markdown)) !== null) {
    const num = match[1];
    const name = match[2].trim();
    const body = match[3];

    const goalMatch = body.match(/\*\*Goal:\*\*\s*(.+?)(?:\n|$)/);
    const statusMatch = body.match(/\*\*Status:\*\*\s*(.+?)(?:\n|$)/);

    // If no explicit status, infer from the ## section this epic is under
    const sectionContext = findSectionContext(markdown, match.index);
    const inferredStatus = statusMatch
      ? statusMatch[1].trim()
      : inferStatusFromSection(sectionContext);
    const storiesMatch = body.match(/\*\*Stories:\*\*\s*(.+?)(?:\n|$)/);
    const remainingMatch = body.match(/\*\*Remaining:\*\*\s*(.+?)(?:\n|$)/);
    const linkMatch = body.match(/\[Detailed Breakdown\]\((.+?)\)/);

    // Key deliverables
    const deliverableSection = body.match(
      /\*\*Key Deliverables:\*\*\n([\s\S]*?)(?=\n\*\*|$)/
    );
    const deliverables: string[] = [];
    if (deliverableSection) {
      for (const line of deliverableSection[1].split('\n')) {
        const bulletMatch = line.match(/^-\s+(.+)/);
        if (bulletMatch) deliverables.push(bulletMatch[1].trim());
      }
    }

    // Post-completion fixes
    const postSection = body.match(
      /\*\*Post-completion (?:fixes|changes):\*\*\n([\s\S]*?)(?=\n\*\*|$)/
    );
    const postCompletion: string[] = [];
    if (postSection) {
      for (const line of postSection[1].split('\n')) {
        const bulletMatch = line.match(/^-\s+(.+)/);
        if (bulletMatch) postCompletion.push(bulletMatch[1].trim());
      }
    }

    details.push({
      number: num,
      name,
      goal: goalMatch ? goalMatch[1].trim() : '',
      status: inferredStatus,
      deliverables,
      storiesRange: storiesMatch ? storiesMatch[1].trim() : '',
      remaining: remainingMatch ? remainingMatch[1].trim() : '',
      link: linkMatch ? linkMatch[1] : '',
      rawContent: body.trim(),
      postCompletion,
    });
  }
  return details;
}

function parseTimelinePhases(markdown: string): TimelinePhase[] {
  const phases: TimelinePhase[] = [];
  const timelineMatch = markdown.match(
    /## Timeline Visualization\s*\n```\n([\s\S]*?)\n```/
  );
  if (!timelineMatch) return phases;

  const content = timelineMatch[1];
  const phaseRegex =
    /Phase (\d+)\s*\(([^)]+)\):\s*\n([\s\S]*?)(?=\nPhase \d|\s*$)/g;

  let m;
  while ((m = phaseRegex.exec(content)) !== null) {
    const statusLabel = m[2].trim().toLowerCase();
    const status: 'completed' | 'active' | 'planned' = statusLabel.includes(
      'completed'
    )
      ? 'completed'
      : statusLabel.includes('active')
        ? 'active'
        : 'planned';

    const epicLines = m[3].trim().split('\n');
    const epics: PhaseEpic[] = [];
    for (const line of epicLines) {
      const epicMatch = line.match(
        /Epic ([\d.]+)\s+\((.+?)\)\s+\[(.+?)\]/
      );
      if (epicMatch) {
        epics.push({
          number: epicMatch[1],
          name: epicMatch[2].trim(),
          status: epicMatch[3].trim(),
        });
      }
    }

    phases.push({
      name: `Phase ${m[1]} (${m[2].trim()})`,
      status,
      epics,
    });
  }
  return phases;
}

function parseSuccessMetrics(markdown: string): SuccessMetric[] {
  const metrics: SuccessMetric[] = [];
  const metricsMatch = markdown.match(
    /## Key Success Metrics\s*\n([\s\S]*?)(?=\n---|\n## |$)/
  );
  if (!metricsMatch) return metrics;

  for (const line of metricsMatch[1].split('\n')) {
    const m = line.match(/^-\s+\*\*(.+?):\*\*\s*(.+?)(?:\s*\((.+?)\))?$/);
    if (m) {
      metrics.push({
        label: m[1].trim(),
        value: m[2].trim().replace(/\s*\(.*$/, ''),
        description: m[3] ? m[3].trim() : '',
      });
    }
  }
  return metrics;
}

function getEpicStatusType(
  status: string
): 'completed' | 'near-complete' | 'active' | 'partial' | 'planned' {
  const lower = status.toLowerCase();
  if (lower.includes('completed') && !lower.includes('near'))
    return 'completed';
  if (lower.includes('near complete')) return 'near-complete';
  if (lower.includes('ready for dev') || lower.includes('in progress'))
    return 'active';
  if (lower.includes('partially')) return 'partial';
  return 'planned';
}

function getStatusBadge(statusType: string) {
  switch (statusType) {
    case 'completed':
      return {
        bg: 'bg-emerald-500/10',
        text: 'text-emerald-400',
        border: 'border-emerald-500/20',
        dot: 'bg-emerald-500',
        label: 'Completed',
      };
    case 'near-complete':
      return {
        bg: 'bg-emerald-500/10',
        text: 'text-emerald-300',
        border: 'border-emerald-500/20',
        dot: 'bg-emerald-400',
        label: 'Near Complete',
      };
    case 'active':
      return {
        bg: 'bg-amber-500/10',
        text: 'text-amber-400',
        border: 'border-amber-500/20',
        dot: 'bg-amber-500',
        label: 'Active',
      };
    case 'partial':
      return {
        bg: 'bg-blue-500/10',
        text: 'text-blue-400',
        border: 'border-blue-500/20',
        dot: 'bg-blue-500',
        label: 'Partial',
      };
    default:
      return {
        bg: 'bg-zinc-500/10',
        text: 'text-zinc-400',
        border: 'border-zinc-500/20',
        dot: 'bg-zinc-600',
        label: 'Planned',
      };
  }
}

// Prose classes shared across fallback markdown blocks
const proseClasses =
  'prose prose-invert prose-sm max-w-none prose-p:text-[15px] prose-p:text-zinc-300 prose-a:text-blue-400 prose-a:no-underline hover:prose-a:underline prose-strong:text-zinc-100 prose-code:text-amber-300 prose-code:text-[12px] prose-code:bg-zinc-800/80 prose-code:px-1.5 prose-code:py-0.5 prose-code:rounded prose-pre:bg-[#18181b] prose-pre:border prose-pre:border-zinc-800 prose-pre:rounded-lg prose-pre:text-[13px] prose-li:text-[14px] prose-li:text-zinc-300';

// --- Components ---

function StatusBadge({ statusText }: { statusText: string }) {
  const type = getEpicStatusType(statusText);
  const badge = getStatusBadge(type);
  return (
    <span
      className={`inline-flex items-center gap-1.5 text-[11px] font-medium px-2 py-0.5 rounded-full border ${badge.bg} ${badge.text} ${badge.border}`}
    >
      <span className={`w-1.5 h-1.5 rounded-full ${badge.dot}`} />
      {badge.label}
    </span>
  );
}

function TimelineSection({ phases }: { phases: TimelinePhase[] }) {
  if (phases.length === 0) return null;

  return (
    <div>
      <h2 className="text-sm font-semibold text-zinc-500 uppercase tracking-wider mb-6">
        Timeline
      </h2>
      <div className="relative">
        {phases.map((phase, pi) => {
          const lineColor =
            phase.status === 'completed'
              ? 'bg-emerald-500'
              : phase.status === 'active'
                ? 'bg-amber-500'
                : 'bg-zinc-700';
          const lineDashed = phase.status === 'planned';
          const dotColor =
            phase.status === 'completed'
              ? 'bg-emerald-500 ring-emerald-500/20'
              : phase.status === 'active'
                ? 'bg-amber-500 ring-amber-500/20'
                : 'bg-zinc-600 ring-zinc-600/20';
          const phaseLabel =
            phase.status === 'completed'
              ? 'text-emerald-400'
              : phase.status === 'active'
                ? 'text-amber-400'
                : 'text-zinc-500';

          return (
            <div key={pi} className="relative flex gap-6 pb-8 last:pb-0">
              {/* Vertical line */}
              <div className="flex flex-col items-center shrink-0 w-32">
                <div
                  className={`w-3 h-3 rounded-full ring-4 ${dotColor} shrink-0 z-10`}
                />
                {pi < phases.length - 1 && (
                  <div
                    className={`w-0.5 flex-1 mt-1 ${lineColor} ${lineDashed ? 'opacity-40' : ''}`}
                    style={lineDashed ? { backgroundImage: 'repeating-linear-gradient(to bottom, currentColor 0, currentColor 4px, transparent 4px, transparent 8px)' } : undefined}
                  />
                )}
              </div>

              {/* Phase content */}
              <div className="flex-1 -mt-1 pb-2">
                <div
                  className={`text-xs font-semibold uppercase tracking-wider mb-3 ${phaseLabel}`}
                >
                  {phase.name}
                </div>
                <div className="space-y-2">
                  {phase.epics.map((epic) => {
                    const epicStatusType = getEpicStatusType(epic.status);
                    const epicBadge = getStatusBadge(epicStatusType);
                    return (
                      <div
                        key={epic.number}
                        className="flex items-center gap-3 bg-zinc-900/50 border border-zinc-800 rounded-lg px-4 py-2.5 hover:border-zinc-700 transition-colors"
                      >
                        <span className="text-xs font-mono text-zinc-600 w-6 text-right shrink-0">
                          {epic.number}
                        </span>
                        <span className="text-[13px] text-zinc-300 truncate">
                          {epic.name}
                        </span>
                        <span
                          className={`ml-auto shrink-0 inline-flex items-center gap-1 text-[10px] font-medium px-1.5 py-0.5 rounded-full border ${epicBadge.bg} ${epicBadge.text} ${epicBadge.border}`}
                        >
                          <span
                            className={`w-1 h-1 rounded-full ${epicBadge.dot}`}
                          />
                          {epic.status
                            .replace(/[\[\]]/g, '')
                            .split(' - ')[0]
                            .trim()
                            .substring(0, 20)}
                        </span>
                      </div>
                    );
                  })}
                </div>
              </div>
            </div>
          );
        })}
      </div>
    </div>
  );
}

function EpicDetailCard({
  detail,
  isOpen,
  onToggle,
}: {
  detail: ParsedEpicDetail;
  isOpen: boolean;
  onToggle: () => void;
}) {
  const statusType = getEpicStatusType(detail.status || detail.name);

  return (
    <div className="bg-zinc-900/50 border border-zinc-800 rounded-xl overflow-hidden transition-colors hover:border-zinc-700">
      {/* Header - always visible */}
      <button
        onClick={onToggle}
        className="w-full flex items-center gap-3 px-5 py-4 text-left group"
      >
        <span className="text-xs font-mono text-zinc-600 bg-zinc-800/60 px-2 py-0.5 rounded shrink-0">
          {detail.number}
        </span>
        <span className="text-[15px] font-medium text-zinc-200 group-hover:text-white transition-colors truncate">
          {detail.name}
        </span>
        <span className="ml-auto shrink-0 flex items-center gap-3">
          <StatusBadge statusText={detail.status || statusType} />
          <svg
            className={`w-4 h-4 text-zinc-500 transition-transform duration-200 ${isOpen ? 'rotate-180' : ''}`}
            fill="none"
            viewBox="0 0 24 24"
            stroke="currentColor"
            strokeWidth={2}
          >
            <path
              strokeLinecap="round"
              strokeLinejoin="round"
              d="M19 9l-7 7-7-7"
            />
          </svg>
        </span>
      </button>

      {/* Expanded content */}
      <div
        className={`overflow-hidden transition-all duration-300 ease-in-out ${
          isOpen ? 'max-h-[2000px] opacity-100' : 'max-h-0 opacity-0'
        }`}
      >
        <div className="px-5 pb-5 pt-0 space-y-4 border-t border-zinc-800/60">
          {/* Goal */}
          {detail.goal && (
            <div className="pt-4">
              <div className="text-[11px] font-semibold text-zinc-600 uppercase tracking-wider mb-1.5">
                Goal
              </div>
              <p className="text-[14px] text-zinc-300 leading-relaxed">
                <InlineMarkdown>{detail.goal}</InlineMarkdown>
              </p>
            </div>
          )}

          {/* Key Deliverables */}
          {detail.deliverables.length > 0 && (
            <div>
              <div className="text-[11px] font-semibold text-zinc-600 uppercase tracking-wider mb-2">
                Key Deliverables
              </div>
              <ul className="space-y-1.5">
                {detail.deliverables.map((d, i) => (
                  <li key={i} className="flex items-start gap-2">
                    <span className="w-1.5 h-1.5 rounded-full bg-blue-500/60 mt-1.5 shrink-0" />
                    <span className="text-[13px] text-zinc-400 leading-relaxed">
                      <InlineMarkdown>{d}</InlineMarkdown>
                    </span>
                  </li>
                ))}
              </ul>
            </div>
          )}

          {/* Stories range */}
          {detail.storiesRange && (
            <div className="flex items-center gap-2 text-[13px] text-zinc-500">
              <svg
                className="w-3.5 h-3.5"
                fill="none"
                viewBox="0 0 24 24"
                stroke="currentColor"
                strokeWidth={1.5}
              >
                <path
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z"
                />
              </svg>
              {detail.storiesRange}
            </div>
          )}

          {/* Remaining */}
          {detail.remaining && (
            <div className="text-[13px] text-zinc-500">
              <span className="text-zinc-600 font-medium">Remaining:</span>{' '}
              <InlineMarkdown>{detail.remaining}</InlineMarkdown>
            </div>
          )}

          {/* Post-completion notes */}
          {detail.postCompletion.length > 0 && (
            <div className="bg-zinc-800/30 rounded-lg p-3">
              <div className="text-[11px] font-semibold text-zinc-600 uppercase tracking-wider mb-1.5">
                Post-completion Notes
              </div>
              <ul className="space-y-1">
                {detail.postCompletion.map((note, i) => (
                  <li
                    key={i}
                    className="text-[12px] text-zinc-500 leading-relaxed"
                  >
                    - <InlineMarkdown>{note}</InlineMarkdown>
                  </li>
                ))}
              </ul>
            </div>
          )}

          {/* Link to detailed breakdown */}
          {detail.link && (
            <Link
              to={detail.link}
              className="inline-flex items-center gap-1.5 text-[13px] text-blue-400 hover:text-blue-300 transition-colors"
            >
              View detailed breakdown
              <svg
                className="w-3.5 h-3.5"
                fill="none"
                viewBox="0 0 24 24"
                stroke="currentColor"
                strokeWidth={2}
              >
                <path
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  d="M9 5l7 7-7 7"
                />
              </svg>
            </Link>
          )}
        </div>
      </div>
    </div>
  );
}

function MetricsSection({ metrics }: { metrics: SuccessMetric[] }) {
  if (metrics.length === 0) return null;

  return (
    <div>
      <h2 className="text-sm font-semibold text-zinc-500 uppercase tracking-wider mb-4">
        Key Success Metrics
      </h2>
      <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-3">
        {metrics.map((metric, i) => (
          <div
            key={i}
            className="bg-zinc-900/50 border border-zinc-800 rounded-xl p-4"
          >
            <div className="text-xl font-bold text-white mb-1">
              {metric.value}
            </div>
            <div className="text-[13px] font-medium text-zinc-300">
              {metric.label}
            </div>
            {metric.description && (
              <div className="text-[11px] text-zinc-600 mt-1">
                {metric.description}
              </div>
            )}
          </div>
        ))}
      </div>
    </div>
  );
}

// --- Main component ---

export default function RoadmapPage() {
  const [content, setContent] = useState('');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(false);
  const [expandedEpics, setExpandedEpics] = useState<Set<string>>(new Set());

  useEffect(() => {
    document.title = 'Project Roadmap \u2014 Tamma Docs';
  }, []);

  useEffect(() => {
    setLoading(true);
    fetch('/content/roadmap.md')
      .then(async (res) => {
        if (!res.ok) throw new Error('Not found');
        return res.text();
      })
      .then((text) => {
        const stripped = text.replace(/^---[\s\S]*?---\n*/, '');
        setContent(stripped);
        setLoading(false);
      })
      .catch(() => {
        setError(true);
        setLoading(false);
      });
  }, []);

  const epicOverview = useMemo(() => parseEpicOverviewTable(content), [content]);
  const epicDetails = useMemo(() => parseEpicDetails(content), [content]);
  const timelinePhases = useMemo(
    () => parseTimelinePhases(content),
    [content]
  );
  const successMetrics = useMemo(
    () => parseSuccessMetrics(content),
    [content]
  );

  // Group detail sections by category
  const completedDetails = useMemo(
    () =>
      epicDetails.filter((d) => {
        const type = getEpicStatusType(d.status || '');
        return type === 'completed' || type === 'near-complete';
      }),
    [epicDetails]
  );
  const activeDetails = useMemo(
    () =>
      epicDetails.filter((d) => {
        const type = getEpicStatusType(d.status || '');
        return type === 'active' || type === 'partial';
      }),
    [epicDetails]
  );
  const plannedDetails = useMemo(
    () =>
      epicDetails.filter((d) => {
        const type = getEpicStatusType(d.status || '');
        return type === 'planned';
      }),
    [epicDetails]
  );

  // Count summary stats
  const totalEpics = epicOverview.length;
  const completedCount = epicOverview.filter((e) => {
    const s = e.status.toLowerCase();
    return s.includes('completed') && !s.includes('near');
  }).length;
  const nearCompleteCount = epicOverview.filter(
    (e) =>
      e.status.toLowerCase().includes('near complete') ||
      (e.done !== '0' &&
        e.done !== '--' &&
        !e.status.toLowerCase().includes('completed'))
  ).length;
  const draftedCount = epicOverview.filter((e) =>
    e.status.toLowerCase().includes('drafted')
  ).length;

  const toggleEpic = useCallback((num: string) => {
    setExpandedEpics((prev) => {
      const next = new Set(prev);
      if (next.has(num)) {
        next.delete(num);
      } else {
        next.add(num);
      }
      return next;
    });
  }, []);

  // Identify sections that were not parsed into structured data for fallback
  const fallbackSections = useMemo(() => {
    const handled = new Set([
      'epic overview',
      'completed / near complete epics',
      'near complete epics',
      'planned / in-progress epics',
      'timeline visualization',
      'key success metrics',
    ]);
    const sections: Array<{ heading: string; content: string }> = [];
    const regex = /^## (.+)$/gm;
    let m;
    const headings: Array<{ heading: string; start: number }> = [];
    while ((m = regex.exec(content)) !== null) {
      headings.push({ heading: m[1], start: m.index + m[0].length });
    }
    for (let i = 0; i < headings.length; i++) {
      const end =
        i < headings.length - 1
          ? content.lastIndexOf('\n##', headings[i + 1].start - 3)
          : content.length;
      const h = headings[i].heading.toLowerCase().trim();
      if (!handled.has(h) && !h.includes('epic')) {
        const sectionContent = content.substring(headings[i].start, end).trim();
        if (sectionContent) {
          sections.push({ heading: headings[i].heading, content: sectionContent });
        }
      }
    }
    return sections;
  }, [content]);

  if (loading) {
    return (
      <div className="animate-pulse space-y-6 py-8">
        <div className="h-4 bg-zinc-800/30 rounded w-48 mb-6" />
        <div className="h-10 bg-zinc-800/50 rounded w-96 mb-2" />
        <div className="h-4 bg-zinc-800/30 rounded w-80 mb-8" />
        <div className="grid grid-cols-4 gap-3">
          {Array.from({ length: 4 }).map((_, i) => (
            <div
              key={i}
              className="h-20 bg-zinc-900/50 rounded-xl border border-zinc-800"
            />
          ))}
        </div>
        <div className="space-y-3 mt-8">
          {Array.from({ length: 5 }).map((_, i) => (
            <div
              key={i}
              className="h-14 bg-zinc-900/50 rounded-xl border border-zinc-800"
            />
          ))}
        </div>
      </div>
    );
  }

  if (error) {
    return (
      <div className="py-16 text-center">
        <div className="text-6xl mb-4">404</div>
        <div className="text-zinc-500 text-lg">Roadmap page not found</div>
        <Link
          to="/"
          className="inline-flex items-center gap-1.5 mt-6 text-sm text-blue-400 hover:text-blue-300 transition-colors"
        >
          <svg
            className="w-4 h-4"
            fill="none"
            viewBox="0 0 24 24"
            stroke="currentColor"
            strokeWidth={2}
          >
            <path
              strokeLinecap="round"
              strokeLinejoin="round"
              d="M15 19l-7-7 7-7"
            />
          </svg>
          Back to Home
        </Link>
      </div>
    );
  }

  return (
    <div className="space-y-10">
      {/* Breadcrumbs */}
      <nav className="flex items-center gap-1.5 text-[13px] text-zinc-500">
        <Link to="/" className="hover:text-zinc-300 transition-colors">
          Home
        </Link>
        <svg
          className="w-3 h-3 text-zinc-700"
          fill="none"
          viewBox="0 0 24 24"
          stroke="currentColor"
          strokeWidth={2}
        >
          <path
            strokeLinecap="round"
            strokeLinejoin="round"
            d="M9 5l7 7-7 7"
          />
        </svg>
        <span className="text-zinc-400">Roadmap</span>
      </nav>

      {/* Header */}
      <div>
        <h1 className="text-3xl font-bold text-white tracking-tight">
          Project Roadmap
        </h1>
        <p className="mt-2 text-zinc-400 text-[15px] leading-relaxed max-w-2xl">
          {totalEpics} epics from foundation to SaaS platform. Track progress
          across all phases of the Tamma autonomous development platform.
        </p>
      </div>

      {/* Summary stats */}
      <div className="grid grid-cols-2 sm:grid-cols-4 gap-3">
        <div className="bg-zinc-900/50 border border-zinc-800 rounded-xl p-4 text-center">
          <div className="text-2xl font-bold text-white">{totalEpics}</div>
          <div className="text-xs text-zinc-500 mt-1 uppercase tracking-wider">
            Total Epics
          </div>
        </div>
        <div className="bg-zinc-900/50 border border-zinc-800 rounded-xl p-4 text-center">
          <div className="flex items-center justify-center gap-1.5">
            <span className="w-2 h-2 rounded-full bg-emerald-500" />
            <span className="text-2xl font-bold text-white">
              {completedCount}
            </span>
          </div>
          <div className="text-xs text-zinc-500 mt-1 uppercase tracking-wider">
            Completed
          </div>
        </div>
        <div className="bg-zinc-900/50 border border-zinc-800 rounded-xl p-4 text-center">
          <div className="flex items-center justify-center gap-1.5">
            <span className="w-2 h-2 rounded-full bg-amber-500" />
            <span className="text-2xl font-bold text-white">
              {nearCompleteCount}
            </span>
          </div>
          <div className="text-xs text-zinc-500 mt-1 uppercase tracking-wider">
            In Progress
          </div>
        </div>
        <div className="bg-zinc-900/50 border border-zinc-800 rounded-xl p-4 text-center">
          <div className="flex items-center justify-center gap-1.5">
            <span className="w-2 h-2 rounded-full bg-zinc-600" />
            <span className="text-2xl font-bold text-white">
              {draftedCount}
            </span>
          </div>
          <div className="text-xs text-zinc-500 mt-1 uppercase tracking-wider">
            Planned
          </div>
        </div>
      </div>

      {/* Epic Overview Table */}
      {epicOverview.length > 0 && (
        <div>
          <h2 className="text-sm font-semibold text-zinc-500 uppercase tracking-wider mb-4">
            Epic Overview
          </h2>
          <div className="bg-zinc-900/50 border border-zinc-800 rounded-xl overflow-hidden">
            <div className="overflow-x-auto">
              <table className="w-full text-[13px]">
                <thead>
                  <tr className="bg-zinc-800/50">
                    <th className="text-left text-zinc-400 font-medium px-4 py-2.5 border-b border-zinc-800 w-16">
                      #
                    </th>
                    <th className="text-left text-zinc-400 font-medium px-4 py-2.5 border-b border-zinc-800">
                      Name
                    </th>
                    <th className="text-center text-zinc-400 font-medium px-4 py-2.5 border-b border-zinc-800 w-20">
                      Stories
                    </th>
                    <th className="text-center text-zinc-400 font-medium px-4 py-2.5 border-b border-zinc-800 w-16">
                      Done
                    </th>
                    <th className="text-left text-zinc-400 font-medium px-4 py-2.5 border-b border-zinc-800">
                      Status
                    </th>
                  </tr>
                </thead>
                <tbody>
                  {epicOverview.map((row) => {
                    const badge = getStatusBadge(getEpicStatusType(row.status));
                    return (
                      <tr
                        key={row.number}
                        className="hover:bg-zinc-800/30 transition-colors"
                      >
                        <td className="px-4 py-2.5 text-zinc-500 font-mono border-b border-zinc-800/50">
                          {row.number}
                        </td>
                        <td className="px-4 py-2.5 text-zinc-300 font-medium border-b border-zinc-800/50">
                          {row.name}
                        </td>
                        <td className="px-4 py-2.5 text-zinc-400 text-center border-b border-zinc-800/50">
                          {row.stories}
                        </td>
                        <td className="px-4 py-2.5 text-zinc-400 text-center border-b border-zinc-800/50">
                          {row.done}
                        </td>
                        <td className="px-4 py-2.5 border-b border-zinc-800/50">
                          <span
                            className={`inline-flex items-center gap-1.5 text-[11px] font-medium px-2 py-0.5 rounded-full border ${badge.bg} ${badge.text} ${badge.border}`}
                          >
                            <span
                              className={`w-1.5 h-1.5 rounded-full ${badge.dot}`}
                            />
                            {badge.label}
                          </span>
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          </div>
        </div>
      )}

      {/* Visual Timeline */}
      <TimelineSection phases={timelinePhases} />

      {/* Epic Details - Completed / Near Complete */}
      {completedDetails.length > 0 && (
        <div>
          <div className="flex items-center gap-2 mb-4">
            <span className="w-2 h-2 rounded-full bg-emerald-500" />
            <h2 className="text-sm font-semibold text-zinc-500 uppercase tracking-wider">
              Completed / Near Complete ({completedDetails.length})
            </h2>
          </div>
          <div className="space-y-3">
            {completedDetails.map((detail) => (
              <EpicDetailCard
                key={detail.number}
                detail={detail}
                isOpen={expandedEpics.has(detail.number)}
                onToggle={() => toggleEpic(detail.number)}
              />
            ))}
          </div>
        </div>
      )}

      {/* Epic Details - Active */}
      {activeDetails.length > 0 && (
        <div>
          <div className="flex items-center gap-2 mb-4">
            <span className="w-2 h-2 rounded-full bg-amber-500" />
            <h2 className="text-sm font-semibold text-zinc-500 uppercase tracking-wider">
              Active / In Progress ({activeDetails.length})
            </h2>
          </div>
          <div className="space-y-3">
            {activeDetails.map((detail) => (
              <EpicDetailCard
                key={detail.number}
                detail={detail}
                isOpen={expandedEpics.has(detail.number)}
                onToggle={() => toggleEpic(detail.number)}
              />
            ))}
          </div>
        </div>
      )}

      {/* Epic Details - Planned */}
      {plannedDetails.length > 0 && (
        <div>
          <div className="flex items-center gap-2 mb-4">
            <span className="w-2 h-2 rounded-full bg-zinc-600" />
            <h2 className="text-sm font-semibold text-zinc-500 uppercase tracking-wider">
              Planned ({plannedDetails.length})
            </h2>
          </div>
          <div className="space-y-3">
            {plannedDetails.map((detail) => (
              <EpicDetailCard
                key={detail.number}
                detail={detail}
                isOpen={expandedEpics.has(detail.number)}
                onToggle={() => toggleEpic(detail.number)}
              />
            ))}
          </div>
        </div>
      )}

      {/* Success Metrics */}
      <MetricsSection metrics={successMetrics} />

      {/* Fallback sections */}
      {fallbackSections.map((section, i) => (
        <div key={i}>
          <h2 className="text-sm font-semibold text-zinc-500 uppercase tracking-wider mb-4">
            {section.heading}
          </h2>
          <div className="bg-zinc-900/50 border border-zinc-800 rounded-xl p-5">
            <div className={proseClasses}>
              <Markdown remarkPlugins={[remarkGfm]} rehypePlugins={[rehypeRaw]}>
                {section.content}
              </Markdown>
            </div>
          </div>
        </div>
      ))}

      {/* Footer */}
      <div className="border-t border-zinc-800 pt-6 text-xs text-zinc-600">
        Last audited: 2026-03-31 ·{' '}
        <Link to="/epics" className="text-zinc-500 hover:text-zinc-400">
          View all epics
        </Link>{' '}
        ·{' '}
        <a
          href="https://github.com/meywd/tamma"
          className="text-zinc-500 hover:text-zinc-400"
        >
          GitHub
        </a>
      </div>
    </div>
  );
}
