/**
 * Git Diff Detector
 *
 * Detects changed files using git commands for efficient incremental indexing.
 * Falls back gracefully when git is not available or the project is not a git repo.
 */

import { execFileSync } from 'node:child_process';
import * as path from 'node:path';

/**
 * Change type for a detected file
 */
export type ChangeType = 'added' | 'modified' | 'deleted';

/**
 * A detected file change from git
 */
export interface DetectedChange {
  /** Relative file path from project root */
  filePath: string;
  /** Type of change */
  changeType: ChangeType;
}

/**
 * Options for git diff detection
 */
export interface GitDiffOptions {
  /** Reference to diff against (default: HEAD~1) */
  ref?: string;
  /** Include untracked files (default: true) */
  includeUntracked?: boolean;
  /** Include staged changes (default: true) */
  includeStaged?: boolean;
  /** Include unstaged changes (default: true) */
  includeUnstaged?: boolean;
}

/**
 * Git diff-based change detector for incremental indexing
 */
export class GitDiffDetector {
  private projectRoot: string;

  constructor(projectRoot: string) {
    this.projectRoot = path.resolve(projectRoot);
  }

  /**
   * Check if the project directory is a git repository
   */
  isGitRepo(): boolean {
    try {
      execFileSync('git', ['rev-parse', '--is-inside-work-tree'], {
        cwd: this.projectRoot,
        stdio: 'pipe',
        encoding: 'utf-8',
      });
      return true;
    } catch {
      return false;
    }
  }

  /**
   * Detect changed files since a given reference
   *
   * Combines git diff and git status to detect all changes:
   * - Modified/added/deleted files since the reference commit
   * - Untracked files
   * - Staged but uncommitted changes
   *
   * @param options - Detection options
   * @returns Array of detected changes
   */
  detectChanges(options: GitDiffOptions = {}): DetectedChange[] {
    const {
      ref = 'HEAD~1',
      includeUntracked = true,
      includeStaged = true,
      includeUnstaged = true,
    } = options;

    const changes = new Map<string, DetectedChange>();

    // Get diff against the reference
    try {
      const diffOutput = this.execGit(['diff', '--name-status', ref]);
      this.parseDiffOutput(diffOutput, changes);
    } catch (error) {
      // ref might not exist (e.g., first commit), try HEAD
      console.warn(`[GitDiffDetector] git diff against ref "${ref}" failed, retrying with HEAD:`, error instanceof Error ? error.message : String(error));
      try {
        const diffOutput = this.execGit(['diff', '--name-status', 'HEAD']);
        this.parseDiffOutput(diffOutput, changes);
      } catch (fallbackError) {
        // No commits yet or git not available
        console.warn('[GitDiffDetector] git diff HEAD fallback also failed:', fallbackError instanceof Error ? fallbackError.message : String(fallbackError));
      }
    }

    // Get status for working directory changes
    try {
      const statusOutput = this.execGit(['status', '--porcelain']);
      this.parseStatusOutput(statusOutput, changes, {
        includeUntracked,
        includeStaged,
        includeUnstaged,
      });
    } catch (error) {
      console.warn('[GitDiffDetector] git status failed:', error instanceof Error ? error.message : String(error));
    }

    return Array.from(changes.values());
  }

  /**
   * Get files changed between two refs
   *
   * @param fromRef - Starting reference (e.g., commit SHA, branch name)
   * @param toRef - Ending reference (default: HEAD)
   * @returns Array of detected changes
   */
  detectChangesBetween(fromRef: string, toRef: string = 'HEAD'): DetectedChange[] {
    const changes = new Map<string, DetectedChange>();

    try {
      const diffOutput = this.execGit(
        ['diff', '--name-status', `${fromRef}...${toRef}`],
      );
      this.parseDiffOutput(diffOutput, changes);
    } catch {
      // refs might not exist
    }

    return Array.from(changes.values());
  }

  /**
   * Get only the file paths of changed files (convenience method)
   *
   * @param options - Detection options
   * @returns Array of changed file paths (relative to project root)
   */
  getChangedFiles(options: GitDiffOptions = {}): string[] {
    return this.detectChanges(options)
      .filter((c) => c.changeType !== 'deleted')
      .map((c) => c.filePath);
  }

