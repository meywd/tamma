import {
  bucketOverTime,
  eventTone,
  eventsToCsv,
  eventsToJson,
  exportFilename,
  formatTagsPreview,
  groupByType,
  tagValue,
} from '../event-explorer-utils.js';
import type { DomainEventRow } from '../../../../hooks/monitoring/useEventQuery.js';

function ev(partial: Partial<DomainEventRow>): DomainEventRow {
  return {
    id: partial.id ?? 'id-1',
    type: partial.type ?? 'CODE.GENERATED.SUCCESS',
    tags: partial.tags ?? null,
    data: partial.data ?? null,
    createdAt: partial.createdAt ?? '2026-07-05T12:00:00.000Z',
    issueNumber: partial.issueNumber ?? null,
    sequenceNumber: partial.sequenceNumber ?? 1,
  };
}

describe('eventTone', () => {
  it('maps success events to green', () => {
    expect(eventTone('CODE.GENERATED.SUCCESS')).toBe('green');
    expect(eventTone('PLAN_APPROVED')).toBe('green');
    expect(eventTone('PR_MERGED')).toBe('green');
  });

  it('maps failure events to red (checked before success)', () => {
    expect(eventTone('CODE.GENERATED.FAILED')).toBe('red');
    expect(eventTone('IMPLEMENTATION_FAILED')).toBe('red');
    expect(eventTone('ERROR_OCCURRED')).toBe('red');
    expect(eventTone('PLAN_REJECTED')).toBe('red');
  });

  it('maps monitoring events to yellow and cleanup to gray', () => {
    expect(eventTone('STATE_TRANSITION')).toBe('yellow');
    expect(eventTone('CI_CHECK_STARTED')).toBe('yellow');
    expect(eventTone('BRANCH_DELETED')).toBe('gray');
  });

  it('falls back to blue for progress / informational events', () => {
    expect(eventTone('ISSUE_SELECTED')).toBe('blue');
    expect(eventTone('SOME.RANDOM.EVENT')).toBe('blue');
  });
});

describe('tagValue / formatTagsPreview', () => {
  it('extracts a string tag safely', () => {
    expect(tagValue({ correlationId: 'run-9' }, 'correlationId')).toBe('run-9');
    expect(tagValue(null, 'correlationId')).toBe('');
    expect(tagValue({ n: 7 }, 'n')).toBe('7');
  });

  it('previews a bounded number of tag pairs', () => {
    expect(formatTagsPreview({ a: '1', b: '2' })).toBe('a=1, b=2');
    expect(formatTagsPreview({ a: '1', b: '2', c: '3', d: '4' })).toBe('a=1, b=2, c=3, +1 more');
    expect(formatTagsPreview(null)).toBe('');
  });
});

describe('groupByType', () => {
  it('counts by type sorted by count desc', () => {
    const rows = [
      ev({ type: 'A' }),
      ev({ type: 'B' }),
      ev({ type: 'A' }),
      ev({ type: 'A' }),
    ];
    expect(groupByType(rows)).toEqual([
      { type: 'A', count: 3 },
      { type: 'B', count: 1 },
    ]);
  });
});

describe('bucketOverTime', () => {
  it('returns a single bucket when all timestamps are equal', () => {
    const rows = [ev({ createdAt: '2026-07-05T12:00:00.000Z' }), ev({ createdAt: '2026-07-05T12:00:00.000Z' })];
    const series = bucketOverTime(rows, 24);
    expect(series).toHaveLength(1);
    expect(series[0]?.value).toBe(2);
  });

  it('distributes events across buckets and preserves the total count', () => {
    const rows = [
      ev({ createdAt: '2026-07-05T00:00:00.000Z' }),
      ev({ createdAt: '2026-07-05T06:00:00.000Z' }),
      ev({ createdAt: '2026-07-05T12:00:00.000Z' }),
      ev({ createdAt: '2026-07-05T23:59:00.000Z' }),
    ];
    const series = bucketOverTime(rows, 4);
    expect(series).toHaveLength(4);
    expect(series.reduce((sum, p) => sum + p.value, 0)).toBe(4);
  });

  it('returns empty for no events', () => {
    expect(bucketOverTime([], 24)).toEqual([]);
  });
});

describe('export helpers', () => {
  it('produces parseable JSON', () => {
    const rows = [ev({ id: 'x', type: 'A' })];
    const parsed = JSON.parse(eventsToJson(rows)) as DomainEventRow[];
    expect(parsed[0]?.id).toBe('x');
  });

  it('produces a CSV with a header and one row per event, escaping commas', () => {
    const rows = [
      ev({
        id: 'e1',
        type: 'CODE.GENERATED.SUCCESS',
        issueNumber: 42,
        tags: { correlationId: 'run-1', userId: 'u9' },
        data: { note: 'a,b' },
      }),
    ];
    const csv = eventsToCsv(rows);
    const lines = csv.split('\r\n');
    expect(lines[0]).toBe('id,type,createdAt,sequenceNumber,issueNumber,correlationId,actor,data');
    expect(lines[1]).toContain('e1,CODE.GENERATED.SUCCESS');
    expect(lines[1]).toContain('run-1');
    expect(lines[1]).toContain('u9');
    // data field with an embedded comma is quoted.
    expect(csv).toContain('"{""note"":""a,b""}"');
  });

  it('builds a filter-aware filename', () => {
    expect(exportFilename('json', 'AGENT.TASK')).toMatch(/^tamma-events-AGENT\.TASK-\d{4}-\d{2}-\d{2}\.json$/);
    expect(exportFilename('csv')).toMatch(/^tamma-events-\d{4}-\d{2}-\d{2}\.csv$/);
  });
});
