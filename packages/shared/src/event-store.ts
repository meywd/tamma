import { randomUUID } from 'node:crypto';
import type { EngineEvent, EngineEventType, IEventStore } from './types/index.js';
import { monotonicNow } from './utils/index.js';

export class InMemoryEventStore implements IEventStore {
  private events: EngineEvent[] = [];

  record(event: Omit<EngineEvent, 'id' | 'timestamp'>): EngineEvent {
    const full: EngineEvent = {
      ...event,
      id: randomUUID(),
      timestamp: monotonicNow(),
    };
    this.events.push(full);
    return full;
  }

  getEvents(tenantId: string, issueNumber?: number): EngineEvent[] {
    return this.events.filter((e) => {
      if (e.tenantId !== tenantId) return false;
      if (issueNumber !== undefined && e.issueNumber !== issueNumber) return false;
      return true;
    });
  }

  getLastEvent(tenantId: string, type: EngineEventType): EngineEvent | undefined {
    for (let i = this.events.length - 1; i >= 0; i--) {
      const event = this.events[i];
      if (event !== undefined && event.tenantId === tenantId && event.type === type) {
        return event;
      }
    }
    return undefined;
  }

  clear(tenantId: string): void {
    this.events = this.events.filter((e) => e.tenantId !== tenantId);
  }
}
