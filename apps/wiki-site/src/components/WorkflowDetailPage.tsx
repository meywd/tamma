import { useEffect, useState, useMemo } from 'react';
import { useParams, Link } from 'react-router';
import Markdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import rehypeRaw from 'rehype-raw';

interface ManifestEntry {
  path: string;
  title: string;
  section: string;
}

interface ParsedSection {
  heading: string;
  level: number;
  content: string;
}

function parseSections(markdown: string): ParsedSection[] {
  const lines = markdown.split('\n');
  const sections: ParsedSection[] = [];
  let currentHeading = '';
  let currentLevel = 0;
  let currentLines: string[] = [];

  for (const line of lines) {
    const headingMatch = line.match(/^(#{2,3})\s+(.+)$/);
    if (headingMatch) {
      if (currentHeading || currentLines.length > 0) {
        sections.push({
          heading: currentHeading,
          level: currentLevel,
          content: currentLines.join('\n').trim(),
        });
      }
      currentHeading = headingMatch[2];
      currentLevel = headingMatch[1].length;
      currentLines = [];
    } else {
      currentLines.push(line);
    }
  }
  if (currentHeading || currentLines.length > 0) {
    sections.push({
      heading: currentHeading,
      level: currentLevel,
      content: currentLines.join('\n').trim(),
    });
  }
  return sections;
}

function parseTableData(content: string): { headers: string[]; rows: string[][] } | null {
  const lines = content.split('\n').filter((l) => l.trim().startsWith('|'));
  if (lines.length < 2) return null;

  const parseRow = (line: string): string[] =>
    line
      .split('|')
      .slice(1, -1)
      .map((cell) => cell.trim());

  const headers = parseRow(lines[0]);
  const rows = lines.slice(2).map(parseRow);
  return { headers, rows };
}

function extractFlowDiagram(content: string): string | null {
  const match = content.match(/```[\s\S]*?\n([\s\S]*?)```/);
  return match ? match[1] : null;
}

interface FlowStep {
  id: string;
  label: string;
  type: 'process' | 'decision' | 'terminal' | 'start';
}

function parseFlowSteps(diagram: string): FlowStep[] {
  const steps: FlowStep[] = [];
  const seen = new Set<string>();

  // Match box patterns: +------+ / | text | / +------+
  const boxRegex = /\|\s*(.+?)\s*\|/g;
  let match;
  while ((match = boxRegex.exec(diagram)) !== null) {
    const label = match[1].trim();
    // Skip divider-only lines (e.g. lines of dashes)
    if (label.match(/^[-+]+$/) || label === '') continue;
    // Skip labels that are just arrows or separators
    if (label.match(/^[v^|<>]+$/)) continue;

    const key = label.replace(/\s+/g, ' ').toLowerCase();
    if (seen.has(key)) continue;
    seen.add(key);

    let type: FlowStep['type'] = 'process';
    if (label.match(/\?$/)) type = 'decision';
    else if (label.match(/finish|end|stop|terminate/i)) type = 'terminal';
    else if (label.match(/init|start|load|begin/i)) type = 'start';

    steps.push({ id: key, label, type });
  }
  return steps;
}

function extractRelatedWorkflows(content: string): Array<{ name: string; slug: string }> {
  const links: Array<{ name: string; slug: string }> = [];
  const seen = new Set<string>();

  // Match markdown links that reference other workflows
  const linkRegex = /\[([^\]]+)\]\((?:Workflow-)?([^)]+)\)/g;
  let match;
  while ((match = linkRegex.exec(content)) !== null) {
    const name = match[1];
    let target = match[2];

    // Convert wiki-style links to URL paths
    if (!target.startsWith('/') && !target.startsWith('http')) {
      target = target
        .replace(/^Workflow-/, '')
        .replace(/([A-Z])/g, (m, c, i) => (i > 0 ? '-' : '') + c.toLowerCase());
    }

    // Only include workflow links
    if (
      target.includes('workflow') ||
      target.includes('Workflow') ||
      name.toLowerCase().includes('cycle') ||
      name.toLowerCase().includes('workflow')
    ) {
      const slug = target.replace(/^\/workflows\//, '').replace(/^Workflow-/i, '');
      const normalizedSlug = slug
        .replace(/([A-Z])/g, '-$1')
        .toLowerCase()
        .replace(/^-/, '')
        .replace(/-+/g, '-');

      if (!seen.has(normalizedSlug)) {
        seen.add(normalizedSlug);
        links.push({ name, slug: normalizedSlug });
      }
    }
  }
  return links;
}

function extractMetadata(content: string): Record<string, string> {
  const meta: Record<string, string> = {};
  const lines = content.split('\n').slice(0, 10);
  for (const line of lines) {
    const match = line.match(/^\*\*(.+?):\*\*\s*(.+)/);
    if (match) {
      meta[match[1].trim()] = match[2].trim();
    }
  }
  return meta;
}

const stepTypeStyles = {
  start: {
    bg: 'bg-blue-500/10',
    border: 'border-blue-500/20',
    text: 'text-blue-400',
    icon: 'M14.752 11.168l-3.197-2.132A1 1 0 0010 9.87v4.263a1 1 0 001.555.832l3.197-2.132a1 1 0 000-1.664z',
  },
  process: {
    bg: 'bg-zinc-800/50',
    border: 'border-zinc-700/50',
    text: 'text-zinc-300',
    icon: 'M10.325 4.317c.426-1.756 2.924-1.756 3.35 0a1.724 1.724 0 002.573 1.066c1.543-.94 3.31.826 2.37 2.37a1.724 1.724 0 001.066 2.573c1.756.426 1.756 2.924 0 3.35a1.724 1.724 0 00-1.066 2.573c.94 1.543-.826 3.31-2.37 2.37a1.724 1.724 0 00-2.573 1.066c-.426 1.756-2.924 1.756-3.35 0a1.724 1.724 0 00-2.573-1.066c-1.543.94-3.31-.826-2.37-2.37a1.724 1.724 0 00-1.066-2.573c-1.756-.426-1.756-2.924 0-3.35a1.724 1.724 0 001.066-2.573c-.94-1.543.826-3.31 2.37-2.37.996.608 2.296.07 2.572-1.065z',
  },
  decision: {
    bg: 'bg-amber-500/10',
    border: 'border-amber-500/20',
    text: 'text-amber-400',
    icon: 'M8.228 9c.549-1.165 2.03-2 3.772-2 2.21 0 4 1.343 4 3 0 1.4-1.278 2.575-3.006 2.907-.542.104-.994.54-.994 1.093m0 3h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z',
  },
  terminal: {
    bg: 'bg-emerald-500/10',
    border: 'border-emerald-500/20',
    text: 'text-emerald-400',
    icon: 'M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z',
  },
};

export default function WorkflowDetailPage() {
  const { slug } = useParams();
  const [content, setContent] = useState('');
  const [title, setTitle] = useState('');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(false);
  const [manifest, setManifest] = useState<ManifestEntry[]>([]);

  useEffect(() => {
    fetch('/content/manifest.json')
      .then((r) => r.json())
      .then((data: ManifestEntry[]) => setManifest(data))
      .catch(() => {});
  }, []);

  useEffect(() => {
    setLoading(true);
    setError(false);
    fetch(`/content/workflows/${slug}.md`)
      .then((res) => {
        if (!res.ok) throw new Error('Not found');
        return res.text();
      })
      .then((text) => {
        const fmMatch = text.match(/^---\s*\n[\s\S]*?title:\s*"?([^"\n]+)"?[\s\S]*?---\s*\n/);
        if (fmMatch) setTitle(fmMatch[1].trim());
        const stripped = text.replace(/^---[\s\S]*?---\n*/, '');
        if (!fmMatch) {
          const h1Match = stripped.match(/^#\s+(.+)$/m);
          if (h1Match) setTitle(h1Match[1].trim());
        }
        setContent(stripped);
        setLoading(false);
      })
      .catch(() => {
        setError(true);
        setLoading(false);
      });
  }, [slug]);

  useEffect(() => {
    if (title) document.title = `${title} — Tamma Docs`;
  }, [title]);

  const workflowName = useMemo(() => {
    return title.replace(/^Workflow:\s*/, '');
  }, [title]);

  const metadata = useMemo(() => extractMetadata(content), [content]);
  const sections = useMemo(() => parseSections(content), [content]);

  // Find the purpose/description section
  const purposeSection = useMemo(
    () => sections.find((s) => s.heading.toLowerCase().includes('purpose')),
    [sections]
  );

  // Find flow diagram section
  const flowSection = useMemo(
    () => sections.find((s) => s.heading.toLowerCase().includes('flow') || s.heading.toLowerCase().includes('diagram')),
    [sections]
  );
  const flowDiagram = useMemo(() => (flowSection ? extractFlowDiagram(flowSection.content) : null), [flowSection]);
  const flowSteps = useMemo(() => (flowDiagram ? parseFlowSteps(flowDiagram) : []), [flowDiagram]);

  // Find configuration section
  const configSection = useMemo(
    () =>
      sections.find(
        (s) =>
          s.heading.toLowerCase().includes('config') ||
          s.heading.toLowerCase().includes('input') ||
          s.heading.toLowerCase().includes('variable')
      ),
    [sections]
  );

  // Find all table sections (exclude flow diagram which has | chars from ASCII art)
  const tableSections = useMemo(
    () =>
      sections.filter((s) => {
        if (flowSection && s.heading === flowSection.heading) return false;
        // Check for actual markdown table rows (lines starting with |)
        const tableLines = s.content.split('\n').filter((l) => l.trim().startsWith('|'));
        return tableLines.length >= 3; // header + separator + at least one row
      }),
    [sections, flowSection]
  );

  // Related workflows from content
  const relatedWorkflows = useMemo(() => extractRelatedWorkflows(content), [content]);

  // All workflow entries from manifest for link validation
  const workflowEntries = useMemo(
    () => manifest.filter((e) => e.section === 'Workflows'),
    [manifest]
  );

  // Sections handled specially
  const handledHeadings = useMemo(() => {
    const set = new Set<string>();
    if (purposeSection) set.add(purposeSection.heading);
    if (flowSection) set.add(flowSection.heading);
    // Table sections handled inline
    for (const s of tableSections) set.add(s.heading);
    return set;
  }, [purposeSection, flowSection, tableSections]);

  // Remaining sections for fallback rendering
  const fallbackSections = useMemo(
    () =>
      sections.filter(
        (s) =>
          s.heading &&
          !handledHeadings.has(s.heading) &&
          s.level === 2 &&
          s.content.trim().length > 0
      ),
    [sections, handledHeadings]
  );

  // Prev/next workflow navigation
  const sortedWorkflows = useMemo(
    () => [...workflowEntries].sort((a, b) => a.title.localeCompare(b.title)),
    [workflowEntries]
  );
  const currentIdx = useMemo(
    () => sortedWorkflows.findIndex((e) => e.path === `/workflows/${slug}`),
    [sortedWorkflows, slug]
  );
  const prevWorkflow = currentIdx > 0 ? sortedWorkflows[currentIdx - 1] : null;
  const nextWorkflow = currentIdx < sortedWorkflows.length - 1 ? sortedWorkflows[currentIdx + 1] : null;

  if (loading) {
    return (
      <div className="animate-pulse space-y-6 py-8">
        <div className="h-4 bg-zinc-800/30 rounded w-48 mb-6" />
        <div className="h-8 bg-zinc-800/50 rounded w-80" />
        <div className="h-4 bg-zinc-800/30 rounded w-full" />
        <div className="h-4 bg-zinc-800/30 rounded w-5/6" />
        <div className="h-48 bg-zinc-900/50 rounded-xl border border-zinc-800 mt-6" />
      </div>
    );
  }

  if (error) {
    return (
      <div className="py-16 text-center">
        <div className="text-6xl mb-4">404</div>
        <div className="text-zinc-500 text-lg">Workflow not found</div>
        <Link
          to="/workflows"
          className="inline-flex items-center gap-1.5 mt-6 text-sm text-blue-400 hover:text-blue-300 transition-colors"
        >
          <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
            <path strokeLinecap="round" strokeLinejoin="round" d="M15 19l-7-7 7-7" />
          </svg>
          Back to Workflows
        </Link>
      </div>
    );
  }

  return (
    <div className="space-y-8">
      {/* Breadcrumbs */}
      <nav className="flex items-center gap-1.5 text-[13px] text-zinc-500 flex-wrap">
        <Link to="/" className="hover:text-zinc-300 transition-colors">Home</Link>
        <svg className="w-3 h-3 text-zinc-700" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
          <path strokeLinecap="round" strokeLinejoin="round" d="M9 5l7 7-7 7" />
        </svg>
        <Link to="/workflows" className="hover:text-zinc-300 transition-colors">Workflows</Link>
        <svg className="w-3 h-3 text-zinc-700" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
          <path strokeLinecap="round" strokeLinejoin="round" d="M9 5l7 7-7 7" />
        </svg>
        <span className="text-zinc-400 truncate max-w-xs">{workflowName}</span>
      </nav>

      {/* Header */}
      <div className="space-y-3">
        <div className="flex items-start gap-3.5">
          <div className="w-10 h-10 rounded-lg bg-blue-500/10 border border-blue-500/20 flex items-center justify-center shrink-0 mt-1">
            <svg className="w-5 h-5 text-blue-400" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1.5}>
              <path strokeLinecap="round" strokeLinejoin="round" d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
            </svg>
          </div>
          <div>
            <h1 className="text-2xl sm:text-3xl font-bold text-white tracking-tight">
              {workflowName}
            </h1>
            {/* Metadata badges */}
            <div className="flex items-center gap-2 mt-2 flex-wrap">
              {metadata['Definition ID'] && (
                <span className="text-xs font-mono text-blue-400 bg-blue-500/10 border border-blue-500/20 px-2 py-0.5 rounded">
                  {metadata['Definition ID']}
                </span>
              )}
              {metadata['Class'] && (
                <span className="text-xs font-mono text-violet-400 bg-violet-500/10 border border-violet-500/20 px-2 py-0.5 rounded">
                  {metadata['Class']}
                </span>
              )}
            </div>
          </div>
        </div>

        {/* Source file */}
        {metadata['Source'] && (
          <div className="flex items-center gap-2 text-xs text-zinc-600">
            <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
              <path strokeLinecap="round" strokeLinejoin="round" d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />
            </svg>
            <code className="text-zinc-500 font-mono">{metadata['Source']}</code>
          </div>
        )}

        {/* Purpose description */}
        {purposeSection && (
          <div className="text-[15px] text-zinc-400 leading-relaxed max-w-3xl prose prose-invert prose-sm max-w-none prose-p:text-zinc-400 prose-a:text-blue-400 prose-li:text-zinc-400 prose-strong:text-zinc-200">
            <Markdown remarkPlugins={[remarkGfm]} rehypePlugins={[rehypeRaw]}>
              {purposeSection.content}
            </Markdown>
          </div>
        )}
      </div>

      {/* Flow Diagram */}
      {flowSection && (
        <div>
          <h2 className="text-sm font-semibold text-zinc-500 uppercase tracking-wider mb-4">
            {flowSection.heading}
          </h2>

          {/* Styled flow steps */}
          {flowSteps.length > 2 && (
            <div className="flex flex-wrap gap-2 mb-4">
              {flowSteps.map((step, i) => {
                const styles = stepTypeStyles[step.type];
                return (
                  <div key={i} className="flex items-center gap-2">
                    <div
                      className={`flex items-center gap-2 ${styles.bg} border ${styles.border} rounded-lg px-3 py-2`}
                    >
                      <svg className={`w-3.5 h-3.5 ${styles.text} shrink-0`} fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1.5}>
                        <path strokeLinecap="round" strokeLinejoin="round" d={styles.icon} />
                      </svg>
                      <span className={`text-[12px] font-medium ${styles.text}`}>
                        {step.label.replace(/\(.*?\)/g, '').trim()}
                      </span>
                    </div>
                    {i < flowSteps.length - 1 && (
                      <svg className="w-3.5 h-3.5 text-zinc-600 shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                        <path strokeLinecap="round" strokeLinejoin="round" d="M9 5l7 7-7 7" />
                      </svg>
                    )}
                  </div>
                );
              })}
            </div>
          )}

          {/* ASCII diagram in styled pre block */}
          {flowDiagram && (
            <div className="bg-[#18181b] border border-zinc-800 rounded-lg overflow-x-auto">
              <pre className="p-4 text-[12px] leading-relaxed font-mono text-zinc-400 whitespace-pre">
                {flowDiagram}
              </pre>
            </div>
          )}
        </div>
      )}

      {/* Table sections (Configuration, Variables, Outputs, etc.) */}
      {tableSections.map((section, idx) => {
        const table = parseTableData(section.content);
        if (!table) return null;

        // Split content into pre-table text and table
        const preTableText = section.content
          .split('\n')
          .filter((l) => !l.trim().startsWith('|') && !l.trim().match(/^[-|:]+$/))
          .join('\n')
          .trim();

        return (
          <div key={idx}>
            <h2 className="text-sm font-semibold text-zinc-500 uppercase tracking-wider mb-4">
              {section.heading}
            </h2>

            {preTableText && (
              <div className="text-[14px] text-zinc-400 leading-relaxed mb-3 prose prose-invert prose-sm max-w-none prose-p:text-zinc-400 prose-code:text-amber-300 prose-code:text-[12px] prose-code:bg-zinc-800/80 prose-code:px-1.5 prose-code:py-0.5 prose-code:rounded">
                <Markdown remarkPlugins={[remarkGfm]} rehypePlugins={[rehypeRaw]}>
                  {preTableText}
                </Markdown>
              </div>
            )}

            <div className="bg-zinc-900/50 border border-zinc-800 rounded-xl overflow-hidden">
              <div className="overflow-x-auto">
                <table className="w-full text-[13px]">
                  <thead>
                    <tr className="bg-zinc-800/50">
                      {table.headers.map((h, hi) => (
                        <th key={hi} className="text-left text-zinc-400 font-medium px-4 py-2.5 border-b border-zinc-800">
                          {h}
                        </th>
                      ))}
                    </tr>
                  </thead>
                  <tbody>
                    {table.rows.map((row, ri) => (
                      <tr key={ri} className="hover:bg-zinc-800/30 transition-colors">
                        {row.map((cell, ci) => (
                          <td key={ci} className="px-4 py-2.5 text-zinc-300 border-b border-zinc-800/50">
                            <span
                              dangerouslySetInnerHTML={{
                                __html: cell
                                  .replace(/`([^`]+)`/g, '<code class="text-amber-300 text-[12px] bg-zinc-800/80 px-1.5 py-0.5 rounded">$1</code>')
                                  .replace(/\*\*([^*]+)\*\*/g, '<strong class="text-zinc-100 font-medium">$1</strong>')
                                  .replace(/\[([^\]]+)\]\(([^)]+)\)/g, '<a href="$2" class="text-blue-400 hover:underline">$1</a>')
                                  .replace(/--/g, '\u2014'),
                              }}
                            />
                          </td>
                        ))}
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </div>
          </div>
        );
      })}

      {/* Fallback sections */}
      {fallbackSections.map((section, i) => (
        <div key={i}>
          <h2 className="text-sm font-semibold text-zinc-500 uppercase tracking-wider mb-4">
            {section.heading}
          </h2>
          <div className="bg-zinc-900/50 border border-zinc-800 rounded-xl p-5">
            <div className="prose prose-invert prose-sm max-w-none prose-p:text-[15px] prose-p:text-zinc-300 prose-a:text-blue-400 prose-a:no-underline hover:prose-a:underline prose-strong:text-zinc-100 prose-code:text-amber-300 prose-code:text-[12px] prose-code:bg-zinc-800/80 prose-code:px-1.5 prose-code:py-0.5 prose-code:rounded prose-pre:bg-[#18181b] prose-pre:border prose-pre:border-zinc-800 prose-pre:rounded-lg prose-pre:text-[13px] prose-li:text-[14px] prose-li:text-zinc-300">
              <Markdown remarkPlugins={[remarkGfm]} rehypePlugins={[rehypeRaw]}>
                {section.content}
              </Markdown>
            </div>
          </div>
        </div>
      ))}

      {/* Related Workflows */}
      {relatedWorkflows.length > 0 && (
        <div>
          <h2 className="text-sm font-semibold text-zinc-500 uppercase tracking-wider mb-4">
            Related Workflows
          </h2>
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
            {relatedWorkflows.map((rw) => {
              const entry = workflowEntries.find(
                (e) =>
                  e.path.includes(rw.slug) ||
                  e.title.toLowerCase().includes(rw.name.toLowerCase().replace('workflow', '').trim())
              );
              const path = entry ? entry.path : `/workflows/${rw.slug}`;
              const name = entry ? entry.title.replace(/^Workflow:\s*/, '') : rw.name;

              return (
                <Link
                  key={rw.slug}
                  to={path}
                  className="group flex items-center gap-3 bg-zinc-900/50 border border-zinc-800 rounded-xl p-4 hover:bg-zinc-800/40 hover:border-zinc-700 transition-all"
                >
                  <div className="w-8 h-8 rounded-lg bg-blue-500/10 border border-blue-500/20 flex items-center justify-center shrink-0">
                    <svg className="w-4 h-4 text-blue-400" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1.5}>
                      <path strokeLinecap="round" strokeLinejoin="round" d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15" />
                    </svg>
                  </div>
                  <div className="min-w-0">
                    <span className="text-[14px] text-zinc-300 group-hover:text-white transition-colors">
                      {name}
                    </span>
                  </div>
                  <svg className="w-4 h-4 text-zinc-600 group-hover:text-zinc-400 ml-auto shrink-0 transition-colors" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                    <path strokeLinecap="round" strokeLinejoin="round" d="M9 5l7 7-7 7" />
                  </svg>
                </Link>
              );
            })}
          </div>
        </div>
      )}

      {/* Prev/Next Navigation */}
      {(prevWorkflow || nextWorkflow) && (
        <div className="flex items-stretch gap-3 mt-12 pt-8 border-t border-zinc-800">
          {prevWorkflow ? (
            <Link
              to={prevWorkflow.path}
              className="group flex-1 flex flex-col items-start gap-1 p-4 bg-zinc-900/30 border border-zinc-800/60 rounded-xl hover:bg-zinc-800/40 hover:border-zinc-700 transition-all"
            >
              <span className="text-[11px] text-zinc-600 uppercase tracking-wider flex items-center gap-1">
                <svg className="w-3 h-3" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                  <path strokeLinecap="round" strokeLinejoin="round" d="M15 19l-7-7 7-7" />
                </svg>
                Previous
              </span>
              <span className="text-[13px] text-zinc-400 group-hover:text-zinc-200 transition-colors line-clamp-1">
                {prevWorkflow.title.replace(/^Workflow:\s*/, '')}
              </span>
            </Link>
          ) : (
            <div className="flex-1" />
          )}
          {nextWorkflow ? (
            <Link
              to={nextWorkflow.path}
              className="group flex-1 flex flex-col items-end gap-1 p-4 bg-zinc-900/30 border border-zinc-800/60 rounded-xl hover:bg-zinc-800/40 hover:border-zinc-700 transition-all text-right"
            >
              <span className="text-[11px] text-zinc-600 uppercase tracking-wider flex items-center gap-1">
                Next
                <svg className="w-3 h-3" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                  <path strokeLinecap="round" strokeLinejoin="round" d="M9 5l7 7-7 7" />
                </svg>
              </span>
              <span className="text-[13px] text-zinc-400 group-hover:text-zinc-200 transition-colors line-clamp-1">
                {nextWorkflow.title.replace(/^Workflow:\s*/, '')}
              </span>
            </Link>
          ) : (
            <div className="flex-1" />
          )}
        </div>
      )}
    </div>
  );
}
