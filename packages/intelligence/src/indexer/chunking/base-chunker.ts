/**
 * Base Chunker
 *
 * Base class for code chunkers with common functionality.
 */

import type {
  CodeChunk,
  ChunkingStrategy,
  SupportedLanguage,
  ICodeChunker,
} from '../types.js';
import { estimateTokens } from '../metadata/token-counter.js';
import { calculateHash, generateChunkId } from '../metadata/hash-calculator.js';

/**
 * Abstract base class for code chunkers
 */
export abstract class BaseChunker implements ICodeChunker {
  abstract readonly supportedLanguages: SupportedLanguage[];

  /**
   * Chunk a file's content into semantic units
   */
  abstract chunk(
    content: string,
    filePath: string,
    fileId: string,
    strategy: ChunkingStrategy,
  ): Promise<CodeChunk[]>;

  /**
   * Estimate token count for content
   */
  estimateTokens(content: string): number {
    return estimateTokens(content);
  }

  /**
   * Create a code chunk with calculated metadata
   */
  protected createChunk(
    content: string,
    filePath: string,
    fileId: string,
    chunkIndex: number,
    options: {
      chunkType: CodeChunk['chunkType'];
      name: string;
      startLine: number;
      endLine: number;
      language: SupportedLanguage;
      parentScope?: string;
      imports?: string[];
      exports?: string[];
      docstring?: string | undefined;
    },
  ): CodeChunk {
    const hash = calculateHash(content);
    const tokenCount = this.estimateTokens(content);
    const id = generateChunkId(fileId, chunkIndex, content);

    return {
      id,
      fileId,
      filePath,
      language: options.language,
      chunkType: options.chunkType,
      name: options.name,
      content,
      startLine: options.startLine,
      endLine: options.endLine,
      parentScope: options.parentScope,
      imports: options.imports ?? [],
      exports: options.exports ?? [],
      docstring: options.docstring,
      tokenCount,
      hash,
    };
  }

  /**
   * Split content that exceeds token limit using sliding window
   */
  protected splitByTokenLimit(
    content: string,
    startLine: number,
    maxTokens: number,
    overlapTokens: number,
  ): Array<{ content: string; startLine: number; endLine: number }> {
    const lines = content.split('\n');
    const chunks: Array<{ content: string; startLine: number; endLine: number }> = [];

    if (this.estimateTokens(content) <= maxTokens) {
      return [{ content, startLine, endLine: startLine + lines.length - 1 }];
    }

    let currentChunkLines: string[] = [];
    let currentChunkStart = startLine;
    let overlapLines: string[] = [];

    for (let i = 0; i < lines.length; i++) {
      currentChunkLines.push(lines[i]!);
      const currentContent = currentChunkLines.join('\n');
      const currentTokens = this.estimateTokens(currentContent);

      if (currentTokens >= maxTokens && currentChunkLines.length > 1) {
        // Remove last line and save chunk
        currentChunkLines.pop();
        const chunkContent = currentChunkLines.join('\n');
        const chunkEndLine = currentChunkStart + currentChunkLines.length - 1;

        chunks.push({
          content: chunkContent,
          startLine: currentChunkStart,
          endLine: chunkEndLine,
        });

        // Calculate overlap lines based on token count
        overlapLines = [];
        let overlapTokenCount = 0;
        for (let j = currentChunkLines.length - 1; j >= 0 && overlapTokenCount < overlapTokens; j--) {
          overlapLines.unshift(currentChunkLines[j]!);
          overlapTokenCount = this.estimateTokens(overlapLines.join('\n'));
        }

        // Start new chunk with overlap + current line
        currentChunkLines = [...overlapLines, lines[i]!];
        currentChunkStart = currentChunkStart + (currentChunkLines.length - overlapLines.length - 1);
      }
    }

    // Don't forget the last chunk
    if (currentChunkLines.length > 0) {
      const chunkContent = currentChunkLines.join('\n');
      const chunkEndLine = currentChunkStart + currentChunkLines.length - 1;

      chunks.push({
        content: chunkContent,
        startLine: currentChunkStart,
        endLine: chunkEndLine,
      });
    }

    return chunks;
  }

  /**
   * Count lines in content
   */
  protected countLines(content: string): number {
    return content.split('\n').length;
  }

  /**
   * Get line number for a position in content
   */
  protected getLineNumber(content: string, position: number): number {
    return content.slice(0, position).split('\n').length;
  }
}
