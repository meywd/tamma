/**
 * Generic Chunker
 *
 * Fallback chunker that uses line-based chunking with sliding window.
 * Used for languages without specific AST support.
 */

import { BaseChunker } from './base-chunker.js';
import type { CodeChunk, ChunkingStrategy, SupportedLanguage } from '../types.js';

/**
 * Generic line-based code chunker
 */
export class GenericChunker extends BaseChunker {
  readonly supportedLanguages: SupportedLanguage[] = [
    'python',
    'go',
    'rust',
    'java',
    'unknown',
  ];

  /**
   * Chunk content using line-based sliding window
   */
  async chunk(
    content: string,
    filePath: string,
    fileId: string,
    strategy: ChunkingStrategy,
  ): Promise<CodeChunk[]> {
    const chunks: CodeChunk[] = [];
    let chunkIndex = 0;

    // Try to detect logical boundaries in the content
    const sections = this.detectSections(content, strategy.language);

    if (sections.length === 0) {
      // No sections detected, use sliding window
      const splitChunks = this.splitByTokenLimit(
        content,
        1,
        strategy.maxChunkTokens,
        strategy.overlapTokens,
      );

      for (const split of splitChunks) {
        const chunk = this.createChunk(
          split.content,
          filePath,
          fileId,
          chunkIndex++,
          {
            chunkType: 'block',
            name: `block_${chunkIndex}`,
            startLine: split.startLine,
            endLine: split.endLine,
            language: strategy.language,
          },
        );
        chunks.push(chunk);
      }
    } else {
      // Process detected sections
      for (const section of sections) {
        const sectionContent = content
          .split('\n')
          .slice(section.startLine - 1, section.endLine)
          .join('\n');

        if (this.estimateTokens(sectionContent) > strategy.maxChunkTokens) {
          // Split large sections
          const splitChunks = this.splitByTokenLimit(
            sectionContent,
            section.startLine,
            strategy.maxChunkTokens,
            strategy.overlapTokens,
          );

          for (const split of splitChunks) {
            const chunk = this.createChunk(
              split.content,
              filePath,
              fileId,
              chunkIndex++,
              {
                chunkType: section.type,
                name: section.name || `${section.type}_${chunkIndex}`,
                startLine: split.startLine,
                endLine: split.endLine,
                language: strategy.language,
                docstring: section.docstring,
              },
            );
            chunks.push(chunk);
          }
        } else {
          const chunk = this.createChunk(
            sectionContent,
            filePath,
            fileId,
            chunkIndex++,
            {
              chunkType: section.type,
              name: section.name || `${section.type}_${chunkIndex}`,
              startLine: section.startLine,
              endLine: section.endLine,
              language: strategy.language,
              docstring: section.docstring,
            },
          );
          chunks.push(chunk);
        }
      }
    }

    return chunks;
  }

  /**
   * Detect logical sections in code based on language patterns
   */
  private detectSections(
    content: string,
    language: SupportedLanguage,
  ): Array<{
    type: CodeChunk['chunkType'];
    name?: string;
    startLine: number;
    endLine: number;
    docstring?: string | undefined;
  }> {
    const lines = content.split('\n');
    const sections: Array<{
      type: CodeChunk['chunkType'];
      name?: string;
      startLine: number;
      endLine: number;
      docstring?: string | undefined;
    }> = [];

    // Language-specific patterns
    const patterns = this.getLanguagePatterns(language);

    let i = 0;
    while (i < lines.length) {
      const line = lines[i]!;
      const lineNumber = i + 1;

      // Check for function/class definitions
      for (const pattern of patterns) {
        const match = line.match(pattern.regex);
        if (match) {
          const name = match[1] || 'anonymous';
          const startLine = lineNumber;

          // Find the end of this block
          const endLine = this.findBlockEnd(lines, i, language);

          // Check for docstring above
          const docstring = this.extractDocstring(lines, i - 1, language);

          sections.push({
            type: pattern.type,
            name,
            startLine: docstring ? startLine - this.countDocstringLines(lines, i - 1, language) : startLine,
            endLine,
            docstring,
          });

          i = endLine;
          break;
        }
      }

      i++;
    }

    return sections;
  }

