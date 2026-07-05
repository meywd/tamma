/**
 * File Discovery
 *
 * Recursively scans directories to find source files for indexing,
 * respecting include/exclude patterns and .gitignore.
 */

import * as fs from 'node:fs';
import * as path from 'node:path';
import { GitignoreParser, createGitignoreParser } from './gitignore-parser.js';
import type { DiscoveredFile, SupportedLanguage } from '../types.js';
import { DiscoveryError } from '../errors.js';

/**
 * Language detection by file extension
 */
const EXTENSION_LANGUAGE_MAP: Record<string, SupportedLanguage> = {
  // TypeScript
  '.ts': 'typescript',
  '.tsx': 'typescript',
  '.mts': 'typescript',
  '.cts': 'typescript',

  // JavaScript
  '.js': 'javascript',
  '.jsx': 'javascript',
  '.mjs': 'javascript',
  '.cjs': 'javascript',

  // Python
  '.py': 'python',
  '.pyi': 'python',
  '.pyw': 'python',

  // Go
  '.go': 'go',

  // Rust
  '.rs': 'rust',

  // Java
  '.java': 'java',
};

/**
 * Default include patterns for source files
 */
export const DEFAULT_INCLUDE_PATTERNS: string[] = [
  '**/*.ts',
  '**/*.tsx',
  '**/*.js',
  '**/*.jsx',
  '**/*.mjs',
  '**/*.cjs',
  '**/*.py',
  '**/*.go',
  '**/*.rs',
  '**/*.java',
];

/**
 * Default exclude patterns
 */
export const DEFAULT_EXCLUDE_PATTERNS: string[] = [
  '**/node_modules/**',
  '**/dist/**',
  '**/build/**',
  '**/.git/**',
  '**/vendor/**',
  '**/target/**',
  '**/__pycache__/**',
  '**/venv/**',
  '**/.venv/**',
  '**/coverage/**',
  '**/*.test.ts',
  '**/*.spec.ts',
  '**/*.test.js',
  '**/*.spec.js',
  '**/*.test.tsx',
  '**/*.spec.tsx',
  '**/__tests__/**',
  '**/*.d.ts',
  '**/*.min.js',
  '**/*.bundle.js',
];

/**
 * File discovery options
 */
export interface FileDiscoveryOptions {
  /** Glob patterns for files to include */
  includePatterns?: string[];
  /** Glob patterns for files to exclude */
  excludePatterns?: string[];
  /** Whether to respect .gitignore */
  respectGitignore?: boolean;
  /** Maximum directory depth to scan */
  maxDepth?: number;
}

/**
 * File discovery service for finding source files to index
 */
export class FileDiscovery {
  private projectRoot: string;
  private includePatterns: string[];
  private excludePatterns: string[];
  private respectGitignore: boolean;
  private maxDepth: number;
  private gitignoreParser: GitignoreParser | null = null;

  constructor(projectRoot: string, options: FileDiscoveryOptions = {}) {
    this.projectRoot = path.resolve(projectRoot);
    this.includePatterns = options.includePatterns ?? DEFAULT_INCLUDE_PATTERNS;
    this.excludePatterns = options.excludePatterns ?? DEFAULT_EXCLUDE_PATTERNS;
    this.respectGitignore = options.respectGitignore ?? true;
    this.maxDepth = options.maxDepth ?? Infinity;
  }

  /**
   * Initialize the file discovery (load .gitignore if needed)
   */
  async initialize(): Promise<void> {
    if (this.respectGitignore) {
      this.gitignoreParser = await createGitignoreParser(this.projectRoot);
    }
  }

  /**
   * Discover all source files in the project
   * @returns Array of discovered files
   */
  async discover(): Promise<DiscoveredFile[]> {
    const files: DiscoveredFile[] = [];

    try {
      await this.scanDirectory(this.projectRoot, '', 0, files);
    } catch (error) {
      throw new DiscoveryError(`Failed to discover files in ${this.projectRoot}`, {
        cause: error instanceof Error ? error : new Error(String(error)),
        context: { projectRoot: this.projectRoot },
      });
    }

    return files;
  }

