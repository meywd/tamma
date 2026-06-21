import type { WorkflowNodeKind } from './types';

/**
 * Visual descriptor for each node kind: accent color (hex, for minimap/edges),
 * a Tailwind-ish palette of class fragments, a short label, and an inline SVG
 * path (Heroicons-style, 24x24 stroke) used as the node icon.
 *
 * The viewer ships its own scoped CSS (see `styles.css`) keyed by these colors
 * so it does not depend on the host app's Tailwind config.
 */
export interface KindDescriptor {
  kind: WorkflowNodeKind;
  label: string;
  /** Accent color (used for borders, icons, minimap). */
  color: string;
  /** Tinted background color (rgba). */
  bg: string;
  /** Inline SVG path data for the node icon. */
  icon: string;
}

export const KIND_DESCRIPTORS: Record<WorkflowNodeKind, KindDescriptor> = {
  activity: {
    kind: 'activity',
    label: 'Activity',
    color: '#a1a1aa',
    bg: 'rgba(82,82,91,0.18)',
    icon: 'M9 5H7a2 2 0 00-2 2v12a2 2 0 002 2h10a2 2 0 002-2V7a2 2 0 00-2-2h-2M9 5a2 2 0 002 2h2a2 2 0 002-2M9 5a2 2 0 012-2h2a2 2 0 012 2',
  },
  'dispatch-subworkflow': {
    kind: 'dispatch-subworkflow',
    label: 'Sub-workflow',
    color: '#60a5fa',
    bg: 'rgba(59,130,246,0.12)',
    icon: 'M13 10V3L4 14h7v7l9-11h-7z',
  },
  'api-call': {
    kind: 'api-call',
    label: 'API call',
    color: '#34d399',
    bg: 'rgba(16,185,129,0.12)',
    icon: 'M5 12h14M12 5l7 7-7 7',
  },
  'wait/bookmark': {
    kind: 'wait/bookmark',
    label: 'Wait / Bookmark',
    color: '#fbbf24',
    bg: 'rgba(245,158,11,0.12)',
    icon: 'M12 8v4l3 3m6-3a9 9 0 11-18 0 9 9 0 0118 0z',
  },
  gate: {
    kind: 'gate',
    label: 'Gate',
    color: '#f472b6',
    bg: 'rgba(236,72,153,0.12)',
    icon: 'M9 12l2 2 4-4m6 2a9 9 0 11-18 0 9 9 0 0118 0z',
  },
  decision: {
    kind: 'decision',
    label: 'Decision',
    color: '#f59e0b',
    bg: 'rgba(245,158,11,0.12)',
    icon: 'M8.228 9c.549-1.165 2.03-2 3.772-2 2.21 0 4 1.343 4 3 0 1.4-1.278 2.575-3.006 2.907-.542.104-.994.54-.994 1.093m0 3h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z',
  },
  terminal: {
    kind: 'terminal',
    label: 'Terminal',
    color: '#f87171',
    bg: 'rgba(239,68,68,0.12)',
    icon: 'M5.636 5.636a9 9 0 1012.728 0M12 3v9',
  },
};

export function kindOf(kind: string | undefined): KindDescriptor {
  return KIND_DESCRIPTORS[(kind as WorkflowNodeKind)] ?? KIND_DESCRIPTORS.activity;
}

/** Ordered list for legends. */
export const KIND_ORDER: WorkflowNodeKind[] = [
  'activity',
  'decision',
  'dispatch-subworkflow',
  'api-call',
  'wait/bookmark',
  'gate',
  'terminal',
];