  /**
   * Get regex patterns for detecting code sections by language
   */
  private getLanguagePatterns(
    language: SupportedLanguage,
  ): Array<{ regex: RegExp; type: CodeChunk['chunkType'] }> {
    switch (language) {
      case 'python':
        return [
          { regex: /^\s*(?:async\s+)?def\s+(\w+)\s*\(/, type: 'function' },
          { regex: /^\s*class\s+(\w+)/, type: 'class' },
        ];
      case 'go':
        return [
          { regex: /^func\s+(?:\([^)]+\)\s+)?(\w+)\s*\(/, type: 'function' },
          { regex: /^type\s+(\w+)\s+struct\s*\{/, type: 'class' },
          { regex: /^type\s+(\w+)\s+interface\s*\{/, type: 'interface' },
        ];
      case 'rust':
        return [
          { regex: /^\s*(?:pub\s+)?(?:async\s+)?fn\s+(\w+)/, type: 'function' },
          { regex: /^\s*(?:pub\s+)?struct\s+(\w+)/, type: 'class' },
          { regex: /^\s*(?:pub\s+)?trait\s+(\w+)/, type: 'interface' },
          { regex: /^\s*(?:pub\s+)?enum\s+(\w+)/, type: 'enum' },
          { regex: /^\s*impl\s+(\w+)/, type: 'class' },
        ];
      case 'java':
        return [
          {
            regex: /^\s*(?:public|private|protected)?\s*(?:static)?\s*(?:final)?\s*(?:\w+\s+)?(\w+)\s*\([^)]*\)\s*(?:throws\s+\w+(?:,\s*\w+)*)?\s*\{/,
            type: 'function',
          },
          {
            regex: /^\s*(?:public|private|protected)?\s*(?:abstract|final)?\s*class\s+(\w+)/,
            type: 'class',
          },
          {
            regex: /^\s*(?:public|private|protected)?\s*interface\s+(\w+)/,
            type: 'interface',
          },
          { regex: /^\s*(?:public|private|protected)?\s*enum\s+(\w+)/, type: 'enum' },
        ];
      default:
        return [];
    }
  }

  /**
   * Find the end of a code block (matching braces or indentation)
   */
  private findBlockEnd(
    lines: string[],
    startIndex: number,
    language: SupportedLanguage,
  ): number {
    if (language === 'python') {
      // Python uses indentation
      return this.findPythonBlockEnd(lines, startIndex);
    }

    // Brace-based languages
    return this.findBraceBlockEnd(lines, startIndex);
  }

  /**
   * Find end of Python indentation-based block
   */
  private findPythonBlockEnd(lines: string[], startIndex: number): number {
    const startLine = lines[startIndex]!;
    const startIndent = this.getIndentation(startLine);

    // Find the colon that starts the block
    const colonMatch = startLine.match(/:\s*$/);
    if (!colonMatch) {
      // No colon found, return same line
      return startIndex + 1;
    }

    // Look for the first line with content at the block's indentation level
    for (let i = startIndex + 1; i < lines.length; i++) {
      const line = lines[i]!;
      const trimmed = line.trim();

      // Skip empty lines and comments
      if (!trimmed || trimmed.startsWith('#')) {
        continue;
      }

      const indent = this.getIndentation(line);

      // If we find a line with less or equal indentation, block ends
      if (indent <= startIndent) {
        return i;
      }
    }

    return lines.length;
  }

  /**
   * Find end of brace-based block
   */
  private findBraceBlockEnd(lines: string[], startIndex: number): number {
    let braceCount = 0;
    let foundOpenBrace = false;

    for (let i = startIndex; i < lines.length; i++) {
      const line = lines[i]!;

      // Count braces, ignoring strings and comments
      for (const char of line) {
        if (char === '{') {
          braceCount++;
          foundOpenBrace = true;
        } else if (char === '}') {
          braceCount--;
          if (foundOpenBrace && braceCount === 0) {
            return i + 1;
          }
        }
      }
    }

    return lines.length;
  }

  /**
   * Get indentation level of a line
   */
  private getIndentation(line: string): number {
    const match = line.match(/^(\s*)/);
    if (!match) return 0;
    const spaces = match[1]!;
    // Convert tabs to 4 spaces
    return spaces.replace(/\t/g, '    ').length;
  }

  /**
   * Extract docstring from lines above a definition
   */
  private extractDocstring(
    lines: string[],
    startIndex: number,
    language: SupportedLanguage,
  ): string | undefined {
    if (startIndex < 0) return undefined;

    switch (language) {
      case 'python':
        return this.extractPythonDocstring(lines, startIndex);
      case 'go':
        return this.extractGoDocstring(lines, startIndex);
      case 'rust':
        return this.extractRustDocstring(lines, startIndex);
      case 'java':
        return this.extractJavaDocstring(lines, startIndex);
      default:
        return undefined;
    }
  }

  /**
   * Count lines in docstring above definition
   */
  private countDocstringLines(
    lines: string[],
    startIndex: number,
    language: SupportedLanguage,
  ): number {
    if (startIndex < 0) return 0;

    const docstring = this.extractDocstring(lines, startIndex, language);
    if (!docstring) return 0;

    return docstring.split('\n').length;
  }

  /**
   * Extract Python docstring (triple-quoted string)
   */
  private extractPythonDocstring(_lines: string[], _startIndex: number): string | undefined {
    // Check for docstring in the line after the definition
    // Python docstrings are inside the function
    return undefined;
  }

  /**
   * Extract Go documentation comment
   */
  private extractGoDocstring(lines: string[], startIndex: number): string | undefined {
    const comments: string[] = [];
    let i = startIndex;

    while (i >= 0) {
      const line = lines[i]!.trim();
      if (line.startsWith('//')) {
        comments.unshift(line.slice(2).trim());
        i--;
      } else if (!line) {
        i--;
      } else {
        break;
      }
    }

    return comments.length > 0 ? comments.join('\n') : undefined;
  }

  /**
   * Extract Rust doc comment (///)
   */
  private extractRustDocstring(lines: string[], startIndex: number): string | undefined {
    const comments: string[] = [];
    let i = startIndex;

    while (i >= 0) {
      const line = lines[i]!.trim();
      if (line.startsWith('///')) {
        comments.unshift(line.slice(3).trim());
        i--;
      } else if (line.startsWith('//!')) {
        comments.unshift(line.slice(3).trim());
        i--;
      } else if (!line) {
        i--;
      } else {
        break;
      }
    }

    return comments.length > 0 ? comments.join('\n') : undefined;
  }

  /**
   * Extract Java Javadoc comment
   */
  private extractJavaDocstring(lines: string[], startIndex: number): string | undefined {
    // Look backwards for /** ... */
    let i = startIndex;
    let inComment = false;
    const commentLines: string[] = [];

    while (i >= 0) {
      const line = lines[i]!.trim();

      if (line.endsWith('*/') && !inComment) {
        inComment = true;
        const content = line.slice(0, -2).trim();
        if (content && !content.startsWith('/**')) {
          commentLines.unshift(content.replace(/^\*\s*/, ''));
        }
      } else if (inComment) {
        if (line.startsWith('/**')) {
          const content = line.slice(3).trim();
          if (content) {
            commentLines.unshift(content.replace(/^\*\s*/, ''));
          }
          break;
        } else if (line.startsWith('*')) {
          const content = line.slice(1).trim();
          commentLines.unshift(content);
        }
      } else if (line && !line.startsWith('@')) {
        // Non-empty, non-annotation line - stop looking
        break;
      }

      i--;
    }

    return commentLines.length > 0 ? commentLines.join('\n') : undefined;
  }
}
