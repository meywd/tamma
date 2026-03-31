import { useEffect, useState } from 'react';
import { useParams, useLocation } from 'react-router';
import Markdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import rehypeRaw from 'rehype-raw';

interface Props {
  path?: string;
  prefix?: string;
}

export default function MarkdownPage({ path, prefix }: Props) {
  const params = useParams();
  const location = useLocation();
  const [content, setContent] = useState('');
  const [title, setTitle] = useState('');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(false);

  const resolvedPath = path
    ?? (prefix === 'epics' ? `epics/${params.slug}.md`
    : prefix === 'stories' ? `stories/${params.epic}/${params.story}.md`
    : prefix === 'workflows' ? `workflows/${params.slug}.md`
    : `${location.pathname.replace(/^\//, '')}.md`);

  useEffect(() => {
    setLoading(true);
    setError(false);
    fetch(`/content/${resolvedPath}`)
      .then((res) => {
        if (!res.ok) throw new Error('Not found');
        return res.text();
      })
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
    if (title) document.title = `${title} — Tamma Docs`;
  }, [title]);

  if (loading) {
    return (
      <div className="animate-pulse space-y-4 py-8">
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
      </div>
    );
  }

  return (
    <article className="prose prose-invert max-w-none
      prose-headings:font-semibold prose-headings:tracking-tight
      prose-h1:text-3xl prose-h1:border-b prose-h1:border-zinc-800 prose-h1:pb-3 prose-h1:mb-6
      prose-h2:text-xl prose-h2:mt-10 prose-h2:mb-4
      prose-h3:text-lg prose-h3:mt-8
      prose-p:text-[15px] prose-p:leading-relaxed prose-p:text-zinc-300
      prose-a:text-blue-400 prose-a:no-underline hover:prose-a:underline
      prose-strong:text-zinc-100
      prose-code:text-amber-300 prose-code:text-[13px] prose-code:bg-zinc-800/80 prose-code:px-1.5 prose-code:py-0.5 prose-code:rounded
      prose-pre:bg-[#18181b] prose-pre:border prose-pre:border-zinc-800 prose-pre:rounded-lg prose-pre:text-[13px]
      prose-blockquote:border-l-zinc-700 prose-blockquote:text-zinc-400
      prose-li:text-[15px] prose-li:text-zinc-300
      prose-table:text-[14px]
      prose-th:text-zinc-300 prose-th:font-medium prose-th:border-zinc-700 prose-th:px-3 prose-th:py-2
      prose-td:border-zinc-800 prose-td:px-3 prose-td:py-2
      prose-hr:border-zinc-800
      prose-img:rounded-lg">
      <Markdown remarkPlugins={[remarkGfm]} rehypePlugins={[rehypeRaw]}>
        {content}
      </Markdown>
    </article>
  );
}
