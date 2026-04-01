import { useEffect, useState } from 'react';
import { Link } from 'react-router';

interface ManifestEntry {
  path: string;
  title: string;
  section: string;
}

interface EpicGroup {
  section: string;
  epicNumber: string;
  epicTitle: string;
  overviewPath: string | null;
  stories: ManifestEntry[];
}

// Status for epics (same as EpicsPage)
const completedEpics = new Set([
  '6', '7', '8', '9', '10', '11', '12', '13', '14', '15', '16', '25',
]);
const inProgressEpics = new Set(['1', '1.5', '2', '3', '17', '18', '19', '23', '24']);

function parseEpicNumber(section: string): string {
  // "Epic 1" -> "1", "Epic 1-5" -> "1.5", "Epic 23" -> "23"
  const match = section.match(/Epic\s+([\d-]+)/);
  if (!match) return '99';
  return match[1].replace('-', '.');
}

function numericSort(a: string, b: string): number {
  const na = parseFloat(a);
  const nb = parseFloat(b);
  return na - nb;
}

function getStatus(epicNum: string): 'done' | 'in-progress' | 'drafted' {
  // Normalize "1-5" to "1.5" for lookup
  const normalized = epicNum.replace('-', '.');
  if (completedEpics.has(normalized)) return 'done';
  if (inProgressEpics.has(normalized)) return 'in-progress';
  return 'drafted';
}

const statusStyles = {
  done: {
    dot: 'bg-emerald-500',
    badge: 'bg-emerald-500/10 text-emerald-400 border-emerald-500/20',
    label: 'Completed',
  },
  'in-progress': {
    dot: 'bg-amber-500',
    badge: 'bg-amber-500/10 text-amber-400 border-amber-500/20',
    label: 'In Progress',
  },
  drafted: {
    dot: 'bg-zinc-600',
    badge: 'bg-zinc-500/10 text-zinc-400 border-zinc-500/20',
    label: 'Planned',
  },
};

