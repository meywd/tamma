import { useEffect, useState, useMemo } from 'react';
import { Link } from 'react-router';
import Markdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import rehypeRaw from 'rehype-raw';
import MermaidDiagram from './MermaidDiagram';

// --- Types ---

interface ParsedSection {
  heading: string;
  level: number;
  content: string;
}

interface ArchLayer {
  name: string;
  components: ArchComponent[];
}

interface ArchComponent {
  name: string;
  description: string;
}

interface TechBadge {
  label: string;
  color: string;
}

// --- Constants ---

const techBadges: TechBadge[] = [
  { label: 'TypeScript', color: 'bg-blue-500/10 text-blue-400 border-blue-500/20' },
  { label: 'C# / .NET 8', color: 'bg-violet-500/10 text-violet-400 border-violet-500/20' },
  { label: 'PostgreSQL', color: 'bg-cyan-500/10 text-cyan-400 border-cyan-500/20' },
  { label: 'Docker', color: 'bg-sky-500/10 text-sky-400 border-sky-500/20' },
  { label: 'ELSA', color: 'bg-purple-500/10 text-purple-400 border-purple-500/20' },
  { label: 'React', color: 'bg-teal-500/10 text-teal-400 border-teal-500/20' },
  { label: 'Fastify', color: 'bg-green-500/10 text-green-400 border-green-500/20' },
  { label: 'RabbitMQ', color: 'bg-orange-500/10 text-orange-400 border-orange-500/20' },
];

const architectureHighlights = [
  {
    title: 'Dual-Stack Architecture',
    description: 'TypeScript for AI/CLI/API, C# for ELSA workflow orchestration',
    iconPath: 'M4 7v10c0 2.21 3.582 4 8 4s8-1.79 8-4V7M4 7c0 2.21 3.582 4 8 4s8-1.79 8-4M4 7c0-2.21 3.582-4 8-4s8 1.79 8 4m0 5c0 2.21-3.582 4-8 4s-8-1.79-8-4',
    color: 'text-blue-400 bg-blue-500/10 border-blue-500/20',
  },
  {
    title: 'ELSA Workflow Engine',
    description: '20+ code-first workflows, visual designer, pausable/resumable',
    iconPath: 'M13 10V3L4 14h7v7l9-11h-7z',
    color: 'text-purple-400 bg-purple-500/10 border-purple-500/20',
  },
  {
    title: 'Multi-Provider AI',
    description: '4+ providers with fallback chains and circuit breakers',
    iconPath: 'M9.75 17L9 20l-1 1h8l-1-1-.75-3M3 13h18M5 17h14a2 2 0 002-2V5a2 2 0 00-2-2H5a2 2 0 00-2 2v10a2 2 0 002 2z',
    color: 'text-amber-400 bg-amber-500/10 border-amber-500/20',
  },
  {
    title: 'Defense-in-Depth Security',
    description: 'Content sanitization, SSRF protection, action gating',
    iconPath: 'M9 12l2 2 4-4m5.618-4.016A11.955 11.955 0 0112 2.944a11.955 11.955 0 01-8.618 3.04A12.02 12.02 0 003 9c0 5.591 3.824 10.29 9 11.622 5.176-1.332 9-6.03 9-11.622 0-1.042-.133-2.052-.382-3.016z',
    color: 'text-emerald-400 bg-emerald-500/10 border-emerald-500/20',
  },
  {
    title: 'Event Sourcing',
    description: 'Complete audit trail with DCB pattern for time-travel debugging',
    iconPath: 'M12 8v4l3 3m6-3a9 9 0 11-18 0 9 9 0 0118 0z',
    color: 'text-cyan-400 bg-cyan-500/10 border-cyan-500/20',
  },
  {
    title: 'Three Deployment Modes',
    description: 'CLI standalone, self-hosted server, multi-tenant SaaS',
    iconPath: 'M3 15a4 4 0 004 4h9a5 5 0 10-.1-9.999 5.002 5.002 0 10-9.78 2.096A4.001 4.001 0 003 15z',
    color: 'text-sky-400 bg-sky-500/10 border-sky-500/20',
  },
];

// --- Parsing helpers ---

