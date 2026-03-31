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
        // Strip frontmatter
        const stripped = text.replace(/^---[\s\S]*?---\n*/, '');
        setContent(stripped);
        setLoading(false);
      })
      .catch(() => {
        setError(true);
        setLoading(false);
      });
  }, [resolvedPath]);

  if (loading) return <div className="p-8 text-zinc-400">Loading...</div>;
  if (error) return <div className="p-8 text-red-400">Page not found: {resolvedPath}</div>;

  return (
    <article className="prose prose-invert prose-zinc max-w-none p-6 lg:p-10
      prose-headings:text-zinc-100 prose-a:text-blue-400 prose-code:text-amber-300
      prose-pre:bg-zinc-900 prose-pre:border prose-pre:border-zinc-700
      prose-td:border-zinc-700 prose-th:border-zinc-700
      prose-table:border-collapse prose-img:rounded-lg">
      <Markdown remarkPlugins={[remarkGfm]} rehypePlugins={[rehypeRaw]}>
        {content}
      </Markdown>
    </article>
  );
}
