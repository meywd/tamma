import { useEffect, useState, useMemo } from 'react';
import { useParams, useLocation, Link } from 'react-router';
import Markdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import rehypeRaw from 'rehype-raw';

interface Props {
  path?: string;
  prefix?: string;
}

interface ManifestEntry {
  path: string;
  title: string;
  section: string;
}

interface TocHeading {
  level: number;
  text: string;
  id: string;
}

function generateId(text: string): string {
  return text
    .toLowerCase()
    .replace(/[^\w\s-]/g, '')
    .replace(/\s+/g, '-')
    .replace(/-+/g, '-')
    .trim();
}

function extractHeadings(markdown: string): TocHeading[] {
  const headings: TocHeading[] = [];
  // Match ## and ### headings, skip # (h1 is the title)
  const regex = /^(#{2,3})\s+(.+)$/gm;
  let match;
  while ((match = regex.exec(markdown)) !== null) {
    headings.push({
      level: match[1].length,
      text: match[2].replace(/\*\*/g, '').replace(/`/g, '').trim(),
      id: generateId(match[2]),
    });
  }
  return headings;
}

function buildBreadcrumbs(
  pathname: string,
  manifest: ManifestEntry[],
  currentTitle: string
): Array<{ label: string; to: string }> {
  const crumbs: Array<{ label: string; to: string }> = [{ label: 'Home', to: '/' }];

  if (pathname.startsWith('/epics/')) {
    crumbs.push({ label: 'Epics', to: '/epics' });
    if (currentTitle) {
      crumbs.push({ label: currentTitle, to: pathname });
    }
  } else if (pathname.startsWith('/workflows/')) {
    crumbs.push({ label: 'Workflows', to: '/workflows' });
    if (currentTitle) {
      crumbs.push({
        label: currentTitle.replace(/^Workflow:\s*/, ''),
        to: pathname,
      });
    }
  } else if (pathname.startsWith('/stories/')) {
    crumbs.push({ label: 'Stories', to: '/stories' });

    // Find the section from manifest to get epic name
    const entry = manifest.find((e) => e.path === pathname);
    if (entry && entry.section.startsWith('Epic ')) {
      // Find the epic overview entry
      const epicOverview = manifest.find(
        (e) => e.section === entry.section && /^\/stories\/epic-[\d.-]+$/.test(e.path)
      );
      if (epicOverview) {
        crumbs.push({
          label: entry.section,
          to: epicOverview.path,
        });
      }
    }
    if (currentTitle) {
      crumbs.push({ label: currentTitle, to: pathname });
    }
  } else if (currentTitle) {
    crumbs.push({ label: currentTitle, to: pathname });
  }

  return crumbs;
}

function findSiblings(
  pathname: string,
  manifest: ManifestEntry[]
): { prev: ManifestEntry | null; next: ManifestEntry | null } {
  // Find the current entry
  const current = manifest.find((e) => e.path === pathname);
  if (!current) return { prev: null, next: null };

  // Get siblings in the same section
  const siblings = manifest.filter((e) => e.section === current.section);
  const idx = siblings.findIndex((e) => e.path === pathname);

  return {
    prev: idx > 0 ? siblings[idx - 1] : null,
    next: idx < siblings.length - 1 ? siblings[idx + 1] : null,
  };
}

export default function MarkdownPage({ path, prefix }: Props) {
  const params = useParams();
  const location = useLocation();
  const [content, setContent] = useState('');
  const [title, setTitle] = useState('');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(false);
  const [manifest, setManifest] = useState<ManifestEntry[]>([]);
  const [activeHeading, setActiveHeading] = useState('');

  const resolvedPath = path
    ?? (prefix === 'epics' ? `epics/${params.slug}.md`
    : prefix === 'stories' ? `stories/${params.epic}/${params.story}.md`
    : prefix === 'workflows' ? `workflows/${params.slug}.md`
    : `${location.pathname.replace(/^\//, '')}.md`);

  // Load manifest
  useEffect(() => {
    fetch('/content/manifest.json')
      .then((r) => r.json())
      .then((data: ManifestEntry[]) => setManifest(data))
      .catch(() => {});
  }, []);

  useEffect(() => {
    setLoading(true);
    setError(false);
    // Try `${path}.md` first; if the server returns the SPA HTML fallback
    // (status 200 but body is index.html), or a 404, fall back to
    // `${path}/index.md`. This handles directory-style URLs like
    // /stories/epic-28 where the overview lives at stories/epic-28/index.md
    // (sync-content.ts emits /stories/epic-N as a manifest path per epic dir).
    const looksLikeMarkdown = (text: string): boolean => {
      const trimmed = text.trimStart().toLowerCase();
      return !trimmed.startsWith('<!doctype') && !trimmed.startsWith('<html');
    };
    const tryFetch = async (): Promise<string> => {
      const res = await fetch(`/content/${resolvedPath}`);
      if (res.ok) {
        const text = await res.text();
        if (looksLikeMarkdown(text)) return text;
      }
      // Fall back to index.md under a directory of the same name
      const dirPath = resolvedPath.replace(/\.md$/, '/index.md');
      if (dirPath !== resolvedPath) {
        const fallback = await fetch(`/content/${dirPath}`);
        if (fallback.ok) {
          const text = await fallback.text();
          if (looksLikeMarkdown(text)) return text;
        }
      }
      throw new Error('Not found');
    };
    tryFetch()
      .then((text) => {
        // Extract title from frontmatter
        const fmMatch = text.match(/^---\s*\n[\s\S]*?title:\s*"?([^"\n]+)"?[\s\S]*?---\s*\n/);
        if (fmMatch) setTitle(fmMatch[1].trim());

        // Strip frontmatter
        const stripped = text.replace(/^---[\s\S]*?---\n*/, '');

        // Extract first H1 as title if no frontmatter title
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
  }, [resolvedPath]);

  useEffect(() => {
    if (title) document.title = `${title} \u2014 Tamma Docs`;
  }, [title]);

  const headings = useMemo(() => extractHeadings(content), [content]);
  const breadcrumbs = useMemo(
    () => buildBreadcrumbs(location.pathname, manifest, title),
    [location.pathname, manifest, title]
  );
  const { prev, next } = useMemo(
    () => findSiblings(location.pathname, manifest),
    [location.pathname, manifest]
  );

  const showToc = headings.length > 3;

  // Track active heading on scroll
  useEffect(() => {
    if (!showToc) return;

    const observer = new IntersectionObserver(
      (entries) => {
        for (const entry of entries) {
          if (entry.isIntersecting) {
            setActiveHeading(entry.target.id);
          }
        }
      },
      { rootMargin: '-80px 0px -70% 0px', threshold: 0 }
    );

    // Observe all heading elements
    const timer = setTimeout(() => {
      for (const h of headings) {
        const el = document.getElementById(h.id);
        if (el) observer.observe(el);
      }
    }, 200);

    return () => {
      clearTimeout(timer);
      observer.disconnect();
    };
  }, [headings, showToc]);

  if (loading) {
    return (
      <div className="animate-pulse space-y-4 py-8">
        <div className="h-4 bg-zinc-800/30 rounded w-48 mb-6" />
        <div className="h-8 bg-zinc-800/50 rounded w-2/3" />
        <div className="h-4 bg-zinc-800/30 rounded w-full" />
        <div className="h-4 bg-zinc-800/30 rounded w-5/6" />
        <div className="h-4 bg-zinc-800/30 rounded w-4/6" />
      </div>
    );
  }

  if (error) {
    return (
      <div className="py-16 text-center">
        <div className="text-6xl mb-4">404</div>
        <div className="text-zinc-500 text-lg">Page not found</div>
        <div className="text-zinc-600 text-sm mt-2 font-mono">{resolvedPath}</div>
        <Link
          to="/"
          className="inline-flex items-center gap-1.5 mt-6 text-sm text-blue-400 hover:text-blue-300 transition-colors"
        >
          <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
            <path strokeLinecap="round" strokeLinejoin="round" d="M15 19l-7-7 7-7" />
          </svg>
          Back to Home
        </Link>
      </div>
    );
  }

  return (
    <div className={showToc ? 'flex gap-8' : ''}>
      {/* Main content */}
      <div className={showToc ? 'min-w-0 flex-1' : ''}>
        {/* Breadcrumbs */}
        {breadcrumbs.length > 1 && (
          <nav className="flex items-center gap-1.5 text-[13px] text-zinc-500 mb-6 flex-wrap">
            {breadcrumbs.map((crumb, i) => (
              <span key={i} className="flex items-center gap-1.5">
                {i > 0 && (
                  <svg className="w-3 h-3 text-zinc-700" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                    <path strokeLinecap="round" strokeLinejoin="round" d="M9 5l7 7-7 7" />
                  </svg>
                )}
                {i < breadcrumbs.length - 1 ? (
                  <Link
                    to={crumb.to}
                    className="hover:text-zinc-300 transition-colors"
                  >
                    {crumb.label}
                  </Link>
                ) : (
                  <span className="text-zinc-400 truncate max-w-xs">{crumb.label}</span>
                )}
              </span>
            ))}
          </nav>
        )}

        {/* Article */}
        <article className="prose prose-invert max-w-none
          prose-headings:font-semibold prose-headings:tracking-tight
          prose-h1:text-3xl prose-h1:border-b prose-h1:border-zinc-800 prose-h1:pb-3 prose-h1:mb-6
          prose-h2:text-xl prose-h2:mt-10 prose-h2:mb-4 prose-h2:scroll-mt-6
          prose-h3:text-lg prose-h3:mt-8 prose-h3:scroll-mt-6
          prose-p:text-[15px] prose-p:leading-relaxed prose-p:text-zinc-300
          prose-a:text-blue-400 prose-a:no-underline hover:prose-a:underline
          prose-strong:text-zinc-100
          prose-code:text-amber-300 prose-code:text-[13px] prose-code:bg-zinc-800/80 prose-code:px-1.5 prose-code:py-0.5 prose-code:rounded
          prose-pre:bg-[#18181b] prose-pre:border prose-pre:border-zinc-800 prose-pre:rounded-lg prose-pre:text-[13px]
          prose-blockquote:border-l-zinc-700 prose-blockquote:text-zinc-400
          prose-li:text-[15px] prose-li:text-zinc-300
          prose-table:text-[14px]
          prose-th:text-zinc-300 prose-th:font-medium prose-th:border-zinc-700 prose-th:px-3 prose-th:py-2 prose-th:bg-zinc-800/50
          prose-td:border-zinc-800 prose-td:px-3 prose-td:py-2
          prose-hr:border-zinc-800
          prose-img:rounded-lg
          [&_table]:border [&_table]:border-zinc-800 [&_table]:rounded-lg [&_table]:overflow-hidden
          [&_thead]:bg-zinc-800/50 [&_thead_th]:sticky [&_thead_th]:top-0
          [&_tbody_tr:nth-child(even)]:bg-zinc-900/30
          [&_tbody_tr]:hover:bg-zinc-800/30 [&_tbody_tr]:transition-colors">
          <Markdown
            remarkPlugins={[remarkGfm]}
            rehypePlugins={[rehypeRaw]}
            components={{
              // Add id attributes to headings for ToC anchor links
              h2: ({ children, ...props }) => {
                const text = typeof children === 'string' ? children : extractTextFromChildren(children);
                const id = generateId(text);
                return <h2 id={id} {...props}>{children}</h2>;
              },
              h3: ({ children, ...props }) => {
                const text = typeof children === 'string' ? children : extractTextFromChildren(children);
                const id = generateId(text);
                return <h3 id={id} {...props}>{children}</h3>;
              },
            }}
          >
            {content}
          </Markdown>
        </article>

        {/* Prev/Next navigation */}
        {(prev || next) && (
          <div className="flex items-stretch gap-3 mt-12 pt-8 border-t border-zinc-800">
            {prev ? (
              <Link
                to={prev.path}
                className="group flex-1 flex flex-col items-start gap-1 p-4 bg-zinc-900/30 border border-zinc-800/60 rounded-xl hover:bg-zinc-800/40 hover:border-zinc-700 transition-all"
              >
                <span className="text-[11px] text-zinc-600 uppercase tracking-wider flex items-center gap-1">
                  <svg className="w-3 h-3" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                    <path strokeLinecap="round" strokeLinejoin="round" d="M15 19l-7-7 7-7" />
                  </svg>
                  Previous
                </span>
                <span className="text-[13px] text-zinc-400 group-hover:text-zinc-200 transition-colors line-clamp-1">
                  {prev.title}
                </span>
              </Link>
            ) : (
              <div className="flex-1" />
            )}
            {next ? (
              <Link
                to={next.path}
                className="group flex-1 flex flex-col items-end gap-1 p-4 bg-zinc-900/30 border border-zinc-800/60 rounded-xl hover:bg-zinc-800/40 hover:border-zinc-700 transition-all text-right"
              >
                <span className="text-[11px] text-zinc-600 uppercase tracking-wider flex items-center gap-1">
                  Next
                  <svg className="w-3 h-3" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                    <path strokeLinecap="round" strokeLinejoin="round" d="M9 5l7 7-7 7" />
                  </svg>
                </span>
                <span className="text-[13px] text-zinc-400 group-hover:text-zinc-200 transition-colors line-clamp-1">
                  {next.title}
                </span>
              </Link>
            ) : (
              <div className="flex-1" />
            )}
          </div>
        )}
      </div>

      {/* Table of Contents sidebar */}
      {showToc && (
        <aside className="hidden lg:block w-56 shrink-0">
          <div className="sticky top-6">
            <h4 className="text-[11px] font-semibold text-zinc-500 uppercase tracking-widest mb-3">
              On this page
            </h4>
            <nav className="flex flex-col gap-0.5 max-h-[calc(100vh-120px)] overflow-y-auto">
              {headings.map((heading, i) => (
                <a
                  key={`${heading.id}-${i}`}
                  href={`#${heading.id}`}
                  onClick={(e) => {
                    e.preventDefault();
                    const el = document.getElementById(heading.id);
                    if (el) {
                      el.scrollIntoView({ behavior: 'smooth', block: 'start' });
                      setActiveHeading(heading.id);
                    }
                  }}
                  className={`block text-[12px] leading-snug py-1 transition-colors ${
                    heading.level === 3 ? 'pl-3' : ''
                  } ${
                    activeHeading === heading.id
                      ? 'text-blue-400 font-medium'
                      : 'text-zinc-500 hover:text-zinc-300'
                  }`}
                >
                  {heading.text.length > 40 ? heading.text.slice(0, 40) + '...' : heading.text}
                </a>
              ))}
            </nav>
          </div>
        </aside>
      )}
    </div>
  );
}

/**
 * Recursively extract text content from React children for heading ID generation.
 */
function extractTextFromChildren(children: unknown): string {
  if (typeof children === 'string') return children;
  if (typeof children === 'number') return String(children);
  if (Array.isArray(children)) return children.map(extractTextFromChildren).join('');
  if (children && typeof children === 'object' && 'props' in children) {
    const props = children as { props?: { children?: unknown } };
    return extractTextFromChildren(props.props?.children ?? '');
  }
  return '';
}
