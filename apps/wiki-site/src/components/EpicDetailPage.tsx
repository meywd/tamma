import { useEffect, useState, useMemo } from 'react';
import { useParams, Link } from 'react-router';
import Markdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import rehypeRaw from 'rehype-raw';
import InlineMarkdown from './InlineMarkdown';

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

interface StoryEntry {
  id: string;
  title: string;
  status: string;
  description: string;
  link: string | null;
  taskCount: string | null;
}

// Same status sets as EpicsPage for consistency
// Updated 2026-04-21 — Epic 19 complete; Epic 12 partial; Epic 22 absorbed by 19;
// Epic 27/28/29/30/31/33 newly scoped (drafted); Epic 33 deferred.
const completedEpics = new Set([
  '7', '8', '9', '11', '13', '14', '15', '16', '19', '25',
]);
const inProgressEpics = new Set(['1', '1.5', '2', '3', '4', '5', '6', '12', '17', '18', '21', '22', '23', '24', '26']);

function parseEpicNumberFromSlug(slug: string): string {
  // "1-foundation" -> "1", "1-5-infrastructure" -> "1.5", "11-14-elsa" -> "11-14"
  const match = slug.match(/^(\d+(?:-\d+)?)/);
  if (!match) return '';
  const raw = match[1];
  // "1-5" -> "1.5" for lookup, but "11-14" stays "11-14"
  if (raw === '1-5') return '1.5';
  return raw;
}

function getEpicSectionKey(epicNumber: string): string {
  if (epicNumber === '1.5') return 'Epic 1-5';
  return `Epic ${epicNumber}`;
}

function getEpicStatus(num: string): 'done' | 'in-progress' | 'drafted' {
  if (completedEpics.has(num)) return 'done';
  if (inProgressEpics.has(num)) return 'in-progress';
  return 'drafted';
}

const statusConfig = {
  done: {
    label: 'Completed',
    badgeClass: 'bg-emerald-500/10 text-emerald-400 border border-emerald-500/20',
    dotClass: 'bg-emerald-500',
    barClass: 'bg-emerald-500',
  },
  'in-progress': {
    label: 'In Progress',
    badgeClass: 'bg-amber-500/10 text-amber-400 border border-amber-500/20',
    dotClass: 'bg-amber-500',
    barClass: 'bg-amber-500',
  },
  drafted: {
    label: 'Planned',
    badgeClass: 'bg-zinc-500/10 text-zinc-400 border border-zinc-500/20',
    dotClass: 'bg-zinc-600',
    barClass: 'bg-zinc-600',
  },
};

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

function parseStoriesFromMarkdown(markdown: string): StoryEntry[] {
  const stories: StoryEntry[] = [];
  // Match ### Story X-Y: Title patterns followed by **Status:** and description
  const storyRegex = /###\s+Story\s+([\d.-]+):\s*(.+)\n\*\*Status:\*\*\s*(.+?)(?:\n|$)([\s\S]*?)(?=(?:###\s+Story|---\s*$|$))/gm;
  let match;
  while ((match = storyRegex.exec(markdown)) !== null) {
    const statusLine = match[3].trim();
    const statusParts = statusLine.split('|').map((s: string) => s.trim());
    const status = statusParts[0] || statusLine;
    const taskMatch = statusParts.find((p: string) => p.includes('Tasks:'));
    const taskCount = taskMatch ? taskMatch.replace('**Tasks:**', '').trim() : null;

    const body = match[4].trim();
    // Extract description (first non-empty line that isn't a link)
    const descLines = body.split('\n').filter((l: string) => l.trim() && !l.trim().startsWith('-'));
    const description = descLines[0] || '';

    // Extract link
    const linkMatch = body.match(/\[.*?\]\((.*?)\)/);
    const link = linkMatch ? linkMatch[1] : null;

    stories.push({
      id: match[1],
      title: match[2].trim(),
      status,
      description,
      link,
      taskCount,
    });
  }
  return stories;
}

function parseKeyDeliverables(content: string): string[] {
  // Extract bullet points from content
  const items: string[] = [];
  for (const line of content.split('\n')) {
    const bulletMatch = line.match(/^[-*]\s+(.+)/);
    if (bulletMatch) {
      items.push(bulletMatch[1].trim());
    }
  }
  return items;
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
  // Skip separator line
  const rows = lines.slice(2).map(parseRow);
  return { headers, rows };
}

