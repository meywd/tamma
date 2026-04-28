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

interface ChecklistItem {
  checked: boolean;
  text: string;
  indent: number;
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

function parseChecklist(content: string): ChecklistItem[] {
  const items: ChecklistItem[] = [];
  for (const line of content.split('\n')) {
    const match = line.match(/^(\s*)-\s*\[([ xX])\]\s+(.+)/);
    if (match) {
      items.push({
        checked: match[2].toLowerCase() === 'x',
        text: match[3].trim(),
        indent: Math.floor((match[1].length || 0) / 2),
      });
    }
  }
  return items;
}

function parseNumberedList(content: string): string[] {
  const items: string[] = [];
  for (const line of content.split('\n')) {
    const match = line.match(/^\d+\.\s+(.+)/);
    if (match) {
      items.push(match[1].trim());
    }
  }
  return items;
}

function extractFilePaths(content: string): string[] {
  const paths: string[] = [];
  const seen = new Set<string>();
  // Match common file path patterns
  const regex = /`((?:[\w@.-]+\/)+[\w.-]+\.[a-z]{1,5})`/g;
  let match;
  while ((match = regex.exec(content)) !== null) {
    if (!seen.has(match[1])) {
      seen.add(match[1]);
      paths.push(match[1]);
    }
  }
  return paths;
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

function extractStoryId(title: string): string {
  const match = title.match(/Story\s+([\d.-]+)/i);
  return match ? match[1] : '';
}

function extractStoryName(title: string): string {
  const match = title.match(/:\s*(.+)/);
  return match ? match[1] : title;
}

function getStoryStatusFromContent(content: string): string {
  // Check first few lines for status
  const lines = content.split('\n').slice(0, 5);
  for (const line of lines) {
    if (line.toLowerCase().startsWith('status:')) {
      return line.replace(/^status:\s*/i, '').trim();
    }
  }
  return '';
}

function getStatusStyle(status: string): {
  label: string;
  badgeClass: string;
  dotClass: string;
} {
  const lower = status.toLowerCase();
  if (lower.includes('done') || lower.includes('complete')) {
    return {
      label: 'Completed',
      badgeClass: 'bg-emerald-500/10 text-emerald-400 border border-emerald-500/20',
      dotClass: 'bg-emerald-500',
    };
  }
  if (lower.includes('progress') || lower.includes('started')) {
    return {
      label: 'In Progress',
      badgeClass: 'bg-amber-500/10 text-amber-400 border border-amber-500/20',
      dotClass: 'bg-amber-500',
    };
  }
  if (lower.includes('ready')) {
    return {
      label: 'Ready for Dev',
      badgeClass: 'bg-blue-500/10 text-blue-400 border border-blue-500/20',
      dotClass: 'bg-blue-500',
    };
  }
  return {
    label: status || 'Planned',
    badgeClass: 'bg-zinc-500/10 text-zinc-400 border border-zinc-500/20',
    dotClass: 'bg-zinc-600',
  };
}

export default function StoryDetailPage() {
  const { epic, story } = useParams();
  const [content, setContent] = useState('');
  const [title, setTitle] = useState('');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(false);
  const [manifest, setManifest] = useState<ManifestEntry[]>([]);

  useEffect(() => {
    fetch('/content/manifest.json')
      .then(async (r) => r.json())
      .then((data: ManifestEntry[]) => setManifest(data))
      .catch(() => {});
  }, []);

  useEffect(() => {
    setLoading(true);
    setError(false);
    fetch(`/content/stories/${epic}/${story}.md`)
      .then(async (res) => {
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
  }, [epic, story]);

  useEffect(() => {
    if (title) document.title = `${title} — Tamma Docs`;
  }, [title]);

  const storyId = useMemo(() => extractStoryId(title), [title]);
  const storyName = useMemo(() => extractStoryName(title), [title]);
  const storyStatus = useMemo(() => getStoryStatusFromContent(content), [content]);
  const statusStyle = useMemo(() => getStatusStyle(storyStatus), [storyStatus]);

  const sections = useMemo(() => parseSections(content), [content]);

  // Find the epic section name from manifest
  const epicSection = useMemo(() => {
    const entry = manifest.find((e) => e.path === `/stories/${epic}/${story}`);
    return entry?.section || '';
  }, [manifest, epic, story]);

  // Find the epic overview path
  const epicOverviewPath = useMemo(() => {
    if (!epicSection) return null;
    const overview = manifest.find(
      (e) => e.section === epicSection && /^\/stories\/epic-[\d.-]+$/.test(e.path)
    );
    return overview?.path || null;
  }, [manifest, epicSection]);

  // Acceptance Criteria section
  const acSection = useMemo(
    () =>
      sections.find(
        (s) =>
          s.heading.toLowerCase().includes('acceptance') ||
          s.heading.toLowerCase().includes('criteria')
      ),
    [sections]
  );
  const acceptanceCriteria = useMemo(() => {
    if (!acSection) return [];
    // Try numbered list first
    const numbered = parseNumberedList(acSection.content);
    if (numbered.length > 0) return numbered;
    // Fall back to bullet points
    return acSection.content
      .split('\n')
      .filter((l) => l.match(/^[-*]\s/))
      .map((l) => l.replace(/^[-*]\s+/, '').trim());
  }, [acSection]);

  // Tasks section with checkboxes
  const tasksSection = useMemo(
    () =>
      sections.find(
        (s) =>
          s.heading.toLowerCase().includes('task') &&
          (s.content.includes('- [') || s.content.includes('- [ ]'))
      ),
    [sections]
  );
  const taskItems = useMemo(
    () => (tasksSection ? parseChecklist(tasksSection.content) : []),
    [tasksSection]
  );
  const tasksDone = useMemo(() => taskItems.filter((t) => t.checked && t.indent === 0).length, [taskItems]);
  const tasksTotal = useMemo(() => taskItems.filter((t) => t.indent === 0).length, [taskItems]);

  // Story section (user story)
  const storySection = useMemo(
    () => sections.find((s) => s.heading.toLowerCase() === 'story'),
    [sections]
  );

  // Dev Notes / Technical Context sections
  const technicalSections = useMemo(
    () =>
      sections.filter(
        (s) =>
          s.heading.toLowerCase().includes('dev notes') ||
          s.heading.toLowerCase().includes('technical') ||
          s.heading.toLowerCase().includes('project structure') ||
          s.heading.toLowerCase().includes('requirements context')
      ),
    [sections]
  );

  // Dependencies section
  const dependenciesSection = useMemo(
    () =>
      sections.find(
        (s) =>
          s.heading.toLowerCase().includes('dependenc') ||
          s.heading.toLowerCase().includes('prerequisite')
      ),
    [sections]
  );

  // File paths referenced
  const filePaths = useMemo(() => extractFilePaths(content), [content]);

  // Table sections
  const tableSections = useMemo(
    () =>
      sections.filter(
        (s) =>
          s.content.includes('|') &&
          s.content.split('|').length > 4 &&
          s.level === 2
      ),
    [sections]
  );

  // Sections handled specially
  const handledHeadings = useMemo(() => {
    const set = new Set<string>();
    if (acSection) set.add(acSection.heading);
    if (tasksSection) set.add(tasksSection.heading);
    if (storySection) set.add(storySection.heading);
    if (dependenciesSection) set.add(dependenciesSection.heading);
    for (const s of technicalSections) set.add(s.heading);
    for (const s of tableSections) set.add(s.heading);
    // Skip mandatory process reminders
    sections
      .filter((s) => s.heading.toLowerCase().includes('mandatory') || s.heading.toLowerCase().includes('before you code'))
      .forEach((s) => set.add(s.heading));
    return set;
  }, [acSection, tasksSection, storySection, dependenciesSection, technicalSections, tableSections, sections]);

  // Remaining sections
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

  // Sibling stories for prev/next
  const siblingStories = useMemo(
    () =>
      epicSection
        ? manifest
            .filter((e) => e.section === epicSection)
            .sort((a, b) => a.title.localeCompare(b.title))
        : [],
    [manifest, epicSection]
  );
  const currentIdx = useMemo(
    () => siblingStories.findIndex((e) => e.path === `/stories/${epic}/${story}`),
    [siblingStories, epic, story]
  );
  const prevStory = currentIdx > 0 ? siblingStories[currentIdx - 1] : null;
  const nextStory = currentIdx < siblingStories.length - 1 ? siblingStories[currentIdx + 1] : null;

  if (loading) {
    return (
      <div className="animate-pulse space-y-6 py-8">
        <div className="h-4 bg-zinc-800/30 rounded w-64 mb-6" />
        <div className="h-8 bg-zinc-800/50 rounded w-96" />
        <div className="h-4 bg-zinc-800/30 rounded w-full" />
        <div className="h-4 bg-zinc-800/30 rounded w-5/6" />
        <div className="space-y-2 mt-6">
          {Array.from({ length: 5 }).map((_, i) => (
            <div key={i} className="h-8 bg-zinc-900/50 rounded-lg border border-zinc-800" />
          ))}
        </div>
      </div>
    );
  }

  if (error) {
    return (
      <div className="py-16 text-center">
        <div className="text-6xl mb-4">404</div>
        <div className="text-zinc-500 text-lg">Story not found</div>
        <Link
          to="/stories"
          className="inline-flex items-center gap-1.5 mt-6 text-sm text-blue-400 hover:text-blue-300 transition-colors"
        >
          <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
            <path strokeLinecap="round" strokeLinejoin="round" d="M15 19l-7-7 7-7" />
          </svg>
          Back to Stories
        </Link>
      </div>
    );
  }

  return (
    <div className="space-y-8">
      {/* Breadcrumbs */}
      <nav className="flex items-center gap-1.5 text-[13px] text-zinc-500 flex-wrap">
        <Link to="/" className="hover:text-zinc-300 transition-colors">Home</Link>
        <ChevronSeparator />
        <Link to="/stories" className="hover:text-zinc-300 transition-colors">Stories</Link>
        {epicSection && (
          <>
            <ChevronSeparator />
            {epicOverviewPath ? (
              <Link to={epicOverviewPath} className="hover:text-zinc-300 transition-colors">
                {epicSection}
              </Link>
            ) : (
              <span className="text-zinc-500">{epicSection}</span>
            )}
          </>
        )}
        <ChevronSeparator />
        <span className="text-zinc-400 truncate max-w-xs">
          {storyId ? `Story ${storyId}` : storyName}
        </span>
      </nav>

      {/* Header */}
      <div className="space-y-3">
        <div className="flex items-start gap-3">
          {storyId && (
            <span className="text-sm font-mono font-bold text-zinc-500 bg-zinc-800/60 px-2.5 py-1 rounded-lg shrink-0 mt-1">
              {storyId}
            </span>
          )}
          <div className="min-w-0">
            <h1 className="text-2xl sm:text-3xl font-bold text-white tracking-tight leading-tight">
              {storyName}
            </h1>
            <div className="flex items-center gap-3 mt-2 flex-wrap">
              <span className={`inline-flex items-center gap-1.5 text-xs font-medium px-2.5 py-1 rounded-full ${statusStyle.badgeClass}`}>
                <span className={`w-1.5 h-1.5 rounded-full ${statusStyle.dotClass}`} />
                {statusStyle.label}
              </span>
              {epicSection && (
                <span className="text-xs text-zinc-600">
                  {epicSection}
                </span>
              )}
            </div>
          </div>
        </div>

        {/* User Story */}
        {storySection && (
          <div className="bg-blue-500/5 border border-blue-500/10 rounded-xl p-4 mt-2">
            <div className="text-[14px] text-zinc-300 leading-relaxed italic">
              <Markdown remarkPlugins={[remarkGfm]} rehypePlugins={[rehypeRaw]}>
                {storySection.content}
              </Markdown>
            </div>
          </div>
        )}
      </div>

      {/* Tasks Progress */}
      {tasksTotal > 0 && (
        <div className="bg-zinc-900/50 border border-zinc-800 rounded-xl p-5">
          <div className="flex items-center justify-between mb-3">
            <h3 className="text-sm font-semibold text-zinc-500 uppercase tracking-wider">Task Progress</h3>
            <span className="text-sm text-zinc-400">
              {tasksDone}/{tasksTotal} tasks
              <span className="text-zinc-600 ml-1.5">
                ({tasksTotal > 0 ? Math.round((tasksDone / tasksTotal) * 100) : 0}%)
              </span>
            </span>
          </div>
          <div className="w-full h-2.5 bg-zinc-800 rounded-full overflow-hidden">
            <div
              className="h-full rounded-full bg-emerald-500 transition-all duration-500"
              style={{ width: `${tasksTotal > 0 ? (tasksDone / tasksTotal) * 100 : 0}%` }}
            />
          </div>
        </div>
      )}

      {/* Acceptance Criteria */}
      {acceptanceCriteria.length > 0 && (
        <div>
          <h2 className="text-sm font-semibold text-zinc-500 uppercase tracking-wider mb-4">
            Acceptance Criteria
          </h2>
          <div className="bg-zinc-900/50 border border-zinc-800 rounded-xl divide-y divide-zinc-800/60">
            {acceptanceCriteria.map((item, i) => (
              <div key={i} className="flex items-start gap-3 px-4 py-3">
                <span className="w-6 h-6 rounded-full bg-blue-500/10 border border-blue-500/20 flex items-center justify-center text-[11px] font-medium text-blue-400 shrink-0 mt-0.5">
                  {i + 1}
                </span>
                <span className="text-[14px] text-zinc-300 leading-relaxed">
                  <InlineMarkdown>{item}</InlineMarkdown>
                </span>
              </div>
            ))}
          </div>
        </div>
      )}

      {/* Tasks / Subtasks */}
      {taskItems.length > 0 && (
        <div>
          <h2 className="text-sm font-semibold text-zinc-500 uppercase tracking-wider mb-4">
            {tasksSection?.heading || 'Tasks'}
          </h2>
          <div className="bg-zinc-900/50 border border-zinc-800 rounded-xl overflow-hidden">
            <div className="divide-y divide-zinc-800/40">
              {taskItems.map((item, i) => (
                <div
                  key={i}
                  className="flex items-start gap-3 px-4 py-2.5 hover:bg-zinc-800/20 transition-colors"
                  style={{ paddingLeft: `${1 + item.indent * 1.25}rem` }}
                >
                  {item.checked ? (
                    <svg className="w-4.5 h-4.5 text-emerald-500 shrink-0 mt-0.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                      <path strokeLinecap="round" strokeLinejoin="round" d="M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z" />
                    </svg>
                  ) : (
                    <div className="w-4.5 h-4.5 rounded-full border-2 border-zinc-700 shrink-0 mt-0.5" />
                  )}
                  <span
                    className={`text-[13px] leading-relaxed ${
                      item.checked ? 'text-zinc-500 line-through' : 'text-zinc-300'
                    } ${item.indent > 0 ? 'text-[12px]' : 'font-medium'}`}
                  >
                    {(() => {
                      const acMatch = item.text.match(/\(AC:\s*([\d,\s]+)\)\s*$/);
                      const body = acMatch
                        ? item.text.slice(0, acMatch.index).trim()
                        : item.text;
                      return (
                        <>
                          <InlineMarkdown>{body}</InlineMarkdown>
                          {acMatch && (
                            <span className="text-[11px] text-zinc-600 ml-1">
                              (AC: {acMatch[1]})
                            </span>
                          )}
                        </>
                      );
                    })()}
                  </span>
                </div>
              ))}
            </div>
          </div>
        </div>
      )}

      {/* Table sections (Change Log, etc.) */}
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

      {/* Dependencies */}
      {dependenciesSection && (
        <div>
          <h2 className="text-sm font-semibold text-zinc-500 uppercase tracking-wider mb-4">
            Dependencies
          </h2>
          <div className="bg-zinc-900/50 border border-zinc-800 rounded-xl p-5">
            <div className="prose prose-invert prose-sm max-w-none prose-p:text-zinc-300 prose-a:text-blue-400 prose-a:no-underline hover:prose-a:underline prose-li:text-[14px] prose-li:text-zinc-300 prose-code:text-amber-300 prose-code:text-[12px] prose-code:bg-zinc-800/80 prose-code:px-1.5 prose-code:py-0.5 prose-code:rounded">
              <Markdown remarkPlugins={[remarkGfm]} rehypePlugins={[rehypeRaw]}>
                {dependenciesSection.content}
              </Markdown>
            </div>
          </div>
        </div>
      )}

      {/* Technical Context */}
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
                <div className="prose prose-invert prose-sm max-w-none prose-p:text-[14px] prose-p:text-zinc-300 prose-a:text-blue-400 prose-a:no-underline hover:prose-a:underline prose-strong:text-zinc-100 prose-code:text-amber-300 prose-code:text-[12px] prose-code:bg-zinc-800/80 prose-code:px-1.5 prose-code:py-0.5 prose-code:rounded prose-pre:bg-[#18181b] prose-pre:border prose-pre:border-zinc-800 prose-pre:rounded-lg prose-pre:text-[13px] prose-li:text-[14px] prose-li:text-zinc-300">
                  <Markdown remarkPlugins={[remarkGfm]} rehypePlugins={[rehypeRaw]}>
                    {section.content}
                  </Markdown>
                </div>
              </div>
            ))}
          </div>
        </div>
      )}

