/**
 * Gitignore Parser
 *
 * Parses .gitignore files and provides pattern matching for ignored files.
 */

import * as fs from 'node:fs';
import * as path from 'node:path';

/**
 * A parsed gitignore rule
 */
interface GitignoreRule {
  /** Original pattern string */
  pattern: string;
  /** Whether this is a negation rule (starts with !) */
  negation: boolean;
  /** Whether pattern only matches directories (ends with /) */
  directoryOnly: boolean;
  /** Regex for matching */
  regex: RegExp;
}

/** Maximum allowed length for a single gitignore pattern */
const MAX_PATTERN_LENGTH = 1024;

/**
 * Gitignore parser and matcher
 */
export class GitignoreParser {
  private rules: GitignoreRule[] = [];
  private projectRoot: string;

  constructor(projectRoot: string) {
    this.projectRoot = projectRoot;
  }

  /**
   * Load and parse .gitignore from project root
   * @returns This instance for chaining
   */
  async load(): Promise<GitignoreParser> {
    const gitignorePath = path.join(this.projectRoot, '.gitignore');

    try {
      const content = await fs.promises.readFile(gitignorePath, 'utf-8');
      this.parseContent(content);
    } catch (error) {
      // .gitignore doesn't exist - that's fine
      if ((error as NodeJS.ErrnoException).code !== 'ENOENT') {
        throw error;
      }
    }

    return this;
  }

  /**
   * Parse gitignore content string
   * @param content - Content of .gitignore file
   */
  parseContent(content: string): void {
    const lines = content.split(/\r?\n/);

    for (const line of lines) {
      const rule = this.parseLine(line);
      if (rule) {
        this.rules.push(rule);
      }
    }
  }

  /**
   * Parse a single line from .gitignore
   * @param line - Line to parse
   * @returns Parsed rule or null if line should be ignored
   */
  private parseLine(line: string): GitignoreRule | null {
    // Trim trailing whitespace (but not leading - it's significant for comments)
    let pattern = line.trimEnd();

    // Skip empty lines and comments
    if (!pattern || pattern.startsWith('#')) {
      return null;
    }

    // Handle negation
    const negation = pattern.startsWith('!');
    if (negation) {
      pattern = pattern.slice(1);
    }

    // Handle directory-only patterns
    const directoryOnly = pattern.endsWith('/');
    if (directoryOnly) {
      pattern = pattern.slice(0, -1);
    }

    // Trim leading spaces now
    pattern = pattern.trimStart();

    if (!pattern) {
      return null;
    }

    // Reject overly long patterns to prevent ReDoS
    if (pattern.length > MAX_PATTERN_LENGTH) {
      return null;
    }

    // Convert gitignore pattern to regex with safety checks
    let regex: RegExp;
    try {
      regex = this.patternToRegex(pattern);
    } catch {
      // If regex compilation fails, skip this pattern
      return null;
    }

    return {
      pattern,
      negation,
      directoryOnly,
      regex,
    };
  }

  /**
   * Convert gitignore glob pattern to regex
   * @param pattern - Gitignore pattern
   * @returns Compiled regex
   */
  private patternToRegex(pattern: string): RegExp {
    let regexStr = '';

    // If pattern doesn't contain /, it matches in any directory
    const matchAnywhere = !pattern.includes('/');

    // If pattern starts with /, it's relative to root
    if (pattern.startsWith('/')) {
      pattern = pattern.slice(1);
      regexStr = '^';
    } else if (matchAnywhere) {
      regexStr = '(?:^|/)';
    } else {
      regexStr = '^';
    }

    // Escape special regex characters except * and ?
    let i = 0;
    while (i < pattern.length) {
      const char = pattern[i]!;

      if (char === '*') {
        // Check for **
        if (pattern[i + 1] === '*') {
          if (pattern[i + 2] === '/') {
            // **/ matches zero or more directories
            regexStr += '(?:.*/)?';
            i += 3;
            continue;
          } else if (i + 2 >= pattern.length) {
            // ** at end matches everything
            regexStr += '.*';
            i += 2;
            continue;
          }
        }
        // Single * matches anything except /
        regexStr += '[^/]*';
      } else if (char === '?') {
        regexStr += '[^/]';
      } else if (char === '[') {
        // Character class - find closing bracket
        const closeBracket = pattern.indexOf(']', i + 1);
        if (closeBracket === -1) {
          regexStr += '\\[';
        } else {
          const charClass = pattern.slice(i, closeBracket + 1);
          regexStr += charClass;
          i = closeBracket;
        }
      } else if ('.+^${}()|\\'.includes(char)) {
        regexStr += '\\' + char;
      } else {
        regexStr += char;
      }
      i++;
    }

    // Pattern should match to end of path or directory boundary
    if (!regexStr.endsWith('.*')) {
      regexStr += '(?:/|$)';
    }

    return new RegExp(regexStr);
  }

  /**
   * Check if a path should be ignored
   * @param relativePath - Path relative to project root
   * @param isDirectory - Whether the path is a directory
   * @returns True if path should be ignored
   */
  isIgnored(relativePath: string, isDirectory: boolean = false): boolean {
    // Normalize path separators
    const normalizedPath = relativePath.replace(/\\/g, '/');

    let ignored = false;

    for (const rule of this.rules) {
      // Skip directory-only rules for files
      if (rule.directoryOnly && !isDirectory) {
        continue;
      }

      try {
        if (rule.regex.test(normalizedPath)) {
          ignored = !rule.negation;
        }
      } catch {
        // Skip rules whose regex execution fails
        continue;
      }
    }

    return ignored;
  }

  /**
   * Add additional patterns (e.g., from config)
   * @param patterns - Array of gitignore-style patterns
   */
  addPatterns(patterns: string[]): void {
    for (const pattern of patterns) {
      const rule = this.parseLine(pattern);
      if (rule) {
        this.rules.push(rule);
      }
    }
  }

  /**
   * Get number of rules loaded
   * @returns Number of active rules
   */
  getRuleCount(): number {
    return this.rules.length;
  }

  /**
   * Clear all rules
   */
  clear(): void {
    this.rules = [];
  }
}

/**
 * Create and load a gitignore parser
 * @param projectRoot - Path to project root
 * @returns Loaded GitignoreParser instance
 */
export async function createGitignoreParser(
  projectRoot: string,
): Promise<GitignoreParser> {
  const parser = new GitignoreParser(projectRoot);
  await parser.load();
  return parser;
}
