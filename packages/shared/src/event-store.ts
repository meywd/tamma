import { randomUUID } from 'node:crypto';
import type { EngineEvent, EngineEventType, IEventStore } from './types/index.js';
import { monotonicNow } from './utils/index.js';

export class InMemoryEventStore implements IEventStore {
  private events: EngineEvent[] = [];

  async record(event: Omit<EngineEvent, 'id' | 'timestamp'>): Promise<EngineEvent> {
    const full: EngineEvent = {
      ...event,
      id: randomUUID(),
      timestamp: monotonicNow(),
    };
    this.events.push(full);
    return full;
  }

  async getEvents(tenantId: string, issueNumber?: number): Promise<EngineEvent[]> {
    return this.events.filter((e) => {
      if (e.tenantId !== tenantId) return false;
      if (issueNumber !== undefined && e.issueNumber !== issueNumber) return false;
      return true;
    });
  }

  async getLastEvent(tenantId: string, type: EngineEventType): Promise<EngineEvent | undefined> {
    for (let i = this.events.length - 1; i >= 0; i--) {
      const event = this.events[i];
      if (event !== undefined && event.tenantId === tenantId && event.type === type) {
        return event;
      }
    }
    return undefined;
  }

  async clear(tenantId: string): Promise<void> {
    this.events = this.events.filter((e) => e.tenantId !== tenantId);
  }
}