      {/* Referenced Files */}
      {filePaths.length > 0 && (
        <div>
          <h2 className="text-sm font-semibold text-zinc-500 uppercase tracking-wider mb-4">
            Referenced Files
          </h2>
          <div className="bg-zinc-900/50 border border-zinc-800 rounded-xl divide-y divide-zinc-800/40">
            {filePaths.slice(0, 20).map((fp, i) => (
              <div key={i} className="flex items-center gap-2.5 px-4 py-2.5">
                <svg className="w-4 h-4 text-zinc-600 shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={1.5}>
                  <path strokeLinecap="round" strokeLinejoin="round" d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />
                </svg>
                <code className="text-[12px] font-mono text-zinc-400">{fp}</code>
              </div>
            ))}
            {filePaths.length > 20 && (
              <div className="px-4 py-2.5 text-[12px] text-zinc-600">
                +{filePaths.length - 20} more files
              </div>
            )}
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
            <div className="prose prose-invert prose-sm max-w-none prose-p:text-[15px] prose-p:text-zinc-300 prose-a:text-blue-400 prose-a:no-underline hover:prose-a:underline prose-strong:text-zinc-100 prose-code:text-amber-300 prose-code:text-[12px] prose-code:bg-zinc-800/80 prose-code:px-1.5 prose-code:py-0.5 prose-code:rounded prose-pre:bg-[#18181b] prose-pre:border prose-pre:border-zinc-800 prose-pre:rounded-lg prose-pre:text-[13px] prose-li:text-[14px] prose-li:text-zinc-300 prose-table:text-[13px] [&_table]:border [&_table]:border-zinc-800 [&_table]:rounded-lg [&_table]:overflow-hidden [&_thead]:bg-zinc-800/50 prose-th:text-zinc-300 prose-th:font-medium prose-th:border-zinc-700 prose-th:px-3 prose-th:py-2 prose-td:border-zinc-800 prose-td:px-3 prose-td:py-2">
              <Markdown remarkPlugins={[remarkGfm]} rehypePlugins={[rehypeRaw]}>
                {section.content}
              </Markdown>
            </div>
          </div>
        </div>
      ))}

      {/* Prev/Next Navigation */}
      {(prevStory || nextStory) && (
        <div className="flex items-stretch gap-3 mt-12 pt-8 border-t border-zinc-800">
          {prevStory ? (
            <Link
              to={prevStory.path}
              className="group flex-1 flex flex-col items-start gap-1 p-4 bg-zinc-900/30 border border-zinc-800/60 rounded-xl hover:bg-zinc-800/40 hover:border-zinc-700 transition-all"
            >
              <span className="text-[11px] text-zinc-600 uppercase tracking-wider flex items-center gap-1">
                <svg className="w-3 h-3" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                  <path strokeLinecap="round" strokeLinejoin="round" d="M15 19l-7-7 7-7" />
                </svg>
                Previous
              </span>
              <span className="text-[13px] text-zinc-400 group-hover:text-zinc-200 transition-colors line-clamp-1">
                {prevStory.title}
              </span>
            </Link>
          ) : (
            <div className="flex-1" />
          )}
          {nextStory ? (
            <Link
              to={nextStory.path}
              className="group flex-1 flex flex-col items-end gap-1 p-4 bg-zinc-900/30 border border-zinc-800/60 rounded-xl hover:bg-zinc-800/40 hover:border-zinc-700 transition-all text-right"
            >
              <span className="text-[11px] text-zinc-600 uppercase tracking-wider flex items-center gap-1">
                Next
                <svg className="w-3 h-3" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                  <path strokeLinecap="round" strokeLinejoin="round" d="M9 5l7 7-7 7" />
                </svg>
              </span>
              <span className="text-[13px] text-zinc-400 group-hover:text-zinc-200 transition-colors line-clamp-1">
                {nextStory.title}
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

function ChevronSeparator() {
  return (
    <svg className="w-3 h-3 text-zinc-700" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
      <path strokeLinecap="round" strokeLinejoin="round" d="M9 5l7 7-7 7" />
    </svg>
  );
}
