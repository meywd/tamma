import { useCallback, useMemo, useRef, useState, useEffect } from 'react';
import './styles.css';

import type { WorkflowDataset, WorkflowMetadata, WorkflowNode } from './types';
import { buildMapLayout, type MapLayout } from './mapLayout';
import { StationDetailPopup } from './StationDetailPopup';
import { LinkDetailPopup } from './LinkDetailPopup';
import { kindOf, KIND_ORDER, KIND_DESCRIPTORS } from './kinds';

export interface WorkflowMapProps {
  /** The full dataset (all workflows), enabling sub-workflow cross-navigation. */
  metadata: WorkflowDataset | WorkflowMetadata[];
  /** Which workflow to render (by id). Falls back to the first workflow. */
  workflowId?: string;
  /** Currently selected step/node id (controlled, for deep-links). */
  stepId?: string;
  /** Called when the selected step changes (clicked, or popup closed). */
  onStepChange?: (stepId: string | null) => void;
  /** Called when the user opens a sub-workflow (its map). */
  onNavigate?: (workflowId: string, stepId?: string) => void;
  /**
   * Real href for a sub-workflow's page, so the popup's name pill is a true
   * `<a>` link (middle/ctrl-click opens a new tab). Returns undefined when the
   * target isn't in the dataset. If omitted, the pill falls back to in-app
   * navigation only.
   */
  subWorkflowHref?: (workflowId: string) => string | undefined;
}

function normalizeDataset(
  metadata: WorkflowDataset | WorkflowMetadata[],
): WorkflowMetadata[] {
  if (Array.isArray(metadata)) return metadata;
  return metadata.workflows ?? [];
}

/**
 * Subway / transit-map workflow view. STATIC and mobile-first: NO zoom, NO pan.
 * The user simply scrolls down. Stations are fixed-scale HTML cards positioned
 * by lane (x, a fraction of container width so it always fits the viewport) and
 * row (y, scrolls vertically). Connections are orthogonal SVG "rails" with
 * rounded corners, colour-coded per subway "line".
 *
 * Selection is controlled via `stepId`/`onStepChange` (deep-link friendly), and
 * sub-workflow stations navigate via `onNavigate`. Tapping a station opens a
 * floating popup (full-screen on phones, centred modal on desktop).
 */
