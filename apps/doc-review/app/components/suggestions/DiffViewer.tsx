import { useState, useMemo } from 'react';
import { Copy, Check, ChevronDown, ChevronUp } from 'lucide-react';
import type { DiffLine } from '~/lib/types/suggestion';
import {
  parseUnifiedDiff,
  collapseUnchangedLines,
  getDiffSummary,
} from '~/lib/utils/diff-parser';

interface DiffViewerProps {
  diffString: string;
  originalText: string;
  suggestedText: string;
  viewMode?: 'unified' | 'split';
  showCollapse?: boolean;
  contextLines?: number;
  className?: string;
}

export function DiffViewer({
  diffString,
  originalText,
  suggestedText,
  viewMode = 'unified',
  showCollapse = true,
  contextLines = 3,
  className = '',
}: DiffViewerProps) {
  const [mode, setMode] = useState<'unified' | 'split'>(viewMode);
  const [collapsed, setCollapsed] = useState(showCollapse);
  const [copiedSuggested, setCopiedSuggested] = useState(false);

  const parsedDiff = useMemo(() => {
    try {
      return parseUnifiedDiff(diffString);
    } catch (error) {
      console.error('Failed to parse diff:', error);
      return null;
    }
  }, [diffString]);

  const summary = useMemo(
    () => (parsedDiff ? getDiffSummary(parsedDiff) : null),
    [parsedDiff]
  );

  const processedHunks = useMemo(() => {
    if (!parsedDiff) return [];
    return parsedDiff.hunks.map((hunk) =>
      collapsed ? collapseUnchangedLines(hunk, contextLines) : hunk
    );
  }, [parsedDiff, collapsed, contextLines]);

  const handleCopy = async (text: string) => {
    try {
      await navigator.clipboard.writeText(text);
      setCopiedSuggested(true);
      setTimeout(() => setCopiedSuggested(false), 2000);
    } catch (error) {
      console.error('Failed to copy text:', error);
    }
  };

  if (!parsedDiff) {
    return (
      <div className={`rounded-lg border border-red-200 bg-red-50 p-4 ${className}`}>
        <p className="text-sm text-red-800">Failed to parse diff content.</p>
      </div>
    );
  }

  return (
    <div className={`rounded-lg border border-gray-200 bg-white ${className}`}>
      {/* Header with controls */}
      <div className="flex items-center justify-between border-b border-gray-200 bg-gray-50 px-4 py-2">
        <div className="flex items-center gap-4">
          <div className="text-sm font-medium text-gray-900">Changes</div>
          {summary && (
            <div className="flex items-center gap-3 text-xs">
              <span className="text-green-600">+{summary.additions} additions</span>
              <span className="text-red-600">-{summary.deletions} deletions</span>
            </div>
          )}
        </div>

        <div className="flex items-center gap-2">
          {/* View mode toggle */}
          <div className="flex rounded-md border border-gray-300 bg-white">
            <button
              type="button"
              onClick={() => setMode('unified')}
              className={`px-3 py-1 text-xs font-medium ${
                mode === 'unified'
                  ? 'bg-indigo-100 text-indigo-700'
                  : 'text-gray-600 hover:bg-gray-50'
              }`}
            >
              Unified
            </button>
            <button
              type="button"
              onClick={() => setMode('split')}
              className={`border-l border-gray-300 px-3 py-1 text-xs font-medium ${
                mode === 'split'
                  ? 'bg-indigo-100 text-indigo-700'
                  : 'text-gray-600 hover:bg-gray-50'
              }`}
            >
              Split
            </button>
          </div>

          {/* Collapse toggle */}
          {showCollapse && (
            <button
              type="button"
              onClick={() => setCollapsed(!collapsed)}
              className="flex items-center gap-1 rounded-md border border-gray-300 bg-white px-3 py-1 text-xs font-medium text-gray-600 hover:bg-gray-50"
              title={collapsed ? 'Show all lines' : 'Collapse unchanged lines'}
            >
              {collapsed ? (
                <>
                  <ChevronDown className="h-3 w-3" />
                  Expand
                </>
              ) : (
                <>
                  <ChevronUp className="h-3 w-3" />
                  Collapse
                </>
              )}
            </button>
          )}

          {/* Copy buttons */}
          <button
            type="button"
            onClick={async () => handleCopy(suggestedText)}
            className="flex items-center gap-1 rounded-md border border-gray-300 bg-white px-3 py-1 text-xs font-medium text-gray-600 hover:bg-gray-50"
            title="Copy suggested text"
          >
            {copiedSuggested ? (
              <>
                <Check className="h-3 w-3 text-green-600" />
                Copied
              </>
            ) : (
              <>
                <Copy className="h-3 w-3" />
                Copy New
              </>
            )}
          </button>
        </div>
      </div>

      {/* Diff content */}
      <div className="overflow-x-auto">
        {mode === 'unified' ? (
          <UnifiedDiffView hunks={processedHunks} />
        ) : (
          <SplitDiffView originalText={originalText} suggestedText={suggestedText} />
        )}
      </div>
    </div>
  );
}