const deliverableIcons: Record<string, string> = {
  interface: 'M8 9l3 3-3 3m5 0h3M5 20h14a2 2 0 002-2V6a2 2 0 00-2-2H5a2 2 0 00-2 2v12a2 2 0 002 2z',
  provider: 'M13 10V3L4 14h7v7l9-11h-7z',
  platform: 'M3 15a4 4 0 004 4h9a5 5 0 10-.1-9.999 5.002 5.002 0 10-9.78 2.096A4.001 4.001 0 003 15z',
  cli: 'M8 9l3 3-3 3m5 0h3M5 20h14a2 2 0 002-2V6a2 2 0 00-2-2H5a2 2 0 00-2 2v12a2 2 0 002 2z',
  config: 'M10.325 4.317c.426-1.756 2.924-1.756 3.35 0a1.724 1.724 0 002.573 1.066c1.543-.94 3.31.826 2.37 2.37a1.724 1.724 0 001.066 2.573c1.756.426 1.756 2.924 0 3.35a1.724 1.724 0 00-1.066 2.573c.94 1.543-.826 3.31-2.37 2.37a1.724 1.724 0 00-2.573 1.066c-.426 1.756-2.924 1.756-3.35 0a1.724 1.724 0 00-2.573-1.066c-1.543.94-3.31-.826-2.37-2.37a1.724 1.724 0 00-1.066-2.573c-1.756-.426-1.756-2.924 0-3.35a1.724 1.724 0 001.066-2.573c-.94-1.543.826-3.31 2.37-2.37.996.608 2.296.07 2.572-1.065z',
  default: 'M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z',
};

function getDeliverableIcon(text: string): string {
  const lower = text.toLowerCase();
  if (lower.includes('interface') || lower.includes('contract')) return deliverableIcons.interface;
  if (lower.includes('provider') || lower.includes('ai')) return deliverableIcons.provider;
  if (lower.includes('platform') || lower.includes('git')) return deliverableIcons.platform;
  if (lower.includes('cli') || lower.includes('command')) return deliverableIcons.cli;
  if (lower.includes('config') || lower.includes('setting')) return deliverableIcons.config;
  return deliverableIcons.default;
}

function getStoryStatus(status: string): 'done' | 'in-progress' | 'drafted' {
  const lower = status.toLowerCase();
  if (lower.includes('done') || lower.includes('complete')) return 'done';
  if (lower.includes('progress') || lower.includes('started') || lower.includes('near')) return 'in-progress';
  return 'drafted';
}