  /**
   * Recursively scan a directory for source files
   */
  private async scanDirectory(
    absolutePath: string,
    relativePath: string,
    depth: number,
    files: DiscoveredFile[],
  ): Promise<void> {
    if (depth > this.maxDepth) {
      return;
    }

    let entries: fs.Dirent[];
    try {
      entries = await fs.promises.readdir(absolutePath, { withFileTypes: true });
    } catch (error) {
      // Skip directories we can't read
      return;
    }

    for (const entry of entries) {
      const entryRelPath = relativePath ? `${relativePath}/${entry.name}` : entry.name;
      const entryAbsPath = path.join(absolutePath, entry.name);

      // Check if should be excluded
      if (this.shouldExclude(entryRelPath, entry.isDirectory())) {
        continue;
      }

      if (entry.isDirectory()) {
        await this.scanDirectory(entryAbsPath, entryRelPath, depth + 1, files);
      } else if (entry.isFile()) {
        if (this.shouldInclude(entryRelPath)) {
          const stat = await fs.promises.stat(entryAbsPath);
          const language = this.detectLanguage(entry.name);

          files.push({
            absolutePath: entryAbsPath,
            relativePath: entryRelPath,
            language,
            sizeBytes: stat.size,
            lastModified: stat.mtime,
          });
        }
      }
    }
  }

  /**
   * Check if a path should be excluded
   */
  private shouldExclude(relativePath: string, isDirectory: boolean): boolean {
    // Check gitignore first
    if (this.gitignoreParser?.isIgnored(relativePath, isDirectory)) {
      return true;
    }

    // Check exclude patterns
    return this.matchesAnyPattern(relativePath, this.excludePatterns, isDirectory);
  }

  /**
   * Check if a file should be included
   */
  private shouldInclude(relativePath: string): boolean {
    return this.matchesAnyPattern(relativePath, this.includePatterns, false);
  }

  /**
   * Check if path matches any glob pattern
   */
  private matchesAnyPattern(
    relativePath: string,
    patterns: string[],
    isDirectory: boolean,
  ): boolean {
    for (const pattern of patterns) {
      if (this.matchPattern(relativePath, pattern, isDirectory)) {
        return true;
      }
    }
    return false;
  }

  /**
   * Match a path against a glob pattern
   * Simplified glob matching (supports *, **, ?)
   */
  private matchPattern(
    relativePath: string,
    pattern: string,
    _isDirectory: boolean,
  ): boolean {
    // Convert glob pattern to regex
    let regexStr = '^';

    let i = 0;
    while (i < pattern.length) {
      const char = pattern[i]!;

      if (char === '*') {
        if (pattern[i + 1] === '*') {
          if (pattern[i + 2] === '/') {
            // **/ matches zero or more path segments
            regexStr += '(?:.*?/)?';
            i += 3;
            continue;
          } else if (i + 2 >= pattern.length) {
            // ** at end matches everything
            regexStr += '.*';
            i += 2;
            continue;
          } else {
            // ** not followed by / - treat as two *
            regexStr += '[^/]*[^/]*';
            i += 2;
            continue;
          }
        }
        // Single * matches anything except /
        regexStr += '[^/]*';
      } else if (char === '?') {
        regexStr += '[^/]';
      } else if ('.+^${}()|[]\\'.includes(char)) {
        regexStr += '\\' + char;
      } else {
        regexStr += char;
      }
      i++;
    }

    regexStr += '$';

    try {
      const regex = new RegExp(regexStr);
      return regex.test(relativePath);
    } catch {
      return false;
    }
  }

  /**
   * Detect programming language from file extension
   */
  private detectLanguage(filename: string): SupportedLanguage {
    const ext = path.extname(filename).toLowerCase();
    return EXTENSION_LANGUAGE_MAP[ext] ?? 'unknown';
  }

  /**
   * Get project root path
   */
  getProjectRoot(): string {
    return this.projectRoot;
  }

  /**
   * Detect language from file path (static utility)
   * @param filePath - File path or name
   * @returns Detected language
   */
  static detectLanguage(filePath: string): SupportedLanguage {
    const ext = path.extname(filePath).toLowerCase();
    return EXTENSION_LANGUAGE_MAP[ext] ?? 'unknown';
  }
}

/**
 * Create and initialize a file discovery instance
 * @param projectRoot - Project root path
 * @param options - Discovery options
 * @returns Initialized FileDiscovery instance
 */
export async function createFileDiscovery(
  projectRoot: string,
  options?: FileDiscoveryOptions,
): Promise<FileDiscovery> {
  const discovery = new FileDiscovery(projectRoot, options);
  await discovery.initialize();
  return discovery;
}
