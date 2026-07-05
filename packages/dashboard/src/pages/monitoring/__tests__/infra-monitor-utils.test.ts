import { describe, expect, it } from 'vitest';
import { dependencyKind, formatBytes, formatUptime, usageTone } from '../infra-monitor-utils.js';

describe('formatBytes', () => {
  it('formats binary units', () => {
    expect(formatBytes(0)).toBe('0 B');
    expect(formatBytes(512)).toBe('512 B');
    expect(formatBytes(1536)).toBe('1.5 KB');
    expect(formatBytes(314572800)).toBe('300 MB');
    expect(formatBytes(1073741824)).toBe('1 GB');
  });

  it('guards non-finite / negative input', () => {
    expect(formatBytes(-1)).toBe('0 B');
    expect(formatBytes(Number.NaN)).toBe('0 B');
  });
});

describe('formatUptime', () => {
  it('renders compact d/h/m', () => {
    expect(formatUptime(0)).toBe('0m');
    expect(formatUptime(59)).toBe('0m');
    expect(formatUptime(60)).toBe('1m');
    expect(formatUptime(3_600)).toBe('1h');
    expect(formatUptime(93_784)).toBe('1d 2h 3m');
  });
});

describe('dependencyKind', () => {
  it('maps probe status to a badge kind', () => {
    expect(dependencyKind('healthy')).toBe('healthy');
    expect(dependencyKind('unhealthy')).toBe('down');
    expect(dependencyKind('unknown')).toBe('unknown');
    expect(dependencyKind('anything-else')).toBe('unknown');
  });
});

describe('usageTone', () => {
  it('escalates green → yellow → red', () => {
    expect(usageTone(10)).toBe('green');
    expect(usageTone(74.9)).toBe('green');
    expect(usageTone(75)).toBe('yellow');
    expect(usageTone(89.9)).toBe('yellow');
    expect(usageTone(90)).toBe('red');
    expect(usageTone(100)).toBe('red');
  });
});
