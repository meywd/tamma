import dagre from '@dagrejs/dagre';
import { Position, type Node, type Edge } from '@xyflow/react';
import type { WorkflowMetadata } from './types';
import type { WorkflowNodeData } from './nodes';

const NODE_WIDTH = 210;
const NODE_HEIGHT = 76;

export interface LaidOutGraph {
  nodes: Node<WorkflowNodeData>[];
  edges: Edge[];
}

/**
 * Convert a {@link WorkflowMetadata} graph into React Flow nodes + edges,
 * laid out top-to-bottom with dagre (matches the wiki's dagre dependency).
 *
 * Pure function — no React, no DOM — so it can be unit-tested and reused.
 */
export function buildGraph(workflow: WorkflowMetadata): LaidOutGraph {
  const g = new dagre.graphlib.Graph();
  g.setGraph({
    rankdir: 'TB',
    nodesep: 60,
    ranksep: 90,
    marginx: 24,
    marginy: 24,
  });
  g.setDefaultEdgeLabel(() => ({}));

  for (const node of workflow.nodes) {
    g.setNode(node.id, { width: NODE_WIDTH, height: NODE_HEIGHT });
  }

  // Only lay out edges whose endpoints both exist as nodes.
  const nodeIds = new Set(workflow.nodes.map((n) => n.id));
  const validEdges = workflow.edges.filter(
    (e) => nodeIds.has(e.from) && nodeIds.has(e.to),
  );
  for (const edge of validEdges) {
    g.setEdge(edge.from, edge.to);
  }

  dagre.layout(g);

  const nodes: Node<WorkflowNodeData>[] = workflow.nodes.map((node) => {
    const laid = g.node(node.id);
    // dagre returns center coordinates; React Flow uses top-left.
    const x = (laid?.x ?? 0) - NODE_WIDTH / 2;
    const y = (laid?.y ?? 0) - NODE_HEIGHT / 2;
    return {
      id: node.id,
      type: 'workflowNode',
      position: { x, y },
      data: { node },
      sourcePosition: Position.Bottom,
      targetPosition: Position.Top,
      width: NODE_WIDTH,
      height: NODE_HEIGHT,
    };
  });

  const edges: Edge[] = validEdges.map((edge, i) => ({
    id: `${edge.from}__${edge.to}__${edge.label ?? ''}__${i}`,
    source: edge.from,
    target: edge.to,
    label: edge.label,
    type: 'smoothstep',
    labelStyle: { fill: '#a1a1aa', fontSize: 10 },
    labelBgStyle: { fill: '#18181b', fillOpacity: 0.9 },
    labelBgPadding: [4, 6],
    labelBgBorderRadius: 4,
    style: { stroke: '#52525b', strokeWidth: 1.5 },
  }));

  return { nodes, edges };
}
