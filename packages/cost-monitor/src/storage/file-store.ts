/**
 * File-based implementation of ICostStorage
 * Persists data to JSON files for MVP
 */

import { readFile, writeFile, mkdir } from 'fs/promises';
import { existsSync } from 'fs';
import { dirname, join } from 'path';
import type {
  ICostStorage,
  UsageRecord,
  UsageFilter,
  UsageAggregate,
  UsageLimit,
  CostAlert,
  AlertStatus,
  ReportSchedule,
  GroupByDimension,
} from '../types.js';
import { InMemoryStore } from './in-memory-store.js';

interface StorageData {
  usageRecords: UsageRecord[];
  limits: UsageLimit[];
  alerts: CostAlert[];
  reportSchedules: ReportSchedule[];
}

/**
 * File-based storage implementation for cost data
 * Uses an in-memory store for operations, with periodic flushing to disk
 */
export class FileStore implements ICostStorage {
  private readonly filePath: string;
  private readonly inMemory: InMemoryStore;
  private isDirty = false;
  private flushTimer?: ReturnType<typeof setInterval>;

  constructor(filePath: string, autoFlushIntervalMs = 30000) {
    this.filePath = filePath;
    this.inMemory = new InMemoryStore();

    // Set up auto-flush timer
    if (autoFlushIntervalMs > 0) {
      this.flushTimer = setInterval(() => {
        if (this.isDirty) {
          this.flush().catch((error: unknown) => {
            console.warn('[FileStore] Auto-flush failed:', error instanceof Error ? error.message : String(error));
          });
        }
      }, autoFlushIntervalMs);
    }
  }

  // --- Usage Records ---

  async saveUsageRecord(record: UsageRecord): Promise<void> {
    await this.inMemory.saveUsageRecord(record);
    this.isDirty = true;
  }

  async getUsageRecords(filter: UsageFilter): Promise<UsageRecord[]> {
    return this.inMemory.getUsageRecords(filter);
  }

  // --- Limits ---

  async saveLimitConfig(limit: UsageLimit): Promise<void> {
    await this.inMemory.saveLimitConfig(limit);
    this.isDirty = true;
  }

  async getLimitConfigs(): Promise<UsageLimit[]> {
    return this.inMemory.getLimitConfigs();
  }

  async deleteLimitConfig(id: string): Promise<void> {
    await this.inMemory.deleteLimitConfig(id);
    this.isDirty = true;
  }

  // --- Alerts ---

  async saveAlert(alert: CostAlert): Promise<void> {
    await this.inMemory.saveAlert(alert);
    this.isDirty = true;
  }

  async updateAlert(id: string, updates: Partial<CostAlert>): Promise<void> {
    await this.inMemory.updateAlert(id, updates);
    this.isDirty = true;
  }

  async getAlerts(filter?: { status?: AlertStatus }): Promise<CostAlert[]> {
    return this.inMemory.getAlerts(filter);
  }

  // --- Report Schedules ---

  async saveReportSchedule(schedule: ReportSchedule): Promise<void> {
    await this.inMemory.saveReportSchedule(schedule);
    this.isDirty = true;
  }

  async getReportSchedules(): Promise<ReportSchedule[]> {
    return this.inMemory.getReportSchedules();
  }

  async deleteReportSchedule(id: string): Promise<void> {
    await this.inMemory.deleteReportSchedule(id);
    this.isDirty = true;
  }

  // --- Aggregation ---

  async aggregateUsage(
    filter: UsageFilter,
    groupBy: GroupByDimension[]
  ): Promise<UsageAggregate[]> {
    return this.inMemory.aggregateUsage(filter, groupBy);
  }

  // --- Persistence ---

  async flush(): Promise<void> {
    const data: StorageData = {
      usageRecords: await this.inMemory.getUsageRecords({}),
      limits: await this.inMemory.getLimitConfigs(),
      alerts: await this.inMemory.getAlerts(),
      reportSchedules: await this.inMemory.getReportSchedules(),
    };

    // Ensure directory exists
    const dir = dirname(this.filePath);
    if (!existsSync(dir)) {
      await mkdir(dir, { recursive: true });
    }

    // Write to temporary file first, then rename (atomic write)
    const tempPath = `${this.filePath}.tmp`;
    await writeFile(tempPath, JSON.stringify(data, null, 2), 'utf-8');

    // Rename is atomic on most filesystems
    const { rename } = await import('fs/promises');
    await rename(tempPath, this.filePath);

    this.isDirty = false;
  }

  async load(): Promise<void> {
    if (!existsSync(this.filePath)) {
      return; // No data to load
    }

    try {
      const content = await readFile(this.filePath, 'utf-8');
      const data: StorageData = JSON.parse(content);

      // Clear existing data
      this.inMemory.clear();

      // Load usage records (convert date strings back to Date objects)
      for (const record of data.usageRecords || []) {
        await this.inMemory.saveUsageRecord({
          ...record,
          timestamp: new Date(record.timestamp),
        });
      }

      // Load limits
      for (const limit of data.limits || []) {
        await this.inMemory.saveLimitConfig({
          ...limit,
          createdAt: new Date(limit.createdAt),
          updatedAt: new Date(limit.updatedAt),
        });
      }

      // Load alerts
      for (const alert of data.alerts || []) {
        const loadedAlert: CostAlert = {
          ...alert,
          createdAt: new Date(alert.createdAt),
        };
        if (alert.acknowledgedAt) {
          loadedAlert.acknowledgedAt = new Date(alert.acknowledgedAt);
        }
        if (alert.resolvedAt) {
          loadedAlert.resolvedAt = new Date(alert.resolvedAt);
        }
        await this.inMemory.saveAlert(loadedAlert);
      }

      // Load report schedules
      for (const schedule of data.reportSchedules || []) {
        await this.inMemory.saveReportSchedule(schedule);
      }

      this.isDirty = false;
    } catch (error) {
      console.error(`Failed to load cost data from ${this.filePath}:`, error);
      throw error;
    }
  }

  // --- Lifecycle ---

  async dispose(): Promise<void> {
    if (this.flushTimer) {
      clearInterval(this.flushTimer);
    }

    // Final flush
    if (this.isDirty) {
      await this.flush();
    }
  }

  // --- Helper Methods ---

  getFilePath(): string {
    return this.filePath;
  }
}

/**
 * Create a file store with a default path
 */
export function createFileStore(
  baseDir: string,
  projectId = 'default'
): FileStore {
  const filePath = join(baseDir, '.tamma', 'cost-data', `${projectId}.json`);
  return new FileStore(filePath);
}
