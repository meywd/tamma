import { describe, it, expect, vi, beforeEach, afterEach } from 'vitest';
import * as fs from 'node:fs';
import { EngineState } from '@tamma/shared';
import {
  writeLockfile,
  readLockfile,
  removeLockfile,
  isProcessRunning,
  getLockfilePath,
} from './state.js';
import type { IssueData } from '@tamma/shared';
import type { EngineStats } from '@tamma/orchestrator';

vi.mock('node:fs');

describe('state management', () => {
  beforeEach(() => {
    // Vitest 4: vi.restoreAllMocks() only restores manual spies, not automocks
    // (vi.mock'd modules). Use clearAllMocks() to also reset call history of
    // automocked functions so each test starts with mock.calls === [].
    vi.clearAllMocks();
    vi.restoreAllMocks();
  });

  afterEach(() => {
    // Vitest 4: vi.restoreAllMocks() only restores manual spies, not automocks
    // (vi.mock'd modules). Use clearAllMocks() to also reset call history of
    // automocked functions so each test starts with mock.calls === [].
    vi.clearAllMocks();
    vi.restoreAllMocks();
  });

  describe('getLockfilePath', () => {
    it('should return path in ~/.tamma/', () => {
      const p = getLockfilePath();
      expect(p).toContain('.tamma');
      expect(p).toContain('engine.lock');
    });
  });

  describe('writeLockfile', () => {
    it('should write lockfile JSON', () => {
      vi.mocked(fs.mkdirSync).mockReturnValue(undefined);
      vi.mocked(fs.writeFileSync).mockReturnValue(undefined);

      const stats: EngineStats = {
        issuesProcessed: 3,
        totalCostUsd: 1.50,
        startedAt: 1000,
      };

      const issue: IssueData = {
        number: 42,
        title: 'Test issue',
        body: 'body',
        labels: ['tamma'],
        url: 'http://example.com',
        comments: [],
        relatedIssueNumbers: [],
        createdAt: '2024-01-01',
      };

      writeLockfile(EngineState.IMPLEMENTING, issue, stats);

      expect(fs.mkdirSync).toHaveBeenCalled();
      expect(fs.writeFileSync).toHaveBeenCalled();

      const written = JSON.parse(vi.mocked(fs.writeFileSync).mock.calls[0]![1] as string);
      expect(written.pid).toBe(process.pid);
      expect(written.state).toBe('IMPLEMENTING');
      expect(written.issue.number).toBe(42);
      expect(written.issue.title).toBe('Test issue');
      expect(written.stats.issuesProcessed).toBe(3);
      expect(written.stats.totalCostUsd).toBe(1.5);
    });

    it('should write null issue when none', () => {
      vi.mocked(fs.mkdirSync).mockReturnValue(undefined);
      vi.mocked(fs.writeFileSync).mockReturnValue(undefined);

      const stats: EngineStats = {
        issuesProcessed: 0,
        totalCostUsd: 0,
        startedAt: 1000,
      };

      writeLockfile(EngineState.IDLE, null, stats);

      const written = JSON.parse(vi.mocked(fs.writeFileSync).mock.calls[0]![1] as string);
      expect(written.issue).toBeNull();
    });
  });

  describe('readLockfile', () => {
    it('should return null when file does not exist', () => {
      vi.mocked(fs.existsSync).mockReturnValue(false);
      expect(readLockfile()).toBeNull();
    });

    it('should return parsed data when file exists', () => {
      vi.mocked(fs.existsSync).mockReturnValue(true);
      vi.mocked(fs.readFileSync).mockReturnValue(JSON.stringify({
        pid: 12345,
        state: 'IDLE',
        issue: null,
        startedAt: 1000,
        stats: { issuesProcessed: 0, totalCostUsd: 0, startedAt: 1000 },
        updatedAt: 2000,
      }));

      const data = readLockfile();
      expect(data).not.toBeNull();
      expect(data!.pid).toBe(12345);
      expect(data!.state).toBe('IDLE');
    });

    it('should return null on parse error', () => {
      vi.mocked(fs.existsSync).mockReturnValue(true);
      vi.mocked(fs.readFileSync).mockReturnValue('not json');
      expect(readLockfile()).toBeNull();
    });
  });

  describe('removeLockfile', () => {
    it('should remove the lockfile', () => {
      vi.mocked(fs.unlinkSync).mockReturnValue(undefined);
      removeLockfile();
      expect(fs.unlinkSync).toHaveBeenCalledWith(expect.stringContaining('engine.lock'));
    });

    it('should not throw when file does not exist', () => {
      vi.mocked(fs.unlinkSync).mockImplementation(() => { throw new Error('ENOENT'); });
      expect(() => removeLockfile()).not.toThrow();
    });
  });

  describe('isProcessRunning', () => {
    it('should return true for current process', () => {
      expect(isProcessRunning(process.pid)).toBe(true);
    });

    it('should return false for non-existent PID', () => {
      // PID 99999999 is unlikely to exist
      expect(isProcessRunning(99999999)).toBe(false);
    });
  });
});