export default function StoriesPage() {
  const [epicGroups, setEpicGroups] = useState<EpicGroup[]>([]);
  const [expanded, setExpanded] = useState<Record<string, boolean>>({});
  const [loading, setLoading] = useState(true);
  const [searchTerm, setSearchTerm] = useState('');

  useEffect(() => {
    document.title = 'Stories \u2014 Tamma Docs';
    fetch('/content/manifest.json')
      .then((r) => r.json())
      .then((data: ManifestEntry[]) => {
        const epicMap = new Map<string, ManifestEntry[]>();

        for (const entry of data) {
          if (!entry.section.startsWith('Epic ')) continue;
          if (!epicMap.has(entry.section)) epicMap.set(entry.section, []);
          epicMap.get(entry.section)!.push(entry);
        }

        const groups: EpicGroup[] = [];

        for (const [section, entries] of epicMap) {
          const epicNumber = parseEpicNumber(section);

          // Find the overview entry (matches /stories/epic-N pattern)
          const overviewEntry = entries.find((e) => {
            return /^\/stories\/epic-[\d.-]+$/.test(e.path);
          });

          // Filter out overview, tech-spec, and non-story entries from the story list
          const stories = entries.filter((e) => {
            if (e === overviewEntry) return false;
            return true;
          });

          // Sort stories: tech specs first, then stories, then tasks
          stories.sort((a, b) => {
            const aIsTechSpec = a.path.includes('tech-spec');
            const bIsTechSpec = b.path.includes('tech-spec');
            if (aIsTechSpec && !bIsTechSpec) return -1;
            if (!aIsTechSpec && bIsTechSpec) return 1;

            // Extract story/task numbers for numerical sorting
            const aMatch = a.title.match(/[\d.]+/);
            const bMatch = b.title.match(/[\d.]+/);
            if (aMatch && bMatch) {
              return parseFloat(aMatch[0]) - parseFloat(bMatch[0]);
            }
            return a.title.localeCompare(b.title);
          });

          groups.push({
            section,
            epicNumber,
            epicTitle: overviewEntry?.title || section,
            overviewPath: overviewEntry?.path || null,
            stories,
          });
        }

        // Sort groups by epic number
        groups.sort((a, b) => numericSort(a.epicNumber, b.epicNumber));
        setEpicGroups(groups);
        setLoading(false);
      })
      .catch(() => setLoading(false));
  }, []);

  const toggle = (section: string) => {
    setExpanded((prev) => ({ ...prev, [section]: !prev[section] }));
  };

  const expandAll = () => {
    const all: Record<string, boolean> = {};
    for (const g of epicGroups) {
      all[g.section] = true;
    }
    setExpanded(all);
  };

  const collapseAll = () => {
    setExpanded({});
  };

  // Filter by search
  const filteredGroups = epicGroups
    .map((group) => {
      if (!searchTerm) return group;
      const term = searchTerm.toLowerCase();
      const titleMatch = group.epicTitle.toLowerCase().includes(term);
      const filteredStories = group.stories.filter((s) =>
        s.title.toLowerCase().includes(term)
      );
      if (titleMatch) return group;
      if (filteredStories.length > 0) {
        return { ...group, stories: filteredStories };
      }
      return null;
    })
    .filter(Boolean) as EpicGroup[];

  const totalStories = epicGroups.reduce((sum, g) => sum + g.stories.length, 0);

  if (loading) {
    return (
      <div className="space-y-6">
        <div className="animate-pulse">
          <div className="h-8 bg-zinc-800/50 rounded w-48 mb-2" />
          <div className="h-4 bg-zinc-800/30 rounded w-96 mb-8" />
          {Array.from({ length: 8 }).map((_, i) => (
            <div key={i} className="h-12 bg-zinc-900/50 rounded-lg border border-zinc-800 mb-2" />
          ))}
        </div>
      </div>
    );
  }

  return (
    <div className="space-y-8">
      {/* Header */}
      <div>
        <div className="flex items-center gap-2 text-sm text-zinc-500 mb-4">
          <Link to="/" className="hover:text-zinc-300 transition-colors">Home</Link>
          <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
            <path strokeLinecap="round" strokeLinejoin="round" d="M9 5l7 7-7 7" />
          </svg>
          <span className="text-zinc-400">Stories</span>
        </div>
        <h1 className="text-3xl font-bold text-white tracking-tight">Stories</h1>
        <p className="mt-2 text-zinc-400 text-[15px] leading-relaxed max-w-2xl">
          {totalStories} stories across {epicGroups.length} epics. Each story includes detailed specs,
          tasks, and acceptance criteria.
        </p>
      </div>

      {/* Controls */}
      <div className="flex flex-col sm:flex-row gap-3 items-start sm:items-center justify-between">
        <div className="relative w-full sm:w-72">
          <svg className="absolute left-3 top-1/2 -translate-y-1/2 w-4 h-4 text-zinc-600" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
            <path strokeLinecap="round" strokeLinejoin="round" d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
          </svg>
          <input
            type="text"
            placeholder="Search stories..."
            value={searchTerm}
            onChange={(e) => {
              setSearchTerm(e.target.value);
              // Auto-expand matching groups when searching
              if (e.target.value) {
                expandAll();
              }
            }}
            className="w-full pl-9 pr-3 py-2 bg-zinc-900/50 border border-zinc-800 rounded-lg text-[13px] text-zinc-300 placeholder:text-zinc-600 outline-none focus:border-zinc-600 transition-colors"
          />
        </div>
        <div className="flex gap-2">
          <button
            onClick={expandAll}
            className="px-3 py-1.5 text-[12px] text-zinc-400 hover:text-zinc-200 bg-zinc-900/50 border border-zinc-800 rounded-lg hover:border-zinc-700 transition-all"
          >
            Expand All
          </button>
          <button
            onClick={collapseAll}
            className="px-3 py-1.5 text-[12px] text-zinc-400 hover:text-zinc-200 bg-zinc-900/50 border border-zinc-800 rounded-lg hover:border-zinc-700 transition-all"
          >
            Collapse All
          </button>
        </div>
      </div>

      {/* Epic groups */}
      <div className="space-y-1">
        {filteredGroups.map((group) => {
          const status = getStatus(group.epicNumber);
          const styles = statusStyles[status];
          const isExpanded = expanded[group.section] || false;

          return (
            <div key={group.section} className="border border-zinc-800/60 rounded-lg overflow-hidden">
              {/* Epic header row */}
              <button
                onClick={() => toggle(group.section)}
                className="flex items-center gap-3 w-full px-4 py-3 bg-zinc-900/30 hover:bg-zinc-900/60 transition-colors text-left"
              >
                <svg
                  className={`w-3.5 h-3.5 text-zinc-500 shrink-0 transition-transform ${isExpanded ? 'rotate-90' : ''}`}
                  fill="none"
                  viewBox="0 0 24 24"
                  stroke="currentColor"
                  strokeWidth={2}
                >
                  <path strokeLinecap="round" strokeLinejoin="round" d="M9 5l7 7-7 7" />
                </svg>

                <span className={`w-2 h-2 rounded-full shrink-0 ${styles.dot}`} />

                <span className="text-xs font-mono text-zinc-600 shrink-0 w-6 text-right">
                  {group.epicNumber}
                </span>

                <span className="text-[14px] font-medium text-zinc-300 truncate">
                  {group.epicTitle.replace(/^Epic\s+[\d.]+:\s*/, '')}
                </span>

                <span className="ml-auto text-[11px] text-zinc-600 shrink-0 tabular-nums">
                  {group.stories.length}
                </span>
              </button>

              {/* Stories list */}
              {isExpanded && (
                <div className="border-t border-zinc-800/40">
                  {group.overviewPath && (
                    <Link
                      to={group.overviewPath}
                      className="flex items-center gap-2 px-4 py-2 pl-14 text-[13px] text-blue-400 hover:text-blue-300 hover:bg-zinc-800/20 transition-colors"
                    >
                      <svg className="w-3.5 h-3.5 shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                        <path strokeLinecap="round" strokeLinejoin="round" d="M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
                      </svg>
                      Epic Overview
                    </Link>
                  )}
                  {group.stories.map((story) => {
                    const isTechSpec = story.path.includes('tech-spec');
                    return (
                      <Link
                        key={story.path}
                        to={story.path}
                        className="flex items-center gap-2 px-4 py-2 pl-14 text-[13px] text-zinc-400 hover:text-zinc-200 hover:bg-zinc-800/20 transition-colors"
                      >
                        {isTechSpec ? (
                          <svg className="w-3.5 h-3.5 text-violet-400 shrink-0" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                            <path strokeLinecap="round" strokeLinejoin="round" d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z" />
                          </svg>
                        ) : (
                          <span className="w-1 h-1 rounded-full bg-zinc-700 shrink-0 ml-1 mr-0.5" />
                        )}
                        <span className="truncate">
                          {story.title.length > 70 ? story.title.slice(0, 70) + '...' : story.title}
                        </span>
                      </Link>
                    );
                  })}
                </div>
              )}
            </div>
          );
        })}
      </div>

      {filteredGroups.length === 0 && searchTerm && (
        <div className="text-center py-12">
          <div className="text-zinc-600 text-sm">No stories matching "{searchTerm}"</div>
        </div>
      )}
    </div>
  );
}
