import { Fragment } from 'react';
import Markdown from 'react-markdown';
import remarkGfm from 'remark-gfm';

interface InlineMarkdownProps {
  /**
   * The raw markdown source string.
   */
  children: string;

  /**
   * If true, all markdown content (including soft line breaks) is collapsed onto
   * a single inline line — paragraph and list block elements are removed and any
   * `--` is converted to an em-dash. Use this for card descriptions / table cells
   * where you want bold, links, and inline code to render correctly without
   * paragraphs breaking the layout.
   *
   * Default: true
   */
  inline?: boolean;

  /**
   * Optional className applied to the wrapping span when inline.
   */
  className?: string;
}

/**
 * Inline markdown renderer.
 *
 * Replaces ad-hoc `dangerouslySetInnerHTML` regex hacks with a proper markdown
 * parser. Supports bold, italics, inline code, links, and lists, but renders
 * everything as inline elements so the resulting markup can be dropped into a
 * card, table cell, or short paragraph without inserting block-level wrappers.
 *
 * Why we needed this:
 * - The previous approach used regex-based HTML injection which lost links and
 *   broke any non-trivial markdown syntax (and required stripping bold markers
 *   to keep cards readable).
 * - react-markdown defaults wrap text in <p> blocks, which break inline card
 *   layouts. We override `p` to render a Fragment so paragraphs flow inline.
 *
 * Usage:
 *   <InlineMarkdown>{goal}</InlineMarkdown>
 */
export default function InlineMarkdown({
  children,
  inline = true,
  className,
}: InlineMarkdownProps) {
  // Collapse "--" to em-dash to match prior visual output
  const text = children.replace(/--/g, '\u2014');

  if (!inline) {
    return (
      <Markdown remarkPlugins={[remarkGfm]}>
        {text}
      </Markdown>
    );
  }

  // Inline mode: strip block wrappers so the result is span-only.
  const content = (
    <Markdown
      remarkPlugins={[remarkGfm]}
      components={{
        // Paragraph -> Fragment so it inlines into the parent span
        p: ({ children }) => <>{children}</>,
        // Lists in card extractions are usually accidental — collapse to inline
        ul: ({ children }) => <>{children}</>,
        ol: ({ children }) => <>{children}</>,
        li: ({ children }) => <Fragment>{children} </Fragment>,
        // Strip any pre/code blocks down to inline code styling
        pre: ({ children }) => <>{children}</>,
        // Style inline code consistently with the rest of the wiki
        code: ({ children }) => (
          <code className="text-amber-300 text-[0.92em] bg-zinc-800/80 px-1.5 py-0.5 rounded">
            {children}
          </code>
        ),
        // Style links to match wiki conventions
        a: ({ children, href }) => (
          <a
            href={href}
            className="text-blue-400 hover:underline"
            target={href?.startsWith('http') ? '_blank' : undefined}
            rel={href?.startsWith('http') ? 'noopener noreferrer' : undefined}
          >
            {children}
          </a>
        ),
        // Bold rendering preserved via default <strong>
        strong: ({ children }) => (
          <strong className="text-zinc-100 font-medium">{children}</strong>
        ),
      }}
    >
      {text}
    </Markdown>
  );

  if (className) {
    return <span className={className}>{content}</span>;
  }
  return <>{content}</>;
}