export function WorkflowMap({
  metadata,
  workflowId,
  stepId,
  onStepChange,
  onNavigate,
  subWorkflowHref,
}: WorkflowMapProps) {
  const workflows = useMemo(() => normalizeDataset(metadata), [metadata]);

  const workflow = useMemo<WorkflowMetadata | undefined>(() => {
    if (workflowId) {
      const byId = workflows.find(
        (w) => w.id === workflowId || w.inventoryId === workflowId,
      );
      if (byId) return byId;
    }
    return workflows[0];
  }, [workflows, workflowId]);

  const layout = useMemo<MapLayout>(
    () => (workflow ? buildMapLayout(workflow) : { stations: [], rails: [], laneCount: 0, rowCount: 0 }),
    [workflow],
  );

  // Measure container width so lane x-positions can be fractions of it.
  const wrapRef = useRef<HTMLDivElement>(null);
  const [width, setWidth] = useState(0);
  useEffect(() => {
    const el = wrapRef.current;
    if (!el) return;
    const ro = new ResizeObserver((entries) => {
      const w = entries[0]?.contentRect.width ?? 0;
      setWidth(w);
    });
    ro.observe(el);
    setWidth(el.clientWidth);
    return () => ro.disconnect();
  }, []);

  const selectedNode = useMemo<WorkflowNode | undefined>(() => {
    if (!stepId || !workflow) return undefined;
    return workflow.nodes.find((n) => n.id === stepId);
  }, [stepId, workflow]);

  // A clicked rail/link opens its own transition panel (NOT the target step).
  const [selectedRailId, setSelectedRailId] = useState<string | null>(null);
  // A selected step and a selected link are mutually exclusive.
  useEffect(() => {
    if (stepId) setSelectedRailId(null);
  }, [stepId]);

  const handleStationClick = useCallback(
    (id: string) => {
      setSelectedRailId(null);
      onStepChange?.(id === stepId ? null : id);
    },
    [onStepChange, stepId],
  );
  const handleClose = useCallback(() => onStepChange?.(null), [onStepChange]);
  const handleRailClick = useCallback(
    (railId: string) => {
      onStepChange?.(null); // close any open step panel
      setSelectedRailId((cur) => (cur === railId ? null : railId));
    },
    [onStepChange],
  );
  const handleLinkClose = useCallback(() => setSelectedRailId(null), []);
  const handleGoToStep = useCallback(
    (id: string) => {
      setSelectedRailId(null);
      onStepChange?.(id);
    },
    [onStepChange],
  );
  const handleOpenSub = useCallback(
    (targetId: string) => onNavigate?.(targetId),
    [onNavigate],
  );

  if (!workflow) {
    return <div className="twv-empty">No workflow metadata available.</div>;
  }
  if (layout.stations.length === 0) {
    return (
      <div className="twv-map-root">
        <div className="twv-empty">
          No diagram metadata for this workflow — its graph could not be parsed.
        </div>
      </div>
    );
  }

  const geom = computeGeometry(layout, width);
  const kindsPresent = new Set(workflow.nodes.map((n) => n.kind));
  const selectedRail = selectedRailId
    ? geom.railPaths.find((rp) => rp.id === selectedRailId)
    : undefined;

  return (
    <div className="twv-map-root">
      <div className="twv-map-scroll" ref={wrapRef}>
        <div
          className="twv-map-canvas"
          style={{ height: geom.totalHeight }}
        >
          {/* Rails (SVG overlay, behind stations) */}
          <svg
            className="twv-map-rails"
            width={width || '100%'}
            height={geom.totalHeight}
            viewBox={`0 0 ${width || 1} ${geom.totalHeight}`}
            preserveAspectRatio="none"
            aria-hidden="true"
          >
            <defs>
              {/* Direction arrowhead — inherits each rail's stroke colour. */}
              <marker
                id="twv-rail-arrow"
                viewBox="0 0 10 10"
                refX="8.5"
                refY="5"
                markerWidth="7"
                markerHeight="7"
                markerUnits="userSpaceOnUse"
                orient="auto"
              >
                <path d="M0.5 0.5 L9 5 L0.5 9.5 z" fill="context-stroke" />
              </marker>
            </defs>
            {geom.railPaths.map((rp) => (
              <g key={rp.id} className="twv-rail">
                {/* visible rail — decorative; clicks are handled by the hit path */}
                <path
                  className="twv-rail-line"
                  d={rp.d}
                  fill="none"
                  stroke={rp.color}
                  strokeWidth={rp.isBackEdge ? 2 : 3}
                  strokeLinecap="round"
                  strokeLinejoin="round"
                  strokeDasharray={rp.isBackEdge ? '6 5' : undefined}
                  opacity={rp.isBackEdge ? 0.85 : 0.95}
                  markerEnd="url(#twv-rail-arrow)"
                />
                {/* wide invisible hit area — click a line to open its transition panel */}
                <path
                  className="twv-rail-hit"
                  d={rp.d}
                  fill="none"
                  data-selected={rp.id === selectedRailId ? 'true' : 'false'}
                  onClick={() => handleRailClick(rp.id)}
                >
                  <title>{rp.title}</title>
                </path>
              </g>
            ))}
          </svg>

          {/* Rail labels (HTML, on top of rails but below station hit-area) */}
          {geom.railLabels.map((rl) => (
            <span
              key={rl.id}
              className="twv-map-rail-label"
              style={{ left: rl.x, top: rl.y }}
            >
              {rl.label}
            </span>
          ))}

          {/* Stations */}
          {geom.stations.map((s) => {
            const k = kindOf(s.node.kind);
            const isSub =
              s.node.kind === 'dispatch-subworkflow' || Boolean(s.node.subWorkflowId);
            const isApi = s.node.kind === 'api-call' || Boolean(s.node.api);
            return (
              <button
                type="button"
                key={s.node.id}
                className="twv-station"
                data-kind={s.node.kind}
                data-selected={s.node.id === stepId ? 'true' : 'false'}
                data-sub={isSub ? 'true' : 'false'}
                data-compact={s.width < 108 ? 'true' : 'false'}
                style={{
                  left: s.x,
                  top: s.y,
                  width: s.width,
                  '--twv-accent': k.color,
                  '--twv-bg': k.bg,
                } as React.CSSProperties}
                onClick={() => handleStationClick(s.node.id)}
                aria-label={`${s.node.name} (${k.label})`}
                title={s.node.name}
              >
                <span className="twv-station-dot" aria-hidden="true">
                  <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth={1.7} aria-hidden="true">
                    <path strokeLinecap="round" strokeLinejoin="round" d={k.icon} />
                  </svg>
                </span>
                <span className="twv-station-label">{s.node.name}</span>
                {isSub && s.node.subWorkflowResolves && (
                  <span className="twv-station-badge" aria-hidden="true">↳</span>
                )}
                {isApi && !isSub && (
                  <span className="twv-station-badge twv-station-badge-api" aria-hidden="true">∞</span>
                )}
              </button>
            );
          })}
        </div>
      </div>

      {/* Legend (static, top of the immersive page) */}
      <div className="twv-map-legend" aria-label="Station kinds">
        {KIND_ORDER.filter((k) => kindsPresent.has(k)).map((k) => (
          <span key={k} className="twv-legend-item">
            <span className="twv-legend-dot" style={{ background: KIND_DESCRIPTORS[k].color }} />
            {KIND_DESCRIPTORS[k].label}
          </span>
        ))}
      </div>

      {selectedNode && (
        <StationDetailPopup
          node={selectedNode}
          onClose={handleClose}
          onOpenSubWorkflow={handleOpenSub}
          subWorkflowHref={subWorkflowHref}
        />
      )}

      {selectedRail && !selectedNode && (
        <LinkDetailPopup
          source={{ id: selectedRail.from, name: selectedRail.fromName }}
          target={{ id: selectedRail.to, name: selectedRail.toName }}
          trigger={selectedRail.trigger}
          color={selectedRail.color}
          onClose={handleLinkClose}
          onGoToStep={handleGoToStep}
        />
      )}
    </div>
  );
}