  /**
   * Get only the file paths of deleted files (convenience method)
   *
   * @param options - Detection options
   * @returns Array of deleted file paths (relative to project root)
   */
  getDeletedFiles(options: GitDiffOptions = {}): string[] {
    return this.detectChanges(options)
      .filter((c) => c.changeType === 'deleted')
      .map((c) => c.filePath);
  }

  /**
   * Execute a git command and return stdout.
   * Uses execFileSync to avoid shell interpretation and prevent command injection.
   *
   * @param args - Array of arguments to pass to git (e.g., ['diff', '--name-status', 'HEAD'])
   */
  private execGit(args: string[]): string {
    return execFileSync('git', args, {
      cwd: this.projectRoot,
      stdio: 'pipe',
      encoding: 'utf-8',
      timeout: 10000,
    }).trim();
  }

  /**
   * Parse git diff --name-status output
   *
   * Format: STATUS\tFILENAME (optionally \tNEWFILENAME for renames)
   */
  private parseDiffOutput(
    output: string,
    changes: Map<string, DetectedChange>,
  ): void {
    if (!output) return;

    for (const line of output.split('\n')) {
      const trimmed = line.trim();
      if (!trimmed) continue;

      const parts = trimmed.split('\t');
      if (parts.length < 2) continue;

      const statusCode = parts[0].charAt(0);
      const filePath = parts[1];

      let changeType: ChangeType;
      switch (statusCode) {
        case 'A':
          changeType = 'added';
          break;
        case 'D':
          changeType = 'deleted';
          break;
        case 'M':
        case 'T': // Type change
          changeType = 'modified';
          break;
        case 'R': // Rename
          // Old file is deleted
          changes.set(filePath, { filePath, changeType: 'deleted' });
          // New file is added
          if (parts[2]) {
            changes.set(parts[2], { filePath: parts[2], changeType: 'added' });
          }
          continue;
        case 'C': // Copy
          if (parts[2]) {
            changes.set(parts[2], { filePath: parts[2], changeType: 'added' });
          }
          continue;
        default:
          changeType = 'modified';
      }

      changes.set(filePath, { filePath, changeType });
    }
  }

  /**
   * Parse git status --porcelain output
   *
   * Format: XY FILENAME
   * X = status in staging area, Y = status in working tree
   */
  private parseStatusOutput(
    output: string,
    changes: Map<string, DetectedChange>,
    options: {
      includeUntracked: boolean;
      includeStaged: boolean;
      includeUnstaged: boolean;
    },
  ): void {
    if (!output) return;

    for (const line of output.split('\n')) {
      if (line.length < 3) continue;

      const staging = line.charAt(0);
      const working = line.charAt(1);
      const filePath = line.substring(3).trim();

      if (!filePath) continue;

      // Untracked files
      if (staging === '?' && working === '?') {
        if (options.includeUntracked) {
          changes.set(filePath, { filePath, changeType: 'added' });
        }
        continue;
      }

      // Staged changes
      if (options.includeStaged && staging !== ' ' && staging !== '?') {
        const changeType = this.statusToChangeType(staging);
        if (changeType) {
          changes.set(filePath, { filePath, changeType });
        }
      }

      // Unstaged changes
      if (options.includeUnstaged && working !== ' ' && working !== '?') {
        const changeType = this.statusToChangeType(working);
        if (changeType) {
          // Don't override a delete with a modification
          const existing = changes.get(filePath);
          if (!existing || existing.changeType !== 'deleted') {
            changes.set(filePath, { filePath, changeType });
          }
        }
      }
    }
  }

  /**
   * Convert a git status code to a ChangeType
   */
  private statusToChangeType(code: string): ChangeType | null {
    switch (code) {
      case 'A':
        return 'added';
      case 'D':
        return 'deleted';
      case 'M':
      case 'R':
      case 'C':
      case 'U':
        return 'modified';
      default:
        return null;
    }
  }
}

/**
 * Create a git diff detector instance
 *
 * @param projectRoot - Path to the project root
 * @returns GitDiffDetector instance, or null if not a git repo
 */
export function createGitDiffDetector(projectRoot: string): GitDiffDetector | null {
  const detector = new GitDiffDetector(projectRoot);
  return detector.isGitRepo() ? detector : null;
}
