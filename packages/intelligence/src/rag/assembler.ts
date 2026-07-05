/**
 * Context Assembler
 *
 * Assembles retrieved chunks into formatted context for LLM consumption.
 * Handles token budget management, formatting, and source attribution.
 */

import type {
  RetrievedChunk,
  AssemblyConfig,
  AssembledContext,
  ContextFormat,
  IContextAssembler,
} from './types.js';
import { estimateTokensSimple } from '../indexer/metadata/token-counter.js';

/**
 * Context assembler implementation
 */
export class ContextAssembler implements IContextAssembler {
  /**
   * Assemble chunks into formatted context within token budget
   */
  assemble(chunks: RetrievedChunk[], config: AssemblyConfig): AssembledContext {
    if (chunks.length === 0) {
      return {
        chunks: [],
        text: '',
        tokenCount: 0,
        truncated: false,
      };
    }

    const assembled: RetrievedChunk[] = [];
    let totalTokens = 0;
    let truncated = false;

    // Reserve tokens for formatting overhead
    const overheadTokens = this.estimateFormattingOverhead(config.format);
    const availableTokens = Math.max(config.maxTokens - overheadTokens, 1);

    for (const chunk of chunks) {
      const chunkTokens = this.countTokens(chunk.content);

      // Check if adding this chunk would exceed budget
      if (totalTokens + chunkTokens > availableTokens) {
        // Try to fit a truncated version
        const remaining = availableTokens - totalTokens;
        if (remaining > 0) {
          // Worth truncating
          const truncatedChunk = this.truncateChunk(chunk, remaining);
          assembled.push(truncatedChunk);
          totalTokens += remaining;
        }
        truncated = true;
        break;
      }

      assembled.push(chunk);
      totalTokens += chunkTokens;
    }

    // Format the context
    let text = this.formatContext(assembled, config);
    let actualTokens = this.countTokens(text);

    // Enforce maxTokens on formatted output by removing trailing chunks
    while (actualTokens > config.maxTokens && assembled.length > 1) {
      assembled.pop();
      truncated = true;
      text = this.formatContext(assembled, config);
      actualTokens = this.countTokens(text);
    }

    // If still over budget with a single chunk, accept minor overrun
    // rather than producing empty output (format wrapper may exceed tight budgets)
    if (actualTokens > config.maxTokens && assembled.length === 1) {
      truncated = true;
    }

    return {
      chunks: assembled,
      text,
      tokenCount: actualTokens,
      truncated,
    };
  }

  /**
   * Count tokens in text
   */
  countTokens(text: string): number {
    return estimateTokensSimple(text);
  }

  /**
   * Format context based on specified format
   */
  private formatContext(chunks: RetrievedChunk[], config: AssemblyConfig): string {
    switch (config.format) {
      case 'xml':
        return this.formatAsXML(chunks, config.includeScores);
      case 'markdown':
        return this.formatAsMarkdown(chunks, config.includeScores);
      case 'json':
        return this.formatAsJSON(chunks, config.includeScores);
      case 'plain':
      default:
        return this.formatAsPlain(chunks, config.includeScores);
    }
  }

  /**
   * Format as XML (recommended for Claude)
   */
  private formatAsXML(chunks: RetrievedChunk[], includeScores: boolean): string {
    const parts = ['<retrieved_context>'];

    for (const chunk of chunks) {
      const scoreAttr = includeScores ? ` score="${(chunk.fusedScore ?? chunk.score).toFixed(3)}"` : '';
      const location = this.formatLocation(chunk);

      parts.push(`  <chunk source="${chunk.source}"${scoreAttr}>`);
      parts.push(`    <location>${this.escapeXML(location)}</location>`);
      parts.push(`    <content>`);
      parts.push(`${this.escapeXML(chunk.content)}`);
      parts.push(`    </content>`);
      parts.push(`  </chunk>`);
    }

    parts.push('</retrieved_context>');
    return parts.join('\n');
  }

  /**
   * Format as Markdown
   */
  private formatAsMarkdown(chunks: RetrievedChunk[], includeScores: boolean): string {
    const parts: string[] = [];

    for (let i = 0; i < chunks.length; i++) {
      const chunk = chunks[i]!;
      const location = this.formatLocation(chunk);
      const scoreText = includeScores
        ? ` (relevance: ${(chunk.fusedScore ?? chunk.score).toFixed(2)})`
        : '';

      parts.push(`### ${location}${scoreText}`);
      parts.push('');

      // Determine language for code fence
      const language = chunk.metadata.language ?? this.inferLanguage(chunk);
      parts.push(`\`\`\`${language}`);
      parts.push(chunk.content);
      parts.push('```');

      if (i < chunks.length - 1) {
        parts.push('');
        parts.push('---');
        parts.push('');
      }
    }

    return parts.join('\n');
  }