function parseSections(markdown: string): ParsedSection[] {
  const lines = markdown.split('\n');
  const sections: ParsedSection[] = [];
  let currentHeading = '';
  let currentLevel = 0;
  let currentLines: string[] = [];

  for (const line of lines) {
    const headingMatch = line.match(/^(#{2,4})\s+(.+)$/);
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

function parseArchitectureDiagram(markdown: string): ArchLayer[] {
  // Check if the diagram code block exists
  const codeBlockMatch = markdown.match(
    /## High-Level Architecture\s*\n```\n([\s\S]*?)\n```/
  );
  if (!codeBlockMatch) return [];

  // Rather than fragile regex parsing of ASCII art, use a structured
  // representation that mirrors the diagram content faithfully.
  return [
    {
      name: 'Tamma Engine (TypeScript)',
      components: [
        { name: '@tamma/cli', description: 'CLI modes: start, server, api' },
        { name: '@tamma/api', description: 'Fastify REST API, OAuth, webhooks' },
        { name: '@tamma/orchestrator', description: 'Engine brain, ElsaClient bridge' },
        { name: '@tamma/dashboard', description: 'React SPA, admin panel' },
        { name: '@tamma/providers', description: 'AI providers, role resolver, chains' },
        { name: '@tamma/platforms', description: 'IGitPlatform, GitHub impl' },
        { name: '@tamma/shared', description: 'Security, config, diagnostics' },
        { name: '@tamma/intelligence', description: 'RAG, vector DB, knowledge base' },
        { name: '@tamma/mcp-client', description: 'MCP protocol, tool interceptors' },
        { name: '@tamma/cost-monitor', description: 'Usage tracking, budget alerts' },
        { name: '@tamma/gates', description: 'Permissions, violation recording' },
        { name: '@tamma/scrum-master', description: 'Task supervisor, approvals' },
        { name: '@tamma/observability', description: 'Pino structured logging' },
      ],
    },
    {
      name: 'ELSA Workflow Engine (C# / .NET 8)',
      components: [
        { name: 'Tamma.ElsaServer', description: '20+ code-first workflows, REST API' },
        { name: 'Tamma.Studio', description: 'Custom Blazor WASM, Tamma-branded UI' },
        { name: 'Tamma.Activities', description: 'ADL, AI, Assessment, LLM, TDD, Tools' },
        { name: 'Tamma.Core', description: 'Enums, models, shared types' },
        { name: 'Tamma.Data', description: 'DB context, migrations' },
        { name: 'Tamma.Api', description: '.NET REST API' },
      ],
    },
    {
      name: 'Infrastructure',
      components: [
        { name: 'PostgreSQL 17', description: 'Data, events, ELSA state' },
        { name: 'RabbitMQ', description: 'Message broker' },
        { name: 'ChromaDB', description: 'Vector store' },
        { name: 'OpenSearch', description: 'Log aggregation (optional)' },
        { name: 'nginx', description: 'Reverse proxy + dashboard' },
        { name: 'Cloudflare', description: 'DNS, SSL (Full mode)' },
      ],
    },
  ];
}

function parseTableFromContent(content: string): {
  headers: string[];
  rows: string[][];
} | null {
  const lines = content.split('\n').filter((l) => l.trim().startsWith('|'));
  if (lines.length < 3) return null;

  const parseRow = (line: string): string[] =>
    line
      .split('|')
      .slice(1, -1)
      .map((cell) => cell.trim());

  const headers = parseRow(lines[0]);
  const rows = lines.slice(2).map(parseRow);
  return { headers, rows };
}

// Prose classes for markdown fallback
const proseClasses =
  'prose prose-invert prose-sm max-w-none prose-p:text-[15px] prose-p:text-zinc-300 prose-a:text-blue-400 prose-a:no-underline hover:prose-a:underline prose-strong:text-zinc-100 prose-code:text-amber-300 prose-code:text-[12px] prose-code:bg-zinc-800/80 prose-code:px-1.5 prose-code:py-0.5 prose-code:rounded prose-pre:bg-[#18181b] prose-pre:border prose-pre:border-zinc-800 prose-pre:rounded-lg prose-pre:text-[13px] prose-li:text-[14px] prose-li:text-zinc-300 prose-h3:text-base prose-h3:mt-6 prose-h3:mb-3 prose-h4:text-sm prose-h4:mt-4 prose-h4:mb-2';

// --- Component Diagram ---

function ComponentDiagram({ layers }: { layers: ArchLayer[] }) {
  if (layers.length === 0) return null;

  const layerColors = [
    {
      border: 'border-blue-500/30',
      bg: 'bg-blue-500/5',
      label: 'text-blue-400',
      chip: 'bg-blue-500/10 text-blue-300 border-blue-500/20',
    },
    {
      border: 'border-purple-500/30',
      bg: 'bg-purple-500/5',
      label: 'text-purple-400',
      chip: 'bg-purple-500/10 text-purple-300 border-purple-500/20',
    },
    {
      border: 'border-cyan-500/30',
      bg: 'bg-cyan-500/5',
      label: 'text-cyan-400',
      chip: 'bg-cyan-500/10 text-cyan-300 border-cyan-500/20',
    },
  ];

  return (
    <div>
      <h2 className="text-sm font-semibold text-zinc-500 uppercase tracking-wider mb-4">
        System Layers
      </h2>
      <div className="space-y-0">
        {layers.map((layer, li) => {
          const colors = layerColors[li % layerColors.length];
          const connectionLabels = [
            'HTTP API (ElsaClient)',
            'PostgreSQL / RabbitMQ',
          ];
          return (
            <div key={li}>
              <div
                className={`border rounded-xl p-5 ${colors.border} ${colors.bg}`}
              >
                <div
                  className={`text-xs font-semibold uppercase tracking-wider mb-3 ${colors.label}`}
                >
                  {layer.name}
                </div>
                <div className="flex flex-wrap gap-2">
                  {layer.components.map((comp, ci) => (
                    <div
                      key={ci}
                      className={`inline-flex flex-col px-3 py-2 rounded-lg border text-[12px] ${colors.chip}`}
                      title={comp.description}
                    >
                      <span className="font-medium">{comp.name}</span>
                      {comp.description && (
                        <span className="text-[10px] opacity-60 mt-0.5 max-w-[200px] truncate">
                          {comp.description}
                        </span>
                      )}
                    </div>
                  ))}
                </div>
              </div>

              {/* Connection arrow between layers */}
              {li < layers.length - 1 && (
                <div className="flex justify-center py-2">
                  <div className="flex flex-col items-center text-zinc-600">
                    <svg
                      className="w-5 h-5"
                      fill="none"
                      viewBox="0 0 24 24"
                      stroke="currentColor"
                      strokeWidth={1.5}
                    >
                      <path
                        strokeLinecap="round"
                        strokeLinejoin="round"
                        d="M7 16V4m0 0L3 8m4-4l4 4m6 0v12m0 0l4-4m-4 4l-4-4"
                      />
                    </svg>
                    <span className="text-[10px] mt-0.5">
                      {connectionLabels[li] || 'Connection'}
                    </span>
                  </div>
                </div>
              )}
            </div>
          );
        })}
      </div>
    </div>
  );
}

// --- Deployment Mode Card ---

function DeploymentModeCard({
  mode,
  command,
  description,
  details,
}: {
  mode: string;
  command: string;
  description: string;
  details: string[];
}) {
  return (
    <div className="bg-zinc-900/50 border border-zinc-800 rounded-xl p-5">
      <div className="text-[15px] font-medium text-zinc-200 mb-1">{mode}</div>
      <code className="text-[12px] text-amber-300 bg-zinc-800/80 px-2 py-0.5 rounded">
        {command}
      </code>
      <p className="text-[13px] text-zinc-400 mt-3 leading-relaxed">
        {description}
      </p>
      <ul className="mt-3 space-y-1">
        {details.map((d, i) => (
          <li key={i} className="flex items-start gap-2">
            <span className="w-1 h-1 rounded-full bg-zinc-600 mt-2 shrink-0" />
            <span className="text-[12px] text-zinc-500">{d}</span>
          </li>
        ))}
      </ul>
    </div>
  );
}

// --- Styled Table from Content ---

function StyledTable({
  content,
  title,
}: {
  content: string;
  title?: string;
}) {
  const table = parseTableFromContent(content);
  if (!table) return null;

  return (
    <div className="bg-zinc-900/50 border border-zinc-800 rounded-xl overflow-hidden">
      <div className="overflow-x-auto">
        <table className="w-full text-[13px]">
          <thead>
            <tr className="bg-zinc-800/50">
              {table.headers.map((h, hi) => (
                <th
                  key={hi}
                  className="text-left text-zinc-400 font-medium px-4 py-2.5 border-b border-zinc-800"
                >
                  {h}
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {table.rows.map((row, ri) => (
              <tr
                key={ri}
                className="hover:bg-zinc-800/30 transition-colors"
              >
                {row.map((cell, ci) => (
                  <td
                    key={ci}
                    className="px-4 py-2.5 text-zinc-300 border-b border-zinc-800/50"
                  >
                    <span
                      dangerouslySetInnerHTML={{
                        __html: cell
                          .replace(
                            /`([^`]+)`/g,
                            '<code class="text-amber-300 text-[12px] bg-zinc-800/80 px-1.5 py-0.5 rounded">$1</code>'
                          )
                          .replace(
                            /\*\*([^*]+)\*\*/g,
                            '<strong class="text-zinc-100 font-medium">$1</strong>'
                          )
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
  );
}

// --- Collapsible Section ---

function CollapsibleSection({
  heading,
  children,
  defaultOpen = false,
}: {
  heading: string;
  children: React.ReactNode;
  defaultOpen?: boolean;
}) {
  const [isOpen, setIsOpen] = useState(defaultOpen);

  return (
    <div className="bg-zinc-900/50 border border-zinc-800 rounded-xl overflow-hidden transition-colors hover:border-zinc-700">
      <button
        onClick={() => setIsOpen((v) => !v)}
        className="w-full flex items-center gap-3 px-5 py-4 text-left group"
      >
        <span className="text-[15px] font-medium text-zinc-200 group-hover:text-white transition-colors">
          {heading}
        </span>
        <svg
          className={`w-4 h-4 text-zinc-500 ml-auto transition-transform duration-200 ${isOpen ? 'rotate-180' : ''}`}
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
      </button>
      <div
        className={`overflow-hidden transition-all duration-300 ease-in-out ${
          isOpen ? 'max-h-[5000px] opacity-100' : 'max-h-0 opacity-0'
        }`}
      >
        <div className="px-5 pb-5 border-t border-zinc-800/60">{children}</div>
      </div>
    </div>
  );
}

// --- Section Renderer ---

function SectionContent({ content, heading }: { content: string; heading: string }) {
  // Check if content has tables
  const hasTable = content.includes('|') && content.split('\n').filter((l) => l.trim().startsWith('|')).length >= 3;

  // Check if this section is deployment modes
  const isDeployment = heading.toLowerCase().includes('deployment');

  if (isDeployment) {
    return <DeploymentModesFromContent content={content} />;
  }

  if (hasTable) {
    // Split content into pre-table, table, and post-table
    const lines = content.split('\n');
    const firstTableLine = lines.findIndex((l) => l.trim().startsWith('|'));
    const lastTableLine = (() => {
      for (let i = lines.length - 1; i >= 0; i--) {
        if (lines[i].trim().startsWith('|')) return i;
      }
      return -1;
    })();

    const preTable = lines.slice(0, firstTableLine).join('\n').trim();
    const tableContent = lines.slice(firstTableLine, lastTableLine + 1).join('\n');
    const postTable = lines.slice(lastTableLine + 1).join('\n').trim();

    return (
      <div className="space-y-4 pt-4">
        {preTable && (
          <div className={proseClasses}>
            <Markdown remarkPlugins={[remarkGfm]} rehypePlugins={[rehypeRaw]}>
              {preTable}
            </Markdown>
          </div>
        )}
        <StyledTable content={tableContent} />
        {postTable && (
          <div className={proseClasses}>
            <Markdown remarkPlugins={[remarkGfm]} rehypePlugins={[rehypeRaw]}>
              {postTable}
            </Markdown>
          </div>
        )}
      </div>
    );
  }

  return (
    <div className={`pt-4 ${proseClasses}`}>
      <Markdown remarkPlugins={[remarkGfm]} rehypePlugins={[rehypeRaw]}>
        {content}
      </Markdown>
    </div>
  );
}

function DeploymentModesFromContent({ content }: { content: string }) {
  // Parse the three deployment modes from the markdown
  const modes: Array<{
    mode: string;
    command: string;
    description: string;
    details: string[];
  }> = [];

  const modeRegex = /### \d+\.\s+(.+?)\s*\(`(.+?)`\)\s*\n```\w*\n(.+?)\n```\n([\s\S]*?)(?=### \d+|---|\n$|$)/g;
  let m;
  while ((m = modeRegex.exec(content)) !== null) {
    const body = m[4].trim();
    const bodyLines = body.split('\n');
    const details: string[] = [];
    for (const line of bodyLines) {
      const bulletMatch = line.match(/^-\s+(.+)/);
      if (bulletMatch) details.push(bulletMatch[1].trim());
    }
    modes.push({
      mode: m[1].trim(),
      command: m[3].trim(),
      description: bodyLines[0]?.startsWith('-') ? '' : bodyLines[0] || '',
      details,
    });
  }

  if (modes.length === 0) {
    return (
      <div className={`pt-4 ${proseClasses}`}>
        <Markdown remarkPlugins={[remarkGfm]} rehypePlugins={[rehypeRaw]}>
          {content}
        </Markdown>
      </div>
    );
  }

  return (
    <div className="grid grid-cols-1 sm:grid-cols-3 gap-3 pt-4">
      {modes.map((mode, i) => (
        <DeploymentModeCard key={i} {...mode} />
      ))}
    </div>
  );
}

// --- Main component ---

export default function ArchitecturePage() {
  const [content, setContent] = useState('');
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState(false);

  useEffect(() => {
    document.title = 'System Architecture \u2014 Tamma Docs';
  }, []);

  useEffect(() => {
    setLoading(true);
    fetch('/content/architecture.md')
      .then((res) => {
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

  const sections = useMemo(() => parseSections(content), [content]);
  const architectureLayers = useMemo(
    () => parseArchitectureDiagram(content),
    [content]
  );

  // Extract intro paragraph (before first ##)
  const introParagraph = useMemo(() => {
    const firstHeading = content.indexOf('\n## ');
    if (firstHeading === -1) return '';
    return content.substring(0, firstHeading).trim();
  }, [content]);

  // Group level-2 sections with their level-3 children
  const topLevelSections = useMemo(() => {
    const result: Array<{
      heading: string;
      content: string;
      children: ParsedSection[];
    }> = [];
    let current: { heading: string; content: string; children: ParsedSection[] } | null = null;

    for (const section of sections) {
      if (section.level === 2) {
        if (current) result.push(current);
        current = {
          heading: section.heading,
          content: section.content,
          children: [],
        };
      } else if (section.level >= 3 && current) {
        current.children.push(section);
      }
    }
    if (current) result.push(current);

    return result;
  }, [sections]);

  // Identify the "High-Level Architecture" section to skip (we render it specially)
  const specialSections = new Set([
    'High-Level Architecture',
    'For More Details',
  ]);

  if (loading) {
    return (
      <div className="animate-pulse space-y-6 py-8">
        <div className="h-4 bg-zinc-800/30 rounded w-48 mb-6" />
        <div className="h-10 bg-zinc-800/50 rounded w-96 mb-2" />
        <div className="h-4 bg-zinc-800/30 rounded w-80 mb-8" />
        <div className="flex flex-wrap gap-2 mb-6">
          {Array.from({ length: 6 }).map((_, i) => (
            <div
              key={i}
              className="h-6 w-20 bg-zinc-800/40 rounded-full"
            />
          ))}
        </div>
        <div className="grid grid-cols-2 sm:grid-cols-3 gap-3">
          {Array.from({ length: 6 }).map((_, i) => (
            <div
              key={i}
              className="h-28 bg-zinc-900/50 rounded-xl border border-zinc-800"
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
        <div className="text-zinc-500 text-lg">Architecture page not found</div>
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
        <span className="text-zinc-400">Architecture</span>
      </nav>

      {/* Header */}
      <div>
        <h1 className="text-3xl font-bold text-white tracking-tight">
          System Architecture
        </h1>
        <p className="mt-2 text-zinc-400 text-[15px] leading-relaxed max-w-3xl">
          {introParagraph
            .replace(/\*\*/g, '')
            .replace(/\[.*?\]\(.*?\)/g, '')
            .substring(0, 200) || 'Dual-stack architecture combining TypeScript and C# for autonomous development orchestration.'}
        </p>

        {/* Tech stack badges */}
        <div className="flex flex-wrap gap-2 mt-4">
          {techBadges.map((badge) => (
            <span
              key={badge.label}
              className={`inline-flex px-2.5 py-0.5 text-xs rounded-full border ${badge.color}`}
            >
              {badge.label}
            </span>
          ))}
        </div>
      </div>

      {/* Architecture Highlights */}
      <div>
        <h2 className="text-sm font-semibold text-zinc-500 uppercase tracking-wider mb-4">
          Architecture Highlights
        </h2>
        <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-3">
          {architectureHighlights.map((highlight) => (
            <div
              key={highlight.title}
              className="bg-zinc-900/50 border border-zinc-800 rounded-xl p-4 hover:border-zinc-700 transition-colors"
            >
              <div className="flex items-start gap-3">
                <div
                  className={`w-9 h-9 rounded-lg border flex items-center justify-center shrink-0 ${highlight.color}`}
                >
                  <svg
                    className="w-4.5 h-4.5"
                    fill="none"
                    viewBox="0 0 24 24"
                    stroke="currentColor"
                    strokeWidth={1.5}
                  >
                    <path
                      strokeLinecap="round"
                      strokeLinejoin="round"
                      d={highlight.iconPath}
                    />
                  </svg>
                </div>
                <div>
                  <div className="text-[14px] font-medium text-zinc-200">
                    {highlight.title}
                  </div>
                  <div className="text-[12px] text-zinc-500 mt-0.5 leading-relaxed">
                    {highlight.description}
                  </div>
                </div>
              </div>
            </div>
          ))}
        </div>
      </div>

      {/* Component Diagram */}
      <ComponentDiagram layers={architectureLayers} />

      {/* Architecture Flow Diagram */}
      <MermaidDiagram
        title="Autonomous Development Flow"
        chart={`flowchart TB
  subgraph Input["Input Layer"]
    GH["GitHub/GitLab Webhook"]
    CLI["CLI Command"]
    API["REST API"]
  end

  subgraph Orchestrator["ELSA Workflow Engine"]
    ADL["ADL Orchestrator"]
    SIC["Single Issue Cycle"]

    subgraph Workflows["Sub-Workflows"]
      IS["Issue Selection"]
      PG["Plan Generation"]
      TDD["TDD Cycle"]
      CR["Code Review"]
      MG["Merge"]
    end
  end

  subgraph Providers["AI Provider Layer"]
    PC["Provider Chain"]
    CB["Circuit Breaker"]
    RP["Role-Based Resolver"]

    subgraph AI["Providers"]
      Claude["Claude"]
      GPT["OpenAI"]
      OR["OpenRouter"]
      Local["Local LLMs"]
    end
  end

  subgraph Platform["Git Platform Layer"]
    GitHub["GitHub API"]
    GitLab["GitLab API"]
    Gitea["Gitea/Forgejo"]
  end

  subgraph Infra["Infrastructure"]
    PG_DB["PostgreSQL"]
    RMQ["RabbitMQ"]
    Chroma["ChromaDB"]
    OS["OpenSearch"]
  end

  GH --> ADL
  CLI --> ADL
  API --> ADL
  ADL --> SIC
  SIC --> IS & PG & TDD & CR & MG
  IS & PG & TDD & CR --> PC
  PC --> CB --> RP
  RP --> Claude & GPT & OR & Local
  MG --> GitHub & GitLab & Gitea
  ADL --> PG_DB & RMQ
  TDD --> Chroma
  ADL --> OS`}
      />

      <MermaidDiagram
        title="Security Pipeline"
        chart={`flowchart LR
  Input["User/LLM Input"] --> CS["Content Sanitizer"]
  CS --> |"HTML strip, zero-width removal"| PH["Prompt Hardening"]
  PH --> |"Anti-extraction preamble"| LLM["LLM Call"]
  LLM --> |"Tool requests"| TV["Tool Validator"]
  TV --> |"Allowlist + schema check"| AG["Action Gate"]
  AG --> |"Dangerous op check"| TE["Tool Executor"]
  TE --> |"Output"| RS["RedactSecrets"]
  RS --> |"10 patterns"| OV["Output Validator"]
  OV --> |"Clean output"| LLM

  style CS fill:#1e3a5f,stroke:#3b82f6
  style PH fill:#1e3a5f,stroke:#3b82f6
  style TV fill:#3b1e1e,stroke:#ef4444
  style AG fill:#3b1e1e,stroke:#ef4444
  style RS fill:#1e3b1e,stroke:#22c55e
  style OV fill:#1e3b1e,stroke:#22c55e`}
      />

      {/* Collapsible Sections */}
      {topLevelSections
        .filter((s) => !specialSections.has(s.heading) && s.heading)
        .map((section, i) => {
          // Build combined content including children
          let fullContent = section.content;
          for (const child of section.children) {
            const prefix = '#'.repeat(child.level);
            fullContent += `\n\n${prefix} ${child.heading}\n\n${child.content}`;
          }

          return (
            <CollapsibleSection
              key={i}
              heading={section.heading}
              defaultOpen={i < 2}
            >
              <SectionContent
                content={fullContent}
                heading={section.heading}
              />
            </CollapsibleSection>
          );
        })}

      {/* Footer */}
      <div className="border-t border-zinc-800 pt-6 text-xs text-zinc-600">
        <Link to="/roadmap" className="text-zinc-500 hover:text-zinc-400">
          View roadmap
        </Link>{' '}
        ·{' '}
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