export default function EpicDetailPage() {
  const { slug } = useParams();
  const [content, setContent] = useState('');
  const [title, setTitle] = useState('');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(false);
  const [manifest, setManifest] = useState<ManifestEntry[]>([]);

  const epicNumber = useMemo(() => parseEpicNumberFromSlug(slug || ''), [slug]);
  const epicStatus = useMemo(() => getEpicStatus(epicNumber), [epicNumber]);
  const config = statusConfig[epicStatus];

  useEffect(() => {
    fetch('/content/manifest.json')
      .then((r) => r.json())
      .then((data: ManifestEntry[]) => setManifest(data))
      .catch(() => {});
  }, []);

  useEffect(() => {
    setLoading(true);
    setError(false);
    fetch(`/content/epics/${slug}.md`)
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

  // Parse epic data
  const epicName = useMemo(() => {
    const match = title.match(/:\s*(.+)/);
    return match ? match[1] : title;
  }, [title]);

  const sections = useMemo(() => parseSections(content), [content]);
  const parsedStories = useMemo(() => parseStoriesFromMarkdown(content), [content]);

  // Get stories from manifest for this epic's section
  const sectionKey = useMemo(() => getEpicSectionKey(epicNumber), [epicNumber]);
  const manifestStories = useMemo(
    () => manifest.filter((e) => e.section === sectionKey),
    [manifest, sectionKey]
  );
  const totalManifestStories = manifestStories.length;

  // Count done stories from parsed markdown
  const doneStories = useMemo(
    () => parsedStories.filter((s) => getStoryStatus(s.status) === 'done').length,
    [parsedStories]
  );

  // Extract the metadata line (Status, Stories, etc.)
  const metaLine = useMemo(() => {
    const firstLines = content.split('\n').slice(0, 5);
    return firstLines.filter((l) => l.startsWith('**Status') || l.startsWith('**Stories') || l.startsWith('**Milestone') || l.startsWith('**Packages'));
  }, [content]);

  // Parse description from "Overview" section
  const overviewSection = useMemo(
    () => sections.find((s) => s.heading.toLowerCase().includes('overview')),
    [sections]
  );
  // First paragraph of overview, preserving inline markdown (bold, links, code)
  const overviewFirstParagraph = useMemo(() => {
    if (!overviewSection) return '';
    const blankIdx = overviewSection.content.search(/\n\s*\n/);
    return blankIdx === -1
      ? overviewSection.content
      : overviewSection.content.slice(0, blankIdx);
  }, [overviewSection]);

  // Parse goals
  const goalsSection = useMemo(
    () => sections.find((s) => s.heading.toLowerCase().includes('goal')),
    [sections]
  );
  const goals = useMemo(() => {
    if (!goalsSection) return [];
    return goalsSection.content
      .split('\n')
      .filter((l) => l.match(/^\d+\.\s/))
      .map((l) => l.replace(/^\d+\.\s*/, '').trim());
  }, [goalsSection]);

  // Parse key deliverables/implementation tables
  const implementationSection = useMemo(
    () =>
      sections.find(
        (s) =>
          s.heading.toLowerCase().includes('implementation') ||
          s.heading.toLowerCase().includes('deliverable') ||
          s.heading.toLowerCase().includes('packages')
      ),
    [sections]
  );

  // Find table sections (must have actual markdown table rows starting with |)
  const tableSections = useMemo(
    () =>
      sections.filter((s) => {
        const tableLines = s.content.split('\n').filter((l) => l.trim().startsWith('|'));
        return tableLines.length >= 3; // header + separator + at least one row
      }),
    [sections]
  );

  // Parse status line for story/package counts
  const storyCountFromMeta = useMemo(() => {
    const line = metaLine.find((l) => l.includes('Stories'));
    if (!line) return null;
    const match = line.match(/\*\*Stories:\*\*\s*(.+?)(?:\n|$)/);
    return match ? match[1].trim() : null;
  }, [metaLine]);

  // Find sections that have code blocks for "Technical Context"
  const technicalSections = useMemo(
    () => sections.filter((s) => s.content.includes('```') && !s.heading.toLowerCase().includes('stor')),
    [sections]
  );

  // Sibling epics for prev/next navigation
  const epicEntries = useMemo(
    () => manifest.filter((e) => e.section === 'Epics').sort((a, b) => {
      const aNum = parseFloat(a.path.replace('/epics/', '').replace(/-.*/, '').replace('-', '.'));
      const bNum = parseFloat(b.path.replace('/epics/', '').replace(/-.*/, '').replace('-', '.'));
      return aNum - bNum;
    }),
    [manifest]
  );

  const currentIdx = useMemo(
    () => epicEntries.findIndex((e) => e.path === `/epics/${slug}`),
    [epicEntries, slug]
  );
  const prevEpic = currentIdx > 0 ? epicEntries[currentIdx - 1] : null;
  const nextEpic = currentIdx < epicEntries.length - 1 ? epicEntries[currentIdx + 1] : null;

  // Sections we handle specially
  const specialHeadings = useMemo(() => {
    const set = new Set<string>();
    if (overviewSection) set.add(overviewSection.heading);
    if (goalsSection) set.add(goalsSection.heading);
    for (const s of technicalSections) set.add(s.heading);
    for (const s of tableSections) set.add(s.heading);
    // Stories section
    const storiesSection = sections.find((s) => s.heading.toLowerCase() === 'stories');
    if (storiesSection) set.add(storiesSection.heading);
    // Skip section headings that are parents of handled sub-sections (e.g. "Implementation Summary")
    for (const s of sections) {
      if (s.level === 2 && s.content.trim().length === 0) set.add(s.heading);
    }
    return set;
  }, [overviewSection, goalsSection, technicalSections, tableSections, sections]);

  // Remaining sections to render as markdown fallback
  const fallbackSections = useMemo(
    () => {
      // Collect headings from table sections so we don't double-render
      const tableHeadings = new Set(tableSections.map((s) => s.heading));
      return sections.filter(
        (s) =>
          s.heading &&
          !specialHeadings.has(s.heading) &&
          !tableHeadings.has(s.heading) &&
          s.level === 2 &&
          !s.heading.toLowerCase().includes('stories') &&
          // Skip sections with no meaningful content (empty or only whitespace/sub-heading markers)
          s.content.trim().length > 0 &&
          // Skip sections whose content is just sub-headings with no own text
          !s.content.trim().split('\n').every(line => line.trim() === '' || line.trim().startsWith('### '))
      );
    },
    [sections, specialHeadings, tableSections]
  );

  if (loading) {
    return (
      <div className="animate-pulse space-y-6 py-8">
        <div className="h-4 bg-zinc-800/30 rounded w-48 mb-6" />
        <div className="flex items-center gap-3 mb-4">
          <div className="h-10 w-10 bg-zinc-800/50 rounded-lg" />
          <div className="h-8 bg-zinc-800/50 rounded w-96" />
        </div>
        <div className="h-4 bg-zinc-800/30 rounded w-full" />
        <div className="h-4 bg-zinc-800/30 rounded w-5/6" />
        <div className="h-3 bg-zinc-800/20 rounded-full w-full mt-6" />
        <div className="grid grid-cols-2 gap-3 mt-6">
          {Array.from({ length: 4 }).map((_, i) => (
            <div key={i} className="h-20 bg-zinc-900/50 rounded-xl border border-zinc-800" />
          ))}
        </div>
      </div>
    );
  }

  if (error) {
    return (
      <div className="py-16 text-center">
        <div className="text-6xl mb-4">404</div>
        <div className="text-zinc-500 text-lg">Epic not found</div>
        <Link
          to="/epics"
          className="inline-flex items-center gap-1.5 mt-6 text-sm text-blue-400 hover:text-blue-300 transition-colors"
        >
          <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
            <path strokeLinecap="round" strokeLinejoin="round" d="M15 19l-7-7 7-7" />
          </svg>
          Back to Epics
        </Link>
      </div>
    );
  }

  const progressPercent =
    parsedStories.length > 0
      ? Math.round((doneStories / parsedStories.length) * 100)
      : 0;

  return (
    <div className="space-y-8">
      {/* Breadcrumbs */}
      <nav className="flex items-center gap-1.5 text-[13px] text-zinc-500 flex-wrap">
        <Link to="/" className="hover:text-zinc-300 transition-colors">Home</Link>
        <svg className="w-3 h-3 text-zinc-700" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
          <path strokeLinecap="round" strokeLinejoin="round" d="M9 5l7 7-7 7" />
        </svg>
        <Link to="/epics" className="hover:text-zinc-300 transition-colors">Epics</Link>
        <svg className="w-3 h-3 text-zinc-700" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
          <path strokeLinecap="round" strokeLinejoin="round" d="M9 5l7 7-7 7" />
        </svg>
        <span className="text-zinc-400 truncate max-w-xs">{epicName}</span>
      </nav>

      {/* Header */}
      <div className="space-y-4">
        <div className="flex items-start gap-4">
          <span className="text-lg font-mono font-bold text-zinc-500 bg-zinc-800/60 px-3 py-1.5 rounded-lg shrink-0">
            {epicNumber}
          </span>
          <div className="min-w-0">
            <h1 className="text-2xl sm:text-3xl font-bold text-white tracking-tight leading-tight">
              {epicName}
            </h1>
            <div className="flex items-center gap-3 mt-2 flex-wrap">
              <span className={`inline-flex items-center gap-1.5 text-xs font-medium px-2.5 py-1 rounded-full ${config.badgeClass}`}>
                <span className={`w-1.5 h-1.5 rounded-full ${config.dotClass}`} />
                {config.label}
              </span>
              {storyCountFromMeta && (
                <span className="text-xs text-zinc-500">
                  {storyCountFromMeta}
                </span>
              )}
            </div>
          </div>
        </div>

        {overviewSection && (
          <div className="text-[15px] text-zinc-400 leading-relaxed max-w-3xl line-clamp-4">
            <InlineMarkdown>{overviewFirstParagraph}</InlineMarkdown>
          </div>
        )}
      </div>

      {/* Progress */}
      {parsedStories.length > 0 && (
        <div className="bg-zinc-900/50 border border-zinc-800 rounded-xl p-5">
          <div className="flex items-center justify-between mb-3">
            <h3 className="text-sm font-semibold text-zinc-500 uppercase tracking-wider">Progress</h3>
            <span className="text-sm text-zinc-400">
              {doneStories}/{parsedStories.length} stories
              <span className="text-zinc-600 ml-1.5">({progressPercent}%)</span>
            </span>
          </div>
          <div className="w-full h-2.5 bg-zinc-800 rounded-full overflow-hidden">
            <div
              className={`h-full rounded-full transition-all duration-500 ${config.barClass}`}
              style={{ width: `${progressPercent}%` }}
            />
          </div>
          {totalManifestStories > 0 && (
            <div className="text-xs text-zinc-600 mt-2">
              {totalManifestStories} items in this section (stories, tasks, tech specs)
            </div>
          )}
        </div>
      )}

      {/* Goals */}
      {goals.length > 0 && (
        <div>
          <h2 className="text-sm font-semibold text-zinc-500 uppercase tracking-wider mb-4">Goals</h2>
          <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
            {goals.map((goal, i) => (
              <div
                key={i}
                className="flex items-start gap-3 bg-zinc-900/50 border border-zinc-800 rounded-xl p-4"
              >
                <span className="w-6 h-6 rounded-full bg-blue-500/10 border border-blue-500/20 flex items-center justify-center text-xs font-medium text-blue-400 shrink-0 mt-0.5">
                  {i + 1}
                </span>
                <span className="text-[14px] text-zinc-300 leading-relaxed">
                  <InlineMarkdown>{goal}</InlineMarkdown>
                </span>
              </div>
            ))}
          </div>
        </div>
      )}

      {/* Table sections (Packages, Providers, Platforms, etc.) */}
      {tableSections.map((section, idx) => {
        const table = parseTableData(section.content);
        if (!table) return null;
        return (
          <div key={idx}>
            <h2 className="text-sm font-semibold text-zinc-500 uppercase tracking-wider mb-4">
              {section.heading}
            </h2>
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
                            <InlineMarkdown>{cell}</InlineMarkdown>
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

      {/* Key Deliverables (from overview or goals) */}
      {implementationSection && implementationSection.content.trim().length > 0 && !tableSections.some((s) => s.heading === implementationSection.heading) && (
        <div>
          <h2 className="text-sm font-semibold text-zinc-500 uppercase tracking-wider mb-4">
            {implementationSection.heading}
          </h2>
          {(() => {
            const items = parseKeyDeliverables(implementationSection.content);
            if (items.length > 0) {
              return (
                <div className="grid grid-cols-1 sm:grid-cols-2 gap-3">
                  {items.map((item, i) => (
                    <div
                      key={i}
                      className="flex items-start gap-3 bg-zinc-900/50 border border-zinc-800 rounded-xl p-4 hover:bg-zinc-800/40 hover:border-zinc-700 transition-all"
                    >
                      <div className="w-8 h-8 rounded-lg bg-violet-500/10 border border-violet-500/20 flex items-center justify-center shrink-0">
                        <svg className="w-4 h-4 text-violet-400" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1.5}>
                          <path strokeLinecap="round" strokeLinejoin="round" d={getDeliverableIcon(item)} />
                        </svg>
                      </div>
                      <span className="text-[14px] text-zinc-300 leading-relaxed">
                        <InlineMarkdown>{item}</InlineMarkdown>
                      </span>
                    </div>
                  ))}
                </div>
              );
            }
            // Fallback to markdown
            return (
              <div className="bg-zinc-900/50 border border-zinc-800 rounded-xl p-5">
                <div className="prose prose-invert prose-sm max-w-none prose-p:text-zinc-300 prose-code:text-amber-300 prose-code:text-[12px] prose-code:bg-zinc-800/80 prose-code:px-1.5 prose-code:py-0.5 prose-code:rounded prose-pre:bg-[#18181b] prose-pre:border prose-pre:border-zinc-800 prose-pre:rounded-lg prose-pre:text-[13px]">
                  <Markdown remarkPlugins={[remarkGfm]} rehypePlugins={[rehypeRaw]}>
                    {implementationSection.content}
                  </Markdown>
                </div>
              </div>
            );
          })()}
        </div>
      )}

      {/* Technical Context (sections with code blocks) */}
      {technicalSections.length > 0 && (
        <div>
          <h2 className="text-sm font-semibold text-zinc-500 uppercase tracking-wider mb-4">
            Technical Context
          </h2>
          <div className="space-y-4">
            {technicalSections.map((section, i) => (
              <div key={i} className="bg-zinc-900/50 border border-zinc-800 rounded-xl p-5">
                {section.heading && (
                  <h3 className="text-sm font-medium text-zinc-300 mb-3">{section.heading}</h3>
                )}
                <div className="prose prose-invert prose-sm max-w-none prose-p:text-zinc-300 prose-code:text-amber-300 prose-code:text-[12px] prose-code:bg-zinc-800/80 prose-code:px-1.5 prose-code:py-0.5 prose-code:rounded prose-pre:bg-[#18181b] prose-pre:border prose-pre:border-zinc-800 prose-pre:rounded-lg prose-pre:text-[13px]">
                  <Markdown remarkPlugins={[remarkGfm]} rehypePlugins={[rehypeRaw]}>
                    {section.content}
                  </Markdown>
                </div>
              </div>
            ))}
          </div>
        </div>
      )}

      {/* Stories List */}
      {parsedStories.length > 0 && (
        <div>
          <h2 className="text-sm font-semibold text-zinc-500 uppercase tracking-wider mb-4">
            Stories ({parsedStories.length})
          </h2>
          <div className="border border-zinc-800 rounded-xl overflow-hidden divide-y divide-zinc-800/60">
            {parsedStories.map((story) => {
              const storyStatus = getStoryStatus(story.status);
              const storyConfig = statusConfig[storyStatus];
              // Try to find a matching manifest entry for a link
              const manifestMatch = manifestStories.find((e) =>
                e.title.includes(`Story ${story.id}`) && !e.title.includes('Task')
              );
              const storyPath = manifestMatch?.path || story.link;

              return (
                <div
                  key={story.id}
                  className="flex items-center gap-3 px-4 py-3 hover:bg-zinc-800/20 transition-colors"
                >
                  <span className={`w-2 h-2 rounded-full shrink-0 ${storyConfig.dotClass}`} />
                  <span className="text-xs font-mono text-zinc-600 shrink-0 w-8">
                    {story.id}
                  </span>
                  {storyPath ? (
                    <Link
                      to={storyPath}
                      className="text-[14px] text-zinc-300 hover:text-white transition-colors truncate"
                    >
                      {story.title}
                    </Link>
                  ) : (
                    <span className="text-[14px] text-zinc-300 truncate">{story.title}</span>
                  )}
                  <span className="ml-auto shrink-0">
                    <span className={`text-[11px] font-medium px-2 py-0.5 rounded-full ${storyConfig.badgeClass}`}>
                      {story.status.split('|')[0].replace(/\*\*/g, '').trim()}
                    </span>
                  </span>
                  {story.taskCount && (
                    <span className="text-[11px] text-zinc-600 shrink-0">
                      {story.taskCount} tasks
                    </span>
                  )}
                </div>
              );
            })}
          </div>
        </div>
      )}

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

      {/* Prev/Next Epic Navigation */}
      {(prevEpic || nextEpic) && (
        <div className="flex items-stretch gap-3 mt-12 pt-8 border-t border-zinc-800">
          {prevEpic ? (
            <Link
              to={prevEpic.path}
              className="group flex-1 flex flex-col items-start gap-1 p-4 bg-zinc-900/30 border border-zinc-800/60 rounded-xl hover:bg-zinc-800/40 hover:border-zinc-700 transition-all"
            >
              <span className="text-[11px] text-zinc-600 uppercase tracking-wider flex items-center gap-1">
                <svg className="w-3 h-3" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                  <path strokeLinecap="round" strokeLinejoin="round" d="M15 19l-7-7 7-7" />
                </svg>
                Previous Epic
              </span>
              <span className="text-[13px] text-zinc-400 group-hover:text-zinc-200 transition-colors line-clamp-1">
                {prevEpic.title.replace(/^Epic\s+[\d.]+:\s*/, '')}
              </span>
            </Link>
          ) : (
            <div className="flex-1" />
          )}
          {nextEpic ? (
            <Link
              to={nextEpic.path}
              className="group flex-1 flex flex-col items-end gap-1 p-4 bg-zinc-900/30 border border-zinc-800/60 rounded-xl hover:bg-zinc-800/40 hover:border-zinc-700 transition-all text-right"
            >
              <span className="text-[11px] text-zinc-600 uppercase tracking-wider flex items-center gap-1">
                Next Epic
                <svg className="w-3 h-3" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                  <path strokeLinecap="round" strokeLinejoin="round" d="M9 5l7 7-7 7" />
                </svg>
              </span>
              <span className="text-[13px] text-zinc-400 group-hover:text-zinc-200 transition-colors line-clamp-1">
                {nextEpic.title.replace(/^Epics?\s+[\d.-]+:\s*/, '')}
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