export default WorkflowMap;

// ---------------------------------------------------------------------------
// Geometry: turn the abstract (row, lane) layout into pixel positions.
// Lanes are fractions of the measured container width (→ no horizontal scroll,
// fits any viewport). Rows have a fixed vertical pitch (→ scroll down).
// ---------------------------------------------------------------------------

interface StationGeom {
  node: WorkflowNode;
  x: number;
  y: number;
  width: number;
  cx: number; // centre x of the station dot (for rails)
  cy: number; // centre y
}
interface RailPath {
  id: string;
  d: string;
  color: string;
  isBackEdge: boolean;
  /** Source node id of the transition. */
  from: string;
  /** Destination node id of the transition. */
  to: string;
  /** Source step display name. */
  fromName: string;
  /** Target step display name. */
  toName: string;
  /** Trigger / branch condition, when the edge is conditional. */
  trigger?: string;
  /** Hover tooltip, e.g. "Validate → Gather Context  ·  when: Valid". */
  title: string;
}
interface RailLabel {
  id: string;
  label: string;
  x: number;
  y: number;
}
interface Geometry {
  stations: StationGeom[];
  railPaths: RailPath[];
  railLabels: RailLabel[];
  totalHeight: number;
}

/** Subway-line palette (small, distinct). Rails pick a colour by lane index. */
const LINE_COLORS = [
  '#60a5fa', // blue
  '#f472b6', // pink
  '#34d399', // green
  '#fbbf24', // amber
  '#a78bfa', // violet
  '#22d3ee', // cyan
  '#fb923c', // orange
  '#4ade80', // lime
  '#e879f9', // fuchsia
  '#f87171', // red
];

const ROW_PITCH = 92; // vertical px between row centres
const TOP_PAD = 36;
const SIDE_PAD = 14;
const STATION_H = 56;
const CORNER = 12; // rounded-corner radius for rails

