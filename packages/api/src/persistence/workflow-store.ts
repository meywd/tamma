/**
 * Workflow Store
 *
 * In-memory implementation of the workflow persistence layer used for ELSA
 * workflow synchronisation. This can be replaced with a SQLite or PostgreSQL
 * backend without changing the interface.
 */

import { randomUUID } from 'node:crypto';

// ---------------------------------------------------------------------------
// Models
// ---------------------------------------------------------------------------

export interface WorkflowDefinition {
  id: string;
  name: string;
  version: number;
  description?: string;
  activities: unknown[];
  syncedAt: number;
}

export interface WorkflowInstance {
  id: string;
  definitionId: string;
  tenantId: string;
  status: string;
  currentActivity?: string;
  variables: Record<string, unknown>;
  createdAt: number;
  updatedAt: number;
}

export interface ListInstancesOptions {
  page?: number;
  pageSize?: number;
  definitionId?: string;
  tenantId?: string;
}

export interface PaginatedResult<T> {
  data: T[];
  total: number;
}

// ---------------------------------------------------------------------------
// Interface
// ---------------------------------------------------------------------------

export interface IWorkflowStore {
  upsertDefinition(def: WorkflowDefinition): Promise<WorkflowDefinition>;
  listDefinitions(): Promise<WorkflowDefinition[]>;
  getDefinition(id: string): Promise<WorkflowDefinition | null>;

  createInstance(instance: WorkflowInstance): Promise<WorkflowInstance>;
  updateInstance(
    id: string,
    update: Partial<WorkflowInstance>,
  ): Promise<WorkflowInstance | null>;
  getInstance(id: string): Promise<WorkflowInstance | null>;
  listInstances(
    options?: ListInstancesOptions,
  ): Promise<PaginatedResult<WorkflowInstance>>;
}

// ---------------------------------------------------------------------------
// In-memory implementation
// ---------------------------------------------------------------------------

export class InMemoryWorkflowStore implements IWorkflowStore {
  private definitions = new Map<string, WorkflowDefinition>();
  private instances = new Map<string, WorkflowInstance>();

  async upsertDefinition(
    def: WorkflowDefinition,
  ): Promise<WorkflowDefinition> {
    const existing = this.definitions.get(def.id);
    const merged: WorkflowDefinition = {
      ...existing,
      ...def,
      syncedAt: Date.now(),
    };
    this.definitions.set(merged.id, merged);
    return merged;
  }

  async listDefinitions(): Promise<WorkflowDefinition[]> {
    return [...this.definitions.values()];
  }

  async getDefinition(id: string): Promise<WorkflowDefinition | null> {
    return this.definitions.get(id) ?? null;
  }

  async createInstance(
    instance: WorkflowInstance,
  ): Promise<WorkflowInstance> {
    const record: WorkflowInstance = {
      ...instance,
      id: instance.id || randomUUID(),
      createdAt: instance.createdAt || Date.now(),
      updatedAt: instance.updatedAt || Date.now(),
    };
    this.instances.set(record.id, record);
    return record;
  }

  async updateInstance(
    id: string,
    update: Partial<WorkflowInstance>,
  ): Promise<WorkflowInstance | null> {
    const existing = this.instances.get(id);
    if (existing === undefined) {
      return null;
    }
    const updated: WorkflowInstance = {
      ...existing,
      ...update,
      id: existing.id, // id is immutable
      updatedAt: Date.now(),
    };
    this.instances.set(id, updated);
    return updated;
  }

  async getInstance(id: string): Promise<WorkflowInstance | null> {
    return this.instances.get(id) ?? null;
  }

  async listInstances(
    options?: ListInstancesOptions,
  ): Promise<PaginatedResult<WorkflowInstance>> {
    let items = [...this.instances.values()];

    // Filter by tenantId when provided
    if (options?.tenantId !== undefined) {
      items = items.filter((i) => i.tenantId === options.tenantId);
    }

    // Filter by definitionId when provided
    if (options?.definitionId !== undefined) {
      items = items.filter((i) => i.definitionId === options.definitionId);
    }

    const total = items.length;
    const page = options?.page ?? 1;
    const pageSize = options?.pageSize ?? 50;
    const start = (page - 1) * pageSize;
    const data = items.slice(start, start + pageSize);

    return { data, total };
  }
}
