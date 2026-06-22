import dagre from '@dagrejs/dagre';
import type { WorkflowMetadata, WorkflowNode } from './types';

/**
 * Layout engine for the subway/transit map view (`<WorkflowMap />`).
 *
 * Goal: a STATIC, vertical, mobile-first "metro map". Unlike the React Flow
 * viewer (which pans/zooms freely), this produces a fixed grid:
 *  - rows  (y)  = top-to-bottom rank (dagre layering; cycle-safe)
 *  - lanes (x)  = discrete columns ("subway lines"), mapped later to fractions
 *                 of the container width so the diagram always fits the viewport
 *                 width with no horizontal scroll and no zoom.
 *
 * Pure function — no React, no DOM — so it is deterministic and testable.
 * We use dagre only for RANKING + relative ordering (it breaks cycles
 * internally), then quantize dagre's x-centres into a small number of lanes.
 */

/** A station (node) placed on the map grid. */
export interface MapStation {
  node: WorkflowNode;
  /** Row index (0 = top). Monotonic with flow direction. */
  row: number;
  /** Lane index (0 = leftmost / central-ish trunk). */
  lane: number;
}

/** An orthogonal rail (edge) between two stations. */
export interface MapRail {
  id: string;
  from: string;
  to: string;
  /** Branch label (outcome), if any. */
  label?: string | undefined;
  fromRow: number;
  fromLane: number;
  toRow: number;
  toLane: number;
  /** Stable "line" index used to colour the rail from the subway palette. */
  line: number;
  /** True when the rail flows upward (a loop-back / retry edge). */
  isBackEdge: boolean;
}

export interface MapLayout {
  stations: MapStation[];
  rails: MapRail[];
  /** Number of lanes (columns) actually used. */
  laneCount: number;
  /** Number of rows. */
  rowCount: number;
}

const DAGRE_NODE_W = 160;
const DAGRE_NODE_H = 48;

interface RawNode {
  id: string;
  node: WorkflowNode;
  x: number;
  y: number;
  row: number;
  rawLane: number;
  lane: number;
}

/**
 * Build a metro-map layout from workflow metadata.
 *
 * Strategy:
 *  1. Run dagre (TB) for rank (→ row) and x-centre (→ left-to-right ordering).
 *     Dagre breaks cycles internally, so this is safe on the cyclic
 *     retry/merge graphs Tamma produces.
 *  2. Quantize dagre y-centres into dense, gap-free ROW indices.
 *  3. Assign LANES with continuity: a node inherits its primary predecessor's
 *     lane (so a "line" stays in its column down the map); genuine branches
 *     peel off into the nearest free lane. This keeps lane count close to the
 *     widest fan-out rather than dagre's pixel spread, and makes branches read
 *     as coloured side-lines that merge back — the subway feel.
 *  4. Centre the lanes so the busiest column sits toward the middle (trunk).
 *  5. Tag each rail with a "line" colour index (its source lane) and flag
 *     back-edges (target row ≤ source row) so loop-backs draw distinctly.
 */