interface UnifiedDiffViewProps {
  hunks: Array<{
    oldStart: number;
    oldLines: number;
    newStart: number;
    newLines: number;
    lines: DiffLine[];
  }>;
}

function UnifiedDiffView({ hunks }: UnifiedDiffViewProps) {
  return (
    <div className="font-mono text-xs">
      {hunks.map((hunk, hunkIndex) => (
        <div key={hunkIndex} className="border-b border-gray-100 last:border-b-0">
          {hunk.lines.map((line, lineIndex) => (
            <DiffLineComponent key={lineIndex} line={line} />
          ))}
        </div>
      ))}
    </div>
  );
}

interface DiffLineComponentProps {
  line: DiffLine;
}

function DiffLineComponent({ line }: DiffLineComponentProps) {
  const bgColor = {
    add: 'bg-green-50 hover:bg-green-100',
    remove: 'bg-red-50 hover:bg-red-100',
    normal: 'bg-white hover:bg-gray-50',
    header: 'bg-blue-50',
  }[line.type];

  const textColor = {
    add: 'text-green-900',
    remove: 'text-red-900',
    normal: 'text-gray-800',
    header: 'text-blue-700 font-semibold',
  }[line.type];

  const prefix = {
    add: '+',
    remove: '-',
    normal: ' ',
    header: '',
  }[line.type];

  return (
    <div className={`flex ${bgColor} transition-colors`}>
      {/* Line numbers */}
      {line.type !== 'header' && (
        <div className="flex border-r border-gray-200 bg-gray-50">
          <div className="w-12 px-2 text-right text-gray-500">
            {line.lineNumberBefore ?? ''}
          </div>
          <div className="w-12 px-2 text-right text-gray-500">
            {line.lineNumberAfter ?? ''}
          </div>
        </div>
      )}

      {/* Content */}
      <div className={`flex-1 px-3 py-0.5 ${textColor}`}>
        <span className="mr-2 select-none opacity-50">{prefix}</span>
        <span className="whitespace-pre-wrap break-all">
          {line.content || ' '}
        </span>
      </div>
    </div>
  );
}

interface SplitDiffViewProps {
  originalText: string;
  suggestedText: string;
}

function SplitDiffView({ originalText, suggestedText }: SplitDiffViewProps) {
  const originalLines = originalText.split('\n');
  const suggestedLines = suggestedText.split('\n');

  return (
    <div className="grid grid-cols-2 divide-x divide-gray-200">
      {/* Original side */}
      <div>
        <div className="sticky top-0 border-b border-gray-200 bg-red-50 px-3 py-2 text-xs font-semibold text-red-900">
          Original
        </div>
        <div className="font-mono text-xs">
          {originalLines.map((line, index) => (
            <div
              key={index}
              className="flex border-b border-gray-100 bg-red-50 hover:bg-red-100"
            >
              <div className="w-12 border-r border-gray-200 bg-gray-50 px-2 text-right text-gray-500">
                {index + 1}
              </div>
              <div className="flex-1 px-3 py-0.5 text-red-900">
                <span className="whitespace-pre-wrap break-all">{line || ' '}</span>
              </div>
            </div>
          ))}
        </div>
      </div>

      {/* Suggested side */}
      <div>
        <div className="sticky top-0 border-b border-gray-200 bg-green-50 px-3 py-2 text-xs font-semibold text-green-900">
          Suggested
        </div>
        <div className="font-mono text-xs">
          {suggestedLines.map((line, index) => (
            <div
              key={index}
              className="flex border-b border-gray-100 bg-green-50 hover:bg-green-100"
            >
              <div className="w-12 border-r border-gray-200 bg-gray-50 px-2 text-right text-gray-500">
                {index + 1}
              </div>
              <div className="flex-1 px-3 py-0.5 text-green-900">
                <span className="whitespace-pre-wrap break-all">{line || ' '}</span>
              </div>
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}