  /**
   * Format as JSON
   */
  private formatAsJSON(chunks: RetrievedChunk[], includeScores: boolean): string {
    const formatted = chunks.map((chunk) => ({
      source: chunk.source,
      location: this.formatLocation(chunk),
      ...(includeScores && { score: chunk.fusedScore ?? chunk.score }),
      content: chunk.content,
      metadata: {
        ...(chunk.metadata.language && { language: chunk.metadata.language }),
        ...(chunk.metadata.symbols?.length && { symbols: chunk.metadata.symbols }),
      },
    }));

    return JSON.stringify({ context: formatted }, null, 2);
  }

  /**
   * Format as plain text
   */
  private formatAsPlain(chunks: RetrievedChunk[], includeScores: boolean): string {
    const parts: string[] = [];

    for (const chunk of chunks) {
      const location = this.formatLocation(chunk);
      const scoreText = includeScores
        ? ` [score: ${(chunk.fusedScore ?? chunk.score).toFixed(2)}]`
        : '';

      parts.push(`// ${location}${scoreText}`);
      parts.push(chunk.content);
      parts.push('');
      parts.push('---');
      parts.push('');
    }

    return parts.join('\n').trim();
  }

  /**
   * Format location string from chunk metadata
   */
  private formatLocation(chunk: RetrievedChunk): string {
    const { filePath, startLine, endLine, url, title } = chunk.metadata;

    if (filePath) {
      if (startLine !== undefined && endLine !== undefined) {
        return `${filePath}:${startLine}-${endLine}`;
      }
      if (startLine !== undefined) {
        return `${filePath}:${startLine}`;
      }
      return filePath;
    }

    if (url) {
      return title ? `${title} (${url})` : url;
    }

    if (title) {
      return title;
    }

    return `${chunk.source}:${chunk.id}`;
  }

  /**
   * Infer programming language from chunk
   */
  private inferLanguage(chunk: RetrievedChunk): string {
    const { filePath } = chunk.metadata;
    if (!filePath) {
      return '';
    }

    const ext = filePath.split('.').pop()?.toLowerCase();
    const langMap: Record<string, string> = {
      ts: 'typescript',
      tsx: 'typescript',
      js: 'javascript',
      jsx: 'javascript',
      py: 'python',
      go: 'go',
      rs: 'rust',
      java: 'java',
      rb: 'ruby',
      php: 'php',
      cs: 'csharp',
      cpp: 'cpp',
      c: 'c',
      h: 'c',
      hpp: 'cpp',
      md: 'markdown',
      yaml: 'yaml',
      yml: 'yaml',
      json: 'json',
      xml: 'xml',
      html: 'html',
      css: 'css',
      scss: 'scss',
      sql: 'sql',
      sh: 'bash',
      bash: 'bash',
    };

    return ext ? langMap[ext] ?? '' : '';
  }

  /**
   * Truncate a chunk to fit within token limit
   */
  private truncateChunk(chunk: RetrievedChunk, maxTokens: number): RetrievedChunk {
    const indicator = '// ... (truncated)';
    const indicatorTokens = this.countTokens(indicator);
    const contentBudget = Math.max(0, maxTokens - indicatorTokens);

    const lines = chunk.content.split('\n');
    const truncatedLines: string[] = [];
    let tokens = 0;

    for (const line of lines) {
      const lineTokens = this.countTokens(line);
      if (tokens + lineTokens > contentBudget) {
        // Try word-level truncation for this line
        const remaining = contentBudget - tokens;
        if (remaining > 0) {
          const words = line.split(/\s+/);
          const fittingWords: string[] = [];
          let wordTokens = 0;
          for (const word of words) {
            const wt = this.countTokens(word);
            if (wordTokens + wt > remaining) break;
            fittingWords.push(word);
            wordTokens += wt;
          }
          if (fittingWords.length > 0) {
            truncatedLines.push(fittingWords.join(' '));
          }
        }
        break;
      }
      truncatedLines.push(line);
      tokens += lineTokens;
    }

    truncatedLines.push(indicator);

    return {
      ...chunk,
      content: truncatedLines.join('\n'),
    };
  }

  /**
   * Estimate formatting overhead for a given format
   */
  private estimateFormattingOverhead(format: ContextFormat): number {
    switch (format) {
      case 'xml':
        return 50; // Tags, structure, and per-chunk wrapper
      case 'markdown':
        return 30; // Headers and code fences
      case 'json':
        return 40; // JSON structure
      case 'plain':
      default:
        return 20; // Minimal overhead
    }
  }

  /**
   * Escape special XML characters
   */
  private escapeXML(text: string): string {
    return text
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;')
      .replace(/'/g, '&apos;');
  }
}

/**
 * Create an assembler instance
 */
export function createContextAssembler(): ContextAssembler {
  return new ContextAssembler();
}