export function buildMapLayout(workflow: WorkflowMetadata): MapLayout {
  const nodes = workflow.nodes;
  if (nodes.length === 0) {
    return { stations: [], rails: [], laneCount: 0, rowCount: 0 };
  }

  const nodeIds = new Set(nodes.map((n) => n.id));
  const validEdges = workflow.edges.filter(
    (e) => nodeIds.has(e.from) && nodeIds.has(e.to),
  );

  const g = new dagre.graphlib.Graph({ multigraph: true });
  g.setGraph({ rankdir: 'TB', nodesep: 40, ranksep: 60, marginx: 8, marginy: 8 });
  g.setDefaultEdgeLabel(() => ({}));

  for (const n of nodes) {
    g.setNode(n.id, { width: DAGRE_NODE_W, height: DAGRE_NODE_H });
  }
  validEdges.forEach((e, i) => {
    // multigraph: a name keeps parallel a→b edges distinct
    g.setEdge(e.from, e.to, {}, `e${i}`);
  });

  dagre.layout(g);

  const raw: RawNode[] = nodes.map((n) => {
    const d = g.node(n.id) as { x?: number; y?: number } | undefined;
    return { id: n.id, node: n, x: d?.x ?? 0, y: d?.y ?? 0, row: 0, rawLane: 0, lane: 0 };
  });
  const byId = new Map(raw.map((r) => [r.id, r]));

  // ---- rows: quantize dagre y-centres into dense, gap-free row indices.
  const ys = [...new Set(raw.map((r) => Math.round(r.y)))].sort((a, b) => a - b);
  const rowIdx = new Map(ys.map((y, i) => [y, i] as const));
  for (const r of raw) r.row = rowIdx.get(Math.round(r.y)) ?? 0;

  const byRow = new Map<number, RawNode[]>();
  for (const r of raw) {
    const arr = byRow.get(r.row) ?? [];
    arr.push(r);
    byRow.set(r.row, arr);
  }
  // Stable left→right order inside each row (dagre x).
  for (const arr of byRow.values()) arr.sort((a, b) => a.x - b.x);

  // ---- lane assignment with continuity.
  // Predecessors per node, ordered for stable "primary predecessor" choice.
  const preds = new Map<string, string[]>();
  for (const e of validEdges) {
    const a = byId.get(e.from);
    const b = byId.get(e.to);
    if (!a || !b || b.row <= a.row) continue; // forward edges only for inheritance
    const list = preds.get(e.to) ?? [];
    list.push(e.from);
    preds.set(e.to, list);
  }

  const rowMax = Math.max(0, ...ys.map((_, i) => i));
  for (let row = 0; row <= rowMax; row++) {
    const arr = byRow.get(row) ?? [];
    const taken = new Set<number>();
    // First pass: nodes that can inherit a predecessor's lane keep it if free.
    const wishes = new Map<RawNode, number>();
    for (const r of arr) {
      const ps = (preds.get(r.id) ?? [])
        .map((p) => byId.get(p))
        .filter((p): p is RawNode => Boolean(p));
      if (ps.length > 0) {
        // Primary predecessor = the one with the most forward children gets the
        // trunk; here we simply take the predecessor's lane (first in order).
        wishes.set(r, ps[0]!.lane);
      }
    }
    // Resolve wishes left→right; grant if free, else find nearest free lane.
    for (const r of arr) {
      const wish = wishes.get(r);
      let lane = wish ?? nearestFreeLane(taken, idealLane(r, arr));
      if (taken.has(lane)) lane = nearestFreeLane(taken, lane);
      taken.add(lane);
      r.rawLane = lane;
    }
  }

  // ---- compact lanes, then CENTRE the trunk so branches fan to BOTH sides.
  // Trunk = the lane carrying the most stations (the spine). The trunk takes the
  // centre column; the remaining lanes alternate to its right and left
  // (+1, -1, +2, -2, …). This stops every branch piling up on one side (a
  // left-anchored, right-only fan) and instead fans the map symmetrically about
  // a centred trunk — less width, less clutter.
  const laneCounts = new Map<number, number>();
  for (const r of raw) laneCounts.set(r.rawLane, (laneCounts.get(r.rawLane) ?? 0) + 1);
  const usedLanes = [...laneCounts.keys()];
  const trunk = usedLanes
    .slice()
    .sort((a, b) => (laneCounts.get(b)! - laneCounts.get(a)!) || a - b)[0]!;
  const others = usedLanes.filter((l) => l !== trunk).sort((a, b) => a - b);
  const signed = new Map<number, number>([[trunk, 0]]);
  others.forEach((l, i) => {
    const step = Math.floor(i / 2) + 1;
    signed.set(l, i % 2 === 0 ? step : -step); // +1, -1, +2, -2, …
  });
  const minSigned = Math.min(0, ...signed.values());
  for (const r of raw) r.lane = (signed.get(r.rawLane) ?? 0) - minSigned;

  const laneCount = usedLanes.length;
  const stations: MapStation[] = raw.map((r) => ({ node: r.node, row: r.row, lane: r.lane }));
  const stationById = new Map(stations.map((s) => [s.node.id, s]));
  const rowCount = ys.length;

  // ---- rails. Colour ("line") by source lane; flag upward/loop-back edges.
  const rails: MapRail[] = [];
  validEdges.forEach((e, i) => {
    const a = stationById.get(e.from);
    const b = stationById.get(e.to);
    if (!a || !b) return;
    const isBackEdge = b.row <= a.row;
    rails.push({
      id: `${e.from}__${e.to}__${e.label ?? ''}__${i}`,
      from: e.from,
      to: e.to,
      label: e.label,
      fromRow: a.row,
      fromLane: a.lane,
      toRow: b.row,
      toLane: b.lane,
      // Line colour keyed by the lane the rail departs from, which makes
      // branches that share a lane share a colour (subway-line feel).
      line: a.lane,
      isBackEdge,
    });
  });

  return { stations, rails, laneCount, rowCount };
}

/** The lane a node would "like" based on its order within its row. */
function idealLane(r: RawNode, rowArr: RawNode[]): number {
  return rowArr.indexOf(r);
}

/** Nearest free lane to `target` (searches outward), defaulting up if all taken. */
function nearestFreeLane(taken: Set<number>, target: number): number {
  if (!taken.has(target)) return target;
  for (let d = 1; d < 64; d++) {
    if (target - d >= 0 && !taken.has(target - d)) return target - d;
    if (!taken.has(target + d)) return target + d;
  }
  // Fallback: next index past the max.
  let i = 0;
  while (taken.has(i)) i++;
  return i;
}