function computeGeometry(layout: MapLayout, width: number): Geometry {
  const w = width || 360;
  const lanes = Math.max(1, layout.laneCount);
  const inner = Math.max(120, w - SIDE_PAD * 2);
  const lanePitch = inner / lanes;
  // Station width tracks the lane pitch, with a floor so labels stay legible.
  // For very wide fan-outs the floor would push cards past the lane centres;
  // clamp the floor to the pitch so a station never exceeds its lane (→ no
  // edge clipping / no horizontal scroll regardless of lane count).
  const minW = Math.min(64, lanePitch);
  const stationW = Math.min(168, Math.max(minW, lanePitch - 8));

  const laneCenterX = (lane: number): number =>
    SIDE_PAD + lanePitch * (lane + 0.5);
  const rowCenterY = (row: number): number => TOP_PAD + row * ROW_PITCH;

  const stations: StationGeom[] = layout.stations.map((s) => {
    const cx = laneCenterX(s.lane);
    const cy = rowCenterY(s.row);
    return {
      node: s.node,
      x: cx - stationW / 2,
      y: cy - STATION_H / 2,
      width: stationW,
      cx,
      cy,
    };
  });
  const byId = new Map(stations.map((s) => [s.node.id, s]));

  const railPaths: RailPath[] = [];
  const railLabels: RailLabel[] = [];
  for (const rail of layout.rails) {
    const a = byId.get(rail.from);
    const b = byId.get(rail.to);
    if (!a || !b) continue;
    const color = LINE_COLORS[rail.line % LINE_COLORS.length]!;
    const { d, labelX, labelY } = orthogonalPath(a, b, rail.isBackEdge, w);
    // Link info: always name the source AND target step; surface the trigger
    // (branch condition / event) only when it adds information — a trigger that
    // just repeats the target step name is noise, so drop it.
    const trigger =
      rail.label && rail.label !== b.node.name ? rail.label : undefined;
    railPaths.push({
      id: rail.id,
      d,
      color,
      isBackEdge: rail.isBackEdge,
      from: rail.from,
      to: rail.to,
      fromName: a.node.name,
      toName: b.node.name,
      ...(trigger ? { trigger } : {}),
      title: trigger
        ? `${a.node.name} → ${b.node.name}  ·  when: ${trigger}`
        : `${a.node.name} → ${b.node.name}`,
    });
    if (trigger) {
      railLabels.push({ id: rail.id, label: trigger, x: labelX, y: labelY });
    }
  }

  const maxRow = Math.max(0, ...layout.stations.map((s) => s.row));
  const totalHeight = TOP_PAD + maxRow * ROW_PITCH + STATION_H / 2 + TOP_PAD;

  return { stations, railPaths, railLabels, totalHeight };
}

/**
 * Build an orthogonal rail (vertical segments + a horizontal jog with rounded
 * 90° corners) from station `a`'s bottom to station `b`'s top. Same-lane edges
 * are straight verticals. Back-edges (loop-backs) bow out to the side so they
 * don't overlap the forward trunk.
 */
function orthogonalPath(
  a: StationGeom,
  b: StationGeom,
  isBackEdge: boolean,
  maxX: number,
): { d: string; labelX: number; labelY: number } {
  const startY = a.cy + STATION_H / 2;
  const endY = b.cy - STATION_H / 2;

  if (isBackEdge) {
    // Loop-back (retry/return): exit the RIGHT EDGE of `a`, run vertically in a
    // side gutter, and re-enter the RIGHT EDGE of `b`. Dashed styling + the
    // arrowhead show the return direction. The gutter sits just outside the
    // wider of the two cards and is clamped to the canvas so it never overflows.
    const aRight = a.cx + a.width / 2;
    const bRight = b.cx + b.width / 2;
    const gutter = Math.min(maxX - 6, Math.max(aRight, bRight) + 22);
    const ay = a.cy;
    const by = b.cy;
    const r = Math.min(CORNER, Math.abs(ay - by) / 2);
    const up = ay > by ? -1 : 1; // vertical direction a → b
    const d = [
      `M ${aRight} ${ay}`,
      `H ${gutter - r}`,
      `Q ${gutter} ${ay} ${gutter} ${ay + up * r}`,
      `V ${by - up * r}`,
      `Q ${gutter} ${by} ${gutter - r} ${by}`,
      `H ${bRight}`,
    ].join(' ');
    return { d, labelX: Math.min(gutter + 4, maxX - 44), labelY: (ay + by) / 2 };
  }

  if (Math.abs(a.cx - b.cx) < 1) {
    // Straight vertical.
    const d = `M ${a.cx} ${startY} V ${endY}`;
    return { d, labelX: a.cx + 8, labelY: (startY + endY) / 2 };
  }

  // Orthogonal: down to a mid-y, horizontal jog with rounded corners, then down.
  const midY = (startY + endY) / 2;
  const r = Math.min(CORNER, Math.abs(b.cx - a.cx) / 2, Math.abs(endY - startY) / 2);
  const dir = b.cx > a.cx ? 1 : -1;
  const d = [
    `M ${a.cx} ${startY}`,
    `V ${midY - r}`,
    `Q ${a.cx} ${midY} ${a.cx + dir * r} ${midY}`,
    `H ${b.cx - dir * r}`,
    `Q ${b.cx} ${midY} ${b.cx} ${midY + r}`,
    `V ${endY}`,
  ].join(' ');
  return { d, labelX: (a.cx + b.cx) / 2, labelY: midY - 8 };
}
