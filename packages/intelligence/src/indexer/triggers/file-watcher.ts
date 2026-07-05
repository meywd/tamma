/**
 * File Watcher Trigger
 *
 * Watches for file changes in indexed directories and triggers
 * incremental re-indexing. Uses the built-in fs.watch API.
 */

import * as fs from 'node:fs';
import * as path from 'node:path';

/**
 * File watcher configuration
 */
export interface FileWatcherConfig {
  /** Directories to watch */
  watchPaths: string[];
  /** Debounce delay in ms (default: 500) */
  debounceMs?: number;
  /** File extensions to watch (default: all) */
  extensions?: string[];
  /** Patterns to ignore (simple string matching) */
  ignorePatterns?: string[];
}

/**
 * Callback type for change notifications
 */
export type FileChangeCallback = (changedFiles: string[]) => void;

/**
 * File watcher that triggers re-indexing on file changes
 */
export class FileWatcher {
  private config: Required<FileWatcherConfig>;
  private watchers: fs.FSWatcher[] = [];
  private pendingChanges: Set<string> = new Set();
  private debounceTimer: ReturnType<typeof setTimeout> | null = null;
  private callback: FileChangeCallback | null = null;
  private running = false;

  constructor(config: FileWatcherConfig) {
    this.config = {
      watchPaths: config.watchPaths,
      debounceMs: config.debounceMs ?? 500,
      extensions: config.extensions ?? [],
      ignorePatterns: config.ignorePatterns ?? [
        'node_modules',
        '.git',
        'dist',
        'build',
        'coverage',
        '.tsbuildinfo',
      ],
    };
  }

  /**
   * Start watching for file changes
   *
   * @param callback - Function to call when changes are detected
   */
  start(callback: FileChangeCallback): void {
    if (this.running) {
      return;
    }

    this.callback = callback;
    this.running = true;

    for (const watchPath of this.config.watchPaths) {
      try {
        const resolvedPath = path.resolve(watchPath);
        const watcher = fs.watch(
          resolvedPath,
          { recursive: true },
          (_eventType, filename) => {
            if (filename) {
              this.handleChange(resolvedPath, filename);
            }
          },
        );

        watcher.on('error', (error: unknown) => {
          console.warn('[FileWatcher] Watcher error for path:', resolvedPath, error instanceof Error ? error.message : String(error));
        });

        this.watchers.push(watcher);
      } catch {
        // Skip directories that don't exist or can't be watched
      }
    }
  }

  /**
   * Stop watching for file changes
   */
  stop(): void {
    this.running = false;

    // Clear debounce timer
    if (this.debounceTimer) {
      clearTimeout(this.debounceTimer);
      this.debounceTimer = null;
    }

    // Close all watchers
    for (const watcher of this.watchers) {
      try {
        watcher.close();
      } catch {
        // Ignore close errors
      }
    }
    this.watchers = [];

    // Clear pending changes
    this.pendingChanges.clear();
    this.callback = null;
  }

  /**
   * Check if the watcher is running
   */
  isRunning(): boolean {
    return this.running;
  }

  /**
   * Get the number of active watchers
   */
  getWatcherCount(): number {
    return this.watchers.length;
  }

  /**
   * Handle a file change event
   */
  private handleChange(watchRoot: string, filename: string): void {
    if (!this.running) return;

    // Build full path
    const fullPath = path.join(watchRoot, filename);

    // Check if the file should be ignored
    if (this.shouldIgnore(filename)) {
      return;
    }

    // Check extension filter
    if (this.config.extensions.length > 0) {
      const ext = path.extname(filename).toLowerCase();
      if (!this.config.extensions.includes(ext)) {
        return;
      }
    }

    // Add to pending changes
    this.pendingChanges.add(fullPath);

    // Debounce the callback
    if (this.debounceTimer) {
      clearTimeout(this.debounceTimer);
    }

    this.debounceTimer = setTimeout(() => {
      this.flushChanges();
    }, this.config.debounceMs);
  }

  /**
   * Flush pending changes and notify callback
   */
  private flushChanges(): void {
    if (this.pendingChanges.size === 0 || !this.callback) {
      return;
    }

    const changedFiles = Array.from(this.pendingChanges);
    this.pendingChanges.clear();
    this.debounceTimer = null;

    try {
      this.callback(changedFiles);
    } catch (error) {
      console.warn('[FileWatcher] Callback error:', error instanceof Error ? error.message : String(error));
    }
  }

  /**
   * Check if a file path should be ignored
   */
  private shouldIgnore(filename: string): boolean {
    const normalized = filename.replace(/\\/g, '/');

    for (const pattern of this.config.ignorePatterns) {
      if (normalized.includes(pattern)) {
        return true;
      }
    }

    return false;
  }
}

/**
 * Create a file watcher instance
 *
 * @param config - Watcher configuration
 * @returns FileWatcher instance
 */
export function createFileWatcher(config: FileWatcherConfig): FileWatcher {
  return new FileWatcher(config);
}
